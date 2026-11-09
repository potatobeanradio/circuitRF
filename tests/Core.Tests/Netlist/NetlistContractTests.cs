using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// AUT-8 — the netlist contract. Every test here pins a case that used to be accepted in silence and
/// is now refused by name, or a report that used to be misleading and is now accurate.
///
/// <para>The governing rule (AUT-7 R-aut7-0) is that <b>on the netlist path, silence is the
/// defect</b>: a client that writes a document, asks <c>check</c> whether it is sound and is told
/// "yes" must be able to act on that answer.</para>
/// </summary>
public class NetlistContractTests
{
    private static (Library, TestBench) Read(string src) => new CnlReader().Read(src);

    // ── R-aut8-3. Unit= on a sparam directive ────────────────────────────────

    /// <summary>
    /// The brief's own stated gate, word for word: write the directive, run it, and assert the FIRST
    /// SWEPT FREQUENCY is 5e8 Hz — not that the parse succeeded, because the whole failure mode was
    /// that parsing "succeeded" and the sweep then ran nine orders of magnitude out.
    /// </summary>
    [Fact]
    public void SparamUnitKey_SetsTheSweepBand_NotJustTheHeader()
    {
        var (_, tb) = Read("analysis SP1 type=sparam Start=0.5 Stop=6 Npts=551 Unit=GHz\n");

        var sp = Assert.IsType<SParameterAnalysis>(Assert.Single(tb.Analyses));
        var f  = Assert.Single(sp.Sweeps);

        Assert.Equal(5e8, FirstFrequencyHz(f), 1e-3);
        Assert.Equal(6e9, LastFrequencyHz(f),  1e-3);
    }

    /// <summary>
    /// A unit given per key still wins over the shorthand — it is the more specific statement — and a
    /// unit written INLINE is not scaled a second time. `start=1 GHz … Unit=GHz` reaching 1e18 Hz
    /// would be R-aut8-3's own defect arriving from the other direction.
    /// </summary>
    [Theory]
    [InlineData("analysis SP type=sparam start=1 stop=6 npts=11 Unit=GHz startUnit=MHz", 1e6)]
    [InlineData("analysis SP type=sparam start=1 GHz stop=6 GHz npts=11 Unit=GHz",       1e9)]
    public void AUnitStatedTwiceIsAppliedOnce(string directive, double expectedStartHz)
    {
        var (_, tb) = Read(directive + "\n");
        var sp = Assert.IsType<SParameterAnalysis>(Assert.Single(tb.Analyses));
        Assert.Equal(expectedStartHz, FirstFrequencyHz(Assert.Single(sp.Sweeps)), expectedStartHz * 1e-9);
    }

    /// <summary>
    /// `step`'s own fallback is the bare literal 1e8. Reading that as 1e8 GHz because the line happens
    /// to carry Unit=GHz would turn a 100 MHz step into 1e17 Hz.
    /// </summary>
    [Fact]
    public void TheShorthandNeverReachesAKeyTheDirectiveDidNotState()
    {
        var (_, tb) = Read("analysis SP type=sparam start=1 stop=2 Unit=GHz\n");
        var f = Assert.Single(Assert.IsType<SParameterAnalysis>(Assert.Single(tb.Analyses)).Sweeps);
        Assert.Equal(1e8, StepHz(f), 1.0);
    }

    // ── R-aut8-1. Unknown keys on an analysis directive ──────────────────────

    /// <summary>
    /// The exercise's own failure: key names copied out of a schematic's serialised analysis block.
    /// Every one used to be accepted and discarded, `check` reported zero errors, and the run then
    /// failed on one missing key per attempt.
    /// </summary>
    [Fact]
    public void AnUnknownKeyIsRefusedByName_WithTheLegalOnesListed()
    {
        var ex = Assert.Throws<CnlReadException>(() => Read(
            "analysis LP1 type=loadpull_pursuit Tone=RFfreq LoadTuner=L SourceTuner=S Nonsense=3\n"));

        Assert.Contains("Nonsense", ex.Message);
        Assert.Contains("LoadTuner", ex.Message);      // the legal list is in the message
        Assert.Contains("Line 1", ex.Message);          // R-aut8-7
    }

