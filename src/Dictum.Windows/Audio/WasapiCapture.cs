using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dictum.Windows.Audio;

/// <summary>
/// WASAPI shared-mode capture via NAudio's WasapiRecorder. The device is opened on Start and released on
/// Stop, so the Windows microphone indicator is off whenever Dictum is idle (SPEC §4).
/// </summary>
public sealed class WasapiCapture : IAudioCapture, IDisposable
{
    private readonly ILog _log;
    private readonly object _gate = new();
    private WasapiRecorder? _recorder;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private CaptureBuffer? _buffer;
    private long _keyDownStamp;
    private int _firstPacketLogged;

    public WasapiCapture(ILog log) => _log = log;

    public event Action<Exception>? Failed;

    /// <summary>The first sound of each recording (CaptureBuffer.SoundFloor), for the "No sound from mic" warning.</summary>
    public event Action? SoundHeard;

    public bool ReportsSound => true;

    public float CurrentLevel => Volatile.Read(ref _buffer)?.CurrentLevel ?? 0f;

    public float PeakLevel => Volatile.Read(ref _buffer)?.PeakLevel ?? 0f;

    public long BlocksMeasured => Volatile.Read(ref _buffer)?.BlocksMeasured ?? 0;

    public void Start(string? deviceSetting, long keyDownStamp)
    {
        lock (_gate)
        {
            ReleaseDevice();
            var sw = Stopwatch.StartNew();
            _keyDownStamp = keyDownStamp;
            _firstPacketLogged = 0;
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _device = CaptureDevices.Resolve(_enumerator, deviceSetting, _log);
                var recorder = new WasapiRecorderBuilder()
                    .WithDevice(_device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithBufferLength(50)
                    .Build();
                var format = recorder.WaveFormat;
                var buffer = new CaptureBuffer(SampleDecoder.EncodingOf(format), format.Channels, format.SampleRate, () => SoundHeard?.Invoke());
                recorder.DataAvailable += OnData;
                recorder.RecordingStopped += OnStopped;
                Volatile.Write(ref _buffer, buffer);
                _recorder = recorder;
                recorder.StartRecording();
                _log.Info($"Capture started in {sw.ElapsedMilliseconds} ms ({Stopwatch.GetElapsedTime(keyDownStamp).TotalMilliseconds:0} ms after key-down) on \"{_device.FriendlyName}\" {format.SampleRate} Hz {format.Channels} ch {format.BitsPerSample}-bit, latency {recorder.LatencyMilliseconds} ms.");
            }
            catch
            {
                ReleaseDevice();
                throw;
            }
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var target = Volatile.Read(ref _buffer);
        if (target is null) return;
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            // The engine marks the packet silent; its contents must be treated as zeros.
            var zeros = new byte[buffer.Length];
            target.Append(zeros);
        }
        else
        {
            target.Append(buffer);
        }
        if (Interlocked.Exchange(ref _firstPacketLogged, 1) == 0)
        {
            // Both figures on the QPC clock: whether speech between key-down and the first captured frame is lost.
            _log.Info($"First audio packet: its first frame was recorded {FirstFrameAfterKeyDownMs(qpcPosition, _keyDownStamp, Stopwatch.Frequency):0} ms after key-down; delivered {Stopwatch.GetElapsedTime(_keyDownStamp).TotalMilliseconds:0} ms after key-down.");
        }
    }

    /// <summary>
    /// When the packet's first frame was recorded, in ms after key-down; positive means speech in that gap was not captured.
    /// WASAPI gives the QPC position already converted to 100-ns units (IAudioCaptureClient::GetBuffer, Microsoft Learn,
    /// read 15/10/2026; NAudio.Wasapi 3.1.0's DataAvailable docs say the same), so only the key-down stamp, in raw
    /// Stopwatch ticks, is converted. Doubles: the 100-ns product of a raw stamp overflows a long after about a day of uptime.
    /// </summary>
    /// <param name="qpcPosition">WASAPI's QPC position, in 100-ns units.</param>
    /// <param name="keyDownStamp">Stopwatch.GetTimestamp() at key-down.</param>
    /// <param name="frequency">Stopwatch.Frequency.</param>
    internal static double FirstFrameAfterKeyDownMs(long qpcPosition, long keyDownStamp, long frequency) =>
        (qpcPosition - keyDownStamp * (10_000_000.0 / frequency)) / 10_000.0;

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        _log.Error($"Capture stopped with error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        Failed?.Invoke(e.Exception);
    }

    public CapturedAudio Stop()
    {
        lock (_gate)
        {
            var buffer = Volatile.Read(ref _buffer);
            var audio = buffer?.Seal() ?? CapturedAudio.Empty;
            ReleaseDevice();
            return audio;
        }
    }

    /// <summary>Hands-free: the recording's buffer cuts at pauses from now on (CaptureBuffer.SegmentAtPauses); nothing when not recording.</summary>
    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part)
    {
        lock (_gate) Volatile.Read(ref _buffer)?.SegmentAtPauses(silenceThreshold, part);
    }

    private void ReleaseDevice()
    {
        var recorder = _recorder;
        _recorder = null;
        Volatile.Write(ref _buffer, null);
        if (recorder is not null)
        {
            recorder.DataAvailable -= OnData;
            recorder.RecordingStopped -= OnStopped;
            try
            {
                recorder.StopRecording();
            }
            catch (Exception ex)
            {
                _log.Debug($"StopRecording: {ex.GetType().Name}");
            }
            recorder.Dispose();
        }
        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose()
    {
        lock (_gate) ReleaseDevice();
    }
}
