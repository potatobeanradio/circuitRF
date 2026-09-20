// ================================================================
//  RailRemovalTests.cs — brief-railrf-19-unreachable-states.md §1
//
//  NOTHING IN THIS WINDOW MAY BECOME UNREACHABLE.
//
//  A rail could be added and never removed. PickRail adds one; no command, no menu row, no context
//  menu and no keystroke took one back. A rail added by mistake states no reference layer, and the
//  run gate's UnreferencedRail branch — which is CORRECT, because the rail set is solved together —
//  then refused Run, Accuracy, the plane solve, Compare and Export for the life of the document,
//  naming the one remedy the user does not want. A first-time designer hit it within minutes of
//  opening the shipped example.
//
//  So: the removal, the refusal that names BOTH doors, and the reference return that is MEASURED
//  from the artwork rather than matched by name.
//
//  ── WHY THE NAME RULES ARE TESTED AS ABSENT ───────────────────────────────────────────────────
//
//  The obvious fix is to drop GND from the pick list, and R-rail19-1c refuses two forms of it. The
//  synthetic board below is the case both of them fail on: its plane layer is called "Inner 2" and
//  its return net is called "RTN", so neither a net-name rule nor a layer-name rule finds it and
//  only the measurement does. The shipped four-layer technology really does call its planes
//  "Inner 1" and "Inner 2", which is why that spelling and not an invented one.
//
//  One test per CLAIM the brief makes.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRemovalTests
{
    // ══ R-rail19-1a — the trap, and the door out of it ═══════════════════════════════════════════

    /// <summary>
    /// <b>Picking a second net kills Run; removing the rail brings it back.</b>
    /// </summary>
    /// <remarks>
    /// Driven on the shipped example because that is the document the report is about, and the whole
    /// sequence is the reported one: open it, make a rail of the other net on the board, watch Run go
    /// out. The second half — <c>RemoveRailCommand</c> — <b>had nothing to call at HEAD.</b>
    /// </remarks>
    [Fact]
    public void AddingARailByMistakeKillsRun_AndRemovingItBringsRunBack()
    {
        var vm = Example();

        Assert.True(vm.CanRun, vm.RunBlockedReason);
        var before = vm.Rails.ToArray();

        // The reported mis-click, through the same method the pick button calls.
        vm.PickRail("GND");

        Assert.False(vm.CanRun);
        Assert.Contains("GND", vm.Rails);

        // And it is not only the rail that is showing: going back to the rail that IS configured
        // still refuses, because the rail set is solved together. That is the sentence the designer
        // was left with and could not act on.
        vm.SelectedRailName = "+3V3";
        Assert.False(vm.CanRun);
        Assert.Contains("'GND' states no reference layer", vm.RunBlockedReason, StringComparison.Ordinal);

        vm.SelectedRailName = "GND";
        Assert.True(vm.RemoveRailCommand.CanExecute(null));
        vm.RemoveRailCommand.Execute(null);

        Assert.Equal(before, vm.Rails.ToArray());
        Assert.True(vm.CanRun, vm.RunBlockedReason);
    }

    /// <summary>
    /// <b>Removing a rail removes its sources, loads, targets and aggressors with it.</b>
    /// </summary>
    /// <remarks>
    /// They are the rail's and nothing else references them, so this is a property of the document
    /// rather than of a cleanup pass — which is exactly why it is worth pinning: a later change that
    /// emptied a rail instead of dropping it would leave a rail the solve order still has to place,
    /// and every assertion here would still read as though it had been removed.
    /// </remarks>
    [Fact]
    public void RemovingARailTakesItsSourcesLoadsTargetsAndAggressorsWithIt()
    {
        var vm = Example();

        var rail = vm.Document.Rails.Single();
        Assert.NotEmpty(rail.Sources);
        Assert.NotEmpty(rail.Loads);
        Assert.NotEmpty(rail.Aggressors);
        Assert.NotNull(rail.ImpedanceTarget);

        vm.RemoveRailCommand.Execute(null);

        Assert.Empty(vm.Document.Rails);
        Assert.Empty(vm.Rails);
        Assert.Empty(vm.Sources);
        Assert.Empty(vm.Loads);
        Assert.Empty(vm.Aggressors);
        Assert.Empty(vm.Parts);
        Assert.False(vm.RemoveRailCommand.CanExecute(null));
    }

    // ══ R-rail19-1b — the refusal names BOTH doors ═══════════════════════════════════════════════

    /// <summary>
    /// <b>The unreferenced-rail sentence offers the reference AND the removal.</b>
    /// </summary>
    /// <remarks>
    /// At HEAD it named one exit, and it is the wrong one for the user it is usually shown to: a
    /// rail added by mistake is one they want gone, not one they want to give a reference to. A
    /// refusal that names one of two exits traps whoever wanted the other.
    /// </remarks>
    [Fact]
    public void TheUnreferencedRailRefusalNamesBothRemedies()
    {
        var vm = Example();
        vm.PickRail("GND");
        vm.SelectedRailName = "+3V3";

        string why = vm.RunBlockedReason;

        Assert.Contains("confirm its reference", why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remove it", why, StringComparison.OrdinalIgnoreCase);
    }

    // ══ R-rail19-1c — the reference return is MEASURED, never named ══════════════════════════════

    /// <summary>
    /// <b>The reference return is identified from the artwork, on a board where every name rule
    /// fails.</b>
    /// </summary>
    /// <remarks>
    /// Two boards, and the second is the point. On the shipped example the answer is <c>GND</c>,
    /// which a name rule would also have reached — so it proves the measurement runs, not that it
    /// is needed. The synthetic board's plane layer is called <b>Inner 2</b> and its return net
    /// <b>RTN</b>: no net-name rule and no layer-name rule finds it, and the measurement does.
    ///
    /// <para>What makes it a measurement rather than a count is galvanic ambiguity. The rail pad on
    /// this board sits directly over the plane — as almost every pad on a real board does — so a
    /// containment count would credit the RAIL with being on the reference layer. It covers two
    /// pieces of copper that are not joined to each other, so it is skipped; the return's pad is
    /// joined to the plane by its via, so it counts.</para>
    /// </remarks>
    [Fact]
    public void TheReferenceReturnIsMeasuredFromTheCopper_NotMatchedByName()
    {
        Assert.Equal("GND", Example().ReferenceReturnNet);

        var tech = SplitNameBoard();
        var regions = PdnMeshExtractor.BuildLayerRegions(SplitNameShapes(), tech);

        // The rail's pad sits over the plane and the return's pad is stitched to it.
        List<PdnNetPoint> points =
        [
            new("VBUS", Mm(2), Mm(0.5)),
            new("RTN",  Mm(15.5), Mm(3.5)),
        ];

        Assert.Equal("RTN", PdnRailRegions.ReferenceNetOn(regions, tech, points, Inner2));

        // The half that says this is not a coincidence: neither name is suggestive, and the plane
        // layer's own name is not what was read.
        Assert.Equal("Inner 2", tech.Layers.Single(l => l.Key == Inner2).Name);
    }

    // ══ R-rail19-1d — flagged after confirmation, refused when picked, never filtered ════════════

    /// <summary>
    /// <b>The reference return is marked only once the reference is confirmed, picking it is
    /// refused with a sentence and a remedy, and it is never taken out of the list.</b>
    /// </summary>
    /// <remarks>
    /// All three halves are one decision. Before the confirmation railRF does not know which net the
    /// return is and must not act as though it did. After it, the row is MARKED rather than removed:
    /// a user who cannot find <c>GND</c> in a list of every net on the board and is told nothing is
    /// in exactly the position the dead Run button put him in — a row that says why is an answer, a
    /// missing row is a second mystery.
    /// </remarks>
    [Fact]
    public void TheReferenceReturnIsMarkedAfterConfirmation_RefusedWhenPicked_AndNeverFiltered()
    {
        var vm = Example();
        var gnd = vm.AvailableNets.Single(r => r.Name == "GND");

        // Unconfirmed: nothing is marked, because nothing has been measured against anything.
        vm.RemoveRailCommand.Execute(null);                 // no rails, so no confirmed reference
        Assert.False(vm.IsReferenceConfirmed);
        Assert.Null(vm.ReferenceReturnNet);
        Assert.All(vm.AvailableNets, r => Assert.False(r.IsReferenceReturn));

        // Confirmed: it is marked, and it is still in the list and still selectable.
        vm.PickRail("+3V3");
        vm.ConfirmReferenceCommand.Execute(null);

        Assert.True(vm.IsReferenceConfirmed);
        Assert.True(gnd.IsReferenceReturn);
        Assert.Equal("the reference return", gnd.Mark);
        Assert.Contains(vm.AvailableNets, r => r.Name == "GND");    // the filter that must not exist

        vm.SelectNet("GND");
        Assert.True(vm.PickSelectedNetCommand.CanExecute(null));

        vm.PickSelectedNetCommand.Execute(null);

        // Refused, with the sentence and the remedy — and NO rail was added.
        Assert.DoesNotContain("GND", vm.Rails);
        Assert.NotNull(vm.Refusal);
        Assert.Contains("the reference return", vm.Refusal!.Sentence, StringComparison.Ordinal);
        Assert.Contains("Pick the power net instead", vm.Refusal!.Sentence, StringComparison.Ordinal);
        Assert.True(vm.IsReferenceLayerFlagged, "the refusal names no control, so nothing turns red.");
    }

    // ── fixtures ───────────────────────────────────────────────────────────────────────────────

    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        Assert.Empty(vm.LoadDocumentReferences());
        vm.RebuildParts();
        return vm;
    }

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopLayer = new(1, 0);
    private static readonly LayerKey Inner2 = new(2, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>A board whose return net and plane layer are both named unsuggestively.</summary>
    private static Technology SplitNameBoard()
    {
        var tech = new Technology { Name = "unsuggestive" };
        tech.Layers =
        [
            new LayerDef { Key = TopLayer, Name = "TOP" },
            new LayerDef { Key = Inner2,   Name = "Inner 2" },
            new LayerDef { Key = ViaLayer, Name = "Via" },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [TopLayer],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Mm(0.2), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "Inner 2",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Inner2],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "Inner 2",
            },
        ];
        return tech;
    }

    private static IReadOnlyList<LayoutShape> SplitNameShapes() =>
    [
        // The plane, under everything.
        Rect(Inner2, Mm(0), Mm(-5), Mm(20), Mm(5)),

        // The rail's trace, ON TOP OF THE PLANE and joined to it by nothing.
        Rect(TopLayer, Mm(0), Mm(0), Mm(20), Mm(1)),

        // The return's land, off the trace, stitched down to the plane.
        Rect(TopLayer, Mm(15), Mm(3), Mm(16), Mm(4)),
        Rect(ViaLayer, Mm(15.4), Mm(3.4), Mm(15.6), Mm(3.6)),
    ];

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
