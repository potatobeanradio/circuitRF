using System;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CircuitRF.Design.Plugins;
using SkiaSharp;

namespace CircuitRF.Ui.Plugins.Views;

/// <summary>
/// An <c>image</c> view (brief-plg-3 §5), the escape hatch: a PNG or SVG the plugin drew, letterboxed and fit to the
/// frame — never cropped, never stretched. An SVG is read by <c>Svg.Skia</c>, the loader the documentation's figures
/// are checked with. With <c>clickable</c>, a click on the picture sends <c>event {kind: "click", x, y}</c> in the
/// image's own pixel coordinates (an SVG's user units); a click in the letterbox margin is not on the picture and sends
/// nothing. No drag, no hover, no zoom.
/// </summary>
public sealed class PluginImageViewControl : Control, IPluginViewControl
{
    private readonly PluginViewGesture _gesture;
    private Bitmap? _bitmap;
    private Size _imageSize;
    private bool _clickable;

    public PluginImageViewControl(string name, PluginViewGesture gesture)
    {
        _gesture = gesture;
        ClipToBounds = true;
        PointerPressed += OnPressed;
    }

    public Size ImageSize => _imageSize;

    public string? Show(PluginView view)
    {
        var v = (PluginImageView)view;
        var (bitmap, size, problem) = Load(v.Path);
        if (problem is not null) return $"The {v.Name} view cannot be drawn: {problem}";
        _bitmap?.Dispose();
        (_bitmap, _imageSize, _clickable) = (bitmap, size, v.Clickable);
        Cursor = _clickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        InvalidateVisual();
        return null;
    }

    /// <summary>The picture as a bitmap and its own size: a PNG as itself, an SVG rasterised at twice its size so it is
    /// sharp at the usual panel scale.</summary>
    internal static (Bitmap? Bitmap, Size Size, string? Problem) Load(string path)
    {
        try
        {
            if (!Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var png = new Bitmap(path);
                return (png, png.Size, null);
            }
            using var svg = new Svg.Skia.SKSvg();
            if (svg.Load(path) is not { } picture) return (null, default, $"{Path.GetFileName(path)} is not an SVG that can be drawn.");
            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) return (null, default, $"{Path.GetFileName(path)} has no size.");
            const float Oversample = 2f;
            int w = (int)Math.Ceiling(bounds.Width * Oversample), h = (int)Math.Ceiling(bounds.Height * Oversample);
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(Oversample);
            surface.Canvas.Translate(-bounds.Left, -bounds.Top);
            surface.Canvas.DrawPicture(picture);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());
            return (new Bitmap(stream), new Size(bounds.Width, bounds.Height), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, default, $"{Path.GetFileName(path)} could not be read ({e.Message}).");
        }
    }

    /// <summary>Where the picture is drawn in this control: fit, centred, aspect kept.</summary>
    public Rect PictureRect()
    {
        if (_imageSize.Width <= 0 || _imageSize.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return default;
        double s = Math.Min(Bounds.Width / _imageSize.Width, Bounds.Height / _imageSize.Height);
        double w = _imageSize.Width * s, h = _imageSize.Height * s;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    /// <summary>A point in this control as a point on the picture, in its own pixels; null in the margin.</summary>
    public Point? ToImage(Point p)
    {
        var r = PictureRect();
        if (r.Width <= 0 || !r.Contains(p)) return null;
        double s = r.Width / _imageSize.Width;
        return new Point((p.X - r.X) / s, (p.Y - r.Y) / s);
    }

    /// <summary>A click at <paramref name="p"/> in this control; true when it was on the picture and sent.</summary>
    public bool Click(Point p)
    {
        if (!_clickable || ToImage(p) is not { } at) return false;
        _gesture("click", new JsonObject { ["x"] = Math.Round(at.X, 3), ["y"] = Math.Round(at.Y, 3) }, null, null);
        return true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (Click(e.GetPosition(this))) e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is null) return;
        var r = PictureRect();
        if (r.Width > 0) context.DrawImage(_bitmap, new Rect(_bitmap.Size), r);
    }
}
