using System.Diagnostics;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// Owns the loaded whisper engine. Loads happen in the background, one at a time; a recording that finishes while a
/// model is loading waits for it. Every load belongs to a generation: a newer choice (or quit) makes an older load stale,
/// and a stale load releases the engine it made instead of installing it, so the engine dictation uses, the one decoder
/// changes reach and the one quit releases are always the user's last choice. Swapping models disposes the old engine
/// only after any in-flight transcription. A load or warm-up that fails while the GPU is in use is tried once more on the
/// CPU (<see cref="Notice"/>), and an engine whose warm-up fails is released, never left loaded. A GPU load is recorded in
/// a <see cref="GpuLoadMarker"/> while it runs, so a start after the GPU ended Dictum loads that model on the CPU.
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
    private readonly GpuLoadMarker _marker;
    private Task<ISpeechEngine>? _loading; // the latest choice's load
    private Task _tail = Task.CompletedTask; // the last load started: the next one waits for it
    private long _generation; // bumped by every BeginLoad and by Dispose
    private ISpeechEngine? _engine;
    private bool _engineOnCpu; // the engine in use was loaded off the GPU: whisper.gpu = cpu, its GPU load failed, or the marker
    private ModelFailure.Assessment? _failure; // why the latest load failed; null while it loads or once it loaded
    private DecodeOptions _decode = DecodeOptions.Default;

    /// <param name="integrity">Checks catalogue files before they load; its folder (the per-user local data folder in the app) holds
    /// the <see cref="GpuLoadMarker"/>. Without it no marker is read or written.</param>
    /// <param name="loader">Tests load fakes; production loads whisper.cpp through <see cref="WhisperEngine.Load"/>.</param>
    /// <param name="gpuInUse">Whether the GPU is in use (tests decide; production: Vulkan is). On it, loads and decoder changes run a
    /// warm-up, and a load or warm-up that fails is tried once more on the CPU.</param>
    /// <param name="visualCppRuntime">Whether the Visual C++ runtime is on this PC (tests decide; production looks:
    /// <see cref="ModelFailure.VisualCppRuntimePresent()"/>).</param>
    public ModelManager(ILog log, ModelIntegrity? integrity = null, Func<string, WhisperSettings, DecodeOptions, ISpeechEngine>? loader = null,
        Func<bool>? gpuInUse = null, Func<bool>? visualCppRuntime = null)
    {
        _log = log;
        _integrity = integrity;
        _loader = loader ?? ((path, s, decode) => WhisperEngine.Load(path, s.Threads, s.Gpu, log, decode));
        // The production loader fixes whisper's runtime order when a load is asked for, not when it runs, so the clean-up
        // model's backend choice can read the GPUs that runtime finds (WhisperEngine.VulkanDevicesForCleanup).
        _configureRuntime = loader is null ? gpu => WhisperEngine.ConfigureRuntime(gpu, log) : _ => { };
        _gpu = gpuInUse ?? (() => WhisperEngine.VulkanActive);
        _visualCppRuntime = visualCppRuntime ?? ModelFailure.VisualCppRuntimePresent;
        _marker = new GpuLoadMarker(integrity is null ? null : Path.Combine(integrity.Folder, GpuLoadMarker.SpeechFile), log);
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

    /// <summary>Why the latest load failed, in plain words when the cause is known (<see cref="ModelFailure.Assess"/>); null otherwise.</summary>
    public string? LastError => _failure?.LastError;

    /// <summary>The tray panel and About offer "Try on the CPU" for the latest load's failure (<see cref="ModelFailure.Assess"/>).</summary>
    public bool CpuMightHelp => _failure?.CpuMightHelp == true;

    /// <summary>Only a restart can put the latest load's model on the CPU-only runtime, and the problem line says so (<see cref="ModelFailure.Assess"/>).</summary>
    public bool RestartForCpu => _failure?.RestartForCpu == true;

    /// <summary>
    /// The engine in use runs on the CPU: whisper.gpu is cpu, its load failed on the GPU and was made again on the CPU, or the GPU
    /// ended Dictum while it loaded last time (<see cref="GpuLoadMarker"/>).
    /// </summary>
    public bool RunsOnCpu
    {
        get
        {
            lock (_gate) return _engine is not null && _engineOnCpu;
        }
    }

    /// <summary>
    /// While the engine in use runs on the CPU because the GPU failed: <see cref="ModelFailure.GpuFailed"/> after this run's retry,
    /// <see cref="ModelFailure.GpuFailedLastTime"/> after the GPU ended Dictum last time; null otherwise.
    /// </summary>
    public string? Notice { get; private set; }

    public void BeginLoad(string modelPath, WhisperSettings settings)
    {
        var cpuForThisRun = _marker.CpuForThisRun(modelPath, settings.Gpu);
        _configureRuntime(cpuForThisRun ? "cpu" : settings.Gpu); // the GPU ended Dictum on this model: whisper's Vulkan runtime never loads
        lock (_gate)
        {
            var generation = ++_generation;
            ModelPath = modelPath;
            _failure = null;
            var before = _tail;
            var load = Task.Run(async () =>
            {
                await before.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false); // two models never load at once
                return Load(modelPath, settings, generation, cpuForThisRun);
            });
            _loading = load;
            _tail = load;
            load.ContinueWith(t => Finished(t, modelPath, settings, generation), TaskScheduler.Default);
        }
        Changed?.Invoke();
    }

    private ISpeechEngine Load(string modelPath, WhisperSettings settings, long generation, bool cpuForThisRun)
    {
        if (IsStale(generation)) return Overtaken(modelPath, loaded: false); // chosen and replaced before its turn
        _integrity?.Verify(modelPath); // a catalogue file that fails its size or SHA-256 check is never loaded
        DecodeOptions decode;
        lock (_gate) decode = _decode;
        var onCpu = cpuForThisRun || settings.Gpu == "cpu";
        var notice = cpuForThisRun ? ModelFailure.GpuFailedLastTime : null;
        ISpeechEngine engine;
        if (onCpu)
        {
            engine = LoadAndWarm(modelPath, settings with { Gpu = "cpu" }, decode, onCpu);
        }
        else
        {
            // simplification: the retry below catches only failures whisper.cpp reports. Ceiling: when the GPU refuses a model's
            // memory whisper.cpp can end the program instead (GGML_ASSERT, 17/10/2026), which no retry survives; the marker then
            // puts that model on the CPU at the next start, but an end during a later dictation or decoding change is not
            // recorded. Trigger to retire: whisper.cpp reporting allocation failures as errors, or a report of Dictum ending
            // after a model had loaded on the GPU.
            try
            {
                _marker.Begin(modelPath);
                try
                {
                    engine = LoadAndWarm(modelPath, settings, decode, onCpu);
                }
                finally
                {
                    _marker.End();
                }
            }
            catch (Exception ex) when (ModelFailure.RetryOnCpu(ex, _gpu(), _shutdown.Token))
            {
                if (IsStale(generation)) return Overtaken(modelPath, loaded: false); // a newer choice came in while this one failed
                _log.Warn($"Speech model {Path.GetFileName(modelPath)} failed while the GPU was in use ({ex.GetType().Name}: {ex.Message}); loading it again on the CPU.");
                onCpu = true;
                try
                {
                    engine = LoadAndWarm(modelPath, settings with { Gpu = "cpu" }, decode, onCpu);
                }
                catch (Exception cpu)
                {
                    ModelFailure.CpuTried(cpu); // its own failure is the reason now, and Try on the CPU would only repeat it
                    throw;
                }
                notice = ModelFailure.GpuFailed;
            }
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
            var failure = IsStale(generation) ? null : ModelFailure.Assess(load.Exception!, _gpu(), settings.Gpu, _visualCppRuntime); // may look for DLLs: not under the lock
            bool current;
            lock (_gate)
            {
                current = generation == _generation;
                if (current) _failure = failure;
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
        _marker.Close(); // a GPU load still under way at quit did not end Dictum
        if (engine is null) return;
        var sw = Stopwatch.StartNew();
        engine.Dispose();
        _log.Info($"Whisper engine disposed ({Path.GetFileName(engine.ModelPath)}) in {sw.ElapsedMilliseconds} ms.");
    }
}
