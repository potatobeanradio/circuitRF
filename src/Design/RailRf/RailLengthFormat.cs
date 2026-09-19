// How railRF spells a length or a coordinate that came off the artwork.
//
// ── DBU IS A STORAGE UNIT, NOT A READING UNIT ───────────────────────────────────────────────────
//
// Reported 2026-09-18: source and load positions, the mesh cell setting and every other coordinate
// readout and result were in DBU, and all of them should read in the board file's own units.
//
// Every coordinate railRF holds is an integer database unit on the artwork's own grid, which is what
// makes it exact and what makes two ports at the same place provably the same port. It is not what a
// board designer reads: `(26500000, 9875000) DBU` is the same place as `(26.5, 9.875) mm` and only
// one of them can be checked against a board. The number is the storage; this is the reading.
//
// ── ONE FORMAT OBJECT, CARRIED ──────────────────────────────────────────────────────────────────
//
// The two halves of the answer — which unit, and how many DBU are in a micron — belong to the LAYOUT
// (`LayoutView.DisplayUnit`, `LayoutView.DbuPerMicron`), not to railRF and not to a preference. So
// they travel together, from the board inputs to whatever prints. A caller with no board has no unit
// to print in and gets `Dbu`, which says so on the face of the string rather than looking like a
// length in whatever unit the reader assumes.

using CircuitRF.Design.Layout;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// The artwork's own display unit and resolution — everything needed to turn a DBU into a length a
/// board designer reads.
/// </summary>
/// <param name="Unit">The layout's <see cref="LayoutView.DisplayUnit"/>.</param>
/// <param name="DbuPerMicron">The layout's own resolution.</param>
public readonly record struct RailLengthFormat(LayoutUnit Unit, int DbuPerMicron)
{
    /// <summary>
    /// What a caller with no artwork prints in: raw DBU, said out loud.
    /// </summary>
    /// <remarks>
    /// <b>Not a default of micrometres.</b> A number printed in a unit nobody stated is a number that
    /// reads as correct and is wrong by three orders of magnitude, which is the trap every unit note
    /// in this repository is about. "DBU" in the string is the honest answer to "in what?" when
    /// nothing has said.
    /// </remarks>
    public static readonly RailLengthFormat Dbu = new((LayoutUnit)(-1), LayoutUnits.DefaultDbuPerMicron);

    /// <summary>True when this is the no-artwork fallback and lengths print as bare DBU.</summary>
    public bool IsRawDbu => !System.Enum.IsDefined(Unit);

    /// <summary>The artwork's format, from the layout itself.</summary>
    public static RailLengthFormat For(LayoutView view) =>
        new(view.DisplayUnit, view.DbuPerMicron);

    /// <summary>One length, with its unit — <c>0.209 mm</c>.</summary>
    public string Length(long dbu) =>
        IsRawDbu
            ? $"{dbu} DBU"
            : $"{LayoutUnits.Format(dbu, Unit, DbuPerMicron)} {LayoutUnits.Suffix(Unit)}";

    /// <summary>One point, with its unit once — <c>(26.5, 9.875) mm</c>.</summary>
    public string Point(long x, long y) =>
        IsRawDbu
            ? $"({x}, {y}) DBU"
            : $"({LayoutUnits.Format(x, Unit, DbuPerMicron)}, "
            + $"{LayoutUnits.Format(y, Unit, DbuPerMicron)}) {LayoutUnits.Suffix(Unit)}";

    /// <summary>A length given in METRES — what the extractor and the mesh settings deal in.</summary>
    /// <remarks>
    /// Rounded to DBU the way <see cref="LayoutUnits.ToDbu"/> rounds, away from zero, so a length that
    /// came from a drawn coordinate round-trips to the number that was drawn. The same arithmetic
    /// <c>EmLengthFormat</c> does, for the same reason.
    /// </remarks>
    public string Metres(double metres) =>
        Length((long)System.Math.Round(metres * 1e9 * DbuPerMicron / 1000.0,
                                       System.MidpointRounding.AwayFromZero));

    /// <summary>Metres from a number typed in this format's own unit, or null where it does not read.</summary>
    public double? ParseMetres(string? text)
    {
        if (text is not { Length: > 0 }) return null;
        return LayoutUnits.TryParse(text, Unit, DbuPerMicron, out long dbu)
            ? dbu / (DbuPerMicron * 1e6)
            : null;
    }
}
