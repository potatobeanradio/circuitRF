// .cem file format — rev 1 (alpha, no back-compat per policy).
// Mirrors TechPersistence exactly: System.Text.Json, WriteIndented, enum-as-string,
// WhenWritingNull, reject-on-newer-format-version, AtomicFile write, gzip sniff on load.

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Em;

public sealed class CemFrequency
{
    public string       StartExpr { get; set; } = "1";
    public string       StopExpr  { get; set; } = "20";
    public string       StepExpr  { get; set; } = "";
    public int?         NumPoints { get; set; } = 101;
    public FreqSpecMode Mode      { get; set; } = FreqSpecMode.PointCount;
    public SweepKind    Kind      { get; set; } = SweepKind.Linear;
    public string       StartUnit { get; set; } = "GHz";
    public string       StopUnit  { get; set; } = "GHz";
    public string       StepUnit  { get; set; } = "GHz";
}

public sealed class CemMesh
{
    public int    MinCellsAcrossWidth { get; set; } = EmMeshSettings.Default.MinCellsAcrossWidth;
    public int    EdgeCells           { get; set; } = EmMeshSettings.Default.EdgeCells;
    public double EdgeFractionOfWidth { get; set; } = EmMeshSettings.Default.EdgeFractionOfWidth;
    public double EdgeGrowthRatio     { get; set; } = EmMeshSettings.Default.EdgeGrowthRatio;
    public double TruncationHeights   { get; set; } = EmMeshSettings.Default.TruncationHeights;
    public int    TruncationTailCells { get; set; } = EmMeshSettings.Default.TruncationTailCells;
}

/// <summary>
/// D3's three planar-mesh controls. A separate block from <see cref="CemMesh"/> because the two
/// meshers' settings are genuinely different quantities, not two spellings of one.
/// </summary>
public sealed class CemPlanarMesh
{
    public bool Auto               { get; set; } = true;
    public int  CellsPerWavelength { get; set; } = PlanarMeshSettings.DefaultCellsPerWavelength;
    public bool EdgeMesh           { get; set; } = PlanarMeshSettings.DefaultEdgeMesh;
    public int  EdgeCells          { get; set; } = PlanarMeshSettings.DefaultEdgeCells;
}

public sealed class CemFile
{
    public int    FormatVersion { get; set; } = 1;
    public string Name          { get; set; } = "";
    public string LayoutRef     { get; set; } = "";
    public string? SignalStackupLayerName { get; set; }

    public CemFrequency Frequency { get; set; } = new();

    public double Port1Z0Real { get; set; } = 50;
    public double Port1Z0Imag { get; set; }
    public double Port2Z0Real { get; set; } = 50;
    public double Port2Z0Imag { get; set; }

    /// <summary>
    /// R-cpl-6: optional per-port reference impedances in D3 order, as flat [re, im] pairs.
    /// <b>Null when unused</b> (WhenWritingNull), so a .cem written before L7b loads unchanged and a
    /// setup that overrides nothing re-serializes byte-identically — which is why this is additive
    /// alongside Port1Z0/Port2Z0 rather than replacing them.
    /// </summary>
    public List<double>? PortZ0s { get; set; }

    /// <summary>
    /// L9d/D5: which conductor stackup entries the planar analysis includes, bottom-to-top.
    /// <b>Null when unused</b> (WhenWritingNull) — a <c>.cem</c> that never named its levels writes
    /// no field and re-serialises byte-identically, exactly as <see cref="PortZ0s"/> does.
    /// </summary>
    public List<string>? AnalysisLevelNames { get; set; }

    public CemMesh Mesh { get; set; } = new();

    /// <summary>
    /// <b>Non-nullable on purpose, unlike every other flag here.</b> The model's default flipped to
    /// <c>true</c>, and every <c>.cem</c> ever written carries this field explicitly — so an
    /// existing setup keeps whatever it recorded and only a newly created one picks the new default
    /// up. Making it nullable-and-omitted would silently change the answer for every file on disk.
    /// </summary>
    public bool    DispersionCorrection  { get; set; }

