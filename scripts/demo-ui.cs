// Windows UI Automation and input helpers for scripts\demo-inside.ps1, which compiles this file with Add-Type inside
// Windows Sandbox (Windows PowerShell 5.1, so C# 5: no string interpolation, no expression-bodied members, no out var).
// Every pointer action moves the real cursor visibly (the recording shows it) and clicks with SendInput, so what the
// video shows is what a person would do. Never run it outside the sandbox: it moves the mouse and presses keys.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

public static class DemoUi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_EXTENDEDKEY = 0x0001;

    /// <summary>Physical pixels everywhere: UI Automation rectangles, the cursor and the recording agree.</summary>
    public static void Init()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
        SetProcessDPIAware();
    }

    public static int ScreenWidth { get { return GetSystemMetrics(0); } }
    public static int ScreenHeight { get { return GetSystemMetrics(1); } }

    // ---- finding elements ----

    /// <summary>
    /// The name test: exact (case-insensitive, trimmed), prefix, contains or regex. A null or empty pattern matches anything.
    /// </summary>
    public static bool NameMatches(string actual, string pattern, string mode)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        actual = (actual ?? "").Trim();
        switch ((mode ?? "exact").ToLowerInvariant())
        {
            case "prefix": return actual.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
            case "contains": return actual.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            case "regex": return Regex.IsMatch(actual, pattern, RegexOptions.IgnoreCase);
            default: return string.Equals(actual, pattern.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ControlType TypeOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        switch (name.ToLowerInvariant())
        {
            case "button": return ControlType.Button;
            case "edit": return ControlType.Edit;
            case "text": return ControlType.Text;
            case "listitem": return ControlType.ListItem;
            case "list": return ControlType.List;
            case "checkbox": return ControlType.CheckBox;
            case "radiobutton": return ControlType.RadioButton;
            case "combobox": return ControlType.ComboBox;
            case "window": return ControlType.Window;
            case "hyperlink": return ControlType.Hyperlink;
            case "menuitem": return ControlType.MenuItem;
            case "tabitem": return ControlType.TabItem;
            case "pane": return ControlType.Pane;
            case "document": return ControlType.Document;
            case "image": return ControlType.Image;
            case "group": return ControlType.Group;
            default: throw new ArgumentException("Unknown control type '" + name + "'.");
        }
    }

    /// <summary>The top-level windows whose name matches <paramref name="window"/> (regex), or every top-level window.</summary>
    public static List<AutomationElement> Windows(string window, string windowClass)
    {
        var list = new List<AutomationElement>();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                var c = w.Current;
                if (!string.IsNullOrEmpty(window) && !Regex.IsMatch(c.Name ?? "", window, RegexOptions.IgnoreCase)) continue;
                if (!string.IsNullOrEmpty(windowClass) && !string.Equals(c.ClassName, windowClass, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(w);
            }
            catch (ElementNotAvailableException) { }
        }
        return list;
    }

    /// <summary>
    /// Every element matching the name test and control type under the matching windows (the windows themselves count
    /// too), in tree order.
    /// </summary>
    public static List<AutomationElement> FindAll(string window, string windowClass, string name, string match, string controlType, string automationId)
    {
        var found = new List<AutomationElement>();
        var type = TypeOf(controlType);
        Condition condition = Condition.TrueCondition;
        if (type != null) condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
        if (!string.IsNullOrEmpty(automationId))
        {
            var id = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
            condition = type != null ? (Condition)new AndCondition(condition, id) : id;
        }
        foreach (var w in Windows(window, windowClass))
        {
            try
            {
                foreach (AutomationElement e in w.FindAll(TreeScope.Subtree, condition))
                {
                    try
                    {
                        if (NameMatches(e.Current.Name, name, match)) found.Add(e);
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch (ElementNotAvailableException) { }
        }
        return found;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a match. With <paramref name="sameRowAs"/>, the match is the one whose
    /// vertical centre is nearest that of the first element named <paramref name="sameRowAs"/> (prefix test): several
    /// buttons share one name ("Use") and only the row tells them apart. <paramref name="index"/> picks among the rest.
    /// Returns null on timeout.
    /// </summary>
    public static AutomationElement Find(string window, string windowClass, string name, string match, string controlType, string automationId,
        string sameRowAs, int index, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var all = FindAll(window, windowClass, name, match, controlType, automationId);
            if (all.Count > 0)
            {
                if (!string.IsNullOrEmpty(sameRowAs))
                {
                    var anchors = FindAll(window, windowClass, sameRowAs, "prefix", null, null);
                    if (anchors.Count > 0)
                    {
                        var a = anchors[0].Current.BoundingRectangle;
                        var ay = a.Top + a.Height / 2;
                        AutomationElement best = null;
                        var bestDistance = double.MaxValue;
                        foreach (var e in all)
                        {
                            var r = e.Current.BoundingRectangle;
                            if (r.IsEmpty) continue;
                            var d = Math.Abs(r.Top + r.Height / 2 - ay);
                            if (d < bestDistance) { bestDistance = d; best = e; }
                        }
                        if (best != null) return best;
                    }
                }
                else if (index < all.Count)
                {
                    return all[index];
                }
            }
            if (watch.ElapsedMilliseconds >= timeoutMs) return null;
            Thread.Sleep(250);
        }
    }

    /// <summary>The top-level window holding <paramref name="e"/>.</summary>
    public static AutomationElement TopLevel(AutomationElement e)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = e;
        while (true)
        {
            var parent = walker.GetParent(current);
            if (parent == null || Automation.Compare(parent, AutomationElement.RootElement)) return current;
            current = parent;
        }
    }

    /// <summary>
    /// Brings <paramref name="e"/> into its window's view: ScrollItemPattern when the element offers it, else the mouse wheel
    /// over the window (the cursor moves there first, as a person's would). Returns false if it is still out of view.
    /// </summary>
    public static bool EnsureVisible(AutomationElement e, int glideMs)
    {
        var top = TopLevel(e);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            var r = e.Current.BoundingRectangle;
            var w = top.Current.BoundingRectangle;
            // Inside its window's rectangle counts as in view (UI Automation's IsOffscreen is not reliable across frameworks:
            // the taskbar's buttons report it while they are on screen, observed 21/10/2026).
            var visible = !r.IsEmpty && r.Top >= w.Top - 1 && r.Bottom <= w.Bottom + 1 && r.Left >= w.Left - 1 && r.Right <= w.Right + 1;
            if (visible) return true;
            if (w.Height < 200) return false; // a strip such as the taskbar does not scroll
            if (attempt == 0)
            {
                object pattern;
                if (e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out pattern))
                {
                    try { ((ScrollItemPattern)pattern).ScrollIntoView(); Thread.Sleep(300); continue; } catch (InvalidOperationException) { }
                }
                Glide((int)(w.Left + w.Width * 0.6), (int)(w.Top + w.Height * 0.55), glideMs);
            }
            var down = r.IsEmpty || r.Top > w.Top;
            Wheel(down ? -120 : 120);
            Thread.Sleep(180);
        }
        return false;
    }

    /// <summary>The clickable point: UI Automation's own when it has one inside the element, else the rectangle's centre.</summary>
    public static POINT ClickPoint(AutomationElement e)
    {
        var r = e.Current.BoundingRectangle;
        Point p;
        try
        {
            if (e.TryGetClickablePoint(out p) && r.Contains(p)) return new POINT { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
        }
        catch (ElementNotAvailableException) { }
        return new POINT { X = (int)Math.Round(r.Left + r.Width / 2), Y = (int)Math.Round(r.Top + r.Height / 2) };
    }

    public static bool Invoke(AutomationElement e)
    {
        object pattern;
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) { ((InvokePattern)pattern).Invoke(); return true; }
        if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) { ((SelectionItemPattern)pattern).Select(); return true; }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) { ((TogglePattern)pattern).Toggle(); return true; }
        return false;
    }

    // ---- the pointer ----

    /// <summary>Moves the cursor to (x, y) over <paramref name="ms"/>, eased, so the viewer's eye can follow it.</summary>
    public static void Glide(int x, int y, int ms)
    {
        POINT from;
        GetCursorPos(out from);
        var steps = Math.Max(1, ms / 15);
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            SetCursorPos((int)Math.Round(from.X + (x - from.X) * eased), (int)Math.Round(from.Y + (y - from.Y) * eased));
            Thread.Sleep(15);
        }
        SetCursorPos(x, y);
    }

    private static void Mouse(uint flags, int data)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dwFlags = flags, mouseData = data };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Click()
    {
        Mouse(MOUSEEVENTF_LEFTDOWN, 0);
        Thread.Sleep(70);
        Mouse(MOUSEEVENTF_LEFTUP, 0);
    }

    public static void DoubleClick()
    {
        Click();
        Thread.Sleep(90);
        Click();
    }

    public static void Wheel(int delta)
    {
        Mouse(MOUSEEVENTF_WHEEL, delta);
    }

    // ---- the keyboard ----

    private static void Key(ushort vk, ushort scan, uint flags)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    /// <summary>Types <paramref name="text"/> one character at a time (Unicode input), visibly.</summary>
    public static void TypeText(string text, int delayMs)
    {
        foreach (var ch in text)
        {
            Key(0, ch, KEYEVENTF_UNICODE);
            Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            Thread.Sleep(delayMs);
        }
    }

    private static readonly Dictionary<string, ushort> Keys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        { "Enter", 0x0D }, { "Tab", 0x09 }, { "Escape", 0x1B }, { "Esc", 0x1B }, { "Space", 0x20 }, { "Backspace", 0x08 },
        { "Delete", 0x2E }, { "Home", 0x24 }, { "End", 0x23 }, { "Up", 0x26 }, { "Down", 0x28 }, { "Left", 0x25 }, { "Right", 0x27 },
        { "PageUp", 0x21 }, { "PageDown", 0x22 }, { "Win", 0x5B }, { "Ctrl", 0x11 }, { "Shift", 0x10 }, { "Alt", 0x12 },
        { "RightCtrl", 0xA3 }, { "LeftCtrl", 0xA2 }, { "RightAlt", 0xA5 }, { "F4", 0x73 }, { "F9", 0x78 }, { "A", 0x41 },
    };

    private static readonly HashSet<ushort> Extended = new HashSet<ushort> { 0x2E, 0x24, 0x23, 0x26, 0x28, 0x25, 0x27, 0x21, 0x22, 0x5B, 0xA3, 0xA5 };

    /// <summary>Presses a chord such as "Enter", "Win" or "Ctrl+A": modifiers down in order, then up in reverse.</summary>
    public static void Press(string chord, int holdMs)
    {
        var names = chord.Split('+');
        var codes = new List<ushort>();
        foreach (var n in names)
        {
            ushort vk;
            if (!Keys.TryGetValue(n.Trim(), out vk)) throw new ArgumentException("Unknown key '" + n + "' in '" + chord + "'.");
            codes.Add(vk);
        }
        foreach (var vk in codes) Key(vk, 0, Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        Thread.Sleep(holdMs);
        for (var i = codes.Count - 1; i >= 0; i--) Key(codes[i], 0, KEYEVENTF_KEYUP | (Extended.Contains(codes[i]) ? KEYEVENTF_EXTENDEDKEY : 0));
    }

    // ---- the desktop ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
    [DllImport("user32.dll")] private static extern bool SetSysColors(int count, int[] elements, int[] colors);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>
    /// A plain desktop for the recording: a solid colour instead of the picture, and the desktop icons hidden (the same
    /// toggle as the desktop's View > Show desktop icons). Returns what it did.
    /// </summary>
    public static string PlainDesktop(int r, int g, int b, bool hideIcons)
    {
        var done = new List<string>();
        SetSysColors(1, new[] { 1 }, new[] { r | (g << 8) | (b << 16) }); // COLOR_DESKTOP
        if (SystemParametersInfo(0x0014, 0, "", 0x01 | 0x02)) done.Add("solid colour background"); // SPI_SETDESKWALLPAPER, no picture
        if (hideIcons)
        {
            var view = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            var worker = IntPtr.Zero;
            while (view == IntPtr.Zero && (worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            var icons = view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (icons != IntPtr.Zero && IsWindowVisible(icons))
            {
                SendMessage(view, 0x0111, new IntPtr(0x7402), IntPtr.Zero); // WM_COMMAND: toggle desktop icons
                done.Add("desktop icons hidden");
            }
            else done.Add(view == IntPtr.Zero ? "desktop icons: no desktop view found" : "desktop icons already hidden");
        }
        return string.Join(", ", done.ToArray());
    }

    /// <summary>One line per element under the matching windows, for the step log when a lookup fails.</summary>
    public static List<string> Dump(string window, int max)
    {
        var lines = new List<string>();
        foreach (var w in Windows(window, null))
        {
            try
            {
                foreach (AutomationElement e in w.FindAll(TreeScope.Subtree, Condition.TrueCondition))
                {
                    if (lines.Count >= max) return lines;
                    try
                    {
                        var c = e.Current;
                        lines.Add(c.ControlType.ProgrammaticName.Replace("ControlType.", "") + " '" + (c.Name ?? "").Replace("\r", " ").Replace("\n", " ") + "' class=" +
                                  c.ClassName + " id=" + c.AutomationId + " rect=" + c.BoundingRectangle + (c.IsOffscreen ? " offscreen" : ""));
                    }
                    catch (ElementNotAvailableException) { }
                }
            }
            catch (ElementNotAvailableException) { }
        }
        return lines;
    }
}
