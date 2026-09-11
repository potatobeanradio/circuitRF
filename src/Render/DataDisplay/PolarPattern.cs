// ================================================================
//  PolarPattern.cs  —  the dB RADIAL MODE of the polar plot, and the
//  sentences a pattern has to carry with it
//
//  ANT-7 §2/§4. The polar plot's radial axis was already general — it
//  frames on the window's own radius and lays a 1/2/5 lattice under it
//  (AxesRenderer.PolarRings) — but that lattice is LINEAR, and an
//  antenna pattern is read in dB with a floor. This file is the mode
//  that makes it so: where the outer ring is, how far down the centre
//  is, where the rings fall, and what each one says.
//
//  IT IS A MODE, NOT A DERIVED EXPRESSION, and ANT-7 §2 says why: a
//  pattern CAN be pushed through the existing polar plot as
//  z = 10^((dB−ref)/20)·e^{jθ} and it will draw. The rings are then
//  linear and mislabelled, the floor is invisible, and the plot has no
//  idea it is showing a pattern — so it cannot say whether it is
//  normalised, which cut it is, or that the lower hemisphere is absent.
//  A picture that is PLAUSIBLE is indistinguishable from one that is
//  RIGHT.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CircuitRF.Render.DataDisplay;

/// <summary>How a Polar plot's RADIUS is read.</summary>
public enum PolarRadialMode
{
    /// <summary>The radius is the value itself, on the 1/2/5 lattice
    /// <see cref="AxesRenderer.PolarRings"/> builds. Every polar plot that existed before ANT-7 —
    /// a Γ locus, an impedance in ohms, a loop gain.</summary>
    Linear,

    /// <summary>The radius is decibels between a floor at the centre and a reference at the outer
    /// ring — an antenna pattern.</summary>
    Db,
}

/// <summary>What the outer ring of a <see cref="PolarRadialMode.Db"/> plot IS.</summary>
public enum PolarDbReferenceMode
{
    /// <summary>The peak of the traces on the plot, so the outer ring reads 0 dB. The usual
    /// default, and the plot must say so — a normalised pattern and an absolute one look
    /// identical and mean very different things.</summary>
    Peak,

    /// <summary>A value the author chose, in the quantity's own unit (dBi, dB(W/sr), …).</summary>
    Absolute,
}

/// <summary>
/// The resolved radial scale of one pattern plot: where the outer ring is, where the centre is, and
/// what the rings between them say. Immutable, and built by <see cref="Plot.RefreshPolarPattern"/>
/// from ALL the traces on the plot at once.
///
/// <para><b>The reference is per-PLOT and never per-trace, and that is the point of computing it
/// here.</b> An E-plane cut and an H-plane cut each normalised to its OWN peak is a lie about their
/// relative levels — the two curves would touch the outer ring together whatever the antenna
/// does.</para>
/// </summary>
public sealed record PolarPatternScale
{
    /// <summary>The value at the outer ring, in the trace's own dB quantity.</summary>
    public required double ReferenceDb { get; init; }

    /// <summary>The value at the CENTRE. Everything at or below it is drawn there — see
    /// <see cref="Radius"/>.</summary>
    public required double FloorDb { get; init; }

    /// <summary>Spacing of the labelled rings, in dB. 10 by default, which is what a pattern is
    /// read on.</summary>
    public required double RingStepDb { get; init; }

    /// <summary>True when <see cref="ReferenceDb"/> came from the data's own peak rather than from
    /// a value the author named. Drives the whole of the reference sentence.</summary>
    public required bool Normalised { get; init; }

    /// <summary>The unit the numbers are in — "dB" when nothing better is known, "dBi" or
    /// "dB(W/sr)" when the cube said so. Never empty.</summary>
    public required string Unit { get; init; }

    /// <summary>How many samples sat ABOVE <see cref="ReferenceDb"/> and were therefore drawn at
    /// the outer ring. Zero whenever <see cref="Normalised"/> (the peak IS the reference); non-zero
    /// only when an author named an absolute reference below their own data, which the plot states
    /// rather than hides.</summary>
    public int AboveReferenceCount { get; init; }

    /// <summary>The span the disc covers, always positive.</summary>
    public double SpanDb => ReferenceDb - FloorDb;

