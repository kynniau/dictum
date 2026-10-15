using System.Text.RegularExpressions;

namespace Dictum.Llm;

/// <summary>
/// Maps British/Australian and US spellings of a lower-case word to one form, so the validator does not count
/// "colour" in the reply against "color" in the transcript (the prompt asks for Australian spelling; whisper's
/// English models often write US spelling). Covers -our/-or, -ise/-ize, -yse/-yze, -tre/-ter, -ogue/-og and
/// -ence/-ense with their common endings. Applied to both sides alike, so an over-eager mapping ("flour" →
/// "flor") still compares equal; it never decides what is pasted.
/// </summary>
public static partial class SpellingVariants
{
    [GeneratedRegex(@"our(s|ed|ing|ful|ite|ites|able|er|ers)?$")]
    private static partial Regex Our();

    [GeneratedRegex(@"([iy])s(e|es|ed|ing|ation|ations|er|ers)$")]
    private static partial Regex Ise();

    [GeneratedRegex(@"tre(s?)$")]
    private static partial Regex Tre();

    [GeneratedRegex(@"tred$")]
    private static partial Regex Tred();

    [GeneratedRegex(@"ogue(s?)$")]
    private static partial Regex Ogue();

    [GeneratedRegex(@"ence(s?)$")]
    private static partial Regex Ence();

    public static string Normalise(string word)
    {
        if (word.Length < 5) return word; // "our", "four", "hour", "your" stay distinct
        word = Our().Replace(word, "or$1");
        word = Ise().Replace(word, "$1z$2");
        word = Tred().Replace(word, "tered");
        word = Tre().Replace(word, "ter$1");
        word = Ogue().Replace(word, "og$1");
        word = Ence().Replace(word, "ense$1");
        return word;
    }
}
