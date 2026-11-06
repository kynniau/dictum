using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Dictum.Audio;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;

namespace Dictum.Meetings.Import;

// Read stored 16 kHz mono PCM16 WAVs in bounded parts, including the synthetic WAV of chunked audio.
internal sealed class BundleAudioSource(IMeetingStore store, MeetingId id, Channel[] tracks) : IMeetingAudioSource
{
    public TimeSpan Watermark { get; private set; } = TimeSpan.MinValue;
    public async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readers = tracks.Select(channel => ReadTrack(channel, cancellationToken).GetAsyncEnumerator(cancellationToken)).ToArray();
        var ready = new bool[readers.Length];
        try
        {
            for (var i = 0; i < readers.Length; i++) ready[i] = await readers[i].MoveNextAsync();
            while (ready.Any(value => value))
            {
                var index = Enumerable.Range(0, readers.Length).Where(i => ready[i]).MinBy(i => readers[i].Current.Offset);
                Watermark = readers[index].Current.Offset;
                yield return readers[index].Current;
                ready[index] = await readers[index].MoveNextAsync();
            }
        }
        finally { foreach (var reader in readers) await reader.DisposeAsync(); }
    }

    private async IAsyncEnumerable<(Channel Channel, CapturedAudio Part, TimeSpan Offset)> ReadTrack(Channel channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        {
            using var audio = store.OpenAudio(id, channel);
            var header = new byte[12]; audio.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw BundleFormat.Invalid();
            var chunk = new byte[8];
            var formatSeen = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audio.ReadExactly(chunk);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
                {
                    if (length < 16 || length > 4096) throw BundleFormat.Invalid();
                    var format = new byte[length]; audio.ReadExactly(format);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)) != 1 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) != 16000 || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) != 16)
                        throw BundleFormat.Invalid();
                    formatSeen = true;
                }
                else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
                {
                    if (!formatSeen || length % 2 != 0) throw BundleFormat.Invalid();
                    var bytes = new byte[16000 * 2 * 30];
                    long samplesRead = 0;
                    try
                    {
                        for (long left = length; left > 0;)
                        {
                            MeetingProcessingConsent.Check(cancellationToken);
                            var count = (int)Math.Min(bytes.Length, left);
                            audio.ReadExactly(bytes.AsSpan(0, count));
                            var samples = new float[count / 2];
                            for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) / 32768f;
                            // Reuse L5's pause segmenter after PCM decoding. Bound each input window to 30 s.
                            // simplification: FileSource ends a segment at each window boundary; retire when it accepts streaming PCM blocks.
                            try
                            {
                                var source = new FileSource(new CapturedAudio(samples, 0));
                                await foreach (var part in source.ReadAsync(cancellationToken))
                                {
                                    try { yield return (tracks.Length == 1 ? Channel.Mixed : channel, part.Part, TimeSpan.FromSeconds(samplesRead / 16000d) + part.Offset); }
                                    finally { Array.Clear(part.Part.Samples); }
                                }
                            }
                            finally { Array.Clear(samples); }
                            samplesRead += samples.Length; left -= count;
                            await Task.CompletedTask;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    break;
                }
                else
                {
                    var skip = new byte[81920];
                    try
                    {
                        for (long left = length; left > 0;)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var count = (int)Math.Min(skip.Length, left); audio.ReadExactly(skip.AsSpan(0, count)); left -= count;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(skip); }
                }
                if ((length & 1) != 0 && audio.ReadByte() == -1) throw BundleFormat.Invalid();
            }
        }
    }
}
