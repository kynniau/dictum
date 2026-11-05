using System.Text;
using Dictum.Audio;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;
using Dictum.Platform;

namespace Dictum.Meetings.Import;

public sealed class MeetingImporter(IMeetingStore store, IMeetingTranscriber transcriber,
    IAudioDecoder decoder, TimeProvider? timeProvider = null,
    Action<MeetingId, Agreement>? saveConfirmation = null)
{
    public async Task<MeetingId> ImportAsync(string path, MeetingSource source, Agreement agreement,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        if (source is not (MeetingSource.ImportedFile or MeetingSource.ImportedZoom))
            throw new ArgumentOutOfRangeException(nameof(source), "Choose an imported meeting source.");
        if (agreement.Confirmation is not { Kind: AgreementKind.Import, Route.KeepAudio: not null } confirmation
            || string.IsNullOrWhiteSpace(confirmation.Script) || string.IsNullOrWhiteSpace(confirmation.ImporterIdentity)
            || string.IsNullOrWhiteSpace(confirmation.Answer) || string.IsNullOrWhiteSpace(confirmation.ScriptVersion))
            throw new ArgumentException("A complete import confirmation is required.", nameof(agreement));
        if (saveConfirmation is null) throw new InvalidOperationException("Confirmation storage is required.");
        MeetingProcessingConsent.Check(cancellationToken);
        path = Path.GetFullPath(path);
        // Keep the recording unchanged while decoding and copying it; concurrent writers must finish first.
        MeetingMeta? created = null;
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
                // Claim the meeting before Create: Create can persist metadata and then fail (initial transcript, index), and that
                // half-made meeting must still be rolled back. The rollback deletes only a meeting that is provably this one.
                var meta = new MeetingMeta(MeetingId.New(), Path.GetFileNameWithoutExtension(path),
                    (timeProvider ?? TimeProvider.System).GetUtcNow(), audio.Duration, source,
                    agreement with { Kind = AgreementKind.Import }, [], [], hasVideo, null, null);
                created = meta;
                id = store.Create(meta);
                saveConfirmation(id, agreement);
                MeetingProcessingConsent.Check(cancellationToken);
                progress?.Report("Saving encrypted recording");
                cancellationToken.ThrowIfCancellationRequested();
                store.WriteAudio(id, Channel.Mixed, wav);
                if (hasVideo) store.WriteVideo(id, path);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Transcribing recording");
            cancellationToken.ThrowIfCancellationRequested();
            MeetingProcessingConsent.Check(cancellationToken);
            await transcriber.RunAsync(id, new FileSource(audio), _ => { }, cancellationToken).ConfigureAwait(false);
            progress?.Report("Import complete");
            return id;
        }
        catch (Exception error)
        {
            if (created is { } meta) DeleteFailedImport(meta, error);
            throw;
        }
    }

    private void DeleteFailedImport(MeetingMeta meta, Exception error)
    {
        try
        {
            // An id that already belonged to another meeting (Create refused it) must never be deleted: if readable metadata
            // exists under the id and is not the one this import wrote, leave it alone. Unreadable or absent metadata can only
            // be this import's own partial write, or nothing at all, which Delete tolerates.
            MeetingMeta? stored = null;
            try { stored = store.Read(meta.Id); }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                              or InvalidDataException) { }
            if (stored is not null && (stored.StartedAt != meta.StartedAt || stored.Title != meta.Title)) return;
            store.Delete(meta.Id);
        }
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
