using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;

namespace CircuitRF.Cli;

/// <summary>
/// HZN-7 R-hzn7-2 — <c>em design.hzn --design D [--setup S]</c>: a Hertzon 3D design run on circuitRF's EMerge driver.
///
/// <para>It owns no run: the plan (which setup, where the answer lands, what the run does not use) is
/// <see cref="HznEmergeRun"/>, and the run is <c>EmRunService.RunThreeDView</c> — the door a native <c>.c3d</c> goes
/// through — so a result written here is the one Simulate on the opened design writes. The walk-ups the other <c>em</c>
/// inputs follow do not apply: the technology is the project's own materials, and the layout is the design.</para>
/// </summary>
internal static class HznEm
{
    /// <param name="report">The <c>em</c> verb's own reporting, so this input prints exactly what a <c>.c3d</c> run prints.</param>
    public static int Run(string input, string? design, string? setupName, string? output, string? solverName, string? refused,
                          int? maxCores, bool force, long? memoryLimit, CircuitRF.Design.Em3d.EmergeDevice emergeDevice,
                          RunControl progress, CancellationToken cancellation, Func<EmRunResult, EmSetup, string, int> report)
    {
        // Options that belong to a layout or to another solver are refused, never ignored.
        if (refused is not null) return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(input, $"{refused} is not offered for a Hertzon design."));
        if (solverName is not null && !string.Equals(solverName, "emerge", StringComparison.OrdinalIgnoreCase))
            return JsonRun.Fail(HznEmergeRun.OnlyEmerge(solverName));

        var (plan, refusal) = HznEmergeRun.Plan(input, design, setupName);
        if (plan is null) return JsonRun.Fail(refusal!);

        Console.Error.WriteLine($"[circuitRF] Hertzon design '{plan.Design}', setup '{plan.C3dSetup}' ({plan.HznSetup}); results: {plan.ResultsRoot}");

        string? destination = null;
        if (output is not null)
        {
            destination = Path.GetFullPath(output);
            if (Path.GetDirectoryName(destination) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        }

        EmRunResult result;
        try
        {
            result = HznEmergeRun.Run(plan, cancellation, progress, maxCores, force ? _ => true : null, memoryLimit, emergeDevice, destination);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonRun.Fail(CliDiagnostics.RunFailed(ex.Message));
        }
        return report(result, plan.Setup, plan.DocumentPath);
    }
}
