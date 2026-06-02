namespace CircuitRF.Core.Expressions;

/// <summary>
/// Shared warning state for AD domain-error clamp-and-warn (§2.5).
/// The current model name is set by SddEvaluator before evaluation so that
/// domain warnings can name the offending model without threading context everywhere.
/// </summary>
public static class AdWarnings
{
    [ThreadStatic]
    private static string? _currentModel;

    /// <summary>Set by SddEvaluator before each evaluation.</summary>
    public static string? CurrentModel
    {
        get => _currentModel;
        set => _currentModel = value;
    }

    /// <summary>
    /// Emit a domain-clamp warning to Console.Error, naming the model and operation.
    /// The solve continues with the clamped value.
    /// </summary>
    public static void WarnDomain(string operation, double badValue)
    {
        var model = _currentModel ?? "<unknown>";
        Console.Error.WriteLine(
            $"[AD domain clamp] {model}: {operation}({badValue:G}) is out of domain — " +
            $"clamping to safe value. Check SDD equation or continuation overshoot.");
    }
}
