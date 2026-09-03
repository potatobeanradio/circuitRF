using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Routing a kit device that references a <c>.model</c> card to the compiled Verilog-A artefact
/// implementing that card's type.
///
/// <para><b>Why this needs a real artefact and a real worker rather than a fixture.</b> The whole
/// mechanism turns on facts that only a compiled file carries: the module name lives INSIDE it and is
/// not derivable from its file name, and the parameter spellings it declares are what a card's names
/// have to be respelled into. A stand-in would have to state both, which is to assume the two things
/// the discovery exists to find out. So these skip with a reason where the worker or the sample model
/// is not built, in the same shape as <c>OsdiWorkerTests</c>.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkCompiledModelRoutingTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    private const string Kit  = "SampleKit";
    private const string Part = "PART_A";

    /// <summary>A module the sample artefact really declares, and one of its parameters.</summary>
    private const string Module = "crf_rc";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-osdi-route-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previousTools = DeviceWorkerManifest.ToolsDirectory;

    private string KitDir => Path.Combine(_root, "kit");

    public PdkCompiledModelRoutingTests() => Directory.CreateDirectory(Path.Combine(KitDir, "models"));

    public void Dispose()
    {
        DeviceWorkerManifest.ToolsDirectory = _previousTools;
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── the kit ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A netlist in the SPICE dialect whose subcircuit instantiates a device by naming a
    /// <c>.model</c> card — which is how this dialect writes a compiled device, and the shape that
    /// used to bottom out in "provider not available".
    ///
    /// <para>The card's parameters are deliberately written in the WRONG CASE for the artefact
    /// (<c>G0</c> where the module declares <c>g0</c>): the dialect is case-insensitive and the
    /// artefact is not, so this is the ordinary state of affairs rather than a contrived one.</para>
    /// </summary>
    private const string Netlist = """
        .subckt myrc a b gval=0.002
        Nx a b rc_card g0=gval
        .ends

        .model rc_card crf_rc  G0 = 0.5  C = 1e-12  TC = 0.0  TNOM = 300.0
        """;

    private PdkImportReport BuildKit(string netlist = Netlist, bool withArtefact = true)
    {
        string rel = Path.Combine("models", "dev.lib");
        File.WriteAllText(Path.Combine(KitDir, rel), netlist);

        if (withArtefact)
            File.Copy(FixturePaths.Require(ModelRel), Path.Combine(KitDir, "models", "fake_osdi.osdi"));

        var report = new PdkImportReport { RootPath = KitDir, KitName = Kit };
        report.Parts.Add(new PdkPart(
            Part, "Part A",
            // A declared parameter, because that is what a real part has — and because the installer
            // only offers ModelLibrary on a part that declares some, which is the row these tests
            // need present-and-blank.
            Parameters: [new PdkPartParameter("gval", "0.002")],
            Pins: [new KitSymbolPin("a", 0, 0), new KitSymbolPin("b", 500, 0)],
            DefinitionRelativePath: rel.Replace(Path.DirectorySeparatorChar, '/'),
            DefinitionCell: "myrc"));
        return report;
    }

    private PdkPartInstaller.InstallOutcome Install(PdkImportReport report)
    {
        DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(FixturePaths.Require(WorkerRel))!;
        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? [], outcome.OsdiModels);
        return outcome;
    }

    /// <summary>
    /// The part, placed the way PLACEMENT actually places it — carrying every parameter the cell
    /// declares, most of them blank.
    ///
    /// <para><b>That detail is not decoration; it is where the first real bug lived.</b>
    /// <c>ModelLibrary</c> is offered on every kit part and defaults to an empty string, so the
    /// per-instance override reads as <c>""</c> rather than null. A test that places a bare component
    /// carries no such parameter and so exercises the null path only — which is exactly how a
    /// null-coalesce onto the artefact path passed here and composed a bare provider name in the
    /// application.</para>
    /// </summary>
    private static EditableComponent Place(string part = Part)
    {
        var installed = PdkKitRegistry.Find(PdkKitRegistry.RefFor(Kit, part), null)
                        ?? throw new Xunit.Sdk.XunitException($"'{part}' is not loaded");

        var comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, part),
            X = 0, Y = 0,
        };

        foreach (var p in installed.Ccell.Parameters)
            comp.Parameters.Add(new EditableParameter
            {
                Name = p.Name, Expression = p.DefaultExpression ?? "", Unit = p.Unit ?? "",
            });

        return comp;
    }

    /// <summary>The one device instance inside the kit's own cell, after extraction.</summary>
    private static (Instance Instance, IReadOnlyList<string> Conflicts) ExtractDevice(
        EditableComponent? placed = null)
    {
        var model = new SchematicEditModel { SchematicDirectory = Path.GetTempPath() };
        model.Components.Add(placed ?? Place());

        var ext  = NetExtractor.Extract(model, "tb");
        var cell = ext.Library.Cells.FirstOrDefault(c => c.Name.Equals("myrc", StringComparison.OrdinalIgnoreCase));
        return (cell?.Instances.FirstOrDefault()!, ext.Conflicts);
    }

    private static string? Value(Instance inst, string name)
        => inst.Overrides.FirstOrDefault(o => o.Name == name)?.Expression;

    // ── discovery ─────────────────────────────────────────────────────────────

    [FixtureFact(WorkerRel, HowTo)]
    public void TheArtefactIsFoundNearTheKit_AndTheModuleIsReadFromInsideIt()
    {
        var outcome = Install(BuildKit());

        var model = Assert.Single(outcome.OsdiModels!);
        Assert.Equal("fake_osdi.osdi", Path.GetFileName(model.FilePath));

        // The file's own name says nothing about what it implements — which is the whole reason the
        // module is asked for rather than derived.
        Assert.Contains(Module, model.TypeIds);
        Assert.Equal(model.TypeIds, PdkKitRegistry.OsdiModelsOf(null, Kit).Single().TypeIds);
    }

    /// <summary>
    /// A WORKSPACE OPEN finds the artefacts too, even though it replays recorded settings and so
    /// never reaches the synthesis.
    ///
    /// <para>This is the case a user is actually in every day after the first import, and getting it
    /// wrong would leave every card-backed device reporting "no compiled model implementing it" on
    /// reopen while a fresh import worked — which is why the discovery lives in <c>Install</c> rather
    /// than inside the synthesis it feeds.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void AReopenReplayingRecordedSettings_StillFindsTheArtefacts()
    {
        var report   = BuildKit();
        var recorded = Install(report).Settings;
        Assert.NotNull(recorded);

        PdkKitRegistry.ResetAllForTests();
        var reopened = PdkPartInstaller.Install(report, recorded);

        Assert.Single(reopened.OsdiModels!);
        Assert.Same(recorded, reopened.Settings);   // replayed, not re-derived
    }

    /// <summary>
    /// The manifest names <c>osdi-worker</c> by BARE command with the artefact as its one argument —
    /// the form <c>tools/osdi-worker/README.md</c> documents and <c>O7</c> gates. Bare, so it resolves
    /// out of circuitRF's tools folder on whichever machine eventually runs the design rather than
    /// recording the one that imported the kit.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void TheSettledManifest_LaunchesTheOsdiWorkerAgainstTheArtefact()
    {
        var outcome = Install(BuildKit());

        var manifest = PdkPartInstaller.ManifestFrom(outcome.Settings, KitDir, Kit);
        Assert.NotNull(manifest);

        var launch = Assert.Single(manifest!.Launches);
        Assert.Equal("osdi-worker", launch.Command);
        Assert.EndsWith(".osdi", Assert.Single(launch.Arguments), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(manifest.LaunchForThisMachine());
    }

    // ── routing ───────────────────────────────────────────────────────────────

    [FixtureFact(WorkerRel, HowTo)]
    public void ACardBackedDevice_IsRoutedToTheArtefactImplementingTheCardsType()
    {
        Install(BuildKit());

        var (inst, conflicts) = ExtractDevice();

        Assert.Empty(conflicts);
        Assert.Equal("ExtDevice", inst.Reference);

        // The FILE travels in the provider name, because that is what the device registry keys on:
        // two models in one design must get two workers, not one evaluating both.
        string provider = Value(inst, "Provider")!;
        var (kit, library) = DeviceWorkerProviderResolver.SplitOverride(provider);
        Assert.Equal(Kit, kit);
        Assert.Equal("fake_osdi.osdi", Path.GetFileName(library));

        // The MODULE is the artefact's own spelling — never the card's name, which no provider has
        // ever heard of, and never the card's type as written, which is matched ordinally downstream.
        Assert.Equal(Module, Value(inst, "Type"));
    }

    /// <summary>
    /// A model library the USER chose on the instance still wins over the artefact the card resolved
    /// to — that is a person saying "evaluate this one against that file", and it can only mean the
    /// file it names.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void AnExplicitModelLibrary_StillBeatsTheArtefactTheCardResolvedTo()
    {
        Install(BuildKit());

        var comp = Place();
        comp.Parameters.Single(p => p.Name == PdkPartInstaller.ModelLibraryParameter)
            .Expression = "/elsewhere/other.osdi";

        var (inst, _) = ExtractDevice(comp);

        Assert.Equal("/elsewhere/other.osdi",
                     DeviceWorkerProviderResolver.SplitOverride(Value(inst, "Provider")!).Library);
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void TheCardsParametersAreMerged_UnderTheInstancesOwn()
    {
        Install(BuildKit());

        var (inst, _) = ExtractDevice();

        // From the card, which the instance line says nothing about.
        Assert.Equal("1E-12", Value(inst, "c"));   // the reader's own normalised spelling

        // The instance line wins where both state it: the card is the model's parameterisation and
        // the instance is what this particular device was built as.
        Assert.Equal("gval", Value(inst, "g0"));
        Assert.DoesNotContain(inst.Overrides, o => o.Name == "G0");
    }

    /// <summary>
    /// The card writes <c>G0</c>; the artefact declares <c>g0</c>; the worker matches with
    /// <c>strcmp</c>. Measured against a real compiled model: the mismatched spelling is refused by
    /// name, so respelling is correctness rather than tidying.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void ParameterNames_AreRespelledTheWayTheArtefactDeclaresThem()
    {
        Install(BuildKit());

        var (inst, _) = ExtractDevice();

        Assert.Contains(inst.Overrides, o => o.Name == "c"    && o.Expression == "1E-12");
        Assert.Contains(inst.Overrides, o => o.Name == "tnom" && o.Expression == "300");
        Assert.DoesNotContain(inst.Overrides, o => o.Name is "C" or "TNOM");
    }

    /// <summary>
    /// A name the artefact declares NOTHING like is left exactly as written, so a genuine typo is
    /// still refused by name downstream instead of being quietly turned into a parameter the model
    /// does accept. Without this the respelling would be a spell-checker rather than a translation.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void AParameterTheModuleDoesNotDeclare_IsLeftExactlyAsWritten()
    {
        Install(BuildKit(Netlist.Replace("TNOM = 300.0", "TNOM = 300.0  NOTAPARAM = 7")));

        var (inst, _) = ExtractDevice();

        Assert.Contains(inst.Overrides, o => o.Name == "NOTAPARAM" && o.Expression == "7");
    }

    // ── the case that must not be silent ──────────────────────────────────────

    /// <summary>
    /// A card naming a module nobody has compiled is REPORTED and the instance is left as the kit
    /// wrote it. Routing it to the kit's own provider under the card's name instead would fail at Run
    /// naming a device type that appears nowhere — which sends the reader looking for a missing
    /// provider when what is missing is a build step they have not run.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void AModuleNobodyCompiled_IsNamedAndTheBuildStepIsStated()
    {
        Install(BuildKit(Netlist.Replace("crf_rc  G0", "mdla_va  G0")));

        var (inst, conflicts) = ExtractDevice();

        Assert.NotEqual("ExtDevice", inst.Reference);
        Assert.Contains(conflicts, c => c.Contains("mdla_va", StringComparison.Ordinal)
                                     && c.Contains("Verilog-A", StringComparison.Ordinal));
    }

    /// <summary>
    /// The pre-existing rule is untouched where it still applies: an instance whose reference is
    /// neither a primitive, nor a cell the kit defines, nor a model card is the kit's own compiled
    /// device family, and it still binds to the kit's provider under that name.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void ADeviceThatNamesNoCard_StillBindsByItsOwnTypeName()
    {
        Install(BuildKit(Netlist.Replace("Nx a b rc_card g0=gval", "Nx a b KIT_FAMILY_v1 g0=gval")));

        var (inst, _) = ExtractDevice();

        Assert.Equal("ExtDevice", inst.Reference);
        Assert.Equal("KIT_FAMILY_v1", Value(inst, "Type"));
        Assert.Equal(Kit, Value(inst, "Provider"));   // no library override composed into the name
    }

    /// <summary>
    /// A part the kit DRAWS but does not model — no circuit definition, and no compiled module of its
    /// name — is explained rather than left to the provider to refuse.
    ///
    /// <para>Measured: its inductors are layout PCells with no simulation model
    /// anywhere in the netlists. Such a part reaches the provider under its own id, and the failure at
    /// Run reads <i>"does not expose a device type 'inductor'. Available: auxmodel"</i> — where
    /// <c>auxmodel</c> is simply whichever artefact the manifest names by default. Everything in that
    /// sentence is true and none of it is the answer.</para>
    ///
    /// <para>The instance is still emitted, so the run stops. A skipped component would leave a run
    /// that produces numbers for a circuit the user did not draw.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void APartTheKitDrawsButDoesNotModel_IsExplainedRatherThanLeftToTheProvider()
    {
        // No definition and no card: the leaf provider-backed shape, which on a compiled-Verilog-A
        // kit can never resolve.
        var report = BuildKit();
        report.Parts.Clear();
        report.Parts.Add(new PdkPart("SPIRAL_IND", "Spiral inductor",
                                     Parameters: [new PdkPartParameter("w", "1u")],
                                     Pins: [new KitSymbolPin("a", 0, 0), new KitSymbolPin("b", 500, 0)]));
        Install(report);

        var model = new SchematicEditModel { SchematicDirectory = Path.GetTempPath() };
        model.Components.Add(Place("SPIRAL_IND"));
        var ext = NetExtractor.Extract(model, "tb");

        Assert.Contains(ext.Conflicts, c => c.Contains("SPIRAL_IND", StringComparison.Ordinal)
                                         && c.Contains(Module, StringComparison.Ordinal));

        // Still emitted — the run must fail, not quietly lose the component.
        Assert.Contains(ext.TestBench.Instances, i => i.Reference == "ExtDevice");
    }

    /// <summary>
    /// …and a kit that is NOT compiled Verilog-A says nothing, because there the part's own id
    /// legitimately IS the device type the provider serves.
    /// </summary>
    [Fact]
    public void OnAKitWithNoCompiledModels_ThatExplanationIsNotOffered()
    {
        var report = new PdkImportReport { RootPath = KitDir, KitName = Kit };
        report.Parts.Add(new PdkPart("FAMILY_v1", "A family",
                                     Parameters: [new PdkPartParameter("w", "1u")],
                                     Pins: [new KitSymbolPin("a", 0, 0), new KitSymbolPin("b", 500, 0)]));

        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? [], outcome.OsdiModels);
        Assert.Empty(PdkKitRegistry.OsdiModelsOf(null, Kit));

        var model = new SchematicEditModel { SchematicDirectory = Path.GetTempPath() };
        model.Components.Add(Place("FAMILY_v1"));

        Assert.Empty(NetExtractor.Extract(model, "tb").Conflicts);
    }

    // ── it actually evaluates ─────────────────────────────────────────────────

    /// <summary>
    /// The gate that makes the rest mean anything: the routed provider name is resolved, the worker
    /// is started against the artefact the name carries, and the module answers.
    ///
    /// <para>Everything above could be exactly right and still name a launch nobody can perform —
    /// the substitution replaces the argument that names a LIBRARY, and an <c>.osdi</c> only counts
    /// as one because it was added to that check.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void TheRoutedProviderName_StartsTheWorkerAgainstThatArtefact()
    {
        var outcome = Install(BuildKit());
        var manifest = PdkPartInstaller.ManifestFrom(outcome.Settings, KitDir, Kit)!;

        var (inst, _) = ExtractDevice();
        string provider = Value(inst, "Provider")!;

        var resolver = new DeviceWorkerProviderResolver([(Kit, manifest)]);
        using var launched = resolver.Resolve(provider) as IDisposable;

        Assert.NotNull(launched);
        Assert.Contains(((IExternalDeviceProvider)launched!).Describe(), d => d.TypeId == Module);
    }
}
