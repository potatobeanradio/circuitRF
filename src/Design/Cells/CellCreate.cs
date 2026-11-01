using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Results;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Cells;

// ──────────────────────────────────────────────────────────────────────────────
//  CellCreate — writing a cell's view files, once.
//
//  brief-automation-3-authoring-verbs.md R-aut3-1: `circuitrf new cell` and the GUI's New Cell /
//  New Schematic / New Symbol / New Layout commands call THESE, and after this file there is exactly
//  one implementation of "write an empty view of type T into a cell folder".
//
//  WHY THE LEAF WRITERS ARE THE CAPABILITY, AND `Create` IS ONLY THEIR COMPOSITION. The GUI does not
//  create a cell in one call and it cannot be made to: R-cc-1 says a schematic that fails to write
//  must never roll back the cell that already exists, so New Cell reports the folder, refreshes the
//  tree, and only then writes the schematic — and New Schematic writes one into a cell that already
//  exists, with a file name that need not be the cell's. What both paths genuinely share is the
//  WRITE, so that is what lives here. `Create` is the headless composition of the same three writers
//  in the obvious order, and the byte-identity gate compares the two compositions.
//
//  R-aut3-7: a cell created here satisfies CellFolder.ResolvePrimary's rules or it is not created.
//  Every view written is the SOLE file in its sub-folder, which is CellFolder's `SoleFile` branch —
//  implicitly primary, with nothing named in `.ccell` to contradict it and therefore no way to
//  produce the `MissingNamedPrimary` state the project tree surfaces as a warning.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Which views <see cref="CellCreate.Create"/> writes. Flags, because the four are
/// independent and a cell may legitimately have any subset of them (including none — that is a cell
/// folder with three empty sub-folders, which is what the folder-creation call on its own makes).</summary>
[Flags]
public enum CellViews
{
    None      = 0,
    Schematic = 1,
    Symbol    = 2,
    Layout    = 4,

    /// <summary>A 3D view (brief-em3d-41). Its <c>3d/</c> sub-folder is made by writing it, never by
    /// the folder creation (D10).</summary>
    ThreeD    = 8,
}

/// <param name="CellDir">The cell folder.</param>
/// <param name="SchematicPath">The written <c>.csch</c>, or null when it was not asked for.</param>
/// <param name="SymbolPath">The written <c>.csym</c>, or null when it was not asked for.</param>
/// <param name="LayoutPath">The written <c>.clay</c>, or null when it was not asked for.</param>
/// <param name="ThreeDPath">The written <c>.c3d</c>, or null when it was not asked for.</param>
public sealed record CellCreateResult(
    string CellDir, string? SchematicPath, string? SymbolPath, string? LayoutPath, string? ThreeDPath = null)
{
    /// <summary>Every path the creation produced, cell folder first — what a <c>--json</c> caller
    /// reads, since its next step is usually to rewrite one of them.</summary>
    public IEnumerable<string> Paths
    {
        get
        {
            yield return CellDir;
            if (SchematicPath is { } s) yield return s;
            if (SymbolPath    is { } y) yield return y;
            if (LayoutPath    is { } l) yield return l;
            if (ThreeDPath    is { } t) yield return t;
        }
    }
}

public static class CellCreate
{
    /// <summary>
    /// What <c>new cell</c> creates when <c>--views</c> says nothing.
    ///
    /// <para><b>Schematic alone, because that is what the GUI's New Cell creates</b> — it calls
    /// <see cref="CellFolder.CreateCellFolder"/> and then writes one <c>.csch</c> (R-cc-1: a New Cell
    /// always creates that cell's primary schematic). The symbol and layout sub-folders are made and
    /// left empty, and a user makes those views when they want them. The brief's own text says the
    /// default is symbol and schematic "matching the GUI's own New Cell"; the tree says otherwise,
    /// and the RULE — headless matches the GUI — is what decides it, since a headless default that
    /// differs from the dialog's is a second product (R-aut3-3's reasoning). See
    /// <c>src/Cli/RESOLVED.md</c>.</para>
    /// </summary>
    public const CellViews DefaultViews = CellViews.Schematic;

