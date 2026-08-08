using CircuitRF.Core.Elaboration;
using CircuitRF.WBond;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Runs <see cref="CouplingAudit"/> over an elaborated netlist (R-wbb-7, WB30 / WB30a).
///
/// <para><b>An audit nothing calls is indistinguishable from an audit that finds nothing.</b> With
/// <c>CouplingDomain</c> deferred to v2, this is the only thing standing between a user and a
/// silently-unmodelled coupling, so it is wired into the run rather than left as a library anyone
/// could forget to call.</para>
///
/// <para>It <b>reports and never refuses</b> — two wBonds that genuinely do not interact are a
/// legitimate design, and forcing them into one component would make one large matrix where two
/// small ones are faster and no less accurate.</para>
/// </summary>
public static class WBondCouplingAudit
{
    /// <summary>
    /// Finds every pair of wBond instances in <paramref name="netlist"/> whose wires are close enough
    /// for their unmodelled mutual coupling to matter.
    /// </summary>
    public static IReadOnlyList<CouplingAudit.Finding> Audit(
        ElaboratedNetlist netlist, double threshold = CouplingAudit.DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(netlist);

        var instances = new List<(string Name, WBondDesign Design)>();
        foreach (var component in netlist.Components)
        {
            if (component.Model is WBondModel wbond)
                instances.Add((component.InstancePath, wbond.Design));
        }

        return instances.Count < 2
            ? []
            : CouplingAudit.Audit(instances, threshold);
    }

    /// <summary>
    /// Runs the audit and adds each finding to the netlist's warnings, so it reaches the user through
    /// the same channel every other run-time diagnostic uses.
    /// </summary>
    public static int AuditAndWarn(ElaboratedNetlist netlist, double threshold = CouplingAudit.DefaultThreshold)
    {
        var findings = Audit(netlist, threshold);
        foreach (var finding in findings)
            netlist.AddWarningOnce($"wbond-coupling:{finding.InstanceA}|{finding.InstanceB}", finding.Message);
        return findings.Count;
    }
}
