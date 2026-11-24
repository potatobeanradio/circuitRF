// ================================================================
//  ControlContainerView.cs  —  a display control on the canvas
//  (brief-ddl-5 R-ddl5-2, R-ddl5-4, R-ddl5-6, R-ddl5-9)
//
//  Drawn by ControlRenderer — the code Copy, Export and `render` draw it
//  with — plus the window's chrome (selection, gripper, lock glyph).
//
//  THE FACE OPERATES, THE FRAME ARRANGES. A press on the track or thumb
//  sets the value and a drag slides it; a press on the label strip or
//  the border selects, and a drag past the threshold moves the control
//  (with the rest of the selection). Alt-drag moves it from anywhere. The
//  bottom-right gripper resizes. A LOCKED control is never moved or
//  resized: its frame drag slides the value instead, its gripper is not
//  shown, and Alt does nothing more than a plain press.
//
//  A RANGE slider's end labels are its start and stop: a double-click on
//  one opens an in-place text box over it (the unit kept, the number
//  selected), with the application's three-key contract — Return and
//  losing focus commit, Escape reverts. A bound slider's ends are its
//  axis's samples and are not edited here.
// ================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Material.Icons;
using Material.Icons.Avalonia;
using SkiaSharp;

namespace CircuitRF.Ui.Views.DataDisplay;

public sealed class ControlContainerView : Panel
{
    // A Panel's own Render is sealed, so the control is drawn by a child that fills it; the end editor floats in a
    // canvas laid over it.
    private readonly Surface _surface;
    private readonly Canvas  _editorHost = new();

    private sealed class Surface(ControlContainerView owner) : Control
    {
        public override void Render(DrawingContext context) => owner.RenderSurface(context);
    }

    private void Redraw() => _surface.InvalidateVisual();

    private const double DragThreshold = 4.0;

    private enum Mode { None, Pending, Value, Move, Resize }

    private Mode _gesture;
    private bool    _pendingAlt, _pendingCtrl;
    private Point   _pressRoot, _lastRoot;
    private bool    _gripHot;
    /// <summary>The pointer is over the face, or a value drag is under way: the thumb shows its halo.</summary>
    private bool    _thumbHot;
    /// <summary>A press on a locked control's gripper corner: it neither resizes nor operates.</summary>
    private bool    _inert;
    /// <summary>The last press was on the face: the arrow keys are this control's until focus leaves, selected or not —
    /// someone who has just slid the thumb expects the arrows to nudge it.</summary>
    private bool    _faceFocused;

    private ControlContainerViewModel? Vm => DataContext as ControlContainerViewModel;

    /// <summary>The value drag under way is a locked frame drag, relative to where it began.</summary>
    private bool _relative;

