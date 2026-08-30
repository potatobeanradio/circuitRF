namespace CircuitRF.Core.Expressions;

/// <summary>
/// HB-P4 M1 — domain-clamp warnings (design §11) collected across a whole grid call and emitted
/// ONCE, through <see cref="AdWarnings"/>, naming the model exactly as the scalar path does.
///
/// <para>The scalar path warns per evaluation, so a 1,024-sample two-tone iterate that overshoots
/// into <c>log</c>'s forbidden half would write 1,024 identical lines to stderr per Newton step. The
/// grid path records the first offending argument per operation and emits one line for it after the
/// grid completes — same sink, same text. A parallel grid merges its chunks' collectors before
/// emitting, so a warning is never lost and never doubled.</para>
/// </summary>
public struct GridDomainWarnings
{
    private bool _log, _sqrt;
    private double _logVal, _sqrtVal;

    internal void NoteLog(double bad) { if (!_log) { _log = true; _logVal = bad; } }
    internal void NoteSqrt(double bad) { if (!_sqrt) { _sqrt = true; _sqrtVal = bad; } }

    /// <summary>True when anything was clamped anywhere in the grid.</summary>
    public readonly bool Any => _log || _sqrt;

    /// <summary>Folds a worker chunk's collector in. The earlier chunk's argument wins, so the
    /// reported value is the same one a serial run would report.</summary>
    public void Merge(in GridDomainWarnings other)
    {
        if (!_log && other._log) { _log = true; _logVal = other._logVal; }
        if (!_sqrt && other._sqrt) { _sqrt = true; _sqrtVal = other._sqrtVal; }
    }

    /// <summary>Emits at most one line per clamped operation and clears the collector.</summary>
    public void Emit(string modelName)
    {
        if (!Any) return;
        AdWarnings.CurrentModel = modelName;
        if (_log) AdWarnings.WarnDomain("log", _logVal);
        if (_sqrt) AdWarnings.WarnDomain("sqrt", _sqrtVal);
        _log = _sqrt = false;
    }
}
