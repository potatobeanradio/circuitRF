namespace CircuitRF.Core.Matching;

/// <summary>
/// A transformable pair: two like-kind elements of opposite orientation, at most three positions
/// apart, with the moves that will make them adjacent.
/// </summary>
/// <param name="IndexA">Position of the first element in the network's element list.</param>
/// <param name="IndexB">Position of the second.</param>
/// <param name="NameA">The first element's name — how a <see cref="TransformRecord"/> refers to it.</param>
/// <param name="NameB">The second element's name.</param>
/// <param name="MoveA">Where <paramref name="IndexA"/> ends up, as an offset.</param>
/// <param name="MoveB">Where <paramref name="IndexB"/> ends up, as an offset.</param>
public sealed record TransformPair(
    int IndexA, int IndexB, string NameA, string NameB, int MoveA, int MoveB);

/// <summary>The positivity-bounded slider range for one pair, at one moment.</summary>
/// <param name="Min">Lowest usable N.</param>
/// <param name="Max">Highest usable N.</param>
/// <param name="PropagateRight">Which side of the pair the N^2 scaling lands on.</param>
/// <param name="Threshold">The raw positivity threshold, before the strictly-inside clamp.</param>
/// <param name="NGreaterThanOne">True when this pair steps up.</param>
public sealed record TransformRange(
    double Min, double Max, bool PropagateRight, double Threshold, bool NGreaterThanOne)
{
    /// <summary>False when the clamped bounds crossed — the pair is then unusable, not repairable.</summary>
    public bool IsUsable => Min < Max;

    /// <summary>Brings a candidate N inside the range.</summary>
    public double Clamp(double n) => Math.Min(Math.Max(n, Min), Max);
}

/// <summary>What applying one transform produced.</summary>
/// <param name="Network">The new network.</param>
/// <param name="Position">Where the three products were inserted.</param>
/// <param name="Range">The range that was in force, recomputed from the elements as they stood.</param>
/// <param name="NUsed">The N actually applied, after clamping.</param>
/// <param name="Clamped">True when the requested N was outside the range.</param>
/// <param name="GuardFired">True when an absolute value guard had to act — see the remarks on
/// <see cref="NortonTransform.Apply"/>.</param>
public sealed record TransformApplication(
    MatchNetwork Network, int Position, TransformRange Range, double NUsed, bool Clamped, bool GuardFired);

/// <summary>
/// match.md §4.7: the element-value degrees of freedom. A Norton transform replaces an L-section of
/// two like-kind elements with a pi or T of three, plus an ideal transformer of ratio N which is then
/// absorbed by scaling everything on one side. <b>The two-port's transfer function is unchanged</b> —
/// only the element values and the terminating resistance move.
/// </summary>
public static class NortonTransform
{
    /// <summary>How far inside its positivity threshold N is kept; at the threshold a product is infinite.</summary>
    public const double ThresholdMargin = 1e-9;

    /// <summary>
    /// How far off <b>unity</b> N is kept. N = 1 is an identity transformer, and every one of the four
    /// product formulae divides by (N − 1) or multiplies by it: pi produces an infinite element there
    /// and T a zero one, which for a capacitor pair inverts to infinite as well.
    /// </summary>
    /// <remarks>
    /// <b>Unity is a pole, exactly like the positivity threshold, and it was not excluded</b>
    /// (owner-reported, 2026-08-19: <i>"if slider N1 goes to 1, the plots all fail"</i>). The range's
    /// unity end was the bare 1.0, so the slider could be dragged onto the pole itself and the ladder
    /// then carried a non-finite element — which is not a bad value the response engine can plot and
    /// flag, it is no response at all. Both ends of the range are now open intervals, by the same
    /// margin and for the same reason. Nothing else changes: at 1 ± 1e-9 the products are enormous
    /// but FINITE, the transfer function is still exactly invariant (a near-identity transformer is
    /// a near-open shunt), and the absolute guards report the element as out of range in red — which
    /// is the designed behaviour for an N parked on a bound, not a repair.
    /// </remarks>
    public const double UnityMargin = 1e-9;

