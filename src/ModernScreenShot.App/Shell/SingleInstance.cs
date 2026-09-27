using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using ModernScreenShot.App.Services;

namespace ModernScreenShot.App.Shell;

/// <summary>
/// Named-mutex single instance guard. The first instance owns <see cref="MutexName"/> and listens
/// on a named pipe; later instances serialize their command line over that pipe and exit. Received
/// arguments are marshalled to the UI thread before being executed.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\Modern-ScreenShot-SingleInstance";
    private const string PipeName = "Modern-ScreenShot-Arguments";
    private const int ForwardTimeoutMs = 3000;

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Tries to become the only running instance. Returns false when another instance owns the
    /// mutex; the caller should then forward its arguments via <see cref="TryForward"/> and exit.
    /// </summary>
    public bool TryStart(Action<string[]> onArgumentsReceived)
    {
        if (_disposed) return false;
        if (_mutex is not null) return true; // already started

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var listener = new Thread(() => Listen(onArgumentsReceived, token))
        {
            IsBackground = true,
            Name = "SingleInstance.PipeListener",
        };
        listener.Start();
        Log.Info($"Single-instance mutex '{MutexName}' acquired; listening on pipe '{PipeName}'.");
        return true;
    }

    /// <summary>Runs in the SECOND instance: sends its command line to the first one.</summary>
    public static bool TryForward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect(ForwardTimeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(args));
            Log.Info($"Arguments forwarded to the running instance: [{string.Join(' ', args)}]");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Forwarding arguments to the running instance failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Accept loop; one connection per argument forwarding. Runs on a background thread.</summary>
    private static void Listen(Action<string[]> onArgumentsReceived, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                if (token.IsCancellationRequested) break;

                string? line;
                using (var reader = new StreamReader(server))
                    line = reader.ReadLine();
                if (string.IsNullOrEmpty(line)) continue;

                var args = JsonSerializer.Deserialize<string[]>(line) ?? [];
                Log.Info($"Forwarded arguments received: [{string.Join(' ', args)}]");
                // The pipe loop runs on a worker thread; captures must run on the UI thread.
                Application.Current?.Dispatcher.BeginInvoke(() => onArgumentsReceived(args));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Single-instance pipe error: {ex.Message}");
            }
        }
        Log.Info("Single-instance listener stopped.");
    }

    /// <summary>Restricts the pipe to the current user so other accounts cannot inject arguments.</summary>
    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel(); // listener thread observes this; the CTS itself is left to the GC
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex(); // must run on the acquiring (UI) thread; may already be released
            }
            catch (ApplicationException)
            {
                // Not owned by this thread anymore - nothing to release.
            }
            _mutex.Dispose();
            _mutex = null;
        }
        Log.Info("Single-instance guard released.");
    }
}
