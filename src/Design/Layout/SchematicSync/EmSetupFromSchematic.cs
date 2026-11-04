using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Mom;
using CircuitRF.Design.Schematic;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-5 R-sdl5-2b: Update Layout from Schematic's <b>Create or update EM setup</b> option. The schematic's Ports
/// say each port's number and reference impedance (<c>Num</c>, <c>Z</c>); the <c>.cem</c> is where a reference
/// impedance lives (<see cref="EmSetup.PortZ0s"/>, D5), so this writes them there.
///
/// <list type="bullet">
///   <item><b>No setup references the layout:</b> one is created through <see cref="EmSetupCreate"/>, named after the
///   cell, a planar analysis, with each Port's <c>Z</c> and the schematic's first S-parameter sweep (else the
///   setup's own default sweep).</item>
///   <item><b>Setups reference it:</b> each gains an impedance for a port number it has none for, and reports a number
///   it was given that the schematic no longer has. A changed <c>Z</c> is written only while the setup still holds
///   the value this command wrote last (<see cref="EmSetup.SchematicPortZ0s"/>); otherwise the setup's value is kept
///   and reported. Nothing is deleted, and no other setting — the frequency plan included — is touched.</item>
/// </list>
///
/// <para>It writes files, so it is not part of the layout's undoable plan: the caller runs it after the layout
/// command, and only when the option is on.</para>
/// </summary>
public static class EmSetupFromSchematic
{
    /// <summary>One schematic Port as the setup sees it.</summary>
    public sealed record Port(string Id, int Number, Complex Z0);

    /// <summary>The setups written (absolute paths) and the report lines.</summary>
    public sealed record Outcome(IReadOnlyList<string> Written, IReadOnlyList<ReportLine> Lines);

    /// <summary>
    /// Brings the setups of the layout at <paramref name="clayPath"/> up to date with <paramref name="model"/>'s Ports,
    /// or creates one when the workspace at <paramref name="workspaceCwsPath"/> has none for it.
    /// <paramref name="cellName"/> names a created setup.
    /// </summary>
    public static Outcome Run(SchematicEditModel model, string workspaceCwsPath, string clayPath, string cellName)
    {
        var lines = new List<ReportLine>();
        var extraction = NetExtractor.Extract(model, "tb", null);
        var ports = PortsOf(model, extraction, lines);
        string workspaceDir = Path.GetDirectoryName(Path.GetFullPath(workspaceCwsPath))!;
        var written = new List<string>();

        var existing = EmSetupResolver.FindSetupsForLayout(workspaceCwsPath, clayPath);
        if (existing.Count == 0)
        {
            if (ports.Count == 0)
            {
                lines.Add(Info("", "No EM setup was created: the schematic has no numbered Port"));
                return new Outcome(written, lines);
            }
            var frequency = FrequencyOf(extraction, out string? frequencyNote);
            var created = EmSetupCreate.Create(workspaceDir, EmSetupCreate.NextFreeName(workspaceDir, cellName),
                EmSetupCreate.LayoutRefOf(workspaceDir, clayPath), setup =>
                {
                    setup.AnalysisKind = EmAnalysisKind.Planar;
                    if (frequency is not null) setup.Frequency = frequency;
                    foreach (var port in ports) Write(setup, port);
                });
            if (created.Refusal is { } refusal)
            {
                lines.Add(new ReportLine("", $"No EM setup was created: {refusal}", ReportSeverity.Warning));
                return new Outcome(written, lines);
            }
            written.Add(created.Path!);
            string rel = EmSetupCreate.LayoutRefOf(workspaceDir, created.Path!);
            lines.Add(Info("", $"Created EM setup {rel}: ports {NumberList(ports.Select(p => p.Number))}, " +
                               (frequency is not null ? "the schematic's S-parameter sweep" : "the default sweep")));
            if (frequencyNote is not null) lines.Add(Info("", $"{rel}: {frequencyNote}"));
            return new Outcome(written, lines);
        }

        foreach (string path in existing)
        {
            string rel = EmSetupCreate.LayoutRefOf(workspaceDir, path);
            EmSetup setup;
            try { setup = EmSetupPersistence.LoadFromFile(path); }
            catch (Exception ex)
            {
                lines.Add(new ReportLine("", $"{rel} was not updated: it could not be read ({ex.Message})", ReportSeverity.Warning));
                continue;
            }
            if (Update(setup, ports, rel, lines))
            {
                EmSetupPersistence.SaveToFile(path, setup);
                written.Add(path);
            }
        }
        return new Outcome(written, lines);
    }

