using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CircuitRF.Ui.Recognition;

/// <summary>
/// The picture dialog's body (R-im5-2, R-im5-3). The window owns only what needs a window; this places the measure
/// tools' inline boxes at the point clicked, flashes a hovered layer row's colour, and takes a dropped file.
/// </summary>
public partial class ImageReadingPanel : UserControl
{
    private ImageSourceViewModel? _vm;

    public ImageReadingPanel()
    {
        InitializeComponent();
        Canvas.ViewChanged += PlaceInline;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.Scale.PropertyChanged -= OnScaleChanged;
        _vm = DataContext as ImageSourceViewModel;
        if (_vm is not null) _vm.Scale.PropertyChanged += OnScaleChanged;
    }

    private void OnScaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageScaleViewModel.PromptAt) or nameof(ImageScaleViewModel.AskingDistance)
            or nameof(ImageScaleViewModel.AskingZ0))
        {
            PlaceInline();
            // The box takes the keyboard as it appears, so the distance is typed straight after the second click.
            if (_vm?.Scale.AskingDistance == true) Dispatcher.UIThread.Post(() => DistanceInput.Focus());
            else if (_vm?.Scale.AskingZ0 == true) Dispatcher.UIThread.Post(() => { Z0Input.Focus(); Z0Input.SelectAll(); });
        }
    }

    /// <summary>The inline box beside the point it asks about.</summary>
    private void PlaceInline()
    {
        if (_vm?.Scale.PromptAt is not { } p) return;
        var at = Canvas.ToView(p.X, p.Y);
        foreach (var box in new Control[] { DistanceBox, Z0Box })
        {
            double x = Math.Clamp(at.X + 12, 4, Math.Max(4, Canvas.Bounds.Width - 320));
            double y = Math.Clamp(at.Y + 12, 4, Math.Max(4, Canvas.Bounds.Height - 80));
            Avalonia.Controls.Canvas.SetLeft(box, x);
            Avalonia.Controls.Canvas.SetTop(box, y);
        }
    }

    private void OnDistanceKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Scale.ApplyDistanceCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Scale.CancelToolCommand.Execute(null); e.Handled = true; }
    }

    private void OnZ0Key(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.Scale.ApplyZ0Command.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm?.Scale.CancelToolCommand.Execute(null); e.Handled = true; }
    }

    private void OnLayerEnter(object? sender, PointerEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is ImageLayerRowViewModel row) _vm.HoveredLayer = row;
    }

    private void OnLayerExit(object? sender, PointerEventArgs e)
    {
        if (_vm is not null && ReferenceEquals((sender as Control)?.DataContext, _vm.HoveredLayer)) _vm.HoveredLayer = null;
    }

    // ── a dropped picture (R-im5-2) ─────────────────────────────────────────────────────────────────

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not null && DroppedPath(e) is { } path) _vm.LoadFile(path);
        e.Handled = true;
    }

    /// <summary>The first file a drop carries — whatever it is: a file that is not a picture is refused by the read, in
    /// the status line, not silently here. The payload's type varies by platform (one item on macOS, a list elsewhere).</summary>
    internal static string? DroppedPath(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            string? path = item.TryGetRaw(DataFormat.File) switch
            {
                IStorageItem single => single.Path?.LocalPath,
                IEnumerable<IStorageItem> files => files.FirstOrDefault()?.Path?.LocalPath,
                string s => s,
                _ => null,
            };
            if (path is not null) return path;
        }
        return null;
    }
}
