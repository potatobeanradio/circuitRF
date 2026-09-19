// What a railRF result is written OUT as — the provenance every writer carries, the CSV, and the
// DataSet (railrf.md §11.5, R-rail10-4, R-rail10-5).
//
// ══ WHY THIS IS IN src/Design AND NOT IN src/Cli ══════════════════════════════════════════════
//
// It was in `src/Cli/Rail.cs`, private, and that was correct while the CLI was the only thing that
// wrote a rail result to a file. It stopped being correct the moment the WINDOW's Export button was
// wired (owner, 2026-09-19: "Compare… and Export are disabled and say they are not wired yet. Why
// not?"), because `src/Ui` may not reference `src/Cli` — so the only two ways to give the window an
// Export were to move this down or to write it a second time.
//
// The repository already states which of those is right, in `src/Design/CLAUDE.md` and in
// `src/Cli/Authoring.cs`: *an operation that lives only in a view model is not a capability, and a
// verb that re-implements one diverges from it silently.* It reads the same in the mirror. A second
// CSV writer would agree with the first one today and drift the first time either was touched, and
// the drift would be invisible because both produce a plausible file — which is exactly the failure
// R-rail10-8 ("no second export path") exists to forbid.
//
// So the verb and the window now call ONE function per format, and a test can compare their bytes.
//
// ══ WHAT IS DELIBERATELY NOT HERE ═════════════════════════════════════════════════════════════
//
// The PAGE. `RailReportPage` is in `CircuitRF.Render`, which sits ABOVE this project, so the
// sections' WORDING is here (as `RailReportText`) and the drawing is there — the same split
// `RailComparisonReport.Sections` already makes, and for the reason `src/Render`'s own `.csproj`
// gives: it draws, it does not decide what a finding says.
//
// Nothing here touches the filesystem, writes to a console or reports a diagnostic. It returns
// bytes, strings and cubes; the caller decides where they go and what to say about it. That is
// `src/Design/CLAUDE.md`'s "diagnostics are RETURNED, never posted", applied to output.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CircuitRF.Design.Layout.Pdn;
using RfCore.Data;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// One heading and its lines, as a report page prints them.
/// </summary>
/// <remarks>
/// The same shape as <see cref="RailComparisonSection"/> and a separate type for its reason: this
/// project sits below <c>CircuitRF.Render</c> and cannot name <c>RailReportSection</c>. Two records
/// of three fields is the price of the firewall and it is the right price.
/// </remarks>
public sealed record RailReportText(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// What a reader six months later has to know and has no status strip to read it from.
/// </summary>
/// <remarks>
/// <b>Computed once and handed to every writer</b> — the console, the CSV header, the <c>.npy</c>
/// group, the report page, <c>--json</c> and now the window's own Export — because six writers each
/// assembling their own would be six chances for one of them to omit the line that matters.
/// Overview §4 rule 1.
/// </remarks>
public sealed record RailProvenance(
    string Model,
    string Extent,
    double TemperatureCelsius,
    int PartsWithoutBiasCurve,
    int PartsModelledFromFile,
    bool Indicative,
    string? PartLibraryPath,
    int PartsReferenced,
    string ArtworkPath,
    string TechnologyPath)
{
    /// <summary>The banner, in the order every surface prints it.</summary>
    public IReadOnlyList<string> Lines =>
    [
        $"model: {Model} · reference {Extent} · {TemperatureCelsius:0.#} °C",
        PartLibraryPath is null
            ? "no part library resolved: no part is modelled from a file and no bias-curve "
            + "coverage is known"
            // Nothing to count is not a count of nothing. A rail that declares no part rows has
            // no decoupling bank IN THE DOCUMENT, and printing "0 part(s) modelled from a file"
            // there reads as a checked board that came back clean.
            : PartsReferenced == 0
            ? "no part is declared on the rail(s) reported here, so nothing was asked of the "
            + "part library: no bias-curve coverage is known"
            : $"{PartsModelledFromFile} of {PartsReferenced} part number(s) modelled from a file, "
            + $"{PartsWithoutBiasCurve} with no bias curve"
            + (Indicative
                ? " — some ESRs are class defaults, so any peak height derived from them is INDICATIVE"
                : ""),
        $"artwork: {ArtworkPath}",
        $"stackup: {TechnologyPath}",
    ];

    /// <summary>How a model kind reads on the banner.</summary>
    public static string ModelText(PdnModelKind model) =>
        model == PdnModelKind.Accurate ? "Accuracy (meshed)" : "Fast";

    /// <summary>How a reference extent reads on the banner. <b>Two of the three say they are
    /// optimistic</b>, because a reader of the numbers has to know which basis produced them.</summary>
    public static string ExtentText(RailReferenceExtent extent) => extent switch
    {
        RailReferenceExtent.AsImported      => "as imported",
        RailReferenceExtent.FilledToOutline => "filled to outline — optimistic",
        _                                   => "infinite — an upper bound",
    };

    /// <summary>
    /// The provenance of one run, from what the run was given.
    /// </summary>
    /// <remarks>
    /// <b>The arithmetic is here and not at each call site</b> — R-rail11-6's two headline counts
    /// are about THE PARTS ON THIS BOARD, which are the rails' own rows and not the library's, and
    /// a surface that asked <c>PartLibrary.Coverage</c> about the library instead would answer a
    /// different question with the same-looking number (a shared library of 500 rows in front of a
    /// twelve-part rail prints "412 with no bias curve" and that reads as a statement about the
    /// board). Doing it once is what keeps the window's status strip and the verb's banner from
    /// disagreeing about one document.
    /// </remarks>
    public static RailProvenance Of(
        PdnModelKind model,
        RailDocument document,
        IReadOnlyList<RailSpec> rails,
        IReadOnlyList<RailDcResult> results,
        PartLibrary? library,
        string? libraryPath,
        string artworkPath,
        string technologyName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(results);

        string extent = rails.Select(r => ExtentText(r.ReferenceExtent))
                             .Distinct(StringComparer.Ordinal).ToList() is { Count: 1 } one
            ? one[0]
            : "mixed across the rails reported here";

        double temperature = results.Count > 0
            ? results[0].Netlist.Provenance.CopperTemperatureCelsius
            : document.Settings.CopperTemperatureCelsius;

        var referenced = rails
            .SelectMany(r => r.Parts)
            .Select(p => p.PartNumber)
            .Where(pn => !string.IsNullOrWhiteSpace(pn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var coverage = library is not null && referenced.Count > 0 ? library.Coverage(referenced) : null;

        int fromFile = library is null
            ? 0
            : referenced.Count(pn => library.ResolveModel(pn).Source == PartModelSource.AttachedFile);

        return new RailProvenance(
            ModelText(model),
            extent,
            temperature,
            coverage?.WithoutBiasCurve.Count ?? 0,
            fromFile,
            coverage is { Indicative.Count: > 0 },
            libraryPath,
            referenced.Count,
            artworkPath,
            technologyName);
    }
}

/// <summary>
/// The CSV, the <see cref="DataSet"/> and the report page's text — one writer each, for every
/// surface that writes a rail result out.
/// </summary>
public static class RailExport
{
    /// <summary>The group an <c>.npy</c> or a <c>.mat</c> carries the provenance in — <c>em</c>'s
    /// diagnostics group is the precedent, and the name is said once here.</summary>
    public const string ProvenanceGroup = "provenance";

    /// <summary>
    /// §11.5's CSV: the ranked breakdown, the via flags, the parts and the ports.
    /// </summary>
    /// <remarks>
    /// <b>The provenance is a comment header</b> (R-rail10-5) — the CSV's own spelling of what the
    /// <c>.npy</c> carries as a group and the page carries in its banner.
    ///
    /// <para><b>The anti-resonance table is named and empty.</b> §11.5 lists it and it is a
    /// frequency answer; saying so costs one line and is the difference between "this board has
    /// none" and "this phase does not compute them", which a reader of a CSV cannot otherwise tell
    /// apart.</para>
    /// </remarks>
    public static string Csv(
        RailDocument doc, IReadOnlyList<RailDcResult> results, RailProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(provenance);

        var sb = new StringBuilder();
        sb.Append("# circuitRF railRF — DC\n");
        sb.Append($"# document,{Q(doc.Name)}\n");
        foreach (string line in provenance.Lines) sb.Append($"# {line}\n");

        sb.Append("\nsection,rail,label,value,unit,detail\n");

        foreach (var r in results)
        {
            foreach (var p in r.Ports)
            {
                sb.Append($"port,{Q(r.RailName)},{Q(p.Name)},{F(p.VoltageV)},V,{Q(p.Describe())}\n");
                if (p.CurrentA is { } i)
                    sb.Append($"port-current,{Q(r.RailName)},{Q(p.Name)},{F(i)},A,\n");
            }

            foreach (var s in r.Sources)
                sb.Append($"source,{Q(r.RailName)},{Q(s.Name)},{F(s.CurrentA)},A,{F(s.ShareOfTotal)} share\n");

            foreach (var row in r.Breakdown)
                sb.Append($"breakdown,{Q(r.RailName)},{Q(row.Label)},{F(row.DropV)},V," +
                          $"{F(row.ResistanceOhms)} Ohm / {F(row.CurrentA)} A / {row.ElementCount} element(s)\n");

            foreach (var t in r.ViaCheck.Transitions)
                sb.Append(
                    $"via,{Q(r.RailName)},{Q($"{t.FromLayer} to {t.ToLayer}")}," +
                    $"{F(t.Worst?.CurrentA ?? 0)},A," +
                    $"{t.Count} barrel(s) / {F(t.TotalCurrentA)} A total / peaking {F(t.PeakingFactor)}\n");

            foreach (var flag in r.ViaCheck.Flags)
                sb.Append($"via-flag,{Q(r.RailName)},{Q(flag.Describe())},,,\n");

            foreach (string n in r.ViaCheck.Notes)
                sb.Append($"via-note,{Q(r.RailName)},,,,{Q(n)}\n");

            foreach (var reg in r.Regulators)
                sb.Append($"regulator,{Q(r.RailName)},{Q(reg.Refdes)},{F(reg.InputVoltageV)},V,{Q(reg.Describe())}\n");

            foreach (string f in r.Findings) sb.Append($"finding,{Q(r.RailName)},,,,{Q(f)}\n");
            foreach (string n in r.Notes)    sb.Append($"note,{Q(r.RailName)},,,,{Q(n)}\n");
        }

        sb.Append("# anti-resonances: none are reported here. They are a FREQUENCY answer and this "
                + "is the DC phase — an empty table here does not mean this board has none.\n");
        return sb.ToString();

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Every rail's own <see cref="DataSet"/>, grouped by rail name, plus the provenance.
    /// </summary>
    /// <remarks>
    /// <b>Nothing invents a result type</b> — each rail's cubes are the ones <c>DcResultPacker</c>
    /// already packed, moved into a group named after the rail so two rails of one board do not
    /// collide. The provenance rides as a labelled AXIS rather than as prose, because that is the
    /// only string a <c>.npy</c> carries and a reader that can find the numbers can find the labels
    /// beside them.
    /// </remarks>
    public static DataSet Pack(IReadOnlyList<RailDcResult> results, RailProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(provenance);

        var merged = new DataSet();

        foreach (var r in results)
            foreach (string group in r.Data.Groups)
                foreach (var (name, cube) in r.Data.CubesIn(group))
                    merged.AddToGroup(
                        group.Length == 0 ? r.RailName : $"{r.RailName}.{group}", name, cube);

        var lines = provenance.Lines;
        var axis = new Axis("provenance", [.. Enumerable.Range(0, lines.Count).Select(i => (double)i)],
                            "index", [.. lines]);
        merged.AddToGroup(ProvenanceGroup, "Lines", new DataCube([axis], new double[lines.Count]));
        merged.AddToGroup(ProvenanceGroup, "TemperatureC", DataCube.Scalar(provenance.TemperatureCelsius));
        merged.AddToGroup(ProvenanceGroup, "PartsWithoutBiasCurve",
                          DataCube.Scalar(provenance.PartsWithoutBiasCurve));
        merged.AddToGroup(ProvenanceGroup, "PartsModelledFromFile",
                          DataCube.Scalar(provenance.PartsModelledFromFile));
        merged.AddToGroup(ProvenanceGroup, "Indicative", DataCube.Scalar(provenance.Indicative ? 1.0 : 0.0));

        return merged;
    }

    /// <summary>
    /// The report page's text blocks. Every line is the RESULT's own sentence — see
    /// <c>RailReportPage</c>'s header for why none of them is re-worded here.
    /// </summary>
    /// <param name="results">The rails, in solve order.</param>
    /// <param name="rows">How many breakdown rows to print per rail.</param>
    /// <param name="all">True to print every breakdown row, ignoring <paramref name="rows"/>.</param>
    public static IReadOnlyList<RailReportText> Sections(
        IReadOnlyList<RailDcResult> results, int rows, bool all)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sections = new List<RailReportText>();

        foreach (var r in results)
        {
            sections.Add(new RailReportText(
                $"Rail '{r.RailName}' — ports", [.. r.Ports.Select(p => p.Describe())]));

            int shown = all ? r.Breakdown.Count : Math.Min(rows, r.Breakdown.Count);
            sections.Add(new RailReportText(
                $"Rail '{r.RailName}' — where the drop is",
                [.. r.Breakdown.Take(shown).Select(
                     row => $"{row.DropV * 1e3:0.###} mV ({row.ShareOfTotal:P0}) · {row.Label}")]));

            if (r.ViaCheck.Transitions.Count > 0)
                sections.Add(new RailReportText(
                    $"Rail '{r.RailName}' — vias",
                    r.ViaCheck.Flags.Count == 0
                        ? [$"{r.ViaCheck.Transitions.Count} transition(s), none over its limit"]
                        : [.. r.ViaCheck.Flags.Select(f => f.Describe())]));

            if (r.Findings.Count > 0)
                sections.Add(new RailReportText($"Rail '{r.RailName}' — findings", r.Findings));
        }

        return sections;
    }
}
