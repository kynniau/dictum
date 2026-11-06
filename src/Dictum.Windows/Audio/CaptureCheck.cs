using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dictum.Windows.Audio;

internal static class CaptureCheck
{
    internal const int Seconds = 5;
    internal const int Frequency = 1000;
    internal const float Amplitude = 0.1f;

    internal static int Run(TextWriter output, TextWriter error)
    {
        try
        {
            // Finish both trials before printing, so a failed request never looks like a successful comparison.
            var raw = Measure(false);
            var processed = Measure(true);
            output.WriteLine(Format(false, raw));
            output.WriteLine(Format(true, processed));
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"capture-check failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static string Format(bool processed, double ratio) =>
        $"{new MicrophoneCaptureMode(processed).Name}: {ratio.ToString("F6", CultureInfo.InvariantCulture)}";

    private static double Measure(bool processed)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var player = new WasapiPlayerBuilder().WithDevice(render).WithSharedMode().Build();
        using var capture = new WasapiCapture(NullLog.Instance, () => new AudioSettings { ProcessedCapture = processed });
        Exception? failure = null;
        capture.Failed += ex => Interlocked.CompareExchange(ref failure, ex, null);
        player.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is { } ex) Interlocked.CompareExchange(ref failure, ex, null);
        };
        // All samples, including playback, are generated and retained only in memory.
        var tone = new CheckTone();
        player.Init(tone);
        capture.Start(null, Stopwatch.GetTimestamp());
        try
        {
            player.Play();
            Thread.Sleep(TimeSpan.FromSeconds(Seconds));
        }
        finally { player.Stop(); }
        var audio = capture.Stop();
        if (failure is { } failed) throw new InvalidOperationException("Audio trial failed.", failed);
        // simplification: allow up to one second of startup loss; retire this ceiling after hardware latency measurements.
        if (audio.Samples.Length < MonoResampler.WhisperRate * (Seconds - 1))
            throw new InvalidOperationException("The microphone did not deliver enough samples.");
        if (tone.FramesRead < CheckTone.Rate * (Seconds - 1))
            throw new InvalidOperationException("The output did not consume enough samples.");
        return ToneRatio(audio.Samples);
    }

    internal static double ToneRatio(ReadOnlySpan<float> samples)
    {
        // simplification: a single 1 kHz tone measured in 100 ms quadrature windows, rather than a speech/AEC score;
        // retire this diagnostic ceiling when hardware trials require a broadband, latency-aligned reference.
        // The ratio is microphone tone-band mean-square / generated output mean-square (0.1^2 / 2 * 3/5).
        // It includes the silent lead/tail and ambient sound at this frequency; output volume must stay fixed.
        const int window = MonoResampler.WhisperRate / 10;
        double energy = 0;
        int measured = 0;
        for (int start = 0; start + window <= samples.Length; start += window)
        {
            double sine = 0, cosine = 0;
            for (int i = 0; i < window; i++)
            {
                var phase = 2 * Math.PI * Frequency * i / MonoResampler.WhisperRate;
                sine += samples[start + i] * Math.Sin(phase);
                cosine += samples[start + i] * Math.Cos(phase);
            }
            energy += 2 * (sine * sine + cosine * cosine) / window;
            measured += window;
        }
        if (measured == 0) throw new ArgumentException("No complete measurement window.", nameof(samples));
        return energy / measured / (Amplitude * Amplitude / 2 * 3.0 / Seconds);
    }

    private sealed class CheckTone : IWaveProvider
    {
        internal const int Rate = 48000;
        internal int FramesRead { get; private set; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(Span<byte> buffer)
        {
            var frames = Math.Min(buffer.Length / sizeof(float), Rate * Seconds - FramesRead);
            var target = MemoryMarshal.Cast<byte, float>(buffer[..(frames * sizeof(float))]);
            // simplification: fixed five-second trial, three-second tone and 10 ms fades; retire for configurable
            // stimuli if owner hardware checks show this duration or frequency cannot distinguish the two modes.
            for (int i = 0; i < frames; i++)
            {
                var frame = FramesRead++;
                var inside = frame - Rate;
                var envelope = Math.Clamp(Math.Min(inside, 3 * Rate - 1 - inside) / (Rate * 0.01), 0, 1);
                target[i] = (float)(Amplitude * envelope * Math.Sin(2 * Math.PI * Frequency * frame / Rate));
            }
            return frames * sizeof(float);
        }
    }
}
