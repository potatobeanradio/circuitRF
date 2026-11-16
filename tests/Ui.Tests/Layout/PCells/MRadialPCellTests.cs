using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// MRadial artwork: one outline whose far edge is a TRUE arc about the sector's apex, the narrow end W wide on pin 1 at
/// the origin, and the flattened copper the sector less the triangle behind its narrow end, which is what the model
/// simulates; a butterfly is that and its mirror image. A plan hangs it on a tee's branch through the same generator,
/// and its defaults build on a GaAs die.
/// </summary>
public class MRadialPCellTests
{
    private const double W = 1.1e-3, Ro = 8e-3;
    private const int Dbu = 1000;                              // LayoutUnits.DefaultDbuPerMicron

    private static PCellResult Gen(double angleDeg, int sides, double ro = Ro)
    {
        Assert.True(PCellRegistry.TryGet(MRadialPCell.GeneratorId, out var generate));
        return generate(PCellParameters.FromReals(new Dictionary<string, double>
            { ["W"] = W, ["Ro"] = ro, ["Angle"] = angleDeg, ["Sides"] = sides }), null, PCellLayerSelection.Default);
    }

    private static double Area(IReadOnlyList<long[]> rings)
    {
        double a = 0;
        foreach (var xy in rings)
        {
            int n = xy.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                a += (double)xy[2 * i] * xy[2 * j + 1] - (double)xy[2 * j] * xy[2 * i + 1];
            }
        }
        return Math.Abs(a) / 2;
    }

    /// <summary>The sector of radius Ro less the triangle between its apex and its narrow end, in DBU².</summary>
    private static double SectorArea(double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0, ro = Ro * 1e6 * Dbu;
        double ri = MicrostripRadialStub.InnerRadius(W, angleDeg) * 1e6 * Dbu;
        return a / 2.0 * ro * ro - 0.5 * ri * ri * Math.Sin(a);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    public void OneSector_IsTheSectorPastItsNarrowEnd_OnAPinFacingTheFeed(double angleDeg)
    {
        var res = Gen(angleDeg, 1);
        Assert.Null(res.Diagnostics);
        var curve = Assert.IsType<CurveShape>(Assert.Single(res.Shapes));
        var arc = Assert.Single(curve.Edges!, e => e.Kind == EdgeKind.Arc);
        Assert.Equal(Math.Tan(angleDeg * Math.PI / 720.0), arc.Bulge, 12);
        Assert.Equal((0L, -550_000L, 0L, 550_000L), (curve.Xy[0], curve.Xy[1], curve.Xy[6], curve.Xy[7]));

        var pin = Assert.Single(res.Pins);
        Assert.Equal(("1", 0L, 0L, 1_100_000L, 180.0), (pin.Name, pin.X, pin.Y, pin.WidthDbu, pin.OutwardDirectionDeg));

        double area = Area(LayoutFlattener.Flatten(curve, tolDbu: 100));   // 0.1 um chord tolerance
        Assert.InRange(area / SectorArea(angleDeg), 0.999, 1.0001);
    }

    [Fact]
    public void AButterfly_IsTheSectorAndItsMirror_InOneOutline_WithThePinBetweenThem()
    {
        var res = Gen(60.0, 2);
        Assert.Null(res.Diagnostics);
        var curve = Assert.IsType<CurveShape>(Assert.Single(res.Shapes));
        Assert.Equal(2, curve.Edges!.Count(e => e.Kind == EdgeKind.Arc));
        Assert.Equal(-curve.Xy[2], curve.Xy[10]);                          // the mirror arc's ends
        Assert.Equal(270.0, Assert.Single(res.Pins).OutwardDirectionDeg);

        double area = Area(LayoutFlattener.Flatten(curve, tolDbu: 100));
        Assert.InRange(area / (2.0 * SectorArea(60.0)), 0.999, 1.0001);
    }

    [Fact]
    public void ARadiusShortOfTheFeedsWidth_IsReported_AndNothingIsDrawn()
    {
        var res = Gen(60.0, 1, ro: 1e-3);                                  // r_i is W = 1.1 mm at 60 deg
        Assert.Empty(res.Shapes);
        Assert.Contains(res.Diagnostics!, d => d.Contains("Lengthen Ro", StringComparison.Ordinal));
    }

    /// <summary>A plan hangs the stub on a tee's branch: its narrow end lands on the branch's end, edge to edge, and it
    /// fans out away from the through line.</summary>
    [Fact]
    public void APlanHangsTheStubOnATeesBranch_NarrowEndOnTheBranchEnd()
    {
        const double l = 1.1e-3;
        var plan = new PassivePlan(["1", "2"],
        [
            new PlanTee(W, W, W, l, l, l, [1, 2, 3], 0.0, 0.0, 0.0),
            new PlanRadialStub([3], l, -l, 270.0, W, Ro, 60.0),
        ], 3);
        var art = PassivePlanPCells.Draw(plan, null, PCellLayerSelection.Default);
        Assert.Null(art.Diagnostics);
        Assert.Equal(["1", "2"], art.Pins.Select(p => p.Name));

        var stub = Assert.Single(art.Shapes.OfType<CurveShape>());
        long x0 = 1_100_000, y0 = -1_100_000, hw = 550_000;
        Assert.Equal((x0 - hw, y0, x0 + hw, y0), (stub.Xy[0], stub.Xy[1], stub.Xy[6], stub.Xy[7]));
        Assert.True(stub.Xy.Where((_, i) => i % 2 == 1).Max() <= y0);
        var tee = MTeePCell.Generate(new Dictionary<string, PCellValue>
            { ["W1"] = W, ["W2"] = W, ["W3"] = W, ["L1"] = l, ["L2"] = l, ["L3"] = l }, null, PCellLayerSelection.Default);
        var branch = tee.Pins.Single(p => p.Name == "3");
        Assert.Equal((x0, y0, 270.0), (branch.X, branch.Y, branch.OutwardDirectionDeg));
    }

    /// <summary>Placed on the GaAs die, the defaults are the die's own 50 Ω feed and ten of its widths of radius: the
    /// cell draws with no finding, and the model builds on the die's substrate and shorts the line in the tens of GHz,
    /// inside its stated range.</summary>
    [Fact]
    public void OnAGaAsDie_TheDefaultsDrawAndSimulate()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var rows = ComponentTypeRegistry.DefaultParameters(SymbolKind.MRadial, 1)
            .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension })
            .ToList();
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(rows, tech, SymbolKind.MRadial);
        double Si(string name)
        {
            var r = rows.Single(q => q.Name == name);
            double scale = r.Unit switch { "µm" => 1e-6, "mm" => 1e-3, "mil" => 25.4e-6, _ => throw new InvalidOperationException(r.Unit) };
            return double.Parse(r.Expression, CultureInfo.InvariantCulture) * scale;
        }
        double w = Si("W"), ro = Si("Ro");
        Assert.InRange(ro / w, 9.5, 10.5);

        var art = MRadialPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = w, ["Ro"] = ro, ["Angle"] = 60.0, ["Sides"] = 1.0 },
            tech, PCellLayerSelection.Default);
        Assert.Null(art.Diagnostics);
        Assert.Single(art.Shapes);

        var (s, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.NotNull(s);
        var model = new MicrostripRadialStubModel(w, ro, 60.0, 1,
            new MicrostripSubstrate(s!.HeightMeters, s.ThicknessMeters, s.RelativePermittivity, s.ConductivitySPerM,
                s.LossTangent, s.RoughnessRmsMeters), "S1");
        // The series resonance: where the stub's impedance crosses zero from capacitive to inductive.
        double? f0 = null;
        for (double f = 5e9; f < 150e9 && f0 is null; f += 0.25e9)
            if ((1.0 / model.Admittance(f)).Imaginary < 0 && (1.0 / model.Admittance(f + 0.25e9)).Imaginary >= 0) f0 = f;
        Assert.NotNull(f0);
        Assert.InRange(f0!.Value, 10e9, 100e9);
        Assert.Empty(model.DrainWarnings());
    }
}
