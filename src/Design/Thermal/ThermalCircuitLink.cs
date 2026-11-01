// brief-em3d-79 — a thermal setup driven from a circuit: the schematic names the harmonic-balance power sweep, the instance
// whose S-parameter model is this 3D view's EM result carries each pin's DC and harmonic currents at every drive level, and
// the thermal run solves the wires at each (R-em3d79-1/-2/-3). ONE WAY: nothing goes back into the HB — it sees the wires at
// whatever temperature its S-parameters were solved at; the thermal run reports what temperature they actually reach.
//
//   * The circuit is read the way the application reads it (a .csch through its extraction, a .cnl as itself), `--set` lands
//     in its globals before elaboration, the chain is chosen by ChainSelector — a named inner analysis promoted to its
//     sweep, exactly as `hb` promotes it — and it runs through HbCircuitRun, the function `hb` calls. So the HB numbers here
//     are the `hb` verb's numbers.
//   * PORT p IS PIN p (overview §1c): the EM result has one pin per port, numbered as the ports are. The instance is found by
//     its Touchstone path — one of this 3D view's EM setups' PREDICTABLE result paths (EmRunService.ResolveSnpBasePath) —
//     never by a name the user has to keep in step.
//   * The pin currents come from HB's linear back-solver, OPT-IN for this one instance (AnalysisSettings.HbPinCurrents), so
//     no other HB result changes.
//   * HB phasors are PEAK (HbFft's frozen convention: X[k] = raw[k]/(N/2), so a cos of amplitude A has |X[1]| = A); brief
//     78's harmonics are peak internally, so the conversion is the identity — stated once, in PeakOf, and pinned by a test.
//
// What the thermal run receives is an ordinary brief-78 current setup: one entry per pin, whose Dc / F0 / Amp texts are names
// in the reserved "circuit:" space (never an identifier a document can declare), bound per point from the table below. So the
// conductive balance, the array share, both-ends driving and the continuation between points are brief 77/78's, unchanged.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;

namespace CircuitRF.Design.Thermal;

/// <summary>One pin of the linked instance: its port number (= pin number), the circuit net on it, and its currents per point.</summary>
/// <param name="Dc">Per point, the DC current into the pin, A (signed: positive enters the port's positive conductor).</param>
/// <param name="Peak">Per point, per harmonic k = 0 … K, the peak magnitude A (index 0 unused), after the relative floor.</param>
public sealed record ThermalCircuitPin(int Pin, string Net, double[] Dc, double[][] Peak);

/// <summary>R-em3d79-3 — a circuit's HB sweep as the thermal run consumes it.</summary>
public sealed class ThermalCircuitDrive
{
    public required string SchematicPath { get; init; }
    public required string SnpPath { get; init; }
    public required string Instance { get; init; }
    /// <summary>The chain that ran (after promotion).</summary>
    public required string Analysis { get; init; }
    /// <summary>The HB's own sweep axes (empty for an unswept HB): the thermal result's axes.</summary>
    public required List<(string Var, double[] Values, string Unit)> Axes { get; init; }
    public required int Points { get; init; }
    public required bool[] Converged { get; init; }
    /// <summary>Per point, the HB's fundamental, Hz.</summary>
    public required double[] F0 { get; init; }
    public required int MaxHarmonic { get; init; }
    public required IReadOnlyList<ThermalCircuitPin> Pins { get; init; }
    /// <summary>The HB cubes copied into the thermal result, each on the HB's sweep axes.</summary>
    public required IReadOnlyList<(string Name, DataCube Cube)> Carried { get; init; }
    public required List<string> Notes { get; init; }
    /// <summary>brief-em3d-87 — every file the circuit was read from: the schematic, each sub-cell schematic and <c>.ccell</c> its
    /// extraction descended into, and each S-parameter file its elaborated netlist names (this view's own EM result among
    /// them). What the thermal run's input manifest hashes beside the 3D view's own inputs.</summary>
    public required IReadOnlyList<string> FilesRead { get; init; }

