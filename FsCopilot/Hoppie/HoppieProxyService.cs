namespace FsCopilot.Hoppie;

using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using FsCopilot.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

public sealed class HoppieProxyService : IDisposable
{
    private static readonly TimeSpan PollRefreshWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AutomaticPollWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutomaticPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActiveCallsignRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TunnelTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ImmediatePollWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly CompositeDisposable _d = new();
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly SemaphoreSlim _messageGate = new(1, 1);
    private readonly SemaphoreSlim _listenerGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _activeCallsigns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CallsignData> _receivedMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TaskCompletionSource<bool>>> _pollWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OutgoingMessage> _sentMessages = [];
    private readonly BehaviorSubject<HoppieProxyStatus> _status;

    private readonly INetwork _net;
    private readonly HoppieSettingsStore _settingsStore;
    private readonly SessionRole _sessionRole;
    private readonly HoppieTransparentCaptureManager _transparentCapture;
    private readonly string _selfPeerId;

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private IHost? _transparentHttpHost;
    private IHost? _transparentHttpsHost;
    private bool _listenerRunning;
    private bool _transparentHttpRunning;
    private bool _transparentHttpsRunning;
    private int _peerCount;
    private int _disposed;
    private readonly Task _monitorTask;

    public HoppieProxyService(
        string selfPeerId,
        INetwork net,
        HoppieSettingsStore settingsStore,
        SessionRole sessionRole,
        HoppieTransparentCaptureManager transparentCapture)
    {
        _selfPeerId = selfPeerId;
        _net = net;
        _settingsStore = settingsStore;
        _sessionRole = sessionRole;
        _transparentCapture = transparentCapture;
        _status = new(new(false, settingsStore.Current.LocalUrl, "Built-in Hoppie tunnel disabled."));
        Trace("INIT", $"service-created selfPeer={_selfPeerId}");
        _monitorTask = Task.Run(() => MonitorLoopAsync(_shutdownCts.Token), CancellationToken.None);

        _net.RegisterPacket<HoppieTunnelRequest, HoppieTunnelRequest.Codec>();
        _net.RegisterPacket<HoppieTunnelResponse, HoppieTunnelResponse.Codec>();
        _net.RegisterPacket<HoppieFanoutMessages, HoppieFanoutMessages.Codec>();
        _net.RegisterPacket<HoppieFanoutAck, HoppieFanoutAck.Codec>();

        _d.Add(_net.Peers.Subscribe(peers =>
        {
            _peerCount = peers.Count;
            PublishStatus();
        }));

        _d.Add(_sessionRole.Changes.Subscribe(_ => PublishStatus()));
        _d.Add(_transparentCapture.Changes.Subscribe(_ => PublishStatus()));
        _d.Add(_settingsStore.Changes.Subscribe(settings => _ = ApplySettingsAsync(settings)));
        _d.Add(_net.Stream<HoppieTunnelRequest>().Subscribe(request => _ = HandleRemoteRequestAsync(request)));
        _d.Add(_net.Stream<HoppieTunnelResponse>().Subscribe(HandleRemoteResponse));
        _d.Add(_net.Stream<HoppieFanoutMessages>().Subscribe(packet => _ = HandleFanoutMessagesAsync(packet)));
        _d.Add(_net.Stream<HoppieFanoutAck>().Subscribe(packet => _ = HandleFanoutAckAsync(packet)));
    }

    public HoppieProxyStatus CurrentStatus => _status.Value;

    public IObservable<HoppieProxyStatus> StatusChanged => _status.DistinctUntilChanged();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _d.Dispose();
        try
        {
            _shutdownCts.Cancel();
            _monitorTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception e)
        {
            TraceException("MONITOR", "loop-shutdown-failed", e);
        }

        StopListener();
        StopTransparentListenersAsync(fastShutdown: true).GetAwaiter().GetResult();
        _http.Dispose();
        _status.Dispose();
        _shutdownCts.Dispose();
        _messageGate.Dispose();
        _listenerGate.Dispose();

        foreach (var waiter in _pendingResponses.Values)
            waiter.TrySetCanceled();

