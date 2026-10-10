using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Ui.Recognition;

/// <summary>The picture source of the Create … from Image dialog (R-im5-2). The thumbnail is drawn here, from the
/// decoded picture, so the view model holds no bitmap of the UI framework's.</summary>
public partial class ImageSourcePane : UserControl
{
    private const int ThumbSide = 88;
    private ImageSourceViewModel? _vm;

    public ImageSourcePane() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ImageSourceViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        RefreshThumbnail();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageSourceViewModel.Source)) RefreshThumbnail();
    }

    private void RefreshThumbnail()
    {
        (Thumbnail.Source as IDisposable)?.Dispose();
        Thumbnail.Source = _vm?.Raster is { } r ? Thumb(r) : null;
    }

    /// <summary>The picture reduced to the thumbnail's size by nearest sampling — enough to recognise it by.</summary>
    private static WriteableBitmap Thumb(RasterImage r)
    {
        double s = Math.Min(1.0, (double)ThumbSide / Math.Max(r.Width, r.Height));
        int w = Math.Max(1, (int)Math.Round(r.Width * s)), h = Math.Max(1, (int)Math.Round(r.Height * s));
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(r.Height - 1, (int)(y / s));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(r.Width - 1, (int)(x / s));
                Buffer.BlockCopy(r.Rgba, (sy * r.Width + sx) * 4, row, x * 4, 4);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    }
}
