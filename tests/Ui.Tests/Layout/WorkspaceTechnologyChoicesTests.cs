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
}
