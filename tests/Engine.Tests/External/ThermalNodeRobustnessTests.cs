using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// A compiled electrothermal model states the bias range it is valid over by REFUSING outside it,
/// and its thermal pin carries a temperature rather than a voltage. Both facts change what the
/// Newton loop is allowed to do, and neither is visible anywhere in the netlist.
///
/// <para><b>The failure these exist for.</b> On a weakly-referenced thermal node a full Newton step
/// is easily large enough to leave the model's range — below absolute zero, in the case that
/// prompted this. The model refuses that point, correctly, and a solver that treats the first
/// refusal as the end of the solve throws away a run that was converging. Measured on a production
/// kit: the solve failed outright and reported a bias problem, on a circuit that has a perfectly
/// good operating point.</para>
/// </summary>
[Collection("ExternalDeviceRegistry")]
public sealed class ThermalNodeRobustnessTests : IDisposable
{
    private const string Provider = "thermal-probe";

    public ThermalNodeRobustnessTests()
    {
        ExternalDeviceRegistry.Clear();
        ExternalDeviceRegistry.Register(new HeaterProvider(Provider));
    }

    public void Dispose() => ExternalDeviceRegistry.Clear();

    // ── The fixture ───────────────────────────────────────────────────────────

    /// <summary>
    /// How the fixture's dissipation varies with its own temperature. Each shape exists to drive one
    /// solver trajectory, and both trajectories were computed independently before being relied on
    /// here — a fixture whose path through the solve is assumed rather than known can pass a refusal
    /// test without a refusal ever happening.
    /// </summary>
    private enum HeatShape
    {
        /// <summary>
        /// <c>P(T) = P₀/(1 + (T/50)⁴)</c>. Flat at the origin, so the first Newton step is set by the
        /// external resistance alone and OVERSHOOTS; the fourth-power rolloff then pulls it back. At
        /// P₀ = 5 mW into 20,000 °C/W the path is 0 → 100 → 22.9 → 65.5 → 46.9 → 50, settling at
        /// exactly 50 °C. That first excursion to 100 is what a refusal ceiling can sit in.
        /// </summary>
        Saturating,

        /// <summary>
        /// <c>P(T) = P₀·(2 − e^(−T/400))</c>. Its slope at the origin outweighs a keep-alive
        /// resistance, so the Jacobian points the wrong way and the first step is a large NEGATIVE
        /// excursion — 0 → −403 at P₀ = 5 mW into 10⁷ °C/W, which is past absolute zero. This is the
        /// shape of the real failure, and the only one that exercises the floor.
        /// </summary>
        Runaway,

        /// <summary>
        /// <c>P(T) = P₀</c>, flat. The thermal node is then LINEAR and Newton reaches it exactly in
        /// one step from any start, at any resistance — which is what the reporting tests want, since
        /// they are about the value of the resistance and not about whether a fixture converges.
        /// </summary>
        Constant,
    }

    /// <summary>
    /// A two-node device that does nothing but heat: it drives power into its thermal pin exactly as
    /// a real electrothermal model does — a current numerically equal to watts, read back as degrees
    /// — and draws an ordinary linear current at its electrical pin.
    ///
    /// <para>Deliberately minimal. These tests are about the SOLVER's behaviour around a thermal
    /// node, so the device is the simplest thing that produces one; anything richer would put its own
    /// convergence behaviour between the test and what is being asserted.</para>
    /// </summary>
    private sealed class HeaterProvider(string name) : IExternalDeviceProvider
    {
        public const string TypeName = "Heater";
        public const int    Elec = 0, Thermal = 1, NodeCount = 2;

        public string Name { get; } = name;

