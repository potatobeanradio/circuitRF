using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Ui.Controls;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Content;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.Views.Layout;
using CircuitRF.Ui.Views.Messages;
using CircuitRF.Ui.Views.Tuning;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// AN-02's figures (<c>brief-an02-gerber-import-app-note.md</c> R-an02-3, R-an02-4): a Gerber file set imported and
/// turned into a schematic, walked through on the shipped <c>Artwork to Schematic</c> example.
/// </summary>
/// <remarks>
/// <b>Every walkthrough figure runs the real thing on a COPY of the example</b> — the import through
/// <see cref="GerberImportEntry"/>, the recognition through <see cref="ArtworkRecognitionRunner"/>, the S-parameter run
/// through <c>SchematicRunService</c> — into a temporary folder each figure deletes in its <c>Cleanup</c>. A copy,
/// because an import and a Create both WRITE into the workspace, and the example under <c>examples/</c> is what a
/// reader's File ▸ New ▸ Examples hands them.
///
/// <para><b>An import into that copy lands in <c>Board_2</c></b>, because the example already holds the same import,
/// done once and with its stackup corrected, as <c>Board</c>. That is what a reader following the note sees too.</para>
///
/// <para><b>Three figures use a made-up set</b>, because the example cannot show what they are of: a drill file that
/// does not state its format (<see cref="DrillFormat"/>), and a file nothing identifies, which is asked the stackup
/// question (<see cref="UnidentifiedLayerChoices"/>). Their captions say so.</para>
///
/// <para><b>No figure prints a path.</b> The Messages figure shows the import's own sentences, which name files but no
/// folder; the Use import's closing sentence names its technology relative to the workspace (R-an02-6).</para>
/// </remarks>
public static class DocAppNoteGerberFixtures
{
    private const string ExampleFolder = "Artwork to Schematic";
    private static int DbuPerMicron => LayoutUnits.DefaultDbuPerMicron;

    // ── The copy ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The example, copied into a temporary folder; <see cref="Ws"/> is its workspace folder.</summary>
    private sealed record Copy(string Root, string Ws)
    {
        public string FabSet => Path.Combine(Ws, "fab", "Board");
        public string WorkspaceTechPath => Path.Combine(Ws, "tech", "board.ctech");
        public string ShippedClay => Path.Combine(Ws, "Board", "Board", "layout", "Board.clay");
        public void Delete() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
    }

