// Where a traced layout goes — brief-img-3-trace-layout-image.md R-im3-7 (D4, D14).
//
//   NewCell(parent, name) — a new cell with a layout view, the technology reference, the picture kept beside it
//                           (ImageKeep) and the underlay pointing at that copy; the provenance block marks it as ours.
//   IntoLayout            — the layout the picture's bitmap sits in, OVER the bitmap. Returned as an EDIT — the shapes
//                           — that the GUI applies as one undo step through the editor's own commands. Never a file
//                           write, so AS D4's "never replace" rule is not engaged.
//   Replace(cellDir)      — a cell whose primary layout carries this command's ImageSource block: a history checkpoint
//                           first, then the layout is rewritten. A layout without one is never replaced.

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Recognition.Image;

public enum ImageTraceTargetKind { NewCell, IntoLayout, Replace }

/// <summary>A target (D14).</summary>
/// <param name="ParentDir">Where a new cell is made.</param>
/// <param name="CellName">The new cell's name.</param>
/// <param name="CellDir">The cell whose layout is replaced.</param>
public sealed record ImageTraceTarget(ImageTraceTargetKind Kind, string? ParentDir = null, string? CellName = null, string? CellDir = null)
{
    /// <summary>The checkpoint's intent before a replace.</summary>
    public const string ReplaceIntent = "Create Layout from Image";

    public static ImageTraceTarget NewCell(string parentDir, string name) => new(ImageTraceTargetKind.NewCell, parentDir, name);

    /// <summary>The layout the picture's bitmap sits in, over the bitmap.</summary>
    public static ImageTraceTarget IntoLayout { get; } = new(ImageTraceTargetKind.IntoLayout);

    public static ImageTraceTarget Replace(string cellDir) => new(ImageTraceTargetKind.Replace, CellDir: cellDir);

    /// <summary>The name a new cell is offered: the picture's file name, made a valid cell name, else
    /// <c>pasted_image_&lt;n&gt;</c> — the first <paramref name="parentDir"/> does not already hold.</summary>
    public static string DefaultCellName(ImageSource source, string parentDir)
    {
        string stem = Path.GetFileNameWithoutExtension(source.OriginalName);
        bool pasted = source.Origin == ImageOrigin.Bytes && source.OriginalName == ImageSource.PastedName;
        string name = pasted ? "" : new string(stem.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()).Trim('_');
        if (name.Length > 0 && !char.IsLetter(name[0])) name = "img_" + name;
        if (name.Length > 0 && NameValidator.Validate(name) is null && !Exists(parentDir, name)) return name;
        string root = name.Length > 0 && NameValidator.Validate(name) is null ? name : "pasted_image";
        for (int n = 1; ; n++)
        {
            string candidate = $"{root}_{n}";
            if (!Exists(parentDir, candidate)) return candidate;
        }
    }

