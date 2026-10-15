using System.Runtime.InteropServices;
using System.Text;
using Dictum.Insert;
using Dictum.Windows.Interop;
using Dictum.Windows.Shell;

namespace Dictum.Windows.Input;

/// <summary>
/// The Win32 clipboard, used directly (no WinForms, no OLE). A hidden message-only window owns it: OpenClipboard with no
/// owner makes SetClipboardData fail (Microsoft Learn, OpenClipboard remarks, read 16/10/2026). Opening is retried 10
/// times, 50 ms apart, while another app holds it open. Snapshots keep the raw bytes of the text formats only (plain,
/// Unicode, RTF, HTML, CSV); images and files are not restored (SPEC §6). Use on the UI thread.
/// </summary>
public sealed class Win32Clipboard : IClipboard, IDisposable
{
    private const int Retries = 10;
    private const int RetryDelayMs = 50;

    /// <summary>
    /// Registered formats Windows reads to decide on clipboard history and cloud sync: a DWORD 0 in each keeps the item out
    /// of Win+V history and off the user's other devices. https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats
    /// (Cloud Clipboard and Clipboard History Formats; page updated 15/04/2025, read 15/10/2026).
    /// </summary>
    public static readonly string[] PrivacyFormats = ["CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];

    /// <summary>The text formats a snapshot keeps (the registered names WinForms used for RTF, HTML and CSV).</summary>
    private static readonly string[] TextFormats = ["CF_UNICODETEXT", "CF_TEXT", "Rich Text Format", "HTML Format", "Csv"];

    private HiddenWindow? _owner;

    public ClipboardSnapshot Snapshot()
    {
        var formats = new Dictionary<string, object>();
        using (Open())
        {
            foreach (var name in TextFormats)
            {
                var id = FormatId(name);
                if (!NativeClipboard.IsClipboardFormatAvailable(id)) continue;
                var handle = NativeClipboard.GetClipboardData(id);
                if (handle != 0 && ReadGlobal(handle) is { } bytes) formats[name] = bytes;
            }
        }
        return new ClipboardSnapshot(formats);
    }

    public void SetText(string text, bool keepOutOfHistory) => Write(Payload(text, keepOutOfHistory));

    public void Restore(ClipboardSnapshot snapshot) =>
        Write([.. snapshot.Formats.Where(f => f.Value is byte[]).Select(f => (f.Key, (byte[])f.Value))]);

    /// <summary>What <see cref="SetText"/> puts on the clipboard: the text, and the two privacy DWORDs unless history is allowed.</summary>
    internal static IReadOnlyList<(string Format, byte[] Bytes)> Payload(string text, bool keepOutOfHistory)
    {
        var list = new List<(string, byte[])> { ("CF_UNICODETEXT", Encoding.Unicode.GetBytes(text + "\0")) };
        if (keepOutOfHistory) list.AddRange(PrivacyFormats.Select(f => (f, BitConverter.GetBytes(0u))));
        return list;
    }

    private void Write(IReadOnlyList<(string Format, byte[] Bytes)> items)
    {
        using (Open())
        {
            if (!NativeClipboard.EmptyClipboard()) throw new ExternalException($"EmptyClipboard failed (Win32 error {Marshal.GetLastWin32Error()}).");
            foreach (var (format, bytes) in items)
            {
                var memory = AllocGlobal(bytes);
                if (NativeClipboard.SetClipboardData(FormatId(format), memory) == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    NativeClipboard.GlobalFree(memory); // still ours: the system takes ownership only on success
                    throw new ExternalException($"SetClipboardData({format}) failed (Win32 error {error}).");
                }
            }
        }
    }

    private static uint FormatId(string name) => name switch
    {
        "CF_UNICODETEXT" => NativeClipboard.CF_UNICODETEXT,
        "CF_TEXT" => NativeClipboard.CF_TEXT,
        _ => NativeClipboard.RegisterClipboardFormat(name),
    };

    /// <summary>Opens the clipboard, retrying while another app holds it; disposing closes it.</summary>
    private IDisposable Open()
    {
        _owner ??= new HiddenWindow("Clipboard", messageOnly: true);
        for (var attempt = 1; ; attempt++)
        {
            if (NativeClipboard.OpenClipboard(_owner.Handle)) return new Closer();
            if (attempt >= Retries) throw new ExternalException($"The clipboard is held by another app (OpenClipboard failed {Retries} times, Win32 error {Marshal.GetLastWin32Error()}).");
            Thread.Sleep(RetryDelayMs);
        }
    }

    private sealed class Closer : IDisposable
    {
        public void Dispose() => NativeClipboard.CloseClipboard();
    }

    private static byte[]? ReadGlobal(nint handle)
    {
        var size = (int)NativeClipboard.GlobalSize(handle);
        var pointer = NativeClipboard.GlobalLock(handle);
        if (pointer == 0) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeClipboard.GlobalUnlock(handle);
        }
    }

    private static nint AllocGlobal(byte[] bytes)
    {
        var memory = NativeClipboard.GlobalAlloc(NativeClipboard.GMEM_MOVEABLE, (nuint)Math.Max(bytes.Length, 1));
        if (memory == 0) throw new OutOfMemoryException("GlobalAlloc failed for the clipboard.");
        var pointer = NativeClipboard.GlobalLock(memory);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        NativeClipboard.GlobalUnlock(memory);
        return memory;
    }

    public void Dispose() => _owner?.Dispose();
}
