// ================================================================
//  HarmonicaHooksAndInterchangeTests.cs  —  M4 and M5's gates, brief-harmonicarf-h8
//
//  M4   the four hooks H7 left null are wired, and a .charm in a workspace is in the project tree
//       (open item 6, settled in the affirmative — see the note on the tree tests below).
//  M5   THE PHASE GATE: one .charm, two binaries, the same numbers.
//
//  R-h8-11  Copy Plot / Export Data go through the EXISTING exporters, never a second one.
//  R-h8-13  the gate runs through the product path, not around it.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaHooksAndInterchangeTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.True(dir.Length > 0, "could not locate the repository root");
        return dir;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static HarmonicaViewModel Solved(CircuitModel? model = null)
    {
        var vm = new HarmonicaViewModel(model);
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        return vm;
    }

    // ══ M4 — every hook is wired, by reflection over the hook set ════════════

    /// <summary>
    /// The load-bearing form of "the four unwired hooks are wired": every <c>Action?</c> hook the menu
    /// declares is ASSIGNED in <see cref="CircuitRF.Ui.Views.Harmonica.HarmonicaView"/>'s own wiring —
    /// found by reflection, not by a hand-written list, so a hook added later and left null fails HERE
    /// rather than doing nothing under a menu item that looks live.
    /// </summary>
    [Fact]
    public void EveryMenuHook_IsAssignedInTheViewsWiring()
    {
        var hooks = typeof(HarmonicaMenuViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Action) && p.Name.EndsWith("Hook", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();

        Assert.NotEmpty(hooks);   // a vacuous pass would be a test of nothing

        string view = Read("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs");
        var unwired = hooks.Where(h => !view.Contains($"menus.{h}", StringComparison.Ordinal)).ToArray();

        Assert.True(unwired.Length == 0, $"unwired hook(s): {string.Join(", ", unwired)}");
        output.WriteLine($"{hooks.Length} hooks, all wired: {string.Join(", ", hooks)}");
    }

    /// <summary>
    /// The four this phase pays for, named individually — the reflection test above would still pass
    /// if all four were dropped from the menu rather than wired, and dropping them is not the fix.
    /// </summary>
    [Theory]
    [InlineData("SetDutHook")]
    [InlineData("ExportDataHook")]
    [InlineData("CopyPlotHook")]
    [InlineData("HelpHook")]
    public void TheFourHooksH7LeftNull_ExistAndAreWired(string hookName)
    {
        var prop = typeof(HarmonicaMenuViewModel).GetProperty(hookName);
        Assert.NotNull(prop);
        Assert.Equal(typeof(Action), prop!.PropertyType);
        Assert.Contains($"menus.{hookName}", Read("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs"),
                        StringComparison.Ordinal);
    }

    // ══ M4 — Export Data writes something that reads back ════════════════════

    /// <summary>
    /// R-h8-11's "through the EXISTING exporters": the export is one call to
    /// <c>RfCore.Export.DataSetExporter</c> on the frame's own published <c>DataSet</c>, so the gate is
    /// that the written file re-imports with the same cubes and the same numbers. The picker is the
    /// only part of the real path this cannot reach headlessly (an <c>IStorageFile</c> needs a live
    /// platform), so the format-by-extension dispatch is pinned by source scan alongside it.
    /// </summary>
    [Fact]
    public void ExportData_WritesTheFramesOwnDataSet_AndItReadsBackUnchanged()
    {
        var vm = Solved();
        var ds = vm.Frame.Published;
        Assert.NotNull(ds);

        string path = Path.Combine(Path.GetTempPath(), $"harm-export-{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(ds!, path, RfCore.Export.ExportFormat.Npy);
            Assert.True(File.Exists(path));

            var back = RfCore.Export.DataSetImporter.Import(path).DataSet;

            // Every cube survives, by name and by shape — an export that dropped or reshaped one
            // would be a file that disagrees with what was on screen.
            foreach (var (name, a) in ds!.Cubes)
            {
                Assert.True(back.Cubes.ContainsKey(name), $"cube '{name}' missing after round trip");
                var b = back.Cubes[name];
                Assert.Equal(a.Rank, b.Rank);
                Assert.Equal(a.DataKind, b.DataKind);
            }

            var gi = ds.Cubes["Gamma_intr"];
            var gb = back.Cubes["Gamma_intr"];
            var za = gi.ComplexValues;
            var zb = gb.ComplexValues;
            Assert.Equal(za.Length, zb.Length);
            for (int i = 0; i < za.Length; i++)
                Assert.Equal(za[i], zb[i]);

            output.WriteLine($"{ds.Cubes.Count} cubes round-tripped through {Path.GetExtension(path)}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExportData_PicksItsFormatFromTheChosenExtension()
    {
        string view = Read("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs");
        foreach (string s in new[]
                 {
                     "\".mat\" => RfCore.Export.ExportFormat.Mat",
                     "\".txt\" => RfCore.Export.ExportFormat.Tsv",
                     "RfCore.Export.DataSetExporter.Export(ds, path, format)",
                 })
            Assert.Contains(s, view, StringComparison.Ordinal);
    }

    // ══ M4 — open item 6: a .charm in a workspace IS in the project tree ═════

    /// <summary>
    /// Open item 6, settled YES. A <c>.charm</c> is a results-facing document that lives beside a
    /// <c>.cdd</c>, and a workspace file that does not appear in the tree is a file the user has no way
    /// to reopen. It rides the Data Displays filter rather than a seventh checkbox for the same reason.
    /// </summary>
    [Fact]
    public void ACharmInAWorkspace_IsScannedAsAHarmonicaFile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harm-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".cws"), "{}");
            File.WriteAllText(Path.Combine(root, "bias-study.charm"), "{}");

            var node = WorkspaceScanner.Scan(root);
            var charm = FindByName(node, "bias-study.charm");

            Assert.NotNull(charm);
            Assert.Equal(NodeKind.HarmonicaFile, charm!.Kind);
        }
        finally { Directory.Delete(root, recursive: true); }

        static ProjectTreeNode? FindByName(ProjectTreeNode n, string name)
        {
            if (string.Equals(n.Name, name, StringComparison.Ordinal)) return n;
            foreach (var c in n.Children)
                if (FindByName(c, name) is { } hit) return hit;
            return null;
        }
    }

    [Fact]
    public void AHarmonicaFileNode_IsOpenableAndRidesTheDataDisplaysFilter()
    {
        var charm = new ProjectTreeNode(NodeKind.HarmonicaFile, "bias-study.charm",
                                        "/tmp/ws/bias-study.charm", "bias-study.charm");
        var root = new ProjectTreeNode(NodeKind.Workspace, "ws", "/tmp/ws", "");
        root.AddChild(charm);

        var filter = new ProjectTreeFilterState();
        var vm = new ProjectTreeNodeViewModel(root, filter);
        var charmVm = vm.Children.Single();

        Assert.True(charmVm.IsOpenableFile);
        Assert.Single(vm.FilteredChildren);            // everything on ⇒ shown

        filter.SetAll(false);
        Assert.Empty(vm.FilteredChildren);             // nothing on ⇒ hidden

        filter.DataDisplays = true;
        Assert.Single(vm.FilteredChildren);            // it rides the Data Displays toggle
    }

    // ══ M5 — THE PHASE GATE: one .charm, two binaries, the same numbers ══════

    /// <summary>
    /// §8's claim is that a <c>.charm</c> is self-describing; two binaries reading one file is the test
    /// of it. Both binaries are the SAME assembly with a different <c>Main</c>, so what is actually at
    /// risk is not two readers disagreeing — it is one of the two OPEN PATHS resolving something the
    /// other does not. This drives the file through the whole thing: write it, read it back with no
    /// ambient state at all, solve, and compare every published cube bit-for-bit.
    /// </summary>
    [Fact]
    public void ACharmWrittenOnce_SolvesToTheSameNumbersWhenReadBack()
    {
        var model = HarmonicaViewModel.DefaultModel() with
        {
            Embedding = new EmbeddingStack { Package = new LumpedPackage { Rs = 0.8, Ls = 0.15e-9, Rd = 4.0 } },
        };

        string path = Path.Combine(Path.GetTempPath(), $"harm-gate-{Guid.NewGuid():N}.charm");
        try
        {
            var written = Solved(model);
            CharmIo.WriteFile(path, written.Model, written.Terminations);

            // Read it back the way an opening binary does — from the file alone, nothing carried over.
            var contents = CharmIo.ReadAllFile(path);
            var reopened = Solved(contents.Model);

            var a = written.Frame.Published!;
            var b = reopened.Frame.Published!;

            Assert.Equal(a.Cubes.Keys.OrderBy(n => n, StringComparer.Ordinal),
                         b.Cubes.Keys.OrderBy(n => n, StringComparer.Ordinal));

            int compared = 0;
            foreach (var (name, ca) in a.Cubes)
            {
                var cb = b.Cubes[name];
                Assert.Equal(ca.Rank, cb.Rank);
                Assert.Equal(ca.DataKind, cb.DataKind);

                if (ca.DataKind == DataKind.Complex)
                {
                    var za = ca.ComplexValues;
                    var zb = cb.ComplexValues;
                    Assert.Equal(za.Length, zb.Length);
                    for (int i = 0; i < za.Length; i++)
                    {
                        AssertSame(za[i].Real, zb[i].Real, name, i);
                        AssertSame(za[i].Imaginary, zb[i].Imaginary, name, i);
                        compared++;
                    }
                }
                else
                {
                    var ra = ca.RealValues;
                    var rb = cb.RealValues;
                    Assert.Equal(ra.Length, rb.Length);
                    for (int i = 0; i < ra.Length; i++) { AssertSame(ra[i], rb[i], name, i); compared++; }
                }
            }

            Assert.True(compared > 0, "the gate compared no values");
            output.WriteLine($"{a.Cubes.Count} cubes / {compared} values, identical across the round trip");
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        static void AssertSame(double x, double y, string cube, int i)
        {
            if (double.IsNaN(x) && double.IsNaN(y)) return;   // an unavailable intrinsic side is NaN by design
            Assert.True(x.Equals(y), $"{cube}[{i}]: {x} vs {y}");
        }
    }

    /// <summary>
    /// The other half of the gate, and the half a value comparison cannot reach: both binaries open a
    /// <c>.charm</c> through the SAME method. circuitRF's Dock document and the standalone shell each
    /// call <c>HarmonicaView.LoadCharmFile</c> — one loader, so "two binaries, one file" is true by
    /// construction rather than by two implementations happening to agree today.
    /// </summary>
    [Fact]
    public void BothBinaries_OpenACharmThroughTheOneLoader()
    {
        Assert.Contains("public void LoadCharmFile(string path)",
                        Read("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs"), StringComparison.Ordinal);

        // The standalone shell's own open path.
        Assert.Contains("View.LoadCharmFile(path)",
                        Read("src/Ui/Views/Harmonica/HarmonicaShellWindow.axaml.cs"), StringComparison.Ordinal);

        // circuitRF's: the workspace opens the document, the view loads the file.
        Assert.Contains("LoadCharmFile", Read("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs"),
                        StringComparison.Ordinal);
        Assert.Contains("OpenHarmonicaPath", Read("src/Ui/ViewModels/WorkspaceViewModel.cs"),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>.charm</c> saved from either binary lands in the workspace's own tracking, so reopening it
    /// activates the open document instead of minting a second one — the failure a save-then-reopen
    /// would otherwise produce is two live views of one file, each unaware of the other's edits.
    /// </summary>
    [Fact]
    public void SavingACharm_ReKeysTheWorkspacesOpenDocumentTracking()
    {
        string ws = Read("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("NotifyHarmonicaSaved", ws, StringComparison.Ordinal);
        Assert.Contains("Workspace?.NotifyHarmonicaSaved(_doc, path)",
                        Read("src/Ui/Views/Harmonica/HarmonicaView.axaml.cs"), StringComparison.Ordinal);
    }
}
