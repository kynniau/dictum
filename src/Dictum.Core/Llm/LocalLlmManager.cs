using Dictum.Core;

namespace Dictum.Llm;

/// <summary>
/// Owns the loaded local cleanup model. Loads in the background, one at a time; until a model is ready the cleanup step
/// falls back to the deterministic tidy instead of making the user wait. As in <see cref="Transcribe.ModelManager"/>, every
/// load belongs to a generation that a newer choice, <see cref="Unload"/> or quit makes stale: a stale load releases its
/// engine, so <see cref="Ready"/> is always the user's last choice. Swapping disposes the old engine after it is idle.
/// </summary>
public sealed class LocalLlmManager : IDisposable
{
    private readonly ILog _log;
    private readonly Dictum.Transcribe.ModelIntegrity? _integrity;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new(); // stops a warm-up at quit
    private readonly Func<string, string, ILocalLlmEngine> _loader;
    private Task<ILocalLlmEngine>? _loading; // the latest choice's load; null after Unload
    private Task _tail = Task.CompletedTask; // the last load started: the next one waits for it
    private long _generation; // bumped by every BeginLoad, Unload and Dispose
    private ILocalLlmEngine? _engine;
    private string _prompt = CleanupPrompt.Default;

    public LocalLlmManager(ILog log, Dictum.Transcribe.ModelIntegrity? integrity = null, Func<string, string, ILocalLlmEngine>? loader = null)
    {
        _log = log;
        _integrity = integrity;
        _loader = loader ?? ((path, gpu) => LocalLlmEngine.Load(path, gpu, log));
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
    public string? LastError { get; private set; }

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
        lock (_gate)
        {
            if (ModelPath == path && _loading is { IsFaulted: false }) return;
            var generation = ++_generation;
            ModelPath = path;
            LastError = null;
            var before = _tail;
            var load = Task.Run(async () =>
            {
                await before.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false); // two models never load at once
                return await LoadAsync(path, gpuSetting, generation).ConfigureAwait(false);
            });
            _loading = load;
            _tail = load;
            load.ContinueWith(t => Finished(t, path, generation), TaskScheduler.Default);
        }
        Changed?.Invoke();
    }

    private async Task<ILocalLlmEngine> LoadAsync(string path, string gpuSetting, long generation)
    {
        if (IsStale(generation)) return Overtaken(path, loaded: false); // chosen and replaced before its turn
        _integrity?.Verify(path); // a catalogue file that fails its size or SHA-256 check is never loaded
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
                var time = await engine.WarmUpAsync(prompt, _shutdown.Token).ConfigureAwait(false);
                var (promptRate, generationRate) = engine.Rates;
                _log.Info($"Cleanup model warm-up on {LocalLlmEngine.Backend} took {time.TotalMilliseconds:0} ms (prompt {promptRate:0} tokens/s, generation {generationRate:0.0} tokens/s).");
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

    private void Finished(Task<ILocalLlmEngine> load, string path, long generation)
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
            if (current) _log.Error($"Cleanup model load failed for {path}: {error.GetType().Name}: {error.Message}");
            else _log.Warn($"Cleanup model load failed for {path} after a newer choice: {error.GetType().Name}: {error.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>Stop using a local model (provider changed): the engine is released, and so is one still loading when it finishes.</summary>
    public void Unload()
    {
        ILocalLlmEngine? engine;
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
        ILocalLlmEngine? engine;
        lock (_gate)
        {
            _generation++; // a load still running releases its engine when it finishes
            engine = _engine;
            _engine = null;
        }
        _shutdown.Cancel(); // a warm-up stops between tokens
        if (engine is null) return;
        engine.Dispose();
        _log.Info($"Cleanup model disposed ({Path.GetFileName(engine.ModelPath)}).");
    }
}

/// <summary>ILlmClient over the loaded local model.</summary>
public sealed class LocalLlmClient(Func<ILocalLlmEngine?> engine, string name) : ILlmClient
{
    public string Name => $"local/{name}";

    public async Task<LlmReply> CompleteAsync(string systemPrompt, string rawTranscript, CancellationToken cancellationToken)
    {
        var loaded = engine() ?? throw new InvalidOperationException("the cleanup model is not loaded yet");
        var maxTokens = CleanupExamples.LocalMaxTokens(loaded.CountTokens(rawTranscript));
        var result = await loaded.CompleteAsync(systemPrompt, CleanupExamples.Pairs, rawTranscript, maxTokens, cancellationToken).ConfigureAwait(false);
        return new LlmReply(result.Text, result.Finished ? "end_turn" : "max_tokens");
    }

    /// <summary>Estimated time at the rates measured at warm-up and on the latest calls; null until known.</summary>
    public TimeSpan? Predict(string rawTranscript) => engine() is { } loaded ? loaded.Predict(loaded.CountTokens(rawTranscript)) : null;
}
