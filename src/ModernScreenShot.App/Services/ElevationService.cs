using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using ModernScreenShot.App.Shell;

namespace ModernScreenShot.App.Services;

/// <summary>
/// Process elevation helpers. The app ships as <c>asInvoker</c> (see app.manifest), so it runs at the
/// caller's integrity level; capturing content that sits above higher-integrity always-on-top windows
/// (Task Manager, elevated apps) needs the process itself to run elevated. This service reports the
/// current state and can relaunch the app elevated via a UAC (runas) prompt.
/// </summary>
public static class ElevationService
{
    /// <summary>True when the current process runs with administrator (high integrity) rights.</summary>
    public static bool IsElevated { get; } = ComputeElevated();

    private static bool ComputeElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn($"Elevation check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Relaunches this executable elevated through the UAC prompt, then shuts the current instance
    /// down so the new elevated one can take over the single-instance mutex. The single-instance guard
    /// is released first: otherwise the elevated relaunch would see the mutex already held, forward its
    /// (empty) arguments back to this instance, and exit — leaving nothing elevated.
    /// If the relaunch cannot be started (e.g. the user declines the UAC prompt) the guard is
    /// re-acquired with <paramref name="onArgumentsReceived"/> so this instance stays the sole owner,
    /// and the method returns false with the current instance running unchanged.
    /// </summary>
    public static bool RestartAsAdmin(SingleInstance? single, Action<string[]> onArgumentsReceived)
    {
        if (IsElevated) return true; // already elevated; nothing to do
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Error("Cannot restart elevated: Environment.ProcessPath is empty.");
            return false;
        }

        // Release the single-instance mutex/pipe BEFORE launching so the elevated instance becomes the
        // owner instead of forwarding-and-exiting against us.
        single?.Dispose();

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true, // required for the "runas" verb to trigger the UAC prompt
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Most commonly ERROR_CANCELLED (1223) when the user dismisses the UAC dialog.
            Log.Warn($"Elevated relaunch was not started: {ex.Message}");
            single?.TryStart(onArgumentsReceived); // re-acquire so this instance keeps working
            return false;
        }

        Log.Info("Elevated instance launched; shutting the current instance down.");
        Application.Current.Shutdown(0);
        return true;
    }
}
