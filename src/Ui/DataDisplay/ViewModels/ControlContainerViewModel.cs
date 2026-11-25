// ================================================================
//  ControlContainerViewModel.cs  —  one display control on the canvas
//  (brief-ddl-5 R-ddl5-2, R-ddl5-4, R-ddl5-5, R-ddl5-9)
//
//  Beside PlotContainerViewModel: a frame in display logical units, put
//  on screen with the canvas zoom and pan. The FACE operates (a value
//  drag, a click on the track, an arrow key) and the FRAME arranges
//  (move, resize). Every change is one undo step: a drag is recorded
//  when it ends, never per pointer move, and a value change marks the
//  document dirty because the value is saved.
// ================================================================

using System;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed partial class ControlContainerViewModel : ViewModelBase
{
    internal DataDisplayViewModel Display { get; }

    /// <summary>The control itself — the same object the file holds.</summary>
    public DisplayControl Model { get; }

    internal ControlContainerViewModel(DisplayControl model, DataDisplayViewModel display)
    {
        Model = model;
        Display = display;
    }

    /// <summary>Raised when the drawing must be redone — a value, a look or the theme changed.</summary>
    public event Action? RedrawRequested;

    // ---- geometry: logical, and on screen --------------------------------------------------------

    public double Left
    {
        get => Model.Left;
        set { if (Model.Left == value) return; Model.Left = value; OnPropertyChanged(nameof(ViewLeft)); }
    }

    public double Top
    {
        get => Model.Top;
        set { if (Model.Top == value) return; Model.Top = value; OnPropertyChanged(nameof(ViewTop)); }
    }

    private double Zoom => Display.ZoomLevel > 0 ? Display.ZoomLevel : 1.0;
    public double ViewLeft   => Model.Left * Zoom + Display.ViewOffsetX;
    public double ViewTop    => Model.Top  * Zoom + Display.ViewOffsetY;
    public double ViewWidth  => Model.Width  * Zoom;
    public double ViewHeight => Model.Height * Zoom;

    /// <summary>The canvas zoomed or panned.</summary>
    internal void NotifyViewProperties()
    {
        OnPropertyChanged(nameof(ViewLeft));
        OnPropertyChanged(nameof(ViewTop));
        OnPropertyChanged(nameof(ViewWidth));
        OnPropertyChanged(nameof(ViewHeight));
        RedrawRequested?.Invoke();
    }

    /// <summary>
    /// The model changed under the view (an undo, a redo, an inspector edit, a paste): every derived property, the
    /// drawing, the links that follow it and the inspector are refreshed.
    /// </summary>
    internal void Changed()
    {
        NotifyViewProperties();
        OnPropertyChanged(nameof(IsLocked));
        Display.OnControlChanged(this);
    }

    // ---- selection and lock -----------------------------------------------------------------------

    [ObservableProperty] private bool _isSelected;

    /// <summary>Stacking among the controls: a control selected comes to the front, as a selected plot does.</summary>
    [ObservableProperty] private int _zIndex;

    partial void OnIsSelectedChanged(bool value) => RedrawRequested?.Invoke();

    public bool IsLocked => Model.Locked;

    public void RequestSelectOnly()  => Display.SelectOnly(this);
    public void RequestToggleSelect() => Display.ToggleSelect(this);

    // ---- operate: the value -----------------------------------------------------------------------

    private DisplayControl? _valueStart;

    /// <summary>A press on the face: the value drag begins, and the press itself sets the value (a click on the track
    /// jumps there).</summary>
    internal void BeginValueDrag(SKPoint viewPoint)
    {
        _valueStart = Model.Clone();
        if (Model is KnobControl knob)
        {
            // A knob is turned by the drag, not set by where it is pressed — except a bound knob's tick, which a
            // press sets (R-ddl6-1).
            if (ControlRenderer.KnobTickAt(knob, (float)ViewWidth, (float)ViewHeight, viewPoint) is { } tick && knob.SetStop(tick))
            {
                RedrawRequested?.Invoke();
                Display.RequestLinkFollow(released: false);
            }
            BeginKnobTurn(viewPoint, fine: false);
            return;
        }
        DragValueTo(viewPoint);
    }

    // ---- the knob: a drag turns it (R-ddl6-1) --------------------------------------------------------

    // Up turns it up, at one sweep per KnobControl.PixelsPerSweep pixels, as the brief asks. Right turns it up too —
    // clockwise across the top of the dial, the way the dial itself goes — at its own rate: a drag across the dial's
    // width (its outer diameter, ticks included) is one whole sweep, so moving across the knob sweeps its arc at any
    // size. Down and left turn it down; a diagonal drag adds the two. Ctrl makes both ten times finer.
    private double _turnFrom, _turnX, _turnY, _lastTurnX, _lastTurnY;
    private bool _turnFine;

    /// <summary>Takes where the knob is now as the start of a turn: the drag from <paramref name="at"/> turns it from
    /// here. Called again when Ctrl is pressed or let go mid-drag, so the rate changes without a jump.</summary>
    internal void BeginKnobTurn(SKPoint at, bool fine)
    {
        _turnFrom = Model is SteppedControl s ? s.Fraction : 0;
        _turnX = _lastTurnX = at.X;
        _turnY = _lastTurnY = at.Y;
        _turnFine = fine;
    }

    /// <summary>The fraction of a sweep a drag of (<paramref name="dx"/>, <paramref name="dy"/>) screen pixels turns
    /// the knob: up and right positive.</summary>
    private double TurnOf(KnobControl k, double dx, double dy, bool fine)
    {
        var l = ControlRenderer.KnobLayout(k, (float)ViewWidth, (float)ViewHeight);
        double across = Math.Max(24, 2 * (l.Radius + l.TickRoom));
        double finer = fine ? 10 : 1;
        return -dy / (KnobControl.PixelsPerSweep * finer) + dx / (across * finer);
    }

    /// <summary>The knob with the pointer at <paramref name="at"/> (control-relative screen pixels).</summary>
    internal void TurnKnob(SKPoint at, bool fine)
    {
        if (Model is not KnobControl k) return;
        if (fine != _turnFine)
        {
            // The rate changes from where the pointer last was, so pressing Ctrl mid-drag never jumps the knob.
            _turnFrom = Math.Clamp(_turnFrom + TurnOf(k, _lastTurnX - _turnX, _lastTurnY - _turnY, _turnFine), 0, 1);
            _turnX = _lastTurnX;
            _turnY = _lastTurnY;
            _turnFine = fine;
        }
        _lastTurnX = at.X;
        _lastTurnY = at.Y;
        if (!k.SetFraction(_turnFrom + TurnOf(k, at.X - _turnX, at.Y - _turnY, fine))) return;
        RedrawRequested?.Invoke();
        Display.RequestLinkFollow(released: false);
    }

    // ---- the button (R-ddl6-2) ----------------------------------------------------------------------

    /// <summary>
    /// A press on a button's face: a toggle flips, one undo step, saved; a momentary button lights while held and is
    /// neither recorded nor saved — it is a gesture, not a setting. Links follow either way.
    /// </summary>
    internal void PressButton()
    {
        if (Model is not ButtonControl b) return;
        if (b.Mode == ButtonMode.Toggle)
        {
            var before = Model.Clone();
            b.On = !b.On;
            RedrawRequested?.Invoke();
            Record(before, b.On ? "on" : "off");
        }
        else
        {
            if (b.On) return;
            b.On = true;
            Gesture.Note("control.press", Model.Name);
            RedrawRequested?.Invoke();
        }
        Display.RequestLinkFollow(released: true);
    }

    /// <summary>The press ended (released, or the pointer lost): a momentary button goes dark.</summary>
    internal void ReleaseButton()
    {
        if (Model is not ButtonControl { Mode: ButtonMode.Momentary, On: true } b) return;
        b.On = false;
        Gesture.Note("control.release", Model.Name);
        RedrawRequested?.Invoke();
        Display.RequestLinkFollow(released: true);
    }

    /// <summary>A readout's text changed with the links it shows: drawn again.</summary>
    internal void RequestRedraw() => RedrawRequested?.Invoke();

    /// <summary>The value under <paramref name="viewPoint"/> (control-relative, screen pixels); links follow live.</summary>
    internal void DragValueTo(SKPoint viewPoint)
    {
        if (Model is KnobControl) { TurnKnob(viewPoint, _turnFine); return; }
        if (Model is not SliderControl s) return;
        int stop = ControlRenderer.StopAt(s, (float)ViewWidth, (float)ViewHeight, viewPoint);
        if (!s.SetStop(stop)) return;
        RedrawRequested?.Invoke();
        Display.RequestLinkFollow(released: false);
    }

    /// <summary>The drag ended: one undo step when the value moved, and the slow consumers resolve now.</summary>
    internal void EndValueDrag()
    {
        if (_valueStart is not { } start) return;
        _valueStart = null;
        Record(start, "value");
        Display.RequestLinkFollow(released: true);
    }

    internal bool IsDraggingValue => _valueStart is not null;

    private int _relativeStop;
    private SKPoint _relativeFrom;

    /// <summary>
    /// A locked control's frame drag (R-ddl5-9): the value moves BY the drag, from where it is — never jumps to the
    /// pointer, so a move or a resize that missed nudges the reading rather than throwing it across the range.
    /// </summary>
    internal void BeginRelativeValueDrag(SKPoint viewPoint)
    {
        _valueStart = Model.Clone();
        _relativeFrom = viewPoint;
        _relativeStop = Model is SteppedControl s ? s.StopIndex : 0;
        if (Model is KnobControl) BeginKnobTurn(viewPoint, fine: false);
    }

    internal void DragValueRelative(SKPoint viewPoint)
    {
        if (Model is KnobControl) { TurnKnob(viewPoint, _turnFine); return; }
        if (Model is not SliderControl s) return;
        var l = ControlRenderer.Layout(s, (float)ViewWidth, (float)ViewHeight);
        float dx = l.TrackEnd.X - l.TrackStart.X, dy = l.TrackEnd.Y - l.TrackStart.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        int n = s.Stops.Count;
        if (len <= 0 || n < 2) return;
        float along = ((viewPoint.X - _relativeFrom.X) * dx + (viewPoint.Y - _relativeFrom.Y) * dy) / len;
        int stop = _relativeStop + (int)Math.Round(along / (len / (n - 1)));
        if (!s.SetStop(stop)) return;
        RedrawRequested?.Invoke();
        Display.RequestLinkFollow(released: false);
    }

    /// <summary>An arrow key on a selected control: one stop, one undo step (as a selected marker steps).</summary>
    public bool Step(int direction)
    {
        if (Model is not SteppedControl s) return false;
        var start = Model.Clone();
        if (!s.SetStop(s.StopIndex + direction)) return false;
        RedrawRequested?.Invoke();
        Record(start, "step");
        Display.RequestLinkFollow(released: true);
        return true;
    }

    // ---- arrange: size ----------------------------------------------------------------------------

    private DisplayControl? _resizeStart;

    internal void BeginResize()
    {
        _resizeStart = Model.Clone();
        Display.BringToFront(this);   // a control being enlarged is drawn over its neighbours
    }

    /// <summary>The gripper: a free size in logical units, which makes the size Custom (R-ddl5-2).</summary>
    internal void ResizeTo(double width, double height)
    {
        Model.Width  = Math.Max(MinWidth, width);
        Model.Height = Math.Max(MinHeight, height);
        Model.Size   = ControlSize.Custom;
        NotifyViewProperties();
    }

    internal void EndResize()
    {
        if (_resizeStart is not { } start) return;
        _resizeStart = null;
        Record(start, "resize");
    }

    /// <summary>The smallest frame the gripper leaves (<see cref="DisplayControl.MinimumSize"/>): the Small preset for
    /// most kinds, much smaller for a button.</summary>
    private double MinWidth  => Model.MinimumSize().Width;
    private double MinHeight => Model.MinimumSize().Height;

    // ---- edits ------------------------------------------------------------------------------------

    /// <summary>
    /// Records the change from <paramref name="before"/> to the model as it is now as one undo step — nothing when
    /// nothing changed. The change is already applied; the command only remembers both ends.
    /// </summary>
    internal void Record(DisplayControl before, string what)
    {
        var after = Model.Clone();
        if (System.Text.Json.JsonSerializer.Serialize(before, DataDisplayJson.Options)
            == System.Text.Json.JsonSerializer.Serialize(after, DataDisplayJson.Options)) return;
        Gesture.Note("control." + what, Model.Name);
        Display.UndoRedo.Push(new EditControlCommand(this, before, after));
        Changed();
    }

    /// <summary>Applies <paramref name="edit"/> to the model as one undo step — what the inspector and the context menu
    /// do.</summary>
    internal void Edit(Action<DisplayControl> edit, string what)
    {
        var before = Model.Clone();
        edit(Model);
        Record(before, what);
    }
}
