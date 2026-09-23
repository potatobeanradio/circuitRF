// Reads another part library's rows into this one — the way a library built for one design is reused
// by the next (field report, 2026-09-23: a team that buys the same part numbers board after board was
// rebuilding the library from nothing in every workspace).
//
// ── IT COPIES; IT DOES NOT LINK ──────────────────────────────────────────────────────────────────
//
// A `.crail` can already NAME a library in another workspace (the import dialog's Part library row),
// and that is the right answer for a library a team maintains in one place. This is the other one:
// the rows arrive in THIS library, which is saved, archived and revision-controlled with this
// workspace, so the design keeps its answer when the other workspace moves or changes.
//
// ── THIS LIBRARY'S VALUES WIN, AND EVERY DISAGREEMENT IS NAMED ───────────────────────────────────
//
// A part number the source has and this library does not becomes a new row, whole. For one both have,
// a field the source states fills a field this library leaves EMPTY — which is exactly the state a
// library seeded from a design's part numbers is in. Where both state a value and they differ, this
// library's is kept and the pair is reported: two maintained libraries disagreeing about one part
// number is a question for the reader, and overwriting a value somebody typed here would answer it
// silently. Free text (description, footprint) fills an empty field and is otherwise left alone —
// two wordings of one part are not a disagreement.
//
// ── PART NUMBERS MATCH EXACTLY ───────────────────────────────────────────────────────────────────
//
// Ignoring case, and nothing else. The table import's prefix match exists for a SUPPLIER's tool that
// trims packaging codes; both sides here are internal part numbers, and two that differ in their last
// characters are two parts.
//
// ── AN ATTACHED MODEL FILE STAYS WHERE IT IS ─────────────────────────────────────────────────────
//
// A row's ModelRef is relative to its own library's folder, so copying the text would point one hop
// away from the file. It is restated against this library's folder instead, and named in the report,
// because the Touchstone it names still lives beside the other library.

using CircuitRF.Design.Matching;

namespace CircuitRF.Design.RailRf;

public static class PartLibraryMerge
{
    /// <summary>
    /// Merges <paramref name="source"/>'s rows into <paramref name="target"/> in place.
    /// </summary>
    /// <param name="sourceName">What the report calls the source library.</param>
    public static PartLibraryImportReport Apply(PartLibrary target, PartLibrary source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var added   = new List<string>();
        var updated = new List<string>();
        var notes   = new List<string>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var incoming in source.Rows)
        {
            string pn = incoming.PartNumber.Trim();
            if (pn.Length == 0 || !seen.Add(pn)) continue;

            string? model = Rebase(incoming.ModelRef, source.BaseDirectory, target.BaseDirectory);

            if (target.Part(pn) is not { } row)
            {
                row = new PartLibraryRow
                {
                    PartNumber              = pn,
                    Description             = incoming.Description,
                    Footprint               = incoming.Footprint,
                    DielectricClass         = incoming.DielectricClass,
                    VoltageRatingV          = incoming.VoltageRatingV,
                    CapacitanceFarads       = incoming.CapacitanceFarads,
                    SelfResonantFrequencyHz = incoming.SelfResonantFrequencyHz,
                    StatedInductanceHenries = incoming.StatedInductanceHenries,
                    EsrOhms                 = incoming.EsrOhms,
                    ModelRef                = model,
                };
                row.BiasCurve.AddRange(incoming.BiasCurve);
                target.Rows.Add(row);
                added.Add(pn);
                if (model is not null) notes.Add(ModelNote(pn, model));
                continue;
            }

            var conflicts = new List<string>();
            bool changed = false;
            changed |= Fill(row.Description, incoming.Description, v => row.Description = v);
            changed |= Fill(row.Footprint,   incoming.Footprint,   v => row.Footprint = v);
            changed |= Fill(row.DielectricClass, incoming.DielectricClass, v => row.DielectricClass = v,
                            conflicts, "class");
            changed |= Fill(row.VoltageRatingV, incoming.VoltageRatingV, v => row.VoltageRatingV = v,
                            conflicts, "rating", v => $"{v:0.##} V");
            changed |= Fill(row.CapacitanceFarads, incoming.CapacitanceFarads, v => row.CapacitanceFarads = v,
                            conflicts, "C", v => Quantity(v, MatchQuantity.Capacitance));
            changed |= Fill(row.SelfResonantFrequencyHz, incoming.SelfResonantFrequencyHz,
                            v => row.SelfResonantFrequencyHz = v,
                            conflicts, "f₀", v => Quantity(v, MatchQuantity.Frequency));
            changed |= Fill(row.StatedInductanceHenries, incoming.StatedInductanceHenries,
                            v => row.StatedInductanceHenries = v,
                            conflicts, "L", v => Quantity(v, MatchQuantity.Inductance));
            changed |= Fill(row.EsrOhms, incoming.EsrOhms, v => row.EsrOhms = v,
                            conflicts, "ESR", v => Quantity(v, MatchQuantity.Resistance));

            if (model is not null)
            {
                if (row.ModelRef is not { Length: > 0 })
                {
                    row.ModelRef = model;
                    changed = true;
                    notes.Add(ModelNote(pn, model));
                }
                else if (!SameFile(row.ModelRef, target.BaseDirectory, model))
                    conflicts.Add($"model file {row.ModelRef} kept, {sourceName} names {model}");
            }

            if (incoming.BiasCurve.Count > 0)
            {
                if (row.BiasCurve.Count == 0)
                {
                    row.BiasCurve.AddRange(incoming.BiasCurve);
                    changed = true;
                }
                else if (!row.BiasCurve.SequenceEqual(incoming.BiasCurve))
                    conflicts.Add($"bias curve kept, {sourceName}'s differs");
            }

            if (changed) updated.Add(pn);
            if (conflicts.Count > 0)
                notes.Add($"{pn}: this library's value kept where the two disagree — " +
                          string.Join("; ", conflicts) + ".");
        }

