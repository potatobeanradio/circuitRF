namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// R-pc-16 / microstrip-models.md R4: each model's validity range is recorded with it, and a
/// parameter outside that range is reported — once per distinct violation, never per frequency
/// point, never silently extrapolated. R13: each discontinuity model carries its OWN (narrower)
/// range, reported at that model's own bound, not the line model's.
///
/// This is deliberately a tiny, dependency-free, per-instance tracker — one per component
/// instance (constructed alongside it), not a process-wide singleton, so two different MLIN
/// instances each warn about their own out-of-range condition rather than one silencing the
/// other.
///
/// <b>R-mk-7/R-mk-8 (brief-mklopf-performance-and-messages.md): this class never writes to the
/// console.</b> It used to write directly to <c>Console.Error</c> — which, in a GUI run, reaches
/// nobody at all (nothing in this codebase connects <c>Console.Error</c> to the Messages window);
/// that was the actual bug behind every microstrip validity warning silently vanishing, not just
/// the two call sites the owner happened to notice in MKlopf. Every message is instead queued and
/// exposed via <see cref="Drain"/>; a model holding this reporter implements
/// <see cref="IReportsWarnings"/> (returning <c>Drain()</c>) so the engine can route it into
/// <c>ElaboratedNetlist.Warnings</c> — see that interface's own doc comment for the full path.
/// </summary>
public sealed class MicrostripValidityReporter
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<(string Key, string Message)> _pending = new();
    private readonly string _instancePath;

    public MicrostripValidityReporter(string instancePath) => _instancePath = instancePath;

    /// <summary>Queues <paramref name="message"/> (prefixed with this reporter's own instance
    /// path — never a second, hand-typed prefix; R-mk-9's "let the instance path identify the
    /// component") at most once per distinct <paramref name="key"/> for this reporter's lifetime.
    /// A general-purpose sibling to <see cref="CheckRange"/> for free-form informational/warning
    /// text that isn't a range violation (e.g. a curvature warning or a section-count report).</summary>
    public void ReportOnce(string key, string message)
    {
        if (_reported.Add(key))
            _pending.Add((key, $"{_instancePath}: {message}"));
    }

    /// <summary>Checks <paramref name="value"/> against [<paramref name="min"/>,
    /// <paramref name="max"/>]; reports once per distinct (model, parameter) pair for this
    /// instance, via <see cref="ReportOnce"/>. Returns the value unchanged either way — this
    /// never clamps or alters what gets computed; R-pc-16 forbids silent extrapolation, not
    /// evaluation past the bound (the formula still runs; the user is just told its output is no
    /// longer within the published claim).</summary>
    public double CheckRange(string modelName, string parameterName, double value, double min, double max, string units = "")
    {
        if (value >= min && value <= max) return value;

        string unitSuffix = units.Length > 0 ? " " + units : "";
        ReportOnce($"{modelName}:{parameterName}",
            $"{modelName}'s {parameterName}={value:G6}{unitSuffix} is outside its published validity range " +
            $"[{min:G6}, {max:G6}]{unitSuffix} — the result is a plausible-looking extrapolation, not a validated value.");
        return value;
    }

    /// <summary>Returns and clears every message queued since the last drain. The once-per-key
    /// <see cref="_reported"/> gate is untouched by a drain — an already-reported violation never
    /// re-queues even if <see cref="CheckRange"/>/<see cref="ReportOnce"/> is called again with the
    /// same key.</summary>
    public IReadOnlyList<(string Key, string Message)> Drain()
    {
        if (_pending.Count == 0) return Array.Empty<(string, string)>();
        var result = _pending.ToArray();
        _pending.Clear();
        return result;
    }
}

/// <summary>A named (min,max) validity bound, so each physics function can declare its own range
/// next to the formula it governs (R4: "ranges are transcribed from the sources, not estimated").</summary>
public readonly record struct ValidityRange(double Min, double Max, string Units = "")
{
    public bool Contains(double v) => v >= Min && v <= Max;
}
