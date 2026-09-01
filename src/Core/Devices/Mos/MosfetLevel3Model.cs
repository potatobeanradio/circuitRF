namespace CircuitRF.Core.Devices.Mos;

/// <summary>
/// The level-3 MOS transistor — the semi-empirical short-channel model, written as the set of
/// departures from the square law that its own parameters name. Everything the two levels share —
/// terminals, ports, polarity, the bulk junctions, gate charge, the ohmic resistances, the
/// temperature relations — comes from <see cref="MosfetModelBase"/>; only the channel-current law
/// is here.
///
/// <para><b>Each parameter turns on exactly one mechanism, and each is OFF at zero.</b> That is the
/// whole shape of the model, and it is why a level-3 card that states only two or three of them is
/// an ordinary thing to import:</para>
///
/// <list type="table">
/// <item><term><c>Eta</c></term><description>Drain-induced barrier lowering — the threshold falls as
/// the drain is pulled up. This is what makes a short device's output conductance real rather than
/// a fitted slope, and it is why level 3 has no <c>Lambda</c>.</description></item>
/// <item><term><c>Theta</c></term><description>Mobility degradation with gate field — carriers
/// pressed against the oxide scatter off it, so transconductance stops rising with gate
/// drive.</description></item>
/// <item><term><c>Vmax</c></term><description>Velocity saturation. Carriers stop going faster, so
/// the current stops rising with field and the device saturates EARLIER than the square law's
/// pinch-off would.</description></item>
/// <item><term><c>Kappa</c></term><description>Channel-length modulation, done as a real shortening
/// of the channel rather than as a multiplier on the current.</description></item>
/// <item><term><c>Xj</c> with <c>Nsub</c></term><description>Short-channel charge sharing: the
/// source and drain depletion regions take a share of the bulk charge the gate would otherwise have
/// to, so the body effect weakens as the channel gets shorter.</description></item>
/// <item><term><c>Delta</c></term><description>The narrow-width effect, which pushes the other
/// way.</description></item>
/// </list>
///
/// <para><b>The derivatives are carried exactly through the whole chain by <see cref="Grad3"/></b>
/// rather than hand-derived. See that type for why: the chain here is a dozen stages deep, and a
/// single dropped term produces a Jacobian that is plausible everywhere and right nowhere. What
/// comes out is the analytic derivative, not a finite difference.</para>
///
/// <para><b>The gate charge is the long-channel one</b>, shared with level 1 — the base's own. That
/// is deliberate and it is what the published model does too: the 2/3 factor and the drain/source
/// partition are long-channel results, and level 3's departures are all in the CURRENT. So the
/// charge model does not see the DIBL shift or the charge-sharing factor.</para>
///
/// <para><b>What is NOT modelled, deliberately:</b> subthreshold conduction (<c>Nfs</c>) — the
/// current still goes to exactly zero at threshold, so a device biased there is being asked a
/// question this model cannot answer; and the impact-ionisation substrate current. Both are real
/// level-3 mechanisms and both are absent, which is why they are named here rather than left to be
/// discovered.</para>
/// </summary>
public sealed class MosfetLevel3Model : MosfetModelBase
{
    /// <summary>
    /// Where the channel-length-modulation square root is softened. <c>Δl ∝ √(Vds − Vdsat)</c> has an
    /// UNBOUNDED derivative exactly at the saturation boundary, which is where a Newton iterate
    /// spends its time — and an unbounded Jacobian entry is not a matrix. Shifting the argument by
    /// this much and subtracting the same offset keeps <c>Δl = 0</c> at the boundary while making the
    /// slope there large but finite. A tenth of a millivolt is far below anything a bias means.
    /// </summary>
    private const double ClmSoftening = 1e-4;

    /// <summary>
    /// The published charge-sharing polynomial's three coefficients. They are a fit, not derived
    /// quantities, and they are written here as the one place they appear.
    /// </summary>
    private const double Ck1 = 0.0631353, Ck2 = 0.8013292, Ck3 = -0.01110777;

