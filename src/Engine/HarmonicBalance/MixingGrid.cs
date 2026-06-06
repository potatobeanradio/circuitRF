namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Diamond-truncated two-tone mixing lattice: enumerates the retained half-plane
/// representatives (k₁,k₂) with |k₁|+|k₂| ≤ MaxMixOrder.
///
/// Enumeration order (harmonic-balance.md §16 item 1 — LOCKED, never renumber):
///   1. Ascending total order m = |k₁|+|k₂|  →  DC (0,0) is index 0.
///   2. Within m: upper-half-plane rule  (k₁ > 0)  OR  (k₁ = 0 ∧ k₂ ≥ 0).
///   3. Within the half-plane: k₁ descending, then k₂ descending.
///
/// Raising MaxMixOrder only appends indices — existing indices are unchanged.
/// The measurement library's tone()/IMn() depend on this order.
///
/// Total retained count M = 1 + MaxMixOrder*(MaxMixOrder+1).
/// </summary>
public sealed class MixingGrid
{
    private readonly (int k1, int k2)[]      _tones; // mixIdx → (k1,k2)
    private readonly Dictionary<(int,int),int> _idx;  // (k1,k2) → mixIdx

    public int MaxMixOrder { get; }

    /// <summary>Number of retained mixing products (the diamond size M).</summary>
    public int MixCount => _tones.Length;

    public MixingGrid(int maxMixOrder)
    {
        if (maxMixOrder < 0) throw new ArgumentOutOfRangeException(nameof(maxMixOrder));
        MaxMixOrder = maxMixOrder;

        int capacity = 1 + maxMixOrder * (maxMixOrder + 1);
        var list = new List<(int, int)>(capacity);

        for (int m = 0; m <= maxMixOrder; m++)
        {
            // Enumerate half-plane representatives at total order m,
            // sorted: k1 descending, within each k1 by k2 descending.
            for (int k1 = m; k1 >= 0; k1--)
            {
                int absK2 = m - k1;
                if (k1 > 0)
                {
                    // k1 > 0 → both +absK2 and −absK2 are in the upper half-plane.
                    // k2 descending: positive first (unless absK2 == 0).
                    if (absK2 == 0)
                        list.Add((k1, 0));
                    else
                    {
                        list.Add((k1,  absK2));
                        list.Add((k1, -absK2));
                    }
                }
                else
                {
                    // k1 == 0 → only k2 ≥ 0 is in the half-plane.
                    list.Add((0, absK2));
                    // (0, -absK2) for absK2 > 0 is the conjugate: excluded.
                }
            }
        }

        _tones = list.ToArray();
        _idx   = new Dictionary<(int,int),int>(_tones.Length);
        for (int i = 0; i < _tones.Length; i++)
            _idx[_tones[i]] = i;
    }

    // ── Accessors ─────────────────────────────────────────────────────────────

    /// <summary>Returns (k₁, k₂) for the given mix index.</summary>
    public (int k1, int k2) ToneOf(int mixIdx) => _tones[mixIdx];

    /// <summary>
    /// Returns the mix index for (k₁, k₂), or −1 if not in the retained half-plane set.
    /// </summary>
    public int IndexOf(int k1, int k2) =>
        _idx.TryGetValue((k1, k2), out int idx) ? idx : -1;

    /// <summary>
    /// Physical angular frequency (rad/s) of mix index, signed:
    ///   ω = k₁·ω₁ + k₂·ω₂
    /// May be negative (e.g. (1,−1) when f₁ &lt; f₂).
    /// </summary>
    public double OmegaOf(int mixIdx, double omega1, double omega2)
    {
        var (k1, k2) = _tones[mixIdx];
        return k1 * omega1 + k2 * omega2;
    }

    /// <summary>Physical frequency (Hz) of mix index, signed.</summary>
    public double FrequencyOf(int mixIdx, double f1, double f2)
    {
        var (k1, k2) = _tones[mixIdx];
        return k1 * f1 + k2 * f2;
    }

    /// <summary>
    /// Whether a rectangular-grid index (k1,k2) with k1 in [0..N1/2], k2 in [−N2/2..N2/2]
    /// is in the half-plane retained set (not just whether it is within the diamond).
    /// </summary>
    public bool IsRetained(int k1, int k2) => _idx.ContainsKey((k1, k2));

    /// <summary>Enumerate all retained (k₁, k₂) pairs in mixIndex order.</summary>
    public IEnumerable<(int k1, int k2)> All()
    {
        foreach (var t in _tones) yield return t;
    }
}