        public static readonly ExternalDeviceDescriptor TypeDescriptor = new(
            TypeId:            TypeName,
            DisplayName:       "Heater (synthetic)",
            ExternalPinCount:  2,
            InternalNodeCount: 0,
            Parameters:
            [
                new ExternalParamDescriptor("Power", ExternalParamKind.Double, "0.005", "W"),
                new ExternalParamDescriptor("Gelec", ExternalParamKind.Double, "0.001", "S"),
                new ExternalParamDescriptor("Shape", ExternalParamKind.Double, "0",     ""),
                // The validity range, stated the way a real model states it: by refusing outside.
                // Both ends are settable so a test can drive the refusal path from either side, and
                // in particular WITHOUT relying on absolute zero — otherwise the floor and the
                // backoff cannot be told apart.
                new ExternalParamDescriptor("MinTemp", ExternalParamKind.Double, "-1e30", "degC"),
                new ExternalParamDescriptor("MaxTemp", ExternalParamKind.Double, "1e30",  "degC"),
                // An INTERNAL thermal resistance, to its own zero reference. A provider is entitled
                // to carry one, and one that does has already referenced its thermal node — which is
                // the difference between an open pin that is floating and an open pin that is fine.
                // Infinite by default, so every test above is untouched.
                new ExternalParamDescriptor("Rth", ExternalParamKind.Double, "0", "degC/W"),
            ],
            Nodes:
            [
                new ExternalNodeDescriptor(Elec,    External: true, NodeQuantityKind.Electrical, "elec"),
                new ExternalNodeDescriptor(Thermal, External: true, NodeQuantityKind.Thermal,    "thermal"),
            ]);

        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [TypeDescriptor];

        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => new Instance(parameters);

        /// <summary>Every thermal-node value any instance was asked to evaluate at, across the run.</summary>
        public static readonly List<double> AskedTemperatures = [];

        public static double Dissipation(double p0, HeatShape shape, double t) => shape switch
        {
            HeatShape.Saturating => p0 / (1.0 + Math.Pow(t / 50.0, 4)),
            HeatShape.Runaway    => p0 * (2.0 - Math.Exp(-t / 400.0)),
            _                    => p0,
        };

        private static double Slope(double p0, HeatShape shape, double t) => shape switch
        {
            HeatShape.Saturating => -p0 * 4.0 * Math.Pow(t, 3) / Math.Pow(50.0, 4)
                                    / Math.Pow(1.0 + Math.Pow(t / 50.0, 4), 2),
            HeatShape.Runaway    => p0 * Math.Exp(-t / 400.0) / 400.0,
            _                    => 0.0,
        };

        private sealed class Instance(IReadOnlyDictionary<string, string> p) : IExternalDeviceInstance
        {
            private readonly double    _power   = Get(p, "Power", 0.005);
            private readonly double    _gElec   = Get(p, "Gelec", 0.001);
            private readonly HeatShape _shape   = (HeatShape)(int)Get(p, "Shape", 0);
            private readonly double    _minTemp = Get(p, "MinTemp", -1e30);
            private readonly double    _maxTemp = Get(p, "MaxTemp",  1e30);
            private readonly double    _rth     = Get(p, "Rth",     0.0);   // 0 = none

            private static double Get(IReadOnlyDictionary<string, string> p, string k, double dflt)
                => p.TryGetValue(k, out var s) &&
                   double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : dflt;

            public ExternalDeviceDescriptor Descriptor => TypeDescriptor;

            public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> v)
            {
                double t = v[Thermal];
                AskedTemperatures.Add(t);

                if (t < _minTemp || t > _maxTemp)
                    throw new ExternalDeviceException(
                        $"'{TypeName}' refuses T = {t:G6}: outside its valid range " +
                        $"[{_minTemp:G6}, {_maxTemp:G6}].");

                var i = new double[NodeCount];
                i[Elec]    = _gElec * v[Elec];
                i[Thermal] = -Dissipation(_power, _shape, t);   // out of the node: watts as amps
                if (_rth > 0) i[Thermal] += t / _rth;          // its own path back to its own zero

                var g = new double[NodeCount, NodeCount];
                g[Elec,    Elec]    = _gElec;
                g[Thermal, Thermal] = -Slope(_power, _shape, t) + (_rth > 0 ? 1.0 / _rth : 0.0);

                return new ExternalDeviceEvaluation(i, new double[NodeCount], g, new double[NodeCount, NodeCount]);
            }

