using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Measurements;

/// <summary>
/// A global DERIVED from a swept one (<c>y = 2*x</c>, <c>x</c> swept) read by a measurement: one value per sweep point,
/// as the swept variable itself is (<see cref="SweptVariableMeasurementTests"/>), not the nominal value of the run's first
/// elaboration. The circuit always followed the sweep — every point re-elaborates — so each test also reads a source set
/// to the derived variable, and the measurement must agree with it: it is evaluated at each point as the elaborator did.
/// </summary>
public class DerivedVariableMeasurementTests
{
    /// <summary>Runs the outermost sweep of <paramref name="cnl"/> and evaluates its measurements as a run does: against the
    /// netlist the run elaborated first, the sweep's results under the name of the DC analysis it wraps.</summary>
    private static (DataSet Meas, IReadOnlyList<string> Errors) Run(string cnl, IStatisticalDraws? draws = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        tb.StatisticalDraws = draws;
        var sweeps = tb.Analyses.OfType<ParametricSweepAnalysis>().ToList();
        var top = sweeps.Single(s => !sweeps.Any(o => o.InnerAnalysisName == s.Name));
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = ParametricSweepEngine.Run(top, lib, tb);
        var meas = new DataSet();
        var errors = new MeasurementEvaluator(tb, nl, new Dictionary<string, DataSet> { ["DC1"] = ds }).EvaluateInto(meas);
        return (meas, errors);
    }

    private static double At(DataCube c, params (string Axis, int Index)[] pins)
    {
        foreach (var (axis, index) in pins) c = c.At(axis, index);
        Assert.Equal(0, c.Rank);
        return c.RealValues[0];
    }

    [Fact]
    public void ADerivedVariable_ReadPerPoint_MatchesTheHandComputation_AndTheCircuit()
    {
        var (meas, errors) = Run("""
            x = 1
            y = 2*x + 1
            Vdc:V1  in 0  Vdc=y
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=x Values=1,2,3 Inner=DC1
            measure y_read = y
            measure v_in   = DC1.V("in")
            """);

        Assert.Empty(errors);
        var y = meas["y_read"];
        Assert.Equal("x", Assert.Single(y.Axes).Name);
        Assert.Equal([3.0, 5.0, 7.0], y.RealValues);            // 2·x + 1 at x = 1, 2, 3
        var v = meas["v_in"].RealValues;
        for (int i = 0; i < 3; i++) Assert.Equal(y.RealValues[i], v[i], 9);   // the source the circuit was given
    }

    /// <summary>Two sweeps, nested: a variable of both is a cube over both, in the sweeps' order (outer first) whatever
    /// order the expression names them in, and still the circuit's value at every point.</summary>
    [Fact]
    public void AVariableOfTwoNestedSweeps_IsACubeOverBoth_InTheSweepsOrder()
    {
        var (meas, errors) = Run("""
            x = 1
            z = 10
            w = z*x + 1
            Vdc:V1  in 0  Vdc=w
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SWZ type=parametric_sweep Var=z Values=10,20 Inner=DC1
            analysis SWX type=parametric_sweep Var=x Values=1,2,3 Inner=SWZ
            measure w_read = w
            measure v_in   = DC1.V("in")
            """);

        Assert.Empty(errors);
        var w = meas["w_read"];
        Assert.Equal(["x", "z"], w.Axes.Select(a => a.Name));
        double[] xs = [1, 2, 3], zs = [10, 20];
        for (int i = 0; i < xs.Length; i++)
            for (int k = 0; k < zs.Length; k++)
            {
                Assert.Equal(xs[i] * zs[k] + 1, At(w, ("x", i), ("z", k)));
                Assert.Equal(xs[i] * zs[k] + 1, At(meas["v_in"], ("x", i), ("z", k)), 9);
            }
    }

    /// <summary>
    /// Units as each point applied them. The swept <c>L</c> is declared in mil, so each point injects it in metres marked as
    /// unit-bearing; <c>L2 = 2*L</c> declared in mil is then 2·L metres (var-unit-wins), and the bare 5 in <c>L + 5</c> is
    /// 5 mil. Reading <c>L</c> as a bare cube here would scale <c>L2</c> by the mil a second time.
    /// </summary>
    [Fact]
    public void ADerivedVariableWithAUnit_IsScaledAsEachPointScaledIt()
    {
        var (meas, errors) = Run("""
            L  = 10 mil
            L2 = 2*L mil
            Lp = L + 5 mil
            Vdc:V1  in 0  Vdc=1
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=L Values=10,30 Unit=mil Inner=DC1
            measure L2_read = L2
            measure Lp_read = Lp
            """);

        Assert.Empty(errors);
        const double Mil = 2.54e-5;
        Assert.Equal(2 * 10 * Mil, meas["L2_read"].RealValues[0], 15);
        Assert.Equal(2 * 30 * Mil, meas["L2_read"].RealValues[1], 15);
        Assert.Equal(15 * Mil, meas["Lp_read"].RealValues[0], 15);
        Assert.Equal(35 * Mil, meas["Lp_read"].RealValues[1], 15);
    }

