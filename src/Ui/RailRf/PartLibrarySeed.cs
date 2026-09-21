// The rows a design asked for, as rows — docs/sonnet-briefs/brief-authored-board-4-part-library-seeding.md
// (R-ab4-2, R-ab4-3).
//
// ── WHY A FILE OF ITS OWN, AND WHY IT IS PURE ──────────────────────────────────────────────────
//
// Seeding is the one place in this feature where a FIELD gets a value nobody typed, so it is the one
// place a wrong rule is invisible: a library row that carries a number is a library row the coverage
// count calls covered, and a run against it answers with that number rather than saying it had none.
// Keeping the rule here — a static function over a part number and an optional bill of materials —
// is what lets the gate drive it with no editor, no workspace and no window, and it is what stops a
// second copy of it growing beside the "create a library that does not exist yet" path, which needs
// exactly the same rows.
//
// ── THE RULE, IN ONE LINE ──────────────────────────────────────────────────────────────────────
//
// Nothing is INFERRED; only what a file the user handed over already states is carried across.
//
// With no bill of materials the row carries its part number and nothing else (R-ab4-2a).
// `PartLibraryRow`'s own summary is why: "every electrical field is optional, because a real
// maintained table is partly populated and §9's whole point is that partial population must be
// VISIBLE rather than filled in." A seeded row therefore still counts in `WithoutBiasCurve` and in
// `WithoutEsrBasis`, which is the honest answer — the part number is now known, and nothing about
// the part is.
//
// `DielectricClass` is the one worth naming twice (R-ab4-2c). Brief 11's ESR fallback keys on it, so
// a null one produces a part marked as having no class rather than a part quietly given X7R's
// dissipation factor — that record's own words.
//
// ── TWO THINGS THE BOM STATES THAT ARE DELIBERATELY NOT CARRIED ────────────────────────────────
//
//   • `BomDescriptionParse.CaseCode`. A description reading "0402" does not say which SCHEME, and
//     the eight codes that name a real case in both are 2.4x apart — `FootprintTokens`' own
//     ambiguity report exists for exactly that. Writing `smt:0402` here would state a scheme the
//     description never did. The footprint comes from the BOM's own footprint COLUMN or from
//     nowhere.
//   • `BomDescriptionParse.TolerancePercent`. `PartLibraryRow` has no home for it, and inventing one
//     would change the format, which §6 puts out of scope.
//
// ── AND THE TRAP IN THE VALUE COLUMN ───────────────────────────────────────────────────────────
//
// A BOM value is read as a capacitance ONLY where it carries the unit. `RailValueFormat.TryParse`
// falls back to the ladder's base unit for a bare number, so "100" in a value column — which on a
// decoupling BOM means 100 nF and on another means 100 pF — would be read as ONE HUNDRED FARADS,
// which is the "mark read without its scale" failure that once produced a run at 2 Hz looking
// entirely normal. A bare number is left null.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

/// <summary>Builds the <see cref="PartLibraryRow"/> a design's part number asks for.</summary>
public static class PartLibrarySeed
{
    /// <summary>
    /// A row for <paramref name="partNumber"/> — empty but for the part number, or carrying what
    /// <paramref name="bom"/> already said about it.
    /// </summary>
    /// <param name="partNumber">The design's own spelling, which is the key the model attaches to.</param>
    /// <param name="bom">The bill of materials the design was imported with, or null.</param>
    /// <param name="fromBom">True where the bill of materials named this part and something of it
    /// was carried across — what the row reports as its provenance (R-ab4-3b).</param>
    public static PartLibraryRow Row(string partNumber, BomTable? bom, out bool fromBom)
    {
        ArgumentNullException.ThrowIfNull(partNumber);

        var row = new PartLibraryRow { PartNumber = partNumber.Trim() };
        fromBom = false;

        if (Match(bom, row.PartNumber) is not { } entry) return row;

        row.Description     = Blank(entry.Description);
        row.Footprint       = Blank(entry.Footprint);
        row.DielectricClass = Blank(entry.Parsed.DielectricClass);
        row.VoltageRatingV  = Positive(entry.Parsed.VoltageRatingV);
        row.CapacitanceFarads = Capacitance(entry.Value);

        fromBom = row.Description is not null || row.Footprint is not null
               || row.DielectricClass is not null || row.VoltageRatingV is not null
               || row.CapacitanceFarads is not null;
        return row;
    }

    /// <summary>The same, where the provenance is not wanted.</summary>
    public static PartLibraryRow Row(string partNumber, BomTable? bom = null) =>
        Row(partNumber, bom, out _);

    /// <summary>
    /// The first bill-of-materials row naming this part number, or null.
    /// </summary>
    /// <remarks>
    /// <b>The first, in file order.</b> One internal part number sits in front of a list of approved
    /// manufacturers (<c>BomTable.RowsFor</c>'s own note), so several rows naming one part number is
    /// ordinary and they describe the same part — it is <i>several part numbers on one REFDES</i>
    /// that is the ambiguity that table reports, and this is the other axis.
    /// </remarks>
    private static BomRow? Match(BomTable? bom, string partNumber) =>
        bom is null || partNumber.Length == 0
            ? null
            : bom.Rows.FirstOrDefault(
                r => string.Equals(r.PartNumber, partNumber, StringComparison.OrdinalIgnoreCase));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static double? Positive(double? v) =>
        v is { } d && double.IsFinite(d) && d > 0 ? d : null;

    /// <summary>
    /// A BOM value column read as a marked capacitance, or null.
    /// </summary>
    /// <remarks>
    /// <b>The unit has to be there.</b> See this file's header: a bare "100" would be read as 100 F
    /// by the ladder's own base-unit fallback. Requiring the farad is also what leaves a ferrite's
    /// "600R" and a resistor's "10k" null rather than nonsense.
    /// </remarks>
    private static double? Capacitance(string? value)
    {
        if (Blank(value) is not { } text) return null;
        if (!text.EndsWith("F", StringComparison.OrdinalIgnoreCase)) return null;
        return RailValueFormat.TryParse(text, RailQuantity.Capacitance, out double c) && c > 0
            ? c
            : null;
    }

    /// <summary>
    /// Every part number in <paramref name="partNumbers"/>, in the order given, blanks dropped and
    /// repeats collapsed case-insensitively — <see cref="PartLibrary.Coverage"/>'s own rule, so the
    /// two cannot disagree about what "the parts this design asks for" means.
    /// </summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string>? partNumbers) =>
        partNumbers is null
            ? []
            : [.. partNumbers.Where(p => !string.IsNullOrWhiteSpace(p))
                             .Select(p => p.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase)];
}
