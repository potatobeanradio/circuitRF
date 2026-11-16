using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>One quarter-wave arm of a branch-line coupler.</summary>
/// <param name="Z">The arm's impedance.</param>
/// <param name="WMeters">Its width, MLIN's at F.</param>
/// <param name="Eeff">MLIN's dispersive εeff of the arm at F.</param>
/// <param name="LMeters">Its line between the two junctions' reference planes: λg/4 at F.</param>
public sealed record BranchLineArm(double Z, double WMeters, double Eeff, double LMeters);

/// <summary>What a 3 dB branch-line coupler comes out to on one substrate: the derived numbers, and the plan
/// the model stamps and the PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 feeds.</param>
/// <param name="FeedMeters">Length of each feed, junction to pin.</param>
/// <param name="Series">The series arms of one through line, from the 1-2 end on; the 4-3 line is the same.</param>
/// <param name="Shunts">The shunt arms joining the two through lines, from the 1-4 end on.</param>
public sealed record BranchLineDesign(
    double Z0, double FreqHz, int Sections, double W0Meters, double FeedMeters,
    IReadOnlyList<BranchLineArm> Series, IReadOnlyList<BranchLineArm> Shunts, PassivePlan Plan);

/// <summary>
/// The 3 dB branch-line (90°) hybrid planned in microstrip, in one or two sections. Pure: specs and a substrate
/// in, <see cref="BranchLineDesign"/> out; <see cref="MicrostripPlanModel"/> stamps the plan and the PCell draws
/// it.
///
/// <para><b>Pins.</b> 1 and 2 are the ends of one through line, 4 and 3 of the other; the shunt arms join the two
/// lines. Driven at 1, 2 is the through port (−90° for one section), 3 the coupled port (90° behind 2) and 4 is
/// isolated (D. M. Pozar, Microwave Engineering, the quadrature hybrid).</para>
///
/// <para><b>The arms.</b> Even/odd analysis about the line between the two through lines (J. Reed and
/// G. J. Wheeler, IRE Trans. MTT, 1956) turns each shunt arm into a λ/8 open or shorted stub on one quarter-wave
/// line. One section: series arms of Z0/√2 and shunt arms of Z0. Two sections: series arms of Z0 and shunt arms of
/// (1 + √2)·Z0, √2·Z0, (1 + √2)·Z0, the equal-split two-section design (the synthesis method is R. Levy and
/// L. F. Lind, IEEE Trans. MTT, 1968, and M. Muraguchi, T. Yukitake and Y. Naito, IEEE Trans. MTT, 1983; these values
/// solve its even/odd conditions); matched, isolated and split 3 dB at F, as the model checks. Widths are MLIN's at F;
/// each arm is λg/4 in MLIN's own dispersive εeff, measured between the reference planes of the two MTEEs it
/// joins (the through arms' at the branch's half width, the branch's at the wider through arm's half width).
/// The shunt arms share one span between the two lines, so arms of different εeff (two sections) each take the
/// mean of their quarter waves.</para>
///
/// <para><b>The pieces.</b> An MTEE at each junction, an MLIN for each arm and an MLIN feed to each pin. Each
/// tee's branch carries one W0 of its shunt arm past the reference plane: MTEE treats a branch ending at the
/// through line's edge as a straight line with a pin on it, which would leave the junction unsimulated. Every
/// port pin sits one W0 of Z0 line past its junction, as <see cref="WilkinsonPlan"/>'s do.</para>
/// </summary>
public static class BranchLinePlan
{
    /// <summary>The part's pins, in terminal order.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4"];

    /// <param name="sections">N, 1 or 2.</param>
    public static BranchLineDesign Build(double z0, double fHz, double sections, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.branchline.spec-not-positive",
                    DiagnosticSeverity.Error, "Branch-line coupler: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));
        if (sections is not (1.0 or 2.0))
            throw new ParameterRefusalException(Diagnostic.Create("device.branchline.sections",
                DiagnosticSeverity.Error, "Branch-line coupler: N = {n}; one or two sections are planned.",
                ("n", sections)));

        int n = (int)sections;
        double r2 = Math.Sqrt(2.0);
        double[] seriesZ = n == 1 ? [z0 / r2] : [z0, z0];
        double[] shuntZ = n == 1 ? [z0, z0] : [(1.0 + r2) * z0, r2 * z0, (1.0 + r2) * z0];

        double w0 = PassivePlan.OnGrid(Width(z0, fHz, s, "Branch-line coupler", "device.branchline.width-unreachable"));
        BranchLineArm Arm(double z)
        {
            double w = PassivePlan.OnGrid(Width(z, fHz, s, "Branch-line coupler", "device.branchline.width-unreachable"));
            double eeff = Line(w, s).LineParameters(fHz).Eeff;
            return new(z, w, eeff, PassivePlan.OnGrid(PlanarLineAngle.LengthMeters(eeff, Math.PI / 2.0, fHz)));
        }
        var series = seriesZ.Select(Arm).ToArray();
        var shunts = shuntZ.Select(Arm).ToArray();

        // One span for every shunt arm: each arm's quarter wave plus its two junctions' offsets, averaged.
        double[] seriesW = [.. series.Select(a => a.WMeters)];
        double[] offset = [.. shunts.Select((_, k) => Offset(k, w0, seriesW))];
        double span = PassivePlan.OnGrid(shunts.Select((a, k) => a.LMeters + 2.0 * offset[k]).Average());
        shunts = [.. shunts.Select((a, k) => a with { LMeters = span - 2.0 * offset[k] })];

        double feed = w0, stub = w0;
        if (shunts.FirstOrDefault(a => a.LMeters - 2.0 * stub <= 0.0) is { } shortArm)
            throw new ParameterRefusalException(Diagnostic.Create("device.branchline.arm-too-short",
                DiagnosticSeverity.Error,
                "Branch-line coupler: a quarter wave of the {z} ohm arm at F = {f} Hz is {l} m, too short to leave the "
              + "junctions on each end {stub} m of it. Lower F.",
                ("z", shortArm.Z), ("f", fHz), ("l", shortArm.LMeters), ("stub", stub)));

        return new BranchLineDesign(z0, fHz, n, w0, feed, series, shunts,
            Layout(w0, feed, stub, [.. series.Select(a => (a.WMeters, a.LMeters))],
                   [.. shunts.Select(a => (a.WMeters, a.LMeters))]));
    }

