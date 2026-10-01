namespace FsCopilot;

using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Connection;
using Diagnostics;
using Hoppie;
using Microsoft.Extensions.DependencyInjection;
using Network;
using Simulation;
using Splat;
using ViewModels;
using Views;

public class App : Application
{
    public static readonly string Version =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0] ?? "unknown";

    public static readonly string BuildLabel = GetBuildLabel();
    public static readonly string WindowTitle = string.IsNullOrWhiteSpace(BuildLabel)
        ? "FS Copilot"
        : $"FS Copilot [{BuildLabel}]";
    public static readonly string VersionDisplay = string.IsNullOrWhiteSpace(BuildLabel)
        ? Version
        : $"{Version} - {BuildLabel}";
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var dev = args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            if (!dev)
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = Locator.Current.GetService<MainViewModel>()
                };
                desktop.Exit += (_, _) =>
                {
                    TryAction(() => Locator.Current.GetService<SessionLogSnapshotService>()
                        ?.CaptureAndExchangeAsync()
                        .GetAwaiter()
                        .GetResult());
                    TryDispose<MainViewModel>();
                    TryDispose<Coordinator>();
                    TryDispose<FenixSidestickCoordinator>();
                    TryDispose<LocalJoystickService>();
                    TryAction(() => Locator.Current.GetService<INetwork>()?.Disconnect());
                    TryAction(() => Locator.Current.GetService<MasterSwitch>()?.TakeControl());
                    TryDispose<HoppieCaptureAutomation>();
                    TryDispose<HoppieProxyService>();
                    TryDispose<HoppieTransparentCaptureManager>();
                    TryDispose<HoppieSettingsStore>();
                    TryDispose<SimClient>();
                    TryDispose<SessionRole>();
                    TryAction(() =>
                    {
                        if (Locator.Current.GetService<INetwork>() is IDisposable disposable)
                            disposable.Dispose();
                    });
                };
            }
            else
            {
                desktop.MainWindow = new DevelopWindow
                {
                    DataContext = Locator.Current.GetService<DevelopViewModel>()
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private static string GetBuildLabel()
    {
        try
        {
            var labelPath = Path.Combine(AppContext.BaseDirectory, "BUILD_LABEL.txt");
            if (File.Exists(labelPath))
            {
                var explicitLabel = File.ReadAllText(labelPath).Trim();
                if (!string.IsNullOrWhiteSpace(explicitLabel))
                    return explicitLabel;
            }

            var path = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDispose<T>() where T : class
    {
        TryAction(() =>
        {
            if (Locator.Current.GetService<T>() is IDisposable disposable)
                disposable.Dispose();
        });
    }

    private static void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error(e, "[Application] Failed to shut down a service cleanly");
        }
    }
}
