using Dictum.Audio;
using Dictum.Settings;
using Dictum.Windows.Audio;
using NAudio.CoreAudioApi;

namespace Dictum.Tests;

public class ProcessedCaptureTests
{
    [Fact]
    public void Setting_defaults_off_and_generated_file_explains_it()
    {
        Assert.False(new AudioSettings().ProcessedCapture);
        Assert.False(SettingsLoader.Parse("{}").Settings.Audio.ProcessedCapture);
        var json = SettingsFile.DefaultJson("small.en");
        Assert.False(SettingsLoader.Parse(json).Settings.Audio.ProcessedCapture);
        Assert.Contains("Ask Windows to remove echo and background noise from the microphone. Off until verified on your PC.", json);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", false)]
    public void Setting_selects_capture_category_without_opening_a_device(string value, bool expected)
    {
        var loaded = SettingsLoader.Parse("{\"audio\":{\"processed_capture\":" + value + "}}");
        Assert.Equal(expected, loaded.Settings.Audio.ProcessedCapture);
        Assert.Equal(value == "\"true\"", loaded.Warnings.Count > 0);
        var categories = new List<AudioStreamCategory>();
        new MicrophoneCaptureMode(loaded.Settings.Audio.ProcessedCapture).Configure(categories.Add);
        Assert.Equal(expected ? new[] { AudioStreamCategory.Communications } : [], categories);
    }

    [Theory]
    [InlineData(false, "microphone: raw")]
    [InlineData(true, "microphone: processed (communications)")]
    public void Successful_start_log_names_the_selected_mode(bool processed, string expected)
    {
        var log = new CaptureLog();
        new MicrophoneCaptureMode(processed).LogStarted(log);
        Assert.Equal([(LogLevel.Info, expected)], log.Lines);
    }

    [Fact]
    public void Reader_uses_selected_profile_reloads_and_keeps_previous_when_unreadable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dictum-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var reader = new MicrophoneSettingsReader(path);
            Assert.False(reader.Read().ProcessedCapture);
            File.WriteAllText(path, "{\"audio\":{\"processed_capture\":true}}");
            Assert.True(reader.Read().ProcessedCapture);
            File.WriteAllText(path, "{");
            Assert.True(reader.Read().ProcessedCapture);
            File.WriteAllText(path, "{\"audio\":{\"processed_capture\":false}}");
            Assert.False(reader.Read().ProcessedCapture);
        }
        finally { File.Delete(path); Directory.Delete(folder); }
    }

    [Fact]
    public void Tone_ratio_measures_the_reference_and_rejects_another_frequency()
    {
        var reference = new float[MonoResampler.WhisperRate * CaptureCheck.Seconds];
        var other = new float[reference.Length];
        for (int i = MonoResampler.WhisperRate; i < 4 * MonoResampler.WhisperRate; i++)
        {
            reference[i] = CaptureCheck.Amplitude * (float)Math.Sin(2 * Math.PI * CaptureCheck.Frequency * i / MonoResampler.WhisperRate);
            other[i] = CaptureCheck.Amplitude * (float)Math.Sin(2 * Math.PI * 500 * i / MonoResampler.WhisperRate);
        }
        Assert.InRange(CaptureCheck.ToneRatio(reference), 0.99999, 1.00001);
        Assert.InRange(CaptureCheck.ToneRatio(other), 0, 0.000001);
        Assert.Equal(0, CaptureCheck.ToneRatio(new float[reference.Length]));
        Assert.Throws<ArgumentException>(() => CaptureCheck.ToneRatio([]));
    }

    [Fact]
    public void Diagnostic_output_has_only_mode_names_and_two_invariant_numbers()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("raw: 0.125000", CaptureCheck.Format(false, 0.125));
            Assert.Equal("processed (communications): 0.031250", CaptureCheck.Format(true, 0.03125));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    private sealed class CaptureLog : ILog
    {
        internal List<(LogLevel, string)> Lines { get; } = [];
        public void Write(LogLevel level, string message) => Lines.Add((level, message));
    }
}
