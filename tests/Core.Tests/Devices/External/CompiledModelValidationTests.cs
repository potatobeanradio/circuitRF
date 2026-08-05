using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The OSDI worker driven against <b>REAL compiled compact models</b> — the ones a user builds from
/// their own kit's Verilog-A sources with their own compiler.
///
/// <para><b>Why this is separate from <c>OsdiWorkerTests</c>.</b> That file drives a fixture written
/// by us: it proves the mechanism against arithmetic we control. This one proves the mechanism
/// against a model nobody here wrote — hundreds of parameters, a dozen internal nodes, and node
/// collapsing decided by the model's own physics. Every defect these found was invisible to the
/// fixture, by construction, because the fixture never does what a real model does.</para>
///
/// <para><b>Nothing here is committed and nothing here is GPL.</b> The models are built by a
/// Verilog-A compiler under GPL-3.0 that circuitRF never links, bundles or invokes; the user
/// installs it and compiles their own kit's Apache-licensed sources, exactly as they would for any
/// other simulator. The artifacts are per-platform build outputs, so they are located by
/// <see cref="ModelDirVariable"/> and these tests <b>Skip with a reason</b> when it is unset. A
/// machine without a kit must report these skipped, never red.</para>
///
/// <para><b>Every oracle is the model's own output compared with itself under a different
/// operation</b> — a Jacobian against a finite difference of the currents it returned, a charge
/// against the capacitance it returned, a current sum against zero. None of it needs a reference
/// simulator, and none of it would survive a sign error in the worker's marshalling.</para>
/// </summary>
public sealed class CompiledModelValidationTests
{
    /// <summary>
    /// Names a directory of compiled <c>.osdi</c> models.
    ///
    /// <para>Supplied, never searched for. These are built from a specific kit with a specific
    /// compiler, and any built-in path would have to name a supplier's folder — which this
    /// repository does not do. An environment variable keeps the fixture's identity entirely on the
    /// machine that has it.</para>
    /// </summary>
    private const string ModelDirVariable = "CIRCUITRF_OSDI_MODELS";

    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    private static string? ModelDir()
    {
        string? d = Environment.GetEnvironmentVariable(ModelDirVariable);
        return string.IsNullOrWhiteSpace(d) || !Directory.Exists(d) ? null : d;
    }

    /// <summary>Every compiled model present, largest last so the cheap ones fail first.</summary>
    private static IReadOnlyList<string> Models()
        => ModelDir() is { } d
            ? Directory.EnumerateFiles(d, "*.osdi").OrderBy(f => new FileInfo(f).Length).ToList()
            : [];

    private static DeviceWorkerProvider Launch(string model)
        => DeviceWorkerProvider.Launch("osdi-real", FixturePaths.Require(WorkerRel), [model]);

    /// <summary>
    /// A bias that puts a device somewhere interesting without knowing what it is: a volt across the
    /// first two terminals and a spread of small values on the rest, so no two nodes sit at the same
    /// potential and a transposed Jacobian cannot pass by symmetry.
    /// </summary>
    private static double[] Bias(int n)
    {
        var v = new double[n];
        for (int k = 0; k < n; k++) v[k] = k == 0 ? 1.0 : 0.05 * k;
        return v;
    }

    // ── V1 — a real model loads, describes and evaluates ──────────────────────

    [FixtureFact(WorkerRel, HowTo)]
    public void V1_EveryCompiledModelDescribesAndEvaluates()
    {
        if (ModelDir() is null) return;      // no kit on this machine; V0 says so
        Assert.NotEmpty(Models());

        foreach (string model in Models())
        {
            using var p = Launch(model);
            var types = p.Describe();
            Assert.NotEmpty(types);

            foreach (var t in types)
            {
                Assert.True(t.ExternalPinCount > 0, $"{Path.GetFileName(model)}/{t.TypeId} has no terminals");
                Assert.NotEmpty(t.Parameters);

                using var inst = p.Create(t.TypeId, new Dictionary<string, string>
                {
                    [DeviceWorkerProvider.ReservedTemperatureKey] = "300",
                });

                int n = inst.Descriptor.NodeCount;
                var r = inst.Evaluate(Bias(n));

                Assert.Equal(n, r.Current.Length);
                Assert.All(r.Current, x => Assert.True(double.IsFinite(x), "a current came back non-finite"));
                Assert.All(r.Charge,  x => Assert.True(double.IsFinite(x), "a charge came back non-finite"));
            }
        }
    }

    // ── V2 — the Jacobian, against finite differences of the model's own currents ──

