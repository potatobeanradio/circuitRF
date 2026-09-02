using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Cells;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One thing in a SPICE file that could become a cell — a <c>.model</c> card or a <c>.subckt</c>
/// definition. Exactly one of <see cref="Card"/> and <see cref="Subcircuit"/> is set.
/// </summary>
/// <param name="Name">What the file calls it.</param>
/// <param name="TypeLabel">How it is written — <c>.NMOS</c>, <c>.SUBCKT</c>.</param>
/// <param name="Detail">What would be built, or the reason nothing can be.</param>
public sealed record SpiceCellCandidate(
    string                 Name,
    string                 TypeLabel,
    string                 Detail,
    ModelCardTranslation?  Card,
    SubcircuitTranslation? Subcircuit)
{
    public bool IsSupported => Card?.IsSupported ?? Subcircuit?.IsSupported ?? false;
}

/// <summary>What one SPICE file holds, once read.</summary>
/// <param name="Candidates">Everything in it that could become a cell, supported or not, in file order.</param>
/// <param name="Subcircuits">Every translated definition — a candidate's nested calls resolve against this.</param>
/// <param name="Notes">The reader's own notes: lines it skipped, definitions it could not use.</param>
/// <param name="Error">Why nothing could be read at all. Null when the file was read.</param>
public sealed record SpiceCellScan(
    IReadOnlyList<SpiceCellCandidate>    Candidates,
    IReadOnlyList<SubcircuitTranslation> Subcircuits,
    IReadOnlyList<SpiceNetlistNote>      Notes,
    string?                              Error)
{
    /// <summary>The ones that can actually become a cell.</summary>
    public IReadOnlyList<SpiceCellCandidate> Supported
        => [.. Candidates.Where(c => c.IsSupported)];

    /// <summary>
    /// Every file that contributed to this read, entry point first — the deck's INCLUDE CLOSURE.
    ///
    /// <para>Carried up from <see cref="SpiceNetlistResult.FilesRead"/> because a SPICE reference is
    /// rarely one file and only the reader knows which others it opened. <c>WorkspaceArchiveScanner</c>
    /// is what asks: a <c>.lib</c> that arrives at a colleague without the models it includes is a
    /// design missing a piece of itself, and the failure is silent until they simulate.</para>
    /// </summary>
    public IReadOnlyList<string> FilesRead { get; init; } = [];

    /// <summary>
    /// The <c>.lib</c> section names THIS file declares, in declaration order — the alternatives it
    /// offers. Empty for the overwhelming majority of files, which declare none.
    ///
    /// <para>Only the scanned file's own sections, not those of anything it includes: an included
    /// file's sections are a separate axis (<see cref="SpiceNetlistResult.Sections"/> is grouped by
    /// file for exactly that reason), and flattening them would offer one pick where the deck offers
    /// several.</para>
    /// </summary>
    public IReadOnlyList<string> SectionNames { get; init; } = [];

    /// <summary>Which section was read, or null for the whole file — today's default.</summary>
    public string? Section { get; init; }
}

/// <summary>
/// The one door both import gestures go through — the project tree's <b>Copy to Workspace as
/// Cell…</b> and <b>File ▸ Import ▸ Model or Subcircuit…</b>.
///
/// <para><b>A <c>.model</c> card and a <c>.subckt</c> definition are the same gesture and must not
/// become two.</b> They arrive in the same files, from the same suppliers, and a user who has one
/// does not first classify it — they have "the file for this part", which very often holds both. So
/// the file is read once, everything importable in it is listed together, and the choice is made in
/// one picker. What differs — a card is a parameter set, a subcircuit is a netlist — differs below
/// this line, in <see cref="ModelCardCellBuilder"/> and <see cref="SubcircuitCellBuilder"/>.</para>
/// </summary>
public static class SpiceCellImport
{
    /// <summary>
    /// Reads <paramref name="path"/> and says what circuitRF can make of everything in it. Never
    /// throws for a file it cannot read — that is the <see cref="SpiceCellScan.Error"/> field,
    /// because this runs behind a menu item and a stack trace is not an answer.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="section">
    /// Which <c>.lib</c> section to read, or null for the whole file. <b>Null is not "all of them"</b>:
    /// sections are alternatives, so a whole-file read skips every one and records their names in
    /// <see cref="SpiceCellScan.SectionNames"/> — which is exactly the pass a caller uses to find out
    /// what to offer before asking for one.
    /// </param>
    public static SpiceCellScan Scan(string path, string? section = null)
    {
        SpiceNetlistResult result;
        try
        {
            result = SpiceNetlistReader.ReadFile(path, section);
        }
        catch (Exception ex)
        {
            return new SpiceCellScan([], [], [], $"{Path.GetFileName(path)} could not be read: {ex.Message}")
            {
                FilesRead = [Path.GetFullPath(path)],
                Section   = section,
            };
        }

        var subcircuits = SubcircuitTranslator.TranslateAll(result);
        var cards       = SpiceModelCardTranslation.TranslateAll(result.ModelCards);

        var candidates = new List<SpiceCellCandidate>(subcircuits.Count + cards.Count);

        // Subcircuits first: a file holding both almost always states the cards as the SUPPORT for a
        // subcircuit that is the part, so that is the thing the user came for.
        foreach (var s in subcircuits)
            candidates.Add(new SpiceCellCandidate(
                s.Name, ".SUBCKT", DescribeSubcircuit(s), null, s));

        foreach (var c in cards)
            candidates.Add(new SpiceCellCandidate(
                c.Card.Name, "." + c.Card.ModelType.Trim().ToUpperInvariant(),
                DescribeCard(c), c, null));

        return new SpiceCellScan(
            candidates, subcircuits, result.Notes,
            candidates.Count > 0
                ? null
                : $"{Path.GetFileName(path)} contains no '.model' cards and no '.subckt' definitions.")
        {
            FilesRead    = result.FilesRead,
            SectionNames = SectionsDeclaredBy(result, path),
            Section      = section,
        };
    }

