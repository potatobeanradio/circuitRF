// brief-paa-4 R-paa4-1 / R-paa4-2 / R-paa4-4 / R-paa4-5 / R-paa4-6 — a Phased Antenna Array in Feed mode, placed as its parts
// (docs/design/phased-array.md §4.3-§4.4).
//
// ONE PIN, F. Behind it, three things on internal nodes, each an ordinary elaborated component, so every analysis an ordinary circuit
// runs in — S-parameters, S-parameter noise, HB, multi-tone HB, envelope — takes the array with nothing special done (R-paa4-8):
//
//   PA1.feed    the beamformer (PhasedArrayFeedModel): F split N ways, path n carrying element n's quantised weight;
//   PA1.ch[n]   the channel cell, when Channel= names one: an ordinary hierarchical instance, its first port on the beamformer's path n and
//               its second on element n, so V(PA1.ch[3].out) is a node like any sub-instance's;
//   PA1         the array network (PhasedArrayModel) on the N element nodes — the component a fields analysis names (block=PA1, D8).
//
// THE WEIGHTS ARE EVALUATED ONCE PER ELEMENT in element scope (ElementScope), Amp[n]/Phase[n]/Delay[n] winning over Amp/Phase/Delay, then
// quantised (PhaseBits, AttenStep) before the channel. Phase takes degrees as its site unit (Evaluator.EvalPhase); Delay is seconds.
//
// THE CHANNEL'S SCOPE IS THE ELEMENT'S, so an expression inside the cell may read n, row, col, x, y. Whether any does decides IDENTICAL: a
// channel whose expressions (its own and every cell below it, its own parameter and variable names excepted) name none of the five is
// identical across elements, the fact PAA-8's fast path reads. An element switched off keeps a channel that differs per element (its noise
// still reaches the feed) and loses an identical one, the element then terminated in Z0 by the beamformer's matched zero-weight port.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Elaboration;

