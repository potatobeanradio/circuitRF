using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 02 gate: SourceTuner / LoadTuner are the SAME engine component as the general
/// Tuner (EngineReference "Tuner", identical params) — differing only by glyph and instance prefix.
/// All three emit identical net bindings [pin → Nodes[0] (DUT-facing), "0" → Nodes[1] (ground ref)];
/// the SourceTuner's internal RF-drive node is minted by the engine (__tuner_&lt;inst&gt;_outer), so the
/// schematic surface and net ordering are symmetric across the family.
/// </summary>
public class SourceLoadTunerExtractionTests
{
    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent TunerKind(SymbolKind kind, string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            c.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                  ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        return c;
    }

    private static Instance InstanceNamed(NetExtractor.ExtractionResult r, string name)
        => r.TestBench.Instances.First(i => i.InstanceName == name);

    // ── Test 1: LoadTuner ≡ general Tuner (the equivalence proof) ─────────────

    [Fact]
    public void LoadTuner_Equivalent_To_GeneralTuner()
    {
        // Both pins are on the LEFT at local (-300,0). Place at (300,0) so the pin lands on
        // world (0,0), connected to a wire net labeled "n_dut".
        var model = new SchematicEditModel();
        model.Components.Add(TunerKind(SymbolKind.Tuner,     "Tuner1",     300, 0));
        model.Components.Add(TunerKind(SymbolKind.LoadTuner, "LoadTuner1", 300, 0));
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "n_dut" });

        var result = NetExtractor.Extract(model);
        var gen    = InstanceNamed(result, "Tuner1");
        var load   = InstanceNamed(result, "LoadTuner1");

        // Identical engine reference and net bindings (instance name is the only difference).
        Assert.Equal("Tuner", gen.Reference);
        Assert.Equal("Tuner", load.Reference);
        Assert.Equal(new[] { "n_dut", "0" }, gen.NetBindings.ToArray());
        Assert.Equal(new[] { "n_dut", "0" }, load.NetBindings.ToArray());

        // Identical parameter assignments (name → expression+unit).
        static Dictionary<string, (string, string?)> Params(Instance i) =>
            i.Overrides.ToDictionary(o => o.Name, o => (o.Expression, o.Unit));
        Assert.Equal(Params(gen), Params(load));
    }

    // ── Test 2: SourceTuner emits load-style [pin, "0"] (internal drive node minted by engine) ──

    [Fact]
    public void SourceTuner_LoadStyleOrdering_GroundReference()
    {
        // SourceTuner pin is on the RIGHT at local (+300,0). Place at (-300,0) so the pin lands on
        // world (0,0), connected to a wire net labeled "n_gate".
        var model = new SchematicEditModel();
        model.Components.Add(TunerKind(SymbolKind.SourceTuner, "SourceTuner1", -300, 0));
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "n_gate" });

        var result = NetExtractor.Extract(model);
        var src    = InstanceNamed(result, "SourceTuner1");

        Assert.Equal("Tuner", src.Reference);
        // Symmetric with LoadTuner: Nodes[0] = pin (DUT-facing), Nodes[1] = "0" (ground reference).
        // The RF-drive node is minted internally (__tuner_<inst>_outer), not declared here.
        Assert.Equal(new[] { "n_gate", "0" }, src.NetBindings.ToArray());
    }

    // ── Test 3: every SourceTuner is net-identical (shared symmetric ordering) ─

    [Fact]
    public void TwoSourceTuners_BothGroundReferenced()
    {
        var model = new SchematicEditModel();
        // Two independent SourceTuners on separate (unconnected) nets.
        model.Components.Add(TunerKind(SymbolKind.SourceTuner, "SourceTuner1", -300, 0));
        model.Components.Add(TunerKind(SymbolKind.SourceTuner, "SourceTuner2", -300, 1000));
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.Wires.Add(Wire((0, 1000), (0, 1400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200,  Name = "g1" });
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 1200, Name = "g2" });

        var result = NetExtractor.Extract(model);
        var s1 = InstanceNamed(result, "SourceTuner1");
        var s2 = InstanceNamed(result, "SourceTuner2");

        // Distinct DUT nets, both ground-referenced; no per-instance internal net leaks into bindings.
        Assert.Equal(new[] { "g1", "0" }, s1.NetBindings.ToArray());
        Assert.Equal(new[] { "g2", "0" }, s2.NetBindings.ToArray());
    }
}
