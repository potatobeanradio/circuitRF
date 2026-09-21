// ================================================================
//  SchematicNetlistTests.cs — the gate for brief-lvs-4-schematic-netlist.md §7.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  A `.csch` becomes the SAME LvsNetlist type a `.clay` becomes, through the round trip the GUI's
//  own Simulate performs, with one canonical device type answering for both sides.
//
//  Three of these tests are worth more than the rest and are the reason the file exists:
//
//   * TheQuotedParameterSurvivesTheRoundTripAndFailsWithoutIt — asserts BOTH directions. The
//     negative is what pins the REASON for the round trip; without it the round trip reads as
//     ceremony and the next person removes it.
//   * SchematicAndLayoutAgreeOnDeviceType — the brief calls it the single most valuable test in
//     it, and it is: two sides that disagree about what a part IS produce findings nobody can act
//     on, in a design that is correct.
//   * EveryRegisteredGeneratorHasASymbolKind — fails on a NEW generator with no entry, rather than
//     letting one fall back to Unknown at runtime, where two "unknown" devices then match.
//
//  Every finding is asserted by DIAGNOSTIC ID and never by prose (R-aut1-8): the id is the contract
//  a caller filters on, and a test that pins a sentence teaches people not to improve sentences.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class SchematicNetlistTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-lvs4-" + Guid.NewGuid().ToString("N")[..12]);

    public SchematicNetlistTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ══ 1 — the divider again, from the other side ═══════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> The same two-resistor circuit brief 3 reads out of copper, read out of a
    /// drawing: two devices, four terminals, three nets, and the middle net is the shared one.
    /// </summary>
    /// <remarks>
    /// <b>Asserted structurally</b>, as the brief requires — the net NUMBERING is each side's own,
    /// and pinning it here would be pinning an implementation detail of one extraction against the
    /// other. What has to agree is the partition: how many nets, how many terminals on each, and
    /// which two terminals share one.
    /// </remarks>
    [Fact]
    public void ATwoResistorDividerIsThreeNetsTwoDevicesAndFourTerminals()
    {
        var netlist = Read(Divider(), "divider.csch");

        Assert.Empty(netlist.Notes.Where(n => n.Severity >= DiagnosticSeverity.Warning));
        Assert.Equal(["R1", "R2"], netlist.Devices.Select(d => d.Path));
        Assert.Equal(4, netlist.Devices.Sum(d => d.Terminals.Count));
        Assert.Equal(3, netlist.Nets.Count);

        int r1a = NetOf(netlist, "R1", 1), r1b = NetOf(netlist, "R1", 2);
        int r2a = NetOf(netlist, "R2", 1), r2b = NetOf(netlist, "R2", 2);

        Assert.Equal(r1b, r2a);                                    // the middle net
        Assert.Equal(3, new[] { r1a, r1b, r2b }.Distinct().Count());
        Assert.Equal([1, 2, 1], netlist.Nets.Select(n => n.Pins.Count));
    }

    // ══ 2 — the round trip is not ceremony (R-lvs4-1b) ═══════════════════════════════════════════

    /// <summary>
    /// <b>Gate 2, and the reason §1 of the brief exists.</b> A schematic parameter is an
    /// EXPRESSION, so a bare word fails elaboration — unless it has been through the `.cnl`, which
    /// quotes it on the way out and reads it back as a string.
    /// </summary>
    /// <remarks>
    /// <b>Both directions, deliberately.</b> "It reads without error" alone is also what a reader
    /// that ignored the parameter entirely would say. The negative half — the identical model,
    /// elaborated straight out of extraction, throwing the elaborator's own "Unresolved name"
    /// sentence — is what pins the REASON, and without it the round trip looks like ceremony and
    /// the next person deletes it.
    /// </remarks>
    [Fact]
    public void TheQuotedParameterSurvivesTheRoundTripAndFailsWithoutIt()
    {
        var netlist = Read(BareWordParameter(), "bench.csch", includeFixture: true);

        Assert.DoesNotContain("lvs.schematic.elaboration-failed", Ids(netlist));
        Assert.Contains(netlist.Devices, d => d.Path == "T1");

        // The same model, elaborated WITHOUT the round trip — extraction's own TestBench.
        var raw = NetExtractor.Extract(BareWordParameter(), "bench", DiskCellResolver.Instance);
        var thrown = Record.Exception(() => new Elaborator(raw.Library).Elaborate(raw.TestBench));

        Assert.NotNull(thrown);
        Assert.Contains("on", thrown!.Message, StringComparison.Ordinal);
    }

    // ══ 3, 4 — the exclusion list (R-lvs4-3) ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 3.</b> A VAR, a MEAS, three Grounds, a Pin and a disabled resistor contribute no
    /// device — and every ground terminal is on net <c>"0"</c> (R-lvs4-3c).
    /// </summary>
    [Fact]
    public void NothingOnTheExclusionListBecomesADevice()
    {
        var netlist = Read(Excluded(), "excluded.csch");

        Assert.Equal(["R1"], netlist.Devices.Select(d => d.Path));

        // Ground is excluded as a DEVICE and is the reason net "0" exists: R1's lower terminal sits
        // on it, and it is index 0 because ground is always first.
        Assert.Equal("0", netlist.Nets[0].Label);
        Assert.Equal(0, NetOf(netlist, "R1", 2));
    }

    /// <summary>
    /// <b>Gate 4.</b> The list lives in ONE place and both readers consult it (R-lvs4-3b).
    /// </summary>
    /// <remarks>
    /// <b>Two halves, because either alone is weak.</b> The source scan says neither file restates
    /// the kinds; the behavioural half walks <see cref="SchematicExclusions.NeverADevice"/> itself
    /// and asserts that for EVERY member neither reader emits anything — so a kind added to the set
    /// is covered by this test the moment it is added, which is what "adding a kind changes both"
    /// actually needs to mean when the set is immutable.
    /// </remarks>
    [Fact]
    public void TheExclusionListIsSharedRatherThanRestated()
    {
        // Both readers ASK, and neither answers for itself.
        foreach (string file in new[] { "src/Design/Schematic/NetExtractor.cs",
                                        "src/Design/Layout/Lvs/SchematicRead.cs" })
        {
            string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), file)));
            Assert.Contains("SchematicExclusions.", code, StringComparison.Ordinal);

            // Scanned for the EQUALITY and not for the name: NetExtractor legitimately asks
            // `!= SymbolKind.Var` and `!= SymbolKind.Ground` elsewhere — those are the loops that
            // ROUTE a VAR to Variables and resolve ground to net "0", which is a different question
            // from "is this a device". What may not come back is `== SymbolKind.<excluded>` beside
            // a `continue`, which is the restatement this rule exists to prevent.
            foreach (var kind in SchematicExclusions.NeverADevice)
                Assert.DoesNotContain($"== SymbolKind.{kind}", code, StringComparison.Ordinal);
        }

        // And the set itself is declared once, in the file whose job that is.
        var declaring = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal))
            .Where(f => StripComments(File.ReadAllText(f))
                        .Contains("NeverADevice = ", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["SchematicExclusions.cs"], declaring);

        foreach (var kind in SchematicExclusions.NeverADevice)
        {
            var model = new SchematicEditModel();
            model.Components.Add(new EditableComponent { InstanceName = "X1", Symbol = kind, X = 0, Y = 0 });
            model.Components.Add(Resistor("R1", 0, 600));

            var extracted = NetExtractor.Extract(model, "one", DiskCellResolver.Instance);
            Assert.DoesNotContain(extracted.TestBench.Instances, i => i.InstanceName == "X1");
            Assert.DoesNotContain(Read(model, "one.csch").Devices, d => d.Path == "X1");
        }
    }

    // ══ 5 — cell ports are in PORT order, not drawing order (R-lvs4-4b) ══════════════════════════

    /// <summary>
    /// <b>Gate 5.</b> Three <c>Pin</c>s drawn in the order 2, 3, 1 produce boundary nets in port
    /// order 1, 2, 3 — because <c>NetExtractor.BuildCellPorts</c> sorts by <c>Num</c>, which is
    /// already the schematic's own answer about which port is which.
    /// </summary>
    /// <remarks>
    /// The oracle is the NET each boundary entry lands on, never its index: each port is wired to
    /// its own resistor, so "boundary[0] is the net R1 touches" is a statement about the port
    /// number and not about the order a list happened to come out in.
    /// </remarks>
    [Fact]
    public void BoundaryNetsFollowThePortNumberAndNotTheDrawingOrder()
    {
        var netlist = Read(ThreePortCell(), "cell.csch");

        Assert.Equal(3, netlist.BoundaryNets.Count);
        Assert.Equal(NetOf(netlist, "R1", 1), netlist.BoundaryNets[0]);   // Num=1, drawn last
        Assert.Equal(NetOf(netlist, "R2", 1), netlist.BoundaryNets[1]);   // Num=2, drawn first
        Assert.Equal(NetOf(netlist, "R3", 1), netlist.BoundaryNets[2]);   // Num=3, drawn second
    }

    // ══ 6 — --testbench (R-lvs4-4c/d) ════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 6.</b> One testbench cell, read both ways. Without <c>--testbench</c> the Terms are
    /// not devices and their nets are the boundary; with it they are ordinary devices and there is
    /// no boundary to speak of.
    /// </summary>
    [Fact]
    public void TestBenchScopeDecidesWhetherTheFixtureIsADeviceOrABoundary()
    {
        var excluded = Read(Bench(), "bench.csch", isTestBenchCell: true);

        Assert.Equal(["R1"], excluded.Devices.Select(d => d.Path));
        Assert.Equal([NetOf(excluded, "R1", 1), NetOf(excluded, "R1", 2)], excluded.BoundaryNets);
        Assert.Contains("lvs.scope.testbench-excluded", Ids(excluded));
        Assert.Equal(DiagnosticSeverity.Info,
            excluded.Notes.Single(n => n.Id == "lvs.scope.testbench-excluded").Severity);

        var included = Read(Bench(), "bench.csch", includeFixture: true, isTestBenchCell: true);

        Assert.Equal(["P1", "R1", "P2"], included.Devices.Select(d => d.Path));
        Assert.Empty(included.BoundaryNets);
        Assert.DoesNotContain("lvs.scope.testbench-excluded", Ids(included));
    }

    // ══ 7 — the canonical device type (R-lvs4-5b) ════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 7, and the brief calls it the single most valuable test in it.</b> For every kind
    /// circuitRF can place, the schematic side and the layout side answer with a type that
    /// <see cref="DeviceType.CouldBe"/> accepts — and the kinds themselves agree.
    /// </summary>
    /// <remarks>
    /// <b><see cref="DeviceType.CouldBe"/> and not record equality</b>, because the two are not
    /// equal and must not be: a built-in resistor has no cell folder on the schematic side and a
    /// land pattern on the layout side, so the DIRECTORY field legitimately differs while the
    /// answer to "could these be the same part" is yes. Equality would have forced the layout side
    /// to forget where the part lives, which is the strongest identity it has.
    ///
    /// <para>The cell cases — an imported component, a kit part, a hand-drawn cell — are one case
    /// three times over on purpose: all three land as an ordinary cell folder, which is exactly
    /// R-lvs3-3b's claim that a kit needs no registration.</para>
    /// </remarks>
    [Theory]
    [InlineData(SymbolKind.Resistor,  DeviceKind.Resistor)]
    [InlineData(SymbolKind.Capacitor, DeviceKind.Capacitor)]
    [InlineData(SymbolKind.Inductor,  DeviceKind.Inductor)]
    [InlineData(SymbolKind.Snp,       DeviceKind.Unknown)]
    [InlineData(SymbolKind.Sdd,       DeviceKind.Unknown)]
    [InlineData(SymbolKind.Diode,     DeviceKind.Diode)]
    [InlineData(SymbolKind.FetCurtice, DeviceKind.Transistor)]
    [InlineData(SymbolKind.Mlin,      DeviceKind.TransmissionLine)]
    [InlineData(SymbolKind.MBend,     DeviceKind.TransmissionLine)]
    [InlineData(SymbolKind.MTee,      DeviceKind.TransmissionLine)]
    [InlineData(SymbolKind.MCross,    DeviceKind.TransmissionLine)]
    [InlineData(SymbolKind.Mtaper,    DeviceKind.TransmissionLine)]
    [InlineData(SymbolKind.Mklopf,    DeviceKind.TransmissionLine)]
    public void SchematicAndLayoutAgreeOnDeviceType(SymbolKind kind, DeviceKind expected)
    {
        var schematic = DeviceTypes.OfSchematic(
            new EditableComponent { InstanceName = "X1", Symbol = kind }, _root);

        // The layout side of the SAME part: a land pattern that declares its PartKind, which is how
        // a layout-first placement says what it is.
        var layout = DeviceTypes.OfLayout(
            new LayoutInstance { CellRef = "cells/Land", PartKind = LayoutPartKind.Name(kind) },
            Path.Combine(_root, "cells", "Land"), null);

        Assert.Equal(expected, schematic.Kind);
        Assert.Equal(expected, layout.Kind);
        Assert.True(schematic.CouldBe(layout));
        Assert.True(layout.CouldBe(schematic));
    }

    /// <summary>
    /// <b>Gate 7, the cell half.</b> A hand-drawn cell, an imported component and a kit part are
    /// one case: all three resolve to a cell FOLDER, and an absolute path is an identity that needs
    /// no name matching and nothing registered anywhere (R-lvs4-5b, R-lvs3-3b).
    /// </summary>
    [Fact]
    public void ACellResolvesToTheSameDirectoryFromBothSides()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Amp");
        string schematicDir = Path.Combine(_root, "top");
        Directory.CreateDirectory(schematicDir);

        var schematic = DeviceTypes.OfSchematic(
            new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../Amp" },
            schematicDir);
        var layout = DeviceTypes.OfLayout(
            new LayoutInstance { CellRef = "../Amp" }, cellDir, null);

        Assert.Equal(Path.GetFullPath(cellDir), Path.GetFullPath(schematic.CellDir!));
        Assert.True(schematic.CouldBe(layout));
        Assert.Equal(DeviceKind.Cell, schematic.Kind);

        // And a DIFFERENT folder is a veto, which is the half that makes the first half mean
        // something: "they match" is also what a function returning true always would say.
        string other = CellFolder.CreateCellFolder(_root, "Buffer");
        Assert.False(schematic.CouldBe(DeviceTypes.OfLayout(
            new LayoutInstance { CellRef = "../Buffer" }, other, null)));
    }

    // ══ 8, 9 — the generator map (R-lvs4-5c/d) ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8.</b> Every generator <c>PCellRegistry</c> registers has a <see cref="SymbolKind"/>
    /// here. A new generator with no entry FAILS THIS TEST rather than falling back at runtime:
    /// a silent fallback produces two devices of "unknown" type that then match each other.
    /// </summary>
    /// <remarks>
    /// Asserted over <c>KnownGeneratorIds</c> — the BUILT-IN registrations — and deliberately not
    /// over <c>AllKnownGeneratorIds</c>, which includes whatever resolvers a session happens to
    /// have registered. A kit's own generator is not something this repository can have an entry
    /// for, and requiring one would fail the build for a design nobody here has seen.
    /// </remarks>
    [Fact]
    public void EveryRegisteredGeneratorHasASymbolKind()
    {
        var missing = PCellRegistry.KnownGeneratorIds
            .Where(id => !DeviceTypes.TryGetSymbolKind(id, out _))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal([], missing);

        // And the map is not vacuously satisfied by answering yes to everything.
        Assert.False(DeviceTypes.TryGetSymbolKind("MNOTAGENERATOR", out _));
    }

    /// <summary>
    /// <b>Gate 9.</b> <c>ReverseGeneratorMap</c> is gone and nothing has grown a second copy of it
    /// (R-lvs4-5d) — two maps with one meaning drift, which is this repository's recurring scar.
    /// </summary>
    [Fact]
    public void TheGeneratorMapExistsInExactlyOnePlace()
    {
        var naming = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs",
                                                   SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)) continue;
            string code = StripComments(File.ReadAllText(file));
            if (code.Contains("ReverseGeneratorMap", StringComparison.Ordinal))
                naming.Add(Path.GetFileName(file) + ":gone");

            // The map's own shape: a generator id BOUND to a SymbolKind. Scanned for the binding
            // rather than for the id, because "MKLOPF" is also the engine component's type name and
            // legitimately appears in the model factory, the type registry and the generator itself.
            if (Regex.IsMatch(code, @"=\s*SymbolKind\.Mklopf\s*,"))
                naming.Add(Path.GetFileName(file) + ":map");
        }

        Assert.Equal(["DeviceType.cs:map"], naming);
    }

    // ══ 10 — the N+1 reference terminal (R-lvs4-6b) ══════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 10.</b> An <c>SnP</c> referenced to something other than ground carries an
    /// <c>N+1</c> terminal bound to that net.
    /// </summary>
    /// <remarks>
    /// <b>Dropping it would compare as though the part were grounded</b> — a circuit the designer
    /// did not draw, with no symptom: the device count matches, the terminal count is off by one on
    /// a part nobody looks at, and the finding lands on whatever else happens to be near that net.
    /// </remarks>
    [Fact]
    public void AnSnPReferencedToANonGroundNetCarriesAnNPlusOneTerminal()
    {
        var netlist = Read(ReferencedSnp(), "snp.csch");

        var snp = netlist.Devices.Single(d => d.Path == "S1");
        var reference = snp.Terminals.Single(t => t.Name == "REF");

        Assert.Equal(3, reference.Port);                                   // N = 2, so REF is 3
        Assert.Equal(3, snp.Terminals.Count);
        Assert.NotEqual(0, reference.NetIndex);                            // not ground
        Assert.Equal(NetOf(netlist, "R9", 1), reference.NetIndex);         // the net it was wired to
    }

    // ══ 11 — an elaboration failure is a refusal (R-lvs4-2c) ═════════════════════════════════════

    /// <summary>
    /// <b>Gate 11.</b> A design whose parameters do not resolve comes back with NO netlist and the
    /// elaborator's own sentence — never a partial comparison.
    /// </summary>
    /// <remarks>
    /// <b>Partial is the dangerous outcome, not the merciful one.</b> A design that does not
    /// elaborate has no values to compare AND its topology may depend on the values it could not
    /// resolve; comparing what was left would report on a circuit that does not exist.
    /// </remarks>
    [Fact]
    public void AnElaborationFailureRefusesAndProducesNoPartialNetlist()
    {
        var netlist = Read(UnresolvableParameter(), "broken.csch");

        Assert.Empty(netlist.Devices);
        Assert.Empty(netlist.Nets);
        Assert.Empty(netlist.BoundaryNets);

        var refusal = netlist.Notes.Single(n => n.Id == "lvs.schematic.elaboration-failed");
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
        Assert.Contains("Rnonesuch", refusal.Render(), StringComparison.Ordinal);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    private LvsNetlist Read(SchematicEditModel model, string fileName,
                            bool includeFixture = false, bool isTestBenchCell = false)
        => SchematicRead.Read(model, Path.Combine(_root, fileName), includeFixture, isTestBenchCell);

    private static EditableComponent Resistor(string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "R", Expression = "50" });
        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    /// <summary>Two resistors in series on one vertical run — the same circuit brief 3 reads out of
    /// copper. A resistor at (x, y) puts its pins at (x, y-200) and (x, y+200).</summary>
    private static SchematicEditModel Divider()
    {
        var m = new SchematicEditModel();
        m.Components.Add(Resistor("R1", 0, 200));     // pins (0,0) and (0,400)
        m.Components.Add(Resistor("R2", 0, 800));     // pins (0,600) and (0,1000)
        m.Wires.Add(Wire((0, 400), (0, 600)));        // the middle net
        return m;
    }

    /// <summary>A Tuner carrying <c>BiasTee=on</c> — the real value four of the shipped examples
    /// carry, and the one that fails elaboration as a bare word.</summary>
    private static SchematicEditModel BareWordParameter()
    {
        var m = new SchematicEditModel();
        var tuner = new EditableComponent { InstanceName = "T1", Symbol = SymbolKind.Tuner, X = 0, Y = 200 };
        tuner.Parameters.Add(new EditableParameter { Name = "BiasTee", Expression = "on" });
        // The fundamental termination the Tuner requires — nothing to do with the bare word, and
        // without it the elaboration fails for a reason that would mask the one being tested.
        tuner.Parameters.Add(new EditableParameter { Name = "Z[1]", Expression = "50" });
        m.Components.Add(tuner);
        m.Components.Add(Resistor("R1", 0, 800));
        m.Wires.Add(Wire((0, 400), (0, 600)));
        return m;
    }

    /// <summary>One resistor to ground, surrounded by everything that is never a device.</summary>
    private static SchematicEditModel Excluded()
    {
        var m = new SchematicEditModel();
        m.Components.Add(Resistor("R1", 0, 200));                       // pins (0,0), (0,400)
        m.Components.Add(Ground("GND1", 0, 400));

        var v = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 1000, Y = 0 };
        v.Parameters.Add(new EditableParameter { Name = "w", Expression = "10" });
        m.Components.Add(v);

        var meas = new EditableComponent { InstanceName = "M1", Symbol = SymbolKind.Meas, X = 1000, Y = 400 };
        meas.Parameters.Add(new EditableParameter { Name = "Name", Expression = "m1" });
        meas.Parameters.Add(new EditableParameter { Name = "Expr", Expression = "1" });
        m.Components.Add(meas);

        var pin = new EditableComponent { InstanceName = "P1", Symbol = SymbolKind.Pin, X = -100, Y = 0 };
        pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" });
        m.Components.Add(pin);

        m.Components.Add(Ground("GND2", 2000, 0));
        m.Components.Add(Ground("GND3", 2400, 0));

        var disabled = Resistor("R2", 0, 1200);
        disabled.Disable = DisableState.Open;
        m.Components.Add(disabled);

        return m;

        static EditableComponent Ground(string name, double x, double y)
            => new() { InstanceName = name, Symbol = SymbolKind.Ground, X = x, Y = y };
    }

    /// <summary>Three ports whose <c>Num</c> are 2, 3, 1 in DRAWING order, each on its own
    /// resistor — so the boundary's order can be read off the nets rather than off a list.</summary>
    private static SchematicEditModel ThreePortCell()
    {
        var m = new SchematicEditModel();
        int[] nums = [2, 3, 1];
        string[] owners = ["R2", "R3", "R1"];

        for (int i = 0; i < 3; i++)
        {
            double x = i * 1000;
            m.Components.Add(Resistor(owners[i], x, 200));          // pins (x,0) and (x,400)
            // A Pin's own terminal is at (+100, 0) from its body, so the body goes 100 to the LEFT
            // of the resistor pin it is meant to name.
            var pin = new EditableComponent { InstanceName = $"Port{nums[i]}", Symbol = SymbolKind.Pin, X = x - 100, Y = 0 };
            pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = nums[i].ToString() });
            m.Components.Add(pin);
        }
        return m;
    }

    /// <summary>Port 1 — R — port 2, the shape a bench has.</summary>
    private static SchematicEditModel Bench()
    {
        var m = new SchematicEditModel();
        m.Components.Add(Term("P1", 100, 200, 1));
        m.Components.Add(new EditableComponent { InstanceName = "GP1", Symbol = SymbolKind.Ground, X = 100, Y = 400 });
        m.Components.Add(Resistor("R1", 300, 200));
        m.Components.Add(Term("P2", 500, 600, 2));
        m.Components.Add(new EditableComponent { InstanceName = "GP2", Symbol = SymbolKind.Ground, X = 500, Y = 800 });
        m.Wires.Add(Wire((100, 0), (300, 0)));
        m.Wires.Add(Wire((300, 400), (500, 400)));
        return m;

        static EditableComponent Term(string name, double x, double y, int num)
        {
            var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term, X = x, Y = y };
            c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
            c.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
            return c;
        }
    }

    /// <summary>A 2-port SnP whose reference pin is wired to a net that is not ground.</summary>
    private static SchematicEditModel ReferencedSnp()
    {
        var m = new SchematicEditModel();
        var snp = new EditableComponent
        {
            InstanceName = "S1", Symbol = SymbolKind.Snp, X = 0, Y = 0,
        };
        snp.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "2" });
        snp.Parameters.Add(new EditableParameter { Name = "RefNode",  Expression = "true" });
        m.Components.Add(snp);

        // A resistor holding the reference net up, so it is a real net with two pins on it rather
        // than a dangling terminal. The wire runs from the SnP's OWN reference pin — asked of the
        // component rather than worked out here, so the fixture cannot drift from the geometry.
        var (rx, ry) = snp.GetPortWorldCoord(2);
        m.Components.Add(Resistor("R9", rx + 2000, ry + 200));      // pins (rx+2000, ry) and (…, ry+400)
        m.Wires.Add(Wire((rx, ry), (rx + 2000, ry)));
        return m;
    }

    /// <summary>A resistor whose value names a variable nobody declared.</summary>
    private static SchematicEditModel UnresolvableParameter()
    {
        var m = new SchematicEditModel();
        var r = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 200 };
        r.Parameters.Add(new EditableParameter { Name = "R", Expression = "Rnonesuch" });
        m.Components.Add(r);
        return m;
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static int NetOf(LvsNetlist netlist, string device, int port) =>
        netlist.Devices.Single(d => d.Path == device).Terminals.Single(t => t.Port == port).NetIndex;

    private static IReadOnlyList<string> Ids(LvsNetlist netlist) =>
        [.. netlist.Notes.Select(n => n.Id)];

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline),
                      @"//[^\r\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
