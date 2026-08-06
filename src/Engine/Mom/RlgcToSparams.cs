using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using RfCore;
using RfCore.Data;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The optional Kirschning–Jansen dispersion inputs (§7). Present only when the cross-section is a
/// single microstrip, which is the only case the correction is defined for.
/// </summary>
public sealed record MicrostripDispersion(double WOverH, double EpsR, double HMeters);

/// <summary>
/// RLGC → s-parameters → <see cref="DataSet"/>.
///
/// <code>
/// Z  = R + jωL
/// Y  = jω·C_complex                     (R-mom-6: this is exactly G + jωC)
/// γ  = √(ZY)          branch with Re(γ) ≥ 0
/// Zc = √(Z/Y)
/// Z11 = Z22 = Zc·coth(γℓ)
/// Z12 = Z21 = Zc / sinh(γℓ)
/// S   = RFNetwork.ZToS(Z, z0PerPort)
/// </code>
///
/// <para><b>R-mom-14.</b> The Z-matrix is formed and converted with <see cref="RFNetwork.ZToS"/>;
/// there is no second ABCD→S in this project. That routine already handles per-port and complex
/// reference impedances and is already the path every other s-parameter here goes through, so
/// reciprocity is <i>structural</i> rather than hoped for.</para>
///
/// <para><b>R-mom-15. De-embedding is a no-op for kernel A, and that is a finding, not a
/// shortcut.</b> §10.6 requires de-embedding because a <i>meshed</i> port excitation carries a port
/// discontinuity. Kernel A computes γ and Z_c analytically and forms the Z of a uniform line of
/// length ℓ — the reference planes are exactly at the line ends by construction, and there is
/// nothing to remove. The two-line calibration becomes real work at L8, when a meshed port exists;
/// building it now would be building a calibration for an error that does not exist.</para>
///
/// <para><b>Kernel A ships the single-conductor 2-port.</b> [C] is a matrix throughout, so
/// multiconductor modal decomposition is an addition at L7b, not a rewrite.</para>
/// </summary>
public static class RlgcToSparams
{
    /// <summary>Np → dB.</summary>
    public const double NeperToDb = 8.685889638065035;

