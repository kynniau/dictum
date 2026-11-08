using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Dictum.Insert;
using Dictum.Mac.Input;
using Dictum.Mac.Interop;

namespace Dictum.Tests;

/// <summary>
/// The macOS pasteboard snapshot and restore (src/Dictum.Mac/Input/Pasteboard.cs): every item and every type comes back
/// after a dictation, so a copied image or file is not lost. The key scheme, the item grouping and the incomplete-marker
/// path are pure and run on every OS; the round trips use the real general pasteboard and run only on a Mac with
/// DICTUM_UI_TESTS=1, restoring the pasteboard they found (through the code under test) in finally.
/// </summary>
public class MacPasteboardRestoreTests
{
    private const string Png = "public.png";

    // ---- Pure: no macOS calls -------------------------------------------------------------------------------------

    [Fact]
    public void MacPasteboard_key_round_trips_and_item_zero_keeps_the_bare_type_name()
    {
        Assert.Equal(Pasteboard.PlainText, Pasteboard.Key(0, Pasteboard.PlainText));
        Assert.Equal("1/public.png", Pasteboard.Key(1, Png));
        Assert.Equal("12/dyn.ah62d4rv4gu8y", Pasteboard.Key(12, "dyn.ah62d4rv4gu8y"));
        foreach (var (item, type) in new[] { (0, Pasteboard.PlainText), (1, Png), (7, "public.file-url"), (12, "dyn.ah62d4rv4gu8y") })
            Assert.Equal((item, type), Pasteboard.ParseKey(Pasteboard.Key(item, type)));

        // A bare type name (the shape the earlier snapshots and MacInputTests build by hand) is item 0; so is anything
        // whose prefix is not a whole number of 1 or more.
        Assert.Equal((0, Pasteboard.Html), Pasteboard.ParseKey(Pasteboard.Html));
        Assert.Equal((0, "0/public.png"), Pasteboard.ParseKey("0/public.png"));
        Assert.Equal((0, "x/public.png"), Pasteboard.ParseKey("x/public.png"));
        Assert.Equal((0, "-1/public.png"), Pasteboard.ParseKey("-1/public.png"));
    }

