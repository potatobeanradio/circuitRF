using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// A node a model does not drive is reported as SLAVED to the node whose voltage it follows, and
/// takes that node's index instead of an unknown of its own.
///
/// <para><b>The failure this exists for.</b> Minting a node for a slaved index and then overwriting
/// it with its master's leaves an unknown in the system that nothing references — an all-zero row AND
/// column, which is the definition of a singular matrix. DC hides it completely: gmin holds the
/// orphan at zero and no equation reads it, so every voltage and current is right. It surfaces only
/// in the S-parameter assembly, as a singularity report naming nodes like
/// <c>__extdev_X1.T1.FET1_n6</c> that the user cannot find anywhere in their schematic, because they
/// exist in no schematic. On a production kit that was ten such rows — five devices, two slaved
/// nodes each — reported on every run.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class SlavedNodeAllocationTests : IDisposable
{
    private const string Provider = "slaved-probe";

    public SlavedNodeAllocationTests()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new SlavedProvider(Provider));
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    /// <summary>
    /// Two external pins and three internal nodes, of which node 4 follows node 3 — the shape a real
    /// compiled model reports once circuitRF's alias map tells the worker which of its nodes it never
    /// drives.
    /// </summary>
    private sealed class SlavedProvider(string name) : IExternalDeviceProvider
    {
        public const string TypeName = "Slaved";
        public const int    A = 0, B = 1, Free = 2, Master = 3, Follower = 4, NodeCount = 5;

        public string Name { get; } = name;

        public static readonly ExternalDeviceDescriptor TypeDescriptor = new(
            TypeId:            TypeName,
            DisplayName:       "Slaved-node device (synthetic)",
            ExternalPinCount:  2,
            InternalNodeCount: 3,
            Parameters:        [],
            Nodes:
            [
                new ExternalNodeDescriptor(A,        External: true,  NodeQuantityKind.Electrical, "a"),
                new ExternalNodeDescriptor(B,        External: true,  NodeQuantityKind.Electrical, "b"),
                new ExternalNodeDescriptor(Free,     External: false, NodeQuantityKind.Electrical, "free"),
                new ExternalNodeDescriptor(Master,   External: false, NodeQuantityKind.Electrical, "master"),
                new ExternalNodeDescriptor(Follower, External: false, NodeQuantityKind.Electrical, "follower",
                                           SlavedTo: Master),
            ]);

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [TypeDescriptor];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => new Instance();

        private sealed class Instance : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor => TypeDescriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
            {
                // A plain resistive path between the two external pins, plus small ties holding the
                // genuine internal unknowns. The slaved node contributes nothing at all — that is what
                // being slaved means, and it is why an unknown minted for it can never be constrained.
                var i = new double[NodeCount];
                var g = new double[NodeCount, NodeCount];

                double gd = 0.01;
                i[A] = gd * (v[A] - v[B]);
                i[B] = gd * (v[B] - v[A]);
                g[A, A] =  gd; g[A, B] = -gd;
                g[B, B] =  gd; g[B, A] = -gd;

                foreach (int n in new[] { Free, Master })
                {
                    i[n]    = 0.001 * v[n];
                    g[n, n] = 0.001;
                }

                return new ExternalDeviceEvaluation(i, new double[NodeCount], g, new double[NodeCount, NodeCount]);
            }

            public void Dispose() { }
        }
    }

    private static ElaboratedNetlist Elaborate()
    {
        var (lib, tb) = new CnlReader().Read(
            "Term:T1  a  0  Num=1 Z=50 Ohm\n" +
            "Term:T2  b  0  Num=2 Z=50 Ohm\n" +
            $"ExtDevice:X1  a  b  Provider={Provider} Type={SlavedProvider.TypeName}\n");
        return new Elaborator(lib).Elaborate(tb);
    }

    [Fact]
    public void ASlavedNodeIsGivenItsMastersIndex_NotAnUnknownOfItsOwn()
    {
        var nl = Elaborate();
        var ec = nl.Components.Single(c => c.Model is ExternalDeviceModel);

        // Ground-referenced pairs: node k occupies Nodes[2k].
        int master   = ec.Nodes[2 * SlavedProvider.Master];
        int follower = ec.Nodes[2 * SlavedProvider.Follower];

        Assert.Equal(master, follower);
    }

    [Fact]
    public void NoNodeIsMintedForASlavedIndex()
    {
        // The direct regression. The orphan is invisible from the device — its Nodes array is
        // correct either way — and shows up only as a name in the netlist's own node map that
        // nothing then references.
        var nl = Elaborate();

        Assert.DoesNotContain(nl.Nodes.AllNames,
            n => n.EndsWith($"_n{SlavedProvider.Follower}", StringComparison.Ordinal));

        // The genuine internal unknowns are still there — this must not prune real nodes.
        Assert.Contains(nl.Nodes.AllNames, n => n.EndsWith($"_n{SlavedProvider.Free}",   StringComparison.Ordinal));
        Assert.Contains(nl.Nodes.AllNames, n => n.EndsWith($"_n{SlavedProvider.Master}", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>An external pin collapsed onto an internal node keeps the USER'S net.</b> This is the shape
    /// a real compact model reports — a MOSFET collapses its drain terminal onto its internal drain,
    /// and its bulk terminal plus three internal bulk nodes onto one — and reading "node A follows
    /// node B" literally gives the TERMINAL the internal node's index. The net the user wired to that
    /// pin is then dropped, and the device solves perfectly while disconnected from the circuit
    /// around it. Nothing on screen says so.
    ///
    /// <para>Asserted with the group shape that makes it hard: several nodes collapsed onto one
    /// master, only one of them external, and the external one NOT first. Assigning as each is
    /// encountered would copy the internal index into the others before reaching the terminal.</para>
    /// </summary>
    [Fact]
    public void AnExternalPinCollapsedOntoAnInternalNode_KeepsTheUsersNet()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new CollapsingProvider(Provider));

        var (lib, tb) = new CnlReader().Read(
            "Term:T1  a  0  Num=1 Z=50 Ohm\n" +
            "Term:T2  b  0  Num=2 Z=50 Ohm\n" +
            $"ExtDevice:X1  a  b  Provider={Provider} Type={CollapsingProvider.TypeName}\n");
        var nl = new Elaborator(lib).Elaborate(tb);

        var ec = nl.Components.Single(c => c.Model is ExternalDeviceModel);
        int netA = nl.Nodes.IndexOf("a");

        // Ground-referenced pairs: node k occupies Nodes[2k]. Pin A, the internal node it was
        // collapsed onto, AND the other members of that group all sit on the user's net.
        Assert.Equal(netA, ec.Nodes[2 * CollapsingProvider.PinA]);
        Assert.Equal(netA, ec.Nodes[2 * CollapsingProvider.Master]);
        Assert.Equal(netA, ec.Nodes[2 * CollapsingProvider.AlsoSlaved]);

        // …and no internal unknown was minted for any of them.
        Assert.DoesNotContain(nl.Nodes.AllNames, n => n.Contains("_n2", StringComparison.Ordinal));
    }

    /// <summary>Two terminals collapsed together shorts two user nets, which circuitRF cannot carry.</summary>
    [Fact]
    public void TwoExternalPinsCollapsedTogether_AreRefused()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new CollapsingProvider(Provider, collapseBothPins: true));

        var (lib, tb) = new CnlReader().Read(
            "Term:T1  a  0  Num=1 Z=50 Ohm\n" +
            "Term:T2  b  0  Num=2 Z=50 Ohm\n" +
            $"ExtDevice:X1  a  b  Provider={Provider} Type={CollapsingProvider.TypeName}\n");

        var ex = Assert.Throws<ExternalDeviceException>(() => new Elaborator(lib).Elaborate(tb));
        Assert.Contains("shorts the nets", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four nodes, of which pin 0 and pin 1 are external and nodes 2 and 3 internal. Node 2 is the
    /// master; pin 0 and node 3 follow it — the external one declared LAST, which is the ordering
    /// that catches an assign-as-you-go implementation.
    /// </summary>
    private sealed class CollapsingProvider(string name, bool collapseBothPins = false) : IExternalDeviceProvider
    {
        public const string TypeName = "Collapsing";
        public const int    PinA = 0, PinB = 1, Master = 2, AlsoSlaved = 3, NodeCount = 4;

        public string Name { get; } = name;

        private readonly ExternalDeviceDescriptor _descriptor = new(
            TypeId: TypeName, DisplayName: "Collapsing device (synthetic)",
            ExternalPinCount: 2, InternalNodeCount: 2, Parameters: [],
            Nodes:
            [
                new ExternalNodeDescriptor(PinB,       External: true,  Label: "b"),
                new ExternalNodeDescriptor(Master,     External: false, Label: "master"),
                new ExternalNodeDescriptor(AlsoSlaved, External: false, Label: "also", SlavedTo: Master),
                new ExternalNodeDescriptor(PinA,       External: true,  Label: "a",    SlavedTo: Master),
            ]);

        private readonly ExternalDeviceDescriptor _shorting = new(
            TypeId: TypeName, DisplayName: "Collapsing device (synthetic)",
            ExternalPinCount: 2, InternalNodeCount: 2, Parameters: [],
            Nodes:
            [
                new ExternalNodeDescriptor(Master,     External: false, Label: "master"),
                new ExternalNodeDescriptor(AlsoSlaved, External: false, Label: "also"),
                new ExternalNodeDescriptor(PinA,       External: true,  Label: "a", SlavedTo: Master),
                new ExternalNodeDescriptor(PinB,       External: true,  Label: "b", SlavedTo: Master),
            ]);

        private ExternalDeviceDescriptor Chosen => collapseBothPins ? _shorting : _descriptor;

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [Chosen];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> p)
            => new Instance(Chosen);

        private sealed class Instance(ExternalDeviceDescriptor descriptor) : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor { get; } = descriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
            {
                var i = new double[NodeCount];
                var g = new double[NodeCount, NodeCount];
                const double gd = 0.01;
                i[Master] = gd * (v[Master] - v[PinB]);
                i[PinB]   = gd * (v[PinB] - v[Master]);
                g[Master, Master] =  gd; g[Master, PinB] = -gd;
                g[PinB, PinB]     =  gd; g[PinB, Master] = -gd;
                return new ExternalDeviceEvaluation(i, new double[NodeCount], g, new double[NodeCount, NodeCount]);
            }

            public void Dispose() { }
        }
    }

    // ── Collapse CHAINS ───────────────────────────────────────────────────────

    /// <summary>
    /// Five nodes in a CHAIN: the external pin 0 follows internal node 3, which itself follows
    /// internal node 2. One group of three, and the pin is the member carrying the user's net.
    ///
    /// <para>This is the shape a published GaN HEMT model reports — its drain terminal follows an
    /// internal node that follows another — and reading only the first link cannot resolve it: pin 0
    /// would take node 3's index, which is not the index the group settles on.</para>
    /// </summary>
    private sealed class ChainedProvider(string name, bool cyclic = false) : IExternalDeviceProvider
    {
        public const string TypeName = "Chained";
        public const int    PinA = 0, PinB = 1, Root = 2, Mid = 3, Free = 4, NodeCount = 5;

        public string Name { get; } = name;

        private readonly ExternalDeviceDescriptor _descriptor = new(
            TypeId: TypeName, DisplayName: "Chain-collapsing device (synthetic)",
            ExternalPinCount: 2, InternalNodeCount: 3, Parameters: [],
            Nodes:
            [
                // PinA -> Mid -> Root. Declared with the chain's HEAD first, so an implementation
                // that resolves in declaration order meets the unresolved link before the link that
                // would resolve it.
                new ExternalNodeDescriptor(PinA, External: true,  NodeQuantityKind.Electrical, "a",
                                           SlavedTo: cyclic ? Mid : Mid),
                new ExternalNodeDescriptor(PinB, External: true,  NodeQuantityKind.Electrical, "b"),
                new ExternalNodeDescriptor(Root, External: false, NodeQuantityKind.Electrical, "root",
                                           SlavedTo: cyclic ? Mid : null),
                new ExternalNodeDescriptor(Mid,  External: false, NodeQuantityKind.Electrical, "mid",
                                           SlavedTo: Root),
                new ExternalNodeDescriptor(Free, External: false, NodeQuantityKind.Electrical, "free"),
            ]);

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [_descriptor];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> p)
            => new Instance(_descriptor);

        private sealed class Instance(ExternalDeviceDescriptor descriptor) : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor { get; } = descriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
            {
                var i = new double[NodeCount];
                var g = new double[NodeCount, NodeCount];
                const double gd = 0.01;
                i[Root] = gd * (v[Root] - v[PinB]);
                i[PinB] = gd * (v[PinB] - v[Root]);
                g[Root, Root] =  gd; g[Root, PinB] = -gd;
                g[PinB, PinB] =  gd; g[PinB, Root] = -gd;
                i[Free]       = 0.001 * v[Free];
                g[Free, Free] = 0.001;
                return new ExternalDeviceEvaluation(i, new double[NodeCount], g, new double[NodeCount, NodeCount]);
            }

            public void Dispose() { }
        }
    }

    private static ElaboratedNetlist ElaborateChained(bool cyclic = false)
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new ChainedProvider(Provider, cyclic));

        var (lib, tb) = new CnlReader().Read(
            "Term:T1  a  0  Num=1 Z=50 Ohm\n" +
            "Term:T2  b  0  Num=2 Z=50 Ohm\n" +
            $"ExtDevice:X1  a  b  Provider={Provider} Type={ChainedProvider.TypeName}\n");
        return new Elaborator(lib).Elaborate(tb);
    }

    [Fact]
    public void ACollapseChain_ResolvesToOneGroup_RatherThanBeingRefused()
    {
        // The refusal this replaces stopped a published GaN HEMT model elaborating at all: "node 0
        // is slaved to node 13, which is itself slaved — chains are not supported."
        var nl = ElaborateChained();
        var ec = nl.Components.Single(c => c.Model is ExternalDeviceModel);

        int pinA = ec.Nodes[2 * ChainedProvider.PinA];
        int mid  = ec.Nodes[2 * ChainedProvider.Mid];
        int root = ec.Nodes[2 * ChainedProvider.Root];

        // All three are ONE unknown — that is what a chain of collapses means.
        Assert.Equal(pinA, mid);
        Assert.Equal(pinA, root);
    }

    [Fact]
    public void AChainContainingAPin_SettlesOnTheUsersNet_NotAnInternalIndex()
    {
        // The whole point of the external-pin-wins rule, which a chain must not defeat: give the
        // group an internal index and the device solves happily, disconnected from net "a", with
        // nothing on screen saying so.
        var nl = ElaborateChained();
        var ec = nl.Components.Single(c => c.Model is ExternalDeviceModel);

        int expected = nl.Nodes.GetOrAssign("a");
        Assert.Equal(expected, ec.Nodes[2 * ChainedProvider.PinA]);
        Assert.Equal(expected, ec.Nodes[2 * ChainedProvider.Mid]);
        Assert.Equal(expected, ec.Nodes[2 * ChainedProvider.Root]);

        // And the node that follows nothing still gets an unknown of its own.
        Assert.NotEqual(expected, ec.Nodes[2 * ChainedProvider.Free]);
    }

    [Fact]
    public void AChainedDeviceIsNotSingular()
    {
        var nl = ElaborateChained();
        SParameterEngine.Run(nl, [1e9, 2e9]);

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("singular", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("zero row", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACycleOfCollapses_IsStillRefused()
    {
        // Allowing chains must not quietly allow a cycle: with every member following another
        // member there is no node for the group to settle on, and following the links never
        // terminates.
        var ex = Assert.Throws<ExternalDeviceException>(() => ElaborateChained(cyclic: true));
        Assert.Contains("cycle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSParameterAssemblyIsNotSingular()
    {
        // What the user actually sees. DC cannot show this — gmin holds an orphan at zero and nothing
        // reads it — so the assertion has to be made against the S-parameter path.
        var nl = Elaborate();
        SParameterEngine.Run(nl, [1e9, 2e9]);

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("singular", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("zero row", StringComparison.OrdinalIgnoreCase));
    }
}
