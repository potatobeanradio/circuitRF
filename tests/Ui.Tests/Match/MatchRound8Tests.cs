// ================================================================
//  MatchRound8Tests.cs  —  the owner's 2026-08-28 round-8 list for the Match Designer: the
//  specification pane loses Order, Filter Response and Options; the Solutions list moves into it and
//  becomes the whole order x response cross-product, filtered rather than specified; the applied
//  solution is marked so it cannot be missed; and Delete Plot is greyed on the two response plots.
//
//  Same discipline as rounds 1-7: view-model, geometry and source-scan tests, never pixels. Where the
//  ask is about layout declared in AXAML, or about a wiring path that needs a live Avalonia
//  application, the assertion is made against the source the mechanism is written in and NAMES that
//  mechanism — a scan for "the file mentions the word" would pass over a broken fix.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public sealed class MatchRound8Tests(ITestOutputHelper output)
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

    private static string Xaml() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");
    private static string Code() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");

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

    // ══ 1 — the three cards that left, and the one that had to stay ═════════

    /// <summary>
    /// <b>Order, Filter Response and Options are gone from the specification pane</b> (owner: "remove
    /// the Order group and Filter Response groups from the Specification panel — they are no longer
    /// needed because we now have the Solution panel displaying all the possible solutions"; "remove
    /// the Options Group").
    /// </summary>
    /// <remarks>
    /// Asserted by the CONTROLS each card owned rather than by its heading, because a heading can be
    /// renamed while the control it labelled stays. What must be absent is the order selector's
    /// binding, the response combo's, and the two Options checkboxes' — each of which is the one
    /// place that setting could be made.
    /// </remarks>
    [Fact]
    public void TheSpecificationPane_NoLongerSpecifiesOrderResponseOrTheTwoOptions()
    {
        string xaml = Xaml();

        foreach (string binding in new[]
        {
            "{Binding OrderChoices}",
            "SelectedItem=\"{Binding OrderChoice, Mode=TwoWay}\"",
            "{Binding ResponseOptions}",
            "{Binding SelectedResponseOption, Mode=TwoWay}",
            "{Binding QAdjustEnabled, Mode=TwoWay}",
            "{Binding AllowNegativeComponents, Mode=TwoWay}",
        })
            Assert.DoesNotContain(binding, xaml, StringComparison.Ordinal);

        // RIPPLE stayed, and had to: it is an input to the PROBLEM, not a choice of family. Its own
        // CARD did not — the band and ripple cards were merged on 2026-08-28 to free height for the
        // Solutions list (see MatchRound9Tests) — so what is checked is the ROW, which is what the
        // claim was ever about: the field is still there and still editable.
        Assert.Contains("Text=\"Ripple, dB\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding RippleEntry, Mode=TwoWay}", xaml, StringComparison.Ordinal);

        // So did the ONE automatic-change note the departing cards carried that is still worth
        // saying: a Q-adjust the terminations have overtaken is a number the user typed, and nothing
        // else on screen reports that it has been cleared.
        Assert.Contains("{Binding QAdjustNote}", xaml, StringComparison.Ordinal);

        // The ORDER's note went entirely (owner-reported, 2026-08-28: "I don't want to see messages
        // like this after I make changes just because the order changed. I can clearly see the order
        // changed because a different solution card is now selected, so cluttering the UI with this
        // message is bad UX", and then to drop the dead-end line too). An adjusted order is on the
        // applied card; a pair that permits no order refuses, and the refusal is in the status strip.
        Assert.DoesNotContain("OrderNote", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The Solutions list is under the Frequency Band card, in the specification pane</b> (owner:
    /// "move the Solutions listing to be under the Frequency Band group in the Specification panel"),
    /// and the drawer it used to be is gone along with its toggle.
    /// </summary>
    [Fact]
    public void TheSolutionsList_IsInTheSpecificationPane_BelowTheBand()
    {
        string xaml = Xaml();

        int spec = xaml.IndexOf("Text=\"Specification\"", StringComparison.Ordinal);
        // One heading since 2026-08-28: the band and ripple cards were merged to free height for this
        // very list, on the owner's instruction. What this test is about — the list sits below that
        // card and inside this pane — is unchanged.
        int band = xaml.IndexOf("Text=\"Frequency Band &amp; Ripple\"", StringComparison.Ordinal);
        int list = xaml.IndexOf("ItemsSource=\"{Binding Solutions}\"", StringComparison.Ordinal);
        int network = xaml.IndexOf("Text=\"Impedance Matching Network\"", StringComparison.Ordinal);

        Assert.True(spec > 0 && band > spec, "the band card is inside the specification pane");
        Assert.True(list > band, "the solutions list comes after the band card");
        Assert.True(list < network, "the solutions list is still inside the specification pane");

        // The drawer, its column and its toggle are all gone — asserted on both surfaces, because a
        // binding left behind names a property that no longer exists.
        Assert.DoesNotContain("SolutionsPanelOpen", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SolutionsPanelOpen", Code(), StringComparison.Ordinal);
        Assert.DoesNotContain("SolutionsPanelOpen",
                              Src("src", "Ui", "Match", "MatchDesignerViewModel.Network.cs"),
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The specification column is wider</b> (owner: "widen the Specification panel slightly") —
    /// and still narrower than the 300 it started at, which is what "slightly" has to mean.
    /// </summary>
    [Fact]
    public void TheSpecificationColumn_IsWiderThanItWas_AndTheGridHasThreeColumns()
    {
        var cols = Regex.Match(Xaml(), @"ColumnDefinitions=""(\d+),\*,380""");
        Assert.True(cols.Success, "the pane grid no longer declares three columns with a fixed first");

        double column = double.Parse(cols.Groups[1].Value, CultureInfo.InvariantCulture);
        output.WriteLine($"specification column {column}");
        Assert.InRange(column, 240, 300);
    }

    /// <summary>
    /// <b>The list virtualizes.</b> One order in one family was a handful of rows; the cross-product
    /// is hundreds, and an <c>ItemsControl</c> in a <c>ScrollViewer</c> realizes every one of them.
    /// </summary>
    /// <remarks>
    /// The same answer, and the same class name, this repository already reached twice — see
    /// <c>TechEditorView</c>'s row lists and the wBond Touchstone export dialog's port list.
    /// </remarks>
    [Fact]
    public void TheSolutionsList_Virtualizes()
    {
        string xaml = Xaml();
        int list = xaml.IndexOf("ItemsSource=\"{Binding Solutions}\"", StringComparison.Ordinal);
        Assert.True(list > 0);

        // 400, not 200: the opening tag grew two attributes on 2026-08-28 (AutoScrollToSelectedItem
        // and ScrollViewer.BringIntoViewOnFocusChange, both off — see MatchRound9Tests), which is
        // ~250 characters between "<ListBox" and the ItemsSource this window is measured back from.
        string open = xaml[Math.Max(0, list - 400)..list];
        Assert.Contains("<ListBox", open, StringComparison.Ordinal);
        Assert.Contains("Classes=\"rows\"", open, StringComparison.Ordinal);

        // …and the class flattens the selection chrome, so a virtualized row still reads as a card.
        Assert.Contains("Selector=\"ListBox.rows ListBoxItem:selected /template/ ContentPresenter\"",
                        xaml, StringComparison.Ordinal);
    }

    // ══ 2 — the list is the whole cross-product ═════════════════════════════

    /// <summary>
    /// <b>Every permitted order and every response family, in one list</b> (owner: "I want the
    /// Solutions panel to list all the solutions for every filter response and order").
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheList_SpansEveryPermittedOrder_AndMoreThanOneFamily()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var orders = d.AllSolutions.Select(r => r.Order).Distinct().OrderBy(o => o).ToList();
        var families = d.AllSolutions.Select(r => r.Response).Distinct().ToList();
        output.WriteLine($"{d.AllSolutions.Count} solutions over orders [{string.Join(", ", orders)}] "
                         + $"and {families.Count} families");

        Assert.True(orders.Count > 1, "the list is confined to one order");
        Assert.True(families.Count > 1, "the list is confined to one family");

        // Every row is at an order this termination pair actually permits — nothing is searched that
        // could not be applied.
        var permitted = MatchOrders.ValidOrders(d.Design.Term1, d.Design.Term2);
        Assert.All(d.AllSolutions, r => Assert.Contains(r.Order, permitted));

        d.Dispose();
    }

    /// <summary>
    /// <b>Each card names its family, its order, and the two things worth warning about</b> (owner:
    /// "each solution listed now needs to indicate Filter Response type, order, and if it's
    /// Q-adjusted or if it has a negative component"). <b>And nothing else</b> — "if the solution is
    /// not Q-adjusted then the Solution card should say nothing about it. Same with negative values."
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ACard_NamesItsFamilyAndOrder_AndOnlyTheExceptions()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        foreach (var row in d.AllSolutions)
        {
            Assert.Contains(row.ResponseName, row.TitleText, StringComparison.Ordinal);
            Assert.Contains($"order {row.Order}", row.TitleText, StringComparison.Ordinal);

            // The exception lines are present exactly when the exception is.
            Assert.Equal(row.Solution.QAdjust > 0, row.QAdjustText.Length > 0);
            Assert.Equal(row.HasNegativeComponents, row.NegativeNote.Length > 0);
        }

        // The fixture has to contain both kinds for the two equalities above to mean anything.
        Assert.Contains(d.AllSolutions, r => r.Solution.QAdjust > 0);
        Assert.Contains(d.AllSolutions, r => !r.HasNegativeComponents);
        output.WriteLine(string.Join("\n", d.AllSolutions.Take(6)
            .Select(r => $"{r.TitleText,-30} {r.CountText,-13} {r.QAdjustText,-22} {r.NegativeNote}")));

        d.Dispose();
    }

    /// <summary>
    /// <b>The negative flag is read off the network, not off the search that permitted it.</b>
    /// </summary>
    /// <remarks>
    /// Every cell is searched with <c>AllowNegativeComponents</c> ON, because the flag only ever
    /// widens a transform's positivity range and one pass therefore finds what two would. That makes
    /// "which pass found it" useless as an answer and the finished network the only honest one.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheNegativeFlag_IsWhatTheNetworkContains()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.All(d.AllSolutions, r => Assert.Equal(
            r.Solution.Network.Elements.Any(e => e.Value <= 0), r.HasNegativeComponents));

        d.Dispose();
    }

    // ══ 3 — the filter ══════════════════════════════════════════════════════

    /// <summary>
    /// <b>Q-adjusted on, negative components off</b> (owner: "the default Solutions filter should
    /// have show Q-adjusted on, but Negative Components turned off"), and every order and family on.
    /// </summary>
    [Fact]
    public void TheFilterDefaults_AreQAdjustedOn_AndNegativeComponentsOff()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.True(d.Filter.ShowQAdjusted);
        Assert.False(d.Filter.ShowNegativeComponents);
        Assert.All(d.Filter.Orders, o => Assert.True(o.IsOn));
        Assert.All(d.Filter.Responses, r => Assert.True(r.IsOn));

        // Which is visible in the list: Q-adjusted rows are there, negative ones are not.
        Assert.Contains(d.Solutions, r => r.Solution.QAdjust > 0);
        Assert.DoesNotContain(d.Solutions, r => r.HasNegativeComponents && !r.IsCurrent);
        Assert.Contains(d.AllSolutions, r => r.HasNegativeComponents);

        d.Dispose();
    }

    /// <summary>
    /// <b>The filter FILTERS — it does not search.</b> Every row it hides is still in hand, and
    /// turning it back on shows the same object, not a re-found one.
    /// </summary>
    [Fact]
    public void TheFilter_HidesAndShows_WithoutResearching()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var all = d.AllSolutions.ToList();
        int before = d.Solutions.Count;
        Assert.NotEmpty(all);

        var order = d.Filter.Orders.First(o => d.AllSolutions.Any(r => r.Order == o.Order));
        var sw = Stopwatch.StartNew();
        order.IsOn = false;
        sw.Stop();

        Assert.DoesNotContain(d.Solutions, r => r.Order == order.Order && !r.IsCurrent);
        Assert.True(d.Solutions.Count < before, "turning an order off hid nothing");
        Assert.Equal(all, d.AllSolutions);                    // same objects, same order, nothing re-found
        Assert.False(d.IsSearchingSolutions, "the filter started a search");
        output.WriteLine($"one filter toggle: {sw.Elapsed.TotalMilliseconds:F2} ms, "
                         + $"{before} -> {d.Solutions.Count} of {all.Count}");

        order.IsOn = true;
        Assert.Equal(before, d.Solutions.Count);
        Assert.Equal(all, d.AllSolutions);

        d.Dispose();
    }

    /// <summary>
    /// <b>The applied solution is listed whatever the filter says.</b> A panel whose job is to make
    /// "which one am I looking at?" obvious cannot answer it by hiding the answer.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheAppliedSolution_SurvivesEveryFilter()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var pick = d.Solutions.First();
        pick.Apply();
        d.WaitForAnalysis();
        Assert.True(pick.IsCurrent);

        foreach (var o in d.Filter.Orders) o.IsOn = false;
        foreach (var r in d.Filter.Responses) r.IsOn = false;
        d.Filter.ShowQAdjusted = false;

        Assert.Contains(pick, d.Solutions);
        Assert.Single(d.Solutions);
        // The button's DOT is gone (owner, 2026-08-28 — see MatchRound9Tests); the flag it drove is
        // not, because it is still what decides whether the list is the whole answer, and the
        // tooltip below says the same thing in words.
        Assert.True(d.Filter.IsNarrowed, "the filter does not report that it is hiding things");
        output.WriteLine(d.Filter.Summary);

        d.Dispose();
    }

    /// <summary>
    /// <b>The filter control is the Project Tree's</b> (owner: "change the Filter UI in the Solutions
    /// panel to be more like the Project Tree filter UI for workspaces. It uses checkboxes and
    /// overall has a way better UX; I also like its icon better").
    /// </summary>
    /// <remarks>
    /// Asserted against BOTH files, because "like the Project Tree's" is a claim about two surfaces
    /// agreeing — a copy that drifts from the thing it was copied from is the failure this catches.
    /// The code-behind is asserted too: the first shape of this control was a menu built there, and a
    /// handler left wired to a button that now carries a Flyout would open both.
    /// </remarks>
    [Fact]
    public void TheFilterControl_IsTheProjectTreesOwn()
    {
        string xaml = Xaml();
        string tree = Src("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int here = xaml.IndexOf("Name=\"SolutionsFilterButton\"", StringComparison.Ordinal);
        Assert.True(here > 0, "the filter button is not in the AXAML");
        string block = xaml[here..xaml.IndexOf("</Button>", here, StringComparison.Ordinal)];

        // The icon the owner named, in the same weight and the same foreground.
        Assert.Contains("Kind=\"Filter\"", tree, StringComparison.Ordinal);
        Assert.Contains("Kind=\"Filter\" Width=\"14\" Height=\"14\"", block, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource SystemBaseMediumColor}\"", block, StringComparison.Ordinal);

        // A Button.Flyout of CheckBoxes, not a menu.
        Assert.Contains("<Button.Flyout>", block, StringComparison.Ordinal);
        Assert.Contains("<CheckBox", block, StringComparison.Ordinal);
        Assert.Contains("{Binding Filter.ShowQAdjusted}", block, StringComparison.Ordinal);
        Assert.Contains("{Binding Filter.ShowNegativeComponents}", block, StringComparison.Ordinal);
        Assert.Contains("{Binding Filter.Orders}", block, StringComparison.Ordinal);
        Assert.Contains("{Binding Filter.Responses}", block, StringComparison.Ordinal);

        Assert.DoesNotContain("ShowSolutionsFilterMenu", Code(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The order lines follow the termination pair, and an order the user turned off <b>stays off</b>
    /// across a change that keeps it.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void TheOrderLines_FollowTheTerminations_AndRememberWhatWasTurnedOff()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        // The UNION across the three forms, not the bandpass parity alone (match.md §16.4 item 2).
        // A mixed pair is orders 2, 4, 6 in bandpass form and 2..6 in the other two, and the filter
        // is a view over what was FOUND — a line missing here is a row nobody can hide.
        Assert.Equal(d.FilterOrderOptions, d.Filter.Orders.Select(o => o.Order));
        Assert.Equal([2, 3, 4, 5, 6], d.Filter.Orders.Select(o => o.Order));
        Assert.Equal([2, 4, 6], MatchOrders.ValidOrders(d.Design.Term1, d.Design.Term2));

        var four = d.Filter.Orders.Single(o => o.Order == 4);
        four.IsOn = false;

        d.Term1.Resistance = 6.0;                    // same parity, so order 4 survives the rebuild
        d.WaitForAnalysis();

        Assert.False(d.Filter.Orders.Single(o => o.Order == 4).IsOn,
                     "a surviving order was silently switched back on");
        d.Dispose();
    }

    // ══ 4 — the applied row is unmissable ═══════════════════════════════════

    /// <summary>
    /// <b>Bold title, a green tick and an accent border on the applied card</b> (owner: "the solution
    /// that is currently being viewed has a check mark indicator on its card. This needs to be more
    /// prominent and obvious to the user… Consider using bold text in the card's title, perhaps
    /// change the check mark color indictor to green?").
    /// </summary>
    /// <remarks>
    /// All three hang off ONE class, so what is asserted is that the class is bound to
    /// <c>IsCurrent</c> and that each of the three selectors exists. Three separate bindings could
    /// disagree; one class cannot.
    /// </remarks>
    [Fact]
    public void TheAppliedCard_IsBoldGreenAndBordered_FromOneFlag()
    {
        string xaml = Xaml();
        Assert.Contains("Classes.current=\"{Binding IsCurrent}\"", xaml, StringComparison.Ordinal);

        // The title's selector is :is(TextBlock) since the card's lines became SelectableTextBlock
        // (2026-08-28) — an Avalonia type selector matches its type EXACTLY, so the bare spelling
        // would silently stop applying. The badge is still a plain TextBlock and still exact-type.
        foreach (string selector in new[]
        {
            "Selector=\"Border.card.current\"",
            "Selector=\"Border.card.current TextBlock.solbadge\"",
            "Selector=\"Border.card.current :is(TextBlock).soltitle\"",
        })
            Assert.Contains(selector, xaml, StringComparison.Ordinal);

        // The tick is green and bold; the title is bold. Read out of the three style bodies rather
        // than assumed from their names.
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"#2FA85A\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderBrush\"     Value=\"#2FA85A\"/>", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(xaml, @"Border\.card\.current (?::is\(TextBlock\)|TextBlock)\.(solbadge|soltitle)"">\s*(?:<Setter[^>]*>\s*)*?<Setter Property=""FontWeight"" Value=""Bold""/>").Count);

        // …and the flag itself is what a row exposes.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        var pick = d.Solutions.First(r => !r.IsCurrent);
        pick.Apply();
        d.WaitForAnalysis();

        Assert.True(pick.IsCurrent);
        Assert.Equal("✓", pick.BadgeGlyph);
        Assert.Single(d.AllSolutions, r => r.IsCurrent);
        d.Dispose();
    }

    // ══ 5 — Apply carries the order and the family, and does not re-search ══

    /// <summary>
    /// <b>Applying a row moves the design onto its order, its family and its negative-component
    /// setting</b> — it has to, because those are no longer things the user set beforehand.
    /// </summary>
    [Fact]
    public void Apply_CarriesTheRowsOrderFamilyAndNegativeSetting()
    {
        var (_, comp, d) = Open(Problem());
        d.WaitForAnalysis();

        var elsewhere = d.AllSolutions.First(r => r.Order != d.Design.Order || r.Response != d.Design.Response);
        output.WriteLine($"applying {elsewhere.TitleText} onto a design at "
                         + $"order {d.Design.Order}, {d.Design.Response}");

        elsewhere.Apply();
        d.WaitForAnalysis();

        Assert.Equal(elsewhere.Order, d.Design.Order);
        Assert.Equal(elsewhere.Response, d.Design.Response);
        Assert.Equal(elsewhere.HasNegativeComponents, d.Design.AllowNegativeComponents);
        Assert.True(elsewhere.IsCurrent);

        // …and it is COMMITTED, so the schematic, a save and an undo all see it.
        string stored = comp.Parameters.First(p => p.Name == "Design").Expression;
        Assert.True(MatchEmbedding.TryDecode(stored, out var back) && back is not null);
        Assert.Equal(elsewhere.Order, back!.Order);
        Assert.Equal(elsewhere.Response, back.Response);

        d.Dispose();
    }

    /// <summary>
    /// <b>The rebuild reproduces the network the card described</b> — which is the whole reason Apply
    /// writes <c>AllowNegativeComponents</c> rather than leaving it where the user had it.
    /// </summary>
    /// <remarks>
    /// Measured, not argued: a solution carrying a negative element, applied with the flag OFF, comes
    /// back off target with element values differing by whole orders of magnitude, because
    /// <c>MatchRebuild.ApplySequence</c> clamps every N back inside its positivity range. A solution
    /// with no negative element is unaffected either way, since the clamp is a no-op on a rack that
    /// is already inside its ranges — so the flag can be, and is, set from what the row contains.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void EveryAppliedRow_RebuildsToTheNetworkItPromised()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        foreach (var row in d.AllSolutions.Take(12).ToList())
        {
            row.Apply();
            d.WaitForAnalysis();

            var built = d.Rebuild!.Network;
            Assert.NotNull(built);
            Assert.Equal(row.Solution.Network.Elements.Count, built!.Elements.Count);
            for (int i = 0; i < built.Elements.Count; i++)
                Assert.Equal(row.Solution.Network.Elements[i].Value, built.Elements[i].Value, 9);
        }

        d.Dispose();
    }

    /// <summary>
    /// <b>Apply does not restart the search</b>, and neither does a filter toggle. This is what makes
    /// clicking through candidates usable when the search behind them costs seconds.
    /// </summary>
    /// <remarks>
    /// The search is keyed on <c>MatchSpecKey</c> — the terminations, the band, the ripple, the
    /// analysis end and Qmin — and Apply writes none of those. Asserted by object identity of the
    /// rows: a re-search clears the list and builds new ones.
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Apply_DoesNotRestartTheSearch()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        var all = d.AllSolutions.ToList();

        var sw = Stopwatch.StartNew();
        d.AllSolutions.First(r => !r.IsCurrent).Apply();
        sw.Stop();
        d.WaitForAnalysis();

        Assert.Equal(all, d.AllSolutions);
        Assert.True(d.SolutionsComplete);
        output.WriteLine($"apply blocked the UI thread for {sw.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(sw.Elapsed.TotalMilliseconds < 250.0,
                    $"apply blocked for {sw.Elapsed.TotalMilliseconds:F1} ms");

        // …and a termination change, which DOES move the key, does restart it.
        d.Term2.Resistance = 75.0;
        d.WaitForAnalysis();
        Assert.NotEqual(all, d.AllSolutions);

        d.Dispose();
    }

    /// <summary>
    /// <b>The list is built in the background and lands in pieces</b> (owner: "UI needs to feel
    /// responsive so if the solution list is built (and added to the UI) in the background that would
    /// be nice").
    /// </summary>
    /// <remarks>
    /// <b>No wall-clock threshold</b> — this repository does not add timing tests, they measure the
    /// machine. What is asserted is the STRUCTURE that makes streaming possible and would have to be
    /// undone to lose it: the search publishes per (order, family) cell rather than once at the end,
    /// the design's own cell is published FIRST so the applied solution is on screen immediately, and
    /// the rows are INSERTED by sort key so the list reads the same whichever cell finishes when.
    /// </remarks>
    [Fact]
    public void TheSearch_PublishesCellByCell_OwnCombinationFirst()
    {
        string analysis = Src("src", "Ui", "Match", "MatchDesignerViewModel.Analysis.cs");

        // Published per cell, from inside the loop.
        Assert.Contains(
            "publish(new MatchSolutionBatch(form, order, shape, set, isCurrent, design.BandCount));",
            analysis, StringComparison.Ordinal);

        // The design's own combination is yielded before the sweep.
        Assert.Contains("if (ownIsValid) yield return (design.Form, design.Order, design.Response);",
                        analysis, StringComparison.Ordinal);

        // APPENDED, never inserted — see the next test for the defect that establishes.
        Assert.Contains("_allSolutions.Add(new MatchSolutionRowViewModel(", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("_allSolutions.Insert(", analysis, StringComparison.Ordinal);

        // …and the panel says the list is not final yet, so a short one is never mistaken for the
        // whole answer.
        Assert.Contains("{Binding SolutionsProgressNote}", Xaml(), StringComparison.Ordinal);

        var (_, _, d) = Open(Problem());
        Assert.True(d.IsSearchingSolutions, "the search did not start in the background");
        Assert.False(d.SolutionsComplete);
        d.WaitForAnalysis();
        Assert.True(d.SolutionsComplete);
        Assert.False(d.IsSearchingSolutions);
        d.Dispose();
    }

    /// <summary>
    /// <b>A row that has landed never moves.</b> Rows are appended, so a cell that finishes while the
    /// user is reading cannot slide the card under their pointer.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-28:</b> <i>"sometimes hitting Apply changes the scroll view to a
    /// different position and it seems like my solution selection was not picked (I did not see a
    /// green outline box)"</i>. The rows were being INSERTED at their canonical (order, family, rank)
    /// position while the cells arrived in a different order — the design's own runs first — so for
    /// the seconds the cross-product takes, every landing cell pushed the rows below it down. A click
    /// in that window applies whichever card has arrived under the cursor.
    ///
    /// <para>What is asserted is the property that makes that impossible: a prefix of the list, once
    /// taken, is still the same prefix afterwards.</para>
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ARowThatHasLanded_NeverMoves()
    {
        var (_, _, d) = Open(Problem());

        var seen = new List<MatchSolutionRowViewModel>();
        for (int i = 0; i < 400 && d.IsSearchingSolutions; i++)
        {
            var now = d.AllSolutions.ToList();
            Assert.Equal(seen, now.Take(seen.Count));       // the prefix is unchanged, every time
            seen = now;
            System.Threading.Thread.Sleep(5);
        }

        d.WaitForAnalysis();
        Assert.Equal(seen, d.AllSolutions.Take(seen.Count));
        output.WriteLine($"{seen.Count} rows had landed while the search ran; "
                         + $"{d.AllSolutions.Count} in the end");
        d.Dispose();
    }

    /// <summary>
    /// <b>The design's own order and family come FIRST in the list</b>, which is what makes appending
    /// a better order rather than merely a safe one — and is what the scroll-to-applied lands on.
    /// </summary>
    [Fact]
    public void TheDesignsOwnCombination_IsAtTheTopOfTheList()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        int order = d.Design.Order;
        var response = d.Design.Response;
        var head = d.AllSolutions.TakeWhile(r => r.Order == order && r.Response == response).ToList();

        Assert.NotEmpty(head);
        Assert.Equal(head.Count, d.AllSolutions.Count(r => r.Order == order && r.Response == response));
        output.WriteLine($"{head.Count} of {d.AllSolutions.Count} rows are the design's own combination, "
                         + "and they are the first ones");
        d.Dispose();
    }

    /// <summary>
    /// <b>Two solutions of the same order and family have the SAME response, and that is not a bug.</b>
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-28:</b> <i>"hitting Apply on the Solution card does not update the
    /// Response plots"</i>. Measured against the plots' own SNP: applying a sibling leaves it
    /// bit-identical, and applying a row from another order or family moves it. A Norton transform is
    /// response-preserving by construction — that is MN-1's whole premise, and the reason the search
    /// can offer several racks for one network at all. Siblings differ in which elements were split
    /// and therefore in what is BUILDABLE, never in what the network does.
    ///
    /// <para>Pinned rather than fixed, because the alternative reading — "Apply is broken" — is the
    /// one a reader will otherwise reach, and it would send them looking for a refresh path that is
    /// working correctly. What made it look like a bug was the list moving under the pointer, which
    /// is <see cref="ARowThatHasLanded_NeverMoves"/>.</para>
    /// </remarks>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ASiblingSolution_HasTheSameResponse_AndAnotherCombinationDoesNot()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        double[] Response()
        {
            Assert.NotNull(d.ResponseSnp);
            return [.. Enumerable.Range(0, d.ResponseSnp!.FrequencyCount)
                .Select(i => d.ResponseSnp.Matrices[i][1, 0].Magnitude)];
        }

        // "The same" to a part in 1e12, not bit for bit: the two racks reach the required ratio by
        // different sequences of transforms, so the arithmetic differs in the last couple of digits.
        double Worst(double[] a, double[] b) =>
            Enumerable.Range(0, a.Length).Max(i => Math.Abs(a[i] - b[i]));

        d.AllSolutions.First().Apply();
        d.WaitForAnalysis();
        var baseline = Response();

        // A sibling is same order, same family AND same Q-adjust. A Q-adjusted variant is a
        // different DESIGN — it inflates the analysis end's Q and adds an element there — so it is
        // entitled to a different response and is not what this test is about.
        var sibling = d.AllSolutions.First(r => !r.IsCurrent
                                                && r.Order == d.Design.Order && r.Response == d.Design.Response
                                                && r.Solution.QAdjust == d.Design.QAdjust);
        sibling.Apply();
        d.WaitForAnalysis();
        double same = Worst(baseline, Response());
        output.WriteLine($"sibling {sibling.TitleText} / {sibling.CountText}: worst |Δ|S21|| = {same:E3}");
        Assert.True(same < 1e-12, $"a sibling moved the response by {same:E3}");

        var elsewhere = d.AllSolutions.First(r => r.Order != d.Design.Order || r.Response != d.Design.Response);
        elsewhere.Apply();
        d.WaitForAnalysis();
        double moved = Worst(baseline, Response());
        output.WriteLine($"{elsewhere.TitleText}: worst |Δ|S21|| = {moved:E3}");
        Assert.True(moved > 1e-6, $"another order or family should move the response; it moved {moved:E3}");

        d.Dispose();
    }

    /// <summary>
    /// <b>Clicking a card applies it</b>, and <b>the list scrolls to the applied one when it first
    /// appears</b> (owner: "when Solutions scroll view first appears, it needs to be scrolled to the
    /// currently applied solution").
    /// </summary>
    /// <remarks>
    /// Both are wiring a headless test cannot exercise, so each is asserted where it is written and by
    /// the mechanism it is written in.
    ///
    /// <para>THE GESTURE MOVED on 2026-08-28 and this test moved with it. Round 8 made a DOUBLE-click
    /// apply, beside an Apply button; round 9 removed the button and made a single click do it, by
    /// hanging the apply off the list's SELECTION rather than off the pointer — which is what also
    /// made the arrow keys work (see MatchRound9Tests). What this test was about is unchanged and is
    /// still worth holding: there is ONE entry point, and a double-click still applies, because its
    /// first press selects.</para>
    ///
    /// <para>The scroll is checked to be armed once, re-armed on a Reset, and posted rather than
    /// called inline, because a row published this instant has no container to scroll to yet.</para>
    /// </remarks>
    [Fact]
    public void ACardAppliesOnClick_AndTheListScrollsToTheAppliedRow()
    {
        string xaml = Xaml();
        string code = Code();

        var sel = Between(code, "private void OnSolutionSelectionChanged(");
        Assert.Contains("row.Apply();", sel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSolutionDoubleTapped", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnApplySolution", code, StringComparison.Ordinal);

        Assert.Contains("Name=\"SolutionsList\"", xaml, StringComparison.Ordinal);
        var scroll = Between(code, "private void ScrollToAppliedOnce()");
        Assert.Contains("_scrolledToApplied", scroll, StringComparison.Ordinal);
        Assert.Contains("r.IsCurrent", scroll, StringComparison.Ordinal);
        var go = Between(code, "private void ScrollToApplied()");
        Assert.Contains("ScrollIntoView(applied)", go, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", go, StringComparison.Ordinal);

        var changed = Between(code, "private void OnSolutionsCollectionChanged(");
        Assert.Contains("NotifyCollectionChangedAction.Reset", changed, StringComparison.Ordinal);
        Assert.Contains("_scrolledToApplied = false;", changed, StringComparison.Ordinal);

        Assert.Contains("WireSolutionsList();", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A button beside the filter goes back to the applied card</b> (owner: "add a button next to
    /// the filter button that will auto-scroll to the current solution card").
    /// </summary>
    /// <remarks>
    /// The automatic scroll fires once, when the list first has an applied row; after that the panel
    /// stays where the user left it, which leaves no way back from four hundred rows down. Both
    /// routes call the same <c>ScrollToApplied</c>, so there is one behaviour and not two — and the
    /// button is disabled when there is nothing to go to, which a design on a hand-set transform set
    /// genuinely is.
    /// </remarks>
    [Fact]
    public void AButtonBesideTheFilter_GoesBackToTheAppliedCard()
    {
        string xaml = Xaml();
        string code = Code();

        int button = xaml.IndexOf("Name=\"ScrollToAppliedButton\"", StringComparison.Ordinal);
        int filter = xaml.IndexOf("Name=\"SolutionsFilterButton\"", StringComparison.Ordinal);
        Assert.True(button > 0 && filter > button, "the two buttons are not side by side in the header");

        string block = xaml[button..xaml.IndexOf("</Button>", button, StringComparison.Ordinal)];
        Assert.Contains("IsEnabled=\"{Binding HasAppliedSolution}\"", block, StringComparison.Ordinal);
        Assert.Contains("<mi:MaterialIcon", block, StringComparison.Ordinal);

        Assert.Contains("WireButton(\"ScrollToAppliedButton\"", code, StringComparison.Ordinal);
        Assert.Contains("ScrollToApplied()", Between(code, "private void ScrollToAppliedOnce()"),
                        StringComparison.Ordinal);

        // …and the flag it is enabled by says what it claims.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.Equal(d.Solutions.Any(r => r.IsCurrent), d.HasAppliedSolution);

        var pick = d.Solutions.First(r => !r.IsCurrent);
        pick.Apply();
        d.WaitForAnalysis();
        Assert.True(d.HasAppliedSolution);
        d.Dispose();
    }

    // ══ 6 — Settings loses its fourth entry ═════════════════════════════════

    /// <summary>
    /// <b>"Offer Q-adjusted solutions" is gone from the Settings menu</b> (owner: "remove Offer
    /// Q-adjusted solutions from the Settings button menu") — <b>and gone from the settings object
    /// with it</b>, because a setting nothing can reach is state that silently does nothing.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Settings_NoLongerOffersTheQAdjustSwitch()
    {
        Assert.DoesNotContain("OfferQAdjustedSolutions", Code(), StringComparison.Ordinal);
        Assert.DoesNotContain("OfferQAdjustedSolutions",
                              Src("src", "Ui", "Match", "MatchDesignerSettings.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("Offer Q-adjusted", Code(), StringComparison.Ordinal);

        // The three that stay are still there, and the search still runs Q-adjusted candidates.
        string code = Code();
        foreach (string entry in new[] { "UnitMenu(", "DigitsMenu()", "QMinMenu()" })
            Assert.Contains(entry, code, StringComparison.Ordinal);

        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.Contains(d.AllSolutions, r => r.Solution.QAdjust > 0);
        d.Dispose();
    }

    // ══ 7 — Delete Plot on the two response plots ═══════════════════════════

    /// <summary>
    /// <b>Delete Plot is greyed on the Designer's two plots</b> (owner, reported again 2026-08-28:
    /// "the 'Delete Plot' context menu should be disabled (greyed out) on the two response plots").
    /// </summary>
    /// <remarks>
    /// <b>The flag was already set and the enablement was applied in ONE place: the menu's
    /// <c>Opening</c> handler.</b> The menu is built lazily on the first right-click and cached for
    /// the control's lifetime, so that single hook was the whole mechanism — its own remark has
    /// always claimed build time as well, and only the hook was wired. Both halves are now real, plus
    /// a property-changed hook for a host that moves either flag after the menu exists. Asserted as
    /// three separate places, because that is what the failure was: one place, and the wrong one.
    /// </remarks>
    [Fact]
    public void DeletePlot_IsDisabledOnTheResponsePlots_AtBuildTimeAndOnEveryChange()
    {
        string xaml = Xaml();
        Assert.Equal(2, Regex.Matches(xaml, @"CanDeletePlot=""False""").Count);
        Assert.Equal(2, Regex.Matches(xaml, @"CanEditPlotProperties=""False""").Count);

        string plot = Src("src", "Ui", "DataDisplay", "Controls", "PlotControl.cs");

        // 1. Build time — the half that was missing.
        string build = plot[plot.IndexOf("private ContextMenu BuildContextMenu()", StringComparison.Ordinal)..];
        build = build[..build.IndexOf("\n        }", StringComparison.Ordinal)];
        Assert.Contains("ApplyMenuAvailability();\n\n            return menu;", build, StringComparison.Ordinal);

        // 2. Every open.
        Assert.Contains("menu.Opening +=", build, StringComparison.Ordinal);

        // 3. And whenever a host moves either flag after the menu exists.
        Assert.Contains("change.Property == CanDeletePlotProperty", plot, StringComparison.Ordinal);
        Assert.Contains("change.Property == CanEditPlotPropertiesProperty", plot, StringComparison.Ordinal);

        // …and what "disabled" means here is greyed as well as inert: the theme's :disabled selector
        // dims the header text and leaves the icon at full colour, which on macOS still reads as live.
        string apply = plot[plot.IndexOf("private void ApplyMenuAvailability()", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("\n        }\n", StringComparison.Ordinal)];
        Assert.Contains("item.IsEnabled = on;", apply, StringComparison.Ordinal);
        Assert.Contains("item.Opacity = on ? 1.0 : 0.4;", apply, StringComparison.Ordinal);
    }
}
