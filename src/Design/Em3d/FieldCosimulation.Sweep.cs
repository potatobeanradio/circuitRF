// brief-cosim-pattern-5 — a parametric sweep around a fields analysis's far field. A point is one circuit solve and a sum, so a sweep is that many
// sums stacked on an axis: the engine runs the points (ParametricSweepEngine, through its runOtherInner) and what a point does is here, since the
// far field is read in this layer. An analysis that would run a field set is refused: its volumes are written per run and its cube average is a long solve per run.
// A result holding a field set beside its far field sweeps wherever the analysis takes the far field alone: at freq=all, or at a frequency with no field set.
//
// What a run says is per sweep, not per point: a point's numbers (|Γ|, the power checks) would be the first point's alone once the diagnostics merge, so
// the notes describe the sweep, and a warning names the point it is about.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>R-cp5-4 — the memory a far field's cubes take under a sweep, known before it runs.</summary>
public sealed record FieldsSweepSize(int Directions, int Frequencies, long Points)
{
    /// <summary>What one direction costs across the pattern's cubes at one frequency and one sweep point, with the Ludwig-3 pair: E_θ, E_φ, U, the axial ratio, its sense and the pair.</summary>
    public const int BytesPerDirection = 72;

    /// <summary>Above this a run warns before it starts.</summary>
    public const long WarnAboveBytes = 2_000_000_000;

    public double Bytes => (double)Directions * Frequencies * Points * BytesPerDirection;

    public string Amount => Bytes >= 1e9
        ? (Bytes / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : (Bytes / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    public string Sentence =>
        $"about {Amount} of pattern cubes ({Directions.ToString("N0", CultureInfo.InvariantCulture)} directions x {Frequencies} " +
        $"{(Frequencies == 1 ? "frequency" : "frequencies")} x {Points.ToString("N0", CultureInfo.InvariantCulture)} {(Points == 1 ? "point" : "points")})";
}

public static partial class FieldCosimulation
{
    /// <summary><see cref="PlanOf"/>, refusing a plan that would run the field set under a sweep.</summary>
    private static FieldsPlan SweepPlanOf(FieldsAnalysis fa, string recordPath, double hz, string who, out PortFarFieldSet? far)
    {
        var plan = PlanOf(fa, recordPath, hz, who, out far);
        if (!plan.FieldSet) return plan;
        string remedy = far is null ? "" : ", or use freq=all, which takes the far field alone";
        throw new InvalidOperationException(
            $"{who}: a parametric sweep wraps a far field alone, and at this frequency '{Path.GetFileName(recordPath)}' holds a field set. Its SAR and B1 volumes are written per run and " +
            $"its cube average is a long solve per run, so neither is repeated per sweep point. Change a value with a tuning slider or --set{remedy}.");
    }

    /// <summary>
    /// R-cp5-2 / R-cp5-4 — whether a sweep can wrap <paramref name="fa"/> and what its cubes will take; throws the sentence a run would refuse with.
    /// One function for the run, its summary and <c>explain --analysis</c>, so they cannot disagree.
    /// </summary>
    public static FieldsSweepSize PlanSweep(FieldsAnalysis fa, ElaboratedNetlist nl, long points)
    {
        string who = $"Fields '{fa.Name}'";
        var (_, recordPath, hz, _) = Prepare(fa, nl, who);
        var plan = SweepPlanOf(fa, recordPath, hz, who, out var far);
        return new FieldsSweepSize(far!.Grid.DirectionCount, plan.FarField.Count, points);
    }

    /// <summary>R-cp5-4 — the warning for a sweep whose cubes would pass <see cref="FieldsSweepSize.WarnAboveBytes"/>; null below it.</summary>
    public static string? SweepSizeWarning(FieldsAnalysis fa, FieldsSweepSize size)
    {
        if (size.Bytes <= FieldsSweepSize.WarnAboveBytes) return null;
        string remedy = fa.AtEveryFrequency && size.Frequencies > 1
            ? $"Name one frequency with freq= rather than freq=all, which takes it to {(size with { Frequencies = 1 }).Amount}."
            : "This is one frequency per point, the least freq= can ask for, so fewer sweep points are what reduces it.";
        return $"Fields '{fa.Name}' under a parametric sweep forms {size.Sentence} in memory. {remedy}";
    }

    /// <summary>
    /// R-cp5-1 — the engine's inner runner for a sweep around a fields analysis. A value that moves <c>freq=</c> fails its point: a stacked cube has one
    /// frequency axis, and a second frequency under it would be labelled as the first.
    /// </summary>
    public static Func<Analysis, ElaboratedNetlist, CircuitRF.Engine.RunControl?, DataSet> SweepPoints(FieldsOutput output)
    {
        double? first = null;
        return (inner, nl, control) =>
        {
            var fa = (FieldsAnalysis)inner;
            string who = $"Fields '{fa.Name}'";
            if (!fa.AtEveryFrequency)
            {
                double hz = Prepare(fa, nl, who).Hz;
                first ??= hz;
                if (hz != first)
                    throw new InvalidOperationException($"{who}: this value moves freq= to {MaterialDispersion.Hz(hz)} from {MaterialDispersion.Hz(first.Value)}, and a pattern's cube " +
                                                        "has one frequency axis. Use freq=all for every frequency the result holds.");
            }
            // The sweep engine's own text for this point, nested axes included: it is on the netlist for what a point reports.
            return Run(fa, nl, [], control, null, output, null, null, nl.WireThermal?.Drive ?? "a sweep point");
        };
    }

    /// <summary>What the measurements of a swept analysis read: each refuses, naming the cube that holds the number per point.</summary>
    public static IFieldMetrics SweptMetrics(FieldsAnalysis fa, ParametricSweepAnalysis sweep) => new SweptFieldsMetrics(fa, sweep);

    private sealed class SweptFieldsMetrics(FieldsAnalysis fa, ParametricSweepAnalysis sweep) : IFieldMetrics
    {
        public double Evaluate(FieldMetric metric, string? region, double? massGrams)
            => throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads a field set, and a parametric sweep wraps fields analysis '{fa.Name}' over a far field alone.");

        public double EvaluatePattern(FieldMetric metric, double? thetaDeg, double? phiDeg)
            => throw new ExpressionException($"{metric}({fa.Name}) is one number, and a parametric sweep over {sweep.SweepVarName} wraps fields analysis '{fa.Name}', so it has one per point. " +
                                             $"Plot {fa.Name}.farfield.{metric} over {sweep.SweepVarName}, or read it from a fields analysis no sweep wraps.");
    }

    private static void SweepDiagnostics(FieldsAnalysis fa, ElaboratedNetlist nl, PortFarFieldSet far, int port,
                                         IReadOnlyList<string> planNotes, List<string> warnings, string point)
    {
        nl.AddNoteOnce($"fields:{fa.Name}:sweep",
                       $"Fields '{fa.Name}' at {fa.FrequencyText}: the pattern of '{fa.Block}' with this circuit around it, recombined at each sweep point from its {far.PortCount} ports' " +
                       $"stored patterns and referred to 1 V at circuit port {port}. Gain is per watt accepted and realized gain includes mismatch, as for a 3D run." +
                       (fa.PerAvailable ? " norm=available does not apply to the pattern." : ""));
        foreach (string note in planNotes) nl.AddNoteOnce($"fields:{fa.Name}:{note}", note);
        foreach (string warning in warnings) nl.AddWarningOnce($"fields:{fa.Name}:{point}:{warning}", $"At {point}: {warning}");
    }
}
