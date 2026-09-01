using System.Numerics;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The tone machinery shared by the ideal VOLTAGE tone source (<see cref="ToneSourceModel"/>,
/// <c>V_1Tone</c>/<c>V_nTone</c>) and the ideal CURRENT tone source
/// (<see cref="CurrentToneSourceModel"/>, <c>I_1Tone</c>/<c>I_nTone</c>).
///
/// <para>Everything about a tone source EXCEPT how it reaches the matrix lives here: the tone table,
/// the DC offset, the sweep-time re-evaluation of amplitude/phase expressions, and the zero-Hz-tone
/// warning. The two derived types differ only in their <c>Stamp</c> — a voltage source is a Group 2
/// branch-current element (it pins Va−Vb) and a current source is a Group 1 RHS injection (it pins
/// nothing and is an OPEN off its tones).</para>
///
/// <para>The two are otherwise the same device, which is why engine code that asks "is this a tone
/// source" (commensurability checks, sweep-point re-evaluation, drive zeroing for Y extraction)
/// tests THIS type rather than either leaf — a check that named only the voltage leaf would let a
/// current tone source off the grid, or leave its drive live during a source-zeroed extraction.</para>
/// </summary>
public abstract class ToneSourceModelBase : ComponentModel, IDriveScalable
{
    /// <inheritdoc/>
    public double DriveScale { get; set; } = 1.0;

    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    // Matching tolerance: 1 rad/s — negligible at GHz; exact by the HB guarantee.
    protected const double OmegaTolRads = 1.0;

    /// <summary>
    /// One declared tone. The AMPLITUDE and the PHASE are carried separately rather than
    /// pre-multiplied into one phasor, because either may be a swept expression that
    /// <see cref="ReevaluateFromGlobals"/> re-resolves on its own; the phasor is
    /// <c>VResolved·e^(j·PhaseRad)</c> and is formed in one place.
    /// </summary>
    /// <param name="FreqHz">Tone frequency in Hz.</param>
    /// <param name="VResolved">Resolved amplitude, WITHOUT the phase applied.</param>
    /// <param name="PhaseRad">
    /// The resolved phase, in RADIANS. It is radians because the Elaborator has already applied the
    /// parameter's own angle unit — the convention <c>TLineModel</c>'s <c>E</c> established — so an
    /// authored <c>Phase=45 deg</c> arrives here as 0.7854.
    /// </param>
    /// <param name="VExpr">Raw amplitude expression, when the amplitude referenced a variable.</param>
    /// <param name="PhaseExpr">Raw phase expression, when the phase referenced a variable.</param>
    /// <param name="VScale">
    /// The unit multiplier the Elaborator applied to the AMPLITUDE, so that re-evaluating
    /// <paramref name="VExpr"/> — which is only the raw expression TEXT, with no unit attached —
    /// lands on the same number the first resolution did. 1.0 when there is no unit, and also 1.0
    /// under the var-unit-wins rule, where the referenced variable carried its own unit and the site
    /// unit was never applied in the first place.
    /// </param>
    /// <param name="PhaseScale">The same multiplier for the PHASE — π/180 for <c>deg</c>.</param>
    /// <param name="ScopeVars">Resolved scope this tone's expressions are evaluated against.</param>
    public record ToneEntry(double FreqHz, Complex VResolved, double PhaseRad,
        Expr? VExpr, Expr? PhaseExpr, double VScale, double PhaseScale,
        IReadOnlyDictionary<string, Value> ScopeVars);

    /// <summary>A tone's phasor: its amplitude turned by its phase. The ONE place the two meet.</summary>
    private static Complex Phasor(ToneEntry t)
        => t.VResolved * Complex.FromPolarCoordinates(1.0, t.PhaseRad);

    private readonly ToneEntry[] _tones;
    private readonly double      _dcResolved;
    private readonly Expr?       _dcExpr;
    private readonly IReadOnlyDictionary<string, Value> _dcScopeVars;

    // Working copies, updated by ReevaluateFromGlobals.
    private readonly Complex[] _currentPhasors;
    private          double    _currentDc;

    protected ToneSourceModelBase(ToneEntry[] tones, double dcResolved,
        Expr? dcExpr, IReadOnlyDictionary<string, Value>? dcScopeVars)
    {
        _tones           = tones;
        _dcResolved      = dcResolved;
        _dcExpr          = dcExpr;
        _dcScopeVars     = dcScopeVars ?? new Dictionary<string, Value>();
        _currentPhasors  = tones.Select(Phasor).ToArray();
        _currentDc       = dcResolved;
    }

    /// <summary>
    /// The parameter that carries this source's DC offset — <c>Vdc</c> for a voltage source,
    /// <c>Idc</c> for a current source. Named here only so the zero-Hz warning can point at the
    /// right one.
    /// </summary>
    protected abstract string DcParamName { get; }

    /// <summary>
    /// The source's excitation at <paramref name="omega"/> — volts for a voltage source, amps for a
    /// current source.
    ///
    ///   ω = 0         → DC offset + every tone that sits at 0 Hz
    ///   ω ≈ 2π·Freq_i → that tone's phasor
    ///   otherwise     → zero (no excitation at this frequency)
    /// </summary>
    protected Complex ExcitationAt(double omega)
    {
        // DriveScale multiplies the TONES and not the DC offset — see IDriveScalable. A tone that
        // happens to sit at 0 Hz is still a tone and still scales; Vdc/Idc is the bias and does not.
        if (Math.Abs(omega) < OmegaTolRads)
        {
            var e = new Complex(_currentDc, 0);
            for (int i = 0; i < _tones.Length; i++)
                if (Math.Abs(2.0 * Math.PI * _tones[i].FreqHz) < OmegaTolRads)
                    e += DriveScale * _currentPhasors[i];
            return e;
        }

        for (int i = 0; i < _tones.Length; i++)
            if (Math.Abs(omega - 2.0 * Math.PI * _tones[i].FreqHz) < OmegaTolRads)
                return DriveScale * _currentPhasors[i];

        return Complex.Zero;
    }

    /// <summary>
    /// Returns one warning per tone with Freq≈0 Hz that has non-negligible amplitude.
    /// Called by the Elaborator after construction; routes via <c>ElaboratedNetlist.AddWarningOnce</c>.
    /// </summary>
    public IReadOnlyList<string> GetZeroHzToneWarnings(string instancePath)
    {
        var warnings = new List<string>();
        for (int i = 0; i < _tones.Length; i++)
            if (Math.Abs(_tones[i].FreqHz) < 1.0 && _currentPhasors[i].Magnitude > 1e-12)
                warnings.Add($"'{instancePath}': a tone has Freq=0 — use {DcParamName} for DC bias.");
        return warnings;
    }

    /// <summary>
    /// Re-evaluate phasors and the DC offset using updated global variable values.
    /// Called by the HB engine at each sweep point when a sweep variable changes.
    /// </summary>
    public void ReevaluateFromGlobals(IReadOnlyDictionary<string, Value> globals)
    {
        for (int i = 0; i < _tones.Length; i++)
        {
            var tone = _tones[i];
            if (tone.VExpr is null && tone.PhaseExpr is null)
                continue;                       // both were literals — the initial phasor stands

            // Both halves re-apply their own unit multiplier, because what is re-evaluated here is
            // the RAW expression text and the Elaborator's unit scaling is not part of it. A phase
            // that is a literal keeps its already-resolved radians rather than falling back to zero.
            Complex v = tone.VExpr is null
                ? tone.VResolved
                : EvalComplex(tone.VExpr, tone.ScopeVars, globals) * tone.VScale;

            double phase = tone.PhaseExpr is null
                ? tone.PhaseRad
                : EvalReal(tone.PhaseExpr, tone.ScopeVars, globals, 0.0) * tone.PhaseScale;

            _currentPhasors[i] = v * Complex.FromPolarCoordinates(1.0, phase);
        }

        if (_dcExpr is not null)
            _currentDc = EvalReal(_dcExpr, _dcScopeVars, globals, _dcResolved);
    }

    // Evaluate a complex-valued expression with merged scope (static + updated globals).
    private static Complex EvalComplex(Expr expr, IReadOnlyDictionary<string, Value> scopeVars,
        IReadOnlyDictionary<string, Value> overrides)
    {
        var val = EvalWithOverrides(expr, scopeVars, overrides);
        return val.Kind == ValueKind.Real ? new Complex(val.AsReal(), 0) : val.AsComplex();
    }

    private static double EvalReal(Expr? expr, IReadOnlyDictionary<string, Value> scopeVars,
        IReadOnlyDictionary<string, Value> overrides, double fallback)
    {
        if (expr is null) return fallback;
        var val = EvalWithOverrides(expr, scopeVars, overrides);
        return val.Kind == ValueKind.Real ? val.AsReal() : val.AsComplex().Real;
    }

    private static Value EvalWithOverrides(Expr expr, IReadOnlyDictionary<string, Value> scopeVars,
        IReadOnlyDictionary<string, Value> overrides)
    {
        const string scopeName = "ToneSource";
        var scope = new Scope(scopeName);
        var ev    = new Evaluator();

        // Inject base scope vars, then override with updated globals.
        foreach (var kv in scopeVars)
        {
            scope.Bind(kv.Key, kv.Value.ToString()!);
            ev.InjectResolved(scopeName, kv.Key, kv.Value);
        }
        foreach (var kv in overrides)
        {
            scope.Bind(kv.Key, kv.Value.ToString()!);
            ev.InjectResolved(scopeName, kv.Key, kv.Value);
        }

        return ev.EvalExpr(expr, scope);
    }
}
