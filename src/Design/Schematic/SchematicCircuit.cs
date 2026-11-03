// A `.csch` becomes a circuit — the ONE place the round trip lives.
//
// ── WHY THIS IS NOT IN src/Cli ANY MORE ───────────────────────────────────────────────────────
//
// It was, and `CircuitSource` still calls it. It moved down for brief-lvs-4-schematic-netlist.md
// R-lvs4-1a: LVS reads a schematic too, LVS lives below the UI firewall in `src/Design`, and
// `src/Design` cannot reference `src/Cli`. The alternative was a second copy of
// `NetExtractor.Extract → CnlWriter.Write → CnlReader.Read` — and two implementations of one
// meaning drift, which is this repository's recurring scar. A drift HERE would mean the netlist
// LVS compares is not the netlist the design runs as, and nothing would say so.
//
// The claim the CLI's own gate makes ("exactly one file turns a schematic into netlist TEXT") is
// unchanged and is now stronger: zero files in `src/Cli` name `CnlWriter.Write`, and this one does.

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The <c>(Library, TestBench)</c> a schematic holds — read the way the APPLICATION reads it,
/// which is not the obvious way.
///
/// <para><b>A `.csch` goes through the `.cnl` on its way to the elaborator, and that round trip is
/// load-bearing.</b> The GUI's Simulate is
/// <c>NetExtractor.Extract → CnlWriter.Write → CnlReader.Read → Elaborator</c>
/// (<c>WorkspaceViewModel.WriteNetlist</c> then <c>SchematicRunService.Prepare</c>), and the two
/// readers do NOT agree about bare words: a schematic parameter is an EXPRESSION, so
/// <c>BiasTee=on</c> read straight out of extraction fails elaboration with "Unresolved name 'on'",
/// while the same value written to a `.cnl` and read back is quoted by <c>CnlReader</c> and
/// elaborates. Four of the shipped example schematics carry exactly that value.</para>
///
/// <para>So a reader that skipped the round trip would report four errors the application does not
/// have — which is the precise failure R-aut4-2 exists to prevent, in the other direction: not a
/// rule the GUI does not enforce, but a rule the GUI does not APPLY. The finding is recorded in
/// <c>src/Cli/RESOLVED.md</c>; what this file does is refuse to have a second opinion about it.</para>
///
/// <para><b>Nothing is written</b> (R-aut4-6, R-lvs4-1c). The `.cnl` exists as a string and is
/// handed straight to <c>CnlReader.Read</c> with the schematic's own reference base as the source
/// directory — the same argument <c>CnlReader.ReadFile</c> derives from a real path, so relative
/// SnP and model references resolve identically.</para>
/// </summary>
public static class SchematicCircuit
{
    /// <summary>
    /// Extraction, then the `.cnl` round trip — see this type's own remarks for why the second half
    /// is not optional.
    /// </summary>
    /// <param name="hostWorkspaceRoot">The workspace a LOOSE schematic is run from — a window's own, or
    /// the CLI's <c>--workspace</c> (<see cref="SchematicEditModel.HostWorkspaceRoot"/>). Ignored for a
    /// schematic inside a workspace.</param>
    public static (Library Lib, TestBench Tb) FromSchematic(string cschPath, string? hostWorkspaceRoot = null)
    {
        var (extracted, name) = ExtractFile(cschPath, hostWorkspaceRoot);
        return FromExtraction(extracted, name, cschPath, hostWorkspaceRoot);
    }

    /// <summary>The round trip of an extraction <see cref="ExtractFile"/> made — the second half of
    /// <see cref="FromSchematic(string, string?)"/>, for a caller that reports the extraction first.</summary>
    public static (Library Lib, TestBench Tb) FromExtraction(
        NetExtractor.ExtractionResult extracted, string name, string cschPath, string? hostWorkspaceRoot = null)
        => RoundTrip(CnlTextOf(extracted, name), name, ReferenceBaseOf(cschPath, hostWorkspaceRoot));

