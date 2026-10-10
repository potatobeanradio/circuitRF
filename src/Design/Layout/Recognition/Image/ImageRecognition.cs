// A layout picture made into a schematic — brief-img-4-layout-image-to-schematic.md R-im4-1 … R-im4-6 (D3, D5, D8, D14).
//
//   picture ─ IM-3 ImageTrace.Trace ─ RecognitionInput.FromTrace ─ ArtworkRecognition.Circuit (AS-3 … AS-6, unchanged)
//           ─ NetlistSchematic.Build with the artwork hints ─ one cell holding BOTH views, each with the picture under it
//
// This is glue, and the measure of it is how little of the artwork pipeline it touches: the recognition is told nothing
// about pictures. What the picture lacks arrives through seams the recognition already has — an undrawn reference plane
// through the technology (PictureTechnology), the silkscreen through IPartEvidenceSource (RasterSilkscreenEvidence).
//
// The picture is traced ONCE: the layout written and the schematic recognised are the same trace. Circuit is the
// no-write form (the CLI's read-only default, the dialog's preview); Run writes, and a refusal at any step before the
// write writes nothing. A re-run over a cell this command made replaces both views under AS D4's rule, after ONE
// history checkpoint covering both.
//
// The schematic's underlay is placed by the drawing's own artwork hints: the similarity (one scale, the y axis
// flipped, no rotation — the picture's own aspect) that best carries each component's copper onto where the drawing put
// it, so each part sits as near its copper as a drawing arranged by NetlistSchematic can.

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>What a picture-to-schematic run reads.</summary>
public sealed record ImageRecognitionInput
{
    /// <summary>The picture, the technology, the scale, the layer map and the trace options (IM-3).</summary>
    public required ImageTraceInput Trace { get; init; }

    /// <summary>The recognition's options (AS-3).</summary>
    public RecognitionOptions Options { get; init; } = new();

    /// <summary>A placement file, a bill of materials or an edited parts table, when the user gives one.</summary>
    public PlacementTable? Placement { get; init; }
    public BomTable? Bom { get; init; }
    public string? PartsCsvPath { get; init; }
    public string? PartsCsvText { get; init; }
}

public enum ImageRecognitionTargetKind { NewCell, Replace }

