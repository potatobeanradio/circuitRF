// ================================================================
//  PropertyTests.cs — the gate for brief-lvs-10-properties.md §7.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  Two things, and the first is the unusual one. The property tolerances were MEASURED off
//  `examples/LVS/Bias tee/` rather than chosen, so the FIRST test here recomputes that measurement
//  on every run and fails if a later PCell change widens the quantisation spread past the tolerance
//  that was set to accommodate it. Without that, the shipped numbers become magic within a release
//  and the first symptom of a widened spread is LVS passing a design that is genuinely wrong.
//
//  The rest is what the comparison says: which SENTENCE a difference gets (a derived value, an
//  unread one and an ordinary one send the designer to three different places), what is exact and
//  what has slack, and — the two easiest things to get wrong at this scale — that four hundred
//  identical land patterns produce ONE info line and that a device nothing matched produces none.
//
//  ── WHY SO FEW OF THESE RUN THE WHOLE PASS ────────────────────────────────────────────────────
//
//  The fixtures have three dimensions between them and every one of their values is correct, which
//  is the point of them. Everything about a DIFFERENCE is therefore built here: two netlists, the
//  real `LvsCompare` to pair them, and the real property pass over the pairs it produced. Using the
//  real comparator rather than a hand-made correspondence is what makes R-lvs10-1c ("no property
//  findings on an unmatched device") a fact about the code rather than about the test.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class PropertyTests
{
    private const string Mmic   = "Bias tee";
    private const string Broken = "Attenuator broken";

    private static string Lvs(params string[] parts)
        => Path.Combine([RepoRoot(), "examples", "LVS", .. parts]);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    // ══ 1 — the derivation is reproducible, and a widened spread fails HERE ══════════════════════
    //
    // Gate 1, R-lvs10-6a/6c. Each part in the MMIC cell is drawn on a 0.25 µm grid, so the value its
    // geometry resolves to is not exactly the one the schematic asked for; that gap is the floor
    // under any tolerance, because a tolerance tighter than it rejects good artwork. The shipped
    // default has to sit above the widest measured gap and below one step of the E96 series, which
    // is the smallest wrong part anybody could have fitted.

    [Fact]
    public void EveryShippedToleranceSitsBetweenTheTwoBoundsItWasDerivedFrom()
    {
        var measured = SpreadsOfTheCorrectMmicCell();

        Assert.NotEmpty(LvsPropertyTolerances.Derivations);
        foreach (var d in LvsPropertyTolerances.Derivations)
        {
            Assert.True(d.ObservedSpread < d.Chosen && d.Chosen < d.SmallestFault,
                $"{d.Dimension}: {d.Chosen} is not between {d.ObservedSpread} and {d.SmallestFault}");

            Assert.True(measured.TryGetValue(d.Dimension, out double now),
                $"{d.Dimension} has a recorded derivation and no representative in the fixture — "
                + "one of the two has moved.");

            Assert.True(now <= d.ObservedSpread,
                $"{d.Dimension}: the correct MMIC cell now spreads {now:G6}, wider than the "
                + $"{d.ObservedSpread:G6} this tolerance was derived from. {d.Evidence}");
        }

        // And the correct cell is silent about every one of them, which is the whole claim.
        Assert.DoesNotContain(
            LvsRun.Run(Lvs(Mmic)).Diagnostics,
            f => f.Id.StartsWith("lvs.property.", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-lvs10-6a's step 2, run again: the widest gap between what the schematic asked for and what
    /// the artwork resolved to, per unit dimension, over the whole correct cell.
    /// </summary>
    private static Dictionary<UnitDimension, double> SpreadsOfTheCorrectMmicCell()
    {
        var result = LvsRun.Run(Lvs(Mmic));
        var widest = new Dictionary<UnitDimension, double>();

        foreach (var pair in result.Comparison.Devices)
        {
            var s = result.Schematic.Devices[pair.Schematic];
            var l = result.Layout.Devices[pair.Layout];

            foreach (var (name, asked) in s.Parameters)
            {
                if (asked is not double a || !l.Parameters.TryGetValue(name, out object? drawn)) continue;
                if (drawn is not double b) continue;

                var dimension = s.ParameterFacts.TryGetValue(name, out var fact)
                    ? fact.Dimension : UnitDimension.None;
                if (dimension == UnitDimension.None) continue;

                double spread = Math.Abs(a - b) / Math.Max(Math.Abs(a), Math.Abs(b));
                widest[dimension] = Math.Max(widest.GetValueOrDefault(dimension), spread);
            }
        }
        return widest;
    }

    // ══ 2 — F6 names both values AND the tolerance ══════════════════════════════════════════════
    //
    // Gate 2, R-lvs10-3c. Never the word "mismatch" alone: the defaults are provisional, so a wrong
    // one has to be visible on the line it produced rather than latent in a table nobody opens.

    [Fact]
    public void TheWrongPartOnTheBrokenBoardNamesBothValuesAndTheToleranceApplied()
    {
        var finding = Assert.Single(
            LvsRun.Run(Lvs(Broken)).Findings, f => f.Id == "lvs.property.mismatch");

        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal("R3", finding.Diagnostic.Arguments["schematicPath"]);
        Assert.Equal("R", finding.Diagnostic.Arguments["name"]);
        Assert.Equal("294 Ω", finding.Diagnostic.Arguments["schematic"]);
        Assert.Equal("150 Ω", finding.Diagnostic.Arguments["layout"]);
        Assert.Equal("1 %", finding.Diagnostic.Arguments["tolerance"]);

        // R-lvs8-2b/2c: it names the designer's own part and there is somewhere to look.
        Assert.Contains("R3", finding.Objects);
        Assert.True(finding.HasMarker);
    }

    // ══ 3 — two rows straddling the boundary, not a ladder ══════════════════════════════════════

    [Theory]
    [InlineData(100.9, null)]                      // 0.892 % — inside 1 %
    [InlineData(101.1, "lvs.property.mismatch")]   // 1.088 % — outside it
    public void AValueInsideTheToleranceIsSilentAndOneJustOutsideIsAFinding(double drawn, string? expected)
        => AssertOnly(expected, Pass(
            Two(("R", 100.0, UnitDimension.Resistance)), Two(("R", drawn, UnitDimension.Resistance))));

    // ══ 4 — forty identical land patterns are ONE line ══════════════════════════════════════════
    //
    // Gate 4, R-lvs10-2b. The land pattern is shared by every 0402 on the board, so a value stored
    // on it would be wrong for all but one; claiming nothing is the correct thing for it to do, and
    // forty lines saying so is a report nobody reads.

    [Fact]
    public void ABoardWithNoLayoutSideValuesSaysSoOncePerDeviceTypeAndNotOncePerDevice()
    {
        var schematic = new Build();
        var layout    = new Build();
        for (int i = 1; i <= 40; i++)
        {
            schematic.Device($"R{i}", DeviceKind.Resistor, "Resistor", [$"a{i}", $"b{i}"],
                             ("R", 100.0, UnitDimension.Resistance));
            layout.Cell($"R{i}", "R0402", [$"a{i}", $"b{i}"]).Anchor($"R{i}");
        }

        var found = Pass(schematic, layout);

        var only = Assert.Single(found, d => d.Id == "lvs.property.layout-silent");
        Assert.Equal(DiagnosticSeverity.Info, only.Severity);
        Assert.Equal("R0402", only.Arguments["cellName"]);
        Assert.Equal(40, only.Arguments["devices"]);
        Assert.Equal("R", only.Arguments["parameters"]);

        // R-lvs10-2b's own point: not a finding, and nothing above info came of it.
        Assert.DoesNotContain(found, d => d.Severity > DiagnosticSeverity.Info);
    }

    // ══ 5 and 6 — a derived value and an unread one get their own sentences ═════════════════════
    //
    // Gate 5/6, R-lvs10-4b/4c. Asserted on the ID, which differs: "C1 value mismatch" and "the
    // layout's geometry gives 1.82 pF; the schematic asks for 2.0 pF" send a designer to two
    // different files, and an unread parameter sends them nowhere because nothing is wrong.

    [Fact]
    public void ADerivedParameterReportsTheDerivedSentenceAndAnUnreadOneIsOnlyInformation()
    {
        var asked = Two(("C", 2.0e-12, UnitDimension.Capacitance));

        var derived = Assert.Single(Pass(asked, Two(("C", 1.82e-12, UnitDimension.None)).Computed("C")));
        Assert.Equal("lvs.property.derived-differs", derived.Id);
        Assert.Equal(DiagnosticSeverity.Error, derived.Severity);
        Assert.Equal("2 pF", derived.Arguments["schematic"]);
        Assert.Equal("1.82 pF", derived.Arguments["layout"]);
        Assert.Equal("1 %", derived.Arguments["tolerance"]);

        var unread = Assert.Single(Pass(asked, Two(("C", 1.82e-12, UnitDimension.None)).Unread("C")));
        Assert.Equal("lvs.property.unread-differs", unread.Id);
        Assert.Equal(DiagnosticSeverity.Info, unread.Severity);
    }

    // ══ 7 — multiplicity ════════════════════════════════════════════════════════════════════════
    //
    // Gate 7, R-lvs10-5. Four fingers against Nf=4 is the design working; against Nf=3 it is a
    // property finding and not a topology one, because the merge is what made the comparison
    // possible; against a schematic that declares no multiplicity at all it is an
    // under-specification the designer should know about, and the finding names all four.

    [Theory]
    [InlineData(4, null)]
    [InlineData(3, "lvs.property.multiplicity")]
    [InlineData(0, "lvs.reduce.multiplicity-unstated")]
    public void FourInParallelAreJudgedAgainstWhatTheSchematicDeclares(int nf, string? expected)
    {
        var schematic = new Build().Boundary("in", "out");
        if (nf > 0) schematic.Device("M1", DeviceKind.Resistor, "Resistor", ["in", "out"], ("Nf", (double)nf, UnitDimension.None));
        else        schematic.Device("M1", DeviceKind.Resistor, "Resistor", ["in", "out"]);

        var layout = new Build().Boundary("in", "out");
        foreach (string finger in new[] { "M1", "M2", "M3", "M4" })
            layout.Cell(finger, "FET", ["in", "out"]);
        layout.Anchor("M1");

        var found = Pass(schematic, layout);
        var finding = AssertOnly(expected, found);

        if (expected == "lvs.reduce.multiplicity-unstated")
        {
            Assert.Equal(4, finding!.Arguments["devices"]);
            Assert.Equal("M1, M2, M3, M4", finding.Arguments["group"]);
        }
        else if (expected is not null)
        {
            Assert.Equal(3, finding!.Arguments["schematic"]);
            Assert.Equal(4, finding.Arguments["layout"]);
        }
    }

    // ══ 8 — a model name has no tolerance ═══════════════════════════════════════════════════════

    [Fact]
    public void AModelNameDifferingByOneCharacterIsAFinding()
    {
        var one = Assert.Single(Pass(
            Two(("Model", "nch_18", UnitDimension.None)),
            Two(("Model", "nch_10", UnitDimension.None))));

        Assert.Equal("lvs.property.mismatch", one.Id);
        Assert.Equal("'nch_18'", one.Arguments["schematic"]);
        Assert.Equal("exact", one.Arguments["tolerance"]);

        Assert.Empty(Pass(
            Two(("Model", "nch_18", UnitDimension.None)),
            Two(("Model", "nch_18", UnitDimension.None))));
    }

    // ══ 9 — a geometric parameter is absolute, in DBU ═══════════════════════════════════════════
    //
    // Gate 9, R-lvs10-3e. A sub-DBU difference cannot exist, so the bound is the database's own
    // resolution rather than a percentage — and a difference of exactly one DBU is a real one.

    [Theory]
    [InlineData(1.0e-6, null)]                       // the same value
    [InlineData(1.001e-6, "lvs.property.mismatch")]  // one DBU at 1000 DBU/µm
    public void AGeometricParameterDiffersByOneDbuOrNotAtAll(double drawn, string? expected)
    {
        var found = Pass(
            Two(("W", 1.0e-6, UnitDimension.Length)), Two(("W", drawn, UnitDimension.None)),
            tolerances: LvsPropertyTolerances.For(null, dbuPerMicron: 1000));

        var finding = AssertOnly(expected, found);
        if (finding is not null) Assert.Equal("1 nm", finding.Arguments["tolerance"]);
    }

    // ══ 10 — a technology override changes the verdict and the printed number ═══════════════════

    [Fact]
    public void ATechnologyOverrideChangesBothTheVerdictAndThePrintedTolerance()
    {
        var schematic = Two(("R", 100.0, UnitDimension.Resistance));
        var layout    = Two(("R", 100.5, UnitDimension.Resistance));

        // Shipped: half a percent is inside one percent.
        Assert.Empty(Pass(schematic, layout));

        var tight = new Technology
        {
            LvsTolerances = [new LvsToleranceRule { Dimension = UnitDimension.Resistance, Relative = 0.001 }],
        };
        var finding = Assert.Single(Pass(
            schematic, layout, tolerances: LvsPropertyTolerances.For(tight, 1000)));

        Assert.Equal("lvs.property.mismatch", finding.Id);
        Assert.Equal("0.1 %", finding.Arguments["tolerance"]);

        // And the other direction: a process that holds nothing to better than half is silent on
        // the same two numbers the shipped table would have reported.
        var loose = new Technology
        {
            LvsTolerances = [new LvsToleranceRule { Dimension = UnitDimension.Resistance, Relative = 0.5 }],
        };
        Assert.Empty(Pass(
            Two(("R", 294.0, UnitDimension.Resistance)), Two(("R", 150.0, UnitDimension.Resistance)),
            tolerances: LvsPropertyTolerances.For(loose, 1000)));
    }

    // ══ 11 — a device nothing matched gets no property finding ══════════════════════════════════
    //
    // Gate 11, R-lvs10-1c. One fault, one finding: comparing the parameters of devices that may not
    // correspond produces noise proportional to the size of the design, and the noise buries the
    // topology finding that caused it.

    [Fact]
    public void AnUnmatchedDeviceProducesNoPropertyFindings()
    {
        var schematic = new Build().Boundary("in", "out")
            .Device("R1", DeviceKind.Resistor, "Resistor", ["in", "mid"], ("R", 100.0, UnitDimension.Resistance))
            .Device("R9", DeviceKind.Resistor, "Resistor", ["mid", "out"], ("R", 999.0, UnitDimension.Resistance));

        var layout = new Build().Boundary("in", "out")
            .Cell("R1", "R0402", ["in", "mid"], ("R", 100.0, UnitDimension.Resistance)).Anchor("R1");

        var comparison = LvsCompare.Compare(schematic.Netlist(), layout.Netlist());
        Assert.Contains(comparison.Findings, d => d.Id == "lvs.device.unmatched-schematic");

        Assert.Empty(LvsProperties.Compare(
            schematic.Netlist(), layout.Netlist(), comparison, LvsPropertyTolerances.For(null, 1000)));
    }

    // ══ 12 — a dimension nobody measured is exact, and says so ══════════════════════════════════
    //
    // Gate 12, R-lvs10-6e. No tolerance is invented before it is measured: the comparison is exact
    // and the run states the gap, which is honest and is what makes it visible enough to close.

    [Fact]
    public void ADimensionWithNoFixtureRepresentativeIsExactAndTheRunSaysSo()
    {
        var found = Pass(
            Two(("Vt", 0.70, UnitDimension.Voltage)), Two(("Vt", 0.7000001, UnitDimension.None)));

        var mismatch = Assert.Single(found, d => d.Id == "lvs.property.mismatch");
        Assert.Equal("exact (no tolerance has been measured for Voltage)", mismatch.Arguments["tolerance"]);

        var gap = Assert.Single(found, d => d.Id == "lvs.property.tolerance-unestablished");
        Assert.Equal(DiagnosticSeverity.Info, gap.Severity);
        Assert.Equal("Voltage", gap.Arguments["dimension"]);
        Assert.Equal(1, gap.Arguments["compared"]);
    }

    // ══ R-lvs10-2d — the artwork states values and this one is not among them ═══════════════════
    //
    // Deliberately NOT the same as the silent land pattern above: this cell HAS a parameter list, so
    // a parameter the schematic asks for and it does not carry means the two are not the same
    // generator. The reverse — a generator carrying dimensions the schematic never mentions — is
    // every PCell there is and is silent.

    [Fact]
    public void AParameterTheArtworksGeneratorDoesNotCarryIsAWarningAndTheReverseIsSilent()
    {
        var missing = Assert.Single(Pass(
            Two(("W", 1.0e-5, UnitDimension.Length)),
            Two(("L", 2.0e-5, UnitDimension.None), ("R", 50.0, UnitDimension.None))));

        Assert.Equal("lvs.property.missing", missing.Id);
        Assert.Equal(DiagnosticSeverity.Warning, missing.Severity);
        Assert.Equal("W", missing.Arguments["name"]);
        Assert.Equal("L, R", missing.Arguments["stated"]);

        Assert.Empty(Pass(
            Two(("R", 50.0, UnitDimension.Resistance)),
            Two(("R", 50.0, UnitDimension.None), ("W", 1.0e-5, UnitDimension.None))));
    }

    // ── Running the pass over two built netlists ─────────────────────────────────────────────

    /// <summary>
    /// The real comparator, then the real property pass over the pairs it produced — never a
    /// hand-made correspondence, because "after topology, never before" is the requirement.
    /// </summary>
    private static IReadOnlyList<Diagnostic> Pass(
        Build schematic, Build layout, LvsPropertyTolerances? tolerances = null)
    {
        var s = schematic.Netlist();
        var l = LvsReduce.Apply(layout.Netlist()).Reduced;
        return LvsProperties.Compare(
            s, l, LvsCompare.Compare(s, l), tolerances ?? LvsPropertyTolerances.For(null, 1000));
    }

    /// <summary>One two-terminal device between the same two boundary nets on either side.</summary>
    private static Build Two(params (string Name, object Value, UnitDimension Dimension)[] values)
        => new Build().Boundary("in", "out")
                      .Device("X1", DeviceKind.Resistor, "Resistor", ["in", "out"], values)
                      .Anchor("X1");

    /// <summary>Exactly the expected id and nothing else, or nothing at all.</summary>
    private static Diagnostic? AssertOnly(string? expected, IReadOnlyList<Diagnostic> found)
    {
        if (expected is null) { Assert.Empty(found); return null; }
        var one = Assert.Single(found);
        Assert.Equal(expected, one.Id);
        return one;
    }

    // ── A netlist, by hand ───────────────────────────────────────────────────────────────────

    private sealed class Build
    {
        private readonly List<LvsDevice> _devices = [];
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
        private readonly List<string?> _labels = [];
        private readonly List<List<(int Device, int Terminal)>> _pins = [];
        private readonly List<string> _boundary = [];

        public Build Boundary(params string[] nets)
        {
            _boundary.AddRange(nets);
            foreach (string net in nets) Index(net);
            return this;
        }

        public Build Device(
            string path, DeviceKind kind, string typeName, string[] nets,
            params (string Name, object Value, UnitDimension Dimension)[] values)
            => Add(path, new DeviceType(kind, null, typeName), nets, values);

        /// <summary>A layout placement: a cell, with the land pattern's own directory as its
        /// identity, which is what <c>DeviceTypes.OfLayout</c> answers for an ordinary board.</summary>
        public Build Cell(
            string path, string cellName, string[] nets,
            params (string Name, object Value, UnitDimension Dimension)[] values)
            => Add(path, new DeviceType(DeviceKind.Cell, null, cellName), nets, values);

        /// <summary>Marks the last device as claiming its counterpart on the other side is called
        /// <paramref name="schematicPath"/> — brief 7's tier-0 anchor.</summary>
        public Build Anchor(string schematicPath)
        {
            _devices[^1] = _devices[^1] with { AnchorId = schematicPath };
            return this;
        }

        /// <summary>Marks a parameter of the last device as one the generator DERIVED.</summary>
        public Build Computed(string name) => Fact(name, f => f with { Computed = true });

        /// <summary>Marks a parameter of the last device as one the generator never READ.</summary>
        public Build Unread(string name) => Fact(name, f => f with { Unread = true });

        public LvsNetlist Netlist() => new(
            _devices,
            [.. Enumerable.Range(0, _labels.Count).Select(i => new LvsNet(i, _labels[i], _pins[i]))],
            [.. _boundary.Select(Index)],
            []);

        private Build Add(
            string path, DeviceType type, string[] nets,
            (string Name, object Value, UnitDimension Dimension)[] values)
        {
            int device = _devices.Count;
            var terminals = new List<LvsTerminal>(nets.Length);
            for (int i = 0; i < nets.Length; i++)
            {
                int net = Index(nets[i]);
                terminals.Add(new LvsTerminal(i + 1, "", net));
                _pins[net].Add((device, i));
            }

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            var facts      = new Dictionary<string, LvsParameterFact>(StringComparer.Ordinal);
            foreach (var (name, value, dimension) in values)
            {
                parameters[name] = value;
                facts[name] = new LvsParameterFact(dimension);
            }

            _devices.Add(new LvsDevice(
                path, path, type, terminals, parameters, new LvsProvenance("fixture", path, 0, 0))
            {
                ParameterFacts = facts,
            });
            return this;
        }

        private Build Fact(string name, Func<LvsParameterFact, LvsParameterFact> change)
        {
            var facts = new Dictionary<string, LvsParameterFact>(
                _devices[^1].ParameterFacts, StringComparer.Ordinal);
            facts[name] = change(facts.GetValueOrDefault(name));
            _devices[^1] = _devices[^1] with { ParameterFacts = facts };
            return this;
        }

        private int Index(string net)
        {
            if (_byName.TryGetValue(net, out int existing)) return existing;
            _labels.Add(net == "0" ? "0" : null);
            _pins.Add([]);
            return _byName[net] = _labels.Count - 1;
        }
    }
}
