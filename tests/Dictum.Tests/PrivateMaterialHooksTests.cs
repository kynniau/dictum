using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// The guard rails that keep recordings, transcripts and personal test words out of this public repository:
/// .githooks/pre-commit, .githooks/pre-push and the shared .githooks/private-guard.sh. Each test builds a throwaway
/// repository that carries the real hooks and drives them through git itself, so a change to a rule fails here.
/// Invented words only. The real denylist is never read: DICTUM_PRIVATE_DENYLIST always points inside the scratch folder.
/// </summary>
public sealed class PrivateMaterialHooksTests : IDisposable
{
    private const string Word = "zebrafish-7";

    private static readonly string? GitPath = FindOnPath("git.exe");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dictum-hooks-");
    private readonly string _denylist;
    private readonly string _emptyConfig;

    public PrivateMaterialHooksTests()
    {
        _denylist = Path.Combine(_dir.FullName, "denylist.txt");
        File.WriteAllText(_denylist, $"# invented words, never real data\n{Word}\n\n   Quokka Meeting 42   \n");
        _emptyConfig = Path.Combine(_dir.FullName, "empty-gitconfig");
        File.WriteAllText(_emptyConfig, "");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in _dir.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal; // git objects are read-only
            _dir.Delete(recursive: true);
        }
        catch (IOException) { } // a leftover temp folder is not a test failure
        catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), exe)).FirstOrDefault(File.Exists);

    /// <summary>Runs git (by full path) with no user or system configuration, and the denylist pointed at the scratch one unless <paramref name="env"/> says otherwise (a null value removes a variable).</summary>
    private (int Exit, string Output) Git(string folder, string[] args, Dictionary<string, string?>? env = null)
    {
        Assert.SkipWhen(GitPath is null, "git is not on PATH.");
        var psi = new ProcessStartInfo(GitPath!)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_CONFIG_GLOBAL"] = _emptyConfig;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["DICTUM_PRIVATE_DENYLIST"] = _denylist;
        foreach (var (name, value) in env ?? []) { if (value is null) psi.Environment.Remove(name); else psi.Environment[name] = value; }
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args)} did not finish in 2 minutes.");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static void Ok((int Exit, string Output) result) => Assert.True(result.Exit == 0, $"git exited {result.Exit}: {result.Output}");

    /// <summary>A repository that carries the real hooks, switched on the way a clone would switch them on.</summary>
    private string NewRepo(string? path = null)
    {
        var repo = Directory.CreateDirectory(path ?? Path.Combine(_dir.FullName, "work")).FullName;
        Ok(Git(repo, ["init", "-q", "-b", "main", "."]));
        File.AppendAllText(Path.Combine(repo, ".git", "config"), // written directly: each git call costs about a tenth of a second here
            "[user]\n\tname = Hook Test\n\temail = hooktest@example.invalid\n[core]\n\tautocrlf = false\n\thooksPath = .githooks\n");
        var hooks = Directory.CreateDirectory(Path.Combine(repo, ".githooks")).FullName;
        foreach (var file in Directory.GetFiles(Path.Combine(FixBatchDownloadTests.RepoRoot(), ".githooks")))
        {
            File.Copy(file, Path.Combine(hooks, Path.GetFileName(file)));
        }
        return repo;
    }

    /// <summary>A repository with a bare remote called origin, and one clean commit already pushed.</summary>
    private string NewRepoWithRemote()
    {
        var repo = NewRepo();
        Ok(Git(_dir.FullName, ["init", "-q", "--bare", "remote.git"]));
        File.AppendAllText(Path.Combine(repo, ".git", "config"),
            $"[remote \"origin\"]\n\turl = {Path.Combine(_dir.FullName, "remote.git").Replace('\\', '/')}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        Write(repo, "readme.txt", "hello\n");
        Stage(repo, "readme.txt");
        Assert.Equal((0, ""), Commit(repo, "first"));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "-u", "origin", "main"]));
        return repo;
    }

    private static void Write(string repo, string path, string text = "x\n")
    {
        var full = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void WriteBytes(string repo, string path, int length)
    {
        var full = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[length]);
    }

    private void Stage(string repo, params string[] paths) => Ok(Git(repo, ["add", "-f", "--", .. paths]));

    private (int Exit, string Output) Commit(string repo, string message = "change", bool noVerify = false, Dictionary<string, string?>? env = null) =>
        Git(repo, noVerify ? ["commit", "-q", "--no-verify", "-m", message] : ["commit", "-q", "-m", message], env);

    private static Dictionary<string, string?> Denylist(string path) => new() { ["DICTUM_PRIVATE_DENYLIST"] = path };

    /// <summary>True when the hook output names exactly this path (as a whole token at the end of a line).</summary>
    private static bool Lists(string output, string path) => Regex.IsMatch(output, $@"\s{Regex.Escape(path)}\r?$", RegexOptions.Multiline);

    // ---- pre-commit ----

    [Fact]
    public void A_clean_commit_and_a_clean_push_print_nothing()
    {
        var repo = NewRepoWithRemote(); // its own commit and push are asserted silent
        Write(repo, "docs/notes.md", "more\n");
        Stage(repo, "docs/notes.md");
        Assert.Equal((0, ""), Commit(repo, "second"));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "main"]));
    }

    [Fact]
    public void Media_files_and_private_folders_are_refused_by_name_in_any_case_and_nothing_else_is_named()
    {
        var repo = NewRepo();
        string[] refused =
        [
            "memo.m4a", "RECORDING.M4A", "Aufnahme für María.mp3", "a/b/clip.WebM", "tone.wav", "tests/fixtures/sub/tone.wav",
            "private/a.txt", "docs/Recordings/a.txt", "x/y/TRANSCRIPTS/z/a.txt",
        ];
        string[] allowed = ["tests/fixtures/tone.wav", "notes.txt", "my-private-notes.txt", "src/Private.cs", "Privateer/a.txt", "recordings.txt"];
        foreach (var path in refused.Concat(allowed)) Write(repo, path);
        Stage(repo, [.. refused, .. allowed]);

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.Contains("commit refused", output);
        Assert.All(refused, path => Assert.True(Lists(output, path), $"{path} is not named in: {output}"));
        Assert.All(allowed, path => Assert.False(Lists(output, path), $"{path} should not be named in: {output}"));
    }

    [Fact]
    public void A_file_over_5_MB_is_refused_and_one_of_exactly_5_MiB_is_not()
    {
        var repo = NewRepo();
        WriteBytes(repo, "exact.dat", 5 * 1024 * 1024);
        Stage(repo, "exact.dat");
        Assert.Equal((0, ""), Commit(repo, "exact"));

        WriteBytes(repo, "over.dat", 5 * 1024 * 1024 + 1);
        WriteBytes(repo, "six.dat", 6 * 1024 * 1024);
        Stage(repo, "over.dat", "six.dat");
        var (exit, output) = Commit(repo, "over");

        Assert.Equal(1, exit);
        Assert.True(Lists(output, "over.dat") && Lists(output, "six.dat"), output);
        Assert.False(Lists(output, "exact.dat"), output);
    }

    [Fact]
    public void Denylist_words_are_refused_in_any_case_by_file_name_only_and_never_printed()
    {
        var repo = NewRepo();
        Write(repo, "scenario.md", "call about ZebraFish-7 on Monday\n");
        Write(repo, "agenda.md", "agenda: QUOKKA MEETING 42 notes\n"); // the entry is padded and mixed-case in the file
        Write(repo, "fine.md", "nothing private here\n");
        Write(repo, "a.txt", $"about {Word}\n");
        Write(repo, "[ab].txt", "nothing private here\n"); // a clean file named like a glob that would also match a.txt
        Stage(repo, "scenario.md", "agenda.md", "fine.md", "a.txt", "[ab].txt");

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.True(Lists(output, "scenario.md") && Lists(output, "agenda.md") && Lists(output, "a.txt"), output);
        Assert.False(Lists(output, "fine.md") || Lists(output, "[ab].txt"), output); // file names are literal, never globs
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quokka", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_denylist_word_in_a_file_name_is_refused_and_binary_content_is_not_scanned()
    {
        var repo = NewRepo();
        File.WriteAllBytes(Path.Combine(repo, $"{Word}.dat"), [0, 1, 2]);
        File.WriteAllBytes(Path.Combine(repo, "data.dat"), [0, .. Encoding.ASCII.GetBytes(Word), 0]);
        Stage(repo, $"{Word}.dat", "data.dat");

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.True(Lists(output, $"{Word}.dat"), output);
        Assert.False(Lists(output, "data.dat"), output); // the documented gap: a binary file is checked by name, type and size only
    }

    [Fact]
    public void The_denylist_may_have_a_byte_order_mark_CRLF_line_ends_comments_and_padding()
    {
        var repo = NewRepo();
        var odd = Path.Combine(_dir.FullName, "odd-denylist.txt");
        File.WriteAllBytes(odd, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($"  {Word}  \r\n# a comment\r\n\r\n")]); // the word is on the first line, after the mark
        Write(repo, "leak.md", "x ZEBRAFISH-7\n");
        Write(repo, "fine.md");
        Stage(repo, "leak.md", "fine.md");

        var (exit, output) = Commit(repo, env: Denylist(odd));

        Assert.Equal(1, exit);
        Assert.True(Lists(output, "leak.md"), output);
        Assert.False(Lists(output, "fine.md"), output);
    }

    [Fact]
    public void A_UTF_16_denylist_is_refused_because_it_would_load_but_never_match()
    {
        var repo = NewRepo();
        var utf16 = Path.Combine(_dir.FullName, "utf16-denylist.txt");
        File.WriteAllText(utf16, Word + "\r\n", Encoding.Unicode);
        Write(repo, "fine.md");
        Stage(repo, "fine.md");

        var (exit, output) = Commit(repo, env: Denylist(utf16));

        Assert.Equal(1, exit);
        Assert.Contains("not plain text", output);
    }

    [Fact]
    public void Without_a_denylist_the_check_is_skipped_and_one_line_says_so()
    {
        var repo = NewRepo();
        Write(repo, "leak.md", $"about {Word}\n");
        Stage(repo, "leak.md");

        var (exit, output) = Commit(repo, env: Denylist(Path.Combine(_dir.FullName, "missing", "denylist.txt")));

        Assert.Equal(0, exit);
        var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("no denylist", line);
    }

    [Fact]
    public void A_line_already_in_the_repository_never_blocks_an_edit_to_its_file()
    {
        var repo = NewRepo();
        Write(repo, "old.md", $"an old line about {Word}\n");
        Stage(repo, "old.md");
        Assert.Equal(0, Commit(repo, "old", env: Denylist(Path.Combine(_dir.FullName, "none.txt"))).Exit); // committed while no denylist existed

        File.AppendAllText(Path.Combine(repo, "old.md"), "a harmless new line\n");
        Stage(repo, "old.md");
        Assert.Equal((0, ""), Commit(repo, "harmless edit"));

        File.AppendAllText(Path.Combine(repo, "old.md"), $"and now {Word} again\n");
        Stage(repo, "old.md");
        var (exit, output) = Commit(repo, "again");
        Assert.Equal(1, exit);
        Assert.True(Lists(output, "old.md"), output);
    }

    [Fact]
    public void Deleting_a_file_that_should_not_be_there_is_always_allowed()
    {
        var repo = NewRepo();
        Write(repo, "memo.m4a");
        Write(repo, $"{Word}.txt"); // a file whose name holds a denylist word
        Stage(repo, "memo.m4a", $"{Word}.txt");
        Assert.Equal(0, Commit(repo, "oops", noVerify: true).Exit);
        Ok(Git(repo, ["rm", "-q", "memo.m4a", $"{Word}.txt"]));

        Assert.Equal((0, ""), Commit(repo, "remove them"));
    }

    // ---- pre-push ----

    [Fact]
    public void Pre_push_catches_a_commit_made_with_no_verify_and_the_remote_does_not_move()
    {
        var repo = NewRepoWithRemote();
        var before = Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output;
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);

        var (exit, output) = Git(repo, ["push", "origin", "main"]);

        Assert.NotEqual(0, exit);
        Assert.Contains("push refused", output);
        Assert.True(Lists(output, "sneaky.m4a"), output);
        Assert.Equal(before, Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output);
    }

    [Fact]
    public void Pre_push_catches_a_file_added_in_one_commit_and_deleted_in_the_next()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        Ok(Git(repo, ["rm", "-q", "sneaky.m4a"]));
        Assert.Equal((0, ""), Commit(repo, "remove it"));
        Ok(Git(repo, ["diff", "--quiet", "origin/main", "HEAD"])); // the net difference is empty, so a tip-to-tip check would pass

        var (exit, output) = Git(repo, ["push", "origin", "main"]);

        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
    }

    [Fact]
    public void Pre_push_applies_the_denylist_to_every_commit_pushed_even_one_later_undone()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "leak.md", $"about {Word}\n");
        Stage(repo, "leak.md");
        Assert.Equal(0, Commit(repo, "leak", noVerify: true).Exit);
        Ok(Git(repo, ["rm", "-q", "leak.md"]));
        Assert.Equal((0, ""), Commit(repo, "undo it"));

        var (exit, output) = Git(repo, ["push", "origin", "main"]);

        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "leak.md"), output);
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pre_push_lets_clean_work_through_including_a_new_branch_a_tag_and_a_deletion()
    {
        var repo = NewRepoWithRemote();
        Ok(Git(repo, ["checkout", "-q", "-b", "feature"]));
        Write(repo, "feature.txt");
        Stage(repo, "feature.txt");
        Assert.Equal((0, ""), Commit(repo, "feature work"));

        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "feature"])); // a new branch
        Ok(Git(repo, ["tag", "v1"]));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "v1"])); // a tag
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "--delete", "feature"])); // a deletion sends no files
    }

    [Fact]
    public void Pre_push_judges_a_new_branch_by_what_the_remote_does_not_already_hold()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "oops.m4a");
        Stage(repo, "oops.m4a");
        Assert.Equal(0, Commit(repo, "oops", noVerify: true).Exit); // local main now holds a recording that is not pushed
        Ok(Git(repo, ["branch", "bad", "main"]));

        var (exit, output) = Git(repo, ["push", "origin", "bad"]); // a new branch holding it is refused
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "oops.m4a"), output);

        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "main"])); // now the remote holds it already (pushed past the hook)
        Ok(Git(repo, ["checkout", "-q", "-b", "later"]));
        Write(repo, "later.txt");
        Stage(repo, "later.txt");
        Assert.Equal((0, ""), Commit(repo, "later work"));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "later"])); // only what the remote lacks is judged
    }

    // ---- where the denylist is found, and the CI audit ----

    [Fact]
    public void Without_the_variable_every_worktree_finds_the_denylist_beside_the_main_checkout()
    {
        var layout = Directory.CreateDirectory(Path.Combine(_dir.FullName, "layout")).FullName;
        Directory.CreateDirectory(Path.Combine(layout, "Dictum private"));
        File.WriteAllText(Path.Combine(layout, "Dictum private", "denylist.txt"), Word + "\n");
        var main = NewRepo(Path.Combine(layout, "main"));
        Ok(Git(main, ["add", ".githooks"]));
        Assert.Equal((0, ""), Commit(main, "install the hooks"));
        var lane = Path.Combine(main, ".worktrees", "lane");
        Ok(Git(main, ["worktree", "add", "-q", "-b", "lane", lane]));
        var unset = new Dictionary<string, string?> { ["DICTUM_PRIVATE_DENYLIST"] = null };

        foreach (var repo in new[] { main, lane })
        {
            Write(repo, "leak.md", $"about {Word}\n");
            Stage(repo, "leak.md");
            var (exit, output) = Commit(repo, "leak", env: unset);
            Assert.True(exit == 1 && Lists(output, "leak.md"), $"{repo}: {output}");
        }
    }

    [Fact]
    public void The_CI_audit_lists_tracked_recordings_private_folders_and_large_files_and_passes_a_clean_tree()
    {
        var repo = NewRepoWithRemote();
        string[] audit = ["-c", "alias.audit=!. .githooks/private-guard.sh; pg_audit_tracked_files", "audit"]; // run by git's own sh
        Assert.Equal((0, ""), Git(repo, audit));

        Write(repo, "call.M4A");
        Write(repo, "Private/a.txt");
        WriteBytes(repo, "big.dat", 6 * 1024 * 1024);
        Write(repo, "tests/fixtures/tone.wav");
        Stage(repo, "call.M4A", "Private/a.txt", "big.dat", "tests/fixtures/tone.wav");
        Assert.Equal(0, Commit(repo, "oops", noVerify: true).Exit);

        var (exit, output) = Git(repo, audit);

        Assert.Equal(1, exit);
        Assert.True(Lists(output, "call.M4A") && Lists(output, "Private/a.txt") && Lists(output, "big.dat"), output);
        Assert.False(Lists(output, "tests/fixtures/tone.wav") || Lists(output, "readme.txt"), output);
    }

    // ---- the committed files themselves ----

    [Fact]
    public void The_hooks_are_committed_executable_with_LF_endings()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var (exit, modes) = Git(root, ["ls-files", "-s", ".githooks"]);
        Assert.SkipWhen(exit != 0, "Not a git checkout.");
        Assert.Matches(@"(?m)^100755 \S+ 0\t\.githooks/pre-commit\r?$", modes);
        Assert.Matches(@"(?m)^100755 \S+ 0\t\.githooks/pre-push\r?$", modes);
        var eol = Git(root, ["ls-files", "--eol", ".githooks"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, eol.Length);
        Assert.All(eol, line => Assert.StartsWith("i/lf", line));
    }

    [Fact]
    public void The_media_list_the_folders_and_the_fixture_exception_agree_across_gitignore_the_hooks_the_CI_job_and_SECURITY()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var lib = File.ReadAllText(Path.Combine(root, ".githooks", "private-guard.sh"));
        var hookExtensions = Regex.Match(lib, "PG_MEDIA_EXTS='([^']+)'").Groups[1].Value.Split('|').Order().ToArray();
        var block = File.ReadAllLines(Path.Combine(root, ".gitignore"))
            .SkipWhile(line => !line.StartsWith("# Private material never leaves this PC")).Skip(1).TakeWhile(line => line.Length > 0).ToArray();
        var ignoredExtensions = block.Where(line => Regex.IsMatch(line, @"^\*\.\w+$")).Select(line => line[2..]).Order().ToArray();

        Assert.NotEmpty(hookExtensions);
        Assert.Equal(ignoredExtensions, hookExtensions);
        Assert.Contains("!tests/fixtures/*.wav", block);
        Assert.Contains("tests/fixtures/[^/]+[.]wav", lib);
        Assert.All(new[] { "private", "recordings", "transcripts" }, folder =>
        {
            Assert.Contains($"{folder}/", block);
            Assert.Contains(folder, lib);
        });

        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n"); // a Windows checkout may use CRLF
        Assert.Contains("\n  private-material:\n", ci);
        Assert.Contains(". .githooks/private-guard.sh", ci);
        Assert.Contains("pg_audit_tracked_files", ci);
        Assert.Contains("## Private material", File.ReadAllText(Path.Combine(root, ".github", "SECURITY.md")));
    }
}
