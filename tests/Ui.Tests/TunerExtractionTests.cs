using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 01 gate: the general <see cref="SymbolKind.Tuner"/> component extracts
/// to an engine Instance with Reference "Tuner" and LOAD-STYLE net ordering — its single
/// DUT-facing pin → Nodes[0], and a hard-coded ground "0" → Nodes[1] (the reference net is
/// implicit ground, not a pin; loadpull.md §1). Mirrors the P1Tone extraction test.
/// </summary>
public class TunerExtractionTests
{
    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    // Builds a Tuner with its registry default parameters seeded (mirrors placement).
    private static EditableComponent Tuner(string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Tuner, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Tuner, 0))
            c.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                  ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        return c;
    }

    // A Tuner whose single DUT-facing pin sits on a wire net labeled "n_dut".
    private static SchematicEditModel ModelWithLabeledTuner(out EditableComponent tuner)
    {
        var model = new SchematicEditModel();
        // Tuner pin "1" is at local (-300, 0). Place the Tuner at (300, 0) so the pin lands at
        // world (0, 0). A wire runs through (0,0); a mid-segment "n_dut" label names that net.
        tuner = Tuner("Tuner1", 300, 0);
        model.Components.Add(tuner);
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "n_dut" });
        return model;
    }

    private static Instance TunerInstance(NetExtractor.ExtractionResult r)
        => r.TestBench.Instances.First(i => i.InstanceName == "Tuner1");

    // ── Test 1: Reference + exactly two nets in load-style order ──────────────

    [Fact]
    public void Tuner_Extracts_Reference_And_TwoNets_LoadStyle()
    {
        var model = ModelWithLabeledTuner(out _);

        var result = NetExtractor.Extract(model);
        var inst   = TunerInstance(result);

        Assert.Equal("Tuner", inst.Reference);
        // Exactly two declared nets: [Nodes0 = DUT-facing pin net, Nodes1 = ground "0"].
        Assert.Equal(2, inst.NetBindings.Count);
        Assert.Equal("n_dut", inst.NetBindings[0]);
        Assert.Equal("0",     inst.NetBindings[1]);
        Assert.Null(inst.RefNetBinding);
    }

    // ── Test 2: default parameters survive as unit-normalized assignments ─────

    [Fact]
    public void Tuner_Extracts_DefaultParameters_UnitNormalized()
    {
        var model = ModelWithLabeledTuner(out _);

        var result = NetExtractor.Extract(model);
        var inst   = TunerInstance(result);

        var z1 = inst.Overrides.Single(o => o.Name == "Z[1]");
        Assert.Equal("50", z1.Expression);
        Assert.Equal("Ohm", z1.Unit);   // Ω → Ohm at the extraction boundary

        Assert.Equal("1e-6", inst.Overrides.Single(o => o.Name == "Zdefault").Expression);
        Assert.Equal("Ohm",  inst.Overrides.Single(o => o.Name == "Zdefault").Unit);
        Assert.Equal("50",   inst.Overrides.Single(o => o.Name == "Z0").Expression);

        var biasTee = inst.Overrides.Single(o => o.Name == "BiasTee");
        Assert.Equal("off", biasTee.Expression);
        Assert.True(string.IsNullOrEmpty(biasTee.Unit));   // dimensionless

        Assert.Equal("0", inst.Overrides.Single(o => o.Name == "Vbias").Expression);
    }

    // ── Test 3: a user-added Z[2] (the "+" path) round-trips into the netlist ──

    [Fact]
    public void Tuner_UserAddedZ2_RoundTrips()
    {
        var model = ModelWithLabeledTuner(out var tuner);
        // The parameter-editor "+" adds Z[2] (FirstAddIndex = 2, the first index past the
        // fundamental Z[1] row). Emulate that by appending the row directly.
        tuner.Parameters.Add(new EditableParameter
            { Name = "Z[2]", Expression = "1", Unit = "Ω", ShowOnSchematic = true,
              Dimension = UnitDimension.Resistance });

        var result = NetExtractor.Extract(model);
        var inst   = TunerInstance(result);

        var z2 = inst.Overrides.Single(o => o.Name == "Z[2]");
        Assert.Equal("1", z2.Expression);
        Assert.Equal("Ohm", z2.Unit);
    }

    // ── Test 4: extracted Tuner round-trips through .cnl and elaborates ───────

    [Fact]
    public void Tuner_RoundTripsThroughCnl_AndElaborates()
    {
        var model = ModelWithLabeledTuner(out _);

        // Production run path: extract → CnlWriter → CnlReader → Elaborate. The reader quotes
        // BiasTee so the elaborator sees a string value; the factory recognizes "Tuner".
        var extracted = NetExtractor.Extract(model);
        var cnl        = CnlWriter.Write(extracted.TestBench);
        var (lib, tb)  = new CnlReader().Read(cnl);

        var netlist = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(netlist.Components,
            c => c.ComponentType.Equals("Tuner", System.StringComparison.OrdinalIgnoreCase));
    }
}
