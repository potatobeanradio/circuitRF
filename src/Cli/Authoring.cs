// `circuitrf new workspace`, `circuitrf new cell` and `circuitrf import part` — creating correct
// INITIAL documents, headlessly (brief-automation-3-authoring-verbs.md).
//
// WHAT THESE VERBS ARE NOT. There is no `place-instance`, no `add-wire`, no `set-parameter`
// (R-aut0-5). Once a document exists, the way to change it is to WRITE it: every format circuitRF
// owns is readable, culture-invariant JSON and the format IS the contract (R-aut-5). What was
// missing was a way to get the FIRST correct one without a display, which is all these three do.
//
// WHY THERE IS ALMOST NO CODE HERE. R-aut3-1: each verb calls a capability the GUI's own command
// also calls, and after this brief there is exactly one implementation of each —
// `WorkspaceCreate.Create`, `CellCreate`, `ComponentImport.Import`. A verb that re-implemented what
// a view model does is the failure this file exists to avoid: the two would diverge silently, and
// the first symptom would be a workspace created headlessly that the GUI treats as malformed. So
// what IS here is argument parsing, refusals, and reporting — the shell of a verb and nothing else.
//
// THE ONE RULE THAT DECIDES EVERY DEFAULT (R-aut3-3, R-aut3-11). Whatever the GUI's dialog
// pre-selects, the verb selects with no flag; anything the dialog would have ASKED is a refusal that
// prints the inference and the flags that answer it, never a guess. `circuitrf convert` already
// fixed this rule for interchange imports and it applies here unchanged.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Cli;

public static class Authoring
{
    // ── new ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-aut3-13: <c>new</c> is ONE verb with a noun, not three verbs. The command surface has a
    /// standing cost and few broad verbs beat many narrow ones (R-aut-9) — adding <c>new schematic</c>
    /// later is a noun here, not a fourth top-level verb.
    /// </summary>
    public static int RunNew(string[] args)
    {
        if (args.Length == 0)
        {
            JsonRun.Report(CliDiagnostics.NewNounRequired());
            return NewUsage();
        }

        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = "new " + noun;

        return noun switch
        {
            "workspace" => NewWorkspace(args[1..]),
            "cell"      => NewCell(args[1..]),
            "view3d"    => NewView3d(args[1..]),
            _           => UnknownNoun(noun),
        };
    }

    private static int UnknownNoun(string noun)
    {
        JsonRun.Report(CliDiagnostics.NewUnknownNoun(noun));
        return NewUsage();
    }

