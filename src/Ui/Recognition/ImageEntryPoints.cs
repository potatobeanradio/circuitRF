using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Recognition;

/// <summary>Opens the Create … from Image dialog — the workspace's, reached from a canvas or the Project Tree.</summary>
public interface IImageDialogHost
{
    /// <summary>The dialog, empty or already reading <paramref name="source"/>, with Make preset.</summary>
    void ShowImageDialog(ImageSource? source, bool makeSchematic, Window? owner);

    /// <summary>The dialog on <paramref name="read"/>'s picture; a picture that cannot be read is said in Messages.</summary>
    void ShowImageDialogFor(ImageSourceResult read, bool makeSchematic, Window? owner);
}

/// <summary>One right-click row on a placed picture.</summary>
/// <param name="MakeSchematic">Make preset: a schematic (true) or a layout.</param>
/// <param name="IntoPlaced">Its target is the document the picture sits in, over the picture (D14).</param>
public sealed record ImageContextRow(string Header, string Tip, bool MakeSchematic, bool IntoPlaced);

/// <summary>
/// The ways into the Create … from Image dialog that need deciding (brief-img-6-entry-points.md R-im6-3, R-im6-4): which
/// rows a placed picture offers, and which tree files are pictures. <b>Never offered where it cannot work</b> — a
/// bitmap whose file does not resolve gets none of these rows (Resolve Path… is the row that is there), and the tree
/// decides by extension so building a menu never decodes a file.
/// </summary>
public static class ImageEntryPoints
{
    public const string CreateSchematicHeader = "Create Schematic from Image…";
    public const string CreateLayoutHeader = "Create Layout from Image…";
    public const string TraceIntoLayoutHeader = "Trace Image into This Layout…";

    public const string CreateSchematicTip =
        "Read a picture of a layout and write it as a schematic of native components in a new cell, reviewed first in a parts table.";
    public const string CreateLayoutTip =
        "Trace a picture of a layout into the copper, vias and outline of a new layout cell.";
    public const string TraceIntoLayoutTip =
        "Trace this picture into this layout, over the picture, at the size it is placed — one undo step.";
    public const string PasteSchematicTip =
        "Read the picture on the clipboard and write it as a schematic in a new cell. Enabled when the clipboard holds a picture.";
    public const string PasteLayoutTip =
        "Trace the picture on the clipboard into a new layout cell. Enabled when the clipboard holds a picture.";

    /// <summary>
    /// A layout bitmap's rows: <i>Trace Image into This Layout…</i> (scale <i>As placed</i>, target this layout, its
    /// technology) and <i>Create Schematic from Image…</i> (a new cell, <i>As placed</i> offered as scale evidence).
    /// None when the picture does not resolve. A locked bitmap is offered too — locking stops dragging, not reading.
    /// A layout never saved has no file to trace into, so it offers only the new cell.
    /// </summary>
    public static IReadOnlyList<ImageContextRow> LayoutBitmapRows(string? clayPath, BitmapShape bitmap)
    {
        if (!Resolves(clayPath, bitmap.ImagePathRef)) return [];
        return clayPath is null
            ? [new(CreateSchematicHeader, CreateSchematicTip, true, false)]
            : [new(TraceIntoLayoutHeader, TraceIntoLayoutTip, false, true), new(CreateSchematicHeader, CreateSchematicTip, true, false)];
    }

    /// <summary>
    /// A schematic bitmap's rows: <i>Create Schematic from Image…</i> and <i>Create Layout from Image…</i>, each a new
    /// cell. A schematic has no physical scale, so <i>As placed</i> is not offered. None when the picture does not
    /// resolve.
    /// </summary>
    public static IReadOnlyList<ImageContextRow> SchematicBitmapRows(string? cschPath, EditableBitmap bitmap) =>
        !Resolves(cschPath, bitmap.ImagePath) ? []
            : [new(CreateSchematicHeader, CreateSchematicTip, true, false), new(CreateLayoutHeader, CreateLayoutTip, false, false)];

    /// <summary>
    /// The picture a placed bitmap shows, as the dialog reads it: placed in its document when there is one, else the
    /// file itself.
    /// </summary>
    public static ImageSourceResult SourceOf(string? clayPath, BitmapShape bitmap) =>
        clayPath is not null ? ImageSource.FromLayoutBitmap(clayPath, bitmap) : ImageSource.FromFile(bitmap.ImagePathRef);

    /// <inheritdoc cref="SourceOf(string?, BitmapShape)"/>
    public static ImageSourceResult SourceOf(string? cschPath, EditableBitmap bitmap) =>
        cschPath is not null ? ImageSource.FromSchematicBitmap(cschPath, bitmap) : ImageSource.FromFile(bitmap.ImagePath);

    /// <summary>True when <paramref name="reference"/> names a file that is there — the primitive's own rule: a rooted
    /// reference wins, a relative one is relative to the document. A document never saved resolves only a rooted one.</summary>
    public static bool Resolves(string? documentPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (documentPath is null) return Path.IsPathRooted(reference) && File.Exists(reference);
        string dir = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        return File.Exists(CircuitRF.Core.RefPath.Resolve(dir, reference));
    }
}
