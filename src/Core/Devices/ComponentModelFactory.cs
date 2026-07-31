using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Devices.Microstrip;
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
            { "Vdc",   () => new VdcModel() },
            { "Port",  () => new PortModel()          },
            { "Term",  () => new TermModel()          },
            { "Short",  () => new ShortModel()          },
            { "IProbe", () => new IProbeModel()        },
        };

    // Types that require resolved parameters at construction time.
    private static readonly HashSet<string> _parameterizedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SnP", "Mutual", "SDD", "Z_Port", "V_1Tone", "V_nTone", "Tuner", "P1Tone", "PnTone",
            "NonlinearC", "TLIN", "MLIN", "MBEND", "MTEE", "MCROSS", "MTAPER", "MKLOPF", "Chain",
            "ExtDevice",
        };

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
        if (typeName.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
            return CreateTunerModel(parameters);
        if (typeName.Equals("P1Tone", StringComparison.OrdinalIgnoreCase))
            return CreateP1ToneModel(parameters);
        if (typeName.Equals("PnTone", StringComparison.OrdinalIgnoreCase))
            return CreatePnToneModel(parameters);
        if (typeName.Equals("NonlinearC", StringComparison.OrdinalIgnoreCase))
            return CreateNonlinearCModel(parameters);
        if (typeName.Equals("TLIN", StringComparison.OrdinalIgnoreCase))
            return CreateTLineModel(parameters);
        if (typeName.Equals("MLIN", StringComparison.OrdinalIgnoreCase))
            return CreateMicrostripLineModel(parameters);
        if (typeName.Equals("MBEND", StringComparison.OrdinalIgnoreCase))
            return CreateMicrostripBendModel(parameters);
        if (typeName.Equals("MTEE", StringComparison.OrdinalIgnoreCase))
            return CreateMicrostripTeeModel(parameters);
        if (typeName.Equals("MCROSS", StringComparison.OrdinalIgnoreCase))
            return CreateMicrostripCrossModel(parameters);
        if (typeName.Equals("MTAPER", StringComparison.OrdinalIgnoreCase))
            return CreateMicrostripTaperModel(parameters);
        if (typeName.Equals("MKLOPF", StringComparison.OrdinalIgnoreCase))
            return CreateMicrostripKlopfModel(parameters);
        if (typeName.Equals("ExtDevice", StringComparison.OrdinalIgnoreCase))
            return CreateExternalDeviceModel(parameters);
        if (typeName.Equals("Chain", StringComparison.OrdinalIgnoreCase))
            return CreateChainModel(parameters);
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

    /// <summary>
    /// ExtDevice: a device supplied by a registered external provider.
    ///
    /// Two reserved parameter names — Provider (which registered provider) and Type (which device
    /// type it exposes) — select the device. EVERY other parameter is forwarded to the provider
    /// verbatim, matched by the names the provider's own descriptor declared. circuitRF never
    /// interprets them, which is what lets one generic component serve any provider.
    /// </summary>
    private static ExternalDeviceModel CreateExternalDeviceModel(
        IReadOnlyDictionary<string, Value> parameters)
    {
        if (!parameters.TryGetValue("Provider", out var pv) || pv.Kind != ValueKind.String)
            throw new ExternalDeviceException(
                "ExtDevice: the 'Provider' parameter is missing — it names the registered device " +
                "provider to load this device from.");
        if (!parameters.TryGetValue("Type", out var tv) || tv.Kind != ValueKind.String)
            throw new ExternalDeviceException(
                "ExtDevice: the 'Type' parameter is missing — it names the device type to create.");

        string providerName = pv.AsString();
        string typeId       = tv.AsString();
        var    provider     = ExternalDeviceRegistry.Require(providerName);

        var descriptor = provider.Describe()
            .FirstOrDefault(d => string.Equals(d.TypeId, typeId, StringComparison.Ordinal))
            ?? throw new ExternalDeviceException(
                $"ExtDevice: provider '{providerName}' does not expose a device type '{typeId}'. " +
                $"Available: {string.Join(", ", provider.Describe().Select(d => d.TypeId))}.");

        // Forward everything except the two selectors, stringified. A provider declares its own
        // parameter kinds; transporting as text keeps this layer free of any per-provider typing.
        var forwarded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, val) in parameters)
        {
            if (key.Equals("Provider", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Type",     StringComparison.OrdinalIgnoreCase)) continue;
            forwarded[key] = val.Kind == ValueKind.String
                ? val.AsString()
                : val.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        string label = parameters.TryGetValue("__instanceLabel", out var lv) && lv.Kind == ValueKind.String
                       ? lv.AsString() : typeId;

        var instance = provider.Create(typeId, forwarded);
        if (instance.Descriptor.NodeCount != descriptor.NodeCount)
            throw new ExternalDeviceException(
                $"ExtDevice: provider '{providerName}' described type '{typeId}' with " +
                $"{descriptor.NodeCount} nodes but created an instance with " +
                $"{instance.Descriptor.NodeCount}.");

        return new ExternalDeviceModel(instance, providerName, label);
    }

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

    // ── Chain (ABCD two-port) ────────────────────────────────────────────────

    private static ChainModel CreateChainModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string name = parameters.TryGetValue("ChainName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "Chain";

        Expr? Pick(string key) =>
            parameters.TryGetValue(key, out var v) && v.Kind == ValueKind.String
                ? Parser.Parse(v.AsString()) : null;

        var numericParams = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var kv in parameters)
        {
            if (kv.Key is "ChainName" or "A" or "B" or "C" or "D") continue;
            if (kv.Value.Kind is ValueKind.Real or ValueKind.Complex)
                numericParams[kv.Key] = kv.Value;
        }

        return new ChainModel(Pick("A"), Pick("B"), Pick("C"), Pick("D"), numericParams, name);
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

    // ── Tuner ─────────────────────────────────────────────────────────────────

    private static readonly Regex RxTunerZ = new(@"^Z\[(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxTunerG = new(@"^G\[(\d+)\]$", RegexOptions.Compiled);

    private static TunerModel CreateTunerModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string instanceName = parameters.TryGetValue("TunerName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "Tuner";

        double z0 = parameters.TryGetValue("Z0", out var z0v) && z0v.Kind == ValueKind.Real
            ? z0v.AsReal() : 50.0;

        // Zdefault: catch-all for harmonics not declared.
        Complex zDefault = new(1e-6, 0);
        if (parameters.TryGetValue("Zdefault", out var zdv))
            zDefault = ToComplex(zdv);

        bool   hasBiasTee = false;
        double vbias      = 0.0;
        if (parameters.TryGetValue("BiasTee", out var btv) &&
            btv.Kind == ValueKind.String && btv.AsString().Equals("on", StringComparison.OrdinalIgnoreCase))
            hasBiasTee = true;
        if (parameters.TryGetValue("Vbias", out var vbv) && vbv.Kind == ValueKind.Real)
            vbias = vbv.AsReal();

        // Collect per-harmonic Z and G entries; detect same-harmonic Z+G conflict.
        var harmonicZ = new Dictionary<int, Complex>();
        var hasZ      = new HashSet<int>();
        var hasG      = new HashSet<int>();

        bool hasZ1 = false;

        foreach (var kv in parameters)
        {
            var mz = RxTunerZ.Match(kv.Key);
            if (mz.Success)
            {
                int k = int.Parse(mz.Groups[1].Value);
                if (hasG.Contains(k))
                    throw new InvalidOperationException(
                        $"Tuner '{instanceName}': harmonic {k} has both Z[{k}] and G[{k}] — only one allowed.");
                harmonicZ[k] = ToComplex(kv.Value);
                hasZ.Add(k);
                if (k == 1) hasZ1 = true;
                continue;
            }
            var mg = RxTunerG.Match(kv.Key);
            if (mg.Success)
            {
                int k = int.Parse(mg.Groups[1].Value);
                if (hasZ.Contains(k))
                    throw new InvalidOperationException(
                        $"Tuner '{instanceName}': harmonic {k} has both Z[{k}] and G[{k}] — only one allowed.");
                // Convert Γ → Z: Z = Z0·(1+Γ)/(1−Γ)
                var gamma = ToComplex(kv.Value);
                var one   = Complex.One;
                harmonicZ[k] = z0 * (one + gamma) / (one - gamma);
                hasG.Add(k);
                if (k == 1) hasZ1 = true;
                continue;
            }
        }

        if (!hasZ1)
            throw new InvalidOperationException(
                $"Tuner '{instanceName}': Z[1] or G[1] is required (the fundamental termination must be specified).");

        return new TunerModel(instanceName, harmonicZ, zDefault, hasBiasTee, vbias);
    }

    // ── P1Tone ────────────────────────────────────────────────────────────────

    private static P1ToneModel CreateP1ToneModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string instanceName = parameters.TryGetValue("P1ToneName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "P1Tone";

        // Z is both the Zdefault (catch-all) and the Z0 for Γ→Z conversion.
        double z0 = parameters.TryGetValue("Z", out var zv) && zv.Kind == ValueKind.Real
            ? zv.AsReal() : 50.0;
        var zDefault = new Complex(z0, 0);

        double pavlDbm = GetReal(parameters, "Pavl",  0.0);
        double freqHz  = GetReal(parameters, "Freq",  1e9);
        double phaseDeg = GetReal(parameters, "Phase", 0.0);

        // Collect per-harmonic Z[k] and G[k] entries (same logic as Tuner).
        var harmonicZ = new Dictionary<int, Complex>();
        var hasZ      = new HashSet<int>();
        var hasG      = new HashSet<int>();

        foreach (var kv in parameters)
        {
            var mz = RxTunerZ.Match(kv.Key);
            if (mz.Success)
            {
                int k = int.Parse(mz.Groups[1].Value);
                if (hasG.Contains(k))
                    throw new InvalidOperationException(
                        $"P1Tone '{instanceName}': harmonic {k} has both Z[{k}] and G[{k}] — only one allowed.");
                harmonicZ[k] = ToComplex(kv.Value);
                hasZ.Add(k);
                continue;
            }
            var mg = RxTunerG.Match(kv.Key);
            if (mg.Success)
            {
                int k = int.Parse(mg.Groups[1].Value);
                if (hasZ.Contains(k))
                    throw new InvalidOperationException(
                        $"P1Tone '{instanceName}': harmonic {k} has both Z[{k}] and G[{k}] — only one allowed.");
                // Γ → Z: Z = Z0·(1+Γ)/(1−Γ)
                var gamma = ToComplex(kv.Value);
                harmonicZ[k] = z0 * (Complex.One + gamma) / (Complex.One - gamma);
                hasG.Add(k);
                continue;
            }
        }

        return new P1ToneModel(instanceName, harmonicZ, zDefault, pavlDbm, freqHz, phaseDeg);
    }

    // ── PnTone (multi-tone power source) ──────────────────────────────────────────

    private static PnToneModel CreatePnToneModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string instanceName = parameters.TryGetValue("PnToneName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "PnTone";

        // Z is both the Zdefault (catch-all) and the Z0 for Γ→Z conversion (same as P1Tone).
        double z0 = parameters.TryGetValue("Z", out var zv) && zv.Kind == ValueKind.Real
            ? zv.AsReal() : 50.0;
        var zDefault = new Complex(z0, 0);

        // Scan consecutive tones Freq[i]/Pavl[i]/Phase[i], i = 1, 2, … until Freq[i] is absent.
        // (Mirrors how the parameter editor's "+" adds indexed tone groups; no NumFreqs needed.)
        var tones = new List<PnToneModel.Tone>();
        for (int i = 1; ; i++)
        {
            if (!parameters.TryGetValue($"Freq[{i}]", out var fv) || fv.Kind != ValueKind.Real) break;
            double pavl  = GetReal(parameters, $"Pavl[{i}]",  0.0);
            double phase = GetReal(parameters, $"Phase[{i}]", 0.0);
            tones.Add(new PnToneModel.Tone(pavl, fv.AsReal(), phase));
        }
        // Fallback: a scalar Freq (P1Tone-style single tone) lets a degenerate PnTone still resolve.
        if (tones.Count == 0 && parameters.TryGetValue("Freq", out var f0) && f0.Kind == ValueKind.Real)
            tones.Add(new PnToneModel.Tone(GetReal(parameters, "Pavl", 0.0), f0.AsReal(),
                                           GetReal(parameters, "Phase", 0.0)));

        // Per-harmonic-band Z[k]/G[k] terminations (shared across tones; same logic as P1Tone).
        var harmonicZ = new Dictionary<int, Complex>();
        var hasZ      = new HashSet<int>();
        var hasG      = new HashSet<int>();
        foreach (var kv in parameters)
        {
            var mz = RxTunerZ.Match(kv.Key);
            if (mz.Success)
            {
                int k = int.Parse(mz.Groups[1].Value);
                if (hasG.Contains(k))
                    throw new InvalidOperationException(
                        $"PnTone '{instanceName}': harmonic {k} has both Z[{k}] and G[{k}] — only one allowed.");
                harmonicZ[k] = ToComplex(kv.Value);
                hasZ.Add(k);
                continue;
            }
            var mg = RxTunerG.Match(kv.Key);
            if (mg.Success)
            {
                int k = int.Parse(mg.Groups[1].Value);
                if (hasZ.Contains(k))
                    throw new InvalidOperationException(
                        $"PnTone '{instanceName}': harmonic {k} has both Z[{k}] and G[{k}] — only one allowed.");
                var gamma = ToComplex(kv.Value);
                harmonicZ[k] = z0 * (Complex.One + gamma) / (Complex.One - gamma);
                hasG.Add(k);
            }
        }

        return new PnToneModel(instanceName, tones.ToArray(), harmonicZ, zDefault);
    }

    // ── NonlinearC ────────────────────────────────────────────────────────────

    private static NonlinearCModel CreateNonlinearCModel(IReadOnlyDictionary<string, Value> parameters)
    {
        // Read C0, C1, … consecutively; stop at the first absent index. Absent ⇒ implicitly 0
        // (so trailing zeros may be omitted). No C0 at all ⇒ degenerate 0 F cap (allowed; warns elsewhere).
        var coeffs = new List<double>();
        for (int k = 0; ; k++)
        {
            if (!parameters.TryGetValue($"C{k}", out var val) || val.Kind != ValueKind.Real) break;
            coeffs.Add(val.AsReal());
        }
        return new NonlinearCModel(coeffs.Count > 0 ? coeffs.ToArray() : [0.0]);
    }

    // ── TLIN (ideal/lossless transmission line) ─────────────────────────────────

    private static TLineModel CreateTLineModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string name = parameters.TryGetValue("TLineName", out var nm) && nm.Kind == ValueKind.String
            ? nm.AsString() : "TLIN";

        // Units already applied by the elaborator: Z in Ω, F in Hz, and E in RADIANS.
        // The elaborator's generic parameter path multiplies the authored value by the angle
        // unit's scale (Units.Scale("deg") = π/180), so an authored "E=90 deg" arrives here as
        // π/2 ≈ 1.5708. TLineModel consumes E as radians directly — it does NOT re-apply π/180.
        // (Defaults are quoted in the SAME post-scale convention: 90° = π/2 rad.)
        double z0     = GetReal(parameters, "Z", 50.0);
        double eRad   = GetReal(parameters, "E", Math.PI / 2.0);
        double fRefHz = GetReal(parameters, "F", 1e9);
        // A = total attenuation at F, in dB. Optional, additive (TLineModel.cs doc comment);
        // absent ⇒ 0 ⇒ byte-identical lossless behavior for every pre-existing "TLIN:" instance.
        double aDb    = GetReal(parameters, "A", 0.0);

        return new TLineModel(z0, eRad, fRefHz, name, aDb);
    }

    // ── MLIN / MBend / MTee / MCross (brief-L5a-pcell-contract-and-microstrip.md) ────────────────
    // W/L/H/T are SI metres (the elaborator applies the "m/mm/um/mil" unit scale, matching TLIN's
    // own length-unit handling). H/T/Er/Sigma/TanD default to the PCB starter technology's own FR-4
    // substrate (StarterTechnologies.Pcb2Layer) so a bare ".cnl" instance with no explicit substrate
    // override still constructs something physically sensible; a real schematic instance gets its
    // true substrate injected as explicit overrides by the resolver in src/Ui/Schematic/ before this
    // factory ever runs (Core never resolves a workspace technology itself).
    // Public so a UI-layer "what substrate am I actually simulating against" computation (the
    // MKlopf entry-mode switch in ParameterEditorViewModel, which converts Z1/Z2<->W1/W2 and
    // L<->F3db) can mirror the SAME fallback this factory uses when no H/T/Er override is present —
    // one set of default numbers, not two.
    public const double DefaultSubstrateHMeters = 1.6e-3;
    public const double DefaultSubstrateTMeters = 35e-6;
    public const double DefaultSubstrateEpsR = 4.4;
    public const double DefaultSubstrateSigmaSPerM = 5.8e7;
    public const double DefaultSubstrateTanD = 0.02;

    private static string MicrostripInstanceName(IReadOnlyDictionary<string, Value> parameters, string fallback)
        => parameters.TryGetValue("Name", out var nm) && nm.Kind == ValueKind.String ? nm.AsString() : fallback;

    private static MicrostripLineModel CreateMicrostripLineModel(IReadOnlyDictionary<string, Value> parameters)
    {
        double w = GetReal(parameters, "W", 0.0);
        double l = GetReal(parameters, "L", 0.0);
        double h = GetReal(parameters, "H", DefaultSubstrateHMeters);
        double t = GetReal(parameters, "T", DefaultSubstrateTMeters);
        double er = GetReal(parameters, "Er", DefaultSubstrateEpsR);
        double sigma = GetReal(parameters, "Sigma", DefaultSubstrateSigmaSPerM);
        double tanD = GetReal(parameters, "TanD", DefaultSubstrateTanD);
        double roughness = GetReal(parameters, "Roughness", 0.0);
        string name = MicrostripInstanceName(parameters, "MLIN");

        return new MicrostripLineModel(w, l, h, t, er, sigma, tanD, name, roughness);
    }

    private static MicrostripBendModel CreateMicrostripBendModel(IReadOnlyDictionary<string, Value> parameters)
    {
        double w = GetReal(parameters, "W", 0.0);
        double angleDeg = GetReal(parameters, "Angle", 90.0);
        // "Miter" is the 3-way mode (0=None, 1=Fifty, 2=Optimal — MicrostripBendMiter's own
        // declaration order); the older boolean "Mitered" (0/1) is still accepted for backward
        // compatibility with any hand-authored .cnl predating this brief, mapped None/Optimal (the
        // shipped default before this brief always meant "the real Douville-James chamfer").
        MicrostripBendMiter miter;
        if (parameters.ContainsKey("Miter"))
            miter = (MicrostripBendMiter)(int)Math.Round(GetReal(parameters, "Miter", 0.0));
        else
            miter = GetReal(parameters, "Mitered", 0.0) != 0.0 ? MicrostripBendMiter.Optimal : MicrostripBendMiter.None;
        double h = GetReal(parameters, "H", DefaultSubstrateHMeters);
        double t = GetReal(parameters, "T", DefaultSubstrateTMeters);
        double er = GetReal(parameters, "Er", DefaultSubstrateEpsR);
        double sigma = GetReal(parameters, "Sigma", DefaultSubstrateSigmaSPerM);
        double tanD = GetReal(parameters, "TanD", DefaultSubstrateTanD);
        string name = MicrostripInstanceName(parameters, "MBEND");

        return new MicrostripBendModel(w, angleDeg, miter, h, t, er, sigma, tanD, name);
    }

    private static MicrostripTeeModel CreateMicrostripTeeModel(IReadOnlyDictionary<string, Value> parameters)
    {
        double w1 = GetReal(parameters, "W1", 0.0);
        double w2 = GetReal(parameters, "W2", 0.0);
        double w3 = GetReal(parameters, "W3", 0.0);
        double h = GetReal(parameters, "H", DefaultSubstrateHMeters);
        double t = GetReal(parameters, "T", DefaultSubstrateTMeters);
        double er = GetReal(parameters, "Er", DefaultSubstrateEpsR);
        double sigma = GetReal(parameters, "Sigma", DefaultSubstrateSigmaSPerM);
        double tanD = GetReal(parameters, "TanD", DefaultSubstrateTanD);
        string name = MicrostripInstanceName(parameters, "MTEE");

        return new MicrostripTeeModel(w1, w2, w3, h, t, er, sigma, tanD, name);
    }

    private static MicrostripCrossModel CreateMicrostripCrossModel(IReadOnlyDictionary<string, Value> parameters)
    {
        double w1 = GetReal(parameters, "W1", 0.0);
        double w2 = GetReal(parameters, "W2", 0.0);
        double w3 = GetReal(parameters, "W3", 0.0);
        double w4 = GetReal(parameters, "W4", 0.0);
        double h = GetReal(parameters, "H", DefaultSubstrateHMeters);
        double t = GetReal(parameters, "T", DefaultSubstrateTMeters);
        double er = GetReal(parameters, "Er", DefaultSubstrateEpsR);
        double sigma = GetReal(parameters, "Sigma", DefaultSubstrateSigmaSPerM);
        double tanD = GetReal(parameters, "TanD", DefaultSubstrateTanD);
        string name = MicrostripInstanceName(parameters, "MCROSS");

        return new MicrostripCrossModel(w1, w2, w3, w4, h, t, er, sigma, tanD, name);
    }

    private static MicrostripTaperModel CreateMicrostripTaperModel(IReadOnlyDictionary<string, Value> parameters)
    {
        double w1 = GetReal(parameters, "W1", 0.0);
        double w2 = GetReal(parameters, "W2", 0.0);
        double l = GetReal(parameters, "L", 0.0);
        double h = GetReal(parameters, "H", DefaultSubstrateHMeters);
        double t = GetReal(parameters, "T", DefaultSubstrateTMeters);
        double er = GetReal(parameters, "Er", DefaultSubstrateEpsR);
        double sigma = GetReal(parameters, "Sigma", DefaultSubstrateSigmaSPerM);
        double tanD = GetReal(parameters, "TanD", DefaultSubstrateTanD);
        int nOverride = (int)Math.Round(GetReal(parameters, "N", 0.0));
        string name = MicrostripInstanceName(parameters, "MTAPER");

        return new MicrostripTaperModel(w1, w2, l, h, t, er, sigma, tanD, name, nOverride);
    }

    private static MicrostripKlopfModel CreateMicrostripKlopfModel(IReadOnlyDictionary<string, Value> parameters)
    {
        double h = GetReal(parameters, "H", DefaultSubstrateHMeters);
        double t = GetReal(parameters, "T", DefaultSubstrateTMeters);
        double er = GetReal(parameters, "Er", DefaultSubstrateEpsR);
        double sigma = GetReal(parameters, "Sigma", DefaultSubstrateSigmaSPerM);
        double tanD = GetReal(parameters, "TanD", DefaultSubstrateTanD);
        var quiet = new MicrostripValidityReporter("(MKLOPF entry-route resolution, not reported)");

        // R-klp-3a: Z1/Z2 entry is authoritative whenever present (fixes the impedances; width
        // follows the technology); otherwise W1/W2 (fixes the geometry; impedance follows it).
        // The interactive "last-edited pair wins, never re-derived from the other's displayed
        // value" linking (mirroring the Scale dialog's ScaleFieldLinker) is a UI-layer concern —
        // this factory resolves deterministically from whichever pair is actually present, which
        // is what makes a technology retarget correctly change the W-entry route's design while
        // leaving the Z-entry route's design fixed (gate 4c).
        double z1, z2;
        if (parameters.ContainsKey("Z1") || parameters.ContainsKey("Z2"))
        {
            z1 = GetReal(parameters, "Z1", 50.0);
            z2 = GetReal(parameters, "Z2", 50.0);
        }
        else
        {
            double w1 = GetReal(parameters, "W1", 0.0);
            double w2 = GetReal(parameters, "W2", 0.0);
            (z1, z2) = MicrostripKlopfEntryConversion.WidthToImpedance(w1, w2, h, t, er, quiet);
        }

        double gammaMax = GetReal(parameters, "GammaMax", 0.05);
        double offset = GetReal(parameters, "Offset", 0.0);
        int nOverride = (int)Math.Round(GetReal(parameters, "N", 0.0));
        string name = MicrostripInstanceName(parameters, "MKLOPF");

        // R-klp-3: L entry is authoritative whenever present; otherwise derive it from F3db via the
        // SAME eeff-at-center conversion the UI's own entry-mode switch uses (MicrostripKlopfEntryConversion) —
        // one implementation, not two.
        double l = parameters.ContainsKey("L")
            ? GetReal(parameters, "L", 0.0)
            : MicrostripKlopfEntryConversion.F3dbToLength(
                z1, z2, gammaMax, GetReal(parameters, "F3db", 1e9), h, t, er, quiet);

        return new MicrostripKlopfModel(z1, z2, l, gammaMax, offset, h, t, er, sigma, tanD, name, nOverride);
    }

    private static Complex ToComplex(Value v)
    {
        if (v.Kind == ValueKind.Real)    return new Complex(v.AsReal(), 0);
        if (v.Kind == ValueKind.Complex) return v.AsComplex();
        throw new InvalidOperationException($"Expected numeric value, got {v.Kind}");
    }

    // Regex for I[p,w] two-index form — current (w=0), charge (w=1), or higher (w≥2).
    private static readonly Regex RxCurrentEq = new(@"^I\[(\d+),(\d+)\]$", RegexOptions.Compiled);
    // Single-index sugar: I[p] → current (w=0); Q[p] → charge (w=1).
    private static readonly Regex RxCurrentEq1 = new(@"^I\[(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxChargeEq1  = new(@"^Q\[(\d+)\]$", RegexOptions.Compiled);
    // H[w] weighting-function parameter — user-defined for w≥2 only.
    private static readonly Regex RxWeightFn = new(@"^H\[(\d+)\]$", RegexOptions.Compiled);
    // C[n] and Cport[n] — control-current references parsed in this brief.
    private static readonly Regex RxControlRef  = new(@"^C\[(\d+)\]$",     RegexOptions.Compiled);
    private static readonly Regex RxControlPort = new(@"^Cport\[(\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // _c{N} references in equations — used for cross-validation.
    private static readonly Regex RxControlVarRef = new(@"^_c(\d+)$", RegexOptions.Compiled);
    // Regex for unsupported constructs that must hard-error.
    private static readonly Regex RxImplicitEq = new(@"^F\[",  RegexOptions.Compiled);
    // Noise entries (In, Nc) — silently skip.
    private static readonly Regex RxNoise = new(@"^(In|Nc)\[", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static SddModel CreateSddModel(IReadOnlyDictionary<string, Value> parameters)
    {
        // Parameters dict for SDD (populated by Elaborator.ResolveSddParameters):
        //   "I[p,w]" → Value.String(expressionText)  — equation entries
        //   "H[w]"   → Value.String(expressionText)  — weight-function entries (w≥2)
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
        var higherAst  = new List<(int W, Expr Ast)>[portCount];
        for (int k = 0; k < portCount; k++) higherAst[k] = [];
        var weightAst  = new Dictionary<int, Expr>();

        // Control-current references: N → (RefInstance, Port) where Port=0 means Cport absent.
        var controlRefInsts = new Dictionary<int, string>();
        var controlRefPorts = new Dictionary<int, int>();

        foreach (var kv in parameters)
        {
            var key = kv.Key;

            // Hard error for implicit F[...] equations (unsupported physics).
            if (RxImplicitEq.IsMatch(key))
                throw new InvalidOperationException(
                    $"SDD '{sddName}': implicit equation F[...] not supported; use I[...] for explicit current");

            // Silently skip noise entries (In, Nc — out of v1 scope, don't affect solve)
            if (RxNoise.IsMatch(key)) continue;

            // C[n]=<instanceName> — control-current instance reference.
            var mCref = RxControlRef.Match(key);
            if (mCref.Success)
            {
                int n = int.Parse(mCref.Groups[1].Value);
                if (n < 1)
                    throw new InvalidOperationException($"SDD '{sddName}': C[{n}] index must be ≥ 1");
                string refInst = kv.Value.Kind == ValueKind.String
                    ? kv.Value.AsString().Trim()
                    : throw new InvalidOperationException(
                        $"SDD '{sddName}': C[{n}] value must be a String (instance name), got {kv.Value.Kind}");
                controlRefInsts[n] = refInst;
                continue;
            }

            // Cport[n]=<port> — port selector for multi-port control reference.
            var mCport = RxControlPort.Match(key);
            if (mCport.Success)
            {
                int n = int.Parse(mCport.Groups[1].Value);
                string portStr = kv.Value.Kind == ValueKind.String
                    ? kv.Value.AsString().Trim()
                    : throw new InvalidOperationException(
                        $"SDD '{sddName}': Cport[{n}] value must be a String, got {kv.Value.Kind}");
                if (!int.TryParse(portStr, out int portNum) || portNum < 1)
                    throw new InvalidOperationException(
                        $"SDD '{sddName}': Cport[{n}]={portStr} is not a valid port number (≥1)");
                controlRefPorts[n] = portNum;
                continue;
            }

            // H[w] weighting-function parameter
            var mH = RxWeightFn.Match(key);
            if (mH.Success)
            {
                int w = int.Parse(mH.Groups[1].Value);
                if (w < 2)
                    throw new InvalidOperationException(
                        $"SDD '{sddName}': H[{w}] is a built-in weighting function and cannot be redefined");
                if (kv.Value.Kind != ValueKind.String)
                    throw new InvalidOperationException(
                        $"SDD '{sddName}': H[{w}] must be stored as a String expression, got {kv.Value.Kind}");
                weightAst[w] = Parser.Parse(kv.Value.AsString());
                continue;
            }

            // Two-index form: I[p,w]
            var m = RxCurrentEq.Match(key);
            if (m.Success)
            {
                int p = int.Parse(m.Groups[1].Value);
                int w = int.Parse(m.Groups[2].Value);
                if (w == 0)
                {
                    ValidateAndBind(key, p, portCount, kv.Value, sddName, currentAst);
                }
                else if (w == 1)
                {
                    ValidateAndBind(key, p, portCount, kv.Value, sddName, chargeAst);
                }
                else
                {
                    // w≥2 — validate port range and store in per-port higher-bucket list.
                    if (p < 1 || p > portCount)
                        throw new InvalidOperationException(
                            $"SDD '{sddName}': equation references port {p} but only {portCount} port(s) of nets were given" +
                            $" (need {p * 2} nets for a {p}-port SDD: p1+ p1− … p{p}+ p{p}−)");
                    if (kv.Value.Kind != ValueKind.String)
                        throw new InvalidOperationException(
                            $"SDD '{sddName}': {key} must be stored as a String expression, got {kv.Value.Kind}");
                    higherAst[p - 1].Add((w, Parser.Parse(kv.Value.AsString())));
                }
                continue;
            }

            // Single-index sugar: I[p] → current (w=0)
            var m1 = RxCurrentEq1.Match(key);
            if (m1.Success)
            {
                ValidateAndBind(key, int.Parse(m1.Groups[1].Value), portCount, kv.Value, sddName, currentAst);
                continue;
            }

            // Single-index sugar: Q[p] → charge (w=1)
            var m2 = RxChargeEq1.Match(key);
            if (m2.Success)
            {
                ValidateAndBind(key, int.Parse(m2.Groups[1].Value), portCount, kv.Value, sddName, chargeAst);
                continue;
            }

            // key is SddName, SddPortCount, H[w], or a resolved numeric param — already handled above
        }

        // Cross-validate: every w≥2 referenced by some I[p,w] must have a matching H[w] declared.
        var referencedW = new HashSet<int>();
        foreach (var list in higherAst)
            foreach (var (w, _) in list)
                referencedW.Add(w);
        foreach (int w in referencedW)
            if (!weightAst.ContainsKey(w))
                throw new InvalidOperationException(
                    $"SDD '{sddName}': I[p,{w}] references weighting H[{w}] which is not defined");

        // Cross-validate: every _cn referenced in any equation must have a C[n] entry.
        var controlVarRefs = new HashSet<int>();
        void CollectControlVarRefs(Expr? ast)
        {
            if (ast is null) return;
            foreach (var name in AstWalker.CollectRefs(ast))
            {
                var mCtrl = RxControlVarRef.Match(name);
                if (mCtrl.Success) controlVarRefs.Add(int.Parse(mCtrl.Groups[1].Value));
            }
        }
        foreach (var ast in currentAst) CollectControlVarRefs(ast);
        foreach (var ast in chargeAst)  CollectControlVarRefs(ast);
        foreach (var list in higherAst)
            foreach (var (_, ast) in list) CollectControlVarRefs(ast);

        foreach (int refN in controlVarRefs)
            if (!controlRefInsts.ContainsKey(refN))
                throw new InvalidOperationException(
                    $"SDD '{sddName}': equation references '_c{refN}' but C[{refN}] is not defined");

        // Build sorted control-refs list (by N).
        var controlRefs = controlRefInsts
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value, controlRefPorts.GetValueOrDefault(kv.Key, 0)))
            .ToList();

        var higherAstRo = Array.ConvertAll(higherAst,
            list => (IReadOnlyList<(int W, Expr Ast)>)list);

        return new SddModel(sddName, portCount, currentAst, chargeAst, numericParams, higherAstRo, weightAst, controlRefs);
    }

    private static void ValidateAndBind(
        string key, int p, int portCount, Value val, string sddName, Expr?[] target)
    {
        if (p < 1 || p > portCount)
            throw new InvalidOperationException(
                $"SDD '{sddName}': equation references port {p} but only {portCount} port(s) of nets were given" +
                $" (need {p * 2} nets for a {p}-port SDD: p1+ p1− … p{p}+ p{p}−)");
        if (val.Kind != ValueKind.String)
            throw new InvalidOperationException(
                $"SDD '{sddName}': {key} must be stored as a String expression, got {val.Kind}");
        target[p - 1] = Parser.Parse(val.AsString());
    }
}
