// Two owner reports from one session (2026-09-19), both about what happens to a railRF window that
// is not the window being looked at.
//
// The first is the native-menu exporter throwing on every activation of a railRF window; the second
// is quitting circuitRF with a dirty `.crail` and never being asked about it. They are unrelated
// faults with one thing in common: railRF is the first STANDALONE window that carries a document and
// a native menu of its own, so both of the application-wide paths that sweep "every other window"
// met a case they were written before.

using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Views.RailRf;
using CircuitRF.Ui.Views.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailWindowQuitAndMenuTests
{
    /// <summary>
    /// A window that declares a <c>NativeMenu</c> of its own keeps it — the workspace's shared menu
    /// is attached only to a window that has none.
    /// </summary>
    /// <remarks>
    /// <b>Owner report:</b> activating a railRF window printed
    /// <c>ArgumentException("The menu being updated does not match.")</c> out of
    /// <c>__MicroComIAvnMenuProxy.Update</c>, then three more of the same off the dispatcher. The
    /// attach is written for a torn-off dock window, which has no menu at all; it returned early only
    /// when the window already held THIS menu, so a window holding its OWN had the workspace's hung
    /// over the top of it — File, View and Window replaced by items bound to another window's view
    /// model, and a second menu handed to an exporter that had already bound one.
    ///
    /// <para>Scanned rather than driven: the method is macOS-gated and takes two real
    /// <c>Window</c>s, so the only thing a headless test can read is the guard. The guard is the
    /// whole fix.</para>
    /// </remarks>
    [Fact]
    public void AWindowThatBroughtItsOwnNativeMenu_IsNotGivenTheWorkspacesOne()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/ViewModels/WorkspaceViewModel.cs"));
        int i = src.IndexOf("private static void AttachSharedNativeMenuIfMacOS", StringComparison.Ordinal);
        Assert.True(i > 0, "AttachSharedNativeMenuIfMacOS not found");

        string body = src[i..];
        int set = body.IndexOf("NativeMenu.SetMenu(tornOffWindow", StringComparison.Ordinal);
        Assert.True(set > 0, "the attach itself must still happen");

        // Ahead of the set: a return on the torn-off window ALREADY having a menu of any kind.
        string ahead = body[..set];
        Assert.Matches(
            new Regex(@"NativeMenu\.GetMenu\(tornOffWindow\) is not null\) return;"),
            ahead);
    }

    /// <summary>
    /// Quitting circuitRF asks every standalone document window before it ends the process.
    /// </summary>
    /// <remarks>
    /// <b>Owner report:</b> circuitRF was quit with a dirty <c>.crail</c> open and went away without
    /// asking. The window's own close prompt was never the problem — it was never reached.
    /// <c>Quit</c> asked every <c>WorkspaceWindow</c> and nothing else, and the last of those closing
    /// runs <c>CloseAllFloatingWindows</c> and <c>Environment.Exit</c> in one dispatcher pass, so the
    /// <c>e.Cancel = true</c> that a railRF close uses to re-issue itself after a modal answer had no
    /// later pass to run on.
    ///
    /// <para>The registration is <see cref="ICrfDocumentWindow"/> and the enumeration is over the
    /// interface, so the next standalone document window is asked for implementing it rather than for
    /// being remembered here.</para>
    /// </remarks>
    [Fact]
    public void QuitAsksEveryStandaloneDocumentWindow_BeforeItClosesAnything()
    {
        Assert.True(typeof(ICrfDocumentWindow).IsAssignableFrom(typeof(RailRfWindow)));
        Assert.True(typeof(ICrfDocumentWindow).IsAssignableFrom(typeof(WBondShellWindow)));

        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/App.axaml.cs"));

        // Collected by interface, never by a list of concrete window types.
        Assert.Contains("OfType<ICrfDocumentWindow>()", src, StringComparison.Ordinal);

        int q = src.IndexOf("private async Task QuitAsync", StringComparison.Ordinal);
        Assert.True(q > 0, "QuitAsync not found");
        string body = src[q..];

        int ask   = body.IndexOf("await d.ConfirmCloseAsync()", StringComparison.Ordinal);
        int close = body.IndexOf("foreach (var w in windows) w.Close();", StringComparison.Ordinal);
        Assert.True(ask > 0, "the standalone documents must be asked");
        Assert.True(close > ask, "…and asked BEFORE anything is closed (MW1 R-mw1-18's two passes)");
    }

    /// <summary>
    /// Confirming is not closing: the window stays on screen, marked clear to close.
    /// </summary>
    /// <remarks>
    /// <b>This is the half that makes the two passes work</b>, and it is the one the old code got
    /// wrong for free by never being on this path: <c>ConfirmCloseAsync</c> used to end in
    /// <c>Close()</c>. A window that closed as it answered would already be gone by the time a LATER
    /// window's prompt was cancelled — the failure MW1 R-mw1-18 fixed for workspace windows,
    /// re-introduced through a different door. The close box keeps its old behaviour through a
    /// wrapper that asks and then closes.
    /// </remarks>
    [Fact]
    public void ConfirmingLeavesTheWindowOpen_TheCloseBoxClosesItThroughItsOwnWrapper()
    {
        foreach (string file in new[]
                 {
                     "src/Ui/Views/RailRf/RailRfWindow.Save.cs",
                     "src/Ui/Views/WBond/WBondShellWindow.axaml.cs",
                 })
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), file));
            int i = src.IndexOf("public async Task<bool> ConfirmCloseAsync()", StringComparison.Ordinal);
            Assert.True(i > 0, file + ": no interface implementation");

            // The method's own body ends at the next member; close enough is the next `/// <summary>`
            // or the class's closing brace, and neither may hold a Close() call.
            int end = src.IndexOf("\n    /// <summary>", i, StringComparison.Ordinal);
            string body = end > i ? src[i..end] : src[i..];
            Assert.DoesNotContain("Close();", body);
            Assert.Contains("_closeConfirmed = true;", body);

            // …and the close box still closes, through the re-issue wrapper.
            Assert.Contains("if (await ConfirmCloseAsync()) Close();", src, StringComparison.Ordinal);
            Assert.Contains("_ = ReissueCloseAsync();", src, StringComparison.Ordinal);
        }
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
