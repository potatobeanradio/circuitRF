// ================================================================
//  SnpPerturbation.cs  —  dB / degree offsets on the entries of an
//                         S-parameter block (brief-sens-2)
//
//  S′(i,j) = S(i,j) · 10^(δdB/20) · e^{j·δdeg·π/180}, applied to the
//  matrix AFTER interpolation, so the offset is uniform over the band.
//  At 0 Hz only the magnitude offset applies: a DC S-parameter is real,
//  and a phase there would make the block's DC solution complex.
//
//  The zero-offset path returns its input — the same instance, no copy —
//  so a block with no offsets stamps exactly the bits it always did.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using NumFlat;

namespace RfCore
{
    /// <summary>How an SnP block's offsets treat S(i,j) and S(j,i) (brief-sens-2 R-sens2-3).</summary>
    public enum SnpReciprocal
    {
        /// <summary>Decided by the file: tied when its worst |S(i,j) − S(j,i)| is within
        /// <see cref="SnpPerturbation.ReciprocalTolerance"/>.</summary>
        Auto,
        /// <summary>One offset (stated on the lower triangle, i ≥ j) moves S(i,j) and S(j,i) together.</summary>
        Tied,
        /// <summary>Every one of the N² entries has its own offset.</summary>
        Untied,
    }

    /// <summary>Whether an entry offset is on the magnitude (dB) or the phase (degrees).</summary>
    public enum SnpOffsetKind { Db, Deg }

    /// <summary>The worst non-reciprocity of a file: the largest |S(i,j) − S(j,i)| over its own frequency points and
    /// every pair, with the pair (1-based, i &gt; j) and the frequency where it occurs. A 1-port, or a file with no
    /// S, is 0 at no pair.</summary>
    public readonly record struct SnpReciprocity(double Worst, int I, int J, double Hz, double WorstDb);

    /// <summary>
    /// One SnP block's entry offsets — N×N magnitude offsets in dB and phase offsets in degrees, already expanded over
    /// the tie (a tied offset is written to both (i,j) and (j,i)) — and the one function that applies them.
    /// </summary>
    public sealed class SnpOffsets
    {
        private readonly double[,] _db;
        private readonly double[,] _deg;

        public SnpOffsets(int ports)
        {
            Ports = ports;
            _db = new double[ports, ports];
            _deg = new double[ports, ports];
        }

        public int Ports { get; }

        /// <summary>The magnitude offset of S(i,j), dB (0-based).</summary>
        public double Db(int i, int j) => _db[i, j];

        /// <summary>The phase offset of S(i,j), degrees (0-based).</summary>
        public double Deg(int i, int j) => _deg[i, j];

        /// <summary>Sets one entry's offset (0-based).</summary>
        public void Set(SnpOffsetKind kind, int i, int j, double value)
        {
            if (kind == SnpOffsetKind.Db) _db[i, j] = value; else _deg[i, j] = value;
        }

        /// <summary>True when any offset is nonzero.</summary>
        public bool Any
        {
            get
            {
                for (int i = 0; i < Ports; i++)
                    for (int j = 0; j < Ports; j++)
                        if (_db[i, j] != 0 || _deg[i, j] != 0) return true;
                return false;
            }
        }