    [Fact]
    public void MacPasteboard_items_group_by_item_in_order_and_skip_the_marker()
    {
        var snapshot = new ClipboardSnapshot(new Dictionary<string, object>
        {
            ["2/public.file-url"] = Encoding.UTF8.GetBytes("file:///b"),
            [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("zero"),
            ["1/public.file-url"] = Encoding.UTF8.GetBytes("file:///a"),
            [Png] = new byte[] { 1, 2, 3 },
        });
        var items = Pasteboard.Items(snapshot);
        Assert.Equal(3, items.Count);
        Assert.Equal([Png, Pasteboard.PlainText], items[0].Select(t => t.Type).Order(StringComparer.Ordinal));
        Assert.Equal("file:///a", Encoding.UTF8.GetString(Assert.Single(items[1]).Bytes));
        Assert.Equal("file:///b", Encoding.UTF8.GetString(Assert.Single(items[2]).Bytes));

        Assert.Empty(Pasteboard.Items(new ClipboardSnapshot(new Dictionary<string, object> { [Pasteboard.IncompleteMarker] = true })));
        Assert.Empty(Pasteboard.Items(ClipboardSnapshot.Empty));
    }

    /// <summary>The marker path returns before any Objective-C call, so this runs on Windows too: one Warn line, no contents.</summary>
    [Fact]
    public void MacPasteboard_incomplete_snapshot_is_not_restored_and_logs_one_warning_without_contents()
    {
        var log = new RecordingLog();
        new Pasteboard(log).Restore(new ClipboardSnapshot(new Dictionary<string, object>
        {
            [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("secret clipboard words"),
            [Pasteboard.IncompleteMarker] = true,
        }));
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.StartsWith("Clipboard not restored", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", log.All, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restore is written current-host-only when any item carries org.nspasteboard.ConcealedType (a password manager's
    /// concealed copy, or a dictation written with history kept off), so Universal Clipboard cannot carry it off this Mac on
    /// the way back; anything else goes back with no options, as the user's own contents were.
    /// </summary>
    [Fact]
    public void MacPasteboard_restore_options_keep_a_concealed_item_on_this_host_only()
    {
        static IReadOnlyList<IReadOnlyList<(string Type, byte[] Bytes)>> Items(Dictionary<string, object> formats) => Pasteboard.Items(new ClipboardSnapshot(formats));

        var concealed = Items(new()
        {
            [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("user text"),
            ["1/" + Pasteboard.PlainText] = Encoding.UTF8.GetBytes("a concealed copy"),
            ["1/" + Pasteboard.ConcealedType] = Array.Empty<byte>(),
        });
        var dictationWithHistoryAllowed = Items(new()
        {
            [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("earlier dictation"),
            [Pasteboard.SourceType] = Encoding.UTF8.GetBytes(Pasteboard.BundleId),
        });
        var plain = Items(new()
        {
            [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("user text"),
            ["1/" + Png] = new byte[] { 1, 2, 3 },
        });
        Assert.Equal(Pasteboard.CurrentHostOnly, Pasteboard.RestoreOptions(concealed));
        Assert.Equal((nuint)0, Pasteboard.RestoreOptions(dictationWithHistoryAllowed));
        Assert.Equal((nuint)0, Pasteboard.RestoreOptions(plain));
        Assert.Equal((nuint)0, Pasteboard.RestoreOptions([]));
    }

    /// <summary>
    /// RestoreOptions is pinned above, but nothing on Windows runs Restore itself; this pins the source so the options
    /// reach the restore write.
    /// </summary>
    [Fact]
    public void MacPasteboard_source_applies_the_restore_options_on_every_restore_write()
    {
        var source = File.ReadAllText(Path.Combine(FixBatchDownloadTests.RepoRoot(), "src", "Dictum.Mac", "Input", "Pasteboard.cs"));
        var restore = source[source.IndexOf("public void Restore(ClipboardSnapshot snapshot)", StringComparison.Ordinal)..];
        restore = restore[..restore.IndexOf("internal static nuint RestoreOptions(", StringComparison.Ordinal)];
        Assert.Contains("WriteItems(items, RestoreOptions(items))", restore);
    }

    // ---- On a Mac: the real general pasteboard ---------------------------------------------------------------------

    private const string Gate = "Set DICTUM_UI_TESTS=1 on a Mac (uses the real pasteboard, restores what it found after).";

    [Fact]
    public void MacPasteboard_text_only_round_trips()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        RoundTrip([[(Pasteboard.PlainText, Encoding.UTF8.GetBytes("before ✓")), (Pasteboard.Html, Encoding.UTF8.GetBytes("<i>before</i>"))]]);
    }

    [Fact]
    public void MacPasteboard_image_only_round_trips_and_leaves_no_text()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var png = OnePixelPng();
        var after = RoundTrip([[(Png, png)]]);
        Assert.Equal(png, Assert.Single(after)[Png]);
        Assert.DoesNotContain(Pasteboard.PlainText, after[0].Keys);
    }

    [Fact]
    public void MacPasteboard_text_and_image_in_one_item_both_come_back()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var png = OnePixelPng();
        var after = RoundTrip([[(Pasteboard.PlainText, Encoding.UTF8.GetBytes("caption")), (Png, png)]]);
        Assert.Equal(png, Assert.Single(after)[Png]);
        Assert.Equal("caption", Encoding.UTF8.GetString(after[0][Pasteboard.PlainText]));
    }

    [Fact]
    public void MacPasteboard_several_items_are_restored_as_several_items()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var png = OnePixelPng();
        var after = RoundTrip(
        [
            [(Pasteboard.PlainText, Encoding.UTF8.GetBytes("first"))],
            [(Png, png)],
            [(Pasteboard.PlainText, Encoding.UTF8.GetBytes("third")), (Pasteboard.Rtf, Encoding.UTF8.GetBytes(@"{\rtf1 third}"))],
        ]);
        Assert.Equal(3, after.Count);
        Assert.Equal("first", Encoding.UTF8.GetString(after[0][Pasteboard.PlainText]));
        Assert.Equal(png, after[1][Png]);
        Assert.Equal("third", Encoding.UTF8.GetString(after[2][Pasteboard.PlainText]));
        Assert.Equal(@"{\rtf1 third}", Encoding.UTF8.GetString(after[2][Pasteboard.Rtf]));
    }

    /// <summary>An incomplete snapshot (hand-built here) leaves the pasteboard exactly as it was, change count included.</summary>
    [Fact]
    public void MacPasteboard_unreadable_type_leaves_the_pasteboard_exactly_as_it_was()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var log = new RecordingLog();
        WithRealPasteboard(log, board =>
        {
            board.SetText("Dictum transcript", keepOutOfHistory: true);
            var current = ReadRaw();
            var changeCount = ChangeCount();
            board.Restore(new ClipboardSnapshot(new Dictionary<string, object>
            {
                [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("would have been restored"),
                [Pasteboard.IncompleteMarker] = true,
            }));
            Assert.Equal(changeCount, ChangeCount());
            AssertSame(current, ReadRaw());
            Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
        });
    }

    /// <summary>
    /// The nil case for real: an item whose type is promised by a data provider that provides nothing, so
    /// <c>dataForType:</c> returns nil. The snapshot must carry the marker, never an empty type that would be written back.
    /// </summary>
    [Fact]
    public void MacPasteboard_a_type_whose_data_is_nil_marks_the_snapshot_incomplete()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var log = new RecordingLog();
        WithRealPasteboard(log, board =>
        {
            WithPromisedItem(plainText: null, () =>
            {
                var snapshot = board.Snapshot();
                Assert.True(snapshot.Formats.ContainsKey(Pasteboard.IncompleteMarker), "a type with nil data must mark the snapshot incomplete");
                Assert.DoesNotContain(PromisedType, snapshot.Formats.Keys);
                Assert.Empty(Pasteboard.Items(snapshot)); // no text on the item, so nothing to fall back to
                Assert.Single(log.Lines, l => l.Level == LogLevel.Info && l.Message.StartsWith("Clipboard snapshot incomplete", StringComparison.Ordinal));
            });
        });
    }

    /// <summary>
    /// The one fallback for an incomplete snapshot: a single item holding real plain text plus a promised type that gives
    /// nothing. The text comes back as one item, the promised type is skipped, and one Warn says how many were skipped.
    /// </summary>
    [Fact]
    public void MacPasteboard_single_text_item_with_a_declined_extra_type_restores_the_text_and_warns()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var log = new RecordingLog();
        WithRealPasteboard(log, board =>
        {
            WithPromisedItem(Encoding.UTF8.GetBytes("text beside a promise"), () =>
            {
                var snapshot = board.Snapshot();
                Assert.True(snapshot.Formats.ContainsKey(Pasteboard.IncompleteMarker));
                board.SetText("Dictum transcript", keepOutOfHistory: true);
                board.Restore(snapshot);
                var after = Assert.Single(ReadRaw());
                Assert.Equal("text beside a promise", Encoding.UTF8.GetString(after[Pasteboard.PlainText]));
                Assert.DoesNotContain(PromisedType, after.Keys);
                Assert.DoesNotContain(Pasteboard.ConcealedType, after.Keys);
                var warning = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
                Assert.Equal("Clipboard restored as text only: 1 promised type(s) on the previous clipboard could not be read and were skipped.", warning.Message);
            });
        });
    }

    /// <summary>
    /// A type a restored item refuses is left out of that item (MacInputTests pins that), but an item that would come back
    /// with no type at all is never silently dropped: nothing is written, the pasteboard keeps its change count and items.
    /// </summary>
    [Fact]
    public void MacPasteboard_an_item_with_every_type_refused_leaves_the_pasteboard_untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        var log = new RecordingLog();
        WithRealPasteboard(log, board =>
        {
            board.SetText("Dictum transcript", keepOutOfHistory: true);
            var current = ReadRaw();
            var changeCount = ChangeCount();
            board.Restore(new ClipboardSnapshot(new Dictionary<string, object>
            {
                [Pasteboard.PlainText] = Encoding.UTF8.GetBytes("item zero"),
                ["1/NSStringPboardType"] = Encoding.UTF8.GetBytes("not a UTI, refused"),
            }));
            Assert.Equal(changeCount, ChangeCount());
            Assert.Equal(current.Count, ReadRaw().Count);
            AssertSame(current, ReadRaw());
            var warning = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning);
            Assert.StartsWith("Clipboard not restored: 1 item(s)", warning.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>Nothing was on the pasteboard before the dictation: restore clears it, so the transcript does not stay.</summary>
    [Fact]
    public void MacPasteboard_empty_before_restores_an_empty_pasteboard()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && TestEnv.Get("UI_TESTS") == "1", Gate);
        WithRealPasteboard(null, board =>
        {
            WriteRaw([]);
            var before = board.Snapshot();
            Assert.Empty(before.Formats);
            board.SetText("Dictum transcript", keepOutOfHistory: true);
            Assert.NotEmpty(ReadRaw());
            board.Restore(before);
            Assert.Empty(ReadRaw());
        });
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private const string PromisedType = "au.kynni.dictum.test-promised";

    /// <summary>
    /// Replaces the pasteboard with one item whose <see cref="PromisedType"/> is promised by a provider that provides
    /// nothing (plus real plain text when <paramref name="plainText"/> is given), runs <paramref name="body"/>, then clears
    /// the pasteboard before the provider is released.
    /// </summary>
    private static void WithPromisedItem(byte[]? plainText, Action body)
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var pasteboard = ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"));
        var provider = ObjC.Send(ObjC.Send(EmptyProviderClass.Value, ObjC.Sel("alloc")), ObjC.Sel("init"));
        var item = ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboardItem"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        try
        {
            if (plainText is not null)
                Assert.NotEqual(0, (byte)ObjC.Send(item, ObjC.Sel("setString:forType:"), ObjC.NSString(Encoding.UTF8.GetString(plainText)), ObjC.NSString(Pasteboard.PlainText)));
            var types = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObject:"), ObjC.NSString(PromisedType));
            Assert.NotEqual(0, (byte)ObjC.Send(item, ObjC.Sel("setDataProvider:forTypes:"), provider, types));
            ObjC.Send(pasteboard, ObjC.Sel("clearContents"));
            Assert.NotEqual(0, (byte)ObjC.Send(pasteboard, ObjC.Sel("writeObjects:"), ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObject:"), item)));
            body();
        }
        finally
        {
            ObjC.Send(pasteboard, ObjC.Sel("clearContents")); // drop the item before its provider is released
            ObjC.Release(item);
            ObjC.Release(provider);
        }
    }

    /// <summary>
    /// Runs <paramref name="test"/> against the real pasteboard and puts back what the developer had. Skips when that cannot
    /// be read in full (restore would then leave the test's contents behind); a failing restore in finally never hides the
    /// test's own failure, and is rethrown only when the test passed.
    /// </summary>
    private static void WithRealPasteboard(ILog? log, Action<Pasteboard> test)
    {
        var board = new Pasteboard(log);
        var original = new Pasteboard().Snapshot();
        Assert.SkipWhen(original.Formats.ContainsKey(Pasteboard.IncompleteMarker), "the pasteboard holds an unreadable type; copy plain text first and rerun");
        var failed = true;
        try
        {
            test(board);
            failed = false;
        }
        finally
        {
            try
            {
                new Pasteboard().Restore(original);
            }
            catch when (failed)
            {
                // The test's own exception is already propagating; this one would replace it.
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="items"/> to the pasteboard directly, snapshots it, dictates over it, restores, and returns
    /// what the pasteboard then holds, read directly; asserts the result equals what was written.
    /// </summary>
    private static List<Dictionary<string, byte[]>> RoundTrip(IReadOnlyList<IReadOnlyList<(string Type, byte[] Bytes)>> items)
    {
        List<Dictionary<string, byte[]>> after = [];
        var log = new RecordingLog();
        WithRealPasteboard(log, board =>
        {
            WriteRaw(items);
            var written = ReadRaw();
            var snapshot = board.Snapshot();
            // Every snapshot logs its time and counts at Debug, with no type names or contents.
            var timing = Assert.Single(log.Lines, l => l.Level == LogLevel.Debug && l.Message.StartsWith("Clipboard snapshot: ", StringComparison.Ordinal));
            Assert.EndsWith($"ms, {written.Count} item(s), {written.Sum(i => i.Count)} type(s).", timing.Message, StringComparison.Ordinal);
            TestContext.Current.TestOutputHelper?.WriteLine(timing.Message);
            Assert.DoesNotContain(Pasteboard.IncompleteMarker, snapshot.Formats.Keys);
            board.SetText("Dictum transcript", keepOutOfHistory: true);
            Assert.Equal("Dictum transcript", Encoding.UTF8.GetString(Assert.Single(ReadRaw())[Pasteboard.PlainText]));
            board.Restore(snapshot);
            after = ReadRaw();
            AssertSame(written, after);
            Assert.DoesNotContain(after, item => item.ContainsKey(Pasteboard.ConcealedType));
        });
        return after;
    }

    private delegate void ProvideDataCallback(nint self, nint sel, nint pasteboard, nint item, nint type);

    /// <summary>Held in a static so the delegate behind the function pointer is never collected.</summary>
    private static readonly ProvideDataCallback ProvideNothing = (_, _, _, _, _) => { };

    /// <summary>
    /// An NSObject subclass implementing NSPasteboardItemDataProvider's required
    /// <c>pasteboard:item:provideDataForType:</c> ("v@:@@@") by providing nothing, defined once through ObjC.DefineClass
    /// with the protocol declared (AppKit refuses a provider that does not conform).
    /// </summary>
    private static readonly Lazy<nint> EmptyProviderClass = new(() =>
        ObjC.DefineClass("DictumTestEmptyPasteboardProvider", "NSObject", ["NSPasteboardItemDataProvider"],
            ("pasteboard:item:provideDataForType:", Marshal.GetFunctionPointerForDelegate(ProvideNothing), "v@:@@@")));


    private static void AssertSame(List<Dictionary<string, byte[]>> expected, List<Dictionary<string, byte[]>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Keys.Order(StringComparer.Ordinal), actual[i].Keys.Order(StringComparer.Ordinal));
            foreach (var (type, bytes) in expected[i]) Assert.Equal(bytes, actual[i][type]);
        }
    }

    /// <summary>Replaces the pasteboard with one NSPasteboardItem per entry, without going through Pasteboard.</summary>
    private static void WriteRaw(IReadOnlyList<IReadOnlyList<(string Type, byte[] Bytes)>> items)
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var board = ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"));
        ObjC.Send(board, ObjC.Sel("clearContents"));
        if (items.Count == 0) return;
        var array = ObjC.Send(ObjC.Class("NSMutableArray"), ObjC.Sel("array"));
        foreach (var types in items)
        {
            var item = ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboardItem"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            foreach (var (type, bytes) in types)
            {
                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
                nint data;
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer, bytes.Length);
                    data = ObjC.Send(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), buffer, bytes.Length); // copies
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                }
                Assert.NotEqual(0, (byte)ObjC.Send(item, ObjC.Sel("setData:forType:"), data, ObjC.NSString(type)));
            }
            ObjC.Send(array, ObjC.Sel("addObject:"), item);
            ObjC.Release(item);
        }
        Assert.NotEqual(0, (byte)ObjC.Send(board, ObjC.Sel("writeObjects:"), array));
    }

