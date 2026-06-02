using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Step 1 gate test: AD of the hero GaN FET i2 equation matches central finite-difference
/// to ≥4 significant figures at the bias (v1 = −3.05 V, v2 = 48 V).
/// Golden values: gm ≈ 62.4 mS, gds ≈ −9.45 µS (§5.3 — gds is negative, that is correct).
/// </summary>
public class AdVsFdTests
{
    // Hero GaN HEMT parameters (§5.1)
    private static readonly Dictionary<string, double> HeroParams = new(StringComparer.Ordinal)
    {
        ["Sv"]  = -0.837,
        ["Sc"]  =  0.71,
        ["TV0"] =  4.268,
        ["TC"]  =  1.507,
        ["th"]  =  0.001,
        ["a"]   =  0.176,
        ["g"]   =  0.089,
        ["lam"] =  0.0012,
        ["B"]   =  1130.0,
    };

    // Hero i2 expression (§5.1) — _v1 = vgs, _v2 = vds
    private const string I2Expr =
        "(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*log(exp(-(Sv - _v1)/Sc) + 1))) + 1))" +
        " * log(exp(-(2*TV0 - 2*_v1 + 2*_v2*th + 2*Sc*log(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1)" +
        " * (_v2*lam + 1)) / 2";

    // Hero i1 = vgs / 50
    private const string I1Expr = "_v1 / 50";

    // Bias point
    private const double V1Bias = -3.05;
    private const double V2Bias = 48.0;

    // Golden values (§5.3, verified by hand/MATLAB)
    private const double ExpectedI2   =  49.11e-3;   // ≈49.11 mA
    private const double ExpectedI1   = -61.0e-3;    // = -3.05/50 exact
    private const double ExpectedGm   =  62.4e-3;    // ≈62.4 mS (∂i2/∂v1)
    private const double ExpectedGds  = -9.45e-6;    // ≈-9.45 µS (∂i2/∂v2) — negative!

    [Fact]
    public void I2_Value_MatchesGolden()
    {
        var ast = Parser.Parse(I2Expr);
        double i2 = SddEvaluator.EvalDouble(ast, HeroParams, [V1Bias, V2Bias], "HeroFet");
        // 1% tolerance on value — the gate is on derivatives, value is a sanity check
        Assert.Equal(ExpectedI2, i2, 3);  // 3 decimal places in A ≈ 1 mA resolution
    }

    [Fact]
    public void I1_Value_IsExact()
    {
        var ast = Parser.Parse(I1Expr);
        double i1 = SddEvaluator.EvalDouble(ast, HeroParams, [V1Bias, V2Bias], "HeroFet");
        Assert.Equal(V1Bias / 50.0, i1, 10);  // exact linear relation
        Assert.Equal(ExpectedI1, i1, 6);
    }

    /// <summary>
    /// THE GATE TEST: AD must match FD to ≥4 significant figures for gm and gds.
    /// If this fails, everything downstream is wrong — do not proceed to Step 2.
    /// </summary>
    [Fact]
    public void I2_AdGradient_MatchesFd_To4SigFigs()
    {
        var ast = Parser.Parse(I2Expr);

        // AD evaluation
        (double i2Ad, double[] gradAd) = SddEvaluator.EvalDual(
            ast, HeroParams, [V1Bias, V2Bias], "HeroFet");

        double gmAd  = gradAd[0];  // ∂i2/∂v1 = gm
        double gdsAd = gradAd[1];  // ∂i2/∂v2 = gds

        // FD evaluation
        double[] gradFd = FiniteDiff.Gradient(
            v => SddEvaluator.EvalDouble(ast, HeroParams, v, "HeroFet"),
            [V1Bias, V2Bias],
            relH: 1e-5);

        double gmFd  = gradFd[0];
        double gdsFd = gradFd[1];

        // 4 significant figures: relative error < 5e-4
        const double tol4SigFig = 5e-4;

        double gmRelErr  = Math.Abs((gmAd - gmFd) / gmFd);
        double gdsRelErr = Math.Abs((gdsAd - gdsFd) / gdsFd);

        Assert.True(gmRelErr < tol4SigFig,
            $"gm AD ({gmAd:G6}) vs FD ({gmFd:G6}): relative error {gmRelErr:G3} exceeds 4 sig-fig tolerance {tol4SigFig}");
        Assert.True(gdsRelErr < tol4SigFig,
            $"gds AD ({gdsAd:G6}) vs FD ({gdsFd:G6}): relative error {gdsRelErr:G3} exceeds 4 sig-fig tolerance {tol4SigFig}");

        // Sanity: values are in the right ballpark
        Assert.True(Math.Abs(gmAd - ExpectedGm) / ExpectedGm < 0.01,
            $"gm AD {gmAd:G4} differs from golden {ExpectedGm:G4} by more than 1%");
        Assert.True(Math.Abs(gdsAd - ExpectedGds) / Math.Abs(ExpectedGds) < 0.01,
            $"gds AD {gdsAd:G4} differs from golden {ExpectedGds:G4} by more than 1%");

        // Sign check: gds must be negative (§5.3)
        Assert.True(gdsAd < 0.0,
            $"gds must be negative (§5.3), got gds = {gdsAd:G4}");

        // Report actuals for visibility
        _ = $"i2={i2Ad * 1000:F3} mA  gm={gmAd * 1000:F4} mS  gds={gdsAd * 1e6:F4} µS";
    }

    [Fact]
    public void I1_AdGradient_IsCorrect()
    {
        var ast = Parser.Parse(I1Expr);
        (double _, double[] gradAd) = SddEvaluator.EvalDual(
            ast, HeroParams, [V1Bias, V2Bias], "HeroFet");

        // i1 = v1/50; di1/dv1 = 1/50 = 0.02 S; di1/dv2 = 0
        Assert.Equal(1.0 / 50.0, gradAd[0], 10);
        Assert.Equal(0.0,        gradAd[1], 10);
    }
}
