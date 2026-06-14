// ================================================================
//  DragSelectOverlay.cs
//
//  Transparent overlay placed above all canvas content.
//  Renders the rubber-band selection rectangle while the user
//  is drag-selecting items on the Data Display background.
//
//  IsHitTestVisible="False" in XAML — pointer events pass through.
//  The owning canvas view drives state via SetSelectionRect.
// ================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace CircuitRF.Ui.DataDisplay.Controls;

internal sealed class DragSelectOverlay : Control
{
    private Rect? _selectionRect;

    /// <summary>
    /// Sets the current selection rect in control-local pixel coordinates.
    /// Pass <see langword="null"/> to hide the overlay.
    /// </summary>
    internal void SetSelectionRect(Rect? rect)
    {
        _selectionRect = rect;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_selectionRect is not { } rect || rect.Width < 1 || rect.Height < 1)
            return;

        // Resolve accent colour on the UI thread — same source as plot/InfoBox highlights.
        // Fill uses low alpha so content behind the rect remains readable.
        // Stroke uses a higher alpha so the border is clearly visible.
        var skFill   = RenderTheme.GetTransparentAccent(40);
        var skStroke = RenderTheme.GetTransparentAccent(170);

        var fill   = new ImmutableSolidColorBrush(
            new Color(skFill.Alpha,   skFill.Red,   skFill.Green,   skFill.Blue));
        var stroke = new ImmutableSolidColorBrush(
            new Color(skStroke.Alpha, skStroke.Red, skStroke.Green, skStroke.Blue));

        context.DrawRectangle(fill, new Pen(stroke, 1.0), rect);
    }
}
