using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report, 2026-09-21: quitting with a torn-off Smith Chart window in front left the "Unsaved
/// Changes" prompt underneath it, so circuitRF looked like it was refusing to quit. The prompt is a
/// CHILD window of the shell and a child is ordered relative to its own owner, while a torn-off
/// document window is a deliberate PEER — so a peer in front of the shell is in front of the shell's
/// dialogs too. See <c>src/Ui/Views/ModalPromptFront.cs</c>.
///
/// <para>Source-text scans, like every other window-ordering gate in this project: z-order is a
/// platform fact and this suite calls no Avalonia runtime API.</para>
/// </summary>
public class QuitPromptFrontTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    /// <summary>
    /// The OWNER is activated first and the prompt second. Activating the prompt alone cannot lift it
    /// past a peer window — its place in the stack is its owner's place plus one — so the order here
    /// is the fix, not a detail of it. All three prompts that can stand between the user and a quit
    /// take it.
    /// </summary>
    [Fact]
    public void EveryCloseOrQuitPrompt_RaisesItsOwnerGroup_OwnerFirst()
    {
        var front = ReadRepoFile("src/Ui/Views/ModalPromptFront.cs");

        var ownerIdx  = front.IndexOf("owner.Activate();", StringComparison.Ordinal);
        var dialogIdx = front.IndexOf("dialog.Activate();", StringComparison.Ordinal);
        Assert.True(ownerIdx  > 0, "the prompt's owner must be activated");
        Assert.True(dialogIdx > ownerIdx,
                    "the owner must be activated BEFORE the prompt — the prompt alone cannot pass a peer window");

        foreach (var prompt in new[]
                 {
                     "src/Ui/Views/Dialogs/SaveChangesDialog.axaml.cs",   // "Unsaved Changes" — the report
                     "src/Ui/Views/Dialogs/SavePlanDialog.axaml.cs",      // its "Save All" follow-on
                     "src/Ui/Views/Dialogs/EmRunInFlightDialog.cs",       // asked first on the same path
                 })
            Assert.Contains("ModalPromptFront.Attach(", ReadRepoFile(prompt), StringComparison.Ordinal);
    }

    /// <summary>
    /// R-dock-14's raise (floating tool panels rise with the shell) must stand down while a prompt is
    /// open. Those panels are owned by the shell too, so raising them when the shell is activated —
    /// which is exactly what the fix above does — would put them over the prompt and reproduce the
    /// report with a floating Properties panel in place of the Smith Chart.
    /// </summary>
    [Fact]
    public void TheFloatingPanelRaise_StandsDown_WhileAPromptIsOpen()
    {
        var src  = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");
        var i    = src.IndexOf("private void RaiseFloatingToolWindows()", StringComparison.Ordinal);
        Assert.True(i > 0, "RaiseFloatingToolWindows not found");
        var body = src[i..src.IndexOf("\n    protected override", i, StringComparison.Ordinal)];

        var guardIdx = body.IndexOf("ModalPromptFront.HasOpenPrompt(this)", StringComparison.Ordinal);
        var raiseIdx = body.IndexOf("tool.Activate();", StringComparison.Ordinal);
        Assert.True(guardIdx > 0, "the raise must check for an open prompt over this window");
        Assert.True(guardIdx < raiseIdx, "the check must come BEFORE anything is raised");

        // A latch that could stick would switch the raise off for the rest of the session with
        // nothing reported, so the record drops windows that have gone rather than trusting Closed.
        Assert.Contains("_open.RemoveAll(e => e.Dialog.PlatformImpl is null);",
                        ReadRepoFile("src/Ui/Views/ModalPromptFront.cs"), StringComparison.Ordinal);
    }
}
