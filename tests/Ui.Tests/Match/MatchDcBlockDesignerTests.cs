// ================================================================
//  MatchDcBlockDesignerTests.cs — MN-DCB in the Match Designer (match.md §22.5).
//
//  What the owner sees, and nothing else: the toggle's enabled state and its disabled reasons (MN-DCB2:
//  the toggle follows the DC PATH — a series-RC end is enabled, a real series capacitor is named),
//  the seed and the shadowed value a re-check restores, the inline edit of L1blk and its undo, the
//  status line's own numbers and warn class, and where the block capacitor is drawn.
//
//  Same discipline as the Match rounds: view-model, geometry and source-scan tests, never pixels.
// ================================================================

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchDcBlockDesignerTests(ITestOutputHelper output)
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open(MatchDesign design)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "Design").Expression = MatchEmbedding.Encode(design);

        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    /// <summary>The drain network of match.md §22 — 4 Ω ‖ 30 pF into 50 Ω, shunt-inductor end at 1.</summary>
    private static MatchDesign Drain(double qAdjust = 0.0) => new()
    {
        F1 = 1.8e9, F2 = 2.2e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        QAdjust = qAdjust,
        Term1 = new Termination(4.0, ReactanceKind.C, TerminationTopology.Parallel, 30e-12),
        Term2 = Termination.Resistive(50.0),
    };

    /// <summary>match.md §4.9's interstage design — termination 2's end arm is a SERIES arm.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    /// <summary>A lowpass ladder — no shunt inductor anywhere, so neither end can carry a block.</summary>
    private static MatchDesign Lowpass() => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = 3, Form = NetworkForm.Lowpass,
        AnalysisEnd = AnalysisEndChoice.Term1,
        Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12),
        Term2 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 5e-12),
    };

    // ── 9. The toggle follows the current rebuild ─────────────────────────────

    /// <summary>
    /// <b>Enabled exactly when a real shunt inductor lies on this end's DC path in the network as it
    /// now stands</b> (MN-DCB2, match.md §22.1) — and when none does, the tooltip names WHICH
    /// reason. §4.9's termination 2 is a series-RC end, a FET input: under MN-DCB the toggle was
    /// disabled there with "its capacitor already blocks DC", which was false — the capacitor is the
    /// device's own, absorbed, not on the board — so it is now ENABLED, naming the host (L3) and the
    /// series inductor the bias reaches it through (L4). A lowpass ladder passes DC end to end and
    /// stays disabled at both ends for the form's own reason.
    /// </summary>
    [Fact]
    public void TheToggle_IsEnabledWhereAShuntInductorLiesOnTheDcPath_AndNamesTheReasonOtherwise()
    {
        var (_, _, bandpass) = Open(Golden());
        Assert.True(bandpass.Term1.DcBlockEnabled);
        Assert.Contains("in series with this end's shunt inductor",
                        bandpass.Term1.DcBlockTooltip, StringComparison.Ordinal);

        Assert.True(bandpass.Term2.DcBlockEnabled);
        Assert.Contains("in series with L3, the first shunt inductor on this end's DC path",
                        bandpass.Term2.DcBlockTooltip, StringComparison.Ordinal);
        Assert.Contains("reached through L4 — a series inductor passes DC",
                        bandpass.Term2.DcBlockTooltip, StringComparison.Ordinal);
        Assert.Contains("L3 is enlarged", bandpass.Term2.DcBlockTooltip, StringComparison.Ordinal);

        foreach (var t in new[] { bandpass.Term1, bandpass.Term2 })
        {
            Assert.DoesNotContain("already blocks DC", t.DcBlockTooltip, StringComparison.Ordinal);
            Assert.DoesNotContain("series arm", t.DcBlockTooltip, StringComparison.Ordinal);
        }

        var (_, _, lowpass) = Open(Lowpass());
        Assert.Null(lowpass.Rebuild?.Refusal);
        Assert.False(lowpass.Term1.DcBlockEnabled);
        Assert.False(lowpass.Term2.DcBlockEnabled);
        foreach (var t in new[] { lowpass.Term1, lowpass.Term2 })
        {
            Assert.Contains("lowpass ladder passes DC end to end", t.DcBlockTooltip, StringComparison.Ordinal);
            Assert.Contains("not offered here", t.DcBlockTooltip, StringComparison.Ordinal);
        }

        output.WriteLine("term 2 (series-RC): " + bandpass.Term2.DcBlockTooltip);
        output.WriteLine("lowpass:            " + lowpass.Term1.DcBlockTooltip);
    }

    /// <summary>
    /// <b>A real series capacitor in the through path isolates the end, and the toggle says so</b> —
    /// naming the capacitor and where the bias has to be fed instead (match.md §22.1, fact 5). A
    /// series-C termination whose Q is far below the synthesis Q grows a real <c>CFano</c> outward
    /// of its own absorbed capacitor; a block on L3 beyond it would protect nothing.
    /// </summary>
    [Fact]
    public void ARealSeriesCapacitorEnd_DisablesTheToggle_AndNamesTheCapacitorAndTheFeed()
    {
        var design = Golden();
        design.Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 100e-12);
        var (_, _, vm) = Open(design);
        Assert.Null(vm.Rebuild?.Refusal);
        Assert.Contains(vm.Rebuild!.Network!.Elements, e => e.Name == "CFano" && !e.IsShunt);

        Assert.False(vm.Term2.DcBlockEnabled);
        Assert.Contains("CFano is a real capacitor in this end's through path and already isolates it from DC",
                        vm.Term2.DcBlockTooltip, StringComparison.Ordinal);
        Assert.Contains("feed its bias on the termination's side of CFano",
                        vm.Term2.DcBlockTooltip, StringComparison.Ordinal);
        Assert.True(vm.Term1.DcBlockEnabled);

        // Toggling it on stores the value and applies nothing; the line says why.
        vm.SetDcBlock(2, 1e-9);
        var line = Assert.Single(vm.Status.DcBlocks);
        Assert.Contains("CFano", line.Text, StringComparison.Ordinal);
        Assert.Contains("Stored, not applied", line.Text, StringComparison.Ordinal);
        Assert.All(vm.Rebuild!.Network!.Elements, e => Assert.Equal(0.0, e.DcBlock));
        output.WriteLine(vm.Term2.DcBlockTooltip);
    }

    /// <summary>
    /// <b>π ↔ T on the end pair keeps the block and the user's value.</b> The value is on the design
    /// and the host is re-resolved each rebuild, so a T on (L1, L2) moves the block to the T's shunt
    /// product — reached through its first series product — and a π back puts it on the end node
    /// again, with the design's number untouched throughout. One undo per step.
    /// </summary>
    [Fact]
    public void SwitchingTheEndPairBetweenPiAndT_MovesTheHostAndKeepsTheValue()
    {
        var (vmSchematic, _, vm) = Open(Drain());
        vm.Term1.HasDcBlock = true;
        vm.SetDcBlock(1, 470e-12);
        Assert.Equal("L1", vm.Rebuild!.DcBlocks[0].ElementName);

        vm.AddTransform(vm.AvailablePairs().First(p => p.Display == "L1 / L2"));
        // A π of inductors has TWO shunt products on this end's DC path, and both are blocked.
        Assert.Equal(2, vm.Rebuild!.DcBlocks.Count);
        var pi = vm.Rebuild!.DcBlocks[0];
        Assert.True(pi.Applied, pi.Reason);
        Assert.Equal("L1_N1_1", pi.ElementName);
        Assert.Empty(pi.Path);
        Assert.Equal("L1_N1_3", vm.Rebuild!.DcBlocks[1].ElementName);
        Assert.Equal(["L1_N1_2"], vm.Rebuild!.DcBlocks[1].Path);
        AssertSame(470e-12, vm.Design.Term1DcBlock);
        Assert.True(vm.Term1.HasDcBlock);
        Assert.Contains("each of L1_N1_1 and L1_N1_3", vm.Term1.DcBlockTooltip, StringComparison.Ordinal);
        Assert.Equal(2, vm.Status.DcBlocks.Count);

        vm.SetTransformForm(0, TransformForm.T);
        var t = Assert.Single(vm.Rebuild!.DcBlocks);
        Assert.True(t.Applied, t.Reason);
        Assert.Equal("L1_N1_2", t.ElementName);
        Assert.Equal(["L1_N1_1"], t.Path);
        AssertSame(470e-12, vm.Design.Term1DcBlock);
        Assert.True(vm.Term1.HasDcBlock);
        Assert.True(vm.Term1.DcBlockEnabled);
        var tLine = Assert.Single(vm.Status.DcBlocks);
        Assert.Contains("in series with L1_N1_2", tLine.Text, StringComparison.Ordinal);
        Assert.Contains("reaches L1_N1_2 through L1_N1_1", tLine.Text, StringComparison.Ordinal);
        output.WriteLine(tLine.Text);

        vm.SetTransformForm(0, TransformForm.Pi);
        Assert.Equal(2, vm.Rebuild!.DcBlocks.Count);
        Assert.Equal("L1_N1_1", vm.Rebuild!.DcBlocks[0].ElementName);
        Assert.Empty(vm.Rebuild!.DcBlocks[0].Path);
        AssertSame(470e-12, vm.Design.Term1DcBlock);

        // One undo per step: π → T → (add) → 470 pF → seed.
        vmSchematic.UndoRedo.Undo();
        Assert.Equal("L1_N1_2", vm.Rebuild!.DcBlocks[0].ElementName);
        vmSchematic.UndoRedo.Undo();
        Assert.Equal("L1_N1_1", vm.Rebuild!.DcBlocks[0].ElementName);
        vmSchematic.UndoRedo.Undo();
        Assert.Equal("L1", vm.Rebuild!.DcBlocks[0].ElementName);
        AssertSame(470e-12, vm.Design.Term1DcBlock);
    }

    /// <summary>
    /// <b>The owner's π of inductors: two shunt inductors on one end's DC path, two blocks</b>
    /// (2026-08-28: "there's only one placed"). After a Norton π on (L1, L2) the Term1 path is
    /// shunt L1_N1_1 / series L1_N1_2 / shunt L1_N1_3 / C2, and the series product passes DC — so
    /// both shunt products are blocked with the one value, each drawn under its own inductor in its
    /// own column with its own ground, both flattened, and both in the copy.
    /// </summary>
    [Fact]
    public void APiOfInductors_BlocksBothShuntProducts_DrawsFlattensAndCopiesBoth()
    {
        var (_, _, vm) = Open(Drain());
        vm.AddTransform(vm.AvailablePairs().First(p => p.Display == "L1 / L2"));
        vm.Term1.HasDcBlock = true;

        Assert.Equal(2, vm.Rebuild!.DcBlocks.Count);
        Assert.All(vm.Rebuild!.DcBlocks, n => Assert.True(n.Applied, n.Reason));
        var hosts = vm.Rebuild!.DcBlocks.Select(n => n.ElementName).ToList();
        Assert.Equal(["L1_N1_1", "L1_N1_3"], hosts);

        // The seed is sized from the SMALLER host, so neither branch resonates above f₀/10.
        double om0 = vm.Design.Omega0;
        double smallest = vm.Rebuild!.DcBlocks.Min(n => n.InductanceBefore);
        AssertSame(MatchDcBlock.DefaultFor(smallest, om0, vm.Settings.DcBlockMaxFarads), vm.Design.Term1DcBlock);
        Assert.All(vm.Rebuild!.DcBlocks, n => Assert.True(n.SeriesResonanceHz <= om0 / (2 * Math.PI) / 10.0 * 1.0001));

        // Drawn: each block under its own host, in that host's column, each with its own ground.
        foreach (string host in hosts)
        {
            var inductor = vm.Ladder.Elements.Single(e => e.Name == host);
            var block = vm.Ladder.Elements.Single(e => e.Name == MatchDcBlock.BlockName(host));
            Assert.Equal(MatchElementRole.DcBlock, block.Role);
            Assert.Equal(inductor.X, block.X, 9);
            Assert.Equal(MatchLadderLayout.BlockY, block.Y, 9);
            Assert.Null(MatchLadderLayout.GroundYFor(vm.Ladder, inductor));
            Assert.NotNull(MatchLadderLayout.GroundYFor(vm.Ladder, block));
        }
        Assert.Equal(2, vm.Status.DcBlocks.Count);
        Assert.Contains("reaches L1_N1_3 through L1_N1_2", vm.Status.DcBlocks[1].Text, StringComparison.Ordinal);

        // Flattened: both blocks, both hosts compensated.
        var cell = MatchFlatten.BuildSchematic(vm.Rebuild!, vm.Design, "MN1", DateTime.UtcNow);
        foreach (string host in hosts)
        {
            Assert.Contains(cell.Components, c => c.InstanceName == host);
            Assert.Contains(cell.Components, c => c.InstanceName == MatchDcBlock.BlockName(host));
        }

        // Copied: one ground per shunt column, under the block where there is one.
        var copy = MatchSchematicCopy.Build(vm.Ladder);
        foreach (string host in hosts)
        {
            var block = vm.Ladder.Elements.Single(e => e.Name == MatchDcBlock.BlockName(host));
            var g = Assert.Single(copy.Components, c => c.Symbol == SymbolKind.Ground && Math.Abs(c.X - block.X) < 1e-9);
            Assert.Equal(block.Y + MatchSchematicGeometry.LeadHalf, g.Y, 9);
        }

        // Typing 0 into EITHER block clears the end's one value, and both go.
        var target = vm.ResolveInlineEdit(MatchDcBlock.BlockName("L1_N1_3"));
        Assert.NotNull(target);
        Assert.True(vm.CommitInlineEdit(target!, "0"));
        Assert.Empty(vm.Rebuild!.DcBlocks);
        Assert.False(vm.Term1.HasDcBlock);

        output.WriteLine(string.Join("\n", vm.Status.DcBlocks.Select(l => l.Text)));
    }

    /// <summary>
    /// <b>The status line names the route when the host is not on the end node</b> (match.md §22.3's
    /// feed rule, said for the topology the user actually has): the DC path from termination 2
    /// reaches L3 through L4, and the bias fed through L3 reaches the termination through L4.
    /// </summary>
    [Fact]
    public void TheStatusLine_NamesTheRoute_WhenTheHostIsOneSeriesInductorIn()
    {
        var (_, _, vm) = Open(Golden());
        vm.SetDcBlock(2, 1e-9);

        var note = Assert.Single(vm.Rebuild!.DcBlocks);
        Assert.True(note.Applied, note.Reason);
        var line = Assert.Single(vm.Status.DcBlocks);
        Assert.Equal(2, line.End);
        Assert.Contains("DC block at termination 2:", line.Text, StringComparison.Ordinal);
        Assert.Contains("in series with L3 (", line.Text, StringComparison.Ordinal);
        Assert.Contains(") — the DC path from termination 2 reaches L3 through L4; branch resonates at",
                        line.Text, StringComparison.Ordinal);
        Assert.Contains("Feed the bias through L3; it reaches the termination through L4, not through a separate choke.",
                        line.Text, StringComparison.Ordinal);
        output.WriteLine(line.Text);

        // An end-node host still reads the plain sentence.
        vm.SetDcBlock(1, 1e-9);
        var plain = vm.Status.DcBlocks.Single(l => l.End == 1);
        Assert.DoesNotContain("the DC path from", plain.Text, StringComparison.Ordinal);
        Assert.Contains("Feed the bias through L1, not through a separate choke.", plain.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The block draws under an interior host, in that host's column</b>, and the end column's
    /// ground is where it always was. <c>GroundYFor</c> is per column, so nothing about an interior
    /// host needed a new rule — this pins that it did not.
    /// </summary>
    [Fact]
    public void TheDrawing_PutsTheBlockUnderAnInteriorHost_AndLeavesTheEndColumnAlone()
    {
        var (_, _, vm) = Open(Golden());
        vm.Term2.HasDcBlock = true;

        string host = vm.Rebuild!.DcBlocks[0].ElementName;
        Assert.Equal("L3", host);
        var inductor = vm.Ladder.Elements.Single(e => e.Name == host);
        var block = vm.Ladder.Elements.Single(e => e.Name == MatchDcBlock.BlockName(host));
        var end = vm.Ladder.Elements.Single(e => e.Name == "L1");

        Assert.Equal(MatchElementRole.DcBlock, block.Role);
        Assert.Equal(2, block.DcBlockEnd);
        Assert.Equal(inductor.X, block.X, 9);
        Assert.True(block.Y > inductor.Y);
        Assert.Equal(MatchLadderLayout.BlockY, block.Y, 9);
        Assert.True(inductor.X > end.X, "the host is an interior column, right of the end column");

        Assert.Null(MatchLadderLayout.GroundYFor(vm.Ladder, inductor));
        Assert.Equal(block.Y + 200.0, MatchLadderLayout.GroundYFor(vm.Ladder, block)!.Value, 9);
        Assert.Equal(MatchLadderLayout.ShuntGroundY, MatchLadderLayout.GroundYFor(vm.Ladder, end)!.Value, 9);

        var schematic = MatchSchematicModel.Build(vm.Ladder);
        Assert.Contains(schematic.Components, c => c.Id == block.Name + MatchSchematicModel.GroundIdSuffix);
        Assert.DoesNotContain(schematic.Components, c => c.Id == host + MatchSchematicModel.GroundIdSuffix);
        Assert.Contains(schematic.Components, c => c.Id == "L1" + MatchSchematicModel.GroundIdSuffix);
        Assert.Single(schematic.ConnectionDots.Where(d => Math.Abs(d.X - inductor.X) < 1e-9));

        output.WriteLine($"{host} at ({inductor.X:0}, {inductor.Y:0}); {block.Name} at ({block.X:0}, {block.Y:0}); "
                         + $"L1 at ({end.X:0}, {end.Y:0}) grounded at {MatchLadderLayout.GroundYFor(vm.Ladder, end):0}");
    }

    /// <summary>
    /// <b>The flattened cell of the series-RC fixture</b> carries the host compensated, its block,
    /// and the absorbed termination capacitor DISABLED — and the network pane lists the same live
    /// elements the cell writes.
    /// </summary>
    [Fact]
    public void TheFlattenedCell_OfASeriesRcEnd_CarriesTheCompensatedHostItsBlock_AndTheAbsorbedCDisabled()
    {
        var (_, _, vm) = Open(Golden());
        vm.Term2.HasDcBlock = true;
        var note = Assert.Single(vm.Rebuild!.DcBlocks);
        string host = note.ElementName;
        Assert.Equal("L3", host);

        var cell = MatchFlatten.BuildSchematic(vm.Rebuild!, vm.Design, "MN1", DateTime.UtcNow);
        var l3 = cell.Components.Single(c => c.InstanceName == host);
        var blk = cell.Components.Single(c => c.InstanceName == MatchDcBlock.BlockName(host));
        var c4 = cell.Components.Single(c => c.InstanceName == "C4");
        Assert.Equal(DisableState.None, l3.Disable);
        Assert.Equal(DisableState.None, blk.Disable);
        Assert.Equal(DisableState.Open, c4.Disable);

        var l3Value = vm.Rebuild!.Network!.Elements.Single(e => e.Name == host);
        Assert.True(l3Value.Value > note.InductanceBefore);
        Assert.Equal(note.InductanceAfter, l3Value.Value, 15);

        // The dialog's element list is the network pane's: the same live names, block included.
        var live = vm.Elements.Select(r => r.Instance).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(host, live);
        Assert.Contains(MatchDcBlock.BlockName(host), live);
        Assert.Equal("DC block", vm.Elements.Single(r => r.Instance == MatchDcBlock.BlockName(host)).Note);
        foreach (var c in cell.Components.Where(c => c.Disable == DisableState.None && (c.InstanceName.StartsWith('L') || c.InstanceName.StartsWith('C'))))
            Assert.Contains(c.InstanceName, live);

        string text = string.Join("\n", cell.CanvasObjects.OfType<EditableText>().Select(t => t.Text));
        Assert.Contains("DC block at termination 2", text, StringComparison.Ordinal);
        Assert.Contains("reaches L3 through L4", text, StringComparison.Ordinal);
        output.WriteLine(text.Split('\n').First(l => l.Contains("DC block", StringComparison.Ordinal)));
    }

    // ── 10. Check, uncheck, re-check, and the cap ─────────────────────────────

    /// <summary>
    /// <b>Check seeds f₀/10, uncheck stores 0, re-check restores what the user had.</b> The shadowed
    /// value lives on the DESIGNER, never on the design — a design with no block holds 0, which is
    /// what "no block" is — so unchecking cannot leave a number in the file that nothing reads.
    /// </summary>
    [Fact]
    public void CheckSeedsTheDefault_UncheckStoresZero_AndReCheckRestoresTheUsersOwnValue()
    {
        var (_, _, vm) = Open(Drain());
        Assert.False(vm.Term1.HasDcBlock);
        Assert.Equal(0.0, vm.Design.Term1DcBlock);

        double om0 = vm.Design.Omega0;
        double l = MatchDcBlock.EndShuntInductor(vm.Rebuild!.Network, 1)!.Value;
        double expected = MatchDcBlock.DefaultFor(l, om0, vm.Settings.DcBlockMaxFarads);

        vm.Term1.HasDcBlock = true;
        Assert.True(vm.Term1.HasDcBlock);
        AssertSame(expected, vm.Design.Term1DcBlock);

        // f_s lands at f₀/√(1 + 100) — not f₀/10 exactly, because the compensation enlarges L too.
        var note = Assert.Single(vm.Rebuild!.DcBlocks);
        Assert.True(note.Applied);
        Assert.Equal(om0 / (2.0 * Math.PI) / Math.Sqrt(101.0), note.SeriesResonanceHz, 3);
        Assert.False(note.Warn);
        output.WriteLine($"seed {expected * 1e12:0.###} pF, f_s {note.SeriesResonanceHz / 1e6:0.#} MHz");

        // The user's own value, then off and back on.
        vm.SetDcBlock(1, 100e-12);
        AssertSame(100e-12, vm.Design.Term1DcBlock);

        vm.Term1.HasDcBlock = false;
        Assert.Equal(0.0, vm.Design.Term1DcBlock);
        Assert.False(vm.Term1.HasDcBlock);
        Assert.Empty(vm.Rebuild!.DcBlocks);

        vm.Term1.HasDcBlock = true;
        AssertSame(100e-12, vm.Design.Term1DcBlock);
    }

    /// <summary>
    /// <b>The cap bounds the SEED and nothing else</b> (owner, 2026-08-28: too big a capacitor can be
    /// impossible to build). A design whose f₀/10 value exceeds it seeds the cap; a typed 100 pF is
    /// accepted, shown with its own spread and warn class, and never clamped.
    /// </summary>
    [Fact]
    public void TheSeedIsCapped_ButATypedValueIsNeverClamped()
    {
        var (_, _, vm) = Open(Drain());
        double uncapped = MatchDcBlock.DefaultFor(
            MatchDcBlock.EndShuntInductor(vm.Rebuild!.Network, 1)!.Value, vm.Design.Omega0, 0.0);

        vm.Settings.DcBlockMaxFarads = 1e-9;
        Assert.True(uncapped > 1e-9, $"the fixture must exceed the cap to test it; f₀/10 wants {uncapped:E3} F");

        vm.Term1.HasDcBlock = true;
        AssertSame(1e-9, vm.Design.Term1DcBlock);

        // A typed value passes through untouched, however far above the cap it is...
        vm.SetDcBlock(1, 47e-9);
        AssertSame(47e-9, vm.Design.Term1DcBlock);

        // ...and however far below. 100 pF on this fixture puts f_s well above f₀/5, so it warns —
        // a hint, not a refusal: the compensation is still exact at ω₀.
        vm.SetDcBlock(1, 100e-12);
        AssertSame(100e-12, vm.Design.Term1DcBlock);

        var line = Assert.Single(vm.Status.DcBlocks);
        Assert.True(line.Warn);
        Assert.Contains("detune the band", line.Text, StringComparison.Ordinal);
        output.WriteLine(line.Text);
    }

    // ── 11. The inline edit ───────────────────────────────────────────────────

    /// <summary>
    /// <b><c>L1blk</c> is an element of the network pane like any other</b> — it has a row in the
    /// grid, a symbol in the drawing, and a double-click that edits it. What it writes is
    /// <c>Term1DcBlock</c>, through <c>Commit</c>, so ONE undo puts the previous value back from
    /// either window. Typing 0 removes the block and unchecks the toggle.
    /// </summary>
    [Fact]
    public void EditingTheBlockCapacitor_WritesTheDesign_AndOneUndoRestoresIt()
    {
        var (vmSchematic, _, vm) = Open(Drain());
        vm.Term1.HasDcBlock = true;
        double seeded = vm.Design.Term1DcBlock;

        string name = MatchDcBlock.BlockName(vm.Rebuild!.DcBlocks[0].ElementName);
        Assert.Contains(vm.Ladder.Elements, e => e.Name == name);
        Assert.Contains(vm.Elements, r => r.Instance == name);
        Assert.Equal("DC block", vm.Elements.Single(r => r.Instance == name).Note);

        var target = vm.ResolveInlineEdit(name);
        Assert.NotNull(target);
        Assert.Equal(MatchInlineEditKind.DcBlock, target!.Kind);
        Assert.Equal(1, target.End);
        Assert.Equal(MatchQuantity.Capacitance, target.Quantity);

        Assert.True(vm.CommitInlineEdit(target, "470 pF"));
        AssertSame(470e-12, vm.Design.Term1DcBlock);
        Assert.Equal("", vm.InlineEditNote);

        // ONE undo, on the owning schematic's own stack.
        vmSchematic.UndoRedo.Undo();
        AssertSame(seeded, vm.Design.Term1DcBlock);

        // Typing 0 clears the block, which unchecks the toggle — there is no second copy of the state.
        var again = vm.ResolveInlineEdit(MatchDcBlock.BlockName(vm.Rebuild!.DcBlocks[0].ElementName));
        Assert.True(vm.CommitInlineEdit(again!, "0"));
        Assert.Equal(0.0, vm.Design.Term1DcBlock);
        Assert.False(vm.Term1.HasDcBlock);
        Assert.Empty(vm.Rebuild!.DcBlocks);
    }

    /// <summary>
    /// The HOST inductor is still an ordinary element the transform rack aims at — the block did not
    /// turn it into a specification input beside it.
    /// </summary>
    [Fact]
    public void TheHostInductor_IsStillAnOrdinaryElement()
    {
        var (_, _, vm) = Open(Drain());
        vm.Term1.HasDcBlock = true;
        string host = vm.Rebuild!.DcBlocks[0].ElementName;

        var target = vm.ResolveInlineEdit(host);
        Assert.NotNull(target);
        Assert.Equal(MatchInlineEditKind.ElementValue, target!.Kind);
        Assert.Equal(MatchQuantity.Inductance, target.Quantity);
    }

    // ── 12. The status line ───────────────────────────────────────────────────

    /// <summary>
    /// <b>match.md §22.2's own 500 pF row, as the strip renders it.</b> f_s 672 MHz, the compensated
    /// inductor 112.3 pH from 99.5 pH, and the warn class — with the feed-through rule, which is
    /// stated here because nothing else on screen would say it (§22.3). At 10 nF the same design
    /// carries no warning.
    /// </summary>
    [Fact]
    public void TheStatusLine_QuotesSection22_2sNumbers_AndWarnsBelowF0Over5()
    {
        // QAdjust chosen so the end inductor is §22.2's own 99.5 pH — the number every figure in that
        // row is computed from. Found by bisection rather than quoted, so a change to the synthesis
        // moves the fixture instead of silently invalidating the comparison.
        var (_, _, probe) = Open(Drain());
        double q = Bisect(probe);
        var (_, _, vm) = Open(Drain(q));

        double l = MatchDcBlock.EndShuntInductor(vm.Rebuild!.Network, 1)!.Value;
        Assert.Equal(99.5, l * 1e12, 1);

        vm.Settings.SignificantDigits = 4;
        vm.SetDcBlock(1, 500e-12);

        var note = Assert.Single(vm.Rebuild!.DcBlocks);
        Assert.Equal(112.29, note.InductanceAfter * 1e12, 1);
        Assert.Equal(671.7, note.SeriesResonanceHz / 1e6, 0);
        Assert.True(note.Warn);

        var line = Assert.Single(vm.Status.DcBlocks);
        output.WriteLine(line.Text);
        Assert.True(line.Warn);
        Assert.Contains("DC block at termination 1", line.Text, StringComparison.Ordinal);
        Assert.Contains("500 pF", line.Text, StringComparison.Ordinal);
        Assert.Contains("112.3 pH", line.Text, StringComparison.Ordinal);
        Assert.Contains("from 99.5", line.Text, StringComparison.Ordinal);
        Assert.Contains("671.7 MHz", line.Text, StringComparison.Ordinal);
        Assert.Contains("Feed the bias through", line.Text, StringComparison.Ordinal);
        Assert.Contains("not through a separate choke", line.Text, StringComparison.Ordinal);

        // ── The spread §22.2 quotes is the second-order ESTIMATE ──────────────
        //
        // The section prints ±2.3 % beside L_eff values that run −2.9 % / +2.3 %; the exact half range
        // is ±2.6 %, and that is what the line states. See src/Core/Match/RESOLVED.md §MN-DCB.
        Assert.Contains("±2.6 %", line.Text, StringComparison.Ordinal);
        Assert.InRange(note.BandSpread, 0.0259, 0.0261);

        // At 10 nF the same design says the same things without warning.
        vm.SetDcBlock(1, 10e-9);
        var quiet = Assert.Single(vm.Status.DcBlocks);
        Assert.False(quiet.Warn);
        Assert.DoesNotContain("detune the band", quiet.Text, StringComparison.Ordinal);
        output.WriteLine(quiet.Text);

        static double Bisect(MatchDesignerViewModel probe)
        {
            double lo = 2.0, hi = 6.0;
            for (int i = 0; i < 60; i++)
            {
                double m = 0.5 * (lo + hi);
                var r = MatchRebuild.Rebuild(Drain(m));
                double v = MatchDcBlock.EndShuntInductor(r.Network, 1)?.Value ?? 0.0;
                if (v > 99.5e-12) lo = m; else hi = m;
            }
            _ = probe;
            return 0.5 * (lo + hi);
        }
    }

    /// <summary>
    /// <b>Stored, not applied, is a LINE and not a refusal.</b> An end whose DC path has no host
    /// keeps the value the user typed — they may be mid-way through changing the order or the form —
    /// and says so in the same place an active block reports. (MN-DCB2: §4.9's own termination 2
    /// now HAS a host, so the fixture is the lowpass ladder, which has none anywhere.)
    /// </summary>
    [Fact]
    public void AnEndWithNowhereToPutABlock_SaysSoInTheSamePlace()
    {
        var design = Lowpass();
        design.Term2DcBlock = 1e-9;
        var (_, _, vm) = Open(design);

        Assert.Null(vm.Status.Refusal);
        var line = Assert.Single(vm.Status.DcBlocks);
        Assert.Equal(2, line.End);
        Assert.False(line.Warn);
        Assert.Contains("stored, not applied", line.Text, StringComparison.Ordinal);
        AssertSame(1e-9, vm.Design.Term2DcBlock);
        output.WriteLine(line.Text);
    }

    // ── 13. The drawing ───────────────────────────────────────────────────────

    /// <summary>
    /// <b><c>L1blk</c> sits UNDER <c>L1</c> in the same column, in the block role, and the arm's one
    /// ground moves down under it.</b> The two are one shunt arm, so there is one column, one junction
    /// dot on the spine and one <c>Ground</c> — not two of each.
    /// </summary>
    [Fact]
    public void TheDrawing_PutsTheBlockUnderItsInductorInOneColumn()
    {
        var (_, _, vm) = Open(Drain());
        vm.Term1.HasDcBlock = true;

        string host = vm.Rebuild!.DcBlocks[0].ElementName;
        var inductor = vm.Ladder.Elements.Single(e => e.Name == host);
        var block = vm.Ladder.Elements.Single(e => e.Name == MatchDcBlock.BlockName(host));

        Assert.Equal(MatchElementRole.DcBlock, block.Role);
        Assert.Equal(1, block.DcBlockEnd);
        Assert.Equal(0, inductor.DcBlockEnd);
        Assert.Equal(inductor.X, block.X, 9);
        Assert.True(block.Y > inductor.Y, "the block hangs BELOW the inductor it blocks");
        Assert.Equal(MatchLadderLayout.BlockY, block.Y, 9);

        // No other element shares that column — the block takes no pitch of its own.
        Assert.Equal(2, vm.Ladder.Elements.Count(e => Math.Abs(e.X - inductor.X) < 1e-9));

        // The arm's ground is under the BLOCK, and the inductor above it has none.
        Assert.Null(MatchLadderLayout.GroundYFor(vm.Ladder, inductor));
        Assert.Equal(block.Y + 200.0, MatchLadderLayout.GroundYFor(vm.Ladder, block)!.Value, 9);

        var schematic = MatchSchematicModel.Build(vm.Ladder);
        Assert.Contains(schematic.Components, c => c.Id == block.Name);
        Assert.Contains(schematic.Components, c => c.Id == block.Name + MatchSchematicModel.GroundIdSuffix);
        Assert.DoesNotContain(schematic.Components, c => c.Id == host + MatchSchematicModel.GroundIdSuffix);

        // ONE junction dot for the arm, at the spine, and it belongs to the inductor.
        Assert.Single(schematic.ConnectionDots.Where(d => Math.Abs(d.X - inductor.X) < 1e-9));

        output.WriteLine($"{host} at ({inductor.X:0}, {inductor.Y:0}); "
                         + $"{block.Name} at ({block.X:0}, {block.Y:0}); ground at "
                         + $"{MatchLadderLayout.GroundYFor(vm.Ladder, block):0}");
    }

    /// <summary>
    /// <b>Copy puts ONE ground under a blocked arm, under the block capacitor</b> (owner-reported,
    /// 2026-08-28: the pasted schematic had two grounds on the shunt inductor and none on the
    /// blocking capacitor). <c>MatchSchematicCopy</c> was grounding every shunt element at the shared
    /// <c>ShuntGroundY</c>, which for a two-symbol column is the inductor's lower lead twice; it now
    /// asks the pane's own per-column <c>GroundYFor</c>, so the copy is the drawing on screen.
    /// </summary>
    [Theory]
    [InlineData(1)]   // an end-node host (the drain network's L1)
    [InlineData(2)]   // an interior host (§4.9's L3, one series inductor in)
    public void Copy_GroundsABlockedArmOnce_UnderTheBlockCapacitor(int end)
    {
        var (_, _, vm) = Open(end == 1 ? Drain() : Golden());
        if (end == 1) vm.Term1.HasDcBlock = true; else vm.Term2.HasDcBlock = true;

        string host = vm.Rebuild!.DcBlocks[0].ElementName;
        var inductor = vm.Ladder.Elements.Single(e => e.Name == host);
        var block = vm.Ladder.Elements.Single(e => e.Name == MatchDcBlock.BlockName(host));

        var copy = MatchSchematicCopy.Build(vm.Ladder);
        Assert.Contains(copy.Components, c => c.InstanceName == host);
        Assert.Contains(copy.Components, c => c.InstanceName == block.Name);

        var groundsInColumn = copy.Components
            .Where(c => c.Symbol == SymbolKind.Ground && Math.Abs(c.X - inductor.X) < 1e-9)
            .ToList();
        var ground = Assert.Single(groundsInColumn);
        Assert.Equal(block.Y + MatchSchematicGeometry.LeadHalf, ground.Y, 9);
        Assert.True(ground.Y > MatchLadderLayout.ShuntGroundY, "the one ground is BELOW the block, not at the inductor's lead");

        // Every other shunt column is grounded exactly once, at the ordinary depth.
        foreach (var e in vm.Ladder.Elements.Where(e => e.IsShunt && Math.Abs(e.X - inductor.X) >= 1e-9))
        {
            var g = Assert.Single(copy.Components, c => c.Symbol == SymbolKind.Ground && Math.Abs(c.X - e.X) < 1e-9);
            Assert.Equal(MatchLadderLayout.ShuntGroundY, g.Y, 9);
        }

        // As many grounds as shunt COLUMNS, not as shunt elements.
        int columns = vm.Ladder.Elements.Where(e => e.IsShunt).Select(e => Math.Round(e.X, 6)).Distinct().Count();
        Assert.Equal(columns, copy.Components.Count(c => c.Symbol == SymbolKind.Ground));
        output.WriteLine($"{host} at y {inductor.Y:0}, {block.Name} at y {block.Y:0}, ground at y {ground.Y:0}; "
                         + $"{columns} shunt columns, {copy.Components.Count(c => c.Symbol == SymbolKind.Ground)} grounds");
    }

    /// <summary>
    /// The flattened cell carries both instances and says in its own record what the block did — the
    /// compensated inductor is not derivable from the design's numbers by anyone reading the drawing
    /// six months later.
    /// </summary>
    [Fact]
    public void TheFlattenedCell_CarriesBothInstancesAndRecordsTheCompensation()
    {
        var (_, _, vm) = Open(Drain());
        vm.Term1.HasDcBlock = true;
        string host = vm.Rebuild!.DcBlocks[0].ElementName;

        var cell = MatchFlatten.BuildSchematic(vm.Rebuild!, vm.Design, "MN1", DateTime.UtcNow);
        Assert.Contains(cell.Components, c => c.InstanceName == host);
        Assert.Contains(cell.Components, c => c.InstanceName == MatchDcBlock.BlockName(host));

        string text = string.Join("\n", cell.CanvasObjects.OfType<EditableText>().Select(t => t.Text));
        Assert.Contains("DC block at termination 1", text, StringComparison.Ordinal);
        Assert.Contains("Feed the bias through", text, StringComparison.Ordinal);
        output.WriteLine(text.Split('\n').First(l => l.Contains("DC block", StringComparison.Ordinal)));
    }

    // ── The controls are actually wired ───────────────────────────────────────

    /// <summary>
    /// <b>The toggle is in the termination header row's empty column</b> — the one placement that
    /// costs no height and needs no label (match.md §22.5). Declared in AXAML, so it is asserted
    /// against the source it is declared in, naming the mechanism rather than a word.
    /// </summary>
    [Fact]
    public void TheBlockToggle_IsDeclaredInTheTerminationHeaderRow()
    {
        string axaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Match",
                                                     "MatchDesignerWindow.axaml"));
        int header = axaml.IndexOf("ColumnDefinitions=\"Auto,*,Auto,Auto\"", StringComparison.Ordinal);
        Assert.True(header > 0, "the termination card's header row should still be Auto,*,Auto,Auto");

        int probe = axaml.IndexOf("Content=\"Probe\"", header, StringComparison.Ordinal);
        int toggle = axaml.IndexOf("<ToggleButton Grid.Column=\"1\" Content=\"DC Block\"", header,
                                   StringComparison.Ordinal);
        Assert.True(toggle > header && toggle < probe,
                    "the Block toggle belongs in column 1, between the heading and Probe");

        foreach (string binding in new[]
                 {
                     "IsChecked=\"{Binding HasDcBlock, Mode=TwoWay}\"",
                     "IsEnabled=\"{Binding DcBlockEnabled}\"",
                     "ToolTip.Tip=\"{Binding DcBlockTooltip}\"",
                 })
            Assert.Contains(binding, axaml, StringComparison.Ordinal);

        // The status lines carry the warn class off the line's own flag, not off a converter.
        Assert.Contains("ItemsSource=\"{Binding Status.DcBlocks}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Classes.warn=\"{Binding Warn}\"", axaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Equality at a RELATIVE tolerance. A capacitance has no natural scale, so a decimal-place
    /// comparison against 1e-10 is meaningless in either direction — see
    /// <c>MatchDesignerViewModel.SetTerminationReactance</c>'s own note on the same trap.
    /// </summary>
    private static void AssertSame(double expected, double actual)
    {
        Assert.True(Math.Abs(actual - expected) <= 1e-12 * Math.Max(1e-30, Math.Abs(expected)),
                    $"{actual:E17} != {expected:E17}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
