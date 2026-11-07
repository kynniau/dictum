using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dictum.Meetings;
using Dictum.Meetings.Import;
using Dictum.Meetings.Store;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.UI.Views.Meetings;

internal sealed partial class MeetingsWindow
{
    private Button? _bundleImportButton;

    /// <summary>The notice text shown before recording or import; a pure function of the route, so the bundle importer can require it exactly.</summary>
    internal static string NoticeScript(bool importing, bool systemAudio, MeetingProcessingRoute route)
    {
        var summaryRoute = route.SummaryEngine switch { SummaryEngine.Ollama => APPROVED_Ollama, SummaryEngine.OwnKey => string.Format(APPROVED_OwnKey, route.SummaryHost ?? PROPOSED_UnconfiguredProvider), _ => APPROVED_Local };
        if (route.SummaryEngine == SummaryEngine.Ollama && route.SummaryHost is not null)
            summaryRoute += Environment.NewLine + string.Format(PROPOSED_SummaryHost, route.SummaryHost);
        if (route.SummaryEngine == SummaryEngine.Local && route.SummaryModel is { } localModel)
            summaryRoute += Environment.NewLine + string.Format(PROPOSED_LocalSummaryModel, localModel);
        summaryRoute = (route.SpeechHost is { } speechHost ? string.Format(APPROVED_CloudSpeech, speechHost)
            : APPROVED_LocalSpeech) + Environment.NewLine + summaryRoute;
        var retention = route.KeepAudio == KeepAudio.Forever ? APPROVED_KeepForever
            : string.Format(APPROVED_KeepDays, route.KeepAudio == KeepAudio.Days30 ? 30 : 90);
        return string.Join("\n\n", importing ? APPROVED_ImportAgreement : APPROVED_Agreement,
            importing ? "" : (systemAudio ? APPROVED_CaptureSystem : APPROVED_CaptureMicrophone),
            importing ? "" : APPROVED_Uses, APPROVED_Access, summaryRoute, retention, APPROVED_ConfirmationNote);
    }
    internal static string BundleImportScript(MeetingProcessingRoute route) => NoticeScript(true, false, route);

    /// <summary>
    /// One line of a meeting record. Only the three agreement answers use the approved "you said everyone agreed" wording;
    /// every other answer (refused, agreed again, unconfirmed, discard requested, or one this version does not know) has
    /// its own line, so a recorded refusal can never be shown as "Each person said yes".
    /// </summary>
    internal static string RecordText(AgreementKind kind, DateTimeOffset at, string how)
    {
        var kindText = kind == AgreementKind.Live ? APPROVED_LiveKind : APPROVED_ImportKind;
        var local = at.ToLocalTime();
        string Agreed(string answer) => string.Format(APPROVED_Record, kindText, local, answer);
        string Line(string format) => string.Format(format, kindText, local);
        return how switch
        {
            "spoken" => Agreed(APPROVED_Spoken),
            "written" => Agreed(APPROVED_Written),
            "platform-announced" => Agreed(PROPOSED_Platform),
            "refused" => Line(APPROVED_RecordRefused),
            "agreed" => Line(APPROVED_RecordAgreedAgain),
            "unconfirmed" => Line(APPROVED_RecordUnconfirmed),
            "discard-requested" => Line(APPROVED_RecordDiscardRequested),
            _ => Line(APPROVED_RecordUnknown),
        };
    }

    /// <summary>The origin device's recorded agreement and confirmations (design A.6 step 3), shown before the import notice.</summary>
    private static Control OriginRecord(BundleInfo info)
    {
        var lines = info.OriginAgreement is { } agreement ? [RecordText(agreement.Kind, agreement.AcknowledgedAt, agreement.How)] : new List<string>();
        lines.AddRange(info.OriginConfirmations.Select(c => RecordText(c.Kind, c.At, c.Answer)));
        return Ui.Card(Ui.Stack(8, (lines.Count == 0 ? [PROPOSED_NotesText] : lines).Select(line => (Control)Ui.Text(line)).ToArray()), APPROVED_RecordHeading);
    }

    /// <summary>The suggested bundle file name: no title, people or words from the meeting, only its local start date and a short id.</summary>
    internal static string SuggestedBundleName(MeetingMeta meta) =>
        APPROVED_BundleSlug + "-" + meta.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + meta.Id.Value[..8] + ".dictum";

