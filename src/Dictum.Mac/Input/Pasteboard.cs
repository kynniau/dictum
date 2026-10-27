using System.Runtime.InteropServices;
using System.Text;
using Dictum.Insert;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Input;

/// <summary>
/// The general pasteboard (NSPasteboard) through Objective-C messaging. Snapshots keep the raw bytes of the text types
/// only (plain, RTF, HTML), as the Windows clipboard keeps its text formats; images and
/// files are not restored (SPEC §6), and a pasteboard holding several items is restored as one item of those types. Every
/// call runs inside its own autorelease pool, so it is safe off the UI thread too; the shell calls it on the UI thread.
/// </summary>
internal sealed class Pasteboard : IClipboard, IDisposable
{
    // The type names, as AppKit's exported constants hold them on this Mac (NSPasteboardTypeString, NSPasteboardTypeRTF,
    // NSPasteboardTypeHTML, read from AppKit 22/10/2026). The deprecated NSStringPboardType is not kept: reading it returned
    // data whenever plain text was present, and writing that back was refused (setData:forType: returned NO, macOS 27.2,
    // 23/10/2026), which with the earlier write order left the pasteboard empty.
    internal const string PlainText = "public.utf8-plain-text";
    internal const string Rtf = "public.rtf";
    internal const string Html = "public.html";

    /// <summary>
    /// The nspasteboard.org convention clipboard history apps read (Verified 22/10/2026 at nspasteboard.org): TransientType
    /// marks an item that is restored within seconds, ConcealedType a confidential one; history apps skip both. The source
    /// type names the app that wrote the item.
    /// </summary>
    internal const string ConcealedType = "org.nspasteboard.ConcealedType";
    internal const string TransientType = "org.nspasteboard.TransientType";
    internal const string SourceType = "org.nspasteboard.source";
    internal const string BundleId = "au.kynni.dictum";

    /// <summary>The types a snapshot keeps.</summary>
    internal static readonly string[] TextTypes = [PlainText, Rtf, Html];

    public ClipboardSnapshot Snapshot()
    {
        var formats = new Dictionary<string, object>();
        using (ObjC.AutoreleasePool.Begin())
        {
            var board = General();
            foreach (var type in TextTypes)
            {
                var data = ObjC.Send(board, ObjC.Sel("dataForType:"), ObjC.NSString(type));
                if (data != 0) formats[type] = Bytes(data);
            }
        }
        return new ClipboardSnapshot(formats);
    }

    /// <summary>
    /// NSPasteboardContentsCurrentHostOnly (NSPasteboard.h in the macOS SDK, read 23/10/2026; macOS 10.12 or later): the
    /// pasteboard contents are not made available to the user's other devices, so Handoff's Universal Clipboard cannot
    /// carry a dictation off this Mac.
    /// </summary>
    internal const nuint CurrentHostOnly = 1;

    /// <summary>
    /// The <c>prepareForNewContentsWithOptions:</c> options a dictation is written with: current-host-only unless clipboard
    /// history is allowed, the same switch that lets Windows upload a dictation to its cloud clipboard (CanUploadToCloudClipboard).
    /// </summary>
    internal static nuint ContentsOptions(bool keepOutOfHistory) => keepOutOfHistory ? CurrentHostOnly : 0;

    public void SetText(string text, bool keepOutOfHistory) => Write(Payload(text, keepOutOfHistory), ContentsOptions(keepOutOfHistory));

    /// <summary>Restores with no options: the user's own contents are offered to their other devices again, as they were before the dictation.</summary>
    public void Restore(ClipboardSnapshot snapshot) =>
        Write([.. snapshot.Formats.Where(f => f.Value is byte[]).Select(f => (f.Key, (byte[])f.Value))], options: 0);

    /// <summary>What <see cref="SetText"/> writes as one item: the text as UTF-8 plain text, and unless history is allowed the two opt-out types (empty) and the source type naming Dictum.</summary>
    internal static IReadOnlyList<(string Type, byte[] Bytes)> Payload(string text, bool keepOutOfHistory)
    {
        var list = new List<(string, byte[])> { (PlainText, Encoding.UTF8.GetBytes(text)) };
        if (keepOutOfHistory)
        {
            list.Add((ConcealedType, []));
            list.Add((TransientType, []));
            list.Add((SourceType, Encoding.UTF8.GetBytes(BundleId)));
        }
        return list;
    }

    /// <summary>
    /// Writes one item holding every type given, replacing the pasteboard; an empty list leaves it empty (an empty snapshot
    /// restores an empty pasteboard). The item is built before the pasteboard is cleared, and a type the item refuses is
    /// left out rather than failing the rest, so a refused type can never leave the user with an empty pasteboard. Throws
    /// only when nothing could be written. <paramref name="options"/> are NSPasteboardContentsOptions; they persist until the
    /// next prepare or clearContents (NSPasteboard.h), so every write states its own, and 0 is the plain clearContents.
    /// </summary>
    private static void Write(IReadOnlyList<(string Type, byte[] Bytes)> items, nuint options)
    {
        using (ObjC.AutoreleasePool.Begin())
        {
            var board = General();
            if (items.Count == 0)
            {
                ObjC.Send(board, ObjC.Sel("clearContents"));
                return;
            }
            var item = ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboardItem"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            try
            {
                var refused = new List<string>();
                foreach (var (type, bytes) in items)
                {
                    // BOOL results read from the low byte: objc_msgSend's declared nint return carries the BOOL in its low 8 bits.
                    if ((byte)ObjC.Send(item, ObjC.Sel("setData:forType:"), Data(bytes), ObjC.NSString(type)) == 0) refused.Add(type);
                }
                if (refused.Count == items.Count) throw new ExternalException($"NSPasteboardItem refused every type ({string.Join(", ", refused)}); the pasteboard was left as it was.");
                ObjC.Send(board, ObjC.Sel("prepareForNewContentsWithOptions:"), (nint)options);
                var array = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObject:"), item);
                if ((byte)ObjC.Send(board, ObjC.Sel("writeObjects:"), array) == 0) throw new ExternalException("NSPasteboard writeObjects: failed.");
            }
            finally
            {
                ObjC.Release(item);
            }
        }
    }

    private static nint General() => ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"));

    /// <summary>An autoreleased NSData copy of <paramref name="bytes"/> (<c>dataWithBytes:length:</c> copies).</summary>
    private static unsafe nint Data(byte[] bytes)
    {
        fixed (byte* p = bytes) return ObjC.Send(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), (nint)p, bytes.Length);
    }

    private static byte[] Bytes(nint data)
    {
        var length = (int)ObjC.Send(data, ObjC.Sel("length"));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(ObjC.Send(data, ObjC.Sel("bytes")), bytes, 0, length);
        return bytes;
    }

    /// <summary>Nothing to free: the general pasteboard is a shared singleton. Kept so the platform can dispose its clipboard on every OS alike.</summary>
    public void Dispose()
    {
    }
}
