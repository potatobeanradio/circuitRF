// ================================================================
//  NumberFormatTableTests.cs — Table cube cells honor the Number Format
//
//  A complex DataCube slice shown on a Table (no scalar transform) must render
//  in the trace's MatrixFormat (MA / RI / DB) set via the right-click "Number
//  Format" menu — previously it was hard-coded to MA.
// ================================================================

using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class NumberFormatTableTests
{
    private static Trace MakeComplexCubeTrace()
    {
        var snp = new SNP(new double[] { 1e9 }, 2);
        var t   = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = "V",
            Transform = CubeTransform.None,   // no scalar transform → complex value shown
        };
        // Single complex sample 3 + 4j (mag 5, angle 53.13°, 20·log10(5) ≈ 13.98 dB).
        t.SetCubeData(new double[] { 1e9 }, new Complex[] { new(3, 4) }, realValues: null,
            xAxisName: "freq", xUnit: "Hz", PlotType.Table, FreqUnit.GHz);
        return t;
    }

    private static string Cell(Trace t) => t.FormatCubeCell(0, t.FormatString, t.MaximumFractionDigits);

    [Fact]
    public void TableCubeCell_HonorsMatrixFormat_MA_RI_DB()
    {
        var t = MakeComplexCubeTrace();

        t.MatrixFormat = MatrixFormat.MA;
        var ma = Cell(t);
        Assert.Contains("∠", ma);
        Assert.StartsWith("5", ma);              // magnitude

        t.MatrixFormat = MatrixFormat.RI;
        var ri = Cell(t);
        Assert.Contains("j", ri);                // Real ± jImag
        Assert.DoesNotContain("∠", ri);
        Assert.StartsWith("3", ri);              // real part

        t.MatrixFormat = MatrixFormat.DB;
        var db = Cell(t);
        Assert.Contains("∠", db);
        Assert.StartsWith("13", db);             // 20·log10(5) ≈ 13.98 dB (not the magnitude 5)
    }

    // Family (multi-curve) traces use FormatFamilyCell — it must honor MatrixFormat the same way.
    [Fact]
    public void TableFamilyCell_HonorsMatrixFormat_MA_RI_DB()
    {
        var snp = new SNP(new double[] { 1e9 }, 2);
        var t   = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = "V",
            Transform = CubeTransform.None,
        };
        var curves = new List<(double, string?, Complex[]?, double[]?)>
        {
            (0.0, "n0", new Complex[] { new(3, 4) }, null),   // curve 0, sample 0 = 3+4j
        };
        t.SetFamilyData(new double[] { 1e9 }, "freq", "Hz", "node", curves, PlotType.Table, FreqUnit.GHz);

        string FamilyCell() => t.FormatFamilyCell(0, 0, t.FormatString, t.MaximumFractionDigits);

        t.MatrixFormat = MatrixFormat.MA;
        var ma = FamilyCell();
        Assert.Contains("∠", ma);
        Assert.StartsWith("5", ma);

        t.MatrixFormat = MatrixFormat.RI;
        var ri = FamilyCell();
        Assert.Contains("j", ri);
        Assert.DoesNotContain("∠", ri);
        Assert.StartsWith("3", ri);

        t.MatrixFormat = MatrixFormat.DB;
        var db = FamilyCell();
        Assert.Contains("∠", db);
        Assert.StartsWith("13", db);
    }
}
