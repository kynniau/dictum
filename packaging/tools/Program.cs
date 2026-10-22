// Renders Dictum's MSIX logo PNGs from the SVG mark (scripts/package-msix.ps1 runs it; nothing here ships in the app).
//   dotnet run --project packaging/tools -- logos <mark.svg> <mark-for-dark-surfaces.svg> <output folder>
// Sizes and file names: "Construct your Windows app's icon" (Microsoft Learn, read 16/10/2026): every manifest logo at
// scale 100, 125, 150, 200 and 400 (rounded half up, as Learn's table does: 187.5 -> 188), and the app-list icon at
// target sizes 16 to 256 in three forms: plated, "altform-unplated" (dark theme) and "altform-lightunplated" (light).
// Icons fill their canvas (the mark keeps its own 4/64 margin); tiles show the mark at half the tile's height, inside
// the "at least 16% margins on each side" of Learn's tile guidance. Backgrounds are transparent.
//   dotnet run --project packaging/tools -- ico <mark.svg> <out.ico>   (Dictum.exe's icon, below)
//   dotnet run --project packaging/tools -- iconset <mark.svg> <out.iconset folder>   (Dictum.app's icon: scripts/publish-mac.sh
//   runs iconutil on the folder; the ten icon_<n>x<n>[@2x].png names are the ones iconutil reads)
using SkiaSharp;
using Svg.Skia;

if (args is ["ico", var icoSvgPath, var icoOutPath]) return WriteIco(icoSvgPath, icoOutPath);
if (args is ["iconset", var setSvgPath, var setOutDir]) return WriteIconset(setSvgPath, setOutDir);
if (args is not ["logos", var markPath, var darkMarkPath, var outDir])
{
    Console.Error.WriteLine("usage: logos <mark.svg> <mark-for-dark-surfaces.svg> <output folder> | ico <mark.svg> <out.ico> | iconset <mark.svg> <out.iconset folder>");
    return 2;
}

int[] scales = [100, 125, 150, 200, 400];
int[] targetSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
// Manifest logo name, size at scale 100, and the mark's height as a share of the image height.
(string Name, int Width, int Height, double Mark)[] logos =
[
    ("Square44x44Logo", 44, 44, 1.0),
    ("StoreLogo", 50, 50, 1.0),
    ("Square71x71Logo", 71, 71, 0.5),
    ("Square150x150Logo", 150, 150, 0.5),
    ("Wide310x150Logo", 310, 150, 0.5),
    ("Square310x310Logo", 310, 310, 0.5),
];

using var mark = Load(markPath);
using var darkMark = Load(darkMarkPath);
Directory.CreateDirectory(outDir);
var files = 0;
long bytes = 0;

foreach (var (name, width, height, share) in logos)
{
    foreach (var scale in scales)
    {
        Write(mark, $"{name}.scale-{scale}.png", Scaled(width, scale), Scaled(height, scale), share);
    }
}
foreach (var size in targetSizes)
{
    Write(mark, $"Square44x44Logo.targetsize-{size}.png", size, size, 1.0);
    Write(darkMark, $"Square44x44Logo.targetsize-{size}_altform-unplated.png", size, size, 1.0);
    Write(mark, $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png", size, size, 1.0);
}
Console.WriteLine($"Rendered {files} logo PNGs ({bytes:N0} bytes) into {Path.GetFullPath(outDir)}");
return 0;

static int Scaled(int size, int scale) => (int)Math.Round(size * scale / 100.0, MidpointRounding.AwayFromZero);

static SKSvg Load(string path)
{
    var svg = SKSvg.CreateFromFile(path);
    if (svg.Picture is null) throw new InvalidOperationException($"{path} did not render to a picture.");
    return svg;
}

void Write(SKSvg svg, string fileName, int width, int height, double share)
{
    using var bitmap = Render(svg, width, height, share, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    var path = Path.Combine(outDir, fileName);
    File.WriteAllBytes(path, png.ToArray());
    files++;
    bytes += new FileInfo(path).Length;
}

static SKBitmap Render(SKSvg svg, int width, int height, double share, SKColorType colorType, SKAlphaType alphaType)
{
    var picture = svg.Picture!;
    var bounds = picture.CullRect; // the SVG's viewBox: the mark is drawn square, centred
    var side = (float)(Math.Min(width, height) * share);
    var scale = side / Math.Max(bounds.Width, bounds.Height);
    var bitmap = new SKBitmap(new SKImageInfo(width, height, colorType, alphaType));
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Translate((width - bounds.Width * scale) / 2f, (height - bounds.Height * scale) / 2f);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
    }
    return bitmap;
}

// Dictum.exe's icon (polish lane, 16/10/2026): "ico <mark.svg> <out.ico>" writes one .ico with the mark at 16, 24, 32, 48, 64,
// 128 and 256 px. Frames up to 128 px are classic 32-bit DIBs (BGRA rows bottom-up, straight alpha, an all-zero AND mask);
// the 256 px frame is PNG, the convention since Windows Vista. The tray's idle icon loads the same file.
static int WriteIco(string svgPath, string outPath)
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    using var svg = Load(svgPath);
    var frames = new List<(int Size, byte[] Data)>();
    foreach (var size in sizes)
    {
        using var bitmap = Render(svg, size, size, 1.0, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (size == 256)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            frames.Add((size, png.ToArray()));
            continue;
        }
        using var dib = new MemoryStream();
        using (var w = new BinaryWriter(dib))
        {
            var maskStride = (size + 31) / 32 * 4;
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32); // BITMAPINFOHEADER, height doubled for the mask
            w.Write(0); w.Write(size * size * 4 + maskStride * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var pixels = bitmap.Bytes; // top-down BGRA rows of RowBytes each
            for (var row = size - 1; row >= 0; row--) w.Write(pixels, row * bitmap.RowBytes, size * 4);
            w.Write(new byte[maskStride * size]);
        }
        frames.Add((size, dib.ToArray()));
    }
    using var stream = File.Create(outPath);
    using var ico = new BinaryWriter(stream);
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)frames.Count); // ICONDIR: reserved, type 1 = icon, count
    var offset = 6 + 16 * frames.Count;
    foreach (var (size, data) in frames)
    {
        ico.Write((byte)(size >= 256 ? 0 : size)); ico.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        ico.Write((byte)0); ico.Write((byte)0); ico.Write((short)1); ico.Write((short)32);
        ico.Write(data.Length); ico.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in frames) ico.Write(data);
    Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}: {frames.Count} frames ({string.Join(", ", sizes)} px), {offset:N0} bytes");
    return 0;
}

// Dictum.app's icon: the ten PNGs iconutil reads from a .iconset folder (point size 16, 32, 128, 256, 512, each at 1x and @2x;
// the mark fills its canvas, transparent background, as in the .ico).
static int WriteIconset(string svgPath, string outDir)
{
    (string Name, int Pixels)[] images =
    [
        ("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64), ("icon_128x128", 128),
        ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512), ("icon_512x512", 512), ("icon_512x512@2x", 1024),
    ];
    using var svg = Load(svgPath);
    Directory.CreateDirectory(outDir);
    foreach (var (name, pixels) in images)
    {
        using var bitmap = Render(svg, pixels, pixels, 1.0, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), png.ToArray());
    }
    Console.WriteLine($"Wrote {images.Length} PNGs into {Path.GetFullPath(outDir)}");
    return 0;
}
