using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// A <c>.cmodel</c> the reader refuses. <see cref="Exception.Message"/> is one sentence that begins with
/// the FIELD it is about (<c>Core.Constituents.Id.Method: …</c>), which is what <c>check</c> prints and
/// what a placed <c>ComposedFET</c> reports (R-mc1-2).
/// </summary>
public sealed class CmodelFormatException(string field, string sentence)
    : Exception($"{field}: {sentence}")
{
    public string Field { get; } = field;
}

/// <summary>
/// Reads, validates and writes the <c>.cmodel</c> container (R-mc1-1, R-mc1-2).
///
/// <para><b>Deterministic.</b> <c>model.json</c> first, then <c>arrays/*.npy</c> in ordinal name
/// order; every entry carries the same fixed timestamp; JSON properties in a fixed order with
/// <c>\n</c> line ends; arrays through RfCore's own <c>.npy</c> writer. The same model writes the same
/// bytes, so a model can be diffed, hashed and committed.</para>
///
/// <para><b>The validator is the only one.</b> <see cref="Read(string)"/> calls <see cref="Validate"/>
/// before returning, and <c>check</c> calls <see cref="Read(string)"/> — so a file <c>check</c> passes
/// is a file a <c>ComposedFET</c> loads, and the sentence a refusal prints is the same in both.</para>
/// </summary>
public static class CmodelFile
{
    public const string Extension     = ".cmodel";
    public const string ModelEntry    = "model.json";
    public const string ArraysFolder  = "arrays/";

    /// <summary>The methods a constituent may name in this build.</summary>
    public static readonly IReadOnlyList<string> KnownMethods =
        [Constituent.EquationMethod, Constituent.TableMethod, Constituent.AnnMethod, Constituent.DriftMethod];

    /// <summary>The trap model names this build knows (D10): <c>None</c> and every registered model.</summary>
    public static IReadOnlyList<string> KnownTrapModels => TrapModels.Names;

    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Reading ───────────────────────────────────────────────────────────────

