using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// pre-push, continued from PrivateMaterialHooksTests.cs: what a merge adds on its own, refs that are not commits, and
/// the edges of "what the remote lacks". Same scratch repositories, same invented words.
/// </summary>
public sealed partial class PrivateMaterialHooksTests
{
    [Fact]
    public void Pre_push_reads_what_a_merge_adds_on_its_own()
    {
        var repo = NewRepoWithRemote();
        Ok(Git(repo, ["checkout", "-q", "-b", "side"]));
        Write(repo, "side.txt");
        Stage(repo, "side.txt");
        Assert.Equal((0, ""), Commit(repo, "side work"));
        Ok(Git(repo, ["checkout", "-q", "main"]));
        Write(repo, "main.txt");
        Stage(repo, "main.txt");
        Assert.Equal((0, ""), Commit(repo, "main work"));
        Ok(Git(repo, ["merge", "-q", "--no-commit", "--no-ff", "side"])); // joins the two without a conflict, then stops
        Write(repo, "call.m4a");
        Write(repo, "leak.md", $"about {Word}\n");
        Stage(repo, "call.m4a", "leak.md"); // two files neither side had: the merge's own change
        Assert.Equal(0, Commit(repo, "merge side", noVerify: true).Exit);
        var before = Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output;

        var (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "call.m4a"), output);
        Assert.False(Lists(output, "side.txt") || Lists(output, "main.txt"), output); // the two sides were clean

