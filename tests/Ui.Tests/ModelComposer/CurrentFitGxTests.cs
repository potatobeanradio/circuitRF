using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>Issue 67 §2: a G_x taken from a conductance that overflowed is refused at the fit, naming the branch and the
/// point, instead of reaching the record as +∞ and failing only when the record is written.</summary>
public sealed class CurrentFitGxTests
{
    /// <summary>u = a·vgs: a transformed payload whose asinh output's slope, s·cosh(u)·a, overflows at large vgs.</summary>
    private sealed class Linear(double a) : RuntimeEvaluator
    {
        public override int InputCount => 2;
        public override double Evaluate(ReadOnlySpan<double> x, Span<double> grad, ReadOnlySpan<double> w, Span<double> hw)
        {
            grad[0] = a; grad[1] = 0;
            if (!w.IsEmpty) { hw[0] = 0; hw[1] = 0; }
            return a * x[0];
        }
    }

    [Fact]
    public void AnOverflowingConductance_IsRefused_NamingTheBranchAndThePoint()
    {
        var f = new AsinhOutput(new Linear(1000), 1e-3);
        double[][] points = [[-0.1, 5.0], [0.0, 5.0], [1.0, 5.0]];   // u = 1000 at vgs = 1: cosh overflows

        var ex = Assert.Throws<ModelingException>(() => CurrentFit.Gx(f, points, [1, 0], "IgGd", ["vgs", "vds"]));
        Assert.Equal("IgGd", ex.Input);
        Assert.Contains("vgs = 1, vds = 5", ex.Message);

        double gx = CurrentFit.Gx(f, points[..2], [1, 0], "IgGd", ["vgs", "vds"]);
        Assert.True(double.IsFinite(gx) && gx > 0);
    }
}
