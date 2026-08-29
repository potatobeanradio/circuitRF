// ================================================================
//  MatchRound9Tests.cs  —  the owner's 2026-08-28 round-9 list for the Match Designer, which is one
//  cleanup pass over the Solutions panel that round 8 built plus the vertical space it needs:
//
//    * Applying a solution must not move the scroll view at all — only the green card border and the
//      button's own wording change.
//    * The frequency-response plots must actually redraw when a solution is applied.
//    * Page Up / Page Down / Home / End must work wherever the focus happens to be.
//    * Frequency Band and Ripple become ONE card of three rows, each Probe button moves onto its
//      termination's heading, and the height both changes free goes to the Solutions list.
//
//  Same discipline as rounds 1-8: view-model, geometry and source-scan tests, never pixels. Where the
//  ask is about layout declared in AXAML, or about a wiring path that needs a live Avalonia
//  application, the assertion is made against the source the mechanism is written in and NAMES that
//  mechanism — a scan for "the file mentions the word" would pass over a broken fix.
// ================================================================

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Input;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound9Tests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign? design = null)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        if (design is not null)
            comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);

        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    /// <summary>50 Ω into 5 Ω ∥ 1 pF over 1.8-2.2 GHz — a mixed pair, so orders 2, 4 and 6.</summary>
    private static MatchDesign Problem(int order = 4) => new()
    {
        F1 = 1.8e9, F2 = 2.2e9, Order = order, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
        Term2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Series, 2e-12),
    };

    /// <summary>
    /// The same pair with a purely resistive far end — <b>the one on which Bessel refuses at every
    /// order</b>, which is what the two auto-solve tests below need. The mixed-reactance
    /// <see cref="Problem"/> above does produce Bessel rows (carrying negative elements), so it
    /// cannot stand in for "a family with nothing to offer".
    /// </summary>
    private static MatchDesign RefusingFamilyProblem() => new()
    {
        F1 = 1.8e9, F2 = 2.2e9, Order = 4, Response = ResponseShape.Bessel,
        Term1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
        Term2 = Termination.Resistive(50.0),
    };

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
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);
        return raw;
    }

    private static string Xaml()     => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");
    private static string Code()     => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");
    private static string Response() => Src("src", "Ui", "Match", "MatchDesignerViewModel.Response.cs");
    private static string Network()  => Src("src", "Ui", "Match", "MatchDesignerViewModel.Network.cs");

    /// <summary>The body of the method whose signature line starts with <paramref name="signature"/>.</summary>
    private static string Between(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source");
        int open = src.IndexOf('{', i);
        int depth = 0;
        for (int j = open; j < src.Length; j++)
        {
            if (src[j] == '{') depth++;
            else if (src[j] == '}' && --depth == 0) return src[open..(j + 1)];
        }
        Assert.Fail($"'{signature}' has no closing brace");
        return "";
    }

    /// <summary>The opening tag of the element whose declaration contains <paramref name="marker"/>.</summary>
    private static string OpeningTag(string xaml, string marker)
    {
        int at = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0, $"'{marker}' is not in the AXAML");
        int start = xaml.LastIndexOf('<', at);
        int end = xaml.IndexOf('>', at);
        Assert.True(start >= 0 && end > start);
        return xaml[start..(end + 1)];
    }

    // ══ 1 — Apply must not move the scroll view ═════════════════════════════

    /// <summary>
    /// <b>Applying a solution scrolls the list by nothing at all</b> (owner-reported: applying one,
    /// by double-clicking a card or by its Apply button, nudges the scroll view; it should not move at
    /// all, and the only things that change are the applied card's green border and the button's own
    /// wording).
    /// </summary>
    /// <remarks>
    /// <b>Two independent framework defaults were moving it</b>, which is why the two gestures moved
    /// it by different amounts and why switching off either alone would have left the report standing:
    ///
    /// <list type="bullet">
    /// <item><c>SelectingItemsControl.AutoScrollToSelectedItem</c> defaults to TRUE. A double-click
    /// selects the card's <c>ListBoxItem</c> on the way through, and the ListBox then scrolls that
    /// whole item into view — a card half off the bottom edge slid up under the pointer.</item>
    /// <item><c>ScrollViewer.BringIntoViewOnFocusChange</c> defaults to TRUE. Clicking the Apply
    /// button focuses it, and the focus change asks the nearest scroller for the button's own
    /// rectangle.</item>
    /// </list>
    ///
    /// <para>Asserted on the ListBox's own opening tag rather than by searching the file, because
    /// "the file contains AutoScrollToSelectedItem" would be satisfied by the word appearing in a
    /// comment or on some other control.</para>
    /// </remarks>
    [Fact]
    public void ApplyingASolution_DoesNotScrollTheList()
    {
        string tag = OpeningTag(Xaml(), "ItemsSource=\"{Binding Solutions}\"");
        output.WriteLine(tag);

        Assert.Contains("<ListBox", tag, StringComparison.Ordinal);
        Assert.Contains("AutoScrollToSelectedItem=\"False\"", tag, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.BringIntoViewOnFocusChange=\"False\"", tag, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Neither Apply path asks for a scroll</b>, and the automatic one is still armed exactly
    /// once — so the only thing that moves the list after an apply is the user.
    /// </summary>
    /// <remarks>
    /// The two AXAML switches above stop the framework scrolling. This is the other half: no APPLY on
    /// our own side may scroll either. <c>ScrollToApplied</c> keeps exactly three callers — the
    /// header's target button, the once-per-list automatic scroll that fires when the list first has
    /// an applied row to show, and (owner-reported, 2026-08-28) the move the user did NOT make: an
    /// undo, a redo, a band-count change, the auto-solve. That third one cannot be an apply path,
    /// because the view-model suppresses its own event for the duration of a click
    /// (<c>ApplyingByClick</c>) — which is what the assertions below check rather than assume.
    /// </remarks>
    [Fact]
    public void NoApplyPath_ScrollsTheList()
    {
        string code = Code();

        // The one path an apply takes — the list's own selection, since the button and the
        // double-tap handler both went in this round (see TheCardIsTheButton below).
        Assert.DoesNotContain("ScrollTo", Between(code, "private void OnSolutionSelectionChanged("),
                              StringComparison.Ordinal);

        // Three callers, and only three: the button's wiring, the once-only arm, and the applied-row
        // move. Four occurrences, the fourth being the declaration itself.
        Assert.Equal(4, Regex.Matches(code, @"ScrollToApplied\(\)").Count);
        Assert.Contains("private void ScrollToApplied()", code, StringComparison.Ordinal);
        Assert.Contains("ScrollToApplied()", Between(code, "private void ScrollToAppliedOnce()"),
                        StringComparison.Ordinal);
        Assert.Contains("WireButton(\"ScrollToAppliedButton\"", code, StringComparison.Ordinal);
        Assert.Contains("ScrollToApplied();", Between(code, "private void OnAppliedSolutionMoved("),
                        StringComparison.Ordinal);

        // …and the third caller is not reachable from a click: the view-model refuses to report a
        // move it made on the user's behalf while applying the card they clicked.
        string vm = Src("src", "Ui", "Match", "MatchDesignerViewModel.Network.cs");
        Assert.Contains("ApplyingByClick = true;",
                        Between(vm, "public void ApplySolution(MatchSolutionRowViewModel row)"),
                        StringComparison.Ordinal);
        Assert.Contains("&& !ApplyingByClick",
                        Between(Src("src", "Ui", "Match", "MatchDesignerViewModel.Analysis.cs"),
                                "private void RebadgeSolutions()"),
                        StringComparison.Ordinal);

        // …and the once-only guard is still a guard.
        Assert.Contains("if (_scrolledToApplied", Between(code, "private void ScrollToAppliedOnce()"),
                        StringComparison.Ordinal);

        // The view-model half applies a design and nothing else.
        Assert.DoesNotContain("Scroll", Between(Network(), "public void ApplySolution("), StringComparison.Ordinal);
    }

    // ══ 2 — the plots redraw when a solution is applied ═════════════════════

    /// <summary>
    /// <b>Applying a solution repaints both plots</b> (owner-reported: applying one sometimes leaves
    /// the frequency-response plots looking unchanged, including across a move between two response
    /// families that should look nothing alike).
    /// </summary>
    /// <remarks>
    /// <b>"Sometimes" was the tell, and the cause was that nothing here ever invalidated the
    /// control.</b> The two plot MODELS are the same two objects for the window's whole life — a
    /// rebuild clears their <c>Traces</c> and refills them — so the <c>OnPropertyChanged</c> that
    /// followed re-pushed an unchanged reference through the binding, and <c>OnPlotChanged</c> only
    /// raises property changes on the container's own view-model. Neither is a repaint. The
    /// <c>PlotControl</c> redrew when something else happened to invalidate it: the pointer crossing
    /// it, a resize, the window being re-activated — which is exactly a change that appears
    /// "sometimes", and appears the moment the user moves the mouse to look closer.
    ///
    /// <para><c>PlotContainerViewModel.PlotNeedsRedraw</c> is the seam the Data Display already uses
    /// for this, and <c>WirePlotHost</c> already had it wired to <c>PlotControl.InvalidateVisual</c>;
    /// it simply had no caller on this side. Asserted by SUBSCRIBING to that event and applying a
    /// solution, which is the same thing the live control does.</para>
    /// </remarks>
    [Fact]
    public void ApplyingASolution_AsksBothPlotsToRepaint()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        int magnitude = 0, phase = 0;
        d.MagnitudeContainer.PlotNeedsRedraw += (_, _) => magnitude++;
        d.PhaseContainer.PlotNeedsRedraw     += (_, _) => phase++;

        d.AllSolutions.First(r => !r.IsCurrent).Apply();
        d.WaitForAnalysis();

        output.WriteLine($"repaint requests: magnitude {magnitude}, phase {phase}");
        Assert.True(magnitude > 0, "the magnitude plot was never asked to repaint");
        Assert.True(phase > 0, "the phase plot was never asked to repaint");

        d.Dispose();
    }

    /// <summary>
    /// <b>And the response behind the repaint really is a different one</b> — the owner's own case,
    /// a move between two response families at the same order.
    /// </summary>
    /// <remarks>
    /// The repaint is worth nothing if the model under it did not move, and a source scan cannot tell
    /// the difference. Two rows of the SAME order and DIFFERENT family are applied in turn and the
    /// resulting S11 is compared point for point: a family change has to move it somewhere in the
    /// band, or the list is offering two names for one network.
    /// </remarks>
    [Fact]
    public void TwoFamiliesAtOneOrder_PlotDifferentResponses()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var byFamily = d.AllSolutions
            .GroupBy(r => r.Order)
            .Select(g => g.GroupBy(r => r.Response).Where(f => f.Any()).ToList())
            .FirstOrDefault(g => g.Count >= 2);
        Assert.NotNull(byFamily);

        var a = byFamily![0].First();
        var b = byFamily[1].First();
        output.WriteLine($"{a.TitleText}  vs  {b.TitleText}");

        a.Apply();
        d.WaitForAnalysis();
        var first = d.ResponseSnp;
        Assert.NotNull(first);
        var s11A = first!.Frequencies.Select((_, i) => first.Matrices[i][0, 0]).ToArray();

        b.Apply();
        d.WaitForAnalysis();
        var second = d.ResponseSnp;
        Assert.NotNull(second);
        var s11B = second!.Frequencies.Select((_, i) => second.Matrices[i][0, 0]).ToArray();

        Assert.Equal(s11A.Length, s11B.Length);
        double worst = s11A.Zip(s11B, (x, y) => (x - y).Magnitude).Max();
        output.WriteLine($"largest |ΔS11| across the plotted band: {worst:G6}");
        Assert.True(worst > 1e-6, "the two families plotted the same response");

        d.Dispose();
    }

    /// <summary>
    /// <b>Every plot rebuild leaves by ONE exit</b>, so no path can announce two of the three things
    /// a rebuild owes the host.
    /// </summary>
    /// <remarks>
    /// The three are the info-box notification (the markers' traces have just been replaced), the two
    /// binding notifications, and the repaint. They were spelled out three times in three places and
    /// the repaint was in none of them — which is the shape of bug that survives being read. One
    /// method now carries all three and every exit calls it.
    /// </remarks>
    [Fact]
    public void ThePlotRebuild_AnnouncesItselfInOnePlace()
    {
        string src = Response();

        var announce = Between(src, "private void AnnounceRebuiltPlots()");
        Assert.Contains("MagnitudeContainer.OnPlotChanged", announce, StringComparison.Ordinal);
        Assert.Contains("PhaseContainer.OnPlotChanged", announce, StringComparison.Ordinal);
        Assert.Contains("MagnitudeContainer.RequestPlotRedraw();", announce, StringComparison.Ordinal);
        Assert.Contains("PhaseContainer.RequestPlotRedraw();", announce, StringComparison.Ordinal);

        // Nowhere else raises either notification — one exit, not four.
        Assert.Equal(1, Regex.Matches(src, @"MagnitudeContainer\.OnPlotChanged").Count);
        Assert.Equal(1, Regex.Matches(src, @"PhaseContainer\.OnPlotChanged").Count);
        Assert.Equal(1, Regex.Matches(src, @"RequestPlotRedraw\(\);\s*\n\s*PhaseContainer\.RequestPlotRedraw").Count);

        // …and both exits of the two rebuild paths take it.
        Assert.Contains("AnnounceRebuiltPlots();", Between(src, "public void UpdatePlots()"),
                        StringComparison.Ordinal);
        Assert.Contains("AnnounceRebuiltPlots();", Between(src, "private void BuildPlots()"),
                        StringComparison.Ordinal);

        // The host end of the seam is the one that turns it into a repaint, and it is still wired.
        Assert.Contains("container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();",
                        Code(), StringComparison.Ordinal);
    }

    // ══ 3 — Page Up / Page Down / Home / End ════════════════════════════════

    /// <summary>
    /// <b>The four scrolling keys work wherever the focus is</b> (owner-reported: Page Up, Page Down,
    /// Home and End are unreliable — the same keystroke sometimes moves the list and sometimes does
    /// nothing).
    /// </summary>
    /// <remarks>
    /// <b>Nothing bound them at all.</b> The only thing answering those keys was the <c>ListBox</c>'s
    /// own navigation, which runs on <c>KeyDown</c> and therefore only while the keyboard focus is
    /// already inside the list — so the same keystroke worked after a click on a card and did nothing
    /// after a wheel scroll, an edit in the specification pane, or a freshly opened window. The
    /// handler is now a TUNNEL handler on the window, which sees the key on its way down to whatever
    /// has focus.
    ///
    /// <para>The rule itself is <see cref="PanelScrollKeys"/>'s — the one the Project Tree, the
    /// Library palette and the .ctech editor's row lists already share — rather than a fourth copy of
    /// the same four-case switch.</para>
    /// </remarks>
    [Fact]
    public void TheFourScrollKeys_AreBound_FromTheWindow_ThroughTheSharedRule()
    {
        string code = Code();

        Assert.Contains("AddHandler(KeyDownEvent, OnPanelScrollKeyDown, RoutingStrategies.Tunnel);",
                        code, StringComparison.Ordinal);

        var handler = Between(code, "private void OnPanelScrollKeyDown(");
        Assert.Contains("PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox)", handler, StringComparison.Ordinal);
        Assert.Contains("PanelScrollKeys.Apply(action.Value", handler, StringComparison.Ordinal);
        Assert.Contains("ComboBox { IsDropDownOpen: true }", handler, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", handler, StringComparison.Ordinal);

        // The fallback target is the solutions list's own scroller, which is where the report was
        // aimed; a focused scroller wins when there is one.
        var target = Between(code, "private ScrollViewer? ScrollerForKeys()");
        Assert.Contains("_solutionsList", target, StringComparison.Ordinal);
        Assert.Contains("GetFocusedElement()", target, StringComparison.Ordinal);
        Assert.Contains("is ScrollViewer sv", target, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>And the shared rule is the one this window wants</b>: Page Up/Down always scroll, Home and
    /// End yield to a text field.
    /// </summary>
    /// <remarks>
    /// The specification pane and the transform rack are full of <c>InlineEditText</c>, each of which
    /// opens a real <c>TextBox</c> while it is being typed into — and Home there means "caret to the
    /// start of this field", not "top of the solutions list". Page Up/Down mean nothing to a
    /// single-line box, so they are free to take.
    /// </remarks>
    [Fact]
    public void TheSharedRule_YieldsHomeAndEndToAField_AndNeverThePageKeys()
    {
        Assert.Equal(PanelScrollAction.Home,     PanelScrollKeys.ActionFor(Key.Home, false));
        Assert.Equal(PanelScrollAction.End,      PanelScrollKeys.ActionFor(Key.End, false));
        Assert.Null(PanelScrollKeys.ActionFor(Key.Home, true));
        Assert.Null(PanelScrollKeys.ActionFor(Key.End, true));

        Assert.Equal(PanelScrollAction.PageUp,   PanelScrollKeys.ActionFor(Key.PageUp, true));
        Assert.Equal(PanelScrollAction.PageDown, PanelScrollKeys.ActionFor(Key.PageDown, true));

        Assert.Null(PanelScrollKeys.ActionFor(Key.Down, false));
    }

    // ══ 4 — the height the Solutions list was given ═════════════════════════

    /// <summary>
    /// <b>Frequency Band and Ripple are ONE card of three rows</b> (owner: the two groups are to
    /// become one, headed "Frequency Band &amp; Ripple", with three rows — f1, f2 and Ripple, dB).
    /// </summary>
    /// <remarks>
    /// Asserted by the three rows living inside ONE <c>Border.card</c>, not by the heading alone: a
    /// renamed heading over two cards would pass a heading check and fail the ask. The ripple row
    /// keeps everything it had — its own label, its own tooltip, the <c>RippleEnabled</c> dimming and
    /// the note that says which end is the reason.
    /// </remarks>
    [Fact]
    public void TheBandAndRipple_AreOneCardOfThreeRows()
    {
        string xaml = Xaml();

        Assert.Contains("Text=\"Frequency Band &amp; Ripple\"", xaml, StringComparison.Ordinal);
        // The two headings it replaces are gone as headings.
        Assert.DoesNotContain("cardhdr\" Text=\"Frequency Band\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("cardhdr\" Text=\"Ripple\"", xaml, StringComparison.Ordinal);

        // One card, from its heading to its closing Border, holds all three rows.
        int head = xaml.IndexOf("Text=\"Frequency Band &amp; Ripple\"", StringComparison.Ordinal);
        int end  = xaml.IndexOf("</Border>", head, StringComparison.Ordinal);
        Assert.True(end > head);
        string card = xaml[head..end];

        Assert.Contains("Text=\"f1\"", card, StringComparison.Ordinal);
        Assert.Contains("Text=\"f2\"", card, StringComparison.Ordinal);
        Assert.Contains("Text=\"Ripple, dB\"", card, StringComparison.Ordinal);
        Assert.Contains("{Binding F1Entry, Mode=TwoWay}", card, StringComparison.Ordinal);
        Assert.Contains("{Binding F2Entry, Mode=TwoWay}", card, StringComparison.Ordinal);
        Assert.Contains("{Binding RippleEntry, Mode=TwoWay}", card, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding RippleEnabled}\"", card, StringComparison.Ordinal);
        Assert.Contains("{Binding RippleNote}", card, StringComparison.Ordinal);

        // No second card opens between the heading and those rows.
        Assert.DoesNotContain("Classes=\"card\"", card, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Each Probe button sits beside its termination's heading</b> (owner: each button had a row of
    /// its own, and the two termination cards were asked to give some of that vertical height back —
    /// with the right of the "Termination &lt;x&gt;" heading suggested as where the button could go).
    /// </summary>
    /// <remarks>
    /// ONE declaration, because the two terminations are one shared <c>DataTemplate</c> — which is
    /// also why the button cannot simply be checked for existence twice. What is asserted is that the
    /// button is inside the heading's own <c>Grid</c>, above the pictogram row that follows it, and
    /// that it kept the command, the enablement and the tooltip that say which of match.md §10.4's
    /// reasons a disabled one is.
    /// </remarks>
    [Fact]
    public void EachProbeButton_IsOnItsTerminationsHeadingRow()
    {
        string xaml = Xaml();

        int header = xaml.IndexOf("Text=\"{Binding Header}\"", StringComparison.Ordinal);
        int probe  = xaml.IndexOf("Command=\"{Binding ProbeCommand}\"", StringComparison.Ordinal);
        int grid   = xaml.IndexOf("</Grid>", header, StringComparison.Ordinal);
        int picto  = xaml.IndexOf("<mv:MatchPictogramControl", StringComparison.Ordinal);

        Assert.True(header > 0 && probe > header, "the Probe button is not after the card heading");
        Assert.True(probe < grid, "the Probe button is not inside the heading's own Grid");
        Assert.True(grid < picto, "the heading Grid does not close before the pictogram row");

        // One button, one template, and it kept everything that made it usable.
        Assert.Equal(1, Regex.Matches(xaml, @"Command=""\{Binding ProbeCommand\}""").Count);
        string tag = OpeningTag(xaml, "Command=\"{Binding ProbeCommand}\"");
        Assert.Contains("Content=\"Probe\"", tag, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanProbe}\"", tag, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding ProbeTooltip}\"", tag, StringComparison.Ordinal);

        // The two things the probe has to SAY stay at the foot of the card, where a card's own
        // messages belong — and both are empty until there is something to report.
        Assert.Contains("{Binding ProbeError}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ProbeFlag}", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The height those two changes freed went to the Solutions list</b> (owner: the Solutions
    /// listing needs more vertical space, and the height the two changes above free is to go to it).
    /// </summary>
    /// <remarks>
    /// The specification cards are an <c>Auto</c> row over the list's <c>*</c> row, so the cap on the
    /// cards' scroller IS the floor under the list — an Auto row takes what it wants first. Lowering
    /// the cap is therefore the whole mechanism.
    ///
    /// <para><b>The cap is no longer a fixed number, and this test moved with it</b> (owner,
    /// 2026-08-28: a Dual or Tri specification is to expand minimally, at this list's expense, so
    /// that every band edge is readable without scrolling). What the AXAML declares is now the
    /// pane's RESTING cap — what it shows until the first layout pass reaches
    /// <c>SyncSpecificationCap</c> — and the number that decides how much reaches the list is
    /// <c>SolutionsFloor</c>, which the method keeps below the specification whatever the band count
    /// asks for. Both are asserted, because it is still the pair that makes the owner's earlier ask
    /// true: the specification cannot take the whole pane, and what it does not take is the list's.
    /// The shape of the computation is held by
    /// <c>MatchMultibandDesignerTests.TheSpecificationPane_TakesItsCapFromThePaneHeight…</c>.</para>
    /// </remarks>
    [Fact]
    public void TheSpecificationCards_GaveTheirFreedHeightToTheSolutionsList()
    {
        var cap = Regex.Match(Xaml(), @"<ScrollViewer Grid\.Row=""1"" [^>]*MaxHeight=""(\d+)""");
        Assert.True(cap.Success, "the specification pane's scroller no longer declares a MaxHeight");

        double max = double.Parse(cap.Groups[1].Value, CultureInfo.InvariantCulture);
        output.WriteLine($"specification cards rest at {max} px");
        Assert.True(max < 392, $"the cap is still {max} — the freed height did not reach the list");
        Assert.InRange(max, 240, 340);

        // And the list keeps a floor of its own once the cap is being computed, or a tri-band
        // specification would grow until there was no list under it at all.
        var floor = Regex.Match(Code(), @"SolutionsFloor = (\d+);");
        Assert.True(floor.Success, "nothing reserves any height for the Solutions list");
        Assert.InRange(double.Parse(floor.Groups[1].Value, CultureInfo.InvariantCulture), 100, 260);
    }
    // ══ 5 — the card IS the button ══════════════════════════════════════════

    /// <summary>
    /// <b>The Apply / Applied button is gone and a click on the card applies it</b> (owner,
    /// 2026-08-28), <b>with the arrow keys stepping between solutions</b>.
    /// </summary>
    /// <remarks>
    /// The two asks are one mechanism, which is the point of the shape chosen: the apply hangs off
    /// the list's SELECTION rather than off a pointer, and a <c>ListBox</c> already turns both a
    /// click and an arrow key into a selection. A Click handler on the card would have satisfied the
    /// first ask and left the second needing its own implementation to diverge from.
    /// </remarks>
    [Fact]
    public void TheCardIsTheButton_AndSelectingARowAppliesIt()
    {
        string xaml = Xaml();
        string code = Code();

        // Nothing in the card template applies anything any more — no button, no handler, no
        // ApplyText for one to read.
        int list = xaml.IndexOf("ItemsSource=\"{Binding Solutions}\"", StringComparison.Ordinal);
        int end  = xaml.IndexOf("</ListBox>", list, StringComparison.Ordinal);
        Assert.True(list > 0 && end > list);
        string template = xaml[list..end];

        Assert.DoesNotContain("<Button", template, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleTapped", template, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyText", Src("src", "Ui", "Match", "MatchSolutionRowViewModel.cs"),
                              StringComparison.Ordinal);

        // The card says it can be clicked, now that nothing on it looks like a control.
        Assert.Contains("Cursor=\"Hand\"", template, StringComparison.Ordinal);

        // Selection is the one path, and it guards against re-entering itself — Apply re-badges every
        // row and the sync writes SelectedItem straight back, so both raise the event again.
        var sel = Between(code, "private void OnSolutionSelectionChanged(");
        Assert.Contains("row.Apply();", sel, StringComparison.Ordinal);
        Assert.Contains("_syncingSolutionSelection", sel, StringComparison.Ordinal);
        Assert.Contains("row.IsCurrent", sel, StringComparison.Ordinal);
        Assert.Contains("_solutionsList.SelectionChanged += OnSolutionSelectionChanged;",
                        code, StringComparison.Ordinal);

        // …and the row's own entry point is unchanged, so what a selection reaches is what the button
        // reached.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        var pick = d.Solutions.First(r => !r.IsCurrent);
        pick.Apply();
        d.WaitForAnalysis();
        Assert.True(pick.IsCurrent);
        d.Dispose();
    }

    /// <summary>
    /// <b>Up and Down step the solutions — unless a marker is selected, and then they are the
    /// marker's</b> (owner, 2026-08-28, who named the conflict in the same breath as asking for the
    /// gesture and said the marker wins).
    /// </summary>
    /// <remarks>
    /// A selected marker steps by one x-axis sample on Up/Down, from <c>PlotControl.OnKeyDown</c> and
    /// <c>MarkerInfoBoxView.OnKeyDown</c> — both BUBBLING handlers, so the window's tunnel handler
    /// would otherwise take the key before either ever saw it. The yield is asserted to use the same
    /// "is a marker selected" test <c>DeleteSelectedMarkers</c> in this window already uses, because
    /// two gestures disagreeing about what a selected marker is would be its own bug.
    ///
    /// <para>The three controls that own their own arrows are named as well. This is a tunnel
    /// handler: without excluding them it would silently take Up/Down off every slider in the
    /// transform rack, which is how an N is nudged.</para>
    /// </remarks>
    [Fact]
    public void TheArrowKeys_StepTheSolutions_AndYieldToASelectedMarker()
    {
        string code = Code();

        var handler = Between(code, "private void OnPanelScrollKeyDown(");
        Assert.Contains("Key.Up or Key.Down", handler, StringComparison.Ordinal);
        Assert.Contains("SolutionsTakeTheArrowKeys(e.Source)", handler, StringComparison.Ordinal);
        Assert.Contains("MoveSolutionSelection(e.Key == Key.Up ? -1 : +1)", handler, StringComparison.Ordinal);

        var yields = Between(code, "private bool SolutionsTakeTheArrowKeys(");
        Assert.Contains("MarkerInfoBoxes.Any(b => b.IsSelected)", yields, StringComparison.Ordinal);
        Assert.Contains("TextBox or Slider or ComboBox", yields, StringComparison.Ordinal);

        // The same test the window's own Delete gesture uses for "a marker is selected".
        Assert.Contains("MarkerInfoBoxes.Where(b => b.IsSelected)",
                        Src("src", "Ui", "Match", "MatchDesignerViewModel.Response.cs"),
                        StringComparison.Ordinal);

        // Moving the selection scrolls EXPLICITLY, which is the whole reason the list's own
        // auto-scroll is off: a click must not move the viewport and an arrow key must.
        var move = Between(code, "private bool MoveSolutionSelection(");
        Assert.Contains("_solutionsList.SelectedIndex = to;", move, StringComparison.Ordinal);
        Assert.Contains("ScrollIntoView(to)", move, StringComparison.Ordinal);
    }

    // ══ 6 — one gesture, one undo entry ═════════════════════════════════════

    /// <summary>
    /// <b>A termination edit is ONE undo entry</b> (owner, 2026-08-28), even though it writes the
    /// design twice.
    /// </summary>
    /// <remarks>
    /// The second write is the auto-solve moving the design onto a rack that reaches the new target.
    /// Two commits was two <c>SetParametersCommand</c>s, so one Ctrl+Z put the transforms back and
    /// left the termination where the user had typed it — halfway through an edit they made once.
    ///
    /// <para><b>And the number of entries was NONDETERMINISTIC</b>, which is how this was found:
    /// whether the auto-solve had run by the time the gesture returned depended on whether the
    /// background search had finished, so the same edit was one entry under load and two on an idle
    /// machine. <c>MatchRound5Tests.ATermGEdit_WritesTheSpecificationsOwnR_AndRefusesAComplexValue</c>
    /// failed in ISOLATION and passed in a full run for exactly that reason.</para>
    ///
    /// <para>Asserted by UNDOING, not by counting: what the owner asked for is that one Ctrl+Z puts
    /// the design back the way it was, and a count could be right while the entries spanned the wrong
    /// states.</para>
    /// </remarks>
    [Fact]
    public void ATerminationEdit_IsOneUndoEntry()
    {
        var (vm, _, d) = Open(Problem());
        d.WaitForAnalysis();

        double r0 = d.Design.Term2.R;
        var racks0 = d.Design.Transforms.Select(t => (t.ElementA, t.ElementB, t.Form, t.N)).ToList();

        d.SetTermination(2, d.Design.Term2 with { R = 75.0 });
        d.WaitForAnalysis();
        Assert.Equal(75.0, d.Design.Term2.R, 9);

        vm.UndoRedo.Undo();
        Assert.Equal(r0, d.Design.Term2.R, 9);

        // …and the rack the auto-solve may have moved came back with it, in the same step.
        var racks1 = d.Design.Transforms.Select(t => (t.ElementA, t.ElementB, t.Form, t.N)).ToList();
        Assert.Equal(racks0.Count, racks1.Count);
        for (int i = 0; i < racks0.Count; i++)
        {
            Assert.Equal(racks0[i].ElementA, racks1[i].ElementA);
            Assert.Equal(racks0[i].ElementB, racks1[i].ElementB);
            Assert.Equal(racks0[i].Form, racks1[i].Form);
            Assert.Equal(racks0[i].N, racks1[i].N, 9);
        }

        output.WriteLine($"one entry: R {r0} -> 75 -> {d.Design.Term2.R}");
        d.Dispose();
    }

    /// <summary>
    /// <b>Both halves of the mechanism</b>, because the search can land on either side of the gesture
    /// returning and only one of the two covers each case.
    /// </summary>
    /// <remarks>
    /// A synchronous landing runs the auto-solve INSIDE the gesture, before its own commit — deferring
    /// every commit for the duration and making one at the end collapses that, and it absorbs
    /// <c>RelinkAfterSpecChange</c>'s commit on the way. A later landing has nothing left to defer, so
    /// the entry is remembered by its undo STAMP and the auto-solve's commit amends it. The stamp,
    /// rather than a flag, is what makes the amend safe: this window is not modal, and a schematic
    /// edit between the two commits must not be the thing that gets undone.
    /// </remarks>
    [Fact]
    public void TheOneEntry_IsBuiltTwoWays_AndTheAmendIsGuarded()
    {
        string src = Src("src", "Ui", "Match", "MatchDesignerViewModel.cs");

        // The block lives in CommitSpecChangeWithAutoSolve now, because a SECOND edit needs it: a
        // band-count change rebuilds the ladder underneath the stored transform records exactly as a
        // topology change does, and it asks for the same auto-solve (owner-reported, 2026-08-28).
        Assert.Contains("CommitSpecChangeWithAutoSolve();",
                        Between(src, "internal void SetTermination("), StringComparison.Ordinal);
        Assert.Contains("CommitSpecChangeWithAutoSolve();",
                        Between(src, "public int BandCount"), StringComparison.Ordinal);

        var set = Between(src, "private void CommitSpecChangeWithAutoSolve()");
        Assert.Contains("_commitSuppressed++", set, StringComparison.Ordinal);
        Assert.Contains("_commitDeferred", set, StringComparison.Ordinal);
        Assert.Contains("AsOneEdit(() =>", set, StringComparison.Ordinal);
        Assert.Contains("lock (RefreshGate) edit();",
                        Between(src, "private void AsOneEdit(Action edit)"), StringComparison.Ordinal);

        var commit = Between(src, "private void CommitCore(long amendStamp)");
        Assert.Contains("if (_commitSuppressed > 0)", commit, StringComparison.Ordinal);
        // The stamp is recorded AT THE PUSH, not at the end of the gesture: the gesture does not know
        // which of its writes ends up on the stack, and reading TopUndoStamp afterwards raced the
        // landing on a host whose result scheduler is the thread pool.
        Assert.Contains("if (_pendingAutoSolve is not null)", commit, StringComparison.Ordinal);
        Assert.Contains("_autoSolveCommitStamp = _schematicVm.UndoRedo.TopUndoStamp;", commit,
                        StringComparison.Ordinal);
        Assert.Contains("_schematicVm.UndoRedo.TopUndoStamp == amendStamp", commit, StringComparison.Ordinal);
        Assert.Contains("_schematicVm.UndoRedo.Undo();", commit, StringComparison.Ordinal);

        // The undo is inside the guard that stops this Designer re-reading a design it is writing.
        int guard = commit.IndexOf("_isCommitting = true;", StringComparison.Ordinal);
        int undo  = commit.IndexOf("UndoRedo.Undo();", StringComparison.Ordinal);
        Assert.True(guard >= 0 && undo > guard, "the amend's undo is outside the _isCommitting guard");

        // The auto-solve is the only caller, and it clears the stamp as it takes it.
        var auto = Between(Src("src", "Ui", "Match", "MatchDesignerViewModel.Analysis.cs"),
                           "private bool AutoApplyAReachingSolution(");
        Assert.Contains("_autoSolveCommitStamp = 0;", auto, StringComparison.Ordinal);
        Assert.Contains("ApplySolution(chosen, amend)", auto, StringComparison.Ordinal);
    }

    // ══ 7 — the group delay's units ═════════════════════════════════════════

    /// <summary>
    /// <b>The right-hand y-axis names the group delay's units</b> (owner, 2026-08-28).
    /// </summary>
    /// <remarks>
    /// On the trace's <c>CubeName</c> rather than as a custom axis label, because that is the one
    /// string the axis label, the marker readout and the info box all derive from
    /// (<c>TraceLabeler.BuildCubeQuantity</c>): a custom Y2 label would have put the unit on the axis
    /// and left the marker beside it reading a bare number. And it is read from the application's own
    /// derived-parameter table, so the Designer's group delay and the Data Display's are not two
    /// differently-spelled quantities.
    /// </remarks>
    [Fact]
    public void TheGroupDelayTrace_IsNamedWithItsUnit()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var delay = d.PhasePlot.Traces.FirstOrDefault(t => t.UseSecondaryAxis);
        Assert.NotNull(delay);
        output.WriteLine(delay!.CubeName ?? "(null)");

        Assert.Contains("(ns)", delay.CubeName!, StringComparison.Ordinal);
        Assert.Equal(DerivedParameters.GroupDelay.Description(), delay.CubeName);
        Assert.Equal(MatchDesignerViewModel.TraceGroupDelayName, delay.CubeName);

        // The label the axis actually renders comes through the shared labeller, so it carries it too.
        Assert.Contains("(ns)", TraceLabeler.QuantityFor(delay), StringComparison.Ordinal);
        d.Dispose();
    }

    // ══ 8 — a pi/T switch does not re-frame the drawing ═════════════════════

    /// <summary>
    /// <b>Switching a transform between its pi and T equivalents leaves the schematic view where the
    /// user put it</b> (owner-reported, 2026-08-28: it appeared to autoscale).
    /// </summary>
    /// <remarks>
    /// <b>Measured, and it is why the old test was the wrong one.</b> pi and T produce the same
    /// element COUNT, the same names, the same x positions and a bounding box identical to the last
    /// decimal — three elements simply move between the series rail and a shunt arm. The canvas
    /// re-fitted whenever the SHAPE changed, and a topology change is a change of shape that is no
    /// change at all to what the frame has to contain, so the user's zoom was thrown away for a
    /// redraw occupying exactly the same pixels.
    ///
    /// <para>The extent is the honest question and it is also the one the canvas's own comment was
    /// already trying to ask. Both halves are asserted: that the extent really is identical across the
    /// switch, and that the canvas gates on the extent rather than on the elements.</para>
    /// </remarks>
    [Fact]
    public void APiToTSwitch_DrawsIntoTheSameRectangle_AndTheCanvasGatesOnThat()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var row = d.AllSolutions.First(r => r.Solution.Transforms.Count > 0);
        row.Apply();
        d.WaitForAnalysis();

        var before = MatchSchematicModel.Build(d.Ladder);
        var was = d.Design.Transforms[0].Form;
        d.SetTransformForm(0, was == TransformForm.Pi ? TransformForm.T : TransformForm.Pi);
        d.WaitForAnalysis();
        var after = MatchSchematicModel.Build(d.Ladder);

        output.WriteLine($"{was}: ({before.BbMinX},{before.BbMinY})..({before.BbMaxX},{before.BbMaxY})");
        output.WriteLine($"then: ({after.BbMinX},{after.BbMinY})..({after.BbMaxX},{after.BbMaxY})");

        Assert.Equal(before.BbMinX, after.BbMinX, 6);
        Assert.Equal(before.BbMaxX, after.BbMaxX, 6);
        Assert.Equal(before.BbMinY, after.BbMinY, 6);
        Assert.Equal(before.BbMaxY, after.BbMaxY, 6);

        // …and the drawing really did change, or the paragraph above is about nothing.
        Assert.NotEqual(was, d.Design.Transforms[0].Form);

        // The canvas gates the re-fit on that rectangle, and no longer on the elements themselves.
        string canvas = Src("src", "Ui", "Views", "Match", "MatchSchematicCanvas.cs");
        Assert.Contains("bool sameExtent = SameExtent(_model, nextModel);", canvas, StringComparison.Ordinal);
        Assert.Contains("if (!sameExtent) _fitted = false;", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("SameShape", canvas, StringComparison.Ordinal);
        d.Dispose();
    }

    // ══ 9 — the title strip, and the dot that left it ═══════════════════════

    /// <summary>
    /// <b>Undo and Redo are on the title strip, left of Settings</b> (owner, 2026-08-28).
    /// </summary>
    /// <remarks>
    /// The commands are not new: <c>MatchDesignerViewModel</c>'s have always delegated to the OWNING
    /// SCHEMATIC's stack, because a Designer edit is a schematic edit and a second history beside it
    /// would be two answers to "what did I just do". What was missing was somewhere to aim a pointer.
    /// Asserted to bind those commands rather than to do anything of their own — a button that undid
    /// something local would be the bug this names.
    /// </remarks>
    [Fact]
    public void TheTitleStrip_CarriesUndoAndRedo_LeftOfSettings()
    {
        string xaml = Xaml();

        int undo = xaml.IndexOf("Name=\"UndoButton\"", StringComparison.Ordinal);
        int redo = xaml.IndexOf("Name=\"RedoButton\"", StringComparison.Ordinal);
        int settings = xaml.IndexOf("Name=\"SettingsButton\"", StringComparison.Ordinal);
        Assert.True(undo > 0, "there is no Undo button");
        Assert.True(undo < redo && redo < settings, "Undo/Redo are not left of Settings");

        string block = xaml[undo..settings];
        Assert.Contains("Command=\"{Binding UndoCommand}\"", block, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RedoCommand}\"", block, StringComparison.Ordinal);
        Assert.Contains("<mi:MaterialIcon", block, StringComparison.Ordinal);

        // The tooltips NAME the entry, from the schematic stack's own description — so this window
        // and the application's Edit menu say the same words about the same entry.
        var (vm, _, d) = Open(Problem());
        Assert.Equal("Nothing to undo", d.UndoTooltip);
        d.SetTermination(2, d.Design.Term2 with { R = 75.0 });
        d.WaitForAnalysis();
        Assert.Equal(vm.UndoRedo.UndoDescription, d.UndoTooltip);
        Assert.True(d.UndoCommand.CanExecute(null));
        d.Dispose();
    }

    /// <summary>
    /// <b>No dot beside the filter button</b> (owner, 2026-08-28: it was distracting).
    /// </summary>
    /// <remarks>
    /// It was lit on almost every design, because the DEFAULT filter hides negative-component
    /// solutions — a mark that is always on marks nothing, and a warning colour for a normal state is
    /// worse than no mark at all. What it was FOR is still done, in words: the button's tooltip is
    /// the filter's own summary of what it is hiding. The flag behind it stays for that reason and is
    /// asserted to still answer, so this is a rendering change and not a loss of the distinction.
    /// </remarks>
    [Fact]
    public void TheFilterButton_CarriesNoDot_AndSaysItInWordsInstead()
    {
        string xaml = Xaml();

        int filter = xaml.IndexOf("Name=\"SolutionsFilterButton\"", StringComparison.Ordinal);
        Assert.True(filter > 0);
        string block = xaml[filter..xaml.IndexOf("</Button>", filter, StringComparison.Ordinal)];

        Assert.DoesNotContain("<Ellipse", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Filter.IsNarrowed", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding Filter.Summary}\"", block, StringComparison.Ordinal);

        // The distinction the dot made is still made, and still available to anyone who needs it.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.True(d.Filter.IsNarrowed);                       // negative components hidden by default
        Assert.Contains("hiding", d.Filter.Summary, StringComparison.Ordinal);
        d.Dispose();
    }

    // ══ 10 — the Probe button ═══════════════════════════════════════════════

    /// <summary>
    /// <b>The Probe button hugs the card's right edge, and its label is centred in it</b> (owner,
    /// 2026-08-28).
    /// </summary>
    /// <remarks>
    /// <b>The centring is fixed GLOBALLY and deliberately so.</b> Avalonia's <c>ContentControl</c>
    /// default is Stretch on both axes, so a Button taller than its label renders the text against
    /// the top edge — which is the same bug the repo's own Button style already fixes on the
    /// HORIZONTAL axis, with a comment saying in as many words that fixing it button-by-button is how
    /// it comes back. So the vertical setter went beside it, and this test asserts it there rather
    /// than on this one button.
    ///
    /// <para>The position is a column move: the button is the last-but-one column of the heading row
    /// with a <c>*</c> in front of it, which puts it over the same right edge the R and X value fields
    /// below run to — those being the <c>*</c> column of their own rows.</para>
    /// </remarks>
    [Fact]
    public void TheProbeButton_HugsTheRightEdge_AndItsLabelIsCentred()
    {
        string styles = Src("src", "Ui", "Styles", "CircuitRfStyles.axaml");
        int button = styles.IndexOf("<Style Selector=\"Button\">", StringComparison.Ordinal);
        Assert.True(button > 0, "the global Button style is gone");
        string style = styles[button..styles.IndexOf("</Style>", button, StringComparison.Ordinal)];
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Center\"/>",
                        style, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"VerticalContentAlignment\" Value=\"Center\"/>",
                        style, StringComparison.Ordinal);

        string xaml = Xaml();
        string tag = OpeningTag(xaml, "Command=\"{Binding ProbeCommand}\"");
        output.WriteLine(tag);

        // No fixed Height — that is what left the label against the top edge, and a button sized by
        // its own padding cannot raise the question again.
        // MinHeight is not a Height, hence the word boundary.
        Assert.False(Regex.IsMatch(tag, @"(?<![A-Za-z])Height="""),
                     "the Probe button still fixes its height");

        // Pushed right by a star column in front of it, inside the heading row.
        int header = xaml.IndexOf("Text=\"{Binding Header}\"", StringComparison.Ordinal);
        string row = xaml[xaml.LastIndexOf("<Grid ", header, StringComparison.Ordinal)
                          ..xaml.IndexOf("</Grid>", header, StringComparison.Ordinal)];
        Assert.Contains("ColumnDefinitions=\"Auto,*,Auto,Auto\"", row, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", tag, StringComparison.Ordinal);
    }

    // ══ 11 — the auto-solve must not leave a mismatch on screen ═════════════

    /// <summary>
    /// <b>A termination edit that its own family cannot answer moves onto one that can</b>
    /// (owner-reported, 2026-08-28: adding reactance left the termination target unmet and the design
    /// on its old rack, even though a solution existed).
    /// </summary>
    /// <remarks>
    /// <b>TWO bugs, and each alone would have kept the report standing.</b>
    ///
    /// <para><b>1. <c>OnTarget</c> is not "matched".</b> It compares Π N² against the ratio the rack
    /// has to reach — and a design whose synthesis REFUSED has no ladder and no transforms, so both
    /// sides are 1, the comparison passes, and the auto-solve returned early on the one state that
    /// most needed re-solving. Measured on this fixture before the fix: <c>Achieved = Required = 1</c>,
    /// <c>OnTarget = true</c>, and termination 2 flagged red on screen. The question asked now is the
    /// one the window is showing: is there a network, was it built without a refusal, and does it
    /// reach.</para>
    ///
    /// <para><b>2. The request was spent on the first cell to land.</b> The design's own combination
    /// is searched FIRST, precisely so an auto-solve can fire early — but that cell is exactly the one
    /// that comes back EMPTY when the family refuses. The request was consumed there, against a list
    /// of nothing, and never retried as the cells that did have solutions landed behind it. It is
    /// offered every cell now and kept until something answers it, with the end of the search as the
    /// last chance.</para>
    ///
    /// <para>And the candidate set had to widen, or there would still have been nothing to move onto:
    /// see <see cref="TheCandidates_WidenPastTheDesignsOwnFamily_NearestFirst"/>.</para>
    /// </remarks>
    [Fact]
    public void ATerminationEditItsOwnFamilyCannotAnswer_StillEndsMatched()
    {
        var (_, _, d) = Open(RefusingFamilyProblem());   // Bessel refuses this pair at every order
        d.LinkTransforms = false;
        d.WaitForAnalysis();
        Assert.DoesNotContain(d.AllSolutions, r => r.Response == ResponseShape.Bessel);

        // The state the guard used to read as "matched": no network, no transforms, so Π N² and the
        // ratio it must reach are both 1.
        Assert.True(d.Rebuild!.OnTarget, "the fixture no longer reproduces the guard's blind spot");
        Assert.NotNull(d.Rebuild.Refusal);

        d.Term2.Resistance = 75.0;
        d.WaitForAnalysis();

        output.WriteLine($"{d.Design.Response} order {d.Design.Order} — "
                         + $"Π N² {d.Rebuild!.Achieved:0.####} of {d.Rebuild.Required:0.####}");
        Assert.Null(d.Rebuild.Refusal);
        Assert.NotNull(d.Rebuild.Network);
        Assert.True(d.Rebuild.OnTarget, "left off target");
        Assert.False(d.Term1.IsFlagged);
        Assert.False(d.Term2.IsFlagged);
        d.Dispose();
    }

    /// <summary>
    /// <b>A reactance edit is the edit the owner reported</b>, and it gets the same treatment as a
    /// resistance edit — including from a probe, which writes its termination through the same door.
    /// </summary>
    [Fact]
    public void AReactanceEdit_AndAProbedTermination_BothEndMatched()
    {
        var (_, _, d) = Open(RefusingFamilyProblem());
        d.LinkTransforms = false;
        d.WaitForAnalysis();

        d.Term1.Reactance = 4e-12;                       // 1 pF → 4 pF
        d.WaitForAnalysis();
        Assert.Equal(4e-12, d.Design.Term1.Value, 15);
        Assert.True(d.Rebuild!.OnTarget && d.Rebuild.Refusal is null, "a reactance edit left a mismatch");

        // A probe writes through ApplyProbedTermination -> SetTermination, so it arms the same
        // auto-solve; that shared door is what makes "or probes to new termination" free.
        Assert.Contains("SetTermination(end, probed, fromProbe: true)",
                        Src("src", "Ui", "Match", "MatchDesignerViewModel.Probe.cs"),
                        StringComparison.Ordinal);

        d.ApplyProbedTermination(2, new Termination(
            12.5, ReactanceKind.L, TerminationTopology.Series, 2e-9, Probed: true,
            ProbedAtUtc: DateTime.UtcNow));
        d.WaitForAnalysis();

        output.WriteLine($"{d.Design.Response} order {d.Design.Order} — "
                         + $"Π N² {d.Rebuild!.Achieved:0.####} of {d.Rebuild.Required:0.####}");
        Assert.True(d.Rebuild.OnTarget && d.Rebuild.Refusal is null, "a probed termination left a mismatch");
        Assert.True(d.Term1.IsProbed || d.Term2.IsProbed);
        d.Dispose();
    }

    /// <summary>
    /// <b>The candidates widen past the design's own family — but nearest first</b>, so what the user
    /// chose is given up only as far as it has to be.
    /// </summary>
    /// <remarks>
    /// Three tiers, and the ORDER of them is the whole design: own order and family (nothing the user
    /// picked moves, and this answers almost every edit); then own order in any family, because the
    /// order is the more structural of the two — it is how many elements the network has; then the
    /// nearest order. <c>OrderBy</c> is stable, so within one order the rows keep MN-1's own ranking.
    ///
    /// <para>And a negative element is still not moved onto silently: the design's own
    /// <c>AllowNegativeComponents</c> is what says whether this user has asked for such a network,
    /// even though the search finds them in every cell.</para>
    /// </remarks>
    [Fact]
    public void TheCandidates_WidenPastTheDesignsOwnFamily_NearestFirst()
    {
        var pick = Between(Src("src", "Ui", "Match", "MatchDesignerViewModel.Analysis.cs"),
                           "private MatchSolutionRowViewModel? NearestReachingSolution()");

        // The FILTERED list, and only it (owner, 2026-08-28) — see
        // TheCandidates_AreTheFilteredListAndOnlyIt below for why that also retires the separate
        // negative-element guard.
        Assert.Contains("var candidates = Solutions.ToList();", pick, StringComparison.Ordinal);
        Assert.Contains("IsSameRack(r.Solution, _design)", pick, StringComparison.Ordinal);

        int own    = pick.IndexOf("r.Order == _design.Order && r.Response == _design.Response",
                                  StringComparison.Ordinal);
        int order  = pick.IndexOf("Best(candidates.Where(r => r.Order == _design.Order))",
                                  StringComparison.Ordinal);
        int nearest = pick.IndexOf("Math.Abs(r.Order - _design.Order)", StringComparison.Ordinal);
        Assert.True(own > 0 && order > own && nearest > order,
                    "the three tiers are not tried own-first, then own order, then nearest");

        // Buildable beats exact-but-unbuildable at every tier, which is why Best is one helper.
        Assert.Contains("FirstOrDefault(r => !r.Solution.ImplausibleValues)", pick, StringComparison.Ordinal);

        // …and the first tier really is preferred: an edit the design's own family CAN answer does not
        // wander off it.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        d.ApplySolution(d.Solutions[0]);
        d.WaitForAnalysis();
        var (family, order0) = (d.Design.Response, d.Design.Order);

        d.LinkTransforms = false;
        d.WaitForAnalysis();
        d.Term2.Resistance = 25.0;
        d.WaitForAnalysis();

        Assert.Equal(family, d.Design.Response);
        Assert.Equal(order0, d.Design.Order);
        Assert.True(d.Rebuild!.OnTarget);
        d.Dispose();
    }

    /// <summary>
    /// <b>The request is kept until something answers it</b>, and dropped at the end of the search if
    /// nothing does.
    /// </summary>
    /// <remarks>
    /// Held as a source scan as well as by the behaviour above, because the failure mode is a silent
    /// one: consuming the request on the first landing looks correct on every design whose own cell
    /// has solutions, which is most of them.
    /// </remarks>
    [Fact]
    public void TheAutoSolveRequest_SurvivesACellThatOffersNothing()
    {
        string src = Src("src", "Ui", "Match", "MatchDesignerViewModel.Analysis.cs");

        var land = Between(src, "private void LandBatch(");
        Assert.Contains("TryPendingAutoSolve();", land, StringComparison.Ordinal);
        Assert.DoesNotContain("if (batch.IsCurrent) TryPendingAutoSolve();", land, StringComparison.Ordinal);

        var complete = Between(src, "private void LandSearchComplete(");
        Assert.Contains("TryPendingAutoSolve(lastChance: true);", complete, StringComparison.Ordinal);

        var trypend = Between(src, "private void TryPendingAutoSolve(bool lastChance = false)");
        Assert.Contains("AutoApplyAReachingSolution(epoch) || lastChance", trypend,
                        StringComparison.Ordinal);

        // "Settled" is the return, and "nothing to move onto YET" is the one false.
        var auto = Between(src, "private bool AutoApplyAReachingSolution(");
        Assert.Contains("if (chosen is null) return false;", auto, StringComparison.Ordinal);

        // The guard that reads the WINDOW's state, not just the transform ratio.
        Assert.Contains("_rebuild.Refusal is null && _rebuild.Network is not null && _rebuild.OnTarget",
                        auto, StringComparison.Ordinal);

        // …and it still fires at most once per edit.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        d.LinkTransforms = false;
        d.WaitForAnalysis();
        d.Term2.Resistance = 25.0;
        d.WaitForAnalysis();
        int applied = d.Design.AppliedSolutions.Count;
        d.WaitForAnalysis();
        Assert.Equal(applied, d.Design.AppliedSolutions.Count);
        d.Dispose();
    }

    // ══ 12 — the title strip's two buttons are live ═════════════════════════

    /// <summary>
    /// <b>Undo and Redo are enabled when there is something to undo</b> (owner-reported, 2026-08-28:
    /// they were always disabled).
    /// </summary>
    /// <remarks>
    /// Binding <c>Command</c> alone leaves a Button's enablement to arrive through
    /// <c>ICommand.CanExecuteChanged</c>, and its first evaluation happens when the binding attaches —
    /// which is after <c>SetTarget</c> has run, against an empty stack. Every other gated button in
    /// this window states its enablement outright (Probe reads <c>CanProbe</c>, the scroll-to-applied
    /// button reads <c>HasAppliedSolution</c>), so these two do too rather than introducing a second
    /// mechanism beside it. The commands are still notified, so the keyboard path stays right.
    /// </remarks>
    [Fact]
    public void UndoAndRedo_AreEnabledWhenThereIsSomethingToUndo()
    {
        string xaml = Xaml();
        Assert.Contains("IsEnabled=\"{Binding CanUndo}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanRedo}\"", xaml, StringComparison.Ordinal);

        var (vm, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.False(d.CanUndo);
        Assert.False(d.CanRedo);

        // Each of the three edits the owner named: a solution, a transform, a specification field.
        d.ApplySolution(d.Solutions.First(r => !r.IsCurrent));
        d.WaitForAnalysis();
        Assert.True(d.CanUndo, "applying a solution left Undo unavailable");

        vm.UndoRedo.Undo();
        Assert.True(d.CanRedo, "an undo left Redo unavailable");
        vm.UndoRedo.Redo();                              // back onto the solution, and its transforms
        d.WaitForAnalysis();

        Assert.NotEmpty(d.Design.Transforms);
        d.SetTransformForm(0, d.Design.Transforms[0].Form == TransformForm.Pi
                              ? TransformForm.T : TransformForm.Pi);
        d.WaitForAnalysis();
        Assert.True(d.CanUndo, "a transform edit left Undo unavailable");

        d.Term2.Resistance = 30.0;
        d.WaitForAnalysis();
        Assert.True(d.CanUndo, "a specification edit left Undo unavailable");

        // The notification really is raised, or the buttons would never re-read any of it.
        int raised = 0;
        d.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(d.CanUndo)) raised++; };
        while (vm.UndoRedo.CanUndo) vm.UndoRedo.Undo();
        Assert.True(raised > 0, "CanUndo never notified");
        Assert.False(d.CanUndo);
        d.Dispose();
    }

    // ══ 13 — the absorbed-element legend ════════════════════════════════════

    /// <summary>
    /// <b>The legend is one clause</b> (owner, 2026-08-28: the sentence about being drawn beside the
    /// termination that supplies it, and this component not containing it, does not make sense —
    /// "is supplied by the external termination" is enough).
    /// </summary>
    /// <remarks>
    /// It said the same thing three ways: the drawing already puts the element beside its
    /// termination, and "supplied by the external termination" already says the component has not
    /// got it.
    /// </remarks>
    [Fact]
    public void TheAbsorbedElementLegend_IsOneClause()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        string legend = d.LadderLegend;
        output.WriteLine(legend);
        Assert.NotEmpty(legend);

        Assert.Contains("supplied by the external termination", legend, StringComparison.Ordinal);
        Assert.DoesNotContain("drawn beside", legend, StringComparison.Ordinal);
        Assert.DoesNotContain("does not contain", legend, StringComparison.Ordinal);
        Assert.EndsWith(".", legend, StringComparison.Ordinal);
        Assert.DoesNotContain("—", legend, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>An edit is held against the analysis landings for its whole read-modify-write</b> — the
    /// hazard the auto-solve's longer wait created.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not theorised.</b> Switching a transform from π to T straight after a termination
    /// edit came back as π: the auto-solve had replaced <c>_design.Transforms</c> wholesale between
    /// this edit reading the record and its refresh rebuilding from it. Every landing already took
    /// <c>RefreshGate</c>, and <c>Refresh</c> took it too — but an edit is a READ, a MUTATION and
    /// then a refresh, and only the last of the three was inside it.
    ///
    /// <para>That was survivable while the auto-solve could only fire in the narrow window right
    /// after the edit that asked for it. It stopped being survivable when the request was allowed to
    /// wait for later cells of the search, so the fix ships with the change that created the need for
    /// it. In the application both run on the UI thread and cannot interleave; a host whose result
    /// scheduler falls back to the thread pool is where it was caught, and where this test runs.</para>
    /// </remarks>
    [Fact]
    public void AnEditIsHeldAgainstTheLandings_ForItsWholeReadModifyWrite()
    {
        string vmSrc = Src("src", "Ui", "Match", "MatchDesignerViewModel.cs");
        Assert.Contains("lock (RefreshGate) edit();", Between(vmSrc, "private void AsOneEdit(Action edit)"),
                        StringComparison.Ordinal);

        // Every path that mutates the design goes through it — the spec funnel, the transform rack,
        // an applied solution, a typed element value and Revert.
        foreach (var (file, signature) in new[]
        {
            ("MatchDesignerViewModel.cs",            "private void CommitSpecChange()"),
            ("MatchDesignerViewModel.cs",            "public void Revert()"),
            ("MatchDesignerViewModel.Transforms.cs", "public void AddTransform("),
            ("MatchDesignerViewModel.Transforms.cs", "public void RemoveLastTransform()"),
            ("MatchDesignerViewModel.Transforms.cs", "public void SetTransformN("),
            ("MatchDesignerViewModel.Transforms.cs", "public void SetTransformForm("),
            ("MatchDesignerViewModel.Transforms.cs", "public void SetTransformLocked("),
            ("MatchDesignerViewModel.Network.cs",    "private void ApplySolution(MatchSolutionRowViewModel row, long amendStamp)"),
            ("MatchDesignerViewModel.InlineEdit.cs", "private bool SetElementValue(string name, double target)"),
        })
        {
            // From the SIGNATURE, not from Between's body: an expression-bodied
            // `=> AsOneEdit(() => { ... })` puts the call outside the first brace.
            string src = Src("src", "Ui", "Match", file);
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, $"{signature} is not in {file}");
            Assert.Contains("AsOneEdit(", src[at..(at + signature.Length + 400)],
                            StringComparison.Ordinal);
        }

        // …and the behaviour: a transform edit made immediately after a termination edit, with the
        // search still running behind it, is the edit that stands.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        d.ApplySolution(d.AllSolutions.First(r => r.Solution.Transforms.Count > 0));
        d.WaitForAnalysis();

        d.Term2.Resistance = 30.0;               // arms an auto-solve that may land at any point
        Assert.NotEmpty(d.Design.Transforms);
        var was = d.Design.Transforms[0].Form;
        var want = was == TransformForm.Pi ? TransformForm.T : TransformForm.Pi;

        d.SetTransformForm(0, want);
        Assert.Equal(want, d.Design.Transforms[0].Form);

        d.WaitForAnalysis();
        output.WriteLine($"{was} -> {want}, settled at {d.Design.Transforms.FirstOrDefault()?.Form}");
        d.Dispose();
    }

    /// <summary>
    /// <b>Only the filtered list is drawn from</b> (owner, 2026-08-28: the candidates must come only
    /// from the filtered list of solutions; a filter set so tight that nothing is left is a fine
    /// outcome).
    /// </summary>
    /// <remarks>
    /// Auto-applying a row the panel is not showing would move the design onto a network the user has
    /// said they do not want to consider, and leave them reading a list that does not contain the
    /// thing they are now on.
    ///
    /// <para><b>It also retires the separate negative-element guard, which was the same rule said
    /// twice.</b> The filter's own "Allow negative components" toggle is exactly that question, it is
    /// off by default, and it is the one the user can see. <c>MatchDesign.AllowNegativeComponents</c>
    /// is no longer an input at all — nothing in the window sets it, and <c>ApplySolution</c> WRITES
    /// it from the row that was applied — so gating candidates on it was gating on the auto-solve's
    /// own output.</para>
    /// </remarks>
    [Fact]
    public void TheCandidates_AreTheFilteredListAndOnlyIt()
    {
        // 1. A filter tight enough to empty the list leaves the design alone rather than reaching
        //    past it — "that's ok" is the owner's own word for this outcome.
        var (_, _, d) = Open(RefusingFamilyProblem());
        d.LinkTransforms = false;
        d.WaitForAnalysis();

        foreach (var o in d.Filter.Orders) o.IsOn = false;
        foreach (var r in d.Filter.Responses) r.IsOn = false;
        Assert.Empty(d.Solutions.Where(r => !r.IsCurrent));

        var before = d.Design.Transforms.Select(t => (t.ElementA, t.Form, t.N)).ToList();
        d.Term2.Resistance = 75.0;
        d.WaitForAnalysis();

        Assert.Equal(before, d.Design.Transforms.Select(t => (t.ElementA, t.Form, t.N)).ToList());
        Assert.Empty(d.Design.AppliedSolutions);
        output.WriteLine($"filtered to nothing: still {d.Design.Response} order {d.Design.Order}");
        d.Dispose();

        // 2. And with the filter open, the same edit is answered — so the emptiness above is the
        //    filter's doing and not an auto-solve that has stopped working.
        var (_, _, e) = Open(RefusingFamilyProblem());
        e.LinkTransforms = false;
        e.WaitForAnalysis();
        e.Term2.Resistance = 75.0;
        e.WaitForAnalysis();
        Assert.True(e.Rebuild!.OnTarget && e.Rebuild.Refusal is null);
        Assert.NotEmpty(e.Design.AppliedSolutions);
        e.Dispose();

        // 3. Whatever is chosen is a row the panel is showing.
        var (_, _, f) = Open(Problem());
        f.WaitForAnalysis();
        f.LinkTransforms = false;
        f.WaitForAnalysis();
        f.Term2.Resistance = 25.0;
        f.WaitForAnalysis();
        var applied = f.AllSolutions.FirstOrDefault(r => r.IsCurrent);
        Assert.NotNull(applied);
        Assert.Contains(applied!, f.Solutions);
        f.Dispose();

        // 4. The design flag the guard used to read is an OUTPUT of applying, which is why gating on
        //    it was gating on this method's own answer.
        Assert.Contains("_design.AllowNegativeComponents = row.HasNegativeComponents;",
                        Src("src", "Ui", "Match", "MatchDesignerViewModel.Network.cs"),
                        StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding AllowNegativeComponents, Mode=TwoWay}", Xaml(),
                              StringComparison.Ordinal);
    }
}
