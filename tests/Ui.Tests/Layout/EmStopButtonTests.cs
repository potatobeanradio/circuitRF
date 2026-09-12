// ================================================================
//  EmStopButtonTests.cs — the EM panel's running button is STOP, and Cancel is its context menu.
//
//  Owner request, 2026-09-11: "change the Cancel button in the EM Setup to be Stop while the
//  simulation is running, and make it stop rather than cancel. If the user right-clicks on the Stop
//  button, a context menu appears with a Cancel menu."
//
//  The two are different promises, not two words for one thing: Stop finishes the run now and KEEPS
//  every point already solved; Cancel throws the run away and writes nothing. The button a user
//  reaches for mid-sweep is almost always the first — an EM sweep is the one operation here where
//  "I have seen enough" is ordinary — so that is the one on the button, and the destructive one is
//  a right-click away.
//
//  The view is SCANNED rather than measured, for the reason EmSetupHeaderShrinksTests gives: this
//  test project has no Avalonia platform. What is asserted is the wiring a regression would undo.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public sealed class EmStopButtonTests
{
    private const string View = "src/Ui/Views/Layout/EmSetupEditorView.axaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static XElement Button(string name)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), View));
        var el = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Button" &&
                                         (string?)e.Attribute(XName.Get("Name", "")) == name);
        Assert.True(el is not null, $"{View} has no Button named {name}");
        return el!;
    }

    // ── The view ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRunningButton_IsBoundToStop_NotCancel()
    {
        var b = Button("StopSimulateButton");

        Assert.Equal("{Binding ViewModel.StopSimulateCommand}", (string?)b.Attribute("Command"));
        Assert.Equal("{Binding ViewModel.IsRunning}",           (string?)b.Attribute("IsVisible"));

        // Its label is the STOP text, which is the one that can say "Stopping…".
        var label = b.Descendants().First(e => e.Name.LocalName == "TextBlock");
        Assert.Equal("{Binding ViewModel.StopButtonText}", (string?)label.Attribute("Text"));
    }

    [Fact]
    public void RightClickingIt_OffersCancel()
    {
        var menu = Button("StopSimulateButton")
            .Descendants().Where(e => e.Name.LocalName == "MenuItem").ToList();

        var cancel = Assert.Single(menu);
        Assert.Equal("Cancel", (string?)cancel.Attribute("Header"));
        Assert.Equal("{Binding ViewModel.CancelSimulateCommand}", (string?)cancel.Attribute("Command"));
    }

    /// <summary>
    /// Meshing keeps its Cancel. There is no partial mesh worth keeping, so a "Stop" there would be
    /// a second word for the same act — and the value of this whole distinction is that the two
    /// words mean different things.
    /// </summary>
    [Fact]
    public void TheMeshButton_StillSaysCancel()
    {
        var b = Button("CancelMeshButton");
        Assert.Equal("{Binding ViewModel.CancelMeshCommand}", (string?)b.Attribute("Command"));

        var label = b.Descendants().First(e => e.Name.LocalName == "TextBlock");
        Assert.Equal("{Binding ViewModel.CancelButtonText}", (string?)label.Attribute("Text"));
    }

    // ── The view model ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stop_CallsTheHostsStop_AndSaysSoUntilTheRunEnds()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        int stopped = 0, cancelled = 0;
        vm.StopRequested   = () => stopped++;
        vm.CancelRequested = () => cancelled++;

        vm.IsRunning = true;
        Assert.True(vm.StopSimulateCommand.CanExecute(null));
        Assert.Equal("Stop", vm.StopButtonText);

        vm.StopSimulateCommand.Execute(null);
        Assert.Equal(1, stopped);
        Assert.Equal(0, cancelled);

        // The host answers by setting the pending state — a stop lands at a work boundary, so the
        // button has to say the press was taken rather than sit there still offering itself.
        vm.IsStopping = true;
        Assert.Equal("Stopping…", vm.StopButtonText);
        Assert.False(vm.StopSimulateCommand.CanExecute(null));
    }

    /// <summary>
    /// Cancel is still reachable while a stop is pending, and it OUTRANKS it — so the button says
    /// "Cancelling…", because that is what is now going to happen to the run.
    /// </summary>
    [Fact]
    public void CancelStaysAvailableAfterAStop_AndTheLabelSaysWhichOneWon()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        vm.StopRequested   = () => { };
        int cancelled = 0;
        vm.CancelRequested = () => cancelled++;

        vm.IsRunning  = true;
        vm.IsStopping = true;

        Assert.True(vm.CancelSimulateCommand.CanExecute(null));
        vm.CancelSimulateCommand.Execute(null);
        Assert.Equal(1, cancelled);

        vm.IsCancelling = true;
        Assert.Equal("Cancelling…", vm.StopButtonText);
        Assert.False(vm.CancelSimulateCommand.CanExecute(null));
        Assert.False(vm.StopSimulateCommand.CanExecute(null));
    }

    /// <summary>Nothing to stop when nothing is running, and a stray press after the run ends is a
    /// no-op rather than a throw — the host clears the delegate in its finally.</summary>
    [Fact]
    public void NoRun_NoStop()
    {
        var vm = EmProgressFixtures.NewSetupVm();
        Assert.False(vm.StopSimulateCommand.CanExecute(null));

        vm.IsRunning = true;
        vm.StopRequested = null;
        vm.StopSimulateCommand.Execute(null);   // must not throw
    }
}
