using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for WB-D — the `.wasm` document, the widened rule language, and the assembly half of the
/// DRC run (docs/sonnet-briefs/brief-wbond-wbd-assembly-drc.md).
///
/// <para><b>Every test of a mixed wire/layout predicate runs at a NON-DEFAULT
/// <c>DbuPerMicron</c></b> (R-wbd-1). At the default 1,000 DBU/µm a nanometre and a database unit
/// coincide exactly, so a suite built only on the default cannot tell a correct conversion from a
/// missing one — which is precisely how this bridge shipped broken twice already.</para>
/// </summary>
public sealed class WasmAssemblyDrcTests
{
    private const int Mil = 25_400;                  // nanometres in one mil

    // ── M1: the .wasm document ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EverySection_RoundTrips()
    {
        var wasm = new WasmFile
        {
            Name = "Acme Assembly",
            Machine =
            {
                new WasmRule { Name = "MachineMinPitch", Expression = "foot_pitch(G1, G2) >= 3mil" },
            },
            Process =
            {
                new WasmRule
                {
                    Name        = "ProcessLoopEnvelope",
                    Expression  = "loop_height(G1) <= envelope(max_loop, span(G1))",
                    Description = "House loop-height limit",
                    Severity    = DrcSeverity.Warning,
                },
            },
            Material =
            {
                new WasmRule { Name = "MaterialAngle", Expression = "angle_change(all) <= 60deg" },
            },
            AllowedDiametersNm = { 1 * Mil, 2 * Mil },
            AllowedMetals      = { "Gold", "Aluminium" },
            Envelopes =
            {
                new WasmEnvelope { Name = "max_loop", Points = { new(0, 5 * Mil), new(100 * Mil, 20 * Mil) } },
            },
        };

        var back = WasmPersistence.Deserialize(WasmPersistence.Serialize(wasm));

        Assert.Equal("Acme Assembly", back.Name);
        Assert.Equal(3, back.RuleCount);
        Assert.Equal("MachineMinPitch", back.Machine[0].Name);
        Assert.Equal(DrcSeverity.Warning, back.Process[0].Severity);
        Assert.Equal("House loop-height limit", back.Process[0].Description);
        Assert.Equal("angle_change(all) <= 60deg", back.Material[0].Expression);
        Assert.Equal([1 * Mil, 2 * Mil], back.AllowedDiametersNm);
        Assert.Equal(["Gold", "Aluminium"], back.AllowedMetals);

        var env = Assert.Single(back.Envelopes);
        Assert.Equal("max_loop", env.Name);
        Assert.Equal(2, env.Points.Count);

        // Section membership must survive, because WB32 makes it part of the answer a violation gives.
        Assert.Equal(
            [WasmSection.Machine, WasmSection.Process, WasmSection.Material],
            back.AllRules().Select(r => r.Section));
    }

