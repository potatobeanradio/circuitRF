// ================================================================
//  RailRfImportedBoardTests.cs
//
//  Two defects reported from the field, 2026-09-21 — a Gerber set imported into a workspace, its
//  footprints placed by hand in the `.clay`, and the workspace then handed on. One test per CLAIM.
//
//   1. A `.crail` names its artwork DOCUMENT-RELATIVE. `RailDocument` says so on all five of its
//      reference fields ("so an archived workspace still resolves") and nothing wrote them that
//      way: the open and the import both handed the document the absolute path they had just read.
//      Such a document opens for its author and for nobody else — the reported workspace's own
//      artwork reference is a `C:\Users\…` path, and off Windows that string is not even rooted, so
//      it resolves under the document's own folder and the board "is not there".
//   2. A conductor the stackup declares with NO drawing layer attached is LISTED in the reference
//      combo, disabled, and the sentence under the combo names it and the remedy. It used to be
//      absent: a designer added an inner ground plane to the stackup, came back to railRF, and the
//      combo offered the same two outer layers as before — while the proposal line said the stackup
//      marked no ground reference at all, on a stackup whose ground-reference tick they had set
//      themselves.
// ================================================================

using System;
using System.IO;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfImportedBoardTests
{
    // ══ 1 — the references a save writes ════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Saving a document restates every reference against the folder it is written into</b> — so
    /// an absolute path becomes relative, and a <i>Save as…</i> into another folder keeps the board.
    /// </summary>
    /// <remarks>
    /// The assertion is on the STORED text and then on the resolution, because the defect is only
    /// visible in the text: an absolute reference resolves perfectly well on the machine that wrote
    /// it, which is why it survived. The second half moves the document one folder down and resolves
    /// again — the case a rebase that only handled absolute paths would get wrong.
    /// </remarks>
    [Fact]
    public void SavingRestatesEveryReferenceAgainstTheDocumentsOwnFolder()
    {
        string root = NewDir();
        try
        {
            string layoutDir = Path.Combine(root, "board", "layout");
            Directory.CreateDirectory(layoutDir);
            string clay = Path.Combine(layoutDir, "board.clay");
            LayoutPersistence.SaveToFile(clay, new LayoutView());

            string crail = Path.Combine(root, "board.crail");
            var doc = new RailDocument { Name = "board", ArtworkCellRef = clay };   // absolute, as the window sets it

            RailArtwork.RebaseReferences(doc, null, crail);
            RailDocumentIo.SaveToFile(crail, doc);

            Assert.Equal("board/layout/board.clay", ReadArtworkRef(crail));
            Assert.Equal(
                RailArtworkOutcome.NoTechnology,   // it resolved: an empty layout names no stackup
                RailArtwork.Resolve(RailDocumentIo.LoadFromFile(crail), crail, null, new TechnologyCache())
                          .Outcome);

            // Save as… into a subfolder: the reference was already relative, so this is the half that
            // needs the OLD base as well as the new one.
            string nested = Path.Combine(root, "copies");
            Directory.CreateDirectory(nested);
            string moved = Path.Combine(nested, "board.crail");

            var reopened = RailDocumentIo.LoadFromFile(crail);
            RailArtwork.RebaseReferences(reopened, crail, moved);
            RailDocumentIo.SaveToFile(moved, reopened);

            Assert.Equal("../board/layout/board.clay", ReadArtworkRef(moved));
            Assert.Equal(
                Path.GetFullPath(clay),
                RailArtwork.Resolve(RailDocumentIo.LoadFromFile(moved), moved, null, new TechnologyCache())
                          .ClayPath);
        }
        finally { Delete(root); }
    }

    // ══ 2 — a conductor with no drawing layer ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>An inner conductor with no drawing layer is offered, disabled, and the reason names the
    /// remedy</b> rather than being left out of the list in silence.
    /// </summary>
    [Fact]
    public void AConductorWithNoDrawingLayerIsListedDisabled_AndTheProposalSaysWhatToAttach()
    {
        var tech = TwoOuterLayersAndAnUnmappedGroundPlane();

        var vm = new RailRfViewModel(OneRail(), null) { PostToUi = a => a() };
        vm.Board = new RailBoardInputs { Shapes = [], Technology = tech };

        var unmapped = Assert.Single(vm.ReferenceLayerOptions, o => !o.IsSelectable);
        Assert.Equal("GND", unmapped.Name);
        Assert.Contains(RailRfViewModel.NoDrawingLayer, unmapped.ToString(), StringComparison.Ordinal);

        // The two outer layers are still there and still choosable.
        Assert.Equal(2, vm.ReferenceLayerOptions.Count(o => o.IsSelectable));

        // The sentence does not claim the stackup marks no ground — it marks one — and it names the
        // conductor and the button that fixes it.
        Assert.Contains("'GND'", vm.ReferenceProposalReason, StringComparison.Ordinal);
        Assert.Contains("Edit Technology", vm.ReferenceProposalReason, StringComparison.Ordinal);
        Assert.DoesNotContain("marks no conductor", vm.ReferenceProposalReason, StringComparison.Ordinal);

        // And it cannot be taken: its key is `default`, and confirming it would point the rail at a
        // layer 0/0 the board does not have.
        vm.SelectedReferenceLayer = unmapped;
        vm.ConfirmReferenceCommand.Execute(null);

        Assert.False(vm.IsReferenceConfirmed);
        Assert.Null(vm.SelectedRail!.ReferenceLayer);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The reported stackup's shape: top and bottom copper mapped, and an inner ground
    /// plane added afterwards that no drawing layer was attached to.</summary>
    private static Technology TwoOuterLayersAndAnUnmappedGroundPlane()
    {
        var tech = new Technology { Name = "board" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(11, 0), Name = "Top Copper" });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0),  Name = "Bottom Copper" });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(3, 0),  Name = "gnd" });

        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Top Copper", DrawingLayers = [new LayerKey(11, 0)],
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "GND", IsGroundReference = true,
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Bottom Copper", DrawingLayers = [new LayerKey(1, 0)],
        });
        return tech;
    }

    private static RailDocument OneRail()
    {
        var doc = new RailDocument { Name = "board" };
        doc.Rails.Add(new RailSpec { Name = "+3V3", NetName = "+3V3" });
        return doc;
    }

    private static string ReadArtworkRef(string crail)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(crail));
        return json.RootElement.GetProperty("ArtworkCellRef").GetString()!;
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-rail-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
