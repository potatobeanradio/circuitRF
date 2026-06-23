using System.IO;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Regression for the .gam picker path base (Loadpull UI 06 follow-up bug): the LP Grid and LPP
/// OutputGrid pickers must store the path relative to the WORKSPACE ROOT — the engine's resolution
/// base (netlist.cnl is written there) — NOT the schematic directory. Storing relative to the
/// schematic dir produced a wrong absolute path at run time (resolved against the wrong base →
/// "Could not find a part of the path …").
/// </summary>
public sealed class LpGridPathPolicyTests
{
    // Cell-homed layout: schematic dir is two levels below the workspace root.
    private static readonly string WsRoot       = Path.Combine(Path.GetTempPath(), "crf_ws");
    private static readonly string SchematicDir = Path.Combine(WsRoot, "MyCell", "schematic");
    private static readonly string PickedAbs    = Path.Combine(WsRoot, "results", "lpp_test.gam");

    private static SchematicEditModel Model()
        => new() { SchematicDirectory = SchematicDir };

    // ── LPP OutputGrid: stored relative to the workspace root, resolves back ──

    [Fact]
    public void Lpp_OutputGrid_StoredRelativeToWorkspaceRoot()
    {
        var vm = new LppBodyViewModel(Model(), workspaceRoot: WsRoot);
        vm.ApplyPickedOutputGridPath(PickedAbs);

        Assert.Equal("results/lpp_test.gam", vm.OutputGridPath);
        Assert.DoesNotContain("..", vm.OutputGridPath);   // bug signature was a "../" path

        // The engine resolves the stored relative path against the workspace root → original abs.
        string resolved = Path.GetFullPath(Path.Combine(WsRoot, vm.OutputGridPath));
        Assert.Equal(Path.GetFullPath(PickedAbs), resolved);
    }

    // ── LP Grid: same bug class, same fix ─────────────────────────────────────

    [Fact]
    public void Lp_Grid_StoredRelativeToWorkspaceRoot()
    {
        var vm = new LpBodyViewModel(Model(), workspaceRoot: WsRoot);
        vm.ApplyPickedGridPath(PickedAbs);

        Assert.Equal("results/lpp_test.gam", vm.GridPath);
        Assert.DoesNotContain("..", vm.GridPath);

        string resolved = Path.GetFullPath(Path.Combine(WsRoot, vm.GridPath));
        Assert.Equal(Path.GetFullPath(PickedAbs), resolved);
    }

    // ── No workspace root (scratch): absolute path kept (no relativization) ───

    [Fact]
    public void Lpp_NoWorkspaceRoot_KeepsAbsolutePath()
    {
        var vm = new LppBodyViewModel(Model(), workspaceRoot: null);
        vm.ApplyPickedOutputGridPath(PickedAbs);
        Assert.Equal(PickedAbs, vm.OutputGridPath);
    }
}