    /// <summary>
    /// The whole cell: folder, sub-folders, <c>.ccell</c>, and one empty-but-valid file per requested
    /// view, each named after the cell.
    /// </summary>
    /// <param name="schematic">
    /// The schematic model to write. Null writes an empty one — which is what the New Cell dialog's
    /// own pre-selected "(Empty)" template choice produces, so the headless default and the dialog's
    /// agree (R-aut3-3). The GUI passes a loaded shipped template here when the user picked one.
    /// </param>
    /// <param name="tech">
    /// The technology a <c>.clay</c> takes its display unit and snap from, resolved by the caller —
    /// the same values <c>New Layout</c> writes. Null is the supported no-technology state and gives
    /// the same defaults the GUI's own null-technology branch does.
    /// </param>
    /// <exception cref="ArgumentException">R-aut3-9: <paramref name="cellName"/> fails
    /// <see cref="NameValidator"/>. There is no second rule set — a headless caller must not be able
    /// to create a name the GUI rejects.</exception>
    public static CellCreateResult Create(
        string parentDir, string cellName, CellViews views = DefaultViews,
        SchematicEditModel? schematic = null, Technology? tech = null)
    {
        string cellDir = CellFolder.CreateCellFolder(parentDir, cellName);

        string? schematicPath = views.HasFlag(CellViews.Schematic) ? WriteSchematicView(cellDir, cellName, cellName, schematic) : null;
        string? symbolPath    = views.HasFlag(CellViews.Symbol)    ? WriteSymbolView(cellDir, cellName)                         : null;
        string? layoutPath    = views.HasFlag(CellViews.Layout)    ? WriteLayoutView(cellDir, cellName, NewLayoutView(tech))    : null;

        // AFTER the layout, deliberately: a 3D view takes its units from the cell's primary .clay when
        // there is one, so `--views layout,3d` gives a 3D view in the layout's units.
        string? threeDPath    = views.HasFlag(CellViews.ThreeD)    ? WriteThreeDView(cellDir, cellName, NewThreeDView(cellDir, tech)) : null;

        return new CellCreateResult(cellDir, schematicPath, symbolPath, layoutPath, threeDPath);
    }

    /// <summary>
    /// Writes one <c>.csch</c> into <paramref name="cellDir"/>'s schematic sub-folder and returns its
    /// path. <paramref name="cellName"/> is the cell the schematic belongs to (it is stored in the
    /// file); <paramref name="fileNameWithoutExt"/> is what the file is called, which is not always
    /// the same thing — New Schematic writes a second view into an existing cell.
    /// </summary>
    public static string WriteSchematicView(
        string cellDir, string cellName, string fileNameWithoutExt, SchematicEditModel? model = null)
    {
        string path = ViewPath(cellDir, ViewType.Schematic, fileNameWithoutExt);
        SchematicPersistence.SaveToFile(path, model ?? new SchematicEditModel(), cellName: cellName);
        return path;
    }

