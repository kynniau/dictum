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
        using var audio = store.OpenAudio(id, channel);
        var length = StoreWav.SeekData(audio, BundleFormat.Invalid, cancellationToken);
        var bytes = new byte[16000 * 2 * 30];
        long samplesRead = 0;
        try
        {
            for (var left = length; left > 0;)
            {
                MeetingProcessingConsent.Check(cancellationToken);
                var count = (int)Math.Min(bytes.Length, left);
                audio.ReadExactly(bytes.AsSpan(0, count));
                var samples = new float[count / 2];
                StoreWav.ToSamples(bytes.AsSpan(0, count), samples);
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
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
