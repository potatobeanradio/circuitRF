// ================================================================
//  TechnologyLiveDisplayEditTests.cs — brief-railrf-20-layer-visibility.md R-rail20-2
//
//  AN UNSAVED `.ctech` EDIT HAS TO REACH railRF.
//
//  The designer's report, precisely (2026-09-20): toggling a layer's Vis "only works after saving
//  the file and then closing railRF and reopening".
//
//  ── WHAT THE BUG TURNED OUT TO BE, WHICH IS NOT WHAT THE BRIEF EXPECTED ──────────────────────
//
//  The brief's §2 traced the fault to a missing live seam ("no unsaved .ctech edit reaches any
//  viewer, in any window"). That is not the case and has not been since brief-L1-fix: the tech
//  editor already raises TechLiveChanged from its one edit funnel, WorkspaceViewModel installs the
//  clone with TechnologyCache.SetLive, and SetLive raises TechnologyChanged → OnTechnologyChanged →
//  TechnologyReResolved. The layout editor follows an unsaved edit today, and LayoutLiveTechnology-
//  Tests has gated that since it landed.
//
//  The fault was railRF's own, and it was in the LAST link. RailRfWindow.AdoptLiveTechnology had
//  two routes to a technology and both went through the `.clay`: the open layout SESSION, which
//  exists only while someone has that file open in its own window, and `board.View`, which railRF's
//  OPEN path never sets — only the live-artwork swap does, and that swap needs a session too. So
//  the pair of windows the layer question actually sends a user to, railRF and the technology
//  editor, reached neither route. A save did not help either, for the same reason, which is exactly
//  why closing railRF and reopening it was the thing that worked.
//
//  ── AND WHY THERE IS NO SECOND, DISPLAY-ONLY SEAM ────────────────────────────────────────────
//
//  R-rail20-2a asked for one carrying display properties alone. Adding it beside the seam that
//  already exists would give one file two live readings that could disagree, and railRF already
//  draws the display/stackup line where the brief wants it — on the CONSEQUENCE rather than on the
//  event. RailRfViewModel.StackupSignature is that line and argues it at length: a visibility edit
//  keeps the numbers, a thickness edit drops them, and both are asserted below.
//
//  ── THE HARNESS ──────────────────────────────────────────────────────────────────────────────
//
//  WorkspaceViewModel cannot be constructed headlessly, so this composes the real types it wires
//  with the SAME subscription bodies — LayoutLiveTechnologyTests' own shape, one link longer. The
//  source scan at the end is what stops the mirror drifting from the window it mirrors.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public class TechnologyLiveDisplayEditTests
{
    /// <summary>
    /// The designer's report as a test: untick <c>Vis</c>, do NOT save, and railRF stops drawing
    /// that layer.
    /// </summary>
    [Fact]
    public void AnUnsavedVisibilityEditReachesRailRf()
    {
        using var h = new Harness();
        var key = new LayerKey(1, 0);

        h.ToggleVisible(key, false);

        Assert.Contains(key, h.Rail.BoardOverlayLayer.HiddenLayers);
        Assert.False(h.Rail.BoardLayout!.Technology!.Layers.Single(l => l.Key == key).Visible);
    }

    /// <summary>
    /// <b>And the SECOND one does too</b> — R-rail20-2b's trap, which a test toggling once would
    /// pass with the bug present.
    /// </summary>
    /// <remarks>
    /// <c>AdoptTechnology</c> returns immediately on <c>ReferenceEquals</c>, so an edit handed out
    /// as the editor's own mutated-in-place working copy would be dropped from the second one
    /// onward. What makes the reference test safe is that <c>ApplySnapshot</c> deserialises a FRESH
    /// clone per committed edit and <c>SetLive</c> stores that clone — the side this repository
    /// picked, written down here and in <c>AdoptTechnology</c>'s own remarks.
    /// </remarks>
    [Fact]
    public void AndTheSecondUnsavedEditDoesToo()
    {
        using var h = new Harness();

        h.ToggleVisible(new LayerKey(1, 0), false);
        var afterFirst = h.Rail.Board!.Technology;

        h.ToggleVisible(new LayerKey(2, 0), false);

        Assert.NotSame(afterFirst, h.Rail.Board!.Technology);     // a fresh instance, not a mutation
        Assert.Contains(new LayerKey(1, 0), h.Rail.BoardOverlayLayer.HiddenLayers);
        Assert.Contains(new LayerKey(2, 0), h.Rail.BoardOverlayLayer.HiddenLayers);
    }

    /// <summary>
    /// A DISPLAY edit keeps the numbers; a STACKUP edit drops them. The split R-rail20-2a asks for,
    /// drawn where <c>StackupSignature</c> already draws it.
    /// </summary>
    /// <remarks>
    /// <b>Not two seams.</b> A live edit carries the whole working copy, because that is what the
    /// layout editor beside this window is already drawing with and a second, narrower reading of
    /// one file could disagree with it. What must not happen is a visibility toggle costing a
    /// re-solve, and what must happen is a copper thickness invalidating numbers priced against the
    /// old one — railRF cannot tell the two apart from the outside, so it compares the stackup.
    /// </remarks>
    [Fact]
    public void AVisibilityEditKeepsTheNumbersAndAStackupEditDoesNot()
    {
        using var h = new Harness();
        h.Rail.RunCommand.Execute(null);
        Assert.NotEmpty(h.Rail.ByModel);

        h.ToggleVisible(new LayerKey(1, 0), false);
        Assert.NotEmpty(h.Rail.ByModel);                          // the picture moved, not the board

        h.Edit(w => w.Stackup.Layers[0].ThicknessDbu += 1_000, "Thicken L1");
        Assert.Empty(h.Rail.ByModel);                             // priced against a stackup that is gone
    }

    /// <summary>The save path is unchanged and still fires the ordinary invalidate — R-rail20-2c.</summary>
    /// <remarks>
    /// The live seam is ADDITIVE. A brief that replaced the save path with it would make an unsaved
    /// edit indistinguishable from a saved one, and the <c>.ctech</c>'s dirty state is what tells a
    /// user their change is not on disk yet.
    /// </remarks>
    [Fact]
    public void TheSavePathStillInvalidates()
    {
        using var h = new Harness();
        h.ToggleVisible(new LayerKey(1, 0), false);

        Assert.True(h.Editor.IsDirty);
        Assert.True(h.Cache.HasLiveOverride(h.TechPath));

        h.Changed.Clear();
        h.Editor.SaveCommand.Execute(null);

        Assert.False(h.Editor.IsDirty);
        Assert.False(h.Cache.HasLiveOverride(h.TechPath));        // Invalidate drops the override
        Assert.Contains(h.TechPath, h.Changed);                   // and the seam fired
        Assert.False(TechPersistence.LoadFromFile(h.TechPath)
                                    .Layers.Single(l => l.Key == new LayerKey(1, 0)).Visible);
    }

    /// <summary>
    /// The window itself carries the by-path route, so the harness above is not the only place it
    /// exists.
    /// </summary>
    /// <remarks>
    /// The harness mirrors <c>AdoptLiveTechnology</c> because <c>WorkspaceViewModel</c> cannot be
    /// constructed headlessly, and a mirror proves nothing on its own — the production method could
    /// lose the route and every test here would still pass. This reads the file.
    /// </remarks>
    [Fact]
    public void TheWindowResolvesTheTechnologyByPathAsWell()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "RailRf", "RailRfWindow.LiveArtwork.cs"));

        Assert.Contains("TechnologyAt(techPath)", source, StringComparison.Ordinal);
        Assert.Contains("board.TechPath", source, StringComparison.Ordinal);

        // …and it is reached on a board that names no workspace cell, which is what putting the
        // call outside the ArtworkCellRef gate buys.
        int gate    = source.IndexOf("if (board.ArtworkCellRef is { Length: > 0 } clay)", StringComparison.Ordinal);
        int byPath  = source.IndexOf("tech ??= board.TechPath", StringComparison.Ordinal);
        int adopt   = source.IndexOf("vm.AdoptTechnology(tech)", StringComparison.Ordinal);
        Assert.True(gate > 0 && byPath > gate && adopt > byPath);
    }

    // ══ the harness ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The real types <c>WorkspaceViewModel</c> wires, with its own subscription bodies — and
    /// railRF in the state the report was made in: a board whose <c>.ctech</c> resolved, with
    /// <b>no layout session and no <c>LayoutView</c></b>, because the `.clay` is not open.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public string TechPath { get; }
        public TechnologyCache Cache { get; } = new();
        public TechEditorViewModel Editor { get; }
        public RailRfViewModel Rail { get; }
        public List<string> Changed { get; } = [];

        public Harness()
        {
            TechPath = Path.Combine(Path.GetTempPath(), $"railtech-{Guid.NewGuid():N}.ctech");
            TechPersistence.SaveToFile(TechPath, TwoLayerTech());

            Rail = new RailRfViewModel(OneRail(), null)
            {
                PostToUi     = a => a(),
                RunOffThread = (work, _) => Task.FromResult(work()),
            };
            Rail.SolveFunc = (request, _) =>
                new RailDcRunResult(null, [], [.. request.Document.Rails.Select(r => r.Name)], []);
            Rail.Board = new RailBoardInputs
            {
                Shapes     = [],
                Technology = Cache.Get(TechPath)!,
                TechPath   = TechPath,
                // No ArtworkCellRef and no View — the `.clay` is not open, which is the whole point.
            };
            Rail.ConfirmReferenceCommand.Execute(null);

            // Mirrors WorkspaceViewModel.OnTechnologyChanged: the open layout documents (none here)
            // re-resolve, then TechnologyReResolved is raised for the windows that are not documents.
            Cache.TechnologyChanged += path =>
            {
                Changed.Add(path);
                AdoptLiveTechnology();
            };

            Editor = new TechEditorViewModel(TechPath, TechPersistence.LoadFromFile(TechPath));
            // Mirrors OnTechLiveChanged (applied synchronously — nothing here is racing a frame)
            // and OnTechSaved, body for body.
            Editor.TechLiveChanged += (path, clone) => Cache.SetLive(path, clone);
            Editor.TechSaved       += path => Cache.Invalidate(path);
        }

        /// <summary>Mirrors <c>RailRfWindow.AdoptLiveTechnology</c>: no session, no model, so it is
        /// the by-path route that has to answer.</summary>
        private void AdoptLiveTechnology()
        {
            if (Rail.Board is not { TechPath: { Length: > 0 } path }) return;
            if (Cache.Get(path) is { } tech) Rail.AdoptTechnology(tech);
        }

        /// <summary>One layer's <c>Vis</c> box, through the editor's own row and its own commit.</summary>
        public void ToggleVisible(LayerKey key, bool visible) =>
            Editor.Layers.Single(r => r.Layer.Key == key).Visible = visible;

        /// <summary>Any other edit, through the same funnel a row uses.</summary>
        public void Edit(Action<Technology> write, string description)
        {
            string before = Editor.SnapshotJson();
            write(Editor.Working);
            Editor.CommitEdit(before, description);
        }

        public void Dispose() { try { File.Delete(TechPath); } catch { } }
    }

    private static RailDocument OneRail()
    {
        var doc = new RailDocument { Name = "live" };
        var rail = new RailSpec { Name = "+1V8", NetName = "+1V8" };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.7,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
        doc.Rails.Add(rail);
        return doc;
    }

    private static Technology TwoLayerTech()
    {
        var tech = new Technology { Name = "board" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "L1", ZOrder = 1 });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "L2", ZOrder = 2 });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "L1", ThicknessDbu = 35_000,
            DrawingLayers = [new LayerKey(1, 0)],
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "L2", ThicknessDbu = 35_000,
            IsGroundReference = true, DrawingLayers = [new LayerKey(2, 0)],
        });
        return tech;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