    /// <summary>
    /// The harmonics any pin carries at any point, and whether any pin carries DC — what the synthetic setup states.
    /// </summary>
    public (bool Dc, int[] Harmonics) Carries(ThermalCircuitPin p)
        => (p.Dc.Any(d => d != 0), [.. Enumerable.Range(1, MaxHarmonic).Where(k => p.Peak.Any(pt => pt[k] != 0))]);

    /// <summary>
    /// <paramref name="t"/> with its currents replaced by one ordinary entry per pin that carries anything — its Dc and each
    /// harmonic's Amp (Peak) a reserved name bound per point by <see cref="Point"/>, and F0 the HB's fundamental.
    /// </summary>
    public CemThermal Setup(CemThermal t)
    {
        var s = t.Clone();
        var currents = new List<CemThermalCurrent>();
        foreach (var p in Pins)
        {
            var (dc, harmonics) = Carries(p);
            if (!dc && harmonics.Length == 0) continue;
            currents.Add(new CemThermalCurrent
            {
                Port = p.Pin,
                Dc = dc ? ThermalCircuitLink.DcName(p.Pin) : null,
                F0 = harmonics.Length > 0 ? ThermalCircuitLink.F0Name : null,
                Harmonics = harmonics.Length > 0
                    ? [.. harmonics.Select(k => new CemThermalHarmonic { N = k, Amp = ThermalCircuitLink.HarmonicName(p.Pin, k), As = ThermalAmplitude.Peak })]
                    : null,
            });
        }
        s.Currents = currents.Count > 0 ? currents : null;
        return s;
    }

    /// <summary>Point <paramref name="i"/>'s values of the reserved names, and its sweep coordinates (so a continuation
    /// between two points interpolates the sweep value with the currents, and a runaway is placed in the sweep's own unit).</summary>
    public List<(string Var, double Value)> Point(int i)
    {
        var list = new List<(string, double)> { (ThermalCircuitLink.F0Name, F0[i]) };
        foreach (var p in Pins)
        {
            list.Add((ThermalCircuitLink.DcName(p.Pin), p.Dc[i]));
            for (int k = 1; k <= MaxHarmonic; k++) list.Add((ThermalCircuitLink.HarmonicName(p.Pin, k), p.Peak[i][k]));
        }
        var idx = Indices(i);
        for (int a = 0; a < Axes.Count; a++) list.Add((ThermalCircuitLink.AxisName(Axes[a].Var), Axes[a].Values[idx[a]]));
        return list;
    }

    /// <summary>Point <paramref name="i"/>'s index along each axis (last axis fastest — row-major, as every cube).</summary>
    public int[] Indices(int i)
    {
        var idx = new int[Axes.Count];
        for (int a = Axes.Count - 1; a >= 0; a--)
        {
            idx[a] = i % Axes[a].Values.Length;
            i /= Axes[a].Values.Length;
        }
        return idx;
    }

    /// <summary>A drive vector's sweep coordinates, as a sentence names them: <c>Pin ≈ 31.2 dBm</c>. Empty when it has none.</summary>
    public string Say(IReadOnlyList<(string Var, double Value)> point)
        => string.Join(", ", Axes.Select(a => (a, v: point.FirstOrDefault(p => p.Var == ThermalCircuitLink.AxisName(a.Var)).Value))
                                 .Select(x => $"{x.a.Var} ≈ {x.v.ToString("G4", CultureInfo.InvariantCulture)}{(x.a.Unit.Length > 0 ? " " + x.a.Unit : "")}"));
}

/// <summary>R-em3d79 — resolving, running and reading a thermal setup's circuit link.</summary>
public static class ThermalCircuitLink
{
    /// <summary>The reserved names' prefix: a colon is never part of a document identifier, so no VAR can collide.</summary>
    public const string Prefix = "circuit:";

    /// <summary>R-em3d79-2c — a harmonic below this fraction of its pin's largest current at a point is taken as zero.</summary>
    public const double RelativeFloor = 1e-6;

    /// <summary>A current below this, A, is the solve's round-off (an open pin), never a current: it heats nothing measurable,
    /// and taking it as a harmonic would ask the run for a wire array the pin does not have.</summary>
    public const double AbsoluteFloorA = 1e-9;

