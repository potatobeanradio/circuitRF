// Owner: PCD-15. A part's angle stated as a cell parameter, found on the Wilkinson3 rebuild: the layout reads it in the unit the
// circuit model does (radians in SI, degrees for the generator) with or without a unit in the text, and LVS on the cell's own
// layout does not report the round-off of that conversion as a mismatch.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd15AngleParameterTests : IDisposable
{
    private readonly string _ws = Pcd9Cells.Workspace(Path.Combine(Path.GetTempPath(), "crf-pcd15a-" + Guid.NewGuid().ToString("N")[..12]));

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>`Theta=Th` and `Theta=Th deg` (Th a cell parameter in deg) plan the same copper, and D3's straight run is Th/20 of
    /// DS's (both straight MDLYs of one line at F, DS 20°).</summary>
    [Fact]
    public void AnAngleFromACellParameter_PlansTheSameWithOrWithoutItsUnit()
    {
        var bare = Pcd9Cells.Wilkinson with { Cell = "Wilkinson3Bare", Text = Pcd9Cells.Wilkinson.Text.Replace("Theta=Th deg", "Theta=Th") };
        Assert.NotEqual(Pcd9Cells.Wilkinson.Text, bare.Text);

        var (withUnit, withDir) = Plan(Pcd9Cells.Wilkinson);
        var (without, withoutDir) = Plan(bare);
        Assert.Equal(Geometry(withUnit, withDir), Geometry(without, withoutDir));

        double ratio = (double)Pcd6Fixture.Length(Pcd6Fixture.Box(without, withoutDir, "D3"))
                     / Pcd6Fixture.Length(Pcd6Fixture.Box(without, withoutDir, "DS"));
        Assert.InRange(ratio, 49.0 / 20 * 0.98, 49.0 / 20 * 1.02);
    }

    /// <summary>Th = 48°: LVS on the cell's own layout is clean (the angle's degree/radian round trip is not a mismatch).</summary>
    [Fact]
    public void AnAngleThatRoundTripsBadly_IsLvsCleanOnItsOwnLayout()
    {
        var spec = Pcd9Cells.Wilkinson with
        {
            Cell = "Wilkinson3At48",
            Parameters = [.. Pcd9Cells.Wilkinson.Parameters.Select(p => p.Name == "Th" ? p with { Default = "48" } : p)],
        };
        string cell = Pcd9Cells.Design(_ws, spec);
        using (GeneratedCellsRun.Prepare(Path.Combine(cell, "layout", spec.Cell + ".clay")))
        {
            var lvs = CircuitRF.Design.Layout.Lvs.LvsRun.Run(cell);
            Assert.True(lvs.ErrorCount == 0, string.Join("\n", lvs.Findings.Where(f => f.Severity == DiagnosticSeverity.Error)
                                                                        .Select(f => $"{f.Id}: {f.Diagnostic}")));
        }
    }

    /// <summary>`Style="Straight"`, the netlist's quoted form of a word, plans the same copper as `Style=Straight`, and both
    /// straight MDLYs keep their two pins (the quotes once reached the generator, which drew a pinless meander).</summary>
    [Fact]
    public void AQuotedWord_PlansTheSameAsTheBareWord()
    {
        var quoted = Pcd9Cells.Wilkinson with { Cell = "Wilkinson3Quoted", Text = Pcd9Cells.Wilkinson.Text.Replace("Style=Straight", "Style=\"Straight\"") };
        Assert.NotEqual(Pcd9Cells.Wilkinson.Text, quoted.Text);

        var (bare, bareDir) = Plan(Pcd9Cells.Wilkinson);
        var (inQuotes, quotedDir) = Plan(quoted);
        Assert.Equal(Geometry(bare, bareDir), Geometry(inQuotes, quotedDir));
        foreach (string id in new[] { "DS", "D3" })
        {
            var inst = inQuotes.Instances.Single(i => i.SchematicId == id);
            Assert.Equal(2, CellLayoutResolver.Resolve(inst.CellRef, quotedDir).View!.Pins.Count);
        }
    }

    /// <summary>`Theta=(90-38*F/2.45e9) deg` is −36.4 rad by the unit rule (expressions.md §8: F carries a unit, so only the 90 is
    /// read in degrees). The circuit model refuses it, and the layout now refuses the same parts with the same sentence instead
    /// of placing three MDLYs with no pins.</summary>
    [Fact]
    public void ANegativeTheta_IsRefusedOnBothSides_NotDrawnWithoutPins()
    {
        const string Refusal = "Delay line: θ = -36.4292036732051 must be positive.";
        var sym = Pcd9Cells.WilkinsonSym;
        var deg = sym with { Cell = "Wilkinson3SymDeg", Text = sym.Text.Replace("Theta=(90-38*F/2.45e9)*pi/180", "Theta=(90-38*F/2.45e9) deg") };
        Assert.NotEqual(sym.Text, deg.Text);

        string cell = Pcd9Cells.Design(_ws, deg);
        var tech = TechPersistence.LoadFromFile(Path.Combine(_ws, "tech", Pcd8PadTee.Tech));
        var at = DesignedCellLayout.At(cell, new Dictionary<string, Value>(), _ws, tech, null);
        Assert.True(at.Layout is not null, at.Refusal);
        foreach (string id in new[] { "H2", "H3", "H4" })
        {
            Assert.DoesNotContain(at.Layout!.Instances, i => i.SchematicId == id);
            Assert.Contains(at.Lines, l => l.Text.Contains(id, StringComparison.Ordinal) && l.Text.Contains(Refusal, StringComparison.Ordinal));
        }

        var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read("""
            define CELLX ( a b )
            parameters  F=2.45 GHz
            MDLY:H1 a b Z0=50 Ohm F=F Theta=(90-38*F/2.45e9) deg Style=Straight
            end CELLX
            CELLX:X1 n1 n2
            Port:P1 n1 0 Num=1 Z=50 Ohm
            Port:P2 n2 0 Num=2 Z=50 Ohm
            """);
        var refused = Assert.ThrowsAny<Exception>(() => new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb));
        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
    }

    private (CircuitRF.Design.Layout.LayoutView Layout, string LayoutDir) Plan(Pcd9Spec spec)
    {
        string cell = Pcd9Cells.Design(_ws, spec);
        var tech = TechPersistence.LoadFromFile(Path.Combine(_ws, "tech", Pcd8PadTee.Tech));
        var at = DesignedCellLayout.At(cell, new Dictionary<string, Value>(), _ws, tech, null);
        Assert.True(at.Layout is not null, at.Refusal);
        return (at.Layout!, Path.Combine(cell, "layout"));
    }

    private static string Geometry(CircuitRF.Design.Layout.LayoutView layout, string layoutDir)
        => string.Join("\n", layout.Instances.OrderBy(i => i.SchematicId, StringComparer.Ordinal).Select(i =>
               $"{i.SchematicId}@{i.X},{i.Y} r{i.RotationDegrees} m{i.MirrorX} {CellHierarchy.InstanceBbox(i, layoutDir)}"));
}
