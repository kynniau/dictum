using Dictum.Core;
using Dictum.Settings;
using Whisper.net;

namespace Dictum.Transcribe;

/// <summary>Whether a take holds speech, and why in words for the log (the segment count and how much was speech; never the audio).</summary>
public sealed record VoiceVerdict(bool Speech, string Why);

/// <summary>
/// Decides, before the speech step hears a take, whether it holds speech at all (SPEC §3, voice activity gate, 04/11/2026):
/// whisper writes text for silence and noise, and a take that holds none is discarded instead of transcribed.
/// </summary>
public interface IVoiceGate
{
    Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken);
}

/// <summary>
/// whisper.cpp's Silero voice activity detector through Whisper.net 1.9.1's WhisperVadFactory and WhisperVadProcessor (read in the
/// package's Whisper.net.xml, 04/11/2026; the whisper_vad_* exports are in each runtime Dictum ships: CPU, Vulkan, no-AVX and the
/// macOS build), so no second inference runtime. It runs on the CPU with whisper.cpp's default settings (threshold 0.5, at least
/// 250 ms of speech), and only decides: a take with speech goes to the speech step whole, exactly as it would without the gate.
/// </summary>
public sealed class SileroVoiceGate : IVoiceGate, IDisposable
{
    /// <summary>The model's short name: the file is ggml-silero-v6.2.0.bin, like the speech models' ggml-&lt;name&gt;.bin.</summary>
    public const string Name = "silero-v6.2.0";

    public static string FileName => ModelCatalog.FileName(Name);

    /// <summary>
    /// The ggml Silero model whisper.cpp's project publishes (huggingface.co/ggml-org/whisper-vad, MIT), at the commit the Hugging Face
    /// API reported on 04/11/2026 (9ffd54a1e1ee413ddf265af9913beaf518d1639b, last modified 17/11/2025), so the bytes cannot change
    /// under the hash. Size and SHA-256 from that API's file listing (the LFS oid), the same day.
    /// </summary>
    public static DownloadSpec Download { get; } = new(
        new Uri("https://huggingface.co/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/ggml-silero-v6.2.0.bin"),
        FileName, 885_098, "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987");

    private readonly SerialUse _use = new(nameof(SileroVoiceGate));
    private readonly WhisperVadFactory _factory;
    private readonly WhisperVadProcessor _processor;

    private SileroVoiceGate(WhisperVadFactory factory, WhisperVadProcessor processor, string modelPath)
    {
        _factory = factory;
        _processor = processor;
        ModelPath = modelPath;
    }

    public string ModelPath { get; }

    /// <summary>
    /// Loads the model into the whisper runtime a speech load has already loaded (<see cref="VoiceGates.Ready"/>): Whisper.net keeps
    /// the first native library it loads, so a detector that loaded first would load it outside GpuLoadMarker's record, and before
    /// ModelManager's SHA-256 check of the speech model.
    /// </summary>
    public static SileroVoiceGate Load(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Voice activity model file not found.", modelPath);
        return NativeLoadGate.Run(() =>
        {
            var factory = WhisperVadFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
            try
            {
                return new SileroVoiceGate(factory, factory.CreateBuilder().WithUseGpu(false).Build(), modelPath);
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        });
    }

    public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken) =>
        _use.RunAsync(async () =>
        {
            var segments = await _processor.DetectSpeechAsync(samples16kMono, cancellationToken).ConfigureAwait(false);
            return Decide([.. segments.Select(s => (s.Start, s.End))], TimeSpan.FromSeconds(samples16kMono.Length / 16000.0));
        }, cancellationToken);

    /// <summary>The verdict from the detector's speech segments: any segment is speech.</summary>
    internal static VoiceVerdict Decide(IReadOnlyList<(TimeSpan Start, TimeSpan End)> segments, TimeSpan duration)
    {
        if (segments.Count == 0) return new(false, $"the voice activity detector found no speech in {duration.TotalSeconds:0.0} s");
        var speech = segments.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.End - s.Start));
        return new(true, $"the voice activity detector found speech: {segments.Count} segment{(segments.Count == 1 ? "" : "s")}, " +
                         $"{speech.TotalSeconds:0.0} s of {duration.TotalSeconds:0.0} s");
    }

    public void Dispose() => _use.Free(() =>
    {
        Task.Run(() => _processor.DisposeAsync().AsTask()).GetAwaiter().GetResult(); // on the pool, as WhisperEngine.Dispose says why
        _factory.Dispose();
    });
}

/// <summary>
/// A native object used by one call at a time and freed only after the call in progress (lens, 04/11/2026): a call queued behind
/// the free wakes to find it freed and throws ObjectDisposedException instead of running on freed native state.
/// </summary>
internal sealed class SerialUse(string owner)
{
    private readonly SemaphoreSlim _busy = new(1, 1); // never disposed: a call still queued must be able to wake and see the free
    private int _freed;

    public async Task<T> RunAsync<T>(Func<Task<T>> use, CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _freed) == 1) throw new ObjectDisposedException(owner);
            return await use().ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Frees once: waits for the call in progress, then runs <paramref name="free"/>; later calls do nothing.</summary>
    public void Free(Action free)
    {
        if (Interlocked.Exchange(ref _freed, 1) == 1) return;
        _busy.Wait();
        try
        {
            free();
        }
        finally
        {
            _busy.Release();
        }
    }
}

