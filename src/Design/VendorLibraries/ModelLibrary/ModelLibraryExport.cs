using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using SymbolModel = CircuitRF.Design.Symbol.Symbol;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>What Export to Library is asked for (brief vendorlib-10 §3, §4). Every field is the dialog's and the
/// CLI's flag of the same name; null or empty means the dialog's default.</summary>
public sealed record ExportOptions
{
    /// <summary>The library folder. An empty or missing folder is a new library.</summary>
    public required string LibraryDir { get; init; }
    /// <summary>The category folder under the library, <c>/</c>-separated (<c>Amplifiers/LNA</c>); empty puts the
    /// model at the library's root.</summary>
    public string Category { get; init; } = "";
    /// <summary>The part number; null means the cell's name.</summary>
    public string? PartNumber { get; init; }
    public string Description { get; init; } = "";
    /// <summary><see cref="ModelPartManifest.KindActive"/> or <see cref="ModelPartManifest.KindPassive"/>; null is the
    /// default the cell implies (<see cref="ExportPlan.DefaultKind"/>).</summary>
    public string? Kind { get; init; }
    /// <summary>A passive's value (<c>10 pF</c>); ignored for an active part.</summary>
    public string Value { get; init; } = "";
    public string FrequencyLow { get; init; } = "";
    public string FrequencyHigh { get; init; } = "";
    public string Conditions { get; init; } = "";
    public string Footprint { get; init; } = "";
    public string CaseMetric { get; init; } = "";
    public string CaseImperial { get; init; } = "";
    /// <summary>Files copied into the model's <c>docs/</c>.</summary>
    public IReadOnlyList<string> Documents { get; init; } = [];
    /// <summary>The symbol: one of <see cref="ModelLibraryExport.SymbolChoices"/>; null is the default
    /// (<see cref="ExportPlan.DefaultSymbol"/>).</summary>
    public string? Symbol { get; init; }
    /// <summary>Replace a model already exported under this part number. The dialog asks; headless it is
    /// <c>--replace</c>.</summary>
    public bool Replace { get; init; }
    /// <summary>Copy the vendor parts the cell places into the model, carrying their libraries' terms with it. The
    /// dialog lists them; headless it is <c>--include-vendor-parts</c>.</summary>
    public bool IncludeVendorParts { get; init; }

    // library.clib, used only when the library has none
    public string LibraryName { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string LibraryVersion { get; init; } = "1.0.0";
    public string License { get; init; } = "";
}

/// <summary>One thing an export brings into the model folder.</summary>
public enum ExportCopyKind
{
    /// <summary>A file a document references, into <c>models/</c>.</summary>
    File,
    /// <summary>A sub-cell the schematic places, into <c>cells/</c>.</summary>
    SubCell,
    /// <summary>A vendor SPICE part's own <c>.subckt</c> block, into <c>models/</c>.</summary>
    VendorSpicePart,
    /// <summary>A vendor library's model, as a sub-cell.</summary>
    VendorModel,
}

/// <param name="Source">The absolute file or folder copied.</param>
/// <param name="Library">For a vendor part, the name of the library it came from.</param>
/// <param name="Part">For a vendor part, its part number.</param>
public sealed record ExportCopy(ExportCopyKind Kind, string Source, string? Library = null, string? Part = null)
{
    /// <summary>One line, as the dialog lists it.</summary>
    public string Describe() => Kind switch
    {
        ExportCopyKind.File            => $"file  {Source}",
        ExportCopyKind.SubCell         => $"sub-cell  {System.IO.Path.GetFileName(Source)}  ({Source})",
        ExportCopyKind.VendorSpicePart => $"vendor part  {Part}  from library '{Library}'",
        ExportCopyKind.VendorModel     => $"vendor model  {Part}  from library '{Library}'",
        _ => Source,
    };
}

/// <summary>
/// What an export of one cell would do, read without writing anything — what the dialog shows live
/// (R-vl10-3e) and what <see cref="ModelLibraryExport.Export"/> refuses on first.
/// </summary>
public sealed record ExportPlan
{
    public required string CellDir { get; init; }
    public required string PartNumber { get; init; }
    /// <summary>The model folder the export writes, absolute.</summary>
    public required string ModelDir { get; init; }
    /// <summary>The cell's pins, in port order — the model's pins.</summary>
    public IReadOnlyList<string> Pins { get; init; } = [];
    /// <summary>Active when any component is nonlinear, Passive otherwise (R-vl10-3c).</summary>
    public string DefaultKind { get; init; } = ModelPartManifest.KindPassive;
    /// <summary>The symbol the export writes when none is asked for (R-vl10-3d).</summary>
    public string DefaultSymbol { get; init; } = ModelLibraryExport.SymbolBox;
    /// <summary>The cell has a primary <c>.csym</c> of its own.</summary>
    public bool HasOwnSymbol { get; init; }
    /// <summary>A model is already exported under this part number; its revision, else 0.</summary>
    public int ExistingRevision { get; init; }
    public bool Exists => ExistingRevision > 0 || Directory.Exists(ModelDir);
    /// <summary>The library folder holds no <c>library.clib</c> yet; Export writes one.</summary>
    public bool LibraryIsNew { get; init; }
    public IReadOnlyList<ExportCopy> Copies { get; init; } = [];
    /// <summary>Every reason the export cannot go ahead. Nothing is written while any stands.</summary>
    public IReadOnlyList<string> Refusals { get; init; } = [];

