using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// Plain words for a model that could not load, shared by the speech and clean-up models, for the tray panel, Settings > About
/// and the load balloon. The Store certification investigation (16/10/2026) saw the engines' own words reach the user:
/// Whisper.net's "Failed to load native whisper library. Error: Cannot load the library on this platform using NativeLibrary.
/// PInvokeError: The operation completed successfully." and LLamaSharp's "The type initializer for 'LLama.Native.NativeApi'
/// threw an exception.", in Windows Sandbox without the Visual C++ runtime.
/// </summary>
public static class ModelFailure
{
    /// <summary>
    /// An engine's native library cannot load because the Visual C++ runtime it imports is not on this PC. PROPOSED copy
    /// (17/10/2026), awaiting the product owner's review: pinned word for word; do not reword.
    /// </summary>
    public const string RuntimeMissing = "The Microsoft Visual C++ runtime is missing; install it from Microsoft, then restart Dictum";

    /// <summary>
    /// The model failed on the GPU and was loaded again on the CPU, where it runs now. PROPOSED copy (17/10/2026), awaiting the
    /// product owner's review: pinned word for word; do not reword.
    /// </summary>
    public const string GpuFailed = "The graphics driver could not run the model; Dictum will use the CPU";

    /// <summary>
    /// The Visual C++ runtime DLLs the engines import: all 54 whisper.cpp and llama.cpp DLLs under the build's runtimes folder
    /// import MSVCP140 and VCRUNTIME140, 38 also VCRUNTIME140_1 and 14 (ggml's CPU and base builds) VCOMP140 (their import
    /// tables, read 17/10/2026; the other 9 DLLs there are Avalonia's and import none).
    /// </summary>
    internal static readonly string[] RuntimeDlls = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll"];

    /// <summary>
    /// True when an engine's native library could not load at all, so no retry in this process can help: Whisper.net's "Failed to
    /// load native whisper library" (a string in Whisper.net 1.9.1, read 17/10/2026), LLamaSharp's failed NativeApi type
    /// initialiser or its own native-library errors (strings in LLamaSharp 0.27.0, read 17/10/2026), or a DLL not found.
    /// </summary>
    public static bool IsNativeLibraryFailure(Exception error) => Chain(error).Any(e =>
        e is DllNotFoundException
        || e is TypeInitializationException { TypeName: { } type } && type.StartsWith("LLama.Native.", StringComparison.Ordinal)
        || e.Message.StartsWith("Failed to load native whisper library", StringComparison.Ordinal)
        || e.Message.StartsWith("The native library cannot be correctly loaded", StringComparison.Ordinal)
        || e.Message.StartsWith("Failed to load the native library", StringComparison.Ordinal));

    /// <summary>
    /// What the user is told about a failed load (<see cref="ModelManager.LastError"/> and the clean-up model's): plain words when
    /// the cause is known, else the engine's own message, as before.
    /// </summary>
    /// <param name="runtimePresent">Whether the Visual C++ runtime is on this PC; asked only after a native library failed to load.</param>
    public static string Describe(Exception error, Func<bool> runtimePresent) =>
        IsNativeLibraryFailure(error) && !runtimePresent() ? RuntimeMissing : error.GetBaseException().Message;

    /// <summary>
    /// Whether a load or warm-up that failed is worth one more try on the CPU: not when an engine library could not load (the CPU
    /// build needs the same runtime), the file is missing or fails its catalogue check, or the load was stopped.
    /// </summary>
    public static bool WorthCpuRetry(Exception error) =>
        !IsNativeLibraryFailure(error)
        && !Chain(error).Any(e => e is OperationCanceledException or FileNotFoundException or ModelIntegrityException or ObjectDisposedException);

    /// <summary>
    /// The Visual C++ runtime counts as present when each of its DLLs loads by name (System32, the program's folder, a packaged
    /// app's framework dependency) or sits in one of the program's native runtime folders (a copy beside the engines).
    /// </summary>
    public static bool VisualCppRuntimePresent() => VisualCppRuntimePresent(Loads, AppLocalFolders());

    internal static bool VisualCppRuntimePresent(Func<string, bool> loads, IReadOnlyCollection<string> appLocalFolders) =>
        RuntimeDlls.All(dll => loads(dll) || appLocalFolders.Any(folder => File.Exists(Path.Combine(folder, dll))));