    /// <summary>R-sdl5-2b's rules on one setup, in place. True when anything changed.</summary>
    public static bool Update(EmSetup setup, IReadOnlyList<Port> ports, string name, List<ReportLine> lines)
    {
        bool changed = false;
        foreach (var port in ports)
        {
            int slot = port.Number - 1;
            if (slot >= setup.PortZ0s.Count)
            {
                // A port the setup has no impedance for: added, the slots before it filled with what they resolve to
                // already, so no other port's impedance changes.
                Write(setup, port);
                lines.Add(Info(port.Id, $"{name}: port {port.Number} added at {Ohms(port.Z0)}"));
                changed = true;
                continue;
            }
            var held = setup.PortZ0s[slot];
            if (held == port.Z0) continue;
            if (setup.SchematicPortZ0s.TryGetValue(port.Number, out var wrote) && wrote == held)
            {
                Write(setup, port);
                lines.Add(Info(port.Id, $"{name}: port {port.Number} {Ohms(held)} → {Ohms(port.Z0)}"));
                changed = true;
            }
            else
                lines.Add(Info(port.Id, $"{name}: port {port.Number} is {Ohms(held)} in the setup; the schematic says " +
                                        $"{Ohms(port.Z0)}. The setup's value is kept"));
        }

        var now = ports.Select(p => p.Number).ToHashSet();
        foreach (int gone in setup.SchematicPortZ0s.Keys.Where(n => !now.Contains(n)))
            lines.Add(Info("", $"{name}: port {gone} is no longer in the schematic; its settings are kept"));
        return changed;
    }

    /// <summary>The schematic's numbered Ports, in number order, each with its <c>Z</c>. A Port whose number or
    /// impedance cannot be read is reported and left out; a number used twice keeps its first Port.</summary>
    public static IReadOnlyList<Port> PortsOf(SchematicEditModel model, NetExtractor.ExtractionResult extraction,
                                              List<ReportLine> lines)
    {
        var scope = new Scope("global");
        foreach (var v in extraction.TestBench.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        var evaluator = new Evaluator();
        var ports = new SortedDictionary<int, Port>();
        foreach (var comp in model.Components.Where(c => PortPlacement.IsPort(c) && c.InstanceName is { Length: > 0 }
                                                         && c.Disable is not (DisableState.Open or DisableState.Short))
                                             .OrderBy(c => c.InstanceName, Comparer<string>.Create(JointGraph.CompareIds)))
        {
            string id = comp.InstanceName;
            if (!EmPortExtraction.TryParseNumber(PortPlacement.NumberText(comp), out int number) || number < 1)
            {
                lines.Add(Info(id, $"{id} has no port number, so the EM setup has no impedance for it"));
                continue;
            }
            var z = comp.Parameters.FirstOrDefault(p => p.Name == "Z");
            Complex z0 = new(50, 0);
            if (z is { Expression: { Length: > 0 } expr })
            {
                string unit = UnitNormalizer.ToEngineUnit(z.Unit);
                try
                {
                    var v = evaluator.Eval(expr, scope, unit.Length > 0 ? unit : null);
                    z0 = v.Kind == ValueKind.Complex ? v.AsComplex() : new Complex(v.AsReal(), 0);
                }
                catch (Exception ex)
                {
                    lines.Add(new ReportLine(id, $"{id}: Z could not be read ({ex.Message}); the EM setup takes 50 Ω for port {number}",
                                             ReportSeverity.Warning));
                }
            }
            if (!ports.TryAdd(number, new Port(id, number, z0)))
                lines.Add(new ReportLine(id, $"{id} is port {number}, which {ports[number].Id} already is; the EM setup takes " +
                                             $"{ports[number].Id}'s impedance", ReportSeverity.Warning));
        }
        return [.. ports.Values];
    }

    /// <summary>The schematic's first S-parameter sweep as a setup's frequency plan — its first segment, when it is
    /// written in numbers a <c>.cem</c> can hold (a sweep written in the schematic's variables is not), else null with
    /// why.</summary>
    public static FrequencySpec? FrequencyOf(NetExtractor.ExtractionResult extraction, out string? note)
    {
        note = null;
        if (extraction.TestBench.Analyses.OfType<SParameterAnalysis>().FirstOrDefault() is not { } sp) return null;
        var f = sp.Freq;
        bool Literal(string e) => e.Length == 0 || double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (!Literal(f.StartExpr) || !Literal(f.StopExpr) || !Literal(f.StepExpr))
        {
            note = $"{sp.Name}'s sweep is written in the schematic's variables, so the setup keeps its default sweep";
            return null;
        }
        if (sp.Sweeps.Count > 1) note = $"{sp.Name} has {sp.Sweeps.Count} segments; the setup takes the first";
        return f;
    }

    /// <summary>Sets port <paramref name="port"/>'s impedance and records it as written by this command.</summary>
    private static void Write(EmSetup setup, Port port)
    {
        int slot = port.Number - 1;
        while (setup.PortZ0s.Count <= slot) setup.PortZ0s.Add(setup.ResolvePortZ0(setup.PortZ0s.Count));
        setup.PortZ0s[slot] = port.Z0;
        setup.SchematicPortZ0s[port.Number] = port.Z0;
    }

    private static ReportLine Info(string id, string text) => new(id, text, ReportSeverity.Info);

    private static string Ohms(Complex z) =>
        (z.Imaginary == 0 ? z.Real.ToString("0.###", CultureInfo.InvariantCulture)
                          : $"{z.Real.ToString("0.###", CultureInfo.InvariantCulture)}{(z.Imaginary < 0 ? "−" : "+")}j" +
                            Math.Abs(z.Imaginary).ToString("0.###", CultureInfo.InvariantCulture)) + " Ω";

    private static string NumberList(IEnumerable<int> numbers) => string.Join(", ", numbers);
}
