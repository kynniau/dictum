using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;

namespace Dictum.Meetings.Import;

public sealed record BundleInfo(MeetingId MeetingId, int FileCount, long Bytes, string Platform, string AppVersion);

public sealed class BundleImporter(FileMeetingStore store, IMeetingTranscriber transcriber,
    Func<BundleInfo, string, CancellationToken, Task<Agreement?>> confirmImport,
    Func<Agreement, IDisposable>? useConfirmedRoute = null)
{
    // simplification: one staged bundle import per process; retire for concurrent import queues.
    private static readonly SemaphoreSlim ImportGate = new(1, 1);
    public BundleInfo Inspect(string path)
    {
        using var input = File.OpenRead(path);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        return Inspect(archive).Info;
    }
    private (BundleManifest Manifest, BundleInfo Info) Inspect(ZipArchive archive)
    {
        var manifest = BundleFormat.Read(archive);
        var id = new MeetingId(manifest.Meeting);
        store.RefuseExistingBundle(id);
        return (manifest, new(id, manifest.Files.Length, manifest.Files.Sum(f => f.Bytes), manifest.CreatedBy.Platform, manifest.CreatedBy.Version));
    }
    public async Task<MeetingId> ImportAsync(string path, string transferCode, string importerIdentity,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await ImportGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ImportCore(path, transferCode, importerIdentity, progress, ct).ConfigureAwait(false); }
        finally { ImportGate.Release(); }
    }
    private async Task<MeetingId> ImportCore(string path, string transferCode, string importerIdentity, IProgress<string>? progress, CancellationToken ct)
    {
        using var input = File.OpenRead(path); // Hold the same snapshot from inspection through extraction.
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var (manifest, info) = Inspect(archive);
        var transfer = TransferCode.Derive(transferCode, manifest.Transfer.Salt);
        byte[] content;
        try { content = MeetingCrypto.Unwrap(new("transfer", manifest.Transfer.Wrap), transfer); }
        finally { CryptographicOperations.ZeroMemory(transfer); }
        byte[]? owner = null;
        string? staged = null;
        var published = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            owner = store.BundleOwnerKey();
            var target = Path.Combine(store.Root, ".import-" + info.MeetingId.Value);
            if (Directory.Exists(target) || File.Exists(target)) throw BundleFormat.Invalid();
            Directory.CreateDirectory(target);
            staged = target;
            progress?.Report(BundleText.PROPOSED_Verifying);
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                using var source = archive.GetEntry(file.Name)!.Open();
                using var destination = new FileStream(Path.Combine(staged, file.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                BundleFormat.CopyBounded(source, destination, file.Bytes, ct);
            }
            var meta = Read<MeetingMeta>(staged, info.MeetingId, "meta.json.enc", content);
            if (meta.Id != info.MeetingId || meta.Wraps is not { Count: 0 } || meta.IncompleteAudioChannels is null) throw BundleFormat.Invalid();
            VerifyAll(staged, manifest, meta, content, ct);
            var confirmations = manifest.Files.Any(f => f.Name == "confirmations.json.enc")
                ? Read<MeetingConfirmation[]>(staged, meta.Id, "confirmations.json.enc", content) : [];
            ct.ThrowIfCancellationRequested();
            var agreement = await confirmImport(info, importerIdentity, ct).ConfigureAwait(false);
            var confirmation = agreement?.Confirmation;
            if (agreement is not { Kind: AgreementKind.Import } || confirmation is not { Kind: AgreementKind.Import, Route.KeepAudio: not null } ||
                string.IsNullOrWhiteSpace(importerIdentity) || confirmation.ImporterIdentity != importerIdentity ||
                string.IsNullOrWhiteSpace(confirmation.Script) || string.IsNullOrWhiteSpace(confirmation.ScriptVersion) ||
                confirmation.Answer is not ("spoken" or "written"))
                throw new InvalidOperationException(BundleText.PROPOSED_ConfirmationRequired);
            using var route = useConfirmedRoute?.Invoke(agreement);
            MeetingProcessingConsent.Check(ct);
            Write(staged, meta.Id, "confirmations.json.enc", confirmations.Append(confirmation).ToArray(), content);
            if (!manifest.Files.Any(f => f.Name == "transcript.json.enc"))
                Write(staged, meta.Id, "transcript.json.enc", new { Segments = Array.Empty<Segment>(), IncludedBatches = Array.Empty<string>() }, content);
            File.Delete(Path.Combine(staged, "meta.json.enc"));
            store.PublishBundle(staged, meta with { Wraps = [MeetingCrypto.Wrap(content, owner, "owner")] });
            published = true;
            var tracks = Enum.GetValues<Channel>().Where(c => manifest.Files.Any(f =>
                f.Name == $"audio-{c.ToString().ToLowerInvariant()}.wav.enc" || f.Name == $"audio-{c.ToString().ToLowerInvariant()}.chunks.enc")).ToArray();
            if (tracks.Length != 0 && store.ReadTranscript(meta.Id).Count == 0)
            {
                progress?.Report(BundleText.PROPOSED_Transcribing);
                MeetingProcessingConsent.Check(ct);
                await transcriber.RunAsync(meta.Id, new BundleAudioSource(store, meta.Id, tracks), _ => { }, ct).ConfigureAwait(false);
                MeetingProcessingConsent.Check(ct);
            }
            return meta.Id;
        }
        catch
        {
            if (published) store.Delete(info.MeetingId);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (owner is not null) CryptographicOperations.ZeroMemory(owner);
            if (staged is not null && Directory.Exists(staged)) Directory.Delete(staged, true);
        }
    }
    private static T Read<T>(string folder, MeetingId id, string name, byte[] content)
    {
        using var plain = MeetingCrypto.OpenDecrypted(Path.Combine(folder, name), content, id.Value + "/" + name);
        return JsonSerializer.Deserialize<T>(plain) ?? throw BundleFormat.Invalid();
    }
    private static void Write<T>(string folder, MeetingId id, string name, T value, byte[] content)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { using var plain = new MemoryStream(bytes); MeetingCrypto.Encrypt(plain, Path.Combine(folder, name), content, id.Value + "/" + name); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void VerifyAll(string folder, BundleManifest manifest, MeetingMeta meta, byte[] content, CancellationToken ct)
    {
        var buffer = new byte[81920];
        try
        {
            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(folder, file.Name);
                var identity = meta.Id.Value + "/" + file.Name;
                var channel = Enum.GetValues<Channel>().FirstOrDefault(c => file.Name == $"audio-{c.ToString().ToLowerInvariant()}.chunks.enc");
                using var plain = file.Name.EndsWith(".chunks.enc", StringComparison.Ordinal)
                    ? MeetingCrypto.OpenChunkedAudio(path, content, identity, meta.IncompleteAudioChannels.Contains(channel))
                    : MeetingCrypto.OpenDecrypted(path, content, identity);
                ct.ThrowIfCancellationRequested();
                while (plain.Read(buffer) != 0) { ct.ThrowIfCancellationRequested(); CryptographicOperations.ZeroMemory(buffer); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
