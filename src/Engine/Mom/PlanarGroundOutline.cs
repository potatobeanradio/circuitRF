// ANT-11 §2 — THE GROUND POUR'S OUTLINE, READ AND CARRIED RATHER THAN COUNTED AND DISCARDED.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHAT THIS IS, AND — MORE IMPORTANTLY — WHAT IT IS NOT
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// R-fg-1. THIS IS A DESCRIBED BOUNDARY, NEVER ARTWORK TO MESH. Nothing downstream of it meshes a
// cell, stamps a basis function or changes a matrix entry. The ground plane in this kernel is the
// laterally infinite PEC the Green's function terminates on, and that is not an approximation the
// mesher makes — it is what the layered Green's function IS, which is why there is no airbox and no
// PML anywhere in this directory. Meshing the pour instead is a different, larger project and
// `brief-antenna-11` §1 argues it buys only PART of one mechanism; the reasons are recorded in
// RESOLVED.md §ANT-11 rather than repeated here.
//
// What it is for is the one thing the solver genuinely could not say before: **how big the ground
// plane is, in wavelengths.** That is the single number that predicts whether the infinite-plane
// assumption is defensible, and a user reading a directivity off a 0.4 λ₀ ground plane needs it
// before they trust the number. The extractor already FOUND this outline and threw it away with a
// one-line ignored-count; reading it costs essentially nothing and it is worth having even though
// ANT-11's edge-diffraction estimate was refused (see PlanarFiniteGround, and R-fg-4 there).
//
// R-fg-2. IT IS THE RETURN PLANE'S OWN OUTLINE, NOT "THE ARTWORK ON ANY GROUND LAYER". R-em-4 makes
// the ground plane the TOP SURFACE OF THE HIGHEST GROUND-DESIGNATED CONDUCTOR BELOW THE SIGNAL, so
// on a four-layer board with two planes exactly one of them is the return and the other is metal
// that is in the way rather than in the structure. Measuring the wrong one would report a size for a
// plane the fields never see — and it is the easy mistake, because both are "the ground layer" in
// the technology. PlanarExtractor selects by band identity and says so in its own note.
//
// R-fg-3. EVERY MEASURE HERE NAMES ITS OWN DEFINITION, because "how big is the ground plane" has
// several defensible answers that differ by a factor of two on the same board:
//
//   • the BOUNDING BOX, which is what a datasheet means by "70 x 70 mm" and what a user will
//     recognise, but which reads a cross-shaped pour as its enclosing square;
//   • the EQUIVALENT-CIRCLE DIAMETER, 2 sqrt(A/pi), which is shape-independent and area-honest and
//     which no user will recognise;
//   • the MARGIN — how far the plane extends BEYOND the radiating metal — which is the electrically
//     meaningful one, because edge effects are set by the distance from the currents to the rim and
//     not by the plane's absolute size. A 70 mm plane under a 49 mm patch has a 10 mm margin, and
//     10 mm is 0.06 λ₀ where 70 mm is 0.41 λ₀: reading the first number as if it were the second is
//     a factor of SEVEN, in the optimistic direction.
//
// All three are reported. None is called "the size".

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>ANT-11 §2 — the finite ground pour's outline, as a DESCRIBED BOUNDARY.</b> See the file header:
/// nothing meshes this, stamps it or solves with it. The ground plane remains the laterally infinite
/// PEC the Green's function terminates on; this is what lets a run say how big the real one is.
/// </summary>
/// <param name="ConductorName">The stackup entry the pour was drawn on — the RETURN PLANE's own
/// conductor (R-fg-2), never simply "a ground layer".</param>
/// <param name="Polygons">The pour, in the layout plane, metres, outer ring plus holes. More than one
/// polygon is ordinary: a plane is routinely drawn as several pours, and a split plane genuinely is
/// two.</param>
public sealed record PlanarGroundOutline(
    string?                      ConductorName,
    IReadOnlyList<PlanarPolygon> Polygons)
{
    /// <summary>Net enclosed metal, metres², holes removed. Zero for an empty outline.</summary>
    public double AreaM2
    {
        get
        {
            double a = 0;
            foreach (var p in Polygons) a += p.Area();
            return a;
        }
    }

    /// <summary>Axis-aligned bounds over every polygon, metres. NaN bounds for an empty outline.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        if (Polygons.Count == 0) return (double.NaN, double.NaN, double.NaN, double.NaN);
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in Polygons)
        {
            var (a, b, c, d) = p.Bounds();
            if (a < x0) x0 = a;
            if (b < y0) y0 = b;
            if (c > x1) x1 = c;
            if (d > y1) y1 = d;
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>The bounding box's x and y extent, metres — R-fg-3's first measure.</summary>
    public (double WidthM, double HeightM) BoxSize()
    {
        var (x0, y0, x1, y1) = Bounds();
        return (x1 - x0, y1 - y0);
    }

    /// <summary>
    /// <c>2·sqrt(A/pi)</c>, metres — R-fg-3's second measure: the diameter of the circle of the same
    /// AREA. Shape-independent, which the bounding box is not, and immune to one narrow tab dragging
    /// the box out by a wavelength.
    /// </summary>
    public double EquivalentDiameterM => 2.0 * Math.Sqrt(AreaM2 / Math.PI);

    public bool IsEmpty => Polygons.Count == 0 || !(AreaM2 > 0);
}