    private static Copy CopyExample()
    {
        string examples = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the AN-02 figures have no board.");
        string root = Directory.CreateTempSubdirectory("crf-doc-an02-").FullName;
        string ws = Path.Combine(root, ExampleFolder);
        CopyDirectory(Path.Combine(examples, ExampleFolder), ws);
        return new Copy(root, ws);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>Builds a scene on a fresh copy and deletes the copy afterwards — and on a failure to build.</summary>
    private static FigureScene OnCopy(Func<Copy, FigureScene> build, Action? alsoCleanup = null)
    {
        var copy = CopyExample();
        try
        {
            var scene = build(copy);
            var inner = scene.Cleanup;
            return new FigureScene(scene.Content)
            {
                Popups = scene.Popups,
                AfterLayout = scene.AfterLayout,
                Cleanup = () => { inner?.Invoke(); alsoCleanup?.Invoke(); copy.Delete(); },
            };
        }
        catch
        {
            copy.Delete();
            throw;
        }
    }

    // ── The import, as File ▸ Import ▸ Gerber runs it ───────────────────────────────────────────────

    /// <summary>
    /// File ▸ Import ▸ Gerber on <c>fab/Board</c>, into the copy's workspace, with the layer-mapping dialog answered
    /// on <paramref name="use"/>: the workspace's own technology, or New technology from the files. The answer is the
    /// one <see cref="LayerMappingDialog"/>'s Continue builds — the choice's rows, and the choice committed.
    /// </summary>
    private static GerberImport.ImportResult Import(Copy copy, bool use)
    {
        var workspaceTech = TechPersistence.LoadFromFile(copy.WorkspaceTechPath);
        var result = GerberImportEntry.RunFolder(copy.FabSet, copy.Ws, workspaceTech, DbuPerMicron,
            resolveMapping: request =>
            {
                var choices = ImportTechnologyChoices.Build(copy.Ws, request.CopperCount, ShippedCatalog());
                var choice = use
                    ? choices.Single(c => c.Kind == ImportTechnologyChoiceKind.Workspace
                                          && string.Equals(c.Path, copy.WorkspaceTechPath, StringComparison.Ordinal))
                    : choices.Single(c => c.Kind == ImportTechnologyChoiceKind.New);
                var rows = choice.Kind == ImportTechnologyChoiceKind.New
                    ? (request.Target.IsUse ? request.Repropose(null) : request.Rows)
                    : request.Repropose(choice.Technology);
                return new ImportMappingAnswer(rows, ImportTechnologyChoices.Commit(choice, copy.Ws));
            });
        if (result.Cancelled || result.CellDir is null)
            throw new InvalidOperationException(
                "The AN-02 import of the example's fab/Board set did not complete: " + string.Join(" ", result.Messages));
        return result;
    }

    /// <summary>The SHIPPED catalog only, as <see cref="DocGerberFixtures"/> takes it: a figure must not list the
    /// technologies installed on the machine running DocGen.</summary>
    private static List<TechnologyCatalogEntry> ShippedCatalog() => ShippedTechnologies.All
        .Select(e => new TechnologyCatalogEntry(e.Id, ShippedTechnologies.Load(e).Name, TechnologyOrigin.Shipped, e.ResourceName, null))
        .OrderBy(e => e.Id, StringComparer.Ordinal)
        .ToList();

    // ── Pointing at the files ───────────────────────────────────────────────────────────────────────

    /// <summary>The scope question after picking <c>Board.GTL</c> alone.</summary>
    public static FigureScene ScopePrompt() => OnCopy(copy =>
    {
        var survey = GerberImportEntry.Survey(Path.Combine(copy.FabSet, "Board.GTL"));
        if (!survey.NeedsPrompt)
            throw new InvalidOperationException("The example's Board.GTL no longer has siblings, so no scope question is asked.");
        return Lifted(new GerberImportScopeDialog(survey));
    });

    // ── The layer-mapping dialog ────────────────────────────────────────────────────────────────────

    /// <summary>The mapping dialog for the example's set, on the Technology the dialog opens on.</summary>
    public static FigureScene LayerMapping() => OnCopy(copy =>
    {
        var workspaceTech = TechPersistence.LoadFromFile(copy.WorkspaceTechPath);
        ImportMappingRequest? captured = null;
        GerberImportEntry.RunFolder(copy.FabSet, copy.Ws, workspaceTech, DbuPerMicron,
            resolveMapping: r => { captured = r; return null; });
        var request = captured ?? throw new InvalidOperationException(
            "The example's import never reached its mapping callback, so there is no dialog to photograph.");
        var choices = ImportTechnologyChoices.Build(copy.Ws, request.CopperCount, ShippedCatalog());
        int selected = ImportTechnologyChoices.DefaultIndex(choices, copy.WorkspaceTechPath);
        return Lifted(new LayerMappingDialog("Import Gerber — Layer Mapping", workspaceTech, request, choices, selected, copy.Ws));
    });

    /// <summary>
    /// A made-up set the identification cascade cannot finish: two coppers that say what they are, and a third file
    /// that says nothing — no attribute, no job file, an extension no rung knows. Its row asks the stackup question,
    /// and the figure opens that row's combo.
    /// </summary>
    public static FigureScene UnidentifiedLayerChoices()
    {
        string root = Directory.CreateTempSubdirectory("crf-doc-an02-unid-").FullName;
        try
        {
            var ws = WorkspaceCreate.Create(root, "Board", DocGerberFixtures.FourLayerId);
            string wsDir = Path.GetDirectoryName(ws.CwsPath)!;
            string defaultPath = Path.Combine(wsDir, "tech", DocGerberFixtures.FourLayerId + ".ctech");
            var workspaceTech = TechPersistence.LoadFromFile(defaultPath);

            string setDir = Path.Combine(root, "board");
            Directory.CreateDirectory(setDir);
            Artwork(setDir, "board.gtl", "%TF.FileFunction,Copper,L1,Top,Signal*%\n", trace: true);
            Artwork(setDir, "board.gbl", "%TF.FileFunction,Copper,L2,Bot,Signal*%\n", trace: true);
            Artwork(setDir, "board.pl3", "", trace: false, plane: true);

            ImportMappingRequest? captured = null;
            GerberImportEntry.RunFolder(setDir, wsDir, workspaceTech, DbuPerMicron,
                resolveMapping: r => { captured = r; return null; });
            var request = captured ?? throw new InvalidOperationException(
                "The made-up set never reached its mapping callback, so there is no dialog to photograph.");
            if (!request.Rows.Any(r => r.StackupConductors is not null))
                throw new InvalidOperationException(
                    "Every file of the made-up set was identified, so no row asks the stackup question this figure is of.");

            var choices = ImportTechnologyChoices.Build(wsDir, request.CopperCount, ShippedCatalog());
            int selected = ImportTechnologyChoices.DefaultIndex(choices, defaultPath);
            var scene = Lifted(new LayerMappingDialog("Import Gerber — Layer Mapping", workspaceTech, request, choices, selected, wsDir));
            return new FigureScene(scene.Content)
            {
                Popups = OpenStackupCombo,
                Cleanup = () => { try { Directory.Delete(root, recursive: true); } catch (IOException) { } },
            };
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>One artwork file of a made-up set: two pads and, for a signal layer, the trace between them; a plane
    /// is one filled region across the board.</summary>
    private static void Artwork(string dir, string name, string attributes, bool trace, bool plane = false)
    {
        string body = "%FSLAX46Y46*%\n%MOMM*%\n" + attributes +
                      "%ADD10C,0.600000*%\n%ADD11C,0.300000*%\n" +
                      (plane
                          ? "G36*\nX0Y0D02*\nX6000000Y0D01*\nX6000000Y3000000D01*\nX0Y3000000D01*\nX0Y0D01*\nG37*\n"
                          : "D10*\nX1000000Y1500000D03*\nX5000000Y3500000D03*\n" + (trace ? "D11*\nX1000000Y1500000D02*\nX5000000Y3500000D01*\n" : "")) +
                      "M02*\n";
        File.WriteAllText(Path.Combine(dir, name), body);
    }

    /// <summary>Opens the "In the stackup as" combo on the row asking it, and describes the drop-down's content — the
    /// way <see cref="DocGerberFixtures"/> opens the Technology combo, for the same reason.</summary>
    private static IReadOnlyList<PopupCapture> OpenStackupCombo(Control root)
    {
        var combo = root.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.IsVisible && c.DataContext is LayerMappingRowViewModel { ShowStackupCombo: true }
                                 && ReferenceEquals(c.ItemsSource, ((LayerMappingRowViewModel)c.DataContext).StackupOptions))
            ?? throw new InvalidOperationException("No row of the mapping dialog shows the stackup combo.");
        combo.IsDropDownOpen = true;
        UiArtworkGenerator.Pump();

        var popup = combo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault();
        if (popup?.Child is not { } list) return [];
        var at = combo.TranslatePoint(new Point(0, combo.Bounds.Height), root) ?? default;
        return DocFixtures.DescribePopup(list, root, at);
    }

    // ── Drill data ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The drill-format prompt on a made-up set whose drill file states neither its units nor its zero suppression —
    /// the import's own inference and cross-check, captured at its callback, in the real dialog.
    /// </summary>
    public static FigureScene DrillFormat()
    {
        string root = Directory.CreateTempSubdirectory("crf-doc-an02-drill-").FullName;
        try
        {
            string setDir = Path.Combine(root, "board");
            Directory.CreateDirectory(setDir);
            Artwork(setDir, "board.gtl", "%TF.FileFunction,Copper,L1,Top,Signal*%\n", trace: true);
            Artwork(setDir, "board.gbl", "%TF.FileFunction,Copper,L2,Bot,Signal*%\n", trace: true);
            // No INCH/METRIC line, no format comment, no decimal points: every question is left to the inference.
            File.WriteAllText(Path.Combine(setDir, "board.drl"), "M48\nT1C0.0118\n%\nT1\nX394Y591\nX1969Y1378\nM30\n");

            (string File, DrillFormatInference Inferred, DrillExtentsCheck Check, int Remaining)? captured = null;
            GerberImportEntry.RunFolder(setDir, root, null, DbuPerMicron,
                resolveDrillFormat: (file, inferred, check, remaining) => { captured = (file, inferred, check, remaining); return null; });
            var c = captured ?? throw new InvalidOperationException(
                "The made-up drill file was read without a question, so there is no prompt to photograph.");

            var scene = Lifted(new GerberDrillFormatPromptDialog(c.File, c.Inferred, c.Check, c.Remaining));
            return new FigureScene(scene.Content)
            {
                Cleanup = () => { try { Directory.Delete(root, recursive: true); } catch (IOException) { } },
            };
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    // ── What you get ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Messages panel after the import with New technology from the files: the row the import settles into and
    /// the summary block under it, posted as the workspace posts them. Timestamps off, as in every figure.
    /// </summary>
    public static FigureScene ImportSummary()
    {
        var priorMode = MessageDisplay.Mode;
        MessageDisplay.Mode = MessageTimestampMode.None;
        return OnCopy(copy =>
        {
            var result = Import(copy, use: false);
            var tool = new MessagesTool();
            tool.Post(MessageLevel.Success,
                $"Imported {result.Layers.Count} layer(s) from Gerber into '{Path.GetFileName(result.ImportDir!)}'.");
            foreach (string message in result.Messages) tool.Post(MessageLevel.Info, message);
            return new FigureScene(new MessagesView { DataContext = tool });
        }, alsoCleanup: () => MessageDisplay.Mode = priorMode);
    }

    /// <summary>The layout editor on the board a New import wrote, every layer shown.</summary>
    public static FigureScene ImportedLayout() => OnCopy(copy =>
    {
        var result = Import(copy, use: false);
        var vm = Editor(result, out _);
        return new FigureScene(new LayoutEditorView { DataContext = new LayoutDocument(Path.GetFileName(result.ImportDir!), vm) })
        {
            AfterLayout = DocFixtures.ZoomLayoutToFit,
        };
    });

    /// <summary>The same board with only the top copper shown, framed on the bend, the tee and its open stub.</summary>
    public static FigureScene ImportedCopper() => OnCopy(copy =>
    {
        var result = Import(copy, use: false);
        var vm = Editor(result, out var tech);
        // Hidden in the view's own Layers list — visibility is not the technology's (brief-layout-layers-panel.md D1).
        foreach (var layer in tech.Layers.Where(l => l.Name != "Top")) vm.SetLayerVisible(layer.Key, false);
        return new FigureScene(new LayoutEditorView { DataContext = new LayoutDocument(Path.GetFileName(result.ImportDir!), vm) })
        {
            AfterLayout = root => FrameOn(root, BendTeeAndStub),
        };
    });

    /// <summary>The bend, the tee, its open stub and the line between them, in µm — read off the example's artwork.</summary>
    private static readonly (double X1, double Y1, double X2, double Y2) BendTeeAndStub = (5000, 2300, 12500, 9000);

    /// <summary>The three parts and the via, in µm.</summary>
    private static readonly (double X1, double Y1, double X2, double Y2) Parts = (4000, 9800, 11500, 19800);

    private static LayoutEditorViewModel Editor(GerberImport.ImportResult result, out Technology tech)
    {
        string clay = Directory.GetFiles(Path.Combine(result.CellDir!, CellFolder.LayoutSubFolder), "*.clay").Single();
        tech = TechPersistence.LoadFromFile(result.TechPath!);
        // No file path: given one, the editor shows the "From workspace" banner a layout opened outside the current
        // workspace carries, and in the application this workspace IS the current one.
        return new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(clay)) { Technology = tech };
    }

    /// <summary>Sets the editor's canvas on a region, by the arithmetic <see cref="DocLayoutFixtures.Framed"/> uses —
    /// against the canvas's arranged size, which AfterLayout is late enough to read.</summary>
    private static void FrameOn(Control root, (double X1, double Y1, double X2, double Y2) um)
    {
        var canvas = root.GetVisualDescendants().OfType<LayoutCanvas>().FirstOrDefault()
            ?? throw new InvalidOperationException("The layout editor holds no LayoutCanvas to frame.");
        double w = canvas.Bounds.Width, h = canvas.Bounds.Height;
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"The layout canvas arranged to {w}x{h}.");
        double x1 = um.X1 * DbuPerMicron, y1 = um.Y1 * DbuPerMicron, x2 = um.X2 * DbuPerMicron, y2 = um.Y2 * DbuPerMicron;
        double zoom = Math.Min(w / (x2 - x1), h / (y2 - y1));
        double cx = 0.5 * (x1 + x2), cy = 0.5 * (y1 + y2);
        canvas.SetViewport(new LayoutViewport(cx - w / (2.0 * zoom), cy - h / (2.0 * zoom), zoom, w, h));
    }

    // ── The stackup ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Technology Editor's Stackup tab on the technology a New import wrote: the FR-4 it guessed.</summary>
    public static FigureScene StackupGuessed() => OnCopy(copy =>
    {
        var result = Import(copy, use: false);
        var tech = TechPersistence.LoadFromFile(result.TechPath!);
        var vm = new TechEditorViewModel(result.TechPath!, tech) { SelectedTabIndex = 1 };
        var view = new TechEditorView { DataContext = new TechDocument(tech.Name, vm, vm.FilePath) };
        return new FigureScene(view) { AfterLayout = c => (c as TechEditorView)?.ApplyStackupSplitForCapture() };
    });

    /// <summary>The example's own <c>Board/Board.ctech</c> in cross-section — the same import, with the fabricator's
    /// stackup typed in.</summary>
    public static FigureScene StackupCorrected() => OnCopy(copy =>
        DocStackupFixtures.CrossSectionOf(TechPersistence.LoadFromFile(Path.Combine(copy.Ws, "Board", "Board.ctech"))));

    // ── From artwork to a schematic ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create Schematic from Artwork's view model on the example's board, with its placement file and BOM given
    /// relative to the workspace (<see cref="DocRecognitionFixtures"/>' rule: no absolute path), recognition awaited.
    /// </summary>
    private static CreateSchematicFromArtworkViewModel Recognised(Copy copy)
    {
        var runner = ArtworkRecognitionRunner.Instance;
        var vm = new CreateSchematicFromArtworkViewModel(runner.Load(copy.ShippedClay), [], runner, debounce: TimeSpan.Zero);
        string cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = copy.Ws;
            vm.PlacementPath = Path.Combine("fab", "Board.pos");
            vm.BomPath = Path.Combine("fab", "Board-bom.csv");
        }
        finally { Environment.CurrentDirectory = cwd; }
        vm.Recognition.GetAwaiter().GetResult();
        if (vm.Rows.Count == 0)
            throw new InvalidOperationException($"Recognising the example's board produced no parts ({vm.Status}).");
        return vm;
    }

    /// <summary>The example's board in the layout editor, on its own technology, fitted.</summary>
    private static (LayoutEditorViewModel Vm, LayoutEditorView View) ShippedBoard(Copy copy)
    {
        var tech = TechPersistence.LoadFromFile(Path.Combine(copy.Ws, "Board", "Board.ctech"));
        var vm = new LayoutEditorViewModel(LayoutPersistence.LoadFromFile(copy.ShippedClay)) { Technology = tech };
        return (vm, new LayoutEditorView { DataContext = new LayoutDocument("Board", vm) });
    }

    /// <summary>The layout with L1's row selected in the parts table: the dialog's own highlight callback, wired to the
    /// layout as the workspace wires it.</summary>
    public static FigureScene PartsRowSelected() => OnCopy(copy =>
    {
        var dialogVm = Recognised(copy);
        var (layoutVm, view) = ShippedBoard(copy);
        dialogVm.PartHighlighted = (rings, focus) =>
        {
            if (rings is null) layoutVm.ClearArtworkProbe();
            else layoutVm.ShowArtworkProbe(rings, focus, zoom: false);
        };
        dialogVm.SelectedRow = dialogVm.Rows.FirstOrDefault(r => r.Row.Refdes == "L1")
            ?? throw new InvalidOperationException("The example's parts table has no L1 row.");
        if (layoutVm.ArtworkProbe.Count == 0)
            throw new InvalidOperationException("Selecting L1 drew nothing on the layout.");
        return new FigureScene(view) { AfterLayout = root => FrameOn(root, Parts) };
    });

    /// <summary>Create, as the dialog's Create runs it: the same input, target and emit options, into the copy.</summary>
    private static RecognitionRun Create(Copy copy)
    {
        var vm = Recognised(copy);
        var run = ArtworkRecognitionRunner.Instance.Run(
            vm.BuildInput(), vm.Target, new RecognitionRunOptions { Emit = vm.BuildEmit() }, null);
        if (run.Refusal is { } why) throw new InvalidOperationException($"Create Schematic from Artwork refused: {why}");
        if (run.SchematicPath is null) throw new InvalidOperationException("Create Schematic from Artwork wrote no schematic.");
        return run;
    }

    private static SchematicViewModel Schematic(string csch)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        model.SchematicDirectory = Path.GetDirectoryName(csch);
        return new SchematicViewModel(model);
    }

