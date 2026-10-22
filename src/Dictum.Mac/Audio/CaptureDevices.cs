using Dictum.Audio;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Audio;

/// <summary>A microphone the HAL lists, with what capture needs to fall back to the device's own format.</summary>
/// <param name="Id">The device UID (kAudioDevicePropertyDeviceUID): persistent across boots; audio.device accepts it.</param>
/// <param name="Channels">Input channels across the device's input streams.</param>
/// <param name="NominalRate">The device's nominal sample rate in Hz (0 when the HAL did not say).</param>
internal sealed record InputDevice(string Id, string Name, bool IsDefault, int Channels = 1, double NominalRate = 0)
{
    public AudioDeviceInfo Info => new(Id, Name, IsDefault);
}

/// <summary>The microphones macOS lists: every HAL device with at least one input stream.</summary>
internal static class CaptureDevices
{
    public static IReadOnlyList<AudioDeviceInfo> List() => [.. Inputs().Select(d => d.Info)];

    /// <summary>
    /// Every device (kAudioHardwarePropertyDevices on the system object) whose input-scope stream configuration has a buffer
    /// with at least one channel; the default is kAudioHardwarePropertyDefaultInputDevice. A device whose name or UID cannot
    /// be read is left out (it cannot be selected or opened by UID anyway).
    /// </summary>
    public static unsafe IReadOnlyList<InputDevice> Inputs()
    {
        var all = new CoreAudio.PropertyAddress(CoreAudio.HardwareDevices, CoreAudio.ScopeGlobal, CoreAudio.ElementMain);
        if (CoreAudio.GetPropertyDataSize(CoreAudio.SystemObject, all, 0, 0, out var size) != 0 || size == 0) return [];
        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
        {
            if (CoreAudio.GetPropertyData(CoreAudio.SystemObject, all, 0, 0, ref size, p) != 0) return [];
        }
        var count = (int)(size / sizeof(uint)); // the list can shrink between the two calls
        var defaultId = ReadUInt32(CoreAudio.SystemObject, CoreAudio.HardwareDefaultInputDevice, CoreAudio.ScopeGlobal) ?? CoreAudio.ObjectUnknown;
        var list = new List<InputDevice>();
        for (var i = 0; i < count; i++)
        {
            var id = ids[i];
            var channels = InputChannels(id);
            if (channels <= 0) continue;
            var uid = ReadString(id, CoreAudio.DeviceUID);
            var name = ReadString(id, CoreAudio.ObjectName);
            if (string.IsNullOrEmpty(uid) || name is null) continue;
            list.Add(new InputDevice(uid, name, id == defaultId && defaultId != CoreAudio.ObjectUnknown, channels, ReadDouble(id, CoreAudio.DeviceNominalSampleRate) ?? 0));
        }
        return list;
    }

    /// <summary>
    /// Picks the configured device: exact UID (case-insensitive, as on Windows), else the first device whose name contains the
    /// setting (case-insensitive), else the default input device, warning when a setting matched nothing. No microphone at
    /// all, or none marked default, throws <see cref="NoMicrophoneException"/> (the one no-microphone sentence).
    /// </summary>
    public static InputDevice Resolve(IReadOnlyList<InputDevice> devices, string? setting, ILog log)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            var match = devices.FirstOrDefault(d => string.Equals(d.Id, setting, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(d => d.Name.Contains(setting, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            log.Warn($"Configured audio.device \"{setting}\" not found among active microphones; using the macOS default.");
        }
        return devices.FirstOrDefault(d => d.IsDefault) ?? throw new NoMicrophoneException();
    }

    /// <summary>Sum of mNumberChannels over the AudioBufferList the input-scope stream configuration returns.</summary>
    private static unsafe int InputChannels(uint device)
    {
        var address = new CoreAudio.PropertyAddress(CoreAudio.DeviceStreamConfiguration, CoreAudio.ScopeInput, CoreAudio.ElementMain);
        if (CoreAudio.GetPropertyDataSize(device, address, 0, 0, out var size) != 0 || size < sizeof(uint)) return 0;
        var bytes = new byte[size];
        fixed (byte* p = bytes)
        {
            if (CoreAudio.GetPropertyData(device, address, 0, 0, ref size, p) != 0) return 0;
            return ChannelsInBufferList(bytes.AsSpan(0, (int)size));
        }
    }

    /// <summary>
    /// Reads an AudioBufferList laid out as on 64-bit macOS: mNumberBuffers (UInt32) padded to 8, then mNumberBuffers
    /// AudioBuffer entries of 16 bytes each (mNumberChannels UInt32, mDataByteSize UInt32, mData pointer). Returns the total
    /// channel count; entries beyond the data are ignored.
    /// </summary>
    internal static int ChannelsInBufferList(ReadOnlySpan<byte> list)
    {
        if (list.Length < 4) return 0;
        var buffers = BitConverter.ToUInt32(list[..4]);
        var channels = 0;
        for (var i = 0; i < buffers; i++)
        {
            var offset = 8 + i * 16;
            if (offset + 4 > list.Length) break;
            channels += (int)BitConverter.ToUInt32(list.Slice(offset, 4));
        }
        return channels;
    }

    private static unsafe uint? ReadUInt32(uint objectId, uint selector, uint scope)
    {
        var address = new CoreAudio.PropertyAddress(selector, scope, CoreAudio.ElementMain);
        uint value = 0, size = sizeof(uint);
        return CoreAudio.GetPropertyData(objectId, address, 0, 0, ref size, &value) == 0 ? value : null;
    }

    private static unsafe double? ReadDouble(uint objectId, uint selector)
    {
        var address = new CoreAudio.PropertyAddress(selector, CoreAudio.ScopeGlobal, CoreAudio.ElementMain);
        double value = 0;
        uint size = sizeof(double);
        return CoreAudio.GetPropertyData(objectId, address, 0, 0, ref size, &value) == 0 ? value : null;
    }

    /// <summary>A CFString property (the HAL hands back a retained CFString, released here).</summary>
    private static unsafe string? ReadString(uint objectId, uint selector)
    {
        var address = new CoreAudio.PropertyAddress(selector, CoreAudio.ScopeGlobal, CoreAudio.ElementMain);
        nint cf = 0;
        var size = (uint)sizeof(nint);
        if (CoreAudio.GetPropertyData(objectId, address, 0, 0, ref size, &cf) != 0 || cf == 0) return null;
        try
        {
            return CF.ToString(cf);
        }
        finally
        {
            CF.Release(cf);
        }
    }
}
