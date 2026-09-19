// The four panel lamps (owner, 2026-09-19) — "a way for a user to toggle which panes within railRF
// are being viewed, so some panes can quickly and temporarily occupy more space".
//
// RailWindowTests' own shape: the MEANING is on the view model, which needs no application host,
// and the part that is genuinely chrome — that the buttons exist, that they are LEFT of Open with a
// rule between them, and that each is lit by the property it toggles — is a scan of the AXAML,
// because that is where those decisions live.

using System;
using System.IO;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailPaneToggleTests
{
    /// <summary>
    /// All four panels are on screen to begin with, and the two derived predicates follow the
    /// toggles — including the case the owner called out by name: with the board AND the parts
    /// table off, the centre column is gone entirely and the results panel is what takes the width.
    /// </summary>
    [Fact]
    public void FourPanels_StartShown_AndTheCentreColumnGoesOnlyWhenBothOfItsPanelsDo()
    {
        var vm = new RailRfViewModel();

        Assert.True(vm.ShowSpecification);
        Assert.True(vm.ShowBoard);
        Assert.True(vm.ShowParts);
        Assert.True(vm.ShowResults);
        Assert.True(vm.ShowBoardColumn);
        Assert.False(vm.PartsFillsBoardColumn);

        // The parts table alone still needs the column — and it takes the height the board released
        // rather than staying at its cap.
        vm.ToggleBoardCommand.Execute(null);
        Assert.True(vm.ShowBoardColumn);
        Assert.True(vm.PartsFillsBoardColumn);

        // Both off: nothing is left in the centre, so the column itself goes.
        vm.TogglePartsCommand.Execute(null);
        Assert.False(vm.ShowBoardColumn);
        Assert.False(vm.PartsFillsBoardColumn);

        // The board back on its own does NOT make the parts table fill anything.
        vm.ToggleBoardCommand.Execute(null);
        Assert.True(vm.ShowBoardColumn);
        Assert.False(vm.PartsFillsBoardColumn);
    }

    /// <summary>
    /// The last showing panel cannot be hidden, and its button says so by being disabled rather
    /// than by doing nothing when pressed.
    /// </summary>
    /// <remarks>
    /// <b>Both halves.</b> A window with all four hidden has a toolbar, a status strip and nothing
    /// between them — reachable in four clicks, and escapable only by recognising four dark buttons
    /// as the way out. And a button that can be pressed and does nothing is indistinguishable from
    /// one that is broken, which is why this is a <c>CanExecute</c> and not a silent guard: the
    /// Command binding dims the button itself.
    /// </remarks>
    [Fact]
    public void TheLastShowingPanelCannotBeHidden_AndItsButtonIsDisabledRatherThanInert()
    {
        var vm = new RailRfViewModel();

        // With four showing, every one of them can be hidden.
        Assert.True(vm.ToggleSpecificationCommand.CanExecute(null));
        Assert.True(vm.ToggleBoardCommand.CanExecute(null));
        Assert.True(vm.TogglePartsCommand.CanExecute(null));
        Assert.True(vm.ToggleResultsCommand.CanExecute(null));

        vm.ToggleSpecificationCommand.Execute(null);
        vm.ToggleBoardCommand.Execute(null);
        vm.TogglePartsCommand.Execute(null);

        // Results is the only one left: its button is dimmed, the other three are not — they turn
        // their own panel back ON.
        Assert.False(vm.ToggleResultsCommand.CanExecute(null));
        Assert.True(vm.ToggleSpecificationCommand.CanExecute(null));
        Assert.True(vm.ToggleBoardCommand.CanExecute(null));
        Assert.True(vm.TogglePartsCommand.CanExecute(null));

        // And the setter refuses it too, for a caller that is not the button.
        vm.ShowResults = false;
        Assert.True(vm.ShowResults);

        // A hand-edited document that hides everything opens with everything, rather than with a
        // window nobody can see anything in.
        var all = RailDocumentIo.Deserialize("""
            {
              "FormatVersion": 1,
              "Panels": { "ShowSpecification": false, "ShowBoard": false,
                          "ShowParts": false, "ShowResults": false }
            }
            """);
        Assert.True(all.Panels.AllShown);
    }

    /// <summary>
    /// The four buttons are the toolbar's leftmost group, in the window's own left-to-right order,
    /// separated from the document buttons by a rule — and each is lit by the property it toggles.
    /// </summary>
    /// <remarks>
    /// <b>The ORDER is the feature.</b> A strip of four identical-sized glyphs is read by position,
    /// and the positions are the panels' own: specification, board, parts, results. The rule is what
    /// says the group means something different from the three beside it — these change what is
    /// shown, those act on the document.
    /// </remarks>
    [Fact]
    public void TheFourButtonsAreLeftOfOpen_InPanelOrder_EachLitByItsOwnProperty()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml"));

        int spec    = Index(xaml, "Name=\"SpecificationPaneButton\"");
        int board   = Index(xaml, "Name=\"BoardPaneButton\"");
        int parts   = Index(xaml, "Name=\"PartsPaneButton\"");
        int results = Index(xaml, "Name=\"ResultsPaneButton\"");
        int rule    = xaml.IndexOf("<Border Width=\"1\" VerticalAlignment=\"Stretch\"", StringComparison.Ordinal);
        int open    = Index(xaml, "Name=\"OpenButton\"");

        Assert.True(spec < board && board < parts && parts < results,
                    "The four panel buttons are not in the window's own left-to-right panel order.");
        Assert.True(results < rule && rule < open,
                    "The panel buttons are not separated from the document buttons by a vertical rule left of Open.");

        foreach ((string button, string property, string command) in new[]
        {
            ("SpecificationPaneButton", "ShowSpecification", "ToggleSpecificationCommand"),
            ("BoardPaneButton",         "ShowBoard",         "ToggleBoardCommand"),
            ("PartsPaneButton",         "ShowParts",         "TogglePartsCommand"),
            ("ResultsPaneButton",       "ShowResults",       "ToggleResultsCommand"),
        })
        {
            string block = xaml[Index(xaml, $"Name=\"{button}\"")..];
            block = block[..block.IndexOf("</Button>", StringComparison.Ordinal)];

            Assert.Contains($"Classes.ToolActive=\"{{Binding {property}}}\"", block);
            Assert.Contains($"Command=\"{{Binding {command}}}\"", block);
        }

        // And the panels themselves are gated on the same four facts — the half a button with no
        // reader would be.
        Assert.Contains("Name=\"SpecificationPane\"", xaml);
        Assert.Contains("IsVisible=\"{Binding ShowSpecification}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding ShowBoardColumn}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding ShowBoard}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding ShowParts}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding ShowResults}\"", xaml);
    }

    /// <summary>
    /// The four toggles are the DOCUMENT's, they survive a round trip through the <c>.crail</c>,
    /// and a file that says nothing about panels opens with all four shown.
    /// </summary>
    /// <remarks>
    /// <b>The absent case is the load-bearing half.</b> Every <c>.crail</c> written before panels
    /// existed says nothing about them, and so does every one nobody has collapsed a panel in — a
    /// default of false anywhere in that chain would open those documents with a panel missing and
    /// nothing on screen to say why. The block is therefore written only when something is hidden,
    /// which is also what keeps an untouched document byte-identical in revision control.
    /// </remarks>
    [Fact]
    public void ThePanelsRoundTripThroughTheCrail_AndAbsentMeansAllFourShown()
    {
        var doc = new RailDocument { Name = "board" };
        Assert.True(doc.Panels.AllShown);

        // Nothing hidden: the file does not mention panels at all.
        Assert.DoesNotContain("\"Panels\"", RailDocumentIo.Serialize(doc));

        // A document that says nothing opens with all four — including one from before the block
        // existed, which is what this hand-written JSON stands for.
        var bare = RailDocumentIo.Deserialize("""{ "FormatVersion": 1, "Name": "board" }""");
        Assert.True(bare.Panels.ShowSpecification);
        Assert.True(bare.Panels.ShowBoard);
        Assert.True(bare.Panels.ShowParts);
        Assert.True(bare.Panels.ShowResults);

        // Hidden panels are written, and they come back.
        var vm = new RailRfViewModel(doc, null);
        vm.ToggleSpecificationCommand.Execute(null);
        vm.ToggleResultsCommand.Execute(null);

        string json = RailDocumentIo.Serialize(doc);
        Assert.Contains("\"Panels\"", json);

        var reopened = new RailRfViewModel(RailDocumentIo.Deserialize(json), null);
        Assert.False(reopened.ShowSpecification);
        Assert.True(reopened.ShowBoard);
        Assert.True(reopened.ShowParts);
        Assert.False(reopened.ShowResults);

        // And showing them again takes the block back out, so a document put back the way it was is
        // the bytes it was.
        reopened.ToggleSpecificationCommand.Execute(null);
        reopened.ToggleResultsCommand.Execute(null);
        Assert.DoesNotContain("\"Panels\"", RailDocumentIo.Serialize(reopened.Document));
    }

    private static int Index(string xaml, string needle)
    {
        int i = xaml.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{needle} is not in the window's AXAML.");
        return i;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
