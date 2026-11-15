// ================================================================
//  PropagationChannelModel.Ends.cs  —  instance ends, polarization and the ray unknowns (brief PCH-8)
//
//  Each end is a set of radiating elements: a pin is one (a gain with a polarization, or one port's
//  pattern), a Phased Antenna Array named by Tx=/Rx= is its N elements. Toward a global direction u
//  each element has a realized-gain field F (|F|² = G_r: an array element's pattern per unit incident
//  wave times √(4π/η₀), the same normalization a pin pattern carries) in u's global (θ̂, φ̂), its
//  position phase e^(jk·û_local·r_n) included. A ray carries the transmit end's field to the receive
//  end through its 2×2 polarization matrix M (note §2.1, eq. 1):
//
//    y_r      = s_r · M_r · Σ_m F_tx,m(Ω_D,r) · 2a_m          (a 2-vector: the ray unknowns, θ and φ)
//    s_r      = λ/(4πd) · 10^(−Lex/20) · A_r · e^(−j2πf·τ_r) · fade_r(t) · e^(j2π·ν_r·t)
//
//  and a receive element n takes c_n = ½·j·Σ_r F_rx,n(Ω_A,r)ᵀ·y_r — PAA-7's eq. (4), whose j is the
//  reciprocity theorem's: a pin source of EIRP P·G at distance d puts on the element exactly the wave
//  PAA-7's incident wave of density P·G/(4πd²) from that direction does (gate 1). A pin receive end
//  takes ½·Σ_r F_rxᵀ·y_r on its wave row with no j, as PCH-6's H has none: the bulk phase is not
//  simulated, so a constant phase is invisible, and pin-to-pin links keep PCH-6's numbers.
//
//  The coupling is stamped as 2K auxiliary unknowns, one pair per ray (note §5.4): each row couples to
//  the transmit elements' nodes and branches, and each receive element's source row to the ray pairs.
//  The entries are 2K·(2N_t + N_r); nothing of N_r × N_t is ever formed. The array's branches are read
//  from its own stamp, so a channel with an instance end is stamped after every array (the elaborator
//  moves it last).
//
//  A gain end with no stated polarization is matched to the other end along the line of sight (as
//  PCH-6's gain end was co-polar); two gain ends with none are both θ̂.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using RfCore.Propagation;

namespace CircuitRF.Core.Devices;

public sealed partial class PropagationChannelModel
{
    /// <summary>One end at one frequency: its elements' realized-gain fields toward a global direction.</summary>
    internal sealed class EndField
    {
        public required int Elements { get; init; }
        public required Func<int, bool> Active { get; init; }
        /// <summary>For a global direction u: element e's field, in u's global (θ̂, φ̂), its position phase in it.</summary>
        public required Func<Vec3, Func<int, (Complex Theta, Complex Phi)>> Toward { get; init; }
        /// <summary>True for an array end: its received wave carries reciprocity's j.</summary>
        public bool IsArray { get; init; }
        /// <summary>For a global direction u: the end's reference element's field without its position phase, and u in the end's own
        /// frame (θ, φ radians).</summary>
        public required Func<Vec3, ((Complex Theta, Complex Phi) Field, double Theta, double Phi)> Reference { get; init; }
    }

    /// <summary>Counters for brief PCH-8's gates: coupling entries stamped through ray unknowns, and the longest coefficient array
    /// the channel allocated (an N_r × N_t matrix would show here).</summary>
    public static long RayCouplingEntries => Interlocked.Read(ref _rayEntries);
    public static long LargestCoefficientArray => Interlocked.Read(ref _largest);
    private static long _rayEntries, _largest;
    internal static void NoteArray(long length) { long seen; while (length > (seen = Interlocked.Read(ref _largest)) && Interlocked.CompareExchange(ref _largest, length, seen) != seen) { } }
    public static void ResetCounters() { Interlocked.Exchange(ref _rayEntries, 0); Interlocked.Exchange(ref _largest, 0); }