/// <summary>Where the two views go (D14).</summary>
public sealed record ImageRecognitionTarget(ImageRecognitionTargetKind Kind, string? ParentDir = null, string? CellName = null,
                                            string? CellDir = null)
{
    /// <summary>The checkpoint's intent before a replace.</summary>
    public const string ReplaceIntent = "Create Schematic from Image";

    public static ImageRecognitionTarget NewCell(string parentDir, string name) => new(ImageRecognitionTargetKind.NewCell, parentDir, name);

    public static ImageRecognitionTarget Replace(string cellDir) => new(ImageRecognitionTargetKind.Replace, CellDir: cellDir);

    /// <summary>Whether <paramref name="cellDir"/> is this command's to replace: its layout carries an <c>ImageSource</c>
    /// block, and its schematic — when it has one — carries one too.</summary>
    public static bool IsReplaceable(string cellDir)
    {
        if (!ImageTraceTarget.IsReplaceable(cellDir)) return false;
        if (RecognitionTarget.PrimarySchematic(cellDir) is not { } csch) return true;
        try { return SchematicPersistence.LoadFromFile(csch).model is { ImageSource: not null, ArtworkSource: not null }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

public sealed record ImageRecognitionRunOptions
{
    public RecognitionEmitOptions Emit { get; init; } = new();

    /// <summary>The ONE checkpoint a replace takes first, covering both views: a path and the intent in, an error out.
    /// Null is the real one — <see cref="WorkspaceCheckpoints.BeforeWrite"/>.</summary>
    public Func<string, string, string?>? Checkpoint { get; init; }

    /// <summary>The time the provenance blocks record; null is now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>What a reading produced, written nowhere.</summary>
/// <param name="Trace">The trace.</param>
/// <param name="Input">The recognition's input, or null when the trace was refused before it.</param>
/// <param name="Recognition">The recognition, or null.</param>
/// <param name="Circuit">The circuit, or null exactly when <paramref name="Refusal"/> is set.</param>
/// <param name="Report">The trace's findings followed by the recognition's (R-im4-6).</param>
public sealed record ImageRecognitionCircuit(ImageTraceResult Trace, RecognitionInput? Input, RecognitionResult? Recognition,
                                             RecognitionCircuit? Circuit, RecognitionReport Report, string? Refusal)
{
    public bool Ok => Refusal is null && Circuit is not null;
}

/// <summary>What one run did.</summary>
public sealed record ImageRecognitionRun(ImageRecognitionCircuit Reading, SchematicEditModel? Schematic, string? LayoutPath,
                                         string? SchematicPath, string? CellDir, string? Refusal)
{
    public bool Ok => Refusal is null && SchematicPath is not null;
    public RecognitionReport Report => Reading.Report;
    public bool CheckpointTaken { get; init; }
}

public static class ImageRecognition
{
    /// <summary>Trace, recognise and emit, and write nothing (R-im4-2).</summary>
    public static ImageRecognitionCircuit Circuit(ImageRecognitionInput input, RecognitionEmitOptions? emit = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Read(input, null, emit ?? new RecognitionEmitOptions(), control);
    }

    /// <summary>R-im4-2 — trace → recognise → emit → write: a cell holding the traced layout and the recognised schematic,
    /// each with the picture under it.</summary>
    public static ImageRecognitionRun Run(ImageRecognitionInput input, ImageRecognitionTarget target,
                                          ImageRecognitionRunOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ImageRecognitionRunOptions();
        var empty = new ImageRecognitionCircuit(new ImageTraceResult { Report = new RecognitionReport() }, null, null, null,
                                                new RecognitionReport(), null);
        ImageRecognitionRun Before(string why) => new(empty, null, null, null, null, why);

        // ── where it goes ────────────────────────────────────────────────────────────────────────────
        string cellDir, cellName;
        string? existingClay = null, existingCsch = null;
        if (target.Kind == ImageRecognitionTargetKind.NewCell)
        {
            cellName = (target.CellName ?? "").Trim();
            if (NameValidator.Validate(cellName) is { } bad) return Before($"'{cellName}' cannot be a cell name: {bad}");
            string parent = Path.GetFullPath(target.ParentDir ?? ".");
            cellDir = Path.Combine(parent, cellName);
            if (Directory.Exists(cellDir) || File.Exists(cellDir))
                return Before($"A cell named '{cellName}' already exists in {parent}; choose another name.");
        }
        else
        {
            cellDir = Path.GetFullPath(target.CellDir ?? "");
            cellName = Path.GetFileName(cellDir);
            if (!ImageRecognitionTarget.IsReplaceable(cellDir))
                return Before($"{cellName} was not created from a picture; choose a new cell.");
            existingClay = ImageTraceTarget.PrimaryLayout(cellDir);
            existingCsch = RecognitionTarget.PrimarySchematic(cellDir);
        }
        string clayPath = existingClay
                          ?? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), cellName + CellFolder.ViewExtension(ViewType.Layout));
        string schematicDir = existingCsch is not null ? Path.GetDirectoryName(existingCsch)! : CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

        // ── the reading, and its drawing ─────────────────────────────────────────────────────────────
        var reading = Read(input, clayPath, options.Emit, control);
        ImageRecognitionRun Refused(string why) => new(reading, null, null, null, null, why);
        if (!reading.Ok) return Refused(reading.Refusal ?? "The picture could not be read.");
        var circuit = reading.Circuit!;
        var report = reading.Report;
        var now = options.Now ?? DateTime.UtcNow;

        var drawn = NetlistSchematic.Build(new Library("netlist"), circuit.TestBench, schematicDir, circuit.Hints,
                                           artworkSides: options.Emit.ArtworkSides);
        if (drawn.Schematic is not { } model)
            return Refused("The recognised circuit cannot be drawn: " + string.Join(" ", drawn.Refusals));
        report.Add(RecognitionFindingClass.DrawingNotes, drawn.Notes.Count, string.Join(" ", drawn.Notes));
        RecognitionProvenance.Apply(model, circuit);
        model.TechRef = input.Trace.TechnologyPath is { } tech ? SchematicTechnology.StoredRef(Path.GetFullPath(tech), schematicDir) : null;
        model.ArtworkSource = RecognitionProvenance.For(reading.Input!, schematicDir, now, options.Emit);

        // ── the write: one checkpoint, the layout, then the schematic over the same kept picture ──────
        bool checkpoint = existingClay is not null;
        string layoutPath, schematicPath;
        try
        {
            if (checkpoint)
            {
                var take = options.Checkpoint
                           ?? ((p, intent) => WorkspaceCheckpoints.BeforeWrite(p, intent, CheckpointOrigin.SavePoint)?.Render());
                if (take(existingClay!, ImageRecognitionTarget.ReplaceIntent) is { } failed)
                    return Refused($"The history checkpoint a replace takes first could not be taken, so nothing was replaced: {failed}");
            }
            var layout = ImageTrace.Write(input.Trace, reading.Trace,
                checkpoint ? ImageTraceTarget.Replace(cellDir) : ImageTraceTarget.NewCell(Path.GetDirectoryName(cellDir)!, cellName),
                new ImageTraceRunOptions { Checkpoint = (_, _) => null, Now = now });
            if (!layout.Ok) return Refused(layout.Refusal ?? "The layout could not be written.");
            layoutPath = layout.LayoutPath!;

            string pictureRef = ImageKeep.Into(cellDir, cellName, input.Trace.Source).RefFrom(schematicDir);
            var raster = input.Trace.Source.Raster;
            if (input.Trace.Options.KeepUnderlay && Underlay(model, circuit, reading.Trace, raster.Width, raster.Height, pictureRef) is { } underlay)
                model.CanvasObjects.Add(underlay);
            model.ImageSource = ImageTrace.ProvenanceOf(input.Trace, reading.Trace, pictureRef, now);

            if (existingCsch is not null)
            {
                schematicPath = existingCsch;
                SchematicPersistence.SaveToFile(schematicPath, model, cellName);
            }
            else schematicPath = CellCreate.WriteSchematicView(cellDir, cellName, cellName, model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Refused($"The cell could not be written: {ex.Message}");
        }

        string verb = existingCsch is not null ? "replaced" : "written";
        report.Add(RecognitionFindingClass.ImageLayoutWritten, 1,
            (checkpoint ? $"A history checkpoint (\"{ImageRecognitionTarget.ReplaceIntent}\") was taken, then " : "") +
            $"{cellName}'s layout was {(checkpoint ? "replaced" : "written")}: {layoutPath}.");
        report.Add(RecognitionFindingClass.SchematicWritten, 1, $"{cellName}'s schematic was {verb}: {schematicPath}.");
        return new ImageRecognitionRun(reading, model, layoutPath, schematicPath, cellDir, null) { CheckpointTaken = checkpoint };
    }

    // ── the reading ─────────────────────────────────────────────────────────────────────────────────

    private static ImageRecognitionCircuit Read(ImageRecognitionInput input, string? clayPath, RecognitionEmitOptions emit, RunControl? control)
    {
        var trace = ImageTrace.Trace(input.Trace with { TargetIsPlacedLayout = false }, control);
        var technology = input.Trace.Technology;
        var recognition = RecognitionInput.FromTrace(trace, technology, input.Options) with
        {
            ClayPath = clayPath, TechnologyPath = input.Trace.TechnologyPath, Placement = input.Placement, Bom = input.Bom,
            PartsCsvPath = input.PartsCsvPath, PartsCsvText = input.PartsCsvText,
        };

        var report = new RecognitionReport();
        foreach (var f in trace.Report.Findings) report.Add(f.Class, f.Count, f.Sentence, f.Anchors);
        if (recognition.Refusal is not null || technology is null)
            return new ImageRecognitionCircuit(trace, null, null, null, report, recognition.Refusal ?? ImageTrace.NoTechnologyRefusal);

        // What the picture did not say, said once, before the recognition's own lines.
        var fmt = RailLengthFormat.For(recognition.View);
        var implied = technology.Stackup.Layers.Zip(recognition.Technology!.Stackup.Layers)
                                .Where(p => p.First.DrawingLayers.Count > 0 && p.Second.DrawingLayers.Count == 0)
                                .Select(p => $"'{p.First.Name}'").Distinct().ToList();
        if (implied.Count > 0)
            report.Add(RecognitionFindingClass.ImageReferenceImplied, 1,
                $"Nothing in the picture is on {string.Join(" or ", implied)}, the stackup's reference plane, so the plane was " +
                "implied: every via carried down to it reaches ground.");
        if (!trace.Layers.Any(l => l.Role == ImageLayerRole.BoardOutline && l.Shapes.Count > 0))
            report.Add(RecognitionFindingClass.ImageFrameIsBoardEdge, 1,
                "The picture draws no board outline, so the edge of what was traced was read as the board's: a line that " +
                "runs off it is an edge launch.");
        foreach (var silk in recognition.EvidenceSources.OfType<RasterSilkscreenEvidence>()) silk.Report(report);

        // The recognition's own lines, two of them reworded where what they say of the technology is the picture's
        // doing: the ground point the user did not click, and the mask the picture did not draw.
        var (result, circuit) = ArtworkRecognition.Circuit(recognition, emit, control);
        bool pour = recognition.Options.GroundAt is { } at && input.Options.GroundAt != at;
        bool noMask = technology.Layers.Any(l => recognition.Technology.Layers.All(c => c.Key != l.Key));
        foreach (var f in result.Report.Findings)
            report.Add(f.Class, f.Count, f.Class switch
            {
                RecognitionFindingClass.GroundChosen when pour =>
                    $"Ground is the largest top-side pour, at {fmt.Point(recognition.Options.GroundAt!.Value.X, recognition.Options.GroundAt.Value.Y)}, " +
                    "with the implied reference plane under it.",
                RecognitionFindingClass.MaskPasteAbsent when noMask =>
                    "The picture draws no solder mask or paste, so land patterns were read from the copper itself: pad-shaped " +
                    "copper and pads at line ends. A pad joined to a pour is not seen this way.",
                _ => f.Sentence,
            }, f.Anchors);
        return new ImageRecognitionCircuit(trace, recognition, result, circuit, report,
                                           circuit is null ? result.Refusal ?? "The traced layout could not be recognised." : null);
    }

    // ── the schematic's underlay (D5) ───────────────────────────────────────────────────────────────

    /// <summary>The picture as a locked 35 % bitmap behind the drawing, placed by the similarity that best carries each
    /// component's artwork hint onto where the drawing put it; without two usable hints, spanning the drawing's width.</summary>
    internal static EditableBitmap? Underlay(SchematicEditModel model, RecognitionCircuit circuit, ImageTraceResult trace,
                                             int widthPx, int heightPx, string pictureRef)
    {
        if (trace.Frame is not { } frame) return null;
        var (x0, yTop) = frame.ToTarget(0, 0);
        var (x1, yBottom) = frame.ToTarget(widthPx, heightPx);

        var hints = circuit.Hints;
        var pairs = model.Components.Where(c => hints.ContainsKey(c.InstanceName))
                         .Select(c => (x: (double)hints[c.InstanceName].X, y: (double)hints[c.InstanceName].Y, X: c.X, Y: c.Y)).ToList();
        double k = 0, bx = 0, by = 0;
        if (pairs.Count >= 2)
        {
            double mx = pairs.Average(p => p.x), my = pairs.Average(p => p.y), mX = pairs.Average(p => p.X), mY = pairs.Average(p => p.Y);
            double num = 0, den = 0;
            foreach (var p in pairs)
            {
                num += (p.x - mx) * (p.X - mX) - (p.y - my) * (p.Y - mY);
                den += (p.x - mx) * (p.x - mx) + (p.y - my) * (p.y - my);
            }
            k = den > 0 ? num / den : 0;
            (bx, by) = (mX - k * mx, mY + k * my);
        }
        if (!(k > 0) || !double.IsFinite(k))
        {
            var comps = model.Components;
            double minX = comps.Count > 0 ? comps.Min(c => c.X) : 0, maxX = comps.Count > 0 ? comps.Max(c => c.X) : 0;
            double minY = comps.Count > 0 ? comps.Min(c => c.Y) : 0, maxY = comps.Count > 0 ? comps.Max(c => c.Y) : 0;
            k = (maxX > minX ? maxX - minX : 1000) / Math.Max(1, x1 - x0);
            (bx, by) = (0.5 * (minX + maxX) - k * 0.5 * (x0 + x1), 0.5 * (minY + maxY) + k * 0.5 * (yTop + yBottom));
        }

        double left = k * x0 + bx, top = -k * yTop + by, width = k * (x1 - x0), height = k * (yTop - yBottom);
        return new EditableBitmap
        {
            ImagePath = pictureRef, X = left + width / 2, Y = top + height / 2, Width = width, Height = height,
            Transparency = 1 - ImageTrace.UnderlayOpacity, IsLocked = true,
            ZOrder = model.CanvasObjects.Count > 0 ? model.CanvasObjects.Min(o => o.ZOrder) - 1 : 0,
        };
    }
}
