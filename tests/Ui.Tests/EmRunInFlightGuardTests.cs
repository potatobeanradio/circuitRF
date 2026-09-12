using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request, 2026-09-11: an EM analysis in flight must not be destroyed — or silently orphaned —
/// by File ▸ New Workspace, File ▸ Close Workspace or File ▸ Quit. The user is warned, told what the
/// gesture costs, and offered a second window where one makes sense.
///
/// <para><b>Why a source scan for the wiring half.</b> <c>WorkspaceViewModel</c> constructs fine
/// headless, but the commands under test all open a modal <c>Window</c>, which this suite has no
/// platform for — the same constraint every menu/dialog phase here has hit. What actually has to hold
/// is that no workspace-replacing command reaches its destructive work without consulting the guard
/// first, and that is a property of the source. The sentence-building half is a pure function and is
/// tested directly.</para>
/// </summary>
public class EmRunInFlightGuardTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md walking up from this test file).");
        return dir!;
    }

    private static string ReadStripped(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        // Comments only. A guard described in a doc-comment and never called is exactly the failure
        // this test exists to catch, so nothing may be credited to prose.
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^[ \t]*///.*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[ \t]*//.*$",  "", RegexOptions.Multiline);
        return text;
    }

    /// <summary>The body of a method, from its signature to the matching closing brace.</summary>
    private static string MethodBody(string source, string signatureFragment)
    {
        int start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signatureFragment}' in the source.");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"Could not find the body of '{signatureFragment}'.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[start..(i + 1)];
        }

        Assert.Fail($"Unbalanced braces after '{signatureFragment}'.");
        return "";
    }

    // ---- The wording ---------------------------------------------------------

    [Fact]
    public void OneRunReadsAsOneSentence()
        => Assert.Equal("the EM run 'Filter'",
                        WorkspaceViewModel.DescribeEmWorkInFlight(["the EM run 'Filter'"]));

    [Fact]
    public void TwoPiecesOfWorkAreJoinedWithAnd()
        => Assert.Equal("the EM run 'Filter' and the mesh of 'Board'",
                        WorkspaceViewModel.DescribeEmWorkInFlight(
                            ["the EM run 'Filter'", "the mesh of 'Board'"]));

    [Fact]
    public void ThreePiecesOfWorkKeepTheFinalAnd()
        => Assert.Equal("a, b and c", WorkspaceViewModel.DescribeEmWorkInFlight(["a", "b", "c"]));

    [Fact]
    public void NothingInFlightDescribesNothing()
        => Assert.Equal("", WorkspaceViewModel.DescribeEmWorkInFlight([]));

    // ---- The wiring ----------------------------------------------------------

    /// <summary>
    /// Every command that REPLACES or closes this window's workspace. Each entry names the method and
    /// the destructive call it must not reach unasked — so the test fails if a guard is deleted AND if
    /// a new path to the same destruction is added without one.
    /// </summary>
    public static TheoryData<string, string> WorkspaceReplacingCommands() => new()
    {
        { "private async Task NewWorkspace(Window? owner)",        "WorkspaceCreate.Create" },
        { "private async Task OpenWorkspace(Window? owner)",       "SwitchToWorkspaceReporting" },
        { "private async Task OpenRecentWorkspace(string? cwsPath)", "SwitchToWorkspaceReporting" },
        { "private async Task CloseWorkspace()",                   "ResetToBlankShell" },
        { "private async Task SaveWorkspaceAs(Window? owner)",     "SwitchToWorkspaceReporting" },
    };

    [Theory]
    [MemberData(nameof(WorkspaceReplacingCommands))]
    public void EveryWorkspaceReplacingCommandAsksBeforeItDestroys(string signature, string destructiveCall)
    {
        var body = MethodBody(ReadStripped("src/Ui/ViewModels/WorkspaceViewModel.cs"), signature);

        int guard = body.IndexOf("ConfirmDespiteEmWork", StringComparison.Ordinal);
        Assert.True(guard >= 0,
            $"'{signature}' replaces the workspace without asking about an EM analysis in flight.");

        int destroys = body.IndexOf(destructiveCall, StringComparison.Ordinal);
        Assert.True(destroys >= 0, $"'{signature}' no longer calls {destructiveCall} — update this test.");
        Assert.True(guard < destroys,
            $"'{signature}' calls {destructiveCall} before it asks about the EM analysis.");
    }

    /// <summary>
    /// Unarchive and Rename are the same hazard reached by a different gesture — both reopen the
    /// workspace, and Rename MOVES the folder the run is going to write into.
    /// </summary>
    [Theory]
    [InlineData("private async Task UnarchiveWorkspace")]
    [InlineData("public async Task RenameWorkspaceAsync")]
    public void ReopeningGesturesAskToo(string signatureFragment)
    {
        var source = ReadStripped("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("ConfirmDespiteEmWork", MethodBody(source, signatureFragment));
    }

    /// <summary>
    /// File ▸ Quit and the window's own close box both route through <c>ConfirmCloseAsync</c>, which is
    /// the one place either can be stopped — and the <c>quitting</c> flag is what makes the two say
    /// different things, since quitting ENDS the run while a window close only orphans it.
    /// </summary>
    [Fact]
    public void ClosingTheWindowAndQuittingBothAsk()
    {
        var window = ReadStripped("src/Ui/Views/WorkspaceWindow.axaml.cs");

        var confirm = MethodBody(window, "internal async Task<bool> ConfirmCloseAsync(bool quitting");
        Assert.Contains("IsEmWorkInFlight",      confirm);
        Assert.Contains("ConfirmDespiteEmWork",  confirm);
        Assert.Contains("Quit anyway",           confirm);

        // The clean-exit fast path must not skip it: a .cem saved before Simulate was pressed is not
        // dirty, so HasAnyDirtyWork alone would wave a run of hours straight through.
        var closing = MethodBody(window, "protected override async void OnClosing(WindowClosingEventArgs e)");
        Assert.Contains("!_vm.IsEmWorkInFlight && !_vm.HasAnyDirtyWork()", closing);

        // Quit asks EVERY window before closing ANY of them, so it must pass the flag through.
        var app = ReadStripped("src/Ui/App.axaml.cs");
        Assert.Contains("ConfirmCloseAsync(quitting: true)", MethodBody(app, "private async Task QuitAsync"));
    }

    /// <summary>
    /// The run that was allowed to carry on must not open a Data Display over another workspace's
    /// results in whatever workspace the window has moved to — the boundary the project tree is built
    /// on. It says where the file went instead.
    /// </summary>
    [Fact]
    public void ARunThatOutlivesItsWorkspaceOpensNoDataDisplayInTheNewOne()
    {
        var body = MethodBody(ReadStripped("src/Ui/ViewModels/WorkspaceViewModel.cs"),
                              "private async Task RunEmSetupAsync(EmSetupEditorViewModel vm)");

        Assert.Contains("var owningWorkspace = CurrentWorkspacePath;", body);

        int check = body.IndexOf("string.Equals(CurrentWorkspacePath, owningWorkspace", StringComparison.Ordinal);
        Assert.True(check >= 0, "RunEmSetupAsync no longer compares the run's workspace with the window's.");

        int autoOpen = body.IndexOf("AutoOpenOrCreateDataDisplayAsync", StringComparison.Ordinal);
        Assert.True(check < autoOpen,
            "The Data Display is opened before the workspace is checked, so a run that outlived its "
            + "workspace would open a document over another workspace's results.");
    }

    /// <summary>
    /// The run and the mesh both have to be recorded, and both have to be un-recorded on EVERY exit —
    /// a descriptor left behind by a failed run would warn about a run that ended minutes ago, forever.
    /// </summary>
    [Theory]
    [InlineData("private async Task RunEmSetupAsync(EmSetupEditorViewModel vm)")]
    [InlineData("private async Task MeshEmSetupAsync(EmSetupEditorViewModel vm)")]
    public void InFlightWorkIsRecordedAndAlwaysReleased(string signature)
    {
        var body = MethodBody(ReadStripped("src/Ui/ViewModels/WorkspaceViewModel.cs"), signature);

        Assert.Equal(1, Regex.Matches(body, @"_emWorkInFlight\.Add\(").Count);
        Assert.Equal(1, Regex.Matches(body, @"_emWorkInFlight\.Remove\(").Count);

        // The release must sit in a finally, not on the happy path: this method has four early
        // returns and two catch blocks between the two calls.
        var finallyBody = MethodBody(body[body.IndexOf("_emWorkInFlight.Add(", StringComparison.Ordinal)..],
                                     "finally");
        Assert.Contains("_emWorkInFlight.Remove(", finallyBody);
    }
}
