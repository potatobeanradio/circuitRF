// ================================================================
//  VSwitchModel.cs  —  the voltage-controlled switch (docs/design/transient.md §7.1;
//  brief-trn-3 R-trn3-1, R-trn3-2)
//
//  VSwitch:S1 p n cp cn Ron=1 Roff=1e9 Von=1 Voff=0
//
//  A conductance g(vc) between p and n, vc = v(cp) − v(cn), the control pair drawing no
//  current. g moves between 1/Roff and 1/Ron along a smooth monotone transition: its
//  LOGARITHM follows a cubic smoothstep in u = (vc − Voff)/(Von − Voff),
//
//      ln g = ln gOff + (ln gOn − ln gOff)·(3u² − 2u³),   u clamped to [0, 1],
//
//  so g and dg/dvc are continuous everywhere (the cubic's slope is zero at both ends) and
//  the decades between the two resistances are crossed evenly rather than all at the
//  on end. Von < Voff is inverse logic with no other change. The device has no charge.
//
//  An ordinary nonlinear ComponentModel: DC, S-parameter (linearised at its bias), HB,
//  envelope and transient all read Evaluate. Hyst (hysteresis, volts) is transient-only
//  state (ITransientDevice): off, the window sits Hyst/2 further towards Von; on, Hyst/2
//  further towards Voff; the state flips only once the device is fully across its window,
//  where g is already at its end value, so the flip moves g by nothing.
// ================================================================

using System.Globalization;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A nonlinear device the transient engine tells about the run (brief TRN-3): the window of a control voltage across which
/// it changes, which the step control limits its step across without landing on it (a soft breakpoint), and any state it
/// carries from one accepted step to the next.
/// </summary>
public interface ITransientDevice
{
    /// <summary>The control port (0-based), the window [Lo, Hi] of its voltage across which the device changes, and how many
    /// accepted steps a full crossing of it should take. Null for no window.</summary>
    (int Port, double Lo, double Hi, int Points)? SoftWindow { get; }

    /// <summary>A transient run is starting: anything steady-state analyses are told about this device is moot.</summary>
    void EnterTransient();

    /// <summary>The starting state is solved, at these port voltages.</summary>
    void BeginTransient(ReadOnlySpan<double> portV);

    /// <summary>A step was accepted at these port voltages. True when the device's state — and so its i(v) — changed.</summary>
    bool AcceptStep(ReadOnlySpan<double> portV);

    /// <summary>The transient run is over: the device is back in steady state.</summary>
    void EndTransient();
}

public sealed class VSwitchModel : ComponentModel, ITransientDevice, IReportsWarnings
{
    /// <summary>The <c>.cnl</c> type.</summary>
    public const string TypeName = "VSwitch";

    /// <summary>The keys it takes.</summary>
    public static readonly string[] Keys = ["Ron", "Roff", "Von", "Voff", "Hyst", "TransitionPoints"];

    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Nonlinear;

    public double Ron { get; }
    public double Roff { get; }
    public double Von { get; }
    public double Voff { get; }
    public double Hyst { get; }
    public int TransitionPoints { get; }

    private readonly double _lnOff, _dLn;
    private readonly string _name;
    private readonly List<(string, string)> _warnings = [];
    private bool _inTransient;
    private bool _on;

