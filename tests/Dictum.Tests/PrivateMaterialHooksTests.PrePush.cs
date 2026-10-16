using System.Text.RegularExpressions;

namespace Dictum.Tests;

/// <summary>
/// pre-push, continued from PrivateMaterialHooksTests.cs: what a merge adds on its own, refs that are not commits, the
/// edges of "what the remote lacks" (judged by what the destination says it holds, as it says so when the push is made),
/// and what a push carries besides commits. Same scratch repositories, same invented words.
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

    // ---- what the destination already holds ----

    [Fact]
    public void Pre_push_does_not_judge_again_what_a_rebase_or_an_update_merge_brings_into_a_branch_the_remote_already_has()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "published.md", $"about {Word}\n");
        Write(repo, "published.m4a");
        Stage(repo, "published.md", "published.m4a");
        Assert.Equal(0, Commit(repo, "published past the hook", noVerify: true).Exit);
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "main"])); // origin/main holds a recording and a denylist line already
        string[] branches = ["merged", "rebased"];
        foreach (var branch in branches) // each starts before that commit, and the remote has each at its tip
        {
            Ok(Git(repo, ["checkout", "-q", "-b", branch, "main~1"]));
            Write(repo, $"{branch}.txt");
            Stage(repo, $"{branch}.txt");
            Assert.Equal((0, ""), Commit(repo, $"{branch} work"));
            Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", branch]));
        }

        Ok(Git(repo, ["checkout", "-q", "merged"]));
        Ok(Git(repo, ["merge", "-q", "--no-edit", "origin/main"])); // an update merge brings in the published commit
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "merged"]));
        Ok(Git(repo, ["checkout", "-q", "rebased"]));
        Ok(Git(repo, ["rebase", "-q", "origin/main"])); // a rebase puts the same commit under the branch's own
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "+rebased"])); // a rewritten branch needs the +

        foreach (var branch in branches) // what is new on top is still judged, and only that
        {
            Ok(Git(repo, ["checkout", "-q", branch]));
            Write(repo, $"on-top-{branch}.m4a");
            Stage(repo, $"on-top-{branch}.m4a");
            Assert.Equal(0, Commit(repo, "on top", noVerify: true).Exit);
            var (exit, output) = Git(repo, ["push", "origin", branch]);
            Assert.NotEqual(0, exit);
            Assert.True(Lists(output, $"on-top-{branch}.m4a"), output);
            Assert.False(Lists(output, "published.m4a") || Lists(output, "published.md"), output);
        }
    }

    [Fact]
    public void Pre_push_is_not_fooled_by_a_remote_tracking_branch_for_a_branch_the_remote_no_longer_has()
    {
        var repo = NewRepoWithRemote();
        Ok(Git(repo, ["checkout", "-q", "-b", "stale"]));
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "stale"])); // the remote holds it, and origin/stale says so
        Ok(Git(_dir.FullName, ["--git-dir=origin.git", "update-ref", "-d", "refs/heads/stale"])); // then the branch is deleted on the remote, from elsewhere
        Ok(Git(repo, ["rev-parse", "--verify", "-q", "refs/remotes/origin/stale"])); // this clone still has the tracking branch
        Ok(Git(repo, ["branch", "again", "stale"]));

        var (exit, output) = Git(repo, ["push", "origin", "again"]); // the remote holds none of it now, so it would be uploaded again
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
    }

    [Fact]
    public void Pre_push_judges_a_push_by_what_the_push_url_holds_when_it_differs_from_the_fetch_url()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "main"])); // origin, at its fetch URL, holds it, and so does origin/main
        Ok(Git(_dir.FullName, ["init", "-q", "--bare", "elsewhere.git"]));
        var elsewhere = Slashes(Path.Combine(_dir.FullName, "elsewhere.git"));
        Ok(Git(repo, ["config", "remote.origin.pushurl", elsewhere]));
        Write(repo, "next.txt");
        Stage(repo, "next.txt");
        Assert.Equal((0, ""), Commit(repo, "next"));

        var (exit, output) = Git(repo, ["push", "origin", "main"]); // this goes to elsewhere.git, which holds none of it
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
        Assert.Equal("", Git(repo, ["ls-remote", elsewhere]).Output); // and nothing got there
    }

    [Fact]
    public void Pre_push_copes_with_a_remote_that_holds_a_tag_on_a_tree_or_a_file()
    {
        var repo = NewRepoWithRemote();
        var tree = Git(repo, ["rev-parse", "HEAD^{tree}"]).Output.Trim();
        Write(repo, "words.txt");
        var blob = Git(repo, ["hash-object", "-w", "words.txt"]).Output.Trim();
        Ok(Git(repo, ["tag", "on-tree", tree]));
        Ok(Git(repo, ["tag", "on-blob", blob]));
        Ok(Git(repo, ["tag", "-a", "-m", "notes", "annotated-tree", tree]));
        Ok(Git(repo, ["tag", "-a", "-m", "notes", "annotated-commit"]));
        Ok(Git(repo, ["push", "-q", "--no-verify", "origin", "on-tree", "on-blob", "annotated-tree", "annotated-commit"]));
        Ok(Git(repo, ["checkout", "-q", "-b", "feature"]));
        Write(repo, "feature.txt");
        Stage(repo, "feature.txt");
        Assert.Equal((0, ""), Commit(repo, "feature work"));

        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "feature"])); // the listing of what the remote holds is not upset by them
    }

    [Fact]
    public void Pre_push_copes_with_a_remote_that_holds_commits_this_clone_has_never_fetched()
    {
        var repo = NewRepoWithRemote();
        var head = Git(repo, ["rev-parse", "HEAD"]).Output.Trim();
        var tree = Git(repo, ["rev-parse", "HEAD^{tree}"]).Output.Trim();
        var theirs = Git(_dir.FullName, ["-c", "user.name=Someone Else", "-c", "user.email=else@example.invalid", "--git-dir=origin.git", "commit-tree", tree, "-p", head, "-m", "from another clone"]).Output.Trim();
        Ok(Git(_dir.FullName, ["--git-dir=origin.git", "update-ref", "refs/heads/theirs", theirs]));
        Assert.NotEqual(0, Git(repo, ["cat-file", "-e", theirs]).Exit); // the premise: this clone does not have it

        Ok(Git(repo, ["checkout", "-q", "-b", "feature"]));
        Write(repo, "feature.txt");
        Stage(repo, "feature.txt");
        Assert.Equal((0, ""), Commit(repo, "feature work"));
        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "feature"])); // a commit it cannot read is left out of the list, not a reason to stop

        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        var (exit, output) = Git(repo, ["push", "origin", "feature:dirty"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
    }

    [Fact]
    public void Pre_push_refuses_when_the_destination_cannot_say_what_it_holds()
    {
        var repo = NewRepoWithRemote();
        var head = Git(repo, ["rev-parse", "HEAD"]).Output.Trim();
        string[] Hook(string destination) => ["-c", $"alias.pushhook=!echo 'refs/heads/main {head} refs/heads/main 0000000000000000000000000000000000000000' | sh .githooks/pre-push origin {destination}", "pushhook"]; // run by the sh that git itself uses, the way git runs a hook
        Assert.Equal((0, ""), Git(repo, Hook(Slashes(Path.Combine(_dir.FullName, "origin.git"))))); // the control: a destination that answers

        var missing = Slashes(Path.Combine(_dir.FullName, "no-such-remote.git"));
        var (exit, output) = Git(repo, Hook(missing)); // a real push would stop before the hook, so the hook is run by hand
        Assert.Equal(1, exit);
        Assert.Contains("cannot list what the push destination holds", output);

        string[] delete = ["-c", $"alias.pushdelete=!echo '(delete) 0000000000000000000000000000000000000000 refs/heads/gone {head}' | sh .githooks/pre-push origin {missing}", "pushdelete"];
        Assert.Equal((0, ""), Git(repo, delete)); // a push that only deletes sends nothing, so it never asks: taking something down must not wait on a listing
    }

    // ---- what a push carries besides commits ----

    [Fact]
    public void Pre_push_reads_the_message_of_an_annotated_tag_and_lets_a_clean_one_through()
    {
        var repo = NewRepoWithRemote();
        Ok(Git(repo, ["tag", "-a", "-m", $"notes about {Word}", "v-leaky"]));
        Ok(Git(repo, ["tag", "-a", "-m", "release notes\n\nnothing private here", "v-clean"]));
        Ok(Git(repo, ["tag", "v-light"]));
        var before = Git(repo, ["ls-remote", "origin"]).Output;

        var (exit, output) = Git(repo, ["push", "origin", "v-leaky"]);
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "tag message (v-leaky)"), output); // names the tag, never the text
        Assert.DoesNotContain(Word, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Git(repo, ["ls-remote", "origin"]).Output);

        Assert.Equal((0, ""), Git(repo, ["push", "-q", "origin", "v-clean", "v-light"])); // a clean annotated tag, and a lightweight one
    }

    [Fact]
    public void Pre_push_is_not_shown_a_clean_commit_by_a_replace_ref_where_the_push_sends_another()
    {
        var repo = NewRepoWithRemote();
        Write(repo, "sneaky.m4a");
        Stage(repo, "sneaky.m4a");
        Assert.Equal(0, Commit(repo, "sneaky", noVerify: true).Exit);
        var dirty = Git(repo, ["rev-parse", "HEAD"]).Output.Trim();
        var clean = Git(repo, ["commit-tree", "HEAD~1^{tree}", "-p", "HEAD~1", "-m", "clean"]).Output.Trim(); // a commit that changes nothing
        Ok(Git(repo, ["replace", dirty, clean]));
        Assert.DoesNotContain("sneaky.m4a", Git(repo, ["log", "--raw", "--format=", "main"]).Output); // the premise: git log now shows the clean one

        var (exit, output) = Git(repo, ["push", "origin", "main"]); // the pack still carries the real commit
        Assert.NotEqual(0, exit);
        Assert.True(Lists(output, "sneaky.m4a"), output);
    }
}
