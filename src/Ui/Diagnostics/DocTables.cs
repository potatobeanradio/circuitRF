using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Tables generated from the LIVE registries, so the prose never re-types a fact the code already
/// knows.
///
/// <para>Lives in <c>src/Ui</c> rather than in <c>tools/DocGen</c> because it reads
/// <see cref="ComponentTypeRegistry"/> and <see cref="ToolbarCatalog"/> — and because the test suite
/// asserts that the generated per-button table has exactly one row per manifest entry, in order,
/// which it can only do if it can call the same generator the docs run calls.</para>
///
/// <para>Figures are the obvious drift; this is the second one, and it is quieter. Component
/// parameters, their defaults, their units and whether they show on the schematic are all declared
/// in <see cref="ComponentTypeRegistry"/>, and every hand-written table restating them is a copy
/// waiting to go stale — silently, because a wrong default in a document does not fail anything.</para>
/// </summary>
public static class DocTables
{
    /// <summary>
    /// A parameter a reader must never be offered as a row, because it is not a value: it is a
    /// machine-written payload that CARRIES the component.
    ///
    /// <para>Match's and wBond's <c>Design</c> are base64 of the whole design's JSON. The parameter
    /// panel already refuses to show either as a text row (<c>IsMatchPanelParameter</c>,
    /// <c>IsWBondPanelParameter</c>) for the same reason: nobody can read it, act on it, or safely
    /// edit it, and hand-editing it is the one way to produce a component that refuses at
    /// elaboration. A documentation table listing it is the same mistake with a wider audience —
    /// it invites exactly the edit the interface declines to offer (owner, 2026-08-20).</para>
    /// </summary>
    private static bool IsOpaquePayload(SymbolKind kind, string name)
        => (kind == SymbolKind.Match || kind == SymbolKind.WBond)
        && string.Equals(name, "Design", StringComparison.Ordinal);

    /// <summary>A component's parameter table, read from <see cref="ComponentTypeRegistry"/>.</summary>
    public static string ComponentParameters(SymbolKind kind, int ports)
    {
        var rows = ComponentTypeRegistry.DefaultParameters(kind, ports)
                                        .Where(p => !IsOpaquePayload(kind, p.Name))
                                        .ToList();
        if (rows.Count == 0)
            return "<p class=\"small\">No fixed parameters — this component's rows are authored by the user.</p>";

        // The MEANING column appears only when the registry has meanings to put in it. Today only
        // VerilogA does. That is a real limit of what can be generated and it is better stated than
        // papered over: the registry knows a parameter's name, default, unit and visibility — the
        // facts that drift — but not what it is FOR. Those words belong in the Markdown beside the
        // table, because the alternative is prose in a C# string literal.
        bool anyMeaning = rows.Any(p => ComponentTypeRegistry.ParameterDescription(kind, p.Name).Length > 0);

        var sb = new StringBuilder();
        sb.AppendLine("<table class=\"param-table\">");
        sb.Append("<thead><tr><th>Name</th><th>Default</th><th>Unit</th><th>On schematic</th>");
        if (anyMeaning) sb.Append("<th>Meaning</th>");
        sb.AppendLine("</tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var p in rows)
        {
            sb.Append("<tr>")
              .Append($"<td>{E(p.Name)}</td>")
              .Append($"<td>{(p.Expression.Length == 0 ? "&mdash;" : "<code>" + E(p.Expression) + "</code>")}</td>")
              .Append($"<td>{(p.Unit.Length == 0 ? "&mdash;" : E(p.Unit))}</td>")
              .Append($"<td>{(p.ShowOnSchematic ? "shown" : "&mdash;")}</td>");
            if (anyMeaning)
            {
                string meaning = ComponentTypeRegistry.ParameterDescription(kind, p.Name);
                sb.Append($"<td>{(meaning.Length == 0 ? "&mdash;" : E(meaning))}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");
        return sb.ToString();
    }

    /// <summary>
    /// A toolbar's per-button table, generated from the SAME manifest traversal that produced the
    /// figure — so the numbering in the prose and the numbering in the picture cannot disagree.
    /// </summary>
    /// <param name="entries">The toolbar manifest, in presentation order.</param>
    /// <param name="buttonCell">
    /// Renders the second column: the BUTTON ITSELF, captured on its own. Three columns became two
    /// here (owner, 2026-08-20) — a <c>Button</c> column holding an <c>x:Name</c> and an <c>Icon</c>
    /// column holding an icon's enum name said the same thing twice and neither said what the reader
    /// is looking for, which is "which one of these is it on the toolbar". A null callback falls back
    /// to the name, for a caller with no captures to hand (the tests).
    /// </param>
    public static string ToolbarButtons(IReadOnlyList<ToolbarCatalog.Entry> entries,
                                        Func<ToolbarCatalog.Entry, string>? buttonCell = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<table class=\"param-table toolbar-table\">");
        sb.AppendLine("<thead><tr><th>#</th><th>Button</th><th>What it does</th></tr></thead>");
        sb.AppendLine("<tbody>");
        foreach (var e in entries.Where(x => x.Index > 0))
        {
            string cell = buttonCell?.Invoke(e)
                       ?? (e.Id.Length == 0 ? "&mdash;" : "<code>" + E(e.Id) + "</code>");
            sb.Append("<tr>")
              .Append($"<td>{e.Index}</td>")
              .Append($"<td class=\"glyph-cell\">{cell}</td>")
              .Append($"<td>{(e.Tooltip.Length == 0 ? "<em>no tooltip</em>" : E(e.Tooltip))}</td>")
              .AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");
        return sb.ToString();
    }

    /// <summary>
    /// The workspace figure's legend: one row per numbered region, in the figure's own order.
    ///
    /// <para>Generated from <see cref="WorkspaceRegions.Catalog"/> for the same reason the toolbar
    /// table is generated from the toolbar manifest — the number in the picture and the number in the
    /// table have to be the same number, and a hand-written legend is one renumbering away from
    /// pointing at the wrong panel.</para>
    /// </summary>
    public static string WorkspaceRegionLegend()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<table class=\"param-table\">");
        sb.AppendLine("<thead><tr><th>#</th><th>Region</th><th>What it is for</th></tr></thead><tbody>");
        foreach (var r in WorkspaceRegions.Catalog)
            sb.AppendLine($"<tr><td>{r.Index}</td><td class=\"nowrap\"><b>{E(r.Title)}</b></td>"
                        + $"<td>{E(r.What)}</td></tr>");
        sb.AppendLine("</tbody></table>");
        return sb.ToString();
    }

    /// <summary>The component list, one row per user-placeable kind, each linking to its section.</summary>
    public static string ComponentIndex()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<table class=\"param-table\">");
        sb.AppendLine("<thead><tr><th>Component</th><th>Type</th><th>Category</th></tr></thead><tbody>");
        foreach (var (kind, _, ports) in SymbolArtworkGenerator.Catalog)
        {
            var info = ComponentTypeRegistry.Get(kind);
            string anchor = DocAnchors.ComponentAnchor(kind);
            sb.AppendLine($"<tr><td><a href=\"#{anchor}\">{E(ComponentTypeRegistry.DisplayName(kind, ports))}</a></td>"
                        + $"<td>{E(kind.ToString())}</td><td>{E(info.Category.ToString())}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
        return sb.ToString();
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
