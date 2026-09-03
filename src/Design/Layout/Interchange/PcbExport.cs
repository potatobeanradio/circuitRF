// Board-format export orchestrator — the only piece of the board WRITE stack that touches
// CellFolder/Technology/hierarchy, exactly as PcbImport is for the read stack and GdsiiExport/DxfExport
// are for theirs. PcbWriter itself sees a finished model and emits tokens.
//
// Analyze() then Write(), the same two-step every other export here uses, and for the same reason: the
// preview a dialog shows must BE the write, discarded, so it can never disagree with what lands on disk.


using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbExport
{
    /// <summary>The same class of ceiling the import path applies (R-L4d-20) — a write that dies
    /// partway leaves a half-written board file that opens and is wrong.</summary>
    public const long ShapeHardCeiling = LayoutFlatten.FlattenAllLevelsHardCeiling;

    public sealed record ExportPlan(
        PcbExportModel Model,
        PcbWriteSummary Summary,
        string? Refusal,
        /// <summary>Instances whose cell declares no <see cref="LayoutPin"/>s. A footprint with no pads
        /// is a footprint nothing can route to, so those are FLATTENED into board geometry instead —
        /// the artwork survives, the component does not, and this is the count that says so.</summary>
        int CellsFlattenedForLackOfPins,
        int UnresolvedInstanceReferences)
    {
        public bool CanWrite => Refusal is null;

        /// <summary>True when the write carries everything at full fidelity and there is nothing a
        /// dialog would usefully say.</summary>
        public bool HasNothingToReport =>
            CanWrite && CellsFlattenedForLackOfPins == 0 && UnresolvedInstanceReferences == 0 &&
            Summary.CubicsFlattened == 0 && Summary.BitmapsSkipped == 0 &&
            Summary.PinsWithNoArtwork == 0 && Summary.UnmappedLayerNames.Count == 0 &&
            Summary.Notes.Count == 0;
    }

    /// <summary>
    /// Builds the model and runs the real write into <see cref="TextWriter.Null"/> to produce the
    /// plan. <paramref name="rootView"/> is the live, possibly-unsaved view when the root cell is open
    /// in the editor (the same convention <c>GdsiiExport.Analyze</c> takes, and for the same reason).
    /// </summary>
    public static ExportPlan Analyze(string rootCellDir, Technology? tech, int dbuPerMicron, LayoutView? rootView = null)
    {
        var model = new PcbExportModel
        {
            Tech = tech,
            DbuPerMicron = dbuPerMicron,
            BoardTitle = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootCellDir)),
        };

        var view = rootView ?? LoadPrimaryLayout(rootCellDir);
        var layoutDir = CellFolder.SubFolderPath(rootCellDir, ViewType.Layout);

        model.BoardShapes.AddRange(view.Shapes);

        int flattenedCells = 0, unresolved = 0;
        long shapeCount = view.Shapes.Count;

        foreach (var inst in view.Instances)
        {
            if (Workspace.ExternalCellRef.ResolveCellDir(inst.CellRef, layoutDir) is not { } targetDir)
            { unresolved++; continue; }
            if (!Directory.Exists(targetDir)) { unresolved++; continue; }

            LayoutView cellView;
            try { cellView = LoadPrimaryLayout(targetDir); }
            catch { unresolved++; continue; }

            if (cellView.Pins.Count == 0)
            {
                // No pins means no pads. Flatten the artwork onto the board rather than writing a
                // component with nothing to connect to — every board tool would show it as an
                // unroutable graphic anyway, and this way it at least lands on the right layers.
                var flattened = LayoutFlatten.FlattenAllLevels(inst, layoutDir);
                model.BoardShapes.AddRange(flattened.Shapes);
                shapeCount += flattened.Shapes.Count;
                flattenedCells++;
                continue;
            }

            string defName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetDir));
            if (!model.Definitions.ContainsKey(defName))
            {
                var shapes = new List<LayoutShape>(cellView.Shapes);
                // A cell that itself places sub-cells: this format's footprints do not nest, so the
                // sub-levels are flattened INTO the definition. Reported through the same shape count.
                var cellLayoutDir = CellFolder.SubFolderPath(targetDir, ViewType.Layout);
                foreach (var sub in cellView.Instances)
                    shapes.AddRange(LayoutFlatten.FlattenAllLevels(sub, cellLayoutDir).Shapes);

                model.Definitions[defName] = new PcbFootprintDef(defName, shapes, cellView.Pins);
                shapeCount += shapes.Count;
            }

            // An array instance is N placements — this format has no array primitive.
            for (int row = 0; row < Math.Max(1, inst.Rows); row++)
                for (int col = 0; col < Math.Max(1, inst.Cols); col++)
                {
                    model.Placements.Add(new PcbFootprintPlacement(
                        defName,
                        inst.X + col * inst.PitchX,
                        inst.Y + row * inst.PitchY,
                        inst.RotationDegrees,
                        inst.MirrorX,
                        // No reference designator: circuitRF's layout model has no such field, and
                        // inventing R1/C2 names would put fabrication-facing identifiers in a file the
                        // user did not author. The receiving tool shows an unnamed footprint, which is
                        // recoverable; a wrong designator silently is not.
                        Reference: null));
                    shapeCount += model.Definitions[defName].Shapes.Count;
                }
        }

        if (shapeCount > ShapeHardCeiling)
            return new ExportPlan(model, Empty(), 
                $"This layout would write about {shapeCount:N0} shapes, above the {ShapeHardCeiling:N0} " +
                "export ceiling — nothing was written. Export a cropped region instead.",
                flattenedCells, unresolved);

        var summary = PcbWriter.Write(TextWriter.Null, model);
        return new ExportPlan(model, summary, null, flattenedCells, unresolved);
    }

    /// <summary>Writes a previously-analyzed plan. Never writes a partial file — a refused plan throws
    /// rather than producing a board file that opens and is wrong.</summary>
    public static PcbWriteSummary Write(string filePath, ExportPlan plan)
    {
        if (!plan.CanWrite) throw new InvalidOperationException(plan.Refusal);
        using var writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8);
        return PcbWriter.Write(writer, plan.Model);
    }

    /// <summary>The human-readable report — what came out, and everything the format could not carry.</summary>
    public static IReadOnlyList<string> Describe(ExportPlan plan)
    {
        var messages = new List<string>();
        if (plan.Refusal is { } refusal) return [refusal];

        var s = plan.Summary;
        messages.Add(
            $"Wrote {s.Footprints:N0} footprint(s), {s.Segments:N0} track segment(s), {s.Arcs:N0} track arc(s), " +
            $"{s.Vias:N0} via(s), {s.Zones:N0} copper zone(s), {s.Graphics:N0} graphic(s) and {s.Texts:N0} text item(s), " +
            $"at format epoch {PcbLayerNaming.TargetVersion}.");

        if (plan.CellsFlattenedForLackOfPins > 0)
            messages.Add(
                $"{plan.CellsFlattenedForLackOfPins:N0} placed cell(s) declare no pins, so their artwork was " +
                "flattened onto the board rather than written as components — a footprint with no pads is one " +
                "nothing can route to. Give those cells pins (import or a PCell does this) to export them as parts.");

        if (plan.UnresolvedInstanceReferences > 0)
            messages.Add($"{plan.UnresolvedInstanceReferences:N0} instance(s) reference a cell that could not be resolved — not written.");

        if (s.PinsWithNoArtwork > 0)
            messages.Add(
                $"{s.PinsWithNoArtwork:N0} pin(s) sit on no copper shape, so their pads were written at the pin's " +
                "own stated width. Check those pads before fabricating.");

        if (s.CubicsFlattened > 0)
            messages.Add($"{s.CubicsFlattened:N0} cubic curve(s) were flattened to line segments — this format carries no cubic on a track.");

        if (s.HolesKeyholed > 0)
            messages.Add($"{s.HolesKeyholed:N0} hole(s) were written as zero-width slits, which is this format's own representation of a hole.");

        if (s.UnnamedDrills > 0)
            messages.Add(
                $"{s.UnnamedDrills:N0} drilled hole(s) inside a placed cell belong to no pin, so they were written " +
                "as unnamed through-hole pads — mounting and thermal holes survive, but with no pad number.");

        if (s.BitmapsSkipped > 0)
            messages.Add($"{s.BitmapsSkipped:N0} bitmap(s) skipped — this format carries no raster artwork.");

        if (s.UnmappedLayerNames.Count > 0)
            messages.Add(
                $"{s.UnmappedLayerNames.Count:N0} layer(s) had nothing saying where they belong and were written to " +
                $"{PcbLayerNaming.FallbackName}: {string.Join(", ", s.UnmappedLayerNames)}. Set each one's board " +
                "layer name in the technology editor to place it properly.");

        messages.AddRange(s.Notes);
        return messages;
    }

    private static PcbWriteSummary Empty() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], []);

    private static LayoutView LoadPrimaryLayout(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null)
            return new LayoutView();
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, primary.ResolvedName));
    }
}
