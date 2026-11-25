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
            OnPropertyChanged(nameof(Locked));
            OnPropertyChanged(nameof(LineStyle));
            OnPropertyChanged(nameof(ColorSwatch));
            OnPropertyChanged(nameof(FillSwatch));
            OnPropertyChanged(nameof(HasFill));
            OnPropertyChanged(nameof(FillOpacity));
            OnPropertyChanged(nameof(CanFill));
            OnPropertyChanged(nameof(IsLine));
            OnPropertyChanged(nameof(LineWidth));
            OnPropertyChanged(nameof(StartArrow));
            OnPropertyChanged(nameof(EndArrow));
        };
    }

    internal CanvasAnnotation? Annotation => _display.SelectedAnnotation;
    public bool IsActive => Annotation is not null;
    public bool IsText => Annotation is CanvasText;
    public bool HasStroke => Annotation is CanvasLine or CanvasBox or CanvasOval;
    public bool CanFill => Annotation is CanvasBox or CanvasOval;
    public bool IsLine => Annotation is CanvasLine;
    public string ShapeName => Annotation switch { CanvasBox => "Box", CanvasOval => "Oval", _ => "Line" };
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

    /// <summary>A text's Lock Position.</summary>
    public bool Locked
    {
        get => (Annotation as CanvasText)?.Locked ?? false;
        set { if (Annotation is CanvasText text) Edit(text with { Locked = value }); }
    }

    public CanvasTextRotation Rotation
    {
        get => (Annotation as CanvasText)?.Rotation ?? CanvasTextRotation.R0;
        set { if (Annotation is CanvasText text) Edit(text with { Rotation = value }); }
    }

    public CanvasLineStyle LineStyle
    {
        get => Annotation switch { CanvasLine line => line.Style, CanvasBox box => box.Style, CanvasOval oval => oval.Style, _ => CanvasLineStyle.Solid };
        set
        {
            if (Annotation is CanvasLine line) Edit(line with { Style = value });
            else if (Annotation is CanvasBox box) Edit(box with { Style = value });
            else if (Annotation is CanvasOval oval) Edit(oval with { Style = value });
        }
    }

    public IBrush? ColorSwatch => Color.TryParse(Annotation?.Color, out var color) ? new SolidColorBrush(color) : null;
    public IBrush? FillSwatch => Color.TryParse(Annotation?.FillColor, out var fill) ? new SolidColorBrush(fill) : null;

    public decimal LineWidth
    {
        get => (decimal)Math.Clamp(Annotation?.LineWidth ?? 1.5, 0.25, 40);
        set { if (Annotation is { } annotation) Edit(annotation with { LineWidth = (double)Math.Clamp(value, 0.25m, 40m) }); }
    }

    public bool StartArrow
    {
        get => Annotation is CanvasLine { StartArrow: true };
        set { if (Annotation is CanvasLine line) Edit(line with { StartArrow = value }); }
    }

    public bool EndArrow
    {
        get => Annotation is CanvasLine { EndArrow: true };
        set { if (Annotation is CanvasLine line) Edit(line with { EndArrow = value }); }
    }

    public bool HasFill
    {
        get => Annotation?.FillColor is not null;
        set { if (Annotation is { } annotation) Edit(annotation with { FillColor = value ? "#4080A0C0" : null }); }
    }

    public double FillOpacity
    {
        get => Color.TryParse(Annotation?.FillColor, out var color) ? color.A * 100.0 / 255 : 0;
        set
        {
            if (Annotation is not { } annotation || !Color.TryParse(annotation.FillColor, out var color)) return;
            var alpha = (byte)Math.Round(Math.Clamp(value, 0, 100) * 255 / 100);
            Edit(annotation with { FillColor = new Color(alpha, color.R, color.G, color.B).ToString() });
        }
    }

    internal void SetFillColor(string color)
    {
        if (Annotation is { } annotation) Edit(annotation with { FillColor = color });
    }

    internal void SetColor(string? color)
    {
        if (Annotation is { } annotation) Edit(annotation with { Color = color });
    }

    private void Edit(CanvasAnnotation annotation) => _display.EditAnnotation(_display.SelectedAnnotationIndex, annotation);
}