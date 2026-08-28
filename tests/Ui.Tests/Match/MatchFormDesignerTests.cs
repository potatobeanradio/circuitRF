// ================================================================
//  MatchFormDesignerTests.cs  —  match.md §16 in the Designer: the Solutions panel searches
//  (form x order x family), the filter gains a Form group above Orders, cards name their form,
//  applying a lowpass row sets Design.Form and empties the transform rack with a note rather than a
//  fault, and one undo puts the bandpass design back.
//
//  Same discipline as rounds 1-8: view-model, geometry and source-scan tests, never pixels.
// ================================================================

using System;
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

public sealed class MatchFormDesignerTests(ITestOutputHelper output)
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

    /// <summary>
    /// 50 Ω ∥ 0.5 pF stepping down into 25 Ω + 0.1 nH, 1.8-2.2 GHz.
    /// </summary>
    /// <remarks>
    /// <b>A mixed pair, mixed the RIGHT way round for a lowpass ladder, and with reactances small
    /// enough that every order absorbs them.</b> Two things had to be got right at once:
    /// <list type="bullet">
    /// <item>The parallel capacitance sits on the 50 Ω side and the series inductance on the 25 Ω
    /// side, which is where a lowpass network puts its shunt C and its series L — the impedance
    /// RATIO decides which end takes which, not the analysis end (match.md §16.4, corrected). The
    /// other way round produces only bandpass rows and would prove nothing about the panel.</item>
    /// <item><b>A bigger termination is harder at a HIGHER order here, which is the opposite of the
    /// bandpass intuition.</b> With the ratio pinned at DC, more elements means better return loss
    /// and a gentler ladder, so the END elements SHRINK with order (a = 0.5, r = 10: g1 is 2.485 at
    /// order 2 and 0.820 at order 6). A fixture sized for a bandpass order-4 network absorbs at order
    /// 2 and refuses at 5 and 6, which would have left half the cross-product untested.</item>
    /// </list>
    /// </remarks>
    private static MatchDesign Problem(int order = 4, NetworkForm form = NetworkForm.Bandpass) => new()
    {
        F1 = 1.8e9, F2 = 2.2e9, Order = order, Form = form,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.5e-12),
        Term2 = new Termination(25.0, ReactanceKind.L, TerminationTopology.Series, 0.1e-9),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"<!--.*?-->", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);
        return raw;
    }

    private static string Xaml() => Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml");

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

    private static MatchSolutionRowViewModel? FirstLowpass(MatchDesignerViewModel d) =>
        d.AllSolutions.FirstOrDefault(r => r.Form == NetworkForm.Lowpass);

    // ══ 1 — the filter's Form group ═════════════════════════════════════════

    [Fact]
    public void TheFilter_HasThreeFormLines_AllOnByDefault()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.Equal(
            [NetworkForm.Bandpass, NetworkForm.Lowpass, NetworkForm.Highpass],
            d.Filter.Forms.Select(f => f.Form));
        Assert.Equal(["Bandpass", "Lowpass", "Highpass"], d.Filter.Forms.Select(f => f.Label));
        Assert.All(d.Filter.Forms, f => Assert.True(f.IsOn));

        d.Dispose();
    }

    [Fact]
    public void TheFormGroup_IsFirstInTheFlyout_AboveTheOrders()
    {
        string xaml = Xaml();
        int forms = xaml.IndexOf("{Binding Filter.Forms}", StringComparison.Ordinal);
        int orders = xaml.IndexOf("{Binding Filter.Orders}", StringComparison.Ordinal);
        int responses = xaml.IndexOf("{Binding Filter.Responses}", StringComparison.Ordinal);

        Assert.True(forms > 0, "the flyout has no Form group");
        Assert.True(forms < orders, "the Form group is not above the Order group");
        Assert.True(orders < responses, "the groups are out of order");
    }

    [Fact]
    public void TurningLowpassOff_HidesItsRows_AndLeavesTheOthers()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        int lowpass = d.AllSolutions.Count(r => r.Form == NetworkForm.Lowpass);
        Assert.True(lowpass > 0, "the search found no lowpass solutions on a pair that admits them");
        output.WriteLine(
            $"{d.AllSolutions.Count} solutions: "
            + string.Join(", ", d.AllSolutions.GroupBy(r => r.Form)
                                              .Select(g => $"{g.Key} {g.Count()}")));

        int before = d.Solutions.Count;
        d.Filter.Forms.Single(f => f.Form == NetworkForm.Lowpass).IsOn = false;

        Assert.DoesNotContain(d.Solutions, r => r.Form == NetworkForm.Lowpass && !r.IsCurrent);
        Assert.Equal(before - lowpass, d.Solutions.Count);
        Assert.Contains("lowpass", d.Filter.Summary, StringComparison.Ordinal);

        d.Filter.Forms.Single(f => f.Form == NetworkForm.Lowpass).IsOn = true;
        Assert.Equal(before, d.Solutions.Count);
        d.Dispose();
    }

    [Fact]
    public void TheAppliedRow_SurvivesItsOwnFormBeingTurnedOff()
    {
        // A panel whose whole job is to make "which one am I looking at?" obvious cannot answer it by
        // hiding the answer — the same rule the order and family lines already follow.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        var row = FirstLowpass(d);
        Assert.NotNull(row);
        d.ApplySolution(row!);
        d.WaitForAnalysis();

        d.Filter.Forms.Single(f => f.Form == NetworkForm.Lowpass).IsOn = false;
        Assert.Contains(d.Solutions, r => r.IsCurrent && r.Form == NetworkForm.Lowpass);
        d.Dispose();
    }

    // ══ 2 — the cards ═══════════════════════════════════════════════════════

    [Fact]
    public void EveryCardNamesItsForm_BandpassIncluded()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        foreach (var row in d.AllSolutions)
        {
            Assert.Contains(MatchSolutionRowViewModel.FormName(row.Form), row.TitleText,
                            StringComparison.Ordinal);
            Assert.Contains($"order {row.Order}", row.TitleText, StringComparison.Ordinal);
            Assert.Contains(row.ResponseName, row.TitleText, StringComparison.Ordinal);
        }
        Assert.Contains(d.AllSolutions, r => r.TitleText.Contains("bandpass", StringComparison.Ordinal));
        d.Dispose();
    }

    [Fact]
    public void TheFamilyName_DropsTheSingleDoubleDistinctionInTheseForms()
    {
        // match.md §16.2: with the ratio pinned there is one free parameter, so the double-match
        // Chebyshev is not on offer and a contrast with it would be with nothing.
        Assert.Equal("Chebyshev",
            MatchSolutionRowViewModel.FamilyName(ResponseShape.ChebyshevFano, NetworkForm.Lowpass));
        Assert.Equal("Chebyshev",
            MatchSolutionRowViewModel.FamilyName(ResponseShape.ChebyshevFano, NetworkForm.Highpass));

        // The filter's family lines keep the bandpass spellings — they hide cards across all forms.
        Assert.Equal("Chebyshev (single-match)",
            MatchSolutionRowViewModel.FamilyName(ResponseShape.ChebyshevFano, NetworkForm.Bandpass));
        Assert.Equal("Chebyshev (single-match)",
            MatchSolutionRowViewModel.FamilyName(ResponseShape.ChebyshevFano));
        Assert.Equal("Chebyshev (double-match)",
            MatchSolutionRowViewModel.FamilyName(ResponseShape.ChebyshevTwoEnded));
    }

    [Fact]
    public void NeitherBesselNorDoubleMatchChebyshev_IsSearchedInTheseForms()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        foreach (var row in d.AllSolutions.Where(r => r.Form != NetworkForm.Bandpass))
            Assert.True(row.Response is ResponseShape.ChebyshevFano or ResponseShape.Butterworth,
                        $"{row.Response} was searched in {row.Form} form");
        d.Dispose();
    }

    // ══ 3 — applying a solution ═════════════════════════════════════════════

    [Fact]
    public void ApplyingALowpassRow_SetsTheForm_AndOneUndoPutsTheBandpassDesignBack()
    {
        var (vm, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.Equal(NetworkForm.Bandpass, d.Design.Form);

        var row = FirstLowpass(d);
        Assert.NotNull(row);
        d.ApplySolution(row!);
        d.WaitForAnalysis();

        Assert.Equal(NetworkForm.Lowpass, d.Design.Form);
        Assert.Equal(row!.Order, d.Design.Order);
        Assert.Equal(row.Response, d.Design.Response);
        Assert.Empty(d.Design.Transforms);
        Assert.Contains(d.Solutions, r => r.IsCurrent && r.Form == NetworkForm.Lowpass);

        // ONE undo entry for the whole apply, as an order or a family change already is.
        vm.UndoRedo.Undo();
        Assert.True(MatchEmbedding.TryDecode(
            vm.EditModel.Components[0].Parameters.First(p => p.Name == "Design").Expression,
            out var back));
        Assert.Equal(NetworkForm.Bandpass, back!.Form);

        d.Dispose();
    }

    [Fact]
    public void TheFormEchoParameter_FollowsTheDesign()
    {
        var (_, comp, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.Equal("Bandpass", comp.Parameters.First(p => p.Name == "Form").Expression);

        var row = FirstLowpass(d);
        Assert.NotNull(row);
        d.ApplySolution(row!);
        d.WaitForAnalysis();

        Assert.Equal("Lowpass", comp.Parameters.First(p => p.Name == "Form").Expression);
        d.Dispose();
    }

    // ══ 3b — the card's readout ═════════════════════════════════════════════

    [Fact]
    public void TheReturnLoss_IsQuotedSigned()
    {
        // Owner, 2026-08-28: "RL -10.5 dB", not "RL 10.5 dB". The card sits beside response plots
        // whose y axis is negative, and two spellings of one number in one window is the ambiguity.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        Assert.NotEmpty(d.AllSolutions);
        foreach (var row in d.AllSolutions)
        {
            Assert.StartsWith("RL -", row.ReturnLossText, StringComparison.Ordinal);
            Assert.EndsWith(" dB", row.ReturnLossText, StringComparison.Ordinal);
            Assert.Equal(
                $"RL {row.Solution.WorstReturnLossDb.ToString("0.00", CultureInfo.InvariantCulture)} dB",
                row.ReturnLossText);
        }
        d.Dispose();
    }

    /// <summary>
    /// <b>Every line of the card is selectable text, and the click gesture still applies it.</b>
    /// </summary>
    /// <remarks>
    /// The two halves are one change. <c>SelectableTextBlock.OnPointerPressed</c> sets
    /// <c>e.Handled</c> and captures the pointer, and <c>InputElement</c> registers its class handler
    /// for that event without <c>handledEventsToo</c> — so <c>ListBoxItem</c> stops selecting the row
    /// and a click on the card's TEXT would no longer apply the solution. A tunnelling handler on the
    /// list restores it; asserting the text change without asserting that handler would pin half a
    /// feature.
    /// </remarks>
    [Fact]
    public void EveryCardLine_IsSelectableText_AndTheCardStillAppliesOnClick()
    {
        // The solutions card is the one with the applied-row class on it; three other panes use
        // Classes="card" for an ordinary bordered box.
        string xaml = Xaml();
        int start = xaml.IndexOf("Classes=\"card\" Classes.current=", StringComparison.Ordinal);
        Assert.True(start > 0, "the solution card template moved");
        string card = xaml[start..xaml.IndexOf("</Border>", start, StringComparison.Ordinal)];

        foreach (string binding in new[]
                 {
                     "TitleText", "CountText", "PairsText", "QAdjustText",
                     "ReturnLossText", "NegativeNote", "ImplausibleNote",
                 })
            Assert.Matches(
                @"<SelectableTextBlock\b[^>]*Text=""\{Binding " + binding + @"\}""", card);

        // Seven selectable lines; the badge stays a plain TextBlock — a tick has nothing to copy,
        // and it is the element carrying the tooltip that explains it.
        Assert.Equal(7, Regex.Matches(card, "<SelectableTextBlock ").Count);
        Assert.Equal(1, Regex.Matches(card, "<TextBlock ").Count);
        Assert.Contains("Classes=\"solbadge\"", card, StringComparison.Ordinal);

        // …and the gesture the selectable text would otherwise have eaten. Both halves, because a
        // tunnelling handler that set Handled would take text selection away again.
        string code = Src("src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml.cs");
        Assert.Matches(
            @"AddHandler\(\s*PointerPressedEvent,\s*OnSolutionsPointerPressed,\s*RoutingStrategies\.Tunnel\)",
            code);
        string handler = Between(code, "private void OnSolutionsPointerPressed(");
        Assert.Contains("_solutionsList.SelectedItem = row;", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Handled", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The card's classes have to match a <c>SelectableTextBlock</c>, or its lines change size.</b>
    /// </summary>
    /// <remarks>
    /// Owner-reported, 2026-08-28: the card's font size changed when its lines became selectable. An
    /// Avalonia type selector matches that type EXACTLY, and <c>SelectableTextBlock</c> derives from
    /// <c>TextBlock</c> — so <c>TextBlock.note</c> skipped it silently and the lines fell back to the
    /// inherited size. <c>:is(TextBlock)</c> is the selector that includes derived types. Asserted
    /// per class rather than by counting, because the failure is silent: nothing errors, the styles
    /// simply do not apply.
    /// </remarks>
    [Fact]
    public void TheCardsTextStyles_MatchDerivedTypes_SoSelectableLinesKeepTheirSize()
    {
        string xaml = Xaml();
        foreach (string cls in new[] { "soltitle", "note", "warn" })
        {
            Assert.Contains($":is(TextBlock).{cls}", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain($"Selector=\"TextBlock.{cls}\"", xaml, StringComparison.Ordinal);
        }

        // The badge is the one line that stays a plain TextBlock, so its style stays exact-type.
        Assert.Contains("Selector=\"TextBlock.solbadge\"", xaml, StringComparison.Ordinal);
    }

    // ══ 3c — the Ripple row's note and tooltip ══════════════════════════════

    /// <summary>
    /// <b>With both ends reactive the Ripple row shows no note, and says it in the tooltip instead.</b>
    /// </summary>
    /// <remarks>
    /// Owner-reported, 2026-08-28: the line put a scroll bar on the specification column. It is the
    /// DEFAULT shape of a design and most real ones, so the line the column could least afford was
    /// the one it showed almost always — and with both ends reactive it has no end to name, which is
    /// the only thing a reader could not get from looking at the row. The tooltip opens with it now.
    /// </remarks>
    [Fact]
    public void WithBothEndsReactive_TheRippleNoteIsSilent_AndTheTooltipCarriesIt()
    {
        var (_, _, d) = Open(Problem());          // both ends carry a reactance
        d.WaitForAnalysis();

        Assert.Equal("", d.RippleNote);
        Assert.Contains("The terminations' reactances set this.", d.RippleTooltip,
                        StringComparison.Ordinal);
        d.Dispose();
    }

    [Fact]
    public void WithOneEndReactive_TheNoteStillNamesThatEnd()
    {
        // The single-end spelling stays: WHICH end is the half that cannot be read off the row.
        var design = Problem();
        design.Term2 = Termination.Resistive(25.0, TerminationTopology.Series);

        var (_, _, d) = Open(design);
        d.WaitForAnalysis();

        Assert.Contains("Termination 1", d.RippleNote, StringComparison.Ordinal);
        Assert.DoesNotContain("The terminations' reactances set this.", d.RippleNote,
                              StringComparison.Ordinal);
        d.Dispose();
    }

    // ══ 4 — the transform rack's empty state ════════════════════════════════

    [Fact]
    public void ALowpassBasis_EmptiesTheRack_WithANoteRatherThanAFault()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        Assert.True(d.TransformRackApplies);
        Assert.Equal("", d.TransformRackNote);

        var row = FirstLowpass(d);
        Assert.NotNull(row);
        d.ApplySolution(row!);
        d.WaitForAnalysis();

        Assert.False(d.TransformRackApplies);
        Assert.Empty(d.Transforms);
        Assert.Contains("no Norton pairs", d.TransformRackNote, StringComparison.Ordinal);

        // NOT the bandpass "no transformable pair" wording, which reads as something being wrong.
        Assert.DoesNotContain("transformable pair", d.TransformRackNote, StringComparison.Ordinal);
        Assert.Equal("", d.SolutionsRefusal);
        d.Dispose();
    }

    [Fact]
    public void TheRacksControls_AreBoundToTheSameFlagAsItsNote()
    {
        // One flag, so the note and the controls cannot disagree about whether the rack applies.
        string xaml = Xaml();
        Assert.Contains("IsVisible=\"{Binding TransformRackApplies}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !TransformRackApplies}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding TransformRackNote}", xaml, StringComparison.Ordinal);
    }

    // ══ 5 — the ladder, the flatten and the stamp all take single-element arms ══

    [Fact]
    public void TheLadderLayout_PlacesASingleElementLadder()
    {
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();
        var row = FirstLowpass(d);
        Assert.NotNull(row);
        d.ApplySolution(row!);
        d.WaitForAnalysis();

        // 2n elements, alternating L and C, single-element arms throughout.
        var laid = d.Ladder.Elements;
        Assert.Equal(2 * d.Design.Order, laid.Count(e => e.Role != MatchElementRole.Excess));
        Assert.NotEmpty(d.Elements);
        Assert.Empty(d.Ladder.Brackets);                   // no transforms, so no braces

        // Every element on its own x, nothing off-canvas, and the shunt/series alternation intact —
        // the three things a layout that assumed two elements per arm would break.
        Assert.All(laid, e => Assert.True(double.IsFinite(e.X) && double.IsFinite(e.Y)));
        Assert.Equal(laid.Count, laid.Select(e => Math.Round(e.X, 6)).Distinct().Count());
        Assert.True(d.Ladder.PortRightX > d.Ladder.PortLeftX);
        d.Dispose();
    }

    /// <summary>
    /// What the preview DRAWS is what MN-1 synthesised, in both new forms.
    /// </summary>
    /// <remarks>
    /// The other half of the brief's "component ≡ flattened cell" gate is
    /// <c>MatchFlattenTests.AMatchInEitherNewForm_AndItsFlattenedCell_AlsoAgree</c>, which needs a
    /// real cell folder on disk and lives with the rest of the flatten harness.
    /// </remarks>
    [Theory]
    [InlineData(NetworkForm.Lowpass)]
    [InlineData(NetworkForm.Highpass)]
    public void ThePreview_DrawsWhatWasSynthesised_InBothForms(NetworkForm form)
    {
        // A highpass ladder wants the mirrored pair: series C on the low side, shunt L on the high.
        var design = Problem(4, form);
        if (form == NetworkForm.Highpass)
        {
            design.Term1 = new Termination(50.0, ReactanceKind.L, TerminationTopology.Parallel, 15e-9);
            design.Term2 = new Termination(25.0, ReactanceKind.C, TerminationTopology.Series, 80e-12);
        }

        var basis = MatchSynthesis.Synthesize(design);
        Assert.True(basis.Ok, basis.Refusal?.Message);
        var net = MatchSynthesis.WithEndSplits(basis.Network!, basis, design);

        var (_, _, d) = Open(design);
        d.WaitForAnalysis();

        // What the Designer draws IS what MN-1 synthesised — the same network the component stamps
        // and Flatten writes out. Matched BY NAME rather than by position: the preview is free to
        // place a shunt element wherever it reads best, and this test is about the values.
        var laid = d.Ladder.Elements.ToDictionary(e => e.Name);
        Assert.Equal(net.Elements.Count, laid.Count);
        foreach (var want in net.Elements)
        {
            Assert.True(laid.TryGetValue(want.Name, out var got), $"{want.Name} is not drawn");
            Assert.Equal(want.Type, got!.Type);
            Assert.Equal(want.IsShunt, got.IsShunt);
            Assert.Equal(want.Value, got.Value, 1e-9 * Math.Abs(want.Value));
        }
        d.Dispose();
    }

    // ══ 6 — the search cross-product ════════════════════════════════════════

    /// <summary>
    /// The cross-product reaches every form — <b>and a REACTIVE pair admits only one of the two new
    /// ones, which is the physics and not a gap in the search.</b>
    /// </summary>
    /// <remarks>
    /// A lowpass ladder absorbs R&#8741;C and R+L; a highpass ladder absorbs their duals, R&#8741;L
    /// and R+C. A pair is one or the other, never both, so a design with a reactance at each end sees
    /// bandpass rows plus ONE of lowpass/highpass. Only a purely resistive pair lists all three, which
    /// is what the third case here checks.
    /// </remarks>
    [Theory]
    [InlineData(NetworkForm.Lowpass)]
    [InlineData(NetworkForm.Highpass)]
    public void TheSearch_ReachesTheFormThePairAdmits(NetworkForm expected)
    {
        var design = Problem();
        if (expected == NetworkForm.Highpass)
        {
            design.Term1 = new Termination(50.0, ReactanceKind.L, TerminationTopology.Parallel, 15e-9);
            design.Term2 = new Termination(25.0, ReactanceKind.C, TerminationTopology.Series, 80e-12);
        }

        var (_, _, d) = Open(design);
        d.WaitForAnalysis();

        var byForm = d.AllSolutions.GroupBy(r => r.Form).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine(string.Join(", ", byForm.Select(kv => $"{kv.Key} {kv.Value}")));

        Assert.Contains(NetworkForm.Bandpass, byForm.Keys);
        Assert.Contains(expected, byForm.Keys);
        Assert.DoesNotContain(
            expected == NetworkForm.Lowpass ? NetworkForm.Highpass : NetworkForm.Lowpass,
            byForm.Keys);

        // One row per (form, order, family) cell — zero transforms each (match.md §16.5), so there is
        // nothing for the transform-set enumeration to multiply.
        foreach (var row in d.AllSolutions.Where(r => r.Form != NetworkForm.Bandpass))
            Assert.Empty(row.Solution.Transforms);
        Assert.Equal(10, byForm[expected]);          // 5 orders x 2 families
        Assert.Equal(
            byForm[expected],
            d.AllSolutions.Where(r => r.Form == expected)
                          .Select(r => (r.Order, r.Response)).Distinct().Count());
        d.Dispose();
    }

    [Fact]
    public void AResistivePair_ListsAllThreeForms()
    {
        var design = Problem();
        design.Term1 = Termination.Resistive(50.0);
        design.Term2 = Termination.Resistive(25.0, TerminationTopology.Series);

        var (_, _, d) = Open(design);
        d.WaitForAnalysis();

        var byForm = d.AllSolutions.GroupBy(r => r.Form).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine(string.Join(", ", byForm.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.Contains(NetworkForm.Bandpass, byForm.Keys);
        Assert.Equal(10, byForm[NetworkForm.Lowpass]);
        Assert.Equal(10, byForm[NetworkForm.Highpass]);
        d.Dispose();
    }

    [Fact]
    public void ALikeTopologyPair_ListsOnlyBandpassRows_ButKeepsItsOrderLines()
    {
        var design = Problem();
        design.Term2 = new Termination(25.0, ReactanceKind.C, TerminationTopology.Parallel, 2e-12);

        var (_, _, d) = Open(design);
        d.WaitForAnalysis();

        Assert.All(d.AllSolutions, r => Assert.Equal(NetworkForm.Bandpass, r.Form));

        // The order lines are still the pair's own bandpass parity — a filter with no lines at all
        // would leave every row unhideable.
        Assert.Equal([3, 5], d.Filter.Orders.Select(o => o.Order));
        d.Dispose();
    }

    [Fact]
    public void NoQAdjustedRowsInTheseForms()
    {
        // match.md §16.4 item 3: the near end's surplus element already IS the adjustment.
        var (_, _, d) = Open(Problem());
        d.WaitForAnalysis();

        foreach (var row in d.AllSolutions.Where(r => r.Form != NetworkForm.Bandpass))
            Assert.Equal(0.0, row.Solution.QAdjust);
        d.Dispose();
    }
}
