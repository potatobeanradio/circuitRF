using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using SkiaSharp;
using Xunit;

// See the note at the top of StackupSceneTests.cs on why the namespace is not `…Tests.Stackup`.
namespace CircuitRF.Ui.Tests.StackupRender;

/// <summary>brief-stackup-render-9-resistor-films.md's gate: a film on its conductor, defined, drawn, selected, dragged.</summary>
public class StackupFilmTests
{
    private const string GaAs = "mmic-GaAs_2LM_100um";
    private static readonly LayerKey Resistor = new(4, 0);
    private const double PaneWidth = 900;

    private static TechEditorViewModel Editor(Technology? tech = null) =>
        new(Path.Combine(Path.GetTempPath(), "stackup-film-tests.ctech"), tech ?? ShippedTechnologies.Load(GaAs));

    /// <summary>The shipped technology with its film's optional values stated too.</summary>
    private static Technology Stated()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var film = ResistiveFilms.Of(tech, Resistor)!;
        film.ThicknessDbu = 50;
        film.At = ConductorSheetSurface.Top;
        return tech;
    }

    private static StackupCanvas Canvas(TechEditorViewModel vm)
    {
        var canvas = new StackupCanvas { ViewModel = vm };
        canvas.MeasureForWidth(PaneWidth);
        return canvas;
    }

    private static StackupScene SceneOf(StackupCanvas canvas)
    {
        canvas.MeasureForWidth(PaneWidth);     // a committed edit dropped the scene; a layout pass rebuilds it
        return canvas.SceneCache.Current!;
    }

    private static string Json(TechEditorViewModel vm) => TechPersistence.Serialize(vm.Working);

    private static string Repo()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    /// <summary>A point on the film's strip clear of any barrel, so it hit-tests to the film.</summary>
    private static Point OnStrip(StackupScene scene, LayerKey key)
    {
        var rect = scene.FilmRectOf(key)!.Value;
        for (float x = rect.Left + 2; x < rect.Right - 2; x += 2)
            if (scene.HitTest(x, rect.MidY) is { Kind: StackupHitKind.Film } hit && hit.FilmLayer == key)
                return new Point(x, rect.MidY);
        throw new InvalidOperationException("no point of the strip hit-tests to it");
    }

    private static Point OnBand(StackupScene scene, string name, Func<SKRect, float> y)
    {
        var rect = scene.Bands.First(b => b.Name == name).Rect;
        return new Point(rect.Left + 12, y(rect));
    }

    // ── R-stk9-0: the model ───────────────────────────────────────────────────────────────────────

    /// <summary>R-stk9-0, R-stk9-0b — films round-trip and stay out of DrawingLayers; a level with none writes nothing.</summary>
    [Fact]
    public void AFilmRoundTripsOnItsConductorAndAnEmptyListIsOmitted()
    {
        var tech = Stated();
        var copy = TechPersistence.Clone(tech);
        var film = ResistiveFilms.Of(copy, Resistor)!;
        Assert.Equal((50.0, (long?)50, ConductorSheetSurface.Top), (film.SheetResistanceOhmPerSq, film.ThicknessDbu, film.At!.Value));
        Assert.DoesNotContain(Resistor, ResistiveFilms.ConductorOf(copy, Resistor)!.DrawingLayers);

        ResistiveFilms.ConductorOf(tech, Resistor)!.Films!.Clear();
        string json = TechPersistence.Serialize(tech);
        Assert.DoesNotContain("\"Films\"", json);
        Assert.DoesNotContain("SheetResistanceOhmPerSq", json);
    }

    /// <summary>R-stk9-0b — the three legacy shapes a file written before the move can have.</summary>
    [Theory]
    [InlineData("bound", "Metal1")]
    [InlineData("unbound", "Metal1")]
    [InlineData("no-conductor", null)]
    public void ALegacyLayerFieldMovesToALevelOnLoad(string shape, string? level)
    {
        var tech = ShippedTechnologies.Load(GaAs);
        ResistorFilmBinding.Remove(tech, Resistor);
        var metal1 = tech.Stackup.Layers.First(l => l.Name == "Metal1");
        if (shape == "bound") metal1.DrawingLayers.Add(Resistor);
        if (shape == "no-conductor") tech.Stackup.Layers.Clear();

        // The old spelling, written by hand: nothing in the writer can produce it now.
        string keyed = TechPersistence.Serialize(tech);
        int at = keyed.IndexOf("\"Name\": \"Resistor\"", StringComparison.Ordinal);
        string legacy = keyed.Insert(at, "\"SheetResistanceOhmPerSq\": 25, \"FilmThicknessDbu\": 60, \"FilmAt\": \"Bottom\", ");

        var loaded = TechPersistence.Deserialize(legacy);
        if (level is null)
        {
            Assert.Null(ResistiveFilms.Of(loaded, Resistor));
            Assert.Contains(TechValidation.Analyze(loaded), p => p.Id == ResistorFilmBinding.Ids.NoLevel && p.Message.Contains("Resistor"));
        }
        else
        {
            var film = ResistiveFilms.Of(loaded, Resistor)!;
            Assert.Equal(level, ResistiveFilms.ConductorOf(loaded, Resistor)!.Name);
            Assert.Equal((25.0, (long?)60, ConductorSheetSurface.Bottom), (film.SheetResistanceOhmPerSq, film.ThicknessDbu, film.At!.Value));
            Assert.DoesNotContain(Resistor, loaded.Stackup.Layers.First(l => l.Name == level).DrawingLayers);
        }
        Assert.DoesNotContain("FilmThicknessDbu", TechPersistence.Serialize(loaded));
    }

    /// <summary>R-stk9-0d — renumbering and removing a layer each follow through in one undo entry.</summary>
    [Fact]
    public void AFilmFollowsItsLayerInOneUndoEntry()
    {
        var vm = Editor();
        string start = Json(vm);
        var row = vm.Layers.First(r => r.Layer.Key == Resistor);

        row.StagedLayerNumber = "40";
        row.CommitLayerNumber();
        Assert.NotNull(ResistiveFilms.Of(vm.Working, new LayerKey(40, 0)));
        vm.UndoCommand.Execute(null);
        Assert.Equal(start, Json(vm));

        vm.RemoveLayer(vm.Layers.First(r => r.Layer.Key == Resistor));
        Assert.Null(ResistiveFilms.Of(vm.Working, Resistor));
        vm.UndoCommand.Execute(null);
        Assert.Equal(start, Json(vm));
    }

    /// <summary>R-stk9-0e — every copy of the technology states the one film on Metal1, at one R_s.</summary>
    [Fact]
    public void EveryCopyStatesTheOneFilm()
    {
        var techs = new List<(string Name, Technology Tech)> { ("starter", StarterTechnologies.MmicGaAs()) };
        foreach (string root in new[] { "examples", "testdata", Path.Combine("src", "Design", "resources") })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo(), root), "mmic-GaAs_2LM*.ctech", SearchOption.AllDirectories))
                techs.Add((file, TechPersistence.LoadOwnFromFile(file)));
        Assert.True(techs.Count >= 5, $"found {techs.Count}");

        foreach (var (name, tech) in techs)
        {
            var (level, film) = Assert.Single(ResistiveFilms.All(tech));
            Assert.True(level.Name == "Metal1" && film.Layer == Resistor && film.SheetResistanceOhmPerSq == 50, name);
            Assert.Equal(2, tech.DrcRules.Count(r => r.Layer == Resistor));
            Assert.DoesNotContain(TechValidation.Analyze(tech), p => p.Id?.StartsWith("tech.film.", StringComparison.Ordinal) == true);
        }
    }

    /// <summary>R-stk9-6 — on every target of the starter stack the canvas's question and validation's answer agree.</summary>
    [Fact]
    public void CanPlaceOnAndValidationAgreeOnEveryEntry()
    {
        var names = StarterTechnologies.MmicGaAs().Stackup.Layers.Select(l => l.Name).ToList();
        foreach (string target in names)
        {
            var tech = StarterTechnologies.MmicGaAs();
            bool may = ResistorFilmBinding.CanPlaceOn(tech, Resistor, target, out string reason);
            Assert.Equal(!may, reason.Length > 0);

            ResistorFilmBinding.Remove(tech, Resistor);
            tech.Stackup.Layers.First(l => l.Name == target).Films = [new StackupFilm { Layer = Resistor, SheetResistanceOhmPerSq = 50 }];
            var problems = TechValidation.Analyze(tech).Where(p => p.Id?.StartsWith("tech.film.", StringComparison.Ordinal) == true).ToList();
            Assert.Equal(may, problems.Count == 0);
            if (!may) Assert.Contains(problems, p => p.Message == reason);
        }
    }

    // ── R-stk9-1, R-stk9-2: the scene ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "Top")]        // the level's own SheetAt
    [InlineData("Top", "Top")]
    [InlineData("Bottom", "Bottom")]
    public void TheStripIsFlushWithItsFaceInsideItsBand(string? at, string face)
    {
        var tech = ShippedTechnologies.Load(GaAs);
        ResistiveFilms.Of(tech, Resistor)!.At = at is null ? null : Enum.Parse<ConductorSheetSurface>(at);
        var scene = StackupScene.Build(tech, (float)PaneWidth);
        var band = scene.Bands.First(b => b.Name == "Metal1").Rect;
        var strip = scene.FilmRectOf(Resistor)!.Value;

        Assert.Equal(StackupScene.FilmStripPx, strip.Height, 3);
        Assert.Equal(band.Left, strip.Left, 3);
        Assert.Equal(band.Right, strip.Right, 3);
        Assert.Equal(face == "Top" ? band.Top : band.Bottom, face == "Top" ? strip.Top : strip.Bottom, 3);
    }

    /// <summary>R-stk9-1 — films on one face share the strip in equal segments; the other face has its own.</summary>
    [Fact]
    public void FilmsOnOneFaceShareTheStripInEqualSegments()
    {
        var tech = ShippedTechnologies.Load(GaAs);
        var metal1 = tech.Stackup.Layers.First(l => l.Name == "Metal1");
        metal1.Films!.Add(new StackupFilm { Layer = new LayerKey(5, 0), SheetResistanceOhmPerSq = 10 });
        metal1.Films!.Add(new StackupFilm { Layer = new LayerKey(20, 0), SheetResistanceOhmPerSq = 5, At = ConductorSheetSurface.Bottom });
        var scene = StackupScene.Build(tech, (float)PaneWidth);
        var band = scene.Bands.First(b => b.Name == "Metal1").Rect;
        var (a, b, c) = (scene.FilmRectOf(Resistor)!.Value, scene.FilmRectOf(new LayerKey(5, 0))!.Value, scene.FilmRectOf(new LayerKey(20, 0))!.Value);

        Assert.Equal(band.Width / 2, a.Width, 2);
        Assert.Equal(a.Right, b.Left, 2);
        Assert.Equal(band.Width, c.Width, 2);
        Assert.Equal(band.Bottom, c.Bottom, 3);
    }

    /// <summary>R-stk9-1 — a film's pieces say only what is stated. R-stk9-2 — strip before band, and the values carry fields.</summary>
    [Fact]
    public void TheLabelSaysWhatIsStatedAndItsValuesCarryTheirFields()
    {
        var scene = StackupScene.Build(ShippedTechnologies.Load(GaAs), (float)PaneWidth);
        string Sentence(StackupScene s) => string.Join(" ", s.Labels.Where(l => l.FilmLayer == Resistor).Select(l => l.Text));
        Assert.Equal("Resistor · 50 Ω/sq", Sentence(scene));

        var stated = StackupScene.Build(Stated(), (float)PaneWidth);
        Assert.Equal("Resistor · 50 Ω/sq · 0.05 µm · top", Sentence(stated));

        var value = stated.Labels.Single(l => l.Field == StackupField.FilmSheetResistance);
        var hit = stated.HitTest(value.Rect.MidX, value.Rect.MidY)!;
        Assert.Equal((StackupHitKind.Label, StackupField.FilmSheetResistance, (LayerKey?)Resistor), (hit.Kind, hit.Field, hit.FilmLayer));
        Assert.Single(stated.Labels, l => l.Field == StackupField.FilmThickness);

        var strip = stated.FilmRectOf(Resistor)!.Value;
        Assert.Equal(StackupHitKind.Film, stated.HitTest(strip.Left + 2, strip.MidY)!.Kind);
        var band = stated.Bands.First(b => b.Name == "Metal1").Rect;
        Assert.Equal(StackupHitKind.Band, stated.HitTest(band.Left + 2, band.Bottom - 2)!.Kind);
    }

    /// <summary>R-stk9-1 — no film, no change: the same technology with its film gone draws a scene with nothing new in it.</summary>
    [Fact]
    public void NoFilmLeavesTheSceneUnchanged()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var scene = StackupScene.Build(tech, (float)PaneWidth);
        Assert.Empty(scene.Films);
        Assert.DoesNotContain(scene.Hits, h => h.Kind == StackupHitKind.Film || h.FilmLayer is not null);
        Assert.DoesNotContain(scene.Labels, l => l.FilmLayer is not null);
    }

    // ── R-stk9-4, R-stk9-5: selection and the one write path ──────────────────────────────────────

    [Fact]
    public void AFilmAndABandAreSelectedExclusivelyAndEscClearsBoth()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        vm.StackupFilter = "GaAs";

        Assert.True(canvas.PressAt(OnStrip(SceneOf(canvas), Resistor)));
        canvas.ReleaseAt(OnStrip(SceneOf(canvas), Resistor));
        Assert.Equal(Resistor, vm.SelectedFilmLayerKey);
        Assert.Null(vm.SelectedStackupLayerName);
        Assert.Equal("", vm.StackupFilter);                     // the filter would have hidden the card
        Assert.Equal("Metal1", vm.StackupScrollTargetRow!.Layer.Name);
        Assert.True(vm.StackupScrollTargetRow!.FilmRows.Single().IsSelected);
        Assert.Equal(Resistor, canvas.CurrentOverlay.SelectedFilm);

        vm.SelectedStackupLayerName = "GaAs";
        Assert.Null(vm.SelectedFilmLayerKey);
        Assert.False(vm.StackupLayers.First(r => r.Layer.Name == "Metal1").FilmRows.Single().IsSelected);

        vm.SelectedFilmLayerKey = Resistor;
        Assert.Null(vm.SelectedStackupLayerName);
        vm.ClearStackupSelection();
        Assert.Null(vm.SelectedFilmLayerKey);
    }

    /// <summary>R-stk9-5, R-stk9-9 — an inline R_s edit, the same edit typed in the card: one technology, one undo stack.</summary>
    [Theory]
    [InlineData(StackupField.FilmSheetResistance, "75")]
    [InlineData(StackupField.FilmThickness, "0.1")]
    public void TheDrawingAndTheCardAreOneEdit(StackupField field, string text)
    {
        var inline = Editor(Stated());
        var card = Editor(Stated());

        var scene = StackupScene.Build(inline.Working, (float)PaneWidth);
        var label = scene.Labels.Single(l => l.Field == field);
        var target = StackupInlineEditor.TargetAt(scene, inline, label.Rect.MidX, label.Rect.MidY)!.Value;
        Assert.Equal(field == StackupField.FilmSheetResistance ? "50" : "0.05", target.Seed);
        StackupInlineEditor.Apply(inline.StackupScrollTargetRowFor(Resistor), field, text, Resistor);

        var row = card.StackupScrollTargetRowFor(Resistor).FilmRows.Single();
        if (field == StackupField.FilmSheetResistance) { row.StagedSheetResistance = text; row.CommitSheetResistance(); }
        else { row.StagedThickness = text; row.CommitThickness(); }

        Assert.Equal(Json(card), Json(inline));
        Assert.True(inline.UndoCommand.CanExecute(null));
        inline.UndoCommand.Execute(null);
        Assert.Equal(TechPersistence.Serialize(Stated()), Json(inline));
    }

    /// <summary>R-stk9-9 — a value that is not a positive number is refused, and writes nothing.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void AValueThatIsNotPositiveIsRefused(string text)
    {
        var vm = Editor();
        string start = Json(vm);
        var row = vm.StackupScrollTargetRowFor(Resistor).FilmRows.Single();
        row.StagedSheetResistance = text;
        row.CommitSheetResistance();
        Assert.True(row.HasError);
        Assert.Equal("50", row.StagedSheetResistance);
        Assert.Equal(start, Json(vm));
    }

    /// <summary>R-stk9-9 — a film's layer is offered disabled among the metal, with a tooltip saying where it is.</summary>
    [Fact]
    public void AFilmLayerIsDisabledInTheCheckList()
    {
        var vm = Editor();
        var option = vm.StackupLayers.First(r => r.Layer.Name == "Metal2").DrawingLayerOptions.Single(o => o.Key == Resistor);
        Assert.False(option.IsEnabled);
        Assert.Contains("Metal1", option.Tip);
        Assert.Equal("Resistor · 50 Ω/sq", option.Name);
    }

    // ── R-stk9-7: the drags ───────────────────────────────────────────────────────────────────────

    private static bool Drag(StackupCanvas canvas, Point from, Point to)
    {
        canvas.PressAt(from);
        canvas.MoveAt(new Point(from.X, from.Y + (to.Y > from.Y ? 6 : -6)));
        canvas.MoveAt(to);
        return canvas.ReleaseAt(to);
    }

    [Fact]
    public void ADragToTheOtherFaceIsOneUndoEntryAndTheSameFaceWritesNothing()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string start = Json(vm);
        var from = OnStrip(SceneOf(canvas), Resistor);

        // Back to the face it came from: the level's SheetAt is Top, and a gesture changing nothing states nothing.
        Assert.False(Drag(canvas, from, OnBand(SceneOf(canvas), "Metal1", r => r.Top + 1)));
        Assert.Equal(start, Json(vm));

        Assert.True(Drag(canvas, from, OnBand(SceneOf(canvas), "Metal1", r => r.Bottom - 1)));
        Assert.Equal(ConductorSheetSurface.Bottom, ResistiveFilms.Of(vm.Working, Resistor)!.At);
        vm.UndoCommand.Execute(null);
        Assert.Equal(start, Json(vm));
    }

    [Fact]
    public void ADragOntoAnotherConductorMovesTheEntryInOneEdit()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string start = Json(vm);

        Assert.True(Drag(canvas, OnStrip(SceneOf(canvas), Resistor), OnBand(SceneOf(canvas), "Metal2", r => r.MidY)));
        Assert.Equal("Metal2", ResistiveFilms.ConductorOf(vm.Working, Resistor)!.Name);
        Assert.Null(vm.Working.Stackup.Layers.First(l => l.Name == "Metal1").Films);
        vm.UndoCommand.Execute(null);
        Assert.Equal(start, Json(vm));
    }

    /// <summary>A refused target shows the binding's own sentence, and releasing there writes nothing.</summary>
    [Theory]
    [InlineData("GaAs")]
    [InlineData("Backside Metal")]
    public void ADropOnARefusedTargetWritesNothing(string target)
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string start = Json(vm);
        var scene = SceneOf(canvas);
        var from = OnStrip(scene, Resistor);
        var to = OnBand(scene, target, r => r.MidY);

        canvas.PressAt(from);
        canvas.MoveAt(new Point(from.X, from.Y + 6));
        canvas.MoveAt(to);
        ResistorFilmBinding.CanPlaceOn(vm.Working, Resistor, target, out string reason);
        Assert.Equal(reason, canvas.Drag.RefusalReason);
        Assert.True(canvas.CurrentOverlay.DragRefused);
        Assert.False(canvas.ReleaseAt(to));
        Assert.Equal(start, Json(vm));
    }

    // ── R-stk9-8: the menu ────────────────────────────────────────────────────────────────────────

    private static List<MenuItem> Items(List<object> menu) => [.. menu.OfType<MenuItem>()];

    private static void Click(MenuItem item) => item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

    [Fact]
    public void TheFilmMenuWritesOnlyItsOwnField()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var menu = Items(canvas.BuildContextMenuItems(OnStrip(SceneOf(canvas), Resistor)));
        Assert.Equal(["Face", "Move to", "Remove Film", "Copy"], menu.Select(m => (string)m.Header!));

        var faces = ((IEnumerable<object>)menu[0].ItemsSource!).OfType<MenuItem>().ToList();
        Assert.True(faces.Single(f => (string)f.Header! == "Top").IsChecked);
        var targets = ((IEnumerable<object>)menu[1].ItemsSource!).OfType<MenuItem>().Select(m => (string)m.Header!).ToList();
        Assert.DoesNotContain("Metal1", targets);
        Assert.DoesNotContain("Backside Metal", targets);
        Assert.Contains("Metal2", targets);

        Click(faces.Single(f => (string)f.Header! == "Bottom"));
        Assert.Equal(ConductorSheetSurface.Bottom, ResistiveFilms.Of(vm.Working, Resistor)!.At);
        Assert.Equal(50, ResistiveFilms.Of(vm.Working, Resistor)!.SheetResistanceOhmPerSq);

        var again = Items(canvas.BuildContextMenuItems(OnStrip(SceneOf(canvas), Resistor)));
        Click(again.Single(m => (string)m.Header! == "Remove Film"));
        Assert.Null(ResistiveFilms.Of(vm.Working, Resistor));
        Assert.Contains(vm.Working.Layers, l => l.Key == Resistor);   // the layer stays
    }

    /// <summary>New Resistor Film… makes the layer and the film in one undo entry; Add Resistor Film offers only free layers.</summary>
    [Fact]
    public async System.Threading.Tasks.Task TheConductorMenuAddsAFilm()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        canvas.FilmPrompt = (_, askName) => System.Threading.Tasks.Task.FromResult<(string?, double)?>((askName ? "NiCr" : null, 100.0));
        string start = Json(vm);
        int layers = vm.Working.Layers.Count;
        var metal2 = SceneOf(canvas).Bands.First(b => b.Name == "Metal2").Rect;

        var menu = Items(canvas.BuildContextMenuItems(new Point(metal2.Left + 2, metal2.MidY)));
        var add = menu.Single(m => (string)m.Header! == "Add Resistor Film");
        var offered = ((IEnumerable<object>)add.ItemsSource!).OfType<MenuItem>().Select(m => (string)m.Header!).ToList();
        Assert.DoesNotContain("Resistor", offered);
        Assert.DoesNotContain("Metal1", offered);
        Assert.Contains("Nitride", offered);

        Click(menu.Single(m => (string)m.Header! == "New Resistor Film…"));
        await System.Threading.Tasks.Task.Yield();
        Assert.Equal(layers + 1, vm.Working.Layers.Count);
        var created = vm.Working.Layers[^1];
        Assert.Equal("NiCr", created.Name);
        Assert.Equal("Metal2", ResistiveFilms.ConductorOf(vm.Working, created.Key)!.Name);
        vm.UndoCommand.Execute(null);
        Assert.Equal(start, Json(vm));
    }

    /// <summary>The Layers tab is for drawing: neither its view model nor the XAML names a film's values.</summary>
    [Fact]
    public void TheLayersTabNamesNoFilmValue()
    {
        string vm = File.ReadAllText(Path.Combine(Repo(), "src", "Ui", "Layout", "LayerRowViewModel.cs"));
        foreach (string word in new[] { "SheetResistance", "ThicknessDbu", "FilmAt", "Ω/sq" })
            Assert.DoesNotContain(word, vm, StringComparison.Ordinal);
    }
}
