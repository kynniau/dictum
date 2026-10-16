using Dictum.Core;
using Dictum.Transcribe;

namespace Dictum.Llm;

/// <summary>
/// Owns the loaded local cleanup model. Loads in the background, one at a time; until a model is ready the cleanup step
/// falls back to the deterministic tidy instead of making the user wait. As in <see cref="Transcribe.ModelManager"/>, every
/// load belongs to a generation that a newer choice, <see cref="Unload"/> or quit makes stale: a stale load releases its
/// engine, so <see cref="Ready"/> is always the user's last choice. Swapping disposes the old engine after it is idle. A load
/// or warm-up that fails while the GPU is in use is tried once more on the CPU (<see cref="Notice"/>). A GPU load is recorded in
/// a <see cref="GpuLoadMarker"/> while it runs, so a start after the GPU ended Dictum loads that model on the CPU.
/// </summary>
public sealed class LocalLlmManager : IDisposable
{
    private readonly ILog _log;
    private readonly ModelIntegrity? _integrity;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new(); // stops a warm-up at quit
    private readonly Func<string, string, ILocalLlmEngine> _loader;
    private readonly Func<bool> _gpu;
    private readonly Func<bool> _visualCppRuntime;
    private readonly GpuLoadMarker _marker;
    private Task<ILocalLlmEngine>? _loading; // the latest choice's load; null after Unload
    private Task _tail = Task.CompletedTask; // the last load started: the next one waits for it
    private long _generation; // bumped by every BeginLoad, Unload and Dispose
    private CancellationTokenSource _superseded = new(); // the latest load's: cancelled when a newer BeginLoad or Unload bumps the generation
    private ILocalLlmEngine? _engine;
    private bool _engineOnCpu; // the engine in use was loaded with llm.gpu "cpu": by the setting, after its GPU load failed, or by the marker
    private ModelFailure.Assessment? _failure; // why the latest load failed; null while it loads or once it loaded
    private string _prompt = CleanupPrompt.Default;

    /// <param name="integrity">Checks catalogue files before they load; its folder (the per-user local data folder in the app) holds
    /// the <see cref="GpuLoadMarker"/>. Without it no marker is read or written.</param>
    /// <param name="loader">Tests load fakes; production loads llama.cpp through <see cref="LocalLlmEngine.Load"/>.</param>
    /// <param name="gpuInUse">Whether the GPU is in use (tests decide; production: the Vulkan build of llama.cpp loaded). A load or
    /// warm-up that fails on it is tried once more on the CPU.</param>
    /// <param name="visualCppRuntime">Whether the Visual C++ runtime is on this PC (tests decide; production looks:
    /// <see cref="ModelFailure.VisualCppRuntimePresent()"/>).</param>
    public LocalLlmManager(ILog log, ModelIntegrity? integrity = null, Func<string, string, ILocalLlmEngine>? loader = null,
        Func<bool>? gpuInUse = null, Func<bool>? visualCppRuntime = null)
    {
        _log = log;
        _integrity = integrity;
        _loader = loader ?? ((path, gpu) => LocalLlmEngine.Load(path, gpu, log));
        _gpu = gpuInUse ?? (() => LocalLlmEngine.Backend == "Vulkan");
        _visualCppRuntime = visualCppRuntime ?? ModelFailure.VisualCppRuntimePresent;
        _marker = new GpuLoadMarker(integrity is null ? null : Path.Combine(integrity.Folder, GpuLoadMarker.CleanupFile), log);
    }

