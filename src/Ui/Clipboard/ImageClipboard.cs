// A picture and nothing else on the system clipboard — the 3D view's Copy. The same two routes as every
// other copy here: on Windows, WindowsClipboard's one P/Invoke session (PNG), with NO text slot, so a
// receiver never prefers an empty string over the image; elsewhere, Avalonia's DataFormat.Bitmap.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
}
