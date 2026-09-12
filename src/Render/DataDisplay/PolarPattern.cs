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
using RfCore.Data;

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
    /// <b>Why a quantity is greyed in the picker of a dB-radial POLAR plot, or null when it can be
    /// drawn there.</b>
    ///
    /// <para>A pattern plot's compass is the trace's own swept axis, so a cube that is not a
    /// function of DIRECTION has nothing to draw on one: it resolves to
    /// <c>Trace.PatternAxisInvalid</c> and reads "&lt;invalid&gt;" beside a picture with no curve in
    /// it. Most of a far-field run's registry is per-frequency and per-port — the directivity, the
    /// gains, the efficiencies, TRP, peak EIRP — and not one of them has an angle axis, so the
    /// polar plot was offering a long list of quantities it could not draw (owner, 2026-09-11).
    /// That is the same report <see cref="SurfaceResolve.DisabledReasonOn"/> answers for the 3D
    /// surface, and it is answered here the same way rather than differently.</para>
    ///
    /// <para>They are offered DISABLED WITH A REASON rather than dropped, which is this
    /// repository's rule everywhere a plot kind cannot take a quantity: a row that vanishes cannot
    /// be told apart from a quantity the run did not publish, and those are very different
    /// facts.</para>
    ///
    /// <para><paramref name="cube"/> is null for an item that has no cube to test — a derived
    /// network metric, a WSProbe quantity — and the answer for those is the same sentence, because
    /// none of them is a function of direction either.</para>
    /// </summary>
    public static string? DisabledReasonOnPattern(DataCube? cube)
    {
        if (cube is not null && TryFindAngleAxis(cube, out _)) return null;
        return NotOnAPatternRefusal;
    }

    /// <summary>The sentence <see cref="DisabledReasonOnPattern"/> gives. Says what a pattern plot
    /// IS before it says what this quantity is not, and names the plots that draw it perfectly well
    /// — nothing is wrong with the quantity.</summary>
    public const string NotOnAPatternRefusal =
        "A polar pattern plot draws one quantity around its own swept ANGLE — a polar angle (theta " +
        "or el), an azimuth (phi or az) or a cut plane — with the radius in dB. This one is not " +
        "swept over an angle, so there is no cut to draw from it. Put it on a rectangular plot or " +
        "a table, where it reads normally.";

    /// <summary>
    /// The first axis of <paramref name="cube"/> that can be the compass — an ANGLE on
    /// <see cref="TryDegreesPerUnit"/>'s own test, which is the test the trace itself applies to its
    /// X axis, so the picker and the picture cannot disagree about what an angle is.
    ///
    /// <para><b>ONE axis, not two</b> — that is the whole difference from
    /// <see cref="SurfaceResolve.TryFindAngleAxes"/>: a cut is a single plane, and a cube carrying
    /// only θ has a cut in it even though it has no surface. Which of its angle axes the trace
    /// finally sweeps is the slice's business; this asks only whether it has one to sweep.</para>
    ///
    /// <para><b>An angle axis of a single sample does not count</b>, which is the same length rule
    /// the surface finder applies and is here for the same reason: it draws a lone point at one
    /// bearing, and a dot on a disc is not a cut. It is a case a real run reaches — ANT-5's
    /// beamwidth is per-CUT, and a run with one cut plane publishes <c>BeamwidthDeg</c> over a
    /// <c>cut</c> axis of length 1 — so the refusal says "not SWEPT over an angle" rather than "has
    /// no angle axis", which would be untrue of exactly that cube.</para>
    /// </summary>
    internal static bool TryFindAngleAxis(DataCube cube, out int angleDim)
    {
        for (int d = 0; d < cube.Rank; d++)
        {
            var ax = cube.Axes[d];
            if (ax.Length < 2) continue;
            if (!TryDegreesPerUnit(ax.Name, ax.Unit, out _)) continue;
            angleDim = d;
            return true;
        }
        angleDim = -1;
        return false;
    }

    /// <summary>
    /// <b>§4's last statement, built from the cube's own axis and never written as a constant.</b>
    /// ANT-4's θ axis stops at 90° because a laterally infinite ground plane has no field below it,
    /// so a pattern plot occupies part of the disc — and with nothing saying why, that reads as a
    /// rendering fault rather than as the model's own stated limit. <b>When ANT-11 extends θ to
    /// 180° this sentence changes with it</b>, because it is derived from the span that is actually
    /// present.
    ///
    /// <para><b>NOTHING DRAWS IT SINCE 2026-09-11</b> — the owner asked for the whole caption block
    /// under a pattern plot to go, and <see cref="PatternCaption"/> records what that cost. It is
    /// kept, unreferenced, because it is the correct sentence for the fact and the fact has not
    /// changed: a θ axis stopping at 90° still draws a half-disc, and anything that wants to say so
    /// — a run note, a tooltip, an export header — should say it in these words rather than invent a
    /// second phrasing. Delete it only when the model stops having a lower hemisphere it cannot
    /// see.</para>
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
/// <b>What is written under a pattern plot — which, since 2026-09-11, is its ANGLE AXIS and
/// nothing else.</b>
///
/// <para>ANT-7 §4 put five sentences here: whether the radius was normalised or absolute, what the
/// reference was, that 0° is at the top, that a cut is a plane, and what the θ range means. Every
/// one of them is true and none of them is wrong. Read on a real plot they were reported as
/// distracting, on both the polar and the 3D pattern, and on 2026-09-11 all of it was asked to go —
/// with the polar plot to carry its angle axis instead, θ or φ, whichever it is swept in.</para>
///
/// <para>What it cost is worth stating rather than hiding, because §4 was not arbitrary: the
/// reference sentence is the one thing on a NORMALISED pattern that says a 0 dB peak is 0 dB
/// relative to itself. It is not lost — <see cref="PolarPatternScale.ReferenceCaption"/> still
/// composes it and the RINGS still carry their own numbers with the unit on the outer one, which is
/// where a reader looks for a level anyway. The rest was genuinely restating what the label strip
/// beside the plot already says (the cut, the port, the frequency) or what the picture already
/// shows.</para>
///
/// <para>What replaces it is the one thing the picture could NOT say: <b>which angle the compass
/// is</b>. A θ cut and a φ cut are the same disc with the same rings and different meanings, and
/// the axis name was the only sentence here that was not available anywhere else on the plot.</para>
/// </summary>
public static class PatternCaption
{
    /// <summary>
    /// The lines drawn under a pattern plot, in order — at most one, and empty for every plot that
    /// is not a POLAR pattern, so the caller can ask unconditionally.
    ///
    /// <para><b>A 3D surface gets nothing.</b> Its angles are the scene's own drawn axes, which are
    /// labelled in the picture; a line under it would be naming an axis that is not on the bottom
    /// of the plot. The surface renderer still writes the trace's own identity there — that is the
    /// trace LABEL, which a surface has no strip for, not a caption.</para>
    /// </summary>
    public static IReadOnlyList<string> Lines(Plot plot)
    {
        if (plot is null || !plot.IsPolarPattern)
            return Array.Empty<string>();

        foreach (var t in plot.Traces)
        {
            if (t.IsContourTrace || t.IsSummaryColumn) continue;
            if (t.CubeXValues is not { Count: > 0 }) continue;
            if (AngleAxisLabel(t.CubeXAxisName, t.CubeXUnit) is { } label) return [label];
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// <b>"θ (deg)"</b> — the axis's own name in its own symbol, with its own unit. Null when the
    /// swept axis is not an angle at all, which on a dB-radial polar plot is a trace already
    /// refusing to draw (<c>PatternAxisInvalid</c>): naming a bearing it is not drawn in would be a
    /// caption that is confidently wrong, and this is the one line left under the plot.
    /// </summary>
    internal static string? AngleAxisLabel(string axisName, string? axisUnit)
    {
        if (!PolarPatternAngle.TryDegreesPerUnit(axisName, axisUnit, out _)) return null;

        // The unit the numbers around the rim are actually printed in — always degrees, whatever
        // the cube stored. A cut swept in radians is drawn on a compass in degrees, so labelling it
        // "rad" would name a unit that appears nowhere on the picture.
        return $"{AxisSymbols.Display(axisName)} (deg)";
    }
}
