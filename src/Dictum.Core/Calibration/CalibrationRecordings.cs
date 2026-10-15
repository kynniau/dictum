using Dictum.Audio;
using Dictum.Personal;
using Dictum.Settings;
using NAudio.Wave;

namespace Dictum.Calibration;

/// <summary>
/// The calibration sentences (shipped as calibration-sentences.txt beside Dictum.exe) and the user's recordings of
/// them in %LOCALAPPDATA%\Dictum\calibration (clip-01.wav …), kept so the test can be re-run when a model is added
/// and removed by "Delete recordings". The recordings never leave the machine.
/// </summary>
public sealed class CalibrationRecordings(string directory, IReadOnlyList<string> script)
{
    public static CalibrationRecordings For(AppPaths paths, Action<string>? warn = null) =>
        new(paths.CalibrationDir, TextList.Read(paths.CalibrationScriptFile, warn));

    public IReadOnlyList<string> Script { get; } = script;

    public string Directory { get; } = directory;

    /// <summary>1-based, as shown in the window.</summary>
    public string PathFor(int index) => System.IO.Path.Combine(Directory, $"clip-{index:00}.wav");

    public bool Has(int index) => File.Exists(PathFor(index));

    /// <summary>Which sentence each take was recorded for ("03&lt;TAB&gt;sentence" per line), so a reworded script is noticed.</summary>
    public string ManifestFile => System.IO.Path.Combine(Directory, "takes.txt");

    /// <summary>
    /// Writes the recording as 16 kHz mono 16-bit PCM (NAudio's WaveFileWriter), replacing an earlier take, and notes the
    /// sentence it was recorded for. A failed write leaves no partial recording behind.
    /// </summary>
    public void Save(int index, CapturedAudio audio)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = PathFor(index) + ".tmp";
        var samples = audio.Samples.Select(s => Math.Clamp(s, -1f, 1f)).ToArray(); // a resampler overshoot must not wrap round in 16-bit
        try
        {
            using (var writer = new WaveFileWriter(temp, new WaveFormat(MonoResampler.WhisperRate, 16, 1)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }
            File.Move(temp, PathFor(index), overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        var takes = Manifest();
        takes[index] = Script[index - 1];
        File.WriteAllLines(ManifestFile, takes.OrderBy(t => t.Key).Select(t => $"{t.Key:00}\t{t.Value}"));
    }

    /// <summary>Every sentence that has a current recording, read back as 16 kHz mono (stale takes are left out).</summary>
    public IReadOnlyList<CalibrationClip> LoadClips()
    {
        var stale = StaleTakes();
        var clips = new List<CalibrationClip>();
        for (var i = 1; i <= Script.Count; i++)
        {
            if (Has(i) && !stale.Contains(i)) clips.Add(new CalibrationClip(i, Script[i - 1], AudioFiles.ReadWav(PathFor(i)).Samples));
        }
        return clips;
    }

    /// <summary>
    /// Recorded sentences whose take was made for a different sentence text (the script changed since). Takes from before
    /// the manifest existed have no entry and are taken as current.
    /// </summary>
    public IReadOnlyList<int> StaleTakes()
    {
        var takes = Manifest();
        return [.. Enumerable.Range(1, Script.Count).Where(i => Has(i) && takes.TryGetValue(i, out var said) && said != Script[i - 1])];
    }

    /// <summary>Identifies the recordings on disk (sentence and time of each take), so results can be matched to them.</summary>
    public string ClipSet() =>
        string.Join(";", Enumerable.Range(1, Script.Count).Where(Has).Select(i => $"{i}:{File.GetLastWriteTimeUtc(PathFor(i)).Ticks}"));

    private Dictionary<int, string> Manifest()
    {
        var takes = new Dictionary<int, string>();
        if (!File.Exists(ManifestFile)) return takes;
        foreach (var line in File.ReadAllLines(ManifestFile))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0 && int.TryParse(line[..tab], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i)) takes[i] = line[(tab + 1)..];
        }
        return takes;
    }

    /// <summary>Deletes every recording, leftovers of failed saves and the manifest (and the folder if nothing else is in it). Returns how many recordings were deleted.</summary>
    public int DeleteAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        var files = System.IO.Directory.GetFiles(Directory, "clip-*");
        foreach (var f in files) File.Delete(f);
        TryDelete(ManifestFile);
        if (System.IO.Directory.GetFileSystemEntries(Directory).Length == 0) System.IO.Directory.Delete(Directory);
        return files.Count(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // gone already, or held open: "Delete recordings" reports what it removed
        }
    }
}
