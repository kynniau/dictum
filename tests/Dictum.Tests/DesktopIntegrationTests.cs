using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Windows.Audio;
using Dictum.Windows.Input;

namespace Dictum.Tests;

/// <summary>
/// End-to-end checks against the real Windows input stack and microphone. Gated because they inject a
/// system-wide keystroke (F24, unbound on almost every system) or briefly open the default microphone.
/// </summary>
public class DesktopIntegrationTests(ITestOutputHelper output)
{
    // INPUT on x64: type at 0, the union (8-byte aligned) at 8; MOUSEINPUT makes the whole struct 40 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] inputs, int size);

    [Fact]
    public void Native_input_struct_matches_the_x64_layout()
    {
        Assert.Equal(40, Marshal.SizeOf<Dictum.Windows.Interop.Native.INPUT>());
        Assert.Equal(24, Marshal.SizeOf<Dictum.Windows.Interop.Native.KEYBDINPUT>());
        Assert.Equal(8, (int)Marshal.OffsetOf<Dictum.Windows.Interop.Native.INPUT>(nameof(Dictum.Windows.Interop.Native.INPUT.U)));
    }

    [Fact]
    public void Hook_thread_sees_injected_hotkey_presses()
    {
        TestEnv.WindowsOnly("Installs a Win32 low-level hook on a hook thread and injects input with SendInput.");
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 (injects F24 system-wide).");
        Assert.Equal(40, Marshal.SizeOf<KeyInput>());

        var events = new BlockingCollection<HookEvent>();
        using var hook = new InputHook(new HotkeyMatcher(HotkeyParser.Parse("F24"), suppress: true), events.Add, NullLog.Instance);
        Assert.True(hook.Start(), "SetWindowsHookEx failed");

        const ushort f24 = 0x87;
        var inputs = new[]
        {
            new KeyInput { Type = 1, Vk = f24 },
            new KeyInput { Type = 1, Vk = f24 },               // auto-repeat style second down
            new KeyInput { Type = 1, Vk = f24, Flags = 0x2 },  // key up
        };
        var sent = SendInput(3, inputs, Marshal.SizeOf<KeyInput>());
        // Windows refuses injection while the session has no input desktop (locked, or a minimised RDP client).
        Assert.SkipWhen(sent == 0, $"SendInput injected nothing (Win32 error {Marshal.GetLastWin32Error()}): no interactive input desktop right now.");
        Assert.Equal(3u, sent);

        Assert.True(events.TryTake(out var down, TimeSpan.FromSeconds(3)), "no hotkey-down event from the hook thread");
        Assert.True(events.TryTake(out var up, TimeSpan.FromSeconds(3)), "no hotkey-up event from the hook thread");
        Assert.Equal(HotkeySignal.Down, down.Signal);
        Assert.Equal(HotkeySignal.Up, up.Signal);
        Assert.False(events.TryTake(out var extra, TimeSpan.FromMilliseconds(300)), $"unexpected extra event {extra}"); // repeat produced nothing
        output.WriteLine($"down→up {up.TimestampMs - down.TimestampMs} ms apart");

        hook.Enabled = false;
        SendInput(3, inputs, Marshal.SizeOf<KeyInput>());
        Assert.False(events.TryTake(out _, TimeSpan.FromMilliseconds(500)), "disabled hook still produced events");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint mem);

    /// <summary>Reads each privacy format straight from the Win32 clipboard: null if absent, else its first DWORD.</summary>
    private static Dictionary<string, uint?> ReadPrivacyFormats()
    {
        var result = new Dictionary<string, uint?>();
        Assert.True(OpenClipboard(0));
        try
        {
            foreach (var name in Win32Clipboard.PrivacyFormats)
            {
                var id = RegisterClipboardFormat(name);
                if (!IsClipboardFormatAvailable(id)) { result[name] = null; continue; }
                var handle = GetClipboardData(id);
                var ptr = GlobalLock(handle);
                try
                {
                    Assert.True(GlobalSize(handle) >= 4);
                    result[name] = (uint)Marshal.ReadInt32(ptr);
                }
                finally { GlobalUnlock(handle); }
            }
        }
        finally { CloseClipboard(); }
        return result;
    }

    /// <summary>CF_UNICODETEXT straight from the Win32 clipboard.</summary>
    private static string? ReadText()
    {
        Assert.True(OpenClipboard(0));
        try
        {
            var handle = GetClipboardData(13);
            if (handle == 0) return null;
            var ptr = GlobalLock(handle);
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    [Fact]
    public void Clipboard_transcript_carries_the_history_and_cloud_opt_out_formats()
    {
        Assert.SkipUnless(TestEnv.Get("UI_TESTS") == "1", "Set DICTUM_UI_TESTS=1 (uses the real clipboard, restores its text after).");
        using var clipboard = new Win32Clipboard();
        var previous = clipboard.Snapshot();
        try
        {
            clipboard.SetText("dictate privacy test", keepOutOfHistory: true);
                var marked = ReadPrivacyFormats();
                output.WriteLine("keepOutOfHistory=true: " + string.Join(", ", marked.Select(kv => $"{kv.Key}={kv.Value?.ToString() ?? "absent"}")));
                Assert.All(marked.Values, v => Assert.Equal(0u, v));
                Assert.Equal("dictate privacy test", ReadText());

                clipboard.SetText("dictate privacy test", keepOutOfHistory: false);
                var unmarked = ReadPrivacyFormats();
                output.WriteLine("keepOutOfHistory=false: " + string.Join(", ", unmarked.Select(kv => $"{kv.Key}={kv.Value?.ToString() ?? "absent"}")));
                Assert.All(unmarked.Values, Assert.Null);
        }
        finally
        {
            clipboard.Restore(previous);
        }
    }

    [Fact]
    public void Microphone_capture_starts_and_returns_16kHz_audio()
    {
        Assert.SkipUnless(TestEnv.Get("AUDIO_TESTS") == "1", "Set DICTUM_AUDIO_TESTS=1 (opens the default microphone for one second; audio stays in memory).");

        using var capture = new WasapiCapture(NullLog.Instance);
        var started = Environment.TickCount64;
        capture.Start(null, started);
        var startMs = Environment.TickCount64 - started;
        Thread.Sleep(1000);
        var audio = capture.Stop();
        output.WriteLine($"start {startMs} ms, captured {audio.Duration.TotalMilliseconds:0} ms, peak block RMS {audio.PeakLevel:0.0000}");
        Assert.InRange(audio.Duration.TotalMilliseconds, 700, 1300);
        Assert.Equal(0f, capture.CurrentLevel);
    }
}
