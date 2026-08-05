using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §3A — "Update Schematic from Layout," gates
/// 19/20/22: pushing a linked instance's edited PCell parameter back into the schematic; creating a
/// schematic component (and stamping SchematicId) for a layout-first, never-linked instance; and the
/// same overwrite-report symmetry §2.2 uses, roles reversed.
/// </summary>
public sealed class LayoutToSchematicGeneratorTests : IDisposable
{
    private readonly string _root;

    public LayoutToSchematicGeneratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-l2s-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeLayoutDir(string cellName)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, cellName);
        return CellFolder.SubFolderPath(cellDir, ViewType.Layout);
    }

    private static EditableComponent MakeMlin(string instanceName, double wMm)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Name == "W" ? wMm.ToString() : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Fact]
    public void LayoutFirstInstance_CreatesSchematicComponent_AndStampsSchematicId_Gate20()
    {
        var layoutDir = MakeLayoutDir("Amp");
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        var source = new LayoutView();
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), X = 0, Y = 0, Mag = 1.0 };
        source.Instances.Add(inst);
        Assert.Null(inst.SchematicId); // layout-first — never in a schematic

        var schematic = new SchematicEditModel();
        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);

        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.Single(schematic.Components);
        var created = schematic.Components[0];
        Assert.Equal(SymbolKind.Mlin, created.Symbol);
        Assert.Equal(1, result.CreatedCount);

        // R-L5-20: the create half writes SchematicId as it goes.
        Assert.Equal(created.InstanceName, inst.SchematicId);

        // Running again must UPDATE the same component, not create a second one.
        var result2 = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        Assert.True(result2.NothingChanged);
        Assert.Single(schematic.Components);
    }

    [Fact]
    public void LinkedInstance_EditedInLayout_PushesNewValueIntoSchematic_Gate19()
    {
        var layoutDir = MakeLayoutDir("Amp2");
        var schematic = new SchematicEditModel { SchematicDirectory = Path.Combine(Path.GetDirectoryName(layoutDir)!, "schematic") };
        var comp = MakeMlin("ML1", wMm: 10);
        schematic.Components.Add(comp);

        // Forward run creates the layout instance and links it.
        var source = new LayoutView();
        var fwd = SchematicToLayoutGenerator.Run(schematic, source, schematic.SchematicDirectory!, _root, layoutDir, null, null, null);
        fwd.Command!.Execute();
        Assert.Equal("ML1", source.Instances[0].SchematicId);

        // Edit the layout instance's W to 15 mm (repoint to a new generated cell, same mechanism
        // EditInstancePCellParameters uses).
        var origin = CellLayoutResolver.Resolve(source.Instances[0].CellRef, layoutDir).View!.PCellOrigin!;
        var newParams = new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 15 * 1e-3 };
        string newCellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", newParams, null, null, PCellLayerSelection.Default);
        source.Instances[0].CellRef = Path.GetRelativePath(layoutDir, newCellDir);

        var rev = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        Assert.NotNull(rev.Command);
        rev.Command!.Execute();

        var wParam = comp.Parameters.First(p => p.Name == "W");
        Assert.Equal(15.0, double.Parse(wParam.Expression), 6);

        // Then re-running the FORWARD direction now agrees — nothing left to push.
        var fwd2 = SchematicToLayoutGenerator.Run(schematic, source, schematic.SchematicDirectory!, _root, layoutDir, null, null, null);
        Assert.True(fwd2.NothingChanged);
    }

    [Fact]
    public void SchematicEditSinceLastSync_IsOverwritten_ReportedAsWarning_Gate22()
    {
        var layoutDir = MakeLayoutDir("Amp3");
        var schematic = new SchematicEditModel { SchematicDirectory = Path.Combine(Path.GetDirectoryName(layoutDir)!, "schematic") };
        var comp = MakeMlin("ML1", wMm: 10);
        schematic.Components.Add(comp);

        var source = new LayoutView();
        var fwd = SchematicToLayoutGenerator.Run(schematic, source, schematic.SchematicDirectory!, _root, layoutDir, null, null, null);
        fwd.Command!.Execute();

        // Edit the SCHEMATIC directly (not through a forward re-run) — this is the edit about to be discarded.
        comp.Parameters.First(p => p.Name == "W").Expression = "13";

        // The layout hasn't moved at all — the reverse command still has nothing NEW to say about the
        // layout's own value, but the schematic has diverged from the last-synced snapshot, so pushing
        // (even the unchanged layout value) reports the discard as a warning.
        var rev = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        Assert.NotNull(rev.Command);
        var wLine = rev.Lines.First(l => l.Text.Contains("W changed"));
        Assert.Equal(SchematicToLayoutGenerator.ReportSeverity.Warning, wLine.Severity);
    }

    [Fact]
    public void BrokenInstance_IsSkipped_NeverThrows()
    {
        var layoutDir = MakeLayoutDir("Amp4");
        var source = new LayoutView();
        source.Instances.Add(new LayoutInstance { CellRef = "../does-not-exist", X = 0, Y = 0, Mag = 1.0 });

        var schematic = new SchematicEditModel();
        var ex = Record.Exception(() => LayoutToSchematicGenerator.Run(source, schematic, layoutDir));
        Assert.Null(ex);
    }

    // ── §2 (brief-misc-termg-units-technologies.md), R-misc-3/4/5 ────────────────────────────────

    // R-misc-3/4: a layout-first PCell pushed to the schematic on a mil-based PCB technology must
    // show its width in mil, at the CORRECT magnitude — not a bare SI-metres coefficient (the
    // "wrong by a factor of 1000 or 25400" class of bug) and not a hardcoded "mm" default that
    // ignores the technology's own DefaultDisplayUnit.
    [Fact]
    public void LayoutFirstMlin_PushedToSchematic_OnPcbTechnology_ShowsWidthInMil_CorrectMagnitude()
    {
        var layoutDir = MakeLayoutDir("Amp5");
        // 40 mil width, expressed directly in SI metres — exactly what a layout-side PCell always
        // stores (PCell contract R-pc-6), regardless of what unit the user typed it in.
        double wMeters = 40.0 * 0.0254e-3; // 40 mil -> metres
        var pcellParams = new Dictionary<string, PCellValue> { ["W"] = wMeters, ["L"] = 0.01 };
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", pcellParams, null, null, PCellLayerSelection.Default);

        var source = new LayoutView();
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

        var schematic = new SchematicEditModel();
        var tech = StarterTechnologies.Pcb2Layer(); // DefaultDisplayUnit = Mil
        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir, tech);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        var wParam = schematic.Components.Single().Parameters.First(p => p.Name == "W");
        Assert.Equal("mil", wParam.Unit);
        Assert.Equal(40.0, double.Parse(wParam.Expression), 3); // correct magnitude — not 0.001016, not 1016, not 40000
    }

    // R-misc-5: place a microstrip PCell in a layout, push to schematic, push back to layout — the
    // geometry (the PCell's own resolved SI parameters) must be IDENTICAL. This is the cheapest
    // possible guard against the whole SI-vs-coefficient-and-unit class of bug: any unit mismatch in
    // either direction fails this round trip.
    [Fact]
    public void LayoutFirstMlin_RoundTrip_ToSchematicAndBackToLayout_GeometryIdentical_Gate3()
    {
        var layoutDir = MakeLayoutDir("Amp6");
        double wMeters = 40.0 * 0.0254e-3; // 40 mil
        double lMeters = 250.0 * 0.0254e-3; // 250 mil
        var originalParams = new Dictionary<string, PCellValue> { ["W"] = wMeters, ["L"] = lMeters };
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", originalParams, null, null, PCellLayerSelection.Default);

        var source = new LayoutView();
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), X = 0, Y = 0, Mag = 1.0 };
        source.Instances.Add(inst);

        var schematic = new SchematicEditModel { SchematicDirectory = Path.Combine(Path.GetDirectoryName(layoutDir)!, "schematic") };
        var tech = StarterTechnologies.Pcb2Layer();

        // Layout -> schematic (creates the component, R-misc-3/4's fixed path).
        var rev = LayoutToSchematicGenerator.Run(source, schematic, layoutDir, tech);
        rev.Command!.Execute();
        Assert.Equal(inst.SchematicId, schematic.Components.Single().InstanceName);

        // Schematic -> layout (the mechanical inverse, forward direction) — must reproduce the exact
        // same resolved cell (same generated CellRef, since GeneratedCellStore is content-addressed:
        // identical resolved parameters hash to the identical folder).
        string originalCellRef = inst.CellRef!;
        var fwd = SchematicToLayoutGenerator.Run(schematic, source, schematic.SchematicDirectory!, _root, layoutDir, tech, null, null);
        Assert.True(fwd.NothingChanged); // nothing to push — the round trip already agrees
        Assert.Equal(originalCellRef, inst.CellRef); // same generated cell, geometry identical by construction

        // Directly confirm the resolved PCell parameters are bit-for-bit the values we started with.
        var origin = CellLayoutResolver.Resolve(inst.CellRef, layoutDir).View!.PCellOrigin!;
        Assert.Equal(wMeters, origin.Parameters.Real("W"), 9);
        Assert.Equal(lMeters, origin.Parameters.Real("L"), 9);
    }
}