    /// <summary>
    /// Every missing required key at once. A caller that must re-run to discover the second one pays
    /// the full cost of a run for each — which is exactly what the exercise did, fifteen times.
    /// </summary>
    [Fact]
    public void EveryMissingRequiredKeyIsReportedInOneRefusal()
    {
        var ex = Assert.Throws<CnlReadException>(() => Read(
            "analysis LP1 type=loadpull Tone=RFfreq\n"));

        Assert.Contains("LoadTuner",   ex.Message);
        Assert.Contains("SourceTuner", ex.Message);
        Assert.Contains("Grid",        ex.Message);
    }

    /// <summary>
    /// A required key written but left BLANK is a key the directive does not have. The engine has
    /// always read it that way; saying so at parse time is what turns it into one line rather than
    /// one run.
    /// </summary>
    [Fact]
    public void ARequiredKeyLeftBlankCountsAsMissing()
    {
        var ex = Assert.Throws<CnlReadException>(() => Read(
            "analysis LP1 type=loadpull_pursuit Tone=RFfreq LoadTuner= SourceTuner=S\n"));
        Assert.Contains("LoadTuner", ex.Message);
    }

    /// <summary>
    /// The schematic's own key spellings are the .cnl ones with an Lp/Lpp/Psa prefix and an
    /// Expr/Name/Path suffix. Accepting them is what stops the two serialisations of one concept
    /// costing a caller fifteen round trips to reconcile.
    /// </summary>
    [Theory]
    [InlineData("LpLoadTunerName",   "LoadTuner")]
    [InlineData("LpSourceTunerName", "SourceTuner")]
    [InlineData("LpToneExpr",        "Tone")]
    [InlineData("LpGridPath",        "Grid")]
    [InlineData("LppOutputGridPath", "OutputGrid")]
    [InlineData("PsaVarName",        "Var")]
    [InlineData("PsaInnerName",      "Inner")]
    public void TheSchematicsOwnKeySpellingsAreAccepted(string written, string canonical)
    {
        var spec = AnalysisDirectiveSchema.Find(
            canonical is "Var" or "Inner" ? "parametric_sweep"
            : canonical is "OutputGrid"   ? "loadpull_pursuit"
            :                               "loadpull")!;

        Assert.Equal(canonical, AnalysisDirectiveSchema.ResolveKey(spec, written));
    }

    /// <summary>An alias resolves to the same analysis as the canonical spelling does.</summary>
    [Fact]
    public void AnAliasedKeyProducesTheSameAnalysisAsTheCanonicalOne()
    {
        var (_, viaAlias) = Read(
            "RFfreq = 2 GHz\n" +
            "analysis LP1 type=lpp LpToneExpr=RFfreq LpLoadTunerName=Load LpSourceTunerName=Src\n");
        var (_, viaCanon) = Read(
            "RFfreq = 2 GHz\n" +
            "analysis LP1 type=loadpull_pursuit Tone=RFfreq LoadTuner=Load SourceTuner=Src\n");

        var a = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(viaAlias.Analyses));
        var b = Assert.IsType<LoadpullPursuitAnalysis>(Assert.Single(viaCanon.Analyses));

