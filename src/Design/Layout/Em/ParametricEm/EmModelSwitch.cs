// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 R-sdl6-1/2 — what an analysis with model=em runs on. The run host asks this once per dispatched chain;
// the elaborator then only sees ordinary instances, one of which carries Model=EM.
//
// Path A (the schematic simulated by EM of its own layout): the bench's parts are replaced by ONE instance — the EM
// N-port of the schematic's own cell — between the schematic's Ports, numbered by Num. The globals, analyses and
// measurements are the bench's own, so results keep their names (R-sdl6-3) and every --set, sweep and tuned value
// reaches the geometry through the ordinary scope.
//
// Path B (EM-capable cells placed as parts): every instance that names an EM cell (EmCell=) is switched to Model=EM.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>What a model=em chain runs on, and how it was made.</summary>
/// <param name="Tb">The bench the analysis runs on.</param>
/// <param name="Lib">Its library.</param>
/// <param name="PathA">The cell simulated by EM of its own layout, or null when only placed EM cells were switched.</param>
/// <param name="Switched">The instances switched to EM (Path B).</param>
public sealed record EmSwitched(TestBench Tb, Library Lib, EmCellTarget? PathA, IReadOnlyList<string> Switched)
{
    /// <summary>brief-icp-8 — the parts put back on the EM result's component ports, each by its model from the schematic.</summary>
    public IReadOnlyList<string> Parts { get; init; } = [];

    /// <summary>brief-icp-8 — what putting them back says (a cut instance's terminal left open, a nested value written as a number).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>One line for the run's notes: what simulates the analysis.</summary>
    public string Describe(string analysis) =>
        (PathA is { } a
            ? $"'{analysis}' runs on the EM result of {a.CellName}'s layout ({a.SetupName})" +
              (Parts.Count > 0 ? $", with {string.Join(", ", Parts)} on {(Parts.Count == 1 ? "its" : "their")} component ports" : "") +
              (Switched.Count > 0 ? $", and {string.Join(", ", Switched)} by EM" : "") + "."
            : $"'{analysis}' runs with {string.Join(", ", Switched)} simulated by EM.")
        + string.Concat(Notes.Select(n => " " + n));
}

public static class EmModelSwitch
{
    /// <summary>The reference a Path A instance carries — a name no library cell has, so the elaborator takes its values
    /// from the global scope.</summary>
    public const string PathAReference = "__EMCELL__";

    /// <summary>Whether the chain topped by <paramref name="top"/> runs by EM: the run-wide switch when one is given
    /// (<c>--em on|off</c>), else any analysis of the chain stating <c>model=em</c>. A fields analysis already reads an EM
    /// result, so it asks nothing of the schematic's layout: there the request reaches only the EM-capable cells the bench
    /// places, and with none it runs by circuit models.</summary>
    public static bool IsEm(Analysis top, TestBench tb, Library lib, bool? runWide = null) =>
        (runWide ?? Chain(top, tb).Any(a => a.Model == AnalysisModel.Em))
        && (!RecombinesSavedEm(top, tb) || HasEmInstances(tb, lib));

    /// <summary>Whether the chain's base is a fields analysis. Path A cannot serve one: it replaces the bench's parts with
    /// the layout's N-port, and the EM Field Data block the analysis recombines is one of those parts.</summary>
    private static bool RecombinesSavedEm(Analysis top, TestBench tb) => Chain(top, tb)[^1] is FieldsAnalysis;

