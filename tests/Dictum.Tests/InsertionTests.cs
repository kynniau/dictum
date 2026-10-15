using Dictum.Core;
using Dictum.Insert;
using Dictum.Settings;

namespace Dictum.Tests;

/// <summary>Records every clipboard/input operation into one shared, ordered log.</summary>
internal sealed class FakeDesktop : IClipboard, IInputSender
{
    public List<string> Ops { get; } = [];
    public string? ClipboardText { get; private set; } = "previous";
    public bool FailSnapshot { get; set; }

    public ClipboardSnapshot Snapshot()
    {
        Ops.Add("snapshot");
        if (FailSnapshot) throw new System.Runtime.InteropServices.ExternalException("OpenClipboard failed", unchecked((int)0x800401D0));
        return new ClipboardSnapshot(new Dictionary<string, object> { ["UnicodeText"] = ClipboardText! });
    }

    public bool? LastKeepOutOfHistory { get; private set; }

    public void SetText(string text, bool keepOutOfHistory)
    {
        Ops.Add($"set-text:{text}");
        LastKeepOutOfHistory = keepOutOfHistory;
        ClipboardText = text;
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        Ops.Add("restore");
        ClipboardText = (string)snapshot.Formats["UnicodeText"];
    }

    public void ReleaseModifiers() => Ops.Add("release-modifiers");

    public void SendCtrlV() => Ops.Add("ctrl-v");

    public Task TypeTextAsync(string text)
    {
        Ops.Add($"type:{text}");
        return Task.CompletedTask;
    }
}

public class InsertionTests
{
    private static (TextInserter, FakeDesktop) Make()
    {
        var desktop = new FakeDesktop();
        var inserter = new TextInserter(desktop, desktop, NullLog.Instance, ms =>
        {
            desktop.Ops.Add($"delay:{ms}");
            return Task.CompletedTask;
        });
        return (inserter, desktop);
    }

    [Fact]
    public async Task Paste_without_restore_leaves_the_transcript_on_the_clipboard()
    {
        var (inserter, desktop) = Make();
        await inserter.InsertAsync("Hello there.", new InsertSettings { Method = "paste", RestoreClipboard = false }, CancellationToken.None);
        Assert.Equal(["release-modifiers", "set-text:Hello there. ", "ctrl-v", "delay:150"], desktop.Ops);
        Assert.Equal("Hello there. ", desktop.ClipboardText);
    }

    [Fact]
    public async Task Paste_with_restore_snapshots_first_and_restores_after_the_delay()
    {
        var (inserter, desktop) = Make();
        await inserter.InsertAsync("Hello there.", new InsertSettings { Method = "paste", RestoreClipboard = true, PasteDelayMs = 200 }, CancellationToken.None);
        Assert.Equal(["snapshot", "release-modifiers", "set-text:Hello there. ", "ctrl-v", "delay:200", "restore"], desktop.Ops);
        Assert.Equal("previous", desktop.ClipboardText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Type_method_never_touches_the_clipboard(bool restore)
    {
        var (inserter, desktop) = Make();
        await inserter.InsertAsync("Line one\nline two", new InsertSettings { Method = "type", RestoreClipboard = restore }, CancellationToken.None);
        Assert.Equal(["release-modifiers", "type:Line one\nline two "], desktop.Ops);
        Assert.Equal("previous", desktop.ClipboardText);
    }

    [Fact]
    public async Task Trailing_space_can_be_turned_off()
    {
        var (inserter, desktop) = Make();
        await inserter.InsertAsync("Hi", new InsertSettings { TrailingSpace = false }, CancellationToken.None);
        Assert.Contains("set-text:Hi", desktop.Ops);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Transcripts_are_kept_out_of_clipboard_history_unless_allowed(bool clipboardHistory, bool expectedKeepOut)
    {
        var (inserter, desktop) = Make();
        await inserter.InsertAsync("Hi", new InsertSettings { ClipboardHistory = clipboardHistory }, CancellationToken.None);
        Assert.Equal(expectedKeepOut, desktop.LastKeepOutOfHistory);
    }

    [Fact]
    public async Task Snapshot_failure_still_pastes_the_transcript_without_restoring()
    {
        var (inserter, desktop) = Make();
        desktop.FailSnapshot = true;
        await inserter.InsertAsync("Hello there.", new InsertSettings { RestoreClipboard = true }, CancellationToken.None);
        Assert.Equal(["snapshot", "release-modifiers", "set-text:Hello there. ", "ctrl-v", "delay:150"], desktop.Ops);
        Assert.Equal("Hello there. ", desktop.ClipboardText);
    }

    [Fact]
    public async Task Cancelled_session_sends_no_keystrokes()
    {
        var (inserter, desktop) = Make();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            inserter.InsertAsync("Hello", new InsertSettings(), new CancellationToken(canceled: true)));
        Assert.DoesNotContain("ctrl-v", desktop.Ops);
        Assert.DoesNotContain(desktop.Ops, op => op.StartsWith("set-text", StringComparison.Ordinal));
    }
}
