using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;

namespace CircuitRF.Engine.Matching;

/// <summary>
/// match.md §10 — looks <b>outward</b> from one pin of a placed <c>Match</c> into the circuit it sits
/// in, measures that network's impedance over the design band, fits the four two-element termination
/// models to it, and returns them ranked.
/// </summary>
/// <remarks>
/// <h3>The one thing that makes this correct: the <c>Match</c> is not in the circuit</h3>
/// <para>The probe works on an in-memory copy of the testbench with the <c>Match</c> instance
/// <b>deleted</b>. With it gone the two sides are electrically separate, so each pin's probe sees only
/// its own side — which is what "looking outward" means. Leaving the instance in place would measure
/// the external network in series with the very thing the user is designing.</para>
///
/// <h3>Bias is kept</h3>
/// <para>Every DC source and bias network stays. The interesting case is a transistor, and a
/// transistor's small-signal impedance is only meaningful at its operating point — so if the DC solve
/// does not converge the probe <b>refuses</b> rather than returning an impedance computed at zero
/// bias, which would be a plausible number and a wrong one.</para>
///
/// <h3>It lives in <c>src/Engine</c></h3>
/// <para>Because it runs <see cref="SParameterEngine"/>. The fit itself (<see cref="Fit"/>) is pure
/// arithmetic over a measured impedance and is public separately, so a test can pin the ranking
/// without an engine run — and so the UI can re-rank a stored measurement without re-simulating.</para>
/// </remarks>
public static class TerminationProbe
{
    /// <summary>The reference impedance the probe port presents, and the reference every Γ here is in.</summary>
    public const double ProbeZ0 = 50.0;

    /// <summary>
    /// match.md §14.5's default warning threshold on the best fit's mean |ΔΓ|. A calibration task, not
    /// a design constant — it is a <i>setting</i> everywhere it is used, and it only ever adds a
    /// warning: the residual is displayed regardless.
    /// </summary>
    public const double DefaultResidualWarning = 0.05;

    // ── Results ───────────────────────────────────────────────────────────────

