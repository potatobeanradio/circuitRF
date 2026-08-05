using System;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// A design-wide ambient temperature, stated as the global <c>temp</c> (°C), reaching the devices
/// that are temperature-aware.
///
/// <para><b>Why the observation is indirect.</b> A model bakes its temperature in at construction
/// and exposes it nowhere, so these tests recover it from BEHAVIOUR: with <c>Is = 1</c> and
/// <c>N = 1</c> a diode's conduction current is <c>exp(V/Vt) − 1</c>, so <c>Vt = V / ln(I + 1)</c>
/// and <c>T = Vt·q/k</c>. That is a stronger check than a property read would be — it proves the
/// number reached the equations, not merely a field.</para>
///
/// <para><b>The fixture states <c>Xti=0 Eg=0</c>, and that is what keeps the oracle honest.</b> A
/// real diode's saturation current moves with temperature too, so the inversion above would have to
/// undo that movement before it could read <c>Vt</c> — using the model's own temperature code to
/// check the model's own temperature code, which proves nothing. Switching the bandgap term off
/// holds <c>Is</c> at 1 and leaves exactly one temperature-dependent quantity in the answer. The
/// saturation current's own temperature dependence is gated separately, in
/// <c>DiodeModelTests</c>, against a closed form.</para>
///
/// <para><b>The two that matter most</b> are A1 (a design saying nothing about temperature is
/// bit-identical to before ambient existed) and A6 (ambient does NOT move <c>Tnom</c> — moving both
/// together would cancel the very ΔT being asked for, and every temperature relation would quietly
/// collapse to the identity while appearing to work).</para>
/// </summary>
public class AmbientTemperatureTests
{
    private const double V = 0.01;   // well inside the exponential region

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>Recovers a diode's junction temperature in °C from its own conduction current.</summary>
    private static double DeviceTempC(ElaboratedNetlist n, string type = "Diode")
    {
        var c = n.Components.Single(x => x.ComponentType.Equals(type, StringComparison.OrdinalIgnoreCase));
        var r = c.Model.Evaluate(new PortVoltages([V]));
        double vt = V / Math.Log(r.I[0] + 1.0);
        return Temperature.ToCelsius(vt * Temperature.ElemCharge / Temperature.Boltzmann);
    }

    private const string Diode = "Diode:D1  a 0  Is=1 N=1 Xti=0 Eg=0\nR:R1 a 0 R=1000";

    // ── A1 — the regression that makes this additive ──────────────────────────

    [Fact]
    public void A1_NoTempGlobal_DeviceSitsAtTheNominal()
    {
        var n = Elaborate(Diode);

        Assert.Equal(Temperature.NominalC, DeviceTempC(n), 9);
        // ...and says nothing about it. A design that never mentions temperature must not acquire
        // a message about temperature.
        Assert.DoesNotContain(n.Warnings, w => w.Contains("mbient", StringComparison.Ordinal));
    }

    // ── A2 — ambient reaches a device that states nothing ─────────────────────

    [Fact]
    public void A2_TempGlobal_BecomesTheDeviceTemperature()
    {
        var n = Elaborate($"temp = 85\n{Diode}");

        Assert.Equal(85.0, DeviceTempC(n), 9);
        // Reported, because the user did not ask for `temp` to mean this.
        Assert.Contains(n.Warnings, w => w.Contains("85", StringComparison.Ordinal)
                                      && w.Contains("temp", StringComparison.Ordinal));
    }

    // ── A3 — Dtemp is a RISE above ambient ────────────────────────────────────

    [Fact]
    public void A3_DtempAddsToAmbient()
    {
        var n = Elaborate("temp = 85\nDiode:D1  a 0  Is=1 N=1 Xti=0 Eg=0 Dtemp=10\nR:R1 a 0 R=1000");
        Assert.Equal(95.0, DeviceTempC(n), 9);
    }

    [Fact]
    public void A3b_DtempWithNoAmbient_RisesAboveTheNominal()
    {
        var n = Elaborate("Diode:D1  a 0  Is=1 N=1 Xti=0 Eg=0 Dtemp=10\nR:R1 a 0 R=1000");
        Assert.Equal(Temperature.NominalC + 10.0, DeviceTempC(n), 9);
    }

    // ── A4 — an explicit Temp is ABSOLUTE and overrides ambient entirely ──────

    [Fact]
    public void A4_ExplicitTempOverridesAmbient()
    {
        var n = Elaborate("temp = 85\nDiode:D1  a 0  Is=1 N=1 Xti=0 Eg=0 Temp=40\nR:R1 a 0 R=1000");
        Assert.Equal(40.0, DeviceTempC(n), 9);
    }

    // ── A5 — stating both is resolved, and said out loud ──────────────────────

