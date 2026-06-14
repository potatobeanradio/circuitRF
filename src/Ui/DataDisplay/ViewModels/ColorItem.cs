using Avalonia.Media;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// A color entry from <see cref="TraceProperties.ColorLUT"/> formatted for
/// display in a ComboBox color picker.  Brush is created once and reused.
/// </summary>
public sealed class ColorItem
{
    public int    Index { get; }
    public Color  Color { get; }
    public string Name  { get; }
    public IBrush Brush { get; }

    public ColorItem(int index, Color color, string name)
    {
        Index = index;
        Color = color;
        Name  = name;
        Brush = new SolidColorBrush(color);
    }

    public override string ToString() => Name;
}
