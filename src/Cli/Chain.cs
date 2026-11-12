using System.Globalization;
using CircuitRF.Design.Chain;
using CircuitRF.Render.DataDisplay;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf chain &lt;path.cchain&gt; [-o budget.{npy,mat,txt}] [--to-schematic [dir]] [--name N] [--replace] [--demo]</c> —
/// the RF Chain Budget window, headless.
///
/// <para><b>It owns no arithmetic and no drawing.</b> The budget is <see cref="ChainBudget.Evaluate"/>, the benches
/// <see cref="ChainBenches.Write"/> and the display <see cref="ChainBudgetDisplay"/>, the three calls the window's own
/// Transfer to Schematic makes. What is here is argument parsing, refusals and reporting, on <c>Authoring.cs</c>' terms.</para>
///
/// <para><b>The budget is a DataSet</b>, as every run's result is: cubes over a <c>stage</c> axis and the chain's totals
/// as scalars, so <c>--json</c>, <c>-o</c> and the Data Display read it with nothing new.</para>
/// </summary>
internal static class Chain
{
    public static int Run(string[] args)
    {
        string? path = null, output = null, into = null, name = null;
        bool toSchematic = false, replace = false, demo = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output" when i + 1 < args.Length: output = args[++i]; continue;
                case "--to-schematic":
                    toSchematic = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) into = args[++i];
                    continue;
                case "--name" when i + 1 < args.Length: name = args[++i]; continue;
                case "--replace": replace = true; continue;
                case "--demo": demo = true; continue;
            }
            if (args[i].StartsWith('-')) return JsonRun.Fail(CliDiagnostics.ChainUnknownOption(args[i]));
            if (path is not null) return JsonRun.Fail(CliDiagnostics.ChainUnknownOption(args[i]));
            path = args[i];
        }
        if (path is null)
        {
            Console.Error.WriteLine(
                "Usage: circuitrf chain <path.cchain> [-o budget.{npy,mat,txt}]\n" +
                "                       [--to-schematic [workspace-dir]] [--name <chain>] [--replace] [--demo]");
            return 1;
        }
        JsonRun.InputPath = path;
        var kind = DocumentKinds.Classify(path);
        if (kind != DocumentKind.Chain)
            return JsonRun.Fail(CliDiagnostics.ChainNotAChain(path, DocumentKinds.Name(kind)));

        ChainDesign design;
        try { design = ChainDesignIo.LoadFromFile(path); design.Demo |= demo; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { return JsonRun.Fail(CliDiagnostics.ChainUnreadable(path, ex.Message)); }

        var budget = ChainBudget.Evaluate(design);
        if (budget.Totals is null)
        {
            for (int i = 0; i < budget.Refusals.Count - 1; i++) JsonRun.Report(CliDiagnostics.ChainRefused(path, budget.Refusals[i]));
            return JsonRun.Fail(CliDiagnostics.ChainRefused(path, budget.Refusals[^1]));
        }
        foreach (var n in budget.Notes) JsonRun.Note(CliDiagnostics.ChainNote(n));
        Report(budget, design);
        JsonRun.Data = ChainBudget.ToDataSet(budget);

        if (output is not null)
        {
            var format = CliEntry.FormatFromExtension(output);
            DataSetExporter.Export(JsonRun.Data, output, format, new ExportOptions(Format: format));
            Console.WriteLine($"Wrote {output}");
            JsonRun.AddOutput(JsonRun.KindOf(output), output);
        }

        if (!toSchematic) return 0;

        string dir = Path.GetFullPath(into ?? Path.GetDirectoryName(Path.GetFullPath(path))!);
        name ??= Path.GetFileNameWithoutExtension(path);
        var written = ChainBenches.Write(design, dir, name, replace);
        if (written.SchematicPaths.Count == 0)
        {
            for (int i = 0; i < written.Refusals.Count - 1; i++) JsonRun.Report(CliDiagnostics.ChainTransferRefused(written.Refusals[i]));
            return JsonRun.Fail(CliDiagnostics.ChainTransferRefused(written.Refusals[^1]));
        }
        foreach (var n in written.Notes.Except(budget.Notes)) JsonRun.Note(CliDiagnostics.ChainNote(n));
        foreach (var p in written.SchematicPaths)
        {
            Console.WriteLine(p);
            JsonRun.AddOutput("csch", p);
        }
        foreach (var cdd in ChainBudgetDisplay.WriteAll(dir, name, design, budget))
        {
            Console.WriteLine(cdd);
            JsonRun.AddOutput("cdd", cdd);
        }
        return 0;
    }

    private static string D(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "-";

    private static void Report(ChainBudgetResult r, ChainDesign d)
    {
        var t = r.Totals!;
        Console.WriteLine($"Chain budget: {r.Stages.Count} stage(s), {d.FrequencyHz / 1e6:0.###} MHz in, {t.OutputFrequencyHz / 1e6:0.###} MHz out, "
                        + $"{d.BandwidthHz / 1e6:0.###} MHz bandwidth, {D(d.InputPowerDbm)} dBm in, source {d.TemperatureK:0.#} K");
        Console.WriteLine($"  {"stage",-12} {"part",-12} {"G dB",8} {"NF dB",8} {"IIP3",8} {"OIP3",8} {"IP1dB",8} {"OP1dB",8} {"Pout",8} {"SNR dB",8} {"F-1 %",7}");
        foreach (var s in r.Stages)
            Console.WriteLine($"  {s.Name,-12} {s.Part,-12} {D(s.GainDb),8} {D(s.NfDb),8} {D(s.Iip3Dbm),8} {D(s.Oip3Dbm),8} "
                            + $"{D(s.Ip1dbDbm),8} {D(s.Op1dbDbm),8} {D(s.PoutDbm),8} {D(s.SnrDb),8} {D(s.NoiseSharePct),7}");
        Console.WriteLine($"  gain {D(t.GainDb)} dB, NF {D(t.NfDb)} dB (Te {t.TeK:0.#} K), IIP3 {D(t.Iip3Dbm)} dBm, OIP3 {D(t.Oip3Dbm)} dBm, "
                        + $"IP1dB {D(t.Ip1dbDbm)} dBm, OP1dB {D(t.Op1dbDbm)} dBm");
        Console.WriteLine($"  thermal floor {D(t.ThermalFloorDbm)} dBm, output noise {D(t.NoiseOutDbm)} dBm, MDS {D(t.MdsDbm)} dBm, "
                        + $"sensitivity {D(t.SensitivityDbm)} dBm at {D(d.RequiredSnrDb)} dB SNR");
        Console.WriteLine($"  SNR {D(t.SnrDb)} dB, SFDR {D(t.SfdrDb)} dB" + (t.ArrayGainDb > 0 ? $", array gain {D(t.ArrayGainDb)} dB" : ""));
        if (ChainCapacity.Read(r) is { } o)
            Console.WriteLine($"  noise at the input {D(o.NirDbm)} dBm (NF of the array {D(o.NfArrayDb)} dB) in {o.BandwidthHz / 1e6:0.###} MHz, "
                            + $"margin {D(o.MarginDb)} dB, rate {ChainCapacity.Rate(o.RateBps)}"
                            + (o.Streams > 1 ? $" over {o.Streams} streams" : "") + $", ceiling {D(o.CeilingDbm)} dBm");
        if (r.Design is { Link: { } link } rd && link.Budget(rd.FrequencyHz) is var l)
            Console.WriteLine($"  link: {D(l.PoutDbm)} dBm from each of {l.Mtx} PAs, EIRP {D(l.EirpDbm)} dBm, free space {D(l.LfsDb)} dB "
                            + $"and {D(l.AtmDb)} dB more over the path, {D(l.PrxDbm)} dBm at each element, {D(rd.InputPowerDbm)} dBm in");
        if (ChainLink.Power(r) is { TotalW: > 0 } p)
            Console.WriteLine($"  DC power: transmit {ChainLink.Watts(p.TxW)}, receive {ChainLink.Watts(p.RxW)} ({p.Mrx} element(s)), "
                            + $"total {ChainLink.Watts(p.TotalW)}, {D(p.EnergyPerBitJ * 1e12)} pJ/bit");
    }
}
