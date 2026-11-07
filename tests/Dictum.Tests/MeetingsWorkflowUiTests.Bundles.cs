using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using Dictum.Core;
using Dictum.Meetings;
using Dictum.Meetings.Store;
using Dictum.Settings;
using Dictum.UI.Shell;
using Dictum.UI.Views.Meetings;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.Tests;

public sealed partial class MeetingsWorkflowUiTests
{
    [Theory]
    [InlineData("spoken", APPROVED_Spoken)]
    [InlineData("written", APPROVED_Written)]
    [InlineData("platform-announced", PROPOSED_Platform)]
    [InlineData("refused", APPROVED_Refused)]
    [InlineData("agreed", "you confirmed the notice again")]
    [InlineData("unconfirmed", "did not confirm that they agreed")]
    [InlineData("discard-requested", "asked to discard")]
    [InlineData("some-future-answer", "does not recognise")]
    public void A_record_line_never_shows_agreement_for_an_answer_that_was_not_an_agreement(string answer, string expected)
    {
        var line = MeetingsWindow.RecordText(AgreementKind.Live, new DateTimeOffset(2026, 11, 7, 3, 2, 0, TimeSpan.Zero), answer);
        Assert.Contains(expected, line);
        var isAgreement = answer is "spoken" or "written" or "platform-announced";
        Assert.Equal(isAgreement, line.Contains("you said everyone agreed"));
        if (answer != "spoken") Assert.DoesNotContain(APPROVED_Spoken, line);
    }

    [Fact]
    public Task Suggested_file_name_carries_no_title_words() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var meta = fixture.Files.Meta() with { Title = "Quarterly salary review with Alex Example" };
        var id = fixture.Store.Create(meta);
        string? suggested = null;
        var window = new MeetingsWindow(fixture.Host with { ChooseBundleDestination = name => { suggested = name; return Task.FromResult<string?>(null); } });
        window.Show(); window.ShowMeeting(id);
        Click(window, APPROVED_MoveToAnotherDevice); Click(Assert.Single(window.OwnedWindows), APPROVED_ShareContinue);
        await Until(() => suggested is not null);
        Assert.Matches(new Regex("^meeting-[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9a-f]{8}\\.dictum$"), suggested!);
        Assert.Equal("meeting-" + meta.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + id.Value[..8] + ".dictum", suggested);
        foreach (var word in meta.Title.Split(' ')) Assert.DoesNotContain(word, suggested, StringComparison.OrdinalIgnoreCase);
        window.Close(); return true;
    });

    [Fact]
    public Task Turning_Meetings_off_during_the_import_notice_cancels_the_import_and_frees_the_gate() => UiSession.RunAsync(async () =>
    {
        using var fixture = new Fixture(); using var recipient = new Fixture();
        var id = fixture.Store.Create(fixture.Files.Meta());
        fixture.Store.AppendSegments(id, MeetingFixture.Segments);
        var path = Path.Combine(fixture.Files.Root, "portable.dictum"); var code = TransferCode.New();
        ((MeetingsHost.CurrentSettingsStore)fixture.Store).BundleStore.ExportBundle(id, path, code);
        recipient.ImportsEnabled = true;
        var incoming = new MeetingsWindow(recipient.Host with { ChooseBundle = () => Task.FromResult<string?>(path) });
        incoming.Show();
        Click(incoming, APPROVED_ImportBundle);
        All<TextBox>(incoming).First().Text = code; Dispatcher.UIThread.RunJobs();
        Click(incoming, APPROVED_CheckBundle);
        await Until(() => Texts(incoming).Contains(APPROVED_ImportAgreement) && incoming.IsEnabled);
        var staging = Path.Combine(recipient.Files.Root, "meetings", ".import-" + id.Value);
        Assert.True(Directory.Exists(staging)); // the import is waiting on the notice, holding the import gate
        recipient.Settings = recipient.Settings with { Enabled = false };
        Click(incoming, APPROVED_Import);
        // The waiting import is released as cancelled: staging goes, nothing is listed, and the window can close.
        await Until(() => !Directory.Exists(staging));
        Assert.False(Directory.Exists(Path.Combine(recipient.Files.Root, "meetings", id.Value)));
        await Until(() => { if (incoming.IsVisible) incoming.Close(); Dispatcher.UIThread.RunJobs(); return !incoming.IsVisible; });
        Assert.False(incoming.IsVisible);
        // The gate is free: a fresh import of the same bundle completes instead of waiting forever.
        recipient.Settings = recipient.Settings with { Enabled = true };
        var route = recipient.Host.ProcessingRoute();
        var importer = recipient.Host.CreateBundleImporter((_, identity, _) => Task.FromResult<Agreement?>(
            new Agreement(DateTimeOffset.UtcNow, "spoken", ApprovedScriptVersion, AgreementKind.Import)
            {
                Confirmation = new("spoken", DateTimeOffset.UtcNow, AgreementKind.Import, ApprovedScriptVersion,
                    MeetingsWindow.BundleImportScript(route), identity, route),
            }), ApprovedScriptVersion, MeetingsWindow.BundleImportScript);
        Assert.Equal(id, await importer.ImportAsync(path, code, "test").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        return true;
    });

    [Fact]
    public void The_transfer_code_copy_is_always_kept_out_of_clipboard_history()
    {
        using var fixture = new Fixture();
        var platform = new FakePlatform(fixture.Paths);
        using var shell = new AppShell(platform, fixture.Paths, NullLog.Instance, new SecretRedactor(), new ShellOptions(), gpuMachine: () => false);
        // insert.clipboard_history on: ordinary copies may enter history, the transfer code never does.
        typeof(AppShell).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(shell, new AppSettings { Insert = new InsertSettings { ClipboardHistory = true } });
        var host = shell.CreateMeetingsHost();
        host.Copy("ordinary");
        Assert.False(platform.Desktop.LastKeepOutOfHistory);
        host.CopyOutOfHistory("abandon ability able about above absent");
        Assert.True(platform.Desktop.LastKeepOutOfHistory);
        Assert.Equal("abandon ability able about above absent", platform.Desktop.ClipboardText);
    }
}