            public void Dispose() { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const double P0 = 0.005;

    private static string Netlist(
        double rThermal, HeatShape shape = HeatShape.Saturating,
        double minTemp = -1e30, double maxTemp = 1e30, double vElec = 1.0)
    {
        static string N(double d) => d.ToString("G17", CultureInfo.InvariantCulture);

        // AMBIENT 0 °C, deliberately. This fixture's thermal resistance runs to ground, so the
        // temperature it produces is a rise above zero — which is only the junction temperature if
        // the ambient IS zero. Saying so keeps the oracle below (T/R = P(T)) exact and keeps this
        // file's subject the SOLVER, rather than quietly making every case here an example of the
        // mis-referenced network that AThermalNetworkTiedToGround_IsReported is about.
        return $"temp = 0\n" +
               $"Vdc:V1  e  0  Vdc={N(vElec)}\n" +
               $"R:Rth   tj 0  R={N(rThermal)}\n" +
               $"ExtDevice:X1  e  tj  Provider={Provider} Type={HeaterProvider.TypeName} " +
               $"Power={N(P0)} Gelec=0.001 Shape={(int)shape} " +
               $"MinTemp={N(minTemp)} MaxTemp={N(maxTemp)}\n";
    }

    private static (NonlinearDcEngine.DcResult Result, ElaboratedNetlist Netlist) Run(string cnl)
    {
        HeaterProvider.AskedTemperatures.Clear();
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (NonlinearDcEngine.Run(nl), nl);
    }

    private static double NodeV(NonlinearDcEngine.DcResult r, ElaboratedNetlist nl, string name)
    {
        int idx = nl.Nodes.GetOrAssign(name);
        return idx == 0 ? 0.0 : r.NodeVoltages[idx - 1];
    }

    /// <summary>
    /// Where the thermal node settles, solved as a scalar by bisection on T/R = P(T) — derived
    /// without touching the engine, so an assertion against it tests the solve rather than restates
    /// it. The tolerance follows from the solver's own stopping rule: it stops at AbsTol on the
    /// residual NORM, which on this node is a current, so the implied slack in temperature is that
    /// tolerance divided by the node's conductance.
    /// </summary>
    private static void AssertSettlesAt(double r, double actual)
    {
        double lo = 0.0, hi = 1e9;
        for (int k = 0; k < 300; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid / r - HeaterProvider.Dissipation(P0, HeatShape.Saturating, mid) < 0) lo = mid;
            else hi = mid;
        }
        double expected = 0.5 * (lo + hi);
        double tol      = Math.Max(1e-6 * r, 1e-6);

        Assert.True(Math.Abs(expected - actual) < tol,
            $"oracle {expected:G12} °C, engine {actual:G12} °C (tolerance {tol:G3})");
    }

    // ── The fixture really is an electrothermal node ──────────────────────────

    [Fact]
    public void AThermalNodeSettlesWhereItsOwnPowerAndResistanceSayItShould()
    {
        // Asserted before anything else, because every test below depends on it: this node's
        // temperature is set by the device's own dissipation through its own path, and nothing else.
        var (r, nl) = Run(Netlist(rThermal: 20_000.0));

        Assert.True(r.Converged);
        AssertSettlesAt(20_000.0, NodeV(r, nl, "tj"));
    }

    // ── 1. A refused point is not a failed solve ──────────────────────────────

    [Fact]
    public void AModelRefusingAPointOnTheWay_StillConverges()
    {
        // The refusal ceiling sits at 80, and the first Newton step from a cold start lands at 100 on
        // its way to 50 — so the solve MUST pass through a refused point to get there. Deliberately a
        // ceiling rather than a floor: absolute zero is nowhere near this, so the clamp cannot be
        // what rescues it and only backing off along the step can.
        var (r, nl) = Run(Netlist(rThermal: 20_000.0, maxTemp: 80.0));

        Assert.True(r.Converged, "a model refusing an intermediate point ended the solve");
        AssertSettlesAt(20_000.0, NodeV(r, nl, "tj"));

        Assert.Contains(HeaterProvider.AskedTemperatures, t => t > 80.0);   // the refusal really fired
    }

