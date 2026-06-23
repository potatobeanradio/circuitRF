using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 03 gate: the display-only ShowBias toggle draws the embedded bias-tee glyph but
/// NEVER reaches the engine — the extracted Instance is identical regardless of ShowBias, and the
/// per-instance glyph grows downward when ShowBias is on.
/// </summary>
public class TunerPolishTests
{
    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Tuner(string name, double x, double y, bool showBias)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Tuner, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Tuner, 0))
            c.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Name == "ShowBias" ? (showBias ? "true" : "false") : dp.Expression,
                  Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        // BiasTee on so ShowBias is meaningful.
        c.Parameters.First(p => p.Name == "BiasTee").Expression = "on";
        return c;
    }

    private static Instance ExtractSingle(EditableComponent tuner)
    {
        var model = new SchematicEditModel();
        model.Components.Add(tuner);
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "n_dut" });
        return NetExtractor.Extract(model).TestBench.Instances.First();
    }

    // ── ShowBias is display-only: never emitted as a ParameterAssignment ──────

    [Fact]
    public void ShowBias_NotEmittedToEngine()
    {
        var inst = ExtractSingle(Tuner("Tuner1", 300, 0, showBias: true));

        Assert.DoesNotContain(inst.Overrides, o => o.Name == "ShowBias");
    }

    // ── Extracted Instance is identical regardless of ShowBias ────────────────

    [Fact]
    public void Extraction_Identical_RegardlessOfShowBias()
    {
        var withBias    = ExtractSingle(Tuner("Tuner1", 300, 0, showBias: true));
        var withoutBias = ExtractSingle(Tuner("Tuner1", 300, 0, showBias: false));

        Assert.Equal(withoutBias.Reference, withBias.Reference);
        Assert.Equal(withoutBias.NetBindings.ToArray(), withBias.NetBindings.ToArray());

        static Dictionary<string, (string, string?)> Params(Instance i) =>
            i.Overrides.ToDictionary(o => o.Name, o => (o.Expression, o.Unit));
        Assert.Equal(Params(withoutBias), Params(withBias));
    }

    // ── ShowBias extends the glyph downward (per-instance variant is wired) ───

    [Fact]
    public void ShowBias_ExtendsGlyphDownward()
    {
        var off = Tuner("Tuner1", 0, 0, showBias: false).ToRenderComponent();
        var on  = Tuner("Tuner1", 0, 0, showBias: true).ToRenderComponent();

        // The bias-tee add-on is drawn beneath the box, so the glyph's bottom edge moves down.
        Assert.True(on.GlyphBbMaxY > off.GlyphBbMaxY,
            $"expected ShowBias glyph (maxY={on.GlyphBbMaxY}) to extend below the no-bias glyph (maxY={off.GlyphBbMaxY})");
    }

    // ── Labels clear the (taller) bias glyph ──────────────────────────────────

    [Fact]
    public void LabelBaseY_ClearsBiasGlyph()
    {
        var on = Tuner("Tuner1", 0, 0, showBias: true).ToRenderComponent();
        double glyphHalfH = on.GlyphBbMaxY - on.Y;
        double labelBaseY = SchematicComponent.LabelBaseYFor(SymbolKind.Tuner, 1, glyphHalfH);

        // First label row sits below the glyph bottom (with at least one line-step of clearance).
        Assert.True(labelBaseY >= glyphHalfH + SchematicComponent.LabelWorldStep);
    }
}
