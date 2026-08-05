using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §5/R-L5g-11/R-L5g-12 (round 2) and
/// brief-L5-followups-3.md §3/R-L5h-5/6/7 (round 3, the actual fix): "Changing MBend's Miter (0/1/2)
/// does nothing" and "Optimal doesn't look optimal."
///
/// <b>R-L5g-11 ("does changing 0/1/2 even RESOLVE?") — investigated, NOT the cause (round 2, still
/// true).</b> <c>Miter</c>'s default parameter is a plain numeric expression
/// (<c>ComponentTypeRegistry.DefaultParameters(MBend)</c>: <c>new("Miter", "2", "", true,
/// UnitDimension.None)</c>), never an enum/string-typed value at the expression-evaluation layer.
/// <see cref="MiterExpression_ResolvesWithoutError_ForEveryMode"/> and
/// <see cref="ThreeMiterModes_ProduceThreeDistinctGeneratedCells_ResolvedValueReachesTheGenerator"/>
/// still prove this end to end.
///
/// <b>Round 2's finding stands as the correct root cause, but round 2 declined to fix it — round 3
/// fixes it.</b> <c>MBendPCell.BuildMiterCutTriangle</c> computed its "sharp outer corner" as the
/// intersection of the two arms' outer EDGE LINES — but the two arms (each built independently via
/// <c>PCellGeometryHelpers.BuildArmRect</c>, stopping/starting exactly AT the nominal pivot) only
/// overlapped in a halfW×halfW QUARTER of the true W×W corner square, so the computed corner was
/// never actually ON the union's real boundary and the miter cut was a complete no-op for every
/// magnitude. <b>Neither of round 3's own two leading hypotheses (R-L5h-5's "M inverted",
/// R-L5h-6's "missing √2") was actually present</b> — checked directly against
/// <c>MicrostripDiscontinuities.MiterCutLength</c> (<c>src/Core</c>, untouched): it already returns
/// the PER-EDGE LEG length with <c>M</c> correctly interpreted as the fraction REMOVED (≈69% of W at
/// W/h=1, matching R-L5h-5's own worked expectation exactly) — there was no sign inversion and no
/// missing √2 to add (nothing here divides by √2 in the first place: the leg IS X/√2, already what
/// the geometry needs).
///
/// <b>The actual fix (<c>MBendPCell.cs</c>), in two parts:</b> (1) each arm now extends HALF a width
/// past/before the pivot along its own centerline, so the two arms' widths form the real W×W overlap
/// square the corner-intersection math always assumed — this alone made the corner computation land
/// on a real point, but a SECOND bug then surfaced: (2) the cut-point for arm2 used <c>-d2</c> (walk
/// backward from the corner), which — now that arm2's own origin sits BEHIND the nominal corner —
/// walks straight off arm2's real edge into empty space. Pin2 always sits FURTHER along <c>+d2</c>
/// from the corner (by construction, unrelated to the arm-extension fix), so the correct cut point is
/// <c>outer + d2·leg</c>, not <c>outer - d2·leg</c>. Both fixes verified numerically below — not by
/// eye — including a direct cross-check against two independently-fetched microstrip mitred-bend
/// calculators (see <see cref="MBendMiterGeometryTests"/>, the calculator-oracle table
/// R-L5h-6 asks for).
///
/// <b>R-L5h-7's decision:</b> the miter cut is restricted to an EXACT 90° bend
/// (<c>MBendPCell.IsRightAngleBend</c>) — Douville &amp; James's own fit, and the corner-square
/// construction itself, are both right-angle-specific; a non-90° bend with a non-None Miter keeps
/// its unmitered (square-corner) geometry and reports why via <c>PCellResult.Diagnostics</c>, never
/// silently extrapolated.
/// </summary>
public sealed class MBendMiterResolutionTests : IDisposable
{
    private readonly string _root;