    public IEnumerable<ExportCopy> VendorParts
        => Copies.Where(c => c.Kind is ExportCopyKind.VendorSpicePart or ExportCopyKind.VendorModel);
}

/// <summary>What an export did.</summary>
public sealed record ExportResult
{
    /// <summary>The model folder written; null when nothing was.</summary>
    public string? ModelDir { get; init; }
    /// <summary>Why nothing was written.</summary>
    public string? Refusal { get; init; }
    public bool Cancelled { get; init; }
    public int Revision { get; init; }
    /// <summary>Every file written, absolute, in ordinal order — the result a caller reports.</summary>
    public IReadOnlyList<string> Created { get; init; } = [];
    /// <summary>The <c>library.clib</c> written, when the library was new.</summary>
    public string? LibraryManifest { get; init; }
    /// <summary>The one Messages line (R-vl10-2g).</summary>
    public string? Message { get; init; }
    /// <summary>check's warnings on the new folder, which do not stop an export.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Succeeded => ModelDir is not null;
}

/// <summary>
/// Export to Library (brief vendorlib-10): a cell packaged as a self-contained model folder (brief 6's format) in a
/// model library. <b>The one implementation</b> — the dialog and <c>circuitrf export part</c> both call
/// <see cref="Plan"/> and <see cref="Export"/> and hold no logic of their own (R-vl10-1b).
///
/// <para><b>Order</b> (R-vl10-2): every refusal before anything is written; the model built in a temporary folder
/// beside its destination; the cell and everything it uses copied in with each reference rewritten relative to the
/// model folder; the netlist extracted from the COPY; <c>part.cpart</c> written last but its hash; brief 6's check
/// and then the self-check, which runs the source cell and the exported model side by side; and only then the
/// rename. A refusal or a cancel deletes the temporary folder, so nothing is ever left half-written.</para>
///
/// <para><b>The temporary folder is <c>&lt;category&gt;/.&lt;part&gt;.tmp-&lt;id&gt;/&lt;part&gt;</c></b>, not the
/// brief's <c>&lt;part&gt;.tmp-*</c>: a model folder is held to carry its part number as its name (check's
/// <c>check.model.part-not-folder</c>, and the netlist is named after the folder), so the check that must pass before
/// the rename can only pass on a folder already called <c>&lt;part&gt;</c>. The leading dot keeps the library index
/// and <see cref="ModelFolder.Under"/> from listing it meanwhile.</para>
/// </summary>
public static partial class ModelLibraryExport
{
    public const string SymbolOwn = "own";
    public const string SymbolBox = "box";
    public const string SymbolFet = "FET";

    /// <summary>The symbols a model can be exported with: the cell's own <c>.csym</c>, the FET glyph, R, C, L, or the
    /// auto-generated box.</summary>
    public static readonly IReadOnlyList<string> SymbolChoices = [SymbolOwn, SymbolFet, "R", "C", "L", SymbolBox];

    /// <summary>The self-check's tolerance: relative, per entry, with a floor for entries that are zero on both
    /// sides (a node no source biases, an S-parameter of an ideal short).</summary>
    public const double SelfCheckTolerance = 1e-9;
    private const double SelfCheckFloor = 1e-12;

