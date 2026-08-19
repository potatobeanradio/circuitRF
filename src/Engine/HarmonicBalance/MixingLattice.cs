namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Diamond-truncated <b>T-tone</b> mixing lattice: enumerates the retained half-SPACE
/// representatives k = (k₁ … k_T) with Σ_t |k_t| ≤ MaxMixOrder (harmonic-balance.md §6.5).
///
/// <para>Enumeration order (LOCKED for T ≥ 3, never renumber):</para>
/// <list type="number">
///   <item>Ascending total order m = Σ_t |k_t|  →  DC (0,…,0) is index 0.</item>
///   <item>Within m: the half-space rule — k = 0, or the FIRST nonzero component is positive.</item>
///   <item>Within the half-space: lexicographic DESCENDING on the vector.</item>
/// </list>
/// Raising MaxMixOrder only appends indices — existing indices are unchanged, so a cube's
/// mixIndex axis is stable across an order change.
///
/// <para><b>At T = 2 this reproduces <see cref="MixingGrid"/>'s locked order element for
/// element</b> — its "k₁ descending, then k₂ descending within the upper half-plane" is exactly
/// lexicographic-descending under the half-space rule. That equivalence is pinned by
/// <c>MixingLatticeTests</c> and is what lets one class serve every tone count. Production still
/// dispatches T = 2 to <see cref="MixingGrid"/> / <see cref="HbFft2D"/> / <see cref="HbNewton2D"/>:
/// the two-tone path and its frozen goldens are deliberately untouched by the T ≥ 3 work.</para>
///
/// <para>Total retained count M = (L + 1) / 2 where L = Σ_j 2^j·C(T,j)·C(O,j) is the number of
/// lattice points in the diamond; <see cref="CountFor"/> evaluates M in closed form so the
/// analysis ceiling can be checked without enumerating anything.</para>
/// </summary>
public sealed class MixingLattice
{
    /// <summary>Largest per-component magnitude the packed index key can represent.</summary>
    private const int MaxComponent = 30;

    private readonly int[][]              _tones;  // mixIdx → k vector (length ToneCount)
    private readonly Dictionary<long,int> _idx;    // packed k → mixIdx

    /// <summary>Number of independent tones T.</summary>
    public int ToneCount   { get; }

    /// <summary>Diamond bound O: retained iff Σ_t |k_t| ≤ O.</summary>
    public int MaxMixOrder { get; }

    /// <summary>Number of retained mixing products (the diamond's half-space size M).</summary>
    public int MixCount => _tones.Length;

    public MixingLattice(int toneCount, int maxMixOrder)
    {
        if (toneCount   < 1) throw new ArgumentOutOfRangeException(nameof(toneCount));
        if (maxMixOrder < 0) throw new ArgumentOutOfRangeException(nameof(maxMixOrder));
        if (maxMixOrder > MaxComponent)
            throw new ArgumentOutOfRangeException(nameof(maxMixOrder),
                $"MaxMixOrder={maxMixOrder} exceeds the lattice's representable limit {MaxComponent}.");

        ToneCount   = toneCount;
        MaxMixOrder = maxMixOrder;

        var list = new List<int[]>(CountFor(toneCount, maxMixOrder));
        var work = new int[toneCount];
        for (int m = 0; m <= maxMixOrder; m++)
            Enumerate(list, work, position: 0, remaining: m);

        _tones = list.ToArray();
        _idx   = new Dictionary<long,int>(_tones.Length);
        for (int i = 0; i < _tones.Length; i++)
            _idx[Pack(_tones[i])] = i;
    }

    /// <summary>
    /// Emit every k with Σ|k| exactly <paramref name="remaining"/> at or after
    /// <paramref name="position"/>, in lexicographic-descending order, keeping only half-space
    /// representatives. Iterating each component from +remaining down to −remaining IS the
    /// descending lexicographic walk, so no sort is needed.
    /// </summary>
    private static void Enumerate(List<int[]> sink, int[] work, int position, int remaining)
    {
        int last = work.Length - 1;
        if (position == last)
        {
            // Last component is forced to ±remaining (or 0) to hit the exact total order.
            if (remaining == 0) { work[last] = 0; if (IsHalfSpace(work)) sink.Add((int[])work.Clone()); return; }
            work[last] = remaining;
            if (IsHalfSpace(work)) sink.Add((int[])work.Clone());
            work[last] = -remaining;
            if (IsHalfSpace(work)) sink.Add((int[])work.Clone());
            work[last] = 0;
            return;
        }

        for (int v = remaining; v >= -remaining; v--)
        {
            work[position] = v;
            Enumerate(sink, work, position + 1, remaining - Math.Abs(v));
        }
        work[position] = 0;
    }

