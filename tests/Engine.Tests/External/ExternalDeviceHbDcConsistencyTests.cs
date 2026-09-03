using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// Harmonic balance and the DC engine must reach the SAME operating point on an external compiled
/// device when the drive is off.
///
/// <para><b>Why this is the check worth having.</b> Every static check on an external device compares
/// the model's outputs with each other — a Jacobian against a finite difference of its own currents,
/// a capacitance against a finite difference of its own charges. All of them pass on a device whose
/// CURRENT and CHARGE have been swapped, or whose reactive Jacobian has been folded into the
/// resistive one, because both halves are then self-consistently wrong. The two engines are not: the
/// DC solve reads only <c>i</c> and <c>dg</c>, while HB carries <c>q</c> and <c>dc</c> through its
/// own frequency-domain assembly. With no drive there is nothing for the reactive half to do, so
/// both must land on the same numbers — and a mix-up anywhere between the worker's byte offsets and
/// the HB Jacobian moves one of them.</para>
///
/// <para>The fixture is <c>tools/fake-osdi-model</c>'s <c>crf_fet</c> — three terminals, a smooth
/// pinch-off and Cgs/Cgd charge, with its closed form written in that library's own comment. No kit
/// and no proprietary artefact; it skips with a reason when the native worker is not built.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class ExternalDeviceHbDcConsistencyTests : IDisposable
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";
    private const string Provider  = "OsdiHbDc";

    public ExternalDeviceHbDcConsistencyTests()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(DeviceWorkerProvider.Launch(
            Provider, Locate(WorkerRel), [Locate(ModelRel)]));
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    private static string Locate(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(
                dir, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"fixture '{relative}' not found");
    }

    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>
    /// A common-source stage with a resistive drain load, so the DEVICE sets the operating point.
    /// An ideal source on every terminal would fix the answer before either engine ran, and the
    /// comparison would prove nothing.
    /// </summary>
    private static string Netlist(double driveV) => $"""
        V_1Tone:Vdrive  n_gate 0    Freq=2e9  V={N(driveV)}  Phase=0  Vdc=-1.5
        Vdc:VDD         n_vdd  0    Vdc=10
        R:RD            n_vdd  n_drain  R=100

        ExtDevice:X1  n_gate n_drain 0  Provider={Provider} Type=crf_fet \
            beta=0.06 vth=-2.5 lambda=0.02 alpha=1.5 delta=0.2 \
            cgs=2e-12 cgd=2e-13 ggs=1e-6

        analysis HB1 type=hb Tone=2e9 MaxHarm=3 Tol=1e-10
        """;

    private static ElaboratedNetlist Elaborate(double driveV, out TestBench bench)
    {
        var (lib, tb) = new CnlReader().Read(Netlist(driveV));
        bench = tb;
        return new Elaborator(lib).Elaborate(tb);
    }

    private static DataSet RunHb(double driveV)
    {
        var nl = Elaborate(driveV, out var tb);
        var p  = HbEngine.Resolve((HarmonicBalanceAnalysis)tb.Analyses[0],
                                  nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var r  = new HbEngine(nl, tb).Run(p);
        Assert.True(r.Converged, $"harmonic balance did not converge at drive {driveV} V");
        return r.DataSet;
    }

    private static Complex V(DataCube cube, string node, int k)
    {
        int i = Array.FindIndex(cube.Axes[0].Labels!, n => n.Equals(node, StringComparison.Ordinal));
        Assert.True(i >= 0, $"node '{node}' missing from the V cube's node axis");
        return (Complex)cube[i, k];
    }

    // ── H1 — the two engines agree ────────────────────────────────────────────

    [FixtureFact(WorkerRel, HowTo)]
    public void H1_AtZeroDrive_HbLandsOnTheDcEnginesOwnOperatingPoint()
    {
        var dcNl = Elaborate(0.0, out _);
        var dc   = NonlinearDcEngine.Run(dcNl);
        Assert.True(dc.Converged, "the DC operating point must be reached before HB can be compared to it");

        var v = RunHb(0.0)["V"];

        foreach (string node in new[] { "n_gate", "n_drain", "n_vdd" })
        {
            double expected = dc.NodeVoltages[dcNl.Nodes.IndexOf(node) - 1];
            Complex got     = V(v, node, 0);

            // THE TOLERANCE IS THE TWO SOLVERS' OWN STOPPING RULES, not a fudge. Each stops on its
            // own residual norm — a CURRENT — and on this node a residual of e amperes is e x 100 V.
            // The two land 3.7e-5 V apart here, which is that and nothing else. It is four orders of
            // magnitude clear of what this exists to catch: swapping i for q, or folding the
            // reactive Jacobian into the resistive one, moves an operating point by volts.
            Assert.True(Math.Abs(got.Real - expected) <= 1e-4 * Math.Max(Math.Abs(expected), 1.0),
                $"'{node}': DC engine {expected:G9} V, HB DC harmonic {got.Real:G9} V");
            Assert.True(Math.Abs(got.Imaginary) < 1e-9, $"'{node}': the DC harmonic is not real");
        }

        // NOT VACUOUS: the device has to be setting the operating point, or this compares two
        // circuits in which nothing happened. A device that elaborated but ended up disconnected
        // leaves the drain sitting at the supply and every line above still passes.
        double vd = dc.NodeVoltages[dcNl.Nodes.IndexOf("n_drain") - 1];
        Assert.True(vd > 0.5 && vd < 9.5,
            $"the device is not setting the operating point: Vd = {vd:G6} V against a 10 V supply");
    }

    [FixtureFact(WorkerRel, HowTo)]
    public void H2_AtZeroDrive_NothingAppearsAboveDc()
    {
        var v = RunHb(0.0)["V"];

        foreach (string node in new[] { "n_gate", "n_drain" })
            for (int k = 1; k <= 3; k++)
                Assert.True(V(v, node, k).Magnitude < 1e-9,
                    $"'{node}' harmonic {k} is {V(v, node, k).Magnitude:G6} V with no drive");
    }

    // ── H3 — and the drive really does reach the device ───────────────────────

    [FixtureFact(WorkerRel, HowTo)]
    public void H3_WithDrive_TheDeviceGeneratesHarmonicsAndShiftsItsOwnDcPoint()
    {
        double quiet = V(RunHb(0.0)["V"], "n_drain", 0).Real;

        var driven = RunHb(1.0)["V"];
        double loud = V(driven, "n_drain", 0).Real;

        // A driven nonlinear device rectifies: its own DC point moves. If it did not, H1 and H2 would
        // be measurements of a circuit the drive never reached.
        Assert.True(Math.Abs(loud - quiet) > 1e-3,
            $"the drive moved the DC point by {Math.Abs(loud - quiet):G3} V — it is not reaching the device");

        // And it generates harmonics, which is the half H2 asserts the absence of.
        Assert.True(V(driven, "n_drain", 2).Magnitude > 1e-6,
            "no second harmonic at the drain — the device is behaving linearly");
    }
}