        /// <summary>True when any PHASE offset is nonzero.</summary>
        public bool AnyPhase
        {
            get
            {
                for (int i = 0; i < Ports; i++)
                    for (int j = 0; j < Ports; j++)
                        if (_deg[i, j] != 0) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// The S-parameter entry offsets of an SnP block (brief-sens-2): their parameter names, the reciprocity rule that
    /// ties S(i,j) to S(j,i), and <see cref="Apply"/> — the one place the offsets touch a matrix.
    /// </summary>
    public static class SnpPerturbation
    {
        /// <summary>The magnitude offset's parameter prefix: <c>SdB_2_1</c>.</summary>
        public const string DbPrefix = "SdB_";

        /// <summary>The phase offset's parameter prefix: <c>Sdeg_2_1</c>.</summary>
        public const string DegPrefix = "Sdeg_";

        /// <summary>The reciprocity switch: <c>Reciprocal=auto|tied|untied</c>.</summary>
        public const string ReciprocalParameter = "Reciprocal";

        /// <summary>
        /// The worst |S(i,j) − S(j,i)| (absolute, linear) at or below which <c>Reciprocal=auto</c> resolves to tied.
        /// Measured against all 166 Touchstone files under examples/ and testdata/, 3D-EM results among them: every
        /// passive one sits at or below 5.5e-3 and every active one (amplifiers, FET bias points) at 0.117 and above
        /// (src/RfCore/RESOLVED.md, brief-sens-2).
        /// </summary>
        public const double ReciprocalTolerance = 0.01;

        /// <summary>The parameter name of one entry's offset: <c>SdB_10_1</c> (1-based, always separated by <c>_</c>).</summary>
        public static string ParameterName(SnpOffsetKind kind, int i, int j)
            => (kind == SnpOffsetKind.Db ? DbPrefix : DegPrefix)
               + i.ToString(CultureInfo.InvariantCulture) + "_" + j.ToString(CultureInfo.InvariantCulture);

        /// <summary>Whether a parameter name is an entry offset, and which (1-based ports).</summary>
        public static bool TryParseName(string name, out SnpOffsetKind kind, out int i, out int j)
        {
            kind = SnpOffsetKind.Db; i = j = 0;
            string rest;
            if (name.StartsWith(DbPrefix, StringComparison.Ordinal)) { kind = SnpOffsetKind.Db; rest = name[DbPrefix.Length..]; }
            else if (name.StartsWith(DegPrefix, StringComparison.Ordinal)) { kind = SnpOffsetKind.Deg; rest = name[DegPrefix.Length..]; }
            else return false;
            int us = rest.IndexOf('_');
            if (us <= 0 || us == rest.Length - 1) return false;
            return int.TryParse(rest[..us], NumberStyles.None, CultureInfo.InvariantCulture, out i)
                && int.TryParse(rest[(us + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out j)
                && i >= 1 && j >= 1;
        }

        /// <summary>True when a parameter name is an entry offset or the reciprocity switch.</summary>
        public static bool IsPerturbationParameter(string name)
            => TryParseName(name, out _, out _, out _) || name.Equals(ReciprocalParameter, StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads <c>auto|tied|untied</c>; null for anything else.</summary>
        public static SnpReciprocal? ParseReciprocal(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => SnpReciprocal.Auto,
            "tied"               => SnpReciprocal.Tied,
            "untied"             => SnpReciprocal.Untied,
            _                    => null,
        };

        /// <summary>
        /// Applies the offsets to S at <paramref name="hz"/>. With no offsets (null, or all zero) it returns
        /// <paramref name="s"/> itself — the same storage, nothing copied. At 0 Hz only the magnitude offset applies.
        /// </summary>
        public static Mat<Complex> Apply(Mat<Complex> s, SnpOffsets? offsets, double hz)
        {
            if (offsets is null || !offsets.Any) return s;
            int n = Math.Min(offsets.Ports, Math.Min(s.RowCount, s.ColCount));
            var r = new Mat<Complex>(s.RowCount, s.ColCount);
            for (int i = 0; i < s.RowCount; i++)
                for (int j = 0; j < s.ColCount; j++)
                    r[i, j] = s[i, j];
            bool dc = hz == 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double db = offsets.Db(i, j), deg = dc ? 0 : offsets.Deg(i, j);
                    if (db == 0 && deg == 0) continue;
                    double mag = Math.Pow(10, db / 20);
                    r[i, j] = s[i, j] * Complex.FromPolarCoordinates(mag, deg * Math.PI / 180);
                }
            return r;
        }

        /// <summary>
        /// The file with its offsets applied at each of its own frequency points, as S — what an activity test measures
        /// (brief-sens-2 R-sens2-2). Null offsets (or all zero) return the file itself.
        /// </summary>
        public static SNP ApplyToFile(SNP snp, SnpOffsets? offsets)
        {
            if (offsets is null || !offsets.Any) return snp;
            var s = snp.Type switch
            {
                MatrixType.Z => RFNetwork.ZToS(snp),
                MatrixType.Y => RFNetwork.YToS(snp),
                _            => snp,
            };
            var m = new Mat<Complex>[s.FrequencyCount];
            for (int k = 0; k < m.Length; k++) m[k] = Apply(s.Matrices[k], offsets, s.Frequencies[k]);
            return new SNP(s.Frequencies, m, MatrixType.S, s.Format, s.Z0);
        }

        /// <summary>
        /// Resolves <c>Reciprocal=auto</c> against the file: tied when its worst |S(i,j) − S(j,i)| is within
        /// <see cref="ReciprocalTolerance"/>. Also returns the measurement, for the reason a tie state states.
        /// </summary>
        public static (bool Tied, SnpReciprocity Measured) ResolveTie(SNP snp, SnpReciprocal mode)
        {
            var rec = RFNetwork.Reciprocity(snp);
            bool tied = mode switch
            {
                SnpReciprocal.Tied   => true,
                SnpReciprocal.Untied => false,
                _                    => rec.Worst <= ReciprocalTolerance,
            };
            return (tied, rec);
        }

        /// <summary>
        /// The offsets an instance states, expanded over the tie. Every stated name must be inside the port count and,
        /// on a tied block, on the lower triangle (i ≥ j); <paramref name="refusal"/> names the first that is not.
        /// </summary>
        /// <param name="stated">(parameter name, value) — dB for <c>SdB_</c>, degrees for <c>Sdeg_</c>.</param>
        public static SnpOffsets? Build(int ports, bool tied, IEnumerable<(string Name, double Value)> stated, out string? refusal)
        {
            refusal = null;
            SnpOffsets? o = null;
            foreach (var (name, value) in stated)
            {
                if (!TryParseName(name, out var kind, out int i, out int j)) continue;
                if (i > ports || j > ports)
                {
                    refusal = $"{name} names port {Math.Max(i, j)}, and the block has {ports} port(s)";
                    return null;
                }
                if (tied && i < j)
                {
                    refusal = $"{name} is on the upper triangle of a tied block — state {ParameterName(kind, j, i)} " +
                              $"(it moves S{j}{Sep(ports)}{i} and S{i}{Sep(ports)}{j} together), or set {ReciprocalParameter}=untied";
                    return null;
                }
                if (value == 0) continue;
                o ??= new SnpOffsets(ports);
                o.Set(kind, i - 1, j - 1, value);
                if (tied && i != j) o.Set(kind, j - 1, i - 1, value);
            }
            return o;
        }

        private static string Sep(int ports) => ports > 9 ? "," : "";
    }
}
