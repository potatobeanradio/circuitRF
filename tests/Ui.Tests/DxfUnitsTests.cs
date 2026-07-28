using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate 8 (R-L4b-4) — $INSUNITS = 4 imports as mm; 13 as µm; absent PROMPTS rather than
/// assuming; the chosen interpretation is reported. A finer-than-DBU source warns with a count.</summary>
public class DxfUnitsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dxf-units-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string BuildDxf(int insUnits, long x1, long y1, long x2, long y2)
    {
        var structures = new List<InterchangeStructure>
        {
            new("TOP", [new RectShape { Layer = new LayerKey(1, 0), X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 }], []),
        };
        using var sw = new StringWriter();
        DxfWriter.Write(sw, structures, "TOP", null, 1000, new DxfExportOptions { InsUnits = insUnits });
        return sw.ToString();
    }

    private static Stream ToStream(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void InsUnits_Millimeters_ImportsAsMm_NoPromptNeeded()
    {
        string text = BuildDxf(DxfUnits.Millimeters, 0, 0, 1_000_000, 1_000_000); // 1mm square
        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("millimeters", StringComparison.OrdinalIgnoreCase));

        var topDir = Path.Combine(_dir, result.CellNameByBlockName["TOP"]);
        var layoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, $"{result.CellNameByBlockName["TOP"]}.clay"));
        var poly = Assert.IsType<PolygonShape>(Assert.Single(view.Shapes)); // DXF has no native Rect primitive
        Assert.Equal(1_000_000, poly.Xy[4] - poly.Xy[0]); // 1mm = 1,000,000 DBU at 1000 DBU/um
    }

    [Fact]
    public void InsUnits_Microns_ImportsAsMicrons()
    {
        string text = BuildDxf(DxfUnits.Microns, 0, 0, 5000, 5000); // 5 um square (DBU already at 1000/um)
        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);

        var topDir = Path.Combine(_dir, result.CellNameByBlockName["TOP"]);
        var layoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, $"{result.CellNameByBlockName["TOP"]}.clay"));
        var poly = Assert.IsType<PolygonShape>(Assert.Single(view.Shapes));
        Assert.Equal(5000, poly.Xy[4] - poly.Xy[0]);
    }

    [Fact]
    public void InsUnits_Absent_PromptsRatherThanAssuming()
    {
        string text = BuildAbsentUnitsDxf();
        bool promptWasCalled = false;
        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000, resolveUnits: raw =>
        {
            promptWasCalled = true;
            Assert.Equal(0, raw);
            return DxfUnits.Millimeters;
        });

        Assert.True(promptWasCalled);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("absent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InsUnits_Absent_NoPromptCallback_DefaultsToMm_AndReportsIt()
    {
        string text = BuildAbsentUnitsDxf();
        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("millimeters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InsUnits_Absent_PromptReturnsNull_AbortsImport_NothingCreated()
    {
        string text = BuildAbsentUnitsDxf();
        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000, resolveUnits: _ => null);
        Assert.True(result.Cancelled);
        Assert.Empty(Directory.GetDirectories(_dir));
    }

    [Fact]
    public void FinerSourceResolution_WarnsWithAffectedCoordinateCount()
    {
        // Destination DBU/micron is coarse (10) relative to a source coordinate that lands on a
        // fraction of a DBU at that resolution.
        string text = BuildDxf(DxfUnits.Millimeters, 0, 0, 1_000_003, 1_000_000);
        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 10);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("coordinate(s) will round", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>DXF with no <c>$INSUNITS</c> header var at all (not merely 0) — a plausible real-world
    /// file from a minimal writer.</summary>
    private static string BuildAbsentUnitsDxf()
    {
        var structures = new List<InterchangeStructure>
        {
            new("TOP", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 1_000_000 }], []),
        };
        using var sw = new StringWriter();
        DxfWriter.Write(sw, structures, "TOP", null, 1000, new DxfExportOptions());
        string full = sw.ToString();
        // Strip the $INSUNITS var + its value line to simulate a minimal/absent-units writer.
        var lines = full.Split('\n').ToList();
        int idx = lines.IndexOf("$INSUNITS");
        Assert.True(idx > 0);
        // Remove the full 4-line block: "9" / "$INSUNITS" / "70" / "<value>", starting one line before idx.
        lines.RemoveRange(idx - 1, 4);
        return string.Join('\n', lines);
    }
}
