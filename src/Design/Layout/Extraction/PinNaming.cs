// How a placed pin's NET is allowed to be decided — brief-lvs-2-shared-extraction.md R-lvs2-2.
//
// ── THE TRAP THIS ENUM EXISTS TO CLOSE (LVS overview §1a) ──────────────────────────────────────
//
// PdnLayoutNets' governing rule is "net(pad) = the schematic's own binding, else the net stated on
// the copper". It is exactly right for railRF, which wants the best available answer about a board.
//
// It is CATASTROPHIC for LVS, and silently so: LVS asks the artwork what the artwork says, gets the
// SCHEMATIC's answer back, and every net on every design matches. No exception, no warning, no
// finding — a tool that passes everything, which is worse than a tool that fails everything because
// nothing about the output looks wrong.
//
// So the mode is an ENUM and it is REQUIRED. "Pass null for the schematic delegates" would have
// worked too, and null-means-artwork-only is already a supported shape here (R-ab1-4d) — which is
// precisely how a later caller half-supplies the schematic by accident and re-enters the trap
// without ever deciding to. Making the caller WRITE THE WORD is the whole mechanism.

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>Which claims may name the net of a <see cref="PlacedPin"/> — R-lvs2-2a.</summary>
public enum PinNaming
{
    /// <summary>
    /// Ask the schematic first, then the artwork. railRF's rule (the owner's, 2026-09-20),
    /// unchanged and now spelled out at every call site that takes it.
    /// </summary>
    SchematicThenArtwork,

    /// <summary>
    /// The artwork only. <b>Nothing a schematic says may reach the answer</b> — supplying a
    /// schematic-facing delegate in this mode is an <see cref="ArgumentException"/> (R-lvs2-2c)
    /// rather than a parameter that is quietly ignored, because a parameter that is silently
    /// ignored in one mode is a parameter somebody will supply and believe in.
    /// </summary>
    ArtworkOnly,
}
