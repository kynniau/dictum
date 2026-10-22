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
/// <param name="Write">Writes settings.json keys through the tray's writer (which reloads and hot-swaps the model).</param>
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
    Action<(string Key, JsonNode? Value)[]> Write,
    Action<string, Action<bool>, Action, Action<bool>> DownloadSpeechModel,
    Action<IReadOnlyList<string>> AddDictionaryTerms,
    Action<IReadOnlyList<CorrectionRule>> AddCorrections,
    Func<string, WhisperSettings, DecodeOptions, ISpeechEngine>? LoadSpeech = null,
    Func<bool>? MovingFiles = null)
{
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
