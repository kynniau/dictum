using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dictum.Meetings;
using Dictum.Meetings.Store;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.UI.Views.Meetings;

internal sealed partial class MeetingsWindow
{
    private Button? _bundleImportButton;
    private async Task MoveBundle(MeetingId id)
    {
        await Work(async () =>
        {
            var title = _host.Store.Read(id).Title.ToLowerInvariant();
            var slug = string.Join('-', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new string(word.Where(char.IsAsciiLetterOrDigit).ToArray())).Where(word => word.Length != 0));
            if (slug.Length == 0) slug = PROPOSED_BundleSlug;
            slug = slug[..Math.Min(slug.Length, 80)];
            var name = slug + "-" + id.Value + ".dictum";
            string? destination;
            if (_host.ChooseBundleDestination is { } choose) destination = await choose(name);
            else
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = PROPOSED_MoveToAnotherDevice, SuggestedFileName = name, DefaultExtension = "dictum",
                    FileTypeChoices = [new FilePickerFileType(PROPOSED_MoveToAnotherDevice) { Patterns = ["*.dictum"] }],
                });
                destination = file?.TryGetLocalPath();
            }
            if (destination is null) return;
            var code = TransferCode.New();
            await Task.Run(() => _host.Store.ExportBundle(id, destination, code), _lifetime.Token);
            Screen(Ui.Heading(PROPOSED_TransferCode), Named(new TextBlock
                { Text = code, FontSize = 28, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, PROPOSED_TransferCode),
                Ui.Text(PROPOSED_BundleNeedsCode), Ui.Text(PROPOSED_SendCodeSeparately),
                SafeButton(PROPOSED_CopyCode, () => { _host.Copy(code); Say(PROPOSED_Copied); }),
                SafeButton(PROPOSED_Done, () => ShowMeeting(id)));
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
                    Title = PROPOSED_ImportBundle, AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType(PROPOSED_ImportBundle) { Patterns = ["*.dictum"] }],
                });
                path = files.FirstOrDefault()?.TryGetLocalPath();
            }
            if (path is not null) ShowBundleCode(path);
        }
        catch (Exception error) { BundleFailure(error); }
    }
    private void BundleFailure(Exception error) => Say(error.Message is BundleText.PROPOSED_AlreadyHere or BundleText.PROPOSED_Invalid or
        BundleText.PROPOSED_Unsupported or BundleText.PROPOSED_CodeRequired ? error.Message : PROPOSED_BundleFailed);

    internal void ShowBundleCode(string path)
    {
        var info = _host.CreateBundleImporter((_, _, _) => Task.FromResult<Agreement?>(null)).Inspect(path);
        var boxes = Enumerable.Range(1, 6).Select(i => Named(new TextBox { MinWidth = 76, MaxWidth = 120 }, string.Format(PROPOSED_CodeWord, i))).ToArray();
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
        Screen(Ui.Heading(PROPOSED_ImportBundle), Ui.Text(string.Format(PROPOSED_BundleInfo, info.FileCount, info.Bytes, info.Platform, info.AppVersion)),
            Ui.Heading(PROPOSED_TransferCode), Ui.Row(8, boxes),
            SafeButton(PROPOSED_CheckBundle, () =>
            {
                var code = string.Join(' ', boxes.Select(b => b.Text ?? ""));
                _importCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _activeImport = ImportBundle(path, code, _importCancellation.Token);
            }), SafeButton(APPROVED_Cancel, ShowList));
    }

    private async Task ImportBundle(string path, string code, CancellationToken ct)
    {
        _busy = true; _screen.IsEnabled = false;
        try
        {
            var importer = _host.CreateBundleImporter(async (_, _, cancellation) =>
            {
                var answer = new TaskCompletionSource<Agreement?>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellation.Register(() => answer.TrySetCanceled(cancellation));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _busy = false; _screen.IsEnabled = true;
                    BeginImportAgreement(path, agreement =>
                    {
                        _importRoute = agreement?.Confirmation?.Route;
                        answer.TrySetResult(agreement);
                    });
                });
                return await answer.Task.ConfigureAwait(false);
            });
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
        catch (OperationCanceledException) { if (!_exitPending) ShowList(); }
        catch (InvalidOperationException error) when (error.Message == BundleText.PROPOSED_ConfirmationRequired) { }
        catch (Exception error) { BundleFailure(error); }
        finally
        {
            _busy = false; _screen.IsEnabled = true;
            _importCancellation?.Dispose(); _importCancellation = null; _importRoute = null;
            if (_screen.Content is Control content) FocusScreen(content);
        }
    }
}
