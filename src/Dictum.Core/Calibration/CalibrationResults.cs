using Dictum.Personal;

namespace Dictum.Calibration;

/// <summary>One recorded calibration sentence: the script text and the audio (16 kHz mono).</summary>
public sealed record CalibrationClip(int Index, string Script, float[] Samples)
{
    public double Seconds => Samples.Length / 16000.0;
}

/// <summary>One clip transcribed by one model with one decoding.</summary>
public sealed record ClipOutcome(int Index, string Heard, double Seconds, double AudioSeconds, WerResult Wer);

/// <summary>One model × decoding combination over every clip (SPEC §16.5).</summary>
public sealed record ComboResult(string Model, int BeamSize, IReadOnlyList<ClipOutcome> Clips)
{
    public string Decoding => BeamSize > 0 ? $"beam search ({BeamSize})" : "greedy";

    /// <summary>Total edits over total script words, all clips together.</summary>
    public double Wer => Words > 0 ? Edits / (double)Words : 0;

    /// <summary>The raw error count behind <see cref="Wer"/> (a follow-up to the first real-voice calibration: ten sentences are about a hundred words, so one error is about one point).</summary>
    public int Edits => Clips.Sum(c => c.Wer.Edits);

    /// <summary>Script words over every clip.</summary>
    public int Words => Clips.Sum(c => c.Wer.ReferenceWords);

    /// <summary>"4.7% (5 of 107 words)": the rate with the count beside it.</summary>
    public string ErrorsText => $"{Wer:P1} ({Edits} of {Words} words)";

    public double AverageSeconds => Clips.Count == 0 ? 0 : Clips.Average(c => c.Seconds);

    /// <summary>Seconds of transcription per 10 s of audio, with clips under 10 s counted as 10 s (see <see cref="CalibrationRanking"/>).</summary>
    public double SecondsPer10s => Clips.Sum(c => Math.Max(c.AudioSeconds, 10)) is var audio and > 0 ? Clips.Sum(c => c.Seconds) / audio * 10 : 0;
}

/// <summary>
/// Ranking (SPEC §16.5): the recommendation is the most accurate combination at or under the latency ceiling of 2.5 s per
/// 10 s of audio, measured on this machine. whisper.cpp works on 30-second windows, so a 4-second clip costs about as much
/// as a 10-second one: each clip is therefore allowed 2.5 s per 10 s of its length but never less than 2.5 s, which is the
/// ceiling a 10-second dictation gets. Near-ties go to the faster combination (a follow-up to the first real-voice
/// result): every combination within one percentage point of the lowest WER counts as tied, because with ten sentences
/// one point is about one word.
/// </summary>
public static class CalibrationRanking
{
    public const double CeilingSecondsPer10s = 2.5;

    /// <summary>WER differences up to this (one percentage point) are a near-tie.</summary>
    public const double NearTie = 0.01;

    public static bool WithinCeiling(ComboResult combo) => combo.Clips.Count > 0 && combo.SecondsPer10s <= CeilingSecondsPer10s;

    public static ComboResult? Recommend(IEnumerable<ComboResult> combos)
    {
        var eligible = combos.Where(WithinCeiling).ToList();
        if (eligible.Count == 0) return null;
        var best = eligible.Min(c => c.Wer);
        // The small epsilon keeps a difference of exactly one point (4.7% vs 5.7%) a near-tie despite floating-point error.
        return eligible.Where(c => c.Wer <= best + NearTie + 1e-9).OrderBy(c => c.AverageSeconds).ThenBy(c => c.Wer).First();
    }
}

/// <summary>A suggestion from calibration, shown unticked: add a word to the dictionary, or add a correction.</summary>
public sealed record MissedWordSuggestion(string Word, CorrectionRule? Correction)
{
    public string DictionaryText => $"Add to dictionary: {Word}";
    public string? CorrectionText => Correction is null ? null : $"Add correction: {Correction.Heard} => {Correction.Written}";
}

/// <summary>
/// Missed words (SPEC §16.5 step 3): a script word that the recommended combination got wrong in a consistent way across
/// clips. "Consistent" here: the word appears at least twice in the script and was missed in at least two of its
/// appearances and in at least two thirds of them. When every miss was heard as the same text, a correction is
/// suggested too. Numbers and words under three letters are never suggested; nor is anything already in the
/// dictionary or the corrections.
/// </summary>
public static class MissedWords
{
    public static IReadOnlyList<MissedWordSuggestion> Find(ComboResult combo, IReadOnlyList<string> script, PersonalDictionary dictionary, Corrections corrections)
    {
        var spelling = ScriptSpelling(script);
        var byWord = combo.Clips.SelectMany(c => c.Wer.Alignment.Select(a => (Clip: c, Word: a)))
            .Where(x => x.Word.Reference.Length >= 3 && !x.Word.Reference.All(char.IsDigit))
            .GroupBy(x => x.Word.Reference, StringComparer.Ordinal);
        var suggestions = new List<MissedWordSuggestion>();
        foreach (var group in byWord)
        {
            var seen = group.Count();
            var misses = group.Where(x => x.Word.Op != AlignOp.Match).ToList();
            if (seen < 2 || misses.Count < 2 || misses.Count * 3 < seen * 2) continue;
            var word = spelling.TryGetValue(group.Key, out var written) ? written : group.Key;
            if (dictionary.Contains(word)) continue;
            var heard = misses.Select(m => m.Word.Heard).Distinct(StringComparer.Ordinal).ToList();
            CorrectionRule? rule = heard is [{ Length: > 0 } stable] && !corrections.Rules.Any(r => r.Heard == WholeWords.Key(stable))
                ? new CorrectionRule(stable, word)
                : null;
            // "heard" is the comparable (normalised) form; a rule that would not change what whisper actually wrote
            // ("Para-Matta", "Kowalski's", number words turned to digits) would do nothing once added, so it is not offered.
            if (rule is not null && misses.Any(m => new Corrections([rule]).Apply(m.Clip.Heard) == m.Clip.Heard)) rule = null;
            suggestions.Add(new MissedWordSuggestion(word, rule));
        }
        return suggestions.OrderBy(s => s.Word, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The script's own spelling of each comparable word ("siobhan" → "Siobhan").</summary>
    private static Dictionary<string, string> ScriptSpelling(IReadOnlyList<string> script)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sentence in script)
        {
            foreach (var token in sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var bare = token.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')');
                if (bare.Length == 0 || !bare.All(c => char.IsLetter(c) || c is '\'' or '-')) continue;
                if (WordErrorRate.Normalise(bare) is [var key]) map.TryAdd(key, bare);
            }
        }
        return map;
    }
}
