using Microsoft.Extensions.DependencyInjection;

namespace ModernScreenShot.App;

/// <summary>
/// Feature wiring. Later tasks register their services and start-up logic here so App.xaml.cs stays stable.
/// </summary>
public partial class App
{
    partial void RegisterFeatureServices(IServiceCollection services)
    {
        // Feature services (overlay, editor, output, shell, ...) are registered here by later tasks.
    }

    partial void OnStartupCompleted(string[] args)
    {
        // Tray/hotkeys/single-instance start here in later tasks.
        ModernScreenShot.App.Services.Log.Info($"Startup completed ({args.Length} args); no shell features registered yet.");
    }

    partial void OnSmokeTest(IServiceProvider services)
    {
        // Feature-specific smoke checks go here.
    }
}