    private async Task MoveBundle(MeetingId id)
    {
        await Work(async () =>
        {
            var name = SuggestedBundleName(_host.Store.Read(id));
            string? destination;
            if (_host.ChooseBundleDestination is { } choose) destination = await choose(name);
            else
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = APPROVED_MoveToAnotherDevice, SuggestedFileName = name, DefaultExtension = "dictum",
                    FileTypeChoices = [new FilePickerFileType(APPROVED_MoveToAnotherDevice) { Patterns = ["*.dictum"] }],
                });
                destination = file?.TryGetLocalPath();
            }
            if (destination is null) return;
            var code = TransferCode.New();
            await Task.Run(() => _host.Store.ExportBundle(id, destination, code), _lifetime.Token);
            Screen(Ui.Heading(APPROVED_TransferCode), Named(new TextBlock
                { Text = code, FontSize = 28, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, APPROVED_TransferCode),
                Ui.Text(APPROVED_BundleNeedsCode), Ui.Text(APPROVED_SendCodeSeparately),
                SafeButton(APPROVED_CopyCode, () => { _host.CopyOutOfHistory(code); Say(PROPOSED_Copied); }),
                SafeButton(APPROVED_Done, () => ShowMeeting(id)));
        });
    }

    private async Task PickBundle()
    {
        try
        {
            string? path;
            if (_host.ChooseBundle is { } choose) path = await choose();
            else
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = APPROVED_ImportBundle, AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType(APPROVED_ImportBundle) { Patterns = ["*.dictum"] }],
                });
                path = files.FirstOrDefault()?.TryGetLocalPath();
            }
            if (path is not null) ShowBundleCode(path);
        }
        catch (Exception error) { BundleFailure(error); }
    }
    private void BundleFailure(Exception error) => Say(error.Message is BundleText.APPROVED_AlreadyHere or BundleText.APPROVED_Invalid or
        BundleText.APPROVED_Unsupported or BundleText.APPROVED_CodeRequired ? error.Message : APPROVED_BundleFailed);

    internal void ShowBundleCode(string path)
    {
        var info = _host.CreateBundleImporter((_, _, _) => Task.FromResult<Agreement?>(null), ApprovedScriptVersion, BundleImportScript).Inspect(path);
        var boxes = Enumerable.Range(1, 6).Select(i => Named(new TextBox { MinWidth = 76, MaxWidth = 120 }, string.Format(APPROVED_CodeWord, i))).ToArray();
        for (var i = 0; i < boxes.Length; i++)
        {
            var index = i;
            boxes[i].TextChanged += (_, _) =>
            {
                var words = TransferCode.Normalise(boxes[index].Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length <= 1) return;
                for (var j = 0; j < words.Length && index + j < 6; j++) boxes[index + j].Text = words[j];
                boxes[Math.Min(5, index + words.Length - 1)].Focus(NavigationMethod.Tab);
            };
        }
        Screen(Ui.Heading(APPROVED_ImportBundle), Ui.Text(string.Format(APPROVED_BundleInfo, info.FileCount, info.Bytes, info.Platform, info.AppVersion)),
            Ui.Heading(APPROVED_TransferCode), Ui.Row(8, boxes),
            SafeButton(APPROVED_CheckBundle, () =>
            {
                var code = string.Join(' ', boxes.Select(b => b.Text ?? ""));
                _importCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _activeImport = ImportBundle(path, code, _importCancellation.Token);
            }), SafeButton(APPROVED_Cancel, ShowList));
    }

    private async Task ImportBundle(string path, string code, CancellationToken ct)
    {
        _busy = true; _screen.IsEnabled = false;
        var declined = false;
        try
        {
            var importer = _host.CreateBundleImporter(async (info, _, cancellation) =>
            {
                var answer = new TaskCompletionSource<Agreement?>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellation.Register(() => answer.TrySetCanceled(cancellation));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _busy = false; _screen.IsEnabled = true;
                    BeginImportAgreement(path, agreement =>
                    {
                        declined = agreement is null;
                        _importRoute = agreement?.Confirmation?.Route;
                        answer.TrySetResult(agreement);
                    }, () => OriginRecord(info));
                });
                return await answer.Task.ConfigureAwait(false);
            }, ApprovedScriptVersion, BundleImportScript);
            var progress = new Progress<string>(text =>
            {
                _screen.IsEnabled = true;
                Screen(Ui.Heading(text), new ProgressBar { IsIndeterminate = true },
                    SafeButton(APPROVED_Cancel, () => _importCancellation?.Cancel()));
                Say(text);
            });
            var id = await Task.Run(() => importer.ImportAsync(path, code, Environment.UserName, progress, ct), ct);
            if (!_exitPending) ShowMeeting(id);
        }
        // A declined notice already chose the next screen (list, notes, or Meetings turned off).
        catch (OperationCanceledException) { if (!declined && !_exitPending) ShowList(); }
        catch (InvalidOperationException error) when (error.Message == BundleText.APPROVED_ConfirmationRequired) { }
        catch (Exception error) { BundleFailure(error); }
        finally
        {
            _busy = false; _screen.IsEnabled = true;
            _importCancellation?.Dispose(); _importCancellation = null; _importRoute = null;
            if (_screen.Content is Control content) FocusScreen(content);
        }
    }
}
