using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The discrete-value toggle and its editable ladders (<c>docs/design/smith-chart.md</c> §5.6a;
/// owner instruction, 2026-09-21). <b>One test per claim.</b>
/// </summary>
/// <remarks>
/// <b>In <see cref="UserStateDirectoryCollection"/> because it moves the per-user state
/// directory.</b> The ladders in force are a PREFERENCE, so a test that read the real file would
/// answer differently on a machine whose owner had edited their own list — and a test that WROTE it
/// would edit it. The redirect makes every case here a first-launch installation.
/// </remarks>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class SmithPreferredValuesTests : IDisposable
{
    private readonly string _root;

    public SmithPreferredValuesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-smith-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        CircuitRF.Ui.AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not the test */ }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private const double DesignHz = 2.0e9;

    private static SmithDesign Design(params SmithElement[] elements)
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm = 50.0;
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    private static SmithElement L(double henry, SmithPlacement placement, string name)
        => new() { Kind = SmithElementKind.L, Placement = placement, Name = name,
                   Values = { LHenry = henry } };

    private static SmithElement C(double farad, SmithPlacement placement, string name)
        => new() { Kind = SmithElementKind.C, Placement = placement, Name = name,
                   Values = { CFarad = farad } };

    private static int DrainUndo(SmithChartViewModel vm)
    {
        int n = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); n++; }
        return n;
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    /// <summary>True when <paramref name="v"/> is a rung of <paramref name="ladder"/>, to the
    /// precision a mantissa times a power of ten survives.</summary>
    private static bool OnLadder(double v, IReadOnlyList<double> ladder)
        => ladder.Any(r => Math.Abs(v / r - 1.0) < 1e-9);

    // ═════════════════════════════════════════════════════════════════════════
    //  1. What ships is E12, over the decades that were decided
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The shipped ladders are IEC 60063's E12, 0.1 pF … 100 nF and 0.1 nH … 100 µH</b> (owner
    /// decision, 2026-09-21).
    /// </summary>
    /// <remarks>
    /// The ENDS are the claim as much as the series is: 0.1 pF is the owner's own floor — where an
    /// RF chip capacitor range starts — and a ladder that began at 1 pF would silently refuse half
    /// the matching elements this tool exists to place.
    /// </remarks>
    [Fact]
    public void TheShippedLaddersAreE12OverTheDecidedDecades()
    {
        var caps = SmithPreferredValues.ShippedCapacitorsFarad;
        var inds = SmithPreferredValues.ShippedInductorsHenry;

        Assert.Equal(0.1e-12, caps[0],  12);
        Assert.Equal(100e-9,  caps[^1], 12);
        Assert.Equal(0.1e-9,  inds[0],  12);
        Assert.Equal(100e-6,  inds[^1], 12);

        // Ascending, with no repeats.
        Assert.True(caps.Zip(caps.Skip(1)).All(p => p.Second > p.First));
        Assert.True(inds.Zip(inds.Skip(1)).All(p => p.Second > p.First));

        // Twelve per decade plus the closing rung: 6 decades × 12 + 1.
        Assert.Equal(6 * 12 + 1, caps.Count);
        Assert.Equal(6 * 12 + 1, inds.Count);

        // Every mantissa is an E12 one — which is what makes it that series and not merely a list
        // of 73 numbers between the two ends.
        foreach (double v in caps.Concat(inds))
        {
            double mantissa = v / Math.Pow(10.0, Math.Floor(Math.Log10(v) + 1e-9));
            Assert.Contains(SmithPreferredValues.E12, m => Math.Abs(m - mantissa) < 1e-6);
        }

        // 1.0 1.2 1.5 1.8 2.2 2.7 3.3 3.9 4.7 5.6 6.8 8.2 — the series itself, spelled out, because
        // a test that derived it from the same table it checks would agree with any typo in it.
        Assert.Equal([1.0, 1.2, 1.5, 1.8, 2.2, 2.7, 3.3, 3.9, 4.7, 5.6, 6.8, 8.2],
                     SmithPreferredValues.E12);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. Nearest is nearest by RATIO
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The snap is geometric, not linear</b> — and a value that is not a part is left alone.
    /// </summary>
    /// <remarks>
    /// The two rungs 1.0 and 1.2 have an arithmetic midpoint of 1.10 and a geometric one of
    /// 1.0954…, so 1.09 separates the two rules: a linear nearest sends it UP to 1.2 and the
    /// geometric one sends it DOWN to 1.0. A ladder is a ratio scale — a tolerance is ±5 %, not
    /// ±5 pF — so the geometric answer is the one a component behaves like.
    /// </remarks>
    [Fact]
    public void TheSnapIsNearestByRatioAndLeavesNonPartsAlone()
    {
        var ladder = SmithPreferredValues.ShippedCapacitorsFarad;

        // The discriminating case: 1.09 pF is BELOW the geometric midpoint of 1.0 and 1.2 and above
        // the arithmetic one.
        Assert.Equal(1.0e-12, SmithPreferredValues.Snap(1.09e-12, ladder), 15);
        Assert.Equal(1.2e-12, SmithPreferredValues.Snap(1.11e-12, ladder), 15);

        // Ordinary cases either side of a rung.
        Assert.Equal(2.2e-12, SmithPreferredValues.Snap(2.37e-12, ladder), 15);
        Assert.Equal(4.7e-12, SmithPreferredValues.Snap(4.5e-12,  ladder), 15);

        // Past either end lands ON the end — nearest is nearest, and the alternative is a toggle
        // that says every value is on the ladder while one of them is not.
        Assert.Equal(0.1e-12, SmithPreferredValues.Snap(0.01e-12, ladder), 15);
        Assert.Equal(100e-9,  SmithPreferredValues.Snap(1.0e-6,   ladder), 15);

        // Zero farads is an OPEN CIRCUIT and zero henries a WIRE — both meaningful, neither a small
        // part. Snapping either to the bottom rung would change what the element IS.
        Assert.Equal(0.0, SmithPreferredValues.Snap(0.0, ladder));
        Assert.Equal(-1.0, SmithPreferredValues.Snap(-1.0, ladder));
        Assert.Equal(3.0, SmithPreferredValues.Snap(3.0, []));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. Turning it on snaps L and C, and nothing else, as ONE undo entry
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The flag and the snap are one gesture and therefore one undo entry</b>, and the snap
    /// reaches L and C only.
    /// </summary>
    /// <remarks>
    /// Splitting them would let one undo take the flag off and leave the values moved — a state
    /// nobody asked for and which pressing the button again does not undo. And the R of an SRLC is
    /// an ESR: a measured parasitic rather than an ordered part, so putting it on a preferred-value
    /// ladder would state something untrue about the design (owner decision, 2026-09-21).
    /// </remarks>
    [Fact]
    public void TurningItOnSnapsLAndCOnlyAndIsOneUndoEntry()
    {
        var srlc = new SmithElement
        {
            Kind = SmithElementKind.Srlc, Placement = SmithPlacement.Series, Name = "SRLC1",
            Values = { ROhm = 0.37, LHenry = 2.37e-9, CFarad = 1.64e-12 },
        };
        var line = new SmithElement
        {
            Kind = SmithElementKind.Tline, Placement = SmithPlacement.Series, Name = "TL1",
            Values = { Z0Ohm = 63.7, ElectricalLengthDeg = 41.3, ReferenceFrequencyHz = DesignHz },
        };

        var vm = new SmithChartViewModel(Design(srlc, line, C(0.83e-12, SmithPlacement.Shunt, "C1")));
        string before = SmithDesignIo.SerializeUnvalidated(vm.Design);

        Assert.False(vm.SnapToPreferredValues);
        vm.SnapToPreferredValues = true;

        var e0 = vm.Design.Elements[0].Values;
        var e1 = vm.Design.Elements[1].Values;
        var e2 = vm.Design.Elements[2].Values;

        // L and C moved onto the ladder…
        Assert.Equal(2.2e-9,  e0.LHenry, 15);
        Assert.Equal(1.5e-12, e0.CFarad, 15);
        Assert.Equal(0.82e-12, e2.CFarad, 15);

        // …and nothing else moved at all.
        Assert.Equal(0.37, e0.ROhm);
        Assert.Equal(63.7, e1.Z0Ohm);
        Assert.Equal(41.3, e1.ElectricalLengthDeg);

        // ONE entry, and it takes the flag back with the values.
        Assert.Equal(1, DrainUndo(vm));
        Assert.Equal(before, SmithDesignIo.SerializeUnvalidated(vm.Design));
        Assert.False(vm.SnapToPreferredValues);

        // Turning it OFF moves nothing — the ladder values ARE the design now, and the way back is
        // the undo stack rather than a memory of what was there before.
        vm.SnapToPreferredValues = true;
        string snapped = SmithDesignIo.SerializeUnvalidated(vm.Design);
        vm.SnapToPreferredValues = false;
        Assert.Equal(2.2e-9, vm.Design.Elements[0].Values.LHenry, 15);
        Assert.NotEqual(snapped, SmithDesignIo.SerializeUnvalidated(vm.Design));  // only the flag
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. The toggle is in the document; the ladder is not
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The flag round-trips through a `.csmith`, and a document with it off writes nothing.</b>
    /// <b>The ladder is never in the file at all.</b>
    /// </summary>
    /// <remarks>
    /// A `.csmith` carrying its own copy of the list would open on another machine snapping to a
    /// parts drawer its owner never chose, with nothing on screen to tell it from their own. The
    /// list is per-user state for that reason — and the absence is worth a test, because adding it
    /// later would look like a feature.
    /// </remarks>
    [Fact]
    public void TheFlagRoundTripsAndTheLadderIsNeverInTheFile()
    {
        var vm = new SmithChartViewModel(Design(L(2.2e-9, SmithPlacement.Series, "L1")));

        // Off: absent from the JSON entirely, so no existing document changes.
        string off = SmithDesignIo.Serialize(vm.Design);
        Assert.DoesNotContain("SnapToPreferredValues", off, StringComparison.OrdinalIgnoreCase);
        Assert.False(SmithDesignIo.Deserialize(off).SnapToPreferredValues);

        vm.SnapToPreferredValues = true;
        string on = SmithDesignIo.Serialize(vm.Design);
        Assert.True(SmithDesignIo.Deserialize(on).SnapToPreferredValues);

        // The ladder itself is nowhere in it — not as a list, not as a count, not as a name.
        Assert.DoesNotContain("preferred_capacitors", on, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preferred_inductors", on, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ladder", on, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. Both doors snap — a slider and a gripper
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Every value an edit produces is on the ladder</b>, whichever of the two doors it came
    /// through — a slider or typed value (<c>SetElementValue</c>) and a handle on the chart
    /// (<c>DragGripperTo</c>).
    /// </summary>
    /// <remarks>
    /// This is what makes "the document holds buyable values" true rather than mostly true. A third
    /// write path added later without a snap would leave the toggle on and one value off the
    /// ladder, and the only evidence would be the number itself.
    /// </remarks>
    [Fact]
    public void BothWriteDoorsSnap()
    {
        var vm = new SmithChartViewModel(Design(L(3.3e-9, SmithPlacement.Series, "L1")));
        vm.SnapToPreferredValues = true;
        vm.SelectElement(0);

        var inds = SmithPreferredValues.ShippedInductorsHenry;

        // ── door 1: the slider, dragged ──────────────────────────────────────
        var row = vm.SliderRows.Single();
        vm.BeginSliderDrag();
        for (int i = 1; i <= 12; i++)
        {
            row.Position += 0.037;
            Assert.True(OnLadder(vm.Design.Elements[0].Values.LHenry, inds),
                        $"mid-drag value {vm.Design.Elements[0].Values.LHenry} is off the ladder");
        }
        vm.EndSliderDrag();

        // ── door 1b: a TYPED value, which is snapped too ─────────────────────
        // A typed number is an instruction, and with the toggle on the instruction it carries is
        // "the nearest value I can buy" — a typed 2.37 nH that stayed would make the toggle false.
        row.ValueEntry = "2.37 nH";
        Assert.Equal(2.2e-9, vm.Design.Elements[0].Values.LHenry, 15);

        // ── door 2: a gripper on the chart ───────────────────────────────────
        var tf    = PlotRenderer.BuildTransforms(vm.ChartPlot, (420.0, 420.0));
        var nodes = SmithCascade.Evaluate(vm.Design, DesignHz);
        var g1    = SmithCascade.Gamma(nodes[1].Z, vm.Design.Chart.Z0Ohm);
        var start = tf.PrimaryToCanvas(g1.Real, g1.Imaginary);

        Assert.True(vm.BeginGripperDrag(1));
        for (int step = 1; step <= 10; step++)
        {
            var (wx, wy) = tf.PrimaryFromCanvas(start.X + 3f * step, start.Y - 3f * step);
            vm.DragGripperTo(new Complex(wx, wy));
            Assert.True(OnLadder(vm.Design.Elements[0].Values.LHenry, inds),
                        $"gripper value {vm.Design.Elements[0].Values.LHenry} is off the ladder");
        }
        vm.EndGripperDrag(cancelled: false);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. Reading a list somebody pasted
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The comma is a SEPARATOR in this field and never a decimal point</b>, lines and commas
    /// both split, and an empty list is a refusal.
    /// </summary>
    /// <remarks>
    /// circuitRF accepts <c>1,5</c> for one and a half in every field whose grammar leaves the comma
    /// free — and this field's does not. <c>NumericText.NormalizeDecimalSeparator</c> states the
    /// exception itself ("any field whose own grammar separates values with commas … must never be
    /// passed through here"), and a `.cnl`'s <c>Values=</c> list settled the same question the same
    /// way. <c>0,1, 0,12</c> is either two values or four and nothing in the text says which.
    /// </remarks>
    [Fact]
    public void ThePastedListSplitsOnCommasAndRefusesAnEmptyOne()
    {
        // A spreadsheet column, a comma-separated row, a comment and a blank line — one list.
        const string pasted = """
            0.1 pF
            0.12, 0.15,0.18

            # a comment runs to the end of its line
            2.2 pF, 1 nF
            """;

        Assert.True(SmithPreferredValues.TryParse(pasted, MatchQuantity.Capacitance, "pF",
                                                  out var values, out string? err));
        Assert.Null(err);

        // Sorted, de-duplicated, bare numbers read as pF.
        double[] want = [0.1e-12, 0.12e-12, 0.15e-12, 0.18e-12, 2.2e-12, 1.0e-9];
        Assert.Equal(want.Length, values.Count);
        for (int i = 0; i < want.Length; i++)
            Assert.True(Math.Abs(values[i] / want[i] - 1.0) < 1e-12,
                        $"entry {i} read as {values[i]}, expected {want[i]}");

        // A decimal comma is NOT read as a decimal point here: "1,2" is 1 pF and 2 pF, not 1.2 pF.
        Assert.True(SmithPreferredValues.TryParse("1,2", MatchQuantity.Capacitance, "pF",
                                                  out var twoValues, out _));
        Assert.Equal([1.0e-12, 2.0e-12], twoValues);

        // A unit from the WRONG ladder is refused rather than discarded.
        Assert.False(SmithPreferredValues.TryParse("2.2 nH", MatchQuantity.Capacitance, "pF",
                                                   out _, out string? unitErr));
        Assert.Contains("2.2 nH", unitErr);

        // Zero and negative are not values a part can have.
        Assert.False(SmithPreferredValues.TryParse("1 pF, 0 pF", MatchQuantity.Capacitance, "pF",
                                                   out _, out _));

        // An empty list would leave the toggle on and doing nothing, silently — so it is refused,
        // and the refusal names the button that gets out of it.
        Assert.False(SmithPreferredValues.TryParse("   \n # nothing \n ", MatchQuantity.Capacitance,
                                                   "pF", out _, out string? emptyErr));
        Assert.Contains("Revert", emptyErr);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. The user's own list, and the way back
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>An edited list replaces the ladder, and Revert stores NULL rather than the shipped
    /// numbers.</b>
    /// </summary>
    /// <remarks>
    /// The distinction is not cosmetic. Storing the shipped values on revert would freeze that user
    /// on the table current the day they pressed it — a later circuitRF could extend the ladder and
    /// they would never see it, with nothing on screen to explain why. Null means "whatever ships",
    /// so revert is a genuine return to the default and not a copy of today's one.
    /// </remarks>
    [Fact]
    public void AnEditedLadderReplacesTheShippedOneAndRevertStoresNull()
    {
        var vm = new SmithPreferredValuesViewModel();

        // It opens on the shipped list, spelled with units.
        Assert.Contains("0.1 pF", vm.Buffer);
        Assert.False(SmithPreferredValueStore.IsCustomized);

        // A three-value drawer, pasted in.
        vm.Buffer = "1 pF, 10 pF, 100 pF";
        vm.ApplyCommand.Execute(null);

        Assert.True(SmithPreferredValueStore.IsCustomized);
        Assert.Equal([1e-12, 10e-12, 100e-12], SmithPreferredValueStore.Capacitors);

        // The inductors were untouched by a capacitor edit.
        Assert.Equal(SmithPreferredValues.ShippedInductorsHenry, SmithPreferredValueStore.Inductors);

        // And a design now snaps to the drawer rather than to E12.
        var design = new SmithChartViewModel(Design(C(3.0e-12, SmithPlacement.Shunt, "C1")));
        design.SnapToPreferredValues = true;
        Assert.Equal(1e-12, design.Design.Elements[0].Values.CFarad, 15);

        // ── the way back ─────────────────────────────────────────────────────
        vm.RevertToShippedCommand.Execute(null);
        Assert.False(SmithPreferredValueStore.IsCustomized);
        Assert.Equal(SmithPreferredValues.ShippedCapacitorsFarad, SmithPreferredValueStore.Capacitors);

        // NULL, not a copy of the shipped numbers — which is what makes a later ladder reach a user
        // who has reverted.
        var prefs = CircuitRF.Ui.Theming.AppPreferencesIo.Load();
        Assert.Null(prefs.SmithPreferredCapacitorsFarad);
        Assert.Null(prefs.SmithPreferredInductorsHenry);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  8. Editing the list re-snaps an open design, once
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Applying a new ladder re-snaps a design that has the toggle on — as ONE undo entry — and
    /// does nothing at all to one that has it off.</b>
    /// </summary>
    /// <remarks>
    /// Leaving the open design alone would leave the toggle saying something false: the window would
    /// claim every value is on the ladder while the ladder had just been replaced under it. Doing it
    /// per committed row instead of per Apply would put an undo entry behind every keystroke of a
    /// table edit, which is the Match Designer's entry-per-notification defect by another route.
    /// </remarks>
    [Fact]
    public void ApplyingANewLadderResnapsAnOpenDesignOnce()
    {
        var vm = new SmithChartViewModel(Design(C(1.15e-12, SmithPlacement.Shunt, "C1"),
                                                C(8.0e-12,  SmithPlacement.Shunt, "C2")));
        string original = SmithDesignIo.SerializeUnvalidated(vm.Design);

        vm.SnapToPreferredValues = true;
        Assert.Equal(1.2e-12, vm.Design.Elements[0].Values.CFarad, 15);
        Assert.Equal(8.2e-12, vm.Design.Elements[1].Values.CFarad, 15);

        // A design with the toggle OFF is untouched by a list edit.
        var off = new SmithChartViewModel(Design(C(3.0e-12, SmithPlacement.Shunt, "C1")));
        string offBefore = SmithDesignIo.SerializeUnvalidated(off.Design);

        int reapplied = 0;
        var editor = new SmithPreferredValuesViewModel(() =>
        {
            reapplied++;
            vm.ReapplyPreferredValues();
            off.ReapplyPreferredValues();
        });

        editor.Buffer = "1 pF, 10 pF, 100 pF";
        editor.ApplyCommand.Execute(null);

        Assert.Equal(1, reapplied);
        Assert.Equal(1e-12,  vm.Design.Elements[0].Values.CFarad, 15);
        Assert.Equal(10e-12, vm.Design.Elements[1].Values.CFarad, 15);

        // TWO entries for the whole session — the toggle, and the re-snap. Not one per element, and
        // not one per row of the list that was applied.
        Assert.Equal(2, DrainUndo(vm));
        Assert.Equal(original, SmithDesignIo.SerializeUnvalidated(vm.Design));

        // …and the design with the toggle OFF was not touched at all.
        Assert.Equal(offBefore, SmithDesignIo.SerializeUnvalidated(off.Design));
        Assert.False(off.UndoRedo.CanUndo);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  9. The button is where it was asked for, with a glyph that is not the magnet
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The toggle is the last button before Help in the network toolbar, and its glyph is the
    /// staircase</b> (owner instruction, 2026-09-21).
    /// </summary>
    /// <remarks>
    /// The glyph half is not decoration. <c>Magnet</c>/<c>MagnetOn</c> already mean "snap the
    /// drawing to a grid" in the layout editor and in wBond; reusing it for a snap of VALUES would
    /// put one glyph on two meanings in one application, which is how a toolbar stops being
    /// readable. A staircase is a quantized ramp — a value ladder, drawn.
    /// </remarks>
    [Fact]
    public void TheToggleSitsLeftOfHelpAndIsNotTheMagnet()
    {
        string xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src/Ui/Views/Smith/SmithChartView.axaml"));

        int toggle = xaml.IndexOf("Name=\"DiscreteValuesButton\"", StringComparison.Ordinal);
        int help   = xaml.IndexOf("Name=\"HelpButton\"", StringComparison.Ordinal);

        Assert.True(toggle > 0, "the discrete-values button is not in the toolbar");
        Assert.True(help > toggle, "the discrete-values button must sit to the LEFT of Help");

        string between = xaml[toggle..help];
        Assert.Contains("Kind=\"Stairs\"", between);
        Assert.DoesNotContain("Magnet", between);

        // It latches like every other tool button in this window, and it is a command rather than a
        // two-way binding because a Button has no checked state to bind.
        Assert.Contains("Classes.ToolActive=\"{Binding ViewModel.SnapToPreferredValues}\"", between);
        Assert.Contains("ToggleSnapToPreferredValuesCommand", between);

        // The list is reachable whether the toggle is on or off — the slider rows' own
        // "right-click ▸ Set range…" idiom one control along.
        Assert.Contains("OnEditPreferredValues", between);
    }
}
