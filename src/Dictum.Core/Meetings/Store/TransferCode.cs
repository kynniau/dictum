using System.Security.Cryptography;

namespace Dictum.Meetings.Store;

public static class TransferCode
{
    // BIP-39 English word list, MIT, trezor/python-mnemonic src/mnemonic/wordlist/english.txt (see transfer-words.LICENSE).
    // Only the dictionary is reused; transfer codes are six independent random words, with no BIP-39 checksum.
    private static readonly string[] Words = LoadWords();
    private static string[] LoadWords()
    {
        using var stream = typeof(TransferCode).Assembly.GetManifestResourceStream("Dictum.Meetings.Store.transfer-words.txt")!;
        using var reader = new StreamReader(stream);
        var words = reader.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2048 || words.Distinct(StringComparer.Ordinal).Count() != 2048) throw new InvalidDataException();
        return words;
    }
    public static string New() => string.Join(' ', Enumerable.Range(0, 6).Select(_ => Words[RandomNumberGenerator.GetInt32(2048)]));
    public static string Normalise(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return string.Join(' ', code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
    internal static byte[] Derive(string code, byte[] salt)
    {
        var normalised = Normalise(code);
        if (normalised.Split(' ').Length != 6 || normalised.Length > 256) throw new ArgumentException(BundleText.APPROVED_CodeRequired);
        // simplification: PBKDF2 instead of a memory-hard KDF (Argon2 is not in the BCL); retire when a memory-hard KDF reaches the BCL or a customer's threat model names offline cracking of a leaked bundle.
        return Rfc2898DeriveBytes.Pbkdf2(normalised, salt, 600000, HashAlgorithmName.SHA256, 32);
    }
}
