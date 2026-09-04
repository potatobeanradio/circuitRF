using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The operating-point read-back protocol, driven against the REAL worker and the real test model —
/// <c>describe</c>'s op-var list, the standalone <c>opvars</c> read, and the per-point block that
/// rides on an <c>eval</c>.
///
/// <para><b>Both halves exist because neither can do the other's job.</b> The standalone read
/// returns the storage as it stands, which is the bias the caller last evaluated: exactly right for
/// a DC operating point, and useless for harmonic balance, where a whole time grid crosses the pipe
/// in one call and only the final sample would survive. The per-point form captures inside the
/// worker's own loop. They are checked against the same closed form so a drift between them shows
/// up as a disagreement rather than as two consistent wrong answers.</para>
/// </summary>
public sealed class WorkerOpVarProtocolTests : IClassFixture<WorkerOpVarProtocolTests.OneWorker>
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    /// <summary>
    /// ONE worker process for the whole class, not one per test.
    ///
    /// <para>xUnit builds a fresh test-class instance per method, so a provider launched in the
    /// constructor is a separate PROCESS per test — nine of them here, for questions one worker
    /// answers. That is real added load on a full-solution run, and this repo already has a
    /// documented casualty of exactly that: <c>Ui.Tests</c>' own note on
    /// <c>VerilogAModelIntrospectionTests</c> records a worker-startup race going from clean to
    /// failing 2 runs in 3 when one more process-starting file was added. A class fixture keeps
    /// every assertion and adds one process instead of nine.</para>
    ///
    /// <para>Sharing state between tests is safe here because none of them mutates the worker: each
    /// creates and disposes its own instances, and the read commands are pure.</para>
    /// </summary>
    public sealed class OneWorker : IDisposable
    {
        public DeviceWorkerProvider? Provider { get; } =
            FixturePaths.Find(ModelRel) is null || FixturePaths.Find(WorkerRel) is null
                ? null
                : DeviceWorkerProvider.Launch("opvar-protocol",
                                              FixturePaths.Require(WorkerRel),
                                              [FixturePaths.Require(ModelRel)]);

        public void Dispose() => Provider?.Dispose();
    }

    private readonly OneWorker _worker;

    public WorkerOpVarProtocolTests(OneWorker worker) => _worker = worker;

    private DeviceWorkerProvider Provider => _worker.Provider!;

    // ── the model's own arithmetic, from the library's header comment ─────────

    private const double Beta = 0.06, Vth = -2.5, Lambda = 0.02, Alpha = 1.5, Delta = 0.2;

    private static (double Id, double Gm, double Gds, double Vov, double Region) ClosedForm(
        double vgs, double vds)
    {
        double u    = vgs - Vth;
        double root = Math.Sqrt(u * u + Delta * Delta);
        double vov  = 0.5 * (u + root);
        double dvov = 0.5 * (1.0 + u / root);
        double x    = Alpha * vds;
        double sc   = 1.0 / Math.Sqrt(1.0 + x * x);
        double sat  = x * sc;
        double dsat = Alpha * sc * sc * sc;
        double chan = 1.0 + Lambda * vds;

        return (Id:     Beta * vov * vov * chan * sat,
                Gm:     2.0 * Beta * vov * dvov * chan * sat,
                Gds:    Beta * vov * vov * (Lambda * sat + chan * dsat),
                Vov:    vov,
                Region: vov <= 0.5 * Delta ? 0.0 : sat < 0.9 ? 1.0 : 2.0);
    }

    private static void Close(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= 1e-15 + 1e-12 * Math.Abs(expected),
                       $"{what}: expected {expected:G17}, got {actual:G17}");

    /// <summary>The FET's node order is (G, D, S); the source is held at zero.</summary>
    private static double[] Bias(double vgs, double vds) => [vgs, vds, 0.0];

    private static void CheckAgainstClosedForm(IReadOnlyDictionary<string, double> got,
                                               double vgs, double vds)
    {
        var w = ClosedForm(vgs, vds);
        Close(w.Id,  got["id"],  "id");
        Close(w.Gm,  got["gm"],  "gm");
        Close(w.Gds, got["gds"], "gds");
        Close(w.Vov, got["vov"], "vov");
        Assert.Equal(w.Region, got["region"]);
    }

    // ── P1: describe ──────────────────────────────────────────────────────────

    /// <summary>
    /// Op-vars are declared in their own list, with the model's own units and description, and a
    /// quantity is never in both lists — an output offered as settable would be a writable box for
    /// a value the model computes.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void Describe_ListsOpVarsSeparatelyFromParameters()
    {
        var types = Provider.Describe();

        foreach (var t in types)
            Assert.Empty(t.Parameters.Select(p => p.Name)
                          .Intersect(t.OpVars.Select(o => o.Name), StringComparer.Ordinal));

        var fet = types.Single(t => t.TypeId == "crf_fet");
        var by  = fet.OpVars.ToDictionary(o => o.Name, StringComparer.Ordinal);

        Assert.Equal(["id", "gm", "gds", "vov", "region", "regime"], fet.OpVars.Select(o => o.Name));
        Assert.Equal(ExternalParamKind.Double, by["gm"].Kind);
        Assert.Equal("S",                      by["gm"].Units);
        Assert.Equal("transconductance",       by["gm"].Description);

        // All three types the ABI allows, so the one that CANNOT be read back is declared here.
        Assert.Equal(ExternalParamKind.Int,    by["region"].Kind);
        Assert.Equal(ExternalParamKind.String, by["regime"].Kind);

        // More than one type carries them, and a type that computes none says so with an empty list
        // rather than by omitting the key — so "declares none" never has to be told from "does not
        // speak this protocol".
        Assert.Equal(["temp_k"], types.Single(t => t.TypeId == "crf_rc").OpVars.Select(o => o.Name));
        Assert.Empty(types.Single(t => t.TypeId == "crf_therm").OpVars);
    }

    // ── P2: the standalone read ───────────────────────────────────────────────

    /// <summary>
    /// <b>The read tracks the bias the caller last evaluated.</b> Two evaluations at genuinely
    /// different biases, each followed by a read: a read that lagged by one point would sail through
    /// a single-bias test and disagree here, on an integer as well as in the last digits.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void ReadOperatingPoint_TracksTheLastEvaluatedBias()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());

        (double vgs, double vds)[] biases = [(-1.0, 8.0), (-2.4, 0.3)];
        Assert.NotEqual(ClosedForm(-1.0, 8.0).Region, ClosedForm(-2.4, 0.3).Region);

        foreach (var (vgs, vds) in biases)
        {
            inst.Evaluate(Bias(vgs, vds));
            var got = inst.ReadOperatingPoint();
            Assert.NotNull(got);
            CheckAgainstClosedForm(got!, vgs, vds);
        }
    }

    /// <summary>
    /// The string-valued op-var is declared and is never read back — a single-kind numeric cube has
    /// nowhere to put it. Declared-and-unreadable, which the descriptor still says, rather than
    /// silently absent.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void ReadOperatingPoint_OmitsAStringOpVar_ButTheDescriptorStillDeclaresIt()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());
        inst.Evaluate(Bias(-1.0, 8.0));

        var got = inst.ReadOperatingPoint()!;
        Assert.DoesNotContain("regime", got.Keys);
        Assert.Equal(["id", "gm", "gds", "vov", "region"], got.Keys);

        Assert.Contains(inst.Descriptor.OpVars, o => o.Name == "regime");
    }

    /// <summary>
    /// A read with no prior evaluation is DEFINED: it reports the instance as setup left it. Every
    /// op-var here is written only by the load, so they come back at their zero-initialised value —
    /// an honest "the model has computed nothing yet", not a refusal and not an invented bias.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void ReadOperatingPoint_WithNoPriorEval_IsDefined()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());

        var got = inst.ReadOperatingPoint();
        Assert.NotNull(got);
        Assert.Equal(5, got!.Count);
        Assert.All(got.Values, v => Assert.Equal(0.0, v));
    }

    /// <summary>
    /// A type that declares none is not asked at all — which is what keeps the two OTHER workers
    /// circuitRF ships out of this entirely. They host different ABIs and would answer an unknown
    /// command with an error, turning a working simulation into a refusal.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void ADeviceThatDeclaresNone_IsNeverAsked()
    {
        using var inst = Provider.Create("crf_therm", new Dictionary<string, string>());
        inst.Evaluate([1.0, 0.0, 300.0]);

        Assert.Null(inst.ReadOperatingPoint());
        Assert.Null(inst.EvaluateOperatingPoint([[1.0, 0.0, 300.0]]));
    }

    // ── P2: the per-point block ───────────────────────────────────────────────

    /// <summary>
    /// <b>Every point carries its own values.</b> This is the shape harmonic balance needs, and the
    /// failure it guards against is specific: a capture taken after the worker's loop rather than
    /// inside it gives four copies of the last point, which is plausible and wrong.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void EvaluateOperatingPoint_ReturnsOneRowPerPoint()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());

        (double vgs, double vds)[] pts = [(-1.0, 8.0), (-2.4, 0.3), (-0.5, 2.0), (-3.4, 5.0)];
        var op = inst.EvaluateOperatingPoint([.. pts.Select(p => Bias(p.vgs, p.vds))]);

        Assert.NotNull(op);
        Assert.Equal(["id", "gm", "gds", "vov", "region"], op!.Names);
        Assert.Equal(pts.Length, op.Values.Count);

        for (int k = 0; k < pts.Length; k++)
        {
            var row = op.Names.Zip(op.Values[k]).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);
            CheckAgainstClosedForm(row, pts[k].vgs, pts[k].vds);
        }

        // Not four copies of one point.
        Assert.Equal(pts.Length, op.Values.Select(r => r[0]).Distinct().Count());
    }

    /// <summary>
    /// The two paths agree. They read the same storage by two different routes, so a drift between
    /// them is a real disagreement rather than two consistent wrong answers.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void TheTwoReadPathsAgree()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());

        var op    = inst.EvaluateOperatingPoint([Bias(-1.5, 4.0)])!;
        var after = inst.ReadOperatingPoint()!;   // the batch left it at that same, single point

        for (int i = 0; i < op.Names.Count; i++)
            Assert.Equal(op.Values[0][i], after[op.Names[i]]);
    }

    /// <summary>
    /// An ordinary <c>EvaluateBatch</c> is untouched — asking for op-vars is opt-in on the wire, so
    /// a caller that does not want them gets the payload it always got.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void AnOrdinaryEvaluationIsUnchanged()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());

        var r = inst.EvaluateBatch([Bias(-1.0, 8.0), Bias(-2.4, 0.3)]);

        Assert.Equal(2, r.Count);
        Close(ClosedForm(-1.0, 8.0).Id, r[0].Current[1], "I[D] at the first point");
        Close(ClosedForm(-2.4, 0.3).Id, r[1].Current[1], "I[D] at the second point");
        Close(ClosedForm(-1.0, 8.0).Gm, r[0].Conductance[1, 0], "dI[D]/dV[G] at the first point");
    }

    /// <summary>
    /// Reading allocates nothing in the worker: it touches an instance the host already owns, so
    /// unlike <c>defaults</c> there is no probe to stand up and no slot to leak. The worker's table
    /// is small, and a leak would exhaust it on a long sweep.
    /// </summary>
    [FixtureFact(ModelRel, HowTo)]
    public void ReadingLeaksNoInstanceSlot()
    {
        using var inst = Provider.Create("crf_fet", new Dictionary<string, string>());
        for (int i = 0; i < 64; i++) inst.ReadOperatingPoint();

        // If a read had consumed a slot, standing up 64 more devices would start failing.
        var made = new List<IExternalDeviceInstance>();
        try
        {
            for (int i = 0; i < 64; i++)
                made.Add(Provider.Create("crf_rc", new Dictionary<string, string>()));
        }
        finally
        {
            foreach (var m in made) m.Dispose();
        }
        Assert.Equal(64, made.Count);
    }
}