    public static ComposedModel Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }

    public static ComposedModel Read(Stream stream)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException)
        {
            throw new CmodelFormatException("container", "the file is not a ZIP container.");
        }

        using (zip)
        {
            var entry = zip.GetEntry(ModelEntry)
                ?? throw new CmodelFormatException(ModelEntry, "the container holds no model.json.");

            JsonDocument doc;
            using (var s = entry.Open())
            {
                try { doc = JsonDocument.Parse(s); }
                catch (JsonException ex) { throw new CmodelFormatException(ModelEntry, $"it is not JSON ({ex.Message})."); }
            }

            ComposedModel model;
            using (doc) model = Parse(doc.RootElement);

            var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(ArraysFolder, StringComparison.Ordinal)) continue;
                // A folder entry (`arrays/`) is what most zip tools add before the files in it — Matlab's zip,
                // `zip -r`, an operating system's "compress" — so it carries no array and is not refused.
                if (e.FullName.EndsWith('/')) continue;
                string name = e.FullName[ArraysFolder.Length..];
                if (!name.EndsWith(".npy", StringComparison.Ordinal))
                    throw new CmodelFormatException(e.FullName, "an entry under arrays/ is not a .npy.");
                name = name[..^4];
                using var es = e.Open();
                using var buffer = new MemoryStream();
                es.CopyTo(buffer);
                if (PlainNpy.TryRead(buffer.ToArray(), e.FullName) is { } plain)
                {
                    arrays[name] = plain;
                    continue;
                }
                buffer.Position = 0;
                DataSet ds;
                try { ds = DataSetImporter.ImportNpy(buffer); }
                catch (Exception ex) { throw new CmodelFormatException(e.FullName, $"the array does not read ({ex.Message})."); }
                if (!ds.Contains(name))
                    throw new CmodelFormatException(e.FullName, $"the .npy holds no cube named '{name}'.");
                arrays[name] = ds[name];
            }

            model = model with { Arrays = arrays };
            Validate(model);
            return model;
        }
    }

    private static ComposedModel Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new CmodelFormatException(ModelEntry, "the top level is not a JSON object.");

        int version = Int(root, "FormatVersion");
        if (version > ComposedModel.CurrentFormatVersion)
            throw new CmodelFormatException("FormatVersion",
                $"this file is format {version}, written by a newer circuitRF; this one reads up to " +
                $"format {ComposedModel.CurrentFormatVersion}.");
        if (version < 1)
            throw new CmodelFormatException("FormatVersion", $"{version} is not a format version.");

        ModelThermal? thermal = root.TryGetProperty("Thermal", out var th) ? ParseThermal(th) : null;

        ModelExtrapolation? extrapolation = null;
        if (root.TryGetProperty("Extrapolation", out var ex))
        {
            var gxe = Obj(ex, "Gx", "Extrapolation.Gx");
            var gx = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in gxe.EnumerateObject())
                gx[p.Name] = Num(gxe, p.Name, $"Extrapolation.Gx.{p.Name}");
            foreach (var p in ex.EnumerateObject())
                if (p.Name is not ("Gx" or "Mode"))
                    throw new CmodelFormatException($"Extrapolation.{p.Name}", "it is not a field of Extrapolation; the fields are Gx and Mode.");
            var exMode = ExtrapolationMode.Global;
            if (ex.TryGetProperty("Mode", out _))
            {
                string m = Str(ex, "Mode", "Extrapolation.Mode");
                exMode = m switch
                {
                    "Global" => ExtrapolationMode.Global,
                    "Local" => ExtrapolationMode.Local,
                    _ => throw new CmodelFormatException("Extrapolation.Mode", $"'{m}' is not a law; it is Global or Local."),
                };
            }
            extrapolation = new ModelExtrapolation(gx) { Mode = exMode };
        }

        ModelTraps? traps = root.TryGetProperty("Traps", out var tr) ? ParseTraps(tr) : null;

        var bp = Obj(root, "BasePeriphery");
        var t  = Obj(root, "Temperature");
        string mode = Str(t, "Mode", "Temperature.Mode");
        var temperature = mode switch
        {
            "Single" => ModelTemperature.Single(Num(t, "T0", "Temperature.T0")),
            "Range"  => ModelTemperature.Range(Num(t, "Tmin", "Temperature.Tmin"), Num(t, "Tmax", "Temperature.Tmax")),
            _ => throw new CmodelFormatException("Temperature.Mode", $"'{mode}' is not Single or Range."),
        };

        var net = Obj(root, "Network");
        var scaling = new List<ScalingRule>();
        if (net.TryGetProperty("Scaling", out var sc))
        {
            if (sc.ValueKind != JsonValueKind.Object)
                throw new CmodelFormatException("Network.Scaling", "it is not an object of variable → rule.");
            foreach (var p in sc.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    throw new CmodelFormatException($"Network.Scaling.{p.Name}", "the rule is not a name.");
                scaling.Add(new ScalingRule(p.Name, p.Value.GetString()!));
            }
        }

        var coreObj = Obj(root, "Core");
        var symmetry = DrainSourceSymmetry.None;
        if (coreObj.TryGetProperty("Symmetry", out _))
        {
            string option = Str(coreObj, "Symmetry", "Core.Symmetry");
            symmetry = option switch
            {
                "None"     => DrainSourceSymmetry.None,
                "Exchange" => DrainSourceSymmetry.Exchange,
                _ => throw new CmodelFormatException("Core.Symmetry", $"'{option}' is not None or Exchange."),
            };
        }
        ModelDispersion? dispersion = null;
        if (coreObj.TryGetProperty("Dispersion", out var dsp))
        {
            if (dsp.ValueKind != JsonValueKind.Object)
                throw new CmodelFormatException("Core.Dispersion", "it is not an object.");
            foreach (var p in dsp.EnumerateObject())
                if (p.Name != "Tau")
                    throw new CmodelFormatException($"Core.Dispersion.{p.Name}", "it is not a field of Dispersion; the field is Tau.");
            dispersion = new ModelDispersion(Num(dsp, "Tau", "Core.Dispersion.Tau"));
        }
        ModelNoise? noise = coreObj.TryGetProperty("Noise", out var nz) ? ParseNoise(nz) : null;
        var core = Obj(coreObj, "Constituents", "Core.Constituents");
        Constituent One(string name, bool optional = false)
        {
            string field = $"Core.Constituents.{name}";
            if (!core.TryGetProperty(name, out var c))
            {
                if (optional) return null!;
                throw new CmodelFormatException(field, "it is missing.");
            }
            return ParseConstituent(c, field);
        }

        Constituent? idRf = core.TryGetProperty("IdRF", out _) ? One("IdRF") : null;

        string? symbol = null;
        if (root.TryGetProperty("Symbol", out var sym))
        {
            if (sym.ValueKind != JsonValueKind.String)
                throw new CmodelFormatException("Symbol", "it is not a symbol name or a .csym path.");
            symbol = sym.GetString();
        }

        var provenance = new ModelProvenance();
        if (root.TryGetProperty("Provenance", out var pv))
        {
            var sources = new List<ModelSource>();
            if (pv.TryGetProperty("Sources", out var srcs))
                foreach (var s in srcs.EnumerateArray())
                    sources.Add(new ModelSource(Str(s, "Path", "Provenance.Sources.Path"),
                                                Str(s, "Sha256", "Provenance.Sources.Sha256")));
            provenance = new ModelProvenance
            {
                Created          = OptStr(pv, "Created"),
                CircuitRfVersion = OptStr(pv, "CircuitRfVersion"),
                Sources          = sources,
                SettingsDigest   = OptStr(pv, "SettingsDigest"),
                Charges          = OptStr(pv, "Charges"),
            };
        }

        return new ComposedModel
        {
            FormatVersion = version,
            Kind          = Str(root, "Kind", "Kind"),
            Terminals     = Strings(root, "Terminals", "Terminals"),
            BasePeriphery = new BasePeriphery(Int(bp, "Fingers", "BasePeriphery.Fingers"),
                                              Str(bp, "UnitWidth", "BasePeriphery.UnitWidth")),
            Temperature   = temperature,
            Thermal       = thermal,
            Traps         = traps,
            Network       = new ExtrinsicNetwork(Str(net, "Netlist", "Network.Netlist"), scaling),
            Core          = new CoreConstituents(One("Id"), One("Ig"), One("Qg"), One("Qd"), idRf)
                            { Symmetry = symmetry, Dispersion = dispersion, Noise = noise },
            Extrapolation = extrapolation,
            Symbol        = symbol,
            Provenance    = provenance,
        };
    }

    private static readonly string[] NoiseKeys = ["Tg", "Td", "Kf", "Af"];

    /// <summary><c>Core.Noise</c> (brief NZ-4): <c>Tg</c>, <c>Td</c> as a number (kelvin) or a constituent object; <c>Kf</c>,
    /// <c>Af</c> as numbers.</summary>
    private static ModelNoise ParseNoise(JsonElement n)
    {
        if (n.ValueKind != JsonValueKind.Object) throw new CmodelFormatException("Core.Noise", "it is not an object.");
        foreach (var p in n.EnumerateObject())
            if (!NoiseKeys.Contains(p.Name, StringComparer.Ordinal))
                throw new CmodelFormatException($"Core.Noise.{p.Name}",
                    $"it is not a field of Noise; the fields are {string.Join(", ", NoiseKeys)}.");
        NoiseValue? Temp(string key)
        {
            if (!n.TryGetProperty(key, out var v)) return null;
            string field = $"Core.Noise.{key}";
            return v.ValueKind == JsonValueKind.Number ? NoiseValue.Of(Num(n, key, field)) : NoiseValue.Of(ParseConstituent(v, field));
        }
        return new ModelNoise(Temp("Tg"), Temp("Td"),
                              n.TryGetProperty("Kf", out _) ? Num(n, "Kf", "Core.Noise.Kf") : 0.0,
                              n.TryGetProperty("Af", out _) ? Num(n, "Af", "Core.Noise.Af") : 1.0);
    }

    private static readonly string[] ThermalKeys = ["Rth", "Cth", "Ladder", "RthSlope", "Tref"];

    /// <summary><c>Thermal</c>: one pole as <c>Rth</c>/<c>Cth</c>, or a <c>Ladder</c> of them; optionally <c>RthSlope</c> and
    /// <c>Tref</c>. Ranges are the validator's.</summary>
    private static ModelThermal ParseThermal(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) throw new CmodelFormatException("Thermal", "it is not an object.");
        foreach (var p in t.EnumerateObject())
            if (!ThermalKeys.Contains(p.Name, StringComparer.Ordinal))
                throw new CmodelFormatException($"Thermal.{p.Name}",
                    $"it is not a field of Thermal; the fields are {string.Join(", ", ThermalKeys)}.");

        bool single = t.TryGetProperty("Rth", out _) || t.TryGetProperty("Cth", out _);
        bool ladder = t.TryGetProperty("Ladder", out var l);
        if (single == ladder)
            throw new CmodelFormatException("Thermal", "it carries either Rth and Cth (one pole) or a Ladder, not both and not neither.");

        var poles = new List<ThermalPole>();
        if (single) poles.Add(new ThermalPole(Num(t, "Rth", "Thermal.Rth"), Num(t, "Cth", "Thermal.Cth")));
        else
        {
            if (l.ValueKind != JsonValueKind.Array) throw new CmodelFormatException("Thermal.Ladder", "it is not a list of poles.");
            int k = 0;
            foreach (var pole in l.EnumerateArray())
            {
                string f = $"Thermal.Ladder[{k++}]";
                if (pole.ValueKind != JsonValueKind.Object) throw new CmodelFormatException(f, "it is not an object of Rth and Cth.");
                foreach (var p in pole.EnumerateObject())
                    if (p.Name is not ("Rth" or "Cth"))
                        throw new CmodelFormatException($"{f}.{p.Name}", "it is not a field of a pole; the fields are Rth and Cth.");
                poles.Add(new ThermalPole(Num(pole, "Rth", $"{f}.Rth"), Num(pole, "Cth", $"{f}.Cth")));
            }
        }
        return new ModelThermal(poles)
        {
            RthSlope = t.TryGetProperty("RthSlope", out _) ? Num(t, "RthSlope", "Thermal.RthSlope") : 0.0,
            Tref     = t.TryGetProperty("Tref", out _) ? Num(t, "Tref", "Thermal.Tref") : 25.0,
        };
    }

    private static readonly string[] TrapKeys = ["Model", "Coupling", "States", "Parameters", "Levels", "Expressions"];

    /// <summary><c>Traps</c> (MC-10): the model by name — refused by name when unknown — and its states, parameters, levels
    /// and expressions. A block naming <c>None</c> is no traps. The model's own rules are the validator's.</summary>
    private static ModelTraps? ParseTraps(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) throw new CmodelFormatException("Traps", "it is not an object.");
        foreach (var p in t.EnumerateObject())
            if (!TrapKeys.Contains(p.Name, StringComparer.Ordinal))
                throw new CmodelFormatException($"Traps.{p.Name}", $"it is not a field of Traps; the fields are {string.Join(", ", TrapKeys)}.");
        string model = Str(t, "Model", "Traps.Model");
        if (!KnownTrapModels.Contains(model, StringComparer.Ordinal))
            throw new CmodelFormatException("Traps.Model",
                $"'{model}' is not a known trap model; the known models are {string.Join(", ", KnownTrapModels)}.");
        if (model == ModelTraps.None) return null;

        var parameters = new List<KeyValuePair<string, double>>();
        if (t.TryGetProperty("Parameters", out var pe))
        {
            if (pe.ValueKind != JsonValueKind.Object) throw new CmodelFormatException("Traps.Parameters", "it is not an object of name → number.");
            foreach (var p in pe.EnumerateObject()) parameters.Add(new(p.Name, Num(pe, p.Name, $"Traps.Parameters.{p.Name}")));
        }
        var levels = new List<KeyValuePair<string, IReadOnlyList<double>>>();
        if (t.TryGetProperty("Levels", out var le))
        {
            if (le.ValueKind != JsonValueKind.Object) throw new CmodelFormatException("Traps.Levels", "it is not an object of state → levels.");
            foreach (var p in le.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array || p.Value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number))
                    throw new CmodelFormatException($"Traps.Levels.{p.Name}", "it is not a list of voltages.");
                levels.Add(new(p.Name, [.. p.Value.EnumerateArray().Select(x => x.GetDouble())]));
            }
        }
        var expressions = new List<KeyValuePair<string, string>>();
        if (t.TryGetProperty("Expressions", out var ee))
        {
            if (ee.ValueKind != JsonValueKind.Object) throw new CmodelFormatException("Traps.Expressions", "it is not an object of state → expressions.");
            foreach (var st in ee.EnumerateObject())
            {
                if (st.Value.ValueKind != JsonValueKind.Object)
                    throw new CmodelFormatException($"Traps.Expressions.{st.Name}", "it is not an object of Dynamics and Dc.");
                foreach (var e in st.Value.EnumerateObject())
                    expressions.Add(new($"{st.Name}.{e.Name}", Str(st.Value, e.Name, $"Traps.Expressions.{st.Name}.{e.Name}")));
            }
        }
        return new ModelTraps
        {
            Model = model,
            Coupling = t.TryGetProperty("Coupling", out _) ? Str(t, "Coupling", "Traps.Coupling") : ModelTraps.Trained,
            States = t.TryGetProperty("States", out _) ? Strings(t, "States", "Traps.States") : [],
            Parameters = parameters, Levels = levels, Expressions = expressions,
        };
    }

    private static Constituent ParseConstituent(JsonElement c, string field)
    {
        if (c.ValueKind != JsonValueKind.Object)
            throw new CmodelFormatException(field, "it is not an object.");

        var expressions = new List<KeyValuePair<string, string>>();
        var parameters  = new List<KeyValuePair<string, double>>();
        TrainingDomain? domain = null;
        double? smoothing = null;
        AnnActivation? activation = null;
        OutputTransform? transform = null;
        foreach (var p in c.EnumerateObject())
        {
            switch (p.Name)
            {
                case "Method":
                case "Inputs":
                    break;
                case "Domain":
                    domain = ParseDomain(p.Value, $"{field}.Domain");
                    break;
                case "Smoothing":
                    smoothing = Num(c, "Smoothing", $"{field}.Smoothing");
                    break;
                case "Activation":
                    string act = Str(c, "Activation", $"{field}.Activation");
                    activation = act switch
                    {
                        "Tanh"    => AnnActivation.Tanh,
                        "Sigmoid" => AnnActivation.Sigmoid,
                        _ => throw new CmodelFormatException($"{field}.Activation", $"'{act}' is not Tanh or Sigmoid."),
                    };
                    break;
                case "Transform":
                    if (p.Value.ValueKind != JsonValueKind.Object)
                        throw new CmodelFormatException($"{field}.Transform", "it is not an object.");
                    foreach (var q in p.Value.EnumerateObject())
                        if (q.Name is not ("Kind" or "Scale"))
                            throw new CmodelFormatException($"{field}.Transform.{q.Name}", "it is not a field of Transform; the fields are Kind and Scale.");
                    transform = new OutputTransform(Str(p.Value, "Kind", $"{field}.Transform.Kind"),
                                                    Num(p.Value, "Scale", $"{field}.Transform.Scale"));
                    break;
                case "Parameters":
                    if (p.Value.ValueKind != JsonValueKind.Object)
                        throw new CmodelFormatException($"{field}.Parameters", "it is not an object of name → number.");
                    foreach (var q in p.Value.EnumerateObject())
                    {
                        if (q.Value.ValueKind != JsonValueKind.Number)
                            throw new CmodelFormatException($"{field}.Parameters.{q.Name}", "it is not a number.");
                        parameters.Add(new(q.Name, q.Value.GetDouble()));
                    }
                    break;
                default:
                    if (p.Value.ValueKind != JsonValueKind.String)
                        throw new CmodelFormatException($"{field}.{p.Name}", "it is not an expression.");
                    expressions.Add(new(p.Name, p.Value.GetString()!));
                    break;
            }
        }

        return new Constituent
        {
            Method      = Str(c, "Method", $"{field}.Method"),
            Inputs      = Strings(c, "Inputs", $"{field}.Inputs"),
            Expressions = expressions,
            Parameters  = parameters,
            Domain      = domain,
            Smoothing   = smoothing,
            Activation  = activation,
            Transform   = transform,
        };
    }

    private static TrainingDomain ParseDomain(JsonElement e, string field)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new CmodelFormatException(field, "it is not an object.");
        if (!e.TryGetProperty("Polygon", out var poly) || poly.ValueKind != JsonValueKind.Array)
            throw new CmodelFormatException($"{field}.Polygon", "it is missing or not a list of [vgs, vds] vertices.");
        var vertices = new List<(double, double)>();
        foreach (var v in poly.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2
                || v[0].ValueKind != JsonValueKind.Number || v[1].ValueKind != JsonValueKind.Number)
                throw new CmodelFormatException($"{field}.Polygon", "a vertex is not [vgs, vds].");
            vertices.Add((v[0].GetDouble(), v[1].GetDouble()));
        }

        var box = new List<KeyValuePair<string, (double, double)>>();
        if (e.TryGetProperty("Box", out var b))
        {
            if (b.ValueKind != JsonValueKind.Object)
                throw new CmodelFormatException($"{field}.Box", "it is not an object of input → [lo, hi].");
            foreach (var p in b.EnumerateObject())
            {
                var r = p.Value;
                if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 2
                    || r[0].ValueKind != JsonValueKind.Number || r[1].ValueKind != JsonValueKind.Number)
                    throw new CmodelFormatException($"{field}.Box.{p.Name}", "it is not [lo, hi].");
                box.Add(new(p.Name, (r[0].GetDouble(), r[1].GetDouble())));
            }
        }
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("Polygon" or "Box"))
                throw new CmodelFormatException($"{field}.{p.Name}", "it is not a field of a Domain; the fields are Polygon and Box.");
        return new TrainingDomain(vertices, box);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every rule a model must satisfy, refused by field. Called by <see cref="Read(string)"/> and by
    /// <see cref="Write(ComposedModel, Stream)"/>, so neither direction lets a bad model through.
    /// </summary>
    public static void Validate(ComposedModel m)
    {
        if (m.FormatVersion > ComposedModel.CurrentFormatVersion)
            throw new CmodelFormatException("FormatVersion",
                $"this model is format {m.FormatVersion}; this circuitRF reads up to format " +
                $"{ComposedModel.CurrentFormatVersion}.");
        if (m.Kind != ComposedModel.FetKind)
            throw new CmodelFormatException("Kind", $"'{m.Kind}' is not a known kind; the known kind is {ComposedModel.FetKind}.");
        if (!m.Terminals.SequenceEqual(["G", "D", "S"], StringComparer.Ordinal))
            throw new CmodelFormatException("Terminals", "a FET's terminals are G, D, S, in that order.");

        if (m.BasePeriphery.Fingers < 1)
            throw new CmodelFormatException("BasePeriphery.Fingers", $"{m.BasePeriphery.Fingers} is not a finger count.");
        if (UnitWidthMetres(m.BasePeriphery.UnitWidth) is not > 0)
            throw new CmodelFormatException("BasePeriphery.UnitWidth",
                $"'{m.BasePeriphery.UnitWidth}' is not a positive width with its unit (\"125 um\").");

        var t = m.Temperature;
        if (t.Mode == TemperatureMode.Single && !double.IsFinite(t.T0))
            throw new CmodelFormatException("Temperature.T0", "it is not a temperature.");
        if (t.Mode == TemperatureMode.Range && !(double.IsFinite(t.Tmin) && double.IsFinite(t.Tmax) && t.Tmin < t.Tmax))
            throw new CmodelFormatException("Temperature", "Tmin must be below Tmax.");

        ValidateThermal(m);

        foreach (var r in m.Network.Scaling)
            if (!PeripheryScaling.IsKnown(r.Rule))
                throw new CmodelFormatException($"Network.Scaling.{r.Variable}",
                    $"'{r.Rule}' is not a known scaling rule; the known rules are {string.Join(", ", PeripheryScaling.Known)}.");
        var network = ComposedNetworkTemplate.Build(m.Network);
        if (m.Thermal is { } thermal)
        {
            // The names the synthesised cell gives the thermal network must not already mean something in the network.
            foreach (string element in ComposedNetworkTemplate.ThermalElementNames(thermal))
                if (network.HasElement(element))
                    throw new CmodelFormatException("Network.Netlist",
                        $"an element is named '{element}', which names the thermal network of a self-heating model.");
            foreach (string net in ComposedNetworkTemplate.ThermalNetNames(thermal))
                if (network.HasNet(net))
                    throw new CmodelFormatException("Network.Netlist",
                        $"a net is named '{net}', which names the thermal network of a self-heating model.");
        }

        ValidateTraps(m);

        var inputs = new HashSet<string>(StringComparer.Ordinal) { ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds };
        if (t.Mode == TemperatureMode.Range) inputs.Add(ComposedCoreProgram.T);
        // The trap states enter Id and Qd only ([X10] eq. 1.5, D10): never the gate current or the gate charge.
        var trapped = new HashSet<string>(inputs, StringComparer.Ordinal);
        foreach (string state in m.Traps?.States ?? []) trapped.Add(state);

        // R-mc19-1: under a Shift coupling the core shifts Id's gate voltage, so Id takes no state itself.
        ValidateConstituent(m, m.Core.Id, "Id", m.Traps is { Shifted: true } ? inputs : trapped);
        ValidateConstituent(m, m.Core.Ig, "Ig", inputs);
        ValidateConstituent(m, m.Core.Qg, "Qg", inputs);
        ValidateConstituent(m, m.Core.Qd, "Qd", trapped);
        if (m.Core.IdRF is { } idRf) ValidateConstituent(m, idRf, "IdRF", inputs);
        ValidateDriftPair(m);
        ValidateDispersion(m);
        ValidateExtrapolation(m);
        ValidateNoise(m, inputs, network);

        if (m.Symbol is { Length: 0 })
            throw new CmodelFormatException("Symbol", "it is empty; leave it out for the standard FET symbol.");
    }

    /// <summary>
    /// <c>Core.Noise</c>: each temperature a positive finite constant, or an equation or table reading the core's own
    /// <c>vgs</c>, <c>vds</c> and temperature (no trap state); <c>Kf</c> finite and not negative, <c>Af</c> positive; and a
    /// <c>Tg</c> only where the network has the <c>Ri</c> it heats.
    /// </summary>
    private static void ValidateNoise(ComposedModel m, HashSet<string> inputs, ComposedNetworkTemplate network)
    {
        if (m.Core.Noise is not { } n) return;
        void Temp(NoiseValue? v, string key)
        {
            if (v is null) return;
            string field = $"Core.Noise.{key}";
            if (v.Constant is { } k)
            {
                if (!(double.IsFinite(k) && k > 0)) throw new CmodelFormatException(field, "it is not a temperature in kelvin above zero.");
                return;
            }
            var c = v.Function!;
            if (c.Method is not (Constituent.EquationMethod or Constituent.TableMethod))
                throw new CmodelFormatException($"{field}.Method", $"'{c.Method}' is not Equation or Table.");
            ValidateConstituent(m, c, key, inputs, field);
        }
        Temp(n.Tg, "Tg");
        Temp(n.Td, "Td");
        if (!(double.IsFinite(n.Kf) && n.Kf >= 0)) throw new CmodelFormatException("Core.Noise.Kf", "it is not a flicker coefficient of zero or more.");
        if (!(double.IsFinite(n.Af) && n.Af > 0)) throw new CmodelFormatException("Core.Noise.Af", "it is not a flicker exponent above zero.");
        if (n.Tg is not null && !network.HasElement(ModelNoise.GateResistorName))
            throw new CmodelFormatException("Core.Noise.Tg",
                $"it is the temperature of the network's intrinsic gate resistance, and the network has no element named " +
                $"'{ModelNoise.GateResistorName}'.");
    }

    private static void ValidateConstituent(ComposedModel m, Constituent c, string name, HashSet<string> allowed,
                                            string? fieldName = null)
    {
        string field = fieldName ?? $"Core.Constituents.{name}";
        if (!KnownMethods.Contains(c.Method, StringComparer.Ordinal))
            throw new CmodelFormatException($"{field}.Method",
                $"'{c.Method}' is not a known method; the known methods are {string.Join(", ", KnownMethods)}.");
        if (c.Method == Constituent.DriftMethod)
        {
            ValidateDrift(c, name, field);
            return;
        }
        string[] keys = name == "Ig" ? ["IgGs", "IgGd"] : [c.PayloadKey];
        if (c.Inputs.Distinct(StringComparer.Ordinal).Count() != c.Inputs.Count)
            throw new CmodelFormatException($"{field}.Inputs", "an input is listed twice.");

        foreach (string input in c.Inputs)
        {
            if (allowed.Contains(input)) continue;
            string why = input switch
            {
                ComposedCoreProgram.T => "a single-temperature model has no temperature input",
                _ when m.Traps is null && TrapState(input) => "trap states are inputs only of a model with traps",
                _ when name == "Id" && m.Traps is { Shifted: true } sh && sh.States.Contains(input)
                    => $"under a {ModelTraps.Shift} coupling Id takes no trap state; the core shifts its gate voltage by the states",
                _ when m.Traps is { } own && own.States.Contains(input) => "the trap states are inputs of Id and Qd only, never of the gate current or charge",
                _ when m.Traps is { } any && TrapState(input) => $"the model's trap states are {string.Join(", ", any.States)}",
                _                     => $"the inputs are {string.Join(", ", allowed)}",
            };
            throw new CmodelFormatException($"{field}.Inputs", $"'{input}' is not an input here: {why}.");
        }

        foreach (var e in c.Expressions)
            if (!keys.Contains(e.Key))
                throw new CmodelFormatException($"{field}.{e.Key}",
                    $"{(c.Method == Constituent.TableMethod ? "a" : "an")} {c.Method} {name} is {string.Join(" and ", keys)}; '{e.Key}' is not one of them.");

        if (c.Transform is { } tr)
        {
            if (c.Method == Constituent.EquationMethod)
                throw new CmodelFormatException($"{field}.Transform", "an Equation states its current directly and has no transform.");
            if (name is "Qg" or "Qd")
                throw new CmodelFormatException($"{field}.Transform", "only a current has an output transform.");
            if (tr.Kind != OutputTransform.Asinh)
                throw new CmodelFormatException($"{field}.Transform.Kind", $"'{tr.Kind}' is not a known transform; the known one is {OutputTransform.Asinh}.");
            if (!(double.IsFinite(tr.Scale) && tr.Scale > 0))
                throw new CmodelFormatException($"{field}.Transform.Scale", "it is not a positive current in amps.");
        }

        if (c.Method != Constituent.EquationMethod)
        {
            ValidateRuntime(m, c, field, keys);
            return;
        }
        if (c.Domain is not null)
            throw new CmodelFormatException($"{field}.Domain", "an Equation is global and has no training domain.");
        if (c.Smoothing is not null)
            throw new CmodelFormatException($"{field}.Smoothing", "only a Table has a smoothing weight.");
        if (c.Activation is not null)
            throw new CmodelFormatException($"{field}.Activation", "only an ANN has an activation.");

        var names = new HashSet<string>(c.Inputs, StringComparer.Ordinal);
        foreach (var p in c.Parameters)
        {
            if (!double.IsFinite(p.Value))
                throw new CmodelFormatException($"{field}.Parameters.{p.Key}", "it is not a finite number.");
            if (allowed.Contains(p.Key))
                throw new CmodelFormatException($"{field}.Parameters.{p.Key}", "a parameter cannot share an input's name.");
            names.Add(p.Key);
        }

        foreach (string key in keys)
        {
            string ef = $"{field}.{key}";
            string text = c.Expression(key) ?? throw new CmodelFormatException(ef, "it is missing.");
            Expr ast;
            try { ast = Parser.Parse(text); }
            catch (Exception ex) { throw new CmodelFormatException(ef, $"it does not parse: {ex.Message}"); }
            foreach (string r in AstWalker.CollectRefs(ast))
                if (!names.Contains(r) && r is not ("pi" or "e"))
                    throw new CmodelFormatException(ef,
                        $"'{r}' is neither one of its inputs ({string.Join(", ", c.Inputs)}) nor one of its parameters.");
        }
    }

    /// <summary>A table or ANN: its inputs, its domain, its method's settings, and every array it names.</summary>
    private static void ValidateRuntime(ComposedModel m, Constituent c, string field, string[] keys)
    {
        foreach (string needed in (string[])[ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds])
            if (!c.Inputs.Contains(needed))
                throw new CmodelFormatException($"{field}.Inputs",
                    $"a {c.Method} is trained over a (vgs, vds) domain, so its inputs include {needed}.");
        if (c.Parameters.Count > 0)
            throw new CmodelFormatException($"{field}.Parameters", "only an Equation has parameters.");
        if (c.Method == Constituent.TableMethod)
        {
            if (c.Activation is not null)
                throw new CmodelFormatException($"{field}.Activation", "only an ANN has an activation.");
            if (c.Smoothing is { } l && !(double.IsFinite(l) && l >= 0))
                throw new CmodelFormatException($"{field}.Smoothing", "the smoothing weight is a number ≥ 0.");
        }
        else if (c.Smoothing is not null)
            throw new CmodelFormatException($"{field}.Smoothing", "only a Table has a smoothing weight.");

        var domain = c.Domain ?? throw new CmodelFormatException($"{field}.Domain",
            $"a {c.Method} stores the domain it was trained over; it is missing.");
        ValidateDomain(domain, c.Inputs, $"{field}.Domain");

        foreach (string key in keys)
        {
            if (c.Expression(key) is not { Length: > 0 })
                throw new CmodelFormatException($"{field}.{key}", "it is missing; it names the payload's arrays.");
            RuntimePayload.Build(m, c, key, $"{field}.{key}");
        }
    }

    /// <summary>
    /// A drift charge's own shape (MC-8 R-mc8-3): a charge, reading vgs and vds, naming its three pieces, with the
    /// partition factor on Qd and every piece's interval in its box. The pieces' arrays are built by
    /// <see cref="ValidateDriftPair"/>, which needs both charges.
    /// </summary>
    private static void ValidateDrift(Constituent c, string name, string field)
    {
        if (name is not ("Qg" or "Qd"))
            throw new CmodelFormatException($"{field}.Method", $"Drift is a charge method; {name} is a current.");
        if (!c.Inputs.SequenceEqual([ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds], StringComparer.Ordinal))
            throw new CmodelFormatException($"{field}.Inputs", "a Drift charge reads vgs and vds, and the current through Id.");
        var keys = name == "Qg" ? DriftCharges.QgKeys : DriftCharges.QdKeys;
        foreach (var e in c.Expressions)
            if (!keys.Contains(e.Key))
                throw new CmodelFormatException($"{field}.{e.Key}",
                    $"a Drift {name} is {string.Join(", ", keys)}; '{e.Key}' is not one of them.");
        foreach (string key in keys)
            if (c.Expression(key) is not { Length: > 0 })
                throw new CmodelFormatException($"{field}.{key}", "it is missing; it names the piece's network.");
        if (c.Smoothing is not null)
            throw new CmodelFormatException($"{field}.Smoothing", "only a Table has a smoothing weight.");

        string[] parameters = name == "Qd" ? [DriftCharges.Lambda] : [];
        foreach (var p in c.Parameters)
            if (!parameters.Contains(p.Key))
                throw new CmodelFormatException($"{field}.Parameters.{p.Key}",
                    name == "Qd" ? "a Drift Qd's one parameter is Lambda." : "a Drift Qg has no parameters; Lambda is Qd's.");
        if (name == "Qd")
        {
            var lambda = c.Parameters.FirstOrDefault(p => p.Key == DriftCharges.Lambda);
            if (lambda.Key is null)
                throw new CmodelFormatException($"{field}.Parameters.Lambda", "it is missing; the partition factor, 0 by default.");
            if (!(lambda.Value is >= 0 and <= 1))
                throw new CmodelFormatException($"{field}.Parameters.Lambda", "the partition factor is between 0 and 1.");
        }

        var domain = c.Domain ?? throw new CmodelFormatException($"{field}.Domain",
            "a Drift charge stores the data's hull and each piece's interval; it is missing.");
        ValidatePolygon(domain.Polygon, $"{field}.Domain");
        foreach (var (key, (lo, hi)) in domain.Box)
        {
            if (!DriftCharges.BoxKeys.Contains(key))
                throw new CmodelFormatException($"{field}.Domain.Box.{key}",
                    $"a Drift charge's box holds {string.Join(", ", DriftCharges.BoxKeys)}.");
            if (!(double.IsFinite(lo) && double.IsFinite(hi) && lo < hi))
                throw new CmodelFormatException($"{field}.Domain.Box.{key}", "lo must be below hi.");
        }
        foreach (string key in DriftCharges.BoxKeys)
            if (!domain.Box.Any(b => b.Key == key))
                throw new CmodelFormatException($"{field}.Domain.Box.{key}", "the piece's training interval is missing.");
    }

    /// <summary>Qg and Qd are both Drift or neither, share their QGD and Qdrift, and their pieces build.</summary>
    private static void ValidateDriftPair(ComposedModel m)
    {
        bool g = m.Core.Qg.Method == Constituent.DriftMethod, d = m.Core.Qd.Method == Constituent.DriftMethod;
        if (!g && !d) return;
        if (g != d)
            throw new CmodelFormatException($"Core.Constituents.{(g ? "Qd" : "Qg")}.Method",
                "Qg and Qd are both Drift or neither: the two share QGD and Qdrift.");
        foreach (string key in (string[])[DriftCharges.Qgd, DriftCharges.Qdrift])
            if (m.Core.Qg.Expression(key) != m.Core.Qd.Expression(key))
                throw new CmodelFormatException($"Core.Constituents.Qd.{key}", $"it names a different network from Qg's {key}.");
        DriftCharges.Build(m);
    }

    private static void ValidateDomain(TrainingDomain d, IReadOnlyList<string> inputs, string field)
    {
        ValidatePolygon(d.Polygon, field);
        var boxed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (input, (lo, hi)) in d.Box)
        {
            if (input is ComposedCoreProgram.Vgs or ComposedCoreProgram.Vds || !inputs.Contains(input))
                throw new CmodelFormatException($"{field}.Box.{input}",
                    "the box holds the inputs other than vgs and vds, and this is not one of them.");
            if (!(double.IsFinite(lo) && double.IsFinite(hi) && lo < hi))
                throw new CmodelFormatException($"{field}.Box.{input}", "lo must be below hi.");
            boxed.Add(input);
        }
        foreach (string input in inputs)
            if (input is not (ComposedCoreProgram.Vgs or ComposedCoreProgram.Vds) && !boxed.Contains(input))
                throw new CmodelFormatException($"{field}.Box.{input}", "the input's training interval is missing.");
    }

    private static void ValidatePolygon(IReadOnlyList<(double Vgs, double Vds)> poly, string field)
    {
        if (poly.Count < 3)
            throw new CmodelFormatException($"{field}.Polygon", "a polygon has at least three vertices.");
        if (poly.Any(v => !double.IsFinite(v.Vgs) || !double.IsFinite(v.Vds)))
            throw new CmodelFormatException($"{field}.Polygon", "a vertex is not finite.");

        // Convex, with positive area: every turn has the sign of the whole (collinear turns allowed).
        double area = 0.0;
        int n = poly.Count;
        for (int k = 0; k < n; k++)
            area += poly[k].Vgs * poly[(k + 1) % n].Vds - poly[(k + 1) % n].Vgs * poly[k].Vds;
        double scale = (poly.Max(v => v.Vgs) - poly.Min(v => v.Vgs)) * (poly.Max(v => v.Vds) - poly.Min(v => v.Vds));
        if (!(Math.Abs(area) > 1e-12 * scale) || scale <= 0)
            throw new CmodelFormatException($"{field}.Polygon", "the polygon encloses no area.");
        for (int k = 0; k < n; k++)
        {
            var a = poly[k]; var b = poly[(k + 1) % n]; var c = poly[(k + 2) % n];
            double turn = (b.Vgs - a.Vgs) * (c.Vds - b.Vds) - (b.Vds - a.Vds) * (c.Vgs - b.Vgs);
            if (turn * Math.Sign(area) < -1e-12 * scale)
                throw new CmodelFormatException($"{field}.Polygon",
                    "the polygon is not convex; the nearest-point continuation needs a convex domain.");
        }
    }

    /// <summary>A thermal network: on a ranged model only (D8), one to five poles of positive R and C, and a thermal
    /// resistance that stays positive over the model's temperature range.</summary>
    private static void ValidateThermal(ComposedModel m)
    {
        if (m.Thermal is not { } th) return;
        if (m.Temperature.Mode != TemperatureMode.Range)
            throw new CmodelFormatException("Thermal",
                "self-heating needs a model over a temperature range; this one is valid at one temperature.");
        if (th.Poles.Count is < 1 or > ModelThermal.MaxPoles)
            throw new CmodelFormatException("Thermal.Ladder",
                $"{th.Poles.Count} pole(s); a thermal network has 1 to {ModelThermal.MaxPoles}.");
        bool ladder = th.Poles.Count > 1;
        for (int k = 0; k < th.Poles.Count; k++)
        {
            string f = ladder ? $"Thermal.Ladder[{k}]" : "Thermal";
            if (!(double.IsFinite(th.Poles[k].Rth) && th.Poles[k].Rth > 0))
                throw new CmodelFormatException($"{f}.Rth", "it is not a positive thermal resistance in °C/W.");
            if (!(double.IsFinite(th.Poles[k].Cth) && th.Poles[k].Cth > 0))
                throw new CmodelFormatException($"{f}.Cth", "it is not a positive thermal capacitance in J/°C.");
        }
        if (!double.IsFinite(th.RthSlope))
            throw new CmodelFormatException("Thermal.RthSlope", "it is not a number of °C/W per °C.");
        if (!double.IsFinite(th.Tref))
            throw new CmodelFormatException("Thermal.Tref", "it is not a temperature.");
        if (!(th.RthAt(m.Temperature.Tmin) > 0 && th.RthAt(m.Temperature.Tmax) > 0))
            throw new CmodelFormatException("Thermal.RthSlope",
                "it makes the thermal resistance zero or negative inside the model's temperature range.");
    }

    private static bool TrapState(string input) => input.StartsWith("phi", StringComparison.Ordinal);

    /// <summary>
    /// <c>Traps</c> (MC-10): the model's block compiled by the registry, which refuses an unknown model by name and
    /// everything else by field — and two combinations refused: [P1] eq. 6.52's dispersion, whose DC/RF difference the trap
    /// states carry instead, and drain–source exchange, under which the trap states (which follow the forward device's
    /// gate and drain) have no image.
    /// </summary>
    private static void ValidateTraps(ComposedModel m)
    {
        if (m.Traps is not { } traps) return;
        if (traps.Model == ModelTraps.None)
            throw new CmodelFormatException("Traps.Model", "a model without traps carries no Traps block.");
        TrapModels.Build(traps, "validation");
        if (m.Core.Dispersion is not null || m.Core.IdRF is not null)
            throw new CmodelFormatException("Core.Dispersion",
                "dispersion and traps are not combined: with traps the DC/RF difference follows from the trap states.");
        if (m.Core.Symmetry == DrainSourceSymmetry.Exchange)
            throw new CmodelFormatException("Core.Symmetry", "drain–source exchange is not defined for a model with traps.");
    }

    /// <summary><c>IdRF</c> and <c>Core.Dispersion</c> come together: one without the other has no meaning.</summary>
    private static void ValidateDispersion(ComposedModel m)
    {
        var dsp = m.Core.Dispersion;
        if (m.Core.IdRF is not null && dsp is null)
            throw new CmodelFormatException("Core.Dispersion",
                "it is missing; an IdRF constituent needs the time constant Tau of its DC/RF transition.");
        if (dsp is null) return;
        if (m.Core.IdRF is null)
            throw new CmodelFormatException("Core.Dispersion", "there is no IdRF constituent for it to apply to.");
        if (!(double.IsFinite(dsp.Tau) && dsp.Tau > 0))
            throw new CmodelFormatException("Core.Dispersion.Tau", "it is not a positive time in seconds.");
    }

    /// <summary>Every table or ANN current payload has its <c>G_x</c>, and nothing else does.</summary>
    private static void ValidateExtrapolation(ComposedModel m)
    {
        var needed = new HashSet<string>(StringComparer.Ordinal);
        if (m.Core.Id.Method != Constituent.EquationMethod) needed.Add("Id");
        if (m.Core.Ig.Method != Constituent.EquationMethod) { needed.Add("IgGs"); needed.Add("IgGd"); }
        if (m.Core.IdRF is { } idRf && idRf.Method != Constituent.EquationMethod) needed.Add("IdRF");

        var gx = m.Extrapolation?.Gx ?? new Dictionary<string, double>();
        foreach (var (key, value) in gx)
        {
            if (!ModelExtrapolation.Keys.Contains(key))
                throw new CmodelFormatException($"Extrapolation.Gx.{key}",
                    $"'{key}' is not a current payload; they are {string.Join(", ", ModelExtrapolation.Keys)}.");
            if (!needed.Contains(key))
                throw new CmodelFormatException($"Extrapolation.Gx.{key}",
                    "that current is an Equation, which is global and is not extrapolated.");
            if (!(double.IsFinite(value) && value > 0))
                throw new CmodelFormatException($"Extrapolation.Gx.{key}", "it is not a positive conductance.");
        }
        foreach (string key in ModelExtrapolation.Keys)
            if (needed.Contains(key) && !gx.ContainsKey(key))
                throw new CmodelFormatException($"Extrapolation.Gx.{key}",
                    "it is missing; a table or ANN current is extrapolated with an added conductance G_x.");
    }

    /// <summary>The unit width in metres, or null when the text is not a number and a length unit.</summary>
    public static double? UnitWidthMetres(string text)
    {
        var parts = text.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        if (parts.Length == 1) return null;
        return Units.Scale(parts[1]) is { } s && Units.BaseUnit(parts[1]) == "metre" ? v * s : null;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    public static void Write(ComposedModel model, string path)
    {
        using var buffer = new MemoryStream();
        Write(model, buffer);
        File.WriteAllBytes(path, buffer.ToArray());
    }

    public static void Write(ComposedModel model, Stream stream)
    {
        Validate(model);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var entry = zip.CreateEntry(ModelEntry, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using (var s = entry.Open())
        {
            var bytes = ModelJson(model);
            s.Write(bytes, 0, bytes.Length);
        }

        foreach (var (name, cube) in model.Arrays.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            var e = zip.CreateEntry($"{ArraysFolder}{name}.npy", CompressionLevel.Optimal);
            e.LastWriteTime = EntryTime;
            var ds = new DataSet();
            ds.Add(name, cube);
            using var es = e.Open();
            DataSetExporter.ExportNpy(ds, es);
        }
    }

    /// <summary>The <c>model.json</c> bytes for <paramref name="m"/>, in the fixed order.</summary>
    public static byte[] ModelJson(ComposedModel m)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("FormatVersion", m.FormatVersion);
            w.WriteString("Kind", m.Kind);
            w.WriteStartArray("Terminals");
            foreach (string term in m.Terminals) w.WriteStringValue(term);
            w.WriteEndArray();

            w.WriteStartObject("BasePeriphery");
            w.WriteNumber("Fingers", m.BasePeriphery.Fingers);
            w.WriteString("UnitWidth", m.BasePeriphery.UnitWidth);
            w.WriteEndObject();

            w.WriteStartObject("Temperature");
            w.WriteString("Mode", m.Temperature.Mode.ToString());
            if (m.Temperature.Mode == TemperatureMode.Single)
                w.WriteNumber("T0", m.Temperature.T0);
            else
            {
                w.WriteNumber("Tmin", m.Temperature.Tmin);
                w.WriteNumber("Tmax", m.Temperature.Tmax);
            }
            w.WriteEndObject();

            if (m.Thermal is { } th)
            {
                w.WriteStartObject("Thermal");
                if (th.Poles.Count == 1)
                {
                    w.WriteNumber("Rth", th.Poles[0].Rth);
                    w.WriteNumber("Cth", th.Poles[0].Cth);
                }
                else
                {
                    w.WriteStartArray("Ladder");
                    foreach (var pole in th.Poles)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("Rth", pole.Rth);
                        w.WriteNumber("Cth", pole.Cth);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                if (th.RthSlope != 0)
                {
                    w.WriteNumber("RthSlope", th.RthSlope);
                    w.WriteNumber("Tref", th.Tref);
                }
                w.WriteEndObject();
            }

            if (m.Traps is { } traps) WriteTraps(w, traps);

            w.WriteStartObject("Network");
            w.WriteString("Netlist", m.Network.Netlist);
            w.WriteStartObject("Scaling");
            foreach (var r in m.Network.Scaling) w.WriteString(r.Variable, r.Rule);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("Core");
            if (m.Core.Symmetry != DrainSourceSymmetry.None) w.WriteString("Symmetry", m.Core.Symmetry.ToString());
            if (m.Core.Dispersion is { } dsp)
            {
                w.WriteStartObject("Dispersion");
                w.WriteNumber("Tau", dsp.Tau);
                w.WriteEndObject();
            }
            w.WriteStartObject("Constituents");
            WriteConstituent(w, "Id", m.Core.Id);
            WriteConstituent(w, "Ig", m.Core.Ig);
            WriteConstituent(w, "Qg", m.Core.Qg);
            WriteConstituent(w, "Qd", m.Core.Qd);
            if (m.Core.IdRF is { } idRf) WriteConstituent(w, "IdRF", idRf);
            w.WriteEndObject();
            if (m.Core.Noise is { } noise)
            {
                w.WriteStartObject("Noise");
                foreach (var (key, v) in new[] { ("Tg", noise.Tg), ("Td", noise.Td) })
                {
                    if (v is null) continue;
                    if (v.Constant is { } k) w.WriteNumber(key, k);
                    else WriteConstituent(w, key, v.Function!);
                }
                if (noise.Kf != 0) w.WriteNumber("Kf", noise.Kf);
                if (noise.Af != 1) w.WriteNumber("Af", noise.Af);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            if (m.Extrapolation is { } ex)
            {
                w.WriteStartObject("Extrapolation");
                // A global-law model is written as every model before the local law was (brief-modelcomposer-29).
                if (ex.Mode == ExtrapolationMode.Local) w.WriteString("Mode", "Local");
                w.WriteStartObject("Gx");
                foreach (string key in ModelExtrapolation.Keys)
                    if (ex.Gx.TryGetValue(key, out double gx)) w.WriteNumber(key, gx);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            if (m.Symbol is { } symbol) w.WriteString("Symbol", symbol);

            w.WriteStartObject("Provenance");
            w.WriteString("Created", m.Provenance.Created);
            w.WriteString("CircuitRfVersion", m.Provenance.CircuitRfVersion);
            w.WriteStartArray("Sources");
            foreach (var s in m.Provenance.Sources)
            {
                w.WriteStartObject();
                w.WriteString("Path", s.Path);
                w.WriteString("Sha256", s.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("SettingsDigest", m.Provenance.SettingsDigest);
            if (m.Provenance.Charges.Length > 0) w.WriteString("Charges", m.Provenance.Charges);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>The <c>Traps</c> block as <c>model.json</c> writes it — what a <c>.cmc</c> records for a fitted model before
    /// export (brief-modelcomposer-10). Not validated here: a block is validated with its model.</summary>
    public static string TrapsJson(ModelTraps t)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            WriteTraps(w, t);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.GetProperty("Traps").GetRawText();
    }

    /// <summary>The inverse of <see cref="TrapsJson"/>: null for a block naming <c>None</c>.</summary>
    public static ModelTraps? ParseTrapsJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CmodelFormatException("Traps", $"it is not JSON ({ex.Message})."); }
        using (doc) return ParseTraps(doc.RootElement);
    }

    private static void WriteTraps(Utf8JsonWriter w, ModelTraps t)
    {
        w.WriteStartObject("Traps");
        w.WriteString("Model", t.Model);
        if (t.Coupling != ModelTraps.Trained) w.WriteString("Coupling", t.Coupling);
        w.WriteStartArray("States");
        foreach (string s in t.States) w.WriteStringValue(s);
        w.WriteEndArray();
        w.WriteStartObject("Parameters");
        foreach (var (name, value) in t.Parameters) w.WriteNumber(name, value);
        w.WriteEndObject();
        if (t.Levels.Count > 0)
        {
            w.WriteStartObject("Levels");
            foreach (var (state, levels) in t.Levels)
            {
                w.WriteStartArray(state);
                foreach (double l in levels) w.WriteNumberValue(l);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        if (t.Expressions.Count > 0)
        {
            w.WriteStartObject("Expressions");
            foreach (string state in t.States)
            {
                var own = t.Expressions.Where(e => e.Key.StartsWith(state + ".", StringComparison.Ordinal)).ToList();
                if (own.Count == 0) continue;
                w.WriteStartObject(state);
                foreach (var (key, text) in own) w.WriteString(key[(state.Length + 1)..], text);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// One constituent as <c>model.json</c> writes it — what a <c>.cmc</c> records for a fitted constituent before
    /// export (brief-modelcomposer-7), so the two cannot spell one differently. Not validated here: a constituent
    /// is validated with its model.
    /// </summary>
    public static string ConstituentJson(Constituent c)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            WriteConstituentValue(w, c);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The inverse of <see cref="ConstituentJson"/>; a malformed field is refused by <paramref name="field"/>.</summary>
    public static Constituent ParseConstituentJson(string json, string field)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CmodelFormatException(field, $"it is not JSON ({ex.Message})."); }
        using (doc) return ParseConstituent(doc.RootElement, field);
    }

    private static void WriteConstituent(Utf8JsonWriter w, string name, Constituent c)
    {
        w.WritePropertyName(name);
        WriteConstituentValue(w, c);
    }

    private static void WriteConstituentValue(Utf8JsonWriter w, Constituent c)
    {
        w.WriteStartObject();
        w.WriteString("Method", c.Method);
        w.WriteStartArray("Inputs");
        foreach (string i in c.Inputs) w.WriteStringValue(i);
        w.WriteEndArray();
        foreach (var e in c.Expressions) w.WriteString(e.Key, e.Value);
        if (c.Activation is { } act) w.WriteString("Activation", act.ToString());
        if (c.Smoothing is { } lambda) w.WriteNumber("Smoothing", lambda);
        if (c.Transform is { } tr)
        {
            w.WriteStartObject("Transform");
            w.WriteString("Kind", tr.Kind);
            w.WriteNumber("Scale", tr.Scale);
            w.WriteEndObject();
        }
        if (c.Domain is { } d)
        {
            w.WriteStartObject("Domain");
            w.WriteStartArray("Polygon");
            foreach (var (vgs, vds) in d.Polygon)
            {
                w.WriteStartArray();
                w.WriteNumberValue(vgs);
                w.WriteNumberValue(vds);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            if (d.Box.Count > 0)
            {
                w.WriteStartObject("Box");
                foreach (var (input, (lo, hi)) in d.Box)
                {
                    w.WriteStartArray(input);
                    w.WriteNumberValue(lo);
                    w.WriteNumberValue(hi);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        if (c.Method is Constituent.EquationMethod || c.Parameters.Count > 0)
        {
            w.WriteStartObject("Parameters");
            foreach (var p in c.Parameters) w.WriteNumber(p.Key, p.Value);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    // ── JSON field helpers: every miss names its field ───────────────────────

    private static JsonElement Obj(JsonElement e, string name, string? field = null)
    {
        field ??= name;
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.Object) throw new CmodelFormatException(field, "it is not an object.");
        return v;
    }

    private static string Str(JsonElement e, string name, string field)
    {
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.String) throw new CmodelFormatException(field, "it is not a string.");
        return v.GetString()!;
    }

    private static string OptStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double Num(JsonElement e, string name, string field)
    {
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.Number) throw new CmodelFormatException(field, "it is not a number.");
        return v.GetDouble();
    }

    private static int Int(JsonElement e, string name, string? field = null)
    {
        field ??= name;
        double d = Num(e, name, field);
        if (d != Math.Floor(d) || d > int.MaxValue || d < int.MinValue)
            throw new CmodelFormatException(field, $"{d.ToString(CultureInfo.InvariantCulture)} is not a whole number.");
        return (int)d;
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name, string field)
    {
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.Array) throw new CmodelFormatException(field, "it is not a list.");
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) throw new CmodelFormatException(field, "an entry is not a string.");
            list.Add(x.GetString()!);
        }
        return list;
    }
}
