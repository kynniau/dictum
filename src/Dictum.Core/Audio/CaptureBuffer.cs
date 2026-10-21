namespace Dictum.Audio;

/// <summary>A finished recording: 16 kHz mono float32 plus the loudest block level seen.</summary>
public sealed record CapturedAudio(float[] Samples, float PeakLevel)
{
    public static readonly CapturedAudio Empty = new([], 0f);

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)MonoResampler.WhisperRate);
}

/// <summary>
/// Accumulates decoded device audio as 16 kHz mono and meters it: RMS per 20 ms block drives the pill,
/// and the loudest block RMS is the "peak level" compared with silence_threshold. In hands-free it also cuts the recording
/// at pauses (<see cref="SegmentAtPauses"/>) and hands each finished part over, keeping only what follows the last cut.
/// Thread-safe: written from the capture thread, read from the controller and UI.
/// </summary>
public sealed class CaptureBuffer
{
    /// <summary>Samples in one 20 ms block (<see cref="SpeechSegmenter.BlockMs"/>): the unit of metering and of every cut.</summary>
    public const int BlockSamples = MonoResampler.WhisperRate * SpeechSegmenter.BlockMs / 1000;

    /// <summary>
    /// A 20 ms block louder than this (RMS, 0-1) is a sound from the microphone, for the "No sound from mic" warning
    /// (21/10/2026). 1e-5 is the 20 ms block RMS after decoding, downmixing and resampling to 16 kHz, not the level at the
    /// device: resampling 48 kHz white noise to 16 kHz drops it by about 6 dB (mono) or 9 dB (stereo, after the downmix).
    /// Measured by the PR #22 lens on 21/10/2026 through <see cref="Append"/> with 16-bit Gaussian noise at 48 kHz, and pinned
    /// in SilenceWarningTests at its clear ends: digital zeros, and white noise quieter than about -91 dBFS (stereo) or
    /// -94 dBFS (mono) at the device, read as no sound; -84 dBFS passes. Not measured on real microphones: the warning's log
    /// line gives each session's peak block RMS, so real noise floors can be read from logs. A microphone whose own noise
    /// suppression sends exact zeros between words reads as silent until the speaker talks.
    /// </summary>
    public const float SoundFloor = 1e-5f;

    private readonly object _gate = new();
    private readonly List<float> _samples = new(MonoResampler.WhisperRate * 30);
    private readonly SampleEncoding _encoding;
    private readonly int _channels;
    private readonly MonoResampler _resampler;
    private float[] _decoded = [];
    private float[] _mono = [];
    private double _blockSumSquares;
    private int _blockCount;
    private float _peak;
    private float _level;
    private bool _sealed;
    private long _blocks;          // whole blocks measured since the start
    private long _firstBlock;      // the block _samples[0] starts (parts before it were handed over)
    private SpeechSegmenter? _segmenter;
    private Action<CapturedAudio, CutReason>? _onPart;
    private readonly Action? _firstSound;
    private bool _heard;

    /// <param name="firstSound">Called once, on the capture thread and under this buffer's lock, at the first block louder than
    /// <see cref="SoundFloor"/>. Must not block or call back into the buffer.</param>
    public CaptureBuffer(SampleEncoding encoding, int channels, int sampleRate, Action? firstSound = null)
    {
        _firstSound = firstSound;
        _encoding = encoding;
        _channels = Math.Max(1, channels);
        _resampler = new MonoResampler(sampleRate);
    }

    /// <summary>Most recent 20 ms RMS, 0-1. Read by the pill's level meter.</summary>
    public float CurrentLevel => Volatile.Read(ref _level);

    /// <summary>The loudest 20 ms block RMS so far this recording (0 before the first block), for the no-sound log line.</summary>
    public float PeakLevel
    {
        get { lock (_gate) return _peak; }
    }

    /// <summary>Whole 20 ms blocks measured so far this recording (0: no audio has arrived).</summary>
    public long BlocksMeasured
    {
        get { lock (_gate) return _blocks; }
    }