public sealed partial class Elaborator
{
    private void ElaboratePhasedArrayFeed(Instance inst, string childPath, Func<string, string> resolveNet, Scope scope, Scope globalScope,
                                          ElaboratedNetlist netlist)
    {
        inst = TakeNoiseSwitch(inst, childPath, out bool noiseOff, out bool noiseOn);
        if (inst.NetBindings.Count != 1)
            throw new InvalidOperationException($"'{childPath}': a {PhasedArrayModel.DisplayName} in Feed mode has one pin, F, and its line binds " +
                                                $"{inst.NetBindings.Count} nets. Bind one net, the feed; Elements mode is the one with a pin per element.");

        var p = ResolvePhasedArrayParameters(inst, scope);
        PhasedArrayModel array;
        try { array = PhasedArrayModel.Create(p); }
        catch (ParameterRefusalException ex) { throw new ParameterRefusalException($"'{childPath}': {ex.Message}"); }
        var lattice = array.Lattice;
        int n = lattice.Count;

        // ── the weights, per element ──────────────────────────────────────────────────────────────
        var common = new Dictionary<string, ParameterAssignment>(StringComparer.OrdinalIgnoreCase);
        var indexed = new Dictionary<(string, int), ParameterAssignment>();
        foreach (var ov in inst.Overrides)
        {
            if (!PhasedArrayModel.IsElementParameter(ov.Name, out string baseName, out int? index)) continue;
            string key = baseName.ToLowerInvariant();
            if (index is null) { common[key] = ov; continue; }
            if (index < 1 || index > n)
                throw new ParameterRefusalException($"'{childPath}': {ov.Name} names element {index}, and the {lattice.Rows} × {lattice.Cols} array's elements are " +
                                                    $"numbered 1 to {n}, row-major from the top left seen from boresight.");
            indexed[(key, index.Value)] = ov;
        }
        int bits = WholeParameter(p, "PhaseBits", childPath, 0, 24);
        double step = p.TryGetValue("AttenStep", out var sv) ? RealParameter(sv, "AttenStep", childPath) : 0;
        if (step < 0) throw new ParameterRefusalException($"'{childPath}': AttenStep={step} is not a step: it is 0 (continuous) or a positive number of dB.");

        var amp = new double[n];
        var phase = new double[n];
        var delay = new double[n];
        var scopes = new Scope[n];
        for (int e = 0; e < n; e++)
        {
            var (x, y) = lattice.Position(e);
            int row = e / lattice.Cols + 1, col = e % lattice.Cols + 1;
            var es = ElementScope.Create(scope, $"{childPath}[{e + 1}]", _evaluator, e + 1, row, col, x, y, lattice.Rows, lattice.Cols);
            scopes[e] = es;
            ParameterAssignment? Pick(string b) => indexed.TryGetValue((b, e + 1), out var o) ? o : common.GetValueOrDefault(b);
            double Of(ParameterAssignment? ov, double fallback, Func<ParameterAssignment, Value> eval)
            {
                if (ov is null) return fallback;
                Value v;
                try { v = eval(ov); }
                catch (ExpressionException ex)
                {
                    throw new ParameterRefusalException($"'{childPath}': {ov.Name}={ov.Expression} at element {e + 1} (row {row}, col {col}): {ex.Message}");
                }
                if (v.Kind != ValueKind.Real || !double.IsFinite(v.AsReal()))
                    throw new ParameterRefusalException($"'{childPath}': {ov.Name}={ov.Expression} is {v} at element {e + 1} (row {row}, col {col}): it is a real number.");
                return v.AsReal();
            }
            string site(ParameterAssignment ov) => $"{inst.InstanceName}.{ov.Name}";
            amp[e] = Of(Pick("amp"), 1, ov => _evaluator.EvalParameter(ov.Expression, es, ov.Unit, site(ov)));
            phase[e] = Of(Pick("phase"), 0, ov => _evaluator.EvalPhase(ov.Expression, es, ov.Unit, site(ov)));
            delay[e] = Of(Pick("delay"), 0, ov => _evaluator.EvalParameter(ov.Expression, es, ov.Unit, site(ov)));
        }

        // ── quantisation (R-paa4-4): the phase to 2π/2^b, the attenuation from the largest amplitude to the step ──
        var requested = new Complex[n];
        var quantised = new Complex[n];
        double largest = 0;
        for (int e = 0; e < n; e++) if (lattice.IsOn(e)) largest = Math.Max(largest, Math.Abs(amp[e]));
        for (int e = 0; e < n; e++)
        {
            if (!lattice.IsOn(e)) continue;
            requested[e] = amp[e] * Complex.FromPolarCoordinates(1, phase[e]);
            double ph = phase[e] + (amp[e] < 0 ? Math.PI : 0);
            quantised[e] = Complex.FromPolarCoordinates(ArrayWeights.QuantiseAmplitude(Math.Abs(amp[e]), largest, step), ArrayWeights.QuantisePhase(ph, bits));
        }
        var feed = new PhasedArrayFeedModel(requested, quantised, delay, array.Z0);
        array.Feed = feed;
        int off = lattice.Off.Count;
        array.FeedNote = $"Feed mode: one pin, F, split {n} ways to the elements through their weights; phases " +
                         (bits > 0 ? $"rounded to {360 / Math.Pow(2, bits):G6}° ({bits} bits)" : "continuous") + ", amplitudes " +
                         (step > 0 ? $"rounded to {step:G6} dB of attenuation from the largest" : "continuous") +
                         (off > 0 ? $"; {off} element{(off == 1 ? "" : "s")} switched off, each terminated in Z0 by its zero weight" : "") + ".";
        if (feed.Activity != Activity.Passive)
            netlist.AddWarningOnce($"phased-array-gain:{childPath}",
                $"{PhasedArrayModel.Describe(childPath)}: the weights' RMS amplitude is {feed.SigmaMax:G4}, above 1, so its beamformer has gain and is not " +
                "passive; it has no thermal noise. A taper normalised to 1 at its peak keeps the beamformer passive.");

        // ── the channel cell (R-paa4-5) ─────────────────────────────────────────────────────────
        string channelName = p.TryGetValue("Channel", out var cv) && cv.Kind == ValueKind.String ? cv.AsString().Trim() : "";
        Cell? channel = null;
        PhasedArrayChannels? channels = null;
        if (channelName.Length > 0)
        {
            channel = FindCell(channelName)
                      ?? throw new ParameterRefusalException($"'{childPath}': Channel={channelName} names no cell. It names a two-port cell placed once per element, " +
                                                             "its first port on the beamformer and its second on the element.");
            if (channel.Ports.Count != 2)
                throw new ParameterRefusalException($"'{childPath}': Channel={channelName} names a cell with {channel.Ports.Count} port" +
                                                    $"{(channel.Ports.Count == 1 ? "" : "s")} ({string.Join(", ", channel.Ports)}), and a channel is a two-port: " +
                                                    "its first port in, from the beamformer, and its second out, to the element.");
            var reads = ElementNamesRead(channel);
            var omitted = reads.Count == 0 ? lattice.Off.OrderBy(k => k).ToList() : [];
            channels = new PhasedArrayChannels(channel.Name, channel.Ports[0], channel.Ports[1], reads.Count == 0, reads, omitted)
            {
                Sites = reads.Count == 0 ? [] : ElementNameSites(channel, "", []),
            };
            array.Channels = channels;
            netlist.AddNoteOnce($"phased-array-channel:{childPath}", channels.Sentence(childPath));
        }

        // ── the nodes, then the parts ───────────────────────────────────────────────────────────
        int fNode = netlist.Nodes.GetOrAssign(resolveNet(inst.NetBindings[0]));
        var feedNodes = new int[2 * (n + 1)];
        feedNodes[0] = fNode;
        var elementNodes = new int[n];
        var placed = new List<(int K, string Path, string WName, string EName)>();
        for (int k = 1; k <= n; k++)
        {
            bool withChannel = channel is not null && !(channels!.Omitted.Contains(k));
            if (withChannel)
            {
                string chPath = PhasedArrayChannels.PathOf(childPath, k);
                string wName = $"{chPath}.{channel!.Ports[0]}", eName = $"{chPath}.{channel.Ports[1]}";
                feedNodes[2 * k] = netlist.Nodes.GetOrAssign(wName);
                elementNodes[k - 1] = netlist.Nodes.GetOrAssign(eName);
                placed.Add((k, chPath, wName, eName));
            }
            else
            {
                int e = netlist.Nodes.GetOrAssign($"__paa_{childPath}_e{k}");
                elementNodes[k - 1] = e;
                feedNodes[2 * k] = e;
            }
        }

        // brief-paa-7 — each port's noise (and, for the elements, received-wave) source: one node per port after its pins.
        var feedSources = new int[n + 1];
        for (int k = 0; k <= n; k++) feedSources[k] = netlist.Nodes.GetOrAssign($"__paa_{childPath}_fs{k}");
        var elementSources = PhasedArraySourceNodes(childPath, n, netlist);

        bool quiet = noiseOff || _noiseOffDepth > 0;
        netlist.AddComponent(new ElaboratedComponent(PhasedArrayModel.TypeName, $"{childPath}.feed", [.. feedNodes, .. feedSources], p, feed)
                             { NoiseOff = quiet, NoiseOn = noiseOn });
        netlist.AddComponent(new ElaboratedComponent(PhasedArrayModel.TypeName, childPath, [.. elementNodes, .. elementSources], p, array)
                             { ReferenceNode = 0, NoiseOff = quiet, NoiseOn = noiseOn });
        ConfigureReceive(array, inst, childPath, scope, p, elementSources, netlist);
        if (noiseOff) _noiseOffDepth++;
        try
        {
            foreach (var (k, chPath, wName, eName) in placed)
            {
                var portMap = new Dictionary<string, string>(StringComparer.Ordinal) { [channel!.Ports[0]] = wName, [channel.Ports[1]] = eName };
                var chScope = BuildCellScope(channel, parentScope: scopes[k - 1], overrides: [], scopeName: chPath, instanceName: $"ch[{k}]");
                FlattenCell(channel, chPath, portMap, chScope, globalScope, netlist);
            }
        }
        finally { if (noiseOff) _noiseOffDepth--; }
    }

