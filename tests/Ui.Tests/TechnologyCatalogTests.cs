// ================================================================
//  TechnologyCatalogTests.cs — Settings > Technology, below the firewall.
//
//  The feature's claim is that a user's own .ctech becomes an ordinary member of the list a new
//  workspace is created from — offered by the dialog, accepted by `circuitrf new workspace --tech`,
//  eligible to be the default — and that removing one cannot damage a design already made with it.
//  Every one of those is a property of TechnologyCatalog, which is why they are asserted here rather
//  than through the tab: the tab owns no rule, and a rule that lived there would be a rule the
//  headless verb did not have.
//
//  Each test redirects the per-user state directory, so none of them reads or writes the developer's
//  own technologies or preferences — and the class is in UserStateDirectoryCollection, because that
//  pointer is a process-global with one slot and a concurrent class moving it is what turns "the
//  technology I just installed" into "not installed". See that collection's own note.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(UserStateDirectoryCollection.Name)]
public sealed class TechnologyCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-techcat-" + Guid.NewGuid().ToString("N")[..12]);

    public TechnologyCatalogTests()
    {
        Directory.CreateDirectory(_root);
        CircuitRF.Ui.AppDataRoot.RedirectTo(Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>A .ctech on disk, named <paramref name="stem"/>, carrying <paramref name="name"/> as
    /// its own authored name — written through the same writer the technology editor saves with.</summary>
    private string AuthorTech(string stem, string name)
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        tech.Name = name;

        string path = Path.Combine(_root, stem + ".ctech");
        File.WriteAllText(path, TechPersistence.Serialize(tech));
        return path;
    }

    // ── What is offered ───────────────────────────────────────────────────────

    /// <summary>
    /// With nothing installed the catalog IS the shipped list — same ids, same order. The feature
    /// adds a rung to the resolution chain; it does not change what a fresh installation offers.
    /// </summary>
    [Fact]
    public void WithNothingInstalledTheCatalogIsExactlyTheShippedList()
    {
        Assert.Equal(
            ShippedTechnologies.All.Select(e => e.Id).ToList(),
            TechnologyCatalog.All.Select(e => e.Id).ToList());

        Assert.All(TechnologyCatalog.All, e => Assert.False(e.IsUserInstalled));
    }

    /// <summary>
    /// An installed technology is an ordinary member of the list: it carries its OWN authored name
    /// (which is what the picker displays), its file stem as the id (which is what a workspace
    /// records), and it sorts with the rest rather than into a section of its own.
    /// </summary>
    [Fact]
    public void AnInstalledTechnologyJoinsTheListWithItsOwnNameAndItsFileStemAsTheId()
    {
        var result = TechnologyCatalog.Install(AuthorTech("aaa-house-6layer", "House 6-Layer"));
        Assert.Null(result.Refusal);

        var entry = Assert.Single(TechnologyCatalog.All, e => e.Id == "aaa-house-6layer");
        Assert.Equal("House 6-Layer", entry.Name);
        Assert.True(entry.IsUserInstalled);

        // Sorted by id with the rest — "aaa-" puts it first, which a separate user section would not.
        Assert.Equal("aaa-house-6layer", TechnologyCatalog.All[0].Id);
        Assert.Equal(ShippedTechnologies.All.Count + 1, TechnologyCatalog.All.Count);
    }

    /// <summary>
    /// The install goes through the technology READER, so a file that is not one is refused at the
    /// moment it is added rather than at the moment somebody creates a workspace with it — and
    /// nothing is copied.
    /// </summary>
    [Fact]
    public void AFileThatIsNotATechnologyIsRefusedAndNothingIsCopied()
    {
        string junk = Path.Combine(_root, "not-really.ctech");
        File.WriteAllText(junk, "{ this is not a technology");

        var result = TechnologyCatalog.Install(junk);

        Assert.Null(result.Entry);
        Assert.NotNull(result.Refusal);
        Assert.DoesNotContain(TechnologyCatalog.All, e => e.Id == "not-really");
        Assert.False(File.Exists(Path.Combine(TechnologyCatalog.UserDirectory, "not-really.ctech")));
    }

    /// <summary>
    /// <b>An id already in the catalog is refused, and the refusal names the remedy.</b> Shadowing is
    /// the tempting alternative: the id is what a <c>.cws</c>, a <c>--tech</c> argument and the
    /// default preference all record, so letting one id mean different bytes on two machines makes a
    /// design disagree with itself. Both directions are checked — a shipped id and an installed one —
    /// because they are the same rule and only one of them is obvious.
    /// </summary>
    [Fact]
    public void AnIdAlreadyInTheCatalogIsRefusedWhicheverSideItCollidesWith()
    {
        string shippedId = ShippedTechnologies.DefaultId;

        var overShipped = TechnologyCatalog.Install(AuthorTech(shippedId, "Mine, Not circuitRF's"));
        Assert.Null(overShipped.Entry);
        Assert.Contains(shippedId, overShipped.Refusal!, StringComparison.Ordinal);

        // The shipped one is untouched — still shipped, still its own name.
        var stillShipped = Assert.Single(TechnologyCatalog.All, e => e.Id == shippedId);
        Assert.False(stillShipped.IsUserInstalled);
        Assert.NotEqual("Mine, Not circuitRF's", stillShipped.Name);

        Assert.Null(TechnologyCatalog.Install(AuthorTech("house", "House A")).Refusal);
        var overMine = TechnologyCatalog.Install(AuthorTech("house", "House B"));
        Assert.Null(overMine.Entry);
        Assert.Contains("house", overMine.Refusal!, StringComparison.Ordinal);
        Assert.Equal("House A", Assert.Single(TechnologyCatalog.All, e => e.Id == "house").Name);
    }

    /// <summary>
    /// A file in the folder that cannot be read is SKIPPED, not thrown: this enumeration is on the
    /// path that opens File ▸ New Workspace and the path that runs <c>new workspace</c>, and a
    /// half-copied file must not be able to stop either.
    /// </summary>
    [Fact]
    public void AnUnreadableFileInTheFolderIsSkippedRatherThanBreakingTheList()
    {
        Assert.Null(TechnologyCatalog.Install(AuthorTech("good", "Good")).Refusal);

        Directory.CreateDirectory(TechnologyCatalog.UserDirectory);
        File.WriteAllText(Path.Combine(TechnologyCatalog.UserDirectory, "broken.ctech"), "{{{");

        var ids = TechnologyCatalog.All.Select(e => e.Id).ToList();
        Assert.Contains("good", ids);
        Assert.DoesNotContain("broken", ids);
    }

    // ── The default ───────────────────────────────────────────────────────────

    /// <summary>
    /// The default is the user's choice when they have made one — INCLUDING when it is one of their
    /// own technologies, which is the case the feature exists for — and circuitRF's own otherwise.
    /// </summary>
    [Fact]
    public void TheDefaultIsWhateverThePreferenceNamesAndTheShippedOneUntilItNamesSomething()
    {
        Assert.Equal(ShippedTechnologies.DefaultId, TechnologyCatalog.DefaultId);

        Assert.Null(TechnologyCatalog.Install(AuthorTech("house", "House 6-Layer")).Refusal);
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.DefaultTechnologyId = "house");

        Assert.Equal("house", TechnologyCatalog.DefaultId);
        Assert.Equal("house", WorkspaceCreate.DefaultTechnologyId);
    }

    /// <summary>
    /// <b>A preference naming a technology that is no longer installed falls back rather than
    /// failing</b> — removing a file must not be able to break File ▸ New Workspace. The preference
    /// is deliberately LEFT naming it, so re-adding the same file restores the choice instead of
    /// having silently forgotten it.
    /// </summary>
    [Fact]
    public void ADefaultThatWasRemovedFallsBackAndComesBackWhenItIsReinstalled()
    {
        string source = AuthorTech("house", "House 6-Layer");
        Assert.Null(TechnologyCatalog.Install(source).Refusal);
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.DefaultTechnologyId = "house");

        Assert.Null(TechnologyCatalog.Uninstall("house"));

        Assert.Equal(ShippedTechnologies.DefaultId, TechnologyCatalog.DefaultId);
        Assert.Equal("house", TechnologyCatalog.PreferredDefaultId());

        Assert.Null(TechnologyCatalog.Install(source).Refusal);
        Assert.Equal("house", TechnologyCatalog.DefaultId);
    }

    // ── Removing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removing takes away the OFFER and nothing else: a workspace created with the technology keeps
    /// its own copy, byte for byte, which is what makes a workspace portable to a machine that never
    /// had the file. A shipped technology cannot be removed at all.
    /// </summary>
    [Fact]
    public void RemovingATechnologyLeavesEveryWorkspaceMadeWithItIntact()
    {
        string source = AuthorTech("house", "House 6-Layer");
        Assert.Null(TechnologyCatalog.Install(source).Refusal);

        var created = WorkspaceCreate.Create(_root, "Amp", "house");
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(created.TechPath!));

        Assert.Null(TechnologyCatalog.Uninstall("house"));

        Assert.DoesNotContain(TechnologyCatalog.All, e => e.Id == "house");
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(created.TechPath!));

        Assert.NotNull(TechnologyCatalog.Uninstall(ShippedTechnologies.DefaultId));
        Assert.Contains(TechnologyCatalog.All, e => e.Id == ShippedTechnologies.DefaultId);
    }

    // ── Creating from one ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>WorkspaceCreate</c> accepts a user technology exactly as it accepts a shipped one, writes
    /// its bytes VERBATIM, and points the <c>.cws</c> at the copy — so from the moment the workspace
    /// exists the two origins are indistinguishable. An unknown id still throws and lists the real
    /// ones, which now includes the installed ones.
    /// </summary>
    [Fact]
    public void AWorkspaceCreatedFromAUserTechnologyIsIndistinguishableFromOneCreatedFromAShippedOne()
    {
        string source = AuthorTech("house", "House 6-Layer");
        Assert.Null(TechnologyCatalog.Install(source).Refusal);

        var created = WorkspaceCreate.Create(_root, "Amp", "house");

        Assert.Equal(Path.Combine(created.WorkspaceDir, "tech", "house.ctech"), created.TechPath);
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(created.TechPath!));
        Assert.Equal(Path.Combine("tech", "house.ctech"),
                     WorkspacePersistence.LoadFromFile(created.CwsPath).DefaultTechRef);

        var thrown = Assert.Throws<ArgumentException>(
            () => WorkspaceCreate.Create(_root, "Other", "no-such-process"));
        Assert.Contains("house", thrown.Message, StringComparison.Ordinal);
    }

    // ── The summary the tab shows ─────────────────────────────────────────────

    /// <summary>
    /// The summary is derived from the technology itself, so it cannot disagree with the file. The
    /// four-layer board is the case worth pinning: its conductor count is what a reader is actually
    /// looking for, and the overall thickness is the SUM of the non-via entries rather than the
    /// second-opinion <c>BoardThicknessDbu</c>.
    /// </summary>
    [Fact]
    public void TheSummaryCountsWhatIsInTheStackupAndAddsUpTheEntriesItself()
    {
        var tech    = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var summary = TechnologySummary.Of(tech);

        Assert.Equal(tech.Name,          summary.Name);
        Assert.Equal(tech.Layers.Count,  summary.DrawingLayers);
        Assert.Equal(4,                  summary.Conductors);
        Assert.True(summary.Dielectrics > 0);

        long expected = tech.Stackup.Layers
            .Where(l => l.Kind != StackupKind.Via)
            .Sum(l => l.ThicknessDbu);
        Assert.Equal(expected, summary.OverallThicknessDbu);
        Assert.NotNull(summary.ThicknessText);

        Assert.Contains("4 conductors", summary.OneLine, StringComparison.Ordinal);
    }

    /// <summary>A technology with no stackup says so rather than reporting a thickness of zero, which
    /// would read as a measurement.</summary>
    [Fact]
    public void ATechnologyWithNoStackupReportsNoThicknessAtAll()
    {
        var summary = TechnologySummary.Of(new Technology { Name = "Bare", DefaultDisplayUnit = LayoutUnit.Um });

        Assert.Null(summary.OverallThicknessDbu);
        Assert.Null(summary.ThicknessText);
        Assert.Contains("no stackup", summary.OneLine, StringComparison.Ordinal);
    }
}