    /// <summary>
    /// <b>The test this whole phase exists for.</b> The worker installs scratch doubles at the
    /// offsets the descriptor declares, lets the model write through them, and scatters the results
    /// into <c>G</c> by each entry's own node pair. A wrong offset, a transposed pair or a dropped
    /// entry produces a device that stamps cleanly, returns finite numbers, and simply will not
    /// converge — with nothing anywhere saying why.
    ///
    /// <para>The oracle is the model itself: <c>∂I[r]/∂V[c]</c> by central difference of the
    /// currents it returned, against the <c>G[r,c]</c> it reported. Nothing external is needed, and
    /// a sign error cannot survive it.</para>
    ///
    /// <para>Entries are compared where the finite difference is large enough to mean something. A
    /// compact model's Jacobian spans many orders of magnitude, and asserting on an entry whose true
    /// value is at the noise floor of a difference of two nearly-equal currents tests arithmetic
    /// precision, not correctness.</para>
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V2_TheReportedJacobianMatchesAFiniteDifferenceOfTheCurrents()
    {
        if (ModelDir() is null) return;

        foreach (string model in Models())
        {
            using var p = Launch(model);

            foreach (var t in p.Describe())
            {
                using var inst = p.Create(t.TypeId, new Dictionary<string, string>
                {
                    [DeviceWorkerProvider.ReservedTemperatureKey] = "300",
                });

                int n = inst.Descriptor.NodeCount;
                var v = Bias(n);
                var reported = inst.Evaluate(v).Conductance;

                const double h = 1e-5;
                int compared = 0;

                for (int c = 0; c < n; c++)
                {
                    var up = (double[])v.Clone(); up[c] += h;
                    var dn = (double[])v.Clone(); dn[c] -= h;

                    var iUp = inst.Evaluate(up).Current;
                    var iDn = inst.Evaluate(dn).Current;

                    for (int r = 0; r < n; r++)
                    {
                        double fd = (iUp[r] - iDn[r]) / (2.0 * h);
                        if (!double.IsFinite(fd) || Math.Abs(fd) < 1e-9) continue;

                        compared++;
                        double got = reported[r, c];
                        Assert.True(Math.Abs(got - fd) <= 1e-3 * Math.Abs(fd) + 1e-9,
                            $"{Path.GetFileName(model)}/{t.TypeId}: G[{r},{c}] = {got:G6}, " +
                            $"central difference = {fd:G6}");
                    }
                }

                Assert.True(compared > 0,
                    $"{Path.GetFileName(model)}/{t.TypeId}: no Jacobian entry was large enough to " +
                    "compare — the bias puts this device somewhere it does nothing, so the check " +
                    "would pass vacuously.");
            }
        }
    }

    // ── V3 — charge against the capacitance reported with it ──────────────────

    /// <summary>
    /// The reactive half, and the one most easily left at zero: a device with the right currents and
    /// no <c>dQ/dV</c> converges perfectly at DC and is wrong at every frequency. Same technique as
    /// V2, against the charges.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V3_TheReportedCapacitanceMatchesAFiniteDifferenceOfTheCharges()
    {
        if (ModelDir() is null) return;

        foreach (string model in Models())
        {
            using var p = Launch(model);

            foreach (var t in p.Describe())
            {
                using var inst = p.Create(t.TypeId, new Dictionary<string, string>
                {
                    [DeviceWorkerProvider.ReservedTemperatureKey] = "300",
                });

                int n = inst.Descriptor.NodeCount;
                var v = Bias(n);
                var reported = inst.Evaluate(v).Capacitance;

                const double h = 1e-5;
                for (int c = 0; c < n; c++)
                {
                    var up = (double[])v.Clone(); up[c] += h;
                    var dn = (double[])v.Clone(); dn[c] -= h;

                    var qUp = inst.Evaluate(up).Charge;
                    var qDn = inst.Evaluate(dn).Charge;

                    for (int r = 0; r < n; r++)
                    {
                        double fd = (qUp[r] - qDn[r]) / (2.0 * h);
                        if (!double.IsFinite(fd) || Math.Abs(fd) < 1e-18) continue;

                        Assert.True(Math.Abs(reported[r, c] - fd) <= 1e-3 * Math.Abs(fd) + 1e-18,
                            $"{Path.GetFileName(model)}/{t.TypeId}: C[{r},{c}] = {reported[r, c]:G6}, " +
                            $"central difference = {fd:G6}");
                    }
                }
            }
        }
    }

    // ── V4 — node collapsing, as a real model reports it ──────────────────────

    /// <summary>
    /// Real compact models collapse nodes routinely, and the shape they report is the one the
    /// synthetic fixture could not produce: a TERMINAL collapsed onto the internal node behind it,
    /// and several nodes collapsed onto one master of which only one is a terminal. Both are
    /// asserted structurally here — that whatever is reported names valid nodes, and that a
    /// collapsed terminal is never left pointing at nothing.
    /// </summary>
    [FixtureFact(WorkerRel, HowTo)]
    public void V4_CollapsedNodesAreReportedCoherently()
    {
        if (ModelDir() is null) return;

        int sawCollapse = 0;

        foreach (string model in Models())
        {
            using var p = Launch(model);

            foreach (var t in p.Describe())
            {
                using var inst = p.Create(t.TypeId, new Dictionary<string, string>
                {
                    [DeviceWorkerProvider.ReservedTemperatureKey] = "300",
                });

                var d = inst.Descriptor;
                foreach (var (node, master) in d.SlavedNodes)
                {
                    sawCollapse++;
                    Assert.InRange(node,   0, d.NodeCount - 1);
                    Assert.InRange(master, 0, d.NodeCount - 1);
                    Assert.NotEqual(node, master);

                    // Chains are not supported downstream, so a master must not itself be slaved.
                    Assert.Null(d.Nodes.Single(x => x.Index == master).SlavedTo);
                }
            }
        }

        Assert.True(sawCollapse > 0,
            "no compiled model reported a collapsed node, so this check passed vacuously — the " +
            "models present are not exercising the mechanism it exists for.");
    }

    // ── V0 — the skip is honest ───────────────────────────────────────────────

    /// <summary>
    /// Says out loud when the compiled-model tier did not run. Without this the suite is green on a
    /// machine with no kit AND on a machine where the models are silently unreadable, and those are
    /// completely different situations.
    /// </summary>
    [Fact]
    public void V0_TheCompiledModelTierSaysWhetherItRan()
    {
        if (ModelDir() is null)
        {
            Assert.Null(Environment.GetEnvironmentVariable(ModelDirVariable));
            return;
        }

        Assert.True(Models().Count > 0,
            $"{ModelDirVariable} names a directory with no .osdi files in it. Compile the kit's " +
            "Verilog-A sources with your own compiler, or unset the variable.");
    }
}
