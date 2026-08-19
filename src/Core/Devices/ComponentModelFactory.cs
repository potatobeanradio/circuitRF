using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using RfCore;

using CircuitRF.WBond;

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
            "NonlinearC", "Diode", "SemiC", "VerilogA",
            "FET_Curtice", "FET_CurticeCubic", "FET_Statz", "FET_Materka", "FET_Angelov",
            "TLIN", "MLIN", "MBEND", "MTEE", "MCROSS", "MTAPER", "MKLOPF", "Chain",
            "ExtDevice", "wBond",
        };

    /// <summary>
    /// Returns a new ComponentModel, using resolved parameters when needed.
    /// Returns null only if the type name is not a known primitive (i.e. it is a sub-cell).
    /// </summary>
    /// <param name="functions">
    /// User-defined expression functions declared by the netlist being elaborated. Models that
    /// evaluate an expression at STAMP time (per frequency) need these: they build their own
    /// <c>Evaluator</c> long after elaboration, and one constructed empty cannot resolve a call to
    /// a function the netlist declared. Passing them here keeps the table tied to the netlist that
    /// declared it, so two designs open at once cannot see each other's functions.
    /// </param>
    /// <param name="ambientC">
    /// The design's ambient temperature in °C — what a device is evaluated at when it states no
    /// temperature of its own. Defaults to <see cref="Temperature.NominalC"/>, so a caller that
    /// does not supply one gets exactly the behaviour that predates ambient support: every device
    /// sits at its own extraction point and every temperature relation collapses to the identity.
    /// </param>
    public static ComponentModel? TryCreate(string typeName,
        IReadOnlyDictionary<string, Value> parameters,
        IReadOnlyList<UserFunction>? functions = null,
        double ambientC = Temperature.NominalC)
    {
        if (typeName.Equals("SnP",    StringComparison.OrdinalIgnoreCase))
            return CreateSnpModel(parameters);
        if (typeName.Equals("Mutual", StringComparison.OrdinalIgnoreCase))
            return CreateMutualModel(parameters);
        if (typeName.Equals("SDD",    StringComparison.OrdinalIgnoreCase))
            return CreateSddModel(parameters, functions);
        if (typeName.Equals("Z_Port", StringComparison.OrdinalIgnoreCase))
            return CreateZPortModel(parameters, functions);
        if (typeName.Equals("V_1Tone", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("V_nTone", StringComparison.OrdinalIgnoreCase))
            return CreateToneSourceModel(typeName, parameters, functions);
        if (typeName.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
            return CreateTunerModel(parameters);
        if (typeName.Equals("P1Tone", StringComparison.OrdinalIgnoreCase))
            return CreateP1ToneModel(parameters);
        if (typeName.Equals("PnTone", StringComparison.OrdinalIgnoreCase))
            return CreatePnToneModel(parameters);
        if (typeName.Equals("NonlinearC", StringComparison.OrdinalIgnoreCase))
            return CreateNonlinearCModel(parameters);
        if (typeName.Equals("Diode", StringComparison.OrdinalIgnoreCase))
            return CreateDiodeModel(parameters, ambientC);
        if (typeName.Equals("R", StringComparison.OrdinalIgnoreCase))
            return CreateResistorModel(parameters, ambientC);
        if (typeName.Equals("SemiC", StringComparison.OrdinalIgnoreCase))
            return CreateSemiCapacitorModel(parameters, ambientC);
        if (typeName.StartsWith("FET_", StringComparison.OrdinalIgnoreCase))
            return CreateFetModel(typeName, parameters, ambientC);
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
        if (typeName.Equals("VerilogA", StringComparison.OrdinalIgnoreCase))
            return CreateVerilogAModel(parameters, ambientC);
        if (typeName.Equals("Chain", StringComparison.OrdinalIgnoreCase))
            return CreateChainModel(parameters, functions);
        if (typeName.Equals("wBond", StringComparison.OrdinalIgnoreCase))
            return CreateWBondModel(parameters);
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
    /// Which key on an instance is circuitRF's reserved <paramref name="reserved"/> selector, or null
    /// when nothing spells it. Exact spelling first, then a case-insensitive match.
    ///
    /// <para><b>Why the exact spelling has to win, rather than matching case-insensitively outright.</b>
    /// A compiled model may genuinely declare a parameter called <c>TYPE</c> — a real MOS compact
    /// model uses it for the channel polarity — and a case-blind rule then eats it as circuitRF's own
    /// device-type selector. The device still builds, still solves, and is a different transistor.
    /// Preferring the exact spelling keeps the two apart when both are present while leaving a design
    /// that writes <c>type=</c> for the selector working exactly as before, since such a design has no
    /// other parameter of that name for it to be confused with.</para>
    /// </summary>
    public static string? ReservedKey(IEnumerable<string> names, string reserved)
    {
        ArgumentNullException.ThrowIfNull(names);

        string? loose = null;
        foreach (string n in names)
        {
            if (string.Equals(n, reserved, StringComparison.Ordinal)) return n;
            loose ??= string.Equals(n, reserved, StringComparison.OrdinalIgnoreCase) ? n : null;
        }
        return loose;
    }

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
        // Resolved ONCE, by key, so that a model parameter which merely differs in case from a
        // selector is forwarded rather than swallowed — see ReservedKey.
        string? providerKey = ReservedKey(parameters.Keys, "Provider");
        string? typeKey     = ReservedKey(parameters.Keys, "Type");

        if (providerKey is null || !parameters.TryGetValue(providerKey, out var pv) || pv.Kind != ValueKind.String)
            throw new ExternalDeviceException(
                "ExtDevice: the 'Provider' parameter is missing — it names the registered device " +
                "provider to load this device from.");
        if (typeKey is null || !parameters.TryGetValue(typeKey, out var tv) || tv.Kind != ValueKind.String)
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
        //
        // Parameters prefixed "__" are circuitRF's own plumbing (e.g. __instanceLabel, read just
        // below) and are NOT part of "everything else". Forwarding them asks a provider to accept a
        // name it never declared — which a permissive provider ignores, and a strict one rejects,
        // failing every device it serves. The strict behaviour is the correct one: it is what turns
        // a user's misspelled parameter into an error instead of a silently defaulted device.
        var forwarded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, val) in parameters)
        {
            if (key.StartsWith("__", StringComparison.Ordinal)) continue;
            if (string.Equals(key, providerKey, StringComparison.Ordinal) ||
                string.Equals(key, typeKey,     StringComparison.Ordinal)) continue;
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

    // ── VerilogA — a compiled model the USER named, with no kit involved ──────

    /// <summary>Names the compiled model file. A file picker in the parameter dialog fills it in.</summary>
    public const string VerilogAFileParam = "File";

    /// <summary>Selects one device type inside that file. Optional when the file declares exactly one.</summary>
    public const string VerilogAModelParam = "Model";

    /// <summary>
    /// How many terminals the SYMBOL draws. circuitRF's own, not the model's: the schematic has to
    /// know before anything has opened the file. Never forwarded — a model asked to accept a
    /// parameter it never declared refuses, which would fail every device it serves.
    /// </summary>
    public const string VerilogAPinsParam = "Pins";

    /// <summary>
    /// A compiled compact model a user placed on a schematic and pointed at their own file.
    ///
    /// <para><b>The difference from <c>ExtDevice</c> is who supplies the model.</b> That one names a
    /// PROVIDER — a kit that was installed, with a manifest saying which program evaluates its
    /// devices. This one names a FILE, and there is no kit, no manifest and nothing to install: a
    /// user compiles their own Verilog-A with their own compiler and places it. Everything below the
    /// provider seam is identical, which is the point — an externally-supplied device is an ordinary
    /// nonlinear component either way.</para>
    ///
    /// <para><b>Every parameter but the two selectors is forwarded verbatim</b>, matched against the
    /// names the model's own descriptor declares. circuitRF interprets none of them: a compact model
    /// has hundreds and they belong to its author.</para>
    /// </summary>
    private static ExternalDeviceModel CreateVerilogAModel(
        IReadOnlyDictionary<string, Value> parameters, double ambientC)
    {
        if (!parameters.TryGetValue(VerilogAFileParam, out var fv) ||
            fv.Kind != ValueKind.String || fv.AsString().Trim().Length == 0)
            throw new ExternalDeviceException(
                "VerilogA: no model file. Set the component's 'File' parameter to a compiled model — " +
                "circuitRF loads one you built, it does not compile Verilog-A itself.");

        string modelFile   = fv.AsString().Trim();
        string providerName = VerilogAFileResolver.ProviderNameFor(modelFile);
        var    provider     = ExternalDeviceRegistry.Require(providerName);

        var offered = provider.Describe();
        if (offered.Count == 0)
            throw new ExternalDeviceException(
                $"VerilogA: '{modelFile}' declares no device type. It loaded, so it is a compiled " +
                "model file, but there is nothing in it to place.");

        // The type is optional when there is no ambiguity — a model file usually holds one device,
        // and asking the user to name it as well as find it is a step that answers itself.
        string typeId;
        if (parameters.TryGetValue(VerilogAModelParam, out var tv) &&
            tv.Kind == ValueKind.String && tv.AsString().Trim().Length > 0)
        {
            typeId = tv.AsString().Trim();
            if (!offered.Any(d => string.Equals(d.TypeId, typeId, StringComparison.Ordinal)))
                throw new ExternalDeviceException(
                    $"VerilogA: '{modelFile}' has no device type '{typeId}'. It offers: " +
                    $"{string.Join(", ", offered.Select(d => d.TypeId))}.");
        }
        else if (offered.Count == 1)
        {
            typeId = offered[0].TypeId;
        }
        else
        {
            throw new ExternalDeviceException(
                $"VerilogA: '{modelFile}' declares {offered.Count} device types, so which one to " +
                $"place has to be stated. Set 'Model' to one of: " +
                $"{string.Join(", ", offered.Select(d => d.TypeId))}.");
        }

        var forwarded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, val) in parameters)
        {
            // `__`-prefixed names are circuitRF's own plumbing and are not part of "everything
            // else"; forwarding one asks the model to accept a name it never declared.
            if (key.StartsWith(ExternalDeviceProviderReservedPrefix, StringComparison.Ordinal)) continue;
            if (key.Equals(VerilogAFileParam,  StringComparison.OrdinalIgnoreCase) ||
                key.Equals(VerilogAModelParam, StringComparison.OrdinalIgnoreCase) ||
                key.Equals(VerilogAPinsParam,  StringComparison.OrdinalIgnoreCase)) continue;
            // Temperature is not a model parameter — it rides as its own reserved field below.
            if (key.Equals(Temperature.AbsoluteParamName, StringComparison.Ordinal) ||
                key.Equals(Temperature.DeltaParamName,    StringComparison.Ordinal)) continue;

            forwarded[key] = val.Kind == ValueKind.String
                ? val.AsString()
                : val.AsReal().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        // The device's own temperature, in KELVIN, through the reserved key — the same one rule the
        // diode and the FET family resolve through, so no two devices in one design can disagree.
        forwarded[DeviceWorkerProvider.ReservedTemperatureKey] =
            Temperature.ToKelvin(Temperature.ResolveDeviceC(parameters, ambientC))
                       .ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        string label = parameters.TryGetValue("__instanceLabel", out var lv) && lv.Kind == ValueKind.String
                       ? lv.AsString() : typeId;

        return new ExternalDeviceModel(provider.Create(typeId, forwarded), providerName, label);
    }

    private const string ExternalDeviceProviderReservedPrefix = DeviceWorkerProvider.ReservedPrefix;

    private static SnpModel CreateSnpModel(IReadOnlyDictionary<string, Value> parameters)
    {
        if (!parameters.TryGetValue("NumPorts", out var np) || np.Kind != ValueKind.Real)
            throw new InvalidOperationException("SnP: NumPorts parameter is missing or not a number");
        int portCount = (int)np.AsReal();

        if (!parameters.TryGetValue("File", out var fileVal) || fileVal.Kind != ValueKind.String)
            throw new InvalidOperationException("SnP: File parameter is missing or not a string");
        string filePath = fileVal.AsString();

        // Default is cubic spline — anything other than an explicit "linear"/"makima" falls back to
        // it, which also covers the pre-existing stored value "Cubic".
        var interpMethod = InterpolationMethod.CubicSpline;
        if (parameters.TryGetValue("InterpMode", out var im) && im.Kind == ValueKind.String)
            interpMethod = im.AsString() switch
            {
                var s when s.Equals("linear", StringComparison.OrdinalIgnoreCase) => InterpolationMethod.Linear,
                var s when s.Equals("makima", StringComparison.OrdinalIgnoreCase) => InterpolationMethod.Makima,
                _ => InterpolationMethod.CubicSpline,
            };

        // MA (magnitude/angle, default) or RI (real/imaginary) — anything other than an explicit
        // "RI" falls back to MA.
        var interpFormat = InterpolationFormat.MagPhase;
        if (parameters.TryGetValue("InterpDomain", out var id) && id.Kind == ValueKind.String)
            interpFormat = id.AsString().Equals("RI", StringComparison.OrdinalIgnoreCase)
                ? InterpolationFormat.RealImag
                : InterpolationFormat.MagPhase;

        var extrapPolicy = OutOfRangePolicy.WarnClamp;
        if (parameters.TryGetValue("ExtrapMode", out var em) && em.Kind == ValueKind.String)
            extrapPolicy = em.AsString().Equals("extrapolate", StringComparison.OrdinalIgnoreCase)
                ? OutOfRangePolicy.WarnExtrapolate
                : OutOfRangePolicy.WarnClamp;

        return new SnpModel(portCount, filePath, interpMethod, extrapPolicy, interpFormat);
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

    private static ZPortModel CreateZPortModel(IReadOnlyDictionary<string, Value> parameters, IReadOnlyList<UserFunction>? functions = null)
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

        return new ZPortModel(portCount, zExprs, numericParams, name, functions);
    }

    // ── Chain (ABCD two-port) ────────────────────────────────────────────────

    private static ChainModel CreateChainModel(IReadOnlyDictionary<string, Value> parameters, IReadOnlyList<UserFunction>? functions = null)
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

        return new ChainModel(Pick("A"), Pick("B"), Pick("C"), Pick("D"), numericParams, name, functions);
    }

    // ── ToneSource (V_1Tone / V_nTone) ───────────────────────────────────────

    private static ToneSourceModel CreateToneSourceModel(
        string typeName, IReadOnlyDictionary<string, Value> parameters,
        IReadOnlyList<UserFunction>? functions = null)
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

    // ── FET family ────────────────────────────────────────────────────────────

    /// <summary>
    /// The built-in large-signal FET models. Each is a SEPARATE type with its OWN parameter set —
    /// they are not variants of one another, and several use the same spelling for different
    /// quantities (the quadratic law's `Beta` is a transconductance parameter; the cubic law's is a
    /// gate-voltage shift with drain bias). Sharing one parameter block across them would silently
    /// mis-feed whichever model the user did not have in mind.
    ///
    /// Every parameter is optional and takes a conventional default, so a freshly placed FET is a
    /// working device the user can then edit.
    /// </summary>
    private static ComponentModel? CreateFetModel(
        string typeName, IReadOnlyDictionary<string, Value> parameters, double ambientC)
    {
        double P(string name, double fallback) =>
            parameters.TryGetValue(name, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : fallback;

        // Shared across the family: gate charge, gate conduction, and temperature.
        //   CapModel 0 = none, 1 = constant Cgs/Cgd (default), 2 = bias-dependent junction charge.
        // The published laws differ on this, so it is a parameter rather than a hardcoded choice.
        //
        // Temp and Tnom are both in DEGREES CELSIUS and both default to the same value, so a model
        // that states no temperature is evaluated exactly at its extraction point and every
        // temperature relation collapses to the identity — no silent shift from a unit mismatch.
        //
        // Temp/Dtemp resolve through the ONE shared rule (Temperature.ResolveDeviceC) so this family
        // and the diode cannot answer the question differently. Tnom does NOT: it is the parameter
        // set's own extraction temperature, a property of the model card rather than of the run, and
        // ambient must never move it — doing so would cancel the very ΔT being asked for.
        double cgs = P("Cgs", 0.0), cgd = P("Cgd", 0.0);
        double isg = P("Is", 0.0), ng = P("N", 1.0);
        int    cap = (int)P("CapModel", 1.0);
        double vbi = P("Vbi", 1.0), mg = P("Mj", 0.5), fc = P("Fc", 0.5);
        double tC  = Temperature.ResolveDeviceC(parameters, ambientC);
        double tnC = P("Tnom", Temperature.NominalC);
        double xti = P("Xti", 0.0), eg = P("Eg", 1.16);

        // Every call is by NAME. These constructors carry a dozen-plus optional parameters and only
        // the coefficients each model actually owns, so their signatures differ and will keep
        // differing; positional binding would compile silently after a reorder and feed Cgd where
        // Alpha belongs.
        return typeName.ToUpperInvariant() switch
        {
            "FET_CURTICE" => new Fet.CurticeQuadraticFetModel(
                vto: P("Vto", -2.0), beta: P("Beta", 0.02),
                lambda: P("Lambda", 0.0), alpha: P("Alpha", 2.0),
                cgs: cgs, cgd: cgd, gateSaturationCurrent: isg, gateEmissionCoefficient: ng,
                capModel: cap, vbi: vbi, mGrading: mg, fc: fc,
                tempC: tC, tnomC: tnC, xti: xti, eg: eg,
                betatc: P("Betatc", 0.0), alphatc: P("Alphatc", 0.0), vtotc: P("Vtotc", 0.0)),

            "FET_CURTICECUBIC" => new Fet.CurticeCubicFetModel(
                a0: P("A0", 0.1), a1: P("A1", 0.05), a2: P("A2", 0.0), a3: P("A3", 0.0),
                gamma: P("Gamma", 2.0), beta: P("Beta", 0.0), vds0: P("Vds0", 5.0),
                cgs: cgs, cgd: cgd, gateSaturationCurrent: isg, gateEmissionCoefficient: ng,
                capModel: cap, vbi: vbi, mGrading: mg, fc: fc,
                tempC: tC, tnomC: tnC, xti: xti, eg: eg,
                gammatc: P("Gammatc", 0.0)),

            "FET_STATZ" => new Fet.StatzFetModel(
                vto: P("Vto", -2.0), beta: P("Beta", 0.02), b: P("B", 0.3),
                alpha: P("Alpha", 2.0), lambda: P("Lambda", 0.0),
                cgs: cgs, cgd: cgd, gateSaturationCurrent: isg, gateEmissionCoefficient: ng,
                capModel: cap, vbi: vbi, mGrading: mg, fc: fc,
                tempC: tC, tnomC: tnC, xti: xti, eg: eg,
                betatc: P("Betatc", 0.0), alphatc: P("Alphatc", 0.0), vtotc: P("Vtotc", 0.0)),

            "FET_MATERKA" => new Fet.MaterkaFetModel(
                idss: P("Idss", 0.1), vp0: P("Vp0", -2.0),
                gamma: P("Gamma", 0.0), alpha: P("Alpha", 2.0),
                cgs: cgs, cgd: cgd, gateSaturationCurrent: isg, gateEmissionCoefficient: ng,
                capModel: cap, vbi: vbi, mGrading: mg, fc: fc,
                tempC: tC, tnomC: tnC, xti: xti, eg: eg,
                alphatc: P("Alphatc", 0.0), gammatc: P("Gammatc", 0.0), vtotc: P("Vtotc", 0.0)),

            "FET_ANGELOV" => new Fet.AngelovFetModel(
                ipk: P("Ipk", 0.1), vpk: P("Vpk", -1.0),
                p1: P("P1", 1.0), p2: P("P2", 0.0), p3: P("P3", 0.0),
                alpha: P("Alpha", 2.0), lambda: P("Lambda", 0.0),
                cgs: cgs, cgd: cgd, gateSaturationCurrent: isg, gateEmissionCoefficient: ng,
                capModel: cap, vbi: vbi, mGrading: mg, fc: fc,
                tempC: tC, tnomC: tnC, xti: xti, eg: eg,
                alphatc: P("Alphatc", 0.0), vtotc: P("Vtotc", 0.0)),

            _ => null,
        };
    }

    // ── Diode ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Junction diode. Every parameter is optional and carries the conventional default, because a
    /// supplier kit states only the ones that matter for its device and expects the rest to take
    /// their usual values — omitting Cj0 must give a diode with no junction capacitance, not an error.
    /// </summary>
    private static DiodeModel CreateDiodeModel(IReadOnlyDictionary<string, Value> parameters, double ambientC)
    {
        double P(string name, double fallback) =>
            parameters.TryGetValue(name, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : fallback;

        return new DiodeModel(
            saturationCurrent:   P("Is",   1e-14),
            emissionCoefficient: P("N",    1.0),
            zeroBiasCapacitance: P("Cj0",  0.0),
            junctionPotential:   P("Vj",   1.0),
            gradingCoefficient:  P("M",    0.5),
            forwardBiasCapCoeff: P("Fc",   0.5),
            breakdownVoltage:    P("Bv",   0.0),
            breakdownCurrent:    P("Ibv",  1e-3),
            transitTime:         P("Tt",   0.0),
            minimumConductance:  P("Gmin", 0.0),   // the DC engine supplies gmin per node
            // `Temp` is in DEGREES CELSIUS here, matching the FET family and every published
            // parameter table. The model itself takes kelvin because that is what kT/q wants;
            // the conversion belongs at this boundary, not in the user's parameter value. Two
            // components in the same palette must never read the same parameter name in
            // different units. `Temp`/`Dtemp`/ambient resolve through the same shared rule the
            // FET family uses, so the two cannot drift.
            temperatureK:        Temperature.ToKelvin(Temperature.ResolveDeviceC(parameters, ambientC)),
            seriesResistance:    P("Rs",   0.0),
            // Recombination is off unless a card asks for it: Isr = 0 is the ordinary case, and a
            // non-zero default would put a second exponential under every diode ever placed.
            recombinationCurrent:  P("Isr", 0.0),
            recombinationEmission: P("Nr",  2.0),
            // Nbv defaults to the PUBLISHED 1, not to N. Before this parameter existed the
            // breakdown branch reused N, which made the reverse knee follow the forward ideality —
            // nothing physical requires that, and no parameter table states it.
            breakdownEmission:     P("Nbv", 1.0),
            area:                  P("Area", 1.0),
            // Tnom is the CARD's extraction temperature, and ambient must never move it. Every
            // temperature relation is written in T − Tnom, so moving both together makes ΔT zero at
            // every ambient while the device still looks temperature-aware.
            nominalTemperatureK:   Temperature.ToKelvin(P("Tnom", Temperature.NominalC)),
            saturationTempExponent: P("Xti", 3.0),
            bandgapAtZeroK:        P("Eg",  Temperature.SiliconBandgapEv));
    }

    // ── R (temperature coefficients) ──────────────────────────────────────────

    /// <summary>
    /// A resistor with temperature coefficients. <c>TC1</c>/<c>TC2</c> absent gives a factor of
    /// EXACTLY 1 and therefore the resistor circuitRF has always had — the whole of this path is
    /// additive, and the parameterless <c>TryCreate("R")</c> still returns exactly that.
    /// </summary>
    private static ResistorModel CreateResistorModel(
        IReadOnlyDictionary<string, Value> parameters, double ambientC)
    {
        double P(string name, double fallback) =>
            parameters.TryGetValue(name, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : fallback;

        double tc1 = P("TC1", 0.0), tc2 = P("TC2", 0.0);
        if (tc1 == 0.0 && tc2 == 0.0) return new ResistorModel();

        // Tnom is the value's own extraction temperature; ambient must never move it. Temp/Dtemp go
        // through the ONE shared rule so a resistor and a diode in one design cannot disagree about
        // what temperature they are at.
        double dT = Temperature.DeltaT(
            Temperature.ResolveDeviceC(parameters, ambientC), P("Tnom", Temperature.NominalC));

        return new ResistorModel(Temperature.PolynomialScale(tc1, tc2, dT));
    }

    // ── SemiC (a capacitor whose value comes from process and geometry) ───────

    /// <summary>
    /// The area and perimeter components are BOTH optional and add: a capacitor stated only by
    /// <c>C</c> is an ordinary one, and one stated by <c>Cj</c>/<c>Cjsw</c> gets its value from the
    /// process. Giving both is legitimate — a fixed parasitic beside a geometric term.
    /// </summary>
    private static SemiCapacitorModel CreateSemiCapacitorModel(
        IReadOnlyDictionary<string, Value> parameters, double ambientC)
    {
        double P(string name, double fallback) =>
            parameters.TryGetValue(name, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : fallback;

        double w = P("W", 0.0), l = P("L", 0.0);

        return new SemiCapacitorModel(
            fixedCapacitance:     P("C", 0.0),
            areaCapacitance:      P("Cj",   0.0),
            perimeterCapacitance: P("Cjsw", 0.0),
            // Width and length give the area and perimeter of a rectangle, which is what a card's
            // Cj/Cjsw are stated per. Either may be given directly instead, for a shape that is not
            // one — the explicit value wins, because it is the more specific statement.
            area:      parameters.ContainsKey("Area")  ? P("Area",  0.0) : w * l,
            perimeter: parameters.ContainsKey("Perim") ? P("Perim", 0.0) : 2.0 * (w + l),
            tc1: P("TC1", 0.0), tc2: P("TC2", 0.0),
            deltaT: Temperature.DeltaT(
                Temperature.ResolveDeviceC(parameters, ambientC), P("Tnom", Temperature.NominalC)));
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

    /// <summary>
    /// R7B §3.2/§3.5 — true when <paramref name="name"/> is one of the SDD equation-parameter shapes
    /// this factory parses (<c>I[p,w]</c>, <c>I[p]</c>, <c>Q[p]</c>, <c>H[w]</c>, <c>C[n]</c>,
    /// <c>Cport[n]</c>, <c>In[…]</c>, <c>Nc[…]</c>) rather than a plain scope-variable name. The
    /// harmonicaRF SDD text editor and netlist builder key off this SAME set instead of re-spelling
    /// it, so a new equation shape only ever needs adding here.
    /// </summary>
    public static bool IsSddEquationName(string name) =>
        RxCurrentEq.IsMatch(name) || RxCurrentEq1.IsMatch(name) || RxChargeEq1.IsMatch(name) ||
        RxWeightFn.IsMatch(name) || RxControlRef.IsMatch(name) || RxControlPort.IsMatch(name) ||
        RxNoise.IsMatch(name);

    private static SddModel CreateSddModel(IReadOnlyDictionary<string, Value> parameters,
        IReadOnlyList<UserFunction>? functions = null)
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

    /// <summary>
    /// wBond: a wirebond component, read from a <c>.wBond</c> file
    /// (<c>docs/design/wbond.md</c> §5, brief-wbond-wbb R-wbb-1).
    ///
    /// <para>Parameters: <c>Design</c> CARRIES the wires (what a schematic writes — see
    /// <c>WBondEmbedding</c>), or <c>File</c> names a <c>.wBond</c> to load (what a hand-authored
    /// netlist may still write). One of the two is required. <c>Temp</c> overrides the operating
    /// temperature, which defaults to the design's own value and ultimately to 85 °C — load-bearing
    /// for R, so it is overridable per instance. <c>GroundPlane</c> (0/1) overrides the plane.</para>
    ///
    /// <para><b><c>Design</c> wins where both are present.</b> An embedded payload is the component's
    /// own wires and needs nothing from the filesystem; falling back to a path when one is right
    /// there would make the answer depend on where the netlist happens to be sitting.</para>
    /// </summary>
    private static ComponentModel CreateWBondModel(IReadOnlyDictionary<string, Value> parameters)
    {
        string path = "";
        WBondDesign design;

        if (parameters.TryGetValue("Design", out var payload) &&
            payload.Kind == ValueKind.String &&
            !string.IsNullOrWhiteSpace(payload.AsString()))
        {
            if (!WBondEmbedding.TryDecode(payload.AsString(), out var embedded) || embedded is null)
                throw new InvalidOperationException(
                    "wBond: the 'Design' parameter could not be read as a wirebond design. Re-import " +
                    "the wires (File ▸ Import ▸ Wirebond Wires…) to replace it.");
            design = embedded;
        }
        else
        {
            if (!parameters.TryGetValue("File", out var fileValue))
                throw new InvalidOperationException(
                    "wBond: neither 'Design' (the embedded wires) nor 'File' (a .wBond to load) is set.");

            path = fileValue.AsString();

            // WB45's own "Not Found" state (§3.1). §5.0/WB17b's argument against a referenced design
            // was precisely that it reintroduces one; a Linked instance accepts that, so the refusal
            // has to read like the cell-reference one the user already knows — the path that failed,
            // and the two ways out of it.
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"wBond: its linked wirebond file was not found: '{path}'. Either restore the file, " +
                    "or set the component's Source back to Carried so it simulates the wires it carries.",
                    path);

            design = WBondIo.ReadFile(path);
        }

        if (parameters.TryGetValue("Temp", out var temp))
            design.OperatingTempC = temp.AsReal();

        if (parameters.TryGetValue("GroundPlane", out var plane))
            design.GroundPlane.Enabled = IsTrue(plane);

        var notes = new List<string>();
        ApplyControllingParameters(design, parameters, notes);
        ReportArrayDrift(design, parameters, notes);

        // Artwork AND terminal count: with the external reference pin off (the default) the component
        // has 2M terminals, with it on 2M+1. REF is always the LAST one, so this changes nothing about
        // the signal terminals or the stamp — see WBondModel's own note. Read as text rather than as a
        // number because that is how the schematic writes it and how the elaborator stores it.
        bool refPin = parameters.TryGetValue("RefPin", out var pin) && IsTrue(pin);

        // Capacitance to the reference plane (wbond.md §3.7). An instance parameter WINS over the
        // design's own flag, the same way GroundPlane and Temp do; absent, the design decides — which
        // is what makes the wBond editor's toolbar toggle the default a newly-placed component
        // inherits rather than a setting the schematic silently ignores.
        bool? includeCapacitance = parameters.TryGetValue("IncludeCapacitance", out var cap)
            ? IsTrue(cap)
            : null;

        return new WBondModel(design, path, refPin, notes, includeCapacitance);
    }

    /// <summary>
    /// A boolean-ish parameter value: <c>true</c> either as a real non-zero or as the word. Both
    /// spellings reach here — a schematic writes "true"/"false", and a hand-authored <c>.cnl</c> may
    /// write 1/0.
    /// </summary>
    private static bool IsTrue(Value value) => value.Kind switch
    {
        ValueKind.String => value.AsString().Equals("true", StringComparison.OrdinalIgnoreCase),
        ValueKind.Bool => value.AsBool(),
        ValueKind.Real => value.AsReal() != 0.0,
        _ => false,
    };

    /// <summary>
    /// Reduces the resolved parameter dictionary to the <b>controlling parameters</b> of
    /// <c>wbond.md</c> §5.5.1/WB44 and applies them — loop height, wire diameter and wire material,
    /// globally or array-scoped.
    ///
    /// <para><b>The geometry itself lives in <c>ControllingParameters.ApplyTo</c>, not here</b>, because
    /// Update Layout from Schematic needs exactly the same reshaping (owner, 2026-08-17: three arrays
    /// set to 30/20/15 mil all arrived in the layout at the drawn 20 mil). A second copy in
    /// <c>src/Ui</c> would be a second set of clone-on-write and detached-wire rules to keep in step.
    /// What is left here is the TRANSLATION: the expression engine has already resolved every length to
    /// SI metres, so a netlist writing <c>LoopHeight=25 mil</c> arrives converted.</para>
    /// </summary>
    private static void ApplyControllingParameters(
        WBondDesign design, IReadOnlyDictionary<string, Value> parameters, List<string> notes)
    {
        var overrides = new WBondOverrides();

        foreach (var (key, value) in parameters)
        {
            if (IsControllingLength(key, "LoopHeight") || IsControllingLength(key, "Diameter"))
                overrides.SetLength(key, value.Kind == ValueKind.Real ? value.AsReal() : null);
            else if (IsControllingName(key, "Material"))
                overrides.SetName(key, value.Kind == ValueKind.String ? value.AsString() : null);
        }

        notes.AddRange(ControllingParameters.ApplyTo(design, overrides));
    }

    /// <summary>
    /// True for <c>&lt;parameter&gt;</c> or <c>&lt;parameter&gt;_&lt;scope&gt;</c>, case-insensitively.
    /// A schematic writes the exact spelling; a hand-authored <c>.cnl</c> need not, and silently
    /// ignoring <c>loopheight_g1</c> is the flat-curve failure this area is haunted by.
    /// </summary>
    private static bool IsControllingLength(string key, string parameter) =>
        key.Equals(parameter, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(parameter + "_", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="IsControllingLength"/>
    private static bool IsControllingName(string key, string parameter) =>
        IsControllingLength(key, parameter);

    /// <summary>
    /// §3.2/WB35a — <b>the array-drift check, run at elaboration for a LINKED instance.</b>
    ///
    /// <para>Under <c>Linked</c> this check becomes MORE load-bearing, not less. Carried drift is
    /// introduced by an explicit re-import, so it can be reported at the moment of the import; linked
    /// drift arrives the moment someone reorders arrays in the <c>.wBond</c>, changing the symbol's pin
    /// order live beneath an already-wired schematic. Pin order IS array order, so every pin keeps its
    /// position while its name moves to a different row — the same defect, arriving more quietly.
    /// Without this, linking would be strictly more dangerous than carrying on that one axis.</para>
    ///
    /// <para><c>Arrays</c> is the record the schematic maintains (the array editor is the only thing
    /// that writes it), and <c>NetExtractor</c> forwards it for a linked instance ALONE — a carried
    /// instance's payload cannot drift against itself.</para>
    /// </summary>
    private static void ReportArrayDrift(
        WBondDesign design, IReadOnlyDictionary<string, Value> parameters, List<string> notes)
    {
        if (NameOf(parameters, "Arrays") is not { } recorded || recorded.Length == 0) return;

        string current = string.Join("|", design.Arrays.Select(a => a.Name));
        if (string.Equals(current, recorded, StringComparison.Ordinal)) return;

        bool reorder = recorded.Split('|').OrderBy(s => s, StringComparer.Ordinal)
            .SequenceEqual(current.Split('|').OrderBy(s => s, StringComparer.Ordinal));

        notes.Add(reorder
            ? $"the arrays in its linked wirebond file are REORDERED relative to what this instance was "
              + $"wired against ({recorded} → {current}). Every pin keeps its position while its name "
              + "moves, so the wires now connect to different arrays. Check the wiring."
            : $"the array list in its linked wirebond file has changed since this instance was wired "
              + $"({recorded} → {current}), so its pins have moved. Check the wiring.");
    }

    // ── Reading one controlling parameter ─────────────────────────────────────

    /// <summary>
    /// A parameter by name, case-insensitively. The resolved dictionary is ordinal-keyed and a
    /// schematic writes the exact spelling, but a hand-authored <c>.cnl</c> need not — and silently
    /// ignoring <c>loopheight_g1</c> is exactly the flat-curve failure §1 warns about.
    /// </summary>
    private static bool TryFindParameter(
        IReadOnlyDictionary<string, Value> parameters, string name, out Value value)
    {
        if (parameters.TryGetValue(name, out value!)) return true;

        foreach (var kv in parameters)
            if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }

        value = default!;
        return false;
    }

    /// <summary>A name-valued parameter, or null when it is not set or is blank.</summary>
    private static string? NameOf(IReadOnlyDictionary<string, Value> parameters, string name)
    {
        if (!TryFindParameter(parameters, name, out var v)) return null;
        string s = v.Kind == ValueKind.String ? v.AsString().Trim() : "";
        return s.Length == 0 ? null : s;
    }
}