        foreach (var waiter in _pollWaiters.Values.SelectMany(waiters => waiters).ToArray())
            waiter.TrySetCanceled();
    }

    private async Task ApplySettingsAsync(HoppieSettings settings)
    {
        if (_shutdownCts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            return;

        await _listenerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdownCts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                return;

            Trace("SETTINGS", $"apply enabled={settings.Enabled} localPort={settings.LocalPort} transparent={settings.TransparentCaptureEnabled} recorder={settings.RecorderModeEnabled} upstreamConfigured={!string.IsNullOrWhiteSpace(settings.UpstreamLogon)} upstreamUrl={settings.UpstreamUrl} joiner={_sessionRole.IsJoiner} peers={_peerCount}");
            Log.Debug(
                "[Hoppie] Applying settings enabled={Enabled} localPort={LocalPort} transparent={Transparent} recorder={Recorder} upstreamConfigured={UpstreamConfigured} upstreamUrl={UpstreamUrl} joiner={Joiner} peers={Peers}",
                settings.Enabled,
                settings.LocalPort,
                settings.TransparentCaptureEnabled,
                settings.RecorderModeEnabled,
                !string.IsNullOrWhiteSpace(settings.UpstreamLogon),
                settings.UpstreamUrl,
                _sessionRole.IsJoiner,
                _peerCount);

            StopListener();
            await StopTransparentListenersAsync().ConfigureAwait(false);

            if (settings.Enabled)
            {
                StartListener(settings);
                await StartTransparentListenersAsync(settings).ConfigureAwait(false);
            }
        }
        finally
        {
            _listenerGate.Release();
            PublishStatus();
        }
    }

    private void StartListener(HoppieSettings settings)
    {
        try
        {
            var listener = new HttpListener
            {
                IgnoreWriteExceptions = true
            };
            listener.Prefixes.Add($"http://127.0.0.1:{settings.LocalPort.ToString(CultureInfo.InvariantCulture)}/");
            listener.Prefixes.Add($"http://localhost:{settings.LocalPort.ToString(CultureInfo.InvariantCulture)}/");
            listener.Start();

            var cts = new CancellationTokenSource();
            _listener = listener;
            _listenerCts = cts;
            _listenerRunning = true;

            Log.Debug("[Hoppie] Local listener started on http://127.0.0.1:{Port}/ and http://localhost:{Port}/", settings.LocalPort);
            Trace("LISTENER", $"local-started port={settings.LocalPort}");

            _ = Task.Run(() => ListenerLoopAsync(listener, cts.Token), CancellationToken.None);
        }
        catch (Exception e)
        {
            _listenerRunning = false;
            Log.Error(e, "[Hoppie] Failed to start local listener on port {Port}", settings.LocalPort);
            TraceException("LISTENER", $"local-start-failed port={settings.LocalPort}", e);
        }
    }

    private void StopListener()
    {
        _listenerRunning = false;
        Trace("LISTENER", "local-stopping");

        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            _listener?.Close();
        }
        catch
        {
            // ignored
        }

        _listener = null;

        if (_listenerCts != null)
        {
            _listenerCts.Dispose();
            _listenerCts = null;
        }
    }

    private async Task StartTransparentListenersAsync(HoppieSettings settings)
    {
        _transparentHttpRunning = false;
        _transparentHttpsRunning = false;

        if (!settings.TransparentCaptureEnabled)
        {
            Trace("CAPTURE", "transparent-disabled");
            return;
        }

        if (!HoppieTransparentCaptureManager.IsAdministrator)
        {
            Trace("CAPTURE", "transparent-waiting-for-admin-restart");
            return;
        }

        _transparentCapture.Refresh();
        var captureState = _transparentCapture.Current;
        Trace("CAPTURE", $"state installed={captureState.HostsOverrideInstalled} certificateReady={captureState.CertificateReady} certificateTrusted={captureState.CertificateTrusted}");

        try
        {
            _transparentHttpHost = BuildTransparentHost(HoppieTransparentCaptureManager.HttpPort, null);
            await _transparentHttpHost.StartAsync().ConfigureAwait(false);
            _transparentHttpRunning = true;
            Log.Debug("[Hoppie] Transparent HTTP listener started on port {Port}", HoppieTransparentCaptureManager.HttpPort);
            Trace("CAPTURE", $"http-started port={HoppieTransparentCaptureManager.HttpPort}");
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to start transparent HTTP listener on port {Port}", HoppieTransparentCaptureManager.HttpPort);
            _transparentHttpHost = null;
            TraceException("CAPTURE", $"http-start-failed port={HoppieTransparentCaptureManager.HttpPort}", e);
        }

        try
        {
            var certificate = _transparentCapture.EnsureTrustedCertificate();
            _transparentHttpsHost = BuildTransparentHost(HoppieTransparentCaptureManager.HttpsPort, certificate);
            await _transparentHttpsHost.StartAsync().ConfigureAwait(false);
            _transparentHttpsRunning = true;
            Log.Debug("[Hoppie] Transparent HTTPS listener started on port {Port}", HoppieTransparentCaptureManager.HttpsPort);
            Trace("CAPTURE", $"https-started port={HoppieTransparentCaptureManager.HttpsPort} thumbprint={certificate.Thumbprint}");
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to start transparent HTTPS listener on port {Port}", HoppieTransparentCaptureManager.HttpsPort);
            _transparentHttpsHost = null;
            TraceException("CAPTURE", $"https-start-failed port={HoppieTransparentCaptureManager.HttpsPort}", e);
        }
    }

    private async Task StopTransparentListenersAsync(bool fastShutdown = false)
    {
        _transparentHttpRunning = false;
        _transparentHttpsRunning = false;
        Trace("CAPTURE", "transparent-stopping");

        if (fastShutdown)
        {
            AbandonTransparentHost(ref _transparentHttpHost, "http");
            AbandonTransparentHost(ref _transparentHttpsHost, "https");
            Trace("CAPTURE", "transparent-stop-abandoned-for-process-exit");
            return;
        }

        if (_transparentHttpHost != null)
        {
            var host = _transparentHttpHost;
            _transparentHttpHost = null;
            try
            {
                using var cts = new CancellationTokenSource(HostShutdownTimeout);
                await host.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            await DisposeHostAsync(host, fastShutdown).ConfigureAwait(false);
        }

        if (_transparentHttpsHost != null)
        {
            var host = _transparentHttpsHost;
            _transparentHttpsHost = null;
            try
            {
                using var cts = new CancellationTokenSource(HostShutdownTimeout);
                await host.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            await DisposeHostAsync(host, fastShutdown).ConfigureAwait(false);
        }
    }

    private static void AbandonTransparentHost(ref IHost? host, string label)
    {
        var abandoned = host;
        host = null;
        if (abandoned == null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(HostShutdownTimeout);
                await abandoned.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignored during process exit
            }

            try
            {
                abandoned.Dispose();
            }
            catch
            {
                // ignored during process exit
            }

            Trace("CAPTURE", $"transparent-{label}-background-stop-finished");
        }, CancellationToken.None);
    }

    private static async Task DisposeHostAsync(IHost host, bool fastShutdown)
    {
        var disposeTask = Task.Run(() =>
        {
            try
            {
                host.Dispose();
            }
            catch
            {
                // The process is exiting; do not let Kestrel disposal keep FS Copilot alive.
            }
        }, CancellationToken.None);

        if (!fastShutdown)
            await Task.WhenAny(disposeTask, Task.Delay(HostShutdownTimeout)).ConfigureAwait(false);
    }

    private async Task ListenerLoopAsync(HttpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => HandleHttpContextAsync(context, ct), CancellationToken.None);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Local listener loop failed");
            TraceException("LISTENER", "local-loop-failed", e);
        }
        finally
        {
            _listenerRunning = false;
            Trace("LISTENER", "local-loop-stopped");
            PublishStatus();
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var requestCt = linkedCts.Token;
        try
        {
            Log.Debug("[Hoppie] Local HTTP {Request}", DescribeListenerRequest(context.Request));
            Trace("LOCAL", DescribeListenerRequest(context.Request));

            if (!IsConnectPath(context.Request.Url?.AbsolutePath))
            {
                Log.Debug("[Hoppie] Local HTTP ignored on unexpected path {Path}", context.Request.Url?.AbsolutePath ?? "<null>");
                Trace("LOCAL", $"ignored-path path={context.Request.Url?.AbsolutePath ?? "<null>"}");
                context.Response.StatusCode = 404;
                await WriteResponseAsync(context.Response, "not found", "text/plain", requestCt).ConfigureAwait(false);
                return;
            }

            var proxyRequest = await ParseRequestAsync(context.Request, _selfPeerId, requestCt).ConfigureAwait(false);
            var content = await HandleRequestAsync(proxyRequest, requestCt).ConfigureAwait(false);
            Log.Debug("[Hoppie] Local HTTP response {Response}", Summarize(content));
            Trace("LOCAL", $"response {Summarize(content)}");
            await WriteResponseAsync(context.Response, content, "text/html", requestCt).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to process local request");
            TraceException("LOCAL", "request-failed", e);
            try
            {
                await WriteResponseAsync(context.Response, "error {FS Copilot Hoppie tunnel failure}", "text/html", requestCt).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
                context.Response.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    private IHost BuildTransparentHost(int port, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
    {
        var builder = new HostBuilder();
        builder.ConfigureWebHost(webHost =>
        {
            webHost.UseKestrel(options =>
            {
                options.AddServerHeader = false;
                options.ListenLocalhost(port, listen =>
                {
                    if (certificate != null)
                        listen.UseHttps(certificate);
                });
            });
            webHost.Configure(app =>
            {
                app.Run(HandleTransparentHttpContextAsync);
            });
        });

        return builder.Build();
    }

    private async Task HandleTransparentHttpContextAsync(HttpContext context)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdownCts.Token);
        var ct = linkedCts.Token;
        try
        {
            Log.Debug("[Hoppie] Transparent HTTP {Request}", DescribeAspNetRequest(context.Request));
            Trace("TRANSPARENT", DescribeAspNetRequest(context.Request));

            if (!IsConnectPath(context.Request.Path.Value))
            {
                Log.Debug("[Hoppie] Transparent HTTP ignored on unexpected path {Path}", context.Request.Path.Value ?? "<null>");
                Trace("TRANSPARENT", $"ignored-path path={context.Request.Path.Value ?? "<null>"}");
                context.Response.StatusCode = 404;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("not found", ct).ConfigureAwait(false);
                return;
            }

            var proxyRequest = await ParseRequestAsync(context.Request, _selfPeerId, ct).ConfigureAwait(false);
            var content = await HandleRequestAsync(proxyRequest, ct).ConfigureAwait(false);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            Log.Debug("[Hoppie] Transparent HTTP response {Response}", Summarize(content));
            Trace("TRANSPARENT", $"response {Summarize(content)}");
            await context.Response.WriteAsync(content, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to process transparent Hoppie request");
            TraceException("TRANSPARENT", "request-failed", e);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("error {FS Copilot Hoppie tunnel failure}", ct).ConfigureAwait(false);
        }
    }

    private async Task<string> HandleRequestAsync(ProxyRequest request, CancellationToken ct)
    {
        var settings = _settingsStore.Current;
        if (!settings.RecorderModeEnabled && ShouldForwardToHost())
        {
            if (request.MsgType.Equals("poll", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(request.FromCallsign))
            {
                var localFanoutResponse = await TryServeJoinerLocalPollAsync(request.ClientInstanceId, request.FromCallsign, ct).ConfigureAwait(false);
                if (localFanoutResponse != null)
                    return localFanoutResponse;
            }

            Log.Debug("[Hoppie] Forwarding request to session host {Request}", DescribeProxyRequest(request));
            Trace("ROUTE", $"forward-to-host {DescribeProxyRequest(request)}");
            return await ForwardToHostAsync(request, ct).ConfigureAwait(false);
        }

        Log.Debug("[Hoppie] Processing request locally {Request}", DescribeProxyRequest(request));
        Trace("ROUTE", $"process-locally {DescribeProxyRequest(request)}");
        return await ProcessCoreAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> ForwardToHostAsync(ProxyRequest request, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingResponses.TryAdd(requestId, waiter))
            return "error {FS Copilot tunnel bookkeeping failure}";

        try
        {
            Log.Debug("[Hoppie] Tunnel send requestId={RequestId} {Request}", requestId, DescribeProxyRequest(request));
            Trace("TUNNEL", $"send requestId={requestId} {DescribeProxyRequest(request)}");
            _net.SendAll(new HoppieTunnelRequest(
                requestId,
                request.ClientInstanceId,
                request.Logon,
                request.FromCallsign,
                request.ToCallsign,
                request.MsgType,
                request.Packet));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TunnelTimeout);
            var content = await waiter.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.FromCallsign))
                await MarkMessagesSeenFromResponseAsync(request.ClientInstanceId, request.FromCallsign, content, timeoutCts.Token).ConfigureAwait(false);

            Log.Debug("[Hoppie] Tunnel response requestId={RequestId} {Response}", requestId, Summarize(content));
            Trace("TUNNEL", $"response requestId={requestId} {Summarize(content)}");
            return content;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[Hoppie] Tunnel timeout requestId={RequestId}", requestId);
            Trace("TUNNEL", $"timeout requestId={requestId}");
            return "error {FS Copilot Hoppie tunnel timeout}";
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Failed to forward request to session host");
            TraceException("TUNNEL", $"forward-failed requestId={requestId}", e);
            return "error {FS Copilot Hoppie tunnel failure}";
        }
        finally
        {
            _pendingResponses.TryRemove(requestId, out _);
        }
    }

    private async Task HandleRemoteRequestAsync(HoppieTunnelRequest request)
    {
        if (ShouldForwardToHost())
            return;

        Log.Debug("[Hoppie] Host received tunneled request requestId={RequestId} client={Client} type={Type} from={From} to={To}",
            request.RequestId,
            request.ClientInstanceId,
            request.MsgType,
            request.FromCallsign,
            request.ToCallsign);
        Trace("TUNNEL", $"host-received requestId={request.RequestId} client={request.ClientInstanceId} type={request.MsgType} from={Display(request.FromCallsign)} to={Display(request.ToCallsign)}");

        var content = await ProcessCoreAsync(new ProxyRequest(
            request.ClientInstanceId,
            request.Logon,
            request.FromCallsign,
            request.ToCallsign,
            request.MsgType,
            request.Packet), CancellationToken.None).ConfigureAwait(false);

        Log.Debug("[Hoppie] Host returning tunneled response requestId={RequestId} {Response}", request.RequestId, Summarize(content));
        Trace("TUNNEL", $"host-response requestId={request.RequestId} {Summarize(content)}");
        _net.SendAll(new HoppieTunnelResponse(request.RequestId, request.ClientInstanceId, content));
    }

    private void HandleRemoteResponse(HoppieTunnelResponse response)
    {
        if (!response.ClientInstanceId.Equals(_selfPeerId, StringComparison.Ordinal))
            return;

        Log.Debug("[Hoppie] Received tunneled response requestId={RequestId} {Response}", response.RequestId, Summarize(response.Content));
        Trace("TUNNEL", $"client-received requestId={response.RequestId} {Summarize(response.Content)}");
        if (_pendingResponses.TryRemove(response.RequestId, out var waiter))
            waiter.TrySetResult(response.Content);
    }

    private async Task HandleFanoutMessagesAsync(HoppieFanoutMessages packet)
    {
        if (!_sessionRole.IsJoiner || packet.Messages.Length == 0)
            return;

        await _messageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var added = AddIncomingMessagesLocked(packet.Callsign, packet.Messages, updateLastPollUtc: false);
            Trace("FANOUT", $"received callsign={packet.Callsign} messages={packet.Messages.Length} added={added.Length}");
            if (added.Length > 0)
                SignalPollWaitersLocked(packet.Callsign);
        }
        finally
        {
            _messageGate.Release();
        }

        _net.SendAll(new HoppieFanoutAck(packet.Callsign, _selfPeerId, packet.Messages));
        Trace("FANOUT", $"ack-sent callsign={packet.Callsign} messages={packet.Messages.Length} client={_selfPeerId}");
    }

    private async Task HandleFanoutAckAsync(HoppieFanoutAck packet)
    {
        if (ShouldForwardToHost() || packet.Messages.Length == 0)
            return;

        await _messageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_receivedMessages.TryGetValue(packet.Callsign, out var data))
            {
                Trace("FANOUT", $"ack-ignored callsign={packet.Callsign} client={packet.ClientInstanceId} reason=no-cache");
                return;
            }

            var delivered = new HashSet<string>(packet.Messages, StringComparer.Ordinal);
            var matched = 0;
            foreach (var message in data.Messages)
            {
                if (!delivered.Contains(message.Data))
                    continue;

                if (message.SeenClientInstances.Add(packet.ClientInstanceId))
                    matched++;
            }

            if (matched > 0)
                CleanReceivedMessages();

            Trace("FANOUT", $"ack-received callsign={packet.Callsign} client={packet.ClientInstanceId} matched={matched} messages={packet.Messages.Length}");
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private async Task<string> ProcessCoreAsync(ProxyRequest request, CancellationToken ct)
    {
        var settings = _settingsStore.Current;
        if (!settings.Enabled)
            return "error {FS Copilot Hoppie tunnel is disabled}";

        Log.Debug("[Hoppie] REQ client={Client} logon={Logon} from={From} to={To} type={Type}",
            request.ClientInstanceId, request.Logon, request.FromCallsign, request.ToCallsign, request.MsgType);
        Trace("CORE", $"request {DescribeProxyRequest(request)}");

        if (string.IsNullOrWhiteSpace(request.Logon)
            && string.IsNullOrWhiteSpace(request.FromCallsign)
            && string.IsNullOrWhiteSpace(request.ToCallsign)
            && string.IsNullOrWhiteSpace(request.MsgType)
            && string.IsNullOrWhiteSpace(request.Packet))
        {
            Trace("CORE", "reject no-parameters-given");
            return "error {no parameters given}";
        }

        if (string.IsNullOrWhiteSpace(request.Logon))
        {
            Trace("CORE", "reject no-logon-given");
            return "error {no logon given}";
        }

        if (string.IsNullOrWhiteSpace(request.MsgType))
        {
            Trace("CORE", "reject no-packet-type-given");
            return "error {no packet type given}";
        }

        if (settings.RecorderModeEnabled)
            return await HandleRecorderModeAsync(request, settings, ct).ConfigureAwait(false);

        var normalizedType = request.MsgType.Trim().ToLowerInvariant();
        TrackActiveCallsign(request.FromCallsign, $"request-{Display(request.MsgType)}");

        if (normalizedType == "poll")
        {
            if (string.IsNullOrWhiteSpace(request.FromCallsign))
                return "error {from callsign required for polling}";

            return await HandlePollAsync(request.ClientInstanceId, request.FromCallsign, settings, ct).ConfigureAwait(false);
        }

        await _messageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return normalizedType switch
            {
                "cpdlc" or "telex" => string.IsNullOrWhiteSpace(request.FromCallsign)
                                       || string.IsNullOrWhiteSpace(request.ToCallsign)
                                       || string.IsNullOrWhiteSpace(request.Packet)
                    ? "error {from/to callsigns and packet payload required for sending cpdlc/telex}"
                    : await HandleTelexCpdlcAsync(
                        request.ClientInstanceId,
                        request.FromCallsign,
                        request.ToCallsign,
                        request.Packet,
                        request.MsgType,
                        settings,
                        ct).ConfigureAwait(false),
                _ => AttachPendingMessagesForClient(
                    request.ClientInstanceId,
                    request.FromCallsign,
                    await SendUpstreamAsync(request.FromCallsign, request.ToCallsign, request.Packet, request.MsgType, settings, ct)
                        .ConfigureAwait(false),
                    $"request-{normalizedType}")
            };
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private async Task<string> HandleRecorderModeAsync(ProxyRequest request, HoppieSettings settings, CancellationToken ct)
    {
        var effectiveLogon = string.IsNullOrWhiteSpace(request.Logon)
            ? settings.UpstreamLogon
            : request.Logon;

        if (string.IsNullOrWhiteSpace(effectiveLogon))
        {
            Trace("RECORDER", $"reject missing-logon request={DescribeProxyRequest(request)}");
            return "error {no logon given}";
        }

        Trace("RECORDER", $"passthrough request={DescribeProxyRequest(request)} effectiveLogon={Summarize(effectiveLogon, 24)}");
        return await SendUpstreamCoreAsync(
            effectiveLogon,
            request.FromCallsign,
            request.ToCallsign,
            request.Packet,
            request.MsgType,
            settings.UpstreamUrl,
            ct).ConfigureAwait(false);
    }

    private async Task<string> HandlePollAsync(string clientInstanceId, string callsign, HoppieSettings settings, CancellationToken ct)
    {
        TaskCompletionSource<bool>? waiter = null;
        try
        {
            var shouldPollImmediately = false;
            await _messageGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (TryHasPendingMessagesLocked(clientInstanceId, callsign, out var pendingCount))
                {
                    Trace("POLL", $"deliver-immediate client={clientInstanceId} callsign={callsign} pending={pendingCount}");
                    return AttachPendingMessagesForClient(clientInstanceId, callsign, "ok", "poll");
                }

                shouldPollImmediately = ShouldPollImmediatelyLocked(callsign);
                waiter = RegisterPollWaiterLocked(callsign);
                Trace("POLL", $"wait client={clientInstanceId} callsign={callsign} timeoutSeconds={LongPollTimeout.TotalSeconds} immediatePoll={shouldPollImmediately}");
            }
            finally
            {
                _messageGate.Release();
            }

            if (shouldPollImmediately)
            {
                var immediateResponse = await TryCompletePollAsync(clientInstanceId, callsign, settings, forceUpstream: true, "poll-open", ct).ConfigureAwait(false);
                if (immediateResponse != null)
                    return immediateResponse;
            }

            var waitTimedOut = false;
            try
            {
                await waiter.Task.WaitAsync(LongPollTimeout, ct).ConfigureAwait(false);
                Trace("POLL", $"wait-signaled client={clientInstanceId} callsign={callsign}");
            }
            catch (TimeoutException)
            {
                waitTimedOut = true;
                Trace("POLL", $"wait-timeout client={clientInstanceId} callsign={callsign}");
            }
            finally
            {
                await _messageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    UnregisterPollWaiterLocked(callsign, waiter);
                }
                finally
                {
                    _messageGate.Release();
                }
            }

            return await TryCompletePollAsync(
                    clientInstanceId,
                    callsign,
                    settings,
                    forceUpstream: waitTimedOut,
                    waitTimedOut ? "poll-timeout" : "poll-ready",
                    ct)
                .ConfigureAwait(false)
                ?? "ok";
        }
        catch (HoppieUpstreamException e)
        {
            Trace("POLL", $"upstream-error callsign={callsign} {Summarize(e.Content)}");
            return e.Content;
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Poll failed");
            TraceException("POLL", $"poll-failed callsign={callsign}", e);
            return "error {FS Copilot upstream poll failure}";
        }
    }

    private async Task<string?> TryServeJoinerLocalPollAsync(string clientInstanceId, string callsign, CancellationToken ct)
    {
        await _messageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryHasPendingMessagesLocked(clientInstanceId, callsign, out var pendingCount))
            {
                Trace("FANOUT", $"joiner-local-miss client={clientInstanceId} callsign={callsign}");
                return null;
            }

            Trace("FANOUT", $"joiner-local-hit client={clientInstanceId} callsign={callsign} pending={pendingCount}");
            return AttachPendingMessagesForClient(clientInstanceId, callsign, "ok", "joiner-local");
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private async Task PollUpstreamAsync(string callsign, HoppieSettings settings, CancellationToken ct)
    {
        Log.Debug("[Hoppie] POLL upstream callsign={Callsign}", callsign);
        Trace("POLL", $"upstream-request callsign={callsign}");
        var content = await SendUpstreamAsync(
            callsign,
            "SERVER",
            null,
            "poll",
            settings,
            ct).ConfigureAwait(false);

        if (content.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            throw new HoppieUpstreamException(content);

        var messages = ExtractMessages(content);
        Log.Debug("[Hoppie] POLL upstream result callsign={Callsign} messages={Count} response={Response}",
            callsign,
            messages.Length,
            Summarize(content));
        Trace("POLL", $"upstream-result callsign={callsign} messages={messages.Length} response={Summarize(content)}");
        var addedMessages = AddIncomingMessagesLocked(callsign, messages, updateLastPollUtc: true);

        if (addedMessages.Length > 0)
        {
            SignalPollWaitersLocked(callsign);
            BroadcastFanoutMessages(callsign, addedMessages);
        }

        CleanReceivedMessages();
    }

    private async Task<string> HandleTelexCpdlcAsync(
        string clientInstanceId,
        string fromCallsign,
        string toCallsign,
        string packet,
        string packetType,
        HoppieSettings settings,
        CancellationToken ct)
    {
        CleanSentMessages();

        var now = DateTime.UtcNow;
        var duplicate = _sentMessages.LastOrDefault(previous =>
            previous.FromCallsign.Equals(fromCallsign, StringComparison.OrdinalIgnoreCase)
            && previous.ToCallsign.Equals(toCallsign, StringComparison.OrdinalIgnoreCase)
            && previous.PacketType.Equals(packetType, StringComparison.OrdinalIgnoreCase)
            && AreDuplicatePayloads(previous.Payload, packet)
            && now - previous.SentAtUtc <= DuplicateWindow);

        if (duplicate == null)
        {
            duplicate = _sentMessages.LastOrDefault(previous =>
                previous.FromCallsign.Equals(fromCallsign, StringComparison.OrdinalIgnoreCase)
                && previous.PacketType.Equals(packetType, StringComparison.OrdinalIgnoreCase)
                && AreDuplicatePayloads(previous.Payload, packet)
                && now - previous.SentAtUtc <= DuplicateWindow);
        }

        if (duplicate != null)
        {
            Log.Debug("[Hoppie] Duplicate {Type} send suppressed for {From}->{To}", packetType, fromCallsign, toCallsign);
            if (!duplicate.ToCallsign.Equals(toCallsign, StringComparison.OrdinalIgnoreCase))
                Trace("SEND", $"duplicate-address-mismatch type={packetType} from={fromCallsign} firstTo={duplicate.ToCallsign} currentTo={toCallsign} strategy=payload-based");
            Trace("SEND", $"duplicate-suppressed type={packetType} from={fromCallsign} to={toCallsign} originalClient={duplicate.SendingClientInstance} response={Summarize(duplicate.UpstreamResponse)}");
            return AttachPendingMessagesForClient(clientInstanceId, fromCallsign, duplicate.UpstreamResponse, $"duplicate-{packetType}");
        }

        Trace("SEND", $"upstream-send type={packetType} from={fromCallsign} to={toCallsign} payload={Summarize(packet, 160)}");
        var response = await SendUpstreamAsync(fromCallsign, toCallsign, packet, packetType, settings, ct).ConfigureAwait(false);
        Trace("SEND", $"upstream-response type={packetType} from={fromCallsign} to={toCallsign} response={Summarize(response)}");
        _sentMessages.Add(new(packet, fromCallsign, toCallsign, packetType, clientInstanceId, response, now));
        CleanSentMessages();
        return AttachPendingMessagesForClient(clientInstanceId, fromCallsign, response, $"send-{packetType}");
    }

    private async Task<string> SendUpstreamAsync(
        string? fromCallsign,
        string? toCallsign,
        string? packet,
        string? packetType,
        HoppieSettings settings,
        CancellationToken ct) =>
        await SendUpstreamCoreAsync(
            settings.UpstreamLogon,
            fromCallsign,
            toCallsign,
            packet,
            packetType,
            settings.UpstreamUrl,
            ct).ConfigureAwait(false);

    private async Task<string> SendUpstreamCoreAsync(
        string? logon,
        string? fromCallsign,
        string? toCallsign,
        string? packet,
        string? packetType,
        string? upstreamUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(logon))
        {
            Trace("UPSTREAM", "reject missing-upstream-logon");
            return "error {FS Copilot upstream logon is not configured}";
        }

        if (string.IsNullOrWhiteSpace(upstreamUrl))
        {
            Trace("UPSTREAM", "reject missing-upstream-url");
            return "error {FS Copilot upstream URL is not configured}";
        }

        try
        {
            var upstreamBaseUrl = GetEffectiveUpstreamUrl(upstreamUrl);
            var url = AppendQuery(upstreamBaseUrl, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["logon"] = logon,
                ["from"] = fromCallsign,
                ["to"] = toCallsign,
                ["type"] = packetType,
                ["packet"] = packet
            });

            Log.Debug("[Hoppie] Upstream GET type={Type} from={From} to={To} url={Url}",
                packetType,
                fromCallsign ?? string.Empty,
                toCallsign ?? string.Empty,
                SanitizeUrl(url));
            Trace("UPSTREAM", $"get type={Display(packetType)} from={Display(fromCallsign)} to={Display(toCallsign)} url={SanitizeUrl(url)}");

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Debug("[Hoppie] Upstream RESP status={StatusCode} type={Type} from={From} to={To} content={Content}",
                (int)response.StatusCode,
                packetType,
                fromCallsign ?? string.Empty,
                toCallsign ?? string.Empty,
                Summarize(content));
            Trace("UPSTREAM", $"resp status={(int)response.StatusCode} type={Display(packetType)} from={Display(fromCallsign)} to={Display(toCallsign)} content={Summarize(content)}");
            if (!response.IsSuccessStatusCode)
                return $"error {{HTTP {(int)response.StatusCode} {response.ReasonPhrase}}}";

            return string.IsNullOrWhiteSpace(content) ? "ok" : content.Trim();
        }
        catch (TaskCanceledException)
        {
            Trace("UPSTREAM", $"timeout type={Display(packetType)} from={Display(fromCallsign)} to={Display(toCallsign)}");
            return "error {upstream request timed out}";
        }
        catch (Exception e)
        {
            Log.Error(e, "[Hoppie] Upstream request failed type={Type} from={From} to={To}", packetType, fromCallsign ?? string.Empty, toCallsign ?? string.Empty);
            TraceException("UPSTREAM", $"request-failed type={Display(packetType)} from={Display(fromCallsign)} to={Display(toCallsign)}", e);
            return "error {upstream request failed}";
        }
    }

    private string GetEffectiveUpstreamUrl(string configuredUrl)
    {
        if (!_transparentCapture.Current.HostsOverrideInstalled)
            return configuredUrl;

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
            return configuredUrl;

        if (!HoppieTransparentCaptureManager.TargetHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return configuredUrl;

        var rewritten = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttp,
            Host = HoppieTransparentCaptureManager.UpstreamBypassHost,
            Port = -1
        }.Uri.ToString();

        Log.Debug("[Hoppie] Rewriting upstream URL from {OriginalUrl} to {RewrittenUrl} to avoid the local transparent capture loop",
            configuredUrl,
            rewritten);
        Trace("UPSTREAM", $"rewrite original={SanitizeUrl(configuredUrl)} rewritten={SanitizeUrl(rewritten)}");
        return rewritten;
    }

    private string AttachPendingMessagesForClient(string clientInstanceId, string? callsign, string response, string reason)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return response;

        if (response.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            return response;

        if (!_receivedMessages.TryGetValue(callsign, out var callsignData))
        {
            Trace("DELIVER", $"no-cached-data client={clientInstanceId} callsign={callsign} reason={reason}");
            return response;
        }

        var messages = callsignData.Messages
            .Where(message => !message.SeenClientInstances.Contains(clientInstanceId))
            .ToArray();

        if (messages.Length == 0)
        {
            Trace("DELIVER", $"no-pending client={clientInstanceId} callsign={callsign} reason={reason}");
            return response;
        }

        foreach (var message in messages)
            message.SeenClientInstances.Add(clientInstanceId);

        CleanReceivedMessages();

        var combined = AppendMessagesToResponse(response, messages.Select(message => message.Data));
        Log.Debug("[Hoppie] Deliver cached inbound messages to client={Client} callsign={Callsign} reason={Reason} count={Count}",
            clientInstanceId,
            callsign,
            reason,
            messages.Length);
        Trace("DELIVER", $"client={clientInstanceId} callsign={callsign} reason={reason} messages={messages.Length} result={Summarize(combined)}");
        return combined;
    }

    private string[] AddIncomingMessagesLocked(string callsign, IEnumerable<string> messages, bool updateLastPollUtc)
    {
        if (!_receivedMessages.TryGetValue(callsign, out var data))
        {
            data = new();
            _receivedMessages[callsign] = data;
        }

        if (updateLastPollUtc)
            data.LastPollUtc = DateTime.UtcNow;

        var existing = new HashSet<string>(data.Messages.Select(message => message.Data), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message))
                continue;

            if (!existing.Add(message))
                continue;

            data.Messages.Add(new(message));
            added.Add(message);
        }

        if (added.Count > 0)
            Trace("CACHE", $"store callsign={callsign} added={added.Count} total={data.Messages.Count} updatedLastPoll={updateLastPollUtc}");

        return added.ToArray();
    }

    private void BroadcastFanoutMessages(string callsign, string[] messages)
    {
        if (messages.Length == 0 || _peerCount <= 0 || ShouldForwardToHost())
            return;

        _net.SendAll(new HoppieFanoutMessages(callsign, messages));
        Trace("FANOUT", $"broadcast callsign={callsign} messages={messages.Length} peers={_peerCount}");
    }

    private async Task MarkMessagesSeenFromResponseAsync(string clientInstanceId, string callsign, string response, CancellationToken ct)
    {
        var messages = ExtractMessages(response);
        if (messages.Length == 0)
            return;

        await _messageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_receivedMessages.TryGetValue(callsign, out var data))
                return;

            var delivered = new HashSet<string>(messages, StringComparer.Ordinal);
            var matched = 0;
            foreach (var message in data.Messages)
            {
                if (!delivered.Contains(message.Data))
                    continue;

                if (message.SeenClientInstances.Add(clientInstanceId))
                    matched++;
            }

            if (matched > 0)
            {
                CleanReceivedMessages();
                Trace("FANOUT", $"mark-from-response client={clientInstanceId} callsign={callsign} matched={matched}");
            }
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        Trace("MONITOR", $"loop-started intervalSeconds={AutomaticPollInterval.TotalSeconds}");

        using var timer = new PeriodicTimer(AutomaticPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await PollKnownCallsignsAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    TraceException("MONITOR", "tick-failed", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        finally
        {
            Trace("MONITOR", "loop-stopped");
        }
    }

    private async Task PollKnownCallsignsAsync(CancellationToken ct)
    {
        var settings = _settingsStore.Current;
        if (!settings.Enabled || settings.RecorderModeEnabled || string.IsNullOrWhiteSpace(settings.UpstreamLogon) || ShouldForwardToHost())
            return;

        var now = DateTime.UtcNow;
        foreach (var stale in _activeCallsigns
                     .Where(entry => now - entry.Value > ActiveCallsignRetention)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _activeCallsigns.TryRemove(stale, out _);
        }

        var callsigns = _activeCallsigns
            .Where(entry => now - entry.Value <= ActiveCallsignRetention)
            .Select(entry => entry.Key)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (callsigns.Length == 0)
            return;

        Trace("MONITOR", $"tick active={callsigns.Length} callsigns={string.Join(",", callsigns)}");

        foreach (var callsign in callsigns)
        {
            await _messageGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_receivedMessages.TryGetValue(callsign, out var data)
                    && data.LastPollUtc is { } lastPollUtc
                    && lastPollUtc >= DateTime.UtcNow - AutomaticPollWindow)
                {
                    Trace("MONITOR", $"skip-recent callsign={callsign} lastPollUtc={lastPollUtc:O}");
                    continue;
                }

                Trace("MONITOR", $"poll callsign={callsign}");
                await PollUpstreamAsync(callsign, settings, ct).ConfigureAwait(false);
            }
            catch (HoppieUpstreamException e)
            {
                Trace("MONITOR", $"upstream-error callsign={callsign} {Summarize(e.Content)}");
            }
            catch (Exception e)
            {
                TraceException("MONITOR", $"poll-failed callsign={callsign}", e);
            }
            finally
            {
                _messageGate.Release();
            }
        }
    }

    private void TrackActiveCallsign(string? callsign, string reason)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return;

        var normalized = callsign.Trim().ToUpperInvariant();
        _activeCallsigns[normalized] = DateTime.UtcNow;
        Trace("MONITOR", $"track callsign={normalized} reason={reason}");
    }

    private void CleanReceivedMessages()
    {
        var expectedRecipients = Math.Max(1, _peerCount + 1);
        var cutoff = DateTime.UtcNow - ActiveCallsignRetention;
        foreach (var (callsign, data) in _receivedMessages.ToArray())
        {
            data.Messages.RemoveAll(message => message.SeenClientInstances.Count >= expectedRecipients);
            if (data.Messages.Count == 0 && (data.LastPollUtc == null || data.LastPollUtc < cutoff))
                _receivedMessages.Remove(callsign);
        }
    }

    private void CleanSentMessages()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        _sentMessages.RemoveAll(message => message.SentAtUtc < cutoff);
        if (_sentMessages.Count > 40)
            _sentMessages.RemoveRange(0, _sentMessages.Count - 40);
    }

    private bool ShouldForwardToHost() => _sessionRole.IsJoiner && _peerCount > 0;

    private void PublishStatus()
    {
        var settings = _settingsStore.Current;
        var endpointStatus = !settings.Enabled
            ? "Hoppie sync is off."
            : settings.RecorderModeEnabled
                ? "Recorder mode is running."
            : !_listenerRunning
                ? "Hoppie sync could not start."
                : string.IsNullOrWhiteSpace(settings.UpstreamLogon)
                    ? "Enter the host Hoppie logon."
                    : ShouldForwardToHost()
                        ? "Connected through the host."
                        : _peerCount > 0
                            ? "This PC owns the shared Hoppie session."
                            : "Hoppie sync is ready.";

        var transparentStatus = settings.Enabled && settings.TransparentCaptureEnabled
            ? !HoppieTransparentCaptureManager.IsAdministrator
                ? " Capture restart required."
                : !_transparentCapture.Current.HostsOverrideInstalled
                ? " Capture not installed."
                : !_transparentHttpRunning && !_transparentHttpsRunning
                    ? " Capture unavailable."
                    : _transparentHttpRunning && _transparentHttpsRunning
                        ? " Capture active."
                        : _transparentHttpsRunning
                            ? " HTTPS capture active."
                            : " HTTP capture active."
            : string.Empty;

        _status.OnNext(new(_listenerRunning || _transparentHttpRunning || _transparentHttpsRunning, settings.LocalUrl, endpointStatus + transparentStatus));
    }

    private async Task<string?> TryCompletePollAsync(
        string clientInstanceId,
        string callsign,
        HoppieSettings settings,
        bool forceUpstream,
        string reason,
        CancellationToken ct)
    {
        await _messageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryHasPendingMessagesLocked(clientInstanceId, callsign, out var pendingCount))
            {
                Trace("POLL", $"deliver-ready client={clientInstanceId} callsign={callsign} pending={pendingCount} reason={reason}");
                return AttachPendingMessagesForClient(clientInstanceId, callsign, "ok", reason);
            }

            var dataExists = _receivedMessages.TryGetValue(callsign, out var data);
            var needsPoll = forceUpstream || !dataExists || data!.LastPollUtc == null || data.LastPollUtc < DateTime.UtcNow - PollRefreshWindow;
            Log.Debug("[Hoppie] POLL client={Client} callsign={Callsign} needsUpstream={NeedsPoll} cachedMessages={CachedMessages} force={Force}",
                clientInstanceId,
                callsign,
                needsPoll,
                data?.Messages.Count ?? 0,
                forceUpstream);
            Trace("POLL", $"check client={clientInstanceId} callsign={callsign} needsUpstream={needsPoll} cachedMessages={data?.Messages.Count ?? 0} force={forceUpstream}");

            if (needsPoll)
            {
                await PollUpstreamAsync(callsign, settings, ct).ConfigureAwait(false);
                if (TryHasPendingMessagesLocked(clientInstanceId, callsign, out pendingCount))
                {
                    Trace("POLL", $"deliver-after-upstream client={clientInstanceId} callsign={callsign} pending={pendingCount} reason={reason}");
                    return AttachPendingMessagesForClient(clientInstanceId, callsign, "ok", reason);
                }
            }

            return null;
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private bool TryHasPendingMessagesLocked(string clientInstanceId, string callsign, out int pendingCount)
    {
        pendingCount = 0;
        if (!_receivedMessages.TryGetValue(callsign, out var data))
            return false;

        pendingCount = data.Messages.Count(message => !message.SeenClientInstances.Contains(clientInstanceId));
        return pendingCount > 0;
    }

    private bool ShouldPollImmediatelyLocked(string callsign)
    {
        if (_pollWaiters.TryGetValue(callsign, out var waiters) && waiters.Count > 0)
            return false;

        if (!_receivedMessages.TryGetValue(callsign, out var data))
            return true;

        return data.LastPollUtc == null || data.LastPollUtc < DateTime.UtcNow - ImmediatePollWindow;
    }

    private TaskCompletionSource<bool> RegisterPollWaiterLocked(string callsign)
    {
        if (!_pollWaiters.TryGetValue(callsign, out var waiters))
        {
            waiters = [];
            _pollWaiters[callsign] = waiters;
        }

        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters.Add(waiter);
        return waiter;
    }

    private void UnregisterPollWaiterLocked(string callsign, TaskCompletionSource<bool> waiter)
    {
        if (!_pollWaiters.TryGetValue(callsign, out var waiters))
            return;

        waiters.Remove(waiter);
        if (waiters.Count == 0)
            _pollWaiters.Remove(callsign);
    }

    private void SignalPollWaitersLocked(string callsign)
    {
        if (!_pollWaiters.TryGetValue(callsign, out var waiters) || waiters.Count == 0)
            return;

        foreach (var waiter in waiters.ToArray())
            waiter.TrySetResult(true);
    }

    private static async Task<ProxyRequest> ParseRequestAsync(HttpListenerRequest request, string clientInstanceId, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.QueryString.AllKeys)
        {
            if (key != null)
                values[key] = request.QueryString[key];
        }

        if (request.HasEntityBody)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var contentType = request.ContentType ?? string.Empty;

            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        values[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                }
            }
            else
            {
                foreach (var (key, value) in ParseFormUrlEncoded(body))
                    values[key] = value;
            }
        }

        ProxyRequest parsed = new(
            ClientInstanceId: clientInstanceId,
            Logon: GetValue(values, "logon"),
            FromCallsign: GetValue(values, "from"),
            ToCallsign: GetValue(values, "to"),
            MsgType: GetValue(values, "type"),
            Packet: GetValue(values, "packet"));

        Log.Debug("[Hoppie] Parsed local request {Request}", DescribeProxyRequest(parsed));
        Trace("PARSE", $"local {DescribeProxyRequest(parsed)}");
        return parsed;

        static string GetValue(IReadOnlyDictionary<string, string?> source, string key) =>
            source.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
    }

    private static async Task<ProxyRequest> ParseRequestAsync(HttpRequest request, string clientInstanceId, CancellationToken ct)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Query)
            values[pair.Key] = pair.Value.ToString();

        if (request.ContentLength is > 0)
        {
            request.EnableBuffering();
            string? body = null;

            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                foreach (var pair in form)
                    values[pair.Key] = pair.Value.ToString();
            }
            else
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
                body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                request.Body.Position = 0;
            }

            if (body != null)
            {
                var contentType = request.ContentType ?? string.Empty;
                if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            values[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    }
                }
                else
                {
                    foreach (var (key, value) in ParseFormUrlEncoded(body))
                        values[key] = value;
                }
            }
        }

        ProxyRequest parsed = new(
            ClientInstanceId: clientInstanceId,
            Logon: GetValue(values, "logon"),
            FromCallsign: GetValue(values, "from"),
            ToCallsign: GetValue(values, "to"),
            MsgType: GetValue(values, "type"),
            Packet: GetValue(values, "packet"));

        Log.Debug("[Hoppie] Parsed transparent request {Request}", DescribeProxyRequest(parsed));
        Trace("PARSE", $"transparent {DescribeProxyRequest(parsed)}");
        return parsed;

        static string GetValue(IReadOnlyDictionary<string, string?> source, string key) =>
            source.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ParseFormUrlEncoded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            yield break;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index < 0)
            {
                yield return new(DecodeFormComponent(pair), string.Empty);
                continue;
            }

            var key = DecodeFormComponent(pair[..index]);
            var value = DecodeFormComponent(pair[(index + 1)..]);
            yield return new(key, value);
        }
    }

    private static string DecodeFormComponent(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool IsConnectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.TrimEnd('/').Equals("/acars/system/connect.html", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeListenerRequest(HttpListenerRequest request)
    {
        var url = request.Url?.ToString() ?? "<null>";
        var host = request.UserHostName ?? request.Headers["Host"] ?? "<unknown>";
        return $"method={request.HttpMethod} host={host} url={SanitizeUrl(url)} contentType={request.ContentType ?? "<none>"}";
    }

    private static string DescribeAspNetRequest(HttpRequest request)
    {
        var scheme = request.Scheme;
        var host = request.Host.HasValue ? request.Host.Value : "<unknown>";
        var path = request.Path.HasValue ? request.Path.Value : "/";
        var query = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
        return $"method={request.Method} host={host} url={SanitizeUrl($"{scheme}://{host}{path}{query}")} contentType={request.ContentType ?? "<none>"}";
    }

    private static string DescribeProxyRequest(ProxyRequest request) =>
        $"client={request.ClientInstanceId} type={Display(request.MsgType)} from={Display(request.FromCallsign)} to={Display(request.ToCallsign)} logon={Summarize(request.Logon, 24)} packet={Summarize(request.Packet, 96)}";

    private static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var query = string.Join("&", uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var split = part.Split('=', 2);
                if (split.Length != 2)
                    return part;

                return split[0].Equals("logon", StringComparison.OrdinalIgnoreCase)
                    ? $"{split[0]}=<redacted>"
                    : part;
            }));

        return string.IsNullOrWhiteSpace(query)
            ? uri.GetLeftPart(UriPartial.Path)
            : $"{uri.GetLeftPart(UriPartial.Path)}?{query}";
    }

    private static string Summarize(string? value, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}...({compact.Length} chars)";
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value;

    private static void Trace(string category, string message) =>
        HoppieDump.Write(category, message);

    private static void TraceException(string category, string context, Exception exception) =>
        HoppieDump.WriteException(category, context, exception);

    private static async Task WriteResponseAsync(HttpListenerResponse response, string content, string contentType, CancellationToken ct)
    {
        response.StatusCode = 200;
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;

        var bytes = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string AppendQuery(string url, IReadOnlyDictionary<string, string?> parameters)
    {
        var query = string.Join("&", parameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

        if (string.IsNullOrWhiteSpace(query))
            return url;

        return url.Contains('?', StringComparison.Ordinal) ? $"{url}&{query}" : $"{url}?{query}";
    }

    private static string AppendMessagesToResponse(string response, IEnumerable<string> messages)
    {
        var delivered = string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        if (string.IsNullOrWhiteSpace(delivered))
            return string.IsNullOrWhiteSpace(response) ? "ok" : response;

        var baseResponse = string.IsNullOrWhiteSpace(response) ? "ok" : response.TrimEnd();
        return baseResponse.EndsWith(' ')
            ? baseResponse + delivered + " "
            : baseResponse + " " + delivered + " ";
    }

    private static bool AreDuplicatePayloads(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;

        var normalizedLeft = NormalizeDuplicatePayload(left);
        var normalizedRight = NormalizeDuplicatePayload(right);

        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            return false;

        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
            return true;

        var lengthDiff = Math.Abs(normalizedLeft.Length - normalizedRight.Length);
        if (lengthDiff <= 3
            && (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
                || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeDuplicatePayload(string value)
    {
        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray();
        return new string(chars);
    }

    private static string[] ExtractMessages(string content)
    {
        var messages = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var ch in content)
        {
            if (ch == '{')
                depth++;

            if (depth > 0)
                current.Append(ch);

            if (ch != '}')
                continue;

            depth--;
            if (depth == 0)
            {
                messages.Add(current.ToString());
                current.Clear();
            }
        }

        return messages.ToArray();
    }

    public sealed record HoppieProxyStatus(bool IsRunning, string LocalUrl, string Message);

    private sealed record ProxyRequest(
        string ClientInstanceId,
        string Logon,
        string FromCallsign,
        string ToCallsign,
        string MsgType,
        string Packet);

    private sealed record IncomingMessage(string Data)
    {
        public HashSet<string> SeenClientInstances { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CallsignData
    {
        public DateTime? LastPollUtc { get; set; }
        public List<IncomingMessage> Messages { get; } = [];
    }

    private sealed record OutgoingMessage(
        string Payload,
        string FromCallsign,
        string ToCallsign,
        string PacketType,
        string SendingClientInstance,
        string UpstreamResponse,
        DateTime SentAtUtc);

    private sealed class HoppieUpstreamException(string content) : Exception(content)
    {
        public string Content { get; } = content;
    }

    private sealed record HoppieTunnelRequest(
        string RequestId,
        string ClientInstanceId,
        string Logon,
        string FromCallsign,
        string ToCallsign,
        string MsgType,
        string Packet)
    {
        public sealed class Codec : IPacketCodec<HoppieTunnelRequest>
        {
            public void Encode(HoppieTunnelRequest packet, BinaryWriter bw)
            {
                bw.Write(packet.RequestId);
                bw.Write(packet.ClientInstanceId);
                bw.Write(packet.Logon);
                bw.Write(packet.FromCallsign);
                bw.Write(packet.ToCallsign);
                bw.Write(packet.MsgType);
                bw.Write(packet.Packet);
            }

            public HoppieTunnelRequest Decode(BinaryReader br) => new(
                br.ReadString(),
                br.ReadString(),
                br.ReadString(),
                br.ReadString(),
                br.ReadString(),
                br.ReadString(),
                br.ReadString());
        }
    }

    private sealed record HoppieTunnelResponse(string RequestId, string ClientInstanceId, string Content)
    {
        public sealed class Codec : IPacketCodec<HoppieTunnelResponse>
        {
            public void Encode(HoppieTunnelResponse packet, BinaryWriter bw)
            {
                bw.Write(packet.RequestId);
                bw.Write(packet.ClientInstanceId);
                bw.Write(packet.Content);
            }

            public HoppieTunnelResponse Decode(BinaryReader br) => new(
                br.ReadString(),
                br.ReadString(),
                br.ReadString());
        }
    }

    private sealed record HoppieFanoutMessages(string Callsign, string[] Messages)
    {
        public sealed class Codec : IPacketCodec<HoppieFanoutMessages>
        {
            public void Encode(HoppieFanoutMessages packet, BinaryWriter bw)
            {
                bw.Write(packet.Callsign);
                bw.Write(packet.Messages.Length);
                foreach (var message in packet.Messages)
                    bw.Write(message);
            }

            public HoppieFanoutMessages Decode(BinaryReader br)
            {
                var callsign = br.ReadString();
                var count = br.ReadInt32();
                var messages = new string[count];
                for (var i = 0; i < count; i++)
                    messages[i] = br.ReadString();

                return new(callsign, messages);
            }
        }
    }

    private sealed record HoppieFanoutAck(string Callsign, string ClientInstanceId, string[] Messages)
    {
        public sealed class Codec : IPacketCodec<HoppieFanoutAck>
        {
            public void Encode(HoppieFanoutAck packet, BinaryWriter bw)
            {
                bw.Write(packet.Callsign);
                bw.Write(packet.ClientInstanceId);
                bw.Write(packet.Messages.Length);
                foreach (var message in packet.Messages)
                    bw.Write(message);
            }

            public HoppieFanoutAck Decode(BinaryReader br)
            {
                var callsign = br.ReadString();
                var clientInstanceId = br.ReadString();
                var count = br.ReadInt32();
                var messages = new string[count];
                for (var i = 0; i < count; i++)
                    messages[i] = br.ReadString();

                return new(callsign, clientInstanceId, messages);
            }
        }
    }
}
