namespace CircuitRF.Core.Expressions;

/// <summary>
/// brief-sar-10 R-sar10-1 — the field measurements over a <c>fields</c> analysis (sar.md §12): <c>B1pMean(F, region)</c>,
/// <c>B1pMin(F, region)</c>, <c>B1pCoV(F, region)</c>, <c>psSAR(F, mass)</c>, <c>SARwb(F, region)</c>,
/// <c>SarEff(F, region, mass)</c> and <c>Pabs(F)</c>.
///
/// <para><b>F names the analysis</b>, as <c>SP1</c> does in <c>SP1.S</c>: it is read as a name, never evaluated, so a
/// variable called F1 does not shadow the analysis. A <b>region</b> is a string — a tissue (<c>"brain"</c>, or its whole
/// path <c>"head/brain"</c>), several joined by <c>+</c>, a phantom's name for all its layers, <c>"all"</c>, or a box
/// <c>"box(x0, x1, y0, y1, z0, z1)"</c> with a length unit on each number or none for metres; omitted, the setup's own
/// (Sar.B1Region for B1, Sar.Region for SAR). A <b>mass</b> is in grams. What the region and the normalisation mean is the
/// analysis's (<see cref="IFieldMetrics"/>); nothing here computes a field.</para>
///
/// <para>brief-cosim-pattern-4 R-cp4-4 — the same analysis's far field, each function named after the cube it equals:
/// <c>RealizedGainDbi(F)</c>, <c>GainDbi(F)</c> and <c>DirectivityDbi(F)</c> at the peak, or <c>(F, theta, phi)</c> in that direction in
/// degrees; <c>RadiationEfficiency(F)</c>, <c>TrpDbm(F)</c> and <c>PeakEirpDbm(F)</c>.</para>
/// </summary>
public sealed partial class Evaluator
{
    internal static bool IsFieldMetric(string name) => FieldMetricFunctions.IsFieldMetric(name);

    private Value EvalFieldMetric(CallExpr cl, Scope scope)
    {
        if (FieldMetricFunctions.IsPatternMetric(cl.Name)) return EvalPatternMetric(cl, scope);

        // (metric, smallest arity, largest arity, which argument is the region, which the mass)
        var (metric, lo, hi, regionAt, massAt) = cl.Name switch
        {
            "B1pMean" => (FieldMetric.B1pMean, 1, 2, 1, -1),
            "B1pMin"  => (FieldMetric.B1pMin, 1, 2, 1, -1),
            "B1pCoV"  => (FieldMetric.B1pCoV, 1, 2, 1, -1),
            "psSAR"   => (FieldMetric.PsSar, 2, 2, -1, 1),
            "SARwb"   => (FieldMetric.SarWb, 1, 2, 1, -1),
            "SarEff"  => (FieldMetric.SarEff, 3, 3, 1, 2),
            "Pabs"    => (FieldMetric.Pabs, 1, 1, -1, -1),
            _         => throw new UnknownFunctionException(cl.Name),
        };
        if (cl.Args.Length < lo || cl.Args.Length > hi) throw new ArityException(cl.Name, hi, cl.Args.Length);
        if (_ctx is null)
            throw new ExpressionException($"{cl.Name}() reads a fields analysis's result, so it belongs in a measure or goal line.");

        string analysis = FieldsAnalysisNamed(cl);
        string? region = null;
        if (regionAt >= 0 && regionAt < cl.Args.Length)
        {
            var v = EvalExpr(cl.Args[regionAt], scope);
            region = v.Kind == ValueKind.String ? v.AsString()
                : throw new TypeErrorException($"{cl.Name}(): the region is a string — a tissue (\"brain\"), tissues joined by + " +
                                               "(\"skin+skull\"), \"all\", or \"box(x0, x1, y0, y1, z0, z1)\".");
        }
        double? mass = null;
        if (massAt >= 0)
        {
            var v = EvalExpr(cl.Args[massAt], scope);
            mass = v.Kind == ValueKind.Real ? v.AsReal()
                : throw new TypeErrorException($"{cl.Name}(): the averaging mass is a number of grams, as 10 in {cl.Name}(F1, 10).");
        }
        return new Value(_ctx.GetFieldMetrics(analysis).Evaluate(metric, region, mass));
    }

    private Value EvalPatternMetric(CallExpr cl, Scope scope)
    {
        var (metric, directional) = cl.Name switch
        {
            "RealizedGainDbi"     => (FieldMetric.RealizedGainDbi, true),
            "GainDbi"             => (FieldMetric.GainDbi, true),
            "DirectivityDbi"      => (FieldMetric.DirectivityDbi, true),
            "RadiationEfficiency" => (FieldMetric.RadiationEfficiency, false),
            "TrpDbm"              => (FieldMetric.TrpDbm, false),
            "PeakEirpDbm"         => (FieldMetric.PeakEirpDbm, false),
            _                     => throw new UnknownFunctionException(cl.Name),
        };
        int n = cl.Args.Length;
        if (n != 1 && !(directional && n == 3)) throw new ArityException(cl.Name, directional ? 3 : 1, n);
        if (_ctx is null)
            throw new ExpressionException($"{cl.Name}() reads a fields analysis's result, so it belongs in a measure or goal line.");
        string analysis = FieldsAnalysisNamed(cl);
        double? theta = n == 3 ? Angle(cl, cl.Args[1], scope) : null;
        double? phi = n == 3 ? Angle(cl, cl.Args[2], scope) : null;
        return new Value(_ctx.GetFieldMetrics(analysis).EvaluatePattern(metric, theta, phi));
    }

    private double Angle(CallExpr cl, Expr arg, Scope scope)
    {
        var v = EvalExpr(arg, scope);
        return v.Kind == ValueKind.Real ? v.AsReal()
            : throw new TypeErrorException($"{cl.Name}(): a direction is two numbers of degrees, as {cl.Name}(F1, 30, 90) for theta 30, phi 90.");
    }

    private static string FieldsAnalysisNamed(CallExpr cl) => cl.Args[0] switch
    {
        RefExpr r           => r.Name,
        StringLiteralExpr s => s.Value,
        _ => throw new TypeErrorException($"{cl.Name}(): the first argument names a fields analysis, as F1 in {cl.Name}(F1, …)."),
    };
}
