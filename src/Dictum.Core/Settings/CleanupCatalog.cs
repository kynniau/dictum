namespace Dictum.Settings;

/// <summary>
/// What a download must be: where from, the file name, the exact size and SHA-256 when known, and whether the host
/// needs a logged-in account (gated repositories answer an anonymous request with 401).
/// </summary>
public sealed record DownloadSpec(Uri Uri, string FileName, long? Bytes, string? Sha256, bool RequiresLogin = false);

/// <param name="Name">Short name used in settings ("qwen2.5-1.5b").</param>
/// <param name="Vendor">The organisation that publishes the file on huggingface.co (the source rule: vendor first).</param>
/// <param name="Repo">Hugging Face repository.</param>
/// <param name="Bytes">Exact size: HTTP HEAD X-Linked-Size, or the HF API tree listing for gated repos (HEAD answers 401).</param>
/// <param name="Sha256">LFS SHA-256 from https://huggingface.co/api/models/&lt;repo&gt;/tree/main; null where the API masks it (gated).</param>
/// <param name="Licence">Licence as stated on the model card / LICENSE file.</param>
/// <param name="LicenceUrl">Where that licence was read.</param>
/// <param name="CommercialUse">False → listed with a "non-commercial licence" marker and never recommended.</param>
/// <param name="RequiresLogin">Gated on huggingface.co: Dictum cannot download it anonymously and says so instead of trying.</param>
/// <param name="Revision">The repository commit the download is pinned to (read <see cref="CleanupCatalog.HashesRead"/>: the HF API tree at it
/// lists the file with this size and SHA-256; HEAD of the pinned URL answered 302 with both, or 401 for a gated repo).</param>
public sealed record CleanupModelInfo(
    string Name, string Vendor, string Repo, string FileName, long Bytes, string? Sha256,
    string Licence, string LicenceUrl, bool CommercialUse, bool RequiresLogin, string TradeOff, string Revision)
{
    public string SizeText => ModelCatalog.FormatSize(Bytes);
    public Uri DownloadUri => new($"https://{ModelCatalog.DownloadHost}/{Repo}/resolve/{Revision}/{FileName}");
    public DownloadSpec Download => new(DownloadUri, FileName, Bytes, Sha256, RequiresLogin);

    public string Marker => (CommercialUse ? "" : " · non-commercial licence") + (RequiresLogin ? " · requires a Hugging Face login" : "") + (Sha256 is null ? " · size-checked only" : "")
        + (LicenceNote is { } note ? " · " + note : "");

    /// <summary>
    /// The licence a picker, a menu and the download question name beside the model, or null for one under an open-source licence
    /// (Apache-2.0, MIT). The two Gemma models: APPROVED by the owner 23/10/2026 13:20 AEDT (legal strings block 2): do not reword, "Gemma Terms of Use apply
    /// (ai.google.dev/gemma/terms)".
    /// </summary>
    public string? LicenceNote => Licence == CleanupCatalog.GemmaLicence ? CleanupCatalog.GemmaNote : null;

    /// <summary>How a picker lists it: "qwen2.5-1.5b · 1.1 GB · not downloaded".</summary>
    public string ItemText(bool downloaded) => $"{Name} · {SizeText} · {(downloaded ? "downloaded" : "not downloaded")}{Marker}";
}

/// <summary>
/// Cleanup model catalogue (SPEC §5): the single source for the tray "Cleanup model" submenu, the first-run picker,
/// --cleanup-model / --download-cleanup-model and (mirrored, kept in sync by a unit test) download-model.ps1 -Cleanup.
/// Every value below was read on 15/10/2026 from huggingface.co (HEAD, the HF API and the model cards); none is
/// from memory. Families the vendor does not publish as GGUF (Phi-4-mini, Llama 3.2) are left out: see DECISIONS.md.
/// No entry is under a non-commercial licence: qwen2.5-3b (Qwen Research License) was removed by the owner's legal block
/// (23/10/2026, section D item 9), so Dictum may be used at work with any model on its list (Terms section 2).
/// </summary>
public static class CleanupCatalog
{
    public const string HashesRead = "15/10/2026";

    /// <summary>The licence name the two Gemma entries carry (their model card's "gemma" licence).</summary>
    public const string GemmaLicence = "Gemma Terms of Use";

    /// <summary>APPROVED by the owner 23/10/2026 13:20 AEDT (legal strings block 2): do not reword. What Dictum shows beside a Gemma model.</summary>
    public const string GemmaNote = "Gemma Terms of Use apply (ai.google.dev/gemma/terms)";