    private static bool Exists(string parent, string name) =>
        Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name));

    /// <summary>The primary layout of <paramref name="cellDir"/>, or null.</summary>
    internal static string? PrimaryLayout(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        return primary.ResolvedName is { } name ? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name) : null;
    }

    /// <summary>Whether <paramref name="cellDir"/>'s primary layout was written by this command — it carries an
    /// <c>ImageSource</c> block — and so may be replaced.</summary>
    public static bool IsReplaceable(string cellDir)
    {
        if (PrimaryLayout(cellDir) is not { } path) return false;
        try { return LayoutPersistence.LoadFromFile(path).ImageSource is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary>Shapes to add to an open layout — applied by the GUI as ONE undo step (R-im3-7).</summary>
/// <param name="ClayPath">The layout they go into: the one the picture's bitmap sits in.</param>
public sealed record ImageTraceEdit(string ClayPath, IReadOnlyList<LayoutShape> Shapes);

public sealed record ImageTraceRunOptions
{
    /// <summary>The checkpoint a replace takes first: the layout's path and the intent in, an error out. Null is the real
    /// one — <see cref="WorkspaceCheckpoints.BeforeWrite"/>.</summary>
    public Func<string, string, string?>? Checkpoint { get; init; }

    /// <summary>The time the provenance records; null is now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>What one run did.</summary>
/// <param name="LayoutPath">The written <c>.clay</c>, or null (an edit, or a refusal).</param>
/// <param name="Edit">The shapes for the open layout, for <see cref="ImageTraceTargetKind.IntoLayout"/>.</param>
public sealed record ImageTraceRun(ImageTraceResult Result, string? LayoutPath, string? CellDir, ImageTraceEdit? Edit, string? Refusal)
{
    public bool Ok => Refusal is null && (LayoutPath is not null || Edit is not null);
    public RecognitionReport Report => Result.Report;
    public bool CheckpointTaken { get; init; }
}

public static partial class ImageTrace
{
    /// <summary>The refusal for a write with no scale (D6), listing what evidence there was.</summary>
    public static string NoScaleRefusal(IReadOnlyList<ImageScaleCandidate> candidates)
    {
        var offered = candidates.Where(c => c.Kind is ImageScaleKind.Parts or ImageScaleKind.Resolution or ImageScaleKind.Placement).ToList();
        return "The picture states no scale, and none was inferred: pick two points and type the distance between them, " +
               "or state the scale." +
               (offered.Count == 0 ? "" : " Found: " + string.Join("; ", offered.Select(c => $"{c.Display} ({c.Evidence})")) + ".");
    }

    /// <summary>R-im3-1/R-im3-7 — trace, then write (or, into the open layout, return the edit). A refusal at any step
    /// writes nothing.</summary>
    public static ImageTraceRun Run(ImageTraceInput input, ImageTraceTarget target, ImageTraceRunOptions? options = null,
                                    RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ImageTraceRunOptions();
        bool into = target.Kind == ImageTraceTargetKind.IntoLayout;
        var placed = input.Source.Placement as LayoutBitmapPlacement;
        if (into && placed is null)
            return new ImageTraceRun(new ImageTraceResult { Report = new RecognitionReport() }, null, null, null,
                "Only a picture placed in a layout can be traced into it; choose a new cell.");

        var result = Trace(input with { TargetIsPlacedLayout = into }, control);
        ImageTraceRun Refused(string why) => new(result, null, null, null, why);
        if (!result.Ok) return Refused(result.Refusal!);
        if (result.Scale is null) return Refused(NoScaleRefusal(result.ScaleCandidates));

        if (into)
        {
            result.Report.Add(RecognitionFindingClass.ImageLayoutWritten, 1,
                $"{Plural(result.Shapes.Count, "shape goes", "shapes go")} into {Path.GetFileName(placed!.Document)}, over the picture, as one undo step.");
            return new ImageTraceRun(result, null, null, new ImageTraceEdit(placed!.Document, result.Shapes), null);
        }

        // ── where it goes ────────────────────────────────────────────────────────────────────────────
        string cellDir, cellName;
        string? existing = null;
        if (target.Kind == ImageTraceTargetKind.NewCell)
        {
            string name = (target.CellName ?? "").Trim();
            if (NameValidator.Validate(name) is { } bad) return Refused($"'{name}' cannot be a cell name: {bad}");
            string parent = Path.GetFullPath(target.ParentDir ?? ".");
            cellDir = Path.Combine(parent, name);
            if (Directory.Exists(cellDir) || File.Exists(cellDir))
                return Refused($"A cell named '{name}' already exists in {parent}; choose another name.");
            cellName = name;
        }
        else
        {
            cellDir = Path.GetFullPath(target.CellDir ?? "");
            cellName = Path.GetFileName(cellDir);
            existing = ImageTraceTarget.PrimaryLayout(cellDir);
            if (existing is null || !ImageTraceTarget.IsReplaceable(cellDir))
                return Refused($"{cellName}'s layout was not created from a picture; choose a new cell.");
        }

        // ── the write: folder, the kept picture, the layout ─────────────────────────────────────────
        string path;
        bool checkpoint = false;
        try
        {
            if (existing is not null)
            {
                checkpoint = true;
                var take = options.Checkpoint
                           ?? ((p, intent) => WorkspaceCheckpoints.BeforeWrite(p, intent, CheckpointOrigin.SavePoint)?.Render());
                if (take(existing, ImageTraceTarget.ReplaceIntent) is { } failed)
                    return Refused($"The history checkpoint a replace takes first could not be taken, so nothing was replaced: {failed}");
            }
            else CellFolder.CreateCellFolder(Path.GetDirectoryName(cellDir)!, cellName);

            string layoutDir = existing is not null ? Path.GetDirectoryName(existing)! : CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            var kept = ImageKeep.Into(cellDir, cellName, input.Source);
            var view = ViewOf(input, result, kept.RefFrom(layoutDir), layoutDir, options.Now ?? DateTime.UtcNow);
            if (existing is not null)
            {
                path = existing;
                LayoutPersistence.SaveToFile(path, view);
            }
            else path = CellCreate.WriteLayoutView(cellDir, cellName, view);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refused($"The layout could not be written: {ex.Message}");
        }

        result.Report.Add(RecognitionFindingClass.ImageLayoutWritten, 1,
            (checkpoint ? $"A history checkpoint (\"{ImageTraceTarget.ReplaceIntent}\") was taken, then " : "") +
            $"{cellName}'s layout was {(checkpoint ? "replaced" : "written")}: {path}.");
        return new ImageTraceRun(result, path, cellDir, null, null) { CheckpointTaken = checkpoint };
    }

    /// <summary>The layout view a run writes: the shapes, the underlay pointing at the kept copy, the technology
    /// reference and the provenance.</summary>
    private static LayoutView ViewOf(ImageTraceInput input, ImageTraceResult result, string pictureRef, string layoutDir, DateTime now)
    {
        var view = CellCreate.NewLayoutView(input.Technology);
        view.DbuPerMicron = result.DbuPerMicron;
        if (input.TechnologyPath is { } tech) view.TechRef = SchematicTechnology.StoredRef(Path.GetFullPath(tech), layoutDir);
        view.Shapes.AddRange(result.Shapes);
        if (result.Underlay is { } u)
        {
            u.ImagePathRef = pictureRef;
            view.Shapes.Insert(0, u);
        }

        var kind = input.Kind ?? ImageKind.Classify(input.Source.Raster);
        if (kind.Kind != DrawingKind.Layout) kind = kind.Force(DrawingKind.Layout);
        var provenance = ImageProvenance.For(input.Source, kind, pictureRef, now);
        provenance.Scale = result.Scale!.Display;
        provenance.ScaleEvidence = result.Scale.Evidence;
        provenance.LayerMap = result.LayerMap!.ToProvenance();
        provenance.Options = input.Options.ToProvenance();
        view.ImageSource = provenance;
        return view;
    }
}
