// ================================================================
//  DesignerRound21CrashTests.cs — designer feedback round 21: the Data Display copy crash
//
//  Copying a plot on Windows ended the process twice: Copy writes the clipboard and then re-reads it to
//  decide whether Paste is enabled, and Avalonia's OLE wrapper threw a COMException ("EnumFormatEtc
//  failed") out of that read. A clipboard the platform refuses to read now reads as one holding nothing
//  pasteable (ClipboardRead): Paste stays enabled, as it is whenever a reader is wired, and adds nothing. Also here: the mixed wBond clipboard parser refuses foreign JSON without
//  throwing, and the crash-recovery offer's Later no longer discards what it offered.
// ================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DesignerRound21CrashTests
{
    private static DisplayWindowViewModel MakeVm()
    {
        var vm = new DisplayWindowViewModel();
        vm.DataSourceLibrary.ResultsRootProvider     = () => "";
        vm.DataSourceLibrary.KnownTouchstoneProvider = () => Array.Empty<string>();
        return vm;
    }

    [Fact]
    // Paste is enabled whenever a clipboard reader is wired and reads the clipboard when it runs (the plot paste
    // between displays), so an unreadable clipboard no longer shows as a disabled Paste: it shows as a Paste that adds
    // nothing. The crash this round fixed is the claim, and it is held both ways.
    public async Task Copy_WhenTheClipboardCannotBeRead_DoesNotThrow_AndPastePastesNothing()
    {
        var vm = MakeVm();
        string? written = null;
        vm.SetRichCopyAction((containers, _) =>
        {
            written = PlotExporter.BuildContainersJson(containers);
            return Task.CompletedTask;
        });
        vm.SetGetClipboardTextAction(() => throw new COMException("EnumFormatEtc failed"));

        await vm.InvokeCopyAsync();
        Assert.NotNull(written);

        int plots = vm.DataDisplay!.Plots.Count;
        await vm.InvokePasteAsync();   // a refused read is "nothing to paste", not a crash
        Assert.Equal(plots, vm.DataDisplay.Plots.Count);
    }

    [Fact]
    public async Task ClipboardRead_AnswersTheFallback_WhenThePlatformThrows()
    {
        Assert.Null(await ClipboardRead.Try<string?>(() => throw new COMException("EnumFormatEtc failed"), null));
        Assert.Null(await ClipboardRead.TextAsync(null));
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Layout\":[1,2]}")]
    [InlineData("{\"Marker\":\"circuitrf/wbond-mixed-clipboard-v1\",\"Layout\":[1,2]}")]
    public void MixedWBondClipboard_RefusesForeignJson_WithoutThrowing(string text)
    {
        Assert.False(WBondMixedClipboard.TryParse(text, out _));
        Assert.Equal((text, text), WBondMixedClipboard.Unwrap(text));
    }

    [Fact]
    public void RecoveryOffer_LaterKeepsThePriorSessions()
    {
        Assert.False(RecoveryManager.AnswerSettlesOffer(SaveChangesResult.Cancel));
        Assert.True(RecoveryManager.AnswerSettlesOffer(SaveChangesResult.Save));
        Assert.True(RecoveryManager.AnswerSettlesOffer(SaveChangesResult.DontSave));
    }
}