    /// <summary>The phased-array shape: a phase step derived from a swept angle through <c>sin</c>, read by a measurement and
    /// set on a source — both cube-taking fixes at once.</summary>
    [Fact]
    public void APhaseDerivedThroughSin_FollowsTheAngleSweep()
    {
        var (meas, errors) = Run("""
            theta = 0
            psi = 360*0.5*sin(theta*pi/180)
            Vdc:V1  in 0  Vdc=psi
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=theta Values=-30,0,30,90 Inner=DC1
            measure psi_read = psi
            measure v_in     = DC1.V("in")
            measure u        = imag(exp(j*theta*pi/180))
            """);

        Assert.Empty(errors);
        double[] th = [-30, 0, 30, 90];
        for (int i = 0; i < th.Length; i++)
        {
            double hand = 180 * Math.Sin(th[i] * Math.PI / 180);
            Assert.Equal(hand, meas["psi_read"].RealValues[i], 12);
            Assert.Equal(hand, meas["v_in"].RealValues[i], 9);
            Assert.Equal(Math.Sin(th[i] * Math.PI / 180), meas["u"].RealValues[i], 14);
        }
    }

    /// <summary>Through user functions: an argument that is swept, and a body that reads the swept global itself.</summary>
    [Fact]
    public void ADerivedVariable_ThroughAUserFunction_FollowsTheSweep()
    {
        var (meas, errors) = Run("""
            x = 1
            f(a) = 3*a
            g(a) = a*x
            y1 = f(x)
            y2 = g(2)
            Vdc:V1  in 0  Vdc=y2
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=x Values=1,2 Inner=DC1
            measure y1_read = y1
            measure y2_read = y2
            """);

        Assert.Empty(errors);
        Assert.Equal([3.0, 6.0], meas["y1_read"].RealValues);
        Assert.Equal([2.0, 4.0], meas["y2_read"].RealValues);
    }

    /// <summary>
    /// Expressions that hold only at a point, as the elaborator evaluates them: a conditional, max, a clamp, a polar
    /// phasor in degrees. Over a cube each of them is refused; at each point each is a number.
    /// </summary>
    [Fact]
    public void ConditionalsAndTwoArgumentFunctions_ReadTheirValueAtEachPoint()
    {
        var (meas, errors) = Run("""
            x = 1
            y1 = x > 1 ? 1 : 2
            y2 = max(x, 0)
            y3 = limit(x, 0, 2)
            y4 = polar(1, x)
            Vdc:V1  in 0  Vdc=1
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=x Values=1,3 Inner=DC1
            measure a = y1
            measure b = y2
            measure c = y3
            measure d = y4
            """);

        Assert.Empty(errors);
        Assert.Equal([2.0, 1.0], meas["a"].RealValues);
        Assert.Equal([1.0, 3.0], meas["b"].RealValues);
        Assert.Equal([1.0, 2.0], meas["c"].RealValues);
        var d = meas["d"].ComplexValues;
        Assert.Equal(Complex.FromPolarCoordinates(1, Math.PI / 180), d[0]);
        Assert.Equal(Complex.FromPolarCoordinates(1, 3 * Math.PI / 180), d[1]);
    }

    /// <summary>A complex variable's own arithmetic: imag(log(Zl)) is its phase, which the circuit is given; a cube's
    /// log would have read |Zl| and that phase as 0.</summary>
    [Fact]
    public void AComplexDerivedVariable_IsTheCircuitsValue()
    {
        var (meas, errors) = Run("""
            x = 10
            Zl = 50 + j*x
            th = imag(log(Zl))
            Vdc:V1  in 0  Vdc=1000*th
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=x Values=10,20 Inner=DC1
            measure th_read = 1000*th
            measure v_in    = DC1.V("in")
            """);

        Assert.Empty(errors);
        Assert.Equal(1000 * Math.Atan(10.0 / 50), meas["th_read"].RealValues[0], 12);   // 197.4
        Assert.Equal(1000 * Math.Atan(20.0 / 50), meas["th_read"].RealValues[1], 12);   // 380.5
        for (int i = 0; i < 2; i++) Assert.Equal(meas["th_read"].RealValues[i], meas["v_in"].RealValues[i], 9);
    }

