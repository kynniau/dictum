using System.Text.Json.Nodes;
using Dictum.Core;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Calibration;

/// <summary>What the calibration window needs from the running app; every write goes through the app's own paths.</summary>
/// <param name="FindSpeechModel">
/// The tray app's own lookup (<see cref="App.ModelCoordinator.Find"/>): the file a catalogue speech model is used from, in the
/// model folders, with a file that came with Dictum held to its full check (size and SHA-256); null when it is not on this PC.
/// </param>
/// <param name="Write">
/// Writes settings.json keys through the tray's writer (which reloads and hot-swaps the model). False when the file could not be
/// written: the tray's balloon has said so, and nothing was reloaded (<see cref="App.ModelCoordinator.Write"/>).
/// </param>
/// <param name="CurrentModel">
/// The speech model loaded or loading (<see cref="App.ModelCoordinator.CurrentName"/>): it sees a --model or DICTUM_MODEL_PATH
/// override. It names a model whose load then failed too (ModelManager.BeginLoad sets its path at once and a fault does not reset
/// it), which is why <paramref name="SpeechLoadProblem"/> is asked as well. Null: settings.json's whisper.model.
/// </param>
/// <param name="SpeechLoadProblem">
/// The tray's text for a failed speech model load while the latest load has failed (<c>ModelManager.LoadFailed</c>, with the text
/// the tray balloon shows), else null. A model whose load failed is not in use: the old one keeps hearing dictation. Null: never failed.
/// </param>
/// <param name="CloudSpeechHost">
/// The host each recording goes to while cloud speech is on (<c>CloudSpeech.SendsAudio</c>; the local model is then only the
/// fallback), else null. Null: never.
/// </param>
/// <param name="SpeechReady">
/// A speech model is loaded and none is loading (<c>ModelManager.IsReady</c>); the dictation hotkey is ignored until then. Null: always ready.
/// </param>
/// <param name="DownloadSpeechModel">Asks, downloads through the app's downloader (refused under offline_only), then calls back, in
/// this order: whether the download goes ahead (true once agreed, false when the question is declined; not called when it is
/// refused before the question; called synchronously on the calling thread, before this returns), then on arrival, then the
/// last callback says whether it arrived (false when declined, refused, failed or cancelled; these two may come later, from a
/// download).</param>
/// <param name="LoadSpeech">Tests load fakes; production loads whisper.cpp through <see cref="WhisperEngine.Load"/>.</param>
internal sealed record CalibrationHost(
    AppPaths Paths,
    Func<AppSettings> Settings,
    Func<PersonalText> Personal,
    ModelIntegrity Integrity,
    ILog Log,
    Func<ModelInfo, string?> FindSpeechModel,
    Func<(string Key, JsonNode? Value)[], bool> Write,
    Action<string, Action<bool>, Action, Action<bool>> DownloadSpeechModel,
    Action<IReadOnlyList<string>> AddDictionaryTerms,
    Action<IReadOnlyList<CorrectionRule>> AddCorrections,
    Func<string, WhisperSettings, DecodeOptions, ISpeechEngine>? LoadSpeech = null,
    Func<bool>? MovingFiles = null,
    Func<string>? CurrentModel = null,
    Func<string?>? SpeechLoadProblem = null,
    Func<string?>? CloudSpeechHost = null,
    Func<bool>? SpeechReady = null)
{
    /// <summary>Whether the app holds this model: the one named by <see cref="CurrentModel"/> (loaded or loading, failed or not).</summary>
    public bool HoldsModel(string model) =>
        string.Equals(CurrentModel?.Invoke() ?? Settings().Whisper.Model, model, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this model is the one dictation uses now: <see cref="HoldsModel"/>, and its latest load has not failed (<see cref="SpeechLoadProblem"/>).</summary>
    public bool ModelInUse(string model) => SpeechLoadProblem?.Invoke() is null && HoldsModel(model);

    /// <summary>Whether this combination is what dictation uses now: <see cref="ModelInUse"/>, and the decoding settings.json holds (greedy is whisper.beam_size 0).</summary>
    public bool InUse(ComboResult combo) => ModelInUse(combo.Model) && Settings().Whisper.BeamSize == combo.BeamSize;

    /// <summary>Why calibration cannot run now (model files are moving: <see cref="App.ModelCoordinator.MovingRefusal"/>), or null.</summary>
    public string? Refusal() => MovingFiles?.Invoke() == true ? App.ModelCoordinator.MovingRefusal : null;

    /// <summary>Catalogue models on this PC: the file each name resolves to through the tray app's lookup (<see cref="FindSpeechModel"/>).</summary>
    public IReadOnlyList<SpeechCandidate> InstalledModels() =>
        [.. ModelCatalog.Entries
            .Select(e => (e.Name, Path: FindSpeechModel(e)))
            .Where(c => c.Path is not null)
            .Select(c => new SpeechCandidate(c.Name, c.Path!))];

    /// <summary>Catalogue models the same lookup does not find: calibration offers them as "Download and include".</summary>
    public IReadOnlyList<ModelInfo> MissingModels() => ModelCatalog.Entries.Where(e => FindSpeechModel(e) is null).ToList();

    /// <summary>
    /// The engine over real whisper models: each file is size/SHA-256 checked before it is loaded, as everywhere else. It decodes
    /// with calibration's own options (English, no translation), never whisper.language or whisper.translate: the sentences
    /// read aloud are English (SPEC §23).
    /// </summary>
    public CalibrationEngine CreateEngine()
    {
        // No model loads while the model files move (re-lens on PR #61): it would open a file in the folder being emptied.
        if (Refusal() is { } refused) throw new InvalidOperationException(refused);
        var s = Settings();
        var load = LoadSpeech ?? ((path, whisper, decode) => WhisperEngine.Load(path, whisper.Threads, whisper.Gpu, Log, decode));
        return new CalibrationEngine((path, decode) =>
        {
            if (Refusal() is { } moving) throw new InvalidOperationException(moving); // a run that began before the move
            Integrity.Verify(path);
            return load(path, s.Whisper, decode);
        }, Log, () => WhisperEngine.VulkanActive, s.LogTranscripts);
    }
}