    /// <summary>
    /// The radius, 0 at the centre and 1 at the outer ring, of one dB value.
    ///
    /// <para><b>Below the floor is drawn AT the floor, not dropped</b> (ANT-7 §2). A gap in a
    /// pattern trace reads as a null in the antenna, and a real null and a clipped value must not
    /// look the same — so the clamp is here and there is no "skip this point" branch anywhere
    /// above it. A non-finite value is the one thing that still has no radius; the caller skips it,
    /// exactly as it does on a rectangular plot.</para>
    /// </summary>
    public double Radius(double db)
    {
        if (!double.IsFinite(db)) return double.NaN;
        double span = SpanDb;
        if (!(span > 0)) return double.NaN;
        double r = (db - FloorDb) / span;
        return r < 0 ? 0.0 : r > 1.0 ? 1.0 : r;
    }

    /// <summary>
    /// The rings, outer first: <see cref="ReferenceDb"/> and every step below it that still clears
    /// the floor. The outer ring is always present — it is what says what scale the plot is at —
    /// and the centre carries no ring, being a point.
    /// </summary>
    public IReadOnlyList<double> RingsDb
    {
        get
        {
            var rings = new List<double>();
            double span = SpanDb;
            if (!(span > 0) || !(RingStepDb > 0)) return rings;

            // A ring a small fraction of a step above the floor is dropped rather than drawn on top
            // of the centre — the same rule the linear lattice applies at its own boundary, for the
            // same reason: two circles a fraction of a step apart read as a rendering fault.
            double lowest = FloorDb + RingStepDb * 0.4;
            for (double v = ReferenceDb; v > lowest; v -= RingStepDb) rings.Add(v);
            return rings;
        }
    }

    /// <summary>
    /// What one ring SAYS. A normalised plot labels relative to its own reference (0, −10, −20 …);
    /// an absolute one labels the value itself. The unit is on the outer ring only — repeating it
    /// on every ring is four copies of one fact around a small circle.
    /// </summary>
    public string RingLabel(double ringDb, bool withUnit)
    {
        double shown = Normalised ? ringDb - ReferenceDb : ringDb;
        // A ring lattice is a round number by construction; digits past the step's own resolution
        // are noise. One decimal is enough for a 0.5 dB step and reads as an integer above it.
        string text = Math.Abs(shown - Math.Round(shown)) < 5e-4
            ? Math.Round(shown).ToString("0", CultureInfo.InvariantCulture)
            : shown.ToString("0.#", CultureInfo.InvariantCulture);
        return withUnit ? $"{text} {Unit}" : text;
    }

    /// <summary>
    /// The sentence §4 requires first: normalised or absolute, and the reference value. Never
    /// omitted — "a 0 dB peak with no reference is not a result".
    /// </summary>
    public string ReferenceCaption()
    {
        string refText = ReferenceDb.ToString("0.##", CultureInfo.InvariantCulture);
        string head = Normalised
            ? $"normalised — outer ring = peak {refText} {Unit}"
            : $"absolute — outer ring {refText} {Unit}";
        string tail = $", {Math.Round(SpanDb).ToString("0.#", CultureInfo.InvariantCulture)} dB to centre, "
                    + $"{RingStepDb.ToString("0.#", CultureInfo.InvariantCulture)} dB rings";
        if (AboveReferenceCount > 0)
            tail += $" ({AboveReferenceCount} sample(s) above the reference, drawn at the outer ring)";
        return head + tail;
    }
}

/// <summary>
/// The angular half of a pattern plot, and the sentences that say what is drawn. Separated from
/// <see cref="PolarPatternScale"/> because the radius is a property of the PLOT and the angle is a
/// property of one TRACE's own swept axis.
/// </summary>
public static class PolarPatternAngle
{
    /// <summary>
    /// <b>0° at the top, increasing CLOCKWISE</b> — the compass convention, which is what both an
    /// elevation cut (θ from zenith) and an azimuth cut (φ from a chosen bearing) are read on. It is
    /// fixed rather than configurable: the alternative is the complex plane's own convention (0° at
    /// the right, counter-clockwise), which puts an antenna's zenith at three o'clock, and a plot
    /// whose orientation is a setting is a plot whose orientation has to be read off a control
    /// before the picture means anything.
    /// </summary>
    /// <returns>World (x, y) on the unit disc — y up, as the plot's own window is.</returns>
    public static (double X, double Y) Point(double angleDeg, double radius)
    {
        double a = (90.0 - angleDeg) * Math.PI / 180.0;
        return (radius * Math.Cos(a), radius * Math.Sin(a));
    }

