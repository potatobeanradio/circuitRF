using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Core.Matching;

/// <summary>
/// The design a Match component carries INSIDE the schematic — base64 of UTF-8 JSON, exactly the
/// <c>WBondEmbedding</c> pattern (match.md §7.2).
/// </summary>
/// <remarks>
/// <para><b>Base64, and why not raw JSON.</b> The payload has to survive both <c>.csch</c> (a JSON
/// string) and <c>.cnl</c> (a whitespace-delimited line format whose only string escape is a pair of
/// quotes, with no way to escape a quote inside one). A design's JSON is full of quotes, so it cannot
/// be a quoted <c>.cnl</c> token. Base64 is a single bare token needing no quoting rule on either
/// side.</para>
///
/// <para><b>The padding is stripped, and that is load-bearing.</b> <c>CnlReader</c>'s spaced-assignment
/// merge reads a token ENDING in <c>=</c> as <c>name=</c> with an empty value and glues the NEXT token
/// on as that value, so a padded payload followed by any other parameter on the same instance line
/// arrives as one run-on string and decodes to nothing. <see cref="TryDecode"/> re-pads before
/// decoding; do not "restore" the padding here.</para>
/// </remarks>
public static class MatchEmbedding
{
    /// <summary>The component parameter that carries the design.</summary>
    public const string DesignParameter = "Design";

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Serializes a design to the JSON the payload wraps. Readable, for a hand-authored file.</summary>
    public static string Write(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return JsonSerializer.Serialize(design, Options);
    }

    /// <summary>Parses the JSON form.</summary>
    public static MatchDesign Read(string json) =>
        JsonSerializer.Deserialize<MatchDesign>(json, Options)
        ?? throw new JsonException("The Match design payload decoded to null.");

