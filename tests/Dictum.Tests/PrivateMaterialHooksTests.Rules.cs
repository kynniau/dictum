using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// Where the denylist comes from and what text the checks read, continued from PrivateMaterialHooksTests.cs. Same
/// scratch repositories, same invented words.
/// </summary>
public sealed partial class PrivateMaterialHooksTests
{
    private const string MovedTail = "moved/denylist.txt";

    /// <summary>A path where a denylist used to be: never created.</summary>
    private string MovedList() => Path.Combine(_dir.FullName, "moved", "denylist.txt");

    /// <summary>True when the hook output names the moved list (the path may be printed in either slash style).</summary>
    private static bool NamesMovedList(string output) => Slashes(output).Contains(MovedTail, StringComparison.OrdinalIgnoreCase);

    // ---- where the denylist comes from ----

    [Fact]
    public void A_denylist_named_by_the_variable_but_missing_refuses_the_commit_the_merge_and_the_push()
    {
        var repo = NewRepoWithRemote();
        var moved = Denylist(MovedList());
        Write(repo, "fine.md");
        Stage(repo, "fine.md");

        var (exit, output) = Commit(repo, "fine", env: moved); // pre-commit
        Assert.Equal(1, exit);
        Assert.True(NamesMovedList(output), output);
        Assert.Contains("DICTUM_PRIVATE_DENYLIST=none", output); // and says how to skip the check on purpose
        Assert.Equal("1", Git(repo, ["rev-list", "--count", "HEAD"]).Output.Trim()); // nothing was committed

        (exit, output) = Commit(repo, "a folder is not a list", env: Denylist(_dir.FullName));
        Assert.Equal(1, exit);
        Assert.Contains("cannot read the denylist", output);

        Assert.Equal(0, Commit(repo, "fine", noVerify: true).Exit);
        var before = Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output;
        (exit, output) = Git(repo, ["push", "origin", "main"], moved); // pre-push
        Assert.NotEqual(0, exit);
        Assert.True(NamesMovedList(output), output);
        Assert.Equal(before, Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output);

        Ok(Git(repo, ["checkout", "-q", "-b", "side", "HEAD~1"]));
        Write(repo, "side.txt");
        Stage(repo, "side.txt");
        Assert.Equal((0, ""), Commit(repo, "side work"));
        Ok(Git(repo, ["checkout", "-q", "main"]));
        (exit, output) = Git(repo, ["merge", "--no-ff", "--no-edit", "side"], moved); // a clean merge runs commit-msg alone
        Assert.NotEqual(0, exit);
        Assert.True(NamesMovedList(output), output);
        Ok(Git(repo, ["merge", "--abort"]));
    }

    [Fact]
    public void A_denylist_named_by_the_config_key_but_missing_is_refused_including_a_relative_path_a_linked_worktree_lacks()
    {
        var main = NewRepo();
        File.Copy(_denylist, Path.Combine(main, "deny-here.txt")); // untracked, at the top of this worktree only
        Ok(Git(main, ["config", "dictum.privateDenylist", "deny-here.txt"])); // relative: read from the top of whichever worktree git is working in
        Ok(Git(main, ["add", ".githooks"]));
        Assert.Equal((0, ""), Commit(main, "install the hooks", env: NoVariable()));
        var lane = Path.Combine(main, ".worktrees", "lane");
        Ok(Git(main, ["worktree", "add", "-q", "-b", "lane", lane]));

        Write(main, "leak.md", $"about {Word}\n");
        Stage(main, "leak.md");
        var (exit, output) = Commit(main, "leak", env: NoVariable());
        Assert.True(exit == 1 && Lists(output, "leak.md"), output); // here the file is found, and its list is in force
        Ok(Git(main, ["reset", "-q"]));
        File.Delete(Path.Combine(main, "leak.md"));

        Write(lane, "fine.md");
        Stage(lane, "fine.md");
        (exit, output) = Commit(lane, "fine", env: NoVariable());
        Assert.Equal(1, exit); // there it is not: refused, not quietly unchecked
        Assert.Contains("deny-here.txt", output);
        Assert.Contains("does not exist", output);

        Ok(Git(main, ["config", "dictum.privateDenylist", Slashes(MovedList())])); // and an absolute path to a file that has moved
        Write(main, "fine.md");
        Stage(main, "fine.md");
        (exit, output) = Commit(main, "fine", env: NoVariable());
        Assert.Equal(1, exit);
        Assert.True(NamesMovedList(output), output);
    }

