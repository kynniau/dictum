using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dictum.Windows.Audio;

/// <summary>Default render endpoint, independent of the dictation microphone.</summary>
public sealed class LoopbackCapture : ITimestampedCapture, IDisposable
{
    private readonly ILog _log;
    private readonly Func<string?, ILoopbackCaptureSource> _factory;
    private readonly Action<Action> _queue;
    private readonly Func<TimeSpan>? _now;
    private readonly object _sync = new(), _lifecycle = new();
    private ILoopbackCaptureSource? _capture;
    private EventHandler<WaveInEventArgs>? _data;
    private EventHandler<StoppedEventArgs>? _stopped;
    private ManualResetEventSlim? _drained;
    private Timer? _timer;
    private Stopwatch? _clock;
    private LoopbackAudioBuffer? _audio;
    private readonly List<byte[]> _packets = new();
    private TimeSpan _lastArrival;
    private long _packetFrames;
    private int _rate, _align;
    private long _generation;
    private bool _running, _stopping, _failed, _recordingStarted, _anchored;
    private TimeSpan Now => _now?.Invoke() ?? _clock!.Elapsed;
    public event Action<Exception>? Failed;
    public float CurrentLevel { get { lock (_sync) return _audio?.CurrentLevel ?? 0; } }
    public CaptureProgress Progress
    {
        get
        {
            lock (_sync)
            {
                var progress = _audio?.Progress ?? new(CaptureActivity.Silent, TimeSpan.Zero);
                // Idle progress need not commit zeros that a later drain could cover. Buffered speech
                // still pins the watermark; leave a packet grace before advancing an empty track.
                if (_running && _packets.Count == 0 && _audio is not null && progress.PendingStart >= _audio.End)
                {
                    var idle = Now - TimeSpan.FromMilliseconds(250);
                    if (idle > progress.PendingStart) return new(CaptureActivity.Silent, idle);
                }
                return progress;
            }
        }
    }

    public LoopbackCapture(ILog log) : this(log, setting => new DefaultLoopbackSource(setting)) { }
    internal LoopbackCapture(ILog log, Func<string?, ILoopbackCaptureSource> factory,
        Func<TimeSpan>? now = null, Action<Action>? queue = null)
    {
        _log = log; _factory = factory; _now = now;
        _queue = queue ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    public void Start(string? deviceSetting, long keyDownStamp) => Start(deviceSetting, Stopwatch.StartNew());
    public void Start(string? deviceSetting, Stopwatch sessionClock)
    {
        lock (_lifecycle)
        {
            DisposeDevice();
            var generation = ++_generation;
            try
            {
                _capture = _factory(deviceSetting);
                var format = _capture.WaveFormat;
                _drained = new ManualResetEventSlim();
                lock (_sync)
                {
                    _clock = sessionClock;
                    _rate = format.SampleRate; _align = format.BlockAlign;
                    _audio = new LoopbackAudioBuffer(format, Now);
                    _packets.Clear(); _packetFrames = 0;
                    _failed = _stopping = _anchored = false; _running = true;
                }
                var drained = _drained;
                _data = (_, args) => OnData(generation, args);
                _stopped = (_, args) =>
                {
                    drained.Set();
                    lock (_sync)
                    {
                        if (generation != _generation) return;
                        if (args.Exception is { } error) ReportFailure(generation, error);
                        else if (!_stopping) ReportFailure(generation, new InvalidOperationException("The output capture stopped unexpectedly."));
                    }
                };
                _capture.DataAvailable += _data;
                _capture.RecordingStopped += _stopped;
                _capture.StartRecording();
                _recordingStarted = true;
                if (_now is null) _timer = new Timer(_ => Pulse(generation), null, 100, 100);
                _log.Info($"Loopback started: {format.SampleRate} Hz, {format.Channels} channels.");
            }
            catch { DisposeDevice(); throw; }
        }
    }

    private void OnData(long generation, WaveInEventArgs args)
    {
        try
        {
            lock (_sync)
            {
                if (generation != _generation || !_running || _failed) return;
                var frames = args.BytesRecorded / _align;
                if (frames == 0) return;
                var arrival = Now;
                if (_packets.Count != 0 && arrival - _lastArrival > TimeSpan.FromMilliseconds(250)) FlushPackets();
                // simplification: retain at most ten minutes before a consumer attaches or the pulse drains packets.
                if (_packetFrames + frames > _rate * 600L)
                    throw new InvalidOperationException("Loopback retained audio exceeded the ten minute limit.");
                _packets.Add(args.Buffer.AsSpan(0, frames * _align).ToArray());
                _packetFrames += frames;
                _lastArrival = arrival;
            }
        }
        catch (Exception ex) { ReportFailure(generation, ex); }
    }

    private void FlushPackets()
    {
        if (_packets.Count == 0) return;
        // NAudio supplies no QPC packet position. Batch packets before comparing the
        // clock with the sample-count end, so all packets in a wake-up cover their own time first.
        var start = _lastArrival - TimeSpan.FromSeconds(_packetFrames / (double)_rate);
        if (!_anchored)
        {
            _audio!.Anchor(start);
            _anchored = true;
        }
        if (start - _audio!.End > TimeSpan.FromMilliseconds(250)) _audio.FillSilence(start);
        foreach (var packet in _packets) _audio.Append(packet);
        _packets.Clear(); _packetFrames = 0;
    }

    internal void Pulse() => Pulse(_generation);
    private void Pulse(long generation)
    {
        try
        {
            lock (_lifecycle)
            {
                if (generation != _generation || _capture is null) return;
                _capture.CheckDevice();
                lock (_sync) { if (_running && !_failed) FlushPackets(); }
            }
        }
        catch (Exception ex) { ReportFailure(generation, ex); }
    }

    private void ReportFailure(long generation, Exception error)
    {
        lock (_sync)
        {
            if (generation != _generation || _failed) return;
            _failed = true;
        }
        _log.Error($"Loopback stopped: {error.GetType().Name}: {error.Message}");
        _queue(() =>
        {
            lock (_lifecycle)
            {
                if (generation != _generation) return;
                DisposeDevice();
                Failed?.Invoke(error);
            }
        });
    }

    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part) =>
        SegmentAtPauses(silenceThreshold, (audio, reason, _) => part(audio, reason));
    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason, TimeSpan> part)
    { lock (_sync) _audio?.SegmentAtPauses(silenceThreshold, part); }
    public CapturedAudio Stop() => StopTimestamped().Part;
    public (CapturedAudio Part, TimeSpan Offset) StopTimestamped()
    {
        lock (_lifecycle)
        {
            var boundary = _clock is null ? TimeSpan.Zero : Now;
            DisposeDevice();
            lock (_sync)
            {
                if (_audio is not null && !_failed)
                {
                    FlushPackets();
                    _audio.FillSilence(boundary);
                }
                return _audio?.Seal() ?? (CapturedAudio.Empty, TimeSpan.Zero);
            }
        }
    }

    private void DisposeDevice()
    {
        _timer?.Dispose(); _timer = null;
        var capture = _capture;
        if (capture is null) return;
        lock (_sync) _stopping = true;
        // StopRecording is asynchronous. DataAvailable remains attached and running until the last
        // packet and RecordingStopped, before we fill any missing tail at the saved stop boundary.
        try
        {
            capture.StopRecording();
            if (_recordingStarted && !_drained!.Wait(TimeSpan.FromSeconds(2)))
                _log.Error("Loopback drain timed out after 2 seconds waiting for RecordingStopped; detaching callbacks and filling the missing tail.");
        }
        finally
        {
            lock (_sync) _running = false;
            capture.DataAvailable -= _data;
            capture.RecordingStopped -= _stopped;
            _capture = null;
            _recordingStarted = false;
            capture.Dispose();
            // Keep the signal alive for a callback already in flight; it is collected with its session.
            _drained = null;
        }
    }
    public void Dispose() { lock (_lifecycle) DisposeDevice(); }
}