    /// <summary>The schematic editor on the <c>Board_model</c> the dialog's Create wrote.</summary>
    public static FigureScene RecognisedSchematic() => OnCopy(copy =>
    {
        var run = Create(copy);
        string csch = run.SchematicPath!;
        var doc = new SchematicDocument(Path.GetFileNameWithoutExtension(csch), Schematic(csch), csch);
        return new FigureScene(new SchematicView { DataContext = doc }) { AfterLayout = DocFixtures.ZoomSchematicToFit };
    });

    /// <summary>Show in Artwork on the stub's line, resolved as the context menu resolves it and drawn on the layout.</summary>
    public static FigureScene ShowInArtwork() => OnCopy(copy =>
    {
        var run = Create(copy);
        var tuned = Schematic(run.SchematicPath!);
        var stub = tuned.EditModel.Components
            .Where(c => c.FromArtwork && c.ArtworkAnchor.Count >= 2)
            .OrderByDescending(c => c.ArtworkAnchor.Min(p => p.Y) > 7000 * DbuPerMicron && c.ArtworkAnchor.Max(p => p.Y) < 8000 * DbuPerMicron)
            .ThenByDescending(c => c.ArtworkAnchor.Max(p => p.X))
            .First();
        var target = ArtworkCrossProbe.Resolve(run.SchematicPath, tuned.EditModel, stub);
        if (!target.Ok) throw new InvalidOperationException($"Show in Artwork on {stub.InstanceName}: {target.Refusal}");

        var (layoutVm, view) = ShippedBoard(copy);
        layoutVm.ShowArtworkProbe(target.Rings, target.Focus, zoom: false);
        return new FigureScene(view) { AfterLayout = root => FrameOn(root, BendTeeAndStub) };
    });

