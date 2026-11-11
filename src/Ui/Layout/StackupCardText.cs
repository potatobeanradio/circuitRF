namespace CircuitRF.Ui.Layout;

/// <summary>
/// The words the Stackup tab uses for the handful of CLOSED-CHOICE fields that appear in two places
/// at once — on an entry's card, and (brief 6, R-stk6-8) on the cross-section's context menu.
///
/// <h3>Why these strings are here rather than typed into the .axaml</h3>
/// <para>R-stk6-8 states two constraints on the menu: <b>the labels are the card's labels, verbatim</b>,
/// and <b>the tooltips come with them</b>. "Metal thickness goes to:" was deliberately reworded away
/// from an earlier spelling that could not be answered without knowing what a conductor's z <i>band</i>
/// is — a word the control never defined — and re-inventing it in a second place is exactly how the
/// two come to ask different questions. A constant referenced from both is the only version of that
/// rule the next change cannot miss.</para>
///
/// <para>The .axaml reads these through <c>{x:Static}</c>, the same way it already reads
/// <see cref="TechEditorMetrics"/>'s pinned sizes — so this is not a second copy of the card's text,
/// it IS the card's text. It also collapses a duplication the card already carried: each of these
/// tooltips was written out twice there, once on the label and once on the control beside it, with a
/// comment asking that the two not drift.</para>
///
/// <h3>What is NOT here</h3>
/// <para>The drawing-layer rows' label, which is <c>StackupLayerRowViewModel.DrawingLayersLabel</c> —
/// it is already one string chosen by the row's kind ("Drawing layers:" for a conductor, "Drawing
/// layer:" for a via), and the menu reads the row's own property rather than restating it.</para>
/// </summary>
public static class StackupCardText
{
    // ── Conductor: the ground-reference flag ──────────────────────────────────────────────────────

    public const string GroundReference = "Ground reference";

    public const string GroundReferenceTip =
        "Marks this conductor as a ground-reference plane for microstrip substrate resolution.";

    // ── Conductor: which neighbour absorbs the metal's thickness (MIM-6) ──────────────────────────

    public const string SheetAt = "Metal thickness goes to:";

    public const string SheetAtTip =
        "The full-wave planar solver has no thickness for metal — a conductor is a sheet at one " +
        "height — so this conductor's thickness has to be given to the dielectric on one side of " +
        "it. The layer above (the default, and what every technology written before this field " +
        "means) leaves the substrate under the line exactly as you entered it, which is what the " +
        "closed-form microstrip models agree with. The substrate below grows the substrate by this " +
        "metal, which is what a thin-film capacitor's LOWER plate needs so the modelled plate gap " +
        "is the capacitor dielectric alone. The stack height never changes either way.";

    // ── Dielectric: the conductor a patterned thin film is deposited under (MIM-7) ────────────────

    public const string PresentWith = "Patterned with:";

    public const string PresentWithTip =
        "For a patterned thin film such as a capacitor dielectric: what says where it is. Name the " +
        "CONDUCTOR it is deposited under — the one directly ABOVE this entry — and an EM run " +
        "carries the film only when that conductor is one of its analysis levels. Or name the " +
        "DRAWING LAYER that masks it, and a run carries the film only when the layout draws on " +
        "that layer, which is what a process streaming its own nitride mask actually says. Either " +
        "way, a run that does not carry it puts air at the same thickness in its place and says so " +
        "in its notes. Leave as (none) for an ordinary, laterally continuous dielectric.";

    // ── Dielectric: opened where a mask is drawn (PSV-2) ───────────────────────────────────────

    public const string OpenWith = "Opened by:";

    public const string OpenWithTip =
        "For a passivation or other cover with openings in it, such as over the bond pads: the DRAWING " +
        "LAYER the openings are drawn on. The dielectric exists everywhere except where that layer is " +
        "drawn. A 3D run cuts each opening out of the slab with straight walls; a planar run keeps the " +
        "slab everywhere and says in its notes how much of the layout is open. Choosing a layer here " +
        "clears Patterned with, and the reverse. Leave as (none) for a slab with no openings.";

