// `circuitrf convert` — every interchange format to every other one, headlessly.
//
// WHY IT IS ONE VERB AND NOT FIVE. Every reader in src/Design/Layout/Interchange lands on the same
// neutral thing (a cell folder plus a technology) and every writer starts from it, so the N x N table
// of conversions is not N x N pieces of code: it is one import step, one export step, and a rule for
// what sits between them. DXF -> Gerber is GDSII -> board is `.clay` -> DXF; only the two ends differ.
//
// THE INTERMEDIATE IS A REAL CELL, NOT A BUFFER. An import writes cell folders — that is what the
// readers do, and re-implementing them against an in-memory model would be a second copy of the
// importer with its own bugs. So a conversion whose TARGET is `.clay` simply stops after the import,
// and every other conversion runs the import into a scratch directory it deletes afterwards
// (--keep-cells keeps it, which is the way to see what a conversion actually understood).
//
// WHAT THIS FILE REFUSES TO DO. It never guesses at a drill file's coordinate format and it never
// silently resolves an ambiguous layer mapping differently from the GUI. The GUI answers both with a
// dialog; headless, the first is a refusal that names the flag that answers it, and the second takes
// the same default the dialog pre-selects (add the layer to the technology) and says so.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Cli;

public static class LayoutConvert
{
    private enum Fmt { Clay, Gdsii, Dxf, Gerber, Board }

    private sealed class Options
    {
        public string? Input, Output, TechPath, Cws, KeepCells, Cell, Name;
        public Fmt? From, To;
        public bool ListCells;
        public int DbuPerMicron = 1000;

        // DXF
        public DxfAcadVersion AcadVersion = DxfAcadVersion.R2018;
        public int? InsUnits;

        // Excellon
        public GerberUnit? DrillUnit;
        public GerberZeroOmission? DrillZeros;
        public int? DrillIntegerDigits, DrillDecimalDigits;
        public bool AcceptInferredDrillFormat;
    }