/// <summary>
/// <b>One frequency's reading of an outline: the three R-fg-3 measures, in wavelengths.</b> A record
/// rather than three loose doubles because they are only ever meaningful together — each one alone
/// invites exactly the misreading the file header's factor-of-seven example describes.
/// </summary>
/// <param name="FrequencyHz">The frequency λ₀ is taken at. <b>FREE-SPACE λ₀, not λ_g</b> — the
/// quantity being judged is how large the plane is compared with the radiation it is supposed to be a
/// reference for, and that radiation is in the air above.</param>
/// <param name="BoxWidthLambda">Bounding-box x extent, in λ₀.</param>
/// <param name="BoxHeightLambda">Bounding-box y extent, in λ₀.</param>
/// <param name="EquivalentDiameterLambda">Equal-area circle diameter, in λ₀.</param>
/// <param name="MarginLambda">
/// <b>The smallest distance from the analysed metal's bounding box to the pour's, in λ₀</b> — how far
/// the plane extends beyond the radiator. NEGATIVE when the metal overhangs the pour on some side,
/// which is a real and reportable condition: the artwork claims a return that is not under it.
/// <b>It is a BOUNDING-BOX measure and is named as one</b>, not a true polygon-to-polygon clearance;
/// the box is what makes it O(vertices) rather than O(vertices²) on a pour with a via-relief pattern
/// in it, and the honest measure is the one that is cheap enough to be computed on every run.
/// </param>
public sealed record PlanarGroundExtent(
    double FrequencyHz,
    double BoxWidthLambda,
    double BoxHeightLambda,
    double EquivalentDiameterLambda,
    double MarginLambda)
{
    /// <summary>The smaller bounding-box side, in λ₀ — the dimension that binds.</summary>
    public double SmallestBoxSideLambda => Math.Min(BoxWidthLambda, BoxHeightLambda);

    /// <summary>
    /// <b>The sentence a run prints, and it is the whole point of §2.</b> It states the three
    /// measures, what the model does with the plane, and which direction each published number is
    /// wrong in — never a pass/fail verdict against a threshold nobody measured.
    ///
    /// <para><b>There is deliberately NO "your ground plane is big enough" line.</b> ANT-11 measured
    /// two things about the correction (PlanarFiniteGround) and neither of them yields a size
    /// threshold, so printing one would be a rule of thumb wearing a measurement's clothes. What the
    /// user gets instead is the numbers and the direction of the error, which is actionable and true.</para>
    /// </summary>
    public string Note =>
        $"The ground plane drawn on this board is {BoxWidthLambda:G3} × {BoxHeightLambda:G3} λ₀ " +
        $"({EquivalentDiameterLambda:G3} λ₀ equal-area diameter) at " +
        $"{SurfaceMesher.Eng(FrequencyHz)}Hz, and it extends {MarginLambda:G3} λ₀ beyond the analysed " +
        $"metal's own bounding box. " + WhatTheModelDoes;

    /// <summary>The invariant half of <see cref="Note"/> — true of every run, outline or not, and
    /// worded once so the engine, the CLI and the Data Display cannot drift.</summary>
    public const string WhatTheModelDoes =
        "The ANALYSIS used a laterally INFINITE ground plane — that is what the layered Green's " +
        "function is, and it is why this kernel needs no airbox and no absorbing boundary. So the " +
        "plane's real extent changed nothing about the numbers beside this note, and the three ways " +
        "they are optimistic are known and one-directional: there is no back radiation at all (the " +
        "field below the plane is identically zero by construction, not small), the directivity is " +
        "high because none of the power went behind, and the pattern has none of the ripple or tilt a " +
        "rim of finite extent produces. The margin is the number to read: edge effects are set by how " +
        "far the plane reaches beyond the currents, not by its absolute size.";

    /// <summary>
    /// The reading of <paramref name="outline"/> at one frequency, against the metal
    /// <paramref name="metalBounds"/> the analysis actually meshed. Null when there is no outline or
    /// it encloses no area — an absent outline is not a zero-sized one.
    /// </summary>
    public static PlanarGroundExtent? Of(PlanarGroundOutline? outline,
                                         (double MinX, double MinY, double MaxX, double MaxY)? metalBounds,
                                         double frequencyHz)
    {
        if (outline is null || outline.IsEmpty || !(frequencyHz > 0)) return null;

        double lambda = EmConstants.C0 / frequencyHz;
        var (w, h) = outline.BoxSize();
        var (gx0, gy0, gx1, gy1) = outline.Bounds();

        // The margin is the WORST of the four sides, so one overhanging edge is not averaged away by
        // three generous ones. With no metal bounds to compare against it is NaN rather than zero:
        // "not measured" and "flush with the rim" are different facts.
        double margin = double.NaN;
        if (metalBounds is { } m)
            margin = Math.Min(Math.Min(m.MinX - gx0, gx1 - m.MaxX),
                              Math.Min(m.MinY - gy0, gy1 - m.MaxY));

        return new PlanarGroundExtent(frequencyHz, w / lambda, h / lambda,
                                      outline.EquivalentDiameterM / lambda, margin / lambda);
    }

    /// <summary>
    /// <b>ANT-11 §5 — the note a run prints, over a SWEEP rather than at one point.</b> Null when
    /// there is no outline, which is the only case in which a run says nothing: an absent pour is not
    /// a fact about the plane's size, and inventing a sentence for it would put a note on every
    /// single-conductor run in the repository.
    ///
    /// <para><b>The span is stated when the sweep has one</b>, because λ₀ is what makes these numbers
    /// mean anything and a 1-20 GHz sweep spans a factor of twenty in every one of them. Quoting only
    /// one end would be a number that is right at one frequency and twenty times wrong at the other,
    /// with nothing on screen to say which end it came from.</para>
    /// </summary>
    public static string? SweepNote(PlanarGroundOutline? outline,
                                   (double MinX, double MinY, double MaxX, double MaxY)? metalBounds,
                                   double lowestHz, double highestHz)
    {
        var lo = Of(outline, metalBounds, lowestHz);
        if (lo is null) return null;

        var (wM, hM) = outline!.BoxSize();
        string physical =
            $"The ground plane drawn on '{outline.ConductorName ?? "the return conductor"}' measures " +
            $"{wM * 1e3:G4} × {hM * 1e3:G4} mm ({outline.AreaM2 * 1e6:G4} mm² of metal in " +
            $"{outline.Polygons.Count} pour(s)). ";

        var hi = Of(outline, metalBounds, highestHz);
        bool span = hi is not null && Math.Abs(highestHz - lowestHz) > 1e-9 * Math.Max(1.0, highestHz);

        string electrical = span
            ? $"That is {lo.BoxWidthLambda:G3} × {lo.BoxHeightLambda:G3} λ₀ at " +
              $"{SurfaceMesher.Eng(lowestHz)}Hz rising to {hi!.BoxWidthLambda:G3} × " +
              $"{hi.BoxHeightLambda:G3} λ₀ at {SurfaceMesher.Eng(highestHz)}Hz, and it extends " +
              $"{lo.MarginLambda:G3} to {hi.MarginLambda:G3} λ₀ beyond the analysed metal across that " +
              $"sweep. "
            : $"That is {lo.BoxWidthLambda:G3} × {lo.BoxHeightLambda:G3} λ₀ at " +
              $"{SurfaceMesher.Eng(lowestHz)}Hz ({lo.EquivalentDiameterLambda:G3} λ₀ equal-area " +
              $"diameter), and it extends {lo.MarginLambda:G3} λ₀ beyond the analysed metal. ";

        return physical + electrical + WhatTheModelDoes;
    }
}
