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
/// Two build modes differ only in whether variable UNITS are bound:
///   • <see cref="Build"/> (unit-stripped) binds bare expressions (unit = null) — the raw numeric
///     evaluation. Used by the parametric-sweep row, which applies its own unit scaling on top.
///   • <see cref="BuildResolved"/> (unit-aware) binds each variable's declared unit, converted from
///     editor glyphs ("Ω", "µH") to engine ASCII ("Ohm", "uH") via <see cref="UnitNormalizer"/>, so
///     a unit-bearing reference resolves to its true base-unit value. This is what lets the
///     analysis-editor preview be honest ("= 2e9" for RFfreq = 2 GHz). Unrecognised units bind null
///     (raw) to stay throw-safe — the original reason units were skipped (glyph → ApplyUnit throw)
///     is gone now that UnitNormalizer + the identity-unit-tolerant ApplyUnit exist.
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
        => BuildCore(model, selfName, resolveUnits: false);

    /// <summary>
    /// Like <see cref="Build"/>, but ALSO binds each variable's declared unit (glyph→engine via
    /// <see cref="UnitNormalizer"/>), so a reference to a unit-bearing variable resolves to its true
    /// base-unit value — e.g. <c>Cval = 1 pF</c> → <c>1e-12</c>, <c>RFfreq = 2 GHz</c> → <c>2e9</c>.
    /// The evaluator's var-unit-wins rule applies the unit exactly once.
    ///
    /// This is what the "= value" analysis-editor preview uses so it can be honest about exactness.
    /// The plain unit-stripped <see cref="Build"/> is retained for the parametric-sweep row, which
    /// applies its own unit scaling on top of a raw coefficient.
    ///
    /// Throw-safety: an unrecognised unit binds null (raw) rather than a unit that would make
    /// <c>ApplyUnit</c> throw — so the preview degrades to the old raw behaviour, never to a crash.
    /// </summary>
    public static Scope BuildResolved(SchematicEditModel model, string? selfName = null)
        => BuildCore(model, selfName, resolveUnits: true);

    private static Scope BuildCore(SchematicEditModel model, string? selfName, bool resolveUnits)
    {
        var scope = new Scope("design-preview");
        foreach (var comp in model.Components)
        {
            // SDD parameters are device-equation slots, not scalar bindings — skip them so
            // they can never shadow a real scalar name or feed a scalar resolve.
            if (comp.Symbol is SymbolKind.Sdd) continue;

            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrEmpty(p.Name) || string.IsNullOrEmpty(p.Expression)) continue;
                if (selfName is not null && p.Name == selfName) continue;

                // Bind name → expression. Last-wins on duplicate names. The engine resolves the bound
                // expression lazily when something references this name; an unresolvable inner
                // reference just propagates as a thrown exception the caller catches (→ no preview).
                // Unit is bound only in the resolved variant (and only when recognised) — see remarks.
                string? unit = null;
                if (resolveUnits)
                {
                    string eu = UnitNormalizer.ToEngineUnit(p.Unit);
                    if (!string.IsNullOrEmpty(eu) && Units.IsRecognizedUnit(eu)) unit = eu;
                }
                scope.Bind(p.Name, p.Expression, unit);
            }
        }
        return scope;
    }
}
