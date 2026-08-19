using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's SIXTH batch (2026-08-17). Unlike round 5 there is no single root cause: it is a list of
/// thirteen, spanning the Properties inspector, the clipboard, the toolbar, the theme set, the
/// parameter dialog and the palette.
///
/// <para>Two of them are the same shape and worth naming together — <b>a control that cannot say what
/// it is</b>. The Group combo went blank because its selection was assigned before its items existed;
/// the two panel buttons said nothing about whether their panel was on screen. Both are fixed by
/// reading the truth (the item list, the dock tree) at the moment of display rather than tracking a
/// copy of it.</para>
/// </summary>
public class WBondRound6Tests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>A design of <paramref name="arrays"/> arrays, each holding <paramref name="perArray"/> wires.</summary>
    private static WBondDesign Design(int arrays = 2, int perArray = 2)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = "G" + (a + 1) };
            for (int w = 0; w < perArray; w++)
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(a * 50, w * 6.0, 4), Point3.Mils(a * 50 + 30, w * 6.0, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            design.Arrays.Add(array);
        }

        return design;
    }

    private static WireSelection Wires(params int[] flatIndices) =>
        new() { Wires = [.. flatIndices] };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1. The Properties inspector's wire context
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A multi-wire selection states both counts</b> (owner: "x wires selected from y groups").
    ///
    /// <para>The group count is the one that says whether the regroup about to be offered would MERGE
    /// anything — four wires from one group is a rename, four from three is a merge — and it is not
    /// derivable from anything else on screen.</para>
    /// </summary>
    [Fact]
    public void AMultiWireSelection_StatesTheWireAndGroupCounts()
    {
        var vm = new WBondViewModel(Design(arrays: 2, perArray: 2));
        var panel = new WBondWirePropertiesViewModel();
        panel.SetContext(vm);

        // Two wires, both out of G1.
        vm.Selection = Wires(0, 1);
        Assert.Equal(2, panel.SelectedWireCount);
        Assert.Equal(1, panel.SelectedGroupCount);
        Assert.Equal("2 wires selected from 1 group", panel.EmptyMessage);

        // …and one from each group.
        vm.Selection = Wires(0, 2);
        Assert.Equal(2, panel.SelectedWireCount);
        Assert.Equal(2, panel.SelectedGroupCount);
        Assert.Equal("2 wires selected from 2 groups", panel.EmptyMessage);
    }

    /// <summary>
    /// <b>"Group Wires As…" is offered only for two or more</b> (owner). For a single wire the Group
    /// combo directly above it already moves that wire into any group, including a new one — a second
    /// control for the same edit, one of them behind a modal.
    /// </summary>
    [Fact]
    public void TheGroupWiresButton_IsOfferedOnlyForTwoOrMore()
    {
        var vm = new WBondViewModel(Design(arrays: 2, perArray: 2));
        var panel = new WBondWirePropertiesViewModel();
        panel.SetContext(vm);

        vm.Selection = new WireSelection();
        Assert.False(panel.CanGroupWires);

        vm.Selection = Wires(0);
        Assert.False(panel.CanGroupWires);   // the combo's job, not the button's

        vm.Selection = Wires(0, 2);
        Assert.True(panel.CanGroupWires);
    }

    /// <summary>
    /// <b>The group name is CLEARED when the panel empties</b> — one half of the blank-combo fix
    /// (owner: "sometimes the Group combobox item is empty when I click on a wire").
    ///
    /// <para>It used to be left standing, so selecting a wire in the same group as the last one
    /// re-assigned <c>GroupName</c> the value it already held — which raises no change notification,
    /// so the view was never told to put the combo back after an item-list rebuild had dropped its
    /// selection. Clearing it makes every single-wire selection a real change.</para>
    /// </summary>
    [Fact]
    public void TheGroupName_IsClearedWheneverThePanelEmpties()
    {
        var vm = new WBondViewModel(Design(arrays: 2, perArray: 2));
        var panel = new WBondWirePropertiesViewModel();
        panel.SetContext(vm);

        vm.Selection = Wires(0);
        Assert.Equal("G1", panel.GroupName);

        vm.Selection = new WireSelection();
        Assert.Equal("", panel.GroupName);

        int notifications = 0;
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WBondWirePropertiesViewModel.GroupName)) notifications++;
        };

        // The SAME group as before: the notification the view needs must still arrive.
        vm.Selection = Wires(1);
        Assert.Equal("G1", panel.GroupName);
        Assert.True(notifications > 0, "selecting another wire in the same group must re-raise GroupName");
    }

    /// <summary>
    /// <b>Whatever <c>GroupName</c> says is always in <c>AvailableGroups</c></b> — the invariant a
    /// ComboBox needs to render a selection at all. A name absent from the item list renders blank,
    /// which is exactly what the owner reported seeing.
    /// </summary>
    [Fact]
    public void TheGroupName_IsAlwaysOneOfTheOfferedGroups()
    {
        var vm = new WBondViewModel(Design(arrays: 3, perArray: 1));
        var panel = new WBondWirePropertiesViewModel();
        panel.SetContext(vm);

        for (int wire = 0; wire < 3; wire++)
        {
            vm.Selection = Wires(wire);
            Assert.Contains(panel.GroupName, panel.AvailableGroups);
        }
    }

    /// <summary>
    /// The VIEW assigns the combo's items BEFORE its selection, and does so from code rather than
    /// through a binding.
    ///
    /// <para>This is a source assertion because the ordering is the whole defect and it cannot be
    /// reproduced without a real ComboBox: an <c>ItemsSource</c> binding is attached when the
    /// DataContext reaches the control, which is AFTER this view's own PropertyChanged handler runs —
    /// so a selection set first was resolved against the old item list and silently dropped.</para>
    /// </summary>
    [Fact]
    public void TheGroupCombo_GetsItsItemsBeforeItsSelection()
    {
        var xaml = Read("src", "Ui", "Views", "Properties", "WBondWirePropertiesView.axaml");

        // No ItemsSource binding on the combo — the code-behind owns both halves.
        int combo = xaml.IndexOf("x:Name=\"GroupCombo\"", StringComparison.Ordinal);
        Assert.True(combo >= 0);
        Assert.DoesNotContain("ItemsSource=\"{Binding AvailableGroups}\"", xaml, StringComparison.Ordinal);

        var code = Read("src", "Ui", "Views", "Properties", "WBondWirePropertiesView.axaml.cs");
        int items = code.IndexOf("GroupCombo.ItemsSource = Vm.AvailableGroups;", StringComparison.Ordinal);
        int selection = code.IndexOf("GroupCombo.SelectedItem = Vm.GroupName;", StringComparison.Ordinal);

        Assert.True(items >= 0 && selection >= 0);
        Assert.True(items < selection, "the item list must be assigned before the selection, or the selection is dropped");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1b. Alt-drag scales the SELECTION (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Alt-drag in the PROFILE view scales the whole GROUP, and only that group</b> (owner,
    /// 2026-08-17 — first "alt dragging a wire causes all wires to change; it should only be the wires
    /// that are selected", then "it needs to change ALL the wires in the group at once").
    ///
    /// <para>Both reports are one rule: the unit is the ARRAY. The old code used the
    /// <see cref="LoopProfile"/> as a proxy for it, and a profile routinely spans every array in the
    /// design — the shipped default creates ONE and every array references it — so a drag in G1 moved
    /// G2 as well. The wires here deliberately share a profile, which is the configuration that
    /// reproduced it.</para>
    /// </summary>
    [Fact]
    public void AltDrag_ScalesTheWholeGroup_AndOnlyThatGroup()
    {
        var vm = new WBondViewModel(Design(arrays: 2, perArray: 2));

        var wires = vm.Design.AllWires().ToList();
        var spansBefore = wires.Select(w => w.SpanMetres()).ToList();

        // ONE wire of G1 selected; both of G1 move, neither of G2 does.
        vm.Selection = Wires(0);
        Assert.Equal(2, vm.ScaleSelection(spanFactor: 1.5, heightFactor: 1.0,
                                          moveOutputFoot: true, wholeArray: true));

        Assert.Equal(spansBefore[0] * 1.5, wires[0].SpanMetres(), 9);
        Assert.Equal(spansBefore[1] * 1.5, wires[1].SpanMetres(), 9);
        Assert.Equal(spansBefore[2], wires[2].SpanMetres(), 12);
        Assert.Equal(spansBefore[3], wires[3].SpanMetres(), 12);
    }

    /// <summary>
    /// A selection spanning two groups moves both groups whole — the promotion is per-array, not "the
    /// first array it found".
    /// </summary>
    [Fact]
    public void AltDrag_PromotesEveryGroupTheSelectionTouches()
    {
        var vm = new WBondViewModel(Design(arrays: 3, perArray: 2));

        var wires = vm.Design.AllWires().ToList();
        var spansBefore = wires.Select(w => w.SpanMetres()).ToList();

        vm.Selection = Wires(0, 5);   // one wire of G1, one of G3
        Assert.Equal(4, vm.ScaleSelection(spanFactor: 2.0, heightFactor: 1.0,
                                          moveOutputFoot: true, wholeArray: true));

        foreach (int i in new[] { 0, 1, 4, 5 })
            Assert.Equal(spansBefore[i] * 2.0, wires[i].SpanMetres(), 9);

        foreach (int i in new[] { 2, 3 })   // G2 is untouched
            Assert.Equal(spansBefore[i], wires[i].SpanMetres(), 12);
    }

    /// <summary>
    /// …and the LAYOUT view does not promote: there each wire is drawn at its own place among the pads
    /// and an alt-drag stretches THAT wire onto THAT pad, so the wires of a group are not
    /// interchangeable. The two views pass opposite values and the difference is deliberate.
    /// </summary>
    [Fact]
    public void AltDrag_InTheLayoutView_ScalesOnlyTheSelectedWire()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 3));

        var wires = vm.Design.AllWires().ToList();
        var spansBefore = wires.Select(w => w.SpanMetres()).ToList();

        vm.Selection = Wires(1);
        Assert.Equal(1, vm.ScaleSelection(spanFactor: 1.5, heightFactor: 1.0, moveOutputFoot: true));

        Assert.Equal(spansBefore[1] * 1.5, wires[1].SpanMetres(), 9);
        Assert.Equal(spansBefore[0], wires[0].SpanMetres(), 12);
        Assert.Equal(spansBefore[2], wires[2].SpanMetres(), 12);

        // The two callers, stated where they are: the profile canvas promotes, the layout overlay
        // does not. A test on the arithmetic alone cannot see which view asked.
        Assert.Contains("wholeArray: true",
                        Read("src", "Ui", "Controls", "WBondProfileCanvas.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("wholeArray",
                              Read("src", "Ui", "WBond", "WBondLayoutOverlay.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whichever unit is in force, <b>an alt-drag reaches the dragged group and stops there</b> — the
    /// second half of the original defect, and the one that outlived the drag: the old path wrote the
    /// scaled height back onto the loop profile every array followed, so G2's wires moved when G1 was
    /// dragged.
    ///
    /// <para>There is no shared object left to write (2026-08-18), which is why this now reads as a
    /// plain statement about geometry rather than as a statement about a profile's stored height.</para>
    /// </summary>
    [Fact]
    public void AltDrag_LeavesTheOtherGroupExactlyAlone()
    {
        var vm = new WBondViewModel(Design(arrays: 2, perArray: 2));

        var wires = vm.Design.AllWires().ToList();
        var heightsBefore = wires.Select(w => ProfileEnvelope.HeightAt(w, 0.5)).ToList();
        var otherGroupPoints = wires.Skip(2).Select(w => w.Points.ToArray()).ToList();

        vm.Selection = Wires(0);
        Assert.Equal(2, vm.ScaleSelection(spanFactor: 1.0, heightFactor: 2.0,
                                          moveOutputFoot: true, wholeArray: true));

        Assert.Equal(heightsBefore[0] * 2.0, ProfileEnvelope.HeightAt(wires[0], 0.5), 0);
        Assert.Equal(heightsBefore[1] * 2.0, ProfileEnvelope.HeightAt(wires[1], 0.5), 0);

        // The other group, to the nanometre — not merely "about the same height".
        for (int i = 0; i < otherGroupPoints.Count; i++)
            Assert.Equal(otherGroupPoints[i], wires[2 + i].Points.ToArray());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2. Delete and the clipboard, in the LAYOUT host
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Delete removes the selected wires from the Layout Editor</b> (owner: the key did nothing
    /// there). The standalone wBond editor has always had this on its own tunnel handler — a handler
    /// that does not exist in the Layout Editor, where the wires reach the canvas through the overlay
    /// seam instead.
    /// </summary>
    [Fact]
    public void Delete_RemovesTheSelectedWires_ThroughTheOverlay()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 3));
        var overlay = new WBondLayoutOverlay(vm);

        vm.Selection = Wires(1);

        Assert.True(overlay.OnKeyDown(Key.Delete, KeyModifiers.None));
        Assert.Equal(2, vm.Design.WireCount);
    }

    /// <summary>
    /// …and DECLINES the key with no wire selected, which is what keeps the layout's own delete
    /// working in the same view. An unconditional claim here would swallow every Delete meant for a
    /// selected shape or instance.
    /// </summary>
    [Fact]
    public void Delete_IsDeclinedWithNoWireSelected()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 3));
        var overlay = new WBondLayoutOverlay(vm);

        Assert.False(overlay.OnKeyDown(Key.Delete, KeyModifiers.None));
        Assert.Equal(3, vm.Design.WireCount);
    }

    /// <summary>
    /// <b>Every combination of wires, geometry and instances survives a copy/paste round trip</b>
    /// (owner, mid-round: "copy/paste should work with any combination of wires and or geometry
    /// primitives or cell instances").
    ///
    /// <para>The envelope is what makes that true without either editor knowing what the other can
    /// hold: a single-kind selection travels as the plain payload every existing paste path already
    /// reads, and only a genuinely mixed one is wrapped. This asserts the property at the seam both
    /// the Layout Editor and the wBond editor go through.</para>
    /// </summary>
    [Theory]
    [InlineData(true, false)]    // wires only
    [InlineData(false, true)]    // geometry and/or instances only
    [InlineData(true, true)]     // both
    public void EveryClipboardCombination_ComesBackOutOfTheEnvelope(bool wires, bool layout)
    {
        string? wiresJson = wires ? "{\"marker\":\"wires\"}" : null;
        string? layoutJson = layout ? "{\"marker\":\"layout\"}" : null;

        string? text = WBondMixedClipboard.Compose(wiresJson, layoutJson);
        Assert.NotNull(text);

        var (backWires, backLayout) = WBondMixedClipboard.Unwrap(text);

        // A single-kind payload is offered to BOTH parsers unchanged — each refuses what is not its
        // own — so what matters is that the half that was copied is reachable, not that the other
        // half is null.
        if (wires) Assert.Contains("\"wires\"", backWires);
        if (layout) Assert.Contains("\"layout\"", backLayout);

        // Mixed is wrapped; single-kind is not, or every existing paste path would stop reading it.
        Assert.Equal(wires && layout, WBondMixedClipboard.IsMixed(text));
    }

    /// <summary>
    /// The Layout Editor's own copy and paste reach the wire layer — the wiring a headless test
    /// cannot exercise, since both ends are <c>IClipboard</c> traffic inside a view.
    /// </summary>
    [Fact]
    public void TheLayoutEditor_CopiesAndPastesWiresToo()
    {
        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");

        // COPY: the wire half is tried first, and composes through the shared envelope.
        Assert.Contains("if (await CopyWithWiresAsync()) return;", code, StringComparison.Ordinal);
        Assert.Contains("WBondMixedClipboard.Compose(wiresJson, layoutJson)", code, StringComparison.Ordinal);

        // PASTE: likewise, and BOTH halves move by the wire half's own free-pitch displacement, or a
        // mixed paste comes apart.
        Assert.Contains("if (await PasteWithWiresAsync(clipboard)) return;", code, StringComparison.Ordinal);
        Assert.Contains("editor.FreePasteOffset(payload, WBondDefaults.PastePitchNm)", code, StringComparison.Ordinal);
        Assert.Contains("pasted += PasteLayoutHalf(layoutJson, dx, dy);", code, StringComparison.Ordinal);

        // CUT takes both kinds, matching what the copy just wrote.
        Assert.Contains("wires.DeleteSelectedWires() > 0", code, StringComparison.Ordinal);
        Assert.Contains("Vm?.CutSelectionAfterCopy();", code, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3. The theme set
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The winning wBond palette IS the default now</b> (owner, 2026-08-17: "copy the colour theme
    /// colours from wBond-Orchid to the default colour theme … then delete wBond-Orchid as an
    /// option"). One theme ships, and its wire colours are the ones the owner chose.
    /// </summary>
    [Fact]
    public void TheOrchidColours_AreTheDefaultTheme_AndOrchidIsGone()
    {
        Assert.Equal("Default", ThemeResolver.DefaultThemeName);
        Assert.Equal(["Default"], ThemeResolver.BuiltInThemeNames);

        string dir = Path.Combine(RepoRoot(), "src", "Ui", "Assets", "Color");
        Assert.False(File.Exists(Path.Combine(dir, "wBond-Orchid.ccolor")));

        // Resolving through the real chain — including a NAME NOBODY SHIPS ANY MORE, which is what a
        // .cws or preferences.json written before today still asks for. It must land on the orchid
        // colours rather than on something that reads as "the theme did nothing".
        foreach (string name in new[] { "Default", "wBond-Orchid" })
        {
            var theme = ThemeResolver.Resolve(name);
            Assert.Equal(new Rgba(165, 64, 130), theme.Resolve(ColorRole.WBondWire, ColorVariant.Light));
            Assert.Equal(new Rgba(214, 122, 182), theme.Resolve(ColorRole.WBondWire, ColorVariant.Dark));
        }
    }

    /// <summary>
    /// Every entry point that has to turn "no recorded preference" into a theme uses that one name —
    /// the three application shells, and the two places a chosen name is compared against the default
    /// to decide whether it is worth recording at all.
    /// </summary>
    [Fact]
    public void NoRecordedPreference_MeansTheDefaultThemeEverywhere()
    {
        foreach (var shell in new[] { "App.axaml.cs", "WBondApp.axaml.cs", "HarmonicaApp.axaml.cs" })
            Assert.Contains("ThemeResolver.Resolve(prefs.ActiveThemeName ?? ThemeResolver.DefaultThemeName)",
                            Read("src", "Ui", shell), StringComparison.Ordinal);

        // Recorded as null when it IS the default, in both places that write one.
        Assert.Contains("activeName != ThemeResolver.DefaultThemeName",
                        Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml.cs"), StringComparison.Ordinal);
        Assert.Contains("ThemeService.Active.Name == ThemeResolver.DefaultThemeName",
                        Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Choosing a different theme repaints the WIRES in the layout host</b> (owner, 2026-08-17: "I
    /// changed the colour theme, but the wire colours in the wBond layout host did not update").
    ///
    /// <para>Two different events, and the view needs both. <c>ActualThemeVariantChanged</c> is
    /// light-vs-dark; <c>ThemeService.ThemeChanged</c> is a different theme being selected. The canvas
    /// invalidates itself on the second, but it redraws the overlay from the <c>WBondRenderTheme</c>
    /// this view handed it — a plain object with no notifications of its own — so without a
    /// re-resolve the layout repainted underneath wires still in the old colours.</para>
    /// </summary>
    [Fact]
    public void ChoosingAnotherTheme_RepaintsTheWiresInTheLayoutHost()
    {
        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");

        Assert.Contains("ThemeService.ThemeChanged += OnActiveThemeChanged;", code, StringComparison.Ordinal);
        Assert.Contains("ThemeService.ThemeChanged -= OnActiveThemeChanged;", code, StringComparison.Ordinal);

        // The handler re-resolves the palette AND repaints — the overlay is not part of the layout's
        // own path cache, so invalidating the canvas alone would not redraw it.
        int at = code.IndexOf("private void OnActiveThemeChanged", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = code[at..(at + 250)];
        Assert.Contains("ApplyCanvasOverlay();", body, StringComparison.Ordinal);
        Assert.Contains("InvalidateOverlay();", body, StringComparison.Ordinal);

        // Dropped on detach: ThemeService.ThemeChanged is STATIC, so a handler left on it holds every
        // document view ever opened.
        int detach = code.IndexOf("protected override void OnDetachedFromVisualTree", StringComparison.Ordinal);
        Assert.True(detach >= 0);
        Assert.Contains("ThemeService.ThemeChanged -= OnActiveThemeChanged;",
                        code[detach..(detach + 400)], StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4. The toolbar
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The two mesh toggles sit beside EM Setup</b> (owner: "these are all related items so they
    /// should be close together"), and no longer among the drawing-tool toggles.
    /// </summary>
    [Fact]
    public void TheMeshToggles_SitRightOfEmSetup()
    {
        var xaml = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        int emSetup = xaml.IndexOf("Click=\"OnOpenEmSetup\"", StringComparison.Ordinal);
        int crossSection = xaml.IndexOf("ToolTip.Tip=\"Show EM cross-section mesh\"", StringComparison.Ordinal);
        int surface = xaml.IndexOf("ToolTip.Tip=\"Show full-wave surface mesh\"", StringComparison.Ordinal);

        Assert.True(emSetup >= 0 && crossSection >= 0 && surface >= 0);
        Assert.True(emSetup < crossSection, "the cross-section toggle must follow EM Setup");
        Assert.True(crossSection < surface, "the two mesh toggles stay in their own order");

        // Nothing else between them: they read as one group or the grouping is not the point.
        int pcellPins = xaml.IndexOf("ToolTip.Tip=\"Show PCell pins\"", StringComparison.Ordinal);
        Assert.True(pcellPins < emSetup, "the pin toggle stays with the drawing tools it belongs to");
    }

    /// <summary>
    /// <b>The two wBond panel buttons say whether their panel is in view</b> (owner). They are
    /// ToggleButtons whose checked state is pushed from the dock tree, never bound two-way: the truth
    /// is the tree, which a panel closed by its own tab X also changes.
    /// </summary>
    [Fact]
    public void TheWirePanelButtons_ShowWhetherTheirPanelIsInView()
    {
        var xaml = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        foreach (var name in new[] { "WireProfileBtn", "WireInductanceBtn" })
        {
            int at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at >= 0);
            Assert.Equal("<ToggleButton", xaml[(xaml.LastIndexOf('<', at))..(xaml.LastIndexOf('<', at) + 13)]);
        }

        // The state is re-read on every notification rather than tracked here, and the subscription is
        // dropped when the view leaves the tree — the workspace outlives every document view.
        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        Assert.Contains("ToolPanelVisibilityChanged += UpdateWirePanelCheckedStates", code, StringComparison.Ordinal);
        Assert.Contains("ToolPanelVisibilityChanged -= UpdateWirePanelCheckedStates", code, StringComparison.Ordinal);
        Assert.Contains("SubscribeToPanelVisibility(null);", code, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5. First use: the two panels, arranged
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The first wBond a workspace ever sees arranges the two panels</b> (owner, with their own
    /// <c>.cws</c> as the reference): the Wire Profile tabbed into the left column's Properties group
    /// as its front tab, and the Array Inductance in its own narrow column beside the documents.
    /// </summary>
    [Fact]
    public void TheFirstUseArrangement_PlacesBothPanelsWhereTheOwnerKeepsThem()
    {
        var live = new CwsDockLayout
        {
            Panels =
            {
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Open = true, Side = DockSide.Left, Group = 0, Order = 0, Active = true, Proportion = 0.466 },
                new CwsDockPanel { Id = DockPanelIds.Properties,  Open = true, Side = DockSide.Left, Group = 1, Order = 0, Active = true, Proportion = 0.534 },
                new CwsDockPanel { Id = DockPanelIds.Analyses,    Open = true, Side = DockSide.Left, Group = 1, Order = 1, Proportion = 0.534 },
                new CwsDockPanel { Id = DockPanelIds.Palette,     Open = true, Side = DockSide.Right, Group = 0, Order = 0, Active = true, Proportion = 1 },
            },
        };

        WorkspaceViewModel.ArrangeWBondPanels(live);

        var profile = Assert.Single(live.Panels.Where(p => p.Id == DockPanelIds.WBondProfile));
        Assert.True(profile.Open);
        Assert.Equal(DockSide.Left, profile.Side);
        Assert.False(profile.Inboard);
        Assert.Equal(1, profile.Group);                 // the Properties group, not a column of its own
        Assert.Equal(2, profile.Order);                 // after Properties and Analyses
        Assert.Equal(0.534, profile.Proportion);
        Assert.True(profile.Active);                    // the front tab — it is what the user was sent to look at

        // Only ONE tab in a group can be in front, and leaving the old flag set is how a panel comes
        // back BEHIND the one that was there (the round-5 report, one surface over).
        Assert.False(live.Panels.First(p => p.Id == DockPanelIds.Properties).Active);
        Assert.Single(live.Panels.Where(p => p.Side == DockSide.Left && !p.Inboard && p.Group == 1 && p.Active));

        var inductance = Assert.Single(live.Panels.Where(p => p.Id == DockPanelIds.WBondInductance));
        Assert.True(inductance.Inboard);                // between the tool columns and the documents
        Assert.Equal(DockSide.Left, inductance.Side);
        Assert.True(inductance.Active);

        // The COLUMN's own width lives on the side entry, and it is the narrow strip the owner keeps —
        // never that file's 0.8, which is the container holding the column AND the documents.
        var side = Assert.Single(live.Sides.Where(s => s.Side == DockSide.Left && s.Inboard));
        Assert.InRange(side.Proportion, 0.15, 0.25);

        // Nothing else moved. The reference .cws described a project tree, a palette and a document
        // order too, and none of that is this command's business.
        Assert.Equal(0, live.Panels.First(p => p.Id == DockPanelIds.ProjectTree).Group);
        Assert.Equal(1, live.Panels.First(p => p.Id == DockPanelIds.Palette).Proportion);
        Assert.Equal(DockSide.Right, live.Panels.First(p => p.Id == DockPanelIds.Palette).Side);
    }

    /// <summary>
    /// A panel the user has already placed by hand is <b>opened, never moved</b>. Someone who found
    /// these in View ▸ Panels before ever generating a wBond has already answered the question this
    /// arrangement exists to answer.
    /// </summary>
    [Fact]
    public void TheFirstUseArrangement_LeavesAHandPlacedPanelWhereItIs()
    {
        var live = new CwsDockLayout
        {
            Panels =
            {
                new CwsDockPanel { Id = DockPanelIds.WBondProfile, Open = false, Side = DockSide.Bottom, Group = 3, Order = 1, Proportion = 0.42 },
            },
        };

        WorkspaceViewModel.ArrangeWBondPanels(live);

        var profile = Assert.Single(live.Panels.Where(p => p.Id == DockPanelIds.WBondProfile));
        Assert.True(profile.Open);                  // opened…
        Assert.Equal(DockSide.Bottom, profile.Side); // …and otherwise untouched
        Assert.Equal(3, profile.Group);
        Assert.Equal(0.42, profile.Proportion);
    }

    /// <summary>
    /// <b>The arrangement is gated PER WORKSPACE, by the live layout itself — never by a per-user
    /// preference</b> (owner, 2026-08-17: a new workspace, the first wBond in it, and both panels
    /// floating).
    ///
    /// <para>The old gate was <c>wbond_panels_arranged</c> in <c>preferences.json</c>, set the first
    /// time any workspace on the machine needed the panels. A panel's home is per-workspace, so by the
    /// second workspace the flag was spent while that workspace still had nowhere to put either panel —
    /// and <c>ShowToolPanel</c>'s only answer for a panel with no home is to float it. The preference is
    /// deleted rather than rescoped: the per-panel test one level down already does the job.</para>
    ///
    /// <para>Asserted on the SOURCE because the alternative is writing to this machine's real
    /// preferences file. The behavioural half — that a second arrangement pass leaves placed panels
    /// alone — is the two tests above this one, which exercise <c>ArrangeWBondPanels</c> directly.</para>
    /// </summary>
    [Fact]
    public void TheArrangement_IsGatedByTheLiveLayout_NotByAPerUserPreference()
    {
        var code  = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.WBondPanels.cs");
        var prefs = Read("src", "Ui", "Theming", "AppPreferences.cs");

        Assert.DoesNotContain("WBondPanelsArranged", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AppPreferencesIo", code, StringComparison.Ordinal);

        // Gone from the preference type too — and its own key must not come back under another name.
        Assert.DoesNotContain("public bool? WBondPanelsArranged", prefs, StringComparison.Ordinal);
        Assert.DoesNotContain("[JsonPropertyName(\"wbond_panels_arranged\")]", prefs, StringComparison.Ordinal);

        // What remains IS the gate: per panel, against the layout in hand.
        Assert.Contains("IsPlacedAnywhere(live, DockPanelIds.WBondProfile)", code, StringComparison.Ordinal);
        Assert.Contains("IsPlacedAnywhere(live, DockPanelIds.WBondInductance)", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reported bug, as a value: a BRAND-NEW workspace — a layout that has never held either wire
    /// panel — gets both of them DOCKED, at the owner's own placement, rather than floating.
    ///
    /// <para>This is the case the per-installation flag broke, and it is not covered by the two
    /// "already placed" tests above: those start from a layout that names the panel.</para>
    /// </summary>
    [Fact]
    public void AFreshWorkspace_DocksBothPanels_RatherThanFloatingThem()
    {
        // A new workspace's shipped layout: project tree left, palette right, messages bottom. No wire
        // panel anywhere, and nothing floating.
        var live = new CwsDockLayout
        {
            Panels =
            {
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Open = true, Side = DockSide.Left,   Group = 0, Order = 0, Active = true,  Proportion = 0.466 },
                new CwsDockPanel { Id = DockPanelIds.Properties,  Open = true, Side = DockSide.Left,   Group = 1, Order = 0, Active = true,  Proportion = 0.534 },
                new CwsDockPanel { Id = DockPanelIds.Messages,    Open = true, Side = DockSide.Bottom, Group = 0, Order = 0, Active = true,  Proportion = 0.2   },
                new CwsDockPanel { Id = DockPanelIds.Palette,     Open = true, Side = DockSide.Right,  Group = 0, Order = 0, Active = true,  Proportion = 1     },
            },
        };

        WorkspaceViewModel.ArrangeWBondPanels(live);

        // Wire Profile — tabbed into the left column's lower group, in front, exactly as the owner's
        // .cws has it (Left / outboard / with Properties / Active).
        var profile = Assert.Single(live.Panels.Where(p => p.Id == DockPanelIds.WBondProfile));
        Assert.True(profile.Open);
        Assert.Equal(DockSide.Left, profile.Side);
        Assert.False(profile.Inboard);
        Assert.Equal(1, profile.Group);                       // Properties' group, not a row of its own
        Assert.True(profile.Active);
        Assert.False(live.Panels.Single(p => p.Id == DockPanelIds.Properties).Active);   // …so it is the FRONT tab

        // Array Inductance — its own narrow column between the tools and the documents.
        var inductance = Assert.Single(live.Panels.Where(p => p.Id == DockPanelIds.WBondInductance));
        Assert.True(inductance.Open);
        Assert.Equal(DockSide.Left, inductance.Side);
        Assert.True(inductance.Inboard);
        Assert.Contains(live.Sides, s => s.Side == DockSide.Left && s.Inboard);

        // The point of the whole exercise: neither is floating.
        Assert.Empty(live.FloatingWindows);

        // …and nothing the workspace already had was moved.
        Assert.Equal(DockSide.Right,  live.Panels.Single(p => p.Id == DockPanelIds.Palette).Side);
        Assert.Equal(DockSide.Bottom, live.Panels.Single(p => p.Id == DockPanelIds.Messages).Side);
        Assert.Equal(0.466, live.Panels.Single(p => p.Id == DockPanelIds.ProjectTree).Proportion);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6. The parameter dialog
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) PlaceWBond()
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);

        return (vm, comp, editor);
    }

    /// <summary>
    /// <b>GroundPlane is a picker, and its first entry is what UNSET means</b> (owner: "if GroundPlane
    /// is a bool … it needs to be a checkbox or combobox entry").
    ///
    /// <para>The engine reads it through <c>IsTrue</c>, so only three values do anything; the text box
    /// it used to be accepted "yes" and silently disabled the plane, which changes every inductance in
    /// the component.</para>
    /// </summary>
    [Fact]
    public void GroundPlane_IsAPickerOfThreeValues()
    {
        var (_, comp, editor) = PlaceWBond();

        Assert.Equal(["As designed", "Yes", "No"], editor.WBondGroundPlaneChoices);
        Assert.Equal(0, editor.WBondGroundPlaneIndex);     // blank means the design's own setting

        editor.WBondGroundPlaneIndex = 2;
        Assert.Equal("false", comp.Parameters.First(p => p.Name == "GroundPlane").Expression);

        editor.WBondGroundPlaneIndex = 1;
        Assert.Equal("true", comp.Parameters.First(p => p.Name == "GroundPlane").Expression);

        editor.WBondGroundPlaneIndex = 0;
        Assert.Equal("", comp.Parameters.First(p => p.Name == "GroundPlane").Expression);
    }

    /// <summary>
    /// A value the picker does not offer — an expression typed before it existed — is <b>shown and
    /// kept</b>. A ComboBox whose selection is absent from its items renders blank, which reads as the
    /// value having been lost; rewriting it would actually lose it.
    /// </summary>
    [Fact]
    public void GroundPlane_KeepsAnExpressionThePickerDoesNotOffer()
    {
        var (vm, comp, editor) = PlaceWBond();

        comp.Parameters.First(p => p.Name == "GroundPlane").Expression = "gnd_on";
        editor.SetTargetDirect(vm, comp, showClose: false);

        Assert.Equal("gnd_on", editor.WBondGroundPlaneChoices.Last());
        Assert.Equal(editor.WBondGroundPlaneChoices.Count - 1, editor.WBondGroundPlaneIndex);
        Assert.Equal("gnd_on", comp.Parameters.First(p => p.Name == "GroundPlane").Expression);
    }

    /// <summary>
    /// The design summary and the Update Layout button are on separate ROWS (owner: they "render
    /// overtop of each other if the text string gets too long"). The summary's length is not bounded
    /// by anything — it grows with the array count and gains "· with overrides" — so any share of one
    /// row is a share it will eventually outgrow.
    /// </summary>
    [Fact]
    public void TheUpdateLayoutButton_SitsOnItsOwnRow()
    {
        var xaml = Read("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml");

        int at = xaml.IndexOf("Content=\"Update Layout\"", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string row = xaml[(at - 200)..(at + 200)];
        Assert.Contains("Grid.Row=\"1\"", row, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Carried/Linked is the wBond panel's LAST row</b> (owner, 2026-08-17: "an advanced feature that
    /// only wBond experts would use").
    ///
    /// <para>It used to sit third, directly under the design summary — the panel's most consequential
    /// and least-used control placed in front of every control an ordinary user actually touches. The
    /// ordinary flow never sets it at all: a placed wBond is Carried by construction and
    /// <c>WBondCellSeeding</c> flips it to Linked (WB45a). Asserted as a full ORDER rather than as
    /// "after the checkbox", so a later insertion that lands between two of these is caught too.</para>
    ///
    /// <para>The note line has to stay directly beneath its own box — it carries the one consequence
    /// that separates the two options, and down here it is read less often, not more.</para>
    /// </summary>
    [Fact]
    public void TheSourceRow_IsLastInTheWBondPanel_BeingTheExpertsOnlyControl()
    {
        var xaml = Read("src", "Ui", "Views", "ParameterEditor", "ParameterEditorView.axaml");

        int At(string needle)
        {
            int i = xaml.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(i >= 0, $"Not found in ParameterEditorView.axaml: {needle}");
            return i;
        }

        int summary = At("{Binding WBondSummary}");
        int arrays  = At("Text=\"Arrays\"");
        int pitch   = At("Text=\"Symbol Pitch\"");
        int ground  = At("Text=\"Ground plane\"");
        int refPin  = At("Content=\"External reference pin\"");
        int source  = At("{Binding WBondSourceOptions}");
        int note    = At("{Binding WBondSourceNote}");

        Assert.True(summary < arrays && arrays < pitch && pitch < ground && ground < refPin,
                    "The ordinary wBond rows changed order — this test only pins where Source sits.");
        Assert.True(refPin < source, "Source must be the LAST row of the wBond panel, after every ordinary control.");
        Assert.True(source < note, "The consequence note belongs directly under its own box.");

        // …and still INSIDE the panel: moved to its tail, not out from under `IsWBond` into rows that
        // every other component type would then render.
        Assert.True(note < At("<!-- Generic parameter rows, LAST"),
                    "Source escaped the wBond panel — it must stay within the IsWBond StackPanel.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  7. Dropping a wBond into a layout (WB40b)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static LayoutEditorViewModel NewLayout() => new(new LayoutView());

    /// <summary>
    /// <b>A wBond dropped out of the palette gives the layout its wire layer</b> (owner: "Cannot drag
    /// and drop a wBond component from the Library Palette into a layout").
    ///
    /// <para>It has no PCell generator and never will — no wire enters a <c>.clay</c> — so the
    /// generator-based drop path correctly refused it and there was nothing behind it to say yes
    /// instead. What it produces is not a shape: it is this session's wire design, the same one a cell
    /// with a <c>.wBond</c> beside its <c>.clay</c> arrives with.</para>
    /// </summary>
    [Fact]
    public void DroppingAWBond_AttachesAWireDesignAtTheDropPoint()
    {
        var layout = NewLayout();

        Assert.Null(layout.WireDesign);
        Assert.True(layout.CanDropWBond());

        long xDbu = 40_000, yDbu = 25_000;   // 1 DBU = 1 nm at the default resolution
        Assert.True(layout.CommitWBondDrop(xDbu, yDbu));

        Assert.NotNull(layout.WireDesign);
        Assert.NotNull(layout.WireEditor);
        Assert.NotNull(layout.WireOverlay);
        Assert.Equal(1, layout.WireDesign!.WireCount);

        // The wire lands where the drop did: its input foot carries the shipped design's own offset.
        var wire = layout.WireDesign.AllWires().Single();
        long xNm = WBondSnap.ToNm(xDbu, layout.Model.DbuPerMicron);
        long yNm = WBondSnap.ToNm(yDbu, layout.Model.DbuPerMicron);
        Assert.Equal(WBondEmbedding.DefaultWire.Start.X + xNm, wire.Points[0].X);
        Assert.Equal(WBondEmbedding.DefaultWire.Start.Y + yNm, wire.Points[0].Y);

        // …and it is UNSAVED work. Attaching a design read from disk is not an edit; attaching one the
        // user just made is, or the wires would be on screen and absent from the file after a save.
        Assert.True(layout.IsDirty);
    }

    /// <summary>
    /// A second drop onto a layout that already carries wires adds <b>another array</b>, not another
    /// wire layer. One <c>.wBond</c> per <c>.clay</c> is the model; "another group of wires" is the
    /// honest reading of the gesture, and it never refuses a drop the cursor had already accepted.
    /// </summary>
    [Fact]
    public void ASecondDrop_AddsAnotherArray()
    {
        var layout = NewLayout();

        Assert.True(layout.CommitWBondDrop(0, 0));
        Assert.Single(layout.WireDesign!.Arrays);

        Assert.True(layout.CommitWBondDrop(80_000, 0));
        Assert.Equal(2, layout.WireDesign!.Arrays.Count);
        Assert.Equal(2, layout.WireDesign.WireCount);

        // Distinct arrays, each with its own wire — a second wire in the FIRST array would be a
        // different (and wrong) answer: an array is a port, and its wires are one bond group.
        Assert.All(layout.WireDesign.Arrays, a => Assert.Single(a.Wires));
        Assert.Equal(2, layout.WireDesign.Arrays.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  7b. Deleting the last wire un-makes a wirebond cell (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A layout with no wires left removes the wBond from the schematic</b> (owner: "a wBond symbol
    /// remains in the schematic; there should be no wBond component").
    ///
    /// <para>Not tidiness: a wBond carrying an empty payload still draws its pins and still declares
    /// its terminals, so the netlist would go on modelling a bond group the layout no longer has.</para>
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_RemovesTheWBondWhenEveryWireIsGone()
    {
        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(Design(arrays: 1, perArray: 1), "W1"));

        var result = WBondSchematicReconcile.Run(model, new WBondDesign());

        Assert.NotNull(result.Command);
        result.Command!.Execute();
        Assert.Empty(model.Components);

        // UNDO puts it back — the owner's own condition for accepting any of this. It rides on the
        // schematic's own DeleteCommand, which restores a component at its original list index.
        result.Command.Undo();
        var restored = Assert.Single(model.Components);
        Assert.Equal("W1", restored.InstanceName);
        Assert.Equal(SymbolKind.WBond, restored.Symbol);
    }

    /// <summary>
    /// A layout with NO WIRE LAYER AT ALL still says nothing — that is every ordinary cell this command
    /// runs on, and "there is no .wBond here" is not a statement about the schematic.
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_LeavesTheWBondAloneWhenTheLayoutHasNoWireLayer()
    {
        var model = new SchematicEditModel();
        model.Components.Add(WBondPlacement.BuildCarrying(Design(arrays: 1, perArray: 1), "W1"));

        var result = WBondSchematicReconcile.Run(model, null);

        Assert.Null(result.Command);
        Assert.Empty(result.Messages);
        Assert.Single(model.Components);
    }

    /// <summary>An empty layout and an empty schematic is a silent no-op, not a message about nothing.</summary>
    [Fact]
    public void UpdateSchematicFromLayout_IsSilentWhenThereIsNothingLeftToRemove()
    {
        var model = new SchematicEditModel();

        var result = WBondSchematicReconcile.Run(model, new WBondDesign());

        Assert.Null(result.Command);
        Assert.Empty(result.Messages);
    }

    /// <summary>
    /// <b>Saving a layout whose wires are all gone DELETES the sidecar</b> (owner) — a file stating
    /// "no wires" is exactly a file that should not exist, because <c>WBondCell</c> resolves a layout's
    /// wires by the file's PRESENCE: an empty one leaves the cell a wirebond cell for ever, with three
    /// toolbar buttons and two panels about wires there are none of.
    ///
    /// <para><b>And undo still works</b>, which is the owner's own condition. Nothing is detached when
    /// the last wire goes, so the wire history survives: undo brings the wires back in memory, marks
    /// the session dirty, and the next save writes the file again. That whole round trip is what this
    /// test walks.</para>
    /// </summary>
    [Fact]
    public void SavingWithNoWiresLeft_RemovesTheSidecar_AndUndoBringsItBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-wbond-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sidecar = Path.Combine(dir, "amp.wBond");

            var vm = NewLayout();
            vm.AttachWireDesign(Design(arrays: 1, perArray: 2), sidecar);
            vm.MarkWiresDirty();
            vm.MarkSaved();

            Assert.True(File.Exists(sidecar), "a layout WITH wires still writes its sidecar");

            // Every wire deleted, then saved.
            vm.WireEditor!.SelectAllWires();
            Assert.Equal(2, vm.WireEditor.DeleteSelectedWires());
            vm.MarkSaved();

            Assert.False(File.Exists(sidecar), "an emptied layout must not leave a wires-less .wBond behind");

            // …and the wires come back, file and all, because nothing was detached.
            vm.WireEditor.Undo();
            Assert.Equal(2, vm.WireDesign!.WireCount);

            vm.MarkSaved();
            Assert.True(File.Exists(sidecar));
            Assert.Equal(2, WBondIo.ReadFile(sidecar).WireCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  8. The wire tool in the layout host (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A layout session carrying wires — a wirebond cell, as the Layout Editor sees one.</summary>
    private static LayoutEditorViewModel WirebondCell()
    {
        var vm = NewLayout();
        vm.AttachWireDesign(Design(arrays: 1, perArray: 2), "");
        return vm;
    }

    /// <summary>
    /// <b>W arms the wire tool</b> — the same key the wBond editor has always used, so the gesture is
    /// one thing wherever the wires are drawn.
    /// </summary>
    [Fact]
    public void W_ArmsTheWireTool_OnAWirebondCell()
    {
        var vm = WirebondCell();

        Assert.False(vm.WireDrawArmed);

        vm.OnKeyDown(Key.W, KeyModifiers.None);

        Assert.True(vm.WireDrawArmed);
        Assert.True(vm.WireOverlay!.WireDrawArmed);   // …and the overlay is the thing that acts on it
    }

    /// <summary>
    /// …and does nothing on an ordinary layout, which is what keeps the key free everywhere else. A
    /// layout with no wires has nowhere to put one.
    /// </summary>
    [Fact]
    public void W_DoesNothing_OnALayoutWithNoWires()
    {
        var vm = NewLayout();

        vm.OnKeyDown(Key.W, KeyModifiers.None);

        Assert.False(vm.WireDrawArmed);
    }

    /// <summary>
    /// <b>The wire tool and the layout tools are mutually exclusive, in both directions.</b>
    ///
    /// <para>Not tidiness: the overlay gives an armed LAYOUT tool first refusal on every press
    /// (<c>LayoutToolArmed</c>), so a wire tool armed alongside Rectangle would look armed and never
    /// see a click — the exact failure mode that is hardest to report.</para>
    /// </summary>
    [Fact]
    public void TheWireTool_AndTheLayoutTools_CannotBothBeArmed()
    {
        var vm = WirebondCell();

        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.WireDrawArmed = true;

        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);   // arming the wire tool stands the layout tool down
        Assert.True(vm.WireDrawArmed);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Circle;

        Assert.False(vm.WireDrawArmed);             // …and the other way round
        Assert.False(vm.WireOverlay!.WireDrawArmed);
    }

    /// <summary>
    /// <b>Escape disarms; a second Escape deselects everything</b> (owner, 2026-08-17). "Everything" is
    /// three kinds on a wirebond cell — shapes, instances and WIRES — and the wires are the half the
    /// layout's own selection code cannot reach.
    /// </summary>
    [Fact]
    public void Escape_DisarmsFirst_ThenDeselectsEverything()
    {
        var vm = WirebondCell();
        vm.Model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });

        vm.SelectAllCommand.Execute(null);
        vm.WireEditor!.SelectAllWires();
        vm.WireDrawArmed = true;

        Assert.NotEmpty(vm.SelectedIndices);
        Assert.False(vm.WireEditor.Selection.IsEmpty);

        // FIRST Escape: the tool goes, the selections stay. A press that cancels a mode and throws
        // away the selection in one go is two undoable-looking things at once.
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.WireDrawArmed);
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
        Assert.NotEmpty(vm.SelectedIndices);
        Assert.False(vm.WireEditor.Selection.IsEmpty);

        // SECOND Escape: everything is deselected, wires included.
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Empty(vm.SelectedIndices);
        Assert.True(vm.WireEditor.Selection.IsEmpty);
    }

    /// <summary>
    /// Escape with nothing armed goes straight to deselecting — the two-press contract is about what
    /// is IN PROGRESS, not a counter, so it cannot get out of step with what is on screen.
    /// </summary>
    [Fact]
    public void Escape_WithNothingArmed_DeselectsImmediately()
    {
        var vm = WirebondCell();
        vm.WireEditor!.SelectAllWires();

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.True(vm.WireEditor.Selection.IsEmpty);
    }

    /// <summary>
    /// The button is on the toolbar to the RIGHT of the Array Inductance button, and comes and goes
    /// with the other two — all three exist only on a wirebond cell.
    /// </summary>
    [Fact]
    public void TheWireToolButton_SitsWithTheOtherTwo_AndIsGatedWithThem()
    {
        var xaml = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        int inductance = xaml.IndexOf("x:Name=\"WireInductanceBtn\"", StringComparison.Ordinal);
        int draw = xaml.IndexOf("x:Name=\"WireDrawBtn\"", StringComparison.Ordinal);
        Assert.True(inductance >= 0 && draw > inductance, "Draw Wire must follow the Array Inductance button");

        // Bound, not pushed: this one is view-model state, so the key and Escape can reach it.
        Assert.Contains("IsChecked=\"{Binding ActiveViewModel.WireDrawArmed}\"", xaml, StringComparison.Ordinal);

        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        int at = code.IndexOf("private void UpdateWirePanelButtonStates", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("WireDrawBtn.IsVisible = show;", code[at..(at + 800)], StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  8b. Rotating wires from the layout host (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R rotates the selected WIRES 90°, exactly as it rotates a rectangle</b> (owner: "want this
    /// to work just like the layout primitives work").
    /// </summary>
    [Fact]
    public void R_RotatesTheSelectedWires_LikeAnyOtherPrimitive()
    {
        var vm = WirebondCell();
        var wire = vm.WireDesign!.AllWires().First();

        vm.WireEditor!.Selection = Wires(0);

        var before = wire.Points.Select(p => (p.X, p.Y, p.Z)).ToList();
        vm.OnKeyDown(Key.R, KeyModifiers.None);
        var after = wire.Points.Select(p => (p.X, p.Y, p.Z)).ToList();

        Assert.NotEqual(before, after);

        // A 90° turn: every point's displacement from the pivot has swapped axes. Asserted as the
        // property rather than as coordinates — the pivot is the selection's own centre, which is the
        // layout's rule and not this test's business to re-derive.
        var (bw, bh) = Extent(before);
        var (aw, ah) = Extent(after);
        Assert.Equal(bw, ah);
        Assert.Equal(bh, aw);

        // z is untouched: an in-plane rotation says nothing about how high a wire loops.
        Assert.Equal(before.Select(p => p.Z), after.Select(p => p.Z));

        static (long W, long H) Extent(System.Collections.Generic.List<(long X, long Y, long Z)> pts) =>
            (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));
    }

    /// <summary>
    /// <b>A mixed selection turns as ONE rigid body, in ONE undo entry.</b>
    ///
    /// <para>Both halves matter. The shared pivot is what keeps a wire on the pad it lands on — §6.3's
    /// "select the pads and the wires landing on them" is one gesture — and the single entry is what
    /// stops one Ctrl+Z putting the pad back while the wire stays turned, a state the user never made
    /// and could not explain.</para>
    /// </summary>
    [Fact]
    public void RotatingPadsAndWiresTogether_IsOneRigidBody_AndOneUndo()
    {
        var vm = WirebondCell();
        vm.Model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 20_000 });

        vm.SelectAllCommand.Execute(null);
        vm.WireEditor!.SelectAllWires();

        var wire = vm.WireDesign!.AllWires().First();
        var wireBefore = wire.Points.Select(p => (p.X, p.Y)).ToList();
        var shapeBefore = (vm.Model.Shapes[0] as RectShape)!;
        long shapeX1Before = shapeBefore.X1;

        vm.OnKeyDown(Key.R, KeyModifiers.None);

        var wireAfter = wire.Points.Select(p => (p.X, p.Y)).ToList();
        Assert.NotEqual(wireBefore, wireAfter);
        Assert.NotEqual(shapeX1Before, (vm.Model.Shapes[0] as RectShape)!.X1);

        // ONE undo puts BOTH back.
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();

        Assert.Equal(wireBefore, wire.Points.Select(p => (p.X, p.Y)).ToList());
        Assert.Equal(shapeX1Before, (vm.Model.Shapes[0] as RectShape)!.X1);
    }

    /// <summary>
    /// The toolbar's Rotate button is ENABLED with only wires selected — greying it out there would be
    /// refusing the commonest thing on a wirebond cell.
    /// </summary>
    [Fact]
    public void RotateIsAvailable_WithOnlyWiresSelected()
    {
        var vm = WirebondCell();

        Assert.False(vm.RotateAvailability.CanExecute);

        vm.WireEditor!.SelectAllWires();
        Assert.True(vm.RotateAvailability.CanExecute);
    }

    /// <summary>
    /// <b>Alt+R arms the ANGLE-WIRE tool</b> — the swing-about-the-far-end gesture that was fully
    /// implemented on the overlay (WB26a) and had no way in (owner: "there is probably already code
    /// for this … but we just have no UI entry point").
    /// </summary>
    [Fact]
    public void AltR_ArmsTheAngleWireTool_AndPlainRDoesNot()
    {
        var vm = WirebondCell();
        vm.WireEditor!.SelectAllWires();

        vm.OnKeyDown(Key.R, KeyModifiers.Alt);
        Assert.True(vm.WireRotateArmed);
        Assert.True(vm.WireOverlay!.WireRotateArmed);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);
        Assert.False(vm.WireRotateArmed);

        // Plain R is the 90° rotate and must not arm anything.
        vm.OnKeyDown(Key.R, KeyModifiers.None);
        Assert.False(vm.WireRotateArmed);
    }

    /// <summary>
    /// The two wire tools are mutually exclusive with each other as well as with the layout's — one
    /// armed thing at a time, or the canvas has two owners for the next click.
    /// </summary>
    [Fact]
    public void TheTwoWireTools_AreMutuallyExclusive()
    {
        var vm = WirebondCell();

        vm.WireDrawArmed = true;
        vm.WireRotateArmed = true;
        Assert.False(vm.WireDrawArmed);

        vm.WireDrawArmed = true;
        Assert.False(vm.WireRotateArmed);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        Assert.False(vm.WireDrawArmed);
        Assert.False(vm.WireRotateArmed);
    }

    /// <summary>
    /// Both new buttons are on the toolbar with the rest of the wire group, and the Transform one
    /// opens the wBond editor's OWN dialog rather than a second implementation of it.
    /// </summary>
    [Fact]
    public void TheAngleWireAndTransformButtons_AreGatedWithTheWireGroup()
    {
        var xaml = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        int draw = xaml.IndexOf("x:Name=\"WireDrawBtn\"", StringComparison.Ordinal);
        int rotate = xaml.IndexOf("x:Name=\"WireRotateBtn\"", StringComparison.Ordinal);
        int transform = xaml.IndexOf("x:Name=\"WireTransformBtn\"", StringComparison.Ordinal);
        Assert.True(draw >= 0 && rotate > draw && transform > rotate);

        Assert.Contains("IsChecked=\"{Binding ActiveViewModel.WireRotateArmed}\"", xaml, StringComparison.Ordinal);

        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");
        int at = code.IndexOf("private void UpdateWirePanelButtonStates", StringComparison.Ordinal);
        Assert.Contains("WireRotateBtn.IsVisible = show;", code[at..(at + 900)], StringComparison.Ordinal);
        Assert.Contains("WireTransformBtn.IsVisible = show;", code[at..(at + 900)], StringComparison.Ordinal);

        // The dialog is the wBond editor's, called the same way — never a second set of arithmetic.
        Assert.Contains("WBondTransformDialog.ShowAsync(", code, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  8c. Delete removes what was picked; Add Vertex and Straighten Wire (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A SEGMENT selection deletes the segment, not the wire it belongs to</b> (owner: "whole wires
    /// are deleted when the user selects segments and uses the Delete keystroke").
    ///
    /// <para>The key called <c>DeleteSelectedWires</c> unconditionally, so the finest thing it could
    /// remove was a whole wire however carefully a segment had been picked — while the context menu,
    /// one right-click away, removed exactly that segment. The selection already distinguished the
    /// cases; nothing read it.</para>
    /// </summary>
    [Fact]
    public void Delete_OnASegmentSelection_RemovesTheSegment_NotTheWire()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 2));
        var wire = vm.Design.AllWires().First();
        int pointsBefore = wire.Points.Count;

        vm.Selection = new WireSelection { Segments = { new SegmentRef(0, 2) } };

        Assert.Equal(1, vm.DeleteSelection());

        Assert.Equal(2, vm.Design.WireCount);                       // both wires still there
        Assert.Equal(pointsBefore - 1, wire.Points.Count);           // …one point lighter
    }

    /// <summary>A POINT selection removes that vertex, on the same rule.</summary>
    [Fact]
    public void Delete_OnAPointSelection_RemovesTheVertex()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        var wire = vm.Design.AllWires().Single();
        int pointsBefore = wire.Points.Count;

        vm.Selection = new WireSelection { Points = { new PointRef(0, 3) } };

        Assert.Equal(1, vm.DeleteSelection());
        Assert.Equal(1, vm.Design.WireCount);
        Assert.Equal(pointsBefore - 1, wire.Points.Count);
    }

    /// <summary>…and a WHOLE-wire selection still deletes whole wires, which is what it always did.</summary>
    [Fact]
    public void Delete_OnAWholeWireSelection_StillDeletesTheWire()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 3));

        vm.Selection = Wires(1);

        Assert.Equal(1, vm.DeleteSelection());
        Assert.Equal(2, vm.Design.WireCount);
    }

    /// <summary>
    /// A mixed selection does both, in ONE undo entry — one press of Delete is one thing to undo.
    /// </summary>
    [Fact]
    public void Delete_OnAMixedSelection_DoesBothInOneUndo()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 3));
        var second = vm.Design.AllWires().ElementAt(1);
        int pointsBefore = second.Points.Count;

        vm.Selection = new WireSelection { Wires = { 0 }, Segments = { new SegmentRef(1, 2) } };

        Assert.Equal(2, vm.DeleteSelection());
        Assert.Equal(2, vm.Design.WireCount);

        vm.Undo();

        Assert.Equal(3, vm.Design.WireCount);
        Assert.Equal(pointsBefore, vm.Design.AllWires().ElementAt(1).Points.Count);
    }

    /// <summary>
    /// A wire cannot be taken below its two feet, and the refusal is SAID rather than the delete
    /// silently doing nothing.
    /// </summary>
    [Fact]
    public void Delete_RefusesToLeaveAWireWithFewerThanTwoPoints()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        var wire = vm.Design.AllWires().Single();

        // Every interior point AND both feet.
        var selection = new WireSelection();
        for (int i = 0; i < wire.Points.Count; i++) selection.Points.Add(new PointRef(0, i));
        vm.Selection = selection;

        string? refusal = null;
        vm.EditRefused += r => refusal = r;

        Assert.Equal(0, vm.DeleteSelection());
        Assert.Equal(7, wire.Points.Count);
        Assert.NotNull(refusal);
        Assert.Contains("two points", refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Add Vertex inserts a point that changes the wire's shape not at all</b> — collinear with its
    /// two neighbours, at their interpolated z (owner, 2026-08-17). It is a handle where there was
    /// none, which is the whole command.
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void AddVertex_InsertsACollinearPointWithInterpolatedZ(double t)
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        var wire = vm.Design.AllWires().Single();

        const int segment = 2;
        var a = wire.Points[segment];
        var b = wire.Points[segment + 1];
        int before = wire.Points.Count;

        Assert.True(vm.AddWirePoint(0, segment, t));

        Assert.Equal(before + 1, wire.Points.Count);

        var inserted = wire.Points[segment + 1];
        Assert.Equal(a.X + (long)Math.Round((b.X - a.X) * t), inserted.X);
        Assert.Equal(a.Y + (long)Math.Round((b.Y - a.Y) * t), inserted.Y);
        Assert.Equal(a.Z + (long)Math.Round((b.Z - a.Z) * t), inserted.Z);

        // The neighbours are untouched, so the segment it sat on is now two collinear segments.
        Assert.Equal(a, wire.Points[segment]);
        Assert.Equal(b, wire.Points[segment + 2]);
    }

    /// <summary>
    /// <b>Straighten Wire removes lateral bow and nothing else</b> — the feet stay put and every
    /// point's z survives (owner: "straighten the wire vertices in the XY plane; start and end points
    /// are anchors").
    /// </summary>
    [Fact]
    public void StraightenWire_FlattensXyAndKeepsEveryZ()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        var wire = vm.Design.AllWires().Single();

        // Bow it sideways: this fixture runs east, so x is along the wire and y is the bow.
        for (int i = 1; i < wire.Points.Count - 1; i++)
        {
            var p = wire.Points[i];
            wire.Points[i] = new Point3(p.X, p.Y + WBondUnits.ToNm(3.0, WBondUnit.Mil), p.Z);
        }

        var feetBefore = (wire.Points[0], wire.Points[^1]);
        var zBefore = wire.Points.Select(p => p.Z).ToList();

        Assert.True(vm.StraightenWire(0));

        Assert.Equal(feetBefore.Item1, wire.Points[0]);       // both feet anchored
        Assert.Equal(feetBefore.Item2, wire.Points[^1]);
        Assert.Equal(zBefore, wire.Points.Select(p => p.Z));  // the loop is untouched

        // …and every point now sits on the straight line between the feet, in XY.
        foreach (var p in wire.Points)
            Assert.Equal(wire.Points[0].Y, p.Y);
    }

    /// <summary>
    /// <b>Several selected wires all straighten, each about its OWN feet</b> (owner, 2026-08-17: "if
    /// the user has multiple wires selected, then all those wires are straightened … each wire must be
    /// straightened individually using its own anchors").
    ///
    /// <para>The fixture fans the wires out from a common pad, so a shared chord would be visibly
    /// wrong: it would swing every wire onto the first one's line. Each wire keeping its own feet is
    /// what makes a fan-out survive the command.</para>
    /// </summary>
    [Fact]
    public void StraightenWires_StraightensEachAboutItsOwnFeet()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        // A fan-out: three wires from one pad, each landing somewhere different.
        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < 3; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, 0, 4), Point3.Mils(40, (w - 1) * 20.0, 4),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
        design.Arrays.Add(array);

        var vm = new WBondViewModel(design);

        // Bow every wire off its own chord.
        foreach (var wire in vm.Design.AllWires())
        {
            for (int i = 1; i < wire.Points.Count - 1; i++)
            {
                var p = wire.Points[i];
                wire.Points[i] = new Point3(p.X, p.Y + WBondUnits.ToNm(5.0, WBondUnit.Mil), p.Z);
            }
        }

        var feet = vm.Design.AllWires().Select(w => (w.Points[0], w.Points[^1])).ToList();

        Assert.Equal(3, vm.StraightenWires([0, 1, 2]));

        int index = 0;
        foreach (var wire in vm.Design.AllWires())
        {
            // Its own feet, unmoved…
            Assert.Equal(feet[index].Item1, wire.Points[0]);
            Assert.Equal(feet[index].Item2, wire.Points[^1]);

            // …and every interior point on the line between THEM, not on some shared chord.
            var a = wire.Points[0];
            var b = wire.Points[^1];
            foreach (var p in wire.Points)
            {
                double cross = (double)(b.X - a.X) * (p.Y - a.Y) - (double)(b.Y - a.Y) * (p.X - a.X);
                Assert.True(Math.Abs(cross) < 1e6, $"wire {index} is not straight about its own feet");
            }

            index++;
        }

        // The three wires still point in three different directions — a shared chord would have
        // collapsed them onto one.
        var directions = vm.Design.AllWires()
                           .Select(w => w.Points[^1].Y - w.Points[0].Y)
                           .Distinct()
                           .Count();
        Assert.Equal(3, directions);
    }

    /// <summary>
    /// The menu item reads the SELECTION only when there is genuinely more than one wire in it —
    /// otherwise a right-click on a different wire would act somewhere the user is not pointing.
    /// </summary>
    [Fact]
    public void TheStraightenItem_TakesTheSelectionOnlyWhenSeveralWiresAreSelected()
    {
        var code = Read("src", "Ui", "WBond", "WBondLayoutOverlay.ContextMenu.cs");

        int at = code.IndexOf("private MenuItem BuildStraightenItem", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = code[at..(at + 1600)];
        Assert.Contains("selected.Count > 1 ? [.. selected]", body, StringComparison.Ordinal);
        Assert.Contains("hit.Found        ? [hit.Wire]", body, StringComparison.Ordinal);

        // Both spellings exist, and the plural is the multi-selection one.
        Assert.Contains("targets.Count > 1 ? \"Straighten Wires\" : \"Straighten Wire\"",
                        body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An already-straight wire is a no-op that leaves NO undo entry — a Ctrl+Z that appears to do
    /// nothing is worse than one that was never offered.
    /// </summary>
    [Fact]
    public void StraightenWire_OnAStraightWire_LeavesNoUndoEntry()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));

        Assert.False(vm.CanUndo);
        Assert.False(vm.StraightenWire(0));
        Assert.False(vm.CanUndo);
    }

    /// <summary>
    /// The two new items are in the layout wire menu in the owner's order, and <b>Straighten Wire is
    /// NOT in the profile menu</b> — that view's horizontal axis IS position along the wire's path, so
    /// there is no XY plane there to straighten in.
    /// </summary>
    [Fact]
    public void TheWireMenus_CarryAddVertex_AndStraightenIsLayoutOnly()
    {
        var layout = Read("src", "Ui", "WBond", "WBondLayoutOverlay.ContextMenu.cs");

        int group = layout.IndexOf("BuildGroupItem(host,", StringComparison.Ordinal);
        int add = layout.IndexOf("BuildAddVertexItem(worldX", StringComparison.Ordinal);
        int deletes = layout.IndexOf("BuildDeleteItems(worldX", StringComparison.Ordinal);
        int straighten = layout.IndexOf("BuildStraightenItem(worldX", StringComparison.Ordinal);

        // Group … | Add Vertex | Deletes … | Straighten Wire — and Straighten last, so it lands above
        // the canvas's own Rotate 90° items.
        Assert.True(group < add && add < deletes && deletes < straighten);

        var profile = Read("src", "Ui", "Views", "WBond", "WBondProfileView.ContextMenu.cs");
        Assert.Contains("AddVertexItem(editor)", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("StraightenWire", profile, StringComparison.Ordinal);

        // Both Delete keystrokes dispatch on what is SELECTED rather than deleting whole wires.
        Assert.Contains("_vm.DeleteSelection()",
                        Read("src", "Ui", "WBond", "WBondLayoutOverlay.cs"), StringComparison.Ordinal);
        Assert.Contains("_bound.Editor.DeleteSelection()",
                        Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs"), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  8d. Alt-drag: the snap pitch, and the stale glyph (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One wire, 30 mil long, running east with its feet LEVEL.
    ///
    /// <para>Level on purpose: "span" is the foot-to-foot CHORD everywhere in this application (it is
    /// what the Properties panel's own Span field shows, and what <c>WireEdits.ScaleSpan</c> scales
    /// along), and on a descending wire the chord is longer than the XY footprint. Level feet make the
    /// two coincide, so an assertion on the x extent is an assertion on the span with no arithmetic in
    /// between — and level feet are the shipped default anyway.</para>
    /// </summary>
    private static WBondDesign LevelWire()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires =
            {
                LoopShape.CreateSeedWire(Point3.Mils(0, 0, 4), Point3.Mils(30, 0, 4),
                                   WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm),
            },
        });
        return design;
    }

    /// <summary>An overlay whose drag frames always commit — the quality ladder is fed measured wall
    /// clock, so a test that asserts GEOMETRY has to put the frame budget out of reach.</summary>
    private static WBondLayoutOverlay DragOverlay(WBondViewModel vm, long pitchNm) =>
        new(vm, frameBudgetMs: 1e9)
        {
            GridPitchNm = pitchNm,
            SnapEnabled = false,   // the GRID is the subject here, not geometry snapping
        };

    /// <summary>
    /// <b>An alt-drag lands the SPAN on the snap pitch</b> (owner: "alt drag for span and loop height
    /// does not respect the snap distance setting").
    ///
    /// <para>The span is the quantity that has to come out round — not the cursor, and not the scale
    /// factor, which is a ratio and means nothing snapped. The drag below asks for a span the grid
    /// cannot express, and the wire must land on the multiple nearest it.</para>
    /// </summary>
    [Fact]
    public void AltDrag_LandsTheSpanOnTheSnapPitch()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        long pitch = 5 * mil;

        var vm = new WBondViewModel(LevelWire());
        var overlay = DragOverlay(vm, pitch);

        var wire = vm.Design.AllWires().Single();
        long spanBefore = wire.Points[^1].X - wire.Points[0].X;      // 30 mil, east
        Assert.Equal(30 * mil, spanBefore);

        // Grab the OUTPUT foot with Alt, and pull to a span of 37 mil — which the 5 mil grid cannot
        // express. 1 DBU = 1 nm at the default resolution.
        long tol = mil;
        Assert.True(overlay.OnPointerPressed(30 * mil, 0, tol, KeyModifiers.Alt, 1));
        Assert.True(overlay.OnPointerMoved(37 * mil, 0, tol, leftButtonDown: true, KeyModifiers.Alt));
        overlay.OnPointerReleased(37 * mil, 0);

        long spanAfter = wire.Points[^1].X - wire.Points[0].X;

        Assert.Equal(35 * mil, spanAfter);                            // the nearest multiple, not 37
        Assert.Equal(0, spanAfter % pitch);
        Assert.Equal(0L, wire.Points[0].X);                           // the far foot is the anchor
    }

    /// <summary>
    /// With NO snap pitch the drag is continuous again — the setting turns the behaviour on, it is not
    /// hard-wired.
    /// </summary>
    [Fact]
    public void AltDrag_WithNoPitch_IsContinuous()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(LevelWire());
        var overlay = DragOverlay(vm, pitchNm: 0);
        var wire = vm.Design.AllWires().Single();

        Assert.True(overlay.OnPointerPressed(30 * mil, 0, mil, KeyModifiers.Alt, 1));
        Assert.True(overlay.OnPointerMoved(37 * mil, 0, mil, leftButtonDown: true, KeyModifiers.Alt));
        overlay.OnPointerReleased(37 * mil, 0);

        Assert.Equal(37 * mil, wire.Points[^1].X - wire.Points[0].X);
    }

    /// <summary>
    /// <b>No snap glyph during an alt-drag</b> (owner: "the wire vertex snap glyph is still rendered
    /// when the user performs an alt drag to change span").
    ///
    /// <para>An alt-drag SCALES — there is no point being placed, so there is nothing to mark. And
    /// because the alt path returns before the snap is ever recomputed, the marker left over from an
    /// EARLIER gesture would otherwise sit frozen on screen for the whole drag, which is why this test
    /// makes an ordinary drag first: that is what puts a marker there to go stale.</para>
    /// </summary>
    [Fact]
    public void AltDrag_PublishesNoSnapGlyph_EvenAfterAnOrdinaryDragLeftOne()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(LevelWire());
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9)
        {
            GridPitchNm = mil,
            SnapEnabled = true,
            SnapToleranceNm = mil,
            ReferenceLayout = PadAt(40 * mil, 0),
        };

        vm.SelectAllWires();

        // An ordinary drag onto the pad — this is what sets a marker. Asserted, or the test below it
        // would pass with nothing to go stale and prove nothing.
        Assert.True(overlay.OnPointerPressed(30 * mil, 0, mil, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(40 * mil, 0, mil, leftButtonDown: true, KeyModifiers.None));
        Assert.NotNull(overlay.SnapMarker);
        overlay.OnPointerReleased(40 * mil, 0);

        // …and now an ALT drag. Whatever the marker holds, nothing may be published.
        Assert.True(overlay.OnPointerPressed(40 * mil, 0, mil, KeyModifiers.Alt, 1));
        Assert.True(overlay.OnPointerMoved(50 * mil, 0, mil, leftButtonDown: true, KeyModifiers.Alt));

        Assert.Null(overlay.SnapMarker);
    }

    // ── The layout's two geometry-snap toggles govern the wires (owner, 2026-08-17) ───────────────

    /// <summary>
    /// A drawn foot with geometry snap ON lands on the PAD; with it OFF it lands on the GRID.
    ///
    /// <para>The pad corner is deliberately off-grid, so the two answers are different numbers and the
    /// test cannot pass by accident. <c>S</c>/<c>F3</c> was governing every shape in the view except
    /// the wires: the overlay's own default is ON and nothing in the layout host ever wrote it.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]    // geometry wins — the pad corner, off-grid though it is
    [InlineData(false)]   // geometry off — the grid rounds the same 300 nm offset away
    public void TheGeometrySnapToggle_GovernsWireSnapping(bool geometrySnap)
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        // Expressed in mil, never as a literal: a mil is 25,400 nm, so a hand-written "40_300" is a
        // different point entirely.
        long expectedXNm = geometrySnap ? 40 * mil + 300 : 40 * mil;

        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        var overlay = new WBondLayoutOverlay(vm)
        {
            WireDrawArmed = true,
            SnapEnabled = true,                       // the master stays on in both cases…
            GeometrySnapEnabled = geometrySnap,       // …this is the one under test
            GridPitchNm = mil,
            SnapToleranceNm = mil,
            ReferenceLayout = PadAt(40 * mil + 300, 0),
        };

        // 40_400 nm: within a mil of the pad corner at 40_300, and 400 nm above the grid line at 40_000.
        Assert.True(overlay.OnPointerPressed(40 * mil + 400, 0, 500, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerPressed(40 * mil + 400, 40_000, 500, KeyModifiers.None, 1));

        var drawn = vm.Design.AllWires().Last();
        Assert.Equal(expectedXNm, drawn.Points[0].X);

        // No feature was used, so nothing may be marked — a glyph on a grid landing would claim a snap
        // that did not happen. (With geometry snap on there IS one; that is the alt-drag test above.)
        if (!geometrySnap) Assert.Null(overlay.SnapMarker);
    }

    /// <summary>
    /// The MASTER switch still means what it meant: off is off, grid included. Splitting geometry out
    /// of it must not quietly turn `SnapEnabled = false` into "grid only".
    /// </summary>
    [Fact]
    public void TheMasterSnapToggle_StillSuppressesTheGridToo()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        var overlay = new WBondLayoutOverlay(vm)
        {
            WireDrawArmed = true,
            SnapEnabled = false,
            GeometrySnapEnabled = true,
            GridPitchNm = mil,
        };

        Assert.True(overlay.OnPointerPressed(40 * mil + 400, 0, 500, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerPressed(40 * mil + 400, 40_000, 500, KeyModifiers.None, 1));

        Assert.Equal(40 * mil + 400, vm.Design.AllWires().Last().Points[0].X);
    }

    /// <summary>
    /// <b>Include Intersections reaches the wires too</b> — the overlay hard-coded it to <c>false</c>,
    /// so the layout editor's toggle governed shapes and silently did nothing for a wire.
    ///
    /// <para>Asserted at the seam rather than by building a crossing-edge fixture: the flag's own
    /// meaning is <c>LayoutSnapFeatures</c>' and is tested there, so what is worth pinning here is that
    /// this overlay stopped deciding it and passes the host's answer through.</para>
    /// </summary>
    [Fact]
    public void TheIncludeIntersectionsToggle_ReachesTheWireSnap()
    {
        var overlay = new WBondLayoutOverlay(new WBondViewModel(Design(arrays: 1, perArray: 1)));

        Assert.False(overlay.IncludeIntersections);       // off by default, as in the layout editor
        overlay.IncludeIntersections = true;
        Assert.True(overlay.IncludeIntersections);

        var code = Read("src", "Ui", "WBond", "WBondLayoutOverlay.cs");
        Assert.Contains("includeIntersections: IncludeIntersections", code, StringComparison.Ordinal);
        Assert.DoesNotContain("includeIntersections: false", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both toggles are PUSHED by the layout host — the half that was actually missing. The overlay's
    /// defaults are permissive, so a property nobody writes is a property that reads as ignored.
    /// </summary>
    [Fact]
    public void TheLayoutHost_PushesBothSnapToggles_AndRepaintsOnAChange()
    {
        var code = Read("src", "Ui", "Layout", "LayoutEditorViewModel.Wires.cs");

        Assert.Contains("overlay.GeometrySnapEnabled = GeometrySnapEnabled;", code, StringComparison.Ordinal);
        Assert.Contains("overlay.IncludeIntersections = IncludeIntersectionsEnabled;", code, StringComparison.Ordinal);

        // …on every change, not only at attach — and with a repaint, because the layout recomputes its
        // own marker on the toggle rather than waiting for the next pointer move (R-snp-7).
        Assert.Contains("nameof(GeometrySnapEnabled) or nameof(IncludeIntersectionsEnabled)",
                        code, StringComparison.Ordinal);
    }

    /// <summary>A layout holding one pad, so a drag has something to snap to.</summary>
    private static LayoutView PadAt(long xNm, long yNm)
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = xNm, Y1 = yNm - 5000, X2 = xNm + 10_000, Y2 = yNm + 5000,
        });
        return view;
    }


    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  8e. The angle-wire tool: the anchor, and stranded gestures (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Two wires pointing in DIFFERENT directions, far apart — so a rotation measured about
    /// the wrong one's foot cannot accidentally come out right.</summary>
    private static WBondDesign TwoOpposedWires()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        var array = new WireArray { Name = "G1" };
        array.Wires.Add(LoopShape.CreateSeedWire(Point3.Mils(0, 0, 4), Point3.Mils(30, 0, 4),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
        array.Wires.Add(LoopShape.CreateSeedWire(Point3.Mils(200, 100, 4), Point3.Mils(170, 100, 4),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
        design.Arrays.Add(array);

        return design;
    }

    /// <summary>
    /// <b>The swing is measured about the GRABBED wire's far foot</b> (owner: the tool "rotates about
    /// the center of the wire when clicking on the start", and "sometimes doesn't rotate").
    ///
    /// <para>Both reports are one defect. The angle used to be measured about
    /// <c>Selection.TouchedWires().First()</c> — and that is a HASH SET, so "first" is an arbitrary
    /// member: with several wires selected the wire under the cursor turned by an angle computed about
    /// some other wire's foot. Measured before the fix, a quarter turn asked for on the grabbed wire
    /// came out as 191,120 instead of 170,130 — about a third of one, which reads as the wire refusing
    /// to follow the hand.</para>
    /// </summary>
    [Fact]
    public void TheAngleWireTool_SwingsAboutTheGrabbedWiresOwnFarFoot()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(TwoOpposedWires());
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9)
        {
            WireRotateArmed = true,
            SnapEnabled = false,
        };

        vm.SelectAllWires();   // …so "the first selected wire" is NOT the one being grabbed
        var grabbed = vm.Design.AllWires().ElementAt(1);

        // Grab wire 1's start foot (200,100) and swing a quarter turn about its far foot (170,100):
        // the cursor goes to (170,130), where that foot should follow.
        Assert.True(overlay.OnPointerPressed(200 * mil, 100 * mil, mil, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(170 * mil, 130 * mil, mil, true, KeyModifiers.None));
        overlay.OnPointerReleased(170 * mil, 130 * mil);

        Assert.Equal(170 * mil, grabbed.Points[^1].X);   // the ANCHOR did not move…
        Assert.Equal(100 * mil, grabbed.Points[^1].Y);

        Assert.Equal(170 * mil, grabbed.Points[0].X);    // …and the grabbed foot went where the hand did
        Assert.Equal(130 * mil, grabbed.Points[0].Y);
    }

    /// <summary>
    /// Grabbing the OTHER end anchors the other foot — the pivot is the end further from the grab, so
    /// the gesture needs no mode switch.
    /// </summary>
    [Fact]
    public void TheAngleWireTool_AnchorsWhichEverEndIsFurtherFromTheGrab()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(LevelWire());   // (0,0) → (30,0)
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9)
        {
            WireRotateArmed = true,
            SnapEnabled = false,
        };

        var wire = vm.Design.AllWires().Single();

        // Grab the OUTPUT foot this time: the INPUT foot at the origin must stay put.
        Assert.True(overlay.OnPointerPressed(30 * mil, 0, mil, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(0, 30 * mil, mil, true, KeyModifiers.None));
        overlay.OnPointerReleased(0, 30 * mil);

        Assert.Equal(0L, wire.Points[0].X);
        Assert.Equal(0L, wire.Points[0].Y);
        Assert.Equal(0L, wire.Points[^1].X);
        Assert.Equal(30 * mil, wire.Points[^1].Y);
    }

    /// <summary>One wire at an awkward bearing, so an ABSOLUTE snap and a RELATIVE one cannot agree.</summary>
    private static WBondDesign WireAtDegrees(double degrees, double lengthMils = 30.0)
    {
        double radians = degrees * Math.PI / 180.0;

        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires =
            {
                LoopShape.CreateSeedWire(
                    Point3.Mils(0, 0, 4),
                    Point3.Mils(lengthMils * Math.Cos(radians), lengthMils * Math.Sin(radians), 4),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm),
            },
        });
        return design;
    }

    /// <summary>The wire's own bearing, in degrees, measured foot to foot.</summary>
    private static double BearingDegrees(Wire wire) =>
        Math.Atan2(wire.Points[^1].Y - wire.Points[0].Y, wire.Points[^1].X - wire.Points[0].X)
        * 180.0 / Math.PI;

    /// <summary>
    /// <b>Shift snaps the wire onto the ABSOLUTE 15° grid</b> (owner, 2026-08-17), not to 15° from
    /// wherever it started.
    ///
    /// <para>The wire below starts at 20°. Relative increments would put it at 35°, 50°, 65° — every
    /// one as crooked as it began, and 0/90/180/270 (what almost every bond array wants) unreachable by
    /// the very gesture meant to reach it. The cursor here asks for roughly −4°, and the wire must land
    /// on exactly 0°.</para>
    /// </summary>
    [Fact]
    public void ShiftDuringAnAngleWireSwing_SnapsToTheAbsoluteGrid()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(WireAtDegrees(20.0));
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9)
        {
            WireRotateArmed = true,
            SnapEnabled = false,
        };

        var wire = vm.Design.AllWires().Single();
        Assert.Equal(20.0, BearingDegrees(wire), 3);

        // Grab the OUTPUT foot (the far end swings, the origin is the anchor) and drag to a bearing of
        // about −4°: nearest absolute multiple of 15° is 0.
        long grabX = wire.Points[^1].X, grabY = wire.Points[^1].Y;
        Assert.True(overlay.OnPointerPressed(grabX, grabY, mil, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(30 * mil, -2 * mil, mil, true, KeyModifiers.Shift));
        overlay.OnPointerReleased(30 * mil, -2 * mil);

        Assert.Equal(0.0, BearingDegrees(wire), 3);
        Assert.Equal(0L, wire.Points[0].X);           // the anchor never moved
        Assert.Equal(0L, wire.Points[0].Y);
    }

    /// <summary>
    /// …and every OTHER multiple is reachable too — the snap is a grid, not a "straighten to zero".
    ///
    /// <para>The 30° case is the one that says why the step is 15° and not 45° (owner, 2026-08-17): a
    /// coarser grid cannot express it at all, so a 30° fan-out leg would need a free-hand drag to
    /// land. 15 divides 45, 90 and 180, so nothing the coarser step could reach was given up.</para>
    /// </summary>
    [Theory]
    [InlineData(88.0, 90.0)]
    [InlineData(43.0, 45.0)]
    [InlineData(178.0, 180.0)]
    [InlineData(32.0, 30.0)]
    [InlineData(63.0, 60.0)]
    public void ShiftDuringAnAngleWireSwing_ReachesEveryFifteenDegreeStep(
        double cursorDegrees, double expectedBearing)
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(WireAtDegrees(20.0));
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9)
        {
            WireRotateArmed = true,
            SnapEnabled = false,
        };

        var wire = vm.Design.AllWires().Single();
        long grabX = wire.Points[^1].X, grabY = wire.Points[^1].Y;

        double radians = cursorDegrees * Math.PI / 180.0;
        long toX = (long)Math.Round(40 * mil * Math.Cos(radians));
        long toY = (long)Math.Round(40 * mil * Math.Sin(radians));

        Assert.True(overlay.OnPointerPressed(grabX, grabY, mil, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(toX, toY, mil, true, KeyModifiers.Shift));
        overlay.OnPointerReleased(toX, toY);

        Assert.Equal(expectedBearing, BearingDegrees(wire), 3);
    }

    /// <summary>
    /// WITHOUT Shift the swing is free — the wire follows the hand exactly, which is what makes the
    /// modifier a choice rather than the only behaviour.
    /// </summary>
    [Fact]
    public void AnAngleWireSwingWithoutShift_FollowsTheHandExactly()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(WireAtDegrees(20.0));
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e9)
        {
            WireRotateArmed = true,
            SnapEnabled = false,
        };

        var wire = vm.Design.AllWires().Single();
        long grabX = wire.Points[^1].X, grabY = wire.Points[^1].Y;

        // The cursor asks for 37°, which is on no 15° multiple.
        double radians = 37.0 * Math.PI / 180.0;
        long toX = (long)Math.Round(40 * mil * Math.Cos(radians));
        long toY = (long)Math.Round(40 * mil * Math.Sin(radians));

        Assert.True(overlay.OnPointerPressed(grabX, grabY, mil, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(toX, toY, mil, true, KeyModifiers.None));
        overlay.OnPointerReleased(toX, toY);

        Assert.Equal(37.0, BearingDegrees(wire), 3);
    }

    /// <summary>
    /// <b>A stranded drag is unwound before the context menu is built</b> (owner: "sometimes the
    /// Straighten Wire menu is disabled when I right-click on a wire … if I close the context menu and
    /// open it immediately again, then it is properly enabled").
    ///
    /// <para>The mechanism is the quality ladder's chord COLLAPSE: while a drag runs, a wire whose
    /// moving points are all feet is reduced to those two feet and restored at <c>EndDrag</c>. A drag
    /// whose release went elsewhere — the pointer left the window — leaves it collapsed, so the wire
    /// has two points and "Straighten Wire" correctly reports that there is nothing between them. The
    /// mouse movement needed to reopen the menu was what unwound it, which is why the second attempt
    /// worked.</para>
    ///
    /// <para><b>Rewritten 2026-08-18.</b> The bug was that a stranded drag left the wire collapsed to
    /// its two feet, so the menu described a two-point wire. The collapse no longer exists at all
    /// (see <c>QualityLadder</c>), so these two now assert the invariant that replaced it: a drag
    /// never reshapes the wires it moves, stranded or not.</para>
    /// </summary>
    [Fact]
    public void AStrandedDrag_IsUnwoundBeforeTheContextMenuIsBuilt()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(LevelWire());
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e-9)   // every frame overruns → the ladder drops to the collapse rung
        {
            SnapEnabled = false,
        };

        var wire = vm.Design.AllWires().Single();
        Assert.Equal(7, wire.Points.Count);

        // A drag on the start FOOT — the case the collapse applies to — and NO release: the gesture is
        // stranded exactly as it is when the pointer leaves the window.
        overlay.OnPointerPressed(0, 0, mil, KeyModifiers.None, 1);
        overlay.OnPointerMoved(5 * mil, 5 * mil, mil, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerMoved(9 * mil, 9 * mil, mil, leftButtonDown: true, KeyModifiers.None);

        // The wire is NOT collapsed, and since 2026-08-18 it never can be: the ladder's Chord rung —
        // the only thing that ever replaced a polyline with its chord mid-drag — was removed, because
        // the readout it produced was ~70 % low and it rebuilt the mesh every frame. So the hazard
        // this test was written for is now structurally unreachable rather than merely handled, and
        // that is the stronger thing to assert.
        Assert.Equal(7, wire.Points.Count);

        // Whatever the ladder did, a right-click must describe the wire the user is looking at.
        var items = overlay.BuildContextMenuItems(5 * mil, 5 * mil, mil, null, new Avalonia.Controls.Canvas());
        Assert.NotEmpty(items);

        Assert.Equal(7, wire.Points.Count);
        Assert.Null(vm.WhyCannotStraighten(0));
    }

    /// <summary>
    /// …and losing FOCUS unwinds it too, which is the other way a release goes missing: a toolbar
    /// button or another window takes the pointer mid-drag.
    /// </summary>
    [Fact]
    public void AStrandedDrag_IsUnwoundWhenTheCanvasLosesFocus()
    {
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var vm = new WBondViewModel(LevelWire());
        var overlay = new WBondLayoutOverlay(vm, frameBudgetMs: 1e-9) { SnapEnabled = false };
        var wire = vm.Design.AllWires().Single();

        overlay.OnPointerPressed(0, 0, mil, KeyModifiers.None, 1);
        overlay.OnPointerMoved(5 * mil, 5 * mil, mil, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerMoved(9 * mil, 9 * mil, mil, leftButtonDown: true, KeyModifiers.None);

        Assert.Equal(7, wire.Points.Count);   // …stranded, and no longer collapsible — as above

        overlay.OnFocusLost();

        Assert.Equal(7, wire.Points.Count);
        Assert.Null(vm.WhyCannotStraighten(0));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  9. Wire z-height — one setting for every new wire (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The shipped Wire z-height is 4 mil and both feet take it.</b> Owner: <i>"being consistent is
    /// more important than being right, and we can't guess what height the user wants the wire
    /// landings"</i> — so it is one number, settable, rather than two guesses.
    /// </summary>
    [Fact]
    public void TheShippedWireZHeight_IsFourMils_OnBothFeet()
    {
        Assert.Equal(4.0, WBondEmbedding.DefaultWire.FootZMils, 6);
        Assert.Equal(WBondUnits.ToNm(4.0, WBondUnit.Mil), WBondDefaults.ShippedFootZNm);

        var wire = WBondEmbedding.DefaultDesign().AllWires().Single();
        Assert.Equal(WBondDefaults.ShippedFootZNm, wire.Points[0].Z);
        Assert.Equal(WBondDefaults.ShippedFootZNm, wire.Points[^1].Z);
    }

    /// <summary>
    /// A design built at an explicit z puts BOTH feet there — including zero and a NEGATIVE value,
    /// which are a wire on the reference plane and a foot in a cavity below it. Both are geometry
    /// someone bonds, which is why this setting alone treats "absent" and "zero" as different things.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(2.5)]
    [InlineData(-3.0)]
    public void ANewDesign_PutsBothFeetAtTheGivenZ(double mils)
    {
        long z = WBondUnits.ToNm(mils, WBondUnit.Mil);

        var wire = WBondEmbedding.DefaultDesign(z).AllWires().Single();

        Assert.Equal(z, wire.Points[0].Z);
        Assert.Equal(z, wire.Points[^1].Z);

        // …and the loop is still a loop, measured from the feet rather than from the origin.
        Assert.True(wire.Points.Max(p => p.Z) > z);
    }

    /// <summary>
    /// <b>A wire DRAWN in the layout view lands on the setting</b> — the report this started from
    /// (drawn feet were at z = 0 while a new component's were at 4 mil). The layout view has no z axis
    /// for the user to have meant anything by, so the setting is the only answer available.
    /// </summary>
    [Fact]
    public void ADrawnWire_LandsItsFeetOnTheConfiguredZ()
    {
        var vm = new WBondViewModel(Design(arrays: 1, perArray: 1));
        long z = WBondUnits.ToNm(7.0, WBondUnit.Mil);

        var overlay = new WBondLayoutOverlay(vm) { FootZNm = z, WireDrawArmed = true, SnapEnabled = false };

        // Two clicks: the start foot, then the end foot (1 DBU = 1 nm at the default resolution).
        Assert.True(overlay.OnPointerPressed(0, 0, 500, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerPressed(0, 40_000, 500, KeyModifiers.None, 1));

        var drawn = vm.Design.AllWires().Last();
        Assert.Equal(z, drawn.Points[0].Z);
        Assert.Equal(z, drawn.Points[^1].Z);
    }

    /// <summary>
    /// Every creation path reads the ONE resolver rather than the shipped constant — that is what
    /// makes the setting mean "all new wires" rather than "some of them".
    /// </summary>
    [Fact]
    public void EveryCreationPath_ReadsTheSetting()
    {
        // A new component placed on a schematic, a wBond dropped into a layout, an array added from
        // the parameter dialog, and the two overlays whose layout-view draw tool needs a z.
        foreach (var (file, parts) in new (string, string[])[]
        {
            ("schematic placement", ["src", "Ui", "Schematic", "WBondPlacement.cs"]),
            ("palette drop",        ["src", "Ui", "Layout", "LayoutEditorViewModel.WBondDrop.cs"]),
            ("array editor",        ["src", "Ui", "ViewModels", "ParameterEditorViewModel.WBond.cs"]),
            ("layout host overlay", ["src", "Ui", "Layout", "LayoutEditorViewModel.Wires.cs"]),
            ("wBond editor overlay",["src", "Ui", "WBond", "WBondDocument.cs"]),
        })
        {
            Assert.Contains("WBondDefaults.FootZNm", Read(parts), StringComparison.Ordinal);
        }

        // The PROFILE view deliberately does not: there the user clicks a z, which is the whole point
        // of drawing in that view.
        Assert.DoesNotContain("FootZNm", Read("src", "Ui", "Controls", "WBondProfileCanvas.cs"),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// The preference round-trips under its own key, and <b>zero is a value rather than "unset"</b> —
    /// the one place the wBond defaults differ from each other, and the easiest thing to get wrong by
    /// copying the diameter's `is > 0` guard.
    /// </summary>
    [Fact]
    public void TheWireZHeightPreference_TreatsZeroAsAValue()
    {
        var prefs = new AppPreferences { WBondWireFootZNm = 0 };

        Assert.Equal(0, AppPreferencesIo.Migrate(prefs).WBondWireFootZNm);
        Assert.Contains("wbond_wire_foot_z_nm",
                        Read("src", "Ui", "Theming", "AppPreferences.cs"), StringComparison.Ordinal);

        // The resolver falls back only on a NULL, never on a zero.
        Assert.Contains("AppPreferencesIo.Load().WBondWireFootZNm ?? ShippedFootZNm",
                        Read("src", "Ui", "WBond", "WBondDefaults.cs"), StringComparison.Ordinal);

        // …and the Settings box lets a negative one be typed, or a cavity foot is unreachable.
        Assert.Contains("Name=\"WBondFootZUpDown\" Minimum=\"-100\"",
                        Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Pasting wires into a layout that has none CREATES the wire layer</b> (owner, 2026-08-17: "I
    /// copied wires and pcells from a hosted layout and pasted them into a fresh .clay, but the wires
    /// did not get pasted in").
    ///
    /// <para>The paste path required a wire editor to already be there and silently dropped the wire
    /// half when it was not — which is EVERY ordinary layout, since a cell only gains one once
    /// something has put wires in it. So the geometry arrived and the wires vanished.</para>
    /// </summary>
    [Fact]
    public void PastingWiresIntoALayoutWithNone_CreatesTheWireLayer()
    {
        var source = new WBondViewModel(Design(arrays: 1, perArray: 2));
        source.SelectAllWires();

        string? payload = source.CopySelection();
        Assert.NotNull(payload);

        // A FRESH layout — no .wBond, no wire editor, exactly what the owner pasted into.
        var target = NewLayout();
        Assert.Null(target.WireEditor);

        int notified = 0;
        target.WireLayerAdded += () => notified++;

        var editor = target.EnsureWireLayer("test");

        Assert.NotNull(editor);
        Assert.NotNull(target.WireDesign);
        Assert.NotNull(target.WireOverlay);
        Assert.Equal(1, notified);          // the shell is told, so the two panels can appear
        Assert.True(target.IsDirty);        // …and the sidecar is written on the next save

        // The layer arrives EMPTY — the caller is about to add what it is carrying, and a default
        // wire would be a spare nobody asked for.
        Assert.Equal(0, target.WireDesign!.WireCount);

        // …and the payload lands in it, groups and all.
        Assert.Equal(2, editor!.PasteWires(payload, 0, 0));
        Assert.Equal(2, target.WireDesign.WireCount);
        Assert.Equal("G1", target.WireDesign.Arrays.Single().Name);
    }

    /// <summary>A layout that ALREADY has wires keeps the editor it has — no second layer, no message.</summary>
    [Fact]
    public void EnsureWireLayer_OnALayoutThatAlreadyHasWires_ChangesNothing()
    {
        var target = WirebondCell();
        var before = target.WireEditor;

        int notified = 0;
        target.WireLayerAdded += () => notified++;

        Assert.Same(before, target.EnsureWireLayer("should not be said"));
        Assert.Equal(0, notified);
    }

    /// <summary>
    /// The paste path reaches for it before giving up — and never on a layout whose wires belong to a
    /// HOST, which would attach a second, invisible design to the wBond editor's reference layout.
    /// </summary>
    [Fact]
    public void ThePastePath_CreatesTheLayerButNeverUnderAHost()
    {
        var code = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");

        int at = code.IndexOf("private async Task<bool> PasteWithWiresAsync", StringComparison.Ordinal);
        Assert.True(at >= 0);

        string body = code[at..(at + 2000)];
        Assert.Contains("vm.EnsureWireLayer(", body, StringComparison.Ordinal);
        Assert.Contains("_hostOverlay is not null && vm.WireEditor is null) return false;",
                        body, StringComparison.Ordinal);

        // The guard has to come FIRST, or the layer is created before the refusal is reached.
        Assert.True(body.IndexOf("_hostOverlay is not null", StringComparison.Ordinal)
                    < body.IndexOf("vm.EnsureWireLayer(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The canvas answers a wBond tile BEFORE the generator test, which would (correctly) refuse it —
    /// the drag-over cursor has to say yes, and the drop has to reach the wire path.
    /// </summary>
    [Fact]
    public void TheCanvas_RoutesAWBondTileToTheWirePath()
    {
        var code = Read("src", "Ui", "Controls", "LayoutCanvas.cs");

        int dragOver = code.IndexOf("private void OnPaletteDragOver", StringComparison.Ordinal);
        int drop = code.IndexOf("private void OnPaletteDrop", StringComparison.Ordinal);
        Assert.True(dragOver >= 0 && drop < code.Length);

        string dragBody = code[dragOver..drop];
        Assert.True(dragBody.IndexOf("payload.Kind == SymbolKind.WBond", StringComparison.Ordinal)
                    < dragBody.IndexOf("vm.CanDropPaletteComponent(", StringComparison.Ordinal),
                    "a wBond must be answered before the generator test, which refuses it");

        string dropBody = code[drop..(drop + 900)];
        Assert.Contains("vm.CommitWBondDrop(sx, sy);", dropBody, StringComparison.Ordinal);

        // …and it is refused where the wires on screen belong to a HOST — the wBond editor hosts this
        // canvas and its own overlay outranks the frame's, so a drop there would attach a second,
        // invisible wire design to the reference layout.
        Assert.Contains("ReferenceEquals(_canvasOverlay, vm.WireOverlay)", code, StringComparison.Ordinal);
    }
}