    /// <summary>Last-resort absolute guard: no inductance above this, in henries.</summary>
    public const double GuardMaxValue = 1.0;

    /// <summary>Last-resort absolute guard: nothing below this, in henries or farads.</summary>
    public const double GuardMinValue = 1e-24;

    /// <summary>
    /// Every transformable pair in the ladder (match.md §4.7's structural scan).
    /// </summary>
    /// <remarks>
    /// <b>The absorbed-element rule here is the general one, not the reference implementation's.</b>
    /// The reference allows a pair whenever its type is L and otherwise forbids either index from
    /// being absorbed — which is correct only because in that implementation the absorbed element is
    /// always a capacitor. With the inductive terminations of match.md §5 that is false, and the rule
    /// has to key on the absorbed element's own type:
    /// <c>movable(a,b) := type(a) is not an absorbed type, OR neither a nor b is absorbed</c>.
    /// </remarks>
    public static IReadOnlyList<TransformPair> Discover(MatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var el = network.Elements;
        int m = el.Count;
        var absorbedTypes = el.Where(e => e.IsAbsorbed).Select(e => e.Type).ToHashSet();

        bool Movable(int a, int b) =>
            !absorbedTypes.Contains(el[a].Type) || (!el[a].IsAbsorbed && !el[b].IsAbsorbed);
        bool Opposite(int a, int b) => el[a].IsShunt != el[b].IsShunt;

        var seen = new HashSet<(int, int)>();
        var found = new List<TransformPair>();

        void Consider(int a, int b, int moveA, int moveB, bool extra)
        {
            if (a < 0 || b >= m || !extra) return;
            if (el[a].Type != el[b].Type || !Opposite(a, b) || !Movable(a, b)) return;
            if (!seen.Add((a, b))) return;
            found.Add(new TransformPair(a, b, el[a].Name, el[b].Name, moveA, moveB));
        }

        for (int j = 0; j + 3 <= m - 1; j++)
        {
            // (j, j+2): whichever of the two ends of the gap shares an orientation with its
            // neighbour is the one that can be walked across without changing the circuit.
            var mv2 = Opposite(j, j + 1) ? (0, -1) : (1, 0);
            Consider(j, j + 2, mv2.Item1, mv2.Item2, true);

            // (j, j+3): only when what lies between them matches, so both walks stay inside an arm.
            Consider(j, j + 3, 1, -1, el[j + 1].Type == el[j + 2].Type);

            Consider(j + 1, j + 2, 0, 0, true);

            var mv4 = Opposite(j + 1, j + 2) ? (0, -1) : (1, 0);
            Consider(j + 1, j + 3, mv4.Item1, mv4.Item2, true);
        }

        return found;
    }

    /// <summary>
    /// True when two pairs would need the same element, before or after their moves — so the solution
    /// search never proposes both.
    /// </summary>
    public static bool Conflicts(TransformPair a, TransformPair b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Raw(a, b) || Raw(b, a);

        static bool Raw(TransformPair x, TransformPair y)
        {
            if (x.IndexA == y.IndexB || x.IndexB == y.IndexA) return true;
            int xLast = x.IndexB + x.MoveB, yFirst = y.IndexA + y.MoveA, yLast = y.IndexB + y.MoveB;
            return xLast == yFirst || xLast == yLast;
        }
    }

    /// <summary>
    /// The pair's usable N range, <b>recomputed from the element values as they stand right now</b>.
    /// </summary>
    /// <remarks>
    /// Never cache or persist this. The threshold depends on the values at the moment the transform
    /// is applied, which depends on every earlier transform; a stored bound goes stale against the
    /// elements it bounds, and a stale bound silently permits a negative element.
    /// </remarks>
    public static TransformRange Range(
        MatchNetwork network, TransformPair pair, bool analysisIsTerm1, bool allowNegative)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(pair);
        var el = network.Elements;

