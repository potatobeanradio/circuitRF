using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// Owner report, 2026-09-04: the layout editor's "Change Technology…" picker does not offer all the
/// <c>.ctech</c> files in the workspace — it listed the workspace's <c>tech/</c> folder alone. A
/// technology can equally live beside the cell it belongs to, arrive inside an imported cell folder,
/// or come out of an archive; Browse… could always reach those, which is the tell that they were in
/// the workspace all along.
/// </summary>
public sealed class WorkspaceTechnologyChoicesTests : IDisposable
{
    private readonly string _root;

    public WorkspaceTechnologyChoicesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crfTechChoices_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string TechDir => Path.Combine(_root, "tech");

    private string WriteTech(string relativePath, string name)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TechPersistence.SaveToFile(full, new Technology { Name = name });
        return full;
    }

    [Fact]
    public void EveryCtechUnderTheWorkspaceIsOffered_NotOnlyTheOnesInTechSlash()
    {
        WriteTech("tech/house.ctech",              "House");
        WriteTech("cells/amp.ccell/local.ctech",   "Local");
        WriteTech("imported/vendor.ctech",         "Vendor");

        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        Assert.Equal(3, choices.Count);
        Assert.Contains(choices, c => c.Label == "House");
        Assert.Contains(choices, c => c.Label == "Local");
        Assert.Contains(choices, c => c.Label == "Vendor");
    }

    [Fact]
    public void TheTechFolderComesFirst_ItIsStillTheConventionalHome()
    {
        WriteTech("aaa/early.ctech",  "Early");     // sorts before "tech/" alphabetically
        WriteTech("tech/house.ctech", "House");

        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        Assert.Equal("House", choices[0].Label);
        Assert.Equal("Early", choices[1].Label);
    }

    [Fact]
    public void TwoTechnologiesWithTheSameName_AreToldApartByTheirFolder()
    {
        WriteTech("tech/board.ctech",             "Board");
        WriteTech("cells/amp.ccell/board.ctech",  "Board");

        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        Assert.Equal(2, choices.Count);
        // Neither row may be the bare name — an unmakeable choice is worse than a wordy one.
        Assert.DoesNotContain(choices, c => c.Label == "Board");
        Assert.All(choices, c => Assert.StartsWith("Board", c.Label));
        Assert.Contains(choices, c => c.Label.Contains("tech"));
        Assert.Contains(choices, c => c.Label.Contains("amp.ccell"));
    }

    [Fact]
    public void TwoTechnologiesWithTheSameNameInTheSameFolder_AreStillToldApart()
    {
        // Owner report, 2026-09-09: a Save As from the technology editor writes a second .ctech
        // carrying the same internal Name, and the obvious place to put it is beside the original —
        // so disambiguating by FOLDER produced two identical rows again, which is the very state the
        // rule above exists to prevent. The file name is the half that always differs.
        WriteTech("tech/board.ctech",    "Board");
        WriteTech("tech/board-v2.ctech", "Board");

        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        Assert.Equal(2, choices.Count);
        Assert.Equal(2, choices.Select(c => c.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(choices, c => c.Label.Contains("board.ctech", StringComparison.Ordinal));
        Assert.Contains(choices, c => c.Label.Contains("board-v2.ctech", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRowCarriesTheFileItStandsFor_SoTheDialogCanShowThePath()
    {
        // The tooltip the dialog puts on each row is this path (ChangeTechnologyDialog.Row). It is
        // the answer for a UNIQUE row too, where no label carries a path at all.
        string path = WriteTech("tech/only.ctech", "Only");

        var choice = Assert.Single(WorkspaceTechnologyChoices.Enumerate(_root, TechDir));
        Assert.Equal(path, choice.AbsolutePath);
    }

    [Fact]
    public void AUniqueName_IsNotClutteredWithAFolder()
    {
        WriteTech("cells/amp.ccell/only.ctech", "Only");

        var choice = Assert.Single(WorkspaceTechnologyChoices.Enumerate(_root, TechDir));
        Assert.Equal("Only", choice.Label);
    }

    [Fact]
    public void AnUnreadableCtech_FallsBackToItsFilenameRatherThanVanishing()
    {
        File.WriteAllText(Path.Combine(_root, "broken.ctech"), "{ not really json");

        var choice = Assert.Single(WorkspaceTechnologyChoices.Enumerate(_root, TechDir));
        Assert.Equal("broken", choice.Label);
    }

    [Fact]
    public void NoWorkspaceRoot_YieldsNothing_LeavingOnlyWorkspaceDefaultAndBrowse()
    {
        Assert.Empty(WorkspaceTechnologyChoices.Enumerate(null, null));
        Assert.Empty(WorkspaceTechnologyChoices.Enumerate(Path.Combine(_root, "nope"), null));
    }

    // -- Which row the picker opens on (owner report, 2026-09-17) ---------------------------------
    //
    // It opened on "(Workspace default)" for every layout, so the dialog answered its own question
    // wrongly for any layout carrying an explicit TechRef — a Gerber-imported board being the case
    // that made it visible, since the import writes a .ctech beside the cell and points the new
    // .clay at it. Confirming that reading cleared the ref and handed the board the workspace's
    // technology, which in a workspace holding several imports is another board's.

    [Fact]
    public void TheLayoutsOwnTechnologyIsTheRowThePickerOpensOn_NotTheWorkspaceDefault()
    {
        WriteTech("tech/house.ctech", "House");
        string imported = WriteTech("boardB/boardB.ctech", "boardB");

        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        int index = WorkspaceTechnologyChoices.IndexOfCurrent(choices, "../../boardB.ctech", imported);

        Assert.Equal("boardB", choices[index].Label);
    }

    [Fact]
    public void ANullTechRefIsTheWorkspaceDefault_AndSelectsIt()
    {
        string house = WriteTech("tech/house.ctech", "House");
        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        // Even though a technology DID resolve — through the workspace default, which is what a null
        // ref means. The resolved file matching a row must not promote it to an explicit choice.
        Assert.Equal(-1, WorkspaceTechnologyChoices.IndexOfCurrent(choices, null, house));
    }

    [Fact]
    public void ATechnologyOutsideTheWorkspaceMatchesNoRow_SoTheDialogAddsOneRatherThanPreSelecting()
    {
        WriteTech("tech/house.ctech", "House");
        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "vendor.ctech");

        Assert.Equal(-1, WorkspaceTechnologyChoices.IndexOfCurrent(choices, "../vendor.ctech", outside));
    }

    [Fact]
    public void AnUnresolvableTechRefSelectsNoRow_TheCurrentLineAlreadySaysNothingResolved()
    {
        WriteTech("tech/house.ctech", "House");
        var choices = WorkspaceTechnologyChoices.Enumerate(_root, TechDir);

        Assert.Equal(-1, WorkspaceTechnologyChoices.IndexOfCurrent(choices, "../../gone.ctech", null));
    }
}
