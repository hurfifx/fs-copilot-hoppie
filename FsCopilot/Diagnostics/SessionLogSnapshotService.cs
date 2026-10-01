namespace FsCopilot.Diagnostics;

using System.Reactive.Disposables;
using Network;
using Simulation;

public sealed class SessionLogSnapshotService : IDisposable
{
    private const int ChunkSize = 12 * 1024;
    private static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(6);

    private readonly MasterSwitch _masterSwitch;
    private readonly INetwork _network;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, IncomingTransfer> _incoming = new(StringComparer.Ordinal);

    private DateTime _lastCaptureUtc = DateTime.MinValue;
    private string _lastConnectedRole = "master";
    private bool _hasPeers;
    private TaskCompletionSource<bool>? _pendingRemoteSnapshots;
    private HashSet<string>? _expectedRemoteFiles;

    public SessionLogSnapshotService(MasterSwitch masterSwitch, INetwork network)
    {
        _masterSwitch = masterSwitch;
        _network = network;

        _network.RegisterPacket<SnapshotChunk, SnapshotChunk.Codec>();
        _network.RegisterPacket<SnapshotControl, SnapshotControl.Codec>();

        _subscriptions.Add(Observable
            .CombineLatest(
                _network.Peers.StartWith(Array.Empty<Peer>()),
                _masterSwitch.Master.StartWith(_masterSwitch.IsMaster),
                (peers, isMaster) => new { HasPeers = peers.Count > 0, Role = isMaster ? "master" : "slave" })
            .Subscribe(state =>
            {
                lock (_sync)
                {
                    _hasPeers = state.HasPeers;
                    if (state.HasPeers)
                        _lastConnectedRole = state.Role;
                }
            }));

        _subscriptions.Add(_network.Stream<SnapshotChunk>()
            .Subscribe(OnSnapshotChunk));
        _subscriptions.Add(_network.Stream<SnapshotControl>()
            .Subscribe(OnSnapshotControl));
    }

    public void Dispose() => _subscriptions.Dispose();

    public bool Capture()
    {
        return CaptureCore(sendRemote: true);
    }

