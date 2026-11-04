using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The three generated topics an MCP-only agent found missing when it designed a filter with no access
/// to the source: a component INDEX (the full catalogue is ~150 kB, more than one tool result holds),
/// the TECHNOLOGIES that ship (it found the ids only by provoking a refusal), and the MATERIALS that
/// ship (nothing listed them at all).
///
/// <para>All three are generated at every call from the thing that defines them — the registries, the
/// embedded <c>.ctech</c> files read through <see cref="ShippedTechnologies.Load(ShippedTechnologyEntry)"/>,
/// and the embedded <c>.cmat</c> libraries read through the library reader — under the rule
/// <see cref="Reference"/>'s own remarks give: a hand-written list would be a second description that
/// drifts from the first silently.</para>
/// </summary>
internal static partial class Reference
{
    /// <summary>Every type token in a line each — the cheap answer to "what types exist".</summary>
    public const string ComponentIndexTopic = "component-index";

    /// <summary>Every technology that ships, as <c>create</c>'s <c>tech</c> takes it.</summary>
    public const string TechnologiesTopic = "technologies";

    /// <summary>Every material in every library that ships. Not <c>materials</c>: that topic is the
    /// <c>.cmat</c> FORMAT, and this one is the shipped CONTENT written in it.</summary>
    public const string ShippedMaterialsTopic = "shipped-materials";

    // ── the component index ──────────────────────────────────────────────────

    private static int ComponentIndex()
    {
        var all = ComponentCatalog.All();
        JsonRun.Reference = new ReferenceReportJson(null, null, null,
            ComponentIndex: [.. all.Select(IndexRow)]);
        Console.Out.Write(RenderComponentIndex(all));
        return 0;
    }

    private static ReferenceComponentIndexJson IndexRow(CatalogEntry e) => new(
        e.Type,
        e.Nets.Count,
        e.Nets.Count is null ? e.Nets.DeterminedBy : null,
        [.. e.Symbols.Select(s => s.Category).Distinct()],
        Description(e),
        e.Simulatable,
        e.Placeable);

    /// <summary>The palette's own names for the type.</summary>
    private static string Description(CatalogEntry e)
    {
        // A type no tile draws has no name of its own to give; its row says so through the
        // [no palette entry] flag, and 'components <TYPE>' carries the note explaining what it is.
        return string.Join(" / ", e.Symbols.Select(s => s.DisplayName).Distinct());
    }

    private static string RenderComponentIndex(IReadOnlyList<CatalogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Component index — every .cnl type token. For one type's terminals and parameters:");
        sb.AppendLine("    circuitrf reference components <TYPE>");
        sb.AppendLine();
        sb.AppendLine($"{"TYPE",-16} {"NETS",-14} {"CATEGORY",-22} DESCRIPTION");
        foreach (var e in entries)
        {
            string nets = e.Nets.Count is { } n ? n.ToString(CultureInfo.InvariantCulture)
                        : e.Nets.DeterminedBy is { } by ? "set by " + by
                        : "-";
            string category = string.Join(", ", e.Symbols.Select(s => s.Category).Distinct());
            string description = Description(e);
            if (!e.Simulatable) description += "  [not simulatable]";
            else if (!e.Placeable) description += "  [no palette entry]";
            sb.AppendLine($"{e.Type,-16} {nets,-14} {(category.Length == 0 ? "-" : category),-22} {description}".TrimEnd());
        }
        return sb.ToString();
    }

    // ── the shipped technologies ─────────────────────────────────────────────

    private static int Technologies(string? id)
    {
        var all = ShippedTechnologies.All;
        var chosen = id is null
            ? all
            : [.. all.Where(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))];

        if (chosen.Count == 0)
            return JsonRun.Fail(CliDiagnostics.ReferenceUnknownTechnology(id!, string.Join(", ", all.Select(e => e.Id))));

