using CircuitRF.Core.Devices.Coupled;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Even/odd-mode impedances to a broadside pair on a given stack: the strip width W and the sideways offset O whose
/// modes, as BCLIN or MBCLIN solve them (<see cref="BroadsideCoupledPair"/>, the part's own model), are the asked Ze and
/// Zo, and the length λg/4 at their mean εeff. The stack fixes the strips' separation, so the offset is the coupling's
/// handle: strips one over the other couple most, and moving them apart sideways weakens it.
///
/// <para><b>The search.</b> Newton's method in (ln W, O) on ln √(Ze·Zo) and ln(Ze/Zo), with a finite-difference
/// Jacobian and halved steps until the residual falls; each step is three cross-section solves or a few more, which the
/// pair's cache keeps, and the search is capped so a stack that cannot give the pair is refused in bounded time. A
/// coupling tighter than the strips reach one over the other (O = 0) is a coded refusal, as is a pair the search does
/// not reach.</para>
/// </summary>
public static class BroadsideSynthesis
{
    /// <summary>The solved modes of strips <paramref name="w"/> wide, <paramref name="o"/> apart, on <paramref name="stack"/>,
    /// exactly as the part's model solves them.</summary>
    public static BroadsideModes ModesOf(double w, double o, BroadsideStack stack)
        => stack.Stripline
            ? new StriplineBroadsideCoupledModel(w, o, 1.0, stack.Hs, stack.Ha, stack.Hb, stack.Ta, stack.Tb, stack.EpsR,
                stack.SigmaSPerM, stack.TanD, "(broadside synthesis)").Modes
            : new MicrostripBroadsideCoupledModel(w, o, 1.0, stack.Hb, stack.Hs, stack.Ta, stack.Tb, stack.Hd, stack.EpsR,
                stack.SigmaSPerM, stack.TanD, "(broadside synthesis)").Modes;

    public static (double W, double O, double L, BroadsideModes Modes) Synthesize(double ze, double zo, double fHz,
        BroadsideStack stack, string part)
    {
        double z0 = Math.Sqrt(ze * zo), ratio = ze / zo, h = stack.Hb + stack.Tb + stack.Hs;
        (double, double) F(double lnW, double o)
        {
            var m = ModesOf(Math.Exp(lnW), o, stack);
            return (Math.Log(Math.Sqrt(m.Ze * m.Zo) / z0), Math.Log(m.Ze / m.Zo / ratio));
        }
        double x = Math.Log(2.0 * h), y = Math.Exp(x) / 2.0;
        var (f1, f2) = F(x, y);
        for (int it = 0; it < 25 && Math.Abs(f1) + Math.Abs(f2) > 1e-10; it++)
        {
            double dx = 1e-4, dy = 1e-4 * Math.Exp(x);
            var (a1, a2) = F(x + dx, y);
            var (b1, b2) = F(x, y + dy);
            double j11 = (a1 - f1) / dx, j21 = (a2 - f2) / dx, j12 = (b1 - f1) / dy, j22 = (b2 - f2) / dy;
            double det = j11 * j22 - j12 * j21;
            if (det == 0 || !double.IsFinite(det)) break;
            double sx = -(j22 * f1 - j12 * f2) / det, sy = -(-j21 * f1 + j11 * f2) / det;
            sx = Math.Clamp(sx, -0.5, 0.5);
            sy = Math.Clamp(sy, -Math.Exp(x), Math.Exp(x));
            double t = 1.0, norm = Math.Abs(f1) + Math.Abs(f2);
            for (int back = 0; back < 8; back++, t /= 2)
            {
                double nx = Math.Clamp(x + t * sx, Math.Log(h / 50.0), Math.Log(h * 50.0)), ny = Math.Max(0.0, y + t * sy);
                var (n1, n2) = F(nx, ny);
                if (Math.Abs(n1) + Math.Abs(n2) < norm) { x = nx; y = ny; f1 = n1; f2 = n2; break; }
            }
            if (Math.Abs(f1) + Math.Abs(f2) >= norm) break;
        }
        if (Math.Abs(f2) > 1e-6 && y == 0.0 && f2 < 0)
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line.broadside-too-tight",
                DiagnosticSeverity.Error, "{part}: coupling {c} needs strips closer than this stack's {hs} m between the two "
              + "conductors; even one over the other they reach only {cmax}.", ("part", part),
                ("c", CoupledLineSynthesis.Coupling(ze, zo)), ("hs", stack.Hs),
                ("cmax", MaxCoupling(Math.Exp(x), stack))));
        if (Math.Abs(f1) + Math.Abs(f2) > 1e-6)
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line.broadside-unreached",
                DiagnosticSeverity.Error, "{part}: no broadside pair on this stack has Ze = {ze} ohm and Zo = {zo} ohm.",
                ("part", part), ("ze", ze), ("zo", zo)));

        double w = PassivePlan.OnGrid(Math.Exp(x)), o = PassivePlan.OnGrid(y);
        var modes = ModesOf(w, o, stack);
        double l = PassivePlan.OnGrid(PlanarLineAngle.LengthMeters(0.5 * (modes.EeffE + modes.EeffO), Math.PI / 2.0, fHz));
        return (w, o, l, modes);
    }

    private static double MaxCoupling(double w, BroadsideStack stack)
    {
        var m = ModesOf(w, 0.0, stack);
        return CoupledLineSynthesis.Coupling(m.Ze, m.Zo);
    }
}
