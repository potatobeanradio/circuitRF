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

    // ── Ordinary hierarchical instances (owner, 2026-08-17) ──────────────────────────────────────
    //
    // "I performed an Update Schematic from Layout, but my cell instance was not placed in the
    // schematic (even though it has a symbol)." Every test below is on the ORDINARY path — a cell the
    // user drew and placed, with no PCellOrigin — which the generator used to skip with a bare
    // `continue` that reported nothing either.

    /// <summary>A real cell folder with a layout (so a layout instance resolves), a symbol (so the
    /// schematic can draw it), and a published parameter interface (so the created component can be
    /// checked for seeding). Deliberately NOT a PCell — that is the whole point.</summary>
    private string MakePlainCell(string cellName, bool withSymbol = true, params (string Name, string Default)[] parameters)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, cellName);

        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, cellName + ".clay"), new LayoutView());

        if (withSymbol)
        {
            string symDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
            SymbolPersistence.SaveToFile(Path.Combine(symDir, cellName + ".csym"),
                new EditableSymbol { UserEditable = true }.ToSymbol());
        }

        if (parameters.Length > 0)
        {
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            foreach (var (name, def) in parameters)
                ccell.Parameters.Add(new CcellParameter { Name = name, DefaultExpression = def });
            CellPersistence.SaveToFile(ccellPath, ccell);
        }

        return cellDir;
    }

    private (LayoutView Source, SchematicEditModel Schematic, string LayoutDir) MakeTop(string topName)
    {
        string topCellDir = CellFolder.CreateCellFolder(_root, topName);
        return (new LayoutView(),
                new SchematicEditModel { SchematicDirectory = CellFolder.SubFolderPath(topCellDir, ViewType.Schematic) },
                CellFolder.SubFolderPath(topCellDir, ViewType.Layout));
    }

    [Fact]
    public void PlainCellInstance_IsPlacedInTheSchematic_AndReferencesTheCell()
    {
        string cellDir = MakePlainCell("Filter", withSymbol: true, ("Cap", "1p"));
        var (source, schematic, layoutDir) = MakeTop("TopA");
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), Mag = 1.0 };
        source.Instances.Add(inst);

        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);

        Assert.NotNull(result.Command);   // the bug: this was null, and the run reported nothing at all
        result.Command!.Execute();

        var placed = Assert.Single(schematic.Components);
        Assert.Equal(1, result.CreatedCount);

        // It refers to the CELL — that reference is what resolves the symbol the owner already had.
        Assert.Equal(Path.GetRelativePath(schematic.SchematicDirectory!, cellDir), placed.CellRef);
        Assert.Equal(CellSymbolState.Resolved,
            CellSymbolResolver.Resolve(placed.CellRef!, schematic.SchematicDirectory).State);

        // Seeded from the cell's own published interface, exactly as a palette drop seeds it.
        Assert.Equal("1p", Assert.Single(placed.Parameters, p => p.Name == "Cap").Expression);

        // Linked, so a second run is idempotent rather than placing a duplicate.
        Assert.Equal(placed.InstanceName, inst.SchematicId);
        var again = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        Assert.True(again.NothingChanged);
        Assert.Single(schematic.Components);
        Assert.Equal(1, again.UnchangedCount);
    }

    [Fact]
    public void TwoPlainCellInstances_GetDistinctNames_AndBothSurviveTheRun()
    {
        string cellDir = MakePlainCell("Res", withSymbol: true);
        var (source, schematic, layoutDir) = MakeTop("TopB");
        string cellRef = Path.GetRelativePath(layoutDir, cellDir);
        var a = new LayoutInstance { CellRef = cellRef, X = 0, Mag = 1.0 };
        var b = new LayoutInstance { CellRef = cellRef, X = 1000, Mag = 1.0 };
        source.Instances.Add(a);
        source.Instances.Add(b);

        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        result.Command!.Execute();

        // Nothing in Run() executes a command, so NextAvailableName scanning schematic.Components
        // alone handed both of these the same name — the second create then overwrote the first's
        // identity and both SchematicIds pointed at one component.
        Assert.Equal(2, schematic.Components.Count);
        Assert.Equal(2, result.CreatedCount);
        Assert.NotEqual(schematic.Components[0].InstanceName, schematic.Components[1].InstanceName);
        Assert.NotEqual(a.SchematicId, b.SchematicId);
        Assert.NotEqual(schematic.Components[0].X, schematic.Components[1].X); // not stacked on one point
    }

    /// <summary>Owner, 2026-08-17: a placed instance "does not render the pins" — because the cell it
    /// names has no symbol at all. Placed anyway (refusing would be the old silence in a narrower form),
    /// but the cell is REPORTED for symbol generation rather than merely warned about, which is what the
    /// Library palette already does when the very same cell is dropped onto a schematic.</summary>
    [Fact]
    public void PlainCellInstance_WithNoSymbolAtAll_IsPlaced_AndTheCellIsOfferedForSymbolGeneration()
    {
        string cellDir = MakePlainCell("NoSym", withSymbol: false);
        var (source, schematic, layoutDir) = MakeTop("TopC");
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), Mag = 1.0 });

        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        result.Command!.Execute();

        Assert.Single(schematic.Components);
        Assert.Equal(cellDir, Assert.Single(result.CellsWithoutSymbols));
    }

    /// <summary>The OTHER kind of "no primary symbol", which is a different question and must not be
    /// answered by generating a further symbol: several exist and the user has not said which is
    /// primary. Warned, never auto-resolved.</summary>
    [Fact]
    public void PlainCellInstance_WithSymbolsButNoPrimary_IsWarnedAbout_NotOfferedGeneration()
    {
        string cellDir = MakePlainCell("Ambiguous", withSymbol: true);
        string symDir  = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        SymbolPersistence.SaveToFile(Path.Combine(symDir, "Alternate.csym"),
            new EditableSymbol { UserEditable = true }.ToSymbol());   // now two, and .ccell names neither

        var (source, schematic, layoutDir) = MakeTop("TopC2");
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), Mag = 1.0 });

        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        result.Command!.Execute();

        Assert.Single(schematic.Components);
        Assert.Empty(result.CellsWithoutSymbols);
        Assert.Contains(result.Lines, l => l.Severity == SchematicToLayoutGenerator.ReportSeverity.Warning
                                        && l.Text.Contains("no primary chosen"));
    }

    [Fact]
    public void PlainCellInstance_WithASymbol_IsNotOfferedGeneration()
    {
        string cellDir = MakePlainCell("HasSym", withSymbol: true);
        var (source, schematic, layoutDir) = MakeTop("TopC3");
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), Mag = 1.0 });

        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);

        Assert.Empty(result.CellsWithoutSymbols);
    }

    [Fact]
    public void PlainCellInstance_OnAnUnsavedSchematic_IsReportedRatherThanSkippedInSilence()
    {
        string cellDir = MakePlainCell("Lonely", withSymbol: true);
        var (source, _, layoutDir) = MakeTop("TopD");
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, cellDir), Mag = 1.0 });

        // No SchematicDirectory — a cell reference is a relative path and has nothing to be relative to.
        var result = LayoutToSchematicGenerator.Run(source, new SchematicEditModel(), layoutDir);

        Assert.Equal(0, result.CreatedCount);
        Assert.Contains(result.Lines, l => l.Severity == SchematicToLayoutGenerator.ReportSeverity.Warning
                                        && l.Text.Contains("save the schematic first"));
    }

    [Fact]
    public void PlainCellInstance_AlongsideAPCell_BothArePlaced()
    {
        string plainDir = MakePlainCell("Pad", withSymbol: true);
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string pcellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        var (source, schematic, layoutDir) = MakeTop("TopE");
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, pcellDir), Mag = 1.0 });
        source.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, plainDir), Mag = 1.0 });

        var result = LayoutToSchematicGenerator.Run(source, schematic, layoutDir);
        result.Command!.Execute();

        Assert.Equal(2, result.CreatedCount);
        Assert.Contains(schematic.Components, c => c.Symbol == SymbolKind.Mlin);
        Assert.Contains(schematic.Components, c => c.CellRef is { Length: > 0 } r && r.EndsWith("Pad"));
    }
}
