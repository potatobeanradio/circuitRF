// §4.1 with ω ≠ 0, and §4.3's mounting loop — the arithmetic, in ONE place
// (docs/sonnet-briefs/brief-railrf-13-distributed.md R-rail13-1 … R-rail13-5, railrf.md §4.1, §4.3).
//
// ── WHY ONE FILE ───────────────────────────────────────────────────────────────────────────────
//
// Three callers need these expressions: the mesh (one L per cell edge), the fast graph (one loop
// inductance per trace section) and the mounting loop (partial self and mutual inductance of a via
// pair). §4.6's claim that the fast model "is not a second simulator, it is a second reading of the
// geometry" is only true while the two readings price a square of copper identically — so the
// square is priced HERE and both callers multiply it by their own count of squares.
//
// ── THE FACTOR OF TWO, ONE LAYER DOWN FROM BRIEF 3'S ───────────────────────────────────────────
//
// PdnMeshExtractor's header explains why the LOOP's resistance is 2·Rs and why meshing both
// conductors is what pays it. This file is where the second half of that sentence — §4.1's
// "Rs = √(π·f·µ/σ) → ρ/T below two skin depths" — had to be resolved, because AS WRITTEN the design
// note's two statements about the same crossover disagree with each other by a factor of four in
// frequency:
//
//   Read Rs = √(πfµ/σ) as ONE PLANE's sheet resistance and it equals ρ/δ. That crosses the DC value
//   ρ/T at δ = T, which is 3.6 MHz on 1 oz copper — not the 14 MHz §2.8 tabulates, and at §2.8's own
//   14 MHz the skin expression is already TWICE the DC one. A piecewise rule built on those two
//   sentences literally puts a factor-of-two STEP in R in the middle of the 1 MHz–100 MHz band this
//   brief exists to make quantitative, and nothing would report it.
//
//   Read √(πfµ/σ) as the LOOP's skin resistance — 2·Rs, the two planes, exactly the factor §4.1 is
//   about — and every number in the note is consistent at once. Rs = ½√(πfµ/σ) = ρ/(2δ) is a
//   conductor carrying current on BOTH faces, which is what "two skin depths" MEANS, and it meets
//   ρ/T exactly at T = 2δ: 57 MHz at 0.5 oz, 14 MHz at 1 oz, 3.6 MHz at 2 oz — §2.8's own three
//   numbers, arrived at rather than asserted.
//
// SO THE CROSSOVER IS WHERE THE TWO EXPRESSIONS GENUINELY MEET AND THERE IS NO STEP. A reader who
// "corrects" Rs back to √(πfµ/σ) per plane restores the step and halves every crossover frequency
// in the tool. PdnDistributedTests asserts both halves: the three crossovers, and continuity across
// each of them.
//
// ── NOTHING HERE READS ARTWORK OR BUILDS AN ELEMENT ────────────────────────────────────────────
//
// Doubles in, doubles out, base SI. PdnMountingLoop reads the geometry; PdnAssembly builds the
// element. That is what makes every expression below checkable against its published closed form by
// a test that constructs no board at all.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>§4.1's inductance and skin-effect resistance, and §4.3's partial inductances.</summary>
public static class PdnInductance
{
    /// <summary>The permeability of free space, H/m. Copper is non-magnetic and railRF has no
    /// µᵣ anywhere — a board that needed one would need a different model, not a constant.</summary>
    public const double MuZero = 4.0e-7 * Math.PI;

    // ── R-rail13-1: §4.1's series inductance ───────────────────────────────────────────────────

    /// <summary>
    /// <c>L = µ₀·h</c> — the series inductance of ONE SQUARE of a plane pair separated by
    /// <paramref name="separationMetres"/>, in henries.
    ///
    /// <para><b>Per square, and therefore dimensionless in the plane.</b> A 100 µm four-layer pair
    /// is 126 pH per square and a 1.5 mm two-layer board is 1.88 nH per square — the fifteen-fold
    /// difference that makes §2.8's two crossover frequencies (1.2 MHz and 83 kHz) fifteen-fold
    /// apart, and the whole reason the form-factor question has a quantitative answer.</para>
    /// </summary>
    public static double SquareInductanceHenries(double separationMetres) =>
        separationMetres > 0 ? MuZero * separationMetres : 0.0;

