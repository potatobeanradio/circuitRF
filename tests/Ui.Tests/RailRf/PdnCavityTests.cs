// ================================================================
//  PdnCavityTests.cs — brief-railrf-14-cavity.md §5
//
//  P2b: §4.1's last two terms — the shunt capacitance to the reference plane and the dielectric
//  loss across it — and the sampling that makes a narrow plane resonance findable.
//
//  ── WHAT IS EXTERNAL HERE ─────────────────────────────────────────────────────────────────────
//
//  Every gate below is closed-form arithmetic that is not circuitRF's:
//
//    C = ε₀εᵣA/h                              — the parallel-plate capacitor (§7's "lumped limit")
//    Z_in = Z₀·coth(γℓ), open-circuited        — the lossy uniform line, textbook, written out in
//    γ = √((R'+jωL')(G'+jωC'))                   OneDimensionalLine below
//    f_m = m·c / (2·√εᵣ·a)                     — the rectangular cavity's own modes
//    tan(βa)/βa − 1                            — what leaving a distributed shunt out costs
//
//  ── WHY A ONE-CELL-WIDE STRIP IS THE CAVITY FIXTURE, AND IT IS NOT A SHORTCUT ─────────────────
//
//  §4.1's mesh over a strip exactly one cell wide IS the classical LC ladder discretisation of a
//  uniform line: each cell's shunt is C'Δ, each edge's loop inductance is L'Δ and each edge's loop
//  resistance is R'Δ, all three arrived at from the AREAS rather than assumed. So the mesh has an
//  exact continuum limit with a textbook closed form — including its loss — and the only error left
//  is the ladder's own dispersion, which is (π/2N)²/6 in frequency and is 0.07 % at the 25 cells
//  these fixtures use. A two-dimensional cavity's closed form is a doubly-infinite modal sum whose
//  own truncation error would then be the thing under test.
//
//  The 2-D mesh is still gated: the lumped limit and the overlap area are both taken on a square
//  plane pair with a cutout in it, which is where "the area is the OVERLAP, not the outline" is a
//  claim with teeth.
//
//  ── THE TESTS SOLVE; THE EXTRACTOR NEVER DOES ─────────────────────────────────────────────────
//
//  Same rule as PdnMeshExtractorTests and PdnDistributedTests: the extraction produces an
//  ElaboratedNetlist and nothing in src/Design/Layout/Pdn factorises anything. Where a gate needs an
//  impedance it goes through SParameterEngine.
//
//  One test per CLAIM the brief makes, not one per measured rung.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnCavityTests
{
    // ── the board ──────────────────────────────────────────────────────────────────────────────

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private const double CopperSigma = 5.8e7;              // S/m at 20 °C
    private const double CopperRho = 1.0 / CopperSigma;
    private const double OneOunceUm = 34.8;
    private const double MuZero = 4.0e-7 * Math.PI;
    private const double C0 = 299_792_458.0;

    /// <summary>ε₀ from µ₀ and c, which is how <see cref="PdnCavity.Epsilon0"/> is derived. Written
    /// out here so the gate is the constant's DEFINITION rather than a copy of its value.</summary>
    private const double Eps0 = 1.0 / (MuZero * C0 * C0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>A plane pair: rail on TOP, reference on BOT, <paramref name="coreUm"/> of dielectric
    /// between them — so §4.1's <c>h</c> is exactly that dielectric and §4.1's medium is exactly
    /// this one entry.</summary>
    private static Technology Pair(double coreUm, double epsr, double tanD, string coreName = "CORE")
    {
        var tech = new Technology { Name = "plane pair" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(OneOunceUm), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = coreName,
                ThicknessDbu = Um(coreUm), Epsr = epsr, TanD = tanD,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(OneOunceUm), SigmaSm = CopperSigma, DrawingLayers = [Bot],
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

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>
    /// A rail OBSERVED at <c>U1.VDD</c> and, where <paramref name="sourceOhms"/> says so, fed at
    /// <c>BT1.1</c> through a series resistance alone.
    ///
    /// <para><b>No open-circuit voltage anywhere in this file.</b> A stated one becomes a voltage
    /// branch, which in an s-parameter run is a SHORT across the plane pair — every impedance below
    /// would then be the fixture's rather than the board's. §2.2 is explicit that an unstated value
    /// is never a defaulted one, so a source with only an impedance is a legal rail and exactly the
    /// one these gates need.</para>
    /// </summary>
    private static PdnExtractionRequest Request(
        Technology tech, IReadOnlyList<LayoutShape> shapes,
        (long X, long Y) load, double frequencyHz,
        (long X, long Y)? source = null, double? sourceOhms = null, double? cellSize = null)
    {
        var rail = new RailSpec { Name = "VDD", NetName = "VDD", ReferenceLayer = Bot };
        var pads = new List<PdnPad> { new("U1", "VDD", "VDD", load.X, load.Y) };
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });

        if (source is { } s)
        {
            rail.Sources.Add(new RailSource
            {
                Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
                SeriesResistanceOhms = sourceOhms,
            });
            pads.Add(new PdnPad("BT1", "1", "VDD", s.X, s.Y));
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
                PortRefinementRatio = 1,
                CellSizeMetres = cellSize,
            },
        };
    }

    // ── R-rail14-3 / §7's lumped limit ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§7's own gate, and it catches three mistakes at once.</b>
    ///
    /// <para><i>"Well below the first mode, a plane pair is a parallel-plate capacitor. The extracted
    /// Z must approach 1/(jωC) with C = ε₀εᵣA/h to under 1 %."</i> A wrong permittivity, a wrong area
    /// and a wrong dielectric thickness each move that number and each is caught here — which is what
    /// makes it worth more than its cost.</para>
    ///
    /// <para><b>The board has a cutout, because the area is the OVERLAP and not the outline</b>
    /// (R-rail14-3). A 30 × 20 mm plane pair with a 10 × 10 mm hole through BOTH conductors has
    /// 500 mm² of overlap and a 600 mm² outline: a model that read the outline would be 20 % high,
    /// in the optimistic direction, and its curve would look entirely ordinary.</para>
    ///
    /// <para>The same test asserts the READOUT §9 asks for — the single number the window and the
    /// report both print — equals the same closed form. A readout computed by different arithmetic
    /// from the model it describes would be the one thing worse than no readout, because it would
    /// be believed.</para>
    /// </summary>
    [Fact]
    public void WellBelowTheFirstModeThePlanePairIsEpsilonZeroEpsilonRAOverH()
    {
        const double hUm = 200.0, epsr = 4.3, tanD = 0.02;
        double h = hUm * 1e-6;

        var tech = Pair(hUm, epsr, tanD);

        // 30 × 20 mm of plane pair with a 10 × 10 mm hole through both conductors.
        var shapes = new List<LayoutShape>();
        foreach (var layer in new[] { Top, Bot })
        {
            shapes.Add(Rect(layer, 0, 0, Mm(30), Mm(10)));                 // the band below the hole
            shapes.Add(Rect(layer, 0, Mm(10), Mm(10), Mm(20)));            // left of the hole
            shapes.Add(Rect(layer, Mm(20), Mm(10), Mm(30), Mm(20)));       // right of the hole
        }

        double overlapM2 = (30e-3 * 10e-3) + 2 * (10e-3 * 10e-3);          // 500 mm², not 600
        double expectedC = Eps0 * epsr * overlapM2 / h;

        // The first mode of the 30 mm span, from §4.5's own closed form — the frequency this gate
        // has to stay well below for "a plane pair is a capacitor" to be the right statement at all.
        double firstMode = C0 / (2.0 * Math.Sqrt(epsr) * 30e-3);
        double f = firstMode / 100.0;

        var extraction = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(15), Mm(5)), f, cellSize: 1e-3));

        Assert.Null(extraction.Refusal);
        var pdn = extraction.Netlist!;

        // ── the readout §9 says must not be buried ────────────────────────────────────────────
        Assert.Equal(overlapM2, pdn.Provenance.PlaneOverlapSquareMetres, 9);
        Assert.Equal(expectedC, pdn.Provenance.PlaneCapacitanceFarads, expectedC * 1e-9);
        Assert.Equal(epsr, pdn.Provenance.RelativePermittivity, 12);
        Assert.Equal(tanD, pdn.Provenance.LossTangent, 12);
        Assert.False(pdn.Provenance.LossTangentIsClassDefault);
        Assert.True(pdn.Provenance.ShuntBranchPresent);

        // ── and the SOLVED impedance is 1/(jωC) of that same capacitance ──────────────────────
        var z = PortImpedance(pdn, f);
        double lumped = 1.0 / (2.0 * Math.PI * f * expectedC);

        Assert.InRange(z.Magnitude / lumped, 0.99, 1.01);

        // Capacitive, not merely of the right size: the sign is what separates ε₀εᵣA/h from an
        // inductance that happens to have the same magnitude at one frequency.
        Assert.True(z.Imaginary < 0, $"a plane pair below its first mode is capacitive: {z}");
    }

    // ── R-rail14-3, the negative half ──────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The overlap is the overlap, asserted where a plane and a trace share a cell.</b>
    ///
    /// <para>The gate above removes copper from BOTH conductors at once, which min(A₁, A₂) would
    /// also get right. Here the rail is a 2 mm trace over a 20 mm plane, so every cell along it holds
    /// a little rail copper and a lot of reference copper — and the capacitance is of the 2 mm strip,
    /// not of the plane under it. A model that took either conductor's own area would be ten times
    /// high.</para>
    /// </summary>
    [Fact]
    public void TheOverlapIsTheOverlapAndNotEitherConductorsOwnArea()
    {
        const double hUm = 200.0, epsr = 4.3;
        double h = hUm * 1e-6;

        var tech = Pair(hUm, epsr, 0.02);
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, Mm(9), Mm(20), Mm(11)),        // a 20 × 2 mm rail trace
            Rect(Bot, 0, 0, Mm(20), Mm(20)),            // over a 20 × 20 mm reference plane
        };

        var extraction = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(1), Mm(10)), 10e6, cellSize: 0.5e-3));

        Assert.Null(extraction.Refusal);
        var p = extraction.Netlist!.Provenance;

        double overlapM2 = 20e-3 * 2e-3;
        Assert.Equal(overlapM2, p.PlaneOverlapSquareMetres, 9);
        Assert.Equal(Eps0 * epsr * overlapM2 / h, p.PlaneCapacitanceFarads,
                     Eps0 * epsr * overlapM2 / h * 1e-9);
    }

    // ── R-rail14-3, the flag ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Where the stackup states no tan δ, brief 11's per-class figure serves and the result is
    /// FLAGGED</b> — which here means every peak height in the cavity band is indicative.
    ///
    /// <para>§2.2 names tan δ as one of the two numbers that matter most and are most often wrong:
    /// <i>"it sets how sharp the cavity resonances are, which is the difference between a 6 dB bump
    /// and a 20 dB one."</i> Zero is what "not stated" looks like in a <c>StackupLayer</c>, so a
    /// stackup that never mentioned it would otherwise get a lossless dielectric and an infinitely
    /// sharp cavity — the optimistic direction.</para>
    /// </summary>
    [Fact]
    public void AnUnstatedTanDeltaTakesTheClassFigureAndTheResultSaysSo()
    {
        var tech = Pair(200.0, 4.3, tanD: 0.0, coreName: "FR-4 core");
        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(20), Mm(20)),
            Rect(Bot, 0, 0, Mm(20), Mm(20)),
        };

        var extraction = PdnMeshExtractor.Extract(
            Request(tech, shapes, (Mm(10), Mm(10)), 10e6, cellSize: 2e-3));

        Assert.Null(extraction.Refusal);
        var p = extraction.Netlist!.Provenance;

        Assert.True(p.LossTangentIsClassDefault);
        Assert.Equal(RailEsrDefaults.GeneralPurposeLaminateTanDelta, p.LossTangent, 12);
        Assert.Contains(p.Notes, n => n.Contains("6 dB bump", StringComparison.Ordinal));

        // …and a stackup that DID state one is not flagged, which is the half that makes the flag
        // mean something.
        var stated = PdnMeshExtractor.Extract(
            Request(Pair(200.0, 4.3, tanD: 0.004, coreName: "low-loss"), shapes,
                    (Mm(10), Mm(10)), 10e6, cellSize: 2e-3));

        Assert.False(stated.Netlist!.Provenance.LossTangentIsClassDefault);
        Assert.Equal(0.004, stated.Netlist!.Provenance.LossTangent, 12);
    }

    // ── §7's second cavity gate: the input impedance, below and THROUGH the first mode ─────────

    /// <summary>
    /// <b>§7: "A uniform rectangular plane pair has closed-form modes and a closed-form input
    /// impedance." Brief 15 gates the MODES; this gates the IMPEDANCE.</b>
    ///
    /// <para>The oracle is the open-circuited lossy uniform line — <c>Z_in = Z₀·coth(γℓ)</c> with
    /// <c>γ = √((R'+jωL')(G'+jωC'))</c> and <c>Z₀ = √((R'+jωL')/(G'+jωC'))</c> — evaluated from the
    /// stackup's own numbers and not from the extraction's. It is the standard transmission-line
    /// result, it carries both losses, and its poles are exactly §4.5's <c>f_m = m·c/(2√εᵣ·a)</c>.
    /// See this file's header for why a one-cell-wide strip is the right fixture for it.</para>
    ///
    /// <para><b>Through the mode, not merely up to it.</b> Below resonance the shunt branch only
    /// bends the curve; at resonance it IS the curve, and a model with C in the wrong place would
    /// still look plausible below it.</para>
    /// </summary>
    [Fact]
    public void TheOpenPlanePairMatchesTheLossyLineBelowAndThroughItsFirstMode()
    {
        // One cell across at half a millimetre — 100 cells along the 50 mm strip. See the residual
        // below for why the pitch is part of the claim rather than a convenience.
        var line = new OneDimensionalLine(lengthMm: 50, widthMm: 0.5, hUm: 200, epsr: 4.3, tanD: 0.02);
        double firstMode = line.FirstModeHz;

        // §4.5's own closed form, arrived at independently of the line above.
        Assert.Equal(C0 / (2.0 * Math.Sqrt(4.3) * 50e-3), firstMode, 0);

        // Six decades of nothing is not the gate: these are the points where the curve is CHANGING
        // shape — capacitive, then up through the half-wave parallel resonance and out the far side.
        //
        // THE QUARTER-WAVE ZERO AT 0.5·f₁ IS DELIBERATELY NOT AMONG THEM, and not because it
        // disagrees: |Z| passes THROUGH ZERO there, so a ratio is the wrong measure of anything.
        // (At 0.5·f₁ this mesh reads 0.56 Ω against the line's 0.003 Ω, on a curve whose own scale
        // Z₀ is 18 Ω.) The zero is also not a mode — §4.5's f_mn are the half-wave family — so
        // gating "below and through the first one" does not reach it.
        double[] fractions = [0.02, 0.10, 0.30, 0.90, 0.98, 1.00, 1.02, 1.10];

        foreach (double k in fractions)
        {
            double f = firstMode * k;
            var extraction = PdnMeshExtractor.Extract(line.Request(f));
            Assert.Null(extraction.Refusal);

            var got = PortImpedance(extraction.Netlist!, f);
            var want = line.OpenCircuitInputImpedance(f);

            Assert.InRange(got.Magnitude / want.Magnitude, 0.99, 1.01);

            // Reactive SIGN, which is the half that says the shunt branch is in the right place: an
            // open line is capacitive below its quarter-wave point and inductive above it. A model
            // with C in the wrong place can match a magnitude and cannot match this.
            if (k <= 0.30) Assert.True(got.Imaginary < 0, $"capacitive below λ/4 — {k:0.##}·f₁ gave {got}");
            if (k is 0.90 or 0.98) Assert.True(got.Imaginary > 0, $"inductive below λ/2 — {k:0.##}·f₁ gave {got}");
        }

        // ── §7's "monotone convergence in cell size", and what the residual IS ────────────────
        //
        // The port attaches at the first cell's CENTRE, half a cell in from the copper's edge, so
        // the stub the mesh presents is ℓ − Δ/2 long rather than ℓ. That is a FIRST-ORDER error in
        // Δ and it is the whole of what is left above: 2.56 % at Δ = 2 mm, 1.29 % at 1 mm, 0.65 % at
        // 0.5 mm — halving with the pitch, three times over. Asserting the convergence rather than
        // only the final number is what stops a later reader reading that 0.65 % as a modelling
        // error in the shunt branch and "fixing" it.
        double Error(double pitchMm)
        {
            var l = new OneDimensionalLine(50, pitchMm, 200, 4.3, 0.02);
            double f = l.FirstModeHz * 0.30;
            var e = PdnMeshExtractor.Extract(l.Request(f));
            Assert.Null(e.Refusal);
            return Math.Abs(PortImpedance(e.Netlist!, f).Magnitude /
                            l.OpenCircuitInputImpedance(f).Magnitude - 1.0);
        }

        double coarse = Error(2.0), middle = Error(1.0), fine = Error(0.5);

        Assert.True(middle < coarse * 0.6 && fine < middle * 0.6,
            $"halving the pitch must halve the error — {coarse:P2} / {middle:P2} / {fine:P2}");
    }

    // ── R-rail14-3's sensitivity claim ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>tan δ is load-bearing, and a model insensitive to it has a bug.</b>
    ///
    /// <para>§2.2: tan δ <i>"sets how sharp the cavity resonances are, which is the difference
    /// between a 6 dB bump and a 20 dB one."</i> Two boards an order of magnitude apart in tan δ
    /// and identical in every other respect, and the claim is the SPREAD rather than a precise
    /// number — which is why the band asserted is the note's own 6-to-20 dB rather than a figure
    /// this implementation produced.</para>
    ///
    /// <para>The same two peaks are also checked against the line's own <c>Z₀/tanh(αℓ)</c>, which is
    /// what makes this a gate on <c>G = ωC·tan δ</c> being that expression and not merely on the
    /// answer moving in the right direction. The copper's own loss is in both and is why the peaks
    /// are not simply ten times apart.</para>
    /// </summary>
    [Fact]
    public void TheCavityPeakHeightMovesWithTanDeltaByRoughlyTheSpreadTheNoteNames()
    {
        double lossy = PeakOhms(new OneDimensionalLine(50, 2, 200, 4.3, tanD: 0.02), out double lossyWant);
        double lowLoss = PeakOhms(new OneDimensionalLine(50, 2, 200, 4.3, tanD: 0.002), out double lowWant);

        // Each peak against the line's own closed form. 10 %: the peak is sampled on a 21-point
        // ladder across the resonance, so the measured maximum is a little below the true one.
        Assert.InRange(lossy / lossyWant, 0.90, 1.10);
        Assert.InRange(lowLoss / lowWant, 0.90, 1.10);

        double spreadDb = 20.0 * Math.Log10(lowLoss / lossy);
        Assert.InRange(spreadDb, 6.0, 20.0);
    }

    /// <summary>The extracted peak |Z| across the first mode, and what the line says it should be.</summary>
    private static double PeakOhms(OneDimensionalLine line, out double closedForm)
    {
        double firstMode = line.FirstModeHz;
        double best = 0;

        for (int i = 0; i <= 20; i++)
        {
            double f = firstMode * (0.985 + 0.0015 * i);
            var extraction = PdnMeshExtractor.Extract(line.Request(f));
            Assert.Null(extraction.Refusal);
            best = Math.Max(best, PortImpedance(extraction.Netlist!, f).Magnitude);
        }

        closedForm = line.OpenCircuitInputImpedance(firstMode).Magnitude;
        return best;
    }

    // ── R-rail14-4 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§4.4's whole argument, in both halves: the adaptive sweep FINDS the resonance and a log
    /// grid of the same point count STEPS OVER it.</b>
    ///
    /// <para><i>"Adaptive frequency sampling is not optional once the cavity band is in scope: plane
    /// resonances are narrow and a log grid steps straight over one."</i> The second half is what
    /// proves the first was necessary — a sampler that found the peak on a grid that would have
    /// found it anyway has demonstrated nothing.</para>
    ///
    /// <para>The board's resonance is Q ≈ 160, so its half-power width is 0.6 % of f₀ and the 40-point
    /// log grid below steps in 8 % — thirteen times wider. The curve the grid draws is smooth and is
    /// missing its own worst point by better than a factor of three, and NOTHING about it says so:
    /// the mask passes, the anti-resonance table has no row for it and the coincidence check is
    /// computed over a curve the peak is not in.</para>
    ///
    /// <para><b>Gated on the peak being FOUND, not on a speed-up</b> (R-rail14-4). The recorded
    /// finding from the EM adaptive-sweep work is that the saving tracks grid oversampling; here the
    /// motive is different and so is the gate.</para>
    /// </summary>
    [Fact]
    public void AdaptiveSamplingFindsTheNarrowResonanceAndALogGridOfTheSameSizeStepsOverIt()
    {
        var line = new OneDimensionalLine(50, 2, 200, 4.3, tanD: 0.002);
        double f0 = line.FirstModeHz;
        double truePeak = line.OpenCircuitInputImpedance(f0).Magnitude;

        // A log grid over the band, deliberately ordinary: 40 points from 100 MHz to 2 GHz is 8 %
        // per step and nothing about it is contrived to miss.
        double[] grid = LogGrid(100e6, 2e9, 40);

        Assert.DoesNotContain(grid, f => Math.Abs(f - f0) / f0 < 0.01);

        int solves = 0;
        Mat<Complex> SolveAt(double f)
        {
            solves++;
            var extraction = PdnMeshExtractor.Extract(line.Request(f));
            Assert.Null(extraction.Refusal);
            return SMatrix(extraction.Netlist!, f);
        }

        // ── the half that proves the other half was necessary ─────────────────────────────────
        var plain = PdnAdaptiveSweep.Run(grid, new Complex(50, 0), settings: null, SolveAt);

        Assert.Equal(grid.Length, plain.FrequenciesHz.Length);
        Assert.Empty(plain.AddedHz);

        double gridPeak = plain.FrequenciesHz
            .Select((_, i) => ZFromS(plain.S[i]).Magnitude)
            .Max();

        Assert.True(gridPeak < truePeak / 3.0,
            $"the log grid was supposed to step over this resonance — it read {gridPeak:0.#} Ω " +
            $"against a true peak of {truePeak:0.#} Ω");

        // ── and the sampler finds it ──────────────────────────────────────────────────────────
        solves = 0;
        var sampled = PdnAdaptiveSweep.Run(grid, new Complex(50, 0), PdnSamplingSettings.Default, SolveAt);

        Assert.NotEmpty(sampled.AddedHz);
        Assert.Equal(grid.Length + sampled.AddedHz.Count, sampled.FrequenciesHz.Length);

        // Every requested point is still on the axis, bit for bit: the search ADDS, it never moves.
        foreach (double f in grid) Assert.Contains(f, sampled.FrequenciesHz);

        var located = sampled.Resonances
            .Where(r => r.Kind == PlanarResonanceKind.Parallel)
            .OrderBy(r => Math.Abs(r.FrequencyHz - f0))
            .FirstOrDefault();

        Assert.NotNull(located);
        Assert.InRange(located!.FrequencyHz / f0, 0.99, 1.01);

        double sampledPeak = sampled.FrequenciesHz
            .Select((_, i) => ZFromS(sampled.S[i]).Magnitude)
            .Max();

        Assert.True(sampledPeak > truePeak * 0.8,
            $"the sampler was supposed to land on this resonance — it read {sampledPeak:0.#} Ω " +
            $"against a true peak of {truePeak:0.#} Ω");

        Assert.Contains(sampled.Notes, n => n.Contains("added to the grid", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The same sampling, through the sweep a user actually runs</b> — so the knob is wired and
    /// not merely present.
    ///
    /// <para><c>PdnSweep</c>'s axis is what §2.4's mask verdict, anti-resonance table, coincidence
    /// rows and removal ranking are ALL read off, so a peak the requested grid stepped over is
    /// absent from four answers at once. Here two low-ESR parts make an anti-resonance far narrower
    /// than the grid's own step; the reference is the SAME model sampled densely, because the claim
    /// under test is about sampling rather than about physics.</para>
    /// </summary>
    [Fact]
    public void TheRailSweepFindsAnAntiResonanceItsOwnGridStepsOver()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "BULK", DielectricClass = "X7R",
            CapacitanceFarads = 1e-6, SelfResonantFrequencyHz = 5.31e6, EsrOhms = 5e-4,
        });
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "DECAP", DielectricClass = "X7R",
            CapacitanceFarads = 10e-9, SelfResonantFrequencyHz = 53.1e6, EsrOhms = 5e-4,
        });

        PdnSweepResult Sweep(RailBand band, PdnSamplingSettings? sampling)
        {
            var rail = new RailSpec { Name = "VDD_CORE", Band = band };
            rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "BULK" });
            rail.Parts.Add(new RailPart { Refdes = "C2", PartNumber = "DECAP" });
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });

            var result = PdnSweep.Run(new PdnSweepRequest
            {
                Rail = rail,
                Parts = new RailPartResolver(library).ResolveAll(rail.Parts, railVoltageV: 3.6),
                Sources = [new RailSourceModel(0, "BT1", RailSourceBasis.Rl, 1.0, 1e-6, 3.6)],
                RankRemovals = false,
                Sampling = sampling,
            });
            Assert.Null(result.Refusal);
            return result;
        }

        var coarse = new RailBand(1e6, 1e8, 40, true);

        double Peak(PdnSweepResult r) => r.Ports[0].MagnitudeOhms.Max();

        double dense = Peak(Sweep(new RailBand(1e6, 1e8, 20001, true), null));
        double stepped = Peak(Sweep(coarse, null));
        var found = Sweep(coarse, PdnSamplingSettings.Default);

        Assert.True(stepped < dense / 2.0,
            $"the 40-point grid was supposed to step over this peak — {stepped:0.###} Ω against " +
            $"{dense:0.###} Ω");

        Assert.NotEmpty(found.AddedHz);
        Assert.Equal(40 + found.AddedHz.Count, found.FrequenciesHz.Length);
        Assert.True(Peak(found) > dense * 0.8,
            $"the sampler was supposed to land on it — {Peak(found):0.###} Ω against {dense:0.###} Ω");

        // The table §2.4 builds is read off that axis, so the peak now has a row in it.
        Assert.Contains(found.Ports[0].Peaks, pk => pk.PeakOhms > dense * 0.8);
    }

    // ── R-rail14-2 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Both cell-size rules bind now, and the cell is the SMALLER of the two.</b>
    ///
    /// <para>Brief 3 set the cell size from the narrowest current-carrying conductor and noted that
    /// the wavelength rule binds only from this brief onward. Here it does — so a thin-trace board
    /// takes the feature rule even at 5 GHz, and a wide-plane board with nothing narrow on it takes
    /// λ/20. Each failure is silent and they are in opposite directions: too coarse for the feature
    /// loses a thin trace's resistance, too coarse for the wavelength loses the resonance the cavity
    /// band exists to find.</para>
    ///
    /// <para>The two λ/20 figures the design note states are checked against the closed form first —
    /// 7.2 mm at 1 GHz on FR-4 and 1.4 mm at 5 GHz — so a rule that bound for the wrong reason would
    /// not pass by accident.</para>
    /// </summary>
    [Fact]
    public void TheCellSizeIsTheSmallerOfTheFeatureRuleAndTheWavelengthRule()
    {
        // §4.1's own two numbers, from the closed form rather than from the extraction.
        Assert.Equal(7.23e-3, PdnMeshExtractor.WavelengthCellSizeMetres(1e9, 4.3), 5);
        Assert.Equal(1.446e-3, PdnMeshExtractor.WavelengthCellSizeMetres(5e9, 4.3), 6);
        Assert.Equal(double.PositiveInfinity, PdnMeshExtractor.WavelengthCellSizeMetres(0, 4.3));

        var tech = Pair(200.0, 4.3, 0.02);

        // ── a thin-trace board at 5 GHz: 0.2 mm of copper at 3 cells across is 67 µm, far below
        //    the 1.4 mm λ/20 asks for, so GEOMETRY binds.
        var thin = new List<LayoutShape>
        {
            Rect(Top, 0, Mm(0.9), Mm(2), Mm(1.1)),
            Rect(Bot, 0, 0, Mm(2), Mm(2)),
        };

        var thinExtraction = PdnMeshExtractor.Extract(
            Request(tech, thin, (Mm(0.2), Mm(1)), 5e9));

        Assert.Null(thinExtraction.Refusal);
        var thinP = thinExtraction.Netlist!.Provenance;
        // Within 2 % of 0.2 mm at three across: PdnMeshExtractor measures the narrowest copper by
        // eroding it rather than by reading a drawn width, so the figure is the trace's own to
        // within a DBU or two and the gate is on which RULE bound, not on the last digit.
        Assert.InRange(thinP.CellSizeMetres, 0.2e-3 / 3.0 * 0.98, 0.2e-3 / 3.0 * 1.02);
        Assert.Contains("narrowest copper", thinP.CellSizeBasis, StringComparison.Ordinal);

        // ── a wide-plane board at 5 GHz: nothing on it is narrow, so 30 mm at 3 cells across is
        //    10 mm and WAVELENGTH binds at 1.4 mm.
        var wide = new List<LayoutShape>
        {
            Rect(Top, 0, 0, Mm(30), Mm(30)),
            Rect(Bot, 0, 0, Mm(30), Mm(30)),
        };

        var wideExtraction = PdnMeshExtractor.Extract(
            Request(tech, wide, (Mm(15), Mm(15)), 5e9));

        Assert.Null(wideExtraction.Refusal);
        var wideP = wideExtraction.Netlist!.Provenance;
        Assert.InRange(wideP.CellSizeMetres, 1.4e-3, 1.446e-3);
        Assert.Contains("shortest wavelength", wideP.CellSizeBasis, StringComparison.Ordinal);

        // ── and the same wide board at DC takes the feature rule, because λ/20 does not bind ───
        var dc = PdnMeshExtractor.Extract(Request(tech, wide, (Mm(15), Mm(15)), 0));
        Assert.Null(dc.Refusal);
        Assert.InRange(dc.Netlist!.Provenance.CellSizeMetres, 30e-3 / 3.0 * 0.98, 30e-3 / 3.0 * 1.02);
        Assert.Contains("narrowest copper", dc.Netlist!.Provenance.CellSizeBasis, StringComparison.Ordinal);
    }

    // ── R-rail14-5 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The Fast model's refusal threshold is at or below the frequency where the shunt branch
    /// actually starts to matter — measured, on this board, rather than derived and left there.</b>
    ///
    /// <para>Brief 4 <c>R-rail4-4</c> put the ceiling at a tenth of the plane pair's first cavity
    /// resonance, on the argument that leaving a distributed shunt out costs <c>tan(βa)/βa − 1</c>
    /// and that this is 3.4 % there. This brief is where "negligible" acquires a number that can be
    /// checked: run the same extraction with and without §4.1's shunt branch, find the lowest
    /// frequency at which |Z| differs by more than §7's own 5 % Fast-versus-Accurate tolerance, and
    /// assert the refusal threshold is below it.</para>
    ///
    /// <para><b>A threshold set too high is a fast model quietly answering in a band where it
    /// cannot</b>, which is the failure with no symptom.</para>
    ///
    /// <para>The two solves differ in exactly the shunt branch and in nothing else: the netlist is
    /// extracted once per frequency and the <see cref="PdnOriginKind.PlaneShunt"/> elements are
    /// removed from the copy. Comparing the mesh against the GRAPH extractor instead would have
    /// compared two readings of the copper as well, and the copper is not what is under test.</para>
    /// </summary>
    [Fact]
    public void TheFastRefusalThresholdIsBelowWhereTheShuntBranchStartsToMatter()
    {
        const double fraction = 0.05;       // §7's own Fast-vs-Accurate agreement tolerance
        var line = new OneDimensionalLine(50, 2, 200, 4.3, tanD: 0.02);

        // The rail is fed at the far end through a milliohm, which is what makes this the shorted
        // line R-rail4-4's tan(βa)/βa is about: below the threshold Z_in is ωL'ℓ and above it the
        // distributed shunt bends it away.
        double firstMatters = double.PositiveInfinity;

        for (int i = 1; i <= 40; i++)
        {
            double f = 10e6 * i;
            var extraction = PdnMeshExtractor.Extract(line.Request(f, fedThroughMilliohms: true));
            Assert.Null(extraction.Refusal);

            var pdn = extraction.Netlist!;
            double withShunt = PortImpedance(pdn, f).Magnitude;
            double without = PortImpedance(WithoutTheShuntBranch(pdn), f).Magnitude;

            if (Math.Abs(withShunt - without) / without > fraction) { firstMatters = f; break; }
        }

        Assert.True(double.IsFinite(firstMatters),
            "the shunt branch never moved |Z| by 5 % anywhere in the band this swept, so this gate " +
            "measured nothing");

        double threshold = PdnGraphExtractor.ShuntBandTopHz(50e-3, 4.3);

        Assert.True(threshold <= firstMatters,
            $"Fast refuses above {threshold / 1e6:0.#} MHz and the shunt branch already moves |Z| " +
            $"by {fraction:P0} at {firstMatters / 1e6:0.#} MHz — a threshold above that is a fast " +
            "model answering in a band where it cannot");

        // …and it is not so conservative as to be meaningless: the ceiling is within a factor of two
        // of where the term actually bites, which is what makes it a derived number rather than a
        // safe one.
        Assert.True(threshold > firstMatters / 2.0,
            $"{threshold / 1e6:0.#} MHz against {firstMatters / 1e6:0.#} MHz");
    }

    /// <summary>
    /// The same netlist with §4.1's shunt branch taken out, and NOTHING else changed.
    /// </summary>
    /// <remarks>
    /// Removed by index, descending, so the earlier indices the origins carry stay valid while the
    /// walk is running. The result is handed straight to the solver and its origins are not read
    /// again.
    /// </remarks>
    private static PdnNetlist WithoutTheShuntBranch(PdnNetlist pdn)
    {
        var drop = pdn.Origins
            .Where(o => o.Kind == PdnOriginKind.PlaneShunt)
            .Select(o => o.ComponentIndex)
            .OrderByDescending(i => i)
            .ToList();

        Assert.NotEmpty(drop);
        foreach (int i in drop) pdn.Netlist.Components.RemoveAt(i);
        return pdn;
    }


    // ── the oracle ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A plane pair one cell wide — the uniform lossy transmission line, and its textbook closed
    /// form.
    ///
    /// <para><b>Every per-unit-length quantity below comes from the STACKUP and none from the
    /// extraction.</b> <c>L' = µ₀h/w</c> and <c>C' = ε₀εᵣw/h</c> are §4.1's own per-square terms
    /// divided by the strip width; <c>R' = 2·Rs/w</c> is §4.1's loop resistance, both planes; and
    /// <c>G' = ωC'·tan δ</c> is the term this brief adds. <c>Z_in = Z₀·coth(γℓ)</c> for an
    /// open-circuited line is Pozar or any other text and is not circuitRF's arithmetic.</para>
    /// </summary>
    private sealed class OneDimensionalLine(
        double lengthMm, double widthMm, double hUm, double epsr, double tanD)
    {
        private readonly double _l = lengthMm * 1e-3;
        private readonly double _w = widthMm * 1e-3;
        private readonly double _h = hUm * 1e-6;

        public double FirstModeHz => C0 / (2.0 * Math.Sqrt(epsr) * _l);

        /// <summary>The strip, meshed at exactly one cell across — see this file's header.</summary>
        public PdnExtractionRequest Request(double frequencyHz, bool fedThroughMilliohms = false)
        {
            var tech = Pair(hUm, epsr, tanD);
            var shapes = new List<LayoutShape>
            {
                Rect(Top, 0, 0, Mm(lengthMm), Mm(widthMm)),
                Rect(Bot, 0, 0, Mm(lengthMm), Mm(widthMm)),
            };

            // The port sits on the first cell's own centre and the feed, where there is one, on the
            // last cell's.
            var load = (Mm(widthMm / 2), Mm(widthMm / 2));
            var feed = (Mm(lengthMm - widthMm / 2), Mm(widthMm / 2));

            return PdnCavityTests.Request(
                tech, shapes, load, frequencyHz,
                source: fedThroughMilliohms ? feed : null,
                sourceOhms: fedThroughMilliohms ? 1e-3 : null,
                cellSize: _w);
        }

        private Complex Series(double f) =>
            new(2.0 * PdnInductance.SheetResistanceOhmsPerSquare(f, OneOunceUm * 1e-6, CopperSigma) / _w,
                2.0 * Math.PI * f * MuZero * _h / _w);

        private Complex Shunt(double f)
        {
            double cPrime = Eps0 * epsr * _w / _h;
            double w = 2.0 * Math.PI * f;
            return new Complex(w * cPrime * tanD, w * cPrime);
        }

        /// <summary><c>Z_in = Z₀·coth(γℓ)</c> — the open-circuited lossy line.</summary>
        public Complex OpenCircuitInputImpedance(double f)
        {
            var z = Series(f);
            var y = Shunt(f);
            var gamma = Complex.Sqrt(z * y);
            var z0 = Complex.Sqrt(z / y);
            return z0 / Complex.Tanh(gamma * _l);
        }
    }

    // ── solving ────────────────────────────────────────────────────────────────────────────────

    private static double[] LogGrid(double lo, double hi, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++)
            f[i] = lo * Math.Pow(hi / lo, i / (n - 1.0));
        return f;
    }

    private static Mat<Complex> SMatrix(PdnNetlist pdn, double frequencyHz)
    {
        var raw = SParameterEngine.Run(pdn.Netlist, [frequencyHz])["S"].ComplexValues;
        int n = pdn.Ports.Count;

        var s = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                s[i, j] = raw[i * n + j];
        return s;
    }

    /// <summary>Z₁₁ from S with a uniform 50 Ω reference — a REFERENCE and not a termination, so the
    /// number below is the network's own.</summary>
    private static Complex ZFromS(Mat<Complex> s)
    {
        var z0 = new Complex(50, 0);
        return z0 * (Complex.One + s[0, 0]) / (Complex.One - s[0, 0]);
    }

    private static Complex PortImpedance(PdnNetlist pdn, double frequencyHz) =>
        ZFromS(SMatrix(pdn, frequencyHz));
}
