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
    private readonly SpeedEstimate _speed = new();
    private int _disposed;

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
    /// tries the Vulkan build first when enabled and falls back automatically. llm.gpu = "cpu" disables Vulkan and "vulkan"
    /// keeps it; "auto" keeps it too unless the only GPUs are integrated graphics (<see cref="UseVulkan"/>), which it learns from
    /// the devices whisper's runtime found (<see cref="Transcribe.WhisperEngine.VulkanDevicesForCleanup"/>, about 0.3 s when that
    /// runtime is not loaded yet).
    /// </summary>
    public static void ConfigureBackend(string gpuSetting, ILog log)
    {
        lock (BackendGate)
        {
            if (_backendConfigured) return;
            _backendConfigured = true;
            // simplification: the devices come only from whisper's runtime. Ceiling: with whisper.gpu = cpu (Try on the CPU writes
            // it, and a start after the GPU ended Dictum on the speech model sets the same CPU order), or before any speech load is
            // asked for, they are never described, so auto keeps Vulkan on a PC whose only GPU is integrated graphics. Trigger to
            // retire: a report of a slow clean-up on such a PC with the speech model on the CPU; then enumerate Vulkan devices
            // without loading whisper's runtime.
            var vulkan = UseVulkan(gpuSetting, gpuSetting == "auto" ? Transcribe.WhisperEngine.VulkanDevicesForCleanup(log) : null);
            NativeLibraryConfig.All
                .WithVulkan(vulkan)
                .WithCuda(false)
                .WithAutoFallback(true)
                .WithLogCallback(ForwardNativeLog);
            var why = vulkan || gpuSetting == "cpu" ? "" : "; the only GPUs Vulkan found are integrated graphics, where the CPU is faster";
            log.Info($"Cleanup model backend order: {(vulkan ? "Vulkan -> CPU" : "CPU")} (llm.gpu = {gpuSetting}{why}).");
        }
    }

    /// <summary>
    /// Whether the clean-up model may use the Vulkan build: llm.gpu = "cpu" never, "vulkan" always, and "auto" unless every Vulkan
    /// device found is integrated graphics (uma: 1, <see cref="Transcribe.WhisperEngine.ParseDevice"/>). Measured 16/10/2026 on the
    /// Intel UHD P630 alone (GGML_VK_VISIBLE_DEVICES=1), qwen2.5-1.5b: 4.3 s per clean-up on Vulkan against 2.5-3.0 s on the CPU
    /// build, and a warm-up of 12-21 s against 3-5 s. A discrete GPU keeps Vulkan, and so do devices never described (null) or
    /// none found, as before.
    /// </summary>
    // simplification: one integrated GPU measured (the UHD P630 beside a 6-core Xeon) decides for every integrated GPU. Ceiling: a
    // newer integrated GPU (Iris Xe, Arc, Radeon graphics) may run the clean-up faster on Vulkan than on its CPU, and auto then
    // leaves that speed unused. Trigger to retire: a measurement on such a GPU where Vulkan wins; then decide by measured speed.
    internal static bool UseVulkan(string gpuSetting, (int Found, int Discrete, int Integrated)? devices) => gpuSetting switch
    {
        "cpu" => false,
        "auto" => devices is not { Found: > 0, Discrete: 0, Integrated: > 0 },
        _ => true,
    };

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
        var (gpuLayers, opOffload) = Parameters(gpuSetting);
        var parameters = new ModelParams(path) { ContextSize = ContextSize, GpuLayerCount = gpuLayers, OpOffload = opOffload };
        var weights = NativeLoadGate.Run(() => LLamaWeights.LoadFromFile(parameters));
        sw.Stop();
        log.Info($"Cleanup model {Path.GetFileName(path)} loaded in {sw.Elapsed.TotalSeconds:0.0} s on {Backend}{(gpuSetting == "cpu" && Backend == "Vulkan" ? ", on the CPU" : "")} ({LoadedLibrary}).");
        return new LocalLlmEngine(weights, parameters, path, sw.Elapsed);
    }

    /// <summary>
    /// Where the model's work goes for this llm.gpu value: all layers on the GPU when the Vulkan build loaded (999) and llama.cpp's
    /// own op offload (null); for "cpu" inside the Vulkan build (a changed setting, or the retry after the GPU failed) no layers
    /// and op offload off, so no work goes to the GPU: with llama.cpp's default op offload the retry still asked Vulkan for prompt
    /// buffers (DECISIONS, 17/10/2026). Two values, not LLamaSharp's ModelParams: constructing that loads llama.cpp's native
    /// library (seen in the test host, 17/10/2026: the Vulkan build's DLLs, before any model).
    /// </summary>
    internal static (int GpuLayerCount, bool? OpOffload) Parameters(string gpuSetting) => gpuSetting == "cpu" ? (0, false) : (999, null);

    /// <summary>
    /// Evaluates the default prefix into the KV cache and runs one short cleanup, right after loading: the prefix is paid
    /// for here, not by the first dictation; on a GPU the driver compiles its shaders now (observed: the first-ever Vulkan
    /// run took about 17 s, later runs about 0.4 s); and the speed estimate gets both its figures. The input is the first
    /// few-shot example's raw text, whose cleaned reply runs past the 4 tokens a generation rate needs (the earlier "ok"
    /// warm-up stopped short of that, so the long-dictation skip stayed off until a dictation finished). A model's first
    /// warm-up runs twice and the estimate keeps the second run's figures (<see cref="SpeedEstimate.WarmUpAsync"/>).
    /// </summary>
    /// <param name="systemPrompt">The prompt dictations will use (with any grammar-assist and dictionary lines), so its
    /// prefix is the one left in the cache; the built-in prompt when not given.</param>
    public async Task<TimeSpan> WarmUpAsync(string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await _speed.WarmUpAsync(() => CompleteAsync(systemPrompt ?? CleanupPrompt.Default, CleanupExamples.Pairs, CleanupExamples.Pairs[0].Raw, WarmUpTokens,
            cancellationToken)).ConfigureAwait(false);
        return sw.Elapsed;
    }

    /// <summary>Reply tokens the warm-up may generate: enough for a generation rate, few enough to stay short on a CPU.</summary>
    internal const int WarmUpTokens = 16;

    public int CountTokens(string text) => _weights.Tokenize(text, false, false, Encoding.UTF8).Length;

    /// <summary>Tokens per second for prompt evaluation and for generation, from the latest calls (0 = not measured yet).</summary>
    public (double Prompt, double Generation) Rates => _speed.Rates;

    /// <inheritdoc cref="SpeedEstimate.Predict"/>
    public TimeSpan? Predict(int dictationTokens) => _speed.Predict(dictationTokens);

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
            var evaluations = _session.PrefixEvaluations;
            var result = await _session.GenerateAsync(systemPrompt, examples, raw, carried, maxTokens, cancellationToken).ConfigureAwait(false);
            if (_session.PrefixEvaluations != evaluations) _speed.RecordPrefix(_session.LastPrefix.Tokens, _session.LastPrefix.Time); // only a prefix this call evaluated
            _speed.Record(result);
            return result;
        }
        catch (OperationCanceledException) when (_session.Cancelled is { } partial)
        {
            _speed.Record(partial); // a call stopped at its deadline still measured the speed
            throw;
        }
        finally
        {
            _busy.Release();
        }
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

