using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>What <see cref="SpiceDeckWriter.Write"/> writes, and from what.</summary>
public sealed record SpiceDeckRequest
{
    /// <summary>The elaborated bench.</summary>
    public required ElaboratedNetlist Netlist { get; init; }
    /// <summary>The transient analysis the deck runs.</summary>
    public required TransientAnalysis Analysis { get; init; }
    /// <summary>The bench's name, for the header.</summary>
    public string BenchName { get; init; } = "";
    /// <summary>circuitRF's version, for the header.</summary>
    public string Version { get; init; } = "";
    /// <summary>The design the netlist was elaborated from, to find placed SPICE files (<see cref="Cell.SpiceSource"/>);
    /// null writes every element from the netlist.</summary>
    public TestBench? Bench { get; init; }
    public Library? Library { get; init; }
    /// <summary>The transient engine's fits, for <c>SpiceFit=yes</c>.</summary>
    public ISpiceFitSource? Fits { get; init; }
    /// <summary>Compile a placed Verilog-A source to find its <c>.osdi</c>; false (a check) names the source and writes no
    /// path.</summary>
    public bool CompileVerilogA { get; init; } = true;
}

/// <summary>One element the translation refused, with the reason.</summary>
public sealed record SpiceRefusal(string Instance, string Type, string Reason)
{
    public string Message => Type.Length == 0 ? $"{Instance}: {Reason}" : $"{Instance} ({Type}): {Reason}";
    public override string ToString() => Message;
}

/// <summary>One line written as the dialect's lossy line under <c>SpiceLines=rlgc</c>: its fit and how far it drifts.</summary>
/// <param name="Element">TXL or CPL.</param>
/// <param name="Rlgc">TXL: R, L, G, C over the whole line. CPL: the self terms (11 = 22).</param>
/// <param name="Mutual">CPL only: the mutual terms (12), R and G zero.</param>
public sealed record SpiceLineReport(string Instance, string Element, double FitHz, double MaxDeltaS, double AtHz,
                                     double LoHz, double HiHz, SpiceRlgc Rlgc, SpiceRlgc? Mutual)
{
    public string Sentence => string.Format(CultureInfo.InvariantCulture,
        "ngspice ran {0} as a {1} fitted at {2}; largest |ΔS| {3:G3} at {4}", Instance, Element, SpiceDeckWriter.Hz(FitHz), MaxDeltaS,
        SpiceDeckWriter.Hz(AtHz));
}

/// <summary>A deck: its text (null when anything was refused), its names, and what it says about itself.</summary>
public sealed class SpiceDeck
{
    public string? Text { get; init; }
    public required SpiceNameMap Names { get; init; }
    public IReadOnlyList<SpiceRefusal> Refusals { get; init; } = [];
    /// <summary>Lines written as TXL or CPL (<c>SpiceLines=rlgc</c>), each with its largest |ΔS| — the run's notes.</summary>
    public IReadOnlyList<SpiceLineReport> Lines { get; init; } = [];
    /// <summary>Elements written as circuitRF's fit (<c>SpiceFit=yes</c>), each with its note — the run's notes.</summary>
    public IReadOnlyList<(string Instance, string Sentence)> Fits { get; init; } = [];
    /// <summary>Every vector the deck saves, as the dialect names it, and the circuitRF net or probe it is.</summary>
    public IReadOnlyList<(string Vector, string CircuitRf)> Saved { get; init; } = [];
    /// <summary>True when the deck was written.</summary>
    public bool Ok => Text is not null;
}

/// <summary>
/// Writes an elaborated bench with a transient analysis as one flat deck in the SPICE dialect (docs/design/transient.md §9.2,
/// brief TRN-6). <b>Exact or refused (D7):</b> every element is written with the same meaning or refused by name with its
/// reason; every refusal is collected before anything is written, and nothing is written when there is one. The approximate
/// translations — lossy lines as the dialect's TXL/CPL (<c>SpiceLines=rlgc</c>) and frequency-defined elements as circuitRF's
/// own fit (<c>SpiceFit=yes</c>) — are written only when asked for, and each is reported.
///
/// <para><b>The only writer of the dialect in circuitRF.</b> The CLI's <c>netlist -o x.cir</c>, MCP's <c>netlist</c> and
/// <c>check</c>'s <c>check.tran.untranslatable</c> all come here; a source scan holds that shut.</para>
/// </summary>
public static class SpiceDeckWriter
{
    /// <summary>The file extension a deck is written with.</summary>
    public const string Extension = ".cir";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string N(double v) => v.ToString("R", Inv);

    internal static string Hz(double hz) => hz switch
    {
        >= 1e9 => (hz / 1e9).ToString("0.###", Inv) + " GHz",
        >= 1e6 => (hz / 1e6).ToString("0.###", Inv) + " MHz",
        >= 1e3 => (hz / 1e3).ToString("0.###", Inv) + " kHz",
        _ => hz.ToString("0.###", Inv) + " Hz",
    };

    /// <summary>Writes the deck, or refuses every element that cannot be written.</summary>
    public static SpiceDeck Write(SpiceDeckRequest request) => new Writer(request).Run();

    private sealed class Writer(SpiceDeckRequest req)
    {
        private readonly ElaboratedNetlist _nl = req.Netlist;
        private readonly SpiceNameMap _names = new();
        private readonly StringBuilder _el = new();
        private readonly List<SpiceRefusal> _refusals = [];
        private readonly List<SpiceLineReport> _lines = [];
        private readonly List<(string, string)> _fits = [];
        private readonly List<string> _osdi = [];
        private readonly List<string> _includes = [];
        private readonly HashSet<string> _probePaths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _vsourceDeck = new(StringComparer.Ordinal);
        private readonly List<(string Prefix, string Line, string Path)> _placed = [];
        private Dictionary<string, Value> _globals = new(StringComparer.Ordinal);

        // the analysis, resolved
        private double _stop, _step, _start, _maxStep, _relTol, _vTol, _iTol, _fMax;
        private bool _uic, _gear, _rlgc, _fitOn;
        private double? _lineHz;
        private Dictionary<string, double> _ic = new(StringComparer.Ordinal);
        private SpiceFitSet? _fitSet;

        private void Refuse(string path, string type, string reason) => _refusals.Add(new SpiceRefusal(path, type, reason));
        private void Refuse(ElaboratedComponent ec, string reason) => Refuse(ec.InstancePath, ec.ComponentType, reason);

        private string Node(int index) => index <= 0 ? "0" : _names.Net(_nl.Nodes.NameOf(index));

        private void Line(string text, string path) => _el.Append(text).Append(" ; ").AppendLine(path);

        public SpiceDeck Run()
        {
            foreach (var kv in _nl.ResolvedGlobals) _globals[kv.Key] = kv.Value;
            ResolveAnalysis();

            var under = PlacedSpiceFiles();
            if (_fitOn && _refusals.Count == 0)
            {
                if (req.Fits is null) Refuse(req.Analysis.Name, "tran", "SpiceFit=yes writes circuitRF's own fits, and none were supplied.");
                else _fitSet = req.Fits.Realize(_nl, _fMax);
            }

            foreach (var ec in _nl.Components)
            {
                if (under.Any(p => ec.InstancePath.StartsWith(p + ".", StringComparison.Ordinal))) continue;
                try { One(ec); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Refuse(ec, $"it could not be written ({ex.Message.TrimEnd('.')}).");
                }
            }

            if (_refusals.Count > 0)
                return new SpiceDeck { Names = _names, Refusals = _refusals };

            return new SpiceDeck
            {
                Text = Assemble(out var saved), Names = _names, Lines = _lines, Fits = _fits, Saved = saved,
            };
        }

        // ── the analysis ─────────────────────────────────────────────────

