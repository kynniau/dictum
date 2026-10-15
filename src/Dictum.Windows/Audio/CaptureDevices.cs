using Dictum.Audio;
using Dictum.Core;
using NAudio.CoreAudioApi;

namespace Dictum.Windows.Audio;

/// <summary>The microphones Windows lists (WASAPI endpoints, through NAudio.Wasapi).</summary>
public static class CaptureDevices
{
    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var def) && def is not null)
        {
            defaultId = def.ID;
            def.Dispose();
        }
        var list = new List<AudioDeviceInfo>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultId));
            d.Dispose();
        }
        return list;
    }

    /// <summary>
    /// Picks the configured device: exact endpoint ID, else first active device whose name contains the
    /// setting (case-insensitive), else the Windows default capture device.
    /// </summary>
    public static MMDevice Resolve(MMDeviceEnumerator enumerator, string? setting, ILog log)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (string.Equals(d.ID, setting, StringComparison.OrdinalIgnoreCase)
                    || d.FriendlyName.Contains(setting, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
                d.Dispose();
            }
            log.Warn($"Configured audio.device \"{setting}\" not found among active microphones; using the Windows default.");
        }
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var device) || device is null)
        {
            throw new InvalidOperationException("No microphone found. Check Settings > Privacy & security > Microphone and that a recording device is enabled.");
        }
        return device;
    }
}
