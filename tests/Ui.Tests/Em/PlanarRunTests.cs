// L8e Tiers 2 and 3 — the planar run through EmRunService, and D9's staleness stamp.
//
// COST NOTE, because it is why the fixture is what it is. A de-embedded frequency point costs 7.66 s
// on §10.7's own hero (L8d Tier 7), and the calibration standards are 78% of that. Tier 2 is about
// the PLUMBING — the DataSet's shape, the .snp's path, back-annotation, the provenance stamp — none
// of which is about mesh quality, so it runs on L8d's own coarse-mesh idea (cells/λ 10, no edge mesh)
// on a short line at a single frequency. The measurements that genuinely need a converged answer are
// the phase gate's, and those are Category=Benchmark.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// One planar run, shared across every assertion that only reads it. A de-embedded point costs a
/// fill and a factorisation of the DUT plus two calibration standards, so paying it once per
/// ASSERTION would put a phase-gate-sized cost in the routine tier for no extra coverage.
/// </summary>
public sealed class PlanarRunFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(
        Path.GetTempPath(), "crf-planar-run-" + Guid.NewGuid().ToString("N")[..8]);

    public EmSetup     Setup  { get; }
    public LayoutView  View   { get; }
    public EmRunResult Result { get; }

    public PlanarRunFixture()
    {
        Directory.CreateDirectory(Dir);
        Setup  = PlanarRunTests.NewSetup();
        View   = PlanarRunTests.NewLayout();
        Result = EmRunService.Run(Setup, PlanarRunTests.Source(View), Path.Combine(Dir, "results"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class PlanarRunTests(PlanarRunFixture fixture) : IClassFixture<PlanarRunFixture>, IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-planar-run-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>A short 2.9 mm-wide line with a port label at each end — the cheapest geometry that
    /// still exercises the whole path (two ports, one calibration shared between them).</summary>
    internal static LayoutView NewLayout(double lengthMm = 4.0)
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(lengthMm), Y2 = Mm(2.9) });
        v.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = 0, Y = Mm(1.45), Text = "P1", Height = Mm(0.4), IsPort = true,
        });
        v.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = Mm(lengthMm), Y = Mm(1.45), Text = "P2", Height = Mm(0.4), IsPort = true,
        });
        return v;
    }

    internal static EmLayoutSource Source(LayoutView view)
        => new("layout.clay", view, StarterTechnologies.Pcb2Layer(), Dbu);

    /// <summary>An EXPLICIT planar setup: the geometry is a plain rectangle, which kernel A accepts,
    /// so Auto would (correctly) pick A. Naming Planar is also R-res-3's own case.</summary>
    internal static EmSetup NewSetup(double fGHz = 5.0) => new()
    {
        Name         = "planar",
        LayoutRef    = "layout.clay",
        AnalysisKind = EmAnalysisKind.Planar,
        PlanarMesh   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false),
        Frequency    = new Core.Design.FrequencySpec(
            fGHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fGHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
    };

    private EmRunResult Run(EmSetup setup, LayoutView view)
    {
        Directory.CreateDirectory(_dir);
        return EmRunService.Run(setup, Source(view), Path.Combine(_dir, "results"));
    }

    // ══ Tier 2 — the run, the DataSet, the .snp, back-annotation ═════════════════════════════

    [Fact]
    public void APlanarRun_ProducesTheHouseDataSetShape_PlusOneDiagnosticsGroup()
    {
        var r = fixture.Result;

        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.Equal(EmAnalysisKind.Planar, r.Kind);
        Assert.Equal(PlanarKernel.KernelName, r.KernelName);

        var data = r.Data!;

        // R-res-6 — the SAME shape kernel A produces: an S cube plus a per-port Z0 cube.
        string sGroup = Assert.Single(data.Groups, g => data.CubesIn(g).ContainsKey("S"));
        var s = data.CubesIn(sGroup)["S"];
        Assert.Equal(3, s.Axes.Count);                       // [freq, i, j]
        Assert.Equal(2, s.Axes[1].Values.Length);
        Assert.Contains("Z0", data.CubesIn(sGroup).Keys);

        // D4 — one diagnostics group, under its OWN name so nothing collides with "tline".
        var planar = data.CubesIn(PlanarKernel.DiagnosticsGroup);
        Assert.NotEmpty(planar);
        foreach (string want in new[] { "Gamma", "Zc", "Eeff", "AttenDbPerM", "Cpul", "DeembedResidual" })
            Assert.Contains(want, planar.Keys);

        // …and it is a [freq, port] cube, not eight named scalars.
        Assert.Equal(2, planar["Zc"].Axes.Count);
        Assert.Equal("port", planar["Zc"].Axes[1].Name);
    }

    /// <summary>The "tline" group is kernel A's, and a planar run must not publish one — a Data
    /// Display trace pointing at <c>tline.Zc</c> would otherwise mean two different quantities
    /// depending on which kernel happened to run.</summary>
    [Fact]
    public void APlanarRun_PublishesNoTlineGroup()
    {
        var r = fixture.Result;
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.DoesNotContain("tline", r.Data!.Groups);
    }

    [Fact]
    public void APlanarRun_WritesTheSnpAtThePredictablePath_AndTheNpyBesideIt()
    {
        var setup = fixture.Setup;
        var r = fixture.Result;

        Assert.Equal(EmRunStatus.Ok, r.Status);

        string expected = EmRunService.ResolveSnpPath(Path.Combine(fixture.Dir, "results"), setup, 2);
        Assert.Equal(expected, r.SnpPath);
        Assert.True(File.Exists(expected), $"no .snp at {expected}");
        Assert.EndsWith(".s2p", expected, StringComparison.Ordinal);

        Assert.NotNull(r.NpyPath);
        Assert.True(File.Exists(r.NpyPath!));
    }

    /// <summary>R-cpl-12 — <c>EmBackAnnotation</c> needed NO change for kernel B. Tier G5's own rule:
    /// prove that rather than assuming it.</summary>
    [Fact]
    public void BackAnnotation_PlacesOneSnP_AndARerunUpdatesRatherThanDuplicating()
    {
        var setup = fixture.Setup;
        var r = fixture.Result;
        Assert.Equal(EmRunStatus.Ok, r.Status);

        var schematic = new SchematicEditModel();

        var first = EmBackAnnotation.Annotate(schematic, r.SnpPath!, 2, setup.Name, fixture.Dir);
        Assert.NotNull(first.Command);
        first.Command!.Execute();
        Assert.True(first.Created);

        var snp = Assert.Single(schematic.Components, c => c.Symbol == SymbolKind.Snp);
        Assert.Equal(EmBackAnnotation.ComponentNameFor(setup.Name), snp.InstanceName);

        var again = EmBackAnnotation.Annotate(schematic, r.SnpPath!, 2, setup.Name, fixture.Dir);
        again.Command?.Execute();
        Assert.Single(schematic.Components, c => c.Symbol == SymbolKind.Snp);
    }

    /// <summary>R-res-1 — the choice and its reason ride out on EVERY run, chosen or refused.</summary>
    [Fact]
    public void TheKernelChoice_AndItsReason_AreInTheRunsNotes()
    {
        Assert.Contains(fixture.Result.Notes ?? [],
                        n => n.Contains("explicitly", StringComparison.Ordinal));
    }

    /// <summary>§0's third finding, surfaced rather than published as if it were dispersion.</summary>
    [Fact]
    public void TheQuasiStaticZcCaveat_IsInTheRunsNotes()
    {
        Assert.Contains(fixture.Result.Notes ?? [], n =>
            n.Contains("QUASI-STATIC", StringComparison.Ordinal) &&
            n.Contains("+6.3% at 20 GHz", StringComparison.Ordinal));
    }

    [Fact]
    public void APlanarRunWithNoPortLabels_IsRefusedByName_AndNothingIsWritten()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(6), Y2 = Mm(2.9) });

        var r = Run(NewSetup(), view);

        Assert.Equal(EmRunStatus.Refused, r.Status);
        Assert.Contains("Port tool", r.Error!, StringComparison.Ordinal);
        Assert.Null(r.SnpPath);
    }

    // ══ Tier 3 — D9/R-res-9: the stamp covers geometry, mesh settings AND ports ═══════════════
    //
    // The MECHANISM is the three hashes, so the three-way independence matrix is asserted on them
    // directly — no solve, and a far sharper test than watching a warning string appear. The WIRING
    // (compare before overwriting, on the real run path) then needs exactly one end-to-end case.

    private static PlanarProblem PlanarProblemOf(LayoutView view)
    {
        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 5e9);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    private static IReadOnlyList<PlanarPort> PortsOf(LayoutView view, EmSetup setup)
    {
        var r = EmPortExtraction.Extract(view.Shapes, PlanarProblemOf(view), Dbu, setup.ResolvePortZ0);
        Assert.True(r.Ok, r.Refusal);
        return r.Ports;
    }

    /// <summary>
    /// D9's headline. Without planar hashes a planar run would write a stamp that CANNOT go stale —
    /// worse than no stamp, because the §10.8 warning would stay silent while a schematic went on
    /// reading a result the layout no longer produces. Each of the three trips it INDEPENDENTLY, and
    /// the message says WHICH moved, which is the whole reason there are three rather than one.
    /// </summary>
    [Theory]
    [InlineData("geometry")]
    [InlineData("mesh")]
    [InlineData("ports")]
    public void EditingTheGeometryTheMeshOrAPort_IndependentlyTripsTheStamp(string what)
    {
        var setup = NewSetup();
        var view  = NewLayout();

        var problem = PlanarProblemOf(view);
        var ports   = PortsOf(view, setup);
        var mesh    = setup.PlanarMesh;

        Directory.CreateDirectory(_dir);
        string snp = Path.Combine(_dir, "stamped.s2p");
        File.WriteAllLines(snp,
            EmSnpProvenance.BuildHeader(problem, mesh, ports, "planar", "layout.clay", DateTimeOffset.Now)
                           .Select(l => "! " + l)
                           .Append("# HZ S RI R 50"));

        // Nothing changed: nothing to warn about.
        Assert.Null(EmSnpProvenance.DescribeStaleness(snp, problem, mesh, ports));

        switch (what)
        {
            case "geometry": problem = PlanarProblemOf(NewLayout(9.0)); break;
            case "mesh":     mesh    = mesh with { CellsPerWavelength = 12 }; break;
            case "ports":
                setup.PortZ0s = [new Complex(75, 0), new Complex(50, 0)];
                ports = PortsOf(view, setup);
                break;
        }

        string? stale = EmSnpProvenance.DescribeStaleness(snp, problem, mesh, ports);
        Assert.NotNull(stale);

        string expectedPhrase = what switch
        {
            "geometry" => "the layout geometry",
            "mesh"     => "the mesh settings",
            _          => "the ports",
        };
        Assert.Contains(expectedPhrase, stale!, StringComparison.Ordinal);

        // …and ONLY that one is named, which is what makes the three hashes worth having.
        foreach (string other in new[] { "the layout geometry", "the mesh settings", "the ports" })
            if (other != expectedPhrase)
                Assert.DoesNotContain(other, stale!, StringComparison.Ordinal);
    }

    /// <summary>Moving a port LABEL to the other end of the conductor changes the answer completely
    /// and changes nothing the geometry or mesh hashes can see — so position is in the port hash.</summary>
    [Fact]
    public void MovingAPortLabel_TripsThePortHash()
    {
        var setup = NewSetup();
        var a = PortsOf(NewLayout(), setup);

        var moved = NewLayout();
        var label = moved.Shapes.OfType<LabelShape>().First(l => l.Text == "P1");
        label.X = Mm(2.0);                       // still on the metal, now mid-span
        label.Y = Mm(0);                         //   …and against the low-y edge, a different side

        var b = PortsOf(moved, setup);

        Assert.NotEqual(EmSnpProvenance.PortHash(a), EmSnpProvenance.PortHash(b));
    }

    /// <summary>The WIRING — R-em-20's "compare BEFORE overwriting", on the real run path. One
    /// end-to-end case, because the matrix above already pins the mechanism.</summary>
    [Fact]
    public void ARunAgainstAnSnpFromDifferentGeometry_WarnsBeforeOverwritingIt()
    {
        var setup = NewSetup();
        Assert.Equal(EmRunStatus.Ok, Run(setup, NewLayout()).Status);

        var second = Run(setup, NewLayout(9.0));

        Assert.Equal(EmRunStatus.Ok, second.Status);
        Assert.Contains(second.Warnings, w =>
            w.Contains("was written from a different setup", StringComparison.Ordinal) &&
            w.Contains("the layout geometry", StringComparison.Ordinal));
    }

    /// <summary>A third-party or hand-written Touchstone carries no circuitRF stamp, and that is not
    /// staleness — there is simply nothing to compare against.</summary>
    [Fact]
    public void AnUnstampedSnp_IsNotReportedAsStale()
    {
        var setup   = NewSetup();
        var view    = NewLayout();
        var problem = PlanarProblemOf(view);
        var ports   = PortsOf(view, setup);

        Directory.CreateDirectory(_dir);
        string snp = Path.Combine(_dir, "third-party.s2p");
        File.WriteAllText(snp, "! someone else's file\n# HZ S RI R 50\n");

        Assert.Null(EmSnpProvenance.DescribeStaleness(snp, problem, setup.PlanarMesh, ports));
    }
}
