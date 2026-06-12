using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 2 and Layer 3 oracle gates.
///
/// Oracle circuit (RC ladder, 2-port S-parameter):
///   Port:P1  n1 0  Num=1 Z=50
///   R:R1     n1 n2  R=50
///   C:C1     n2 0   C=1e-12
///   Port:P2  n2 0  Num=2 Z=50
///
/// Schematic layout (GridSize=100, all coordinates in world units):
///   P1  at (100, 200) R0 → "+" at (100,  0), "−" at (100, 400)
///   R1  at (300, 200)    → pin0 at (300, 0),  pin1 at (300, 400)
///   C1  at (200, 600)    → pin0 at (200, 400), pin1 at (200, 800)
///   G1  at (200, 800)    → ground pin at (200, 800)    [grounds C1.pin1]
///   GP1 at (100, 400)    → ground pin at (100, 400)    [grounds P1."−"]
///   P2  at (500, 600) R0 → "+" at (500, 400), "−" at (500, 800)
///   GP2 at (500, 800)    → ground pin at (500, 800)    [grounds P2."−"]
///   Wire W1: (100,0)→(300,0)     [P1."+" – R1.pin0 → net n1]
///   Wire W2: (200,400)→(300,400)→(500,400) [C1.pin0 – R1.pin1 – P2."+" → net n2]
/// </summary>
public class ExtractionOracleTests
{
    // ── Oracle schematic builder ─────────────────────────────────────────────

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Term(string name, double x, double y, int num)
    {
        var c = new EditableComponent
        {
            InstanceName = name,
            Symbol       = SymbolKind.Term,
            X = x, Y = y,
        };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        c.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
        return c;
    }

    private static EditableComponent Ground(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Ground, X = x, Y = y };

    private static EditableComponent Resistor(string name, double x, double y, string r,
        SymbolRotation rotation = SymbolRotation.R0)
        => new()
        {
            InstanceName = name,
            Symbol       = SymbolKind.Resistor,
            X = x, Y = y,
            Rotation     = rotation,
            Parameters   = { new EditableParameter { Name = "R", Expression = r } },
        };

    private static EditableComponent Capacitor(string name, double x, double y, string c)
        => new()
        {
            InstanceName = name,
            Symbol       = SymbolKind.Capacitor,
            X = x, Y = y,
            Parameters   = { new EditableParameter { Name = "C", Expression = c } },
        };

    /// <summary>
    /// Builds the oracle schematic.  transposeR1=true rotates R1 by 180° (swapping its terminals).
    /// </summary>
    private static SchematicEditModel BuildOracleSchematic(bool transposeR1 = false)
    {
        var model = new SchematicEditModel();

        // Terms: "+" at local(0,-200), "−" at local(0,+200) (R0 rotation).
        model.Components.Add(Term("P1", 100, 200, num: 1));   // "+" at (100,0), "−" at (100,400)
        model.Components.Add(Resistor("R1", 300, 200, "50",
            transposeR1 ? SymbolRotation.R180 : SymbolRotation.R0));
        model.Components.Add(Capacitor("C1", 200, 600, "1e-12"));
        model.Components.Add(Ground("G1",  200, 800));         // grounds C1.pin1 at (200,800)
        model.Components.Add(Ground("GP1", 100, 400));         // grounds P1."−" at (100,400)
        model.Components.Add(Term("P2", 500, 600, num: 2));   // "+" at (500,400), "−" at (500,800)
        model.Components.Add(Ground("GP2", 500, 800));         // grounds P2."−" at (500,800)

        // Wire W1: P1."+"(100,0) → R1.pin0(300,0)
        model.Wires.Add(Wire((100, 0), (300, 0)));

        // Wire W2: C1.pin0(200,400) → R1.pin1(300,400) → P2."+"(500,400)
        model.Wires.Add(Wire((200, 400), (300, 400), (500, 400)));

        return model;
    }

    /// <summary>
    /// Hand-authored TestBench for the oracle circuit — the ground-truth the extractor must match.
    /// Uses name strings that happen to match the extractor's auto-names (n1/n2/0),
    /// but topology comparison never relies on name equality.
    /// </summary>
    private static TestBench BuildAuthoredTestBench()
    {
        var tb = new TestBench("oracle");
        tb.Instances.Add(new Instance("P1", "Port", ["n1", "0"],
            [new ParameterAssignment("Num", "1"),
             new ParameterAssignment("Z",   "50")]));
        tb.Instances.Add(new Instance("R1", "R", ["n1", "n2"],
            [new ParameterAssignment("R", "50")]));
        tb.Instances.Add(new Instance("C1", "C", ["n2", "0"],
            [new ParameterAssignment("C", "1e-12")]));
        tb.Instances.Add(new Instance("P2", "Port", ["n2", "0"],
            [new ParameterAssignment("Num", "2"),
             new ParameterAssignment("Z",   "50")]));
        return tb;
    }

    // ── Layer 2: topology equivalence ───────────────────────────────────────

    [Fact]
    public void L2_ExtractedTopology_MatchesAuthored()
    {
        var model     = BuildOracleSchematic();
        var extracted = NetExtractor.Extract(model).TestBench;
        var authored  = BuildAuthoredTestBench();

        AssertTopologyEquivalent(authored, extracted);
    }

    [Fact]
    public void L2_TransposedR1_FailsTopologyCheck()
    {
        var model     = BuildOracleSchematic(transposeR1: true);
        var extracted = NetExtractor.Extract(model).TestBench;
        var authored  = BuildAuthoredTestBench();

        // A topology mismatch must be detectable via the partition comparison.
        // ThrowsAny: EqualException is a subtype of XunitException.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertTopologyEquivalent(authored, extracted));
    }

