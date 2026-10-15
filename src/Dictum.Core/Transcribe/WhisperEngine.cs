using System.Diagnostics;
using System.Text.RegularExpressions;
using Dictum.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Dictum.Transcribe;

public interface ITranscriber
{
    Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken);
}

/// <summary>How whisper decodes (SPEC §16.2, §16.5): the initial prompt (the personal dictionary) and greedy or beam search.</summary>
/// <param name="Prompt">whisper's initial prompt, or null for none.</param>
/// <param name="BeamSize">0 = greedy; 1 or more = beam search with that many beams (whisper.beam_size).</param>
public sealed record DecodeOptions(string? Prompt, int BeamSize)
{
    public static readonly DecodeOptions Default = new(null, 0);

    /// <summary>Beams used when calibration tries beam search: Whisper.net's own default (its XML docs: "If not configured, 5 beam sizes are used").</summary>
    public const int CalibrationBeamSize = 5;

    public string Describe() => BeamSize > 0 ? $"beam search ({BeamSize})" : "greedy";
}

/// <summary>A loaded speech model whose decoding can be changed without reloading it (dictionary edits, calibration).</summary>
public interface ISpeechEngine : ITranscriber, IDisposable
{
    string ModelPath { get; }

    DecodeOptions Decode { get; }

    /// <summary>Rebuilds the decoder with these options; waits for a transcription in progress first.</summary>
    void Reconfigure(DecodeOptions decode);
}

/// <summary>
/// whisper.cpp via Whisper.net. The native runtime is chosen once per process (Vulkan then CPU by default;
/// the Cuda packages are not shipped because they need an installed CUDA Toolkit). The model is loaded once per model
/// load; its processor is rebuilt only when the decoding options change; calls are serialised because a processor is
/// not re-entrant.
/// </summary>
public sealed partial class WhisperEngine : ISpeechEngine
{
    private static readonly object RuntimeGate = new();
    private static int _vulkanDevices = -1;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> Devices = new(); // index → integrated
    private static bool _runtimeConfigured;
    private static IDisposable? _nativeLogSubscription;

    private readonly SemaphoreSlim _busy = new(1, 1);
    private int _disposed;
    private readonly WhisperFactory _factory;
    private readonly string _language;
    private readonly int _threads;
    private WhisperProcessor _processor;

    private WhisperEngine(WhisperFactory factory, string language, int threads, DecodeOptions decode, string modelPath, TimeSpan loadTime)
    {
        _factory = factory;
        _language = language;
        _threads = threads;
        Decode = decode;
        _processor = Build(factory, language, threads, decode);
        ModelPath = modelPath;
        LoadTime = loadTime;
    }

    public DecodeOptions Decode { get; private set; }

