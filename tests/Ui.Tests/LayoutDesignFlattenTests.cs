using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>R-L4c-6 (gate 7): Gerber has no hierarchy at all, so the whole design must flatten before
/// export. Reuses L3c's own <see cref="LayoutFlatten"/> machinery and R-L3c-3's cross-technology
/// reconciliation (via <see cref="LayoutLayerMapping"/>) — this file drives the whole-design walk
/// <see cref="LayoutDesignFlatten"/> adds on top of that existing machinery.</summary>
public class LayoutDesignFlattenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gerber-flatten-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        populate(view);
        var layoutPath = Path.Combine(layoutDir, $"{name}.clay");
        LayoutPersistence.SaveToFile(layoutPath, view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    [Fact]
    public void PlainInstance_ProducesSubCellShapes_TranslatedIntoParentFrame()
    {
        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 5000, Y = 7000, Mag = 1.0 });

        var result = LayoutDesignFlatten.Flatten(topView, topDir, null, null, null);

        Assert.Equal(1, result.TopLevelInstancesFlattened);
        var rect = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
        Assert.Equal(5000, rect.X1);
        Assert.Equal(7000, rect.Y1);
        Assert.Equal(6000, rect.X2);
        Assert.Equal(8000, rect.Y2);
    }

    [Fact]
    public void FiveByFiveArray_ProducesTwentyFiveFlattenedFootprints_AtCorrectPositions()
    {
        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0, Rows = 5, Cols = 5, PitchX = 1000, PitchY = 1000,
        });

        var result = LayoutDesignFlatten.Flatten(topView, topDir, null, null, null);

        Assert.Equal(25, result.Shapes.Count);
        Assert.Empty(result.UnresolvedInstances);
        // Spot-check two corners land exactly on the pitch grid.
        Assert.Contains(result.Shapes, s => s is RectShape r && r.X1 == 0 && r.Y1 == 0);
        Assert.Contains(result.Shapes, s => s is RectShape r && r.X1 == 4000 && r.Y1 == 4000);
    }

    [Fact]
    public void UnresolvedInstance_ReportedNotSilent_ContributesNoGeometry()
    {
        var topDir = CreateCell("TOP", v => v.Instances.Add(
            new LayoutInstance { CellRef = "../DoesNotExist", X = 0, Y = 0, Mag = 1.0 }));
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));

        var result = LayoutDesignFlatten.Flatten(topView, topDir, null, null, null);

        Assert.Empty(result.Shapes);
        Assert.Single(result.UnresolvedInstances);
        Assert.Contains("DoesNotExist", result.UnresolvedInstances[0]);
    }

    [Fact]
    public void CrossTechnologySubCell_RequiresConfirmation_LeavesSubtreeUnflattenedUntilResolved()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var mmic = StarterTechnologies.MmicGaAs();

        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = mmic.Layers[0].Key, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0 });

        TechResolution ResolveTechAt(string? techRef, string clayDir) =>
            new(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);

        var result = LayoutDesignFlatten.Flatten(topView, topDir, pcb, ResolveTechAt, null);

        Assert.Empty(result.Shapes); // left unflattened — nothing silently remapped
        Assert.NotEmpty(result.PendingCrossTechMappings);
        var rows = Assert.Single(result.PendingCrossTechMappings).Value;
        Assert.True(LayoutLayerMapping.RequiresConfirmation(rows));
    }

    [Fact]
    public void CrossTechnologySubCell_ResolvedMapping_RemapsLayerKey_NotSilentPassthrough()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var mmic = StarterTechnologies.MmicGaAs();
        var mmicMetal1 = mmic.Layers[0].Key;
        var pcbDrill = pcb.Layers.Single(l => l.Name == "Drill").Key;

        var childDir = CreateCell("CHILD", v => v.Shapes.Add(new RectShape { Layer = mmicMetal1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var topDir = CreateCell("TOP", v => { });
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, "TOP.clay"));
        topView.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(topLayoutDir, childDir), X = 0, Y = 0, Mag = 1.0 });

        TechResolution ResolveTechAt(string? techRef, string clayDir) =>
            new(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);

        var firstPass = LayoutDesignFlatten.Flatten(topView, topDir, pcb, ResolveTechAt, null);
        var pendingEntry = Assert.Single(firstPass.PendingCrossTechMappings);

        // The user explicitly chooses to remap onto PCB's Drill layer — deliberately NOT the
        // same-key coincidence (both starter technologies use (1,0)) a naive flatten would silently
        // produce, so a passing test here actually proves L1g's mapping ran, not just that geometry
        // survived unchanged.
        var settledRows = pendingEntry.Value
            .Select(r => r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.MapToExisting, pcbDrill) })
            .ToList();
        var resolved = new Dictionary<string, IReadOnlyList<LayerMappingRow>> { [pendingEntry.Key] = settledRows };

        var secondPass = LayoutDesignFlatten.Flatten(topView, topDir, pcb, ResolveTechAt, resolved);

        Assert.Empty(secondPass.PendingCrossTechMappings);
        var rect = Assert.IsType<RectShape>(Assert.Single(secondPass.Shapes));
        Assert.Equal(pcbDrill, rect.Layer);
        Assert.NotEqual(mmicMetal1, rect.Layer);
    }
}
