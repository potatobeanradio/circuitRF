using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Messages;
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
/// (brief-img-6-entry-points.md) — the Design-menu rows, Edit ▸ Paste Image as …, a placed picture's right-click and
/// the Project Tree's picture rows — calls <see cref="ShowImageDialog"/>. What is written is written by
/// <see cref="ImageTrace.Run"/> and <see cref="ImageRecognition.Run"/> — the functions the CLI calls (overview D2).</para>
/// </summary>
public partial class WorkspaceViewModel : IImageDialogHost
{
    private CreateSchematicFromArtworkDialog? _imageDialog;
    private PasteImageAvailability? _pasteImage;

    // ── the Design menu (R-im6-1): enabled whenever a workspace is open ──────────────────────────────

    private bool IsWorkspaceOpenForImage() => CurrentWorkspaceRoot is not null;

    [RelayCommand(CanExecute = nameof(IsWorkspaceOpenForImage))]
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

    [RelayCommand(CanExecute = nameof(CanPasteImage))]
    private Task PasteImageAsSchematic(Window? owner) => PasteImageAs(makeSchematic: true, owner);

    [RelayCommand(CanExecute = nameof(CanPasteImage))]
    private Task PasteImageAsLayout(Window? owner) => PasteImageAs(makeSchematic: false, owner);

    private async Task PasteImageAs(bool makeSchematic, Window? owner)
    {
        string command = makeSchematic ? "Paste Image as Schematic" : "Paste Image as Layout";
        var read = await PasteImage.ReadAsync();
        if (read is null) Messages.Error($"{command}: the clipboard holds no picture.");
        else if (!read.Ok) Messages.Error($"{command}: {read.Refusal}");
        else ShowImageDialog(read.Source, makeSchematic, owner);
    }

    private void RaiseImageCommandsChanged()
    {
        CreateSchematicFromImageCommand.NotifyCanExecuteChanged();
        CreateLayoutFromImageCommand.NotifyCanExecuteChanged();
        PasteImageAsSchematicCommand.NotifyCanExecuteChanged();
        PasteImageAsLayoutCommand.NotifyCanExecuteChanged();
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
    public void ShowImageDialog(ImageSource? source, bool makeSchematic, Window? owner)
    {
        if (CurrentWorkspaceRoot is not { } root)
        {
            Messages.Error("Create from Image: open a workspace first — the result is a new cell in it.");
            return;
        }
        if (_imageDialog is { } open)
        {
            if (source is not null && open.DataContext is ImageSourceViewModel reading) reading.Read(source, makeSchematic);
            open.Activate();
            return;
        }

        var (technologies, preferred) = ImageTechnologies(root, source);
        var prefs = AppPreferencesIo.Load();
        var vm = new ImageSourceViewModel(root, technologies, preferred, source, makeSchematic,
                                          post: a => Dispatcher.UIThread.Post(a),
                                          options: new ImageTraceOptions { KeepUnderlay = prefs.KeepSourcePictureUnderResult ?? true })
        {
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
        dialog.Closed += (_, _) => _imageDialog = null;
        _imageDialog = dialog;
        if (ResolveOwner(owner) is { } window) dialog.Show(window);
        else dialog.Show();
    }

    /// <summary>
    /// R-im5-6: the workspace's technologies, and the one chosen first — the technology of the layout a placed picture
    /// sits in, else the workspace default, else the first.
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
        if (run.LayoutPath is { } path) OpenOrActivateLayout(Path.GetFullPath(path));
    }

    /// <summary>Make Schematic written: the cell holds both views; the schematic opens focused, and the Tuning panel,
    /// which follows the focused schematic, lists its unknown values (as AS-8 R-as8-4).</summary>
    private void ReportImageSchematic(ImageRecognitionRun run)
    {
        _factory.ProjectTreeTool?.Refresh();
        PostImageReport(run.Report, "Create Schematic from Image", run.SchematicPath);
        if (run.SchematicPath is { } schematic) OpenOrActivateSchematic(Path.GetFullPath(schematic));
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
