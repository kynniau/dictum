using Dictum.Audio;

namespace Dictum.Calibration;

/// <summary>
/// The calibration window's state and rules, apart from WPF (SPEC §16.5): which controls may act, a take's length limit,
/// and which results still belong to the recordings on disk.
/// </summary>
internal sealed class CalibrationSession
{
    /// <summary>A take longer than this is discarded: each sentence takes about 5-10 s to read, so the rest is room sound.</summary>
    public static readonly TimeSpan TakeLimit = TimeSpan.FromSeconds(30);

    private readonly List<ComboResult> _results = [];
    private readonly HashSet<string> _downloading = new(StringComparer.Ordinal);
    private string? _resultsClipSet; // the recordings the results were measured on

    /// <summary>The sentence being recorded (1-based), or 0.</summary>
    public int Recording { get; private set; }

    public bool Running { get; private set; }

    public IReadOnlyList<ComboResult> Results => _results;

    /// <summary>Never mid-take: the open microphone would keep recording through the test and be saved as that sentence.</summary>
    public bool CanRun => !Running && Recording == 0;

    /// <summary>The take in progress can always be stopped; another can start only when nothing records or runs.</summary>
    public bool CanRecord(int index) => Recording == index || (!Running && Recording == 0);

    /// <summary>Not during a run (its results would come back for the deleted audio) or a take.</summary>
    public bool CanDelete => !Running && Recording == 0;

    public bool IsDownloading(string model) => _downloading.Contains(model);

    public void StartTake(int index) => Recording = index;

    public void EndTake() => Recording = 0;

    public void SetRunning(bool running) => Running = running;

    public void StartDownload(string model) => _downloading.Add(model);

    public void EndDownload(string model) => _downloading.Remove(model);

    /// <summary>Why a finished take is not kept, or null to keep it.</summary>
    public static string? RejectTake(CapturedAudio audio, double silenceThreshold) =>
        audio.Samples.Length > TakeLimit.TotalSeconds * MonoResampler.WhisperRate
            ? $"longer than {TakeLimit.TotalSeconds:0} s"
            : RecordingGate.RejectReason(audio, 1000, silenceThreshold);

    /// <summary>A take was saved or the recordings deleted: results measured on other audio no longer apply.</summary>
    public void RecordingsChanged()
    {
        _results.Clear();
        _resultsClipSet = null;
    }

    /// <summary>A finished combination from a run over the recordings identified by <paramref name="clipSet"/>; results from other recordings go first.</summary>
    public void Add(ComboResult combo, string clipSet)
    {
        if (_resultsClipSet != clipSet)
        {
            _results.Clear();
            _resultsClipSet = clipSet;
        }
        _results.RemoveAll(r => r.Model == combo.Model && r.BeamSize == combo.BeamSize);
        _results.Add(combo);
    }

    /// <summary>
    /// The models a run after "Download and include" covers: just the new one when the results are for these recordings,
    /// otherwise every installed model, so one recommendation is never drawn across different audio.
    /// </summary>
    public IReadOnlyList<SpeechCandidate> ModelsToRun(IReadOnlyList<SpeechCandidate> installed, string added, string clipSet) =>
        _resultsClipSet == clipSet && _results.Count > 0 ? [.. installed.Where(m => m.Name == added)] : installed;
}
