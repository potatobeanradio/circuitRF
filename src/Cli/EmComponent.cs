using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf em [&lt;workspace | .cws | .ctech&gt;] --component "SPIRAL N=3 W=10 um S=8 um Din=100 um"
/// -o L1.s2p [--freq start:stop:step]</c> — one built-in component's DRAWN part, EM-extracted to a
/// Touchstone whose port <i>n</i> is the component's terminal <i>n</i> (brief-agent-authoring-overview.md
/// AA-1: the "extract this spiral" half of the spiral's hybrid model).
///
/// <para><b>It owns no EM logic, no artwork and no parsing of its own.</b> The component line is read
/// by the <c>.cnl</c> reader and elaborated by the elaborator, so <c>W=10 um</c> means here exactly what
/// it means on an instance line; the artwork is the component's own built-in PCell; the ports, the
/// setup and the run are <see cref="ComponentEmExtraction"/> and <see cref="EmRunService.Run"/>; and
/// the report is the one every <c>em</c> run prints. A mode of <c>em</c> rather than a verb of its own,
/// because what it produces is exactly what <c>em</c> produces.</para>
///
/// <para><b>The technology is a WALK-UP, as everywhere else</b>: the nearest workspace above the path
/// given (or the current directory) and its default technology — the one a schematic there would
/// simulate the part on — or a <c>.ctech</c> named directly. No technology is a refusal: there is no
/// stackup to draw on.</para>
/// </summary>
internal static class EmComponent
{
    /// <param name="force">`--force`: a run past the memory it is sized on starts rather than being declined —
    /// the planar run's own question, on the same terms as a <c>.cem</c>'s.</param>
    /// <param name="memoryLimitBytes">`--memory-limit`: the memory the planar run is sized on in place of this
    /// machine's physical memory, exactly as for a <c>.cem</c>.</param>
    public static int Run(string componentLine, string? path, string? output, string? freq,
                          Func<EmRunResult, EmSetup, string, int> report, CircuitRF.Engine.RunControl? control,
                          bool force = false, long? memoryLimitBytes = null)
    {
        if (output is null)
            return JsonRun.Fail(CliDiagnostics.EmComponentRefused(
                "-o out.sNp is required: there is no setup to name the result after."));

        // ── the component line, read as an instance line ──────────────────────────────────────
        var tokens = componentLine.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !ComponentTypeRegistry.TryParseCode(tokens[0], out var kind, out int portCount))
            return JsonRun.Fail(CliDiagnostics.EmComponentRefused(
                $"'{(tokens.Length > 0 ? tokens[0] : "")}' is not a component type. The line is a type followed by its " +
                "parameters, as on a .cnl instance line: \"SPIRAL N=3 W=10 um\"."));
        string token = ComponentTypeRegistry.EngineReference(kind, portCount);
        if (!PCellRegistry.KnownGeneratorIds.Contains(token, StringComparer.OrdinalIgnoreCase))
            return JsonRun.Fail(CliDiagnostics.EmComponentRefused(
                $"{token} has no built-in layout generator, so there is no drawn part to extract. Components with one: " +
                string.Join(", ", PCellRegistry.KnownGeneratorIds.Order(StringComparer.Ordinal)) + "."));

