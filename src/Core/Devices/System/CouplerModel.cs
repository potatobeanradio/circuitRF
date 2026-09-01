using System.Numerics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal directional coupler — and, at 3.0103 dB, the ideal 90° hybrid. Four ports, eight nets
/// (<c>[in+, in−, thru+, thru−, cpl+, cpl−, iso+, iso−]</c>); the single-ended tile ties each port's
/// − net to ground at extraction.
///
/// <para><b>One component, three tiles.</b> <c>Coupler</c>, <c>Hybrid90</c> and <c>Hybrid180</c> are
/// all <c>EngineReference = "Coupler"</c> with different registry defaults — the
/// <c>Mixer</c>/<c>MixerD</c> and <c>Switch</c>/<c>SwitchD</c> arrangement, for the same reason:
/// nothing electrical distinguishes a hybrid from a 3 dB coupler. The hybrids keep their OWN
/// instance prefix (<c>HYB</c>) rather than sharing the coupler's <c>CPL</c>, which is a deliberate
/// deviation from the shared-prefix reasoning: a user does not swap a hybrid for a directional
/// coupler mid-design, and <c>HYB1</c> is the name they expect to see on the drawing.</para>
///
/// <para><b>Port order is 1 = IN, 2 = THRU, 3 = CPL, 4 = ISO</b>, and the pairing follows from it:
/// 1↔2 and 3↔4 are through paths, 1↔3 and 2↔4 are coupled paths, 1↔4 and 2↔3 are the isolated
/// ones.</para>
///
/// <para><b>Its S-matrix, in full.</b> Write <c>c = 10^(−Coupling/20)</c>,
/// <c>t = √(1 − c²)</c>, <c>ℓ = 10^(−IL/20)</c>, <c>d = 10^(−Directivity/20)</c> (0 exactly at
/// ≥ 150 dB), <c>ρ = 10^(−RL/20)</c> (0 exactly at ≥ 150 dB) and <c>φ = Phase</c>:</para>
/// <code>
///   S21 = S12 = S43 = S34 = t·ℓ                 the through paths
///   S31 = S13 = S42 = S24 = c·ℓ·e^(−jφ)         the coupled paths
///   S41 = S14 = S32 = S23 = c·d·ℓ               the isolated paths
///   S_ii = ρ
/// </code>
///
/// <para><b>The ideal split is set by <c>Coupling</c> ALONE, and it is lossless.</b> <c>t</c> is
/// whatever is left after the coupled port has taken its share, so a 20 dB coupler already loses
/// 0.0436 dB through its main arm and that comes out of the arithmetic rather than out of a
/// parameter. <c>IL</c> is a loss ADDED on top of the split — it scales all three transmission
/// paths, which is what keeps <c>Directivity</c> meaning what it says: the isolated port sits
/// <c>Directivity</c> dB below the coupled port whatever the insertion loss is. At 3.0103 dB,
/// <c>c = t = 1/√2</c> exactly to the precision the number carries, which is the hybrid.</para>
///
/// <para><b><c>Phase</c> reaches this model in RADIANS.</b> The Elaborator applies the parameter's
/// own angle unit before the factory ever sees it, so an authored <c>Phase=90 deg</c> arrives as
/// π/2 — the same convention <see cref="TLineModel"/>'s <c>E</c> carries and documents. A
/// hand-written netlist line that says a bare <c>Phase=90</c> is asking for 90 RADIANS, which is a
/// real number and is stamped as one; write the unit.</para>
///
/// <para><b><c>Phase = 90°</c> makes S complex</b> — the first block in this family that does — which
/// is what <see cref="IdealSBlockModel"/>'s <c>S(−ω) = conj(S(ω))</c> rule exists for. Say the
/// consequence rather than leaving it to be discovered: <b>a quadrature relationship held at EVERY
/// frequency is an idealisation with no causal realisation.</b> A frequency-flat −90° is a Hilbert
/// transform, not a network. circuitRF is a frequency-domain simulator, so it costs nothing here and
/// is exactly what a system block diagram wants; it would be meaningless in a transient one.</para>
///
/// <para><b>What this is NOT is a branch-line coupler.</b> A real quadrature hybrid holds its 90°
/// over a band and rolls off outside it. A user who wants that bandwidth should build one from four
/// quarter-wave <c>TLIN</c> arms — two at <c>Z₀/√2</c> and two at <c>Z₀</c> — which circuitRF has
/// modelled since long before this block existed. The same goes for a coupled-line coupler and for
/// the rat-race that makes a real 180° hybrid.</para>
///
/// <para><b>Only the quadrature case is unitary, and that is a theorem rather than an oversight.</b>
/// A lossless, matched, reciprocal four-port with directivity <i>must</i> have its coupled arm in
/// quadrature with its through arm; at <c>Phase</c> 0 or 180 the rows of the matrix above are each
/// still of unit norm but rows 1 and 4 are no longer orthogonal, so the block is energy-consistent
/// under any single-port excitation and not simultaneously realisable. It is stamped anyway. A user
/// is allowed to type numbers a physical part could not have — a coupling above 0 dB included, where
/// <c>t</c> is taken as the honest imaginary <c>j·√(c²−1)</c> rather than a NaN — and this model
/// refuses only what cannot be stamped.</para>
/// </summary>
public sealed class CouplerModel : IdealSBlockModel
{
    private readonly Complex[,] _sFlat;

