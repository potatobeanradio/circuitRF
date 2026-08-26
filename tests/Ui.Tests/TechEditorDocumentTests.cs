using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L0d gates: TechDocument / TechEditorViewModel editor lifecycle ──

public class TechEditorDocumentTests
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
            new LayerDef { Key = new LayerKey(1, 0), Name = "Metal1", Color = new CircuitRF.Design.Theming.Rgba(200, 100, 50), ZOrder = 1 },
            new LayerDef { Key = new LayerKey(2, 0), Name = "Metal2", Color = new CircuitRF.Design.Theming.Rgba(50, 100, 200), ZOrder = 2 },
        ],
    };

    // ── Scratch identity / dirty mirroring ──────────────────────────────────

    [Fact]
    public void NewTechDocument_StartsClean_NeverScratch()
    {
        var path = TempPath();
        var vm   = new TechEditorViewModel(path, FreshTech());
        var doc  = new TechDocument("test.ctech", vm, path);

        Assert.False(doc.IsDirty);
        Assert.Equal(path, doc.FilePath);
        Assert.Equal("test.ctech", doc.Title);
    }

    [Fact]
    public void CommittedEdit_MarksDirty_ShowsBulletInTitle()
    {
        var path = TempPath();
        var vm   = new TechEditorViewModel(path, FreshTech());
        var doc  = new TechDocument("test.ctech", vm, path);

        vm.Layers[0].StagedName = "Renamed";
        vm.Layers[0].CommitName();

        Assert.True(vm.IsDirty);
        Assert.True(doc.IsDirty);
        Assert.Equal("• test.ctech", doc.Title);
    }

    // ── Gate 5: undo/redo of a color edit, layer add, layer reorder, stackup reorder, DRC edit ──

    [Fact]
    public void Undo_ColorEdit_RestoresOriginalColor_RedoReapplies()
    {
        var vm = new TechEditorViewModel(TempPath(), FreshTech());
        var row = vm.Layers[0];
        var original = row.Layer.Color;
        var changed = new CircuitRF.Design.Theming.Rgba(9, 9, 9);

        var before = vm.SnapshotJson();
        row.Layer.Color = changed;
        vm.CommitEdit(before, "Change color");

        Assert.Equal(changed, vm.Layers[0].Layer.Color);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(original, vm.Layers[0].Layer.Color);

        vm.UndoRedo.Redo();
        Assert.Equal(changed, vm.Layers[0].Layer.Color);
    }

    [Fact]
    public void Undo_LayerAdd_RemovesIt_RedoReAddsIt()
    {
        var vm = new TechEditorViewModel(TempPath(), FreshTech());
        int before = vm.Layers.Count;

        vm.AddLayerCommand.Execute(null);
        Assert.Equal(before + 1, vm.Layers.Count);

        vm.UndoRedo.Undo();
        Assert.Equal(before, vm.Layers.Count);

        vm.UndoRedo.Redo();
        Assert.Equal(before + 1, vm.Layers.Count);
    }

    [Fact]
    public void Undo_LayerReorder_RestoresOrder_RedoReapplies()
    {
        var vm = new TechEditorViewModel(TempPath(), FreshTech());
        var firstName = vm.Working.Layers[0].Name;
        var secondName = vm.Working.Layers[1].Name;

        vm.MoveLayer(vm.Layers[1], -1); // move Metal2 up, swapping with Metal1

        Assert.Equal(secondName, vm.Working.Layers[0].Name);
        Assert.Equal(firstName, vm.Working.Layers[1].Name);

        vm.UndoRedo.Undo();
        Assert.Equal(firstName, vm.Working.Layers[0].Name);
        Assert.Equal(secondName, vm.Working.Layers[1].Name);

        vm.UndoRedo.Redo();
        Assert.Equal(secondName, vm.Working.Layers[0].Name);
        Assert.Equal(firstName, vm.Working.Layers[1].Name);
    }

    [Fact]
    public void Undo_StackupReorder_RestoresOrder_RedoReapplies()
    {
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1000, SigmaSm = 1e7 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 1000, Epsr = 4 });
        var vm = new TechEditorViewModel(TempPath(), tech);

        vm.MoveStackupLayer(vm.StackupLayers[1], -1);
        Assert.Equal("Core", vm.Working.Stackup.Layers[0].Name);
        Assert.Equal("Top", vm.Working.Stackup.Layers[1].Name);

        vm.UndoRedo.Undo();
        Assert.Equal("Top", vm.Working.Stackup.Layers[0].Name);
        Assert.Equal("Core", vm.Working.Stackup.Layers[1].Name);

        vm.UndoRedo.Redo();
        Assert.Equal("Core", vm.Working.Stackup.Layers[0].Name);
        Assert.Equal("Top", vm.Working.Stackup.Layers[1].Name);
    }

    [Fact]
    public void Undo_DrcRuleEdit_RestoresOriginal_RedoReapplies()
    {
        var tech = FreshTech();
        tech.DrcRules.Add(new DrcRule { Name = "MinW", Kind = DrcRuleKind.MinWidth, Layer = tech.Layers[0].Key, ValueDbu = 1000 });
        var vm = new TechEditorViewModel(TempPath(), tech);
        var row = vm.DrcRules[0];

        row.StagedName = "Renamed";
        row.CommitName();
        Assert.Equal("Renamed", vm.Working.DrcRules[0].Name);

        vm.UndoRedo.Undo();
        Assert.Equal("MinW", vm.Working.DrcRules[0].Name);

        vm.UndoRedo.Redo();
        Assert.Equal("Renamed", vm.Working.DrcRules[0].Name);
    }

    [Fact]
    public void Undo_PastFirstEdit_IsNoOp()
    {
        var vm = new TechEditorViewModel(TempPath(), FreshTech());
        vm.AddLayerCommand.Execute(null);
        vm.UndoRedo.Undo();

        Assert.False(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo(); // no-op, must not throw
        Assert.Equal(2, vm.Working.Layers.Count);
    }

    // ── Gate 5 continued: IsDirty tracks back to false when undone to saved state ──

    [Fact]
    public void Save_ThenUndo_TracksIsDirtyBackToFalse()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");
        try
        {
            var vm = new TechEditorViewModel(path, FreshTech());
            vm.AddLayerCommand.Execute(null);
            Assert.True(vm.IsDirty);

            vm.SaveCommand.Execute(null);
            Assert.False(vm.IsDirty);
            Assert.True(System.IO.File.Exists(path));

            vm.AddLayerCommand.Execute(null);
            Assert.True(vm.IsDirty);

            vm.UndoRedo.Undo(); // back to the saved baseline
            Assert.False(vm.IsDirty);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void Save_FiresTechSaved_WithPath()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");
        try
        {
            var vm = new TechEditorViewModel(path, FreshTech());
            string? savedPath = null;
            vm.TechSaved += p => savedPath = p;

            vm.AddLayerCommand.Execute(null);
            vm.SaveCommand.Execute(null);

            Assert.Equal(path, savedPath);
        }
        finally { System.IO.File.Delete(path); }
    }

    // ── Gate 4: round-trip — edit one field in each section, save, reload, assert persisted ──

    [Fact]
    public void RoundTrip_EditEachSection_Save_Reload_AllPersist()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");
        try
        {
            var tech = FreshTech();
            tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1000, SigmaSm = 1e7 });
            tech.DrcRules.Add(new DrcRule { Name = "MinW", Kind = DrcRuleKind.MinWidth, Layer = tech.Layers[0].Key, ValueDbu = 1000 });
            var vm = new TechEditorViewModel(path, tech);

            vm.Layers[0].StagedName = "Metal1-Renamed";
            vm.Layers[0].CommitName();

            vm.StackupLayers[0].StagedName = "Top-Renamed";
            vm.StackupLayers[0].CommitName();

            vm.DrcRules[0].StagedName = "MinW-Renamed";
            vm.DrcRules[0].CommitName();

            vm.SaveCommand.Execute(null);

            var reloaded = TechPersistence.LoadFromFile(path);
            Assert.Equal("Metal1-Renamed", reloaded.Layers[0].Name);
            Assert.Equal("Top-Renamed", reloaded.Stackup.Layers[0].Name);
            Assert.Equal("MinW-Renamed", reloaded.DrcRules[0].Name);
            Assert.Equal("Metal2", reloaded.Layers[1].Name); // nothing else moved
        }
        finally { System.IO.File.Delete(path); }
    }

    // ── Gate 6: live validation — duplicate (Layer,Datatype) surfaces + clears; save allowed ──

    [Fact]
    public void DuplicateLayerKey_SurfacesValidationMessage_FixingClearsIt()
    {
        var vm = new TechEditorViewModel(TempPath(), FreshTech());
        Assert.False(vm.HasValidationIssues);

        vm.Layers[1].StagedLayerNumber = "1";
        vm.Layers[1].StagedDatatype = "0";
        vm.Layers[1].CommitLayerNumber();
        vm.Layers[1].CommitDatatype();

        Assert.True(vm.HasValidationIssues);
        Assert.Contains(vm.ValidationIssues, m => m.Contains("Duplicate layer"));

        vm.Layers[1].StagedLayerNumber = "2";
        vm.Layers[1].CommitLayerNumber();

        Assert.False(vm.HasValidationIssues);
    }

    [Fact]
    public void Save_WithValidationIssuesPresent_StillWritesFile()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");
        try
        {
            var vm = new TechEditorViewModel(path, FreshTech());
            vm.Layers[1].StagedLayerNumber = "1";
            vm.Layers[1].CommitLayerNumber();
            Assert.True(vm.HasValidationIssues);

            vm.SaveCommand.Execute(null);

            Assert.True(System.IO.File.Exists(path));
            Assert.False(vm.IsDirty);
        }
        finally { System.IO.File.Delete(path); }
    }

    // ── Gate 9: drawing-layer selection is closed; deleting a referenced layer surfaces a message ──

    [Fact]
    public void DeletingReferencedLayer_SurfacesValidationMessage_DoesNotCorruptStackup()
    {
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1000, SigmaSm = 1e7,
            DrawingLayers = [tech.Layers[0].Key], IsGroundReference = true,
        });
        var vm = new TechEditorViewModel(TempPath(), tech);
        Assert.False(vm.HasValidationIssues);

        vm.RemoveLayer(vm.Layers[0]);

        Assert.True(vm.HasValidationIssues);
        Assert.Contains(vm.ValidationIssues, m => m.Contains("unknown drawing layer"));
        // The stackup's DrawingLayers list is untouched — validation flags it rather than silently fixing it.
        Assert.Single(vm.Working.Stackup.Layers[0].DrawingLayers);
    }

    // ── Gate 10: dimension fields parse 1.6mm / 35u / 100 um and redisplay in default unit ──

    [Theory]
    [InlineData("1.6mm", 1_600_000)]
    [InlineData("35u", 35_000)]
    [InlineData("100 um", 100_000)]
    public void ThicknessField_ParsesVariousUnitSuffixes_ToCorrectDbu(string text, long expectedDbu)
    {
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 1, Epsr = 4 });
        var vm = new TechEditorViewModel(TempPath(), tech);
        var row = vm.StackupLayers[0];

        row.StagedThicknessText = text;
        row.CommitThickness();

        Assert.Equal(expectedDbu, vm.Working.Stackup.Layers[0].ThicknessDbu);
        Assert.False(row.HasThicknessError);
        // Redisplays in the technology's default display unit (Um here).
        Assert.Equal(LayoutUnits.Format(expectedDbu, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron), row.StagedThicknessText);
    }

    [Fact]
    public void ThicknessUnitSuffix_ReflectsTechnologyDefaultDisplayUnit()
    {
        var tech = FreshTech();
        tech.DefaultDisplayUnit = LayoutUnit.Mil;
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1000, SigmaSm = 1e7 });
        var vm = new TechEditorViewModel(TempPath(), tech);

        Assert.Equal("mil", vm.StackupLayers[0].ThicknessUnitSuffix);
    }

    // ── Gate 3 (the L0 gate line): editing + saving a technology re-resolves an open layout ──
    // through L0c's already-working cache/seam. WorkspaceViewModel can't be instantiated headlessly
    // (needs the Avalonia runtime — see src/Ui/CLAUDE.md's testing notes), so this "simulates" the
    // exact production wiring — TechnologyCache.TechnologyChanged → re-resolve → ApplyTechResolution —
    // using the real types WorkspaceViewModel composes, mirroring WorkspaceViewModel.OnTechSaved's body.
    [Fact]
    public void SaveInEditor_InvalidatesCache_OpenLayoutReceivesNewTechnologyInstance()
    {
        var techPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"techtest-{System.Guid.NewGuid():N}.ctech");
        try
        {
            TechPersistence.SaveToFile(techPath, FreshTech());

            var cache = new TechnologyCache();
            var layoutVm = new LayoutEditorViewModel(new LayoutView
            {
                DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle,
            });
            layoutVm.ApplyTechResolution(new TechResolution(
                cache.Get(techPath), techPath, TechResolutionSource.WorkspaceDefault, []));
            var originalInstance = layoutVm.Technology;
            Assert.NotNull(originalInstance);

            // Mirrors OnTechnologyChanged: re-resolve any layout whose ResolvedTechPath matches.
            cache.TechnologyChanged += changedPath =>
            {
                if (!string.Equals(layoutVm.ResolvedTechPath, changedPath, System.StringComparison.OrdinalIgnoreCase)) return;
                layoutVm.ApplyTechResolution(new TechResolution(
                    cache.Get(changedPath), changedPath, TechResolutionSource.WorkspaceDefault, []));
            };

            var editorVm = new TechEditorViewModel(techPath, TechPersistence.LoadFromFile(techPath));
            // Mirrors WorkspaceViewModel.OnTechSaved's body exactly.
            editorVm.TechSaved += path => cache.Invalidate(path);

            var before = editorVm.SnapshotJson();
            editorVm.Layers[0].Layer.Color = new CircuitRF.Design.Theming.Rgba(1, 2, 3);
            editorVm.CommitEdit(before, "Change color");
            editorVm.SaveCommand.Execute(null);

            Assert.NotSame(originalInstance, layoutVm.Technology);
            Assert.Equal(new CircuitRF.Design.Theming.Rgba(1, 2, 3), layoutVm.Technology!.Layers[0].Color);
        }
        finally { System.IO.File.Delete(techPath); }
    }

    // ── Drawing-layer cardinality (§10.4): conductor = many, via/dielectric = at most one ──

    [Fact]
    public void Conductor_CheckingSecondDrawingLayer_KeepsBothChecked()
    {
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = 1000, SigmaSm = 1e7 });
        var vm = new TechEditorViewModel(TempPath(), tech);
        var row = vm.StackupLayers[0];
        Assert.True(row.AllowMultipleDrawingLayers);

        row.DrawingLayerOptions[0].IsChecked = true;
        var refreshed = vm.StackupLayers[0]; // rebuilt after the commit
        refreshed.DrawingLayerOptions[1].IsChecked = true;

        Assert.Equal(2, vm.Working.Stackup.Layers[0].DrawingLayers.Count);
    }

    [Theory]
    [InlineData(StackupKind.Via)]
    [InlineData(StackupKind.Dielectric)]
    public void ViaOrDielectric_CheckingSecondDrawingLayer_UnchecksFirst(StackupKind kind)
    {
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = kind, Name = "Row", ThicknessDbu = 1000, Epsr = 4, SigmaSm = 1e7 });
        var vm = new TechEditorViewModel(TempPath(), tech);
        var row = vm.StackupLayers[0];
        Assert.False(row.AllowMultipleDrawingLayers);

        row.DrawingLayerOptions[0].IsChecked = true;
        var afterFirst = vm.StackupLayers[0];
        Assert.Single(vm.Working.Stackup.Layers[0].DrawingLayers);
        Assert.Equal(tech.Layers[0].Key, vm.Working.Stackup.Layers[0].DrawingLayers[0]);

        afterFirst.DrawingLayerOptions[1].IsChecked = true;
        var afterSecond = vm.Working.Stackup.Layers[0];

        Assert.Single(afterSecond.DrawingLayers);
        Assert.Equal(tech.Layers[1].Key, afterSecond.DrawingLayers[0]);
    }

    // ── brief-technology-editor-units-and-layers.md gate 2: ground-reference checkbox ──────────

    [Fact]
    public void ConductorRow_TogglingIsGroundReference_CommitsUndoably()
    {
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = 1000, SigmaSm = 1e7 });
        var vm = new TechEditorViewModel(TempPath(), tech);
        var row = vm.StackupLayers[0];

        Assert.False(row.IsGroundReference);
        row.IsGroundReference = true;

        Assert.True(vm.Working.Stackup.Layers[0].IsGroundReference);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.False(vm.Working.Stackup.Layers[0].IsGroundReference);

        vm.UndoRedo.Redo();
        Assert.True(vm.Working.Stackup.Layers[0].IsGroundReference);
    }

    [Theory]
    [InlineData(StackupKind.Via)]
    [InlineData(StackupKind.Dielectric)]
    public void NonConductorRow_IsConductorFalse_GroundCheckboxHasNothingToBindTo(StackupKind kind)
    {
        // R-tec-1: the checkbox itself is gated in the view by IsConductor (absent, not disabled,
        // on a dielectric/via row) — this pins the VM-side predicate the view's IsVisible binds to.
        var tech = FreshTech();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = kind, Name = "Row", ThicknessDbu = 1000, Epsr = 4, SigmaSm = 1e7 });
        var vm = new TechEditorViewModel(TempPath(), tech);
        Assert.False(vm.StackupLayers[0].IsConductor);
    }

    [Fact]
    public void Validate_NoConductorMarkedGround_Reported_TwoMarked_NotReported()
    {
        var oneConductorNoGround = new Technology
        {
            Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "M", SigmaSm = 1, ThicknessDbu = 1 }] },
        };
        Assert.Contains(TechValidation.Validate(oneConductorNoGround), s => s.Contains("ground", System.StringComparison.OrdinalIgnoreCase));

        var twoConductorsBothGround = new Technology
        {
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", SigmaSm = 1, ThicknessDbu = 1, IsGroundReference = true },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", SigmaSm = 1, ThicknessDbu = 1, IsGroundReference = true },
                ],
            },
        };
        Assert.DoesNotContain(TechValidation.Validate(twoConductorsBothGround), s => s.Contains("ground", System.StringComparison.OrdinalIgnoreCase));
    }

    // ── brief-technology-editor-units-and-layers.md gate 4: DefaultDisplayUnit is a seed ───────

    [Fact]
    public void DefaultDisplayUnit_Toggle_CommitsUndoably_RoundTripsThroughSaveReload()
    {
        var tech = FreshTech();
        tech.DefaultDisplayUnit = LayoutUnit.Um;
        var path = TempPath();
        var vm = new TechEditorViewModel(path, tech);

        Assert.Equal(LayoutUnit.Um, vm.DefaultDisplayUnit);
        vm.DefaultDisplayUnit = LayoutUnit.Mil;

        Assert.Equal(LayoutUnit.Mil, vm.Working.DefaultDisplayUnit);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(LayoutUnit.Um, vm.Working.DefaultDisplayUnit);
        Assert.Equal(LayoutUnit.Um, vm.DefaultDisplayUnit);

        vm.UndoRedo.Redo();
        Assert.Equal(LayoutUnit.Mil, vm.Working.DefaultDisplayUnit);

        vm.SaveCommand.Execute(null);
        var reloaded = TechPersistence.LoadFromFile(path);
        Assert.Equal(LayoutUnit.Mil, reloaded.DefaultDisplayUnit);
        System.IO.File.Delete(path);
    }

    [Fact]
    public void DefaultDisplayUnit_SameValue_IsANoOp_PushesNoUndoEntry()
    {
        var tech = FreshTech();
        tech.DefaultDisplayUnit = LayoutUnit.Um;
        var vm = new TechEditorViewModel(TempPath(), tech);

        vm.DefaultDisplayUnit = LayoutUnit.Um; // unchanged
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void DefaultDisplayUnit_IsASeed_NewlyCreatedLayoutPicksItUp_ButNeverRePropagatesToAnAlreadyConstructedOne()
    {
        // Mirrors WorkspaceViewModel.NewLayout's own construction exactly (WorkspaceViewModel
        // itself cannot be instantiated headlessly — see src/Ui/CLAUDE.md's own testing note —
        // so this simulates the seam directly against the real LayoutView/LayoutEditorViewModel
        // types it actually constructs).
        var tech = new Technology { DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };

        var firstModel = new LayoutView { DisplayUnit = tech.DefaultDisplayUnit, SnapDbu = tech.DefaultSnapDbu };
        var firstVm = new LayoutEditorViewModel(firstModel);
        Assert.Equal(LayoutUnit.Um, firstVm.DisplayUnit);

        // R-tec-4: editing the technology's DefaultDisplayUnit afterward must NEVER re-seed an
        // already-constructed layout — there is no live subscription between them at all.
        tech.DefaultDisplayUnit = LayoutUnit.Mil;
        Assert.Equal(LayoutUnit.Um, firstVm.DisplayUnit);

        // A layout created AFTER the change picks up the new default, exactly like the first one
        // picked up the original value — proving the seed genuinely applies at creation time.
        var secondModel = new LayoutView { DisplayUnit = tech.DefaultDisplayUnit, SnapDbu = tech.DefaultSnapDbu };
        var secondVm = new LayoutEditorViewModel(secondModel);
        Assert.Equal(LayoutUnit.Mil, secondVm.DisplayUnit);

        // ...and still leaves the first, already-open layout completely untouched.
        Assert.Equal(LayoutUnit.Um, firstVm.DisplayUnit);
    }
}