    // ── Plan ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What exporting <paramref name="cellDir"/> with <paramref name="options"/> would do, and every refusal. Reads,
    /// never writes. The dialog calls it on each change; <see cref="Export"/> calls it first.
    /// </summary>
    public static ExportPlan Plan(string cellDir, ExportOptions options)
    {
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        string cellName = Path.GetFileName(dir);
        string part = (options.PartNumber ?? "").Trim();
        if (part.Length == 0) part = cellName;

        var refusals = new List<string>();
        var walk = new Walk();

        if (NameValidator.Validate(part) is { } bad) refusals.Add($"The part number '{part}' cannot be used: {bad}");

        string libraryDir = Path.GetFullPath(options.LibraryDir.Trim().Length == 0 ? "." : options.LibraryDir);
        string category = NormaliseCategory(options.Category);
        if (category.Split('/').Any(s => s is "." or ".." || (s.Length > 0 && NameValidator.Validate(s) is not null)))
            refusals.Add($"The category '{options.Category}' is not a folder path a library can hold.");
        string modelDir = Path.Combine(libraryDir, RefPathNative(category), part);

        if (ModelFolder.OwningModelFolder(libraryDir) is { } inside)
            refusals.Add($"'{libraryDir}' is inside the model folder '{inside}'; a library holds model folders, not the other way round.");
        if (IsSameOrUnder(libraryDir, dir) || IsSameOrUnder(modelDir, dir))
            refusals.Add("The library folder cannot be inside the cell being exported.");

        // The cell itself.
        CcellFile ccell = new();
        string ccellPath = Path.Combine(dir, CellFolder.CcellFileName);
        if (File.Exists(ccellPath))
        {
            try { ccell = CellPersistence.LoadFromFile(ccellPath); }
            catch (Exception ex) { refusals.Add($"{ccellPath} could not be read ({ex.Message})."); }
        }
        if (ccell.IsTestBench)
            refusals.Add($"'{cellName}' is a test bench; a model is a cell with pins that another design places.");
        if (ccell.ExternalProvider is { Length: > 0 } kit)
            refusals.Add($"'{cellName}' is a part of the kit '{kit}'; a kit part cannot be exported, because a model must run on a machine that does not have the kit.");

        string? schPath = PrimaryPath(dir, ViewType.Schematic);
        string? symPath = PrimaryPath(dir, ViewType.Symbol);
        IReadOnlyList<string> pins = [];
        string defaultKind = ModelPartManifest.KindPassive;

        if (schPath is null)
            refusals.Add($"'{cellName}' has no primary schematic to export.");
        else
        {
            walk.Visit(schPath, dir, top: true, refusals);
            if (walk.TopPinCount == 0)
                refusals.Add($"'{cellName}' has no Pin; a model connects to the design that places it through its Pins.");

            var extracted = NetExtractor.ExtractCell(dir, out string? why);
            if (extracted is null) refusals.Add(why ?? $"'{cellName}' could not be extracted.");
            else
            {
                pins = [.. extracted.Value.Library.Find(extracted.Value.CellName)!.Ports];
                if (extracted.Value.Library.Cells.SelectMany(c => c.Instances).Any(i => IsNonlinearReference(i.Reference)))
                    defaultKind = ModelPartManifest.KindActive;
            }
        }
        if (walk.Nonlinear) defaultKind = ModelPartManifest.KindActive;

        if (!options.IncludeVendorParts)
            foreach (var v in walk.Copies.Where(c => c.Kind is ExportCopyKind.VendorSpicePart or ExportCopyKind.VendorModel))
                refusals.Add($"{v.Part} comes from the vendor library '{v.Library}', and exporting copies it into this model, so that library's terms travel with it. Include vendor parts to accept that.");

        // What the export would replace.
        int revision = 0;
        if (Directory.Exists(modelDir))
        {
            var (existing, _) = ModelPartManifest.Read(Path.Combine(modelDir, ModelFolder.PartFileName));
            revision = existing is null ? 0 : Math.Max(existing.Revision, 1);
            if (existing is null && Directory.EnumerateFileSystemEntries(modelDir).Any())
                refusals.Add($"'{modelDir}' already exists and is not a model folder; it is never replaced.");
            else if (!options.Replace)
                refusals.Add($"{part} is already exported to '{Path.GetDirectoryName(modelDir)}' (revision {revision}). Replace it to export again.");
        }

        string kind = options.Kind ?? defaultKind;
        string value = kind == ModelPartManifest.KindPassive ? options.Value : "";
        bool hasOwn = symPath is not null;
        string defaultSymbol = DefaultSymbolFor(hasOwn, pins, kind, value);
        string symbol = options.Symbol ?? defaultSymbol;
        if (!SymbolChoices.Contains(symbol, StringComparer.Ordinal))
            refusals.Add($"'{symbol}' is not a symbol a model can take; it is one of {string.Join(", ", SymbolChoices)}.");
        else if (symbol == SymbolOwn && !hasOwn)
            refusals.Add($"'{cellName}' has no symbol of its own; choose the FET glyph, R, C, L or the box.");
        else if (symbol is "R" or "C" or "L" && pins.Count != 2)
            refusals.Add($"The {symbol} glyph has two pins and '{cellName}' has {pins.Count}.");
        else if (symbol == SymbolFet && pins.Count is not (3 or 4))
            refusals.Add($"The FET glyph has three or four pins and '{cellName}' has {pins.Count}.");
        if (options.Kind is { } k && k is not (ModelPartManifest.KindActive or ModelPartManifest.KindPassive))
            refusals.Add($"The kind '{k}' is not one a model has; it is Active or Passive.");

        foreach (string doc in options.Documents)
            if (!File.Exists(doc)) refusals.Add($"The document '{doc}' does not exist.");

        return new ExportPlan
        {
            CellDir = dir, PartNumber = part, ModelDir = modelDir, Pins = pins,
            DefaultKind = defaultKind, DefaultSymbol = defaultSymbol, HasOwnSymbol = hasOwn,
            ExistingRevision = revision,
            LibraryIsNew = !File.Exists(Path.Combine(libraryDir, ModelFolder.LibraryFileName)),
            Copies = walk.Copies, Refusals = refusals.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>R-vl10-3d's default, first match wins: the cell's own symbol; the FET glyph for 3 or 4 pins of an active
    /// cell whose names bind; R, C or L for a two-pin passive whose value is one; the box.</summary>
    public static string DefaultSymbolFor(bool hasOwnSymbol, IReadOnlyList<string> pins, string kind, string value)
    {
        if (hasOwnSymbol) return SymbolOwn;
        if (kind == ModelPartManifest.KindActive && pins.Count is 3 or 4
            && VerilogASymbolProvider.DefaultKind(pins.Count, VerilogASymbolProvider.FormatTerminals(pins, [])) is not null)
            return SymbolFet;
        // Read as the index reads a model's Value, so the glyph and value search agree on what it is.
        if (kind == ModelPartManifest.KindPassive && pins.Count == 2 && value.Trim().Length > 0
            && VendorLibrarySearch.TryReadValue(value.Replace(" ", ""), out _, out var q))
            return q switch
            {
                VendorPartQuantity.Capacitance => "C",
                VendorPartQuantity.Inductance  => "L",
                VendorPartQuantity.Resistance  => "R",
                _ => SymbolBox,
            };
        return SymbolBox;
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exports the cell at <paramref name="cellDir"/> (R-vl10-2). Never throws for a refusal: the result says why
    /// and nothing is left on disk. <paramref name="control"/> carries progress and Cancel.
    /// </summary>
    public static ExportResult Export(string cellDir, ExportOptions options, RunControl? control = null)
    {
        var plan = Plan(cellDir, options);
        if (plan.Refusals.Count > 0) return new ExportResult { Refusal = string.Join(" ", plan.Refusals) };

        string modelDir   = plan.ModelDir;
        string parent     = Path.GetDirectoryName(modelDir)!;
        string libraryDir = Path.GetFullPath(options.LibraryDir);
        string id         = Guid.NewGuid().ToString("N")[..8];
        string wrapper    = Path.Combine(parent, $".{plan.PartNumber}.tmp-{id}");
        string tmp        = Path.Combine(wrapper, plan.PartNumber);
        string? backup    = null;
        bool libraryNew   = plan.LibraryIsNew;

        try
        {
            Directory.CreateDirectory(tmp);
            control?.BeginStage("Copying the cell");
            var built = Build(plan, options, tmp);
            if (Cancelled(control)) return Cancel(wrapper);

            control?.BeginStage("Writing the netlist");
            // A provisional manifest first: OwningRoot finds the folder as a model folder only once part.cpart is
            // there, and the extraction resolves every relative reference against it (brief 8).
            string partPath = Path.Combine(tmp, ModelFolder.PartFileName);
            ModelPartManifest.Write(partPath, new ModelPartManifest { Part = plan.PartNumber, Cell = plan.PartNumber, Pins = [.. plan.Pins] });
            InvalidateResolution();

            string? cnl = ModelNetlist.FromCell(tmp, out string? why);
            if (cnl is null) return Refuse(wrapper, why ?? "The copied cell could not be extracted.");
            if (ModelFolderCheck.TopLevelStatements(cnl).FirstOrDefault() is { Statement: { } stray })
                return Refuse(wrapper, $"The extracted netlist holds '{stray}' outside every define block.");
            File.WriteAllText(ModelNetlist.PathOf(tmp), cnl);

            var (lib, _) = new CnlReader().Read(cnl, "model", sourceDirectory: null);
            var top = lib.Find(plan.PartNumber);
            if (top is null) return Refuse(wrapper, $"The extracted netlist has no define named {plan.PartNumber}.");

            string kind = options.Kind ?? plan.DefaultKind;
            string symbol = options.Symbol ?? DefaultSymbolFor(plan.HasOwnSymbol, plan.Pins, kind,
                                                               kind == ModelPartManifest.KindPassive ? options.Value : "");
            var manifest = new ModelPartManifest
            {
                Part = plan.PartNumber, Cell = top.Name, Kind = kind, Description = options.Description,
                Pins = [.. top.Ports],
                Parameters = [.. top.Parameters.Where(p => !p.Hidden).Select(p => new ModelPartParameter
                {
                    Name = p.Name, Default = Spell(p.DefaultExpression, p.Unit),
                })],
                Glyph = symbol is SymbolOwn or SymbolBox ? "" : symbol,
                Value = kind == ModelPartManifest.KindPassive ? options.Value.Trim() : "",
                Frequency = options.FrequencyLow.Trim().Length + options.FrequencyHigh.Trim().Length == 0 ? null
                    : new ModelPartFrequency { Low = options.FrequencyLow.Trim(), High = options.FrequencyHigh.Trim() },
                Conditions = options.Conditions, Footprint = options.Footprint,
                CaseMetric = options.CaseMetric, CaseImperial = options.CaseImperial,
                Revision = plan.ExistingRevision + 1,
                Converted = [],
            };
            ModelPartManifest.Write(partPath, manifest);

            string? clib = null;
            if (libraryNew)
            {
                Directory.CreateDirectory(libraryDir);
                clib = Path.Combine(libraryDir, ModelFolder.LibraryFileName);
            }

            // PublishedHash last: over every file but part.cpart, so writing it does not change it.
            manifest = manifest with { PublishedHash = ModelPartHash.Compute(tmp) };
            ModelPartManifest.Write(partPath, manifest);
            if (Cancelled(control)) return Cancel(wrapper);

            control?.BeginStage("Checking the model");
            InvalidateResolution();
            var findings = ModelFolderCheck.Validate(tmp);
            var errors = findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                return Refuse(wrapper, "The exported model does not check clean: " + string.Join(" ", errors.Select(e => e.Render())));

            control?.BeginStage("Self-check", 2, "run(s)");
            if (SelfCheck(plan.CellDir, tmp, manifest, control) is { } mismatch) return Refuse(wrapper, mismatch);
            if (Cancelled(control)) return Cancel(wrapper);

            // The rename. A replaced model is moved aside first and deleted only once the new one is in place.
            if (Directory.Exists(modelDir))
            {
                backup = Path.Combine(parent, $".{plan.PartNumber}.old-{id}");
                Directory.Move(modelDir, backup);
            }
            Directory.Move(tmp, modelDir);
            TryDelete(wrapper);
            if (backup is not null) TryDelete(backup);
            if (clib is not null)
                ModelLibraryManifest.Write(clib, new ModelLibraryManifest
                {
                    Name = options.LibraryName.Trim().Length > 0 ? options.LibraryName.Trim() : Path.GetFileName(Path.TrimEndingDirectorySeparator(libraryDir)),
                    Publisher = options.Publisher, LibraryVersion = options.LibraryVersion, License = options.License,
                });
            InvalidateResolution();

            var created = Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories)
                                   .Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToList();
            string where = Path.GetRelativePath(Path.GetDirectoryName(libraryDir) ?? libraryDir, parent).Replace('\\', '/');
            return new ExportResult
            {
                ModelDir = modelDir, Revision = manifest.Revision, Created = created, LibraryManifest = clib,
                Message = $"Exported {plan.PartNumber} to {where} (revision {manifest.Revision}).",
                Warnings = [.. findings.Where(f => f.Severity != DiagnosticSeverity.Error).Select(f => f.Render()).Concat(built.Warnings)],
            };
        }
        catch (OperationCanceledException) { return Cancel(wrapper); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (backup is not null && !Directory.Exists(modelDir) && Directory.Exists(backup))
                try { Directory.Move(backup, modelDir); } catch { }
            return Refuse(wrapper, $"The export could not be written: {ex.Message}");
        }
    }

    private static bool Cancelled(RunControl? c)
        => c is not null && (c.StopRequested || c.Token.IsCancellationRequested);

    private static ExportResult Refuse(string wrapper, string why)
    {
        TryDelete(wrapper);
        InvalidateResolution();
        return new ExportResult { Refusal = why };
    }

    private static ExportResult Cancel(string wrapper)
    {
        TryDelete(wrapper);
        InvalidateResolution();
        return new ExportResult { Refusal = "Export to Library was cancelled; nothing was written.", Cancelled = true };
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>The memoised walks that answer "which root does this folder belong to" — a <c>part.cpart</c>
    /// appearing or a folder moving changes the answer.</summary>
    private static void InvalidateResolution()
    {
        WorkspaceRootFinder.InvalidateCache();
        OwningRoot.InvalidateCache();
        ExternalCellRef.InvalidateCache();
        CellStat.InvalidateCache();
    }

    // ── The walk (Plan's half) ───────────────────────────────────────────────────────────────────

    /// <summary>Every schematic the export reaches, every sub-cell, file and vendor part, and every refusal on the
    /// way — read once for the plan.</summary>
    private sealed class Walk
    {
        public readonly List<ExportCopy> Copies = [];
        public int TopPinCount;
        public bool Nonlinear;
        private readonly HashSet<string> _visited = new(DocumentFileRefs.PathComparer);
        private readonly HashSet<string> _files = new(DocumentFileRefs.PathComparer);

        public void Visit(string schPath, string cellDir, bool top, List<string> refusals)
        {
            if (!_visited.Add(Path.GetFullPath(schPath))) return;
            SchematicEditModel model;
            try { model = SchematicPersistence.LoadFromFile(schPath).model; }
            catch (Exception ex) { refusals.Add($"{schPath} could not be read ({ex.Message})."); return; }
            string schDir = Path.GetDirectoryName(Path.GetFullPath(schPath))!;
            model.SchematicDirectory = schDir;
            string where = top ? "" : $" (in {Path.GetFileName(cellDir)})";

            foreach (var comp in model.Components)
            {
                if (top && comp.Symbol == SymbolKind.Pin) TopPinCount++;
                string name = comp.InstanceName + where;

                if (IsKitPart(comp))
                {
                    refusals.Add($"{name} is a kit (pdk://) part; a model must run on a machine that does not have the kit, so it cannot be exported.");
                    continue;
                }
                if (IsTechnologyBound(comp.Symbol))
                {
                    refusals.Add($"{name} ({ComponentTypeRegistry.EngineReference(comp.Symbol)}) takes its substrate from the technology; a model cannot carry one.");
                    continue;
                }
                if (IsNonlinearReference(ComponentTypeRegistry.EngineReference(comp.Symbol))) Nonlinear = true;

                string? file = comp.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.Expression?.Trim();
                if (comp.Symbol == SymbolKind.SpiceModel && VendorLibraryRefs.IsVendorRef(file))
                {
                    string want = comp.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.NameParameter)?.Expression?.Trim() ?? "";
                    if (VendorPartSource.Resolve(file, want, schDir, out string? refusal) is { } part)
                        Copies.Add(new ExportCopy(ExportCopyKind.VendorSpicePart, part.FilePath, part.LibraryName, part.Entry.Part));
                    else refusals.Add($"{name}: {refusal}");
                }
                else CheckFileParameters(comp, schDir, name, refusals);

                if (comp.CellRef is not { Length: > 0 } cellRef) continue;
                if (VendorModelRefs.IsRef(cellRef))
                {
                    if (VendorModelRefs.Resolve(cellRef, schDir, model.HostWorkspaceRoot, out string? refusal) is not { } m)
                    { refusals.Add($"{name}: {refusal}"); continue; }
                    AddSubCell(m.ModelDir, new ExportCopy(ExportCopyKind.VendorModel, m.ModelDir, m.Library, m.Entry.Part), refusals);
                    continue;
                }
                if (cellRef.Contains("://", StringComparison.Ordinal) && !ExternalCellRef.IsExternalRef(cellRef)) continue;
                string? sub = ExternalCellRef.ResolveCellDir(cellRef, schDir, model.HostWorkspaceRoot, out _, out bool exists);
                if (sub is null || !exists) { refusals.Add($"{name} places the cell '{cellRef}', which does not resolve."); continue; }
                AddSubCell(sub, new ExportCopy(ExportCopyKind.SubCell, Path.GetFullPath(sub)), refusals);
            }

            // Every file the document references, through the archive's own walk.
            foreach (string f in DocumentFileRefs.Find(schPath, OwningRoot.Of(schDir)))
                if (!IsUnder(f, cellDir) && _files.Add(f)) Copies.Add(new ExportCopy(ExportCopyKind.File, f));
        }

        private void AddSubCell(string subDir, ExportCopy copy, List<string> refusals)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(subDir));
            if (Copies.Any(c => c.Kind is ExportCopyKind.SubCell or ExportCopyKind.VendorModel
                                && DocumentFileRefs.PathComparer.Equals(c.Source, full))) return;
            Copies.Add(copy with { Source = full });
            if (PrimaryPath(full, ViewType.Schematic) is { } subSch) Visit(subSch, full, top: false, refusals);
            else refusals.Add($"The sub-cell '{Path.GetFileName(full)}' has no primary schematic.");
            if (PrimaryPath(full, ViewType.Symbol) is { } subSym)
                foreach (string f in DocumentFileRefs.Find(subSym, OwningRoot.Of(Path.GetDirectoryName(subSym))))
                    if (!IsUnder(f, full) && _files.Add(f)) Copies.Add(new ExportCopy(ExportCopyKind.File, f));
        }
    }