        // ── the technology ─────────────────────────────────────────────────────────────────────
        string start = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        if (path is not null && !File.Exists(start) && !Directory.Exists(start))
            return JsonRun.Fail(CliDiagnostics.FileNotFound(start));
        Technology? tech;
        string? techPath;
        string dir;
        if (string.Equals(Path.GetExtension(start), ".ctech", StringComparison.OrdinalIgnoreCase))
        {
            techPath = start;
            dir = Path.GetDirectoryName(start)!;
            try { tech = TechPersistence.LoadFromFile(start); }
            catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.SetupUnreadable(start, ex.Message)); }
        }
        else
        {
            dir = Directory.Exists(start) ? start : Path.GetDirectoryName(start)!;
            techPath = MicrostripSubstrateInjection.ResolveWorkspaceTechnologyPath(dir);
            tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(dir);
        }
        if (tech is null)
            return JsonRun.Fail(CliDiagnostics.EmComponentRefused(
                $"no technology resolves from '{dir}' — it is not inside a workspace, or the workspace names no default " +
                "technology. Give a workspace folder, its .cws, or a .ctech."));
        Console.Error.WriteLine($"[circuitRF] technology: {techPath}");

        // ── the parameters, through the reader and the elaborator ──────────────────────────────
        IReadOnlyDictionary<string, PCellValue> parameters;
        try { parameters = Elaborated(kind, token, tokens.Length > 1 ? tokens[1] : "", dir); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.EmComponentRefused(ex.Message)); }

        // ── the sweep ──────────────────────────────────────────────────────────────────────────
        var spec = new EmSetup().Frequency;
        if (freq is not null)
        {
            var parts = freq.Split(':');
            if (parts.Length != 3 || !TryHz(parts[0], out double f0) || !TryHz(parts[1], out double f1) || !TryHz(parts[2], out double df)
                || !(f0 > 0) || !(f1 >= f0) || !(df > 0))
                return JsonRun.Fail(CliDiagnostics.EmComponentRefused(
                    $"--freq '{freq}' is not start:stop:step with 0 < start <= stop and a positive step (e.g. 1GHz:20GHz:1GHz)."));
            spec = new FrequencySpec(R(f0), R(f1), R(df));
        }
        else
            Console.Error.WriteLine("[circuitRF] no --freq: sweeping 1-20 GHz, the default of a new EM setup");

        // ── the problem, and the run every em run is ───────────────────────────────────────────
        string outFull = Path.GetFullPath(output);
        string? outDir = Path.GetDirectoryName(outFull);
        if (outDir is { Length: > 0 }) Directory.CreateDirectory(outDir);
        string layoutName = Path.Combine(dir, Path.GetFileNameWithoutExtension(outFull) + ".clay");

        var prepared = ComponentEmExtraction.Prepare(token, parameters, tech, spec, layoutName, out string? refusal);
        if (prepared is null) return JsonRun.Fail(CliDiagnostics.EmComponentRefused(refusal!));
        foreach (var note in prepared.Notes)
        {
            Console.Error.WriteLine($"note: {token}: {note}");
            JsonRun.Note(CliDiagnostics.EmSetupWarning($"{token}: {note}"));
        }

        EmRunResult result;
        try
        {
            // The .npy goes where every em run's goes — the workspace's results folder — and -o moves the
            // Touchstone only, as it does for a .cem.
            string resultsRoot = ResultsRoot.For(layoutName, CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(dir));
            result = EmRunService.Run(prepared.Setup, prepared.Source, resultsRoot, RunHost.Cancellation, control,
                                      confirmMemory: force ? _ => true : null, memoryCeilingBytes: memoryLimitBytes,
                                      target: new EmRunTarget { TouchstoneDestination = outFull });
        }
        catch (Exception ex)
        {
            return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message));
        }
        return report(result, prepared.Setup, layoutName);
    }

    /// <summary>
    /// The component line's parameters in SI, as the PCell receives them — read as a one-instance
    /// <c>.cnl</c> and elaborated, so units, expressions and defaults behave exactly as they do on an
    /// instance line. Nets are placeholders: the line names none, and the generator draws its own pins.
    /// </summary>
    private static IReadOnlyDictionary<string, PCellValue> Elaborated(SymbolKind kind, string token, string rest, string dir)
    {
        int nets = SymbolPortDefs.For(kind).Length;
        string netList = string.Join(' ', Enumerable.Range(1, Math.Max(nets, 1)).Select(i => $"n{i}"));
        var (lib, tb) = new CnlReader().Read($"{token}:X1 {netList} {rest}\n", "tb", dir);
        if (tb.ReadWarnings.Count > 0) throw new InvalidOperationException(string.Join(" ", tb.ReadWarnings));
        var netlist = new Elaborator(lib).Elaborate(tb);
        var comp = netlist.Components.FirstOrDefault()
                   ?? throw new InvalidOperationException($"the line elaborated to no component: \"{token} {rest}\".");

        var values = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
        foreach (var (name, value) in comp.Parameters)
        {
            if (value.Kind == CircuitRF.Core.Expressions.ValueKind.Real) values[name] = PCellValue.Real(value.AsReal());
            else if (value.Kind == CircuitRF.Core.Expressions.ValueKind.String) values[name] = PCellValue.Text(value.AsString());
        }
        return values;
    }

    // Invariant, as the run verbs' --freq is: a command line means the same thing in every locale.
    private static bool TryHz(string s, out double hz)
    {
        s = s.Trim();
        double scale = 1;
        foreach (var (suffix, k) in new[] { ("GHz", 1e9), ("MHz", 1e6), ("kHz", 1e3), ("Hz", 1.0) })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { s = s[..^suffix.Length]; scale = k; break; }
        bool ok = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        hz = v * scale;
        return ok;
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
