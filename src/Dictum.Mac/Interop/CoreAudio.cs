using System.Runtime.InteropServices;

namespace Dictum.Mac.Interop;

/// <summary>
/// Core Audio's plain C API: the HAL (AudioObject*, CoreAudio.framework) for the device list, and Audio Queue Services
/// (AudioQueue*, AudioToolbox.framework) for capture. No Objective-C and no blocks. Every constant, struct layout and
/// signature here was read from the macOS SDK headers in Xcode on 22/10/2026 (paths relative to
/// MacOSX.sdk/System/Library/Frameworks/): AudioToolbox.framework/Headers/AudioQueue.h,
/// CoreAudio.framework/Headers/AudioHardware.h and AudioHardwareBase.h, CoreAudioTypes.framework/Headers/CoreAudioBaseTypes.h;
/// the host clock from MacOSX.sdk/usr/include/mach/mach_time.h. OSStatus is a 32-bit int; 0 is success.
/// </summary>
internal static unsafe class CoreAudio
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    /// <summary>A four-character code as Core Audio spells its constants ('dev#').</summary>
    public static uint FourCC(string code) => (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);

    // AudioHardware.h / AudioHardwareBase.h
    public const uint SystemObject = 1;                                   // kAudioObjectSystemObject
    public static readonly uint HardwareDevices = FourCC("dev#");         // kAudioHardwarePropertyDevices
    public static readonly uint HardwareDefaultInputDevice = FourCC("dIn "); // kAudioHardwarePropertyDefaultInputDevice
    public static readonly uint DeviceStreamConfiguration = FourCC("slay"); // kAudioDevicePropertyStreamConfiguration
    public static readonly uint ObjectName = FourCC("lnam");              // kAudioObjectPropertyName (caller releases the CFString)
    public static readonly uint DeviceUID = FourCC("uid ");               // kAudioDevicePropertyDeviceUID (caller releases the CFString)
    public static readonly uint DeviceNominalSampleRate = FourCC("nsrt"); // kAudioDevicePropertyNominalSampleRate (Float64)
    public static readonly uint ScopeGlobal = FourCC("glob");             // kAudioObjectPropertyScopeGlobal
    public static readonly uint ScopeInput = FourCC("inpt");              // kAudioObjectPropertyScopeInput
    public const uint ElementMain = 0;                                    // kAudioObjectPropertyElementMain
    public const uint ObjectUnknown = 0;                                  // kAudioObjectUnknown

    // AudioQueue.h
    public static readonly uint QueueIsRunning = FourCC("aqrn");          // kAudioQueueProperty_IsRunning (UInt32)
    public static readonly uint QueueCurrentDevice = FourCC("aqcd");      // kAudioQueueProperty_CurrentDevice (CFStringRef UID)
    public static readonly uint QueueStreamDescription = FourCC("aqft");  // kAudioQueueProperty_StreamDescription (ASBD)

    // CoreAudioBaseTypes.h
    public static readonly uint FormatLinearPCM = FourCC("lpcm");         // kAudioFormatLinearPCM
    public const uint FlagIsFloat = 1u << 0;                              // kAudioFormatFlagIsFloat
    public const uint FlagIsBigEndian = 1u << 1;                          // kAudioFormatFlagIsBigEndian
    public const uint FlagIsSignedInteger = 1u << 2;                      // kAudioFormatFlagIsSignedInteger
    public const uint FlagIsPacked = 1u << 3;                             // kAudioFormatFlagIsPacked
    public const uint FlagIsNonInterleaved = 1u << 5;                     // kAudioFormatFlagIsNonInterleaved
    public const uint TimeStampHostTimeValid = 1u << 1;                   // kAudioTimeStampHostTimeValid

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyAddress(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    /// <summary>AudioStreamBasicDescription: 40 bytes (a Float64 then eight UInt32).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    /// <summary>The leading fields of AudioQueueBuffer (the rest is never read or written here).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public nint AudioData;
        public uint AudioDataByteSize;
        public nint UserData;
    }

    /// <summary>
    /// AudioTimeStamp: mSampleTime (Float64, offset 0), mHostTime (UInt64, 8), mRateScalar (Float64, 16), mWordClockTime
    /// (UInt64, 24), mSMPTETime (SMPTETime, 24 bytes, 32), mFlags (UInt32, 56), mReserved (UInt32, 60): 64 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct TimeStamp
    {
        [FieldOffset(0)] public double SampleTime;
        [FieldOffset(8)] public ulong HostTime;
        [FieldOffset(56)] public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TimebaseInfo
    {
        public uint Numer;
        public uint Denom;
    }

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyDataSize")]
    public static extern int GetPropertyDataSize(uint objectId, in PropertyAddress address, uint qualifierSize, nint qualifier, out uint dataSize);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    public static extern int GetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, nint qualifier, ref uint dataSize, void* data);

    /// <param name="callback">An AudioQueueInputCallback: <c>void (void*, AudioQueueRef, AudioQueueBufferRef, const AudioTimeStamp*, UInt32, const AudioStreamPacketDescription*)</c>.</param>
    /// <param name="runLoop">0: the callback runs on one of the queue's own threads.</param>
    [DllImport(AudioToolbox, EntryPoint = "AudioQueueNewInput")]
    public static extern int QueueNewInput(in StreamDescription format, nint callback, nint userData, nint runLoop, nint runLoopMode, uint flags, out nint queue);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueAllocateBuffer")]
    public static extern int QueueAllocateBuffer(nint queue, uint byteSize, out QueueBuffer* buffer);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueEnqueueBuffer")]
    public static extern int QueueEnqueueBuffer(nint queue, QueueBuffer* buffer, uint packetDescriptionCount, nint packetDescriptions);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueStart")]
    public static extern int QueueStart(nint queue, nint startTime);

    /// <param name="immediate">Boolean (one byte): true stops synchronously; pending buffer callbacks run during the stop.</param>
    [DllImport(AudioToolbox, EntryPoint = "AudioQueueStop")]
    public static extern int QueueStop(nint queue, byte immediate);

    /// <param name="immediate">Boolean (one byte). After it returns the queue invokes no more callbacks, unless called from one.</param>
    [DllImport(AudioToolbox, EntryPoint = "AudioQueueDispose")]
    public static extern int QueueDispose(nint queue, byte immediate);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueSetProperty")]
    public static extern int QueueSetProperty(nint queue, uint propertyId, void* data, uint dataSize);

    [DllImport(AudioToolbox, EntryPoint = "AudioQueueGetProperty")]
    public static extern int QueueGetProperty(nint queue, uint propertyId, void* data, ref uint dataSize);

    /// <param name="listener">An AudioQueuePropertyListenerProc: <c>void (void*, AudioQueueRef, AudioQueuePropertyID)</c>.</param>
    [DllImport(AudioToolbox, EntryPoint = "AudioQueueAddPropertyListener")]
    public static extern int QueueAddPropertyListener(nint queue, uint propertyId, nint listener, nint userData);

    [DllImport(LibSystem, EntryPoint = "mach_timebase_info")]
    public static extern int MachTimebaseInfo(out TimebaseInfo info);

    /// <summary>An OSStatus as Core Audio's tools print it: the number, plus its four characters when they are printable.</summary>
    public static string Describe(int status)
    {
        var u = (uint)status;
        Span<char> c = [(char)(u >> 24 & 0xFF), (char)(u >> 16 & 0xFF), (char)(u >> 8 & 0xFF), (char)(u & 0xFF)];
        foreach (var ch in c)
        {
            if (ch < 0x20 || ch > 0x7E) return status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return $"{status} ('{new string(c)}')";
    }
}
