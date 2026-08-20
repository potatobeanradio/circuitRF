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
    /// The design a freshly-placed <c>Match</c> carries: 1-2 GHz, order 3, both ends 50 ohms and
    /// purely resistive, Chebyshev-Fano.
    /// </summary>
    /// <remarks>
    /// <b>It has to synthesise cleanly, because nothing else will make it.</b> Until the Designer
    /// (MN-3) exists a placed <c>Match</c> is edited only by hand-writing this parameter, so a
    /// component that arrives refusing would be a component nobody could repair from the schematic.
    /// Both ends resistive also makes it the ONE default whose meaning does not depend on what it is
    /// wired to: it absorbs nothing, so it stamps its whole ladder wherever it is dropped. Same rule
    /// <c>WBondEmbedding.DefaultDesign</c> follows in shipping one real wire rather than an empty
    /// array.
    /// </remarks>
    public static MatchDesign DefaultDesign() => new()
    {
        F1 = 1e9,
        F2 = 2e9,
        Order = 3,
        Response = ResponseShape.ChebyshevFano,
        Term1 = Termination.Resistive(50.0),
        Term2 = Termination.Resistive(50.0),
    };

    /// <summary>
    /// The encoded default, computed once — every freshly-placed <c>Match</c> carries this exact
    /// string, so two of them decode to the same design and rebuild identically.
    /// </summary>
    public static string DefaultPayload => _defaultPayload ??= Encode(DefaultDesign());

    private static string? _defaultPayload;

    private static string RePad(string text) =>
        (text.Length % 4) switch { 2 => text + "==", 3 => text + "=", _ => text };
}
