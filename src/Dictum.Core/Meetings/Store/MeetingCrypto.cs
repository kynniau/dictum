using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Dictum.Meetings.Store;

internal static class AtomicFile
{
    internal static void Write(string path, Action<Stream> write)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class MeetingCrypto
{
    public const int ChunkSize = 1024 * 1024;
    private static readonly byte[] Magic = "DCTMENC\u0001"u8.ToArray();
    public static byte[] NewContentKey() => RandomNumberGenerator.GetBytes(32);

    public static KeyWrap Wrap(byte[] contentKey, byte[] recipientKey, string recipientId)
    {
        if (contentKey.Length != 32) throw new ArgumentException("Invalid content key.");
        var result = new byte[60];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(recipientKey, 16);
        aes.Encrypt(result.AsSpan(0, 12), contentKey, result.AsSpan(12, 32), result.AsSpan(44, 16), Encoding.UTF8.GetBytes(recipientId));
        return new(recipientId, result);
    }

    public static byte[] Unwrap(KeyWrap wrap, byte[] recipientKey)
    {
        if (wrap.WrappedKey.Length != 60) throw new CryptographicException("Invalid key wrap.");
        var key = new byte[32];
        using var aes = new AesGcm(recipientKey, 16);
        try
        {
            aes.Decrypt(wrap.WrappedKey.AsSpan(0, 12), wrap.WrappedKey.AsSpan(12, 32), wrap.WrappedKey.AsSpan(44, 16), key, Encoding.UTF8.GetBytes(wrap.RecipientId));
            return key;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    public static void EncryptFile(string plainPath, string cipherPath, byte[] contentKey, string identity)
    {
        using var input = File.OpenRead(plainPath);
        Encrypt(input, cipherPath, contentKey, identity);
    }

    internal static void Encrypt(Stream input, string cipherPath, byte[] key, string identity) => AtomicFile.Write(cipherPath, output =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        // The caller's identity binds the meeting and file role; the random header binds this
        // particular encryption. Sequence and a mandatory terminal
        // chunk detect reordering, splicing and truncation, including exact chunk boundaries.
        var header = Magic.Concat(RandomNumberGenerator.GetBytes(16)).ToArray();
        output.Write(header);
        using var aes = new AesGcm(key, 16);
        var plain = new byte[ChunkSize];
        long sequence = 0;
        try
        {
            while (true)
            {
                var length = input.ReadAtLeast(plain, ChunkSize, false);
                var aad = Aad(header, sequence++, length, identity);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[length];
                var tag = new byte[16];
                aes.Encrypt(nonce, plain.AsSpan(0, length), cipher, tag, aad);
                output.Write(aad.AsSpan(header.Length + 8, 4));
                output.Write(nonce);
                output.Write(cipher);
                output.Write(tag);
                if (length == 0) break;
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    });

    private static byte[] Aad(byte[] header, long sequence, int length, string identity)
    {
        var identityBytes = Encoding.UTF8.GetBytes(identity);
        var aad = new byte[header.Length + 12 + identityBytes.Length];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(header.Length), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(header.Length + 8), length);
        identityBytes.CopyTo(aad, header.Length + 12);
        return aad;
    }

    public static void DecryptFile(string cipherPath, string plainPath, byte[] contentKey, string identity)
    {
        using var input = OpenDecrypted(cipherPath, contentKey, identity);
        AtomicFile.Write(plainPath, input.CopyTo);
    }

    public static Stream OpenDecrypted(string cipherPath, byte[] contentKey, string identity) => new DecryptedStream(cipherPath, contentKey, identity);

    private sealed class DecryptedStream : Stream
    {
        private readonly FileStream _input;
        private readonly AesGcm _aes;
        private readonly byte[] _header = new byte[24];
        private readonly string _identity;
        private bool _verified;
        private byte[] _plain = [];
        private int _position;
        private long _sequence;
        private bool _ended;
        private bool _failed;

        public DecryptedStream(string path, byte[] key, string identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            _identity = identity;
            _aes = new AesGcm(key, 16);
            _input = File.OpenRead(path);
            try
            {
                _input.ReadExactly(_header);
                if (!_header.AsSpan(0, 8).SequenceEqual(Magic)) throw new CryptographicException("Invalid encrypted file header.");
            }
            catch { _input.Dispose(); _aes.Dispose(); throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination)
        {
            if (_failed) throw new CryptographicException("Encrypted stream failed authentication.");
            if (destination.IsEmpty || _ended) return 0;
            try
            {
                if (!_verified)
                {
                    // simplification: two passes over the file; retire if a single-pass committing format is adopted
                    while (!_ended) ReadChunk();
                    CryptographicOperations.ZeroMemory(_plain);
                    _plain = [];
                    _input.Position = _header.Length;
                    _sequence = 0;
                    _position = 0;
                    _ended = false;
                    _verified = true;
                }
                if (_position == _plain.Length)
                {
                    ReadChunk();
                    if (_ended) return 0;
                }
                var n = Math.Min(destination.Length, _plain.Length - _position);
                _plain.AsSpan(_position, n).CopyTo(destination);
                _position += n;
                return n;
            }
            catch (Exception e) when (e is CryptographicException or EndOfStreamException)
            {
                _failed = true;
                CryptographicOperations.ZeroMemory(_plain);
                throw new CryptographicException("Encrypted chunk authentication failed.", e);
            }
        }
        private void ReadChunk()
        {
            CryptographicOperations.ZeroMemory(_plain);
            Span<byte> size = stackalloc byte[4];
            _input.ReadExactly(size);
            var length = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (length < 0 || length > ChunkSize) throw new CryptographicException("Invalid encrypted chunk length.");
            var nonce = new byte[12];
            _input.ReadExactly(nonce);
            var cipher = new byte[length];
            _input.ReadExactly(cipher);
            var tag = new byte[16];
            _input.ReadExactly(tag);
            _plain = new byte[length];
            _position = 0;
            _aes.Decrypt(nonce, cipher, tag, _plain, Aad(_header, _sequence++, length, _identity));
            if (length == 0)
            {
                if (_input.ReadByte() != -1) throw new CryptographicException("Trailing encrypted data.");
                _ended = true;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { CryptographicOperations.ZeroMemory(_plain); _aes.Dispose(); _input.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
