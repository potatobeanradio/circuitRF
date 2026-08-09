using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// A subcircuit written in the SPICE dialect, whose capacitor's value comes from a
/// <c>.model</c> card and whose geometry comes from the instance — read, elaborated, and SOLVED.
///
/// <para><b>Why this exists on top of the reader's own tests.</b> Those check what was read. This
/// checks what it is worth: a part can be read perfectly and still be a different circuit by the
/// time it reaches the matrix — a parameter name spelled in a case circuitRF compares ordinally, a
/// geometry sealed shut so an override never lands, a series resistance dropped somewhere between
/// the two. Each of those leaves a design that simulates and is wrong, which is the failure mode
/// this whole area is most prone to.</para>
///
/// <para><b>The oracle is the card's own arithmetic, not a stored number.</b> Recovering the value
/// from a solved two-port is what makes the check span the whole chain rather than one layer of it.
/// The fixture is synthetic; the repository commits no third-party kit data.</para>
/// </summary>
public sealed class SpiceModelCardSolveTests
{
    /// <summary>
    /// A capacitor stated the way a process states one: a card carrying an area and a sidewall
    /// coefficient, geometry passed in from outside, and a real series resistance in front of it.
    /// </summary>
    private const string Netlist = """
        .param carea = 1.5E-15
        .subckt part PLUS MINUS
        .param l=7u
        .param w=7u
        .param sf=1E-6
        R1 PLUS 1 r=55m
        C1 1 MINUS plate l=l/sf w=w/sf scale=1
        .ends part
        .model plate C (TC1=3.6E-6 TC2=2E-9 TNOM=27 CJ=carea CJSW=40E-18)
        """;

    private const double Cj = 1.5e-15, Cjsw = 40e-18, Esr = 0.055;

    [Theory]
    [InlineData( 7e-6,  7e-6)]   // the card's own default geometry
    [InlineData(20e-6, 30e-6)]   // and an override, which only lands if '.param' is a declaration
    public void SP1_TheSolvedCapacitanceIsTheCardsOwnArithmetic(double w, double l)
    {
        var read = SpiceNetlistReader.Read(Netlist);
        Assert.Empty(read.IncompleteCells);

        var tb = new TestBench("cap");
        tb.GlobalVariables.AddRange(read.Variables);
        tb.Instances.Add(new Instance("T1", "Term", ["a", "0"], [new ParameterAssignment("Num", "1")]));
        tb.Instances.Add(new Instance("T2", "Term", ["b", "0"], [new ParameterAssignment("Num", "2")]));
        tb.Instances.Add(new Instance("X1", "part", ["a", "b"],
        [
            new ParameterAssignment("w", w.ToString("R")),
            new ParameterAssignment("l", l.ToString("R")),
        ]));

        var netlist = new Elaborator(read.Library).Elaborate(tb);

        const double f = 1e9, z0 = 50.0;
        var snp = DataSetBuilder.ToSnp(SParameterEngine.Run(netlist, [f]));

        // The DUT is the only thing between the two ports, so its series impedance follows from S21.
        Complex s21 = snp.Matrices[0][1, 0];
        Complex z   = 2.0 * z0 * (1.0 / s21 - 1.0);

        // The card's own arithmetic, in the units the file itself pairs up: the instance divides its
        // metres by sf to hand the card microns, so the coefficients are per micron and per square
        // micron. Nothing rescales either side.
        double wUm = w / 1e-6, lUm = l / 1e-6;
        double expected = Cj * wUm * lUm + Cjsw * 2.0 * (wUm + lUm);

        double solved = -1.0 / (2 * Math.PI * f * z.Imaginary);

        Assert.Equal(expected, solved, expected * 1e-6);

        // And the series resistance the subcircuit puts in front of it is still there. A capacitor
        // whose ESR quietly went missing has an infinite Q and plots beautifully.
        Assert.Equal(Esr, z.Real, 1e-6);
    }

    [Fact]
    public void SP2_TheGeometryOverrideIsWhatMovesTheValue()
    {
        // Guards the pair above against passing for the wrong reason: if the override never landed,
        // both rows would report the card's default geometry and agree with an expectation computed
        // from it. They must genuinely differ.
        static double Solve(double w, double l)
        {
            var read = SpiceNetlistReader.Read(Netlist);
            var tb   = new TestBench("cap");
            tb.GlobalVariables.AddRange(read.Variables);
            tb.Instances.Add(new Instance("T1", "Term", ["a", "0"], [new ParameterAssignment("Num", "1")]));
            tb.Instances.Add(new Instance("T2", "Term", ["b", "0"], [new ParameterAssignment("Num", "2")]));
            tb.Instances.Add(new Instance("X1", "part", ["a", "b"],
            [
                new ParameterAssignment("w", w.ToString("R")),
                new ParameterAssignment("l", l.ToString("R")),
            ]));

            var snp = DataSetBuilder.ToSnp(
                SParameterEngine.Run(new Elaborator(read.Library).Elaborate(tb), [1e9]));
            var z = 2.0 * 50.0 * (1.0 / snp.Matrices[0][1, 0] - 1.0);
            return -1.0 / (2 * Math.PI * 1e9 * z.Imaginary);
        }

        Assert.True(Solve(20e-6, 30e-6) > 10 * Solve(7e-6, 7e-6));
    }
}