        bool firstIsShunt = el[pair.IndexA].IsShunt;
        bool nGreater = (firstIsShunt && !analysisIsTerm1) || (!firstIsShunt && analysisIsTerm1);
        bool propagateRight = (nGreater && !firstIsShunt) || (!nGreater && firstIsShunt);

        var series = firstIsShunt ? el[pair.IndexB] : el[pair.IndexA];
        var shunt = firstIsShunt ? el[pair.IndexA] : el[pair.IndexB];
        double z1 = series.Value, z2 = shunt.Value;
        if (series.Type == ElementType.C) { z1 = 1.0 / z1; z2 = 1.0 / z2; }

        double threshold = nGreater ? 1.0 + z1 / z2 : z2 / (z1 + z2);

        // Strictly OFF unity, at both ends: see UnityMargin. A step-up pair may not reach 1 from
        // above, a step-down pair may not reach it from below, and neither may sit on it.
        double up = 1.0 + UnityMargin, down = 1.0 - UnityMargin;

        double min, max;
        if (allowNegative)
        {
            (min, max) = nGreater ? (up, 10.0) : (1e-3, down);
        }
        else
        {
            // Strictly inside: exactly at the threshold one of the three products is infinite.
            double inside = 1.0 + (threshold - 1.0) * (1.0 - ThresholdMargin);
            (min, max) = nGreater ? (up, inside) : (inside, down);
        }

