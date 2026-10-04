using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Output;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App;

/// <summary>
/// Shared capture-path operation feedback (translate / OCR after-capture actions): a singleton
/// <see cref="ToastWindow"/> anchored to the cursor monitor's work area, plus the
/// <see cref="CancellationTokenSource"/> of the operation currently in flight.
///
/// Single-toast replace-in-place semantics: starting a new operation cancels the previous
/// operation's token and supersedes its toast. The delay gate (<see cref="OperationToastDelayed"/>)
/// keeps sub-second operations from flashing a toast at all.
/// </summary>
public partial class App
{
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _toastGateCts;
    private ToastWindow? _operationToast;

    /// <summary>
    /// Starts a new cancellable operation: cancels and disposes the previous operation's token
    /// source, then returns the fresh token to thread through OCR / download / translate calls.
    /// </summary>
    private CancellationToken BeginOperation()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _operationCts, next);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        return next.Token;
    }

    /// <summary>Cancels the operation in flight (wired to the toast's cancel button).</summary>
    private void CancelOperation() => _operationCts?.Cancel();

    /// <summary>
    /// Cancels a pending toast delay gate. The gate task disposes its own token source when it
    /// finishes, so the field can reference an already-disposed instance — that race is expected
    /// and means the gate already fired (or was already superseded), which needs no cancel.
    /// </summary>
    private void CancelToastGate()
    {
        try
        {
            _toastGateCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The gate already fired and was disposed by its own task.
        }
    }

    /// <summary>Releases the operation token source once the operation has fully ended.</summary>
    private void EndOperation()
    {
        var cts = Interlocked.Exchange(ref _operationCts, null);
        cts?.Dispose();
    }

    /// <summary>
    /// Shows or updates the singleton operation toast on the cursor monitor. Pass
    /// <paramref name="cancellable"/> during phases the user may abort (download / engine start /
    /// inference) to show the cancel text button wired to <see cref="CancelOperation"/>.
    /// </summary>
    private ToastWindow OperationToast(string text, bool cancellable = false)
    {
        // An immediate show supersedes any pending delay gate.
        CancelToastGate();
        var area = ToastWindow.WorkAreaFor(new MonitorService().GetCursorMonitor());
        _operationToast = ToastWindow.ShowOrUpdate(text, area,
            cancellable ? L.Get("Toast.Cancel") : null,
            cancellable ? CancelOperation : (Action?)null);
        return _operationToast;
    }

    /// <summary>
    /// Shows the toast only when the operation is still running after <paramref name="delayMs"/>.
    /// Hot-path operations (warm OCR at 60-400ms) finish before the gate opens and never flash a
    /// toast. The gate is tied to the operation token: cancelling the operation cancels the gate.
    /// </summary>
    private void OperationToastDelayed(string text, CancellationToken ct, int delayMs = 300, bool cancellable = false)
    {
        var gate = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(gate.Token, ct);
        CancelToastGate();
        _toastGateCts = gate;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, linked.Token);
                OperationToast(text, cancellable);
            }
            catch (OperationCanceledException)
            {
                // The operation finished (or was cancelled) before the gate opened — no toast.
            }
            finally
            {
                linked.Dispose();
                gate.Dispose();
                // The field must not keep referencing the disposed gate — later cancels would
                // otherwise trip over it. Only clear when it still points at this gate.
                if (ReferenceEquals(_toastGateCts, gate)) _toastGateCts = null;
            }
        });
    }

    /// <summary>
    /// Terminal feedback (failure / cancelled): kills any pending delay gate, swaps in the final
    /// message (null keeps the text the toast is already showing) and lets the toast auto-close
    /// via fade after ~3s.
    /// </summary>
    private void OperationToastComplete(string? finalText)
    {
        CancelToastGate();
        _operationToast?.Complete(finalText);
        _operationToast = null;
    }

    /// <summary>
    /// Immediate close without terminal text (success paths that hand over to a result window).
    /// Also kills any pending delay gate so a fast operation leaves zero UI trace.
    /// </summary>
    private void OperationToastClose()
    {
        CancelToastGate();
        _operationToast?.CloseInstant();
        _operationToast = null;
    }
}
