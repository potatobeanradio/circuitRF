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

        // Before the plane answer arrives the tab SAYS SO rather than looking like a broken map.
        overlay.Kind = RailMapKind.Impedance;
        Assert.Empty(overlay.Scene.Tiles);
        Assert.Contains("plane resonances", overlay.Scene.Note!, StringComparison.OrdinalIgnoreCase);

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

        var pads = new List<PdnPad> { new("U1", "VDD", "VDD", Mm(0.5), Mm(0.5)) };
        if (secondPort) pads.Add(new PdnPad("U2", "VDD", "VDD", Mm(AMm / 2), Mm(0.5)));

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
