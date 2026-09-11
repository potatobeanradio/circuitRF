using Avalonia.Input;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The arrow-key pan gesture, defined once for every document canvas that pans on the middle mouse
/// button (schematic, symbol, layout, wBond profile, Data Display).
///
/// <para><b>Why it exists at all</b> (owner, 2026-09-11): a trackpad has no middle button, so on a
/// laptop the only way to pan was a two-finger scroll, which every one of these canvases spends on
/// ZOOM instead. The arrow keys are the pan that needs no hardware.</para>
///
/// <para><b>Only when nothing is selected.</b> Arrow keys already nudge the selection in four of
/// these editors, and that gesture is the older one — so the pan is what an arrow key means when
/// there is nothing to nudge, never a replacement for the nudge. Each canvas asks its own view model
/// whether the selection is empty; this class only says how far a step goes.</para>
///
/// <para><b>The step is SCREEN-space, not world-space.</b> A world-space step would move a hair at
/// board zoom and fly off the canvas at via zoom. One step is the same distance on screen at every
/// magnification, which is the only definition that feels the same everywhere.</para>
/// </summary>
internal static class CanvasArrowPan
{
    /// <summary>Device pixels the view travels per keystroke.</summary>
    public const double StepPixels = 40.0;

    /// <summary>Shift multiplies the step — the same "coarse" spelling the nudge gestures use.</summary>
    public const double CoarseMultiplier = 5.0;

    /// <summary>
    /// How far the VIEW moves for <paramref name="key"/>, in device pixels, <b>Y down</b> — so
    /// Right is +X and Down is +Y regardless of which way the canvas's own world Y points. A canvas
    /// with a Y-up world (layout, wBond profile) negates the Y component; one with a Y-down world
    /// (schematic, symbol) does not; the Data Display translates its CONTENT, so it subtracts both.
    ///
    /// <para>Null for anything that is not a bare arrow key. Ctrl/Cmd/Alt arrows are declined
    /// deliberately: those combinations belong to whatever else has claimed them (text navigation,
    /// a menu accelerator), and quietly panning under one would make an unrelated shortcut scroll
    /// the page.</para>
    /// </summary>
    public static (double Dx, double Dy)? ScreenStep(Key key, KeyModifiers modifiers)
    {
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0) return null;

        double step = StepPixels * ((modifiers & KeyModifiers.Shift) != 0 ? CoarseMultiplier : 1.0);
        return key switch
        {
            Key.Left  => (-step,   0.0),
            Key.Right => ( step,   0.0),
            Key.Up    => (  0.0, -step),
            Key.Down  => (  0.0,  step),
            _         => null,
        };
    }
}
