using System.Text.Json.Nodes;
using CircuitRF.Design.Plugins;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The verbs a plugin's host calls are answered with (brief-plg-4 §1): <see cref="CliEntry.Run"/> itself, in this
/// process, with the isolation <c>serve</c> gives each tool call — its own collectors (<see cref="JsonRun.Isolate"/>), its
/// own cancellation (<see cref="RunHost.Install"/>) and its document written into a string rather than to stdout. So a
/// plugin's <c>simulate</c> is the run <c>circuitRF sparam</c> performs, not a description of it, and a resolved
/// reference is spelled as <c>explain --json</c> spells it. Argument parsing and nothing else lives here.
/// </summary>
public sealed class PluginVerbs : IPluginVerbs
{
    public static PluginVerbs Instance { get; } = new();

    public PluginVerbRun Run(IReadOnlyList<string> argv, string? exportDataTo, CancellationToken ct)
    {
        using var isolated = JsonRun.Isolate();
        var document = new StringWriter();
        JsonRun.Sink = document;
        int exit;
        try
        {
            using (RunHost.Install(ct, null))
                exit = CliEntry.Run([.. argv, "--json", "--result", "summary"]);
            // The data set the verb produced, through the exporter `-o out.npy` writes with — read before the
            // collectors are reset, while it is still this call's.
            if (exit == 0 && exportDataTo is not null && JsonRun.Data is { } data)
                DataSetExporter.Export(data, exportDataTo, ExportFormat.Npy, new ExportOptions(Format: ExportFormat.Npy));
        }
        catch (OperationCanceledException) { exit = 130; }
        finally
        {
            JsonRun.Sink = null;
            JsonRun.Reset();
        }

        JsonObject? parsed = null;
        try { parsed = JsonNode.Parse(document.ToString()) as JsonObject; }
        catch (System.Text.Json.JsonException) { }
        return new PluginVerbRun(exit, parsed);
    }
}
