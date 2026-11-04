using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dictum.Windows.Audio;

/// <summary>Default render endpoint, independent of the dictation microphone.</summary>
public sealed class LoopbackCapture(ILog log) : ITimestampedCapture, IDisposable
{
    private readonly object _sync = new();
    private readonly object _lifecycle = new();
    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private Timer? _timer;
    private Stopwatch? _clock;
    private LoopbackAudioBuffer? _audio;
    private TimeSpan _origin;
    private int _rate, _align;
    private bool _running, _followDefault;
    private int _failed;
    public event Action<Exception>? Failed;
    public float CurrentLevel { get { lock (_sync) return _audio?.CurrentLevel ?? 0; } }
    public CaptureProgress Progress { get { lock (_sync) return _audio?.Progress ?? new(CaptureActivity.Silent, TimeSpan.Zero); } }

    public void Start(string? deviceSetting, long keyDownStamp) => Start(deviceSetting, Stopwatch.StartNew());

    public void Start(string? deviceSetting, Stopwatch sessionClock)
    {
        lock (_lifecycle)
        {
            DisposeDevice();
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _followDefault = string.IsNullOrWhiteSpace(deviceSetting);
                _device = _followDefault
                    ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : _enumerator.GetDevice(deviceSetting);
                _capture = new WasapiLoopbackCapture(_device);
                var format = _capture.WaveFormat;
                lock (_sync)
                {
                    _clock = sessionClock;
                    _origin = sessionClock.Elapsed;
                    _rate = format.SampleRate;
                    _align = format.BlockAlign;
                    _audio = new LoopbackAudioBuffer(format, _origin);
                    _failed = 0;
                    _running = true;
                }
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _capture.StartRecording();
                _timer = new Timer(_ => Pulse(), null, 100, 100);
                log.Info($"Loopback started: {format.SampleRate} Hz, {format.Channels} channels.");
            }
            catch
            {
                DisposeDevice();
                throw;
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        try
        {
            lock (_sync)
            {
                if (!_running) return;
                var frames = args.BytesRecorded / _align;
                // simplification: legacy WasapiLoopbackCapture exposes no packet QPC positions. Place packets at
                // callback time minus duration, bounded by already synthesised audio; retire with a QPC-capable loopback API.
                FillSilence(_clock!.Elapsed - TimeSpan.FromSeconds(frames / (double)_rate));
                var packetStart = _clock!.Elapsed - TimeSpan.FromSeconds(frames / (double)_rate);
                if (packetStart < _origin) packetStart = _origin;
                _audio!.Append(args.Buffer.AsSpan(0, frames * _align), packetStart);
            }
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void Pulse()
    {
        try
        {
            // Poll endpoint identity off the audio thread. Disappearance and a new default both end this session.
            lock (_lifecycle)
            {
                if (_capture is null) return;
                if (_followDefault)
                {
                    using var current = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (current.ID != _device!.ID)
                        throw new InvalidOperationException("The default output device changed.");
                }
                if (_device!.State != DeviceState.Active)
                    throw new InvalidOperationException("The output device vanished or became unavailable.");
                lock (_sync)
                {
                    // Leave one endpoint buffer for late packets before filling an absent packet with silence.
                    // simplification: a fixed 200 ms packet grace bounds silence synthesis latency; retire after device latency measurements.
                    if (_running) FillSilence(_clock!.Elapsed - TimeSpan.FromMilliseconds(200));
                }
            }
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void FillSilence(TimeSpan through) => _audio!.FillSilence(through);

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is { } error) ReportFailure(error);
        else if (Volatile.Read(ref _running)) ReportFailure(new InvalidOperationException("The output capture stopped unexpectedly."));
    }

    private void ReportFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        log.Error($"Loopback stopped: {error.GetType().Name}: {error.Message}");
        // Stop away from NAudio's callback thread, which Dispose may join.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lifecycle) DisposeDevice();
            Failed?.Invoke(error);
        });
    }

    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part) =>
        SegmentAtPauses(silenceThreshold, (audio, reason, _) => part(audio, reason));

    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason, TimeSpan> part)
    {
        lock (_sync) _audio?.SegmentAtPauses(silenceThreshold, part);
    }

    public CapturedAudio Stop() => StopTimestamped().Part;

    public (CapturedAudio Part, TimeSpan Offset) StopTimestamped()
    {
        lock (_lifecycle)
        {
            DisposeDevice();
            lock (_sync)
            {
                if (_audio is not null && _clock is not null && _failed == 0) FillSilence(_clock.Elapsed);
                return _audio?.Seal() ?? (CapturedAudio.Empty, TimeSpan.Zero);
            }
        }
    }

    private void DisposeDevice()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_sync) _running = false;
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            try { capture.StopRecording(); }
            catch (Exception ex) { log.Debug($"Loopback stop: {ex.GetType().Name}"); }
            try { capture.Dispose(); }
            catch (Exception ex) { log.Debug($"Loopback release: {ex.GetType().Name}"); }
        }
        try { _device?.Dispose(); }
        catch (Exception ex) { log.Debug($"Loopback endpoint release: {ex.GetType().Name}"); }
        _device = null;
        try { _enumerator?.Dispose(); }
        catch (Exception ex) { log.Debug($"Loopback enumerator release: {ex.GetType().Name}"); }
        _enumerator = null;
    }

    public void Dispose() { lock (_lifecycle) DisposeDevice(); }
}