    /// <summary>The array end's pattern field per unit incident wave, as a realized-gain field: E·√(4π/η₀).</summary>
    private static EndField ArrayField(PhasedArrayModel array, AntennaFrame frame, double hz)
    {
        var rad = array.Radiation(hz);
        double scale = Math.Sqrt(4 * Math.PI / rad.Eta0), k = 2 * Math.PI * hz / rad.C0;
        var lattice = array.Lattice;
        return new EndField
        {
            Elements = lattice.Count,
            Active = array.Radiates,
            IsArray = true,
            Reference = u =>
            {
                var (th, ph) = frame.Angles(u);
                var (et, ep) = rad.At(rad.PatternOf[ReferenceElement(array)], th, ph);
                return (GlobalBasis.Components(frame.ToGlobal(th, ph, et * scale, ep * scale), u), th, ph);
            },
            Toward = u =>
            {
                var (th, ph) = frame.Angles(u);
                double st = Math.Sin(th), ux = st * Math.Cos(ph), uy = st * Math.Sin(ph);
                var cache = new Dictionary<int, (Complex, Complex)>();
                return e =>
                {
                    int p = rad.PatternOf[e];
                    if (!cache.TryGetValue(p, out var f))
                    {
                        var (et, ep) = rad.At(p, th, ph);
                        f = GlobalBasis.Components(frame.ToGlobal(th, ph, et * scale, ep * scale), u);
                        cache[p] = f;
                    }
                    var (x, y) = lattice.Position(e);
                    var (sn, cs) = Math.SinCos(k * (ux * x + uy * y));
                    var phase = new Complex(cs, sn);
                    return (f.Item1 * phase, f.Item2 * phase);
                };
            },
        };
    }

    private static EndField PinField(Func<Vec3, (Complex, Complex)> f)
        => new() { Elements = 1, Active = _ => true, Toward = u => { var v = f(u); return _ => v; }, Reference = u => (f(u), double.NaN, double.NaN) };

    /// <summary>One ray as its two ends see it: the transmit end's field toward it (its reference element's, without position phase),
    /// the polarization matrix it is carried through (Faraday included), the receive end's reference field from it, and its arrival in
    /// the receive end's own frame (radians; NaN for a pin). What a report lists and an independent check recomputes from.</summary>
    public sealed record RayEnds(ChannelRay Ray, (Complex Theta, Complex Phi) Tx, PolarizationMatrix M, (Complex Theta, Complex Phi) Rx,
                                 double RxTheta, double RxPhi, double TxTheta, double TxPhi);

    /// <summary>Every ray's ends at <paramref name="hz"/> (≥ 0).</summary>
    public IReadOnlyList<RayEnds> DescribeRays(double hz)
    {
        var r = At(ReferenceHz(hz));
        var (tx, rx) = EndFields(r, hz);
        return [.. r.Rays.Select(ray =>
        {
            var t = tx.Reference(ray.Departure);
            var a = rx.Reference(ray.ArrivalFrom);
            return new RayEnds(ray, t.Field, RayMatrix(r, ray), a.Field, a.Theta, a.Phi, t.Theta, t.Phi);
        })];
    }

    /// <summary>The reference element of an end, the one a gain end's polarization is matched to: an array's centre element.</summary>
    private static int ReferenceElement(PhasedArrayModel? array) => array is null ? 0 : (array.Lattice.Rows / 2) * array.Lattice.Cols + array.Lattice.Cols / 2;