    /// <summary>Whether any instance in the bench or its cells is already set to <c>Model=EM</c> (Path B, chosen on the
    /// instance) — such a bench makes EM evaluations whatever its analyses say.</summary>
    public static bool HasInstancesSetToEm(TestBench tb, Library lib) =>
        tb.Instances.Concat(lib.Cells.SelectMany(c => c.Instances)).Any(i =>
            NamesEmCell(i) && i.Overrides.Any(o => o.Name.Equals(ParametricModelRegistry.ModelParameter, StringComparison.OrdinalIgnoreCase)
                                                 && o.Expression.Trim('"').Trim().Equals(ParametricModelRegistry.EmValue, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The <c>emSetup=</c> the chain names, innermost first; null for none.</summary>
    public static string? SetupOf(Analysis top, TestBench tb) =>
        Chain(top, tb).Reverse().Select(a => a.EmSetup).FirstOrDefault(s => s is { Length: > 0 });

    /// <summary>The chain from <paramref name="top"/> down through every enabled sweep to its base analysis.</summary>
    public static IReadOnlyList<Analysis> Chain(Analysis top, TestBench tb)
    {
        var list = new List<Analysis> { top };
        var at = top;
        while (at is ParametricSweepAnalysis sweep && AnalysisChain.ResolveEffectiveInner(sweep.InnerAnalysisName, tb) is { } inner
               && !list.Contains(inner))
        {
            list.Add(inner);
            at = inner;
        }
        return list;
    }

    /// <summary>
    /// The bench a model=em chain runs on. <paramref name="sourcePath"/> is the document the bench was read from — a
    /// <c>.csch</c> names its own cell; a <c>.cnl</c> names one only through <paramref name="emSetup"/> (its layout's
    /// cell). Null with <paramref name="refusal"/> when nothing in it can be simulated by EM, or when a part cannot be
    /// connected to the EM result. <paramref name="top"/> is the chain being run: it names the setup, and a fields chain
    /// never takes Path A (<see cref="RecombinesSavedEm"/>).
    /// </summary>
    /// <param name="wholeSchematic">True when the chain asks for EM (model=em, --em on); false when it runs by circuit
    /// models and only instances already set to EM make evaluations — then nothing is switched.</param>
    public static EmSwitched? Apply(TestBench tb, Library lib, Analysis top, string? sourcePath, string? baseDirectory,
                                    out string? refusal, bool wholeSchematic = true)
    {
        refusal = null;
        if (!wholeSchematic)
        {
            var already = tb.Instances.Where(NamesEmCell).Select(i => i.InstanceName).ToList();
            return new EmSwitched(tb, lib, null, already);
        }
        string? emSetup = SetupOf(top, tb);
        string? ownCell = RecombinesSavedEm(top, tb) ? null : OwnCell(sourcePath, emSetup, baseDirectory);
        var switchedLib = SwitchLibrary(lib, out var inCells);

        if (ownCell is not null)
        {
            var target = EmCapability.Resolve(ownCell, emSetup, baseDirectory ?? Path.GetDirectoryName(sourcePath), out var why);
            if (target is not null)
                return PathA(tb, switchedLib, target, inCells, out refusal);
            if (!HasEmInstances(tb, lib))
            {
                refusal = why;
                return null;
            }
        }
        else if (!HasEmInstances(tb, lib))
        {
            refusal = sourcePath is { } p && Path.GetExtension(p).Equals(".cnl", StringComparison.OrdinalIgnoreCase)
                ? "model=em on a netlist needs emSetup= naming the .cem whose layout it is, or an instance of an EM-capable cell."
                : "Nothing here can be simulated by EM: the schematic is not in a cell folder, and it places no EM-capable cell.";
            return null;
        }

        var bench = Copy(tb);
        bench.Instances.Clear();
        var onBench = new List<string>();
        foreach (var inst in tb.Instances) bench.Instances.Add(SwitchInstance(inst, onBench));
        return new EmSwitched(bench, switchedLib, null, [.. onBench, .. inCells]);
    }

    /// <summary>
    /// brief-sdl-10 D2 — the instances of EM cells that have no circuit model (a 3D-parametric cell with no schematic, whose
    /// definition has ports and nothing in it), as <c>X1</c> or <c>cell:X1</c>. Such a part is simulated by EM only:
    /// <c>em=correct</c> has nothing to search on, and a run by circuit models would leave it open.
    /// </summary>
    public static IReadOnlyList<string> WithoutCircuitModel(TestBench tb, Library lib)
    {
        bool Empty(Instance i) => NamesEmCell(i) && lib.Find(i.Reference) is { Instances.Count: 0 };
        return tb.Instances.Where(Empty).Select(i => i.InstanceName)
            .Concat(lib.Cells.SelectMany(c => c.Instances.Where(Empty).Select(i => $"{c.Name}:{i.InstanceName}")))
            .ToList();
    }

    /// <summary>The refusal of a run by circuit models of <paramref name="names"/> (<see cref="WithoutCircuitModel"/>).</summary>
    public static string NoCircuitModelSentence(IReadOnlyList<string> names) =>
        $"{string.Join(", ", names)} {(names.Count == 1 ? "has" : "have")} no circuit model (a 3D-parametric cell with no " +
        "schematic), so it cannot be run by circuit models; it is simulated by EM only.";

    /// <summary><c>--em off</c>: every instance back on its circuit model, whatever it states.</summary>
    public static (TestBench Tb, Library Lib) CircuitOnly(TestBench tb, Library lib)
    {
        var bench = Copy(tb);
        bench.Instances.Clear();
        foreach (var inst in tb.Instances) bench.Instances.Add(WithModel(inst, ParametricModelRegistry.CircuitValue));
        var l = new Library(lib.Name);
        foreach (var c in lib.Cells)
        {
            var copy = c.CopyShallow(c.Name);
            copy.Instances.Clear();
            copy.Instances.AddRange(c.Instances.Select(i => WithModel(i, ParametricModelRegistry.CircuitValue)));
            l.Cells.Add(copy);
        }
        return (bench, l);
    }

    // ── Path A ─────────────────────────────────────────────────────────────────

    private static EmSwitched? PathA(TestBench tb, Library lib, EmCellTarget target, IReadOnlyList<string> inCells, out string? refusal)
    {
        refusal = null;
        SchematicEditModel model;
        try { model = SchematicPersistence.LoadFromFile(target.SchematicPath).model; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            refusal = $"{target.CellName}'s schematic could not be read ({e.Message}).";
            return null;
        }

        // R-sdl6-2: what each part is to the EM result. brief-icp-8 — a part that is not copper is put back on a component port.
        var noLayout = new List<string>();
        string? schematicDir = Path.GetDirectoryName(target.SchematicPath);
        // brief-sdl-10 D1 — a 3D-parametric cell's schematic is a circuit model only: none of its parts is in the EM view's
        // geometry, so none is asked to be copper.
        foreach (var c in target.ThreeDOnly ? Enumerable.Empty<EditableComponent>() : model.Components)
        {
            if (!SchematicLayoutPlanner.IsPhysical(c) || PortPlacement.IsPort(c) || c.InstanceName.Length == 0) continue;
            switch (EmPartKinds.Of(c, schematicDir))
            {
                case EmPartKind.Copper:
                case EmPartKind.Part:     break;
                case EmPartKind.NoLayout: noLayout.Add(Label(c)); break;
            }
        }
        if (noLayout.Count > 0)
        {
            refusal = $"{Join(noLayout)} {(noLayout.Count == 1 ? "has" : "have")} no layout, so {(noLayout.Count == 1 ? "it" : "they")} " +
                      $"cannot be connected to the EM result of {target.CellName}'s layout. Give each a footprint, a line " +
                      "model or a layout view, or run this analysis by circuit models.";
            return null;
        }

        // The EM ports are the schematic's Ports, numbered by Num (SDL-5); the N-port's terminal k is Port k's net.
        var byNum = new SortedDictionary<int, string>();
        var bench = Copy(tb);
        bench.Instances.Clear();
        foreach (var inst in tb.Instances)
        {
            if (!IsPortInstance(inst)) continue;
            string numText = inst.Overrides.FirstOrDefault(o => o.Name.Equals("Num", StringComparison.OrdinalIgnoreCase))?.Expression ?? "";
            if (!int.TryParse(numText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
            {
                refusal = $"Port '{inst.InstanceName}' states its number as '{numText}'; an EM port needs a plain Num.";
                return null;
            }
            string reference = inst.NetBindings.Count > 1 ? inst.NetBindings[1] : inst.RefNetBinding ?? "0";
            if (reference != "0")
            {
                refusal = $"Port '{inst.InstanceName}' is referenced to net '{reference}', not ground; the EM result's ports are " +
                          "referenced to its ground plane.";
                return null;
            }
            if (!byNum.TryAdd(num, inst.NetBindings[0]))
            {
                refusal = $"Two Ports are numbered {num}.";
                return null;
            }
            bench.Instances.Add(inst);
        }
        if (byNum.Count == 0)
        {
            refusal = $"'{target.CellName}' has no Ports, so its EM result has no terminals to drive.";
            return null;
        }
        if (byNum.Keys.First() != 1 || byNum.Keys.Last() != byNum.Count)
        {
            refusal = $"The Ports are numbered {string.Join(", ", byNum.Keys)}; an EM result's ports run 1 to N with none missing.";
            return null;
        }

        // brief-icp-8 — with component ports, the N-port has a terminal per result port and the parts close the ones that are not
        // the schematic's Ports. A 3D-parametric cell's schematic is a circuit model only, and its parts are in no EM view.
        IReadOnlyList<string> terminals = [.. byNum.Values];
        var parts = new List<string>();
        var notes = new List<string>();
        if (!target.ThreeDOnly && target.RunSetup.ComponentPorts.Count > 0)
        {
            if (PartsOnPorts(tb, lib, target, model, byNum, out refusal) is not { } closed) return null;
            terminals = closed.Terminals;
            foreach (var part in closed.Parts)
            {
                bench.Instances.Add(part);
                parts.Add(part.InstanceName);
            }
            notes.AddRange(closed.Notes);
        }

        bench.Instances.Add(new Instance("EM_" + ResultsNameOf(target.CellName), PathAReference, terminals,
        [
            new ParameterAssignment(ParametricModelRegistry.ModelParameter, ParametricModelRegistry.EmValue),
            new ParameterAssignment(ParametricModelRegistry.CellParameter, Quote(target.CellDir)),
            new ParameterAssignment(ParametricModelRegistry.SetupParameter, Quote(target.CemPath)),
        ]));
        return new EmSwitched(bench, lib, target, inCells) { Parts = parts, Notes = notes };
    }

    /// <summary>
    /// brief-icp-8 R-icp8-2 — the parts of <paramref name="tb"/> put back on the EM result's component ports, and the N-port's
    /// terminals: the result's ports planned at the schematic's own values (<see cref="EmEvaluation.ResultPorts"/>, the geometry
    /// every point builds) and closed by <see cref="EmCellParts.Close"/> with the bench's own instances, so their overrides
    /// evaluate in this run's scope at every point.
    /// </summary>
    private static (List<Instance> Parts, List<string> Terminals, List<string> Notes)? PartsOnPorts(
        TestBench tb, Library lib, EmCellTarget target, SchematicEditModel model, IReadOnlyDictionary<int, string> byNum, out string? refusal)
    {
        var g = EmEvaluation.Geometry(target, new Dictionary<string, CircuitRF.Core.Expressions.Value>(), out refusal);
        if (g is null) return null;
        if (EmEvaluation.ResultPorts(g) is not { Count: > 0 } ports)
        {
            refusal = $"The ports of the EM result of {target.CellName}'s layout ({target.SetupName}) cannot be found before it is " +
                      "solved, so its parts cannot be put back on them. Run its EM setup on its own to see why.";
            return null;
        }
        var byName = new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in tb.Instances)
            if (inst.InstanceName is { Length: > 0 } n) byName.TryAdd(n, inst);
        var models = new EmCoSimModels(byName, lib, tb.Technology)
        {
            Variables = [.. tb.GlobalVariables],
            PinNames = EmCoSimNetlist.PinNamesOf(model, target.SchematicPath),
        };
        return EmCellParts.Close(ports, byNum, models, target.CellName, out refusal);
    }

    private static string Label(EditableComponent c) => $"{c.InstanceName} ({ComponentTypeRegistry.DisplayName(c.Symbol, c.PortCount)})";

    private static string Join(List<string> names) => names.Count switch
    {
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static bool IsPortInstance(Instance inst) =>
        inst.Reference.Equals("Port", StringComparison.OrdinalIgnoreCase) || inst.Reference.Equals("Term", StringComparison.OrdinalIgnoreCase);

    // ── Path B ─────────────────────────────────────────────────────────────────

    private static bool HasEmInstances(TestBench tb, Library lib) =>
        tb.Instances.Concat(lib.Cells.SelectMany(c => c.Instances)).Any(NamesEmCell);

    private static bool NamesEmCell(Instance i) =>
        i.Overrides.Any(o => o.Name.Equals(ParametricModelRegistry.CellParameter, StringComparison.OrdinalIgnoreCase));

    private static Library SwitchLibrary(Library lib, out List<string> switched)
    {
        switched = [];
        var l = new Library(lib.Name);
        foreach (var c in lib.Cells)
        {
            if (!c.Instances.Any(NamesEmCell)) { l.Cells.Add(c); continue; }
            var copy = c.CopyShallow(c.Name);
            copy.Instances.Clear();
            var names = new List<string>();
            copy.Instances.AddRange(c.Instances.Select(i => SwitchInstance(i, names)));
            switched.AddRange(names.Select(n => $"{c.Name}:{n}"));
            l.Cells.Add(copy);
        }
        return l;
    }

    private static Instance SwitchInstance(Instance inst, List<string> switched)
    {
        if (!NamesEmCell(inst)) return inst;
        switched.Add(inst.InstanceName);
        return WithModel(inst, ParametricModelRegistry.EmValue);
    }

    private static Instance WithModel(Instance inst, string model)
    {
        if (!NamesEmCell(inst)) return inst;
        var overrides = inst.Overrides
            .Where(o => !o.Name.Equals(ParametricModelRegistry.ModelParameter, StringComparison.OrdinalIgnoreCase))
            .Append(new ParameterAssignment(ParametricModelRegistry.ModelParameter, model)).ToList();
        return new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, overrides) { RefNetBinding = inst.RefNetBinding };
    }

    // ── Shared ─────────────────────────────────────────────────────────────────

    private static string? OwnCell(string? sourcePath, string? emSetup, string? baseDirectory)
    {
        if (sourcePath is not { Length: > 0 }) return null;
        if (Path.GetExtension(sourcePath).Equals(".csch", StringComparison.OrdinalIgnoreCase))
            return EmCapability.CellDirOfSchematic(sourcePath);
        if (emSetup is not { Length: > 0 }) return null;
        string dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        // brief-sdl-8 D1 — <cell>/3d/<view>.c3d#<setup>: the cell is the 3D view's folder's parent. A '#' in a folder name or
        // in a .cem's own name is part of that path (EmCapability.SplitSetup), so such a .cem is looked for below.
        if (EmCapability.SplitSetup(emSetup, out string c3d, out _, out bool ambiguous) && c3d.Length > 0)
        {
            if (!c3d.EndsWith(CircuitRF.Design.ThreeD.C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase)) c3d += CircuitRF.Design.ThreeD.C3dPersistence.Extension;
            foreach (var root in new[] { dir, baseDirectory }.Where(r => r is { Length: > 0 }).Cast<string>())
            {
                string full = Path.IsPathRooted(c3d) ? c3d : Path.Combine(root, c3d);
                if (File.Exists(full)) return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(full)));
            }
            if (!ambiguous) return null;
        }
        foreach (var root in new[] { dir, baseDirectory }.Where(r => r is { Length: > 0 }).Cast<string>())
        {
            string cem = Path.IsPathRooted(emSetup) ? emSetup : Path.Combine(root, emSetup);
            if (!cem.EndsWith(EmSetupPersistence.Extension, StringComparison.OrdinalIgnoreCase)) cem += EmSetupPersistence.Extension;
            if (File.Exists(cem)) return EmCapability.CellDirOfSetup(cem);
        }
        return null;
    }

    private static TestBench Copy(TestBench tb)
    {
        var c = new TestBench(tb.Name) { Tuning = tb.Tuning, Technology = tb.Technology, StatisticalDraws = tb.StatisticalDraws };
        c.Instances.AddRange(tb.Instances);
        c.GlobalVariables.AddRange(tb.GlobalVariables);
        c.Functions.AddRange(tb.Functions);
        c.Analyses.AddRange(tb.Analyses);
        c.Measurements.AddRange(tb.Measurements);
        c.RawDirectives.AddRange(tb.RawDirectives);
        c.ReadWarnings.AddRange(tb.ReadWarnings);
        c.ReadNotes.AddRange(tb.ReadNotes);
        c.LabeledNets.UnionWith(tb.LabeledNets);
        return c;
    }

    private static string Quote(string s) => "\"" + s + "\"";

    private static string ResultsNameOf(string cell) =>
        new([.. cell.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')]);
}
