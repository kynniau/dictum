using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// The guard rails that keep recordings, transcripts and personal test words out of this public repository:
/// .githooks/pre-commit, .githooks/commit-msg, .githooks/pre-push and the shared .githooks/private-guard.sh. Each test
/// builds a throwaway repository that carries the real hooks and drives them through git itself, so a change to a rule
/// fails here. Invented words only. The real denylist is never read: the denylist always lives inside the scratch folder.
/// </summary>
[Collection("Process-wide state")] // starts many git and shell processes: never beside the timing-based download tests
public sealed partial class PrivateMaterialHooksTests : IDisposable
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

    /// <summary>A bare repository called <paramref name="name"/>, next to the work folders and registered as a remote of <paramref name="repo"/>.</summary>
    private void AddRemote(string repo, string name)
    {
        Ok(Git(_dir.FullName, ["init", "-q", "--bare", $"{name}.git"]));
        File.AppendAllText(Path.Combine(repo, ".git", "config"),
            $"[remote \"{name}\"]\n\turl = {Path.Combine(_dir.FullName, $"{name}.git").Replace('\\', '/')}\n\tfetch = +refs/heads/*:refs/remotes/{name}/*\n");
    }

    /// <summary>A repository with a bare remote called origin, and one clean commit already pushed.</summary>
    private string NewRepoWithRemote()
    {
        var repo = NewRepo();
        AddRemote(repo, "origin");
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

    private static Dictionary<string, string?> NoVariable() => new() { ["DICTUM_PRIVATE_DENYLIST"] = null };

    /// <summary>A path with forward slashes, which git config and git remotes accept on every system.</summary>
    private static string Slashes(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Commits through git's editor flow (git commit -v): a stand-in editor puts <paramref name="message"/> above the template git wrote.</summary>
    private (int Exit, string Output) CommitWithEditor(string repo, string message, string? cleanup = null, bool noVerify = false, string? cleanupFlag = null)
    {
        var editor = Path.Combine(_dir.FullName, "editor.sh");
        File.WriteAllText(editor, "#!/bin/sh\n{ printf '%s\\n' \"$EDITOR_MESSAGE\"; cat \"$1\"; } > \"$1.new\" && mv \"$1.new\" \"$1\"\n");
        List<string> args = cleanup is null ? [] : ["-c", $"commit.cleanup={cleanup}"]; // the config key is visible to a hook; the --cleanup flag is not
        args.AddRange(["commit", "-q", "-v"]);
        if (noVerify) args.Add("--no-verify");
        if (cleanupFlag is not null) args.Add($"--cleanup={cleanupFlag}");
        return Git(repo, [.. args], new() { ["GIT_EDITOR"] = $"sh \"{editor.Replace('\\', '/')}\"", ["EDITOR_MESSAGE"] = message });
    }

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
            "memo.m4a", "RECORDING.M4A", "Aufnahme für María.mp3", "a/b/clip.WebM", "tone.wav", "tests/fixtures/tone.wav", "tests/fixtures/sub/tone.wav",
            "private/a.txt", "docs/Recordings/a.txt", "x/y/TRANSCRIPTS/z/a.txt", "recording/a.txt", "docs/Transcript/b.txt",
        ];
        string[] allowed =
        [
            "notes.txt", "my-private-notes.txt", "src/Private.cs", "Privateer/a.txt", "recordings.txt", "transcript.txt", "recorded/a.txt",
            "notes.pdf.txt", "archive.zipx", "docs/pdf/readme.txt",
        ];
        foreach (var path in refused.Concat(allowed)) Write(repo, path);
        Stage(repo, [.. refused, .. allowed]);

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.Contains("commit refused", output);
        Assert.All(refused, path => Assert.True(Lists(output, path), $"{path} is not named in: {output}"));
        Assert.All(allowed, path => Assert.False(Lists(output, path), $"{path} should not be named in: {output}"));
    }

    [Fact]
    public void A_file_over_5_MiB_is_refused_and_one_of_exactly_5_MiB_is_not()
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
        Assert.Contains("over 5 MiB", output); // the label says the unit the limit is in
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
    public void A_denylist_word_in_a_file_name_is_refused_and_a_file_git_calls_binary_is_read_as_text()
    {
        var repo = NewRepo();
        File.WriteAllBytes(Path.Combine(repo, $"{Word}.dat"), [0, 1, 2]);
        File.WriteAllBytes(Path.Combine(repo, "data.dat"), [0, .. Encoding.ASCII.GetBytes(Word), 0]);
        File.WriteAllBytes(Path.Combine(repo, $"empty-{Word}.dat"), []); // an empty file has no +++ line in the patch, so only its name can give it away
        Stage(repo, $"{Word}.dat", "data.dat", $"empty-{Word}.dat");

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.True(Lists(output, $"{Word}.dat") && Lists(output, $"empty-{Word}.dat"), output);
        Assert.True(Lists(output, "data.dat"), output); // a NUL byte makes git call it binary, and the guard reads it as text all the same
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

        var (exit, output) = Commit(repo, "about " + Word, env: NoVariable()); // neither the variable nor the config key
        Assert.Equal(0, exit);
        var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)); // pre-commit says so; commit-msg stays quiet
        Assert.Contains("no denylist is set", line);
    }

    [Fact]
    public void The_denylist_may_be_named_by_the_shared_config_key_and_the_variable_wins()
    {
        var main = NewRepo();
        Ok(Git(main, ["config", "dictum.privateDenylist", _denylist.Replace('\\', '/')]));
        Ok(Git(main, ["add", ".githooks"]));
        Assert.Equal((0, ""), Commit(main, "install the hooks", env: NoVariable()));
        var lane = Path.Combine(main, ".worktrees", "lane");
        Ok(Git(main, ["worktree", "add", "-q", "-b", "lane", lane]));

        foreach (var repo in new[] { main, lane }) // the key is in the shared config, so a linked worktree sees it too
        {
            Write(repo, "leak.md", $"about {Word}\n");
            Stage(repo, "leak.md");
            var (exit, output) = Commit(repo, "leak", env: NoVariable());
            Assert.True(exit == 1 && Lists(output, "leak.md"), $"{repo}: {output}");
            Ok(Git(repo, ["reset", "-q"]));
            File.Delete(Path.Combine(repo, "leak.md"));
        }

        var other = Path.Combine(_dir.FullName, "other-denylist.txt");
        File.WriteAllText(other, "some-other-word-3\n");
        Write(main, "leak.md", $"about {Word}\n");
        Stage(main, "leak.md");
        Assert.Equal((0, ""), Commit(main, "leak", env: Denylist(other))); // the variable wins: the key's list, which holds the word, is not consulted
        Write(main, "other.md", "about some-other-word-3\n");
        Stage(main, "other.md");
        var inForce = Commit(main, "other", env: Denylist(other)); // and the variable's list is the one in force
        Assert.True(inForce.Exit == 1 && Lists(inForce.Output, "other.md"), inForce.Output);
    }

    [Fact]
    public void A_line_already_in_the_repository_never_blocks_an_edit_to_its_file()
    {
        var repo = NewRepo();
        Write(repo, "old.md", $"an old line about {Word}\n");
        Stage(repo, "old.md");
        Assert.Equal(0, Commit(repo, "old", env: NoVariable()).Exit); // committed while no denylist was set

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

    // ---- commit-msg ----

    [Fact]
    public void A_commit_message_with_a_denylist_word_is_refused_without_printing_it()
    {
        var repo = NewRepo();
        Write(repo, "fine.md");
        Stage(repo, "fine.md");

        var (exit, output) = Commit(repo, "call about ZebraFish-7 on Monday");
        Assert.Equal(1, exit);
        Assert.Contains("commit refused", output);
        Assert.True(Lists(output, "commit message"), output);
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0, Git(repo, ["rev-parse", "--verify", "-q", "HEAD"]).Exit); // nothing was committed

        (exit, output) = Commit(repo, "# QUOKKA MEETING 42"); // with -m, a line that starts with # is part of the message
        Assert.Equal(1, exit);
        Assert.True(Lists(output, "commit message"), output);
        Assert.DoesNotContain("quokka", output, StringComparison.OrdinalIgnoreCase);

        Assert.Equal((0, ""), Commit(repo, "an ordinary message"));
    }

    [Fact]
    public void In_the_editor_flow_the_template_comments_and_the_diff_under_the_scissors_line_are_not_the_message()
    {
        var repo = NewRepo();
        Write(repo, "old.md", $"keep\nan old line about {Word}\n");
        Stage(repo, "old.md");
        Assert.Equal(0, Commit(repo, "old", env: NoVariable()).Exit); // committed while no denylist was set
        Write(repo, "old.md", "keep\n"); // this commit removes that line, so the diff git shows under the scissors line holds the word
        Stage(repo, "old.md");
        Write(repo, $"untracked-{Word}.txt"); // and git's template lists this untracked file by name

        Assert.Equal((0, ""), CommitWithEditor(repo, "tidy up"));

        Write(repo, "next.md");
        Stage(repo, "next.md");
        var (exit, output) = CommitWithEditor(repo, $"mention {Word} here"); // a word the editor really typed into the message
        Assert.Equal(1, exit);
        Assert.True(Lists(output, "commit message"), output);
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
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
    public void Pre_push_catches_a_message_whose_commit_skipped_the_commit_msg_hook()
    {
        var repo = NewRepoWithRemote();
        var before = Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output;
        Write(repo, "fine.md");
        Stage(repo, "fine.md");
        Assert.Equal(0, Commit(repo, "call about ZebraFish-7 on Monday", noVerify: true).Exit);
        var bad = Git(repo, ["rev-parse", "--short=7", "HEAD"]).Output.Trim();
        Write(repo, "fine2.md");
        Stage(repo, "fine2.md");
        Assert.Equal((0, ""), Commit(repo, "a clean message after it"));
        var clean = Git(repo, ["rev-parse", "--short=7", "HEAD"]).Output.Trim();

        var (exit, output) = Git(repo, ["push", "origin", "main"]);

        Assert.NotEqual(0, exit);
        Assert.Contains("push refused", output);
        Assert.True(Lists(output, $"commit message ({bad})"), output); // names the commit, never the text
        Assert.False(Lists(output, $"commit message ({clean})"), output);
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output);
    }

    [Fact]
    public void Pre_push_refuses_a_branch_or_tag_name_holding_a_denylist_word_but_allows_deleting_one()
    {
        var repo = NewRepoWithRemote();
        Ok(Git(repo, ["branch", $"topic-{Word}"]));
        Ok(Git(repo, ["tag", $"v-{Word}"]));

        var (exit, output) = Git(repo, ["push", "origin", $"topic-{Word}"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, $"branch name (topic-{Word})"), output);

        (exit, output) = Git(repo, ["push", "origin", $"v-{Word}"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, $"tag name (v-{Word})"), output);

        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", $"topic-{Word}:renamed"])); // only the name on the remote is published, so the local name is not judged
        (exit, output) = Git(repo, ["push", "origin", $"main:other-{Word}"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, $"branch name (other-{Word})"), output);

        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", $"topic-{Word}"])); // it got to the remote past the hook ...
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "--delete", $"topic-{Word}"])); // ... and taking it down is always allowed
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

    // ---- the CI audit ----

    [Fact]
    public void The_CI_audit_lists_tracked_recordings_private_folders_and_large_files_and_passes_a_clean_tree()
    {
        var repo = NewRepoWithRemote();
        string[] audit = ["-c", "alias.audit=!. .githooks/private-guard.sh; pg_audit_tracked_files", "audit"]; // run by git's own sh
        Assert.Equal((0, ""), Git(repo, audit));

        Write(repo, "call.M4A");
        Write(repo, "Private/a.txt");
        WriteBytes(repo, "big.dat", 6 * 1024 * 1024);
        Write(repo, "tests/fixtures/tone.wav"); // no exception for test audio: it is made at test time
        Write(repo, "docs/Minutes.PDF");
        Stage(repo, "call.M4A", "Private/a.txt", "big.dat", "tests/fixtures/tone.wav", "docs/Minutes.PDF");
        Assert.Equal(0, Commit(repo, "oops", noVerify: true).Exit);

        var (exit, output) = Git(repo, audit);

        Assert.Equal(1, exit);
        Assert.True(Lists(output, "call.M4A") && Lists(output, "Private/a.txt") && Lists(output, "big.dat") && Lists(output, "tests/fixtures/tone.wav") && Lists(output, "docs/Minutes.PDF"), output);
        Assert.False(Lists(output, "readme.txt"), output);
    }

    // ---- the committed files themselves ----

    [Fact]
    public void The_hooks_are_committed_executable_with_LF_endings()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var (exit, modes) = Git(root, ["ls-files", "-s", ".githooks"]);
        Assert.SkipWhen(exit != 0, "Not a git checkout.");
        Assert.Matches(@"(?m)^100755 \S+ 0\t\.githooks/pre-commit\r?$", modes);
        Assert.Matches(@"(?m)^100755 \S+ 0\t\.githooks/commit-msg\r?$", modes);
        Assert.Matches(@"(?m)^100755 \S+ 0\t\.githooks/pre-push\r?$", modes);
        var eol = Git(root, ["ls-files", "--eol", ".githooks"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, eol.Length);
        Assert.All(eol, line => Assert.StartsWith("i/lf", line));
    }

    [Fact]
    public void The_media_list_and_the_folders_agree_across_gitignore_the_hooks_the_CI_job_and_SECURITY_with_no_exception()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var lib = File.ReadAllText(Path.Combine(root, ".githooks", "private-guard.sh"));
        var hookExtensions = Regex.Match(lib, "PG_MEDIA_EXTS='([^']+)'").Groups[1].Value.Split('|').Order().ToArray();
        var block = File.ReadAllLines(Path.Combine(root, ".gitignore"))
            .SkipWhile(line => !line.StartsWith("# Private material never leaves this PC")).Skip(1).TakeWhile(line => line.Length > 0).ToArray();
        var ignoredExtensions = block.Where(line => Regex.IsMatch(line, @"^\*\.\w+$")).Select(line => line[2..]).Order().ToArray();

        Assert.NotEmpty(hookExtensions);
        Assert.Equal(ignoredExtensions, hookExtensions);
        Assert.DoesNotContain(block, line => line.StartsWith('!')); // no exception: a .wav is refused wherever it is (test audio is made at test time)
        Assert.DoesNotContain("tests/fixtures", lib);
        Assert.All(new[] { "private", "recording", "recordings", "transcript", "transcripts" }, folder =>
        {
            Assert.Contains($"{folder}/", block);
            Assert.Contains(folder, lib);
        });

        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n"); // a Windows checkout may use CRLF
        Assert.Contains("\n  private-material:\n", ci);
        Assert.Contains(". .githooks/private-guard.sh", ci);
        Assert.Contains("pg_audit_tracked_files", ci);
        var security = File.ReadAllText(Path.Combine(root, ".github", "SECURITY.md"));
        Assert.Contains("## Private material", security);
        Assert.DoesNotContain("tests/fixtures", security);
    }

    [Fact]
    public void No_tracked_file_names_the_folder_that_holds_the_private_word_list_and_the_docs_name_the_variable_and_the_config_key()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var folder = string.Join(' ', "Dictum", "private"); // not spelled out in one piece: this file is committed too
        var (exit, listing) = Git(root, ["ls-files", "-z"]);
        Assert.SkipWhen(exit != 0, "Not a git checkout.");
        var files = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => Path.Combine(root, path)).Where(File.Exists).ToArray();
        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.DoesNotContain(folder, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase));

        foreach (var doc in new[] { "SECURITY.md", "CONTRIBUTING.md" })
        {
            var text = File.ReadAllText(Path.Combine(root, ".github", doc));
            Assert.Contains("DICTUM_PRIVATE_DENYLIST", text);
            Assert.Contains("dictum.privateDenylist", text);
        }
    }
}