    /// <summary>The Tuning panel on <c>Board_model</c>, L1_L's minimum lowered to 1 nH and the value set to 8.2 nH —
    /// through the row's own number boxes, as a reader types them.</summary>
    public static FigureScene TuningL1() => OnCopy(copy =>
    {
        var run = Create(copy);
        var tool = new TuningTool();
        tool.Panel.SetActiveSchematic(Schematic(run.SchematicPath!), Path.GetFileName(run.SchematicPath!));
        var row = tool.Panel.Rows.FirstOrDefault(r => r.Key == "L1_L")
            ?? throw new InvalidOperationException("The Tuning panel lists no L1_L for Board_model; it lists "
                                                 + (tool.Panel.Rows.Count == 0 ? "nothing" : string.Join(", ", tool.Panel.Rows.Select(r => r.Key))) + ".");
        row.MinText = "1n";
        row.CommitMinText();
        row.ValueBoxText = "8.2n";
        row.CommitValueText();
        return new FigureScene(new TuningToolView { DataContext = tool });
    });

    /// <summary>
    /// S11 of the recognised board with L1 unknown (1 µH), with L1 tuned to 8.2 nH, and of <c>Board design</c> — three
    /// real runs, plotted through the trace cards' own Source combo. The tuned run sets the global the Tuning panel
    /// moves, on the extraction, as a tuning run does.
    /// </summary>
    public static FigureScene S11() => OnCopy(copy =>
    {
        var created = Create(copy);
        string model = created.SchematicPath!;
        string design = Path.Combine(copy.Ws, "Board design", CellFolder.SchematicSubFolder, "Board design.csch");

        string before = RunAtWorkspace(copy, "Board_model", model, null);
        string after = RunAtWorkspace(copy, "Board_model_L1_8.2nH", model, ("L1_L", "8.2", "nH"));
        string drawn = RunAtWorkspace(copy, "Board_design", design, null);

        var (vm, plot) = DocDataDisplayFixtures.PlotFor(before, PlotType.Rect, traces: 3, size: (700.0, 400.0), also: [after, drawn]);
        string[] sources = [before, after, drawn];
        for (int i = 0; i < sources.Length; i++)
        {
            var card = DocDataDisplayFixtures.Card(plot, i);
            DocDataDisplayFixtures.PickSource(card, sources[i]);
            DocDataDisplayFixtures.PickSignal(card(), "S(1,1)");
            DocDataDisplayFixtures.SetTransform(card, CubeTransform.dB20);
            if (sources[i] == drawn) DocDataDisplayFixtures.Dashed(card());
            // The second default colour is pure blue, which on the dark variant's plot is barely readable as a
            // legend label; green reads in both, through the card's own colour picker.
            if (sources[i] == after) card().SelectedLineColor = PlotInspectorViewModel.ColorItems.First(c => c.Name == "Green");
        }
        return new FigureScene(new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, "Board_model") })
        {
            AfterLayout = DocDataDisplayFixtures.CentredPlot(plot),
        };
    });

    /// <summary>A schematic's netlist, written at the workspace root — where a recognised schematic's technology
    /// reference resolves — and run there.</summary>
    private static string RunAtWorkspace(Copy copy, string key, string csch, (string Name, string Value, string Unit)? set)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        string name = Path.GetFileNameWithoutExtension(csch);
        var extracted = SchematicCircuit.ExtractInWorkspace(model, name, DiskCellResolver.Instance, csch);
        if (set is { } v)
        {
            var globals = extracted.TestBench.GlobalVariables;
            int at = globals.FindIndex(g => g.Name == v.Name);
            if (at < 0) throw new InvalidOperationException($"{name} declares no global {v.Name} to tune.");
            globals[at] = new Variable(v.Name, v.Value, v.Unit);
        }
        string cnl = Path.Combine(copy.Ws, key + ".cnl");
        File.WriteAllText(cnl, SchematicCircuit.CnlTextOf(extracted, name));
        return DocRunData.RunNetlistFile(key, cnl);
    }

    // ── Lifting a dialog ────────────────────────────────────────────────────────────────────────────

    /// <summary>A dialog's content, lifted out of its window with its margin kept as padding —
    /// <see cref="DocGerberFixtures"/>' reason: the generator sizes the lifted body to the whole figure.</summary>
    private static FigureScene Lifted(Window dialog)
    {
        var content = (Control)dialog.Content!;
        var styles = dialog.Styles.ToList();
        dialog.Content = null;
        dialog.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        var margin = content.Margin;
        content.Margin = default;
        return new FigureScene(new Border { Padding = margin, Child = content });
    }
}
