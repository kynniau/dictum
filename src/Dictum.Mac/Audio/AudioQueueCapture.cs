using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Audio;

/// <summary>
/// Capture through an Audio Queue Services input queue (plain C; no AVFoundation, no blocks). The queue is created and
/// started on Start and stopped and disposed on Stop, so the system's microphone indicator is on only while Dictum records
/// (SPEC §4). The queue is asked for 16 kHz mono Float32 and converts itself; a device that refuses that is opened at its
/// own rate and channel count and CaptureBuffer resamples. Callbacks run on the queue's own thread: they append, re-enqueue
/// and return, never block on the gate, and never let an exception reach native code.
///
/// Microphone permission: Core Audio asks the user itself on the first input, naming the app whose Info.plist carries
/// NSMicrophoneUsageDescription (run from a bare <c>dotnet</c> process, the prompt names the terminal). When access is
/// denied the queue is believed (Recalled, not verified) to start and deliver zeros, which CaptureBuffer reads as no sound,
/// so the recording gets the "No sound from mic" warning rather than an error.
///
/// Measured 22/10/2026 on the one microphone of the Mac this was written on (a USB camera's, 48 kHz mono): the first
/// packet's first frame is stamped 70-140 ms after key-down, but the first ~460 ms of every recording arrive as exact zeros,
/// whether the queue converts to 16 kHz or records at 48 kHz, so the device (or the system) starts silent. The first-packet
/// log line therefore understates how much speech after key-down is lost on such a device. Other devices: not measured.
/// </summary>
internal sealed unsafe class AudioQueueCapture(ILog log) : IAudioCapture, IDisposable
{
    /// <summary>Queue buffers kept in flight, and their length: 3 × 40 ms keeps 120 ms queued and meters every 40 ms.</summary>
    internal const int BufferCount = 3, BufferMs = 40;

    // Callbacks find their recording by a number, not a GCHandle: a callback that arrives after the recording was disposed
    // finds nothing and returns, instead of dereferencing a freed handle.
    private static readonly ConcurrentDictionary<nint, Session> Sessions = new();
    private static long _nextId;
    private static readonly Lazy<CoreAudio.TimebaseInfo> Timebase = new(() =>
        CoreAudio.MachTimebaseInfo(out var info) == 0 && info.Denom != 0 ? info : new CoreAudio.TimebaseInfo { Numer = 1, Denom = 1 });

