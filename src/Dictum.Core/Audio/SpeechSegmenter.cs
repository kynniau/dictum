namespace Dictum.Audio;

/// <summary>Why a hands-free part ended.</summary>
public enum CutReason
{
    /// <summary>A pause: <see cref="SpeechSegmenter.PauseBlocks"/> quiet blocks after <see cref="SpeechSegmenter.MinSpeechBlocks"/> of speech.</summary>
    Pause,

    /// <summary>The part reached <see cref="SpeechSegmenter.LimitBlocks"/> with no pause: cut at the quietest recent block.</summary>
    Limit,

    /// <summary>The recording stopped (the stop tap, the auto-stop, a lost microphone): what followed the last cut.</summary>
    Stop,
}

/// <param name="Block">
/// The first 20 ms block of the next part, counted from the start of the recording. The part that ends here is every block
/// from the previous cut up to, not including, this one, so the parts tile the recording with nothing lost or repeated.
/// </param>
/// <param name="Reason">Why the part ended.</param>
/// <param name="Peak">The loudest block RMS of the part that ends here (what <see cref="RecordingGate"/> compares with silence_threshold).</param>
public readonly record struct SegmentCut(long Block, CutReason Reason, float Peak);

/// <summary>
/// Hands-free "segments as you speak" (product owner, 16/10/2026): reads the 20 ms block levels <see cref="CaptureBuffer"/>
/// already measures and says where to cut the recording, so each part can be typed while the speaker goes on. A part ends at
/// a pause of <see cref="PauseBlocks"/> quiet blocks once it holds <see cref="MinSpeechBlocks"/> blocks of speech, cut in the
/// middle of the pause so both sides keep some quiet; with no such pause it ends at <see cref="LimitBlocks"/>, cut at the
/// quietest block of the last <see cref="LookBackBlocks"/> (a quiet one when there is any), so a word is not split. A block is
/// quiet below the larger of silence_threshold and <see cref="RelativeFloor"/> of the recent speech level. Runs on the
/// capture thread: each block costs O(1) and allocates nothing (two fixed rings of the last <see cref="LookBackBlocks"/>
/// blocks); only a cut scans the look-back. Not thread-safe: the capture buffer calls it under its own lock.
/// </summary>
// simplification: the timing constants below are fixed, not settings. Ceiling: a speaker who pauses for 0.7 s mid-sentence
// gets a part boundary there (the clean-up then usually ends that part with a full stop), and one who never pauses for
// 0.7 s gets 30 s parts. Trigger to retire: the log's "Segment N: …" lines showing most parts cut at the 30 s limit, or
// users reporting sentences split at thinking pauses; then measure and tune, or make them settings.
public sealed class SpeechSegmenter
{
    /// <summary>One block: CaptureBuffer's level meter interval.</summary>
    public const int BlockMs = 20;

    /// <summary>0.7 s of quiet ends a part: longer than the gaps between words, short enough to type each sentence soon after it.</summary>
    public const int PauseBlocks = 35;

    /// <summary>2 s of speech before a pause may end a part: enough words for whisper to transcribe well on its own.</summary>
    public const int MinSpeechBlocks = 100;

    /// <summary>30 s, whisper's own window: a part with no pause is cut here.</summary>
    public const int LimitBlocks = 1500;

    /// <summary>The 30 s cut looks back 3 s for its quietest block, about one sentence's worth of gaps between words.</summary>
    public const int LookBackBlocks = 150;

    /// <summary>
    /// A block is quiet below a tenth (−20 dB) of the recent speech level, or below silence_threshold when that is higher.
    /// silence_threshold alone (0.01 by default) misses every pause in a room whose noise is above it (a laptop fan, an open
    /// office), which would leave only 30 s cuts; the relative floor finds those pauses, and silence_threshold stays the
    /// lower bound, so a quiet speaker in a silent room is measured exactly as the existing setting says.
    /// </summary>
    // simplification: one fixed fraction of a decaying peak, not a noise-floor estimate. Ceiling: the floor decays with the
    // level through the pause, so the 35th quiet block is measured against 0.1 × 0.986^34 ≈ 0.062 of the loudest recent block
    // (about −24 dB); room noise within about 24 dB of the loudest recent block defeats a 0.7 s pause, and only the 30 s cut
    // splits (at the quietest block, which may then be inside a sound). Trigger to retire: parts cut at the limit in a room
    // where the speaker did pause.
    public const float RelativeFloor = 0.1f;