    /// <summary>A file-valued parameter whose value names no file is a refusal, not a silently empty model.</summary>
    private static void CheckFileParameters(EditableComponent comp, string schDir, string name, List<string> refusals)
    {
        string reference = ComponentTypeRegistry.EngineReference(comp.Symbol);
        foreach (var p in comp.Parameters)
        {
            bool isFile = FileReferences.KindOf(reference, p.Name) == FileValueKind.Path
                          || comp.Symbol == SymbolKind.SpiceModel && p.Name == SpiceModelSymbolProvider.FileParameter;
            if (!isFile) continue;
            string v = (p.Expression ?? "").Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();
            if (v.Length == 0 || FileReferences.IsVirtual(v)) continue;
            // A VerilogA's bare word may name a variable the elaborator binds in scope.
            if (comp.Symbol == SymbolKind.VerilogA && Regex.IsMatch(v, @"^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            string? resolved = SnpPathPolicy.Resolve(v, OwningRoot.Of(schDir), schDir);
            if (resolved is null || !File.Exists(resolved))
                refusals.Add($"{name}: its {p.Name} '{v}' does not resolve to a file.");
        }
    }

    private static bool IsKitPart(EditableComponent comp)
        => PdkKitRegistry.IsKitRef(comp.CellRef)
           || comp.Parameters.Any(p => (p.Expression ?? "").Contains(PdkKitRegistry.Scheme, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every component whose substrate is injected from the technology's stackup (brief 11 converts them).</summary>
    public static bool IsTechnologyBound(SymbolKind kind)
        => PlanarLineSubstrateInjection.IsStackupLineKind(kind)
           || kind is SymbolKind.Via or SymbolKind.ViaGnd or SymbolKind.TestPad or SymbolKind.MimCap or SymbolKind.Spiral
                   or SymbolKind.OctSpiral or SymbolKind.Tfr or SymbolKind.Airbridge;

    /// <summary>The engine references of nonlinear components (R-vl10-3c): Verilog-A, ComposedFET, the FET, BJT and
    /// MOS families, diodes, SDD and external devices.</summary>
    public static bool IsNonlinearReference(string reference)
    {
        string r = reference.ToUpperInvariant();
        return r is "VERILOGA" or "SDD" or "DIODE" or "EXTDEVICE" or "NONLINEARC" or "COMPOSEDFET"
               || r.StartsWith("FET_", StringComparison.Ordinal) || r.StartsWith("PFET_", StringComparison.Ordinal)
               || r.StartsWith("BJT_", StringComparison.Ordinal) || r.StartsWith("MOS", StringComparison.Ordinal)
               || r.StartsWith("VDMOS_", StringComparison.Ordinal) || r.StartsWith("IGBT_", StringComparison.Ordinal)
               || r.StartsWith("JFET_", StringComparison.Ordinal);
    }

    // ── The copy (Export's half) ─────────────────────────────────────────────────────────────────

    private sealed record Built(IReadOnlyList<string> Warnings);

    /// <summary>
    /// R-vl10-2c: the cell, its sub-cells, every file they reference and every vendor part, copied into
    /// <paramref name="modelDir"/>, with each reference rewritten to where its copy now lives.
    /// </summary>
    private static Built Build(ExportPlan plan, ExportOptions options, string modelDir)
    {
        var warnings = new List<string>();
        var files = new FileCopier(modelDir);

        // Sub-cell folders, named by their leaf; a second cell of the same name takes a numeric suffix.
        var cellMap = new Dictionary<string, string>(DocumentFileRefs.PathComparer) { [plan.CellDir] = modelDir };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in plan.Copies.Where(c => c.Kind is ExportCopyKind.SubCell or ExportCopyKind.VendorModel))
        {
            string leaf = Path.GetFileName(c.Source);
            string name = leaf;
            for (int n = 2; !used.Add(name); n++) name = $"{leaf}_{n}";
            cellMap[c.Source] = Path.Combine(modelDir, ModelFolder.CellsFolder, name);
        }

        foreach (var (src, dst) in cellMap)
        {
            bool top = DocumentFileRefs.PathComparer.Equals(src, plan.CellDir);
            string name = top ? plan.PartNumber : Path.GetFileName(dst);
            CopyCell(src, dst, name, top, plan, options, cellMap, files, warnings);
        }

        foreach (string doc in options.Documents)
        {
            string docs = Path.Combine(modelDir, ModelFolder.DocsFolder);
            Directory.CreateDirectory(docs);
            File.Copy(doc, Unique(Path.Combine(docs, Path.GetFileName(doc))));
        }
        return new Built(warnings);
    }

    private static void CopyCell(string src, string dst, string name, bool top, ExportPlan plan, ExportOptions options,
                                 Dictionary<string, string> cellMap, FileCopier files, List<string> warnings)
    {
        Directory.CreateDirectory(dst);
        string? srcSch = PrimaryPath(src, ViewType.Schematic);
        string? srcSym = PrimaryPath(src, ViewType.Symbol);

        var ccell = File.Exists(Path.Combine(src, CellFolder.CcellFileName))
            ? CellPersistence.LoadFromFile(Path.Combine(src, CellFolder.CcellFileName)) : new CcellFile();
        ccell.PrimaryLayout = null;
        ccell.Primary3D = null;
        ccell.PrimarySchematic = srcSch is null ? null : name + CellFolder.ViewExtension(ViewType.Schematic);
        ccell.IsTestBench = false;

        // The schematic: file references through the archive's walk, then the cell and vendor references.
        if (srcSch is not null)
        {
            string dstSch = Path.Combine(CellFolder.SubFolderPath(dst, ViewType.Schematic), name + CellFolder.ViewExtension(ViewType.Schematic));
            Directory.CreateDirectory(Path.GetDirectoryName(dstSch)!);
            string text = RewriteFiles(srcSch, dstSch, files, cellMap);
            text = RewriteSchematic(text, srcSch, dstSch, name, files, cellMap, options);
            AtomicFile.WriteAllText(dstSch, text);
        }

        // The symbol: the top cell's as chosen; a sub-cell keeps its own, and a vendor model drawn by its glyph gets
        // that glyph written as its own so it draws the same placed locally.
        string symDir = CellFolder.SubFolderPath(dst, ViewType.Symbol);
        string dstSym = Path.Combine(symDir, name + CellFolder.ViewExtension(ViewType.Symbol));
        SymbolModel? written = null;
        if (top)
        {
            string kind = options.Kind ?? plan.DefaultKind;
            string symbol = options.Symbol ?? DefaultSymbolFor(plan.HasOwnSymbol, plan.Pins, kind,
                                                               kind == ModelPartManifest.KindPassive ? options.Value : "");
            if (symbol == SymbolOwn && srcSym is not null)
            {
                Directory.CreateDirectory(symDir);
                AtomicFile.WriteAllText(dstSym, RewriteFiles(srcSym, dstSym, files, cellMap));
            }
            else written = symbol == SymbolBox ? AutoSymbolGenerator.Generate(name, plan.Pins.Count)
                                               : VendorModelSymbolProvider.GlyphSymbol(symbol, plan.Pins);
            if (written is not null) NamePins(written, plan.Pins);
        }
        else if (srcSym is not null)
        {
            Directory.CreateDirectory(symDir);
            AtomicFile.WriteAllText(dstSym, RewriteFiles(srcSym, dstSym, files, cellMap));
        }
        else if (ModelFolder.Is(src) && ModelPartManifest.Read(Path.Combine(src, ModelFolder.PartFileName)).Manifest is { Glyph.Length: > 0 } m)
        {
            written = VendorModelSymbolProvider.GlyphSymbol(m.Glyph, m.Pins);
            NamePins(written, m.Pins);
        }
        if (written is not null)
        {
            Directory.CreateDirectory(symDir);
            SymbolPersistence.SaveToFile(dstSym, written);
        }
        ccell.PrimarySymbol = File.Exists(dstSym) ? Path.GetFileName(dstSym) : null;
        CellPersistence.SaveToFile(Path.Combine(dst, CellFolder.CcellFileName), ccell);
    }

    /// <summary>A built-in glyph's pins take the cell's pin names, in port order, so the model's symbol reads as its
    /// pins do in any circuitRF without a rule being re-applied.</summary>
    private static void NamePins(SymbolModel symbol, IReadOnlyList<string> pins)
    {
        foreach (var pin in symbol.Pins)
            if (pin.PortIndex >= 0 && pin.PortIndex < pins.Count) pin.Name = pins[pin.PortIndex];
    }

    /// <summary>The document's text with every file it references copied in and re-pointed — through
    /// <see cref="DocumentFileRefs"/>, the walk the archive repoints with.</summary>
    private static string RewriteFiles(string srcDoc, string dstDoc, FileCopier files, Dictionary<string, string> cellMap)
    {
        string srcDir = Path.GetDirectoryName(Path.GetFullPath(srcDoc))!;
        string dstDir = Path.GetDirectoryName(Path.GetFullPath(dstDoc))!;
        string? rewritten = DocumentFileRefs.Rewrite(srcDoc, OwningRoot.Of(srcDir), (abs, refBase) =>
        {
            // A file inside a cell being copied is where that cell's copy puts it, not a model file.
            string target = MappedIntoCell(abs, cellMap) ?? files.Copy(abs);
            return refBase == RefBase.Document
                ? Path.GetRelativePath(dstDir, target).Replace('\\', '/')
                : Path.GetRelativePath(files.ModelDir, target).Replace('\\', '/');
        });
        return rewritten ?? DocumentFileRefs.ReadText(srcDoc);
    }

    private static string? MappedIntoCell(string abs, Dictionary<string, string> cellMap)
    {
        foreach (var (src, dst) in cellMap)
            if (IsUnder(abs, src))
            {
                string rel = Path.GetRelativePath(src, abs);
                // The primary views are renamed after the cell; anything else keeps its place.
                string dir = Path.GetDirectoryName(rel) ?? "";
                string? folderName = Path.GetFileName(dst);
                foreach (var t in (ReadOnlySpan<ViewType>)[ViewType.Schematic, ViewType.Symbol])
                    if (DocumentFileRefs.PathComparer.Equals(Path.GetFullPath(abs), PrimaryPath(src, t) ?? ""))
                        return Path.Combine(dst, CellFolder.SubFolderName(t), folderName + CellFolder.ViewExtension(t));
                return Path.Combine(dst, dir, Path.GetFileName(rel));
            }
        return null;
    }

    /// <summary>
    /// The schematic's own references that are not files: a placed cell re-pointed at its copy under <c>cells/</c>
    /// (a vendor model's stored glyph dropped with its <c>vendormodel://</c> reference, since the copy draws its own
    /// symbol), a vendor SPICE part's <c>File</c> re-pointed at its <c>.subckt</c> block copied into <c>models/</c>, and
    /// the document's cell name.
    /// </summary>
    private static string RewriteSchematic(string text, string srcSch, string dstSch, string name, FileCopier files,
                                           Dictionary<string, string> cellMap, ExportOptions options)
    {
        // A document that is not a JSON object has no components to re-point; the extraction says what is wrong with it.
        if (JsonNode.Parse(text) is not JsonObject root) return text;
        string srcDir = Path.GetDirectoryName(Path.GetFullPath(srcSch))!;
        string dstDir = Path.GetDirectoryName(Path.GetFullPath(dstSch))!;
        root["CellName"] = name;

        if (root["Components"] is not JsonArray comps) return root.ToJsonString(JsonOptions);
        foreach (var node in comps.OfType<JsonObject>())
        {
            var parameters = node["Parameters"] as JsonArray;
            if (node["CellRef"]?.GetValue<string>() is { Length: > 0 } cellRef)
            {
                string? src = null;
                if (VendorModelRefs.IsRef(cellRef))
                {
                    src = VendorModelRefs.Resolve(cellRef, srcDir, null, out _)?.ModelDir;
                    if (parameters is not null)
                        foreach (var p in parameters.OfType<JsonObject>().Where(p => p["Name"]?.GetValue<string>() == VendorModelRefs.GlyphParameter).ToList())
                            parameters.Remove(p);
                }
                else if (!cellRef.Contains("://", StringComparison.Ordinal) || ExternalCellRef.IsExternalRef(cellRef))
                    src = ExternalCellRef.ResolveCellDir(cellRef, srcDir);
                if (src is not null && cellMap.TryGetValue(Path.TrimEndingDirectorySeparator(Path.GetFullPath(src)), out string? dst))
                {
                    node["CellRef"] = Path.GetRelativePath(dstDir, dst).Replace('\\', '/');
                    node.Remove("CellInterfaceHash");
                }
            }

            if (parameters is null) continue;
            var fileParam = parameters.OfType<JsonObject>().FirstOrDefault(p => p["Name"]?.GetValue<string>() == SpiceModelSymbolProvider.FileParameter);
            string? file = fileParam is null ? null : Expression(fileParam);
            if (fileParam is null || !VendorLibraryRefs.IsVendorRef(file)) continue;
            if (!options.IncludeVendorParts) continue;
            string want = parameters.OfType<JsonObject>().FirstOrDefault(p => p["Name"]?.GetValue<string>() == SpiceModelSymbolProvider.NameParameter) is { } np
                ? Expression(np) ?? "" : "";
            if (VendorPartSource.Resolve(file, want, srcDir, out _) is not { } part) continue;
            string copied = files.CopyText(part.Entry.Part + ".lib", VendorLibraryIndex.ReadBlock(part.LibraryRoot, part.Entry),
                                           key: part.FilePath + "|" + part.Entry.Part);
            fileParam.Remove("Value");
            fileParam["Expression"] = Path.GetRelativePath(files.ModelDir, copied).Replace('\\', '/');
        }
        return root.ToJsonString(JsonOptions);
    }

    private static string? Expression(JsonObject p)
        => p["Expression"]?.GetValue<string>() ?? (p["Value"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Copies referenced files into <c>models/</c>, once each: a name already taken by another file gets a numeric
    /// suffix and the reference follows it; a <c>.va</c> brings every file it <c>`include</c>s, keeping their
    /// relative layout so the include still resolves.
    /// </summary>
    private sealed partial class FileCopier(string modelDir)
    {
        public string ModelDir { get; } = modelDir;
        private readonly Dictionary<string, string> _copied = new(DocumentFileRefs.PathComparer);

        public string Copy(string abs)
        {
            string full = Path.GetFullPath(abs);
            if (_copied.TryGetValue(full, out string? done)) return done;
            string models = Path.Combine(ModelDir, ModelFolder.ModelsFolder);
            Directory.CreateDirectory(models);

            var includes = IsVerilogA(full) ? Includes(full) : [];
            if (includes.Count == 0)
            {
                string dst = Unique(Path.Combine(models, Path.GetFileName(full)));
                File.Copy(full, dst);
                return _copied[full] = dst;
            }

            // The .va and what it includes keep their layout under one folder of their own, rooted at the deepest
            // folder that holds them all.
            string root = Path.GetDirectoryName(full)!;
            foreach (string inc in includes)
                while (!IsUnder(inc, root) && Path.GetDirectoryName(root) is { } up) root = up;
            string folder = Unique(Path.Combine(models, Path.GetFileNameWithoutExtension(full)));
            foreach (string f in includes.Prepend(full))
            {
                string dst = Path.Combine(folder, Path.GetRelativePath(root, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst)) File.Copy(f, dst);
            }
            return _copied[full] = Path.Combine(folder, Path.GetRelativePath(root, full));
        }

        /// <summary>Text written into <c>models/</c> under <paramref name="fileName"/> (made unique), once per
        /// <paramref name="key"/>.</summary>
        public string CopyText(string fileName, string text, string key)
        {
            if (_copied.TryGetValue(key, out string? done)) return done;
            string models = Path.Combine(ModelDir, ModelFolder.ModelsFolder);
            Directory.CreateDirectory(models);
            string dst = Unique(Path.Combine(models, fileName));
            File.WriteAllText(dst, text.EndsWith('\n') ? text : text + "\n");
            return _copied[key] = dst;
        }

        private static bool IsVerilogA(string path)
            => Path.GetExtension(path).ToLowerInvariant() is ".va" or ".vams";

        [GeneratedRegex(@"^\s*`include\s+""([^""]+)""", RegexOptions.Multiline)]
        private static partial Regex IncludeLine();

        /// <summary>Every file <paramref name="va"/> includes, transitively, that resolves beside the file including
        /// it. A standard header the compiler supplies (<c>disciplines.vams</c>) resolves nowhere here and is left to
        /// the compiler, as it is when the source is compiled in place.</summary>
        public static List<string> Includes(string va)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(DocumentFileRefs.PathComparer) { Path.GetFullPath(va) };
            var queue = new Queue<string>([Path.GetFullPath(va)]);
            while (queue.TryDequeue(out string? file))
            {
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                foreach (Match m in IncludeLine().Matches(text))
                {
                    string inc = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, CircuitRF.Core.RefPath.ToNative(m.Groups[1].Value)));
                    if (File.Exists(inc) && seen.Add(inc)) { found.Add(inc); queue.Enqueue(inc); }
                }
            }
            return found;
        }
    }

    /// <summary><paramref name="path"/>, or the first <c>name_2.ext</c>, <c>name_3.ext</c>… that names nothing.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            string p = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    // ── The self-check (R-vl10-2f) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves the copy changed nothing: the source cell and the exported model, each with a 50 Ω port from every pin
    /// to ground at its default parameters, give the same S-parameters at eleven frequencies and the same DC operating
    /// point at every pin, to <see cref="SelfCheckTolerance"/> relative per entry. Null when they agree; otherwise the
    /// refusal naming the worst entry. Equality is the claim, not physics, so an unbiased transistor is a valid subject.
    /// </summary>
    public static string? SelfCheck(string sourceCellDir, string modelDir, ModelPartManifest manifest, RunControl? control = null)
    {
        double[] freqs = SelfCheckFrequencies(manifest.Frequency);

        string? srcText = ModelNetlist.FromCell(sourceCellDir, out string? why);
        if (srcText is null) return $"The self-check could not extract the source cell: {why}";
        string srcSch = PrimaryPath(sourceCellDir, ViewType.Schematic) ?? sourceCellDir;
        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(Path.GetDirectoryName(srcSch));

        string modelText;
        try { modelText = File.ReadAllText(ModelNetlist.PathOf(modelDir)); }
        catch (Exception ex) { return $"The self-check could not read the exported netlist: {ex.Message}"; }

        Run a, b;
        try
        {
            a = RunBench(srcText, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceCellDir))), srcBase, manifest.Pins.Count, freqs);
            control?.TickStage();
            b = RunBench(modelText, manifest.Cell, modelDir, manifest.Pins.Count, freqs);
            control?.TickStage();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"The self-check could not run: {ex.Message}";
        }

