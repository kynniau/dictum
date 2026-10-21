using Dictum.Audio;

namespace Dictum.UI.Views;

/// <summary>
/// The microphone list as the tray panel, Settings > Microphone and the onboarding's microphone test show it: "System
/// default" first, then each device ("(default)" after the system's default one), and a chosen device that is not
/// connected last, marked so. One builder for the three lists, so their labels cannot drift apart.
/// </summary>
internal static class MicrophoneChoices
{
    /// <summary>The device each entry chooses (null: the system default) and its label, in the same order.</summary>
    public static (List<string?> Ids, List<string> Labels) Build(IReadOnlyList<AudioDeviceInfo> devices, string? chosen)
    {
        List<string?> ids = [null, .. devices.Select(d => (string?)d.Name)];
        var labels = new List<string> { "System default" };
        labels.AddRange(devices.Select(d => d.IsDefault ? $"{d.Name} (default)" : d.Name));
        if (chosen is not null && !ids.Contains(chosen))
        {
            ids.Add(chosen);
            labels.Add($"{chosen} (not connected)");
        }
        return (ids, labels);
    }
}