        return new TransformRange(min, max, propagateRight, threshold, nGreater);
    }

    /// <summary>
    /// Applies one transform: make the pair adjacent, replace it with the pi or T of three, then
    /// scale everything on the propagation side by N^2.
    /// </summary>
    /// <remarks>
    /// <b>On the absolute guards, which REPORT rather than repair.</b> The reference implementation
    /// clamps any produced value above 1 H / 1 F to 1.0 and anything below 1e-24 to 0.0 — absolute
    /// guards in SI units, against the infinities at the positivity threshold. Clamping is not kept:
    /// rewriting a value is exactly the thing that breaks the one invariant this whole mechanism
    /// rests on, so a network that has been "repaired" no longer has the response it claims. The
    /// condition is kept as a last-resort assert — <see cref="TransformApplication.GuardFired"/> —
    /// and the value is left alone.
    ///
    /// <para>It is reachable, and knowing when matters: N held one part in 1e9 inside its threshold
    /// is still one part in 1e9 from a pole, so a solver that parks N on the bound produces a
    /// mathematically exact but absurd element (2.9 kH on the design doc's own problem). That is a
    /// bad N allocation, not a bad transform — see <c>MatchSolutionSearch</c>'s seeding.</para>
    /// </remarks>
    public static TransformApplication Apply(
        MatchNetwork network, TransformPair pair, double n, TransformForm form,
        bool analysisIsTerm1, bool allowNegative, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(pair);

        var range = Range(network, pair, analysisIsTerm1, allowNegative);
        double used = range.Clamp(n);
        bool clamped = Math.Abs(used - n) > 0.0;

        var net = network.Clone();
        var el = net.Elements;
        int i = pair.IndexA, j = pair.IndexB;

        // A. Make the pair adjacent. Every swap here is between two elements of the SAME
        //    orientation, which is what makes it a re-ordering rather than a different circuit -
        //    nets are derived from list order, so a shunt/series swap would move a branch point.
        int gap = j - i;
        if (gap == 2)
        {
            if (el[i].IsShunt == el[i + 1].IsShunt) { Swap(el, i, i + 1); i++; }
            else { RequireSameOrientation(el, j - 1, j); Swap(el, j - 1, j); j--; }
        }
        else if (gap == 3)
        {
            RequireSameOrientation(el, i, i + 1);
            RequireSameOrientation(el, j - 1, j);
            Swap(el, i, i + 1); i++;
            Swap(el, j - 1, j); j--;
        }
        else if (gap != 1)
        {
            throw new InvalidOperationException($"A transform pair is {gap} apart; only 1..3 is reachable.");
        }

        if (j != i + 1)
            throw new InvalidOperationException("The pair did not become adjacent.");

        // B. Identify the L-section: the one NOT connected to ground is the series impedance.
        var e1 = el[i];
        var e2 = el[j];
        bool firstIsShunt = e1.IsShunt;
        var series = firstIsShunt ? e2 : e1;
        var shunt = firstIsShunt ? e1 : e2;
        ElementType type = e1.Type;
        double z1 = series.Value, z2 = shunt.Value;
        if (type == ElementType.C) { z1 = 1.0 / z1; z2 = 1.0 / z2; }

        // C. The three new values, all of the pair's own type.
        double nn = used;
        double[] v = (form, nn > 1.0) switch
        {
            (TransformForm.Pi, true) =>
                [nn * z1 / (nn - 1), nn * z1, nn * nn * z1 * z2 / (z1 + (1 - nn) * z2)],
            (TransformForm.Pi, false) =>
                [nn * nn * z1 / (1 - nn), nn * z1, nn * z1 * z2 / (nn * z1 + (nn - 1) * z2)],
            (TransformForm.T, true) =>
                [z1 + (1 - nn) * z2, nn * z2, nn * (nn - 1) * z2],
            _ =>
                [nn * nn * (z1 + z2) - nn * z2, nn * z2, (1 - nn) * z2],
        };
        if (type == ElementType.C) v = [1.0 / v[0], 1.0 / v[1], 1.0 / v[2]];
        if (firstIsShunt) (v[0], v[2]) = (v[2], v[0]);

        // Report, do not repair: see the remarks above.
        bool guard = v.Any(x => Math.Abs(x) > GuardMaxValue || Math.Abs(x) < GuardMinValue);

        // D. pi is shunt-series-shunt; T is series-shunt-series. Deriving nets from the list makes
        //    that the whole of the net assignment: pi hangs off a, spans a-b, hangs off b; T spans
        //    a-t, hangs off t, spans t-b with t minted by the walk.
        bool[] orientation = form == TransformForm.Pi ? [true, false, true] : [false, true, false];
        var products = new MatchElement[3];
        for (int k = 0; k < 3; k++)
            products[k] = new MatchElement
            {
                Name = $"{pair.NameA}_N{ordinal}_{k + 1}",
                Type = type,
                IsShunt = orientation[k],
                Value = v[k],
            };

        int pos = i;
        el.RemoveRange(pos, 2);
        el.InsertRange(pos, products);

        // E. Absorb the ideal transformer into everything on the propagation side.
        double n2 = used * used;
        if (range.PropagateRight)
        {
            for (int k = pos + 3; k < el.Count; k++) ScaleElement(el[k], n2);
            net.R2 *= n2;
        }
        else
        {
            for (int k = 0; k < pos; k++) ScaleElement(el[k], n2);
            net.R1 *= n2;
        }

        return new TransformApplication(net, pos, range, used, clamped, guard);
    }

    private static void ScaleElement(MatchElement e, double n2) =>
        e.Value = e.Type == ElementType.L ? e.Value * n2 : e.Value / n2;

    private static void Swap(List<MatchElement> el, int a, int b) => (el[a], el[b]) = (el[b], el[a]);

    private static void RequireSameOrientation(List<MatchElement> el, int a, int b)
    {
        if (el[a].IsShunt != el[b].IsShunt)
            throw new InvalidOperationException(
                $"Making a transform pair adjacent would swap {el[a].Name} past {el[b].Name}, which " +
                "have different orientations - that is a different circuit, not a re-ordering.");
    }
}
