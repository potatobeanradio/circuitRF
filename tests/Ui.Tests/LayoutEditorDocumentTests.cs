using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L0b gates: LayoutDocument + LayoutEditorViewModel dirty/save lifecycle ──

public class LayoutEditorDocumentTests
{
    private static LayoutView FreshModel() => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
        AngleMode    = AngleMode.AnyAngle,
    };

    // ── Scratch identity / dirty mirroring ─────────────────────────────────────

    [Fact]
    public void NewScratchDocument_StartsClean()
    {
        var vm  = new LayoutEditorViewModel(FreshModel());
        var doc = new LayoutDocument("Untitled-Layout-1", vm);

        Assert.True(doc.IsScratch);
        Assert.False(doc.IsDirty);
        Assert.Equal("Untitled-Layout-1", doc.Title);
    }

    [Fact]
    public void LayoutDocument_MirrorsVmDirty_AndShowsBulletInTitle()
    {
        var vm  = new LayoutEditorViewModel(FreshModel());
        var doc = new LayoutDocument("Untitled-Layout-1", vm);

        vm.DisplayUnit = LayoutUnit.Mil; // a document preference edit — dirties the VM
        Assert.True(vm.IsDirty);
        Assert.True(doc.IsDirty);
        Assert.Equal("• Untitled-Layout-1", doc.Title);
    }

    // ── Gate 7: display-unit / snap-grid edits dirty the document, never touch geometry ──

    [Fact]
    public void DisplayUnitChange_MarksDirty_WritesThroughToModel()
    {
        var model = FreshModel();
        var vm    = new LayoutEditorViewModel(model);

        Assert.False(vm.IsDirty);
        vm.DisplayUnit = LayoutUnit.Mil;

        Assert.True(vm.IsDirty);
        Assert.Equal(LayoutUnit.Mil, model.DisplayUnit);
    }

    [Fact]
    public void SnapDbuChange_MarksDirty_WritesThroughToModel()
    {
        var model = FreshModel();
        var vm    = new LayoutEditorViewModel(model);

        vm.SnapDbu = 500;

        Assert.True(vm.IsDirty);
        Assert.Equal(500, model.SnapDbu);
    }

    [Fact]
    public void DisplayUnitChange_IsSerializationNoOp_ExceptDisplayUnitToken()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 2_900_000, Y2 = 20_000_000 });

        var jsonUm = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model);
        vm.DisplayUnit = LayoutUnit.Mil;
        var jsonMil = LayoutPersistence.Serialize(model);

        Assert.NotEqual(jsonUm, jsonMil);

        var linesUm  = jsonUm.Split('\n').Where(l => !l.TrimStart().StartsWith("\"DisplayUnit\"")).ToArray();
        var linesMil = jsonMil.Split('\n').Where(l => !l.TrimStart().StartsWith("\"DisplayUnit\"")).ToArray();
        Assert.Equal(string.Join('\n', linesUm), string.Join('\n', linesMil));
    }

    // ── Metadata bar ────────────────────────────────────────────────────────────

    [Fact]
    public void ExtentText_EmptyLayout_ReturnsEmDash()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        Assert.Equal("—", vm.ExtentText);
    }

    [Fact]
    public void MetadataBar_ShapeAndInstanceCounts_Correct()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        model.Shapes.Add(new CircleShape { Cx = 0, Cy = 0, R = 50 });
        model.Instances.Add(new LayoutInstance { CellRef = "../other" });

        var vm = new LayoutEditorViewModel(model);

        Assert.Equal("2", vm.ShapeCountText);
        Assert.Equal("1", vm.InstanceCountText);
    }

    [Fact]
    public void ExtentText_UnionsAllShapeBboxes_InDisplayUnit()
    {
        var model = FreshModel();
        model.DisplayUnit = LayoutUnit.Um;
        model.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 2000 }); // 1um x 2um at 1000 DBU/um

        var vm = new LayoutEditorViewModel(model);
        Assert.Equal("1 × 2 µm", vm.ExtentText);
    }

    [Fact]
    public void ResolutionText_ReflectsDbuPerMicron()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        Assert.Equal("1 DBU = 1 nm", vm.ResolutionText);
    }

    // ── L0c: technology readout ──────────────────────────────────────────────

    [Fact]
    public void Technology_Unset_ReadsAsNoTechnologyFallbackColors()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        Assert.Equal("No technology", vm.TechNameText);
        Assert.Equal("fallback colors", vm.LayerCountText);
        Assert.Equal("No technology · fallback colors", vm.TechSummaryText);
    }

    [Fact]
    public void ApplyTechResolution_SetsTechnologyAndReadout_WithoutTouchingDisplayUnitOrSnap()
    {
        var model = FreshModel();
        model.DisplayUnit = LayoutUnit.Mil;
        model.SnapDbu     = 25_400;
        var vm = new LayoutEditorViewModel(model);

        var tech = StarterTechnologies.Pcb2Layer();
        var resolution = new TechResolution(tech, "/ws/tech/pcb-2layer.ctech", TechResolutionSource.WorkspaceDefault, []);

        vm.ApplyTechResolution(resolution);

        Assert.Same(tech, vm.Technology);
        Assert.Equal("PCB 2-Layer", vm.TechNameText);
        Assert.Equal($"{tech.Layers.Count} layers", vm.LayerCountText);
        Assert.Equal(LayoutUnit.Mil, vm.DisplayUnit);
        Assert.Equal(25_400, vm.SnapDbu);
        Assert.False(vm.IsDirty); // resolving a technology is not a document edit
    }

    [Fact]
    public void ApplyTechResolution_NullTech_ReadsAsNoTechnology()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        var resolution = new TechResolution(null, null, TechResolutionSource.None, []);

        vm.ApplyTechResolution(resolution);

        Assert.Null(vm.Technology);
        Assert.Equal("No technology · fallback colors", vm.TechSummaryText);
    }

    // ── Gate 4 / 5: Save / round-trip ───────────────────────────────────────────

    [Fact]
    public void PerformSave_WritesFile_ClearsDirty_FiresLayoutSaved()
    {
        var model = FreshModel();
        var vm    = new LayoutEditorViewModel(model);
        vm.DisplayUnit = LayoutUnit.Mil; // dirty it first

        var tmp = Path.GetTempFileName();
        try
        {
            string? saved = null;
            vm.LayoutSaved += p => saved = p;

            vm.PerformSave(tmp);

            Assert.False(vm.IsDirty);
            Assert.Equal(tmp, vm.CurrentLayoutPath);
            Assert.Equal(tmp, saved);

            var restored = LayoutPersistence.LoadFromFile(tmp);
            Assert.Equal(model.DisplayUnit, restored.DisplayUnit);
            Assert.Equal(model.SnapDbu, restored.SnapDbu);
            Assert.Equal(model.DbuPerMicron, restored.DbuPerMicron);
            Assert.Equal(model.AngleMode, restored.AngleMode);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void PerformSave_Failure_RaisesSaveError_LeavesDirtyUnchanged()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.DisplayUnit = LayoutUnit.Mil; // dirty

        string? error = null;
        vm.SaveError += m => error = m;

        var badPath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}", "layout.clay");
        vm.PerformSave(badPath);

        Assert.NotNull(error);
        Assert.True(vm.IsDirty);       // failed save must not clear dirty
        Assert.Null(vm.CurrentLayoutPath);
    }

    [Fact]
    public void LayoutDocument_Materialize_ClearsDirty_SetsFilePath_UpdatesTitle()
    {
        var vm  = new LayoutEditorViewModel(FreshModel());
        var doc = new LayoutDocument("Untitled-Layout-1", vm);
        vm.DisplayUnit = LayoutUnit.Mil;
        Assert.True(doc.IsDirty);

        var tmp = Path.GetTempFileName();
        try
        {
            doc.Materialize(tmp);

            Assert.False(doc.IsScratch);
            Assert.Equal(tmp, doc.FilePath);
            Assert.False(doc.IsDirty);
            Assert.False(vm.IsDirty);
            Assert.Equal(tmp, vm.CurrentLayoutPath);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Reopen_RoundTrip_PreservesUnitsSnapAndAngleMode()
    {
        var original = new LayoutView
        {
            DbuPerMicron = 1000,
            DisplayUnit  = LayoutUnit.Mil,
            SnapDbu      = 25_400,
            AngleMode    = AngleMode.Manhattan,
        };
        original.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 500, Y2 = 500 });

        var tmp = Path.GetTempFileName();
        try
        {
            LayoutPersistence.SaveToFile(tmp, original);

            // Simulate closing the tab and reopening from the file picker.
            var reloaded = LayoutPersistence.LoadFromFile(tmp);
            var vm       = new LayoutEditorViewModel(reloaded, tmp);
            var doc      = new LayoutDocument(Path.GetFileName(tmp), vm, tmp);

            Assert.Equal(LayoutUnit.Mil, vm.DisplayUnit);
            Assert.Equal(25_400, vm.SnapDbu);
            Assert.Equal(1000, doc.ViewModel.Model.DbuPerMicron);
            Assert.Equal(AngleMode.Manhattan, doc.ViewModel.Model.AngleMode);
            Assert.False(doc.IsDirty);
            Assert.False(doc.IsScratch);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