    /// <summary>
    /// Dispatches on what the RLGC model actually describes: one conductor → the single-line 2-port
    /// (byte-for-byte the path kernel A shipped, and the only one the Kirschning–Jansen dispersion
    /// correction applies to), N ≥ 2 conductors → L7b-b's <b>general</b> modal 2N-port.
    ///
    /// <para><b>D1 — there is no symmetric-pair branch any more.</b> L7b's fixed
    /// <c>[1 1; 1 −1]</c> construction survives in the tests as an exact oracle; a symmetric pair
    /// now goes through the general path like everything else, because two code paths that must
    /// agree are two code paths that will eventually disagree.</para>
    /// </summary>
    /// <param name="notes">
    /// Optional collector for the per-solve remarks R-gen-5 asks to be surfaced — chiefly the
    /// measured mode-coupling residual. Additive: every existing caller passes nothing.
    /// </param>
    public static DataSet Build(
        RlgcModel              rlgc,
        double                 lengthMeters,
        double[]               freqsHz,
        Complex[]              z0PerPort,
        MicrostripDispersion?  dispersion   = null,
        CancellationToken      ct           = default,
        ICollection<string>?   notes        = null)
    {
        ArgumentNullException.ThrowIfNull(rlgc);
        ArgumentNullException.ThrowIfNull(freqsHz);
        ArgumentNullException.ThrowIfNull(z0PerPort);
        if (freqsHz.Length == 0)
            throw new ArgumentException("At least one frequency point required.", nameof(freqsHz));

        if (rlgc.ConductorCount >= 2 && z0PerPort.Length == 2 * rlgc.ConductorCount)
            return BuildGeneral(rlgc, lengthMeters, freqsHz, z0PerPort, notes, ct);

        if (rlgc.ConductorCount != 1 || z0PerPort.Length != 2)
            throw new ArgumentException(
                $"This cross-section has {rlgc.ConductorCount} signal conductor" +
                (rlgc.ConductorCount == 1 ? "" : "s") + $" and {z0PerPort.Length} ports, but a " +
                $"uniform multiconductor line needs exactly {2 * rlgc.ConductorCount} — one at each " +
                "end of each conductor (D3: port 2k−1 is conductor k's near end, 2k its far end).",
                nameof(z0PerPort));

        int nf = freqsHz.Length;
        var sMats = new Mat<Complex>[nf];

        var zc    = new Complex[nf];
        var gamma = new Complex[nf];
        var eeff  = new double[nf];
        var atten = new double[nf];
        var rpul  = new double[nf];
        var lpul  = new double[nf];
        var gpul  = new double[nf];
        var cpul  = new double[nf];

        var quiet = new MicrostripValidityReporter("(MoM dispersion correction, not reported)");
        double staticL = rlgc.LPerM;
        double staticC = rlgc.CPerM;
        double staticZ0 = staticC > 0 ? Math.Sqrt(staticL / staticC) : 0;

        for (int i = 0; i < nf; i++)
        {
            ct.ThrowIfCancellationRequested();

            double f = freqsHz[i];
            double w = 2.0 * Math.PI * f;

            double lp = staticL, cp = staticC, ee = rlgc.Eeff;
            double gp = rlgc.GPerM(w);
            double rp = rlgc.RPerM(w);

            if (dispersion is not null && f > 0 && staticZ0 > 0)
            {
                // The static solve is the thing that was validated; dispersion is a closed-form
                // correction applied ON TOP of it, never a substitute for it.
                var (z0f, eeffF) = KirschningJansen.Compute(
                    f, dispersion.WOverH, dispersion.EpsR, dispersion.HMeters,
                    staticZ0, rlgc.Eeff, quiet);
                double cf = Math.Sqrt(eeffF) / (EmConstants.C0 * z0f);
                double lf = z0f * Math.Sqrt(eeffF) / EmConstants.C0;
                gp = staticC > 0 ? gp * cf / staticC : gp;   // keep the loss tangent, not the value
                cp = cf;
                lp = lf;
                ee = eeffF;
            }

            var z = new Complex(rp, w * lp);
            var y = new Complex(gp, w * cp);

            var g = PrincipalSqrt(z * y);
            var zChar = y == Complex.Zero ? Complex.Zero : PrincipalSqrt(z / y);
            var gl = g * lengthMeters;

            var sinh = Complex.Sinh(gl);
            var z11 = zChar * Complex.Cosh(gl) / sinh;
            var z12 = zChar / sinh;

            var zMat = new Mat<Complex>(2, 2);
            zMat[0, 0] = z11; zMat[1, 1] = z11;
            zMat[0, 1] = z12; zMat[1, 0] = z12;

            sMats[i] = RFNetwork.ZToS(zMat, z0PerPort);

            zc[i]    = zChar;
            gamma[i] = g;
            eeff[i]  = ee;
            atten[i] = g.Real * NeperToDb;
            rpul[i]  = rp;
            lpul[i]  = lp;
            gpul[i]  = gp;
            cpul[i]  = cp;
        }

        // The house convention, exactly as SParameterEngine ends.
        var snp = new SNP(freqsHz, sMats, MatrixType.S, MatrixFormat.RI, z0PerPort[0]);
        var ds  = DataSetBuilder.FromSnp(snp);
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort));

        // The quantities a transmission-line solver is uniquely able to report. They cost nothing
        // and they are what makes a wrong answer diagnosable.
        // A fresh Axis per cube: DataCube keeps its axes, and sharing one mutable instance across
        // nine cubes is the kind of aliasing that only bites much later.
        Axis[] Ax() => [new Axis("freq", freqsHz, "Hz")];

        ds.AddToGroup("tline", "Zc",          new DataCube(Ax(), zc));
        ds.AddToGroup("tline", "Gamma",       new DataCube(Ax(), gamma));
        ds.AddToGroup("tline", "Eeff",        new DataCube(Ax(), eeff));
        ds.AddToGroup("tline", "AttenDbPerM", new DataCube(Ax(), atten));
        ds.AddToGroup("tline", "Rpul",        new DataCube(Ax(), rpul));
        ds.AddToGroup("tline", "Lpul",        new DataCube(Ax(), lpul));
        ds.AddToGroup("tline", "Gpul",        new DataCube(Ax(), gpul));
        ds.AddToGroup("tline", "Cpul",        new DataCube(Ax(), cpul));

        return ds;
    }

    // ── L7b-b: the GENERAL multiconductor 2N-port ─────────────────────────────────────────────

    /// <summary>
    /// <b>R-gen-2 — L7b's 4-port block construction is the N = 2 special case of this one.</b> With
    /// <c>Tv</c> the voltage modal matrix and <c>x_m</c> the entry of mode m's own 2-port line
    /// matrix, every block of the 2N-port Z is
    /// <code>Zblock(x) = Tv · diag(x_m) · Ti⁻¹</code>
    /// with <c>x_m = Zc_m·coth(γ_m ℓ)</c> for the near/near and far/far blocks and
    /// <c>x_m = Zc_m/sinh(γ_m ℓ)</c> for the near/far ones, exactly as the single line does.
    /// Substituting <c>Tv = Ti = [[1, 1], [1, −1]]</c> reproduces L7b's <c>Zs = ½(Z_e2 + Z_o2)</c>,
    /// <c>Zm = ½(Z_e2 − Z_o2)</c> identically — checked by hand before this was written, and pinned
    /// by the continuity gate in <c>GeneralModalTests</c>.
    ///
    /// <para><b>D3 — the port map is unchanged.</b> Port <c>2k−1</c> is conductor <i>k</i>'s NEAR
    /// end, <c>2k</c> its FAR end. A transposed map produces a coupler whose through and coupled
    /// ports are swapped: smooth, plausible, wrong, and invisible in a magnitude plot of a symmetric
    /// structure.</para>
    ///
    /// <para><b>Reciprocity survives as a STRUCTURAL property, and this is why.</b> R-gen-2 warns
    /// that a general <c>Tv·diag·Ti⁻¹</c> does not obviously preserve <c>S = Sᵀ</c>. It does here,
    /// because <c>Ti</c> is the biorthogonal partner up to a per-mode scale: <c>Ti = (Tvᵀ)⁻¹·diag(e)</c>
    /// makes <c>Ti⁻¹ = diag(1/e)·Tvᵀ</c>, so every block is <c>Tv·diag(x/e)·Tvᵀ</c> — symmetric for
    /// ANY Tv. It is assembled as <c>Σ_m (x_m/e_m)·Tv[i,m]·Tv[j,m]</c> so that the [i,j] and [j,i]
    /// entries are <b>bit-identical</b>, not merely equal to solver tolerance, and
    /// <see cref="RFNetwork.ZToS"/> then carries that through to S exactly as it does for the single
    /// line (R-mom-14).</para>
    /// </summary>
    private static DataSet BuildGeneral(
        RlgcModel rlgc, double lengthMeters, double[] freqsHz, Complex[] z0PerPort,
        ICollection<string>? notes, CancellationToken ct)
    {
        // Route A step 1–3, ONCE for the whole sweep. Tv comes from the lossless problem, which has
        // no ω in it — so mode identity is fixed for the entire sweep by construction (R-gen-7), and
        // R-mom-11's frequency-independence is untouched.
        var modes = ModalDecomposition.DecomposeGeneral(rlgc);

        int n  = modes.ModeCount;
        int np = 2 * n;
        int nf = freqsHz.Length;

        var sMats = new Mat<Complex>[nf];

        // Rank-2 [freq, mode] storage, row-major with mode fastest (R-gen-8).
        var zc     = new Complex[nf * n];
        var gamma  = new Complex[nf * n];
        var atten  = new double[nf * n];
        var rpul   = new double[nf * n];
        var gpul   = new double[nf * n];
        var eeff   = new double[nf * n];
        var lpul   = new double[nf * n];
        var cpul   = new double[nf * n];
        var couple = new double[nf];

        double worstCoupling = 0;
        var xSelf  = new Complex[n];
        var xCross = new Complex[n];

        for (int i = 0; i < nf; i++)
        {
            ct.ThrowIfCancellationRequested();

            double w = 2.0 * Math.PI * freqsHz[i];

            // Route A steps 4–5: form the FULL modal matrices with loss in them, keep the diagonals,
            // and measure what was discarded.
            var pt = ModalDecomposition.EvaluateAt(rlgc, modes, w);
            couple[i] = pt.ModeCouplingResidual;
            worstCoupling = Math.Max(worstCoupling, pt.ModeCouplingResidual);

            for (int m = 0; m < n; m++)
            {
                var z = pt.Z[m];
                var y = pt.Y[m];

                var g  = PrincipalSqrt(z * y);
                var zCh = y == Complex.Zero ? Complex.Zero : PrincipalSqrt(z / y);
                var gl = g * lengthMeters;
                var sinh = Complex.Sinh(gl);

                xSelf[m]  = zCh * Complex.Cosh(gl) / sinh;
                xCross[m] = zCh / sinh;

                int o = i * n + m;
                zc[o]    = zCh;
                gamma[o] = g;
                atten[o] = g.Real * NeperToDb;
                rpul[o]  = pt.RPerM[m];
                gpul[o]  = -w * modes.CComplexPerM[m].Imaginary;
                eeff[o]  = modes.Eeff[m];
                lpul[o]  = modes.LPerM[m];
                cpul[o]  = modes.CComplexPerM[m].Real;
            }

            var self  = ModalBlock(modes, xSelf);
            var cross = ModalBlock(modes, xCross);

            var zMat = new Mat<Complex>(np, np);
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                zMat[2 * a,     2 * b]     = self[a, b];    // near–near
                zMat[2 * a + 1, 2 * b + 1] = self[a, b];    // far–far
                zMat[2 * a,     2 * b + 1] = cross[a, b];   // near–far
                zMat[2 * a + 1, 2 * b]     = cross[a, b];   // far–near
            }

            sMats[i] = RFNetwork.ZToS(zMat, z0PerPort);
        }

        var snp = new SNP(freqsHz, sMats, MatrixType.S, MatrixFormat.RI, z0PerPort[0]);
        var ds  = DataSetBuilder.FromSnp(snp);
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort));

        // D4 — no new result type, for the third phase running. R-gen-8: N modes get a MODE AXIS,
        // not N named scalars — "even" and "odd" are names two modes have and N do not.
        var modeValues = new double[n];
        for (int m = 0; m < n; m++) modeValues[m] = m;

        Axis[] Ax2() => [new Axis("freq", freqsHz, "Hz"), new Axis("mode", modeValues)];
        Axis[] Ax1() => [new Axis("freq", freqsHz, "Hz")];

        ds.AddToGroup("tline", "Zc",          new DataCube(Ax2(), zc));
        ds.AddToGroup("tline", "Gamma",       new DataCube(Ax2(), gamma));
        ds.AddToGroup("tline", "Eeff",        new DataCube(Ax2(), eeff));
        ds.AddToGroup("tline", "AttenDbPerM", new DataCube(Ax2(), atten));
        ds.AddToGroup("tline", "Rpul",        new DataCube(Ax2(), rpul));
        ds.AddToGroup("tline", "Lpul",        new DataCube(Ax2(), lpul));
        ds.AddToGroup("tline", "Gpul",        new DataCube(Ax2(), gpul));
        ds.AddToGroup("tline", "Cpul",        new DataCube(Ax2(), cpul));
        ds.AddToGroup("tline", "ModeCouplingResidual", new DataCube(Ax1(), couple));

        AddEvenOddAliases(ds, modes, freqsHz, n, zc, gamma, atten, rpul, gpul, eeff, lpul, cpul);

        if (notes is not null)
        {
            notes.Add(
                $"Route A mode-coupling residual max_{{i≠j}}|M_ij|/min_i|M_ii| peaks at " +
                $"{worstCoupling:P3} across the sweep — that is the fraction of the modal matrices " +
                "the perturbative treatment of loss discards. It is reported per frequency as " +
                "tline.ModeCouplingResidual.");
            if (worstCoupling > ModalDecomposition.ModeCouplingWarnThreshold)
                notes.Add(
                    $"That residual is above {ModalDecomposition.ModeCouplingWarnThreshold:P0}: the " +
                    "modal matrix taken from the lossless problem no longer diagonalises the lossy " +
                    "one well. Loss matters relative to reactance as R/(ωL) and G/(ωC), both of " +
                    "which grow as ω falls — raising the lowest sweep frequency, or lowering tanδ, " +
                    "is what shrinks it.");
        }

        return ds;
    }

    /// <summary>
    /// <b>R-gen-2's transform, stated once.</b> <c>Tv·diag(x)·Ti⁻¹</c>, assembled as
    /// <c>Σ_m (x_m/e_m)·Tv[i,m]·Tv[j,m]</c> so the result is bit-exactly symmetric — see
    /// <see cref="BuildGeneral"/>'s remarks on structural reciprocity.
    ///
    /// <para>Public because R-gen-3's normalisation-invariance gate drives it directly: it is the
    /// one place a wrong <c>Ti</c> would show, so the test that exists to catch that must exercise
    /// this function rather than a restatement of it.</para>
    /// </summary>
    public static Mat<Complex> ModalBlock(ModalDecomposition.GeneralModes modes, Complex[] x)
    {
        int n = modes.ModeCount;
        var r = new Mat<Complex>(n, n);
        for (int m = 0; m < n; m++)
        {
            var w = x[m] / modes.CurrentScale[m];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                r[i, j] += w * (modes.Tv[i, m] * modes.Tv[j, m]);
        }
        return r;
    }

    /// <summary>
    /// <b>R-gen-8's own open question, decided: keep <c>…Even</c>/<c>…Odd</c> as an ADDITIONAL alias
    /// for N = 2, sourced from the same arrays.</b> A coupled-line designer thinks in even and odd,
    /// every existing Data Display trace pointing at <c>tline.ZcEven</c> keeps working, and a second
    /// name for one number cannot drift. Silently breaking saved <c>.cdd</c> traces is the kind of
    /// change that costs a user their working plots, and it buys nothing.
    ///
    /// <para>The two modes are identified from the SIGN PATTERN of <c>Tv</c>'s columns, never from
    /// their position in the mode axis — the conductors move together in the even mode and in
    /// opposition in the odd one, whatever order R-gen-7's λ sort put them in. A pair whose pattern
    /// is not unambiguous publishes no alias rather than a guessed one.</para>
    /// </summary>
    private static void AddEvenOddAliases(
        DataSet ds, ModalDecomposition.GeneralModes modes, double[] freqsHz, int n,
        Complex[] zc, Complex[] gamma, double[] atten, double[] rpul, double[] gpul,
        double[] eeff, double[] lpul, double[] cpul)
    {
        if (!modes.TryIdentifyEvenOdd(out int even, out int odd)) return;

        int nf = freqsHz.Length;
        Axis[] Ax() => [new Axis("freq", freqsHz, "Hz")];

        Complex[] SliceC(Complex[] src, int m)
        {
            var v = new Complex[nf];
            for (int i = 0; i < nf; i++) v[i] = src[i * n + m];
            return v;
        }
        double[] SliceR(double[] src, int m)
        {
            var v = new double[nf];
            for (int i = 0; i < nf; i++) v[i] = src[i * n + m];
            return v;
        }

        void AliasC(string name, Complex[] src)
        {
            ds.AddToGroup("tline", name + "Even", new DataCube(Ax(), SliceC(src, even)));
            ds.AddToGroup("tline", name + "Odd",  new DataCube(Ax(), SliceC(src, odd)));
        }
        void AliasR(string name, double[] src)
        {
            ds.AddToGroup("tline", name + "Even", new DataCube(Ax(), SliceR(src, even)));
            ds.AddToGroup("tline", name + "Odd",  new DataCube(Ax(), SliceR(src, odd)));
        }

        AliasC("Zc", zc);
        AliasC("Gamma", gamma);
        AliasR("AttenDbPerM", atten);
        AliasR("Rpul", rpul);
        AliasR("Gpul", gpul);
        AliasR("Eeff", eeff);
        AliasR("Lpul", lpul);
        AliasR("Cpul", cpul);
    }

    /// <summary>√ on the branch with Re ≥ 0 — a propagation constant never has a negative
    /// attenuation, and Z_c of a passive line never has a negative resistance.</summary>
    internal static Complex PrincipalSqrt(Complex v)
    {
        var r = Complex.Sqrt(v);
        return r.Real < 0 ? -r : r;
    }
}
