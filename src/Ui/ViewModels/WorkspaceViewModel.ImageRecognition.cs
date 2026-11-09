using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Engine;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Workspace;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Create Schematic / Layout from Image (brief-img-5-dialog.md R-im5-6, R-im5-7, R-im5-9) — the shell around the
/// dialog: opening it on the workspace with its technologies and the user's own settings, and writing what it creates
/// into the workspace's surfaces — the result opened focused, the report in Messages, a trace into the open layout as
/// one undo step with what it added selected.
///
/// <para><b>It never runs except from the user's invocation</b> (the L5 rule). Every way in
/// (brief-img-6-entry-points.md) — the Design ▸ Create rows, a placed picture's right-click and the Project Tree's picture
/// rows — calls <see cref="ShowImageDialog"/>; Edit ▸ Paste Image as Schematic / Layout ask nothing and paste into the
/// document in focus with every default. What is written is written by
/// <see cref="ImageTrace.Run"/> and <see cref="ImageRecognition.Run"/> — the functions the CLI calls (overview D2).</para>
/// </summary>
public partial class WorkspaceViewModel : IImageDialogHost
{
    private CreateSchematicFromArtworkDialog? _imageDialog;
    private PasteImageAvailability? _pasteImage;

    // ── the Design menu (R-im6-1): enabled whenever a workspace is open ──────────────────────────────

    private bool IsWorkspaceOpenForImage() => CurrentWorkspaceRoot is not null;

    /// <summary>Whether a schematic is made from a picture here — <see cref="ImageEntryPoints.SchematicFromImageOffered"/>,
    /// greyed while it is off; a test of the withheld reader turns it on. The dialog this window opens follows it.</summary>
    public bool SchematicFromImageOffered
    {
        get => _schematicFromImageOffered;
        set { _schematicFromImageOffered = value; RaiseImageCommandsChanged(); }
    }

    private bool _schematicFromImageOffered = ImageEntryPoints.SchematicFromImageOffered;

    private bool CanCreateSchematicFromImage() => SchematicFromImageOffered && IsWorkspaceOpenForImage();

    [RelayCommand(CanExecute = nameof(CanCreateSchematicFromImage))]
    private void CreateSchematicFromImage(Window? owner) => ShowImageDialog(null, makeSchematic: true, owner);

    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
    private void CreateLayoutFromImage(Window? owner) => ShowImageDialog(null, makeSchematic: false, owner);

    // ── Edit ▸ Paste Image as … (R-im6-2): enabled only when the clipboard holds a picture ────────────

    /// <summary>The clipboard the paste rows read — the window's, installed by it; null until a window has one.</summary>
    public Func<IPictureClipboard?> PictureClipboard { get; set; } = () => null;

    /// <summary>Whether the clipboard holds a picture, as last asked by <see cref="RefreshPasteImage"/>.</summary>
    public PasteImageAvailability PasteImage => _pasteImage ??= NewPasteImage();

    private PasteImageAvailability NewPasteImage()
    {
        var p = new PasteImageAvailability(() => PictureClipboard());
        p.Changed += RaiseImageCommandsChanged;
        return p;
    }

    /// <summary>
    /// Asks the clipboard again — called just before the Edit menu shows (NeedsUpdate on macOS, SubmenuOpened
    /// elsewhere) and when the window is activated, because the clipboard changes while the user is in another
    /// application. Nothing else calls it: the clipboard is never polled.
    /// </summary>
    public Task RefreshPasteImage() => PasteImage.RefreshAsync();

    private bool CanPasteImage() => CurrentWorkspaceRoot is not null && PasteImage.Available;

    // Paste Image as Schematic / as Layout paste INTO the document in focus, so each is enabled only while a document of
    // its own kind is — and asks nothing: every setting is chosen per picture (IMF-9). Design ▸ Create ▸ is where the
    // options are. Neither is enabled while a paste is still reading (R-imf9-6).
    private bool CanPasteImageAsSchematic() => SchematicFromImageOffered && _paste is null && PasteImage.Available && IsSchematicDocumentActive();

    private bool CanPasteImageAsLayout() => _paste is null && PasteImage.Available && IsLayoutDocumentActive();