    private readonly double _kp, _wOverL;
    private readonly double _eta, _theta, _kappa, _vmax, _delta, _xj;
    private readonly double _sigma;      // the DIBL coefficient, formed once
    private readonly double _fn;         // the narrow-width term, formed once
    private readonly double _xd;         // depletion-width coefficient, m/√V; zero when Nsub is absent
    private readonly double _vbi;        // the threshold's bias-independent part
    private readonly double _sqrtPhi;

    /// <param name="eta">Drain-induced barrier lowering. Zero means NOT MODELLED.</param>
    /// <param name="theta">Mobility degradation with gate field, 1/V. Zero means NOT MODELLED.</param>
    /// <param name="kappa">Channel-length modulation. Zero means NOT MODELLED — the channel does not
    /// shorten at all, which is a saturation with no output slope.</param>
    /// <param name="vmax">Carrier saturation velocity, m/s. Zero means NOT MODELLED, and the device
    /// then saturates at the square law's own pinch-off.</param>
    /// <param name="delta">Narrow-width factor. Zero means NOT MODELLED.</param>
    /// <param name="xj">Metallurgical junction depth, m. With <c>Nsub</c>, turns on short-channel
    /// charge sharing; without either, there is none.</param>
    public MosfetLevel3Model(
        Channel4 channel = Channel4.N,
        double vto = 1.0, double kp = 2.0e-5, double gamma = 0.0, double phi = 0.6,
        double nsub = 0.0,
        double eta = 0.0, double theta = 0.0, double kappa = 0.2, double vmax = 0.0,
        double delta = 0.0, double xj = 0.0,
        double w = 100e-6, double l = 100e-6, double ld = 0.0, double tox = 0.0, double uo = 600.0,
        double cgso = 0.0, double cgdo = 0.0, double cgbo = 0.0,
        double saturationCurrent = 1e-14, double saturationCurrentDensity = 0.0,
        double bulkEmission = 1.0,
        double cbd = 0.0, double cbs = 0.0, double cj = 0.0, double cjsw = 0.0,
        double ad = 0.0, double @as = 0.0, double pd = 0.0, double ps = 0.0,
        double pb = 0.8, double mj = 0.5, double mjsw = 0.33, double fc = 0.5,
        double rd = 0.0, double rs = 0.0, double rsh = 0.0, double nrd = 0.0, double nrs = 0.0,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double xti = 3.0, double eg = Temperature.SiliconBandgapEv)
        : base(channel, vto, kp, gamma, phi, nsub, w, l, ld, tox, uo,
               cgso, cgdo, cgbo,
               saturationCurrent, saturationCurrentDensity, bulkEmission,
               cbd, cbs, cj, cjsw, ad, @as, pd, ps, pb, mj, mjsw, fc,
               rd, rs, rsh, nrd, nrs, tempC, tnomC, xti, eg)
    {
        _kp     = Kp;
        _wOverL = Width / Leff;
        _eta    = eta;
        _theta  = theta > 0 ? theta : 0.0;
        _kappa  = kappa > 0 ? kappa : 0.0;
        _vmax   = vmax > 0 ? vmax : 0.0;
        _delta  = delta;
        _xj     = xj > 0 ? xj : 0.0;

        _sqrtPhi = System.Math.Sqrt(Phi);
        // The bias-independent part of the threshold, from the card's own Vto with the body-effect
        // term taken off it — the published derivation, and the same one level 1 makes implicitly.
        //
        // <b>The narrow-width term is NOT taken off as well</b>, and that is the point of it: it is
        // an ADDITION on top of Vto, so a narrow device's threshold is HIGHER than the card's stated
        // one. Subtracting it here too would make Vth(Vbs = 0) come out as exactly Vto whatever
        // Delta said — a parameter read, carried, and doing nothing, which is the failure mode this
        // whole file is written to avoid. The same goes for the charge-sharing factor, which lowers
        // it: neither is a correction to Vto, both are what the geometry does to it.
        _vbi = Vth0 - Gamma * _sqrtPhi;

        // The narrow-width term. Formed once: it is constant for the device's lifetime and Channel
        // is on the Newton inner loop.
        _fn = _delta != 0 && CoxPerArea > 0
            ? _delta * System.Math.PI * SiliconRelativePermittivity * Eps0 / (2.0 * CoxPerArea * Width)
            : 0.0;

        // The DIBL coefficient. The constant is the published one and carries the units — it is
        // 8.15e-22 with Cox in F/m² and Leff in m, which is what makes sigma dimensionless.
        _sigma = _eta != 0 && CoxPerArea > 0
            ? _eta * 8.15e-22 / (CoxPerArea * Leff * Leff * Leff)
            : 0.0;

        // The depletion-width coefficient. It needs the substrate doping and there is nothing else
        // to derive one from, so without Nsub there is no charge sharing and no channel-length
        // modulation — both are stated as absent rather than guessed at.
        double nsubM3 = nsub > 0 ? nsub * 1e6 : 0.0;        // cm^-3 on the card, m^-3 here
        _xd = nsubM3 > 0
            ? System.Math.Sqrt(2.0 * SiliconRelativePermittivity * Eps0 / (Temperature.ElemCharge * nsubM3))
            : 0.0;
    }

