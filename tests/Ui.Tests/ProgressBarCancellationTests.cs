// Owner request, 2026-08-19: "I want to add ability to cancel any of these types of operations by
// right-clicking on the Progress bar and selecting 'Cancel' from its context menu. Of course the
// actual computation work only needs to happen on the next work section. Also, any dialog that also
// has a cancel button needs to know that the user cancelled the operation and update its UI
// accordingly. Some operations give two progress bars for the same related calculation operation.
// Both progress bars should offer a Cancel context menu and both should cancel the overall,
// high-level operation that was in flight."
//
// Three separate claims, and they fail in three different ways:
//   1. the bar can stop the run                       — RunCancellation + MessageEntry, here
//   2. TWO bars of one run stop ALL of it, once       — the shared-handle tests, here
//   3. a surface with its own Cancel button KNOWS     — the EM panel and the Compare dialog's
//      it was cancelled from somewhere else             view models, here

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Messages;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ProgressBarCancellationTests
{
    private static readonly Action<Action> Inline = a => a();

    private static (LiveProgressMessage Live, MessageEntry Entry) NewLive(string text = "Running…")
    {
        var entry = new MessageEntry(MessageLevel.Info, text, null, DateTime.Now)
        {
            ProgressIndeterminate = true,
            ProgressPercent       = 0,
        };
        return (new LiveProgressMessage(entry, Inline), entry);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root walking up from this test file.");
        return dir!;
    }

    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    // ── The handle ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_AsksTheRunOnce_HoweverManyTimesItIsPressed()
    {
        int asked = 0;
        var c = new RunCancellation("the run", () => asked++);

        Assert.True(c.CanCancel);
        c.Cancel();
        c.Cancel();
        c.Cancel();

        // Idempotent BECAUSE the same operation is reachable from several surfaces at once — a panel
        // button and two progress bars. Three presses is one request, not three, and the second must
        // not read as a stronger cancel.
        Assert.Equal(1, asked);
        Assert.True(c.IsCancellationRequested);
        Assert.False(c.CanCancel);
    }

    [Fact]
    public void Cancel_AfterTheRunSettled_DoesNothing()
    {
        int asked = 0;
        var c = new RunCancellation("the run", () => asked++);
        c.Finish();

        c.Cancel();

        // The realistic case: a context menu opened while the bar was live, and the run finished
        // before the click landed. Cancelling then must not reach into whatever ran next.
        Assert.Equal(0, asked);
        Assert.False(c.CanCancel);
        Assert.True(c.IsFinished);
    }

    [Fact]
    public void StateChanged_FiresOnBothTransitions_SoEverySurfaceCanReRead()
    {
        var c = new RunCancellation("the run", () => { });
        int raised = 0;
        c.StateChanged += (_, _) => raised++;

        c.Cancel();
        c.Finish();

        Assert.Equal(2, raised);
    }

    // ── The row ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARowWithNoOperationBound_OffersADisabledCancel_WithTheReasonInTheTooltip()
    {
        var (_, entry) = NewLive();

        // Present but disabled, rather than absent: a menu whose contents change shape between
        // right-clicks is harder to learn than one that greys out — and the tooltip has to say why,
        // which is the rule this repo has already been given twice.
        Assert.False(entry.CanCancelRun);
        Assert.Contains("cannot be stopped", entry.CancelTooltip);
    }

    [Fact]
    public void BindCancellation_PutsTheOperationOnTheRow_AndTheRowRepaintsAsItChanges()
    {
        var (live, entry) = NewLive();
        var c = new RunCancellation("the EM run 'MLin'", () => { });

        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        live.BindCancellation(c);

        Assert.Same(c, entry.Cancellation);
        Assert.True(entry.CanCancelRun);
        Assert.Contains("the EM run 'MLin'", entry.CancelTooltip);
        Assert.Contains(nameof(MessageEntry.CanCancelRun), changed);

        // And the row follows the handle without being told again: the menu item is bound to
        // CanCancelRun, so a stop asked for on ANOTHER surface has to grey this one out too.
        changed.Clear();
        c.Cancel();

        Assert.False(entry.CanCancelRun);
        Assert.Contains("Already stopping", entry.CancelTooltip);
        Assert.Contains(nameof(MessageEntry.CanCancelRun), changed);
        Assert.Contains(nameof(MessageEntry.CancelTooltip), changed);
    }

    [Fact]
    public void ARunThatFinishes_LeavesNothingToCancel()
    {
        var (live, entry) = NewLive();
        var c = new RunCancellation("the run", () => { });
        live.BindCancellation(c);

        c.Finish();

        Assert.False(entry.CanCancelRun);
        Assert.Contains("already finished", entry.CancelTooltip);
    }

    // ── Two bars, one operation (the owner's third claim) ────────────────────────────────────────

    [Fact]
    public void TwoRowsOfOneRun_ShareOneHandle_SoEitherBarStopsTheWholeRun()
    {
        int asked = 0;
        var c = new RunCancellation("the EM run", () => asked++);

        var (sweep, sweepEntry) = NewLive("EM 'MLin'");
        var (stage, stageEntry) = NewLive("EM 'MLin' — starting");
        sweep.BindCancellation(c);
        stage.BindCancellation(c);

        // Right-click the STAGE row's bar — the inner one, which is drawing a sub-step rather than the
        // sweep. It must stop the whole computation, not the step it happens to be showing.
        stageEntry.Cancellation!.Cancel();

        Assert.Equal(1, asked);

        // And the OTHER bar knows immediately, without a second observation arriving: the two rows are
        // two views of one run, so one of them still offering to stop it would be a lie.
        Assert.False(sweepEntry.CanCancelRun);
        Assert.False(stageEntry.CanCancelRun);

        // Cancelling from the second bar as well is still ONE request.
        sweepEntry.Cancellation!.Cancel();
        Assert.Equal(1, asked);
    }

    // ── The surfaces that also carry a Cancel button ─────────────────────────────────────────────

    [Fact]
    public void TheEmPanel_SaysTheStopWasTaken_AndStopsOfferingItAgain()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        int asked = 0;
        vm.CancelRequested = () => asked++;
        vm.IsRunning = true;

        Assert.True(vm.CancelSimulateCommand.CanExecute(null));
        Assert.Equal("Cancel", vm.CancelButtonText);

        // A stop asked for ANYWHERE — this button, or either of the run's two bars in the Messages
        // panel — puts the panel in the pending state. Cancellation lands at a work boundary, so a
        // full-wave run keeps going for tens of seconds afterwards; a button still reading "Cancel"
        // through all of that is what makes a user press it again.
        vm.IsCancelling = true;

        Assert.Equal("Cancelling…", vm.CancelButtonText);
        Assert.False(vm.CancelSimulateCommand.CanExecute(null));
    }

    [Fact]
    public void TheEmPanelsMeshCancel_BehavesTheSameWay()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        vm.CancelMeshRequested = () => { };
        vm.IsMeshing = true;

        Assert.True(vm.CancelMeshCommand.CanExecute(null));

        vm.IsCancelling = true;

        Assert.Equal("Cancelling…", vm.CancelButtonText);
        Assert.False(vm.CancelMeshCommand.CanExecute(null));
    }

    [Fact]
    public void TheCompareDialog_SaysTheStopWasTaken_WhereverItCameFrom()
    {
        var vm = new CircuitRF.Ui.WBond.WBondMomCompareViewModel(new CircuitRF.WBond.WBondDesign());

        Assert.Equal("Run", vm.RunButtonText);
        Assert.False(vm.CancelRunCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.Equal("Cancel", vm.RunButtonText);
        Assert.True(vm.CancelRunCommand.CanExecute(null));
        Assert.True(vm.IsRunButtonEnabled);

        // The stop can arrive from this dialog's button, from its own bar's right-click, or from either
        // Messages row behind it — all one RunCancellation — so the label and the enablement follow the
        // STATE rather than the click handler that happened to fire.
        vm.IsCancelling = true;
        Assert.Equal("Cancelling…", vm.RunButtonText);
        Assert.False(vm.IsRunButtonEnabled);
        Assert.False(vm.CancelRunCommand.CanExecute(null));
    }

    /// <summary>
    /// harmonicaRF's grid bar. Its solve is cancelled through the pool, which raises NOTHING for a
    /// cancelled job (that silence is what keeps latest-wins cheap on a drag) — so the view model has
    /// to settle its own "solving" state here rather than waiting to be told.
    /// </summary>
    [Fact]
    public void TheHarmonicaBar_StopsTheSolve_KeepsTheFrame_AndSaysSo()
    {
        var vm = new CircuitRF.Ui.Harmonica.HarmonicaViewModel();
        var shown = vm.Frame;

        vm.IsSolving     = true;
        vm.IsSolvingGrid = true;

        vm.CancelSolve();

        Assert.False(vm.IsSolving);
        Assert.False(vm.IsSolvingGrid);          // the bar goes away
        Assert.Same(shown, vm.Frame);            // the contours already on screen are still the truth
        Assert.Equal("solve cancelled", vm.StatusMessage);

        // The next frame supersedes the notice — a line claiming a cancel over a freshly solved frame
        // would contradict what is on screen.
        vm.PublishFrame(shown);
        Assert.NotEqual("solve cancelled", vm.StatusMessage);
    }

    [Fact]
    public void TheHarmonicaBar_CancelOnAnIdleDocument_DoesNothing()
    {
        var vm = new CircuitRF.Ui.Harmonica.HarmonicaViewModel();

        vm.CancelSolve();

        Assert.Null(vm.CancelNotice);
    }

    // ── The XAML: every progress bar in the application carries the menu ─────────────────────────

    [Theory]
    [InlineData("src/Ui/Views/Dialogs/WBondMomCompareDialog.axaml")]         // the modal's own bar
    [InlineData("src/Ui/Views/Harmonica/HarmonicaView.axaml")]               // harmonicaRF's solving bar
    public void AStandaloneProgressBar_CarriesItsOwnCancelContextMenu(string relative)
    {
        var xaml = Read(relative);

        Assert.Contains("<ProgressBar", xaml);
        Assert.Contains("ProgressBar.ContextMenu", xaml);
        Assert.Contains("Header=\"Cancel\"", xaml);

        // Declared ONCE in XAML and owned by the framework. A menu built per right-click is the
        // stacking bug this codebase has already fixed three times (LayoutCanvas, PlotControl, and
        // the message row's own Copy menu).
        Assert.Equal(1, CountOccurrences(xaml, "<ProgressBar.ContextMenu>"));
    }

    /// <summary>
    /// Owner report, 2026-08-19: "the Copy All Messages context menu interferes with the progress bar
    /// cancel context menu."
    ///
    /// <para>A Messages row's bar is an <c>InlineUIContainer</c> INSIDE the row's
    /// <c>SelectableTextBlock</c>, which already owns the Copy menu — so a second ContextMenu on the
    /// bar puts two menus on one right-click and which one opens depends on which control the hit test
    /// resolved to. The row's text and its bar are ONE target and get ONE menu, with Cancel at the top,
    /// shown only while the row is live.</para>
    /// </summary>
    [Fact]
    public void AMessagesRow_HasExactlyOneContextMenu_AndCancelIsInIt()
    {
        var xaml = Read("src/Ui/Views/Messages/MessagesView.axaml");

        Assert.DoesNotContain("<ProgressBar.ContextMenu>", xaml);
        Assert.Equal(1, CountOccurrences(xaml, "<ContextMenu>"));

        Assert.Contains("Header=\"Cancel\"", xaml);
        Assert.Contains("OnCancelRunClick", xaml);

        // Hidden on an ordinary message — an ordinary row's menu is exactly the two Copy items it has
        // always been — and disabled, with the reason in its tooltip, once there is nothing to stop.
        Assert.Contains("IsVisible=\"{Binding HasProgress}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanCancelRun}\"", xaml);
        Assert.Contains("ToolTip.Tip=\"{Binding CancelTooltip}\"", xaml);
    }

    /// <summary>
    /// The regression that started this: the wirebond Touchstone export handed the kernel
    /// <c>CancellationToken.None</c>, so the 3-D wire MoM solve checked a token at every work boundary
    /// that could never be cancelled — and the export, which has no window of its own once the options
    /// dialog closes, could not be stopped from the UI at all.
    /// </summary>
    [Fact]
    public void TheTouchstoneExport_RunsOnARealTokenSource_NotCancellationTokenNone()
    {
        var src = StripComments(Read("src/Ui/WBond/WBondPublishCommands.cs"));

        Assert.DoesNotContain("CancellationToken.None", src);
        Assert.Contains("new CancellationTokenSource()", src);
        Assert.Contains("new RunCancellation(", src);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// Comments are stripped before a source scan, or a note ABOUT the thing being forbidden fails the
    /// test that forbids it — the trap H8's own source-scan gate recorded.
    /// </summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
            }
            else sb.Append(src[i]);
        }
        return sb.ToString();
    }
}
