using System.Diagnostics;
using System.Text;
using Dictum.Core;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Dictum.Llm;

/// <summary>
/// A loaded GGUF cleanup model run in-process by llama.cpp through LLamaSharp (SPEC §5). Greedy decoding, the
/// model's own chat template (read from the GGUF metadata by llama.cpp), stop at end-of-turn, cancellable
/// between tokens. Calls are serialised; one context keeps the prompt prefix evaluated, and each call's own tokens are
/// removed from it afterwards, so nothing leaks between dictations.
/// </summary>
public sealed class LocalLlmEngine : ILocalLlmEngine
{
    private const int ContextSize = 4096;
    private static readonly object BackendGate = new();
    private static bool _backendConfigured;
    private static string _loadedLibrary = "";
    private static ILog _nativeLog = NullLog.Instance; // the latest loader's log; the native callback is set once per process

    private readonly LLamaWeights _weights;
    private readonly CleanupSession _session;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private int _disposed;
    private double _promptRate;
    private double _generationRate;

    private LocalLlmEngine(LLamaWeights weights, ModelParams parameters, string path, TimeSpan loadTime)
    {
        _weights = weights;
        _session = new CleanupSession(weights, parameters);
        ModelPath = path;
        LoadTime = loadTime;
    }

    public string ModelPath { get; }
    public TimeSpan LoadTime { get; }

    /// <summary>"Vulkan", "CPU" or "not loaded", from the native library LLamaSharp actually loaded.</summary>
    public static string Backend
    {
        get
        {
            var lib = Volatile.Read(ref _loadedLibrary);
            if (lib.Length == 0) return "not loaded";
            return lib.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ? "Vulkan" : "CPU";
        }
    }

    /// <summary>Path of the loaded llama native library (for the log and --selftest).</summary>
    public static string LoadedLibrary => Volatile.Read(ref _loadedLibrary);

    /// <summary>
    /// Backend order Vulkan → CPU (SPEC §5), chosen once per process before the first model load: LLamaSharp
    /// tries the Vulkan build first when enabled and falls back automatically. llm.gpu = "cpu" disables Vulkan.
    /// </summary>
    public static void ConfigureBackend(string gpuSetting, ILog log)
    {
        lock (BackendGate)
        {
            if (_backendConfigured) return;
            _backendConfigured = true;
            NativeLibraryConfig.All
                .WithVulkan(gpuSetting != "cpu")
                .WithCuda(false)
                .WithAutoFallback(true)
                .WithLogCallback(ForwardNativeLog);
            log.Info($"Cleanup model backend order: {(gpuSetting == "cpu" ? "CPU" : "Vulkan -> CPU")} (llm.gpu = {gpuSetting}).");
        }
    }

    private static void ForwardNativeLog(LLamaLogLevel level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var log = Volatile.Read(ref _nativeLog);
        var text = message.TrimEnd();
        if (LoadedLibraryPath(text) is { } path) Volatile.Write(ref _loadedLibrary, path);
        var important = text.Contains("loaded", StringComparison.OrdinalIgnoreCase) && text.Contains(".dll", StringComparison.OrdinalIgnoreCase)
            || text.Contains("offloaded", StringComparison.Ordinal)
            || text.Contains("ggml_vulkan: Found", StringComparison.Ordinal)
            || text.Contains("buffer size", StringComparison.Ordinal) && text.Contains("model", StringComparison.Ordinal);
        var ours = level == LLamaLogLevel.Error ? LogLevel.Warning : important ? LogLevel.Info : LogLevel.Debug;
        try
        {
            log.Write(ours, $"llama: {text}");
        }
        catch (Exception)
        {
            // This runs inside a native llama.cpp callback: an exception must never cross back into native code.
        }
    }

