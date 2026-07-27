using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>The 8 bbox points + centre + the layout origin (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md
/// §2.1) — the numeric "Scale…" dialog's anchor picker.</summary>
public enum ScaleAnchorKind { Center, BottomLeft, Bottom, BottomRight, Left, Right, TopLeft, Top, TopRight, Origin }

public sealed record ScaleAnchorItem(ScaleAnchorKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Result of the "Scale…" dialog: the settled factors and the anchor's WORLD (DBU)
/// coordinates — the caller passes both straight to <see cref="LayoutEditorViewModel.ApplyScale"/>.</summary>
public sealed record ScaleDialogResult(double FactorX, double FactorY, long AnchorX, long AnchorY);

/// <summary>
/// "Scale…" — factor and target-size fields linked live (§2.1: "typing 1.5 updates the target
/// dimensions, typing 2.9mm into the width updates the factor"), uniform by default with an unlock
/// for separate X/Y, and an anchor picker. A live preview shows the resulting bbox in display units.
/// Mirrors <see cref="OffsetDialog"/>/<see cref="FlattenToPolygonDialog"/>'s shape: a <see cref="Window"/>
/// returning a typed result via <c>ShowDialog&lt;T&gt;</c>, or null on cancel.
///
/// <b>This is a thin shim over <see cref="ScaleFieldLinker"/> — and, per
/// docs/sonnet-briefs/brief-L1h-fix-scale-dialog-width.md, it is deliberately kept as thin as a shim
/// can be.</b> The linker's exact-factor math was ALREADY correct through two prior fix attempts; the
/// bug that survived both was policy living here, in the one layer that cannot be constructed in this
/// project's headless test suite (a <see cref="Window"/> subclass). This file now holds no policy at
/// all: <c>Commit</c> forwards a LostFocus/Enter commit to <see cref="ScaleFieldLinker.Edit"/>, and
/// <c>RefreshFields</c> loops the four boxes writing whatever <see cref="ScaleFieldLinker.DisplayFor"/>
/// returns — which is null, and therefore skipped, for the field the user is actively editing. There
/// is no <c>skip*</c> flag to pass correctly or forget; the linker enforces "never write back the
/// field the user just typed into" by construction.
///
/// <b>Commit convention is LostFocus + Enter, never TextChanged</b> (matching every other typed
/// dimension field in this editor — see <c>LayoutShapePropertiesView.axaml.cs</c>'s header comment).
/// Per-keystroke commit was the actual root cause: it created a re-entrancy window where a
/// programmatic write to one box (echoing a just-typed value in another) could itself raise a live
/// commit, deriving an exact factor from an already-rounded display string.
/// </summary>
public partial class ScaleDialog : Window
{
    private readonly LayoutEditorViewModel? _vm;
    private readonly Bbox _bbox;
    private readonly ScaleFieldLinker? _linker;
    private bool _updating;

    public ScaleDialog() => InitializeComponent();

    public ScaleDialog(LayoutEditorViewModel vm) : this()
    {
        _vm = vm;
        _bbox = vm.SelectionBbox();
        _linker = new ScaleFieldLinker(
            _bbox.IsEmpty ? 1 : _bbox.MaxX - _bbox.MinX,
            _bbox.IsEmpty ? 1 : _bbox.MaxY - _bbox.MinY,
            vm.DisplayUnit, vm.Model.DbuPerMicron);

        AnchorCombo.ItemsSource = new List<ScaleAnchorItem>
        {
            new(ScaleAnchorKind.Center, "Selection center"),
            new(ScaleAnchorKind.BottomLeft, "Bottom-left"),
            new(ScaleAnchorKind.Bottom, "Bottom"),
            new(ScaleAnchorKind.BottomRight, "Bottom-right"),
            new(ScaleAnchorKind.Left, "Left"),
            new(ScaleAnchorKind.Right, "Right"),
            new(ScaleAnchorKind.TopLeft, "Top-left"),
            new(ScaleAnchorKind.Top, "Top"),
            new(ScaleAnchorKind.TopRight, "Top-right"),
            new(ScaleAnchorKind.Origin, "Layout origin (0,0)"),
        };
        AnchorCombo.SelectedIndex = 0;
        AnchorCombo.SelectionChanged += (_, _) => UpdatePreview();

        UniformCheck.IsCheckedChanged += (_, _) =>
        {
            _linker.IsUniform = UniformCheck.IsChecked != false;
            FactorYBox.IsVisible = !_linker.IsUniform;
            RefreshFields();
        };
        FactorYBox.IsVisible = false;

        RefreshFields();
        Opened += (_, _) => { FactorBox.Focus(); FactorBox.SelectAll(); };
    }

    // ── Commit: LostFocus + Enter only — never TextChanged (R-fix-1). ─────────────

    private void OnFactorLostFocus(object? sender, RoutedEventArgs e) => Commit(ScaleField.FactorX, FactorBox);
    private void OnFactorKeyDown(object? sender, KeyEventArgs e) => HandleKeyDown(e, ScaleField.FactorX, FactorBox);

    private void OnFactorYLostFocus(object? sender, RoutedEventArgs e) => Commit(ScaleField.FactorY, FactorYBox);
    private void OnFactorYKeyDown(object? sender, KeyEventArgs e) => HandleKeyDown(e, ScaleField.FactorY, FactorYBox);

    private void OnWidthLostFocus(object? sender, RoutedEventArgs e) => Commit(ScaleField.Width, WidthBox);
    private void OnWidthKeyDown(object? sender, KeyEventArgs e) => HandleKeyDown(e, ScaleField.Width, WidthBox);

    private void OnHeightLostFocus(object? sender, RoutedEventArgs e) => Commit(ScaleField.Height, HeightBox);
    private void OnHeightKeyDown(object? sender, KeyEventArgs e) => HandleKeyDown(e, ScaleField.Height, HeightBox);

    private void HandleKeyDown(KeyEventArgs e, ScaleField field, TextBox box)
    {
        if (e.Key is Key.Return or Key.Enter) { Commit(field, box); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    /// <summary>Forwards a user commit to <see cref="ScaleFieldLinker.Edit"/> (which records
    /// <paramref name="field"/> as authoritative) and then refreshes every box from the linker's
    /// current display strings. No policy lives here — see the type-level doc comment.</summary>
    private void Commit(ScaleField field, TextBox box)
    {
        if (_updating || _linker is null) return;
        if (field is ScaleField.Width or ScaleField.Height && _bbox.IsEmpty) return;
        if (field == ScaleField.FactorY && _linker.IsUniform) return;
        if (!_linker.Edit(field, box.Text ?? "")) { UpdatePreview(); return; }
        RefreshFields();
    }

    /// <summary>Writes every box from <see cref="ScaleFieldLinker.DisplayFor"/> — null (the
    /// authoritative field, per R-fix-2) is skipped unconditionally, so the field the user is
    /// actively editing is never written back, and R-fix-4's no-op guard means an unchanged box
    /// never re-raises a change notification either.</summary>
    private void RefreshFields()
    {
        if (_linker is null) return;
        _updating = true;
        try
        {
            SetIfChanged(FactorBox, _linker.DisplayFor(ScaleField.FactorX));
            SetIfChanged(FactorYBox, _linker.DisplayFor(ScaleField.FactorY));
            if (!_bbox.IsEmpty)
            {
                SetIfChanged(WidthBox, _linker.DisplayFor(ScaleField.Width));
                SetIfChanged(HeightBox, _linker.DisplayFor(ScaleField.Height));
            }
        }
        finally
        {
            _updating = false;
        }
        UpdatePreview();
    }

    private static void SetIfChanged(TextBox box, string? text)
    {
        if (text is null) return;
        if (!string.Equals(box.Text, text, System.StringComparison.Ordinal)) box.Text = text;
    }

    // ── Anchor + live preview ──────────────────────────────────────────────────

    private (long X, long Y) ResolveAnchor()
    {
        if (AnchorCombo.SelectedItem is not ScaleAnchorItem item) return (0, 0);
        if (item.Kind == ScaleAnchorKind.Origin) return (0, 0);
        if (_bbox.IsEmpty) return (0, 0);

        long x1 = _bbox.MinX, y1 = _bbox.MinY, x2 = _bbox.MaxX, y2 = _bbox.MaxY;
        return item.Kind switch
        {
            ScaleAnchorKind.Center      => ((x1 + x2) / 2, (y1 + y2) / 2),
            ScaleAnchorKind.BottomLeft  => (x1, y1),
            ScaleAnchorKind.Bottom      => ((x1 + x2) / 2, y1),
            ScaleAnchorKind.BottomRight => (x2, y1),
            ScaleAnchorKind.Left        => (x1, (y1 + y2) / 2),
            ScaleAnchorKind.Right       => (x2, (y1 + y2) / 2),
            ScaleAnchorKind.TopLeft     => (x1, y2),
            ScaleAnchorKind.Top         => ((x1 + x2) / 2, y2),
            ScaleAnchorKind.TopRight    => (x2, y2),
            _                           => (0, 0),
        };
    }

    private void UpdatePreview()
    {
        if (_linker is null || _bbox.IsEmpty) { PreviewText.Text = "No selection."; return; }
        string unit = LayoutUnits.Suffix(_vm!.DisplayUnit);
        PreviewText.Text = $"Result: {_linker.WidthText} × {_linker.HeightText} {unit}";
    }

    // ── Commit ──────────────────────────────────────────────────────────────────

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void TryCommit()
    {
        if (_linker is null) return;
        double fx = _linker.FactorX;
        double fy = _linker.IsUniform ? _linker.FactorX : _linker.FactorY;
        if (fx <= 0 || fy <= 0) return;

        var (ax, ay) = ResolveAnchor();
        Close(new ScaleDialogResult(fx, fy, ax, ay));
    }
}
