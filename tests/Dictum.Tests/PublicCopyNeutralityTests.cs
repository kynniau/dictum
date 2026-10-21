using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// Public copy names no specific languages or regions tied to the owner's team (DECISIONS, 21/10/2026). What git lists as
/// public in this repository (tracked files, plus untracked files that are not ignored) is public: docs, README, site, code,
/// tests and comments. This sweep fails when any of them names one of the words below. The words are spelled in pieces so
/// this file does not contain them itself. Ignored build output and test results are not public and are not read.
/// </summary>
[Collection("Process-wide state")] // starts git processes: never beside the timing-based download tests
public class PublicCopyNeutralityTests
{
    private static readonly string[] Words =
    [
        "hin" + "di", "taga" + "log", "ceb" + "uano", "ceb" + "u", "fili" + "pino", "philip" + "pin", "off" + "shore", "ilo" + "cano",
        "hili" + "gaynon", "deva" + "nagari", "hing" + "lish", "tag" + "lish",
        "ind" + "ia", "ind" + "ian", "subcon" + "tinent", "bis" + "aya", "vis" + "aya", "man" + "ila",
    ];

    private static readonly string[] BinaryExtensions = [".png", ".ico", ".jpg", ".jpeg", ".gif", ".dll", ".exe", ".bin", ".zip", ".msix", ".pdb", ".woff", ".woff2", ".mp4"];

    /// <summary>The one file that holds Whisper's own language table, as git prints its path.</summary>
    private const string TableFile = "src/Dictum.Core/Transcribe/WhisperLanguages.cs";

    /// <summary>One entry of Whisper's own language table, <c>("xx", "Name")</c>: the list the app offers, which names every language alike.</summary>
    private static readonly Regex TableEntry = new("""\("[a-z]{2,3}", "[A-Z][A-Za-z ]+"\)""", RegexOptions.Compiled);

