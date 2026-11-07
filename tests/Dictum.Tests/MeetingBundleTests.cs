using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictum.Meetings;
using Dictum.Meetings.Import;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;
using Dictum.Audio;
using Dictum.Transcribe;
using Dictum.Core;
using Dictum.UI.Views.Meetings;
using static Dictum.UI.Views.Meetings.MeetingsText;

namespace Dictum.Tests;

public sealed class MeetingBundleTests
{
    private const string Code = "abandon ability able about above absent";
    private static string Export(MeetingFixture files, MeetingId id)
    {
        var path = Path.Combine(files.Root, "invented-" + id.Value + ".dictum");
        files.Store().ExportBundle(id, path, Code);
        return path;
    }
    private static readonly MeetingProcessingRoute Route = new(null, SummaryEngine.Local, null, KeepAudio.Forever);
    private static Agreement Confirm(string identity) => new(DateTimeOffset.UtcNow, "written", ApprovedScriptVersion, AgreementKind.Import)
    {
        Confirmation = new("written", DateTimeOffset.UtcNow, AgreementKind.Import, ApprovedScriptVersion,
            MeetingsWindow.BundleImportScript(Route), identity, Route),
    };
    private static BundleImporter Importer(MeetingFixture destination,
        Func<BundleInfo, string, CancellationToken, Task<Agreement?>>? confirm = null, IMeetingTranscriber? transcriber = null, ILog? log = null) =>
        new(destination.Store(), transcriber ?? new FakeTranscriber(destination.Store()), confirm ?? ((_, identity, _) => Task.FromResult<Agreement?>(Confirm(identity))),
            ApprovedScriptVersion, MeetingsWindow.BundleImportScript, log: log);
    private static byte[] Entry(ZipArchive zip, string name)
    {
        using var input = zip.GetEntry(name)!.Open(); using var copy = new MemoryStream(); input.CopyTo(copy); return copy.ToArray();
    }
    private static void ChangeEntry(string path, string name, Func<byte[], byte[]> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var changed = change(Entry(zip, name)); zip.GetEntry(name)!.Delete();
        using var output = zip.CreateEntry(name).Open(); output.Write(changed);
    }
    private static void ChangeManifest(string path, Action<JsonObject> change) => ChangeEntry(path, "manifest.json", bytes =>
    {
        var json = JsonNode.Parse(bytes)!.AsObject(); change(json); return Encoding.UTF8.GetBytes(json.ToJsonString());
    });
    private static byte[] ContentKey(ZipArchive zip)
    {
        var manifest = BundleFormat.Read(zip);
        var key = TransferCode.Derive(Code, manifest.Transfer.Salt);
        try { return MeetingCrypto.Unwrap(new("transfer", manifest.Transfer.Wrap), key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static IReadOnlyList<MeetingConfirmation> Confirmations(MeetingFixture files, MeetingId id) =>
        new MeetingsHost.CurrentSettingsStore(files.Root, () => new(), files.Keyring).ReadConfirmations(id);

    [Fact]
    public async Task Bundle_round_trips_a_meeting_between_two_stores_with_different_owner_keys()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store());
        var original = new MeetingsHost.CurrentSettingsStore(source.Root, () => new(), source.Keyring);
        original.WriteConfirmation(id, new("spoken", MeetingFixture.Started, AgreementKind.Live, ApprovedScriptVersion, "Synthetic original notice"));
        var path = Export(source, id); var before = File.ReadAllBytes(path);
        var info = Importer(target).Inspect(path);
        Assert.Equal(id, info.MeetingId); Assert.True(info.FileCount > 0); Assert.True(info.Bytes > 0);
        Assert.Equal(id, await Importer(target).ImportAsync(path, Code, "Synthetic importer"));
        Assert.NotEqual(source.Keyring.OwnerKey(), target.Keyring.OwnerKey());
        Assert.NotEqual(source.Store().Read(id).Wraps[0].WrappedKey, target.Store().Read(id).Wraps[0].WrappedKey);
        Assert.Equal(source.Store().Read(id).Title, target.Store().Read(id).Title);
        Assert.Equal(source.Store().ReadTranscript(id), target.Store().ReadTranscript(id));
        Assert.Equal(JsonSerializer.Serialize(source.Store().ReadSummary(id)), JsonSerializer.Serialize(target.Store().ReadSummary(id)));
        foreach (var channel in Enum.GetValues<Channel>())
        {
            using var input = target.Store().OpenAudio(id, channel); using var copy = new MemoryStream(); input.CopyTo(copy);
            Assert.Equal(MeetingFixture.Wav(), copy.ToArray());
        }
        Assert.Equal(2, Confirmations(target, id).Count);
        Assert.Equal("Synthetic original notice", Confirmations(target, id)[0].Script);
        Assert.Equal("Synthetic importer", Confirmations(target, id)[1].ImporterIdentity);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(source.Store().List());
        // A received meeting can itself be exported again.
        target.Store().ExportBundle(id, Path.Combine(target.Root, "again.dictum"), Code);
    }

    private static void AssertAbsent(byte[] haystack, byte[] needle, string what) =>
        Assert.True(haystack.AsSpan().IndexOf(needle) < 0, what + " appears in the raw bundle bytes");

    [Fact]
    public void Bundle_contains_no_owner_wrap_and_no_plaintext_meta()
    {
        using var source = new MeetingFixture(); var id = source.Populate(source.Store());
        var bundle = Export(source, id);
        // The raw .dictum bytes, as anyone holding the file sees them: no title, transcript, code word or owner wrap.
        var raw = File.ReadAllBytes(bundle);
        AssertAbsent(raw, Encoding.UTF8.GetBytes(source.Store().Read(id).Title), "the title");
        AssertAbsent(raw, Encoding.UTF8.GetBytes(MeetingFixture.TranscriptText), "a transcript phrase");
        AssertAbsent(raw, Encoding.UTF8.GetBytes("budget review"), "a transcript phrase");
        foreach (var word in Code.Split(' ')) AssertAbsent(raw, Encoding.UTF8.GetBytes(word), "the transfer code word " + word);
        foreach (var wrap in source.Store().Read(id).Wraps)
        {
            AssertAbsent(raw, wrap.WrappedKey, "an owner wrap");
            AssertAbsent(raw, Encoding.ASCII.GetBytes(Convert.ToBase64String(wrap.WrappedKey)), "an owner wrap in base64");
        }
        using var zip = ZipFile.OpenRead(bundle);
        Assert.Null(zip.GetEntry("meta.json"));
        var manifest = Encoding.UTF8.GetString(Entry(zip, "manifest.json"));
        Assert.DoesNotContain(source.Store().Read(id).Title, manifest);
        Assert.DoesNotContain(MeetingFixture.TranscriptText, manifest);
        var content = ContentKey(zip);
        try
        {
            var sealedPath = Path.Combine(source.Root, "check.enc"); File.WriteAllBytes(sealedPath, Entry(zip, "meta.json.enc"));
            using var plain = MeetingCrypto.OpenDecrypted(sealedPath, content, id.Value + "/meta.json.enc");
            var meta = JsonSerializer.Deserialize<MeetingMeta>(plain)!;
            Assert.Empty(meta.Wraps);
            foreach (var wrap in source.Store().Read(id).Wraps)
                Assert.DoesNotContain(Convert.ToBase64String(wrap.WrappedKey), manifest);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    [Fact]
    public void Bundle_entries_are_byte_identical_to_the_store_files()
    {
        using var source = new MeetingFixture(); var store = source.Store(); var id = source.Populate(store);
        // Retain transcript batches, both WAV and chunked roles, confirmation and video.
        store.AppendSegments(id, MeetingFixture.Segments);
        var video = Path.Combine(source.Root, "invented.mp4"); File.WriteAllBytes(video, [1, 2, 3]); store.WriteVideo(id, video);
        var other = store.Create(source.Meta()); using (var writer = store.BeginAudio(other, Channel.You)) { writer.Append([.5f]); writer.Complete(); }
        using var zip = ZipFile.OpenRead(Export(source, id));
        foreach (var file in Directory.GetFiles(Path.Combine(source.Root, id.Value), "*.enc"))
            Assert.Equal(File.ReadAllBytes(file), Entry(zip, Path.GetFileName(file)));
        using var chunkZip = ZipFile.OpenRead(Export(source, other));
        Assert.Equal(File.ReadAllBytes(source.At(other, "audio-you.chunks.enc")), Entry(chunkZip, "audio-you.chunks.enc"));
    }

    [Fact]
    public async Task Wrong_transfer_code_fails_before_any_file_is_written()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var path = Export(source, source.Populate(source.Store())); var prompted = false;
        var importer = Importer(target, (_, _, _) => { prompted = true; return Task.FromResult<Agreement?>(null); });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => importer.ImportAsync(path, "ability abandon able about above absent", "test"));
        Assert.False(prompted); Assert.False(Directory.Exists(target.Root));
    }

    [Theory]
    [InlineData("summary.md.enc")]
    [InlineData("audio-you.wav.enc")]
    [InlineData("audio-you.chunks.enc")]
    public async Task A_tampered_bundle_entry_fails_verification_before_confirmation_is_shown(string name)
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var store = source.Store(); var id = source.Populate(store);
        if (name.EndsWith(".chunks.enc", StringComparison.Ordinal))
        {
            File.Delete(source.At(id, "audio-you.wav.enc"));
            using var writer = store.BeginAudio(id, Channel.You); writer.Append([.2f]); writer.Complete();
        }
        var path = Export(source, id); var prompted = false;
        ChangeEntry(path, name, bytes => { bytes[^1] ^= 1; return bytes; });
        var importer = Importer(target, (_, _, _) => { prompted = true; return Task.FromResult<Agreement?>(null); });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => importer.ImportAsync(path, Code, "test"));
        Assert.False(prompted); Assert.Empty(target.Store().List());
        Assert.Empty(Directory.GetDirectories(target.Root));
    }

    [Theory]
    [InlineData("unapproved.enc")]
    [InlineData("../transcript.json.enc")]
    [InlineData("folder/transcript.json.enc")]
    [InlineData("folder\\transcript.json.enc")]
    [InlineData("meta.json")]
    [InlineData("audio-mixed.wav.enc:stream")]
    public void An_entry_name_outside_the_allowed_set_or_with_a_path_separator_is_refused(string name)
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var path = Export(source, source.Populate(source.Store()));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var json = JsonNode.Parse(Entry(zip, "manifest.json"))!;
            json["files"]!.AsArray().Add(new JsonObject { ["name"] = name, ["bytes"] = 0 });
            zip.GetEntry("manifest.json")!.Delete();
            using (var output = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(output, json);
            zip.CreateEntry(name);
        }
        Assert.Throws<InvalidDataException>(() => Importer(target).Inspect(path));
        Assert.False(Directory.Exists(target.Root));
    }

    [Fact]
    public async Task Import_requires_the_existing_import_confirmation_before_the_meeting_is_listed()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store()); var path = Export(source, id); var prompts = 0;
        var importer = Importer(target, (_, _, _) =>
        {
            prompts++; Assert.Empty(target.Store().List());
            Assert.False(File.Exists(target.At(id, "meta.json")));
            return Task.FromResult<Agreement?>(null);
        });
        // No confirmation is a cancelled import, with no trace left.
        await Assert.ThrowsAsync<BundleImportDeclinedException>(() => importer.ImportAsync(path, Code, "test"));
        Assert.Equal(1, prompts); Assert.Empty(Directory.GetDirectories(target.Root)); Assert.Empty(target.Store().List());
        await Importer(target).ImportAsync(path, Code, "test");
        Assert.Equal(id, Assert.Single(target.Store().List()).Id);
        var confirmation = Assert.Single(Confirmations(target, id));
        Assert.Equal(AgreementKind.Import, confirmation.Kind); Assert.Equal("test", confirmation.ImporterIdentity);
        Assert.Equal(MeetingsWindow.BundleImportScript(Route), confirmation.Script); Assert.Equal(ApprovedScriptVersion, confirmation.ScriptVersion);
        Assert.StartsWith(APPROVED_ImportAgreement, confirmation.Script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_confirmation_screen_receives_the_origin_agreement_and_confirmations()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store());
        var original = new MeetingsHost.CurrentSettingsStore(source.Root, () => new(), source.Keyring);
        var recorded = new MeetingConfirmation("spoken", MeetingFixture.Started, AgreementKind.Live, ApprovedScriptVersion, "Synthetic original notice");
        original.WriteConfirmation(id, recorded);
        BundleInfo? shown = null;
        await Importer(target, (info, identity, _) => { shown = info; return Task.FromResult<Agreement?>(Confirm(identity)); })
            .ImportAsync(Export(source, id), Code, "test");
        Assert.NotNull(shown);
        Assert.Equal(source.Store().Read(id).Agreement, shown!.OriginAgreement);
        Assert.NotNull(shown.OriginAgreement);
        Assert.Equal(recorded, Assert.Single(shown.OriginConfirmations));
    }

    [Fact]
    public async Task A_stale_staging_folder_from_a_killed_import_is_removed_and_the_import_succeeds()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store()); var path = Export(source, id);
        var stale = Path.Combine(target.Root, ".import-" + id.Value);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "meta.json"), "{\"synthetic\":\"left by a killed import\"}");
        File.WriteAllBytes(Path.Combine(stale, "meta.json.0123.tmp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(stale, "transcript.json.enc"), [4, 5, 6]);
        Assert.Equal(id, await Importer(target).ImportAsync(path, Code, "test"));
        Assert.False(Directory.Exists(stale));
        Assert.Equal(id, Assert.Single(target.Store().List()).Id);
        Assert.Equal(source.Store().ReadTranscript(id), target.Store().ReadTranscript(id));
    }

    [Fact]
    public async Task Failure_after_staging_leaves_no_plaintext_meta_behind()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store()); var path = Export(source, id);
        var staging = Path.Combine(target.Root, ".import-" + id.Value);
        var log = new LinesLog(); FileStream? held = null;
        try
        {
            // A handle held inside staging makes the rename to <root>/<id> fail (after meta.json is written) and the staging
            // delete fail too: the rename's own error must surface, and the leftover is logged.
            var importer = Importer(target, (_, identity, _) =>
            {
                held = new FileStream(Path.Combine(staging, "0-held.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return Task.FromResult<Agreement?>(Confirm(identity));
            }, log: log);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => importer.ImportAsync(path, Code, "test"));
            Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
            Assert.Contains(".import-" + id.Value, error.Message, StringComparison.Ordinal); // the rename's error, not a cleanup error
            Assert.False(Directory.Exists(Path.Combine(target.Root, id.Value)));
            Assert.Contains(log.Lines, line => line.Contains(staging, StringComparison.Ordinal));
            // Only the held file survives: no plaintext metadata or temporaries are left in the leftover staging.
            Assert.Equal(["0-held.bin"], Directory.GetFileSystemEntries(staging).Select(Path.GetFileName));
            Assert.Empty(target.Store().List());
        }
        finally { held?.Dispose(); }
        // Removal zero-fills plaintext metadata and temporaries first: a hard link outside staging sees the same file data.
        var leftover = Path.Combine(target.Root, ".import-" + MeetingId.New().Value);
        Directory.CreateDirectory(leftover);
        var links = new[] { "meta.json", "meta.json.0123.tmp" }.Select((name, i) =>
        {
            File.WriteAllText(Path.Combine(leftover, name), "{\"Wraps\":[\"synthetic owner wrap\"]}");
            var link = Path.Combine(target.Root, "link-" + i);
            Assert.True(CreateHardLink(link, Path.Combine(leftover, name), IntPtr.Zero));
            return link;
        }).ToArray();
        target.Store().DiscardStaging(leftover);
        Assert.False(Directory.Exists(leftover));
        foreach (var link in links)
        {
            var bytes = File.ReadAllBytes(link);
            Assert.NotEmpty(bytes); Assert.All(bytes, b => Assert.Equal(0, b));
            File.Delete(link);
        }
        // The next import clears the earlier leftover and succeeds.
        Assert.Equal(id, await Importer(target).ImportAsync(path, Code, "test"));
        Assert.False(Directory.Exists(staging));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class LinesLog : ILog
    {
        public List<string> Lines { get; } = [];
        public void Write(LogLevel level, string message) { lock (Lines) Lines.Add(message); }
    }

    [Fact]
    public async Task Importing_an_id_already_in_the_store_is_refused_without_change()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store()); var path = Export(source, id);
        await Importer(target).ImportAsync(path, Code, "test");
        var before = Directory.GetFiles(target.Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(BundleText.PROPOSED_AlreadyHere, Assert.Throws<IOException>(() => Importer(target).Inspect(path)).Message);
        await Assert.ThrowsAsync<IOException>(() => Importer(target).ImportAsync(path, Code, "test"));
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(before.Count, Directory.GetFiles(target.Root, "*", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bundle_with_audio_and_no_transcript_is_transcribed_after_import(bool chunked)
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var store = source.Store(); var id = store.Create(source.Meta());
        if (chunked) { using var writer = store.BeginAudio(id, Channel.You); writer.Append([.2f, -.3f]); writer.Complete(); }
        else { using var wav = new MemoryStream(MeetingFixture.Wav()); store.WriteAudio(id, Channel.You, wav); }
        File.Delete(source.At(id, "transcript.json.enc"));
        var fake = new FakeTranscriber(target.Store());
        await Importer(target, transcriber: fake).ImportAsync(Export(source, id), Code, "test");
        Assert.True(fake.Parts > 0); Assert.Equal(Channel.Mixed, Assert.Single(target.Store().ReadTranscript(id)).Channel);
    }

    [Fact]
    public void Transfer_code_normalisation_is_case_and_space_insensitive()
    {
        Assert.Equal(Code, TransferCode.Normalise("  ABANDON\tAbility  able\r\nABOUT above   absent  "));
        var salt = new byte[16];
        Assert.Equal(TransferCode.Derive(Code, salt), TransferCode.Derive(" ABANDON ability able ABOUT above absent ", salt));
        Assert.All(Enumerable.Range(0, 32).Select(_ => TransferCode.New()), code => Assert.Equal(6, code.Split(' ').Length));
    }

    [Fact]
    public async Task Stored_audio_reaches_the_real_transcriber_before_samples_are_cleared()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var store = source.Store(); var id = store.Create(source.Meta());
        foreach (var channel in new[] { Channel.You, Channel.Others })
        {
            using var writer = store.BeginAudio(id, channel); writer.Append([.25f, -.5f, .75f]); writer.Complete();
        }
        var engine = new CheckingEngine();
        var transcriber = new MeetingTranscriber(engine, new SpeechGate(), target.Store());
        await Importer(target, transcriber: transcriber).ImportAsync(Export(source, id), Code, "test");
        Assert.Equal(2, engine.Calls);
        Assert.NotEmpty(target.Store().ReadTranscript(id));
    }

    private sealed class SpeechGate : IVoiceGate
    {
        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken) => Task.FromResult(new VoiceVerdict(true, ""));
    }
    private sealed class CheckingEngine : ISpeechEngine
    {
        public int Calls;
        public string ModelPath => "synthetic";
        public DecodeOptions Decode => DecodeOptions.Default;
        public void Reconfigure(DecodeOptions decode) { }
        public void Dispose() { }
        public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            Assert.Contains(samples16kMono, sample => Math.Abs(sample) > .1f);
            return Task.FromResult("Synthetic speech part " + ++Calls);
        }
    }

    [Fact]
    public async Task Failed_import_leaves_no_folder_behind()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Populate(source.Store()); var path = Export(source, id);
        var importer = Importer(target, (_, _, _) => throw new IOException("Synthetic confirmation failure"));
        await Assert.ThrowsAsync<IOException>(() => importer.ImportAsync(path, Code, "test"));
        Assert.True(Directory.Exists(target.Root)); Assert.Empty(Directory.GetFileSystemEntries(target.Root));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("kind")]
    [InlineData("script")]
    [InlineData("version")]
    [InlineData("wrong-script")]
    [InlineData("wrong-version")]
    [InlineData("other-route-script")]
    [InlineData("route")]
    [InlineData("retention")]
    public async Task Incomplete_confirmation_is_refused_and_staging_is_removed(string missing)
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var path = Export(source, source.Populate(source.Store()));
        var agreement = Confirm("test"); var confirmation = agreement.Confirmation!;
        confirmation = missing switch
        {
            "identity" => confirmation with { ImporterIdentity = "other" },
            "kind" => confirmation with { Kind = AgreementKind.Live },
            "script" => confirmation with { Script = " " },
            "version" => confirmation with { ScriptVersion = " " },
            "wrong-script" => confirmation with { Script = APPROVED_ImportAgreement },
            "wrong-version" => confirmation with { ScriptVersion = "test-v1" },
            // The notice composed for a different route than the one confirmed.
            "other-route-script" => confirmation with { Script = MeetingsWindow.BundleImportScript(Route with { KeepAudio = KeepAudio.Days30 }) },
            "route" => confirmation with { Route = null },
            "retention" => confirmation with { Route = confirmation.Route! with { KeepAudio = null } },
            _ => confirmation,
        };
        agreement = agreement with { Confirmation = confirmation };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer(target, (_, _, _) => Task.FromResult<Agreement?>(agreement)).ImportAsync(path, Code, "test"));
        Assert.Empty(Directory.GetFileSystemEntries(target.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_processing_route_change_before_publication_removes_staging(bool changeRoute)
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture(); using var cancellation = new CancellationTokenSource();
        var path = Export(source, source.Populate(source.Store()));
        var importer = new BundleImporter(target.Store(), new FakeTranscriber(target.Store()), (_, identity, _) =>
        {
            if (!changeRoute) cancellation.Cancel();
            return Task.FromResult<Agreement?>(Confirm(identity));
        }, ApprovedScriptVersion, MeetingsWindow.BundleImportScript, _ => MeetingProcessingConsent.Enter(() => !changeRoute));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(path, Code, "test", ct: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(target.Root));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("iterations")]
    [InlineData("size")]
    public void Unsupported_or_inconsistent_manifest_is_refused(string field)
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var path = Export(source, source.Populate(source.Store()));
        ChangeManifest(path, json =>
        {
            switch (field)
            {
                case "format": json["format"] = "other"; break;
                case "version": json["version"] = 2; break;
                case "kind": json["transfer"]!["kind"] = "other"; break;
                case "iterations": json["transfer"]!["iterations"] = 1; break;
                case "size": json["files"]![0]!["bytes"] = 0; break;
            }
        });
        Assert.Throws<InvalidDataException>(() => Importer(target).Inspect(path)); Assert.False(Directory.Exists(target.Root));
    }

    [Fact]
    public void Export_with_unavailable_keyring_is_locked_and_creates_no_bundle()
    {
        using var source = new MeetingFixture(); var id = source.Populate(source.Store());
        source.Credentials.Available = false;
        var path = Path.Combine(source.Root, "locked.dictum");
        Assert.Throws<MeetingsLockedException>(() => source.Store().ExportBundle(id, path, Code)); Assert.False(File.Exists(path));
    }

    private sealed class FakeTranscriber(IMeetingStore store) : IMeetingTranscriber
    {
        public int Parts;
        public async Task RunAsync(MeetingId id, IMeetingAudioSource source, Action<Segment> onSegment, CancellationToken cancellationToken)
        {
            await foreach (var (channel, part, offset) in source.ReadAsync(cancellationToken))
            {
                Parts++; Assert.NotEmpty(part.Samples);
                store.AppendSegments(id, [new(offset, offset + part.Duration, channel, "Synthetic imported speech")]);
            }
        }
    }
}