    public int SampleCount
    {
        get { lock (_gate) return _samples.Count; }
    }

    /// <summary>Appends raw device bytes (whole frames). Ignored after <see cref="Seal"/>.</summary>
    public void Append(ReadOnlySpan<byte> raw)
    {
        lock (_gate)
        {
            if (_sealed) return;
            var sampleCount = raw.Length / SampleDecoder.BytesPerSample(_encoding);
            if (_decoded.Length < sampleCount) _decoded = new float[sampleCount];
            var decoded = SampleDecoder.Decode(raw, _encoding, _decoded);
            var frames = decoded / _channels;
            if (_mono.Length < frames) _mono = new float[frames];
            var mono = SampleDecoder.Downmix(_decoded.AsSpan(0, decoded), _channels, _mono);
            _resampler.Process(_mono.AsSpan(0, mono), AddResampled);
        }
    }

    /// <summary>Appends samples that are already 16 kHz mono (used by tests and the WAV path).</summary>
    public void AppendResampled(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (!_sealed) AddResampled(samples);
        }
    }

    /// <summary>
    /// Hands-free (SPEC §3 amendment, 16/10/2026): from now on, cut the recording at pauses (<see cref="SpeechSegmenter"/>) and
    /// hand each finished part to <paramref name="part"/>, in order, on the capture thread and under this buffer's lock, so
    /// every part cut before <see cref="Seal"/> has been handed over by the time Seal returns what follows the last cut. The
    /// audio already captured belongs to the first part. <paramref name="part"/> must not block or call back into the buffer.
    /// A second call, or a call after Seal, does nothing.
    /// </summary>
    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part)
    {
        lock (_gate)
        {
            if (_sealed || _segmenter is not null) return;
            _segmenter = new SpeechSegmenter(silenceThreshold, _blocks, _peak);
            _onPart = part;
        }
    }

    private void AddResampled(ReadOnlySpan<float> chunk)
    {
        foreach (var s in chunk)
        {
            _samples.Add(s);
            _blockSumSquares += s * s;
            if (++_blockCount < BlockSamples) continue;
            var rms = (float)Math.Sqrt(_blockSumSquares / _blockCount);
            Volatile.Write(ref _level, rms);
            if (rms > _peak) _peak = rms;
            if (!_heard && rms > SoundFloor)
            {
                _heard = true;
                _firstSound?.Invoke();
            }
            _blockSumSquares = 0;
            _blockCount = 0;
            _blocks++;
            if (_segmenter?.Add(rms) is { } cut) HandOver(cut);
        }
    }

    /// <summary>Hands over every sample before the cut's block (whole blocks) and keeps the rest, which starts the next part.</summary>
    private void HandOver(SegmentCut cut)
    {
        var count = checked((int)((cut.Block - _firstBlock) * BlockSamples));
        var part = new float[count];
        _samples.CopyTo(0, part, 0, count);
        _samples.RemoveRange(0, count);
        _firstBlock = cut.Block;
        _onPart!(new CapturedAudio(part, cut.Peak), cut.Reason);
    }

    /// <summary>Stops accepting audio and returns everything captured (in hands-free, what follows the last cut).</summary>
    public CapturedAudio Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            Volatile.Write(ref _level, 0f);
            return new CapturedAudio([.. _samples], _segmenter?.TailPeak ?? _peak);
        }
    }
}

/// <summary>SPEC §3: too-short or silent recordings are discarded, never transcribed.</summary>
public static class RecordingGate
{
    public static string? RejectReason(CapturedAudio audio, int minRecordingMs, double silenceThreshold)
    {
        if (audio.Duration.TotalMilliseconds < minRecordingMs)
            return $"too short ({audio.Duration.TotalMilliseconds:0} ms < {minRecordingMs} ms)";
        if (audio.PeakLevel < silenceThreshold)
            return $"silent (peak level {audio.PeakLevel:0.0000} < {silenceThreshold})";
        return null;
    }
}
