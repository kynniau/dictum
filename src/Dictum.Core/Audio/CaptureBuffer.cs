namespace Dictum.Audio;

/// <summary>A finished recording: 16 kHz mono float32 plus the loudest block level seen.</summary>
public sealed record CapturedAudio(float[] Samples, float PeakLevel)
{
    public static readonly CapturedAudio Empty = new([], 0f);

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)MonoResampler.WhisperRate);
}

/// <summary>
/// Accumulates decoded device audio as 16 kHz mono and meters it: RMS per 20 ms block drives the pill,
/// and the loudest block RMS is the "peak level" compared with silence_threshold.
/// Thread-safe: written from the capture thread, read from the controller and UI.
/// </summary>
public sealed class CaptureBuffer
{
    private const int BlockSamples = MonoResampler.WhisperRate / 50; // 20 ms

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

    public CaptureBuffer(SampleEncoding encoding, int channels, int sampleRate)
    {
        _encoding = encoding;
        _channels = Math.Max(1, channels);
        _resampler = new MonoResampler(sampleRate);
    }

    /// <summary>Most recent 20 ms RMS, 0-1. Read by the pill's level meter.</summary>
    public float CurrentLevel => Volatile.Read(ref _level);

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
            _blockSumSquares = 0;
            _blockCount = 0;
        }
    }

    /// <summary>Stops accepting audio and returns everything captured.</summary>
    public CapturedAudio Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            Volatile.Write(ref _level, 0f);
            return new CapturedAudio([.. _samples], _peak);
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
