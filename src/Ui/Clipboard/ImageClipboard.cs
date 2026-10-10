// A picture and nothing else on the system clipboard — the 3D view's Copy. The same two routes as every
// other copy here: on Windows, WindowsClipboard's one P/Invoke session (PNG), with NO text slot, so a
// receiver never prefers an empty string over the image; elsewhere, Avalonia's DataFormat.Bitmap.
//
// And the read (brief-img-6-entry-points.md R-im6-2) on the same two routes: Avalonia's clipboard everywhere,
// with the registered PNG format read first on Windows when it is there.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using SkiaSharp;

namespace CircuitRF.Ui.Clipboard;

internal static class ImageClipboard
{
    /// <summary>
    /// An Avalonia bitmap over <paramref name="pixels"/>' own RGBA8 — copied once, never encoded to PNG and
    /// decoded back, which at 4× a window is hundreds of megabytes each way.
    /// </summary>
    public static Bitmap FromSkia(SKBitmap pixels)
        => new(PixelFormat.Rgba8888, AlphaFormat.Premul, pixels.GetPixels(), new PixelSize(pixels.Width, pixels.Height),
               new Vector(96, 96), pixels.RowBytes);

    /// <summary>Puts <paramref name="bitmap"/> on the clipboard of <paramref name="anchor"/>'s window. False when
    /// there is no clipboard to write to.</summary>
    public static async Task<bool> SetAsync(Control anchor, Bitmap bitmap)
    {
        if (OperatingSystem.IsWindows())
        {
            IntPtr hwnd = TopLevel.GetTopLevel(anchor)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            WindowsClipboard.SetClipboard(hwnd, null, null, null, bitmap, 0, 0);
            return true;
        }
        if (TopLevel.GetTopLevel(anchor)?.Clipboard is not { } clipboard) return false;
        var item = new DataTransferItem();
        item.Set(DataFormat.Bitmap, bitmap);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
        return true;
    }

    /// <summary>What <paramref name="top"/>'s clipboard holds: whether a bitmap is among its formats, and its files by
    /// path. Nothing is decoded.</summary>
    public static async Task<PictureClipboardContents> PeekAsync(TopLevel top)
    {
        if (top.Clipboard is not { } clipboard) return PictureClipboardContents.Nothing;
        var formats = await clipboard.GetDataFormatsAsync();
        IReadOnlyList<string> files = [];
        if (formats.Contains(DataFormat.File))
            files = [.. (await clipboard.TryGetFilesAsync() ?? []).Select(f => f.TryGetLocalPath() ?? f.Name)];
        bool bitmap = formats.Contains(DataFormat.Bitmap) || (OperatingSystem.IsWindows() && WindowsClipboard.HasPng());
        return new PictureClipboardContents(bitmap, files);
    }

    /// <summary>
    /// The clipboard's picture: a single copied picture file read as itself; else, on Windows, the registered PNG
    /// format's own bytes; else the bitmap, encoded to PNG. Null when it holds none of these.
    /// </summary>
    public static async Task<ImageSourceResult?> TryReadAsync(TopLevel top)
    {
        if (top.Clipboard is not { } clipboard) return null;
        var files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 })
            return files.Length == 1 && files[0].TryGetLocalPath() is { } path && File.Exists(path) ? ImageSource.FromFile(path) : null;
        if (OperatingSystem.IsWindows() && WindowsClipboard.TryGetPng(top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero) is { } png)
            return ImageSource.FromBytes(png);
        if (await clipboard.TryGetBitmapAsync() is not { } bitmap) return null;
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return ImageSource.FromBytes(stream.ToArray());
    }
}

/// <summary>A window's clipboard, as <see cref="PasteImageAvailability"/> reads it.</summary>
internal sealed class TopLevelPictureClipboard(TopLevel top) : IPictureClipboard
{
    public Task<PictureClipboardContents> PeekAsync() => ImageClipboard.PeekAsync(top);
    public Task<ImageSourceResult?> ReadAsync() => ImageClipboard.TryReadAsync(top);
}
