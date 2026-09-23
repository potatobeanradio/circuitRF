// ================================================================
//  RailZMapTests.cs — brief-railrf-15-modes-and-maps.md §4
//
//  The two halves of brief 15 that need the real extractor and the real overlay seam:
//
//    R-rail15-1  the mode frequencies AGREE WITH THE PEAKS THE SWEEP FINDS, on the same board.
//                §4.5's own claim — "the mode list and the sweep cannot disagree about the
//                structure" — and the reason the eigenproblem is built on brief 14's matrices
//                rather than on a closed form. A disagreement here means they are not, in fact,
//                the same discretisation.
//
//    R-rail15-3  the |Z| overlay renders, is in ContentBounds(), does not invalidate the path
//                cache, and APPEARS IN A CLIPBOARD COPY'S SVG TEXT — brief 9's gate, re-run with
//                this overlay active. That last one is the specific trap the brief names: brief
//                9's overlay list was written when the drop map and the class map were the only
//                overlays, and an overlay nobody added to it produces a copy that looks right and
//                is missing the thing it was taken for.
//
//  §4.5's CLOSED-FORM acceptance — the rectangle's first six modes to 2 %, with monotone
//  convergence — is Engine.Tests/Pdn/PdnModeTests.cs, because it is pure numerics and wants no
//  artwork anywhere near it. What is here is everything that does.
//
//  One test per CLAIM the brief makes, not one per measured rung.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using CircuitRF.Engine.Pdn;
using CircuitRF.Render;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailZMapTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;   // 1000 DBU/µm
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private const double CopperSigma = 5.8e7;
    private const double OneOunceUm = 34.8;
    private const double EpsilonR = 4.3;
    private const double CoreUm = 200.0;
    private const double C0 = 299_792_458.0;

    /// <summary>A 30 × 20 mm plane pair — non-square, so no two of its low modes are degenerate.</summary>
    private const double AMm = 30.0, BMm = 20.0;

    /// <summary>The stated mesh pitch. See <see cref="Modes"/> for why it is stated rather than left
    /// to the rules.</summary>
    private const double CellMm = 1.0;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>§4.5's closed form, for the ladder's own bracket. <b>Not the gate</b> — that is
    /// <c>PdnModeTests</c>'s; here it is only what decides where to look.</summary>
    private static double FirstMode => C0 / (2.0 * Math.Sqrt(EpsilonR) * AMm * 1e-3);

    // ══ R-rail15-1 ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§4.5's claim, measured: the mode the eigenproblem reports is the frequency the SWEEP's
    /// own |Z| peaks at.</b>
    ///
    /// <para><i>"The cavity modes come out of the same discretisation as a generalised eigenproblem
    /// on the loss-free system, so the mode list and the sweep cannot disagree about the
    /// structure."</i> The two answers below share the extractor, the settings and the cell size and
    /// differ in everything else: one is an eigenvalue of the loss-free pencil and the other is the
    /// maximum of |Z₁₁| over a swept MNA solve of the lossy netlist. A disagreement is not a
    /// tolerance problem — it says the eigenproblem was not built on these matrices.</para>
    ///
    /// <para><b>And the negative half is what gives the tolerance teeth</b>: the same ladder's
    /// endpoints are a long way off the peak, so a gate satisfied by "somewhere in the ladder"
    /// would not pass.</para>
    /// </summary>
    [Fact]
    public void TheModeFrequencyIsWhereTheSweepsOwnImpedancePeaks()
    {
        var plane = Modes(FirstMode * 1.2);
        Assert.Null(plane.Refusal);

        var first = plane.Answer!.Modes[0];
        output.WriteLine(
            $"eigenproblem {first.FrequencyHz / 1e9:0.0000} GHz over " +
            $"{plane.Provenance!.CellCount:N0} cells at {plane.Provenance.CellSizeMetres * 1e3:0.###} mm");

        // A ladder ±12 % about the mode, swept through the REAL extraction at every point — one
        // mesh and one sparse complex solve each, which is exactly what a PdnSweep point is.
        double best = 0, at = 0;
        var curve = new List<(double F, double Z)>();

        for (int i = 0; i <= 24; i++)
        {
            double f = first.FrequencyHz * (0.88 + 0.01 * i);
            double z = PortImpedance(f);
            curve.Add((f, z));
            if (z > best) { best = z; at = f; }
        }

        output.WriteLine($"sweep peak  {at / 1e9:0.0000} GHz at {best:0.#} Ω");

        Assert.True(Math.Abs(at / first.FrequencyHz - 1.0) < 0.02,
            $"the mode list and the sweep disagree about this board: the eigenproblem says " +
            $"{first.FrequencyHz / 1e9:0.0000} GHz and |Z| peaks at {at / 1e9:0.0000} GHz");

        // The negative: the ladder's own ends are nowhere near, so "inside the ladder" is not what
        // the assertion above is satisfied by.
        Assert.True(curve[0].Z < best / 3.0 && curve[^1].Z < best / 3.0,
            $"the ladder does not bracket a peak at all — ends {curve[0].Z:0.#} Ω and " +
            $"{curve[^1].Z:0.#} Ω against {best:0.#} Ω");
    }

    /// <summary>
    /// <b>R-rail15-2 through the real extraction: every mode row names a PLACE.</b>
    ///
    /// <para>§2.4's sentence is about a port, and a port here is what a user typed — <c>U1.VDD</c>,
    /// resolved through the pad, tied into one node over its own cells by §4.3. The engine-level
    /// gate in <c>PdnModeTests</c> proves the arithmetic on a hand-built rectangle; this proves the
    /// arithmetic is reachable from a board, which is a different thing and the one that breaks
    /// when a cell map is keyed on a netlist NODE (a port's pin field is one node over several
    /// cells, so a node→cell map is ambiguous exactly under the ports).</para>
    /// </summary>
    [Fact]
    public void EveryModeRowNamesThePortItLandsOnAndTheTwoCornersDoNotReadAlike()
    {
        var plane = Modes(FirstMode * 1.2, secondPort: true);
        Assert.Null(plane.Refusal);

        // Every row names a port a user typed, never a node number — and NOT always the same one:
        // the corner port is the maximum of the (1,0) mode and the centre-line port is the maximum
        // of the (0,1) mode, which is the whole of what a per-port evaluation buys.
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in plane.Answer!.Modes)
        {
            Assert.Equal(2, mode.AtPorts.Count);
            Assert.NotNull(mode.Worst);
            Assert.Contains(mode.Worst!.Name, new[] { "U1.VDD", "U2.VDD" });
            Assert.Contains(mode.Worst.Name, mode.Describe(), StringComparison.Ordinal);
            named.Add(mode.Worst.Name);
            output.WriteLine(mode.Describe());
        }

        Assert.True(named.Count == 2,
            "every mode landed on the same port, so this would pass on a per-port column that was " +
            "a constant — " + string.Join(", ", named));

        // The (1,0) mode is cos(πx/a): the two ports sit on a corner and on the centre line, which
        // is its own null. A mode list that carried only frequencies would have nothing to say here.
        var first = plane.Answer.Modes[0];
        double corner = first.AtPorts[0].Magnitude;
        double centre = first.AtPorts[1].Magnitude;

        Assert.True(corner > 0.9, $"the corner port must sit on this mode's own peak — {corner:0.000}");
        Assert.True(centre < 0.1, $"the centre port sits on its null — {centre:0.000}");
    }

    // ══ R-rail15-3 ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The |Z| map is drawn, it is framed, and a repaint of it is a repaint.</b>
    ///
    /// <para>Brief 8 built the seam and left this tab empty; all three of its rules still apply and
    /// none of them is negotiable. The path-cache half is R-rail8-2 — on a real board that is the
    /// difference between a repaint and a rebuild of half a million shapes — and the framing half is
    /// R-rail8-7, which the legend makes a real question because the plate sits outside the
    /// copper's own bbox.</para>
    /// </summary>
    [Fact]
    public void TheImpedanceOverlayRendersAndIsFramedAndRepaintsWithoutTouchingThePathCache()
    {
        var plane = Modes(FirstMode * 1.2);
        Assert.Null(plane.Refusal);

        var view = Artwork();
        var cache = new LayoutPathCache(1000);
        int modelChanges = 0;
        view.Changed += (_, e) => { modelChanges++; cache.Apply(e); };

        var counters = new LayoutFrameCounters();
        for (int i = 0; i < view.Shapes.Count; i++)
            cache.GetOrBuild(i, view.Shapes[i], 1.0 / Dbu, 0, counters, out _);
        int warm = cache.Count;
        Assert.True(warm > 0);

        var overlay = new RailLayoutOverlay { Result = Dc(), DbuPerMicron = Dbu };
        int repaints = 0;
        overlay.OverlayChanged += () => repaints++;

        // Before the plane answer arrives the tab SAYS SO rather than looking like a broken map —
        // and it says what the map WOULD show, which is
        // TheEmptyImpedanceTabSaysWhatTheMapIsAndARefusalTakesItsPlace's own claim.
        overlay.Kind = RailMapKind.Impedance;
        Assert.Empty(overlay.Scene.Tiles);
        Assert.Equal(RailMapScene.EmptyImpedanceNote, overlay.Scene.Note);

        overlay.Plane = plane.Answer;

        // ── it renders ────────────────────────────────────────────────────────────────────────
        var scene = overlay.Scene;
        Assert.Equal(RailMapKind.Impedance, scene.Kind);
        Assert.NotEmpty(scene.Tiles);
        Assert.NotNull(scene.Legend);

        // The plate reads OHMS, not volts — the |Z| tab and the drop tab share one painter and the
        // painter decides nothing.
        Assert.Contains("Ω", scene.Legend!.ColdLabel + scene.Legend.HotLabel, StringComparison.Ordinal);
        Assert.Contains("|Z| at", scene.Legend.Caption, StringComparison.Ordinal);
        Assert.Contains("U1.VDD", scene.Legend.Caption, StringComparison.Ordinal);

        // ── it is framed (R-rail8-7): ContentBounds is the scene's union, legend included ──────
        Assert.Equal(scene.Bounds, overlay.ContentBounds());
        Assert.True(scene.Legend.Box.MinY < 0,
            "the plate must sit below the copper for the framing half to be about anything");
        Assert.True(overlay.ContentBounds().MinY <= scene.Legend.Box.MinY);

        // ── and a repaint is a repaint (R-rail8-2) ────────────────────────────────────────────
        overlay.OnPointerMoved(Mm(2), Mm(2), Um(20), leftButtonDown: false,
                               Avalonia.Input.KeyModifiers.None);
        overlay.Theme = RailMapTheme.Dark;

        Assert.True(repaints >= 2, "the overlay never asked for a repaint, so this proves nothing.");
        Assert.Equal(0, modelChanges);
        Assert.Equal(warm, cache.Count);

        // The readout is in ohms and names the mode that is worst where the cursor is — §2.4's
        // question is about a PLACE.
        Assert.NotNull(overlay.Readout);
        Assert.Contains("Ω", overlay.Readout!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Brief 9's gate, re-run with the |Z| overlay active — R-rail9-3 and the trap this brief
    /// was told it would hit.</b>
    ///
    /// <para><i>"Adding the |Z| map to the renderer and not to that list produces a copy that looks
    /// right and is missing the thing the user copied it for."</i> The overlay set a copy draws is
    /// an explicit parameter list (§11.7), so the gate has two halves: the map's own marks are in
    /// the SVG, and the identical context with railRF's entry removed — which is exactly the state
    /// "nobody added it" leaves behind — contains none of them and still renders a board.</para>
    /// </summary>
    [Fact]
    public void ACopyTakenWithTheImpedanceMapShowing_ContainsTheImpedanceMap()
    {
        var plane = Modes(FirstMode * 1.2);
        Assert.Null(plane.Refusal);

        var scene = RailMapScene.Build(Dc(), RailMapKind.Impedance, Dbu, plane.Answer);
        Assert.NotEmpty(scene.Tiles);

        string withMap = SvgOf(ContextFor(scene));
        string withoutMap = SvgOf(ContextFor(railMap: null));

        // The legend's caption carries the rail, the model kind (§2.9 rule 1) and what the picture
        // IS — none of which any amount of copper could produce.
        Assert.Contains("VDD", withMap, StringComparison.Ordinal);
        Assert.Contains("Accurate model", withMap, StringComparison.Ordinal);
        Assert.Contains("|Z| at", withMap, StringComparison.Ordinal);

        Assert.True(withoutMap.Length > 0, "the copper-only picture must still render");
        Assert.DoesNotContain("Accurate model", withoutMap, StringComparison.Ordinal);
        Assert.DoesNotContain("|Z| at", withoutMap, StringComparison.Ordinal);

        // ── R-rail9-2: the page is framed on the PAINTED extent, the plate included ────────────
        var framed = LayoutClipboard.SelectionBoundsForTests(ContextFor(scene));
        var blind = LayoutClipboard.SelectionBoundsForTests(ContextFor(railMap: null));
        Assert.NotNull(framed);
        Assert.NotNull(blind);
        Assert.True(framed!.Value.BbMinY <= scene.Legend!.Box.MinY);
        Assert.True(blind!.Value.BbMinY > scene.Legend.Box.MinY);
    }

    /// <summary>
    /// <b>Determinism: the same board's mode field renders to the same bytes twice.</b>
    ///
    /// <para>An eigenvector is determined only up to a sign, so a solver left to itself can hand
    /// back a map and its negative on two runs of one board — two different pictures of one answer.
    /// <c>PdnMode.Field</c> pins the sign explicitly; this is the half of that claim which is about
    /// the PICTURE, which is what brief 9's clipboard gate and brief 17's figures depend on.</para>
    /// </summary>
    [Fact]
    public void TheSameBoardRendersTheSameImpedanceMapTwice()
    {
        var a = Modes(FirstMode * 1.2);
        var b = Modes(FirstMode * 1.2);
        Assert.Null(a.Refusal);
        Assert.Null(b.Refusal);

        string first = SvgOf(ContextFor(RailMapScene.Build(Dc(), RailMapKind.Impedance, Dbu, a.Answer)));
        string second = SvgOf(ContextFor(RailMapScene.Build(Dc(), RailMapKind.Impedance, Dbu, b.Answer)));

        Assert.Equal(WithoutSkiaIds(first), WithoutSkiaIds(second));

        // And the fields themselves, which is where a flipped sign would come from.
        for (int k = 0; k < a.Answer!.Modes.Count; k++)
            Assert.Equal(a.Answer.Modes[k].Field, b.Answer!.Modes[k].Field);
    }

    /// <summary>
    /// <b>A DC extraction is refused BY NAME rather than answered with an empty map.</b>
    /// </summary>
    /// <remarks>
    /// §4.1's shunt branch vanishes at ω = 0 by construction, so the DC netlist the drop map is of
    /// holds no cavity at all. That is the state a caller is in immediately after a Run, and the
    /// honest answer names the frequency as the thing that is missing — an empty mode list would
    /// read as a board with no resonances on it.
    /// </remarks>
    [Fact]
    public void TheDcExtractionIsRefusedAndTheRefusalNamesTheFrequency()
    {
        var run = RailDcRun.Run(Request(secondPort: false));
        Assert.Null(run.Refusal);

        var answer = PdnPlaneModes.Of(run.Rails[0].Netlist);

        Assert.NotNull(answer.Refusal);
        Assert.Contains("ω = 0", answer.Refusal!, StringComparison.Ordinal);
        Assert.Empty(answer.Modes);

        // …and through the run a window actually calls, a zero frequency is refused before anything
        // is extracted at all.
        var refused = RailPlaneRun.Run(new RailPlaneRequest
        {
            Board = Request(secondPort: false),
            RailName = "VDD",
            FrequencyHz = 0,
        });

        Assert.NotNull(refused.Refusal);
        Assert.Null(refused.Answer);
    }

    // ══ The shipped example, and the empty tab ═══════════════════════════════════════════════

    /// <summary>
    /// <b>The |Z| map is reachable on the SHIPPED example, with nothing typed.</b>
    /// </summary>
    /// <remarks>
    /// It was not, and no test said so because every gate here runs on the uniform rectangle above
    /// — whose narrowest copper IS the plane, so the two cell-size rules never collide on it. On a
    /// real board they always do: R-rail3-14's feature rule sizes the DC cell to resolve the
    /// narrowest TRACE's resistance, which on the Power Rail example is 0.066 mm and 61,129 cavity
    /// cells against the dense solve's 4,000. Every press of Find was refused, at every frequency,
    /// and the refusal named two knobs — a coarser cell, or a lower band top — of which the second
    /// does nothing here (λ/20 at 100 MHz in εr 4.3 is wider than the board, so halving the
    /// frequency changed the mesh by nothing at all).
    ///
    /// <para>Driven on the shipped document rather than a fixture for the reason
    /// <c>RailWindowTests</c> already gives: what is under test is that a board somebody can open
    /// produces the picture the window offers them.</para>
    /// </remarks>
    [Fact]
    public void TheShippedExampleProducesAnImpedanceMapWithNoCellSizeTyped()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        Assert.Empty(vm.LoadDocumentReferences());
        Assert.Null(vm.MeshCellMetres);          // nothing typed — the state the example opens in

        var board = vm.Board!;
        var rq = new RailDcRequest
        {
            Document       = vm.Document,
            Shapes         = board.Shapes,
            Technology     = board.Technology,
            DbuPerMicron   = board.DbuPerMicron,
            LengthFormat   = board.LengthFormat,
            Pads           = board.Pads,
            NetPoints      = board.NetPoints,
            ReferenceNet   = board.ReferenceNet,
            BoardOutline   = board.BoardOutline,
            SeriesElements = board.SeriesElements,
            ShuntParts     = board.ShuntParts,
            Model          = PdnModelKind.Accurate,
        };

        var result = RailPlaneRun.Run(new RailPlaneRequest
        {
            Board       = rq,
            RailName    = vm.Document.Rails[0].Name,
            FrequencyHz = vm.Document.Rails[0].Band.StopHz,
        });

        Assert.Null(result.Refusal);
        Assert.NotEmpty(result.Answer!.ImpedanceMap);

        // The mesh it chose is the cavity's, not the DC rules' — and it SAYS so, because a map at a
        // cell size the rest of the window is not at is one somebody will compare square for square
        // against the drop map.
        double chosen = result.Provenance!.CellSizeMetres;
        output.WriteLine(
            $"{result.Answer.ImpedanceMap.Count:N0} map cells at {chosen * 1e3:0.###} mm");
        Assert.True(chosen > 0.2e-3,
            $"the cavity took the DC rules' own {chosen * 1e3:0.###} mm cell, which is the " +
            "collision this closes");
        Assert.Contains(result.Answer.Notes, n => n.Contains("meshed itself", StringComparison.Ordinal));

        // This rail is more than one piece, so the drive reaches one of them and the others solve
        // to exactly zero — which is NOT a low impedance. They are counted and named rather than
        // dropped into an uncoloured patch nobody can account for.
        //
        // HOW MANY is not asserted (R-rail32). The rail walks to two galvanic regions at every
        // pitch; the cavity counts its pieces over cells where the rail FACES its reference, and
        // copper that leaves the reference splits one region into two there at some pitches and
        // not others — 3 at 0.5, 0.6 and 0.75 mm, 2 at 0.65, 0.703 and 0.8 mm. This test once
        // pinned 3, which was the count at the pitch the cavity happened to choose.
        Assert.True(result.Answer.Pieces > 1, $"{result.Answer.Pieces} piece(s)");
        Assert.True(result.Answer.UnreachableCells > 0);
        Assert.Contains(result.Answer.Notes,
            n => n.Contains("cannot reach", StringComparison.Ordinal)
              && n.Contains("UNCOLOURED", StringComparison.Ordinal));
        output.WriteLine(
            $"{result.Answer.UnreachableCells:N0} of {result.Answer.ImpedanceMap.Count:N0} " +
            $"cells unreachable across {result.Answer.Pieces} pieces");

        // ── And it drew, WITH ITS CALLOUTS, having never run the DC solve ────────────────────
        //
        // `null` is the DC result here and that is the whole point: the plane run does not need
        // one. Until this the |Z| map's callouts came only from the DC answer, so pressing Find
        // without ever pressing Run gave a correct map with no ports on it at all — the driven
        // one included, which is the origin of every number on the picture (owner, 2026-09-19).
        var scene = RailMapScene.Build(null, RailMapKind.Impedance, Dbu, result.Answer);
        Assert.NotEmpty(scene.Tiles);
        Assert.Null(scene.Note);

        var named = scene.Markers.Select(m => m.Label).ToList();
        Assert.Contains("U1.VDD", named);
        Assert.Contains("U3.VDD", named);

        var drive = Assert.Single(scene.Markers, m => m.Kind == RailMarkerKind.Driven);
        Assert.Equal(result.Answer.MapPortName, drive.Label);

        // One glyph per port, never two — the DC run's markers and the plane's own are merged by
        // name, and a duplicate would read as two ports on one pad.
        Assert.Equal(named.Count, named.Distinct().Count());

        // And where a DC result IS present the two agree about the PLACE, which is what makes
        // merging them by name legitimate.
        var withDc = RailMapScene.Build(
            RailDcRun.Run(rq).Rails[0], RailMapKind.Impedance, Dbu, result.Answer);
        foreach (var m in scene.Markers.Where(m => m.Label.Contains('.', StringComparison.Ordinal)))
        {
            var same = withDc.Markers.FirstOrDefault(o => o.Label == m.Label);
            Assert.NotNull(same);
            Assert.Equal((m.X, m.Y), (same!.X, same.Y));
        }
    }

    /// <summary>
    /// <b>A stated cell size is never re-meshed, and the refusal names a size that would fit.</b>
    /// </summary>
    /// <remarks>
    /// The auto-fit above must not reach the knob the convergence sweeps turn — a run that
    /// silently re-meshed a stated cell would make those sweeps measure nothing. So that case
    /// stays a refusal, and the negative half here is what stops the fit being written as
    /// "coarsen until it fits" with no exception for it.
    /// </remarks>
    [Fact]
    public void AStatedCellSizeIsRefusedRatherThanReMeshed()
    {
        // Request already STATES 1 mm — see its own remarks for why the fixture pins the pitch.
        var result = RailPlaneRun.Run(new RailPlaneRequest
        {
            Board       = Request(secondPort: false),
            RailName    = "VDD",
            FrequencyHz = FirstMode * 1.2,
            // A ceiling this rectangle's 600 cells is over, so the stated size collides with it.
            Modes       = new PdnModeOptions { MaxCells = 200 },
        });

        Assert.NotNull(result.Refusal);
        Assert.Contains("cell size this run states", result.Refusal!, StringComparison.Ordinal);
        Assert.Contains("mm.", result.Refusal, StringComparison.Ordinal);
        output.WriteLine(result.Refusal);
    }

    /// <summary>
    /// <b>The empty |Z| tab says what the picture would show, and a REFUSAL replaces it.</b>
    /// </summary>
    /// <remarks>
    /// Two faults in one state. The sentence was railRF's own vocabulary — "it is the plane pair's
    /// own answer" — followed by directions to a card on another tab; and because a refused run
    /// leaves <c>Plane</c> null on purpose, that same sentence was what a user saw after pressing
    /// Find and being refused, with the refusal printed on the tab they had left (owner,
    /// 2026-09-19). So the window owns the empty state now: one sentence, from one string, beside
    /// the button that answers it.
    /// </remarks>
    [Fact]
    public void TheEmptyImpedanceTabSaysWhatTheMapIsAndARefusalTakesItsPlace()
    {
        // ONE string, shared with the renderer — a window and a headless render that disagreed
        // about an empty state is exactly how the old sentence survived unread.
        Assert.DoesNotContain("plane pair's own answer", RailMapScene.EmptyImpedanceNote,
                              StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ohms", RailMapScene.EmptyImpedanceNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Find", RailMapScene.EmptyImpedanceNote, StringComparison.Ordinal);

        var vm = new RailRfViewModel(new RailDocument { Name = "empty" }, "");
        Assert.Equal(RailMapScene.EmptyImpedanceNote, vm.ImpedanceFinderNote);

        vm.PlaneRefusal = "This plane pair meshes to 61,129 cavity cells.";
        Assert.True(vm.HasPlaneRefusal);
        Assert.Equal("This plane pair meshes to 61,129 cavity cells.", vm.ImpedanceFinderNote);

        // And the renderer does NOT centre a second copy under the window's own panel.
        var overlay = new RailLayoutOverlay
        {
            Result = Dc(), DbuPerMicron = Dbu, Kind = RailMapKind.Impedance,
        };
        Assert.Contains("ohms", overlay.Scene.Note!, StringComparison.OrdinalIgnoreCase);

        overlay.EmptyNoteShownByHost = true;
        Assert.Null(overlay.Scene.Note);

        // A tab WITH a map is unaffected — the suppression is of an empty tab's sentence only,
        // and the window's own panel goes away with it.
        var plane = Modes(FirstMode * 1.2);
        Assert.Null(plane.Refusal);
        overlay.Plane = plane.Answer;
        Assert.NotEmpty(overlay.Scene.Tiles);

        vm.Board = BoardInputs();
        vm.SelectedBoardOverlay = RailBoardOverlay.Impedance;
        Assert.True(vm.ShowImpedanceFinder, "the empty |Z| tab must offer the controls that fill it.");

        vm.Plane = plane.Answer;
        Assert.True(vm.HasImpedanceMap);
        Assert.False(vm.ShowImpedanceFinder,
                     "the panel stayed over a map it is the empty state for.");

        // …and the frequency does NOT go away with it. The map is at ONE frequency, so changing it
        // is the next thing anybody does, and hiding the box behind a tab switch is the detour
        // this tab's own controls exist to end (owner, 2026-09-19).
        Assert.True(vm.ShowImpedanceRefind, "there is no way to re-run this map at another frequency.");
        Assert.Contains("|Z| at", vm.ImpedanceMapAt, StringComparison.Ordinal);
        Assert.Contains(plane.Answer!.MapPortName, vm.ImpedanceMapAt, StringComparison.Ordinal);

        // The two are exclusive — one frequency control on screen, never two.
        Assert.NotEqual(vm.ShowImpedanceFinder, vm.ShowImpedanceRefind);
    }

    /// <summary>
    /// <b>The run reports the stage it is on, and the stages name the size of the problem.</b>
    /// </summary>
    /// <remarks>
    /// Owner asked for a progress bar, 2026-09-19. There is no honest percentage to show: the
    /// dominant cost is one dense LAPACK call inside <c>PdnModeSolver</c> — cubic in the cell
    /// count, no callbacks, nothing to subdivide — so a determinate bar would animate a number
    /// nobody computed. What the run can report is which of four things it is doing and how big
    /// the problem turned out to be, which is the part that explains the wait.
    ///
    /// <para><b>The re-mesh stage is the one worth gating.</b> It is the reason a run on a real
    /// board takes as long as it does, and it is invisible in the result — an auto-fit that
    /// re-extracted twice and a run that never needed to look identical once the map is up.</para>
    /// </remarks>
    [Fact]
    public void TheRunReportsItsStagesAndNamesTheSizeOfTheProblem()
    {
        var stages = new List<string>();

        var result = RailPlaneRun.Run(new RailPlaneRequest
        {
            Board       = Request(secondPort: false),
            RailName    = "VDD",
            FrequencyHz = FirstMode * 1.2,
            Progress    = stages.Add,
        });

        Assert.Null(result.Refusal);
        foreach (string st in stages) output.WriteLine(st);

        Assert.Contains(stages, s => s.Contains("extracting", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.Contains("dense step", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.Contains("mapping", StringComparison.Ordinal));

        // The slow stage names the CELL COUNT, which is what the cost is cubic in — a stage line
        // that said only "solving" would not explain why one board waits and another does not.
        Assert.Contains(stages, s => s.Contains("cavity cells", StringComparison.Ordinal));

        // In order, and the solve is reported BEFORE it is paid for rather than after.
        Assert.True(stages.FindIndex(s => s.Contains("extracting", StringComparison.Ordinal))
                  < stages.FindIndex(s => s.Contains("dense step", StringComparison.Ordinal)));
        Assert.True(stages.FindIndex(s => s.Contains("dense step", StringComparison.Ordinal))
                  < stages.FindIndex(s => s.Contains("mapping", StringComparison.Ordinal)));

        // The auto-fit's own stage fires only when it fires. This rectangle states its cell size,
        // so nothing is re-meshed and nothing claims to have been.
        Assert.DoesNotContain(stages, s => s.Contains("re-meshing", StringComparison.Ordinal));
    }

    /// <summary>The fixture board, as the window holds it.</summary>
    private static RailBoardInputs BoardInputs()
    {
        var request = Request(secondPort: false);
        return new RailBoardInputs
        {
            Shapes       = request.Shapes,
            Technology   = request.Technology,
            DbuPerMicron = request.DbuPerMicron,
            Pads         = request.Pads,
        };
    }

    /// <summary>
    /// <b>A flat map says it is flat, the caption names the reading that produced it, and the
    /// copper the drive cannot reach is accounted for.</b>
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-19, running the shipped board at 10 MHz: <i>"I got a gradient from 2.405 kΩ
    /// to 2.405 kΩ … does that answer make sense? From where to where?"</i> Three separate faults
    /// behind one screenshot, and the number itself was right.
    ///
    /// <para><b>1. A flat field is the correct answer down there and looked like a broken one.</b>
    /// 10 MHz is 1/268 of that plane pair's first mode, so it is still a lumped capacitor — one
    /// connected piece, one equipotential, 1/ωC and no spatial structure at all. Drawn as a
    /// two-ended ramp with the same number at both ends, which reads as a failure.</para>
    ///
    /// <para><b>2. The caption carried the WINDOW's model kind.</b> It read
    /// <c>result.Netlist.Provenance.ModelKind</c> — the DC run's — so a window on the Fast model
    /// captioned this "Fast model". <c>RailPlaneRun</c> always meshes; it cannot be a fast answer.
    /// §2.9 rule 1 makes a result carrying the WRONG model worse than one carrying none.</para>
    ///
    /// <para><b>3. 454 of the board's 2,544 cells vanished silently.</b> Cells on a galvanically
    /// separate piece have no path to the drive, so they solve to exactly zero volts and |Z| = 0 —
    /// which is −∞ on a log ramp and dropped. The copper under them drew uncoloured, identical to
    /// copper that is not on the rail. Zero there is "not reachable", not "a dead short".</para>
    ///
    /// <para>Driven on the fixture rectangle, which is ONE piece and has its own first mode at
    /// 2.4 GHz — so a low frequency reproduces fault 1 and 2 exactly, and the shipped board's
    /// third piece is what
    /// <see cref="TheShippedExampleProducesAnImpedanceMapWithNoCellSizeTyped"/> reaches.</para>
    /// </remarks>
    [Fact]
    public void FarBelowTheFirstModeTheMapIsFlatAndSaysSoRatherThanDrawingAGradient()
    {
        // Two decades and more below this rectangle's first mode — the regime the owner was in.
        var plane = Modes(FirstMode / 100.0);
        Assert.Null(plane.Refusal);

        var answer = plane.Answer!;
        Assert.True(answer.MapIsFlat,
            $"the field varies by {answer.SpatialSpread:0.0000}×, so this is not the flat case.");

        // The FLAT case is the claim, and it is a claim about physics: 1/ωC of the pair's own
        // capacitance, with no 'where' in it.
        double z = answer.ImpedanceMap.Max(c => c.OhmsMagnitude);
        double lumped = 1.0 / (2 * Math.PI * answer.MapFrequencyHz
                               * plane.Provenance!.PlaneCapacitanceFarads);
        output.WriteLine($"map {z:0.#} Ω vs 1/ωC {lumped:0.#} Ω over {answer.Cells.Count} cells");
        Assert.True(Math.Abs(z / lumped - 1.0) < 0.25,
            $"a flat map far below the first mode IS the pair's own 1/ωC — {z:0.#} Ω against " +
            $"{lumped:0.#} Ω");

        Assert.Contains(answer.Notes, n => n.Contains("FLAT", StringComparison.Ordinal)
                                        && n.Contains("lumped capacitor", StringComparison.Ordinal));

        // ── the plate ─────────────────────────────────────────────────────────────────────────
        var scene = RailMapScene.Build(Dc(), RailMapKind.Impedance, Dbu, answer);
        Assert.Contains("EVERYWHERE", scene.Legend!.Caption, StringComparison.Ordinal);

        // The plate reads ONE value, and the ramp is collapsed so the board paints one colour.
        // Left alone, Normalise stretches a tenth of a percent across the whole cold-to-hot ramp
        // and draws a rainbow out of the fourth significant digit — which is the picture that
        // came with the two identical labels.
        Assert.Equal(scene.Legend.ColdLabel, scene.Legend.HotLabel);
        Assert.Equal(scene.ColdValue, scene.HotValue);
        Assert.All(scene.Tiles, t => Assert.Equal(0, scene.Normalise(t.Value)));

        // …and it is captioned with the reading that produced it, which is ALWAYS the mesh.
        Assert.Equal(PdnModelKind.Accurate, answer.ModelKind);
        Assert.Contains("Accurate model", scene.Legend.Caption, StringComparison.Ordinal);

        // A flat field gets NO extreme markers — two arbitrary cells of one equipotential are not
        // a gradient, and pointing at them would invent one.
        Assert.DoesNotContain(scene.Markers, m => m.Kind == RailMarkerKind.MapExtreme);

        // ── but the DRIVE is marked, which is the "from where" half ───────────────────────────
        var drive = Assert.Single(scene.Markers, m => m.Kind == RailMarkerKind.Driven);
        Assert.Equal(answer.MapPortName, drive.Label);
        Assert.Contains("measured FROM here", drive.Readout, StringComparison.Ordinal);

        // ── and a map WITH structure marks both ends, so "to where" has an answer too ─────────
        var near = Modes(FirstMode * 0.9);
        Assert.Null(near.Refusal);
        Assert.False(near.Answer!.MapIsFlat,
            "close to the first mode the field must have structure, or the negative half proves nothing.");

        var lively = RailMapScene.Build(Dc(), RailMapKind.Impedance, Dbu, near.Answer);
        var ends = lively.Markers.Where(m => m.Kind == RailMarkerKind.MapExtreme).ToList();
        Assert.Equal(2, ends.Count);
        Assert.NotEqual((ends[0].X, ends[0].Y), (ends[1].X, ends[1].Y));
        foreach (var e in ends) output.WriteLine($"{e.Label} at ({e.X}, {e.Y})");

        // Both ends are inside what Zoom to Fit frames — a callout outside the copper's bbox is
        // §11.6 trap 4, and these two are placed by the FIELD rather than by the artwork.
        foreach (var e in ends)
            Assert.True(lively.Bounds.Contains(e.X, e.Y), $"{e.Label} is outside the scene bounds.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The plane pair's answer at one frequency, through the run a window calls.
    /// </summary>
    /// <remarks>
    /// <b>The cell size is STATED, and that is what makes the sweep comparison a comparison.</b>
    /// Left to the rules the pitch would be λ/20 at whatever frequency each point is at — so every
    /// point of the ladder in <see cref="TheModeFrequencyIsWhereTheSweepsOwnImpedancePeaks"/> would
    /// be a different mesh, and "the mode list and the sweep agree" would be a claim about two
    /// discretisations rather than one. Stating it is also what keeps the gate cheap: 30 × 20 cells.
    /// </remarks>
    private static RailPlaneResult Modes(double frequencyHz, bool secondPort = false) =>
        RailPlaneRun.Run(new RailPlaneRequest
        {
            Board = Request(secondPort),
            RailName = "VDD",
            FrequencyHz = frequencyHz,
        });

    /// <summary>|Z₁₁| of the same plane pair at one frequency, through the mesh and
    /// <c>SParameterEngine</c> — one point of the sweep, and nothing about it knows what a mode
    /// is.</summary>
    private static double PortImpedance(double frequencyHz)
    {
        var request = Request(secondPort: false);
        var rail = request.Document.Rail("VDD")!;

        var extraction = PdnMeshExtractor.Extract(
            RailDcRun.RequestFor(request, rail, frequencyHz));
        Assert.Null(extraction.Refusal);

        var raw = SParameterEngine.Run(extraction.Netlist!.Netlist, [frequencyHz])["S"].ComplexValues;
        var z0 = new Complex(50, 0);
        return (z0 * (Complex.One + raw[0]) / (Complex.One - raw[0])).Magnitude;
    }

    private static RailDcResult Dc()
    {
        var run = RailDcRun.Run(Request(secondPort: false));
        Assert.Null(run.Refusal);
        return run.Rails[0];
    }

    /// <summary>
    /// A rectangular plane pair with an observation port at a corner, and optionally a second one on
    /// the centre line.
    /// </summary>
    /// <remarks>
    /// <b>No open-circuit voltage anywhere</b>, for <c>PdnCavityTests</c>' own reason: a stated one
    /// becomes a voltage branch, which in an s-parameter run is a SHORT across the plane pair, and
    /// every impedance here would be the fixture's rather than the board's.
    /// </remarks>
    private static RailDcRequest Request(bool secondPort)
    {
        var doc = new RailDocument { Name = "plane" };
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
        if (secondPort)
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U2", Pin = "VDD" } });
        doc.Rails.Add(rail);

        var pads = new List<PlacedPin> { new("U1", "VDD", "VDD", Mm(0.5), Mm(0.5), PinSource.BoardNetlist) };
        if (secondPort) pads.Add(new PlacedPin("U2", "VDD", "VDD", Mm(AMm / 2), Mm(0.5), PinSource.BoardNetlist));

        return new RailDcRequest
        {
            Document = doc,
            Technology = TestBoard(),
            DbuPerMicron = Dbu,
            Model = PdnModelKind.Accurate,
            Shapes = Shapes(),
            Pads = pads,
            // R-rail3-8's refinement is OFF: a refined band under the port makes the mesh
            // non-uniform, and this fixture's whole point is the uniform rectangle §4.5 has a
            // closed form for.
            Mesh = new PdnMeshSettings
            {
                CellSizeMetres = CellMm * 1e-3,
                PortRefinementRatio = 1,
            },
        };
    }

    private static List<LayoutShape> Shapes() =>
    [
        new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(AMm), Y2 = Mm(BMm) },
        new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(AMm), Y2 = Mm(BMm) },
    ];

    private static LayoutView Artwork()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um };
        foreach (var shape in Shapes()) view.Shapes.Add(shape);
        return view;
    }

    private static Technology TestBoard()
    {
        var tech = new Technology { Name = "plane pair" };
        tech.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", Color = new Design.Theming.Rgba(200, 120, 0), Visible = true },
            new LayerDef { Key = Bot, Name = "BOT", Color = new Design.Theming.Rgba(0, 120, 200), Visible = true },
        ];
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(OneOunceUm), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Um(CoreUm), Epsr = EpsilonR, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(OneOunceUm), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    /// <summary>The real export context, through the seam <c>RailGraphicExport</c> itself uses.</summary>
    private static LayoutClipboard.ExportContext ContextFor(RailMapScene? railMap)
        => LayoutClipboard.MakeExportContext(
            RailGraphicExport.PayloadOf(Artwork()),
            TestBoard(),
            LayoutRenderTheme.Light,
            transparent: true,
            baseDir: "",
            railMap: railMap,
            railTheme: RailMapTheme.Light);

    /// <summary>
    /// The SVG with Skia's own clip-path identifiers taken out.
    /// </summary>
    /// <remarks>
    /// <b>Not a loosening of the gate — the ids are not ours and they are not a function of the
    /// scene.</b> Skia's SVG device numbers its <c>clipPath</c> elements from a counter that lives
    /// in the PROCESS, in hex, so the second render in one process legitimately writes
    /// <c>cl_a</c> where the first wrote <c>cl_7</c>. Everything else — every rect, every colour,
    /// every label — is compared verbatim, which is where a flipped eigenvector sign would show.
    /// (The same counter is what makes a documentation-figure run report hundreds of changed files
    /// that are not changes; the trap is recorded and this is the same one.)
    /// </remarks>
    private static string WithoutSkiaIds(string svg) =>
        System.Text.RegularExpressions.Regex.Replace(svg, @"cl_[0-9a-f]+", "cl_");

    private static string SvgOf(LayoutClipboard.ExportContext ctx)
    {
        var svg = LayoutClipboard.TryRenderToSvg(ctx);
        Assert.NotNull(svg);
        return svg!.Value.Svg;
    }
}
