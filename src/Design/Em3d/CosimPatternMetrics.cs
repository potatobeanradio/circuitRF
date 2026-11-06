// brief-cosim-pattern-4 R-cp4-1 / R-cp4-4 — one fields analysis's far field as the expression engine reads it, at the analysis's one frequency.
// Each value is the registry's own (PlanarMetrics), so RealizedGainDbi(F1) IS the cube F1.farfield.RealizedGainDbi, whichever evaluation asked.
//
// CHEAP WHERE IT CAN BE. An optimizer's evaluation arrives here with the waves and no pattern. The first read sums it, once per evaluation, and
// each metric then computes only itself: the beamwidth cuts and the polarization a full publish forms are never asked for.
//
// A DIRECTION IS ON THE GRID. A value between grid points would be an interpolation nobody asked for, and the grid is the solver's, so a direction
// off it is refused naming the grid, as a frequency between stored points is.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Em3d;

internal sealed class CosimPatternMetrics(FieldsAnalysis fa, PortFarFieldSet far, int frequency, FieldCosimulation.CosimWaves waves, Em3dPatternPoint? solved)
{
    private const double GridToleranceDeg = 1e-6;

    private readonly object _gate = new();
    private PlanarMetricContext? _context;
    private static long _sums;

    /// <summary>The patterns this class has summed in this process: an evaluation no metric read adds nothing.</summary>
    public static long Sums => Interlocked.Read(ref _sums);

    public double Evaluate(FieldMetric metric, double? thetaDeg, double? phiDeg)
    {
        lock (_gate)
        {
            var which = Metric(metric);
            var c = Context();
            var outcome = PlanarMetrics.EvaluateOne(c, which);
            if (!outcome.Ok) throw new ExpressionException($"{metric}({fa.Name}): {outcome.Verdict.Reason}");
            return thetaDeg is { } theta && phiDeg is { } phi
                ? PlanarMetrics.InDirection(c, which, Direction(theta, phi))
                : outcome.Value;
        }
    }

    private static PlanarMetric Metric(FieldMetric metric) => metric switch
    {
        FieldMetric.RealizedGainDbi     => PlanarMetric.RealizedGainDbi,
        FieldMetric.GainDbi             => PlanarMetric.GainDbi,
        FieldMetric.DirectivityDbi      => PlanarMetric.DirectivityDbi,
        FieldMetric.RadiationEfficiency => PlanarMetric.RadiationEfficiency,
        FieldMetric.TrpDbm              => PlanarMetric.TrpDbm,
        FieldMetric.PeakEirpDbm         => PlanarMetric.PeakEirpDbm,
        _ => throw new ExpressionException($"'{metric}' is not a far-field measurement."),
    };

    private PlanarMetricContext Context()
    {
        if (_context is not null) return _context;
        Em3dPatternPoint point;
        if (solved is not null) point = solved;
        else
        {
            try { point = FieldCosimulation.PointOf(fa, far, frequency, waves, out _); }
            catch (InvalidOperationException e) { throw new ExpressionException(e.Message); }
            Interlocked.Increment(ref _sums);
        }
        return _context = Em3dRadiation.Context(far.Solver, point, far.ReferenceInputPowerDbm, far.Planar);
    }

    /// <summary>The grid index of a direction in degrees; φ wraps, so −90 and 270 are one direction.</summary>
    private int Direction(double thetaDeg, double phiDeg)
    {
        var grid = far.Grid;
        int it = Find(grid.ThetaDeg, thetaDeg);
        double wrapped = (phiDeg % 360 + 360) % 360;
        int ip = Find(grid.PhiDeg, wrapped);
        if (ip < 0 && Math.Abs(wrapped - 360) <= GridToleranceDeg) ip = Find(grid.PhiDeg, 0);
        if (it < 0 || ip < 0)
            throw new ExpressionException($"Fields '{fa.Name}': theta {G(thetaDeg)}, phi {G(phiDeg)} is not on its pattern's grid, which is theta {Span(grid.ThetaDeg)} and " +
                                          $"phi {Span(grid.PhiDeg)} in degrees. A value between grid points would be an interpolation: name a direction on it.");
        return grid.IndexOf(it, ip);
    }

    private static int Find(IReadOnlyList<double> axis, double want)
    {
        for (int i = 0; i < axis.Count; i++)
            if (Math.Abs(axis[i] - want) <= GridToleranceDeg) return i;
        return -1;
    }

    private static string Span(IReadOnlyList<double> axis)
        => axis.Count > 1 ? $"{G(axis[0])} to {G(axis[^1])} in steps of {G(axis[1] - axis[0])}" : G(axis[0]);

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