    public VSwitchModel(IReadOnlyDictionary<string, Value> p, string instanceName = "")
    {
        _name = instanceName;
        foreach (var key in p.Keys)
            if (!key.StartsWith('_') && key is not ("m" or Elaborator.NoiseParamName) && !Keys.Contains(key, StringComparer.Ordinal))
                throw new ParameterRefusalException($"VSwitch takes {string.Join(", ", Keys)}; '{key}' is not one of them.");
        Ron = Num(p, "Ron", 1);
        Roff = Num(p, "Roff", 1e9);
        Von = Num(p, "Von", 1);
        Voff = Num(p, "Voff", 0);
        Hyst = Num(p, "Hyst", 0);
        double tp = Num(p, "TransitionPoints", 8);
        if (!(Ron > 0) || !(Roff > 0))
            throw new ParameterRefusalException("VSwitch: Ron and Roff must be positive resistances.");
        if (Von == Voff)
            throw new ParameterRefusalException(
                "VSwitch: Von and Voff must differ — the switch moves between them, and a transition of no width is a step no analysis can resolve.");
        if (Hyst < 0) throw new ParameterRefusalException("VSwitch: Hyst must not be negative.");
        if (!(tp >= 1)) throw new ParameterRefusalException("VSwitch: TransitionPoints must be at least 1.");
        TransitionPoints = (int)Math.Ceiling(tp);
        _lnOff = Math.Log(1 / Roff);
        _dLn = Math.Log(1 / Ron) - _lnOff;
        if (Hyst != 0)
            _warnings.Add(($"vswitch-hyst:{_name}", string.Format(CultureInfo.InvariantCulture,
                "{0}: Hyst={1:G4} V applies in a transient analysis only; this analysis ignores it.",
                _name.Length > 0 ? _name : "VSwitch", Hyst)));
    }

    /// <summary>The window's offset now: Hyst/2 towards Von while off, towards Voff while on, nothing outside transient.</summary>
    private double Shift => !_inTransient || Hyst == 0 ? 0 : (_on ? -0.5 : 0.5) * Hyst * Math.Sign(Von - Voff);

    /// <summary>The conductance at control voltage <paramref name="vc"/> and its derivative, in steady state (no hysteresis).</summary>
    public (double G, double DG) Conductance(double vc) => Conductance(vc, 0);

    private (double G, double DG) Conductance(double vc, double shift)
    {
        double span = Von - Voff;
        double u = (vc - Voff - shift) / span;
        if (u <= 0) return (1 / Roff, 0);
        if (u >= 1) return (1 / Ron, 0);
        double s = u * u * (3 - 2 * u), ds = 6 * u * (1 - u);
        double g = Math.Exp(_lnOff + _dLn * s);
        return (g, g * _dLn * ds / span);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        double vsw = v[0], vc = v[1];
        var (g, dg) = Conductance(vc, Shift);
        var i = new double[2];
        var q = new double[2];
        var dgm = new double[2, 2];
        var dc = new double[2, 2];
        i[0] = g * vsw;
        dgm[0, 0] = g;
        dgm[0, 1] = dg * vsw;
        return new NonlinearResult(i, q, dgm, dc);
    }

    // ── transient ─────────────────────────────────────────────────────────

    public (int Port, double Lo, double Hi, int Points)? SoftWindow
    {
        get
        {
            double a = Voff + Shift, b = Von + Shift;
            return (1, Math.Min(a, b), Math.Max(a, b), TransitionPoints);
        }
    }

    public void EnterTransient()
    {
        _inTransient = true;
        _on = false;
        _warnings.Clear();
    }

    public void BeginTransient(ReadOnlySpan<double> portV)
    {
        // on when the steady-state conductance is past the middle of its decades
        double u = (portV[1] - Voff) / (Von - Voff);
        _on = u >= 0.5;
    }

    public bool AcceptStep(ReadOnlySpan<double> portV)
    {
        if (Hyst == 0) return false;
        double u = (portV[1] - Voff - Shift) / (Von - Voff);
        bool on = _on ? u > 0 : u >= 1;
        if (on == _on) return false;
        _on = on;
        return true;
    }

    public void EndTransient() => _inTransient = false;

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        var list = _warnings.ToList();
        _warnings.Clear();
        return list;
    }

    private static double Num(IReadOnlyDictionary<string, Value> p, string key, double def)
    {
        if (!p.TryGetValue(key, out var v)) return def;
        double d = v.Kind switch
        {
            ValueKind.Real => v.AsReal(),
            ValueKind.Complex => v.AsComplex().Real,
            _ => throw new ParameterRefusalException($"VSwitch: {key} must be a number."),
        };
        if (!double.IsFinite(d)) throw new ParameterRefusalException($"VSwitch: {key} is not a finite number.");
        return d;
    }
}
