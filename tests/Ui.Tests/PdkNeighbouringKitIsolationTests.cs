using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Importing one kit must never pick up a NEIGHBOURING kit's compiled models.
///
/// <para><b>The shape this exists for, from a real report.</b> Unpacked kits live side by side under
/// one folder. The compiled-Verilog-A search used to widen to two ancestor levels when the kit's own
/// tree held nothing — so importing a kit whose devices come from a compiled model LIBRARY found the
/// artefacts belonging to an unrelated kit two levels up, concluded on the strength of them that this
/// was a compiled-Verilog-A kit, and wrote settings naming the other kit's worker. Everything
/// imported cleanly; the failure surfaced at Run, as the provider offering a device type that
/// belonged to somebody else entirely.</para>
///
/// <para><b>Why the rule differs from the library search's.</b> A model library is recognised by the
/// entry points circuitRF's own worker will call, so finding one beside a kit is evidence it serves
/// that kit. A <c>.osdi</c> artefact carries nothing of the sort — it is one compiled module, and a
/// folder of kits therefore answers every one of them with the first kit's models. An ancestor is a
/// coincidence; the kit's own tree, and the folders the workspace was TOLD about, are statements.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkNeighbouringKitIsolationTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-kit-neighbour-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    /// <summary>The kit being imported. Holds a symbol and a netlist, and no compiled artefact.</summary>
    private string KitDir => Path.Combine(_root, "kits", "the-kit");

    /// <summary>A different kit, sitting beside it, whose artefacts are nothing to do with it.</summary>
    private string OtherKitDir => Path.Combine(_root, "kits", "another-kit");

    public PdkNeighbouringKitIsolationTests()
    {
        Directory.CreateDirectory(Path.Combine(KitDir, "models"));
        Directory.CreateDirectory(Path.Combine(OtherKitDir, "models"));
    }

    public void Dispose()
    {
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
        PdkKitRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PdkImportReport BuildKit(string kitDir, string kitName, bool withArtefact)
    {
        string rel = Path.Combine("models", "dev.lib");
        File.WriteAllText(Path.Combine(kitDir, rel), """
            .subckt myrc a b gval=0.002
            Nx a b rc_card g0=gval
            .ends

            .model rc_card crf_rc  G0 = 0.5  C = 1e-12  TC = 0.0  TNOM = 300.0
            """);

        if (withArtefact)
            File.Copy(FixturePaths.Require(ModelRel), Path.Combine(kitDir, "models", "fake_osdi.osdi"));

        var report = new PdkImportReport { RootPath = kitDir, KitName = kitName };
        report.Parts.Add(new PdkPart(
            "PART_A", "Part A",
            Parameters: [new PdkPartParameter("gval", "0.002")],
            Pins: [new KitSymbolPin("a", 0, 0), new KitSymbolPin("b", 500, 0)],
            DefinitionRelativePath: rel.Replace(Path.DirectorySeparatorChar, '/'),
            DefinitionCell: "myrc"));
        return report;
    }

    // ── The gate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The kit being imported has no artefact of its own; the kit beside it has one. Importing the
    /// first must find nothing — the second kit's file is two levels up from it and belongs to
    /// somebody else.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void ACompiledModelBelongingToAKitBesideThisOneIsNotFound()
    {
        BuildKit(OtherKitDir, "another-kit", withArtefact: true);

        DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(FixturePaths.Require(WorkerRel))!;
        var outcome = PdkPartInstaller.Install(BuildKit(KitDir, "the-kit", withArtefact: false));

        Assert.Empty(outcome.OsdiModels ?? []);

        // …and therefore the settings are NOT the compiled-Verilog-A ones. This half is the part the
        // owner actually saw: a kit with a perfectly good model library of its own, described as
        // though its devices were somebody else's compiled modules.
        string? command = (outcome.Settings?["workers"] as JsonArray)?
            .Select(w => w?["command"]?.GetValue<string>())
            .FirstOrDefault(c => c is not null);
        Assert.NotEqual("osdi-worker", command);
    }

    /// <summary>The kit's OWN artefact is still found, however deep inside its tree it sits — the
    /// rule narrows where the search starts, not how far down it reaches.</summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void TheKitsOwnCompiledModelIsStillFound()
    {
        DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(FixturePaths.Require(WorkerRel))!;
        var outcome = PdkPartInstaller.Install(BuildKit(KitDir, "the-kit", withArtefact: true));

        var model = Assert.Single(outcome.OsdiModels!);
        Assert.Equal("fake_osdi.osdi", Path.GetFileName(model.FilePath));
    }

    /// <summary>
    /// A folder the WORKSPACE was told holds model libraries is still searched. That is an explicit
    /// statement by the user, which is exactly what the ancestor walk was not.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void ADeclaredLibraryFolderIsStillSearched()
    {
        BuildKit(OtherKitDir, "another-kit", withArtefact: true);

        DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(FixturePaths.Require(WorkerRel))!;
        var outcome = PdkPartInstaller.Install(
            BuildKit(KitDir, "the-kit", withArtefact: false), null, [OtherKitDir]);

        Assert.Single(outcome.OsdiModels!);
    }

    /// <summary>
    /// Settings circuitRF derived under the OLD rule are redone rather than replayed. Fixing the
    /// search alone would leave every workspace that already recorded an answer pointing at the other
    /// kit's artefact for good — the recorded settings win on every open, by design.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void SettingsDerivedUnderTheOldRuleAreRedoneRatherThanReplayed()
    {
        DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(FixturePaths.Require(WorkerRel))!;

        var stale = new JsonObject
        {
            ["provider"]        = "the-kit",
            ["generatedBy"]     = "circuitRF",
            ["generatedFormat"] = 4,
            ["workers"]         = new JsonArray(new JsonObject
            {
                ["platform"]  = "any",
                ["command"]   = "osdi-worker",
                ["arguments"] = new JsonArray("/somewhere/else/not-this-kits.osdi"),
            }),
        };

        var outcome = PdkPartInstaller.Install(
            BuildKit(KitDir, "the-kit", withArtefact: true), stale);

        string argument = Assert.Single(
            ((outcome.Settings!["workers"] as JsonArray)![0]!["arguments"] as JsonArray)!)!
            .GetValue<string>();
        Assert.Equal("fake_osdi.osdi", Path.GetFileName(argument));
    }

    /// <summary>
    /// A kit's OWN settings, and a user's edits to them, are never redone — only circuitRF's own
    /// earlier working-out is. The format bump must not become a licence to overwrite either.
    /// </summary>
    [Fact]
    public void SettingsThatAreNotCircuitRfsOwnAreLeftAlone()
    {
        var theirs = new JsonObject
        {
            ["provider"] = "the-kit",
            ["workers"]  = new JsonArray(new JsonObject
            {
                ["platform"]  = "any",
                ["command"]   = "their-worker",
                ["arguments"] = new JsonArray(),
            }),
        };

        var outcome = PdkPartInstaller.Install(
            BuildKit(KitDir, "the-kit", withArtefact: false), theirs);

        Assert.Equal("their-worker",
            (outcome.Settings!["workers"] as JsonArray)![0]!["command"]!.GetValue<string>());
    }
}
