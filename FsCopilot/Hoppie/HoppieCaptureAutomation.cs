namespace FsCopilot.Hoppie;

using System.Reactive.Disposables;
using Network;

public sealed class HoppieCaptureAutomation : IDisposable
{
    private readonly CompositeDisposable _d = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly INetwork _network;
    private readonly HoppieSettingsStore _settingsStore;
    private readonly HoppieTransparentCaptureManager _capture;
    private int _disposed;

    public HoppieCaptureAutomation(
        INetwork network,
        HoppieSettingsStore settingsStore,
        HoppieTransparentCaptureManager capture)
    {
        _network = network;
        _settingsStore = settingsStore;
        _capture = capture;

        _d.Add(_network.Peers
            .Select(peers => peers.Count > 0)
            .DistinctUntilChanged()
            .Subscribe(hasPeers => _ = HandlePeerStateChangedAsync(hasPeers)));
    }

    public async Task PrepareForJoinAsync()
    {
        await EnsureCaptureInstalledAsync("join-start").ConfigureAwait(false);
    }

    public async Task CancelPreparedJoinAsync()
    {
        await RemoveCaptureIfPresentAsync("join-cancelled").ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _d.Dispose();
        try
        {
            RemoveCaptureIfPresentAsync("dispose").GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Hoppie] Failed to remove automatic capture during dispose");
        }

        _gate.Dispose();
    }

    private async Task HandlePeerStateChangedAsync(bool hasPeers)
    {
        if (hasPeers)
            await EnsureCaptureInstalledAsync("peers-connected").ConfigureAwait(false);
        else
            await RemoveCaptureIfPresentAsync("peers-disconnected").ConfigureAwait(false);
    }

    private async Task EnsureCaptureInstalledAsync(string reason)
    {
        if (!ShouldManageCapture())
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await Task.Run(_capture.Install).ConfigureAwait(false);
            Log.Information("[Hoppie] Automatic capture install ({Reason}): {Message}", reason, result.Message);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Hoppie] Automatic capture install failed ({Reason})", reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveCaptureIfPresentAsync(string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _capture.Refresh();
            if (!_capture.Current.HostsOverrideInstalled)
                return;

            if (!HoppieTransparentCaptureManager.IsAdministrator)
            {
                Log.Warning("[Hoppie] Automatic capture remove skipped ({Reason}); restart FS Copilot as administrator to remove the Hoppie redirect cleanly", reason);
                return;
            }

            var result = await Task.Run(_capture.Remove).ConfigureAwait(false);
            Log.Information("[Hoppie] Automatic capture remove ({Reason}): {Message}", reason, result.Message);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Hoppie] Automatic capture remove failed ({Reason})", reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ShouldManageCapture()
    {
        var settings = _settingsStore.Current;
        return settings.Enabled
               && settings.TransparentCaptureEnabled
               && !settings.RecorderModeEnabled
               && HoppieTransparentCaptureManager.IsAdministrator;
    }
}