    /// <summary>The two ends' fields at <paramref name="hz"/> (≥ 0) for realization <paramref name="r"/>.</summary>
    internal (EndField Tx, EndField Rx) EndFields(Realization r, double hz)
    {
        var los = r.Geometry.LineOfSight(r.ReferenceHz);
        EndField? Own(bool tx)
        {
            var array = tx ? _txArray : _rxArray;
            if ((tx ? _txInstance : _rxInstance) is { } inst)
                return array is null ? throw new ParameterRefusalException(PropagationChannelDiagnostics.EndNotBound(tx ? "Tx" : "Rx", inst))
                                     : ArrayField(array, Frame(tx, r), hz);
            var pat = PatternAt(tx ? _txPattern : _rxPattern, hz, tx ? "Tx" : "Rx");
            if (pat is not null)
            {
                var frame = Frame(tx, r);
                return PinField(u => GlobalBasis.Components(Field(pat, frame, u), u));
            }
            var pol = tx ? _txPol : _rxPol;
            if (pol is { } pp)
            {
                double g = Math.Pow(10, (tx ? _txGainDb : _rxGainDb) / 20);
                var frame = Frame(tx, r);
                return PinField(u =>
                {
                    var (th, ph) = frame.Angles(u);
                    return GlobalBasis.Components(frame.ToGlobal(th, ph, g * pp.Theta, g * pp.Phi), u);
                });
            }
            return null;   // a gain end with no polarization: matched below
        }
        var txF = Own(true);
        var rxF = Own(false);
        var m = PolarizationMatrix.LineOfSight;
        if (txF is null)
        {
            double g = Math.Pow(10, _txGainDb / 20);
            (Complex, Complex) at = (1, 0);
            if (rxF is not null)
            {
                var (ft, fp) = rxF.Toward(los.ArrivalFrom)(ReferenceElement(_rxArray));
                // the transmit field F maximizing |F_rxᵀ·M·F| along the line of sight: conj(Mᵀ·F_rx), M real
                var (a, b) = m.Transposed.Apply(ft, fp);
                double n = Math.Sqrt(a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude);
                if (n > 0) at = (Complex.Conjugate(a) / n, Complex.Conjugate(b) / n);
            }
            var pvec = GlobalBasis.Vector(at.Item1, at.Item2, los.Departure);
            txF = PinField(u => { var (t, p) = GlobalBasis.Components(pvec, u); return (g * t, g * p); });
        }
        if (rxF is null)
        {
            double g = Math.Pow(10, _rxGainDb / 20);
            var (ft, fp) = txF.Toward(los.Departure)(ReferenceElement(_txArray));
            var (a, b) = m.Apply(ft, fp);
            double n = Math.Sqrt(a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude);
            (Complex, Complex) at = n > 0 ? (Complex.Conjugate(a) / n, Complex.Conjugate(b) / n) : (1, 0);
            var pvec = GlobalBasis.Vector(at.Item1, at.Item2, los.ArrivalFrom);
            rxF = PinField(u => { var (t, p) = GlobalBasis.Components(pvec, u); return (g * t, g * p); });
        }
        return (txF, rxF);
    }

    /// <summary>True when the pin-to-pin coupling must use the polarized form: a ray carries a polarization matrix, or a pin end states
    /// its polarization (otherwise PCH-6's form, unchanged).</summary>
    private bool PolarizedPins(Realization r) => _txPol is not null || _rxPol is not null || r.Rays.Any(x => x.Pol is not null) || r.FaradayRad != 0;

    /// <summary>Pin to pin, polarized: F_rxᵀ·M_r·F_tx along each ray.</summary>
    private Complex[] PolarizedCouplings(Realization r, double hz)
    {
        var (tx, rx) = EndFields(r, hz);
        var c = new Complex[r.Rays.Count];
        for (int i = 0; i < c.Length; i++)
        {
            var ray = r.Rays[i];
            var (ft, fp) = tx.Toward(ray.Departure)(0);
            var (mt, mp) = RayMatrix(r, ray).Apply(ft, fp);
            var (rt, rp) = rx.Toward(ray.ArrivalFrom)(0);
            c[i] = rt * mt + rp * mp;
        }
        return c;
    }

    /// <summary>A ray's polarization matrix with the link's Faraday rotation applied (R-pch8-8).</summary>
    private static PolarizationMatrix RayMatrix(Realization r, ChannelRay ray) => r.FaradayRad == 0 ? ray.Polarization : ray.Polarization.Faraday(r.FaradayRad);