        (double Error, string Where) worst = (0, "");
        for (int i = 0; i < a.S.Length && i < b.S.Length; i++)
        {
            double e = Relative(a.S[i], b.S[i]);
            if (e > worst.Error)
            {
                int n = manifest.Pins.Count, f = i / (n * n), ij = i % (n * n);
                worst = (e, $"S({ij / n + 1},{ij % n + 1}) at {FormatHz(freqs[f])}");
            }
        }
        for (int k = 0; k < a.Dc.Length && k < b.Dc.Length; k++)
        {
            double e = Relative(a.Dc[k], b.Dc[k]);
            if (e > worst.Error) worst = (e, $"the DC voltage at pin {manifest.Pins[k]}");
        }
        if (a.S.Length != b.S.Length) return "The self-check failed: the source and the model give different numbers of S-parameters.";
        if (worst.Error <= SelfCheckTolerance) return null;
        return $"The self-check failed: the exported model differs from the source cell, worst at {worst.Where} " +
               $"(relative difference {worst.Error:G3}, allowed {SelfCheckTolerance:G1}). Nothing was written.";
    }

    private sealed record Run(Complex[] S, double[] Dc);

    private static Run RunBench(string cnlText, string topCell, string? baseDir, int pins, double[] freqs)
    {
        var bench = new System.Text.StringBuilder(cnlText);
        bench.Append('\n');
        for (int k = 1; k <= pins; k++)
            bench.Append($"Port:SCP{k} __sc_n{k} 0 Num={k} Z=50 Ohm\n");
        bench.Append(topCell).Append(":SCX1");
        for (int k = 1; k <= pins; k++) bench.Append($" __sc_n{k}");
        bench.Append('\n');

        var (lib, tb) = new CnlReader().Read(bench.ToString(), "selfcheck", baseDir);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var s = SParameterEngine.Run(nl, freqs).Cubes["S"].ComplexValues;
        var dc = NonlinearDcEngine.Run(nl);
        var v = new double[pins];
        for (int k = 1; k <= pins; k++)
            v[k - 1] = nl.Nodes.TryGetIndex($"__sc_n{k}", out int idx) && idx > 0 && idx - 1 < dc.NodeVoltages.Length
                ? dc.NodeVoltages[idx - 1] : 0;
        return new Run([.. s], v);
    }

