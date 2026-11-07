using SherpaOnnx;

namespace Dictum.Meetings.Speakers;

/// <summary>
/// The CAM++ voice print through sherpa-onnx 2.0.1's SpeakerEmbeddingExtractor (names read from the package's own sherpa-onnx.dll
/// by reflection, 07/11/2026: SpeakerEmbeddingExtractor(SpeakerEmbeddingExtractorConfig), CreateStream(), IsReady(OnlineStream),
/// Compute(OnlineStream), Dim; OnlineStream.AcceptWaveform(int, float[]) and InputFinished()). sherpa-onnx computes the fbank
/// features in C++, so Dictum does not port Kaldi's fbank to C#. On the CPU only.
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

/// <summary>
/// Speech turns from sherpa-onnx's OfflineSpeakerDiarization (pyannote segmentation-3.0, the CAM++ embedding and its fast clustering
/// at the same threshold): each segment it returns holds one voice, and becomes one turn. Its own speaker numbers are dropped:
/// <see cref="SpeakerSeparator"/> embeds every turn and clusters them itself (<see cref="SpeakerClustering"/>), the step the tests
/// drive with fixed vectors.
/// </summary>
// simplification: the turns come from sherpa's whole diarization pipeline, so the audio is embedded twice (once inside it, once per
// turn). Ceiling: separation takes roughly twice the embedding time. Trigger to retire: sherpa-onnx's C API exposing the
// segmentation step on its own, or a measured separation time the owner finds too slow.
public sealed class SherpaTurnFinder : ISpeakerTurnFinder, IDisposable
{
    private readonly string _segmentation, _embedding;
    private readonly int _threads;

    public SherpaTurnFinder(string segmentationPath, string embeddingPath, int threads)
    {
        if (!File.Exists(segmentationPath)) throw new FileNotFoundException("Speaker segmentation model not found.", segmentationPath);
        if (!File.Exists(embeddingPath)) throw new FileNotFoundException("Speaker embedding model not found.", embeddingPath);
        (_segmentation, _embedding, _threads) = (segmentationPath, embeddingPath, threads);
    }

    public IReadOnlyList<(double Start, double End)> FindTurns(float[] samples16kMono, double threshold, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _segmentation;
        config.Segmentation.NumThreads = _threads;
        config.Embedding.Model = _embedding;
        config.Embedding.NumThreads = _threads;
        config.Clustering.NumClusters = -1; // cut by threshold
        config.Clustering.Threshold = (float)threshold;
        using var diarization = new OfflineSpeakerDiarization(config);
        if (diarization.SampleRate != 16000) throw new InvalidDataException($"The segmentation model expects {diarization.SampleRate} Hz audio, not 16000 Hz.");
        // The native side reports progress and ignores the return value (sherpa-onnx c-api.h): it cannot be stopped mid-run.
        OfflineSpeakerDiarizationProgressCallback report = (done, total, _) => { if (total > 0) progress?.Report((double)done / total); return 0; };
        var segments = diarization.ProcessWithCallback(samples16kMono, report, IntPtr.Zero);
        GC.KeepAlive(report);
        cancellationToken.ThrowIfCancellationRequested();
        return segments.Where(s => s.End > s.Start).Select(s => ((double)s.Start, (double)s.End)).OrderBy(t => t.Item1).ToArray();
    }

    public void Dispose() { }
}
