using System.Globalization;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the NonlinearC CV data editor (briefs #4 + #5).
/// All tests are VM-level and headless (no Avalonia runtime needed).
/// </summary>
public class NonlinearCvEditorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (SchematicViewModel schVm, EditableComponent comp) MakeNonlinearC()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { Symbol = SymbolKind.NonlinearC, InstanceName = "C1" };
        comp.Parameters.Add(new EditableParameter { Name = "C0", Expression = "0", Unit = "F" });
        model.Components.Add(comp);
        return (new SchematicViewModel(model), comp);
    }

    private static CvRowViewModel Row(string v, string c, NonlinearCvEditorViewModel owner)
        => new CvRowViewModel(v, c, owner);

    // ── Test 1: Apply writes coefficients + round-trips CvData ───────────────
    // Uses unit="None" so entered C values are treated as SI (backward compat).

    [Fact]
    public void Apply_WritesCoeffsAndCvData_RoundTrips()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);
        vm.CapacitanceUnit = "None"; // scale=1 → entered values are SI

        double[] vs = [-1.0, -0.5, 0.0, 0.5, 1.0];
        double[] cs = [1.0e-12, 1.2e-12, 1.5e-12, 1.9e-12, 2.4e-12];

        vm.Rows.Clear();
        for (int i = 0; i < vs.Length; i++)
            vm.Rows.Add(Row(
                vs[i].ToString("G15", CultureInfo.InvariantCulture),
                cs[i].ToString("G15", CultureInfo.InvariantCulture),
                vm));
        vm.FitOrder = 3;

        vm.ApplyCommand.Execute(null);

        // C0..C3 must match PolynomialFit.Fit(vs, cs, 3).
        double[] expected = PolynomialFit.Fit(vs, cs, 3);
        for (int k = 0; k <= 3; k++)
        {
            var p = comp.Parameters.FirstOrDefault(p => p.Name == $"C{k}");
            Assert.NotNull(p);
            double actual = double.Parse(p.Expression, CultureInfo.InvariantCulture);
            double tol = Math.Abs(expected[k]) * 1e-13 + 1e-30;
            Assert.InRange(actual, expected[k] - tol, expected[k] + tol);
        }

        // CvData must be present and round-trip the table + order.
        var cvData = comp.Parameters.FirstOrDefault(p => p.Name == "CvData");
        Assert.NotNull(cvData);
        Assert.True(cvData.Expression.Length >= 2 && cvData.Expression[0] == '"');
        string raw = cvData.Expression[1..^1];
        var (pts, order) = NonlinearCvEditorViewModel.ParseCvData(raw);
        Assert.Equal(3, order);
        Assert.Equal(5, pts.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(vs[i], pts[i].V, precision: 6);
            Assert.Equal(cs[i], pts[i].C, precision: 15);
        }
    }

    // ── Test 2: Close discards (does NOT apply) ───────────────────────────────

    [Fact]
    public void Close_Discards_ComponentUnchanged()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);

        vm.Rows.Clear();
        vm.Rows.Add(Row("0", "9.99e-12", vm));
        vm.Rows.Add(Row("1", "9.99e-12", vm));

        Assert.Equal("0", comp.Parameters.First(p => p.Name == "C0").Expression);
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "CvData");
    }

    // ── Test 3: Validation gates Apply when too few points ────────────────────

    [Fact]
    public void Validation_GatesApply_TooFewPoints()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);

        vm.Rows.Clear();
        vm.Rows.Add(Row("-1", "1e-12",   vm));
        vm.Rows.Add(Row("0",  "1.5e-12", vm));
        vm.FitOrder = 3; // needs 4 points; only 2 provided

        vm.Validate();
        vm.ApplyCommand.Execute(null);

        Assert.Equal("0", comp.Parameters.First(p => p.Name == "C0").Expression);
        Assert.True(vm.HasValidationErrors);
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "CvData");
    }

    // ── Test 4: Undo restores pre-Apply coefficients ──────────────────────────

    [Fact]
    public void Undo_RestoresPreApplyCoefficients()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);
        vm.CapacitanceUnit = "None";

        double[] vs = [-1.0, 0.0, 1.0, 2.0];
        double[] cs = [1e-12, 1.5e-12, 2e-12, 3e-12];

        vm.Rows.Clear();
        for (int i = 0; i < vs.Length; i++)
            vm.Rows.Add(Row(
                vs[i].ToString("G15", CultureInfo.InvariantCulture),
                cs[i].ToString("G15", CultureInfo.InvariantCulture),
                vm));
        vm.FitOrder = 2;

        string originalC0 = comp.Parameters.First(p => p.Name == "C0").Expression;
        vm.ApplyCommand.Execute(null);

        string afterApplyC0 = comp.Parameters.First(p => p.Name == "C0").Expression;
        Assert.NotEqual(originalC0, afterApplyC0);

        schVm.UndoRedo.Undo();

        Assert.Equal(originalC0, comp.Parameters.First(p => p.Name == "C0").Expression);
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "CvData");
    }

    // ── Test 5: pF unit → same SI coefficients as direct SI fit ─────────────

    [Fact]
    public void Apply_PfUnit_ProducesSICoefficients()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);
        vm.CapacitanceUnit = "pF";

        double[] vs    = [-1.0, -0.5, 0.0, 0.5, 1.0];
        double[] cs_pF = [1.0,   1.2,  1.5,  1.9,  2.4];  // in pF
        double[] cs_SI = cs_pF.Select(c => c * 1e-12).ToArray();

        vm.Rows.Clear();
        for (int i = 0; i < vs.Length; i++)
            vm.Rows.Add(Row(
                vs[i].ToString("G15", CultureInfo.InvariantCulture),
                cs_pF[i].ToString("G15", CultureInfo.InvariantCulture),
                vm));
        vm.FitOrder = 3;

        vm.ApplyCommand.Execute(null);

        double[] expected = PolynomialFit.Fit(vs, cs_SI, 3);
        for (int k = 0; k <= 3; k++)
        {
            var p = comp.Parameters.FirstOrDefault(p => p.Name == $"C{k}");
            Assert.NotNull(p);
            double actual = double.Parse(p.Expression, CultureInfo.InvariantCulture);
            double tol = Math.Abs(expected[k]) * 1e-10 + 1e-30;
            Assert.InRange(actual, expected[k] - tol, expected[k] + tol);
        }
    }

    // ── Test 6: nF unit rescales coefficients relative to pF ────────────────

    [Fact]
    public void Apply_NfUnit_RescalesCoefficients()
    {
        double[] vs    = [-1.0, 0.0, 1.0, 2.0];
        double[] cs_nF = [0.001, 0.0015, 0.002, 0.003]; // 1 pF, 1.5 pF, 2 pF, 3 pF in nF
        double[] cs_pF = cs_nF.Select(c => c * 1e3).ToArray();

        // Build expected from pF values * 1e-12
        double[] cs_SI    = cs_pF.Select(c => c * 1e-12).ToArray();
        double[] expected = PolynomialFit.Fit(vs, cs_SI, 2);

        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);
        vm.CapacitanceUnit = "nF";

        vm.Rows.Clear();
        for (int i = 0; i < vs.Length; i++)
            vm.Rows.Add(Row(
                vs[i].ToString("G15", CultureInfo.InvariantCulture),
                cs_nF[i].ToString("G15", CultureInfo.InvariantCulture),
                vm));
        vm.FitOrder = 2;

        vm.ApplyCommand.Execute(null);

        for (int k = 0; k <= 2; k++)
        {
            var p = comp.Parameters.FirstOrDefault(p => p.Name == $"C{k}");
            Assert.NotNull(p);
            double actual = double.Parse(p.Expression, CultureInfo.InvariantCulture);
            double tol = Math.Abs(expected[k]) * 1e-10 + 1e-30;
            Assert.InRange(actual, expected[k] - tol, expected[k] + tol);
        }
    }

    // ── Test 7: Text parse — comments, blank lines, and tab delimiter ─────────

    [Fact]
    public void TextParse_TabDelimitedWithCommentsAndBlanks_ParsesCorrectly()
    {
        string text = """
            // voltage  capacitance
            -1.0\t1.5

            0.0\t2.0  ; midpoint
            1.0\t3.0
            """.Replace("\\t", "\t");

        var (pts, errors) = NonlinearCvEditorViewModel.ParseTextContent(text);

        Assert.Empty(errors);
        Assert.Equal(3, pts.Count);
        Assert.Equal(-1.0, pts[0].V, precision: 10);
        Assert.Equal(1.5,  pts[0].C, precision: 10);
        Assert.Equal(0.0,  pts[1].V, precision: 10);
        Assert.Equal(2.0,  pts[1].C, precision: 10);
        Assert.Equal(1.0,  pts[2].V, precision: 10);
        Assert.Equal(3.0,  pts[2].C, precision: 10);
    }

    // ── Test 8: Text parse — malformed line flags validation error ────────────

    [Fact]
    public void TextParse_MalformedLine_FlagsValidationError()
    {
        string text = "-1.0\t1.5\nbad line here\n1.0\t3.0";

        var (pts, errors) = NonlinearCvEditorViewModel.ParseTextContent(text);

        Assert.Single(errors);
        Assert.Contains("Line 2", errors[0]);
        Assert.Equal(2, pts.Count); // the two valid lines still parse
    }

    // ── Test 9: Preview points — only for ≥2 valid distinct-V rows ───────────

    [Fact]
    public void PreviewPoints_RequiresTwoDistinctVPoints()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);

        // Start with 2 empty rows → no preview
        vm.Validate();
        Assert.Null(vm.PreviewPoints);

        // One valid row → still no preview
        vm.Rows.Clear();
        vm.Rows.Add(Row("0.0", "1.0", vm));
        vm.Rows.Add(Row("", "", vm));
        vm.Validate();
        Assert.Null(vm.PreviewPoints);

        // Two valid, distinct-V rows → preview with 2 points
        vm.Rows.Clear();
        vm.Rows.Add(Row("-1.0", "1.5", vm));
        vm.Rows.Add(Row("1.0",  "3.0", vm));
        vm.Validate();
        Assert.NotNull(vm.PreviewPoints);
        Assert.Equal(2, vm.PreviewPoints!.Count);
    }

    // ── Test 10: Text mode → Apply round-trips (whitespace fallback) ──────────

    [Fact]
    public void TextMode_Apply_ParsesWhitespaceSeparated()
    {
        var (schVm, comp) = MakeNonlinearC();
        var vm = new NonlinearCvEditorViewModel();
        vm.SetTarget(schVm, comp);
        vm.CapacitanceUnit = "None";

        // Set text mode with whitespace-separated data (no tab)
        vm.SetTextModeCommand.Execute(null);
        vm.TextContent = "-1.0  1.0e-12\n0.0  1.5e-12\n1.0  2.0e-12\n2.0  3.0e-12";
        vm.FitOrder    = 2;

        vm.Validate();
        Assert.False(vm.HasValidationErrors);

        vm.ApplyCommand.Execute(null);

        // C0 should now be written (fit result)
        var c0 = comp.Parameters.FirstOrDefault(p => p.Name == "C0");
        Assert.NotNull(c0);
        Assert.NotEqual("0", c0.Expression);
    }
}
