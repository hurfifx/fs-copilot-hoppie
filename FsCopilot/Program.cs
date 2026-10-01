namespace FsCopilot;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Connection;
using Diagnostics;
using Hoppie;
using Microsoft.Extensions.DependencyInjection;
using Network;
using ReactiveUI.Avalonia;
using ReactiveUI.Avalonia.Splat;
using Serilog;
using Serilog.Events;
using Simulation;
using ViewModels;

sealed class Program
{
    private static Mutex? SingleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        if (HoppieTransparentCaptureManager.TryRunAdminCommand(args, out var helperExitCode))
        {
            Environment.ExitCode = helperExitCode;
            return;
        }

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        var isDebug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase));
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "unknown";
        var buildLabel = App.BuildLabel;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                path: "log",
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: false,
                shared: true,
                retainedFileCountLimit: null,
                fileSizeLimitBytes: null,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: isDev || isDebug ? LogEventLevel.Verbose : LogEventLevel.Debug
            )
            .CreateLogger();

        using (var hoppieStartupSettings = new HoppieSettingsStore())
        {
            if (TryRestartElevatedForHoppie(args, hoppieStartupSettings.Current))
            {
                Log.CloseAndFlush();
                return;
            }
        }

        if (!TryAcquireSingleInstance(isDev))
        {
            Log.Warning("[Application] Another FS Copilot window is already running; exiting duplicate launch");
            Log.CloseAndFlush();
            return;
        }

        HoppieDump.MarkRunStart(version, buildLabel);

        try
        {
            Log.Information("[Application] Loaded {Version} version ({BuildLabel})", version, buildLabel);
            DeployModuleToCommunity();
            BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Application] Something went wrong");
            HoppieDump.WriteException("APP", "Unhandled application exception", ex);
        }
        finally
        {
            HoppieDump.MarkRunEnd();
            SingleInstanceMutex?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private static bool TryAcquireSingleInstance(bool isDev)
    {
        if (isDev)
            return true;

        SingleInstanceMutex = new Mutex(true, "Local\\FsCopilot.Main.Instance", out var createdNew);
        if (createdNew)
            return true;

        SingleInstanceMutex.Dispose();
        SingleInstanceMutex = null;
        return false;
    }

    private static bool TryRestartElevatedForHoppie(string[] args, HoppieSettings settings)
    {
        if (!settings.Enabled
            || !settings.TransparentCaptureEnabled
            || settings.RecorderModeEnabled
            || HoppieTransparentCaptureManager.IsAdministrator)
        {
            return false;
        }

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            Log.Warning("[Hoppie] Enabled but FS Copilot could not locate its own executable for startup elevation");
            return false;
        }

        try
        {
            var forwardedArgs = args
                .Where(arg => !string.Equals(arg, "--hoppie-elevated", StringComparison.OrdinalIgnoreCase))
                .Append("--hoppie-elevated")
                .Select(QuoteArgument);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(' ', forwardedArgs),
                UseShellExecute = true,
                Verb = "runas"
            });

            if (process == null)
                return false;

            Log.Information("[Hoppie] Enabled; restarting FS Copilot elevated so join/leave does not prompt repeatedly");
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            Log.Warning("[Hoppie] Startup elevation was cancelled; Hoppie capture will wait until FS Copilot is restarted as administrator");
            return false;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Hoppie] Startup elevation failed; Hoppie capture will wait until FS Copilot is restarted as administrator");
            return false;
        }
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
            return "\"\"";

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var isDev = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        var isExperimental = args.Any(a => string.Equals(a, "--experimental", StringComparison.OrdinalIgnoreCase));
        var networkMode = ResolveNetworkMode(args, isExperimental);
        var peerId = Random.String(8);
        var name = Environment.UserName;

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUIWithMicrosoftDependencyResolver(
                services =>
                {
                    if (!isDev)
                    {
                        Log.Information("[Network] Mode {NetworkMode}", networkMode);
                        services.AddSingleton<INetwork>(CreateNetwork(networkMode, "p2p.fscopilot.com", peerId, name));
                        services.AddSingleton<SessionRole>();
                        services.AddSingleton<GsxSettingsStore>();
                        services.AddSingleton<HoppieSettingsStore>();
                        services.AddSingleton<HoppieTransparentCaptureManager>();
                        services.AddSingleton<HoppieCaptureAutomation>();
                        services.AddSingleton(sp => new HoppieProxyService(
                            peerId,
                            sp.GetRequiredService<INetwork>(),
                            sp.GetRequiredService<HoppieSettingsStore>(),
                            sp.GetRequiredService<SessionRole>(),
                            sp.GetRequiredService<HoppieTransparentCaptureManager>()));
                        services.AddSingleton<CrewSideState>();
                        services.AddSingleton<BehaviorControlOverrideState>();
                        services.AddSingleton<LocalJoystickService>();
                        services.AddSingleton<FenixSidestickProfileState>();
                        services.AddSingleton<FenixSidestickSessionState>();
                        services.AddSingleton<MasterSwitch>();
                        services.AddSingleton<SessionLogSnapshotService>();
                        services.AddSingleton(new Updater("http://p2p.fscopilot.com:2320"));
                        services.AddSingleton<Coordinator>();
                        services.AddSingleton<FenixSidestickCoordinator>();
                        services.AddSingleton(new SimClient("FS Copilot"));
                        services.AddSingleton(sp => new MainViewModel(
                            peerId,
                            name,
                            sp.GetRequiredService<INetwork>(),
                            sp.GetRequiredService<SimClient>(),
                            sp.GetRequiredService<MasterSwitch>(),
                            sp.GetRequiredService<SessionLogSnapshotService>(),
                            sp.GetRequiredService<Coordinator>(),
                            sp.GetRequiredService<GsxSettingsStore>(),
                            sp.GetRequiredService<Updater>(),
                            NetworkModeDisplayName(networkMode),
                            sp.GetRequiredService<SessionRole>(),
                            sp.GetRequiredService<HoppieSettingsStore>(),
                            sp.GetRequiredService<HoppieCaptureAutomation>(),
                            sp.GetRequiredService<HoppieProxyService>(),
                            sp.GetRequiredService<HoppieTransparentCaptureManager>(),
                            sp.GetRequiredService<CrewSideState>(),
                            sp.GetRequiredService<LocalJoystickService>(),
                            sp.GetRequiredService<FenixSidestickProfileState>(),
                            sp.GetRequiredService<FenixSidestickCoordinator>(),
                            sp.GetRequiredService<FenixSidestickSessionState>()
                        ));
                    }
                    else
                    {
                        services.AddSingleton(new SimClient("FS Copilot DEV"));
                        services.AddSingleton<DevelopViewModel>();
                    }
                },
                null)
            .RegisterReactiveUIViewsFromEntryAssembly()
            .WithInterFont()
            .LogToTrace();
    }

    private static INetwork CreateNetwork(NetworkMode mode, string host, string peerId, string name) =>
        mode switch
        {
            NetworkMode.Relay => new RelayNetwork(host, peerId, name),
            NetworkMode.Hybrid => new HybridNetwork(host, peerId, name),
            _ => new P2PNetwork(host, peerId, name)
        };

    private static NetworkMode ResolveNetworkMode(string[] args, bool isExperimental)
    {
        var explicitMode = ReadNetworkModeFromArgs(args)
            ?? Environment.GetEnvironmentVariable("FSCOPILOT_NETWORK_MODE")
            ?? ReadNetworkModeFile();

        if (TryParseNetworkMode(explicitMode, out var mode))
            return mode;

        return isExperimental ? NetworkMode.Hybrid : NetworkMode.Direct;
    }

    private static string? ReadNetworkModeFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--local", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--lan", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--direct", StringComparison.OrdinalIgnoreCase))
                return "direct";

            if (string.Equals(arg, "--relay", StringComparison.OrdinalIgnoreCase))
                return "relay";

            if (string.Equals(arg, "--hybrid", StringComparison.OrdinalIgnoreCase))
                return "hybrid";

            var split = arg.Split('=', 2);
            if (split.Length == 2 &&
                (string.Equals(split[0], "--network", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(split[0], "--network-mode", StringComparison.OrdinalIgnoreCase)))
                return split[1];

            if ((string.Equals(arg, "--network", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "--network-mode", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }

    private static string? ReadNetworkModeFile()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "network-mode.txt");
            if (!File.Exists(path)) return null;
            return File.ReadLines(path)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
        }
        catch (Exception e)
        {
            Log.Debug(e, "[Network] Could not read network-mode.txt");
            return null;
        }
    }

    private static bool TryParseNetworkMode(string? raw, out NetworkMode mode)
    {
        mode = NetworkMode.Direct;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "local":
            case "lan":
            case "direct":
            case "p2p":
                mode = NetworkMode.Direct;
                return true;
            case "relay":
            case "cloud":
                mode = NetworkMode.Relay;
                return true;
            case "hybrid":
            case "auto":
            case "experimental":
                mode = NetworkMode.Hybrid;
                return true;
            default:
                Log.Warning("[Network] Unknown network mode '{NetworkMode}'. Falling back to default.", raw);
                return false;
        }
    }

    private static string NetworkModeDisplayName(NetworkMode mode) =>
        mode switch
        {
            NetworkMode.Relay => "Relay",
            NetworkMode.Hybrid => "Hybrid",
            _ => "Local / Direct"
        };

    private static void DeployModuleToCommunity()
    {
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "Community", "fscopilot-bridge");
            if (!Directory.Exists(source))
            {
                Log.Debug("[Application] Missing FS Copilot module. Skipped");
                return;
            }

            var packagesPaths = GetInstalledPackagesPath();
            foreach (var packagesPath in packagesPaths)
            {
                var community = Path.Combine(packagesPath, "Community");
                if (!Directory.Exists(community)) Directory.CreateDirectory(community);
                Log.Debug("[Application] Found community folder: {0}", community);
                var target = Path.Combine(community, "fscopilot-bridge");
                if (Directory.Exists(target)) Directory.Delete(target, true);
                CopyDirectory(source, target, overwrite: true);
                // {
                //     CopyDirectory(source, target, overwrite: true);
                // }
                // else
                // {
                //     CopyDirectory(Path.Combine(source, "html_ui"), Path.Combine(target, "html_ui"), overwrite: true);
                //     File.Copy(Path.Combine(source, "layout.json"), Path.Combine(target, "layout.json"), overwrite: true);
                //     File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(target, "manifest.json"), overwrite: true);
                // }
                Log.Debug("[Application] FS Copilot module has been deployed to community");
            }
        }
        catch (Exception e)
        {
            Log.Debug(e.Message);
        }
    }

    /// Finds InstalledPackagesPath from UserCfg.opt (MS Store or Steam).
    private static IEnumerable<string> GetInstalledPackagesPath()
    {
        var cfgPaths = new List<string>()
        {
            // MS Store location
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            // MS Store 2024 location
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            // Steam location
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft Flight Simulator", "UserCfg.opt"),
            // Steam 2024 location
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft Flight Simulator 2024", "UserCfg.opt")
        };

        foreach (var path in cfgPaths.Where(File.Exists))
        {
            Log.Debug("[Application] Detected configuration path: {0}", path);
            foreach (var line in File.ReadAllLines(path))
            {
                if (!line.TrimStart().StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase)) continue;
                var m = Regex.Match(line, "\"([^\"]+)\"");
                if (m.Success)
                {
                    yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(m.Groups[1].Value));
                    break;
                }
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool overwrite)
    {
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSub, overwrite);
        }
    }
}
