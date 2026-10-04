using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ModernScreenShot.Core.Translation;

/// <summary>
/// Runs translation in a llama.cpp child process.
///
/// The model is Hy-MT2-1.8B (Apache-2.0), a translation model rather than a general LLM: it is
/// prompted with a plain translation instruction and answers with the translation alone. llama.cpp
/// applies the model's own chat template, so this class never touches special tokens — it speaks the
/// OpenAI-compatible HTTP API instead, which is also exactly what the quality measurements were
/// taken against.
///
/// The server is started on the first translation and kept alive for the session: loading the model
/// costs a few seconds, after which a sentence takes roughly 0.7 s on CPU. Every failure surfaces as
/// a <see cref="TranslationException"/> with a message the UI can show, never a hang.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private const int StartupTimeoutSeconds = 180;

    /// <summary>
    /// The loaded model costs roughly 2 GB of RAM, which is far too much for a tray utility to hold
    /// for the whole session. The server is therefore stopped once translation has been idle for a
    /// while and started again on the next request (a few seconds of model load).
    /// </summary>
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Timer _idleTimer;

    private Process? _process;
    private HttpClient? _http;
    private DateTime _lastUseUtc = DateTime.UtcNow;
    private bool _disposed;

    public TranslationService()
    {
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>True when the runtime and the model are both on disk.</summary>
    public bool IsAvailable => !_disposed && TranslationEngine.IsRuntimeInstalled && TranslationModelDownloader.IsModelInstalled();

    /// <summary>Raw llama.cpp output, surfaced for the application log.</summary>
    public event Action<string>? Diagnostic;

    /// <summary>Stops the child process when it has not been used for <see cref="IdleShutdown"/>.</summary>
    private void StopIfIdle()
    {
        if (_disposed) return;
        bool running;
        lock (_sync) running = _process is { HasExited: false };
        if (!running || DateTime.UtcNow - _lastUseUtc < IdleShutdown) return;

        Diagnostic?.Invoke($"idle for {IdleShutdown.TotalMinutes:0} min; stopping the engine to release memory");
        _ = StopProcessAsync();
    }

    // ---------------------------------------------------------------- public API

    /// <summary>
    /// Translates <paramref name="text"/> into <paramref name="toCode"/>. Throws
    /// <see cref="TranslationException"/> on failure.
    /// </summary>
    public async Task<TranslationOutcome> TranslateAsync(
        string text, string toCode, CancellationToken ct = default)
    {
        if (_disposed) throw new TranslationException("the translation service has been disposed");
        if (!TranslationEngine.IsRuntimeInstalled)
            throw new TranslationException($"the llama.cpp runtime is missing under {TranslationEngine.RuntimeDir}");
        if (!TranslationModelDownloader.IsModelInstalled())
            throw new TranslationException("the translation model has not been downloaded yet");
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationOutcome(string.Empty, string.Empty, toCode, 0);

        await EnsureStartedAsync(ct).ConfigureAwait(false);

        string prompt =
            $"Translate the following text into {TranslationLanguages.EnglishNameOf(toCode)}. "
            + "Note: only output the translated result, no extra explanation:\n\n" + text;

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = prompt } },
            // Greedy decoding: translation should be reproducible, and Tencent's sampling defaults
            // are aimed at chat. Measured equal or better than sampling on the reference paragraph.
            ["temperature"] = 0.0,
            ["max_tokens"] = 4096,
            ["stream"] = false,
        });

        HttpClient http;
        lock (_sync)
        {
            http = _http ?? throw new TranslationException("the translation engine is not running");
        }

        var stopwatch = Stopwatch.StartNew();
        _lastUseUtc = DateTime.UtcNow;
        string payload;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("v1/chat/completions", content, ct).ConfigureAwait(false);
            payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException($"the translation engine returned {(int)response.StatusCode}: {Trim(payload)}");
            }
        }
        catch (Exception ex) when (ex is not TranslationException and not OperationCanceledException)
        {
            // A dead child must not be reused; the next call starts a fresh one.
            await StopProcessAsync().ConfigureAwait(false);
            throw new TranslationException($"the translation engine failed: {ex.Message}");
        }
        stopwatch.Stop();

        string translated = ExtractContent(payload);
        return new TranslationOutcome(text, translated.Trim(), toCode, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Warms the engine up (starts the process and loads the model). Callers use it to report
    /// "engine unavailable" early without running a translation.
    /// </summary>
    public async Task<string> PingAsync(CancellationToken ct = default)
    {
        if (!TranslationEngine.IsRuntimeInstalled)
            throw new TranslationException($"the llama.cpp runtime is missing under {TranslationEngine.RuntimeDir}");
        if (!TranslationModelDownloader.IsModelInstalled())
            throw new TranslationException("the translation model has not been downloaded yet");

        await EnsureStartedAsync(ct).ConfigureAwait(false);
        string version = TranslationEngine.ReadRuntimeVersion();
        return string.IsNullOrEmpty(version) ? "llama.cpp" : version;
    }

    /// <summary>Stops the child so the next translation reloads (used after the model changes).</summary>
    public Task RestartAsync() => StopProcessAsync();

    // ---------------------------------------------------------------- protocol helpers

    private static string ExtractContent(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic message below.
        }
        throw new TranslationException("the translation engine returned an unexpected response: " + Trim(payload));
    }

    private static string Trim(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";

    // ---------------------------------------------------------------- process lifecycle

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } && _http is not null) return;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _http is not null) return;
            await StopProcessAsync().ConfigureAwait(false);
            await StartProcessAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StartProcessAsync(CancellationToken ct)
    {
        int port = FreeLoopbackPort();
        // Half the logical cores keeps the desktop responsive while the model runs; llama.cpp scales
        // poorly past that for a model this small.
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 4, 16);

        var psi = new ProcessStartInfo
        {
            FileName = TranslationEngine.ServerExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = TranslationEngine.RuntimeDir,
        };
        foreach (string argument in new[]
                 {
                     "-m", TranslationModelDownloader.ModelPath,
                     "-c", "4096",
                     "-t", threads.ToString(),
                     "-ngl", "0",                 // CPU only: no GPU is assumed
                     "--host", "127.0.0.1",
                     "--port", port.ToString(),
                     "--no-webui",
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        _ = Task.Run(() => DrainAsync(process.StandardError));
        _ = Task.Run(() => DrainAsync(process.StandardOutput));

        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(10) };
        lock (_sync)
        {
            _process = process;
            _http = http;
        }

        // The server answers /health only once the weights are loaded; that is the readiness signal.
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw new TranslationException($"the translation engine exited during start-up (code {process.ExitCode})");
            }
            try
            {
                using var response = await http.GetAsync("health", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode &&
                    (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Contains("\"ok\"", StringComparison.Ordinal))
                {
                    Diagnostic?.Invoke($"llama.cpp ready on port {port} ({threads} threads)");
                    return;
                }
            }
            catch (Exception)
            {
                // Not listening yet (or still loading) — keep waiting until the deadline.
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        await StopProcessAsync().ConfigureAwait(false);
        throw new TranslationException($"the translation engine did not become ready within {StartupTimeoutSeconds} s");
    }

    /// <summary>Reserves an ephemeral loopback port by binding and immediately releasing it.</summary>
    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0) Diagnostic?.Invoke(line);
            }
        }
        catch (Exception)
        {
            // The pipe closing is the normal end of this loop.
        }
    }

    private async Task StopProcessAsync()
    {
        Process? process;
        HttpClient? http;
        lock (_sync)
        {
            process = _process;
            http = _http;
            _process = null;
            _http = null;
        }

        http?.Dispose();
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Nothing further we can do about a process that refuses to die here.
        }
        finally
        {
            try { process.Dispose(); } catch (Exception) { /* already torn down */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        try { StopProcessAsync().GetAwaiter().GetResult(); }
        catch (Exception) { /* shutdown must never throw */ }
        _startGate.Dispose();
    }
}

/// <summary>A translation failure with a message intended for the user.</summary>
public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}
