using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dictum.UI.Overlays;

namespace Dictum.Tests;

/// <summary>
/// The pill drawn by Skia on the headless platform, state by state: each state renders the pill's own navy and differs
/// from the others. With DICTUM_STILLS_DIR set, each frame is also saved as pill-&lt;state&gt;.png there, for
/// scripts\demo-stills.ps1, which builds the stills walkthrough of a dictation that Windows Sandbox (no microphone)
/// cannot record (docs\DEMOS.md).
/// </summary>
public class PillStillsTests
{
    public const string OutputVariable = "DICTUM_STILLS_DIR";

    /// <summary>Drawn at three times its size, as on a 300 % display, so a still stays sharp in a video.</summary>
    private const double StillScale = 3;

    private static readonly (PillView View, string Name, int Words)[] States =
    [
        (PillView.RecordingHold, "recording-hold", 0),
        (PillView.RecordingHandsFree, "recording-hands-free", 0),
        (PillView.Transcribing, "transcribing", 0),
        (PillView.Polishing, "polishing", 0),
        (PillView.Done, "done", 12),
        (PillView.Cancelled, "cancelled", 0),
        (PillView.MicError, "mic-error", 0),
    ];

    [Fact]
    public Task Each_pill_state_renders_its_navy_body_and_differs_from_the_others() => UiSession.Run(() =>
    {
        var folder = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
        var pill = new PillWindow(null);
        // The same pill, scaled as a whole (its named parts stay the ones Present changes).
        var body = (Control)pill.Content!;
        pill.Content = null;
        pill.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(StillScale, StillScale), Child = body };
        var start = Environment.TickCount64 - 3_000; // the hands-free timer reads 0:03
        var seen = new HashSet<string>();
        foreach (var (view, name, words) in States)
        {
            pill.Present(new PillState(view, start, words));
            using var frame = CaptureAfterRender(pill, name);
            var (navy, hash) = Inspect(frame);
            Assert.True(navy > 2_000, $"{name}: only {navy} pixels of the pill's navy were drawn");
            Assert.True(seen.Add(hash), $"{name} rendered the same pixels as an earlier state");
            if (!string.IsNullOrWhiteSpace(folder)) frame.Save(Path.Combine(folder, $"pill-{name}.png"));
        }
        pill.Close();
    });

    /// <summary>
    /// The headless platform draws only when its render timer ticks, and CaptureRenderedFrame returns null until a frame
    /// has been drawn: on a slow runner the capture right after Present found none (CI, 52dacee). So run the queued jobs
    /// (layout), tick the render timer at least once (so the frame is of this state, not an earlier one), and tick up to
    /// ten times while there is still no frame.
    /// </summary>
    private static WriteableBitmap CaptureAfterRender(PillWindow pill, string name)
    {
        for (var tick = 0; tick < 10; tick++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = pill.CaptureRenderedFrame();
            if (frame is not null) return frame;
        }
        Assert.Fail($"{name}: no frame was rendered after 10 render ticks");
        return null!; // not reached: Assert.Fail throws
    }

    /// <summary>How many pixels are the pill's rgba(11,18,32,0.92), and a hash of every pixel.</summary>
    private static (int Navy, string Hash) Inspect(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var navy = 0;
        var rgba = buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            for (var x = 0; x < buffer.Size.Width; x++)
            {
                var i = y * buffer.RowBytes + x * 4; // premultiplied, BGRA or RGBA
                int b = bytes[i + (rgba ? 2 : 0)], g = bytes[i + 1], r = bytes[i + (rgba ? 0 : 2)], a = bytes[i + 3];
                if (a < 200) continue;
                int R = r * 255 / a, G = g * 255 / a, B = b * 255 / a;
                if (Math.Abs(R - 11) <= 10 && Math.Abs(G - 18) <= 10 && Math.Abs(B - 32) <= 10) navy++;
            }
        }
        return (navy, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
}
