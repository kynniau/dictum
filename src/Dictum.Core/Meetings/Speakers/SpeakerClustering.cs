namespace Dictum.Meetings.Speakers;

/// <summary>
/// Agglomerative complete-linkage clustering on cosine distance, cut at a distance threshold or at a fixed number of clusters:
/// the same rule as sherpa-onnx's FastClustering (sherpa-onnx/csrc/fast-clustering.cc at the 2.0.1 package's commit
/// af2d8d81dbfa61c4cea4c568da78f3982edca005, read 07/11/2026: rows normalised, distance 1 - cosine floored at 0, hclust-cpp
/// complete linkage, then cutree_cdist, which applies every merge whose height is below the threshold, or cutree_k). sherpa-onnx's
/// C# API exposes that clusterer only inside its whole diarization pipeline, so it is ported here, where a test can drive it with
/// fixed vectors.
/// </summary>
public static class SpeakerClustering
{
    /// <summary>
    /// A cluster index per embedding, numbered in order of first appearance (the first embedding's cluster is 0).
    /// </summary>
    /// <param name="threshold">Merges stop once the closest two clusters are this far apart or further (cosine distance).</param>
    /// <param name="count">When above 0, merges continue until this many clusters remain, whatever the threshold.</param>
    // simplification: the naive O(n³) complete-linkage loop; a meeting has a few hundred turns, which takes well under a second.
    // Ceiling: thousands of turns (a meeting of many hours). Trigger to retire: a separation that takes more than a few seconds
    // in the clustering step; then port hclust-cpp's nearest-neighbour-chain algorithm.
    public static int[] Cluster(IReadOnlyList<float[]> embeddings, double threshold, int count = 0)
    {
        var n = embeddings.Count;
        if (n == 0) return [];
        var unit = embeddings.Select(Normalised).ToArray();
        var distance = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                distance[i, j] = distance[j, i] = Math.Max(0, 1 - Dot(unit[i], unit[j]));
        var clusters = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        while (clusters.Count > 1 && (count <= 0 || clusters.Count > count))
        {
            var (a, b, height) = (-1, -1, double.PositiveInfinity);
            for (var i = 0; i < clusters.Count; i++)
                for (var j = i + 1; j < clusters.Count; j++)
                {
                    var link = clusters[i].Max(x => clusters[j].Max(y => distance[x, y])); // complete linkage
                    if (link < height) (a, b, height) = (i, j, link);
                }
            if (count <= 0 && height >= threshold) break; // cutree_cdist: only merges below the threshold
            clusters[a].AddRange(clusters[b]);
            clusters.RemoveAt(b);
        }
        var labels = new int[n];
        var order = clusters.OrderBy(c => c.Min()).ToList();
        for (var label = 0; label < order.Count; label++)
            foreach (var member in order[label]) labels[member] = label;
        return labels;
    }

    private static float[] Normalised(float[] v)
    {
        var length = Math.Sqrt(v.Sum(x => (double)x * x));
        return length == 0 ? v : v.Select(x => (float)(x / length)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Embeddings differ in length.");
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