    /// <summary>The paste reading in the background, and the document it pastes into (R-imf9-6); null when none is.</summary>
    private (RunCancellation Cancellation, object Document)? _paste;

    /// <summary>Whether a paste is reading its picture — while it is, the window's Esc cancels it (R-imf9-6).</summary>
    public bool IsPasteInFlight => _paste is not null;

    /// <summary>
    /// Esc, or closing the document a paste goes into: cancels the paste in flight, through the one
    /// <see cref="RunCancellation"/> its live row's right-click Cancel uses too. True when there was one to cancel, so
    /// the window marks its Esc handled and the editor neither deselects nor ends a tool.
    /// </summary>
    public bool CancelPasteInFlight()
    {
        if (_paste is not { } p) return false;
        p.Cancellation.Cancel();
        return true;
    }

    /// <summary>The live row a paste reports on, its Cancel bound, and the paste registered as in flight.</summary>
    private (IProgressMessage Live, RunControl Control, RunCancellation Cancellation, CancellationTokenSource Cts) BeginPaste(
        string command, object document, string what = "reading the picture")
    {
        string reading = $"{command}: {what}… (Esc to cancel)";
        var live = Messages.BeginProgress(reading);
        live.Update(reading, "starting", indeterminate: true);
        var cts = new CancellationTokenSource();
        var cancellation = new RunCancellation("the paste", cts.Cancel);
        live.BindCancellation(cancellation);
        var control = new RunControl
        {
            Token = cts.Token,
            Progress = new Progress<RunProgress>(p =>
            {
                if (!cts.IsCancellationRequested) live.Update(reading, string.IsNullOrEmpty(p.Stage) ? "starting" : p.Stage, indeterminate: true);
            }),
        };
        _paste = (cancellation, document);
        RaiseImageCommandsChanged();
        return (live, control, cancellation, cts);
    }

    private void EndPaste(RunCancellation cancellation, CancellationTokenSource cts)
    {
        cancellation.Finish();
        cts.Dispose();
        _paste = null;
        RaiseImageCommandsChanged();
    }

    /// <summary>A document closed while a paste was reading into it: the paste is cancelled, as Esc cancels it.</summary>
    private void CancelPasteInto(object document)
    {
        if (_paste is { } p && ReferenceEquals(p.Document, document)) p.Cancellation.Cancel();
    }

    /// <summary>The read Paste Image as Schematic runs off the UI thread — the one reader (L5). A seam for the tests,
    /// which make it block at a checkpoint.</summary>
    internal Func<CircuitRF.Design.Schematic.Recognition.SchematicImageInput, string?, RunControl,
                  CircuitRF.Design.Schematic.Recognition.SchematicImagePasteDrawing> PasteSchematicReader { get; set; } =
        (input, dir, control) => CircuitRF.Design.Schematic.Recognition.SchematicImageRecognition.PasteDrawing(input, dir, control);

    /// <summary>The trace Paste Image as Layout runs off the UI thread; a seam for the tests.</summary>
    internal Func<ImageTraceInput, RunControl, ImageTraceResult> PasteLayoutTracer { get; set; } =
        (input, control) => ImageTrace.PasteTrace(input, control);

    [RelayCommand(CanExecute = nameof(CanPasteImageAsSchematic))]
    private async Task PasteImageAsSchematic()
    {
        const string command = "Paste Image as Schematic";
        if (ResolveActiveDocumentForCommands() is not SchematicDocument doc)
        {
            Messages.Error($"{command}: click into a schematic first — the picture is pasted into the schematic in focus.");
            return;
        }
        var read = await PasteImage.ReadAsync();
        if (read is null) { Messages.Error($"{command}: the clipboard holds no picture."); return; }
        if (!read.Ok) { Messages.Error($"{command}: {read.Refusal}"); return; }
        await PasteSchematicPicture(doc, read.Source!);
    }

