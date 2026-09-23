// ================================================================
//  BiasCurveImportTests.cs
//
//  Owner request, 2026-09-23: a capacitance-versus-DC-bias curve from a supplier's .csv export or
//  from pasted columns, into railRF's bias-curve editor and the schematic's NonlinearC editor; and a
//  bias cell that moved under the caret on every digit typed. One test per CLAIM.
//
//  The fixture has the SHAPE of a real supplier export — a '#' preamble naming the part, a
//  "DC Bias[V],Capacitance[F]," header, ~200 rows in base SI with a trailing comma — and an invented
//  part number.
// ================================================================

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class BiasCurveImportTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-biascurve-" + Guid.NewGuid().ToString("N")[..8]);

    public BiasCurveImportTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Part = "CAP470N0402X5R10A";

    /// <summary>A smooth X5R-like fall: 481 nF at 0 V to ~46 % at 10 V, every 50 mV.</summary>
    private static double Curve(double v) => 481e-9 / (1 + Math.Pow(v / 7.0, 2.2));

    private static string SupplierExport()
    {
        var sb = new StringBuilder();
        sb.Append($"#{Part},,\n#In Production,,\n#2026/09/24,,\n#capacitance  25.0degC  AC0.5Vrms,,\n");
        sb.Append("DC Bias[V],Capacitance[F],\n");
        for (int i = 0; i <= 200; i++)
        {
            double v = i * 0.05;
            sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(Curve(v).ToString("R", CultureInfo.InvariantCulture)).Append(",\n");
        }
        return sb.ToString();
    }

    private static (PartLibraryEditorViewModel Vm, PartLibraryRow Row) Editor()
    {
        var row = new PartLibraryRow
        {
            PartNumber = Part, CapacitanceFarads = 470e-9, VoltageRatingV = 10, DielectricClass = "X5R",
        };
        row.BiasCurve.Add(new PartBiasPoint(0, 470e-9));
        row.BiasCurve.Add(new PartBiasPoint(5, 300e-9));
        var library = new PartLibrary { Name = "l" };
        library.Rows.Add(row);
        var vm = new PartLibraryEditorViewModel("/nowhere/l.crlib", library);
        vm.SelectedRow = vm.Rows[0];
        return (vm, row);
    }

    /// <summary>The supplier export replaces the curve in base SI as ONE undoable edit, thinned to
    /// what linear interpolation needs — and nowhere between two kept points does the thinned curve
    /// read further than the stated tolerance from the supplier's own number.</summary>
    [Fact]
    public void ASupplierExport_ReplacesTheCurve_ThinnedWithinTolerance_AsOneUndo()
    {
        var (vm, row) = Editor();
        string path = Path.Combine(_tmp, Part + "_InProduction.csv");
        File.WriteAllText(path, SupplierExport());

        vm.ImportBiasCurve(path);

        Assert.InRange(row.BiasCurve.Count, 5, 40);
        Assert.Equal(0.0, row.BiasCurve[0].BiasVolts);
        Assert.Equal(10.0, row.BiasCurve[^1].BiasVolts, 1e-12);
        for (double v = 0; v <= 10; v += 0.05)
            Assert.InRange(RailDerating.CapacitanceAt(row.BiasCurve, v)!.Value - Curve(v),
                           -PartBiasCurveImport.ThinningTolerance * 481e-9,
                           PartBiasCurveImport.ThinningTolerance * 481e-9);
        Assert.Contains("201 points read", vm.ImportReport[0], StringComparison.Ordinal);
        Assert.Equal(row.BiasCurve.Count, vm.BiasPoints.Count);

        vm.UndoRedo.Undo();
        Assert.Equal(2, vm.Working.Rows[0].BiasCurve.Count);
    }

    /// <summary>Pasted spreadsheet columns read with the unit their header states; the same numbers
    /// with no unit anywhere are refused and the curve is left as it was.</summary>
    [Fact]
    public void PastedColumns_ReadWithTheHeaderUnit_AndBareNumbersAreRefused()
    {
        var (vm, row) = Editor();

        Assert.True(vm.PasteBiasCurve("Bias (V)\tC (uF)\n0\t0.47\n3.3\t0.39\n6.3\t0.30\n"));
        Assert.Equal(3, row.BiasCurve.Count);
        Assert.Equal(0.39e-6, row.BiasCurve[1].CapacitanceFarads, 1e-18);

        Assert.False(vm.PasteBiasCurve("0\t0.47\n5\t0.35\n"));
        Assert.Equal(3, row.BiasCurve.Count);
        Assert.Contains(vm.ImportReport, n => n.Contains("states no capacitance unit", StringComparison.Ordinal));
    }

    /// <summary>A curve a thousand times the marked value is a unit error, and nothing is imported.</summary>
    [Fact]
    public void ACurveAtTheWrongScale_IsRefused()
    {
        var (vm, row) = Editor();

        Assert.False(vm.PasteBiasCurve("V,C (nF)\n0,470000\n5,300000\n"));
        Assert.Equal(300e-9, row.BiasCurve[1].CapacitanceFarads);
        Assert.Contains(vm.ImportReport, n => n.Contains("unit error", StringComparison.Ordinal));
    }

    /// <summary>Field report: every digit typed into a bias moved the cell and reset the selection to
    /// the first point. An edit that re-sorts the curve keeps the SAME point view models and selects
    /// the edited point where the sort put it.</summary>
    [Fact]
    public void ABiasEditThatReorders_KeepsTheEditedPointSelected_AndTheCellsAlive()
    {
        var (vm, row) = Editor();
        var cells = vm.BiasPoints.ToList();

        cells[0].BiasEntry = "8 V";           // 0 V → 8 V: now after the 5 V point

        Assert.Equal(8.0, row.BiasCurve[1].BiasVolts);
        Assert.Equal(1, vm.SelectedBiasPointIndex);
        Assert.Equal(cells, vm.BiasPoints);
    }

    /// <summary>The NonlinearC editor takes the same export: voltages keep their sign (a varactor's
    /// table runs through reverse bias), and the Unit selector follows the file.</summary>
    [Fact]
    public void TheNonlinearCEditor_ImportsATable_InAReadableUnit_KeepingNegativeVoltages()
    {
        var vm = new NonlinearCvEditorViewModel();

        Assert.True(vm.ImportCvTable("V (V),C (pF)\n-4,0.8\n-2,1.1\n0,1.9\n0.5,2.6\n", "varactor.csv"));

        Assert.Equal("pF", vm.CapacitanceUnit);
        Assert.Equal(["-4", "-2", "0", "0.5"], vm.Rows.Select(r => r.StagedV));
        Assert.Equal(["0.8", "1.1", "1.9", "2.6"], vm.Rows.Select(r => r.StagedC));
        Assert.False(vm.HasValidationErrors);
    }
}