/// <summary>Managed streaming decode/downmix/resample path shared with microphone capture.
/// Quiet blocks are delivered too, so an idle track remains continuous without holding speech behind it.</summary>
internal sealed class LoopbackAudioBuffer
{
    private readonly SampleEncoding _encoding;
    private readonly int _channels, _inputRate, _align;
    private long _inputFrames;
    private readonly MonoResampler _resampler;
    private readonly TimeSpan _origin;
    private readonly List<float> _pending = new();
    private readonly float[] _block = new float[CaptureBuffer.BlockSamples];
    private int _blockCount;
    private long _firstSample, _receivedSamples;
    private long _segmentOrigin;
    private readonly List<(long Sample, TimeSpan At)> _anchors = new();
    private TimeSpan _lastStart;
    private SpeechSegmenter? _segmenter;
    private Action<CapturedAudio, CutReason, TimeSpan>? _part;
    private double _threshold = .01;
    private bool _sealed;
    private CaptureActivity _activity = CaptureActivity.Silent;
    public float CurrentLevel { get; private set; }
    private TimeSpan Start
    {
        get
        {
            var anchor = _anchors.LastOrDefault(a => a.Sample <= _firstSample, (Sample: 0L, At: _origin));
            var at = anchor.At + TimeSpan.FromTicks((_firstSample - anchor.Sample) * 625);
            return at < _lastStart ? _lastStart : at;
        }
    }
    public CaptureProgress Progress => new(_activity, Start);

    public LoopbackAudioBuffer(WaveFormat format, TimeSpan origin)
    {
        _encoding = SampleDecoder.EncodingOf(format);
        _channels = format.Channels;
        _inputRate = format.SampleRate;
        _align = format.BlockAlign;
        _resampler = new MonoResampler(format.SampleRate);
        _origin = origin;
    }

    public void SegmentAtPauses(double threshold, Action<CapturedAudio, CutReason, TimeSpan> part)
    {
        if (_part is not null || _sealed) return;
        _threshold = threshold;
        _part = part;
        _segmenter = new SpeechSegmenter(threshold);
        // Start is normally immediately followed by segmentation. Preserve any earlier buffered audio.
        for (var i = 0; i + CaptureBuffer.BlockSamples <= _pending.Count; i += CaptureBuffer.BlockSamples)
            _segmenter.Add(Rms(_pending.GetRange(i, CaptureBuffer.BlockSamples)));
    }