    private readonly object _gate = new();
    private Session? _session;
    private CaptureBuffer? _buffer;

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
            Release();
            var sw = Stopwatch.StartNew();
            var device = CaptureDevices.Resolve(CaptureDevices.Inputs(), deviceSetting, log);
            Exception? refused = null;
            foreach (var format in QueueFormat.Candidates(device))
            {
                try
                {
                    var session = Open(device, format, keyDownStamp);
                    _session = session;
                    Volatile.Write(ref _buffer, session.Buffer);
                    var f = session.Format;
                    var converted = device.NominalRate > 0 && (f.SampleRate != (int)Math.Round(device.NominalRate) || f.Channels != device.Channels)
                        ? $", converted by the queue from {device.NominalRate:0} Hz {device.Channels} ch"
                        : "";
                    log.Info($"Capture started in {sw.ElapsedMilliseconds} ms ({Stopwatch.GetElapsedTime(keyDownStamp).TotalMilliseconds:0} ms after key-down) on \"{device.Name}\" {f.SampleRate} Hz {f.Channels} ch {f.Encoding}{converted}, latency {BufferMs} ms ({BufferCount} buffers).");
                    return;
                }
                catch (Exception ex) when (ex is CoreAudioException or NotSupportedException)
                {
                    refused = ex;
                    log.Info($"Audio queue refused {format.SampleRate:0} Hz {format.ChannelsPerFrame} ch on \"{device.Name}\": {ex.Message}");
                }
            }
            throw new InvalidOperationException($"The microphone \"{device.Name}\" could not be opened: {refused?.Message}", refused);
        }
    }

    /// <summary>Creates, configures and starts one queue; on any failure disposes what it made and rethrows.</summary>
    private Session Open(InputDevice device, CoreAudio.StreamDescription format, long keyDownStamp)
    {
        var id = (nint)Interlocked.Increment(ref _nextId);
        var session = new Session(this, id, keyDownStamp);
        Sessions[id] = session;
        try
        {
            delegate* unmanaged<nint, nint, CoreAudio.QueueBuffer*, CoreAudio.TimeStamp*, uint, nint, void> input = &OnInput;
            Check(CoreAudio.QueueNewInput(format, (nint)input, id, 0, 0, 0, out var queue), "AudioQueueNewInput");
            session.Queue = queue;

            var uid = CF.String(device.Id);
            try
            {
                Check(CoreAudio.QueueSetProperty(queue, CoreAudio.QueueCurrentDevice, &uid, (uint)sizeof(nint)), "kAudioQueueProperty_CurrentDevice");
            }
            finally
            {
                CF.Release(uid);
            }

            CoreAudio.StreamDescription actual = default;
            var size = (uint)sizeof(CoreAudio.StreamDescription);
            Check(CoreAudio.QueueGetProperty(queue, CoreAudio.QueueStreamDescription, &actual, ref size), "kAudioQueueProperty_StreamDescription");
            session.Format = QueueFormat.Decode(actual);
            session.Buffer = new CaptureBuffer(session.Format.Encoding, session.Format.Channels, session.Format.SampleRate, () => SoundHeard?.Invoke());

            delegate* unmanaged<nint, nint, uint, void> listener = &OnProperty;
            Check(CoreAudio.QueueAddPropertyListener(queue, CoreAudio.QueueIsRunning, (nint)listener, id), "AudioQueueAddPropertyListener");

            var bytes = QueueFormat.BufferBytes(actual, BufferMs);
            for (var i = 0; i < BufferCount; i++)
            {
                Check(CoreAudio.QueueAllocateBuffer(queue, bytes, out var buffer), "AudioQueueAllocateBuffer");
                Check(CoreAudio.QueueEnqueueBuffer(queue, buffer, 0, 0), "AudioQueueEnqueueBuffer");
            }
            Check(CoreAudio.QueueStart(queue, 0), "AudioQueueStart");
            return session;
        }
        catch
        {
            session.Close(log);
            throw;
        }
    }

    private static void Check(int status, string call)
    {
        if (status != 0) throw new CoreAudioException($"{call} failed: OSStatus {CoreAudio.Describe(status)}.");
    }

    public CapturedAudio Stop()
    {
        lock (_gate)
        {
            var session = _session;
            if (session is null) return CapturedAudio.Empty;
            session.StopQueue(log); // the buffers in flight are delivered and appended during the synchronous stop
            var audio = session.Buffer?.Seal() ?? CapturedAudio.Empty;
            Release();
            return audio;
        }
    }

    /// <summary>Hands-free: the recording's buffer cuts at pauses from now on (CaptureBuffer.SegmentAtPauses); nothing when not recording.</summary>
    public void SegmentAtPauses(double silenceThreshold, Action<CapturedAudio, CutReason> part)
    {
        lock (_gate) Volatile.Read(ref _buffer)?.SegmentAtPauses(silenceThreshold, part);
    }

    private void Release()
    {
        var session = _session;
        _session = null;
        Volatile.Write(ref _buffer, null);
        session?.Close(log);
    }

    public void Dispose()
    {
        lock (_gate) Release();
    }

    /// <summary>
    /// Raised once per recording, off the queue's thread (the thread pool), so a handler that stops or restarts the capture
    /// never runs inside an Audio Queue callback, where stopping and disposing the queue is not safe.
    /// </summary>
    private void RaiseFailed(Session session, Exception error)
    {
        if (session.Stopping || Interlocked.Exchange(ref session.FailedRaised, 1) != 0) return;
        log.Error($"Capture stopped with error: {error.GetType().Name}: {error.Message}");
        ThreadPool.UnsafeQueueUserWorkItem(_ => Failed?.Invoke(error), null);
    }

    [UnmanagedCallersOnly]
    private static void OnInput(nint userData, nint queue, CoreAudio.QueueBuffer* buffer, CoreAudio.TimeStamp* start, uint packetCount, nint packets)
    {
        try
        {
            if (Sessions.TryGetValue(userData, out var session)) session.Deliver(queue, buffer, start);
        }
        catch
        {
            // Never unwind into the queue's thread. Deliver reports its own failures; anything left is dropped.
        }
    }

    [UnmanagedCallersOnly]
    private static void OnProperty(nint userData, nint queue, uint propertyId)
    {
        try
        {
            if (!Sessions.TryGetValue(userData, out var session) || session.Stopping || propertyId != CoreAudio.QueueIsRunning) return;
            uint running = 1, size = sizeof(uint);
            if (CoreAudio.QueueGetProperty(queue, CoreAudio.QueueIsRunning, &running, ref size) == 0 && running == 0)
            {
                session.Owner.RaiseFailed(session, new CoreAudioException("The audio device stopped while recording."));
            }
        }
        catch
        {
            // As OnInput.
        }
    }

    /// <summary>One recording's queue and buffer. Fields written under the owner's gate before the queue starts.</summary>
    private sealed class Session(AudioQueueCapture owner, nint id, long keyDownStamp)
    {
        public AudioQueueCapture Owner { get; } = owner;
        public nint Queue;
        public CaptureFormat Format = new(SampleEncoding.Float32, 1, MonoResampler.WhisperRate);
        public CaptureBuffer? Buffer;
        public volatile bool Stopping;
        public int FailedRaised;
        private int _firstPacketLogged;
        private bool _queueStopped;

        public void Deliver(nint queue, CoreAudio.QueueBuffer* buffer, CoreAudio.TimeStamp* start)
        {
            var bytes = buffer->AudioDataByteSize;
            if (bytes > 0 && Buffer is { } target)
            {
                target.Append(new ReadOnlySpan<byte>((void*)buffer->AudioData, (int)bytes));
                // simplification: the first-packet line is written on the queue's thread, so one log write (file I/O) delays
                // that one callback, once per recording, as WasapiCapture does on its capture thread. Ceiling: 3 × 40 ms of
                // buffers absorb a write of up to about 80 ms. Retire (hand the line to the thread pool) if a log ever shows
                // a gap in the first 120 ms of a recording or the log moves to slow storage.
                if (Interlocked.Exchange(ref _firstPacketLogged, 1) == 0) LogFirstPacket(start);
            }
            if (Stopping) return; // the stop is collecting the buffers in flight: none goes back
            var status = CoreAudio.QueueEnqueueBuffer(queue, buffer, 0, 0);
            if (status != 0 && !Stopping) Owner.RaiseFailed(this, new CoreAudioException($"AudioQueueEnqueueBuffer failed: OSStatus {CoreAudio.Describe(status)}."));
        }

        private void LogFirstPacket(CoreAudio.TimeStamp* start)
        {
            var delivered = Stopwatch.GetElapsedTime(keyDownStamp).TotalMilliseconds;
            if (start is not null && (start->Flags & CoreAudio.TimeStampHostTimeValid) != 0)
            {
                // Both figures on one clock (QueueFormat.FirstFrameAfterKeyDownMs): whether speech between key-down and the first captured frame is lost.
                var tb = Timebase.Value;
                var recorded = QueueFormat.FirstFrameAfterKeyDownMs(start->HostTime, tb.Numer, tb.Denom, keyDownStamp, Stopwatch.Frequency);
                Owner.LogInfo($"First audio packet: its first frame was recorded {recorded:0} ms after key-down; delivered {delivered:0} ms after key-down.");
            }
            else
            {
                Owner.LogInfo($"First audio packet: delivered {delivered:0} ms after key-down (the packet carried no host time).");
            }
        }

        /// <summary>Stops the queue synchronously; the callbacks this runs append the buffers in flight and re-enqueue nothing.</summary>
        public void StopQueue(ILog log)
        {
            Stopping = true;
            if (Queue == 0 || _queueStopped) return;
            _queueStopped = true;
            var status = CoreAudio.QueueStop(Queue, 1);
            if (status != 0) log.Debug($"AudioQueueStop: OSStatus {CoreAudio.Describe(status)}");
        }

        /// <summary>Stops and disposes the queue (the microphone indicator goes off) and forgets the session.</summary>
        public void Close(ILog log)
        {
            StopQueue(log);
            if (Queue != 0)
            {
                var status = CoreAudio.QueueDispose(Queue, 1);
                if (status != 0) log.Debug($"AudioQueueDispose: OSStatus {CoreAudio.Describe(status)}");
                Queue = 0;
            }
            Sessions.TryRemove(id, out _);
        }
    }

    private void LogInfo(string message) => log.Info(message);
}

/// <summary>A Core Audio call returned an error OSStatus.</summary>
internal sealed class CoreAudioException(string message) : InvalidOperationException(message);