    public ControlContainerView()
    {
        Focusable = true;
        ClipToBounds = false;
        _surface = new Surface(this);
        Children.Add(_surface);
        Children.Add(_editorHost);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.RedrawRequested += Redraw;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ControlContainerViewModel.IsSelected) or nameof(ControlContainerViewModel.IsLocked))
                    Redraw();
            };
        }
    }

    // ---- drawing ---------------------------------------------------------------------------------

    private void RenderSurface(DrawingContext context)
    {
        if (Vm is not { } vm) return;
        // Captured here, on the UI thread: the draw runs on the compositor thread.
        context.Custom(new DrawOperation(
            new Rect(_surface.Bounds.Size), vm.Model.Clone(), vm.Display.Theme,
            vm.IsSelected, vm.Model.Locked, _gripHot || _gesture == Mode.Resize, _thumbHot || _gesture == Mode.Value,
            PlotAccentColor.GetTransparentAccent(RenderTheme.SelectionAlpha)));
    }

    private sealed class DrawOperation(Rect bounds, DisplayControl control, RenderTheme theme,
                                       bool selected, bool locked, bool grip, bool thumb, SKColor selection) : ICustomDrawOperation
    {
        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var l = lease.Lease();
            float w = (float)bounds.Width, h = (float)bounds.Height;
            ControlRenderer.Draw(l.SkCanvas, control, theme, new SKRect(0, 0, w, h));
            ControlRenderer.DrawChrome(l.SkCanvas, control, w, h, selected, locked, grip, thumb, selection);
        }

        public void Dispose() { }
    }

    // ---- hit regions -----------------------------------------------------------------------------

    private bool OnGrip(Point p) => ControlRenderer.OnGrip((float)Bounds.Width, (float)Bounds.Height, Sk(p));

    private bool OnFace(ControlContainerViewModel vm, Point p)
        => ControlRenderer.HitFace(vm.Model, (float)Bounds.Width, (float)Bounds.Height, new SKPoint((float)p.X, (float)p.Y));

    private static SKPoint Sk(Point p) => new((float)p.X, (float)p.Y);

    private Point RootPoint(PointerEventArgs e) => TopLevel.GetTopLevel(this) is { } root ? e.GetPosition(root) : e.GetPosition(this);

    // ---- pointer ---------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Vm is not { } vm || FromEditor(e)) return;
        var point = e.GetCurrentPoint(this);
        var p = point.Position;

        // A double-click on a range slider's end label edits that end in place.
        if (point.Properties.IsLeftButtonPressed && e.ClickCount >= 2 && EndAt(vm, p) is { } end)
        {
            _gesture = Mode.None;
            OpenEndEditor(vm, end.IsStart, end.Label);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            ShowMenu(vm);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        Focus();
        _faceFocused = false;
        bool alt  = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        _pressRoot = _lastRoot = RootPoint(e);

        _inert = vm.IsLocked && OnGrip(p);
        if (!vm.IsLocked && OnGrip(p))
        {
            _gesture = Mode.Resize;
            vm.BeginResize();
        }
        else if (!alt && OnFace(vm, p))
        {
            // The face operates at once: a click on the track jumps the thumb there.
            _gesture = Mode.Value;
            _faceFocused = true;
            vm.BeginValueDrag(Sk(p));
        }
        else
        {
            // The frame (or Alt anywhere): a click selects, a drag moves — or, locked, slides the value.
            _gesture = Mode.Pending;
            _pendingAlt = alt;
            _pendingCtrl = ctrl;
        }
        e.Pointer.Capture(this);
        e.Handled = true;   // not an empty-canvas press: no rubber band, no pan
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Vm is not { } vm || FromEditor(e)) return;
        var p = e.GetPosition(this);

        if (_gesture == Mode.None)
        {
            bool hot = !vm.IsLocked && OnGrip(p);
            bool thumb = !hot && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && OnFace(vm, p);
            if (hot != _gripHot || thumb != _thumbHot) { _gripHot = hot; _thumbHot = thumb; Redraw(); }
            Cursor = hot ? new Cursor(StandardCursorType.BottomRightCorner)
                   : EndAt(vm, p) is not null ? new Cursor(StandardCursorType.Ibeam)
                   : !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && OnFace(vm, p) ? new Cursor(StandardCursorType.Hand)
                   : vm.IsLocked ? Cursor.Default
                   : new Cursor(StandardCursorType.SizeAll);
            return;
        }
        if (e.Pointer.Captured != this) return;

        var root = RootPoint(e);
        switch (_gesture)
        {
            case Mode.Value:
                if (_relative) vm.DragValueRelative(Sk(p));
                else           vm.DragValueTo(Sk(p));
                break;

            case Mode.Resize:
            {
                double zoom = vm.Display.ZoomLevel > 0 ? vm.Display.ZoomLevel : 1.0;
                vm.ResizeTo(Math.Max(1, p.X) / zoom, Math.Max(1, p.Y) / zoom);
                break;
            }

            case Mode.Pending:
            {
                double dx = root.X - _pressRoot.X, dy = root.Y - _pressRoot.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;
                // Alt only moves, and a locked control does not move; its gripper corner does nothing at all.
                if (vm.IsLocked && (_pendingAlt || _inert)) return;
                if (vm.IsLocked)
                {
                    // A locked control's frame drag operates the face instead (R-ddl5-9) — by the drag, from where the
                    // thumb is, so a slip never throws the value to wherever the pointer happens to be.
                    _gesture = Mode.Value;
                    _relative = true;
                    var from = e.GetPosition(this);
                    vm.BeginRelativeValueDrag(new SKPoint((float)(from.X - (root.X - _pressRoot.X)), (float)(from.Y - (root.Y - _pressRoot.Y))));
                    vm.DragValueRelative(Sk(p));
                    return;
                }
                _gesture = Mode.Move;
                vm.Display.BeginMoveFromControl(vm);
                _lastRoot = _pressRoot;
                goto case Mode.Move;
            }

            case Mode.Move:
            {
                double zoom = vm.Display.ZoomLevel > 0 ? vm.Display.ZoomLevel : 1.0;
                vm.Display.MoveSelected((root.X - _lastRoot.X) / zoom, (root.Y - _lastRoot.Y) / zoom);
                _lastRoot = root;
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (FromEditor(e)) return;
        if (Vm is not { } vm || e.Pointer.Captured != this) { _gesture = Mode.None; return; }
        // The gesture is taken BEFORE capture is let go: releasing capture raises CaptureLost, which would otherwise
        // end it as a cut-short gesture and leave a click with nothing to do.
        var gesture = _gesture;
        _gesture = Mode.None;
        _relative = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        switch (gesture)
        {
            case Mode.Value:  vm.EndValueDrag(); break;
            case Mode.Resize: vm.EndResize(); break;
            case Mode.Move:   vm.Display.EndMoveOperation(); break;
            case Mode.Pending:
                // A click on the frame selects; Ctrl/Cmd toggles it in the selection.
                if (_pendingCtrl) vm.RequestToggleSelect();
                else              vm.RequestSelectOnly();
                break;
        }
        Redraw();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _faceFocused = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if ((_gripHot && _gesture != Mode.Resize) || (_thumbHot && _gesture != Mode.Value)) { _gripHot = false; _thumbHot = false; Redraw(); }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // A gesture cut short still ends as one step, never half of one.
        if (Vm is { } vm)
            switch (_gesture)
            {
                case Mode.Value:  vm.EndValueDrag(); break;
                case Mode.Resize: vm.EndResize(); break;
                case Mode.Move:   vm.Display.EndMoveOperation(); break;
            }
        _gesture = Mode.None;
    }

    // ---- keys ------------------------------------------------------------------------------------

    /// <summary>A selected control steps its value with the arrow keys, as a selected marker does.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _endEditor is not null || Vm is not { } vm || !(vm.IsSelected || _faceFocused)
            || e.KeyModifiers != KeyModifiers.None) return;
        int dir = e.Key switch { Key.Right or Key.Up => +1, Key.Left or Key.Down => -1, _ => 0 };
        if (dir == 0) return;
        vm.Step(dir);
        e.Handled = true;   // at an end too: the arrow is the control's, not the canvas's
    }

    // ---- editing a range's ends in place ---------------------------------------------------------

    private TextBox? _endEditor;

    /// <summary>True for an event that started in the open end editor — it is the text box's, not the slider's.</summary>
    private bool FromEditor(RoutedEventArgs e)
        => _endEditor is { } box && e.Source is Visual v && (ReferenceEquals(v, box) || box.IsVisualAncestorOf(v));

    /// <summary>The end label under <paramref name="p"/> on a range slider (with a little slack), or null.</summary>
    private (bool IsStart, ControlRenderer.EndLabel Label)? EndAt(ControlContainerViewModel vm, Point p)
    {
        if (vm.Model is not SliderControl { Domain: SliderDomain.Range } s) return null;
        if (ControlRenderer.EndLabels(s, (float)Bounds.Width, (float)Bounds.Height) is not { } ends) return null;
        var at = Sk(p);
        if (Inflate(ends.Lo.Rect).Contains(at)) return (true, ends.Lo);
        if (Inflate(ends.Hi.Rect).Contains(at)) return (false, ends.Hi);
        return null;

        static SKRect Inflate(SKRect r) { r.Inflate(4, 3); return r; }
    }

    private void OpenEndEditor(ControlContainerViewModel vm, bool isStart, ControlRenderer.EndLabel label)
    {
        CloseEndEditor();
        var s = (SliderControl)vm.Model;
        // The end in the slider's own unit, as it is written in the inspector: "1000 MHz", not the label's "1 GHz".
        double end = isStart ? s.Start : s.Stop;
        string text = s.Unit.Length > 0
            ? $"{end.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)} {s.Unit}"
            : end.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        double fontSize = Math.Max(11, label.FontSize);
        double width = Math.Max(InlineEdit.WidthFromMeasuredText(
                           InlineEdit.MeasureWidth(text, fontSize, new Typeface(FontFamily.Default)), fontSize) + 10, 64);
        var box = new TextBox
        {
            Name = "SliderEndEditor", Text = text, FontSize = fontSize, Width = width, MinHeight = 0,
            Padding = new Thickness(3, 1), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        // Anchored on the label's own edge — the start's left, the stop's right (its text is right-aligned there).
        bool alignRight = !isStart && s.Orientation == ControlOrientation.Horizontal;
        Canvas.SetLeft(box, alignRight ? label.Rect.Right - width + 3 : label.Rect.Left - 4);
        Canvas.SetTop(box, label.Rect.MidY - (fontSize + 8) / 2);
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitEndEditor(vm, isStart); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseEndEditor(); Focus(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => { if (ReferenceEquals(_endEditor, box)) CommitEndEditor(vm, isStart); };

        _endEditor = box;
        _editorHost.Children.Add(box);
        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd = InlineEdit.ValueSelectionLength(text);
        CircuitRF.Ui.DataDisplay.Gesture.Note("control.editEnd", $"{s.Name} {(isStart ? "start" : "stop")}");
    }

    /// <summary>Takes the typed end as one undo step; anything that does not read as a number in the slider's unit, or
    /// would leave the range with no width, is dropped and the end stays as it was.</summary>
    private void CommitEndEditor(ControlContainerViewModel vm, bool isStart)
    {
        if (_endEditor is not { } box) return;
        string? typed = box.Text;
        CloseEndEditor();
        Focus();
        var s = (SliderControl)vm.Model;
        if (ControlLinkSource.ParseRangeEnd(s, typed, out _) is not { } v || !double.IsFinite(v)) return;
        double other = isStart ? s.Stop : s.Start;
        if (v == other || (s.Log && v <= 0)) return;
        vm.Edit(m =>
        {
            var sl = (SliderControl)m;
            // The value stays where it was in absolute terms; it snaps to the nearest of the new stops when read.
            if (isStart) sl.Start = v; else sl.Stop = v;
            sl.Value = sl.Stops[SliderControl.NearestStop(sl.Stops, sl.Value)];
        }, isStart ? "start" : "stop");
    }

    private void CloseEndEditor()
    {
        if (_endEditor is not { } box) return;
        _endEditor = null;
        _editorHost.Children.Remove(box);
    }

    // ---- the context menu ------------------------------------------------------------------------

    private void ShowMenu(ControlContainerViewModel vm)
    {
        bool locked = vm.Display.AllLocked(vm);
        var lockItem = new MenuItem
        {
            Header = "Lock Position",
            Icon = new MaterialIcon { Kind = locked ? MaterialIconKind.CheckboxOutline : MaterialIconKind.CheckboxBlankOutline },
        };
        lockItem.Click += (_, _) => vm.Display.ToggleLock(vm);

        var delete = new MenuItem { Header = "Delete", Icon = new MaterialIcon { Kind = MaterialIconKind.DeleteOutline } };
        delete.Click += (_, _) =>
        {
            if (!vm.IsSelected) vm.RequestSelectOnly();
            vm.Display.DeleteSelected();
        };

        // Built fresh each time, so its tick reads the selection as it is now; set as this control's own menu, where
        // anything looking for the open menu finds it.
        var menu = new ContextMenu { Items = { lockItem, new Separator(), delete } };
        ContextMenu = menu;
        menu.Open(this);
    }
}
