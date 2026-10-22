using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Dictum.UI.Overlays;
using Dictum.Windows;

namespace Dictum.UiProbe;

/// <summary>
/// Shows the pill, the orb and the screen-edge glow with Dictum's real Windows platform and checks, in this order: none
/// takes the foreground (SPEC §7's GetForegroundWindow test), each carries the no-activate and click-through styles, a
/// click at its centre reaches the window below, and the pill is actually drawn. One line per check on stdout.
/// Exit code 0: all passed; 1: a check failed; 2: this desktop cannot observe activation (a locked or disconnected
/// session), so nothing was proven either way. With <c>--dialog-capture</c> it checks instead that a question opened by a
/// click gets the mouse (Program.Dialogs.cs).
/// </summary>
internal static partial class Program
{
    private const int GWL_EXSTYLE = -20;
    private const uint GA_ROOT = 2;
    private static int _exit;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);

    [STAThread]
    public static int Main(string[] args)
    {
        using var platform = new WindowsPlatform();
        AppBuilder.Configure(() => new Dictum.UI.App { Started = lifetime => _ = args.Contains(DialogCaptureArg) ? RunAsync(lifetime, () => DialogCaptureAsync(lifetime)) : RunAsync(lifetime, () => ProbeAsync(platform)) })
            .UseWin32()
            .UseSkia()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        return _exit;
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime lifetime, Func<Task<int>> probe)
    {
        try
        {
            _exit = await probe();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            _exit = 1;
        }
        finally
        {
            lifetime.Shutdown(_exit);
        }
    }

    private static bool Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        if (!ok) _exit = 1;
        return ok;
    }

    private static nint Handle(Window w) => w.TryGetPlatformHandle()?.Handle ?? 0;

    private static async Task<int> ProbeAsync(WindowsPlatform platform)
    {
        var host = new Window { Title = "Dictum focus host", Width = 300, Height = 120, Position = new PixelPoint(40, 40) };
        host.Show();
        host.Activate();
        await Task.Delay(400);
        if (GetForegroundWindow() == 0)
        {
            host.Close();
            Console.WriteLine("SKIP GetForegroundWindow() is 0: no window can take the foreground on this desktop (locked, or a minimised or disconnected Remote Desktop session).");
            return 2;
        }
        var strong = GetForegroundWindow() == Handle(host);
        Console.WriteLine(strong ? "INFO strong mode: this process owns the foreground" : $"INFO weak mode: the foreground (0x{GetForegroundWindow():X}) belongs to another process");
        if (strong)
        {
            var control = new Window { Title = "activating control", Width = 200, Height = 80, Position = new PixelPoint(60, 220) };
            control.Show();
            await Task.Delay(400);
            Check(GetForegroundWindow() == Handle(control), "an ordinary window takes the foreground (so the checks below can see activation)");
            control.Close();
            host.Activate();
            await Task.Delay(400);
        }
        var before = GetForegroundWindow();

        var pill = new PillWindow(platform);
        pill.Present(new PillState(PillView.RecordingHold, Environment.TickCount64));
        await Task.Delay(500);
        var pillHandle = Handle(pill);
        Check(pill.IsVisible, "the pill is shown");
        Check(GetForegroundWindow() == before && GetForegroundWindow() != pillHandle, $"showing the pill left the foreground unchanged (0x{before:X})");
        Styles(pillHandle, "pill");
        ClickThrough(pillHandle, "pill");
        var shown = Pixel(pillHandle);
        foreach (var view in new[] { PillView.RecordingHandsFree, PillView.Transcribing, PillView.Busy, PillView.Polishing, PillView.Done })
        {
            pill.Present(new PillState(view, Environment.TickCount64, 4));
            await Task.Delay(150);
            Check(GetForegroundWindow() == before, $"pill state {view} left the foreground unchanged");
        }
        await Task.Delay(1400); // the Done flash, then the fade
        Check(!pill.IsVisible, "the pill hid itself after Done");
        Drawn(shown, Pixel(pillHandle));
        Check(GetForegroundWindow() == before, "hiding the pill left the foreground unchanged");

        var orb = new OrbWindow(platform);
        orb.Present(PillView.RecordingHold, "bottom-right");
        await Task.Delay(400);
        Check(orb.IsVisible && GetForegroundWindow() == before, "showing the orb left the foreground unchanged");
        Styles(Handle(orb), "orb");
        ClickThrough(Handle(orb), "orb");
        orb.Present(PillView.Hidden, "bottom-right");

        var edge = new EdgeWindow(platform);
        if (pill.Screens.Primary is { } screen) edge.Cover(screen);
        edge.Show();
        await Task.Delay(400);
        Check(edge.IsVisible && GetForegroundWindow() == before, "showing the screen-edge glow left the foreground unchanged");
        Styles(Handle(edge), "edge");
        ClickThrough(Handle(edge), "edge");

        edge.Close();
        orb.Stop();
        pill.Close();
        host.Close();
        await Task.Delay(200);
        return _exit;
    }

    private static void Styles(nint handle, string name)
    {
        var ex = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);
        foreach (var (flag, label) in new[] { (0x08000000L, "WS_EX_NOACTIVATE"), (0x00000080L, "WS_EX_TOOLWINDOW"), (0x00000020L, "WS_EX_TRANSPARENT"), (0x00080000L, "WS_EX_LAYERED"), (0x00000008L, "WS_EX_TOPMOST") })
            Check((ex & flag) != 0, $"{name} has {label}");
    }

    /// <summary>The window under the overlay's centre is another one: a click there would reach it.</summary>
    private static void ClickThrough(nint handle, string name)
    {
        GetWindowRect(handle, out var r);
        var hit = WindowFromPoint(new POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
        Check(hit != 0 && GetAncestor(hit, GA_ROOT) != handle, $"a click at the {name}'s centre reaches the window below (hit 0x{hit:X})");
    }

    /// <summary>The screen pixel just inside the pill's left edge (the window carries an 18 px shadow margin), or null if unreadable.</summary>
    private static (uint R, uint G, uint B)? Pixel(nint handle)
    {
        GetWindowRect(handle, out var r);
        var dc = GetDC(0);
        var pixel = GetPixel(dc, r.Left + 18 + 6, (r.Top + r.Bottom) / 2 - 8);
        ReleaseDC(0, dc);
        return pixel == 0xFFFFFFFF ? null : (pixel & 0xFF, (pixel >> 8) & 0xFF, (pixel >> 16) & 0xFF);
    }

    /// <summary>The pill's own pixels were on screen: dark navy while shown, and something else there once it hid.</summary>
    private static void Drawn((uint R, uint G, uint B)? shown, (uint R, uint G, uint B)? hidden)
    {
        if (shown is not { } s || hidden is not { } h)
        {
            Console.WriteLine("INFO the screen could not be read here, so drawing was not checked");
            return;
        }
        var text = $"#{s.R:X2}{s.G:X2}{s.B:X2} while shown, #{h.R:X2}{h.G:X2}{h.B:X2} after it hid";
        var differs = Math.Max(Math.Max(Math.Abs((int)s.R - (int)h.R), Math.Abs((int)s.G - (int)h.G)), Math.Abs((int)s.B - (int)h.B)) > 24;
        if (!differs && s.R < 60 && s.G < 60 && s.B < 80)
        {
            Console.WriteLine($"INFO the pill was dark navy ({text}), but so is what lies beneath it, so drawing is not proven");
            return;
        }
        Check(s.R < 60 && s.G < 60 && s.B < 80 && differs, $"the pill is drawn ({text}; expected the dark navy #0B1220 while shown)");
    }
}
