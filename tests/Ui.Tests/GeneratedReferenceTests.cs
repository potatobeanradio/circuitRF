// ================================================================
//  GeneratedReferenceTests.cs — the gates for AUT-10, the generated reference
//  (docs/sonnet-briefs/brief-automation-10-generated-reference.md).
//
//  Three gates, one per thing that could go stale in silence:
//
//   1. R-aut10-2. For every registered type, the catalogue's stated NET COUNT is the number of nets
//      CnlReader actually binds. This is the gate that closes the series' worst finding: the
//      catalogue described a Tuner as a one-net part because it published the SYMBOL's pin count
//      under the heading "nets", and a client that believed it got a bench whose bias tee delivered
//      nothing, with every diagnostic clean, and reported the component as broken.
//
//   2. R-aut10-1. Every `analysis type=` token the reader accepts appears in the generated analyses
//      topic, and every token the topic lists is accepted by the reader — both directions, because
//      a generated page that can fall behind the registry is the problem it was written to solve.
//
//   3. Every topic the index lists resolves and is non-empty. A topic that lists but does not serve
//      is worse than one that is absent: a client budgets for it and gets nothing.
//
//  The CLI is launched as the already-built CircuitRF.Cli.dll, for ReferenceCliVerbTests' reason
//  (`dotnet run --project` is a hang inside `dotnet test`, not merely a cost).
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class GeneratedReferenceTests(ITestOutputHelper output)
{
    // ══ R-aut10-2 — the catalogue's net count IS the reader's ════════════════════════════════════

    /// <summary>
    /// The brief's own gate, stated as it is written there: for every registered type, the
    /// catalogue's stated net count equals the number of nets <c>CnlReader</c> binds for a minimal
    /// instance line of that type.
    ///
    /// <para><b>It is asserted through the reader, not through the table.</b> A line written with the
    /// catalogue's count must ELABORATE — reaching a different failure is fine, since most types need
    /// parameters this test does not supply, but reaching the net-count refusal is not — and a line
    /// one net short or one net long must be refused BY THAT REFUSAL, naming the instance. Comparing
    /// two functions in one file would prove only that the file is self-consistent.</para>
    /// </summary>
    [Fact]
    public void EveryStatedNetCount_IsTheCountTheReaderBinds()
    {
        var stated = ComponentCatalog.All()
                                     .Where(e => e.Nets.Count is not null)
                                     .ToDictionary(e => e.Type, e => e.Nets.Count!.Value, StringComparer.Ordinal);

        Assert.NotEmpty(stated);

        var wrong = new List<string>();

        foreach (var (type, count) in stated)
        {
            // Mutual names two inductors by parameter and binds no nets at all. A zero-net line is
            // still a line, but there is no "one fewer" to write, so the wrong-count half of the
            // check has only one side for it.
            if (Refusal(type, count) is { } accepted)
            {
                wrong.Add($"{type}: the catalogue says {count} nets and a {count}-net line was " +
                          $"refused with \"{accepted}\"");
                continue;
            }

            if (count > 0 && Refusal(type, count - 1) is null)
                wrong.Add($"{type}: the catalogue says {count} nets and a {count - 1}-net line was accepted");

            if (Refusal(type, count + 1) is null)
                wrong.Add($"{type}: the catalogue says {count} nets and a {count + 1}-net line was accepted");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        output.WriteLine($"{stated.Count} types state a fixed net count; all agree with the reader.");
    }

    /// <summary>
    /// Every type that does NOT state a count states a RULE instead, and there is no third category.
    /// A blank on both is what the catalogue used to publish for Term and ExtDevice, with a note
    /// saying nothing below the UI firewall knew — the caveat the brief calls the defect itself.
    /// </summary>
    [Fact]
    public void NoSimulatableTypeIsSilentAboutItsNetCount()
    {
        var silent = ComponentCatalog.All()
            .Where(e => e.Simulatable && e.Nets.Count is null && e.Nets.Rule.Length == 0)
            .Select(e => e.Type)
            .ToArray();

        Assert.True(silent.Length == 0,
            "These registered types state neither a net count nor a rule: " + string.Join(", ", silent));
    }

    /// <summary>
    /// The finding itself, pinned by name. A Tuner draws ONE pin and its instance line binds TWO
    /// nets, and the catalogue now says both — under two different headings, which is the whole
    /// fix. A regression here is the defect returning, not a cosmetic change.
    /// </summary>
    [Theory]
    [InlineData("Tuner",  2, 1)]
    [InlineData("IProbe", 2, 2)]
    [InlineData("Vdc",    2, 2)]
    [InlineData("Term",   2, null)]
    public void APartWhoseReferenceTerminalIsImplicit_ReportsBothNumbers(string type, int nets, int? pins)
    {
        var entry = ComponentCatalog.All().Single(e => e.Type == type);

        Assert.Equal(nets, entry.Nets.Count);
        // The symbol's own count is a DIFFERENT quantity and is allowed to differ — the Tuner's
        // does, by one, which is the entire finding. What must never happen again is the two being
        // published as one number under one heading.
        Assert.Equal(pins, entry.Ports.Count);

        output.WriteLine($"{type}: {nets} nets, symbol pins = {pins?.ToString() ?? "not fixed"}");
    }

    /// <summary>A count that depends on a parameter is reported as the RULE, never as the number it
    /// happens to take at two ports — R-aut6-9's judgement, applied to nets.</summary>
    [Theory]
    [InlineData("SDD",    "SddPortCount")]
    [InlineData("Z_Port", "ZPortCount")]
    [InlineData("SnP",    "NumPorts")]
    [InlineData("Switch", "Throws")]
    public void AVariadicTypeReportsTheRule_AndNamesTheNetlistParameter(string type, string parameter)
    {
        var entry = ComponentCatalog.All().Single(e => e.Type == type);

        Assert.Null(entry.Nets.Count);
        Assert.Equal(parameter, entry.Nets.DeterminedBy);
        Assert.Contains(parameter, entry.Nets.Rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SDD case the repo's own CLAUDE.md calls out: the netlist parameter is NOT the symbol's.
    /// A client that copied <c>NumPorts</c> off the parameter panel into a <c>.cnl</c> would set
    /// nothing at all, and the SDD would refuse for a reason that names neither.
    /// </summary>
    [Fact]
    public void TheSddsNetlistPortCountKeyIsNotItsSymbolsParameterName()
    {
        var sdd = ComponentCatalog.All().Single(e => e.Type == "SDD");

        Assert.Equal("SddPortCount", sdd.Nets.DeterminedBy);
        Assert.Equal("NumPorts",     sdd.Ports.DeterminedBy);
    }

    // ══ R-aut10-1 — the analyses topic IS the registry ═══════════════════════════════════════════

    /// <summary>
    /// Every <c>type=</c> token the reader accepts is in the generated topic, and every token the
    /// topic lists is accepted by the reader. Both directions, through the CLI as a process and
    /// through <c>CnlReader</c> respectively, so neither side can be satisfied by reading the other.
    /// </summary>
    [Fact]
    public void EveryTypeTokenTheReaderAcceptsIsInTheGeneratedTopic_AndViceVersa()
    {
        var run = RunCli("reference", "analyses", "--json");
        Assert.Equal(0, run.ExitCode);

        var listed = Payload(run.StdOut).GetProperty("reference").GetProperty("analyses")
                                        .EnumerateArray().ToArray();

        var tokens = listed.Select(a => a.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(AnalysisDirectiveSchema.TypeTokens, tokens);

        foreach (var entry in listed)
        {
            string token = entry.GetProperty("type").GetString()!;
            var    all   = new List<string> { token };
            all.AddRange(entry.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!));

            foreach (string spelling in all)
                Assert.True(AnalysisDirectiveSchema.Find(spelling) is not null,
                    $"the topic lists '{spelling}' for {token} and the reader does not accept it.");
        }

        // And nothing the reader accepts is missing: Find is total over the token index, so the
        // check that matters is that the page's set is the registry's set, above, plus this — a
        // token added to _specs and not to Specs would be invisible to both.
        Assert.Equal(AnalysisDirectiveSchema.Specs.Count, listed.Length);
    }

    /// <summary>
    /// Every key the topic lists is a key the reader accepts on that directive, and every key the
    /// reader accepts is listed. This is the half that cost an out-of-process client fifteen round
    /// trips to reconstruct one directive.
    /// </summary>
    [Fact]
    public void EveryKeyTheTopicListsIsAKeyTheReaderAccepts()
    {
        var run = RunCli("reference", "analyses", "--json");
        Assert.Equal(0, run.ExitCode);

        foreach (var entry in Payload(run.StdOut).GetProperty("reference").GetProperty("analyses").EnumerateArray())
        {
            string token = entry.GetProperty("type").GetString()!;
            var    spec  = AnalysisDirectiveSchema.Find(token)!;

            foreach (var k in entry.GetProperty("keys").EnumerateArray())
            {
                string name = k.GetProperty("name").GetString()!;
                // An indexed key is listed as it is WRITTEN, so the [i] is asked about as [1].
                string written = k.GetProperty("indexed").GetBoolean() ? name.Replace("[i]", "[1]") : name;

                Assert.True(AnalysisDirectiveSchema.ResolveKey(spec, written) is not null,
                    $"{token}: the topic lists '{written}' and the reader does not accept it.");
            }

            // The other direction. Every legal key of the directive, universal ones included, is on
            // the page — a key the reader reads and nothing documents is what this brief is about.
            var listed = entry.GetProperty("keys").EnumerateArray()
                              .Select(k => k.GetProperty("name").GetString()!)
                              .ToHashSet(StringComparer.Ordinal);

            foreach (var k in AnalysisDirectiveSchema.UniversalKeys.Concat(spec.Keys))
                Assert.Contains(k.Indexed ? k.Name + "[i]" : k.Name, listed);
        }
    }

    /// <summary>A directive named by an ALIAS resolves to the one it names — the reader accepts
    /// <c>lpp</c>, so a reference that refused it would teach the wrong lesson.</summary>
    [Theory]
    [InlineData("lpp",              "loadpull_pursuit")]
    [InlineData("LoadpullPursuit",  "loadpull_pursuit")]
    [InlineData("sp",               "sparam")]
    [InlineData("op",               "dc")]
    public void AnAliasReachesTheDirectiveItNames(string spelling, string canonical)
    {
        var run = RunCli("reference", "analyses", spelling, "--json");
        Assert.Equal(0, run.ExitCode);

        var one = Payload(run.StdOut).GetProperty("reference").GetProperty("analyses").EnumerateArray().Single();
        Assert.Equal(canonical, one.GetProperty("type").GetString());
    }

    [Fact]
    public void AnUnknownAnalysisType_IsRefused_ListingTheRealOnes()
    {
        var run = RunCli("reference", "analyses", "nosuchanalysis", "--json");
        Assert.Equal(1, run.ExitCode);

        var d = Diagnostics(run.StdOut).Single();
        Assert.Equal("reference.analysis.unknown", d.GetProperty("id").GetString());
        foreach (string token in AnalysisDirectiveSchema.TypeTokens)
            Assert.Contains(token, d.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // ══ every listed topic resolves ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Every topic the index lists comes back, exits 0, and is not empty — the generated ones as
    /// well as the authored ones, and at the size the index promised.
    /// </summary>
    [Fact]
    public void EveryListedTopicResolvesAndIsNonEmpty()
    {
        var index = RunCli("reference", "--json");
        Assert.Equal(0, index.ExitCode);

        var topics = Payload(index.StdOut).GetProperty("reference").GetProperty("topics")
                                          .EnumerateArray().ToArray();
        Assert.NotEmpty(topics);

        foreach (var t in topics)
        {
            string name  = t.GetProperty("topic").GetString()!;
            int    bytes = t.GetProperty("bytes").GetInt32();
            Assert.True(bytes > 0, $"{name} was listed with no size at all.");

            var run = RunCli("reference", name);
            Assert.Equal(0, run.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(run.StdOut), $"{name} resolved to nothing.");

            // The size in the index is the size of what is SERVED, which is the only thing that
            // makes it worth carrying (R-aut6-3). A listing that under-reports is a budget a client
            // sets and then overruns.
            Assert.Equal(bytes, System.Text.Encoding.UTF8.GetByteCount(run.StdOut));
        }

        output.WriteLine($"{topics.Length} topics, all resolved.");
    }

    /// <summary>
    /// The two format topics carry a WORKING example, not a sketch — the whole reason R-aut10-3
    /// exists is that an exercise could only write a <c>.cdd</c> by copying one that happened to be
    /// on the machine. The example is extracted from the served page and parsed as JSON.
    /// </summary>
    [Theory]
    [InlineData("data-display", "FormatVersion")]
    [InlineData("technology",   "FormatVersion")]
    public void AFormatTopicCarriesAnExampleThatParses(string topic, string mustContain)
    {
        var run = RunCli("reference", topic);
        Assert.Equal(0, run.ExitCode);

        string example = FirstJsonObject(run.StdOut);
        Assert.Contains(mustContain, example, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(example);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    /// <summary>
    /// The three formats a client writes to run EM and wirebond work headlessly carry an example the
    /// format's OWN reader accepts — parsing as JSON is not enough, because a <c>.clay</c> whose shape
    /// puts <c>$type</c> second is valid JSON and a refused layout. The examples were also run end to
    /// end (check, render, em, sparam) when written; this is the part that can go stale cheaply.
    /// </summary>
    [Theory]
    [InlineData("layout")]
    [InlineData("em-setup")]
    [InlineData("wbond")]
    public void AFormatTopicsExampleIsReadByItsOwnReader(string topic)
    {
        var run = RunCli("reference", topic);
        Assert.Equal(0, run.ExitCode);
        string example = FirstJsonObject(run.StdOut);

        switch (topic)
        {
            case "layout":
                var layout = CircuitRF.Design.Layout.LayoutPersistence.Deserialize(example);
                Assert.Equal(3, layout.Shapes.Count);
                break;
            case "em-setup":
                var setup = CircuitRF.Design.Layout.Em.EmSetupPersistence.Deserialize(example);
                Assert.Equal("thru/layout/thru.clay", setup.LayoutRef);
                break;
            case "wbond":
                var design = CircuitRF.WBond.WBondIo.Read(example);
                Assert.Equal(2, design.Arrays.Single().Wires.Count);
                break;
        }
    }

    /// <summary>
    /// A polymorphic list — a <c>.clay</c>'s shapes — is written as its DERIVED kinds, so the page
    /// must name the discriminator and every kind's fields. The base type alone lists four fields
    /// and none of the coordinates, which is what the walk produced before it read the attributes.
    /// </summary>
    [Fact]
    public void ThePolymorphicShapeListNamesEveryKindAndItsDiscriminator()
    {
        var run = RunCli("reference", "layout");
        Assert.Equal(0, run.ExitCode);

        foreach (var d in typeof(CircuitRF.Design.Layout.LayoutShape)
                     .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
                     .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>())
            Assert.Contains($"{d.DerivedType.Name}   ($type: \"{d.TypeDiscriminator}\")", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every field the generated half names is a field the reader's own type declares. The page is
    /// a walk over that type, so this pins the walk rather than the fields: a renderer that started
    /// inventing rows, or dropping them, would pass no other test here.
    /// </summary>
    [Fact]
    public void TheGeneratedFieldListIsTheReadersOwnType()
    {
        var run = RunCli("reference", "technology", "--json");
        Assert.Equal(0, run.ExitCode);

        var root = Payload(run.StdOut).GetProperty("reference").GetProperty("schema")[0];
        Assert.Equal(nameof(CircuitRF.Design.Layout.CtechFile), root.GetProperty("type").GetString());

        var listed = root.GetProperty("fields").EnumerateArray()
                         .Select(f => f.GetProperty("name").GetString()!).ToArray();

        var declared = typeof(CircuitRF.Design.Layout.CtechFile)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name).ToArray();

        Assert.Equal(declared, listed);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a minimal <c>.cnl</c> instance line of <paramref name="type"/> with
    /// <paramref name="nets"/> nets and returns the NET-COUNT refusal it produced, or null when the
    /// line got past that check — including when it then failed for some other reason.
    ///
    /// <para><b>The parameters are the catalogue's own minimal set</b>
    /// (<see cref="InstanceNetContract.MinimalParameters"/>), not a second guess: a Tuner with no
    /// <c>Z[1]</c> and a Match with no <c>Design</c> are refused at CONSTRUCTION, before any net
    /// check runs, and a test that could not build one would report every wrong count as accepted.
    /// The two inductors exist for <c>Mutual</c>, which names inductors rather than binding
    /// nets.</para>
    ///
    /// <para><b>Two refusals count, because there are two</b>: <c>InstanceNetContract.Refusal</c>
    /// (AUT-8) and the older per-family <c>ValidatePortPairNetCount</c>, which fires first for the
    /// ideal system blocks and says the same thing in its own words. Either is the reader rejecting
    /// the count; requiring one particular sentence would fail on a correct refusal.</para>
    /// </summary>
    private static string? Refusal(string type, int nets)
    {
        var    ps   = InstanceNetContract.MinimalParameters(type, 2);
        string args = string.Concat(ps.Select(kv => $" {kv.Key}={Literal(kv.Value)}"));
        string line = $"{type}:X1 {string.Join(' ', Enumerable.Range(0, nets).Select(i => "n" + i))}{args}\n";

        try
        {
            var (lib, tb) = new CnlReader().Read("L:L1 nA nB L=1n\nL:L2 nC nD L=1n\n" + line);
            using var _ = new Elaborator(lib).Elaborate(tb);
            return null;
        }
        catch (Exception ex)
        {
            // Anything that does not mention this instance AND a net is a failure this test is not
            // about — a missing file, a provider that is not registered — and means the count itself
            // was accepted.
            return ex.Message.Contains("X1", StringComparison.Ordinal)
                && ex.Message.Contains("net", StringComparison.Ordinal)
                ? ex.Message
                : null;
        }
    }

    /// <summary>A parameter value as a <c>.cnl</c> writes it. A string is QUOTED: a bare word there
    /// parses as an expression and resolves to an unbound name, which fails before the net check
    /// and would make this test blind.</summary>
    private static string Literal(CircuitRF.Core.Expressions.Value v)
        => v.Kind == CircuitRF.Core.Expressions.ValueKind.String
            ? "\"" + v.AsString() + "\""
            : v.ToString();

    private static JsonElement Payload(string stdout)
        => JsonDocument.Parse(stdout).RootElement.GetProperty("result").Clone();

    private static JsonElement[] Diagnostics(string stdout)
        => [.. JsonDocument.Parse(stdout).RootElement.GetProperty("diagnostics").EnumerateArray()
                           .Select(d => d.Clone())];

    /// <summary>The first brace-balanced JSON object in a page, un-indented. The examples are
    /// indented into the prose, and JSON does not mind.</summary>
    private static string FirstJsonObject(string page)
    {
        int start = page.IndexOf('{');
        Assert.True(start >= 0, "the page carries no example at all.");

        int depth = 0;
        for (int i = start; i < page.Length; i++)
        {
            if (page[i] == '{') depth++;
            else if (page[i] == '}' && --depth == 0) return page[start..(i + 1)];
        }
        Assert.Fail("the page's example never closes.");
        return "";
    }

    private static CliRun RunCli(params string[] args)
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
        return new CliRun(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private sealed record CliRun(int ExitCode, string StdOut, string StdErr);

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(GeneratedReferenceTests).Assembly)
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
