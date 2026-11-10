using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Plugins;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Tests.Layout.PCells;
using CircuitRF.Ui.Tests.Revision;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>brief-plg-1 gate 5 — placement: the hello example places a cell, the checkpoint before it exists and
/// restoring it removes the cell; an invalid staged schematic is refused with the validator's sentence and nothing
/// is written.</summary>
[Collection(AppDataRootCollection.Name)]
public class PluginPlacementTests
{
    [PythonAndGitFact]
    public async Task HelloPlacesACellThatOneRestoreRemoves()
    {
        using var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig, "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");
        Assert.True(WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);
        var git = ws.Git();

        var entry = PluginTestKit.Example("hello");
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, PluginTestKit.NewScratch()) with { WorkspaceDir = ws.Root });
        Assert.True(started.Ok, started.Failure?.Sentence);
        await using var session = started.Value!;
        Assert.True((await session.HelloAsync()).Ok);

        var (values, errors) = PluginParams.Resolve(entry.Manifest!.Params, new Dictionary<string, string> { ["r"] = "75 Ohm" }, new(ws.Root));
        Assert.Empty(errors);
        Assert.Equal(75.0, (double)values!["r"]!["value"]!);
        var reply = await session.UpdateAsync(values);
        Assert.True(reply.Ok, reply.Failure?.Sentence);
        var placed = await session.CommitAsync(values);
        Assert.True(placed.Ok, placed.Failure?.Sentence);

        var result = PluginPlacement.Place(new PluginPlacementRequest(ws.Root, entry.Manifest, placed.Value!.Cells.Single(), placed.Value.Messages));
        Assert.True(result.Outcome == PluginPlacementOutcome.Placed, string.Join("\n", result.Refusals));
        Assert.True(File.Exists(Path.Combine(ws.Root, "divider", "schematic", "divider.csch")));
        Assert.True(File.Exists(Path.Combine(ws.Root, "divider", "schematic", "divider.cnl")));
        Assert.Contains("Place Hello Divider 1.0.0", result.Checkpoint!.Label);

        // A second placement under the same name is a question, not an overwrite.
        Assert.Equal(PluginPlacementOutcome.NameTaken,
            PluginPlacement.Place(new PluginPlacementRequest(ws.Root, entry.Manifest, placed.Value.Cells.Single(), [])).Outcome);

        Assert.True(WorkspaceRestore.Restore(git, result.Checkpoint).Ok);
        Assert.False(Directory.Exists(Path.Combine(ws.Root, "divider")));
    }

    [Fact]
    public void AnInvalidStagedSchematicIsRefusedWithTheValidatorsSentenceAndWritesNothing()
    {
        string ws = PluginTestKit.NewScratch();
        File.WriteAllText(Path.Combine(ws, ".cws"), "{}");
        string staging = PluginTestKit.NewScratch();
        string csch = Path.Combine(staging, "bad.csch");
        File.WriteAllText(csch, """{ "Shapes": [] }""");

        var (manifest, _) = PluginManifestReader.ReadFile(Path.Combine(PluginTestKit.ExamplesRoot, "hello", "plugin.json"));
        var result = PluginPlacement.Place(new PluginPlacementRequest(ws, manifest!, new PluginPlacedCell("bad", [csch]), []));

        Assert.Equal(PluginPlacementOutcome.Refused, result.Outcome);
        Assert.Contains(result.Refusals, r => r.StartsWith("bad.csch: it does not look like a .csch view"));
        Assert.Equal([".cws"], Directory.EnumerateFileSystemEntries(ws).Select(Path.GetFileName));
    }
}
