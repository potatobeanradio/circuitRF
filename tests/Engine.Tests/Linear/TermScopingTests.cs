using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Gates for Brief H — Term scoping and engine-stamping refinement.
///
/// Gate 1: Term/Port is inert (open) in DC analysis — the port node is neither shorted
///         nor loaded. S-parameter analysis is unchanged.
/// Gate 2: Only top-level Terms become S-param ports; a Term buried inside an
///         instantiated sub-cell is inert, doesn't add a port, and emits a warning.
/// Gate 3: Linter — Terms in cells, stray top-level Pins, duplicate/missing Num.
/// </summary>
public class TermScopingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (CircuitRF.Core.Elaboration.ElaboratedNetlist nl, CircuitRF.Engine.NonlinearDcEngine.DcResult dc)
        RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl     = new Elaborator(lib).Elaborate(tb);
        var result = NonlinearDcEngine.Run(nl);
        return (nl, result);
    }

    private static Complex Sij(RfCore.Data.DataSet ds, int r, int c, int fi = 0) =>
        (Complex)ds["S"][fi, r, c];

    // ── Gate 1a: Term is inert in DC ─────────────────────────────────────────

    /// <summary>
    /// Circuit: V1=5V → R1=100Ω → n2; Port:P1 at n2; R2=200Ω from n2 to ground.
    /// If Port stamps a 0V branch (old behaviour), n2 is shorted → V(n2) = 0.
    /// If Port is inert (correct behaviour), voltage divider → V(n2) = 5·200/300 = 10/3.
    /// </summary>
    [Fact]
    public void DcAnalysis_TermIsInert_PortNodeNotShorted()
    {
        var (nl, dc) = RunDc(@"
V:V1   n1 0   V=5
R:R1   n1 n2  R=100 Ohm
Port:P1  n2 0  Num=1 Z=50 Ohm
R:R2   n2 0   R=200 Ohm
");
        Assert.True(dc.Converged, "DC solver did not converge");

        int nodeIdx = nl.Nodes.IndexOf("n2");
        double v2 = dc.NodeVoltages[nodeIdx - 1];

        // Voltage divider: V(n2) = 5 × 200 / (100 + 200) = 10/3 ≈ 3.333 V
        Assert.True(Math.Abs(v2 - 10.0 / 3) < 1e-6,
            $"V(n2) = {v2:G6}, expected ≈ 3.333 V; Term must be inert (open) in DC, not a short.");
    }

    /// <summary>
    /// Same circuit with a Term: component (not Port:) — also must be inert in DC.
    /// </summary>
    [Fact]
    public void DcAnalysis_TermComponentIsInert_PortNodeNotShorted()
    {
        var (nl, dc) = RunDc(@"
V:V1   n1 0   V=5
R:R1   n1 n2  R=100 Ohm
Term:T1  n2 0  Num=1 Z=50 Ohm
R:R2   n2 0   R=200 Ohm
");
        Assert.True(dc.Converged, "DC solver did not converge");

        int nodeIdx = nl.Nodes.IndexOf("n2");
        double v2 = dc.NodeVoltages[nodeIdx - 1];

        Assert.True(Math.Abs(v2 - 10.0 / 3) < 1e-6,
            $"V(n2) = {v2:G6}, expected ≈ 3.333 V; TermModel must be inert in DC.");
    }

    // ── Gate 1b: S-param path is unchanged for top-level Terms ────────────────

    [Fact]
    public void SParam_TopLevelTerm_UnchangedByRefactor()
    {
        // Matched 1-port: S11 = 0.  Regression against existing behaviour.
        var (lib, tb) = new CnlReader().Read(@"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 0  R=50 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9]);

        var s11 = Sij(ds, 0, 0);
        Assert.True(s11.Magnitude < 1e-8, $"S11={s11:G4}, expected ≈ 0 (matched load)");
    }

    // ── Gate 2: Buried Terms are inert and warned ─────────────────────────────

    /// <summary>
    /// Sub-cell "Amp" has an internal Port:BuriedTerm that should be ignored.
    /// Top testbench has Port:P1 (Num=1) only.
    /// Expected: 1-port S-matrix; a warning naming the buried Term.
    /// </summary>
    [Fact]
    public void SParam_BuriedTerm_IsInert_OnlyTopLevelPortCounted()
    {
        var cnl = @"
define Amp (p q)
  R:R1  p q  R=50 Ohm
  Port:BuriedTerm  p 0  Num=2 Z=50 Ohm
end

Port:P1  n1 0  Num=1 Z=50 Ohm
Amp:X1  n1 n2
R:RLoad  n2 0  R=50 Ohm
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        // Warning must mention the buried Term path.
        Assert.Contains(nl.Warnings,
            w => w.Contains("BuriedTerm", StringComparison.OrdinalIgnoreCase)
              || w.Contains("instantiated cell", StringComparison.OrdinalIgnoreCase));

        // S-param analysis should see exactly 1 port (P1), not 2.
        var ds = SParameterEngine.Run(nl, [1e9]);
        int portCount = ds["S"].Axes[1].Length;
        Assert.Equal(1, portCount);
    }

    /// <summary>
    /// Buried Term must not perturb the S-parameter result.
    /// Compare: top-level Port:P1 + sub-cell with BuriedTerm versus the same
    /// circuit without the buried Term at all — S11 must be identical.
    /// </summary>
    [Fact]
    public void SParam_BuriedTerm_DoesNotPerturbResult()
    {
        const string flatCnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
R:R1  n1 n2  R=50 Ohm
R:RLoad  n2 0  R=50 Ohm
";
        const string hierarchicalCnl = @"
define Amp (p q)
  R:R1  p q  R=50 Ohm
  Port:BuriedTerm  p 0  Num=2 Z=50 Ohm
end

Port:P1  n1 0  Num=1 Z=50 Ohm
Amp:X1  n1 n2
R:RLoad  n2 0  R=50 Ohm
";
        var (lib1, tb1) = new CnlReader().Read(flatCnl);
        var nl1 = new Elaborator(lib1).Elaborate(tb1);
        var ds1 = SParameterEngine.Run(nl1, [1e9]);

        var (lib2, tb2) = new CnlReader().Read(hierarchicalCnl);
        var nl2 = new Elaborator(lib2).Elaborate(tb2);
        var ds2 = SParameterEngine.Run(nl2, [1e9]);

        double diff = (Sij(ds1, 0, 0) - Sij(ds2, 0, 0)).Magnitude;
        Assert.True(diff < 1e-10,
            $"S11 with buried Term ({Sij(ds2,0,0):G4}) ≠ S11 without ({Sij(ds1,0,0):G4}); " +
            $"buried Term must not perturb results.");
    }

    // ── Gate 3: Linter ────────────────────────────────────────────────────────

    [Fact]
    public void Linter_TermInSubCell_EmitsWarning()
    {
        var cnl = @"
define DUT (n1 n2)
  Port:TermInCell  n1 0  Num=1 Z=50 Ohm
  R:R1  n1 n2  R=50 Ohm
end

Port:P1  p1 0  Num=1 Z=50 Ohm
DUT:X1  p1 p2
R:Rload  p2 0  R=50 Ohm
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("TermInCell", StringComparison.OrdinalIgnoreCase)
              || w.Contains("instantiated cell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linter_TopLevelPin_EmitsWarning()
    {
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Pin:Pin1  n1  Num=1
R:R1  n1 0  R=50 Ohm
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("Pin1", StringComparison.OrdinalIgnoreCase)
              || w.Contains("top", StringComparison.OrdinalIgnoreCase)
              || w.Contains("testbench", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Linter_DuplicateNum_EmitsWarning()
    {
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=1 Z=50 Ohm
R:R1  n1 n2  R=50 Ohm
analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)
              || w.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
              || (w.Contains("Num=1") && w.Contains("P1") && w.Contains("P2")));
    }

    [Fact]
    public void Linter_GapInNumSequence_EmitsWarning()
    {
        // Ports Num=1 and Num=3 — missing Num=2.
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P3  n3 0  Num=3 Z=50 Ohm
R:R13  n1 n3  R=50 Ohm
analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Contains(nl.Warnings,
            w => w.Contains("Num=2") || w.Contains("missing"));
    }

    [Fact]
    public void Linter_CleanTestbench_NoWarnings()
    {
        // A well-formed 2-port testbench (with the S-param analysis that activates the lint) should
        // produce no warnings.
        var cnl = @"
Port:P1  n1 0  Num=1 Z=50 Ohm
Port:P2  n2 0  Num=2 Z=50 Ohm
R:R1  n1 n2  R=50 Ohm
analysis SP type=sparam start=1 GHz stop=3 GHz step=1 GHz
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        Assert.Empty(nl.Warnings);
    }
}
