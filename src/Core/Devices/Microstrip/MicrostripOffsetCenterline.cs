namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// MKlopf's Offset centerline (brief-mtaper-mklopf.md §3) — a genuinely novel, unpublished
/// extension (R-klp-11), so this class's own formulas ARE the specification (the brief's §3.2's
/// quintic + G2-continuity reasoning), not a transcription from an external source; R1 does not
/// apply here the way it does to the physics classes in this folder.
///
/// <b>R-klp-7: the quintic <c>y(t) = Offset·(6t⁵−15t⁴+10t³)</c>, t=x/L</b> — zero SLOPE and zero
/// CURVATURE at both t=0 and t=1, so it joins straight input/output lines with full G2 continuity
/// (a raised cosine has zero slope but MAXIMUM curvature at the ends — exactly backwards; rejected
/// per the brief's own explicit reasoning, §3.2).
///
/// <b>R-klp-6: the Klopfenstein profile is distributed along ARC LENGTH, not axial position</b> —
/// <see cref="AxialPositionAtArcFraction"/> is the one place that mapping happens, so
/// <see cref="MicrostripKlopfModel"/> never has to reason about the two coordinates itself.
///
/// <b>R-klp-8: width is measured perpendicular to the local tangent</b> — callers needing the
/// offset-outline edges should use <see cref="DyDx"/> to build the local normal direction
/// themselves (this class does not build the outline; that is the PCell's own job, in
/// <c>src/Ui/Layout/PCells/</c>).
/// </summary>
public static class MicrostripOffsetCenterline
{
    /// <summary>y(x) — the quintic centerline's own lateral displacement at axial position x.</summary>
    public static double Y(double x, double lengthMeters, double offsetMeters)
    {
        double t = x / lengthMeters;
        return offsetMeters * (6.0 * Math.Pow(t, 5) - 15.0 * Math.Pow(t, 4) + 10.0 * Math.Pow(t, 3));
    }

    /// <summary>dy/dx — the local tangent slope.</summary>
    public static double DyDx(double x, double lengthMeters, double offsetMeters)
    {
        double t = x / lengthMeters;
        double dydt = offsetMeters * 30.0 * t * t * (t - 1.0) * (t - 1.0);
        return dydt / lengthMeters;
    }

    /// <summary>d²y/dx² — feeds <see cref="Curvature"/>.</summary>
    public static double D2yDx2(double x, double lengthMeters, double offsetMeters)
    {
        double t = x / lengthMeters;
        double d2ydt2 = offsetMeters * 60.0 * t * (t - 1.0) * (2.0 * t - 1.0);
        return d2ydt2 / (lengthMeters * lengthMeters);
    }

    /// <summary>Local curvature κ(x) = |y''| / (1+y'²)^1.5 (exact plane-curve formula — the brief's
    /// own §3.2a worked example additionally notes the (1+y'²)^1.5 factor is "a modest correction,
    /// ≈1.12 at Offset/L=1/3," which this exact formula reproduces automatically, not as a
    /// separately-applied fudge).</summary>
    public static double Curvature(double x, double lengthMeters, double offsetMeters)
    {
        double yp = DyDx(x, lengthMeters, offsetMeters);
        double ypp = D2yDx2(x, lengthMeters, offsetMeters);
        return Math.Abs(ypp) / Math.Pow(1.0 + yp * yp, 1.5);
    }

    /// <summary>Arc length of the centerline between two axial positions (Simpson's rule).</summary>
    public static double ArcLength(double xStart, double xEnd, double lengthMeters, double offsetMeters, int segments = 200)
    {
        if (offsetMeters == 0.0) return xEnd - xStart;
        if (segments % 2 != 0) segments++;
        double h = (xEnd - xStart) / segments;
        double Speed(double x)
        {
            double yp = DyDx(x, lengthMeters, offsetMeters);
            return Math.Sqrt(1.0 + yp * yp);
        }
        double sum = Speed(xStart) + Speed(xEnd);
        for (int i = 1; i < segments; i++)
            sum += (i % 2 == 0 ? 2.0 : 4.0) * Speed(xStart + i * h);
        return sum * h / 3.0;
    }

    /// <summary>Total arc length of the whole centerline (R-klp-6 — reported alongside the axial
    /// length "since the two now differ").</summary>
    public static double TotalArcLength(double lengthMeters, double offsetMeters, int segments = 400)
        => offsetMeters == 0.0 ? lengthMeters : ArcLength(0.0, lengthMeters, lengthMeters, offsetMeters, segments);

    /// <summary>The inverse of arc length: the axial position x whose arc length FROM THE START
    /// equals <paramref name="sFraction"/>·(total arc length) — R-klp-6's own mapping, found by
    /// bisection.</summary>
    public static double AxialPositionAtArcFraction(double sFraction, double lengthMeters, double offsetMeters,
        double totalArcLength)
    {
        if (offsetMeters == 0.0) return sFraction * lengthMeters;
        double targetArc = sFraction * totalArcLength;
        double xLo = 0.0, xHi = lengthMeters;
        for (int i = 0; i < 50; i++)
        {
            double xMid = 0.5 * (xLo + xHi);
            double arcMid = ArcLength(0.0, xMid, lengthMeters, offsetMeters, 60);
            if (arcMid < targetArc) xLo = xMid; else xHi = xMid;
        }
        return 0.5 * (xLo + xHi);
    }

    /// <summary>R-klp-10: the minimum radius of curvature anywhere along the centerline (infinite
    /// when Offset=0 — a straight line has no curvature at all).</summary>
    public static double MinRadiusOfCurvature(double lengthMeters, double offsetMeters, int samples = 400)
    {
        if (offsetMeters == 0.0) return double.PositiveInfinity;
        double maxKappa = 0.0;
        for (int i = 0; i <= samples; i++)
        {
            double k = Curvature(lengthMeters * i / samples, lengthMeters, offsetMeters);
            if (k > maxKappa) maxKappa = k;
        }
        return maxKappa > 0.0 ? 1.0 / maxKappa : double.PositiveInfinity;
    }
}
