// M1 — the core-count control: gates 5 and 12 of docs/sonnet-briefs/brief-em-sweep-performance.md.
//
// R-emp-7 is the one that matters and it is a NEGATIVE: the core count must move NO provenance hash.
// R-fil-11 guarantees the answer is independent of scheduling and R-emp-8 asserts it as bit-identity
// (tests/Engine.Tests/Mom/ParallelBudgetTests.cs), so marking an .snp stale because a user moved a
// slider would be a straightforward lie. That is asserted here rather than merely arranged — the
// arrangement (the core count is not part of any model a hash is taken over) is exactly the kind of
// thing a later refactor can quietly undo.
//
// The other half is R-emp-6: STORED in AppPreferences, SHOWN in the EM Setup panel. A .cem travels
// with the workspace; a core count is a property of the machine.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// Swaps <see cref="EmSolveCores"/> onto its test seam for the duration of a test, so nothing here
/// ever writes the developer's real preferences file — the same TestOverride pattern
/// <c>SkiaFonts.TestOverrideTypeface</c> established in this suite.
/// </summary>
internal sealed class CoreStoreScope : IDisposable
{
    public CoreStoreScope(int? seed = null)
    {
        EmSolveCorePreference.TestOverrideStore  = seed;
        EmSolveCorePreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        EmSolveCorePreference.TestOverrideActive = false;
        EmSolveCorePreference.TestOverrideStore  = null;
    }
}

