using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The three questions a caller has to be able to ask before <c>render</c> is usable
/// (brief-render-3-query-surface.md): <b>what cells does this hold and which views does each have</b>,
/// <b>what layers can I ask for</b>, and <b>how big is this</b>. Before RND-3 the only way to answer
/// any of them was to open the GUI.
///
/// <para><b>They are options on <c>explain</c> and not three new verbs</b> (R-rnd3-1). A client that
/// discovers tools up front carries every description for the whole session whether or not it calls
/// one, and <c>cli.md</c> §11.3 says the count is the point — three more would be a 43% increase in a
/// surface deliberately capped at seven, to answer three questions that are all the question
/// <c>explain</c> already exists for: <i>what did circuitRF decide?</i> Which file is a cell's
/// schematic is <c>CellFolder.ResolvePrimary</c>'s decision, with three real failure modes. Which
/// layers exist is the payload of a technology walk-up this verb already performs and already
/// reports. How big it is is what <c>--fit</c> resolves to, which a caller needs before it can ask for
/// anything other than <c>--fit</c>.</para>
///
/// <para><b>Nothing here computes an answer a renderer or a validator already computes.</b> The cell
/// walk is <see cref="CellLookup"/> — the same enumeration <c>render --cell</c> resolves through, so
/// a cell this lists is a cell that verb can draw. Primacy is <c>CellFolder.ResolvePrimary</c> and
/// defects are <c>CellViewFileValidator.DescribeDefect</c>, both of which <c>check</c> already
/// surfaces. The layer counts are <c>CellHierarchy</c>'s own recursive walk. The extents are
/// <see cref="DocumentExtents"/>, which is literally the function <c>render --fit</c> frames on.</para>
/// </summary>
internal static class ExplainQueries
{
    // ── --cells ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every cell reachable from <paramref name="path"/>, and for each the three views with the file
    /// primacy resolved to and the state it resolved in.
    ///
    /// <para><b>It reports the RESOLUTION, not a directory listing</b> (R-rnd3-3). The whole value of
    /// this over <c>ls</c> is the cases where the answer is not obvious: a named primary that is
    /// missing, an empty sub-folder, a folder holding three <c>.csch</c> files and naming none. Each is
    /// listed WITH its state, never omitted and never silently resolved to the alphabetically first
    /// file.</para>
    ///
    /// <para><b>A cell folder reports that one cell</b>, in the same shape, one entry — which is what
    /// makes it composable with <c>render</c>: ask what views a cell has, then render one.</para>
    /// </summary>
    public static (IReadOnlyList<ExplainCellJson> Cells, int Exit) Cells(
        string path, DocumentKind kind, bool all)
    {
        switch (kind)
        {
            case DocumentKind.Cell:
                return ([One(Path.GetFullPath(path), all)], 0);

            case DocumentKind.Workspace:
            case DocumentKind.Folder:
            {
                string root = Directory.Exists(path)
                    ? Path.GetFullPath(path)
                    : Path.GetDirectoryName(Path.GetFullPath(path))!;
                return ([.. CellLookup.All(root, includeGenerated: all).Select(dir => One(dir, all))], 0);
            }

            default:
                return ([], JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable(
                    "--cells", DocumentKinds.Name(kind), "the cells a workspace or a folder holds")));
        }
    }

    private static ExplainCellJson One(string cellDir, bool all)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir));

        string? workspaceRoot = WorkspaceRootFinder.FindAncestorCws(cellDir) is { } cws
            ? Path.GetDirectoryName(cws) : null;
        bool? outside = workspaceRoot is not null
            ? WorkspaceRootFinder.IsOutside(cellDir, workspaceRoot) : null;

        var views = new List<ExplainCellViewJson>(3);
        foreach (var vt in DocumentKinds.AllViewTypes)
        {
            PrimaryResolution res;
            try { res = CellFolder.ResolvePrimary(cellDir, vt); }
            catch { res = new PrimaryResolution { State = PrimaryState.NoView }; }

            string sub = CellFolder.SubFolderPath(cellDir, vt);
            string[] candidates;
            try
            {
                candidates = Directory.Exists(sub)
                    ? [.. Directory.GetFiles(sub, "*" + CellFolder.ViewExtension(vt))
                                   .Select(f => Path.GetFileName(f)!)
                                   .Order(StringComparer.OrdinalIgnoreCase)]
                    : [];
            }
            catch { candidates = []; }

            // The defect of the file primacy actually CHOSE. Where it chose none there is nothing to
            // validate — reporting one candidate's defect would be reporting it about a file no verb
            // would open, and choosing which candidate would be the silent resolution R-rnd3-3 forbids.
            string? defect = null;
            if (res.ResolvedName is { Length: > 0 } chosen)
            {
                try { defect = CellViewFileValidator.DescribeDefect(Path.Combine(sub, chosen), vt); }
                catch (Exception ex) { defect = ex.Message; }
            }

            views.Add(new ExplainCellViewJson(
                CellFolder.SubFolderName(vt), res.ResolvedName ?? res.MissingName,
                StateName(res.State), candidates, defect));
        }

        bool generated = ReservedFolders.IsUnderGeneratedCells(cellDir);
        return new ExplainCellJson(name, cellDir, outside, all && generated ? true : null, views);
    }

    /// <summary><c>PrimaryState</c>, spelled the way this document spells every enum. The five stay
    /// five — collapsing <c>MissingNamedPrimary</c> into <c>NoPrimary</c> is exactly what
    /// <c>CellFolder</c>'s own remarks forbid, and it is the state a caller most needs to see.</summary>
    private static string StateName(PrimaryState s) => s switch
    {
        PrimaryState.SoleFile            => "sole-file",
        PrimaryState.NamedPresent        => "named-present",
        PrimaryState.MissingNamedPrimary => "missing-named-primary",
        PrimaryState.NoPrimary           => "no-primary",
        _                                => "no-view",
    };

    // ── --layers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The resolved technology's layers, and which of them the document actually uses.
    ///
    /// <para><b>The walk is reported as a walk</b> (R-rnd3-7, R-aut4-7) — it is the same walk
    /// <c>explain</c> already prints for a <c>.clay</c>, and this adds the payload to a walk that was
    /// being done anyway. Where it resolves to nothing, that IS the answer, with the fallback palette
    /// NAMED: that is what <c>render</c> will draw with, and a caller needs to know the colours it gets
    /// are not the process's.</para>
    /// </summary>
    public static (ExplainLayersJson? Layers, int Exit) Layers(
        string path, DocumentKind kind, ViewType? askedView, List<ResolutionStepJson> walks)
    {
        switch (kind)
        {
            case DocumentKind.Layout:
                return LayersOfLayout(Path.GetFullPath(path), walks);

            case DocumentKind.Cell:
            {
                string cellDir = Path.GetFullPath(path);
                var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
                if (askedView is { } v && v != ViewType.Layout)
                    return (null, JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable(
                        "--layers", CellFolder.SubFolderName(v), "drawing layers, which only a layout has")));
                if (primary.ResolvedName is not { Length: > 0 } file)
                {
                    // No layout to count against — but the cell still resolves a technology, and that
                    // is a real answer: it is the palette a layout created here would draw on.
                    return LayersOfTechnologyOnly(cellDir, walks);
                }
                return LayersOfLayout(
                    Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), file), walks);
            }

            case DocumentKind.Technology:
            case DocumentKind.Workspace:
            case DocumentKind.Folder:
                return LayersOfTechnologyOnly(Path.GetFullPath(path), walks);

            default:
                return (null, JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable(
                    "--layers", DocumentKinds.Name(kind), "the drawing layers a technology defines")));
        }
    }

    /// <summary>
    /// A technology with no document to count against — a <c>.ctech</c> named directly, a workspace, or
    /// a cell with no layout view.
    ///
    /// <para><b><c>shapes</c> is ABSENT rather than zero</b> (R-rnd3-5). Zero is a claim about a
    /// document, and here there is no document to make it about.</para>
    /// </summary>
    private static (ExplainLayersJson?, int) LayersOfTechnologyOnly(
        string path, List<ResolutionStepJson> walks)
    {
        var cache = new TechnologyCache();
        Technology? tech;
        string? from;
        string by;

        if (DocumentKinds.Classify(path) == DocumentKind.Technology)
        {
            try { tech = TechPersistence.LoadFromFile(path); }
            catch (Exception ex)
            { return (null, JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message))); }
            from = path;
            by   = "the file itself — a technology named directly resolves to nothing else";
        }
        else
        {
            // The workspace's DEFAULT technology, through the resolver every document walks, with a
            // null TechRef — which is exactly the state of a layout that states none.
            var (res, _) = TechnologyResolver.ResolveForDocument(
                null, Path.Combine(path, "x" + CellFolder.ViewExtension(ViewType.Layout)), null, cache);
            tech = res.Tech;
            from = res.ResolvedPath;
            by   = res.Source == TechResolutionSource.WorkspaceDefault
                ? "the workspace's DefaultTechRef"
                : "nothing resolved: no workspace default";
            foreach (var d in res.Diagnostics) JsonRun.Report(CliDiagnostics.CheckResolverNote(path, d));
        }

        walks.Add(new ResolutionStepJson("layers", from ?? path,
            tech is null ? null
                : $"{(tech.Name.Length > 0 ? tech.Name + " — " : "")}{tech.Layers.Count} defined, "
                  + "no document to count against", by));

        if (tech is null)
            return (new ExplainLayersJson(null, null, FallbackNote, []), 0);

        return (new ExplainLayersJson(
            tech.Name.Length > 0 ? tech.Name : null, from, by,
            [.. tech.Layers.Select(l => Layer(l, tech, null, null))]), 0);
    }

    /// <summary>What <c>render</c> draws with when nothing resolves — named, because the colours a
    /// caller then gets are generated rather than the process's (R-rnd3-7).</summary>
    private const string FallbackNote =
        "nothing resolved — render draws on the generated fallback palette, one deterministic colour " +
        "per layer key, and the colours are not the process's";

    private static (ExplainLayersJson?, int) LayersOfLayout(string clay, List<ResolutionStepJson> walks)
    {
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception ex)
        { return (null, JsonRun.Fail(CliDiagnostics.ExplainUnreadable(clay, ex.Message))); }

        var (res, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, null, new TechnologyCache());
        foreach (var d in res.Diagnostics) JsonRun.Report(CliDiagnostics.CheckResolverNote(clay, d));

        string by = res.Source switch
        {
            TechResolutionSource.LayoutRef        => "the layout's own TechRef, relative to its own directory",
            TechResolutionSource.WorkspaceDefault => "the workspace's DefaultTechRef — the layout states none",
            _                                     => FallbackNote,
        };

        // R-rnd3-6: the count is of what would be RENDERED, hierarchy included — a layer used only
        // inside a placed sub-cell IS used. CellHierarchy's own recursive walk with the same layer
        // filter the renderer gates on; there is deliberately no second walk here.
        string baseDir = CellHierarchy.BaseDirOfDocument(clay);
        // No visibility filter, deliberately: `shapes` is "shapes on that layer in the document, DRAWN
        // OR NOT", which is what makes an empty layer and a hidden one two different answers — the
        // `visible` flag beside it is the other half. Same rule, same walk and therefore the same
        // number as `render --json`'s own layers[].shapes.
        var counted = CellHierarchy.ShapeCountsByLayer(view, baseDir);

        if (counted.Truncated)
            JsonRun.Report(CliDiagnostics.ExplainLayerCountTruncated(clay));

        // The technology walk is already reported above (R-aut4-7); this step is the PAYLOAD that walk
        // was being done for, so its rule names how the counting was done rather than repeating how the
        // technology was found.
        walks.Add(new ResolutionStepJson("layers", res.ResolvedPath ?? clay,
            res.Tech is null
                ? $"none defined, {counted.Shapes.Count} used"
                : $"{(res.Tech.Name.Length > 0 ? res.Tech.Name + " — " : "")}"
                  + $"{res.Tech.Layers.Count} defined, {counted.Shapes.Count} used",
            res.Tech is null
                ? FallbackNote
                : "the resolved technology's own layer table, counted against this document's " +
                  "hierarchy — a layer used only inside a placed sub-cell is used"));

        var rows = new List<ExplainLayerJson>();
        var seen = new HashSet<LayerKey>();

        foreach (var l in res.Tech?.Layers ?? [])
        {
            seen.Add(l.Key);
            rows.Add(Layer(l, res.Tech, Count(l.Key), Using(l.Key)));
        }

        // A key the document draws on that the technology does NOT define is a real and common state
        // after an import (layout-view.md §2.4). It renders — on the fallback palette — so leaving it
        // out would report a document as drawing on layers it does not draw on, and hide the ones it
        // does. Named as generated, so a caller can tell the two apart.
        foreach (var key in counted.Shapes.Keys.Where(k => !seen.Contains(k))
                                   .OrderBy(k => k.Layer).ThenBy(k => k.Datatype))
            rows.Add(Layer(FallbackPalette.For(key), res.Tech, Count(key), Using(key)));

        return (new ExplainLayersJson(
            res.Tech is { Name.Length: > 0 } t ? t.Name : null,
            res.ResolvedPath, by, rows, counted.Truncated ? true : null), 0);

        long? Count(LayerKey k)   => counted.Shapes.TryGetValue(k, out long n) ? n : 0;
        int?  Using(LayerKey k)   => counted.Placements.TryGetValue(k, out int p) ? p : 0;
    }

    private static ExplainLayerJson Layer(LayerDef l, Technology? tech, long? shapes, int? placements)
        => new(l.Name.Length > 0 ? l.Name : l.Key.ToString(),
               l.Key.Layer, l.Key.Datatype,
               l.Purpose,
               l.Visible, l.Selectable,
               l.Color.ToHex(),
               tech?.FindFillPattern(l.FillPattern) is { } fp ? fp.Name : "solid",
               shapes, placements);

    // ── --extents ────────────────────────────────────────────────────────────

    /// <summary>The spelling both verbs use for a dimensionless coordinate space. Said once — two
    /// spellings of one fact across two verbs is the drift this series exists to prevent.</summary>
    private const string DesignUnits = "design-units";

    /// <summary>
    /// How big the document is, <b>from the same function <c>render --fit</c> frames on</b>
    /// (R-rnd3-9) — <see cref="DocumentExtents"/>, in <c>CircuitRF.Render</c>. If the two could
    /// disagree the number would be worse than useless, because a caller uses this one to compute a
    /// <c>--window</c> for that one.
    ///
    /// <para><b>Base SI, with the unit AND the scale named</b> (R-rnd3-8). A layout's numbers come out
    /// in metres with the DBU scale that produced them stated, so a caller can reconstruct what it
    /// would type into <c>--window</c>. A schematic's and a symbol's are dimensionless design units and
    /// are SAID to be, rather than quietly emitted as if they were metres.</para>
    /// </summary>
    public static (ExplainExtentsJson? Extents, int Exit) Extents(
        string path, DocumentKind kind, ViewType? askedView)
    {
        switch (kind)
        {
            case DocumentKind.Layout:    return LayoutExtents(Path.GetFullPath(path));
            case DocumentKind.Schematic: return SchematicExtents(Path.GetFullPath(path));
            case DocumentKind.Symbol:    return SymbolExtents(Path.GetFullPath(path));

            case DocumentKind.Cell:
            {
                string cellDir = Path.GetFullPath(path);
                var held = new List<ViewType>();
                foreach (var v in DocumentKinds.AllViewTypes)
                    if (CellFolder.ResolvePrimary(cellDir, v).State != PrimaryState.NoView) held.Add(v);

                if (held.Count == 0)
                    return (null, JsonRun.Fail(CliDiagnostics.ExplainCellHasNoView(cellDir, "drawable")));

                ViewType view;
                if (askedView is { } asked)
                {
                    if (!held.Contains(asked))
                        return (null, JsonRun.Fail(CliDiagnostics.ExplainNoSuchView(
                            cellDir, CellFolder.SubFolderName(asked))));
                    view = asked;
                }
                else if (held.Count > 1)
                {
                    // R-rnd0-6, the same refusal `render` gives for the same question.
                    return (null, JsonRun.Fail(CliDiagnostics.ExplainViewRequired(
                        cellDir, string.Join(", ", held.Select(CellFolder.SubFolderName)))));
                }
                else view = held[0];

                var primary = CellFolder.ResolvePrimary(cellDir, view);
                if (primary.ResolvedName is not { Length: > 0 } file)
                    return (null, JsonRun.Fail(CliDiagnostics.ExplainCellHasNoView(
                        cellDir, CellFolder.SubFolderName(view))));

                string doc = Path.Combine(CellFolder.SubFolderPath(cellDir, view), file);
                return view switch
                {
                    ViewType.Layout    => LayoutExtents(doc),
                    ViewType.Symbol    => SymbolExtents(doc),
                    _                  => SchematicExtents(doc),
                };
            }

            default:
                return (null, JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable(
                    "--extents", DocumentKinds.Name(kind), "the geometry a drawable document holds")));
        }
    }

    private static (ExplainExtentsJson?, int) LayoutExtents(string clay)
    {
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception ex)
        { return (null, JsonRun.Fail(CliDiagnostics.ExplainUnreadable(clay, ex.Message))); }

        var (res, _) = TechnologyResolver.ResolveForDocument(view.TechRef, clay, null, new TechnologyCache());
        string baseDir = CellHierarchy.BaseDirOfDocument(clay);

        var bb = DocumentExtents.LayoutBox(view, res.Tech, baseDir);

        // The DBU→metre scale, and the DISPLAY unit's scale beside it — the same two numbers
        // `render` reports, in the same fields, so a caller reading one can use the other.
        double metresPerDbu = 1e-6 / Math.Max(1, view.DbuPerMicron);
        double displayScale = MetresPerUnit(view.DisplayUnit);

        if (bb.IsEmpty)
            return (new ExplainExtentsJson(null, null, null, null, null, null, null, "m", displayScale,
                                           true, null, EmptyNote), 0);

        var perLayer = DocumentExtents.LayoutBoxPerLayer(view, res.Tech);
        var names = new Dictionary<LayerKey, string>();
        foreach (var l in res.Tech?.Layers ?? [])
            names[l.Key] = l.Name.Length > 0 ? l.Name : l.Key.ToString();

        string? note = view.Rulers.Any(r => r.SizeMode == RulerSizeMode.Fixed)
            ? "a fit adds room for the Fixed-mode ruler readouts, which are measured in screen points " +
              "at render time and have no world extent until a page size is chosen"
            : null;

        return (new ExplainExtentsJson(
            bb.MinX * metresPerDbu, bb.MinY * metresPerDbu,
            bb.MaxX * metresPerDbu, bb.MaxY * metresPerDbu,
            (bb.MaxX - bb.MinX) * metresPerDbu, (bb.MaxY - bb.MinY) * metresPerDbu,
            LayoutWindow(bb, view), "m", displayScale, false,
            [.. perLayer.Select(p => new ExplainLayerExtentJson(
                names.TryGetValue(p.Key, out var n) ? n : p.Key.ToString(),
                p.Box.MinX * metresPerDbu, p.Box.MinY * metresPerDbu,
                p.Box.MaxX * metresPerDbu, p.Box.MaxY * metresPerDbu,
                LayoutWindow(p.Box, view)))],
            note), 0);
    }

    /// <summary>
    /// R-aut12-3. A DBU box written as the <c>x0,y0,x1,y1</c> <c>render --window</c> accepts —
    /// <b>the output of this verb is the input of that one</b>, which it was not while this one
    /// emitted bare metres and that one refused a bare number.
    ///
    /// <para>The DOCUMENT's display unit, not metres: <c>LayoutUnits.TryParse</c> — which is what
    /// <c>render</c> parses a coordinate with — reads nm, um, mm, mil and in, and has no spelling for
    /// a bare metre at all, so emitting the numeric field's own unit would have produced a string
    /// that reads plausibly and is refused. The spelling and the decimal count are
    /// <see cref="LayoutUnits.Spell"/>'s, beside the parser, so the two cannot drift apart.</para>
    /// </summary>
    private static string LayoutWindow(Bbox bb, LayoutView view)
    {
        return string.Join(',', new[] { bb.MinX, bb.MinY, bb.MaxX, bb.MaxY }.Select(One));
        string One(long dbu) => LayoutUnits.Spell(dbu, view.DisplayUnit, view.DbuPerMicron);
    }

    private static (ExplainExtentsJson?, int) SchematicExtents(string csch)
    {
        SchematicEditModel model;
        try { (model, _, _) = SchematicPersistence.LoadFromFile(csch); }
        catch (Exception ex)
        { return (null, JsonRun.Fail(CliDiagnostics.ExplainUnreadable(csch, ex.Message))); }

        var (rm, _) = model.BuildRenderModel();
        var box = DocumentExtents.SchematicBox(model, rm);
        return (Box(box, DesignUnits, 1.0, null), 0);
    }

    private static (ExplainExtentsJson?, int) SymbolExtents(string csym)
    {
        Design.Symbol.Symbol symbol;
        try { symbol = SymbolPersistence.LoadFromFile(csym); }
        catch (Exception ex)
        { return (null, JsonRun.Fail(CliDiagnostics.ExplainUnreadable(csym, ex.Message))); }

        // R-rnd3-10: the GEOMETRY box — the primitives and the pin ANCHORS. A pin's NAME is drawn in
        // pixels at a size with a floor, so it is not in the primitive bounding box and cannot be put
        // there; reporting a zoom-dependent number from a zoom-independent verb is the mistake this
        // note exists instead of.
        var box = DocumentExtents.SymbolBox(symbol);
        string? note = symbol.Pins.Count > 0
            ? "a fit adds room for the pin names, which are drawn in pixels at a size with a floor and " +
              "have no world extent until a page size is chosen"
            : null;
        return (Box(box, DesignUnits, 1.0, note), 0);
    }

    /// <summary>An empty document has NO extents. <c>(0,0,0,0)</c> is a point at the origin — a
    /// different fact, and one a caller would happily divide by (R-rnd3-10).</summary>
    private const string EmptyNote = "the document holds no geometry, so it has no extents — a zero " +
                                     "box would be a point at the origin, which is a different fact";

    private static ExplainExtentsJson Box(WorldRect? r, string unit, double scale, string? note)
        => r is { } b
            ? new ExplainExtentsJson(b.X0, b.Y0, b.X1, b.Y1, b.W, b.H, DesignUnitWindow(b),
                                     unit, scale, false, null, note)
            : new ExplainExtentsJson(null, null, null, null, null, null, null,
                                     unit, scale, true, null, EmptyNote);

    /// <summary>The same round trip on the two document kinds whose coordinates are dimensionless:
    /// <c>render --window</c> takes BARE numbers there and a unit suffix would be the invention, so
    /// this differs from <see cref="LayoutWindow"/> in exactly that. <c>R</c> because the string has
    /// to read back as the same double — a trimmed one frames a subtly different page.</summary>
    private static string DesignUnitWindow(WorldRect b)
        => string.Join(',', new[] { b.X0, b.Y0, b.X1, b.Y1 }
                            .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));


    // ── --footprints ─────────────────────────────────────────────────────────

    /// <summary>
    /// Per component: what it STATES, what that resolved to, how many pads, and — for a built-in —
    /// the technology the pattern would be generated against (R-fp4-4b).
    /// </summary>
    /// <remarks>
    /// <b>The walk is reported as well as the answer</b>, which is what <c>explain</c> is for.
    /// Resolution here is two different walks depending on the first four characters of the stored
    /// value: <c>smt:</c> goes to the case table and is GENERATED, anything else is a path resolved
    /// against the schematic's own folder exactly as a <c>CellRef</c> is. Which one produced the
    /// answer is the part a caller cannot otherwise see.
    ///
    /// <para><b>The technology is reported for a built-in and only for a built-in.</b> A generated
    /// land pattern resolves its copper, mask and silkscreen BY ROLE against the technology in
    /// force, and the shipped technologies disagree about every layer key — so which technology is
    /// in force is part of what the artwork WILL BE. A cell's artwork is already on disk on keys of
    /// its own, and naming a technology beside it would suggest it was about to be re-resolved.</para>
    ///
    /// <para><b>It reads the schematic, not the netlist.</b> <c>Footprint</c> is dropped before
    /// parameter resolution (R-fp2-6), so it is not in an elaborated netlist and never will be —
    /// asking the netlist would report every design as stating none.</para>
    /// </remarks>
    public static (IReadOnlyList<ExplainFootprintJson> Rows, int Exit) Footprints(
        string path, DocumentKind kind, List<ResolutionStepJson> walks)
    {
        string? csch = kind switch
        {
            DocumentKind.Schematic => Path.GetFullPath(path),
            DocumentKind.Cell      => PrimarySchematicOf(Path.GetFullPath(path)),
            _                      => null,
        };

        if (csch is null)
            return ([], JsonRun.Fail(CliDiagnostics.ExplainOptionNotApplicable(
                "--footprints", DocumentKinds.Name(kind),
                "the footprints a schematic's components state")));

        SchematicEditModel model;
        try { (model, _, _) = SchematicPersistence.LoadFromFile(csch); }
        catch (Exception ex) { return ([], JsonRun.Fail(CliDiagnostics.ExplainUnreadable(csch, ex.Message))); }

        string baseDir = Path.GetDirectoryName(csch)!;

        // Resolved ONCE, not per component: the walk is the same for all of them and repeating it
        // would print the same line thirteen times for the Power Rail example.
        var (techRes, _) = TechnologyResolver.ResolveForDocument(
            null, Path.Combine(baseDir, "x" + CellFolder.ViewExtension(ViewType.Layout)), null,
            new TechnologyCache());
        string? techName = techRes.Tech is { } t
            ? (t.Name.Length > 0 ? t.Name : techRes.ResolvedPath) ?? techRes.ResolvedPath
            : null;

        var rows = new List<ExplainFootprintJson>();
        foreach (var comp in model.Components)
        {
            if (comp.Footprint is not { Length: > 0 } stored) continue;

            var r = FootprintCatalog.Resolve(stored, baseDir);
            string who = comp.InstanceName is { Length: > 0 } n ? n : comp.Id;

            string? resolvedTo = r.State switch
            {
                FootprintCatalog.FootprintState.BuiltIn =>
                    $"{r.Case!.Display}, {DensityVariant.Label(r.Density)} — generated",
                FootprintCatalog.FootprintState.Cell => r.ResolvedPath,
                _ => null,
            };

            rows.Add(new ExplainFootprintJson(
                who, stored, r.State.ToString().ToLowerInvariant(), r.Walk,
                r.PadCount, comp.EffectivePortCount, resolvedTo,
                r.State == FootprintCatalog.FootprintState.BuiltIn
                    ? techName ?? "nothing resolved: no workspace default"
                    : null,
                r.Refusal));
        }

        walks.Add(new ResolutionStepJson(
            "footprints", csch,
            rows.Count == 0
                ? "no component states a Footprint"
                : $"{rows.Count} component(s) state a footprint",
            "the schematic's own stored parameters — Footprint is dropped before elaboration, so "
            + "it is not in the netlist"));

        return (rows, 0);
    }

    /// <summary>The cell folder's primary schematic, or null — the same primacy resolution
    /// <c>--cells</c> reports and <c>render</c> draws.</summary>
    private static string? PrimarySchematicOf(string cellDir)
    {
        try
        {
            var res = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            return res.ResolvedName is { Length: > 0 } name
                ? Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name)
                : null;
        }
        catch { return null; }
    }

    /// <summary>The same table <c>render</c> reports its own scale from.</summary>
    private static double MetresPerUnit(LayoutUnit u) => u switch
    {
        LayoutUnit.Nm   => 1e-9,
        LayoutUnit.Um   => 1e-6,
        LayoutUnit.Mm   => 1e-3,
        LayoutUnit.Mil  => 2.54e-5,
        _               => 2.54e-2,
    };
}
