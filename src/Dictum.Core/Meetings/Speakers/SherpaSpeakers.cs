using SherpaOnnx;

namespace Dictum.Meetings.Speakers;

/// <summary>
/// The CAM++ voice print through sherpa-onnx 2.0.1's SpeakerEmbeddingExtractor (names read from the package's own sherpa-onnx.dll
/// by reflection, 07/11/2026: SpeakerEmbeddingExtractor(SpeakerEmbeddingExtractorConfig), CreateStream(), IsReady(OnlineStream),
/// Compute(OnlineStream), Dim; OnlineStream.AcceptWaveform(int, float[]) and InputFinished()). sherpa-onnx computes the fbank
/// features in C++, so Dictum does not port Kaldi's fbank to C#. On the CPU only. IsReady is true once the stream holds one
/// fbank frame (sherpa-onnx/csrc/speaker-embedding-extractor-general-impl.h at commit af2d8d81, read 07/11/2026:
/// GetNumProcessedFrames() &lt; NumFramesReady()), so <see cref="SpeakerSeparator"/> sets its own, longer floor.
/// </summary>
public sealed class SherpaVoiceEmbedder : IVoiceEmbedder, IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly object _gate = new();

    public SherpaVoiceEmbedder(string modelPath, int threads)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Speaker embedding model not found.", modelPath);
        var config = new SpeakerEmbeddingExtractorConfig { Model = modelPath, NumThreads = threads, Provider = "cpu", Debug = 0 };
        _extractor = new SpeakerEmbeddingExtractor(config);
        Dimension = _extractor.Dim;
        if (Dimension <= 0) throw new InvalidDataException("The speaker embedding model did not load.");
    }

    public int Dimension { get; }

    public float[]? Embed(float[] samples16kMono)
    {
        lock (_gate)
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(16000, samples16kMono);
            stream.InputFinished();
            return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _extractor.Dispose();
    }
}