    /// <summary>
    /// brief-pch-8 R-pch8-2 — the coupling through ray unknowns, for a channel with an instance end: the pins' own wave rows (their
    /// reflection only), then per ray two unknowns y = s·M·Σ F_tx·2a and the receive elements' sources ½·(j)·F_rxᵀ·y. While an envelope
    /// run steps (<see cref="Stepping"/>) only the pins are stamped: EnvelopeChannel drives the receive side.
    /// </summary>
    private void StampInstance(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double hz = omega / (2 * Math.PI), f = Math.Abs(hz);
        bool conj = omega < 0;
        Complex C(Complex x) => conj ? Complex.Conjugate(x) : x;
        int txNode = TxPin >= 0 && TxPin < c.Nodes.Length ? c.Nodes[TxPin] : 0;
        int rxNode = RxPin >= 0 && RxPin < c.Nodes.Length ? c.Nodes[RxPin] : 0;
        _branch[0] = TxPin >= 0 ? mna.AddBranch() : -1;
        _branch[1] = RxPin >= 0 ? mna.AddBranch() : -1;
        var pinZ0 = new Complex[2];
        foreach (int logical in new[] { 0, 1 })
        {
            int br = _branch[logical];
            if (br < 0) continue;
            int node = logical == 0 ? txNode : rxNode;
            mna.AddBranchCurrent(br, node, 0);
            var (z0, s) = End(logical, Math.Max(f, 1));
            z0 = C(z0); s = C(s);
            pinZ0[logical] = z0;
            double root = Math.Sqrt(z0.Real);
            Complex back = z0.Imaginary == 0 ? root : Complex.Conjugate(z0) / root, fwd = z0.Imaginary == 0 ? root : z0 / root;
            mna.AddConstraint(br, node, 1 / root);
            mna.AddBranchConstraint(br, br, -back);
            if (s != Complex.Zero)
            {
                mna.AddConstraint(br, node, -s / root);
                mna.AddBranchConstraint(br, br, -s * fwd);
            }
        }
        if (Stepping || hz == 0) return;

        var r = At(ReferenceHz(f));
        var (txF, rxF) = EndFields(r, f);
        double amp = Math.Pow(10, -r.ExcessDb / 20) * PropagationMath.C / (4 * Math.PI * f * r.DistanceM);
        double scale = f / r.ReferenceHz, tt = FrozenTimeS;
        long entries = 0;
        for (int i = 0; i < r.Rays.Count; i++)
        {
            var ray = r.Rays[i];
            var fade = i < r.Fades.Count ? r.Fades[i] : null;
            Complex sr = amp * ray.Gain * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * ray.ExcessDelayS)
                         * (fade is null || fade.Sinusoids == 0 ? Complex.One : fade.Gain(tt))
                         * Complex.FromPolarCoordinates(1, 2 * Math.PI * ray.DopplerHz * scale * tt + Math.PI * r.DriftHzps * scale * tt * tt);
            if (sr == Complex.Zero) continue;
            var mat = RayMatrix(r, ray);
            int yt = mna.AddBranch(), yp = mna.AddBranch();
            mna.AddBranchConstraint(yt, yt, 1);
            mna.AddBranchConstraint(yp, yp, 1);

            // the transmit end: y = s·M·Σ F_m·(v_m + Z0·i_m)/√Re Z0
            var dep = txF.Toward(ray.Departure);
            for (int mIdx = 0; mIdx < txF.Elements; mIdx++)
            {
                if (!txF.Active(mIdx)) continue;
                var (ft, fp) = dep(mIdx);
                var (mt, mp) = mat.Apply(ft, fp);
                Complex tT = C(sr * mt), tP = C(sr * mp);
                int node, br, reference = 0;
                Complex z0;
                if (_txArray is { } ta)
                {
                    node = _txEc!.Nodes[mIdx]; br = ta.PortBranchIndices[mIdx]; reference = _txEc.ReferenceNode; z0 = C(ta.Z0);
                }
                else { node = txNode; br = _branch[0]; z0 = pinZ0[0]; }
                double root = Math.Sqrt(z0.Real);
                Complex fwd = z0.Imaginary == 0 ? root : z0 / root;
                foreach (var (y, t) in new[] { (yt, tT), (yp, tP) })
                {
                    if (t == Complex.Zero) continue;
                    if (node > 0) mna.AddConstraint(y, node, -t / root);
                    if (reference > 0) mna.AddConstraint(y, reference, t / root);
                    mna.AddBranchConstraint(y, br, -t * fwd);
                    entries += 2;
                }
            }

            // the receive end: a source ½·(j)·F_rxᵀ·y on each element, or on the pin's wave row
            var arr = rxF.Toward(ray.ArrivalFrom);
            Complex j = rxF.IsArray ? Complex.ImaginaryOne : Complex.One;
            for (int n = 0; n < rxF.Elements; n++)
            {
                if (!rxF.Active(n)) continue;
                var (rt, rp) = arr(n);
                Complex rT = C(j * rt), rP = C(j * rp);
                if (_rxArray is { } ra)
                {
                    int src = PhasedArrayModel.SourceNode(_rxEc!, n, ra.PortCount);
                    if (src <= 0) continue;
                    if (rT != Complex.Zero) mna.AddNodeBranchCoupling(src, yt, -0.5 * rT);
                    if (rP != Complex.Zero) mna.AddNodeBranchCoupling(src, yp, -0.5 * rP);
                }
                else
                {
                    if (rT != Complex.Zero) mna.AddBranchConstraint(_branch[1], yt, -rT);
                    if (rP != Complex.Zero) mna.AddBranchConstraint(_branch[1], yp, -rP);
                }
                entries += 2;
            }
        }
        Interlocked.Add(ref _rayEntries, entries);
    }

    /// <summary>
    /// brief-pch-8 — the coupling a run reads for an instance end, per ray at the signed frequency <paramref name="hz"/>: its time
    /// terms and its two ends' coefficient vectors (T: s·M·F per transmit element without the time factors, 2a units; R: (j)·F per
    /// receive element). The envelope steps with these.
    /// </summary>
    public sealed record RayCoupling(double DelayS, double DopplerHz, double DriftHzps, RayTerm Term,
                                     Complex[] TxTheta, Complex[] TxPhi, Complex[] RxTheta, Complex[] RxPhi);

    public RayCoupling[] InstanceCouplings(double hz)
    {
        if (hz == 0) return [];
        double f = Math.Abs(hz);
        bool conj = hz < 0;
        Complex C(Complex x) => conj ? Complex.Conjugate(x) : x;
        var r = At(ReferenceHz(f));
        var (txF, rxF) = EndFields(r, f);
        double amp = Math.Pow(10, -r.ExcessDb / 20) * PropagationMath.C / (4 * Math.PI * f * r.DistanceM);
        double scale = f / r.ReferenceHz;
        var list = new RayCoupling[r.Rays.Count];
        for (int i = 0; i < list.Length; i++)
        {
            var ray = r.Rays[i];
            var fade = i < r.Fades.Count ? r.Fades[i] : null;
            Complex sr = amp * ray.Gain * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * ray.ExcessDelayS);
            var mat = RayMatrix(r, ray);
            var tt = new Complex[txF.Elements]; var tp = new Complex[txF.Elements];
            var dep = txF.Toward(ray.Departure);
            for (int m = 0; m < txF.Elements; m++)
            {
                if (!txF.Active(m)) continue;
                var (ft, fp) = dep(m);
                var (mt, mp) = mat.Apply(ft, fp);
                tt[m] = C(sr * mt); tp[m] = C(sr * mp);
            }
            var rt = new Complex[rxF.Elements]; var rp = new Complex[rxF.Elements];
            var arr = rxF.Toward(ray.ArrivalFrom);
            Complex j = rxF.IsArray ? Complex.ImaginaryOne : Complex.One;
            for (int n = 0; n < rxF.Elements; n++)
            {
                if (!rxF.Active(n)) continue;
                var (a, b) = arr(n);
                rt[n] = C(j * a); rp[n] = C(j * b);
            }
            NoteArray(Math.Max(txF.Elements, rxF.Elements));
            double nu = ray.DopplerHz * scale, dr = r.DriftHzps * scale;
            var term = conj ? new RayTerm(1, ray.ExcessDelayS, -nu, -dr, fade, Conjugate: true) : new RayTerm(1, ray.ExcessDelayS, nu, dr, fade);
            list[i] = new RayCoupling(ray.ExcessDelayS, term.DopplerHz, term.DriftHzps, term, tt, tp, rt, rp);
        }
        return list;
    }
}
