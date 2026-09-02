using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// A single flattened, resolved primitive component in the elaborated netlist.
/// InstancePath is the full dot-separated path from the top ("X1.R1").
/// Parameters are fully resolved to kinded Real/Complex values; units applied.
///
/// <para><b>Stamp and evaluate through the component, not through its model.</b>
/// <see cref="Stamp"/>, <see cref="StampLinearized"/> and <see cref="Evaluate"/> are the seam every
/// engine goes through, and they exist so that <see cref="Multiplicity"/> is applied in exactly one
/// place. Calling <c>ec.Model.Stamp(...)</c> directly bypasses it and silently simulates one device
/// where the netlist asked for several.</para>
/// </summary>
public sealed class ElaboratedComponent(
    string componentType,
    string instancePath,
    int[] nodes,
    IReadOnlyDictionary<string, Value> parameters,
    ComponentModel model)
{
    public string     ComponentType { get; } = componentType;
    public string     InstancePath  { get; } = instancePath;
    public int[]      Nodes         { get; } = nodes;
    public int        ReferenceNode { get; init; } = 0;

    /// <summary>Fully resolved parameter values; each is Real or Complex, units applied.</summary>
    public IReadOnlyDictionary<string, Value> Parameters { get; } = parameters;

    public ComponentModel Model       { get; } = model;
    public bool           IsNonlinear => Model.Kind == ModelKind.Nonlinear;

    /// <summary>
    /// How many identical copies of this component are in parallel — the netlist's <c>m</c>. Exactly
    /// 1 for an ordinary component, and the multiplier path is skipped entirely at that value, so a
    /// design that never states one is bit-identical to before this existed.
    /// </summary>
    public double Multiplicity { get; init; } = 1.0;

    private bool HasMultiplier => Multiplicity != 1.0;

    // ── the one seam ──────────────────────────────────────────────────────────

    /// <summary>Linear contribution, with the device multiplier applied.</summary>
    public void Stamp(IMnaContext mna, double omega)
        => Model.Stamp(HasMultiplier ? Wrap(mna) : mna, this, omega);

    /// <summary>A nonlinear device's contribution linearised about a bias, multiplier applied.</summary>
    public void StampLinearized(IMnaContext mna, double omega, in PortVoltages bias)
        => Model.StampLinearized(HasMultiplier ? Wrap(mna) : mna, this, omega, bias);

    /// <summary>Nonlinear evaluation, with the device multiplier applied to all four blocks.</summary>
    public NonlinearResult Evaluate(in PortVoltages v)
        => Scale(Model.Evaluate(v));

    /// <inheritdoc cref="Evaluate(in PortVoltages)"/>
    public NonlinearResult Evaluate(in PortVoltages v, in ControlCurrents c)
        => Scale(Model.Evaluate(v, c));

    /// <inheritdoc cref="ComponentModel.PrefersBatchEvaluate"/>
    public bool PrefersBatchEvaluate => Model.PrefersBatchEvaluate;

    /// <summary>
    /// Batched nonlinear evaluation, with the device multiplier applied to every point — the same
    /// seam as <see cref="Evaluate(in PortVoltages)"/>, for the same reason.
    /// </summary>
    public IReadOnlyList<NonlinearResult> EvaluateBatch(double[][] portVoltages)
    {
        var raw = Model.EvaluateBatch(portVoltages);
        if (!HasMultiplier) return raw;

        var scaled = new NonlinearResult[raw.Count];
        for (int k = 0; k < scaled.Length; k++) scaled[k] = Scale(raw[k]);
        return scaled;
    }

    /// <inheritdoc cref="ComponentModel.PrefersGridEvaluate"/>
    public bool PrefersGridEvaluate => Model.PrefersGridEvaluate;

    /// <summary>
    /// Whole-grid nonlinear evaluation, with the device multiplier applied to every block at every
    /// sample — the same seam as <see cref="Evaluate(in PortVoltages)"/>, for the same reason. The
    /// scaling is done in place, so the multiplier costs no allocation here either.
    /// </summary>
    public void EvaluateGrid(ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
                             int sampleCount, GridResult into)
    {
        Model.EvaluateGrid(portVoltages, controlCurrents, sampleCount, into);
        if (!HasMultiplier) return;

        double m = Multiplicity;
        int P = into.PortCount, C = into.ControlCount, S = sampleCount;
        ScaleRun(into.I, P * S, m);
        ScaleRun(into.Q, P * S, m);
        ScaleRun(into.Dg, P * P * S, m);
        ScaleRun(into.Dc, P * P * S, m);
        ScaleRun(into.DControl, P * C * S, m);
        ScaleRun(into.DControlCharge, P * C * S, m);
        foreach (var term in into.LiveTerms)
        {
            ScaleRun(term.Value, P * S, m);
            ScaleRun(term.Jac, P * P * S, m);
            ScaleRun(term.JacCtrl, P * C * S, m);
        }
    }

    private static void ScaleRun(double[] buf, int count, double m)
    {
        for (int k = 0; k < count; k++) buf[k] *= m;
    }

    private IMnaContext Wrap(IMnaContext mna) => new MultipliedMnaContext(mna, Multiplicity, InstancePath);

    /// <summary>
    /// Scales a nonlinear result. All four blocks scale by the same factor, because m copies across
    /// the same port voltages carry m times the current and m times the charge, and their
    /// derivatives follow — scaling the currents and forgetting the Jacobian would converge, slowly,
    /// to the right answer for the wrong reason, and would be wrong outright at AC.
    /// </summary>
    private NonlinearResult Scale(in NonlinearResult r)
    {
        if (!HasMultiplier) return r;

        double m = Multiplicity;

        double[] i = new double[r.I.Length];
        double[] q = new double[r.Q.Length];
        for (int k = 0; k < i.Length; k++) i[k] = r.I[k] * m;
        for (int k = 0; k < q.Length; k++) q[k] = r.Q[k] * m;

        // The BRANCH block is carried through UNSCALED, and it is unreachable with m ≠ 1 anyway:
        // a branch equation costs a branch-current unknown, and MultipliedMnaContext refuses to
        // allocate one — m ideal voltage sources in parallel is not a circuit. Multiplying a
        // voltage CONSTRAINT by the number of copies would be meaningless even if it were: m
        // identical sources hold the same voltage as one, and share the current.
        return new NonlinearResult(i, q, ScaleMatrix(r.Dg, m)!, ScaleMatrix(r.Dc, m)!,
                                   ScaleTerms(r.Terms, m),
                                   ScaleMatrix(r.DControl, m), ScaleMatrix(r.DControlCharge, m),
                                   r.BranchResidual, r.DBranchV, r.DBranchC);
    }

    private static double[,]? ScaleMatrix(double[,]? a, double m)
    {
        if (a is null) return null;

        int rows = a.GetLength(0), cols = a.GetLength(1);
        var s = new double[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                s[r, c] = a[r, c] * m;
        return s;
    }

    private static IReadOnlyList<WeightedTerm> ScaleTerms(IReadOnlyList<WeightedTerm> terms, double m)
    {
        if (terms.Count == 0) return terms;

        var scaled = new WeightedTerm[terms.Count];
        for (int k = 0; k < terms.Count; k++)
        {
            var t = terms[k];
            var value = new double[t.Value.Length];
            for (int p = 0; p < value.Length; p++) value[p] = t.Value[p] * m;
            scaled[k] = new WeightedTerm(t.W, value, ScaleMatrix(t.Jac, m)!, ScaleMatrix(t.JacCtrl, m));
        }
        return scaled;
    }
}
