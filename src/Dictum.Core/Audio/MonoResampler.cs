using NAudio.Dsp;

namespace Dictum.Audio;

/// <summary>
/// Streaming mono resampler to whisper's 16 kHz, built on NAudio's managed WDL resampler in
/// input-driven (feed) mode so capture blocks can be pushed as they arrive. Same engine and mode
/// settings as NAudio's WdlResamplingSampleProvider (interpolating, 2 filter passes).
/// Channel downmix happens before this (SampleDecoder.Downmix) so any device channel count works.
/// </summary>
public sealed class MonoResampler
{
    public const int WhisperRate = 16000;

    private readonly WdlResampler? _wdl;
    private readonly double _ratio;
    private float[] _out = new float[4096];

    public MonoResampler(int inputRate, int outputRate = WhisperRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _ratio = (double)outputRate / inputRate;
        if (inputRate == outputRate) return;
        _wdl = new WdlResampler();
        _wdl.SetMode(true, 2, false);
        _wdl.SetFilterParms();
        _wdl.SetFeedMode(true);
        _wdl.SetRates(inputRate, outputRate);
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Resamples <paramref name="input"/> and hands each produced chunk to <paramref name="sink"/>.</summary>
    public void Process(ReadOnlySpan<float> input, Action<ReadOnlySpan<float>> sink)
    {
        if (_wdl is null)
        {
            if (input.Length > 0) sink(input);
            return;
        }
        var offset = 0;
        while (offset < input.Length)
        {
            var available = input.Length - offset;
            var wanted = _wdl.ResamplePrepare(available, 1, out var inBuffer);
            var n = Math.Min(Math.Min(wanted, available), inBuffer.Length);
            if (n <= 0) break;
            input.Slice(offset, n).CopyTo(inBuffer);
            var maxOut = (int)Math.Ceiling(n * _ratio) + 32;
            if (_out.Length < maxOut) _out = new float[maxOut * 2];
            var produced = _wdl.ResampleOut(_out, n, maxOut, 1);
            if (produced > 0) sink(_out.AsSpan(0, produced));
            offset += n;
        }
    }

    /// <summary>Convenience for whole buffers (WAV files, tests).</summary>
    public static float[] ResampleAll(ReadOnlySpan<float> mono, int inputRate, int outputRate = WhisperRate)
    {
        var resampler = new MonoResampler(inputRate, outputRate);
        var result = new List<float>((int)(mono.Length * (double)outputRate / inputRate) + 64);
        resampler.Process(mono, chunk => { foreach (var s in chunk) result.Add(s); });
        return [.. result];
    }
}
