using System.Security.Cryptography;
using System.Text.Json;
using Dictum.Meetings.Speakers;
using Dictum.Meetings.Store;
using Dictum.Settings;

namespace Dictum.Meetings.Voice;

/// <summary>What the card shows about the print on disk.</summary>
public enum VoicePrintState { Absent, Ready, OtherModel }

/// <summary>
/// voiceprint.enc (design 1.6 C.2): one file in the data folder, sealed with <see cref="MeetingCrypto"/> under the meetings owner key
/// (<see cref="IMeetingKeyring.OwnerKey"/>: one key, no second credential) with the identity <see cref="Identity"/>. It lives in
/// Dictum.Meetings so the meetings boundary (MeetingsBoundaryTests) still holds: nothing outside this namespace touches sealed files.
/// Never in a .dictum bundle (BundleFormat's allowed names do not include it), never in an export, never written anywhere else.
/// </summary>
public static class VoicePrintStore
{
    public const string FileName = "voiceprint.enc";
    public const string Identity = "voiceprint/v1";

    /// <summary>%LOCALAPPDATA%\Dictum\voiceprint.enc (on a Mac the same name under ~/Library/Application Support/Dictum).</summary>
    public static string PathFor(AppPaths paths) => Path.Combine(paths.LocalDir, FileName);

    /// <summary>The model tag a print must carry to be used: the pinned CAM++ file and the first 8 of its SHA-256.</summary>
    public static string CurrentModel => SpeakerModels.Tag(SpeakerModels.Embedding);

    /// <summary>Seals the print and replaces any earlier one atomically. The plaintext is cleared from memory afterwards.</summary>
    public static void Write(string path, VoicePrint print, IMeetingKeyring keyring)
    {
        ArgumentNullException.ThrowIfNull(print);
        print.Validated();
        var key = keyring.OwnerKey();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(print);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var input = new MemoryStream(bytes);
            MeetingCrypto.Encrypt(input, path, key, Identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// The print and its state. Tolerant like the speakers file (FileMeetingStore.ReadSpeakers): a file that fails decryption or
    /// authentication throws (CryptographicException), so tampering stays loud; once authenticated, a later version or content
    /// <see cref="VoicePrint.Validated"/> refuses reads as absent. A print made with another model than <paramref name="model"/>
    /// is <see cref="VoicePrintState.OtherModel"/>, with no print: it is never used.
    /// </summary>
    public static (VoicePrint? Print, VoicePrintState State) Read(string path, IMeetingKeyring keyring, string model)
    {
        if (!File.Exists(path)) return (null, VoicePrintState.Absent);
        var key = keyring.OwnerKey();
        byte[] plain;
        try
        {
            using var input = MeetingCrypto.OpenDecrypted(path, key, Identity);
            using var buffer = new MemoryStream();
            input.CopyTo(buffer); // throws CryptographicException on a failed tag before anything is parsed
            plain = buffer.ToArray();
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        try
        {
            var print = JsonSerializer.Deserialize<VoicePrint>(plain)?.Validated();
            if (print is null) return (null, VoicePrintState.Absent);
            return string.Equals(print.Model, model, StringComparison.Ordinal) ? (print, VoicePrintState.Ready) : (null, VoicePrintState.OtherModel);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException) { return (null, VoicePrintState.Absent); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Whether a print file is there, usable or not (Delete removes it either way).</summary>
    public static bool Exists(string path) => File.Exists(path);

    /// <summary>Removes the file; true when one was there.</summary>
    public static bool Delete(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
