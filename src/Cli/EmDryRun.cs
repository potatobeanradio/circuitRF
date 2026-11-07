using System.Globalization;
using CircuitRF.Engine.Mom;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// EMB-5 R-emb5-2 — <c>circuitrf em --dry-run</c>: what a run of this setup would cost, before committing to it.
///
/// <para><b>It computes nothing of its own.</b> It is <c>check</c> and <c>explain</c> of the same file in one
/// invocation, so every refusal <c>check</c> raises is here (a planar setup's extract and mesh, its memory check, its
/// near-field estimate; a 3D setup's port and solver refusals) and every number is the one those two report (a planar
/// setup's mesh figures from <c>check</c>; a 3D setup's size rows from <c>explain</c>, Palace's estimate after EMB-4's
/// correction, with its confidence sentence when imported outlines make it low). The one line it adds,
/// <c>em.dry-run.estimate</c>, restates those numbers so a caller reading one diagnostic has them without walking two
/// result sections. Nothing is solved and nothing is written: a planar dry run meshes as <c>check</c> does (a run meshes
/// first too); a 3D one lowers its geometry and starts no mesher.</para>
/// </summary>
internal static class EmDryRun
{
    public static int Run(string input, string? setupName, string? device, string? memoryLimit, int? maxCores = null)
    {
        int checkExit = Check.Run([input]);
        RunHost.Control?.ThrowIfCancellationRequested();

        var explainArgs = new List<string> { input };
        if (setupName is not null) { explainArgs.Add("--setup"); explainArgs.Add(setupName); }
        if (device is not null) { explainArgs.Add("--device"); explainArgs.Add(device); }
        if (memoryLimit is not null) { explainArgs.Add("--memory-limit"); explainArgs.Add(memoryLimit); }
        if (maxCores is { } cores) { explainArgs.Add("--cores"); explainArgs.Add(cores.ToString(CultureInfo.InvariantCulture)); }
        int explainExit = Explain.Run([.. explainArgs]);

        if (Line(JsonRun.Check, JsonRun.Explain) is { } line)
        {
            Console.WriteLine("Estimate:  " + line);
            JsonRun.Note(CliDiagnostics.EmDryRunEstimate(line));
        }
        return checkExit != 0 || explainExit != 0 ? 1 : 0;
    }

    /// <summary>The estimate, restated from the two reports in one line; null when neither carries one (a setup
    /// refused before it was meshed or lowered — the refusal is then the answer).</summary>
    internal static string? Line(CheckReportJson? check, ExplainReportJson? explain)
    {
        var parts = new List<string>();

        if (check?.Documents.FirstOrDefault(d => d.Em is not null)?.Em is { } em)
        {
            string s = $"{em.Kernel}, {em.Unknowns:N0} unknowns";
            if (em.Ceiling is { } ceiling)
                s += $" against the {em.CeilingKind} ceiling of {ceiling:N0} ({em.Verdict})";
            if (em.MemoryBytes is { } b)
                s += $", about {PlanarNearFieldEstimate.Size(b)} a frequency point" +
                     (em.Memory == "past-limit" ? ", past this machine's memory" : "");
            if (em.FactorSecondsPerPoint is { } t)
                s += $", factoring about {Duration(t)} a point" +
                     (em.FactorSecondsTotal is { } total && em.Points is { } n ? $", about {Duration(total)} for {n:N0} points" : "") +
                     " (the factor alone; the fill is extra)";
            parts.Add(s);
        }

        if (explain?.Em3d is { } em3d)
        {
            foreach (var row in em3d.Size.Where(r => r.Kind != "unavailable"))
                parts.Add($"{row.Backend}: {row.Note}");
            if (em3d.Limits is { Count: > 0 } limits) parts.Add(string.Join(" ", limits));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts.Select(p => p.TrimEnd('.') + "."));
    }

    /// <summary>Seconds in the words <see cref="SurfaceMesher.EstimatedFactorTime"/> uses.</summary>
    private static string Duration(double s)
    {
        var c = CultureInfo.InvariantCulture;
        return s < 1 ? "under a second"
             : s < 90 ? $"{s.ToString("0", c)} s"
             : s < 90 * 60 ? $"{(s / 60).ToString("0", c)} min"
             : $"{(s / 3600).ToString("0.0", c)} h";
    }
}