    /// <summary>One of match.md §10.2's four candidate models, fitted and scored.</summary>
    /// <param name="Topology">Series or parallel against R.</param>
    /// <param name="Kind">C or L.</param>
    /// <param name="R">Fitted resistance, ohms. Negative means non-physical, and is reported anyway.</param>
    /// <param name="Value">Fitted farads or henries, per <paramref name="Kind"/>.</param>
    /// <param name="Residual">Mean |Γ_model − Γ_measured| over the band, referenced to 50 Ω.</param>
    public sealed record ProbeFit(
        TerminationTopology Topology, ReactanceKind Kind, double R, double Value, double Residual)
    {
        /// <summary>
        /// False when <see cref="R"/> ≤ 0 or <see cref="Value"/> ≤ 0 (or either is not finite). A
        /// non-physical fit stays in the ranking so the user can see it was considered; it is never
        /// auto-applied.
        /// </summary>
        public bool Physical =>
            double.IsFinite(R) && R > 0.0 && double.IsFinite(Value) && Value > 0.0;

        /// <summary>"parallel R‖C", "series R+L", … — the label the Designer shows.</summary>
        public string Name =>
            Topology == TerminationTopology.Parallel
                ? Kind == ReactanceKind.C ? "parallel R‖C" : "parallel R‖L"
                : Kind == ReactanceKind.C ? "series R+C" : "series R+L";

        /// <summary>This model's own impedance at one frequency, in ohms.</summary>
        public Complex ImpedanceAt(double freqHz)
        {
            double w = 2.0 * Math.PI * freqHz;
            if (Topology == TerminationTopology.Series)
                return Kind == ReactanceKind.C
                    ? new Complex(R, w * Value == 0 ? 0 : -1.0 / (w * Value))
                    : new Complex(R, w * Value);

            Complex y = Kind == ReactanceKind.C
                ? new Complex(1.0 / R, w * Value)
                : new Complex(1.0 / R, w * Value == 0 ? 0 : -1.0 / (w * Value));
            return Complex.One / y;
        }

        /// <summary>This fit as a <see cref="Termination"/>, stamped as probed at <paramref name="atUtc"/>.</summary>
        public Termination ToTermination(DateTime atUtc) =>
            new(R, Kind, Topology, Value, Probed: true, ProbedAtUtc: atUtc);

        /// <summary>
        /// The conjugate target this fit implies at band centre (match.md §10.3): same R, same
        /// topology, the reactance kind flipped, and the value read through match.md §5.1's own
        /// identity <c>C_eq = 1/(w0^2 L)</c>.
        /// </summary>
        /// <remarks>
        /// <b>Exact at w0, and exact in the only sense the synthesis uses.</b> Conjugating a
        /// parallel R‖C gives R/(1 - jQ), which is what a parallel R‖L with L = 1/(w0^2 C) presents at
        /// w0 — the same |Q| with the reactance the other way up. The synthesis reads a termination
        /// only through <see cref="Termination.CeqAt"/> and <see cref="Termination.QAt"/>, both at w0,
        /// so nothing downstream can tell the difference.
        ///
        /// <para><b>Why the conjugate is applied to the FIT and not to the measured data.</b> Z* is
        /// not the impedance of any two-element network — the conjugate of a parallel R‖C is a
        /// parallel R with a NEGATIVE capacitance, which fits the data essentially perfectly and can
        /// never be built. Ranking the four models against Z* therefore hands the prize to whichever
        /// PHYSICAL model happens to fit a curve nothing physical produces, and on the very fixture
        /// match.md §5.4 is written about (200 Ω ‖ 0.125 pF) that is a SERIES R+L — a different end-arm
        /// topology, and therefore a different ladder parity, from the parallel R‖L §5.4 says the user
        /// needs. Conjugating the fitted termination gives §5.4's answer and keeps every reported
        /// residual a statement about the network that was actually measured.</para>
        /// </remarks>
        public ProbeFit ConjugateAt(double omega0)
        {
            if (Kind == ReactanceKind.None || !(Value > 0) || !(omega0 > 0)) return this;
            return this with
            {
                Kind = Kind == ReactanceKind.C ? ReactanceKind.L : ReactanceKind.C,
                Value = 1.0 / (omega0 * omega0 * Value),
            };
        }
    }

    /// <summary>
    /// What one probe produced. <b>A refusal is a returned value</b>, never an exception — the same
    /// rule <c>MatchRefusal</c> follows in the synthesis.
    /// </summary>
    /// <param name="Ok">True when <see cref="Termination"/> is populated and may be applied.</param>
    /// <param name="Refusal">Why nothing was measured or nothing was applicable; null when Ok.</param>
    /// <param name="Fits">All four candidates, best residual first — including non-physical ones.</param>
    /// <param name="Best">
    /// The best-scoring PHYSICAL fit — <b>always a description of the network as measured</b>, never
    /// of a conjugate target. See <paramref name="Target"/>.
    /// </param>
    /// <param name="Target">
    /// What the Designer should aim at: <paramref name="Best"/>, conjugated at band centre when
    /// <paramref name="Conjugate"/> is on and identical to it otherwise.
    /// </param>
    /// <param name="Termination">
    /// <paramref name="Target"/> as a termination record, already carrying its probed provenance.
    /// </param>
    /// <param name="Conjugate">Whether the fitted termination was conjugated into the target.</param>
    /// <param name="Omega0">Geometric band centre, rad/s — the frequency the conjugate is exact at.</param>
    /// <param name="Flag">
    /// Non-empty when even the best residual exceeds the warning threshold. The result is still
    /// applied — this is the honest answer for a network with a resonance in band.
    /// </param>
    /// <param name="Frequencies">The band grid actually run, Hz.</param>
    /// <param name="Impedance">
    /// The measured impedance at each frequency, ohms — <b>after</b> conjugation when it was asked
    /// for, so it is the impedance every residual here is scored against.
    /// </param>
    public sealed record ProbeResult(
        bool Ok,
        string? Refusal,
        IReadOnlyList<ProbeFit> Fits,
        ProbeFit? Best,
        ProbeFit? Target,
        Termination? Termination,
        bool Conjugate,
        double Omega0,
        string Flag,
        double[] Frequencies,
        Complex[] Impedance)
    {
        /// <summary>True when the best fit is applicable but poorly describes the network.</summary>
        public bool Flagged => Flag.Length > 0;

        internal static ProbeResult Refuse(string why) =>
            new(false, why, [], null, null, null, false, 0, "", [], []);
    }