    public async Task<bool> CaptureAndExchangeAsync(TimeSpan? timeout = null)
    {
        var sentRemote = CaptureCore(sendRemote: true);
        if (!HasPeers())
            return sentRemote;

        var expectedFiles = GetExpectedRemoteFiles();
        if (expectedFiles.Count == 0)
            return sentRemote;

        TaskCompletionSource<bool> waiter;
        lock (_sync)
        {
            _expectedRemoteFiles = expectedFiles;
            _pendingRemoteSnapshots = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = _pendingRemoteSnapshots;
        }

        var requestId = Guid.NewGuid().ToString("N");
        _network.SendAll(new SnapshotControl(requestId, SnapshotControlKind.CaptureNow));
        Log.Debug("[Logs] Requested peer snapshot exchange {RequestId}", requestId);

        var completed = false;
        try
        {
            var delayTask = Task.Delay(timeout ?? DefaultExchangeTimeout);
            var done = await Task.WhenAny(waiter.Task, delayTask).ConfigureAwait(false);
            completed = done == waiter.Task && waiter.Task.IsCompletedSuccessfully && waiter.Task.Result;
            if (!completed)
            {
                lock (_sync)
                {
                    var waitingFor = _expectedRemoteFiles == null || _expectedRemoteFiles.Count == 0
                        ? "unknown"
                        : string.Join(", ", _expectedRemoteFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                    Log.Warning("[Logs] Timed out waiting for remote snapshots: {Files}", waitingFor);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _expectedRemoteFiles = null;
                _pendingRemoteSnapshots = null;
            }
        }

        return sentRemote || completed;
    }

    private bool CaptureCore(bool sendRemote)
    {
        var role = ResolveRoleSuffix();
        var hasPeers = HasPeers();
        lock (_sync)
        {
            if (DateTime.UtcNow - _lastCaptureUtc < TimeSpan.FromSeconds(1))
                return sendRemote && hasPeers && ResendLatestSnapshots(role);

            _lastCaptureUtc = DateTime.UtcNow;
        }

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var files = new[]
            {
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, "log"),
                    DestinationPath: Path.Combine(baseDir, $"log{role}"),
                    LogicalName: $"log{role}"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, "hoppie-dump.txt"),
                    DestinationPath: Path.Combine(baseDir, $"hoppie-dump{role}.txt"),
                    LogicalName: $"hoppie-dump{role}.txt"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"gsx-parking-dump{role}.log"),
                    DestinationPath: Path.Combine(baseDir, $"gsx-parking-dump{role}.log"),
                    LogicalName: $"gsx-parking-dump{role}.log"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"gsx-sync-decision-dump{role}.log"),
                    DestinationPath: Path.Combine(baseDir, $"gsx-sync-decision-dump{role}.log"),
                    LogicalName: $"gsx-sync-decision-dump{role}.log"),
                new SnapshotFile(
                    SourcePath: Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Virtuali",
                        "Couatl.log"),
                    DestinationPath: Path.Combine(baseDir, $"Couatllog{role}.log"),
                    LogicalName: $"Couatllog{role}.log")
            };

            var sentRemote = false;
            foreach (var file in files)
            {
                var copied = CopyLocalSnapshot(file.SourcePath, file.DestinationPath);
                if (copied == null) continue;

                if (sendRemote && HasPeers())
                {
                    SendSnapshot(file.LogicalName, copied);
                    sentRemote = true;
                }
            }

            Log.Information("[Logs] Captured session snapshots for {Role}", role);
            return sentRemote;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Logs] Failed to capture session snapshots");
            return false;
        }
    }

    private bool ResendLatestSnapshots(string role)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var files = new[]
            {
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"log{role}"),
                    DestinationPath: Path.Combine(baseDir, $"log{role}"),
                    LogicalName: $"log{role}"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"hoppie-dump{role}.txt"),
                    DestinationPath: Path.Combine(baseDir, $"hoppie-dump{role}.txt"),
                    LogicalName: $"hoppie-dump{role}.txt"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"gsx-parking-dump{role}.log"),
                    DestinationPath: Path.Combine(baseDir, $"gsx-parking-dump{role}.log"),
                    LogicalName: $"gsx-parking-dump{role}.log"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"gsx-sync-decision-dump{role}.log"),
                    DestinationPath: Path.Combine(baseDir, $"gsx-sync-decision-dump{role}.log"),
                    LogicalName: $"gsx-sync-decision-dump{role}.log"),
                new SnapshotFile(
                    SourcePath: Path.Combine(baseDir, $"Couatllog{role}.log"),
                    DestinationPath: Path.Combine(baseDir, $"Couatllog{role}.log"),
                    LogicalName: $"Couatllog{role}.log")
            };

            var resent = false;
            foreach (var file in files)
            {
                if (!File.Exists(file.SourcePath)) continue;
                var data = File.ReadAllBytes(file.SourcePath);
                SendSnapshot(file.LogicalName, data);
                resent = true;
            }

            if (resent)
                Log.Debug("[Logs] Re-sent cached session snapshots for {Role}", role);

            return resent;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Logs] Failed while re-sending cached session snapshots for {Role}", role);
            return false;
        }
    }

    private void OnSnapshotControl(SnapshotControl control)
    {
        try
        {
            if (control.Kind != SnapshotControlKind.CaptureNow)
                return;

            Log.Debug("[Logs] Received peer snapshot request {RequestId}", control.RequestId);
            CaptureCore(sendRemote: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Logs] Failed while handling peer snapshot request {RequestId}", control.RequestId);
        }
    }

    private void OnSnapshotChunk(SnapshotChunk chunk)
    {
        try
        {
            var completed = default(CompletedTransfer?);
            lock (_sync)
            {
                if (!_incoming.TryGetValue(chunk.TransferId, out var transfer))
                {
                    transfer = new IncomingTransfer(chunk.FileName, chunk.TotalChunks);
                    _incoming.Add(chunk.TransferId, transfer);
                }

                if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= transfer.TotalChunks)
                    return;

                transfer.Chunks[chunk.ChunkIndex] = chunk.Data;
                transfer.ReceivedCount++;

                if (transfer.ReceivedCount >= transfer.TotalChunks)
                {
                    _incoming.Remove(chunk.TransferId);
                    completed = new CompletedTransfer(transfer.FileName, transfer.Chunks);
                }
            }

            if (completed == null) return;

            var destinationPath = Path.Combine(AppContext.BaseDirectory, completed.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
            using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            foreach (var data in completed.Chunks)
            {
                if (data is { Length: > 0 })
                    destination.Write(data, 0, data.Length);
            }
            destination.Flush(true);

            Log.Information("[Logs] Received remote snapshot {FileName}", completed.FileName);

            lock (_sync)
            {
                if (_expectedRemoteFiles?.Remove(completed.FileName) == true && _expectedRemoteFiles.Count == 0)
                    _pendingRemoteSnapshots?.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Logs] Failed while receiving remote snapshot {FileName}", chunk.FileName);
        }
    }

    private void SendSnapshot(string logicalName, byte[] content)
    {
        if (content.Length == 0)
        {
            _network.SendAll(new SnapshotChunk(Guid.NewGuid().ToString("N"), logicalName, 1, 0, []));
            return;
        }

        var transferId = Guid.NewGuid().ToString("N");
        var totalChunks = (content.Length + ChunkSize - 1) / ChunkSize;
        for (var i = 0; i < totalChunks; i++)
        {
            var offset = i * ChunkSize;
            var length = Math.Min(ChunkSize, content.Length - offset);
            var buffer = new byte[length];
            Buffer.BlockCopy(content, offset, buffer, 0, length);
            _network.SendAll(new SnapshotChunk(transferId, logicalName, totalChunks, i, buffer));
        }

        Log.Debug("[Logs] Sent remote snapshot {FileName} in {ChunkCount} chunk(s)", logicalName, totalChunks);
    }

    private string ResolveRoleSuffix()
    {
        lock (_sync)
            return _hasPeers ? (_masterSwitch.IsMaster ? "master" : "slave") : _lastConnectedRole;
    }

    private bool HasPeers()
    {
        lock (_sync) return _hasPeers;
    }

    private HashSet<string> GetExpectedRemoteFiles()
    {
        var localRole = ResolveRoleSuffix();
        var remoteRole = localRole.Equals("master", StringComparison.OrdinalIgnoreCase) ? "slave" : "master";
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"log{remoteRole}",
            $"hoppie-dump{remoteRole}.txt",
            $"gsx-parking-dump{remoteRole}.log",
            $"gsx-sync-decision-dump{remoteRole}.log",
            $"Couatllog{remoteRole}.log"
        };
    }

    private static byte[]? CopyLocalSnapshot(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            Log.Debug("[Logs] Snapshot source missing: {Path}", sourcePath);
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);

        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        var data = memory.ToArray();
        File.WriteAllBytes(destinationPath, data);
        return data;
    }

    private sealed record SnapshotFile(string SourcePath, string DestinationPath, string LogicalName);

    private sealed class IncomingTransfer
    {
        public IncomingTransfer(string fileName, int totalChunks)
        {
            FileName = fileName;
            TotalChunks = Math.Max(totalChunks, 1);
            Chunks = new byte[TotalChunks][];
        }

        public string FileName { get; }
        public int TotalChunks { get; }
        public int ReceivedCount { get; set; }
        public byte[][] Chunks { get; }
    }

    private sealed record CompletedTransfer(string FileName, byte[][] Chunks);

    private enum SnapshotControlKind : byte
    {
        CaptureNow = 0
    }

    private sealed record SnapshotControl(
        string RequestId,
        SnapshotControlKind Kind)
    {
        public sealed class Codec : IPacketCodec<SnapshotControl>
        {
            public void Encode(SnapshotControl packet, BinaryWriter bw)
            {
                bw.Write(packet.RequestId);
                bw.Write((byte)packet.Kind);
            }

            public SnapshotControl Decode(BinaryReader br)
            {
                var requestId = br.ReadString();
                var kind = (SnapshotControlKind)br.ReadByte();
                return new(requestId, kind);
            }
        }
    }

    private sealed record SnapshotChunk(
        string TransferId,
        string FileName,
        int TotalChunks,
        int ChunkIndex,
        byte[] Data)
    {
        public sealed class Codec : IPacketCodec<SnapshotChunk>
        {
            public void Encode(SnapshotChunk packet, BinaryWriter bw)
            {
                bw.Write(packet.TransferId);
                bw.Write(packet.FileName);
                bw.Write(packet.TotalChunks);
                bw.Write(packet.ChunkIndex);
                bw.Write(packet.Data.Length);
                bw.Write(packet.Data);
            }

            public SnapshotChunk Decode(BinaryReader br)
            {
                var transferId = br.ReadString();
                var fileName = br.ReadString();
                var totalChunks = br.ReadInt32();
                var chunkIndex = br.ReadInt32();
                var length = br.ReadInt32();
                var data = br.ReadBytes(length);
                return new(transferId, fileName, totalChunks, chunkIndex, data);
            }
        }
    }
}
