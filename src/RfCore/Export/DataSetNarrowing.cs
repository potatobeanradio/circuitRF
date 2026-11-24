// ================================================================
//  DataSetNarrowing.cs — asking a result for part of itself.
//
//  AUT-9 R-aut9-9. A 551-point two-port S-parameter run returned every frequency and all 2,204
//  complex entries inline — 173 KB over the wire — to answer what one entry was at one frequency.
//  `--only` and `--group` narrow by cube NAME, which does nothing at all when the result has one
//  cube; there was no way to narrow by AXIS.
//
//  So: `--at freq=2GHz` picks one point along an axis and `--range freq=1GHz:3GHz` keeps a band of
//  it. Both take unit-bearing values, because a bare 2 could be 2 Hz or 2 GHz and one of those runs
//  is a story this repo already has (sweep-unit-scale-and-mark).
//
//  WHAT THIS FILE DOES NOT DO: decide silently. An axis no cube has is a refusal naming the axes
//  that exist, not an empty result; and a selection that had to move to a grid point says which
//  point it moved to, in the document, beside the numbers it produced.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace RfCore.Export
{
    /// <summary>How <c>--at</c> got from the value asked for to the value returned.</summary>
    public static class AxisPickMode
    {
        /// <summary>The nearest grid point. The default: it returns a number the run actually
        /// computed.</summary>
        public const string Nearest = "nearest";

        /// <summary>Linearly interpolated between the two bracketing grid points — a number the run
        /// did not compute, which is why it is never the default and always said.</summary>
        public const string Interpolated = "interpolated";
    }

    /// <summary>One <c>--at</c>, as typed and as resolved.</summary>
    /// <param name="Axis">The axis name, which is what an axis is located BY (a sweep prepends one
    /// axis per nesting level, so a position would name a different axis on a swept run).</param>
    /// <param name="Text">Exactly what the caller wrote, so a refusal can quote it back.</param>
    /// <param name="Interpolate">Whether the caller asked for interpolation rather than the nearest
    /// grid point.</param>
    public sealed record AxisPick(string Axis, string Text, bool Interpolate);

    /// <summary>One <c>--range</c>, as typed.</summary>
    public sealed record AxisSpan(string Axis, string LoText, string HiText);

    /// <summary>
    /// What a narrowing actually did to one axis, reported beside the values it produced so a caller
    /// never has to work out which point it received.
    /// </summary>
    /// <param name="Asked">The value the caller wrote, in the axis's own base unit.</param>
    /// <param name="At">The value returned: a grid point for <c>nearest</c>, the asked value for
    /// <c>interpolated</c>.</param>
    /// <param name="Mode">One of <see cref="AxisPickMode"/>, or <c>range</c> for a span.</param>
    /// <param name="Clamped">True when the asked value fell outside the axis and the ends were used
    /// instead — a statement about the answer, not a note about the request.</param>
    public sealed record NarrowingJson(
        string  Axis,
        string  Unit,
        string  Mode,
        double? Asked,
        double? At,
        double? From,
        double? To,
        int     Length,
        bool    Clamped,
        IReadOnlyList<string> Cubes);

    /// <summary>
    /// Thrown for a narrowing that cannot be honoured, carrying a coded
    /// <see cref="CircuitRF.Diagnostics.Diagnostic"/> rather than a bare sentence — an id, typed
    /// arguments and an English template, which is what a Messages window and a machine caller both
    /// key on. Its <see cref="Exception.Message"/> is the rendered sentence, so anything that only
    /// catches and prints still reads the same words.
    /// </summary>
    public sealed class NarrowingException(Diagnostic diagnostic) : Exception(diagnostic.Render())
    {
        public Diagnostic Diagnostic { get; } = diagnostic;
    }

    /// <summary>
    /// The refusals axis narrowing can produce. The ids are the durable part: reword a template
    /// freely, change an id and you have made a new diagnostic.
    /// </summary>
    public static class NarrowingDiagnostics
    {
        /// <summary>An <c>--at</c> or <c>--range</c> naming an axis no cube in this result has — a
        /// caller's typo, answered with the axes that do exist rather than with the whole
        /// result.</summary>
        public static Diagnostic UnknownAxis(string axis, string known) => Diagnostic.Create(
            "narrow.axis.unknown",
            DiagnosticSeverity.Error,
            "No axis named '{axis}' in this result. It has {known}.",
            ("axis", axis), ("known", known));

        /// <summary>A value that is not one, on an axis that carries a unit or does not.</summary>
        public static Diagnostic ValueMalformed(string text, string axis, string how) => Diagnostic.Create(
            "narrow.value.malformed",
            DiagnosticSeverity.Error,
            "'{text}' is not a value on axis '{axis}' — {how}.",
            ("text", text), ("axis", axis), ("how", how));

        /// <summary>Two <c>--at</c> picks on one axis. A pick reduces its axis to one point, so a second pick on it could
        /// only ever return the first point again — answered as if it were the one asked for. Refused instead.</summary>
        public static Diagnostic AxisPickedTwice(string axis, string first, string second) => Diagnostic.Create(
            "narrow.axis.picked-twice",
            DiagnosticSeverity.Error,
            "--at picks one point per axis, and '{axis}' was picked twice ({first}, then {second}). Ask for each point in a "
          + "call of its own, or keep a band with --range {axis}=lo:hi.",
            ("axis", axis), ("first", first), ("second", second));

        /// <summary>An axis named with no value after it.</summary>
        public static Diagnostic ValueMissing(string axis) => Diagnostic.Create(
            "narrow.value.missing",
            DiagnosticSeverity.Error,
            "Axis '{axis}' was given no value to narrow to.",
            ("axis", axis));

        /// <summary>A range that keeps nothing — reported with what the axis actually spans, since
        /// that is the number a caller needs to write a range that lands.</summary>
        public static Diagnostic RangeEmpty(string axis, string lo, string hi, string span) => Diagnostic.Create(
            "narrow.range.empty",
            DiagnosticSeverity.Error,
            "--range {axis}={lo}:{hi} keeps no points — that axis runs {span}.",
            ("axis", axis), ("lo", lo), ("hi", hi), ("span", span));
    }

    /// <summary>
    /// Parses an axis value that carries its own unit, and applies the resulting selection to a
    /// <see cref="DataSet"/>.
    /// </summary>
    public static class DataSetNarrowing
    {
        // ── parsing ──────────────────────────────────────────────────────────

        private static readonly (string Prefix, double Scale)[] SiPrefixes =
        [
            ("T",  1e12), ("G",  1e9),  ("M",  1e6),  ("k", 1e3), ("K", 1e3),
            ("m",  1e-3), ("u",  1e-6), ("µ", 1e-6), ("μ", 1e-6),
            ("n",  1e-9), ("p",  1e-12), ("f", 1e-15),
        ];

        /// <summary>
        /// A number with an optional SI prefix and an optional unit, in the axis's own base unit.
        ///
        /// <para><b>The axis's unit is stripped FIRST</b>, then the prefix. That is what makes
        /// <c>5mm</c> five millimetres on a metre axis and <c>5m</c> five metres on the same one —
        /// the alternative, reading a trailing <c>m</c> as milli always, silently divides a length
        /// by a thousand. A unit that is present and is not the axis's is a refusal, never a
        /// coincidence.</para>
        /// </summary>
        public static double ParseAxisValue(string text, string axisName, string axisUnit)
        {
            string t = text.Trim();
            if (t.Length == 0) throw new NarrowingException(NarrowingDiagnostics.ValueMissing(axisName));

            // A plain number, including exponent form, is taken as already being in the base unit.
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
                return plain;

            if (!string.IsNullOrEmpty(axisUnit) &&
                t.EndsWith(axisUnit, StringComparison.OrdinalIgnoreCase))
                t = t[..^axisUnit.Length].TrimEnd();

            foreach (var (prefix, scale) in SiPrefixes)
                if (t.EndsWith(prefix, StringComparison.Ordinal))
                {
                    string head = t[..^prefix.Length].Trim();
                    if (double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        return v * scale;
                }

            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare))
                return bare;

            string how = string.IsNullOrEmpty(axisUnit)
                ? "that axis carries no unit, so write a bare number"
                : "write it in " + axisUnit + ", with an optional SI prefix (2" + axisUnit +
                  ", 2G" + axisUnit + ")";
            throw new NarrowingException(NarrowingDiagnostics.ValueMalformed(text, axisName, how));
        }

        // ── applying ─────────────────────────────────────────────────────────

        /// <summary>
        /// Narrows every cube that HAS one of the named axes, and leaves alone every cube that does
        /// not — a <c>Z0</c> cube has no frequency axis and narrowing by frequency is not a claim
        /// about it. An axis NO cube has is a refusal: it is a caller's typo, and answering it with
        /// the whole result would be answering a different question.
        /// </summary>
        public static (DataSet Data, IReadOnlyList<NarrowingJson> Applied) Apply(
            DataSet ds, IReadOnlyList<AxisPick> picks, IReadOnlyList<AxisSpan> spans)
        {
            if (picks.Count == 0 && spans.Count == 0) return (ds, []);

            var known = AxisNames(ds);
            foreach (string axis in picks.Select(p => p.Axis).Concat(spans.Select(s => s.Axis)))
                if (!known.Contains(axis))
                    throw new NarrowingException(NarrowingDiagnostics.UnknownAxis(
                        axis, known.Count == 0 ? "no axes at all" : string.Join(", ", known)));

            var pickedAxes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pick in picks)
                if (!pickedAxes.TryAdd(pick.Axis, pick.Text))
                    throw new NarrowingException(NarrowingDiagnostics.AxisPickedTwice(pick.Axis, pickedAxes[pick.Axis], pick.Text));

            var applied = new List<NarrowingJson>();
            var outp    = new DataSet();

            // One report per axis, not one per cube: the caller asked one question about the axis
            // and the cubes that carry it all answered it the same way.
            var reportedPick = new Dictionary<string, (double Asked, double At, bool Clamped, List<string> Cubes)>(StringComparer.Ordinal);
            var reportedSpan = new Dictionary<string, (double From, double To, int Length, List<string> Cubes)>(StringComparer.Ordinal);
            var axisUnits    = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string group in ds.Groups)
            {
                foreach (var (name, cube) in ds.CubesIn(group))
                {
                    var current = cube;

                    foreach (var span in spans)
                    {
                        int dim = Dim(current, span.Axis);
                        if (dim < 0) continue;

                        var ax = current.Axes[dim];
                        axisUnits[span.Axis] = ax.Unit;
                        double lo = ParseAxisValue(span.LoText, span.Axis, ax.Unit);
                        double hi = ParseAxisValue(span.HiText, span.Axis, ax.Unit);
                        if (hi < lo) (lo, hi) = (hi, lo);

                        var keep = IndicesWithin(ax, lo, hi);
                        if (keep.Count == 0)
                            throw new NarrowingException(NarrowingDiagnostics.RangeEmpty(
                                span.Axis, span.LoText, span.HiText,
                                $"{Fmt(ax.Values[0])} to {Fmt(ax.Values[^1])} {ax.Unit}".TrimEnd()));

                        current = PinRange(current, dim, keep[0], keep[^1] + 1);
                        var newAx = current.Axes[dim];
                        var slot  = reportedSpan.TryGetValue(span.Axis, out var s0)
                            ? s0 : (From: newAx.Values[0], To: newAx.Values[^1], Length: newAx.Length, Cubes: new List<string>());
                        slot.Cubes.Add(Qualified(group, name));
                        reportedSpan[span.Axis] = slot;
                    }

                    foreach (var pick in picks)
                    {
                        int dim = Dim(current, pick.Axis);
                        if (dim < 0) continue;

                        var ax = current.Axes[dim];
                        axisUnits[pick.Axis] = ax.Unit;
                        double asked = ParseAxisValue(pick.Text, pick.Axis, ax.Unit);

                        (current, double at, bool clamped) = pick.Interpolate
                            ? Interpolate(current, dim, asked)
                            : Nearest(current, dim, asked);

                        var slot = reportedPick.TryGetValue(pick.Axis, out var p0)
                            ? p0 : (Asked: asked, At: at, Clamped: clamped, Cubes: new List<string>());
                        slot.Cubes.Add(Qualified(group, name));
                        reportedPick[pick.Axis] = slot;
                    }

                    outp.AddToGroup(group, name, current);
                }
            }

            foreach (var span in spans)
                if (reportedSpan.TryGetValue(span.Axis, out var r))
                    applied.Add(new NarrowingJson(
                        span.Axis, axisUnits.GetValueOrDefault(span.Axis, ""), "range",
                        null, null, r.From, r.To, r.Length, false, r.Cubes));

            foreach (var pick in picks)
                if (reportedPick.TryGetValue(pick.Axis, out var r))
                    applied.Add(new NarrowingJson(
                        pick.Axis, axisUnits.GetValueOrDefault(pick.Axis, ""),
                        pick.Interpolate ? AxisPickMode.Interpolated : AxisPickMode.Nearest,
                        r.Asked, r.At, null, null, 1, r.Clamped, r.Cubes));

            return (outp, applied);
        }

        /// <summary>Every axis name in the set, in the order first met. What a refusal lists.</summary>
        public static IReadOnlyList<string> AxisNames(DataSet ds)
        {
            var seen = new List<string>();
            foreach (string g in ds.Groups)
                foreach (var (_, cube) in ds.CubesIn(g))
                    foreach (var ax in cube.Axes)
                        if (!seen.Contains(ax.Name, StringComparer.Ordinal)) seen.Add(ax.Name);
            return seen;
        }

        // ── the mechanics ────────────────────────────────────────────────────

        private static string Qualified(string group, string name)
            => group.Length == 0 ? name : group + "." + name;

        private static int Dim(DataCube c, string axis)
        {
            for (int d = 0; d < c.Axes.Count; d++)
                if (string.Equals(c.Axes[d].Name, axis, StringComparison.Ordinal)) return d;
            return -1;
        }

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

        private static List<int> IndicesWithin(Axis ax, double lo, double hi)
        {
            var keep = new List<int>();
            for (int i = 0; i < ax.Length; i++)
                if (ax.Values[i] >= lo && ax.Values[i] <= hi) keep.Add(i);
            return keep;
        }

        /// <summary>The cube with <paramref name="dim"/> narrowed to <c>[lo, hi)</c> and every other
        /// axis kept whole. The axis SURVIVES even at length 1 — collapsing it would take the value
        /// the caller picked out of the document with it.</summary>
        private static DataCube PinRange(DataCube c, int dim, int lo, int hi)
        {
            var args = new object[c.Rank];
            for (int d = 0; d < c.Rank; d++) args[d] = d == dim ? new Range(lo, hi) : Range.All;
            return c.Slice(args).Cube!;
        }

        private static (DataCube, double, bool) Nearest(DataCube c, int dim, double asked)
        {
            var ax = c.Axes[dim];
            int best = 0;
            double bestD = Math.Abs(ax.Values[0] - asked);
            for (int i = 1; i < ax.Length; i++)
            {
                double d = Math.Abs(ax.Values[i] - asked);
                if (d < bestD) { bestD = d; best = i; }
            }
            bool clamped = asked < Math.Min(ax.Values[0], ax.Values[^1])
                        || asked > Math.Max(ax.Values[0], ax.Values[^1]);
            return (PinRange(c, dim, best, best + 1), ax.Values[best], clamped);
        }

        /// <summary>
        /// Linear interpolation between the two bracketing points, with the axis rewritten to the
        /// value asked for — so the returned cube says where it is rather than where its lower
        /// neighbour was. Complex cubes interpolate real and imaginary parts, which is what a linear
        /// interpolation of a complex quantity IS; nothing here interpolates a magnitude and a phase
        /// separately, because that is a different (and around a wrap, wrong) answer.
        /// </summary>
        private static (DataCube, double, bool) Interpolate(DataCube c, int dim, double asked)
        {
            var ax = c.Axes[dim];
            if (ax.Length == 1) return (PinRange(c, dim, 0, 1), ax.Values[0], true);

            // Bracketing indices on an axis assumed monotone, which every swept axis is.
            bool ascending = ax.Values[^1] >= ax.Values[0];
            int lo = 0;
            for (int i = 0; i < ax.Length - 1; i++)
            {
                bool inside = ascending
                    ? asked >= ax.Values[i] && asked <= ax.Values[i + 1]
                    : asked <= ax.Values[i] && asked >= ax.Values[i + 1];
                if (inside) { lo = i; break; }
                lo = i;   // falls through to the last interval when asked is past the end
            }
            int hi = lo + 1;

            double span = ax.Values[hi] - ax.Values[lo];
            double w    = Math.Abs(span) < double.Epsilon ? 0.0 : (asked - ax.Values[lo]) / span;
            bool clamped = w < 0 || w > 1;
            w = Math.Clamp(w, 0.0, 1.0);
            double at = clamped ? ax.Values[lo] + w * span : asked;

            var loCube = PinRange(c, dim, lo, lo + 1);
            var hiCube = PinRange(c, dim, hi, hi + 1);

            var axes = loCube.Axes.ToArray();
            axes[dim] = new Axis(ax.Name, [at], ax.Unit,
                                 ax.Labels is null ? null : [ax.Labels[w < 0.5 ? lo : hi]]);

            DataCube result;
            if (c.DataKind == DataKind.Complex)
            {
                var a = loCube.ComplexValues;
                var b = hiCube.ComplexValues;
                var v = new Complex[a.Length];
                for (int i = 0; i < a.Length; i++) v[i] = a[i] * (1 - w) + b[i] * w;
                result = new DataCube(axes, v);
            }
            else
            {
                var a = loCube.RealValues;
                var b = hiCube.RealValues;
                var v = new double[a.Length];
                for (int i = 0; i < a.Length; i++) v[i] = a[i] * (1 - w) + b[i] * w;
                result = new DataCube(axes, v);
            }
            result.Unit = c.Unit;
            return (result, at, clamped);
        }
    }
}
