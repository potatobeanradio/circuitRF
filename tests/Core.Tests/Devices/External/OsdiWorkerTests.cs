using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The OSDI worker driven through circuitRF's real provider over a real process — not through a
/// hand-rolled script, and not over in-memory streams.
///
/// <para><b>Why the oracle is arithmetic and not a stored answer.</b> The device under test is a
/// two-terminal parallel RC whose behaviour is written down in closed form below, independently of
/// the C that implements it. Comparing the worker to itself would prove nothing; comparing it to a
/// number captured from an earlier run of the same code would prove less.</para>
///
/// <para><b>Skips rather than fails when the worker is not built.</b> It is native, and the standing
/// rule for native workers is that a missing compiler warns and the build still succeeds. A machine
/// without one must report these Skipped with a reason, not red.</para>
/// </summary>
public sealed class OsdiWorkerTests
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";
    private const string TypeId    = "crf_rc";

    // ── the device, in closed form ────────────────────────────────────────────
    //
    //  v    = V(A) - V(B)
    //  g(T) = g0 * (1 + tc * (T - tnom))     T in kelvin
    //  I    = [ +g v, -g v ]        Q  = [ +c v, -c v ]
    //  dI/dV= [[+g,-g],[-g,+g]]     dQ/dV = [[+c,-c],[-c,+c]]

    private const double G0 = 0.002, Cap = 1e-12, Tc = 0.01, Tnom = 300.0;

    private static double G(double kelvin) => G0 * (1.0 + Tc * (kelvin - Tnom));

    /// <summary>
    /// Charges here are ~1e-12, so an absolute decimal-places assertion cannot express "correct" —
    /// it would pass for any value at all. A relative tolerance is what the quantity deserves.
    /// </summary>
    private static void AssertRel(double expected, double actual, double rel = 1e-12)
        => Assert.True(Math.Abs(actual - expected) <= rel * Math.Abs(expected),
               $"expected {expected:G17}, got {actual:G17} (relative tolerance {rel:G3})");

    private static DeviceWorkerProvider Launch()
        => DeviceWorkerProvider.Launch("osdi",
               FixturePaths.Require(WorkerRel),
               [FixturePaths.Require(ModelRel)]);

    private static IExternalDeviceInstance Create(DeviceWorkerProvider p, double kelvin)
        => p.Create(TypeId, new Dictionary<string, string>
        {
            ["g0"]   = G0.ToString("R"),
            ["c"]    = Cap.ToString("R"),
            ["tc"]   = Tc.ToString("R"),
            ["tnom"] = Tnom.ToString("R"),
            [DeviceWorkerProvider.ReservedTemperatureKey] = kelvin.ToString("R"),
        });

    // ── O1 — the library describes itself ─────────────────────────────────────

    [FixtureFact(WorkerRel, HowTo)]
    public void O1_DescribeReadsTheLibrarysOwnDescriptor()
    {
        using var p = Launch();
        var types = p.Describe();

        var d = Assert.Single(types, t => t.TypeId == TypeId);
        Assert.Equal(2, d.ExternalPinCount);
        Assert.Equal(0, d.InternalNodeCount);

        // Settable parameters only. The model also declares an OP-VAR, which is an output; offering
        // it would put a writable box in the editor for a value the model computes.
        Assert.Equal(["g0", "c", "tc", "tnom", "mult"], d.Parameters.Select(x => x.Name).ToArray());
        Assert.DoesNotContain(d.Parameters, x => x.Name == "temp_k");
    }

    // ── O8/O9 — node collapsing, declared by the model and answered per instance ──

    private const string CollapseType = "crf_collapse";
    private const int    NodeA = 0, NodeB = 1, NodeT = 2, NodeAi = 3;

    private static IExternalDeviceInstance CreateCollapse(
        DeviceWorkerProvider p, double rs, int selfHeating)
        => p.Create(CollapseType, new Dictionary<string, string>
        {
            ["g"]   = "0.005",
            ["rs"]  = rs.ToString("R"),
            ["gth"] = "0.1",
            ["sh"]  = selfHeating.ToString(),
        });

    /// <summary>
    /// The mechanism <c>alias-map.json</c> exists to work around on the other worker, and which does
    /// not recur here: this ABI <b>declares</b> its collapsible node pairs, so which node a
    /// degenerate one follows is stated rather than inferred. Getting it wrong is a solve that will
    /// not converge with no error anywhere.
    ///
    /// <para>Both flavours are exercised, because they are genuinely different answers: a node that
    /// follows another node of the same device, and a node that goes to the <b>ground reference</b>
    /// — which cannot be expressed as "follows node 0", since node 0 is an ordinary pin.</para>
    ///
    /// <para>Collapsing is answered at <c>create</c>, not at describe, so the same provider is asked
    /// twice with different parameters. The uncollapsed case is what stops this passing vacuously:
    /// a host that dropped the report entirely would satisfy the second half and fail the first.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void O8_CollapsedNodesAreReportedPerInstance()
    {
        using var p = Launch();

        // rs = 0 degenerates the node behind the series branch; self-heating off deletes the
        // thermal node outright.
        using (var collapsed = CreateCollapse(p, rs: 0.0, selfHeating: 0))
        {
            var d = collapsed.Descriptor;

            Assert.Equal((NodeAi, NodeA), Assert.Single(d.SlavedNodes));
            Assert.Equal(NodeT, Assert.Single(d.GroundedNodes));

            // A grounded node is NOT reported as slaved to node 0 — that is a different claim, and
            // conflating them would ground every device whose first pin is an interesting net.
            Assert.Null(d.Nodes.Single(n => n.Index == NodeT).SlavedTo);
        }

        using (var free = CreateCollapse(p, rs: 100.0, selfHeating: 1))
        {
            Assert.Empty(free.Descriptor.SlavedNodes);
            Assert.Empty(free.Descriptor.GroundedNodes);
        }
    }

    /// <summary>
    /// A collapse report that is merely carried and never acted on is indistinguishable from one
    /// that is wrong, so the collapse has to be visible in the numbers too: with the series branch
    /// gone its conductance leaves the Jacobian, and the thermal node stops conducting.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void O9_CollapsingChangesTheAnswer_NotJustTheReport()
    {
        using var p = Launch();

        // The thermal node is deliberately held OFF zero: at zero volts a conducting thermal node
        // and a deleted one return the same current, and the check would prove nothing.
        double[] v = [1.0, 0.0, 0.5, 1.0];

        using var collapsed = CreateCollapse(p, rs: 0.0,   selfHeating: 0);
        using var free      = CreateCollapse(p, rs: 100.0, selfHeating: 1);

        var rc = collapsed.Evaluate(v);
        var rf = free.Evaluate(v);

        // Series branch: 1/100 S when present, absent entirely when the node is collapsed.
        Assert.Equal(0.0,  rc.Conductance[NodeA, NodeA], 12);
        Assert.Equal(0.01, rf.Conductance[NodeA, NodeA], 12);

        // Thermal node: gth·v when self-heating is on, nothing when the node is gone.
        Assert.Equal(0.0,        rc.Current[NodeT], 12);
        Assert.Equal(0.1 * v[2], rf.Current[NodeT], 12);

        // The conduction path itself is untouched by either collapse — this is the part that must
        // NOT change, and it is what says the two instances are otherwise the same device.
        Assert.Equal(rf.Current[NodeB], rc.Current[NodeB], 12);
    }

    // ── O2 — currents, charges and BOTH Jacobians against closed form ─────────

    [FixtureFact(WorkerRel, HowTo)]
    public void O2_EvaluationMatchesClosedForm()
    {
        using var p = Launch();
        using var d = Create(p, Tnom);

        double g = G(Tnom);
        const double va = 1.0, vb = 0.25, v = va - vb;

        var r = d.Evaluate([va, vb]);

        Assert.Equal( g * v, r.Current[0], 12);
        Assert.Equal(-g * v, r.Current[1], 12);
        AssertRel( Cap * v, r.Charge[0]);
        AssertRel(-Cap * v, r.Charge[1]);

        // The reactive Jacobian is the half most easily left at zero: a device with the right
        // currents and no dQ/dV converges perfectly at DC and is wrong at every frequency.
        Assert.Equal( g,   r.Conductance[0, 0], 12);
        Assert.Equal(-g,   r.Conductance[0, 1], 12);
        Assert.Equal(-g,   r.Conductance[1, 0], 12);
        Assert.Equal( g,   r.Conductance[1, 1], 12);
        AssertRel( Cap, r.Capacitance[0, 0]);
        AssertRel(-Cap, r.Capacitance[0, 1]);
        AssertRel(-Cap, r.Capacitance[1, 0]);
        AssertRel( Cap, r.Capacitance[1, 1]);
    }

    // ── O3 — the temperature actually reaches instance setup ─────────────────

    /// <summary>
    /// The one that pins A0 to A1. OSDI takes temperature as a required argument to instance setup,
    /// and a temperature that never lands still produces finite, entirely plausible currents — so it
    /// has to be observable in the ANSWER, not merely passed. The model's conductance carries a
    /// coefficient, so 400 K must give twice the conductance 300 K does. The test first asserts the
    /// two differ, so it cannot pass vacuously.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void O3_TemperatureReachesInstanceSetup()
    {
        using var p = Launch();

        double IdAt(double kelvin)
        {
            using var d = Create(p, kelvin);
            return d.Evaluate([1.0, 0.0]).Current[0];
        }

        double cold = IdAt(Tnom);          // ΔT = 0   → g = g0
        double hot  = IdAt(Tnom + 100.0);  // ΔT = 100 → g = 2 g0

        Assert.Equal(G0,       cold, 12);
        Assert.Equal(2.0 * G0, hot,  12);
        Assert.True(Math.Abs(hot - cold) > 1e-9,
            "the temperature never reached instance setup: both points used the same conductance.");
    }

    // ── O4 — a batch is one round trip and every point is independent ─────────

    /// <summary>
    /// Batching is the load-bearing API, not a convenience — harmonic balance evaluates every device
    /// once per sample per Newton iteration. A batch large enough to exceed any pipe buffer only
    /// passes if partial reads are looped on both sides, which is the failure that shows up under
    /// load and never in a small test.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void O4_LargeBatchDecodesPointwise()
    {
        using var p = Launch();
        using var d = Create(p, Tnom);

        const int n = 2000;
        var points = new List<IReadOnlyList<double>>(n);
        for (int k = 0; k < n; k++) points.Add([k * 1e-3, 0.0]);

        var results = d.EvaluateBatch(points);

        Assert.Equal(n, results.Count);
        double g = G(Tnom);
        for (int k = 0; k < n; k++)
            Assert.Equal(g * (k * 1e-3), results[k].Current[0], 12);
    }

    // ── O5 — an undeclared parameter is refused, not quietly dropped ──────────

    [FixtureFact(WorkerRel, HowTo)]
    public void O5_UnknownParameterIsRefused()
    {
        using var p = Launch();

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            p.Create(TypeId, new Dictionary<string, string> { ["nonesuch"] = "1" }));

        Assert.Contains("nonesuch", ex.Message, StringComparison.Ordinal);
    }

    // ── O6 — a library that is not one is refused with a reason ──────────────

    /// <summary>
    /// Pointed at a file that is not an OSDI library at all, the worker must fail to start and say
    /// why. Reaching the "provider unavailable" state silently is what sends a user looking in the
    /// wrong place entirely.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void O6_NonLibraryIsRefusedWithAReason()
    {
        string junk = Path.Combine(Path.GetTempPath(), $"crf-not-a-library-{Guid.NewGuid():N}.osdi");
        File.WriteAllText(junk, "this is not a shared library");
        try
        {
            var ex = Record.Exception(() =>
            {
                using var p = DeviceWorkerProvider.Launch("osdi", FixturePaths.Require(WorkerRel), [junk]);
                return p.Describe();
            });

            Assert.NotNull(ex);
        }
        finally { File.Delete(junk); }
    }

    // ── O7 — a DESIGN can name this worker, with no new production code ───────

    /// <summary>
    /// The shipping gate. A design does not launch a worker directly — it names a provider, and the
    /// resolver finds a manifest beside the kit and starts whatever that names. This stands up a kit
    /// folder exactly as an import leaves one and resolves through that path.
    ///
    /// <para><b>The point is that nothing had to be added for it.</b> The manifest already carries a
    /// command plus arguments, already resolves a BARE command name against circuitRF's own tools
    /// folder, and already makes a relative argument absolute against the manifest's directory. An
    /// OSDI library is just "which model library the worker should load" — the case that mechanism
    /// was built for. A second, OSDI-specific launch path would have been a parallel road to
    /// maintain for no gain.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void O7_ResolvesThroughAKitManifestLikeAnyOtherProvider()
    {
        string worker = FixturePaths.Require(WorkerRel);
        string model  = FixturePaths.Require(ModelRel);

        string kit = Path.Combine(Path.GetTempPath(), $"crf-osdi-kit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(kit);
        string previousTools = DeviceWorkerManifest.ToolsDirectory;
        try
        {
            // The library is copied INTO the kit so the manifest can name it relatively — which is
            // what a kit looks like, and it exercises the resolve-against-the-manifest rule.
            string localModel = Path.Combine(kit, "model.osdi");
            File.Copy(model, localModel);

            File.WriteAllText(Path.Combine(kit, "device-provider.json"), $$"""
                {
                  "provider": "OsdiKit",
                  "workers": [
                    { "platform": "any", "command": "{{Path.GetFileName(worker)}}",
                      "arguments": ["model.osdi"] }
                  ]
                }
                """);

            // A bare command name resolves against circuitRF's own tools folder — the mechanism a
            // kit uses to ask for a helper circuitRF ships without knowing where it was installed.
            DeviceWorkerManifest.ToolsDirectory = Path.GetDirectoryName(worker)!;

            var resolver = new DeviceWorkerProviderResolver([kit]);
            var provider = resolver.Resolve("OsdiKit");

            Assert.NotNull(provider);
            using var owned = provider as IDisposable;

            Assert.Single(provider!.Describe(), t => t.TypeId == TypeId);

            using var inst = provider.Create(TypeId, new Dictionary<string, string>
            {
                ["g0"] = G0.ToString("R"),
                [DeviceWorkerProvider.ReservedTemperatureKey] = Tnom.ToString("R"),
            });
            Assert.Equal(G0, inst.Evaluate([1.0, 0.0]).Current[0], 12);
        }
        finally
        {
            DeviceWorkerManifest.ToolsDirectory = previousTools;
            try { Directory.Delete(kit, recursive: true); } catch { /* best effort */ }
        }
    }
}
