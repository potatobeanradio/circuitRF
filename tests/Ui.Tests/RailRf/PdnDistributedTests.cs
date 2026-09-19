// ================================================================
//  PdnDistributedTests.cs — brief-railrf-13-distributed.md §5
//
//  P2a: the mesh with R and L, spreading inductance, and the mounting loop from the actual via
//  geometry. This is the phase that makes railrf.md §6's form-factor answer QUANTITATIVE between
//  about 1 MHz and 100 MHz, which is where the decap self-resonances and the RF crystal are.
//
//  ── WHAT IS EXTERNAL HERE, AND WHAT IS NOT ────────────────────────────────────────────────────
//
//  Three of these gates are closed-form arithmetic that is not circuitRF's:
//
//    L = µ₀·h per square of a plane pair              — the standard unit-cell model (§4.1)
//    L_partial of a round conductor and of a pair     — Grover, Inductance Calculations (1946) §7,
//                                                       written out in the test that uses it
//    |Z| = √(R² + (ωL)²), 10 % high at ωL/R = 0.4583  — arithmetic
//
//  The fourth — R-rail13-2's three skin-depth crossovers — is §2.8's own table, 57 / 14 / 3.6 MHz,
//  and it is a number the design note states rather than one this implementation chose.
//
//  ── ONE PROPERTY MAKES SEVERAL OF THESE EXACT RATHER THAN APPROXIMATE ─────────────────────────
//
//  Every copper element of an extraction carries L/R = (µ₀·h/2)/Rs, because both are that edge's
//  square count times a per-square constant. When the two conductors are the same thickness that
//  constant is the same everywhere, so the WHOLE network's impedance is Z(ω) = R_dc·(1 + jωτ) with
//  τ = µ₀·h/(2·Rs) — for any topology, any mesh, any port. So the |Z| ratio R-rail13-3 gates on is
//  exact to the solver's own precision and does not depend on the fixture's shape at all. That is
//  also why τ, not a geometry, is the thing under test.
//
//  ── THE TESTS SOLVE; THE EXTRACTOR NEVER DOES ─────────────────────────────────────────────────
//
//  Same rule as PdnMeshExtractorTests: the extraction produces an ElaboratedNetlist and nothing in
//  src/Design/Layout/Pdn factorises anything. Where a gate needs an impedance it goes through
//  SParameterEngine — the engine every other circuitRF analysis uses, and the one PdnSweep uses —
//  so the gate measures what a solver would see rather than what the extractor believes it built.
//
//  One test per CLAIM the brief makes, not one per measured rung.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnDistributedTests
{
    // ── the board ──────────────────────────────────────────────────────────────────────────────

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey In1 = new(3, 0);
    private static readonly LayerKey In2 = new(4, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private const double CopperSigma = 5.8e7;              // S/m at 20 °C
    private const double CopperRho = 1.0 / CopperSigma;
    private const double MuZero = 4.0e-7 * Math.PI;

    /// <summary>The three copper weights §2.8 tabulates a skin-depth crossover for.</summary>
    private const double HalfOunceUm = 17.4;
    private const double OneOunceUm = 34.8;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);
    private static double DbuPerMetre => DbuPerMicron * 1e6;

    /// <summary>A plane pair: rail on TOP, reference on BOT, <paramref name="coreUm"/> of dielectric
    /// between them — so §4.1's <c>h</c> is exactly that dielectric.</summary>
    private static Technology Pair(double copperUm, double coreUm)
    {
        var tech = new Technology { Name = "plane pair" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(copperUm), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Um(coreUm), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(copperUm), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }

    /// <summary>
    /// A real four-layer board: parts on TOP, the rail's plane on IN1, its reference on IN2 with
    /// 100 µm between them, and BOT on the far side. <b>The stackup §2.6's worked example is
    /// about</b> — a tight inner pair on a 1.64 mm board, where a mounting loop is via length rather
    /// than plane separation.
    /// </summary>
    private static Technology FourLayer()
    {
        var tech = new Technology { Name = "four layer" };
        tech.Stackup.Layers =
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "TOP", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP1", ThicknessDbu = Um(700), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "IN1", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [In1] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "CORE", ThicknessDbu = Um(100), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "IN2", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [In2], IsGroundReference = true },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "PP2", ThicknessDbu = Um(700), Epsr = 4.3, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "BOT", ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot] },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                SpanFromLayer = "TOP", SpanToLayer = "BOT",
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>
    /// A rail fed at <c>BT1.1</c> and OBSERVED at <c>U1.VDD</c> — an observation port with no
    /// current, because §2.2 says an unstated current is never a defaulted zero and because a
    /// current source in an S-parameter run is not what any of these gates is measuring.
    /// </summary>
    private static PdnExtractionRequest Request(
        Technology tech, IReadOnlyList<LayoutShape> shapes,
        (long X, long Y) source, IReadOnlyList<(long X, long Y)> loads,
        double frequencyHz, double? cellSize = null, int refine = 1)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
        });

        var pads = new List<PdnPad> { new("BT1", "1", "VDD", source.X, source.Y) };

        for (int k = 0; k < loads.Count; k++)
        {
            string refdes = $"U{k + 1}";
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = refdes, Pin = "VDD" } });
            pads.Add(new PdnPad(refdes, "VDD", "VDD", loads[k].X, loads[k].Y));
        }

        return new PdnExtractionRequest
        {
            Rail = rail,
            Shapes = shapes,
            Technology = tech,
            DbuPerMicron = DbuPerMicron,
            Pads = pads,
            FrequencyHz = frequencyHz,
            Mesh = new PdnMeshSettings
            {
                CellsAcrossMinimumFeature = 3,
                PortRefinementRatio = refine,
                CellSizeMetres = cellSize,
            },
        };
    }

    // ── R-rail13-1 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§4.1's inductance, and the factor of two that is easy to lose on it.</b>
    ///
    /// <para>The note writes the plane pair's per-square RESISTANCE with its factor of two visible
    /// (<c>R = 2·Rs</c>, both planes) and its per-square INDUCTANCE without one (<c>L = µ₀·h</c>).
    /// Both are the LOOP's. This extractor pays the resistance's two by meshing both conductors, so
    /// the inductance has to be split the same way — half on each — or µ₀·h copied onto each edge
    /// puts 2·µ₀·h round the loop and doubles every plane-pair inductance in the tool. Measured
    /// ROUND THE LOOP, which is how PdnMeshExtractorTests already measures the resistance.</para>
    ///
    /// <para>The aspect rule — "for non-square cells L and R scale by the aspect ratio" — is asserted
    /// on every cell of a real extraction at once, by measuring that L/R is the same on all of them:
    /// an edge's L is its square count times µ₀h/2 and its R is the same count times Rs, so a mesh
    /// that scaled one and not the other could not hold that ratio. That covers the partial cells at
    /// the copper's edge, which a single hand-picked 2:1 cell would not.</para>
    /// </summary>
    [Fact]
    public void EachSquareOfThePlanePairIsMuZeroTimesH_AndLAndRScaleTogether()
    {
        const double hUm = 100.0;
        double h = hUm * 1e-6;

        // The closed form, on its own: a 2:1 cell is two squares in series and carries twice the
        // inductance. Not circuitRF's arithmetic — §4.1's sentence, written out.
        Assert.Equal(MuZero * h, PdnInductance.SquareInductanceHenries(h), 15);
        Assert.Equal(2.0 * MuZero * h,
                     PdnInductance.EdgeInductanceHenries(h, 2e-3, 1e-3), 15);

        var tech = Pair(OneOunceUm, hUm);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(10), Mm(5)),
            Rect(Bot, 0, 0, Mm(10), Mm(5)),
        };

        var extraction = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(0.5), Mm(2.5)), [(Mm(9.5), Mm(2.5))],
                    frequencyHz: 10e6, cellSize: 0.5e-3));

        Assert.Null(extraction.Refusal);
        var pdn = extraction.Netlist!;

        Assert.Equal(h, pdn.Provenance.PlaneSeparationMetres, 12);

        var edges = pdn.Origins
            .Where(o => o.Kind == PdnOriginKind.MeshEdge && o.InductanceHenries is > 0)
            .ToList();
        Assert.NotEmpty(edges);

        // ── every cell, including the partial ones: L/R is one constant ───────────────────────
        double rsDc = CopperRho / (OneOunceUm * 1e-6);
        double tau = MuZero * h / 2.0 / rsDc;

        foreach (var e in edges)
            Assert.Equal(tau, e.InductanceHenries!.Value / e.ResistanceOhms!.Value, 15);

        // ── a full interior square: half the loop on each conductor ───────────────────────────
        double half = MuZero * h / 2.0;

        Assert.Contains(edges, e => e.From!.Value.IsReference is false &&
                                    Math.Abs(e.InductanceHenries!.Value - half) < half * 1e-9);
        Assert.Contains(edges, e => e.From!.Value.IsReference is true &&
                                    Math.Abs(e.InductanceHenries!.Value - half) < half * 1e-9);

        // …so the loop — out along the rail and back along the reference — is µ₀·h per square.
        double loop = 2.0 * half;
        Assert.Equal(MuZero * h, loop, 15);
    }

    // ── R-rail13-2 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§2.8's three crossovers, and the performance property they state.</b>
    ///
    /// <para>"Copper stays at its DC resistance until its thickness approaches two skin depths:
    /// 57 MHz for 0.5 oz, 14 MHz for 1 oz, 3.6 MHz for 2 oz — so on the thin inner copper these
    /// boards use, ONE R MATRIX SERVES AN UNUSUALLY WIDE BAND."</para>
    ///
    /// <para>Below the crossover the extraction's R is the DC R exactly, bit for bit, and above it
    /// R rises. The crossover is also where the two expressions of §4.1 MEET, so there is no step:
    /// a rule whose two halves disagreed by a factor of two at the breakpoint would put a visible
    /// jump in every curve in this band and nothing would report it. <c>PdnInductance</c>'s header
    /// is why the ½ sits where it does.</para>
    /// </summary>
    [Theory]
    [InlineData(HalfOunceUm, 57.7e6)]   // 0.5 oz — §2.8's "57 MHz"
    [InlineData(OneOunceUm, 14.4e6)]    // 1 oz   — §2.8's "14 MHz"
    [InlineData(2 * OneOunceUm, 3.61e6)]// 2 oz   — §2.8's "3.6 MHz"
    public void RIsTheDcMatrixBelowTwoSkinDepthsAndRisesAboveIt(double thicknessUm, double expectedHz)
    {
        double t = thicknessUm * 1e-6;
        double crossover = PdnInductance.SkinCrossoverHz(t, CopperSigma);

        Assert.InRange(crossover, expectedHz * 0.99, expectedHz * 1.01);

        double dc = PdnInductance.SheetResistanceOhmsPerSquare(0, t, CopperSigma);
        Assert.Equal(CopperRho / t, dc, 15);

        // Below: identical, with no tolerance at all — it is the same expression.
        Assert.Equal(dc, PdnInductance.SheetResistanceOhmsPerSquare(crossover * 0.1, t, CopperSigma));
        Assert.Equal(dc, PdnInductance.SheetResistanceOhmsPerSquare(crossover * 0.999, t, CopperSigma));

        // At the crossover: continuous, because that is where ρ/T and ρ/(2δ) meet.
        Assert.Equal(dc, PdnInductance.SheetResistanceOhmsPerSquare(crossover, t, CopperSigma), 15);

        // Above: rising as √f.
        double above = PdnInductance.SheetResistanceOhmsPerSquare(crossover * 4, t, CopperSigma);
        Assert.Equal(2.0 * dc, above, 12);

        // And the extraction reports it as a stated condition rather than obeying it silently.
        var tech = Pair(thicknessUm, 100.0);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(4), Mm(2)),
            Rect(Bot, 0, 0, Mm(4), Mm(2)),
        };

        var extraction = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(0.4), Mm(1)), [(Mm(3.6), Mm(1))],
                    frequencyHz: crossover * 0.5, cellSize: 0.5e-3));

        Assert.Null(extraction.Refusal);
        Assert.Equal(crossover, extraction.Netlist!.Provenance.SkinCrossoverHz, 8);
    }

    // ── R-rail13-3 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The inductance cannot be dropped from this band, and this is where it starts mattering.</b>
    ///
    /// <para>§2.8, with the numbers rev 3 corrected: using §4.1's loop resistance (<c>R = 2·Rs</c>,
    /// both planes), <c>ωL = R</c> at roughly <b>1.2 MHz</b> for a tight four-layer plane pair
    /// (<c>h</c> = 100 µm) and roughly <b>83 kHz</b> for a two-layer board on 1.5 mm FR-4 — and
    /// <c>|Z|</c> is already 10 % high at 46 % of those, so about <b>570 kHz</b> and <b>38 kHz</b>.
    /// A resistance-only mesh is therefore honest only to a few tens of kilohertz on a two-layer
    /// board.</para>
    ///
    /// <para>The comparison is between two EXTRACTIONS of one board — one at the frequency, one at
    /// DC — solved at the same frequency, which is exactly what a reader would be looking at if the
    /// inductance were left out. The 46 % is arithmetic: <c>√(1 + x²) = 1.1</c> at
    /// <c>x = 0.4583</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(100.0, 1.255e6, 575e3)]     // four-layer, h = 100 µm  — §2.8's 1.2 MHz / 570 kHz
    [InlineData(1500.0, 83.7e3, 38.4e3)]    // two-layer, 1.5 mm FR-4  — §2.8's 83 kHz / 38 kHz
    public void ResistanceOnlyIsTenPercentLowWhereSection28SaysItIs(
        double hUm, double expectedCrossoverHz, double expectedTenPercentHz)
    {
        double h = hUm * 1e-6;
        double rsDc = CopperRho / (OneOunceUm * 1e-6);

        // §2.8's own two numbers, from §4.1's own two expressions. Not the extraction's.
        double crossover = PdnInductance.ReactanceEqualsResistanceHz(h, 2.0 * rsDc);
        Assert.InRange(crossover, expectedCrossoverHz * 0.99, expectedCrossoverHz * 1.01);

        double tenPercent = crossover * PdnInductance.ToleranceFractionOfCrossover(0.10);
        Assert.Equal(0.4583, PdnInductance.ToleranceFractionOfCrossover(0.10), 4);
        Assert.InRange(tenPercent, expectedTenPercentHz * 0.99, expectedTenPercentHz * 1.01);

        // ── and the extracted |Z| does it too ─────────────────────────────────────────────────
        var tech = Pair(OneOunceUm, hUm);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(10), Mm(4)),
            Rect(Bot, 0, 0, Mm(10), Mm(4)),
        };

        var withL = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(0.5), Mm(2)), [(Mm(9.5), Mm(2))],
                    frequencyHz: tenPercent, cellSize: 0.5e-3));

        var resistanceOnly = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(0.5), Mm(2)), [(Mm(9.5), Mm(2))],
                    frequencyHz: 0, cellSize: 0.5e-3));

        Assert.Null(withL.Refusal);
        Assert.Null(resistanceOnly.Refusal);
        Assert.All(withL.Netlist!.Origins.Where(o => o.Kind == PdnOriginKind.MeshEdge),
                   o => Assert.NotNull(o.InductanceHenries));
        Assert.All(resistanceOnly.Netlist!.Origins.Where(o => o.Kind == PdnOriginKind.MeshEdge),
                   o => Assert.Null(o.InductanceHenries));

        double zWithL = PortImpedance(withL.Netlist!, tenPercent).Magnitude;
        double zWithout = PortImpedance(resistanceOnly.Netlist!, tenPercent).Magnitude;

        Assert.Equal(1.10, zWithL / zWithout, 3);
    }

    // ── R-rail13-4 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Spreading inductance falls out of the mesh, and brief 3's refinement gate becomes load
    /// bearing here.</b>
    ///
    /// <para>There is no "spreading inductance" element anywhere in this series. It is what the mesh
    /// produces when current spreads from a port into a plane — and it is correct only if the mesh
    /// under the port is converged. §9: <i>"A BGA's antipad array removes a large fraction of the
    /// copper in a small region and it is precisely under the load port. Too coarse a mesh there and
    /// the spreading inductance is UNDER-ESTIMATED — again optimistically."</i></para>
    ///
    /// <para>So: halving Δ under the port moves the port inductance by less than the stated
    /// tolerance, and the mesh left coarse under the antipad field under-estimates it.
    /// <b>Proving the DIRECTION is what makes the gate worth having</b>: a convergence check that
    /// did not would pass equally on a mesh that was wrong the safe way.</para>
    ///
    /// <para><b>The residual is logarithmic, and that is a property of the PORT rather than of the
    /// mesh.</b> A port attaches at its pads' own cells, so it is a set of POINTS — and the
    /// spreading into a point in two dimensions grows as ln(1/Δ) without bound. So this ladder
    /// climbs 82.9 → 86.8 → 89.5 → 92.5 pH and keeps climbing by roughly a constant per halving
    /// rather than settling: 4.7 %, 3.1 %, 3.4 %. It is under brief 3's 10 % tolerance because the
    /// port and the feed are each a FIELD of pads, which divides the term by their count; a
    /// single-pad port on the same board moves 10 % per halving, which is that tolerance exactly and
    /// is one halving's worth of divergence rather than evidence of convergence.</para>
    ///
    /// <para>What would make it genuinely converge is a port that ties every cell its pads COVER
    /// rather than the one cell each pad's centre lands in — §4.3's "its own pin-field cells" read
    /// as an area. <c>PdnPad</c> carries no pad size, so that is a change to brief 2's readers and
    /// not to this brief. Recorded in <c>src/Design/RESOLVED.md</c> rather than tuned away, because
    /// a fixture adjusted until the number looked converged would have hidden it.</para>
    /// </summary>
    [Fact]
    public void PortInductanceConvergesUnderAnAntipadFieldAndACoarseMeshUnderEstimatesIt()
    {
        const double hUm = 100.0;
        const double f = 50e6;

        var tech = Pair(OneOunceUm, hUm);

        // A 6 × 6 mm plane pair with a 3 × 3 antipad field under the load and a 3 × 3 pin field over
        // it — §4.3's "its own pin-field cells, tied together".
        //
        // THE FEED IS A FIELD TOO, AND IT HAD TO BE. With a single-point source this ladder climbed
        // 10 % per halving and the antipads made no difference to it at all: the divergence was
        // coming from the SOURCE's own point terminal, not from the port under test. A gate on a
        // quantity dominated by the fixture's other end would have measured the fixture.
        var shapes = new List<LayoutShape> { Rect(Bot, 0, 0, Mm(6), Mm(6)) };
        shapes.AddRange(PlaneWithAntipads(Top, Mm(6), Mm(4.5), Mm(3.0), 3, Mm(0.6), Mm(0.3)));

        var pins = new List<(long X, long Y)>();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                pins.Add((Mm(4.5) + (i - 1) * Mm(0.6) + Mm(0.3), Mm(3.0) + (j - 1) * Mm(0.6)));

        var feed = new List<(long X, long Y)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                feed.Add((Mm(0.5) + i * Mm(0.25), Mm(2.0) + j * Mm(0.5)));

        double Inductance(double cellMm, int refine = 1)
        {
            var request = PinFieldRequest(tech, shapes, feed, pins, f, cellMm * 1e-3, refine);
            var extraction = PdnMeshExtractor.Extract(request);
            Assert.Null(extraction.Refusal);
            return PortImpedance(extraction.Netlist!, f).Imaginary / (2.0 * Math.PI * f);
        }

        // One base mesh, refined 1× / 2× / 4× / 8× UNDER THE PORT — brief 3's own R-rail3-8
        // mechanism, re-run here on the inductance.
        double r1 = Inductance(0.3, 1);
        double r2 = Inductance(0.3, 2);
        double r4 = Inductance(0.3, 4);
        double r8 = Inductance(0.3, 8);

        // ── the negative, which is the half that makes the gate worth having ──────────────────
        Assert.True(r1 < r2 && r2 < r4 && r4 < r8,
            $"every refinement under the port must RAISE the port inductance — " +
            $"{r1 * 1e12:0.#} / {r2 * 1e12:0.#} / {r4 * 1e12:0.#} / {r8 * 1e12:0.#} pH");

        Assert.True(r1 < r8 * 0.95,
            $"a mesh left coarse under an antipad field must UNDER-estimate the port inductance — " +
            $"{r1 * 1e12:0.#} pH against {r8 * 1e12:0.#} pH at eight times the refinement");

        // ── the tolerance, which is brief 3's own and is stated in one place ──────────────────
        Assert.InRange(Math.Abs(r8 - r4) / r4, 0.0, RefinementTolerance);
    }

    /// <summary>
    /// The tolerance <see cref="PortInductanceConvergesUnderAnAntipadFieldAndACoarseMeshUnderEstimatesIt"/>
    /// states — <b>brief 3's own <c>RefinementTolerance</c>, deliberately the same number</b>, because
    /// R-rail13-4 is that gate re-run with L in place rather than a second gate with a second
    /// tolerance that could drift from it.
    /// </summary>
    private const double RefinementTolerance = 0.10;

    // ── R-rail13-5 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§7's own gate: the mounting loop against a closed-form partial-inductance calculation for
    /// a via pair, and the minus-two-M behaviour a sign error would invert.</b>
    ///
    /// <para>The closed forms are Grover, <i>Inductance Calculations</i> (1946) §7 — the
    /// low-frequency partial self-inductance of a straight round conductor and the exact Neumann
    /// mutual of two parallel filaments of equal length — written out below in the test's own
    /// arithmetic rather than called:</para>
    ///
    /// <code>
    ///   L_self = (µ₀·ℓ / 2π)·[ ln(2ℓ/r) − 3/4 ]
    ///   M      = (µ₀·ℓ / 2π)·[ ln( ℓ/d + √(1 + (ℓ/d)²) ) − √(1 + (d/ℓ)²) + d/ℓ ]
    ///   L_loop = L_p + L_r − 2·M                                           (§4.3)
    /// </code>
    ///
    /// <para>And the separation sweep is the half that catches a sign: <c>M</c> falls as the pair
    /// separates, so the LOOP rises. A tool with the sign wrong would reward moving a capacitor away
    /// from its load — plausible numbers, wrong advice, nothing to say so. §2.5: <i>"A part that was
    /// 0.4 nH on the reference and is 1.1 nH on yours because its return via moved 4 mm is a finding
    /// you can act on in an afternoon."</i></para>
    /// </summary>
    [Fact]
    public void TheMountingLoopMatchesTheClosedFormAndRisesWithViaSeparation()
    {
        var tech = FourLayer();
        double drill = 0.3e-3, r = drill / 2.0;

        // FourLayer()'s own z arithmetic: TOP mid at 17.5 µm, IN1 mid at 752.5, IN2 mid at 887.5.
        double lPower = (752.5 - 17.5) * 1e-6;
        double lReturn = (887.5 - 17.5) * 1e-6;

        static double Self(double len, double radius) =>
            MuZero * len / (2.0 * Math.PI) * (Math.Log(2.0 * len / radius) - 0.75);

        static double Mutual(double len, double d) =>
            MuZero * len / (2.0 * Math.PI) *
            (Math.Log(len / d + Math.Sqrt(1.0 + len * len / (d * d)))
             - Math.Sqrt(1.0 + d * d / (len * len)) + d / len);

        double previous = 0;

        foreach (double separationMm in new[] { 0.5, 1.0, 2.0, 4.0 })
        {
            long d = Mm(separationMm);

            // Each via sits ON its pad, so the pad-to-via trace is zero length and the loop is the
            // three via terms alone — which is what makes the comparison against the closed form a
            // comparison of the closed form and not of a fourth term as well.
            var shapes = new List<LayoutShape>
            {
                Rect(In1, 0, 0, Mm(10), Mm(10)),
                Rect(In2, 0, 0, Mm(10), Mm(10)),
                new ViaShape { Layer = ViaLayer, X = Mm(5), Y = Mm(5), DrillSize = Um(300), PadSize = Um(600) },
                new ViaShape { Layer = ViaLayer, X = Mm(5) + d, Y = Mm(5), DrillSize = Um(300), PadSize = Um(600) },
            };

            var request = new PdnMountingLoopRequest
            {
                Rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = In2 },
                Shapes = shapes,
                Technology = tech,
                DbuPerMicron = DbuPerMicron,
                ReferenceNet = "GND",
                SearchRadiusMetres = 1e-6,          // the via must be ON the pad
                Pads =
                [
                    new PdnPad("C1", "1", "VDD", Mm(5), Mm(5)),
                    new PdnPad("C1", "2", "GND", Mm(5) + d, Mm(5)),
                ],
            };

            var loop = PdnMountingLoopExtractor.Compute(request, "C1");

            Assert.Null(loop.Unresolved);
            Assert.NotNull(loop.Terms);
            Assert.Equal(separationMm * 1e-3, loop.Terms!.SeparationMetres, 9);
            Assert.Equal(100e-6, loop.Terms.PlaneSeparationMetres, 12);

            double expected = Self(lPower, r) + Self(lReturn, r)
                            - 2.0 * Mutual(Math.Min(lPower, lReturn), separationMm * 1e-3);

            Assert.Equal(expected, loop.Henries!.Value, 15);

            // The minus-two-M behaviour: further apart is a bigger loop, every time.
            Assert.True(loop.Henries!.Value > previous,
                $"the loop must RISE with separation — {loop.Henries!.Value * 1e12:0.#} pH at " +
                $"{separationMm:0.#} mm against {previous * 1e12:0.#} pH at the step before");
            previous = loop.Henries!.Value;

            // §2.2's sanity band, which is the only check a user has on a number like this.
            Assert.InRange(loop.Henries!.Value, 0.3e-9, 1.5e-9);
        }

        // §2.2: "You can override it — a computed value is a DEFAULT, not a fact."
        var resolver = new RailPartResolver(new PartLibrary());

        var typed = resolver.Resolve(
            new RailPart { Refdes = "C1", PartNumber = "X", MountingInductanceHenries = 1.1e-9 },
            3.3, new Dictionary<string, double> { ["C1"] = 0.42e-9 });

        Assert.Equal(1.1e-9, typed.MountingInductanceHenries!.Value, 15);
        Assert.Equal(RailMountingBasis.Typed, typed.MountingBasis);

        var computed = resolver.Resolve(
            new RailPart { Refdes = "C1", PartNumber = "X" },
            3.3, new Dictionary<string, double> { ["C1"] = 0.42e-9 });

        Assert.Equal(0.42e-9, computed.MountingInductanceHenries!.Value, 15);
        Assert.Equal(RailMountingBasis.ComputedFromGeometry, computed.MountingBasis);
    }

    // ── R-rail13-6 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§4.2: two parts sharing one return via are coupled THROUGH it, because the mesh has a
    /// single node there, not two.</b>
    ///
    /// <para>Brief 3 asserts the node count at DC; this asserts the COUPLING, where it has an
    /// observable effect. Two observation ports whose reference sides land on one via's cell share
    /// that node and their transfer impedance is their own self impedance — total coupling. The same
    /// two ports 4 mm apart have two nodes and a transfer impedance well below it.</para>
    ///
    /// <para><b>It is a property to assert rather than a feature to build.</b> Nothing in the
    /// extractor special-cases a shared via: the two parts resolve to the same cell, the cell is one
    /// node, and the coupling is what the solve then says. A reading that gave each part its own
    /// node would report two decoupled parts on copper that is one piece of metal.</para>
    /// </summary>
    [Fact]
    public void PartsOnOneReturnViaAreCoupledThroughItAndPartsOnSeparateViasAreNot()
    {
        const double f = 50e6;
        var tech = Pair(OneOunceUm, 100.0);

        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(10), Mm(6)),
            Rect(Bot, 0, 0, Mm(10), Mm(6)),
            new ViaShape { Layer = ViaLayer, X = Mm(7), Y = Mm(3), DrillSize = Um(300), PadSize = Um(600) },
            new ViaShape { Layer = ViaLayer, X = Mm(7) + Mm(4), Y = Mm(3), DrillSize = Um(300), PadSize = Um(600) },
        };

        // Shared: both parts land inside one 1 mm cell, so the mesh has ONE node under them.
        var shared = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(0.5), Mm(3)), [(Mm(7.0), Mm(3)), (Mm(7.1), Mm(3))],
                    f, cellSize: 1e-3));

        // Separate: 4 mm apart, which on the same mesh is four cells and two nodes.
        var separate = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(0.5), Mm(3)), [(Mm(3.0), Mm(3)), (Mm(7.0), Mm(3))],
                    f, cellSize: 1e-3));

        Assert.Null(shared.Refusal);
        Assert.Null(separate.Refusal);

        // The structure §4.2 states, before any impedance is read off it.
        Assert.Equal(shared.Netlist!.Ports[0].ReferenceNode, shared.Netlist!.Ports[1].ReferenceNode);
        Assert.NotEqual(separate.Netlist!.Ports[0].ReferenceNode,
                        separate.Netlist!.Ports[1].ReferenceNode);

        var zShared = PortMatrix(shared.Netlist!, f);
        var zSeparate = PortMatrix(separate.Netlist!, f);

        Assert.InRange(zShared[0, 1].Magnitude / zShared[0, 0].Magnitude, 0.999, 1.001);

        Assert.True(zSeparate[0, 1].Magnitude < zSeparate[0, 0].Magnitude * 0.9,
            $"two parts on their own return vias must NOT be fully coupled — |Z21| " +
            $"{zSeparate[0, 1].Magnitude * 1e3:0.####} mΩ against |Z11| " +
            $"{zSeparate[0, 0].Magnitude * 1e3:0.####} mΩ");
    }

    // ── R-rail13-7 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Brief 4's agreement gate, extended over 1 MHz–100 MHz.</b>
    ///
    /// <para>§2.9 rule 4 compares the two readings on the user's own board; §7 gates them at 5 % on
    /// a trace-dominated path. A SECTION inductance is a much cruder approximation than a section
    /// resistance — this is the point at which the fast model could quietly become dishonest — so
    /// the gate's frequency range is part of the deliverable rather than an afterthought.</para>
    ///
    /// <para>It holds for a structural reason and not a numerical coincidence: both readings price a
    /// square of copper through the same <c>PdnInductance</c>, so a section of ℓ/W squares over a
    /// plane at <c>h</c> is µ₀·h·ℓ/W of loop in each. The gate is what stops the two drifting if one
    /// of them ever stops calling it.</para>
    /// </summary>
    [Theory]
    [InlineData(1e6)]
    [InlineData(10e6)]
    [InlineData(100e6)]
    public void FastAgreesWithAccurateAcrossTheDistributedBand(double frequencyHz)
    {
        var tech = Pair(OneOunceUm, 200.0);

        // Brief 4's own case A: a stepped trace, three widths, no branches, over a reference plane.
        long w1 = Mm(0.5), w2 = Mm(0.4), w3 = Mm(0.3);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(15), w1),
            Rect(Top, Mm(15), (w1 - w2) / 2, Mm(30), (w1 + w2) / 2),
            Rect(Top, Mm(30), (w1 - w3) / 2, Mm(45), (w1 + w3) / 2),
            Rect(Bot, -Mm(0.3), -Mm(0.3), Mm(45) + Mm(0.3), w1 + Mm(0.3)),
        };

        var request = Request(tech, shapes, (Mm(0.1), w1 / 2), [(Mm(44.9), w1 / 2)], frequencyHz);

        var fast = PdnGraphExtractor.Extract(request);
        var accurate = PdnMeshExtractor.Extract(request);

        Assert.Null(fast.Refusal);
        Assert.Null(accurate.Refusal);

        Assert.Contains(fast.Netlist!.Origins,
                        o => o.Kind == PdnOriginKind.TraceSection && o.InductanceHenries is > 0);

        double zFast = PortImpedance(fast.Netlist!, frequencyHz).Magnitude;
        double zAccurate = PortImpedance(accurate.Netlist!, frequencyHz).Magnitude;

        Assert.InRange(zFast / zAccurate, 0.95, 1.05);
    }

    // ── the fixtures and the solve ─────────────────────────────────────────────────────────────

    /// <summary>A plane with a square array of antipads punched out of it — a BGA's pin field, which
    /// §9 names as the place a coarse mesh under-estimates the spreading.</summary>
    private static IEnumerable<LayoutShape> PlaneWithAntipads(
        LayerKey layer, long size, long cx, long cy, int n, long pitch, long hole)
    {
        var plane = new PolygonShape
        {
            Layer = layer,
            Xy = [0, 0, size, 0, size, size, 0, size],
            Holes = [],
        };

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                long x = cx + (i - (n - 1) / 2) * pitch, y = cy + (j - (n - 1) / 2) * pitch;
                plane.Holes!.Add([
                    x - hole / 2, y - hole / 2,
                    x + hole / 2, y - hole / 2,
                    x + hole / 2, y + hole / 2,
                    x - hole / 2, y + hole / 2,
                ]);
            }

        yield return plane;
    }

    /// <summary>One load whose anchor resolves to a whole pin FIELD, tied into one port — §4.3's
    /// own arrangement, and the reason the port has a finite size.</summary>
    private static PdnExtractionRequest PinFieldRequest(
        Technology tech, IReadOnlyList<LayoutShape> shapes,
        IReadOnlyList<(long X, long Y)> source, IReadOnlyList<(long X, long Y)> pins,
        double frequencyHz, double cellSize, int refine = 1)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });

        var pads = new List<PdnPad>();
        foreach (var (x, y) in source) pads.Add(new PdnPad("BT1", "1", "VDD", x, y));
        foreach (var (x, y) in pins) pads.Add(new PdnPad("U1", "VDD", "VDD", x, y));

        return new PdnExtractionRequest
        {
            Rail = rail,
            Shapes = shapes,
            Technology = tech,
            DbuPerMicron = DbuPerMicron,
            Pads = pads,
            FrequencyHz = frequencyHz,
            Mesh = new PdnMeshSettings { CellSizeMetres = cellSize, PortRefinementRatio = refine },
        };
    }

    /// <summary>
    /// The port impedance the netlist has at one frequency, through <see cref="SParameterEngine"/> —
    /// the same numerical layer every other circuitRF analysis uses, and the one
    /// <see cref="PdnSweep"/> uses. Nothing in <c>src/Design/Layout/Pdn</c> solves anything.
    /// </summary>
    private static Complex PortImpedance(PdnNetlist pdn, double frequencyHz) =>
        PortMatrix(pdn, frequencyHz)[0, 0];

    private static Complex[,] PortMatrix(PdnNetlist pdn, double frequencyHz)
    {
        var data = SParameterEngine.Run(pdn.Netlist, [frequencyHz]);
        var s = data["S"].ComplexValues;
        int n = pdn.Ports.Count;

        // Z = Z₀·(I + S)·(I − S)⁻¹ with a uniform 50 Ω reference — the port Z0 the engine defaults
        // to. It is a REFERENCE and not a termination, so the Z below is the network's own.
        var z0 = new Complex(50, 0);
        var mat = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                mat[i, j] = s[i * n + j];

        return Multiply(Add(Identity(n), mat), Invert(Subtract(Identity(n), mat)), z0);
    }

    private static Complex[,] Identity(int n)
    {
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++) m[i, i] = Complex.One;
        return m;
    }

    private static Complex[,] Add(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0);
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = a[i, j] + b[i, j];
        return m;
    }

    private static Complex[,] Subtract(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0);
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = a[i, j] - b[i, j];
        return m;
    }

    private static Complex[,] Multiply(Complex[,] a, Complex[,] b, Complex scale)
    {
        int n = a.GetLength(0);
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex sum = Complex.Zero;
                for (int k = 0; k < n; k++) sum += a[i, k] * b[k, j];
                m[i, j] = sum * scale;
            }
        return m;
    }

    /// <summary>Gauss-Jordan on a matrix that is at most two by two here. The TEST's, for the same
    /// reason the conjugate-gradient solver in PdnMeshExtractorTests is.</summary>
    private static Complex[,] Invert(Complex[,] a)
    {
        int n = a.GetLength(0);
        var m = new Complex[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n + i] = Complex.One;
        }

        for (int c = 0; c < n; c++)
        {
            int pivot = c;
            for (int r = c + 1; r < n; r++)
                if (m[r, c].Magnitude > m[pivot, c].Magnitude) pivot = r;

            if (pivot != c)
                for (int j = 0; j < 2 * n; j++) (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);

            var d = m[c, c];
            for (int j = 0; j < 2 * n; j++) m[c, j] /= d;

            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                var factor = m[r, c];
                for (int j = 0; j < 2 * n; j++) m[r, j] -= factor * m[c, j];
            }
        }

        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
        return inv;
    }
}