    /// <summary>
    /// The sections for given dimensions: <paramref name="series"/> (width, line between the reference planes) for
    /// each through line's arms, <paramref name="shunts"/> likewise, the feeds' width and length, and the length of
    /// shunt line each tee's branch carries past its reference plane. Through line 1-2 runs along +x at y = 0 with
    /// pin 1 at the origin; line 4-3 lies one shunt span below it. Every shunt arm must give the same span.
    /// </summary>
    public static PassivePlan Layout(double w0, double feed, double stub,
        IReadOnlyList<(double W, double L)> series, IReadOnlyList<(double W, double L)> shunts)
    {
        int n = series.Count;
        double[] seriesW = [.. series.Select(a => a.W)];
        double span = shunts[0].L + 2.0 * Offset(0, w0, seriesW);

        // Plan nodes 5 on: per row (0 the 1-2 line, 1 the 4-3 line) and junction, its left, right and branch ends.
        int next = Pins.Count;
        var left = new int[2, n + 1];
        var right = new int[2, n + 1];
        var branch = new int[2, n + 1];
        for (int r = 0; r < 2; r++)
            for (int k = 0; k <= n; k++)
                (left[r, k], right[r, k], branch[r, k]) = (++next, ++next, ++next);

        var x = new double[n + 1];
        x[0] = feed + shunts[0].W / 2.0;
        for (int k = 0; k < n; k++) x[k + 1] = x[k] + shunts[k].W / 2.0 + series[k].L + shunts[k + 1].W / 2.0;

        var sections = new List<PlanSection>();
        for (int k = 0; k <= n; k++)
        {
            double wb = shunts[k].W, l3 = Offset(k, w0, seriesW) + stub;
            double wl = k == 0 ? w0 : series[k - 1].W, wr = k == n ? w0 : series[k].W;
            // The 1-2 line's tee: through left to right, branch down. The 4-3 line's: turned over, branch up.
            sections.Add(new PlanTee(wl, wr, wb, wb / 2.0, wb / 2.0, l3, [left[0, k], right[0, k], branch[0, k]],
                x[k] - wb / 2.0, 0.0, 0.0));
            sections.Add(new PlanTee(wr, wl, wb, wb / 2.0, wb / 2.0, l3, [right[1, k], left[1, k], branch[1, k]],
                x[k] + wb / 2.0, -span, 180.0));
            sections.Add(new PlanLine([branch[0, k], branch[1, k]], x[k], -l3, 270.0, wb, span - 2.0 * l3));
        }
        for (int k = 0; k < n; k++)
            for (int r = 0; r < 2; r++)
                sections.Add(new PlanLine([right[r, k], left[r, k + 1]], x[k] + shunts[k].W / 2.0, -span * r, 0.0,
                    series[k].W, series[k].L));

        double xEnd = x[n] + shunts[n].W / 2.0;
        sections.Add(new PlanLine([1, left[0, 0]], 0.0, 0.0, 0.0, w0, feed));
        sections.Add(new PlanLine([right[0, n], 2], xEnd, 0.0, 0.0, w0, feed));
        sections.Add(new PlanLine([right[1, n], 3], xEnd, -span, 0.0, w0, feed));
        sections.Add(new PlanLine([4, left[1, 0]], 0.0, -span, 0.0, w0, feed));
        return new PassivePlan(Pins, sections, next);
    }

    /// <summary>Junction <paramref name="k"/>'s branch reference plane from its centre: half its wider through arm.</summary>
    private static double Offset(int k, double w0, double[] seriesW)
        => Math.Max(k == 0 ? w0 : seriesW[k - 1], k == seriesW.Length ? w0 : seriesW[k]) / 2.0;

    internal static MicrostripLineModel Line(double w, MicrostripSubstrate s)
        => new(w, 1.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "(planned passive)", s.RoughnessMeters);

    /// <summary>The width whose MLIN Z0 at F is <paramref name="z"/>; a Z0 no width reaches is refused with
    /// <paramref name="code"/>. Shared by the hybrids' plans.</summary>
    internal static double Width(double z, double fHz, MicrostripSubstrate s, string part, string code)
    {
        var reporter = new MicrostripValidityReporter(part);
        double w = Planar.PlanarLineSynthesis.SynthesizeWidth(x => Line(x, s).LineParameters(fHz).Z0, z, s.HMeters,
            "MLIN", reporter);
        if (reporter.Drain() is [var unreachable, ..])
            throw new ParameterRefusalException(Diagnostic.Create(code, DiagnosticSeverity.Error, "{message}",
                ("message", unreachable.Message)));
        return w;
    }
}