    /// <inheritdoc/>
    protected override (double Id, double Gm, double Gds, double Gmbs) Channel(
        double vgs, double vds, double vbs)
    {
        var g = Grad3.Vgs(vgs);
        var d = Grad3.Vds(vds);
        var b = Grad3.Vbs(vbs);

        // ── Threshold ─────────────────────────────────────────────────────────
        // √(Phi − Vbs), through the BASE's own continuation rather than a local one. Two reasons,
        // both load-bearing:
        //
        //   * the base builds the gate charge out of the same square root, so a second continuation
        //     here would have this law's threshold and that charge disagreeing about one quantity
        //     inside a single device;
        //   * `fb` below DIVIDES by it. A floor freezes the body effect (gmbs reads exactly zero
        //     and the drain current flatlines while the device is still conducting); a tangent goes
        //     NEGATIVE a couple of millivolts on and puts a pole in fb. The base's reciprocal
        //     continuation is the one that is smooth in value and slope AND stays positive — see
        //     BodySqrt.
        //
        // It depends on Vbs alone, so it enters the chain with that one derivative.
        var (sqV, sqDVbs) = BodySqrt(vbs);
        var sq = new Grad3(sqV, 0.0, 0.0, sqDVbs);

        // Phi − Vbs itself, exact and uncontinued: the narrow-width term is LINEAR in it, so it has
        // no runaway to continue. Only the square root does.
        var arg = Phi - b;

        // Short-channel charge sharing: the source and drain depletion regions take a share of the
        // bulk charge the gate would otherwise have to hold, so the body effect weakens as the
        // channel shortens. Without Xj or Nsub there is no such share and the factor is exactly 1.
        var fs = Grad3.Const(1.0);
        if (_xj > 0 && _xd > 0)
        {
            var wp = _xd * sq;
            var u  = wp / _xj;
            var wc = _xj * (Ck1 + Ck2 * u + Ck3 * (u * u));
            var t  = wp / (_xj + wp);
            // t is strictly between 0 and 1 whenever Xj is stated, so the floor below cannot be
            // reached by a real parameter set. It is here because a square root's derivative is
            // unbounded at zero, and a floor is cheaper than proving that bound holds under every
            // future edit — floored rather than OFFSET, because adding a constant inside the root
            // would bias the value everywhere instead of only where it is needed.
            var root = 1.0 - t * t;
            if (root.V < 1e-12) root = Grad3.Const(1e-12);
            fs = 1.0 - ((LateralDiffusion + wc) / Leff * Grad3.Sqrt(root) - LateralDiffusion / Leff);
        }

        // Vth = Vbi − sigma·Vds + Gamma·fs·√(Phi−Vbs) + fn·(Phi−Vbs)
        var vth = _vbi - _sigma * d + Gamma * fs * sq + _fn * arg;
        var vgt = g - vth;
        if (vgt.V <= 0.0) return (0.0, 0.0, 0.0, 0.0);       // cutoff: off, and its derivatives with it

        // The bulk-charge factor: how much of an increase in channel potential is spent widening the
        // depletion region instead of driving current. It is what replaces the square law's plain
        // Vds/2 term.
        var fb = Gamma * fs / (4.0 * sq) + _fn;

        // ── Effective mobility ────────────────────────────────────────────────
        // Carriers pressed against the oxide by the gate field scatter off it, so transconductance
        // stops rising with gate drive. Theta = 0 leaves this exactly 1.
        var beta = Grad3.Const(_kp * _wOverL);
        if (_theta > 0) beta = beta / (1.0 + _theta * vgt);

        // ── Saturation voltage ────────────────────────────────────────────────
        var vdsat = vgt / (1.0 + fb);
        // vc is the voltage at which carriers reach saturation velocity along this channel. Written
        // through Cox rather than through the mobility directly, because Kp IS the mobility times
        // Cox and forming the mobility back out would be one division with nothing to gain.
        Grad3 vc = Grad3.Const(0.0);
        bool velocitySaturation = _vmax > 0 && CoxPerArea > 0 && beta.V > 0;
        if (velocitySaturation)
        {
            // ueff = beta·Leff/(W·Cox), so vc = Vmax·Leff/ueff = Vmax·W·Cox/beta.
            vc = _vmax * Width * CoxPerArea / beta;
            // vdsat = vsat + vc − √(vsat² + vc²): a smooth minimum of the two, which is exactly what
            // "the device saturates at whichever comes first" means.
            vdsat = vdsat + vc - Grad3.Sqrt(vdsat * vdsat + vc * vc);
        }

        // ── The current ───────────────────────────────────────────────────────
        bool linear = vds < vdsat.V;
        var vdse = linear ? d : vdsat;

        var id = beta * (vgt - (1.0 + fb) * 0.5 * vdse) * vdse;
        if (velocitySaturation) id = id / (1.0 + vdse / vc);

        // Channel-length modulation, past saturation only. Done as a real SHORTENING of the channel
        // rather than as a fitted multiplier, which is the whole difference from level 1's Lambda —
        // and it needs the depletion-width coefficient, so without Nsub there is none.
        if (!linear && _kappa > 0 && _xd > 0)
        {
            var over = d - vdsat;
            if (over.V < 0) over = Grad3.Const(0.0);

            Grad3 dl;
            if (velocitySaturation)
            {
                // With velocity saturation the published form carries the field at the pinch-off
                // point, which makes the derivative at the boundary FINITE on its own — no softening
                // is needed or applied.
                var ep = vc * (vc + vdsat) / (Leff * vdsat);
                var a  = ep * (_xd * _xd) * 0.5;
                dl = Grad3.Sqrt(a * a + _kappa * (_xd * _xd) * over) - a;
            }
            else
            {
                // Without it the expression is a bare square root, whose slope at the boundary is
                // unbounded. Shifted by ClmSoftening and offset back, so Δl is still exactly zero
                // there and the slope is large but finite.
                dl = _xd * (Grad3.Sqrt(_kappa * over + _kappa * ClmSoftening)
                            - System.Math.Sqrt(_kappa * ClmSoftening));
            }

            // Δl cannot reach Leff — the channel would have vanished. The published substitution
            // past Leff/2 is continuous in value AND slope there (both sides give Leff/2 and a slope
            // of 1), so it is a smooth ceiling rather than a clamp with a kink in it.
            if (dl.V > 0.5 * Leff) dl = Leff - (0.25 * Leff * Leff) / dl;

            id = id * (Leff / (Leff - dl));
        }

        return (id.V, id.DGs, id.DDs, id.DBs);
    }
}
