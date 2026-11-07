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
        => Wrap(contentKey, recipientKey, recipientId, RandomNumberGenerator.GetBytes(12));

    // Explicit deterministic seam for conformance tests; production callers always use the random overload.
    internal static KeyWrap Wrap(byte[] contentKey, byte[] recipientKey, string recipientId, byte[] nonce)
    {
        if (contentKey.Length != 32) throw new ArgumentException("Invalid content key.");
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, 12, nameof(nonce));
        var result = new byte[60];
        nonce.CopyTo(result, 0);
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

    internal static void Encrypt(Stream input, string cipherPath, byte[] key, string identity) =>
        AtomicFile.Write(cipherPath, output => Encrypt(input, output, key, identity));

    internal static void Encrypt(Stream input, Stream output, byte[] key, string identity)
        => Encrypt(input, output, key, identity, RandomNumberGenerator.GetBytes);

    // No global RNG replacement: only test callers supply a deterministic byte source.
    internal static void Encrypt(Stream input, Stream output, byte[] key, string identity, Func<int, byte[]> randomBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        // The caller's identity binds the meeting and file role; the random header binds this
        // particular encryption. Sequence and a mandatory terminal
        // chunk detect reordering, splicing and truncation, including exact chunk boundaries.
        var header = Magic.Concat(randomBytes(16)).ToArray();
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
                var nonce = randomBytes(12);
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
    }

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

    internal static IMeetingAudioWriter BeginAudio(string path, byte[] content, string identity, Action completed) => new AudioWriter(path, content, identity, completed);
    internal static Stream OpenChunkedAudio(string path, byte[] content, string identity, bool incomplete) => new DecryptedStream(path, content, identity, true, incomplete);

    private sealed class AudioWriter : IMeetingAudioWriter
    {
        private readonly FileStream _output;
        private readonly AesGcm _aes;
        private readonly byte[] _header = "DCTMAUD\u0001"u8.ToArray().Concat(RandomNumberGenerator.GetBytes(16)).ToArray();
        private readonly string _identity;
        private readonly Action _completed;
        private long _sequence;
        private bool _ended, _failed, _completionMarked;
        private long? _terminalStart;
        internal AudioWriter(string path, byte[] content, string identity, Action completed)
        {
            _identity = identity; _completed = completed;
            _aes = new AesGcm(content, 16);
            try
            {
                _output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _output.Write(_header); _output.Flush(true);
            }
            catch { _aes.Dispose(); throw; }
        }
        public void Append(float[] samples)
        {
            if (_ended || _failed) throw new IOException("Audio writer is closed or failed.");
            // Each record is at most 1 MiB, matching the existing encrypted-file bound.
            foreach (var part in samples.Chunk(ChunkSize / 2))
            {
                var pcm = new byte[part.Length * 2];
                try
                {
                    for (var i = 0; i < part.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Clamp(part[i], -1f, 1f) * 32767));
                    Write(pcm);
                }
                finally { CryptographicOperations.ZeroMemory(pcm); Array.Clear(part); }
            }
        }
        private void Write(byte[] plain)
        {
            try
            {
                var aad = Aad(_header, _sequence, plain.Length, _identity);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length]; var tag = new byte[16];
                _aes.Encrypt(nonce, plain, cipher, tag, aad);
                // Authenticate framing before treating a short payload as a torn final write.
                var frameNonce = RandomNumberGenerator.GetBytes(12); var frameTag = new byte[16];
                _aes.Encrypt(frameNonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, frameTag, aad);
                _output.Write(aad.AsSpan(_header.Length + 8, 4));
                _output.Write(frameNonce); _output.Write(frameTag);
                _output.Write(nonce); _output.Write(cipher); _output.Write(tag);
                _output.Flush(true); _sequence++;
            }
            catch { _failed = true; throw; }
        }
        public void Complete()
        {
            if (_completionMarked) return;
            if (_failed) throw new IOException("Audio writer failed.");
            if (!_ended)
            {
                // The PCM is durable. A failed terminal write can be rebuilt on retry,
                // unlike an append whose input may already have left the capture queue.
                _terminalStart ??= _output.Position;
                _output.SetLength(_terminalStart.Value);
                _output.Position = _terminalStart.Value;
                try { Write([]); }
                catch { _failed = false; throw; }
                _ended = true; _output.Dispose();
            }
            _completed(); _completionMarked = true;
        }
        public void Dispose() { _ended = true; _output.Dispose(); _aes.Dispose(); }
    }

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
        private readonly bool _audio;
        private readonly bool _incomplete;
        private readonly long _end;
        private byte[] _wavHeader = [];
        private int _wavPosition;
        private long _pcmLength;

        public DecryptedStream(string path, byte[] key, string identity, bool audio = false, bool incomplete = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            _identity = identity;
            _audio = audio;
            _incomplete = incomplete;
            _aes = new AesGcm(key, 16);
            _input = new FileStream(path, FileMode.Open, FileAccess.Read, audio && incomplete ? FileShare.ReadWrite : FileShare.Read);
            _end = _input.Length;
            try
            {
                _input.ReadExactly(_header);
                if (!_header.AsSpan(0, 8).SequenceEqual(audio ? "DCTMAUD\u0001"u8 : Magic)) throw new CryptographicException("Invalid encrypted file header.");
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
                    if (_audio)
                    {
                        // simplification: standard RIFF PCM is limited to 4 GiB; retire with RF64 for longer recordings.
                        if (_pcmLength > uint.MaxValue - 36) throw new InvalidDataException("Audio exceeds RIFF capacity.");
                        using var header = new MemoryStream();
                        using var writer = new BinaryWriter(header, Encoding.UTF8, true);
                        writer.Write("RIFF"u8); writer.Write((uint)(_pcmLength + 36)); writer.Write("WAVEfmt "u8);
                        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
                        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write((uint)_pcmLength);
                        _wavHeader = header.ToArray();
                    }
                }
                if (_wavPosition < _wavHeader.Length)
                {
                    var copied = Math.Min(destination.Length, _wavHeader.Length - _wavPosition);
                    _wavHeader.AsSpan(_wavPosition, copied).CopyTo(destination); _wavPosition += copied; return copied;
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
            if (_audio && _incomplete && _end - _input.Position < 4) { _ended = true; return; }
            Span<byte> size = stackalloc byte[4];
            _input.ReadExactly(size);
            var length = BinaryPrimitives.ReadInt32LittleEndian(size);
            if (length < 0 || length > ChunkSize) throw new CryptographicException("Invalid encrypted chunk length.");
            if (_audio && (length % 2 != 0)) throw new CryptographicException("Invalid PCM chunk length.");
            if (_audio)
            {
                if (_incomplete && _end - _input.Position < 28) { _ended = true; return; }
                var frameNonce = new byte[12]; var frameTag = new byte[16];
                _input.ReadExactly(frameNonce); _input.ReadExactly(frameTag);
                _aes.Decrypt(frameNonce, ReadOnlySpan<byte>.Empty, frameTag, Span<byte>.Empty, Aad(_header, _sequence, length, _identity));
            }
            if (_audio && _incomplete && _end - _input.Position < length + 28L) { _ended = true; return; }
            var nonce = new byte[12];
            _input.ReadExactly(nonce);
            var cipher = new byte[length];
            _input.ReadExactly(cipher);
            var tag = new byte[16];
            _input.ReadExactly(tag);
            _plain = new byte[length];
            _position = 0;
            _aes.Decrypt(nonce, cipher, tag, _plain, Aad(_header, _sequence++, length, _identity));
            if (!_verified) _pcmLength = checked(_pcmLength + length);
            if (length == 0)
            {
                if (_input.Position != _end) throw new CryptographicException("Trailing encrypted data.");
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
