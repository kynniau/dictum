using Dictum.Core;

namespace Dictum.Platform;

/// <summary>
/// The tray icon in every state, drawn in code from the mark in docs/design/Main.dc.html (three rounded bars on a 16-unit
/// grid): idle = bars in the taskbar's text colour, recording = bars on a red disc, working = dark bars on an amber disc,
/// off = outlined grey bars, error = a dark "!" on an amber disc (not in the design; the nearest of its colours). Rendered
/// to straight-alpha BGRA at any size with 4×4 supersampling, so no image files ship and every platform can turn the
/// pixels into its own icon.
/// </summary>
public static class TrayIconArt
{
    private readonly record struct Bar(float X, float Y, float W, float H, float R);

    private static readonly Bar[] PlainBars = [new(2, 5, 3, 6, 1.5f), new(6.5f, 2, 3, 12, 1.5f), new(11, 4, 3, 8, 1.5f)];
    private static readonly Bar[] DiscBars = [new(3, 5.5f, 2.5f, 5, 1.25f), new(6.75f, 3.5f, 2.5f, 9, 1.25f), new(10.5f, 5, 2.5f, 6, 1.25f)];
    private static readonly Bar[] Exclamation = [new(7, 3, 2, 6.5f, 1), new(7, 11, 2, 2, 1)];

    private const uint Red = 0xFFE5484D, Amber = 0xFFF5A524, Dark = 0xFF1A1A1A, White = 0xFFFFFFFF, Black = 0xFF111111, Grey = 0xFF9CA3AF;

    /// <summary>BGRA pixels, top row first, <paramref name="size"/>² × 4 bytes.</summary>
    /// <param name="lightTaskbar">The taskbar is light, so the idle bars are dark (the design's "tray, light taskbar").</param>
    public static byte[] Render(TrayState state, int size, bool lightTaskbar)
    {
        var pixels = new byte[size * size * 4];
        var scale = size / 16f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var colour = Pixel(state, x, y, scale, lightTaskbar);
                var i = (y * size + x) * 4;
                pixels[i] = (byte)colour;               // B
                pixels[i + 1] = (byte)(colour >> 8);    // G
                pixels[i + 2] = (byte)(colour >> 16);   // R
                pixels[i + 3] = (byte)(colour >> 24);   // A
            }
        }
        return pixels;
    }

    /// <summary>The supersampled colour of one pixel as 0xAARRGGBB (straight alpha).</summary>
    private static uint Pixel(TrayState state, int px, int py, float scale, bool light)
    {
        float a = 0, r = 0, g = 0, b = 0;
        for (var sy = 0; sy < 4; sy++)
        {
            for (var sx = 0; sx < 4; sx++)
            {
                var u = (px + (sx + 0.5f) / 4) / scale; // position on the 16-unit grid
                var v = (py + (sy + 0.5f) / 4) / scale;
                if (Sample(state, u, v, light) is uint c)
                {
                    var alpha = (c >> 24) / 255f;
                    a += alpha;
                    r += ((c >> 16) & 0xFF) * alpha;
                    g += ((c >> 8) & 0xFF) * alpha;
                    b += (c & 0xFF) * alpha;
                }
            }
        }
        if (a <= 0) return 0;
        var outA = (uint)Math.Round(a / 16 * 255);
        return outA << 24 | (uint)Math.Round(r / a) << 16 | (uint)Math.Round(g / a) << 8 | (uint)Math.Round(b / a);
    }

    /// <summary>The colour at one point of the 16-unit grid, or null where the icon is transparent.</summary>
    private static uint? Sample(TrayState state, float u, float v, bool light)
    {
        switch (state)
        {
            case TrayState.Recording:
                return InBars(DiscBars, u, v) ? White : InDisc(u, v) ? Red : null;
            case TrayState.Processing:
                return InBars(DiscBars, u, v) ? Dark : InDisc(u, v) ? Amber : null;
            case TrayState.Error:
                return InBars(Exclamation, u, v) ? Dark : InDisc(u, v) ? Amber : null;
            case TrayState.Disabled:
                return PlainBars.Any(bar => InRounded(bar, u, v, 0.5f) && !InRounded(bar, u, v, -0.5f)) ? Grey : null;
            default:
                return InBars(PlainBars, u, v) ? (light ? Black : White) : null;
        }
    }

    private static bool InDisc(float u, float v) => (u - 8) * (u - 8) + (v - 8) * (v - 8) <= 64;

    private static bool InBars(Bar[] bars, float u, float v) => bars.Any(bar => InRounded(bar, u, v, 0));

    /// <summary>Inside the rounded rectangle grown by <paramref name="grow"/> on every side (negative shrinks it).</summary>
    private static bool InRounded(Bar bar, float u, float v, float grow)
    {
        float x = bar.X - grow, y = bar.Y - grow, w = bar.W + 2 * grow, h = bar.H + 2 * grow, r = Math.Max(0, bar.R + grow);
        if (w <= 0 || h <= 0 || u < x || u > x + w || v < y || v > y + h) return false;
        var dx = Math.Max(Math.Max(x + r - u, 0), u - (x + w - r));
        var dy = Math.Max(Math.Max(y + r - v, 0), v - (y + h - r));
        return dx * dx + dy * dy <= r * r;
    }
}
