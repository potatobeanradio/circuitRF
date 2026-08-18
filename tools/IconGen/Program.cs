// ── circuitRF icon generator ──────────────────────────────────────────────────
//
// Rasterises the committed brand SVGs (src/Ui/Assets/artwork/*-app-icon.svg) into the icon
// containers each operating system reads from the file system:
//
//   src/Ui/Assets/<app>Icon.icns    macOS  — .app bundle icon (CFBundleIconFile)
//   src/Ui/Assets/<app>Icon.ico     Windows — PE resource (ApplicationIcon) + MSI shortcut icon
//   packaging/linux/icons/<app>.png Linux  — hicolor 512x512 icon named by the .desktop entry
//
// Icons are BUILD PRODUCTS, not committed: the repository holds the artwork as SVG only (see the
// .gitignore note). Every packaging script runs this first, so nobody has to remember it.
//
//   dotnet run --project tools/IconGen              # all three applications
//   dotnet run --project tools/IconGen -- circuitrf # just one (circuitrf | harmonica | wbond)
//
// .icns and .ico are both written directly rather than shelled out to iconutil / ImageMagick, which
// is what makes this work identically on Windows, macOS and Linux.

using System.Buffers.Binary;
using System.Text;
using SkiaSharp;
using Svg.Skia;

// ---- the three applications ------------------------------------------------------------------

var apps = new (string Key, string Svg, string IconStem, string LinuxName)[]
{
    ("circuitrf", "circuitRF-app-icon.svg",   "circuitRFIcon",   "circuitrf"),
    ("harmonica", "harmonicaRF-app-icon.svg", "harmonicaRFIcon", "harmonicarf"),
    ("wbond",     "wBond-app-icon.svg",       "wBondIcon",       "wbond"),
};

string root = FindRepoRoot();
var wanted = args.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();
if (wanted.Count > 0 && wanted.Except(apps.Select(a => a.Key)).FirstOrDefault() is { } bad)
{
    Console.Error.WriteLine($"Unknown application '{bad}'. Use: {string.Join(" | ", apps.Select(a => a.Key))}");
    return 1;
}

string artwork = Path.Combine(root, "src", "Ui", "Assets", "artwork");
string assets  = Path.Combine(root, "src", "Ui", "Assets");
string linux   = Path.Combine(root, "packaging", "linux", "icons");
Directory.CreateDirectory(linux);

foreach (var app in apps)
{
    if (wanted.Count > 0 && !wanted.Contains(app.Key)) continue;

    string svgPath = Path.Combine(artwork, app.Svg);
    if (!File.Exists(svgPath))
    {
        Console.Error.WriteLine($"✗ {app.Key}: artwork not found at {svgPath}");
        return 1;
    }

    using var svg = new SKSvg();
    if (svg.Load(svgPath) is null)
    {
        Console.Error.WriteLine($"✗ {app.Key}: could not parse {app.Svg}");
        return 1;
    }

    // One render per size — rasterising the vector at each size beats scaling one big bitmap down,
    // and is why the 16x16 stays legible.
    SKBitmap Render(int size) => RenderSvg(svg, size);

    string icns = Path.Combine(assets, app.IconStem + ".icns");
    string ico  = Path.Combine(assets, app.IconStem + ".ico");
    string png  = Path.Combine(linux,  app.LinuxName + ".png");

    WriteIcns(icns, Render);
    WriteIco(ico, Render);
    using (var big = Render(512))
    using (var data = big.Encode(SKEncodedImageFormat.Png, 100))
    using (var fs = File.Create(png))
        data.SaveTo(fs);

    Console.WriteLine($"✓ {app.Key,-10} → {Rel(root, icns)}, {Rel(root, ico)}, {Rel(root, png)}");
}

return 0;

// ---- rendering -------------------------------------------------------------------------------

static SKBitmap RenderSvg(SKSvg svg, int size)
{
    SKPicture picture = svg.Picture!;
    SKRect box = picture.CullRect;

    var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / box.Width, size / box.Height);
    canvas.Translate(-box.Left, -box.Top);
    canvas.DrawPicture(picture);
    canvas.Flush();
    return bmp;
}