    /// <summary>
    /// Builder methods read in Whisper.net 1.9.1's source (WhisperProcessorBuilder.cs, BeamSearchSamplingStrategyBuilder.cs
    /// and WhisperProcessorOptions.cs at tag 1.9.1): WithPrompt sets whisper's initial prompt; without a sampling strategy
    /// the options default to greedy; WithBeamSearchSamplingStrategy(b => b.WithBeamSize(n)) switches to beam search.
    /// </summary>
    private static WhisperProcessor Build(WhisperFactory factory, string language, int threads, DecodeOptions decode)
    {
        var builder = factory.CreateBuilder().WithLanguage(language);
        if (threads > 0) builder = builder.WithThreads(threads);
        if (!string.IsNullOrWhiteSpace(decode.Prompt)) builder = builder.WithPrompt(decode.Prompt);
        if (decode.BeamSize > 0) builder = builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(decode.BeamSize));
        return builder.Build();
    }

    public void Reconfigure(DecodeOptions decode)
    {
        _busy.Wait();
        try
        {
            if (decode == Decode) return;
            var previous = _processor;
            _processor = NativeLoadGate.Run(() => Build(_factory, _language, _threads, decode));
            Decode = decode;
            Task.Run(() => previous.DisposeAsync().AsTask()).GetAwaiter().GetResult(); // see Dispose for why on the pool
        }
        finally
        {
            _busy.Release();
        }
    }

    public string ModelPath { get; }
    public TimeSpan LoadTime { get; private set; }

    /// <summary>Name of the native runtime that loaded ("Vulkan", "CPU"...), or "not loaded".</summary>
    public static string LoadedRuntime => RuntimeOptions.LoadedLibrary switch
    {
        null => "not loaded",
        RuntimeLibrary.Cpu => "CPU",
        RuntimeLibrary.CpuNoAvx => "CPU (no AVX)",
        RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => "CUDA",
        var other => other.ToString()!,
    };

    /// <summary>
    /// Sets the runtime order for this process. Must run before the first factory is created; later calls are
    /// no-ops because Whisper.net keeps the first native library it loads.
    /// </summary>
    public static void ConfigureRuntime(string gpuSetting, ILog log)
    {
        lock (RuntimeGate)
        {
            if (_runtimeConfigured) return;
            _runtimeConfigured = true;
            // Only the runtimes this app ships (SPEC §2). No Cuda: those packages need an installed CUDA Toolkit.
            RuntimeOptions.RuntimeLibraryOrder = gpuSetting == "cpu"
                ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                : [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            log.Info($"Whisper runtime order: {string.Join(" -> ", RuntimeOptions.RuntimeLibraryOrder)} (whisper.gpu = {gpuSetting}).");
            _nativeLogSubscription = LogProvider.AddLogger((level, message) => ForwardNativeLog(log, level, message));
        }
    }

    private static void ForwardNativeLog(ILog log, WhisperLogLevel level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = message.TrimEnd();
        if (ParseVulkanDeviceCount(text) is int count) Volatile.Write(ref _vulkanDevices, count);
        if (ParseDevice(text) is { } device) Devices[device.Index] = device.Integrated;
        // Device/backend lines are what tells the user whether the GPU is really used; keep them at info.
        var important = text.Contains("ggml_vulkan", StringComparison.Ordinal)
            || text.Contains("Successfully loaded", StringComparison.Ordinal)
            || text.Contains("total size", StringComparison.Ordinal)
            || text.Contains("backends", StringComparison.Ordinal)
            || level == WhisperLogLevel.Error;
        log.Write(level == WhisperLogLevel.Error ? LogLevel.Warning : important ? LogLevel.Info : LogLevel.Debug, $"whisper: {text}");
    }

    /// <summary>
    /// Loads the native runtime without a model and reports which one loaded. Used on first run and by --selftest to
    /// recommend medium.en (a discrete GPU, <see cref="GpuMachine"/>) or base.en (CPU or integrated graphics only).
    /// </summary>
    public static string ProbeRuntime(string gpuSetting, ILog log)
    {
        ConfigureRuntime(gpuSetting, log);
        try
        {
            var info = WhisperFactory.GetRuntimeInfo();
            log.Debug($"Whisper system info: {info}");
        }
        catch (Exception ex)
        {
            log.Warn($"Whisper runtime probe failed: {ex.GetType().Name}: {ex.Message}");
        }
        return LoadedRuntime;
    }

    /// <summary>
    /// True when the Vulkan runtime loaded and did not report zero devices (a Vulkan loader with no usable GPU leaves ggml on
    /// the CPU even inside the Vulkan runtime). Warm-ups use this: any Vulkan device compiles its shaders on first use.
    /// </summary>
    public static bool VulkanActive => RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan && Volatile.Read(ref _vulkanDevices) != 0;

    /// <summary>
    /// Whether the recommendations and the Mode presets treat this PC as a GPU machine: Vulkan is in use and
    /// <see cref="IsGpuMachine"/> holds for the devices it described.
    /// </summary>
    public static bool GpuMachine => VulkanActive && IsGpuMachine(VulkanDevices, Devices.Count(d => !d.Value), Devices.Count(d => d.Value));

    /// <summary>
    /// The rule (fix batch, 15/10/2026): a discrete GPU makes a GPU machine; integrated graphics alone count as CPU, because
    /// every GPU figure behind the recommendations was measured on a discrete card (a Quadro RTX 4000) and none on an iGPU.
    /// A Vulkan runtime whose devices were never described keeps the earlier rule (any device counts).
    /// </summary>
    internal static bool IsGpuMachine(int devicesFound, int discrete, int integrated) => devicesFound != 0 && (discrete > 0 || integrated == 0);

    /// <summary>Devices reported by ggml_vulkan, or -1 if it has not reported.</summary>
    public static int VulkanDevices => Volatile.Read(ref _vulkanDevices);

    /// <summary>"2 (1 discrete, 1 integrated)" for the log and --selftest.</summary>
    public static string DeviceSummary =>
        $"{VulkanDevices} ({Devices.Count(d => !d.Value)} discrete, {Devices.Count(d => d.Value)} integrated)";

    [GeneratedRegex(@"ggml_vulkan: Found (\d+) Vulkan devices?")]
    private static partial Regex VulkanDevicesLine();

    /// <summary>Reads N from whisper.cpp's "ggml_vulkan: Found N Vulkan devices:" log line.</summary>
    internal static int? ParseVulkanDeviceCount(string line)
    {
        var m = VulkanDevicesLine().Match(line);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"ggml_vulkan: (\d+) = [^|]*\| uma: ([01])\b")]
    private static partial Regex VulkanDeviceLine();

    /// <summary>
    /// Reads a device line such as "ggml_vulkan: 1 = Intel(R) UHD Graphics P630 (Intel Corporation) | uma: 1 | fp16: 1 | …"
    /// (format read from this PC's own log, 15/10/2026): the device index and whether it shares system memory (uma: 1, an
    /// integrated GPU; the Quadro's line says uma: 0). Null for other lines.
    /// </summary>
    internal static (int Index, bool Integrated)? ParseDevice(string line)
    {
        var m = VulkanDeviceLine().Match(line);
        return m.Success ? (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value == "1") : null;
    }

    public static WhisperEngine Load(string modelPath, string language, int threads, string gpuSetting, ILog log, DecodeOptions? decode = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper model file not found.", modelPath);
        var sw = Stopwatch.StartNew();
        var engine = NativeLoadGate.Run(() =>
        {
            ConfigureRuntime(gpuSetting, log);
            var factory = WhisperFactory.FromPath(modelPath);
            return new WhisperEngine(factory, language, threads, decode ?? DecodeOptions.Default, modelPath, TimeSpan.Zero);
        });
        sw.Stop();
        engine.LoadTime = sw.Elapsed;
        log.Info($"Whisper model {Path.GetFileName(modelPath)} loaded in {sw.Elapsed.TotalSeconds:0.0} s on runtime {LoadedRuntime} ({engine.Decode.Describe()}{(engine.Decode.Prompt is null ? "" : ", dictionary prompt")}).");
        return engine;
    }

    public static string SystemInfo()
    {
        try
        {
            return WhisperFactory.GetRuntimeInfo() ?? "unavailable";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var segments = new List<string>();
            await foreach (var segment in _processor.ProcessAsync(samples16kMono, cancellationToken).ConfigureAwait(false))
            {
                segments.Add(segment.Text);
            }
            return TranscriptText.Normalise(segments);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// Waits for any native run to finish before freeing it. A cancelled transcription returns at once while
    /// whisper.cpp keeps computing until it next polls its abort callback (observed: seconds on CPU), and
    /// WhisperProcessor.Dispose throws "Cannot dispose while processing" in that window; DisposeAsync waits instead.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _busy.Wait();
        try
        {
            // On the thread pool: Whisper.net's DisposeAsync awaits without ConfigureAwait(false), and on the UI
            // thread after app.Run has returned its continuation would be posted to a dispatcher that no longer runs.
            Task.Run(() => _processor.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            _factory.Dispose();
        }
        finally
        {
            _busy.Release();
            _busy.Dispose();
        }
    }
}
