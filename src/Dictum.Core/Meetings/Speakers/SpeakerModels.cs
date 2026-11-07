using Dictum.Settings;

namespace Dictum.Meetings.Speakers;

/// <summary>
/// The one model file behind "Identify speakers" (design 1.6 B), downloaded on first use through the existing pinned download
/// path (ModelCoordinator.ApplyWithDownloads, ModelDownloader): huggingface.co only, a pinned commit, the exact size and the LFS
/// SHA-256, refused under offline_only. Its size and hash were read from the Hugging Face API tree at the pinned revision
/// (https://huggingface.co/api/models/&lt;repo&gt;/tree/&lt;revision&gt;, the lfs.oid and lfs.size of the file) on
/// <see cref="PinsRead"/>. It is not bundled or committed. Speech turns need no model of their own: they come from the meeting's
/// transcript segments (<see cref="SpeakerSeparator"/>).
/// </summary>
public static class SpeakerModels
{
    public const string PinsRead = "07/11/2026";

    /// <summary>
    /// 3D-Speaker CAM++ trained on VoxCeleb (English), 16 kHz, 512-dimension embeddings: Apache-2.0 (the 3D-Speaker project), as
    /// mirrored for sherpa-onnx at huggingface.co/csukuangfj/speaker-embedding-models, commit 8be2a75c9ed7a590538b268e46fbb65e1aa9d208
    /// (the API's lastModified for that commit: 14/10/2024).
    /// </summary>
    public static DownloadSpec Embedding { get; } = new(
        new Uri("https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/8be2a75c9ed7a590538b268e46fbb65e1aa9d208/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx"),
        "3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx", 29_596_978, "357a834f702b80161e5b981182c038e18553c1f2ca752ed6cec2052365d4129b");

    public static IReadOnlyList<DownloadSpec> All { get; } = [Embedding];

    /// <summary>
    /// Speaker models excluded on licence (design 1.6 B), never to be added: Rev's reverb-diarization (non-production licence),
    /// DiariZen (CC BY-NC), WeSpeaker SimAM and VoxBlink2 models (non-commercial), ReDimNet (no weights licence), and pyannote's
    /// segmentation models (owner decision, Kyle, 07/11/2026: three of segmentation-3.0's training sets carry restrictive terms, and
    /// he chose to avoid the question by design). A test pins that no model file name or URL here contains any of them.
    /// </summary>
    public static IReadOnlyList<string> Excluded { get; } = ["reverb", "diarizen", "simam", "voxblink", "redimnet", "pyannote"];

    /// <summary>The spec for this file name (any folder), or null.</summary>
    public static DownloadSpec? FindByFile(string path) =>
        All.FirstOrDefault(s => string.Equals(s.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>"&lt;file&gt;@&lt;first 8 of its SHA-256&gt;": what speakers.json.enc records as the model that made it.</summary>
    public static string Tag(DownloadSpec spec) => $"{spec.FileName}@{spec.Sha256![..8]}";

    /// <summary>The embedding model in the model folders at its pinned size, or null when it is missing.</summary>
    public static string? Locate(AppPaths paths, AppSettings settings) => ModelFolders.Find(Embedding.FileName, Embedding.Bytes, paths, settings);
}
