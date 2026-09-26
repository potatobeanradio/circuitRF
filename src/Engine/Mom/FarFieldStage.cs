// brief-em3d-31 R-em3d31-1 — the far-field METRICS STAGE, factored away from the transform.
//
// PlanarFarField.Compute used to be the only road into the "farfield" group: a MoM-specific transform
// (rooftop currents over a layered medium, R-ant-1) followed by everything after it — intensity,
// directivity, both gains, efficiency, the power budget, per-plane beamwidth, polarization and the cube
// layout. Everything after is solver-agnostic once three things kernel B derives from its own currents
// and medium are supplied instead (FarFieldExternalTerms), so a 3D solver's pattern drives the SAME
// cubes and the Data Display, the metrics and the CLI need nothing new.
//
// WHAT A SOLVER HANDS IN: a (θ, φ) grid of complex E_θ, E_φ in the r-normalisation PlanarFarFieldPattern
// states (F = lim r·e^{+jk₀r}·E, volts — not "E at 1 m"), the frequency, the driven port, the ACCEPTED
// power under that same excitation, and the driven port's published S_jj. Available power is then
// accepted / (1 − |S_jj|²), R-ant-6's one mismatch factor — the planar sweep's rule, not a re-derivation.
//
// THE PLANAR RESULT IS BYTE-IDENTICAL BEFORE AND AFTER (FarFieldStageTests, against a golden dumped from
// the pre-split code before a line of this file existed). The three publishers below moved here out of
// PlanarKernel unchanged.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// What a solver other than kernel B supplies beside its pattern — see
/// <see cref="PlanarMetricContext.External"/>.
/// </summary>
/// <param name="Solver">The solver, as a run names it ("openEMS (FDTD)").</param>
/// <param name="AcceptedW">The power accepted at the driven port under the excitation the pattern is
/// normalised to, watts. R-ant-5's denominator.</param>
/// <param name="HemisphereFrontToBackRefusal">The sentence refusing front-to-back when the pattern's θ axis
/// stops at 90° — the solver's own reason there is no lower hemisphere.</param>
/// <param name="SurfaceWaveVerdict">The surface-wave term's verdict. A 3D solver has no laterally infinite
/// substrate to launch one into, so it refuses by name (and the dielectric residual goes with it).</param>
/// <param name="ConductorVerdict">The conductor term's verdict — a 3D solver does not itemise its loss.</param>
/// <param name="EfficiencyTolerance">How far past 1 the radiation efficiency may read before it is refused
/// as an error rather than published — the solver's own measured power-balance residual, which for a
/// surface transform against a port DFT is looser than kernel B's quadrature
/// (<see cref="PlanarMetricSettings.EfficiencyTolerance"/>).</param>
public sealed record FarFieldExternalTerms(
    string        Solver,
    double        AcceptedW,
    string        HemisphereFrontToBackRefusal,
    EmSuitability SurfaceWaveVerdict,
    EmSuitability ConductorVerdict,
    double        EfficiencyTolerance = PlanarMetricSettings.EfficiencyTolerance);

/// <summary>One frequency's patterns, metric reports and polarization, one of each per driven port.</summary>
public sealed record FarFieldSlice(
    double                                   FrequencyHz,
    IReadOnlyList<PlanarFarFieldPattern>     Patterns,
    IReadOnlyList<PlanarMetricReport>        Metrics,
    IReadOnlyList<PlanarPolarizationPattern> Polarization);

/// <summary>The three sets a run publishes, assembled from its slices.</summary>
public sealed record FarFieldStageResult(
    PlanarFarFieldSet     FarField,
    PlanarMetricSet       Metrics,
    PlanarPolarizationSet Polarization);

public static class FarFieldStage
{
    /// <summary>η₀, the one constant a pattern's intensity is formed with.</summary>
    public const double Eta0 = FarFieldElementFactors.Eta0;

    /// <summary>
    /// The full sphere at a stated step: θ 0…180° inclusive, φ over [0, 360). What a structure with an
    /// absorber on every face radiates into — <see cref="PlanarFarFieldGrid.Hemisphere"/> stops at 90°,
    /// which is the planar kernel's medium and a 3D box with a conducting floor.
    /// </summary>
    public static PlanarFarFieldGrid Sphere(double thetaStepDeg = 1.0, double phiStepDeg = 1.0)
    {
        int nt = (int)Math.Round(180.0 / thetaStepDeg);
        var theta = new double[nt + 1];
        for (int i = 0; i <= nt; i++) theta[i] = i == nt ? 180.0 : i * thetaStepDeg;
        int np = Math.Max(1, (int)Math.Round(360.0 / phiStepDeg));
        var phi = new double[np];
        for (int i = 0; i < np; i++) phi[i] = i * (360.0 / np);
        return new PlanarFarFieldGrid(theta, phi);
    }

