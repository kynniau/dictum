using System.Text.Json.Nodes;
using Dictum.Core;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Calibration;

/// <summary>What the calibration window needs from the running app; every write goes through the app's own paths.</summary>
/// <param name="Write">Writes settings.json keys through the tray's writer (which reloads and hot-swaps the model).</param>
/// <param name="DownloadSpeechModel">Asks, downloads through the app's downloader (refused under offline_only), then calls back; the
/// last callback says whether it arrived (false when declined, refused, failed or cancelled).</param>
/// <param name="LoadSpeech">Tests load fakes; production loads whisper.cpp through <see cref="WhisperEngine.Load"/>.</param>
internal sealed record CalibrationHost(
    AppPaths Paths,
    Func<AppSettings> Settings,
    Func<PersonalText> Personal,
    ModelIntegrity Integrity,
    ILog Log,
    Action<(string Key, JsonNode? Value)[]> Write,
    Action<string, Action, Action<bool>> DownloadSpeechModel,
    Action<IReadOnlyList<string>> AddDictionaryTerms,
    Action<IReadOnlyList<CorrectionRule>> AddCorrections,
    Func<string, WhisperSettings, DecodeOptions, ISpeechEngine>? LoadSpeech = null)
{
    /// <summary>Catalogue models on this PC: the file each name resolves to in the model folders (ModelFolders; the one that came with Dictum included).</summary>
    public IReadOnlyList<SpeechCandidate> InstalledModels()
    {
        var settings = Settings();
        return [.. ModelCatalog.Entries
            .Select(e => (e.Name, Path: ModelFolders.Find(e.FileName, e.Bytes, Paths, settings)))
            .Where(c => c.Path is not null)
            .Select(c => new SpeechCandidate(c.Name, c.Path!))];
    }

    public IReadOnlyList<ModelInfo> MissingModels()
    {
        var settings = Settings();
        return ModelCatalog.Entries.Where(e => ModelFolders.Find(e.FileName, e.Bytes, Paths, settings) is null).ToList();
    }

    /// <summary>
    /// The engine over real whisper models: each file is size/SHA-256 checked before it is loaded, as everywhere else. It decodes
    /// with calibration's own options (English, no translation), never whisper.language or whisper.translate: the sentences
    /// read aloud are English (SPEC §23).
    /// </summary>
    public CalibrationEngine CreateEngine()
    {
        var s = Settings();
        var load = LoadSpeech ?? ((path, whisper, decode) => WhisperEngine.Load(path, whisper.Threads, whisper.Gpu, Log, decode));
        return new CalibrationEngine((path, decode) =>
        {
            Integrity.Verify(path);
            return load(path, s.Whisper, decode);
        }, Log, () => WhisperEngine.VulkanActive, s.LogTranscripts);
    }
}
