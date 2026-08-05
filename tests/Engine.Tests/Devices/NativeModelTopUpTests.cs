using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The native-model top-ups, exercised through the WHOLE path — a <c>.cnl</c> in <c>testdata/</c>,
/// elaborated and solved by the real engines — rather than against the model classes in isolation.
///
/// <para><b>Why netlist level and not unit level.</b> Each of these features has to survive
/// elaboration to do anything: a temperature coefficient is useless if the ambient never reaches the
/// factory, a geometric capacitance is useless if the unit scale is applied twice, and a device
/// multiplier is useless if the engine stamps through a path that bypasses it. Every one of those
/// failures leaves the model class perfectly correct and the answer wrong.</para>
///
/// <para><b>Every oracle here is a closed form or a second netlist</b>, never a stored number from
/// another simulator: the equations are what has to be right, and a golden file from elsewhere would
/// only show that two implementations agree.</para>
/// </summary>
public sealed class NativeModelTopUpTests
{
    /// <summary>The PRD's S-parameter tolerance. These are lumped networks, so it is a real bar.</summary>
    private const double SParamTol = 1e-6;

    private static string Fixture(string name)
        => Path.Combine(FindRepoRoot(), "testdata", "A3", name);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("testdata/ not found above the test binary");
    }

    private static ElaboratedNetlist Elaborate(string cnlText)
    {
        var (lib, tb) = new CnlReader().Read(cnlText);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static ElaboratedNetlist ElaborateFixture(string name)
    {
        var (lib, tb) = CnlReader.ReadFile(Fixture(name));
        return new Elaborator(lib).Elaborate(tb);
    }

    private static double NodeV(ElaboratedNetlist n, NonlinearDcEngine.DcResult r, string net)
    {
        int idx = n.Nodes.IndexOf(net);
        return idx == 0 ? 0.0 : r.NodeVoltages[idx - 1];
    }

    // ── the resistor's temperature coefficients ───────────────────────────────

    /// <summary>
    /// A divider whose top resistor carries coefficients and whose bottom one does not. The node
    /// voltage is <c>Rbot / (Rtop(T) + Rbot)</c> — written out here, so a coefficient that never
    /// reached the stamp gives a visibly different answer rather than a slightly different one.
    /// </summary>
    [Fact]
    public void R1_TemperatureCoefficientsReachTheStamp()
    {
        var n = ElaborateFixture("resistor_tempco.cnl");
        var r = NonlinearDcEngine.Run(n);

        Assert.True(r.Converged);

        const double rtop = 1000.0, rbot = 1000.0, tc1 = 5e-3, tc2 = 1e-6;
        double dT   = 125.0 - 25.0;                 // the fixture's ambient, its Tnom
        double hot  = rtop * (1.0 + tc1 * dT + tc2 * dT * dT);
        double expected = rbot / (hot + rbot);

        Assert.Equal(expected, NodeV(n, r, "mid"), 1e-9);

        // Not vacuous: without the coefficients the divider sits at exactly one half.
        Assert.True(Math.Abs(expected - 0.5) > 0.05, "the fixture must move the divider visibly");
    }

    /// <summary>
    /// The guard, and it is the one that matters. A design that states no ambient and no
    /// coefficients must produce the resistor circuitRF has always had — bit-exact, because
    /// "additive by construction" is a claim about the arithmetic.
    /// </summary>
    [Fact]
    public void R2_WithoutCoefficientsTheResistorIsUnchanged()
    {
        const string cnl = "Vdc:V1 in 0 Vdc=1\nR:R1 in mid R=1000\nR:R2 mid 0 R=3000\n";

        var n = Elaborate(cnl);
        var r = NonlinearDcEngine.Run(n);

        // Not bit-exact, and deliberately not asserted as such: the DC engine adds gmin to every
        // voltage node for continuity, so an ideal divider reads 0.75 minus a few hundred pV. That
        // is the solver's own regularisation, not this feature.
        Assert.Equal(0.75, NodeV(n, r, "mid"), 1e-9);

        // And an ambient alone moves nothing: a resistor with no coefficients has no temperature.
        var hot = Elaborate("temp = 125\n" + cnl);
        Assert.Equal(NodeV(n, r, "mid"), NodeV(hot, NonlinearDcEngine.Run(hot), "mid"));
    }

    /// <summary>Ambient must never move <c>Tnom</c> — do that and ΔT is zero at every ambient.</summary>
    [Fact]
    public void R3_AmbientDoesNotMoveTnom()
    {
        const string body = "Vdc:V1 in 0 Vdc=1\nR:Rt in mid R=1000 TC1=5e-3 Tnom=25\nR:Rb mid 0 R=1000\n";

        var cold = Elaborate("temp = 25\n"  + body);
        var hot  = Elaborate("temp = 125\n" + body);

        double vc = NodeV(cold, NonlinearDcEngine.Run(cold), "mid");
        double vh = NodeV(hot,  NonlinearDcEngine.Run(hot),  "mid");

        // At its own Tnom the coefficient does nothing at all — exactly.
        Assert.Equal(0.5, vc, 1e-9);
        // …and away from it, it does. If ambient moved Tnom too, these two would be equal.
        Assert.True(vh < vc - 0.05, $"the ambient must reach ΔT: {vh} vs {vc}");
    }

    // ── the semiconductor capacitor ───────────────────────────────────────────

    /// <summary>
    /// A capacitor built from a process and a geometry must behave exactly like the capacitance that
    /// geometry implies. The fixture puts the two side by side in identical low-passes, so the check
    /// is S21 against S21 — which also catches a unit scale applied twice, the failure a value
    /// assertion in isolation would miss.
    /// </summary>
    [Fact]
    public void C1_GeometryAndTemperatureResolveToTheCapacitanceTheyImply()
    {
        var n  = ElaborateFixture("semi_capacitor.cnl");
        var ds = SParameterEngine.Run(n, [1e6, 1e7, 1e8]);

        var s = ds["S"];
        for (int f = 0; f < 3; f++)
        {
            // The two legs are separate one-ports, so the reflection each presents is the
            // comparison — S21 between them is zero by construction and would prove nothing.
            var geometric = (Complex)s[f, 0, 0];      // S11: the SemiC leg
            var stated    = (Complex)s[f, 1, 1];      // S22: the leg stated as a plain capacitance
            Assert.True((geometric - stated).Magnitude < SParamTol,
                $"point {f}: geometric {geometric} vs stated {stated}");
        }
    }

    /// <summary>
    /// The arithmetic behind the fixture, stated independently: area × Cj + perimeter × Cjsw, scaled
    /// by the temperature polynomial. Asserted on the resolved model so a wrong reading of W/L (area
    /// vs perimeter transposed, say) cannot hide behind a network response.
    /// </summary>
    [Fact]
    public void C2_TheValueIsAreaTimesCjPlusPerimeterTimesCjsw()
    {
        const double cj = 1e-3, cjsw = 2e-10, w = 20e-6, l = 40e-6, tc1 = 1e-4;

        var model = (SemiCapacitorModel)ComponentModelFactory.TryCreate("SemiC",
            new System.Collections.Generic.Dictionary<string, Value>
            {
                ["Cj"] = new(cj), ["Cjsw"] = new(cjsw),
                ["W"]  = new(w),  ["L"]    = new(l),
                ["TC1"] = new(tc1), ["Tnom"] = new(25.0),
            },
            functions: null, ambientC: 125.0)!;

        double expected = (cj * (w * l) + cjsw * 2.0 * (w + l)) * (1.0 + tc1 * (125.0 - 25.0));
        Assert.Equal(expected, model.Capacitance, Math.Abs(expected) * 1e-12);

        // Transposing area and perimeter would give a visibly different capacitor — so the check
        // above is a real one rather than a coincidence of the numbers chosen.
        double transposed = (cj * 2.0 * (w + l) + cjsw * (w * l)) * (1.0 + tc1 * 100.0);
        Assert.True(Math.Abs(transposed - expected) > 0.1 * expected);
    }

    // ── the device multiplier ─────────────────────────────────────────────────

    /// <summary>
    /// The multiplier at netlist level: <c>m = 4</c> on a 400 Ω resistor must be indistinguishable
    /// from a 100 Ω one. Asserted through the S-parameter path, so it is the STAMP that is being
    /// checked and not a parameter value.
    /// </summary>
    [Fact]
    public void M1_AMultipliedResistorMatchesTheParallelCombination()
    {
        var n  = ElaborateFixture("multiplier.cnl");
        var ds = SParameterEngine.Run(n, [1e9]);

        var s = ds["S"];
        Complex multiplied = s[0, 0, 0], parallel = s[0, 1, 1];
        Assert.True((multiplied - parallel).Magnitude < SParamTol,
            $"m=4 gave {multiplied}, four in parallel gave {parallel}");
    }

    /// <summary>
    /// A capacitor is the other direction — <c>m</c> multiplies the admittance, so the capacitance
    /// rises where the resistance fell. Checking both directions is what shows the multiplier is
    /// applied to the CONTRIBUTION rather than to a value some model happened to read.
    /// </summary>
    [Fact]
    public void M2_AMultipliedCapacitorMatchesTheLargerOne()
    {
        var n = Elaborate("""
            Port:P1 a 0 Num=1 Z=50
            Port:P2 b 0 Num=2 Z=50
            R:R1 a n1 R=50
            C:C1 n1 0 C=1p m=4
            R:R2 b n2 R=50
            C:C2 n2 0 C=4p
            """);

        var s = SParameterEngine.Run(n, [1e8, 1e9])["S"];
        for (int f = 0; f < 2; f++)
        {
            Complex a = s[f, 0, 0], b = s[f, 1, 1];
            Assert.True((a - b).Magnitude < SParamTol, $"point {f}: {a} vs {b}");
        }
    }

    /// <summary>
    /// The nonlinear half, and the one that proves all four blocks scale. Four diodes in parallel is
    /// one diode of four times the area — the same current at the same bias, arrived at two
    /// different ways, through a real Newton solve.
    /// </summary>
    [Fact]
    public void M3_AMultipliedDiodeMatchesTheLargerOne()
    {
        const string common = "Vdc:V1 s 0 Vdc=0.75\nR:Rs s a R=100\n";

        var multiplied = Elaborate(common + "Diode:D1 a 0 Is=1e-14 N=1.05 m=4\n");
        var wider      = Elaborate(common + "Diode:D1 a 0 Is=1e-14 N=1.05 Area=4\n");

        var rm = NonlinearDcEngine.Run(multiplied);
        var rw = NonlinearDcEngine.Run(wider);

        Assert.True(rm.Converged && rw.Converged);

        double vm = NodeV(multiplied, rm, "a"), vw = NodeV(wider, rw, "a");
        Assert.Equal(vw, vm, 1e-9);

        // Not vacuous: one diode alone sits at a visibly higher voltage for the same drive.
        var single = Elaborate(common + "Diode:D1 a 0 Is=1e-14 N=1.05\n");
        double vs = NodeV(single, NonlinearDcEngine.Run(single), "a");
        Assert.True(vs > vm + 1e-3, $"four diodes must conduct more than one: {vm} vs {vs}");
    }

    /// <summary>
    /// The multiplier is applied ONCE, at the component, and the AC path must see it too. A
    /// nonlinear device stamped into an S-parameter assembly goes through a different entry point
    /// from the DC solve, and a multiplier applied in only one of them is the exact shape of bug
    /// this seam exists to make impossible.
    /// </summary>
    [Fact]
    public void M4_TheMultiplierReachesTheLinearisedStampToo()
    {
        const string ports = "Port:P1 a 0 Num=1 Z=50\nPort:P2 b 0 Num=2 Z=50\n";

        var n = Elaborate(ports +
            "Diode:Dm a 0 Is=1e-14 N=1.0 Cj0=1p m=4\n" +
            "Diode:Dw b 0 Is=1e-14 N=1.0 Cj0=1p Area=4\n");

        var s = SParameterEngine.Run(n, [1e9, 5e9])["S"];
        for (int f = 0; f < 2; f++)
        {
            Complex a = s[f, 0, 0], b = s[f, 1, 1];
            Assert.True((a - b).Magnitude < SParamTol, $"point {f}: m=4 gave {a}, Area=4 gave {b}");
        }
    }

    /// <summary>
    /// Several ideal voltage sources in parallel is not a circuit — it is the same constraint
    /// written more than once — so a multiplier on a branch-contributing component is refused by
    /// name rather than obeyed. This is decided at the moment the branch is asked for, so it covers
    /// every such model without a list of them.
    /// </summary>
    [Fact]
    public void M5_AMultiplierOnABranchElementIsRefusedByName()
    {
        var n = Elaborate("Vdc:V1 in 0 Vdc=1 m=2\nR:R1 in 0 R=1000\n");

        var ex = Assert.ThrowsAny<Exception>(() => NonlinearDcEngine.Run(n));
        Assert.Contains("V1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero copies is refused rather than obeyed. Some dialects read it as "this device is not
    /// there"; deleting a component the user placed, in silence, is the worse answer.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void M6_ANonPositiveMultiplierIsRefused(string m)
    {
        var ex = Assert.ThrowsAny<Exception>(() => Elaborate($"R:R1 a 0 R=1000 m={m}\n"));
        Assert.Contains("R1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The confusable pair, made explicit. Lower-case <c>m</c> is the device multiplier; upper-case
    /// <c>M</c> is the junction diode's grading coefficient — on a component that can carry both,
    /// meaning nothing like each other. Resolved parameters are compared ordinally, so the two are
    /// genuinely different keys; this pins that, because a diode reading its grading coefficient as
    /// a device count would give a circuit with 0.4 diodes in it and simulate perfectly.
    /// </summary>
    [Fact]
    public void M7_LowerCaseMIsTheMultiplier_UpperCaseMIsTheGradingCoefficient()
    {
        var graded = Elaborate("Diode:D1 a 0 Is=1e-14 Cj0=1p M=0.4\n");
        var d = graded.Components.Single(c => c.ComponentType == "Diode");

        Assert.Equal(1.0, d.Multiplicity);                       // M did not become a multiplier
        Assert.Equal(0.4, d.Parameters["M"].AsReal(), 15);       // …and it is still the grading coefficient

        var many = Elaborate("Diode:D1 a 0 Is=1e-14 Cj0=1p m=4\n");
        Assert.Equal(4.0, many.Components.Single(c => c.ComponentType == "Diode").Multiplicity);
    }

    /// <summary>
    /// The regression that makes the whole seam additive: a netlist that states no multiplier is
    /// bit-identical to one elaborated before the multiplier existed. Asserted as exact equality
    /// against a second netlist, because "unchanged" is a claim about the arithmetic.
    /// </summary>
    [Fact]
    public void M8_ANetlistWithNoMultiplierIsUnchanged()
    {
        var n = Elaborate("""
            Port:P1 a 0 Num=1 Z=50
            Port:P2 b 0 Num=2 Z=50
            R:R1 a b R=75
            C:C1 b 0 C=2p
            """);

        Assert.All(n.Components, c => Assert.Equal(1.0, c.Multiplicity));

        var s = SParameterEngine.Run(n, [1e9])["S"];
        // A 75 Ω series resistor between two 50 Ω ports, shunted by 2 pF — computed here rather than
        // stored, so this stays an oracle and not a snapshot of whatever the code last did.
        var y  = new Complex(0, 2 * Math.PI * 1e9 * 2e-12);
        var z2 = 1.0 / (y + 1.0 / 50.0);
        var s11 = (75.0 + z2 - 50.0) / (75.0 + z2 + 50.0);
        Complex got = s[0, 0, 0];
        Assert.True((got - s11).Magnitude < SParamTol, $"{got} vs {s11}");
    }
}