        Assert.Equal(b.LoadTunerName,   a.LoadTunerName);
        Assert.Equal(b.SourceTunerName, a.SourceTunerName);
        Assert.Equal(b.ToneExpr,        a.ToneExpr);
    }

    // ── R-aut8-2. Unknown analysis type= token ───────────────────────────────

    /// <summary>
    /// `type=lpp` produced zero errors and the warning "The document declares no analysis." Nothing
    /// indicated the type token was the problem, and eight spellings were tried before one worked.
    /// It is now an alias; a token that is genuinely unknown is refused WITH the list.
    /// </summary>
    [Fact]
    public void AnUnknownTypeTokenIsRefused_ListingTheLegalOnes()
    {
        var ex = Assert.Throws<CnlReadException>(() => Read("analysis A1 type=loadpul Tone=f\n"));

        Assert.Contains("loadpul", ex.Message);
        Assert.Contains("loadpull_pursuit", ex.Message);
        Assert.Contains("sparam",           ex.Message);
    }

    /// <summary>The schematic's own type tags all resolve, which is where the eight guesses came from.</summary>
    [Theory]
    [InlineData("sp",    "sparam")]
    [InlineData("lp",    "loadpull")]
    [InlineData("lpp",   "loadpull_pursuit")]
    [InlineData("sweep", "parametric_sweep")]
    [InlineData("hb",    "hb")]
    [InlineData("env",   "env")]
    [InlineData("dc",    "dc")]
    public void TheSchematicsOwnTypeTagsResolve(string tag, string canonical)
        => Assert.Equal(canonical, AnalysisDirectiveSchema.Find(tag)?.Type);

    /// <summary>
    /// The "declares no analysis" warning is correct for a document that genuinely declares none, and
    /// must stay for that case — the fix is that it is no longer ALSO what a bad type token produces.
    /// </summary>
    [Fact]
    public void ADocumentThatDeclaresNoAnalysisStillReadsCleanly()
    {
        var (_, tb) = Read("R:R1 a 0 R=50 Ohm\n");
        Assert.Empty(tb.Analyses);
    }

    // ── R-aut8-4. Instance line net counts ───────────────────────────────────

    /// <summary>
    /// The brief's stated gate. `Index was outside the bounds of the array` used to be the whole
    /// diagnostic, naming neither the line, the instance nor the count.
    /// </summary>
    [Fact]
    public void AOneNetResistorIsRefused_NamingTheInstanceAndBothCounts()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(
            "R:R1 n1 R=1000 Ohm\n"));

        Assert.Contains("R:R1", ex.Message);
        Assert.Contains("1 net", ex.Message);
        Assert.Contains("takes 2", ex.Message);
        Assert.DoesNotContain("Index was outside", ex.Message);
    }

    /// <summary>
    /// The Tuner case, which is the series' worst finding. A one-net Tuner did not throw at all: the
    /// elaborator's bias-tee minting is guarded on the declared count, so the short line SKIPPED it
    /// and the bench simulated to completion with no bias anywhere.
    /// </summary>
    [Fact]
    public void AOneNetTunerIsRefused_RatherThanSimulatingWithNoBias()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(
            "Tuner:T1 n1 Z[1]=50 BiasTee=on Vbias=48\n"));

        Assert.Contains("Tuner:T1", ex.Message);
        Assert.Contains("takes 2",  ex.Message);
    }

    /// <summary>Too many nets is refused too, and the message says where nets stop.</summary>
    [Fact]
    public void TooManyNetsIsRefusedAsWell()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(
            "C:C1 a b c C=1 pF\n"));
        Assert.Contains("C:C1", ex.Message);
        Assert.Contains("3 nets", ex.Message);
    }

    /// <summary>The correct count still elaborates, which is what stops this being a wall.</summary>
    [Fact]
    public void TheRightCountIsUntouched()
    {
        using var nl = Elaborate("R:R1 a 0 R=50 Ohm\nC:C1 a 0 C=1 pF\n");
        Assert.Equal(2, nl.Components.Count);
    }

    /// <summary>
    /// <b>No third category.</b> Every type the factory registers either states a net count or is one
    /// of the two that deliberately do not, each because a better statement of the rule already
    /// exists elsewhere. A new primitive that states neither fails here rather than silently joining
    /// the unvalidated set.
    /// </summary>
    [Fact]
    public void EveryRegisteredPrimitiveEitherStatesANetCountOrIsANamedException()
    {
        // The ones that deliberately return null, and why, are documented in InstanceNetContract.
        var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SnP", ComponentModelFactory.DeembedTypeName, "wBond", "ExtDevice", "VerilogA" };

        var unstated = new List<string>();
        foreach (var type in ComponentModelFactory.PrimitiveTypeNames)
        {
            if (exempt.Contains(type)) continue;
            // The catalogue's OWN minimal parameters (AUT-10 R-aut10-2), not a second set written
            // here: a private copy left Tuner, Match and Mutual unconstructible and therefore
            // silently skipped by this test — including the one type the whole series is about.
            if (!InstanceNetContract.TryMinimalModel(type, 2, out var model)) continue;
            if (InstanceNetContract.Expected(model!) is null) unstated.Add(type);
        }

        Assert.True(unstated.Count == 0,
            "These registered primitives state no net count and are not named exceptions in " +
            $"InstanceNetContract: {string.Join(", ", unstated)}");

        // And the test is not vacuous: the three that a private minimal-parameter table used to
        // skip are now reached. Tuner is the one the series exists for.
        foreach (string reached in (string[])["Tuner", "Match", "Mutual"])
            Assert.True(InstanceNetContract.TryMinimalModel(reached, 2, out _),
                $"{reached} could not be constructed, so this test says nothing about it.");
    }

    // ── R-aut8-5. Boolean parameter spellings ────────────────────────────────

    /// <summary>
    /// `true`, `1`, `True` and `Yes` were each accepted with no warning and each produced a Tuner
    /// with no bias at all. They all mean on now.
    /// </summary>
    [Theory]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("1")]
    public void EveryOrdinaryBooleanSpellingTurnsTheBiasTeeOn(string spelling)
    {
        using var nl = Elaborate(
            $"Tuner:T1 n1 0 Z[1]=50 BiasTee={spelling} Vbias=48\n");

        Assert.True(HasBiasTee(nl),
            $"BiasTee={spelling} left the tuner with no bias at all — the silent failure R-aut8-5 " +
            "exists to remove.");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("0")]
    public void EveryOrdinaryFalseSpellingLeavesItOff(string spelling)
    {
        using var nl = Elaborate($"Tuner:T1 n1 0 Z[1]=50 BiasTee={spelling}\n");
        Assert.False(HasBiasTee(nl));
    }

    /// <summary>
    /// The other half, and the one that makes widening safe: a spelling nobody recognises is refused
    /// by name rather than quietly meaning false.
    /// </summary>
    [Fact]
    public void AnUnrecognisedBooleanSpellingIsRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(
            "Tuner:T1 n1 0 Z[1]=50 BiasTee=maybe Vbias=48\n"));

        Assert.Contains("BiasTee", ex.Message);
        Assert.Contains("maybe",   ex.Message);
        Assert.Contains("true",    ex.Message);   // the accepted spellings are listed
        Assert.Contains("off",     ex.Message);
    }

    // ── R-aut8-6. A unit on a top-level variable ─────────────────────────────

    /// <summary>
    /// The reference page states, without qualification, that a variable may carry a unit, and
    /// `RFfreq = 2 GHz` is its own worked example. `VDS = 48 V` was a parse error, because the .cnl
    /// reader lifted only the linear-SCALE units while the schematic's VAR path lifted both.
    /// </summary>
    [Theory]
    [InlineData("RFfreq = 2 GHz",  "RFfreq", "2",    "GHz")]
    [InlineData("VDS = 48 V",      "VDS",    "48",   "V")]
    [InlineData("Pin = 10 dBm",    "Pin",    "10",   "dBm")]
    [InlineData("Rload = 50 Ohm",  "Rload",  "50",   "Ohm")]
    [InlineData("Cbig = 1 uF",     "Cbig",   "1",    "uF")]
    public void AVariableMayCarryAnyUnit(string line, string name, string expr, string unit)
    {
        var (_, tb) = Read(line + "\n");
        var v = Assert.Single(tb.GlobalVariables);
        Assert.Equal(name, v.Name);
        Assert.Equal(expr, v.Expression);
        Assert.Equal(unit, v.Unit);
    }

    /// <summary>
    /// The parse check is what makes the wider unit table safe: every bare SI prefix is a unit name,
    /// so a purely token-based rule tears `2 * f` into `2 *` + femto. An expression that already
    /// parses is left completely alone.
    /// </summary>
    [Theory]
    [InlineData("x = 2 * f")]
    [InlineData("y = a + m")]
    [InlineData("z = n * 2")]
    public void AnExpressionThatAlreadyParsesIsNeverSplit(string line)
    {
        var (_, tb) = Read(line + "\n");
        Assert.Null(Assert.Single(tb.GlobalVariables).Unit);
    }

    // ── R-aut8-7. Diagnostics carry the line ─────────────────────────────────

    /// <summary>
    /// `Parse error at position 6: Unexpected token 'V'` gave a column into a string the caller
    /// cannot see. Every diagnostic off this path now carries the line number and the line's text.
    /// </summary>
    [Fact]
    public void EveryNetlistRefusalCarriesTheLineNumberAndTheLineItself()
    {
        var ex = Assert.Throws<CnlReadException>(() => Read(
            "; a comment\n" +
            "R:R1 a 0 R=50 Ohm\n" +
            "analysis SP type=sparam start=1 GHz badkey=2\n"));

        Assert.Equal(3, ex.LineNumber);
        Assert.Contains("Line 3", ex.Message);
        Assert.Contains("badkey", ex.Message);
        Assert.Contains("analysis SP type=sparam", ex.Message);   // the line's own text
    }

    /// <summary>
    /// An InvalidOperationException raised inside a directive parser is wrapped, so it arrives with a
    /// line number too rather than as a bare sentence about a file the caller cannot navigate.
    /// </summary>
    [Fact]
    public void AParserExceptionIsWrappedWithItsLine()
    {
        var ex = Assert.Throws<CnlReadException>(() => Read(
            "analysis SW1 type=parametric_sweep Var=P Inner=HB1 Values=notanumber\n"));
        Assert.Equal(1, ex.LineNumber);
    }

    // ── Coverage: no third category on the analysis path ─────────────────────

    /// <summary>
    /// <b>The gate the brief asks for.</b> Every key CnlWriter emits must be one the schema declares,
    /// and every analysis the writer can produce must round-trip through the reader without being
    /// refused. A key the writer writes and the schema does not know would make the application
    /// unable to read its own output; a key the schema declares and no parser reads is the silent
    /// third category this whole brief removes.
    /// </summary>
    [Fact]
    public void EveryKeyTheWriterEmitsIsOneTheSchemaDeclares()
    {
        foreach (var text in WriterOutputForEveryAnalysisKind())
        {
            // Reading it back is the assertion: an unknown key or a missing required one throws.
            var (_, tb) = Read(text);
            Assert.NotEmpty(tb.Analyses);
            Assert.Empty(tb.RawDirectives);
        }
    }

    /// <summary>Every canonical type token the schema declares is one the reader actually types.</summary>
    [Fact]
    public void EveryDeclaredTypeTokenProducesATypedAnalysis()
    {
        foreach (var spec in AnalysisDirectiveSchema.Specs)
        {
            var line = MinimalDirectiveFor(spec);
            var (_, tb) = Read(line);
            Assert.True(tb.Analyses.Any(a => a.Name == "A1"),
                $"type={spec.Type} did not produce a typed analysis from: {line}");
            Assert.Empty(tb.RawDirectives);
        }
    }

    /// <summary>
    /// Canonicalisation must be the identity on the schema's own key names, or an alias could collide
    /// with a real key. A key added later that ends in Expr/Name/Path — or starts with Lp/Lpp/Psa —
    /// fails here rather than silently shadowing another.
    /// </summary>
    [Fact]
    public void NoDeclaredKeyIsItselfChangedByCanonicalisation()
    {
        foreach (var spec in AnalysisDirectiveSchema.Specs)
        foreach (var key in spec.Keys.Concat(AnalysisDirectiveSchema.UniversalKeys))
        {
            var stripped = key.Name.Replace("_", "").ToLowerInvariant();
            Assert.True(AnalysisDirectiveSchema.Canonicalise(key.Name) == stripped,
                $"'{key.Name}' on type={spec.Type} is changed by the alias rule and would shadow " +
                "another key. Rename it, or extend the rule deliberately.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the tuner in this netlist was actually built with its internal bias tee. Asked of the
    /// MODEL rather than of the node count: the three internal nodes are minted for every Tuner (the
    /// load role simply ignores the third), so counting them cannot tell the two apart — and a test
    /// that could not tell them apart is what let BiasTee=true mean off for as long as it did.
    /// </summary>
    private static bool HasBiasTee(ElaboratedNetlist nl)
    {
        var tuner = nl.Components.Single(c => c.Model is TunerModel);
        return (bool)typeof(TunerModel)
            .GetField("_hasBiasTee", System.Reflection.BindingFlags.NonPublic |
                                     System.Reflection.BindingFlags.Instance)!
            .GetValue(tuner.Model)!;
    }

    private static ElaboratedNetlist Elaborate(string src)
    {
        var (lib, tb) = Read(src);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static double FirstFrequencyHz(FrequencySpec f) => ResolveHz(f.StartExpr, f.StartUnit);
    private static double LastFrequencyHz (FrequencySpec f) => ResolveHz(f.StopExpr,  f.StopUnit);
    private static double StepHz          (FrequencySpec f) => ResolveHz(f.StepExpr,  f.StepUnit);

    private static double ResolveHz(string expr, string unit)
    {
        double v = double.Parse(expr, System.Globalization.CultureInfo.InvariantCulture);
        return v * unit.ToUpperInvariant() switch
        {
            "GHZ" => 1e9, "MHZ" => 1e6, "KHZ" => 1e3, _ => 1.0,
        };
    }

    private static IEnumerable<string> WriterOutputForEveryAnalysisKind()
    {
        var tb  = new TestBench("tb");
        var lib = new Library("l");

        tb.Analyses.Add(new DcAnalysis("DC1"));
        tb.Analyses.Add(new SParameterAnalysis("SP1",
            new FrequencySpec("1", "10", 201, SweepKind.Linear, "GHz", "GHz")));
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2e9" });
        tb.Analyses.Add(new LoadpullAnalysis("LP1")
            { ToneExpr = "2e9", LoadTunerName = "Load", SourceTunerName = "Src", GridPath = "g.gam" });
        tb.Analyses.Add(new LoadpullPursuitAnalysis("LPP1")
            { ToneExpr = "2e9", LoadTunerName = "Load", SourceTunerName = "Src" });
        tb.Analyses.Add(new ParametricSweepAnalysis("SW1", "Pin", [0.0, 1.0], "HB1"));
        tb.Analyses.Add(new ParametricSweepAnalysis("SW2", "Pin",
            new SweepSpec(0, 10, 11, SweepAxisMode.PointCount, SweepKind.Linear, ""), "HB1"));

        foreach (var a in tb.Analyses)
        {
            var one = new TestBench("tb");
            one.Analyses.Add(a);
            // The sweeps reference HB1, so carry it along for the chain to resolve.
            if (a is ParametricSweepAnalysis)
                one.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2e9" });
            yield return CnlWriter.Write(one, lib, header: null);
        }
    }

    private static string MinimalDirectiveFor(AnalysisDirectiveSpec spec) => spec.Type switch
    {
        "dc"               => "analysis A1 type=dc\n",
        "sparam"           => "analysis A1 type=sparam start=1 GHz stop=2 GHz npts=3\n",
        "hb"               => "analysis A1 type=hb Tone=2e9\n",
        "env"              => "analysis A1 type=env Stop=10e-6 Accuracy=draft\n",
        "loadpull"         => "analysis A1 type=loadpull Tone=2e9 LoadTuner=L SourceTuner=S Grid=\"g.gam\"\n",
        "loadpull_pursuit" => "analysis A1 type=loadpull_pursuit Tone=2e9 LoadTuner=L SourceTuner=S\n",
        "parametric_sweep" => "analysis Inner1 type=hb Tone=2e9\n" +
                              "analysis A1 type=parametric_sweep Var=P Inner=Inner1 Values=1,2\n",
        "fields"           => "analysis A1 type=fields block=X1 freq=298 freqUnit=MHz\n",
        "trl"              => "analysis A1 type=trl thru=T reflect=R line=L reflectType=short\n",
        "osc"              => "analysis A1 type=osc Fguess=0.5 Unit=GHz\n",
        _ => throw new Xunit.Sdk.XunitException(
                 $"type={spec.Type} has no minimal directive in this test. Add one — a declared type " +
                 "token with no worked example is a token nothing proves the reader types."),
    };
}
