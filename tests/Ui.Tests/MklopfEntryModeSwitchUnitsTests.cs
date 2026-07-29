using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported: switching MKlopf from Z1/Z2 to W1/W2 (or L to F3db and back) always wrote the
/// new W1/W2/L parameters in "mm", ignoring the workspace technology's own length-display
/// convention (mil on a PCB board, µm on an MMIC die) — the SAME convention a freshly-placed
/// microstrip component's own defaults already respect
/// (<see cref="MicrostripSubstrateInjection.ApplyTechnologyLengthUnit"/>). Exercised through a REAL
/// temp workspace (.cws + .ctech on disk), mirroring
/// <see cref="MicrostripSubstrateInjectionTests"/>'s own end-to-end pattern, so the ancestor-.cws
/// walk is genuinely exercised rather than assumed.
/// </summary>
public class MklopfEntryModeSwitchUnitsTests : IDisposable
{
    private readonly string _root;

    public MklopfEntryModeSwitchUnitsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mklopf-units-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteWorkspaceWithTech(Technology tech)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);

        var cws = new CwsFile { DefaultTechRef = "tech/t.ctech" };
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), cws);

        var schematicDir = Path.Combine(_root, "Amp", "schematic");
        Directory.CreateDirectory(schematicDir);
        return schematicDir;
    }

    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeMklopf(
        string? schematicDirectory, params (string Name, string Expr, string Unit)[] extraParams)
    {
        var model = new SchematicEditModel { SchematicDirectory = schematicDirectory };
        var comp = new EditableComponent { Symbol = SymbolKind.Mklopf, InstanceName = "MK1", X = 0, Y = 0 };
        foreach (var (name, expr, unit) in extraParams)
            comp.Parameters.Add(new EditableParameter { Name = name, Expression = expr, Unit = unit, ShowOnSchematic = true });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    // ── Z1/Z2 -> W1/W2 respects the workspace's own length unit ─────────────────────────────────

    [Fact]
    public void SwitchingToWidthEntry_OnPcbWorkspace_WritesMilNotMm()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, comp, editor) = MakeMklopf(schematicDir,
            ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null);

        var w1 = comp.Parameters.Single(p => p.Name == "W1");
        var w2 = comp.Parameters.Single(p => p.Name == "W2");
        Assert.Equal("mil", w1.Unit);
        Assert.Equal("mil", w2.Unit);
        Assert.NotEqual("mm", w1.Unit);
    }

    [Fact]
    public void SwitchingToWidthEntry_OnMmicWorkspace_WritesMicronsNotMm()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.MmicGaAs());
        var (_, comp, editor) = MakeMklopf(schematicDir,
            ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null);

        var w1 = comp.Parameters.Single(p => p.Name == "W1");
        Assert.Equal("µm", w1.Unit);
    }

    [Fact]
    public void SwitchingToWidthEntry_NoWorkspace_StillDefaultsToMm()
    {
        var (_, comp, editor) = MakeMklopf(schematicDirectory: null,
            ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null);

        Assert.Equal("mm", comp.Parameters.Single(p => p.Name == "W1").Unit);
    }

    [Fact]
    public void SwitchingToWidthEntry_OnPcbWorkspace_ValueIsPhysicallyEquivalentInMil()
    {
        // 50 Ohm on FR-4 1.6mm synthesizes to a known physical width; the NUMBER written must be
        // that width expressed in mil, not the same numeric value mislabeled as mil.
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, compMil, editorMil) = MakeMklopf(schematicDir, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));
        editorMil.ToggleMklopfImpedanceEntryCommand.Execute(null);
        double w1Mil = double.Parse(compMil.Parameters.Single(p => p.Name == "W1").Expression, CultureInfo.InvariantCulture);

        var (_, compMm, editorMm) = MakeMklopf(schematicDirectory: null, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));
        editorMm.ToggleMklopfImpedanceEntryCommand.Execute(null);
        double w1Mm = double.Parse(compMm.Parameters.Single(p => p.Name == "W1").Expression, CultureInfo.InvariantCulture);

        // 1 mil = 0.0254 mm -> mil value should be ~= mm value / 0.0254
        Assert.Equal(w1Mm / 0.0254, w1Mil, 1);
    }

    // ── W1/W2 -> Z1/Z2 round-trips regardless of which unit W1/W2 were stored in ────────────────

    [Fact]
    public void SwitchingBackToImpedanceEntry_FromMilWidths_StillResolvesCorrectly()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, comp, editor) = MakeMklopf(schematicDir, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // -> W1/W2 in mil
        Assert.Equal("mil", comp.Parameters.Single(p => p.Name == "W1").Unit);

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // -> Z1/Z2 again

        Assert.Equal(50.0, double.Parse(comp.Parameters.Single(p => p.Name == "Z1").Expression, CultureInfo.InvariantCulture), 1);
        Assert.Equal(100.0, double.Parse(comp.Parameters.Single(p => p.Name == "Z2").Expression, CultureInfo.InvariantCulture), 1);
    }

    // ── L <-> F3db: the L side of that toggle respects the workspace unit too ───────────────────

    [Fact]
    public void SwitchingF3dbBackToLength_OnPcbWorkspace_WritesMilNotMm()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, comp, editor) = MakeMklopf(schematicDir, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> F3db
        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> L again

        var l = comp.Parameters.Single(p => p.Name == "L");
        Assert.Equal("mil", l.Unit);
        Assert.NotEqual("mm", l.Unit);
    }

    [Fact]
    public void SwitchingF3dbBackToLength_OnMmicWorkspace_WritesMicronsNotMm()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.MmicGaAs());
        var (_, comp, editor) = MakeMklopf(schematicDir, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> F3db
        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> L again

        Assert.Equal("µm", comp.Parameters.Single(p => p.Name == "L").Unit);
    }

    [Fact]
    public void F3dbItself_AlwaysUsesGHz_RegardlessOfWorkspace()
    {
        // Frequency has no workspace-technology convention (DefaultDisplayUnit is length-only) —
        // F3db should stay GHz on every workspace, including a PCB/MMIC one.
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, comp, editor) = MakeMklopf(schematicDir, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfLengthEntryCommand.Execute(null); // -> F3db

        Assert.Equal("GHz", comp.Parameters.Single(p => p.Name == "F3db").Unit);
    }

    // ── brief-technology-editor-units-and-layers.md R-tec-5 audit regression guard ─────────────
    // Every stored parameter is self-describing (Expression + its OWN Unit string, written together
    // atomically) — `Technology.DefaultDisplayUnit` is consulted ONLY at the moment a NEW value is
    // written (here: the toggle itself), never to reinterpret an already-stored one. This proves
    // that invariant directly: change the technology's DefaultDisplayUnit AFTER a toggle has already
    // written a value, and confirm the already-stored parameter is untouched.

    [Fact]
    public void AlreadyStoredW1W2_IsUnaffectedByALaterDefaultDisplayUnitChange()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        Assert.Equal(LayoutUnit.Mil, pcb.DefaultDisplayUnit); // sanity on the fixture itself
        var schematicDir = WriteWorkspaceWithTech(pcb);
        var (_, comp, editor) = MakeMklopf(schematicDir, ("Z1", "50", "Ω"), ("Z2", "100", "Ω"), ("L", "20", "mm"));

        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // -> W1/W2, written in "mil"
        var w1Before = comp.Parameters.Single(p => p.Name == "W1");
        Assert.Equal("mil", w1Before.Unit);
        string expressionBefore = w1Before.Expression;

        // Simulate a Tech Editor edit+save changing this SAME workspace's technology to a
        // different DefaultDisplayUnit — as if the user had used the R-tec-3 combo added by this
        // brief and saved. Rewrite the same .ctech file on disk with DefaultDisplayUnit = Mm.
        var edited = StarterTechnologies.Pcb2Layer();
        edited.DefaultDisplayUnit = LayoutUnit.Mm;
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), edited);

        // The ALREADY-STORED W1 parameter is untouched — same Expression, same Unit — because
        // nothing revisits an already-placed component's parameters when a technology file changes.
        var w1After = comp.Parameters.Single(p => p.Name == "W1");
        Assert.Same(w1Before, w1After);
        Assert.Equal(expressionBefore, w1After.Expression);
        Assert.Equal("mil", w1After.Unit);

        // And it still round-trips correctly: toggling back to Z1/Z2 now (under the CHANGED
        // technology) must still interpret the stored "mil" value as mil, not silently reinterpret
        // it as the technology's new "mm" default — proving R-klp-3a's "last-edited pair stays
        // authoritative, not re-derived" holds across a unit change, not just within one session.
        editor.ToggleMklopfImpedanceEntryCommand.Execute(null); // -> Z1/Z2 again
        double z1 = double.Parse(comp.Parameters.Single(p => p.Name == "Z1").Expression, CultureInfo.InvariantCulture);
        Assert.Equal(50.0, z1, 1);
    }
}
