namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// HB convergence trace — per-iteration and per-continuation-step diagnostics.
/// Mirrors the Phase-3 ConvergenceTrace pattern, extended to the HB power sweep.
/// Primary diagnostic: if Hero 2 doesn't converge, report this trace rather than grinding.
/// </summary>
public sealed class HbConvergenceTrace
{
    public record IterRecord(int Iter, double ResidualNorm);
    public record StepRecord(double Pin_dBm, int Iterations, bool Converged,
        IReadOnlyList<IterRecord> IterTrace);

    private readonly List<StepRecord> _steps = [];
    public IReadOnlyList<StepRecord> Steps => _steps;

    internal void AddStep(StepRecord s) => _steps.Add(s);
    public int TotalSteps      => _steps.Count;
    public int TotalIterations => _steps.Sum(s => s.Iterations);

    /// <summary>Write a compact table to the given writer (stderr by default).</summary>
    public void Print(TextWriter? writer = null)
    {
        writer ??= Console.Error;
        writer.WriteLine("[HB trace] Pin_dBm  Iters  Converged  FinalResidual");
        foreach (var s in _steps)
        {
            double finalRes = s.IterTrace.Count > 0 ? s.IterTrace[^1].ResidualNorm : double.NaN;
            writer.WriteLine(
                $"[HB trace] {s.Pin_dBm,8:F1}  {s.Iterations,5}  " +
                $"{(s.Converged ? "YES" : " NO")}       {finalRes:E3}");
        }
    }
}
