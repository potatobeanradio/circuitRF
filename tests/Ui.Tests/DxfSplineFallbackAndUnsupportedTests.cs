using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 9 - a degree-5 or rational spline imports flattened, with the entity handle and reason
/// reported; a degree-3 non-rational one imports as exact cubics (covered directly in
/// DxfBulgeAndCurveRoundTripTests; here we hand-craft the raw DXF text so a genuinely non-Bezier-form
/// / higher-degree spline is exercised, which our own writer never produces).
/// Gate 10 - unsupported entities are reported by type with counts and nothing is silently dropped; a
/// file of entirely unsupported content imports as an empty cell with a clear report, not an error.
/// </summary>
public class DxfSplineFallbackAndUnsupportedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dxf-spline-unsupported-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string MinimalDxfWrapping(Action<DxfGroupWriter> writeEntities)
    {
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "SECTION");
        w.WriteString(2, "HEADER");
        w.WriteString(9, "$INSUNITS");
        w.WriteInt(70, DxfUnits.Millimeters);
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION");
        w.WriteString(2, "ENTITIES");
        writeEntities(w);
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "EOF");
        return sw.ToString();
    }

    private static Stream ToStream(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Degree5NonBezierSpline_ImportsFlattened_ReportsHandleAndReason()
    {
        string text = MinimalDxfWrapping(w =>
        {
            w.WriteString(0, "SPLINE");
            w.WriteString(8, "0");
            w.WriteString(5, "1A2B");
            w.WriteInt(70, 0);
            w.WriteInt(71, 5); // degree 5 - not our own writer's degree-3 form
            int[] ctrlXs = [0, 100, 200, 300, 400, 500];
            int numKnots = ctrlXs.Length + 5 + 1;
            w.WriteInt(72, numKnots);
            w.WriteInt(73, ctrlXs.Length);
            w.WriteInt(74, 0);
            for (int i = 0; i < numKnots; i++) w.WriteDouble(40, i);
            foreach (var x in ctrlXs) { w.WriteDouble(10, x); w.WriteDouble(20, 0.0); }
        });

        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("1A2B") && m.Contains("degree 5", StringComparison.OrdinalIgnoreCase));

        var modelDir = Path.Combine(_dir, result.CellNameByBlockName[DxfReader.ModelSpaceName]);
        var layoutDir = CellFolder.SubFolderPath(modelDir, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, $"{result.CellNameByBlockName[DxfReader.ModelSpaceName]}.clay"));
        Assert.Single(view.Shapes); // approximated, not dropped
    }

    [Fact]
    public void UnsupportedEntities_ReportedByTypeWithCounts_NeverSilentlyDropped()
    {
        string text = MinimalDxfWrapping(w =>
        {
            w.WriteString(0, "DIMENSION");
            w.WriteString(8, "0");
            w.WriteString(0, "DIMENSION");
            w.WriteString(8, "0");
            w.WriteString(0, "LEADER");
            w.WriteString(8, "0");
        });

        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("2 unsupported DIMENSION"));
        Assert.Contains(result.Messages, m => m.Contains("1 unsupported LEADER"));
    }

    [Fact]
    public void EntirelyUnsupportedContent_ImportsAsEmptyCell_NotAnError()
    {
        string text = MinimalDxfWrapping(w =>
        {
            w.WriteString(0, "DIMENSION");
            w.WriteString(8, "0");
        });

        using var stream = ToStream(text);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);
        Assert.False(result.Cancelled);
        Assert.Single(result.CreatedCellDirs);

        var modelDir = Path.Combine(_dir, result.CellNameByBlockName[DxfReader.ModelSpaceName]);
        var layoutDir = CellFolder.SubFolderPath(modelDir, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, $"{result.CellNameByBlockName[DxfReader.ModelSpaceName]}.clay"));
        Assert.Empty(view.Shapes);
        Assert.Contains(result.Messages, m => m.Contains("unsupported DIMENSION"));
    }

    [Fact]
    public void BinaryDxf_IsRefusedClearly_NotMisparsed()
    {
        // Out-of-scope statement: binary DXF must be reported and refused clearly, not silently
        // misparsed as garbage ASCII.
        byte[] sentinel = System.Text.Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n\0");
        using var stream = new MemoryStream(sentinel);
        var result = DxfImport.Import(stream, _dir, null, destDbuPerMicron: 1000);

        Assert.True(result.Cancelled);
        Assert.Contains(result.Messages, m => m.Contains("binary", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetDirectories(_dir));
    }
}