    /// <summary>The paste itself, from a picture in hand: read off the UI thread with Esc to cancel, then placed as one
    /// undo step with the parts needing a look selected, and ONE line in Messages saying what was pasted (R-imf9-4).</summary>
    internal async Task PasteSchematicPicture(SchematicDocument doc, ImageSource source)
    {
        const string command = "Paste Image as Schematic";
        var vm = doc.ActiveViewModel;
        string? path = doc.FilePath is { Length: > 0 } fp ? Path.GetFullPath(fp) : null;
        var input = new CircuitRF.Design.Schematic.Recognition.SchematicImageInput { Source = source };
        var (live, control, cancellation, cts) = BeginPaste(command, doc);
        CircuitRF.Design.Schematic.Recognition.SchematicImagePasteDrawing paste;
        try
        {
            var reader = PasteSchematicReader;
            paste = await Task.Run(() => reader(input, path is null ? null : Path.GetDirectoryName(path), control));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            live.Complete(MessageLevel.Error, $"{command}: the picture could not be read: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            live.Complete(MessageLevel.Info, "Paste cancelled.");
            return;
        }
        finally { EndPaste(cancellation, cts); }
        // A cancelled paste changes nothing: no shapes, no picture kept, no undo step.
        if (paste.Reading.Cancelled || cancellation.IsCancellationRequested)
        {
            live.Complete(MessageLevel.Info, "Paste cancelled.");
            return;
        }
        if (!paste.Ok)
        {
            live.Complete(MessageLevel.Error, $"{command}: {paste.Refusal}");
            return;
        }

        // The picture goes under the drawing as an ordinary bitmap, kept beside the schematic so the reference holds —
        // written only now, after the read succeeded.
        string? pictureRef = null;
        if (path is not null)
            // The ABSOLUTE path: an open document holds its bitmaps' paths resolved (the readers resolve a stored relative
            // one against the document's folder as they load); a relative path here is resolved against the process's
            // working directory, finds nothing, and draws the missing-picture cross.
            try { pictureRef = ImageKeep.ForDocument(path, CircuitRF.Design.Cells.ViewType.Schematic, source).Kept.FullPath; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Messages.Warning($"{command}: the picture could not be kept beside the schematic, so it is not under the drawing: {ex.Message}");
            }

        var outcome = CircuitRF.Design.Schematic.Recognition.SchematicPasteOutcome.Of(paste.Reading);
        var (x, y, inView) = vm.PicturePastePlace(paste.Picture.Width, paste.Picture.Height);
        var edit = CircuitRF.Design.Schematic.Recognition.SchematicImageRecognition.PasteInto(paste, vm.EditModel, (x, y), pictureRef);
        if (SchematicImageEditCommand.For(vm, edit, command) is not { } paste1)
        {
            live.Complete(MessageLevel.Error, $"{command}: {edit.Refusal ?? "nothing in the picture was recognised as a part."}");
            return;
        }
        // The command adds these very objects (renamed, if a name was taken): the ones needing a look are selected.
        var flagged = edit.Drawing!.Components.Where(c => outcome.ToCorrect.Contains(c.InstanceName)).ToList();
        vm.Execute(paste1);
        if (flagged.Count > 0) vm.Selection.SetAll(flagged.Select(c => c.Id));
        LastPasteReading = paste.Reading;
        if (outcome.ToCorrect.Count == 0) live.Complete(MessageLevel.Success, outcome.Sentence);
        else
        {
            var reading = paste.Reading;
            live.CompleteWithAction(MessageLevel.Warning, outcome.Sentence, "Correct…", () =>
            {
                OpenImageDialogOnReading(source, reading);
                return Task.CompletedTask;
            });
        }
        if (!inView) doc.RequestZoomToWorldRect(x, y, x + paste.Picture.Width, y + paste.Picture.Height);
    }

    /// <summary>The reading the last paste placed — what its line's Correct… opens the dialog on.</summary>
    internal CircuitRF.Design.Schematic.Recognition.SchematicImageResult? LastPasteReading { get; private set; }

    /// <summary>R-imf9-4: the dialog on the pasted picture, with the paste's own reading loaded — correcting is one step,
    /// not a read again.</summary>
    internal void OpenImageDialogOnReading(ImageSource source, CircuitRF.Design.Schematic.Recognition.SchematicImageResult reading)
    {
        ShowImageDialog(source, makeSchematic: true, null, reading);
    }

    [RelayCommand(CanExecute = nameof(CanPasteImageAsLayout))]
    private async Task PasteImageAsLayout()
    {
        const string command = "Paste Image as Layout";
        if (ResolveActiveDocumentForCommands() is not LayoutDocument doc)
        {
            Messages.Error($"{command}: click into a layout first — the picture is pasted into the layout in focus.");
            return;
        }
        var read = await PasteImage.ReadAsync();
        if (read is null) { Messages.Error($"{command}: the clipboard holds no picture."); return; }
        if (!read.Ok) { Messages.Error($"{command}: {read.Refusal}"); return; }
        await PasteLayoutPicture(doc, read.Source!);
    }

    /// <summary>Paste Image as Layout from a picture in hand — the same live row, Esc and cancel as the schematic paste.</summary>
    internal async Task PasteLayoutPicture(LayoutDocument doc, ImageSource source)
    {
        const string command = "Paste Image as Layout";
        var vm = doc.ActiveViewModel;
        if (vm.Technology is not { } tech)
        {
            Messages.Error($"{command}: the layout has no technology, so there are no layers to trace the picture onto.");
            return;
        }
        string? path = doc.FilePath is { Length: > 0 } fp ? Path.GetFullPath(fp) : null;
        var input = new ImageTraceInput { Source = source, Technology = tech, DbuPerMicron = vm.Model.DbuPerMicron };
        var (live, control, cancellation, cts) = BeginPaste(command, doc);
        ImageTraceResult result;
        try
        {
            var tracer = PasteLayoutTracer;
            result = await Task.Run(() => tracer(input, control));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            live.Complete(MessageLevel.Error, $"{command}: the picture could not be traced: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            live.Complete(MessageLevel.Info, "Paste cancelled.");
            return;
        }
        finally { EndPaste(cancellation, cts); }
        if (cancellation.IsCancellationRequested)
        {
            live.Complete(MessageLevel.Info, "Paste cancelled.");
            return;
        }
        if (!result.Ok || result.Shapes.Count == 0)
        {
            live.Complete(MessageLevel.Error, $"{command}: {result.Refusal ?? ImageTrace.NoCopperRefusal}");
            return;
        }

        var shapes = new List<LayoutShape>();
        if (result.Underlay is { } underlay && path is not null)
            try
            {
                // Absolute, as an open layout holds every bitmap's path (see the schematic paste above).
                underlay.ImagePathRef = ImageKeep.ForDocument(path, CircuitRF.Design.Cells.ViewType.Layout, source).Kept.FullPath;
                shapes.Add(underlay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Messages.Warning($"{command}: the picture could not be kept beside the layout, so it is not under the shapes: {ex.Message}");
            }
        shapes.AddRange(result.Shapes);
        bool inView = vm.InsertTracedPicture(shapes);
        live.Complete(MessageLevel.Success,
                      $"Pasted {result.Shapes.Count} shape{(result.Shapes.Count == 1 ? "" : "s")} traced from the picture" +
                      (path is null ? " — the layout has not been saved, so the picture is not kept under them." : "."));
        // The trace's own findings after it — the scale it fell back to among them, which says how to size the shapes.
        PostImageReport(result.Report, command, null);
        if (!inView) doc.RequestZoomToFit();
    }

    /// <summary>File ▸ Import ▸ Image ▸ As Touchstone…: a picture of an S-parameter plot, browsed to, read in the picture
    /// dialog's plot mode. The dialog's own Paste reads the clipboard's.</summary>
    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
    private async Task ImportImageAsTouchstone(Window? owner)
    {
        if (ResolveOwner(owner) is not { } window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Image as Touchstone",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Pictures") { Patterns = [.. RasterImage.Extensions.Select(e => "*" + e)] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var read = ImageSource.FromFile(path);
        if (read.Ok) ShowPlotDialog(read.Source, csv: false, window);
        else Messages.Error($"Import Image as Touchstone: {read.Refusal}");
    }

    /// <summary>Tools ▸ Digitise Image… (R-im14-13): the dialog, empty, in plot mode — a picture given to it is read as
    /// what it is, so a board picture is traced into a layout (designer round 29).</summary>
    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
    private void DigitisePlot(Window? owner) => ShowPlotDialog(null, csv: true, owner, classify: true);

    public void OpenInSParameterViewer(ProjectTreeNodeViewModel node) => OpenInSParameterViewer(node.AbsolutePath);

    public void OpenNoiseCircles(ProjectTreeNodeViewModel node) => _ = OpenNoiseCirclesAsync(node.AbsolutePath);
    public void OpenSignalQuality(ProjectTreeNodeViewModel node) => _ = OpenSignalQualityAsync(node.AbsolutePath);

    public void CreateTouchstoneFromImageFile(ProjectTreeNodeViewModel node)
    {
        var read = ImageSource.FromFile(node.AbsolutePath);
        if (read.Ok) ShowPlotDialog(read.Source, csv: false, null);
        else Messages.Error($"Create Touchstone from Plot: {read.Refusal}");
    }

    /// <summary>The picture dialog in plot mode — reading <paramref name="source"/> as a plot (added to the session when
    /// the dialog is already reading plots), or empty.</summary>
    public void ShowPlotDialog(ImageSource? source, bool csv, Window? owner, bool classify = false)
    {
        bool wasOpen = _imageDialog is not null;
        if (!wasOpen) ShowImageDialog(null, makeSchematic: false, owner);
        if (_imageDialog?.DataContext is not ImageSourceViewModel vm) return;
        // Digitise Image… opens read by hand unless the user last chose Automatic there; the Touchstone entries automatically.
        bool manual = csv && AppPreferencesIo.Load().ImageDialogPlotReadMode != "automatic";
        if (source is not null) vm.ReadPlot(source, csv, manual);
        else if (!wasOpen) vm.StartPlotMode(csv, manual, classify);
        _imageDialog.Activate();
    }

    private void RaiseImageCommandsChanged()
    {
        CreateSchematicFromImageCommand.NotifyCanExecuteChanged();
        CreateLayoutFromImageCommand.NotifyCanExecuteChanged();
        PasteImageAsSchematicCommand.NotifyCanExecuteChanged();
        PasteImageAsLayoutCommand.NotifyCanExecuteChanged();
        ImportImageAsTouchstoneCommand.NotifyCanExecuteChanged();
        DigitisePlotCommand.NotifyCanExecuteChanged();
    }

    // ── a placed picture (R-im6-3) and the Project Tree (R-im6-4) ────────────────────────────────────

    /// <summary>The dialog on a placed bitmap's picture or a tree file; a picture that cannot be read is said in
    /// Messages, in its own sentence.</summary>
    public void ShowImageDialogFor(ImageSourceResult read, bool makeSchematic, Window? owner)
    {
        if (read.Ok) ShowImageDialog(read.Source, makeSchematic, owner);
        else Messages.Error($"{(makeSchematic ? "Create Schematic from Image" : "Create Layout from Image")}: {read.Refusal}");
    }

    public void CreateFromImageFile(ProjectTreeNodeViewModel node, bool makeSchematic) =>
        ShowImageDialogFor(ImageSource.FromFile(node.AbsolutePath), makeSchematic, null);

    /// <summary>
    /// Opens the picture dialog — empty, or already reading <paramref name="source"/> — with Make preset. One dialog per
    /// window: when it is already open, a picture handed in is read there and the dialog comes forward.
    /// </summary>
    public void ShowImageDialog(ImageSource? source, bool makeSchematic, Window? owner) => ShowImageDialog(source, makeSchematic, owner, null);

    /// <summary>The dialog, as above — and, given a schematic picture's <paramref name="reading"/>, showing it with no
    /// read again (R-imf9-4).</summary>
    public void ShowImageDialog(ImageSource? source, bool makeSchematic, Window? owner,
                                CircuitRF.Design.Schematic.Recognition.SchematicImageResult? reading)
    {
        if (CurrentWorkspaceRoot is not { } root)
        {
            Messages.Error("Create from Image: open a workspace first — the result is a new cell in it.");
            return;
        }
        if (_imageDialog is { } open)
        {
            if (open.DataContext is ImageSourceViewModel shown)
            {
                if (source is not null && reading is not null) shown.SeedSchematic(source, reading);
                else if (source is not null) shown.Read(source, makeSchematic);
            }
            open.Activate();
            return;
        }

        var (technologies, preferred) = ImageTechnologies(root, source);
        var prefs = AppPreferencesIo.Load();
        var vm = new ImageSourceViewModel(root, technologies, preferred, source, makeSchematic,
                                          post: a => Dispatcher.UIThread.Post(a),
                                          options: new ImageTraceOptions { KeepUnderlay = prefs.KeepSourcePictureUnderResult ?? true })
        {
            SchematicOffered = SchematicFromImageOffered,
            ArtworkSides = prefs.LinkSchematicLayoutOrientation ?? true,
            AdvancedExpanded = prefs.ImageDialogAdvancedExpanded ?? false,
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ImageSourceViewModel.AdvancedExpanded)) return;
            var p = AppPreferencesIo.Load();
            p.ImageDialogAdvancedExpanded = vm.AdvancedExpanded ? true : null;
            AppPreferencesIo.Save(p);
        };
        // Read: Automatic | Manual (brief-pdg-4 R-pdg4-5) — the user's choice remembered; Digitise Plot… opens in it.
        vm.Plots.ReadModeChosen += () =>
        {
            var p = AppPreferencesIo.Load();
            p.ImageDialogPlotReadMode = vm.Plots.Manual ? null : "automatic";
            AppPreferencesIo.Save(p);
        };
        vm.Plots.Detailed = prefs.ImageDialogPlotTracesDetailed ?? false;
        vm.Plots.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlotSessionViewModel.Detailed)) return;
            var p = AppPreferencesIo.Load();
            p.ImageDialogPlotTracesDetailed = vm.Plots.Detailed ? true : null;
            AppPreferencesIo.Save(p);
        };

