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
internal sealed record CalibrationHost(
    AppPaths Paths,
    Func<AppSettings> Settings,
    Func<PersonalText> Personal,
    ModelIntegrity Integrity,
    ILog Log,
    Action<(string Key, JsonNode? Value)[]> Write,
    Action<string, Action, Action<bool>> DownloadSpeechModel,
    Action<IReadOnlyList<string>> AddDictionaryTerms,
    Action<IReadOnlyList<CorrectionRule>> AddCorrections)
{
    /// <summary>Catalogue models whose file is in the models folder.</summary>
    public IReadOnlyList<SpeechCandidate> InstalledModels()
    {
        var dir = Paths.ModelsDir(Settings());
        return ModelCatalog.Entries
            .Select(e => new SpeechCandidate(e.Name, ModelCatalog.ResolvePath(e.Name, dir)))
            .Where(c => File.Exists(c.Path))
            .ToList();
    }

    public IReadOnlyList<ModelInfo> MissingModels()
    {
        var dir = Paths.ModelsDir(Settings());
        return ModelCatalog.Entries.Where(e => !File.Exists(ModelCatalog.ResolvePath(e.Name, dir))).ToList();
    }

    /// <summary>The engine over real whisper models: each file is size/SHA-256 checked before it is loaded, as everywhere else.</summary>
    public CalibrationEngine CreateEngine()
    {
        var s = Settings();
        return new CalibrationEngine((path, decode) =>
        {
            Integrity.Verify(path);
            return WhisperEngine.Load(path, s.Whisper.Language, s.Whisper.Threads, s.Whisper.Gpu, Log, decode);
        }, Log, () => WhisperEngine.VulkanActive, s.LogTranscripts);
    }
}
