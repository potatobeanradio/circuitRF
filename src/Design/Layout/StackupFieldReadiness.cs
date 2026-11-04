// Which field on a stackup row an EM run cannot use, and why — one answer per FIELD.
//
// WHY A SECOND SHAPE OF THE SAME QUESTION. TechValidation already reports every one of these, and its
// output is a list of SENTENCES attributed to a TAB. That is the right shape for the problems panel and
// the wrong shape for the editor's own text boxes: a sentence naming a layer by name cannot tell a
// TextBox whether it is the one at fault, and a rule re-implemented beside the box would drift from the
// one the extractors actually refuse on — silently, and in the direction that matters (a field marked
// fine that stops a run).
//
// So the PREDICATES live here, once, and both readers use them: TechValidation phrases them as
// sentences, and the Technology editor marks the field. The thresholds are the extractors' own —
// Epsr >= 1, positive thickness, positive conductivity — not a second opinion about what is plausible.
//
// WHAT THIS IS NOT. It is not a judgement about whether a value is RIGHT. 4.4 on a board that is
// actually 3.66 passes every check here, because nothing in circuitRF knows which board it is looking
// at; that is what the import's own "these are guesses" message is for (SubstrateDefaults). This
// answers only "would an EM run refuse this outright", which is the question a highlight can honestly
// answer.

namespace CircuitRF.Design.Layout;

/// <summary>
/// Which field of a <see cref="StackupLayer"/> something is about.
///
/// <para>The six NUMERIC members are what <see cref="StackupFieldReadiness.Problem"/> answers for, and
/// they are the whole of what this file needs. <see cref="None"/>, <see cref="Name"/> and
/// <see cref="Span"/> were added for the stackup cross-section
/// (docs/sonnet-briefs/brief-stackup-render-1-scene.md R-stk1-10), which has to say which value a
/// piece of text would edit and has two answers this enum did not carry: a layer's name, and the
/// pair of conductor names a via spans. <b>They were added HERE rather than as a second
/// <c>StackupField</c> in <c>src/Render</c></b> — <c>src/Ui</c> consumes both namespaces wholesale
/// through its global usings, so a second enum of this name is an ambiguous reference in every file
/// that uses either.</para>
///
/// <para><see cref="Problem"/> answers null for all three, through its own <c>_</c> arm, and that is
/// correct rather than an omission: a name is checked by <c>NameValidator</c> and a span by
/// <c>TechValidation</c>'s own resolution pass, and neither is a question an EM run's numeric
/// readiness can answer.</para>
/// </summary>
public enum StackupField { None, Name, Thickness, Sigma, Epsr, TanD, Mur, WallThickness, Span, FilmSheetResistance, FilmThickness }

/// <summary>
/// Per-field readiness for the EM path. Every predicate mirrors a check <see cref="TechValidation"/>
/// already makes, and both must move together.
/// </summary>
public static class StackupFieldReadiness
{
    /// <summary>
    /// Why an EM run could not use <paramref name="field"/> on <paramref name="layer"/>, or null when
    /// it can.
    ///
    /// <para>The sentence is written to sit in a tooltip beside the field, so it says what is wrong and
    /// what would fix it and names no layer — the box the reader is hovering over IS the layer.</para>
    ///
    /// <para>A field that this <see cref="StackupKind"/> does not use answers null: the editor hides it
    /// anyway, and a hidden field reporting a problem would put a count on a tab header nobody can
    /// clear.</para>
    /// </summary>
    public static string? Problem(StackupLayer layer, StackupField field) => field switch
    {
        // A via has no z band of its own — it traverses the dielectrics between the conductors it
        // spans — which is why PlanarExtractor.BuildStack skips it and TechValidation exempts it.
        StackupField.Thickness =>
            layer.Kind != StackupKind.Via && layer.ThicknessDbu <= 0
                ? "An EM run needs a positive thickness here. Enter the layer's thickness, e.g. 35u for 1 oz copper or 1.6mm for a board core."
                : null,

        StackupField.Sigma =>
            layer.Kind == StackupKind.Conductor && layer.SigmaSm <= 0
                ? "An EM run needs a positive conductivity here — it is what sets conductor loss and skin depth. Pick a metal from the list beside this box, or type your process's own figure."
                : null,

        // Below 1 is not a material. Zero in particular is how an import used to spell "nobody said";
        // it is now filled in on import, so a zero reaching this is one somebody typed or cleared.
        StackupField.Epsr =>
            layer.Kind == StackupKind.Dielectric && layer.Epsr < 1
                ? "An EM run needs a relative permittivity of at least 1 — 1 is vacuum and no material is below it. Ordinary FR-4 is about 4.4; your fabricator's stackup states the real figure."
                : null,

        StackupField.TanD =>
            layer.Kind == StackupKind.Dielectric && layer.TanD < 0
                ? "A loss tangent cannot be negative. Use 0 for a lossless dielectric; ordinary FR-4 is about 0.02."
                : null,

        StackupField.Mur =>
            layer.Kind == StackupKind.Dielectric && layer.Mur <= 0
                ? "An EM run needs a positive relative permeability. Use 1 for any non-magnetic dielectric, which is nearly all of them."
                : null,

        // Only a PLATED via has a wall. GI1: an unplated hole connects nothing and is not asked.
        StackupField.WallThickness =>
            layer.Kind == StackupKind.Via && layer.Plated != false && layer.Fill == ViaFillKind.Plated
                && layer.WallThicknessDbu is not > 0
                ? "A plated via needs a wall thickness — the metal deposited on the barrel wall, not the hole radius. 20-25 um is typical for a plated through-hole board."
                : null,

        _ => null,
    };
}