    /// <summary>Every item on the pasteboard now, each as type name to bytes, read directly through pasteboardItems.</summary>
    private static List<Dictionary<string, byte[]>> ReadRaw()
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        var items = ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard")), ObjC.Sel("pasteboardItems"));
        var result = new List<Dictionary<string, byte[]>>();
        for (var i = 0; i < (int)ObjC.Send(items, ObjC.Sel("count")); i++)
        {
            var item = ObjC.Send(items, ObjC.Sel("objectAtIndex:"), i);
            var types = ObjC.Send(item, ObjC.Sel("types"));
            var entry = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var t = 0; t < (int)ObjC.Send(types, ObjC.Sel("count")); t++)
            {
                var type = ObjC.Send(types, ObjC.Sel("objectAtIndex:"), t);
                var data = ObjC.Send(item, ObjC.Sel("dataForType:"), type);
                var bytes = new byte[data == 0 ? 0 : (int)ObjC.Send(data, ObjC.Sel("length"))];
                if (bytes.Length > 0) System.Runtime.InteropServices.Marshal.Copy(ObjC.Send(data, ObjC.Sel("bytes")), bytes, 0, bytes.Length);
                entry[ObjC.FromNSString(type)!] = bytes;
            }
            result.Add(entry);
        }
        return result;
    }

    private static nint ChangeCount()
    {
        using var pool = ObjC.AutoreleasePool.Begin();
        return ObjC.Send(ObjC.Send(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard")), ObjC.Sel("changeCount"));
    }

    /// <summary>A valid 1x1 RGBA PNG built here (signature, IHDR, zlib IDAT, IEND, each chunk with its CRC-32).</summary>
    private static byte[] OnePixelPng()
    {
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]); // 1x1, 8-bit, RGBA
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true)) z.Write([0, 0x2A, 0x7F, 0xD4, 0xFF]); // filter 0, one pixel
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(BigEndian((uint)data.Length));
        s.Write(typeBytes);
        s.Write(data);
        s.Write(BigEndian(Crc32([.. typeBytes, .. data])));
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