    /// <summary>Encodes a design for storage on a component.</summary>
    public static string Encode(MatchDesign design) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Write(design))).TrimEnd('=');

    /// <summary>
    /// Decodes a stored payload. Accepts base64 (what <see cref="Encode"/> writes) or raw JSON (what a
    /// hand-authored <c>.cnl</c> may carry). <b>Returns false rather than throwing</b> — an unreadable
    /// payload is a reported, repairable state, not a crash on a render pass.
    /// </summary>
    public static bool TryDecode(string? payload, out MatchDesign? design)
    {
        design = null;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        string text = payload.Trim();
        try
        {
            // A design's JSON always starts with '{'; base64 never does.
            if (text[0] != '{')
                text = Encoding.UTF8.GetString(Convert.FromBase64String(RePad(text)));
            design = Read(text);
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or DecoderFallbackException)
        {
            design = null;
            return false;
        }
    }

    /// <summary>
    /// The design a freshly-placed <c>Match</c> carries: 1.8-2.2 GHz, order 4, 50 ohms down to
    /// 10 ohms, both ends purely resistive, Chebyshev-Fano.
    /// </summary>
    /// <remarks>
    /// <b>It has to synthesise cleanly, because nothing else will make it.</b> A placed <c>Match</c>
    /// can be edited by hand-writing this parameter, so a component that arrives refusing would be a
    /// component nobody could repair from the schematic. Both ends resistive also makes it the ONE
    /// default whose meaning does not depend on what it is wired to: it absorbs nothing, so it stamps
    /// its whole ladder wherever it is dropped. Same rule <c>WBondEmbedding.DefaultDesign</c> follows
    /// in shipping one real wire rather than an empty array.
    ///
    /// <para><b>It also has to survive being POKED</b> (owner, 2026-08-19: "the default settings need
    /// to show solutions and not have any immediate dead ends for the user to play with the UI"). The
    /// former default — 1-2 GHz, order 3, 50 Ω to 50 Ω — synthesised, but it is not a matching problem
    /// at all: ΠN² is 1/1, there is nothing for a transform to do, and the FIRST entry of its own
    /// order picker (order 2) returned no solutions. This one was chosen by measuring that: every
    /// valid order, every response family, and every single- and double-step change a user can make
    /// from the specification pane (either topology, either reactance kind, at either end) still
    /// returns at least one solution. 50 Ω into 10 Ω is also a real problem — a 5:1 transformation an
    /// output match actually has to do — so the transform rack and the solutions list have something
    /// to show rather than an identity.</para>
    /// </remarks>
    public static MatchDesign DefaultDesign()
    {
        var design = new MatchDesign
        {
            F1 = 1.8e9,
            F2 = 2.2e9,
            Order = 4,
            Response = ResponseShape.ChebyshevFano,
            Term1 = Termination.Resistive(50.0),
            Term2 = Termination.Resistive(10.0),
        };

        // ── and it arrives with a solution already APPLIED ────────────────────
        //
        // A real 5:1 transformation needs Norton transforms to REACH it: leave the list empty and the
        // status strip opens on "Π N² 1 / 0.271 ✘ not reached" with termination 2 flagged, which is
        // exactly the wall of warning text the owner asked to be rid of. The former 50 Ω → 50 Ω
        // default avoided that only by needing no transform at all — a matching component that does
        // not match anything.
        //
        // The search is deterministic and its ranking is defined (fewest transforms, then pair
        // position, then Q-adjust), so picking from its list is reproducible rather than arbitrary;
        // two freshly-placed Match components still decode to the same design. If it ever finds
        // nothing, the design is returned untransformed rather than throwing: an unapplied default is
        // a worse default, not a broken one.
        //
        // ── and the pick prefers a CAPACITIVE transform, for a reason worth stating ────────────
        //
        // A Norton transform replaces one element with a pi (or T) of three of its own kind. Do that
        // to an INDUCTOR pair and the result is three ideal inductors in a loop — which at DC is a
        // loop of ideal shorts, and a loop of ideal shorts is a SINGULAR MNA system. The default's
        // first-ranked solution (L1/L2) is exactly that, and it makes a freshly-placed Match run an
        // S-parameter sweep perfectly while refusing to DC-solve at all. Three capacitors carry a
        // series capacitor in the middle branch, which is a DC open, and the network stays solvable.
        //
        // This is a property of the DEFAULT, not a rule about transforms: an inductor Norton
        // transform is a legitimate thing for a user to apply, and the solutions list still offers
        // every one of them. What a shipped default may not be is a circuit that refuses one of the
        // analyses it is placed to run.
        var set = MatchSolutionSearch.Search(design, includeQAdjust: false);
        var plain = set.Solutions.Where(s => s.QAdjust == 0.0 && !s.ImplausibleValues).ToList();
        var pick = plain.FirstOrDefault(AllProductsAreCapacitors) ?? plain.FirstOrDefault();
        if (pick is not null)
        {
            design.Transforms = [.. pick.Transforms];
            design.AppliedSolutions.Add(pick.Fingerprint);
        }
        return design;
    }

    /// <summary>
    /// True when every element a solution's transforms PRODUCED is a capacitor.
    /// </summary>
    /// <remarks>
    /// The products of the transform at one-based ordinal <c>k</c> are named
    /// <c>{ElementA}_N{k}_1..3</c> (<c>NortonTransform.Apply</c>) — the same convention the Designer's
    /// bracket layout keys on — so they are found by that infix rather than by re-deriving the pair,
    /// which no longer exists once the transform has run.
    /// </remarks>
    private static bool AllProductsAreCapacitors(MatchSolution s)
    {
        var products = s.Network.Elements
            .Where(e => e.Name.Contains("_N", StringComparison.Ordinal))
            .ToList();
        return products.Count > 0 && products.All(e => e.Type == ElementType.C);
    }

    /// <summary>
    /// The encoded default, computed once — every freshly-placed <c>Match</c> carries this exact
    /// string, so two of them decode to the same design and rebuild identically.
    /// </summary>
    public static string DefaultPayload => _defaultPayload ??= Encode(DefaultDesign());

    private static string? _defaultPayload;

    private static string RePad(string text) =>
        (text.Length % 4) switch { 2 => text + "==", 3 => text + "=", _ => text };
}
