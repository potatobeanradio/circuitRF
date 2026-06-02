namespace CircuitRF.Engine;

/// <summary>
/// Thrown by the nonlinear-DC Newton solver when <see cref="DcBiasSteppingMode.Never"/> is set
/// and the direct solve does not converge within the max-iteration cap.
/// Reports the final residual norm and iteration count for diagnostics.
/// </summary>
public sealed class NonlinearDcNotConvergedException : Exception
{
    public int    Iterations     { get; }
    public double FinalResidual  { get; }

    public NonlinearDcNotConvergedException(int iterations, double finalResidual)
        : base($"Nonlinear-DC solver did not converge after {iterations} Newton iterations " +
               $"(final ‖F‖ = {finalResidual:G4}). " +
               $"Use DcBiasStepping=IfNecessary or Always to enable source-stepping continuation, " +
               $"or increase NonlinearMaxIter.")
    {
        Iterations    = iterations;
        FinalResidual = finalResidual;
    }
}
