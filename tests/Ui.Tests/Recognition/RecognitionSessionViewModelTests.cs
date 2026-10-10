// brief-artsch-8-gui-command.md §4, brief-img-5-dialog.md §4 — the dialog's shared session, headless. The AS-8 cases,
// moved here unchanged when the body was extracted (RecognitionSessionViewModel): the artwork-cell option only where
// that cell has no schematic; a replace target relabels the button; a table edit survives a re-run an option change
// caused; an unstated placement origin holds recognition until it is chosen, with nothing pre-selected; and Create
// hands the CLI's entry point the CLI's options (a recording runner stands in for the recognition). Then the same for
// a picture source: Create calls the CLI's entry point with the CLI's options.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Engine;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class RecognitionSessionViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crf-as8-{Guid.NewGuid():N}");

    public RecognitionSessionViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>Stands in for the recognition: two parts, the held edits laid over them by the CSV's own reader, and
    /// every call recorded.</summary>
    private sealed class RecordingRunner : IArtworkRecognitionRunner
    {
        public List<RecognitionInput> Previews { get; } = [];
        public List<(RecognitionInput Input, RecognitionTarget Target, RecognitionRunOptions Options)> Runs { get; } = [];

        private static PartsTable Board() => new(
        [
            new PartRow { Refdes = "C1", Kind = PartKind.C, Connection = PartConnection.Series, Variable = "C1_C" },
            new PartRow { Refdes = "R1", Kind = PartKind.R, Connection = PartConnection.Shunt, Value = 50 },
        ], CircuitRF.Design.RailRf.RailLengthFormat.Dbu, null);

        private RecognitionResult Result(RecognitionInput input)
        {
            var table = Board();
            if (input.PartsCsvText is { } csv) table = PartsTableCsv.Read(csv, table).Table!;
            return new RecognitionResult(null, new RecognitionReport(), null) { Parts = table };
        }

        public RecognitionInput Load(string clayPath) => throw new NotSupportedException();

        public (RecognitionResult Result, RecognitionCircuit? Circuit) Preview(
            RecognitionInput input, RecognitionEmitOptions emit, RunControl? control)
        {
            lock (Previews) Previews.Add(input);
            return (Result(input), null);
        }

        public RecognitionRun Run(RecognitionInput input, RecognitionTarget target, RecognitionRunOptions options, RunControl? control)
        {
            Runs.Add((input, target, options));
            return new RecognitionRun(Result(input), null, null, Path.Combine(Path.GetDirectoryName(input.ClayPath!)!, "x.csch"), null, null);
        }
    }

    private string Cell(string name, CellViews views) => CellCreate.Create(_root, name, views).LayoutPath!;

    private static RecognitionInput Input(string clay) =>
        new() { View = new LayoutView(), Technology = null, Shapes = [], ClayPath = clay };

    private static async Task<CreateSchematicFromArtworkViewModel> Open(string clay, RecordingRunner runner)
    {
        var vm = new CreateSchematicFromArtworkViewModel(Input(clay), [], runner, debounce: TimeSpan.Zero);
        await vm.Recognition;
        return vm;
    }

    [Fact]
    public async Task TheArtworkCellOption_IsOffered_OnlyWhenThatCellHasNoSchematicView()
    {
        var runner = new RecordingRunner();
        var bare = await Open(Cell("Bare", CellViews.Layout), runner);
        var drawn = await Open(Cell("Drawn", CellViews.Layout | CellViews.Schematic), runner);

        Assert.True(bare.ArtworkCellOffered);
        Assert.False(drawn.ArtworkCellOffered);
        Assert.Equal("Bare_model", bare.NewCellName);
    }

    [Fact]
    public async Task ANameThatIsACellThisCommandWrote_RelabelsTheButtonReplace_AndOneItDidNotIsRefused()
    {
        string clay = Cell("Board", CellViews.Layout);
        var written = CellCreate.Create(_root, "Board_model", CellViews.Schematic,
                                        new SchematicEditModel { ArtworkSource = new ArtworkProvenance { Layout = "x.clay" } });
        CellCreate.Create(_root, "Hand", CellViews.Schematic);
        var vm = await Open(clay, new RecordingRunner());

        vm.NewCellName = "Board_model";
        Assert.True(vm.IsReplace);
        Assert.Equal("Replace", vm.CreateButtonText);
        Assert.Contains("checkpoint", vm.ReplaceConfirmText);
        Assert.Equal(RecognitionTargetKind.Replace, vm.Target.Kind);
        Assert.Equal(written.CellDir, vm.Target.CellDir);

        vm.NewCellName = "Hand";
        Assert.False(vm.IsReplace);
        Assert.Equal("Create", vm.CreateButtonText);
        Assert.NotEqual("", vm.NameError);
        Assert.False(vm.CreateCommand.CanExecute(null));
    }

    /// <summary>Designer report (round 15): a value typed into the table, with no re-run since, is in Export Parts….</summary>
    [Fact]
    public async Task ExportParts_CarriesTheEditsNotYetRecognised()
    {
        var vm = await Open(Cell("Board", CellViews.Layout), new RecordingRunner());

        vm.Rows.Single(r => r.Refdes == "C1").ValueText = "10 pF";

        Assert.Null(vm.Table.Row("C1")!.Value);
        Assert.Equal(10.0, vm.EditedTable().Row("C1")!.Value!.Value * 1e12, 9);
    }

    [Fact]
    public async Task ATableEdit_SurvivesTheReRunAnOptionChangeCauses()
    {
        var runner = new RecordingRunner();
        var vm = await Open(Cell("Board", CellViews.Layout), runner);

        vm.Rows.Single(r => r.Refdes == "C1").ValueText = "10 pF";
        vm.ViaIndex = 1;
        await vm.Recognition;

        var last = runner.Previews[^1];
        Assert.Equal(ViaPolicy.Ground, last.Options.Vias);
        Assert.Contains("C1", last.PartsCsvText);
        var c1 = vm.Rows.Single(r => r.Refdes == "C1");
        Assert.Equal("10 pF", c1.ValueText);
        Assert.Equal(10e-12, vm.Table.Row("C1")!.Value!.Value, 15);
        Assert.Null(vm.Table.Row("C1")!.Variable);
        Assert.Equal("50 Ohm", vm.Rows.Single(r => r.Refdes == "R1").ValueText);   // the untouched row is untouched
    }

    [Fact]
    public async Task TheDigitsMenu_ReachesTheEmit_AndChecksItsChoice()
    {
        var vm = await Open(Cell("Board", CellViews.Layout), new RecordingRunner());
        Assert.Equal(RecognitionEmitOptions.DefaultDigits, vm.BuildEmit().Digits);

        vm.SetDigitsCommand.Execute(4);
        Assert.Equal(4, vm.BuildEmit().Digits);
        Assert.Equal(4, Assert.Single(vm.DigitsChoices, c => c.IsChecked).Digits);
        Assert.Equal("4 digits", vm.DigitsLabel);
    }

    [Fact]
    public async Task APlacementFileThatDoesNotStateItsOrigin_HoldsRecognition_UntilOneIsChosen_AndNoneIsPreselected()
    {
        string placement = Path.Combine(_root, "place.csv");
        File.WriteAllText(placement, "# Units: mm\nRef,Footprint,PosX,PosY,Rot,Side\nC1,0402,10.0,10.0,0,top\n");
        var runner = new RecordingRunner();
        var vm = await Open(Cell("Board", CellViews.Layout), runner);
        int before = runner.Previews.Count;

        vm.PlacementPath = placement;
        await vm.Recognition;

        Assert.True(vm.PlacementNeedsOrigin);
        Assert.Equal(-1, vm.PlacementOriginIndex);
        Assert.NotNull(vm.Blocked);
        Assert.Equal(before, runner.Previews.Count);
        Assert.False(vm.CreateCommand.CanExecute(null));

        vm.PlacementOriginIndex = 1;   // the part body's centre
        await vm.Recognition;

        Assert.True(vm.PlacementNeedsOrigin);   // the choice stays on screen
        Assert.Null(vm.Blocked);
        Assert.Equal(PlacementOrigin.BodyCentre, runner.Previews[^1].Placement!.Origin);
        Assert.Null(runner.Previews[^1].Placement!.Refusal);
    }

    [Fact]
    public async Task Create_HandsTheCliEntryPointTheOptionsTheCliWouldBuildFromItsFlags()
    {
        // The dialog's state, and the flags that say the same thing:
        //   --into new:Board_v2 --vias ground --coplanar gcpw --coplanar-factor 2 --stop 3GHz --npts 101
        var runner = new RecordingRunner();
        string clay = Cell("Board", CellViews.Layout);
        var vm = await Open(clay, runner);
        RecognitionRun? created = null;
        vm.Created += run => created = run;

        vm.NewCellName = "Board_v2";
        vm.ViaIndex = 1;
        vm.CoplanarIndex = 2;
        vm.CoplanarFactorText = "2";
        vm.StopText = "3 GHz";
        vm.PointsText = "101";
        await vm.CreateCommand.ExecuteAsync(null);

        var (input, target, options) = Assert.Single(runner.Runs);
        Assert.Equal(RecognitionTarget.NewCell("Board_v2"), target);
        Assert.True(input.Scope.IsWhole);
        Assert.Equal(new RecognitionOptions
        {
            Vias = ViaPolicy.Ground, Coplanar = CoplanarReading.Gcpw, CoplanarGapFactor = 2, TopFrequencyHz = 3e9,
        }, input.Options);

        var expected = RecognitionSweep.Compose(null, null, RecognitionSweep.Parse("3GHz"), 101)!;
        var sweep = options.Emit.Sweep!;
        Assert.Equal((expected.StartExpr, expected.StartUnit, expected.StopExpr, expected.StopUnit, expected.NumPoints),
                     (sweep.StartExpr, sweep.StartUnit, sweep.StopExpr, sweep.StopUnit, sweep.NumPoints));
        Assert.Null(options.Checkpoint);   // the real checkpoint — history checkpoint --intent's function
        Assert.NotNull(created);
    }

    [Fact]
    public async Task CreateFromAPicture_HandsTheCliEntryPointTheOptionsTheCliWouldBuild()
    {
        // The dialog's state, and what the same options are as the trace's own records:
        //   Make Layout, a new cell board_v2, simplify 0.5 px, no 45° snapping, at most 6 colours, a stated scale.
        var runner = new RecordingImageRunner(CircuitRF.Design.Imaging.DrawingKind.Layout)
        {
            TraceAs = input => new CircuitRF.Design.Layout.Recognition.Image.ImageTraceResult
            {
                Report = new RecognitionReport(),
                Scale = new CircuitRF.Design.Layout.Recognition.Image.ImageScaleCandidate(
                    CircuitRF.Design.Layout.Recognition.Image.ImageScaleKind.Stated, 20e-6, 1, "stated"),
            },
        };
        var vm = ImageDialog.Open(_root, runner, ImageDialog.Board(), makeSchematic: false);
        await vm.Recognition;
        CircuitRF.Design.Layout.Recognition.Image.ImageTraceRun? created = null;
        vm.LayoutCreated += run => created = run;

        vm.NewCellName = "board_v2";
        vm.SimplifyText = "0.5";
        vm.Snap45 = false;
        vm.MaxColoursText = "6";
        await vm.Recognition;
        await vm.CreateCommand.ExecuteAsync(null);

        var (input, target) = Assert.Single(runner.TraceRuns);
        Assert.Equal(CircuitRF.Design.Layout.Recognition.Image.ImageTraceTarget.NewCell(Path.GetFullPath(_root), "board_v2"), target);
        Assert.Equal(new CircuitRF.Design.Layout.Recognition.Image.ImageTraceOptions { SimplifyPx = 0.5, Snap45 = false, MaxColours = 6 },
                     input.Options);
        Assert.Same(runner.Technology, input.Technology);
        Assert.Equal(Path.Combine(_root, "board.ctech"), input.TechnologyPath);
        Assert.NotNull(created);
    }
}
