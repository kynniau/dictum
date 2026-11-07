using Dictum.Calibration;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Settings;

namespace Dictum.Meetings.Voice;

/// <summary>
/// The two ways to make a print (design 1.6 C.1) and deleting it (C.3). A print is written only when the gates pass; a refusal
/// writes nothing and leaves an earlier print as it was.
/// </summary>
public static class VoiceEnrolment
{
    /// <summary>The prefix of the temporary recording folders under the data folder ("Record my voice now").</summary>
    private const string TemporaryPrefix = "voice-enrolment-";

    /// <summary>From the calibration recordings the user already chose to keep (current takes only); they stay exactly as they are.</summary>
    public static EnrolmentResult FromCalibration(AppPaths paths, IVoiceEmbedder? embedder, IMeetingKeyring keyring, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (embedder is null) return Missing();
        return Seal(VoicePrintBuilder.Build(CalibrationRecordings.For(paths).LoadClips().Select(c => c.Samples), embedder, VoicePrintStore.CurrentModel, now, cancellationToken),
            VoicePrintStore.PathFor(paths), keyring);
    }

    /// <summary>
    /// A new, empty folder under the data folder for "Record my voice now" (the calibration screen records into it). Any folder an
    /// earlier attempt left behind (Dictum closed mid-attempt) is deleted first.
    /// </summary>
    public static string NewTemporaryFolder(AppPaths paths)
    {
        DeleteLeftovers(paths);
        var folder = Path.Combine(paths.LocalDir, TemporaryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Deletes every temporary recording folder left under the data folder; returns how many.</summary>
    public static int DeleteLeftovers(AppPaths paths)
    {
        if (!Directory.Exists(paths.LocalDir)) return 0;
        var count = 0;
        foreach (var folder in Directory.GetDirectories(paths.LocalDir, TemporaryPrefix + "*"))
        {
            DeleteFolder(folder);
            count++;
        }
        return count;
    }

    /// <summary>
    /// The end of a "Record my voice now" attempt: a print from the clips in <paramref name="folder"/> (the calibration screen's
    /// clip-NN.wav), then the folder is deleted, whatever the outcome: made, refused, the model missing, or an exception.
    /// </summary>
    public static EnrolmentResult FromTemporaryFolder(AppPaths paths, string folder, IReadOnlyList<string> script, IVoiceEmbedder? embedder,
        IMeetingKeyring keyring, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        try
        {
            if (embedder is null) return Missing();
            var clips = new CalibrationRecordings(folder, script).LoadClips().Select(c => c.Samples).ToList();
            return Seal(VoicePrintBuilder.Build(clips, embedder, VoicePrintStore.CurrentModel, now, cancellationToken), VoicePrintStore.PathFor(paths), keyring);
        }
        finally { DeleteFolder(folder); }
    }

    /// <summary>
    /// Delete my voice print (C.3): the file goes and both uses are turned off in settings.json (meetings.use_voice_print and
    /// audio.your_voice_only). The settings are written first: when settings.json cannot be written, nothing changes and the card
    /// says so, rather than the file going while both switches still read on.
    /// </summary>
    /// <param name="write">Writes the settings keys; false when settings.json could not be written (the print is then kept).</param>
    public static bool Delete(AppPaths paths, Func<IReadOnlyList<(string Key, bool Value)>, bool> write)
    {
        if (!write([(UseInMeetingsKey, false), (YourVoiceOnlyKey, false)])) return false;
        VoicePrintStore.Delete(VoicePrintStore.PathFor(paths));
        return true;
    }

    public const string UseInMeetingsKey = "meetings.use_voice_print";
    public const string YourVoiceOnlyKey = "audio.your_voice_only";

    /// <summary>A made print is sealed and written; a refusal writes nothing.</summary>
    private static EnrolmentResult Seal(EnrolmentResult result, string path, IMeetingKeyring keyring)
    {
        if (result.Print is { } print) VoicePrintStore.Write(path, print, keyring);
        return result;
    }

    private static EnrolmentResult Missing() => new(EnrolmentOutcome.ModelMissing, null, 0, 0, 0);

    private static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder, recursive: true);
    }
}