public class EmCoreCountTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView LineLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        {
            Layer = new(1, 0),
            X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000,
        });
        return view;
    }

    private static EmSetupEditorViewModel Editor()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-cores-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path  = Path.Combine(dir, "panel.cem");
        var    setup = new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), LineLayout(), StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-emp-7 — the core count moves NO provenance hash
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void REmp7_TheCoreCount_MovesNoProvenanceHash()
    {
        var problem = PlanarProblemFor(LineLayout());
        var mesh    = PlanarMeshSettings.Default;
        var ports   = Array.Empty<PlanarPort>();

        string g = EmSnpProvenance.GeometryHash(problem);
        string m = EmSnpProvenance.MeshHash(mesh);
        string p = EmSnpProvenance.PortHash(ports);

        foreach (int? cap in new int?[] { null, 1, 2, 4, 8 })
        {
            using var _ = new CoreStoreScope(cap);
            Assert.Equal(cap is null ? null : Math.Min(cap.Value, EmSolveCores.ProcessorCount),
                         EmSolveCorePreference.Preferred);

            // The same problem, mesh and ports at every cap. Nothing the cap can reach is in any of
            // the three hashes — which is what makes an .snp produced at 4 cores still current at 8.
            Assert.Equal(g, EmSnpProvenance.GeometryHash(problem));
            Assert.Equal(m, EmSnpProvenance.MeshHash(mesh));
            Assert.Equal(p, EmSnpProvenance.PortHash(ports));
        }
    }

    [Fact]
    public void TheCoreCount_IsNotInTheCem_AtAll()
    {
        // R-emp-6, asserted at the format rather than by reading the model: a .cem carrying a core
        // count would pin a colleague's machine to this one the moment they opened the workspace.
        var setup = new EmSetup
        {
            Name = "hero", LayoutRef = "Amp/layout/Amp.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = PlanarMeshSettings.Default with { MeshFrequencyHz = 10e9 },
        };
        string json = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("Core", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parallel", json, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The preference, the sanitiser, and the choice list
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void APreferenceCopiedFromABiggerMachine_IsClamped_NotTrusted()
    {
        // A preferences file copied from a 64-core box would otherwise ask for more cores than
        // exist; a hand-edited 0 or −1 would reach Parallel.For as a framework exception with no
        // mention of a core count in it. Anything unusable reads as Automatic, which always works.
        int here = EmSolveCores.ProcessorCount;

        Assert.Equal(here, EmSolveCores.Sanitise(here * 64));
        Assert.Null(EmSolveCores.Sanitise(0));
        Assert.Null(EmSolveCores.Sanitise(-4));
        Assert.Null(EmSolveCores.Sanitise(null));
        Assert.Equal(1, EmSolveCores.Sanitise(1));
    }

    [Theory]
    [InlineData(1,  new[] { 1 })]
    [InlineData(2,  new[] { 1, 2 })]
    [InlineData(6,  new[] { 1, 2, 4, 6 })]
    [InlineData(8,  new[] { 1, 2, 4, 8 })]
    [InlineData(10, new[] { 1, 2, 4, 8, 10 })]
    public void TheChoiceList_IsAutomaticThenPowersOfTwoThenTheCountItself(int cores, int[] expected)
    {
        var caps = EmSolveCores.Choices(cores);

        Assert.Null(caps[0]);                                   // Automatic is always first…
        Assert.Equal(expected, caps.Skip(1).Select(c => c!.Value));
        Assert.Equal(expected.Length, caps.Skip(1).Distinct().Count());  // …and 8 is not listed twice.

        // Automatic names the count it resolves to, so the default is not a word with no number
        // behind it — the whole point of showing it.
        Assert.Contains(cores.ToString(), EmSolveCores.Label(null, cores));
        Assert.Equal("1 core", EmSolveCores.Label(1, cores));
    }

    [Fact]
    public void ChoiceRows_CarryTheirOwnLabels_SoTheViewNeedsNoConverter()
    {
        var rows = EmSolveCores.ChoiceRows(8);
        Assert.Equal(EmSolveCores.Choices(8).Count, rows.Count);
        for (int i = 0; i < rows.Count; i++)
            Assert.Equal(EmSolveCores.Label(rows[i].Cap, 8), rows[i].Label);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The panel
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThePanel_SeedsFromThePreference_AndWritingItBackDirtiesNothing()
    {
        using var _ = new CoreStoreScope(seed: 1);
        var vm = Editor();

        Assert.Equal(1, vm.SelectedSolveCores.Cap);
        Assert.False(vm.IsDirty);

        // A core count is not part of the design: no undo entry, no dirty document, and the mesh is
        // NOT invalidated — it cannot change a mesh, and R-emp-8 says it cannot change an answer.
        int undoBefore = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undoBefore++; }
        Assert.Equal(0, undoBefore);

        vm.SelectedSolveCores = vm.SolveCoreChoices.First(c => c.Cap is null);
        Assert.Null(EmSolveCorePreference.Preferred);
        Assert.False(vm.IsDirty);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void ThePanelsNote_SaysItIsAMachineSetting_AndNamesTheMachinesOwnCoreCount()
    {
        using var _ = new CoreStoreScope();
        var vm = Editor();

        Assert.Contains("machine setting", vm.SolveCoresNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".cem", vm.SolveCoresNote, StringComparison.Ordinal);
        Assert.Contains(EmSolveCores.ProcessorCount.ToString(), vm.SolveCoresNote, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The run path actually reads it — a preference nothing consumes is decoration
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EmRunService_PopulatesMaxDegreeOfParallelismFromThePreference()
    {
        // EmRunService cannot be driven headlessly to a solve inside a routine gate, so the WIRING
        // is pinned by a source scan — the same fallback this suite already uses for view-model-only
        // plumbing. Without this, the whole control is inert and nothing else would notice.
        //
        // The wiring is TWO hops since the run service crossed the UI firewall (brief-cli-em-verb.md
        // R-emcli-3): the preference is read on the UI side and handed in as an argument, and the run
        // service puts that argument into the solve settings. Scanning only one hop would leave the
        // other free to be dropped — which is exactly how a preference becomes decoration.
        string runService = File.ReadAllText(RepoFile("src/Design/Layout/Em/EmRunService.cs"));
        Assert.Contains("MaxDegreeOfParallelism = EmSolveCores.Sanitise(maxCores)", runService,
                        StringComparison.Ordinal);

        string workspace = File.ReadAllText(RepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        Assert.Contains("EmSolveCorePreference.Preferred));", workspace, StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static PlanarProblem PlanarProblemFor(LayoutView view)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var res  = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 20e9);
        Assert.NotNull(res.Problem);
        return res.Problem!;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
