using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dictum.UI.Shell;
using Dictum.UI.Views;

namespace Dictum.UiProbe;

/// <summary>
/// <c>--dialog-capture</c>: a question opened by a mouse click on a <see cref="Ui.Button"/> must get the mouse (fix lane,
/// 23/10/2026). Avalonia raises Click inside the button's pointer-released handler, before the opener releases the mouse
/// capture, and <see cref="Modal.Run"/> then runs the dialog's frame inside that Click: a dialog shown on its own got no mouse
/// input until it closed. The click is the probe's technique: WM_LBUTTONDOWN and WM_LBUTTONUP sent to the opener, and
/// GetCapture() read inside the dialog's frame. Three openers: an ordinary window, a Topmost one (the tray panel and the
/// personal windows are Topmost), and one closed while its dialog is up.
/// </summary>
internal static partial class Program
{
    internal const string DialogCaptureArg = "--dialog-capture";
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, GW_OWNER = 4;
    private const nint MK_LBUTTON = 1;

    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private static async Task<int> DialogCaptureAsync(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        await Opener(lifetime, "ordinary opener", topmost: false, closeOwner: false);
        await Opener(lifetime, "Topmost opener", topmost: true, closeOwner: false);
        await Opener(lifetime, "opener closed while its dialog is up", topmost: false, closeOwner: true);
        return _exit;
    }

    private static nint Lparam(Point client) => ((int)client.Y << 16) | ((int)client.X & 0xFFFF);

    private static async Task Opener(IClassicDesktopStyleApplicationLifetime lifetime, string name, bool topmost, bool closeOwner)
    {
        var opener = new Window { Title = $"Dictum dialog probe: {name}", Width = 640, Height = 420, Position = new PixelPoint(40, 40), Topmost = topmost, ShowInTaskbar = false };
        var inside = false;
        var ranInside = false;
        int? answer = null;
        int? second = null;
        var secondShown = false;
        nint handle = 0;
        Button button = null!;
        button = Ui.Button("Open the question", "primary", () =>
        {
            if (inside)
            {
                second = new ShellDialogs().Ask("Second question", "Opened by a second click while the first is up.", new DialogChoice("OK"));
                return;
            }
            inside = true;
            Dispatcher.UIThread.Post(() => _ = InsideAsync(), DispatcherPriority.Background);
            answer = new ShellDialogs().Ask("Probe question", "Opened by a click on a button.", new DialogChoice("OK"), new DialogChoice("Cancel", IsDefault: true));
            if (closeOwner) return;
            Check(IsWindowEnabled(Handle(opener)), $"{name}: the opener is enabled again once the dialog has closed");
            Check(ReferenceEquals(opener.FocusManager?.GetFocusedElement(), button), $"{name}: focus is back on the button that opened the dialog");
        });
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        opener.Content = button;
        opener.Show();
        opener.Activate();
        await Task.Delay(400);
        handle = Handle(opener);
        var at = Lparam(new Point(100, 60));
        SendMessage(handle, WM_LBUTTONDOWN, MK_LBUTTON, at);
        SendMessage(handle, WM_LBUTTONUP, 0, at); // returns once the Click, and the dialog's frame inside it, are over
        Check(ranInside, $"{name}: the checks inside the dialog's frame ran");
        Check(closeOwner ? answer == -1 : answer == 0, $"{name}: Ask returned {answer?.ToString() ?? "nothing"} ({(closeOwner ? "-1, closed with its owner" : "0, the OK clicked")})");
        if (opener.IsVisible) opener.Close();
        await Task.Delay(200);
        return;

        async Task InsideAsync()
        {
            QuestionWindow? dialog = null;
            for (var i = 0; i < 60 && dialog is null; i++)
            {
                dialog = lifetime.Windows.OfType<QuestionWindow>().FirstOrDefault(w => w.IsVisible && w.Title == "Dictum: Probe question");
                if (dialog is null) await Task.Delay(50);
            }
            if (!Check(dialog is not null, $"{name}: the question is shown"))
            {
                foreach (var w in lifetime.Windows.OfType<QuestionWindow>().ToList()) w.Close(); // so the frame ends
                return;
            }
            var shown = dialog!;
            await Task.Delay(400);
            var dialogHandle = Handle(shown);
            var capture = GetCapture();
            Check(capture != handle, $"{name}: inside the dialog's frame the opener no longer holds the mouse capture (GetCapture 0x{capture:X}, opener 0x{handle:X})");
            Check(GetWindow(dialogHandle, GW_OWNER) == handle, $"{name}: the dialog is owned by the window whose button opened it");
            Check(!IsWindowEnabled(handle), $"{name}: the opener is disabled while the dialog is up, so the mouse cannot reach it");
            GetWindowRect(dialogHandle, out var r);
            var hit = WindowFromPoint(new POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
            Check(GetAncestor(hit, GA_ROOT) == dialogHandle, $"{name}: the dialog is on top of its opener at its centre (hit 0x{hit:X}, dialog 0x{dialogHandle:X})");

            Dispatcher.UIThread.Post(async () => // a second dialog, if one opens, is closed so the probe goes on
            {
                await Task.Delay(300);
                foreach (var extra in lifetime.Windows.OfType<QuestionWindow>().Where(w => w.IsVisible && w.Title == "Dictum: Second question").ToList())
                {
                    secondShown = true;
                    extra.Close();
                }
            });
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); // a second click that still reaches the opener
            await Task.Delay(500);
            Check(second == -1 && !secondShown, $"{name}: a second click while the dialog is up opens no second dialog (answer {second?.ToString() ?? "none"})");

            ranInside = true;
            if (closeOwner)
            {
                opener.Close(); // Avalonia closes its owned windows at once
                Check(!shown.IsVisible, $"{name}: closing the opener closed its dialog");
                if (shown.IsVisible) shown.Close(); // so the frame ends
                return;
            }
            // The OK button, clicked with mouse messages at its centre (client pixels).
            var ok = shown.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "OK");
            var centre = ok.TranslatePoint(new Point(ok.Bounds.Width / 2, ok.Bounds.Height / 2), shown)!.Value * shown.RenderScaling;
            SendMessage(dialogHandle, WM_LBUTTONDOWN, MK_LBUTTON, Lparam(centre));
            SendMessage(dialogHandle, WM_LBUTTONUP, 0, Lparam(centre));
            await Task.Delay(300);
            if (shown.IsVisible) // the click did not close it: close it so the probe ends, and the answer check reports it
                shown.Close();
        }
    }
}