    /// <summary>
    /// The same square, stretched: §4.1's <i>"for non-square cells L and R scale by the aspect ratio
    /// (along/across)"</i>.
    ///
    /// <para>A cell twice as long along the current as it is wide across it carries twice the
    /// inductance and twice the resistance, because it is two squares in series. This is one
    /// expression rather than a multiplication at each call site so that the mesh's x edges and its
    /// y edges cannot come to disagree about which of the two lengths is "along".</para>
    /// </summary>
    /// <param name="separationMetres">The dielectric separation <c>h</c>.</param>
    /// <param name="alongMetres">The cell's extent ALONG the current.</param>
    /// <param name="acrossMetres">Its extent ACROSS the current.</param>
    public static double EdgeInductanceHenries(
        double separationMetres, double alongMetres, double acrossMetres)
    {
        if (!(separationMetres > 0) || !(alongMetres > 0) || !(acrossMetres > 0)) return 0.0;
        return MuZero * separationMetres * (alongMetres / acrossMetres);
    }

    // ── R-rail13-2: the skin-depth crossover, stated rather than assumed ───────────────────────

    /// <summary>Skin depth, in metres — <c>δ = 1/√(π·f·µ₀·σ)</c>.</summary>
    public static double SkinDepthMetres(double frequencyHz, double conductivitySm)
    {
        if (!(frequencyHz > 0) || !(conductivitySm > 0)) return double.PositiveInfinity;
        return 1.0 / Math.Sqrt(Math.PI * frequencyHz * MuZero * conductivitySm);
    }

    /// <summary>
    /// The frequency at which this conductor's thickness IS two skin depths, in hertz —
    /// <c>f = 4/(π·T²·µ₀·σ)</c>.
    ///
    /// <para><b>§2.8's own three numbers, and the performance property worth exploiting.</b> 57 MHz
    /// at 0.5 oz (17.4 µm), 14 MHz at 1 oz (34.8 µm), 3.6 MHz at 2 oz (69.6 µm) — so on the thin
    /// inner copper these boards use, ONE R MATRIX SERVES AN UNUSUALLY WIDE BAND and only L and the
    /// solve change per point. This method is what makes that a stated condition: the extraction
    /// puts the number on its provenance, so the point at which R starts varying is visible rather
    /// than implicit.</para>
    ///
    /// <para>It is also exactly where <see cref="SheetResistanceOhmsPerSquare"/>'s two expressions
    /// meet — see this file's header for why that is not a coincidence and must not be "fixed".</para>
    /// </summary>
    public static double SkinCrossoverHz(double thicknessMetres, double conductivitySm)
    {
        if (!(thicknessMetres > 0) || !(conductivitySm > 0)) return double.PositiveInfinity;
        return 4.0 / (Math.PI * thicknessMetres * thicknessMetres * MuZero * conductivitySm);
    }

    /// <summary>
    /// One conductor's sheet resistance at one frequency, in ohms per square:
    /// <c>ρ/T</c> below two skin depths and <c>ρ/(2δ)</c> above, which is <c>½·√(π·f·µ₀/σ)</c>.
    ///
    /// <para><b>The two expressions meet exactly at <see cref="SkinCrossoverHz"/>, so this is
    /// continuous.</b> Both are stated in §4.1 and the file header explains why the ½ belongs on the
    /// second one: the note writes the LOOP's skin resistance, which is two planes.</para>
    ///
    /// <para>Below the crossover the answer does not depend on <paramref name="frequencyHz"/> at all
    /// — that is the property <see cref="SkinCrossoverHz"/> exists to make visible, and it is why a
    /// DC extraction and a 1 MHz extraction of a 1 oz board share their whole R matrix.</para>
    /// </summary>
    /// <param name="frequencyHz">Zero is DC and returns <c>ρ/T</c> exactly.</param>
    /// <param name="thicknessMetres">The stackup's finished copper thickness.</param>
    /// <param name="conductivitySm">Conductivity AT the extraction's stated temperature —
    /// <see cref="PdnMeshExtractor.ResistivityAt"/> is what applies it, and this method never
    /// does it a second time.</param>
    public static double SheetResistanceOhmsPerSquare(
        double frequencyHz, double thicknessMetres, double conductivitySm)
    {
        if (!(thicknessMetres > 0) || !(conductivitySm > 0)) return double.PositiveInfinity;

        double dc = 1.0 / (conductivitySm * thicknessMetres);
        if (!(frequencyHz > 0)) return dc;

        double skin = 0.5 * Math.Sqrt(Math.PI * frequencyHz * MuZero / conductivitySm);
        return Math.Max(dc, skin);
    }

