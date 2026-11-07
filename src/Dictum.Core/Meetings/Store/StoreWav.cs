using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dictum.Meetings.Store;

/// <summary>
/// The one reader of a stored track's WAV header: the store holds every track as 16 kHz mono PCM16 WAV (sealed), and bundle
/// transcription and speaker separation both decode it here. Anything else is refused with the caller's own error.
/// </summary>
internal static class StoreWav
{
    /// <summary>
    /// Reads the RIFF header up to the data chunk and returns that chunk's length in bytes, with <paramref name="audio"/> at its
    /// first sample. Chunks before it are skipped (each padded to an even length, as RIFF requires); a format other than 16 kHz
    /// mono PCM16, a data chunk before the format, or an odd data length throws <paramref name="invalid"/>.
    /// </summary>
    public static long SeekData(Stream audio, Func<Exception> invalid, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        audio.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw invalid();
        var chunk = new byte[8];
        var formatSeen = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            audio.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (length is < 16 or > 4096) throw invalid();
                var format = new byte[length];
                audio.ReadExactly(format);
                if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) != 16000 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) != 16)
                    throw invalid();
                formatSeen = true;
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (!formatSeen || length % 2 != 0) throw invalid();
                return length;
            }
            else Skip(audio, length, cancellationToken);
            if ((length & 1) != 0 && audio.ReadByte() == -1) throw invalid();
        }
    }

    /// <summary>PCM16 little-endian bytes as samples in [-1, 1): one sample per two bytes.</summary>
    public static void ToSamples(ReadOnlySpan<byte> pcm, Span<float> samples)
    {
        for (var i = 0; i < pcm.Length / 2; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) / 32768f;
    }

    // An unknown chunk is read through a bounded buffer that is zeroed afterwards: the stream is decrypted meeting data.
    private static void Skip(Stream audio, long length, CancellationToken cancellationToken)
    {
        var skip = new byte[81920];
        try
        {
            for (var left = length; left > 0;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(skip.Length, left);
                audio.ReadExactly(skip.AsSpan(0, count));
                left -= count;
            }
        }
        finally { CryptographicOperations.ZeroMemory(skip); }
    }
}
