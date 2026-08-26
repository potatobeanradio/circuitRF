using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// Builds the <see cref="SurfaceMesher.PlanarLengthFormat"/> an EM run is asked to use, from the
/// layout's own display unit — owner request, 2026-08-15: "all messages from running an EM sim that
/// reference distance/length need to respect the units of the .clay file."
///
/// <para>The kernel cannot build this itself: <c>src/Engine</c> may not reference <c>LayoutUnits</c>
/// (the UI firewall — see the root <c>CLAUDE.md</c>), so the conversion is built here, on the
/// <c>src/Ui</c> side of it, and handed down as a plain delegate over a double. <c>LayoutUnits</c>
/// itself already IS framework-free (no Avalonia, no SkiaSharp) — this file just supplies the metres
/// half of the round trip <see cref="LayoutUnits.Format"/> does not, since every other caller in this
/// codebase already works in DBU, never metres.</para>
/// </summary>
public static class EmLengthFormat
{
    /// <summary>Metres to nanometres to DBU, rounded the same way <see cref="LayoutUnits.ToDbu"/>
    /// rounds — away from zero — so a length that came from a drawn DBU coordinate round-trips.</summary>
    public static SurfaceMesher.PlanarLengthFormat For(LayoutUnit displayUnit, int dbuPerMicron) =>
        metres =>
        {
            long dbu = (long)Math.Round(metres * 1e9 * dbuPerMicron / 1000.0,
                                        MidpointRounding.AwayFromZero);
            return $"{LayoutUnits.Format(dbu, displayUnit, dbuPerMicron)} {LayoutUnits.Suffix(displayUnit)}";
        };
}
