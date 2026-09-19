// Saving a .crail (owner, 2026-09-19) — the gate on a window that had never written one.
//
// View-model driven, RailWindowTests' own shape: the writing itself is RailDocumentIo's and is
// already gated; what is new here is that the window can REACH it, that the dirty mark says the
// truth, and that a saved document comes back the way it was left.

using System;
using System.IO;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailSaveTests
{
    /// <summary>
    /// The two commands hand the window its one decision — save here, or ask where — and do no
    /// writing of their own.
    /// </summary>
    /// <remarks>
    /// <b>The hook is the whole seam.</b> Saving needs a file picker and a top level; this view
    /// model has neither, which is the same split <c>PostToUi</c> draws. A command that wrote a file
    /// itself would be a second writer and a view model no test could construct without a host.
    /// </remarks>
    [Fact]
    public void SaveAndSaveAs_AskTheWindow_AndDifferOnlyInThatOneFlag()
    {
        var vm = new RailRfViewModel();
        var asked = new System.Collections.Generic.List<bool>();
        vm.SaveRequested = saveAs => asked.Add(saveAs);

        vm.SaveCommand.Execute(null);
        vm.SaveAsCommand.Execute(null);

        Assert.Equal([false, true], asked);
    }

    /// <summary>
    /// A document opened off disk is clean, an edit marks it, and writing it clears the mark — with
    /// the panel state going to the file and coming back.
    /// </summary>
    /// <remarks>
    /// <b>The clean-on-open half is the one that would rot quietly.</b> The mark is a comparison
    /// against the bytes a save would produce, so anything that touches the document while the
    /// window is opening it — resolving a reference, seeding a selection — would show up as a
    /// window that comes up already claiming unsaved work, and a mark that is always on is a mark
    /// nobody reads.
    /// </remarks>
    [Fact]
    public void OpenedClean_EditedDirty_SavedCleanAgain_AndThePanelsSurvive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-rail-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "board.crail");
            RailDocumentIo.SaveToFile(path, new RailDocument { Name = "board" });

            var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(path), path);
            Assert.False(vm.IsDirty);
            Assert.DoesNotContain("•", vm.Title);

            vm.ToggleBoardCommand.Execute(null);
            Assert.True(vm.IsDirty);
            Assert.Contains("•", vm.Title);

            // What the window's hook does, with the picker taken out of it.
            RailDocumentIo.SaveToFile(path, vm.Document);
            vm.NoteSaved(path);
            Assert.False(vm.IsDirty);
            Assert.DoesNotContain("•", vm.Title);

            var reopened = new RailRfViewModel(RailDocumentIo.LoadFromFile(path), path);
            Assert.False(reopened.ShowBoard);
            Assert.True(reopened.ShowParts);
            Assert.True(reopened.PartsFillsBoardColumn);
            Assert.False(reopened.IsDirty);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
