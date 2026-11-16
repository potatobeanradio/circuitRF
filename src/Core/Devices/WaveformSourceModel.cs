// ================================================================
//  WaveformSourceModel.cs  —  the one model under every waveform source
//  (docs/design/transient.md §6.1, D5, D6; brief-trn-2 R-trn2-1, R-trn2-3)
//
//  V_Pulse V_PWL V_Exp V_SFFM V_Expr and I_Pulse I_PWL I_Exp I_SFFM I_Expr. Each type
//  carries only its own keys; this class holds the waveform arithmetic once, as
//  ToneSourceModelBase holds the tone table once for the tone sources.
//
//  In a transient run the source is its waveform: the engine asks ValueAt(t) and
//  Breakpoints(t0, t1). In every other analysis it is its Dc value (default: the
//  waveform at t = 0), stamped exactly as Vdc (a voltage source) or Idc (a current
//  source) is: the value at DC, and a short (voltage) or an open (current) at every
//  other frequency, so nothing time-varying reaches a steady-state analysis.
//
//  Conventions: a voltage source's value is V(+) - V(-), its branch current flowing
//  + to - through it (VdcModel's); a current source injects its value INTO its first
//  node (CurrentToneSourceModel's, src/Engine/CLAUDE.md).
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>Which waveform a <see cref="WaveformSourceModel"/> draws.</summary>
public enum WaveformKind { Pulse, Pwl, Exp, Sffm, Expr }

/// <summary>
/// A source whose time-domain value a transient run reads directly (brief TRN-2). Every other analysis stamps it at its
/// steady-state value through the ordinary <see cref="ComponentModel.Stamp"/>.
/// </summary>
public interface ITransientWaveform
{
    /// <summary>True for a current source (its value in amperes, injected into its first node).</summary>
    bool IsCurrent { get; }

    /// <summary>The source's value at <paramref name="t"/> (s): volts across + to −, or amperes into the first node.</summary>
    double ValueAt(double t);

    /// <summary>The corners of the waveform in (<paramref name="t0"/>, <paramref name="t1"/>].</summary>
    IEnumerable<double> Breakpoints(double t0, double t1);

    /// <summary>The highest frequency (Hz) the waveform carries, for the automatic step bound; 0 when it states none.</summary>
    double HighestFrequencyHz { get; }

    /// <summary>
    /// The value the ordinary stamp uses at DC instead of the steady-state value, while non-null. A transient run sets it to
    /// read each source's own right-hand-side column (its value at 0 and at 1) and clears it again.
    /// </summary>
    double? StampOverride { get; set; }
}

public sealed class WaveformSourceModel : ComponentModel, ITransientWaveform
{
    /// <summary>The ten type names, voltage first.</summary>
    public static readonly string[] TypeNames =
        ["V_Pulse", "V_PWL", "V_Exp", "V_SFFM", "V_Expr", "I_Pulse", "I_PWL", "I_Exp", "I_SFFM", "I_Expr"];