    [Fact]
    public void The_config_key_is_read_as_a_path_so_a_tilde_works_and_a_path_git_cannot_expand_is_refused()
    {
        var repo = NewRepo();
        var home = Directory.CreateDirectory(Path.Combine(_dir.FullName, "home")).FullName;
        File.Copy(_denylist, Path.Combine(home, "denylist.txt"));
        Ok(Git(repo, ["config", "dictum.privateDenylist", "~/denylist.txt"]));
        var env = new Dictionary<string, string?> { ["DICTUM_PRIVATE_DENYLIST"] = null, ["HOME"] = home };
        Write(repo, "fine.md");
        Stage(repo, "fine.md");

        Assert.Equal((0, ""), Commit(repo, "fine", env: env)); // the ~ is expanded: the list is found, and the file is clean
        Write(repo, "leak.md", $"about {Word}\n");
        Stage(repo, "leak.md");
        var (exit, output) = Commit(repo, "leak", env: env);
        Assert.True(exit == 1 && Lists(output, "leak.md"), output); // and that list is in force

        Ok(Git(repo, ["config", "dictum.privateDenylist", "~no-such-user-here/denylist.txt"]));
        (exit, output) = Commit(repo, "again", env: env);
        Assert.Equal(1, exit); // git cannot expand it: refused, not skipped
        Assert.Contains("dictum.privateDenylist", output);
    }