    /// <summary>
    /// The schematic a cell that already has a symbol starts with: one Pin per pin its primary symbol draws, Num the
    /// pin's own number and Name its name, in a column — a pin on the symbol's right-hand side on the right, every other
    /// on the left. A symbol pin connects to the cell's Pin of the SAME number, and a cell whose Pins do not match its
    /// symbol is left out of a run, so starting from the symbol's own pins is what makes the first wiring right: a
    /// three-pin amplifier symbol (in, ground, out) over a two-Pin S-parameter schematic is the field report this
    /// answers. Null when the cell has no symbol, or one with no pins.
    /// </summary>
    public static SchematicEditModel? PinsFromSymbol(string cellDir)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Symbol);
        if (primary.ResolvedName is not { } file) return null;
        CircuitRF.Design.Symbol.Symbol symbol;
        try { symbol = SymbolPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
        if (symbol.Pins.Count == 0) return null;

        double centre = (symbol.Pins.Min(p => p.LocalX) + symbol.Pins.Max(p => p.LocalX)) / 2;
        var model = new SchematicEditModel();
        int left = 0, right = 0;
        foreach (var pin in symbol.Pins.OrderBy(p => p.PortIndex))
        {
            bool onRight = pin.LocalX > centre + 1e-6;
            int row = onRight ? right++ : left++;
            var component = new EditableComponent
            {
                Symbol = SymbolKind.Pin,
                X = onRight ? 600 : -600, Y = row * 200,
                Rotation = onRight ? SymbolRotation.R180 : SymbolRotation.R0,
            };
            component.Parameters.Add(new EditableParameter { Name = "Num", Expression = pin.PortIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            // A pin named only by its number ("1") says nothing its Num does not; one named "GND" says what it is.
            string name = SymbolPinNames.IsStated(pin.Name) && NetName(pin.Name!) is { Length: > 0 } net ? net : "";
            component.Parameters.Add(new EditableParameter { Name = "Name", Expression = name, ShowOnSchematic = name.Length > 0 });
            model.Components.Add(component);
        }
        return model;

        // A pin name that is not a net name (spaces, '~') is left off rather than mangled into one.
        static string NetName(string text) => text.All(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '-' or '.') ? text : "";
    }

    /// <summary>Writes one <c>.csym</c> and returns its path: <paramref name="symbol"/>, or an empty one — no
    /// primitives, no pins, no ports — which loads through <c>SymbolPersistence</c> and opens on an empty canvas
    /// (R-aut3-8). A passive model's export hands its N-pin box here (brief-modelcomposer-12 R-mc12-7).</summary>
    public static string WriteSymbolView(string cellDir, string fileNameWithoutExt, CircuitRF.Design.Symbol.Symbol? symbol = null)
    {
        string path = ViewPath(cellDir, ViewType.Symbol, fileNameWithoutExt);
        SymbolPersistence.SaveToFile(path, symbol ?? new CircuitRF.Design.Symbol.Symbol([], [], portCount: 0));
        return path;
    }

    /// <summary>
    /// The empty layout a new <c>.clay</c> starts as. The display unit and snap come from
    /// <paramref name="tech"/>; <c>TechRef</c> is left null, so the layout resolves its technology
    /// the ordinary way (its own workspace's default) rather than pinning the one it was made under.
    ///
    /// <para>Separate from the write because the GUI opens an editor session on the very model it
    /// saved — reading it back off disk to get one would be a second object and a second chance to
    /// differ.</para>
    /// </summary>
    public static LayoutView NewLayoutView(Technology? tech) => new()
    {
        DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
        DisplayUnit  = tech?.DefaultDisplayUnit ?? LayoutUnit.Um,
        SnapDbu      = tech?.DefaultSnapDbu ?? 1000,
        AngleMode    = AngleMode.AnyAngle,
        TechRef      = null,
    };

    /// <summary>Writes one <c>.clay</c> into the cell folder's layout sub-folder and returns its
    /// path.</summary>
    public static string WriteLayoutView(string cellDir, string fileNameWithoutExt, LayoutView model)
    {
        string path = ViewPath(cellDir, ViewType.Layout, fileNameWithoutExt);
        LayoutPersistence.SaveToFile(path, model);
        return path;
    }

    /// <summary>
    /// The empty 3D view a new <c>.c3d</c> starts as (brief-em3d-41 R-em3d41-4, owner decision D4) —
    /// the GUI's New ▸ 3D View and <c>new cell --views 3d</c> both start here.
    ///
    /// <list type="bullet">
    /// <item><c>DbuPerMicron</c> is the cell's primary <c>.clay</c>'s when it has one, else
    /// <see cref="LayoutUnits.DefaultDbuPerMicron"/>.</item>
    /// <item><c>DisplayUnit</c> is the cell's primary <c>.clay</c>'s when it has one, else the
    /// technology's <see cref="Technology.DefaultDisplayUnit"/>, else µm. The cell's own layout is more
    /// specific than its technology: it is the unit someone already chose for this cell.</item>
    /// <item><c>SnapDbu</c> is the technology's, with <see cref="NewLayoutView"/>'s fallback.</item>
    /// <item><c>TechRef</c> is null, as a new layout's is: the workspace's default, resolved the
    /// ordinary way.</item>
    /// </list>
    /// </summary>
    /// <param name="cellDir">The cell the view is made in — read for its primary <c>.clay</c>, and
    /// nothing is created in it.</param>
    /// <param name="tech">The technology in force where the file will land, resolved by the caller;
    /// null is the supported no-technology state.</param>
    public static C3dDocument NewThreeDView(string cellDir, Technology? tech)
    {
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        LayoutUnit displayUnit = tech?.DefaultDisplayUnit ?? LayoutUnit.Um;

        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (primary.State is PrimaryState.SoleFile or PrimaryState.NamedPresent && primary.ResolvedName is { } clay)
        {
            string clayPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), clay);
            // An unreadable layout leaves the technology's answer standing: the new view is still
            // creatable, and the layout's own defect is what `check` reports about it.
            if (LayoutPersistence.TryReadUnits(clayPath) is { } units)
                (dbuPerMicron, displayUnit) = units;
        }

        var model = NewLayoutView(tech);
        return new C3dDocument
        {
            DbuPerMicron = dbuPerMicron,
            DisplayUnit  = displayUnit,
            SnapDbu      = model.SnapDbu,
            TechRef      = null,
            Look         = C3dLook.ForNewDocument(),
        };
    }

    /// <summary>Writes one <c>.c3d</c> into the cell's <c>3d/</c> sub-folder — creating the sub-folder,
    /// which is the only way it ever comes to exist (D10) — and returns its path.</summary>
    public static string WriteThreeDView(string cellDir, string fileNameWithoutExt, C3dDocument doc)
    {
        string path = ViewPath(cellDir, ViewType.ThreeD, fileNameWithoutExt);
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    /// <summary>
    /// Writes a schematic template's Data Display for a schematic that has just been written at
    /// <paramref name="schematicPath"/> (brief-template-two-port-stability R-tst-2/R-tst-4). Returns the
    /// display's path, and whether it was written — <c>false</c> when a <c>.cdd</c> of that name already
    /// exists, which is never overwritten; the caller names it to the user.
    ///
    /// <para><b>Where, and why there.</b> The file is the AUTHORED display a finished run already opens
    /// before it would create one (<see cref="ResultsWriter.AuthoredDisplayPath"/>, keyed by
    /// <see cref="ResultsWriter.SchematicKey"/>) — so "the schematic points at its display" needs no
    /// field in the <c>.csch</c>. <paramref name="runBaseDir"/> is the directory the run writes its
    /// netlist and <c>results/</c> under, which is the workspace root, NOT the schematic's own folder: a
    /// copy beside the <c>.csch</c> is a file no run looks for.</para>
    ///
    /// <para><b>Repointed, not copied verbatim.</b> <c>SelectedDataSource</c>, every trace's
    /// <c>SourcePath</c>/<c>XSourcePath</c> and every <c>SourceAliases</c> key that named the
    /// template's own results file are rewritten to the file THIS schematic's run writes
    /// (<see cref="ResultsWriter.ResolveFileName"/>). The "whichever source is selected" sentinel and
    /// any other reference are left alone. Done on the JSON rather than on the <c>.cdd</c> model because
    /// that model lives in <c>src/Render</c>, which references this project and not the other way round;
    /// the gate reads the result back through the ordinary reader.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The template display names no data source, so there is
    /// nothing to repoint and the copy would bind to whatever run happens to be newest.</exception>
    public static (string Path, bool Written) WriteTemplateDataDisplay(
        string runBaseDir, string schematicPath, string? resultsFileNameOverride, string displayJson)
    {
        string key    = ResultsWriter.SchematicKey(schematicPath);
        string target = ResultsWriter.AuthoredDisplayPath(runBaseDir, key);
        string source = ResultsWriter.ResolveFileName(resultsFileNameOverride, key);

        string text = RepointDataDisplay(displayJson, source);

        if (File.Exists(target)) return (target, false);

        // CreateNew, so a file that appears between the check and the write is still not overwritten.
        using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(text);
        return (target, true);
    }

    /// <summary>The repoint half of <see cref="WriteTemplateDataDisplay"/>, on its own so a test can
    /// read what it changed without a file system.</summary>
    public static string RepointDataDisplay(string displayJson, string newSource)
    {
        var root = JsonNode.Parse(displayJson) as JsonObject
            ?? throw new InvalidDataException("A Data Display must be a JSON object.");

        string? old = root["SelectedDataSource"]?.GetValue<string>();
        if (string.IsNullOrEmpty(old))
            throw new InvalidDataException("The template's Data Display names no data source to repoint.");

        root["SelectedDataSource"] = newSource;

        if (root["SourceAliases"] is JsonObject aliases && aliases[old] is { } alias)
        {
            aliases.Remove(old);
            aliases[newSource] = alias.DeepClone();
        }

        RepointTraceSources(root, old, newSource);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RepointTraceSources(JsonNode? node, string old, string newSource)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in new[] { "SourcePath", "XSourcePath" })
                    if (obj[name] is JsonValue v && v.TryGetValue<string>(out var s)
                        && string.Equals(s, old, StringComparison.Ordinal))
                        obj[name] = newSource;
                foreach (var (_, child) in obj.ToList()) RepointTraceSources(child, old, newSource);
                break;
            case JsonArray arr:
                foreach (var child in arr) RepointTraceSources(child, old, newSource);
                break;
        }
    }

    /// <summary>
    /// The path a view file of this type and name takes inside a cell folder, and the sub-folder
    /// created if a hand-made cell folder is missing it. A caller that has just run
    /// <see cref="CellFolder.CreateCellFolder"/> already has the first three; <c>3d/</c> is always
    /// made here.
    /// </summary>
    private static string ViewPath(string cellDir, ViewType type, string fileNameWithoutExt)
    {
        string dir = CellFolder.SubFolderPath(cellDir, type);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileNameWithoutExt + CellFolder.ViewExtension(type));
    }
}