        if (source.Rows.Count == 0)
            notes.Add($"{sourceName} holds no parts, so nothing was read.");

        return new(added, updated, notes);
    }

    private static bool Fill(string? current, string? incoming, Action<string> assign)
    {
        if (current is { Length: > 0 } || incoming is not { Length: > 0 }) return false;
        assign(incoming);
        return true;
    }

    private static bool Fill(string? current, string? incoming, Action<string> assign,
                             List<string> conflicts, string what)
    {
        if (incoming is not { Length: > 0 }) return false;
        if (current is not { Length: > 0 }) { assign(incoming); return true; }
        if (!string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase))
            conflicts.Add($"{what} {current} kept, the other says {incoming}");
        return false;
    }

    private static bool Fill(double? current, double? incoming, Action<double> assign,
                             List<string> conflicts, string what, Func<double, string> format)
    {
        if (incoming is not { } v) return false;
        if (current is not { } c) { assign(v); return true; }
        if (c != v) conflicts.Add($"{what} {format(c)} kept, the other says {format(v)}");
        return false;
    }

    private static string Quantity(double v, MatchQuantity q) => MatchValueFormat.FormatWithUnit(v, q, null, 4);

    /// <summary>The model reference restated against the target library's folder, or null.</summary>
    private static string? Rebase(string? modelRef, string fromDir, string toDir)
    {
        if (modelRef is not { Length: > 0 } r) return null;
        if (fromDir.Length == 0) return r;

        string absolute = Core.RefPath.Resolve(fromDir, r);
        return toDir.Length == 0
            ? absolute
            : Core.RefPath.ToStored(Path.GetRelativePath(toDir, absolute));
    }

    private static bool SameFile(string current, string baseDir, string incoming)
    {
        if (baseDir.Length == 0) return string.Equals(current, incoming, StringComparison.Ordinal);
        return string.Equals(Path.GetFullPath(Core.RefPath.Resolve(baseDir, current)),
                             Path.GetFullPath(Core.RefPath.Resolve(baseDir, incoming)),
                             OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                                                         : StringComparison.Ordinal);
    }

    private static string ModelNote(string pn, string model) =>
        $"{pn}'s model file is {model} — it is referenced where it lives, not copied.";
}
