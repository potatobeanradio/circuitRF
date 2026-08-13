namespace CircuitRF.Harmonica;

/// <summary>
/// R-h9r2-18/18a — validates a candidate Pin-sweep range or tickle level BEFORE it is written, the
/// same "invalid input keeps the old value" discipline <see cref="DcivFamily.IsValidOverride"/> already
/// uses: a rejected candidate must never reach <see cref="HarmonicaSettings"/>, so what is on screen
/// stays exactly what it was rather than reverting after the fact.
/// </summary>
public static class PowerSweepValidation
{
    /// <summary>
    /// All three finite, <paramref name="start"/> &lt; <paramref name="stop"/>, <paramref name="step"/>
    /// &gt; 0, and the resulting point count at or under <see cref="HarmonicaSettings.MaxSweepPoints"/>
    /// — refused BY NAME with the computed count, never silently clamped (a clamp would mean the plot
    /// is not the sweep the user typed).
    /// </summary>
    public static bool IsValidRange(double start, double stop, double step, out int pointCount)
    {
        pointCount = 0;
        if (!double.IsFinite(start) || !double.IsFinite(stop) || !double.IsFinite(step)) return false;
        if (start >= stop || step <= 0) return false;

        pointCount = PointCount(start, stop, step);
        return pointCount <= HarmonicaSettings.MaxSweepPoints;
    }

    /// <summary>The ladder's own point count — <c>PinSearch.Sweep</c>'s own rule, mirrored here so the
    /// dialog can show/refuse it WITHOUT running a single HB solve.</summary>
    public static int PointCount(double start, double stop, double step)
    {
        int regular = (int)System.Math.Floor((stop - start) / step) + 1;
        double lastRegular = start + (regular - 1) * step;
        bool stopAlreadyOnLadder = System.Math.Abs(lastRegular - stop) < 1e-9;
        return stopAlreadyOnLadder ? regular : regular + 1;
    }

    /// <summary>R-h9r2-18a — a tickle at or above the sweep's own Start is not a small-signal
    /// reference and the number it produces is meaningless.</summary>
    public static bool IsValidTickle(double tickleDbm, double startDbm)
        => double.IsFinite(tickleDbm) && tickleDbm < startDbm;
}