    /// <summary>
    /// The sheet CONDUCTANCE the mesh actually multiplies by — <c>σ·T</c> at DC, and
    /// <c>1/Rs(f)</c> above the crossover.
    ///
    /// <para>The mesh's half-cell expression is <c>dx²/(2·σT·A)</c>, so the one place frequency
    /// enters an edge's RESISTANCE is this substitution. Naming it rather than inlining
    /// <c>1/Rs</c> at the call site is what stops a later reader "optimising" the reciprocal back
    /// into a conductivity and losing the skin term silently.</para>
    /// </summary>
    public static double SheetSiemensPerSquare(
        double frequencyHz, double thicknessMetres, double conductivitySm)
    {
        double rs = SheetResistanceOhmsPerSquare(frequencyHz, thicknessMetres, conductivitySm);
        return rs > 0 && double.IsFinite(rs) ? 1.0 / rs : 0.0;
    }

    // ── R-rail13-3: where the inductance starts mattering ──────────────────────────────────────

    /// <summary>
    /// The frequency at which <c>ωL = R</c> for one square of a plane pair, in hertz.
    ///
    /// <para>§2.8's two headline numbers: roughly <b>1.2 MHz</b> for a tight four-layer pair
    /// (<c>h</c> = 100 µm on 1 oz copper) and roughly <b>83 kHz</b> for a two-layer board on 1.5 mm
    /// FR-4. <b>Fifteen times apart because <c>h</c> is</b> — which is the whole of why a
    /// resistance-only mesh is honest to a few tens of kilohertz on one of them and to a megahertz
    /// on the other.</para>
    /// </summary>
    /// <param name="separationMetres">The dielectric separation <c>h</c>.</param>
    /// <param name="loopResistanceOhmsPerSquare">§4.1's <c>R = 2·Rs</c>, BOTH planes.</param>
    public static double ReactanceEqualsResistanceHz(
        double separationMetres, double loopResistanceOhmsPerSquare)
    {
        double l = SquareInductanceHenries(separationMetres);
        if (!(l > 0) || !(loopResistanceOhmsPerSquare > 0)) return double.PositiveInfinity;
        return loopResistanceOhmsPerSquare / (2.0 * Math.PI * l);
    }

    /// <summary>
    /// The fraction of <see cref="ReactanceEqualsResistanceHz"/> at which <c>|Z| = √(R² + (ωL)²)</c>
    /// is already <paramref name="fraction"/> above <c>R</c> — <b>0.4583 at 10 %</b>, which is
    /// §2.8's "46 % of those".
    ///
    /// <para>So the resistance-only answer is 10 % low at about 570 kHz on the four-layer pair and
    /// at about 38 kHz on the two-layer board. Those are this brief's own honesty rows.</para>
    /// </summary>
    public static double ToleranceFractionOfCrossover(double fraction)
    {
        double k = 1.0 + fraction;
        return k > 1.0 ? Math.Sqrt(k * k - 1.0) : 0.0;
    }

    // ── R-rail13-5: §4.3's partial inductances ─────────────────────────────────────────────────

    /// <summary>
    /// Partial self-inductance of a straight round conductor of length <paramref name="lengthMetres"/>
    /// and radius <paramref name="radiusMetres"/>, in henries:
    ///
    /// <code>L = (µ₀·ℓ / 2π)·[ ln(2ℓ/r) − 3/4 ]</code>
    ///
    /// <para>Grover, <i>Inductance Calculations</i> (1946), §7 — the low-frequency (uniform current)
    /// form; the <c>−3/4</c> becomes <c>−1</c> when the current is confined to the surface. railRF
    /// uses the uniform form because a plated barrel's wall is thin compared with the skin depth
    /// over most of this band, and because the difference is 0.25 inside a bracket whose value is
    /// 2–3 — bounded, and in the same direction for both vias of a pair, where it very largely
    /// cancels against the <c>−2M</c>.</para>
    ///
    /// <para><b>Returns 0 for ℓ ≤ 0</b>: a part whose pad sits directly on the plane it decouples
    /// has no via and no via inductance, which is a real board and not an error.</para>
    /// </summary>
    public static double RoundSelfPartialHenries(double lengthMetres, double radiusMetres)
    {
        if (!(lengthMetres > 0) || !(radiusMetres > 0)) return 0.0;
        double bracket = Math.Log(2.0 * lengthMetres / radiusMetres) - 0.75;
        return Math.Max(0.0, MuZero * lengthMetres / (2.0 * Math.PI) * bracket);
    }

