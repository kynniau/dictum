using System.Diagnostics;
using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Transcribe;

/// <summary>
/// Owns the loaded whisper engine. Loads happen in the background, one at a time; a recording that finishes while a
/// model is loading waits for it. Every load belongs to a generation: a newer choice (or quit) makes an older load stale,
/// and a stale load releases the engine it made instead of installing it, so the engine dictation uses, the one decoder
/// changes reach and the one quit releases are always the user's last choice. Swapping models disposes the old engine
/// only after any in-flight transcription.
/// </summary>
public sealed class ModelManager : IDisposable
{
    private readonly ILog _log;
    private readonly ModelIntegrity? _integrity;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new(); // stops a warm-up at quit
    private readonly Func<string, WhisperSettings, DecodeOptions, ISpeechEngine> _loader;
    private readonly Func<bool> _warmUp;
    private Task<ISpeechEngine>? _loading; // the latest choice's load
    private Task _tail = Task.CompletedTask; // the last load started: the next one waits for it
    private long _generation; // bumped by every BeginLoad and by Dispose
    private ISpeechEngine? _engine;
    private DecodeOptions _decode = DecodeOptions.Default;

    /// <param name="loader">Tests load fakes; production loads whisper.cpp through <see cref="WhisperEngine.Load"/>.</param>
    /// <param name="warmUp">Whether loads and decoder changes run a warm-up (tests decide; production: Vulkan is in use).</param>
    public ModelManager(ILog log, ModelIntegrity? integrity = null, Func<string, WhisperSettings, DecodeOptions, ISpeechEngine>? loader = null, Func<bool>? warmUp = null)
    {
        _log = log;
        _integrity = integrity;
        _loader = loader ?? ((path, s, decode) => WhisperEngine.Load(path, s.Language, s.Threads, s.Gpu, log, decode));
        _warmUp = warmUp ?? (() => WhisperEngine.VulkanActive);
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
            lock (_gate) decode = _decode;
            if (engine.Decode == decode) return;
            engine.Reconfigure(decode);
            // On Vulkan a new decoder shape compiles shaders on its first run (observed 15/10/2026: 26.1 s for small.en's
            // first transcription with a dictionary prompt, 0.47 s after): pay it now, not on the next dictation.
            if (_warmUp()) WarmUp(engine, "after a decoding change");
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
    public string? LastError { get; private set; }

    public void BeginLoad(string modelPath, WhisperSettings settings)
    {
        lock (_gate)
        {
            var generation = ++_generation;
            ModelPath = modelPath;
            LastError = null;
            var before = _tail;
            var load = Task.Run(async () =>
            {
                await before.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false); // two models never load at once
                return Load(modelPath, settings, generation);
            });
            _loading = load;
            _tail = load;
            load.ContinueWith(t => Finished(t, modelPath, generation), TaskScheduler.Default);
        }
        Changed?.Invoke();
    }

    private ISpeechEngine Load(string modelPath, WhisperSettings settings, long generation)
    {
        if (IsStale(generation)) return Overtaken(modelPath, loaded: false); // chosen and replaced before its turn
        _integrity?.Verify(modelPath); // a catalogue file that fails its size or SHA-256 check is never loaded
        DecodeOptions decode;
        lock (_gate) decode = _decode;
        var engine = _loader(modelPath, settings, decode);
        // First use on a GPU compiles shaders (observed 12.3 s once, then ~0.3 s): pay it at load, not on the first dictation.
        if (_warmUp()) WarmUp(engine, "after loading");
        ISpeechEngine? replaced = null;
        var stale = false;
        lock (_gate)
        {
            if (generation != _generation) stale = true; // a newer choice, or quit, came in while it loaded
            else (replaced, _engine) = (_engine, engine);
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

    private void Finished(Task<ISpeechEngine> load, string modelPath, long generation)
    {
        if (load.IsFaulted)
        {
            var error = load.Exception!.GetBaseException();
            bool current;
            lock (_gate)
            {
                current = generation == _generation;
                if (current) LastError = error.Message;
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
