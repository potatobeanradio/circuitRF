using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Single- or multi-tone ideal voltage source (linear-engine §4.4).
/// One internal model, two netlist spellings: V_1Tone and V_nTone.
///
/// Group 2 branch-current element. Stamps constraint Va − Vb = E(ω):
///   ω = 0         → E = Vdc  (DC bias, may be zero)
///   ω ≈ 2π·Freq_i → E = phasor_i (V * exp(j·Phase_i·π/180))
///   otherwise     → E = 0  (short at non-excited frequencies)
///
/// Dynamic amplitude: HB engine calls ReevaluateFromGlobals at each sweep step
/// so that expressions like V=Vs_mag (which depends on Pavl_dbm) are updated.
/// </summary>
public sealed class ToneSourceModel : ComponentModel
{
    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Linear;

    // Matching tolerance: 1 rad/s — negligible at GHz; exact by the HB guarantee.
    private const double OmegaTolRads = 1.0;

    public record ToneEntry(double FreqHz, Complex Phasor,
        Expr? VExpr, Expr? PhaseExpr, IReadOnlyDictionary<string, Value> ScopeVars);

    private readonly ToneEntry[] _tones;
    private readonly double      _vdcResolved;
    private readonly Expr?       _vdcExpr;
    private readonly IReadOnlyDictionary<string, Value> _vdcScopeVars;

    // Working copies, updated by ReevaluateFromGlobals.
    private Complex[] _currentPhasors;
    private double    _currentVdc;

    public ToneSourceModel(ToneEntry[] tones, double vdcResolved,
        Expr? vdcExpr = null, IReadOnlyDictionary<string, Value>? vdcScopeVars = null)
    {
        _tones           = tones;
        _vdcResolved     = vdcResolved;
        _vdcExpr         = vdcExpr;
        _vdcScopeVars    = vdcScopeVars ?? new Dictionary<string, Value>();
        _currentPhasors  = tones.Select(t => t.Phasor).ToArray();
        _currentVdc      = vdcResolved;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        int va = c.Nodes[0];
        int vb = c.Nodes[1];

        int br = mna.AddBranch();
        mna.AddConstraint(br, va, new Complex(+1, 0));
        mna.AddConstraint(br, vb, new Complex(-1, 0));
        mna.AddBranchCurrent(br, va, vb);

        Complex e = Complex.Zero;
        if (Math.Abs(omega) < OmegaTolRads)
        {
            e = new Complex(_currentVdc, 0);
            for (int i = 0; i < _tones.Length; i++)
            {
                double omegaTone = 2.0 * Math.PI * _tones[i].FreqHz;
                if (Math.Abs(omegaTone) < OmegaTolRads)
                    e += _currentPhasors[i];
            }
        }
        else
        {
            for (int i = 0; i < _tones.Length; i++)
            {
                double omegaTone = 2.0 * Math.PI * _tones[i].FreqHz;
                if (Math.Abs(omega - omegaTone) < OmegaTolRads)
                {
                    e = _currentPhasors[i];
                    break;
                }
            }
            // else e = 0 (short at this frequency — no excitation)
        }
        mna.AddSourceValue(br, e);
    }

    /// <summary>
    /// Returns one warning per tone with Freq≈0 Hz that has non-negligible amplitude.
    /// Called by the Elaborator after construction; routes via <see cref="ElaboratedNetlist.AddWarningOnce"/>.
    /// </summary>
    public IReadOnlyList<string> GetZeroHzToneWarnings(string instancePath)
    {
        var warnings = new List<string>();
        for (int i = 0; i < _tones.Length; i++)
            if (Math.Abs(_tones[i].FreqHz) < 1.0 && _currentPhasors[i].Magnitude > 1e-12)
                warnings.Add($"'{instancePath}': a tone has Freq=0 — use Vdc for DC bias.");
        return warnings;
    }

    /// <summary>
    /// Re-evaluate phasors and Vdc using updated global variable values.
    /// Called by the HB engine at each sweep point when a sweep variable changes.
    /// </summary>
    public void ReevaluateFromGlobals(IReadOnlyDictionary<string, Value> globals)
    {
        for (int i = 0; i < _tones.Length; i++)
        {
            var tone = _tones[i];
            if (tone.VExpr is null) continue;  // phasor was a literal — use initial value

            Complex v     = EvalComplex(tone.VExpr,     tone.ScopeVars, globals);
            double  phase = EvalReal(tone.PhaseExpr, tone.ScopeVars, globals, 0.0);
            _currentPhasors[i] = v * Complex.FromPolarCoordinates(1.0, phase * Math.PI / 180.0);
        }

        if (_vdcExpr is not null)
            _currentVdc = EvalReal(_vdcExpr, _vdcScopeVars, globals, _vdcResolved);
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