    /// <summary>The recent speech level is the loudest block, decaying to half in about a second (0.986^50 ≈ 0.49).</summary>
    public const float LevelDecay = 0.986f;

    private readonly float _threshold;
    private readonly float[] _rms = new float[LookBackBlocks];
    private readonly bool[] _quiet = new bool[LookBackBlocks];
    private long _block;        // blocks fed so far (the next block's index)
    private long _start;        // the first block of the current part
    private int _speech;        // blocks above the floor in the current part
    private int _quietRun;      // quiet blocks ending at the latest block
    private float _level;       // the recent speech level
    private float _earlierPeak; // the loudest block of the current part no longer in the rings

    /// <param name="silenceThreshold">silence_threshold: the floor's lower bound.</param>
    /// <param name="firstBlock">Blocks the recording already holds (hands-free starts after the first tap): they belong to the first part.</param>
    /// <param name="peakSoFar">The loudest of those blocks.</param>
    public SpeechSegmenter(double silenceThreshold, long firstBlock = 0, float peakSoFar = 0f)
    {
        _threshold = (float)silenceThreshold;
        _block = firstBlock;
        _earlierPeak = peakSoFar;
    }

    /// <summary>Blocks fed so far, from the start of the recording.</summary>
    public long Blocks => _block;

    /// <summary>The loudest block since the last cut: the peak of what the stop hands over.</summary>
    public float TailPeak => PeakOf(_start, _block);

    /// <summary>The level below which the next block counts as quiet.</summary>
    public float Floor => Math.Max(_threshold, RelativeFloor * _level);

    /// <summary>Feeds the next block's RMS; returns where to cut, or null. O(1) unless it cuts (then O(<see cref="LookBackBlocks"/>)).</summary>
    public SegmentCut? Add(float rms)
    {
        var slot = Slot(_block);
        if (_block - LookBackBlocks >= _start) _earlierPeak = Math.Max(_earlierPeak, _rms[slot]); // leaving the ring, still in this part
        var quiet = rms < Floor;
        _rms[slot] = rms;
        _quiet[slot] = quiet;
        _level = Math.Max(rms, _level * LevelDecay);
        _block++;
        if (quiet)
        {
            _quietRun++;
        }
        else
        {
            _quietRun = 0;
            _speech++;
        }

        if (quiet && _quietRun >= PauseBlocks && _speech >= MinSpeechBlocks) return Cut(_block - _quietRun / 2, CutReason.Pause);
        if (_block - _start >= LimitBlocks) return Cut(Quietest(), CutReason.Limit);
        return null;
    }

    /// <summary>
    /// The quietest block of the last <see cref="LookBackBlocks"/>, preferring one that was quiet when it arrived (so the cut is
    /// never inside a sound when the look-back holds a pause); the latest on a tie. Never the part's own first block.
    /// </summary>
    private long Quietest()
    {
        var from = Math.Max(_start + 1, _block - LookBackBlocks);
        var best = -1L;
        var bestQuiet = false;
        var bestRms = float.MaxValue;
        for (var b = _block - 1; b >= from; b--)
        {
            var slot = Slot(b);
            var (quiet, rms) = (_quiet[slot], _rms[slot]);
            if (best < 0 || (quiet && !bestQuiet) || (quiet == bestQuiet && rms < bestRms))
            {
                (best, bestQuiet, bestRms) = (b, quiet, rms);
            }
        }
        return best < 0 ? _block : best; // no candidate only if the limit were shorter than two blocks
    }

    private SegmentCut Cut(long boundary, CutReason reason)
    {
        var peak = PeakOf(_start, boundary);
        var speech = 0; // the next part's speech so far: voiced blocks after the boundary (a 30 s cut can leave some)
        for (var b = boundary; b < _block; b++)
        {
            if (!_quiet[Slot(b)]) speech++;
        }
        _start = boundary;
        _speech = speech;
        _earlierPeak = 0f;
        return new SegmentCut(boundary, reason, peak);
    }

    /// <summary>The loudest block in [from, to) of the current part: the blocks gone from the rings, then the rings.</summary>
    private float PeakOf(long from, long to)
    {
        var peak = _earlierPeak;
        for (var b = Math.Max(from, _block - LookBackBlocks); b < to; b++) peak = Math.Max(peak, _rms[Slot(b)]);
        return peak;
    }

    private static int Slot(long block) => (int)(block % LookBackBlocks);
}
