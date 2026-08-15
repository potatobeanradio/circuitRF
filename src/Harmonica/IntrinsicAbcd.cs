// ================================================================
//  IntrinsicAbcd.cs  —  R8C §5.3
//
//  The closed-form replacement for an intrinsic drag's HB-based inverse solve (InverseSolver, which
//  stays in the tree — see CircuitModel.IntrinsicDragAllowed's own remarks). Valid only under that
//  predicate: every DUT capacitor linear, no Cdg, no package input/output coupling. Under it, the
//  network between the intrinsic gate port and the source termination plane is a fixed, passive,
//  linear two-port — and likewise (independently) between the intrinsic drain port and the load
//  plane — so at each harmonic h (ω = 2π·f0·h):
//
//      Z_intr = (A·Z_ext + B) / (C·Z_ext + D)
//
//  a bilinear (Möbius) map, whose inverse is another bilinear map:
//
//      Z_ext = (D·Z_intr − B) / (−C·Z_intr + A)
//
//  so "put the glyph here" has a closed-form, per-band, per-side answer with no HB solve, no
//  Jacobian, no iteration.
// ================================================================

using System.Numerics;

namespace CircuitRF.Harmonica;

public static class IntrinsicAbcd
{
    private readonly record struct Abcd(Complex A, Complex B, Complex C, Complex D)
    {
        public static readonly Abcd Identity = new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

        public static Abcd operator *(Abcd x, Abcd y) => new(
            x.A * y.A + x.B * y.C, x.A * y.B + x.B * y.D,
            x.C * y.A + x.D * y.C, x.C * y.B + x.D * y.D);
    }

    private static Abcd Series(Complex z) => new(Complex.One, z, Complex.Zero, Complex.One);
    private static Abcd Shunt(Complex y) => new(Complex.One, Complex.Zero, y, Complex.One);

    /// <summary>
    /// The chain between one side's extrinsic termination plane and the intrinsic plane, at one
    /// harmonic. Built element by element in the PHYSICAL order the elements sit, extrinsic plane
    /// inward — <b>matching <see cref="HarmonicaNetlist.Build"/>'s own node order exactly</b>: Cpg/Cpd
    /// shunts the TERMINATION plane itself, before the Rg,Lg/Rd,Ld series lead, which is why it is
    /// listed first here even though harmonicarf.md's prose names the series lead first — the netlist
    /// is the authority (§5.4 item 3's round trip is what caught the mismatch; a lone series or shunt
    /// element is direction-symmetric and cannot). Each new element is LEFT-multiplied onto the
    /// accumulated chain (<c>chain = newElement * chain</c>), which is what makes the accumulated
    /// (A, B, C, D) satisfy this file's header formula directly (the element nearest the intrinsic
    /// plane ends up leftmost in the product, matching the standard "Zin at port 1 given a load at
    /// port 2" two-port identity with port 1 = intrinsic).
    /// </summary>
    private static Abcd Chain(TerminationSide side, CircuitModel model, double omega)
    {
        var pkg  = model.Embedding.Package;
        var caps = model.Dut.Capacitances;

        // Cdg and Rs/Ls/CgdExt never appear in this chain — CircuitModel.IntrinsicDragAllowed's
        // predicate guarantees they are zero. Asserted here rather than silently ignored.
        if (!caps.Cdg.IsAbsent || pkg.CouplesInputAndOutput)
            throw new InvalidOperationException(
                "IntrinsicAbcd requires CircuitModel.IntrinsicDragAllowed's predicate to hold: no " +
                "Cdg feedback and no package input/output coupling (Rs, Ls, CgdExt all zero).");

        var chain = Abcd.Identity;

        if (side == TerminationSide.Source)
        {
            // 1. shunt jωCpg — AT the termination plane, before the gate lead.
            if (pkg.Cpg != 0) chain = Shunt(new Complex(0, omega * pkg.Cpg)) * chain;
            // 2. series Rg + jωLg
            chain = Series(new Complex(pkg.Rg, omega * pkg.Lg)) * chain;
            // 3. shunt branch rgs + 1/(jωCgs), at the gate terminal — R8C §3's rgs enters here, the
            //    second half of "make sure rgs is accounted for when calculating the intrinsic source
            //    impedances."
            if (!caps.Cgs.IsAbsent)
            {
                var zBranch = new Complex(caps.RgsOhms, 0) +
                              Complex.One / new Complex(0, omega * caps.Cgs.Farads);
                chain = Shunt(Complex.One / zBranch) * chain;
            }
        }
        else
        {
            // 1. shunt jωCpd — AT the termination plane, before the drain lead.
            if (pkg.Cpd != 0) chain = Shunt(new Complex(0, omega * pkg.Cpd)) * chain;
            // 2. series Rd + jωLd
            chain = Series(new Complex(pkg.Rd, omega * pkg.Ld)) * chain;
            // 3. shunt jωCds, at the drain terminal.
            if (!caps.Cds.IsAbsent) chain = Shunt(new Complex(0, omega * caps.Cds.Farads)) * chain;
        }

        return chain;
    }

    /// <summary>
    /// The extrinsic termination that puts the intrinsic plane at <paramref name="zIntr"/>, on
    /// <paramref name="side"/> at harmonic <paramref name="band"/> (1 = fundamental). May return a
    /// non-finite value at the map's own pole (<c>−C·Z_intr + A → 0</c>) — the caller refuses that
    /// frame rather than moving the marker there (R-h6-9's rule).
    /// </summary>
    public static Complex ExtrinsicFor(CircuitModel model, TerminationSide side, int band, Complex zIntr)
    {
        double omega = 2.0 * Math.PI * model.Settings.FrequencyHz * band;
        var chain = Chain(side, model, omega);
        return (chain.D * zIntr - chain.B) / (-chain.C * zIntr + chain.A);
    }
}
