// Cross-section extraction inputs that are NOT the geometry or the technology
// (docs/sonnet-briefs/brief-L6-L7-em-ui.md R-em-1/R-em-4b/R-em-11).
//
// Framework-free by rule: nothing under src/Ui/Layout/Em/ may reference Avalonia or SkiaSharp.
// R-em-1 exists so every extraction test can run without constructing a document, a canvas or a
// workspace — the same structural decision that made the engine half tractable.

using System.Numerics;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// The settings the extractor itself consumes. A strict subset of what a <c>.cem</c> carries — mesh
/// settings, the frequency sweep, the dispersion opt-in and the <c>.snp</c> path are all consumed
/// downstream by the kernel or the run service, never here.
/// </summary>
/// <param name="SignalStackupLayerName">
/// R-em-4b: which <see cref="StackupKind.Conductor"/> stackup entry is the signal. Null means
/// "infer", which is unambiguous exactly when the drawn shapes land on one signal conductor layer.
/// <c>MmicGaAs</c> is the case this exists for — a genuinely three-conductor stack whose own comment
/// says a zero-config line defaults to Metal2↔Backside Metal and that an MLIN meant for Metal1 needs
/// the explicit override.
/// </param>
/// <param name="Port1Z0">Reference impedance of port 1. Complex is permitted — <c>RFNetwork.ZToS</c>
/// already handles a complex per-port reference.</param>
/// <param name="Port2Z0">Reference impedance of port 2.</param>
/// <param name="SubjectDescription">
/// What this setup is pointed at, in the user's own terms (<c>"Amp/layout/Amp.clay"</c>) — used only
/// in the R-em-6 "no shapes on any bound conductor layer" refusal, which must say <i>what the setup
/// is pointed at</i> as well as what it found. Null falls back to "this layout".
/// </param>
/// <param name="AnalysisLevelNames">
/// <b>L9d/D5 — which conductor stackup entries are IN the analysis, bottom-to-top.</b>
///
/// <para>Null or empty means "infer", which is every signal conductor entry that actually carries
/// artwork. Naming them is what lets a user analyse two of a four-metal stack, and it is the same
/// shape as <paramref name="SignalStackupLayerName"/> one dimension over — that field survives as the
/// single-level spelling of the same thing, and every <c>.cem</c> written before L9d keeps meaning
/// exactly what it meant.</para>
/// </param>
/// <param name="PortZ0s">
/// R-cpl-6: explicit per-port reference impedances in D3 order (port 2k−1 is conductor k's near end,
/// 2k its far end), overriding the near/far defaults. Null — the normal case — means every odd port
/// takes <paramref name="Port1Z0"/> and every even port <paramref name="Port2Z0"/>, which is exactly
/// what a 2-port meant before L7b and generalises to any conductor count without changing it.
/// </param>
public sealed record EmExtractionSettings(
    string?    SignalStackupLayerName = null,
    Complex?   Port1Z0 = null,
    Complex?   Port2Z0 = null,
    string?    SubjectDescription = null,
    Complex[]? PortZ0s = null,
    string[]?  AnalysisLevelNames = null)
{
    public static readonly EmExtractionSettings Default = new();

    public Complex ResolvedPort1Z0 => Port1Z0 ?? new Complex(50, 0);
    public Complex ResolvedPort2Z0 => Port2Z0 ?? new Complex(50, 0);

    /// <summary>The reference impedance of port <paramref name="index"/> (0-based, D3 order).</summary>
    public Complex ResolvePortZ0(int index)
        => PortZ0s is { } list && index >= 0 && index < list.Length
            ? list[index]
            : (index % 2 == 0 ? ResolvedPort1Z0 : ResolvedPort2Z0);
}