/// <summary>
/// The local model's speed behind the long-dictation skip (<see cref="TranscriptCleaner.SkipReason"/>): tokens per second for
/// reading the prompt and for writing the reply, from the warm-up and every call since (one stopped at its deadline included).
/// </summary>
internal sealed class SpeedEstimate
{
    private readonly object _gate = new();
    private double _prompt;
    private double _generation;
    private bool _warmed;

    /// <summary>Tokens per second for prompt evaluation and for generation (0 = not measured yet).</summary>
    public (double Prompt, double Generation) Rates
    {
        get
        {
            lock (_gate) return (_prompt, _generation);
        }
    }

    /// <summary>
    /// Runs a warm-up pass; a model's first warm-up runs it twice and keeps only the second pass's figures. The first pass pays
    /// for what happens once per model (the context and its cache, the weights read in from disk and, on a GPU, the shader
    /// compilation), which is not what a dictation costs. Observed 16/10/2026 on the Intel UHD P630 (Vulkan, qwen2.5-1.5b): the
    /// one warm-up measured 2-3 tokens/s reading and 3-6 tokens/s writing, so every dictation was skipped as "estimated 16-29 s"
    /// while the same calls took 1.9-4.3 s. Later warm-ups (a changed prompt) run once.
    /// </summary>
    public async Task WarmUpAsync(Func<Task> pass)
    {
        bool first;
        lock (_gate) first = !_warmed;
        if (first)
        {
            await pass().ConfigureAwait(false);
            lock (_gate) (_prompt, _generation) = (0, 0); // the first pass's figures are not a dictation's
        }
        await pass().ConfigureAwait(false);
        lock (_gate) _warmed = true;
    }

    /// <summary>A prefix the call evaluated: its speed stands in for the prompt's until a prompt of its own is measured.</summary>
    public void RecordPrefix(int tokens, TimeSpan time)
    {
        lock (_gate)
        {
            if (tokens >= 32 && time > TimeSpan.Zero && _prompt <= 0) _prompt = tokens / time.TotalSeconds;
        }
    }

    public void Record(GenerationResult result)
    {
        lock (_gate)
        {
            if (result.PromptTokens >= 16 && result.PromptTime > TimeSpan.Zero) _prompt = result.PromptTokens / result.PromptTime.TotalSeconds;
            if (result.GeneratedTokens >= 4 && result.GenerationTime > TimeSpan.Zero) _generation = result.GeneratedTokens / result.GenerationTime.TotalSeconds;
        }
    }

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
}
