using System.Security.Cryptography;
using Dictum.Meetings;
using Dictum.Meetings.Store;

namespace Dictum.Tests;

public class MeetingCryptoTests
{
    private const string Identity = "0123456789abcdef0123456789abcdef/audio-you.wav.enc";

    [Fact]
    public void Owner_key_is_created_once_and_read_thereafter()
    {
        var credentials = new FakeCredentialStore(); var keyring = new MeetingKeyring(credentials);
        var first = keyring.OwnerKey(); Assert.Equal(32, first.Length);
        Assert.Equal(Convert.ToBase64String(first), credentials.Read("DICTUM_MEETINGS_KEY", "owner"));
        credentials.RefuseWrites = true;
        Assert.Equal(first, keyring.OwnerKey()); Assert.Equal(first, new MeetingKeyring(credentials).OwnerKey());
        Assert.Single(credentials.Entries);
    }

    [Fact]
    public void No_credential_store_means_MeetingsLockedException_and_no_file_written()
    {
        using var fixture = new MeetingFixture();
        fixture.Credentials.Available = false;
        var error = Assert.Throws<MeetingsLockedException>(() => fixture.Store().Create(fixture.Meta()));
        Assert.Equal(MeetingsLockedException.LockedMessagePlaceholder, error.Message);
        Assert.False(Directory.Exists(fixture.Root)); Assert.Empty(fixture.Credentials.Entries);
        fixture.Credentials.Available = true; fixture.Credentials.RefuseWrites = true;
        Assert.Throws<MeetingsLockedException>(() => fixture.Store().Create(fixture.Meta()));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData(24)] // first length
    [InlineData(28)] // first nonce
    [InlineData(40)] // first ciphertext
    [InlineData(80)] // first tag (plaintext is forty bytes)
    public void A_tampered_byte_is_detected_before_any_plaintext_is_returned(int offset)
    {
        using var fixture = new MeetingFixture(); Directory.CreateDirectory(fixture.Root);
        var source = Path.Combine(fixture.Root, "synthetic.txt"); var encrypted = source + ".enc";
        File.WriteAllBytes(source, Enumerable.Repeat((byte)65, 40).ToArray());
        var key = MeetingCrypto.NewContentKey(); MeetingCrypto.EncryptFile(source, encrypted, key, Identity);
        var bytes = File.ReadAllBytes(encrypted); bytes[offset] ^= 1; File.WriteAllBytes(encrypted, bytes);
        using var input = MeetingCrypto.OpenDecrypted(encrypted, key, Identity);
        var destination = Enumerable.Repeat((byte)0xCC, 100).ToArray();
        Assert.Throws<CryptographicException>(() => input.Read(destination, 0, destination.Length));
        Assert.All(destination, value => Assert.Equal((byte)0xCC, value));
        Assert.Throws<CryptographicException>(() => input.Read(destination, 0, destination.Length));
        var output = Path.Combine(fixture.Root, "output.txt"); File.WriteAllText(output, "untouched");
        Assert.Throws<CryptographicException>(() => MeetingCrypto.DecryptFile(encrypted, output, key, Identity));
        Assert.Equal("untouched", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1048576)]
    [InlineData(2097169)]
    public void Chunked_files_round_trip_and_use_fresh_nonces(int length)
    {
        using var fixture = new MeetingFixture(); Directory.CreateDirectory(fixture.Root);
        var plain = Path.Combine(fixture.Root, "silence.bin"); var cipher = plain + ".enc"; var cipher2 = cipher + "2";
        var output = Path.Combine(fixture.Root, "output.bin");
        var bytes = new byte[length]; File.WriteAllBytes(plain, bytes); var key = MeetingCrypto.NewContentKey();
        MeetingCrypto.EncryptFile(plain, cipher, key, Identity); MeetingCrypto.EncryptFile(plain, cipher2, key, Identity);
        var nonces = ChunkNonces(cipher).Concat(ChunkNonces(cipher2)).ToArray();
        Assert.Equal(2 * ((length + MeetingCrypto.ChunkSize - 1) / MeetingCrypto.ChunkSize + 1), nonces.Length);
        Assert.Equal(nonces.Length, nonces.Distinct(StringComparer.Ordinal).Count());
        using (var input = MeetingCrypto.OpenDecrypted(cipher, key, Identity))
        {
            Assert.False(input.CanSeek); using var copy = new MemoryStream(); input.CopyTo(copy); Assert.Equal(bytes, copy.ToArray());
        }
        MeetingCrypto.DecryptFile(cipher, output, key, Identity); Assert.Equal(bytes, File.ReadAllBytes(output));
    }