    /// <summary>
    /// The sections <paramref name="path"/> itself declares. Matched on the FULL path, which is what
    /// <see cref="SpiceNetlistReader.ReadFile"/> keys its per-file section list by.
    /// </summary>
    private static IReadOnlyList<string> SectionsDeclaredBy(SpiceNetlistResult result, string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return []; }

        foreach (var set in result.Sections)
            if (string.Equals(set.File, full, StringComparison.OrdinalIgnoreCase)) return set.Names;

        return [];
    }

    private static string DescribeCard(ModelCardTranslation t)
        => t.Binding is { } b
            ? $"{b.Parameters.Count} parameter(s)"
              + (b.Unmapped.Count > 0
                    ? $" — {b.Unmapped.Count} not carried: {string.Join(", ", b.Unmapped)}"
                    : "")
            : t.Refusal ?? "";

    private static string DescribeSubcircuit(SubcircuitTranslation t)
        => t.Refusal
        ?? $"{t.Elements.Count} component(s), {t.Definition.Ports.Count} port(s)"
           + (t.Dependencies.Count > 0
                ? $" — also creates {t.Dependencies.Count} cell(s) for the subcircuit(s) it calls: "
                  + string.Join(", ", t.Dependencies)
                : "");

    /// <summary>
    /// Builds the chosen candidate. Returns where the cell landed, what else was created alongside
    /// it, and the lines to post to Messages.
    /// </summary>
    public static SubcircuitCellResult Write(
        string parentDir, string cellName, SpiceCellCandidate candidate, SpiceCellScan scan,
        string? sourceFile = null)
        => WriteMany(parentDir, [(candidate, cellName)], scan, sourceFile);

    /// <summary>
    /// Builds SEVERAL chosen definitions in one gesture — the whole reason for a multi-select picker.
    ///
    /// <para><b>The subcircuits go through one shared plan and the cards go one at a time</b>, which
    /// is not an implementation detail: two subcircuits can share a core cell and a shared core must
    /// be written once (<see cref="SubcircuitCellBuilder.WriteMany"/>), while two <c>.model</c> cards
    /// share nothing at all — a card IS its parameter set.</para>
    ///
    /// <para>Reported as one result: the cell the caller opens is the first the user chose, and every
    /// other folder this created is listed in <see cref="SubcircuitCellResult.AlsoCreated"/>.</para>
    /// </summary>
    public static SubcircuitCellResult WriteMany(
        string parentDir,
        IReadOnlyList<(SpiceCellCandidate Candidate, string CellName)> chosen,
        SpiceCellScan scan,
        string? sourceFile = null)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(scan);
        if (chosen.Count == 0)
            throw new InvalidOperationException("Nothing was chosen to import.");

        foreach (var (c, _) in chosen)
            if (c.Subcircuit is null && c.Card is null)
                throw new InvalidOperationException($"'{c.Name}' names neither a card nor a subcircuit.");

        var subs = chosen.Where(p => p.Candidate.Subcircuit is not null)
                         .Select(p => (Top: p.Candidate.Subcircuit!, p.CellName))
                         .ToList();

        var report = new List<string>();
        var dirs   = new Dictionary<string, (string Dir, string Schematic)>(StringComparer.Ordinal);
        var extra  = new List<string>();          // folders created that nobody explicitly chose

        // Cards first: each is one folder with its own already-exists refusal, so a name clash is
        // reported before the subcircuit group has written anything at all.
        foreach (var (c, name) in chosen)
        {
            if (c.Card is not { } card) continue;
            var r = ModelCardCellBuilder.Write(parentDir, name, card);
            dirs[name] = (r.CellDir, r.SchematicPath);
            report.AddRange(r.Report);
        }

        if (subs.Count > 0)
        {
            var r = SubcircuitCellBuilder.WriteMany(parentDir, subs, scan.Subcircuits, sourceFile);
            report.AddRange(r.Report);
            extra.AddRange(r.AlsoCreated);
            foreach (var (_, name) in subs)
            {
                string dir = Path.Combine(parentDir, name);
                dirs[name] = (dir, string.Equals(dir, r.CellDir, StringComparison.Ordinal)
                                       ? r.SchematicPath
                                       : SchematicIn(dir, name));
            }
        }

        var primary = dirs[chosen[0].CellName];

        // Everything this import created except the one the caller opens — the other chosen
        // definitions AND the shared cores underneath them, de-duplicated because a chosen
        // definition may also be a dependency of another chosen one.
        var also = dirs.Values.Select(v => v.Dir)
                       .Concat(extra)
                       .Where(d => !string.Equals(d, primary.Dir, StringComparison.Ordinal))
                       .Distinct(StringComparer.Ordinal)
                       .ToList();

        return new SubcircuitCellResult(primary.Dir, primary.Schematic, also, report);
    }

    private static string SchematicIn(string cellDir, string cellName)
        => Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Schematic),
            cellName + CellFolder.ViewExtension(ViewType.Schematic));

    /// <summary>
    /// The name to seed the cell-name box with. A subcircuit's own name is already a folder name in
    /// every file that ships one, and a card's very nearly is.
    /// </summary>
    public static string SuggestCellName(SpiceCellCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return SubcircuitCellBuilder.SafeCellName(candidate.Name);
    }
}
