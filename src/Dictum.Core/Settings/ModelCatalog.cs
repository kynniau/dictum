using System.Text.RegularExpressions;

namespace Dictum.Settings;

/// <param name="Name">Short name used in settings ("small.en").</param>
/// <param name="Bytes">Exact file size: the ten original entries verified by HTTP HEAD (X-Linked-Size) on 15/10/2026; the six multilingual entries from the HF API tree at <see cref="ModelCatalog.Revision"/>, read 16/10/2026.</param>
/// <param name="Sha256">LFS SHA-256 from https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main, read 15/10/2026 (the same at <see cref="ModelCatalog.Revision"/>); the multilingual entries from the tree at Revision, 16/10/2026.</param>
/// <param name="TradeOff">One line for the tray, the first-run picker and the README.</param>
public sealed record ModelInfo(string Name, long Bytes, string Sha256, string TradeOff)
{
    public string FileName => ModelCatalog.FileName(Name);
    public string SizeText => ModelCatalog.FormatSize(Bytes);
    public DownloadSpec Download => new(ModelCatalog.DownloadUri(Name), FileName, Bytes, Sha256);

    /// <summary>How a picker lists it: "small.en · 487 MB · not downloaded".</summary>
    public string ItemText(bool downloaded) => $"{Name} · {SizeText} · {(downloaded ? "downloaded" : "not downloaded")}";
}

/// <summary>
/// The model catalogue (SPEC §10): the single list used by the tray submenu, the first-run picker, the CLI and
/// (mirrored, and kept in sync by a unit test) scripts/download-model.ps1. All files come from
/// https://huggingface.co/ggerganov/whisper.cpp/resolve/&lt;commit&gt;/ggml-&lt;name&gt;.bin, pinned to one commit (<see cref="Revision"/>).
/// </summary>
public static partial class ModelCatalog
{
    public const string DownloadHost = "huggingface.co";

    /// <summary>
    /// The ggerganov/whisper.cpp commit every speech download is pinned to, so the URL keeps serving the bytes the hashes
    /// below describe even if the repository's main branch changes. Read on <see cref="RevisionRead"/>: the HF API tree at
    /// this commit lists every file below with the same size and LFS SHA-256, and a HEAD of each of the ten original pinned
    /// URLs answered 302 with a matching X-Linked-Size and X-Linked-ETag (redirecting to the same CDN host as resolve/main).
    /// The six multilingual entries were read from the same tree at this commit on 16/10/2026, with four of the original
    /// entries re-read as controls; no HEAD was made for them.
    /// </summary>
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    public const string RevisionRead = "15/10/2026";

    public static IReadOnlyList<ModelInfo> Entries { get; } =
    [
        new("tiny.en", 77_704_715, "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f", "fastest, lowest accuracy; for slow CPUs"),
        new("base.en", 147_964_211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002", "fast on any CPU, fair accuracy; the CPU default"),
        new("small.en", 487_614_201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d", "more accurate than base.en, a few seconds slower on a CPU"),
        new("small.en-q5_1", 190_098_681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30", "small.en quantised to 5-bit: smallest small download"),
        new("small.en-q8_0", 264_477_561, "67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067", "small.en quantised to 8-bit: about half the size"),
        new("medium.en", 1_533_774_781, "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356", "high accuracy; wants a GPU or a fast desktop CPU"),
        new("medium.en-q5_0", 539_225_533, "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0", "medium.en quantised to 5-bit: about a third of the size"),
        new("medium.en-q8_0", 823_382_461, "43fa2cd084de5a04399a896a9a7a786064e221365c01700cea4666005218f11c", "medium.en quantised to 8-bit: about half the size"),
        new("large-v3-turbo", 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", "multilingual, about 8× faster than large-v3 (OpenAI's table) but cannot translate; needs a GPU"),
        new("large-v3-turbo-q5_0", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", "large-v3-turbo quantised to 5-bit; still needs a GPU"),
        // Multilingual models (sizes and hashes from the HF API tree at Revision, read 16/10/2026): any Whisper language, and
        // speech-to-English translation. OpenAI's README: "Use medium or large for the best translation results".
        new("small", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", "any language, translates to English; the smallest multilingual model, the same size as small.en"),
        new("small-q5_1", 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", "small quantised to 5-bit: smallest multilingual download"),
        new("medium", 1_533_763_059, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", "any language, translates well to English; wants a GPU or a fast desktop CPU"),
        new("medium-q5_0", 539_212_467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f", "medium quantised to 5-bit: about a third of the size"),
        new("large-v3", 3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2", "the most accurate in any language and the best for translation; needs a GPU"),
        new("large-v3-q5_0", 1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1", "large-v3 quantised to 5-bit; still needs a GPU"),
    ];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ShortNamePattern();

    public static ModelInfo? Find(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A short name ("small.en") or a fully qualified path to a model file.</summary>
    public static bool IsValidModelSetting(string value) => IsShortName(value) || Path.IsPathFullyQualified(value);

    public static bool IsShortName(string value) => ShortNamePattern().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);

    public static string FileName(string shortName) => $"ggml-{shortName}.bin";

    /// <summary>
    /// The speech model inside every package (Decision 16, 17/10/2026): models\ggml-base.en.bin beside Dictum.exe. When it is
    /// found the first run asks no model question (ModelCoordinator.FirstRun).
    /// </summary>
    public const string Bundled = "base.en";

    /// <summary>The catalogue entry whose file this is (any folder), for the first-use hash check.</summary>
    public static ModelInfo? FindByFile(string path) =>
        Entries.FirstOrDefault(e => string.Equals(FileName(e.Name), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves the configured model to a file path. An absolute path is used as-is.</summary>
    public static string ResolvePath(string model, string modelsDir) =>
        Path.IsPathFullyQualified(model) ? model : Path.Combine(modelsDir, FileName(model));

    /// <summary>Display name: the short name, or the file name (minus "ggml-" and ".bin") for a path.</summary>
    public static string DisplayName(string model) =>
        Path.IsPathFullyQualified(model) ? Path.GetFileNameWithoutExtension(model).Replace("ggml-", "", StringComparison.Ordinal) : model;

    public static long? KnownSize(string shortName) => Find(shortName)?.Bytes;

    public static Uri DownloadUri(string shortName) =>
        new($"https://{DownloadHost}/ggerganov/whisper.cpp/resolve/{Revision}/{FileName(shortName)}");

    public static string FormatSize(long? bytes) => bytes switch
    {
        null => "size unknown",
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        _ => $"{Math.Floor(bytes.Value / 1e6):0} MB", // floor, so 487,614,201 bytes reads "487 MB" as in SPEC §8
    };

    /// <summary>
    /// SPEC §10 recommendation (revised 15/10/2026): a GPU machine (<see cref="Transcribe.WhisperEngine.GpuMachine"/>: a discrete
    /// GPU) → medium.en; CPU or integrated graphics only → base.en, with small.en
    /// offered as "more accurate, a few seconds slower". Shown in the first-run picker and --selftest; never
    /// applied silently.
    /// </summary>
    public static (string Model, string Reason) Recommend(bool gpuRuntime) => gpuRuntime
        ? ("medium.en", "a discrete GPU is available")
        : ("base.en", "no discrete GPU; small.en is more accurate, a few seconds slower");
}