    // ── The probe ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one probe. <paramref name="bench"/> is <b>never mutated</b> — everything happens on a copy.
    /// </summary>
    /// <param name="bench">The enclosing testbench, already extracted by the caller.</param>
    /// <param name="matchInstanceName">The <c>Match</c> instance to look outward from.</param>
    /// <param name="pinIndex">0 = the Term1 side (pin 1), 1 = the Term2 side (pin 2).</param>
    /// <param name="f1">Band start, Hz.</param>
    /// <param name="f2">Band stop, Hz.</param>
    /// <param name="points">Frequency points across the band; clamped to at least 2.</param>
    /// <param name="conjugate">
    /// Return the conjugate target rather than the measured termination — match.md §10.3. The fit
    /// itself always describes the network as measured; see <see cref="ProbeFit.ConjugateAt"/> for
    /// why the conjugate is applied there and not to the data.
    /// </param>
    /// <param name="library">Cell definitions the bench references; null for a flat bench.</param>
    /// <param name="baseDirectory">Base directory for file-backed models (SnP, kits).</param>
    /// <param name="residualWarning">The §14.5 threshold, a setting.</param>
    /// <param name="settings">Engine settings; null takes the defaults.</param>
    /// <param name="control">Cancellation and progress.</param>
    public static ProbeResult Probe(
        TestBench bench,
        string matchInstanceName,
        int pinIndex,
        double f1, double f2, int points,
        bool conjugate,
        Library? library = null,
        string? baseDirectory = null,
        double residualWarning = DefaultResidualWarning,
        AnalysisSettings? settings = null,
        RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(bench);
        ArgumentNullException.ThrowIfNull(matchInstanceName);

        if (!(f1 > 0) || !(f2 > f1))
            return ProbeResult.Refuse(
                $"The probe band is not a band: f1 = {f1:G6} Hz, f2 = {f2:G6} Hz.");

        var match = bench.Instances.FirstOrDefault(
            i => string.Equals(i.InstanceName, matchInstanceName, StringComparison.Ordinal));
        if (match is null)
            return ProbeResult.Refuse(
                $"'{matchInstanceName}' is not an instance of this testbench, so there is no pin to " +
                "look outward from.");
        if (pinIndex < 0 || pinIndex >= match.NetBindings.Count)
            return ProbeResult.Refuse(
                $"'{matchInstanceName}' has {match.NetBindings.Count} pins; pin index {pinIndex} is not one of them.");

        string net = match.NetBindings[pinIndex];
        if (string.IsNullOrWhiteSpace(net))
            return ProbeResult.Refuse(
                $"Pin {pinIndex + 1} of '{matchInstanceName}' is not connected to anything.");
        if (net == "0")
            return ProbeResult.Refuse(
                $"Pin {pinIndex + 1} of '{matchInstanceName}' is tied to ground; there is no impedance to measure.");

        // ── The copy, with the Match deleted (§0) ─────────────────────────────
        var stripped = CopyWithout(bench, match);

        // Which port numbers the surviving circuit already uses. Learned by elaborating once WITHOUT
        // the probe port, because a Num can be an expression and only the elaborator resolves one.
        // Other Terms are deliberately KEPT: an input termination is part of the external network,
        // and removing it would leave that node open and measure a different circuit.
        var libs = library is null ? Array.Empty<Library>() : [library];
        List<int> existingNums;
        try
        {
            var survey = new Elaborator(libs) { BaseDirectory = baseDirectory }.Elaborate(stripped);
            existingNums = TopLevelPortNums(survey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProbeResult.Refuse($"The circuit around '{matchInstanceName}' could not be elaborated: {ex.Message}");
        }

        int probeNum = 1;
        while (existingNums.Contains(probeNum)) probeNum++;
        int probeIndex = existingNums.Count(n => n < probeNum);

        string probeName = UniqueName(stripped, $"__MatchProbe_{matchInstanceName}");
        stripped.Instances.Add(new Instance(
            probeName, "Term", [net, "0"],
            [
                new ParameterAssignment("Num", probeNum.ToString(CultureInfo.InvariantCulture)),
                new ParameterAssignment("Z", ProbeZ0.ToString("R", CultureInfo.InvariantCulture)),
            ]));

        int n = Math.Clamp(points, 2, 20001);
        var analysis = new SParameterAnalysis(
            "MatchProbe",
            new FrequencySpec(
                f1.ToString("R", CultureInfo.InvariantCulture),
                f2.ToString("R", CultureInfo.InvariantCulture),
                n));
        stripped.Analyses.Clear();
        stripped.Analyses.Add(analysis);
        double[] freqs = analysis.Expand();

        // ── Elaborate and run ─────────────────────────────────────────────────
        ElaboratedNetlist netlist;
        try
        {
            netlist = new Elaborator(libs) { BaseDirectory = baseDirectory }.Elaborate(stripped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProbeResult.Refuse($"The circuit around '{matchInstanceName}' could not be elaborated: {ex.Message}");
        }

        // §1 step 4: a nonlinear device is only meaningful at its operating point, so a DC solve that
        // does not converge is a refusal and not a fallback. SParameterEngine's own behaviour here is
        // to warn and linearize at 0 V, which is the right default for an ordinary analysis and the
        // wrong one for a measurement the user is about to design against.
        if (netlist.Components.Any(c => c.Model.Kind == ModelKind.Nonlinear))
        {
            try
            {
                var dc = NonlinearDcEngine.Run(netlist, settings);
                if (!dc.Converged)
                    return ProbeResult.Refuse(DcRefusal(dc.Iterations, dc.FinalResidual));
            }
            catch (NonlinearDcNotConvergedException ex)
            {
                return ProbeResult.Refuse(DcRefusal(ex.Iterations, ex.FinalResidual));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ProbeResult.Refuse(
                    "The DC operating point could not be solved, so there is no bias to linearize the " +
                    $"circuit at: {ex.Message}");
            }
        }

        RfCore.Data.DataSet ds;
        try
        {
            ds = SParameterEngine.Run(netlist, freqs, settings, control);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ProbeResult.Refuse($"The probe sweep could not be run: {ex.Message}");
        }

        var cube = ds["S"];
        int nf = cube.Axes[0].Length, np = cube.Axes[1].Length;
        if (probeIndex >= np)
            return ProbeResult.Refuse(
                $"The probe port did not appear in the S-parameter result ({np} ports found).");

        var raw = cube.ComplexValues;
        var z = new Complex[nf];
        for (int i = 0; i < nf; i++)
        {
            Complex gamma = raw[(i * np * np) + (probeIndex * np) + probeIndex];
            Complex denom = Complex.One - gamma;
            if (denom.Magnitude < 1e-14)
                return ProbeResult.Refuse(
                    $"The network beyond pin {pinIndex + 1} looks like an open circuit at " +
                    $"{freqs[i] / 1e9:G6} GHz (Γ = 1), so it has no finite impedance to fit.");
            z[i] = ProbeZ0 * (Complex.One + gamma) / denom;
        }

        return Fit(cube.Axes[0].Values, z, conjugate, residualWarning);
    }

    private static string DcRefusal(int iterations, double residual) =>
        $"The DC operating point did not converge (‖F‖ = {residual:G4} after {iterations} Newton " +
        "iterations), so there is no bias to linearize the circuit at. The probe refuses rather than " +
        "reporting an impedance measured at zero bias, which would be a plausible number and a wrong " +
        "one. Fix the bias network, or enable source-stepping continuation, and probe again.";

    // ── The fit (§2) and the ranking (§3) ─────────────────────────────────────

    /// <summary>
    /// match.md §10.2, on an already-measured impedance: the four closed-form least squares, ranked by
    /// mean |ΔΓ|. Public separately so the ranking can be pinned without an engine run.
    /// </summary>
    /// <param name="freqsHz">The band grid.</param>
    /// <param name="measured">The measured impedance at each frequency, ohms.</param>
    /// <param name="conjugate">Return the conjugate target rather than the measured termination.</param>
    /// <param name="residualWarning">The §14.5 warning threshold on the best residual.</param>
    public static ProbeResult Fit(
        double[] freqsHz, Complex[] measured, bool conjugate,
        double residualWarning = DefaultResidualWarning)
    {
        ArgumentNullException.ThrowIfNull(freqsHz);
        ArgumentNullException.ThrowIfNull(measured);
        if (freqsHz.Length == 0 || freqsHz.Length != measured.Length)
            return ProbeResult.Refuse("The probe produced no usable frequency points.");

        var z = (Complex[])measured.Clone();
        if (z.Any(v => !double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary)))
            return ProbeResult.Refuse("The measured impedance is not finite across the band.");

        // Geometric band centre, the same one MatchDesign.Omega0 uses — and the frequency the
        // conjugate target is exact at.
        double omega0 = 2.0 * Math.PI * Math.Sqrt(freqsHz[0] * freqsHz[^1]);

        int n = freqsHz.Length;
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 2.0 * Math.PI * freqsHz[i];

        // Each candidate is linear in its two unknowns in its own domain, and the real part carries
        // the resistive one while the imaginary part carries the reactive one — so both are ordinary
        // one-dimensional normal equations and neither needs a starting guess or a solver.
        var y = new Complex[n];
        for (int i = 0; i < n; i++) y[i] = Complex.One / z[i];

        double rSeries = Mean(z.Select(v => v.Real));
        double gParallel = Mean(y.Select(v => v.Real));

        // Z = R + (1/C)(1/jw)  =>  Im(Z) = -(1/C)/w
        double invCSeries = -SolveInverseOmega(w, [.. z.Select(v => v.Imaginary)]);
        // Z = R + jwL         =>  Im(Z) = wL
        double lSeries = SolveOmega(w, [.. z.Select(v => v.Imaginary)]);
        // Y = G + jwC         =>  Im(Y) = wC
        double cParallel = SolveOmega(w, [.. y.Select(v => v.Imaginary)]);
        // Y = G + (1/L)(1/jw) =>  Im(Y) = -(1/L)/w
        double invLParallel = -SolveInverseOmega(w, [.. y.Select(v => v.Imaginary)]);

        var candidates = new List<ProbeFit>
        {
            Score(TerminationTopology.Series,   ReactanceKind.C, rSeries,           Reciprocal(invCSeries)),
            Score(TerminationTopology.Series,   ReactanceKind.L, rSeries,           lSeries),
            Score(TerminationTopology.Parallel, ReactanceKind.C, Reciprocal(gParallel), cParallel),
            Score(TerminationTopology.Parallel, ReactanceKind.L, Reciprocal(gParallel), Reciprocal(invLParallel)),
        };

        var ranked = candidates.OrderBy(c => double.IsFinite(c.Residual) ? c.Residual : double.MaxValue)
                               .ThenBy(c => c.Name, StringComparer.Ordinal)
                               .ToList();
        var best = ranked.FirstOrDefault(c => c.Physical);

        if (best is null)
            return new ProbeResult(
                false,
                "None of the four two-element models fits this network with a positive resistance and a "
                + "positive reactance. All four are listed with their residuals; nothing has been "
                + "applied, because a negative element is not a termination anyone can build.",
                ranked, null, null, null, conjugate, omega0, "", freqsHz, z);

        string flag = best.Residual > residualWarning
            ? $"The external network is not well described by a two-element model over this band: the "
              + $"best fit ({best.Name}) leaves a mean |ΔΓ| of {best.Residual:G3}, above the "
              + $"{residualWarning:G3} threshold. It has been applied anyway. A resonance in band is the "
              + "usual cause — narrowing the band is the usual answer."
            : "";

        var target = conjugate ? best.ConjugateAt(omega0) : best;
        return new ProbeResult(
            true, null, ranked, best, target, target.ToTermination(DateTime.UtcNow),
            conjugate, omega0, flag, freqsHz, z);

        ProbeFit Score(TerminationTopology topology, ReactanceKind kind, double r, double value)
        {
            var fit = new ProbeFit(topology, kind, r, value, double.PositiveInfinity);
            double sum = 0.0;
            for (int i = 0; i < n; i++)
                sum += (GammaOf(fit.ImpedanceAt(freqsHz[i])) - GammaOf(z[i])).Magnitude;
            return fit with { Residual = sum / n };
        }
    }

    /// <summary>Γ referenced to <see cref="ProbeZ0"/> — the one domain every residual here is in.</summary>
    private static Complex GammaOf(Complex z)
    {
        Complex denom = z + ProbeZ0;
        if (denom.Magnitude < 1e-30) return new Complex(-1, 0);
        var g = (z - ProbeZ0) / denom;
        // A non-physical candidate can produce a non-finite model impedance; its Γ is then meaningless
        // rather than merely large, and scoring it as the worst possible reflection is what keeps it
        // in the ranking (visible, per §3) without letting a NaN sort to the front.
        return double.IsFinite(g.Real) && double.IsFinite(g.Imaginary) ? g : new Complex(-1, 0);
    }

    private static double Mean(IEnumerable<double> xs)
    {
        double s = 0; int k = 0;
        foreach (double x in xs) { s += x; k++; }
        return k == 0 ? 0 : s / k;
    }

    /// <summary>Least squares of <c>im ≈ a·w</c> for <c>a</c>.</summary>
    private static double SolveOmega(double[] w, double[] im)
    {
        double num = 0, den = 0;
        for (int i = 0; i < w.Length; i++) { num += im[i] * w[i]; den += w[i] * w[i]; }
        return den == 0 ? 0 : num / den;
    }

    /// <summary>Least squares of <c>im ≈ a/w</c> for <c>a</c>.</summary>
    private static double SolveInverseOmega(double[] w, double[] im)
    {
        double num = 0, den = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (w[i] == 0) continue;
            num += im[i] / w[i];
            den += 1.0 / (w[i] * w[i]);
        }
        return den == 0 ? 0 : num / den;
    }

