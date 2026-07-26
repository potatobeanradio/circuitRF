using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

// ──────────────────────────────────────────────────────────────────────────────
//  .clay file format — rev 1 (alpha, no back-compat per policy).
//  Mirrors SymbolPersistence.cs conventions:
//    - System.Text.Json, enum-as-string, PascalCase, no naming policy
//    - format_version: reject on mismatch (alpha policy)
//    - Id never persisted (LayoutView/shapes carry no Id at all)
//    - Written through AtomicFile
//  Extra: LoadFromFile sniffs the gzip magic bytes and transparently decompresses if present, so a
//  future gzip writer needs no format-version bump (docs/design/layout-view.md §4).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class ClayFile
{
    public int FormatVersion { get; set; } = 1;
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;
    public long SnapDbu { get; set; }
    public AngleMode AngleMode { get; set; } = AngleMode.AnyAngle;

    /// <summary>Relative path to a .ctech.</summary>
    public string? TechRef { get; set; }

    public List<LayoutShape> Shapes { get; set; } = [];
    public List<LayoutInstance> Instances { get; set; } = [];
}

/// <summary>Reads and writes .clay files. Framework-free (no Avalonia / Skia).</summary>
public static class LayoutPersistence
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(LayoutView view)
        => JsonSerializer.Serialize(ToFileModel(view), JsonOpts);

    public static void SaveToFile(string path, LayoutView view)
        => AtomicFile.WriteAllText(path, Serialize(view));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static LayoutView Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<ClayFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .clay file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".clay format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static LayoutView LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert LayoutView <-> ClayFile ───────────────────────────────────────

    private static ClayFile ToFileModel(LayoutView view) => new()
    {
        FormatVersion = CurrentFormatVersion,
        DbuPerMicron  = view.DbuPerMicron,
        DisplayUnit   = view.DisplayUnit,
        SnapDbu       = view.SnapDbu,
        AngleMode     = view.AngleMode,
        TechRef       = view.TechRef,
        Shapes        = [.. view.Shapes],
        Instances     = [.. view.Instances],
    };

    private static LayoutView FromFileModel(ClayFile file)
    {
        var view = new LayoutView
        {
            DbuPerMicron = file.DbuPerMicron,
            DisplayUnit  = file.DisplayUnit,
            SnapDbu      = file.SnapDbu,
            AngleMode    = file.AngleMode,
            TechRef      = file.TechRef,
        };

        foreach (var shape in file.Shapes)
        {
            PadEdgesIfShort(shape);
            view.Shapes.Add(shape);
        }
        view.Instances.AddRange(file.Instances);

        return view;
    }

    /// <summary>A shorter-than-expected Edges list is padded with Line edges on load — graceful
    /// within-version load (docs/design/layout-view.md §3.2 R9a).</summary>
    private static void PadEdgesIfShort(LayoutShape shape)
    {
        switch (shape)
        {
            case CurveShape { Edges: { } edges } curve:
                PadTo(edges, curve.Xy.Length / 2);
                break;
            case PathShape { Edges: { } edges } path:
                PadTo(edges, Math.Max(0, path.Xy.Length / 2 - 1));
                break;
        }
    }

    private static void PadTo(List<LayoutEdge> edges, int expectedCount)
    {
        while (edges.Count < expectedCount)
            edges.Add(new LayoutEdge());
    }
}

/// <summary>Shared gzip-sniffing text reader for .clay / .ctech (docs/design/layout-view.md §4):
/// writers only ever emit plain JSON in v1, but a reader that already sniffs the gzip magic bytes
/// makes a future gzip writer a write-side-only change with no format-version bump.</summary>
internal static class GzipTextFile
{
    private static readonly byte[] GzipMagic = [0x1F, 0x8B];

    public static string ReadAllTextAutoGzip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == GzipMagic[0] && bytes[1] == GzipMagic[1])
        {
            using var input  = new MemoryStream(bytes);
            using var gzip   = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
