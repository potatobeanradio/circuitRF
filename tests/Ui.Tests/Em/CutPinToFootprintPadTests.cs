// ================================================================
//  CutPinToFootprintPadTests.cs
//
//  The Gysel divider's ground vias are cut-out cells (VIAGND) wired only to a resistor's pad, and the resistor is a footprint part
//  with a component port of its own. That port is built after the cut, so the cut's connectivity check did not see it and reported
//  both via ports as "not connected … reads an open". The check is now handed the footprint parts' pads.
// ================================================================

using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CutPinToFootprintPadTests
{
    [Fact]
    public void ACutViaWiredToAResistorWithAPort_IsConnected()
    {
        string ws  = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Dividers and Couplers");
        string cem = Path.Combine(ws, "Gysel", "em", "Gysel Planar.cem");
        var setup  = EmSetupPersistence.LoadFromFile(cem);
        var source = EmSetupResolver.Resolve(cem, setup.LayoutRef, WorkspaceRootFinder.FindAncestorCws(ws), new TechnologyCache()).Source!;

        var build = EmPlanarComponentPorts.Build(setup, source)!;

        Assert.Contains(build.Ports, p => p.Refdes == "V1" && p.Refusal is null);
        Assert.Contains(build.Ports, p => p.Refdes == "V2" && p.Refusal is null);
        Assert.DoesNotContain(build.Warnings, w => w.Contains("is not connected", System.StringComparison.Ordinal));
    }
}