    /// <summary>A pattern from its two components, with U = (|F_θ|² + |F_φ|²)/2η₀ formed as kernel B forms it.</summary>
    public static PlanarFarFieldPattern Pattern(PlanarFarFieldGrid grid, IReadOnlyList<Complex> eTheta,
                                                IReadOnlyList<Complex> ePhi, int drivenPort, double fHz)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (eTheta.Count != grid.DirectionCount || ePhi.Count != grid.DirectionCount)
            throw new ArgumentException($"A pattern on this grid has {grid.DirectionCount} directions.");
        var u = new double[grid.DirectionCount];
        for (int k = 0; k < u.Length; k++)
            u[k] = (eTheta[k].Magnitude * eTheta[k].Magnitude + ePhi[k].Magnitude * ePhi[k].Magnitude) / (2.0 * Eta0);
        return new PlanarFarFieldPattern(grid, eTheta, ePhi, u, drivenPort, fHz);
    }

    /// <summary>
    /// One (frequency, port)'s metric report and polarization, from a context either road built — the
    /// planar sweep's (currents, mesh, raw admittance) or another solver's
    /// (<see cref="PlanarMetricContext(PlanarFarFieldPattern, FarFieldExternalTerms, Complex?, PlanarMetricSettings?)"/>).
    /// ANT-6 reads the SAME context ANT-5's metrics do, which is what makes the derived Ludwig-3
    /// reference angle and the derived beamwidth cut one number rather than two.
    /// </summary>
    public static (PlanarMetricReport Metrics, PlanarPolarizationPattern Polarization) Evaluate(PlanarMetricContext context)
        => (PlanarMetrics.Evaluate(context), PlanarPolarization.For(context));

    /// <summary>
    /// The three sets, from slices ALREADY in ascending frequency order — row-major <c>[freq, port]</c>,
    /// with the cut-axis and reference-angle agreement rules each set applies.
    /// </summary>
    public static FarFieldStageResult Assemble(PlanarFarFieldGrid grid, IReadOnlyList<int> portNumbers,
                                               IReadOnlyList<FarFieldSlice> slices)
    {
        var freqs = slices.Select(x => x.FrequencyHz).ToArray();
        var flat = new List<PlanarFarFieldPattern>(slices.Count * portNumbers.Count);
        foreach (var x in slices) flat.AddRange(x.Patterns);
        var far = new PlanarFarFieldSet(grid, freqs, portNumbers, flat);

        var flatMetrics = new List<PlanarMetricReport>(slices.Count * portNumbers.Count);
        foreach (var x in slices) flatMetrics.AddRange(x.Metrics);
        var metrics = PlanarMetricSet.From(freqs, portNumbers, flatMetrics);

        var flatPol = new List<PlanarPolarizationPattern>(slices.Count * portNumbers.Count);
        foreach (var x in slices) flatPol.AddRange(x.Polarization);
        var pol = PlanarPolarizationSet.From(freqs, portNumbers, flatPol);
        return new FarFieldStageResult(far, metrics, pol);
    }

    /// <summary>The whole <c>"farfield"</c> group: the pattern, the metrics, the polarization — in that order.</summary>
    public static void Publish(DataSet ds, PlanarFarFieldSet? far, PlanarMetricSet? metrics, PlanarPolarizationSet? pol)
    {
        AddPattern(ds, far);
        AddMetrics(ds, metrics);
        AddPolarization(ds, far, pol);
    }

    /// <summary>
    /// <b>ANT-4 — the far field is ONE MORE GROUP OF CUBES, not a new result type</b> (R-res-6, for
    /// the sixth phase running). <c>DataCube</c> is already N-rank with named, unit-bearing axes and
    /// slicing, so <c>[freq, theta, phi, port]</c> needs nothing new to carry it.
    ///
    /// <para><b>The port axis is here from the first commit</b>, even though every antenna fixture in
    /// this series is a one-port. <c>PlanarPortSolution.Currents</c> is already one vector per driven
    /// port, so it is free at construction — and it is what makes array pattern synthesis possible
    /// later. Retro-fitting an axis onto a shipped cube is not free.</para>
    ///
    /// <para><b>θ spans 0…90° and the AXIS is where that is said.</b> See
    /// <see cref="PlanarFarFieldPattern.ThetaRangeNote"/>, which the run also prints: with a
    /// laterally infinite ground plane there is no field below, so a 0…180° axis would be half full
    /// of structural zeros that read like a measured front-to-back ratio.</para>
    /// </summary>
    public static void AddPattern(DataSet ds, PlanarFarFieldSet? far)
    {
        if (far is null || far.Patterns.Count == 0) return;

        int nf = far.FrequenciesHz.Count, nt = far.Grid.ThetaDeg.Count;
        int np = far.Grid.PhiDeg.Count,   nq = far.PortNumbers.Count;

        Axis[] Ax() =>
        [
            new Axis("freq",  far.FrequenciesHz.ToArray(), "Hz"),
            new Axis("theta", far.Grid.ThetaDeg.ToArray(), "deg"),
            new Axis("phi",   far.Grid.PhiDeg.ToArray(),   "deg"),
            new Axis("port",  far.PortNumbers.Select(n => (double)n).ToArray(), ""),
        ];

        var eth = new Complex[nf * nt * np * nq];
        var eph = new Complex[nf * nt * np * nq];
        var u   = new double [nf * nt * np * nq];

        for (int i = 0; i < nf; i++)
            for (int q = 0; q < nq; q++)
            {
                var pat = far.At(i, q);
                for (int t = 0; t < nt; t++)
                    for (int f = 0; f < np; f++)
                    {
                        int src = far.Grid.IndexOf(t, f);
                        int dst = ((i * nt + t) * np + f) * nq + q;
                        eth[dst] = pat.ETheta[src];
                        eph[dst] = pat.EPhi[src];
                        u[dst]   = pat.U[src];
                    }
            }

        // Units, for the reason AddMetrics gives: a pattern plot's radial numbers are in SOMETHING,
        // and "dB" alone does not say what of. E is r-normalised (r·E with e^{-jk₀r} removed), so it
        // is volts; U is a radiation intensity.
        ds.AddToGroup(PlanarFarField.Group, "Etheta", new DataCube(Ax(), eth) { Unit = "V" });
        ds.AddToGroup(PlanarFarField.Group, "Ephi",   new DataCube(Ax(), eph) { Unit = "V" });
        ds.AddToGroup(PlanarFarField.Group, "U",      new DataCube(Ax(), u)   { Unit = "W/sr" });
    }

    /// <summary>
    /// <b>ANT-5 — the metrics, in ANT-4's own <c>"farfield"</c> group and still with no new result
    /// type</b> (R-res-6 for the seventh phase running). Each is a real cube over
    /// <c>[freq, port]</c>, except the beamwidth, which carries a <c>cut</c> axis between them holding
    /// the φ of each plane in degrees — which is how §4's "report the cut alongside the number" is
    /// satisfied without a second cube to go and read.
    ///
    /// <para><b>A metric is published as one cube or not at all</b>, which
    /// <see cref="PlanarMetricSet.Publishable"/> decides; a refused one is carried as a NOTE in the
    /// registry's own wording, and is PRESENT in <see cref="PlanarMetrics.Registry"/> either way. That
    /// is what makes front-to-back visible to a picker, a listing and an exporter on day one while
    /// still refusing to print a number for it.</para>
    /// </summary>
    public static void AddMetrics(DataSet ds, PlanarMetricSet? metrics)
    {
        if (metrics is null || metrics.Reports.Count == 0) return;

        int nf = metrics.FrequenciesHz.Count, nq = metrics.PortNumbers.Count;
        int nc = metrics.CutsPhiDeg.Count;

        Axis Freq() => new("freq", metrics.FrequenciesHz.ToArray(), "Hz");
        Axis Port() => new("port", metrics.PortNumbers.Select(n => (double)n).ToArray(), "");
        Axis Cut()  => new("cut",  metrics.CutsPhiDeg.ToArray(), "deg");

        foreach (var def in PlanarMetrics.Registry)
        {
            if (!metrics.Publishable(def.Metric).Ok) continue;

            bool perCut = def.Axis == PlanarMetricAxis.PerCut;
            int  stride = perCut ? nc : 1;
            if (perCut && nc == 0) continue;

            var values = new double[nf * stride * nq];
            for (int i = 0; i < nf; i++)
                for (int q = 0; q < nq; q++)
                {
                    var outcome = metrics.At(i, q)[def.Metric];
                    for (int k = 0; k < stride; k++)
                        values[(i * stride + k) * nq + q] = outcome.Values[k];
                }

            // THE UNIT TRAVELS WITH THE CUBE. The registry has carried one per metric since ANT-5
            // and this publish threw it away, which ANT-7 §8 recorded as the reason a pattern plot
            // could not say what its radial numbers were in. It matters more than presentation now:
            // a dBm LEVEL is only meaningful against a reference, and the unit is what tells a
            // reader — and `LevelReference` — that this cube has one (`ReferenceInputPowerDbm`,
            // published beside it) rather than being an absolute nobody can reproduce.
            Axis[] axes = perCut ? [Freq(), Cut(), Port()] : [Freq(), Port()];
            ds.AddToGroup(PlanarFarField.Group, def.CubeName,
                          new DataCube(axes, values) { Unit = def.Unit });
        }
    }

    /// <summary>
    /// <b>ANT-6 — polarization, in ANT-4's own <c>"farfield"</c> group and still with no new result
    /// type</b> (R-res-6 for the eighth phase running). Four real cubes on ANT-4's own
    /// <c>[freq, theta, phi, port]</c> axes, because each is a per-DIRECTION property of a pattern
    /// rather than a per-point metric.
    ///
    /// <para><b><c>AxialRatioDb</c> and <c>PolarizationSense</c> are always published; the Ludwig-3
    /// pair needs a reference angle and is published only when one applies to the whole set</b>
    /// (<see cref="PlanarPolarizationSet.CoCrossVerdict"/>) — a cube whose φ₀ changed halfway along its
    /// own frequency axis would be two quantities under one name. The refusal arrives as a note in
    /// <see cref="PlanarPolarization"/>'s own wording, exactly as a refused metric does.</para>
    ///
    /// <para><b>The DEFINITION travels with the cube</b> (R-ant-8): it is in the name —
    /// <c>CoPolLudwig3Db</c>, not <c>CoPolDb</c> — and in <see cref="PlanarPolarization.Cubes"/>'
    /// note, which a picker, a listing and an exporter all read rather than restating.</para>
    /// </summary>
    public static void AddPolarization(DataSet ds, PlanarFarFieldSet? far, PlanarPolarizationSet? pol)
    {
        if (far is null || pol is null || pol.Patterns.Count == 0) return;

        int nf = pol.FrequenciesHz.Count, nt = far.Grid.ThetaDeg.Count;
        int np = far.Grid.PhiDeg.Count,   nq = pol.PortNumbers.Count;

        Axis[] Ax() =>
        [
            new Axis("freq",  pol.FrequenciesHz.ToArray(),  "Hz"),
            new Axis("theta", far.Grid.ThetaDeg.ToArray(),  "deg"),
            new Axis("phi",   far.Grid.PhiDeg.ToArray(),    "deg"),
            new Axis("port",  pol.PortNumbers.Select(n => (double)n).ToArray(), ""),
        ];

        bool coCross = pol.CoCrossVerdict.Ok;

        foreach (var (cubeName, unit, _) in PlanarPolarization.Cubes)
        {
            bool isPair = cubeName is "CoPolLudwig3Db" or "CrossPolLudwig3Db";
            if (isPair && !coCross) continue;

            var values = new double[nf * nt * np * nq];
            for (int i = 0; i < nf; i++)
                for (int q = 0; q < nq; q++)
                {
                    var pat = pol.At(i, q);
                    var src = cubeName switch
                    {
                        "AxialRatioDb"      => pat.AxialRatioDb,
                        "PolarizationSense" => pat.Sense,
                        "CoPolLudwig3Db"    => pat.CoPolDb,
                        _                   => pat.CrossPolDb,
                    };
                    for (int t = 0; t < nt; t++)
                        for (int f = 0; f < np; f++)
                            values[((i * nt + t) * np + f) * nq + q] = src[far.Grid.IndexOf(t, f)];
                }

            ds.AddToGroup(PlanarPolarization.Group, cubeName,
                          new DataCube(Ax(), values) { Unit = unit });
        }
    }}