    // ── new workspace ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four operations <c>WorkspaceViewModel.NewWorkspace</c> performs after its dialog returns,
    /// through the one function that now performs them for both (R-aut3-1/R-aut3-2).
    /// </summary>
    private static int NewWorkspace(string[] args)
    {
        string? dir = null, name = null, tech = null, lengthUnitText = null;
        bool techStated = false;
        var vendorLibraries = new List<(string Name, string Path)>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--tech" when i + 1 < args.Length: tech = args[++i]; techStated = true; break;
                case "--length-unit" when i + 1 < args.Length: lengthUnitText = args[++i]; break;
                // brief vendorlib-1 R-vl1-4c: the GUI passes the user's own list; headless there is no
                // user list, so nothing is referenced unless it is named here.
                case "--vendor-library" when i + 1 < args.Length:
                {
                    string spec = args[++i];
                    int eq = spec.IndexOf('=');
                    if (eq <= 0 || eq == spec.Length - 1) return JsonRun.Fail(CliDiagnostics.NewVendorLibraryMalformed(spec));
                    vendorLibraries.Add((spec[..eq].Trim(), Path.GetFullPath(spec[(eq + 1)..].Trim())));
                    break;
                }
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.NewUnknownOption(args[i])); return NewUsage(); }
                    if (dir is not null) { JsonRun.Report(CliDiagnostics.NewMultipleDirectories()); return NewUsage(); }
                    dir = args[i];
                    break;
            }
        }

        if (dir is null) { JsonRun.Report(CliDiagnostics.NewWorkspaceDirRequired()); return NewUsage(); }
        JsonRun.InputPath = dir;

        // <dir> alone IS the workspace directory; with --name it is the PARENT the named workspace is
        // created in. Both spellings are how a person actually types this, and neither is ambiguous:
        // the flag is what says which was meant.
        string parentDir, wsName;
        if (name is not null)
        {
            parentDir = Path.GetFullPath(dir);
            wsName    = name;
        }
        else
        {
            string full = Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            parentDir = Path.GetDirectoryName(full) ?? full;
            wsName    = Path.GetFileName(full);
        }

        // R-aut3-9's rule, applied to a workspace: NameValidator's, never a second set. A headless
        // caller must not be able to create a name the GUI rejects.
        if (NameValidator.Validate(wsName) is { } badName)
            return JsonRun.Fail(CliDiagnostics.NewInvalidName("workspace", wsName, badName));

        // R-aut3-3: no --tech is the DIALOG's pre-selected entry, not "none" and not the first in the
        // list. `--tech none` is how a caller asks for the dialog's own "None" row.
        string? techId = techStated
            ? (tech!.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : tech)
            : WorkspaceCreate.DefaultTechnologyId;

        // R-aut3-5: an unknown id is a refusal that lists the valid ones — never a fallback to the
        // default, because a caller that asked for a specific process and silently got another has a
        // wrong design and no way to know.
        if (techId is not null && TechnologyCatalog.Find(techId) is null)
            return JsonRun.Fail(CliDiagnostics.NewUnknownTechnology(
                techId, string.Join(", ", TechnologyCatalog.All.Select(e => e.Id))));

        // brief-dfr22-1 R-dfr22-1-3: the unit the copied technology is written with — the flag spelling of
        // the GUI's preferred length unit. Absent is the technology's own, which is what the dialog does
        // when the preference is Automatic. Parsed and passed; WorkspaceCreate writes it.
        LayoutUnit? lengthUnit = null;
        if (lengthUnitText is not null)
        {
            string unitWord = lengthUnitText.ToLowerInvariant();
            lengthUnit = LengthUnitWords.TryGetValue(unitWord == "µm" ? "um" : unitWord, out var unit) ? unit : null;
            if (lengthUnit is null) return JsonRun.Fail(CliDiagnostics.NewUnknownLengthUnit(lengthUnitText));
            if (techId is null) return JsonRun.Fail(CliDiagnostics.NewLengthUnitWithoutTechnology());
        }

        // R-aut11-4: a missing PARENT is created, rather than refused. The refusal it replaces was
        // defensible and it was discovered by hitting it — `create` under a path whose parent does
        // not exist failed with "No such directory", and a client with no file tools of its own had
        // nowhere to go from there. Creating an intermediate directory is not the destructive act
        // the refusal was guarding against: nothing is overwritten, and the workspace itself is
        // still refused if it already exists.
        //
        // A failure here is reported as itself (a read-only ancestor, a file in the way), naming the
        // directory that could not be made.
        if (!Directory.Exists(parentDir))
        {
            try { Directory.CreateDirectory(parentDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
                { return JsonRun.Fail(CliDiagnostics.NewParentNotCreated(parentDir, ex.Message)); }

            Console.Error.WriteLine($"[circuitRF] created the parent directory {parentDir}");
            JsonRun.Note(CliDiagnostics.NewParentCreated(parentDir));
        }

        // R-sl2-13, and the same sentence the GUI shows: refuse HERE, naming the directory, rather
        // than part-way through — the steps below have no rollback.
        if (WorkspaceCreate.UnwritableParentRefusal(parentDir, "The workspace was not created") is { } refusal)
            return JsonRun.Fail(CliDiagnostics.NewRefused(refusal));

        try
        {
            var created = WorkspaceCreate.Create(parentDir, wsName, techId, lengthUnit, vendorLibraries);

            // R-aut1-3 / the brief's §1: a caller's next step is usually to read or rewrite one of
            // these, so the PATHS are the result and they go to stdout.
            Console.WriteLine(created.CwsPath);
            JsonRun.AddOutput("workspace", created.WorkspaceDir);
            JsonRun.AddOutput("cws", created.CwsPath);
            if (created.TechPath is { } tp)
            {
                Console.WriteLine(tp);
                JsonRun.AddOutput("ctech", tp);
                Console.Error.WriteLine($"[circuitRF] technology: {Path.GetFileName(tp)}");
            }
            else
            {
                Console.Error.WriteLine(
                    "[circuitRF] no technology — the workspace resolves to the fallback palette until one is added.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonRun.Fail(CliDiagnostics.NewRefused(ex.Message));
        }
    }

    // ── new cell ──────────────────────────────────────────────────────────────────────────────────

    private static int NewCell(string[] args)
    {
        string? where = null, cellName = null, viewsText = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--views" when i + 1 < args.Length: viewsText = args[++i]; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.NewUnknownOption(args[i])); return NewUsage(); }
                    if (where is null) where = args[i];
                    else if (cellName is null) cellName = args[i];
                    else { JsonRun.Report(CliDiagnostics.NewCellExtraArgument(args[i])); return NewUsage(); }
                    break;
            }
        }

        if (where is null || cellName is null) { JsonRun.Report(CliDiagnostics.NewCellArgsRequired()); return NewUsage(); }
        JsonRun.InputPath = where;

        if (ParseViews(viewsText) is not { } views) return 1;

        if (NameValidator.Validate(cellName) is { } badName)
            return JsonRun.Fail(CliDiagnostics.NewInvalidName("cell", cellName, badName));

        // A `.cws` is accepted as well as a directory, because that is what a caller has in hand after
        // `new workspace` — the workspace's own folder is what a cell is created in.
        string parentDir = Path.GetFullPath(where);
        if (File.Exists(parentDir) &&
            Path.GetFileName(parentDir).Equals(".cws", StringComparison.OrdinalIgnoreCase))
            parentDir = Path.GetDirectoryName(parentDir)!;

        if (!Directory.Exists(parentDir))
            return JsonRun.Fail(CliDiagnostics.NewParentNotFound(parentDir));

        if (Directory.Exists(Path.Combine(parentDir, cellName)))
            return JsonRun.Fail(CliDiagnostics.NewCellExists(cellName));

        if (WorkspaceCreate.UnwritableParentRefusal(parentDir, "The cell was not created") is { } refusal)
            return JsonRun.Fail(CliDiagnostics.NewRefused(refusal));

        // A `.clay` takes its display unit and snap from the technology in force where it is created —
        // the SAME walk-up `circuitrf em` and the GUI's New Layout do, so a headless cell and a
        // hand-made one agree. Resolved against the cell's own layout sub-folder, which is where the
        // file lands.
        // A `.c3d` takes its display unit and snap from the same technology (brief-em3d-41 R-em3d41-4),
        // and resolves it the same way, so one walk serves both.
        Technology? tech = null;
        if (views.HasFlag(CellViews.Layout) || views.HasFlag(CellViews.ThreeD))
        {
            string clayPath = Path.Combine(
                parentDir, cellName, CellFolder.SubFolderName(ViewType.Layout), cellName + ".clay");
            var (res, _) = TechnologyResolver.ResolveForDocument(null, clayPath, null, new TechnologyCache());
            foreach (var d in res.Diagnostics)
            { Console.Error.WriteLine($"warning: {d}"); JsonRun.Note(CliDiagnostics.EmSetupWarning(d)); }
            if (res.ResolvedPath is { } rp) Console.Error.WriteLine($"[circuitRF] technology: {rp}");
            tech = res.Tech;
        }

        try
        {
            var created = CellCreate.Create(parentDir, cellName, views, schematic: null, tech: tech);

            foreach (string path in created.Paths) Console.WriteLine(path);
            JsonRun.AddOutput("cell", created.CellDir);
            if (created.SchematicPath is { } sch) JsonRun.AddOutput("schematic", sch);
            if (created.SymbolPath    is { } sym) JsonRun.AddOutput("symbol", sym);
            if (created.LayoutPath    is { } lay) JsonRun.AddOutput("layout", lay);
            if (created.ThreeDPath    is { } t3d) JsonRun.AddOutput("3d", t3d);

            // R-aut3-7: a cell the resolver then reports as broken is worse than nothing, so say what
            // it resolves to rather than leaving the caller to open the GUI and find out.
            ReportPrimacy(created.CellDir);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonRun.Fail(CliDiagnostics.NewRefused(ex.Message));
        }
    }

    // ── new view3d ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// brief-emb-1 R-emb1-1 — Design ▸ New 3D View from Layout, headlessly: the document
    /// <see cref="C3dHierarchy.NewFromLayout"/> makes (the one place it is made, and the call the GUI's command makes), written
    /// with <see cref="CellCreate.WriteThreeDView"/>. The cell's own layout is placed as an instance with View Layout, its 3D
    /// setups are copied in as embedded setups and its port labels come in as ports, exactly as the GUI's does. The name the
    /// dialog asks for is <c>--name</c>, defaulting to the cell's; the workspace is the cell's nearest ancestor one.
    /// </summary>
    private static int NewView3d(string[] args)
    {
        string? cellDir = null, name = null;
        bool fromLayout = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from-layout": fromLayout = true; break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.NewUnknownOption(args[i])); return NewUsage(); }
                    if (cellDir is null) cellDir = args[i];
                    else { JsonRun.Report(CliDiagnostics.NewCellExtraArgument(args[i])); return NewUsage(); }
                    break;
            }
        }
        if (cellDir is null) { JsonRun.Report(CliDiagnostics.NewView3dArgs("a cell folder is required.")); return NewUsage(); }
        JsonRun.InputPath = cellDir;
        if (!fromLayout)
            return JsonRun.Fail(CliDiagnostics.NewView3dArgs(
                "say what the 3D view is made from: --from-layout places the cell's own layout. An empty 3D view is " +
                "'new cell <workspace> <name> --views 3d'."));

        string dir = Path.GetFullPath(cellDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!File.Exists(Path.Combine(dir, CellFolder.CcellFileName)))
            return JsonRun.Fail(CliDiagnostics.NewView3dArgs($"'{cellDir}' is not a cell folder (it holds no {CellFolder.CcellFileName})."));
        name ??= Path.GetFileName(dir);
        if (NameValidator.Validate(name) is { } badName)
            return JsonRun.Fail(CliDiagnostics.NewInvalidName("3D view", name, badName));
        string path = Path.Combine(CellFolder.SubFolderPath(dir, ViewType.ThreeD), name + CircuitRF.Design.ThreeD.C3dPersistence.Extension);
        if (File.Exists(path))
            return JsonRun.Fail(CliDiagnostics.NewRefused($"A file named '{name}{CircuitRF.Design.ThreeD.C3dPersistence.Extension}' already exists. Nothing was created."));

        string? cws = WorkspaceRootFinder.FindAncestorCws(dir);
        var made = CircuitRF.Design.ThreeD.C3dHierarchy.NewFromLayout(dir, path, cws, new TechnologyCache());
        if (made.Document is not { } c3d)
            return JsonRun.Fail(CliDiagnostics.NewRefused($"New 3D View from Layout: {made.Refusal}"));
        try { CellCreate.WriteThreeDView(dir, name, c3d); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonRun.Fail(CliDiagnostics.NewRefused($"Failed to create the 3D view: {ex.Message}"));
        }

        Console.WriteLine(path);
        JsonRun.AddOutput("3d", path);
        void Note(string text) { Console.Error.WriteLine("note: " + text); JsonRun.Note(CliDiagnostics.NewView3dNote(text)); }
        if (made.SetupsCopied.Count > 0)
            Note($"Copied {made.SetupsCopied.Count} 3D setup(s) as embedded setups: {string.Join(", ", made.SetupsCopied)}.");
        if (made.PortsNote is { } portsNote) Note(portsNote);
        foreach (string moved in made.PortsMoved) Note(moved);
        if (made.PortsLeftOut.Count > 0)
        {
            string left = string.Join("; ", made.PortsLeftOut);
            Console.Error.WriteLine($"warning: Ports left out: {left}.");
            JsonRun.Note(CliDiagnostics.NewView3dPortsLeftOut(left));
        }
        return 0;
    }

    /// <summary>
    /// <c>--views</c>, or <see cref="CellCreate.DefaultViews"/> when it is absent. Null on a name that
    /// is not one of the four, having already refused — a misspelt view silently creating fewer files
    /// than asked for is exactly the kind of quiet wrong answer these verbs exist to avoid.
    /// </summary>
    private static CellViews? ParseViews(string? text)
    {
        if (text is null) return CellCreate.DefaultViews;

        var views = CellViews.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ViewWords.TryGetValue(part.ToLowerInvariant(), out var one))
            {
                JsonRun.Fail(CliDiagnostics.NewUnknownView(part));
                return null;
            }
            views |= one;
        }
        return views;
    }

    /// <summary>Every word <c>--views</c> takes, and what each creates. EMB-5 R-emb5-6: the MCP <c>create</c> tool's
    /// description lists these, and <c>ToolCatalogAgreesWithTheParserTests</c> holds the two lists together.</summary>
    internal static readonly IReadOnlyDictionary<string, CellViews> ViewWords = new Dictionary<string, CellViews>(StringComparer.Ordinal)
    {
        ["schematic"] = CellViews.Schematic,
        ["symbol"]    = CellViews.Symbol,
        ["layout"]    = CellViews.Layout,
        ["3d"]        = CellViews.ThreeD,
        ["none"]      = CellViews.None,
    };

    /// <summary>Every word <c>--length-unit</c> takes, as the MCP tool documents them (EMB-5 R-emb5-6). <c>µm</c> is
    /// read as well, as the spelling of <c>um</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, LayoutUnit> LengthUnitWords = new Dictionary<string, LayoutUnit>(StringComparer.Ordinal)
    {
        ["um"]  = LayoutUnit.Um,
        ["mm"]  = LayoutUnit.Mm,
        ["mil"] = LayoutUnit.Mil,
    };

    /// <summary>What <c>CellFolder.ResolvePrimary</c> says about each view of the cell just made — the
    /// same five-branch rule the project tree reads (R-aut3-7).</summary>
    private static void ReportPrimacy(string cellDir)
    {
        foreach (var type in Enum.GetValues<ViewType>())
        {
            var res = CellFolder.ResolvePrimary(cellDir, type);
            if (res.State is PrimaryState.NoView) continue;

            string what = res.State switch
            {
                PrimaryState.SoleFile or PrimaryState.NamedPresent => $"primary {CellFolder.SubFolderName(type)}: {res.ResolvedName}",
                _ => $"{CellFolder.SubFolderName(type)}: {res.State}",
            };
            Console.Error.WriteLine($"[circuitRF] {what}");
        }
    }

    // ── import part ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-aut3-10: the verb adds NO import logic. <see cref="ComponentRead"/> resolves the part and
    /// <see cref="ComponentImport.Import"/> builds the layout variants, the symbol and the cell —
    /// exactly as the GUI's Import Component does, through the same two calls. What is here is the
    /// choice the chooser dialog makes, made instead by a flag or refused.
    /// </summary>
    public static int RunImport(string[] args)
    {
        if (args.Length == 0)
        {
            JsonRun.Report(CliDiagnostics.ImportNounRequired());
            return ImportUsage();
        }

        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = "import " + noun;

        // brief-sar-11 — anatomy into a .c3d: its own argument set, and the same one-verb-with-nouns surface.
        if (noun == "voxels") return AnatomyImportVerb.RunVoxels(args[1..]);
        if (noun == "surfaces") return AnatomyImportVerb.RunSurfaces(args[1..]);
        if (noun == "schematic") return ImportSchematic(args[1..]);
        if (noun == "library") return VendorLibraryVerbs.ImportLibrary(args[1..]);
        if (noun == "pdk") return ImportPdk.Run(args[1..]);
        if (noun == "model") return ImportModel.Run(args[1..]);
        if (noun != "part")
        {
            JsonRun.Report(CliDiagnostics.ImportUnknownNoun(noun));
            return ImportUsage();
        }

        string? source = null, into = null, cell = null, variant = null, techPath = null;
        bool addLayers = false, list = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--into" when i + 1 < args.Length: into = args[++i]; break;
                case "--cell" when i + 1 < args.Length: cell = args[++i]; break;
                case "--variant" when i + 1 < args.Length: variant = args[++i]; break;
                case "--tech" when i + 1 < args.Length: techPath = args[++i]; break;
                case "--add-layers": addLayers = true; break;
                case "--list-parts": list = true; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(args[i])); return ImportUsage(); }
                    if (source is not null) { JsonRun.Report(CliDiagnostics.ImportMultipleSources()); return ImportUsage(); }
                    source = args[i];
                    break;
            }
        }

        if (source is null) { JsonRun.Report(CliDiagnostics.ImportSourceRequired()); return ImportUsage(); }
        JsonRun.InputPath = source;

        if (!File.Exists(source) && !Directory.Exists(source))
            return JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));

        // A library model is already a cell; it is not a symbol or a footprint for ComponentImport to read.
        if (Directory.Exists(source) && CircuitRF.Design.VendorLibraries.ModelLibrary.ModelFolder.Is(source))
            return JsonRun.Fail(CliDiagnostics.ImportRefused(
                $"'{source}' is a library model folder, not a part file. Copy it into a workspace with `circuitrf import model {source} --into <workspace>`."));

        // The GUI scans a FOLDER and shows a chooser; ComponentFolderScan takes a single file too, so
        // both spellings work here and both go through the one scanner.
        var scan = ComponentFolderScan.Scan(source);
        if (scan.TruncationNote is { } note)
        { Console.Error.WriteLine($"warning: {note}"); JsonRun.Note(CliDiagnostics.ImportMessage(note)); }

        if (scan.Candidates.Count == 0)
        {
            string reasons = scan.SkippedSummary.Count > 0
                ? " This folder also holds " + string.Join(", ", scan.SkippedSummary) + "."
                : "";
            return JsonRun.Fail(CliDiagnostics.ImportRefused(
                ComponentRead.Refusal(Path.GetFileName(source.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) + reasons));
        }

        if (list) return ListParts(scan);

        var candidate = PickCandidate(scan, cell, variant);
        if (candidate is null) return 1;

        if (into is null) { JsonRun.Report(CliDiagnostics.ImportIntoRequired()); return ImportUsage(); }

        // `--into` names the folder the new CELL FOLDER is created in — a workspace root, or any
        // folder inside one. A `.cws` is accepted for the same reason `new cell` accepts one.
        string parentDir = Path.GetFullPath(into);
        if (File.Exists(parentDir) &&
            Path.GetFileName(parentDir).Equals(".cws", StringComparison.OrdinalIgnoreCase))
            parentDir = Path.GetDirectoryName(parentDir)!;

        if (!Directory.Exists(parentDir))
            return JsonRun.Fail(CliDiagnostics.NewParentNotFound(parentDir));

        if (WorkspaceCreate.UnwritableParentRefusal(parentDir, "The part was not imported") is { } refusal)
            return JsonRun.Fail(CliDiagnostics.NewRefused(refusal));

        // The destination technology decides which of the file's layers already exist and which the
        // import reports as new. Resolved by the same walk-up everything else uses, against the folder
        // the cell will land in; `--tech` overrides it, as it does for `convert`.
        Technology? destTech;
        string? destTechPath = null;
        if (techPath is { } tp)
        {
            try { destTech = TechPersistence.LoadFromFile(tp); destTechPath = Path.GetFullPath(tp); }
            catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ConvertTechnologyUnreadable(tp, ex.Message)); }
        }
        else
        {
            var (res, _) = TechnologyResolver.ResolveForDocument(
                null, Path.Combine(parentDir, "x", "layout", "x.clay"), null, new TechnologyCache());
            foreach (var d in res.Diagnostics)
            { Console.Error.WriteLine($"warning: {d}"); JsonRun.Note(CliDiagnostics.EmSetupWarning(d)); }
            destTech     = res.Tech;
            destTechPath = res.ResolvedPath;
        }
        if (destTechPath is not null) Console.Error.WriteLine($"[circuitRF] technology: {destTechPath}");
        if (destTech is null)
        {
            // Said, not hidden. With no destination technology the reconciliation has nothing to
            // compare the part's layers against, so it reports NONE as new and they arrive with
            // numeric keys and no names — the exact trap `convert` records, and the same state the
            // GUI is in when its workspace has no technology.
            var d = CliDiagnostics.ImportNoTechnology();
            Console.Error.WriteLine($"warning: {d.Render()}");
            JsonRun.Note(d);
        }

        var read = ComponentRead.Read(candidate, LayoutUnits.DefaultDbuPerMicron);
        if (read.Refusal is { } readRefusal)
            return JsonRun.Fail(CliDiagnostics.ImportRefused(readRefusal));

        ComponentImport.ImportResult result;
        try
        {
            // resolveLayerMapping is null, which is R-aut3-11 exactly: the mapping takes the same
            // default the dialog PRE-SELECTS (an unmatched layer is added to the technology) rather
            // than asking. ComponentImport says which mapping it settled on, in its own messages.
            result = ComponentImport.Import(
                read.Part!, parentDir, destTech, LayoutUnits.DefaultDbuPerMicron);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JsonRun.Fail(CliDiagnostics.ImportRefused(ex.Message));
        }

        // R-aut3-12: the messages reach the caller IN FULL. They are how a caller learns a pin was
        // inferred, a layer was dropped or a variant was skipped — on stderr as today, and in the
        // --json document as diagnostics.
        foreach (var m in result.Messages)
        { Console.Error.WriteLine($"[circuitRF] {m}"); JsonRun.Note(CliDiagnostics.ImportMessage(m)); }

        if (result.Cancelled || result.CellDir is not { } cellDir)
            return JsonRun.Fail(CliDiagnostics.ImportNothingCreated());

        Console.WriteLine(cellDir);
        JsonRun.AddOutput("cell", cellDir);
        foreach (var type in (ReadOnlySpan<ViewType>)[ViewType.Symbol, ViewType.Layout])
        {
            string dir = CellFolder.SubFolderPath(cellDir, type);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*" + CellFolder.ViewExtension(type)).OrderBy(x => x, StringComparer.Ordinal))
            { Console.WriteLine(f); JsonRun.AddOutput(type.ToString().ToLowerInvariant(), f); }
        }
        ReportPrimacy(cellDir);

        return InstallLayers(result, destTech, destTechPath, addLayers);
    }

    /// <summary>
    /// The layers the part needs that its destination technology does not define.
    ///
    /// <para><b>Nothing is written unless <c>--add-layers</c> says so, and that IS the GUI's
    /// behaviour</b>: <c>ApplyImportToTechnology</c> installs them into the SESSION's live technology
    /// and says in as many words that nothing was written to disk — the user keeps them by opening the
    /// technology and saving it. Headless there is no session to hold them in, so the honest choices
    /// are to report them or to write them, and which of those a caller wants is not something to
    /// guess. Reporting them is never optional: a layer silently dropped is the trap the repo's own
    /// `convert` note already records.</para>
    /// </summary>
    private static int InstallLayers(
        ComponentImport.ImportResult result, Technology? destTech, string? destTechPath, bool addLayers)
    {
        if (result.LayersToAdd.Count == 0) return 0;

        string names = string.Join(", ", result.LayersToAdd.Select(l => l.Name));

        if (!addLayers)
        {
            var d = CliDiagnostics.ImportLayersNotInstalled(result.LayersToAdd.Count, names);
            Console.Error.WriteLine($"warning: {d.Render()}");
            JsonRun.Note(d);
            return 0;
        }

        if (destTech is null || destTechPath is null)
            return JsonRun.Fail(CliDiagnostics.ImportNoTechnologyToAddTo(names));

        try
        {
            // The same shape the GUI's own install has: a CLONE is edited, so a partial write cannot
            // leave the technology half-updated, and an already-present key is skipped rather than
            // duplicated.
            var clone = TechPersistence.Clone(destTech);
            int added = 0;
            foreach (var def in result.LayersToAdd)
            {
                if (clone.Layers.Any(l => l.Key == def.Key)) continue;
                clone.Layers.Add(def);
                added++;
            }
            if (added == 0) return 0;

            TechPersistence.SaveToFile(destTechPath, clone);
            Console.Error.WriteLine($"[circuitRF] {added} layer(s) added to {destTechPath}");
            JsonRun.AddOutput("ctech", destTechPath);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return JsonRun.Fail(CliDiagnostics.ImportRefused(ex.Message));
        }
    }

    /// <summary>
    /// The chooser dialog's decision, headless. One candidate is taken; several with nothing said is a
    /// refusal that lists them, exactly as <c>convert</c> refuses an ambiguous cell — R-aut3-11's
    /// "anything the dialog would have asked is a refusal, never a guess".
    /// </summary>
    private static ComponentCandidate? PickCandidate(ComponentScanResult scan, string? cell, string? variant)
    {
        var pool = scan.Candidates;

        if (cell is not null)
        {
            pool = [.. pool.Where(c => c.DisplayName.Equals(cell, StringComparison.OrdinalIgnoreCase))];
            if (pool.Count == 0)
            {
                JsonRun.Fail(CliDiagnostics.ImportPartNotFound(
                    cell, string.Join(", ", scan.Candidates.Select(c => c.DisplayName).Distinct())));
                return null;
            }
        }

        if (variant is not null)
        {
            pool = [.. pool.Where(c =>
                VariantKey(c).Equals(variant, StringComparison.OrdinalIgnoreCase) ||
                c.Location.Equals(variant, StringComparison.OrdinalIgnoreCase) ||
                c.Family.ToString().Equals(variant, StringComparison.OrdinalIgnoreCase))];
            if (pool.Count == 0)
            {
                JsonRun.Fail(CliDiagnostics.ImportVariantNotFound(variant));
                return null;
            }
        }

        if (pool.Count == 1) return pool[0];

        JsonRun.Fail(CliDiagnostics.ImportAmbiguous(pool.Count, Describe(pool)));
        return null;
    }

    /// <summary>
    /// What <c>--variant</c> takes, and what <c>--list-parts</c> prints in its second column.
    ///
    /// <para>The candidate's LOCATION when it has one — a library folder holding one part written out
    /// once per target format produces one candidate per folder, and the folder is what separates
    /// them. When they share a folder, which is the ordinary case for a part shipped as a symbol in
    /// two dialects plus its land pattern, what separates them is the EXTENSION of the file the
    /// candidate begins at, which is the same file <c>ComponentCandidate.DisplayName</c> is taken
    /// from. Neither is invented here: both are already how the chooser's own rows differ.</para>
    /// </summary>
    private static string VariantKey(ComponentCandidate c)
    {
        if (c.Location.Length > 0) return c.Location;
        var lead = c.SymbolFile ?? c.FootprintFiles.FirstOrDefault() ?? c.Files.FirstOrDefault();
        string ext = lead is null ? "" : Path.GetExtension(lead.Path);
        return ext.Length > 0 ? ext : c.Family.ToString();
    }

    private static int ListParts(ComponentScanResult scan)
    {
        Console.WriteLine(Describe(scan.Candidates));
        Console.Error.WriteLine($"[circuitRF] {scan.Candidates.Count} part(s) in {scan.FilesScanned:N0} file(s)");
        return 0;
    }

    /// <summary>One line per candidate: the name <c>--cell</c> takes, the key <c>--variant</c> takes,
    /// what the candidate can produce, and what it is made of. It has to print both keys, because a
    /// folder holding one part written out once per format produces one candidate per format and they
    /// share their name.</summary>
    private static string Describe(IReadOnlyList<ComponentCandidate> candidates)
        => string.Join(Environment.NewLine, candidates.Select(c =>
            $"{c.DisplayName}\t{VariantKey(c)}\t{c.Description}\t{c.FormatSummary}"));

    // ── import schematic ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// brief-schimp-5 R-si5-5: a schematic design file as a new cell — the GUI's File ▸ Import ▸ Schematic…, headless.
    /// <see cref="SchematicImport.Read"/> classifies and reads it and <see cref="SchematicImport.Import"/> builds and
    /// writes the cell, the two calls the dialog makes; what is here is the dialog's fields as flags. Every default is
    /// the dialog's: the cell is named after the file, every part is mapped by the map, no port is made.
    ///
    /// <para><b>Exit 1 when the connectivity check is not matched — with the cell written.</b> The import completes so
    /// the user can see what is wrong (overview D8); a build machine must still not read a mismatch as success.</para>
    /// </summary>
    private static int ImportSchematic(string[] args)
    {
        string? source = null, into = null, cell = null, codePageText = null;
        var ports = new List<string>();
        var symbolCells = new List<string>();
        bool list = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--into" when i + 1 < args.Length: into = args[++i]; break;
                case "--cell" when i + 1 < args.Length: cell = args[++i]; break;
                case "--port" when i + 1 < args.Length: ports.Add(args[++i]); break;
                case "--symbol-cell" when i + 1 < args.Length: symbolCells.Add(args[++i]); break;
                case "--codepage" when i + 1 < args.Length: codePageText = args[++i]; break;
                case "--list": list = true; break;
                default:
                    if (args[i].StartsWith('-')) { JsonRun.Report(CliDiagnostics.ImportUnknownOption(args[i])); return ImportUsage(); }
                    if (source is not null) { JsonRun.Report(CliDiagnostics.ImportSchematicArgs("one schematic file, please.")); return ImportUsage(); }
                    source = args[i];
                    break;
            }
        }

        if (source is null) { JsonRun.Report(CliDiagnostics.ImportSchematicArgs("a schematic design file is required.")); return ImportUsage(); }
        JsonRun.InputPath = source;
        int? codePage = null;
        if (codePageText is not null)
        {
            if (!int.TryParse(codePageText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cp) || cp <= 0)
                return JsonRun.Fail(CliDiagnostics.ImportSchematicArgs($"--codepage takes a code page number (1252, 932, …), not '{codePageText}'."));
            codePage = cp;
        }
        if (!File.Exists(source)) return JsonRun.Fail(CliDiagnostics.ImportSourceNotFound(source));

        var read = SchematicImport.Read(source, codePage);
        if (!read.Ok) return JsonRun.Fail(CliDiagnostics.ImportRefused(read.Refusal!));
        var design = read.Schematic!;
        Console.Error.WriteLine($"[circuitRF] format: {SchematicImport.FormatName(read.Kind)}"
                              + (design.Version is { Length: > 0 } v ? $", version {v}" : ""));

        var options = new SchematicImportOptions { Ports = ports, SymbolCells = symbolCells };
        if (list) return ListSchematic(design, options);

        if (into is null) { JsonRun.Report(CliDiagnostics.ImportSchematicArgs("--into <workspace-or-dir> says where the cell is created.")); return ImportUsage(); }
        string parentDir = Path.GetFullPath(into);
        if (File.Exists(parentDir) && Path.GetFileName(parentDir).Equals(".cws", StringComparison.OrdinalIgnoreCase))
            parentDir = Path.GetDirectoryName(parentDir)!;
        if (!Directory.Exists(parentDir)) return JsonRun.Fail(CliDiagnostics.NewParentNotFound(parentDir));
        if (WorkspaceCreate.UnwritableParentRefusal(parentDir, "The schematic was not imported") is { } unwritable)
            return JsonRun.Fail(CliDiagnostics.NewRefused(unwritable));

        cell ??= SchematicImportMap.CellNameFor(Path.GetFileNameWithoutExtension(source));
        var result = SchematicImport.Import(design, parentDir, cell, options);
        foreach (string m in result.Messages)
        { Console.Error.WriteLine($"[circuitRF] {m}"); JsonRun.Note(CliDiagnostics.ImportMessage(m)); }
        if (!result.Ok) return JsonRun.Fail(CliDiagnostics.ImportRefused(result.Refusal!));

        Console.WriteLine(result.CellDir);
        JsonRun.AddOutput("cell", result.CellDir!);
        Console.WriteLine(result.SchematicPath);
        JsonRun.AddOutput("schematic", result.SchematicPath!);
        foreach (string created in result.CreatedCells)
        { Console.WriteLine(created); JsonRun.AddOutput("cell", created); }

        return result.Connectivity is { Matched: false } c
            ? JsonRun.Fail(CliDiagnostics.ImportSchematicNotMatched(c.Summary))
            : 0;
    }

    /// <summary><c>--list</c>: the pages, what each part becomes and what may be a port — the dialog's two tables, and
    /// nothing written.</summary>
    private static int ListSchematic(ForeignSchematic design, SchematicImportOptions options)
    {
        var mapping = SchematicImportMap.Decide(design, options.SymbolCells);
        Console.WriteLine($"{(design.Hierarchical ? "sheets" : "pages")}\t{string.Join(", ", design.Pages.Select(p => p.Name))}");
        foreach (var d in mapping)
            Console.WriteLine($"part\t{d.Reference}\t{d.SymbolName}\t{d.Value ?? ""}\t{d.Describe()}");
        foreach (var p in SchematicImport.PortCandidates(design, mapping))
            Console.WriteLine($"port\t{p.Key}\t{p.Description}");
        Console.Error.WriteLine($"[circuitRF] {mapping.Count} part(s); nothing was written");
        return 0;
    }

    // ── usage ─────────────────────────────────────────────────────────────────────────────────────

    private static int NewUsage()
    {
        Console.Error.WriteLine("Usage: circuitrf new workspace <dir> [--name N] [--tech <id>|none] [--length-unit um|mm|mil] [--vendor-library <name>=<folder>]...");
        Console.Error.WriteLine("       circuitrf new cell <workspace-or-dir> <cellName> [--views schematic,symbol,layout,3d]");
        Console.Error.WriteLine("       circuitrf new view3d <cell-dir> --from-layout [--name N]");
        return 1;
    }

    private static int ImportUsage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf import part <file-or-folder> --into <workspace-or-dir> "
            + "[--cell N] [--variant V] [--tech <file.ctech>] [--add-layers] [--list-parts]\n"
            + "       circuitrf import schematic <file> --into <workspace-or-dir> [--cell N] [--port <ref-or-net>]... "
            + "[--symbol-cell <ref>]... [--codepage N] [--list]\n"
            + VendorLibraryVerbs.ImportUsageText + "\n"
            + ImportPdk.UsageText + "\n"
            + ImportModel.UsageText + "\n"
            + AnatomyImportVerb.UsageText);
        return 1;
    }
}
