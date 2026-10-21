using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// Public copy names no specific languages or regions tied to the owner's team (DECISIONS, 21/10/2026). Everything in this
/// repository is public: docs, README, site, code, tests and comments. This sweep fails when any file names one of the words
/// below. The words are spelled in pieces so this file does not contain them itself.
/// </summary>
public class PublicCopyNeutralityTests
{
    private static readonly string[] Words =
    [
        "hin" + "di", "taga" + "log", "ceb" + "uano", "fili" + "pino", "philip" + "pin", "off" + "shore", "ilo" + "cano",
        "hili" + "gaynon", "indian" + " language", "deva" + "nagari", "hing" + "lish", "tag" + "lish",
    ];

    private static readonly string[] SkippedFolders = [".git", ".worktrees", "bin", "obj", "dist", "node_modules", ".vs"];

    private static readonly string[] BinaryExtensions = [".png", ".ico", ".jpg", ".jpeg", ".gif", ".dll", ".exe", ".bin", ".zip", ".msix", ".pdb", ".woff", ".woff2"];

    /// <summary>One entry of Whisper's own language table, <c>("xx", "Name")</c>: the list the app offers, which names every language alike.</summary>
    private static readonly Regex TableEntry = new("""\("[a-z]{2,3}", "[^"]+"\)""", RegexOptions.Compiled);

    private static IEnumerable<string> PublicFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            if (!BinaryExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) yield return file;
        foreach (var sub in Directory.EnumerateDirectories(folder))
            if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                foreach (var file in PublicFiles(sub)) yield return file;
    }

    /// <summary>The text a file contributes to the sweep: all of it, except that Whisper's language table keeps its entries.</summary>
    internal static string Scanned(string path, string text) =>
        string.Equals(Path.GetFileName(path), "WhisperLanguages.cs", StringComparison.Ordinal) ? TableEntry.Replace(text, "") : text;

    internal static List<string> Offences(string path, string text) =>
        Words.Where(w => Scanned(path, text).Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();

    [Fact]
    public void No_public_file_names_a_specific_language_or_region_tied_to_the_owners_team()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var files = PublicFiles(root).ToList();
        Assert.Contains(files, f => f.EndsWith("README.md", StringComparison.Ordinal)); // the sweep really walked the repository
        Assert.Contains(files, f => f.EndsWith("SPEC.md", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("index.html", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("WhisperLanguages.cs", StringComparison.Ordinal));

        var offences = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (text.Contains('\0')) continue; // a binary file under a text-looking name
            foreach (var word in Offences(file, text)) offences.Add($"{Path.GetRelativePath(root, file)}: {word}");
        }
        Assert.True(offences.Count == 0, "Public copy names a specific language or region: " + string.Join("; ", offences));
    }

    [Fact]
    public void The_sweep_catches_a_word_in_any_file_and_forgives_only_the_language_table()
    {
        Assert.NotEmpty(Offences("README.md", "speakers of " + Words[0] + " dictate"));
        Assert.NotEmpty(Offences("a.cs", "// " + Words[1].ToUpperInvariant()));
        Assert.NotEmpty(Offences("WhisperLanguages.cs", "/// the " + Words[5] + " teams"));
        Assert.Empty(Offences("WhisperLanguages.cs", $"(\"hi\", \"{Words[0]}\"), (\"es\", \"Spanish\")"));
        Assert.NotEmpty(Offences("README.md", $"(\"hi\", \"{Words[0]}\")")); // the allowance is for the table file only
    }
}