static byte[] Png(SKBitmap bmp)
{
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ---- .icns -----------------------------------------------------------------------------------
//
// 'icns' + total length, then one chunk per image: 4-char type, length INCLUDING the 8-byte chunk
// header, PNG payload. The type is what tells macOS the size, so the table below is the format.
// icp4/5/6 are the plain 16/32/64 slots; ic11-ic14 are the @2x variants Retina displays pick up.

static void WriteIcns(string path, Func<int, SKBitmap> render)
{
    (string Type, int Size)[] chunks =
    [
        ("icp4", 16), ("icp5", 32), ("icp6", 64),
        ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
        ("ic11", 32),  // 16@2x
        ("ic12", 64),  // 32@2x
        ("ic13", 256), // 128@2x
        ("ic14", 512), // 256@2x
    ];

    var body = new MemoryStream();
    var cache = new Dictionary<int, byte[]>();
    foreach (var (type, size) in chunks)
    {
        if (!cache.TryGetValue(size, out byte[]? png))
        {
            using var bmp = render(size);
            cache[size] = png = Png(bmp);
        }

        body.Write(Encoding.ASCII.GetBytes(type));
        body.Write(BeUInt32((uint)(png.Length + 8)));
        body.Write(png);
    }

    using var fs = File.Create(path);
    fs.Write(Encoding.ASCII.GetBytes("icns"));
    fs.Write(BeUInt32((uint)(body.Length + 8)));
    body.Position = 0;
    body.CopyTo(fs);
}

// ---- .ico ------------------------------------------------------------------------------------
//
// PNG-compressed entries are only universally understood at 256x256, so everything smaller is
// written as a classic 32-bit DIB with its (opaque) AND mask. That is the difference between an
// icon Explorer draws at every zoom level and one that goes blank in the small-icon views.

static void WriteIco(string path, Func<int, SKBitmap> render)
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];

    var images = new List<(int Size, byte[] Data)>();
    foreach (int size in sizes)
    {
        using var bmp = render(size);
        images.Add((size, size >= 128 ? Png(bmp) : Dib(bmp)));
    }

    using var fs = File.Create(path);
    var head = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), 1);                     // type: icon
    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), (ushort)images.Count);
    fs.Write(head);

    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        var e = new byte[16];
        e[0] = (byte)(size >= 256 ? 0 : size);   // 0 means 256
        e[1] = (byte)(size >= 256 ? 0 : size);
        e[2] = 0;                                 // palette colours
        e[3] = 0;                                 // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), 1);   // colour planes
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(6), 32);  // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)offset);
        fs.Write(e);
        offset += data.Length;
    }

    foreach (var (_, data) in images) fs.Write(data);
}

/// <summary>32-bit bottom-up DIB + 1bpp opaque AND mask, the in-.ico form of a bitmap.</summary>
static byte[] Dib(SKBitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    int maskStride = ((w + 31) / 32) * 4;          // 1bpp rows are padded to 4 bytes
    int pixelBytes = w * h * 4;

    var ms = new MemoryStream();
    var header = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 40);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), w);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), h * 2);   // XOR + AND, per the format
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 32);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)(pixelBytes + maskStride * h));
    ms.Write(header);

    // SKBitmap.GetPixel returns STRAIGHT (unpremultiplied) colour even for a premul bitmap,
    // which is exactly what a .ico entry wants.
    var row = new byte[w * 4];
    for (int y = h - 1; y >= 0; y--)
    {
        for (int x = 0; x < w; x++)
        {
            SKColor c = bmp.GetPixel(x, y);
            row[x * 4 + 0] = c.Blue;
            row[x * 4 + 1] = c.Green;
            row[x * 4 + 2] = c.Red;
            row[x * 4 + 3] = c.Alpha;
        }
        ms.Write(row);
    }

    ms.Write(new byte[maskStride * h]);   // all zero = every pixel opaque; the alpha channel rules
    return ms.ToArray();
}

// ---- helpers ---------------------------------------------------------------------------------

static byte[] BeUInt32(uint v)
{
    var b = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v);
    return b;
}

static string Rel(string root, string path) => Path.GetRelativePath(root, path);

static string FindRepoRoot()
{
    // Walk up from the binary, so the tool works from any working directory.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("circuitRF.slnx not found above the tool's location.");
}