    [Fact]
    public void AStartingPointTheModelRefuses_IsStillAFailure()
    {
        // Backing off is only meaningful when there is a point already known good to back off
        // TOWARD. A model refusing from the very first evaluation is saying this bias is outside its
        // range, and reporting that is right — retrying it forever is not.
        var (r, _) = Run(Netlist(rThermal: 20_000.0, maxTemp: -1.0));

        Assert.False(r.Converged);
    }

    // ── 2. A thermal node is never stepped below absolute zero ────────────────

    [Fact]
    public void NoThermalNodeIsEverEvaluatedBelowAbsoluteZero()
    {
        // Runaway into a keep-alive resistance: the first step is −403, which is not a temperature.
        // Nothing here refuses anything (the range is wide open), so this asserts the floor alone —
        // without it that −403 would simply be handed to the model.
        Run(Netlist(rThermal: 1e7, shape: HeatShape.Runaway));

        Assert.NotEmpty(HeaterProvider.AskedTemperatures);
        Assert.All(HeaterProvider.AskedTemperatures, t => Assert.True(t >= -273.0,
            $"a thermal node was evaluated at {t:G6}, which is not a temperature"));
    }

    [Fact]
    public void AnElectricalNodeIsNotClamped()
    {
        // The floor is a property of temperature, not a convergence aid. Clamping a voltage would be
        // exactly the sort of quiet interference that makes a wrong answer look converged.
        var (r, nl) = Run(Netlist(rThermal: 20_000.0, vElec: -500.0));

        Assert.True(r.Converged);
        Assert.Equal(-500.0, NodeV(r, nl, "e"), 6);
    }

    // ── 3. An implausible thermal resistance is reported ──────────────────────

    [Fact]
    public void AThermalNodeReachedOnlyThroughAKeepAliveResistor_IsReportedByName()
    {
        // The case this exists for. The node is NOT floating — it has a resistor — so nothing
        // structural distinguishes it from a properly referenced one. What gives it away is the
        // value, which is not a thermal resistance by five orders of magnitude.
        var (r, nl) = Run(Netlist(rThermal: 5e7, shape: HeatShape.Constant));

        Assert.True(r.Converged, "this is a warning, not a failure — the run must still answer");

        string warning = Assert.Single(nl.Warnings, w => w.Contains("thermal node", StringComparison.Ordinal));
        Assert.Contains("tj",    warning, StringComparison.Ordinal);   // the node, by name
        Assert.Contains("X1",    warning, StringComparison.Ordinal);   // the device that owns it
        Assert.Contains("5E+07", warning, StringComparison.Ordinal);   // what was actually measured
    }

    [Fact]
    public void ARealThermalResistance_IsNotReported()
    {
        // 200 °C/W is an ordinary junction-to-ambient figure for a small part. A check that fired
        // here would be noise, and noise is what stops the real warning being read.
        var (_, nl) = Run(Netlist(rThermal: 200.0, shape: HeatShape.Constant));

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("thermal node", StringComparison.Ordinal));
    }

    [Fact]
    public void TheThresholdIsWellClearOfBothRealValuesAndKeepAliveResistors()
    {
        // Pins the MARGIN rather than the number: whatever the threshold is, it has to sit far above
        // any real thermal resistance and far below a keep-alive leak, or it is a coin toss.
        Assert.True(NonlinearDcEngine.ImplausibleThermalResistance >= 1e4,
            "close enough to real thermal resistances that it could fire on a real design");
        Assert.True(NonlinearDcEngine.ImplausibleThermalResistance <= 1e6,
            "high enough that a keep-alive leak resistor could slip under it");
    }

    // ── The thermal network's own reference ───────────────────────────────────

