// Owner requests, 2026-08-09:
//  (1) "A lot of the Messages after the EM sim have the yellow warning icon. Change those to info.
//       Perhaps some should be the green check mark. You decide."
//  (2) "EM Setup needs a TextEdit box (and Browse button) for the output file name and path… default
//       should be the layout name with .clay replaced by .sNp. See how the Analysis editor does it."

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class EmOutputPathAndMessageLevelTests
{
    private static EmSetupEditorViewModel Vm(string layoutRef = "MLin.clay", string name = "MLin") =>
        new(Path.Combine(Path.GetTempPath(), "unused-out.cem"),
            new EmSetup { Name = name, LayoutRef = layoutRef, AnalysisKind = EmAnalysisKind.Planar });

    // ── (2) the output-file field ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultShown_IsTheLayoutNameWithTheTouchstoneSuffix()
    {
        Assert.Equal("MLin.sNp", Vm("MLin.clay").SnpOutputPlaceholder);
        Assert.Equal("Amp.sNp",  Vm(Path.Combine("cells", "Amp", "layout", "Amp.clay")).SnpOutputPlaceholder);
    }

    [Fact]
    public void TheDefaultIsAPLACEHOLDER_NotPreFilledText()
    {
        // Blank means "follow the layout". Pre-filling would freeze it into a literal the moment
        // anything was renamed — the same trap TechRef = null avoids by meaning "the default".
        var vm = Vm();
        Assert.Equal("", vm.SnpOutputPathText);
        Assert.NotEqual("", vm.SnpOutputPlaceholder);
    }

    [Fact]
    public void WithNoLayoutRef_ThePlaceholderFallsBackToTheSetupName()
    {
        Assert.Equal("MyEm.sNp", Vm(layoutRef: "", name: "MyEm").SnpOutputPlaceholder);
    }

    [Fact]
    public void CommittingAPath_IsUndoable_AndTabbingThroughUnchangedPushesNothing()
    {
        var vm = Vm();
        vm.SnpOutputPathText = "baseline.s2p";
        vm.CommitSnpOutputPath();
        Assert.Equal("baseline.s2p", vm.Working.SnpOutputPathOverride);

        bool couldUndo = vm.UndoRedo.CanUndo;
        vm.CommitSnpOutputPath();                       // same value again
        Assert.Equal(couldUndo, vm.UndoRedo.CanUndo);   // no new entry

        vm.UndoCommand.Execute(null);
        Assert.Equal("", vm.Working.SnpOutputPathOverride);
    }

    [Fact]
    public void EscapeReverts_WithoutTouchingTheModel()
    {
        var vm = Vm();
        vm.SnpOutputPathText = "half-typed";
        vm.RevertSnpOutputPath();

        Assert.Equal("", vm.SnpOutputPathText);
        Assert.Equal("", vm.Working.SnpOutputPathOverride);
    }

    [Fact]
    public void ABrowsedPathInsideTheResultsFolder_IsStoredRelative_SoTheWorkspaceCanMove()
    {
        string root = Path.Combine(Path.GetTempPath(), "crfOut", "results");
        var vm = Vm();
        vm.ResultsRootProvider = () => root;

        string stored = vm.MakeOutputPathRef(Path.Combine(root, "sub", "baseline.s2p"));

        Assert.False(Path.IsPathRooted(stored));
        Assert.Equal("sub/baseline.s2p", stored);
    }

    [Fact]
    public void ABrowsedPathOutsideIt_IsStoredAbsolute_BecauseNoEncodingMakesItPortable()
    {
        string root = Path.Combine(Path.GetTempPath(), "crfOut", "results");
        string away = Path.Combine(Path.GetTempPath(), "elsewhere", "baseline.s2p");
        var vm = Vm();
        vm.ResultsRootProvider = () => root;

        Assert.Equal(away, vm.MakeOutputPathRef(away));
    }

    [Fact]
    public void TheStoredOverride_IsWhatTheRunActuallyWritesTo()
    {
        // The field would be decoration if the run ignored it. Relative resolves against the results
        // root; absolute is used as given; a typed .sNp is not doubled.
        string root = Path.Combine(Path.GetTempPath(), "crfOutRun", "results");

        var relative = new EmSetup { Name = "MLin", LayoutRef = "MLin.clay", SnpOutputPathOverride = "baseline.s2p" };
        Assert.Equal(Path.Combine(root, "baseline"), EmRunService.ResolveSnpBasePath(root, relative));
        Assert.Equal(Path.Combine(root, "baseline") + ".s2p", EmRunService.ResolveSnpPath(root, relative, 2));

        string abs = Path.Combine(Path.GetTempPath(), "elsewhere", "b.s3p");
        var absolute = new EmSetup { Name = "MLin", LayoutRef = "MLin.clay", SnpOutputPathOverride = abs };
        Assert.Equal(abs[..^4], EmRunService.ResolveSnpBasePath(root, absolute));
    }

    [Fact]
    public void BlankFallsBackToTheDefaultKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "crfOutRun", "results");
        var setup = new EmSetup { Name = "MLin", LayoutRef = "MLin.clay" };
        Assert.Equal(Path.Combine(root, "MLin"), EmRunService.ResolveSnpBasePath(root, setup));
    }

    // ── (1) message levels ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheResultCarriesThreeChannels_SoTheIconCanMatchWhatTheReaderMustDo()
    {
        // The bug was one list doing all three jobs: the engine's descriptive output, real warnings,
        // and outright write failures all came out with the yellow icon. A channel that warns about
        // everything teaches people to ignore it.
        var r = new EmRunResult(EmRunStatus.Ok, null, null, null, null, null, null,
                                Warnings: ["a stale .snp"],
                                Notes:    ["Full-wave planar was chosen because…"],
                                Errors:   ["The .snp could not be written"]);

        Assert.Single(r.Notes!);
        Assert.Single(r.Warnings);
        Assert.Single(r.Errors!);
    }

    [Fact]
    public void NotesAndErrors_DefaultToNull_SoEveryExistingConstructionSiteStillCompiles()
    {
        // Additive: the host reads them as `?? []`.
        var r = new EmRunResult(EmRunStatus.Refused, null, null, null, null, null, "no", []);
        Assert.Null(r.Notes);
        Assert.Null(r.Errors);
        Assert.Empty(r.Notes ?? []);
    }
}