    /// <summary>
    /// The half-space rule: k = 0, or the first nonzero component is positive. Exactly one of
    /// {k, −k} satisfies it, so the retained set carries the full information for a real signal
    /// (the excluded partner is the conjugate).
    /// </summary>
    private static bool IsHalfSpace(int[] k)
    {
        for (int t = 0; t < k.Length; t++)
        {
            if (k[t] > 0) return true;
            if (k[t] < 0) return false;
        }
        return true;   // k = 0 (DC)
    }

    // ── Accessors ─────────────────────────────────────────────────────────────

    /// <summary>The k vector for a mix index. The returned array is the lattice's own — do not mutate.</summary>
    public int[] ToneOf(int mixIdx) => _tones[mixIdx];

    /// <summary>Mix index for k, or −1 if it is not a retained half-space representative.</summary>
    public int IndexOf(ReadOnlySpan<int> k)
    {
        if (k.Length != ToneCount) return -1;
        for (int t = 0; t < k.Length; t++)
            if (Math.Abs(k[t]) > MaxComponent) return -1;
        return _idx.TryGetValue(Pack(k), out int idx) ? idx : -1;
    }

    /// <summary>Total mixing order Σ_t |k_t| of a mix index.</summary>
    public int OrderOf(int mixIdx)
    {
        var k = _tones[mixIdx];
        int s = 0;
        for (int t = 0; t < k.Length; t++) s += Math.Abs(k[t]);
        return s;
    }

    /// <summary>Signed angular frequency (rad/s): ω = Σ_t k_t·ω_t. May be negative.</summary>
    public double OmegaOf(int mixIdx, double[] omegas)
    {
        var k = _tones[mixIdx];
        double w = 0;
        for (int t = 0; t < k.Length; t++) w += k[t] * omegas[t];
        return w;
    }

    /// <summary>Signed physical frequency (Hz): f = Σ_t k_t·f_t. May be negative.</summary>
    public double FrequencyOf(int mixIdx, double[] toneFreqsHz)
    {
        var k = _tones[mixIdx];
        double f = 0;
        for (int t = 0; t < k.Length; t++) f += k[t] * toneFreqsHz[t];
        return f;
    }

    /// <summary>
    /// The mixIndex axis label for a product: "(k1,k2,…,kT)". This is the tag the data display
    /// renders verbatim and the measurement language matches on — e.g. <c>V("Vout","(2,-1,0)")</c>.
    /// </summary>
    public string Label(int mixIdx) => Label(_tones[mixIdx]);

    /// <summary>Label form of an arbitrary k vector (retained or not).</summary>
    public static string Label(ReadOnlySpan<int> k)
    {
        var sb = new System.Text.StringBuilder(2 + 3 * k.Length);
        sb.Append('(');
        for (int t = 0; t < k.Length; t++)
        {
            if (t > 0) sb.Append(',');
            sb.Append(k[t].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Enumerate all retained k vectors in mixIndex order.</summary>
    public IEnumerable<int[]> All()
    {
        foreach (var t in _tones) yield return t;
    }

    // ── Closed-form size, for the analysis ceiling ─────────────────────────────

    /// <summary>
    /// Retained-product count M for T tones at diamond order O, WITHOUT enumerating the lattice —
    /// so the analysis ceiling can be checked at setup time for free.
    ///
    /// <para>The number of integer points with Σ|k| ≤ O in Z^T is L = Σ_j 2^j·C(T,j)·C(O,j)
    /// (choose which j components are nonzero, their signs, and a positive composition of the
    /// budget across them). The half-space keeps one of each ±pair plus DC: M = (L+1)/2.</para>
    ///
    /// <para>Saturates at <see cref="int.MaxValue"/> rather than overflowing, so an absurd
    /// (T, O) still compares correctly against a cap instead of wrapping negative.</para>
    /// </summary>
    public static int CountFor(int toneCount, int maxMixOrder)
    {
        if (toneCount < 1 || maxMixOrder < 0) return 0;

        long total = 0;
        int jMax = Math.Min(toneCount, maxMixOrder);
        for (int j = 0; j <= jMax; j++)
        {
            double term = Math.Pow(2, j) * Binomial(toneCount, j) * Binomial(maxMixOrder, j);
            if (term > int.MaxValue) return int.MaxValue;
            total += (long)term;
            if (total > int.MaxValue) return int.MaxValue;
        }
        return (int)((total + 1) / 2);
    }

    /// <summary>C(n,k) in double, which is exact well past any (T,O) this engine admits.</summary>
    private static double Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        double r = 1;
        for (int i = 0; i < k; i++) r = r * (n - i) / (i + 1);
        return Math.Round(r);
    }

    // ── Index packing ─────────────────────────────────────────────────────────

    // 6 bits per component (offset by MaxComponent+1), T ≤ 10 fits in 60 bits.
    private static long Pack(ReadOnlySpan<int> k)
    {
        long key = 0;
        for (int t = 0; t < k.Length; t++)
            key = (key << 6) | (uint)(k[t] + MaxComponent + 1);
        return key;
    }
}
