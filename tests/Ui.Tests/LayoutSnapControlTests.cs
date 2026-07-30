using System.Collections.Specialized;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §1 — the snap-distance control (ladder
// + typed entry, F9 toggle) — and §2.4's F3/'s' geometry-snap toggle keys.
//
// docs/sonnet-briefs/brief-snap-ladder-crash.md — the load-bearing invariant, superseding an earlier
// (crashing) reading of brief-snap-combobox-and-consistency.md §1: SnapLadderOptions is a pure,
// STATIC function of Technology/DisplayUnit ONLY — selecting an entry (which sets SnapDbu) must never
// mutate it, because that mutation used to run from INSIDE Avalonia's own SelectionChanged
// notification and crashed with ArgumentOutOfRangeException on every single selection. "Never blank"
// is satisfied entirely through SnapDistanceText (the editable combobox's own Text binding, which can
// show any value regardless of list membership) — never by inserting the current value into the list.

public class LayoutSnapControlTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel Vm(LayoutView model) => new(model);

    [Fact]
    public void SnapLadder_DerivedFromTechnologyDefault_FiveMultiples()
    {
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 1000 }; // 1 um
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        Assert.Equal(5, vm.SnapLadderOptions.Count);
        // LayoutUnits.Suffix(Um) renders the µ glyph (U+00B5), not the ASCII "um" spelling.
        Assert.Equal("1 µm", vm.SnapLadderOptions[0]);
        Assert.Equal("5 µm", vm.SnapLadderOptions[1]);
        Assert.Equal("10 µm", vm.SnapLadderOptions[2]);
        Assert.Equal("25 µm", vm.SnapLadderOptions[3]);
        Assert.Equal("50 µm", vm.SnapLadderOptions[4]);
    }

    [Fact]
    public void SnapLadder_RendersInCurrentDisplayUnit_NotAFixedList()
    {
        var model = FreshModel();
        model.DisplayUnit = LayoutUnit.Mil;
        var vm = Vm(model);
        vm.DisplayUnit = LayoutUnit.Mil;
        // Seed SnapDbu to match the technology's own ×1 rung (25400 dbu = 1 mil) — the same
        // tech-derived seeding WorkspaceViewModel.NewLayout performs — so the ladder's own R-cmb-1
        // "always contains the current value" rule doesn't insert an EXTRA (here, irrelevant) rung
        // ahead of it purely because this fixture left SnapDbu at its unrelated 1000-dbu default.
        vm.SnapDbu = 25_400;
        var tech = new Technology { DefaultSnapDbu = 25_400 }; // 1 mil at 1000 dbu/um
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        Assert.Equal("1 mil", vm.SnapLadderOptions[0]);
        Assert.Equal("5 mil", vm.SnapLadderOptions[1]);
    }

    // ── brief-snap-ladder-crash.md — the reported crash and its fix ─────────────────────────────────

    [Fact]
    public void SelectingEveryLadderEntryInTurn_InOneSession_NeverThrows()
    {
        // Gate 2 — the direct repro from the stack trace: select every rung in the ladder, one after
        // another. CommitSnapLadderSelection is the exact call OnSnapDistanceSelectionChanged makes.
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 1000 }; // 1 um
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        foreach (var entry in vm.SnapLadderOptions.ToList())
            vm.CommitSnapLadderSelection(entry); // must never throw ArgumentOutOfRangeException

        Assert.Equal(50_000, vm.SnapDbu); // the last rung selected ("50 µm")
    }

    [Fact]
    public void OffLadderSnapDbu_SelectALadderEntry_ThenReenterTheOffLadderValue_NeverCrashesOrBlanks()
    {
        // Gate 3 — opening a .clay with an off-ladder SnapDbu (0.5 mil), selecting a ladder entry,
        // then re-typing the original off-ladder value: no crash, and SnapDistanceText (what the
        // combobox actually shows) is never empty at any point.
        var model = FreshModel();
        model.DisplayUnit = LayoutUnit.Mil;
        model.SnapDbu = LayoutUnits.ToDbu(0.5m, LayoutUnit.Mil, model.DbuPerMicron);
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = LayoutUnits.ToDbu(1m, LayoutUnit.Mil, model.DbuPerMicron) };
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        Assert.Equal("0.5 mil", vm.SnapDistanceText);
        Assert.NotEmpty(vm.SnapDistanceText);

        vm.CommitSnapLadderSelection(vm.SnapLadderOptions[0]); // "1 mil"
        Assert.Equal("1 mil", vm.SnapDistanceText);
        Assert.NotEmpty(vm.SnapDistanceText);

        vm.CommitSnapDistanceText("0.5mil"); // back to the original off-ladder value
        Assert.Equal("0.5 mil", vm.SnapDistanceText);
        Assert.NotEmpty(vm.SnapDistanceText);
    }

    [Fact]
    public void TypedOffLadderValue_ThenSelectALadderEntry_NeverThrows()
    {
        // Gate 4.
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 1000 }; // 1 um
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        vm.CommitSnapDistanceText("3.7um"); // not a member of the 1/5/10/25/50 ladder
        Assert.Equal(3700, vm.SnapDbu);

        vm.CommitSnapLadderSelection(vm.SnapLadderOptions[2]); // "10 um" — must not throw
        Assert.Equal(10_000, vm.SnapDbu);
    }

    [Fact]
    public void SettingSnapDbuDirectly_RaisesNoChangeNotification_OnTheLadderCollection()
    {
        // Gate 5 — the headless regression test that actually catches a reintroduction of the bug:
        // SnapLadderOptions must not even RAISE a change notification in response to SnapDbu, whether
        // or not the new value happens to already be a member. This is what keeps a future "helpful"
        // rewire back onto OnSnapDbuChanged from silently reintroducing the crash.
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 1000 }; // 1 um
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        bool raised = false;
        ((INotifyCollectionChanged)vm.SnapLadderOptions).CollectionChanged += (_, _) => raised = true;

        vm.SnapDbu = 3_700; // deliberately off-ladder
        Assert.False(raised, "SnapLadderOptions must never change in response to SnapDbu");

        vm.SnapDbu = 5_000; // deliberately ON-ladder ("5 um") — still must not raise
        Assert.False(raised, "SnapLadderOptions must not change even when the new value happens to already be a rung");
    }

    [Fact]
    public void SnapLadder_TechnologyChange_RepopulatesTheList_SelectionTextPreserved_NeverBlank()
    {
        // Gate 6, first half (R-cmb-2) — technology resolution/retarget still repopulates the ladder.
        var model = FreshModel();
        model.DisplayUnit = LayoutUnit.Mil;
        var vm = Vm(model);
        vm.DisplayUnit = LayoutUnit.Mil;
        vm.SnapDbu = 25_400; // 1 mil
        var tech1 = new Technology { DefaultSnapDbu = 25_400 };
        vm.ApplyTechResolution(new TechResolution(tech1, null, TechResolutionSource.WorkspaceDefault, []));
        Assert.Contains("1 mil", vm.SnapLadderOptions);
        Assert.Equal("1 mil", vm.SnapDistanceText);

        // Retarget to a DIFFERENT technology (a different DefaultSnapDbu, so a different rung set).
        // The document's own SnapDbu is untouched by a retarget (L1g's own rule) and stays displayed —
        // via SnapDistanceText, never via list membership, even if it's no longer one of the NEW rungs.
        var tech2 = new Technology { DefaultSnapDbu = 50_800 }; // 2 mil
        vm.ApplyTechResolution(new TechResolution(tech2, null, TechResolutionSource.WorkspaceDefault, []));

        Assert.Equal(5, vm.SnapLadderOptions.Count); // repopulated to exactly the 5 standard rungs, nothing extra
        Assert.Equal("2 mil", vm.SnapLadderOptions[0]);
        Assert.Equal(25_400, vm.SnapDbu); // untouched
        Assert.Equal("1 mil", vm.SnapDistanceText); // still shown
        Assert.NotEmpty(vm.SnapDistanceText);
    }

    [Fact]
    public void SnapLadder_DisplayUnitChange_RelabelsEveryEntry_NeverBlanks()
    {
        // Gate 6, second half (R-cmb-3).
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 1000 }; // 1 um
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));
        Assert.Equal("1 µm", vm.SnapLadderOptions[0]);
        Assert.NotEmpty(vm.SnapDistanceText);

        vm.DisplayUnit = LayoutUnit.Mil;

        // Same underlying DBU rungs, relabeled in the new unit (1 um == 1/25.4 mil) — never blank.
        Assert.Equal("0.0394 mil", vm.SnapLadderOptions[0]);
        Assert.NotEmpty(vm.SnapDistanceText);
    }

    [Fact]
    public void CommitSnapDistanceText_TypedValue_ParsesAndCommitsToSnapDbu_NeverTouchesTechnologyDefault()
    {
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 25_400 };
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        vm.CommitSnapDistanceText("2.5mil");

        Assert.Equal(2.5m, LayoutUnits.FromDbu(vm.SnapDbu, LayoutUnit.Mil, model.DbuPerMicron));
        Assert.Equal(25_400, tech.DefaultSnapDbu); // untouched
    }

    [Fact]
    public void CommitSnapDistanceText_InvalidText_RevertsToCanonicalDisplay()
    {
        var model = FreshModel(snapDbu: 5000);
        var vm = Vm(model);
        vm.CommitSnapDistanceText("garbage");
        Assert.Equal(5000, vm.SnapDbu);
        Assert.Equal(vm.SnapText, vm.SnapDistanceText);
    }

    [Fact]
    public void CommitSnapLadderSelection_CommitsImmediately()
    {
        var model = FreshModel();
        var vm = Vm(model);
        var tech = new Technology { DefaultSnapDbu = 1000 };
        vm.ApplyTechResolution(new TechResolution(tech, null, TechResolutionSource.WorkspaceDefault, []));

        vm.CommitSnapLadderSelection(vm.SnapLadderOptions[2]); // "10 um"
        Assert.Equal(10_000, vm.SnapDbu);
    }

    [Fact]
    public void F9_TogglesSnapOff_ThenRestoresLastNonzeroValue()
    {
        var model = FreshModel(snapDbu: 7000);
        var vm = Vm(model);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;

        vm.OnKeyDown(Key.F9, KeyModifiers.None);
        Assert.Equal(0, vm.SnapDbu);

        vm.OnKeyDown(Key.F9, KeyModifiers.None);
        Assert.Equal(7000, vm.SnapDbu);
    }

    [Fact]
    public void F3_TogglesGeometrySnapEnabled()
    {
        var model = FreshModel();
        var vm = Vm(model);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;
        Assert.True(vm.GeometrySnapEnabled);

        vm.OnKeyDown(Key.F3, KeyModifiers.None);
        Assert.False(vm.GeometrySnapEnabled);

        vm.OnKeyDown(Key.F3, KeyModifiers.None);
        Assert.True(vm.GeometrySnapEnabled);
    }

    [Fact]
    public void SKey_AlsoTogglesGeometrySnapEnabled()
    {
        var model = FreshModel();
        var vm = Vm(model);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;
        vm.OnKeyDown(Key.S, KeyModifiers.None);
        Assert.False(vm.GeometrySnapEnabled);
    }

    [Fact]
    public void IncludeIntersectionsEnabled_DefaultsOff()
    {
        var vm = Vm(FreshModel());
        Assert.False(vm.IncludeIntersectionsEnabled);
    }
}