        var rows = chosen.Select(TechnologyRow).ToList();
        JsonRun.Reference = new ReferenceReportJson(null, null, null, Technologies: rows);
        Console.Out.Write(RenderTechnologies(rows));
        return 0;
    }

    /// <summary>The technologies' own text — what the topic list and the resource listing measure.</summary>
    public static string TechnologiesText()
        => RenderTechnologies([.. ShippedTechnologies.All.Select(TechnologyRow)]);

    private static ReferenceTechnologyJson TechnologyRow(ShippedTechnologyEntry entry)
    {
        var tech = ShippedTechnologies.Load(entry);
        string LayerName(LayerKey k) => tech.Layers.FirstOrDefault(l => l.Key.Equals(k))?.Name ?? $"{k.Layer}/{k.Datatype}";

        var stackup = tech.Stackup.Layers.Select(l => new ReferenceStackupLayerJson(
            l.Kind.ToString(),
            l.Name,
            l.Kind == StackupKind.Via ? null : l.ThicknessDbu / (double)LayoutUnits.DefaultDbuPerMicron,
            string.IsNullOrWhiteSpace(l.Material) ? null : l.Material,
            l.Kind == StackupKind.Dielectric ? l.Epsr : null,
            l.Kind == StackupKind.Dielectric ? l.TanD : null,
            l.Kind == StackupKind.Conductor && l.SigmaSm > 0 ? l.SigmaSm : null,
            [.. l.DrawingLayers.Select(LayerName)],
            l.Kind == StackupKind.Conductor && l.IsGroundReference,
            l.Kind == StackupKind.Via ? l.SpanFromLayer : null,
            l.Kind == StackupKind.Via ? l.SpanToLayer : null,
            l.Films is { Count: > 0 } films
                ? [.. films.Select(f => $"{LayerName(f.Layer)} {f.SheetResistanceOhmPerSq.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)} Ohm/sq")]
                : null)).ToList();

        // A layer no Conductor or Via entry draws on is drawn-only: it joins nothing, which is what a
        // resistive film, a dielectric window and a recognition marker all are.
        var claimed = tech.Stackup.Layers
            .Where(l => l.Kind is StackupKind.Conductor or StackupKind.Via)
            .SelectMany(l => l.DrawingLayers).ToHashSet();

        var materials = tech.ResolvedMaterials.Select(m => m.Name).ToList();

        return new ReferenceTechnologyJson(
            entry.Id, tech.Name, entry.Id == ShippedTechnologies.DefaultId,
            tech.DefaultDisplayUnit.ToString().ToLowerInvariant(),
            tech.Stackup.Top.ToString().ToLowerInvariant(),
            tech.Stackup.Bottom.ToString().ToLowerInvariant(),
            stackup,
            tech.MaterialLibraries ?? [],
            materials,
            tech.DrcRules.Count,
            [.. tech.Layers.Select(l => new ReferenceLayerJson(
                l.Name, l.Key.Layer, l.Key.Datatype, claimed.Contains(l.Key)))],
            tech.DeviceRules.Count == 0 ? null
                : [.. tech.DeviceRules.Select(r => new ReferenceRecognitionRuleJson(
                    r.Name, r.Kind, r.Body, r.Terminals,
                    new SortedDictionary<string, string>(r.Parameters, StringComparer.Ordinal),
                    r.CopperBody, r.GroundTerminal))],
            ConstantsOf(tech));
    }

    // The derived names come first and are marked as such: a formula reading TfrSheetResistance in a
    // technology whose Constants are empty would otherwise look like a reference to nothing.
    private static string[]? ConstantsOf(Technology tech)
    {
        string[] rows =
        [
            .. MmicStackResolver.DeckConstants(tech).Select(c =>
                $"{c.Name} = {c.Expression}{(c.Unit is { Length: > 0 } u ? " " + u : "")} (from the process)"),
            .. tech.Constants.Select(c => $"{c.Name} = {c.Expression}{(c.Unit is { Length: > 0 } u ? " " + u : "")}"),
        ];
        return rows.Length == 0 || tech.DeviceRules.Count == 0 ? null : rows;
    }

    private static string RenderTechnologies(IReadOnlyList<ReferenceTechnologyJson> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Shipped technologies. A workspace is created on one by its id:");
        sb.AppendLine("    circuitrf new workspace <dir> --tech <id>      (MCP: create what=workspace tech=<id>)");
        sb.AppendLine("The workspace gets its own editable copy of the .ctech in tech/, and every document in it");
        sb.AppendLine("resolves that technology. 'reference technology' is the .ctech format itself.");
        sb.AppendLine();

        foreach (var t in rows)
        {
            sb.Append(t.Id);
            if (t.IsDefault) sb.Append("   (the default)");
            sb.AppendLine();
            sb.AppendLine($"  name: {t.Name}");
            sb.AppendLine($"  display unit: {t.DisplayUnit}    top: {t.TopBoundary}    bottom: {t.BottomBoundary}");
            sb.AppendLine("  stackup, top to bottom:");
            foreach (var l in t.Stackup)
            {
                string extent = l.ThicknessUm is { } um ? Num(um) + " um"
                              : l.SpanFrom is not null || l.SpanTo is not null ? $"{l.SpanFrom ?? "?"} to {l.SpanTo ?? "?"}"
                              : "";
                var row = new StringBuilder($"    {l.Kind,-10} {l.Name,-18} {extent,-12}");
                if (l.Material is { } m) row.Append($" {m}");
                if (l.Epsr is { } e) row.Append($"  er {Num(e)}");
                if (l.TanD is { } d) row.Append($"  tand {Num(d)}");
                if (l.SigmaSPerM is { } s) row.Append($"  sigma {Num(s)} S/m");
                if (l.DrawingLayers.Count > 0) row.Append($"  drawn on: {string.Join(", ", l.DrawingLayers)}");
                if (l.Films is { Count: > 0 } films) row.Append($"  resistor films: {string.Join(", ", films)}");
                if (l.GroundReference) row.Append("  [ground reference]");
                sb.AppendLine(row.ToString().TrimEnd());
            }
            sb.AppendLine($"  material libraries: {(t.MaterialLibraries.Count == 0 ? "none" : string.Join(", ", t.MaterialLibraries))}");
            sb.AppendLine($"  materials it can name: {(t.Materials.Count == 0 ? "none" : string.Join(", ", t.Materials))}");
            if (t.Layers is { Count: > 0 } layers)
            {
                sb.AppendLine("  drawing layers (a .clay shape's Layer is the Key):");
                foreach (var l in layers)
                    sb.AppendLine($"    {l.Layer}/{l.Datatype,-4} {l.Name}{(l.InStackup ? "" : "   [drawn only - joins nothing]")}");
            }
            sb.AppendLine($"  DRC rules: {t.DrcRules}");
            if (t.RecognitionRules is { Count: > 0 } rules)
            {
                sb.AppendLine("  device recognition (lvs --recognize; 'reference technology' explains the fields):");
                foreach (var r in rules)
                {
                    var row = new StringBuilder($"    {r.Name}: {r.Kind}, body {r.Body}, terminals {string.Join(" ; ", r.Terminals)}");
                    if (r.CopperBody) row.Append(", copper body");
                    if (r.GroundTerminal) row.Append(", + ground terminal");
                    if (r.Parameters.Count > 0)
                        row.Append(", ").Append(string.Join(", ", r.Parameters.Select(p => $"{p.Key} = {p.Value}")));
                    sb.AppendLine(row.ToString());
                }
                if (t.Constants is { Count: > 0 } constants)
                    sb.AppendLine($"    constants: {string.Join("; ", constants)}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── the shipped materials ────────────────────────────────────────────────

    private static int ShippedMaterials()
    {
        var rows = ShippedMaterialRows();
        JsonRun.Reference = new ReferenceReportJson(null, null, null, Materials: rows);
        Console.Out.Write(RenderShippedMaterials(rows));
        return 0;
    }

    /// <summary>The materials' own text — what the topic list and the resource listing measure.</summary>
    public static string ShippedMaterialsText() => RenderShippedMaterials(ShippedMaterialRows());

    private static IReadOnlyList<ReferenceMaterialJson> ShippedMaterialRows()
        => [.. MaterialLibraries.ShippedLibraryNames().SelectMany(lib =>
                MaterialLibraries.LoadShipped(lib).Select(m => new ReferenceMaterialJson(
                    m.Name, lib, Role(m), m.Epsr, m.TanD, m.Mur, m.Sigma20, m.Alpha20, m.ThermalK,
                    string.IsNullOrWhiteSpace(m.Source) ? null : m.Source)))];

    /// <summary>What a record's stated values make it — the <c>.cmat</c> format's own rule: σ alone is
    /// a conductor, εr alone a dielectric, both is ambiguous, and a material named Air is air.</summary>
    private static string Role(TechMaterial m)
    {
        if (string.Equals(m.Name, "Air", StringComparison.OrdinalIgnoreCase)) return "air";
        if (MaterialDispersion.IsDispersive(m)) return "dielectric (frequency-dependent)";
        return (m.Sigma20 is not null, m.Epsr is not null) switch
        {
            (true, false) => "conductor",
            (false, true) => "dielectric",
            (true, true)  => "both stated",
            _             => "neither stated",
        };
    }

    private static string RenderShippedMaterials(IReadOnlyList<ReferenceMaterialJson> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Shipped materials, by the library they ship in.");
        sb.AppendLine();
        sb.AppendLine("To USE one, the technology names the library: add its file name to the .ctech's");
        sb.AppendLine("MaterialLibraries list (a workspace created on a shipped technology already carries a copy");
        sb.AppendLine("beside its .ctech when that technology names it). To DEFINE a new material, add a record to");
        sb.AppendLine("the .ctech's own Materials list or to a .cmat of your own that the .ctech names — the format");
        sb.AppendLine("is 'reference materials'. A name two sources define with different values is refused.");
        sb.AppendLine();

        foreach (var group in rows.GroupBy(r => r.Library))
        {
            sb.AppendLine(group.Key);
            foreach (var m in group)
            {
                var row = new StringBuilder($"  {m.Name,-40} {m.Role,-11}");
                if (m.Epsr is { } e) row.Append($" er {Num(e)}");
                if (m.TanD is { } d) row.Append($"  tand {Num(d)}");
                if (m.Mur is { } u && u != 1) row.Append($"  mur {Num(u)}");
                if (m.Sigma20 is { } s) row.Append($" sigma20 {Num(s)} S/m");
                if (m.Alpha20 is { } a) row.Append($"  alpha20 {Num(a)} 1/K");
                if (m.ThermalK is { } k) row.Append($"  k {Num(k)} W/(m.K)");
                sb.AppendLine(row.ToString().TrimEnd());
                if (m.Source is { } src) sb.AppendLine($"  {"",-40} source: {src}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
