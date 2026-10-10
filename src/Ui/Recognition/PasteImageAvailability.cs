using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Recognition;

/// <summary>What the clipboard holds, as far as a picture goes — named, never decoded.</summary>
/// <param name="HasBitmap">A bitmap is among its formats.</param>
/// <param name="Files">The files on it, by path (a picture copied in a file manager).</param>
public sealed record PictureClipboardContents(bool HasBitmap, IReadOnlyList<string> Files)
{
    public static readonly PictureClipboardContents Nothing = new(false, []);
}

/// <summary>The clipboard, as Edit ▸ Paste Image as … reads it (brief-img-6-entry-points.md R-im6-2). The window's is
/// <c>ImageClipboard</c>'s; a test hands a fake.</summary>
public interface IPictureClipboard
{
    /// <summary>What it holds — its formats and its file names, nothing decoded.</summary>
    Task<PictureClipboardContents> PeekAsync();

    /// <summary>The picture: a copied picture file read as itself, else the bitmap encoded to PNG. Null when there is
    /// none.</summary>
    Task<ImageSourceResult?> ReadAsync();
}

/// <summary>
/// Whether Edit ▸ Paste Image as Schematic… / as Layout… are enabled (R-im6-2): the clipboard holds a picture. Asked
/// only when <see cref="RefreshAsync"/> is called — just before the Edit menu shows and when the window is activated —
/// and <b>never polled</b>. A read that throws, or does not answer within <see cref="PeekTimeout"/>, leaves the rows
/// disabled.
/// </summary>
public sealed partial class PasteImageAvailability : ObservableObject
{
    /// <summary>How long a menu waits on the clipboard before it shows the rows disabled.</summary>
    public static readonly TimeSpan PeekTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Func<IPictureClipboard?> _clipboard;
    private int _generation;

    public PasteImageAvailability(Func<IPictureClipboard?> clipboard) => _clipboard = clipboard;

    [ObservableProperty] private bool _available;

    /// <summary>Raised when <see cref="Available"/> changes — the commands re-query.</summary>
    public event Action? Changed;

    partial void OnAvailableChanged(bool value) => Changed?.Invoke();

    /// <summary>
    /// A picture is offered when the clipboard holds exactly one file IM-1 reads, or — holding no files — a bitmap.
    /// <b>Files decide when there are any</b>: a file manager puts the copied files' ICONS on the clipboard as a
    /// bitmap beside them, so two copied files, or one text file, would otherwise offer to read a folder icon.
    /// </summary>
    public static bool Offers(PictureClipboardContents c) =>
        c.Files.Count > 0 ? c.Files.Count == 1 && IsPictureFile(c.Files[0]) : c.HasBitmap;

    /// <summary>A path whose extension IM-1 reads — extension only, nothing opened.</summary>
    public static bool IsPictureFile(string path) =>
        RasterImage.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Asks the clipboard once. The latest call wins when two overlap.</summary>
    public async Task RefreshAsync()
    {
        int generation = ++_generation;
        bool offers = false;
        try
        {
            if (_clipboard() is { } clipboard)
            {
                var peek = clipboard.PeekAsync();
                if (await Task.WhenAny(peek, Task.Delay(PeekTimeout)).ConfigureAwait(true) == peek)
                    offers = Offers(await peek.ConfigureAwait(true));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { offers = false; }
        if (generation == _generation) Available = offers;
    }

    /// <summary>The clipboard's picture, read for the dialog; null when there is none or the read fails.</summary>
    public async Task<ImageSourceResult?> ReadAsync()
    {
        try { return _clipboard() is { } clipboard ? await clipboard.ReadAsync().ConfigureAwait(true) : null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
