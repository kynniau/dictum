using System.Text;
using Dictum.Audio;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;
using Dictum.Platform;

namespace Dictum.Meetings.Import;

public sealed class MeetingImporter(IMeetingStore store, IMeetingTranscriber transcriber,
    IAudioDecoder decoder, TimeProvider? timeProvider = null)
{
    public async Task<MeetingId> ImportAsync(string path, MeetingSource source, Agreement agreement,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        if (source is not (MeetingSource.ImportedFile or MeetingSource.ImportedZoom))
            throw new ArgumentOutOfRangeException(nameof(source), "Choose an imported meeting source.");
        path = Path.GetFullPath(path);
        // Keep the recording unchanged while decoding and copying it; concurrent writers must finish first.
        MeetingId? created = null;
        try
        {
            CapturedAudio audio;
            MeetingId id;
            using (var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Decoding recording");
                // IAudioDecoder returns CapturedAudio: downmix and MonoResampler have already run in its shared CaptureBuffer path.
                audio = decoder.Decode(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var hasVideo = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
                using var wav = AudioWithOriginal(audio, hasVideo ? null : original, cancellationToken);
                id = store.Create(new(MeetingId.New(), Path.GetFileNameWithoutExtension(path),
                    (timeProvider ?? TimeProvider.System).GetUtcNow(), audio.Duration, source,
                    agreement with { Kind = AgreementKind.Import }, [], [], hasVideo, null, null));
                created = id;
                progress?.Report("Saving encrypted recording");
                cancellationToken.ThrowIfCancellationRequested();
                store.WriteAudio(id, Channel.Mixed, wav);
                if (hasVideo) store.WriteVideo(id, path);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Transcribing recording");
            cancellationToken.ThrowIfCancellationRequested();
            await transcriber.RunAsync(id, new FileSource(audio), _ => { }, cancellationToken).ConfigureAwait(false);
            progress?.Report("Import complete");
            return id;
        }
        catch (Exception error)
        {
            if (created is { } id) DeleteFailedImport(id, error);
            throw;
        }
    }

    private void DeleteFailedImport(MeetingId id, Exception error)
    {
        try { store.Delete(id); }
        catch (Exception deleteError) { throw new AggregateException("Import failed and its meeting could not be deleted.", error, deleteError); }
    }

    private static MemoryStream AudioWithOriginal(CapturedAudio audio, Stream? original, CancellationToken cancellationToken)
    {
        var encoded = Wav.Encode(audio.Samples);
        var result = new MemoryStream();
        try
        {
            result.Write(encoded);
            if (original is not null)
            {
                // simplification: preserve original bytes in a private RIFF 'dsrc' chunk (max 2 GiB in-memory WAV);
                // retire when IMeetingStore exposes an original-audio attachment API or RF64/streaming imports are needed.
                var totalLength = checked(encoded.Length + 8L + original.Length + (original.Length & 1));
                if (totalLength > int.MaxValue) throw new NotSupportedException("This recording is too large for the in-memory WAV importer.");
                using var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
                writer.Write("dsrc"u8);
                writer.Write(checked((uint)original.Length));
                var chunk = new byte[81920];
                int count;
                while ((count = original.Read(chunk)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Write(chunk.AsSpan(0, count));
                }
                if ((original.Length & 1) != 0) writer.Write((byte)0);
                result.Position = 4;
                writer.Write(checked((uint)(result.Length - 8)));
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