    public void FillSilence(TimeSpan through)
    {
        if (_sealed) return;
        var target = (long)Math.Floor((through - _origin).TotalSeconds * _inputRate);
        var zeros = new byte[_align * Math.Max(1, _inputRate / 50)];
        while (_inputFrames < target)
        {
            var count = (int)Math.Min(zeros.Length / _align, target - _inputFrames);
            Append(zeros.AsSpan(0, count * _align),
                _origin + TimeSpan.FromTicks(_inputFrames * TimeSpan.TicksPerSecond / _inputRate));
        }
    }

    public void Append(ReadOnlySpan<byte> raw, TimeSpan? packetStart = null)
    {
        if (_sealed) return;
        _inputFrames += raw.Length / _align;
        // Anchor each packet to the session clock; sample counts locate boundaries within that packet only.
        // This prevents device-rate drift from accumulating over a long meeting.
        if (packetStart is { } at) _anchors.Add((_receivedSamples, at));
        var decoded = new float[raw.Length / SampleDecoder.BytesPerSample(_encoding)];
        var count = SampleDecoder.Decode(raw, _encoding, decoded);
        var mono = new float[count / _channels];
        var frames = SampleDecoder.Downmix(decoded.AsSpan(0, count), _channels, mono);
        _resampler.Process(mono.AsSpan(0, frames), samples =>
        {
            foreach (var sample in samples)
            {
                _receivedSamples++;
                _pending.Add(sample);
                _block[_blockCount++] = sample;
                if (_blockCount != _block.Length) continue;
                _blockCount = 0;
                CurrentLevel = Rms(_block);
                _activity = CurrentLevel >= _threshold ? CaptureActivity.Speech : CurrentLevel == 0 ? CaptureActivity.Silent : CaptureActivity.Idle;
                if (_segmenter is null) continue;
                if (_segmenter.Add(CurrentLevel) is { } cut)
                {
                    var countToCut = checked((int)(_segmentOrigin + cut.Block * _block.Length - _firstSample));
                    Deliver(countToCut, cut.Reason);
                }
                // Only advance idle progress after delivering ALL buffered quiet samples. Speech, including a short
                // utterance below MinSpeechBlocks, stays buffered and pins PendingStart until its actual cut.
                if (_pending.Count > 0 && _pending.All(s => Math.Abs(s) < _threshold))
                {
                    Deliver(_pending.Count, CutReason.Pause);
                    ResetSegmenter();
                }
            }
        });
    }

    private void ResetSegmenter()
    {
        _segmentOrigin = _firstSample;
        _segmenter = new SpeechSegmenter(_threshold);
        for (var i = 0; i + _block.Length <= _pending.Count; i += _block.Length)
            _segmenter.Add(Rms(_pending.GetRange(i, _block.Length)));
    }

    private void Deliver(int count, CutReason reason)
    {
        var samples = _pending.GetRange(0, count).ToArray();
        var offset = Start;
        _pending.RemoveRange(0, count);
        _firstSample += count;
        _lastStart = offset;
        while (_anchors.Count > 1 && _anchors[1].Sample <= _firstSample) _anchors.RemoveAt(0);
        _part!(new CapturedAudio(samples, Peak(samples)), reason, offset);
    }

    public (CapturedAudio Part, TimeSpan Offset) Seal()
    {
        if (_sealed) return (CapturedAudio.Empty, Start);
        _sealed = true;
        CurrentLevel = 0;
        var result = (new CapturedAudio(_pending.ToArray(), Peak(_pending)), Start);
        _firstSample += _pending.Count;
        _pending.Clear();
        return result;
    }

    private static float Rms(IEnumerable<float> samples)
    {
        double sum = 0; var count = 0;
        foreach (var sample in samples) { sum += sample * sample; count++; }
        return count == 0 ? 0 : (float)Math.Sqrt(sum / count);
    }
    private static float Peak(IReadOnlyList<float> samples)
    {
        float peak = 0;
        for (var i = 0; i < samples.Count; i += CaptureBuffer.BlockSamples)
            peak = Math.Max(peak, Rms(samples.Skip(i).Take(CaptureBuffer.BlockSamples)));
        return peak;
    }
}
