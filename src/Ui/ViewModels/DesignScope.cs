using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Builds a design-time <see cref="Scope"/> from the current schematic so a single parameter
/// expression can be evaluated for the inline "≈ value" preview (parameter-editor.md, "Value
/// preview"). This is a lightweight, read-only mirror of what the real Elaborator builds at run
/// time — it collects the named values an expression might reference and binds them into a scope:
///
///   • Every component parameter that has a NAME and an EXPRESSION is bound (name → expression),
///     so an expression referencing another parameter by name can resolve. Names are bound in
///     component order; a later duplicate name overwrites an earlier one (last-wins) — the same
///     flat-namespace behaviour the preview can offer before the design has a real scoped Var
///     layer (§7.2 Var tool, not yet built).
///
/// Units are deliberately NOT bound. The engine's <see cref="Units"/> table is keyed by ASCII
/// strings ("Ohm", "uH", "uF") while the editor's Unit ComboBox uses display glyphs ("Ω", "µH",
/// "µF"); passing a glyph unit into the engine would make ApplyUnit throw (unknown unit). The
/// preview shows the RAW numeric evaluation of the expression (display-unit scaling is a separate,
/// deferred concern — parameter-editor.md), so binding bare expressions (unit = null) is both
/// correct and throw-safe.
///
/// What this does NOT do (deliberately, for the preview):
///   • No Var components — the Var tool (§7.2) does not exist yet; when it lands, collect Vars
///     here too (name → expression) and they slot into the same flat scope with no other change.
///   • No analysis/measurement context — the preview is a pre-simulation scalar evaluation; the
///     MeasurementContext (post-run accessors like HB1.V(...)) is not involved.
///   • No SDD device equations — those are evaluated in the SDD's own context, never here; the
///     caller gates SDD components out before ever building a scope.
///
/// The scope is intentionally permissive: an expression that references a name not present simply
/// fails to resolve and the caller shows no preview (never an error). Building it is O(params).
/// </summary>
internal static class DesignScope
{
    /// <summary>
    /// Builds a flat scope of every named parameter expression in the model. The optional
    /// <paramref name="selfName"/> is excluded so a parameter does not bind to itself (which would
    /// otherwise look like a trivial cycle when an expression refers to its own name).
    /// </summary>
    public static Scope Build(SchematicEditModel model, string? selfName = null)
    {
        var scope = new Scope("design-preview");
        foreach (var comp in model.Components)
        {
            // SDD/FetSdd parameters are device-equation slots, not scalar bindings — skip them so
            // they can never shadow a real scalar name or feed a scalar resolve.
            if (comp.Symbol is SymbolKind.Sdd or SymbolKind.FetSdd) continue;

            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrEmpty(p.Name) || string.IsNullOrEmpty(p.Expression)) continue;
                if (selfName is not null && p.Name == selfName) continue;
                // Bind name → expression only (no unit — see class remarks). Last-wins on duplicate
                // names. The engine resolves the bound expression lazily when something references
                // this name; an unresolvable inner reference just propagates as a thrown exception
                // the caller catches (→ no preview).
                scope.Bind(p.Name, p.Expression, unit: null);
            }
        }
        return scope;
    }
}