    public static IReadOnlyList<CleanupModelInfo> Entries { get; } =
    [
        new("qwen2.5-0.5b", "Qwen", "Qwen/Qwen2.5-0.5B-Instruct-GGUF", "qwen2.5-0.5b-instruct-q4_k_m.gguf", 491_400_032,
            "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db", "Apache-2.0",
            "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/blob/main/LICENSE", true, false, "faster, but dropped content in 2 of 4 measured cases; never recommended", "9217f5db79a29953eb74d5343926648285ec7e67"),
        new("qwen2.5-1.5b", "Qwen", "Qwen/Qwen2.5-1.5B-Instruct-GGUF", "qwen2.5-1.5b-instruct-q4_k_m.gguf", 1_117_320_736,
            "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", "Apache-2.0",
            "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/blob/main/LICENSE", true, false, "the default: steady filler removal and punctuation", "91cad51170dc346986eccefdc2dd33a9da36ead9"),
        new("smollm2-1.7b", "HuggingFaceTB", "HuggingFaceTB/SmolLM2-1.7B-Instruct-GGUF", "smollm2-1.7b-instruct-q4_k_m.gguf", 1_055_609_536,
            "decd2598bc2c8ed08c19adc3c8fdd461ee19ed5708679d1c54ef54a5a30d4f33", "Apache-2.0",
            "https://huggingface.co/HuggingFaceTB/SmolLM2-1.7B-Instruct", true, false, "another family (Hugging Face); not tested here", "2d4a76a30b4af41ecd395c35725ac11688d4cfe4"),
        new("gemma-3-1b", "Google", "google/gemma-3-1b-it-qat-q4_0-gguf", "gemma-3-1b-it-q4_0.gguf", 1_003_541_152,
            null, GemmaLicence, "https://ai.google.dev/gemma/terms", true, true, "Google's Gemma 3, quantisation-aware Q4_0; size-checked only; not tested here", "d1be121d36172a4b0b964657e2ee859d61138593"),
        new("gemma-3-4b", "Google", "google/gemma-3-4b-it-qat-q4_0-gguf", "gemma-3-4b-it-q4_0.gguf", 3_155_051_328,
            null, GemmaLicence, "https://ai.google.dev/gemma/terms", true, true, "larger Gemma 3; wants a GPU; size-checked only; not tested here", "15f73f5eee9c28f53afefef5723e29680c2fc78a"),
    ];

    /// <summary>
    /// The CPU-only recommendation. Rule (orchestrator, 15/10/2026): recommend qwen2.5-1.5b if it adds at most 2 s to the
    /// 10 s clip on CPU, else the rule-based tidy; never qwen2.5-0.5b, whose replies were rejected for dropping content in
    /// 2 of the 4 measured clip/backend cases. Measured 15/10/2026 on the 6-core Xeon W-1250P with CPU forced, after the
    /// prompt-prefix cache: 1.5b added 1.60 s and 1.79 s (two runs; 4.04 s before the cache).
    /// </summary>
    public const string CpuRecommendation = "qwen2.5-1.5b";

    public static CleanupModelInfo? Find(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The catalogue entry whose file this is (any folder), for the first-use hash check.</summary>
    public static CleanupModelInfo? FindByFile(string path) =>
        Entries.FirstOrDefault(e => string.Equals(e.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>A catalogue name or a fully qualified path to a .gguf file.</summary>
    public static bool IsValidSetting(string value) => Find(value) is not null || (Path.IsPathFullyQualified(value) && value.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));

    public static string ResolvePath(string model, string modelsDir) =>
        Path.IsPathFullyQualified(model) ? model : Path.Combine(modelsDir, FileNameFor(model));

    /// <summary>The file a cleanup model name is kept as: the catalogue's file name, or "&lt;name&gt;.gguf" for a name outside it.</summary>
    public static string FileNameFor(string name) => Find(name)?.FileName ?? $"{name}.gguf";

    /// <summary>The catalogue name for a catalogue name or a path to a catalogue file (any folder, as the hash check matches); otherwise the file name.</summary>
    public static string DisplayName(string model) =>
        Path.IsPathFullyQualified(model) ? FindByFile(model)?.Name ?? Path.GetFileNameWithoutExtension(model) : model;

    /// <summary>
    /// SPEC §5: qwen2.5-1.5b everywhere (0.5b offered as faster, rougher); a non-commercial, gated or untested model
    /// is never recommended. The CPU choice can be overridden by measurement (<see cref="CpuRecommendation"/>).
    /// </summary>
    public static (string Model, string Reason) Recommend(bool gpuRuntime) => gpuRuntime
        ? ("qwen2.5-1.5b", "GPU: about 0.2 s per 10 s dictation")
        : (CpuRecommendation, "CPU: about 1.8 s per 10 s dictation on a 6-core CPU; choose \"No AI cleanup\" if that is too slow");
}