    /// <summary>
    /// The system prompt dictations will use (it changes with llm.grammar_assist and the personal dictionary). The model in
    /// use re-evaluates the new prefix into its cache now, in the background, instead of during the next dictation; a model
    /// still loading checks the prompt again before it is installed.
    /// </summary>
    public void SetPrompt(string prompt)
    {
        ILocalLlmEngine? engine;
        lock (_gate)
        {
            if (prompt == _prompt) return;
            _prompt = prompt;
            engine = _engine;
        }
        if (engine is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var time = await engine.WarmUpAsync(prompt, _shutdown.Token).ConfigureAwait(false);
                _log.Info($"Cleanup prompt changed: new prefix evaluated in {time.TotalMilliseconds:0} ms.");
            }
            catch (ObjectDisposedException)
            {
                // the model was swapped meanwhile; the new one warms up with this prompt
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // quitting
            }
            catch (Exception ex)
            {
                _log.Warn($"Cleanup prompt warm-up failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>Raised (on a worker thread) when a load starts, succeeds or fails.</summary>
    public event Action? Changed;

    public string? ModelPath { get; private set; }
    public bool IsLoading => _loading is { IsCompleted: false };

    /// <summary>Why the latest load failed, in plain words when the cause is known (<see cref="ModelFailure.Assess"/>); null otherwise.</summary>
    public string? LastError => _failure?.LastError;

    /// <summary>The tray panel and About offer "Try on the CPU" for the latest load's failure (<see cref="ModelFailure.Assess"/>).</summary>
    public bool CpuMightHelp => _failure?.CpuMightHelp == true;

    /// <summary>Only a restart can put the latest load's model on llama.cpp's CPU build, and the problem line says so (<see cref="ModelFailure.Assess"/>).</summary>
    public bool RestartForCpu => _failure?.RestartForCpu == true;

    /// <summary>
    /// The engine in use runs on the CPU: llm.gpu is cpu, its load failed on the GPU and was made again on the CPU, or the GPU ended
    /// Dictum while it loaded last time (<see cref="GpuLoadMarker"/>).
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

    /// <summary>The engine if one is loaded and no load is in progress; otherwise null.</summary>
    public ILocalLlmEngine? Ready
    {
        get
        {
            lock (_gate) return _loading is { IsCompletedSuccessfully: true } ? _engine : null;
        }
    }

    public void BeginLoad(string path, string gpuSetting)
    {
        // The first BeginLoad of a run is never for the path already loading, so the marker is read before any early return.
        var cpuForThisRun = _marker.CpuForThisRun(path, gpuSetting);
        CancellationTokenSource stale;
        lock (_gate)
        {
            if (ModelPath == path && _loading is { IsFaulted: false }) return;
            var generation = ++_generation;
            stale = _superseded;
            _superseded = new CancellationTokenSource();
            var superseded = _superseded.Token;
            ModelPath = path;
            _failure = null;
            var before = _tail;
            var load = Task.Run(async () =>
            {
                await before.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false); // two models never load at once
                return await LoadAsync(path, gpuSetting, generation, cpuForThisRun, superseded).ConfigureAwait(false);
            });
            _loading = load;
            _tail = load;
            load.ContinueWith(t => Finished(t, path, gpuSetting, generation), TaskScheduler.Default);
        }
        stale.Cancel(); // the earlier load's warm-up stops; outside the lock, so its continuations never run under _gate
        Changed?.Invoke();
    }

    private async Task<ILocalLlmEngine> LoadAsync(string path, string gpuSetting, long generation, bool cpuForThisRun, CancellationToken superseded)
    {
        if (IsStale(generation)) return Overtaken(path, loaded: false); // chosen and replaced before its turn
        _integrity?.Verify(path); // a catalogue file that fails its size or SHA-256 check is never loaded
        // The GPU ended Dictum on this model last time: llm.gpu = cpu for this run, so llama.cpp's CPU build is the one configured.
        if (cpuForThisRun || gpuSetting == "cpu")
            return await LoadOnAsync(path, "cpu", generation, superseded, cpuForThisRun ? ModelFailure.GpuFailedLastTime : null).ConfigureAwait(false);
        try
        {
            try
            {
                _marker.Begin(path); // inside the try: End releases the gate whatever Begin or the load throws
                return await LoadOnAsync(path, gpuSetting, generation, superseded, notice: null).ConfigureAwait(false);
            }
            finally
            {
                _marker.End();
            }
        }
        catch (Exception ex) when (ModelFailure.RetryOnCpu(ex, _gpu(), _shutdown.Token))
        {
            if (IsStale(generation)) return Overtaken(path, loaded: false); // a newer choice came in while this one failed
            _log.Warn($"Cleanup model {Path.GetFileName(path)} failed while the GPU was in use ({ex.GetType().Name}: {ex.Message}); loading it again on the CPU.");
            try
            {
                return await LoadOnAsync(path, "cpu", generation, superseded, ModelFailure.GpuFailed).ConfigureAwait(false);
            }
            catch (Exception cpu)
            {
                ModelFailure.CpuTried(cpu); // its own failure is the reason now, and Try on the CPU would only repeat it
                throw;
            }
        }
    }

    /// <summary>
    /// Loads the engine with this llm.gpu value, warms it up and installs it; an engine whose warm-up fails is released. The warm-up
    /// stops at quit and when <paramref name="superseded"/> fires (a newer BeginLoad or Unload), so a stale GPU warm-up gives up the
    /// <see cref="GpuLoadMarker"/> gate at once instead of holding the speech model's GPU load behind it.
    /// </summary>
    private async Task<ILocalLlmEngine> LoadOnAsync(string path, string gpuSetting, long generation, CancellationToken superseded, string? notice)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, superseded);
        var engine = _loader(path, gpuSetting);
        // Evaluates the prompt prefix into the cache, measures the speed estimate and, on a GPU, compiles shaders; again if
        // the prompt changed meanwhile, so the engine is installed warm for the prompt dictations will use.
        string? warmed = null;
        while (true)
        {
            string prompt;
            ILocalLlmEngine? replaced = null;
            var installed = false;
            lock (_gate)
            {
                if (generation != _generation) break; // a newer choice, Unload or quit came in while it loaded
                prompt = _prompt;
                if (prompt == warmed)
                {
                    (replaced, _engine) = (_engine, engine);
                    _engineOnCpu = gpuSetting == "cpu";
                    Notice = notice;
                    installed = true;
                }
            }
            if (installed)
            {
                replaced?.Dispose();
                return engine;
            }
            try
            {
                var time = await engine.WarmUpAsync(prompt, stop.Token).ConfigureAwait(false);
                var (promptRate, generationRate) = engine.Rates;
                var backend = gpuSetting == "cpu" ? "CPU" : LocalLlmEngine.Backend;
                _log.Info($"Cleanup model warm-up on {backend} took {time.TotalMilliseconds:0} ms (prompt {promptRate:0} tokens/s, generation {generationRate:0.0} tokens/s).");
            }
            catch
            {
                engine.Dispose(); // a failed or cancelled warm-up never leaves the model resident
                throw;
            }
            warmed = prompt;
        }
        engine.Dispose();
        return Overtaken(path, loaded: true);
    }

