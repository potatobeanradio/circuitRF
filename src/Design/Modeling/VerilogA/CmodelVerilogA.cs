// A composed FET as Verilog-A source (brief-modelcomposer-26, -27; overview D17, D18; docs/design/model-composer.md §25).
//
// Write turns the ComposedModel CmodelFile.Read returns — or the one CmodelExport.Assemble builds in memory — into one
// self-contained module: the extrinsic network, the core's constituents as functions returning value and gradient, guided
// extrapolation, drain-source exchange, drift charges, periphery scaling and the device temperature (MC-26); the
// dispersion node, the trap states, self-heating with its thermal pin, and every instance switch with the bias point the
// linear-charge switches freeze at (MC-27). Equations and ANNs are written as code and compute the .cmodel's numbers to
// rounding; a table is refused by name (D17). Every refusal is collected and reported in one message. Export writes the
// file by a temporary file and a rename, from a .cmodel or from a .cmc assembled as the .cmodel export assembles it —
// the one function the Export step, `model export -o x.va` and the MCP `model` tool call (MC-28).

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using static CircuitRF.Design.Modeling.VerilogA.VaText;

namespace CircuitRF.Design.Modeling.VerilogA;

/// <summary>How a model is written as Verilog-A.</summary>
public sealed record VerilogAOptions
{
    /// <summary>The module's name in place of the one the <c>.cmodel</c>'s file name gives it; refused unless it is a legal
    /// identifier.</summary>
    public string? ModuleName { get; init; }

    /// <summary>The <c>.cmodel</c>'s file name, which names the module and the header. <see cref="CmodelVerilogA.Export"/>
    /// sets it; null names the module <c>composed_fet</c>.</summary>
    public string? SourceName { get; init; }

    /// <summary>The provenance timestamp a model assembled from a <c>.cmc</c> records, as <c>model export --created</c>
    /// gives a <c>.cmodel</c>; null records the time of the export. A <c>.cmodel</c> keeps its own.</summary>
    public string? Created { get; init; }

    public static VerilogAOptions Default { get; } = new();
}

/// <summary>A model that cannot be written as Verilog-A. Every reason is collected: <see cref="Refusals"/> holds each
/// field and its sentence, and the message is one line per refusal, each beginning with its field.</summary>
public sealed class VerilogAExportException(IReadOnlyList<(string Field, string Sentence)> refusals)
    : Exception(string.Join("\n", refusals.Select(r => $"{r.Field}: {r.Sentence}")))
{
    public IReadOnlyList<(string Field, string Sentence)> Refusals { get; } = refusals;

    /// <summary>The first refusal's field.</summary>
    public string Field => Refusals[0].Field;
}

/// <summary>A written module: where it went, what it is called, how many parameters it declares, and the switches that
/// change nothing for this model (each names a part the model does not have).</summary>
public sealed record VerilogAExportResult(string Path, string ModuleName, long Bytes, int Parameters, IReadOnlyList<string> InertSwitches);

public static class CmodelVerilogA
{
    /// <summary>The value <c>Temp</c> and <c>TjQ</c> take when they are not set: −273.15 °C, which circuitRF refuses as a
    /// device temperature, so it can mean nothing else.</summary>
    public const double TempUnset = -273.15;

    /// <summary>The value <c>VgsQ</c> and <c>VdsQ</c> take when they are not set: −1e300 V, which no bias point can be.</summary>
    public const double BiasUnset = -1e300;

