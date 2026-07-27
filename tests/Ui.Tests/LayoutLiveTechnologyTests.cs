using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Tests;

// ── L1 fix §2: live technology editing (brief-L1-fix-path-seams-and-live-tech.md) ──
// WorkspaceViewModel can't be instantiated headlessly (needs the Avalonia runtime — see
// src/Ui/CLAUDE.md's testing notes), so this composes TechnologyCache + TechEditorViewModel +
// LayoutEditorViewModel exactly as WorkspaceViewModel wires them, mirroring the existing L0d gate-3
// "simulated seam" test in TechEditorDocumentTests.cs. Dialog-gated behavior (the close prompt, the
// Reload Technology confirm) is verified via the same "simulate the production switch" pattern
// already established for WorkspaceViewModel-only logic elsewhere in this test suite.

public class LayoutLiveTechnologyTests
{
    private static string TempPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");

    private static Technology FreshTech() => new()
    {
        Name = "Test Tech",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = 1000,
        DefaultFlattenTolDbu = 1000,
        Layers =
        [
            new LayerDef { Key = new LayerKey(1, 0), Name = "Metal1", Color = new Rgba(200, 100, 50), ZOrder = 1 },
            new LayerDef { Key = new LayerKey(2, 0), Name = "Metal2", Color = new Rgba(50, 100, 200), ZOrder = 2 },
        ],
    };

    /// <summary>Composes the same three real types WorkspaceViewModel wires together, with the
    /// SAME event-subscription bodies (OnTechnologyChanged / OnTechLiveChanged / OnTechSaved).</summary>
    private sealed class Harness : IDisposable
    {
        public string TechPath { get; }
        public TechnologyCache Cache { get; } = new();
        public TechEditorViewModel EditorVm { get; }
        public LayoutEditorViewModel LayoutVm { get; }

        public Harness(Technology initial, LayoutView? layoutModel = null)
        {
            TechPath = TempPath();
            TechPersistence.SaveToFile(TechPath, initial);

            LayoutVm = new LayoutEditorViewModel(layoutModel ?? new LayoutView
            {
                DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
            });
            LayoutVm.ApplyTechResolution(new TechResolution(
                Cache.Get(TechPath), TechPath, TechResolutionSource.WorkspaceDefault, []));

            // Mirrors WorkspaceViewModel.OnTechnologyChanged's body.
            Cache.TechnologyChanged += changedPath =>
            {
                if (!string.Equals(LayoutVm.ResolvedTechPath, changedPath, StringComparison.OrdinalIgnoreCase)) return;
                LayoutVm.ApplyTechResolution(new TechResolution(
                    Cache.Get(changedPath), changedPath, TechResolutionSource.WorkspaceDefault, []));
            };

            EditorVm = new TechEditorViewModel(TechPath, TechPersistence.LoadFromFile(TechPath));
            // Mirrors WorkspaceViewModel.OnTechLiveChanged — applied synchronously here (no
            // dispatcher coalescing needed for a test that isn't racing a UI frame).
            EditorVm.TechLiveChanged += (path, clone) => Cache.SetLive(path, clone);
            // Mirrors WorkspaceViewModel.OnTechSaved's body exactly.
            EditorVm.TechSaved += path => Cache.Invalidate(path);
        }

        public void Dispose() { try { System.IO.File.Delete(TechPath); } catch { } }
    }

    // ── Live propagation ──────────────────────────────────────────────────────

