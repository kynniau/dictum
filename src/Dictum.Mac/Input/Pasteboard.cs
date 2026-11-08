using System.Runtime.InteropServices;
using System.Text;
using Dictum.Insert;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Input;

/// <summary>
/// The general pasteboard (NSPasteboard) through Objective-C messaging. A snapshot keeps every item on the pasteboard and,
/// for each item, the raw bytes of every type it lists, so images, files (file URLs) and rich text come back after a
/// dictation, and a pasteboard holding several items is restored as several items. If any listed type cannot be read
/// (<c>dataForType:</c> returns nil, as a lazy provider that declines does), the snapshot is marked incomplete and restore
/// leaves the pasteboard as it is: the dictation stays on it, never a partial copy of the user's contents, except that a
/// one-item pasteboard whose text types could be read gets that text back as one item (the unreadable types skipped,
/// with a warning). Leaving the pasteboard as it is also holds
/// when an item would come back with no type the pasteboard accepts. An empty snapshot (nothing was on the pasteboard
/// before) restores an empty pasteboard. A restore is written current-host-only when any snapshot item carries
/// org.nspasteboard.ConcealedType (a password manager's concealed copy, or a dictation written with history kept off),
/// otherwise with no options. Every call runs inside its
/// own autorelease pool, so it is safe off the UI thread too; the shell calls it on the UI thread.
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

    /// <summary>The text types Dictum names itself (the Windows clipboard keeps the same three); a snapshot keeps every type, not only these.</summary>
    internal static readonly string[] TextTypes = [PlainText, Rtf, Html];

    /// <summary>
    /// The snapshot key (value <c>true</c>) saying a listed type could not be read, so the snapshot is not a full copy and
    /// <see cref="Restore"/> leaves the pasteboard as it is. Its value is <c>true</c>, never bytes, so <see cref="Items"/> skips
    /// it. It is a reverse-DNS name under Dictum's own bundle id; an app writing that exact type on item 0 would only make
    /// the restore leave the pasteboard as it is (the safe direction), never write a wrong item.
    /// </summary>
    internal const string IncompleteMarker = "au.kynni.dictum.snapshot-incomplete";

    /// <summary>Beside the <see cref="IncompleteMarker"/>: how many items the pasteboard held (an int, so <see cref="Items"/> skips it).</summary>
    internal const string ItemCountKey = "au.kynni.dictum.snapshot-items";

    /// <summary>Beside the <see cref="IncompleteMarker"/> of a one-item snapshot: how many listed types were not kept (an int).</summary>
    internal const string SkippedKey = "au.kynni.dictum.snapshot-skipped";

    private readonly ILog _log;

    public Pasteboard() : this(null)
    {
    }

    /// <param name="log">Where a skipped or partial restore is reported (counts only, never contents).</param>
    public Pasteboard(ILog? log) => _log = log ?? NullLog.Instance;

    /// <summary>
    /// The snapshot key for <paramref name="type"/> on item <paramref name="item"/>: the bare type name for item 0 (so a
    /// snapshot of one item reads as it always has), "N/type" for item N of 1 or more. A UTI never contains '/'.
    /// </summary>
    internal static string Key(int item, string type) => item == 0 ? type : $"{item}/{type}";

    /// <summary>The inverse of <see cref="Key"/>: a key with no "N/" prefix (N a whole number of 1 or more) is a type of item 0.</summary>
    internal static (int Item, string Type) ParseKey(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && int.TryParse(key.AsSpan(0, slash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var item) && item >= 1)
            return (item, key[(slash + 1)..]);
        return (0, key);
    }

    /// <summary>
    /// Every item and every type on the general pasteboard, as raw bytes (see <see cref="Key"/>). Reading stops at the first
    /// type that cannot be read (<c>dataForType:</c> returns nil, a nil type name, a name with '/' that cannot be a UTI, or
    /// data longer than a managed array holds) and the snapshot carries the <see cref="IncompleteMarker"/> and the item count
    /// (<see cref="ItemCountKey"/>). When the pasteboard held exactly one item, that item's readable text types (plain, RTF,
    /// HTML) are kept too, with how many listed types were not (<see cref="SkippedKey"/>), so <see cref="Restore"/> can put
    /// the text back; with more items nothing else is kept. One Info line gives the counts of an incomplete snapshot, and
    /// every snapshot logs its time and counts at Debug (never type names or contents).
    /// </summary>
    public ClipboardSnapshot Snapshot()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = Read(out var itemCount, out var typeCount);
        _log.Debug(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Clipboard snapshot: {clock.Elapsed.TotalMilliseconds:0.00} ms, {itemCount} item(s), {typeCount} type(s){(snapshot.Formats.ContainsKey(IncompleteMarker) ? ", incomplete" : "")}."));
        return snapshot;
    }

    private ClipboardSnapshot Read(out int itemCount, out int typeCount)
    {
        var formats = new Dictionary<string, object>();
        itemCount = 0;
        typeCount = 0;
        using (ObjC.AutoreleasePool.Begin())
        {
            // simplification: every type of every item is copied into managed memory, so a very large pasteboard (a big
            // image, a long video copied as data) is held twice for the length of one dictation. Retire by keeping the
            // NSData objects retained instead of copying, if a user reports memory pressure or a slow paste with large copies.
            var items = ObjC.Send(General(), ObjC.Sel("pasteboardItems"));
            if (items == 0) return Incomplete("the pasteboard items could not be retrieved", 0, 0);
            itemCount = (int)ObjC.Send(items, ObjC.Sel("count"));
            for (var i = 0; i < itemCount; i++)
            {
                var item = ObjC.Send(items, ObjC.Sel("objectAtIndex:"), i);
                var types = item == 0 ? 0 : ObjC.Send(item, ObjC.Sel("types"));
                if (types == 0) return Incomplete("an item's types could not be read", itemCount, 0);
                var count = (int)ObjC.Send(types, ObjC.Sel("count"));
                typeCount += count;
                for (var t = 0; t < count; t++)
                {
                    var typeName = ObjC.Send(types, ObjC.Sel("objectAtIndex:"), t);
                    var type = ObjC.FromNSString(typeName);
                    // A UTI never contains '/', and the "N/type" key scheme relies on that, so such a name is not stored.
                    if (type is null || type.Contains('/', StringComparison.Ordinal)) return Incomplete("1 type name could not be used", itemCount, itemCount == 1 ? item : 0, count);
                    var data = ObjC.Send(item, ObjC.Sel("dataForType:"), typeName);
                    if (data == 0 || TooLong(data)) return Incomplete("1 type could not be read", itemCount, itemCount == 1 ? item : 0, count);
                    formats[Key(i, type)] = Bytes(data);
                }
            }
        }
        return new ClipboardSnapshot(formats);
    }

    private static bool TooLong(nint data) => (nuint)ObjC.Send(data, ObjC.Sel("length")) > int.MaxValue;

    /// <summary>
    /// An incomplete snapshot: the marker and the item count, plus, for the only item of a one-item pasteboard
    /// (<paramref name="onlyItem"/> not 0), its readable text types and how many of its <paramref name="listedTypes"/> were
    /// not kept. <see cref="ConcealedType"/> is kept with the text when the item carries it, so <see cref="RestoreOptions"/>
    /// still writes a concealed copy current-host-only. Logged at Info with counts only.
    /// </summary>
    private ClipboardSnapshot Incomplete(string what, int itemCount, nint onlyItem, int listedTypes = 0)
    {
        var formats = new Dictionary<string, object> { [IncompleteMarker] = true, [ItemCountKey] = itemCount };
        if (onlyItem != 0)
        {
            foreach (var type in (string[])[.. TextTypes, ConcealedType])
            {
                var data = ObjC.Send(onlyItem, ObjC.Sel("dataForType:"), ObjC.NSString(type));
                if (data != 0 && !TooLong(data)) formats[type] = Bytes(data);
            }
            formats[SkippedKey] = listedTypes - (formats.Count - 2);
        }
        _log.Info($"Clipboard snapshot incomplete: {what} (reading stopped there; {itemCount} item(s) on the pasteboard, {formats.Count(f => f.Value is byte[])} type(s) kept).");
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

    /// <summary>
    /// Puts the snapshot back. An incomplete snapshot leaves the pasteboard as it is (the dictation stays on it, never a
    /// partial copy), except one case: when the pasteboard held exactly one item and that item's text (plain, RTF or HTML)
    /// was read, that text is written back as one item and one Warn says how many promised types were skipped. An empty
    /// snapshot (nothing was on the pasteboard before) clears it, since empty is the exact prior
    /// state. Otherwise every item is written back; a type an item refuses is left out of that item, but if any item would
    /// lose every type (so fewer items would come back) nothing is written and the pasteboard is left as it is. The options
    /// come from <see cref="RestoreOptions"/>: current-host-only when any item carries <see cref="ConcealedType"/>, else 0.
    /// </summary>
    public void Restore(ClipboardSnapshot snapshot)
    {
        var items = Items(snapshot);
        if (snapshot.Formats.ContainsKey(IncompleteMarker))
        {
            // The one fallback: a one-item pasteboard whose text types were read keeps its text; never fewer items, never a
            // partial set of anything but that one item's text.
            if (snapshot.Formats.TryGetValue(ItemCountKey, out var count) && count is 1 && items.Count == 1 && items[0].Any(t => TextTypes.Contains(t.Type)))
            {
                var text = WriteItems(items, RestoreOptions(items));
                if (text.DroppedItems == 0)
                {
                    var skipped = snapshot.Formats.TryGetValue(SkippedKey, out var n) && n is int k ? k : 0;
                    _log.Warn($"Clipboard restored as text only: {skipped} promised type(s) on the previous clipboard could not be read and were skipped.");
                    return;
                }
            }
            _log.Warn("Clipboard not restored: a type on the previous clipboard could not be read, so the pasteboard was left as it was (the transcript is on it).");
            return;
        }
        if (items.Count == 0)
        {
            Clear();
            return;
        }
        var result = WriteItems(items, RestoreOptions(items));
        if (result.DroppedItems > 0)
            _log.Warn($"Clipboard not restored: {result.DroppedItems} item(s) had every type refused, so the pasteboard was left as it was (the transcript is on it).");
        else if (result.RefusedTypes > 0)
            _log.Warn($"Clipboard restored without {result.RefusedTypes} type(s) the pasteboard refused.");
    }

    /// <summary>
    /// The <c>prepareForNewContentsWithOptions:</c> options a restore writes with: <see cref="CurrentHostOnly"/> when any item
    /// carries <see cref="ConcealedType"/> (a password manager's concealed copy, or Dictum's own dictation written with
    /// history kept off, which always carries it), otherwise 0, so the user's own contents are offered to their other devices
    /// again as they were. The item's own types are the signal because NSPasteboard has no getter for the options the
    /// original contents were written with (Recalled, not checked against the SDK). A history-off dictation therefore stays
    /// on this Mac even if history is allowed later: the safe direction.
    /// </summary>
    internal static nuint RestoreOptions(IReadOnlyList<IReadOnlyList<(string Type, byte[] Bytes)>> items) =>
        items.Any(item => item.Any(t => t.Type == ConcealedType)) ? CurrentHostOnly : 0;

    /// <summary>
    /// The items a snapshot holds, in item order, each with its types and bytes (see <see cref="Key"/>); entries that are
    /// not bytes (the <see cref="IncompleteMarker"/>) are left out. A snapshot keyed by bare type names only is one item.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<(string Type, byte[] Bytes)>> Items(ClipboardSnapshot snapshot) =>
        [.. snapshot.Formats
            .Where(f => f.Value is byte[])
            .Select(f => (Key: ParseKey(f.Key), Bytes: (byte[])f.Value))
            .GroupBy(f => f.Key.Item)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<(string Type, byte[] Bytes)>)[.. g.Select(f => (f.Key.Type, f.Bytes))])];

    /// <summary>
    /// The only clearContents in this file, and the only restore that removes anything: the pasteboard was empty before the
    /// dictation, so empty is the exact prior state, and leaving the transcript there would keep it on the clipboard after
    /// the user's own (empty) contents should have come back.
    /// </summary>
    private static void Clear()
    {
        using (ObjC.AutoreleasePool.Begin()) ObjC.Send(General(), ObjC.Sel("clearContents"));
    }

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
    /// Writes one item holding every type given, replacing the pasteboard; a refused type is left out. Throws when every type
    /// was refused, before touching the pasteboard, so the transcript is never pasted from a pasteboard it is not on.
    /// </summary>
    private static void Write(IReadOnlyList<(string Type, byte[] Bytes)> item, nuint options)
    {
        if (WriteItems([item], options).DroppedItems > 0)
            throw new ExternalException($"NSPasteboardItem refused every type ({string.Join(", ", item.Select(t => t.Type))}); the pasteboard was left as it was.");
    }

    /// <summary>
    /// Writes one NSPasteboardItem per entry, each holding the types given, replacing the pasteboard. Every item is built
    /// before the pasteboard is prepared. A type an item refuses is left out of that item (the rest still goes), but if any
    /// item refuses every type, nothing is written and the pasteboard is not touched (<c>DroppedItems</c> &gt; 0), so a
    /// refusal can never leave the user with an empty pasteboard or with fewer items than they had. Throws only when
    /// <c>writeObjects:</c> fails. <paramref name="options"/> are NSPasteboardContentsOptions; they persist until the next
    /// prepare or clearContents (NSPasteboard.h), so every write states its own, and 0 is the plain clearContents.
    /// </summary>
    private static unsafe (int RefusedTypes, int DroppedItems) WriteItems(IReadOnlyList<IReadOnlyList<(string Type, byte[] Bytes)>> items, nuint options)
    {
        using (ObjC.AutoreleasePool.Begin())
        {
            var board = General();
            var built = new List<nint>();
            try
            {
                var refused = 0;
                var dropped = 0;
                foreach (var types in items)
                {
                    var item = ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboardItem"), ObjC.Sel("alloc")), ObjC.Sel("init"));
                    built.Add(item);
                    var accepted = 0;
                    foreach (var (type, bytes) in types)
                    {
                        // BOOL results read from the low byte: objc_msgSend's declared nint return carries the BOOL in its low 8 bits.
                        if ((byte)ObjC.Send(item, ObjC.Sel("setData:forType:"), Data(bytes), ObjC.NSString(type)) == 0) refused++;
                        else accepted++;
                    }
                    if (accepted == 0) dropped++;
                }
                if (dropped > 0) return (refused, dropped);
                var objects = built.ToArray();
                nint array;
                fixed (nint* p = objects) array = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObjects:count:"), (nint)p, objects.Length);
                ObjC.Send(board, ObjC.Sel("prepareForNewContentsWithOptions:"), (nint)options);
                if ((byte)ObjC.Send(board, ObjC.Sel("writeObjects:"), array) == 0) throw new ExternalException("NSPasteboard writeObjects: failed.");
                return (refused, 0);
            }
            finally
            {
                foreach (var item in built) ObjC.Release(item);
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