    /// <summary>The constituents in the order the module evaluates and reports them.</summary>
    private static readonly string[] ConstituentNames = ["Id", "Ig", "Qg", "Qd", "IdRF"];

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the module of <paramref name="path"/> — a <c>.cmodel</c>, or a <c>.cmc</c> assembled as
    /// <see cref="CmodelExport.Export"/> assembles it — to <paramref name="vaPath"/> by a temporary file and a rename.
    /// Refused, failed or cancelled, nothing is written. The header's SHA-256 is of <c>model.json</c> as the
    /// <c>.cmodel</c> holds it, or would hold it. A <c>.cmc</c>'s refusals are the assembly's (<see cref="ModelingException"/>,
    /// a stale step first) and then the writer's.
    /// </summary>
    public static VerilogAExportResult Export(string path, string vaPath, VerilogAOptions? options = null, RunControl? rc = null)
    {
        rc?.BeginStage("Verilog-A", 1);
        options ??= VerilogAOptions.Default;
        vaPath = Path.GetFullPath(vaPath);
        ComposedModel model;
        string sha;
        if (path.EndsWith(CmcFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            model = CmodelExport.Assemble(path, CmcFile.Read(path), Path.GetDirectoryName(vaPath)!, created: options.Created, rc: rc);
            sha = Convert.ToHexStringLower(SHA256.HashData(CmodelFile.ModelJson(model)));
        }
        else
        {
            model = CmodelFile.Read(path);
            using var zip = ZipFile.OpenRead(path);
            using var s = zip.GetEntry("model.json")!.Open();
            sha = Convert.ToHexStringLower(SHA256.HashData(s));
        }
        options = options with { SourceName = Path.GetFileName(path) };
        var (text, parameters, inert) = Render(model, options, sha);
        rc?.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(vaPath)!);
        string tmp = vaPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var bytes = new UTF8Encoding(false).GetBytes(text);
        try
        {
            File.WriteAllBytes(tmp, bytes);
            rc?.ThrowIfCancellationRequested();
            File.Move(tmp, vaPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        rc?.TickStage();
        return new VerilogAExportResult(vaPath, ModuleName(options), bytes.Length, parameters, inert);
    }

    /// <summary>The module as text — the same model always the same bytes.</summary>
    public static string Write(ComposedModel model, VerilogAOptions options)
        => Render(model, options, Convert.ToHexStringLower(SHA256.HashData(CmodelFile.ModelJson(model)))).Text;

    /// <summary>
    /// The same module as <see cref="Write"/>, as a FRAGMENT to inline into a larger module (brief vendorlib-16
    /// R-vl16-1b): every name it declares prefixed by <paramref name="prefix"/>, and each name <paramref name="bind"/> maps
    /// — its terminals, its ground — bound to the host's node. One writer, two outputs: this renders the standalone module
    /// and renames it, so the equations are never written twice, and every refusal <see cref="Write"/> makes (a table
    /// constituent above all) is made here unchanged.
    /// </summary>
    internal static VaFragment Fragment(ComposedModel model, VerilogAOptions options, string prefix, IReadOnlyDictionary<string, string> bind)
        => VaFragment.From(Write(model, options), prefix, bind);

    /// <summary>
    /// What blocks a Verilog-A export of the document's fitted constituents, before anything is assembled: one line naming
    /// every table — <c>Id and Qg are tables; Verilog-A export needs ANN or equation constituents.</c> — or "" when there
    /// is none. The Export step shows it; the writer's own refusal names each table's field.
    /// </summary>
    public static string TablesLine(CmcDocument doc)
    {
        var current = CurrentFit.Record(doc);
        var charge = ChargeFit.Record(doc);
        (string Name, Constituent? C)[] all =
            [("Id", current?.Id), ("Ig", current?.Ig), ("IdRF", current?.IdRF), ("Qg", charge?.Qg), ("Qd", charge?.Qd)];
        var tables = all.Where(c => c.C?.Method == Constituent.TableMethod).Select(c => c.Name).ToList();
        if (tables.Count == 0) return "";
        string names = tables.Count == 1 ? tables[0] : $"{string.Join(", ", tables.SkipLast(1))} and {tables[^1]}";
        return $"{names} {(tables.Count == 1 ? "is a table" : "are tables")}; Verilog-A export needs ANN or equation constituents.";
    }

    private static string ModuleName(VerilogAOptions o)
        => o.ModuleName ?? ModuleNameFrom(Path.GetFileNameWithoutExtension(o.SourceName ?? "composed_fet"));

    private static (string Text, int Parameters, IReadOnlyList<string> Inert) Render(ComposedModel model, VerilogAOptions options, string modelSha)
    {
        CmodelFile.Validate(model);
        var refusals = new List<(string Field, string Sentence)>();

        string module = ModuleName(options);
        if (options.ModuleName is { } given && !IsIdentifier(given))
            refusals.Add(("ModuleName", $"'{given}' is not a legal Verilog-A module name; " +
                          (IsReserved(given) ? "it is a Verilog-A keyword or built-in name." : "use letters, digits and underscores, beginning with a letter.")));

        var k = model.Core;
        foreach (var (name, c) in Constituents(k))
            if (c.Method == Constituent.TableMethod)
                refusals.Add(($"Core.Constituents.{name}.Method",
                    $"'Table' cannot be written as Verilog-A; refit {name} as an ANN or an equation to export it."));
        if (model.Traps is { } traps && VaTraps.Find(traps.Model) is null)
            refusals.Add(("Traps.Model", $"the trap model '{traps.Model}' has no Verilog-A writer, so a model with traps cannot be exported."));

        var network = VaNetwork.Build(model.Network, refusals);
        var names = new Names(refusals);
        var w = new Writer(model, module, modelSha, options.SourceName, network, names, refusals);
        w.Plan();
        if (refusals.Count > 0) throw new VerilogAExportException(refusals);
        return (w.Text(), w.ParameterCount, w.Inert);
    }

    private static IEnumerable<(string Name, Constituent C)> Constituents(CoreConstituents k)
    {
        yield return ("Id", k.Id);
        yield return ("Ig", k.Ig);
        yield return ("Qg", k.Qg);
        yield return ("Qd", k.Qd);
        if (k.IdRF is { } rf) yield return ("IdRF", rf);
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every identifier the module declares at its own scope, and what it is — so a network or parameter name that is a
    /// keyword, that is already taken, or (for a parameter) that differs from another only in case is refused, naming both.
    /// Many simulators read netlist parameter names without regard to case; renaming silently would change names the user
    /// chose and will look for in the other simulator.
    /// </summary>
    private sealed class Names(List<(string Field, string Sentence)> refusals)
    {
        private readonly Dictionary<string, string> _taken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _parameters = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A name the module itself declares.</summary>
        public void Own(string name, string what) => _taken[name] = what;

        /// <summary>A name the model chose; true when it is usable.</summary>
        public bool Claim(string field, string name, string what, bool parameter)
        {
            if (!IdentifierShape(name))
            {
                refusals.Add((field, $"{what} '{name}' is not a legal Verilog-A identifier."));
                return false;
            }
            if (IsReserved(name))
            {
                refusals.Add((field, $"{what} '{name}' is a Verilog-A keyword or built-in name; rename it to export."));
                return false;
            }
            if (_taken.TryGetValue(name, out var other))
            {
                refusals.Add((field, $"{what} '{name}' has the name of {other}; rename it to export."));
                return false;
            }
            if (parameter && _parameters.TryGetValue(name, out var same))
            {
                refusals.Add((field, $"the parameters '{same}' and '{name}' differ only in case, which many simulators read as one name; rename one to export."));
                return false;
            }
            _taken[name] = $"{what} '{name}'";
            if (parameter) _parameters[name] = name;
            return true;
        }

        public void OwnParameter(string name, string what)
        {
            Own(name, what);
            _parameters[name] = name;
        }

        private static bool IdentifierShape(string name)
            => name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
               && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    // ── The module ────────────────────────────────────────────────────────────

    private sealed class Writer(ComposedModel model, string module, string modelSha, string? source, VaNetwork? network,
                                Names names, List<(string Field, string Sentence)> refusals)
    {
        private const string Vgs = ComposedCoreProgram.Vgs, Vds = ComposedCoreProgram.Vds, T = ComposedCoreProgram.T;

        /// <summary>The thermal terminal, the internal node the thermal network sits on, and the dispersion node.</summary>
        private const string ThermalPin = ComposedNetworkTemplate.ThermalNet, ThermalNode = "ti", DispersionNode = "x";

        private readonly int _n0 = model.BasePeriphery.Fingers;
        private readonly bool _range = model.Temperature.Mode == TemperatureMode.Range;
        private readonly bool _exchange = model.Core.Symmetry == DrainSourceSymmetry.Exchange;
        private readonly bool _drift = model.Core.Qg.Method == Constituent.DriftMethod;
        private readonly bool _dispersive = model.Core.IdRF is not null;
        /// <summary>The continuation's added conductance follows the edge (brief-modelcomposer-29).</summary>
        private bool Local => model.Extrapolation?.Mode == ExtrapolationMode.Local;
        private readonly ModelThermal? _thermal = model.Thermal;
        private readonly ModelTraps? _traps = model.Traps;
        private readonly TrapDynamics? _dynamics = model.Traps is { } t ? TrapModels.Build(t, "Verilog-A") : null;
        private readonly ITrapVerilogA? _trapVa = model.Traps is { } t ? VaTraps.Find(t.Model) : null;
        private bool Shifted => _traps is { Shifted: true };
        private IReadOnlyList<string> States => _traps?.States ?? [];

        /// <summary>The temperature the trap states read: the device's, or a one-temperature model's own.</summary>
        private string TrapT => _range ? T : Num(model.Temperature.T0);

        /// <summary>The ambient a self-heating model's temperature rises from: <c>Tamb</c>, which the range holds.</summary>
        private string Ambient => _thermal is null ? T : "Tamb";

        /// <summary>One line of the header's parameter table: name, unit, default, meaning.</summary>
        private readonly List<(string Name, string Unit, string Default, string Meaning)> _doc = [];
        private readonly List<string> _parameters = [];
        private readonly List<string> _functions = [];

        /// <summary>The module parameter each equation constituent's parameter is, by constituent and name.</summary>
        private readonly Dictionary<(string, string), string> _equationParameter = [];

        /// <summary>What a warning or a refusal calls the model: its file name, safe inside a Verilog-A string.</summary>
        private string Who => new([.. Ascii(source ?? module).Select(c => c is '"' or '\\' or '%' ? '_' : c)]);

        /// <summary>How many parameters the module declares, each a row of the header's table.</summary>
        public int ParameterCount => _doc.Count;

        /// <summary>The switches that name a part this model does not have, in <see cref="ComposedSwitches.Names"/>' order.</summary>
        public IReadOnlyList<string> Inert => [.. ComposedSwitches.Names.Where(s => ComposedSwitches.MissingPart(s, model, _dynamics) is not null)];

        public void Plan()
        {
            foreach (string own in VaNetwork.PinNodes) names.Own(own, $"the module's node '{own}'");
            names.Own(VaNetwork.Ground, "the module's ground node");
            if (_thermal is { } th)
            {
                names.Own(ThermalPin, "the module's thermal terminal 't'");
                names.Own(ThermalNode, $"the module's thermal node '{ThermalNode}'");
                foreach (string net in ComposedNetworkTemplate.ThermalNetNames(th).Skip(1))
                    names.Own(net, $"the module's thermal node '{net}'");
            }
            if (_dispersive) names.Own(DispersionNode, $"the module's dispersion node '{DispersionNode}'");
            foreach (string s in States) names.Own(s, $"the module's trap-state node '{s}'");

            foreach (string v in (string[])["kN", "Tdev", "Tamb", "vgs", "vds", "ugs", "uds", "ugi", "dvs", "id", "idrf", "idch",
                                            "idd", "pw", "igs", "igd", "qg", "qd", "swap", "qgs", "qgd", "qds", "qdr", "th_rf",
                                            "th_cf", "g_vgs", "g_vds", "g_T", T])
                names.Own(v, $"the module's own variable '{v}'");
            foreach (string v in BiasVariables) names.Own(v, $"the module's own variable '{v}'");
            foreach (string s in States)
                foreach (string prefix in (string[])["s_", "g_", "bq_", "r_", "dc_"])
                    names.Own(prefix + s, $"the module's own name '{prefix}{s}'");
            foreach (string payload in (string[])["Id", "IgGs", "IgGd", "Qg", "Qd", "IdRF"])
                foreach (string prefix in (string[])["f_", "x_", "fh_", "xg_"]) names.Own(prefix + payload, $"the module's function '{prefix}{payload}'");
            foreach (string piece in DriftCharges.QgKeys.Concat(DriftCharges.QdKeys).Distinct())
                foreach (string prefix in (string[])["f_", "b_", "fh_", "bg_"]) names.Own(prefix + piece, $"the module's function '{prefix}{piece}'");
            names.Own("qh", "the module's function 'qh'");

            names.OwnParameter("N", "the finger count N");
            names.OwnParameter("Temp", "the device temperature Temp");
            names.OwnParameter(ElaborationNames.Rth, "the thermal resistance Rth");
            names.OwnParameter(ElaborationNames.Cth, "the thermal capacitance Cth");
            foreach (string s in ComposedSwitches.Names) names.OwnParameter(s, $"the switch {s}");
            foreach (string q in (string[])["VgsQ", "VdsQ", "TjQ"]) names.OwnParameter(q, $"the bias-point parameter {q}");

            Parameter("N", "integer", Num0(_n0), "from [1:inf)", "-",
                      $"finger count, a whole number; the model was measured at N0 = {_n0} ({Ascii(model.BasePeriphery.UnitWidth)} each)");
            Parameter("Temp", "real", Num(TempUnset), $"from [{Num(TempUnset)}:inf)", "degC",
                      "the device's temperature; unset follows the simulator's" +
                      (_range ? $", clamped to the model's {Num(model.Temperature.Tmin)} to {Num(model.Temperature.Tmax)} degC"
                              : $"; the model is valid at {Num(model.Temperature.T0)} degC") +
                      (_thermal is null ? "" : "; a self-heating device's channel rises above it"),
                      "unset");
            ThermalParameters();
            Switches();
            BiasParameters();

            if (network is not null)
            {
                foreach (string net in network.Nets)
                    names.Claim("Network.Netlist", net, "the network's net", parameter: false);
                foreach (var e in network.Elements.Where(e => e.Type == "L"))
                    names.Claim("Network.Netlist", e.BranchName, $"the branch of inductor '{e.Name}'", parameter: false);
                foreach (var v in network.Variables)
                {
                    if (!names.Claim("Network.Netlist", v.Name, "the network variable", parameter: true)) continue;
                    if (v.Scaled != v.Name) names.Claim("Network.Netlist", v.Scaled, $"the scaled copy of '{v.Name}'", parameter: false);
                    Parameter(v.Name, "real", Num(v.Value), v.Positive ? "from (0:inf)" : null, v.Unit ?? "-",
                              "network variable" + v.Rule switch
                              {
                                  PeripheryScaling.PerFinger => ", scaled x N/N0 (perFinger)",
                                  PeripheryScaling.PerFingerInverse => ", scaled x N0/N (perFingerInverse)",
                                  _ => "",
                              });
                }
            }

            foreach (var (name, c) in Constituents(model.Core))
            {
                if (c.Method != Constituent.EquationMethod) continue;
                foreach (var p in c.Parameters)
                {
                    string mp = $"{name}_{p.Key}";
                    if (!names.Claim($"Core.Constituents.{name}.Parameters.{p.Key}", mp, $"the parameter of {name}", parameter: true)) continue;
                    _equationParameter[(name, p.Key)] = mp;
                    Parameter(mp, "real", Num(p.Value), null, "-", $"Core.Constituents.{name}.Parameters.{p.Key}");
                }
            }

            if (model.Extrapolation is { } x)
                foreach (string key in ModelExtrapolation.Keys)
                    if (x.Gx.TryGetValue(key, out double gx))
                    {
                        string mp = $"Gx_{key}";
                        if (!names.Claim($"Extrapolation.Gx.{key}", mp, "the extrapolation conductance", parameter: true)) continue;
                        Parameter(mp, "real", Num(gx), "from (0:inf)", "S", x.Mode == ExtrapolationMode.Local
                                  ? $"the cap on guided extrapolation's added conductance on {key}'s own branch (ten times the edge's own), at the base periphery"
                                  : $"guided extrapolation's added conductance on {key}'s own branch, at the base periphery");
                    }

            TrapParameters();

            if (refusals.Count == 0) Functions();
        }

        private void Parameter(string name, string type, string value, string? range, string unit, string meaning,
                               string? shownDefault = null)
        {
            _parameters.Add($"  parameter {type} {name} = {value}{(range is null ? "" : " " + range)};");
            _doc.Add((name, unit, shownDefault ?? value, meaning));
        }

        private static string Num0(int n) => n.ToString(CultureInfo.InvariantCulture);

        // ── the instance's own parameters (R-mc27-3, -4, -5) ──────────────────

        /// <summary>The instance <c>Rth</c> and <c>Cth</c>, with the meaning the elaborator gives them: unset is the model's
        /// scaled network, a value is this device's own at its finger count. A model that does not self-heat refuses a value,
        /// as the elaborator does, by its range.</summary>
        private void ThermalParameters()
        {
            if (_thermal is null)
            {
                const string none = "must stay unset (0): the model does not self-heat";
                Parameter(ElaborationNames.Rth, "real", "0.0", "from [0:0]", "degC/W", "the device's own thermal resistance; " + none, "unset");
                Parameter(ElaborationNames.Cth, "real", "0.0", "from [0:0]", "J/degC", "the device's own thermal capacitance; " + none, "unset");
                return;
            }
            string ladder = _thermal.Poles.Count > 1 ? $", every one of the ladder's {_thermal.Poles.Count} sections by the one factor that keeps the shape" : "";
            Parameter(ElaborationNames.Rth, "real", "0.0", "from [0:inf)", "degC/W",
                      $"the device's own DC thermal resistance at N fingers{ladder}; unset (0) is the model's, x N0/N", "unset");
            Parameter(ElaborationNames.Cth, "real", "0.0", "from [0:inf)", "J/degC",
                      $"the device's own total thermal capacitance at N fingers{ladder}; unset (0) is the model's, x N/N0", "unset");
        }

        /// <summary>The ten switches as integers, in <see cref="ComposedSwitches.Names"/>' order with its defaults; one naming
        /// a part the model lacks still exists and changes nothing (R-mc27-4).</summary>
        private void Switches()
        {
            foreach (string s in ComposedSwitches.Names)
            {
                string meaning = s switch
                {
                    ComposedSwitches.ChargeName => "0: no intrinsic charge (the network's capacitors and the state nodes' own charges stay)",
                    ComposedSwitches.LinearCName => "1: the intrinsic charge linearised at the bias point VgsQ, VdsQ, TjQ",
                    ComposedSwitches.LinearCgsName => "1: Cgs = C11 + C12 held at its bias-point value, the others following the model",
                    ComposedSwitches.LinearCdgName => "1: Cdg = -C12 held at its bias-point value",
                    ComposedSwitches.LinearCdsName => "1: Cds = C22 + C12 held at its bias-point value",
                    ComposedSwitches.LinearCmName => "1: the transcapacitance Cm = C21 - C12 held at its bias-point value",
                    ComposedSwitches.GateLagName => "0: every gate-lag trap state takes its DC value at the instantaneous voltages",
                    ComposedSwitches.DrainLagName => "0: every drain-lag trap state takes its DC value at the instantaneous voltages",
                    ComposedSwitches.SelfHeatingName => "0: evaluated at the ambient, no power into the thermal network",
                    _ => "0: the drain carries IdDC at every frequency",
                };
                if (ComposedSwitches.MissingPart(s, model, _dynamics) is { } missing)
                    meaning += $"; does nothing here: the model {missing}";
                Parameter(s, "integer", ComposedSwitches.DefaultOf(s) ? "1" : "0", "from [0:1]", "-", meaning);
            }
        }

        private void BiasParameters()
        {
            Parameter("VgsQ", "real", Num(BiasUnset), null, "V",
                      "the INTRINSIC gate-source voltage at the bias point the linear-charge switches freeze at", "unset");
            Parameter("VdsQ", "real", Num(BiasUnset), null, "V",
                      "the INTRINSIC drain-source voltage at the bias point", "unset");
            Parameter("TjQ", "real", Num(TempUnset), $"from [{Num(TempUnset)}:inf)", "degC",
                      "the junction temperature at the bias point; unset is the device's temperature" +
                      (_thermal is null ? "" : " (for a self-heating device, the ambient plus the DC rise)"), "unset");
        }

        /// <summary>The trap model's own parameters, then the shift coupling's strengths (R-mc27-2).</summary>
        private void TrapParameters()
        {
            if (_traps is not { } traps || _trapVa is null) return;
            foreach (var p in _trapVa.Parameters(traps))
            {
                if (!names.Claim(p.Field, p.Name, "the trap parameter", parameter: true)) continue;
                Parameter(p.Name, "real", Num(p.Value), p.Range, p.Unit, p.Meaning);
            }
            if (!Shifted) return;
            var definition = TrapModels.Find(traps.Model)!;
            foreach (string s in traps.States)
            {
                string strength = definition.ShiftParameter(s)!;
                if (!names.Claim($"Traps.Parameters.{strength}", strength, "the shift strength", parameter: true)) continue;
                Parameter(strength, "real", Num(traps.Parameter(strength)!.Value), null, "V/V",
                          $"{s}'s strength in the Shift coupling's gate-voltage shift");
            }
        }

        // ── constituents ──────────────────────────────────────────────────────

        /// <summary>What the analog block calls for each payload: <c>id = x_Id(ugs, uds, T)</c>.</summary>
        private readonly List<(string Var, string Call, string Constituent)> _calls = [];

        /// <summary>An input at the circuit's point: the intrinsic voltages (or their image), the device temperature, a
        /// trap state's value.</summary>
        private static string CircuitArg(string input) => input switch { Vgs => "ugs", Vds => "uds", T => T, _ => "s_" + input };

        private void Functions()
        {
            var k = model.Core;
            // Under a Shift coupling Id is evaluated at the gate voltage the states shift (MC-19).
            Payload("Id", k.Id, k.Id.PayloadKey, "id", own: Branch(k.Id.Inputs, 0, 1),
                    arg: i => Shifted && i == Vgs ? "ugi" : CircuitArg(i));
            Payload("Ig", k.Ig, "IgGs", "igs", own: Branch(k.Ig.Inputs, 1, 0));
            Payload("Ig", k.Ig, "IgGd", "igd", own: Branch(k.Ig.Inputs, 1, -1));
            if (k.IdRF is { } rf) Payload("IdRF", rf, rf.PayloadKey, "idrf", own: Branch(rf.Inputs, 0, 1));
            if (_drift) DriftFunctions();
            else
            {
                Payload("Qg", k.Qg, k.Qg.PayloadKey, "qg", own: null);
                Payload("Qd", k.Qd, k.Qd.PayloadKey, "qd", own: null);
            }
            TrapFunctions();
            HeldCharge();
        }

        private static double[] Branch(IReadOnlyList<string> inputs, double vgs, double vds)
            => [.. inputs.Select(i => i == Vgs ? vgs : i == Vds ? vds : 0.0)];

        private void Payload(string name, Constituent c, string key, string variable, double[]? own, Func<string, string>? arg = null)
        {
            arg ??= CircuitArg;
            string payload = name == "Ig" ? key : name;
            string field = $"Core.Constituents.{name}" + (name == "Ig" ? $".{key}" : $".{c.PayloadKey}");
            var inputs = c.Inputs;
            string args = string.Join(", ", inputs.Select(arg));

            if (c.Method == Constituent.EquationMethod)
            {
                string expression = c.Expression(key)!;
                var eq = new VaEquation(inputs, n => inputs.Contains(n) ? n : _equationParameter.GetValueOrDefault((name, n), n));
                (string Value, string?[] Grad) r;
                try { r = eq.Emit(Parser.Parse(expression)); }
                catch (VaUntranslatableException ex)
                {
                    refusals.Add((field, $"{ex.Message} cannot be written as Verilog-A."));
                    return;
                }
                string fn = $"f_{payload}";
                _functions.Add(EquationFunction(fn, $"{field} (Equation): {Ascii(expression)}", inputs, eq, r));
                _calls.Add((variable, $"{fn}({args}, {string.Join(", ", inputs.Select(i => "g_" + i))})", payload));
                return;
            }

            // An ANN, continued by guided extrapolation.
            var net = VaAnnData.Read(model, c.Expression(key)!, inputs.Count, c.Activation ?? AnnActivation.Tanh);
            string hidden = string.Join(" x ", Enumerable.Range(1, net.Layers - 1).Select(net.Width));
            _functions.Add(VaAnn.Function($"f_{payload}",
                $"{field} (ANN, {net.Layers - 1} hidden layer{(net.Layers == 2 ? "" : "s")} of {hidden}, {net.Activation})" +
                (c.Transform is { } t ? $", the current {Num(t.Scale)}*sinh(u) (Transform: Asinh)" : ""),
                inputs, net, c.Transform));
            string gx = $"Gx_{payload}";
            _functions.Add(VaDomain.Function($"x_{payload}",
                $"{field} continued outside Core.Constituents.{name}.Domain (guided extrapolation)",
                $"f_{payload}", inputs, c.Domain!, own is null ? null : gx, own, Local));
            _calls.Add((variable, $"x_{payload}({args})", payload));
        }

        private static string EquationFunction(string fn, string comment, IReadOnlyList<string> inputs, VaEquation eq,
                                               (string Value, string?[] Grad) r)
        {
            var sb = new StringBuilder();
            sb.Append($"  // {comment}\n");
            sb.Append($"  analog function real {fn};\n");
            sb.Append($"    input {string.Join(", ", inputs)};\n");
            sb.Append($"    output {string.Join(", ", inputs.Select(x => "d_" + x))};\n");
            sb.Append($"    real {string.Join(", ", inputs)};\n");
            sb.Append($"    real {string.Join(", ", inputs.Select(x => "d_" + x))};\n");
            foreach (var chunk in eq.Locals.Chunk(12)) sb.Append($"    real {string.Join(", ", chunk)};\n");
            sb.Append("    begin\n");
            foreach (string line in eq.Code) sb.Append($"      {line}\n");
            sb.Append($"      {fn} = {r.Value};\n");
            for (int i = 0; i < inputs.Count; i++) sb.Append($"      d_{inputs[i]} = {r.Grad[i] ?? "0.0"};\n");
            sb.Append("    end\n");
            sb.Append("  endfunction\n");
            return sb.ToString();
        }

        /// <summary>The drift decomposition (<see cref="DriftCharges"/>): each piece an ANN of its own variables, continued
        /// to first order beyond its box — and, for the bias-point charge, the same piece with its gradient.</summary>
        private void DriftFunctions()
        {
            var qg = model.Core.Qg;
            var qd = model.Core.Qd;
            var box = qd.Domain!.Box.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            void Piece(Constituent c, string owner, string key, string[] inputs, string[] intervals)
            {
                var net = VaAnnData.Read(model, c.Expression(key)!, inputs.Length, c.Activation ?? AnnActivation.Tanh);
                string field = $"Core.Constituents.{owner}.{key}";
                var bounds = intervals.Select(i => box[i]).ToArray();
                _functions.Add(VaAnn.Function($"f_{key}", $"{field} (ANN of {string.Join(", ", inputs)}, drift decomposition)",
                                              inputs, net, null));
                _functions.Add(VaDomain.BoxFunction($"b_{key}", $"{field} continued to first order outside Core.Constituents.Qd.Domain.Box",
                                                    $"f_{key}", inputs, bounds));
                _functions.Add(VaAnn.HessianFunction($"fh_{key}", $"{field} with its Hessian along a direction", inputs, net, null));
                _functions.Add(VaDomain.BoxGradientFunction($"bg_{key}", $"{field} continued beyond its box, with its gradient",
                                                            $"fh_{key}", inputs, bounds));
            }
            Piece(qg, "Qg", DriftCharges.Qgs, ["vgs"], [DriftCharges.Qgs]);
            Piece(qg, "Qg", DriftCharges.Qgd, ["vgd"], [DriftCharges.Qgd]);
            Piece(qd, "Qd", DriftCharges.Qds, ["vds"], [DriftCharges.Qds]);
            Piece(qg, "Qg", DriftCharges.Qdrift, ["vgd", "id"], [DriftCharges.DriftVgd, DriftCharges.DriftId]);
        }

        /// <summary>Each trap state's rate and DC value, from the model's own writer (R-mc27-2).</summary>
        private void TrapFunctions()
        {
            if (_traps is not { } traps || _trapVa is null) return;
            for (int k = 0; k < traps.States.Count; k++)
            {
                string s = traps.States[k];
                try
                {
                    _functions.Add(_trapVa.Rate(traps, k, "r_" + s));
                    _functions.Add(_trapVa.Dc(traps, k, "dc_" + s));
                }
                catch (VaUntranslatableException ex)
                {
                    refusals.Add(($"Traps.Expressions.{s}", $"{ex.Message} cannot be written as Verilog-A."));
                }
            }
        }

        // ── the bias-point charge (R-mc27-5) ──────────────────────────────────

        /// <summary>The module variables the bias-point block assigns.</summary>
        private static readonly string[] BiasVariables =
            ["bq_T", "bq_qg", "bq_qd", "bq_c11", "bq_c12", "bq_c21", "bq_c22", "bq_d0", "bq_d1", "bq_x", "bq_a", "bq_b", "bq_c",
             "bq_e", "bq_i0", "bq_i1", "bq_i2", "bq_i3", "bq_r", "bq_on", "bq_y"];

        /// <summary>The gradient-returning call of a charge or channel payload at the held point: an equation's own function,
        /// or an ANN's continuation with its exact gradient (built here on first use).</summary>
        private string GradientCall(string name, Constituent c, string payload, double[]? own, Func<string, string> arg,
                                    string outPrefix, HashSet<string> locals)
        {
            var inputs = c.Inputs;
            foreach (string i in inputs) locals.Add(outPrefix + i);
            string outs = string.Join(", ", inputs.Select(i => outPrefix + i));
            string args = string.Join(", ", inputs.Select(arg));
            if (c.Method == Constituent.EquationMethod) return $"f_{payload}({args}, {outs})";

            if (!_functions.Any(f => f.Contains($"analog function real xg_{payload};", StringComparison.Ordinal)))
            {
                string field = $"Core.Constituents.{name}.{c.PayloadKey}";
                var net = VaAnnData.Read(model, c.Expression(c.PayloadKey)!, inputs.Count, c.Activation ?? AnnActivation.Tanh);
                _functions.Add(VaAnn.HessianFunction($"fh_{payload}", $"{field} with its Hessian along a direction", inputs, net, c.Transform));
                _functions.Add(VaDomain.GradientFunction($"xg_{payload}",
                    $"{field} continued outside its domain, with the continuation's exact gradient",
                    $"f_{payload}", $"fh_{payload}", inputs, c.Domain!, own is null ? null : $"Gx_{payload}", own, Local));
            }
            return $"xg_{payload}({args}, {outs})";
        }

        /// <summary>
        /// <c>qh</c>: the intrinsic charge with its temperature and trap inputs held, at any (vgs, vds), returning <c>Qg</c>
        /// and writing <c>Qd</c> and <c>C = ∂(Qg, Qd)/∂(vgs, vds)</c> in the terminal frame, unscaled — the surface
        /// <c>Q̃</c> circuitRF's <c>HeldSurface</c> reads, from the emitted gradients. The compiler supplies its derivative
        /// where the module needs one.
        /// </summary>
        private void HeldCharge()
        {
            var k = model.Core;
            string Held(string input) => input switch { Vgs => "ug", Vds => "ud", T => "Th", _ => "h_" + input };
            var locals = new HashSet<string>(StringComparer.Ordinal) { "ug", "ud", "qa", "qb", "qc", "qr", "ich", "e0", "e1", "ta", "tb" };
            var body = new StringBuilder();
            body.Append("      ug = vg;\n      ud = vd;\n");
            if (_exchange)
            {
                body.Append("      im = 0;\n");
                body.Append("      if (vd < 0.0) begin ug = vg - vd; ud = -vd; im = 1; end\n");
            }
            if (!_drift)
            {
                body.Append($"      qa = {GradientCall("Qg", k.Qg, "Qg", null, Held, "ga_", locals)};\n");
                body.Append($"      qb = {GradientCall("Qd", k.Qd, "Qd", null, Held, "gb_", locals)};\n");
                body.Append("      c11 = ga_vgs;\n      c12 = ga_vds;\n      c21 = gb_vgs;\n      c22 = gb_vds;\n");
            }
            else
            {
                // DriftCharges.Evaluate: the pieces, and Qdrift through vgd and through the channel current.
                var (chName, ch) = k.IdRF is { } rf ? ("IdRF", rf) : ("Id", k.Id);
                double kq = 1 - k.Qd.Parameters.FirstOrDefault(p => p.Key == DriftCharges.Lambda).Value;
                locals.UnionWith(["d1_vgs", "d2_vgd", "d3_vds", "d4_vgd", "d4_id"]);
                body.Append($"      ich = {GradientCall(chName, ch, chName, Branch(ch.Inputs, 0, 1), Held, "gi_", locals)};\n");
                body.Append($"      qa = bg_{DriftCharges.Qgs}(ug, d1_vgs);\n");
                body.Append($"      qb = bg_{DriftCharges.Qgd}(ug - ud, d2_vgd);\n");
                body.Append($"      qc = bg_{DriftCharges.Qds}(ud, d3_vds);\n");
                body.Append($"      qr = bg_{DriftCharges.Qdrift}(ug - ud, ich, d4_vgd, d4_id);\n");
                body.Append("      ta = qa + qb + qr;\n");
                body.Append($"      qb = -qb - {Operand(kq)}*qr + qc;\n");
                body.Append("      qa = ta;\n");
                body.Append("      e0 = d4_vgd + d4_id*gi_vgs;\n      e1 = -d4_vgd + d4_id*gi_vds;\n");
                body.Append("      c11 = d1_vgs + d2_vgd + e0;\n      c12 = -d2_vgd + e1;\n");
                body.Append($"      c21 = -d2_vgd - {Operand(kq)}*e0;\n      c22 = d2_vgd - {Operand(kq)}*e1 + d3_vds;\n");
            }
            if (_exchange)
            {
                // As the core maps the image back: ∂/∂vds = −∂′/∂vgs′ − ∂′/∂vds′, then Qd = −Qg′ − Qd′.
                body.Append("      if (im) begin\n");
                body.Append("        ta = c11; tb = c12; c12 = -ta - tb;\n");
                body.Append("        ta = c21; tb = c22; c22 = -ta - tb;\n");
                body.Append("        c21 = -c11 - c21;\n        c22 = -c12 - c22;\n");
                body.Append("        qb = -qa - qb;\n");
                body.Append("      end\n");
            }
            body.Append("      oqd = qb;\n      qh = qa;\n");

            var inputs = new List<string> { "vg", "vd" };
            if (_range) inputs.Add("Th");
            inputs.AddRange(States.Select(s => "h_" + s));
            var sb = new StringBuilder();
            sb.Append("  // The intrinsic charge with its temperature and trap inputs held (R-mc27-5): Qg returned, Qd and\n");
            sb.Append("  // C = d(Qg, Qd)/d(vgs, vds) written out, in the terminal frame and unscaled.\n");
            sb.Append("  analog function real qh;\n");
            sb.Append($"    input {string.Join(", ", inputs)};\n");
            sb.Append("    output oqd, c11, c12, c21, c22;\n");
            sb.Append($"    real {string.Join(", ", inputs)};\n");
            sb.Append("    real oqd, c11, c12, c21, c22;\n");
            foreach (var chunk in locals.Order(StringComparer.Ordinal).Chunk(12)) sb.Append($"    real {string.Join(", ", chunk)};\n");
            if (_exchange) sb.Append("    integer im;\n");
            sb.Append("    begin\n").Append(body).Append("    end\n");
            sb.Append("  endfunction\n");
            _functions.Add(sb.ToString());
        }

        // ── text ──────────────────────────────────────────────────────────────

        public string Text()
        {
            var sb = new StringBuilder();
            Header(sb);
            sb.Append("`include \"disciplines.vams\"\n");
            sb.Append("`include \"constants.vams\"\n\n");
            string pins = _thermal is null ? "g, d, s" : $"g, d, s, {ThermalPin}";
            sb.Append($"module {module}({pins});\n");
            sb.Append($"  inout {pins};\n");
            sb.Append("  electrical g, d, s;\n");
            if (_thermal is not null) sb.Append($"  thermal {ThermalPin};\n");
            sb.Append("  // Network.Netlist pins 4-6: the intrinsic gate, drain and source\n");
            sb.Append("  electrical gi, di, si;\n");
            if (network!.Nets.Count > 0)
            {
                sb.Append("  // Network.Netlist internal nets\n");
                sb.Append($"  electrical {string.Join(", ", network.Nets)};\n");
            }
            if (network.UsesGround) sb.Append($"  electrical {VaNetwork.Ground};\n  ground {VaNetwork.Ground};\n");
            if (_dispersive) sb.Append($"  // Core.Dispersion: the state node whose voltage is a current, A\n  electrical {DispersionNode};\n");
            if (States.Count > 0) sb.Append($"  // Traps: one node per enabled state, its voltage the state, V\n  electrical {string.Join(", ", States)};\n");
            if (_thermal is { } th)
                sb.Append($"  // Thermal: the network's own nodes; their temperature is the rise above the ambient, K\n" +
                          $"  thermal {string.Join(", ", ComposedNetworkTemplate.ThermalNetNames(th).Skip(1).Prepend(ThermalNode))};\n");
            var inductors = network.Elements.Where(e => e.Type == "L").ToList();
            if (inductors.Count > 0)
            {
                sb.Append("  // Network.Netlist inductors: each a branch whose current is an unknown\n");
                foreach (var e in inductors) sb.Append($"  branch ({e.A}, {e.B}) {e.BranchName};\n");
            }

            sb.Append("\n  // Parameters (the header lists them with their units)\n");
            foreach (string p in _parameters) sb.Append(p).Append('\n');

            sb.Append("\n  // The core's constituents: each payload a function returning its value and writing its gradient into\n");
            sb.Append("  // the d_ outputs; a table or ANN is evaluated at the nearest point of its training domain and continued.\n\n");
            foreach (string f in _functions) sb.Append(f).Append('\n');

            var scaled = network.Variables.Where(v => v.Scaled != v.Name).Select(v => v.Scaled).ToList();
            sb.Append("  real kN, Tdev, vgs, vds, ugs, uds, id, igs, igd, qg, qd, swap;\n");
            if (_range) sb.Append($"  real {T};\n");
            if (_thermal is not null) sb.Append("  real Tamb, th_rf, th_cf, pw;\n");
            if (_dispersive) sb.Append("  real idrf;\n");
            if (_dispersive || _thermal is not null) sb.Append("  real idd;\n");
            if (_drift) sb.Append("  real qgs, qgd, qds, qdr, idch;\n");
            if (States.Count > 0)
            {
                sb.Append($"  real {string.Join(", ", States.Select(s => "s_" + s))};\n");
                sb.Append($"  real {string.Join(", ", States.Select(s => "bq_" + s))};\n");
            }
            if (Shifted) sb.Append("  real ugi, dvs;\n");
            foreach (var chunk in BiasVariables.Chunk(12)) sb.Append($"  real {string.Join(", ", chunk)};\n");
            sb.Append("  // the equations' gradients, which the core does not read\n");
            var gradients = new List<string> { "g_vgs", "g_vds" };
            if (_range) gradients.Add("g_T");
            gradients.AddRange(States.Select(s => "g_" + s));
            sb.Append($"  real {string.Join(", ", gradients)};\n");
            foreach (var chunk in scaled.Chunk(12)) sb.Append($"  real {string.Join(", ", chunk)};\n");

            sb.Append("\n  analog begin\n");
            Periphery(sb);
            Temperature(sb);
            Thermal(sb);
            sb.Append("\n    // Network.Netlist\n");
            foreach (string c in network.Contributions()) sb.Append($"    {c}\n");
            Core(sb);
            sb.Append("  end\n");
            sb.Append("endmodule\n");
            return sb.ToString();
        }

        private void Header(StringBuilder sb)
        {
            var p = model.Provenance;
            sb.Append($"// {module}: a composed FET written as Verilog-A by circuitRF {Ascii(CmodelExport.Version)}");
            sb.Append(source is null ? ".\n" : $", from {Ascii(source)}.\n");
            sb.Append($"// Model created {Ascii(p.Created)} by circuitRF {Ascii(p.CircuitRfVersion)}; model.json SHA-256 {modelSha}.\n");
            sb.Append("//\n");
            sb.Append(_thermal is null
                ? "// Terminals: g (gate), d (drain), s (source)."
                : "// Terminals: g (gate), d (drain), s (source), t (thermal: its temperature is the channel's rise above the\n" +
                  "// ambient, K). Leave t unconnected and the model heats on its own network; wire it and whatever is attached\n" +
                  "// sits in parallel with that network.");
            sb.Append(" Every current, charge and network value scales from the\n");
            sb.Append($"// measured device's N0 = {_n0} fingers to N; the host's own device multiplier (m) multiplies the whole\n");
            sb.Append("// device, so the module declares none.\n");
            sb.Append("//\n");
            sb.Append("// Parameters: name, unit, default, meaning.\n");
            int w0 = Math.Max(4, _doc.Max(d => d.Name.Length)), w1 = Math.Max(4, _doc.Max(d => d.Unit.Length)),
                w2 = Math.Max(7, _doc.Max(d => d.Default.Length));
            foreach (var (name, unit, value, meaning) in _doc)
                sb.Append($"//   {name.PadRight(w0)}  {unit.PadRight(w1)}  {value.PadRight(w2)}  {Ascii(meaning)}\n");
            sb.Append("//\n");
            sb.Append($"// Temperature: {(_range ? $"the constituents read T, the device's temperature held to {Num(model.Temperature.Tmin)} to {Num(model.Temperature.Tmax)} degC" : $"a one-temperature model, valid at {Num(model.Temperature.T0)} degC")};\n");
            sb.Append("// a device outside that says so once, as a warning at the first step.\n");
            if (_thermal is not null)
                sb.Append("// Self-heating: T is that temperature plus the rise of the internal node ti, which the dissipated power\n" +
                          "// ig*vgs + id*vds heats through the thermal network.\n");
            if (_exchange)
                sb.Append("// Core.Symmetry: Exchange. A point with vds < 0 is evaluated at its image (vgs - vds, -vds), drain and\n" +
                          "// source exchanged.\n");
            sb.Append($"// Switches: {string.Join(", ", ComposedSwitches.Names.SkipLast(1))} and {ComposedSwitches.Names[^1]}, as a\n" +
                      "// placed ComposedFET has them; 1 is on.");
            var inert = Inert;
            string list = inert.Count <= 1 ? string.Concat(inert) : $"{string.Join(", ", inert.SkipLast(1))} and {inert[^1]}";
            sb.Append(inert.Count == 0 ? "\n" : $" For this model {list} do{(inert.Count == 1 ? "es" : "")} nothing.\n");
            sb.Append("// The bias point: LinearC and the four branch switches hold the intrinsic charge at VgsQ, VdsQ and TjQ, which\n" +
                      "// circuitRF finds itself from a DC solve and this module cannot. VgsQ and VdsQ are the INTRINSIC voltages,\n" +
                      "// V(gi, si) and V(di, si), which differ from the terminal bias by the drops on the network's resistances.\n" +
                      $"// Unset ({Num(BiasUnset)}) with such a switch on stops the run ($fatal) at the first step.\n");
            sb.Append("// A constituent that evaluates to a number that is not finite stops the run ($fatal), naming it and the point.\n");
            sb.Append("\n");
        }

        private void Periphery(StringBuilder sb)
        {
            sb.Append($"    // Periphery: N fingers of a device measured at N0 = {_n0}\n");
            sb.Append($"    kN = N/{Num0(_n0)}.0;\n");
            foreach (string s in network!.ScalingStatements(_n0)) sb.Append($"    {s}\n");
        }

        private void Temperature(StringBuilder sb)
        {
            var t = model.Temperature;
            sb.Append("\n    // Temperature: the device's own, or the simulator's\n");
            sb.Append($"    Tdev = (Temp > {Num(TempUnset)}) ? Temp : $temperature - `P_CELSIUS0;\n");
            if (_range)
            {
                sb.Append($"    {Ambient} = min(max(Tdev, {Num(t.Tmin)}), {Num(t.Tmax)});\n");
                sb.Append("    @(initial_step) begin\n");
                sb.Append($"      if (Tdev < {Num(t.Tmin)} - 1e-9 || Tdev > {Num(t.Tmax)} + 1e-9)\n");
                sb.Append($"        $warning(\"{Ascii(Who)} covers {Num(t.Tmin)} to {Num(t.Tmax)} degC; the device is at %g degC, so the model is evaluated at %g degC.\", Tdev, {Ambient});\n");
                sb.Append("    end\n");
            }
            else
            {
                sb.Append("    @(initial_step) begin\n");
                sb.Append($"      if (abs(Tdev - {Num(t.T0)}) > 1e-9)\n");
                sb.Append($"        $warning(\"{Ascii(Who)} is a one-temperature model, valid at {Num(t.T0)} degC; the device is at %g degC and the model is used as it is.\", Tdev);\n");
                sb.Append("    end\n");
            }
        }

        /// <summary>
        /// The thermal network (R-mc27-3) on the internal node <c>ti</c>, never on the terminal: a host commonly grounds an
        /// unconnected terminal, and a network written on it would then be shorted and the device would stop heating.
        /// Unconnected, the terminal is grounded by the module itself rather than left with no equation: a host that does
        /// not ground it — circuitRF does not, for a terminal beyond the placed pin count — would otherwise hold a node no
        /// equation determines, and circuitRF's S-parameter solve regularises such a matrix, moving S by about 2e-8.
        ///
        /// <para>The thermal nodes are read and driven through the generic <c>potential()</c> and <c>flow()</c>, not the
        /// thermal discipline's own <c>Temp()</c> and <c>Pwr()</c>: the module's parameter <c>Temp</c> — the instance
        /// parameter a placed <c>ComposedFET</c> has — hides the access function of the same name, and the compiler refuses
        /// <c>Temp(ti)</c> as a call of a parameter.</para>
        /// </summary>
        private void Thermal(StringBuilder sb)
        {
            if (_thermal is not { } th) return;
            double rSum = th.Rth, cSum = th.Poles.Sum(p => p.Cth);
            string n0 = Num0(_n0) + ".0";
            sb.Append("\n    // Thermal: Rth x N0/N, its slope taken at the ambient, and Cth x N/N0 - or the instance's own Rth and Cth\n");
            sb.Append($"    th_rf = ({ElaborationNames.Rth} > 0.0) ? {ElaborationNames.Rth}/{Operand(rSum)} : ({Operand(rSum)} + {Operand(th.RthSlope)}*(Tamb - {Operand(th.Tref)}))/{Operand(rSum)}*({n0}/N);\n");
            sb.Append($"    th_cf = ({ElaborationNames.Cth} > 0.0) ? {ElaborationNames.Cth}/{Operand(cSum)} : N/{n0};\n");
            sb.Append($"    {T} = Tamb + ((SelfHeating) ? potential({ThermalNode}) : 0.0);\n");
            sb.Append($"    // the terminal joins {ThermalNode} only when it is wired; left unconnected it is grounded on its own, and the\n");
            sb.Append("    // network, which stays on the internal node, keeps heating\n");
            sb.Append($"    if ($port_connected({ThermalPin})) potential({ThermalPin}, {ThermalNode}) <+ 0.0;\n");
            sb.Append($"    else potential({ThermalPin}) <+ 0.0;\n");
            var nets = ComposedNetworkTemplate.ThermalNetNames(th).Skip(1).Prepend(ThermalNode).ToList();
            var elements = ComposedNetworkTemplate.ThermalElementNames(th).ToList();
            for (int k = 0; k < th.Poles.Count; k++)
            {
                string a = nets[k], branch = k == th.Poles.Count - 1 ? a : $"{a}, {nets[k + 1]}";
                var pole = th.Poles[k];
                sb.Append($"    flow({branch}) <+ potential({branch})/({Operand(pole.Rth)}*th_rf) + ddt(({Operand(pole.Cth)}*th_cf)*potential({branch}));   // {elements[2 * k]} || {elements[2 * k + 1]}\n");
            }
        }

        private void Core(StringBuilder sb)
        {
            sb.Append("\n    // The core, on (gi, si) and (di, si), times N/N0\n");
            sb.Append("    vgs = V(gi, si);\n");
            sb.Append("    vds = V(di, si);\n");
            sb.Append("    ugs = vgs;\n");
            sb.Append("    uds = vds;\n");
            if (_exchange)
            {
                sb.Append("    // Core.Symmetry: Exchange - evaluated at the image of a point with vds < 0\n");
                sb.Append("    if (vds < 0.0) begin\n");
                sb.Append("      ugs = vgs - vds;\n");
                sb.Append("      uds = -vds;\n");
                sb.Append("    end\n");
            }
            TrapStates(sb);
            foreach (var (v, call, _) in _calls) sb.Append($"    {v} = {call};\n");
            if (_drift)
            {
                double k = 1 - model.Core.Qd.Parameters.FirstOrDefault(p => p.Key == DriftCharges.Lambda).Value;
                // The drift charge reads the channel as fitted: IdRF when the model has it, and never the Shift coupling's
                // shifted current (R-mc19-1).
                var idIn = model.Core.Id.Inputs;
                string unshifted = model.Core.Id.Method == Constituent.EquationMethod
                    ? $"f_Id({string.Join(", ", idIn.Select(CircuitArg))}, {string.Join(", ", idIn.Select(i => "g_" + i))})"
                    : $"x_Id({string.Join(", ", idIn.Select(CircuitArg))})";
                string channel = _dispersive ? "idrf" : Shifted ? unshifted : "id";
                sb.Append("    // Core.Constituents.Qg and Qd: the drift decomposition, reading the channel current\n");
                sb.Append($"    idch = {channel};\n");
                sb.Append("    qgs = b_QGS(ugs);\n");
                sb.Append("    qgd = b_QGD(ugs - uds);\n");
                sb.Append("    qds = b_QDS(uds);\n");
                sb.Append("    qdr = b_Qdrift(ugs - uds, idch);\n");
                sb.Append("    qg = qgs + qgd + qdr;\n");
                sb.Append($"    qd = -qgd - {Operand(k)}*qdr + qds;\n");
            }

            string at = $"vgs=%g, vds=%g{(_range ? ", T=%g" : "")}";
            string atArgs = $"ugs, uds{(_range ? ", T" : "")}";
            var checks = new List<(string, string)> { ("id", "Id"), ("igs", "IgGs"), ("igd", "IgGd"), ("qg", "Qg"), ("qd", "Qd") };
            if (_dispersive) checks.Add(("idrf", "IdRF"));
            foreach (var (v, name) in checks)
                sb.Append($"    if (!({v} == {v}) || abs({v}) > 1.0e308)\n" +
                          $"      $fatal(1, \"{Ascii(Who)}: constituent {name} evaluated to %g at {at}.\", {v}, {atArgs});\n");

            if (_exchange)
            {
                sb.Append("    if (vds < 0.0) begin\n");
                sb.Append("      id = -id;\n");
                if (_dispersive) sb.Append("      idrf = -idrf;\n");
                sb.Append("      swap = igs;\n");
                sb.Append("      igs = igd;\n");
                sb.Append("      igd = swap;\n");
                sb.Append("      qd = -qg - qd;\n");
                sb.Append("    end\n");
            }

            BiasPoint(sb);

            string drain = "id";
            if (_dispersive)
            {
                sb.Append("    // Core.Dispersion: the drain follows IdRF + x, and x relaxes to IdDC - IdRF with Tau\n");
                sb.Append($"    idd = (Dispersion) ? idrf + V({DispersionNode}) : id;\n");
                drain = "idd";
            }
            else if (_thermal is not null)
            {
                sb.Append("    idd = id;\n");
                drain = "idd";
            }
            sb.Append("    I(gi, si) <+ kN*(igs + igd);\n");
            sb.Append($"    I(di, si) <+ kN*({drain} - igd);\n");
            sb.Append("    I(gi, si) <+ ddt(kN*qg);\n");
            sb.Append("    I(di, si) <+ ddt(kN*qd);\n");

            if (_dispersive)
            {
                double tau = model.Core.Dispersion!.Tau;
                sb.Append($"    // the node is unscaled: N/N0 multiplies the drain current it feeds. Dispersion = 0 ties it to zero.\n");
                sb.Append("    if (Dispersion) begin\n");
                sb.Append($"      I({DispersionNode}) <+ V({DispersionNode}) - (id - idrf);\n");
                sb.Append($"      I({DispersionNode}) <+ ddt({Operand(tau)}*V({DispersionNode}));\n");
                sb.Append("    end else begin\n");
                sb.Append($"      V({DispersionNode}) <+ 0.0;\n");
                sb.Append("    end\n");
            }
            TrapNodes(sb);

            if (_thermal is not null)
            {
                sb.Append($"    // Self-heating: the dissipated power, ig*vgs + id*vds of the scaled currents, flows into {ThermalNode}\n");
                sb.Append($"    pw = (kN*(igs + igd))*vgs + (kN*({drain} - igd))*vds;\n");
                sb.Append($"    if (SelfHeating) flow({ThermalNode}) <+ -pw;\n");
            }
        }

        /// <summary>Each state's value at this point: its node's voltage, or its DC value at the instantaneous voltages when
        /// its lag is switched off (R-mc27-4) — written as code, so the compiler folds its lane into vgs, vds and T.</summary>
        private void TrapStates(StringBuilder sb)
        {
            if (_dynamics is not { } dyn) return;
            sb.Append("    // Traps: each state its node's voltage, or its DC value at the instantaneous voltages when its lag is off\n");
            for (int k = 0; k < States.Count; k++)
            {
                string s = States[k], lag = LagOf(k);
                sb.Append($"    s_{s} = ({lag}) ? V({s}) : dc_{s}(vgs, vds, {TrapT});\n");
            }
            if (!Shifted) return;
            sb.Append("    // Traps.Coupling: Shift - Id at the gate voltage the states shift, sum of a*sign*(phi - phi_DC)\n");
            sb.Append("    dvs = 0.0;\n");
            var definition = TrapModels.Find(_traps!.Model)!;
            for (int k = 0; k < States.Count; k++)
            {
                string s = States[k];
                string c = $"{definition.ShiftParameter(s)}*{Operand(dyn.CaptureSign(k))}";
                sb.Append($"    dvs = dvs + ({c})*(s_{s} - dc_{s}(vgs, vds, {TrapT}));\n");
            }
            sb.Append("    ugi = ugs - dvs;\n");
        }

        private string LagOf(int k) => _dynamics!.CaptureSign(k) < 0 ? ComposedSwitches.GateLagName : ComposedSwitches.DrainLagName;

        /// <summary>Each kept state's node: <c>RateTrapDynamics</c>' form, read at the intrinsic voltages and the device
        /// temperature; a state whose lag is off is tied to zero.</summary>
        private void TrapNodes(StringBuilder sb)
        {
            if (_dynamics is null) return;
            string cap = Num(RateTrapDynamics.NodeCapacitance);
            sb.Append("    // Trap-state nodes, unscaled: C*dphi/dt = C*rate, C the normalised node capacitance\n");
            for (int k = 0; k < States.Count; k++)
            {
                string s = States[k];
                sb.Append($"    if ({LagOf(k)}) begin\n");
                sb.Append($"      I({s}) <+ ddt({cap}*V({s})) - {cap}*r_{s}(V({s}), vgs, vds, {TrapT});\n");
                sb.Append("    end else begin\n");
                sb.Append($"      V({s}) <+ 0.0;\n");
                sb.Append("    end\n");
            }
        }

        /// <summary>
        /// The intrinsic charge as the switches have it (R-mc27-4, -5): none under <c>Charge = 0</c>; under a linear-charge
        /// switch, the charge circuitRF would freeze at the bias point the parameters state — <c>LinearC</c> its exact
        /// tangent, a branch switch <c>BranchLinearisation</c>'s residual by the same Gauss-Legendre rule — and, with that
        /// point unset, a refusal.
        /// </summary>
        private void BiasPoint(StringBuilder sb)
        {
            string anyLinear = string.Join(" || ", ComposedSwitches.Names.Where(s => s.StartsWith(ComposedSwitches.LinearCName, StringComparison.Ordinal)));
            sb.Append("\n    // The intrinsic charge as the switches have it\n");
            sb.Append($"    bq_on = (Charge && ({anyLinear})) ? 1.0 : 0.0;\n");
            sb.Append("    @(initial_step) begin\n");
            bool first = true;
            foreach (string s in ComposedSwitches.Names.Where(s => s.StartsWith(ComposedSwitches.LinearCName, StringComparison.Ordinal)))
            {
                sb.Append($"      {(first ? "" : "else ")}if (Charge && {s} && (VgsQ <= {Num(BiasUnset)} || VdsQ <= {Num(BiasUnset)}))\n");
                sb.Append($"        $fatal(1, \"{Ascii(Who)}: {s}=1 needs the bias point, and VgsQ or VdsQ is not set; set VgsQ and VdsQ to the intrinsic gate-source and drain-source voltages at the bias point, in V.\");\n");
                first = false;
            }
            if (_thermal is not null)
            {
                sb.Append($"      if (bq_on > 0.0 && SelfHeating && TjQ <= {Num(TempUnset)})\n");
                sb.Append($"        $warning(\"{Ascii(Who)}: TjQ is not set, so the charge is frozen at the ambient, %g degC, and not at the junction temperature at the bias point; set TjQ to the ambient plus the DC rise.\", Tamb);\n");
            }
            sb.Append("    end\n");

            sb.Append("    if (!Charge) begin\n");
            sb.Append("      qg = 0.0;\n");
            sb.Append("      qd = 0.0;\n");
            sb.Append("    end else if (bq_on > 0.0) begin\n");
            string held = _range ? $"(TjQ > {Num(TempUnset)}) ? TjQ : {Ambient}" : Num(model.Temperature.T0);
            sb.Append($"      // held at the bias point: its temperature, and every trap state at its DC value there\n");
            sb.Append($"      bq_T = {held};\n");
            foreach (string s in States) sb.Append($"      bq_{s} = dc_{s}(VgsQ, VdsQ, bq_T);\n");
            var heldArgs = new List<string>();
            if (_range) heldArgs.Add("bq_T");
            heldArgs.AddRange(States.Select(s => "bq_" + s));
            string H = heldArgs.Count == 0 ? "" : ", " + string.Join(", ", heldArgs);
            sb.Append($"      bq_qg = qh(VgsQ, VdsQ{H}, bq_qd, bq_c11, bq_c12, bq_c21, bq_c22);\n");
            sb.Append("      bq_d0 = vgs - VgsQ;\n");
            sb.Append("      bq_d1 = vds - VdsQ;\n");
            sb.Append("      if (LinearC) begin\n");
            sb.Append("        // LinearC: q(v_Q) + C(v_Q)*(v - v_Q), in the terminal frame\n");
            sb.Append("        qg = bq_qg + bq_c11*bq_d0 + bq_c12*bq_d1;\n");
            sb.Append("        qd = bq_qd + bq_c21*bq_d0 + bq_c22*bq_d1;\n");
            sb.Append("      end else begin\n");
            sb.Append("        // A branch held: the held surface, less each frozen branch's residual along the line from the bias point,\n");
            sb.Append($"        // r_b = (integral of C_b - C_b(v_Q))*du_b by {BranchLinearisation.Nodes}-point Gauss-Legendre\n");
            sb.Append($"        qg = qh(vgs, vds{H}, qd, bq_a, bq_b, bq_c, bq_e);\n");
            sb.Append("        bq_i0 = 0.0;\n        bq_i1 = 0.0;\n        bq_i2 = 0.0;\n        bq_i3 = 0.0;\n");
            for (int k = 0; k < BranchLinearisation.Nodes; k++)
            {
                var (s, w) = BranchLinearisation.LineNode(k);
                sb.Append($"        bq_y = qh(VgsQ + {Operand(s)}*bq_d0, VdsQ + {Operand(s)}*bq_d1{H}, bq_x, bq_a, bq_b, bq_c, bq_e);\n");
                for (int b = 0; b < 4; b++)
                    sb.Append($"        bq_i{b} = bq_i{b} + {Operand(w)}*({BranchOf(b, "bq_a", "bq_b", "bq_c", "bq_e")});\n");
            }
            for (int b = 0; b < 4; b++)
            {
                string sw = ComposedSwitches.Names[2 + b];
                var ((ug, ud), (eg, ed)) = BranchLinearisation.Branch(b);
                sb.Append($"        if ({sw}) begin\n");
                sb.Append($"          bq_r = (bq_i{b} - ({BranchOf(b, "bq_c11", "bq_c12", "bq_c21", "bq_c22")}))*({Combine(ug, "bq_d0", ud, "bq_d1")});\n");
                if (eg != 0) sb.Append($"          qg = qg {Less(eg, "bq_r")};\n");
                if (ed != 0) sb.Append($"          qd = qd {Less(ed, "bq_r")};\n");
                sb.Append("        end\n");
            }
            sb.Append("      end\n");
            sb.Append("    end\n");
        }

        /// <summary><c>BranchLinearisation.Capacitance(b, c)</c> over four named entries <c>C11 C12 C21 C22</c>.</summary>
        private static string BranchOf(int b, string c11, string c12, string c21, string c22) => b switch
        {
            0 => $"{c11} + {c12}",
            1 => $"-{c12}",
            2 => $"{c22} + {c12}",
            _ => $"{c21} - {c12}",
        };

        private static string Combine(double a, string x, double b, string y)
        {
            var terms = new List<string>();
            if (a != 0) terms.Add(Scaled(a, x));
            if (b != 0) terms.Add(Scaled(b, y));
            return string.Join(" + ", terms).Replace("+ -", "- ");
        }

        /// <summary><c>− a·x</c> as an operator and operand: <c>- x</c>, <c>+ x</c> for a = −1.</summary>
        private static string Less(double a, string x) => a == 1 ? $"- {x}" : a == -1 ? $"+ {x}" : $"- {Operand(a)}*{x}";

        private static string Scaled(double a, string x) => a == 1 ? x : a == -1 ? $"-{x}" : $"{Operand(a)}*{x}";
    }
}

/// <summary>The instance thermal overrides' names, as the elaborator spells them.</summary>
file static class ElaborationNames
{
    public const string Rth = CircuitRF.Core.Elaboration.Elaborator.RthParam, Cth = CircuitRF.Core.Elaboration.Elaborator.CthParam;
}