    /// <summary>
    /// The schematic at <paramref name="cschPath"/> read and extracted as a run extracts it, with its
    /// test-bench name. The one load every path-taking entry point here goes through, so the host
    /// workspace reaches every one of them.
    /// </summary>
    public static (NetExtractor.ExtractionResult Extracted, string Name) ExtractFile(
        string cschPath, string? hostWorkspaceRoot = null)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        model.HostWorkspaceRoot = hostWorkspaceRoot;
        string name = Path.GetFileNameWithoutExtension(cschPath);
        return (ExtractInWorkspace(model, name, DiskCellResolver.Instance, cschPath), name);
    }

    /// <inheritdoc cref="FromSchematic(string)"/>
    public static (Library Lib, TestBench Tb) FromSchematic(
        SchematicEditModel model, string testBenchName, string? sourceDir)
        => RoundTrip(CnlTextOf(model, testBenchName), testBenchName, sourceDir);

    /// <summary>
    /// The SECOND half on its own: netlist text in, circuit out.
    ///
    /// <para>Separate from <see cref="CnlTextOf(SchematicEditModel, string)"/> only for the caller
    /// that needs the extraction's own <see cref="NetExtractor.ExtractionResult"/> as well — LVS
    /// wants <c>CellPorts</c>, which is the schematic's own answer about port ORDER and is not
    /// carried by a <c>TestBench</c> (R-lvs4-4b). It is the same two lines either way.</para>
    /// </summary>
    public static (Library Lib, TestBench Tb) RoundTrip(
        string cnlText, string testBenchName, string? sourceDir)
        => new CnlReader().Read(cnlText, testBenchName, sourceDir);

    /// <summary>
    /// The extraction, with the resolver the WINDOW passes.
    ///
    /// <para>DiskCellResolver, never null. A null resolver tells <see cref="NetExtractor"/> the
    /// caller is flat, and it answers by SKIPPING every cell instance with no conflict note — so a
    /// design whose device lives in a sub-cell extracted to the passive network around the hole,
    /// ran, converged on every point, and reported nothing.</para>
    /// </summary>
    public static NetExtractor.ExtractionResult Extract(SchematicEditModel model, string testBenchName)
        => NetExtractor.Extract(model, testBenchName, DiskCellResolver.Instance);

    /// <summary>
    /// The `.cnl` text an already-performed extraction writes to — the FIRST half of the round trip,
    /// on its own (R-aut11-1).
    ///
    /// <para><b>There is one extraction and this is it.</b> <c>circuitrf netlist</c> writes exactly
    /// this string and every run verb reads exactly this string, so the file a caller is handed is
    /// not merely equivalent to what a run consumed — it is the same bytes. A second writer here,
    /// with its own provenance line or its own ordering, would give a caller a netlist that runs
    /// differently from the schematic it came out of, and nothing would say so.</para>
    ///
    /// <para>The provenance comment is deliberately constant: a timestamp or a verb name in it would
    /// make two extractions of one schematic differ, which is precisely what the byte-for-byte gate
    /// exists to detect.</para>
    /// </summary>
    public static string CnlTextOf(NetExtractor.ExtractionResult extracted, string testBenchName)
        => CnlWriter.Write(extracted.TestBench, extracted.Library, $"extracted from {testBenchName}");

    /// <inheritdoc cref="CnlTextOf(NetExtractor.ExtractionResult, string)"/>
    public static string CnlTextOf(SchematicEditModel model, string testBenchName)
        => CnlTextOf(Extract(model, testBenchName), testBenchName);

    /// <inheritdoc cref="CnlTextOf(NetExtractor.ExtractionResult, string)"/>
    public static string CnlTextOf(string cschPath, string? hostWorkspaceRoot = null)
    {
        var (extracted, name) = ExtractFile(cschPath, hostWorkspaceRoot);
        return CnlTextOf(extracted, name);
    }

    /// <summary>
    /// The extraction a schematic AT A PATH gets — with the kit corners of the workspace it belongs to bound as
    /// Simulate binds them (<c>WorkspaceViewModel.WriteNetlist</c>): the design's own selections, and the binder a
    /// named corner's kit selections resolve through (brief-yield-6 R-ya6-1, docs/design/yield.md §10). A workspace
    /// offering no corner axes extracts exactly as before, byte for byte.
    /// </summary>
    public static NetExtractor.ExtractionResult ExtractInWorkspace(
        SchematicEditModel model, string testBenchName, ICellResolver cells, string cschPath)
    {
        var axes = WorkspaceCorners.ForDocument(cschPath);
        if (axes.Count == 0) return NetExtractor.Extract(model, testBenchName, cells);

        var problems = new List<string>();
        var bound = WorkspaceCorners.Bind(axes, model.CornerSelections, problems);
        var result = NetExtractor.Extract(model, testBenchName, cells, bound.Variables,
            (selections, found) => WorkspaceCorners.BindingsFor(axes, selections, found), bound.Sections);
        return problems.Count == 0 ? result
            : result with { Conflicts = [.. problems, .. result.Conflicts] };
    }

    /// <summary>
    /// brief-em3d-87 R-em3d87-2 — <see cref="CnlTextOf(string)"/> and the files its extraction read: the schematic itself, then
    /// each sub-cell schematic the descent resolved and that cell's <c>.ccell</c>, each once. The same extraction, with
    /// <see cref="DiskCellResolver"/> watched rather than replaced, so the text is the bytes <see cref="CnlTextOf(string)"/>
    /// gives. <see cref="SpiceCellImport.FilesRead"/> is the precedent for the shape.
    /// </summary>
    public static (string Text, IReadOnlyList<string> FilesRead) CnlTextAndFilesOf(string cschPath)
    {
        var (text, files, _, _) = CnlTextAndFilesOf(cschPath, null);
        return (text, files);
    }

    /// <inheritdoc cref="CnlTextAndFilesOf(string)"/>
    /// <remarks>With the workspace a loose schematic is run from, and the cells it chose there.</remarks>
    public static (string Text, IReadOnlyList<string> FilesRead, IReadOnlyList<NetExtractor.HostCellChoice> HostCellChoices,
                   NetExtractor.ExtractionResult Extracted)
        CnlTextAndFilesOf(string cschPath, string? hostWorkspaceRoot)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        model.HostWorkspaceRoot = hostWorkspaceRoot;
        string name = Path.GetFileNameWithoutExtension(cschPath);
        var watch = new WatchedResolver(Path.GetFullPath(cschPath));
        var extracted = ExtractInWorkspace(model, name, watch, cschPath);
        return (CnlTextOf(extracted, name), watch.Files, extracted.HostCellChoices, extracted);
    }

    /// <summary>
    /// brief-em3d-87 — one schematic's OWN contribution to a circuit, as text: its extraction's test bench without the cell
    /// definitions its sub-cells contribute (each of those is a file of its own). What a run's input manifest hashes a
    /// <c>.csch</c> by, so moving a label changes nothing and a sub-cell's edit names the sub-cell, not every parent above it.
    /// </summary>
    public static string OwnCnlTextOf(string cschPath)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        string name = Path.GetFileNameWithoutExtension(cschPath);
        return CnlWriter.Write(Extract(model, name).TestBench, null, $"extracted from {name}");
    }

    /// <summary><see cref="DiskCellResolver"/>, recording each schematic and <c>.ccell</c> it resolves.</summary>
    private sealed class WatchedResolver(string top) : ICellResolver
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal) { top };
        public List<string> Files { get; } = [top];

        public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
        {
            if (HierarchyResolver.ResolvePrimaryPath(cellInstance, containingModel) is { } primary)
            {
                string full = Path.GetFullPath(primary);
                if (_seen.Add(full)) Files.Add(full);
                string ccell = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(full))!, Cells.CellFolder.CcellFileName);
                if (File.Exists(ccell) && _seen.Add(ccell)) Files.Add(ccell);
            }
            return DiskCellResolver.Instance.Resolve(cellInstance, containingModel);
        }
    }

    /// <summary>
    /// What a relative file reference inside a document resolves against: <b>its owning root</b>
    /// (<see cref="OwningRoot"/> — the nearest model folder, else the workspace root), and the
    /// document's own folder only when it belongs to neither.
    ///
    /// <para><b>This is the GUI's base, not a headless one</b> (<c>SnpPathPolicy</c> states the rule
    /// and <c>MoveRefRegistry</c> repairs against it). Simulate writes <c>netlist.cnl</c> at the
    /// workspace root and elaborates from there, so an SnP's <c>File</c> is stored relative to the
    /// root — and a run verb that used the schematic's own folder instead answered differently about
    /// the same design. The shipped S-Parameters example is where that surfaced: it ran headlessly
    /// and, opened, reported its Touchstone file missing at the workspace root. Which of the two was
    /// wrong was not the interesting part — that they disagreed at all is what made a verb's answer
    /// stop meaning anything about the window.</para>
    ///
    /// <para>The no-workspace fallback is the workspace the loose document is RUN from when there is
    /// one — where Simulate writes <c>netlist.cnl</c> for a Known File — and otherwise the document's
    /// own folder, the same fallback <c>SnpPathPolicy.Resolve</c> takes.</para>
    /// </summary>
    public static string? ReferenceBaseOf(string documentPath, string? hostWorkspaceRoot = null)
        => ReferenceBaseOfDirectory(Path.GetDirectoryName(Path.GetFullPath(documentPath)), hostWorkspaceRoot);

    /// <inheritdoc cref="ReferenceBaseOf"/>
    /// <param name="documentDirectory">The folder the document is in; null for an unsaved one.</param>
    public static string? ReferenceBaseOfDirectory(string? documentDirectory, string? hostWorkspaceRoot = null)
    {
        string? own = string.IsNullOrWhiteSpace(documentDirectory) ? null : Path.GetFullPath(documentDirectory);
        return OwningRoot.Of(own)
            ?? (string.IsNullOrWhiteSpace(hostWorkspaceRoot) ? null : WorkspaceRootFinder.Normalize(hostWorkspaceRoot))
            ?? own;
    }
}
