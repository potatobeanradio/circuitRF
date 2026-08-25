// GDSII import orchestrator (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2.4). The ONLY piece
// of the GDSII stack that touches CellFolder/layer reconciliation/Messages — GdsiiReader itself stays
// pure bytes-and-records (R15). A GDSII library becomes N proper circuitRF cells with real layout
// views, never an opaque blob.

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiImport
{
    public sealed record ImportResult(
        bool Cancelled,
        IReadOnlyList<string> CreatedCellDirs,
        IReadOnlyDictionary<string, string> CellNameByStructureName,
        IReadOnlyList<LayerDef> LayersToAdd,
        IReadOnlyList<string> Messages,
        /// <summary>brief-layout-testing-fixes.md item 7/R-fix-6: absolute cell-folder paths (a
        /// subset of <see cref="CreatedCellDirs"/>) for every structure NEVER referenced as another
        /// structure's instance <c>CellRef</c> within this same file — the GDSII notion of "top"
        /// (what a fab or a viewer like KLayout opens by default). Ordinarily exactly one; empty only
        /// for a pathological all-structures-mutually-referenced library, where there is genuinely no
        /// well-defined top and the caller should say so rather than guessing.</summary>
        IReadOnlyList<string> TopLevelCellDirs);

    /// <summary>
    /// Imports every structure in <paramref name="gdsiiStream"/> as a real cell folder under
    /// <paramref name="parentDir"/>. <paramref name="resolveLayerMapping"/> is invoked with the
    /// proposed <see cref="LayerMappingRow"/>s only when <see
    /// cref="LayoutLayerMapping.RequiresConfirmation"/> is true (exactly the existing retarget/paste
    /// gating) — return null to abort the whole import (nothing is created), or a settled choices
    /// dictionary to proceed. When null (no interactive dialog available, e.g. non-interactive
    /// contexts), <see cref="LayoutLayerMapping.BuildChoices"/>'s own defaults apply.
    /// </summary>
    public static ImportResult Import(
        Stream gdsiiStream,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        bool preferSourceResolution,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        PinInferenceRules? pinRules = null)
    {
        int pinsFound = 0, pinsNamed = 0;
        var pinNotes = new List<string>();

        var reader = GdsiiReader.Open(gdsiiStream);
        var rawStructures = reader.ReadStructures().ToList();
        var messages = new List<string>(reader.Diagnostics);

        // §2.2 — unit mismatch: warn + count when the source is finer than the destination's own
        // resolution; "refine" here means creating the new layouts at the SOURCE's own resolution
        // (always exact, by construction — mirrors L0a's LayoutScaling refinement being lossless)
        // rather than rounding down to destDbuPerMicron.
        double sourceDbuPerMicron = reader.Units.SourceDbuPerMicron;
        int targetDbuPerMicron = preferSourceResolution
            ? Math.Max(1, (int)Math.Round(sourceDbuPerMicron))
            : destDbuPerMicron;
        double ratio = targetDbuPerMicron / sourceDbuPerMicron;

        if (!preferSourceResolution && sourceDbuPerMicron > destDbuPerMicron)
        {
            int roundedCount = CountRoundedCoordinates(rawStructures, ratio);
            if (roundedCount > 0)
                messages.Add(
                    $"Source GDSII resolution ({sourceDbuPerMicron:0} DBU/µm) is finer than the " +
                    $"destination's {destDbuPerMicron} DBU/µm — {roundedCount} coordinate(s) will round. " +
                    "Import at the source's own resolution instead to avoid this.");
        }

        var scaled = ratio == 1.0 ? rawStructures : RescaleAll(rawStructures, ratio);

        // R-L4a-2 — one Propose() across every structure's shapes (a single GDSII library uses one
        // consistent layer vocabulary throughout); the .ctech mapping supplies proposals, the existing
        // dialog resolves what it cannot — never a second reconciliation algorithm.
        var allShapes = scaled.SelectMany(s => s.Shapes).ToList();
        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers(allShapes, destTech);
        var rows = LayoutLayerMapping.Propose(allShapes, sourceLayers, destTech);

        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (rows.Count > 0 && LayoutLayerMapping.RequiresConfirmation(rows) && resolveLayerMapping is not null)
        {
            choices = resolveLayerMapping(rows);
            if (choices is null)
                return new ImportResult(true, [], new Dictionary<string, string>(), [], messages, []);
        }
        choices ??= LayoutLayerMapping.BuildChoices(rows);
        messages.Add(LayoutLayerMapping.SummarizeMapping(rows, destTech));

        // §8 — structure name ↔ cell name mapping, reported so a fab's structure name can be traced
        // back to the user's cell.
        var structureNames = scaled.Select(s => s.Name).ToList();
        var cellNameByStructure = GdsiiStructureNaming.NameCellsForImport(structureNames);
        foreach (var s in scaled)
            if (cellNameByStructure[s.Name] != s.Name)
                messages.Add($"GDSII structure \"{s.Name}\" → cell \"{cellNameByStructure[s.Name]}\".");

        // Pass 1 — create every cell folder up front. All names are known before any instance is
        // wired, so a forward reference to a structure that appears later in the file needs no
        // special handling.
        var cellDirByStructure = new Dictionary<string, string>();
        var createdDirs = new List<string>();
        foreach (var s in scaled)
        {
            var cellDir = CellFolder.CreateCellFolder(parentDir, cellNameByStructure[s.Name]);
            cellDirByStructure[s.Name] = cellDir;
            createdDirs.Add(cellDir);
        }

        // Pass 2 — reconcile layers and write each structure's LayoutView, wiring every instance's
        // CellRef to its sibling cell folder exactly as declared (§2.4) — INCLUDING a self- or
        // mutually-referencing structure. No import-time cycle pre-check is added here: the existing
        // CellHierarchy.ResolveForWalk visiting-set + MaxDepth backstop (already exercised by every
        // other hierarchy consumer — render, hit-test, bbox) is what prevents a crash/overflow once
        // this imported design is opened, per the brief's own "route through the same check" instruction.
        var layersToAdd = new List<LayerDef>();
        var addedLayerKeys = new HashSet<LayerKey>();
        foreach (var s in scaled)
        {
            var reconciled = LayoutFragment.ApplyReconciliation(s.Shapes, sourceLayers, choices);
            foreach (var def in reconciled.LayersToAdd)
                if (addedLayerKeys.Add(def.Key))
                    layersToAdd.Add(def);

            var cellDir = cellDirByStructure[s.Name];
            var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            var cellName = cellNameByStructure[s.Name];

            var view = new LayoutView { DbuPerMicron = targetDbuPerMicron };
            view.Shapes.AddRange(reconciled.Shapes);

            // Imported artwork carries no pin list — GDSII has no such record. Recover one from the
            // drawing itself, against the DESTINATION technology (which is what says a purpose means
            // "pin"), so an imported device cell arrives connectable rather than as inert geometry.
            // Run on the RECONCILED shapes: layer mapping has already settled which destination layer
            // each one landed on, and that is what the purpose lookup reads.
            var inferred = PinInference.Infer(cellName, reconciled.Shapes, destTech, pinRules);
            foreach (var pin in inferred.Pins)
                view.Pins.Add(new LayoutPin
                {
                    Name       = pin.Name ?? "",
                    X          = pin.XDbu,
                    Y          = pin.YDbu,
                    WidthDbu   = pin.WidthDbu,
                    OutwardDeg = pin.OutwardDeg,
                    Layer      = pin.Layer,
                });

            pinsFound += inferred.Pins.Count;
            pinsNamed += inferred.Pins.Count(p => !string.IsNullOrEmpty(p.Name));
            foreach (var note in inferred.Notes) pinNotes.Add($"{cellName}: {note}");

            foreach (var inst in s.Instances)
            {
                string cellRef = cellDirByStructure.TryGetValue(inst.CellRef, out var targetDir)
                    ? Path.GetRelativePath(layoutDir, targetDir)
                    : inst.CellRef; // referenced structure absent from the file — left as declared,
                                    // resolves as a broken/missing instance (R-L3a-1's existing placeholder)
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

        // item 7/R-fix-6 — the GDSII notion of "top": a structure never named as any OTHER structure's
        // instance CellRef in this same file (referenced-by-name, before mangling to a cell name — the
        // exact vocabulary inst.CellRef already uses above).
        var referencedStructureNames = new HashSet<string>(
            scaled.SelectMany(s => s.Instances).Select(i => i.CellRef), StringComparer.Ordinal);
        var topLevelCellDirs = scaled
            .Where(s => !referencedStructureNames.Contains(s.Name))
            .Select(s => cellDirByStructure[s.Name])
            .ToList();

        // One aggregate line, not one per cell: a device library is dozens of cells, and a line each
        // would bury the totals that actually tell the user whether inference worked. The individual
        // notes follow, capped — an inconclusive pin is worth naming, but not at the cost of a wall.
        if (pinsFound > 0)
        {
            messages.Add($"Recovered {pinsFound} pin(s) across {createdDirs.Count} cell(s); " +
                         $"{pinsNamed} carried a terminal name.");
            const int MaxNotes = 20;
            messages.AddRange(pinNotes.Take(MaxNotes));
            if (pinNotes.Count > MaxNotes)
                messages.Add($"(+{pinNotes.Count - MaxNotes} more pin note(s).)");
        }

        return new ImportResult(false, createdDirs, cellNameByStructure, layersToAdd, messages, topLevelCellDirs);
    }

    private static int CountRoundedCoordinates(IReadOnlyList<InterchangeStructure> structures, double ratio)
    {
        int count = 0;
        foreach (var s in structures)
        {
            foreach (var shape in s.Shapes)
                foreach (var v in AllCoordinatesOf(shape))
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
            case CircleShape c: yield return c.Cx; yield return c.Cy; break;
            case PathShape path: foreach (var v in path.Xy) yield return v; break;
            case LabelShape l: yield return l.X; yield return l.Y; break;
        }
    }

    private static List<InterchangeStructure> RescaleAll(IReadOnlyList<InterchangeStructure> structures, double ratio)
    {
        var result = new List<InterchangeStructure>(structures.Count);
        foreach (var s in structures)
        {
            var shapes = s.Shapes.Select(sh => RescaleShape(sh, ratio)).ToList();
            var instances = s.Instances.Select(i => RescaleInstance(i, ratio)).ToList();
            result.Add(new InterchangeStructure(s.Name, shapes, instances));
        }
        return result;
    }

    private static long Scale(long v, double ratio) => (long)Math.Round(v * ratio, MidpointRounding.AwayFromZero);

    private static LayoutShape RescaleShape(LayoutShape shape, double ratio)
    {
        var clone = LayoutGeometry.Clone(shape);
        switch (clone)
        {
            case RectShape r:
                r.X1 = Scale(r.X1, ratio); r.Y1 = Scale(r.Y1, ratio);
                r.X2 = Scale(r.X2, ratio); r.Y2 = Scale(r.Y2, ratio);
                break;
            case PolygonShape p:
                p.Xy = p.Xy.Select(v => Scale(v, ratio)).ToArray();
                break;
            case CircleShape c:
                c.Cx = Scale(c.Cx, ratio); c.Cy = Scale(c.Cy, ratio); c.R = Scale(c.R, ratio);
                break;
            case PathShape path:
                path.Xy = path.Xy.Select(v => Scale(v, ratio)).ToArray();
                path.Width = Scale(path.Width, ratio);
                break;
            case LabelShape l:
                l.X = Scale(l.X, ratio); l.Y = Scale(l.Y, ratio); l.Height = Scale(l.Height, ratio);
                break;
        }
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