    /// <summary>DC, then ten points log-spaced over the stated range — 1 MHz to 10 GHz when none is stated, and a
    /// stated low of 0 Hz starts the log at a ten-thousandth of the high.</summary>
    public static double[] SelfCheckFrequencies(ModelPartFrequency? range)
    {
        double? lo = range is null ? null : VendorLibraryIndex.HzOf(range.Low);
        double? hi = range is null ? null : VendorLibraryIndex.HzOf(range.High);
        double high = hi is > 0 ? hi.Value : 10e9;
        double low = lo is > 0 && lo < high ? lo.Value : (hi is > 0 ? high / 1e4 : 1e6);
        var f = new double[11];
        for (int i = 0; i < 10; i++) f[i + 1] = low * Math.Pow(high / low, i / 9.0);
        return f;
    }

    private static double Relative(Complex a, Complex b)
        => (a - b).Magnitude / Math.Max(Math.Max(a.Magnitude, b.Magnitude), SelfCheckFloor);

    private static double Relative(double a, double b)
        => Math.Abs(a - b) / Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), SelfCheckFloor);

    private static string FormatHz(double f) => f == 0 ? "DC" : f >= 1e9 ? $"{f / 1e9:G4} GHz" : f >= 1e6 ? $"{f / 1e6:G4} MHz" : $"{f / 1e3:G4} kHz";

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A default as the <c>.cnl</c>'s <c>parameters</c> line spells it — <see cref="ModelFolderCheck"/>'s rule.</summary>
    private static string Spell(string expr, string? unit)
        => string.Join(' ', ((expr ?? "") + (string.IsNullOrEmpty(unit) ? "" : " " + unit))
                              .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The cell's primary view file, or null.</summary>
    public static string? PrimaryPath(string cellDir, ViewType type)
    {
        try
        {
            var p = CellFolder.ResolvePrimary(cellDir, type);
            return p.State is PrimaryState.SoleFile or PrimaryState.NamedPresent && p.ResolvedName is { } n
                ? Path.Combine(CellFolder.SubFolderPath(cellDir, type), n) : null;
        }
        catch { return null; }
    }

    private static string NormaliseCategory(string category)
        => string.Join('/', (category ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string RefPathNative(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    private static bool IsUnder(string path, string dir)
    {
        string p = Path.GetFullPath(path), d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return p.StartsWith(d + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrUnder(string path, string dir)
        => DocumentFileRefs.PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                                                Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)))
           || IsUnder(path, dir);
}
