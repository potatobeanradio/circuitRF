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
/// The other degenerate node a provider can report: one collapsed onto the <b>ground reference</b>
/// rather than onto another node of the same device.
///
/// <para><b>Why it is not just <c>SlavedTo = 0</c>.</b> Node 0 is an ordinary device node — usually
/// a pin the user wired to something interesting — so reading "grounded" as "follows node 0" would
/// tie a device's own first terminal to whatever the model meant by ground. The two claims are kept
/// apart in the descriptor for that reason, and only one of them is expressible as a node index.</para>
///
/// <para><b>Where it comes from.</b> A compact model that declares a collapsible pair against the
/// ground node marks it collapsed when the physics it belonged to is switched off — a thermal
/// network with self-heating disabled being the ordinary case. It is answered per instance, at
/// create, because it depends on the parameters that instance was given.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class GroundCollapsedNodeTests : IDisposable
{
    private const string Provider = "grounded-node";

    public void Dispose() => ExternalDeviceRegistry.Clear();

    // ── the provider ──────────────────────────────────────────────────────────

    /// <summary>
    /// Two external pins and two internal nodes, of which node 3 is grounded. The node the test
    /// grounds is a parameter of the provider so the same fixture can produce the accepted shape and
    /// each refused one — a separate provider per case would leave the refusals testing a different
    /// device from the one that works.
    /// </summary>
    private sealed class GroundingProvider(string name, int groundedNode, int? alsoSlavedTo = null)
        : IExternalDeviceProvider
    {
        public const string TypeName = "Grounded";
        public const int    A = 0, B = 1, Free = 2, Grounded = 3, NodeCount = 4;

        public string Name { get; } = name;

        private readonly ExternalDeviceDescriptor _descriptor = new(
            TypeId:            TypeName,
            DisplayName:       "Ground-collapsed-node device (synthetic)",
            ExternalPinCount:  2,
            InternalNodeCount: 2,
            Parameters:        [],
            Nodes:
            [
                new ExternalNodeDescriptor(A,    External: true,  Label: "a",
                                           CollapsedToGround: groundedNode == A),
                new ExternalNodeDescriptor(B,    External: true,  Label: "b"),
                new ExternalNodeDescriptor(Free, External: false, Label: "free"),
                new ExternalNodeDescriptor(Grounded, External: false, Label: "grounded",
                                           SlavedTo:          alsoSlavedTo,
                                           CollapsedToGround: groundedNode == Grounded),
            ]);

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [_descriptor];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> p)
            => new Instance(_descriptor);

        private sealed class Instance(ExternalDeviceDescriptor descriptor) : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor { get; } = descriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
            {
                // A resistive path between the two pins, plus a tie holding the one genuine internal
                // unknown. The grounded node contributes nothing — that is what being collapsed
                // means, and it is why an unknown minted for it could never be constrained.
                var i = new double[NodeCount];
                var g = new double[NodeCount, NodeCount];

                const double gd = 0.01;
                i[A] = gd * (v[A] - v[B]);
                i[B] = gd * (v[B] - v[A]);
                g[A, A] =  gd; g[A, B] = -gd;
                g[B, B] =  gd; g[B, A] = -gd;

                i[Free]       = 0.001 * v[Free];
                g[Free, Free] = 0.001;

                return new ExternalDeviceEvaluation(
                    i, new double[NodeCount], g, new double[NodeCount, NodeCount]);
            }

            public void Dispose() { }
        }
    }

    private static ElaboratedNetlist Elaborate(int groundedNode, int? alsoSlavedTo = null)
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new GroundingProvider(Provider, groundedNode, alsoSlavedTo));

        var (lib, tb) = new CnlReader().Read(
            "Term:T1  a  0  Num=1 Z=50 Ohm\n" +
            "Term:T2  b  0  Num=2 Z=50 Ohm\n" +
            $"ExtDevice:X1  a  b  Provider={Provider} Type={GroundingProvider.TypeName}\n");
        return new Elaborator(lib).Elaborate(tb);
    }

    // ── the accepted shape ────────────────────────────────────────────────────

    [Fact]
    public void AGroundedInternalNodeIsGivenNodeZero_NotAnUnknownOfItsOwn()
    {
        var nl = Elaborate(GroundingProvider.Grounded);
        var ec = nl.Components.Single(c => c.Model is ExternalDeviceModel);

        // Ground-referenced pairs: node k occupies Nodes[2k].
        Assert.Equal(0, ec.Nodes[2 * GroundingProvider.Grounded]);

        // The genuine internal unknown is untouched — this must not prune real nodes.
        Assert.NotEqual(0, ec.Nodes[2 * GroundingProvider.Free]);
    }

    [Fact]
    public void NoNodeIsMintedForAGroundedIndex()
    {
        // The direct regression, and the reason it needs its own assertion: the device's own Nodes
        // array is correct either way, so an orphan unknown is invisible from the device and shows
        // up only as a name in the netlist's node map that nothing then references.
        var nl = Elaborate(GroundingProvider.Grounded);

        Assert.DoesNotContain(nl.Nodes.AllNames,
            n => n.EndsWith($"_n{GroundingProvider.Grounded}", StringComparison.Ordinal));
        Assert.Contains(nl.Nodes.AllNames,
            n => n.EndsWith($"_n{GroundingProvider.Free}", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSParameterAssemblyIsNotSingular()
    {
        // What the user actually sees. DC cannot show this — gmin holds an orphan at zero and
        // nothing reads it — so the assertion has to be made against the S-parameter path.
        var nl = Elaborate(GroundingProvider.Grounded);
        SParameterEngine.Run(nl, [1e9, 2e9]);

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("singular", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("zero row", StringComparison.OrdinalIgnoreCase));
    }

    // ── the two refusals ──────────────────────────────────────────────────────

    /// <summary>
    /// An external pin cannot be grounded from inside the device, and the refusal is the point.
    /// The user wired a net to that pin, and circuitRF's two available readings are both wrong and
    /// both silent: hand the pin node 0 and the user's net is left floating rather than shorted;
    /// ignore the report and the device solves a node the model says is not there. Neither is
    /// visible on screen, so the provider is told to stop offering the pin instead.
    /// </summary>
    [Fact]
    public void AnExternalPinReportedAsGrounded_IsRefusedByName()
    {
        var ex = Assert.Throws<ExternalDeviceException>(() => Elaborate(GroundingProvider.A));

        Assert.Contains("X1", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"pin {GroundingProvider.A}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANodeReportedBothGroundedAndSlaved_IsRefused()
    {
        // The two claims name different masters, so one of them is wrong and nothing here can tell
        // which. Picking either would be a guess with a converging, wrong answer behind it.
        var ex = Assert.Throws<ExternalDeviceException>(
            () => Elaborate(GroundingProvider.Grounded, alsoSlavedTo: GroundingProvider.Free));

        Assert.Contains("cannot be both", ex.Message, StringComparison.Ordinal);
    }
}
