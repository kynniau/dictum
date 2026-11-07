using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Core;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;

namespace Dictum.Meetings.Import;

public sealed record BundleInfo(MeetingId MeetingId, int FileCount, long Bytes, string Platform, string AppVersion)
{
    /// <summary>The agreement recorded on the device that made the bundle (design A.6 step 3); set only after decrypt-verify.</summary>
    public Agreement? OriginAgreement { get; init; }
    /// <summary>The origin device's confirmation history, decrypted after verification, shown before the import confirmation.</summary>
    internal IReadOnlyList<MeetingConfirmation> OriginConfirmations { get; init; } = [];
}

/// <summary>The import notice was refused, cancelled or abandoned: the import ends as cancelled, with no trace left.</summary>
public sealed class BundleImportDeclinedException() : OperationCanceledException("The import confirmation was not given.");

/// <param name="approvedScriptVersion">The approved notice version the UI shows; the confirmation must carry exactly this.</param>
/// <param name="approvedScript">The exact import notice the UI shows for a route; the confirmation's script must equal it for its route.</param>
internal sealed class BundleImporter(FileMeetingStore store, IMeetingTranscriber transcriber,
    Func<BundleInfo, string, CancellationToken, Task<Agreement?>> confirmImport,
    string approvedScriptVersion, Func<MeetingProcessingRoute, string> approvedScript,
    Func<Agreement, IDisposable>? useConfirmedRoute = null, ILog? log = null)
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
            // A leftover from a killed import of this id: scrub its plaintext metadata, remove it and start again.
            store.DiscardStaging(target);
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
            MeetingMeta meta;
            try { meta = Read<MeetingMeta>(staged, info.MeetingId, "meta.json.enc", content); }
            // Agreement validates How in its JSON constructor; malformed origin metadata is a damaged bundle.
            catch (ArgumentException) { throw BundleFormat.Invalid(); }
            if (meta.Id != info.MeetingId || meta.Wraps is not { Count: 0 } || meta.IncompleteAudioChannels is null) throw BundleFormat.Invalid();
            VerifyAll(staged, manifest, meta, content, ct);
            var confirmations = manifest.Files.Any(f => f.Name == "confirmations.json.enc")
                ? Read<MeetingConfirmation[]>(staged, meta.Id, "confirmations.json.enc", content) : [];
            ct.ThrowIfCancellationRequested();
            var agreement = await confirmImport(info with { OriginAgreement = meta.Agreement, OriginConfirmations = confirmations },
                importerIdentity, ct).ConfigureAwait(false);
            if (agreement is null) throw new BundleImportDeclinedException();
            var confirmation = agreement.Confirmation;
            if (agreement is not { Kind: AgreementKind.Import } || confirmation is not { Kind: AgreementKind.Import, Route.KeepAudio: not null } ||
                string.IsNullOrWhiteSpace(importerIdentity) || confirmation.ImporterIdentity != importerIdentity ||
                confirmation.ScriptVersion != approvedScriptVersion || confirmation.Script != approvedScript(confirmation.Route) ||
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
            if (staged is not null)
            {
                // A cleanup failure must not replace the import's own exception.
                try { store.DiscardStaging(staged); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    log?.Warn($"Bundle import staging could not be removed and was left at {staged} ({error.GetType().Name}).");
                }
            }
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
