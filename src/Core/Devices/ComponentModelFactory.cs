using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;
using RfCore;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Looks up a primitive type name and returns a ComponentModel instance.
/// Parameterless primitives (R, L, C, Port, Term) use the factory registry.
/// Parameterized primitives (SnP) are created from resolved parameter values.
/// </summary>
public static class ComponentModelFactory
{
    private static readonly Dictionary<string, Func<ComponentModel>> _registry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "R",     () => new ResistorModel()       },
            { "C",     () => new CapacitorModel()    },
            { "L",     () => new InductorModel()     },
            { "V",     () => new VoltageSourceModel() },
            { "Port",  () => new PortModel()          },
            { "Term",  () => new TermModel()          },
            { "Short", () => new ShortModel()         },
        };

    // Types that require resolved parameters at construction time.
    private static readonly HashSet<string> _parameterizedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "SnP", "Mutual", "SDD", "Z_Port", "V_1Tone", "V_nTone" };

    /// <summary>
    /// Returns a new ComponentModel, using resolved parameters when needed.
    /// Returns null only if the type name is not a known primitive (i.e. it is a sub-cell).
    /// </summary>
    public static ComponentModel? TryCreate(string typeName,
        IReadOnlyDictionary<string, Value> parameters)
    {
        if (typeName.Equals("SnP",    StringComparison.OrdinalIgnoreCase))
            return CreateSnpModel(parameters);
        if (typeName.Equals("Mutual", StringComparison.OrdinalIgnoreCase))
            return CreateMutualModel(parameters);
        if (typeName.Equals("SDD",    StringComparison.OrdinalIgnoreCase))
            return CreateSddModel(parameters);
        if (typeName.Equals("Z_Port", StringComparison.OrdinalIgnoreCase))
            return CreateZPortModel(parameters);
        if (typeName.Equals("V_1Tone", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("V_nTone", StringComparison.OrdinalIgnoreCase))
            return CreateToneSourceModel(typeName, parameters);
        return TryCreate(typeName);
    }

    /// <summary>
    /// Returns a new parameterless ComponentModel, or null if not a known primitive.
    /// </summary>
    public static ComponentModel? TryCreate(string typeName)
        => _registry.TryGetValue(typeName, out var factory) ? factory() : null;

    public static bool IsPrimitive(string typeName)
        => _registry.ContainsKey(typeName) || _parameterizedTypes.Contains(typeName);

    /// <summary>Register additional parameterless primitive types.</summary>
    public static void Register(string typeName, Func<ComponentModel> factory)
        => _registry[typeName] = factory;

    private static SnpModel CreateSnpModel(IReadOnlyDictionary<string, Value> parameters)
    {
        if (!parameters.TryGetValue("NumPorts", out var np) || np.Kind != ValueKind.Real)
            throw new InvalidOperationException("SnP: NumPorts parameter is missing or not a number");
        int portCount = (int)np.AsReal();

        if (!parameters.TryGetValue("File", out var fileVal) || fileVal.Kind != ValueKind.String)
            throw new InvalidOperationException("SnP: File parameter is missing or not a string");
        string filePath = fileVal.AsString();

        var interpMethod = InterpolationMethod.CubicSpline;
        if (parameters.TryGetValue("InterpMode", out var im) && im.Kind == ValueKind.String)
            interpMethod = im.AsString().Equals("linear", StringComparison.OrdinalIgnoreCase)
                ? InterpolationMethod.Linear
                : InterpolationMethod.CubicSpline;

        var extrapPolicy = OutOfRangePolicy.WarnClamp;
        if (parameters.TryGetValue("ExtrapMode", out var em) && em.Kind == ValueKind.String)
            extrapPolicy = em.AsString().Equals("extrapolate", StringComparison.OrdinalIgnoreCase)
                ? OutOfRangePolicy.WarnExtrapolate
                : OutOfRangePolicy.WarnClamp;

        return new SnpModel(portCount, filePath, interpMethod, extrapPolicy);
    }

    private static MutualInductanceModel CreateMutualModel(IReadOnlyDictionary<string, Value> parameters)
    {
        if (!parameters.TryGetValue("Inductor1", out var i1) || i1.Kind != ValueKind.String)
            throw new InvalidOperationException("Mutual: Inductor1 parameter is missing or not a string");
        if (!parameters.TryGetValue("Inductor2", out var i2) || i2.Kind != ValueKind.String)
            throw new InvalidOperationException("Mutual: Inductor2 parameter is missing or not a string");
        return new MutualInductanceModel(i1.AsString(), i2.AsString());
    }

    // ── Z_Port ────────────────────────────────────────────────────────────────

    private static readonly Regex RxZEntry = new(@"^Z\[(\d+),(\d+)\]$", RegexOptions.Compiled);

    private static ZPortModel CreateZPortModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string name = parameters.TryGetValue("ZPortName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "Z_Port";
        int portCount = parameters.TryGetValue("ZPortCount", out var pc) && pc.Kind == ValueKind.Real
            ? (int)pc.AsReal()
            : throw new InvalidOperationException("Z_Port: ZPortCount is required");

        var zExprs = new Expr?[portCount, portCount];
        var numericParams = new Dictionary<string, Value>(StringComparer.Ordinal);

        foreach (var kv in parameters)
        {
            if (kv.Key is "ZPortName" or "ZPortCount") continue;
            var m = RxZEntry.Match(kv.Key);
            if (m.Success)
            {
                int p = int.Parse(m.Groups[1].Value) - 1;   // 1-based → 0-based
                int q = int.Parse(m.Groups[2].Value) - 1;
                if (p >= 0 && p < portCount && q >= 0 && q < portCount &&
                    kv.Value.Kind == ValueKind.String)
                    zExprs[p, q] = Parser.Parse(kv.Value.AsString());
            }
            else if (kv.Value.Kind is ValueKind.Real or ValueKind.Complex)
            {
                numericParams[kv.Key] = kv.Value;
            }
        }

        return new ZPortModel(portCount, zExprs, numericParams, name);
    }

    // ── ToneSource (V_1Tone / V_nTone) ───────────────────────────────────────

    private static ToneSourceModel CreateToneSourceModel(
        string typeName, IReadOnlyDictionary<string, Value> parameters)
    {
        bool isV1 = typeName.Equals("V_1Tone", StringComparison.OrdinalIgnoreCase);

        // Collect resolved scope vars (everything that isn't metadata or equations).
        var scopeVars = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var kv in parameters)
            if (kv.Value.Kind is ValueKind.Real or ValueKind.Complex &&
                kv.Key is not ("ToneSrcName" or "ToneSrcNumFreqs"))
                scopeVars[kv.Key] = kv.Value;

        double vdcResolved = 0.0;
        Expr?  vdcExpr     = null;
        if (parameters.TryGetValue("Vdc", out var vdcVal))
        {
            if (vdcVal.Kind == ValueKind.String)
                vdcExpr = Parser.Parse(vdcVal.AsString());
            else if (vdcVal.Kind == ValueKind.Real)
                vdcResolved = vdcVal.AsReal();
        }

        var tones = new List<ToneSourceModel.ToneEntry>();

        if (isV1)
        {
            double freq = GetReal(parameters, "Freq", 0.0);
            tones.Add(BuildToneEntry(freq, parameters, "V", "Phase", scopeVars));
        }
        else
        {
            int numFreqs = (int)GetReal(parameters, "NumFreqs", 0.0);
            for (int i = 1; i <= numFreqs; i++)
            {
                double freq = GetReal(parameters, $"Freq[{i}]", 0.0);
                tones.Add(BuildToneEntry(freq, parameters, $"V[{i}]", $"Phase[{i}]", scopeVars));
            }
        }

        return new ToneSourceModel(tones.ToArray(), vdcResolved, vdcExpr, scopeVars);
    }

    private static ToneSourceModel.ToneEntry BuildToneEntry(
        double freqHz,
        IReadOnlyDictionary<string, Value> parameters,
        string vKey,
        string phaseKey,
        IReadOnlyDictionary<string, Value> scopeVars)
    {
        // Check if the V parameter has a raw expression (variable-ref, needs re-evaluation on sweep).
        Expr?   vExpr     = null;
        Complex phasor    = Complex.Zero;
        Expr?   phaseExpr = null;
        double  phaseDeg  = 0.0;

        string exprVKey = $"_expr_{vKey}";
        if (parameters.TryGetValue(exprVKey, out var rawV) && rawV.Kind == ValueKind.String)
            vExpr = Parser.Parse(rawV.AsString());

        if (parameters.TryGetValue(vKey, out var vVal))
        {
            if (vExpr is null && vVal.Kind == ValueKind.String)
                vExpr = Parser.Parse(vVal.AsString());
            else if (vVal.Kind == ValueKind.Real)
                phasor = new Complex(vVal.AsReal(), 0);
            else if (vVal.Kind == ValueKind.Complex)
                phasor = vVal.AsComplex();
        }

        if (parameters.TryGetValue(phaseKey, out var phVal))
        {
            if (phVal.Kind == ValueKind.String)
                phaseExpr = Parser.Parse(phVal.AsString());
            else if (phVal.Kind == ValueKind.Real)
                phaseDeg = phVal.AsReal();
        }

        if (vExpr is null)
            phasor = phasor * Complex.FromPolarCoordinates(1.0, phaseDeg * Math.PI / 180.0);

        return new ToneSourceModel.ToneEntry(freqHz, phasor, vExpr, phaseExpr, scopeVars);
    }

    private static double GetReal(IReadOnlyDictionary<string, Value> parameters, string key, double fallback)
        => parameters.TryGetValue(key, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : fallback;

    // Regex for I[p,w] parameter names — named groups "p" and "w".
    private static readonly Regex RxCurrentEq = new(@"^I\[(\d+),(\d+)\]$", RegexOptions.Compiled);
    // Regex for unsupported constructs that must hard-error.
    private static readonly Regex RxImplicitEq = new(@"^F\[",  RegexOptions.Compiled);
    private static readonly Regex RxCurrentCtrl = new(@"^C(port)?\[", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Noise entries (In, Nc) — silently skip.
    private static readonly Regex RxNoise = new(@"^(In|Nc)\[", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static SddModel CreateSddModel(IReadOnlyDictionary<string, Value> parameters)
    {
        // Parameters dict for SDD (populated by Elaborator.ResolveSddParameters):
        //   "I[p,w]" → Value.String(expressionText)  — equation entries
        //   "SddName" → Value.String(name)            — device name for warnings
        //   "SddPortCount" → Value.Real(N)            — port count
        //   everything else → Value.Real(double)      — resolved scope variables

        // Extract metadata
        string sddName = parameters.TryGetValue("SddName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "SDD";

        int portCount = parameters.TryGetValue("SddPortCount", out var pc) && pc.Kind == ValueKind.Real
            ? (int)pc.AsReal()
            : throw new InvalidOperationException("SDD: SddPortCount is required");

        // Collect resolved numeric parameters (scope variables like B, Sc, …)
        var numericParams = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var kv in parameters)
        {
            if (kv.Key is "SddName" or "SddPortCount") continue;
            if (kv.Value.Kind == ValueKind.Real)
                numericParams[kv.Key] = kv.Value.AsReal();
        }

        // Build equation arrays — indexed by port-1
        var currentAst = new Expr?[portCount];
        var chargeAst  = new Expr?[portCount];

        foreach (var kv in parameters)
        {
            var key = kv.Key;

            // Hard errors for unsupported SDD constructs that change device physics
            if (RxImplicitEq.IsMatch(key))
                throw new InvalidOperationException(
                    $"SDD '{sddName}': implicit equation F[...] not supported; use I[...] for explicit current");
            if (RxCurrentCtrl.IsMatch(key))
                throw new InvalidOperationException(
                    $"SDD '{sddName}': current-controlled equation C[]/Cport[] not supported (Evaluate is voltage-controlled)");

            // Silently skip noise entries (In, Nc — out of v1 scope, don't affect solve)
            if (RxNoise.IsMatch(key)) continue;

            var m = RxCurrentEq.Match(key);
            if (!m.Success) continue;  // metadata, numeric params already handled above

            int p = int.Parse(m.Groups[1].Value);
            int w = int.Parse(m.Groups[2].Value);

            if (w >= 2)
                throw new InvalidOperationException(
                    $"SDD '{sddName}': weighting w≥2 (H[w]) not supported in v1 (got I[{p},{w}])");

            if (p < 1 || p > portCount)
                throw new InvalidOperationException(
                    $"SDD '{sddName}': port index p={p} out of range (1..{portCount}) in I[{p},{w}]");

            if (kv.Value.Kind != ValueKind.String)
                throw new InvalidOperationException(
                    $"SDD '{sddName}': I[{p},{w}] must be stored as a String expression, got {kv.Value.Kind}");

            var ast = Parser.Parse(kv.Value.AsString());

            if (w == 0) currentAst[p - 1] = ast;
            else        chargeAst [p - 1] = ast;  // w == 1: charge equation
        }

        return new SddModel(sddName, portCount, currentAst, chargeAst, numericParams);
    }
}