    /// <summary>
    /// Partial MUTUAL inductance of two parallel conductors of equal length
    /// <paramref name="lengthMetres"/> whose axes are <paramref name="separationMetres"/> apart:
    ///
    /// <code>M = (µ₀·ℓ / 2π)·[ ln( ℓ/d + √(1 + (ℓ/d)²) ) − √(1 + (d/ℓ)²) + d/ℓ ]</code>
    ///
    /// <para>Grover §7 / Ruehli's partial-element formulation — the exact Neumann double integral for
    /// two parallel filaments of equal length, and the reason it is written out rather than
    /// approximated is that <b>this is the term the whole form-factor question turns on</b>. It falls
    /// monotonically with <c>d</c> and towards zero as the pair separates, so
    /// <c>L_loop = L_p + L_r − 2M</c> RISES with separation — which is the physics §4.3 states, and
    /// which a sign error would invert into a tool that rewards moving a capacitor away from its
    /// load.</para>
    /// </summary>
    public static double ParallelMutualPartialHenries(double lengthMetres, double separationMetres)
    {
        if (!(lengthMetres > 0) || !(separationMetres > 0)) return 0.0;

        double l = lengthMetres, d = separationMetres;
        double bracket = Math.Log(l / d + Math.Sqrt(1.0 + l * l / (d * d)))
                       - Math.Sqrt(1.0 + d * d / (l * l))
                       + d / l;
        return Math.Max(0.0, MuZero * l / (2.0 * Math.PI) * bracket);
    }

    /// <summary>
    /// Partial self-inductance of a flat strip — the pad-to-via trace of §4.3's third term:
    ///
    /// <code>L = (µ₀·ℓ / 2π)·[ ln(2ℓ/(w+t)) + 0.5 + 0.2235·(w+t)/ℓ ]</code>
    ///
    /// <para>Grover §3 / Ruehli (1972), the standard rectangular-bar form. It is the smallest of the
    /// three terms on a well-laid-out part and the largest on a badly-laid-out one, which is why it
    /// is in the model rather than folded into a constant.</para>
    /// </summary>
    public static double StripSelfPartialHenries(
        double lengthMetres, double widthMetres, double thicknessMetres)
    {
        if (!(lengthMetres > 0)) return 0.0;
        double wt = Math.Max(widthMetres, 0.0) + Math.Max(thicknessMetres, 0.0);
        if (!(wt > 0)) return 0.0;

        double bracket = Math.Log(2.0 * lengthMetres / wt) + 0.5 + 0.2235 * wt / lengthMetres;
        return Math.Max(0.0, MuZero * lengthMetres / (2.0 * Math.PI) * bracket);
    }

    /// <summary>
    /// §4.3's mounting loop: <c>L_loop = L_p + L_r − 2·M_pr + L_pad</c>.
    ///
    /// <para><b>The minus-two-M term is the whole physics.</b> A power via and its return via close
    /// together have a small loop; the same pair 4 mm apart does not. Everything else in this
    /// expression is a self-term that a part's placement barely moves.</para>
    ///
    /// <para><b>Clamped at zero and never below.</b> The partial forms above are each valid for
    /// ℓ ≫ r; feeding them a pair whose separation is smaller than a barrel radius can drive
    /// <c>L_p + L_r − 2M</c> negative, which is not a small error but a NEGATIVE INDUCTANCE in a
    /// netlist — a branch that delivers energy. The clamp is where that stops, and
    /// <see cref="PdnMountingLoop"/> is what says so on the row.</para>
    /// </summary>
    public static double LoopHenries(
        double selfPowerH, double selfReturnH, double mutualH, double padH) =>
        Math.Max(0.0, selfPowerH + selfReturnH - 2.0 * mutualH + padH);
}
