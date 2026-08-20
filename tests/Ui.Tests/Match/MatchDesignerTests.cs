using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

/// <summary>
/// MN-3's gates (brief §8). <b>View-model tests, not pixel tests</b> — everything the window has to
/// promise is a property of <see cref="MatchDesignerViewModel"/>, and the two places where a pixel
/// would be the only oracle (the pictogram, the ladder drawing) are asserted through the layout model
/// the renderer reads, so the assertion is about meaning rather than about a colour.
/// </summary>
public class MatchDesignerTests(ITestOutputHelper output)
{
    // ── fixtures ──────────────────────────────────────────────────────────────

    /// <summary>match.md §4.9's interstage problem — the acceptance anchor.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    private static EditableComponent PlaceMatch(MatchDesign? design = null)
    {
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        if (design is not null)
            comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);
        return comp;
    }

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign? design = null)
    {
        var model = new SchematicEditModel();
        var comp = PlaceMatch(design);
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    /// <summary>Picks a transformable pair by its display name, so a test says which one it means.</summary>
    private static MatchAvailablePair Pair(MatchDesignerViewModel d, string display)
    {
        var pairs = d.AvailablePairs();
        var hit = pairs.FirstOrDefault(p => p.Display == display);
        Assert.True(hit is not null,
            $"'{display}' is not available; the ladder offers {string.Join(", ", pairs.Select(p => p.Display))}");
        return hit!;
    }

    private static MatchDesign StoredDesign(EditableComponent comp)
    {
        string payload = comp.Parameters.First(p => p.Name == "Design").Expression;
        Assert.True(MatchEmbedding.TryDecode(payload, out var d));
        return d!;
    }

    // ── §0.3 — everything the user sets survives a save and a reload ──────────

    /// <summary>
    /// The brief's headline guarantee, through the UI: two transforms (one pi, one T, one locked),
    /// link on, a Q-adjusted solution applied — close the window, open a NEW one on the same
    /// component, and every one of those is back.
    /// </summary>
    [Fact]
    public void ASessionRoundTrip_BringsBackEveryTransform_ItsForm_ItsLock_TheLinkStateAndTheBadges()
    {
        var (vm, comp, designer) = Open(Golden());

        var pairs = designer.AvailablePairs();
        Assert.True(pairs.Count >= 2, $"the golden ladder offers {pairs.Count} pairs");
        output.WriteLine("pairs: " + string.Join(", ", pairs.Select(p => p.Display)));

        designer.LinkTransforms = true;
        designer.AddTransform(pairs[0]);
        var second = designer.AvailablePairs().FirstOrDefault();
        Assert.NotNull(second);
        designer.AddTransform(second!);

        designer.Transforms[1].Form = TransformForm.T;
        designer.Transforms[1].Locked = true;

        // A solution's fingerprint has to be in AppliedSolutions for the badge to survive.
        var solution = designer.Solutions.FirstOrDefault();
        Assert.NotNull(solution);
        solution!.Apply();
        string appliedFingerprint = solution.Solution.Fingerprint;

        var before = StoredDesign(comp);
        designer.Dispose();

        var reopened = new MatchDesignerViewModel();
        reopened.SetTarget(vm, comp);

        Assert.Equal(before.Transforms.Count, reopened.Design.Transforms.Count);
        for (int i = 0; i < before.Transforms.Count; i++)
        {
            Assert.Equal(before.Transforms[i].ElementA, reopened.Design.Transforms[i].ElementA);
            Assert.Equal(before.Transforms[i].ElementB, reopened.Design.Transforms[i].ElementB);
            Assert.Equal(before.Transforms[i].Form,     reopened.Design.Transforms[i].Form);
            Assert.Equal(before.Transforms[i].Locked,   reopened.Design.Transforms[i].Locked);
            Assert.Equal(before.Transforms[i].N,        reopened.Design.Transforms[i].N, 12);
        }
        Assert.Equal(before.LinkTransforms, reopened.Design.LinkTransforms);
        Assert.Equal(before.QAdjust, reopened.Design.QAdjust, 12);
        Assert.Contains(appliedFingerprint, reopened.Design.AppliedSolutions);

        // The badge itself, not just the fingerprint behind it.
        var badges = reopened.Solutions
            .Where(s => s.Solution.Fingerprint == appliedFingerprint)
            .Select(s => s.Badge).ToList();
        Assert.NotEmpty(badges);
        Assert.All(badges, b => Assert.True(
            b is MatchSolutionBadge.Current or MatchSolutionBadge.PreviouslyApplied,
            $"badge was {b}"));

        reopened.Dispose();
    }

    // ── §0.3 — nothing lives only in the view-model ───────────────────────────

    /// <summary>
    /// Every committed edit writes <c>Design</c>. Asserted by making one edit of each kind and reading
    /// the component's own parameter back — never the view-model's copy.
    /// </summary>
    [Fact]
    public void EveryCommittedEdit_WritesTheDesignParameter()
    {
        var (_, comp, designer) = Open(Golden());

        designer.Order = 2;
        Assert.Equal(2, StoredDesign(comp).Order);

        designer.Term1.Resistance = 150.0;
        Assert.Equal(150.0, StoredDesign(comp).Term1.R, 9);

        designer.AllowNegativeComponents = true;
        Assert.True(StoredDesign(comp).AllowNegativeComponents);

        designer.LinkTransforms = false;
        Assert.False(StoredDesign(comp).LinkTransforms);

        designer.PlotPoints = 201;
        Assert.Equal(201, StoredDesign(comp).PlotPoints);

        var pair = designer.AvailablePairs().First();
        designer.AddTransform(pair);
        Assert.Single(StoredDesign(comp).Transforms);

        designer.Transforms[0].Locked = true;
        Assert.True(StoredDesign(comp).Transforms[0].Locked);

        designer.Transforms[0].Form = TransformForm.T;
        Assert.Equal(TransformForm.T, StoredDesign(comp).Transforms[0].Form);

        designer.Dispose();
    }

    /// <summary>
    /// The echo parameters follow the design — <b>from the first committed edit onward</b>.
    /// </summary>
    /// <remarks>
    /// Opening the Designer deliberately writes nothing. A hand-authored <c>Design</c> whose echoes
    /// were never filled in therefore still shows its old labels until the first edit, and that is the
    /// lesser of the two evils: the alternative is a commit — an undo entry, and a dirty document —
    /// produced by nothing more than looking at a component.
    /// </remarks>
    [Fact]
    public void TheEchoParameters_FollowTheDesign_FromTheFirstCommittedEdit()
    {
        var (_, comp, designer) = Open(Golden());
        string Value(string n) => comp.Parameters.First(p => p.Name == n).Expression;

        Assert.Equal("1.8", Value("F1"));          // the placement default; opening wrote nothing

        designer.Term2.Resistance = 2.5;

        Assert.Equal("3.3", Value("F1"));
        Assert.Equal("5", Value("F2"));
        Assert.Equal("4", Value("Order"));
        Assert.Equal("ChebyshevFano", Value("Response"));
        Assert.Equal("200", Value("R1"));
        Assert.Equal("2.5", Value("R2"));

        designer.Dispose();
    }

    // ── §1 — undo goes to the schematic's own stack ───────────────────────────

    [Fact]
    public void ADesignerEdit_UndoesFromTheSchematicsOwnStack()
    {
        var (vm, comp, designer) = Open(Golden());
        Assert.False(vm.UndoRedo.CanUndo);

        designer.Order = 2;
        Assert.Equal(2, StoredDesign(comp).Order);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.Equal(4, StoredDesign(comp).Order);
        // ...and the window followed the model rather than keeping its own copy.
        Assert.Equal(4, designer.Design.Order);

        designer.Dispose();
    }

    // ── §5 — the transform rack ───────────────────────────────────────────────

    /// <summary>With link on and exactly one transform, N is fully determined (match.md §4.8).</summary>
    [Fact]
    public void LinkWithOneTransform_DisablesTheSliderAndTheNumericBox()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = true;
        designer.AddTransform(designer.AvailablePairs().First());

        Assert.Single(designer.Transforms);
        Assert.False(designer.Transforms[0].CanEditN);
        Assert.Contains("fully determined", designer.Transforms[0].DisabledReason);

        // A second one makes both movable again — the rule is about the COUNT, not about link.
        designer.AddTransform(designer.AvailablePairs().First());
        Assert.True(designer.Transforms[0].CanEditN);

        designer.Dispose();
    }

    /// <summary>
    /// Dragging one slider leaves <c>Π N²</c> on target and never writes a locked row.
    /// </summary>
    [Fact]
    public void LinkRedistributes_KeepsTheProductOnTarget_AndNeverWritesALockedRow()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = true;
        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.AddTransform(Pair(designer, "L3 / L4"));
        designer.AddTransform(Pair(designer, "C2 / C3"));
        Assert.Equal(3, designer.Transforms.Count);

        // Lock the step-DOWN pair. It is the one whose N the linkage would otherwise pin at its own
        // bound, so locking it is both the realistic gesture and the one that leaves N2 free to
        // absorb what N1 gives up.
        double lockedN = designer.Transforms[2].N;
        designer.Transforms[2].Locked = true;

        output.WriteLine($"before: achieved {designer.Rebuild!.Achieved:G8} " +
                         $"required {designer.Rebuild.Required:G8}");

        double n0 = designer.Transforms[0].N;
        designer.SetTransformN(0, n0 * 0.9);

        foreach (var t in designer.Transforms)
            output.WriteLine($"  {t.Label} {t.ActsOn} N={t.N:G8} locked={t.Locked} " +
                             $"range=[{t.NMin:G6},{t.NMax:G6}]");
        output.WriteLine($"after:  achieved {designer.Rebuild!.Achieved:G8} " +
                         $"required {designer.Rebuild.Required:G8}");

        Assert.Equal(lockedN, designer.Transforms[2].N, 12);
        Assert.NotEqual(n0, designer.Transforms[0].N, 6);
        Assert.True(designer.Rebuild.OnTarget,
            $"achieved {designer.Rebuild.Achieved:G12} vs required {designer.Rebuild.Required:G12}");

        designer.Dispose();
    }

    /// <summary>
    /// The slider's range is the RECOMPUTED one, and the proof is that it MOVES when an earlier
    /// transform's N moves (MN-1 §7.2). A stored bound could not do that.
    /// </summary>
    /// <remarks>
    /// The pair used here is one of the first transform's own PRODUCTS, and deliberately so. A pair
    /// that lies entirely on one side of an earlier transform has an invariant range — the positivity
    /// threshold is a RATIO of the two element values, and absorbing the ideal transformer scales a
    /// whole side by N² — so it would be the one pair that cannot tell a recomputed bound from a
    /// stored one.
    /// </remarks>
    [Fact]
    public void SliderBounds_AreRecomputedAgainstTheLadderAsItStands()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = false;

        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.SetTransformN(0, 3.0);

        var productPair = designer.AvailablePairs()
            .First(p => p.ElementA.StartsWith("L1_N1_", StringComparison.Ordinal));
        designer.AddTransform(productPair);

        var atThree = designer.Transforms[1].Range;
        Assert.NotNull(atThree);
        output.WriteLine($"N1 = 3.0  ->  {productPair.Display} range " +
                         $"[{atThree!.Min:G8}, {atThree.Max:G8}]");

        // Move the FIRST transform and look again. Same pair, same row, different bound.
        designer.SetTransformN(0, 4.0);
        var atFour = designer.Transforms[1].Range;
        Assert.NotNull(atFour);
        output.WriteLine($"N1 = 4.0  ->  {productPair.Display} range " +
                         $"[{atFour!.Min:G8}, {atFour.Max:G8}]");

        Assert.False(
            Math.Abs(atThree.Min - atFour.Min) < 1e-12 && Math.Abs(atThree.Max - atFour.Max) < 1e-12,
            "the second transform's range did not move when the first did — it is a stored bound");

        designer.Dispose();
    }

    /// <summary>
    /// The premise, now through the UI: a Norton transform changes element values and leaves the
    /// two-port's transfer function alone. Same S-parameters, from the same engine, either side of a
    /// slider move.
    /// </summary>
    [Fact]
    public void TheResponseDoesNotMoveWhenASliderDoes()
    {
        var (_, _, designer) = Open(Golden());
        designer.PlotPoints = 51;
        designer.LinkTransforms = true;
        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.AddTransform(Pair(designer, "L3 / L4"));
        Assert.True(designer.Rebuild!.OnTarget, "the two transforms reach the required ratio");

        var before = designer.ResponseSnp;
        Assert.NotNull(before);

        double n0 = designer.Transforms[0].N;
        designer.SetTransformN(0, n0 * 0.95);
        Assert.NotEqual(n0, designer.Transforms[0].N, 6);

        var after = designer.ResponseSnp;
        Assert.NotNull(after);

        double worst = 0.0;
        for (int f = 0; f < before!.Frequencies.Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    worst = Math.Max(worst, (before.Matrices[f][i, j] - after!.Matrices[f][i, j]).Magnitude);

        output.WriteLine($"N1 {n0:G8} -> {designer.Transforms[0].N:G8}; " +
                         $"worst |ΔS| over {before.Frequencies.Length} points = {worst:E3}");
        Assert.True(worst < 1e-9, $"the response moved by {worst:E3}");

        designer.Dispose();
    }

    /// <summary>The plots are held for the duration of a drag and refreshed on release (brief §5).</summary>
    [Fact]
    public void DuringADrag_TheLadderTracksLive_AndThePlotsCatchUpOnRelease()
    {
        var (_, _, designer) = Open(Golden());
        designer.PlotPoints = 51;
        designer.LinkTransforms = false;
        designer.AddTransform(designer.AvailablePairs().First());

        var snpBefore = designer.ResponseSnp;
        string valueBefore = designer.Elements[0].ValueText;

        designer.BeginTransformDrag();
        designer.SetTransformN(0, designer.Transforms[0].N * 0.8);

        Assert.Same(snpBefore, designer.ResponseSnp);              // the plot is held
        Assert.NotEqual(valueBefore, designer.Elements[0].ValueText); // the values are not

        designer.EndTransformDrag();
        Assert.NotSame(snpBefore, designer.ResponseSnp);

        designer.Dispose();
    }

    // ── §3 — order parity and the response selector ───────────────────────────

    /// <summary>
    /// Switching a termination's topology changes the parity the order has to have; the Designer
    /// adjusts it and SAYS SO, because a control that silently changes another control is worse than
    /// one that explains itself (match.md §9.2).
    /// </summary>
    [Fact]
    public void ATopologyChange_AdjustsTheOrderAndEmitsTheExplanatoryLine()
    {
        var (_, comp, designer) = Open(Golden());
        Assert.Equal(4, designer.Order);                                  // mixed pair -> even
        Assert.Equal([2, 4, 6], designer.OrderOptions);

        designer.Term2.Topology = TerminationTopology.Parallel;           // now a LIKE pair -> odd

        Assert.Equal([3, 5], designer.OrderOptions);
        Assert.Contains(designer.Order, designer.OrderOptions);
        Assert.Contains("cannot absorb both ends", designer.OrderNote);
        Assert.Contains($"moved to {designer.Order}", designer.OrderNote);
        Assert.Equal(designer.Order, StoredDesign(comp).Order);
        output.WriteLine(designer.OrderNote);

        designer.Dispose();
    }

    /// <summary>
    /// A response that cannot absorb both ends at the current order is shown DISABLED with the
    /// numeric reason in its tooltip, never silently missing (match.md §6.6).
    /// </summary>
    [Fact]
    public void AnInfeasibleResponse_IsDisabledWithItsNumbersInTheTooltip()
    {
        var (_, _, designer) = Open(Golden());

        // Every family is offered, always — that is the point of the rule.
        Assert.Equal(4, designer.ResponseOptions.Count);

        var disabled = designer.ResponseOptions.Where(o => !o.IsEnabled).ToList();
        foreach (var o in designer.ResponseOptions)
            output.WriteLine($"{o.Shape,-18} enabled={o.IsEnabled}  {o.Tooltip}");

        Assert.NotEmpty(disabled);
        foreach (var o in disabled)
        {
            Assert.NotNull(o.Refusal);
            Assert.NotEmpty(o.Refusal!.Numbers);
            // The tooltip is the refusal's own message, so it carries the refusal's own numbers.
            Assert.Equal(o.Refusal.Message, o.Tooltip);
            Assert.Matches(@"\d", o.Tooltip);
        }

        designer.Dispose();
    }

    // ── §6 — the status strip ─────────────────────────────────────────────────

    [Fact]
    public void TheStatusStrip_StatesQAndTheMatch_AndFlagsNoTermination_WhenTheDesignIsFine()
    {
        var (_, _, designer) = Open(Golden());
        output.WriteLine(designer.Status.Text);

        Assert.False(designer.Status.IsRefused);
        Assert.True(designer.Status.Q1 > 0);
        Assert.True(designer.Status.Q2 > 0);
        Assert.True(designer.Status.WorstReturnLossDb < 0);
        Assert.Contains("Π N²", designer.Status.RatioText);

        designer.Dispose();
    }

    /// <summary>
    /// A refusal appears in the strip with its numbers, and the affected termination turns red
    /// (match.md §9.7).
    /// </summary>
    [Fact]
    public void ARefusal_SurfacesInTheStatusStrip_AndTurnsTheRightTerminationRed()
    {
        // Termination 2's 10 pF at 1.25 ohm is a Q of 3.13; asking Bessel to absorb it is the
        // refusal match.md §6.4 says will be the common one.
        var (_, _, designer) = Open(Golden());
        designer.Response = ResponseShape.Bessel;

        output.WriteLine(designer.Status.Text);
        Assert.True(designer.Status.IsRefused, "Bessel was expected to refuse the golden far end");

        var refusal = designer.Status.Refusal!;
        Assert.NotEmpty(refusal.Numbers);
        Assert.Contains(refusal.Message, designer.Status.Text);

        int end = refusal.End ?? 0;
        Assert.True(end is 1 or 2, "the refusal names an end");
        Assert.Equal(end == 1, designer.Term1.IsFlagged);
        Assert.Equal(end == 2, designer.Term2.IsFlagged);

        designer.Dispose();
    }

    /// <summary>"No solutions available for order 4" — a sentence this window can say plainly.</summary>
    [Fact]
    public void WhenTheSearchFinesNothing_TheWindowSaysSoPlainly()
    {
        var design = Golden();
        design.Response = ResponseShape.Bessel;
        var (_, _, designer) = Open(design);

        output.WriteLine(designer.SolutionsRefusal);
        Assert.Empty(designer.Solutions);
        Assert.StartsWith("No solutions available for order 4.", designer.SolutionsRefusal);

        designer.Dispose();
    }

    // ── §4 — absorbed elements are visually distinct in BOTH presentations ────

    [Fact]
    public void AbsorbedElements_AreADistinctColourRole_InTheSchematicAndInTheGrid()
    {
        var (_, _, designer) = Open(Golden());

        var absorbedInGrid = designer.Elements.Where(e => e.Role == MatchElementRole.Absorbed).ToList();
        Assert.Equal(2, absorbedInGrid.Count);      // the golden design absorbs one element per end
        Assert.All(absorbedInGrid, e => Assert.Equal(ColorRole.MatchAbsorbed, e.ColorRoleKey));
        Assert.All(absorbedInGrid, e => Assert.Equal("absorbed", e.Note));

        var absorbedInLadder = designer.Ladder.Elements
            .Where(e => e.Role == MatchElementRole.Absorbed).ToList();
        Assert.Equal(absorbedInGrid.Count, absorbedInLadder.Count);
        Assert.All(absorbedInLadder, e => Assert.Equal(ColorRole.MatchAbsorbed, e.ColorRoleKey));

        // The two presentations name the SAME elements, which is the property that matters.
        Assert.Equal(
            absorbedInGrid.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal),
            absorbedInLadder.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));

        // ...and the legend is present, rather than left to be inferred.
        Assert.True(designer.Ladder.HasAbsorbed);
        Assert.Contains("Dimmed", designer.LadderLegend);

        // Everything else is the ordinary symbol colour, so "distinct" means something.
        Assert.Contains(designer.Ladder.Elements, e => e.ColorRoleKey == ColorRole.SchematicSymbolLine);

        designer.Dispose();
    }

    /// <summary>Brackets are drawn under the products a transform created, and stacked when they
    /// would overlap (§4).</summary>
    [Fact]
    public void TransformBrackets_SitUnderTheirOwnProducts_AndStackWhenTheyOverlap()
    {
        var (_, _, designer) = Open(Golden());
        designer.LinkTransforms = false;
        designer.AddTransform(Pair(designer, "L1 / L2"));
        designer.AddTransform(Pair(designer, "L3 / L4"));

        var brackets = designer.Ladder.Brackets;
        Assert.Equal(2, brackets.Count);
        foreach (var b in brackets)
            output.WriteLine($"{b.Label}: x {b.X0:F0}..{b.X1:F0} row {b.Row}");

        Assert.Equal(["N1", "N2"], brackets.Select(b => b.Label));
        Assert.All(brackets, b => Assert.True(b.X1 > b.X0));

        // Two brackets that overlap in x must not share a row; two that do not may.
        for (int i = 0; i < brackets.Count; i++)
            for (int j = i + 1; j < brackets.Count; j++)
                if (brackets[i].X0 < brackets[j].X1 && brackets[j].X0 < brackets[i].X1)
                    Assert.NotEqual(brackets[i].Row, brackets[j].Row);

        designer.Dispose();
    }

    // ── the grid's own promises ───────────────────────────────────────────────

    [Fact]
    public void TheGrid_CopiesAsCsv_AndHasNoStandardValueColumn()
    {
        var (_, _, designer) = Open(Golden());
        string csv = designer.ElementsCsv;
        output.WriteLine(csv);

        Assert.StartsWith("instance,type,orientation,value,unit,note", csv);
        Assert.Equal(designer.Elements.Count + 1, csv.TrimEnd().Split('\n').Length);
        Assert.DoesNotContain("E12", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E24", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MN1.", csv, StringComparison.Ordinal);

        designer.Dispose();
    }

    // ── §9 — what is wired but not built ──────────────────────────────────────

    /// <summary>
    /// <b>Nothing in the footer is a placeholder any more.</b> Both Flatten and Probe are built, and
    /// on this bare fixture — a <c>Match</c> alone in a scratch schematic with nothing wired to
    /// either pin — each is disabled for a REAL reason it names, not for a brief it waits on.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite for each in turn: MN-4 built the probe and MN-5 built
    /// the flatten. Kept rather than deleted because the property worth holding is the one that
    /// survived both — <b>a disabled control in this window states a condition the user can act
    /// on</b>. The full sets live in <c>MatchProbeTests</c> and <c>MatchFlattenTests</c>.
    /// </remarks>
    [Fact]
    public void ADisabledFooterControl_NamesAConditionTheUserCanActOn_NeverABriefItWaitsOn()
    {
        var (_, _, designer) = Open(Golden());

        // Flatten: this fixture's schematic was never saved, so there is nowhere to put a cell.
        Assert.False(designer.CanFlatten);
        Assert.Contains("Save this schematic", designer.FlattenTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("MN-5", designer.FlattenTooltip, StringComparison.Ordinal);

        // Probe: nothing is wired to MN1.
        Assert.False(designer.Term1.CanProbe);
        Assert.Equal(MatchProbeBlock.NetIsBare, designer.Term1.Availability.Block);
        Assert.DoesNotContain("MN-4", designer.Term1.ProbeTooltip);

        designer.Dispose();
    }

    // ── the response netlist is the FULL design ───────────────────────────────

    /// <summary>
    /// The plotted netlist contains the absorbed elements and terminates in R1 and R2 — it is the
    /// response the user is judging, not the component's own (match.md §9.6).
    /// </summary>
    [Fact]
    public void ThePlottedNetlist_IsTheFullDesign_TerminatedInR1AndR2()
    {
        var (_, _, designer) = Open(Golden());
        var network = designer.Rebuild!.Network!;
        string cnl = MatchDesignerViewModel.BuildNetlist(network);
        output.WriteLine(cnl);

        int elementLines = cnl.Split('\n').Count(l => l.StartsWith("L:", StringComparison.Ordinal)
                                                   || l.StartsWith("C:", StringComparison.Ordinal));
        Assert.Equal(network.Elements.Count, elementLines);
        Assert.True(network.Elements.Any(e => e.IsAbsorbed), "the golden design absorbs something");

        Assert.Contains($"Num=1 Z={network.R1.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", cnl);
        Assert.Contains($"Num=2 Z={network.R2.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}", cnl);

        // ...and the far port really did reach the requested termination.
        Assert.Equal(designer.Design.Term2.R, network.R2, 6);

        designer.Dispose();
    }

    /// <summary>Both plots exist, with the traces §9.6 names — and no trace renormalises.</summary>
    [Fact]
    public void BothPlots_CarryTheirTraces_AndNoTraceRenormalises()
    {
        var (_, _, designer) = Open(Golden());
        designer.PlotPoints = 101;

        Assert.Equal(2, designer.MagnitudePlot.Traces.Count);
        Assert.Equal(2, designer.PhasePlot.Traces.Count);
        Assert.All(designer.MagnitudePlot.Traces, t => Assert.False(t.Z0OverrideEnabled));
        Assert.All(designer.PhasePlot.Traces, t => Assert.False(t.Z0OverrideEnabled));

        // The two ports genuinely differ, and every trace records that rather than flattening it.
        var net = designer.Rebuild!.Network!;
        foreach (var t in designer.MagnitudePlot.Traces)
        {
            Assert.NotNull(t.SourceZ0PerPort);
            Assert.Equal(net.R1, t.SourceZ0PerPort![0].Real, 6);
            Assert.Equal(net.R2, t.SourceZ0PerPort[1].Real, 6);
        }

        Assert.Equal(DependentVarFormat.Db, designer.MagnitudePlot.Traces[0].YAxis);
        Assert.Equal(DependentVarFormat.Phase, designer.PhasePlot.Traces[0].YAxis);
        Assert.Equal("GroupDelay", designer.PhasePlot.Traces[1].CubeName);
        Assert.True(designer.PhasePlot.Traces[1].UseSecondaryAxis);

        designer.Dispose();
    }
}