    private static List<string> ChunkNonces(string path)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input);
        Assert.Equal("DCTMENC\u0001"u8.ToArray(), reader.ReadBytes(8));
        Assert.Equal(16, reader.ReadBytes(16).Length);
        var nonces = new List<string>();
        while (true)
        {
            var length = reader.ReadInt32();
            Assert.InRange(length, 0, MeetingCrypto.ChunkSize);
            var nonce = reader.ReadBytes(12);
            Assert.Equal(12, nonce.Length);
            nonces.Add(Convert.ToHexString(nonce));
            Assert.Equal(length, reader.ReadBytes(length).Length);
            Assert.Equal(16, reader.ReadBytes(16).Length);
            if (length == 0) break;
        }
        Assert.Equal(input.Length, input.Position);
        return nonces;
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef/audio-others.wav.enc")]
    [InlineData("fedcba9876543210fedcba9876543210/audio-you.wav.enc")]
    public void File_identity_is_authenticated_even_with_the_same_key(string wrongIdentity)
    {
        using var fixture = new MeetingFixture(); Directory.CreateDirectory(fixture.Root);
        var plain = Path.Combine(fixture.Root, "silence.bin"); var cipher = plain + ".enc";
        File.WriteAllBytes(plain, new byte[MeetingCrypto.ChunkSize + 1]);
        var key = MeetingCrypto.NewContentKey();
        MeetingCrypto.EncryptFile(plain, cipher, key, Identity);
        using var input = MeetingCrypto.OpenDecrypted(cipher, key, wrongIdentity);
        var destination = Enumerable.Repeat((byte)0xCC, 100).ToArray();
        Assert.Throws<CryptographicException>(() => input.Read(destination, 0, destination.Length));
        Assert.All(destination, value => Assert.Equal((byte)0xCC, value));
    }

    [Fact]
    public void Wraps_authenticate_the_recipient_and_reject_other_keys()
    {
        var content = MeetingCrypto.NewContentKey(); var owner = MeetingCrypto.NewContentKey();
        var wrap = MeetingCrypto.Wrap(content, owner, "owner");
        Assert.Equal(content, MeetingCrypto.Unwrap(wrap, owner));
        Assert.ThrowsAny<CryptographicException>(() => MeetingCrypto.Unwrap(wrap, MeetingCrypto.NewContentKey()));
        Assert.ThrowsAny<CryptographicException>(() => MeetingCrypto.Unwrap(wrap with { RecipientId = "another" }, owner));
        wrap.WrappedKey[15] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => MeetingCrypto.Unwrap(wrap, owner));
    }

    [Fact]
    public void Truncation_reordering_and_cross_file_chunk_splicing_are_rejected()
    {
        using var fixture = new MeetingFixture(); Directory.CreateDirectory(fixture.Root);
        var plain = Path.Combine(fixture.Root, "silence.bin"); var cipher = plain + ".enc"; var other = cipher + "2";
        File.WriteAllBytes(plain, new byte[MeetingCrypto.ChunkSize * 3]); var key = MeetingCrypto.NewContentKey();
        MeetingCrypto.EncryptFile(plain, cipher, key, Identity); MeetingCrypto.EncryptFile(plain, other, key, Identity);
        var original = File.ReadAllBytes(cipher); var second = File.ReadAllBytes(other);
        var chunk = MeetingCrypto.ChunkSize + 32;
        var variants = new List<byte[]>
        {
            original[..(24 + chunk)], // truncated at a chunk boundary
            original[..^32], // terminal chunk removed
            original[..^1], // terminal tag truncated
            original[..(24 + chunk)].Concat(original[(24 + chunk * 2)..]).ToArray(), // middle chunk removed
            original.Concat(new byte[] { 0 }).ToArray() // trailing data
        };
        var reordered = original.ToArray(); original.AsSpan(24 + chunk, chunk).CopyTo(reordered.AsSpan(24)); original.AsSpan(24, chunk).CopyTo(reordered.AsSpan(24 + chunk)); variants.Add(reordered);
        var spliced = original.ToArray(); second.AsSpan(24, chunk).CopyTo(spliced.AsSpan(24)); variants.Add(spliced);
        foreach (var variant in variants)
        {
            File.WriteAllBytes(cipher, variant);
            using var input = MeetingCrypto.OpenDecrypted(cipher, key, Identity);
            var destination = Enumerable.Repeat((byte)0xCC, 100).ToArray();
            Assert.Throws<CryptographicException>(() => input.Read(destination, 0, destination.Length));
            Assert.All(destination, value => Assert.Equal((byte)0xCC, value));
            Assert.Throws<CryptographicException>(() => input.Read(destination, 0, destination.Length));
        }
    }
}