    private static bool Loads(string dll)
    {
        if (!NativeLibrary.TryLoad(dll, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static string[] AppLocalFolders()
    {
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        try
        {
            return Directory.Exists(runtimes) ? Directory.GetDirectories(runtimes, "*", SearchOption.AllDirectories) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The exception and every exception inside it (inner exceptions, and each of an aggregate's).</summary>
    private static IEnumerable<Exception> Chain(Exception error)
    {
        var pending = new Stack<Exception>();
        pending.Push(error);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (e.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}

/// <summary>
/// Owns the loaded whisper engine. Loads happen in the background, one at a time; a recording that finishes while a
/// model is loading waits for it. Every load belongs to a generation: a newer choice (or quit) makes an older load stale,
/// and a stale load releases the engine it made instead of installing it, so the engine dictation uses, the one decoder
/// changes reach and the one quit releases are always the user's last choice. Swapping models disposes the old engine
/// only after any in-flight transcription. A load or warm-up that fails while the GPU is in use is tried once more on the
/// CPU (<see cref="Notice"/>), and an engine whose warm-up fails is released, never left loaded.
/// </summary>
public sealed class ModelManager : IDisposable
{
    private readonly ILog _log;
    private readonly ModelIntegrity? _integrity;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new(); // stops a warm-up at quit
    private readonly Func<string, WhisperSettings, DecodeOptions, ISpeechEngine> _loader;
    private readonly Func<bool> _gpu;
    private readonly Action<string> _configureRuntime;
    private readonly Func<bool> _visualCppRuntime;
    private Task<ISpeechEngine>? _loading; // the latest choice's load
    private Task _tail = Task.CompletedTask; // the last load started: the next one waits for it
    private long _generation; // bumped by every BeginLoad and by Dispose
    private ISpeechEngine? _engine;
    private bool _engineOnCpu; // the engine in use was loaded off the GPU: whisper.gpu = cpu, or its GPU load failed
    private DecodeOptions _decode = DecodeOptions.Default;

    /// <param name="loader">Tests load fakes; production loads whisper.cpp through <see cref="WhisperEngine.Load"/>.</param>
    /// <param name="warmUp">Whether the GPU is in use (tests decide; production: Vulkan is). On it, loads and decoder changes run a
    /// warm-up, and a load or warm-up that fails is tried once more on the CPU.</param>
    /// <param name="visualCppRuntime">Whether the Visual C++ runtime is on this PC (tests decide; production looks:
    /// <see cref="ModelFailure.VisualCppRuntimePresent()"/>).</param>
    public ModelManager(ILog log, ModelIntegrity? integrity = null, Func<string, WhisperSettings, DecodeOptions, ISpeechEngine>? loader = null,
        Func<bool>? warmUp = null, Func<bool>? visualCppRuntime = null)
    {
        _log = log;
        _integrity = integrity;
        _loader = loader ?? ((path, s, decode) => WhisperEngine.Load(path, s.Threads, s.Gpu, log, decode));
        // The production loader fixes whisper's runtime order when a load is asked for, not when it runs, so the clean-up
        // model's backend choice can read the GPUs that runtime finds (WhisperEngine.VulkanDevicesForCleanup).
        _configureRuntime = loader is null ? gpu => WhisperEngine.ConfigureRuntime(gpu, log) : _ => { };
        _gpu = warmUp ?? (() => WhisperEngine.VulkanActive);
        _visualCppRuntime = visualCppRuntime ?? ModelFailure.VisualCppRuntimePresent;
    }

    /// <summary>The latest load failed (the model it replaced, if any, stays in use); a same-path Ensure then retries it.</summary>
    public bool LoadFailed
    {
        get
        {
            lock (_gate) return _loading is { IsFaulted: true };
        }
    }

    /// <summary>
    /// The dictionary prompt and greedy/beam choice for every transcription from now on (SPEC §16.2, §16.5). The loaded
    /// model keeps running: only its decoder is rebuilt, in the background, after any transcription in progress.
    /// </summary>
    public void SetDecoding(DecodeOptions decode)
    {
        ISpeechEngine? engine;
        lock (_gate)
        {
            if (decode == _decode) return;
            _decode = decode;
            engine = _engine;
        }
        _log.Info($"Speech decoding: {decode.Describe()}, {(decode.Prompt is null ? "no dictionary prompt" : $"dictionary prompt of {decode.Prompt.Length} characters")}.");
        if (engine is not null) _ = Task.Run(() => Reconfigure(engine));
    }

    private void Reconfigure(ISpeechEngine engine)
    {
        try
        {
            DecodeOptions decode;
            bool onCpu;
            lock (_gate)
            {
                decode = _decode;
                onCpu = _engineOnCpu && ReferenceEquals(engine, _engine);
            }
            if (engine.Decode == decode) return;
            engine.Reconfigure(decode);
            // On Vulkan a new decoder shape compiles shaders on its first run (observed 15/10/2026: 26.1 s for small.en's
            // first transcription with a dictionary prompt, 0.47 s after): pay it now, not on the next dictation.
            if (!onCpu && _gpu()) WarmUp(engine, "after a decoding change");
        }
        catch (ObjectDisposedException)
        {
            // replaced by a newer model meanwhile; that one is built with the latest options
        }
        catch (Exception ex)
        {
            _log.Error($"Speech decoder rebuild failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One second of silence through the decoder, untimed by any dictation; quit stops it.</summary>
    private void WarmUp(ISpeechEngine engine, string when)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            engine.TranscribeAsync(new float[Dictum.Audio.MonoResampler.WhisperRate], _shutdown.Token).GetAwaiter().GetResult();
            _log.Info($"Whisper warm-up on {WhisperEngine.LoadedRuntime} {when} took {sw.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            _log.Info($"Whisper warm-up {when} stopped after {sw.ElapsedMilliseconds} ms: Dictum is quitting.");
        }
    }

    /// <summary>Raised (on a worker thread) when a load starts, succeeds or fails.</summary>
    public event Action? Changed;

    public string? ModelPath { get; private set; }
    public bool IsLoading => _loading is { IsCompleted: false };

    /// <summary>A model is loaded and no load is in progress. The hotkey is ignored otherwise (SPEC §8).</summary>
    public bool IsReady
    {
        get
        {
            lock (_gate) return _engine is not null && _loading is { IsCompleted: true };
        }
    }

    /// <summary>Why the latest load failed, in plain words when the cause is known (<see cref="ModelFailure.Describe"/>); null otherwise.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The latest load failed inside a GPU runtime with whisper.gpu not cpu, in a way the CPU-only runtime might get past (not a
    /// missing engine library, file or catalogue match): even once the retry on the CPU inside that runtime has failed, the next
    /// start can load the CPU runtime instead. The tray panel and About then offer "Try on the CPU".
    /// </summary>
    public bool CpuMightHelp { get; private set; }

    /// <summary>
    /// The latest load failed on the CPU inside a GPU runtime with whisper.gpu = cpu: only a restart loads the CPU runtime, and the
    /// problem line says so.
    /// </summary>
    public bool RestartForCpu { get; private set; }

    /// <summary>The engine in use runs on the CPU: whisper.gpu is cpu, or its load failed on the GPU and was made again on the CPU.</summary>
    public bool RunsOnCpu
    {
        get
        {
            lock (_gate) return _engine is not null && _engineOnCpu;
        }
    }

    /// <summary><see cref="ModelFailure.GpuFailed"/> while the engine in use runs on the CPU because its GPU load failed; null otherwise.</summary>
    public string? Notice { get; private set; }

    public void BeginLoad(string modelPath, WhisperSettings settings)
    {
        _configureRuntime(settings.Gpu);
        lock (_gate)
        {
            var generation = ++_generation;
            ModelPath = modelPath;
            LastError = null;
            CpuMightHelp = RestartForCpu = false;
            var before = _tail;
            var load = Task.Run(async () =>
            {
                await before.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false); // two models never load at once
                return Load(modelPath, settings, generation);
            });
            _loading = load;
            _tail = load;
            load.ContinueWith(t => Finished(t, modelPath, settings, generation), TaskScheduler.Default);
        }
        Changed?.Invoke();
    }

    private ISpeechEngine Load(string modelPath, WhisperSettings settings, long generation)
    {
        if (IsStale(generation)) return Overtaken(modelPath, loaded: false); // chosen and replaced before its turn
        _integrity?.Verify(modelPath); // a catalogue file that fails its size or SHA-256 check is never loaded
        DecodeOptions decode;
        lock (_gate) decode = _decode;
        var onCpu = settings.Gpu == "cpu";
        string? notice = null;
        ISpeechEngine engine;
        try
        {
            engine = LoadAndWarm(modelPath, settings, decode, onCpu);
        }
        catch (Exception ex) when (!onCpu && _gpu() && ModelFailure.WorthCpuRetry(ex) && !_shutdown.IsCancellationRequested)
        {
            if (IsStale(generation)) return Overtaken(modelPath, loaded: false); // a newer choice came in while this one failed
            _log.Warn($"Speech model {Path.GetFileName(modelPath)} failed on the GPU ({ex.GetType().Name}: {ex.Message}); loading it again on the CPU.");
            onCpu = true;
            engine = LoadAndWarm(modelPath, settings with { Gpu = "cpu" }, decode, onCpu);
            notice = ModelFailure.GpuFailed;
        }
        ISpeechEngine? replaced = null;
        var stale = false;
        lock (_gate)
        {
            if (generation != _generation)
            {
                stale = true; // a newer choice, or quit, came in while it loaded
            }
            else
            {
                (replaced, _engine) = (_engine, engine);
                _engineOnCpu = onCpu;
                Notice = notice;
            }
        }
        if (stale)
        {
            engine.Dispose();
            return Overtaken(modelPath, loaded: true);
        }
        Reconfigure(engine); // a dictionary edit during the load: a no-op unless the options changed meanwhile
        replaced?.Dispose();
        return engine;
    }

    /// <summary>Loads the engine and, on the GPU, warms it up; an engine whose warm-up fails is released before the failure goes on.</summary>
    private ISpeechEngine LoadAndWarm(string modelPath, WhisperSettings settings, DecodeOptions decode, bool onCpu)
    {
        var engine = _loader(modelPath, settings, decode);
        try
        {
            // First use on a GPU compiles shaders (observed 12.3 s once, then ~0.3 s): pay it at load, not on the first dictation.
            if (!onCpu && _gpu()) WarmUp(engine, "after loading");
            return engine;
        }
        catch
        {
            engine.Dispose(); // until 17/10/2026 a failed warm-up left this engine loaded, holding its memory
            throw;
        }
    }

    private bool IsStale(long generation)
    {
        lock (_gate) return generation != _generation;
    }

    /// <summary>What a stale load hands back: the engine in use, or a cancellation when there is none (GetAsync then waits for the newer load).</summary>
    private ISpeechEngine Overtaken(string modelPath, bool loaded)
    {
        _log.Info($"Speech model {Path.GetFileName(modelPath)} {(loaded ? "loaded and released" : "not loaded")}: a newer choice was made.");
        lock (_gate) return _engine ?? throw new OperationCanceledException("A newer speech model choice replaced this load.");
    }

    private void Finished(Task<ISpeechEngine> load, string modelPath, WhisperSettings settings, long generation)
    {
        if (load.IsFaulted)
        {
            var error = load.Exception!.GetBaseException();
            var described = IsStale(generation) ? null : ModelFailure.Describe(load.Exception!, _visualCppRuntime); // may look for DLLs: not under the lock
            var cpuRuntimeMightHelp = _gpu() && ModelFailure.WorthCpuRetry(load.Exception!); // failed inside a GPU runtime
            bool current;
            lock (_gate)
            {
                current = generation == _generation;
                if (current)
                {
                    LastError = described ?? error.Message;
                    CpuMightHelp = cpuRuntimeMightHelp && settings.Gpu != "cpu";
                    RestartForCpu = cpuRuntimeMightHelp && settings.Gpu == "cpu";
                }
            }
            if (current) _log.Error($"Model load failed for {modelPath}: {error}");
            else _log.Warn($"Model load failed for {modelPath} after a newer choice: {error.GetType().Name}: {error.Message}");
        }
        Changed?.Invoke();
    }

    public async Task<ITranscriber> GetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<ISpeechEngine>? loading;
            lock (_gate) loading = _loading;
            if (loading is null) throw new InvalidOperationException("No speech model is loaded. Use the tray menu to download or choose one.");
            try
            {
                var engine = await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (IsLatest(loading)) return engine;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (!IsLatest(loading)) continue; // overtaken by a newer choice: wait for that one
                lock (_gate)
                {
                    if (_engine is { } kept) return kept; // a failed reload keeps the previously loaded model working
                }
                throw;
            }
        }
    }

    private bool IsLatest(Task loading)
    {
        lock (_gate) return ReferenceEquals(loading, _loading);
    }

    /// <summary>
    /// Releases the loaded model (Settings → Storage deleting every model): dictation then has none until one is chosen, and
    /// a load still running releases its engine when it finishes.
    /// </summary>
    public void Unload()
    {
        ISpeechEngine? engine;
        lock (_gate)
        {
            _generation++;
            engine = _engine;
            _engine = null;
            _engineOnCpu = false;
            Notice = null;
            _loading = null;
            ModelPath = null;
        }
        engine?.Dispose();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        ISpeechEngine? engine;
        lock (_gate)
        {
            _generation++; // a load still running releases its engine when it finishes
            engine = _engine;
            _engine = null;
        }
        _shutdown.Cancel(); // a warm-up stops at whisper.cpp's next abort check
        if (engine is null) return;
        var sw = Stopwatch.StartNew();
        engine.Dispose();
        _log.Info($"Whisper engine disposed ({Path.GetFileName(engine.ModelPath)}) in {sw.ElapsedMilliseconds} ms.");
    }
}