    /// <summary>
    /// <b>Null means ON</b> — the opposite polarity to the flags around it, because the default is
    /// on. A <c>.cem</c> written before adaptive sampling existed has no field, loads with it
    /// enabled, and re-serialises with no field; only an explicit opt-OUT is ever written.
    /// </summary>
    public bool?   AdaptiveSampling      { get; set; }

    /// <summary>
    /// M2 — <b>null means off</b>, which is what every <c>.cem</c> written before it means. Nullable
    /// + <c>WhenWritingNull</c> (the document-wide default) so such a file loads AND re-serialises
    /// byte-identically, exactly as <see cref="AnalysisKind"/> and <see cref="PlanarMesh"/> do.
    /// </summary>
    public bool?   DirectVerticalKernel  { get; set; }
    public string? SnpOutputPathOverride { get; set; }

    /// <summary>
    /// D7 — <b>null means the cross-section analysis</b>, which is what every <c>.cem</c> written
    /// before L8b means. Nullable + <c>WhenWritingNull</c> so such a file loads AND re-serialises
    /// byte-identically; a setup that has never been switched to the planar kernel writes neither
    /// this field nor <see cref="PlanarMesh"/>.
    /// </summary>
    public EmAnalysisKind? AnalysisKind { get; set; }

    /// <summary>Null when unused, for the same byte-identity reason as <see cref="AnalysisKind"/>.</summary>
    public CemPlanarMesh? PlanarMesh { get; set; }
}

/// <summary>Reads and writes <c>.cem</c> files. Framework-free (no Avalonia / Skia).</summary>
public static class EmSetupPersistence
{
    public const string Extension = ".cem";
    public const int    CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public static string Serialize(EmSetup setup)
        => JsonSerializer.Serialize(ToFileModel(setup), JsonOpts);

    public static void SaveToFile(string path, EmSetup setup)
        => AtomicFile.WriteAllText(path, Serialize(setup));

    public static EmSetup Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<CemFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .cem file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".cem format_version {file.FormatVersion} is newer than expected " +
                $"{CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static EmSetup LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── Convert ───────────────────────────────────────────────────────────────

    private static CemFile ToFileModel(EmSetup s) => new()
    {
        FormatVersion          = CurrentFormatVersion,
        Name                   = s.Name,
        LayoutRef              = s.LayoutRef,
        SignalStackupLayerName = s.SignalStackupLayerName is { Length: > 0 } n ? n : null,
        Frequency = new CemFrequency
        {
            StartExpr = s.Frequency.StartExpr,
            StopExpr  = s.Frequency.StopExpr,
            StepExpr  = s.Frequency.StepExpr,
            NumPoints = s.Frequency.NumPoints,
            Mode      = s.Frequency.Mode,
            Kind      = s.Frequency.Kind,
            StartUnit = s.Frequency.StartUnit,
            StopUnit  = s.Frequency.StopUnit,
            StepUnit  = s.Frequency.StepUnit,
        },
        Port1Z0Real = s.Port1Z0.Real,
        Port1Z0Imag = s.Port1Z0.Imaginary,
        Port2Z0Real = s.Port2Z0.Real,
        Port2Z0Imag = s.Port2Z0.Imaginary,
        PortZ0s     = FlattenPortZ0s(s.PortZ0s),
        AnalysisLevelNames = s.AnalysisLevelNames.Count > 0 ? [.. s.AnalysisLevelNames] : null,
        Mesh = new CemMesh
        {
            MinCellsAcrossWidth = s.Mesh.MinCellsAcrossWidth,
            EdgeCells           = s.Mesh.EdgeCells,
            EdgeFractionOfWidth = s.Mesh.EdgeFractionOfWidth,
            EdgeGrowthRatio     = s.Mesh.EdgeGrowthRatio,
            TruncationHeights   = s.Mesh.TruncationHeights,
            TruncationTailCells = s.Mesh.TruncationTailCells,
        },
        DispersionCorrection  = s.DispersionCorrection,
        AdaptiveSampling      = s.AdaptiveSampling ? null : false,
        DirectVerticalKernel  = s.DirectVerticalKernel ? true : null,
        SnpOutputPathOverride = s.SnpOutputPathOverride is { Length: > 0 } p ? p : null,
        AnalysisKind          = s.AnalysisKind == EmAnalysisKind.Auto ? null : s.AnalysisKind,
        PlanarMesh            = s.PlanarMesh == PlanarMeshSettings.Default ? null : new CemPlanarMesh
        {
            Auto               = s.PlanarMesh.Auto,
            CellsPerWavelength = s.PlanarMesh.CellsPerWavelength,
            EdgeMesh           = s.PlanarMesh.EdgeMesh,
            EdgeCells          = s.PlanarMesh.EdgeCells,
        },
    };

