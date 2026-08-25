// DXF import orchestrator (docs/sonnet-briefs/brief-L4b-dxf-interchange.md §2). The ONLY piece of the
// DXF stack that touches CellFolder/layer reconciliation/Messages — DxfReader itself stays pure
// text-and-groups. Mirrors GdsiiImport.Import's overall shape: a DXF file becomes N proper circuitRF
// cells with real layout views, never an opaque blob — but with a genuinely different unit story
// (R-L4b-4: DXF's own $INSUNITS must be resolved or PROMPTED, never guessed, unlike GDSII's UNITS
// record which is always present and self-describing).

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfImport
{
    public sealed record ImportResult(
        bool Cancelled,
        IReadOnlyList<string> CreatedCellDirs,
        IReadOnlyDictionary<string, string> CellNameByBlockName,
        IReadOnlyList<LayerDef> LayersToAdd,
        IReadOnlyList<string> Messages);

    /// <summary>
    /// Imports every BLOCK plus the model-space (top-level ENTITIES) content in
    /// <paramref name="dxfStream"/> as real cell folders under <paramref name="parentDir"/>.
    /// <paramref name="resolveUnits"/> is invoked with the file's raw (0/unset/unsupported)
    /// <c>$INSUNITS</c> value ONLY when it cannot be trusted as-is (R-L4b-4) — return the chosen
    /// <c>$INSUNITS</c> value to proceed, or null to abort the whole import (nothing is created).
    /// When null (no interactive prompt available), defaults to millimeters
    /// (<see cref="DxfUnits.DefaultPromptUnits"/>) per the brief's own stated default.
    /// <paramref name="resolveLayerMapping"/> mirrors <c>GdsiiImport.Import</c>'s identical parameter.
    /// </summary>
    public static ImportResult Import(
        Stream dxfStream,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        Func<int, int?>? resolveUnits = null,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null)
    {
        if (LooksLikeBinaryDxf(dxfStream))
            return new ImportResult(true, [], new Dictionary<string, string>(), [],
                ["This is a binary DXF file — only ASCII DXF is supported. Re-save as ASCII and try again."]);

        // R-dxf-2 (brief-dxf-version-support.md §2): the encoding is genuinely version-dependent —
        // R2007 (AC1021)+ is UTF-8, R2006 and earlier use the drawing's own $DWGCODEPAGE — and was,
        // until this brief, an IMPLICIT plain StreamReader default (silently wrong in either direction).
        // Resolve() sniffs $ACADVER/$DWGCODEPAGE first (a real two-pass read the file's own always-ASCII
        // header variables make safe) and reports what it decided, exactly like the units resolution
        // below already does.
        var encoding = DxfEncoding.Resolve(dxfStream);
        using var textReader = new StreamReader(dxfStream, encoding.Encoding);
        var reader = DxfReader.Read(textReader);
        var messages = new List<string>(reader.Diagnostics) { encoding.Report };

        int rawInsUnits = reader.InsUnits;
        int insUnits;
        if (DxfUnits.NanometersPerDrawingUnit(rawInsUnits) is not null)
        {
            insUnits = rawInsUnits;
            messages.Add($"Units: {DescribeUnits(insUnits)} (from the file's own $INSUNITS).");
        }
        else
        {
            int? chosen = resolveUnits is not null ? resolveUnits(rawInsUnits) : DxfUnits.DefaultPromptUnits;
            if (chosen is null)
                return new ImportResult(true, [], new Dictionary<string, string>(), [], messages);
            insUnits = chosen.Value;
            string why = rawInsUnits == 0 ? "absent" : $"unrecognized ({rawInsUnits})";
            messages.Add($"$INSUNITS was {why} — imported as {DescribeUnits(insUnits)}. Report this if that assumption is wrong.");
        }

        double dbuPerDrawingUnit = (double)DxfUnits.DbuPerDrawingUnit(insUnits, destDbuPerMicron);
        double ratio = dbuPerDrawingUnit / DxfReader.ProvisionalDbuPerDrawingUnit;

        int roundedCount = CountRoundedCoordinates(reader.Structures, ratio);
        if (roundedCount > 0)
            messages.Add(
                $"Source resolution is finer than the destination's {destDbuPerMicron} DBU/µm — " +
                $"{roundedCount} coordinate(s) will round.");

        var rescaled = RescaleAll(reader.Structures, ratio);

        // R-L4a-2-style reuse — DXF's named layers feed the SAME LayoutLayerMapping.Propose unmodified.
        // R-col-3: the file's own parsed LAYER table (reader.LayerTable) rides along so BuildSourceLayers
        // can populate each source LayerDef's real colour/visibility instead of defaulting to black.
        var allNames = rescaled.SelectMany(s => s.Shapes.Select(sh => sh.LayerName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (sourceLayers, keyByName) = DxfLayerReconciliation.BuildSourceLayers(allNames, reader.LayerTable, destTech);
        foreach (var s in rescaled)
            foreach (var sh in s.Shapes)
                sh.Shape.Layer = keyByName[sh.LayerName];

        var allShapes = rescaled.SelectMany(s => s.Shapes.Select(sh => sh.Shape)).ToList();
        var rows = LayoutLayerMapping.Propose(allShapes, sourceLayers, destTech);

        // R-col-4: for a DXF import specifically — never for L1g's own cross-technology PASTE, whose
        // safe default correctly stays Keep-as-unknown — an unmatched (NoMatch) row's default action is
        // "Add to technology" instead. A DXF's own layer names and colours are the author's deliberate
        // intent, not incidental metadata circuitRF invented, so the common case (accept every proposed
        // row) becomes one click instead of requiring the user to notice and flip each unmatched row by
        // hand. This only changes which Choice a row STARTS with — LayoutLayerMapping.Propose itself,
        // the dialog, and ApplyReconciliation are all completely unmodified; a user can still override
        // any row (including choosing Keep as unknown) before accepting.
        rows = rows.Select(r => r.Match == LayerMatchKind.NoMatch
            ? r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.AddToTechnology) }
            : r).ToList();

        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (rows.Count > 0 && LayoutLayerMapping.RequiresConfirmation(rows) && resolveLayerMapping is not null)
        {
            choices = resolveLayerMapping(rows);
            if (choices is null)
                return new ImportResult(true, [], new Dictionary<string, string>(), [], messages);
        }
        choices ??= LayoutLayerMapping.BuildChoices(rows);
        if (rows.Count > 0) messages.Add(LayoutLayerMapping.SummarizeMapping(rows, destTech));

        // §8-equivalent — block name <-> cell name mapping, reported both ways.
        var blockNames = rescaled.Select(s => s.Name).ToList();
        var cellNameByBlock = DxfNaming.NameCellsForImport(blockNames);
        foreach (var s in rescaled)
            if (cellNameByBlock[s.Name] != s.Name)
                messages.Add($"DXF block \"{s.Name}\" → cell \"{cellNameByBlock[s.Name]}\".");

        // Pass 1 — create every cell folder up front (forward references resolve regardless of order).
        var cellDirByBlock = new Dictionary<string, string>();
        var createdDirs = new List<string>();
        foreach (var s in rescaled)
        {
            var cellDir = CellFolder.CreateCellFolder(parentDir, cellNameByBlock[s.Name]);
            cellDirByBlock[s.Name] = cellDir;
            createdDirs.Add(cellDir);
        }

        // Pass 2 — reconcile layers and write each structure's LayoutView. No import-time cycle
        // pre-check (mirrors GdsiiImport's own documented choice) — the existing CellHierarchy.
        // ResolveForWalk visiting-set + MaxDepth backstop already protects every consumer once opened.
        var layersToAdd = new List<LayerDef>();
        var addedKeys = new HashSet<LayerKey>();
        foreach (var s in rescaled)
        {
            var shapes = s.Shapes.Select(sh => sh.Shape).ToList();
            var reconciled = LayoutFragment.ApplyReconciliation(shapes, sourceLayers, choices);
            foreach (var def in reconciled.LayersToAdd)
                if (addedKeys.Add(def.Key)) layersToAdd.Add(def);

            var cellDir = cellDirByBlock[s.Name];
            var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            var cellName = cellNameByBlock[s.Name];

            var view = new LayoutView { DbuPerMicron = destDbuPerMicron };
            view.Shapes.AddRange(reconciled.Shapes);

            foreach (var inst in s.Instances)
            {
                string cellRef = cellDirByBlock.TryGetValue(inst.CellRef, out var targetDir)
                    ? Path.GetRelativePath(layoutDir, targetDir)
                    : inst.CellRef; // referenced block absent from the file — left as declared
                view.Instances.Add(new LayoutInstance
                {
                    CellRef = cellRef,
                    X = inst.X, Y = inst.Y, RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = inst.Mag,
                    Rows = inst.Rows, Cols = inst.Cols, PitchX = inst.PitchX, PitchY = inst.PitchY,
                });
            }

            string layoutFileName = cellName + ".clay";
            LayoutPersistence.SaveToFile(Path.Combine(layoutDir, layoutFileName), view);

            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.PrimaryLayout = layoutFileName;
            CellPersistence.SaveToFile(ccellPath, ccell);
        }

        // §2's own framing: report EVERYTHING unsupported, by type with counts.
        foreach (var (type, count) in reader.UnsupportedEntityCounts.OrderByDescending(kv => kv.Value))
            messages.Add($"{count} unsupported {type} entit{(count == 1 ? "y" : "ies")} skipped — not imported.");

        return new ImportResult(false, createdDirs, cellNameByBlock, layersToAdd, messages);
    }

    /// <summary>The official 22-byte "AutoCAD Binary DXF" sentinel (§2's own out-of-scope statement:
    /// "support ASCII; report and refuse binary clearly"). Requires a seekable stream to peek without
    /// consuming; a non-seekable stream is trusted to be ASCII (the common case for a freshly-opened
    /// file) rather than buffering the whole thing just to check.</summary>
    private static bool LooksLikeBinaryDxf(Stream stream)
    {
        if (!stream.CanSeek) return false;
        byte[] sentinel = "AutoCAD Binary DXF\r\n"u8.ToArray();
        if (stream.Length < sentinel.Length) return false;

        long start = stream.Position;
        var buffer = new byte[sentinel.Length];
        int read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = start;
        return read == sentinel.Length && buffer.AsSpan().SequenceEqual(sentinel);
    }

    private static string DescribeUnits(int insUnits) => insUnits switch
    {
        DxfUnits.Inches => "inches",
        DxfUnits.Feet => "feet",
        DxfUnits.Millimeters => "millimeters",
        DxfUnits.Centimeters => "centimeters",
        DxfUnits.Meters => "meters",
        DxfUnits.Microns => "microns",
        _ => $"$INSUNITS={insUnits}",
    };

    private static int CountRoundedCoordinates(IReadOnlyList<DxfStructure> structures, double ratio)
    {
        int count = 0;
        foreach (var s in structures)
        {
            foreach (var sh in s.Shapes)
                foreach (var v in AllCoordinatesOf(sh.Shape))
                    if (WouldRound(v, ratio)) count++;
            foreach (var inst in s.Instances)
            {
                if (WouldRound(inst.X, ratio)) count++;
                if (WouldRound(inst.Y, ratio)) count++;
            }
        }
        return count;
    }

    private static bool WouldRound(long v, double ratio)
    {
        double scaled = v * ratio;
        return Math.Abs(scaled - Math.Round(scaled)) > 1e-6;
    }

    private static IEnumerable<long> AllCoordinatesOf(LayoutShape shape)
    {
        switch (shape)
        {
            case RectShape r: yield return r.X1; yield return r.Y1; yield return r.X2; yield return r.Y2; break;
            case PolygonShape p: foreach (var v in p.Xy) yield return v; break;
            case RoundedRectShape rr: yield return rr.X1; yield return rr.Y1; yield return rr.X2; yield return rr.Y2; break;
            case CircleShape c: yield return c.Cx; yield return c.Cy; yield return c.R; break;
            case CurveShape curve: foreach (var v in curve.Xy) yield return v; break;
            case PathShape path: foreach (var v in path.Xy) yield return v; break;
            case LabelShape l: yield return l.X; yield return l.Y; break;
        }
    }

    private static List<DxfStructure> RescaleAll(IReadOnlyList<DxfStructure> structures, double ratio)
    {
        var result = new List<DxfStructure>(structures.Count);
        foreach (var s in structures)
        {
            var rescaled = new DxfStructure { Name = s.Name };
            foreach (var sh in s.Shapes)
                rescaled.Shapes.Add(new DxfImportedShape(RescaleShape(sh.Shape, ratio), sh.LayerName));
            foreach (var inst in s.Instances)
                rescaled.Instances.Add(RescaleInstance(inst, ratio));
            result.Add(rescaled);
        }
        return result;
    }

    private static long Scale(long v, double ratio) => (long)Math.Round(v * ratio, MidpointRounding.AwayFromZero);

    private static LayoutShape RescaleShape(LayoutShape shape, double ratio)
    {
        var clone = LayoutGeometry.Clone(shape);
        LayoutCoordinateWalk.Transform(clone, LayoutCoordinateTransform.Uniform(v => Scale(v, ratio)));
        return clone;
    }

    private static LayoutInstance RescaleInstance(LayoutInstance inst, double ratio) => new()
    {
        CellRef = inst.CellRef,
        X = Scale(inst.X, ratio), Y = Scale(inst.Y, ratio),
        RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = inst.Mag,
        Rows = inst.Rows, Cols = inst.Cols,
        PitchX = Scale(inst.PitchX, ratio), PitchY = Scale(inst.PitchY, ratio),
    };
}
