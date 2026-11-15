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
            channels = new PhasedArrayChannels(channel.Name, channel.Ports[0], channel.Ports[1], reads.Count == 0, reads, omitted);
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

        bool quiet = noiseOff || _noiseOffDepth > 0;
        netlist.AddComponent(new ElaboratedComponent(PhasedArrayModel.TypeName, $"{childPath}.feed", feedNodes, p, feed)
                             { NoiseOff = quiet, NoiseOn = noiseOn });
        netlist.AddComponent(new ElaboratedComponent(PhasedArrayModel.TypeName, childPath, elementNodes, p, array)
                             { ReferenceNode = 0, NoiseOff = quiet, NoiseOn = noiseOn });
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
