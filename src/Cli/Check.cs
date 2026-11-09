using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Assembly;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Interchange.Gdstk;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf check &lt;path&gt; [--recursive] [--severity warning|error]</c> — is this design well
/// formed, does it resolve, and is it sound? (brief-automation-4-check-and-explain.md §2.)
///
/// <para><b>This file writes no validation logic and that is the whole design.</b> R-aut4-2: the
/// repo is full of validators and they are scattered rather than missing, so what is new here is the
/// WALK and the reporting — which validator gets pointed at which file, and how its finding becomes
/// a <see cref="Diagnostic"/> with a stable id and the producing validator's own typed values. A
/// rule that existed only in <c>check</c> would be a rule the GUI does not enforce, which is worse
/// than no rule at all: a design would pass here and be refused when someone opened it.</para>
///
/// <para><b>It runs no analysis</b> (R-aut4-1). Elaboration is as far as it goes — that is what
/// answers "do the parameters, expressions and cycles resolve?" — and nothing here solves a matrix.
/// A check that needed a solve to answer belongs in the run verbs' own warnings.</para>
///
/// <para><b>It never writes</b> (R-aut4-6). Not a repair, not a re-save, not a cache file: a caller
/// must be able to run it on a read-only tree and on a workspace another process has open. The two
/// places that would otherwise be tempting are the technology cache (in memory, per invocation) and
/// the DRC waiver store (read from the <c>.clay</c>, never written back).</para>
/// </summary>
internal static class Check
{
    /// <summary>The severity at which a finding decides the exit code (R-aut4-5).</summary>
    private enum Threshold { Warning, Error }

    private sealed class Tally
    {
        public int Errors, Warnings, Notes;

        public void Count(DiagnosticSeverity s)
        {
            switch (s)
            {
                case DiagnosticSeverity.Error:   Errors++;   break;
                case DiagnosticSeverity.Warning: Warnings++; break;
                default:                         Notes++;    break;
            }
        }
    }

    /// <summary>
    /// The one place a finding is recorded. Everything goes to stderr — <c>check</c>'s findings are
    /// not a RESULT, they are what the tool has to say about a design (<c>cli.md</c> §3.1), and
    /// stdout carries the summary the way every other verb's stdout carries its table.
    /// </summary>
    private sealed class Findings
    {
        public readonly Tally Total = new();
        private readonly List<CheckedDocumentJson> _documents = [];
        private Tally _current = new();
        private string _currentPath = "";
        private DocumentKind _currentKind = DocumentKind.Unknown;
        private bool _open;

        public IReadOnlyList<CheckedDocumentJson> Documents => _documents;
        public int Count => _documents.Count;

        public void Begin(string path, DocumentKind kind)
        {
            End();
            _currentPath = path;
            _currentKind = kind;
            _current     = new Tally();
            _open        = true;
        }

        private CheckedEmJson? _currentEm;

        /// <summary>The current document's mesh budget, recorded on its result row.</summary>
        public void SetEm(CheckedEmJson em) { if (_open) _currentEm = em; }

        public void End()
        {
            if (!_open) return;
            _documents.Add(new CheckedDocumentJson(
                _currentPath, DocumentKinds.Name(_currentKind), _current.Errors, _current.Warnings,
                _currentEm));
            _currentEm = null;
            _open = false;
        }

        public void Add(Diagnostic d)
        {
            Total.Count(d.Severity);
            if (_open) _current.Count(d.Severity);

            Console.Error.WriteLine(d.Severity switch
            {
                DiagnosticSeverity.Error   => "error: "   + d.Render(),
                DiagnosticSeverity.Warning => "warning: " + d.Render(),
                _                          => "note: "    + d.Render(),
            });
            JsonRun.Note(d);
        }
    }

