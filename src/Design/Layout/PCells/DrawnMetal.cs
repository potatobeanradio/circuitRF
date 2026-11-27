// Owner: PCD-3 (brief-pcd-0 §5, seam 11; written by PCD-1's seams commit). Framework-free.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>brief-pcd-0 §4.3: the drawn-metal primitive MSHAPE, its parameter names and Stretch by Parameter.</summary>
public static class DrawnMetal
{
    public const string GeneratorId = "MSHAPE";
    public const string Form = "Form", FormPolygon = "polygon", FormPath = "path", Width = "W";
    public const string Model = "Model", ModelNone = "none", ModelOpen = "open", ModelShort = "short";
    public const string SignalLayer = "SignalLayer", GroundReference = "GroundReference";
    public static string X(int i)      => $"X[{i}]";
    public static string Y(int i)      => $"Y[{i}]";
    public static string PinX(int k)   => $"PinX[{k}]";
    public static string PinY(int k)   => $"PinY[{k}]";
    public static string PinW(int k)   => $"PinW[{k}]";
    public static string PinDir(int k) => $"PinDir[{k}]";

    public static bool Is(EditableComponent c) => c.Symbol == SymbolKind.MShape;

    /// <summary>The words among MSHAPE's parameters, <see cref="Form"/> and <see cref="Model"/>: text for the generator, never
    /// an expression to evaluate (brief-pcd-3 D1). What a reader of its parameters hands on as text.</summary>
    public static bool IsWord(string parameterName) => parameterName is Form or Model;

    /// <summary>A copy of shape whose Moving vertices follow binding.Parameter along binding.AxisDeg from binding.Anchor:
    /// unchanged at the current values, moved by δ when the parameter grows by δ; binding appended to Stretch.</summary>
    /// <remarks>brief-pcd-3 D3: each moving <c>X[i]</c> gains <c>(P − P0)·cos θ</c> and <c>Y[i]</c> <c>(P − P0)·sin θ</c>, P0
    /// the parameter's value in <paramref name="scope"/> now, written in the vertex's unit when P carries one. An unknown
    /// vertex, an anchor that also moves, or a parameter that does not evaluate is refused.</remarks>
    public static EditableComponent Stretch(EditableComponent shape, StretchBinding binding, Scope scope, Evaluator evaluator)
    {
        if (!Is(shape)) throw new ArgumentException($"{shape.InstanceName} is not drawn metal (MSHAPE).", nameof(shape));
        if (binding.Moving.Length == 0) throw new ArgumentException("Stretch moves no vertex.", nameof(binding));
        if (binding.Moving.Contains(binding.Anchor))
            throw new ArgumentException($"Vertex {binding.Anchor} is the anchor and cannot also move.", nameof(binding));

        var copy = shape.Clone();
        EditableParameter Row(string name) => copy.Parameters.FirstOrDefault(p => p.Name == name)
            ?? throw new ArgumentException($"{shape.InstanceName} has no {name}.", nameof(binding));
        Row(X(binding.Anchor));

        bool unitBearing = Evaluator.IsUnitBearing(binding.Parameter, scope);
        double now;
        try { now = evaluator.Eval(binding.Parameter, scope).AsReal(); }
        catch (Exception e) when (e is ExpressionException or InvalidOperationException)
        {
            throw new ArgumentException($"{binding.Parameter} does not evaluate: {e.Message}", nameof(binding), e);
        }

        double rad = binding.AxisDeg * Math.PI / 180.0;
        double cos = Math.Round(Math.Cos(rad), 12), sin = Math.Round(Math.Sin(rad), 12);
        foreach (int i in binding.Moving)
        {
            var x = Row(X(i));
            var y = Row(Y(i));
            x.Expression = Moved(x, cos);
            y.Expression = Moved(y, sin);
        }
        copy.Stretch = [.. shape.Stretch ?? [], binding];
        return copy;

        // The row's expression plus (P − P0)·along, P0 in the row's own unit when P carries one (var-unit-wins).
        string Moved(EditableParameter row, double along)
        {
            if (along == 0) return row.Expression;
            string p0 = Number(now);
            if (unitBearing && UnitNormalizer.ToEngineUnit(row.Unit) is { Length: > 0 } unit)
                p0 = Number(now / evaluator.Eval("1", scope, unit).AsReal()) + unit;
            string delta = now == 0 ? binding.Parameter : $"({binding.Parameter} - {p0})";
            string term = Math.Abs(along) == 1 ? delta : $"{delta}*{Number(Math.Abs(along))}";
            string old = row.Expression.Trim();
            if (old is "0" or "") return along < 0 ? "-" + term : term;
            return (Simple.IsMatch(old) ? old : $"({old})") + (along < 0 ? " - " : " + ") + term;
        }
    }

    private static readonly Regex Simple = new(@"^-?[A-Za-z0-9_.]+$", RegexOptions.Compiled);

    private static string Number(double v) => v.ToString("G12", CultureInfo.InvariantCulture);
}
