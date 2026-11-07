using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dictum.Meetings;
using Dictum.Meetings.Store;

namespace Dictum.Tests;

// Public synthetic conformance data only. Never used by production export.
internal static partial class PhoneBundleFixture
{
    internal const string Code = "abandon ability able about above absent";
    internal static readonly MeetingId Id = new("0123456789abcdef0123456789abcdef");
    internal static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    internal const string PROPOSED_Title = "Synthetic phone tone";
    internal const string PROPOSED_Script = "Synthetic fixture notice; no real participants.";
    internal static MeetingMeta Meta => new(Id, PROPOSED_Title, At, TimeSpan.FromSeconds(2), MeetingSource.Phone,
        new(At, "written", "synthetic-v1", AgreementKind.Import), [], [], false, null, null);
    internal static MeetingConfirmation[] Confirmations => [new("written", At, AgreementKind.Import, "synthetic-v1",
        PROPOSED_Script, "Synthetic phone operator", new(null, SummaryEngine.Local, null, KeepAudio.Forever))];
    internal static byte[] Range(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    internal static string PathInWorktree
    {
        get
        {
            var folder = new DirectoryInfo(Environment.CurrentDirectory);
            while (folder is not null)
            {
                if (File.Exists(Path.Combine(folder.FullName, "docs", "MEETINGS-CONTRACTS.md")))
                    return Path.Combine(folder.FullName, "tests", "Dictum.Tests", "Fixtures", "Bundles", "phone-sample.dictum");
                folder = folder.Parent;
            }
            throw new InvalidOperationException("Run inside the worktree.");
        }
    }

    internal static byte[] Wav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("RIFF"u8); writer.Write(64036); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(64000);
        for (var n = 0; n < 32000; n++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * n / 16000)));
        return stream.ToArray();
    }

    internal static byte[] Generate(bool chunked = false, bool incomplete = false)
    {
        var content = Range(0, 32);
        var salt = Range(0x20, 16);
        var transfer = TransferCode.Derive(Code, salt);
        try
        {
            var wrap = MeetingCrypto.Wrap(content, transfer, "transfer", Range(0xf0, 12));
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var files = new List<BundleFile>();
                if (chunked)
                {
                    var audio = ChunkedAudio(content, incomplete);
                    files.Add(new("audio-mixed.chunks.enc", audio.Length));
                    Add("audio-mixed.chunks.enc", audio);
                }
                else Seal("audio-mixed.wav.enc", Wav(), 0x40);
                Seal("confirmations.json.enc", JsonSerializer.SerializeToUtf8Bytes(Confirmations), 0x70);
                Seal("meta.json.enc", JsonSerializer.SerializeToUtf8Bytes(
                    Meta with { IncompleteAudioChannels = incomplete ? [Channel.Mixed] : [] }), 0xa0);
                var manifest = new BundleManifest("dictum-bundle", 1, Id.Value, new("Dictum", "1.5.1.0", "ios"),
                    new("pbkdf2-hmac-sha256", 600000, salt, wrap.WrappedKey), files.ToArray());
                Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, BundleFormat.Json));

                void Seal(string name, byte[] plain, int seed)
                {
                    using var input = new MemoryStream(plain);
                    using var sealedBytes = new MemoryStream();
                    var call = 0;
                    MeetingCrypto.Encrypt(input, sealedBytes, content, Id.Value + "/" + name,
                        count => Range(seed + 0x10 * call++, count));
                    var bytes = sealedBytes.ToArray();
                    files.Add(new(name, bytes.Length));
                    Add(name, bytes);
                }
                void Add(string name, byte[] bytes)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    entry.ExternalAttributes = 0;
                    using var destination = entry.Open();
                    destination.Write(bytes);
                }
            }
            var archive = output.ToArray();
            // Pin the ZIP creator OS to DOS even when this test helper runs on a Mac.
            var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(archive.Length - 6));
            for (var i = 0; i < 4; i++)
            {
                archive[central + 5] = 0;
                central += 46 + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(central + 28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(central + 30))
                    + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(central + 32));
            }
            return archive;
        }
        finally { CryptographicOperations.ZeroMemory(content); CryptographicOperations.ZeroMemory(transfer); }
    }

    internal static string Readme(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var text = new StringBuilder("# Synthetic phone bundle conformance fixture\n\n");
        text.Append("Transfer code: `" + Code + "` (six BIP-39 list words; no checksum).\n\n");
        text.Append("Test data only: fixed secrets must never be used for real recordings.\n\n");
        text.Append("All hex ranges below are inclusive, increasing by one byte.\n\n");
        text.Append("- Content key: `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`.\n");
        text.Append("- PBKDF2 salt: `202122232425262728292a2b2c2d2e2f`; SHA-256, 600000 iterations, 32-byte output.\n");
        var key = TransferCode.Derive(Code, Range(0x20, 16));
        text.Append("- Derived transfer key: `" + Convert.ToHexStringLower(key) + "`.\n");
        CryptographicOperations.ZeroMemory(key);
        text.Append("- Transfer wrap nonce: `f0f1f2f3f4f5f6f7f8f9fafb`; AAD UTF-8 `transfer`. No owner wrap.\n");
        text.Append("- Meeting id: `" + Id.Value + "`; Source Phone = 3; channel Mixed = 2.\n");
        text.Append("- Start and confirmation time: `2026-01-01T12:00:00+00:00`; duration `00:00:02`.\n");
        text.Append("- Creator: Dictum / 1.5.1.0 / ios; transfer.kind omitted.\n");
        text.Append("- WAV: 32000 PCM16 little-endian samples, mono, 16000 Hz, 64044 bytes.\n");
        text.Append("  Sample n = truncate-towards-zero(1000 * sin(2 * pi * 440 * n / 16000)), n = 0..31999.\n");
        text.Append("  Canonical 44-byte RIFF header, PCM format 1, byte rate 32000, block alignment 2.\n");
        text.Append("- Each encrypted entry: DCTMENC version 1, one data record and one zero-length terminal record.\n");
        text.Append("  Header suffix / data nonce / terminal nonce (hex ranges):\n");
        text.Append("  audio-mixed.wav.enc: 40..4f / 50..5b / 60..6b;\n");
        text.Append("  confirmations.json.enc: 70..7f / 80..8b / 90..9b;\n");
        text.Append("  meta.json.enc: a0..af / b0..bb / c0..cb.\n");
        text.Append("- ZIP entry order: audio-mixed.wav.enc, confirmations.json.enc, meta.json.enc, manifest.json.\n");
        text.Append("  All stored (method 0), DOS timestamp 2000-01-01 00:00:00, external attributes 0; no comments, directories or ZIP64.\n");
        text.Append("  ZIP made-by version 20, creator OS DOS (0), required version 20, flags 0, no extra fields.\n");
        text.Append("- No transcript, summaries, markers, video or incomplete channels.\n\n");
        text.Append("Exact UTF-8 metadata plaintext (no BOM or trailing newline):\n\n```json\n" + JsonSerializer.Serialize(Meta) + "\n```\n\n");
        text.Append("Exact UTF-8 confirmation plaintext (no BOM or trailing newline):\n\n```json\n" + JsonSerializer.Serialize(Confirmations) + "\n```\n\n");
        text.Append("SHA-256 of uncompressed entry bytes (ciphertext for .enc entries):\n\n");
        foreach (var entry in zip.Entries)
        {
            using var input = entry.Open();
            text.Append("- `" + entry.FullName + "` (" + entry.Length + " bytes): `" + Convert.ToHexStringLower(SHA256.HashData(input)) + "`\n");
        }
        text.Append("\nWhole bundle SHA-256: `" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "`.\n\n");
        text.Append("Regenerate intentionally from the worktree: set DICTUM_REGENERATE_PHONE_FIXTURE=1 and run the xUnit test\n");
        text.Append("`MeetingBundleTests.The_fixture_generator_reproduces_the_committed_bytes`; unset it afterwards.\n");
        text.Append("Normal runs only compare. The helper is PhoneBundleFixture.Generate; production RNG is never replaced.\n");
        return text.ToString();
    }
}