    public const string F0Name = Prefix + "f0";
    public static string DcName(int pin) => $"{Prefix}{pin}:dc";
    public static string HarmonicName(int pin, int k) => $"{Prefix}{pin}:h{k}";
    public static string AxisName(string var) => $"{Prefix}axis:{var}";
    public static bool IsReserved(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// R-em3d79-2b — HB's phasor of harmonic k as a peak magnitude, amperes. HB's spectra are PEAK phasors (HbFft: a cosine of
    /// amplitude A has |X[1]| = A), so this is the magnitude itself; it exists so the convention is stated in one place, and
    /// a test pins it against the time waveform.
    /// </summary>
    public static double PeakOf(Complex hbPhasor) => hbPhasor.Magnitude;

    /// <summary>The link's schematic, resolved against the <c>.c3d</c>'s own folder.</summary>
    public static string SchematicPath(string c3dPath, string schematic)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, schematic));

    /// <summary>Every EM setup of <paramref name="doc"/> and the path its S-parameter result lands at, less its <c>.sNp</c>
    /// extension: the 3D run's own rule (<see cref="Em3d.Em3dRunService.SnpBasePath"/> — the solver is in the name,
    /// <c>results/&lt;setup&gt;.palace.s2p</c>), with the embedded setup named as its run names it; a setup solved by both
    /// solvers has both results (<see cref="Em3d.Em3dRunService.BothSnpBasePath"/>). brief-em3d-85: this used the planar rule,
    /// which has no solver in it, so no 3D run's result was ever found.</summary>
    public static List<(string Setup, string BasePath)> ResultPaths(C3dDocument doc, string c3dPath, string resultsRoot)
        => [.. C3dSetups.Read(doc).Where(s => s.Setup is { IsThermal: false, Is3D: true }).SelectMany(s =>
           {
               var run = C3dSetups.ForRun(s.Setup!, c3dPath);
               return run.Solver3D.IsCompare
                   ? run.Solver3D.Solvers.Select(v => (s.Name, Path.GetFullPath(Em3d.Em3dRunService.BothSnpBasePath(resultsRoot, run, v))))
                   : [(s.Name, Path.GetFullPath(Em3d.Em3dRunService.SnpBasePath(resultsRoot, run, run.Solver3D.Reference)))];
           })];

    /// <summary>Whether Touchstone <paramref name="snpPath"/> is one of <paramref name="bases"/> with its <c>.sNp</c>.</summary>
    public static bool IsResultOf(string snpPath, IEnumerable<string> bases)
    {
        if (string.IsNullOrWhiteSpace(snpPath)) return false;
        string full = Path.GetFullPath(snpPath);
        string ext = Path.GetExtension(full);
        if (!(ext.Length >= 4 && ext.StartsWith(".s", StringComparison.OrdinalIgnoreCase) && char.ToLowerInvariant(ext[^1]) == 'p'
              && ext[2..^1].All(char.IsAsciiDigit))) return false;
        string stem = full[..^ext.Length];
        return bases.Any(b => string.Equals(b, stem, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A circuit as the application reads it: a <c>.csch</c> through its extraction (once, with
    /// <see cref="SchematicCircuit.FromSchematic(string)"/>'s round trip), a <c>.cnl</c> as itself; and the files that read
    /// (brief-em3d-87: the schematic and every sub-cell the extraction descended into).</summary>
    public static (Library Lib, TestBench Tb, IReadOnlyList<string> Files)? Read(string path, out string? refusal)
    {
        refusal = null;
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".csch")
            {
                var (text, files) = SchematicCircuit.CnlTextAndFilesOf(path);
                string name = Path.GetFileNameWithoutExtension(path);
                var (lib, tb) = SchematicCircuit.RoundTrip(text, name, SchematicCircuit.ReferenceBaseOf(path));
                return (lib, tb, files);
            }
            if (ext == ".cnl")
            {
                var (lib, tb) = CnlTechnologyBinding.ReadFile(path);
                return (lib, tb, [Path.GetFullPath(path)]);
            }
            refusal = $"The thermal setup's circuit '{path}' is neither a .csch nor a .cnl.";
            return null;
        }
        catch (Exception x) when (x is not OperationCanceledException)
        {
            refusal = $"The thermal setup's circuit '{path}' could not be read: {x.Message}";
            return null;
        }
    }

    /// <summary>
    /// R-em3d79-1 — the instance of <paramref name="nl"/> whose model is this 3D view's EM result: <paramref name="named"/>, or
    /// the one there is. Null with the refusal: none, several (listing them), a named one that is not an S-parameter block, or
    /// one whose model is another file (naming it).
    /// </summary>
    public static ElaboratedComponent? Instance(ElaboratedNetlist nl, string? named, IReadOnlyList<(string Setup, string BasePath)> results,
                                                out string? refusal)
    {
        refusal = null;
        var bases = results.Select(r => r.BasePath).ToList();
        string Expected() => results.Count == 0
            ? "this 3D view has no EM setup, so it has no S-parameter result for a circuit to use"
            : "this 3D view's EM results are " + string.Join(", ", results.Select(r => $"'{r.BasePath}.sNp' (setup '{r.Setup}')"));
        var snps = nl.Components.Where(c => c.Model is SnpModel).ToList();
        if (named is not null)
        {
            var ec = nl.Components.FirstOrDefault(c => c.InstancePath == named);
            if (ec is null)
            {
                refusal = $"The circuit has no instance '{named}'" +
                          (snps.Count == 0 ? " (and no S-parameter block at all)." : $"; its S-parameter blocks are {List(snps)}.");
                return null;
            }
            if (ec.Model is not SnpModel snp)
            {
                refusal = $"Instance '{named}' is not an S-parameter block, so its model is not this 3D view's EM result.";
                return null;
            }
            if (!IsResultOf(snp.FilePath, bases))
            {
                refusal = $"Instance '{named}''s model is '{snp.FilePath}', which is not this 3D view's EM result: {Expected()}.";
                return null;
            }
            return ec;
        }
        var ours = snps.Where(c => IsResultOf(((SnpModel)c.Model).FilePath, bases)).ToList();
        if (ours.Count == 1) return ours[0];
        refusal = ours.Count == 0
            ? $"No instance of the circuit uses this 3D view's EM result: {Expected()}." +
              (snps.Count == 0 ? " The circuit has no S-parameter block." : $" Its S-parameter blocks are {List(snps)}.")
            : $"{ours.Count} instances of the circuit use this 3D view's EM result: {List(ours)}. Name one as the link's Instance.";
        return null;
    }

    private static string List(IEnumerable<ElaboratedComponent> list)
        => string.Join(", ", list.Select(c => $"'{c.InstancePath}' ('{((SnpModel)c.Model).FilePath}')"));

    /// <summary>R-em3d79-4 — what the Setups page and <c>explain</c> show of a link, with nothing run: the chains the circuit
    /// could run (HB chains and their inner HB analyses), the instances whose model is this view's result, the chain and the
    /// instance the link resolves to, and port ↔ pin ↔ net for that instance. <see cref="Problem"/> says what did not resolve.</summary>
    public sealed record Survey(string SchematicPath, IReadOnlyList<string> Analyses, IReadOnlyList<string> Instances,
                                string? Analysis, string? Instance, IReadOnlyList<(int Port, string PortName, string Net)> Mapping, string? Problem);

    /// <summary>R-em3d79-4 — <paramref name="fc"/> resolved as far as it goes without running anything.</summary>
    public static Survey Describe(CemThermalFromCircuit fc, C3dDocument doc, string c3dPath, string resultsRoot)
    {
        string path = SchematicPath(c3dPath, fc.Schematic ?? "");
        Survey Stop(string why, IReadOnlyList<string>? analyses = null, IReadOnlyList<string>? instances = null, string? analysis = null)
            => new(path, analyses ?? [], instances ?? [], analysis, null, [], why);
        if (string.IsNullOrWhiteSpace(fc.Schematic)) return Stop("Name the .csch or .cnl whose HB drives the ports.");
        if (!File.Exists(path)) return Stop($"'{fc.Schematic}' does not exist (resolved against the 3D view's folder: '{path}').");
        if (Read(path, out string? why) is not { } circuit) return Stop(why!);
        var (lib, tb, _) = circuit;
        var sel = ChainSelector.Select(tb, fc.Analysis, a => a is HarmonicBalanceAnalysis, "HB", "analysis <name> type=hb ...");
        var analyses = sel.Candidates.Select(c => c.Name)
                          .Concat(sel.Candidates.Select(c => ChainSelector.BaseOfChain(c, tb)?.Name).OfType<string>())
                          .Distinct(StringComparer.Ordinal).ToList();
        ElaboratedNetlist nl;
        try { nl = new Elaborator(lib).Elaborate(tb); }
        catch (Exception x) when (x is not OperationCanceledException) { return Stop($"The circuit does not elaborate: {x.Message}", analyses); }
        using (nl)
        {
            var results = ResultPaths(doc, c3dPath, resultsRoot);
            var bases = results.Select(r => r.BasePath).ToList();
            var instances = nl.Components.Where(c => c.Model is SnpModel m && IsResultOf(m.FilePath, bases)).Select(c => c.InstancePath).ToList();
            string? problem = sel.Selected is null ? $"The circuit runs no HB: {sel.Why}" : null;
            var inst = Instance(nl, fc.Instance, results, out string? instWhy);
            if (inst is null) return new Survey(path, analyses, instances, sel.Selected?.Name, null, [], problem ?? instWhy);
            var snp = (SnpModel)inst.Model;
            var mapping = Enumerable.Range(1, snp.PortCount).Select(k =>
                (k, doc.Ports.FirstOrDefault(q => q.Number == k) is { } port ? C3dPorts.Label(port) : "(no such port)",
                 k - 1 < inst.Nodes.Length ? nl.Nodes.NameOf(inst.Nodes[k - 1]) : "?")).ToList();
            return new Survey(path, analyses, instances, sel.Selected?.Name, inst.InstancePath, mapping, problem);
        }
    }

    /// <summary>
    /// R-em3d79-2/-3 — runs the link's HB (with <paramref name="sets"/> applied to the circuit's globals first) and reads its
    /// pin currents at every point. Null with the refusal; an HB that cannot run is that refusal, verbatim.
    /// </summary>
    public static ThermalCircuitDrive? Run(CemThermalFromCircuit fc, C3dDocument doc, string c3dPath, string resultsRoot,
                                           IReadOnlyList<(string Name, string Expr)>? sets, RunControl? control, out string? refusal)
    {
        refusal = null;
        var notes = new List<string>();
        string path = SchematicPath(c3dPath, fc.Schematic);
        if (!File.Exists(path)) { refusal = $"The thermal setup's circuit '{fc.Schematic}' does not exist (resolved against the 3D view's folder: '{path}')."; return null; }
        if (Read(path, out refusal) is not { } circuit) return null;
        var (lib, tb, circuitFiles) = circuit;
        foreach (var (name, expr) in sets ?? [])
        {
            HbCircuitRun.ApplySet(tb, name, expr);
            notes.Add($"Circuit: set {name} = {expr}.");
        }

        ElaboratedNetlist nl;
        try { nl = new Elaborator(lib).Elaborate(tb); }
        catch (Exception x) when (x is not OperationCanceledException) { refusal = $"The circuit '{fc.Schematic}' does not elaborate: {x.Message}"; return null; }
        using (nl)
        {
            var sel = ChainSelector.Select(tb, fc.Analysis, a => a is HarmonicBalanceAnalysis, "HB", "analysis <name> type=hb ...");
            if (sel.Selected is not { } top) { refusal = $"The circuit '{fc.Schematic}' runs no HB: {sel.Why}"; return null; }
            if (sel.PromotedFrom is { } from) notes.Add("Circuit: " + ChainSelector.PromotionNote(from, top));
            if (ChainSelector.BaseOfChain(top, tb) is not HarmonicBalanceAnalysis hb)
            { refusal = $"The circuit's analysis '{top.Name}' does not bottom out in an HB analysis."; return null; }
            HbAnalysisParams p;
            try { p = HbEngine.Resolve(hb, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit); }
            catch (Exception x) when (x is not OperationCanceledException) { refusal = $"The circuit's HB '{hb.Name}' does not resolve: {x.Message}"; return null; }
            if (p.IsMultiTone)
            {
                refusal = $"The circuit's HB '{hb.Name}' is multi-tone: a thermal run takes harmonics of ONE fundamental (each at N × F0), " +
                          "and a multi-tone spectrum's mixing products are not. Drive it single-tone.";
                return null;
            }

            var results = ResultPaths(doc, c3dPath, resultsRoot);
            if (Instance(nl, fc.Instance, results, out refusal) is not { } inst) return null;
            var snp = (SnpModel)inst.Model;
            // brief-sens-2: the losses are the 3D view's own structure's; offsets on its S describe one it does not hold.
            if (snp.OffsetsNotAppliedBy(inst.InstancePath, "the thermal link, whose losses are the 3D view's own structure's") is { } offsets)
            { refusal = offsets + "."; return null; }
            var missing = Enumerable.Range(1, snp.PortCount).Where(k => !doc.Ports.Any(q => q.Number == k)).ToList();
            if (missing.Count > 0)
            {
                refusal = $"Instance '{inst.InstancePath}' has {snp.PortCount} pins and this 3D view has no port {string.Join(", ", missing)}: port p is pin p.";
                return null;
            }
            var nets = Enumerable.Range(0, snp.PortCount).Select(k => k < inst.Nodes.Length ? nl.Nodes.NameOf(inst.Nodes[k]) : "?").ToArray();

            var settings = HbCircuitRun.Settings(pinCurrents: [inst.InstancePath]);
            HbCircuitSolve solved;
            try { solved = HbCircuitRun.Solve(lib, tb, nl, top, settings, Path.GetDirectoryName(path), control); }
            catch (OperationCanceledException) { throw; }
            catch (Exception x) { refusal = $"The circuit's HB '{top.Name}' could not run: {x.Message}"; return null; }
            var meas = HbCircuitRun.Measure(tb, nl, solved.ResultName, solved.Data, solved.Run, out var measErrors);
            foreach (string err in measErrors) notes.Add($"Circuit measurement: {err}");

            var drive = ReadDrive(solved.Data, meas, inst.InstancePath, snp.PortCount, nets, p.MaxHarmonic, fc.Carry, notes, out refusal);
            if (drive is null) return null;
            // brief-em3d-87 — every S-parameter file the circuit reads is an input of the thermal result (this view's EM result is one).
            var files = circuitFiles.Concat(nl.Components.Select(c => c.Model).OfType<SnpModel>().Select(m => Path.GetFullPath(m.FilePath)))
                                    .Distinct(StringComparer.Ordinal).ToList();
            return new ThermalCircuitDrive
            {
                SchematicPath = path, SnpPath = snp.FilePath, Instance = inst.InstancePath, Analysis = top.Name,
                Axes = drive.Value.Axes, Points = drive.Value.Points, Converged = drive.Value.Converged, F0 = drive.Value.F0,
                MaxHarmonic = p.MaxHarmonic, Pins = drive.Value.Pins, Carried = drive.Value.Carried, Notes = notes,
                FilesRead = files,
            };
        }
    }

    /// <summary>The HB result read as the thermal run takes it: the sweep, each point's convergence and fundamental, each
    /// pin's currents, and the carried cubes. Null with the refusal.</summary>
    private static (List<(string Var, double[] Values, string Unit)> Axes, int Points, bool[] Converged, double[] F0,
                    List<ThermalCircuitPin> Pins, List<(string, DataCube)> Carried)?
        ReadDrive(DataSet ds, DataSet? meas, string instance, int pins, string[] nets, int K, List<string>? carry, List<string> notes,
                  out string? refusal)
    {
        refusal = null;
        var conv = Find(ds, "Converged");
        var icube = Find(ds, "I");
        var tones = Find(ds, "ToneFreqs");
        if (conv is null || icube is null || tones is null)
        {
            refusal = "The circuit's HB result carries no pin currents (no I cube): the instance's currents could not be read.";
            return null;
        }
        var axes = conv.Axes.Select(a => (a.Name, (double[])a.Values.Clone(), a.Unit)).ToList();
        int points = Math.Max(1, axes.Aggregate(1, (n, a) => n * a.Item2.Length));
        var branch = icube.Axes[^2];
        int K1 = icube.Axes[^1].Length, B = branch.Length;
        var converged = conv.RealValues.Select(v => v != 0).ToArray();
        var tv = tones.RealValues;
        int nt = tones.Axes[^1].Length;
        var f0 = Enumerable.Range(0, points).Select(i => tv[i * nt]).ToArray();
        var iv = icube.ComplexValues;

        var list = new List<ThermalCircuitPin>();
        int trimmed = 0;
        for (int pin = 1; pin <= pins; pin++)
        {
            int b = Array.IndexOf(branch.Labels ?? [], $"{instance}:{pin}");
            if (b < 0) { refusal = $"The circuit's HB result carries no current for pin {pin} of '{instance}'."; return null; }
            var dc = new double[points];
            var peak = new double[points][];
            for (int i = 0; i < points; i++)
            {
                peak[i] = new double[K + 1];
                if (!converged[i]) continue;             // a point the HB did not converge at is skipped; its numbers are not currents
                double largest = 0;
                for (int k = 0; k < Math.Min(K1, K + 1); k++) largest = Math.Max(largest, PeakOf(iv[(i * B + b) * K1 + k]));
                double floor = Math.Max(RelativeFloor * largest, AbsoluteFloorA);
                double d = iv[(i * B + b) * K1].Real;
                dc[i] = Math.Abs(d) >= floor && largest > 0 ? d : 0;
                for (int k = 1; k <= K && k < K1; k++)
                {
                    double m = PeakOf(iv[(i * B + b) * K1 + k]);
                    if (m >= floor && largest > 0) peak[i][k] = m;
                    else if (m > 0) trimmed++;
                }
            }
            list.Add(new ThermalCircuitPin(pin, nets[pin - 1], dc, peak));
        }
        if (trimmed > 0)
            notes.Add($"Circuit: {trimmed} harmonic current(s) below {RelativeFloor:G1} of their pin's largest (or below {AbsoluteFloorA:G1} A) were taken as zero.");

        // the carried cubes: named ones must be on the sweep's axes; none named, every scalar measure
        bool OnAxes(DataCube c) => c.Axes.Count == axes.Count && c.Axes.Select((a, i) => a.Name == axes[i].Name && a.Length == axes[i].Item2.Length).All(x => x);
        var carried = new List<(string, DataCube)>();
        if (carry is { Count: > 0 })
            foreach (string name in carry)
            {
                var c = (meas is null ? null : Find(meas, name)) ?? Find(ds, name);
                if (c is null)
                {
                    var scalars = meas is null ? [] : meas.Cubes.Where(kv => OnAxes(kv.Value)).Select(kv => kv.Key).ToList();
                    refusal = $"The link carries '{name}', and the circuit's HB result has no cube of that name" +
                              (scalars.Count == 0 ? " (its testbench measures nothing on the sweep's axes)." : $"; its measures are {string.Join(", ", scalars)}.");
                    return null;
                }
                if (!OnAxes(c))
                {
                    refusal = $"The link carries '{name}', which is not one value per point of the HB's sweep ({string.Join(" × ", c.Axes.Select(a => a.Name))}): " +
                              "carry a scalar measure.";
                    return null;
                }
                carried.Add((name, c));
            }
        else if (meas is not null)
            carried.AddRange(meas.Cubes.Where(kv => OnAxes(kv.Value)).Select(kv => (kv.Key, kv.Value)));
        return (axes, points, converged, f0, list, carried);
    }

    private static DataCube? Find(DataSet ds, string name)
    {
        foreach (var g in ds.Groups)
            if (ds.CubesIn(g).TryGetValue(name, out var c)) return c;
        return null;
    }
}
