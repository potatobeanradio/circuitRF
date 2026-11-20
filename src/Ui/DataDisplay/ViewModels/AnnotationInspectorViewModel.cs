using System;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed class AnnotationInspectorViewModel : ObservableObject
{
    private readonly DataDisplayViewModel _display;

    internal AnnotationInspectorViewModel(DataDisplayViewModel display)
    {
        _display = display;
        display.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DataDisplayViewModel.SelectedAnnotation)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(HasStroke));
            OnPropertyChanged(nameof(ShapeName));
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(Rotation));
            OnPropertyChanged(nameof(RotationIndex));
            OnPropertyChanged(nameof(LineStyle));
            OnPropertyChanged(nameof(ColorSwatch));
        };
    }

    internal CanvasAnnotation? Annotation => _display.SelectedAnnotation;
    public bool IsActive => Annotation is not null;
    public bool IsText => Annotation is CanvasText;
    public bool HasStroke => Annotation is CanvasLine or CanvasBox;
    public string ShapeName => Annotation is CanvasBox ? "Box" : "Line";
    public CanvasLineStyle[] LineStyles { get; } = Enum.GetValues<CanvasLineStyle>();
    public CanvasTextRotation[] Rotations { get; } = Enum.GetValues<CanvasTextRotation>();

    /// <summary>The rotation picker's rows, in degrees, in the order of <see cref="Rotations"/>.</summary>
    public string[] RotationLabels { get; } = Enum.GetValues<CanvasTextRotation>().Select(r => $"{(int)r}°").ToArray();

    public int RotationIndex
    {
        get => Array.IndexOf(Rotations, Rotation);
        set { if (value >= 0 && value < Rotations.Length) Rotation = Rotations[value]; }
    }

    public string Content
    {
        get => (Annotation as CanvasText)?.Text ?? "";
        set
        {
            if (Annotation is not CanvasText text) return;
            if (string.IsNullOrWhiteSpace(value)) OnPropertyChanged(nameof(Content));
            else Edit(text with { Text = value });
        }
    }

    public decimal FontSize
    {
        get => Annotation is CanvasText text ? (decimal)Math.Clamp(text.FontSize, 1, 200) : 16;
        set { if (Annotation is CanvasText text) Edit(text with { FontSize = (double)Math.Clamp(value, 1, 200) }); }
    }

    public CanvasTextRotation Rotation
    {
        get => (Annotation as CanvasText)?.Rotation ?? CanvasTextRotation.R0;
        set { if (Annotation is CanvasText text) Edit(text with { Rotation = value }); }
    }

    public CanvasLineStyle LineStyle
    {
        get => Annotation switch { CanvasLine line => line.Style, CanvasBox box => box.Style, _ => CanvasLineStyle.Solid };
        set
        {
            if (Annotation is CanvasLine line) Edit(line with { Style = value });
            else if (Annotation is CanvasBox box) Edit(box with { Style = value });
        }
    }

    public IBrush? ColorSwatch => Color.TryParse(Annotation?.Color, out var color) ? new SolidColorBrush(color) : null;

    internal void SetColor(string? color)
    {
        if (Annotation is { } annotation) Edit(annotation with { Color = color });
    }

    private void Edit(CanvasAnnotation annotation) => _display.EditAnnotation(_display.SelectedAnnotationIndex, annotation);
}