internal interface ILoopbackCaptureSource : IDisposable
{
    WaveFormat WaveFormat { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
    void CheckDevice();
}

internal sealed class DefaultLoopbackSource : ILoopbackCaptureSource
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice _device;
    private readonly WasapiLoopbackCapture _capture;
    private readonly bool _followDefault;
    public DefaultLoopbackSource(string? setting)
    {
        try
        {
            _followDefault = string.IsNullOrWhiteSpace(setting);
            _device = _followDefault ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : _enumerator.GetDevice(setting);
            try { _capture = new WasapiLoopbackCapture(_device); }
            catch { _device.Dispose(); throw; }
        }
        catch { _enumerator.Dispose(); throw; }
    }
    public WaveFormat WaveFormat => _capture.WaveFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable { add => _capture.DataAvailable += value; remove => _capture.DataAvailable -= value; }
    public event EventHandler<StoppedEventArgs>? RecordingStopped { add => _capture.RecordingStopped += value; remove => _capture.RecordingStopped -= value; }
    public void StartRecording() => _capture.StartRecording();
    public void StopRecording() => _capture.StopRecording();
    public void CheckDevice()
    {
        if (_followDefault)
        {
            using var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (current.ID != _device.ID) throw new InvalidOperationException("The default output device changed.");
        }
        if (_device.State != DeviceState.Active) throw new InvalidOperationException("The output device vanished or became unavailable.");
    }
    public void Dispose() { try { _capture.Dispose(); } finally { _device.Dispose(); _enumerator.Dispose(); } }
}
/// <summary>Managed streaming decode/downmix/resample path shared with microphone capture.
/// Quiet blocks are delivered too, so an idle track remains continuous without holding speech behind it.</summary>
internal sealed class LoopbackAudioBuffer
{
    private readonly SampleEncoding _encoding;
    private readonly int _channels, _inputRate, _align;
    private long _inputFrames;
    private readonly MonoResampler _resampler;
    private TimeSpan _origin;
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
    public TimeSpan End => _origin + TimeSpan.FromTicks(_inputFrames * TimeSpan.TicksPerSecond / _inputRate);
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

    public void Anchor(TimeSpan at)
    {
        if (_inputFrames == 0 && at > _origin) _origin = at;
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
            Append(zeros.AsSpan(0, count * _align));
        }
    }

    public void Append(ReadOnlySpan<byte> raw, TimeSpan? packetStart = null)
    {
        if (_sealed) return;
        // simplification: bound pre-segmentation and retained speech to ten minutes at 16 kHz.
        if (_pending.Count + raw.Length / _align * 16000L / _inputRate > 16000L * 600)
            throw new InvalidOperationException("Loopback retained audio exceeded the ten minute limit.");
        // Explicit anchors are monotonic; the capture normally uses only the contiguous sample clock.
        if (packetStart is { } at) _anchors.Add((_receivedSamples, at < End ? End : at));
        _inputFrames += raw.Length / _align;
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
        _lastStart = offset + TimeSpan.FromTicks(count * 625L);
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