    [Fact]
    public void LiveColorEdit_WithoutSaving_PropagatesToOpenLayoutImmediately()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);
    }

    [Fact]
    public void LiveVisibleToggle_WithoutSaving_PropagatesImmediately()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Visible = false;
        h.EditorVm.CommitEdit(before, "Toggle visible");

        Assert.False(h.LayoutVm.Technology!.Layers[0].Visible);
    }

    [Fact]
    public void LiveSelectableToggle_WithoutSaving_PropagatesImmediately()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Selectable = false;
        h.EditorVm.CommitEdit(before, "Toggle selectable");

        Assert.False(h.LayoutVm.Technology!.Layers[0].Selectable);
    }

    // ── R-fix-1: always a deep clone, never Working itself ────────────────────

    [Fact]
    public void MutatingWorkingAfterALiveUpdate_DoesNotAffectTheConsumersClone_UndoDoesUpdateIt()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");
        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);

        // Mutate Working directly, simulating the next in-progress edit before it commits — the
        // consumer's already-installed clone must be unaffected.
        h.EditorVm.Working.Layers[0].Color = new Rgba(1, 1, 1);
        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);

        // Undo REPLACES Working wholesale (ApplySnapshot) — the consumer must receive the restored
        // value, not silently keep the old clone forever.
        h.EditorVm.UndoRedo.Undo();
        Assert.Equal(new Rgba(200, 100, 50), h.LayoutVm.Technology!.Layers[0].Color); // original
    }

    // ── Discard reverts ────────────────────────────────────────────────────────

    [Fact]
    public void DiscardWithoutSaving_ClearLive_RevertsOpenLayoutsToTheOnDiskTechnology()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");
        Assert.Equal(new Rgba(9, 8, 7), h.LayoutVm.Technology!.Layers[0].Color);

        h.Cache.ClearLive(h.TechPath); // mirrors ConfirmCloseDockable's "Don't Save" branch

        Assert.Equal(new Rgba(200, 100, 50), h.LayoutVm.Technology!.Layers[0].Color);
    }

    // ── Save clears the override ──────────────────────────────────────────────

    [Fact]
    public void Save_ClearsTheLiveOverride_GetReturnsWhatWasSaved()
    {
        using var h = new Harness(FreshTech());

        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));

        h.EditorVm.SaveCommand.Execute(null);

        Assert.False(h.Cache.HasLiveOverride(h.TechPath));
        Assert.Equal(new Rgba(9, 8, 7), h.Cache.Get(h.TechPath)!.Layers[0].Color);
    }

    // ── Units are never re-seeded ──────────────────────────────────────────────

    [Fact]
    public void StreamingSeveralLiveEdits_NeverReSeedsDisplayUnitOrSnapDbu()
    {
        var layoutModel = new LayoutView
        {
            DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mil, SnapDbu = 12_345, AngleMode = AngleMode.AnyAngle,
        };
        // The technology's own defaults deliberately differ from the layout's current state.
        using var h = new Harness(FreshTech(), layoutModel);

        for (int i = 0; i < 5; i++)
        {
            var before = h.EditorVm.SnapshotJson();
            h.EditorVm.Layers[0].Layer.ZOrder = i;
            h.EditorVm.CommitEdit(before, $"Edit {i}");
        }

        Assert.Equal(LayoutUnit.Mil, h.LayoutVm.DisplayUnit);
        Assert.Equal(12_345, h.LayoutVm.Model.SnapDbu);
    }

    // ── Close prompts (simulated — SaveChangesDialog needs the Avalonia runtime) ──────────

    // Mirrors WorkspaceViewModel.ConfirmCloseDockable's TechDocument branch exactly.
    private static bool SimulateCloseDirtyTechDocument(
        TechnologyCache cache, string path, TechEditorViewModel vm, SaveChangesResult choice)
    {
        switch (choice)
        {
            case SaveChangesResult.Cancel:
                return false;
            case SaveChangesResult.DontSave:
                cache.ClearLive(path);
                return true;
            case SaveChangesResult.Save:
                vm.SaveCommand.Execute(null);
                return !vm.IsDirty;
            default:
                return false;
        }
    }

    [Fact]
    public void SimulatedClose_Cancel_KeepsTheDocumentOpen_AndKeepsTheLiveOverride()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool canProceed = SimulateCloseDirtyTechDocument(h.Cache, h.TechPath, h.EditorVm, SaveChangesResult.Cancel);

        Assert.False(canProceed);
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));
    }

    [Fact]
    public void SimulatedClose_DontSave_ClearsTheOverride()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool canProceed = SimulateCloseDirtyTechDocument(h.Cache, h.TechPath, h.EditorVm, SaveChangesResult.DontSave);

        Assert.True(canProceed);
        Assert.False(h.Cache.HasLiveOverride(h.TechPath));
    }

    // ── Reload guard (simulated) ───────────────────────────────────────────────

    // Mirrors WorkspaceViewModel.ReloadTechnologyAsync's guard exactly.
    private static bool SimulateReloadTechnology(TechnologyCache cache, string path, SaveChangesResult? choiceIfLiveOverride)
    {
        if (cache.HasLiveOverride(path) && choiceIfLiveOverride != SaveChangesResult.Save)
            return false; // Cancel (or dialog unavailable) — leave the override intact

        cache.Invalidate(path);
        return true;
    }

    [Fact]
    public void SimulatedReload_LiveOverridePresent_CancelLeavesOverrideIntact()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool reloaded = SimulateReloadTechnology(h.Cache, h.TechPath, SaveChangesResult.Cancel);

        Assert.False(reloaded);
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));
    }

    [Fact]
    public void SimulatedReload_LiveOverridePresent_ConfirmedDiscard_ClearsOverride()
    {
        using var h = new Harness(FreshTech());
        var before = h.EditorVm.SnapshotJson();
        h.EditorVm.Layers[0].Layer.Color = new Rgba(9, 8, 7);
        h.EditorVm.CommitEdit(before, "Change color");

        bool reloaded = SimulateReloadTechnology(h.Cache, h.TechPath, SaveChangesResult.Save); // "Discard" button

        Assert.True(reloaded);
        Assert.False(h.Cache.HasLiveOverride(h.TechPath));
    }

    [Fact]
    public void SimulatedReload_NoLiveOverride_NeverPrompts_ReloadsDirectly()
    {
        using var h = new Harness(FreshTech());

        bool reloaded = SimulateReloadTechnology(h.Cache, h.TechPath, choiceIfLiveOverride: null);

        Assert.True(reloaded);
    }
}