    private static double Reciprocal(double v) => v == 0 ? double.PositiveInfinity : 1.0 / v;

    // ── Bench surgery ─────────────────────────────────────────────────────────

    /// <summary>
    /// The bench without <paramref name="drop"/>, sharing its (immutable) instances. Measurements are
    /// deliberately not carried: they reference circuit quantities by absolute path and several of
    /// those paths lead into the instance just deleted.
    /// </summary>
    private static TestBench CopyWithout(TestBench bench, Instance drop)
    {
        var copy = new TestBench(bench.Name);
        foreach (var i in bench.Instances)
            if (!ReferenceEquals(i, drop))
                copy.Instances.Add(i);
        copy.GlobalVariables.AddRange(bench.GlobalVariables);
        copy.Functions.AddRange(bench.Functions);
        foreach (string labeled in bench.LabeledNets) copy.LabeledNets.Add(labeled);
        return copy;
    }

    /// <summary>Port numbers of the TOP-LEVEL Port/Term/P1Tone components, the set the engine reads.</summary>
    private static List<int> TopLevelPortNums(ElaboratedNetlist netlist)
    {
        var nums = new List<int>();
        foreach (var ec in netlist.Components)
        {
            if (ec.InstancePath.Contains('.')) continue;
            if (ec.Model is not (PortModel or TermModel or P1ToneModel)) continue;
            if (ec.Parameters.TryGetValue("Num", out var v)) nums.Add((int)v.AsReal());
        }
        return nums;
    }

    private static string UniqueName(TestBench bench, string wanted)
    {
        var taken = bench.Instances.Select(i => i.InstanceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;
        for (int k = 2; ; k++)
        {
            string candidate = $"{wanted}_{k}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
