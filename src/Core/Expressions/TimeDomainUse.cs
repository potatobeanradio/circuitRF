using CircuitRF.Core.Design;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// brief TRN-5 R-trn5-6 — a <c>measure</c> line calling a time-domain function (<see cref="Evaluator.TimeDomainFunctions"/>)
/// over an analysis whose result has no time axis, found by reading the bench, with nothing run. The run refuses the same
/// line with the function's own sentence; this says it first. A result has a time axis when it is a transient's or an
/// envelope's, or a parametric sweep's around one; a measurement named in the waveform argument counts with the analyses it
/// reads in turn.
/// </summary>
public static class TimeDomainUse
{
    /// <summary>One misuse: the measurement, the function, and the analysis it reads that has no time axis.</summary>
    public sealed record Misuse(string Measurement, string Function, string Analysis, string AnalysisKind);

    public static IReadOnlyList<Misuse> Find(TestBench tb)
    {
        var byName = tb.Analyses.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var found = new List<Misuse>();
        var reads = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // measurement → the analyses it reads
        foreach (var m in tb.Measurements)
        {
            Expr ast;
            try { ast = Parser.Parse(m.Expression); }
            catch (ExpressionException) { continue; }   // a line that does not parse is reported by the run as it is
            foreach (var (fn, args) in Calls(ast).Where(c => Evaluator.TimeDomainFunctions.Contains(c.Name)))
            {
                int waveforms = fn == "fft_power" ? 2 : 1;
                foreach (var arg in args.Take(waveforms))
                    foreach (var a in Analyses(arg, byName, reads).Order(StringComparer.Ordinal))
                        if (!HasTime(byName[a], byName, 0))
                            found.Add(new Misuse(m.Name, fn, a, KindOf(byName[a])));
            }
            reads[m.Name] = Analyses(ast, byName, reads);
        }
        return [.. found.Distinct()];
    }

    /// <summary>The declared analyses an expression reads: the prefix of every qualified name (<c>TR1.V</c>), and those of any
    /// earlier measurement it names.</summary>
    private static HashSet<string> Analyses(Expr e, Dictionary<string, Analysis> byName, Dictionary<string, HashSet<string>> reads)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Name(string n)
        {
            int dot = n.IndexOf('.');
            if (dot > 0 && byName.ContainsKey(n[..dot])) set.Add(n[..dot]);
            else if (reads.TryGetValue(n, out var r)) set.UnionWith(r);
        }
        foreach (var r in AstWalker.CollectRefs(e)) Name(r);
        foreach (var (n, _) in Calls(e)) Name(n);
        return set;
    }

    private static IEnumerable<(string Name, Expr[] Args)> Calls(Expr e)
    {
        switch (e)
        {
            case CallExpr cl:
                yield return (cl.Name, cl.Args);
                foreach (var a in cl.Args) foreach (var c in Calls(a)) yield return c;
                break;
            case UnaryExpr u: foreach (var c in Calls(u.Operand)) yield return c; break;
            case BinaryExpr b: foreach (var c in Calls(b.Left).Concat(Calls(b.Right))) yield return c; break;
            case CompareExpr cp: foreach (var c in Calls(cp.Left).Concat(Calls(cp.Right))) yield return c; break;
            case LogicExpr lg: foreach (var c in Calls(lg.Left).Concat(Calls(lg.Right))) yield return c; break;
            case ConditionalExpr cd: foreach (var c in Calls(cd.Condition).Concat(Calls(cd.Then)).Concat(Calls(cd.Else))) yield return c; break;
            case IndexExpr ix: foreach (var c in Calls(ix.Target)) yield return c; break;
        }
    }

    private static bool HasTime(Analysis a, Dictionary<string, Analysis> byName, int depth) => a switch
    {
        TransientAnalysis or EnvelopeAnalysis => true,
        ParametricSweepAnalysis sw => depth < 16 && byName.TryGetValue(sw.InnerAnalysisName, out var inner) && HasTime(inner, byName, depth + 1),
        _ => false,
    };

    private static string KindOf(Analysis a) => a switch
    {
        SParameterAnalysis => "S-parameter",
        DcAnalysis => "DC",
        HarmonicBalanceAnalysis => "harmonic-balance",
        ParametricSweepAnalysis => "parametric sweep",
        _ => a.GetType().Name.Replace("Analysis", "", StringComparison.Ordinal),
    };
}
