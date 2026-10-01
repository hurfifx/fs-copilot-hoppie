namespace FsCopilot.Hoppie;

using System.Globalization;
using System.Text.Json;

public sealed record HoppieSettings
{
    public const int DefaultLocalPort = 8751;
    public const string DefaultUpstreamUrl = "https://www.hoppie.nl/acars/system/connect.html";

    public bool Enabled { get; init; }
    public bool TransparentCaptureEnabled { get; init; }
    public bool RecorderModeEnabled { get; init; }
    public int AdminCaptureOptInVersion { get; init; }
    public string UpstreamLogon { get; init; } = string.Empty;
    public string UpstreamUrl { get; init; } = DefaultUpstreamUrl;
    public int LocalPort { get; init; } = DefaultLocalPort;

    public string LocalUrl => $"http://127.0.0.1:{LocalPort.ToString(CultureInfo.InvariantCulture)}/acars/system/connect.html";

    public HoppieSettings Normalize() => this with
    {
        Enabled = Enabled && AdminCaptureOptInVersion >= 1,
        TransparentCaptureEnabled = TransparentCaptureEnabled && AdminCaptureOptInVersion >= 1,
        UpstreamLogon = (UpstreamLogon ?? string.Empty).Trim(),
        UpstreamUrl = string.IsNullOrWhiteSpace(UpstreamUrl) ? DefaultUpstreamUrl : UpstreamUrl.Trim(),
        LocalPort = Math.Clamp(LocalPort, 1024, 65535)
    };
}

public sealed class HoppieSettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly BehaviorSubject<HoppieSettings> _changes;

    public HoppieSettingsStore()
    {
        _changes = new(Load());
    }

    public HoppieSettings Current => _changes.Value;

    public IObservable<HoppieSettings> Changes => _changes.DistinctUntilChanged();

    public void Save(HoppieSettings settings)
    {
        var normalized = settings.Normalize();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(normalized, JsonOptions));
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Hoppie] Failed to save local settings");
        }

        _changes.OnNext(normalized);
    }

    public void Dispose() => _changes.Dispose();

    private static HoppieSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new HoppieSettings().Normalize();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<HoppieSettings>(json, JsonOptions);
            if (settings == null)
                return new HoppieSettings().Normalize();

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var enabledPresent = doc.RootElement.TryGetProperty(nameof(HoppieSettings.Enabled), out _);
                    var transparentPresent = doc.RootElement.TryGetProperty(nameof(HoppieSettings.TransparentCaptureEnabled), out _);
                    var recorderPresent = doc.RootElement.TryGetProperty(nameof(HoppieSettings.RecorderModeEnabled), out _);
                    var optInPresent = doc.RootElement.TryGetProperty(nameof(HoppieSettings.AdminCaptureOptInVersion), out _);
                    settings = settings with
                    {
                        Enabled = optInPresent && enabledPresent && settings.Enabled,
                        TransparentCaptureEnabled = optInPresent && transparentPresent && settings.TransparentCaptureEnabled,
                        RecorderModeEnabled = recorderPresent && settings.RecorderModeEnabled
                    };
                }
            }
            catch (Exception e)
            {
                Log.Debug(e, "[Hoppie] Could not inspect stored settings for default migration");
            }

            return settings.Normalize();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Hoppie] Failed to load local settings");
            return new HoppieSettings().Normalize();
        }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FsCopilot",
        "hoppie-settings.json");
}