    private static EmSetup FromFileModel(CemFile f) => new()
    {
        Name                   = f.Name,
        LayoutRef              = f.LayoutRef,
        SignalStackupLayerName = f.SignalStackupLayerName ?? "",
        Frequency              = BuildSpec(f.Frequency),
        Port1Z0                = new Complex(f.Port1Z0Real, f.Port1Z0Imag),
        Port2Z0                = new Complex(f.Port2Z0Real, f.Port2Z0Imag),
        PortZ0s                = UnflattenPortZ0s(f.PortZ0s),
        AnalysisLevelNames     = f.AnalysisLevelNames is { } lv ? [.. lv] : [],
        Mesh = new EmMeshSettings(
            f.Mesh.MinCellsAcrossWidth,
            f.Mesh.EdgeCells,
            f.Mesh.EdgeFractionOfWidth,
            f.Mesh.EdgeGrowthRatio,
            f.Mesh.TruncationHeights,
            f.Mesh.TruncationTailCells),
        DispersionCorrection  = f.DispersionCorrection,
        AdaptiveSampling      = f.AdaptiveSampling ?? true,
        DirectVerticalKernel  = f.DirectVerticalKernel ?? false,
        SnpOutputPathOverride = f.SnpOutputPathOverride ?? "",
        AnalysisKind          = f.AnalysisKind ?? EmAnalysisKind.Auto,
        PlanarMesh            = f.PlanarMesh is { } pm
            ? new PlanarMeshSettings(pm.Auto, pm.CellsPerWavelength, pm.EdgeMesh, pm.EdgeCells)
            : PlanarMeshSettings.Default,
    };

    /// <summary>Null for an empty list, so the field is omitted entirely rather than written as [].</summary>
    private static List<double>? FlattenPortZ0s(List<Complex> z)
    {
        if (z.Count == 0) return null;
        var flat = new List<double>(z.Count * 2);
        foreach (var c in z) { flat.Add(c.Real); flat.Add(c.Imaginary); }
        return flat;
    }

    /// <summary>A trailing half-pair is dropped rather than throwing — a hand-edited .cem must
    /// degrade to the near/far defaults, not fail to open.</summary>
    private static List<Complex> UnflattenPortZ0s(List<double>? flat)
    {
        var z = new List<Complex>();
        if (flat is null) return z;
        for (int i = 0; i + 1 < flat.Count; i += 2) z.Add(new Complex(flat[i], flat[i + 1]));
        return z;
    }

    private static FrequencySpec BuildSpec(CemFrequency f)
        => f.Mode == FreqSpecMode.StepSize
            ? new FrequencySpec(f.StartExpr, f.StopExpr, f.StepExpr, f.Kind, f.StartUnit, f.StopUnit, f.StepUnit)
            : new FrequencySpec(f.StartExpr, f.StopExpr, Math.Max(1, f.NumPoints ?? 101), f.Kind, f.StartUnit, f.StopUnit);
}