    [Fact]
    public void Setting_the_denylist_to_none_skips_the_check_on_purpose_and_says_so()
    {
        var repo = NewRepo();
        Write(repo, "leak.md", $"about {Word}\n");
        Stage(repo, "leak.md");
        var none = Denylist("none");

        var (exit, output) = Commit(repo, "about " + Word, env: none);
        Assert.Equal(0, exit);
        var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)); // pre-commit says so; commit-msg stays quiet
        Assert.Contains("set to none", line);

        Ok(Git(repo, ["config", "dictum.privateDenylist", Slashes(_denylist)]));
        Write(repo, "leak2.md", $"about {Word}\n");
        Stage(repo, "leak2.md");
        Assert.Equal(0, Commit(repo, "variable", env: none).Exit); // the variable wins over a key that names a real list
        Write(repo, "leak3.md", $"about {Word}\n");
        Stage(repo, "leak3.md");
        var (keyExit, keyOutput) = Commit(repo, "key", env: NoVariable());
        Assert.True(keyExit == 1 && Lists(keyOutput, "leak3.md"), keyOutput); // without the variable the key's list applies

        Ok(Git(repo, ["config", "dictum.privateDenylist", "none"]));
        Assert.Equal(0, Commit(repo, "key none", env: NoVariable()).Exit); // and the key may say none too
    }

    // ---- what is read ----

    [Fact]
    public void Files_git_calls_binary_are_read_as_text_and_a_clean_binary_still_commits()
    {
        var repo = NewRepoWithRemote();
        Write(repo, ".gitattributes", "*.md -diff\n"); // git treats these as binary, whatever they hold
        Write(repo, "notes.md", $"about {Word}\n");
        File.WriteAllBytes(Path.Combine(repo, "data.dat"), [0, .. Encoding.ASCII.GetBytes(Word), 0]); // a NUL byte: binary to git
        var random = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(random);
        File.WriteAllBytes(Path.Combine(repo, "random.bin"), random);
        Stage(repo, ".gitattributes", "notes.md", "data.dat", "random.bin");

        var (exit, output) = Commit(repo);
        Assert.Equal(1, exit);
        Assert.True(Lists(output, "notes.md") && Lists(output, "data.dat"), output); // named one by one: both places that read the patch read it as text
        Assert.False(Lists(output, "random.bin") || Lists(output, ".gitattributes"), output);

        Ok(Git(repo, ["reset", "-q", "--", "notes.md", "data.dat"]));
        Assert.Equal((0, ""), Commit(repo, "a clean binary"));

        Write(repo, "again.md", $"and again {Word}\n"); // pre-push reads it the same way
        Stage(repo, "again.md");
        Assert.Equal(0, Commit(repo, "again", noVerify: true).Exit);
        (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "again.md"), output);
    }

    [Fact]
    public void A_hidden_character_cannot_hide_a_denylist_entry()
    {
        var repo = NewRepo();
        var hiding = new Dictionary<string, string> // each holds an entry once the characters that hide it are dealt with
        {
            ["no-break-space.md"] = "call about quokka\u00A0meeting 42\n",
            ["tab.md"] = "call about quokka\tmeeting 42\n",
            ["spaces.md"] = "call about quokka    meeting   42\n",
            ["soft-hyphen.md"] = "about zebra\u00ADfish-7\n",
            ["zero-width-space.md"] = "about zebra\u200Bfish-7\n",
            ["zero-width-non-joiner.md"] = "about zebra\u200Cfish-7\n",
            ["zero-width-joiner.md"] = "about zebra\u200Dfish-7\n",
            ["all-together.md"] = "QUOKKA \u00A0 \t meeting\u200B 42\n",
        };
        string[] clean = ["quokka meeting 4 2", "zebra fish-7", "zebrafish 7", "42 meeting quokka"]; // close, but not the entries
        foreach (var (name, text) in hiding) Write(repo, name, text);
        for (var i = 0; i < clean.Length; i++) Write(repo, $"clean{i}.md", clean[i] + "\n");
        Stage(repo, [.. hiding.Keys, .. Enumerable.Range(0, clean.Length).Select(i => $"clean{i}.md")]);

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.All(hiding.Keys, name => Assert.True(Lists(output, name), $"{name} is not named in: {output}"));
        Assert.All(Enumerable.Range(0, clean.Length), i => Assert.False(Lists(output, $"clean{i}.md"), output)); // nothing is flattened too far: a space is not a hyphen, and word order counts
    }

    [Fact]
    public void Entries_typed_with_odd_spacing_match_and_so_do_messages_names_and_ref_names()
    {
        var repo = NewRepoWithRemote();
        var odd = Path.Combine(_dir.FullName, "odd-denylist.txt");
        File.WriteAllText(odd, "walrus\u00A0\u00A0ledger  88\nheron\u00ADcall-5\n\u00A0\u00A0\n\u200B\n"); // the last two lines are blank once normalised
        Write(repo, "spaced.md", "the walrus ledger 88 file\n");
        Write(repo, "hyphenated.md", "say heroncall-5 now\n");
        Write(repo, "fine.md", "nothing private here\n"); // a blank entry must not become a pattern that matches everything
        Stage(repo, "spaced.md", "hyphenated.md", "fine.md");

        var (exit, output) = Commit(repo, env: Denylist(odd));
        Assert.Equal(1, exit);
        Assert.True(Lists(output, "spaced.md") && Lists(output, "hyphenated.md"), output);
        Assert.False(Lists(output, "fine.md"), output);

        Ok(Git(repo, ["reset", "-q"])); // the same flattening applies to a message, a file name and a branch name
        Write(repo, "next.md");
        Stage(repo, "next.md");
        (exit, output) = Commit(repo, "call about quokka\u00A0meeting 42");
        Assert.True(exit == 1 && Lists(output, "commit message"), output);

        Ok(Git(repo, ["reset", "-q"]));
        Write(repo, "quokka\u00A0meeting 42.md");
        Stage(repo, "quokka\u00A0meeting 42.md");
        (exit, output) = Commit(repo, "a plain message");
        Assert.True(exit == 1 && Lists(output, "quokka\u00A0meeting 42.md"), output);

        Ok(Git(repo, ["reset", "-q"]));
        Ok(Git(repo, ["branch", "topic-zebra\u200Bfish-7"]));
        (exit, output) = Git(repo, ["push", "origin", "topic-zebra\u200Bfish-7"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "branch name (topic-zebra\u200Bfish-7)"), output);

        Write(repo, "pushed.md");
        Stage(repo, "pushed.md");
        Assert.Equal(0, Commit(repo, "call about quokka\u00A0meeting 42", noVerify: true).Exit); // and pre-push reads the stored message the same way
        var id = Git(repo, ["rev-parse", "--short=7", "HEAD"]).Output.Trim();
        (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, $"commit message ({id})"), output);
    }

    // ---- names and messages ----

    private static readonly string[] BlockedTypes =
    [
        "wav", "mp4", "m4a", "mp3", "ogg", "oga", "opus", "flac", "aac", "wma", "webm", "3gp", "amr", "mov", "mkv", "avi", "aiff", "caf",
        "aif", "wmv", "m4v", "mpg", "mpeg", "m4b", "mka", "weba", "spx", "flv", "3g2", "pcm", "dss", "ds2", "srt", "vtt",
        "docx", "doc", "rtf", "odt", "pdf", "zip", "7z", "rar", "msg", "eml",
    ];

    [Fact]
    public void Every_blocked_type_is_refused_and_the_list_is_the_one_pinned_here()
    {
        var root = FixBatchDownloadTests.RepoRoot();
        var lib = File.ReadAllText(Path.Combine(root, ".githooks", "private-guard.sh"));
        var hookTypes = Regex.Match(lib, "PG_MEDIA_EXTS='([^']+)'").Groups[1].Value.Split('|');
        Assert.Equal(BlockedTypes.Order(), hookTypes.Order()); // pinned here as well as against .gitignore, so dropping a type from both cannot pass unnoticed

        var repo = NewRepo();
        var paths = BlockedTypes.Select((type, i) => $"types/sample.{(i % 2 == 0 ? type : type.ToUpperInvariant())}").ToArray();
        foreach (var path in paths) Write(repo, path);
        Stage(repo, paths);

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.All(paths, path => Assert.True(Lists(output, path), $"{path} is not named in: {output}"));
    }

    [Fact]
    public void The_commit_message_is_read_the_way_git_keeps_it_in_each_cleanup_mode()
    {
        var repo = NewRepo();
        string?[] modes = [null, "whitespace", "verbatim", "scissors"];
        foreach (var mode in modes) Write(repo, $"old-{mode ?? "default"}.md", $"keep\nan old line about {Word}\n");
        Stage(repo, [.. modes.Select(mode => $"old-{mode ?? "default"}.md")]);
        Assert.Equal(0, Commit(repo, "old", env: NoVariable()).Exit); // committed while no list was set
        var typed = $"tidy up\n\n# a note about {Word}"; // the author's own # line

        foreach (var mode in modes)
        {
            var name = mode ?? "default";
            Write(repo, $"old-{name}.md", "keep\n"); // removes the old line, so the diff git puts under the scissors line holds the word
            Stage(repo, $"old-{name}.md");
            Assert.Equal((0, ""), CommitWithEditor(repo, "tidy up", mode)); // and that diff is not part of the message in any mode

            Write(repo, $"kept-{name}.md");
            Stage(repo, $"kept-{name}.md");
            var (exit, output) = CommitWithEditor(repo, typed, mode);
            if (mode is null)
            {
                Assert.Equal((0, ""), (exit, output)); // the default drops # lines, so the hook ignores this one too
                Assert.DoesNotContain("# a note", Git(repo, ["log", "-1", "--format=%B"]).Output);
            }
            else
            {
                Assert.True(exit == 1 && Lists(output, "commit message"), $"{mode}: {output}"); // these modes keep the # line, so the hook reads it
                Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, CommitWithEditor(repo, typed, mode, noVerify: true).Exit);
                Assert.Contains("# a note", Git(repo, ["log", "-1", "--format=%B"]).Output); // the premise, read from git: it was committed
            }
        }
    }

    [Fact]
    public void A_cleanup_flag_on_the_command_line_is_invisible_to_commit_msg_and_pre_push_catches_the_message()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "next.md");
        Stage(repo, "next.md");

        Assert.Equal((0, ""), CommitWithEditor(repo, $"tidy up\n\n# a note about {Word}", cleanupFlag: "verbatim")); // git keeps the # line; a hook is not told the flag, so it drops it
        var id = Git(repo, ["rev-parse", "--short=7", "HEAD"]).Output.Trim();
        Assert.Contains("# a note", Git(repo, ["log", "-1", "--format=%B"]).Output);

        var (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, $"commit message ({id})"), output);
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- ways round a plain read of the diff ----

    [Fact]
    public void A_rename_is_judged_by_its_new_name_and_counts_as_added_at_commit_and_at_push()
    {
        var repo = NewRepoWithRemote();
        Directory.CreateDirectory(Path.Combine(repo, "recordings"));
        Write(repo, "notes.txt", "notes\n");
        Write(repo, "clip.txt", "clip\n");
        Write(repo, "old.md", $"an old line about {Word}\n");
        Stage(repo, "notes.txt", "clip.txt", "old.md");
        Assert.Equal(0, Commit(repo, "plain files", env: NoVariable()).Exit); // old.md went in while no denylist was set
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "main"])); // and the remote has all three, so only the renames are new

        Ok(Git(repo, ["mv", "notes.txt", "recordings/notes.txt"]));
        Ok(Git(repo, ["mv", "clip.txt", "clip.m4a"]));
        var (exit, output) = Commit(repo, "move"); // git sees two renames; the guard must see two new names
        Assert.Equal(1, exit);
        Assert.True(Lists(output, "recordings/notes.txt") && Lists(output, "clip.m4a"), output);
        Assert.Equal(0, Commit(repo, "move", noVerify: true).Exit);
        (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "recordings/notes.txt") && Lists(output, "clip.m4a"), output);

        Ok(Git(repo, ["reset", "-q", "--hard", "origin/main"]));
        Ok(Git(repo, ["mv", "old.md", "renamed.md"])); // a renamed file counts as added, so its old line is read again
        (exit, output) = Commit(repo, "rename");
        Assert.True(exit == 1 && Lists(output, "renamed.md"), output);
        Assert.Equal(0, Commit(repo, "rename", noVerify: true).Exit);
        (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "renamed.md"), output);
        Assert.False(Lists(output, "old.md"), output);
    }

    [Fact]
    public void A_text_conversion_filter_cannot_show_the_checks_something_other_than_what_is_committed()
    {
        var repo = NewRepoWithRemote();
        Write(repo, ".gitattributes", "*.md diff=hide\n");
        Ok(Git(repo, ["config", "diff.hide.textconv", "echo clean"])); // git is shown "clean" whatever the file holds
        Write(repo, "notes.md", $"about {Word}\n");
        Stage(repo, ".gitattributes", "notes.md");
        var shown = Git(repo, ["diff", "--cached", "-p"]).Output; // the premise: a plain git diff is fooled
        Assert.Contains("clean", shown);
        Assert.DoesNotContain(Word, shown, StringComparison.OrdinalIgnoreCase);

        var (exit, output) = Commit(repo);
        Assert.True(exit == 1 && Lists(output, "notes.md"), output);

        Assert.Equal(0, Commit(repo, "past the hook", noVerify: true).Exit);
        (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "notes.md"), output);
    }

    [Fact]
    public void UTF_16_text_is_read_as_text_and_a_clean_binary_still_commits()
    {
        var repo = NewRepo();
        var little = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var big = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        File.WriteAllText(Path.Combine(repo, "little.txt"), $"about {Word}\r\n", little);
        File.WriteAllText(Path.Combine(repo, "big.txt"), "call about QUOKKA MEETING 42\n", big);
        File.WriteAllText(Path.Combine(repo, "clean16.txt"), "nothing private here\r\n", little);
        var random = new byte[3 * 1024 * 1024];
        new Random(11).NextBytes(random);
        File.WriteAllBytes(Path.Combine(repo, "random.bin"), random);
        Stage(repo, "little.txt", "big.txt", "clean16.txt", "random.bin");

        var (exit, output) = Commit(repo);

        Assert.Equal(1, exit);
        Assert.True(Lists(output, "little.txt") && Lists(output, "big.txt"), output); // either byte order, with its byte-order mark
        Assert.False(Lists(output, "clean16.txt") || Lists(output, "random.bin"), output);
        Ok(Git(repo, ["reset", "-q", "--", "little.txt", "big.txt"]));
        Assert.Equal((0, ""), Commit(repo, "the clean ones"));
    }

    [Fact]
    public void Text_that_cannot_be_read_fails_the_normaliser_instead_of_passing_as_clean()
    {
        var repo = NewRepo();
        string[] Normalise(string file) => ["-c", $"alias.norm=!. .githooks/private-guard.sh; pg_setup; pg_init_normaliser; pg_normalise {file} > /dev/null; echo \"status $?\"", "norm"]; // run by the sh that git itself uses

        Assert.Contains("status 0", Git(repo, Normalise(".githooks/pre-commit")).Output); // a file that is there
        Assert.Contains("status 2", Git(repo, Normalise("no-such-file.txt")).Output); // a file that is not: the callers read this as a hit, not as clean
    }
}