    // ── Layer 3: DataSet equivalence ─────────────────────────────────────────

    [Fact]
    public void L3_ExtractedAndAuthored_ProduceMatchingDataSets()
    {
        var model     = BuildOracleSchematic();
        var extracted = NetExtractor.Extract(model).TestBench;
        var authored  = BuildAuthoredTestBench();

        double[] freqs = [1e9, 2e9, 5e9];

        var elab = new Elaborator();
        var dsExtracted = SParameterEngine.Run(elab.Elaborate(extracted), freqs);
        var dsAuthored  = SParameterEngine.Run(elab.Elaborate(authored),  freqs);

        AssertDataSetsMatch(dsAuthored, dsExtracted, tolerance: 1e-9);
    }

    // ── Topology comparison helpers ──────────────────────────────────────────

    /// <summary>
    /// Returns the set of (instanceName, terminalIndex) endpoints that share the same net
    /// as terminal <paramref name="termIdx"/> of instance <paramref name="instName"/> in
    /// <paramref name="tb"/>. Includes the RefNetBinding as terminal N when non-null.
    /// This partition-set comparison is name-agnostic (auto-name renaming safe).
    /// </summary>
    private static HashSet<(string Inst, int Term)> GetSharedTerminals(
        TestBench tb, string instName, int termIdx)
    {
        var targetInst = tb.Instances.First(i => i.InstanceName == instName);
        string targetNet = termIdx < targetInst.NetBindings.Count
            ? targetInst.NetBindings[termIdx]
            : targetInst.RefNetBinding!;

        var result = new HashSet<(string, int)>();
        foreach (var inst in tb.Instances)
        {
            for (int k = 0; k < inst.NetBindings.Count; k++)
            {
                if (inst.NetBindings[k] == targetNet)
                    result.Add((inst.InstanceName, k));
            }
            if (inst.RefNetBinding is not null && inst.RefNetBinding == targetNet)
                result.Add((inst.InstanceName, inst.NetBindings.Count));
        }
        return result;
    }

    private static void AssertTopologyEquivalent(TestBench authored, TestBench extracted)
    {
        // Same instance names (order independent).
        var authoredNames  = authored.Instances.Select(i => i.InstanceName).OrderBy(x => x).ToList();
        var extractedNames = extracted.Instances.Select(i => i.InstanceName).OrderBy(x => x).ToList();
        Assert.Equal(authoredNames, extractedNames);

        // Same references.
        foreach (var ai in authored.Instances)
        {
            var ei = extracted.Instances.First(i => i.InstanceName == ai.InstanceName);
            Assert.Equal(ai.Reference, ei.Reference);
        }

        // Topology: for each instance terminal, the partition of endpoints that share its net
        // must be identical in both TestBenches.
        foreach (var ai in authored.Instances)
        {
            var ei = extracted.Instances.First(i => i.InstanceName == ai.InstanceName);

            // Terminal count must also match (no extra terminals in extracted).
            int authoredTerms  = ai.NetBindings.Count  + (ai.RefNetBinding  is not null ? 1 : 0);
            int extractedTerms = ei.NetBindings.Count + (ei.RefNetBinding is not null ? 1 : 0);
            Assert.Equal(authoredTerms, extractedTerms);

            for (int k = 0; k < ai.NetBindings.Count; k++)
            {
                var authoredPartition  = GetSharedTerminals(authored,  ai.InstanceName, k);
                var extractedPartition = GetSharedTerminals(extracted, ai.InstanceName, k);
                Assert.Equal(authoredPartition, extractedPartition);
            }
        }
    }

    // ── DataSet comparison helpers ───────────────────────────────────────────

    private static void AssertDataSetsMatch(DataSet expected, DataSet actual, double tolerance)
    {
        var expectedKeys = expected.Cubes.Keys.OrderBy(k => k).ToList();
        var actualKeys   = actual.Cubes.Keys.OrderBy(k => k).ToList();

        Assert.Equal(expectedKeys, actualKeys);

        foreach (var key in expectedKeys)
        {
            var ec = expected.Cubes[key];
            var ac = actual.Cubes[key];

            Assert.Equal(ec.DataKind, ac.DataKind);

            if (ec.DataKind == RfCore.Data.DataKind.Complex)
            {
                var ev = ec.ComplexValues;
                var av = ac.ComplexValues;
                Assert.Equal(ev.Length, av.Length);
                for (int i = 0; i < ev.Length; i++)
                {
                    Assert.True(Math.Abs(ev[i].Real      - av[i].Real)      <= tolerance,
                        $"Cube '{key}' index {i}: Re mismatch {ev[i].Real} vs {av[i].Real}");
                    Assert.True(Math.Abs(ev[i].Imaginary - av[i].Imaginary) <= tolerance,
                        $"Cube '{key}' index {i}: Im mismatch {ev[i].Imaginary} vs {av[i].Imaginary}");
                }
            }
            else
            {
                var ev = ec.RealValues;
                var av = ac.RealValues;
                Assert.Equal(ev.Length, av.Length);
                for (int i = 0; i < ev.Length; i++)
                {
                    Assert.True(Math.Abs(ev[i] - av[i]) <= tolerance,
                        $"Cube '{key}' index {i}: mismatch {ev[i]} vs {av[i]}");
                }
            }
        }
    }
}
