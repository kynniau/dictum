# Prints every keyboard event Windows delivers (low-level hook), for choosing a hotkey.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\key-events.ps1 -Seconds 20
param([int]$Seconds = 20)

$src = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class KeyLog
{
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    static HookProc _proc;

    public static void Run(int seconds)
    {
        _proc = Callback;
        IntPtr h = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
        if (h == IntPtr.Zero) { Console.WriteLine("hook failed: " + Marshal.GetLastWin32Error()); return; }
        Console.WriteLine("Hook installed. Press keys now (" + seconds + " s). Each line is one event.");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            System.Threading.Thread.Sleep(5);
        }
        UnhookWindowsHookEx(h);
        Console.WriteLine("Done.");
    }

    static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            string kind = msg == 0x100 ? "down" : msg == 0x101 ? "up" : msg == 0x104 ? "sysdown" : msg == 0x105 ? "sysup" : msg.ToString("x");
            string name = Enum.GetName(typeof(System.Windows.Forms.Keys), (int)k.vkCode) ?? "?";
            Console.WriteLine(string.Format("{0,-8} vk=0x{1:X2} {2,-12} scan=0x{3:X2} flags=0x{4:X}{5}{6}",
                kind, k.vkCode, name, k.scanCode, k.flags,
                (k.flags & 0x01) != 0 ? " extended" : "",
                (k.flags & 0x10) != 0 ? " injected" : ""));
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms
[KeyLog]::Run($Seconds)