    /// <summary>The ambient <c>temp</c>, which the elaborator binds when the design does not declare it.</summary>
    [Fact]
    public void TheAmbientTemperature_IsBoundAsTheElaboratorBindsIt()
    {
        var (meas, errors) = Run("""
            R0 = 100
            RT = R0*(1 + 0.01*(temp - 27))
            Vdc:V1  in 0  Vdc=RT
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=R0 Values=100,200 Inner=DC1
            measure RT_read = RT
            measure v_in    = DC1.V("in")
            """);

        Assert.Empty(errors);
        Assert.Equal(99.85, meas["RT_read"].RealValues[0], 12);    // the ambient is 26.85 °C
        Assert.Equal(199.7, meas["RT_read"].RealValues[1], 12);
        for (int i = 0; i < 2; i++) Assert.Equal(meas["RT_read"].RealValues[i], meas["v_in"].RealValues[i], 9);
    }

    /// <summary>
    /// A Monte Carlo trial: a draw in a global the swept one does not touch (<c>k</c>) and draws inside the derived one
    /// (<c>y</c>) are the trial's own, so in every trial the measurement is the circuit's value at every point.
    /// </summary>
    [Fact]
    public void InEachMonteCarloTrial_TheDerivedVariableIsTheCircuitsValue()
    {
        const string Cnl = """
            x = 1
            k = gauss(1, 0.1)
            y = x*k + agauss(0, 0.3)
            Vdc:V1  in 0  Vdc=y
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=x Values=1,2,3 Inner=DC1
            measure y_read = y
            measure v_in   = DC1.V("in")
            """;
        var nominal = Run(Cnl).Meas["y_read"].RealValues;
        for (int trial = 1; trial <= 5; trial++)
        {
            var (meas, errors) = Run(Cnl, new Draws(trial));
            Assert.Empty(errors);
            var y = meas["y_read"].RealValues;
            Assert.NotEqual(nominal, y);                                            // the trial's draws, not the nominal
            for (int i = 0; i < 3; i++) Assert.Equal(y[i], meas["v_in"].RealValues[i], 9);
        }
    }

    /// <summary>A trial's draws: a pure function of the trial and the stream, as the real draws are.</summary>
    private sealed class Draws(int trial) : IStatisticalDraws
    {
        public StatisticalDraw? Draw(StatisticalKind kind, string stream)
        {
            int h = trial * 7919;
            foreach (char c in stream) h = unchecked(h * 31 + c);
            double u = (h & 0xFFFF) / 65536.0 * 0.98 + 0.01;
            return new StatisticalDraw(Z: 3 * (u - 0.5), U: u, SigmaScale: 1);
        }
    }

    /// <summary>A derived variable with no value at some point is an error naming the point — never its nominal value.</summary>
    [Fact]
    public void ADerivedVariableThatFailsAtAPoint_IsAnError_NotItsNominalValue()
    {
        var (meas, errors) = Run("""
            x = 1
            y = log(x)
            Vdc:V1  in 0  Vdc=1
            R:R1    in 0  R=1 kOhm
            analysis DC1 type=dc
            analysis SW1 type=parametric_sweep Var=x Values=1,0 Inner=DC1
            measure y_read = y
            """);

        Assert.False(meas.Contains("y_read"));
        var error = Assert.Single(errors);
        Assert.Contains("depends on a swept variable and has no value at x = 0", error);
    }

    // ── Unchanged: no sweep, or a sweep whose axis the results do not carry ──────────────────────────

    [Fact]
    public void WithNoSweep_ADerivedVariableIsItsScalar()
    {
        var (lib, tb) = new CnlReader().Read("x = 1\ny = 2*x\nmeasure y_read = y\n");
        var meas = new DataSet();
        Assert.Empty(new MeasurementEvaluator(tb, new Elaborator(lib).Elaborate(tb), new Dictionary<string, DataSet>()).EvaluateInto(meas));
        Assert.Equal(0, meas["y_read"].Rank);
        Assert.Equal(2.0, meas["y_read"].RealValues[0]);
    }

    [Fact]
    public void WhenTheResultsCarryNoSweepAxis_ADerivedVariableIsItsNominalScalar()
    {
        var (lib, tb) = new CnlReader().Read("x = 1\ny = 2*x\nmeasure y_read = y\n");
        tb.Analyses.Add(new ParametricSweepAnalysis("SW", "x", [1, 2, 3], "DC1"));
        var results = new Dictionary<string, DataSet> { ["DC1"] = new() };
        results["DC1"].Add("V", DataCube.Scalar(1.0));
        var meas = new DataSet();
        Assert.Empty(new MeasurementEvaluator(tb, new Elaborator(lib).Elaborate(tb), results).EvaluateInto(meas));
        Assert.Equal(0, meas["y_read"].Rank);
        Assert.Equal(2.0, meas["y_read"].RealValues[0]);
    }
}