    public static int Run(string[] args)
    {
        var o = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; break;
                case "--from" when i + 1 < args.Length:
                    if (ParseFormat(args[++i]) is not { } f) return BadFormat(args[i]);
                    o.From = f; break;
                case "--to" when i + 1 < args.Length:
                    if (ParseFormat(args[++i]) is not { } t) return BadFormat(args[i]);
                    o.To = t; break;
                case "--cell" when i + 1 < args.Length: o.Cell = args[++i]; break;
                case "--name" when i + 1 < args.Length: o.Name = args[++i]; break;
                case "--list-cells": o.ListCells = true; break;
                case "--tech" when i + 1 < args.Length: o.TechPath = args[++i]; break;
                case "--workspace" when i + 1 < args.Length: o.Cws = args[++i]; break;
                case "--keep-cells" when i + 1 < args.Length: o.KeepCells = args[++i]; break;
                case "--dbu" when i + 1 < args.Length && int.TryParse(args[i + 1], out int dbu):
                    o.DbuPerMicron = Math.Max(1, dbu); i++; break;

                case "--dxf-version" when i + 1 < args.Length:
                    switch (args[++i].ToUpperInvariant())
                    {
                        case "AC1015" or "R2000": o.AcadVersion = DxfAcadVersion.R2000; break;
                        case "AC1018" or "R2004": o.AcadVersion = DxfAcadVersion.R2004; break;
                        case "AC1032" or "R2018": o.AcadVersion = DxfAcadVersion.R2018; break;
                        default:
                            Console.Error.WriteLine($"Unknown DXF version '{args[i]}'. Known: AC1015, AC1018, AC1032.");
                            return 1;
                    }
                    break;
                case "--dxf-units" when i + 1 < args.Length && int.TryParse(args[i + 1], out int iu):
                    o.InsUnits = iu; i++; break;

                case "--drill-units" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "mm" or "metric" or "millimetres" or "millimeters": o.DrillUnit = GerberUnit.Millimetres; break;
                        case "in" or "inch" or "inches": o.DrillUnit = GerberUnit.Inches; break;
                        default: Console.Error.WriteLine($"Unknown drill unit '{args[i]}'. Known: mm, inch."); return 1;
                    }
                    break;
                case "--drill-zeros" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "leading": o.DrillZeros = GerberZeroOmission.Leading; break;
                        case "trailing": o.DrillZeros = GerberZeroOmission.Trailing; break;
                        default: Console.Error.WriteLine($"Unknown zero suppression '{args[i]}'. Known: leading, trailing."); return 1;
                    }
                    break;
                case "--drill-format" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out int id) || !int.TryParse(parts[1], out int dd))
                    {
                        Console.Error.WriteLine($"--drill-format wants integer:decimal digits, e.g. 2:4 — got '{args[i]}'.");
                        return 1;
                    }
                    o.DrillIntegerDigits = id; o.DrillDecimalDigits = dd; break;
                }
                case "--accept-inferred-drill-format": o.AcceptInferredDrillFormat = true; break;

                default:
                    if (a.StartsWith('-')) { Console.Error.WriteLine($"Unknown option: {a}"); return Usage(); }
                    if (o.Input is not null) { Console.Error.WriteLine("convert takes one input."); return Usage(); }
                    o.Input = a;
                    break;
            }
        }

        if (o.Input is null) return Usage();
        if (!File.Exists(o.Input) && !Directory.Exists(o.Input))
        {
            Console.Error.WriteLine($"Input not found: {o.Input}");
            return 1;
        }

        // The source format is inferable from what the path IS — a folder is a Gerber file set, and a
        // file with no telling extension is classified by CONTENT through the same classifier the
        // Gerber import itself uses, so `convert` and the import can never disagree about what a file
        // is. --from overrides all of it.
        Fmt from = o.From ?? DetectSource(o.Input) ?? Fmt.Clay;
        if (o.From is null && DetectSource(o.Input) is null)
        {
            Console.Error.WriteLine(
                $"Could not tell what '{Path.GetFileName(o.Input)}' is from its name or its content. " +
                "Name it with --from clay|gdsii|dxf|gerber|board.");
            return 1;
        }

        if (o.ListCells) return ListCells(o, from);

        if (o.Output is null)
        {
            Console.Error.WriteLine("convert needs an output: -o <path>.");
            return Usage();
        }

        Fmt? to = o.To ?? DetectTarget(o.Output);
        if (to is null)
        {
            Console.Error.WriteLine(
                $"'{o.Output}' does not name a format. Give it a known extension " +
                "(.clay, .gds, .dxf, .kicad_pcb) or say --to clay|gdsii|dxf|gerber|board.");
            return 1;
        }

        if (from == Fmt.Clay && to == Fmt.Clay)
        {
            Console.Error.WriteLine("clay to clay is a file copy, not a conversion — nothing to do.");
            return 1;
        }

        Console.Error.WriteLine($"[circuitRF] {Name(from)} -> {Name(to.Value)}");

        string? scratch = null;
        try
        {
            var src = LoadSource(o, from, to.Value, ref scratch);
            if (src is null) return 1;

            // --to clay: the import IS the conversion. The cells and the technology are already on
            // disk, in the folder the user named, and there is nothing left to write.
            if (to == Fmt.Clay)
            {
                Console.Error.WriteLine($"[circuitRF] wrote {src.CreatedCellDirs.Count} cell(s) and a technology to {o.Output}");
                foreach (var d in src.CreatedCellDirs) Console.WriteLine(d);
                return 0;
            }

            return Export(o, to.Value, src);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            if (scratch is not null && Directory.Exists(scratch))
                try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── What a source resolved to ─────────────────────────────────────────────────────────────────

    private sealed record Source(
        string CellDir,
        LayoutView View,
        Technology? Tech,
        int DbuPerMicron,
        string CellName,
        IReadOnlyList<string> CreatedCellDirs);

    private static Source? LoadSource(Options o, Fmt from, Fmt to, ref string? scratch)
    {
        if (from == Fmt.Clay) return LoadClay(o);

        // Everything else imports. The cells land where the user asked when clay is the target, and
        // in a scratch directory otherwise — one that is deleted on the way out unless --keep-cells
        // named somewhere to leave it.
        string staging;
        if (to == Fmt.Clay) staging = Path.GetFullPath(o.Output!);
        else if (o.KeepCells is { } keep) staging = Path.GetFullPath(keep);
        else
        {
            staging = Path.Combine(Path.GetTempPath(), "circuitrf-convert-" + Guid.NewGuid().ToString("N")[..12]);
            scratch = staging;
        }
        Directory.CreateDirectory(staging);

        Technology? destTech = null;
        if (o.TechPath is { } tp)
        {
            try { destTech = TechPersistence.LoadFromFile(tp); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read technology '{tp}': {ex.Message}");
                return null;
            }
        }

        string name = ImportName(o.Input!);

        // An EMPTY technology, never null, when the user named none. Every importer reconciles the
        // file's layers against the destination technology and reports the ones it would ADD; handed a
        // null destination it has nothing to compare against and adds nothing, so the layers arrive
        // with numeric keys and no names — and a re-export then names its Gerber files from a
        // synthetic suffix instead of the technology's own. An empty technology makes every source
        // layer an unmatched row, which is exactly what it is.
        destTech ??= new Technology { Name = name };

        return from switch
        {
            Fmt.Gdsii => ImportGdsii(o, staging, destTech, name),
            Fmt.Dxf   => ImportDxf(o, staging, destTech, name),
            Fmt.Board => ImportBoard(o, staging, destTech, name),
            Fmt.Gerber => ImportGerber(o, staging, destTech),
            _ => null,
        };
    }

    private static Source? LoadClay(Options o)
    {
        string clay = Path.GetFullPath(o.Input!);
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read layout '{clay}': {ex.Message}");
            return null;
        }

        // <cell>/layout/<name>.clay — the cell folder is two levels up, and every export walks the
        // hierarchy from it rather than from the file, because instances are cell references.
        string layoutDir = Path.GetDirectoryName(clay)!;
        string cellDir = Path.GetDirectoryName(layoutDir)!;

        Technology? tech;
        if (o.TechPath is { } tp)
        {
            try { tech = TechPersistence.LoadFromFile(tp); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not read technology '{tp}': {ex.Message}"); return null; }
        }
        else
        {
            // The same walk-up the GUI does and the `em` verb does: the .clay's own TechRef first,
            // then the nearest ancestor workspace's default. --workspace overrides the walk.
            string? cws = o.Cws is { } w ? Path.GetFullPath(w) : null;
            var (res, own) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, cws, new TechnologyCache());
            foreach (var d in res.Diagnostics) Console.Error.WriteLine($"warning: {d}");
            if (res.ResolvedPath is { } rp) Console.Error.WriteLine($"[circuitRF] technology: {rp}");
            else Console.Error.WriteLine(
                "[circuitRF] no technology resolved — layer names, Gerber suffixes and the stackup " +
                (own is null ? "are unavailable (no workspace above this layout)." : "are unavailable."));
            tech = res.Tech;
        }

        return new Source(cellDir, view, tech, view.DbuPerMicron,
            Path.GetFileNameWithoutExtension(clay), [cellDir]);
    }

    // ── The four importers, each reduced to the same Source ───────────────────────────────────────

    private static Source? ImportGdsii(Options o, string staging, Technology? destTech, string name)
    {
        using var stream = File.OpenRead(o.Input!);
        var r = GdsiiImport.Import(stream, staging, destTech, o.DbuPerMicron, preferSourceResolution: true);
        Report(r.Messages);
        if (r.Cancelled) return Refused();

        string? cellDir = PickCell(o.Cell, r.CreatedCellDirs,
            r.TopLevelCellDirs.Count > 0 ? r.TopLevelCellDirs[0] : null, "GDSII structure");
        if (cellDir is null) return null;

        var tech = MintTechnology(staging, name, destTech, r.LayersToAdd, stackup: null, r.CreatedCellDirs);
        return Finish(cellDir, tech, r.CreatedCellDirs);
    }

    private static Source? ImportDxf(Options o, string staging, Technology? destTech, string name)
    {
        using var stream = File.OpenRead(o.Input!);
        var r = DxfImport.Import(stream, staging, destTech, o.DbuPerMicron,
            resolveUnits: o.InsUnits is { } iu ? _ => iu : null);
        Report(r.Messages);
        if (r.Cancelled) return Refused();

        // Model space is the drawing itself; a BLOCK is a definition something else places. The
        // drawing is what a conversion means unless --cell says otherwise.
        string? top = r.CellNameByBlockName.TryGetValue(DxfReader.ModelSpaceName, out var modelCell)
            ? r.CreatedCellDirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), modelCell, StringComparison.OrdinalIgnoreCase))
            : null;

        string? cellDir = PickCell(o.Cell, r.CreatedCellDirs, top, "DXF block");
        if (cellDir is null) return null;

        var tech = MintTechnology(staging, name, destTech, r.LayersToAdd, stackup: null, r.CreatedCellDirs);
        var src = Finish(cellDir, tech, r.CreatedCellDirs);

        // "$MODEL" is DxfReader's own name for the drawing itself, not something anyone typed, and a
        // Gerber set called $MODEL.gbr is a file set nobody wants. The drawing's real name is the
        // file's.
        return src is not null && cellDir == top
            ? src with { CellName = Path.GetFileNameWithoutExtension(o.Input!) }
            : src;
    }

    private static Source? ImportBoard(Options o, string staging, Technology? destTech, string name)
    {
        using var stream = File.OpenRead(o.Input!);
        var r = PcbImport.Import(stream, staging, name, destTech, o.DbuPerMicron);
        Report(r.Messages);
        if (r.Cancelled) return Refused();

        string? cellDir = PickCell(o.Cell, r.CreatedCellDirs, r.BoardCellDir, "footprint");
        if (cellDir is null) return null;

        var tech = MintTechnology(staging, name, destTech, r.LayersToAdd, r.Stackup, r.CreatedCellDirs);
        return Finish(cellDir, tech, r.CreatedCellDirs);
    }

    private static Source? ImportGerber(Options o, string staging, Technology? destTech)
    {
        // A folder is the whole set. A single file is one layer — and unlike the GUI, which asks
        // whether the folder was meant, a command line already said which it meant by what it typed.
        var files = Directory.Exists(o.Input!)
            ? GerberImportEntry.FilesIn(Path.GetFullPath(o.Input!))
            : [Path.GetFullPath(o.Input!)];

        string importName = Directory.Exists(o.Input!)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(o.Input!)))
            : Path.GetFileNameWithoutExtension(o.Input!);

        var r = GerberImport.Import(files, staging, importName, destTech, o.DbuPerMicron,
            resolveDrillFormat: (fileName, inferred, crossCheck, _) => ResolveDrillFormat(o, fileName, inferred, crossCheck));
        Report(r.Messages);
        if (r.Cancelled) return Refused();
        if (r.CellDir is null) { Console.Error.WriteLine("The Gerber import produced no cell."); return null; }

        // Gerber import mints its own .ctech and points the .clay at it (R-L4g-8), so there is nothing
        // for MintTechnology to do here — this is the one importer that already did it.
        return Finish(r.CellDir, r.Technology, r.CreatedCellDirs);
    }

    /// <summary>
    /// The drill-format prompt, answered by flags. R-L4h-6's dialog exists because a drill file read at
    /// the wrong scale is the worst silent failure available here — leading vs trailing zero suppression
    /// differ by four orders of magnitude on identical text — so the headless answer is a REFUSAL that
    /// prints the inference and the evidence, not a guess. Returning null cancels the whole import.
    /// </summary>
    private static GerberImport.DrillFormatChoice? ResolveDrillFormat(
        Options o, string fileName, DrillFormatInference inferred, DrillExtentsCheck crossCheck)
    {
        var over = new DrillFormatOverride(o.DrillUnit, o.DrillIntegerDigits, o.DrillDecimalDigits, o.DrillZeros);
        bool anyOverride = o.DrillUnit is not null || o.DrillZeros is not null ||
                           o.DrillIntegerDigits is not null || o.DrillDecimalDigits is not null;

        // A flag is a statement about the RUN, not about one file, so it settles every drill file in
        // the set at once — which is also what stops the same refusal being printed once per file.
        if (anyOverride) return new GerberImport.DrillFormatChoice(over, ApplyToAll: true);
        if (o.AcceptInferredDrillFormat) return new GerberImport.DrillFormatChoice(null, ApplyToAll: true);

        Console.Error.WriteLine($"error: {fileName} does not state its coordinate format, and the inference had to guess.");
        Console.Error.WriteLine($"       Inferred: {inferred}");
        foreach (var e in inferred.Evidence) Console.Error.WriteLine($"       {e}");
        if (!crossCheck.Agrees) Console.Error.WriteLine($"       {crossCheck.Report}");
        Console.Error.WriteLine("       Accept it with --accept-inferred-drill-format, or state it:");
        Console.Error.WriteLine("         --drill-units mm|inch  --drill-format <int>:<dec>  --drill-zeros leading|trailing");
        return null;
    }

    // ── Between the two halves ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the technology an import DECLARED and points every cell it created at it.
    ///
    /// <para>The GUI grafts an import's layers onto the open workspace's technology, live; headless
    /// there is no workspace and no open technology, so the layers would otherwise be dropped on the
    /// floor and the export would name every layer by number. Writing a `.ctech` beside the cells is
    /// what Gerber import already does for itself (R-L4g-8), for the same reason, and it means an
    /// intermediate kept with --keep-cells opens in the application as a real design.</para>
    /// </summary>
    private static Technology MintTechnology(
        string staging, string name, Technology? destTech,
        IReadOnlyList<LayerDef> layersToAdd, Stackup? stackup, IReadOnlyList<string> cellDirs)
    {
        var tech = destTech is null
            ? new Technology { Name = name }
            : TechPersistence.Deserialize(TechPersistence.Serialize(destTech));

        foreach (var def in layersToAdd)
            if (!tech.Layers.Any(l => l.Key == def.Key)) tech.Layers.Add(def);

        // Never replace a stackup that is already there — the same rule board import follows in the
        // GUI: what was recovered is reported, and nothing already declared is overwritten.
        if (stackup is not null && tech.Stackup.Layers.Count == 0) tech.Stackup = stackup;

        string techPath = Path.Combine(staging, name + ".ctech");
        TechPersistence.SaveToFile(techPath, tech);
        Console.Error.WriteLine($"[circuitRF] technology: {techPath} ({tech.Layers.Count} layer(s))");

        foreach (var cellDir in cellDirs)
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
            if (primary.ResolvedName is not { } file) continue;
            string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            string clay = Path.Combine(layoutDir, file);
            var view = LayoutPersistence.LoadFromFile(clay);
            view.TechRef = Path.GetRelativePath(layoutDir, techPath);
            LayoutPersistence.SaveToFile(clay, view);
        }

        return tech;
    }

    private static Source? Finish(string cellDir, Technology? tech, IReadOnlyList<string> createdCellDirs)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.ResolvedName is not { } file)
        {
            Console.Error.WriteLine($"'{Path.GetFileName(cellDir)}' holds no layout view to convert.");
            return null;
        }

        string clay = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), file);
        var view = LayoutPersistence.LoadFromFile(clay);
        return new Source(cellDir, view, tech, view.DbuPerMicron,
            Path.GetFileNameWithoutExtension(file), createdCellDirs);
    }

    // ── The four writers ──────────────────────────────────────────────────────────────────────────

    private static int Export(Options o, Fmt to, Source src)
    {
        string output = Path.GetFullPath(o.Output!);
        string? dir = to == Fmt.Gerber ? output : Path.GetDirectoryName(output);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

        string stem = o.Name ?? src.CellName;
        Console.Error.WriteLine($"[circuitRF] cell: {src.CellName}  ({src.DbuPerMicron} DBU/µm)");

        switch (to)
        {
            case Fmt.Gdsii:
            {
                var plan = GdsiiExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View);
                foreach (var u in plan.UnresolvedInstanceReferences)
                    Console.Error.WriteLine($"warning: unresolved instance reference '{u}' — not written.");
                if (!plan.CanWrite)
                {
                    Console.Error.WriteLine("error: coordinates overflow GDSII's 32-bit integer range — nothing written.");
                    foreach (var c in plan.CoordinateOverflowOffenders) Console.Error.WriteLine($"       {c}");
                    return 1;
                }
                GdsiiExport.Write(output, plan);
                Note(plan.CurvedShapesFlattened, "curved shape", "flattened to polygons");
                Note(plan.HolesKeyholed, "hole", "keyholed");
                Note(plan.BitmapsSkipped, "bitmap", "skipped — GDSII carries no raster");
                Note(plan.ViaPadsSkipped, "via", "exported as a barrel with no landing pad");
                if (plan.HasVias) Console.Error.WriteLine(
                    "note: this design has vias, and GDSII carries no drill table — geometry only, not a PCB deliverable.");
                Console.WriteLine(output);
                return 0;
            }

            case Fmt.Dxf:
            {
                var plan = DxfExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View);
                foreach (var u in plan.UnresolvedInstanceReferences)
                    Console.Error.WriteLine($"warning: unresolved instance reference '{u}' — not written.");
                var opts = new DxfExportOptions(
                    InsUnits: o.InsUnits ?? DxfUnits.DefaultPromptUnits,
                    AcadVersion: o.AcadVersion);
                var summary = DxfExport.Write(output, plan, opts);
                foreach (var d in summary.Diagnostics) Console.Error.WriteLine($"note: {d}");
                Note(summary.BitmapsSkipped, "bitmap", "skipped — DXF carries no raster");
                Note(summary.MixedArcCubicApproximated, "mixed arc/cubic edge", "approximated");
                Note(summary.NonAsciiTextEscaped, "text string", "escaped for the chosen DXF version");
                if (plan.HasVias) Console.Error.WriteLine(
                    "note: this design has vias, and DXF carries no drill table — geometry only, not a PCB deliverable.");
                Console.WriteLine(output);
                return 0;
            }

            case Fmt.Board:
            {
                var plan = PcbExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View);
                if (!plan.CanWrite)
                {
                    Console.Error.WriteLine($"error: {plan.Refusal}");
                    return 1;
                }
                var summary = PcbExport.Write(output, plan);
                foreach (var n in summary.Notes) Console.Error.WriteLine($"note: {n}");
                foreach (var l in summary.UnmappedLayerNames)
                    Console.Error.WriteLine($"warning: layer '{l}' has no board-layer mapping — written to a general drawing layer.");
                Note(plan.CellsFlattenedForLackOfPins, "cell", "flattened into board geometry: it declares no pins, so it is not a footprint");
                Note(plan.UnresolvedInstanceReferences, "instance reference", "unresolved — not written");
                Note(summary.PinsWithNoArtwork, "pin", "has no artwork on any copper layer");
                Console.WriteLine(output);
                return 0;
            }

            case Fmt.Gerber:
            {
                var cache = new TechnologyCache();
                var plan = GerberExport.Analyze(src.CellDir, src.Tech, src.DbuPerMicron, src.View,
                    resolveTechAt: (techRef, cellLayoutDir) =>
                        TechnologyResolver.ResolveForDocument(techRef, cellLayoutDir, o.Cws, cache).Resolution);

                foreach (var d in plan.Diagnostics) Console.Error.WriteLine($"error: {d}");
                if (plan.ExceedsHierarchyCeiling) return 1;

                if (plan.RequiresMappingConfirmation)
                {
                    // The GUI resolves this with the shared layer-mapping dialog. There is no honest
                    // headless default: a sub-cell drawn against a DIFFERENT technology has layers
                    // whose numbers mean something else, and picking for the user would silently move
                    // copper between layers.
                    Console.Error.WriteLine(
                        "error: this design instantiates cells from another technology, and the layer mapping " +
                        "has to be confirmed. Open it in circuitRF and export once, or flatten the design first.");
                    foreach (var k in plan.PendingCrossTechMappings.Keys) Console.Error.WriteLine($"       {k}");
                    return 1;
                }

                var result = GerberExport.Write(output, stem, plan);
                Note(plan.LabelsConvertedToGeometry, "label", "converted to geometry — Gerber has no text");
                Note(plan.PortLabelsOmitted, "port label", "omitted: a marker, not artwork");
                Note(plan.BitmapsOmitted, "bitmap", "omitted — Gerber carries no raster");
                Note(plan.PathsAsRegion, "path", "written as a region: its end style is not a round cap");
                Note(plan.UnpairedDrillCircles, "bare circle", "on a drill layer drilled a hole with no pad — 'Convert to Via' pairs them");
                if (plan.LabelsConvertedToGeometry > 0 && !LayoutTextOutline.HasEmbeddedTypefaces)
                    Console.Error.WriteLine(
                        "note: labels were flattened with the platform default typeface — the embedded faces the " +
                        "application draws with need a running app, so this glyph artwork differs from the GUI's.");
                Console.Error.WriteLine(
                    $"[circuitRF] {result.FilesWritten.Count} file(s), " +
                    $"{result.DrillToolsDefined} drill tool(s), {result.DrillHitsWritten} hit(s)");
                foreach (var f in result.FilesWritten) Console.WriteLine(f);
                return 0;
            }

            default:
                Console.Error.WriteLine($"Cannot write {Name(to)}.");
                return 1;
        }
    }

    // ── --list-cells ──────────────────────────────────────────────────────────────────────────────

    private static int ListCells(Options o, Fmt from)
    {
        if (from == Fmt.Clay)
        {
            Console.Error.WriteLine("A .clay names one cell — --list-cells applies to a file that can hold several.");
            return 1;
        }

        string staging = Path.Combine(Path.GetTempPath(), "circuitrf-list-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            string? scratch = staging;
            var src = LoadSource(o, from, Fmt.Gdsii /* anything but Clay: import into scratch */, ref scratch);
            if (src is null) return 1;
            foreach (var d in src.CreatedCellDirs) Console.WriteLine(Path.GetFileName(d));
            return 0;
        }
        finally
        {
            if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch { /* best effort */ }
        }
    }

    // ── Small shared pieces ───────────────────────────────────────────────────────────────────────

    private static string? PickCell(string? wanted, IReadOnlyList<string> created, string? preferred, string what)
    {
        if (wanted is not null)
        {
            var hit = created.FirstOrDefault(d => string.Equals(Path.GetFileName(d), wanted, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
            Console.Error.WriteLine($"No cell named '{wanted}' in this file. It holds: {string.Join(", ", created.Select(Path.GetFileName))}");
            return null;
        }

        if (preferred is not null) return preferred;
        if (created.Count == 1) return created[0];

        Console.Error.WriteLine(
            $"This file holds {created.Count} cells and none of them is an unambiguous top ({what} definitions " +
            "are all referenced by something else). Name one with --cell, or list them with --list-cells.");
        return null;
    }

    private static Source? Refused()
    {
        Console.Error.WriteLine("Nothing was converted.");
        return null;
    }

    private static void Report(IReadOnlyList<string> messages)
    {
        foreach (var m in messages) Console.Error.WriteLine($"note: {m}");
    }

    private static void Note(int n, string noun, string what)
    {
        if (n > 0) Console.Error.WriteLine($"note: {n} {noun}{(n == 1 ? "" : "s")} {what}.");
    }

    private static string ImportName(string input) =>
        Directory.Exists(input)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)))
            : Path.GetFileNameWithoutExtension(input);

    private static Fmt? ParseFormat(string s) => s.ToLowerInvariant() switch
    {
        "clay" or "layout" or "circuitrf" => Fmt.Clay,
        "gds" or "gdsii" or "gds2" => Fmt.Gdsii,
        "dxf" => Fmt.Dxf,
        "gerber" or "rs274x" or "excellon" => Fmt.Gerber,
        "board" or "kicad_pcb" => Fmt.Board,   // the extension is a data format; the bare product name is not ours to use
        _ => null,
    };

    private static int BadFormat(string s)
    {
        Console.Error.WriteLine($"Unknown format '{s}'. Known: clay, gdsii, dxf, gerber, board.");
        return 1;
    }

    private static string Name(Fmt f) => f switch
    {
        Fmt.Clay => "clay", Fmt.Gdsii => "GDSII", Fmt.Dxf => "DXF",
        Fmt.Gerber => "Gerber", _ => "board",
    };

    private static Fmt? DetectSource(string path)
    {
        if (Directory.Exists(path)) return Fmt.Gerber;
        if (ByExtension(path) is { } byExt) return byExt;

        // No telling extension. Gerber and Excellon files are named however the toolchain that wrote
        // them felt like, so the answer comes from the content — through the classifier the import
        // itself uses, never a second rule.
        try
        {
            var kind = GerberFileClassifier.Classify(path).Kind;
            if (kind is GerberFileKind.Artwork or GerberFileKind.Drill or GerberFileKind.JobFile) return Fmt.Gerber;
        }
        catch (IOException) { /* fall through to "cannot tell" */ }
        return null;
    }

    private static Fmt? DetectTarget(string path) => ByExtension(path);

    private static Fmt? ByExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".clay" => Fmt.Clay,
        ".gds" or ".gdsii" or ".gds2" => Fmt.Gdsii,
        ".dxf" => Fmt.Dxf,
        ".kicad_pcb" => Fmt.Board,
        _ => null,
    };

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf convert <input> -o <output> [--from f] [--to f] [--cell name]");
        Console.Error.WriteLine("       formats: clay | gdsii | dxf | gerber | board");
        return 1;
    }
}