/// <summary>
/// The app's voice activity gate (SPEC §3): every settings load says which model file it uses (<see cref="Configure"/>), the first
/// take that needs it loads it (after the size and SHA-256 check every model gets), and a changed file or quitting frees it. None
/// (null) while vad is off or no model folder holds the file: then the level decides silence, as before the gate.
/// </summary>
/// <param name="runtimeReady">Whether a speech load has loaded whisper's native library (<see cref="Ready"/>); tests pass their own.</param>
public sealed class VoiceGates(ILog log, ModelIntegrity? integrity = null, Func<bool>? runtimeReady = null) : IDisposable
{
    private readonly object _lock = new();
    private readonly Func<bool> _runtimeReady = runtimeReady ?? (() => Ready(Runtime()));
    private string? _path;
    private SileroVoiceGate? _loaded;
    private bool _failed;
    private bool _waitSaid;
    private bool _configured; // the first Configure always says where the gate stands

    /// <summary>
    /// The whisper runtime as the gate reads it: whether the order is set, and whether a native library has loaded. Tests replace it.
    /// </summary>
    internal static Func<(bool OrderSet, bool LibraryLoaded)> Runtime { get; set; } =
        () => (WhisperEngine.RuntimeConfigured, Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary is not null);

    /// <summary>
    /// The gate may load only once the library has (lens, 04/11/2026), not when the order is set: ModelManager.BeginLoad sets the order
    /// on the calling thread and loads the library later, on the pool, behind its SHA-256 check and inside GpuLoadMarker's record. The
    /// speech load sets the order and loads the library under one NativeLoadGate lock, which the detector's load takes too.
    /// </summary>
    internal static bool Ready((bool OrderSet, bool LibraryLoaded) runtime) => runtime.LibraryLoaded;

    /// <summary>Whether the settings ask for the gate (it still needs its model file).</summary>
    public static bool Enabled(AppSettings settings) => settings.Vad != AppSettings.VadOff;

    /// <summary>Why there is no gate with these settings, for the log and --selftest.</summary>
    public static string OffReason(AppSettings settings) =>
        Enabled(settings) ? $"no {SileroVoiceGate.FileName} in the model folders; Dictum.exe --download-model {SileroVoiceGate.Name} fetches it" : "vad is off in settings.json";

    /// <summary>The model file for these settings: the first model folder holding it at its size (ModelFolders), or null when
    /// <paramref name="enabled"/> is false or none does.</summary>
    public static string? Locate(bool enabled, AppPaths paths, AppSettings settings, Func<string, bool>? bundledCheck = null) =>
        enabled ? ModelFolders.Find(SileroVoiceGate.FileName, SileroVoiceGate.Download.Bytes, paths, settings, bundledCheck) : null;

    /// <summary>The file in use, or null for no gate.</summary>
    public string? ModelPath
    {
        get
        {
            lock (_lock) return _path;
        }
    }

    /// <param name="modelPath">From <see cref="Locate"/>; null turns the gate off.</param>
    /// <param name="why">Why there is no gate, for the log line when <paramref name="modelPath"/> is null.</param>
    public void Configure(string? modelPath, string why = "")
    {
        SileroVoiceGate? retired;
        lock (_lock)
        {
            if (_configured && string.Equals(_path, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            (retired, _loaded, _failed, _waitSaid, _path, _configured) = (_loaded, null, false, false, modelPath, true);
        }
        log.Info(modelPath is null
            ? $"Voice activity gate: off ({why}); the level decides silence (silence_threshold)."
            : $"Voice activity gate: on, with {modelPath}; it loads with the first dictation.");
        retired?.Dispose(); // waits for a check in progress
    }

    /// <summary>
    /// The loaded gate, loading it on first use; null when there is none, when its file failed its check or its load (said once in
    /// the log), or while no speech load has loaded the whisper runtime (said once; the gate loads with a later take). The check and
    /// the load run outside the lock (lens, 04/11/2026): the load waits on NativeLoadGate, and Configure, on the UI thread, must not
    /// wait behind it. What they find is kept only if the file in use is still the one they looked at.
    /// </summary>
    // simplification: until a speech load has loaded the runtime the level decides. Ceiling: with cloud speech and the local model
    // never loaded this session, every take is judged by the level alone. Trigger to retire: cloud speech reported sending silent
    // takes, or a runtime probe that loads the library (ProbeRuntime) made part of the gate's start.
    public IVoiceGate? Get()
    {
        string path;
        lock (_lock)
        {
            if (_loaded is not null || _path is null || _failed) return _loaded;
            path = _path;
        }
        SileroVoiceGate? loaded = null;
        Exception? failure = null;
        var waiting = false;
        long ms = 0;
        try
        {
            integrity?.Verify(path);
            if (!_runtimeReady()) waiting = true;
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                loaded = SileroVoiceGate.Load(path);
                ms = sw.ElapsedMilliseconds;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        SileroVoiceGate? discard = loaded;
        try
        {
            lock (_lock)
            {
                if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return _loaded; // the file changed meanwhile
                if (waiting)
                {
                    if (!_waitSaid) log.Info("Voice activity gate: waiting for the speech model to load the whisper runtime; until then the level decides silence (silence_threshold).");
                    _waitSaid = true;
                    return null;
                }
                if (failure is not null)
                {
                    if (!_failed) log.Warn($"Voice activity gate: {Path.GetFileName(path)} could not be loaded ({failure.GetType().Name}: {failure.Message}); the level decides silence (silence_threshold) until the file changes.");
                    _failed = true;
                    return null;
                }
                if (_loaded is not null) return _loaded; // another take loaded it first
                (_loaded, discard) = (loaded, null);
                log.Info($"Voice activity gate: {Path.GetFileName(path)} loaded in {ms} ms, on the CPU.");
                return _loaded;
            }
        }
        finally
        {
            discard?.Dispose(); // a gate loaded for a file no longer in use, or loaded twice
        }
    }

    public void Dispose() => Configure(null, "quitting");
}
