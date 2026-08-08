namespace CircuitRF.WBond.Tests;

/// <summary>
/// Tier 9 of brief-wbond-wbb §4 — the coupling audit (WB30 / WB30a).
///
/// <para><b>In v1 this is the entire safety mechanism</b> for coupling between separate wBond
/// components, because <c>CouplingDomain</c> is v2. So it gates here rather than later, and the
/// message must name the manual remedy — it is the only one available.</para>
/// </summary>
public class CouplingAuditTests
{
    private static WBondDesign Array(double yOffsetMil, int wires = 4, double heightMil = 20.0)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };

        for (int i = 0; i < wires; i++)
        {
            double y = yOffsetMil + i * 6.0;
            array.Wires.Add(new Wire
            {
                Points = { Point3.Mils(0, y, heightMil), Point3.Mils(100, y, heightMil) },
                DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil),
            });
        }

        design.Arrays.Add(array);
        return design;
    }

    /// <summary>Two adjacent wBonds are reported, and the message names the remedy.</summary>
    [Fact]
    public void Tier9_AdjacentWBonds_AreReportedWithTheRemedy()
    {
        var findings = CouplingAudit.Audit([("WB1", Array(0)), ("WB2", Array(30))]);

        var finding = Assert.Single(findings);
        Assert.Equal("WB1", finding.InstanceA);
        Assert.Equal("WB2", finding.InstanceB);
        Assert.True(finding.EstimatedK > CouplingAudit.DefaultThreshold);

        Assert.Contains("NOT modelled", finding.Message, StringComparison.Ordinal);
        Assert.Contains("single wBond", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WB1", finding.Message, StringComparison.Ordinal);
        Assert.Contains("WB2", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two wBonds far apart are NOT reported. Without this the audit would fire on every design that
    /// legitimately has input-side and output-side bonds, and become noise nobody reads.
    /// </summary>
    [Fact]
    public void Tier9_DistantWBonds_AreNotReported()
    {
        var findings = CouplingAudit.Audit([("WB1", Array(0)), ("WB2", Array(20_000))]);
        Assert.Empty(findings);
    }

    /// <summary>A single wBond has nothing to be coupled to.</summary>
    [Fact]
    public void Tier9_ASingleWBond_ProducesNoFindings()
    {
        Assert.Empty(CouplingAudit.Audit([("WB1", Array(0))]));
    }

    /// <summary>
    /// The estimate follows the physics: coupling falls with separation and RISES with height, because
    /// a taller loop couples further. A bare distance threshold would miss the second half.
    /// </summary>
    [Fact]
    public void Tier9_CouplingFallsWithSeparationAndRisesWithHeight()
    {
        const double radius = 12.7e-6;
        double h = WBondUnits.ToMetres(WBondUnits.ToNm(20.0, WBondUnit.Mil));

        double previous = double.MaxValue;
        foreach (double separationMil in new[] { 10.0, 30.0, 100.0, 300.0 })
        {
            double d = WBondUnits.ToMetres(WBondUnits.ToNm(separationMil, WBondUnit.Mil));
            double k = CouplingAudit.EstimateCoupling(d, h, radius);
            Assert.True(k < previous, $"Coupling must fall with separation; at {separationMil} mil it was {k:P2}.");
            previous = k;
        }

        double dFixed = WBondUnits.ToMetres(WBondUnits.ToNm(60.0, WBondUnit.Mil));
        double low = CouplingAudit.EstimateCoupling(dFixed, h, radius);
        double high = CouplingAudit.EstimateCoupling(dFixed, h * 3.0, radius);

        Assert.True(high > low,
            $"A taller loop couples further at the same separation: {low:P2} at h vs {high:P2} at 3h.");
    }

    /// <summary>
    /// Findings are ordered worst-first, so acting on the first one acts on what matters.
    ///
    /// <para>The offsets are chosen so all three pairs clear the 1 % threshold with clearly different
    /// coupling — at 200 mil the estimate is ~0.5 % and correctly produces no finding at all, which
    /// is the audit working rather than failing.</para>
    /// </summary>
    [Fact]
    public void Tier9_FindingsAreOrderedWorstFirst()
    {
        var findings = CouplingAudit.Audit(
            [("A", Array(0)), ("B", Array(120)), ("C", Array(30))]);

        Assert.True(findings.Count >= 2,
            $"Expected several findings from three mutually-near arrays; got {findings.Count}.");
        for (int i = 1; i < findings.Count; i++)
            Assert.True(findings[i - 1].EstimatedK >= findings[i].EstimatedK);
    }

    /// <summary>
    /// Crossing wires are at zero lateral separation — the endpoint tests alone would report them as
    /// far apart, which is the one geometry an audit must not miss.
    /// </summary>
    [Fact]
    public void Tier9_CrossingWires_AreDetectedAsTouching()
    {
        var across = new WBondDesign();
        across.Arrays.Add(new WireArray
        {
            Name = "X",
            Wires = { new Wire { Points = { Point3.Mils(50, -50, 20), Point3.Mils(50, 50, 20) } } },
        });

        var findings = CouplingAudit.Audit([("WB1", Array(0, wires: 1)), ("WB2", across)]);

        var finding = Assert.Single(findings);
        Assert.Equal(0.0, finding.ClosestApproachMetres, 1e-12);
    }
}
