// Owner: PCD-2 (brief-pcd-0 §5, seam 9; written by PCD-1's seams commit). Framework-free.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>A pin as placed: position in DBU and its outward direction in degrees.</summary>
public readonly record struct PinPose(long X, long Y, double OutwardDeg);

/// <summary>brief-pcd-0 §4.2: the geometry of an offset placement (brief-pcd-2 R-pcd2-2).</summary>
public static partial class OffsetPlacer
{
    /// <summary>Where the offset part's pin goes and which way it faces, given its anchor pin as placed. Null with refusal
    /// when an expression does not evaluate.</summary>
    public static PinPose? Pose(OffsetPlacement offset, PinPose anchor, Scope scope, Evaluator evaluator,
                                int dbuPerMicron, out string? refusal)
        => At(offset, anchor.X, anchor.Y, anchor.OutwardDeg, scope, evaluator, dbuPerMicron, out refusal) is { } p
            ? new PinPose((long)Math.Round(p.X), (long)Math.Round(p.Y), p.Deg)
            : null;

    /// <summary><see cref="Pose"/> from an exact anchor pin, exact (brief-pcd-2 D5): the planner rounds once, when it
    /// writes the instance.</summary>
    internal static (double X, double Y, double Deg)? At(OffsetPlacement offset, double ax, double ay, double anchorDeg,
                                                         Scope scope, Evaluator evaluator, int dbuPerMicron, out string? refusal)
    {
        string? why = null;
        double perMetre = dbuPerMicron * 1e6;
        refusal = null;
        if (!Eval("Dx", offset.Dx, offset.Unit, out double dx) || !Eval("Dy", offset.Dy, offset.Unit, out double dy)
            || !Eval("Angle", offset.Angle, "deg", out double angle))
        {
            refusal = why;
            return null;
        }
        var (c, s) = LayoutAngle.CosSin(anchorDeg);
        (dx, dy) = (dx * perMetre, dy * perMetre);
        return (ax + dx * c - dy * s, ay + dx * s + dy * c, LayoutAngle.Normalize(anchorDeg + 180.0 + angle));

        bool Eval(string name, string expression, string? unit, out double value)
        {
            if (SchematicLayoutPlanner.TryResolveSiValue(expression, unit, scope, evaluator, out value, out string? error)
                && double.IsFinite(value)) return true;
            why ??= $"{name} = {expression} does not evaluate: {error ?? "not a finite number"}";
            return false;
        }
    }

    /// <summary>The offset after dragging the part's pin from now to (toX, toY): Dx and Dy keep their parameter terms and
    /// gain the change as a literal in offset.Unit (S1: an expression, never a coordinate). brief-pcd-2 D6.</summary>
    public static OffsetPlacement Drag(OffsetPlacement offset, PinPose anchor, PinPose now, long toX, long toY,
                                       int dbuPerMicron)
    {
        var (c, s) = LayoutAngle.CosSin(anchor.OutwardDeg);
        double mx = toX - now.X, my = toY - now.Y;
        double scale = offset.Unit is { Length: > 0 } u && Units.Scale(UnitNormalizer.ToEngineUnit(u)) is { } sc && sc != 0 ? sc : 1.0;
        double dbu = 1e-6 / dbuPerMicron / scale;   // one database unit, in offset.Unit
        double InUnit(double dbuCount) => Math.Round(dbuCount) * dbu;
        return offset with
        {
            Dx = Add(offset.Dx, InUnit(mx * c + my * s), dbu),
            Dy = Add(offset.Dy, InUnit(-mx * s + my * c), dbu),
        };
    }

    /// <summary><paramref name="expression"/> plus <paramref name="delta"/>, folded into a plain number or a trailing
    /// <c>± n</c> where there is one.</summary>
    private static string Add(string expression, double delta, double dbu)
    {
        if (delta == 0) return expression;
        string text = expression.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
            return Fmt(Snap(plain + delta, dbu));
        string head = text;
        double tail = 0;
        if (TrailingLiteral().Match(text) is { Success: true } m)
        {
            head = m.Groups["head"].Value;
            tail = double.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture) * (m.Groups["sign"].Value == "-" ? -1 : 1);
        }
        double sum = Snap(tail + delta, dbu);
        if (head.Length == 0) return Fmt(sum);
        return sum == 0 ? head : $"{head} {(sum < 0 ? "-" : "+")} {Fmt(Math.Abs(sum))}";
    }

    private static double Snap(double v, double dbu) => Math.Round(v / dbu) * dbu;

    private static string Fmt(double v) => (v + 0.0).ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>An expression ending in <c>+ n</c> or <c>- n</c> whose head does not end in an operator (so <c>a * -3</c>
    /// and <c>x^-2</c> are not read as one).</summary>
    [GeneratedRegex(@"^(?<head>.*?[^\s*/^(+\-,])\s*(?<sign>[+\-])\s*(?<num>\d+(\.\d*)?([eE][+\-]?\d+)?)$")]
    private static partial Regex TrailingLiteral();
}
