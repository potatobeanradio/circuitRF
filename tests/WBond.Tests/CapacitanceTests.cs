namespace CircuitRF.WBond.Tests;

/// <summary>
/// The capacitance gates of brief-wbond-capacitance §6 — C2, C3, C5, C6, C7, C9 and the C10 half
/// that lives in this project.
///
/// <para>C1 (the flag-off answer) and C8 (REF routing) need the engine and live in
/// <c>Engine.Tests/Devices/WBondCapacitanceStampTests</c>; C4's cost lives in
/// <see cref="CapacitanceCostTests"/>; the panel-facing halves of C6 and C9 live in
/// <c>Ui.Tests/WBondCapacitancePanelTests</c>.</para>
/// </summary>
public class CapacitanceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public CapacitanceTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static double ClosedFormWireOverPlane(double lengthMil, double heightMil, double diameterMil)
    {
        double l = WBondUnits.ToMetres(WBondUnits.ToNm(lengthMil, WBondUnit.Mil));
        double h = WBondUnits.ToMetres(WBondUnits.ToNm(heightMil, WBondUnit.Mil));
        double a = WBondUnits.ToMetres(WBondUnits.ToNm(diameterMil / 2.0, WBondUnit.Mil));
        return 2.0 * Math.PI * PotentialCoefficients.Epsilon0 * l / Math.Acosh(h / a);
    }

    private static CapacitanceReduction Reduce(WBondDesign design) =>
        CapacitanceReduction.Create(WireMesh.Build(design), parallel: false)
        ?? throw new InvalidOperationException("The design has no ground plane, so it has no capacitance.");

    // ---------------------------------------------------------------- C2: the image sign

    /// <summary>
    /// <b>C2 — a single horizontal wire over the plane matches the closed form
    /// <c>2πε·l/acosh(h/a)</c>.</b>
    ///
    /// <para>That closed form is the INFINITE line's, so the comparison is a convergence rather than
    /// a single number: a finite wire has end fringing, and the model has the matching end effect.
    /// Measured here — 8.8 % at l/h = 5, 3.0 % at 15, <b>0.90 % at 50</b>, 0.30 % at 150 — which is
    /// the 1/(l/h) falloff end effects have, and which a wrong kernel would not produce. The gate is
    /// taken at l/h = 50, where the brief's 1 % is met.</para>
    /// </summary>
    [Fact]
    public void C2_ASingleWireOverThePlane_MatchesTheClosedForm()
    {
        const double heightMil = 20.0, diameterMil = 1.0;
        double previous = double.MaxValue;

        foreach (double lengthMil in new[] { 100.0, 300.0, 1000.0, 3000.0 })
        {
            var cap = Reduce(TestDesigns.SingleHorizontalWire(lengthMil, heightMil, diameterMil));
            double closed = ClosedFormWireOverPlane(lengthMil, heightMil, diameterMil);
            double error = Math.Abs(cap.Maxwell(0, 0) - closed) / closed;

            _out.WriteLine($"l/h = {lengthMil / heightMil,5:F0}: C = {cap.Maxwell(0, 0) * 1e15:F3} fF, " +
                           $"closed form {closed * 1e15:F3} fF, error {error * 100:F3} %");

            Assert.True(error < previous, "The error must fall as the wire gets long relative to its height.");
            previous = error;

            if (lengthMil / heightMil >= 50.0)
                Assert.True(error < 0.01,
                    $"At l/h = {lengthMil / heightMil:F0} the model must be within 1 % of the closed " +
                    $"form; it was {error * 100:F3} %.");
        }
    }

    /// <summary>
    /// <b>C2 — the image sign, proved by flipping it.</b>
    ///
    /// <para>An image-sign error produces a finite, plausible, wrong answer rather than a NaN, so a
    /// test that cannot SEE the flip is not a test. This builds the sign-flipped matrix here — adding
    /// the image term instead of subtracting it, which is exactly what
    /// <see cref="InductanceMatrix.Block"/> correctly does for currents — and confirms it misses the
    /// closed form by a mile.</para>
    /// </summary>
    [Fact]
    public void C2_FlippingTheImageSign_BreaksTheClosedForm()
    {
        const double lengthMil = 1000.0, heightMil = 20.0, diameterMil = 1.0;
        var mesh = WireMesh.Build(TestDesigns.SingleHorizontalWire(lengthMil, heightMil, diameterMil));
        var lengths = PotentialCoefficients.WireLengths(mesh);

        // The production block: direct MINUS image.
        double correct = 1.0 / PotentialCoefficients.Block(mesh, lengths, 0, 0);

        // The flipped one: direct PLUS image, written out here rather than reached by a flag, so the
        // production code has no branch that could accidentally be taken.
        double acc = 0.0;
        for (int p = 0; p < mesh.FilamentCount; p++)
            for (int q = 0; q < mesh.FilamentCount; q++)
                acc += PotentialCoefficients.Kernel(in mesh.Filaments[p], in mesh.Filaments[q])
                     + PotentialCoefficients.Kernel(in mesh.Filaments[p], in mesh.Images[q]);

        double flipped = 1.0 / (acc / (4.0 * Math.PI * PotentialCoefficients.Epsilon0 * lengths[0] * lengths[0]));
        double closed = ClosedFormWireOverPlane(lengthMil, heightMil, diameterMil);

        _out.WriteLine($"correct {correct * 1e15:F2} fF, image sign flipped {flipped * 1e15:F2} fF, " +
                       $"closed form {closed * 1e15:F2} fF");

        Assert.True(Math.Abs(correct - closed) / closed < 0.01, "The correct sign must match the closed form.");
        Assert.True(Math.Abs(flipped - closed) / closed > 0.5,
            $"A flipped image sign must be visibly wrong, not merely inaccurate; it gave " +
            $"{flipped * 1e15:F2} fF against {closed * 1e15:F2} fF.");
    }

    /// <summary>
    /// <b>C2, independently — raising a wire lowers its capacitance.</b> An image-sign error inverts
    /// this, and it needs no closed form to state.
    /// </summary>
    [Fact]
    public void C2_RaisingAWire_LowersItsCapacitance()
    {
        double previous = double.MaxValue;

        foreach (double heightMil in new[] { 5.0, 10.0, 20.0, 40.0, 80.0 })
        {
            var cap = Reduce(TestDesigns.SingleHorizontalWire(1000.0, heightMil, 1.0));
            double c = cap.Maxwell(0, 0);

            _out.WriteLine($"h = {heightMil,5:F0} mil: {c * 1e15:F2} fF");
            Assert.True(c < previous, $"Raising the wire to {heightMil} mil must lower its capacitance.");
            previous = c;
        }
    }

    // ---------------------------------------------------------------- C3: the near/far threshold

    /// <summary>
    /// <b>C3 — the near/far threshold is measured, not guessed.</b> Sweeps it against an all-near
    /// reference and asserts that the shipped
    /// <see cref="PotentialCoefficients.FarThresholdFactor"/> is inside 0.1 %, that the sequence
    /// converges, and — the part that makes the constant a MEASUREMENT — that the value one step
    /// below it is not.
    ///
    /// <para>The measured curve is recorded on <see cref="PotentialCoefficients.FarThresholdFactor"/>
    /// itself, the way <see cref="Grover.ParallelEpsilon"/> records its own.</para>
    /// </summary>
    [Fact]
    public void C3_TheFarThresholdIsTheSmallestValueWithinATenthOfAPercent()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 60, arrayCount: 6, pointsPerWire: 7));
        var reference = CapacitanceReduction.Compute(
            mesh, PotentialCoefficients.Fill(mesh, parallel: false, farThresholdFactor: double.PositiveInfinity));

        double WorstError(double threshold)
        {
            var c = CapacitanceReduction.Compute(
                mesh, PotentialCoefficients.Fill(mesh, parallel: false, farThresholdFactor: threshold));

            double worst = 0.0;
            for (int i = 0; i < c.ArrayCount; i++)
                for (int j = 0; j < c.ArrayCount; j++)
                    worst = Math.Max(worst,
                        Math.Abs(c.Maxwell(i, j) - reference.Maxwell(i, j)) / Math.Abs(reference.Maxwell(i, i)));
            return worst;
        }

        double previous = double.MaxValue;
        foreach (double threshold in new[] { 1.0, 2.0, 3.0, 3.25, 3.5, 4.0, 5.0 })
        {
            double error = WorstError(threshold);
            _out.WriteLine($"threshold {threshold,5}: worst C_arr error {error * 100:F5} %");
            Assert.True(error <= previous, "Widening the accurate kernel's reach must not make it worse.");
            previous = error;
        }

        Assert.True(WorstError(PotentialCoefficients.FarThresholdFactor) < 1e-3,
            "The shipped far threshold must hold the array-basis capacitance to 0.1 %.");

        // The constant is the SMALLEST value inside the target — one step down misses it, which is
        // what stops it being quietly raised to a comfortable number later.
        Assert.True(WorstError(3.25) > 1e-3,
            "3.25 is expected to MISS the 0.1 % target; if it no longer does, re-measure the constant " +
            "rather than leaving it larger than it needs to be.");
    }

    // ---------------------------------------------------------------- C5: charge conservation

    /// <summary>
    /// <b>C5 — the end split conserves charge exactly:</b> <c>C1 + C2 + 2·C12</c> equals the array's
    /// own total capacitance to the reference, which is the row sum of <c>C_arr</c>.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(12, 3)]
    [InlineData(60, 6)]
    public void C5_TheEndSplitConservesCharge(int wires, int arrays)
    {
        var cap = Reduce(TestDesigns.PowerAmplifier(wireCount: wires, arrayCount: arrays, pointsPerWire: 7));

        for (int k = 0; k < cap.ArrayCount; k++)
        {
            double split = cap.InputSelfCapacitance(k) + cap.OutputSelfCapacitance(k)
                         + 2.0 * cap.EndToEndCapacitance(k);
            double total = cap.GroundShunt(k);

            _out.WriteLine($"array {k}: split {split * 1e15:F6} fF, total {total * 1e15:F6} fF");
            Assert.Equal(total, split, Math.Abs(total) * 1e-12);
        }
    }

    /// <summary>
    /// The end split's three numbers are the array's own two-port capacitance MATRIX, so the matrix
    /// must be positive semi-definite — that is what makes the negative
    /// <see cref="CapacitanceReduction.EndBridge"/> passive rather than a sign error.
    /// </summary>
    [Fact]
    public void TheEndSplitMatrixIsPositiveSemiDefinite()
    {
        var cap = Reduce(TestDesigns.PowerAmplifier(wireCount: 12, arrayCount: 3, pointsPerWire: 7));

        for (int k = 0; k < cap.ArrayCount; k++)
        {
            double c11 = cap.InputSelfCapacitance(k);
            double c22 = cap.OutputSelfCapacitance(k);
            double c12 = cap.EndToEndCapacitance(k);

            Assert.True(c11 > 0.0 && c22 > 0.0, "Both diagonal entries must be positive.");
            Assert.True(c12 > 0.0, "The two ends are one conductor, so the off-diagonal is POSITIVE.");
            Assert.True(c11 * c22 - c12 * c12 >= -Math.Abs(c11 * c22) * 1e-12,
                $"det = {c11 * c22 - c12 * c12:E3} — the two-port capacitance matrix must be PSD, or " +
                "the negative end bridge really would be a sign error.");
            Assert.Equal(-c12, cap.EndBridge(k), Math.Abs(c12) * 1e-15);
        }
    }

    // ---------------------------------------------------------------- C6: the panel's invariant

    /// <summary>
    /// <b>C6 — with no capacitance the effective inductance is <c>L_arr</c> at EVERY frequency.</b>
    ///
    /// <para>This is the invariant that makes the panel's frequency box inert whenever capacitance is
    /// off, and it holds structurally rather than to a tolerance: with the shunt matrix zero,
    /// <c>B = −Γ/ω</c> and <c>−B⁻¹/ω</c> is <c>L_arr</c> exactly.</para>
    /// </summary>
    [Fact]
    public void C6_WithNoCapacitance_TheEffectiveInductanceIsFrequencyIndependent()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 12, arrayCount: 3, pointsPerWire: 7));
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        var zero = new double[reduction.ArrayCount * reduction.ArrayCount];

        foreach (double ghz in new[] { 0.0, 0.1, 1.0, 10.0, 40.0, 100.0 })
        {
            var effective = CapacitanceReduction.EffectiveInductance(reduction, zero, ghz * 1e9);
            for (int k = 0; k < reduction.ArrayCount; k++)
                Assert.Equal(reduction[k, k], effective[k], Math.Abs(reduction[k, k]) * 1e-12);
        }
    }

    /// <summary>
    /// With capacitance, one array reduces to the shorted-stub closed form <c>L/(1 − ω²LC)</c> — the
    /// check that the M × M network really is the network the panel's own doc claims.
    /// </summary>
    [Fact]
    public void OneArray_ReducesToTheShortedStubClosedForm()
    {
        var mesh = WireMesh.Build(TestDesigns.ParallelArray(4, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0));
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        var cap = CapacitanceReduction.Create(mesh, parallel: false)!;
        var shunt = cap.TerminalShuntMatrix();

        double l = reduction[0, 0];
        double c = shunt[0];

        foreach (double ghz in new[] { 1.0, 10.0, 20.0 })
        {
            double omega = 2.0 * Math.PI * ghz * 1e9;
            double expected = l / (1.0 - omega * omega * l * c);
            double actual = CapacitanceReduction.EffectiveInductance(reduction, shunt, ghz * 1e9)[0];

            _out.WriteLine($"{ghz,5} GHz: {actual * 1e12:F2} pH against {expected * 1e12:F2} pH " +
                           $"(L_arr {l * 1e12:F2} pH)");
            Assert.Equal(expected, actual, Math.Abs(expected) * 1e-9);
        }
    }

    // ---------------------------------------------------------------- C7: shielding

    /// <summary>
    /// <b>C7 — shielding is real and is captured.</b> Two adjacent wires shield each other, so an
    /// array's capacitance is materially BELOW the sum of its wires' isolated values. This is the
    /// test that fails if someone later "optimises" the fill down to <b>P</b>'s diagonal.
    ///
    /// <para><b>The brief's own predicted ratio is arithmetically wrong and the measurement says so.</b>
    /// §4.2 gives <c>P_ij ∝ ln(√(4h²+p²)/p) = 2.31</c> at h = 250 µm, p = 100 µm; that logarithm is
    /// <c>ln(509.9/100) = 1.629</c>, not 2.31, and the ratio <c>2·P_ii/(P_ii + P_ij)</c> it feeds is
    /// therefore <b>1.386</b>, not the 1.25 the brief quotes. Measured here: <b>1.405</b>, which is
    /// the corrected analytic prediction plus the end effects a finite wire has. The claim that
    /// matters is unchanged and is what is asserted — ignoring the cross terms would overestimate by
    /// ~42 %, not by nothing.</para>
    /// </summary>
    [Fact]
    public void C7_ShieldingMakesAnArrayLessCapacitiveThanTheSumOfItsWires()
    {
        // 100 um pitch, 250 um height, 1 mil wire — §4.2's own numbers, in this project's units.
        const double pitchMil = 100.0 / 25.4, heightMil = 250.0 / 25.4;

        var two = Reduce(TestDesigns.ParallelArray(2, pitchMil, lengthMil: 100.0, heightMil: heightMil));
        var one = Reduce(TestDesigns.ParallelArray(1, pitchMil, lengthMil: 100.0, heightMil: heightMil));

        double ratio = two.GroundShunt(0) / one.GroundShunt(0);
        _out.WriteLine($"two wires {two.GroundShunt(0) * 1e15:F3} fF, one wire {one.GroundShunt(0) * 1e15:F3} fF, " +
                       $"ratio {ratio:F4} (no shielding would give 2.0000)");

        Assert.True(ratio < 1.6,
            $"A second wire at 100 um pitch must add materially less than a whole wire's worth of " +
            $"capacitance; the ratio was {ratio:F4}.");
        Assert.True(ratio > 1.0, "It must still add something.");

        // The off-diagonal of C_wire is what carries the shielding, and it is NEGATIVE. Reached
        // through the per-wire ground capacitance, which is the row sum: shielding shows up as each
        // wire's own share being below the isolated value.
        Assert.True(two.WireGroundCapacitance(0) < one.GroundShunt(0),
            "Each wire of the pair must carry less charge than it would alone.");
    }

    // ---------------------------------------------------------------- C9: resonance

    /// <summary>
    /// <b>C9 — above self-resonance the readout reports the state, not a number.</b>
    /// </summary>
    [Fact]
    public void C9_AboveSelfResonance_TheReadoutSaysSoInsteadOfPrintingANumber()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 12, arrayCount: 3, pointsPerWire: 7);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        var cap = CapacitanceReduction.Create(mesh, parallel: false)!;

        double srfGHz = CapacitanceReduction.SelfResonanceHz(reduction, cap.TerminalShuntMatrix()) * 1e-9;
        Assert.True(double.IsFinite(srfGHz) && srfGHz > 0.0, "This design must have a self-resonance.");
        _out.WriteLine($"SRF {srfGHz:F2} GHz");

        design.ReadoutFrequencyGHz = srfGHz * 0.5;
        var below = PanelReadout.Build(design, mesh, reduction, cap);
        Assert.False(below.AboveSelfResonance);
        Assert.Equal("", below.ResonanceWarning);
        Assert.True(below.Rows[0].SelfPicoHenries > below.Rows[0].PartialPicoHenries,
            "Below resonance the effective inductance must read ABOVE the partial one.");

        design.ReadoutFrequencyGHz = srfGHz * 1.2;
        var above = PanelReadout.Build(design, mesh, reduction, cap);
        Assert.True(above.AboveSelfResonance);
        Assert.Contains("Above self-resonance", above.ResonanceWarning, StringComparison.Ordinal);
        Assert.Contains("SRF", above.ResonanceWarning, StringComparison.Ordinal);
        Assert.Equal(Math.Round(srfGHz, 1), Math.Round(above.SelfResonanceGHz, 1));
    }

    /// <summary>
    /// The 0.95 guard band is where the state flips, and it flips at the frequency the doc names —
    /// not at the resonance itself, where the expression is already unusable.
    /// </summary>
    [Fact]
    public void TheAboveResonanceStateStartsAt95PercentOfTheSrf()
    {
        var design = TestDesigns.ParallelArray(4, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        var cap = CapacitanceReduction.Create(mesh, parallel: false)!;
        double srfGHz = CapacitanceReduction.SelfResonanceHz(reduction, cap.TerminalShuntMatrix()) * 1e-9;

        design.ReadoutFrequencyGHz = srfGHz * 0.94;
        Assert.False(PanelReadout.Build(design, mesh, reduction, cap).AboveSelfResonance);

        design.ReadoutFrequencyGHz = srfGHz * 0.96;
        Assert.True(PanelReadout.Build(design, mesh, reduction, cap).AboveSelfResonance);
    }

    // ---------------------------------------------------------------- the readout with the flag off

    /// <summary>
    /// <b>C6, through the readout.</b> With <c>IncludeCapacitance</c> off, the panel's number is the
    /// partial inductance whatever the frequency box says — and the capacitance handed in is ignored,
    /// not merely absent, so a stale one cannot leak into a flag-off document.
    /// </summary>
    [Fact]
    public void C6_WithTheFlagOff_ThePanelIsIndependentOfTheFrequencyBox()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 12, arrayCount: 3, pointsPerWire: 7);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        var cap = CapacitanceReduction.Create(mesh, parallel: false);

        design.IncludeCapacitance = false;

        foreach (double ghz in new[] { 0.1, 1.0, 10.0, 100.0 })
        {
            design.ReadoutFrequencyGHz = ghz;
            var readout = PanelReadout.Build(design, mesh, reduction, cap);

            Assert.False(readout.CapacitanceIncluded);
            Assert.False(readout.AboveSelfResonance);
            for (int k = 0; k < readout.Rows.Count; k++)
            {
                Assert.Equal(reduction.PicoHenries(k, k), readout.Rows[k].SelfPicoHenries);
                Assert.Equal(reduction.PicoHenries(k, k), readout.Rows[k].PartialPicoHenries);
            }
        }
    }

    /// <summary>
    /// With the ground plane disabled there is no reference conductor, so there is no capacitance to
    /// have — whatever the flag says. The panel falls back to the partial inductance rather than
    /// inventing a reference.
    /// </summary>
    [Fact]
    public void WithNoGroundPlane_ThereIsNoCapacitance()
    {
        var design = TestDesigns.ParallelArray(4, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        design.GroundPlane.Enabled = false;

        var mesh = WireMesh.Build(design);
        Assert.Null(CapacitanceReduction.Create(mesh, parallel: false));
        Assert.Null(CapacitanceReduction.Create(design, parallel: false));

        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);
        var readout = PanelReadout.Build(design, mesh, reduction, capacitance: null);

        Assert.True(design.IncludeCapacitance, "The design still ASKS for capacitance.");
        Assert.False(readout.CapacitanceIncluded);
        Assert.Equal(reduction.PicoHenries(0, 0), readout.Rows[0].SelfPicoHenries);
    }

    // ---------------------------------------------------------------- C10: round trip

    /// <summary>
    /// <b>C10 — both fields survive a <c>.wBond</c> round trip, and a file written before they
    /// existed loads with their defaults rather than throwing.</b>
    /// </summary>
    [Fact]
    public void C10_TheTwoFieldsRoundTripAndOldFilesTakeTheDefaults()
    {
        var design = TestDesigns.ParallelArray(2, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        design.IncludeCapacitance = false;
        design.ReadoutFrequencyGHz = 24.5;

        var reloaded = WBondIo.Read(WBondIo.Write(design));
        Assert.False(reloaded.IncludeCapacitance);
        Assert.Equal(24.5, reloaded.ReadoutFrequencyGHz);

        // A file from before either field existed. Neither key is present.
        string old = """
        {
          "FormatVersion": 1,
          "GroundPlaneEnabled": true,
          "Arrays": [ { "Name": "G1", "Wires": [ { "DiameterNm": 25400, "Points": [[0,0,508000],[2540000,0,508000]] } ] } ]
        }
        """;
        var loaded = WBondIo.Read(old);
        Assert.True(loaded.IncludeCapacitance);
        Assert.Equal(10.0, loaded.ReadoutFrequencyGHz);
    }

    /// <summary><b>C10 — and through the embedded payload a schematic component carries.</b></summary>
    [Fact]
    public void C10_TheTwoFieldsSurviveTheEmbeddedPayload()
    {
        var design = TestDesigns.ParallelArray(2, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        design.IncludeCapacitance = false;
        design.ReadoutFrequencyGHz = 2.4;

        Assert.True(WBondEmbedding.TryDecode(WBondEmbedding.Encode(design), out var decoded));
        Assert.False(decoded!.IncludeCapacitance);
        Assert.Equal(2.4, decoded.ReadoutFrequencyGHz);

        // The shipped default carries capacitance ON, which is what a freshly-dropped component
        // inherits.
        Assert.True(WBondEmbedding.DefaultDesign().IncludeCapacitance);
        Assert.True(WBondEmbedding.TryDecode(WBondEmbedding.DefaultPayload, out var shipped));
        Assert.True(shipped!.IncludeCapacitance);
        Assert.Equal(10.0, shipped.ReadoutFrequencyGHz);
    }

    // ---------------------------------------------------------------- structure

    /// <summary>
    /// <b>P</b> is symmetric and positive definite — the two properties the Cholesky route depends on,
    /// and the ones a broken kernel loses first.
    /// </summary>
    [Fact]
    public void ThePotentialMatrixIsSymmetricAndPositiveDefinite()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 24, arrayCount: 4, pointsPerWire: 7));
        var p = PotentialCoefficients.Fill(mesh, parallel: false);

        for (int i = 0; i < p.Order; i++)
        {
            Assert.True(p[i, i] > 0.0, $"P[{i},{i}] must be positive.");
            for (int j = 0; j < p.Order; j++)
                Assert.Equal(p[i, j], p[j, i]);
        }

        // Positive definiteness is what CholeskyFactor asserts by succeeding.
        CholeskyFactor.Factor(p.Values, p.Order);
    }

    /// <summary>
    /// The parallel and serial fills agree bit for bit — the same guarantee the inductance fill
    /// gives, and for the same reason: the pair loop is a pure map over independent wire pairs.
    /// </summary>
    [Fact]
    public void TheParallelFillIsBitIdenticalToTheSerialOne()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 24, arrayCount: 4, pointsPerWire: 7));
        var serial = PotentialCoefficients.Fill(mesh, parallel: false);
        var concurrent = PotentialCoefficients.Fill(mesh, parallel: true);

        for (int i = 0; i < serial.Values.Length; i++)
            Assert.Equal(serial.Values[i], concurrent.Values[i]);
    }

    /// <summary>
    /// The array-basis capacitance is symmetric, its diagonal positive, and its off-diagonals
    /// negative — the Maxwell matrix's own structure, without which the <c>−C_arr[k,j]</c> lumped
    /// capacitors would come out negative.
    /// </summary>
    [Fact]
    public void TheArrayCapacitanceHasTheMaxwellStructure()
    {
        var cap = Reduce(TestDesigns.PowerAmplifier(wireCount: 60, arrayCount: 6, pointsPerWire: 7));

        for (int i = 0; i < cap.ArrayCount; i++)
        {
            Assert.True(cap.Maxwell(i, i) > 0.0);
            Assert.True(cap.GroundShunt(i) > 0.0, "An array must have a positive capacitance to the plane.");

            for (int j = 0; j < cap.ArrayCount; j++)
            {
                Assert.Equal(cap.Maxwell(i, j), cap.Maxwell(j, i));
                if (i == j) continue;

                Assert.True(cap.Maxwell(i, j) <= 0.0, $"C_arr[{i},{j}] = {cap.Maxwell(i, j):E3} must be <= 0.");
                Assert.True(cap.Mutual(i, j) >= 0.0, "The lumped inter-array capacitor must be non-negative.");
            }
        }
    }

    /// <summary>
    /// Each wire's ground capacitance sums, per array, to that array's own row sum of <c>C_arr</c> —
    /// which is what lets the end split be computed per WIRE and stamped per ARRAY.
    /// </summary>
    [Fact]
    public void PerWireGroundCapacitancesSumToTheArraysOwn()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 24, arrayCount: 4, pointsPerWire: 7);
        var mesh = WireMesh.Build(design);
        var cap = CapacitanceReduction.Create(mesh, parallel: false)!;

        for (int a = 0; a < cap.ArrayCount; a++)
        {
            double sum = 0.0;
            for (int w = 0; w < cap.WireCount; w++)
                if (mesh.ArrayOfWire[w] == a) sum += cap.WireGroundCapacitance(w);

            Assert.Equal(cap.GroundShunt(a), sum, Math.Abs(cap.GroundShunt(a)) * 1e-9);
        }
    }
}
