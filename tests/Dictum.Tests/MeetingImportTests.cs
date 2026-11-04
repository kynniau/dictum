using System.Diagnostics;
using System.Text;
using Dictum.Audio;
using Dictum.Meetings;
using Dictum.Meetings.Import;
using Dictum.Meetings.Store;
using Dictum.Meetings.Transcribe;
using Dictum.Platform;
using Dictum.Transcribe;
using Dictum.Windows.Audio;
using NAudio.Wave;

namespace Dictum.Tests;

public sealed class MeetingImportTests
{
    private static Agreement ImportAgreement => new(MeetingFixture.Started, "written", "synthetic-v1", AgreementKind.Live);

    [Fact]
    public async Task A_wav_import_round_trips_to_a_Mixed_transcript()
    {
        using var fixture = new MeetingFixture();
        var path = WriteTone(fixture);
        var store = fixture.Store();
        var id = await Importer(store).ImportAsync(path, MeetingSource.ImportedFile, ImportAgreement);
        var segment = Assert.Single(store.ReadTranscript(id));
        Assert.Equal(Channel.Mixed, segment.Channel);
        Assert.Equal("Synthetic 440 Hz tone", segment.Text);
        Assert.Equal(TimeSpan.Zero, segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(.1), segment.End);
        var meta = store.Read(id);
        Assert.Equal(MeetingSource.ImportedFile, meta.Source);
        Assert.Equal(segment.End, meta.Duration);
        Assert.False(meta.HasVideo);
        using var stored = store.OpenAudio(id, Channel.Mixed);
        using var seekable = new MemoryStream();
        stored.CopyTo(seekable);
        seekable.Position = 0;
        using var reader = new WaveFileReader(seekable);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(3200, reader.Length);
        Assert.NotEqual(File.ReadAllBytes(path), File.ReadAllBytes(fixture.At(id, "audio-mixed.wav.enc")));
    }

    [Fact]
    public async Task The_original_file_is_byte_identical_after_import()
    {
        using var fixture = new MeetingFixture();
        var path = WriteTone(fixture);
        var before = File.ReadAllBytes(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var store = fixture.Store();
        var id = await Importer(store).ImportAsync(path, MeetingSource.ImportedFile, ImportAgreement);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        using var audio = store.OpenAudio(id, Channel.Mixed);
        Assert.Equal(before, ReadOriginal(audio));
    }

    [Fact]
    public async Task An_mp4_import_keeps_the_video_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The MP4 platform decoder requires Windows Media Foundation.");
        using var fixture = new MeetingFixture();
        var wav = WriteTone(fixture);
        var path = Path.Combine(fixture.Root, "synthetic.mp4");
        await Mux(wav, path, video: true);
        var before = File.ReadAllBytes(path);
        var store = fixture.Store();
        var id = await Importer(store, new MediaFoundationDecoder()).ImportAsync(path, MeetingSource.ImportedZoom, ImportAgreement);
        Assert.True(store.Read(id).HasVideo);
        Assert.Equal(MeetingSource.ImportedZoom, store.Read(id).Source);
        var cipher = File.ReadAllBytes(fixture.At(id, "video.mp4.enc"));
        Assert.NotEqual(before, cipher);
        var unwrapped = MeetingCrypto.Unwrap(Assert.Single(store.Read(id).Wraps), fixture.Keyring.OwnerKey());
        using var decrypted = MeetingCrypto.OpenDecrypted(fixture.At(id, "video.mp4.enc"), unwrapped,
            $"{id}/video.mp4.enc");
        using var copy = new MemoryStream();
        decrypted.CopyTo(copy);
        Assert.Equal(before, copy.ToArray());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(Channel.Mixed, Assert.Single(store.ReadTranscript(id)).Channel);
    }

