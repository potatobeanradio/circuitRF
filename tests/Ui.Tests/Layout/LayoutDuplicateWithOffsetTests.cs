using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

// Duplicate with Offset (owner, 2026-08-27) — Duplicate was reachable only from Ctrl+D, and it always
// nudged the copy by one snap step. It is now a visible context-menu item directly below Rotate, and
// both surfaces prompt for an X/Y offset in the layout's own display unit, defaulting to (0,0).
//
// The prompt itself is a Window subclass and cannot be constructed in this project's headless test
// suite, so the same split every prior Layout Editor phase used applies here: the OFFSET behaviour is
// tested against the view model directly, and the wiring that can only live in the view/canvas
// (menu position, one shared path for keyboard and menu, units) is asserted structurally against the
// source, per brief-L1-fix-context-menu-stacking.md's own fallback.

public class LayoutDuplicateWithOffsetTests
{
    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
    };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    private static (LayoutEditorViewModel Vm, LayoutView Model) SelectedRect()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        return (vm, model);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── The offset itself ─────────────────────────────────────────────────────────────────────

    /// <summary>The dialog's own default: a copy exactly on top of the original, which is what makes
    /// "duplicate then type where it goes" possible at all.</summary>
    [Fact]
    public void DuplicateAtZeroOffset_LandsExactlyOnTheOriginal_AsOneUndoEntry()
    {
        var (vm, model) = SelectedRect();

        vm.Duplicate(0, 0);

        Assert.Equal(2, model.Shapes.Count);
        var copy = Assert.IsType<RectShape>(model.Shapes[1]);
        Assert.Equal(0, copy.X1);
        Assert.Equal(0, copy.Y1);
        Assert.Equal(1_000, copy.X2);
        Assert.Equal(1_000, copy.Y2);
        Assert.Equal([1], vm.SelectedIndices);   // the copy, not the original, is what a drag now moves

        vm.UndoRedo.Undo();
        Assert.Single(model.Shapes);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    /// <summary>An asked-for offset is applied verbatim in DBU — never re-snapped, so a typed
    /// dimension means what it says even when it is not a multiple of the snap step.</summary>
    [Theory]
    [InlineData(2_500L, 0L)]
    [InlineData(0L, -7_250L)]
    [InlineData(-1_234L, 5_678L)]
    public void DuplicateAtAnOffset_MovesTheCopyByExactlyThatMuch(long dx, long dy)
    {
        var (vm, model) = SelectedRect();

        vm.Duplicate(dx, dy);

        var copy = Assert.IsType<RectShape>(model.Shapes[1]);
        Assert.Equal(dx, copy.X1);
        Assert.Equal(dy, copy.Y1);
        Assert.Equal(1_000 + dx, copy.X2);
        Assert.Equal(1_000 + dy, copy.Y2);
    }

    /// <summary>Instances travel with the shapes on the same offset — Duplicate has always cloned
    /// both kinds as one undo entry (R-fix-2), and the offset must not split them.</summary>
    [Fact]
    public void DuplicateAtAnOffset_CarriesInstancesByTheSameAmount()
    {
        var model = FreshModel();
        model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 4_000, Y = 6_000, Mag = 1.0 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.Duplicate(2_000, -3_000);

        Assert.Equal(2, model.Instances.Count);
        Assert.Equal(6_000, model.Instances[1].X);
        Assert.Equal(3_000, model.Instances[1].Y);
        Assert.Equal("../../Leaf", model.Instances[1].CellRef);
    }

    /// <summary>The parameterless overload is unchanged — it is the programmatic entry point and
    /// still nudges by one snap step (brief-L1f-clipboard.md §4).</summary>
    [Fact]
    public void TheParameterlessOverload_StillNudgesByOneSnapStep()
    {
        var (vm, model) = SelectedRect();

        vm.Duplicate();

        var copy = Assert.IsType<RectShape>(model.Shapes[1]);
        Assert.Equal(model.SnapDbu, copy.X1);
        Assert.Equal(model.SnapDbu, copy.Y1);
    }

    // ── The menu item's own enabled-ness ──────────────────────────────────────────────────────

    [Fact]
    public void WithNothingSelected_DuplicateIsDisabledWithAStatedReason()
    {
        var vm = new LayoutEditorViewModel(FreshModel());

        var avail = vm.DuplicateAvailability;

        Assert.False(avail.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(avail.DisabledReason));
    }

    [Fact]
    public void WithASelection_DuplicateIsEnabled()
    {
        var (vm, _) = SelectedRect();
        Assert.True(vm.DuplicateAvailability.CanExecute);
    }

    // ── Wiring that only the view/canvas can hold ─────────────────────────────────────────────

    /// <summary>Position is the request: "Duplicate…" sits directly below the two Rotate items, with
    /// no item between them.</summary>
    [Fact]
    public void TheContextMenu_PutsDuplicateDirectlyBelowRotate()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Controls", "LayoutCanvas.cs"));

        int ccw = src.IndexOf("AddAvailItem(\"Rotate 90° CCW\"", System.StringComparison.Ordinal);
        int cw  = src.IndexOf("AddAvailItem(\"Rotate 90° CW\"", System.StringComparison.Ordinal);
        int dup = src.IndexOf("AddAvailItem(\"Duplicate…\"", System.StringComparison.Ordinal);

        Assert.True(ccw >= 0 && cw > ccw, "the two Rotate items are gone or reordered");
        Assert.True(dup > cw, "Duplicate… must come after the Rotate pair");

        // Nothing else may be added between them, or Duplicate stops being "below Rotate".
        string between = src[(cw + 1)..dup];
        Assert.DoesNotContain("AddAvailItem(", between);
    }

    /// <summary>Ctrl+D and the menu item run the SAME method, so the offset prompt cannot appear on
    /// one surface and not the other.</summary>
    [Fact]
    public void BothSurfaces_GoThroughOneShowDuplicateDialogAsync()
    {
        string canvas = ReadRepoFile(Path.Combine("src", "Ui", "Controls", "LayoutCanvas.cs"));
        string view   = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));

        Assert.Contains("AddAvailItem(\"Duplicate…\", _viewModel.DuplicateAvailability).Click += async (_, _) => await ShowDuplicateDialogAsync();", canvas);
        Assert.Contains("ShowDuplicateDialogAsync()", view);
        // The old fire-and-forget "duplicate immediately, no prompt" path must be gone from the view.
        Assert.DoesNotContain("Vm?.Duplicate()", view);
    }

    /// <summary>The prompt parses in the layout's DISPLAY unit at the document's own resolution — the
    /// same LayoutUnits path every other typed dimension in this editor uses, so "500nm" in a µm
    /// document means 500 nm.</summary>
    [Fact]
    public void ThePrompt_ParsesInTheLayoutsOwnUnits()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "DuplicateOffsetDialog.axaml.cs"));

        Assert.Contains("LayoutUnits.TryParse(OffsetXBox.Text", src);
        Assert.Contains("LayoutUnits.TryParse(OffsetYBox.Text", src);
        Assert.Contains("_vm.DisplayUnit", src);
        Assert.Contains("_vm.Model.DbuPerMicron", src);
        // Default (0,0) on every opening — never a remembered previous offset.
        Assert.Contains("LayoutUnits.Format(0, vm.DisplayUnit, vm.Model.DbuPerMicron)", src);
    }

    // ── The toolbar bug: Rotate/Mirror stayed enabled with nothing selected ───────────────────

    /// <summary>Every one of the four starts disabled in XAML, exactly as the Schematic Editor's own
    /// four do — a button enabled before any selection exists is the reported bug's own first
    /// frame.</summary>
    [Fact]
    public void TheRotateAndMirrorButtons_StartDisabled()
    {
        string xaml = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        foreach (string name in new[] { "RotateCcwBtn", "RotateCwBtn", "MirrorHBtn", "MirrorVBtn" })
        {
            int at = xaml.IndexOf($"x:Name=\"{name}\"", System.StringComparison.Ordinal);
            Assert.True(at >= 0, $"{name} is not named in LayoutEditorView.axaml");
            int close = xaml.IndexOf('>', at);
            Assert.Contains("IsEnabled=\"False\"", xaml[at..close]);
        }
    }

    /// <summary>...and their live state is pushed from the view model's own RotateAvailability — the
    /// same property the context menu's Rotate items read, so the two surfaces cannot disagree.</summary>
    [Fact]
    public void TheRotateAndMirrorButtons_FollowRotateAvailability()
    {
        string view = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));

        int method = view.IndexOf("private void UpdateSelectionButtonStates()", System.StringComparison.Ordinal);
        Assert.True(method >= 0, "UpdateSelectionButtonStates is gone");
        string body = view[method..(method + 700)];

        Assert.Contains("RotateAvailability.CanExecute", body);
        foreach (string name in new[] { "RotateCcwBtn", "RotateCwBtn", "MirrorHBtn", "MirrorVBtn" })
            Assert.Contains($"{name}.IsEnabled", body);

        // It has to actually be CALLED when the selection changes — including a wire-only selection,
        // which lives in the wBond overlay and never touches SelectionStatusText.
        Assert.Contains("SelectionStatusText", view);
        int overlayChanged = view.IndexOf("private void OnFrameOverlayChanged()", System.StringComparison.Ordinal);
        Assert.True(overlayChanged >= 0);
        Assert.Contains("UpdateSelectionButtonStates()", view[overlayChanged..(overlayChanged + 400)]);
    }

    /// <summary>The enabled-ness the buttons follow is the view model's, and it is empty-selection
    /// aware — the actual bug, stated where it can be tested without a Window.</summary>
    [Fact]
    public void RotateAvailability_IsFalseWithNothingSelected_AndTrueWithASelection()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        Assert.False(vm.RotateAvailability.CanExecute);
        Assert.False(vm.MirrorAvailability.CanExecute);

        var (selected, _) = SelectedRect();
        Assert.True(selected.RotateAvailability.CanExecute);
        Assert.True(selected.MirrorAvailability.CanExecute);
    }
}