        private double Eval(string key, string expr, double def)
        {
            string t = expr.Trim();
            if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase)) return def;
            if (double.TryParse(t, NumberStyles.Float, Inv, out double d)) return d;
            var ev = new Evaluator();
            var scope = new Scope("spice-deck");
            foreach (var (k, v) in _globals) { scope.Bind(k, v.ToString()!); ev.InjectResolved("spice-deck", k, v); }
            var val = ev.Eval(t, scope);
            return val.Kind == ValueKind.Real ? val.AsReal() : val.Kind == ValueKind.Complex ? val.AsComplex().Real : double.NaN;
        }

        private void ResolveAnalysis()
        {
            var a = req.Analysis;
            string who = a.Name;
            try
            {
                if (string.IsNullOrWhiteSpace(a.StopExpr)) { Refuse(who, "tran", "Stop is required."); return; }
                _stop = Eval("Stop", a.StopExpr, 0);
                _step = Eval("Step", a.StepExpr, _stop / 1000);
                _start = Eval("Start", a.StartExpr, 0);
                _relTol = Eval("RelTol", a.RelTolExpr, 1e-3);
                _vTol = Eval("VTol", a.VTolExpr, 1e-6);
                _iTol = Eval("ITol", a.ITolExpr, 1e-12);
                double fSrc = SourceFrequencies().DefaultIfEmpty(0).Max();
                double maxAuto = fSrc > 0 ? Math.Min(_step, 1 / (20 * fSrc)) : _step;
                _maxStep = Eval("MaxStep", a.MaxStepExpr, maxAuto);
                if (!(_maxStep > 0)) _maxStep = maxAuto;
                _fMax = 1 / (2 * _maxStep);
                foreach (var item in a.IcExpr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int eq = item.IndexOf('=');
                    if (eq <= 0) { Refuse(who, "tran", $"IC entries are net=value, not '{item}'."); continue; }
                    _ic[item[..eq].Trim()] = Eval("IC", item[(eq + 1)..], 0);
                }
                string lineFreq = a.SpiceLineFreq.Trim();
                _lineHz = lineFreq.Length == 0 || lineFreq.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? (fSrc > 0 ? fSrc : null)
                    : Eval("SpiceLineFreq", lineFreq, 0);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Refuse(who, "tran", ex.Message.TrimEnd('.') + ".");
                return;
            }
            foreach (var (key, v) in new[] { ("Stop", _stop), ("Step", _step), ("Start", _start), ("RelTol", _relTol), ("VTol", _vTol),
                                             ("ITol", _iTol), ("MaxStep", _maxStep) })
                if (!double.IsFinite(v)) { Refuse(who, "tran", $"{key} does not evaluate to a finite number."); return; }
            foreach (var (net, v) in _ic)
                if (!double.IsFinite(v)) { Refuse(who, "tran", $"IC on '{net}' does not evaluate to a finite number."); return; }
            if (_lineHz is double lf && !double.IsFinite(lf)) { Refuse(who, "tran", "SpiceLineFreq does not evaluate to a finite number."); return; }
            if (!(_stop > 0)) Refuse(who, "tran", "Stop must be a positive time.");
            if (!(_step > 0)) Refuse(who, "tran", "Step must be a positive time.");

            _gear = a.Method.Trim().ToLowerInvariant() switch
            {
                TransientAnalysis.MethodTrap or "" => false,
                TransientAnalysis.MethodGear2 => true,
                _ => Bad("Method", "trap or gear2", a.Method),
            };
            _uic = a.InitialState.Trim().ToLowerInvariant() switch
            {
                TransientAnalysis.InitialOp or "" => false,
                TransientAnalysis.InitialZero => true,
                _ => Bad("InitialState", "op or zero", a.InitialState),
            };
            _rlgc = a.SpiceLines.Trim().ToLowerInvariant() switch
            {
                "" or TransientAnalysis.SpiceLinesDefault => false,
                "rlgc" => true,
                _ => Bad("SpiceLines", "refuse or rlgc", a.SpiceLines),
            };
            string fit = a.SpiceFit.Trim();
            _fitOn = Analysis.ParseNdf(fit);
            if (!_fitOn && fit.Length > 0 && !fit.Equals("no", StringComparison.OrdinalIgnoreCase) && !fit.Equals("off", StringComparison.OrdinalIgnoreCase)
                && !fit.Equals("false", StringComparison.OrdinalIgnoreCase) && fit != "0")
                Bad("SpiceFit", "yes or no", a.SpiceFit);
            if (a.SpiceOptions.Contains('\n') || a.SpiceOptions.Contains('\r'))
                Refuse(who, "tran", "SpiceOptions is one .options line and holds no line break.");

            bool Bad(string key, string what, string value)
            {
                Refuse(who, "tran", $"{key} must be {what}, not '{value}'.");
                return false;
            }
        }

        /// <summary>Every positive frequency a source plays.</summary>
        private IEnumerable<double> SourceFrequencies()
        {
            foreach (var ec in _nl.Components)
            {
                IEnumerable<double> hz = ec.Model switch
                {
                    ToneSourceModelBase ts => ts.ToneFreqsHz,
                    P1ToneModel p1 => [p1.FreqHz],
                    PnToneModel pn => pn.ToneFreqsHz,
                    ITransientWaveform w => [w.HighestFrequencyHz],
                    _ => [],
                };
                foreach (double f in hz) if (f > 0 && double.IsFinite(f)) yield return f;
            }
        }

        // ── placed SPICE files (R-trn6-5) ────────────────────────────────

        /// <summary>The instance paths of every placed SPICE subcircuit; their lines are written here, and the elements the
        /// elaborator flattened from them are skipped.</summary>
        private List<string> PlacedSpiceFiles()
        {
            var paths = new List<string>();
            if (req.Bench is not { } tb || req.Library is not { } lib) return paths;
            void Walk(IEnumerable<Instance> instances, string prefix, Func<string, string> net, int depth)
            {
                if (depth > 64) return;
                foreach (var inst in instances)
                {
                    if (lib.Find(inst.Reference) is not { } cell) continue;
                    string path = prefix.Length == 0 ? inst.InstanceName : prefix + "." + inst.InstanceName;
                    var bound = inst.NetBindings.Select(net).ToList();
                    if (cell.SpiceSource is { } file)
                    {
                        paths.Add(path);
                        Placed(path, cell, file, bound, inst);
                        continue;
                    }
                    var ports = cell.Ports;
                    Walk(cell.Instances, path, n =>
                    {
                        int k = ports.IndexOf(n);
                        if (k >= 0 && k < bound.Count) return bound[k];
                        return n == "0" ? "0" : path + "." + n;
                    }, depth + 1);
                }
            }
            Walk(tb.Instances, "", n => n, 0);
            return paths;
        }

        private void Placed(string path, Cell cell, string file, List<string> nets, Instance inst)
        {
            if (!File.Exists(file)) { Refuse(path, "SpiceModel", $"its file '{file}' was not found."); return; }
            if (nets.Count != cell.Ports.Count)
            {
                Refuse(path, "SpiceModel", $"it binds {nets.Count} net(s) to '{cell.Name}', which has {cell.Ports.Count} port(s).");
                return;
            }
            var parms = new List<string>();
            foreach (var o in inst.Overrides)
            {
                try
                {
                    double v = Eval(o.Name, o.Expression, double.NaN);
                    if (o.Unit is { Length: > 0 } u && Units.Scale(u) is double s) v *= s;
                    if (!double.IsFinite(v))
                    {
                        Refuse(path, "SpiceModel", $"its parameter {o.Name}={o.Expression} does not resolve to a finite number.");
                        return;
                    }
                    parms.Add($"{o.Name}={N(v)}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Refuse(path, "SpiceModel", $"its parameter {o.Name}={o.Expression} does not resolve to a number ({ex.Message.TrimEnd('.')}).");
                    return;
                }
            }
            if (!_includes.Contains(file)) _includes.Add(file);
            string deck = _names.Instance(path, 'X');
            string nodes = string.Join(' ', nets.Select(n => n is "0" or "" ? "0" : _names.Net(n)));
            Line($"{deck} {nodes} {cell.Name}" + (parms.Count > 0 ? " params: " + string.Join(' ', parms) : ""), path);
        }

        // ── one element ──────────────────────────────────────────────────

        private static double Real(ElaboratedComponent ec, string key, double def)
        {
            if (!ec.Parameters.TryGetValue(key, out var v)) return def;
            return v.Kind switch
            {
                ValueKind.Real => v.AsReal(),
                ValueKind.Complex when v.AsComplex().Imaginary == 0 => v.AsComplex().Real,
                ValueKind.Bool => v.AsBool() ? 1 : 0,
                _ => v.AsReal(),   // a string or a complex value where a number belongs: Value's own refusal, caught as the element's
            };
        }

        private static bool Has(ElaboratedComponent ec, string key) => ec.Parameters.ContainsKey(key);

        private void One(ElaboratedComponent ec)
        {
            string t = ec.ComponentType;
            if (ec.Multiplicity != 1 && SpiceDeviceTable.For(t) is null && ec.Model is not ExternalDeviceModel)
            {
                Refuse(ec, $"its device multiplier m={N(ec.Multiplicity)} has no place on this element in the SPICE dialect.");
                return;
            }
            switch (ec.Model)
            {
                case ResistorModel r: Resistor(ec, r); return;
                case CapacitorModel: Capacitor(ec); return;
                case InductorModel: Inductor(ec); return;
                case MutualInductanceModel: Mutual(ec); return;
                case IdealTransformerModel: Transformer(ec); return;
                case VccsModel: Controlled(ec, 'G', "G", 0); return;
                case VcvsModel: Controlled(ec, 'E', "E", 1); return;
                case IProbeModel: Probe(ec); return;
                case PortModel or TermModel: Termination(ec); return;
                case VdcModel: DcSource(ec, current: false); return;
                case IdcModel: DcSource(ec, current: true); return;
                case WaveformSourceModel w: Waveform(ec, w); return;
                case ToneSourceModelBase ts: Tone(ec, ts); return;
                case P1ToneModel p1: P1Tone(ec, p1); return;
                case VSwitchModel sw: Switch(ec, sw); return;
                case SddModel: Sdd(ec); return;
                case ExternalDeviceModel ext: VerilogA(ec, ext); return;
                case IUniformLine line: UniformLine(ec, line); return;
                case ICoupledPair pair: CoupledPair(ec, pair); return;
                case ILumpedNetwork ln: Lumped(ec, ln); return;
            }
            if (SpiceDeviceTable.For(t) is { } device) { Device(ec, device); return; }
            if (_fitSet is not null && _fitSet.Blocks.TryGetValue(ec.InstancePath, out var block)) { FitBlock(ec, block); return; }
            Refuse(ec, WhyNot(ec));
        }

        private string WhyNot(ElaboratedComponent ec)
        {
            string t = ec.ComponentType;
            if (_fitSet is not null && _fitSet.Refusals.TryGetValue(ec.InstancePath, out var why))
                return $"SpiceFit=yes could not realize it: {why}";
            string? fitHint = t is "SnP" or "Filter" or "Duplexer" || t.StartsWith("EM", StringComparison.Ordinal)
                ? " SpiceFit=yes writes circuitRF's rational fit of it as a subcircuit." : null;
            if (t.StartsWith("FET_", StringComparison.OrdinalIgnoreCase) || t.StartsWith("PFET_", StringComparison.OrdinalIgnoreCase) ||
                t is "ComposedFET" or "ComposedCore" or "JFET_N" or "JFET_P" or "IGBT_N" or "IGBT_P" or "VDMOS_N" or "VDMOS_P"
                    or "NonlinearC" or "SemiC" or "Bead")
                return "a device law the SPICE dialect does not have with circuitRF's equations; refused in v1.";
            if (fitHint is not null) return "a frequency-defined element has no exact counterpart in the SPICE dialect." + fitHint;
            return "it has no counterpart in the SPICE dialect.";
        }

        // ── passives ─────────────────────────────────────────────────────

        private void Resistor(ElaboratedComponent ec, ResistorModel model)
        {
            double r = Real(ec, "R", double.NaN) * model.TemperatureFactor;
            if (!double.IsFinite(r)) { Refuse(ec, "it has no resistance."); return; }
            if (r == 0) { Refuse(ec, "R=0 is stamped as a 1e12 S conductance, which the SPICE dialect has no spelling for; state the resistance."); return; }
            Line($"{_names.Instance(ec.InstancePath, 'R')} {Node(ec.Nodes[0])} {Node(ec.Nodes[1])} {N(r)}", ec.InstancePath);
        }

        /// <summary>IC= on a capacitor or inductor: the device's IC under InitialState=zero (the dialect's uic); under op, a
        /// capacitor's to ground becomes an .ic node hold, which is the dialect's held-through-the-operating-point rule.</summary>
        private string? DeviceIc(ElaboratedComponent ec, bool capacitor)
        {
            if (!Has(ec, "IC")) return null;
            double ic = Real(ec, "IC", 0);
            if (_uic) return $" IC={N(ic)}";
            if (capacitor && (ec.Nodes[0] == 0 || ec.Nodes[1] == 0))
            {
                string net = _nl.Nodes.NameOf(ec.Nodes[0] == 0 ? ec.Nodes[1] : ec.Nodes[0]);
                double v = ec.Nodes[0] == 0 ? -ic : ic;
                if (_ic.TryGetValue(net, out double other) && other != v)
                {
                    Refuse(ec, $"its IC={N(ic)} disagrees with IC {net}={N(other)} on the analysis.");
                    return null;
                }
                _ic[net] = v;
                return "";
            }
            Refuse(ec, capacitor
                ? "IC= on a capacitor between two nets is held through the operating point with InitialState=op, which the SPICE "
                + "dialect can say only for a node against ground; use InitialState=zero or IC on the analysis."
                : "IC= on an inductor is held through the operating point with InitialState=op, which the SPICE dialect cannot "
                + "say; use InitialState=zero.");
            return null;
        }

        private void Capacitor(ElaboratedComponent ec)
        {
            double c = Real(ec, "C", double.NaN);
            if (!double.IsFinite(c)) { Refuse(ec, "it has no capacitance."); return; }
            string? ic = DeviceIc(ec, capacitor: true);
            if (ic is null && Has(ec, "IC")) return;
            Line($"{_names.Instance(ec.InstancePath, 'C')} {Node(ec.Nodes[0])} {Node(ec.Nodes[1])} {N(c)}{ic}", ec.InstancePath);
        }

        private void Inductor(ElaboratedComponent ec)
        {
            double l = Real(ec, "L", double.NaN);
            if (!double.IsFinite(l)) { Refuse(ec, "it has no inductance."); return; }
            string? ic = DeviceIc(ec, capacitor: false);
            if (ic is null && Has(ec, "IC")) return;
            double r = Real(ec, "R", 0);
            bool hasC = Has(ec, "C");
            string a = Node(ec.Nodes[0]), b = Node(ec.Nodes[1]);
            string end = r != 0 || hasC ? _names.Internal(ec.InstancePath + "_l") : b;
            Line($"{_names.Instance(ec.InstancePath, 'L')} {a} {end} {N(l)}{ic}", ec.InstancePath);
            if (r != 0 && hasC)
            {
                string mid = _names.Internal(ec.InstancePath + "_r");
                Line($"{_names.Instance(ec.InstancePath, 'R', "r")} {end} {mid} {N(r)}", ec.InstancePath + " (series R)");
                Line($"{_names.Instance(ec.InstancePath, 'C', "c")} {mid} {b} {N(Real(ec, "C", 0))}", ec.InstancePath + " (series C)");
            }
            else if (r != 0) Line($"{_names.Instance(ec.InstancePath, 'R', "r")} {end} {b} {N(r)}", ec.InstancePath + " (series R)");
            else if (hasC) Line($"{_names.Instance(ec.InstancePath, 'C', "c")} {end} {b} {N(Real(ec, "C", 0))}", ec.InstancePath + " (series C)");
        }

        /// <summary>A name an element refers to (a Mutual's inductor, an SDD's control current), as an instance path: the sibling
        /// of <paramref name="from"/> first, then the name as a path.</summary>
        private ElaboratedComponent? Sibling(string from, string name)
        {
            int dot = from.LastIndexOf('.');
            string sibling = dot < 0 ? name : from[..(dot + 1)] + name;
            return _nl.Components.FirstOrDefault(c => c.InstancePath == sibling)
                ?? _nl.Components.FirstOrDefault(c => c.InstancePath == name);
        }

        private void Mutual(ElaboratedComponent ec)
        {
            string Name(string key) => ec.Parameters.TryGetValue(key, out var v) && v.Kind == ValueKind.String ? v.AsString().Trim('"') : "";
            var l1 = Sibling(ec.InstancePath, Name("Inductor1"));
            var l2 = Sibling(ec.InstancePath, Name("Inductor2"));
            if (l1?.Model is not InductorModel || l2?.Model is not InductorModel)
            {
                Refuse(ec, $"its inductors '{Name("Inductor1")}' and '{Name("Inductor2")}' are not both inductors of this netlist.");
                return;
            }
            double m = Real(ec, "M", double.NaN), la = Real(l1, "L", double.NaN), lb = Real(l2, "L", double.NaN);
            if (!(la > 0) || !(lb > 0) || !double.IsFinite(m)) { Refuse(ec, "its coupling needs M and two positive inductances."); return; }
            double k = m / Math.Sqrt(la * lb);
            if (Math.Abs(k) > 1) { Refuse(ec, $"its coupling coefficient M/√(L1·L2) = {N(k)} exceeds 1, which the SPICE dialect refuses."); return; }
            Line($"{_names.Instance(ec.InstancePath, 'K')} {_names.Instance(l1.InstancePath, 'L')} {_names.Instance(l2.InstancePath, 'L')} {N(k)}",
                 ec.InstancePath);
        }

        private void Transformer(ElaboratedComponent ec)
        {
            double n = Real(ec, "N", 1);
            string pp = Node(ec.Nodes[0]), pm = Node(ec.Nodes[1]), sp = Node(ec.Nodes[2]), sm = Node(ec.Nodes[3]);
            string e = _names.Instance(ec.InstancePath, 'E');   // first, so the map names the element by it
            string mid = _names.Internal(ec.InstancePath + "_p");
            string sense = _names.Instance(ec.InstancePath, 'V', "ip");
            // V(p+) − V(p−) = N (V(s+) − V(s−)); the primary current i_p senses through a zero-volt V; i_s = −N i_p into s+.
            Line($"{e} {pp} {mid} {sp} {sm} {N(n)}", ec.InstancePath);
            Line($"{sense} {mid} {pm} DC 0", ec.InstancePath + " (primary current)");
            Line($"{_names.Instance(ec.InstancePath, 'F', "s")} {sm} {sp} {sense} {N(n)}", ec.InstancePath + " (secondary)");
        }

        private void Controlled(ElaboratedComponent ec, char letter, string key, double def)
        {
            if (Real(ec, "T", 0) != 0) { Refuse(ec, "its delay T has no counterpart on the SPICE dialect's controlled source."); return; }
            double g = letter == 'E' && !Has(ec, "E") && Has(ec, "Gain") ? Real(ec, "Gain", def) : Real(ec, key, def);
            Line($"{_names.Instance(ec.InstancePath, letter)} {Node(ec.Nodes[0])} {Node(ec.Nodes[1])} {Node(ec.Nodes[2])} {Node(ec.Nodes[3])} {N(g)}",
                 ec.InstancePath);
        }

        private void Probe(ElaboratedComponent ec)
        {
            string deck = _names.Instance(ec.InstancePath, 'V');
            _vsourceDeck[ec.InstancePath] = deck;
            _probePaths.Add(ec.InstancePath);
            Line($"{deck} {Node(ec.Nodes[0])} {Node(ec.Nodes[1])} DC 0", ec.InstancePath);
        }

        private void Termination(ElaboratedComponent ec)
        {
            Complex z = new(50, 0);
            if (ec.Parameters.TryGetValue("Z", out var v))
                z = v.Kind switch { ValueKind.Real => v.AsReal(), ValueKind.Complex => v.AsComplex(), _ => Complex.NaN };
            if (z.Imaginary != 0 || !(z.Real > 0))
            {
                Refuse(ec, $"its impedance Z={z.Real.ToString("G6", Inv)}{(z.Imaginary >= 0 ? "+" : "-")}j{Math.Abs(z.Imaginary).ToString("G6", Inv)} Ω "
                         + "is not a positive resistance, and a reactive termination is a frequency-defined element.");
                return;
            }
            Line($"{_names.Instance(ec.InstancePath, 'R')} {Node(ec.Nodes[0])} {Node(ec.Nodes.Length > 1 ? ec.Nodes[1] : 0)} {N(z.Real)}",
                 ec.InstancePath);
        }

        // ── sources ──────────────────────────────────────────────────────

        /// <summary>A current source's nets, written swapped: the dialect's I sinks from its first node, circuitRF's delivers
        /// into it.</summary>
        private (string A, string B) Nets(ElaboratedComponent ec, bool current) => current
            ? (Node(ec.Nodes[1]), Node(ec.Nodes[0]))
            : (Node(ec.Nodes[0]), Node(ec.Nodes[1]));

        private void DcSource(ElaboratedComponent ec, bool current)
        {
            double v = current ? (Has(ec, "Idc") ? Real(ec, "Idc", 0) : Real(ec, "I", 0)) : Real(ec, "Vdc", 0);
            var (a, b) = Nets(ec, current);
            string deck = _names.Instance(ec.InstancePath, current ? 'I' : 'V');
            if (!current) _vsourceDeck[ec.InstancePath] = deck;
            Line($"{deck} {a} {b} DC {N(v)}", ec.InstancePath);
        }

        private void Waveform(ElaboratedComponent ec, WaveformSourceModel w)
        {
            bool current = w.IsCurrent;
            var (a, b) = Nets(ec, current);
            string deck = _names.Instance(ec.InstancePath, w.Waveform == WaveformKind.Expr ? 'B' : current ? 'I' : 'V');
            double at0 = w.ValueAt(0);
            if (w.SteadyValue != at0 && !_uic)
            {
                Refuse(ec, $"its Dc ({N(w.SteadyValue)}) differs from its value at t = 0 ({N(at0)}): the SPICE dialect's operating point "
                         + "takes a waveform source at t = 0, so the operating point would not be circuitRF's. Use InitialState=zero, "
                         + "or remove Dc.");
                return;
            }
            string body;
            switch (w.Waveform)
            {
                case WaveformKind.Pulse:
                {
                    var p = w.PulseShape;
                    foreach (var (key, value) in new[] { ("Rise", p.Rise), ("Fall", p.Fall), ("Width", p.Width) })
                        if (value == 0)
                        {
                            Refuse(ec, $"{key}=0: the SPICE dialect reads a zero {key.ToLowerInvariant()} as its default (the output step "
                                     + "or the stop time), so the pulse would not be this one. State a nonzero time.");
                            return;
                        }
                    double width = double.IsPositiveInfinity(p.Width) ? Math.Max(_stop, p.Delay + p.Rise) * 2 : p.Width;
                    double period = p.Period > 0 ? p.Period : Math.Max(2 * _stop, p.Delay + p.Rise + width + p.Fall) * 2;
                    body = $"PULSE({N(p.V1)} {N(p.V2)} {N(p.Delay)} {N(p.Rise)} {N(p.Fall)} {N(width)} {N(period)})";
                    break;
                }
                case WaveformKind.Exp:
                {
                    var e = w.ExpShape;
                    double d2 = double.IsPositiveInfinity(e.Delay2) ? 2 * Math.Max(_stop, e.Delay1) + 1 : e.Delay2;
                    if (d2 == 0) { Refuse(ec, "Delay2=0: the SPICE dialect reads it as Delay1 plus its output step."); return; }
                    body = $"EXP({N(e.V1)} {N(e.V2)} {N(e.Delay1)} {N(e.Tau1)} {N(d2)} {N(e.Tau2)})";
                    break;
                }
                case WaveformKind.Sffm:
                {
                    var s = w.SffmShape;
                    if (s.Carrier == 0) { Refuse(ec, "Carrier=0: the SPICE dialect reads it as 1/Stop."); return; }
                    if (s.Signal == 0 && s.ModIndex != 0) { Refuse(ec, "Signal=0 with a modulation index: the SPICE dialect reads it as 1/Stop."); return; }
                    body = $"SFFM({N(s.Offset)} {N(s.Amplitude)} {N(s.Carrier)} {N(s.ModIndex)} {N(s.Signal == 0 ? 1 / _stop : s.Signal)})";
                    break;
                }
                case WaveformKind.Pwl:
                {
                    var p = w.PwlShape;
                    var pts = new StringBuilder();
                    for (int i = 0; i < p.Times.Count; i++) pts.Append(i == 0 ? "" : " ").Append(N(p.Times[i])).Append(' ').Append(N(p.Values[i]));
                    body = $"PWL({pts})" + (p.Repeat ? $" r={N(p.Times[0])}" : "");
                    break;
                }
                default:
                {
                    var (expr, scope) = w.ExprShape;
                    if (expr is null) { Refuse(ec, "it has no expression."); return; }
                    string? text = SpiceExpressionWriter.Write(expr, name =>
                        name == TimeVariable.Name ? "time"
                        : scope.TryGetValue(name, out var v) && v.Kind == ValueKind.Real ? $"({N(v.AsReal())})"
                        : _globals.TryGetValue(name, out var g) && g.Kind == ValueKind.Real ? $"({N(g.AsReal())})"
                        : null, out string? why);
                    if (text is null) { Refuse(ec, why!); return; }
                    Line($"{deck} {a} {b} {(current ? "I" : "V")}={text}", ec.InstancePath);
                    return;
                }
            }
            if (!current) _vsourceDeck[ec.InstancePath] = deck;
            Line($"{deck} {a} {b} DC {N(at0)} {body}", ec.InstancePath);
        }

        /// <summary>The shaping keys of a tone source (note §6.2): Delay, Ramp, Damping.</summary>
        private static (double Delay, double Ramp, double Damping) Shape(ElaboratedComponent ec) =>
            (Real(ec, "Delay", 0), Real(ec, "Ramp", 0), Real(ec, "Damping", 0));

        /// <summary>One tone as the dialect's SIN: offset, amplitude, frequency, delay, damping and the SINE's phase in degrees
        /// (a tone's phasor is a cosine's: cos(x + φ) = sin(x + φ + 90°)).</summary>
        private static string Sin(double dc, double hz, Complex phasor, double delay, double damping) =>
            $"SIN({N(dc)} {N(phasor.Magnitude)} {N(hz)} {N(delay)} {N(damping)} {N(phasor.Phase * 180 / Math.PI + 90)})";

        /// <summary>A ramped tone as a behavioural source in time: dc + env(t)·|p|·cos(2πf(t − Delay) + arg p), env the raised
        /// cosine over Ramp times e^(−Damping (t − Delay)), zero before Delay — the transient engine's own envelope.</summary>
        private static string Ramped(double dc, IEnumerable<(double Hz, Complex P)> tones, double delay, double ramp, double damping)
        {
            string tau = $"(time-({N(delay)}))";
            string env = $"((time<({N(delay)})) ? 0 : ((({tau})<({N(ramp)})) ? (0.5*(1-cos({N(Math.PI / ramp)}*{tau}))) : 1))";
            if (damping != 0) env = $"({env}*pow({SpiceExpressionWriter.E},-({N(damping)})*{tau}))";
            var sum = string.Join("+", tones.Select(t => $"({N(t.P.Magnitude)})*cos({N(2 * Math.PI * t.Hz)}*{tau}+({N(t.P.Phase)}))"));
            return $"({N(dc)})+{env}*({sum})";
        }

        private void Tone(ElaboratedComponent ec, ToneSourceModelBase ts)
        {
            bool current = ts is CurrentToneSourceModel;
            var (a, b) = Nets(ec, current);
            var (delay, ramp, damping) = Shape(ec);
            double dc = ts.DcValue;
            var tones = new List<(double Hz, Complex P)>();
            foreach (var (hz, p) in ts.Tones)
            {
                if (Math.Abs(hz) < 1e-9) dc += p.Real;   // a tone at 0 Hz is part of the DC level, its real part
                else tones.Add((hz, p));
            }
            if (ramp > 0)
            {
                Line($"{_names.Instance(ec.InstancePath, 'B')} {a} {b} {(current ? "I" : "V")}={Ramped(dc, tones, delay, ramp, damping)}",
                     ec.InstancePath);
                return;
            }
            if (tones.Count == 0)
            {
                string d = _names.Instance(ec.InstancePath, current ? 'I' : 'V');
                if (!current) _vsourceDeck[ec.InstancePath] = d;
                Line($"{d} {a} {b} DC {N(dc)}", ec.InstancePath);
                return;
            }
            if (!current)
            {
                // one SIN per tone in series, the first carrying the offset; the branch current every one carries is the source's
                string from = a;
                for (int k = 0; k < tones.Count; k++)
                {
                    string to = k == tones.Count - 1 ? b : _names.Internal($"{ec.InstancePath}_t{k + 1}");
                    string deck = k == 0 ? _names.Instance(ec.InstancePath, 'V') : _names.Instance(ec.InstancePath, 'V', $"t{k + 1}");
                    if (k == 0) _vsourceDeck[ec.InstancePath] = deck;
                    Line($"{deck} {from} {to} {Sin(k == 0 ? dc : 0, tones[k].Hz, tones[k].P, delay, damping)}",
                         k == 0 ? ec.InstancePath : $"{ec.InstancePath} (tone {k + 1})");
                    from = to;
                }
                return;
            }
            for (int k = 0; k < tones.Count; k++)
            {
                string deck = k == 0 ? _names.Instance(ec.InstancePath, 'I') : _names.Instance(ec.InstancePath, 'I', $"t{k + 1}");
                Line($"{deck} {a} {b} {Sin(k == 0 ? dc : 0, tones[k].Hz, tones[k].P, delay, damping)}",
                     k == 0 ? ec.InstancePath : $"{ec.InstancePath} (tone {k + 1})");
            }
        }

        private void P1Tone(ElaboratedComponent ec, P1ToneModel p1)
        {
            if (p1.HasHarmonicTerminations)
            {
                Refuse(ec, "its harmonic terminations Z[k] other than Z[1] are a frequency-defined impedance.");
                return;
            }
            Complex z = p1.GetDeclaredZ(1);
            if (z.Imaginary != 0 || !(z.Real > 0))
            {
                Refuse(ec, "its impedance Z is not a positive resistance, and a reactive source impedance is a frequency-defined element.");
                return;
            }
            double pavl = Math.Pow(10.0, (p1.PavlDbm - 30.0) / 10.0);
            double vs = Math.Sqrt(8.0 * pavl * z.Real);
            var phasor = Complex.FromPolarCoordinates(vs, p1.PhaseRad);
            var (delay, ramp, damping) = Shape(ec);
            string ext = Node(ec.Nodes[0]), rf = Node(ec.Nodes.Length > 1 ? ec.Nodes[1] : 0);
            string drv = _names.Internal(ec.InstancePath + "_drv");
            string src = ramp > 0
                ? $"{_names.Instance(ec.InstancePath, 'B')} {drv} {rf} V={Ramped(0, [(p1.FreqHz, phasor)], delay, ramp, damping)}"
                : $"{_names.Instance(ec.InstancePath, 'V')} {drv} {rf} {Sin(0, p1.FreqHz, phasor, delay, damping)}";
            Line(src, ec.InstancePath);
            Line($"{_names.Instance(ec.InstancePath, 'R', "z")} {ext} {drv} {N(z.Real)}", ec.InstancePath + " (Z)");
        }

        // ── the switch, the SDD ──────────────────────────────────────────

        private void Switch(ElaboratedComponent ec, VSwitchModel sw)
        {
            if (sw.Hyst != 0) { Refuse(ec, "its hysteresis is a state the SPICE dialect's behavioural source cannot hold."); return; }
            string p = Node(ec.Nodes[0]), n = Node(ec.Nodes[1]), cp = Node(ec.Nodes[2]), cn = Node(ec.Nodes[3]);
            double lnOff = -Math.Log(sw.Roff), lnOn = -Math.Log(sw.Ron);
            // ln g = ln gOff + (ln gOn − ln gOff)(3u² − 2u³), u = (vc − Voff)/(Von − Voff) clamped to [0, 1] (VSwitchModel)
            string u = $"min(max((V({cp},{cn})-({N(sw.Voff)}))/({N(sw.Von - sw.Voff)}),0),1)";
            string g = $"pow({SpiceExpressionWriter.E},({N(lnOff)})+({N(lnOn - lnOff)})*(3*{u}*{u}-2*{u}*{u}*{u}))";
            Line($"{_names.Instance(ec.InstancePath, 'B')} {p} {n} I=V({p},{n})*{g}", ec.InstancePath);
        }

        private static readonly System.Text.RegularExpressions.Regex RxEq =
            new(@"^(I)\[(\d+),(\d+)\]$|^(V)\[(\d+)\]$|^(C)\[(\d+)\]$|^(Cport)\[(\d+)\]$|^(H)\[(\d+)\]$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private void Sdd(ElaboratedComponent ec)
        {
            int ports = (int)Real(ec, "SddPortCount", 0);
            if (ports <= 0 || ec.Nodes.Length < 2 * ports) { Refuse(ec, "its port count does not match its nets."); return; }
            var current = new Dictionary<int, string>();
            var charge = new Dictionary<int, string>();
            var voltage = new Dictionary<int, string>();
            var controls = new Dictionary<int, string>();
            var numeric = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, v) in ec.Parameters)
            {
                var m = RxEq.Match(key);
                string Text() => v.Kind == ValueKind.String ? v.AsString().Trim().Trim('"') : v.ToString()!;
                if (!m.Success)
                {
                    if (v.Kind == ValueKind.Real && !key.StartsWith("__", StringComparison.Ordinal)) numeric[key] = v.AsReal();
                    continue;
                }
                if (m.Groups[1].Success)
                {
                    int p = int.Parse(m.Groups[2].Value, Inv), w = int.Parse(m.Groups[3].Value, Inv);
                    if (w == 0) current[p] = Text();
                    else if (w == 1) charge[p] = Text();
                    else { Refuse(ec, $"its {key} is a frequency-weighted term (H[{w}]), which has no time-domain spelling."); return; }
                }
                else if (m.Groups[4].Success) voltage[int.Parse(m.Groups[5].Value, Inv)] = Text();
                else if (m.Groups[6].Success) controls[int.Parse(m.Groups[7].Value, Inv)] = Text();
                else if (m.Groups[8].Success)
                {
                    if (Text() != "1") { Refuse(ec, $"its {key} names a port of a multi-port control reference, which the SPICE dialect's i() cannot read."); return; }
                }
            }
            string Pin(int p, int k) => Node(ec.Nodes[2 * (p - 1) + k]);
            string? Ref(string name)
            {
                if (name == TimeVariable.Name) return "time";
                if (name.Length > 2 && name[0] == '_' && name[1] == 'v' && int.TryParse(name[2..], NumberStyles.None, Inv, out int p) && p >= 1 && p <= ports)
                    return $"V({Pin(p, 0)},{Pin(p, 1)})";
                if (name.Length > 2 && name[0] == '_' && name[1] == 'c' && int.TryParse(name[2..], NumberStyles.None, Inv, out int c) && controls.TryGetValue(c, out var target))
                {
                    var src = Sibling(ec.InstancePath, target);
                    if (src is null || !WrittenAsV(src)) return null;
                    return $"i({_names.Instance(src.InstancePath, 'V')})";
                }
                if (numeric.TryGetValue(name, out double d)) return $"({N(d)})";
                if (_globals.TryGetValue(name, out var g) && g.Kind == ValueKind.Real) return $"({N(g.AsReal())})";
                return null;
            }
            string? Expr(string text, string what)
            {
                Expressions.Expr ast;
                try { ast = Parser.Parse(text); }
                catch (Exception ex) { Refuse(ec, $"its {what} does not parse ({ex.Message.TrimEnd('.')})."); return null; }
                string? s = SpiceExpressionWriter.Write(ast, Ref, out string? why);
                if (s is null) Refuse(ec, why! + (why!.Contains("i()") ? "" : $" (in {what})") + ".");
                return s;
            }
            for (int p = 1; p <= ports; p++)
            {
                if (voltage.ContainsKey(p) && (current.ContainsKey(p) || charge.ContainsKey(p)))
                {
                    Refuse(ec, $"its port {p} holds both a voltage and a current or charge equation.");
                    return;
                }
                string a = Pin(p, 0), b = Pin(p, 1);
                if (current.TryGetValue(p, out var ie) && Expr(ie, $"I[{p},0]") is { } i)
                    Line($"{_names.Instance(ec.InstancePath, 'B', $"i{p}")} {a} {b} I={i}", $"{ec.InstancePath} I[{p},0]");
                if (voltage.TryGetValue(p, out var ve) && Expr(ve, $"V[{p}]") is { } v)
                    Line($"{_names.Instance(ec.InstancePath, 'B', $"v{p}")} {a} {b} V={v}", $"{ec.InstancePath} V[{p}]");
                if (charge.TryGetValue(p, out var qe) && Expr(qe, $"I[{p},1]") is { } q)
                {
                    // the charge idiom inverted (spice-models.md §8.8): V(a) − V(mid) = V(a,b) − q holds V(mid,b) = q on a 1 F
                    // capacitor, whose current a → mid → b is dq/dt.
                    string mid = _names.Internal($"{ec.InstancePath}_q{p}");
                    Line($"{_names.Instance(ec.InstancePath, 'B', $"q{p}")} {a} {mid} V=V({a},{b})-({q})", $"{ec.InstancePath} I[{p},1]");
                    Line($"{_names.Instance(ec.InstancePath, 'C', $"q{p}")} {mid} {b} 1", $"{ec.InstancePath} I[{p},1] (1 F)");
                }
            }
        }

        /// <summary>Whether an element is written as a V line of its own name, whose current the dialect's i() reads.</summary>
        private static bool WrittenAsV(ElaboratedComponent src) => src.Model switch
        {
            IProbeModel or VdcModel => true,
            ToneSourceModel => Real(src, "Ramp", 0) == 0,
            WaveformSourceModel w => !w.IsCurrent && w.Waveform != WaveformKind.Expr,
            _ => false,
        };

        // ── Verilog-A (R-trn6-5) ─────────────────────────────────────────

        private void VerilogA(ElaboratedComponent ec, ExternalDeviceModel ext)
        {
            if (VerilogAFileResolver.ModelFileIn(ext.ProviderName) is not { Length: > 0 } file)
            {
                Refuse(ec, "a device a kit's worker evaluates has no compiled model the SPICE dialect can load.");
                return;
            }
            string osdi = file;
            if (!OsdiBuilds.IsCompiled(file))
            {
                if (!req.CompileVerilogA) osdi = file + " (compiled when the deck is written)";
                else
                {
                    try { osdi = VerilogASourceCompiler.Compile(file); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Refuse(ec, $"its Verilog-A source could not be compiled to the .osdi the SPICE dialect loads ({ex.Message.TrimEnd('.')}).");
                        return;
                    }
                }
            }
            if (!_osdi.Contains(osdi)) _osdi.Add(osdi);
            int pins = ext.ConnectedPinCount ?? ext.ExternalPinCount;
            string model = "osdi_" + SpiceNameMap.Sanitize(ec.InstancePath, null);
            var parms = new List<string>();
            foreach (var (k, v) in ec.Parameters)
            {
                if (k.StartsWith("__", StringComparison.Ordinal) || k is "File" or "Model" or "Pins" or "m" || k == Temperature.AbsoluteParamName
                    || k == Temperature.DeltaParamName) continue;
                if (v.Kind == ValueKind.Real) parms.Add($"{k}={N(v.AsReal())}");
                else { Refuse(ec, $"its parameter {k} is {v.Kind}, which the dialect's instance line cannot carry."); return; }
            }
            double tC = Temperature.ResolveDeviceC(ec.Parameters, _nl.AmbientC);
            string nodes = string.Join(' ', ec.Nodes.Take(pins).Select(Node));
            Line($".model {model} {ext.Descriptor.TypeId}", ec.InstancePath + " (model)");
            Line($"{_names.Instance(ec.InstancePath, 'N')} {nodes} {model}" + (parms.Count > 0 ? " " + string.Join(' ', parms) : "")
                 + (ec.Multiplicity != 1 ? $" m={N(ec.Multiplicity)}" : "") + $" temp={N(tC)}", ec.InstancePath);
        }

        // ── the four device models (R-trn6-3) ────────────────────────────

        private void Device(ElaboratedComponent ec, SpiceDeviceModel m)
        {
            double tDev = Temperature.ResolveDeviceC(ec.Parameters, _nl.AmbientC);
            double tnom = Real(ec, "Tnom", Temperature.NominalC);
            if (Math.Abs(tDev - tnom) > 1e-9)
            {
                Refuse(ec, $"it runs at {N(tDev)} °C, not its Tnom of {N(tnom)} °C, and circuitRF's junction temperature relations "
                         + "(a temperature-dependent bandgap) are not the SPICE dialect's; away from Tnom the two models are different equations.");
                return;
            }
            var card = new List<string>();
            var inst = new List<string>();
            bool refused = false;
            foreach (var row in m.Parameters)
            {
                bool stated = Has(ec, row.CircuitRf);
                double value = stated ? Real(ec, row.CircuitRf, row.CircuitRfDefault) : row.CircuitRfDefault;
                if (double.IsNaN(value) && !stated && row.CircuitRf == "Vto")
                    value = ec.ComponentType.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;
                switch (row.Status)
                {
                    case SpiceParameterStatus.NoEffect: continue;
                    case SpiceParameterStatus.NoCounterpart:
                        if (value != row.CircuitRfDefault)
                        {
                            Refuse(ec.InstancePath, "", SpiceDeviceTable.NoCounterpart(ec.InstancePath, row, m)
                                [(ec.InstancePath.Length + 2)..] + ".");
                            refused = true;
                        }
                        continue;
                }
                if (row.CircuitRf == "Nbv")
                {
                    // the dialect's NBV defaults to N; written unless it already equals N
                    double nEm = Has(ec, "N") ? Real(ec, "N", 1) : 1;
                    if (value != nEm) card.Add($"NBV={N(value)}");
                    continue;
                }
                bool sameDefault = row.DialectDefault is double dd && dd == row.CircuitRfDefault && !double.IsNaN(row.CircuitRfDefault);
                if (sameDefault && value == row.CircuitRfDefault) continue;
                (row.OnInstance ? inst : card).Add($"{row.Dialect}={N(value)}");
            }
            if (refused) return;
            string type = m.CardType(ec.ComponentType);
            string cardName = "m_" + SpiceNameMap.Sanitize(ec.InstancePath, null);
            string level = m.Level is int lv ? $" LEVEL={lv}" : "";
            Line($".model {cardName} {type}({level.TrimStart()}{(level.Length > 0 && card.Count > 0 ? " " : "")}{string.Join(' ', card)})",
                 ec.InstancePath + " (model)");
            string nodes = string.Join(' ', DrawnTerminals(ec).Take(m.Terminals).Select(Node));
            if (ec.Multiplicity != 1) inst.Add($"m={N(ec.Multiplicity)}");
            inst.Add($"temp={N(tDev)}");
            Line($"{_names.Instance(ec.InstancePath, m.Letter)} {nodes} {cardName} {string.Join(' ', inst)}", ec.InstancePath);
        }

        /// <summary>
        /// brief TRN-7 — a device's DRAWN terminals in the dialect's order (diode a k; BJT c b e; MOS d g s b). The elaborator
        /// expands them into the model's ports (Elaborator: a diode with Rs is (a, int), (int, k); a BJT's four junction ports and a
        /// MOS's six branches come first, its ohmic resistors' pairs after), so the first nets of <see cref="ElaboratedComponent.Nodes"/>
        /// are internal pins, not terminals. The dialect's card carries RS/RB/RE/RC/RD itself and makes its own internal nodes.
        /// </summary>
        private static int[] DrawnTerminals(ElaboratedComponent ec)
        {
            var n = ec.Nodes;
            switch (ec.Model)
            {
                case DiodeModel { HasSeriesResistance: true } when n.Length == 4:
                    return [n[0], n[3]];
                case BjtModel bjt when n.Length >= 8:
                {
                    int k = 8;
                    int c = bjt.HasCollectorResistance ? n[(k += 2) - 2] : n[4];
                    int b = n[6];
                    if (bjt.HasBaseResistance) k += 2;
                    int e = bjt.HasEmitterResistance ? n[k] : n[1];
                    return [c, b, e];
                }
                case Devices.Mos.MosfetModelBase mos when n.Length >= 12:
                {
                    int d = mos.HasDrainResistance ? n[12] : n[0];
                    int src = mos.HasSourceResistance ? n[mos.HasDrainResistance ? 14 : 12] : n[1];
                    return [d, n[6], src, n[2]];
                }
                default:
                    return n;
            }
        }

        // ── lines (R-trn6-9) and fits (R-trn6-8) ─────────────────────────

        private static bool Lossless(IUniformLine line, double f1, double f2, out double z0, out double tau)
        {
            var (za, ga) = line.LineAt(f1);
            var (zb, gb) = line.LineAt(f2);
            z0 = za;
            tau = ga.Imaginary / (2 * Math.PI * f1);
            double tb = gb.Imaginary / (2 * Math.PI * f2);
            return ga.Real == 0 && gb.Real == 0 && za == zb && Math.Abs(tau - tb) <= 1e-12 * Math.Abs(tau) && tau >= 0;
        }

        private void UniformLine(ElaboratedComponent ec, IUniformLine line)
        {
            if (line.Ends(ec.Nodes) is not { } ends) { Refuse(ec, "it is not a ground-referenced two-conductor line here."); return; }
            string a = Node(ends.A), b = Node(ends.B), rf = Node(ec.ReferenceNode);
            double fHi = Math.Max(_fMax, 1), fLo = fHi / 3;
            if (Lossless(line, fHi, fLo, out double z0, out double tau))
            {
                if (tau == 0) { Refuse(ec, "it has no length."); return; }
                Line($"{_names.Instance(ec.InstancePath, 'T')} {a} {rf} {b} {rf} Z0={N(z0)} TD={N(tau)}", ec.InstancePath);
                return;
            }
            if (_rlgc)
            {
                if (ec.ReferenceNode != 0) { Refuse(ec, "the SPICE dialect's TXL is referred to ground, and this line has a reference net."); return; }
                if (_lineHz is not double f || !(f > 0))
                {
                    Refuse(ec, "SpiceLineFreq=auto takes the highest frequency a source plays, and no source plays one; state SpiceLineFreq.");
                    return;
                }
                var (zc, gl) = line.LineAt(f);
                var rlgc = SpiceLineFit.FromLine(zc, gl, f);
                if (!(rlgc.L > 0) || !(rlgc.C > 0)) { Refuse(ec, $"its fit at {Hz(f)} has a non-positive L or C."); return; }
                double lo = 1 / _stop, hi = Math.Max(_fMax, lo * 1.0001);
                var (worst, at) = SpiceLineFit.LargestDeltaS(x => line.LineAt(x), rlgc, SpiceLineFit.Grid(lo, hi));
                var rep = new SpiceLineReport(ec.InstancePath, "TXL", f, worst, at, lo, hi, rlgc, null);
                _lines.Add(rep);
                string model = "txl_" + SpiceNameMap.Sanitize(ec.InstancePath, null);
                _el.Append("* ").AppendLine(rep.Sentence);
                Line($".model {model} txl R={N(rlgc.R)} L={N(rlgc.L)} G={N(rlgc.G)} C={N(rlgc.C)} length=1", ec.InstancePath + " (model)");
                Line($"{_names.Instance(ec.InstancePath, 'Y')} {a} 0 {b} 0 {model} LEN=1", ec.InstancePath);
                return;
            }
            if (_fitSet is not null && _fitSet.Lines.TryGetValue(ec.InstancePath, out var res)) { FitLine(ec, res); return; }
            if (_fitSet is not null && _fitSet.Blocks.TryGetValue(ec.InstancePath, out var blk)) { FitBlock(ec, blk); return; }
            Refuse(ec, _fitSet is not null && _fitSet.Refusals.TryGetValue(ec.InstancePath, out var why)
                ? $"SpiceFit=yes could not realize it: {why}"
                : "a lossy or dispersive line has no exact counterpart in the SPICE dialect. SpiceLines=rlgc writes it as the dialect's "
                + "TXL fitted at SpiceLineFreq; SpiceFit=yes as circuitRF's delay and causal residual.");
        }

        private void CoupledPair(ElaboratedComponent ec, ICoupledPair pair)
        {
            if (!_rlgc)
            {
                Refuse(ec, "a coupled pair has no exact counterpart in the SPICE dialect. SpiceLines=rlgc writes it as the dialect's CPL fitted at SpiceLineFreq.");
                return;
            }
            if (ec.Nodes.Length != 4 || ec.ReferenceNode != 0) { Refuse(ec, "the SPICE dialect's CPL is referred to ground, four nets."); return; }
            if (_lineHz is not double f || !(f > 0))
            {
                Refuse(ec, "SpiceLineFreq=auto takes the highest frequency a source plays, and no source plays one; state SpiceLineFreq.");
                return;
            }
            var m = pair.PairAt(f);
            var (p, _, _) = SpiceLineFit.FromPair(m.Ze, m.GammaLengthEven, m.Zo, m.GammaLengthOdd, f);
            double scaleR = Math.Max(Math.Abs(p.Self.R), 1e-300), scaleG = Math.Max(Math.Abs(p.Self.G), 1e-300);
            if (Math.Abs(p.Mutual.R) > 1e-9 * scaleR && Math.Abs(p.Mutual.R) > 0 || Math.Abs(p.Mutual.G) > 1e-9 * scaleG && Math.Abs(p.Mutual.G) > 0)
            {
                Refuse(ec, $"its two modes lose differently at {Hz(f)}, so its resistance or conductance matrix has an off-diagonal term, and the "
                         + "SPICE dialect's CPL takes R and G as diagonals only.");
                return;
            }
            var written = new SpiceRlgcPair(p.Self, p.Mutual with { R = 0, G = 0 });
            double lo = 1 / _stop, hi = Math.Max(_fMax, lo * 1.0001);
            var (worst, at) = SpiceLineFit.LargestDeltaS(x => pair.PairAt(x), written, SpiceLineFit.Grid(lo, hi));
            var rep = new SpiceLineReport(ec.InstancePath, "CPL", f, worst, at, lo, hi, written.Self, written.Mutual);
            _lines.Add(rep);
            string model = "cpl_" + SpiceNameMap.Sanitize(ec.InstancePath, null);
            var s = written.Self; var mu = written.Mutual;
            _el.Append("* ").AppendLine(rep.Sentence);
            Line($".model {model} CPL length=1 R={N(s.R)} 0 {N(s.R)} L={N(s.L)} {N(mu.L)} {N(s.L)} G={N(s.G)} 0 {N(s.G)} C={N(s.C)} {N(mu.C)} {N(s.C)}",
                 ec.InstancePath + " (model)");
            Line($"{_names.Instance(ec.InstancePath, 'P')} {Node(ec.Nodes[0])} {Node(ec.Nodes[2])} 0 {Node(ec.Nodes[1])} {Node(ec.Nodes[3])} 0 {model} LEN=1",
                 ec.InstancePath);
        }

        private void Lumped(ElaboratedComponent ec, ILumpedNetwork ln)
        {
            if (ln.Lumped is not { } net)
            {
                if (_fitSet is not null && _fitSet.Blocks.TryGetValue(ec.InstancePath, out var blk)) { FitBlock(ec, blk); return; }
                Refuse(ec, _fitSet is not null && _fitSet.Refusals.TryGetValue(ec.InstancePath, out var why)
                    ? $"SpiceFit=yes could not realize it: {why}"
                    : "its model varies with frequency (its arms are lines), so it is not a fixed lumped network; SpiceFit=yes writes "
                    + "circuitRF's rational fit of it.");
                return;
            }
            var internals = Enumerable.Range(0, net.Internal).Select(k => _names.Internal($"{ec.InstancePath}_n{k + 1}")).ToArray();
            string At(int k) => k == LumpedNetwork.Ground ? Node(ec.ReferenceNode) : k < net.Ports ? Node(ec.Nodes[k]) : internals[k - net.Ports];
            int i = 0;
            foreach (var e in net.Elements)
            {
                i++;
                Line($"{_names.Instance(ec.InstancePath, e.Kind, i == 1 ? null : $"e{i}")} {At(e.A)} {At(e.B)} {N(e.Value)}",
                     i == 1 ? ec.InstancePath : $"{ec.InstancePath} ({e.Kind}{i})");
            }
        }

        /// <summary>The state rows of a rational transfer driven by <paramref name="u"/> (a dialect expression), each pole a node
        /// on a 1/|a| F capacitor so it carries the size of <paramref name="u"/> — as RationalSnpModel scales its rows — and the
        /// sum Σ R_k x_k as a dialect expression.</summary>
        private string States(string prefix, string u, IReadOnlyList<Complex> poles, Func<int, Complex> residue, string path)
        {
            var terms = new List<string>();
            for (int k = 0; k < poles.Count; k++)
            {
                var a = poles[k];
                double m = a.Magnitude;
                if (a.Imaginary == 0)
                {
                    // (s − a) x = u, y = |a| x: (1/|a|) dy/dt = u + (a/|a|) y; R x = (R/|a|) y
                    string y = _names.Internal($"{prefix}_x{k}");
                    Line($"{_names.Instance(path, 'C', $"{prefix}_x{k}")} {y} 0 {N(1 / m)}", path + " (state)");
                    Line($"{_names.Instance(path, 'B', $"{prefix}_x{k}")} 0 {y} I={u}+({N(a.Real / m)})*V({y})", path + " (state)");
                    terms.Add($"({N(residue(k).Real / m)})*V({y})");
                    continue;
                }
                // the pair a, a*: x1 = z + z~, x2 = j(z − z~); s x1 = σx1 + ωx2 + 2u, s x2 = −ωx1 + σx2; y = |a| x
                string y1 = _names.Internal($"{prefix}_x{k}a"), y2 = _names.Internal($"{prefix}_x{k}b");
                double sg = a.Real / m, om = a.Imaginary / m;
                Line($"{_names.Instance(path, 'C', $"{prefix}_x{k}a")} {y1} 0 {N(1 / m)}", path + " (state)");
                Line($"{_names.Instance(path, 'B', $"{prefix}_x{k}a")} 0 {y1} I=2*({u})+({N(sg)})*V({y1})+({N(om)})*V({y2})", path + " (state)");
                Line($"{_names.Instance(path, 'C', $"{prefix}_x{k}b")} {y2} 0 {N(1 / m)}", path + " (state)");
                Line($"{_names.Instance(path, 'B', $"{prefix}_x{k}b")} 0 {y2} I=({N(-om)})*V({y1})+({N(sg)})*V({y2})", path + " (state)");
                var r = residue(k);
                terms.Add($"({N(r.Real / m)})*V({y1})+({N(r.Imaginary / m)})*V({y2})");
                k++;   // the conjugate is covered by the pair
            }
            return terms.Count == 0 ? "0" : string.Join("+", terms);
        }

        /// <summary>An S-matrix fit as a subcircuit of the deck: per port a current sensor, Z0 in series and a behavioural source
        /// holding V − Z0·I = Σ_j D_ij u_j + Σ R x, with u_j = V_j + Z0·I_j and (s − a) x = u — RationalSnpModel's rows.</summary>
        private void FitBlock(ElaboratedComponent ec, SpiceBlockFit fit)
        {
            int n = fit.Nodes.Count;
            string rf = Node(fit.ReferenceNode);
            var sense = new string[n];
            var u = new string[n];
            _el.Append("* ").Append(ec.InstancePath).AppendLine(": circuitRF's rational fit, not the element itself");
            var tail = new string[n];
            for (int j = 0; j < n; j++)
            {
                string p = Node(fit.Nodes[j]);
                tail[j] = _names.Internal($"{ec.InstancePath}_p{j + 1}");
                sense[j] = _names.Instance(ec.InstancePath, 'V', $"p{j + 1}");
                Line($"{sense[j]} {p} {tail[j]} DC 0", $"{ec.InstancePath} (port {j + 1})");
                u[j] = $"(V({p},{rf})+({N(fit.Z0)})*i({sense[j]}))";
            }
            var stateSum = new List<string>[n];
            for (int i = 0; i < n; i++) stateSum[i] = [];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int ii = i, jj = j;
                    string s = States($"{ec.InstancePath}_s{i + 1}{j + 1}", u[jj], fit.Poles, k => fit.Residues[ii * n + jj][k], ec.InstancePath);
                    if (s != "0") stateSum[i].Add(s);
                }
            }
            for (int i = 0; i < n; i++)
            {
                var terms = new List<string>();
                for (int j = 0; j < n; j++)
                    if (fit.D[i * n + j] != 0) terms.Add($"({N(fit.D[i * n + j])})*{u[j]}");
                terms.AddRange(stateSum[i]);
                string mid = _names.Internal($"{ec.InstancePath}_w{i + 1}");
                Line($"{_names.Instance(ec.InstancePath, 'R', $"z{i + 1}")} {tail[i]} {mid} {N(fit.Z0)}", $"{ec.InstancePath} (port {i + 1} Z0)");
                Line($"{_names.Instance(ec.InstancePath, 'B', $"w{i + 1}")} {mid} {rf} V={(terms.Count == 0 ? "0" : string.Join("+", terms))}",
                     $"{ec.InstancePath} (port {i + 1} wave)");
            }
            _fits.Add((ec.InstancePath, $"ngspice ran circuitRF's fit of {ec.InstancePath}, not the element itself"));
        }

        /// <summary>A lossy line as its delay and causal residual (brief TRN-4b): at each end V − Zc·I = H~ * W_far(t − τ), the
        /// far wave W = V + Zc·I delayed by a lossless T driven ideally and terminated in its own impedance.</summary>
        private void FitLine(ElaboratedComponent ec, SpiceLineResidual r)
        {
            _el.Append("* ").Append(ec.InstancePath).AppendLine(": a lossless T plus circuitRF's causal residual, not the line itself");
            string a = Node(r.NodeA), b = Node(r.NodeB);
            var ends = new[] { (Near: a, Far: b, Tag: "a"), (Near: b, Far: a, Tag: "b") };
            var sense = new Dictionary<string, string>();
            var tail = new Dictionary<string, string>();
            foreach (var e in ends)
            {
                tail[e.Tag] = _names.Internal($"{ec.InstancePath}_{e.Tag}");
                sense[e.Tag] = _names.Instance(ec.InstancePath, 'V', e.Tag);
                Line($"{sense[e.Tag]} {e.Near} {tail[e.Tag]} DC 0", $"{ec.InstancePath} (end {e.Tag})");
            }
            foreach (var e in ends)
            {
                string other = e.Tag == "a" ? "b" : "a";
                // the far end's outgoing wave, delayed
                string w = _names.Internal($"{ec.InstancePath}_w{other}"), wd = _names.Internal($"{ec.InstancePath}_wd{other}");
                Line($"{_names.Instance(ec.InstancePath, 'B', $"w{other}")} {w} 0 V=V({e.Far})+({N(r.Zc)})*i({sense[other]})", $"{ec.InstancePath} (wave)");
                Line($"{_names.Instance(ec.InstancePath, 'T', $"d{other}")} {w} 0 {wd} 0 Z0=1 TD={N(r.TauS)}", $"{ec.InstancePath} (delay)");
                Line($"{_names.Instance(ec.InstancePath, 'R', $"d{other}")} {wd} 0 1", $"{ec.InstancePath} (delay termination)");
                string sum = States($"{ec.InstancePath}_h{e.Tag}", $"V({wd})", r.Poles, k => r.Residues[k], ec.InstancePath);
                string drive = $"({N(r.D)})*V({wd})" + (sum == "0" ? "" : "+" + sum);
                string mid = _names.Internal($"{ec.InstancePath}_e{e.Tag}");
                Line($"{_names.Instance(ec.InstancePath, 'R', $"z{e.Tag}")} {tail[e.Tag]} {mid} {N(r.Zc)}", $"{ec.InstancePath} (Zc)");
                Line($"{_names.Instance(ec.InstancePath, 'B', $"h{e.Tag}")} {mid} 0 V={drive}", $"{ec.InstancePath} (residual)");
            }
            _fits.Add((ec.InstancePath, $"ngspice ran circuitRF's fit of {ec.InstancePath}, not the element itself"));
        }

        // ── the deck ─────────────────────────────────────────────────────

        private string Assemble(out List<(string Vector, string CircuitRf)> saved)
        {
            var a = req.Analysis;
            var sb = new StringBuilder();
            sb.Append("* circuitRF transient deck: bench ").Append(req.BenchName.Length > 0 ? req.BenchName : "(unnamed)")
              .Append(", analysis ").AppendLine(a.Name);
            sb.Append("* written by circuitRF ").AppendLine(req.Version.Length > 0 ? req.Version : "(unknown version)");
            sb.AppendLine("* names (deck = circuitRF):");
            foreach (var (net, deck) in _names.Nets) sb.Append("*   net ").Append(deck).Append(" = ").AppendLine(net);
            foreach (var (path, deck) in _names.Instances) sb.Append("*   instance ").Append(deck).Append(" = ").AppendLine(path);
            foreach (var l in _lines) sb.Append("* ").AppendLine(l.Sentence);
            foreach (var (_, s) in _fits) sb.Append("* ").AppendLine(s);
            sb.AppendLine();
            foreach (var inc in _includes) sb.Append(".include \"").Append(inc).AppendLine("\"");
            sb.Append(".temp ").AppendLine(N(_nl.AmbientC));
            sb.Append(".options TNOM=").Append(N(Temperature.NominalC))
              .Append(" RELTOL=").Append(N(_relTol)).Append(" VNTOL=").Append(N(_vTol)).Append(" ABSTOL=").Append(N(_iTol))
              .AppendLine(_gear ? " METHOD=GEAR MAXORD=2" : " METHOD=TRAP");
            if (a.SpiceOptions.Trim().Length > 0) sb.Append(".options ").AppendLine(a.SpiceOptions.Trim());
            sb.Append(_el);
            var ics = _ic.Where(kv => kv.Key != "0").ToList();
            if (ics.Count > 0)
                sb.Append(".ic ").AppendLine(string.Join(' ', ics.Select(kv => $"v({_names.Net(kv.Key)})={N(kv.Value)}")));
            sb.Append(".tran ").Append(N(_step)).Append(' ').Append(N(_stop)).Append(' ').Append(N(_start)).Append(' ').Append(N(_maxStep))
              .AppendLine(_uic ? " uic" : "");

            saved = [];
            var (_, all) = _nl.Nodes.ResultRows(excludeInternal: true);
            var labeled = all.Where(n => _nl.Nodes.LabeledNames.Contains(n)).ToList();
            foreach (var net in (labeled.Count > 0 ? labeled : all.ToList()).Distinct())
                if (_nl.Nodes.TryGetIndex(net, out int idx) && idx > 0) saved.Add(($"v({_names.Net(net)})", net));
            foreach (var p in _probePaths) saved.Add(($"i({_vsourceDeck[p]})", p));
            // brief TRN-7 R-trn7-6 — each IProbe's first net too, as the native engine records it, so the probe's power v·i is the
            // same product on either solver
            foreach (var ec in _nl.Components)
                if (_probePaths.Contains(ec.InstancePath) && ec.Nodes.Length >= 2 && ec.Nodes[0] > 0)
                {
                    string net = _nl.Nodes.NameOf(ec.Nodes[0]);
                    string v = $"v({_names.Net(net)})";
                    if (!saved.Any(x => x.Vector == v)) saved.Add((v, net));
                }
            if (saved.Count > 0) sb.Append(".save ").AppendLine(string.Join(' ', saved.Select(s => s.Vector)));
            if (_osdi.Count > 0)
            {
                sb.AppendLine(".control");
                foreach (var o in _osdi) sb.Append("pre_osdi \"").Append(o).AppendLine("\"");
                sb.AppendLine(".endc");
            }
            sb.AppendLine(".end");
            return sb.ToString();
        }
    }
}
