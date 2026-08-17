using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's 2026-08-16 toolbar round: rulers on both canvases, the Select tool and its Escape,
/// the W/R tool keys and the retirement of the w+click gesture, Save/Save As, the numbered scratch
/// title, the geometry-prompt refusal, and the inductance panel's array double-click and shared
/// mutual list.
///
/// <para>Two kinds of test live here, deliberately. Anything expressible on a view-model or on a
/// framework-free type is asserted directly. The rest — where a button SITS in the toolbar, and what
/// its tooltip says — is only expressible against the XAML source, because <c>Ui.Tests</c> calls no
/// Avalonia runtime API; those are ordered-index scans of the markup, with comments stripped first
/// so a change that only moves a comment cannot pass one.</para>
/// </summary>
public class WBondToolbarAndRulersTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The markup with every <c>&lt;!-- … --&gt;</c> removed.
    ///
    /// <para>Not a nicety: this file's toolbar comments quote the tooltips and the tool names they
    /// describe, so an ordered scan over the raw text would happily match a paragraph of prose and
    /// report a button that is no longer there.</para>
    /// </summary>
    private static string StripXmlComments(string xaml)
    {
        var sb = new System.Text.StringBuilder(xaml.Length);
        int i = 0;
        while (i < xaml.Length)
        {
            int start = xaml.IndexOf("<!--", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(xaml, i, xaml.Length - i); break; }

            sb.Append(xaml, i, start - i);
            int end = xaml.IndexOf("-->", start, StringComparison.Ordinal);
            if (end < 0) break;
            i = end + 3;
        }
        return sb.ToString();
    }

    private static string EditorXaml() => StripXmlComments(Read("src/Ui/Views/WBond/WBondEditorView.axaml"));

    /// <summary>Asserts every needle appears, in this order, in <paramref name="haystack"/>.</summary>
    private static void AssertOrder(string haystack, params string[] needles)
    {
        int at = 0;
        string? previous = null;
        foreach (var needle in needles)
        {
            int found = haystack.IndexOf(needle, at, StringComparison.Ordinal);
            Assert.True(found >= 0,
                previous is null
                    ? $"'{needle}' is missing."
                    : $"'{needle}' does not appear after '{previous}'.");
            at = found + needle.Length;
            previous = needle;
        }
    }

    private static WBondDesign Design(int wires = 3, int arrays = 1)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}", Profile = profile.Name };
            for (int w = 0; w < wires; w++)
                array.Wires.Add(profile.CreateWire(
                    Point3.Mils(a * 300, w * 6, 4), Point3.Mils(a * 300 + 100, w * 6, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));

            design.Arrays.Add(array);
        }

        return design;
    }

    private static WBondLayoutOverlay Overlay(WBondViewModel vm) => new(vm) { SnapEnabled = false };

    // ════════════════════════════════════════════════════════ the marquee, at any profile plane

    /// <summary>
    /// <b>The LAYOUT view's marquee is independent of the profile view's plane.</b>
    ///
    /// <para>Owner report: after setting the plane to 10°, no wire could be marquee-selected in the
    /// layout view. The two views project differently on purpose — the profile view's marquee is
    /// resolved against (span, z) and therefore DOES read the azimuth — so the risk is real and the
    /// coupling would be invisible: a layout marquee resolved against the profile projection would
    /// still select something on the axis planes (0°/90°, where span degenerates to y or x) and
    /// silently select nothing in between. This drives the real overlay at three planes and asserts
    /// the same answer.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]    // Auto
    [InlineData(0.0)]     // XZ
    [InlineData(90.0)]    // YZ — the shipped default
    [InlineData(10.0)]    // the owner's own case
    [InlineData(37.5)]
    public void TheLayoutMarquee_SelectsTheSameWiresAtEveryProfilePlane(double? degrees)
    {
        var vm = new WBondViewModel(Design(wires: 3))
        {
            ProfileAzimuthRadians = degrees is { } d ? d * Math.PI / 180.0 : null,
        };

        var overlay = Overlay(vm);
        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);

        // A crossing box (right → left) over the whole design catches every wire whole.
        overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1);
        overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(-far, -far);

        Assert.Equal(3, vm.Selection.Wires.Count);
        Assert.Equal(3, vm.Selection.TouchedWires().Count);
    }

    /// <summary>
    /// <b>A held-key latch is dropped when focus leaves the canvas</b>, whether or not its key-up
    /// ever arrived.
    ///
    /// <para>This is the one mechanism found that DOES make "marquee select stopped working in the
    /// layout view" permanent, and it is reached by an ordinary gesture: hold Space to pan, then —
    /// still holding it — click a toolbar button or a combo. The release goes to whatever took focus,
    /// <c>LayoutCanvas._spaceHeld</c> stays set, and from then on every left-drag is a PAN and the
    /// marquee never starts. The overlay's own hold-<c>g</c> promotion has the same shape.</para>
    ///
    /// <para>The canvas half is asserted in source (Ui.Tests calls no Avalonia runtime API); the
    /// overlay half is driven directly.</para>
    /// </summary>
    [Fact]
    public void LosingFocus_DropsEveryHeldKeyLatch()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);
        var foot = vm.Design.AllWires().First().Points[0];
        long tol = WBondUnits.ToNm(3.0, WBondUnit.Mil);

        overlay.OnKeyDown(Key.G, KeyModifiers.None);
        overlay.OnFocusLost();                      // …and the key-up never comes

        overlay.OnPointerPressed(foot.X, foot.Y, tol, KeyModifiers.None, clickCount: 1);
        overlay.OnPointerReleased(foot.X, foot.Y);

        // Still latched, this would have promoted the click to the whole three-wire array.
        Assert.Empty(vm.Selection.Wires);
        Assert.NotEmpty(vm.Selection.Points);

        var canvas = Read("src/Ui/Controls/LayoutCanvas.cs");
        Assert.Contains("LostFocus           += OnCanvasLostFocus;", canvas, StringComparison.Ordinal);
        Assert.Contains("_canvasOverlay?.OnFocusLost();", canvas, StringComparison.Ordinal);

        // The space-to-pan latch is the half that produces the reported symptom, so it is named.
        int handler = canvas.IndexOf("private void OnCanvasLostFocus", StringComparison.Ordinal);
        Assert.True(handler >= 0);
        Assert.Contains("_spaceHeld = false;", canvas[handler..], StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ the active tool

    /// <summary>A new editor rests on Select, with neither canvas tool armed.</summary>
    [Fact]
    public void ANewEditor_RestsOnTheSelectTool()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));

        Assert.Equal(WBondTool.Select, document.ActiveTool);
        Assert.False(document.Overlay.WireDrawArmed);
        Assert.False(document.Overlay.WireRotateArmed);
    }

    /// <summary>
    /// The three tools are mutually exclusive <b>by construction</b> — the overlay's two armed flags
    /// are derived from one enum, so arming one cannot leave the other set.
    ///
    /// <para>They were previously two independent <c>ToggleButton</c>s, each responsible for
    /// un-pressing the other, which is exactly the shape that leaves both armed when a third caller
    /// (a key, Escape, a restored view state) sets one of them without going through the button.</para>
    /// </summary>
    [Fact]
    public void ArmingOneTool_DisarmsTheOther()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));

        document.ActiveTool = WBondTool.DrawWire;
        Assert.True(document.Overlay.WireDrawArmed);
        Assert.False(document.Overlay.WireRotateArmed);

        document.ActiveTool = WBondTool.Rotate;
        Assert.False(document.Overlay.WireDrawArmed);
        Assert.True(document.Overlay.WireRotateArmed);

        document.ActiveTool = WBondTool.Select;
        Assert.False(document.Overlay.WireDrawArmed);
        Assert.False(document.Overlay.WireRotateArmed);
    }

    /// <summary>The toolbar's command takes the tool by NAME, exactly as the Layout Editor's does.</summary>
    [Theory]
    [InlineData("Select", WBondTool.Select)]
    [InlineData("DrawWire", WBondTool.DrawWire)]
    [InlineData("Rotate", WBondTool.Rotate)]
    public void TheToolCommand_ResolvesTheNameTheToolbarPasses(string name, WBondTool expected)
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));

        document.ActiveTool = WBondTool.Rotate;   // so "Rotate" is not vacuously already true
        document.ActiveTool = WBondTool.Select;

        document.SetActiveToolCommand.Execute(name);
        Assert.Equal(expected, document.ActiveTool);
    }

    /// <summary>A name the toolbar never passes leaves the tool alone rather than resetting it.</summary>
    [Fact]
    public void AnUnknownToolName_ChangesNothing()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()))
        {
            ActiveTool = WBondTool.DrawWire,
        };

        document.SetActiveToolCommand.Execute("Lasso");
        Assert.Equal(WBondTool.DrawWire, document.ActiveTool);
    }

    /// <summary>
    /// The Select button is to the LEFT of Draw wire (owner), and Rotate is the third of the three.
    /// </summary>
    [Fact]
    public void TheSelectButton_SitsLeftOfDrawWire()
    {
        AssertOrder(EditorXaml(),
            "ConverterParameter=Select",
            "ConverterParameter=DrawWire",
            "ConverterParameter=Rotate");
    }

    /// <summary>Escape names Select in the button's own tooltip, so the unwind is discoverable.</summary>
    [Fact]
    public void TheSelectButton_NamesEscape()
    {
        Assert.Contains("ToolTip.Tip=\"Select  (Esc)\"", EditorXaml(), StringComparison.Ordinal);
    }

    /// <summary>The two tool keys are stated where a user will read them.</summary>
    [Fact]
    public void TheToolTooltips_NameTheirKeys()
    {
        var xaml = EditorXaml();
        Assert.Contains("Draw wire (W)", xaml, StringComparison.Ordinal);
        Assert.Contains("Rotate about end point (R)", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The w+click gesture is gone.</b> W is the Draw Wire tool now, so the overlay must not also
    /// latch a "promote to the whole wire" modifier on the same key — a held W would otherwise turn
    /// every subsequent click into a whole-wire selection with no way to see why.
    /// </summary>
    [Fact]
    public void TheOverlay_NoLongerClaimsTheWKey()
    {
        var source = Read("src/Ui/WBond/WBondLayoutOverlay.cs");

        Assert.DoesNotContain("_wHeld", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Key.W", source, StringComparison.Ordinal);

        // G is untouched — it was never in conflict with anything.
        Assert.Contains("Key.G", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Double-click still promotes to the whole wire, which is what makes retiring w+click a
    /// replacement rather than a removal.
    /// </summary>
    [Fact]
    public void DoubleClicking_StillSelectsTheWholeWire()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);

        // The middle wire's input foot, at (0, 6) mil.
        long x = WBondUnits.ToNm(0.0, WBondUnit.Mil);
        long y = WBondUnits.ToNm(6.0, WBondUnit.Mil);
        long tol = WBondUnits.ToNm(3.0, WBondUnit.Mil);

        overlay.OnPointerPressed(x, y, tol, KeyModifiers.None, clickCount: 2);
        overlay.OnPointerReleased(x, y);

        Assert.Equal([1], vm.Selection.Wires);
        Assert.Empty(vm.Selection.Points);
    }

    // ════════════════════════════════════════════════════════ rulers

    /// <summary>Rulers are on by default, on a new document and on one whose file said nothing.</summary>
    [Fact]
    public void Rulers_AreOnByDefault()
    {
        Assert.True(new WBondDocumentViewModel(new WBondViewModel(Design())).RulersVisible);
        Assert.True(new WBondViewState().RulersVisible);
        Assert.True(WBondViewState.From(new WBondDesign()).RulersVisible);
    }

    /// <summary>The toggle persists with the rest of the arrangement, through the .wBond's view state.</summary>
    [Fact]
    public void TheRulerToggle_TravelsWithTheDocument()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design())) { RulersVisible = false };
        document.CaptureViewState();

        var reopened = new WBondDocumentViewModel(new WBondViewModel(document.Editor.Design));
        Assert.True(reopened.RulersVisible);     // a fresh view-model starts on the default…
        reopened.ApplyViewState();
        Assert.False(reopened.RulersVisible);    // …and the file overrides it
    }

    /// <summary>
    /// Both canvases carry the Layout Editor's OWN ruler control — reused, not reimplemented — and
    /// each is hosted in the same corner-box + strip shape <c>LayoutEditorView</c> uses.
    /// </summary>
    [Fact]
    public void BothCanvases_HostTheLayoutEditorsRulerControl()
    {
        var xaml = EditorXaml();

        foreach (var name in new[] { "ProfileHRuler", "ProfileVRuler", "LayoutHRuler", "LayoutVRuler" })
            Assert.Contains($"ctrl:LayoutRulerControl x:Name=\"{name}\"", xaml, StringComparison.Ordinal);

        // The horizontal strips come before their canvas, the vertical ones down the left.
        AssertOrder(xaml, "ProfileHRuler", "ProfileVRuler", "ProfileCanvas",
                          "LayoutHRuler", "LayoutVRuler", "LayoutCanvasCtrl");
    }

    /// <summary>
    /// <b>ONE toolbar button drives both views' rulers</b> (owner). Two would be two chances to leave
    /// the editor showing a ruler on one picture of the design and not the other.
    /// </summary>
    [Fact]
    public void OneToolbarButton_DrivesBothViewsRulers()
    {
        var xaml = EditorXaml();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, @"ViewModel\.RulersVisible"));
        Assert.Contains("x:Name=\"RulerToggle\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile view's rulers are driven at 1,000 DBU/µm — the resolution at which one database
    /// unit IS one nanometre, which is that canvas's own world unit.
    ///
    /// <para>Driving them at the reference layout's resolution instead would put a ruler on the
    /// profile view whose labels disagreed with the profile view by that factor — and would agree
    /// exactly on the 1,000 DBU/µm default, which is where such a bug would never be noticed.</para>
    /// </summary>
    [Fact]
    public void TheProfileRulers_AreDrivenInNanometres()
    {
        var source = Read("src/Ui/Views/WBond/WBondEditorView.axaml.cs");

        Assert.Contains("NanometreDbuPerMicron = 1000", source, StringComparison.Ordinal);
        Assert.Contains("ProfileHRuler.SetUnits(NanometreDbuPerMicron", source, StringComparison.Ordinal);
        Assert.Contains("ProfileVRuler.SetUnits(NanometreDbuPerMicron", source, StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ toolbar order

    /// <summary>
    /// Zoom to Fit / In / Out / 1:1 are the FIRST four controls in the toolbar, matching the Layout
    /// Editor and the schematic (owner) — the point of moving them is that a hand does not have to
    /// re-learn the toolbar per editor.
    /// </summary>
    [Fact]
    public void TheFourZoomButtons_ComeFirst()
    {
        var xaml = EditorXaml();
        int wrap = xaml.IndexOf("<WrapPanel", StringComparison.Ordinal);
        Assert.True(wrap >= 0, "The toolbar's WrapPanel is gone.");

        AssertOrder(xaml[wrap..],
            "Click=\"OnZoomToFit\"", "Click=\"OnZoomIn\"", "Click=\"OnZoomOut\"", "Click=\"OnZoom1To1\"",
            // …and everything else follows them.
            "Click=\"OnSave\"", "x:Name=\"ViewModeToggle\"", "ConverterParameter=Select");
    }

    /// <summary>
    /// The profile-plane combo is docked RIGHT (owner). A <c>WrapPanel</c> cannot right-align one of
    /// its children, which is why the toolbar gained a <c>DockPanel</c> around it — asserted here
    /// because deleting that wrapper would put the combo silently back in the flow.
    /// </summary>
    [Fact]
    public void TheProfilePlaneCombo_IsDockedRight()
    {
        var xaml = EditorXaml();

        int combo = xaml.IndexOf("x:Name=\"ProfileAxisCombo\"", StringComparison.Ordinal);
        Assert.True(combo >= 0, "The profile-plane combo is gone.");

        int dockRight = xaml.IndexOf("DockPanel.Dock=\"Right\"", combo, StringComparison.Ordinal);
        Assert.True(dockRight >= 0 && dockRight - combo < 200,
                    "The profile-plane combo is no longer docked to the right of the toolbar.");
    }

    /// <summary>Save and Save As are both in the toolbar, in that order.</summary>
    [Fact]
    public void SaveAndSaveAs_AreBothInTheToolbar()
    {
        AssertOrder(EditorXaml(), "Click=\"OnSave\"", "Click=\"OnSaveAs\"");
    }

    /// <summary>
    /// <b>The buttons ASK; the host answers.</b> Both hosts must be listening, or Save would be a
    /// button that does nothing — and a silent Save is worse than no Save.
    /// </summary>
    [Fact]
    public void BothHosts_AnswerTheSaveRequest()
    {
        Assert.Contains("doc.SaveRequested +=", Read("src/Ui/ViewModels/WorkspaceViewModel.cs"),
                        StringComparison.Ordinal);
        Assert.Contains("document.SaveRequested +=", Read("src/Ui/Views/WBond/WBondShellWindow.axaml.cs"),
                        StringComparison.Ordinal);
    }

    /// <summary>The Layout Editor's Select tooltip no longer carries a phase code (owner).</summary>
    [Fact]
    public void TheLayoutEditorsSelectTooltip_HasNoPhaseCode()
    {
        Assert.DoesNotContain("(L1c)", Read("src/Ui/Views/Layout/LayoutEditorView.axaml"),
                              StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ the save-geometry prompt

    /// <summary>
    /// <b>A question with one possible answer is not a question.</b> "Include the layout geometry in
    /// this file?" is asked only when the layout holds something.
    /// </summary>
    [Fact]
    public void TheGeometryPrompt_IsSkippedWhenThereIsNoGeometry()
    {
        Assert.False(WBondGeometryEmbedding.HasGeometryToEmbed(null));
        Assert.False(WBondGeometryEmbedding.HasGeometryToEmbed(new LayoutView()));

        var withShape = new LayoutView();
        withShape.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 100, Layer = new LayerKey(1, 0) });
        Assert.True(WBondGeometryEmbedding.HasGeometryToEmbed(withShape));

        var withInstance = new LayoutView();
        withInstance.Instances.Add(new LayoutInstance { CellRef = "pad" });
        Assert.True(WBondGeometryEmbedding.HasGeometryToEmbed(withInstance));
    }

    /// <summary>Both save paths consult it, so neither can go on asking on an empty document.</summary>
    [Fact]
    public void BothSavePaths_GateThePromptOnActualGeometry()
    {
        Assert.Contains("WBondGeometryEmbedding.HasGeometryToEmbed",
                        Read("src/Ui/ViewModels/WorkspaceViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("WBondGeometryEmbedding.HasGeometryToEmbed",
                        Read("src/Ui/Views/WBond/WBondShellWindow.axaml.cs"), StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ document naming and close

    /// <summary>
    /// A scratch wBond is named the way every other scratch document is — and its Id is that name,
    /// which is what makes "the lowest free N" answerable and what the close dialog reads.
    /// </summary>
    [Fact]
    public void AScratchDocument_IsNamedUntitledWBond1()
    {
        var doc = new WBondDocument();

        Assert.Equal("Untitled-wBond-1", doc.Title);
        Assert.Equal("Untitled-wBond-1", doc.Id);
    }

    /// <summary>A supplied title wins, and the dirty bullet rides on top of it.</summary>
    [Fact]
    public void ASuppliedTitle_IsUsedAndKeepsItsDirtyMarker()
    {
        var doc = new WBondDocument(title: "Untitled-wBond-4");
        Assert.Equal("Untitled-wBond-4", doc.Title);

        doc.ViewModel.Editor.SelectAllWires();          // not an edit — the document is still clean
        Assert.Equal("Untitled-wBond-4", doc.Title);

        doc.ViewModel.IsDirty = true;
        Assert.StartsWith("Untitled-wBond-4", doc.Title, StringComparison.Ordinal);
        Assert.Contains("•", doc.Title, StringComparison.Ordinal);
    }

    /// <summary>Opening a FILE names the document after the file, not after the Untitled ladder.</summary>
    [Fact]
    public void AFileBackedDocument_IsNamedAfterItsFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wbond-title-{Guid.NewGuid():N}.wBond");
        try
        {
            WBondIo.WriteFile(path, Design());
            var doc = WBondDocument.Open(path);

            Assert.Equal(Path.GetFileNameWithoutExtension(path), doc.Title);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// <b>A dirty wBond tab asks before it closes</b> (owner: it did not). The branch is asserted in
    /// the source because the confirm path is an <c>async</c> dialog on the workspace, which
    /// <c>Ui.Tests</c> cannot drive — what is checkable, and what was actually missing, is that a
    /// <c>WBondDocument</c> is considered at all.
    /// </summary>
    [Fact]
    public void ADirtyWBondTab_IsAskedAboutBeforeClosing()
    {
        var source = Read("src/Ui/ViewModels/WorkspaceViewModel.cs");

        Assert.Contains("dockable is WBond.WBondDocument wbCloseDoc && wbCloseDoc.IsDirty",
                        source, StringComparison.Ordinal);

        // …and a cancelled save picker must cancel the close, or "Save" quietly means "Don't Save".
        Assert.Contains("return !wbCloseDoc.IsDirty;", source, StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ the inductance panel

    /// <summary>
    /// Double-clicking a group name selects every wire in that group — resolved through the row's own
    /// array INDEX, so two arrays sharing a name could never select each other's wires.
    /// </summary>
    [Fact]
    public void SelectingAnArray_TakesEveryWireInItAndNothingElse()
    {
        var vm = new WBondViewModel(Design(wires: 3, arrays: 2));

        Assert.Equal(3, vm.SelectArray(1));
        Assert.Equal([3, 4, 5], vm.Selection.Wires.OrderBy(w => w));
        Assert.Empty(vm.Selection.Points);
        Assert.Empty(vm.Selection.Segments);

        Assert.Equal(3, vm.SelectArray(0));
        Assert.Equal([0, 1, 2], vm.Selection.Wires.OrderBy(w => w));
    }

    /// <summary>An index naming no array clears the selection rather than throwing.</summary>
    [Fact]
    public void SelectingAnArrayThatIsNotThere_SelectsNothing()
    {
        var vm = new WBondViewModel(Design(wires: 2, arrays: 1));

        Assert.Equal(0, vm.SelectArray(7));
        Assert.True(vm.Selection.IsEmpty);
    }

    /// <summary>Each panel row carries the array index the double-click resolves through.</summary>
    [Fact]
    public void EachPanelRow_CarriesItsOwnArrayIndex()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 2, arrays: 3)));

        Assert.Equal([0, 1, 2], document.Panel.Rows.Select(r => r.ArrayIndex));
        Assert.Equal(["G1", "G2", "G3"], document.Panel.Rows.Select(r => r.Name));
    }

    /// <summary>
    /// <b>Three arrays make three pairs, not six.</b> M is symmetric, so G1-G3 and G3-G1 are the same
    /// number — listing both was the duplication the owner asked to remove, and it is the failure a
    /// naive "every row for every other row" rebuild would reintroduce.
    /// </summary>
    [Fact]
    public void TheMutualList_NamesEachPairExactlyOnce()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 2, arrays: 3)));

        Assert.True(document.Panel.HasMutualPairs);
        Assert.Equal(["G1-G2", "G1-G3", "G2-G3"], document.Panel.MutualPairs.Select(p => p.Name));
    }

    /// <summary>
    /// The pair's value is the real off-diagonal, in the same pH format a self inductance uses — the
    /// unit is not auto-ranged here either (WB27a).
    /// </summary>
    [Fact]
    public void AMutualPair_ReportsTheOffDiagonalInPicoHenries()
    {
        var vm = new WBondViewModel(Design(wires: 2, arrays: 2));
        var document = new WBondDocumentViewModel(vm);

        var pair = Assert.Single(document.Panel.MutualPairs);
        Assert.EndsWith(" pH", pair.Mutual, StringComparison.Ordinal);
        Assert.EndsWith(" %", pair.Coupling, StringComparison.Ordinal);

        Assert.Equal(WBondPanelViewModel.FormatPicoHenries(vm.Readout.Rows[0].MutualPicoHenries[1]),
                     pair.Mutual);
    }

    /// <summary>
    /// The list is updated IN PLACE, like the cards above it — a live readout that replaced its
    /// collection every frame would flicker exactly where a user is watching a number move.
    /// </summary>
    [Fact]
    public void TheMutualList_IsUpdatedInPlaceRatherThanRebuilt()
    {
        var vm = new WBondViewModel(Design(wires: 2, arrays: 2));
        var document = new WBondDocumentViewModel(vm);

        var before = document.Panel.MutualPairs[0];
        vm.SelectAllWires();
        vm.NudgeSelection(1, 0, coarse: false, EditorView.Layout);

        Assert.Same(before, document.Panel.MutualPairs[0]);
    }

    /// <summary>
    /// The mutuals no longer live on the array cards at all — the per-card list is gone from the
    /// view-model AND from the markup, so a stale binding cannot leave half of them rendering twice.
    /// </summary>
    [Fact]
    public void TheCards_NoLongerCarryTheirOwnMutuals()
    {
        var source = Read("src/Ui/WBond/WBondPanelViewModel.cs");
        Assert.DoesNotContain("HasMutuals ", source, StringComparison.Ordinal);

        var xaml = EditorXaml();
        Assert.DoesNotContain("{Binding Mutuals}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding MutualPairs}", xaml, StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════ the expanded card's geometry rows

    /// <summary>
    /// The expansion's rows, in the owner's own order: Wires, Loop height, Span, Diameter, Material,
    /// Total length. "Landing span" is gone — replaced by the wires' own Span, which is the quantity
    /// a user can set.
    /// </summary>
    [Fact]
    public void TheExpandedCard_ListsItsRowsInOrder()
    {
        var xaml = EditorXaml();

        AssertOrder(xaml,
            "Text=\"Wires\"", "Text=\"Loop height\"", "Text=\"Span\"",
            "Text=\"Diameter\"", "Text=\"Material\"", "Text=\"Total length\"");

        Assert.DoesNotContain("Landing span", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A uniform array reports each quantity plainly — the marker appears only when there is something
    /// to mark, or it would be noise on every card in the common case.
    /// </summary>
    [Fact]
    public void AUniformArray_ReportsItsGeometryUnmarked()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 4)));
        var row = Assert.Single(document.Panel.Rows);

        foreach (var value in new[] { row.LoopHeight, row.Span, row.Diameter, row.Material })
            Assert.DoesNotContain("*", value, StringComparison.Ordinal);

        // Design() builds 1 mil gold wires spanning 100 mil, on a 20 mil ball-bond profile.
        Assert.StartsWith("100.0", row.Span, StringComparison.Ordinal);
        Assert.StartsWith("1.0", row.Diameter, StringComparison.Ordinal);
        Assert.Equal("Gold", row.Material);
    }

    /// <summary>
    /// <b>A non-uniform array reports the MEDIAN, marked.</b> The fixture is chosen so the three
    /// plausible implementations disagree: loop heights of 10 / 22 / 22 / 60 mil give a median of 22,
    /// a mean of 28.5, and a first-wire value of 10.
    /// </summary>
    [Fact]
    public void ANonUniformArray_ReportsTheMedianAndMarksIt()
    {
        var design = Design(wires: 4);
        var vm = new WBondViewModel(design);

        // None of these is the fixture's own 20 mil, so every set is a real change rather than a
        // no-op the setter correctly refuses.
        foreach (var (i, mils) in new[] { (0, 10.0), (1, 22.0), (2, 22.0), (3, 60.0) })
            Assert.True(vm.SetWireLoopHeight(i, WBondUnits.ToNm(mils, WBondUnit.Mil)));

        var row = Assert.Single(new WBondDocumentViewModel(vm).Panel.Rows);

        Assert.StartsWith("22.0", row.LoopHeight, StringComparison.Ordinal);
        Assert.EndsWith(WBondPanelViewModel.NonUniformMarker, row.LoopHeight, StringComparison.Ordinal);

        // Only loop height varies — the other three must NOT pick up a marker they did not earn.
        Assert.DoesNotContain("*", row.Diameter, StringComparison.Ordinal);
        Assert.DoesNotContain("*", row.Material, StringComparison.Ordinal);
    }

    /// <summary>Diameter and material are marked by the same rule, on their own quantity only.</summary>
    [Fact]
    public void DiameterAndMaterial_AreMarkedIndependently()
    {
        var vm = new WBondViewModel(Design(wires: 3));

        Assert.True(vm.SetWireDiameter(0, WBondUnits.ToNm(2.0, WBondUnit.Mil)));
        var row = Assert.Single(new WBondDocumentViewModel(vm).Panel.Rows);

        Assert.EndsWith(WBondPanelViewModel.NonUniformMarker, row.Diameter, StringComparison.Ordinal);
        Assert.DoesNotContain("*", row.Material, StringComparison.Ordinal);
        Assert.DoesNotContain("*", row.LoopHeight, StringComparison.Ordinal);

        // 1 / 1 / 2 mil → median 1 mil, not the 1.333 mean.
        Assert.StartsWith("1.0", row.Diameter, StringComparison.Ordinal);

        Assert.True(vm.SetWireMaterial(0, "Aluminum"));
        row = Assert.Single(new WBondDocumentViewModel(vm).Panel.Rows);
        Assert.EndsWith(WBondPanelViewModel.NonUniformMarker, row.Material, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>An even count takes the mean of the two middles for a length, and the lower middle for a
    /// material</b> — there is no averaging two materials, and the value shown has to be one the
    /// "set every wire" prompt can open on.
    /// </summary>
    [Fact]
    public void AnEvenCount_MediansTheTwoMiddles()
    {
        var vm = new WBondViewModel(Design(wires: 2));   // both already 1 mil, both Gold

        Assert.True(vm.SetWireDiameter(1, WBondUnits.ToNm(2.0, WBondUnit.Mil)));
        Assert.True(vm.SetWireMaterial(1, "Aluminum"));

        var row = Assert.Single(new WBondDocumentViewModel(vm).Panel.Rows);

        Assert.StartsWith("1.5", row.Diameter, StringComparison.Ordinal);       // mean of 1 and 2
        Assert.StartsWith("Aluminum", row.Material, StringComparison.Ordinal);  // lower of the two names
    }

    /// <summary>
    /// Span is compared as whole NANOMETRES, not as the double the chord length computes to — two
    /// wires cut to the same span at different orientations differ in the last bits of the double and
    /// would otherwise carry a <c>*</c> the user can neither act on nor clear.
    /// </summary>
    [Fact]
    public void EquallySpannedWiresAtDifferentAngles_AreNotMarkedNonUniform()
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        // Two 100 mil spans, one along +x and one along +y: identical spans, different arithmetic.
        var array = new WireArray { Name = "G1", Profile = profile.Name };
        array.Wires.Add(profile.CreateWire(Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 4),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        array.Wires.Add(profile.CreateWire(Point3.Mils(0, 50, 4), Point3.Mils(0, 150, 4),
                                           WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        design.Arrays.Add(array);

        var row = Assert.Single(new WBondDocumentViewModel(new WBondViewModel(design)).Panel.Rows);

        Assert.DoesNotContain("*", row.Span, StringComparison.Ordinal);
        Assert.StartsWith("100.0", row.Span, StringComparison.Ordinal);
    }

    /// <summary>
    /// Setting a quantity from the panel is the GROUP setter — every wire in the array lands on the
    /// new value, and the marker clears because they now agree.
    /// </summary>
    [Fact]
    public void SettingFromThePanel_MovesEveryWireInTheArrayAndClearsTheMarker()
    {
        var vm = new WBondViewModel(Design(wires: 3, arrays: 2));

        Assert.True(vm.SetWireDiameter(0, WBondUnits.ToNm(3.0, WBondUnit.Mil)));
        Assert.EndsWith(WBondPanelViewModel.NonUniformMarker,
                        new WBondDocumentViewModel(vm).Panel.Rows[0].Diameter, StringComparison.Ordinal);

        Assert.Equal(3, vm.SetGroupDiameter(0, WBondUnits.ToNm(2.0, WBondUnit.Mil)));

        var rows = new WBondDocumentViewModel(vm).Panel.Rows;
        Assert.StartsWith("2.0", rows[0].Diameter, StringComparison.Ordinal);
        Assert.DoesNotContain("*", rows[0].Diameter, StringComparison.Ordinal);

        // …and the OTHER array is untouched, which is what "for every wire in THIS array" means.
        Assert.StartsWith("1.0", rows[1].Diameter, StringComparison.Ordinal);
    }

    /// <summary>
    /// All four rows are double-clickable and land on the SAME group prompts the profile view's
    /// context menu uses — one implementation, one undo entry, one refusal path.
    /// </summary>
    [Fact]
    public void AllFourGeometryRows_AreDoubleClickableIntoTheGroupPrompts()
    {
        var xaml = EditorXaml();
        foreach (var handler in new[] { "OnArrayLoopHeightDoubleTapped", "OnArraySpanDoubleTapped",
                                        "OnArrayDiameterDoubleTapped", "OnArrayMaterialDoubleTapped" })
            Assert.Contains($"DoubleTapped=\"{handler}\"", xaml, StringComparison.Ordinal);

        var code = Read("src/Ui/Views/WBond/WBondEditorView.axaml.cs");
        foreach (var prompt in new[] { "SetGroupLoopHeightAsync", "SetGroupSpanAsync",
                                       "SetGroupDiameterAsync", "SetGroupMaterialAsync" })
            Assert.Contains(prompt, code, StringComparison.Ordinal);

        // The prompts take the array they act on — reading the CONTEXT MENU's captured array from the
        // panel's double-click would edit whichever group was last right-clicked.
        var menu = Read("src/Ui/Views/WBond/WBondEditorView.ProfileMenu.cs");
        Assert.Contains("SetGroupLoopHeightAsync(int arrayIndex)", menu, StringComparison.Ordinal);
        Assert.Contains("SetGroupMaterialAsync(int arrayIndex)", menu, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four values are plain <c>TextBlock</c>s, not <c>SelectableTextBlock</c>s: a double-click on
    /// selectable text means "select the word", and that gesture now means "edit the array".
    /// </summary>
    [Fact]
    public void TheSettableValues_AreNotSelectableTextBlocks()
    {
        var xaml = EditorXaml();

        foreach (var binding in new[] { "{Binding LoopHeight}", "{Binding Span}",
                                        "{Binding Diameter}", "{Binding Material}" })
        {
            int at = xaml.IndexOf(binding, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{binding} is gone from the card.");

            int elementStart = xaml.LastIndexOf('<', at);
            Assert.StartsWith("<TextBlock", xaml[elementStart..], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The prompt opens on the number the panel is SHOWING — the median — not on the first wire's
    /// value and not on a bound profile's height, which reads 0 on a group of free wires.
    /// </summary>
    [Fact]
    public void ThePromptSeed_IsTheMedianThePanelShows()
    {
        var menu = Read("src/Ui/Views/WBond/WBondEditorView.ProfileMenu.cs");

        Assert.Contains("SeedNm(arrayIndex, r => r.LoopHeightMm.Value)", menu, StringComparison.Ordinal);
        Assert.Contains("SeedNm(arrayIndex, r => r.SpanMm.Value)", menu, StringComparison.Ordinal);
        Assert.Contains("SeedNm(arrayIndex, r => r.DiameterMm.Value)", menu, StringComparison.Ordinal);

        Assert.DoesNotContain("FirstWireOfGroup", menu, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mutual rows are the SAME size as a self inductance (owner: "mutuals are just as important
    /// to user as self inductance"), not the 10 px the collapsed detail rows use.
    /// </summary>
    [Fact]
    public void TheMutualRows_AreTheSameSizeAsASelfInductance()
    {
        var xaml = EditorXaml();

        int box = xaml.IndexOf("{Binding MutualPairs}", StringComparison.Ordinal);
        Assert.True(box >= 0, "The mutual box is gone.");

        int end = xaml.IndexOf("</ItemsControl>", box, StringComparison.Ordinal);
        Assert.True(end > box);

        string block = xaml[box..end];
        Assert.Contains("FontSize=\"12\"", block, StringComparison.Ordinal);
        Assert.DoesNotContain("FontSize=\"10\"", block, StringComparison.Ordinal);
    }
}
