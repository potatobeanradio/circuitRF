namespace CircuitRF.Engine;

/// <summary>
/// Thrown when MNA factorization fails.
/// The message always includes structural diagnostics: which rows/nodes are zero
/// or why the singularity was detected.
/// </summary>
public sealed class SingularMatrixException : Exception
{
    public SingularMatrixException(string message)
        : base(message) { }

    public SingularMatrixException(string message, Exception? inner)
        : base(message, inner) { }
}
