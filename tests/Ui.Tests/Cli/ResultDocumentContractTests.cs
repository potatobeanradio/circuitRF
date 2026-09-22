// ================================================================
//  ResultDocumentContractTests.cs — brief-automation-9-result-documents.md.
//
//  Twelve requirements about what a result document SAYS and what it costs to receive. Nothing in
//  this file asserts about a computed value; every assertion is about the reporting of one.
//
//  The two gates the brief names beyond its per-requirement ones are here as well:
//    • EveryCubeARunEmits_CarriesAUnit — R-aut9-3 cannot recur in a quantity nobody thought about.
//    • OneRepresentativeDocument_StaysUnderItsByteBudget — a regression that reintroduces the
//      duplicated diagnostic text, or an un-narrowable payload, fails here rather than months later
//      on somebody's token bill.
//
//  Runs the BUILT CLI as a process, for CliStructuredOutputTests' reason (a nested `dotnet run`
//  inside `dotnet test` deadlocks on this repository's build locks) — and, where a finding needs a
//  result no committed fixture produces, in process against a hand-built DataSet.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class ResultDocumentContractTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-aut9-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    // ── R-aut9-1. The extension picks the format, or the request is refused ──

    /// <summary>
    /// <c>-o out.npy</c> writes a <c>.npy</c> — asserted by reading it back through the importer the
    /// GUI reads a result file with, not by looking at the first bytes. Before this, every
    /// <c>sparam -o</c> wrote Touchstone whatever it was called, and the caller learned about it
    /// from a later reader's accurate "expected magic \x93NUMPY, got 21-20-4E-4F-54-45".
    /// </summary>
    [Fact]
    public void Sparam_HonoursTheExtension_AndTheFileIsReallyThatFormat()
    {
        Directory.CreateDirectory(_tmp);
        string npy = Path.Combine(_tmp, "hero1.npy");

        var (exit, stdout, stderr) = RunCli("sparam", "testdata/Hero1/hero1.cnl", "-o", npy, "--json");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);

        var outputs = JsonDocument.Parse(stdout).RootElement.GetProperty("outputs").EnumerateArray()
            .Select(o => (o.GetProperty("kind").GetString(), o.GetProperty("path").GetString())).ToArray();
        Assert.Equal([("npy", npy)], outputs);

        var back = DataSetImporter.Import(npy).DataSet;
        Assert.True(back.Contains("S"));
    }

    /// <summary>The default with no <c>-o</c> is unchanged: a Touchstone named after the netlist.</summary>
    [Fact]
    public void Sparam_StillWritesTouchstone_ForATouchstoneExtension()
    {
        Directory.CreateDirectory(_tmp);
        string snp = Path.Combine(_tmp, "hero1.s4p");
        var (exit, stdout, _) = RunCli("sparam", "testdata/Hero1/hero1.cnl", "-o", snp, "--json");

        Assert.Equal(0, exit);
        Assert.Equal("touchstone",
            JsonDocument.Parse(stdout).RootElement.GetProperty("outputs")[0].GetProperty("kind").GetString());
        // An option line and no header note: a uniform real reference has nothing to say about
        // itself that the option line does not already say (R-aut9-2).
        Assert.StartsWith("#", File.ReadAllText(snp).TrimStart());
    }

    /// <summary>An extension naming no format is a refusal listing the ones it writes — never a
    /// Touchstone under that name.</summary>
    [Fact]
    public void Sparam_RefusesAnExtensionItCannotWrite_AndWritesNothing()
    {
        Directory.CreateDirectory(_tmp);
        string weird = Path.Combine(_tmp, "hero1.parquet");

        var (exit, stdout, _) = RunCli("sparam", "testdata/Hero1/hero1.cnl", "-o", weird, "--json");

        Assert.Equal(1, exit);
        var doc = JsonDocument.Parse(stdout).RootElement;
        Assert.Contains("sparam.export.unsupported-format", Ids(doc));
        Assert.Empty(doc.GetProperty("outputs").EnumerateArray());
        Assert.False(File.Exists(weird));
    }

    // ── R-aut9-2. Per-port Z0 is reported as itself ──────────────────────────

    /// <summary>
    /// A two-port whose second port is 12 Ω. The header used to say <c>Port 2: Z0 = &lt;50; 0&gt;</c>
    /// — the uniform value printed once per port, which is a positive claim about ports nobody had
    /// looked at — and <c>read</c> then handed back a <c>Z0</c> cube of <c>[[50,0],[50,0]]</c> for a
    /// matrix generalized w.r.t. [50, 12].
    ///
    /// <para><b>Nothing is renormalized</b> (the brief's own premise: the solve is right and the
    /// reporting is wrong), so this asserts the S entries are byte-for-byte what the un-narrowed run
    /// reported, and that the per-port references now survive the file in both directions.</para>
    /// </summary>
    [Fact]
    public void Touchstone_ReportsPerPortZ0_AndReadsItBack()
    {
        Directory.CreateDirectory(_tmp);
        string cnl = Path.Combine(_tmp, "nonuniform.cnl");
        File.WriteAllText(cnl, """
            Port:Term1   N1 0   Num=1 Z=50 Ohm
            Port:Term2   N2 0   Num=2 Z=12 Ohm
            R:R1  N1 N2  R=25 Ohm
            analysis SP   type=sparam  start=1 GHz stop=1 GHz step=1 GHz
            """);
        string snp = Path.Combine(_tmp, "nonuniform.s2p");

        var (exit, runDoc, stderr) = RunCli("sparam", cnl, "-o", snp, "--json", "--only", "S,Z0");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);

        string text = File.ReadAllText(snp);
        Assert.Contains("!   Port 1: Z0 = <50; 0>", text);
        Assert.Contains("!   Port 2: Z0 = <12; 0>", text);
        // The false claim that used to head every non-strict export, regardless of the actual Z0.
        Assert.DoesNotContain("Original data had complex Z0", text);

        // `run` was already right about the ports; `read` was not.
        var fromRun = Cubes(JsonDocument.Parse(runDoc).RootElement);
        Assert.Equal(50.0, fromRun.GetProperty("Z0").GetProperty("values")[0][0].GetDouble());
        Assert.Equal(12.0, fromRun.GetProperty("Z0").GetProperty("values")[1][0].GetDouble());

        var fromRead = Cubes(JsonDocument.Parse(
            RunCli("read", snp, "--json", "--only", "S,Z0").StdOut).RootElement);
        Assert.Equal(50.0, fromRead.GetProperty("Z0").GetProperty("values")[0][0].GetDouble());
        Assert.Equal(12.0, fromRead.GetProperty("Z0").GetProperty("values")[1][0].GetDouble());

        // …and no S entry moved: the reporting changed, the computation did not.
        var ran  = fromRun.GetProperty("S").GetProperty("values");
        var read = fromRead.GetProperty("S").GetProperty("values");
        for (int i = 0; i < ran.GetArrayLength(); i++)
            Assert.Equal(ran[i][0].GetDouble(), read[i][0].GetDouble(), 9);

        Assert.Equal([new Complex(50, 0), new Complex(12, 0)],
                     RfCore.TouchstoneIO.ReadFile(snp).Z0PerPort!);

        static JsonElement Cubes(JsonElement doc)
            => doc.GetProperty("result").GetProperty("groups").GetProperty("");
    }

    /// <summary>An ordinary uniform-50 Ω export gains no note at all — the block is conditional
    /// now, where it used to run on every non-strict write and announce a complex Z0 whether or not
    /// there was one.</summary>
    [Fact]
    public void Touchstone_SaysNothingAboutZ0_WhenThereIsNothingToSay()
    {
        Directory.CreateDirectory(_tmp);
        string snp = Path.Combine(_tmp, "hero1.s4p");
        RunCli("sparam", "testdata/Hero1/hero1.cnl", "-o", snp);

        string text = File.ReadAllText(snp);
        Assert.DoesNotContain("NOTE:", text);
        Assert.DoesNotContain("Port 1: Z0", text);
    }

    /// <summary>A complex uniform reference survives a round trip, which the option line's single
    /// real R cannot carry on its own.</summary>
    [Fact]
    public void Touchstone_KeepsAComplexReference_AcrossARoundTrip()
    {
        Directory.CreateDirectory(_tmp);
        string path = Path.Combine(_tmp, "complex.s1p");

        var snp = new RfCore.SNP([1e9], [Mat1(new Complex(0.1, 0.2))],
                                 RfCore.MatrixType.S, RfCore.MatrixFormat.RI, new Complex(50, -10));
        RfCore.TouchstoneIO.WriteFile(snp, path);

        Assert.Contains("reference impedance is complex: <50; -10>", File.ReadAllText(path));
        Assert.Equal(new Complex(50, -10), RfCore.TouchstoneIO.ReadFile(path).Z0);
    }

    // ── R-aut9-3. Every cube says what its numbers are in ────────────────────

    /// <summary>
    /// <b>The brief's own gate.</b> Every cube a run emits carries a unit, so the split that put
    /// <c>Efficiency</c> at 65.84 and <c>MXE_Eff</c> at 0.7087 in one document cannot recur in a
    /// quantity nobody thought about. A cube circuitRF genuinely cannot speak for says
    /// <c>unknown</c> in as many words, which is an answer; an empty string would not be.
    /// </summary>
    [Theory]
    [InlineData("sparam|testdata/Hero1/hero1.cnl|-o|<TMP>/hero1.s4p")]
    [InlineData("dc|testdata/Hero2/hero2.cnl")]
    [InlineData("hb|testdata/Hero2/hero2.cnl")]
    [InlineData("lp|testdata/Hero3/hero3.cnl|--all")]
    [InlineData("lpp|testdata/Hero3B/hero3B_at_compression.cnl|--out-grid|<TMP>/found.gam|--all")]
    [InlineData("read|testdata/Hero1/potentially_unstable_amp.s2p")]
    public void EveryCubeARunEmits_CarriesAUnit(string argLine)
    {
        Directory.CreateDirectory(_tmp);
        var (_, stdout, stderr) = RunCli([.. Args(argLine), "--json"]);
        output.WriteLine(stderr);

        var result = JsonDocument.Parse(stdout).RootElement.GetProperty("result");
        int seen = 0;

        foreach (var group in result.GetProperty("groups").EnumerateObject())
            foreach (var cube in group.Value.EnumerateObject())
            {
                seen++;
                Assert.True(cube.Value.TryGetProperty("unit", out var unit),
                            $"cube '{group.Name}.{cube.Name}' carries no unit");
                Assert.False(string.IsNullOrEmpty(unit.GetString()),
                             $"cube '{group.Name}.{cube.Name}' has an empty unit");
            }

        // …and the shape says the same thing about the same cubes, since a caller may read either.
        foreach (var group in result.GetProperty("shape").GetProperty("groups").EnumerateObject())
            foreach (var cube in group.Value.EnumerateObject())
                Assert.False(string.IsNullOrEmpty(cube.Value.GetProperty("unit").GetString()),
                             $"shape of '{group.Name}.{cube.Name}' has an empty unit");

        Assert.True(seen > 0, "this run emitted no cubes at all, so the gate measured nothing");
    }

    /// <summary>
    /// The two spellings of one ratio, told apart by the thing that distinguishes them. An enriched
    /// run publishes <c>Efficiency</c> and <c>PAE</c> as percentages; a pursuit's own scalar is a
    /// fraction. Nothing is rescaled — R-aut9-3 is about the reporting — so the unit beside each
    /// number is what a client reads instead of inferring.
    /// </summary>
    [Fact]
    public void ADimensionlessRatio_SaysWhichConventionItIsIn()
    {
        var doc = JsonDocument.Parse(RunCli("lp", "testdata/Hero3/hero3.cnl", "--json").StdOut).RootElement;
        var columns = doc.GetProperty("result").GetProperty("summary").GetProperty("grid")
                         .GetProperty("columns").EnumerateArray()
                         .ToDictionary(c => c.GetProperty("column").GetString()!, c => c);

        // Enriched: the cubes are already percentages, so the console scales by 1 and both units
        // agree. The unit is on the RAW value, which is the half that used to be missing.
        foreach (string ratio in (string[])["Efficiency", "PAE"])
        {
            Assert.Equal("%", columns[ratio].GetProperty("unit").GetString());
            Assert.Equal(1.0, columns[ratio].GetProperty("consoleScale").GetDouble());
        }
        Assert.Equal("dBm", columns["Pout"].GetProperty("unit").GetString());
    }

    /// <summary>
    /// The pursuit's MXE scalar: a fraction the console prints as a percentage. The two used to be
    /// reported under one field naming the console's unit — <c>%</c> beside 0.7087 — and a client
    /// that believed the label reported 0.7% for a 70.87% amplifier.
    /// </summary>
    [Fact]
    public void ThePursuitOptimum_LabelsTheValueItSitsBeside_NotTheOneTheConsolePrints()
    {
        Directory.CreateDirectory(_tmp);
        var doc = JsonDocument.Parse(RunCli(
            "lpp", "testdata/Hero3B/hero3B_at_compression.cnl",
            "--out-grid", Path.Combine(_tmp, "found.gam"), "--json").StdOut).RootElement;

        var optima = doc.GetProperty("result").GetProperty("summary").GetProperty("optima");
        var mxe = optima.GetProperty("mxe");

        Assert.Equal("1",   mxe.GetProperty("unit").GetString());          // the value: a fraction
        Assert.Equal(100.0, mxe.GetProperty("consoleScale").GetDouble());
        Assert.Equal("%",   mxe.GetProperty("consoleUnit").GetString());   // what the table prints

        var mxp = optima.GetProperty("mxp");
        Assert.Equal("dBm", mxp.GetProperty("unit").GetString());
        Assert.Equal("dBm", mxp.GetProperty("consoleUnit").GetString());
        Assert.Equal(1.0,   mxp.GetProperty("consoleScale").GetDouble());
    }

    /// <summary>A cube's own stated unit survives the file it is written to and read back from —
    /// otherwise the enriched <c>PAE</c> would come back looking like the engine's fraction.</summary>
    [Fact]
    public void ACubesUnit_SurvivesTheNpyRoundTrip()
    {
        Directory.CreateDirectory(_tmp);
        string npy = Path.Combine(_tmp, "units.npy");

        var ds = new DataSet();
        ds.Add("PAE", new DataCube([new Axis("pinStep", [0.0, 1.0])], [12.0, 34.0]) { Unit = "%" });
        DataSetExporter.Export(ds, npy, ExportFormat.Npy, new ExportOptions(Format: ExportFormat.Npy));

        var back = DataSetImporter.Import(npy).DataSet;
        Assert.Equal("%", back.Cubes["PAE"].Unit);
        Assert.Equal("%", ResultUnits.For("PAE", back.Cubes["PAE"]));

        // …and without one, the name vocabulary answers, which is what keeps the engine from having
        // to annotate every cube it makes.
        Assert.Equal("1", ResultUnits.For("PAE"));
        Assert.Equal("dBm", ResultUnits.For("Pout_dBm"));
        Assert.Equal("Ohm", ResultUnits.For("SP1.Z0"));
        Assert.Equal(ResultUnits.Unknown, ResultUnits.For("MyOwnMeasurement"));
    }

    // ── R-aut9-4/5/6. What the shape of a loadpull result says about the run ──

    /// <summary>
    /// R-aut9-4. Every converged drive step at the engine's floor is a device that delivered nothing
    /// — the run that persuaded a client a working component was broken. Driven against a hand-built
    /// result rather than a bench, because the fixture would have to be a deliberately mis-wired
    /// design and the finding is about reading the cubes, not about producing them.
    /// </summary>
    [Fact]
    public void AnInertDevice_IsReported_RatherThanReturningTheFloorSentinelInSilence()
    {
        var findings = LoadpullRunFindings.For(Grid(converged: true, poutW: 0.0));
        Assert.Contains(findings, f => f.Kind == LoadpullFindingKind.DeviceInert);

        // …and an ordinary run says nothing at all.
        Assert.Empty(LoadpullRunFindings.For(Grid(converged: true, poutW: 0.5)));
    }

    /// <summary>
    /// R-aut9-5. A run that converged nowhere reports the counts and the stop distribution the
    /// stderr log already carried — <c>status</c> alone is not a diagnosis.
    /// </summary>
    [Fact]
    public void ARunThatConvergedNowhere_ReportsItsCountsAndStopCodes()
    {
        var findings = LoadpullRunFindings.For(Grid(converged: false, poutW: 0.0));
        var f = Assert.Single(findings, x => x.Kind == LoadpullFindingKind.NothingConverged);

        Assert.Equal(2, f.Attempted);
        Assert.Equal(0, f.Converged);
        Assert.Equal(4, f.DriveSteps);          // two points, two non-tickle rungs each
        Assert.Equal(2, f.StopCodes["no converge"]);
    }

    /// <summary>
    /// R-aut9-6. The 30 dB jump from the tickle to the first real drive step, named as the thing to
    /// change — but ONLY on a run where nothing past the tickle converged, because the tickle is
    /// designed to sit tens of dB below PinStart and a standing warning about it would fire on every
    /// loadpull ever run.
    /// </summary>
    [Fact]
    public void ALargeTickleGap_IsNamedOnlyWhenNothingConverged()
    {
        Assert.Contains(LoadpullRunFindings.For(Grid(converged: false, poutW: 0.0)),
                        f => f.Kind == LoadpullFindingKind.TickleGap);

        // The identical ladder on a run that DID converge says nothing: the gap is the default.
        Assert.DoesNotContain(LoadpullRunFindings.For(Grid(converged: true, poutW: 0.5)),
                              f => f.Kind == LoadpullFindingKind.TickleGap);

        // …and a small gap says nothing either, converged or not.
        Assert.DoesNotContain(LoadpullRunFindings.For(Grid(converged: false, poutW: 0.0, firstDriveDbm: -45)),
                              f => f.Kind == LoadpullFindingKind.TickleGap);
    }

    // ── R-aut9-7. Three problems, three sentences ────────────────────────────

    /// <summary>
    /// A data display whose own source cannot be read is not an unreadable data display. The refusal
    /// used to name the RESULT file inside "'&lt;path&gt;' is not a readable data display", and a
    /// caller reasonably rewrote the <c>.cdd</c> that was never wrong.
    /// </summary>
    [Fact]
    public void AnUnreadableSource_IsNotReportedAsAnUnreadableDataDisplay()
    {
        Directory.CreateDirectory(_tmp);
        string result = Path.Combine(_tmp, "run.npy");
        File.WriteAllText(result, "this is not a .npy at all");

        // PascalCase: DataDisplayJson.Options sets no naming policy, so the `.cdd` on disk is the
        // property names as C# spells them.
        string cdd = Path.Combine(_tmp, "display.cdd");
        File.WriteAllText(cdd, """
            {"FormatVersion":2,
             "SelectedDataSource":"run.npy",
             "Tabs":[{"Name":"Tab 1",
                      "Plots":[{"Traces":[{"SourcePath":"run.npy","CubeName":"S"}]}]}]}
            """);

        var (exit, stdout, _) = RunCli("render", cdd, "-o", Path.Combine(_tmp, "out.svg"), "--json");
        Assert.NotEqual(0, exit);

        var doc = JsonDocument.Parse(stdout).RootElement;
        var ids = Ids(doc);
        Assert.Contains("render.cdd.source-unreadable", ids);
        Assert.DoesNotContain("render.cdd.unreadable", ids);

        string message = doc.GetProperty("diagnostics")[0].GetProperty("message").GetString()!;
        Assert.Contains("display.cdd", message);   // which document referenced it
        Assert.Contains("run.npy",     message);   // and which source failed
    }

    // ── R-aut9-8. A diagnostic is not emitted twice ──────────────────────────

    /// <summary>
    /// <c>message</c> and <c>arguments.text</c> used to be byte-identical on the great majority of
    /// diagnostics — roughly 15 KB of exact duplication in one Gerber import. An argument that
    /// carries the whole sentence carries nothing.
    /// </summary>
    [Fact]
    public void NoDiagnosticRepeatsItsOwnMessageAsAnArgument()
    {
        Directory.CreateDirectory(_tmp);
        var (_, stdout, _) = RunCli(
            "convert", "testdata/pcb-samples/nets.kicad_pcb", "-o", Path.Combine(_tmp, "nets.gds"), "--json");

        var diagnostics = JsonDocument.Parse(stdout).RootElement.GetProperty("diagnostics")
            .EnumerateArray().ToArray();
        Assert.NotEmpty(diagnostics);

        foreach (var d in diagnostics)
        {
            string message = d.GetProperty("message").GetString()!;
            if (!d.TryGetProperty("arguments", out var arguments)) continue;
            if (!arguments.TryGetProperty("text", out var text)) continue;
            Assert.False(text.GetString() == message,
                         $"arguments.text of {d.GetProperty("id")} repeats the whole message");
        }
    }

    /// <summary>
    /// The rule is by NAME, and this is why: <c>convert.cell.listed</c> is templated <c>"{cell}"</c>
    /// and its argument is the ANSWER — one cell name — which happens to be the whole sentence.
    /// Dropping "any argument that equals the message" would have taken the answer out of the
    /// document a caller asked the question with.
    /// </summary>
    [Fact]
    public void AnArgumentThatIsTheAnswer_SurvivesEvenWhenItIsAlsoTheWholeMessage()
    {
        var listed = JsonDocument.Parse(RunCli(
            "convert", "testdata/pcb-samples/nets.kicad_pcb", "--list-cells", "--json").StdOut)
            .RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("id").GetString() == "convert.cell.listed")
            .Select(d => d.GetProperty("arguments").GetProperty("cell").GetString())
            .ToArray();

        Assert.NotEmpty(listed);
        Assert.All(listed, c => Assert.False(string.IsNullOrEmpty(c)));
    }

    /// <summary>An argument that is only PART of the message survives: the reduction costs no
    /// information, which is the whole reason it is safe.</summary>
    [Fact]
    public void AnArgumentThatIsNotTheWholeMessage_IsStillCarried()
    {
        var doc = JsonDocument.Parse(RunCli("hb", "no/such/file.cnl", "--json").StdOut).RootElement;
        Assert.Equal("no/such/file.cnl",
            doc.GetProperty("diagnostics")[0].GetProperty("arguments").GetProperty("path").GetString());
    }

    // ── R-aut9-9. Narrowing by axis ──────────────────────────────────────────

    [Fact]
    public void At_PicksTheNearestGridPoint_AndSaysWhichOne()
    {
        Directory.CreateDirectory(_tmp);
        var doc = JsonDocument.Parse(RunCli(
            "sparam", "testdata/Hero1/hero1.cnl", "-o", Path.Combine(_tmp, "hero1.s4p"),
            "--json", "--only", "S", "--at", "freq=2GHz").StdOut).RootElement;

        var narrowed = doc.GetProperty("result").GetProperty("narrowed")[0];
        Assert.Equal("freq",    narrowed.GetProperty("axis").GetString());
        Assert.Equal("Hz",      narrowed.GetProperty("unit").GetString());
        Assert.Equal("nearest", narrowed.GetProperty("mode").GetString());
        Assert.Equal(2e9,       narrowed.GetProperty("asked").GetDouble());

        var freq = doc.GetProperty("result").GetProperty("groups").GetProperty("")
                      .GetProperty("S").GetProperty("axes")[0];
        Assert.Equal(1, freq.GetProperty("length").GetInt32());
        // The axis SURVIVES at length one: collapsing it would take the answer's own location out of
        // the document, and "which point did I get" is the question `nearest` has to answer.
        Assert.Equal(narrowed.GetProperty("at").GetDouble(), freq.GetProperty("values")[0].GetDouble());
    }

    /// <summary>Interpolation returns a number the run did not compute, so it is never the default
    /// and the document says which mode produced the value.</summary>
    [Fact]
    public void Interp_InterpolatesAndSaysSo()
    {
        Directory.CreateDirectory(_tmp);
        string[] args = ["sparam", "testdata/Hero1/hero1.cnl", "-o", Path.Combine(_tmp, "hero1.s4p"),
                         "--json", "--only", "S"];

        var nearest = JsonDocument.Parse(RunCli([.. args, "--at", "freq=2.03GHz"]).StdOut).RootElement;
        var lerped  = JsonDocument.Parse(RunCli([.. args, "--at", "freq=2.03GHz", "--interp"]).StdOut).RootElement;

        Assert.Equal("nearest",      Mode(nearest));
        Assert.Equal("interpolated", Mode(lerped));
        Assert.Equal(2.03e9, lerped.GetProperty("result").GetProperty("narrowed")[0]
                                   .GetProperty("at").GetDouble(), 3);

        static string Mode(JsonElement d)
            => d.GetProperty("result").GetProperty("narrowed")[0].GetProperty("mode").GetString()!;
    }

    [Fact]
    public void Range_KeepsABandAndNothingElse()
    {
        Directory.CreateDirectory(_tmp);
        var doc = JsonDocument.Parse(RunCli(
            "sparam", "testdata/Hero1/hero1.cnl", "-o", Path.Combine(_tmp, "hero1.s4p"),
            "--json", "--only", "S", "--range", "freq=1.5GHz:2.5GHz").StdOut).RootElement;

        var freq = doc.GetProperty("result").GetProperty("groups").GetProperty("")
                      .GetProperty("S").GetProperty("axes")[0];
        foreach (var v in freq.GetProperty("values").EnumerateArray())
        {
            Assert.True(v.GetDouble() >= 1.5e9);
            Assert.True(v.GetDouble() <= 2.5e9);
        }
        Assert.Equal("range", doc.GetProperty("result").GetProperty("narrowed")[0]
                                 .GetProperty("mode").GetString());
    }

    /// <summary>
    /// An axis no cube has fails the invocation rather than quietly returning everything — the whole
    /// result would be an answer to a question the caller did not ask, at exactly the size this
    /// requirement exists to avoid. The refusal names the axes that do exist.
    /// </summary>
    [Fact]
    public void AnUnknownAxis_IsRefused_AndTheShapeComesBackInstead()
    {
        Directory.CreateDirectory(_tmp);
        var (exit, stdout, _) = RunCli(
            "sparam", "testdata/Hero1/hero1.cnl", "-o", Path.Combine(_tmp, "hero1.s4p"),
            "--json", "--at", "frequency=2GHz");

        Assert.Equal(1, exit);
        var doc = JsonDocument.Parse(stdout).RootElement;
        // The refusal is RfCore's own: the rule about which axes exist lives where the axes do.
        Assert.Contains("narrow.axis.unknown", Ids(doc));
        Assert.Contains("freq", doc.GetProperty("diagnostics")[0].GetProperty("message").GetString());

        var result = doc.GetProperty("result");
        Assert.True(result.TryGetProperty("shape", out _));
        Assert.False(result.TryGetProperty("groups", out _));
    }

    /// <summary>A malformed spelling is refused BEFORE the run — the file is never opened.</summary>
    [Fact]
    public void AMalformedNarrowing_IsRefusedBeforeTheRun()
    {
        var (exit, stdout, _) = RunCli("sparam", "testdata/Hero1/hero1.cnl", "--json", "--at", "freq");
        Assert.Equal(1, exit);

        var doc = JsonDocument.Parse(stdout).RootElement;
        Assert.Contains("cli.narrow.malformed", Ids(doc));
        Assert.Empty(doc.GetProperty("outputs").EnumerateArray());
    }

    /// <summary><c>--result summary</c> returns the shape and no values, for every verb.</summary>
    [Fact]
    public void ResultSummary_ReturnsTheShapeAndNoValues()
    {
        Directory.CreateDirectory(_tmp);
        var doc = JsonDocument.Parse(RunCli(
            "sparam", "testdata/Hero1/hero1.cnl", "-o", Path.Combine(_tmp, "hero1.s4p"),
            "--json", "--result", "summary").StdOut).RootElement;

        var result = doc.GetProperty("result");
        Assert.False(result.TryGetProperty("groups", out _));

        var s = result.GetProperty("shape").GetProperty("groups").GetProperty("").GetProperty("S");
        Assert.Equal("complex", s.GetProperty("kind").GetString());
        Assert.True(s.GetProperty("elements").GetInt64() > 0);
        var freq = s.GetProperty("axes")[0];
        Assert.Equal("freq", freq.GetProperty("name").GetString());
        Assert.Equal("Hz",   freq.GetProperty("unit").GetString());
        Assert.True(freq.GetProperty("last").GetDouble() > freq.GetProperty("first").GetDouble());
    }

    // ── R-aut9-10. The shape is uniform, whether the values are inline or not ──

    [Theory]
    [InlineData("sparam|testdata/Hero1/hero1.cnl|-o|<TMP>/hero1.s4p")]
    [InlineData("dc|testdata/Hero2/hero2.cnl")]
    [InlineData("hb|testdata/Hero2/hero2.cnl")]
    [InlineData("lp|testdata/Hero3/hero3.cnl")]
    [InlineData("lpp|testdata/Hero3B/hero3B_at_compression.cnl|--out-grid|<TMP>/found.gam")]
    [InlineData("read|testdata/Hero1/potentially_unstable_amp.s2p")]
    public void EveryRun_ReturnsItsShape_WhetherOrNotItReturnsItsValues(string argLine)
    {
        Directory.CreateDirectory(_tmp);
        var (_, stdout, stderr) = RunCli([.. Args(argLine), "--json"]);
        output.WriteLine(stderr);

        var shape = JsonDocument.Parse(stdout).RootElement
            .GetProperty("result").GetProperty("shape").GetProperty("groups");

        Assert.NotEmpty(shape.EnumerateObject());
        foreach (var group in shape.EnumerateObject())
            Assert.NotEmpty(group.Value.EnumerateObject());
    }

    // ── R-aut9-11. A summary mode for a verb that emits tens of notes ────────

    [Fact]
    public void Summary_CollapsesTheNotesIntoCounts_AndKeepsTheOutputs()
    {
        Directory.CreateDirectory(_tmp);
        string[] args = ["convert", "testdata/pcb-samples/nets.kicad_pcb", "--list-cells", "--json"];

        var full  = JsonDocument.Parse(RunCli(args).StdOut).RootElement;
        var terse = JsonDocument.Parse(RunCli([.. args, "--summary"]).StdOut).RootElement;

        int notes = full.GetProperty("diagnostics").GetArrayLength();
        Assert.True(notes > 0, "the fixture emitted no notes, so this gate measured nothing");

        var tally = terse.GetProperty("diagnosticSummary");
        Assert.Equal(notes, tally.GetProperty("info").GetInt32() +
                            tally.GetProperty("warning").GetInt32() +
                            tally.GetProperty("error").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(tally.GetProperty("full").GetString()));

        // Every surviving diagnostic is a warning or an error — an info note is what a summary
        // collapses, and nothing else is.
        foreach (var d in terse.GetProperty("diagnostics").EnumerateArray())
            Assert.NotEqual("info", d.GetProperty("severity").GetString());

        Assert.Equal(full.GetProperty("outputs").GetRawText(), terse.GetProperty("outputs").GetRawText());
        Assert.True(terse.ToString().Length < full.ToString().Length, "--summary made the document no smaller");
    }

    /// <summary>Without the flag there is no tally key at all, and the diagnostics are untouched:
    /// the reduction is opt-in, and its absence has to be visible.</summary>
    [Fact]
    public void WithoutSummary_ThereIsNoTallyAndNothingIsCollapsed()
    {
        Directory.CreateDirectory(_tmp);
        var doc = JsonDocument.Parse(RunCli(
            "convert", "testdata/pcb-samples/nets.kicad_pcb", "--list-cells", "--json").StdOut).RootElement;

        Assert.False(doc.TryGetProperty("diagnosticSummary", out _));
        Assert.Contains(doc.GetProperty("diagnostics").EnumerateArray(),
                        d => d.GetProperty("severity").GetString() == "info");
    }

    // ── the brief's second named gate: the byte budget ───────────────────────

    /// <summary>
    /// <b>The brief's own size gate.</b> One representative document, measured — so a regression
    /// that reintroduces the duplicated diagnostic text, or that makes a result un-narrowable again,
    /// fails visibly here rather than months later on somebody's token bill.
    ///
    /// <para>The numbers are generous multiples of what was measured, not tight fits: this is a
    /// tripwire for a structural regression, not a benchmark of the serializer. Roughly four bytes
    /// per token, which is what makes any of these figures worth caring about.</para>
    /// </summary>
    [Fact]
    public void OneRepresentativeDocument_StaysUnderItsByteBudget()
    {
        Directory.CreateDirectory(_tmp);
        string[] sparam = ["sparam", "testdata/Hero1/hero1.cnl", "-o", Path.Combine(_tmp, "hero1.s4p"), "--json"];

        int whole   = RunCli(sparam).StdOut.Length;
        int atOne   = RunCli([.. sparam, "--at", "freq=2GHz"]).StdOut.Length;
        int summary = RunCli([.. sparam, "--result", "summary"]).StdOut.Length;

        output.WriteLine($"whole {whole:N0}  --at {atOne:N0}  --result summary {summary:N0}");

        // The point of R-aut9-9: asking for one frequency costs a small fraction of asking for all
        // of them. A regression that stopped narrowing would make these converge.
        Assert.True(atOne   < whole / 4, $"--at narrowed {whole:N0} to only {atOne:N0}");
        Assert.True(summary < whole / 8, $"--result summary narrowed {whole:N0} to only {summary:N0}");

        // And the shape is genuinely cheap, which is what lets it be unconditional (R-aut9-10).
        Assert.True(summary < 4_000, $"the shape alone came to {summary:N0} bytes");

        // The Gerber-shaped case R-aut9-8 and R-aut9-11 act on: a listing call whose answer is one
        // cell name. 30 KB over the wire was the measurement that produced both requirements.
        int listing = RunCli("convert", "testdata/pcb-samples/nets.kicad_pcb", "--list-cells",
                             "--json", "--summary").StdOut.Length;
        output.WriteLine($"--list-cells --summary {listing:N0}");
        Assert.True(listing < 4_000, $"a one-cell listing came to {listing:N0} bytes");
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A two-point Γ grid with two non-tickle drive rungs each, and the tickle 30 dB below the first
    /// of them — the exercise's own default ladder. Hand-built because the states R-aut9-4/5/6
    /// report need a deliberately broken bench to produce, and the requirement is about reading a
    /// result rather than about producing one.
    /// </summary>
    private static DataSet Grid(bool converged, double poutW, double firstDriveDbm = -20)
    {
        const int nG = 2, nP = 3;   // tickle + two rungs
        var grid = new Axis("gridPoint", [0.0, 1.0]);
        var pin  = new Axis("pinStep",   [-50.0, firstDriveDbm, firstDriveDbm + 1]);

        double[] conv = new double[nG * nP], tick = new double[nG * nP];
        double[] pavl = new double[nG * nP], pout = new double[nG * nP];

        for (int g = 0; g < nG; g++)
            for (int p = 0; p < nP; p++)
            {
                int i = g * nP + p;
                tick[i] = p == 0 ? 1.0 : 0.0;
                pavl[i] = pin.Values[p];
                // The tickle always converges — it is what the ladder warm-starts from, and a run
                // where even it failed is a different story.
                conv[i] = p == 0 || converged ? 1.0 : 0.0;
                pout[i] = poutW;
            }

        var ds = new DataSet();
        ds.Add("Converged", new DataCube([grid, pin], conv));
        ds.Add("IsTickle",  new DataCube([grid, pin], tick));
        ds.Add("PavlDbm",   new DataCube([grid, pin], pavl));
        ds.Add("Pout",      new DataCube([grid, pin], pout));
        // 2 = NonConvergence, 0 = PinMax — LoadpullEngine's own wire encoding.
        ds.Add("StopCode",  new DataCube([grid], converged ? new[] { 0.0, 0.0 } : new[] { 2.0, 2.0 }));
        return ds;
    }

    private static NumFlat.Mat<Complex> Mat1(Complex v)
    {
        var m = new NumFlat.Mat<Complex>(1, 1);
        m[0, 0] = v;
        return m;
    }

    private static string[] Ids(JsonElement doc)
        => [.. doc.GetProperty("diagnostics").EnumerateArray()
                  .Select(d => d.GetProperty("id").GetString()!)];

    private string[] Args(string argLine)
        => [.. argLine.Split('|').Select(a => a.Replace("<TMP>", _tmp))];

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(ResultDocumentContractTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
