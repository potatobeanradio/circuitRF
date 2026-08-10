// Tier R — the run, the .snp artifact, and R-em-20's staleness detection
// (brief-L6-L7-em-ui.md §7).
//
// The staleness pair is the one that matters: "a cosmetic layout edit must NOT report staleness,
// and a change that genuinely moves the cross-section must ALWAYS report it" is a claim about the
// HASH's subject, and hashing the raw file bytes would fail the first half while hashing nothing
// would fail the second.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-emrun-" + Guid.NewGuid().ToString("N")[..12]);

    private string ResultsRoot => Path.Combine(_dir, "results");

    public EmRunTests() => Directory.CreateDirectory(ResultsRoot);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView Hero(long widthDbu = 2_900_000, long lengthDbu = 20_000_000)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = lengthDbu, Y2 = widthDbu });
        return view;
    }

    private static EmLayoutSource Source(LayoutView view)
        => new("/x/a.clay", view, StarterTechnologies.Pcb2Layer(), Dbu);

    private static EmSetup Setup(string name = "hero") => new()
    {
        Name      = name,
        LayoutRef = "a.clay",
        // Three points keeps the test fast; R-mom-11 means the sweep length costs nothing anyway.
        Frequency = new FrequencySpec("1", "10", 3, SweepKind.Linear, "GHz", "GHz"),
    };

    // ── The end-to-end gate ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ARun_ProducesSAndTheTlineGroup_WritesAnNpyAndAnSnp()
    {
        var result = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);

        Assert.Equal(EmRunStatus.Ok, result.Status);
        Assert.Null(result.Error);
        Assert.NotNull(result.Data);

        // R-em-18: no new result type, and the eight tline quantities are what make a wrong answer
        // diagnosable — they must reach Data Display rather than being filtered out on the way.
        var groups = result.Data!.Groups;
        Assert.Contains(groups, g => result.Data.CubesIn(g).ContainsKey("S"));
        Assert.Contains(groups, g => result.Data.CubesIn(g).ContainsKey("Z0"));

        var tline = groups.FirstOrDefault(g => result.Data.CubesIn(g).ContainsKey("Zc"));
        Assert.NotNull(tline);
        var cubes = result.Data.CubesIn(tline!);
        foreach (var name in new[] { "Zc", "Gamma", "Eeff", "AttenDbPerM", "Rpul", "Lpul", "Gpul", "Cpul" })
            Assert.True(cubes.ContainsKey(name), $"the tline group must carry {name}");

        Assert.NotNull(result.NpyPath);
        Assert.True(File.Exists(result.NpyPath!), $"expected an .npy at {result.NpyPath}");

        Assert.NotNull(result.SnpPath);
        Assert.True(File.Exists(result.SnpPath!), $"expected an .snp at {result.SnpPath}");
        Assert.EndsWith(".s2p", result.SnpPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSnpPath_IsPredictable_AndStableAcrossRuns()
    {
        // R-em-19: a schematic's SnP reference has to survive a re-run, which it only does if the
        // path is derived rather than minted.
        var first  = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);
        var second = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);

        Assert.Equal(first.SnpPath, second.SnpPath);
        Assert.Equal(Path.Combine(ResultsRoot, "hero.s2p"), first.SnpPath);
        Assert.Single(Directory.GetFiles(ResultsRoot, "*.s2p"));
    }

    [Fact]
    public void AnOutputPathOverride_IsHonoured_WithoutDoublingTheExtension()
    {
        var setup = Setup();
        setup.SnpOutputPathOverride = "custom/line.s2p";
        var result = EmRunService.Run(setup, Source(Hero()), ResultsRoot);

        Assert.Equal(EmRunStatus.Ok, result.Status);
        Assert.Equal(Path.Combine(ResultsRoot, "custom", "line.s2p"), result.SnpPath);
        Assert.True(File.Exists(result.SnpPath!));
    }

    // ── R-em-20: provenance, and staleness detected rather than silently believed ────────────

    [Fact]
    public void TheSnpCarriesAProvenanceStamp()
    {
        var result = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);
        var stamp = EmSnpProvenance.TryRead(result.SnpPath!);

        Assert.NotNull(stamp);
        Assert.NotEmpty(stamp!.GeometryHash);
        Assert.NotEmpty(stamp.MeshHash);
        Assert.NotEmpty(stamp.PortHash);

        string text = File.ReadAllText(result.SnpPath!);
        Assert.Contains("circuitRF-EM layout: a.clay", text, StringComparison.Ordinal);
        Assert.Contains("circuitRF-EM setup: hero", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReRunningAfterACosmeticLayoutEdit_DoesNotWarn()
    {
        // The whole reason R-em-20 hashes the extracted EmProblem rather than the file bytes:
        // adding a silkscreen label does not move the cross-section, so it is not staleness.
        var view = Hero();
        var first = EmRunService.Run(Setup(), Source(view), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, first.Status);

        view.Shapes.Add(new LabelShape
        { Layer = new(5, 0), X = 1_000_000, Y = 9_000_000, Text = "R1", Height = 1_000_000 });

        var second = EmRunService.Run(Setup(), Source(view), ResultsRoot);

        Assert.Equal(EmRunStatus.Ok, second.Status);
        Assert.DoesNotContain(second.Warnings, w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AHandEditedStaleSnp_DoesWarn_NamingWhatChanged()
    {
        var first = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, first.Status);

        // The layout's width really did change, so whatever schematic references that .snp has been
        // reading s-parameters for a different line.
        var second = EmRunService.Run(Setup(), Source(Hero(widthDbu: 1_500_000)), ResultsRoot);

        Assert.Equal(EmRunStatus.Ok, second.Status);
        var warn = Assert.Single(second.Warnings,
            w => w.Contains("different setup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("cross-section geometry", warn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangingAMeshSettingOrAPortZ0_IsReportedSeparatelyFromGeometry()
    {
        var setup = Setup();
        Assert.Equal(EmRunStatus.Ok, EmRunService.Run(setup, Source(Hero()), ResultsRoot).Status);

        var meshChanged = setup.Clone();
        meshChanged.Mesh = meshChanged.Mesh with { TruncationHeights = 40 };
        var r2 = EmRunService.Run(meshChanged, Source(Hero()), ResultsRoot);
        Assert.Contains(r2.Warnings, w => w.Contains("mesh settings", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r2.Warnings, w => w.Contains("cross-section geometry", StringComparison.OrdinalIgnoreCase));

        var portChanged = meshChanged.Clone();
        portChanged.Port1Z0 = new Complex(75, 0);
        var r3 = EmRunService.Run(portChanged, Source(Hero()), ResultsRoot);
        Assert.Contains(r3.Warnings, w => w.Contains("reference impedances", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AThirdPartyTouchstoneWithNoStamp_IsNotReportedAsStale()
    {
        // Absence of a stamp is not staleness — there is simply nothing to compare against.
        var snpPath = Path.Combine(ResultsRoot, "hero.s2p");
        File.WriteAllText(snpPath, "! written by someone else\n# GHz S RI R 50\n1 0 0 0 0 0 0 0 0\n");

        var result = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, result.Status);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("different setup", StringComparison.OrdinalIgnoreCase));
    }

    // ── Refusals reach the caller as refusals, not as crashes ────────────────────────────────

    [Fact]
    public void AMissingLayout_IsRefusedWithAReason_AndWritesNothing()
    {
        var result = EmRunService.Run(Setup(), null, ResultsRoot);

        Assert.Equal(EmRunStatus.NoLayout, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("a.clay", result.Error!, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(ResultsRoot));
    }

    [Fact]
    public void AGeometryTheExtractorRefuses_ComesBackAsARefusal_NotAnException()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new CircleShape
        { Layer = new(1, 0), Cx = 5_000_000, Cy = 5_000_000, R = 1_000_000 });

        var result = EmRunService.Run(Setup(), Source(view), ResultsRoot);

        Assert.Equal(EmRunStatus.Refused, result.Status);
        Assert.Empty(Directory.GetFiles(ResultsRoot));

        // ── L8e CHANGED WHICH REFUSAL THIS IS, and the change is an improvement worth pinning ──
        //
        // Before L8e a setup defaulted to CrossSection, so a circle came back as "…circle…" from the
        // cross-section extractor and that was the end of it. The default is Auto now: A refuses the
        // circle, B's extractor ACCEPTS the geometry — a disc is a perfectly good planar conductor —
        // and the run stops at the next real problem, which is that nobody placed a port.
        //
        // So the ERROR is now the actionable one (place ports) and the reason A was passed over is in
        // the NOTES. Both halves are asserted: dropping either would leave a user unable to tell
        // either what to do or why the solver they expected was not the one that ran.
        Assert.Contains("port", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Port tool", result.Error!, StringComparison.Ordinal);
        Assert.Contains(result.Notes ?? [], n => n.Contains("circle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>L7b.</b> A SYMMETRIC coupled pair now runs end to end and lands a 4-port. This test
    /// previously asserted the kernel refused it by pointing at L7b; that refusal has been NARROWED
    /// (R-cpl-5), not deleted — see the asymmetric case below.
    /// </summary>
    [Fact]
    public void ASymmetricCoupledPair_RunsAndLandsAFourPort()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0,         X2 = 20_000_000, Y2 = 1_000_000 });
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 1_500_000, X2 = 20_000_000, Y2 = 2_500_000 });

        var result = EmRunService.Run(Setup(), Source(view), ResultsRoot);

        Assert.Equal(EmRunStatus.Ok, result.Status);
        Assert.NotNull(result.Readback);
        Assert.Equal(2, result.Readback!.Conductors.Count);

        // D3: 2N ports for N conductors.
        var z0 = result.Data!["Z0"];
        Assert.Equal(4, z0.ComplexValues.Length);

        // The tline group carries PER-MODE pairs now (D4 — no new result type, the same group).
        var tline = result.Data.Groups.First(g => result.Data.CubesIn(g).ContainsKey("ZcEven"));
        Assert.True(result.Data.CubesIn(tline).ContainsKey("ZcOdd"));
        Assert.True(result.Data.CubesIn(tline).ContainsKey("EeffEven"));
        Assert.True(result.Data.CubesIn(tline).ContainsKey("EeffOdd"));

        // …and the .snp that lands is a 4-port.
        Assert.NotNull(result.SnpPath);
        Assert.EndsWith(".s4p", result.SnpPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.SnpPath!));
    }

    /// <summary>
    /// <b>UPDATED by L7b-b — not loosened.</b> This asserted that an ASYMMETRIC pair is refused by
    /// the kernel pointing at L7b-b. That is what L7b-b delivers, so the pair now runs — and it must
    /// run all the way to a real 4-port <c>.snp</c>, which is the half of the claim worth keeping.
    /// The other half — <b>a kernel refusal is the KERNEL's, not the extractor's</b> — moved to the
    /// case that is still unsupported: R-gen-9's conductor ceiling, below.
    /// </summary>
    [Fact]
    public void AnAsymmetricPair_NowRuns_AndLandsARealFourPort()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0,         X2 = 20_000_000, Y2 = 1_000_000 });
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 1_500_000, X2 = 20_000_000, Y2 = 4_000_000 });

        var result = EmRunService.Run(Setup(), Source(view), ResultsRoot);

        Assert.Equal(EmRunStatus.Ok, result.Status);
        Assert.Equal(4, result.Data!["Z0"].ComplexValues.Length);
        Assert.EndsWith(".s4p", result.SnpPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.SnpPath!));
    }

    /// <summary>
    /// <b>R-gen-9 — the capability boundary that replaced the L7b-b refusals, and it is still the
    /// KERNEL's.</b> The extractor's job is to produce the problem, not to grade it: seventeen
    /// parallel strips extract perfectly cleanly and are refused one layer down, by name, with the
    /// number and what bounds it.
    /// </summary>
    [Fact]
    public void OverTheConductorCeiling_IsRefusedByTheKERNEL_NotByTheExtractor()
    {
        int n = QuasiStaticKernel.MaxSignalConductors + 1;
        var view = new LayoutView { DbuPerMicron = Dbu };
        for (int k = 0; k < n; k++)
        {
            long y0 = k * 800_000;
            view.Shapes.Add(new RectShape
            { Layer = new(1, 0), X1 = 0, Y1 = y0, X2 = 20_000_000, Y2 = y0 + 400_000 });
        }

        var result = EmRunService.Run(Setup(), Source(view), ResultsRoot);

        Assert.Equal(EmRunStatus.Refused, result.Status);
        Assert.Contains(n.ToString(), result.Error!, StringComparison.Ordinal);
        Assert.Contains(QuasiStaticKernel.MaxSignalConductors.ToString(), result.Error!,
                        StringComparison.Ordinal);
        Assert.Contains("dense boundary-element solve", result.Error!, StringComparison.Ordinal);
        Assert.NotNull(result.Readback);   // extraction succeeded — the readback is still useful
    }

    // ── Tier A through the run path ──────────────────────────────────────────────────────────

    [Fact]
    public void TheFiftyOhmHero_LandsAtFiftyOhms_ThroughTheWholeRunPath()
    {
        var result = EmRunService.Run(Setup(), Source(Hero()), ResultsRoot);
        Assert.Equal(EmRunStatus.Ok, result.Status);

        var tline = result.Data!.Groups.First(g => result.Data.CubesIn(g).ContainsKey("Zc"));
        var zc = result.Data.CubesIn(tline)["Zc"];

        double z0 = zc.ComplexValues[0].Magnitude;
        double rel = Math.Abs(z0 - 50.0) / 50.0;
        Assert.True(rel <= 0.03, $"hero Z₀ through the run path: got {z0:G6} Ω, off by {rel:P3}");

        var eeff = result.Data.CubesIn(tline)["Eeff"];
        Assert.InRange(eeff.RealValues[0], 3.0, 3.6);
    }
}