    public MBendMiterResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mbend-miter-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static EditableComponent MakeMBend(string instanceName, string miterExpr)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.MBend, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.MBend, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "Miter" ? miterExpr : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void MiterExpression_ResolvesWithoutError_ForEveryMode(string miterExpr)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Bend" + miterExpr);
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMBend("B1", miterExpr));

        var target = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);

        // A resolution FAILURE shows up as a NoLayoutWarnings entry naming the parameter, per
        // ResolveComponentLayout's own resolveWarning contract — assert there is none, i.e. the
        // resolved value genuinely reached the generator (gate 9's "not just that the parameter was
        // set" requirement) rather than silently falling back to a default.
        Assert.Empty(result.NoLayoutWarnings);
        Assert.NotNull(result.Command);
    }

    [Fact]
    public void ThreeMiterModes_ProduceThreeDistinctGeneratedCells_ResolvedValueReachesTheGenerator()
    {
        string[] cellRefs = new string[3];
        for (int i = 0; i < 3; i++)
        {
            string miterExpr = i.ToString();
            string cellDir = CellFolder.CreateCellFolder(_root, "Bend" + i);
            string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            var model = new SchematicEditModel { SchematicDirectory = schematicDir };
            model.Components.Add(MakeMBend("B1", miterExpr));

            var target = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
            var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
            result.Command!.Execute();
            cellRefs[i] = target.Instances[0].CellRef;
        }

        // Distinct CellRefs (content-addressed on parameters, including Miter) prove the resolved
        // Miter value reached GeneratedCellStore.GetOrCreate distinctly per mode — R-L5g-11's own
        // question, answered: resolution is NOT the problem.
        Assert.NotEqual(cellRefs[0], cellRefs[1], StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(cellRefs[1], cellRefs[2], StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(cellRefs[0], cellRefs[2], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R-L5h-5/6 gate 5/9 — was pinned as a KNOWN BUG (byte-identical outlines) through round 2; now
    /// FIXED, and this asserts the fix directly: at the default W=2.9mm/Angle=90° (no technology),
    /// None/Fifty/Optimal produce three genuinely DISTINCT outlines (R-pc-18), and both mitered modes
    /// have strictly MORE vertices than None (a real chamfer adds one vertex where the sharp corner
    /// used to be — 6 → 7 for this specific L-shape).
    /// </summary>
    [Fact]
    public void ThreeMiterModes_ProduceThreeDistinctOutlines_ForARightAngleBend()
    {
        var defaults = new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["Angle"] = 90.0 };
        var none = MBendPCell.Generate(new Dictionary<string, PCellValue>(defaults) { ["Miter"] = 0.0 }, null, PCellLayerSelection.Default);
        var fifty = MBendPCell.Generate(new Dictionary<string, PCellValue>(defaults) { ["Miter"] = 1.0 }, null, PCellLayerSelection.Default);
        var optimal = MBendPCell.Generate(new Dictionary<string, PCellValue>(defaults) { ["Miter"] = 2.0 }, null, PCellLayerSelection.Default);

        var noneXy = ((PolygonShape)none.Shapes[0]).Xy;
        var fiftyXy = ((PolygonShape)fifty.Shapes[0]).Xy;
        var optimalXy = ((PolygonShape)optimal.Shapes[0]).Xy;

        Assert.False(noneXy.SequenceEqual(fiftyXy));
        Assert.False(noneXy.SequenceEqual(optimalXy));
        Assert.False(fiftyXy.SequenceEqual(optimalXy));

        Assert.Equal(6, noneXy.Length / 2);
        Assert.True(fiftyXy.Length / 2 > noneXy.Length / 2);
        Assert.True(optimalXy.Length / 2 > noneXy.Length / 2);

        Assert.Null(none.Diagnostics);
        Assert.Null(fifty.Diagnostics);
        Assert.Null(optimal.Diagnostics);
    }

    /// <summary>Round 3's fix must hold for BOTH turn directions, not only the CCW example worked
    /// through in the class doc comment — a CW (Angle=-90) right-angle bend mitres too.</summary>
    [Fact]
    public void OptimalMiter_ProducesADistinctCutOutline_ForACwRightAngleBendToo()
    {
        var defaults = new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["Angle"] = -90.0 };
        var none = MBendPCell.Generate(new Dictionary<string, PCellValue>(defaults) { ["Miter"] = 0.0 }, null, PCellLayerSelection.Default);
        var optimal = MBendPCell.Generate(new Dictionary<string, PCellValue>(defaults) { ["Miter"] = 2.0 }, null, PCellLayerSelection.Default);

        var noneXy = ((PolygonShape)none.Shapes[0]).Xy;
        var optimalXy = ((PolygonShape)optimal.Shapes[0]).Xy;

        Assert.False(noneXy.SequenceEqual(optimalXy));
        Assert.True(optimalXy.Length / 2 > noneXy.Length / 2);
    }

    /// <summary>R-L5h-7: a non-90° bend with a non-None Miter reports why it was skipped, and its
    /// geometry stays unmitered (never silently extrapolated).</summary>
    [Fact]
    public void ObliqueBend_WithMiterSet_ReportsWhyAndStaysUnmitered()
    {
        var withoutMiter = MBendPCell.Generate(
            new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["Angle"] = 45.0, ["Miter"] = 0.0 }, null, PCellLayerSelection.Default);
        var withMiter = MBendPCell.Generate(
            new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["Angle"] = 45.0, ["Miter"] = 2.0 }, null, PCellLayerSelection.Default);

        var withoutXy = ((PolygonShape)withoutMiter.Shapes[0]).Xy;
        var withXy = ((PolygonShape)withMiter.Shapes[0]).Xy;

        Assert.True(withoutXy.SequenceEqual(withXy)); // no silent extrapolation — geometry unchanged
        Assert.Null(withoutMiter.Diagnostics);
        Assert.NotNull(withMiter.Diagnostics);
        Assert.Contains(withMiter.Diagnostics!, d => d.Contains("90°") && d.Contains("45"));
    }
}
