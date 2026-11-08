using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A component's parameters cannot be read as the author meant them — two forms stated at once, a
/// key that would be ignored, a value outside its domain. Thrown by <see cref="ComponentModelFactory"/>,
/// which does not know which instance it is building; the elaborator, which does, rethrows it with the
/// instance path in front, so <c>check</c> and a run name the line as well as the keys.
/// </summary>
public sealed class ParameterRefusalException : InvalidOperationException
{
    public ParameterRefusalException(string message) : base(message) { }

    /// <summary>A coded refusal; its message is the diagnostic rendered, so every caller reading the message is unchanged.</summary>
    public ParameterRefusalException(Diagnostic diagnostic) : base(diagnostic.Render()) => Diagnostic = diagnostic;

    /// <summary>The coded refusal, when it was thrown as one.</summary>
    public Diagnostic? Diagnostic { get; }
}