    private bool IsStale(long generation)
    {
        lock (_gate) return generation != _generation;
    }

    /// <summary>What a stale load hands back: the engine in use, or a cancellation when there is none.</summary>
    private ILocalLlmEngine Overtaken(string path, bool loaded)
    {
        _log.Info($"Cleanup model {Path.GetFileName(path)} {(loaded ? "loaded and released" : "not loaded")}: a newer choice was made.");
        lock (_gate) return _engine ?? throw new OperationCanceledException("A newer cleanup model choice replaced this load.");
    }

    private void Finished(Task<ILocalLlmEngine> load, string path, string gpuSetting, long generation)
    {
        if (load.IsFaulted)
        {
            var error = load.Exception!.GetBaseException();
            var failure = IsStale(generation) ? null : ModelFailure.Assess(load.Exception!, _gpu(), gpuSetting, _visualCppRuntime); // may look for DLLs: not under the lock
            bool current;
            lock (_gate)
            {
                current = generation == _generation;
                if (current) _failure = failure;
            }
            if (current) _log.Error($"Cleanup model load failed for {path}: {error.GetType().Name}: {error.Message}");
            else _log.Warn($"Cleanup model load failed for {path} after a newer choice: {error.GetType().Name}: {error.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>Stop using a local model (provider changed): the engine is released, and so is one still loading when it finishes.</summary>
    public void Unload()
    {
        ILocalLlmEngine? engine;
        CancellationTokenSource stale;
        lock (_gate)
        {
            _generation++;
            stale = _superseded;
            _superseded = new CancellationTokenSource();
            engine = _engine;
            _engine = null;
            _engineOnCpu = false;
            Notice = null;
            _loading = null;
            ModelPath = null;
        }
        stale.Cancel(); // a warm-up still under way stops and gives up the GPU gate (outside the lock, as in BeginLoad)
        engine?.Dispose();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        ILocalLlmEngine? engine;
        lock (_gate)
        {
            _generation++; // a load still running releases its engine when it finishes
            engine = _engine;
            _engine = null;
        }
        _shutdown.Cancel(); // a warm-up stops between tokens
        _marker.Close(); // a GPU load still under way at quit did not end Dictum
        if (engine is null) return;
        engine.Dispose();
        _log.Info($"Cleanup model disposed ({Path.GetFileName(engine.ModelPath)}).");
    }
}

/// <summary>
/// ILlmClient over the loaded local model. Its estimate decides the long-dictation skip (<see cref="TranscriptCleaner.SkipReason"/>),
/// and a skipped dictation measures nothing: so after <see cref="SkipsBeforeRemeasure"/> estimated dictations in a row that did not
/// run the model, the next gets no estimate, runs the model and measures its speed again (even a run stopped at its deadline counts,
/// <see cref="LocalLlmEngine.CompleteAsync"/>). A dictation the estimate lets through puts the allowance back to three; one that
/// had to be let through to measure doubles it, up to <see cref="MostSkipsBeforeRemeasure"/>, so a PC that really is too slow
/// waits for its deadline less and less often.
/// </summary>
public sealed class LocalLlmClient(Func<ILocalLlmEngine?> engine, string name) : ILlmClient
{
    internal const int SkipsBeforeRemeasure = 3;
    internal const int MostSkipsBeforeRemeasure = 24;

    private readonly object _gate = new();
    // simplification: the pill's question and the clean-up's are matched as one dictation by string instance (Predict's
    // ReferenceEquals), held weakly. Ceiling: a caller that hands the clean-up a copy of the pill's string (a Trim, a join) counts
    // that dictation twice and re-measures sooner, and a pill question with no clean-up question after it (a settings reload
    // retires the cleaner in between) still counts as estimated. Trigger to retire: any change between the pill's text and the
    // clean-up's in DictationPipeline, or a dictation id passed with each call.
    private WeakReference<string>? _lastDictation; // the pill asks first, then the clean-up, with the same string; held weakly
    private TimeSpan? _lastEstimate;
    private int _estimatedSinceRun;
    private int _allowance = SkipsBeforeRemeasure;
    private bool _remeasuring;

    public string Name => $"local/{name}";

    public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken) =>
        CompleteCoreAsync(systemPrompt, rawTranscript, null, cancellationToken);

    /// <summary>A hands-free part: the carried words go after the cached prompt prefix, so the prefix stays evaluated (CleanupSession.Render).</summary>
    public Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, string carried, CancellationToken cancellationToken) =>
        CompleteCoreAsync(systemPrompt, rawTranscript, carried, cancellationToken);

    private async Task<LlmReply> CompleteCoreAsync(string systemPrompt, string rawTranscript, string? carried, CancellationToken cancellationToken)
    {
        var loaded = engine() ?? throw new InvalidOperationException("the cleanup model is not loaded yet");
        Ran();
        var maxTokens = CleanupExamples.LocalMaxTokens(loaded.CountTokens(rawTranscript));
        var result = carried is null
            ? await loaded.CompleteAsync(systemPrompt, CleanupExamples.Pairs, rawTranscript, maxTokens, cancellationToken).ConfigureAwait(false)
            : await loaded.CompleteAsync(systemPrompt, CleanupExamples.Pairs, rawTranscript, carried, maxTokens, cancellationToken).ConfigureAwait(false);
        return new LlmReply(result.Text, result.Finished ? "end_turn" : "max_tokens");
    }

    /// <summary>Estimated time at the rates measured at warm-up and on the latest calls; null until known, and null for a dictation sent to measure again.</summary>
    public TimeSpan? Predict(string rawTranscript)
    {
        if (engine() is not { } loaded) return null;
        lock (_gate)
        {
            if (_lastDictation is { } last && last.TryGetTarget(out var previous) && ReferenceEquals(previous, rawTranscript)) return _lastEstimate;
            _lastDictation = new WeakReference<string>(rawTranscript);
            var estimate = loaded.Predict(loaded.CountTokens(rawTranscript));
            if (estimate is not null && ++_estimatedSinceRun > _allowance)
            {
                _remeasuring = true;
                estimate = null; // this dictation runs the model, which measures its speed again
            }
            return _lastEstimate = estimate;
        }
    }

    private void Ran()
    {
        lock (_gate)
        {
            _allowance = _remeasuring ? Math.Min(_allowance * 2, MostSkipsBeforeRemeasure) : SkipsBeforeRemeasure;
            _remeasuring = false;
            _estimatedSinceRun = 0;
        }
    }
}
