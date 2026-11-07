using Dictum.Settings;

namespace Dictum.Meetings.Speakers;

/// <summary>
/// The two model files behind "Identify speakers" (design 1.6 B), each downloaded on first use through the existing pinned
/// download path (ModelCoordinator.ApplyWithDownloads, ModelDownloader): huggingface.co only, a pinned commit, the exact size
/// and the LFS SHA-256, refused under offline_only. Sizes and hashes were read from the Hugging Face API tree at each pinned
/// revision (https://huggingface.co/api/models/&lt;repo&gt;/tree/&lt;revision&gt;, the lfs.oid and lfs.size of the file) on
/// <see cref="PinsRead"/>. Neither file is bundled or committed.
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

    /// <summary>
    /// pyannote segmentation-3.0 converted to ONNX by sherpa-onnx (huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0,
    /// commit 9403a6902bb58e3d5ae8c7e77c3422de279db2e0, lastModified 08/10/2024; its LICENSE file: MIT, Copyright (c) 2022 CNRS).
    /// The repository calls the file model.onnx; Dictum saves it under a name that says what it is, so it cannot be mistaken for
    /// another model in the shared models folder.
    /// </summary>
    public static DownloadSpec Segmentation { get; } = new(
        new Uri("https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx"),
        "pyannote-segmentation-3.0.onnx", 5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079");

    public static IReadOnlyList<DownloadSpec> All { get; } = [Segmentation, Embedding];

    /// <summary>
    /// Speaker models excluded on licence (design 1.6 B), never to be added: Rev's reverb-diarization (non-production licence),
    /// DiariZen (CC BY-NC), WeSpeaker SimAM and VoxBlink2 models (non-commercial), ReDimNet (no weights licence). A test pins that no
    /// model file name or URL here contains any of them.
    /// </summary>
    public static IReadOnlyList<string> Excluded { get; } = ["reverb", "diarizen", "simam", "voxblink", "redimnet"];

    /// <summary>The spec for this file name (any folder), or null.</summary>
    public static DownloadSpec? FindByFile(string path) =>
        All.FirstOrDefault(s => string.Equals(s.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>"&lt;file&gt;@&lt;first 8 of its SHA-256&gt;": what speakers.json.enc records as the model that made it.</summary>
    public static string Tag(DownloadSpec spec) => $"{spec.FileName}@{spec.Sha256![..8]}";

    /// <summary>Both files in the model folders at their pinned size, or null for each one missing.</summary>
    public static (string? Segmentation, string? Embedding) Locate(AppPaths paths, AppSettings settings) =>
        (ModelFolders.Find(Segmentation.FileName, Segmentation.Bytes, paths, settings),
         ModelFolders.Find(Embedding.FileName, Embedding.Bytes, paths, settings));
}