    [Fact]
    public async Task Import_agreement_is_recorded_with_kind_Import()
    {
        using var fixture = new MeetingFixture();
        var path = WriteTone(fixture);
        var store = fixture.Store();
        var id = await Importer(store).ImportAsync(path, MeetingSource.ImportedFile, ImportAgreement);
        Assert.Equal(ImportAgreement with { Kind = AgreementKind.Import }, store.Read(id).Agreement);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("flac")]
    public async Task Platform_decoder_imports_synthesized_audio_formats(string extension)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The platform decoder requires Windows Media Foundation.");
        using var fixture = new MeetingFixture();
        var wav = WriteTone(fixture);
        var path = Path.Combine(fixture.Root, "synthetic." + extension);
        await Mux(wav, path, video: false);
        var original = File.ReadAllBytes(path);
        var store = fixture.Store();
        var id = await Importer(store, new MediaFoundationDecoder()).ImportAsync(path, MeetingSource.ImportedFile, ImportAgreement);
        Assert.False(store.Read(id).HasVideo);
        Assert.Equal(Channel.Mixed, Assert.Single(store.ReadTranscript(id)).Channel);
        using var stored = store.OpenAudio(id, Channel.Mixed);
        Assert.Equal(original, ReadOriginal(stored));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Wav_decoder_downmixes_and_resamples_before_transcription()
    {
        using var fixture = new MeetingFixture();
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "stereo.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2)))
        {
            for (var i = 0; i < 4800; i++)
            {
                var sample = (float)(.1 * Math.Sin(2 * Math.PI * 440 * i / 48000));
                writer.WriteSample(sample);
                writer.WriteSample(sample);
            }
        }
        var decoded = new WavOnlyDecoder().Decode(path);
        Assert.InRange(decoded.Samples.Length, 1500, 1600);
        var store = fixture.Store();
        var id = await Importer(store).ImportAsync(path, MeetingSource.ImportedFile, ImportAgreement);
        Assert.Equal(decoded.Duration, Assert.Single(store.ReadTranscript(id)).End);
        Assert.Equal(decoded.Duration, store.Read(id).Duration);
    }

    [Fact]
    public async Task Cancelled_and_invalid_imports_create_no_meeting()
    {
        using var fixture = new MeetingFixture();
        var path = WriteTone(fixture);
        var store = fixture.Store();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer(store).ImportAsync(path,
            MeetingSource.ImportedFile, ImportAgreement, cancellationToken: cancel.Token));
        File.WriteAllText(path, "invalid WAV");
        await Assert.ThrowsAnyAsync<Exception>(() => Importer(store).ImportAsync(path, MeetingSource.ImportedFile, ImportAgreement));
        Assert.Empty(store.List());
    }

    private static MeetingImporter Importer(IMeetingStore store, IAudioDecoder? decoder = null) =>
        new(store, new MeetingTranscriber(new ToneEngine(), new ToneGate(), store), decoder ?? new WavOnlyDecoder());

    private static string WriteTone(MeetingFixture fixture)
    {
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "tone.wav");
        File.WriteAllBytes(path, MeetingFixture.Wav());
        return path;
    }

    private static byte[] ReadOriginal(Stream wav)
    {
        using var reader = new BinaryReader(wav, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4)));
        var length = reader.ReadUInt32();
        Assert.Equal("WAVE", Encoding.ASCII.GetString(reader.ReadBytes(4)));
        long consumed = 4;
        while (consumed < length)
        {
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var count = reader.ReadInt32();
            var contents = reader.ReadBytes(count);
            if (name == "dsrc") return contents;
            if ((count & 1) != 0) reader.ReadByte();
            consumed += 8L + count + (count & 1);
        }
        throw new InvalidDataException("Original source attachment missing.");
    }

    private static async Task Mux(string wav, string destination, bool video)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(wav)!
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", wav })
            start.ArgumentList.Add(argument);
        if (video)
            foreach (var argument in new[] { "-f", "lavfi", "-i", "color=c=black:s=16x16:r=10", "-t", "0.1", "-c:v", "mpeg4", "-c:a", "aac", "-shortest" })
                start.ArgumentList.Add(argument);
        start.ArgumentList.Add(destination);
        Process? process = null;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("No local ffmpeg muxer available; no MP4/audio fixture can be synthesized."); }
        Assert.NotNull(process);
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            await output;
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(destination), await errors);
        }
    }

    private sealed class ToneGate : IVoiceGate
    {
        public Task<VoiceVerdict> CheckAsync(float[] samples16kMono, CancellationToken cancellationToken) =>
            Task.FromResult(new VoiceVerdict(samples16kMono.Any(s => Math.Abs(s) > .001), "Synthesized tone"));
    }

    private sealed class ToneEngine : ISpeechEngine
    {
        public string ModelPath => "synthetic";
        public DecodeOptions Decode { get; private set; } = DecodeOptions.Default;
        public void Reconfigure(DecodeOptions decode) => Decode = decode;
        public Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Verify the generated tone survived decoding; speech inference is intentionally fake.
            double sin = 0, cos = 0;
            for (var i = 0; i < samples16kMono.Length; i++)
            {
                sin += samples16kMono[i] * Math.Sin(2 * Math.PI * 440 * i / 16000);
                cos += samples16kMono[i] * Math.Cos(2 * Math.PI * 440 * i / 16000);
            }
            Assert.True(sin * sin + cos * cos > 1);
            return Task.FromResult("Synthetic 440 Hz tone");
        }
        public void Dispose() { }
    }
}
