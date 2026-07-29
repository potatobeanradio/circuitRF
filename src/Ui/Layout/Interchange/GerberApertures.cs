// Aperture table (docs/sonnet-briefs/brief-L4c-gerber-export.md §3 "Aperture table hygiene"): dedupe
// by (shape, size) so a file doesn't define one aperture per object — a naive writer doing that produces
// files some CAM tools reject outright. Only circular apertures are needed by this brief's scope (a
// Circle flash, a Via pad flash, a round-cap Path stroke — the only three rows in §3's mapping table
// that need an aperture at all); D-codes are file-scoped per the RS-274X spec, so one table per file.

namespace CircuitRF.Ui.Layout.Interchange;

internal sealed class GerberApertureTable
{
    private const int FirstDCode = 10; // D00-D09 are reserved by the spec (D01-D03 are the draw/move/flash op codes)

    private readonly Dictionary<long, int> _codeByDiameterDbu = new();
    private readonly List<(int Code, long DiameterDbu)> _ordered = [];
    private int _next = FirstDCode;

    /// <summary>Returns the D-code for a circular aperture of <paramref name="diameterDbu"/>, minting a
    /// new one (in first-use order) the first time a given diameter is seen. A non-positive diameter is
    /// clamped to 1 DBU — Gerber has no zero-size aperture.</summary>
    internal int CircleAperture(long diameterDbu)
    {
        diameterDbu = Math.Max(diameterDbu, 1);
        if (_codeByDiameterDbu.TryGetValue(diameterDbu, out int code)) return code;
        code = _next++;
        _codeByDiameterDbu[diameterDbu] = code;
        _ordered.Add((code, diameterDbu));
        return code;
    }

    /// <summary>Every aperture minted so far, in the order it was first needed — the order the
    /// <c>%ADD..C,..*%</c> defines are written in.</summary>
    internal IReadOnlyList<(int Code, long DiameterDbu)> Ordered => _ordered;
}
