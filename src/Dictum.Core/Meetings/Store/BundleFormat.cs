using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dictum.Meetings.Store;

public static class BundleText
{
    // APPROVED 07/11/2026 (owner, round 1 rows 13-19): do not reword.
    public const string APPROVED_Unsupported = "This .dictum file uses a format this version of Dictum cannot open.";
    public const string APPROVED_Invalid = "This .dictum file is incomplete or damaged.";
    public const string APPROVED_AlreadyHere = "This meeting is already on this computer.";
    public const string APPROVED_CodeRequired = "Enter the six words of the transfer code.";
    public const string APPROVED_Verifying = "Checking the encrypted meeting";
    public const string APPROVED_Transcribing = "Transcribing the imported recording";
    public const string APPROVED_ConfirmationRequired = "Confirm the existing import notice before importing this meeting.";
}

internal sealed record BundleFile(string Name, long Bytes);
internal sealed record BundleCreator(string App, string Version, string Platform);
internal sealed record BundleTransfer(string Kdf, int Iterations, byte[] Salt, byte[] Wrap, string? Kind = null);
internal sealed record BundleManifest(string Format, int Version, string Meeting, BundleCreator CreatedBy,
    BundleTransfer Transfer, BundleFile[] Files);

internal static class BundleFormat
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    // Exact store roles only. No paths, alternate streams, case aliases or arbitrary attachments.
    private static readonly Regex AllowedName = new(@"\A(?:meta\.json\.enc|confirmations\.json\.enc|transcript\.json\.enc|transcript-batch-[0-9]{19}\.json\.enc|summary\.md\.enc|speakers\.json\.enc|audio-(?:you|others|mixed)\.(?:wav|chunks)\.enc|video\.[a-z0-9]+\.enc)\z", RegexOptions.CultureInvariant);
    internal static bool Allowed(string name) => name.Length <= 255 && !name.Contains('/') && !name.Contains('\\') && AllowedName.IsMatch(name);
    internal static InvalidDataException Invalid() => new(BundleText.APPROVED_Invalid);
    internal static BundleManifest Read(ZipArchive archive)
    {
        var entries = archive.Entries;
        if (entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count) throw Invalid();
        var entry = archive.GetEntry("manifest.json");
        if (entry is null || entry.Length > 1024 * 1024) throw Invalid();
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        CopyBounded(input, bytes, entry.Length, CancellationToken.None);
        BundleManifest manifest;
        try { manifest = JsonSerializer.Deserialize<BundleManifest>(bytes.ToArray(), Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (manifest.Format != "dictum-bundle" || manifest.Version != 1 || manifest.Transfer is null ||
            manifest.Transfer.Kdf != "pbkdf2-hmac-sha256" || manifest.Transfer.Iterations != 600000 ||
            manifest.Transfer.Kind is not (null or "transfer-code")) throw new InvalidDataException(BundleText.APPROVED_Unsupported);
        _ = new MeetingId(manifest.Meeting);
        if (manifest.CreatedBy is not { App: "Dictum", Platform: "windows" or "mac" or "ios" } || string.IsNullOrWhiteSpace(manifest.CreatedBy.Version) ||
            manifest.Transfer.Salt is not { Length: 16 } || manifest.Transfer.Wrap is not { Length: 60 } || manifest.Files is null) throw Invalid();
        if (manifest.Files.Any(f => f is null || f.Name is null || !Allowed(f.Name) || f.Bytes < 0) ||
            manifest.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length ||
            !manifest.Files.Any(f => f.Name == "meta.json.enc") || entries.Count != manifest.Files.Length + 1) throw Invalid();
        foreach (var file in manifest.Files)
        {
            var payload = archive.GetEntry(file.Name);
            if (payload is null || payload.Length != file.Bytes) throw Invalid();
        }
        return manifest;
    }
    internal static void CopyBounded(Stream input, Stream output, long expected, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            total = checked(total + count);
            if (total > expected) throw Invalid();
            output.Write(buffer, 0, count);
        }
        if (total != expected) throw Invalid();
    }
}
