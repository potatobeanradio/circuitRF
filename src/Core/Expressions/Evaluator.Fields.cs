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
        if (FieldMetricFunctions.IsArrayMetric(cl.Name)) return EvalArrayMetric(cl, scope);
        if (cl.Name == "ActiveGamma") return EvalActiveGamma(cl, scope);

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

    /// <summary>brief-paa-3 R-paa3-6 — <c>HpbwDeg(F, plane)</c>, <c>SllDb(F)</c>, <c>PointingErrorDeg(F, theta0, phi0)</c>, <c>ScanLossDb(F)</c>,
    /// <c>GratingMarginDeg(F)</c>, <c>PowerBalance(F)</c>, <c>ActiveVswrMax(F)</c>.</summary>
    private Value EvalArrayMetric(CallExpr cl, Scope scope)
    {
        var (metric, arity) = cl.Name switch
        {
            "HpbwDeg"          => (FieldMetric.HpbwDeg, 2),
            "SllDb"            => (FieldMetric.SllDb, 1),
            "PointingErrorDeg" => (FieldMetric.PointingErrorDeg, 3),
            "ScanLossDb"       => (FieldMetric.ScanLossDb, 1),
            "GratingMarginDeg" => (FieldMetric.GratingMarginDeg, 1),
            "PowerBalance"     => (FieldMetric.PowerBalance, 1),
            "ActiveVswrMax"    => (FieldMetric.ActiveVswrMax, 1),
            "BeamNfDb"         => (FieldMetric.BeamNfDb, 1),
            _                  => throw new UnknownFunctionException(cl.Name),
        };
        if (cl.Args.Length != arity) throw new ArityException(cl.Name, arity, cl.Args.Length);
        if (_ctx is null)
            throw new ExpressionException($"{cl.Name}() reads a fields analysis's result, so it belongs in a measure or goal line.");
        string analysis = FieldsAnalysisNamed(cl);
        string? plane = null;
        if (metric == FieldMetric.HpbwDeg)
        {
            var v = EvalExpr(cl.Args[1], scope);
            plane = v.Kind == ValueKind.String ? v.AsString()
                : throw new TypeErrorException($"HpbwDeg(): the plane is a string, \"elevation\" or \"azimuth\".");
        }
        double? theta = metric == FieldMetric.PointingErrorDeg ? Angle(cl, cl.Args[1], scope) : null;
        double? phi = metric == FieldMetric.PointingErrorDeg ? Angle(cl, cl.Args[2], scope) : null;
        return new Value(_ctx.GetFieldMetrics(analysis).EvaluateArray(metric, plane, theta, phi));
    }

    /// <summary>brief-paa-6 R-paa6-3 — <c>ActiveGamma(F, n)</c>: element n's active reflection, complex.</summary>
    private Value EvalActiveGamma(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
        if (_ctx is null)
            throw new ExpressionException("ActiveGamma() reads a fields analysis's result, so it belongs in a measure or goal line.");
        string analysis = FieldsAnalysisNamed(cl);
        var v = EvalExpr(cl.Args[1], scope);
        double n = v.Kind == ValueKind.Real ? v.AsReal()
            : throw new TypeErrorException("ActiveGamma(): the element is a number, 1 for the first, as ActiveGamma(F1, 3).");
        if (Math.Abs(n - Math.Round(n)) > 1e-9)
            throw new TypeErrorException($"ActiveGamma(): the element is a whole number, and {n} is not.");
        return new Value(_ctx.GetFieldMetrics(analysis).ActiveGamma((int)Math.Round(n)));
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
