// ================================================================
//  MatchRound6Tests.cs  —  the owner's 2026-08-20 round-6 list for the Match Designer.
//
//  Same discipline as rounds 1-5: view-model, geometry and source-scan tests, never pixels. Where the
//  ask is about layout declared in AXAML, or about a wiring path that needs a live Avalonia
//  application, the assertion is made against the source the mechanism is written in and NAMES that
//  mechanism — a scan for "the file mentions the word" would pass over a broken fix.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound6Tests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    private static MatchDesignerViewModel OpenStandalone(string? root = null)
    {
        var d = new MatchDesignerViewModel();
        d.SetStandalone(root);
        return d;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One source file with its COMMENTS STRIPPED — every scan below is about what the code
    /// does, and a comment that quotes the thing it replaced is what a naive scan trips over.</summary>
    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);   // XML / AXAML
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);    // C# block
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);           // C# line + XML doc
        return raw;
    }

    private static string Xaml()   => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");
    private static string Code()   => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");
    private static string Canvas() => Src("src", "Ui", "Views", "Match", "MatchSchematicCanvas.cs");

    /// <summary>The text from <paramref name="start"/> up to and including the next <paramref name="stop"/>.</summary>
    private static string Span(string source, string start, string stop)
    {
        int i = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{start}' is not in the source");
        int j = source.IndexOf(stop, i + start.Length, StringComparison.Ordinal);
        Assert.True(j > i, $"'{start}' is never closed by '{stop}'");
        return source[i..(j + stop.Length)];
    }

    /// <summary>The body of one method, from its signature to the next one at the same indent.</summary>
    private static string Body(string source, string signature)
    {
        int i = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source");
        int j = source.IndexOf("\n    private ", i + signature.Length, StringComparison.Ordinal);
        int k = source.IndexOf("\n    public ", i + signature.Length, StringComparison.Ordinal);
        int end = new[] { j, k }.Where(x => x > 0).DefaultIfEmpty(source.Length).Min();
        return source[i..end];
    }

    // ══ 1. Tools ▸ Match Designer — a Designer bound to nothing ══════════════

    /// <summary>
    /// <b>A standalone Designer authors a real design</b> (owner: "an 'orphaned' Designer window
    /// appears that still allows user to author a design and Flatten to Cell").
    /// </summary>
    /// <remarks>
    /// The point of the test is the DIFFERENCE from <c>IsOrphaned</c>, which is the other window with
    /// no component behind it and is deliberately frozen. If the two were conflated, every setter here
    /// would be a no-op and the window would look identical while doing nothing — so the assertions
    /// are that edits LAND, not merely that no exception is thrown.
    /// </remarks>
    [Fact]
    public void AStandaloneDesigner_Authors_AndIsNotOrphaned()
    {
        var d = OpenStandalone();

        Assert.True(d.IsStandalone);
        Assert.False(d.IsOrphaned);
        Assert.Equal("Match Designer", d.Title);
        Assert.Null(d.Target);

        // It opens on a real synthesised network, not on nothing.
        Assert.NotNull(d.Rebuild?.Network);

        double f1 = d.F1;
        d.SetTermination(2, Termination.Resistive(12.5), fromProbe: false);
        Assert.Equal(12.5, d.Design.Term2.R, 9);
        Assert.Equal(f1, d.F1, 9);                      // unrelated fields untouched
        Assert.NotNull(d.Rebuild?.Network);             // and it re-synthesised onto the new end

        // Revert is the only "undo" a standalone Designer has, and it works.
        d.Revert();
        Assert.NotEqual(12.5, d.Design.Term2.R);
    }

    /// <summary>
    /// <b>Probe is refused, and says why</b> — a standalone Designer has no circuit to look into.
    /// </summary>
    [Fact]
    public void AStandaloneDesigner_CannotProbe_AndSaysSo()
    {
        var d = OpenStandalone();

        Assert.False(d.Term1.CanProbe);
        Assert.False(d.Term2.CanProbe);
        Assert.Equal(MatchProbeBlock.NoSchematic, d.Term1.Availability.Block);
        Assert.Contains("not open in a schematic", d.Term1.Availability.Reason,
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Flatten to Cell runs, and writes a real cell</b> — the half of the ask that would be easy to
    /// leave disabled.
    /// </summary>
    /// <remarks>
    /// The write goes through <c>MatchFlatten.Write</c>, the SAME primitive the schematic-bound path
    /// uses through <c>FlattenMatchCommand</c>; what differs is only the transaction around it (no
    /// instance to replace, no undo stack). So the cell this produces has to be the same cell — which
    /// is what the element count and the carried terminations are checked for.
    /// </remarks>
    [Fact]
    public void AStandaloneDesigner_FlattensToACell_WithNoSchematicAndNoReplacement()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-match-standalone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var d = OpenStandalone(dir);

            Assert.True(d.CanFlatten, d.FlattenTooltip);
            Assert.Equal(dir, d.FlattenAvailability.ParentDir);
            Assert.Equal("MN1_match", d.FlattenAvailability.DefaultName);

            // replaceInPlace is passed TRUE deliberately: there is nothing to replace, and the
            // standalone path has to ignore it rather than fault on it.
            var result = d.Flatten(dir, "MN1_match", replaceInPlace: true);
            output.WriteLine(result.Message);

            Assert.True(result.Ok, result.Message);
            Assert.Null(result.Replacement);
            Assert.NotNull(result.CellDir);
            Assert.True(Directory.Exists(result.CellDir!));
            Assert.NotEmpty(Directory.GetFiles(result.CellDir!, "*", SearchOption.AllDirectories));

            // A second flatten under the same name is refused, never merged into.
            var again = d.Flatten(dir, "MN1_match", replaceInPlace: false);
            Assert.False(again.Ok);
            Assert.Contains("already exists", again.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// <b>A design that does not synthesise is refused with the reason, not with a disabled button and
    /// silence.</b>
    /// </summary>
    [Fact]
    public void AStandaloneFlatten_RefusesADesignThatDoesNotSynthesise()
    {
        var refused = MatchFlattenService.StandaloneAvailability(rebuild: null, "MN1", startingDir: null);
        Assert.False(refused.CanRun);
        Assert.Contains("does not synthesise", refused.Reason, StringComparison.OrdinalIgnoreCase);

        var run = MatchFlattenService.RunStandalone(null, MatchEmbedding.DefaultDesign(), "MN1",
                                                    Path.GetTempPath(), "whatever");
        Assert.False(run.Ok);
        Assert.Null(run.CellDir);
    }

    /// <summary>The Tools menu opens it through the view-model command, on BOTH hand-mirrored surfaces.</summary>
    [Fact]
    public void ToolsMenu_OpensAStandaloneDesigner_ThroughTheSameCommandOnBothSurfaces()
    {
        string shell = Src("src", "Ui", "Views", "WorkspaceWindow.axaml");
        Assert.Equal(2, Regex.Matches(shell, @"NewMatchDesignerCommand").Count);

        string wvm = Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
        var body = Body(wvm, "private void NewMatchDesigner()");
        Assert.Contains("ShowStandalone", body, StringComparison.Ordinal);
        Assert.Contains("CurrentWorkspaceRoot", body, StringComparison.Ordinal);

        // The window entry point sets the STANDALONE mode, never SetTarget.
        var show = Body(Code(), "public static MatchDesignerWindow ShowStandalone(");
        Assert.Contains("SetStandalone", show, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTarget", show, StringComparison.Ordinal);
    }

    // ══ 2. The slider drag no longer lets the plots run per mouse-move ═══════

    /// <summary>
    /// <b>The drag handlers TUNNEL, because Avalonia's Thumb marks the press and the release handled
    /// before either reaches the Slider</b> (owner: "when I change Transforms with the slider UI
    /// controls, the plot's render glitches").
    /// </summary>
    /// <remarks>
    /// This is the whole bug. The hold exists and always did — what did not happen was the hold being
    /// STARTED, because a XAML <c>PointerPressed=</c> attribute subscribes to the bubbling route with
    /// <c>handledEventsToo: false</c>. So the assertion is specifically that the subscription is not
    /// a XAML attribute any more and that it tunnels; a test that only checked
    /// "BeginTransformDrag is called somewhere" passed throughout the broken period.
    /// </remarks>
    [Fact]
    public void TheTransformSlider_StartsItsGestureOnATunnellingHandler_NotAXamlAttribute()
    {
        string xaml = Xaml();
        Assert.DoesNotContain("PointerPressed=\"OnSliderPressed\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerReleased=\"OnSliderReleased\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"OnSliderLoaded\"", xaml, StringComparison.Ordinal);

        var wire = Body(Code(), "private void OnSliderLoaded(");
        output.WriteLine(wire);
        Assert.Contains("RoutingStrategies.Tunnel", wire, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", wire, StringComparison.Ordinal);
        Assert.Contains("PointerPressedEvent", wire, StringComparison.Ordinal);
        Assert.Contains("PointerReleasedEvent", wire, StringComparison.Ordinal);
        // A capture lost without a release must still end the gesture, or the plots stay held.
        Assert.Contains("PointerCaptureLostEvent", wire, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>While a gesture is running the plots are held; the ladder is not.</b>
    /// </summary>
    /// <remarks>
    /// The measurable form of "no glitches": the response SNP — which is what both plots and both
    /// autoscales are built from — is the same object it was before the drag, while the element values
    /// have moved. One rebuild lands on release.
    /// </remarks>
    [Fact]
    public void DuringADrag_ThePlotsAreHeld_AndTheLadderIsNot()
    {
        var (_, _, d) = Open();
        var pairs = d.AvailablePairs();
        Assert.NotEmpty(pairs);
        d.AddTransform(pairs[0]);

        var before = d.ResponseSnp;
        string valuesBefore = string.Join("|", d.Elements.Select(e => e.ValueWithUnit));
        double n0 = d.Transforms[0].N;
        double target = (n0 + d.Transforms[0].NMax) / 2;
        Assert.True(target > n0, "the fixture's first transform has no room to move");

        d.BeginTransformDrag();
        d.SetTransformN(0, target);

        Assert.Same(before, d.ResponseSnp);                                     // plots held
        Assert.NotEqual(valuesBefore, string.Join("|", d.Elements.Select(e => e.ValueWithUnit)));

        d.EndTransformDrag();
        Assert.NotSame(before, d.ResponseSnp);                                  // one rebuild on release
    }

    // ══ 3. The termination card ══════════════════════════════════════════════

    /// <summary>
    /// <b>"R" and the C/L/– selector sit in ONE column of the same width</b> (owner: "the UI control
    /// to set L, C or - is not horizontally aligned to the R text rendered above it (2 instances of
    /// this) — make the text portion align").
    /// </summary>
    /// <remarks>
    /// They could not be aligned before: the R row's label column was <c>Auto</c> and sized to a 7 px
    /// glyph, while the row beneath it sized to a 24 px button. The fix is a shared FIXED width with
    /// the letter centred in it, so both glyphs start at the same x — which is a property a test can
    /// state, unlike "it looks aligned".
    /// </remarks>
    [Fact]
    public void TheRLabel_AndTheKindSelector_ShareOneColumnWidth()
    {
        string xaml = Xaml();

        int rRow = xaml.IndexOf("Text=\"R\"", StringComparison.Ordinal);
        Assert.True(rRow > 0);
        int rGrid = xaml.LastIndexOf("<Grid ColumnDefinitions=", rRow, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"24,*\"", xaml[rGrid..rRow], StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"",
                        xaml[rRow..(rRow + 200)], StringComparison.Ordinal);

        int kind = xaml.IndexOf("MatchTerminationViewModel.KindOptions", StringComparison.Ordinal);
        Assert.True(kind > 0);
        int kGrid = xaml.LastIndexOf("<Grid ColumnDefinitions=", kind, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"24,*\"", xaml[kGrid..kind], StringComparison.Ordinal);
        Assert.Contains("Width=\"24\"", xaml[kGrid..kind], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The selectors read as BUTTONS, not as text in a box</b> (owner: "can you make it look more
    /// like a button? I think it needs a background color that is different than the Group box
    /// background colour behind it").
    /// </summary>
    /// <remarks>
    /// Two halves, and the second is the one that makes it work. <c>Button.seg-btn</c> is an
    /// application-scope STYLE that paints the selector's face transparent, and a style cannot be
    /// overridden by another style from a host with any confidence about which wins. So the control
    /// theme passes the <c>IconSelectButton</c>'s own Background down as a TEMPLATE value — a local
    /// value, which outranks any style — and defaults it to Transparent, so every existing selector
    /// in the application looks exactly as it did.
    /// </remarks>
    [Fact]
    public void TheSelectors_CarryTheirOwnBackground_ThroughTheControlTheme()
    {
        string theme = Src("src", "Ui", "Styles", "SegmentedSelect.axaml");
        Assert.Contains("Background=\"{TemplateBinding Background}\"", theme, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\"/>", theme,
                        StringComparison.Ordinal);

        // Both termination selectors — the topology one and the kind one — take it.
        string xaml = Xaml();
        // THREE: the topology selector, the reactance-kind selector (one template, both
        // terminations), and the Bands selector in the Frequency Band card's header (match.md §18.7).
        // The Order selector was a fourth and is gone with its card — the order is not an input any
        // more, it is something a solution carries (owner, 2026-08-28).
        Assert.Equal(3, Regex.Matches(xaml,
            @"Background=""\{DynamicResource ButtonBackground\}""").Count);
    }

    /// <summary>
    /// <b>No "Topology" label and no Conjugate checkbox</b> (owner: "remove the Topology text object
    /// (2 instance) and move the Parallel/Series UI selector to the left in place of Topology";
    /// "remove the Conjugate checkbox (2 instance) from the Specification panel").
    /// </summary>
    /// <remarks>
    /// One template serves both terminations, so "2 instances" is one edit — which is exactly why the
    /// count is asserted against the RENDERED surface (the template is instantiated twice) rather than
    /// by counting occurrences in the file.
    /// </remarks>
    [Fact]
    public void TheTerminationCard_HasNoTopologyLabel_AndNoConjugateCheckbox()
    {
        string xaml = Xaml();

        Assert.DoesNotContain("Text=\"Topology\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Conjugate\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding Conjugate, Mode=TwoWay}\"", xaml,
                             StringComparison.Ordinal);

        // The selector moved into the column the label used to hold, at the LEFT edge.
        int topo = xaml.IndexOf("MatchTerminationViewModel.TopologyOptions", StringComparison.Ordinal);
        Assert.True(topo > 0);
        int start = xaml.LastIndexOf("<ddc:IconSelectButton", topo, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml[start..topo], StringComparison.Ordinal);
        Assert.DoesNotContain("Grid.Column=\"1\"", xaml[start..topo], StringComparison.Ordinal);

        // The card is still exactly ONE template, so both ends changed together.
        Assert.Equal(2, Regex.Matches(xaml,
            @"ContentTemplate=""\{StaticResource TerminationTemplate\}""").Count);
    }

    // ══ 4. Probe applies the best fit and offers no menu ═════════════════════

    /// <summary>
    /// <b>A probe result applies itself, and the four-candidate listing is gone</b> (owner: "when
    /// clicking probe, do not show all the options to the user — simply pick the best option and
    /// automatically use it. Make sure Undo/Redo works for this").
    /// </summary>
    /// <remarks>
    /// The undo half is the part worth pinning: applying a probed termination is one
    /// <c>SetParametersCommand</c> on the OWNING SCHEMATIC's stack, so a single Ctrl/Cmd+Z from either
    /// window puts the design back. The probe itself needs a live circuit and an engine run, so the
    /// termination is applied here the same way <c>ShowProbeResult</c> applies it.
    /// </remarks>
    [Fact]
    public void AProbedTermination_IsOneUndoableStep_AndNoListIsOffered()
    {
        Assert.DoesNotContain("ProbeFits", Xaml(), StringComparison.Ordinal);

        var (vm, _, d) = Open();
        var before = d.Design.Term1;                       // Termination is a record: immutable
        Assert.False(vm.UndoRedo.CanUndo, "opening a Designer must not put anything on the stack");

        d.ApplyProbedTermination(
            1, new Termination(18.0, ReactanceKind.L, TerminationTopology.Series, 2.4e-9,
                               Probed: true, ProbedAtUtc: DateTime.UtcNow));

        Assert.Equal(18.0, d.Design.Term1.R, 9);
        Assert.True(d.Term1.IsProbed);
        Assert.True(vm.UndoRedo.CanUndo);

        // ONE step, not one per field: a single undo puts the whole termination back.
        Assert.True(d.UndoCommand.CanExecute(null));
        d.UndoCommand.Execute(null);
        Assert.Equal(before.R, d.Design.Term1.R, 9);
        Assert.False(d.Term1.IsProbed);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ══ 5. Ripple ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Ripple IS an input; when it is not settable the row says why and looks it</b> (owner: "is
    /// Ripple, dB supposed to be an input? If so, the inline text editor does not show when I double
    /// click on its value").
    /// </summary>
    /// <remarks>
    /// It was disabled, correctly, and looked identical to an enabled field — an
    /// <c>InlineEditText</c> at rest is a bare <c>TextBlock</c>, and Avalonia's Fluent theme dims
    /// neither a disabled <c>TextBlock</c> nor a disabled <c>Panel</c>. So there are two assertions:
    /// the sentence, and the style that makes the state visible.
    /// </remarks>
    [Fact]
    public void TheRippleField_IsSettableWhenItApplies_AndExplainsItselfWhenItDoesNot()
    {
        var (_, _, d) = Open();

        Assert.True(d.RippleEnabled);
        Assert.Equal("", d.RippleNote);
        d.RippleEntry = "0.25 dB";
        Assert.Equal(0.25, d.RippleDb, 9);

        // A reactive termination is what switches it off — and it names WHICH end.
        d.SetTermination(
            2, new Termination(10.0, ReactanceKind.L, TerminationTopology.Series, 1.5e-9));
        Assert.False(d.RippleEnabled);
        Assert.Contains("Termination 2", d.RippleNote, StringComparison.Ordinal);
        Assert.Contains("reactance", d.RippleNote, StringComparison.OrdinalIgnoreCase);

        // The note is ONE LINE since round 7 (owner: "there is not enough height space for the
        // current amount of text") — it still names the end, and the paragraph it shed is on the
        // row's own tooltip. Both halves are asserted so neither can quietly go.
        //
        // The tooltip quotes no design-note SECTION (owner, 2026-08-28: the user does not read
        // those), so what pins it here is the explanation itself, not the reference it used to carry.
        Assert.True(d.RippleNote.Length < 60, $"the note grew back: \"{d.RippleNote}\"");
        Assert.DoesNotContain("§", d.RippleTooltip, StringComparison.Ordinal);
        Assert.Contains("set by the terminations rather than by hand", d.RippleTooltip,
                        StringComparison.Ordinal);
        Assert.Contains("Termination 2", d.RippleTooltip, StringComparison.Ordinal);

        string xaml = Xaml();
        Assert.Contains("Selector=\"ctl|InlineEditText:disabled\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RippleNote}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding RippleTooltip}\"", xaml, StringComparison.Ordinal);
    }

    // ══ 6. The two pane expanders ════════════════════════════════════════════

    /// <summary>
    /// <b>The two expanders are mutually exclusive, and each glyph shows its own state</b> (owner:
    /// "the button icon shows state… when expanded, the button changes to an icon pointing up and to
    /// the left").
    /// </summary>
    /// <remarks>
    /// Mutual exclusion is not a nicety: each toggle takes the OTHER pane's column, so both on at once
    /// is a state with no width left to describe.
    /// </remarks>
    [Fact]
    public void TheTwoPaneExpanders_AreMutuallyExclusive_AndTheirGlyphsShowState()
    {
        var (_, _, d) = Open();

        Assert.False(d.NetworkExpanded);
        Assert.False(d.ResponseExpanded);
        Assert.Equal(Material.Icons.MaterialIconKind.ArrowBottomRight, d.NetworkExpandIcon);
        Assert.Equal(Material.Icons.MaterialIconKind.ArrowBottomLeft, d.ResponseExpandIcon);

        d.NetworkExpanded = true;
        Assert.False(d.ResponseExpanded);
        Assert.Equal(Material.Icons.MaterialIconKind.ArrowTopLeft, d.NetworkExpandIcon);

        d.ResponseExpanded = true;
        Assert.False(d.NetworkExpanded);
        Assert.Equal(Material.Icons.MaterialIconKind.ArrowTopRight, d.ResponseExpandIcon);
        Assert.Equal(Material.Icons.MaterialIconKind.ArrowBottomRight, d.NetworkExpandIcon);

        d.ResponseExpanded = false;
        Assert.False(d.NetworkExpanded);
        Assert.False(d.ResponseExpanded);
    }

    /// <summary>
    /// <b>An expanded pane takes the other's COLUMN, not just its visibility</b>.
    /// </summary>
    /// <remarks>
    /// Hiding a pane alone leaves its 380 px column standing and the window shows a hole where the
    /// response used to be. The width is moved from code-behind rather than bound, because a
    /// <c>ColumnDefinition</c> is not in the logical tree — no DataContext reaches it and a
    /// <c>{Binding}</c> on its <c>Width</c> silently resolves to nothing, with no error to notice.
    /// </remarks>
    [Fact]
    public void AnExpandedPane_MovesTheColumnWidth_FromCodeNotFromABinding()
    {
        string xaml = Xaml();
        Assert.DoesNotContain("Width=\"{Binding NetworkColumnWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Name=\"PaneGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !ResponseExpanded}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !NetworkExpanded}\"", xaml, StringComparison.Ordinal);

        string code = Code();
        var sync = Body(code, "private void SyncPaneLayout()");
        output.WriteLine(sync);
        Assert.Contains("ColumnDefinitions[1].Width", sync, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions[2].Width", sync, StringComparison.Ordinal);
        Assert.Contains("NetworkExpanded", sync, StringComparison.Ordinal);
        Assert.Contains("ResponseExpanded", sync, StringComparison.Ordinal);

        // …and it is actually called when either flag moves.
        var changed = Body(code, "private void OnVmPropertyChanged(");
        Assert.Contains("SyncPaneLayout()", changed, StringComparison.Ordinal);
        Assert.Contains("NetworkExpanded", changed, StringComparison.Ordinal);
        Assert.Contains("ResponseExpanded", changed, StringComparison.Ordinal);

        // The resting width the code restores is the SAME number the AXAML declares.
        // 285 since 2026-08-28 (the Solutions list moved into the specification pane), and three
        // columns rather than four (the drawer that was the fourth is what moved).
        Assert.Contains("ColumnDefinitions=\"285,*,380\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResponseColumnWidth = 380", code, StringComparison.Ordinal);
    }

    // ══ 7. Zoom to Fit ═══════════════════════════════════════════════════════

    /// <summary>
    /// <b>A Zoom to Fit button overlays the schematic, and it is the editor's own button</b> (owner:
    /// "it should look and feel exactly like the Schematic editor's Zoom to Fit button, only this new
    /// one is overlayed… make sure the keystroke &lt;F&gt; will zoom the schematic to fit").
    /// </summary>
    /// <remarks>
    /// "Exactly like" is asserted against the editor's own AXAML rather than restated here, so the two
    /// cannot drift into different glyphs or different tooltips.
    /// </remarks>
    [Fact]
    public void TheSchematicPane_CarriesTheEditorsOwnZoomToFitButton_Overlaid()
    {
        string xaml = Xaml();
        int btn = xaml.IndexOf("Name=\"NetworkZoomFitButton\"", StringComparison.Ordinal);
        Assert.True(btn > 0, "there is no Zoom to Fit button on the network pane");
        string block = xaml[btn..(btn + 400)];

        // Overlaid: pinned to the pane's top-left, outside the canvas's own transform.
        Assert.Contains("HorizontalAlignment=\"Left\"", block, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Top\"", block, StringComparison.Ordinal);

        // …and it is a SIBLING of the canvas in the host Panel, so no zoom or pan moves it.
        int host = xaml.IndexOf("Name=\"NetworkSchematicHost\"", StringComparison.Ordinal);
        Assert.True(host > 0 && host < btn);

        string editor = Src("src", "Ui", "Views", "Content", "SchematicView.axaml");
        int fit = editor.IndexOf("Click=\"OnZoomToFit\"", StringComparison.Ordinal);
        Assert.True(fit > 0);
        string editorBlock = editor[fit..(fit + 400)];

        foreach (var shared in new[] { "Zoom to Fit  (F)", "Kind=\"FitToPage\"", "Padding=\"6,3\"",
                                       "Width=\"16\" Height=\"16\"" })
        {
            Assert.Contains(shared, editorBlock, StringComparison.Ordinal);
            Assert.Contains(shared, block, StringComparison.Ordinal);
        }

        // The button is wired to the canvas's own ZoomToFit.
        Assert.Contains("WireButton(\"NetworkZoomFitButton\"", Code(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>F re-frames, and never while a text box owns the keyboard.</b>
    /// </summary>
    /// <remarks>
    /// The guard is the whole reason this is an <c>OnKeyDown</c> override rather than a
    /// <c>Window.KeyBindings</c> entry: a <c>TextBox</c> does not mark <c>KeyDown</c> handled for an
    /// ordinary character — it consumes <c>TextInput</c> — so a window-level bare-letter binding would
    /// re-frame the drawing every time the user typed an "f" into a field. It is the same guard
    /// <c>SchematicView</c> applies for the same key.
    /// </remarks>
    [Fact]
    public void TheFKey_ReFrames_ButNotWhileATextBoxHasTheKeyboard()
    {
        string xaml = Xaml();
        Assert.DoesNotContain("Gesture=\"F\"", xaml, StringComparison.Ordinal);

        var key = Body(Code(), "protected override void OnKeyDown(");
        output.WriteLine(key);
        Assert.Contains("Key.F", key, StringComparison.Ordinal);
        Assert.Contains("is TextBox", key, StringComparison.Ordinal);
        Assert.Contains("ZoomToFit()", key, StringComparison.Ordinal);
        Assert.Contains("KeyModifiers.None", key, StringComparison.Ordinal);
    }

    /// <summary>The double-click re-frame is gone, and the canvas can still re-frame on request.</summary>
    [Fact]
    public void TheDoubleClickReFrame_IsGone_ButZoomToFitItselfRemains()
    {
        string canvas = Canvas();
        var tap = Body(canvas, "protected override void OnDoubleTapped");
        Assert.DoesNotContain("ZoomToFit()", tap, StringComparison.Ordinal);
        Assert.Contains("public void ZoomToFit()", canvas, StringComparison.Ordinal);
    }

    // ══ 8. The transform rack ════════════════════════════════════════════════

    /// <summary>
    /// <b>N is typed into an inline editor, and bad input is REFUSED with the reason</b> (owner: "the
    /// textedit box of the N1/N2… transforms to text. Allow user to change it using the inline text
    /// editor. Its input must be validated/refused if bad input").
    /// </summary>
    /// <remarks>
    /// Refused, not clamped. The bounds are recomputed per rebuild from the element values as they
    /// stand, and a field that answers a typed 4 with a 2.37 and says nothing is a field the user
    /// stops believing. Both failure modes are covered — not a number at all, and a number this
    /// transform cannot take — because they produce different sentences.
    /// </remarks>
    [Fact]
    public void ATypedN_IsValidated_AndRefusedWithTheReason()
    {
        var (_, _, d) = Open();
        var pairs = d.AvailablePairs();
        Assert.NotEmpty(pairs);
        d.AddTransform(pairs[0]);

        var row = d.Transforms[0];
        double n0 = row.N;

        // Not a number.
        row.NEntry = "banana";
        Assert.Equal(n0, row.N, 9);
        Assert.Equal(row.N.ToString("0.#####", CultureInfo.InvariantCulture), row.NEntry);
        Assert.Contains("not a turns ratio", d.TransformNote, StringComparison.OrdinalIgnoreCase);

        // Zero and negative are not turns ratios either.
        row.NEntry = "0";
        Assert.Equal(n0, row.N, 9);
        row.NEntry = "-2";
        Assert.Equal(n0, row.N, 9);

        // Outside the recomputed range: refused, with the range NAMED.
        row.NEntry = (row.NMax * 10).ToString("0.#####", CultureInfo.InvariantCulture);
        Assert.Equal(n0, row.N, 9);
        Assert.Contains("must be between", d.TransformNote, StringComparison.OrdinalIgnoreCase);

        // A good one lands, and clears the note.
        double good = (n0 + row.NMax) / 2;
        row.NEntry = good.ToString("0.#####", CultureInfo.InvariantCulture);
        Assert.Equal(good, row.N, 4);
        Assert.Equal("", d.TransformNote);

        // The bound the field just PRINTED is itself acceptable — the format rounds to five DECIMAL
        // places, so a printed bound can be up to 5e-6 off the real one, and refusing the number the
        // field showed is the one refusal nobody can act on.
        row = d.Transforms[0];
        row.NEntry = row.NMax.ToString("0.#####", CultureInfo.InvariantCulture);
        Assert.Equal("", d.TransformNote);
        Assert.InRange(row.N, row.NMin, row.NMax);   // accepted by CLAMPING, never taken literally

        row.NEntry = row.NMin.ToString("0.#####", CultureInfo.InvariantCulture);
        Assert.Equal("", d.TransformNote);
        Assert.InRange(row.N, row.NMin, row.NMax);
    }

    // ══ 9. The slider's travel is what N can actually reach ═════════════════

    /// <summary>
    /// <b>The slider is bounded by what the linkage will actually settle on, not by the positivity
    /// range</b> (owner: "sometimes I can move the slider control higher, but the transform is
    /// already at its maximum level").
    /// </summary>
    /// <remarks>
    /// <c>TransformRange</c> is how far this transform can go before one of its own three products
    /// goes negative. With Link on, <c>MatchLinkage.Redistribute</c> ends by recomputing the dragged
    /// transform from what the OTHERS settled at — so once they are on their own bounds the dragged N
    /// stops moving while the thumb, bounded by the positivity range, keeps going. The reachable end
    /// points are measured with the same function that will run, so the two cannot disagree.
    /// </remarks>
    [Fact]
    public void TheSlider_IsBoundedByWhatTheLinkageCanReach_NotByThePositivityRange()
    {
        var (_, _, d) = Open();
        d.LinkTransforms = true;
        var pairs = d.AvailablePairs();
        Assert.True(pairs.Count >= 2, "the fixture offers too few pairs to link");
        d.AddTransform(pairs[0]);
        d.AddTransform(d.AvailablePairs()[0]);

        var row = d.Transforms[0];
        Assert.NotNull(row.Range);
        output.WriteLine($"{row.Label}: range=[{row.Range!.Min:G8},{row.Range.Max:G8}] "
                       + $"reach=[{row.NMin:G8},{row.NMax:G8}]");

        // The reachable interval is a SUB-interval of the positivity range, and contains where N is.
        Assert.True(row.NMin >= row.Range.Min - 1e-9);
        Assert.True(row.NMax <= row.Range.Max + 1e-9);
        Assert.InRange(row.N, row.NMin - 1e-6, row.NMax + 1e-6);

        // …and it is the honest bound: asking for either end SETTLES there, which is the property
        // that was false of the positivity range.
        foreach (double end in new[] { row.NMin, row.NMax })
        {
            d.SetTransformN(0, end);
            Assert.Equal(end, d.Transforms[0].N, 6);
        }

        // Asking for the positivity bound, when it is beyond reach, does NOT get there — which is
        // precisely the drag the owner could perform and the reason the slider must not offer it.
        if (row.Range.Max > row.NMax + 1e-6)
        {
            d.SetTransformN(0, row.Range.Max);
            Assert.True(d.Transforms[0].N < row.Range.Max - 1e-6,
                        "the positivity bound was reachable after all, so this fixture proves nothing");
        }
    }

    /// <summary>
    /// <b>With Link off, the two ranges coincide</b> — the narrowing is for the right reason.
    /// </summary>
    [Fact]
    public void WithLinkOff_TheSliderTravelsItsWholePositivityRange()
    {
        var (_, _, d) = Open();
        d.LinkTransforms = false;
        var pairs = d.AvailablePairs();
        Assert.NotEmpty(pairs);
        d.AddTransform(pairs[0]);

        var row = d.Transforms[0];
        Assert.NotNull(row.Range);
        Assert.Equal(row.Range!.Min, row.NMin, 12);
        Assert.Equal(row.Range.Max, row.NMax, 12);

        d.SetTransformN(0, row.Range.Max);
        Assert.Equal(row.Range.Max, d.Transforms[0].N, 9);
    }

    /// <summary>
    /// <b>Locking a row does not move its thumb</b> (owner: "when I lock a slider it is disabled —
    /// good — but the position of the slider circle changes between locked and unlocked state —
    /// bad").
    /// </summary>
    /// <remarks>
    /// A slider thumb draws at a FRACTION of its range, so a row whose bounds change while its value
    /// stands still is a circle that jumps for no reason the user can see. The reachable range used
    /// to fall back to the positivity range for a locked row, which widened it the moment it was
    /// locked. A lock decides whether the value may be MOVED and says nothing about where it could
    /// live — and <c>MatchLinkage.Redistribute</c> agrees, ignoring the driven slot's own lock — so
    /// the two states are now identical by construction.
    /// </remarks>
    [Fact]
    public void LockingARow_LeavesItsOwnBoundsAndItsValueExactlyWhereTheyWere()
    {
        var (_, _, d) = Open();
        d.LinkTransforms = true;
        d.AddTransform(d.AvailablePairs()[0]);
        Assert.True(d.Transforms.Count >= 2);

        var row = d.Transforms[0];
        (double min, double max, double n) = (row.NMin, row.NMax, row.N);
        output.WriteLine($"unlocked: [{min:G8}, {max:G8}] at {n:G8}");

        d.SetTransformLocked(0, true);
        row = d.Transforms[0];
        output.WriteLine($"locked:   [{row.NMin:G8}, {row.NMax:G8}] at {row.N:G8}");

        Assert.False(row.CanEditN);                     // still disabled, which was the good half
        Assert.Equal(min, row.NMin, 12);
        Assert.Equal(max, row.NMax, 12);
        Assert.Equal(n,   row.N,    12);

        d.SetTransformLocked(0, false);
        row = d.Transforms[0];
        Assert.Equal(min, row.NMin, 12);
        Assert.Equal(max, row.NMax, 12);
    }

    /// <summary>
    /// <b>A collapsed reachable interval NEVER disables a row — it falls back to the positivity
    /// range</b> (owner: "I can't slide any transforms anymore, they are all disabled even when they
    /// are unlocked").
    /// </summary>
    /// <remarks>
    /// This is the correction to the fix above, and the distinction is the whole lesson: a collapsed
    /// probe interval means <b>the linkage cannot improve Π N² from where the rack stands</b>, which
    /// is not the same statement as "this N cannot move". The state that produces it is an
    /// UNREACHABLE ratio — 5 Ω into 200 Ω needs a product of 54 while every transform's positivity
    /// range caps at 1, so every probe clamps to the same bound at both ends and every row reads as
    /// pinned. Disabling on that reading took the entire rack away in exactly the design the user was
    /// trying to rescue.
    /// </remarks>
    [Fact]
    public void AnUnreachableRatio_LeavesEverySliderUsable_OnItsPositivityRange()
    {
        var (_, _, d) = Open();
        d.SetTermination(1, Termination.Resistive(5.0));
        d.SetTermination(2, Termination.Resistive(200.0));
        d.LinkTransforms = true;
        for (int i = 0; i < 3 && d.AvailablePairs().Count > 0; i++) d.AddTransform(d.AvailablePairs()[0]);

        Assert.NotEmpty(d.Transforms);
        Assert.True(d.Rebuild!.Required > 1.0, "this fixture is meant to be a step UP");

        foreach (var row in d.Transforms)
        {
            output.WriteLine($"{row.Label}: range=[{row.Range?.Min:G6},{row.Range?.Max:G6}] "
                           + $"reach=[{row.NMin:G6},{row.NMax:G6}] canEdit={row.CanEditN}");
            Assert.True(row.CanEditN, $"{row.Label} was disabled on an unreachable ratio");
            Assert.Equal(row.Range!.Min, row.NMin, 12);   // fell back to the positivity range
            Assert.Equal(row.Range.Max, row.NMax, 12);
        }

        // Locking is still the ONE thing that disables a row, and it still says so.
        d.SetTransformLocked(0, true);
        Assert.False(d.Transforms[0].CanEditN);
        Assert.Contains("Locked", d.Transforms[0].DisabledReason, StringComparison.Ordinal);
    }

    // ══ 10. Two things that must not move, and one that must close ══════════

    /// <summary>
    /// <b>An open inline editor is committed by a press anywhere outside it</b> (owner: "if I click
    /// away from the inline text editor in the schematic, it does not close").
    /// </summary>
    /// <remarks>
    /// The box dismisses itself on LostFocus, and LostFocus never came: almost nothing in this window
    /// is FOCUSABLE — the schematic canvas is a plain <c>Control</c>, and so are the pane backgrounds,
    /// the TextBlocks and the borders — so a click on any of them moved focus nowhere and the box was
    /// never told. The schematic PAGE does not have this problem because its canvas takes focus for
    /// its own keyboard tools, which is why the same wiring worked there and not here.
    ///
    /// <para>Asserted against the window's tunnelling press handler, and specifically that the check
    /// runs BEFORE the left-button and modifier guards — a right-click or a Ctrl-click away from the
    /// box is just as much "away" as a plain one, and putting the check after them is the natural
    /// mistake.</para>
    /// </remarks>
    [Fact]
    public void APressOutsideTheInlineEditor_CommitsIt_WhateverTheButtonOrModifier()
    {
        var press = Body(Code(), "private void OnWindowPointerPressed(");
        output.WriteLine(press);

        Assert.Contains("_labelEditor is { IsVisible: true }", press, StringComparison.Ordinal);
        Assert.Contains("SchematicInlineEditBox", press, StringComparison.Ordinal);
        Assert.Contains("CommitLabelEdit()", press, StringComparison.Ordinal);

        int commit  = press.IndexOf("CommitLabelEdit()", StringComparison.Ordinal);
        int button  = press.IndexOf("IsLeftButtonPressed", StringComparison.Ordinal);
        int modifier = press.IndexOf("KeyModifiers.Control", StringComparison.Ordinal);
        Assert.True(commit > 0 && button > commit,
                    "the editor check must run before the left-button guard");
        Assert.True(modifier > commit, "the editor check must run before the modifier guard");

        // The handler is registered TUNNELLING on the window, so it sees the press whatever consumes
        // it afterwards.
        Assert.Contains("AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel)",
                        Code(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A running probe does not resize the specification pane</b> (owner: "if I click probe, the
    /// Group box rendering glitches out then returns to normal. Need no glitches").
    /// </summary>
    /// <remarks>
    /// The progress row was inside the pane's own stack, visible only while the probe ran — so every
    /// card under it slid down when the probe started and back up when it finished. It reports from
    /// the footer now: a fixed <c>Auto</c> row whose content is always present, so showing two lines
    /// and a Cancel button in it costs no layout at all.
    /// </remarks>
    [Fact]
    public void AProbeInProgress_ReportsFromTheFooter_NotFromInsideTheSpecificationPane()
    {
        string xaml = Xaml();

        // Exactly one thing keys off IsProbing, and it is in the footer.
        Assert.Single(Regex.Matches(xaml, @"IsVisible=""\{Binding IsProbing\}"""));

        int probing = xaml.IndexOf("IsVisible=\"{Binding IsProbing}\"", StringComparison.Ordinal);
        int footer  = xaml.IndexOf("<Grid Grid.Row=\"2\"", StringComparison.Ordinal);
        int spec    = xaml.IndexOf("Text=\"Specification\"", StringComparison.Ordinal);
        Assert.True(footer > 0 && spec > 0);
        Assert.True(probing > footer, "the probe progress is still inside the panes, not the footer");
        Assert.True(probing > spec);

        // Cancel travelled with it — a progress report with no way to stop it is worse than none.
        Assert.Contains("{Binding CancelProbeCommand}", xaml[footer..], StringComparison.Ordinal);
    }

    // ══ 12. The status strip told the truth in a way nobody could read ══════

    /// <summary>
    /// <b>"On target" is an engineering question, not a floating-point equality test</b> (owner:
    /// "I'm getting a warning that I can't match terminal 2, but it looks matched to 50 ohms to me").
    /// </summary>
    /// <remarks>
    /// The reported design was off by <b>2e-9</b> — two parts in a billion — against a tolerance of
    /// 1e-9, so the strip read "Π N² 10 / 10 ✘ not reached" and flagged termination 2 on a network
    /// that was matched. <c>Redistribute</c> reaches its target by a sequential pass of divides and
    /// clamps and <c>Π N²</c> squares every term, so a re-link after a dropped transform lands there
    /// routinely. The tolerance is one shared constant now, at a part per million — 4e-6 dB of
    /// impedance ratio, still six orders finer than anything measurable.
    /// </remarks>
    [Fact]
    public void OnTarget_IsAPartPerMillion_AndOneConstantSaysSoEverywhere()
    {
        Assert.Equal(1e-6, MatchLinkage.RatioTolerance, 15);
        Assert.Equal(MatchLinkage.RatioTolerance, MatchSolutionSearch.RatioTolerance, 15);

        // The three places that ask the question all read the one constant.
        foreach (var (file, name) in new[]
                 {
                     (Src("src", "Core", "Match", "MatchRebuild.cs"), "MatchRebuild"),
                     (Src("src", "Core", "Match", "MatchLinkage.cs"), "MatchLinkage"),
                     (Src("src", "Core", "Match", "MatchSolutionSearch.cs"), "MatchSolutionSearch"),
                 })
        {
            Assert.DoesNotContain("<= 1e-9", file, StringComparison.Ordinal);
            Assert.Contains("RatioTolerance", file, StringComparison.Ordinal);
            output.WriteLine($"{name}: reads the shared tolerance");
        }

        // A rack a part per billion off now reads as matched, and flags nothing.
        var (_, _, d) = Open();
        d.AddTransform(d.AvailablePairs()[0]);
        Assert.True(d.Rebuild!.OnTarget, d.Status.RatioText);
        Assert.False(d.Term1.IsFlagged);
        Assert.False(d.Term2.IsFlagged);
        Assert.Contains("✔", d.Status.RatioText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A cross never sits beside two numbers that look equal</b> — when the ratio is off, the
    /// strip says by how much.
    /// </summary>
    /// <remarks>
    /// Three decimals is the right density for the matched case and hid the entire disagreement in
    /// the unmatched one. Anything that now fails the tolerance is at least 0.0001 % off, which this
    /// line can show.
    ///
    /// <para><b>The rack is knocked off target by HAND, not by a termination edit</b> (rewritten
    /// 2026-08-28). This used to declare 5 Ω into 200 Ω and read the strip straight afterwards —
    /// which the termination auto-solve now answers by moving the design onto a solution that does
    /// reach it, so the strip correctly said ✔ and the test was asserting that a feature does not
    /// work. A transform edit reaches the same state and triggers nothing: it is a change to the rack
    /// the user made deliberately, which is exactly the case this line exists for.</para>
    /// </remarks>
    [Fact]
    public void WhenTheRatioIsNotReached_TheStripSaysByHowMuch()
    {
        var (_, _, d) = Open();
        d.SetTermination(1, Termination.Resistive(5.0));
        d.SetTermination(2, Termination.Resistive(200.0));
        d.WaitForAnalysis();

        Assert.NotEmpty(d.Solutions);
        d.ApplySolution(d.Solutions[0]);
        d.WaitForAnalysis();
        Assert.True(d.Status.OnTarget, "the fixture has to start matched for the knock to mean anything");

        // Link OFF, so moving one N is not compensated by the others.
        d.LinkTransforms = false;
        var row = d.Transforms[0];
        d.SetTransformN(0, (row.N + row.NMax) / 2);

        output.WriteLine(d.Status.RatioText);
        Assert.False(d.Status.OnTarget);
        Assert.Contains("✘", d.Status.RatioText, StringComparison.Ordinal);
        Assert.Contains("%", d.Status.RatioText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A refusal must not refute itself</b>: "reaches only 0 against the 0 needed" is not a reason.
    /// </summary>
    /// <remarks>
    /// <c>PrototypeSearchResult.MaxQFar</c> is 0 both when the family's best really was 0 and when
    /// NOTHING was evaluated — and against a purely resistive far end the required Q is 0 too, so
    /// the two zeros met and the sentence said nothing. The two cases are separate messages now,
    /// told apart by <c>AnyMember</c>.
    /// </remarks>
    [Fact]
    public void AnInfeasibleFamily_SaysWhichKindOfInfeasible()
    {
        var (_, _, d) = Open();
        d.SetTermination(2, Termination.Resistive(50.0));    // purely resistive far end: Q = 0
        d.WaitForAnalysis();

        foreach (var o in d.ResponseOptions.Where(o => !o.IsEnabled))
        {
            output.WriteLine(o.Tooltip);
            Assert.DoesNotContain("only 0 against the 0", o.Tooltip, StringComparison.Ordinal);
            Assert.NotEqual("", o.Tooltip);
        }
    }

    // ══ 11. Three more from the same round ══════════════════════════════════

    /// <summary>
    /// <b>Committing a value unchanged is not an edit, and says nothing</b> (owner: "if I double
    /// click on a component value in the LC ladder and then close the inline text editor without
    /// changing anything, I get an error 'L1_N1 cannot reach x pH…'. I shouldn't get an error or
    /// warning if I didn't change anything").
    /// </summary>
    /// <remarks>
    /// The editor seeds itself from the LABEL, and the label is rounded to the display's significant
    /// digits — so committing it unchanged asked the rack to reach the ROUNDED number exactly, which
    /// it truthfully cannot. The refusal was a correct answer to a question the user never asked. The
    /// guard compares at DISPLAY precision, which is the only precision the field has.
    /// </remarks>
    [Fact]
    public void CommittingTheValueTheFieldWasShowing_ChangesNothing_AndSaysNothing()
    {
        var (_, _, d) = Open();
        var element = d.Ladder.Elements.First();
        var target = d.ResolveInlineEdit(element.Name);
        Assert.NotNull(target);

        double before = target!.Current;
        string shown = element.ValueText;      // exactly what the canvas drew and the editor seeds
        output.WriteLine($"{element.Name}: shown '{shown}', stored {before:G17}");

        // The rounded text is NOT the stored value — which is why this used to refuse.
        Assert.False(d.CommitInlineEdit(target, shown));
        Assert.Equal("", d.InlineEditNote);
        Assert.Equal(before, d.Ladder.Elements.First().Value, 15);

        // …and the guard swallows nothing REAL: a different value still reaches the search, which
        // either moves the rack or says why it cannot. Asserted as "one of the two happened" rather
        // than as a moved value, because whether this particular fixture's rack can reach four times
        // L1 is a property of the fixture and not the thing under test.
        string other = MatchValueFormat.FormatWithUnit(
            before * 4, target.Quantity, target.Unit, d.Settings.SignificantDigits);
        Assert.NotEqual(shown, other);
        bool applied = d.CommitInlineEdit(target, other);
        Assert.True(applied || d.InlineEditNote.Length > 0,
                    "a genuinely different value was silently ignored");
    }

    /// <summary>
    /// <b>Series-versus-parallel is disabled when there is no reactance to arrange</b> (owner: "the
    /// series/parallel graphic indicator does not update when the selector is changed").
    /// </summary>
    /// <remarks>
    /// It does update — when there is something to draw. With <c>ReactanceKind.None</c> the pictogram
    /// is a resistor on its own and both topologies are the same picture, and nothing downstream
    /// distinguishes them either: <c>CeqAt</c> and <c>QAt</c> answer 0, and
    /// <c>MatchOrders.ValidOrders</c> short-circuits on <c>!HasReactance</c> before it compares the
    /// topologies at all. A selector that can be moved to no effect is the thing to fix.
    /// </remarks>
    [Fact]
    public void TheTopologySelector_IsDisabledWithNoReactance_AndMovesThePictogramWithOne()
    {
        var (_, _, d) = Open();

        Assert.False(d.Design.Term1.HasReactance);
        Assert.False(d.Term1.TopologyEnabled);
        Assert.Contains("no reactance", d.Term1.TopologyTooltip, StringComparison.OrdinalIgnoreCase);

        // With no reactance the two topologies ARE the same picture — asserted, so the claim above
        // is checked rather than merely stated.
        var asParallel = d.Term1.Pictogram;
        d.SetTermination(1, d.Design.Term1 with { Topology = TerminationTopology.Series });
        Assert.Equal(asParallel.Kind, d.Term1.Pictogram.Kind);

        // Give it a reactance and the selector comes alive and the pictogram follows it.
        d.SetTermination(
            1, new Termination(50, ReactanceKind.C, TerminationTopology.Parallel, 1e-12));
        Assert.True(d.Term1.TopologyEnabled);

        var before = d.Term1.Pictogram;
        d.Term1.Topology = TerminationTopology.Series;
        Assert.NotEqual(before, d.Term1.Pictogram);
        Assert.Equal(TerminationTopology.Series, d.Term1.Pictogram.Topology);
    }

    /// <summary>
    /// <b>Both trace kinds label in ONE transform language</b> (owner: "instead of dB(S(1,1)) it
    /// should say S(1,1) dB20. Don't hard code it in — have the plot render it just the way it's done
    /// in the data display so that they won't ever drift").
    /// </summary>
    /// <remarks>
    /// The Match Designer never labelled anything itself: its plots go through
    /// <c>TraceLabeler.ComputeMinimalLabels</c> and <c>AxesRenderer</c> like every other plot. What
    /// differed was the trace KIND — a network-bound trace read <c>ShortDescription</c>'s
    /// function-call form while a cube-bound one read the name-then-transform form. Both now end with
    /// the same suffix table, which is what makes drift impossible rather than merely unlikely.
    /// </remarks>
    [Fact]
    public void ThePlotsAxisLabels_UseTheDataDisplaysOwnTransformLanguage()
    {
        var (_, _, d) = Open();

        var labels = DataDisplay.TraceLabeler.ComputeMinimalLabels(d.MagnitudePlot.Traces);
        output.WriteLine(string.Join(" | ", labels));

        Assert.Equal(["S(1,1) dB20", "S(2,1) dB20"], labels);
        Assert.All(labels, l => Assert.DoesNotContain("dB(", l, StringComparison.Ordinal));

        // …and it is the SHARED labeler that says so, not a string built in this window.
        string vm = Src("src", "Ui", "Match", "MatchDesignerViewModel.Response.cs");
        Assert.DoesNotContain("dB20", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("YLabel", vm, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The two word-labelled rack buttons are glyphs now</b> (owner: "remove the word 'add' from the
    /// add transform button. Remove the word 'link' from the link button").
    /// </summary>
    [Fact]
    public void TheRackButtons_CarryNoWords()
    {
        string xaml = Xaml();

        int add = xaml.IndexOf("Name=\"AddTransformButton\"", StringComparison.Ordinal);
        Assert.True(add > 0);
        string addBlock = xaml[add..xaml.IndexOf("</Button>", add, StringComparison.Ordinal)];
        // The FACE, not the tooltip — the word that was removed is the one on the button, and the
        // tooltip is where it went, so a blanket "the word 'add' is absent" would be the wrong claim.
        Assert.Contains("Text=\"+\"", addBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("+ add", addBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Regex.Matches(addBlock, @"<TextBlock "));

        int link = xaml.IndexOf("IsChecked=\"{Binding LinkTransforms, Mode=TwoWay}\"",
                                StringComparison.Ordinal);
        Assert.True(link > 0);
        string linkBlock = xaml[link..xaml.IndexOf("</ToggleButton>", link, StringComparison.Ordinal)];
        Assert.DoesNotContain("Text=\"link\"", linkBlock, StringComparison.Ordinal);
        Assert.Contains("Kind=\"LinkVariant\"", linkBlock, StringComparison.Ordinal);
    }
}