        // Opened from a paste's line (R-imf9-4): the paste's own reading shown, not read again.
        if (source is not null && reading is not null) vm.SeedSchematic(source, reading);
        var dialog = new CreateSchematicFromArtworkDialog(vm);
        vm.LayoutCreated += run =>
        {
            ReportImageLayout(run);
            dialog.Close();
        };
        vm.SchematicCreated += run =>
        {
            ReportImageSchematic(run);
            dialog.Close();
        };
        vm.SchematicPictureCreated += run =>
        {
            _factory.ProjectTreeTool?.Refresh();
            PostImageReport(run.Report, "Create Schematic from Image", run.SchematicPath);
            if (run.SchematicPath is { } schematic) _ = ShowWrittenImageDocument(schematic, OpenOrActivateSchematic, schematic);
            dialog.Close();
        };
        vm.SchematicPictureDrawn += (edit, placed) =>
        {
            DrawImageSchematic(edit, placed);
            dialog.Close();
        };
        vm.TouchstoneCreated += (path, assembly, kept, session) =>
        {
            ReportTouchstone(path, assembly, kept, session);
            dialog.Close();
        };
        vm.TouchstoneAlsoSaved += (path, assembly, kept, session) => ReportTouchstone(path, assembly, kept, session);
        vm.PlotCsvCreated += path =>
        {
            Messages.Post(MessageLevel.Success, $"Digitise Plot: wrote {Path.GetFileName(path)}", path);
            _factory.ProjectTreeTool?.Refresh();
            dialog.Close();
        };
        vm.OpenSchematic = path => _openDocsByPath.ContainsKey(Path.GetFullPath(path)) ? GetOrCreateSession(Path.GetFullPath(path)).EditModel : null;
        dialog.Closed += (_, _) => _imageDialog = null;
        _imageDialog = dialog;
        if (ResolveOwner(owner) is { } window) dialog.Show(window);
        else dialog.Show();
    }

    /// <summary>
    /// R-im5-6: the workspace's technologies, then every circuitRF technology the workspace has no copy of (owner,
    /// 2026-10-27: any existing technology can be chosen, for its layers) — and the one chosen first: the technology of the
    /// layout a placed picture sits in, else the workspace default, else the first of the workspace's, else the catalogue's
    /// default.
    /// </summary>
    internal (IReadOnlyList<ImageTechnologyChoice> Choices, int Preferred) ImageTechnologies(string root, ImageSource? source)
    {
        var paths = new List<string>();
        try
        {
            paths.AddRange(Directory.EnumerateFiles(root, "*.ctech", new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
            }).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        string? preferred = null;
        if (source?.Placement is LayoutBitmapPlacement placed)
            preferred = GetOrCreateLayoutSession(Path.GetFullPath(placed.Document)).ResolvedTechPath;
        if (preferred is null)
            try
            {
                if (WorkspacePersistence.LoadFromFile(Path.Combine(root, ".cws")).DefaultTechRef is { Length: > 0 } techRef)
                    preferred = Path.GetFullPath(CircuitRF.Core.RefPath.Resolve(root, techRef));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { }

        if (preferred is not null && File.Exists(preferred) && !paths.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            paths.Insert(0, preferred);
        var choices = paths.Select(p => new ImageTechnologyChoice(Path.GetFileNameWithoutExtension(p), p)).ToList();
        int index = preferred is null ? 0 : Math.Max(0, paths.FindIndex(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase)));
        // A catalogue technology is offered only when the workspace has no file of its id: the workspace's copy is its own.
        var have = paths.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        choices.AddRange(TechnologyCatalog.All.Where(e => !have.Contains(e.Id)).Select(ImageTechnologyChoice.Catalog));
        if (paths.Count == 0)
            index = Math.Max(0, choices.FindIndex(c => string.Equals(c.CatalogId, TechnologyCatalog.DefaultId, StringComparison.OrdinalIgnoreCase)));
        return (choices, index);
    }

    /// <summary>
    /// Make Layout written: the layout opens focused. Into the open layout (D14), the shapes are added through the
    /// editor's own commands as ONE undo step, and what was added is selected.
    /// </summary>
    private void ReportImageLayout(ImageTraceRun run)
    {
        PostImageReport(run.Report, "Create Layout from Image", run.LayoutPath);
        if (run.Edit is { } edit)
        {
            string clay = Path.GetFullPath(edit.ClayPath);
            OpenOrActivateLayout(clay);
            var layoutVm = GetOrCreateLayoutSession(clay);
            if (ImageTraceEditCommand.For(layoutVm.Model, edit) is { } command)
            {
                layoutVm.Execute(command);
                layoutVm.SelectShapes(edit.Shapes);
            }
            return;
        }
        _factory.ProjectTreeTool?.Refresh();
        if (run.LayoutPath is { } path) _ = ShowWrittenImageDocument(path, p => OpenOrActivateLayout(p), path);
    }

    /// <summary>
    /// Opens <paramref name="show"/>, which Create from Image just wrote — after reloading every open tab showing one of
    /// <paramref name="written"/>. A REPLACE rewrites a cell's layout or schematic in place, and a tab already open on it
    /// holds the model it read: activating it showed the old shapes until it was closed and opened again. It is reloaded
    /// as a history restore reloads a file it brought back — closed, its edit session discarded (an undo would put the
    /// replaced content back onto the new file), and reopened where its tab was. The replace was confirmed and took a
    /// checkpoint first.
    /// </summary>
    private async Task ShowWrittenImageDocument(string show, Action<string> open, params string?[] written)
    {
        if (WorkspaceRootDir is { } root && WrittenAsRestoredPaths(root, written) is { Count: > 0 } changed)
            await ReloadChangedDocuments(changed);
        open(Path.GetFullPath(show));
    }

    /// <summary><paramref name="written"/> as the workspace-relative paths a restore reports.</summary>
    internal static List<CircuitRF.Design.Revision.RestoredPath> WrittenAsRestoredPaths(string root, IEnumerable<string?> written) =>
        [.. written.OfType<string>().Select(p => new CircuitRF.Design.Revision.RestoredPath(
            Path.GetRelativePath(root, Path.GetFullPath(p)).Replace('\\', '/'), CircuitRF.Design.Revision.RestoredPathKind.Written))];

    /// <summary>Make Schematic written: the cell holds both views; the schematic opens focused, and the Tuning panel,
    /// which follows the focused schematic, lists its unknown values (as AS-8 R-as8-4).</summary>
    private void ReportImageSchematic(ImageRecognitionRun run)
    {
        _factory.ProjectTreeTool?.Refresh();
        PostImageReport(run.Report, "Create Schematic from Image", run.SchematicPath);
        if (run.SchematicPath is { } schematic)
            _ = ShowWrittenImageDocument(schematic, OpenOrActivateSchematic, schematic, run.LayoutPath);
    }

    /// <summary>
    /// A schematic picture drawn over the placed bitmap it is (brief-img-10 R-im10-6, D14): the schematic it sits in comes
    /// forward, the drawing is added through the editor's own commands as ONE undo step, and what was added is selected.
    /// </summary>
    private void DrawImageSchematic(CircuitRF.Design.Schematic.Recognition.SchematicImageEdit edit, SchematicBitmapPlacement placed)
    {
        string csch = Path.GetFullPath(placed.Document);
        OpenOrActivateSchematic(csch);
        var schematicVm = GetOrCreateSession(csch);
        if (SchematicImageEditCommand.For(schematicVm, edit) is { } command) schematicVm.Execute(command);
        PostImageReport(edit.Report, "Create Schematic from Image", null);
    }

    /// <summary>
    /// After Create, for a plot (brief-img-14 R-im14-11): the report and the health check's verdict to Messages, the file
    /// a known data source, and a Data Display opened on it with the plot types the pictures showed — so the result is
    /// compared with its source at a glance.
    /// </summary>
    private void ReportTouchstone(string path, CircuitRF.Design.Imaging.Plots.TouchstoneAssemblyResult assembly,
                                  IReadOnlyList<KeptPicture> kept, CircuitRF.Design.Imaging.Plots.PlotSession session)
    {
        const string command = "Create Touchstone from Plot";
        Messages.Post(MessageLevel.Success, $"{command}: wrote {Path.GetFileName(path)}", path);
        foreach (string line in assembly.Report) Messages.Post(MessageLevel.Info, $"{command}: {line}", path);
        foreach (var k in kept.Where(k => !k.Reused)) Messages.Post(MessageLevel.Info, $"{command}: kept {k.Ref} beside it", k.FullPath);
        AddKnownFile(path);
        _factory.ProjectTreeTool?.Refresh();
        var types = session.Plots.Where(p => !p.Excluded)
                           .Select(p => p.Type switch
                           {
                               CircuitRF.Design.Imaging.Plots.PlotType.Smith => PlotType.Smith,
                               CircuitRF.Design.Imaging.Plots.PlotType.Polar => PlotType.Polar,
                               _ => PlotType.Rect,
                           })
                           .Distinct().ToList();
        _ = OpenTouchstoneDisplayAsync(path, types.Count == 0 ? [PlotType.Rect] : types);
    }

    private async Task OpenTouchstoneDisplayAsync(string snp, IReadOnlyList<PlotType> types)
    {
        try
        {
            var vm = new CircuitRF.Ui.DataDisplay.DataDisplayDocumentViewModel();
            var doc = new CircuitRF.Ui.DataDisplay.DataDisplayDocument(Path.GetFileNameWithoutExtension(snp), vm);
            _scratchDataDisplays.Add(doc);
            vm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
            vm.Window.GetResultsRootAction = GetResultsRoot;
            WireDataDisplayLibraryEvents(vm);
            WireDataDisplayTreeDirty(doc);
            _factory.OpenDocument(doc);
            var lib = vm.Window.DataSourceLibrary;
            lib.RefreshAvailableDataSources();
            await lib.SelectDataSourceAsync(lib.AvailableDataSources.FirstOrDefault(s =>
                string.Equals(s.AbsolutePath, Path.GetFullPath(snp), StringComparison.OrdinalIgnoreCase))?.LogicalId
                ?? Path.GetFileName(snp));
            if (vm.Window.DataDisplay is not { } display) return;
            for (int i = 0; i < types.Count; i++)
            {
                var container = i == 0 && display.Plots.FirstOrDefault() is { } first ? first : display.AddPlot(types[i]);
                if (container.Inspector.PlotType != types[i]) container.Inspector.PlotType = types[i];
                if (container.Inspector.AddTraceCommand.CanExecute(null)) container.Inspector.AddTraceCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            Messages.Warning($"Create Touchstone from Plot: the file was written, but no Data Display could be opened on it: {ex.Message}", snp);
        }
    }

    private void PostImageReport(RecognitionReport report, string command, string? written)
    {
        foreach (var finding in report.Findings)
        {
            bool wrote = finding.Class is RecognitionFindingClass.SchematicWritten or RecognitionFindingClass.ImageLayoutWritten;
            Messages.Post(wrote ? MessageLevel.Success : MessageLevel.Info, $"{command}: {finding.Sentence}", wrote ? written : null);
        }
    }
}
