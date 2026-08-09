// Reads an INTERCONNECT TECHNOLOGY file — the ordered description of a fabrication process's
// back-end-of-line stack: what each layer is made of, how thick it is, and how the vertical
// connectors between conductors behave.
//
// The format is a plain-text, brace-delimited statement list, and is recognised STRUCTURALLY (a
// technology declaration plus at least one conductor statement) rather than by extension — the same
// rule every other reader in this repository follows, and for the same reason: the extension is a
// convention a kit may not keep, while the grammar is what is actually stable.
//
// This reader is deliberately GEOMETRY-ONLY and knows nothing about any particular process. Every
// name it produces comes out of the file it was handed.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>What one statement in an interconnect technology file describes.</summary>
public enum ProcessStackEntryKind
{
    /// <summary>An insulating slab. Carries a thickness and a relative permittivity.</summary>
    Dielectric,
    /// <summary>A metal (or otherwise conducting) sheet. Carries a thickness and a sheet resistance.</summary>
    Conductor,
    /// <summary>A vertical connector between two named conductors.</summary>
    Via,
}

/// <summary>
/// One statement, exactly as the file states it — micrometres and ohms, no conversion and no
/// interpretation. Turning this into a <see cref="Technology"/> is
/// <see cref="ProcessTechnologyBuilder"/>'s job, and it is kept separate because that conversion has
/// judgement in it (see the embedded-conductor rule there) while this does not.
/// </summary>
/// <param name="Kind">Which of the three statement forms this is.</param>
/// <param name="Name">The name the file gave it. A conductor's name is what a via's span refers to.</param>
/// <param name="ThicknessUm">Dielectric/Conductor only. Micrometres.</param>
/// <param name="RelativePermittivity">Dielectric only. 1 when unstated.</param>
/// <param name="SheetResistanceOhmPerSquare">Conductor only. 0 when unstated.</param>
/// <param name="MinWidthUm">Conductor only — the process minimum drawn width. 0 when unstated.</param>
/// <param name="MinSpacingUm">Conductor only — the process minimum drawn spacing. 0 when unstated.</param>
/// <param name="LayerType">
/// Conductor only — the ROLE the file gives this sheet, verbatim, or empty when it states none.
///
/// <para>The format uses it to mark the sheets that are part of a DEVICE rather than of the
/// interconnect. Carried because that distinction is the only thing in a process file that separates
/// "a layer signals route on" from "a layer a transistor is made of", and nothing else circuitRF can
/// read says it. Interpreted in one place — <see cref="ProcessTechnologyBuilder"/> — never here.</para>
/// </param>
/// <param name="SpanFrom">Via only — the conductor at one end, by name.</param>
/// <param name="SpanTo">Via only — the conductor at the other end, by name.</param>
/// <param name="CrossSectionUm2">Via only — one connector's cross-sectional area, µm².</param>
/// <param name="ResistanceOhms">Via only — one connector's resistance, ohms.</param>
public sealed record ProcessStackEntry(
    ProcessStackEntryKind Kind,
    string                Name,
    double                ThicknessUm                 = 0,
    double                RelativePermittivity        = 1,
    double                SheetResistanceOhmPerSquare = 0,
    double                MinWidthUm                  = 0,
    double                MinSpacingUm                = 0,
    string                LayerType                   = "",
    string?               SpanFrom                    = null,
    string?               SpanTo                      = null,
    double                CrossSectionUm2             = 0,
    double                ResistanceOhms              = 0);

/// <summary>
/// A whole interconnect technology file, read. <see cref="Entries"/> is in FILE ORDER, which is the
/// stack order (top first) — the reader never sorts it, because the order is the geometry.
/// </summary>
public sealed record ProcessStackDescription(
    string                          TechnologyName,
    IReadOnlyList<ProcessStackEntry> Entries,
    IReadOnlyList<string>            Notes);

