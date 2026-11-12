// ================================================================
//  ChainBudgetWindowTests.cs — the RF Chain Budget window's own behaviour, beside the budget it shows
//
//    - a Filter stage's block draws its band form's glyph, not the band-pass default;
//    - the Frequency field reads and writes the frequency in the window's unit;
//    - Blocks ▸ Copy's page is the lineup plus its padding, and its text names every stage;
//    - the stage list's own auto-scroll is off, so the eased scroll is the only one;
//    - a block dropped in a gap moves its stage there, and a drop beside itself moves nothing;
//    - the levels plots name their nodes (IN and the stages) on the x axis rather than numbering them;
//    - every plot the window shows has a container in its host, which is what a plot's Copy renders;
//    - every edit undoes, back to the clean document; a typed value is one step; Save moves the clean point;
//    - a value the budget refuses never reaches the views: a typed one is flagged and put back on leaving the box, a
//      pick is undone at once — except the S-parameter data part, which needs a file before it can evaluate: it is kept,
//      its File box flagged and a file asked for, and a file picked is written relative to the chain's workspace.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Chain;
using Xunit;

namespace CircuitRF.Ui.Tests.Chain;

public sealed class ChainBudgetWindowTests
{
    private static ChainDesign Chain(params (string Name, string Part, (string, string)[] P)[] stages) => new()
    {
        Stages = [.. stages.Select(s => new ChainStage
        {
            Name = s.Name, Part = s.Part,
            Parameters = s.P.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal),
        })],
    };

    [Theory]
    [InlineData("Bandpass", NetworkForm.Bandpass)]
    [InlineData("Highpass", NetworkForm.Highpass)]
    [InlineData("Lowpass",  NetworkForm.Lowpass)]
    public void AFilterBlock_DrawsItsBandFormsGlyph(string form, NetworkForm expected)
    {
        var vm = new ChainBudgetViewModel(Chain(("F", ChainParts.Filter, [("Form", form)])));

        var glyph = Assert.Single(vm.Stages[0].Glyphs);
        Assert.Same(BuiltInSymbols.PrimitivesForFilter(expected).Primitives, glyph.Artwork);
    }

    [Fact]
    public void TheFrequencyField_IsInTheWindowsUnit_BothWays()
    {
        var vm = new ChainBudgetViewModel(Chain(("A", ChainParts.Amp, [])));
        double scale = vm.FrequencyUnit switch { "Hz" => 1, "kHz" => 1e3, "MHz" => 1e6, _ => 1e9 };

        Assert.Equal(2.45e9 / scale, double.Parse(vm.Frequency, System.Globalization.CultureInfo.InvariantCulture), 9);

        vm.Frequency = (5.8e9 / scale).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(5.8e9, vm.ToDesign().FrequencyHz, 3);
    }

    [Fact]
    public void TheBlocksCopy_IsTheLineupPadded_AndNamesEveryStage()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, []), ("BPF", ChainParts.Filter, []), ("ATT", ChainParts.Atten, [])));
        var blocks = ChainBlocksGraphicExport.BlocksOf(vm);

        var (w, h) = ChainBlocksGraphicExport.Extent(blocks);
        var page = ChainBlocksGraphicExport.PageFor(blocks);
        Assert.Equal(w + 2 * ChainBlocksGraphicExport.PagePad, page.Width, 3);
        Assert.Equal(h + 2 * ChainBlocksGraphicExport.PagePad, page.Height, 3);

        string text = ChainBlocksGraphicExport.TextOf(blocks);
        Assert.All(new[] { "LNA", "BPF", "ATT" }, n => Assert.Contains(n, text, StringComparison.Ordinal));

        var (compose, p) = ChainBlocksGraphicExport.Composition(blocks, CircuitRF.Render.ColorVariant.Light, transparentBackground: false);
        Assert.NotEmpty(CircuitRF.Render.DataDisplay.PlotDocumentWriter.BuildPdfBytes(compose, p));
    }

    [Theory]
    [InlineData(1, 5, "A,C,D,E,B")]   // dropped last
    [InlineData(3, 0, "D,A,B,C,E")]   // dropped first
    [InlineData(1, 4, "A,C,D,B,E")]   // dropped before E
    [InlineData(2, 2, "A,B,C,D,E")]   // its own gap, either side: nothing moves
    [InlineData(2, 3, "A,B,C,D,E")]
    public void ADroppedBlock_MovesItsStageIntoTheGap(int from, int insertBefore, string expected)
    {
        var vm = new ChainBudgetViewModel(Chain([.. "ABCDE".Select(c => (c.ToString(), ChainParts.Amp, Array.Empty<(string, string)>()))]));
        var dragged = vm.Stages[from];
        vm.SelectedStage = dragged;   // the press that starts a drag selects
        vm.IsDirty = false;

        vm.MoveStage(dragged, insertBefore);

        Assert.Equal(expected, string.Join(",", vm.Stages.Select(s => s.Name)));
        Assert.Same(dragged, vm.SelectedStage);
        Assert.Equal(expected != "A,B,C,D,E", vm.IsDirty);
    }

    [Fact]
    public void TheLevelsPlots_NameTheirNodes()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, []), ("BPF", ChainParts.Filter, []), ("ATT", ChainParts.Atten, [])));

        foreach (var plot in new[] { vm.LevelsPlot, vm.LevelsDbPlot })
        {
            var categories = plot!.XCategories();
            Assert.NotNull(categories);
            Assert.Equal(["IN", "LNA", "BPF", "ATT"], categories.Value.Labels);
            Assert.Equal(0, categories.Value.First);
        }
    }

    [Fact]
    public void EveryPlotTheWindowShows_HasAContainer_ForItsCopy()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, []), ("ATT", ChainParts.Atten, [])));

        foreach (var plot in new[] { vm.CascadePlot, vm.LevelsPlot, vm.LevelsDbPlot })
            Assert.Contains(vm.PlotHost.Plots, c => ReferenceEquals(c.PlotVM.Plot, plot));
    }

    private static string Snapshot(ChainBudgetViewModel vm) =>
        string.Join("|", vm.Frequency, vm.BandwidthMHz, vm.Blockers, vm.Demo) + "|" +
        string.Join(";", vm.Stages.Select(st => st.Name + ":" + st.Part.Token + ":" + string.Join(",", st.Parameters.Select(p => p.Name + "=" + p.Value))));

    [Fact]
    public void EveryEdit_Undoes_BackToTheCleanDocument_AndRedoesForward()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, []), ("BPF", ChainParts.Filter, []), ("ATT", ChainParts.Atten, [])));
        string clean = Snapshot(vm);
        Assert.False(vm.IsDirty);
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.Frequency = "5";                                                     // a system input
        vm.Stages[0].Parameters.First(p => p.Name == "Gain").Value = "22";      // a stage's parameter
        vm.Stages[1].Name = "RFBPF";                                           // a stage's name
        vm.Stages[2].Part = ChainParts.Find(ChainParts.Amp)!;                   // a stage's part
        vm.Demo = true;                                                         // the demo
        vm.InsertStage(ChainParts.Find(ChainParts.Mixer)!, 1);                  // insert
        vm.MoveStage(vm.Stages[0], 3);                                          // move
        vm.Remove(vm.Stages[2]);                                                // remove
        string edited = Snapshot(vm);
        Assert.True(vm.IsDirty);

        int steps = 0;
        while (vm.UndoCommand.CanExecute(null)) { vm.UndoCommand.Execute(null); steps++; }
        Assert.Equal(8, steps);
        Assert.Equal(clean, Snapshot(vm));
        Assert.False(vm.IsDirty);

        while (vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null);
        Assert.Equal(edited, Snapshot(vm));
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void ATypedValue_IsOneUndoStep()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, [("Gain", "16")])));
        var gain = vm.Stages[0].Parameters.First(p => p.Name == "Gain");

        foreach (var typed in new[] { "1", "12", "12.", "12.5" }) gain.Value = typed;
        vm.UndoCommand.Execute(null);

        // An undo rebuilds the rows, so the field is read again rather than through the old row.
        Assert.Equal("16", vm.Stages[0].Parameters.First(p => p.Name == "Gain").Value);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async System.Threading.Tasks.Task Save_IsTheCleanPoint_ThatUndoAndRedoCross()
    {
        string path = Path.Combine(Path.GetTempPath(), "crf-chain-undo-" + Guid.NewGuid().ToString("N")[..8] + ChainDesignIo.Extension);
        try
        {
            var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, [])));
            vm.Frequency = "3";
            vm.FilePath = path;
            Assert.True(await vm.Save());
            Assert.False(vm.IsDirty);

            vm.UndoCommand.Execute(null);   // before the save: differs from the file
            Assert.True(vm.IsDirty);
            vm.RedoCommand.Execute(null);   // the saved document again
            Assert.False(vm.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ARefusedTypedValue_KeepsTheLastGoodChain_AndIsPutBackOnLeavingTheBox()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, [("Gain", "16")]), ("ATT", ChainParts.Atten, [])));
        string summary = vm.Summary;
        var budget = vm.Budget;
        var gain = vm.Stages[0].Parameters.First(p => p.Name == "Gain");

        gain.Value = "1x";

        Assert.True(gain.Invalid);
        Assert.Same(budget, vm.Budget);                 // the views keep the last chain that evaluated
        Assert.Equal(summary, vm.Summary);
        Assert.NotEmpty(vm.Refusals);                   // and the reason is shown

        vm.CommitEdits();

        Assert.Equal("16", gain.Value);
        Assert.False(gain.Invalid);
        Assert.Empty(vm.Refusals);
        Assert.False(vm.UndoCommand.CanExecute(null));  // a refused value put back leaves no step behind
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void ARefusedSystemInput_IsPutBackToo()
    {
        var vm = new ChainBudgetViewModel(Chain(("LNA", ChainParts.Amp, [])));
        string freq = vm.Frequency;

        vm.Frequency = "abc";
        Assert.True(vm.FrequencyInvalid);

        vm.CommitEdits();
        Assert.Equal(freq, vm.Frequency);
        Assert.False(vm.FrequencyInvalid);
    }

    [Fact]
    public void ARefusedPick_IsNotMade()
    {
        var vm = new ChainBudgetViewModel(ChainDesignIo.LoadFromFile(Path.Combine(
            CircuitRF.Ui.Tests.Examples.ChainBudgetExampleTests.Workspace, "Array.cchain")));
        var cmb = vm.Stages.First(st => st.Part.Token == ChainParts.Combiner);
        cmb.Parameters.First(p => p.Name == "N").Value = "3";
        vm.CommitEdits();
        Assert.Empty(vm.Refusals);

        // A tree combines in pairs: N = 3 as a tree is refused, so the pick is undone and the reason stays shown.
        cmb = vm.Stages.First(st => st.Part.Token == ChainParts.Combiner);
        cmb.Parameters.First(p => p.Name == "Topology").Value = "Tree";

        Assert.Equal("Direct", vm.Stages.First(st => st.Part.Token == ChainParts.Combiner).Parameters.First(p => p.Name == "Topology").Value);
        Assert.StartsWith("Not applied:", vm.Refusals, StringComparison.Ordinal);
        Assert.NotNull(vm.Budget?.Totals);
    }

    [Fact]
    public void PickingTheSnpPart_KeepsIt_FlagsItsFile_AndAsksForOne()
    {
        string ws = Path.Combine(Path.GetTempPath(), "crf-chainsnp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(ws, "data"));
        try
        {
            File.WriteAllText(Path.Combine(ws, "data", "lna.s2p"), "# GHz S MA R 50\n1 0 0 0.9 0 0.9 0 0 0\n4 0 0 0.9 0 0.9 0 0 0\n");
            var vm = new ChainBudgetViewModel(Chain(("A", ChainParts.Amp, []))) { HostWorkspaceRoot = ws };
            ChainParamViewModel? asked = null;
            vm.FileWanted = p => asked = p;

            vm.Stages[0].Part = ChainParts.Find(ChainParts.Snp)!;

            Assert.Equal(ChainParts.Snp, vm.Stages[0].Part.Token);
            Assert.NotNull(asked);
            Assert.True(asked!.Invalid);
            Assert.Contains("no Touchstone file is named", vm.Refusals);

            asked.Value = vm.FileReference(Path.Combine(ws, "data", "lna.s2p"));
            Assert.Equal("data/lna.s2p", asked.Value);
            Assert.True(vm.Recompute(), vm.Refusals);
            Assert.False(asked.Invalid);
        }
        finally { try { Directory.Delete(ws, true); } catch (IOException) { } }
    }

    [Fact]
    public void TheStageListsOwnAutoScrollIsOff_SoTheEasedScrollIsTheOnlyOne()
    {
        string xaml = File.ReadAllText(Path.Combine(CircuitRF.Ui.Tests.Em3d.PalaceBackendTests.RepoRoot(),
                                                    "src", "Ui", "Views", "Chain", "ChainBudgetWindow.axaml"));
        int list = xaml.IndexOf("Name=\"StageList\"", StringComparison.Ordinal);
        Assert.True(list >= 0);
        Assert.Contains("AutoScrollToSelectedItem=\"False\"", xaml[list..(list + 300)], StringComparison.Ordinal);
    }
}
