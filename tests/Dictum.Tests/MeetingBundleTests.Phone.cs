using System.IO.Compression;
using Dictum.Meetings;
using Dictum.Meetings.Import;

namespace Dictum.Tests;

public sealed partial class MeetingBundleTests
{
    [Fact]
    public async Task A_phone_bundle_keeps_its_source_after_import()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var id = source.Store().Create(source.Meta() with { Source = MeetingSource.Phone });
        await Importer(target).ImportAsync(Export(source, id), Code, "Synthetic importer", ct: TestContext.Current.CancellationToken);
        Assert.Equal(MeetingSource.Phone, target.Store().Read(id).Source);
        Assert.Equal(3, (int)MeetingSource.Phone);
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enum.GetValues<MeetingSource>().Select(s => (int)s));
    }

    [Fact]
    public async Task Plain_file_import_still_rejects_Phone()
    {
        using var target = new MeetingFixture();
        var importer = new MeetingImporter(target.Store(), new FakeTranscriber(target.Store()), null!);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            importer.ImportAsync("never-decoded.wav", MeetingSource.Phone, Confirm("Synthetic importer"),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(target.Root));
    }

    [Fact]
    public async Task The_committed_phone_fixture_imports_and_is_transcribed_as_Mixed()
    {
        using var target = new MeetingFixture();
        var transcriber = new FakeTranscriber(target.Store());
        Assert.Equal(PhoneBundleFixture.Id, await Importer(target, transcriber: transcriber)
            .ImportAsync(PhoneBundleFixture.PathInWorktree, PhoneBundleFixture.Code, "Synthetic importer", ct: TestContext.Current.CancellationToken));
        var meta = Assert.Single(target.Store().List());
        Assert.Equal(PhoneBundleFixture.Id, meta.Id);
        Assert.Equal(MeetingSource.Phone, meta.Source);
        Assert.Equal(PhoneBundleFixture.PROPOSED_Title, meta.Title);
        Assert.Equal(PhoneBundleFixture.At, meta.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(2), meta.Duration);
        Assert.Equal(PhoneBundleFixture.Meta.Agreement, meta.Agreement);
        Assert.Equal("owner", Assert.Single(meta.Wraps).RecipientId);
        using var audio = target.Store().OpenAudio(meta.Id, Channel.Mixed);
        using var copy = new MemoryStream(); audio.CopyTo(copy);
        Assert.Equal(PhoneBundleFixture.Wav(), copy.ToArray());
        Assert.Equal(64044, copy.Length);
        Assert.Equal(1, transcriber.Parts);
        var segment = Assert.Single(target.Store().ReadTranscript(meta.Id));
        Assert.Equal(Channel.Mixed, segment.Channel);
        Assert.Equal(TimeSpan.Zero, segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), segment.End);
        Assert.Equal(2, Confirmations(target, meta.Id).Count);
        Assert.Equal(PhoneBundleFixture.Confirmations[0], Confirmations(target, meta.Id)[0]);
    }

    [Fact]
    public void The_fixture_generator_reproduces_the_committed_bytes()
    {
        var generated = PhoneBundleFixture.Generate();
        var path = PhoneBundleFixture.PathInWorktree;
        var readme = Path.ChangeExtension(path, ".README.md");
        if (Environment.GetEnvironmentVariable("DICTUM_REGENERATE_PHONE_FIXTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generated);
            File.WriteAllText(readme, PhoneBundleFixture.Readme(generated), new System.Text.UTF8Encoding(false));
        }
        using var committedZip = ZipFile.OpenRead(path);
        using var generatedZip = new ZipArchive(new MemoryStream(generated), ZipArchiveMode.Read);
        Assert.Equal(committedZip.Entries.Select(e => e.FullName), generatedZip.Entries.Select(e => e.FullName));
        foreach (var entry in committedZip.Entries)
        {
            Assert.Equal(entry.Length, entry.CompressedLength);
            Assert.Equal(Entry(committedZip, entry.FullName), Entry(generatedZip, entry.FullName));
        }
        // Also pins compression method, ZIP headers, timestamps and entry order.
        Assert.Equal(File.ReadAllBytes(path), generated);
        Assert.Equal(File.ReadAllText(readme), PhoneBundleFixture.Readme(generated));
    }

    [Fact]
    public void Two_production_exports_of_the_same_meeting_differ()
    {
        using var source = new MeetingFixture(); var store = source.Store(); var id = store.Create(source.Meta());
        var firstPath = Export(source, id); var first = File.ReadAllBytes(firstPath);
        var secondPath = Path.Combine(source.Root, "second.dictum"); store.ExportBundle(id, secondPath, Code);
        var second = File.ReadAllBytes(secondPath);
        Assert.NotEqual(first, second);
        using var a = new ZipArchive(new MemoryStream(first), ZipArchiveMode.Read);
        using var b = new ZipArchive(new MemoryStream(second), ZipArchiveMode.Read);
        var ma = Dictum.Meetings.Store.BundleFormat.Read(a); var mb = Dictum.Meetings.Store.BundleFormat.Read(b);
        Assert.NotEqual(ma.Transfer.Salt, mb.Transfer.Salt);
        // Compare the nonce itself: different salts/ciphertexts must not mask fixed-nonce reuse.
        Assert.NotEqual(ma.Transfer.Wrap[..12], mb.Transfer.Wrap[..12]);
        var ea = Entry(a, "meta.json.enc"); var eb = Entry(b, "meta.json.enc");
        Assert.NotEqual(ea[8..24], eb[8..24]);
        Assert.NotEqual(ea[28..40], eb[28..40]);
        foreach (var entry in a.Entries.Concat(b.Entries)) Assert.Equal(entry.Length, entry.CompressedLength);
    }

    [Fact]
    public async Task Bundle_readers_accept_deflated_entries()
    {
        using var target = new MeetingFixture();
        Directory.CreateDirectory(target.Root);
        var path = Path.Combine(target.Root, "deflated.dictum");
        File.Copy(PhoneBundleFixture.PathInWorktree, path);
        foreach (var name in new[] { "audio-mixed.wav.enc", "confirmations.json.enc", "meta.json.enc", "manifest.json" })
            ChangeEntry(path, name, bytes => bytes);
        using (var zip = ZipFile.OpenRead(path))
            Assert.True(zip.GetEntry("manifest.json")!.CompressedLength < zip.GetEntry("manifest.json")!.Length);
        Assert.Equal(PhoneBundleFixture.Id, await Importer(target).ImportAsync(path, PhoneBundleFixture.Code,
            "Synthetic importer", ct: TestContext.Current.CancellationToken));
        Assert.Equal(MeetingSource.Phone, target.Store().Read(PhoneBundleFixture.Id).Source);
    }

    [Fact]
    public async Task AAC_disguised_as_a_bundle_WAV_is_refused_without_a_platform_decoder()
    {
        using var source = new MeetingFixture(); using var target = new MeetingFixture();
        var store = source.Store(); var id = store.Create(source.Meta() with { Source = MeetingSource.Phone });
        using var m4a = new MemoryStream([0, 0, 0, 24, .. "ftypM4A "u8.ToArray(), 0, 0, 0, 0]);
        store.WriteAudio(id, Channel.Mixed, m4a);
        await Assert.ThrowsAsync<InvalidDataException>(() => Importer(target).ImportAsync(Export(source, id), Code,
            "Synthetic importer", ct: TestContext.Current.CancellationToken));
        Assert.Empty(target.Store().List());
        Assert.Empty(Directory.GetDirectories(target.Root));
    }
}
