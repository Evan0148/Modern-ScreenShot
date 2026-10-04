using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static string[] StartupArgs { get; private set; } = [];

    /// <summary>Feature modules register their services here (implemented in App.Features.cs / other partial files).</summary>
    partial void RegisterFeatureServices(IServiceCollection services);

    /// <summary>Called after core services and language are ready in normal (non-smoke) runs.</summary>
    partial void OnStartupCompleted(string[] args);

    /// <summary>Extra smoke checks contributed by features. Throw to fail the smoke run.</summary>
    partial void OnSmokeTest(IServiceProvider services);

    private static readonly List<Type> RegisteredSingletons = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupArgs = e.Args;
        HookGlobalExceptionHandlers();
        bool smoke = e.Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));
        Log.Info($"Starting {(smoke ? "smoke test" : "app")} v{typeof(App).Assembly.GetName().Version} args=[{string.Join(' ', e.Args)}]");

        try
        {
            Services = BuildServices();
            var settings = Services.GetRequiredService<SettingsStore>();
            var loc = Services.GetRequiredService<LocalizationService>();
            loc.Apply(settings.Current.Language);

            if (smoke)
            {
                RunSmoke();
                return;
            }

            // --translate-test[=text] / --translate-install[=from-to]: end-to-end translation and
            // language-pack self checks. Run before the shell exactly like --smoke, because they need
            // no tray and must be able to report a clean exit code.
            string? translateTest = e.Args.FirstOrDefault(
                a => a.StartsWith("--translate-test", StringComparison.OrdinalIgnoreCase));
            string? translateInstall = e.Args.FirstOrDefault(
                a => a.StartsWith("--translate-install", StringComparison.OrdinalIgnoreCase));
            if (translateTest is not null || translateInstall is not null)
            {
                CaptureTranslateTestArgument(e.Args);
                RunTranslateDiagnosticAndShutdown(install: translateInstall is not null);
                return;
            }

            OnStartupCompleted(e.Args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal startup failure", ex);
            Shutdown(1);
        }
    }

    private IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        AddSingleton(services, _ =>
        {
            var store = new SettingsStore();
            store.Load();
            if (store.LoadError is not null) Log.Warn($"Settings load failed, defaults used: {store.LoadError}");
            return store;
        });
        AddSingleton(services, _ => new HistoryStore());
        AddSingleton(services, _ => new LocalizationService());
        AddSingleton(services, _ => new MonitorService());
        AddSingleton(services, _ => new ScreenCapturer());
        AddSingleton(services, _ => new WindowEnumerator());
        AddSingleton(services, sp => new WindowCapturer(sp.GetRequiredService<ScreenCapturer>()));
        RegisterFeatureServices(services);

        foreach (var d in services)
            if (d.Lifetime == ServiceLifetime.Singleton && !RegisteredSingletons.Contains(d.ServiceType))
                RegisteredSingletons.Add(d.ServiceType);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static void AddSingleton<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class =>
        services.AddSingleton(factory);

    private void RunSmoke()
    {
        int exitCode = 0;
        try
        {
            foreach (var type in RegisteredSingletons)
            {
                if (type.IsGenericTypeDefinition) continue;
                _ = Services.GetRequiredService(type);
                Log.Info($"smoke: resolved {type.FullName}");
            }

            var loc = Services.GetRequiredService<LocalizationService>();
            foreach (var lang in LocalizationService.SupportedLanguages)
            {
                loc.Apply(lang);
                var name = LocalizationService.Get("App.Name");
                if (name == "App.Name") throw new InvalidOperationException($"Language {lang} did not load.");
                Log.Info($"smoke: {lang} Tray.Region = {LocalizationService.Get("Tray.Region")}");
            }

            var missing = LocalizationService.FindMissingKeys();
            foreach (var m in missing) Log.Warn("smoke: " + m);
            if (missing.Count > 0) throw new InvalidOperationException($"{missing.Count} localization keys are missing.");

            var monitors = Services.GetRequiredService<MonitorService>();
            var vs = monitors.GetVirtualScreen();
            Log.Info($"smoke: virtual screen {vs}, monitors {monitors.GetMonitors().Count}");

            OnSmokeTest(Services);
            loc.Apply(Services.GetRequiredService<SettingsStore>().Current.Language);
            Log.Info("smoke: OK");
        }
        catch (Exception ex)
        {
            Log.Error("smoke: FAILED", ex);
            exitCode = 1;
        }
        Shutdown(exitCode);
    }

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Unhandled domain exception (terminating={args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        if (StartupArgs.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase))) Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"Exit code {e.ApplicationExitCode}");
        (Services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
