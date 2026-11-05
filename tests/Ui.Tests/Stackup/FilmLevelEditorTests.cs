// TFR-V R-tfrv-6 — a level with no metal in the Technology editor: its card, its gestures and its bytes.

using CircuitRF.Design.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.StackupRender;

public sealed class FilmLevelEditorTests : IDisposable
{
    private static readonly LayerKey Film = new(4, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-filmeditor-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Copy()
    {
        Directory.CreateDirectory(_root);
        string from = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag-via", "tech");
        foreach (string f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(_root, Path.GetFileName(f)));
        return Path.Combine(_root, "zigzag-via-GaAs.ctech");
    }

    private TechEditorViewModel Editor(string path)
        => new(path, TechPersistence.LoadOwnFromFile(path), p => new TechnologyCache().LoaderFor(p));

    private static StackupLayerRowViewModel Row(TechEditorViewModel vm, string name)
        => vm.StackupLayers.Single(r => r.Layer.Name == name);

    /// <summary>Gate 11: saving a level the editor only opened writes what it read, with no Material or σ gained.</summary>
    [Fact]
    public void AFilmLevelSavedFromTheEditorRoundTrips()
    {
        string path = Copy();
        var once = Editor(path);
        once.SaveCommand.Execute(null);
        byte[] first = File.ReadAllBytes(path);
        var again = Editor(path);
        again.SaveCommand.Execute(null);
        Assert.Equal(first, File.ReadAllBytes(path));

        var level = TechPersistence.LoadOwnFromFile(path).Stackup.Layers.Single(l => l.Name == "TFR Level");
        Assert.Null(level.Material);
        Assert.Equal(0.0, level.SigmaSm);
    }

    [Fact]
    public void TheCardShowsWhatTheLevelHas()
    {
        var vm = Editor(Copy());
        var level = Row(vm, "TFR Level");
        Assert.False(level.ShowsMetalFields);
        Assert.False(level.ShowsMaterial);
        Assert.False(level.SigmaNeedsValue);
        Assert.Equal("Film level", level.KindLabel);
        Assert.False(level.FilmRows.Single().ShowsFaceAndThickness);
        Assert.True(Row(vm, "Metal1").ShowsMetalFields);
    }

    /// <summary>A drag to the other face has nothing to mean here and writes nothing, not even an undo entry.</summary>
    [Fact]
    public void TheFaceGestureWritesNothingOnAFilmLevel()
    {
        var vm = Editor(Copy());
        Assert.False(vm.SetFilmFace(Film, ConductorSheetSurface.Top));
        Assert.Null(ResistiveFilms.Of(vm.Working, Film)!.At);
    }

    /// <summary>A move onto a metal level makes it overlap-contact, which is what that move means.</summary>
    [Fact]
    public void MovingAFilmOntoMetalMakesItOverlapContact()
    {
        var vm = Editor(Copy());
        Assert.True(vm.MoveFilm(Film, "Metal1"));
        Assert.Same(vm.Working.Stackup.Layers.Single(l => l.Name == "Metal1"), ResistiveFilms.ConductorOf(vm.Working, Film));
    }

    /// <summary>Checking a drawing layer makes it an ordinary metal level, whose card is a conductor's again.</summary>
    [Fact]
    public void AddingMetalMakesItAMetalLevel()
    {
        var vm = Editor(Copy());
        vm.Working.Stackup.Layers.Single(l => l.Name == "TFR Level").DrawingLayers.Add(new LayerKey(1, 0));
        Assert.True(Row(vm, "TFR Level").ShowsMetalFields);
    }

    /// <summary>＋ Film Level, one edit: the entry and its film, with no Material or σ, on a layer the picker offered.</summary>
    [Fact]
    public void AddingAFilmLevelIsOneEdit()
    {
        var vm = Editor(Copy());
        Assert.Equal(TechEditorViewModel.NewFilmLayerChoice, vm.FilmLayerPickerRows()[^1]);
        Assert.DoesNotContain("Resistor", vm.FilmLayerPickerRows());
        Assert.DoesNotContain("TFR Contact", vm.FilmLevelInsertTargets().Where(n => vm.Working.Stackup.Layers.Single(l => l.Name == n).Kind == StackupKind.Via));

        Assert.True(vm.AddFilmLevel("GaAs", null, "Resistor Hi", 500));
        var level = vm.Working.Stackup.Layers.Single(l => l.Name == "TFR Level 2");
        Assert.True(ResistiveFilms.IsFilmLevel(level));
        Assert.Null(level.Material);
        Assert.Equal(0.0, level.SigmaSm);
        Assert.Equal(vm.Working.Stackup.Layers.FindIndex(l => l.Name == "GaAs") + 1, vm.Working.Stackup.Layers.IndexOf(level));

        vm.UndoCommand.Execute(null);
        Assert.DoesNotContain(vm.Working.Stackup.Layers, l => l.Name == "TFR Level 2");
        Assert.DoesNotContain(vm.Working.Layers, l => l.Name == "Resistor Hi");
    }

    /// <summary>The σ field's unit is a display choice: the entry stays in S/m.</summary>
    [Fact]
    public void TheConductivityUnitIsADisplayChoice()
    {
        var vm = Editor(Copy());
        var metal = Row(vm, "Metal1");
        metal.SelectedSigmaUnit = "MS/m";
        Assert.Equal("4.1e+1", metal.SigmaInUnit);
        metal.SigmaInUnit = "30";
        metal.CommitSigmaSm();
        Assert.Equal(30e6, Row(vm, "Metal1").Layer.SigmaSm, 1e-3);
    }
}
