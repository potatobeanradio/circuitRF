using CircuitRF.Design.Interchange.Hzn;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// HZN-8 R-hzn8-1 — Hertzon's line in <c>solver list</c> (and so in the MCP <c>solver</c> tool, which calls it): found or not,
/// its version and command-line schema, the route that found it, and the sentence that turns Run in Hertzon off when the
/// schema is not the one circuitRF is written for. The probe is <see cref="HertzonDiscovery"/>'s, the Settings row's too.
/// </summary>
internal static class HznSolverRow
{
    public static void Append(List<SolverJson> report)
    {
        var discovery = HertzonDiscovery.Shared;
        var ready = discovery.Check();
        var found = ready.Installation;
        string state = found is null ? "not found" : "found";
        string summary = discovery.Summary(ready);
        report.Add(new SolverJson("hertzon", "Hertzon", state, summary, found?.Path, found?.Version, found is null ? null : Route(found.HowFound),
                                  found is null ? null : ready.Ready, [], null) { CliSchema = found?.Schema });
        Console.WriteLine($"Hertzon: {state}");
        Console.WriteLine($"  {summary}");
        if (found is not null)
        {
            Console.WriteLine($"  route: {Route(found.HowFound)}; command-line schema: {found.Schema} (this circuitRF runs {HertzonDiscovery.SupportedSchema})");
            JsonRun.AddOutput("solver", found.Path);
        }
    }

    private static string Route(HertzonHowFound how) => how switch
    {
        HertzonHowFound.Settings         => "settings",
        HertzonHowFound.Environment      => "environment",
        HertzonHowFound.Home             => "environment",
        HertzonHowFound.Path             => "path",
        _                                => "default-directory",
    };
}