    public static int Run(string[] args)
    {
        string? path = null;
        bool recursive = false;
        var threshold = Threshold.Error;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--recursive" or "-r":
                    recursive = true;
                    continue;
                case "--severity" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "warning": threshold = Threshold.Warning; break;
                        case "error":   threshold = Threshold.Error;   break;
                        default: return JsonRun.Fail(CliDiagnostics.CheckUnknownSeverity(args[i]));
                    }
                    continue;
                default:
                    if (args[i].StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.CheckUnknownOption(args[i])); return Usage(); }
                    if (path is not null)
                    { JsonRun.Report(CliDiagnostics.CheckMultiplePaths()); return Usage(); }
                    path = args[i];
                    continue;
            }
        }

        if (path is null) { JsonRun.Report(CliDiagnostics.CheckPathRequired()); return Usage(); }
        JsonRun.InputPath = path;

        if (!File.Exists(path) && !Directory.Exists(path))
            return JsonRun.Fail(CliDiagnostics.CheckPathNotFound(path));

        // R-lvs1-4d: a bare `.ccell` is a CELL — the file IS the cell's own declaration, and pointing
        // at it means the folder it declares. Answered here rather than in `DocumentKinds.Classify`,
        // because every other verb that classifies a path (`render`, `explain`, `find`) takes a cell
        // as a DIRECTORY and would be handed a file.
        if (File.Exists(path)
            && string.Equals(Path.GetFileName(path), CellFolder.CcellFileName, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } owningCell)
            path = owningCell;

        var findings = new Findings();
        var cache    = new TechnologyCache();

        CheckPath(path, DocumentKinds.Classify(path), findings, cache, recursive);
        findings.End();

        int exit = threshold == Threshold.Error
            ? (findings.Total.Errors > 0 ? 1 : 0)
            : (findings.Total.Errors > 0 || findings.Total.Warnings > 0 ? 1 : 0);

        JsonRun.Check = new CheckReportJson(
            path,
            threshold == Threshold.Error ? "error" : "warning",
            findings.Count,
            findings.Total.Errors, findings.Total.Warnings, findings.Total.Notes,
            findings.Documents);

        Console.WriteLine(
            $"{findings.Count} document(s) checked: " +
            $"{findings.Total.Errors} error(s), {findings.Total.Warnings} warning(s), " +
            $"{findings.Total.Notes} note(s).");

        return exit;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: circuitrf check <path> [--recursive] [--severity warning|error]");
        return 1;
    }

    // ── the walk ─────────────────────────────────────────────────────────────

    private static void CheckPath(
        string path, DocumentKind kind, Findings f, TechnologyCache cache, bool recursive)
    {
        switch (kind)
        {
            case DocumentKind.Workspace:  CheckWorkspace(path, f, cache); break;
            case DocumentKind.Cell when ModelFolder.Is(path):
                                          CheckModelFolder(path, f, cache); break;
            case DocumentKind.Cell:       CheckCell(path, f, cache);      break;
            case DocumentKind.Folder:     CheckFolder(path, f, cache, recursive); break;
            case DocumentKind.ModelPart:  Scoped(path, kind, f, () => CheckModelPart(path, f)); break;
            case DocumentKind.ModelLibraryManifest:
                                          Scoped(path, kind, f, () => CheckModelLibraryManifest(path, f)); break;

            case DocumentKind.Schematic:  Scoped(path, kind, f, () => CheckSchematic(path, f)); break;
            case DocumentKind.Symbol:     Scoped(path, kind, f, () => CheckViewFile(path, ViewType.Symbol, f)); break;
            case DocumentKind.Layout:     Scoped(path, kind, f, () => CheckLayout(path, f, cache)); break;
            case DocumentKind.ThreeD:     Scoped(path, kind, f, () => CheckThreeD(path, f, cache)); break;
            case DocumentKind.Technology: Scoped(path, kind, f, () => CheckTechnology(path, f)); break;
            case DocumentKind.MaterialLibrary:
                                          Scoped(path, kind, f, () => CheckMaterialLibrary(path, f)); break;
            case DocumentKind.EmSetup:    Scoped(path, kind, f, () => CheckEmSetup(path, f, cache)); break;
            case DocumentKind.Netlist:    Scoped(path, kind, f, () => CheckNetlist(path, f)); break;
            case DocumentKind.AssemblyRules:
                                          Scoped(path, kind, f, () => CheckAssemblyRules(path, f)); break;
            case DocumentKind.Touchstone: Scoped(path, kind, f, () => CheckTouchstone(path, f)); break;
            case DocumentKind.EmResult:   Scoped(path, kind, f, () => CheckEmResult(path, f)); break;
            case DocumentKind.DataDisplay:
                                          Scoped(path, kind, f, () => CheckDataDisplay(path, f)); break;
            case DocumentKind.Rail:       Scoped(path, kind, f, () => CheckRail(path, f)); break;
            case DocumentKind.Smith:      Scoped(path, kind, f, () => CheckSmith(path, f)); break;
            case DocumentKind.ComposerDocument:
                                          Scoped(path, kind, f, () => CheckComposerDocument(path, f)); break;
            case DocumentKind.ComposedModel:
                                          Scoped(path, kind, f, () => CheckComposedModel(path, f)); break;

            case DocumentKind.Foreign:
                f.Begin(path, kind);
                f.Add(CliDiagnostics.CheckForeignFile(path));
                break;

            case DocumentKind.Picture:    Scoped(path, kind, f, () => CheckPicture(path, f)); break;

            // brief-oasis-gdstk.md §10b — OASIS is the one interchange format read here, because it is binary and
            // compressed and its own extension check (CRC-32 or checksum) is the worker's to run.
            case DocumentKind.Interchange when LayoutConvert.DetectSource(path) == LayoutConvert.Fmt.Oasis:
                Scoped(path, kind, f, () => CheckOasis(path, f));
                break;

            case DocumentKind.Interchange:
                f.Begin(path, kind);
                f.Add(CliDiagnostics.CheckInterchangeNotADocument(
                    path, DocumentKinds.InterchangeFormat(path) ?? "foreign"));
                break;

            default:
                f.Begin(path, kind);
                f.Add(CliDiagnostics.CheckUnknownKind(path));
                break;
        }
    }

    private static void Scoped(string path, DocumentKind kind, Findings f, Action body)
    {
        f.Begin(path, kind);
        body();
        f.End();
    }

    /// <summary>
    /// A workspace checks everything under it (§2). <c>--recursive</c> is what a plain FOLDER needs;
    /// a workspace implies it, because "check my workspace" that stopped at the top level would be
    /// answering a question nobody asks.
    /// </summary>
    private static void CheckWorkspace(string path, Findings f, TechnologyCache cache)
    {
        string cwsPath = Directory.Exists(path) ? Path.Combine(path, DocumentKinds.CwsFileName) : path;
        string root    = Path.GetDirectoryName(Path.GetFullPath(cwsPath))!;

        f.Begin(cwsPath, DocumentKind.Workspace);

        CwsFile? cws = null;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(cwsPath, ex.Message)); }

        if (cws is not null)
        {
            // The three reference lists a `.cws` carries. Each is exactly what the project tree
            // already shows as a warning node when it does not resolve — same finding, no new rule.
            if (cws.DefaultTechRef is { Length: > 0 } techRef
                && !File.Exists(Resolve(root, techRef)))
                f.Add(CliDiagnostics.CheckWorkspaceRefUnresolved(cwsPath, "default technology", techRef));

            foreach (var lib in cws.LibraryRefs)
                if (!Directory.Exists(Resolve(root, lib)) && !File.Exists(Resolve(root, lib)))
                    f.Add(CliDiagnostics.CheckWorkspaceRefUnresolved(cwsPath, "library", lib));

            foreach (var known in cws.KnownFiles)
                if (!File.Exists(Resolve(root, known)) && !Directory.Exists(Resolve(root, known)))
                    f.Add(CliDiagnostics.CheckWorkspaceRefUnresolved(cwsPath, "known file", known));

            // Brief vendorlib-3 R-vl3-4c: every referenced vendor library through the one Validate the
            // Manage Libraries dialog runs — a library that is missing or unreadable as a whole, and a
            // placed part it no longer holds, are errors; unreadable files in it are a warning.
            if (cws.VendorLibraries is { Count: > 0 } vendorLibraries)
            {
                var placed = PlacedVendorParts(root);
                foreach (var library in vendorLibraries)
                {
                    var v = VendorLibraryRefs.Validate(root, library, placed);
                    foreach (var problem in v.Problems)
                        f.Add(v.PartsIndexed < 0
                            ? CliDiagnostics.CheckVendorLibraryMissing(cwsPath, library.Name, problem)
                            : CliDiagnostics.CheckVendorPartMissing(cwsPath, library.Name, problem));
                    foreach (var note in v.Notes)
                        f.Add(note == v.UnreadableNote
                            ? CliDiagnostics.CheckVendorLibraryUnreadable(cwsPath, library.Name, note)
                            : CliDiagnostics.CheckVendorLibraryNote(cwsPath, library.Name, note));
                }
            }
        }

        f.End();
        WalkFolder(root, f, cache, recursive: true);

        static string Resolve(string root, string reference) =>
            Path.IsPathRooted(reference) ? reference : CircuitRF.Core.RefPath.Resolve(root, reference);
    }

    /// <summary>
    /// The File/Name of every vendor part placed in a schematic of the workspace at
    /// <paramref name="root"/> — the set <see cref="VendorLibraryRefs.Validate"/> checks. Hidden folders
    /// and <c>results/</c> are not walked; an unreadable schematic contributes nothing here, because
    /// its own check reports it.
    /// </summary>
    private static List<(string File, string Name)> PlacedVendorParts(string root)
    {
        var placed = new HashSet<(string, string)>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*" + CellFolder.ViewExtension(ViewType.Schematic), SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        foreach (string csch in files)
        {
            string rel = Path.GetRelativePath(root, csch).Replace('\\', '/');
            if (rel.Split('/').Any(seg => seg.StartsWith('.')) || rel.StartsWith("results/", StringComparison.Ordinal)) continue;
            try
            {
                var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
                foreach (var c in model.Components)
                {
                    // A placed library model is its reference, with no part name of its own (brief vendorlib-9).
                    if (c.CellRef is { } cellRef && CircuitRF.Design.VendorLibraries.ModelLibrary.VendorModelRefs.IsRef(cellRef))
                    {
                        placed.Add((CircuitRF.Design.VendorLibraries.ModelLibrary.VendorModelRefs.Bare(cellRef), ""));
                        continue;
                    }
                    if (c.Symbol != SymbolKind.SpiceModel) continue;
                    string file = (c.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.Expression ?? "").Trim();
                    if (!VendorLibraryRefs.IsVendorRef(file)) continue;
                    placed.Add((file, (c.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.NameParameter)?.Expression ?? "").Trim()));
                }
            }
            catch { /* reported by the schematic's own check */ }
        }
        return [.. placed];
    }

    /// <summary>
    /// A folder, and — when it holds model folders — a model library (brief vendorlib-6 R-vl6-2c): every
    /// model folder through the walk, then what the library must hold together. A folder holding a
    /// model folder anywhere below it is walked whole without <c>--recursive</c>, because a library's
    /// categories are sub-folders and needs no <c>library.clib</c> to be one — a `check` of a library
    /// that stopped at its top level would check nothing and report a clean run. Only the folder that
    /// was NAMED is looked at this way; finding out walks directories and opens no file.
    /// </summary>
    private static void CheckFolder(string path, Findings f, TechnologyCache cache, bool recursive)
    {
        bool library = ModelFolder.AnyUnder(path);
        WalkFolder(path, f, cache, recursive || library);
        if (!library) return;
        // Brief vendorlib-15 §5c: the signature, judged against this user's own trust store, read-only.
        var findings = ModelLibraryCheck.Validate(path)
            .Concat(LibrarySignature.Findings(path, PublisherTrust.FromRecordedPreferences())).ToList();
        if (findings.Count == 0) return;
        f.Begin(path, DocumentKind.Folder);
        foreach (var d in findings) f.Add(d);
        f.End();
    }

    private static void WalkFolder(string dir, Findings f, TechnologyCache cache, bool recursive)
    {
        foreach (var file in Enumerate(() => Directory.EnumerateFiles(dir), f, dir).Order(StringComparer.Ordinal))
        {
            // By NAME only: a walk must not open every foreign file to discover it is not Gerber.
            // The content sniff is right when a caller named the path, and it is what the
            // Interchange arm below relies on — but it is answered there, not here.
            var kind = DocumentKinds.Classify(file, contentSniff: false);
            // A folder walk reports what it RECOGNISES. Anything else is somebody's `.md`, `.s2p` or
            // `.DS_Store`, and a walk that called each of those an unknown kind would drown its own
            // findings — which is exactly the failure mode that makes a check stop being run.
            // Touchstone joins the skip list for the reason the comment above gives, sharpened: a
            // kit directory holds hundreds of `.sNp` files, and measuring passivity and causality
            // across all of them would bury a workspace's real findings under data-file notes about
            // parts the user did not author. A file is checked when it is NAMED.
            // A picture likewise (brief-img-2 R-im2-6): a workspace's screenshots and figures are not documents, and
            // classifying each would decode every one of them. `find` lists them with their kinds.
            // A result record too (brief-cosim-pattern-1): results are not kept in history, so a fresh clone holds records whose
            // .npy is not there, and that is not a finding about the design.
            if (kind is DocumentKind.Unknown or DocumentKind.Interchange or DocumentKind.Workspace
                     or DocumentKind.Touchstone or DocumentKind.Foreign or DocumentKind.Picture or DocumentKind.EmResult) continue;
            CheckPath(file, kind, f, cache, recursive);
        }

        foreach (var sub in Enumerate(() => Directory.EnumerateDirectories(dir), f, dir).Order(StringComparer.Ordinal))
        {
            // A workspace's results hold each 3D run's copy of its view (a run's document.c3d), whose relative references mean
            // nothing from there, and a hidden folder is a cache or a history. Neither is a design: both are walked only when named.
            string subName = Path.GetFileName(sub);
            if (subName.StartsWith('.') || subName.Equals("results", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, ".cws"))) continue;

            var kind = DocumentKinds.Classify(sub);
            if (kind == DocumentKind.Cell) { CheckPath(sub, kind, f, cache, recursive: false); continue; }

            // A nested workspace is somebody else's tree. It is walked when it is what was asked for,
            // never dragged in by an enclosing folder — the two have different default technologies
            // and a finding attributed to the wrong one is worse than no finding.
            if (kind == DocumentKind.Workspace) continue;
            if (recursive) WalkFolder(sub, f, cache, recursive);
        }
    }

    /// <summary>An unreadable directory is a finding, not a crash — a check pointed at a tree it
    /// cannot fully read must still report what it did read.</summary>
    private static IEnumerable<string> Enumerate(Func<IEnumerable<string>> get, Findings f, string dir)
    {
        try { return [.. get()]; }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(dir, ex.Message)); return []; }
    }

    // ── one cell folder ──────────────────────────────────────────────────────

    private static void CheckCell(string cellDir, Findings f, TechnologyCache cache)
    {
        f.Begin(cellDir, DocumentKind.Cell);

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir)));
        if (NameValidator.Validate(name) is { } why)
            f.Add(CliDiagnostics.CheckInvalidName(cellDir, "cell", name, why));

        foreach (var view in DocumentKinds.AllViewTypes)
        {
            PrimaryResolution primary;
            try { primary = CellFolder.ResolvePrimary(cellDir, view); }
            catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(cellDir, ex.Message)); continue; }

            switch (primary.State)
            {
                case PrimaryState.MissingNamedPrimary:
                    f.Add(CliDiagnostics.CheckPrimaryMissing(
                        cellDir, CellFolder.SubFolderName(view), primary.MissingName ?? "?"));
                    break;
                case PrimaryState.NoPrimary:
                    f.Add(CliDiagnostics.CheckNoPrimary(
                        cellDir, CellFolder.SubFolderName(view), CountViews(cellDir, view)));
                    break;
            }
        }

        // brief-sdl-10 D1 — an EM component says which kind it is: what its geometry follows.
        if (EmComponentKinds.Of(cellDir) is { Kind: not EmComponentKind.None } emKind)
            f.Add(CliDiagnostics.CheckEmKind(cellDir, emKind.Sentence));

        CheckTerminals(cellDir, f);

        f.End();

        // Then every view file in the cell, each in its own scope so the report says which file a
        // finding belongs to.
        foreach (var view in DocumentKinds.AllViewTypes)
        {
            string sub = CellFolder.SubFolderPath(cellDir, view);
            if (!Directory.Exists(sub)) continue;

            foreach (var file in Enumerate(() => Directory.EnumerateFiles(sub, "*" + CellFolder.ViewExtension(view)), f, sub)
                                 .Order(StringComparer.Ordinal))
                CheckPath(file, DocumentKinds.Classify(file), f, cache, recursive: false);
        }
    }

    /// <summary>
    /// A model folder (brief vendorlib-6 R-vl6-2b): the cell-folder checks every cell gets, then
    /// <see cref="ModelFolderCheck"/>'s — the function Validate and Export's self-check call — under the
    /// <c>part.cpart</c>, then the sub-cells the model carries in <c>cells/</c>.
    /// </summary>
    private static void CheckModelFolder(string dir, Findings f, TechnologyCache cache)
    {
        CheckCell(dir, f, cache);
        CheckModelPart(Path.Combine(dir, ModelFolder.PartFileName), f, scoped: true);
        string cells = Path.Combine(dir, ModelFolder.CellsFolder);
        if (Directory.Exists(cells)) WalkFolder(cells, f, cache, recursive: true);
    }

    /// <summary>A <c>part.cpart</c> is checked as the model folder it describes, less the cell half.</summary>
    private static void CheckModelPart(string partPath, Findings f, bool scoped = false)
    {
        if (scoped) f.Begin(partPath, DocumentKind.ModelPart);
        foreach (var d in ModelFolderCheck.Validate(Path.GetDirectoryName(Path.GetFullPath(partPath))!)) f.Add(d);
        if (scoped) f.End();
    }

    private static void CheckModelLibraryManifest(string path, Findings f)
    {
        var (manifest, refusal) = ModelLibraryManifest.Read(path);
        if (manifest is null) { f.Add(CliDiagnostics.CheckModelLibraryRefused(path, refusal!)); return; }
        string dir = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "";
        f.Add(CliDiagnostics.CheckModelLibrarySummary(path, manifest.Name.Length > 0 ? manifest.Name : dir,
            ModelFolder.Under(Path.GetDirectoryName(Path.GetFullPath(path))!).Count));
    }

    /// <summary>
    /// R-lvs1-4a: which layout pin is which schematic port, and whether the cell's own answer holds
    /// up. <b>No rule of its own</b> — every finding is <c>TerminalMap.Validate</c>'s, authored below
    /// the firewall so the cell Properties panel enforces the identical ones (R-lvs1-4c).
    ///
    /// <para><b>It reports on a cell with NO terminal block at all</b> (R-lvs1-4b), which is the
    /// point: <c>check</c> tells a user their cell cannot be compared before they ever ask for a
    /// comparison. A cell with no LAYOUT is silent, because most cells are that and one finding each
    /// would bury every real one.</para>
    /// </summary>
    private static void CheckTerminals(string cellDir, Findings f)
    {
        foreach (var finding in TerminalMap.ValidateCell(cellDir)) f.Add(finding);
    }

    private static int CountViews(string cellDir, ViewType view)
    {
        string sub = CellFolder.SubFolderPath(cellDir, view);
        try { return Directory.Exists(sub) ? Directory.GetFiles(sub, "*" + CellFolder.ViewExtension(view)).Length : 0; }
        catch { return 0; }
    }

    // ── one document of each kind ────────────────────────────────────────────

    /// <summary>
    /// Step one for every view file, and the one an extension alone cannot do:
    /// <c>CellViewFileValidator</c> requires a JSON key only that format writes, then runs the
    /// format's own reader. A `.clay` renamed to `.csch` deserializes CLEANLY into an empty
    /// schematic without it.
    /// </summary>
    private static bool CheckViewFile(string path, ViewType view, Findings f)
    {
        if (CellViewFileValidator.DescribeDefect(path, view) is { } defect)
        {
            f.Add(CliDiagnostics.CheckViewDefect(path, CellFolder.SubFolderName(view), defect));
            return false;
        }
        return true;
    }

    private static void CheckSchematic(string path, Findings f)
    {
        if (!CheckViewFile(path, ViewType.Schematic, f)) return;

        SchematicEditModel model;
        try { (model, _, _) = CircuitSource.LoadSchematic(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // What the lenient read ignored, misread or dropped — a component with no Symbol is read as a
        // resistor and still simulates, so this is the first thing said about the file.
        foreach (var finding in model.LoadFindings) f.Add(CliDiagnostics.CheckSchematicLoadFinding(path, finding));

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        // Every cell reference the schematic carries, through the resolver the editor draws with —
        // so "the placeholder glyph you would see if you opened this" and "what check says" cannot
        // disagree. The three states stay three states (R-aut4-4).
        foreach (var comp in model.Components)
        {
            if (comp.CellRef is not { Length: > 0 } cellRef) continue;

            var res = CellSymbolResolver.Resolve(cellRef, baseDir);
            string who = comp.InstanceName is { Length: > 0 } n ? n : comp.Id;

            switch (res.State)
            {
                case CellSymbolState.NotFound when PdkKitRegistry.IsKitRef(cellRef):
                    // Not a broken reference: a kit part lives in a registry the GUI populates when
                    // it opens a workspace, and nothing populates it here. Saying "no cell folder"
                    // would name the wrong repair for every part in a PDK design.
                    f.Add(CliDiagnostics.CheckKitRefUnresolved(path, who, cellRef));
                    break;
                case CellSymbolState.NotFound:
                    f.Add(CliDiagnostics.CheckRefNotFound(path, who, cellRef));
                    break;
                case CellSymbolState.PrimaryMissing:
                    f.Add(CliDiagnostics.CheckRefPrimaryMissing(path, who, cellRef));
                    break;
            }

            if (res.Redirect is { } moved)
                f.Add(CliDiagnostics.CheckRefRedirected(path, cellRef, moved.To));
        }

        // R-fp4-4a: every stored Footprint, through FootprintCatalog — the SAME resolution
        // SchematicToLayoutGenerator performs, so `check` cannot call a design clean and Update
        // Layout then refuse it. Warnings, because the design is still simulable and what is
        // missing is artwork.
        foreach (var comp in model.Components)
        {
            if (comp.Footprint is not { Length: > 0 } stored) continue;
            string who = comp.InstanceName is { Length: > 0 } n ? n : comp.Id;

            var resolution = FootprintCatalog.Resolve(stored, baseDir);
            if (resolution.State == FootprintCatalog.FootprintState.Unresolved ||
                resolution.Refusal is { Length: > 0 })
            {
                f.Add(CliDiagnostics.CheckFootprintUnresolved(
                    path, who, stored, resolution.Refusal ?? "It named no case size and no cell."));
                continue;
            }

            // Pad count against port count is the contract (the series overview §1f), and the one
            // that bites is an SnP with RefNode set: it has one more port than its file has, so a
            // 2-port S2P with RefNode is three ports and does not fit a two-pad chip land.
            if (resolution.PadCount >= 0 && resolution.PadCount != comp.EffectivePortCount)
                f.Add(CliDiagnostics.CheckFootprintPadCount(
                    path, who, stored, resolution.PadCount, comp.EffectivePortCount));
        }

        // Extraction is the schematic's own "does this make a netlist?" It reports naming conflicts
        // — two different labels on one physical net — which nothing else in the tree reports.
        //
        // DiskCellResolver, never null: a null resolver is how NetExtractor is told the caller is
        // flat, and it answers by skipping every cell instance WITHOUT a conflict note. `check` then
        // reported a hierarchical design as clean while the thing it was checking had its device
        // silently removed — and every run verb agreed with it, because they extracted the same way.
        NetExtractor.ExtractionResult extracted;
        try { extracted = SchematicCircuit.ExtractInWorkspace(model, Path.GetFileNameWithoutExtension(path), DiskCellResolver.Instance, path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // brief-artsch-6 R-as6-1: a TechRef that does not resolve is an ERROR naming it — the extraction also
        // says so among its conflicts, which are warnings, so that one sentence is not repeated as one.
        string? techError = model.TechRef is { Length: > 0 } ? SchematicTechnology.Resolve(model).Error : null;
        if (techError is not null) f.Add(CliDiagnostics.CheckSchematicTechnologyUnresolved(path, techError));

        foreach (var conflict in extracted.Conflicts)
            if (conflict != techError) f.Add(CliDiagnostics.CheckExtractionConflict(path, conflict));
        foreach (var choice in extracted.HostCellChoices)
            f.Add(CliDiagnostics.CheckHostCellChoice(path, choice.Text, choice.IsWarning));

        // Elaboration runs on what the `.cnl` round trip produces, not on the extraction directly —
        // see CircuitSource for why those are not the same TestBench, and for the four shipped
        // schematics that prove it.
        Library   lib;
        TestBench tb;
        try
        {
            string name = Path.GetFileNameWithoutExtension(path);
            // Against the base a run of this schematic resolves its files against (its owning root: the workspace, or the
            // model folder a library part's schematic sits in), not its own folder — else a model's ComposedFET .cmodel
            // was "not found" here while every run found it.
            (lib, tb) = SchematicCircuit.RoundTrip(SchematicCircuit.CnlTextOf(extracted, name), name,
                                                   CircuitSource.ReferenceBaseOf(path));
        }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // The tuning setup is checked against the schematic's own catalog — its VAR rows, its cells'
        // folders — rather than the bare netlist's, so a key the Tuning window would not offer is
        // reported here in the same words (TuningValidator).
        TunableCatalog? catalog = null;
        if (tb.Tuning is { IsEmpty: false })
        {
            string? cws = DocumentKinds.AncestorCws(Path.GetFullPath(path));
            try { catalog = TunableCatalog.Discover(model, DiskCellResolver.Instance, cws is null ? null : Path.GetDirectoryName(cws),
                                                    SchematicCircuit.ReferenceBaseOf(path)); }
            catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }
        }

        Elaborate(path, tb, lib, f, catalog);
    }

    /// <summary>
    /// brief-sdl-6 R-sdl6-7 — every chain that runs by EM (model=em, or an instance set to EM): whether it can, through
    /// the switch the run builds its bench with, and what it will make, through the run's own count — so a refusal
    /// here is the run's refusal word for word, and D4's unreached variables are reported before anyone optimises them.
    /// Nothing is solved; the count may generate a PCell the evaluated geometry needs into the generated-cell store.
    /// </summary>
    private static void CheckEm(string path, TestBench tb, Library lib, Findings f)
    {
        string? refBase = CircuitSource.ReferenceBaseOf(path);
        foreach (var top in AnalysisChain.RunnableTops(tb))
        {
            bool whole = EmModelSwitch.IsEm(top, tb, lib);
            if (!whole && !EmModelSwitch.HasInstancesSetToEm(tb, lib)) continue;
            var switched = EmModelSwitch.Apply(tb, lib, top, Path.GetFullPath(path), refBase,
                                               out var refusal, whole);
            if (switched is null) { f.Add(CliDiagnostics.CheckEmRefused(path, top.Name, refusal ?? "")); continue; }
            var count = EmSolveCount.Count(switched.Tb, switched.Lib, top, refBase);
            foreach (var r in count.Refusals) f.Add(CliDiagnostics.CheckEmRefused(path, top.Name, r));
            foreach (var u in count.Unreached) f.Add(CliDiagnostics.CheckEmUnreached(path, u));
            foreach (var n in count.Notes) f.Add(CliDiagnostics.CheckEmNote(path, n));
            if (count.Any && count.Refusals.Count == 0) f.Add(CliDiagnostics.CheckEmCount(path, top.Name, count.Sentence));
        }
    }

    private static void CheckLayout(string path, Findings f, TechnologyCache cache)
    {
        if (!CheckViewFile(path, ViewType.Layout, f)) return;

        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // What the read ignored or could not make a shape of — the reader's own findings, which the
        // GUI posts on a fresh load (LayoutLoadAudit); nothing here decides what counts.
        foreach (var finding in view.LoadFindings) f.Add(CliDiagnostics.CheckLayoutLoadFinding(path, finding));

        string full    = Path.GetFullPath(path);
        var (tech, _)  = TechnologyResolver.ResolveForDocument(view.TechRef, full, null, cache);

        foreach (var d in tech.Diagnostics) f.Add(CliDiagnostics.CheckResolverNote(path, d));
        if (tech.Source == TechResolutionSource.None) f.Add(CliDiagnostics.CheckNoTechnology(path));

        // R-gc2-2/3: the placed PCells rebuilt IN MEMORY — check writes nothing — so the design rules
        // see the lands they are about; one that cannot be rebuilt is an ERROR finding, never a skip.
        using var generated = GeneratedCells.PrepareForCheck(view, path, f.Add);

        var sideRefusals = CheckPlacementSides(path, view, full, tech.Tech, f);
        RunDrc(path, view, full, tech.Tech, f, cache, sideRefusals);
    }

    /// <summary>
    /// brief-bsp-3 R-bsp3-10 — every placement's side, through <see cref="CellLayoutResolver"/>, the one
    /// resolution every reader of a placed part makes: a bottom placement whose technology has no bottom
    /// layer for one of its roles is a WARNING naming the part and the role (the resolver's own sentence),
    /// and one on a technology with no bottom copper an ERROR. Nested placements are walked through the
    /// resolved views, whose sides are already the composed ones (brief-bsp-1 D5).
    /// </summary>
    /// <returns>The refusals said, so the design-rule half does not repeat them as notes.</returns>
    private static HashSet<string> CheckPlacementSides(string path, LayoutView view, string fullPath, Technology? tech, Findings f)
    {
        var refusals = new HashSet<string>(StringComparer.Ordinal);
        var said = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(LayoutView v, string baseDir, int depth)
        {
            if (depth >= CellHierarchy.MaxDepth) return;
            foreach (var inst in v.Instances)
            {
                var res = CellLayoutResolver.Resolve(inst, baseDir, tech);
                if (res.State == CellLayoutState.SideRefused && res.Refusal is { } refusal)
                {
                    if (refusals.Add(refusal)) f.Add(CliDiagnostics.CheckLayoutSideRefused(path, refusal));
                    continue;
                }
                foreach (var note in res.SideNotes)
                    if (said.Add(note)) f.Add(CliDiagnostics.CheckLayoutSideRoleOmitted(path, note));
                if (res.State != CellLayoutState.Resolved || res.ResolvedCellDir is not { } dir) continue;
                if (!visiting.Add(dir)) continue;   // a cycle is the hierarchy's own finding
                Walk(res.View!, CellFolder.SubFolderPath(dir, ViewType.Layout), depth + 1);
                visiting.Remove(dir);
            }
        }

        Walk(view, CellHierarchy.BaseDirOfDocument(fullPath), 0);
        return refusals;
    }

    /// <summary>
    /// brief-em3d-41 R-em3d41-5: every finding is <c>C3dValidation</c>'s — no rule of its own — with
    /// the materials checked against the technology the document resolves, exactly as a layout's.
    /// </summary>
    private static void CheckThreeD(string path, Findings f, TechnologyCache cache)
    {
        if (!CheckViewFile(path, ViewType.ThreeD, f)) return;

        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        using var generated = GeneratedCells.PrepareThreeDForCheck(path, f.Add);

        var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, Path.GetFullPath(path), null, cache);
        foreach (var d in tech.Diagnostics) f.Add(CliDiagnostics.CheckResolverNote(path, d));

        Func<string, bool>? known = null;
        if (tech.Tech is { } t) known = name => C3dProblemAssembly.ObjectMaterial(t, name) is not null;
        else f.Add(CliDiagnostics.CheckThreeDNoTechnology(path));

        // brief-em3d-51 R-em3d51-5c — the names and every expression first, through the resolver elaboration uses: an
        // undefined name or a cycle (error), a VAR hiding a parameter or a dimension above 1 m (warning), a rounding and an
        // unused VAR (info). Validation then reads the RESOLVED numbers, and skips the objects whose fields did not resolve
        // rather than calling an unresolved size zero.
        var resolution = C3dResolver.Resolve(doc, C3dCell.Of(Path.GetFullPath(path)));
        foreach (string why in resolution.Errors) f.Add(CliDiagnostics.CheckThreeDElaboration(path, why));
        foreach (string warning in resolution.Warnings) f.Add(CliDiagnostics.CheckEmFinding(path, warning, true));
        foreach (string note in resolution.Notes.Concat(resolution.Infos)) f.Add(CliDiagnostics.CheckThreeDNote(path, note));

        var findings = C3dValidation.Validate(doc, known, resolution.FieldErrors.Select(e => e.Item).ToHashSet(StringComparer.Ordinal),
                                              Path.GetFullPath(path)).ToList();
        // brief-em3d-64 R-em3d64-5e — without the geometry kernel, one error per object that needs it, in the words the
        // editor refuses to open the document with. The kernel is asked only when the document holds such an object.
        if (C3dKernelUse.Of(doc).Count > 0)
            findings.AddRange(C3dValidation.KernelFindings(doc, CircuitRF.Design.ThreeD.Occ.GeometryKernel.Shared.Capability));
        // brief-em3d-73 R-em3d73-6a — the thermal places' own shape, from the one thermal validator.
        findings.AddRange(C3dThermal.Places(doc));
        foreach (var finding in findings)
            f.Add(CliDiagnostics.CheckThreeDFinding(path, finding));

        // brief-em3d-42 R-em3d42-6 — elaboration's refusals, through the elaborator the GUI will draw with, and
        // every embedded setup read as a run would read it. Only once the document is sound: elaborating a
        // document validation already refused would report its defects twice, in two voices.
        if (!resolution.Ok || findings.Any(d => d.Severity == DiagnosticSeverity.Error)) return;
        // EMB-5 R-emb5-3/4 — the elaboration is this check's long step on a board's 3D view: named for a host's progress,
        // and a cancelled call stops before and after it rather than reporting the rest.
        if (RunHost.Control is { } control) { control.ThrowIfCancellationRequested(); control.BeginStage("elaborating the 3D view"); }
        var e = C3dElaborator.ElaborateOnce(doc, Path.GetFullPath(path), null);
        RunHost.Control?.ThrowIfCancellationRequested();
        // Validation's own findings too: an object with no material is a validation warning AND an elaboration warning, in
        // the same words (3D editor bugs round 2), and is said once.
        var said = new HashSet<string>(resolution.Errors.Concat(resolution.Warnings).Concat(resolution.Notes).Concat(resolution.Infos)
                                                        .Concat(findings.Select(d => d.Render())), StringComparer.Ordinal);
        foreach (string refusal in e.Refusals.Where(x => !said.Contains(x) && !e.NotModelledRefusals.Contains(x)))
            f.Add(CliDiagnostics.CheckThreeDElaboration(path, refusal));
        foreach (string warning in e.Warnings.Where(x => !said.Contains(x))) f.Add(CliDiagnostics.CheckEmFinding(path, warning, true));
        foreach (string note in e.Notes.Where(x => !said.Contains(x))) f.Add(CliDiagnostics.CheckThreeDNote(path, note));
        foreach (var setup in C3dSetups.Read(doc))
            if (setup.Refusal is { } why) f.Add(CliDiagnostics.CheckThreeDSetup(path, why));
        // brief-em3d-129 R-em3d129-1a — a Step object holding several solids as one: one material for what is probably several.
        // Only an object the elaboration built as more than one solid asks the kernel to read its file.
        if (doc.Objects.SelectMany(C3dOperands.SelfAndDescendants).Any(o => o is C3dStep { Solid: null })
            && CircuitRF.Design.ThreeD.Occ.GeometryKernel.Shared.Capability.Available)
        {
            var built = e.KernelBuilds.GroupBy(k => k.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Solids, StringComparer.Ordinal);
            try
            {
                foreach (var d in CircuitRF.Design.ThreeD.Step.StepSplit.Findings(doc, Path.GetFullPath(path), CircuitRF.Design.ThreeD.Occ.GeometryKernel.Shared, built))
                    f.Add(CliDiagnostics.CheckThreeDFinding(path, d));
            }
            catch (CircuitRF.Design.ThreeD.Occ.GeometryKernelException) { /* the elaboration has already said what the kernel refused */ }
        }
        // brief-em3d-49 R-em3d49-5c — every port's inferred polarity (info) or its refusal (error), and every face
        // boundary that cannot be placed — through the resolution a run makes.
        // brief-emb-2 R-emb2-3 — against the outline the run meshes: a setup with Robust meshing on (the default) measures its
        // ports on the cleaned outline, as its run does, so check and the run cannot disagree about a port.
        C3dElaboration? robust = null;
        C3dElaboration ElaborationFor(EmSetup? setup) => setup?.RobustMeshing == false ? e
            : robust ??= C3dElaborator.ElaborateOnce(doc, Path.GetFullPath(path), null, new C3dElaborationOptions { RobustMeshing = true });
        foreach (var port in C3dPortReports.For(doc, e, ElaborationFor))
            f.Add(!port.Refused ? CliDiagnostics.CheckThreeDPort(path, port.Text) : CliDiagnostics.CheckThreeDPortRefused(path, port.Text));
        CheckPortDirections(path, doc, e, f);
        CheckComponentPorts(path, doc, f);
        // brief-em3d-93 R-em3d93-5 — what is not modelled (info), and what refers to it: a wire on its pad, every port off under
        // a driven setup, a field plot's face (warning), and the refusals it raised itself (warnings: no run refuses for them).
        foreach (var d in C3dModelled.Findings(doc, e)) f.Add(CliDiagnostics.CheckThreeDFinding(path, d));
        // brief-em3d-101 R-em3d101-8g — a face image on a face its object no longer has: a warning, the record kept.
        foreach (var d in C3dImages.FaceImageFindings(e)) f.Add(CliDiagnostics.CheckThreeDFinding(path, d));
        foreach (string why in C3dPortReports.FaceBoundaryRefusals(doc, e))
            f.Add(CliDiagnostics.CheckThreeDFaceBoundary(path, why));

        // brief-em3d-73 R-em3d73-6a — the thermal places against the solids, and every thermal setup: through the
        // validator the editor and the run use.
        foreach (var d in C3dThermal.Places(doc, e)) f.Add(CliDiagnostics.CheckThreeDFinding(path, d));
        foreach (var embedded in C3dSetups.Read(doc))
            if (embedded.Setup is { IsThermal: true } thermal)
                foreach (var d in C3dThermal.Setup(embedded.Name, thermal, doc, e, resolution,
                                                   (Path.GetFullPath(path), ResultsRoot.For(Path.GetFullPath(path), DocumentKinds.AncestorCws(Path.GetFullPath(path))))))
                    f.Add(CliDiagnostics.CheckThreeDFinding(path, d));

        // brief-em3d-120 R-em3d120-2 C1 — an openEMS setup with coaxial wave ports: on a Cartesian grid, the suggestion of a
        // cylindrical one (a note, never inferred into the run); on a cylindrical grid, what the grid would refuse.
        if (doc.Ports.Any(p => p.Model && p.Kind == CircuitRF.Engine.Em3d.Em3dPortKind.Wave))
            foreach (var embedded in C3dSetups.Read(doc))
                if (embedded is { Refusal: null, Setup: { Is3D: true, IsStatic3D: false } s3 } && s3.Solver3D.Runs(CircuitRF.Design.Em3d.OpenEmsSolver.Id))
                    CylindricalAdvice(path, f, embedded.Name, C3dSetups.ForRun(s3, Path.GetFullPath(path)), doc);

        // brief-em3d-65 R-em3d65-4d — what each embedded setup's solver will not respect of a kernel solid, at the row's own
        // severity. Only a document holding a kernel object assembles anything for those rows.
        // brief-em3d-139 R-em3d139-5 — and metal thin against the skin depth, which a dry run and a run both state, for a setup a
        // solver that models a conductor by its faces runs. That needs the problem whatever the document holds.
        bool kernel = C3dKernelUse.Of(doc).Count > 0;
        foreach (var embedded in C3dSetups.Read(doc))
            if (embedded is { Refusal: null, Setup: { Is3D: true } s } && (kernel || MayModelThinMetal(s)))
            {
                RunHost.Control?.ThrowIfCancellationRequested();
                var run = C3dSetups.ForRun(s, Path.GetFullPath(path));
                var assembled = C3dProblemAssembly.Assemble(run, doc, Path.GetFullPath(path), DocumentKinds.AncestorCws(Path.GetFullPath(path)));
                if (kernel) AddFidelity(path, f, embedded.Name, run, assembled.Problem);
                if (assembled.Problem is not { } problem) continue;
                string solver = s.Solver3D.Runs(CircuitRF.Design.Em3d.PalaceSolver.Id) && s.Solver3D.Runs(CircuitRF.Design.Em3d.EmergeSolver.Id) ? "Palace, EMerge"
                              : s.Solver3D.Runs(CircuitRF.Design.Em3d.PalaceSolver.Id) ? "Palace" : "EMerge";
                foreach (string warning in CircuitRF.Design.Layout.Em3d.Em3dThinMetalLoss.ForRun(problem, run))
                    f.Add(CliDiagnostics.CheckThreeDFidelity(path, embedded.Name, solver, warning, true));
                foreach (string note in CircuitRF.Design.Layout.Em3d.Em3dThinMetalLoss.NotesForRun(problem, run))
                    f.Add(CliDiagnostics.CheckThreeDFidelity(path, embedded.Name, solver, note, false));
            }
    }

    /// <summary>brief-em3d-139 R-em3d139-5 — a driven or eigenmode setup on Palace or EMerge: the only ones thin metal can be said of.</summary>
    private static bool MayModelThinMetal(EmSetup s)
        => !s.IsThermal && s.Problem3D is CircuitRF.Engine.Em3d.Em3dProblemType.Driven or CircuitRF.Engine.Em3d.Em3dProblemType.Eigenmode
           && (s.Solver3D.Runs(CircuitRF.Design.Em3d.PalaceSolver.Id) || s.Solver3D.Runs(CircuitRF.Design.Em3d.EmergeSolver.Id));

    /// <summary>brief-em3d-120 — the cylindrical-grid note or refusals for one embedded openEMS setup, from the run's own planner.</summary>
    private static void CylindricalAdvice(string path, Findings f, string setupName, EmSetup setup, C3dDocument doc)
    {
        var settings = CemOpenEms.ResolveGrid(setup.OpenEms, path);
        if (settings.Problems().Count > 0) return;
        var assembled = C3dProblemAssembly.Assemble(setup, doc, Path.GetFullPath(path), DocumentKinds.AncestorCws(Path.GetFullPath(path)));
        if (assembled.Problem is not { } problem || problem.Validate().Count > 0) return;
        if (settings.Cylindrical is not null)
        {
            var grid = CircuitRF.Engine.Em3d.FdtdGrid.Build(problem, settings, long.MaxValue);
            if ((grid.Refusal ?? CircuitRF.Engine.Em3d.FdtdWavePorts.Place(problem, grid.WavePorts, grid).Refusal) is { } refused)
                f.Add(CliDiagnostics.CheckThreeDSetup(path, $"setup '{setupName}': {refused}"));
            return;
        }
        var plan = CircuitRF.Engine.Em3d.FdtdWavePorts.Plan(problem, settings);
        if (CircuitRF.Engine.Em3d.FdtdWavePorts.CommonCoaxialAxis(problem, plan, out _) is not var (axis, origin)) return;
        static string G(double v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        f.Add(CliDiagnostics.CheckThreeDNote(path,
            $"setup '{setupName}': every wave port is coaxial about the {CircuitRF.Engine.Em3d.FdtdGrid.AxisName(axis)} axis through " +
            $"[{G(origin.X * 1e6)}, {G(origin.Y * 1e6)}, {G(origin.Z * 1e6)}] µm. A cylindrical openEMS grid puts its round conductors on the " +
            $"grid's own circles (OpenEms.Grid: Cylindrical, Axis: {CircuitRF.Engine.Em3d.FdtdGrid.AxisName(axis).ToUpperInvariant()}, " +
            $"AxisOriginUm: [{G(origin.X * 1e6)}, {G(origin.Y * 1e6)}, {G(origin.Z * 1e6)}]); on the Cartesian grid brief 113-a measured " +
            "such a coax 1 to 2.4 % slow in phase velocity."));
    }

    /// <summary>brief-em3d-65 — one finding per fidelity row of <paramref name="problem"/> under <paramref name="setup"/>.</summary>
    private static void AddFidelity(string path, Findings f, string setupName, EmSetup setup, CircuitRF.Engine.Em3d.Em3dProblem? problem)
    {
        if (problem is null) return;
        foreach (var row in CircuitRF.Design.Em3d.Em3dFidelityReport.For(problem, setup))
            f.Add(CliDiagnostics.CheckThreeDFidelity(path, setupName, CircuitRF.Design.Em3d.Em3dFidelityReport.SolverName(row.Solver),
                                                     row.Sentence, row.Severity == CircuitRF.Engine.Em3d.Em3dFidelitySeverity.Warning));
    }

    /// <summary>
    /// brief-emb-1 R-emb1-4 — every embedded EM setup's component ports, built and verified as its run builds them (through
    /// <see cref="C3dProblemAssembly.Assemble"/>, the run's own function): each port's construction (info) or the refusal that
    /// names its refdes (error), and each part the co-simulation netlist would carry as a placeholder (warning).
    /// </summary>
    private static void CheckComponentPorts(string path, C3dDocument doc, Findings f)
    {
        string full = Path.GetFullPath(path);
        foreach (var embedded in C3dSetups.Read(doc))
        {
            if (embedded is not { Refusal: null, Setup: { Is3D: true, IsThermal: false, ComponentPorts.Count: > 0 } s }) continue;
            var run = C3dSetups.ForRun(s, full);
            var assembled = C3dProblemAssembly.Assemble(run, doc, full, DocumentKinds.AncestorCws(full));
            string at = $"setup '{embedded.Name}': ";
            if (assembled.ComponentPorts is not { } build)
            {
                if (assembled.Refusal is { } why) f.Add(CliDiagnostics.CheckThreeDPortRefused(path, at + "its component ports were not built: " + why));
                continue;
            }
            // A part whose port cannot be built is left out of the run, which goes ahead with the rest: it is the build's
            // warning, not an error. The run refusing (no build at all, or no port left) is still an error.
            if (build.Refusal is { } refused) f.Add(CliDiagnostics.CheckThreeDPortRefused(path, at + refused));
            else if (assembled.Refusal is { } none) f.Add(CliDiagnostics.CheckThreeDPortRefused(path, at + none));
            foreach (var p in build.Ports.Where(p => p.Ok))
                f.Add(CliDiagnostics.CheckThreeDPort(path, at + C3dComponentPorts.Describe(p)));
            foreach (string w in build.Warnings) f.Add(CliDiagnostics.CheckThreeDComponentPortWarning(path, at + w));
        }
    }

    /// <summary>
    /// brief-emb-3 R-emb3-2/3 — each lumped port's direction beside its gap's length and width (info), a direction that is not
    /// along the line between its conductors or the pair of edges that faces them (warning), and stated ends that disagree with
    /// what the port touches (error) — from <see cref="C3dPortChecks"/>, which reads the resolution a run makes.
    /// </summary>
    private static void CheckPortDirections(string path, C3dDocument doc, C3dElaboration e, Findings f)
    {
        foreach (var d in C3dPortChecks.For(doc, e)) f.Add(CliDiagnostics.CheckThreeDFinding(path, d));
    }

    /// <summary>
    /// The design rules, run exactly as <c>LayoutEditorViewModel.RunDrc</c> runs them — the same
    /// flatten, the same engine, the same waiver store (R-aut4-3). What is deliberately NOT here is
    /// the wire half: a <c>WBondCheckContext</c> needs the wBond design the layout editor's document
    /// installs at runtime, which is a property of what is OPEN rather than of the artwork, so a
    /// headless check of a `.clay` alone has no wires to check and says so by checking none.
    /// </summary>
    private static void RunDrc(
        string path, LayoutView view, string fullPath, Technology? tech, Findings f, TechnologyCache cache,
        IReadOnlySet<string>? alreadySaid = null)
    {
        string cellDir = CellDirOf(fullPath);

        LayoutDesignFlatten.FlattenResult flat;
        try
        {
            flat = LayoutDesignFlatten.Flatten(
                view, cellDir, tech,
                (techRef, subCellLayoutDir) =>
                    TechnologyResolver.ResolveForDocument(techRef, subCellLayoutDir, null, cache).Resolution,
                resolvedCrossTechMappings: null);
        }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckDrcNote(path, ex.Message)); return; }

        if (flat.ExceedsCeiling)
        {
            f.Add(CliDiagnostics.CheckDrcNote(path,
                $"This design flattens to more than {LayoutDesignFlatten.HardCeiling:N0} shapes — " +
                "the check was refused rather than run."));
            return;
        }

        // A refused bottom placement was reported as an error by CheckPlacementSides; the flatten skips it too.
        foreach (var u in flat.UnresolvedInstances)
            if (alreadySaid is null || !alreadySaid.Any(r => u.StartsWith(r, StringComparison.Ordinal)))
                f.Add(CliDiagnostics.CheckDrcNote(path, u));

        foreach (var pending in flat.PendingCrossTechMappings)
            f.Add(CliDiagnostics.CheckDrcNote(path,
                $"\"{Path.GetFileName(pending.Key)}\" is drawn against a different technology and its " +
                "layers have not been mapped onto this one, so it was not checked."));

        DrcRunResult result;
        try { result = DrcEngine.Run(flat.Shapes, tech, view.DrcWaivers); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckDrcNote(path, ex.Message)); return; }

        foreach (var d in result.Diagnostics) f.Add(CliDiagnostics.CheckDrcNote(path, d));

        foreach (var v in result.Violations)
            f.Add(DrcViolationDiagnostic(path, view, tech, v));
    }

    /// <summary>
    /// A violation as <c>check</c> reports it: the layer by its technology name, the measurement
    /// against the rule, the nets, and the marker box in the layout's display unit — everything a
    /// reader needs to find it without opening the layout editor.
    /// </summary>
    private static Diagnostic DrcViolationDiagnostic(string path, LayoutView view, Technology? tech, DrcViolation v)
    {
        string Spell(long dbu) => LayoutUnits.Spell(dbu, view.DisplayUnit, view.DbuPerMicron);
        double In(long dbu) => (double)LayoutUnits.FromDbu(dbu, view.DisplayUnit, view.DbuPerMicron);

        string? layer     = v.Layer is { } k ? $"{k.Layer}/{k.Datatype}" : null;
        string? layerName = v.Layer is { } key ? tech?.Layers.FirstOrDefault(l => l.Key == key)?.Name : null;

        // A die-side rule's value is in DBU; a wire rule states its own measurement in words.
        string? required = v.IsAssembly || v.Layer is null || v.Kind == DrcRuleKind.MustBeEmpty
            ? null : Spell(v.RequiredDbu);
        string? measured = v.MeasuredText ?? (v.MeasuredDbu is { } m ? Spell(m) : null);
        bool touching    = v.MeasuredDbu == 0 && v.NetA is not null && v.NetB is not null && v.NetA != v.NetB;

        var b = v.Marker;
        string region = $"({Spell(b.MinX)}, {Spell(b.MinY)})–({Spell(b.MaxX)}, {Spell(b.MaxY)})";

        return CliDiagnostics.CheckDrcViolation(
            path, v.RuleName, v.Kind.ToString(),
            // A WAIVED violation is reported and does not count (§9A.1: waiving must be "persisted,
            // and visible"), so it lands as a note rather than at the rule's own severity — which is
            // what keeps a fully-waived design exiting 0.
            v.Waived ? DiagnosticSeverity.Info
                     : v.Severity == DrcSeverity.Error ? DiagnosticSeverity.Error
                                                       : DiagnosticSeverity.Warning,
            layer, layerName, measured, required, v.NetA, v.NetB, touching,
            LayoutUnits.AsciiSuffix(view.DisplayUnit), In(b.MinX), In(b.MinY), In(b.MaxX), In(b.MaxY),
            region, v.Waived);
    }

    /// <summary>The cell folder a view file belongs to: <c>&lt;cell&gt;/&lt;view&gt;/&lt;file&gt;</c>.
    /// A loose file outside a cell folder resolves to its own directory's parent, which is what the
    /// flatten already treats as the root for relative instance references.</summary>
    private static string CellDirOf(string viewFilePath)
        => Path.GetDirectoryName(Path.GetDirectoryName(viewFilePath)!) ?? "";

    private static void CheckTechnology(string path, Findings f)
    {
        Technology tech, raw;
        string json;
        try
        {
            json = GzipTextFile.ReadAllTextAutoGzip(path);
            // brief-em3d-2 R-em3d2-4a: the file AS WRITTEN, for the one rule a loaded technology
            // can no longer see — a named material's numbers disagreeing with the entry's own.
            raw  = TechPersistence.DeserializeUnresolved(json);
        }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // brief-em3d-53 §1d — a technology whose libraries cannot be read, or define one name two ways,
        // does not load. Each such problem is an error under its own id; the file's own content is then
        // still checked, so the rest of what is wrong is not hidden behind the refusal.
        try
        {
            tech = TechPersistence.Deserialize(json, MaterialLibraries.Disk(path), Path.GetFullPath(path));
        }
        catch (MaterialLibraryException ex)
        {
            foreach (var p in ex.Problems)
                f.Add(CliDiagnostics.CheckTechProblem(path, nameof(TechProblemArea.Materials), p.Message,
                                                      DiagnosticSeverity.Error, p.Id));
            foreach (var p in MaterialValidation.Validate(raw.Materials).Concat(MaterialValidation.ValidateInterfaces(raw.ThermalInterfaces)))
                f.Add(CliDiagnostics.CheckTechProblem(path, p.Area.ToString(), p.Message, p.Severity, p.Id));
            return;
        }
        raw.LibraryMaterials = tech.LibraryMaterials;

        foreach (var p in TechValidation.Analyze(tech).Concat(TechValidation.AnalyzeRaw(raw)))
            f.Add(CliDiagnostics.CheckTechProblem(path, p.Area.ToString(), p.Message, p.Severity, p.Id));
    }

    /// <summary>
    /// brief-em3d-53 R-em3d53-7 — a standalone <c>.cmat</c>: the same material rules a technology's own
    /// list is held to (<see cref="MaterialValidation"/>), and nothing about USE, which only a technology
    /// that names the library can answer.
    /// </summary>
    private static void CheckMaterialLibrary(string path, Findings f)
    {
        CmatFile file;
        try { file = MaterialLibraryPersistence.LoadFileFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        foreach (var p in MaterialValidation.Validate(file.Materials).Concat(MaterialValidation.ValidateInterfaces(file.ThermalInterfaces)))
            f.Add(CliDiagnostics.CheckTechProblem(path, p.Area.ToString(), p.Message, p.Severity, p.Id));
    }

    /// <summary>brief-lcp-2 — <c>em --dry-run --component-ports</c>'s list, for the <c>.cem</c> this check reads: the estimate is
    /// then made of the problem the run would solve, not of the file's own list. Async-local, because <c>serve</c> runs verbs
    /// side by side; null everywhere else.</summary>
    internal static readonly AsyncLocal<IReadOnlyList<EmComponentPort>?> ComponentPortsOverride = new();

    private static void CheckEmSetup(string path, Findings f, TechnologyCache cache)
    {
        EmSetup setup;
        try { setup = EmSetupPersistence.LoadFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }
        if (ComponentPortsOverride.Value is { Count: > 0 } wanted && EmComponentPorts.Inapplicable(setup) is null)
            setup.ComponentPorts = [.. wanted];

        // brief-em3d-73 D1 — a thermal setup lives in its .c3d; the run refuses a .cem holding one, and so does check.
        if (setup.IsThermal) { f.Add(CliDiagnostics.CheckEmRefused(path, C3dThermal.CemRefusal)); return; }
        // brief-em3d-120 C5 — a cylindrical grid is a 3D view's own setup's; the run refuses a .cem stating one, and so does check.
        if (setup.Is3D && CircuitRF.Design.Em3d.Em3dSolvers.Of(setup.Solver3D).Select(s => s.CemRefusal(setup)).FirstOrDefault(r => r is not null) is { } cylinder)
        { f.Add(CliDiagnostics.CheckEmRefused(path, cylinder)); return; }

        string full = Path.GetFullPath(path);
        // The same walk-up `circuitrf em` performs, and for the same reason (cli.md §8.1). No flag
        // and no "currently open workspace": the `.cem`'s own ancestor is what its LayoutRef is
        // relative to.
        string? cws = DocumentKinds.AncestorCws(full);

        // brief-em3d-42 R-em3d42-5b — a LayoutRef naming a .c3d: the document, elaborated and assembled with this
        // setup exactly as the run assembles it.
        if (C3dSetups.IsThreeDView(EmSetupResolver.ResolveLayoutPath(full, setup.LayoutRef, cws)))
        {
            var src = Em3dSetupSource.FromCemOnThreeDView(full, setup, cws);
            // brief-lcp-2 R-lcp2-3 — its component ports as a .c3d's are reported, under the .cem's own ids.
            var viewParts = src.Generated?.ComponentPorts;
            if (viewParts is not null)
            {
                foreach (var p in viewParts.Ports.Where(p => p.Ok)) f.Add(CliDiagnostics.CheckCemComponentPort(path, C3dComponentPorts.Describe(p)));
                foreach (string w in viewParts.Warnings) f.Add(CliDiagnostics.CheckCemComponentPortWarning(path, w));
            }
            foreach (string note in src.Generated?.Notes ?? []) f.Add(CliDiagnostics.CheckEmNote(path, note));
            foreach (string warning in src.Generated?.Warnings ?? [])
                if (viewParts?.Warnings.Contains(warning) != true) f.Add(CliDiagnostics.CheckEmFinding(path, warning, true));
            if (src.Refusal is { } refused) { f.Add(CliDiagnostics.CheckEmRefused(path, refused)); return; }
            var p3 = src.Generated!.Problem!;
            var bad = p3.Validate();
            foreach (string b in bad) f.Add(CliDiagnostics.CheckEmRefused(path, b));
            // brief-em3d-65 R-em3d65-4d — a .cem naming a .c3d: the same rows, under this setup.
            if (bad.Count == 0) AddFidelity(path, f, setup.Name is { Length: > 0 } sn ? sn : Path.GetFileNameWithoutExtension(path), setup, p3);
            if (bad.Count == 0)
                f.Add(CliDiagnostics.CheckEmWouldRun(path,
                    $"3D problem for {setup.Solver3D} from a 3D view: {p3.Solids.Count} solid(s), {p3.Sheets.Count} sheet(s), " +
                    $"{p3.Ports.Count} port(s)", 0));
            return;
        }

        var resolution = EmSetupResolver.Resolve(full, setup.LayoutRef, cws, cache);

        foreach (var d in resolution.Diagnostics) f.Add(CliDiagnostics.CheckResolverNote(path, d));

        if (resolution.Source is null)
        {
            f.Add(CliDiagnostics.CheckEmUnresolved(path,
                resolution.Diagnostics.Count > 0
                    ? "its layout did not resolve, so there is no geometry to analyse."
                    : "its layout reference resolves to nothing."));
            return;
        }

        // R-gc2-2/3: the setup's layout, with its placed PCells rebuilt in memory — the geometry the
        // preflight measures is the geometry a run would solve.
        using var generated = GeneratedCells.PrepareForCheck(
            resolution.Source.View, resolution.Source.AbsolutePath, f.Add);

        // ── brief-em3d-3 — A 3D SETUP: BUILD THE 3D PROBLEM, AND NOTHING ELSE ────────────────────
        //
        // The planar preflight answers a question a 3D setup does not ask (it would refuse case B's
        // via transition, which is exactly what 3D exists for). The generator is the 3D run's own
        // first step, so a setup that checks clean here is one whose problem the backend receives.
        if (setup.Is3D)
        {
            CheckEm3dSetup(path, f, setup, resolution.Source);
            return;
        }

        // ── EM-SEV R-emsev-5 — RUN THE EXTRACTION AND THE MESH, AND NOTHING ELSE ─────────────────
        //
        // Until this, `check` on a `.cem` reported "0 errors, 0 warnings, 0 notes" for a setup whose
        // run was about to drop half the drawn circuit on the floor. Every one of those findings is
        // produced before the first frequency point is solved: on the design that prompted the brief
        // the extract-and-mesh phase took a second or two and the solve took eleven minutes.
        //
        // It stays inside this verb's own two rules. It RUNS NO ANALYSIS (R-aut4-1) — `Preflight`
        // stops at the mesh and fills no matrix — and it WRITES NO VALIDATION LOGIC (R-aut4-2):
        // every sentence and every severity below came out of `PlanarExtractor`, `SurfaceMesher` or
        // `PlanarSolve.LevelSeparationNotes`, which is what the GUI's own Simulate calls. It also
        // writes no FILES, so it runs on a read-only tree and on a workspace another process has
        // open, which is the property that makes `check` usable at all.
        // brief-lcp-2 R-lcp2-3 — the setup's component ports, built as its run builds them (EmPlanarComponentPorts.Build, cached,
        // so the preflight below reads this same build): each port at info, each part left out at warning, a list that cannot
        // be built at all at error — CheckComponentPorts' severities on a .c3d. The preflight's copies of those lines are not
        // repeated.
        var parts = EmPlanarComponentPorts.Build(setup, resolution.Source);
        if (parts is not null)
        {
            foreach (var p in parts.Ports.Where(p => p.Ok))
                f.Add(CliDiagnostics.CheckCemComponentPort(path, EmPlanarComponentPorts.Describe(p, resolution.Source)));
            foreach (string w in parts.Warnings) f.Add(CliDiagnostics.CheckCemComponentPortWarning(path, w));
            if (parts.Refusal is { } refusedParts) f.Add(CliDiagnostics.CheckCemComponentPortRefused(path, refusedParts));
        }
        bool Reported(string text) => parts is not null && (parts.Notes.Contains(text) || parts.Warnings.Contains(text));

        EmPreflightResult pre;
        try { pre = EmRunService.Preflight(setup, resolution.Source, RunHost.Control); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            f.Add(CliDiagnostics.CheckEmRefused(path, ex.Message));
            return;
        }

        foreach (var finding in pre.Findings)
            if (!Reported(finding.Text)) f.Add(CliDiagnostics.CheckEmFinding(path, finding.Text, finding.IsWarning));

        // A mesh field the file states and Auto then discards (PlanarMeshSettings.Resolved): the
        // mesh notes report the value USED, so without this nothing says why it differs. Only when
        // the planar kernel was chosen — the other kernels never read PlanarMesh at all, and a
        // warning about a mesh that does not run would be its own false statement.
        if (pre.Kind == EmAnalysisKind.Planar && setup.AutoOverriddenMeshFields() is { Count: > 0 } overridden)
            f.Add(CliDiagnostics.CheckEmMeshAutoOverride(
                path,
                string.Join(", ", overridden.Select(o => o.Key)),
                string.Join(", ", overridden.Select(o => $"{o.Key} = {o.Stated}")),
                string.Join(", ", overridden.Select(o => $"{o.Key} = {o.Used}"))));

        // EMB-4 R-emb4-7 — and the fields stated beside Auto that it does NOT override: they win, which is the rule,
        // and saying so is what tells a reader of the file that Auto chose only the rest.
        if (pre.Kind == EmAnalysisKind.Planar && setup.AutoKeptMeshFields() is { Count: > 0 } kept)
            f.Add(CliDiagnostics.CheckEmMeshAutoKept(
                path,
                string.Join(", ", kept.Select(o => o.Key)),
                string.Join(", ", kept.Select(o => $"{o.Key} = {o.Stated}"))));

        if (pre.PlanarMesh is { } mesh)
        {
            // EMB-5 R-emb5-2 — the numbers behind the sentences above, for `em --dry-run` and any caller that would
            // rather not parse them: the memory the run's own check judges (PlanarMemoryCheck's input), the factor time
            // the mesh budget quotes (or the near-field estimate's when the accelerated solve runs), and the sweep's points.
            int points = 0;
            try { points = setup.ExpandFrequencies().Length; } catch (Exception) { /* the sweep's own refusal is above */ }
            long memory = pre.NearField is { } nf ? nf.ResidentBytes : mesh.RunResidentBytes;
            double? perPoint = pre.NearField is { } near ? near.FactorSeconds
                             : setup.AcceleratedSolve ? null
                             : SurfaceMesher.EstimatedFactorSeconds(mesh.UnknownCount);
            f.SetEm(new CheckedEmJson(
                pre.KernelName, mesh.UnknownCount,
                mesh.CeilingUnknowns > 0 ? mesh.CeilingUnknowns : null,
                mesh.CeilingUnknowns > 0 ? (mesh.AcceleratedCeiling ? "accelerated" : "dense") : null,
                mesh.Verdict.ToString().ToLowerInvariant())
            {
                MemoryBytes           = memory,
                FactorSecondsPerPoint = perPoint,
                Points                = points > 0 ? points : null,
                FactorSecondsTotal    = perPoint is { } s && points > 0 ? s * points : null,
                Memory                = mesh.PastMachineLimit || memory > CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes
                                            ? "past-limit" : "fits",
            });
        }

        if (pre.Refusal is { } refusal)
        {
            if (refusal != parts?.Refusal) f.Add(CliDiagnostics.CheckEmRefused(path, refusal));
        }
        // UnknownCount is what is SOLVED — the merged count under Mesh as Merged Shapes — which is the
        // number the panel and an `em` run quote. Bases.Count is the elemental rooftop count, and
        // quoting it here while the JSON two lines up quoted the other was one verb disagreeing with
        // itself (PM-3).
        else f.Add(CliDiagnostics.CheckEmWouldRun(
            path, pre.KernelName, pre.PlanarMesh?.UnknownCount ?? 0));
    }

    private static void CheckEm3dSetup(string path, Findings f, EmSetup setup, EmLayoutSource source)
    {
        // R-em3d3-3b — kept, not read: said at info so a setting that does nothing is never silent.
        if (setup.PlanarOnlyFieldsSet() is { Count: > 0 } planarOnly)
            f.Add(CliDiagnostics.CheckEmNote(path,
                $"This setup runs the 3D solver {setup.Solver3D}, which ignores its planar-only " +
                $"field{(planarOnly.Count == 1 ? "" : "s")} {string.Join(", ", planarOnly)}. " +
                $"{(planarOnly.Count == 1 ? "It is" : "They are")} kept, so switching the setup back to " +
                $"planar finds {(planarOnly.Count == 1 ? "it where it was" : "them where they were")}."));

        // brief-em3d-22 R-em3d22-1b/c — a static problem on Palace only, and what it keeps but does not read.
        // brief-em3d-23 — likewise an eigenmode solve: the run's own sentence (brief-em3d-116 lifted the wave port).
        if (CircuitRF.Design.Em3d.Em3dRunService.SolverRefusal(setup) is { } solverOnly)
        {
            f.Add(CliDiagnostics.CheckEmRefused(path, solverOnly));
            return;
        }
        if (setup.IsStatic3D && setup.DrivenOnlyFieldsSet() is { Count: > 0 } drivenOnly)
            f.Add(CliDiagnostics.CheckEmNote(path,
                $"This setup is {setup.Problem3D}, which solves no frequency and drives no port impedance, so its " +
                $"field{(drivenOnly.Count == 1 ? "" : "s")} {string.Join(", ", drivenOnly)} " +
                $"{(drivenOnly.Count == 1 ? "is" : "are")} kept but not read."));

        // brief-em3d-31 — a radiation pattern on a problem that radiates nothing in a driven sense.
        if (CircuitRF.Design.Em3d.Em3dRunService.RadiationPatternIgnored(setup) is { } noPattern)
            f.Add(CliDiagnostics.CheckEmNote(path, noPattern));

        if (source.Technology is not { } tech)
        {
            f.Add(CliDiagnostics.CheckEmRefused(path, EmDiagnostics.NoTechnology(setup.LayoutRef).Render()));
            return;
        }

        CircuitRF.Design.Layout.Em3d.Em3dGenerationResult generated;
        try { generated = Em3dSetupProblem.Of(setup, source, tech, workspaceCws: DocumentKinds.AncestorCws(path)); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckEmRefused(path, ex.Message)); return; }

        // brief-lcp-5 R-lcp5-4 — a 3D .cem on a layout builds its component ports in a 3D view of it: reported as a .cem naming a
        // 3D view reports them, under the .cem's own ids, and their warnings not again among the problem's.
        var parts = generated.ComponentPorts;
        if (parts is not null)
        {
            foreach (var p in parts.Ports.Where(p => p.Ok)) f.Add(CliDiagnostics.CheckCemComponentPort(path, C3dComponentPorts.Describe(p)));
            foreach (string w in parts.Warnings) f.Add(CliDiagnostics.CheckCemComponentPortWarning(path, w));
        }
        foreach (string note in generated.Notes) f.Add(CliDiagnostics.CheckEmNote(path, note));
        foreach (string warning in generated.Warnings)
            if (parts?.Warnings.Contains(warning) != true) f.Add(CliDiagnostics.CheckEmFinding(path, warning, true));
        // brief-dfr35-1 R-dfr35-1-7 — the short edges: no board tried with one stopped Gmsh, so said here and not among the warnings.
        foreach (string note in CircuitRF.Design.Layout.Em3d.Em3dGeometryCheck.NoteSentences(generated.GeometryFindings)) f.Add(CliDiagnostics.CheckEmNote(path, note));
        if (generated.Problem is not { } problem)
        {
            f.Add(CliDiagnostics.CheckEmRefused(path, generated.Refusal ?? "the 3D problem could not be built."));
            return;
        }

        var problems = problem.Validate().ToList();
        // R-em3d22-2b — Palace has no floating electrostatic conductor; the run refuses before Gmsh, so check does.
        if (problems.Count == 0 && problem.IsStatic &&
            CircuitRF.Design.Em3d.PalaceConfigWriter.StaticRefusal(problem) is { } floating)
            problems.Add(floating);
        // brief-em3d-31 / brief-em3d-116 — what each chosen solver's own lowering would say of the built problem
        // (Em3dSolverDescriptor.Check): openEMS refuses a radiation pattern's surface in a box too tight for it and a wave
        // port it cannot build, Palace goes on without a pattern and says why.
        foreach (var solver in CircuitRF.Design.Em3d.Em3dSolvers.Of(setup.Solver3D))
        {
            if (problems.Count > 0) break;
            var said = solver.Check(problem, setup, path);
            foreach (string note in said.Notes) f.Add(CliDiagnostics.CheckEmNote(path, note));
            problems.AddRange(said.Refusals);
        }
        foreach (string p in problems) f.Add(CliDiagnostics.CheckEmRefused(path, p));
        if (problems.Count == 0)
            f.Add(CliDiagnostics.CheckEmWouldRun(path,
                problem.IsStatic
                    ? $"3D {problem.Type} problem for {setup.Solver3D}: {problem.Solids.Count} solid(s), {problem.Sheets.Count} " +
                      $"sheet(s), {problem.Terminals.Count} terminal(s)"
                    : $"3D problem for {setup.Solver3D}: {problem.Solids.Count} solid(s), {problem.Sheets.Count} " +
                      $"sheet(s), {problem.Ports.Count} port(s)", 0));
    }

    private static void CheckAssemblyRules(string path, Findings f)
    {
        WasmFile wasm;
        try { wasm = WasmPersistence.LoadFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // The `.wasm`'s own predicate parser is the authority on its rules — the same one the DRC
        // engine compiles them with, so a rule that checks clean here is one the engine will run.
        foreach (var (_, rule) in wasm.AllRules())
            if (!DrcPredicateParser.TryParse(rule.Expression, out _, out var error))
                f.Add(CliDiagnostics.CheckAssemblyRuleInvalid(path, rule.Name, error ?? "unparseable"));
    }

    // ── one Touchstone file ──────────────────────────────────────────────────

    /// <summary>
    /// <c>check part.s2p</c> — is this S-parameter file sound?
    ///
    /// <para><b>What is being checked is DATA, not a design</b>, and that changes the severity rule
    /// in one place worth stating. Passivity, reciprocity and causality are properties a file is
    /// normally ASSUMED to have, but nothing in Touchstone says what the part is: an amplifier has
    /// gain, a circulator is not reciprocal, and neither is a defect. So those three are warnings
    /// carrying the measured number and the frequency, and the caller decides what they mean for
    /// the part it thinks it has. Only the unambiguous defects — unreadable, a port count that
    /// contradicts the file's own name, a frequency axis that is not sorted, a reference impedance
    /// no renormalisation can use — are errors.</para>
    ///
    /// <para><b>Every measurement is <c>RfCore.Data.TouchstoneHealth</c>'s.</b> Nothing here
    /// computes; the same reason every other arm of this verb defers to the validator the
    /// application itself uses. A rule living only in the CLI is one the plot would not enforce.
    /// </para>
    /// </summary>
    /// <summary>
    /// A data display (<c>.cdd</c>): does it parse, does it hold anything, and do the result files
    /// it names exist?
    ///
    /// <para><b>A missing result is a NOTE, never an error</b>, and that is the whole shape of this
    /// arm. A display is a view of a run, the run is not a document, and <c>examples/</c> ships
    /// displays with no results beside them on purpose — reporting that as a defect would make
    /// `check` exit 1 on a workspace with nothing wrong with it, which is how a check stops being
    /// run. What IS a defect is a document that cannot be read or that draws nothing, and those are
    /// reported as themselves.</para>
    ///
    /// <para>The reader is <see cref="DataDisplayConfig"/> through <see cref="DataDisplayJson"/> —
    /// the same deserialization <c>render</c> and the display window perform, so a file that opens
    /// checks clean and one that does not is named here rather than at the moment somebody
    /// double-clicks it.</para>
    /// </summary>
    /// <summary>
    /// A railRF document. <b>No validation logic of its own</b>, on this verb's own rule: the
    /// document's refusals are <c>RailDocumentIo</c>'s — the same reader the window opens one with —
    /// and the solve order is <c>RailOrder</c>'s, which is what a rail set's correctness actually
    /// consists of. A rule living only in <c>check</c> is a rule the application does not enforce.
    /// </summary>
    private static void CheckRail(string path, Findings f)
    {
        RailDocument doc;
        try { doc = RailDocumentIo.LoadFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        var order = RailOrder.Resolve(doc);
        if (order.Refusal is { } refusal) f.Add(CliDiagnostics.CheckRailOrder(path, refusal));
        else f.Add(CliDiagnostics.CheckRailSummary(
                 path, doc.Rails.Count,
                 order.Order.Count > 0 ? string.Join(" → ", order.Order) : "(none)"));

        foreach (var rail in doc.Rails)
            if (rail.ReferenceLayer is null)
                f.Add(CliDiagnostics.CheckRailNoReferenceLayer(path, rail.Name));
    }

    /// <summary>
    /// A Smith Chart design (brief-smith-1-document.md R-smith1-8).
    ///
    /// <para><b>It writes no validation of its own</b>, which is §2's standing rule for this verb:
    /// every finding here is <c>SmithDesign.Refusal</c>'s, reached through the same
    /// <c>SmithDesignIo.LoadFromFile</c> the window opens a document with. A rule living only in
    /// `check` is a rule the application does not enforce.</para>
    ///
    /// <para><b>It is here at brief 1 rather than brief 10 because the DEFAULT arm is not
    /// harmless.</b> A kind with no case falls to <c>CheckUnknownKind</c>, which is an ERROR reading
    /// "nothing circuitRF reads is named this" — so `check` would have exited 1 on a perfectly good
    /// `.csmith` of a type the same change had just registered with three operating systems.</para>
    /// </summary>
    private static void CheckSmith(string path, Findings f)
    {
        SmithDesign design;
        try { design = SmithDesignIo.LoadFromFile(path); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        f.Add(CliDiagnostics.CheckSmithSummary(
            path, design.Elements.Count, design.Generator.Rows.Count, design.DesignFrequencyHz));
    }

    /// <summary>
    /// A <c>.cmodel</c>, through <see cref="CmodelFile.Read(string)"/> — the reader a placed
    /// <c>ComposedFET</c> loads it with, which validates before it returns. No rule of its own: the
    /// refusal printed is the validator's sentence, naming the field.
    /// </summary>
    private static void CheckComposedModel(string path, Findings f)
    {
        ComposedModel model;
        try { model = CmodelFile.Read(path); }
        catch (CmodelFormatException ex) { f.Add(CliDiagnostics.CheckComposedModelRefused(path, ex.Message)); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        var t = model.Temperature;
        f.Add(CliDiagnostics.CheckComposedModelSummary(
            path, model.BasePeriphery.Fingers, model.BasePeriphery.UnitWidth,
            t.Mode == TemperatureMode.Single
                ? $"{t.T0.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} °C only"
                : $"{t.Tmin.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} to {t.Tmax.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} °C"));
    }

    /// <summary>
    /// A <c>.cmc</c>, through <see cref="CmcFile.Read(string)"/> — the reader that validates before it
    /// returns, naming the field it refuses. Its warnings are <see cref="CmcFile.Warnings"/>'. The data is
    /// not imported: check reads nothing but the document, and a missing source is reported by the step
    /// that needs it.
    /// </summary>
    private static void CheckComposerDocument(string path, Findings f)
    {
        CmcDocument doc;
        try { doc = CmcFile.Read(path); }
        catch (CmcFormatException ex) { f.Add(CliDiagnostics.CheckComposerDocumentRefused(path, ex.Message)); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        foreach (string w in CmcFile.Warnings(doc)) f.Add(CliDiagnostics.CheckComposerDocumentWarning(path, w));
        f.Add(CliDiagnostics.CheckComposerDocumentSummary(path, doc.Kind.ToString(), doc.DataSets.Count));
    }

    private static void CheckDataDisplay(string path, Findings f)
    {
        DataDisplayConfig? config;
        try
        {
            config = System.Text.Json.JsonSerializer.Deserialize<DataDisplayConfig>(
                File.ReadAllText(path), DataDisplayJson.Options);
        }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        if (config is null) { f.Add(CliDiagnostics.CheckUnreadable(path, "it is not JSON")); return; }

        // v1 kept its plots at the top level and v2 keeps them in tabs. Both are read, for the
        // reason `render` reads both: the format is the contract and a caller did not choose the
        // version of a file somebody handed them.
        var tabs = config.Tabs.Count > 0
            ? config.Tabs
            : config.Plots.Count > 0
                ? [new TabConfig { Name = "Tab 1", Plots = config.Plots }]
                : (IReadOnlyList<TabConfig>)Array.Empty<TabConfig>();

        int plots  = tabs.Sum(tb => tb.Plots.Count);
        int traces = tabs.Sum(tb => tb.Plots.Sum(p => p.Traces.Count));

        if (plots == 0) { f.Add(CliDiagnostics.CheckDataDisplayEmpty(path)); return; }

        var (sources, searched) = CddSources.Describe(path, config, tabs);

        f.Add(CliDiagnostics.CheckDataDisplaySummary(path, tabs.Count, plots, traces, sources.Count));

        foreach (var (reference, resolved) in sources)
            if (resolved is null)
                f.Add(CliDiagnostics.CheckDataDisplaySourceNotRun(
                    path, reference, string.Join(", ", searched)));

        // A trace that names neither a cube nor an expression draws nothing and says nothing about
        // why — the one defect in a display that survives being opened. A trace with a source and no
        // cube is not that: it reads the source's network by its MatrixType (S, Y, Z and the metrics
        // derived from them), which is how the trace card writes every network trace.
        foreach (var tab in tabs)
        foreach (var pc in tab.Plots)
        foreach (var tc in pc.Traces)
            if (string.IsNullOrWhiteSpace(tc.SourcePath)
                && string.IsNullOrWhiteSpace(tc.CubeName) && string.IsNullOrWhiteSpace(tc.Expression)
                && tc.WsProbe is null && tc.ContourTrace is null && tc.SummaryColumn is null)
                f.Add(CliDiagnostics.CheckDataDisplayEmptyTrace(
                    path, pc.CustomTitle is { Length: > 0 } ? pc.CustomTitle : tab.Name));
    }

    /// <summary>
    /// An OASIS file's header, through the gdstk worker: its cells, its counts and the layers it names. Read-only
    /// and nothing is created — <see cref="GdstkImport.Header"/> transfers no geometry. With no worker the file is
    /// named and a warning says why it was not read (D6).
    /// </summary>
    private static void CheckOasis(string path, Findings f)
    {
        if (StreamRoute.OasisGdstk.Unavailable() is { } reason)
        {
            f.Add(CliDiagnostics.CheckOasisNoWorker(path, reason));
            return;
        }

        GdstkImport.HeaderResult h;
        try { h = GdstkImport.Header(path, GdstkFormat.Oasis, token: RunHost.Cancellation); }
        catch (GdstkException e) { f.Add(CliDiagnostics.CheckUnreadable(path, e.Diagnostic.Render())); return; }

        var tops = h.Cells.Where(c => c.Top).Select(c => c.Name).ToList();
        var names = h.LayerNames.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();
        f.Add(CliDiagnostics.CheckOasisSummary(
            path, h.Cells.Count, tops.Count == 0 ? "none" : string.Join(", ", tops),
            h.Cells.Sum(c => c.Polygons), h.Cells.Sum(c => c.Paths), h.Cells.Sum(c => c.Labels),
            h.Cells.Sum(c => c.References), h.SourceDbuPerMicron.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            names.Count == 0 ? "no named layers" : $"named layers {string.Join(", ", names)}"));
    }

    /// <summary>
    /// brief-img-2 R-im2-6 — a picture: its kind and confidence as an INFO, exit 0. A picture that does not decode is
    /// the decoder's own refusal (it names the formats read and says to export as PNG), as an error.
    /// </summary>
    private static void CheckPicture(string path, Findings f)
    {
        if (PictureKinds.Read(path, out string? refusal) is not { } p)
        {
            f.Add(CliDiagnostics.CheckUnreadable(path, refusal ?? "not a picture"));
            return;
        }
        var raster = p.Source.Raster;
        string format = p.Source.Format.ToUpperInvariant();
        f.Add(p.Kind.Kind == CircuitRF.Design.Imaging.DrawingKind.None
            ? CliDiagnostics.CheckPictureNoDrawing(path, format, raster.Width, raster.Height, p.Kind.Reason!, p.Kind.Confidence)
            : CliDiagnostics.CheckPictureKind(path, format, raster.Width, raster.Height, p.Kind.Name, p.Kind.Confidence));
    }

    /// <summary>brief-cosim-pattern-1 — a result record reads, and the files it names exist and carry its stamp
    /// (<see cref="CircuitRF.Design.Layout.Em.EmResultRecord.Verify"/>, the one rule for that).</summary>
    private static void CheckEmResult(string path, Findings f)
    {
        var (record, refusal) = CircuitRF.Design.Layout.Em.EmResultRecord.Load(path);
        if (record is null) { f.Add(CliDiagnostics.CheckUnreadable(path, refusal!)); return; }
        f.Add(CliDiagnostics.CheckEmResultSummary(path, record.Solver.Name, record.Ports.Count, record.Frequencies.SweepHz.Count,
                                                  record.Setup is null ? "settings not recorded" : "settings recorded"));
        foreach (var d in record.Verify(path)) f.Add(CliDiagnostics.CheckEmResultDisagrees(path, d.Sentence));
    }

    private static void CheckTouchstone(string path, Findings f)
    {
        SNP snp;
        try { snp = TouchstoneIO.ReadFile(path, readComments: false); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        var h = TouchstoneHealth.Analyze(snp);

        f.Add(CliDiagnostics.CheckTouchstoneSummary(
            path, h.Ports, h.FrequencyCount,
            h.FrequencyCount == 0 ? "no points" : $"{Hz(h.FirstFrequencyHz)} to {Hz(h.LastFrequencyHz)}",
            Ohms(h.Z0)));

        if (h.FrequencyCount == 0)
        {
            f.Add(CliDiagnostics.CheckUnreadable(path, "the file carries no frequency points"));
            return;
        }

        // The structural rules are TouchstoneFacts' — the ones the S-Parameter Viewer reports — so the
        // viewer and this verb cannot disagree about what a defect is. The name is a claim about the
        // contents, and the reader will honour the extension over the data for N > 2 (whose rows span
        // several physical lines). A mismatch is therefore not cosmetic: it means the matrices were
        // assembled the wrong shape.
        var facts = TouchstoneFacts.Describe(snp, path);
        if (facts.PortsDisagreeWithName)
            f.Add(CliDiagnostics.CheckTouchstonePortMismatch(path, facts.ExtensionPorts!.Value, facts.Ports));

        if (facts.FrequenciesOutOfOrder)
            f.Add(CliDiagnostics.CheckTouchstoneFrequencyOrder(
                path, facts.FirstNonIncreasingIndex, Hz(snp.Frequencies[facts.FirstNonIncreasingIndex])));

        if (facts.Z0Unusable)
            f.Add(CliDiagnostics.CheckTouchstoneBadZ0(path, Ohms(h.Z0)));
        else if (Math.Abs(h.Z0.Real - 50.0) > 1e-9 || Math.Abs(h.Z0.Imaginary) > 1e-9)
            f.Add(CliDiagnostics.CheckTouchstoneNonStandardZ0(path, Ohms(h.Z0)));

        if (h.WorstSigmaMax > 1.0 + TouchstoneHealth.PassivityTolerance)
            f.Add(CliDiagnostics.CheckTouchstoneNotPassive(
                path, h.WorstSigmaMax.ToString("0.######"), Hz(h.WorstSigmaMaxFrequencyHz)));

        if (h.WorstReciprocityError > TouchstoneHealth.ReciprocityTolerance)
            f.Add(CliDiagnostics.CheckTouchstoneNotReciprocal(
                path, h.WorstReciprocityError.ToString("0.######"), Hz(h.WorstReciprocityFrequencyHz)));

        switch (h.Causality)
        {
            case CausalityVerdict.PrecursorEnergyHigh:
                f.Add(CliDiagnostics.CheckTouchstoneNotCausal(
                    path, h.CausalityPrecursorRatio.ToString("P1")));
                break;
            case CausalityVerdict.NotEvaluated:
                f.Add(CliDiagnostics.CheckTouchstoneCausalitySkipped(
                    path, h.CausalitySkipReason ?? "the test does not apply to this sweep"));
                break;
        }
    }

    /// <summary>A frequency with an SI prefix. Shared by `check` and `explain` so one file cannot
    /// report a band the other spells differently.</summary>
    internal static string Hz(double hz) =>
        !double.IsFinite(hz) ? "?"
        : Math.Abs(hz) >= 1e9 ? $"{hz / 1e9:0.######} GHz"
        : Math.Abs(hz) >= 1e6 ? $"{hz / 1e6:0.######} MHz"
        : Math.Abs(hz) >= 1e3 ? $"{hz / 1e3:0.######} kHz"
        :                       $"{hz:0.######} Hz";

    /// <summary>A reference impedance. Complex only when it genuinely is.</summary>
    internal static string Ohms(System.Numerics.Complex z) =>
        Math.Abs(z.Imaginary) < 1e-12 ? $"{z.Real:0.###} Ω" : $"{z.Real:0.###}{z.Imaginary:+0.###;-0.###}j Ω";

    private static void CheckNetlist(string path, Findings f)
    {
        Library   lib;
        TestBench tb;
        IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable> parts;
        try { (lib, tb) = CnlTechnologyBinding.ReadFile(path, out parts); }
        catch (Exception ex) { f.Add(CliDiagnostics.CheckUnreadable(path, ex.Message)); return; }

        // A vendor part a SpiceModel line places is a tunable (C1.Part) here as in a schematic (EMB-5 R-emb5-13).
        Elaborate(path, tb, lib, f, parts.Count > 0 ? TunableCatalog.FromNetlist(tb, lib, parts: parts) : null);
    }

    /// <summary>
    /// Elaboration and chain selection — the two questions a document holding a circuit has that a
    /// document holding artwork does not. Shared by `.cnl` and `.csch` because the `.csch` becomes
    /// exactly the same <c>TestBench</c> and <c>Library</c> on the way (AUT-2), so checking it
    /// differently would mean checking something the run verbs never see.
    /// </summary>
    /// <summary>
    /// An elaboration for a read-only verb: against the base a run of <paramref name="path"/> resolves its files against
    /// (an EM instance's <c>EmCell=</c> is relative to it, as an SnP <c>File=</c> is), and in counting mode (brief-sdl-6
    /// D5), so an instance set to EM is recorded and never solved. Without the base the cell was looked for under the
    /// process's working directory: not found, it was an error the run does not have; found, it was solved.
    /// </summary>
    internal static ElaboratedNetlist ElaborateReadOnly(Library lib, TestBench tb, string? path)
    {
        using (EmEvaluationScope.Begin(countOnly: true))
            return new Elaborator(lib) { BaseDirectory = path is null ? null : CircuitSource.ReferenceBaseOf(path) }.Elaborate(tb);
    }

    private static void Elaborate(string path, TestBench tb, Library lib, Findings f, TunableCatalog? catalog = null)
    {
        CheckEm(path, tb, lib, f);

        ElaboratedNetlist nl;
        // Counting mode (brief-sdl-6 D5): an instance set to EM is recorded, never solved — check solves nothing.
        try { nl = ElaborateReadOnly(lib, tb, path); }
        catch (Exception ex)
        {
            f.Add(CliDiagnostics.CheckElaborationFailed(path, ex.Message));
            // What reading decided still stands when elaboration then fails, and it is often the
            // cause: a skipped `var x=1` is exactly what leaves `x` unresolved. The elaborator forwards
            // these only on success, so a failed one would otherwise report the effect and drop the
            // cause.
            foreach (var w in tb.ReadWarnings) f.Add(CliDiagnostics.CheckElaborationWarning(path, w));
            CheckTuning(path, tb, lib, null, catalog, f);
            return;
        }

        using (nl)
        {
            CheckTuning(path, tb, lib, nl, catalog, f);

            // brief-sens-2 R-sens2-3: Reciprocal=tied on a file that is not reciprocal — allowed, warned once.
            static bool HasEntries(Instance i) => i.Reference.Equals("SnP", StringComparison.OrdinalIgnoreCase)
                                                  || i.Reference.Equals(CircuitRF.Core.Devices.EmFieldDataRecord.TypeName, StringComparison.OrdinalIgnoreCase);
            if (tb.Instances.Any(HasEntries) || lib.Cells.Any(c => c.Instances.Any(HasEntries)))
                foreach (var w in Design.Statistics.StatisticsValidator.SnpTieWarnings(catalog ?? TunableCatalog.FromNetlist(tb, lib)))
                    f.Add(CliDiagnostics.CheckTuningFinding(path, w));

            foreach (var w in nl.Warnings) f.Add(CliDiagnostics.CheckElaborationWarning(path, w));
            foreach (var n in nl.Notes)    f.Add(CliDiagnostics.CheckElaborationNote(path, n));

            // R-wsp1-12(b): a WSProbe with one net on both terminals. The GUI's own series-probe
            // insertion cut prevents this for a drawn probe; this is its headless twin.
            foreach (var ec in nl.Components)
                if (ec.Model is WSProbeModel && ec.Nodes.Length >= 2 && ec.Nodes[0] == ec.Nodes[1])
                    f.Add(CliDiagnostics.CheckWsProbeShorted(
                        path, ec.InstancePath,
                        ec.Nodes[0] < nl.Nodes.Count ? nl.Nodes.NameOf(ec.Nodes[0]) : $"node {ec.Nodes[0]}"));

            // NZ-1: Noise=no on a part that can never be noisy is accepted and does nothing, which is worth a word.
            foreach (var ec in nl.Components)
                if (ec.NoiseOff && !CircuitRF.Engine.SParameterNoise.CanBeNoisy(ec))
                    f.Add(CliDiagnostics.CheckNoiseSwitchInert(path, ec.InstancePath));

            // NZ-7 follow-up: Noise=yes on a source. One with an impedance (P1Tone, PnTone, MOD_Source) has it thermal in an
            // envelope run and stays noiseless in the analyses whose noise figure already counts the source termination; an
            // ideal source has no impedance, so the switch does nothing there.
            var noiseAnalyses = tb.Analyses.Where(a => a is SParameterAnalysis { Noise: true } or HarmonicBalanceAnalysis { Noise: true }
                                                           or ILoadpullNoiseKeys { Noise: true }).Select(a => a.Name).ToList();
            foreach (var ec in nl.Components.Where(c => c.NoiseOn))
            {
                if (ec.Model is ModSourceModel or P1ToneModel or PnToneModel)
                {
                    if (noiseAnalyses.Count > 0) f.Add(CliDiagnostics.CheckNoisySourceSteadyState(path, ec.InstancePath, noiseAnalyses));
                }
                else if (ec.Model is IDriveScalable or VdcModel)
                    f.Add(CliDiagnostics.CheckNoisySourceIdeal(path, ec.InstancePath));
            }

            // NZ-7: a source's phase-noise mask is simulated by an envelope run only. A mask that does not read is an error
            // (the parser the run uses); one under steady-state analyses is INFO, never an error.
            var steady = tb.Analyses.Where(a => a is HarmonicBalanceAnalysis or LoadpullAnalysis or LoadpullPursuitAnalysis
                                                    or SParameterAnalysis).Select(a => a.Name).ToList();
            foreach (var ec in nl.Components)
            {
                if (!ec.Parameters.TryGetValue(CircuitRF.Core.Elaboration.Elaborator.PhaseNoiseParamName, out var pn)
                    || pn.Kind != CircuitRF.Core.Expressions.ValueKind.String || string.IsNullOrWhiteSpace(pn.AsString())) continue;
                try { RfCore.Signals.PhaseNoiseMask.Parse(pn.AsString()); }
                catch (FormatException ex) { f.Add(CliDiagnostics.CheckPhaseNoiseMask(path, ec.InstancePath, ex.Message)); continue; }
                if (steady.Count > 0) f.Add(CliDiagnostics.CheckPhaseNoiseSteadyState(path, ec.InstancePath, steady));
            }

            foreach (var bridge in CircuitRF.Design.Em3d.EmFieldBlocks.GapPortBridges(nl))
                f.Add(CliDiagnostics.CheckPartBetweenGapPorts(path, bridge.Part, bridge.Block, bridge.PortA, bridge.PortB));

            // Will anything dispatch? Two questions, and only the second one uses ChainSelector.
            //
            // First: does the document declare an analysis AT ALL — the GUI's own NoAnalysis test.
            // A cell's schematic is not supposed to declare one and a `.cnl` written to be included
            // by another is not either, so this is a warning rather than an error.
            if (!CircuitSource.DeclaresARunnableAnalysis(tb))
            {
                f.Add(CliDiagnostics.CheckNoRunnableAnalysis(
                    path, "The document declares no analysis."));
                return;
            }

            // Second: of the kinds that go through chain selection, does a DECLARED one fail
            // to select? Asked through ChainSelector — the function the run verbs themselves select
            // with — so "check says it will run" and "it runs" cannot part company (Gate 4). A kind
            // the netlist declares NONE of is not a finding; a kind it declares and cannot run is —
            // but only when NOTHING runs, which is what the finding says. A disabled HB chain beside
            // an enabled S-parameter or envelope analysis is a bench switched to that analysis, so
            // every kind counts here (AnalysisChain.RunnableTops), not only the chain-selected ones.
            if (AnalysisChain.RunnableTops(tb).Count > 0) return;
            foreach (var (isBase, label, hint, _) in Explain.AnalysisKinds)
            {
                if (!tb.Analyses.Any(isBase)) continue;
                var sel = ChainSelector.Select(tb, null, isBase, label, hint);
                if (sel.Selected is null && sel.Why is { } why)
                    f.Add(CliDiagnostics.CheckNoRunnableAnalysis(path, why));
            }
        }
    }

    /// <summary>
    /// The tuning setup's rules (TuningValidator) — the same code the Tuning and Optimizer windows
    /// refuse on, so a setup that passes here is one the application accepts.
    /// </summary>
    private static void CheckTuning(string path, TestBench tb, Library lib, ElaboratedNetlist? nl,
                                    TunableCatalog? catalog, Findings f)
    {
        if (tb.Tuning is not { IsEmpty: false } setup) return;
        catalog ??= TunableCatalog.FromNetlist(tb, lib);
        var findings = TuningValidator.Validate(tb, catalog, nl, lib);
        foreach (var finding in findings)
            f.Add(CliDiagnostics.CheckTuningFinding(path, finding));

        // brief-tuneopt-11 R-to11-7: an optimize line that would be refused at run time — nothing to
        // optimize, no enabled goal, three parts of one complex value, an algorithm this build lacks —
        // is refused here in the run's own words, because the question goes to the run's own
        // constructor (no evaluation happens there). Asked only when the rules above found no error,
        // since the run refuses on the first of those itself and would only say it twice.
        if (findings.Any(d => d.Severity == DiagnosticSeverity.Error)) return;
        if (setup.Optimizer is not null)
        {
            var run = OptimizationRun.Create(PreparedCircuit.FromBench(lib, tb, null, catalog.PartTunables));
            if (run.Refusal is { } refusal) f.Add(CliDiagnostics.CheckTuningFinding(path, refusal));
        }

        // brief-yield-5 R-ya5-8: a statistical setup the yield verb would refuse — nothing varies, a yield run with
        // no use=yield|both goal — is refused here in the run's own words, asked of StatisticalRun.Create (which
        // evaluates nothing). A setup that names a target or a yield-only goal is asked as a yield run; one that
        // only carries tolerances or a statistics line, as a Monte Carlo.
        bool yieldRun = setup.Statistics?.Target is not null || setup.Goals.Any(g => g.Enabled && g.Use == GoalUse.Yield);
        if (yieldRun || setup.Statistics is not null || setup.Variables.Any(e => e.Distribution != StatDistribution.None))
        {
            var stat = Design.Statistics.StatisticalRun.Create(PreparedCircuit.FromBench(lib, tb, null, catalog.PartTunables),
                new Design.Statistics.StatisticalOptions
                {
                    Mode = yieldRun ? Design.Statistics.StatisticalMode.Yield : Design.Statistics.StatisticalMode.MonteCarlo,
                });
            if (stat.Refusal is { } refusal) f.Add(CliDiagnostics.CheckTuningFinding(path, refusal));
        }

        // brief-yield-11 R-ya11-1: a center line the run would refuse — no opt=1 entry, no yield goal, nothing that
        // varies, an algorithm centering does not offer — in the run's own words, from its constructor.
        if (setup.Centering is not null)
        {
            var center = Design.Statistics.CenteringRun.Create(PreparedCircuit.FromBench(lib, tb, null, catalog.PartTunables));
            if (center.Refusal is { } refusal) f.Add(CliDiagnostics.CheckTuningFinding(path, refusal));
        }

        // brief-yield-14 R-ya14-3: a doe line the run would refuse — no factor, too many for full2, a resolution the
        // fraction table does not hold for k, levels that do not go with the factors — from its constructor.
        if (setup.Doe is not null)
        {
            var doe = Design.Statistics.DoeRun.Create(PreparedCircuit.FromBench(lib, tb, null, catalog.PartTunables));
            if (doe.Refusal is { } refusal) f.Add(CliDiagnostics.CheckTuningFinding(path, refusal));
        }

        // brief-sens-1 R-sens1-6: a sensitivity line the run would refuse — no factor, no goal and no measure — from its
        // constructor, which simulates nothing.
        if (setup.Sensitivity is not null)
        {
            var sens = Design.Statistics.SensitivityRun.Create(PreparedCircuit.FromBench(lib, tb, null, catalog.PartTunables));
            if (sens.Refusal is { } refusal) f.Add(CliDiagnostics.CheckTuningFinding(path, refusal));
        }
    }
}
