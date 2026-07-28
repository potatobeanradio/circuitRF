// $INSUNITS <-> DBU scale (docs/sonnet-briefs/brief-L4b-dxf-interchange.md R-L4b-4). DXF entity
// coordinates are plain doubles in "drawing units" whose physical meaning is declared once, in the
// HEADER section's $INSUNITS variable — never per-entity. An absent or 0 value must not be guessed
// silently: a drawing interpreted at 1000x the intended scale is the worst possible silent failure,
// and unlike a mis-mapped layer it is not visually obvious in a zoom-to-fit.

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>The $INSUNITS values this brief documents as supported (§2). Values outside this set are
/// still accepted numerically (DXF defines more), but are treated the same as "unset" for our purposes
/// — <see cref="DxfUnits.NanometersPerDrawingUnit"/> returns null and the caller must ask.</summary>
public static class DxfUnits
{
    public const int Inches = 1;
    public const int Feet = 2;
    public const int Millimeters = 4;
    public const int Centimeters = 5;
    public const int Meters = 6;
    public const int Microns = 13;

    /// <summary>Exact nanometers per one drawing unit at the given $INSUNITS value, or null when the
    /// value is 0/absent/unrecognized — the caller must not guess (R-L4b-4).</summary>
    public static long? NanometersPerDrawingUnit(int insunits) => insunits switch
    {
        Inches => 25_400_000,
        Feet => 304_800_000,
        Millimeters => 1_000_000,
        Centimeters => 10_000_000,
        Meters => 1_000_000_000,
        Microns => 1_000,
        _ => null,
    };

    /// <summary>Exact DBU per one drawing unit, given the resolved $INSUNITS and the destination's own
    /// DBU/micron resolution. Both factors are exact integers (nm-per-unit, DBU-per-micron), so this
    /// stays exact in <see cref="decimal"/> — never <see cref="double"/>, matching <c>LayoutUnits</c>'s
    /// own exactness discipline.</summary>
    public static decimal DbuPerDrawingUnit(int insunits, int dbuPerMicron)
    {
        long? nmPerUnit = NanometersPerDrawingUnit(insunits);
        if (nmPerUnit is null)
            throw new ArgumentException($"Unsupported/unset $INSUNITS value {insunits}.", nameof(insunits));
        return (decimal)nmPerUnit.Value * dbuPerMicron / 1000m;
    }

    /// <summary>The default this brief specifies for the unit prompt (R-L4b-4: "Default the prompt to
    /// mm").</summary>
    public const int DefaultPromptUnits = Millimeters;
}