        Ok(Git(repo, ["rm", "-q", "--cached", "call.m4a"]));
        Ok(Git(repo, ["commit", "-q", "--amend", "--no-verify", "--no-edit"]));
        (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "leak.md"), output);
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output);

        Ok(Git(repo, ["rm", "-q", "--cached", "leak.md"]));
        Ok(Git(repo, ["commit", "-q", "--amend", "--no-verify", "--no-edit"]));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "main"])); // what is left of the merge is nothing of its own
        Assert.NotEqual(before, Git(repo, ["ls-remote", "origin", "refs/heads/main"]).Output);
    }

    [Fact]
    public void Pre_push_reads_a_conflict_resolution_and_lets_a_clean_one_through()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "shared.txt", "one\ntwo\nthree\n");
        Stage(repo, "shared.txt");
        Assert.Equal((0, ""), Commit(repo, "shared base"));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "main"]));
        Ok(Git(repo, ["checkout", "-q", "-b", "side"]));
        Write(repo, "shared.txt", "one\ntwo from side\nthree\n");
        Stage(repo, "shared.txt");
        Assert.Equal((0, ""), Commit(repo, "side edit"));
        Ok(Git(repo, ["checkout", "-q", "main"]));
        Write(repo, "shared.txt", "one\ntwo from main\nthree\n");
        Stage(repo, "shared.txt");
        Assert.Equal((0, ""), Commit(repo, "main edit"));

        Assert.Equal(1, Git(repo, ["merge", "side"]).Exit); // both sides changed line two
        Write(repo, "shared.txt", $"one\nresolved about {Word}\nthree\n");
        Stage(repo, "shared.txt");
        Assert.Equal(0, Commit(repo, "merge side", noVerify: true).Exit);
        var (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "shared.txt"), output);

        Ok(Git(repo, ["reset", "-q", "--hard", "HEAD~1"]));
        Assert.Equal(1, Git(repo, ["merge", "side"]).Exit);
        Write(repo, "shared.txt", "one\nresolved cleanly\nthree\n");
        Stage(repo, "shared.txt");
        Assert.Equal((0, ""), Commit(repo, "merge side"));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "main"]));
    }

    [Fact]
    public void Pre_push_does_not_judge_again_what_a_clean_update_merge_brings_in()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "published.md", $"about {Word}\n");
        Stage(repo, "published.md");
        Assert.Equal(0, Commit(repo, "published past the hook", noVerify: true).Exit);
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "main"])); // origin/main holds it already
        Ok(Git(repo, ["checkout", "-q", "-b", "feature", "HEAD~1"]));
        Write(repo, "feature.txt");
        Stage(repo, "feature.txt");
        Assert.Equal((0, ""), Commit(repo, "feature work"));
        Ok(Git(repo, ["merge", "-q", "--no-edit", "origin/main"])); // a clean update merge: its own change is empty

        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "feature"]));
    }

    [Fact]
    public void Pre_push_refuses_an_octopus_merge_because_git_cannot_show_what_it_added_on_its_own()
    {
        var repo = NewRepoWithRemote();
        foreach (var branch in new[] { "one", "two", "three" })
        {
            Ok(Git(repo, ["checkout", "-q", "-b", branch, "main"]));
            Write(repo, $"{branch}.txt");
            Stage(repo, $"{branch}.txt");
            Assert.Equal((0, ""), Commit(repo, $"{branch} work"));
        }
        Ok(Git(repo, ["checkout", "-q", "main"]));
        Write(repo, "main.txt");
        Stage(repo, "main.txt");
        Assert.Equal((0, ""), Commit(repo, "main moves on")); // so none of the branches is a fast-forward of main
        Ok(Git(repo, ["merge", "-q", "--no-commit", "one", "two", "three"]));
        Write(repo, "octopus.m4a");
        Stage(repo, "octopus.m4a");
        Assert.Equal(0, Commit(repo, "octopus", noVerify: true).Exit);
        Assert.Equal(4, Git(repo, ["rev-list", "--parents", "-1", "HEAD"]).Output.Trim().Split(' ').Length - 1);
        var merge = Git(repo, ["rev-parse", "--short=7", "HEAD"]).Output.Trim();

        var (exit, output) = Git(repo, ["push", "origin", "main"]);

        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, $"commit {merge}"), output);
        Assert.Contains("octopus merge", output);
        Assert.Single(Regex.Matches(output, @"(?m)^\s+octopus merge\s")); // one row for the merge, and no stray row made from git's own remerge warning
    }

    [Fact]
    public void Pre_push_refuses_a_tag_that_is_not_on_a_commit_and_lets_ordinary_tags_through()
    {
        var repo = NewRepoWithRemote();
        var tree = Git(repo, ["rev-parse", "HEAD^{tree}"]).Output.Trim();
        Write(repo, "words.txt", $"about {Word}\n");
        var blob = Git(repo, ["hash-object", "-w", "words.txt"]).Output.Trim();
        Ok(Git(repo, ["tag", "on-tree", tree]));
        Ok(Git(repo, ["tag", "on-blob", blob]));
        Ok(Git(repo, ["tag", "-a", "-m", "notes", "annotated-tree", tree]));
        Ok(Git(repo, ["tag", "-a", "-m", "notes", "annotated-commit"]));

        foreach (var tag in new[] { "on-tree", "on-blob", "annotated-tree" }) // git log shows nothing for these, so nothing else would have judged them
        {
            var (exit, output) = Git(repo, ["push", "origin", tag]);
            Assert.NotEqual(0, exit);
            Assert.Contains("not a commit", output);
            Assert.True(Lists(output, $"refs/tags/{tag}"), output);
        }

        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "annotated-commit"]));
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "on-tree"])); // it got there past the hook ...
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "--delete", "on-tree"])); // ... and taking it down is always allowed
    }

    [Fact]
    public void Pre_push_judges_every_ref_in_one_push_whether_the_bad_one_comes_first_or_last()
    {
        var repo = NewRepoWithRemote();
        Ok(Git(repo, ["checkout", "-q", "-b", "clean"]));
        Write(repo, "clean.txt");
        Stage(repo, "clean.txt");
        Assert.Equal((0, ""), Commit(repo, "clean work"));
        Ok(Git(repo, ["checkout", "-q", "-b", "dirty", "main"]));
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        var before = Git(repo, ["ls-remote", "origin"]).Output;

        foreach (var order in new[] { new[] { "dirty", "clean" }, new[] { "clean", "dirty" } })
        {
            var (exit, output) = Git(repo, ["push", "origin", .. order]);
            Assert.NotEqual(0, exit);
            Assert.True(Lists(output, "sneaky.m4a"), $"{string.Join(' ', order)}: {output}");
            Assert.Equal(before, Git(repo, ["ls-remote", "origin"]).Output); // not even the clean branch got through
        }
    }

    [Fact]
    public void Pre_push_to_one_remote_judges_what_only_another_remote_holds()
    {
        var repo = NewRepoWithRemote();
        AddRemote(repo, "mirror");
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        Ok(Git(repo, ["push", "-q", "--no-verify", "mirror", "main"])); // the mirror holds it, so refs/remotes/mirror/main does too

        var (exit, output) = Git(repo, ["push", "origin", "main:fresh"]); // a new branch on origin, which does not hold it
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
    }

    [Fact]
    public void Pre_push_reads_a_root_commit_even_when_log_showRoot_is_off()
    {
        var repo = NewRepo();
        AddRemote(repo, "origin"); // empty: the first push sends everything
        Ok(Git(repo, ["config", "log.showRoot", "false"]));
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "the very first commit", noVerify: true).Exit);

        var (exit, output) = Git(repo, ["push", "origin", "main"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
    }
}