    /// <summary>Whether <paramref name="typeName"/> is one of <see cref="TypeNames"/>.</summary>
    public static bool IsTypeName(string typeName) =>
        TypeNames.Any(t => t.Equals(typeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys of each waveform, in the order the editor lists them (the voltage spelling; a current type says
    /// I1/I2 where this says V1/V2). <c>Dc</c> is every type's too.</summary>
    public static IReadOnlyList<string> KeysOf(WaveformKind kind, bool current) => kind switch
    {
        WaveformKind.Pulse => [current ? "I1" : "V1", current ? "I2" : "V2", "Delay", "Rise", "Fall", "Width", "Period"],
        WaveformKind.Pwl => ["Points", "File", "Repeat"],
        WaveformKind.Exp => [current ? "I1" : "V1", current ? "I2" : "V2", "Delay1", "Tau1", "Delay2", "Tau2"],
        WaveformKind.Sffm => ["Offset", "Amplitude", "Carrier", "ModIndex", "Signal"],
        WaveformKind.Expr => ["Expr"],
        _ => [],
    };

    /// <summary>The waveform of a type name, and whether it is a current source.</summary>
    public static (WaveformKind Kind, bool Current) Parse(string typeName)
    {
        bool current = typeName.StartsWith("I_", StringComparison.OrdinalIgnoreCase);
        var kind = typeName[2..].ToUpperInvariant() switch
        {
            "PULSE" => WaveformKind.Pulse,
            "PWL" => WaveformKind.Pwl,
            "EXP" => WaveformKind.Exp,
            "SFFM" => WaveformKind.Sffm,
            "EXPR" => WaveformKind.Expr,
            _ => throw new ArgumentException($"'{typeName}' is not a waveform source type."),
        };
        return (kind, current);
    }

    /// <summary>The prefix of a resolved value an Expr= expression reads (the elaborator stores each name it references).</summary>
    public const string ScopeVarPrefix = "_var_";

    public override int PortCount => 1;
    public override ModelKind Kind => ModelKind.Linear;

    private const double OmegaTolRads = 1.0;

    public string TypeName { get; }
    public WaveformKind Waveform { get; }
    public bool IsCurrent { get; }

    /// <summary>The value every analysis but transient sees: <c>Dc=</c>, else the waveform at t = 0 (D6).</summary>
    public double SteadyValue { get; }

    public double? StampOverride { get; set; }

    /// <summary>Branch index of the most recent stamp (a voltage source); −1 before one, and always for a current source.</summary>
    public int LastBranchIndex { get; private set; } = -1;

    // Pulse / Exp levels and timing
    private readonly double _v1, _v2, _delay, _rise, _fall, _width, _period;
    private readonly double _delay2, _tau1, _tau2;
    // SFFM
    private readonly double _offset, _amp, _fc, _mdi, _fs;
    // PWL
    private readonly double[] _pt = [], _pv = [];
    private readonly bool _repeat;
    // Expr
    private readonly Expr? _expr;
    private readonly Evaluator? _ev;
    private readonly Scope? _scope;
    private readonly Dictionary<string, Value> _exprScope = new(StringComparer.Ordinal);

    // brief TRN-6 — the resolved waveform, read by the SPICE deck writer (SpiceDeckWriter), which writes each in the dialect.

    /// <summary>A pulse's levels and timing (s); Width is +∞ when unstated, Period 0 for a single pulse.</summary>
    public (double V1, double V2, double Delay, double Rise, double Fall, double Width, double Period) PulseShape =>
        (_v1, _v2, _delay, _rise, _fall, _width, _period);

    /// <summary>An exponential's levels and timing (s); Delay2 is +∞ when unstated.</summary>
    public (double V1, double V2, double Delay1, double Tau1, double Delay2, double Tau2) ExpShape =>
        (_v1, _v2, _delay, _tau1, _delay2, _tau2);

    /// <summary>An SFFM's offset, amplitude, carrier (Hz), modulation index and signal frequency (Hz).</summary>
    public (double Offset, double Amplitude, double Carrier, double ModIndex, double Signal) SffmShape =>
        (_offset, _amp, _fc, _mdi, _fs);

    /// <summary>A PWL's vertices — from Points= or File=, as read — and whether it repeats from its first vertex.</summary>
    public (IReadOnlyList<double> Times, IReadOnlyList<double> Values, bool Repeat) PwlShape => (_pt, _pv, _repeat);

    /// <summary>An Expr source's expression, and the resolved values of the names it reads besides <c>time</c>.</summary>
    public (Expr? Expression, IReadOnlyDictionary<string, Value> Scope) ExprShape => (_expr, _exprScope);
    private const string ExprScopeName = "WaveformExpr";

    /// <param name="typeName">One of <see cref="TypeNames"/>.</param>
    /// <param name="p">The resolved parameters (base SI; Points/File/Repeat/Expr as text).</param>
    /// <param name="functions">The netlist's expression functions, which an Expr= may call.</param>
    public WaveformSourceModel(string typeName, IReadOnlyDictionary<string, Value> p,
        IReadOnlyList<UserFunction>? functions = null)
    {
        (Waveform, IsCurrent) = Parse(typeName);
        TypeName = TypeNames.First(t => t.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        string l1 = IsCurrent ? "I1" : "V1", l2 = IsCurrent ? "I2" : "V2";

        foreach (var key in p.Keys)
        {
            if (key.StartsWith('_') || key is "Dc" or "m" or Elaborator.NoiseParamName) continue;
            if (!KeysOf(Waveform, IsCurrent).Contains(key, StringComparer.Ordinal))
                throw new ParameterRefusalException(
                    $"{TypeName} takes {string.Join(", ", KeysOf(Waveform, IsCurrent))} and Dc; '{key}' is not one of them.");
        }

        switch (Waveform)
        {
            case WaveformKind.Pulse:
                _v1 = Num(p, l1, 0); _v2 = Num(p, l2, 0);
                _delay = NonNeg(p, "Delay", 0); _rise = NonNeg(p, "Rise", 0); _fall = NonNeg(p, "Fall", 0);
                _width = p.ContainsKey("Width") ? NonNeg(p, "Width", 0) : double.PositiveInfinity;
                _period = p.ContainsKey("Period") ? NonNeg(p, "Period", 0) : 0;
                if (_period > 0 && _rise + _width + _fall > _period)
                    throw new ParameterRefusalException(
                        $"{TypeName}: Rise + Width + Fall ({_rise + _width + _fall:G6} s) is longer than Period ({_period:G6} s).");
                break;
            case WaveformKind.Exp:
                _v1 = Num(p, l1, 0); _v2 = Num(p, l2, 0);
                _delay = NonNeg(p, "Delay1", 0);
                _tau1 = Num(p, "Tau1", double.NaN);
                if (!(_tau1 > 0)) throw new ParameterRefusalException($"{TypeName}: Tau1 must be a positive time.");
                _delay2 = p.ContainsKey("Delay2") ? NonNeg(p, "Delay2", 0) : double.PositiveInfinity;
                _tau2 = p.ContainsKey("Tau2") ? Num(p, "Tau2", 0) : _tau1;
                if (!(_tau2 > 0)) throw new ParameterRefusalException($"{TypeName}: Tau2 must be a positive time.");
                if (_delay2 < _delay) throw new ParameterRefusalException($"{TypeName}: Delay2 must not be before Delay1.");
                break;
            case WaveformKind.Sffm:
                _offset = Num(p, "Offset", 0); _amp = Num(p, "Amplitude", 0);
                _fc = NonNeg(p, "Carrier", 0); _mdi = Num(p, "ModIndex", 0); _fs = NonNeg(p, "Signal", 0);
                break;
            case WaveformKind.Pwl:
            {
                bool hasPoints = p.TryGetValue("Points", out var pts) && Text(pts).Trim().Length > 0;
                bool hasFile = p.TryGetValue("File", out var file) && Text(file).Trim().Length > 0;
                if (hasPoints && hasFile)
                    throw new ParameterRefusalException($"{TypeName}: Points= and File= are both given; a PWL source takes one or the other.");
                if (!hasPoints && !hasFile)
                    throw new ParameterRefusalException($"{TypeName}: give the waveform as Points=\"t v, t v, ...\" or File= (two columns, time and value).");
                (_pt, _pv) = hasPoints ? ParsePoints(Text(pts!), TypeName) : ReadFile(Text(file!), TypeName);
                _repeat = p.TryGetValue("Repeat", out var rep) && Text(rep).Trim().ToLowerInvariant() is "yes" or "on" or "true" or "1";
                if (_repeat && _pt.Length < 2)
                    throw new ParameterRefusalException($"{TypeName}: Repeat=yes needs at least two points to repeat.");
                break;
            }
            case WaveformKind.Expr:
            {
                string text = p.TryGetValue("Expr", out var e) ? Text(e).Trim() : "";
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
                if (text.Length == 0) throw new ParameterRefusalException($"{TypeName}: Expr= is required, an expression in time and the globals.");
                try { _expr = Parser.Parse(text); }
                catch (Exception ex) { throw new ParameterRefusalException($"{TypeName}: Expr=\"{text}\" does not parse: {ex.Message}"); }
                _ev = new Evaluator();
                foreach (var fn in functions ?? []) _ev.RegisterFunction(fn);
                _scope = new Scope(ExprScopeName);
                foreach (var (k, v) in p)
                {
                    if (!k.StartsWith(ScopeVarPrefix, StringComparison.Ordinal)) continue;
                    string name = k[ScopeVarPrefix.Length..];
                    _scope.Bind(name, v.ToString()!);
                    _ev.InjectResolved(ExprScopeName, name, v);
                    _exprScope[name] = v;
                }
                _scope.Bind(TimeVariable.Name, "0");
                break;
            }
        }

        SteadyValue = p.TryGetValue("Dc", out var dc) && dc.Kind is ValueKind.Real or ValueKind.Complex
            ? (dc.Kind == ValueKind.Real ? dc.AsReal() : dc.AsComplex().Real)
            : ValueAt(0);
        if (!double.IsFinite(SteadyValue))
            throw new ParameterRefusalException($"{TypeName}: its value at t = 0 is not a finite number; give Dc= the value other analyses use.");
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2) return;
        double v = Math.Abs(omega) < OmegaTolRads ? StampOverride ?? SteadyValue : 0.0;
        if (IsCurrent)
        {
            if (v == 0) return;    // an open off DC, as Idc
            mna.AddCurrentInjection(c.Nodes[0], new Complex(v, 0));
            mna.AddCurrentInjection(c.Nodes[1], new Complex(-v, 0));
            return;
        }
        int br = LastBranchIndex = mna.AddBranch();
        mna.AddConstraint(br, c.Nodes[0], new Complex(+1, 0));
        mna.AddConstraint(br, c.Nodes[1], new Complex(-1, 0));
        mna.AddBranchCurrent(br, c.Nodes[0], c.Nodes[1]);
        mna.AddSourceValue(br, new Complex(v, 0));
    }

    // ── the waveform ──────────────────────────────────────────────────────

    public double ValueAt(double t) => Waveform switch
    {
        WaveformKind.Pulse => PulseAt(t),
        WaveformKind.Pwl => PwlAt(t),
        WaveformKind.Exp => ExpAt(t),
        WaveformKind.Sffm => _offset + _amp * Math.Sin(2 * Math.PI * _fc * t + _mdi * Math.Sin(2 * Math.PI * _fs * t)),
        WaveformKind.Expr => ExprAt(t),
        _ => 0,
    };

    // Each segment is closed on its right, so a corner of zero duration (Rise=0, Fall=0) reads its LEFT value at the corner
    // itself: the step that lands on the corner integrates towards the level before it, and the jump starts the next segment.
    private double PulseAt(double t)
    {
        double tau = t - _delay;
        if (tau <= 0) return _v1;
        if (_period > 0)
        {
            tau %= _period;
            if (tau == 0) return _v1;
        }
        if (tau <= _rise) return _v1 + (_v2 - _v1) * (tau / _rise);
        if (tau <= _rise + _width) return _v2;
        if (tau <= _rise + _width + _fall) return _v2 + (_v1 - _v2) * ((tau - _rise - _width) / _fall);
        return _v1;
    }

    private double ExpAt(double t)
    {
        if (t <= _delay) return _v1;
        double v = _v1 + (_v2 - _v1) * (1 - Math.Exp(-(t - _delay) / _tau1));
        if (t > _delay2) v += (_v1 - _v2) * (1 - Math.Exp(-(t - _delay2) / _tau2));
        return v;
    }

    private double PwlAt(double t)
    {
        int n = _pt.Length;
        if (_repeat && t > _pt[^1])
        {
            double per = _pt[^1] - _pt[0];
            t = _pt[0] + (t - _pt[0]) % per;
            if (t == _pt[0]) return _pv[^1];    // the end of a period is the last vertex, not the first
        }
        if (t <= _pt[0]) return _pv[0];
        if (t >= _pt[^1]) return _pv[^1];
        int hi = Array.BinarySearch(_pt, t);
        if (hi >= 0) return _pv[hi];
        hi = ~hi;
        int lo = hi - 1;
        return _pv[lo] + (_pv[hi] - _pv[lo]) * ((t - _pt[lo]) / (_pt[hi] - _pt[lo]));
    }

    private double ExprAt(double t)
    {
        _ev!.InjectResolved(ExprScopeName, TimeVariable.Name, new Value(t));
        var v = _ev.EvalExpr(_expr!, _scope!);
        return v.Kind switch
        {
            ValueKind.Real => v.AsReal(),
            ValueKind.Complex => v.AsComplex().Real,
            ValueKind.Bool => v.AsBool() ? 1 : 0,
            _ => throw new ExpressionException($"{TypeName}: Expr= must be a number, not {v.Kind}."),
        };
    }

    public IEnumerable<double> Breakpoints(double t0, double t1)
    {
        var list = new List<double>();
        void Add(double tb) { if (tb > t0 && tb <= t1 && double.IsFinite(tb)) list.Add(tb); }
        switch (Waveform)
        {
            case WaveformKind.Pulse:
            {
                double[] corners = double.IsInfinity(_width) ? [0, _rise] : [0, _rise, _rise + _width, _rise + _width + _fall];
                if (_period > 0)
                {
                    long k0 = Math.Max(0, (long)Math.Floor((t0 - _delay) / _period) - 1);
                    for (long k = k0; _delay + k * _period <= t1; k++)
                        foreach (var c in corners) Add(_delay + k * _period + c);
                }
                else foreach (var c in corners) Add(_delay + c);
                break;
            }
            case WaveformKind.Exp:
                Add(_delay); Add(_delay2);
                break;
            case WaveformKind.Pwl:
                if (_repeat)
                {
                    double per = _pt[^1] - _pt[0];
                    long k0 = Math.Max(0, (long)Math.Floor((t0 - _pt[0]) / per) - 1);
                    for (long k = k0; _pt[0] + k * per <= t1; k++)
                        foreach (var tv in _pt) Add(tv + k * per);
                }
                else foreach (var tv in _pt) Add(tv);
                break;
        }
        list.Sort();
        return list.Distinct();
    }

    public double HighestFrequencyHz => Waveform == WaveformKind.Sffm ? _fc + (Math.Abs(_mdi) + 1) * _fs : 0;

    /// <summary>brief TRN-5 — an SFFM's carrier (Hz), 0 for every other waveform: what the transient steering line counts periods of.</summary>
    public double CarrierHz => Waveform == WaveformKind.Sffm ? _fc : 0;

    /// <summary>brief TRN-5 — whether the waveform is a modulated carrier (an SFFM with a modulation index and a signal), the case
    /// the steering line sends to the envelope analysis.</summary>
    public bool IsModulatedCarrier => Waveform == WaveformKind.Sffm && _fc > 0 && _mdi != 0 && _fs > 0;

    /// <summary>brief TRN-5 — a repeating pulse's period (s), 0 for every other waveform.</summary>
    public double RepeatPeriodS => Waveform == WaveformKind.Pulse ? _period : 0;

    // ── PWL points and files ──────────────────────────────────────────────

    /// <summary>Points="t v, t v, ..." — numbers separated by commas or whitespace, in pairs. The elaborator has already
    /// evaluated each entry; this reads the result.</summary>
    public static (double[] T, double[] V) ParsePoints(string text, string who)
    {
        var tok = text.Trim().Trim('"').Split([',', ' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0 || tok.Length % 2 != 0)
            throw new ParameterRefusalException($"{who}: Points= takes time-value pairs; it has {tok.Length} number(s).");
        var t = new double[tok.Length / 2];
        var v = new double[tok.Length / 2];
        for (int i = 0; i < t.Length; i++)
        {
            if (!double.TryParse(tok[2 * i], NumberStyles.Float, CultureInfo.InvariantCulture, out t[i])
                || !double.TryParse(tok[2 * i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                throw new ParameterRefusalException($"{who}: Points= pair {i + 1} ('{tok[2 * i]} {tok[2 * i + 1]}') is not two numbers.");
            if (i > 0 && !(t[i] > t[i - 1]))
                throw new ParameterRefusalException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: Points= times must increase; pair {1} is at {2:G6} s, not after {3:G6} s.", who, i + 1, t[i], t[i - 1]));
        }
        return (t, v);
    }

    /// <summary>File= — two columns, time and value, separated by a comma or whitespace; '#' starts a comment; times strictly
    /// increasing. A line that breaks a rule is refused by its number.</summary>
    public static (double[] T, double[] V) ReadFile(string path, string who)
    {
        if (!File.Exists(path)) throw new ParameterRefusalException($"{who}: File='{path}' does not exist.");
        var t = new List<double>();
        var v = new List<double>();
        int lineNo = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNo++;
            int hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) continue;
            var cols = line.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length != 2
                || !double.TryParse(cols[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double tt)
                || !double.TryParse(cols[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double vv))
                throw new ParameterRefusalException($"{who}: line {lineNo} of '{Path.GetFileName(path)}' is not two numbers (time, value): '{line}'.");
            if (t.Count > 0 && !(tt > t[^1]))
                throw new ParameterRefusalException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: line {1} of '{2}': time {3:G6} s is not after the previous line's {4:G6} s; times must increase.",
                    who, lineNo, Path.GetFileName(path), tt, t[^1]));
            t.Add(tt); v.Add(vv);
        }
        if (t.Count == 0) throw new ParameterRefusalException($"{who}: '{Path.GetFileName(path)}' holds no points.");
        return ([.. t], [.. v]);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private string TypeLabel => TypeName;

    private double Num(IReadOnlyDictionary<string, Value> p, string key, double def)
    {
        if (!p.TryGetValue(key, out var v)) return def;
        double d = v.Kind switch
        {
            ValueKind.Real => v.AsReal(),
            ValueKind.Complex => v.AsComplex().Real,
            _ => throw new ParameterRefusalException($"{TypeLabel}: {key} must be a number, not '{Text(v)}'."),
        };
        if (!double.IsFinite(d)) throw new ParameterRefusalException($"{TypeLabel}: {key} is not a finite number.");
        return d;
    }

    private double NonNeg(IReadOnlyDictionary<string, Value> p, string key, double def)
    {
        double d = Num(p, key, def);
        if (d < 0) throw new ParameterRefusalException($"{TypeLabel}: {key} must not be negative.");
        return d;
    }

    private static string Text(Value v) => v.Kind == ValueKind.String ? v.AsString() : v.ToString() ?? "";
}

/// <summary>
/// The DC current source, <c>Idc:I1 p n Idc=…</c> — the counterpart of <see cref="VdcModel"/> (brief TRN-2 R-trn2-2). Its
/// value is injected INTO its first node at DC (the engine's current-source convention), and it is an open at every other
/// frequency. Constant in every analysis, transient included.
/// </summary>
public sealed class IdcModel : ComponentModel
{
    public override int PortCount => 1;
    public override ModelKind Kind => ModelKind.Linear;
    private const double OmegaTolRads = 1.0;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length < 2 || Math.Abs(omega) >= OmegaTolRads) return;
        double i = c.Parameters.TryGetValue("Idc", out var v) || c.Parameters.TryGetValue("I", out v)
            ? (v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real) : 0.0;
        if (i == 0) return;
        mna.AddCurrentInjection(c.Nodes[0], new Complex(i, 0));
        mna.AddCurrentInjection(c.Nodes[1], new Complex(-i, 0));
    }
}
