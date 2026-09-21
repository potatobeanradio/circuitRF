using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// Which control on the railRF window answers a refusal — and therefore which one turns red.
/// </summary>
/// <remarks>
/// <b>§11.1's shape, and the second half of it is the part that is easy to drop.</b> The status strip
/// says the sentence <i>with the numbers in it</i>; this says WHERE. A refusal that names no control
/// is a sentence a user has to go and look for the answer to, and the six railRF actually has all
/// have one — which is why <see cref="RailRefusalControl.None"/> exists for the seventh kind (a
/// refusal from somewhere nobody expected) rather than as the ordinary case.
/// </remarks>
public enum RailRefusalControl
{
    /// <summary>Nothing on the window answers it. The status strip still says it.</summary>
    None,

    /// <summary>The import dialog's placement-origin choice (brief 2, Q-14).</summary>
    PlacementOrigin,

    /// <summary>The import's drill coordinate format — <c>convert</c>'s own refusal.</summary>
    DrillFormat,

    /// <summary>The rail's reference-layer combo (§2.2, Q-8).</summary>
    ReferenceLayer,

    /// <summary>The rail selector — a cycle in the rail order names two rails and a refdes.</summary>
    RailSelector,

    /// <summary>The <c>Accuracy</c> button — Fast above its shunt-band threshold (§2.9 rule 3).</summary>
    ModelKind,

    /// <summary>The stackup, reached from <c>Settings</c> — an unresolved via span.</summary>
    Stackup,
}

/// <summary>
/// One refusal on the railRF window: the sentence, and the control that answers it.
/// </summary>
/// <param name="Sentence">What the status strip says, <b>with its numbers in it</b>. Taken verbatim
/// from whoever raised it — briefs 2-6 write these, and re-wording one here would give the window and
/// the <c>rail</c> verb two different sentences for one condition.</param>
/// <param name="Control">Which control turns red.</param>
public sealed record RailRefusal(string Sentence, RailRefusalControl Control)
{
    /// <summary>The refusal a window with nothing wrong shows: none at all.</summary>
    public static readonly RailRefusal? Nothing = null;
}

/// <summary>
/// Attributing a refusal that came back from an engine to the control that answers it.
/// </summary>
/// <remarks>
/// <b>Four of the six never reach here</b>, and that is deliberate: the window checks the reference
/// layer, the rail order, the placement origin and the drill format ITSELF, before a run, so those
/// four are raised with their control already known. What is left is the two an extraction decides —
/// Fast's shunt-band ceiling and an unresolved via span — and those arrive as a sentence.
///
/// <para><b>So this matches on the STEM of a sentence the engine owns.</b> That is a real coupling and
/// it is stated rather than hidden: the alternative is a refusal <i>code</i> threaded through
/// <c>PdnExtraction</c>, which would be better and is brief 3-6's to add, not brief 7's. The gate is
/// <c>RailWindowTests</c>, which feeds each of the six sentences through and asserts the control —
/// so a re-worded engine sentence fails a test rather than quietly turning nothing red.</para>
/// </remarks>
public static class RailRefusals
{
    /// <summary>The stems, in the order they are tried. Each is a phrase the engine's own sentence
    /// opens with or contains, chosen to be the part of it that states the CONDITION rather than a
    /// number or a board's name.</summary>
    private static readonly (string Stem, RailRefusalControl Control)[] Stems =
    [
        ("states no reference layer",              RailRefusalControl.ReferenceLayer),
        // R-rail27-2. The reference-layer combo is what answers it — the other remedy the sentence
        // offers (pick the supply pour) is the pick button, and the combo is where a user who has
        // already made a rail can act without starting again.
        ("anchored on the copper of its own reference return", RailRefusalControl.ReferenceLayer),
        ("The fast model cannot answer above",     RailRefusalControl.ModelKind),
        ("could not be resolved to a layer span",  RailRefusalControl.Stackup),
        ("does not state its coordinate origin",   RailRefusalControl.PlacementOrigin),
        ("does not state its coordinate format",   RailRefusalControl.DrillFormat),
        ("cannot be put in a solve order",         RailRefusalControl.RailSelector),
        ("is both a load and a source on rail",    RailRefusalControl.RailSelector),
    ];

    /// <summary>
    /// The refusal <paramref name="sentence"/> is, with its control attributed — or
    /// <see cref="RailRefusalControl.None"/> where no stem matched, which still SAYS the sentence.
    /// </summary>
    public static RailRefusal Classify(string sentence)
    {
        foreach (var (stem, control) in Stems)
            if (sentence.Contains(stem, StringComparison.OrdinalIgnoreCase))
                return new RailRefusal(sentence, control);

        return new RailRefusal(sentence, RailRefusalControl.None);
    }

    /// <summary>Every control the classifier can name — what a test enumerates, so a stem added
    /// without a test is visible.</summary>
    public static IReadOnlyList<RailRefusalControl> Attributable =>
        [.. Stems.Select(s => s.Control).Distinct()];
}
