using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;

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
    public static SpiceCellScan Scan(string path)
    {
        SpiceNetlistResult result;
        try
        {
            result = SpiceNetlistReader.ReadFile(path);
        }
        catch (Exception ex)
        {
            return new SpiceCellScan([], [], [], $"{Path.GetFileName(path)} could not be read: {ex.Message}");
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
                : $"{Path.GetFileName(path)} contains no '.model' cards and no '.subckt' definitions.");
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
        string parentDir, string cellName, SpiceCellCandidate candidate, SpiceCellScan scan)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(scan);

        if (candidate.Subcircuit is { } sub)
            return SubcircuitCellBuilder.Write(parentDir, cellName, sub, scan.Subcircuits);

        if (candidate.Card is { } card)
        {
            var r = ModelCardCellBuilder.Write(parentDir, cellName, card);
            return new SubcircuitCellResult(r.CellDir, r.SchematicPath, [], r.Report);
        }

        throw new InvalidOperationException($"'{candidate.Name}' names neither a card nor a subcircuit.");
    }

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