/// <summary>Reads the interconnect technology text format. Framework-free (no Avalonia / Skia).</summary>
public static class ProcessStackReader
{
    // A statement: KEYWORD name { key=value … }. The body is matched non-greedily up to the first
    // closing brace, which is correct because the format does not nest braces.
    private static readonly Regex Statement = new(
        @"^[ \t]*(?<kw>DIELECTRIC|CONDUCTOR|VIA)[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t\r\n]*\{(?<body>[^}]*)\}",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TechnologyName = new(
        @"^[ \t]*TECHNOLOGY[ \t]*=[ \t]*(?<name>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Assignment = new(
        @"(?<k>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?<v>[^\s}]+)",
        RegexOptions.Compiled);

    // Any all-caps word starting a line is a statement keyword in this format. Used only to REPORT
    // the ones this reader does not act on, so a file carrying process data circuitRF ignores says so
    // rather than looking fully understood.
    private static readonly Regex LeadingKeyword = new(
        @"^[ \t]*(?<kw>[A-Z][A-Z0-9_]{2,})\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// True when the text is an interconnect technology file. Requires BOTH a technology declaration
    /// and a conductor statement: the declaration alone appears in unrelated files, and a file with
    /// no conductors describes no stack this reader could build anything from.
    /// </summary>
    public static bool LooksLikeStackFile(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string stripped = StripComments(text);
        if (!TechnologyName.IsMatch(stripped)) return false;

        foreach (Match m in Statement.Matches(stripped))
            if (string.Equals(m.Groups["kw"].Value, "CONDUCTOR", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    public static ProcessStackDescription Read(string text)
    {
        string stripped = StripComments(text ?? "");
        var    notes    = new List<string>();
        var    entries  = new List<ProcessStackEntry>();

        var nameMatch = TechnologyName.Match(stripped);
        string techName = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : "";
        if (techName.Length == 0)
            notes.Add("The file declares no technology name; the imported technology is named after the file.");

        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Statement.Matches(stripped))
        {
            string kw   = m.Groups["kw"].Value.ToUpperInvariant();
            string name = m.Groups["name"].Value;
            var    body = ReadAssignments(m.Groups["body"].Value);

            switch (kw)
            {
                case "DIELECTRIC":
                    entries.Add(new ProcessStackEntry(
                        ProcessStackEntryKind.Dielectric, name,
                        ThicknessUm:          Num(body, "THICKNESS"),
                        RelativePermittivity: Num(body, "ER", fallback: 1.0)));
                    break;

                case "CONDUCTOR":
                    entries.Add(new ProcessStackEntry(
                        ProcessStackEntryKind.Conductor, name,
                        ThicknessUm:                 Num(body, "THICKNESS"),
                        SheetResistanceOhmPerSquare: Num(body, "RPSQ"),
                        MinWidthUm:                  Num(body, "WMIN"),
                        MinSpacingUm:                Num(body, "SMIN"),
                        LayerType:                   Str(body, "LAYER_TYPE") ?? ""));
                    if (!seenNames.Add(name))
                        notes.Add($"Two conductors are both named \"{name}\"; a via naming it as an " +
                                  "endpoint cannot say which one it means.");
                    break;

                case "VIA":
                    entries.Add(new ProcessStackEntry(
                        ProcessStackEntryKind.Via, name,
                        SpanFrom:        Str(body, "FROM"),
                        SpanTo:          Str(body, "TO"),
                        CrossSectionUm2: Num(body, "AREA"),
                        ResistanceOhms:  Num(body, "RPV")));
                    break;
            }
        }

        // Report the statement kinds this reader passes over. A process file routinely carries
        // extraction-only data (temperature coefficients, density polynomials, etc.); circuitRF's
        // technology model has nowhere to put it, and saying so beats appearing to have read it.
        var handled = new HashSet<string>(
            ["TECHNOLOGY", "DIELECTRIC", "CONDUCTOR", "VIA"], StringComparer.Ordinal);
        var ignored = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in LeadingKeyword.Matches(stripped))
        {
            string kw = m.Groups["kw"].Value;
            if (!handled.Contains(kw)) ignored.Add(kw);
        }
        if (ignored.Count > 0)
            notes.Add("Read for geometry only; these statements carry no circuitRF equivalent and were " +
                      $"passed over: {string.Join(", ", ignored)}.");

        if (entries.Count == 0)
            notes.Add("No dielectric, conductor or via statements were found.");

        return new ProcessStackDescription(techName, entries, notes);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes '$'-introduced comments. Applied line by line rather than to the whole text so a '$'
    /// inside one line cannot swallow the rest of the file.
    /// </summary>
    private static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            int cut = line.IndexOf('$');
            sb.Append(cut >= 0 ? line[..cut] : line).Append('\n');
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ReadAssignments(string body)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Assignment.Matches(body))
            d[a.Groups["k"].Value] = a.Groups["v"].Value;
        return d;
    }

    private static double Num(Dictionary<string, string> body, string key, double fallback = 0)
        => body.TryGetValue(key, out var raw) &&
           double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : fallback;

    private static string? Str(Dictionary<string, string> body, string key)
        => body.TryGetValue(key, out var raw) && raw.Length > 0 ? raw : null;
}