    /// <param name="couplingDb">Coupled-port level below the input, dB. Sets the whole split.</param>
    /// <param name="phaseRad">
    /// Phase of the coupled port relative to the through port, in <b>RADIANS</b>. The Elaborator has
    /// already applied the parameter's angle unit (deg→rad via <c>Units.Scale</c>), so an authored
    /// <c>Phase=90 deg</c> arrives here as π/2 — the same convention <c>TLineModel</c>'s <c>E</c>
    /// carries, and for the same reason. Do NOT re-apply π/180.
    /// </param>
    /// <param name="directivityDb">Isolated-port level below the coupled port; ≥ 150 means none.</param>
    /// <param name="ilDb">Loss ADDED to the ideal split, dB. A loss, so it is never snapped.</param>
    /// <param name="returnLossDb">Return loss at each port; ≥ 150 means exactly matched.</param>
    /// <param name="z0">Reference impedance of all four ports, ohms; may be complex.</param>
    /// <param name="pimDbm">
    /// Third-order passive-intermod product level at the THRU port, dBm, with two carriers of
    /// <paramref name="pimPcDbm"/> each entering the IN port. At or below −150 dBm the block is
    /// exactly linear and no overlay is built at all. See <see cref="PimOverlay"/> for what the
    /// quadrature case costs and what it does not.
    /// </param>
    /// <param name="pimPcDbm">Power per carrier the level above was stated at, dBm.</param>
    public CouplerModel(double couplingDb, double phaseRad, double directivityDb,
                        double ilDb, double returnLossDb, Complex z0,
                        double pimDbm = -200.0, double pimPcDbm = 43.0)
        : base([z0, z0, z0, z0])
    {
        double  c    = AmplitudeFromDb(couplingDb);
        double  loss = AmplitudeFromDb(ilDb);
        double  dir  = SuppressedAmplitude(directivityDb);
        double  refl = SuppressedAmplitude(returnLossDb);

        // Complex.Sqrt rather than Math.Sqrt so a coupling ABOVE 0 dB — which a user is allowed to
        // type — becomes j·√(c²−1) instead of a NaN that would surface as a non-convergence with
        // nothing attached to it.
        Complex thru = Complex.Sqrt(new Complex(1.0 - c * c, 0)) * loss;
        Complex cpl  = Complex.FromPolarCoordinates(c * loss, -phaseRad);
        Complex iso  = new Complex(c * dir * loss, 0);

        _sFlat = new Complex[4, 4];
        for (int p = 0; p < 4; p++) _sFlat[p, p] = new Complex(refl, 0);

        Pair(0, 1, thru); Pair(2, 3, thru);      // through
        Pair(0, 2, cpl);  Pair(1, 3, cpl);       // coupled
        Pair(0, 3, iso);  Pair(1, 2, iso);       // isolated

        void Pair(int p, int q, Complex v) => _sFlat[p, q] = _sFlat[q, p] = v;

        // Carriers into IN, product read at THRU — the main line, which is where a coupler's own
        // through-path intermod is measured. The coupled and isolated ports still carry their share:
        // the product is routed by the block's own S, so the isolated port measures exactly the
        // nothing its directivity says it should.
        EnablePim(pimDbm, pimPcDbm, inPort: 0, outPort: 1);
    }

    /// <summary>
    /// The four ports' own words, so a branch-current cube key reads <c>CPL1:cpl</c> rather than
    /// <c>CPL1:3</c> — which is the difference between a plot legend a reader can use and one they
    /// have to look up.
    /// </summary>
    public override string[] TerminalNames => ["in", "thru", "cpl", "iso"];

    protected override void FillS(double omega, Complex[,] s)
        => Array.Copy(_sFlat, s, _sFlat.Length);
}