    /// <summary>
    /// Whether an axis can be an ANGLE at all, and the factor that turns its values into degrees.
    ///
    /// <para>A Db-radial plot maps the trace's X axis onto the compass, so an axis that is not an
    /// angle has no meaning there — a frequency sweep would draw a spiral of nothing. Refused by
    /// returning false, which the trace surfaces as <c>&lt;invalid&gt;</c> rather than drawing a
    /// plausible shape.</para>
    /// </summary>
    public static bool TryDegreesPerUnit(string axisName, string? axisUnit, out double factor)
    {
        switch (axisUnit)
        {
            case "deg" or "degree" or "degrees" or "°": factor = 1.0;                 return true;
            case "rad" or "radian" or "radians":        factor = 180.0 / Math.PI;     return true;
        }

        // An unstated unit is taken as degrees on an axis whose NAME is an angle — "theta", "phi"
        // and ANT-5's beamwidth "cut" axis. Nothing else: guessing on an unnamed, unitless axis is
        // how a sweep variable ends up drawn as a bearing.
        if (string.IsNullOrEmpty(axisUnit)
            && axisName is "theta" or "phi" or "cut" or "az" or "el")
        {
            factor = 1.0;
            return true;
        }

        factor = double.NaN;
        return false;
    }

    /// <summary>
    /// <b>§4's last statement, built from the cube's own axis and never written as a constant.</b>
    /// ANT-4's θ axis stops at 90° because a laterally infinite ground plane has no field below it,
    /// so a pattern plot occupies part of the disc — and with nothing saying why, that reads as a
    /// rendering fault rather than as the model's own stated limit. <b>When ANT-11 extends θ to
    /// 180° this sentence changes with it</b>, because it is derived from the span that is actually
    /// present.
    /// </summary>
    /// <returns>Null when the axis is not θ — a φ sweep says nothing about hemispheres.</returns>
    public static string? HemisphereNote(string axisName, double firstDeg, double lastDeg)
    {
        if (axisName is not ("theta" or "el")) return null;

        double lo = Math.Min(firstDeg, lastDeg);
        double hi = Math.Max(firstDeg, lastDeg);
        string span = $"θ {Fmt(lo)}…{Fmt(hi)}°";

        return hi <= 90.0 + 1e-9
            ? $"{span} — the lower hemisphere is not modelled (the ground plane is laterally infinite, "
              + "so there is no field below it), not measured as empty."
            : $"{span} — both hemispheres are modelled.";

        static string Fmt(double v) =>
            Math.Abs(v - Math.Round(v)) < 5e-4
                ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// <b>ANT-7 §4 — what a pattern plot must state, as the lines it states them on.</b>
///
/// <para>An antenna pattern carries more context than an S-parameter trace and losing it makes the
/// picture unfalsifiable. Two of §4's five items are NOT here, and deliberately: the cut, the driven
/// port and the frequency are the trace's own PINNED AXES, and
/// <see cref="TraceLabeler.ComputeMinimalLabels"/> already puts every one of them in the label strip
/// beside the plot ("U(freq=5e+09 Hz,phi=0 deg,port=1) dB"). Restating them here would be a second
/// copy of one fact, and the two would drift.</para>
///
/// <para>What is left is the two things nothing else says: <b>whether the radius is normalised or
/// absolute</b>, and <b>what the θ range means</b>.</para>
/// </summary>
public static class PatternCaption
{
    /// <summary>
    /// The lines drawn under a pattern plot, in order. Empty for every other plot, so the caller can
    /// ask unconditionally.
    /// </summary>
    public static IReadOnlyList<string> Lines(Plot plot)
    {
        if (plot is null || !plot.IsPolarPattern || plot.PatternScale is not { } scale)
            return Array.Empty<string>();

        var lines = new List<string>(3)
        {
            // §6: "Do not draw an unlabelled normalised pattern." This line is why.
            scale.ReferenceCaption() + "  ·  0° at top, clockwise",
        };

        // The hemisphere statement, per DISTINCT θ range present — normally one. Built from the
        // axis, so ANT-11's extension to 180° changes it with no edit here.
        foreach (var t in plot.Traces)
        {
            if (t.IsContourTrace || t.IsSummaryColumn) continue;
            if (t.CubeXValues is not { Count: > 0 } xs) continue;
            if (PolarPatternAngle.HemisphereNote(t.CubeXAxisName, xs[0], xs[^1]) is not { } note) continue;
            if (!lines.Contains(note)) lines.Add(note);
        }

        return lines;
    }
}
