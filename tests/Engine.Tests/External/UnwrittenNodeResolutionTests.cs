using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// A node a compiled model writes no equation for is not an independent unknown. The provider can
/// measure that much on its own — the node's row in the device's Jacobian is empty — but that says
/// nothing about which node it follows, and those are different questions.
///
/// <para><b>The failure these exist for.</b> Left as a free unknown, such a node is an almost-empty
/// matrix row that nothing pins: the bias ramp wanders to its iteration budget and the residual it
/// finally reports names a supply branch rather than the node responsible. Measured on a real
/// compiled model: 30,675 Newton iterations and 32 seconds of wall clock, against 4 iterations once
/// the node had a master — with nothing anywhere in the run naming the cause.</para>
///
/// <para><b>And why the master is measured rather than asked for.</b> Nobody can supply it: it is an
/// index into node numbering internal to a compiled model, which nothing shows the user. What CAN be
/// established is that a model's analytic derivatives should agree with a finite difference of its
/// own current, and they only do when a node it wrote as dependent is fed the voltage it was written
/// to carry. On that same real model the correct pairing scored 0.0308 against 0.0417 and 0.0556 for
/// the wrong ones, and it was the only one that also produced the right drain current.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class UnwrittenNodeResolutionTests : IDisposable
{
    private const string Provider = "unwritten-node-probe";

    public UnwrittenNodeResolutionTests() => ExternalDeviceRegistry.Clear();
    public void Dispose()                 => ExternalDeviceRegistry.Clear();

    // ── The fixture ───────────────────────────────────────────────────────────

    /// <summary>
    /// A device whose model is written as though <see cref="Inner"/> carries <see cref="Mid"/>'s
    /// voltage — the shape a real compact model has when an internal node collapses.
    ///
    /// <para>The giveaway is deliberate and is exactly the real one: the CURRENT is computed from
    /// Mid, while the derivative for that dependence is declared in Inner's column. Those two agree
    /// only when the two nodes carry the same voltage. Feed Inner independently and the model's own
    /// derivatives stop matching its own current — which is the signal being measured.</para>
    ///
    /// <para><paramref name="honest"/> false makes the declared derivative answer to nothing at all:
    /// no candidate restores agreement, every one scores alike, and there is no measurement to act
    /// on. That is the case that must come back as a refusal rather than as a coin toss.</para>
    /// </summary>
    private sealed class CollapsedInnerProvider(string name, bool honest) : IExternalDeviceProvider
    {
        public const string TypeName = "CollapsedInner";
        public const int    A = 0, B = 1, Mid = 2, Inner = 3, NodeCount = 4;

        private const double Gab = 0.01;   // an ordinary conductance between the two pins
        private const double Gm  = 0.004;  // the dependence written against Mid, declared on Inner

        public string Name { get; } = name;

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() =>
        [
            new ExternalDeviceDescriptor(
                TypeId:            TypeName,
                DisplayName:       "Collapsed-inner device (synthetic)",
                ExternalPinCount:  2,
                InternalNodeCount: 2,
                Parameters:        [],
                Nodes:
                [
                    new ExternalNodeDescriptor(A,     External: true,  NodeQuantityKind.Electrical, "a"),
                    new ExternalNodeDescriptor(B,     External: true,  NodeQuantityKind.Electrical, "b"),
                    new ExternalNodeDescriptor(Mid,   External: false, NodeQuantityKind.Electrical, "mid"),
                    new ExternalNodeDescriptor(Inner, External: false, NodeQuantityKind.Electrical, "inner",
                                               Degenerate: true),
                ]),
        ];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => new Instance(Describe()[0], honest);

        private sealed class Instance(ExternalDeviceDescriptor descriptor, bool honest)
            : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor { get; } = descriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
            {
                var i = new double[NodeCount];
                var g = new double[NodeCount, NodeCount];

                // Mid is an ordinary node with an equation of its own, so the device is solvable.
                i[Mid] = 0.02 * v[Mid];
                g[Mid, Mid] = 0.02;

                double through = Gab * (v[A] - v[B]) + (honest ? Gm * v[Mid] : 0.0);
                i[A] =  through;
                i[B] = -through;

                g[A, A] =  Gab; g[A, B] = -Gab;
                g[B, A] = -Gab; g[B, B] =  Gab;

                // THE DEPENDENCE IS DECLARED AGAINST Inner, never against Mid. This is what makes
                // Mid the answer and every other node wrong: only Inner = Mid brings the model's
                // derivatives back into agreement with its own current.
                g[A, Inner] =  Gm;
                g[B, Inner] = -Gm;

                // Row Inner left empty: the model states nothing about it.
                return new ExternalDeviceEvaluation(i, new double[NodeCount], g,
                                                    new double[NodeCount, NodeCount]);
            }

            public void Dispose() { }
        }
    }

    private static string Netlist() =>
        "Vdc:V1  a  0  Vdc=1\n" +
        "R:R1    b  0  R=50\n" +
        $"ExtDevice:X1  a  b  Provider={Provider} Type={CollapsedInnerProvider.TypeName}\n";

    private static ElaboratedNetlist Elaborate()
    {
        var (lib, tb) = new CnlReader().Read(Netlist());
        return new Elaborator(lib).Elaborate(tb);
    }

    private static void Register(bool honest)
        => ExternalDeviceRegistry.Register(new CollapsedInnerProvider(Provider, honest));

    // ── What it works out ─────────────────────────────────────────────────────

    [Fact]
    public void TheNodeAnUnwrittenOneFollows_IsMeasuredFromTheModelsOwnDerivatives()
    {
        // The whole point: nothing was supplied, and the right answer came out of the model itself.
        Register(honest: true);

        var nl = Elaborate();
        var ec = Assert.Single(nl.Components, c => c.InstancePath == "X1");
        var ed = Assert.IsType<ExternalDeviceModel>(ec.Model);

        var inner = ed.Descriptor.Nodes.Single(n => n.Index == CollapsedInnerProvider.Inner);
        Assert.Equal(CollapsedInnerProvider.Mid, inner.SlavedTo);
    }

    [Fact]
    public void TheResolvedNode_IsGivenItsMastersUnknown_NotOneOfItsOwn()
    {
        // The consequence that matters to the solve. Slaving is not a label: the node has to end up
        // sharing its master's matrix column, or it is still the empty row that cannot converge.
        Register(honest: true);

        var nl = Elaborate();
        var ec = Assert.Single(nl.Components, c => c.InstancePath == "X1");

        // Ground-referenced port pairs: node k spans Nodes[2k].
        int mid   = ec.Nodes[2 * CollapsedInnerProvider.Mid];
        int inner = ec.Nodes[2 * CollapsedInnerProvider.Inner];
        Assert.Equal(mid, inner);
    }

    [Fact]
    public void WorkingItOut_IsAnnouncedWithWhatWasMeasured()
    {
        // circuitRF decided something the design did not state, so it says so — and says it once per
        // TYPE rather than once per instance, since the measurement is a property of the model.
        Register(honest: true);

        var nl = Elaborate();

        // A NOTE, not a warning: nothing here is wrong. circuitRF established something the design
        // could not state and is saying what it established.
        string w = Assert.Single(nl.Notes,
            x => x.Contains("writes no equation", StringComparison.Ordinal));
        Assert.Contains(CollapsedInnerProvider.TypeName, w, StringComparison.Ordinal);
        Assert.Contains($"node {CollapsedInnerProvider.Inner} follows node {CollapsedInnerProvider.Mid}",
                        w, StringComparison.Ordinal);
    }

    // ── When it cannot be worked out ──────────────────────────────────────────

    [Fact]
    public void AModelWhoseDerivativesFavourNoCandidate_IsRefused_NotGuessedAt()
    {
        // The safety rule, and the reason this is a measurement rather than a heuristic. A wrong
        // choice here does not fail — it converges to a wrong number. So a ranking that is flat is
        // not a weak answer, it is no answer, and the run stops.
        Register(honest: false);

        var ex = Assert.Throws<ExternalDeviceException>(Elaborate);

        Assert.Contains("could not work out", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CollapsedInnerProvider.TypeName, ex.Message, StringComparison.Ordinal);
        Assert.Contains(DeviceLibraryDiscovery.AliasMapFileName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatAlreadyNamedTheMaster_IsTakenAtItsWord()
    {
        // Measuring is what circuitRF does when nobody has said. A provider that CAN say is not
        // second-guessed — its answer is the one thing here better founded than a measurement.
        ExternalDeviceRegistry.Register(new PreResolvedProvider(Provider));

        var nl = Elaborate();
        var ec = Assert.Single(nl.Components, c => c.InstancePath == "X1");
        var ed = Assert.IsType<ExternalDeviceModel>(ec.Model);

        Assert.Equal(CollapsedInnerProvider.A,
                     ed.Descriptor.Nodes.Single(n => n.Index == CollapsedInnerProvider.Inner).SlavedTo);
        Assert.DoesNotContain(nl.Notes.Concat(nl.Warnings),
                              w => w.Contains("writes no equation", StringComparison.Ordinal));
    }

    /// <summary>The same device, with the provider naming the master itself — and naming a DIFFERENT
    /// one from the measurable answer, so taking its word is visible rather than inferred.</summary>
    private sealed class PreResolvedProvider(string name) : IExternalDeviceProvider
    {
        public string Name { get; } = name;

        public IReadOnlyList<ExternalDeviceDescriptor> Describe()
        {
            var d = new CollapsedInnerProvider("inner", honest: true).Describe()[0];
            return
            [
                d with
                {
                    Nodes = d.Nodes.Select(n => n.Index == CollapsedInnerProvider.Inner
                        ? n with { SlavedTo = CollapsedInnerProvider.A }
                        : n).ToList(),
                },
            ];
        }

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
        {
            var inner = new CollapsedInnerProvider("inner", honest: true);
            return new Wrapped(inner.Create(typeId, parameters), Describe()[0]);
        }

        private sealed class Wrapped(IExternalDeviceInstance inner, ExternalDeviceDescriptor d)
            : IExternalDeviceInstance
        {
            public ExternalDeviceDescriptor Descriptor { get; } = d;
            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v) => inner.Evaluate(v);
            public void Dispose() => inner.Dispose();
        }
    }
}