    /// <summary>
    /// The same heater, with its thermal resistance run to a chosen reference instead of to ground,
    /// and an ambient the design actually states. <paramref name="referenceC"/> null ties it to
    /// ground — the mistake these tests are about.
    /// </summary>
    private static string ReferencedNetlist(double ambientC, double? referenceC, double rThermal = 200.0)
    {
        static string N(double d) => d.ToString("G17", CultureInfo.InvariantCulture);

        string network = referenceC is { } refC
            ? $"Vdc:VAMB amb 0  Vdc={N(refC)}\n" + $"R:Rth   tj amb  R={N(rThermal)}\n"
            : $"R:Rth   tj 0    R={N(rThermal)}\n";

        return $"temp = {N(ambientC)}\n" +
               $"Vdc:V1  e  0  Vdc=1\n" +
               network +
               $"ExtDevice:X1  e  tj  Provider={Provider} Type={HeaterProvider.TypeName} " +
               $"Power={N(P0)} Gelec=0.001 Shape={(int)HeatShape.Constant}\n";
    }

    [Fact]
    public void AThermalNetworkTiedToGround_IsReported()
    {
        // The whole error, in one number: the model is asked to evaluate at the RISE, so its
        // junction temperature is short by the entire ambient. The solve is perfectly happy.
        var (r, nl) = Run(ReferencedNetlist(ambientC: 25.0, referenceC: null));

        Assert.True(r.Converged, "this is a warning, not a failure — the run must still answer");

        string w = Assert.Single(nl.Warnings, x => x.Contains("referenced to", StringComparison.Ordinal));
        Assert.Contains("tj", w, StringComparison.Ordinal);   // the node, by name
        Assert.Contains("X1", w, StringComparison.Ordinal);   // the device that owns it
        Assert.Contains("25", w, StringComparison.Ordinal);   // the ambient it should have been at

        // And the damage is exactly the ambient, which is what makes it worth a warning at all.
        double grounded = NodeV(r, nl, "tj");
        var (r2, nl2)   = Run(ReferencedNetlist(ambientC: 25.0, referenceC: 25.0));
        Assert.Equal(25.0, NodeV(r2, nl2, "tj") - grounded, 6);
    }