    [Fact]
    public void A5_TempAndDtemp_TempWinsAndItIsReported()
    {
        var n = Elaborate("temp = 85\nDiode:D1  a 0  Is=1 N=1 Xti=0 Eg=0 Temp=40 Dtemp=10\nR:R1 a 0 R=1000");

        Assert.Equal(40.0, DeviceTempC(n), 9);        // not 95, and not 50
        Assert.Contains(n.Warnings, w => w.Contains("Dtemp", StringComparison.Ordinal)
                                      && w.Contains("ignored", StringComparison.Ordinal));
    }

    // ── A6 — ambient must NOT drag Tnom with it ───────────────────────────────

    /// <summary>
    /// The silent one. <c>Tnom</c> is the parameter set's extraction temperature — a property of the
    /// model card, not of the run. If ambient moved it too, ΔT would be zero at every ambient and
    /// every temperature relation would collapse to the identity while looking entirely healthy.
    ///
    /// A FET whose Beta carries a temperature coefficient is the probe: at an ambient well away from
    /// the card's stated Tnom the drain current MUST differ from the same device at Tnom. The test
    /// first asserts the two candidate temperatures actually produce different currents, so it
    /// cannot pass vacuously.
    /// </summary>
    [Fact]
    public void A6_AmbientDoesNotMoveTnom()
    {
        const string fet = "FET_Curtice:Q1 g d 0 Vto=-2 Beta=0.05 Lambda=0.05 Alpha=2 Betatc=-0.5 Tnom=27";

        double IdAt(string prefix)
        {
            var n = Elaborate($"{prefix}{fet}\nR:R1 d 0 R=1000");
            var c = n.Components.Single(x => x.ComponentType.StartsWith("FET_", StringComparison.OrdinalIgnoreCase));
            return c.Model.Evaluate(new PortVoltages([-1.0, 5.0])).I[1];
        }

        double atTnom = IdAt("temp = 27\n");     // ambient == the card's own Tnom → ΔT is exactly 0
        double atHot  = IdAt("temp = 125\n");    // ΔT = 98 degrees against that same Tnom

        // Had ambient dragged Tnom along with it, ΔT would be zero at BOTH and these two would be
        // identical — the device would look temperature-aware and be doing nothing. That is the
        // whole failure this test exists for, so it is asserted as a relative difference rather
        // than a bare inequality that a 1-ulp wobble could satisfy.
        Assert.True(Math.Abs(atHot - atTnom) > 1e-6 * Math.Abs(atTnom),
            $"Ambient moved Tnom with it: ΔT collapsed to zero (Id {atTnom:G6} vs {atHot:G6}).");
    }

    // ── A7 — a temperature SWEEP needs no new mechanism ───────────────────────

    /// <summary>
    /// <c>ParametricSweepEngine</c> sweeps by overriding a global and re-elaborating every point
    /// (verified at <c>ParametricSweepEngine.cs:105</c>). This reproduces that exact mechanism, so
    /// it proves a temperature sweep works without depending on the engine.
    /// </summary>
    [Fact]
    public void A7_OverridingTheGlobalAndReElaborating_MovesTheDevice()
    {
        var (lib, tb) = new CnlReader().Read($"temp = 25\n{Diode}");

        double At(double ambientC)
        {
            var original = tb.GlobalVariables.ToList();
            try
            {
                tb.GlobalVariables.Clear();
                foreach (var v in original)
                    tb.GlobalVariables.Add(
                        v.Name.Equals("temp", StringComparison.OrdinalIgnoreCase)
                            // Unit MUST be null, not "": ApplyUnit returns early only on null, so
                            // an empty string reaches Units.Scale and throws "Unknown unit ''".
                            // ParametricSweepEngine carries the same guard explicitly, which is how
                            // this reproduces its real behaviour rather than a near-miss of it.
                            ? new Variable("temp", ambientC.ToString(System.Globalization.CultureInfo.InvariantCulture), null)
                            : v);
                return DeviceTempC(new Elaborator(lib).Elaborate(tb));
            }
            finally
            {
                tb.GlobalVariables.Clear();
                foreach (var v in original) tb.GlobalVariables.Add(v);
            }
        }

        Assert.Equal(-40.0, At(-40.0), 9);
        Assert.Equal(125.0, At(125.0), 9);
        // The restore actually restored — the sweep engine relies on this too.
        Assert.Equal(25.0, DeviceTempC(new Elaborator(lib).Elaborate(tb)), 9);
    }

    // ── A8 — a temp that is not a number degrades, it does not deny ───────────

    [Fact]
    public void A8_UnusableTempGlobal_IsReportedAndTheNominalIsUsed()
    {
        var n = Elaborate($"temp = \"hot\"\n{Diode}");

        Assert.Equal(Temperature.NominalC, DeviceTempC(n), 9);
        Assert.Contains(n.Warnings, w => w.Contains("temp", StringComparison.Ordinal));
    }
}