/// <summary>
/// The host's own routing. <c>WorkspaceViewModel</c> cannot be constructed headlessly (its ctor
/// stands up a Dock layout and posts to the UI thread), so the call site is pinned by source scan —
/// the same fallback this suite already uses for other view-model-only wiring.
/// </summary>
public class EmMessageLevelRoutingSourceTests
{
    private static string ReadRepoFile(string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string RunEmSetupBody()
    {
        string src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        int start = src.IndexOf("private async Task RunEmSetupAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "RunEmSetupAsync not found — was it renamed?");
        int end = src.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        if (end < 0) end = src.Length;
        return src[start..end];
    }

    [Fact]
    public void EachChannelIsPostedAtItsOwnLevel_NeverAllAsWarnings()
    {
        string body = RunEmSetupBody();
        Assert.Contains("result.Notes ?? [])    Messages.Info(", body, StringComparison.Ordinal);
        Assert.Contains("result.Warnings)       Messages.Warning(", body, StringComparison.Ordinal);
        Assert.Contains("result.Errors ?? [])   Messages.Error(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrittenArtifactIsReportedAsASuccess_NotMerelyInfo()
    {
        // The green check is what says "this run produced something you can open".
        string body = RunEmSetupBody();
        Assert.Contains("Messages.Success(\"Wrote s-parameters\"", body, StringComparison.Ordinal);
        Assert.Contains("Messages.Success(\"Wrote results\"", body, StringComparison.Ordinal);
    }
}