    /// <summary>The llama.dll path from LLamaSharp's "successfully loaded" log line, or null for other lines.</summary>
    internal static string? LoadedLibraryPath(string line)
    {
        // LLamaSharp logs: Successfully loaded '<path>\llama.dll' (the path may contain spaces).
        if (!line.StartsWith("Successfully loaded '", StringComparison.Ordinal)) return null;
        var start = line.IndexOf('\'') + 1;
        var end = line.IndexOf('\'', start);
        if (end <= start) return null;
        var path = line[start..end];
        return path.EndsWith("llama.dll", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    public static LocalLlmEngine Load(string path, string gpuSetting, ILog log)
    {
        Volatile.Write(ref _nativeLog, log);
        ConfigureBackend(gpuSetting, log);
        if (!File.Exists(path)) throw new FileNotFoundException("Cleanup model file not found.", path);
        var sw = Stopwatch.StartNew();
        var parameters = new ModelParams(path)
        {
            ContextSize = ContextSize,
            GpuLayerCount = gpuSetting == "cpu" ? 0 : 999, // all layers on the GPU when the Vulkan build loaded
        };
        var weights = NativeLoadGate.Run(() => LLamaWeights.LoadFromFile(parameters));
        sw.Stop();
        log.Info($"Cleanup model {Path.GetFileName(path)} loaded in {sw.Elapsed.TotalSeconds:0.0} s on {Backend} ({LoadedLibrary}).");
        return new LocalLlmEngine(weights, parameters, path, sw.Elapsed);
    }

    /// <summary>
    /// Evaluates the default prefix into the KV cache and runs one short cleanup, right after loading: the prefix is paid
    /// for here, not by the first dictation; on a GPU the driver compiles its shaders now (observed: the first-ever Vulkan
    /// run took about 17 s, later runs about 0.4 s); and the speed estimate gets both its figures. The input is the first
    /// few-shot example's raw text, whose cleaned reply runs past the 4 tokens a generation rate needs (the earlier "ok"
    /// warm-up stopped short of that, so the long-dictation skip stayed off until a dictation finished).
    /// </summary>
    /// <param name="systemPrompt">The prompt dictations will use (with any grammar-assist and dictionary lines), so its
    /// prefix is the one left in the cache; the built-in prompt when not given.</param>
    public async Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await CompleteAsync(systemPrompt ?? CleanupPrompt.Default, CleanupExamples.Pairs, CleanupExamples.Pairs[0].Raw, WarmUpTokens, cancellationToken)
            .ConfigureAwait(false);
        return sw.Elapsed;
    }

    /// <summary>Reply tokens the warm-up may generate: enough for a generation rate, few enough to stay short on a CPU.</summary>
    internal const int WarmUpTokens = 16;

    public int CountTokens(string text) => _weights.Tokenize(text, false, false, Encoding.UTF8).Length;

    /// <summary>Tokens per second for prompt evaluation and for generation, from the latest calls (0 = not measured yet).</summary>
    public (double Prompt, double Generation) Rates => (Volatile.Read(ref _promptRate), Volatile.Read(ref _generationRate));

    /// <summary>
    /// Estimated time for a dictation of <paramref name="dictationTokens"/> tokens: its prompt plus a reply of about
    /// the same length (cleanup removes a little), at the measured rates. Null until both rates are known.
    /// </summary>
    public TimeSpan? Predict(int dictationTokens)
    {
        var (prompt, generation) = Rates;
        if (prompt <= 0 || generation <= 0) return null;
        return TimeSpan.FromSeconds((dictationTokens + 10) / prompt + (dictationTokens + 8) / generation);
    }

    /// <summary>
    /// Runs one cleanup: system prompt, few-shot pairs, then the raw transcript, through the model's chat template.
    /// The prefix up to the transcript stays evaluated in the KV cache between calls (<see cref="CleanupSession"/>).
    /// </summary>
    public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, int maxTokens, CancellationToken cancellationToken) =>
        CompleteCoreAsync(systemPrompt, examples, raw, null, maxTokens, cancellationToken);

    /// <summary>
    /// A hands-free part: <paramref name="carried"/> (the last part's closing words) goes after the cached prefix as a system
    /// turn of its own (<see cref="CleanupSession.Render"/>), so the prefix stays evaluated and only the part's tokens are new.
    /// </summary>
    public Task<GenerationResult> CompleteAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, string carried, int maxTokens,
        CancellationToken cancellationToken) =>
        CompleteCoreAsync(systemPrompt, examples, raw, carried, maxTokens, cancellationToken);

    private async Task<GenerationResult> CompleteCoreAsync(string systemPrompt, IReadOnlyList<(string Raw, string Clean)> examples, string raw, string? carried, int maxTokens,
        CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _session.GenerateAsync(systemPrompt, examples, raw, carried, maxTokens, cancellationToken).ConfigureAwait(false);
            var prefix = _session.LastPrefix;
            if (prefix.Tokens >= 32 && prefix.Time > TimeSpan.Zero && Volatile.Read(ref _promptRate) <= 0) Volatile.Write(ref _promptRate, prefix.Tokens / prefix.Time.TotalSeconds);
            Record(result);
            return result;
        }
        catch (OperationCanceledException) when (_session.Cancelled is { } partial)
        {
            Record(partial); // a call stopped at its deadline still measured the speed
            throw;
        }
        finally
        {
            _busy.Release();
        }
    }

    private void Record(GenerationResult result)
    {
        if (result.PromptTokens >= 16 && result.PromptTime > TimeSpan.Zero) Volatile.Write(ref _promptRate, result.PromptTokens / result.PromptTime.TotalSeconds);
        if (result.GeneratedTokens >= 4 && result.GenerationTime > TimeSpan.Zero) Volatile.Write(ref _generationRate, result.GeneratedTokens / result.GenerationTime.TotalSeconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _busy.Wait();
        try
        {
            _session.Dispose();
            _weights.Dispose();
        }
        finally
        {
            _busy.Release();
            _busy.Dispose();
        }
    }
}
