// ================================================================
//  LvsPanelTests.cs — the gate for brief-lvs-12-gui.md §6.
//
//  ── ONE CLAIM, AND IT IS THE SAME ONE BRIEF 11 MAKES FROM THE OTHER SIDE ──────────────────────
//
//  The panel is a VIEW of `LvsRun.Run`. Gate 1 asserts that directly — the panel's result and the
//  verb's agree finding for finding — and everything after it is about the four things the panel
//  adds and the engine deliberately does not: where to look, what corresponds to what, when the
//  answer has gone stale, and which findings a human has signed off.
//
//  ── WHY THERE IS NO WINDOW ANYWHERE IN HERE ───────────────────────────────────────────────────
//
//  Every gesture the brief describes lands on `LayoutEditorViewModel`, which is what makes it
//  testable with no display at all. The `.axaml` above it binds and does nothing else — which is
//  itself a requirement (R-lvs12-5a) rather than a convenience.
//
//  ── THE FIXTURE ───────────────────────────────────────────────────────────────────────────────
//
//  `examples/LVS/Attenuator broken` (brief 5's six-fault board), copied to a temp tree whenever a
//  gate writes. Nothing in here ever writes inside the repository.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Lvs;

[Collection(LvsCliConsoleCollection.Name)]
public sealed class LvsPanelTests : IDisposable
{
    private const string Broken = "Attenuator broken";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-lvs12-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp folder that outlives the run is not a failure */ }
    }

    // ══ 1 — the panel's result IS the verb's (R-lvs12-1c) ════════════════════════════════════════
    //
    // The gate brief 11 states, asserted from this side. If these two could ever differ, a design
    // would pass on a build machine and be refused when someone opened it — and nothing would say
    // which of the two was right.

    [Fact]
    public void ThePanelsResult_EqualsTheVerbs_FindingForFinding()
    {
        var vm = Panel(Repo(Broken));

        var panel = vm.RunLvs();
        var verb  = LvsRun.Run(Repo(Broken));

        Assert.NotNull(panel);
        Assert.Equal(verb.Findings.Select(f => (f.Id, string.Join("|", f.Objects))),
                     panel!.Findings.Select(f => (f.Id, string.Join("|", f.Objects))));

        // Not vacuous: the six-fault board has something to say, and the panel says it.
        Assert.Equal(6, panel.ErrorCount);
        Assert.Equal(6, vm.LvsFindings.Count(r => r.IsError));

        // R-lvs12-1b: the strip states what the run DID — both sides' counts, the technology by
        // name and the reduction mode. A workspace with two processes has a default that may not be
        // the one the designer has in mind, and naming it is the whole mitigation.
        Assert.Contains("device(s)", vm.LvsSummaryText);
        Assert.Contains("reduction on", vm.LvsSummaryText);
        Assert.Contains("PCB 2-Layer", vm.LvsTechnologyText);
    }

    // ══ 2 — click a finding, the canvas zooms; an open fits ALL its islands ══════════════════════
    //
    // R-lvs12-2d. Seeing the pieces relative to each other IS the report — a zoom onto one island
    // answers a question nobody asked.

    [Fact]
    public void SelectingAnOpen_ZoomsToFitEveryIsland()
    {
        var vm = Panel(Repo(Broken));
        vm.RunLvs();

        Bbox? zoomed = null;
        vm.ZoomToRegionRequested += r => zoomed = r;

        var open = vm.LvsFindings.Single(r => r.Finding.Id == "lvs.net.open");
        vm.SelectedLvsFinding = open;
        vm.ZoomToSelectedFindingCommand.Execute(null);

        Assert.NotNull(zoomed);

        // Two islands, and the region covers every vertex of both — never just the first.
        Assert.True(open.Finding.MarkerRings.Count > 1);
        foreach (var ring in open.Finding.MarkerRings)
            for (int i = 0; i + 1 < ring.Length; i += 2)
            {
                Assert.InRange(ring[i],     zoomed!.Value.MinX, zoomed.Value.MaxX);
                Assert.InRange(ring[i + 1], zoomed.Value.MinY,  zoomed.Value.MaxY);
            }

        // R-lvs12-2b/5b: the markers the renderer is handed are the finding's own rings, and the
        // selected one is the one that draws heavier.
        var markers = vm.Overlay.LvsMarkers;
        Assert.Contains(markers, m => m.Selected);
        Assert.All(markers, m => Assert.NotEmpty(m.Rings));
    }

    // ══ 3 — cross-probe both directions, and an unmatched part says why ══════════════════════════

    [Fact]
    public void CrossProbeGoesBothWays_AndAnUnmatchedPartSaysWhy()
    {
        var (vm, schematic) = PanelWithSchematic(Repo(Broken));
        vm.RunLvs();

        // Layout → schematic. R2 is drawn and is also in the drawing, so selecting one selects the
        // other — which is the whole feature.
        Assert.Equal(1, vm.SelectLayoutDevices(["R2"]));
        var toSchematic = vm.ProbeFromLayout();
        Assert.True(toSchematic.Matched, toSchematic.Message);
        Assert.Contains("R2", toSchematic.Message);
        Assert.Contains(schematic.EditModel.Components.Where(c => schematic.Selection.IsSelected(c.Id)),
                        c => c.InstanceName == "R2");

        // Schematic → layout, the same pairing read the other way.
        schematic.Selection.SetAll(
            schematic.EditModel.Components.Where(c => c.InstanceName == "R1").Select(c => c.Id));
        var toLayout = vm.ProbeFromSchematic();
        Assert.True(toLayout.Matched, toLayout.Message);
        Assert.Contains("R1", toLayout.Message);
        Assert.NotEmpty(vm.SelectedInstanceIndices);

        // R-lvs12-3b. C1 is in the drawing and not in the artwork, and the panel SAYS SO rather
        // than silently highlighting nothing — which is indistinguishable from a counterpart that
        // is merely off screen.
        schematic.Selection.SetAll(
            schematic.EditModel.Components.Where(c => c.InstanceName == "C1").Select(c => c.Id));
        var unmatched = vm.ProbeFromSchematic();
        Assert.False(unmatched.Matched);
        Assert.Contains("C1", unmatched.Message);
        Assert.Contains("not matched", unmatched.Message);
        Assert.Equal(unmatched.Message, vm.LvsProbeText);
    }

    // ══ 4 — a by-symmetry pairing is announced AT the cross-probe ════════════════════════════════
    //
    // R-lvs12-3c. A user clicking C7 in the schematic and being shown a capacitor the drawing calls
    // C9 will believe the tool is wrong unless it says the pairing was arbitrary. The six-fault
    // board has no symmetric class of its own, so the correspondence here is built directly — which
    // is the right level anyway: WHICH pairings are arbitrary is decided below the firewall
    // (`LvsPairedBy.Symmetry`), and what this gate is about is that the panel announces it.

    [Fact]
    public void ABySymmetryPairing_IsAnnouncedAtTheCrossProbe_NotOnlyInTheList()
    {
        var (vm, schematic) = PanelWithSchematic(Repo(Broken));
        vm.RunLvs();

        var real = vm.LvsResult!;
        var arbitrary = real.Comparison.Devices
            .Select(p => p.SchematicName == "R1" ? p with { By = LvsPairedBy.Symmetry } : p)
            .ToList();
        vm.LvsResult = real with { Comparison = real.Comparison with { Devices = arbitrary } };

        schematic.Selection.SetAll(
            schematic.EditModel.Components.Where(c => c.InstanceName == "R1").Select(c => c.Id));
        var answer = vm.ProbeFromSchematic();

        Assert.True(answer.Matched, answer.Message);
        Assert.True(answer.Arbitrary);
        Assert.Contains("ARBITRARY", answer.Message);
        Assert.Equal(answer.Message, vm.LvsProbeText);
    }

    // ══ 5 — the panel goes stale on an edit, says so, and refuses to probe ═══════════════════════
    //
    // R-lvs12-3d. The correspondence is a snapshot of the last run. It does not re-run, and it does
    // not silently highlight against a result that no longer describes the design.

    [Fact]
    public void AnEditMarksThePanelStale_AndCrossProbingRefuses()
    {
        var (vm, schematic) = PanelWithSchematic(Repo(Broken));
        vm.RunLvs();
        Assert.False(vm.IsLvsStale);
        Assert.True(vm.CanCrossProbeLvs);

        vm.Model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        vm.Model.NotifyChanged();

        Assert.True(vm.IsLvsStale);
        Assert.False(vm.CanCrossProbeLvs);
        Assert.Contains("changed", vm.LvsStaleText);

        // The result is KEPT rather than dropped (unlike a DRC result) — the findings are still
        // readable. What is refused is the probe.
        Assert.NotNull(vm.LvsResult);
        schematic.Selection.SetAll(
            schematic.EditModel.Components.Where(c => c.InstanceName == "R2").Select(c => c.Id));
        var refused = vm.ProbeFromSchematic();
        Assert.False(refused.Matched);
        Assert.Equal(vm.LvsStaleText, refused.Message);
    }

    // ══ 5b — a run over an UNSAVED document says which files it read ════════════════════════════
    //
    // The panel compares what is on disk (this folder's RESOLVED.md §3) and the staleness mark is
    // what makes that honest — but a fresh result CLEARS that mark, so the one case it would
    // otherwise miss is the one it is most needed for: edit, run, and be told the artwork matches
    // a drawing you have already changed.

    [Fact]
    public void ARunOverAnUnsavedDocumentSaysItComparedTheSavedFiles()
    {
        var vm = Panel(Repo(Broken));
        vm.RunLvs();
        Assert.False(vm.IsLvsStale);

        // Any unsaved edit will do; a waiver is the one this view model can make on its own.
        vm.SetLvsWaived(vm.LvsFindings.First(r => r.IsError), true, "for now");
        Assert.True(vm.IsDirty);

        vm.RunLvs();

        Assert.True(vm.IsLvsStale);
        Assert.False(vm.CanCrossProbeLvs);
        Assert.Contains("SAVED", vm.LvsStaleText, StringComparison.Ordinal);
    }

    // ══ 6 — waiver round trip (R-lvs12-4a) ══════════════════════════════════════════════════════

    [Fact]
    public void AWaiver_SurvivesSaveAndReload_AndIsStillReportedButNotCounted()
    {
        string cell = Copy(Broken);
        var vm = Panel(cell);
        var before = vm.RunLvs()!;

        var row = vm.LvsFindings.Single(r => r.Finding.Id == "lvs.property.mismatch");
        vm.SetLvsWaived(row, true, "R3 is deliberately 150 Ω for the bring-up board.");

        Assert.True(vm.IsDirty);
        Assert.Equal(before.ErrorCount - 1, vm.LvsResult!.ErrorCount);   // not counted
        Assert.Equal(1, vm.LvsResult.WaivedCount);
        Assert.Contains(vm.LvsFindings, r => r.Finding.Id == "lvs.property.mismatch" && r.IsWaived);

        LayoutPersistence.SaveToFile(Clay(cell), vm.Model);

        // Reloaded from disk, by the run itself — which is what proves the `.clay` carries it and
        // that `LvsRun` honours it (R-lvs12-4f) rather than the panel re-deciding.
        var reloaded = LvsRun.Run(cell);
        var waived = reloaded.Findings.Single(f => f.Id == "lvs.property.mismatch");
        Assert.True(waived.Waived);                                       // still REPORTED
        Assert.Contains("bring-up", waived.WaiverReason!);
        Assert.Equal(before.ErrorCount - 1, reloaded.ErrorCount);
    }

    // ══ 7 — the key is the CORRESPONDENCE, not a box (R-lvs12-4b, R-lvs12-4c) ═══════════════════
    //
    // Two tests, and they are the reason this key differs from DRC's. A DRC waiver names a PLACE
    // and correctly dies when the shape moves; an LVS waiver names a RELATIONSHIP.

    [Fact]
    public void AWaiverSurvivesMovingThePart_EvenThoughTheMarkerMoves()
    {
        string cell = Copy(Broken);
        var vm = Panel(cell);
        vm.RunLvs();

        var row = vm.LvsFindings.Single(r => r.Finding.Id == "lvs.property.mismatch");
        string placeKey = row.Finding.Key;                  // the DRC-form key: id, objects, box
        vm.SetLvsWaived(row, true, "known");
        LayoutPersistence.SaveToFile(Clay(cell), vm.Model);

        // Move R3's artwork — far enough that its marker box is a different box, and not so far
        // that it leaves its own copper.
        var view = LayoutPersistence.LoadFromFile(Clay(cell));
        var moved = view.Instances.Single(i => i.DisplayRefDes == "R3");
        moved.X += 4000; moved.Y += 4000;                    // 4 µm at 1000 DBU/µm
        LayoutPersistence.SaveToFile(Clay(cell), view);

        var after = LvsRun.Run(cell).Findings.Single(f => f.Id == "lvs.property.mismatch");
        Assert.NotEqual(placeKey, after.Key);                // the PLACE changed …
        Assert.True(after.Waived);                           // … and the waiver still applies
    }

    [Fact]
    public void AWaiverStopsApplying_WhenTheSchematicChanges()
    {
        string cell = Copy(Broken);
        var vm = Panel(cell);
        vm.RunLvs();

        // The short names two SCHEMATIC nets, so its identity is a relationship in the drawing.
        var row = vm.LvsFindings.Single(r => r.Finding.Id == "lvs.net.short");
        vm.SetLvsWaived(row, true, "the ground pour deliberately meets the input pad");
        LayoutPersistence.SaveToFile(Clay(cell), vm.Model);
        Assert.True(LvsRun.Run(cell).Findings.Single(f => f.Id == "lvs.net.short").Waived);

        RenameSchematicNet(cell, "IN", "INPUT");

        var after = LvsRun.Run(cell).Findings.Single(f => f.Id == "lvs.net.short");
        Assert.False(after.Waived);
        Assert.Contains("INPUT", after.Render());
    }

    // ══ 8 — an orphaned waiver is listed with its original text and is removable (R-lvs12-4d) ═══

    [Fact]
    public void AnOrphanedWaiver_IsListedWithItsOriginalTextAndIsRemovable()
    {
        string cell = Copy(Broken);
        var vm = Panel(cell);
        vm.RunLvs();

        var row = vm.LvsFindings.Single(r => r.Finding.Id == "lvs.net.short");
        string text = row.Finding.Render();
        vm.SetLvsWaived(row, true, "deliberate");
        LayoutPersistence.SaveToFile(Clay(cell), vm.Model);
        Assert.Empty(vm.LvsOrphanWaivers);

        RenameSchematicNet(cell, "IN", "INPUT");
        vm.RunLvs();

        var orphan = Assert.Single(vm.LvsOrphanWaivers);
        Assert.Equal(text, orphan.FindingText);              // six months later, the key says nothing
        Assert.Contains("deliberate", orphan.ReasonText);

        orphan.RemoveCommand.Execute(null);
        Assert.Empty(vm.LvsOrphanWaivers);
        Assert.Empty(vm.Model.LvsWaivers);
    }

    // ══ 9 — a `.clay` with no waivers re-serializes byte for byte (R-lvs12-4e) ══════════════════

    [Fact]
    public void AClayWithNoWaivers_ReSerializesByteForByte()
    {
        string cell = Copy(Broken);

        // Written by circuitRF first, deliberately: the committed fixture's artwork is generated by
        // a Python script whose JSON spells some numbers differently (`1.0` for `1`), and a test
        // that compared against THAT would be measuring the generator rather than this brief's
        // additive field. What is pinned here is that a `.clay` holding no waivers round-trips
        // through the reader and the writer unchanged — which is what "no FormatVersion bump" means.
        var view = LayoutPersistence.LoadFromFile(Clay(cell));
        Assert.Empty(view.LvsWaivers);
        LayoutPersistence.SaveToFile(Clay(cell), view);
        byte[] before = File.ReadAllBytes(Clay(cell));

        LayoutPersistence.SaveToFile(Clay(cell), LayoutPersistence.LoadFromFile(Clay(cell)));

        Assert.Equal(before, File.ReadAllBytes(Clay(cell)));
        Assert.DoesNotContain("LvsWaivers", File.ReadAllText(Clay(cell)), StringComparison.Ordinal);
    }

    // ══ 9b — a run writes nothing, waivers or not (R-lvs12-4g) ══════════════════════════════════
    //
    // Note §1.3 says LVS writes nothing and §4 persists something, and the distinction has to be
    // structural rather than remembered: a waiver is a user's edit of their own document; the RUN
    // reads them and never writes them back. That is `check`'s own rule (R-aut4-6) and is what keeps
    // a comparison usable on a read-only tree and on a workspace another process has open.

    [Fact]
    public void ARunWritesNothing_EvenOverAWorkspaceHoldingWaivers()
    {
        string cell = Copy(Broken);
        var vm = Panel(cell);
        vm.RunLvs();
        vm.SetLvsWaived(vm.LvsFindings.Single(r => r.Finding.Id == "lvs.net.short"), true, "deliberate");
        LayoutPersistence.SaveToFile(Clay(cell), vm.Model);

        var before = Snapshot(_root);
        var honoured = LvsRun.Run(cell);
        var after = Snapshot(_root);

        Assert.True(honoured.Findings.Single(f => f.Id == "lvs.net.short").Waived);
        Assert.Equal(before, after);
    }

    // ══ 10 — the CLI honours a waiver written in the GUI (R-lvs12-4f) ═══════════════════════════

    [Fact]
    public void TheCliHonoursAWaiverWrittenInTheGui_AndReportsItAsWaived()
    {
        string cell = Copy(Broken);
        var vm = Panel(cell);
        vm.RunLvs();
        vm.SetLvsWaived(vm.LvsFindings.Single(r => r.Finding.Id == "lvs.property.mismatch"),
                        true, "bring-up board");
        LayoutPersistence.SaveToFile(Clay(cell), vm.Model);

        var real = Console.Out;
        var buffer = new StringWriter();
        int exit;
        try { Console.SetOut(buffer); JsonRun.Reset(); exit = CliEntry.Run(["lvs", cell, "--json"]); }
        finally { Console.SetOut(real); }

        var lvs = JsonDocument.Parse(buffer.ToString()).RootElement
            .GetProperty("result").GetProperty("lvs");
        var one = lvs.GetProperty("cells").EnumerateArray().Single();

        Assert.Equal(1, one.GetProperty("waived").GetInt32());
        Assert.Equal(5, one.GetProperty("errors").GetInt32());   // six faults, one signed off
        Assert.Equal(1, exit);

        var finding = one.GetProperty("findings").EnumerateArray()
            .Single(f => f.GetProperty("id").GetString() == "lvs.property.mismatch");
        Assert.True(finding.GetProperty("waived").GetBoolean());
        Assert.Equal("bring-up board", finding.GetProperty("waiverReason").GetString());
    }

    // ══ 12 — the export gate's checkbox is PRESENT and defaults OFF (R-lvs12-1e) ════════════════
    //
    // "Off with the box visible is not the same as absent." LVS needs a schematic and an
    // artwork-only export is a legitimate thing to do, so the default is the one place this gate
    // deliberately differs from DRC's.

    [Fact]
    public void TheExportGate_IsPresentAndDefaultsOff()
    {
        Assert.Null(new AppPreferences().CheckLvsOnExport);
        Assert.False(new AppPreferences().CheckLvsOnExport ?? false);

        // Present in both the places DRC's is: the settings page, and the gate dialog itself.
        string settings = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/Dialogs/SettingsView.axaml"));
        Assert.Contains("CheckLvsOnExportCheck", settings, StringComparison.Ordinal);

        string dialog = File.ReadAllText(
            Path.Combine(RepoRoot(), "src/Ui/Views/Dialogs/LvsExportGateDialog.axaml"));
        Assert.Contains("KeepCheckingCheck", dialog, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"False\"", dialog, StringComparison.Ordinal);

        // And the export path reads it — a preference nothing consults is a preference that does
        // nothing, which is worse than one that is absent.
        string view = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/Layout/LayoutEditorView.axaml.cs"));
        Assert.Contains("CheckLvsOnExport", view, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
            view, @"ConfirmLayoutVersusSchematicBeforeExportAsync\(vm, owner").Count);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static string Repo(string cell) => Path.Combine(RepoRoot(), "examples", "LVS", cell);

    private static string Clay(string cellDir) => Path.Combine(cellDir, "layout", "Attenuator.clay");

    private static string Csch(string cellDir) => Path.Combine(cellDir, "schematic", "Attenuator.csch");

    /// <summary>The whole workspace on a throwaway copy — nothing here ever writes in the repo.</summary>
    private string Copy(string cell)
    {
        string source = Path.Combine(RepoRoot(), "examples", "LVS");
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return Path.Combine(_root, cell);
    }

    /// <summary>The editor a layout document has, with nothing else attached.</summary>
    private static LayoutEditorViewModel Panel(string cellDir)
        => new(LayoutPersistence.LoadFromFile(Clay(cellDir)), Clay(cellDir));

    /// <summary>The same, with this cell's drawing open beside it — what the workspace installs.</summary>
    private static (LayoutEditorViewModel Layout, SchematicViewModel Schematic) PanelWithSchematic(string cellDir)
    {
        var layout = Panel(cellDir);
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch(cellDir));
        var schematic = new SchematicViewModel(model, messageSink: null);
        layout.LvsSchematic = schematic;
        return (layout, schematic);
    }

    /// <summary>One bounded mutation of the DRAWING: a net label renamed, topology untouched.</summary>
    private static void RenameSchematicNet(string cellDir, string from, string to)
    {
        string text = File.ReadAllText(Csch(cellDir));
        File.WriteAllText(Csch(cellDir), text.Replace($"\"{from}\"", $"\"{to}\""));
    }

    /// <summary>Every file under a tree with its size and last-write time — "wrote nothing", checked.</summary>
    private static IReadOnlyList<string> Snapshot(string root)
        => [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                 .OrderBy(f => f, StringComparer.Ordinal)
                 .Select(f => $"{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}")];

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
