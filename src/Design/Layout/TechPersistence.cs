using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

// ──────────────────────────────────────────────────────────────────────────────
//  .ctech file format — rev 1 (alpha, no back-compat per policy).
//  Clones LayoutPersistence/SymbolPersistence conventions exactly, including the gzip sniff on load.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CtechFile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public LayoutUnit DefaultDisplayUnit { get; set; }
    public long DefaultSnapDbu { get; set; }
    public long DefaultFlattenTolDbu { get; set; }
    public long DefaultLabelHeightDbu { get; set; }
    public long DefaultViaPadDbu { get; set; }
    public long DefaultViaDrillDbu { get; set; }
    public List<LayerDef> Layers { get; set; } = [];

    /// <summary>The stipple table layers name (rev 1, additive). Absent in every .ctech written
    /// before stipples existed, which reads back as an empty table and a solid fill everywhere —
    /// exactly what those files meant.</summary>
    public List<FillPattern>? FillPatterns { get; set; }
    public Stackup Stackup { get; set; } = new();
    public List<DrcRule> DrcRules { get; set; } = [];
}

/// <summary>Reads and writes .ctech files. Framework-free (no Avalonia / Skia).</summary>
public static class TechPersistence
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

    public static string Serialize(Technology tech)
        => JsonSerializer.Serialize(ToFileModel(tech), JsonOpts);

    public static void SaveToFile(string path, Technology tech)
        => AtomicFile.WriteAllText(path, Serialize(tech));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static Technology Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<CtechFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .ctech file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".ctech format_version {file.FormatVersion} is newer than " +
                $"expected {CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static Technology LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert Technology <-> CtechFile ──────────────────────────────────────

    private static CtechFile ToFileModel(Technology tech) => new()
    {
        FormatVersion        = CurrentFormatVersion,
        Name                 = tech.Name,
        DefaultDisplayUnit   = tech.DefaultDisplayUnit,
        DefaultSnapDbu       = tech.DefaultSnapDbu,
        DefaultFlattenTolDbu = tech.DefaultFlattenTolDbu,
        DefaultLabelHeightDbu = tech.DefaultLabelHeightDbu,
        DefaultViaPadDbu     = tech.DefaultViaPadDbu,
        DefaultViaDrillDbu   = tech.DefaultViaDrillDbu,
        Layers               = [.. tech.Layers],
        FillPatterns         = tech.FillPatterns.Count > 0 ? [.. tech.FillPatterns] : null,
        Stackup              = tech.Stackup,
        DrcRules             = [.. tech.DrcRules],
    };

    private static Technology FromFileModel(CtechFile file) => new()
    {
        Name                 = file.Name,
        DefaultDisplayUnit   = file.DefaultDisplayUnit,
        DefaultSnapDbu       = file.DefaultSnapDbu,
        DefaultFlattenTolDbu = file.DefaultFlattenTolDbu,
        DefaultLabelHeightDbu = file.DefaultLabelHeightDbu,
        DefaultViaPadDbu     = file.DefaultViaPadDbu,
        DefaultViaDrillDbu   = file.DefaultViaDrillDbu,
        Layers               = [.. file.Layers],
        FillPatterns         = file.FillPatterns is { Count: > 0 } fp ? [.. fp] : [],
        Stackup              = file.Stackup,
        DrcRules             = [.. file.DrcRules],
    };
}
