using Dictum.Core;
using Dictum.Settings;
using NAudio.CoreAudioApi;

namespace Dictum.Windows.Audio;

internal readonly record struct MicrophoneCaptureMode(bool Processed)
{
    internal string Name => Processed ? "processed (communications)" : "raw";

    internal void Configure(Action<AudioStreamCategory> setCategory)
    {
        // Verified against NAudio.Wasapi 3.1.0 IL: WithCommunicationsMode sets useCommunicationsMode;
        // WasapiRecorder.InitializeStandard calls AudioClient.SetClientProperties(Communications)
        // before Initialize. That overload passes Options=0 (NOT Raw) to IAudioClient2.SetClientProperties.
        // AudioClientProperties is four sequential 32-bit fields: cbSize, bIsOffload=0, eCategory=3, Options=0.
        if (Processed) setCategory(AudioStreamCategory.Communications);
    }

    // "raw" names the unchanged legacy path, not AUDCLNT_STREAMOPTIONS_RAW.
    internal void LogStarted(ILog log) => log.Info($"microphone: {Name}");
}

/// <summary>Uses the same settings file and tolerant reader as the app, including --config and reloads.</summary>
internal sealed class MicrophoneSettingsReader(string path)
{
    private readonly object _gate = new();
    private AudioSettings _previous = new();

    internal AudioSettings Read()
    {
        lock (_gate)
        {
            var loaded = SettingsLoader.LoadFile(path);
            if (loaded.Usable) _previous = loaded.Settings.Audio;
            return _previous;
        }
    }
}