    [Fact]
    public void AThermalNetworkReferencedToTheAmbient_IsNotReported()
    {
        var (_, nl) = Run(ReferencedNetlist(ambientC: 25.0, referenceC: 25.0));

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("referenced to", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceThatIsNotTheAmbientButIsNotGroundEither_IsNotReported()
    {
        // A heatsink base sitting at 60 °C in a 25 °C room is a design decision, not a mistake. The
        // check is specifically "referenced to ground" — anything else is the designer's business,
        // and a check that second-guessed it would fire on the designs that are most careful.
        var (_, nl) = Run(ReferencedNetlist(ambientC: 25.0, referenceC: 60.0));

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("referenced to", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAmbientOfZero_MakesGroundTheRightReference_AndIsNotReported()
    {
        // No false positive on the one design where tying the network to ground is exactly correct.
        var (_, nl) = Run(ReferencedNetlist(ambientC: 0.0, referenceC: null));

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("referenced to", StringComparison.Ordinal));
    }

    // ── An unconnected thermal terminal ───────────────────────────────────────

    /// <summary>The heater with its thermal pin wired to nothing at all.</summary>
    private static string OpenThermalNetlist(double ambientC, int devices = 1)
    {
        static string N(double d) => d.ToString("G17", CultureInfo.InvariantCulture);

        string s = $"temp = {N(ambientC)}\nVdc:V1  e  0  Vdc=1\n";
        for (int k = 1; k <= devices; k++)
            s += $"ExtDevice:X{k}  e  tj  Provider={Provider} Type={HeaterProvider.TypeName} " +
                 $"Power={N(P0)} Gelec=0.001 Shape={(int)HeatShape.Constant}\n";
        return s;
    }

    [Fact]
    public void AnUnconnectedThermalTerminal_SolvesAtTheAmbient_InsteadOfNotSolvingAtAll()
    {
        // THE REGRESSION. A thermal pin left open is a floating node fed by a constant current
        // source: it has no operating point, the temperature runs to the absolute-zero floor, and
        // the solve grinds through every ramp step and every halving before failing. Measured on a
        // kit before this was fixed: 6,210 Newton iterations, no convergence, and a reported
        // bias point the ramp never even reached. The host supplying the reference the design did
        // not is what turns that into an answer.
        var (r, nl) = Run(OpenThermalNetlist(ambientC: 25.0));

        Assert.True(r.Converged, "an unconnected thermal terminal must not cost the whole solve");
        Assert.True(r.Iterations < 20, $"took {r.Iterations} iterations — the runaway is back");
        Assert.Equal(25.0, NodeV(r, nl, "tj"), 6);   // ambient, and no rise: none was stated
    }

    [Fact]
    public void PinningAnUnconnectedThermalTerminal_IsAnnounced()
    {
        // The design did not ask for this, so it is never allowed to be silent.
        var (_, nl) = Run(OpenThermalNetlist(ambientC: 25.0));

        string w = Assert.Single(nl.Warnings, x => x.Contains("not connected", StringComparison.Ordinal));
        Assert.Contains("tj", w, StringComparison.Ordinal);
        Assert.Contains("X1", w, StringComparison.Ordinal);
        Assert.Contains("25", w, StringComparison.Ordinal);
    }

    [Fact]
    public void AThermalNetSharedBetweenDevicesAndReachingNothingElse_IsStillUnreferenced()
    {
        // Two devices on one thermal net, and that net touches nothing else. Counting "is any other
        // component on this node" would call it connected — each device sees the other — and leave
        // it exactly as singular as before. What makes a node referenced is something OTHER than a
        // thermal pin reaching it.
        var (r, nl) = Run(OpenThermalNetlist(ambientC: 25.0, devices: 2));

        Assert.True(r.Converged);
        Assert.Equal(25.0, NodeV(r, nl, "tj"), 6);
    }

    [Fact]
    public void AConnectedThermalTerminal_IsLeftEntirelyAlone()
    {
        // The other half of the contract: a design that DID state its thermal network gets nothing
        // added to it, and no warning. Asserted against the temperature, which a spurious extra
        // source holding the node at the ambient would visibly change.
        var (r, nl) = Run(ReferencedNetlist(ambientC: 25.0, referenceC: 25.0, rThermal: 200.0));

        Assert.DoesNotContain(nl.Warnings, w => w.Contains("not connected", StringComparison.Ordinal));
        Assert.Equal(25.0 + P0 * 200.0, NodeV(r, nl, "tj"), 6);   // ambient + P·R, the rise intact
    }

    [Fact]
    public void ADeviceThatCarriesItsOwnThermalResistance_IsNotPinned_EvenWithAnOpenPin()
    {
        // The boundary. A provider is entitled to model its own thermal resistance, and one that
        // does has already referenced the node: its Jacobian has a real entry there, the open pin is
        // not floating, and the rise it solves for IS the answer the design wanted. Holding it at the
        // ambient would silently delete the device's self-heating — which is why the rule asks the
        // device rather than reading the topology alone.
        const double rth = 200.0;
        string cnl = OpenThermalNetlist(ambientC: 25.0).TrimEnd('\n') +
                     $" Rth={rth.ToString("G17", CultureInfo.InvariantCulture)}\n";

        var (r, nl) = Run(cnl);

        Assert.True(r.Converged);
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("not connected", StringComparison.Ordinal));
        Assert.Equal(P0 * rth, NodeV(r, nl, "tj"), 6);   // its own rise, above its own zero, intact
    }

    [Fact]
    public void ADeviceWhoseThermalDiagonalIsNegative_IsStillPinned()
    {
        // The subtlest half of the rule, and the one that bites. An electrothermal model's thermal
        // row is routinely non-zero and NEGATIVE: that entry is its self-heating FEEDBACK, which
        // pushes the node away from a solution rather than holding it near one. Reading "non-zero"
        // as "the device references this node" therefore leaves the exact devices this exists for
        // unpinned — verified against a real compiled model, whose thermal diagonal is a small
        // negative number and which went straight back to not converging. The SIGN is the test.
        string cnl = OpenThermalNetlist(ambientC: 25.0)
                     .Replace($"Shape={(int)HeatShape.Constant}", $"Shape={(int)HeatShape.Runaway}",
                              StringComparison.Ordinal);

        var (r, nl) = Run(cnl);

        Assert.True(r.Converged, "a negative thermal diagonal is not a reference — this must be pinned");
        Assert.Contains(nl.Warnings, w => w.Contains("not connected", StringComparison.Ordinal));
        Assert.Equal(25.0, NodeV(r, nl, "tj"), 6);
    }

    // ── A solve that cannot settle has to give up in bounded time ─────────────

    [Fact]
    public void ASolveThatCannotSettle_GivesUpWithinItsBudget()
    {
        // Positive thermal feedback with only a keep-alive path back: there is no operating point at
        // any temperature, and no amount of continuation finds one. What matters is how long it
        // takes to say so. A ramp step that succeeds only after halving leaves the ramp barely
        // moved, so the outer loop runs again from almost the same place — and with nothing bounding
        // it, a solve that creeps spends an unlimited number of Newton iterations arriving nowhere.
        // Measured on a real design before this was bounded: 403,893 iterations, every one of them a
        // round trip to an external model, before an answer that was already true at a few thousand.
        var settings = new AnalysisSettings();
        int budget   = settings.DcBiasRampSteps * settings.NonlinearMaxHalvings * settings.NonlinearMaxIter;

        var (r, _) = Run(Netlist(rThermal: 1e7, shape: HeatShape.Runaway));

        Assert.False(r.Converged, "the fixture is chosen to have no solution — if this passes, it is wrong");
        Assert.True(r.Iterations <= budget,
            $"spent {r.Iterations} iterations against a stated budget of {budget}");
    }

    [Fact]
    public void ASolveThatFails_NamesTheUnknownFurthestFromSettling()
    {
        // "residual 6.83" is a number with no address: it names neither the part of the circuit that
        // will not settle nor how far off it is, which leaves bisecting the schematic as the only way
        // forward. One row is almost always enormously worse than the rest.
        var (r, nl) = Run(Netlist(rThermal: 1e7, shape: HeatShape.Runaway));

        Assert.False(r.Converged);
        string w = Assert.Single(nl.Warnings, x => x.Contains("Worst-unsettled", StringComparison.Ordinal));

        // Cross-checked against the residual vector rather than against a guess at which row wins.
        // Which unknown is worst is the fixture's business and can move; that the message names the
        // one the numbers actually pick out is the contract.
        int worst = 0;
        for (int i = 1; i < r.ResidualPerUnknown.Length; i++)
            if (Math.Abs(r.ResidualPerUnknown[i]) > Math.Abs(r.ResidualPerUnknown[worst])) worst = i;

        string expected = worst < r.NodeVoltages.Length
            ? nl.Nodes.NameOf(worst + 1)
            : r.BranchOwners[worst];
        Assert.Contains(expected, w, StringComparison.Ordinal);
    }

    // ── How well a thermal node is referenced is SOLVED, not estimated ────────

    [Fact]
    public void AThermalNodeHeldByAnIdealSource_IsNotReportedAsUnreachable()
    {
        // The false positive this replaced. `1/G[row,row]` looks like a safe lower bound on a node's
        // resistance and is not one: an ideal voltage source lives in a BRANCH row and puts no
        // conductance on the node diagonal at all, so the diagonal reads zero and the bound reads
        // infinity. The result was that the best-referenced node possible — a perfect isothermal
        // boundary, which is how a bench pins a part at a stated case temperature — drew the
        // loudest "no path at all" warning in the file. Seen on a kit's own reference bench,
        // on a run whose answer was correct to six figures.
        string cnl = "temp = 25\n" +
                     "Vdc:V1  e  0   Vdc=1\n" +
                     "Vdc:VT  tj 0   Vdc=25\n" +
                     $"ExtDevice:X1  e  tj  Provider={Provider} Type={HeaterProvider.TypeName} " +
                     $"Power={P0.ToString("G17", CultureInfo.InvariantCulture)} Gelec=0.001 " +
                     $"Shape={(int)HeatShape.Constant}\n";

        var (r, nl) = Run(cnl);

        Assert.True(r.Converged);
        Assert.Equal(25.0, NodeV(r, nl, "tj"), 9);
        Assert.DoesNotContain(nl.Warnings, w => w.Contains("thermal node", StringComparison.Ordinal));
    }
}
