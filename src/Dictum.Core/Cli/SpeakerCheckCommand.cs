using Dictum.Core;
using Dictum.Meetings.Speakers;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Cli;

/// <summary>
/// Dictum.exe --speaker-check &lt;folder&gt; (design 1.6 B): <see cref="SpeakerCheck"/> with the downloaded speaker model, held to its
/// pinned size and SHA-256 first. Makes no network call. Exit 0, or 1 when the model is missing or nothing could be scored.
/// </summary>
public static class SpeakerCheckCommand
{
    public const string PROPOSED_Usage =
        "Dictum.exe --speaker-check <folder>      separate the voices in each <name>.wav (16 kHz mono) of the folder and print the DER " +
        "against its <name>.rttm (0.25 s collar, overlap scored), and the EER of the pairs in trials.txt (\"<1|0> <a.wav> <b.wav>\")";

    public static int Run(CliOptions options, AppPaths paths, ILog log)
    {
        var settings = SettingsLoader.LoadFile(paths.SettingsFile).Settings;
        if (SpeakerModels.Locate(paths, settings) is not { } embedding)
        {
            Console.Error.WriteLine($"error: the speaker model is not in the models folder ({SpeakerModels.Embedding.FileName}). Download it in Settings > Meetings.");
            return 1;
        }
        new ModelIntegrity(paths.VerifiedModelsFile, log, () => ModelFolders.LookupOrder(paths, settings)).Verify(embedding);
        using var embedder = new SherpaVoiceEmbedder(embedding, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        return SpeakerCheck.Run(options.WavPath!, embedder, Console.Out);
    }
}