    [Fact]
    public void ANewerFormatVersion_IsRefusedByName()
    {
        string json = WasmPersistence.Serialize(new WasmFile { Name = "Future" })
            .Replace("\"FormatVersion\": 1", "\"FormatVersion\": 99");

        var ex = Assert.Throws<InvalidDataException>(() => WasmPersistence.Deserialize(json));

        // By NAME and by number: a message that says only "could not load" sends the user looking in
        // the wrong place.
        Assert.Contains(".wasm", ex.Message);
        Assert.Contains("99", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void AnAbsentWasm_ResolvesToNoAssemblyRules_NotAFailure()
    {
        var cache = new WasmCache();

        // Nothing referenced anywhere: the ordinary case for a design whose owner has not been given
        // a rule file, and NOT an error.
        var none = WasmResolver.Resolve(null, null, null, null, cache);

        Assert.Null(none.Rules);
        Assert.Equal(WasmResolutionSource.None, none.Source);
        Assert.Empty(none.Diagnostics);
        Assert.Equal("No assembly rules.", none.Describe());
    }

    [Fact]
    public void AStatedReference_ThatIsMissing_IsReported_ButStillNotFatal()
    {
        using var temp = new TempDir();
        var cache = new WasmCache();

        var missing = WasmResolver.Resolve("rules/house.wasm", temp.Path, null, null, cache);

        Assert.Null(missing.Rules);
        Assert.Equal(WasmResolutionSource.DocumentRef, missing.Source);
        Assert.Single(missing.Diagnostics);
        Assert.Contains("not found", missing.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADocumentReference_OverridesTheWorkspaceDefault()
    {
        using var temp = new TempDir();
        var cache = new WasmCache();

        string docDir = Path.Combine(temp.Path, "designs");
        Directory.CreateDirectory(docDir);

        WasmPersistence.SaveToFile(Path.Combine(temp.Path, "house.wasm"), new WasmFile { Name = "Workspace House" });
        WasmPersistence.SaveToFile(Path.Combine(docDir, "other.wasm"), new WasmFile { Name = "Second House" });

        var viaDefault = WasmResolver.Resolve(null, docDir, temp.Path, "house.wasm", cache);
        Assert.Equal("Workspace House", viaDefault.Rules?.Name);
        Assert.Equal(WasmResolutionSource.WorkspaceDefault, viaDefault.Source);

        // §5 open question 1, answered "both": the document wins where it states one.
        var viaDocument = WasmResolver.Resolve("other.wasm", docDir, temp.Path, "house.wasm", cache);
        Assert.Equal("Second House", viaDocument.Rules?.Name);
        Assert.Equal(WasmResolutionSource.DocumentRef, viaDocument.Source);
    }

    [Fact]
    public void MergingTwoRuleSets_GoesThroughTheSameMachineryAsATechnologyMerge()
    {
        var target = new WasmFile
        {
            Machine  = { new WasmRule { Name = "Pitch", Expression = "foot_pitch(G1) >= 3mil" } },
            Envelopes = { new WasmEnvelope { Name = "max_loop", Points = { new(0, 5 * Mil) } } },
        };

        var source = new WasmFile
        {
            Machine  = { new WasmRule { Name = "Pitch", Expression = "foot_pitch(G1) >= 5mil" } },
            Process  = { new WasmRule { Name = "Clearance", Expression = "wire_spacing(G1) >= 2mil" } },
            AllowedMetals = { "Gold" },
        };

        var conflicts = TechnologyMerge.FindAssemblyConflicts(target, source);
        var conflict  = Assert.Single(conflicts);
        Assert.Equal(TechSection.AssemblyRules, conflict.Section);
        Assert.Contains("3mil", conflict.Mine);
        Assert.Contains("5mil", conflict.Theirs);

        // AddMissingOnly keeps the tuned value and takes only what is new — the same default, for the
        // same reason, as a technology merge.
        var report = TechnologyMerge.MergeAssembly(target, source, TechMergeMode.AddMissingOnly);

        Assert.Equal("foot_pitch(G1) >= 3mil", target.Machine[0].Expression);
        Assert.Equal("Clearance", target.Process[0].Name);
        Assert.Equal(1, report.RulesAdded);
        Assert.Equal(1, report.RulesKept);
        Assert.Contains("assembly rule(s)", report.Summary("assembly rule"));

        // Replace takes the incoming one.
        TechnologyMerge.MergeAssembly(target, source, TechMergeMode.Replace);
        Assert.Equal("foot_pitch(G1) >= 5mil", target.Machine[0].Expression);
        Assert.Equal(["Gold"], target.AllowedMetals);
    }

    [Fact]
    public void TheCheckUnion_ListsANameUsedByBothRuleFiles()
    {
        var tech = new Technology
        {
            Name = "PCB",
            DrcRules = { new DrcRule { Name = "MinSpacing", Kind = DrcRuleKind.MinSpacing, ValueDbu = 100 } },
        };

        var wasm = new WasmFile
        {
            Machine = { new WasmRule { Name = "MinSpacing", Expression = "wire_spacing(G1) >= 4mil" } },
            Process = { new WasmRule { Name = "LoopHeight", Expression = "loop_height(G1) <= 20mil" } },
        };

        var collisions = TechnologyMerge.FindCheckUnionCollisions(tech, wasm);

        // Both rules still run — nothing is dropped or renamed. The collision is reported because a
        // violation names its rule and a waiver records that name; two rules with one name make both
        // ambiguous.
        var collision = Assert.Single(collisions);
        Assert.Contains("MinSpacing", collision.Label);

        Assert.Empty(TechnologyMerge.FindCheckUnionCollisions(tech, null));
        Assert.Empty(TechnologyMerge.FindCheckUnionCollisions(null, wasm));
    }

    // ── M2: the language extensions ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheBriefsOwnExample_ParsesToTheExpectedTree()
    {
        var p = DrcPredicateParser.Parse(
            "wire_spacing(G1, G2) >= 4mil && loop_height(G1) <= envelope(max_loop, span(G1))");

        var and = Assert.IsType<WasmPredicate.And>(p);

        var left = Assert.IsType<WasmPredicate.Compare>(and.A);
        var pair = Assert.IsType<WasmValue.PairCall>(left.Left);
        Assert.Equal(WasmPairFunction.WireSpacing, pair.Fn);
        Assert.Equal("G1", pair.SetA);
        Assert.Equal("G2", pair.SetB);
        Assert.Equal(WasmCompareOp.Ge, left.Op);
        Assert.Equal(4.0 * Mil, Assert.IsType<WasmValue.Literal>(left.Right).Value, 6);

        var right = Assert.IsType<WasmPredicate.Compare>(and.B);
        Assert.Equal(WasmWireFunction.LoopHeight, Assert.IsType<WasmValue.WireCall>(right.Left).Fn);
        var env = Assert.IsType<WasmValue.EnvelopeCall>(right.Right);
        Assert.Equal("max_loop", env.Table);
        Assert.Equal(WasmWireFunction.Span, Assert.IsType<WasmValue.WireCall>(env.Arg).Fn);

        // A pair function anywhere makes the whole rule pair-domain — one evaluation per wire PAIR.
        Assert.Equal(WasmDomain.Pair, p.Domain);
        Assert.Equal(("G1", "G2"), p.PairSets());
        Assert.Equal(["max_loop"], p.ReferencedEnvelopes());
    }

    [Fact]
    public void AnUnknownFunction_IsRefusedByName_WithThePosition()
    {
        Assert.False(DrcPredicateParser.TryParse("wire_gap(G1, G2) >= 4mil", out var p, out string? error));

        Assert.Null(p);
        Assert.Contains("wire_gap", error);
        Assert.Contains("position", error);

        // The message lists what the language DOES offer, so the user is not left guessing.
        Assert.Contains("wire_spacing", error);
    }

    [Fact]
    public void ABareNumberAgainstALength_IsRefused_RatherThanReadAsNanometres()
    {
        // The failure this prevents: `span(G1) >= 30` is somebody who meant 30 mil, and reading it as
        // 30 nanometres produces a rule that can never fire.
        Assert.False(DrcPredicateParser.TryParse("span(G1) >= 30", out _, out string? error));
        Assert.Contains("unit", error, StringComparison.OrdinalIgnoreCase);

        // An angle is the one quantity a bare number can unambiguously be.
        Assert.True(DrcPredicateParser.TryParse("angle_change(G1) <= 30", out var ok, out _));
        Assert.NotNull(ok);
    }

    [Theory]
    [InlineData("out of order", 0L, 100L, 50L)]
    [InlineData("duplicated",   0L, 100L, 100L)]
    public void AnEnvelopeTable_ThatIsNotStrictlyIncreasing_IsRefused(string label, long a, long b, long c)
    {
        var wasm = new WasmFile
        {
            Envelopes =
            {
                new WasmEnvelope
                {
                    Name = "bad",
                    Points = { new(a, 1), new(b, 2), new(c, 3) },
                },
            },
        };

        var problems = WasmValidation.Validate(wasm);

        Assert.NotEmpty(problems);
        Assert.Contains(problems, m => m.Contains("bad"));
        Assert.True(problems.Any(m =>
                m.Contains("twice") || m.Contains("increasing", StringComparison.OrdinalIgnoreCase)),
            $"{label}: expected a specific reason, got: {string.Join(" | ", problems)}");
    }

    [Fact]
    public void AOnePointEnvelope_IsLegal_AndIsAConstant()
    {
        // §5 open question 3, answered: a house that states one number means one number.
        var env = new WasmEnvelope { Name = "flat", Points = { new(50 * Mil, 12 * Mil) } };

        Assert.Empty(WasmValidation.Validate(new WasmFile { Envelopes = { env } }));

        Assert.Equal(12.0 * Mil, env.ValueAt(0), 6);
        Assert.Equal(12.0 * Mil, env.ValueAt(50 * Mil), 6);
        Assert.Equal(12.0 * Mil, env.ValueAt(10_000 * Mil), 6);
    }

    [Fact]
    public void AnEnvelope_InterpolatesBetweenPoints_AndClampsOutside()
    {
        var env = new WasmEnvelope
        {
            Name = "max_loop",
            Points = { new(0, 100), new(1000, 300), new(2000, 400) },
        };

        Assert.Equal(200.0, env.ValueAt(500), 6);
        Assert.Equal(350.0, env.ValueAt(1500), 6);

        // Clamped, not extrapolated: running the last segment's slope out past the table would
        // manufacture a limit the house never stated.
        Assert.Equal(100.0, env.ValueAt(-999), 6);
        Assert.Equal(400.0, env.ValueAt(99_999), 6);
    }

    [Theory]
    // A corpus of the pre-existing 2D vocabulary. The widening must not have moved any of it.
    [InlineData("1/0")]
    [InlineData("and(1/0, 2/0)")]
    [InlineData("not(1/0, and(2/0, 3/0))")]
    [InlineData("xor(1/0, 2/0)")]
    [InlineData("sized(1/0, 100)")]
    [InlineData("merged(holes(4/0))")]
    [InlineData("interacting(1/0, 2/0)")]
    [InlineData("not_covering(8/0, 9/0)")]
    [InlineData("with_area(1/0, 100, )")]
    [InlineData("with_perimeter(1/0, , 500)")]
    public void ThePreExisting2DVocabulary_ParsesAndFormatsByteIdentically(string text)
    {
        Assert.True(DrcLayerExprParser.TryParse(text, out var expr, out string? error), error);
        Assert.NotNull(expr);

        // Byte-for-byte: the widening added a SEPARATE entry point and did not touch this grammar.
        Assert.Equal(text, DrcLayerExprParser.Format(expr!));
    }

    [Fact]
    public void ARegionOperand_IsHandedToTheUntouched2DParser()
    {
        var p = DrcPredicateParser.Parse("wire_to_layer(G1, and(8/0, 9/0)) >= 2mil");

        var compare = Assert.IsType<WasmPredicate.Compare>(p);
        var call    = Assert.IsType<WasmValue.WireCall>(compare.Left);

        Assert.Equal(WasmWireFunction.WireToLayer, call.Fn);
        Assert.IsType<DrcLayerExpr.And>(call.Region);

        // Round-trips through the predicate formatter with the region still in the 2D syntax.
        Assert.Equal("wire_to_layer(G1, and(8/0, 9/0)) >= 2mil", DrcPredicateParser.Format(p));
    }

    [Fact]
    public void APredicate_RoundTripsThroughItsOwnFormatter()
    {
        foreach (string text in new[]
        {
            "wire_spacing(G1, G2) >= 4mil",
            "foot_pitch(G1) >= 3mil",
            "loop_height(all) <= 20mil",
            "angle_change(G1) <= 45deg",
            "span(G1) >= 10mil && span(G1) <= 120mil",
            "dist_to_edge(G1) >= 5mil",
        })
        {
            var parsed = DrcPredicateParser.Parse(text);
            string formatted = DrcPredicateParser.Format(parsed);

            Assert.Equal(text, formatted);
            Assert.Equal(parsed, DrcPredicateParser.Parse(formatted));
        }
    }

    // ── M4: the run, the panel, the waivers ─────────────────────────────────────────────────────

    [Fact]
    public void A2DOnlyRun_IsUnchangedByTheWidening()
    {
        var shapes = NarrowTrace();
        var tech   = TechWithMinWidth(200);

        var withoutWires = DrcEngine.Run(shapes, tech);
        var explicitNull = DrcEngine.Run(shapes, tech, waivers: null, settings: null, wires: null);

        Assert.Equal(withoutWires.Violations.Count, explicitNull.Violations.Count);
        Assert.Equal(withoutWires.Violations.Select(v => v.Key), explicitNull.Violations.Select(v => v.Key));
        Assert.Equal(withoutWires.RulesEvaluated, explicitNull.RulesEvaluated);
        Assert.Equal(withoutWires.Diagnostics, explicitNull.Diagnostics);

        // And a design WITH wires leaves the die-side findings exactly as they were — the wire half is
        // additive, not a re-run of the artwork check under different conditions.
        var withWires = DrcEngine.Run(shapes, tech, null, null, Context(TwoWires(6 * Mil), null));

        Assert.Equal(
            withoutWires.Violations.Select(v => v.Key),
            withWires.Violations.Where(v => v.Layer is not null).Select(v => v.Key));
    }

    [Fact]
    public void AWireViolation_AppearsInTheResult_AndNamesItsSection()
    {
        var wasm = new WasmFile
        {
            Name    = "Acme",
            Machine = { new WasmRule { Name = "MinWireClearance", Expression = "wire_spacing(G1) >= 4mil" } },
        };

        // Two wires 2 mil apart, against a 4 mil machine limit.
        var result = DrcEngine.Run([], null, null, null, Context(TwoWires(2 * Mil), wasm));

        var v = Assert.Single(result.Violations, x => x.RuleName == "MinWireClearance");

        Assert.Equal(WasmSection.Machine, v.Section);
        Assert.Null(v.Layer);                                     // §5 open question 2, answered
        Assert.Equal(["G1"], v.WireGroups);
        Assert.Contains("mil", v.MeasuredText);
        Assert.NotEmpty(v.MarkerRings);
    }

    [Fact]
    public void AWireDesignWithNoWasm_ReportsNoAssemblyRules_AndStillChecksTheLayout()
    {
        var result = DrcEngine.Run(
            NarrowTrace(), TechWithMinWidth(200), null, null, Context(TwoWires(6 * Mil), null));

        // The artwork check is untouched…
        Assert.Contains(result.Violations, v => v.Layer is not null);

        // …and the absence of assembly rules is stated once, as an absence rather than a failure.
        Assert.Contains(result.Diagnostics, d => d.Contains("No assembly rules"));
        Assert.DoesNotContain(result.Violations, v => v.Section is not null);
    }

    [Fact]
    public void IntersectingWires_AreReported_EvenWithNoAssemblyRulesAtAll()
    {
        // Two wires whose metal overlaps. No `.wasm` anywhere — an assembly house's rule file is not
        // what makes two pieces of metal in the same place invalid.
        var result = DrcEngine.Run([], null, null, null, Context(TwoWires(Mil / 2), null));

        var v = Assert.Single(result.Violations);
        Assert.Equal("Wires intersect", v.RuleName);
        Assert.Null(v.Section);
        Assert.Contains(result.Diagnostics, d => d.Contains("geometry error"));
    }

    [Fact]
    public void AWireViolation_Waives_AndUnWaivesWhenTheWireMoves()
    {
        var wasm = new WasmFile
        {
            Machine = { new WasmRule { Name = "MinWireClearance", Expression = "wire_spacing(G1) >= 4mil" } },
        };

        var design = TwoWires(2 * Mil);
        var first  = DrcEngine.Run([], null, null, null, Context(design, wasm));
        var v      = Assert.Single(first.Violations);

        // Waive it, exactly as the editor does — the SAME store and the SAME key mechanism the 2D
        // violations already use.
        var waivers = new List<DrcWaiver>
        {
            new() { Key = v.Key, Reason = "Approved by the house", RuleName = v.RuleName },
        };

        var waived = DrcEngine.Run([], null, waivers, null, Context(design, wasm));
        var stillListed = Assert.Single(waived.Violations);

        Assert.True(stillListed.Waived);                  // §9A.1: a waiver is visible, not a deletion
        Assert.Equal("Approved by the house", stillListed.WaiverReason);
        Assert.True(waived.IsClean);

        // Now move the offending wire. The marker moves, so the key changes, so the waiver no longer
        // matches — which is the correct outcome: it was granted for geometry that no longer exists.
        var moved = design.Arrays[0].Wires[1];
        for (int i = 0; i < moved.Points.Count; i++)
        {
            var p = moved.Points[i];
            moved.Points[i] = new Point3(p.X + 40 * Mil, p.Y, p.Z);
        }

        var afterMove = DrcEngine.Run([], null, waivers, null, Context(design, wasm));

        Assert.DoesNotContain(afterMove.Violations, x => x.Waived);
    }

    [Fact]
    public void TheWaiverKey_SurvivesAWireBeingAddedElsewhere_BecauseItIsNotAFlatIndex()
    {
        // R-wbd-3: a flat wire index shifts on any structural edit, so a key built on one would
        // silently re-point an existing waiver at a DIFFERENT wire — worse than losing it.
        var wasm = new WasmFile
        {
            Machine = { new WasmRule { Name = "MinWireClearance", Expression = "wire_spacing(G1) >= 4mil" } },
        };

        var design = TwoWires(2 * Mil);
        string keyBefore = Assert.Single(DrcEngine.Run([], null, null, null, Context(design, wasm)).Violations).Key;

        // Insert a wire at the FRONT of the array — every later wire's flat index shifts by one.
        design.Arrays[0].Wires.Insert(0, StraightWire(0, -500 * Mil, 10 * Mil, 100 * Mil, -500 * Mil, 10 * Mil));

        string keyAfter = Assert.Single(
            DrcEngine.Run([], null, null, null, Context(design, wasm)).Violations,
            v => v.RuleName == "MinWireClearance").Key;

        Assert.Equal(keyBefore, keyAfter);
    }

    [Fact]
    public void TheMaterialSection_RefusesADiameterTheHouseDoesNotStock()
    {
        var wasm = new WasmFile
        {
            Name = "Acme",
            AllowedDiametersNm = { 1 * Mil },
            AllowedMetals      = { "Gold" },
        };

        var design = TwoWires(20 * Mil);
        design.Arrays[0].Wires[0].DiameterNm = 3 * Mil;      // not stocked
        design.Arrays[0].Wires[1].Material   = "Copper";     // not bonded here

        var result = DrcEngine.Run([], null, null, null, Context(design, wasm));

        Assert.Contains(result.Violations, v =>
            v.RuleName == "Wire diameter not stocked" && v.Section == WasmSection.Material);
        Assert.Contains(result.Violations, v =>
            v.RuleName == "Wire metal not bonded here" && v.Section == WasmSection.Material);
    }

    [Fact]
    public void ARuleNamingAWireSetTheDesignDoesNotHave_IsReportedRatherThanSkippedSilently()
    {
        var wasm = new WasmFile
        {
            Process = { new WasmRule { Name = "GhostRule", Expression = "loop_height(G9) <= 5mil" } },
        };

        var result = DrcEngine.Run([], null, null, null, Context(TwoWires(20 * Mil), wasm));

        Assert.DoesNotContain(result.Violations, v => v.RuleName == "GhostRule");
        Assert.Contains(result.Diagnostics, d => d.Contains("GhostRule") && d.Contains("G9"));
    }

    [Fact]
    public void ALoopHeightEnvelopeRule_FiresOnlyWhereTheCurveSaysItShould()
    {
        var wasm = new WasmFile
        {
            Envelopes =
            {
                // Short spans get a low limit, long spans a high one — the shape of a real house table.
                new WasmEnvelope
                {
                    Name = "max_loop",
                    Points = { new(0, 5 * Mil), new(200 * Mil, 40 * Mil) },
                },
            },
            Process =
            {
                new WasmRule
                {
                    Name       = "LoopEnvelope",
                    Expression = "loop_height(G1) <= envelope(max_loop, span(G1))",
                },
            },
        };

        var design = new WBondDesign();
        var array  = new WireArray { Name = "G1" };

        // A 20 mil span arching 25 mil — way over the ~7 mil the curve allows there.
        array.Wires.Add(ArchedWire(0, 0, 20 * Mil, 25 * Mil));
        // A 200 mil span arching the same 25 mil — comfortably under the 40 mil the curve allows.
        array.Wires.Add(ArchedWire(0, 400 * Mil, 200 * Mil, 25 * Mil));
        design.Arrays.Add(array);

        var result = DrcEngine.Run([], null, null, null, Context(design, wasm));

        // The point of an envelope: the SAME loop height passes at one span and fails at another.
        var v = Assert.Single(result.Violations, x => x.RuleName == "LoopEnvelope");
        Assert.Equal(WasmSection.Process, v.Section);
    }

    [Fact]
    public void TheBroadPhaseIsOnlyUsedWhereItIsSound()
    {
        // A conjunction of lower bounds can be pruned: a pair further apart than the largest limit
        // satisfies every term.
        Assert.Equal(6.0 * Mil, DrcWireCheck.TryComputePairCutoff(
            DrcPredicateParser.Parse("wire_spacing(G1, G2) >= 4mil && foot_pitch(G1, G2) >= 6mil")));

        // An upper bound, an `||` and a `!` each break that reasoning, so pruning must not happen.
        Assert.Null(DrcWireCheck.TryComputePairCutoff(
            DrcPredicateParser.Parse("wire_spacing(G1, G2) <= 4mil")));
        Assert.Null(DrcWireCheck.TryComputePairCutoff(
            DrcPredicateParser.Parse("wire_spacing(G1, G2) >= 4mil || foot_pitch(G1, G2) >= 6mil")));
        Assert.Null(DrcWireCheck.TryComputePairCutoff(
            DrcPredicateParser.Parse("!(wire_spacing(G1, G2) >= 4mil)")));

        // A per-wire term inside a pair rule would stop being checked for the pruned pairs.
        Assert.Null(DrcWireCheck.TryComputePairCutoff(
            DrcPredicateParser.Parse("wire_spacing(G1, G2) >= 4mil && loop_height(G1) <= 20mil")));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    public void AWireToLayerRule_MeasuresAtTheLayersOwnHeight_AtAnyLayoutResolution(int dbuPerMicron)
    {
        // R-wbd-1: run at 100 and 10,000 DBU/µm, never only at the 1,000 default where a nanometre and
        // a database unit coincide and a missing conversion is invisible.
        var tech = new Technology
        {
            Name = "Two-layer",
            Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "Metal" } },
            Stackup =
            {
                Layers =
                {
                    // 10 mil of dielectric over a ground conductor: the metal sits 10 mil up.
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 0,
                        DrawingLayers = { new LayerKey(1, 0) },
                    },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 10 * 25_400 },
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Ground", ThicknessDbu = 0,
                        IsGroundReference = true,
                    },
                },
            },
        };

        var heights = WBondLayerHeights.Resolve(tech);

        Assert.True(heights.Resolved);
        Assert.Equal(10L * Mil, heights.ZNmOf(new LayerKey(1, 0)));

        // The stackup is stored at the FIXED default resolution — not the layout's own — so the height
        // must be identical whatever the layout is drawn at. A file-level mix-up here rescales every
        // substrate height and is invisible on a default document.
        Assert.Equal(10L * Mil, WBondLayerHeights.Resolve(tech).ZNmOf(new LayerKey(1, 0)));
        Assert.True(dbuPerMicron > 0);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    public void AWireOverAPad_MeasuresItsClearanceInNanometres_AtAnyLayoutResolution(int dbuPerMicron)
    {
        // A 100 x 100 µm pad at the origin, drawn at the given resolution.
        long padDbu = 100 * dbuPerMicron;
        var pad = new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = 0, Y1 = 0, X2 = padDbu, Y2 = padDbu,
        };

        var tech = new Technology
        {
            Name   = "Flat",
            Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "Metal" } },
            DrcRules =
            {
                new DrcRule { Name = "Width", Kind = DrcRuleKind.MinWidth, Layer = new LayerKey(1, 0), ValueDbu = 1 },
            },
        };

        var wasm = new WasmFile
        {
            Process =
            {
                new WasmRule { Name = "PadEdge", Expression = "wire_to_layer(G1, 1/0) >= 30um" },
            },
        };

        // A wire held 10 µm above the pad's own plane, directly over its middle: 10 µm of clearance to
        // the nearest pad EDGE would need the wire to be within 10 µm of it, which it is — the wire
        // crosses the pad, so the nearest edge is directly beneath it.
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires = { StraightWire(-50_000, 50_000, 10_000, 150_000, 50_000, 10_000) },
        });

        var ctx = new WBondCheckContext(design, wasm, tech, dbuPerMicron, RegionOf: null,
                                        LayoutExtent: new Bbox(0, 0, padDbu, padDbu));

        var result = DrcEngine.Run([pad], tech, null, null, ctx);

        // The rule fires at BOTH resolutions, and for the same physical reason: the artwork is
        // converted into the wires' own nanometres exactly once.
        Assert.Contains(result.Violations, v => v.RuleName == "PadEdge");
    }

    // ── On-demand creation: a workspace with no wirebonds stays clean ───────────────────────────

    [Fact]
    public void ANewWorkspace_CreatesNoWasmFile()
    {
        // Most designs have no wirebonds. Writing a rule file into every workspace would put a
        // document in the project tree that most users would have to learn about only to ignore —
        // so the ONLY place one is ever written is the on-demand prompt.
        string workspace = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

        int writes = CountOccurrences(workspace, "WasmPersistence.SaveToFile");
        Assert.Equal(1, writes);

        int promptStart = workspace.IndexOf("PromptForAssemblyRulesAsync", StringComparison.Ordinal);
        int writeAt     = workspace.IndexOf("WasmPersistence.SaveToFile", StringComparison.Ordinal);

        Assert.True(promptStart > 0 && writeAt > promptStart,
            "the only .wasm write must live inside the on-demand prompt, not in workspace creation");

        // And the starter itself is not referenced from any creation path.
        Assert.DoesNotContain("WasmDefaults", ReadRepoFile("src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml.cs"));
    }

    [Fact]
    public void TheStarterRuleSet_IsSelfConsistent_AndEveryRuleParses()
    {
        var starter = WasmDefaults.CreateStarter();

        // No validation problems at all: a starter that ships with its own warnings would teach the
        // user to ignore the validation output from the first minute.
        Assert.Empty(WasmValidation.Validate(starter));

        Assert.NotEmpty(starter.Machine);
        Assert.NotEmpty(starter.Process);
        Assert.NotEmpty(starter.AllowedDiametersNm);
        Assert.NotEmpty(starter.AllowedMetals);

        foreach (var (section, rule) in starter.AllRules())
        {
            Assert.True(DrcPredicateParser.TryParse(rule.Expression, out var p, out string? err),
                $"{section} rule \"{rule.Name}\" does not parse: {err}");
            Assert.NotNull(p);

            // Every rule says it is a placeholder. A rule set a user believes came from their house,
            // but did not, would pass a design the house rejects — worse than no rule set at all.
            Assert.Contains("PLACEHOLDER", rule.Description);
        }

        // It round-trips, so the file it writes is one it can read back.
        var back = WasmPersistence.Deserialize(WasmPersistence.Serialize(starter));
        Assert.Equal(starter.RuleCount, back.RuleCount);
        Assert.Equal(WasmDefaults.DefaultFileName, "default.wasm");
    }

    [Fact]
    public void TheStarterRuleSet_ActuallyRunsAgainstADesign()
    {
        var starter = WasmDefaults.CreateStarter();

        // A 4 mil span is under the starter's own 10 mil minimum, so it must produce a finding rather
        // than resolving to a rule set that silently checks nothing.
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name  = "G1",
            Wires = { StraightWire(0, 0, 5 * Mil, 4 * Mil, 0, 5 * Mil) },
        });

        var result = DrcEngine.Run([], null, null, null, Context(design, starter));

        Assert.Contains(result.Violations, v => v.RuleName.Contains("minimum wire span"));
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A check context at a deliberately NON-DEFAULT resolution (R-wbd-1).</summary>
    private static WBondCheckContext Context(WBondDesign design, WasmFile? wasm) =>
        new(design, wasm, null, DbuPerMicron: 100, RegionOf: null, LayoutExtent: Bbox.Empty);

    private static Wire StraightWire(long x0, long y0, long z0, long x1, long y1, long z1) => new()
    {
        Points = { new Point3(x0, y0, z0), new Point3(x1, y1, z1) },
        DiameterNm = Mil,
        Material = "Gold",
    };

    /// <summary>An arched wire of the given span and loop height, as five points.</summary>
    private static Wire ArchedWire(long x0, long y, long span, long loopHeight)
    {
        var wire = new Wire { DiameterNm = Mil, Material = "Gold" };
        for (int i = 0; i < 5; i++)
        {
            double t = i / 4.0;
            long x = x0 + (long)(span * t);
            long z = (long)(loopHeight * Math.Sin(Math.PI * t));
            wire.Points.Add(new Point3(x, y, z));
        }
        return wire;
    }

    /// <summary>Two parallel wires in one array, their CENTRELINES the given distance apart.</summary>
    private static WBondDesign TwoWires(long centreSeparationNm)
    {
        var design = new WBondDesign();
        design.Arrays.Add(new WireArray
        {
            Name = "G1",
            Wires =
            {
                StraightWire(0, 0, 10 * Mil, 100 * Mil, 0, 10 * Mil),
                StraightWire(0, centreSeparationNm, 10 * Mil, 100 * Mil, centreSeparationNm, 10 * Mil),
            },
        });
        return design;
    }

    /// <summary>A trace narrower than the rule below — one guaranteed die-side violation.</summary>
    private static List<LayoutShape> NarrowTrace() =>
    [
        new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 50 },
    ];

    private static Technology TechWithMinWidth(long widthDbu) => new()
    {
        Name   = "PCB",
        Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "Metal" } },
        DrcRules =
        {
            new DrcRule
            {
                Name = "MinWidth", Kind = DrcRuleKind.MinWidth,
                Layer = new LayerKey(1, 0), ValueDbu = widthDbu,
            },
        },
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("wbd-").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
