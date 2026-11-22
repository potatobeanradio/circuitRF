using System.Globalization;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Design.Cells;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf netlist &lt;path&gt; --flatten [-o out.cnl]</c> — the netlist with every top-level <c>Match</c>
/// line replaced by the L and C elements its ladder stamps: the Match Designer's Flatten to Cell, headless.
/// </summary>
/// <remarks>
/// <b>Why.</b> A Match synthesises its ladder at elaboration and nothing on the automation surface showed
/// it, so an agent could design a matching network and simulate it, and could not say what parts to buy.
/// <b>It owns no flattening:</b> the ladder comes from the elaborated <see cref="MatchModel"/> and its
/// nets from <see cref="MatchFlattenPlan"/>, the walk the GUI's Flatten writes its cell from, so the
/// flattened netlist simulates exactly as the Match did. Every other line is copied as written. A Match
/// inside a cell definition is left in place and named, since its cell's lines are the cell's own.
/// </remarks>
internal static class NetlistFlatten
{
    public static int Run(string path, DocumentKind kind, string? csch, string? output)
    {
        string text;
        try { text = kind == DocumentKind.Netlist ? File.ReadAllText(path) : CircuitSource.CnlTextOf(csch!); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistExtractionFailed(path, ex.Message)); }

        var ladders = new Dictionary<string, MatchModel>(StringComparer.OrdinalIgnoreCase);
        var nested = new List<string>();
        try
        {
            var (lib, tb) = new Core.Netlist.CnlReader().Read(text);
            using var nl = new Elaborator(lib) { BaseDirectory = CircuitSource.ReferenceBaseOf(path) }.Elaborate(tb);
            foreach (var c in nl.Components)
            {
                if (c.Model is not MatchModel m) continue;
                if (c.InstancePath.Contains('.')) nested.Add(c.InstancePath);
                else ladders[c.InstancePath] = m;
            }
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistExtractionFailed(path, ex.Message)); }

        if (ladders.Count == 0 && nested.Count == 0)
            return JsonRun.Fail(CliDiagnostics.NetlistFlattenNoMatch(path));
        foreach (string n in nested)
            JsonRun.Report(CliDiagnostics.NetlistFlattenNested(n));

        var sb = new StringBuilder();
        int flattened = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string[] tok = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length >= 3 && tok[0].StartsWith("Match:", StringComparison.OrdinalIgnoreCase)
                && ladders.TryGetValue(tok[0]["Match:".Length..], out var m))
            {
                string name = tok[0]["Match:".Length..];
                sb.Append("; ").Append(name).Append(" flattened from: ").AppendLine(line.Trim());
                AppendLadder(sb, name, tok[1], tok[2], m);
                flattened++;
                continue;
            }
            sb.Append(line).Append('\n');
        }
        string result = sb.ToString();
        if (!text.EndsWith('\n') && result.EndsWith('\n')) result = result[..^1];

        if (output is null)
        {
            JsonRun.Document = new RfCore.Export.DocumentJson(path, DocumentKinds.Name(DocumentKind.Netlist), result);
            Console.Out.Write(result);
            Console.Error.WriteLine($"[circuitRF] flattened {flattened} Match line(s) in {Path.GetFileName(path)}");
            return 0;
        }
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllText(output, result, new UTF8Encoding(false));
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistWriteFailed(output, ex.Message)); }
        JsonRun.AddOutput("cnl", output);
        Console.WriteLine(output);
        Console.Error.WriteLine($"[circuitRF] flattened {flattened} Match line(s) -> {output}");
        return 0;
    }

    /// <summary>One L or C line per stamped element, on the instance's own two nets and nets named after it.</summary>
    private static void AppendLadder(StringBuilder sb, string name, string net1, string net2, MatchModel m)
    {
        var plan = MatchFlattenPlan.Build(m.Network);
        string Net(string n) => n == plan.Port1Net ? net1
                              : n == plan.Port2Net ? net2
                              : n == MatchFlattenPlan.GroundNet ? "0"
                              : $"{name}_{n}";
        foreach (var fe in plan.Elements)
        {
            bool l = fe.Element.Type == ElementType.L;
            double v = fe.Element.Value / (l ? 1e-9 : 1e-12);
            sb.Append(l ? "L:" : "C:").Append(name).Append('_').Append(fe.Element.Name).Append(' ')
              .Append(Net(fe.NetA)).Append(' ').Append(Net(fe.NetB)).Append(' ')
              .Append(l ? "L=" : "C=").Append(v.ToString("G9", CultureInfo.InvariantCulture))
              .Append(l ? " nH" : " pF").Append('\n');
        }
    }
}
