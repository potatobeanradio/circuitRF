// HZN-2 R-hzn2-1 — the refusal a translation returns: every item circuitRF cannot represent, at once.
//
// A design circuitRF cannot represent is refused, never approximated (brief-hzn-0 D3), and the refusal lists EVERY
// unsupported item, grouped by kind, with the objects that use each. Nothing opens half a design, so the translator
// keeps going after the first item and collects them all; the caller sees one list, not one problem per attempt.

using System.Text;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What sort of thing a refusal item is (brief-hzn-0 D3's list).</summary>
public enum HznRefusalKind
{
    /// <summary>A creation step (<c>CreateTorus</c>, …).</summary>
    CreationStep,
    /// <summary>An operation step (<c>Fillet</c>, <c>Subtract</c> on a sheet, …).</summary>
    Operation,
    /// <summary>A face, edge or vertex named by index that cannot be resolved to one of the geometry's faces.</summary>
    FaceReference,
    /// <summary>A boundary kind, or a boundary setting circuitRF cannot state.</summary>
    Boundary,
    /// <summary>An excitation kind, or an excitation setting circuitRF cannot state.</summary>
    Excitation,
    /// <summary>A material that is not defined, or whose properties circuitRF cannot state.</summary>
    Material,
    /// <summary>An expression function, constant, unit or identifier circuitRF does not have, or an expression whose
    /// meaning cannot be carried across exactly.</summary>
    Expression,
    /// <summary>A setting of an object or a design that has no equivalent (an interior that is not solved, a coordinate
    /// system other than the global one, a circuit design).</summary>
    Setting,
}

/// <summary>
/// One unsupported item. <see cref="What"/> names it (<c>CreateTorus</c>, <c>Radiation</c>, <c>function round</c>);
/// <see cref="Why"/> says why, in a sentence; <see cref="Users"/> are the objects, boundaries or setups that use it.
/// Two items with the same kind, name and reason are one item with the union of their users.
/// </summary>
public sealed record HznRefusalItem(HznRefusalKind Kind, string What, string Why, IReadOnlyList<string> Users);

/// <summary>The collector the translator writes to, and the grouped list it ends as.</summary>
public sealed class HznRefusalBuilder
{
    private readonly List<(HznRefusalKind Kind, string What, string Why, string User)> _raw = [];

    public bool Any => _raw.Count > 0;

    public void Add(HznRefusalKind kind, string what, string why, string user) => _raw.Add((kind, what, why, user));

    /// <summary>Items grouped by (kind, what, why), in kind order then first appearance, each with its distinct users.</summary>
    public IReadOnlyList<HznRefusalItem> Items() =>
        [.. _raw.GroupBy(r => (r.Kind, r.What, r.Why))
                .OrderBy(g => g.Key.Kind)
                .Select(g => new HznRefusalItem(g.Key.Kind, g.Key.What, g.Key.Why,
                    [.. g.Select(x => x.User).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal)]))];
}

public static class HznRefusalText
{
    /// <summary>The group heading a kind is listed under.</summary>
    public static string Heading(HznRefusalKind kind) => kind switch
    {
        HznRefusalKind.CreationStep => "Creation steps",
        HznRefusalKind.Operation => "Operations",
        HznRefusalKind.FaceReference => "References by face, edge or vertex index",
        HznRefusalKind.Boundary => "Boundaries",
        HznRefusalKind.Excitation => "Excitations",
        HznRefusalKind.Material => "Materials",
        HznRefusalKind.Expression => "Expressions",
        HznRefusalKind.Setting => "Settings",
        _ => kind.ToString(),
    };

    /// <summary>The whole list, grouped by kind: one heading, then one line per item with its users.</summary>
    public static string Render(IReadOnlyList<HznRefusalItem> items)
    {
        var sb = new StringBuilder();
        foreach (var group in items.GroupBy(i => i.Kind))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Heading(group.Key)).Append(':');
            foreach (var item in group)
            {
                sb.Append("\n  ").Append(item.What).Append(" — ").Append(item.Why);
                if (item.Users.Count > 0) sb.Append(" (").Append(string.Join(", ", item.Users)).Append(')');
            }
        }
        return sb.ToString();
    }
}

public static class HznTranslationDiagnostics
{
    /// <summary>R-hzn2-1 — the design cannot be opened; every item is listed.</summary>
    public static Diagnostic Refused(string design, IReadOnlyList<HznRefusalItem> items) => Diagnostic.Create(
        "hzn.translate.refused",
        DiagnosticSeverity.Error,
        "Design '{design}' cannot be opened in circuitRF: {count} unsupported item(s), so nothing was opened.\n{items}",
        ("design", design), ("count", items.Count), ("items", HznRefusalText.Render(items)));

    public static Diagnostic NoSuchDesign(string design, IReadOnlyList<string> known) => Diagnostic.Create(
        "hzn.translate.no-design",
        DiagnosticSeverity.Error,
        "The project has no design '{design}'. Its designs: {known}.",
        ("design", design), ("known", known.Count == 0 ? "(none)" : string.Join(", ", known)));

    public static Diagnostic NotThreeD(string design) => Diagnostic.Create(
        "hzn.translate.not-3d",
        DiagnosticSeverity.Error,
        "Design '{design}' is a circuit, not a 3D design; circuitRF opens a Hertzon project's 3D designs.",
        ("design", design));

    public static Diagnostic Note(string text) => Diagnostic.Create(
        "hzn.translate.note", DiagnosticSeverity.Info, "{text}", ("text", text));
}