    private static readonly string? GitPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder.Trim('"'), "git.exe")).FirstOrDefault(File.Exists);

    /// <summary>Runs git in <paramref name="folder"/> with no user or system configuration and returns its standard output.</summary>
    private static string Git(string folder, params string[] args)
    {
        Assert.SkipWhen(GitPath is null, "git is not on PATH.");
        var empty = Path.Combine(Path.GetTempPath(), "dictum-no-gitconfig");
        File.WriteAllText(empty, "");
        var psi = new ProcessStartInfo(GitPath!)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_CONFIG_GLOBAL"] = empty;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        if (!process.WaitForExit(TimeSpan.FromMinutes(2))) { process.Kill(entireProcessTree: true); throw new TimeoutException("git did not finish in 2 minutes."); }
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} exited {process.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    /// <summary>The public set: the paths (relative, forward slashes) git lists as tracked or untracked-but-not-ignored.</summary>
    internal static List<string> PublicPaths(string root) =>
        Git(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The text a file contributes to the sweep: all of it, except that Whisper's language table keeps its entries.</summary>
    internal static string Scanned(string relativePath, string text)
    {
        if (!string.Equals(relativePath, TableFile, StringComparison.Ordinal)) return text;
        var start = text.IndexOf("Table =", StringComparison.Ordinal);
        var open = start < 0 ? -1 : text.IndexOf('[', start);
        var close = open < 0 ? -1 : text.IndexOf("];", open, StringComparison.Ordinal);
        if (close < 0) return text; // the table is not where it was: nothing is forgiven
        return text[..open] + TableEntry.Replace(text[open..close], "") + text[close..];
    }

    /// <summary>What the script rule reports: text in that block is allowed in test fixtures only (they test a script rule, not a language).</summary>
    internal const string ScriptRule = "a character in U+0900-U+097F outside tests/";

    internal static List<string> Offences(string relativePath, string text)
    {
        var found = Words.Where(w => Scanned(relativePath, text).Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!relativePath.StartsWith("tests/", StringComparison.Ordinal) && text.Any(c => c is >= '\u0900' and <= '\u097F')) found.Add(ScriptRule);
        return found;
    }

    /// <summary>Every "path: word" offence in the files <paramref name="root"/> lists as public.</summary>
    internal static List<string> Sweep(string root)
    {
        var offences = new List<string>();
        foreach (var path in PublicPaths(root))
        {
            var file = Path.Combine(root, path);
            if (!File.Exists(file) || BinaryExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            if (text.Contains('\0')) continue; // a binary file under a text-looking name
            foreach (var word in Offences(path, text)) offences.Add($"{path}: {word}");
        }
        return offences;
    }

    private static string Capitalised(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    [Fact]
    public void No_public_file_names_a_specific_language_or_region_tied_to_the_owners_team()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var paths = PublicPaths(root);
        Assert.Contains("README.md", paths); // the sweep really read the repository
        Assert.Contains("docs/SPEC.md", paths);
        Assert.Contains("site/index.html", paths);
        Assert.Contains(TableFile, paths);

        var offences = Sweep(root);
        Assert.True(offences.Count == 0, "Public copy names a specific language or region: " + string.Join("; ", offences));
    }

    [Fact]
    public void The_sweep_reads_what_git_lists_as_public_and_nothing_ignored()
    {
        var dir = Directory.CreateTempSubdirectory("dictum-neutrality-");
        try
        {
            Git(dir.FullName, "init", "-q");
            File.WriteAllText(Path.Combine(dir.FullName, ".gitignore"), "out/\n");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "out"));
            File.WriteAllText(Path.Combine(dir.FullName, "out", "generated.xml"), "a " + Words[0] + " word"); // ignored build output
            File.WriteAllText(Path.Combine(dir.FullName, "clean.md"), "nothing to see");
            Git(dir.FullName, "add", ".gitignore", "clean.md");
            Assert.Empty(Sweep(dir.FullName)); // an ignored file with a word does not fail the sweep

            File.WriteAllText(Path.Combine(dir.FullName, "tracked.md"), "a " + Words[0] + " word");
            Git(dir.FullName, "add", "tracked.md");
            Assert.Equal(["tracked.md: " + Words[0]], Sweep(dir.FullName)); // a tracked file with a word does

            Git(dir.FullName, "rm", "-q", "--cached", "tracked.md");
            File.Delete(Path.Combine(dir.FullName, "tracked.md"));
            File.WriteAllText(Path.Combine(dir.FullName, "new.txt"), "a " + Words[1] + " word"); // untracked and not ignored: public once added
            Assert.Equal(["new.txt: " + Words[1]], Sweep(dir.FullName));

            File.Delete(Path.Combine(dir.FullName, "new.txt"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "src"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "tests"));
            File.WriteAllText(Path.Combine(dir.FullName, "src", "a.cs"), "// " + (char)0x0964 + " mark");
            File.WriteAllText(Path.Combine(dir.FullName, "tests", "a.cs"), "// " + (char)0x0964 + " mark"); // a test fixture may use the script
            Git(dir.FullName, "add", "src/a.cs", "tests/a.cs");
            Assert.Equal(["src/a.cs: " + ScriptRule], Sweep(dir.FullName)); // anywhere else it is described by code point
        }
        finally
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal; // git objects are read-only
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void The_sweep_catches_every_word_and_forgives_only_the_language_table()
    {
        Assert.All(Words, word => Assert.NotEmpty(Offences("README.md", "a " + word.ToUpperInvariant() + " here")));
        Assert.Equal([ScriptRule], Offences("docs/SPEC.md", "end mark " + (char)0x0964)); // the script rule: any file outside tests/
        Assert.Equal([ScriptRule], Offences("src/Dictum.Core/X.cs", "'" + (char)0x097F + "'"));
        Assert.Empty(Offences("tests/Dictum.Tests/X.cs", "end mark " + (char)0x0964)); // a test fixture may use the script
        Assert.Empty(Offences("docs/SPEC.md", "next to the block: " + (char)0x08FF + (char)0x0980)); // only U+0900-U+097F
        string[] regionWords = ["ind" + "ia", "ind" + "ian", "subcon" + "tinent", "bis" + "aya", "vis" + "aya", "man" + "ila", "ceb" + "u"];
        Assert.All(regionWords, word => Assert.Contains(word, Words)); // the region words stay on the list

        var table = "private static readonly (string Code, string Name)[] Table =\n[\n    (\"es\", \"Spanish\"), (\"xx\", \"" + Capitalised(Words[0]) + "\"), (\"yy\", \"Haitian Creole\"),\n];\n";
        Assert.Empty(Offences(TableFile, table)); // table entries inside the table are allowed
        Assert.NotEmpty(Offences("README.md", table)); // the allowance is for that file only
        Assert.NotEmpty(Offences("src/Other/WhisperLanguages.cs", table)); // and for its full path, not its file name
        Assert.NotEmpty(Offences(TableFile, "// (\"zz\", \"" + Capitalised(Words[0]) + "\")\n" + table)); // a table-shaped comment before the table is caught
        Assert.NotEmpty(Offences(TableFile, table + "// (\"zz\", \"" + Capitalised(Words[0]) + "\")\n")); // and after it
        Assert.NotEmpty(Offences(TableFile, table + "/// the " + Words[6] + " teams")); // so is prose
        Assert.NotEmpty(Offences(TableFile, table.Replace("(\"xx\", \"" + Capitalised(Words[0]) + "\")", "(\"xx\", \"" + Words[0] + "\")"))); // an entry whose name is not a capitalised name is caught
    }
}
