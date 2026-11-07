using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dictum.Meetings;
using Dictum.Meetings.Store;

namespace Dictum.Tests;

internal static partial class PhoneBundleFixture
{
    internal static string ChunkedPath(bool incomplete) => Path.Combine(Path.GetDirectoryName(PathInWorktree)!,
        incomplete ? "phone-sample-chunked-incomplete.dictum" : "phone-sample-chunked.dictum");

    private static byte[] ChunkedAudio(byte[] content, bool incomplete)
    {
        using var files = new MeetingFixture();
        Directory.CreateDirectory(files.Root);
        var path = Path.Combine(files.Root, "audio-mixed.chunks.enc");
        var call = 0;
        using (var writer = MeetingCrypto.BeginAudioWithFixtureBytes(path, content, Id.Value + "/audio-mixed.chunks.enc", () => { },
            count => Range(0x10 * call++, count)))
        {
            var wav = Wav();
            for (var record = 0; record < 2; record++)
            {
                var samples = new float[16000];
                for (var i = 0; i < samples.Length; i++)
                {
                    var pcm = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + record * 32000 + i * 2));
                    // Keep the float safely inside this PCM integer's truncation interval, so Append reproduces it exactly.
                    samples[i] = (pcm + Math.Sign(pcm) * .25f) / 32767f;
                }
                writer.Append(samples);
            }
            if (!incomplete) writer.Complete();
        }
        return File.ReadAllBytes(path);
    }

    internal static string ChunkedReadme(byte[] complete, byte[] incomplete)
    {
        var text = new StringBuilder("# Synthetic chunked phone bundle conformance fixtures\n\n");
        text.Append("Transfer code: `" + Code + "` (six BIP-39 list words; no checksum).\n\n");
        text.Append("Test data only: fixed secrets must never be used for real recordings.\n\n");
        text.Append("All hex ranges below are inclusive, increasing by one byte. Both bundles use these fixed values:\n\n");
        text.Append("- Content key: `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`.\n");
        text.Append("- PBKDF2 salt: `202122232425262728292a2b2c2d2e2f`; SHA-256, 600000 iterations, 32-byte output.\n");
        var key = TransferCode.Derive(Code, Range(0x20, 16));
        text.Append("- Derived transfer key: `" + Convert.ToHexStringLower(key) + "`.\n");
        CryptographicOperations.ZeroMemory(key);
        text.Append("- Transfer wrap nonce: `f0f1f2f3f4f5f6f7f8f9fafb`; AAD UTF-8 `transfer`. No owner wrap.\n");
        text.Append("- Meeting id: `" + Id.Value + "`; Source Phone = 3; channel Mixed = 2.\n");
        text.Append("- Start and confirmation time: `2026-01-01T12:00:00+00:00`; duration `00:00:02`.\n");
        text.Append("- Creator: Dictum / 1.5.1.0 / ios; transfer.kind omitted.\n");
        text.Append("- PCM: the WAV fixture's same 32000 PCM16 little-endian samples, mono, 16000 Hz.\n");
        text.Append("  Sample n = truncate-towards-zero(1000 * sin(2 * pi * 440 * n / 16000)), n = 0..31999.\n");
        text.Append("  Two data records, 32000 PCM bytes (one second) each, with no WAV header in the records.\n");
        text.Append("- DCTMAUD version 1 stream identity: 00..0f. Every byte-source call starts 0x10 above the previous call.\n");
        text.Append("  Record 0 payload nonce / framing nonce: 10..1b / 20..2b.\n");
        text.Append("  Record 1 payload nonce / framing nonce: 30..3b / 40..4b.\n");
        text.Append("  Terminal (record 2) payload nonce / framing nonce: 50..5b / 60..6b.\n");
        text.Append("  The writer chooses the payload nonce first; on disk each framing nonce/tag precedes the payload nonce/ciphertext/tag.\n");
        text.Append("  Complete: two data records then a zero-length terminal record; IncompleteAudioChannels = [].\n");
        text.Append("  Incomplete: the identical two data records, EOF with NO terminal record; IncompleteAudioChannels = [2] (System.Text.Json Mixed).\n");
        text.Append("- Other encrypted entries: DCTMENC version 1, one data record and one zero-length terminal record.\n");
        text.Append("  Header suffix / data nonce / terminal nonce (hex ranges):\n");
        text.Append("  confirmations.json.enc: 70..7f / 80..8b / 90..9b;\n");
        text.Append("  meta.json.enc: a0..af / b0..bb / c0..cb.\n");
        text.Append("- ZIP entry order: audio-mixed.chunks.enc, confirmations.json.enc, meta.json.enc, manifest.json.\n");
        text.Append("  All stored (method 0), DOS timestamp 2000-01-01 00:00:00, external attributes 0; no comments, directories or ZIP64.\n");
        text.Append("  ZIP made-by version 20, creator OS DOS (0), required version 20, flags 0, no extra fields.\n");
        text.Append("- No transcript, summaries, markers or video.\n\n");
        using var zip = new ZipArchive(new MemoryStream(complete), ZipArchiveMode.Read);
        using var input = zip.GetEntry("audio-mixed.chunks.enc")!.Open();
        var header = new byte[24]; input.ReadExactly(header);
        text.Append("Full DCTMAUD header bytes (both bundles, hex):\n\n`" + Convert.ToHexStringLower(header) + "`\n\n");
        var aad = header.Concat(new byte[8]).Concat(new byte[] { 0x00, 0x7d, 0x00, 0x00 })
            .Concat(Encoding.UTF8.GetBytes(Id.Value + "/audio-mixed.chunks.enc")).ToArray();
        text.Append("Complete fixture, record 0 payload-operation AAD (hex):\n\n`" + Convert.ToHexStringLower(aad) + "`\n\n");
        text.Append("Complete fixture, record 0 framing-operation AAD (hex):\n\n`" + Convert.ToHexStringLower(aad) + "`\n\n");
        text.Append("These AAD byte strings MUST be byte-identical: header:24 || sequence:int64LE (0) || length:int32LE (32000, hex 007d0000) || UTF8(meeting-id + \"/audio-mixed.chunks.enc\").\n");
        text.Append("The framing operation seals empty plaintext but its AAD length is the PCM length 32000, never zero for a data record.\n\n");
        foreach (var cutShort in new[] { false, true })
        {
            var meta = Meta with { IncompleteAudioChannels = cutShort ? [Channel.Mixed] : [] };
            text.Append((cutShort ? "Incomplete" : "Complete") + " exact UTF-8 metadata plaintext (no BOM or trailing newline):\n\n```json\n" + JsonSerializer.Serialize(meta) + "\n```\n\n");
        }
        text.Append("Exact UTF-8 confirmation plaintext (both bundles, no BOM or trailing newline):\n\n```json\n" + JsonSerializer.Serialize(Confirmations) + "\n```\n\n");
        Hashes("phone-sample-chunked.dictum", complete);
        Hashes("phone-sample-chunked-incomplete.dictum", incomplete);
        text.Append("Regenerate intentionally from the worktree: set DICTUM_REGENERATE_PHONE_FIXTURE=1 and run the xUnit test\n");
        text.Append("`MeetingBundleTests.The_chunked_fixture_generator_reproduces_the_committed_bytes`; unset it afterwards.\n");
        text.Append("Normal runs only compare. The helper is PhoneBundleFixture.Generate(chunked: true, incomplete: ...); production RNG is never replaced.\n");
        return text.ToString();

        void Hashes(string name, byte[] bytes)
        {
            text.Append("`" + name + "` SHA-256 of uncompressed entry bytes (ciphertext for .enc entries):\n\n");
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                using var source = entry.Open();
                text.Append("- `" + entry.FullName + "` (" + entry.Length + " bytes): `" + Convert.ToHexStringLower(SHA256.HashData(source)) + "`\n");
            }
            text.Append("\nWhole bundle SHA-256: `" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "`.\n\n");
        }
    }
}
