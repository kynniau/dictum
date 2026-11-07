namespace Dictum.Meetings.Speakers;

/// <summary>
/// Agglomerative complete-linkage clustering on cosine distance, cut at a distance threshold or at a fixed number of clusters:
/// the same rule as sherpa-onnx's FastClustering (sherpa-onnx/csrc/fast-clustering.cc at the 2.0.1 package's commit
/// af2d8d81dbfa61c4cea4c568da78f3982edca005, read 07/11/2026: rows normalised, distance 1 - cosine floored at 0, hclust-cpp
/// complete linkage, then cutree_cdist, which applies every merge whose height is below the threshold, or cutree_k). Ported here
/// so a test can drive it with fixed vectors.
/// </summary>
public static class SpeakerClustering
{
    /// <summary>
    /// A cluster index per embedding, numbered in order of first appearance (the first embedding's cluster is 0). Each step merges
    /// the closest two clusters; of equally close pairs, the one whose first cluster, then second, starts earliest.
    /// </summary>
    /// <param name="threshold">Merges stop once the closest two clusters are this far apart or further (cosine distance).</param>
    /// <param name="count">When above 0, merges continue until this many clusters remain, whatever the threshold.</param>
    // The distances are kept once, as floats in one triangle (a meeting's 2,400 windows take about 11 MB), and each cluster
    // remembers its nearest later cluster. Complete linkage only ever lengthens distances to a merged cluster, so after a merge only
    // the rows that pointed at either half are searched again, instead of every pair of clusters on every step.
    // Measured 07/11/2026 on the development PC: 1,200 windows of 512 numbers around four voices in 0.74 s, 2,400 in 3.04 s.
    // simplification: a row whose nearest cluster merged is searched in full again. Ceiling: the worst case is still n cubed, and
    // the triangle grows with the square (an hour of others' speech, about 3,600 windows, is about 26 MB). Trigger to retire: a
    // clustering step that takes more than a few seconds; then port hclust-cpp's nearest-neighbour chain.
    public static int[] Cluster(IReadOnlyList<float[]> embeddings, double threshold, int count = 0)
    {
        var n = embeddings.Count;
        if (n == 0) return [];
        var unit = embeddings.Select(Normalised).ToArray();
        var distance = new float[(long)n * (n - 1) / 2];
        long At(int i, int j) => i < j ? (long)i * (2L * n - i - 1) / 2 + (j - i - 1) : At(j, i);
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                distance[At(i, j)] = (float)Math.Max(0, 1 - Dot(unit[i], unit[j]));

        var active = Enumerable.Repeat(true, n).ToArray();
        var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToArray();
        var nearest = new int[n];
        var nearestDistance = new float[n];
        void Search(int i) // the nearest active cluster after i, the earliest of equals
        {
            (nearest[i], nearestDistance[i]) = (-1, float.PositiveInfinity);
            for (var j = i + 1; j < n; j++)
                if (active[j] && distance[At(i, j)] < nearestDistance[i]) (nearest[i], nearestDistance[i]) = (j, distance[At(i, j)]);
        }
        for (var i = 0; i < n; i++) Search(i);

        for (var left = n; left > 1 && (count <= 0 || left > count); left--)
        {
            var a = -1;
            for (var i = 0; i < n; i++)
                if (active[i] && nearest[i] >= 0 && (a < 0 || nearestDistance[i] < nearestDistance[a])) a = i;
            var (b, height) = (nearest[a], (double)nearestDistance[a]);
            if (count <= 0 && height >= threshold) break; // cutree_cdist: only merges below the threshold
            active[b] = false;
            members[a].AddRange(members[b]);
            for (var k = 0; k < n; k++)
                if (active[k] && k != a) distance[At(a, k)] = Math.Max(distance[At(a, k)], distance[At(b, k)]); // complete linkage
            for (var i = 0; i < a; i++)
            {
                if (!active[i]) continue;
                if (nearest[i] == a || nearest[i] == b) Search(i);
                else if (distance[At(i, a)] == nearestDistance[i] && a < nearest[i]) nearest[i] = a; // now an equal, earlier pair
            }
            for (var i = a; i < b; i++)
                if (active[i] && (i == a || nearest[i] == b)) Search(i);
        }

        var labels = new int[n];
        var label = 0;
        for (var i = 0; i < n; i++) // a cluster is kept at its earliest member, so this is first-appearance order
        {
            if (!active[i]) continue;
            foreach (var member in members[i]) labels[member] = label;
            label++;
        }
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
