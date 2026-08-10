using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// <b>L7b Tier C5 — co-simulation, end to end. This is the phase gate.</b>
///
/// <para>"Extract a coupled pair from a real layout, Simulate, back-annotate into a schematic, and
/// run an HB analysis that uses it. The <c>.snp</c> must land, the <c>SnP</c> component must resolve
/// it, and the run must produce results. Re-running the EM setup and re-annotating must update the
/// same component rather than adding a second one."</para>
///
/// <para>It is the only test that proves the two halves of L7b actually meet, so it deliberately
/// drives the REAL path at every step: <c>EmRunService</c> writes a real <c>.s4p</c>,
/// <c>EmBackAnnotation</c> edits a real <c>SchematicEditModel</c>, <c>NetExtractor</c> +
/// <c>CnlWriter</c> produce a real <c>netlist.cnl</c>, and <c>SchematicRunService</c> runs it.</para>
/// </summary>
public class EmCoSimulationTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-l7b-cosim").FullName;
    public void Dispose() { try { Directory.Delete(_ws, true); } catch { /* best effort */ } }

    private string ResultsRoot => Path.Combine(_ws, "results");

    // ── the layout half ───────────────────────────────────────────────────────────────────────

    /// <summary>A real edge-coupled pair: two 1 mm lines, 20 mm long, 0.5 mm apart, on Top Copper.</summary>
    private static LayoutView CoupledPairLayout()
    {
        var v = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0,         X2 = 20_000_000, Y2 = 1_000_000 });
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 1_500_000, X2 = 20_000_000, Y2 = 2_500_000 });
        return v;
    }

    private static LayoutView SingleLineLayout()
    {
        var v = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        v.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return v;
    }

    private static EmSetup Setup() => new()
    {
        Name      = "CoupledPair",
        LayoutRef = "Pair/layout/Pair.clay",
        Frequency = new FrequencySpec("1", "5", 5, SweepKind.Linear, "GHz", "GHz"),
    };

    private static EmLayoutSource Source(LayoutView v) => new(
        "/x/Pair.clay", v, StarterTechnologies.Pcb2Layer(), LayoutUnits.DefaultDbuPerMicron);

    private EmRunResult RunEm(LayoutView layout)
    {
        var r = EmRunService.Run(Setup(), Source(layout), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, r.Status);
        return r;
    }

    // ── the schematic half ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A genuine HB testbench around a 4-port SnP: a power source into port 1, a diode across port 2
    /// (so the analysis is really nonlinear rather than a linear solve wearing an HB label), and the
    /// coupled conductor's two ends terminated.
    ///
    /// <para>Components are placed so their PINS COINCIDE rather than wiring them — pin-on-pin is a
    /// connection by this codebase's own connectivity rule, so the topology is exact and needs no
    /// wire geometry to be got right in a test fixture.</para>
    /// </summary>
    private SchematicEditModel Testbench(out EditableComponent placeholder, int ports = 4)
    {
        var m = new SchematicEditModel { SchematicDirectory = _ws };

        var snp = Place(m, SymbolKind.Snp, "SNP1", 0, 0, portCount: ports);
        placeholder = snp;

        // Every SnP pin, in world coordinates, so the rest can be hung off them exactly.
        var pins = m.PortDefsOf(snp).Select(d => m.PortWorldOf(snp, d)).ToList();
        Assert.Equal(ports, pins.Count);

        var hung = new List<string>();
        Place(m, SymbolKind.P1Tone, "P1", pins[0].Item1, pins[0].Item2 + 200);  // pin "1" at (0,-200) local
        Place(m, SymbolKind.Diode,  "D1", pins[1].Item1, pins[1].Item2 + 200);
        hung.Add("P1"); hung.Add("D1");

        for (int k = 2; k < ports; k++)
        {
            string name = $"T{k}";
            SetParam(Place(m, SymbolKind.Term, name, pins[k].Item1, pins[k].Item2 + 200), "Num", k.ToString());
            hung.Add(name);
        }

        // Ground every lower pin of the hung components.
        foreach (string name in hung)
        {
            var c = m.Components.First(x => x.InstanceName == name);
            var lower = m.PortDefsOf(c).Select(d => m.PortWorldOf(c, d)).OrderBy(p => p.Item2).Last();
            Place(m, SymbolKind.Ground, $"GND_{name}", lower.Item1, lower.Item2);
        }
        return m;
    }

    private static EditableComponent SetParam(EditableComponent c, string name, string expr)
    {
        c.Parameters.First(p => p.Name == name).Expression = expr;
        return c;
    }

    private static EditableComponent Place(
        SchematicEditModel m, SymbolKind kind, string name, double x, double y, int portCount = 0)
    {
        var c = new EditableComponent { Symbol = kind, InstanceName = name, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, portCount))
            c.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        m.Components.Add(c);
        return c;
    }

    /// <summary>Extract → write <c>netlist.cnl</c> AT THE WORKSPACE ROOT → run.
    ///
    /// <para>The location is load-bearing, not incidental: <c>CnlReader</c> resolves a relative SnP
    /// <c>File</c> against the <c>.cnl</c>'s own directory, and <c>WorkspaceViewModel.WriteNetlist</c>
    /// puts <c>netlist.cnl</c> at the workspace root — which is exactly the base
    /// <see cref="WorkspaceRefs"/> stores against (R-cpl-13). Writing it anywhere else here would
    /// test a path the application never takes.</para></summary>
    private RunResult ExtractAndRun(SchematicEditModel m, Analysis analysis)
    {
        m.Analyses.Add(analysis);

        var tb = NetExtractor.Extract(m, "TB");
        Assert.Empty(tb.Conflicts);   // a conflict here means the fixture is not the circuit claimed
        string cnl = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(tb.TestBench, "; L7b Tier C5"));
        return SchematicRunService.RunNetlist(cnl);
    }

    // ── The gate ──────────────────────────────────────────────────────────────────────────────

    /// <summary><b>Tier C5 — the L7b phase gate, in one test.</b></summary>
    [Fact]
    public void TC5_CoupledPair_Simulated_BackAnnotated_AndRunInAnHbTestbench()
    {
        // 1 — extract a coupled pair from a real layout and Simulate.
        var em = RunEm(CoupledPairLayout());

        Assert.NotNull(em.SnpPath);
        Assert.True(File.Exists(em.SnpPath!), "the .snp must land on disk");
        Assert.EndsWith(".s4p", em.SnpPath!, StringComparison.OrdinalIgnoreCase);

        // 2 — back-annotate into a schematic.
        var m = Testbench(out var placeholder);
        m.Components.Remove(placeholder);           // the gate places the SnP via back-annotation

        var ann = EmBackAnnotation.Annotate(m, em.SnpPath!, portCount: 4, "CoupledPair", _ws);
        Assert.True(ann.Created);
        Assert.NotNull(ann.Command);
        ann.Command!.Execute();

        // R-cpl-13 — workspace-relative, forward slashes, not an absolute path.
        Assert.Equal("results/CoupledPair.s4p", ann.StoredRef);

        var snp = m.Components.Single(c => c.Symbol == SymbolKind.Snp);
        Assert.Equal("4", snp.Parameters.First(p => p.Name == "NumPorts").Expression);
        Assert.Equal(4, snp.PortCount);

        // 3 — run an HB analysis that uses it. The SnP must RESOLVE the file and the run produce
        // results; a File that did not resolve surfaces here as an engine error, not as a silent pass.
        var run = ExtractAndRun(m, new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr        = "1",     // P1Tone's own default drive frequency — the HB tone grid
            ToneUnit        = "GHz",   // must contain every source, or the run is refused outright
            MaxHarmonicExpr = "3",
        });

        Assert.True(run.Status == RunStatus.Success, $"status={run.Status} error={run.StatusMessage}");
        Assert.NotEmpty(run.DataSets);
        Assert.True(run.DataSets[0].Contains("V"), "the HB run produced no node voltages");
    }

    // ── Tier G5 — the L7b-b phase gate: the same arc at THREE conductors ──────────────────────

    /// <summary>Three 1 mm lines, 20 mm long, 0.5 mm apart, on Top Copper.</summary>
    private static LayoutView ThreeConductorLayout()
    {
        var v = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron };
        for (int k = 0; k < 3; k++)
        {
            long y0 = k * 1_500_000;
            v.Shapes.Add(new RectShape
            { Layer = new(1, 0), X1 = 0, Y1 = y0, X2 = 20_000_000, Y2 = y0 + 1_000_000 });
        }
        return v;
    }

    /// <summary>
    /// <b>Tier G5 — the L7b-b phase gate, in one test.</b> Exactly L7b's own Tier C5 arc with THREE
    /// conductors instead of two: extract from a real layout, Simulate, back-annotate a 6-port
    /// <c>.snp</c>, run an HB analysis against it.
    ///
    /// <para><b><see cref="EmBackAnnotation"/> needed no change, and that is the point of running it
    /// here.</b> R-cpl-12's idempotency key is two-step — the deterministic <c>EM_&lt;setup&gt;</c>
    /// name first, then any <c>SnP</c> already reading this exact file — and the name step exists
    /// precisely so a changing port count repoints the same component instead of adding a second
    /// one. Going from four ports to six is the same operation as going from four to two, which
    /// <c>TC5_WhenThePortCountChanges_TheSameComponentIsRepointed</c> already pins; this asserts the
    /// six-port direction end to end rather than assuming it follows.</para>
    /// </summary>
    [Fact]
    public void TG5_ThreeConductors_Simulated_BackAnnotated_AndRunInAnHbTestbench()
    {
        // 1 — extract three coupled lines from a real layout and Simulate.
        var em = RunEm(ThreeConductorLayout());

        Assert.NotNull(em.SnpPath);
        Assert.True(File.Exists(em.SnpPath!), "the .snp must land on disk");
        Assert.EndsWith(".s6p", em.SnpPath!, StringComparison.OrdinalIgnoreCase);

        // D3 holds unchanged at N = 3: 2N ports, and the tline group carries a MODE AXIS rather than
        // the even/odd names, which belong to a pair (R-gen-8).
        Assert.Equal(6, em.Data!["Z0"].ComplexValues.Length);
        var tline = em.Data.Groups.First(g => em.Data.CubesIn(g).ContainsKey("Zc"));
        var cubes = em.Data.CubesIn(tline);
        Assert.Equal(2, cubes["Zc"].Rank);
        Assert.Equal("mode", cubes["Zc"].Axes[1].Name);
        Assert.Equal(3, cubes["Zc"].Axes[1].Length);
        Assert.True(cubes.ContainsKey("ModeCouplingResidual"));
        Assert.False(cubes.ContainsKey("ZcEven"));

        // R-gen-5 — the residual is surfaced as a named number, not left as an internal step.
        Assert.Contains(em.Notes ?? [], n => n.Contains("mode-coupling residual", StringComparison.Ordinal));

        // 2 — back-annotate into a schematic.
        var m = Testbench(out var placeholder, ports: 6);
        m.Components.Remove(placeholder);

        var ann = EmBackAnnotation.Annotate(m, em.SnpPath!, portCount: 6, "CoupledPair", _ws);
        Assert.True(ann.Created);
        ann.Command!.Execute();
        Assert.Equal("results/CoupledPair.s6p", ann.StoredRef);

        var snp = m.Components.Single(c => c.Symbol == SymbolKind.Snp);
        Assert.Equal("6", snp.Parameters.First(p => p.Name == "NumPorts").Expression);
        Assert.Equal(6, snp.PortCount);

        // 3 — run an HB analysis that uses it.
        var run = ExtractAndRun(m, new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr        = "1",
            ToneUnit        = "GHz",
            MaxHarmonicExpr = "3",
        });

        Assert.True(run.Status == RunStatus.Success, $"status={run.Status} error={run.StatusMessage}");
        Assert.NotEmpty(run.DataSets);
        Assert.True(run.DataSets[0].Contains("V"), "the HB run produced no node voltages");
    }

    /// <summary>
    /// <b>Tier G5 — the port-count change in the direction L7b-b introduces.</b> Editing a layout
    /// from a coupled pair to three conductors turns the artifact from an <c>.s4p</c> into an
    /// <c>.s6p</c>; the deterministic setup-name key repoints the SAME component rather than adding
    /// a second one beside it. If this ever needed a change to <see cref="EmBackAnnotation"/>, that
    /// would be a defect in L7b's idempotency key, to be fixed there rather than worked around here.
    /// </summary>
    [Fact]
    public void TG5_GoingFromTwoConductorsToThree_RepointsTheSameComponent()
    {
        var m = new SchematicEditModel { SchematicDirectory = _ws };

        var pair = RunEm(CoupledPairLayout());
        EmBackAnnotation.Annotate(m, pair.SnpPath!, 4, "CoupledPair", _ws).Command!.Execute();

        var trio = RunEm(ThreeConductorLayout());
        Assert.EndsWith(".s6p", trio.SnpPath!, StringComparison.OrdinalIgnoreCase);

        var second = EmBackAnnotation.Annotate(m, trio.SnpPath!, 6, "CoupledPair", _ws);
        Assert.False(second.Created);
        second.Command!.Execute();

        var snp = Assert.Single(m.Components.Where(c => c.Symbol == SymbolKind.Snp));
        Assert.Equal("results/CoupledPair.s6p", snp.Parameters.First(p => p.Name == "File").Expression);
        Assert.Equal("6", snp.Parameters.First(p => p.Name == "NumPorts").Expression);
        Assert.Equal(6, snp.PortCount);
    }

    /// <summary>
    /// <b>Tier C5's second half: idempotency (R-cpl-12).</b> Re-running the EM setup and
    /// re-annotating must UPDATE the same component, never add a second one beside it.
    /// </summary>
    [Fact]
    public void TC5_ReRunningAndReAnnotating_UpdatesTheSameComponent()
    {
        var em = RunEm(CoupledPairLayout());
        var m  = Testbench(out var placeholder);
        m.Components.Remove(placeholder);

        var first = EmBackAnnotation.Annotate(m, em.SnpPath!, 4, "CoupledPair", _ws);
        first.Command!.Execute();
        Assert.Single(m.Components.Where(c => c.Symbol == SymbolKind.Snp));

        // Re-run the EM setup — same geometry, same output path — and annotate again.
        var again  = RunEm(CoupledPairLayout());
        var second = EmBackAnnotation.Annotate(m, again.SnpPath!, 4, "CoupledPair", _ws);

        Assert.False(second.Created);
        Assert.Equal(first.ComponentName, second.ComponentName);
        Assert.True(second.NothingChanged,
            "nothing about the reference changed, so a re-annotation must not dirty the schematic");
        Assert.Single(m.Components.Where(c => c.Symbol == SymbolKind.Snp));
    }

    /// <summary>
    /// <b>R-cpl-12, the case a path-only key would miss.</b> Editing the layout from a coupled pair
    /// to a single line turns the artifact from an <c>.s4p</c> into an <c>.s2p</c> at a DIFFERENT
    /// path — so matching only on the file would place a second component beside the first. The
    /// deterministic name keyed on the setup is what carries the identity across that change.
    /// </summary>
    [Fact]
    public void TC5_WhenThePortCountChanges_TheSameComponentIsRepointed()
    {
        var m = new SchematicEditModel { SchematicDirectory = _ws };

        var pair = RunEm(CoupledPairLayout());
        EmBackAnnotation.Annotate(m, pair.SnpPath!, 4, "CoupledPair", _ws).Command!.Execute();

        var single = RunEm(SingleLineLayout());
        Assert.EndsWith(".s2p", single.SnpPath!, StringComparison.OrdinalIgnoreCase);

        var second = EmBackAnnotation.Annotate(m, single.SnpPath!, 2, "CoupledPair", _ws);
        Assert.False(second.Created);
        second.Command!.Execute();

        var snp = Assert.Single(m.Components.Where(c => c.Symbol == SymbolKind.Snp));
        Assert.Equal("results/CoupledPair.s2p", snp.Parameters.First(p => p.Name == "File").Expression);
        Assert.Equal("2", snp.Parameters.First(p => p.Name == "NumPorts").Expression);
        Assert.Equal(2, snp.PortCount);
    }

    /// <summary>A user who RENAMES the component must not get a duplicate either — the second match
    /// step finds it by the file it already reads.</summary>
    [Fact]
    public void TC5_ARenamedComponent_IsStillFoundByTheFileItReads()
    {
        var em = RunEm(CoupledPairLayout());
        var m  = new SchematicEditModel { SchematicDirectory = _ws };

        var first = EmBackAnnotation.Annotate(m, em.SnpPath!, 4, "CoupledPair", _ws);
        first.Command!.Execute();
        m.Components.Single(c => c.Symbol == SymbolKind.Snp).InstanceName = "CouplerModel";

        var second = EmBackAnnotation.Annotate(m, em.SnpPath!, 4, "CoupledPair", _ws);
        Assert.False(second.Created);
        Assert.Equal("CouplerModel", second.ComponentName);
        Assert.Single(m.Components.Where(c => c.Symbol == SymbolKind.Snp));
    }

    /// <summary>
    /// <b>R-cpl-13.</b> A <c>.snp</c> written OUTSIDE the workspace cannot be stored portably, so it
    /// is stored absolute and REPORTED — never silently stored in a form that will not travel.
    /// </summary>
    [Fact]
    public void TC5_AnOutsideReference_IsStoredAbsoluteAndReported()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"crf-outside-{Guid.NewGuid():N}.s4p");
        File.WriteAllText(outside, "! placeholder");
        try
        {
            var m = new SchematicEditModel { SchematicDirectory = _ws };
            var r = EmBackAnnotation.Annotate(m, outside, 4, "CoupledPair", _ws);

            Assert.True(Path.IsPathRooted(r.StoredRef));
            Assert.Contains(r.Notes, n => n.Contains("outside this workspace", StringComparison.Ordinal));
            Assert.Contains(r.Notes, n => n.Contains("will not travel", StringComparison.Ordinal));
        }
        finally { File.Delete(outside); }
    }

    /// <summary>
    /// <b>R-cpl-14.</b> Once a schematic points at the <c>.snp</c>, the R-em-20 staleness warning
    /// stops being about a file nobody references and starts saying the user's SIMULATION RESULTS
    /// came from a cross-section that no longer exists — so it is surfaced on the schematic side too,
    /// not only on the EM panel.
    /// </summary>
    [Fact]
    public void TC5_AStalenessWarning_IsCarriedOntoTheSchematicSide()
    {
        var em = RunEm(CoupledPairLayout());
        var m  = new SchematicEditModel { SchematicDirectory = _ws };

        const string stale = "The geometry hash differs from the one this .snp was computed from.";
        var r = EmBackAnnotation.Annotate(m, em.SnpPath!, 4, "CoupledPair", _ws, stalenessNote: stale);

        Assert.Contains(stale, r.Notes);
    }
}