    /// <summary>R-paa4-5 — which of element scope's five names <paramref name="cell"/>'s expressions read, its own and every cell below it,
    /// a name the cell declares as its own parameter or variable excepted. Empty: the channel is identical across elements. A cell whose text
    /// cannot be read is taken to differ, so nothing is claimed identical that was not shown to be.</summary>
    private IReadOnlyList<string> ElementNamesRead(Cell cell)
    {
        if (NamesReadIn(cell, []) is not { } names) return ["an expression that could not be read"];
        var own = cell.Parameters.Select(q => q.Name).Concat(cell.Variables.Select(v => v.Name)).ToHashSet(StringComparer.Ordinal);
        return [.. ElementScope.Names.Where(x => names.Contains(x) && !own.Contains(x))];
    }

    /// <summary>brief-paa-8 R-paa8-2 — the expressions in <paramref name="cell"/> and the cells below it that read one of element scope's names,
    /// each as the parameter it sets (<c>T1.Loss=3+0.1*n</c>, the instance path from the channel), so a fallback names what made the channel
    /// differ per element. A cell's own parameter or variable of the same name shadows it there.</summary>
    private List<string> ElementNameSites(Cell cell, string prefix, HashSet<string> visiting)
    {
        var sites = new List<string>();
        if (!visiting.Add(cell.Name)) return sites;
        var own = cell.Parameters.Select(q => q.Name).Concat(cell.Variables.Select(v => v.Name)).ToHashSet(StringComparer.Ordinal);
        bool Reads(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                return new Tokenizer(text).Tokenize().Any(t => t.Kind == TokenKind.Identifier && ElementScope.Names.Contains(t.Text) && !own.Contains(t.Text));
            }
            catch (Exception) { return false; }
        }
        foreach (var q in cell.Parameters.Where(q => Reads(q.DefaultExpression))) sites.Add($"{prefix}parameter {q.Name}={q.DefaultExpression}");
        foreach (var v in cell.Variables.Where(v => Reads(v.Expression))) sites.Add($"{prefix}{v.Name}={v.Expression}");
        foreach (var inst in cell.Instances)
        {
            foreach (var ov in inst.Overrides.Where(o => Reads(o.Expression))) sites.Add($"{prefix}{inst.InstanceName}.{ov.Name}={ov.Expression}");
            if (!ComponentModelFactory.IsPrimitive(inst.Reference) && FindCell(inst.Reference) is { } child)
                sites.AddRange(ElementNameSites(child, $"{prefix}{inst.InstanceName}.", visiting));
        }
        visiting.Remove(cell.Name);
        return sites;
    }

    /// <summary>brief-paa-7 — the node of each element's source, after its pins (PhasedArrayModel.SourceNode).</summary>
    private static int[] PhasedArraySourceNodes(string childPath, int n, ElaboratedNetlist netlist)
    {
        var nodes = new int[n];
        for (int k = 0; k < n; k++) nodes[k] = netlist.Nodes.GetOrAssign($"__paa_{childPath}_s{k + 1}");
        return nodes;
    }

    /// <summary>
    /// brief-paa-7 R-paa7-1 / R-paa7-2 — the array's sky and its incident waves. Tsky is a temperature or an expression of the elevation
    /// <c>el</c> (radians, so <c>el &lt; 10 deg</c> reads as written), tabulated every 0.01° from the horizon to the zenith; Tground is the
    /// temperature below the horizon. Either omitted is the array's own physical temperature, so an array given neither has the noise a passive
    /// block has always had. Each incident wave k (RxTheta[k], RxPhi[k] in degrees by default, RxPd[k] in dBm/m², RxFreq[k], RxPol[k] a word or an
    /// angle) becomes a tone source on the elements' source nodes, <c>PA1.rx[k]</c>, carrying the wave the record's patterns say it puts on each.
    /// </summary>
    private void ConfigureReceive(PhasedArrayModel array, Instance inst, string childPath, Scope scope, IReadOnlyDictionary<string, Value> p,
                                  int[] sources, ElaboratedNetlist netlist)
    {
        ParameterAssignment? tsky = null, tground = null;
        var waves = new SortedDictionary<int, Dictionary<string, ParameterAssignment>>();
        foreach (var ov in inst.Overrides)
        {
            if (!PhasedArrayModel.IsReceiveParameter(ov.Name, out string baseName, out int? index)) continue;
            if (index is null)
            {
                if (baseName.Equals("Tsky", StringComparison.OrdinalIgnoreCase)) tsky = ov; else tground = ov;
                continue;
            }
            if (index < 1)
                throw new ParameterRefusalException($"'{childPath}': {ov.Name} names incident wave {index}, and the waves are numbered from 1.");
            if (!waves.TryGetValue(index.Value, out var set)) waves[index.Value] = set = new Dictionary<string, ParameterAssignment>(StringComparer.OrdinalIgnoreCase);
            set[baseName] = ov;
        }

        double physical = Temperature.ResolveDeviceC(p, _ambientC) + Temperature.KelvinOffset;
        if (tsky is not null || tground is not null)
        {
            double ground = tground is null ? physical : Kelvin(tground, scope, childPath);
            string said = $"Tsky = {(tsky?.Expression ?? $"{physical:G5} K (the array's own temperature)")}, Tground = " +
                          $"{(tground?.Expression ?? $"{physical:G5} K (the array's own temperature)")}";
            if (tsky is null) array.Sky = new ArraySky(physical, ground, said);
            else if (!AstWalker.CollectRefs(Parser.Parse(tsky.Expression)).Contains("el")) array.Sky = new ArraySky(Kelvin(tsky, scope, childPath), ground, said);
            else
            {
                int count = (int)Math.Round(90 / ArraySky.StepDeg) + 1;
                var table = new double[count];
                for (int i = 0; i < count; i++)
                {
                    string name = $"{childPath}.Tsky@{i}";
                    var at = new Scope(name, scope);
                    at.Bind("el", "__resolved__", "rad");
                    _evaluator.InjectResolved(name, "el", new Value(i * ArraySky.StepDeg * Math.PI / 180));
                    table[i] = Kelvin(tsky, at, childPath);
                }
                array.Sky = new ArraySky(table, ground, said);
            }
        }

        var notes = new List<string>();
        var given = new Dictionary<int, ArrayIncidentWave>();
        foreach (var (k, set) in waves)
        {
            string W(string b) => $"Rx{b}[{k}]";
            ParameterAssignment Need(string b) => set.TryGetValue("Rx" + b, out var ov) ? ov
                : throw new ParameterRefusalException($"'{childPath}': incident wave {k} has no {W(b)}: a wave needs {W("Pd")} (its power density, dBm/m²) and {W("Freq")}.");
            double Real(ParameterAssignment ov, Value v) => v.Kind == ValueKind.Real && double.IsFinite(v.AsReal()) ? v.AsReal()
                : throw new ParameterRefusalException($"'{childPath}': {ov.Name}={ov.Expression} is {v}: it is a real number.");
            string Site(ParameterAssignment ov) => $"{inst.InstanceName}.{ov.Name}";
            double theta = set.TryGetValue("RxTheta", out var th) ? Real(th, _evaluator.EvalPhase(th.Expression, scope, th.Unit, Site(th))) : 0;
            double phi = set.TryGetValue("RxPhi", out var ph) ? Real(ph, _evaluator.EvalPhase(ph.Expression, scope, ph.Unit, Site(ph))) : 0;
            var pd = Need("Pd");
            double dbm = Real(pd, _evaluator.EvalParameter(pd.Expression, scope, null, Site(pd)));
            var fq = Need("Freq");
            double hz = Real(fq, _evaluator.EvalParameter(fq.Expression, scope, fq.Unit, Site(fq)));
            if (!(hz > 0)) throw new ParameterRefusalException($"'{childPath}': {fq.Name}={fq.Expression} is {hz} Hz: a wave's frequency is positive.");
            (System.Numerics.Complex, System.Numerics.Complex) pol = (1, 0);
            string polSaid = "theta";
            if (set.TryGetValue("RxPol", out var po))
            {
                string word = Unquote(po.Expression.Trim()).Trim();
                if (word.ToLowerInvariant() is "theta" or "phi" or "rhcp" or "lhcp") { pol = ArrayReceive.Polarization(word, null); polSaid = word.ToLowerInvariant(); }
                else
                {
                    double psi;
                    try { psi = Real(po, _evaluator.EvalPhase(po.Expression, scope, po.Unit, Site(po))); }
                    catch (ExpressionException)
                    {
                        throw new ParameterRefusalException($"'{childPath}': {po.Name}={po.Expression} is not a polarization: it is theta, phi, rhcp, lhcp or an angle " +
                                                            "from θ̂ towards φ̂ (degrees unless a unit says otherwise).");
                    }
                    pol = ArrayReceive.Polarization("", psi);
                    polSaid = $"{psi * 180 / Math.PI:G6}° from θ̂";
                }
            }
            ArrayRadiation rad;
            try { rad = array.Radiation(hz); }
            catch (InvalidOperationException ex) { throw new ParameterRefusalException($"'{childPath}': incident wave {k}: {ex.Message}"); }
            double density = Math.Pow(10, (dbm - 30) / 10);
            var c = ArrayReceive.Waves(rad, array.Lattice, theta, phi, density, hz, pol, array.Radiates);
            given[k] = new ArrayIncidentWave(theta, phi, dbm, hz, pol.Item1, pol.Item2);
            var model = new ArrayIncidentWaveModel(hz, c);
            var parameters = new Dictionary<string, Value>(StringComparer.Ordinal) { ["Freq"] = new Value(hz) };
            netlist.AddComponent(new ElaboratedComponent(ArrayIncidentWaveTypeName, $"{childPath}.rx[{k}]", sources, parameters, model));
            string at = Math.Abs(rad.FrequencyHz - hz) <= 1e-9 * hz ? ""
                : $"; its record holds no pattern at {hz / 1e9:G6} GHz, so the patterns at {rad.FrequencyHz / 1e9:G6} GHz, the nearest, are used";
            notes.Add($"incident wave {k}: {dbm:G6} dBm/m² at {hz / 1e9:G6} GHz from θ {theta * 180 / Math.PI:G6}°, φ {phi * 180 / Math.PI:G6}°, polarization {polSaid}{at}");
        }
        if (notes.Count > 0)
        {
            array.IncidentNotes = notes;
            array.IncidentWaves = given;
            netlist.AddNoteOnce($"phased-array-rx:{childPath}",
                $"{PhasedArrayModel.Describe(childPath)} receives {string.Join("; ", notes)}. Each is a tone source on the elements ({childPath}.rx[k]), " +
                "each element's wave by reciprocity from its embedded pattern.");
        }
    }

    /// <summary>brief-paa-7 — the component type of an incident wave's tone source.</summary>
    public const string ArrayIncidentWaveTypeName = "PhasedArrayRx";

    private double Kelvin(ParameterAssignment ov, Scope scope, string childPath)
    {
        Value v;
        try { v = _evaluator.EvalParameter(ov.Expression, scope, null, ov.Name); }
        catch (ExpressionException ex) { throw new ParameterRefusalException($"'{childPath}': {ov.Name}={ov.Expression}: {ex.Message}"); }
        double t = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
        if (!(t >= 0) || !double.IsFinite(t))
            throw new ParameterRefusalException($"'{childPath}': {ov.Name}={ov.Expression} is {v}: a brightness temperature is a number of kelvin, zero or more.");
        return t;
    }

    private static int WholeParameter(IReadOnlyDictionary<string, Value> p, string name, string path, int fallback, int max)
    {
        if (!p.TryGetValue(name, out var v)) return fallback;
        double x = RealParameter(v, name, path);
        if (x < 0 || x != Math.Round(x) || x > max)
            throw new ParameterRefusalException($"'{path}': {name}={v} is not a number of bits: it is a whole number from 0 (continuous) to {max}.");
        return (int)x;
    }

    private static double RealParameter(Value v, string name, string path)
        => v.Kind == ValueKind.Real && double.IsFinite(v.AsReal()) ? v.AsReal()
           : throw new ParameterRefusalException($"'{path}': {name}={v} is not a number.");
}
