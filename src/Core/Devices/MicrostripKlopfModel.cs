using System.Collections.Concurrent;
using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MKLOPF — a Klopfenstein-taper microstrip line, 2-port, with an optional off-axis
/// <c>Offset</c> (brief-mtaper-mklopf.md §2-3).
///
/// <b>Electrical model: a cascade of N short uniform MLIN sections</b>, exactly like
/// <see cref="MicrostripTaperModel"/> (R7/R-pc-12's "one implementation," reused verbatim via
/// <see cref="MicrostripAbcd"/>/<see cref="MicrostripCascadeSectioning"/>) — the only difference is
/// how each section's LOCAL WIDTH is chosen: here, from <see cref="KlopfensteinTaper.ImpedanceAt"/>
/// evaluated at the section's own ARC-LENGTH fraction (R-klp-6), inverse-synthesised to a width via
/// <see cref="HammerstadJensen.SynthesizeWidth"/> (R-klp-5 — the SAME Hammerstad-Jensen model family
/// MLIN's own forward direction uses, never a second synthesis formula).
///
/// <b>R-klp-4: the model ALWAYS uses the stepped Klopfenstein profile</b> — the ±ρ₀ endpoint
/// discontinuities baked into <see cref="KlopfensteinTaper.ImpedanceAt"/> are never smoothed here,
/// regardless of the artwork's own <c>SmoothSteps</c> flag (which affects ONLY the PCell's drawn
/// outline, in <c>src/Ui/Layout/PCells/</c> — this class has no smoothing concept at all).
///
/// <b>R-klp-9: the Offset does not change the network topology</b> — a curved centerline changes
/// each section's ARC length (via <see cref="MicrostripOffsetCenterline.TotalArcLength"/>) and,
/// through the arc-fraction-to-Klopfenstein-position mapping, which impedance each section gets;
/// it never adds a second element or a coupling term. Differential phase across the trace width and
/// mode conversion from the curvature itself are NOT modelled (R-klp-9's own stated limit) —
/// <see cref="LastMinRadiusOfCurvatureMeters"/>/R-klp-10's report is how a user is told when that
/// omission has stopped being negligible.
///
/// <b>Performance and messages (brief-mklopf-performance-and-messages.md).</b> Everything above
/// this line depends only on geometry and substrate — never on frequency — and used to be
/// recomputed, root-finds included, on EVERY <see cref="Stamp"/> call (once per frequency point).
/// The fix has three parts, all cached process-wide (keyed on the resolved parameter tuple, mirroring
/// the PCell contract's "evaluate once per unique parameter set" and <c>TechnologyCache</c>'s own
/// per-key sharing — see <see cref="_geometryCache"/>/<see cref="_sectionTableCache"/>):
/// <list type="number">
/// <item><b>R-mk-1/R-mk-2 — hoist the frequency-independent setup.</b> Total arc length, minimum
/// radius of curvature (and the one-time 200-sample scan that finds it — R-mk-2's own fix: the OLD
/// guard flag was only ever set when the warning FIRED, so the scan reran on every frequency point
/// in the common, non-warning case), the two endpoint widths/eeff, and eeffMax are all computed
/// exactly ONCE per distinct geometry+substrate (<see cref="MklopfGeometryData"/>), never per
/// frequency. <see cref="Stamp"/> itself is left doing only what genuinely varies with frequency:
/// Kirschning-Jansen dispersion, the two loss terms, and the ABCD cascade.</item>
/// <item><b>R-mk-4/R-mk-5/R-mk-6 — non-uniform section count and placement.</b> The OLD profile-
/// resolution criterion sampled ΔW uniformly in arc length, which forces the WHOLE taper to the
/// section density its steepest point alone needs (R-mk-4). Sections are instead placed at equal
/// Δ(ln Z) via <see cref="MicrostripCascadeSectioning.NonUniformBoundaries"/> (R-mk-5: a small
/// reflection scales with Δ(ln Z), not ΔW), and the section COUNT is resolved by actually converging
/// the cascade's own Z-parameters (doubling N until doubling again changes nothing beyond tolerance
/// — R-mk-6, "converge on the answer rather than a geometric proxy") rather than by a fixed-tolerance
/// geometric proxy. This runs ONCE per parameter set, at a fixed internal reference frequency (see
/// <see cref="ConvergenceReferenceFreqHz"/>'s own doc comment for why a fixed frequency, not
/// "whichever frequency happens to trigger the cache first," is the correct choice), combined per
/// <see cref="Stamp"/> call with the ordinary frequency-DEPENDENT electrical criterion
/// (<see cref="MicrostripCascadeSectioning.ElectricalSectionCount"/>) evaluated at the REAL stamping
/// frequency.</item>
/// <item><b>R-mk-7/R-mk-8/R-mk-9/R-mk-10 — warnings reach Messages, not the terminal.</b> Both the
/// curvature warning and the section-count report route through <see cref="_reporter"/>
/// (<see cref="IReportsWarnings.DrainWarnings"/> exposes them to the engine's post-Stamp drain —
/// see that interface's own doc comment for the full path into the Messages UI, and R-mk-8's own
/// finding that <see cref="MicrostripValidityReporter"/> itself, not just these two call sites, used
/// to write directly to <c>Console.Error</c>). Neither message hand-types an extra "MKLOPF:" prefix
/// (R-mk-9 — the reporter's own instance path already identifies the component). The section-count
/// line is informational, not a warning, and is only queued when N exceeds
/// <see cref="SectionCountReportThreshold"/> (R-mk-10 — a small, unremarkable N is noise).</item>
/// </list>
/// <c>_sectionCountOverride</c> (the pre-existing manual escape hatch, kept per the brief's own
/// explicit "do not remove" guardrail) deliberately bypasses BOTH the electrical/geometric criteria
/// AND the non-uniform placement above — an overridden N uses the ORIGINAL uniform-arc-fraction
/// placement, so a user who forces an exact section count gets the simple, predictable behavior the
/// override always had, and so R-mk-1's own "hoisting changes nothing" claim is directly testable
/// by comparing an overridden-N stamp before and after this change.
/// </summary>
public sealed class MicrostripKlopfModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    private readonly double _z1, _z2, _length, _gammaMax, _offset, _h, _t, _epsR, _sigma, _tanD;
    private readonly int _sectionCountOverride;
    private readonly MicrostripValidityReporter _reporter;

    /// <summary>The number of cascade sections used on the most recent <see cref="Stamp"/> call.</summary>
    public int LastSectionCount { get; private set; }

    /// <summary>The centerline's total arc length on the most recent <see cref="Stamp"/> call
    /// (R-klp-6 — reported alongside the axial length <c>L</c> since Offset makes them differ).</summary>
    public double LastTotalArcLengthMeters { get; private set; }

    /// <summary>The centerline's minimum radius of curvature (R-klp-10) — <see cref="double.PositiveInfinity"/>
    /// when <c>Offset=0</c>.</summary>
    public double LastMinRadiusOfCurvatureMeters { get; private set; }

    /// <summary>R-mk-7/8: routes this instance's curvature/section-count/validity-range warnings
    /// into ElaboratedNetlist.Warnings via the engine's post-Stamp drain — see IReportsWarnings'
    /// own doc comment.</summary>
    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    /// <summary>R-mk-10: the section-count line is informational — only worth reporting once N is
    /// clearly larger than the routine case (a low tens-of-sections N is unremarkable and would be
    /// noise alongside genuine problems).</summary>
    public const int SectionCountReportThreshold = 200;

    private const int MaxSections = 4096;

    /// <summary>
    /// R-mk-6's own convergence-doubling test needs SOME frequency to evaluate the cascade at — but
    /// the profile-resolution question it answers ("is the DISCRETIZED Z(x) profile fine enough")
    /// is fundamentally about the STATIC taper shape, not about which specific frequency happens to
    /// be in play: dispersion/loss shift the absolute Z(f)/εeff(f) a little but do not move WHERE the
    /// Klopfenstein profile is steepest. Using a FIXED internal reference frequency, rather than
    /// "whichever Stamp call happens to trigger the cache first," makes the resolved section count
    /// deterministic and reproducible regardless of call order — notably including
    /// <c>SParameterEngine.CollectPortsAndBranchLabels</c>'s own preliminary branch-labeling pass,
    /// which stamps every component once at ω=1 rad/s (≈0.16 Hz) purely to capture branch indices;
    /// letting THAT bogus near-DC frequency seed the one-time convergence test would silently under-
    /// resolve N for a design actually swept at, say, 1–10 GHz. The real, frequency-DEPENDENT
    /// electrical criterion is still evaluated fresh at the ACTUAL stamping frequency on every call
    /// (see <see cref="Stamp"/>) and is combined with this reference-frequency result via
    /// <c>Math.Max</c> — so a genuinely high sweep frequency still raises N correctly; it just does
    /// so through the analytic λ/20 rule rather than through this one-time empirical check.
    /// </summary>
    private const double ConvergenceReferenceFreqHz = 1e9;

    private const double ConvergenceTolerance = 1e-4;

    public MicrostripKlopfModel(double z1Ohms, double z2Ohms, double lengthMeters, double gammaMax,
        double offsetMeters, double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD,
        string instancePath, int sectionCountOverride = 0)
    {
        KlopfensteinTaper.ValidateGammaMax(z1Ohms, z2Ohms, gammaMax); // R-klp-2, throws if degenerate

        _z1 = z1Ohms;
        _z2 = z2Ohms;
        _length = lengthMeters;
        _gammaMax = gammaMax;
        _offset = offsetMeters;
        _h = hMeters;
        _t = tMeters;
        _epsR = epsR;
        _sigma = sigmaSPerM;
        _tanD = tanD;
        _sectionCountOverride = sectionCountOverride;
        _reporter = new MicrostripValidityReporter(instancePath);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1];
        double freqHz = omega / (2.0 * Math.PI);

        var key = new MklopfGeometryKey(_z1, _z2, _gammaMax, _length, _offset, _h, _t, _epsR, _sigma, _tanD);
        var geo = GetOrBuildGeometry(key);

        LastTotalArcLengthMeters = geo.TotalArcMeters;
        LastMinRadiusOfCurvatureMeters = geo.MinRadiusMeters;

        if (geo.CurvatureExceedsThreshold)
        {
            _reporter.ReportOnce("curvature",
                $"Offset centerline's minimum radius of curvature ({geo.MinRadiusMeters:G4} m) is below " +
                $"3x the local width ({geo.CurvatureLocalWidthMeters:G4} m) at that point — R-klp-10: the " +
                "gentle-bend model (no differential phase/mode-conversion term) is no longer trustworthy " +
                "here; EM-simulate this geometry.");
        }

        if (omega <= 0.0)
        {
            mna.AddAdmittance(n1, n2, new Complex(1.0e9, 0.0));
            return;
        }

        int n;
        bool nonUniform;
        if (_sectionCountOverride > 0)
        {
            n = _sectionCountOverride;
            nonUniform = false;
        }
        else
        {
            int nElec = MicrostripCascadeSectioning.ElectricalSectionCount(geo.TotalArcMeters, freqHz, geo.EeffMax);
            n = Math.Max(geo.NGeoConverged, nElec);
            nonUniform = true;
        }
        LastSectionCount = n;

        if (n > SectionCountReportThreshold)
        {
            _reporter.ReportOnce("section-count",
                $"cascade uses N={n} sections; total arc length {geo.TotalArcMeters:G4} m (axial length " +
                $"{_length:G4} m{(_offset != 0.0 ? $", Offset={_offset:G4} m" : "")}).");
        }

        var table = GetOrBuildSectionTable(key, n, nonUniform);
        var (z11, z12, z21, z22) = CascadeAt(table, key, freqHz, _reporter);

        int b1 = mna.AddBranch();
        int b2 = mna.AddBranch();
        mna.AddBranchCurrent(b1, n1, 0);
        mna.AddBranchCurrent(b2, n2, 0);
        mna.AddConstraint(b1, n1, Complex.One);
        mna.AddConstraint(b2, n2, Complex.One);
        mna.AddBranchConstraint(b1, b1, -z11);
        mna.AddBranchConstraint(b1, b2, -z12);
        mna.AddBranchConstraint(b2, b1, -z21);
        mna.AddBranchConstraint(b2, b2, -z22);
    }

    // ── Process-wide caches (R-mk-3) ────────────────────────────────────────────────────────────

    private readonly record struct MklopfGeometryKey(
        double Z1, double Z2, double GammaMax, double LengthMeters, double OffsetMeters,
        double HMeters, double TMeters, double EpsR, double SigmaSPerM, double TanD);

    private readonly record struct MklopfSectionTableKey(MklopfGeometryKey Geometry, int N, bool NonUniform);

    private readonly struct MklopfSection(double widthMeters, double z0Static, double eeff0, double arcLengthMeters)
    {
        public readonly double WidthMeters = widthMeters;
        public readonly double Z0Static = z0Static;
        public readonly double Eeff0 = eeff0;
        public readonly double ArcLengthMeters = arcLengthMeters;
    }

    private sealed class MklopfGeometryData
    {
        public required double TotalArcMeters;
        public required double MinRadiusMeters;
        public required double EeffMax;
        public required bool CurvatureExceedsThreshold;
        public required double CurvatureLocalWidthMeters;
        public required int NGeoConverged;
    }

    private sealed class MklopfSectionTable
    {
        public required IReadOnlyList<MklopfSection> Sections;
    }

    private static readonly ConcurrentDictionary<MklopfGeometryKey, Lazy<MklopfGeometryData>> _geometryCache = new();
    private static readonly ConcurrentDictionary<MklopfSectionTableKey, Lazy<MklopfSectionTable>> _sectionTableCache = new();

    /// <summary>Test/diagnostic instrumentation only (brief-mklopf-performance-and-messages.md gate
    /// 3/5): counts actual <see cref="BuildGeometryData"/> executions (cache MISSES) process-wide —
    /// this is where the one-time curvature scan, W1/W2 synthesis, and the whole R-mk-6 convergence-
    /// doubling search live, so a small, bounded count across a many-point sweep is the direct proof
    /// that none of it re-runs per frequency point.</summary>
    public static int GeometryBuildCount { get; private set; }

    /// <summary>Test/diagnostic instrumentation only: counts actual section-table builds (cache
    /// MISSES) process-wide — each build performs O(N) <see cref="HammerstadJensen.SynthesizeWidth"/>
    /// calls, so a bounded count across a many-point sweep (rather than one per point) is the direct
    /// proof of gate 3's "O(N) for the whole sweep, not O(N × points)."</summary>
    public static int SectionTableBuildCount { get; private set; }

    /// <summary>Test-only: clears every process-wide cached geometry/section table and zeroes both
    /// counters above, so a test can measure cache behavior for a fresh parameter set without
    /// cross-test pollution from whichever other test ran first against the same cache.</summary>
    public static void ResetCachesForTesting()
    {
        _geometryCache.Clear();
        _sectionTableCache.Clear();
        GeometryBuildCount = 0;
        SectionTableBuildCount = 0;
    }

    private MklopfGeometryData GetOrBuildGeometry(MklopfGeometryKey key)
        => _geometryCache.GetOrAdd(key,
            k => new Lazy<MklopfGeometryData>(
                () => { GeometryBuildCount++; return BuildGeometryData(k, _reporter); },
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private MklopfSectionTable GetOrBuildSectionTable(MklopfGeometryKey key, int n, bool nonUniform)
    {
        var tkey = new MklopfSectionTableKey(key, n, nonUniform);
        return _sectionTableCache.GetOrAdd(tkey,
            tk => new Lazy<MklopfSectionTable>(
                () =>
                {
                    SectionTableBuildCount++;
                    return nonUniform
                        ? BuildNonUniformSectionTable(tk.Geometry, tk.N, _reporter)
                        : BuildUniformSectionTable(tk.Geometry, tk.N, _reporter);
                },
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>R-mk-1/R-mk-2: everything here is frequency-independent (geometry + substrate
    /// only) and, per <see cref="_geometryCache"/>, computed exactly once for a distinct parameter
    /// set — the width syntheses/H-J compute calls below use whichever instance's real
    /// <paramref name="reporter"/> happens to trigger the build (a deliberate, documented
    /// simplification for the rare case of two component instances sharing byte-identical
    /// parameters: the ONE resulting validity warning, if any, is attributed to the first such
    /// instance rather than to every one of them — the underlying physical fact is identical
    /// either way).</summary>
    private static MklopfGeometryData BuildGeometryData(MklopfGeometryKey k, MicrostripValidityReporter reporter)
    {
        double totalArc = MicrostripOffsetCenterline.TotalArcLength(k.LengthMeters, k.OffsetMeters);
        double minRadius = MicrostripOffsetCenterline.MinRadiusOfCurvature(k.LengthMeters, k.OffsetMeters);

        double curvatureLocalWidth = 0.0;
        bool curvatureExceeds = false;
        if (k.OffsetMeters != 0.0)
        {
            // R-klp-10: find the arc position of maximum curvature and compare against 3x ITS OWN
            // local width (narrower at one end than the other, so the check must use the LOCAL
            // width, not W1 or W2 arbitrarily). Runs exactly once per geometry (R-mk-2's fix — the
            // OLD per-instance guard only latched once the warning had already fired, so in the
            // healthy, non-warning case this 200-sample scan reran on every single frequency point).
            double worstKappa = 0.0, worstX = 0.0;
            const int samples = 200;
            for (int i = 0; i <= samples; i++)
            {
                double x = k.LengthMeters * i / samples;
                double kappa = MicrostripOffsetCenterline.Curvature(x, k.LengthMeters, k.OffsetMeters);
                if (kappa > worstKappa) { worstKappa = kappa; worstX = x; }
            }
            if (worstKappa > 0.0)
            {
                double arcAtWorst = MicrostripOffsetCenterline.ArcLength(0.0, worstX, k.LengthMeters, k.OffsetMeters);
                double sFractionAtWorst = totalArc > 0 ? arcAtWorst / totalArc : 0.0;
                double z = KlopfensteinTaper.ImpedanceAt(sFractionAtWorst, k.Z1, k.Z2, k.GammaMax);
                curvatureLocalWidth = HammerstadJensen.SynthesizeWidth(z, k.HMeters, k.TMeters, k.EpsR, reporter);
                double rMin = 1.0 / worstKappa;
                curvatureExceeds = rMin < 3.0 * curvatureLocalWidth;
            }
        }

        double w1 = HammerstadJensen.SynthesizeWidth(k.Z1, k.HMeters, k.TMeters, k.EpsR, reporter);
        double w2 = HammerstadJensen.SynthesizeWidth(k.Z2, k.HMeters, k.TMeters, k.EpsR, reporter);
        double eeff1 = HammerstadJensen.Compute(w1, k.HMeters, k.TMeters, k.EpsR, reporter).Eeff;
        double eeff2 = HammerstadJensen.Compute(w2, k.HMeters, k.TMeters, k.EpsR, reporter).Eeff;
        double eeffMax = Math.Max(eeff1, eeff2);

        int nGeo = ResolveNonUniformSectionCount(k);

        return new MklopfGeometryData
        {
            TotalArcMeters = totalArc,
            MinRadiusMeters = minRadius,
            EeffMax = eeffMax,
            CurvatureExceedsThreshold = curvatureExceeds,
            CurvatureLocalWidthMeters = curvatureLocalWidth,
            NGeoConverged = nGeo,
        };
    }

    /// <summary>R-mk-6: converge on the section count by actually comparing the cascade's own
    /// Z-parameters at N and 2N sections (non-uniform Δ(ln Z) placement — R-mk-4/5) at a fixed
    /// reference frequency (see <see cref="ConvergenceReferenceFreqHz"/>'s own doc comment), doubling
    /// until a further doubling changes nothing beyond <see cref="ConvergenceTolerance"/>. Runs
    /// entirely against a throwaway, silent reporter — these intermediate section tables are never
    /// cached or reused, only the accepted final N's table is (built again, once, by the caller).</summary>
    private static int ResolveNonUniformSectionCount(MklopfGeometryKey k)
    {
        var quiet = new MicrostripValidityReporter("(MKLOPF convergence search, not reported)");
        int n = 1;
        for (; n <= MaxSections; n *= 2)
        {
            var tableN = BuildNonUniformSectionTable(k, n, quiet);
            var table2N = BuildNonUniformSectionTable(k, 2 * n, quiet);
            var zN = CascadeAt(tableN, k, ConvergenceReferenceFreqHz, quiet);
            var z2N = CascadeAt(table2N, k, ConvergenceReferenceFreqHz, quiet);
            if (ZParamsConverged(zN, z2N, ConvergenceTolerance)) return n;
        }
        return MaxSections;
    }

    private static bool ZParamsConverged(
        (Complex Z11, Complex Z12, Complex Z21, Complex Z22) a,
        (Complex Z11, Complex Z12, Complex Z21, Complex Z22) b, double tol)
        => RelDiff(a.Z11, b.Z11) < tol && RelDiff(a.Z12, b.Z12) < tol
        && RelDiff(a.Z21, b.Z21) < tol && RelDiff(a.Z22, b.Z22) < tol;

    private static double RelDiff(Complex a, Complex b)
        => (a - b).Magnitude / Math.Max(a.Magnitude, 1e-30);

    /// <summary>R-mk-4/5: non-uniform (equal Δ ln Z) section placement, via
    /// <see cref="MicrostripCascadeSectioning.NonUniformBoundaries"/> — the default path whenever
    /// <c>N</c> is not manually overridden.</summary>
    private static MklopfSectionTable BuildNonUniformSectionTable(MklopfGeometryKey k, int n, MicrostripValidityReporter reporter)
    {
        double[] boundaries = MicrostripCascadeSectioning.NonUniformBoundaries(
            t => KlopfensteinTaper.ImpedanceAt(t, k.Z1, k.Z2, k.GammaMax), n);

        double totalArc = MicrostripOffsetCenterline.TotalArcLength(k.LengthMeters, k.OffsetMeters);
        var sections = new MklopfSection[n];
        for (int i = 0; i < n; i++)
        {
            double tMid = 0.5 * (boundaries[i] + boundaries[i + 1]);
            double zMid = KlopfensteinTaper.ImpedanceAt(tMid, k.Z1, k.Z2, k.GammaMax);
            double wMid = HammerstadJensen.SynthesizeWidth(zMid, k.HMeters, k.TMeters, k.EpsR, reporter);
            var (z0Static, eeff0) = HammerstadJensen.Compute(wMid, k.HMeters, k.TMeters, k.EpsR, reporter);
            double dsArc = (boundaries[i + 1] - boundaries[i]) * totalArc;
            sections[i] = new MklopfSection(wMid, z0Static, eeff0, dsArc);
        }
        return new MklopfSectionTable { Sections = sections };
    }

    /// <summary>The ORIGINAL uniform-arc-fraction placement — used only when
    /// <c>_sectionCountOverride</c> forces an exact N, so that path's behavior (and R-mk-1's own
    /// "hoisting changes nothing" claim) stays directly comparable to the pre-this-brief model.</summary>
    private static MklopfSectionTable BuildUniformSectionTable(MklopfGeometryKey k, int n, MicrostripValidityReporter reporter)
    {
        double totalArc = MicrostripOffsetCenterline.TotalArcLength(k.LengthMeters, k.OffsetMeters);
        double sectionArcLen = totalArc / n;
        var sections = new MklopfSection[n];
        for (int i = 0; i < n; i++)
        {
            double sMid = (i + 0.5) / n;
            double z = KlopfensteinTaper.ImpedanceAt(sMid, k.Z1, k.Z2, k.GammaMax);
            double wMid = HammerstadJensen.SynthesizeWidth(z, k.HMeters, k.TMeters, k.EpsR, reporter);
            var (z0Static, eeff0) = HammerstadJensen.Compute(wMid, k.HMeters, k.TMeters, k.EpsR, reporter);
            sections[i] = new MklopfSection(wMid, z0Static, eeff0, sectionArcLen);
        }
        return new MklopfSectionTable { Sections = sections };
    }

    /// <summary>The only genuinely frequency-dependent physics (R-mk-1): Kirschning-Jansen
    /// dispersion, the two loss terms, and the ABCD cascade itself — shared by both the real
    /// <see cref="Stamp"/> call and <see cref="ResolveNonUniformSectionCount"/>'s own convergence
    /// test, so the two can never silently diverge.</summary>
    private static (Complex Z11, Complex Z12, Complex Z21, Complex Z22) CascadeAt(
        MklopfSectionTable table, MklopfGeometryKey k, double freqHz, MicrostripValidityReporter reporter)
    {
        var total = MicrostripAbcd.Identity;
        foreach (var sec in table.Sections)
        {
            var (z0, eeff) = KirschningJansen.Compute(
                freqHz, sec.WidthMeters / k.HMeters, k.EpsR, k.HMeters, sec.Z0Static, sec.Eeff0, reporter);

            double alphaNpPerM = MicrostripLoss.ConductorLossNpPerM(freqHz, k.SigmaSPerM, sec.WidthMeters, z0)
                + MicrostripLoss.DielectricLossNpPerM(freqHz, k.EpsR, eeff, k.TanD);
            double betaRadPerM = 2.0 * Math.PI * freqHz / MicrostripLoss.SpeedOfLight * Math.Sqrt(eeff);
            var gammaLength = new Complex(alphaNpPerM * sec.ArcLengthMeters, betaRadPerM * sec.ArcLengthMeters);

            var section = MicrostripAbcd.UniformSection(new Complex(z0, 0.0), gammaLength);
            total = total.Cascade(section);
        }
        return total.ToZ();
    }
}