    // ── Dielectric: a conformal cover starts on top of the metal (PSV-1b) ─────────────────────────

    public const string Conformal = "Conformal";

    public const string ConformalTip =
        "For a coat deposited conformally over the metal below it, such as passivation: it starts on " +
        "top of the metal and its own thickness is all it is, so the space beside the metal, in the " +
        "metal's own layer, is air. Unticked, that space is filled with this dielectric, which is " +
        "right for a solder mask that flows into the gaps between traces. Every EM solver and the " +
        "drawing follow this setting.";

    public const string ConformalNoConductor =
        "The entry directly below this one is not a conductor, so there is no metal for it to start on " +
        "top of.";

    /// <summary>{0} is the conductor's name.</summary>
    public const string ConformalSheetAtTop =
        "'{0}' below gives its metal thickness to the layer below it, so its band is filled from below " +
        "and this setting would change nothing.";

    // ── Via: is the hole metal at all (GI1 R-gi1-2) ───────────────────────────────────────────────

    /// <summary>The card's own spelling, on its "Fill:" row where the word "Plated" sits beside the
    /// fill combo and cannot be read as anything else. The MENU spells it
    /// <see cref="PlatedHoleMenu"/>, because there the two meanings of the word are two adjacent
    /// items — see R-stk6-5.</summary>
    public const string Plated = "Plated";

    /// <summary>R-stk6-5's spelling of the same flag. <c>StackupLayer.Plated</c> and
    /// <c>ViaFillKind.Plated</c> are different things with the same word, and on the menu they are
    /// one item apart; "Plated hole" is the one of the two that names a HOLE.</summary>
    public const string PlatedHoleMenu = "Plated hole";

    public const string PlatedTip =
        "Whether these holes are metal at all. Unticked means a non-plated hole — a mounting hole, " +
        "a routed cutout, a castellation that was not plated — which is drawn exactly as it is but " +
        "is NOT extracted as a vertical conductor by an EM run; a 3D run drills it as a hole of air. " +
        "Plated is the default and is what every technology means when it says nothing.";

    // ── Via: the fill MODEL, whose two values are both conductive (R-stk6-5) ──────────────────────

    public const string Fill = "Fill";

    public const string FillPlated = "Plated (hollow barrel)";
    public const string FillSolid  = "Solid (filled)";

    public const string FillTip =
        "Which fill model to solve this via with. BOTH values are metal — this is not the question " +
        "of whether the hole is plated, which is the item above. Plated is a hollow barrel with a " +
        "wall of the stated thickness; Solid is filled edge to edge.";

    // ── The tab's two pane expanders (owner, 2026-09-13) ──────────────────────────────────────────

    /// <summary>
    /// What the cross-section's expander promises. It says the three things the glyph cannot: WHICH
    /// pane it acts on — the two toggles sit side by side and their chevrons are identical — that the
    /// space goes to the other pane rather than nowhere, and that the choice is SAVED, because a
    /// control that quietly writes to the document is one a user should be told about before they
    /// press it rather than after the title bar acquires a dot.
    /// </summary>
    public const string DrawingPaneExpanderTip =
        "Toggle cross-section view.";

    /// <inheritdoc cref="DrawingPaneExpanderTip"/>
    public const string CardPaneExpanderTip =
        "Toggle card view.";

    /// <summary>The splitter between the cross-section and the cards.</summary>
    public const string StackupSplitterTip =
        "Drag to resize. Double-click to fit the cross-section.";

    // ── Copy the drawing (R-stk7-4) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What the menu's Copy item promises, and it says the two things a reader cannot otherwise tell:
    /// that the WHOLE stackup goes rather than the part that happens to be scrolled into view, and
    /// that what lands is a picture rather than the technology as data (which is what File ▸ Export
    /// and the .ctech merge are for).
    /// </summary>
    public const string CopyPictureTip =
        "Copies the whole cross-section to the clipboard as a picture — every layer, not just the " +
        "part scrolled into view — ready to paste into a document, a slide or a message. " +
        "Ctrl/Cmd+C does the same. To copy the stackup as DATA, export or merge the technology.";
}
