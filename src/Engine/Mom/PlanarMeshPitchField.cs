// M2/M3 — the transmission-line mesh: a pitch that FOLLOWS THE CURRENT, including round the bends.
//
// OWNER INSTRUCTION, 2026-09-09, and it is the whole specification:
//
//   "The 2 settings can be orthogonal to each other. For a transmission line running east-west, the
//    narrowest_metal / CellsAcross setting is a north-south setting and the CellsPerWavelength is an
//    east-west setting. It doesn't have to be one or the other."
//   "CellsPerWavelength should always be in the direction of the current."
//   "Make sure to follow the bends of the geometry to guess which way the current is going."
//
// WHAT WAS WRONG. The shipped rule is h = min(λ_g/CellsPerWavelength, narrowest/MinCellsAcross), the
// SAME min in both axes. On any artwork whose metal is narrower than a λ cell the geometry term wins
// in both directions, and Cells per wavelength and Mesh frequency are then structurally INERT — not
// "less effective than expected", not able to move the mesh at all, at any value. That is a control
// that does nothing, which is the defect this whole thread started from.
//
// WHAT REPLACES IT. Two facts about a transmission line, one per axis:
//
//   ACROSS the metal the current carries the 1/√d edge singularity, so the WIDTH must be resolved —
//   MinCellsAcrossConductor's job, and unchanged.
//   ALONG the metal the current varies on the scale of a wavelength, so λ_g/CellsPerWavelength is
//   the whole requirement — and taking a min with the narrowness there refines for a singularity
//   that is not present.
//
// So the two settings are ORTHOGONAL, exactly as the owner says, and each always moves the mesh in
// its own direction.
//
// WHY IT IS A FIELD AND NOT ONE ANGLE. D8 is one tensor-product grid, and a cell cannot rotate. But
// the GRID LINES need not be uniform, and that is enough to follow a bend: an east-then-north L-bend
// wants a coarse x pitch over its horizontal arm and a fine one only where the vertical arm stands,
// and the transpose in y. Both are expressible as a non-uniform tensor grid. So every point of the
// metal states what it needs in x and in y, and a column takes the finest need in it. Nothing here
// breaks the tensor product, PlanarCell.IX/IY, the rooftop cell pair, or RectangleIntegrals'
// axis-aligned closed forms — a route that did would be a second mesher, not a setting.
//
// WHY THE FIELD IS THEN SMOOTHED. A column-wise minimum is a STEP function, and L8b already paid for
// a discontinuous size field once: the marcher lands on the discontinuity, and moving the same
// rectangle 3.7 mm changed the mesh by 33%. The fix there was to make the field continuous, and it is
// the fix here — a Lipschitz lower envelope at the same bounded growth ratio the edge fan uses, which
// is two sweeps over an array and restores exact translation invariance.

// ── ANT-3 — THE SECOND INTENT: A SHEET, WHERE THE DIRECTION QUESTION DOES NOT APPLY ────────────
//
// Everything above asks WHICH WAY IS THE CURRENT GOING and declines when it cannot tell. A wide
// radiating sheet is the case it declines by construction, and it is also the case where the
// question is meaningless: on a patch the current varies on the scale of a wavelength in BOTH
// directions — a half-cosine along the resonant dimension, near-uniform across the width — and the
// interior carries no singularity to resolve at all.
//
// So `PlanarCurrentModel.Sheet` asks for λ_g/CellsPerWavelength in BOTH axes, floored locally by
// local-width/MinCellsAcrossConductor only where the metal is genuinely narrower than that. On a
// patch that is the feed and nothing else. Nothing else changes: the same sampling grid, the same
// longest-chord width estimator (for the reason its own note gives — the SHORTEST chord is garbage
// near a rim), the same one-way floor at the per-axis rule, the same Lipschitz lower envelope, the
// same aspect cap. The direction term is what drops out, and with it the decline: there is nothing
// left to be ambiguous about.
//
// THE ASPECT CAP CANNOT BIND ON A SHEET, and that is a property rather than an omission — the two
// axes are asked for the same number at every point, so the requested aspect is exactly 1:1. What a
// realised CELL comes out at is still the product of two independent axis fields, which is the
// tensor product and is true of every mode here.
//
// AND THE RIM IS NOT SKIPPED. The temptation is real: a patch interior is smooth and the rim is
// where the cells go. But the two radiating edges set the effective length, hence the resonant
// frequency, hence every number downstream, and the two non-radiating edges carry the transverse
// 1/√d singularity. ANT-2's per-attractor grading is what makes that affordable; this must not
// spend it back.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The per-axis bulk pitch the current model asks for, sampled and graded, plus everything the report
/// needs to say about how it was arrived at. One record for both intents — a
/// <see cref="PlanarCurrentModel.Sheet"/> field is the same object with the direction term gone.
/// </summary>
/// <param name="Ok">False when the field could not be built; the mesher then falls back to the
/// per-axis rule and the setting does nothing, with <paramref name="Note"/> saying why.
/// <b><see cref="PlanarCurrentModel.TransmissionLine"/> can decline on ordinary artwork</b> — a
/// structure that states no usable direction — whereas <b><see cref="PlanarCurrentModel.Sheet"/>
/// declines only on a degenerate PROBLEM</b> (no metal, no bounded extent, no wavelength), because
/// there is no direction for it to be unsure of.</param>
/// <param name="Note">What it did, or why it declined — always populated, always reported.</param>
/// <param name="MinPitchX">The finest bulk x pitch the field asks for anywhere, for the report.</param>
/// <param name="MaxPitchX">The coarsest.</param>
/// <param name="MinPitchY">As <paramref name="MinPitchX"/>, in y.</param>
/// <param name="MaxPitchY">As <paramref name="MaxPitchX"/>, in y.</param>
/// <param name="WorstAspect">The largest cell aspect the field asks for, after the cap. Exactly 1
/// under <see cref="PlanarCurrentModel.Sheet"/>, which asks both axes for the same number at every
/// point.</param>
/// <param name="Capped">Whether <see cref="PlanarMeshSettings.MaxCellAspect"/> actually bound. Never
/// true under <see cref="PlanarCurrentModel.Sheet"/> — see <paramref name="WorstAspect"/>.</param>
/// <param name="FloorSamples">How many metal field samples asked for the local-width floor rather
/// than the λ cap — the report's "where the floor bound", and 0 says the bulk pitch is the whole
/// answer.</param>
/// <param name="MetalSamples">How many field samples landed on metal at all, so
/// <paramref name="FloorSamples"/> reads as a fraction rather than a bare count.</param>
public sealed record PlanarPitchField(
    bool     Ok,
    string   Note,
    double   MinPitchX,
    double   MaxPitchX,
    double   MinPitchY,
    double   MaxPitchY,
    double   WorstAspect,
    bool     Capped,
    int      FloorSamples = 0,
    int      MetalSamples = 0)
{
    /// <summary>The x bulk cap at abscissa x — the field, ready for the grid marcher.</summary>
    public Func<double, double> AtX { get; init; } = _ => double.PositiveInfinity;

    /// <summary>The y bulk cap at ordinate y.</summary>
    public Func<double, double> AtY { get; init; } = _ => double.PositiveInfinity;
}

public static class PlanarMeshPitchField
{
    /// <summary>
    /// How many directions the local chord is measured in. 12 is 15° apart, which resolves the
    /// direction to ±7.5°; the pitch's dependence on the angle is a cosine, so a 7.5° error costs
    /// under 1% and finer sampling buys nothing measurable while costing a full scan pass each.
    /// </summary>
    public const int DirectionSamples = 12;

    /// <summary>
    /// Samples per axis of the field grid. This is NOT the mesh — it is the resolution at which the
    /// local width and direction are estimated, and it is fixed rather than derived so that the
    /// answer does not depend on the mesh it is about to produce (D9's own rule, one level up).
    /// </summary>
    public const int FieldSamples = 384;

    /// <summary>
    /// <b>Build the pitch field.</b> Nothing is meshed, nothing is solved.
    /// </summary>
    /// <param name="problem">The artwork.</param>
    /// <param name="hWave">λ_g / CellsPerWavelength — the ALONG requirement under
    /// <see cref="PlanarCurrentModel.TransmissionLine"/>, and the BULK requirement in both axes
    /// under <see cref="PlanarCurrentModel.Sheet"/>.</param>
    /// <param name="minCellsAcross">MinCellsAcrossConductor — the ACROSS requirement.</param>
    /// <param name="model">
    /// <b>Which intent this field is FOR — ANT-3.</b> <see cref="PlanarCurrentModel.TransmissionLine"/>
    /// is everything the top of this file describes; <see cref="PlanarCurrentModel.Sheet"/> drops the
    /// direction term and asks both axes for the bulk pitch, floored locally by the metal's own width
    /// only where that is finer. <see cref="PlanarCurrentModel.None"/> never reaches here — the caller
    /// does not build a field at all.
    /// </param>
    /// <param name="growth">The Lipschitz constant the field is graded at, r − 1 from the edge fan's
    /// own derived ratio, so the bulk field and the edge fan cannot disagree about how fast a cell
    /// size may change.</param>
    /// <param name="ports">Used only to CHECK the field, never to build it — see the note.</param>
    /// <param name="detailFloorM">
    /// <b>ANT-2's detail floor, in metres — a local across-chord below this is read AS this.</b> 0 is
    /// off, and off is bit-identical to what this built before the floor existed.
    ///
    /// <para>It belongs here as much as in <c>SurfaceMesher.MeasureNarrowness</c>, and for the same
    /// reason: this field measures a width at every metal point and hands it straight to
    /// <c>minCellsAcross</c>, so with the floor applied only to the global measurement a
    /// sub-wavelength import artefact would go on setting the pitch of its own column — which, in a
    /// tensor product, is a gridline across the whole part.</para>
    /// </param>
    public static PlanarPitchField Build(
        PlanarProblem problem, double hWave, int minCellsAcross, double growth,
        double floorX, double floorY,
        IReadOnlyList<PlanarPort>? ports = null,
        double detailFloorM = 0.0,
        PlanarCurrentModel model = PlanarCurrentModel.TransmissionLine)
    {
        ArgumentNullException.ThrowIfNull(problem);
        bool sheet = model == PlanarCurrentModel.Sheet;

        var (x0, y0, x1, y1) = problem.Bounds();
        double w = x1 - x0, h = y1 - y0;
        if (!(w > 0) || !(h > 0) || !(hWave > 0) || double.IsInfinity(hWave) || minCellsAcross < 1
            || !(floorX > 0) || !(floorY > 0))
            return Declined("it needs a finite wavelength cell and a bounded piece of artwork; this " +
                            "problem has neither.");

        int n = FieldSamples;
        double dx = w / n, dy = h / n;

        // ── The local chord, in every direction ──────────────────────────────────────────────
        //
        // For each of DirectionSamples directions φ, cast scan lines ALONG φ and paint the length of
        // the RUN of metal containing each field cell. Keeping all of them is what lets the next
        // block ask for a chord in a direction chosen PER CELL.
        var chord = new double[DirectionSamples][];
        for (int d = 0; d < DirectionSamples; d++)
        {
            chord[d] = new double[n * n];
            Paint(problem, chord[d], Math.PI * d / DirectionSamples, n, x0, y0, x1, y1, dx, dy);
        }

        // ── What each metal point needs, in x and in y ───────────────────────────────────────
        //
        // THE DIRECTION IS THE LONGEST CHORD, NOT THE SHORTEST GAP. The first version of this took
        // the shortest chord as the transverse direction, which is right in the middle of a strip
        // and catastrophic anywhere near a rim: a scan line nearly tangent to an edge cuts a chord a
        // sampling step long, so the "width" collapsed to ~1 µm and the mesh came out 2.27 MILLION
        // cells on a plain 10 mm line. Taking the LONGEST chord as the along direction is stable —
        // near an end cap the longest chord still runs down the line — and the width is then the
        // chord ACROSS it, which is the width of the metal there and nothing else.
        var needX = new double[n];
        var needY = new double[n];
        Array.Fill(needX, double.PositiveInfinity);
        Array.Fill(needY, double.PositiveInfinity);

        double worstAspect = 1.0;
        bool capped = false, anyMetal = false;
        int perp = DirectionSamples / 2;                     // half of 180° is a quarter turn
        int metalSamples = 0, floorSamples = 0;              // ANT-3's "where did the floor bind"

        // The finest pitch the per-axis rule would have used, in either axis — the floor for the
        // ACROSS requirement, since "across" is in the rotated frame and has no single axis.
        double acrossFloor = Math.Min(floorX, floorY);

        for (int iy = 0; iy < n; iy++)
            for (int ix = 0; ix < n; ix++)
            {
                int idx = iy * n + ix;
                int best = -1;
                double bestLen = 0;
                for (int d = 0; d < DirectionSamples; d++)
                    if (chord[d][idx] > bestLen) { bestLen = chord[d][idx]; best = d; }
                if (best < 0) continue;                      // not metal
                anyMetal = true;

                double across = chord[(best + perp) % DirectionSamples][idx];
                if (!(across > 0)) across = bestLen;         // degenerate: treat it as isotropic
                if (across < detailFloorM) across = detailFloorM;   // ANT-2 M1 — see the parameter
                metalSamples++;

                // ── ANT-3 — THE SHEET: THE SAME NUMBER IN BOTH AXES, AND NO DIRECTION AT ALL ────
                //
                // A radiating sheet's current varies on the scale of a wavelength in BOTH
                // directions, so the bulk requirement is λ_g/CellsPerWavelength either way and the
                // metal's own width is a FLOOR that applies only where the metal is genuinely
                // narrower than that — on a patch, the feed and nothing else.
                //
                // `across` is the same longest-chord estimator the directed branch uses, and it is
                // reused rather than replaced with "the shortest chord" for exactly the reason its
                // own note gives: a scan line nearly tangent to a rim cuts a chord one sampling
                // step long, and taking THAT as the width meshed a plain 10 mm line at 2.27 million
                // cells. The estimate does not become safer just because the intent changed.
                //
                // THE FLOOR IS THE SAME acrossFloor, for the same reason: this is a sampled
                // quantity, and it must never drive the mesh finer than a direct measurement of the
                // polygons did. Combined with the per-axis floor applied below, the sheet's PITCH is
                // bounded below by the per-axis rule's on every input — which is the structural
                // invariant. It does NOT follow that the CELL COUNT is bounded above by it: a
                // coarser bulk gives the graded edge fan further to climb, and on artwork that is
                // mostly rim the extra fan cells can outweigh the bulk saving (measured on an
                // 8-segment taper: 702 cells under the per-axis rule against 810 here, and 679
                // under both with the edge mesh off). Same trade the directed mode already reports.
                if (sheet)
                {
                    double hSheet = Math.Max(acrossFloor, Math.Min(hWave, across / minCellsAcross));
                    if (!(hSheet > 0)) continue;

                    // "WHERE THE FLOOR BOUND" IS A QUESTION ABOUT THE OUTCOME, NOT THE REQUEST, and
                    // the difference is not pedantic. Counting `across/minCellsAcross < hWave`
                    // instead counts every sample whose WIDTH TERM was finer than the λ cap —
                    // including the ones the per-axis floor then raised straight back to the λ cap,
                    // which is most of a rim on wide metal (measured: 73% of a 20 × 30 mm plate,
                    // whose x pitch came out at λ_g/20 exactly). A user reading "the width bound the
                    // pitch on 73% of this plate" beside a pitch that IS the wavelength cap has been
                    // told something false. `hSheet < hWave` says only what actually happened.
                    if (hSheet < hWave) floorSamples++;
                    if (hSheet < needX[ix]) needX[ix] = hSheet;
                    if (hSheet < needY[iy]) needY[iy] = hSheet;
                    continue;
                }

                double theta = Math.PI * best / DirectionSamples;

                // THE FLOOR IS APPLIED HERE, BEFORE THE ASPECT CAP, AND THE ORDER IS LOAD-BEARING.
                // A field sample sitting on a rim has a short across-chord — a fraction of the
                // metal's real width — and capping the ALONG pitch at 64× THAT pins the along pitch
                // to a few hundred µm. Measured on the owner's own connector cutout: the along pitch
                // topped out at 476 µm against a λ cell of 715 µm, so Cells per wavelength was STILL
                // inert with the setting on, which is the entire bug this setting exists to fix.
                // Flooring first makes the cap describe the mesh's real finest cell.
                double hAcross = Math.Max(acrossFloor, Math.Min(hWave, across / minCellsAcross));
                double hAlong  = hWave;
                if (!(hAcross > 0)) continue;

                // The aspect cap is applied to the REQUIREMENT rather than to the finished cells:
                // capping afterwards would leave the two axes disagreeing about which constraint
                // they had honoured.
                if (hAlong > hAcross * PlanarMeshSettings.MaxCellAspect)
                {
                    hAlong = hAcross * PlanarMeshSettings.MaxCellAspect;
                    capped = true;
                }
                worstAspect = Math.Max(worstAspect, hAlong / hAcross);

                double c = Math.Abs(Math.Cos(theta)), sn = Math.Abs(Math.Sin(theta));
                double rx = 1.0 / (c / hAlong + sn / hAcross);
                double ry = 1.0 / (sn / hAlong + c / hAcross);
                if (rx < needX[ix]) needX[ix] = rx;
                if (ry < needY[iy]) needY[iy] = ry;
            }

        if (!anyMetal)
            return Declined(sheet
                ? "no field sample landed on metal, so there is no width to measure."
                : "no field sample landed on metal, so there is no direction to follow.");

        // ── THE INVARIANT THAT MAKES THIS SAFE: it may only COARSEN ─────────────────────────────
        //
        // Every pitch is floored at the one the per-axis rule would have used. The transmission-line
        // mesh exists to stop the geometry term over-refining ALONG the current; it has no business
        // refining anything, and a local width estimate is a sampled quantity that must never be
        // allowed to drive the mesh finer than a measurement of the actual polygons. So the cell
        // count with this on is bounded above by the count with it off, structurally, and no sampling
        // artefact can turn a setting the user reached for to make the mesh smaller into one that
        // makes it bigger.
        for (int i = 0; i < n; i++)
        {
            if (needX[i] < floorX) needX[i] = floorX;
            if (needY[i] < floorY) needY[i] = floorY;
        }

        // ── Grade both fields, and it is not optional ────────────────────────────────────────
        //
        // A column-wise minimum is a STEP function. L8b already paid for a discontinuous size field
        // once: the marcher lands on the discontinuity and moving the same rectangle 3.7 mm changed
        // the mesh by 33%. A Lipschitz lower envelope at the edge fan's own growth rate removes it —
        // the same h(x) = min over x' of [h(x') + g·|x − x'|] the fan uses, in two sweeps.
        Lipschitz(needX, dx, growth);
        Lipschitz(needY, dy, growth);

        double minX = double.PositiveInfinity, maxX = 0, minY = double.PositiveInfinity, maxY = 0;
        foreach (double v in needX) { if (double.IsInfinity(v)) continue; minX = Math.Min(minX, v); maxX = Math.Max(maxX, v); }
        foreach (double v in needY) { if (double.IsInfinity(v)) continue; minY = Math.Min(minY, v); maxY = Math.Max(maxY, v); }
        if (double.IsInfinity(minX) || double.IsInfinity(minY) || !(minX > 0) || !(minY > 0))
            return Declined("the pitch field came out empty in one axis.");

        // A column with no metal in it keeps infinity, which the marcher must never read; hold the
        // coarsest value there instead — no cell of the mesh lives there anyway.
        var fx = new double[n];
        var fy = new double[n];
        for (int i = 0; i < n; i++)
        {
            fx[i] = double.IsInfinity(needX[i]) ? maxX : needX[i];
            fy[i] = double.IsInfinity(needY[i]) ? maxY : needY[i];
        }

        double SampleX(double at) => fx[Math.Clamp((int)((at - x0) / dx), 0, n - 1)];
        double SampleY(double at) => fy[Math.Clamp((int)((at - y0) / dy), 0, n - 1)];

        // ONE LINE OF NUMBERS, not a paragraph of reasoning. Owner instruction, 2026-09-09: an
        // engineer does not read a paragraph in a side panel, and the reasoning belongs in the source
        // — which is the top of this file — rather than in front of someone trying to read a pitch.
        // ANT-3 — A SHEET SAYS WHAT A SHEET DID. It never declines, so the note is the ONLY way the
        // user sees that it acted; the brief asks for three things by name and they are all here —
        // the bulk pitch in each axis, WHERE the width floor bound, and the worst aspect.
        string note = sheet
            ? $"Sheet mesh ON — λ_g/N in BOTH axes, no direction. " +
              $"x pitch {SurfaceMesher.Eng(minX)}m–{SurfaceMesher.Eng(maxX)}m, " +
              $"y pitch {SurfaceMesher.Eng(minY)}m–{SurfaceMesher.Eng(maxY)}m, " +
              $"worst aspect {worstAspect:G3}:1. " +
              (floorSamples > 0
                  ? $"The metal's own width, not the wavelength, set the pitch on {floorSamples:N0} " +
                    $"of {metalSamples:N0} metal sample(s) " +
                    $"({(double)floorSamples / metalSamples:P1}) — that is where the narrow metal is."
                  : "Nothing on this artwork is narrow enough for the width floor to bind, so the " +
                    "bulk pitch is the whole answer.")
            : $"Transmission-line mesh ON — λ along the current, width across it. " +
              $"x pitch {SurfaceMesher.Eng(minX)}m–{SurfaceMesher.Eng(maxX)}m, " +
              $"y pitch {SurfaceMesher.Eng(minY)}m–{SurfaceMesher.Eng(maxY)}m, " +
              $"worst aspect {worstAspect:G3}:1" +
              (capped ? $" (capped)" : "") + ".";

        // THE EDGE FAN'S OWN TRADE, said out loud. A coarser bulk gives the graded fan at every
        // conductor edge further to climb, and the fan's ratio is clamped at 3× per cell, so each
        // attractor costs roughly log₃(coarser/finer) extra cells. On artwork that is mostly rim and
        // hardly any bulk — a finely tessellated taper, say — that can outweigh the bulk saving and
        // the CELL COUNT can rise even though every cell is the same size or larger. Measured:
        // 728 → 858 cells on an 8-segment taper. Anyone who turns this on to save cells and gets
        // more of them needs the reason on screen, not in a file.
        double climb = Math.Max(maxX / Math.Max(minX, 1e-18), maxY / Math.Max(minY, 1e-18));
        if (climb > 3.0)
            note += $" Bulk pitch spans {climb:G3}× here, so each edge fan has further to climb; on " +
                    "mostly-rim artwork that can RAISE the cell count. Edge mesh off removes the fan.";

        // THE PORT CHECK IS THE DIRECTED MODE'S AND ONLY ITS. It asks whether a port sits on a face
        // the metal runs INTO, which is a question about a direction — and a sheet has none. Asking
        // it here would report a "disagreement" on every patch, about an answer nothing used.
        if (!sheet)
        {
            string? portNote = PortAgreement(problem, ports, chord, n, x0, y0, dx, dy);
            if (portNote is not null) note += " " + portNote;
        }

        return new PlanarPitchField(true, note, minX, maxX, minY, maxY, worstAspect, capped,
                                    floorSamples, metalSamples)
        {
            AtX = SampleX,
            AtY = SampleY,
        };

        PlanarPitchField Declined(string why) =>
            new(false, (sheet ? "Sheet mesh" : "Transmission-line mesh") + " NOT applied — " + why +
                       " Used the per-axis rule.",
                0, 0, 0, 0, 1, false);
    }

    /// <summary>
    /// Paint the length of the metal run containing each field cell, for scan lines cast along one
    /// direction. Even–odd crossings, so a hole falls out with no special case — the same rule
    /// <c>SurfaceMesher.Spans</c> uses, and for the same reason.
    /// </summary>
    private static void Paint(PlanarProblem problem, double[] into, double phi, int n,
                              double x0, double y0, double x1, double y1, double dx, double dy)
    {
        double ux = Math.Cos(phi), uy = Math.Sin(phi);
        double vx = -uy, vy = ux;
        double w = x1 - x0, h = y1 - y0;
        double span = Math.Sqrt(w * w + h * h);
        double cx = 0.5 * (x0 + x1), cy = 0.5 * (y0 + y1);

        double step = 0.5 * Math.Min(dx, dy);
        int lines = (int)Math.Ceiling(span / step) + 1;
        var hits = new List<double>(16);

        foreach (var layer in problem.Layers)
            foreach (var poly in layer.Polygons)
                for (int i = 0; i < lines; i++)
                {
                    double t = span * ((i + 0.5) / lines - 0.5);
                    double ax = cx + vx * t, ay = cy + vy * t;
                    hits.Clear();
                    Cross(poly.Outer, ax, ay, ux, uy, hits);
                    foreach (var ring in poly.HoleRings) Cross(ring, ax, ay, ux, uy, hits);
                    if (hits.Count < 2) continue;
                    hits.Sort();

                    for (int k = 0; k + 1 < hits.Count; k += 2)
                    {
                        double lo = hits[k], hi = hits[k + 1], run = hi - lo;
                        if (!(run > 0)) continue;
                        for (double sPos = lo; sPos <= hi; sPos += step)
                        {
                            int ix = (int)((ax + ux * sPos - x0) / dx);
                            int iy = (int)((ay + uy * sPos - y0) / dy);
                            if ((uint)ix >= (uint)n || (uint)iy >= (uint)n) continue;
                            int idx = iy * n + ix;
                            if (run > into[idx]) into[idx] = run;
                        }
                    }
                }
    }

    /// <summary>
    /// <b>The ports are a CHECK, not the source — and that is a deliberate reversal of the brief.</b>
    ///
    /// <para>The owner's own suggestion was to take the direction FROM the port orientation, and for
    /// a straight line that is exactly right. It cannot survive "follow the bends", though: two ports
    /// state one direction between them, and an east-then-north bend's two ports state 45°, which is
    /// the direction of neither arm. A per-point measurement of the metal answers the bend; the ports
    /// then say whether that measurement agrees with where the current actually enters.</para>
    ///
    /// <para>So this reports a disagreement and never acts on one. A port sitting on a face the local
    /// field says is ALONG the current rather than across it means one of the two is wrong, and the
    /// user is the one who can tell which.</para>
    /// </summary>
    private static string? PortAgreement(
        PlanarProblem problem, IReadOnlyList<PlanarPort>? ports,
        double[][] chord, int n, double x0, double y0, double dx, double dy)
    {
        if (ports is null || ports.Count == 0) return null;

        int disagreed = 0, checkedCount = 0;
        foreach (var p in ports)
        {
            if (p.Kind != PlanarPortKind.Edge) continue;      // an interior gap states no through-direction
            int ix = (int)((p.Location.X - x0) / dx);
            int iy = (int)((p.Location.Y - y0) / dy);
            // A port sits ON the rim, so look a little way in rather than exactly at it.
            double best = 0, theta = 0;
            for (int oy = -2; oy <= 2; oy++)
                for (int ox = -2; ox <= 2; ox++)
                {
                    int jx = ix + ox, jy = iy + oy;
                    if ((uint)jx >= (uint)n || (uint)jy >= (uint)n) continue;
                    int idx = jy * n + jx;
                    for (int d = 0; d < chord.Length; d++)
                        if (chord[d][idx] > best) { best = chord[d][idx]; theta = Math.PI * d / chord.Length; }
                }
            if (!(best > 0)) continue;

            checkedCount++;
            double declared = p.Side is PlanarPortSide.MinX or PlanarPortSide.MaxX ? 0.0 : 0.5 * Math.PI;
            if (Separation(declared, theta)
                > PlanarMeshSettings.DirectionAgreementDegrees * Math.PI / 180.0) disagreed++;
        }

        if (checkedCount == 0 || disagreed == 0) return null;
        return $"{disagreed} of {checkedCount} port(s) sit on a face the metal does not run into — " +
               "the mesh follows the metal, so check those ports' sides.";
    }

    /// <summary>The angle between two DIRECTIONS, in [0, π/2]. Current flowing +x̂ and −x̂ wants the
    /// same cells, so 10° and 190° are the same answer and must not read as 180° apart.</summary>
    internal static double Separation(double a, double b)
    {
        static double Fold(double r) { double t = r % Math.PI; return t < 0 ? t + Math.PI : t; }
        double d = Math.Abs(Fold(a) - Fold(b));
        return d > 0.5 * Math.PI ? Math.PI - d : d;
    }

    /// <summary>
    /// h(x) ← min over x' of [h(x') + g·|x − x'|], in two sweeps. The same lower envelope
    /// <c>SurfaceMesher.SizeAt</c> forms over attractors, on a sampled axis instead of a point set —
    /// which is what makes the bulk field and the edge fan grade at one rate rather than two.
    /// </summary>
    internal static void Lipschitz(double[] f, double step, double growth)
    {
        if (!(growth > 0) || f.Length < 2) return;
        double g = growth * step;
        for (int i = 1; i < f.Length; i++) f[i] = Math.Min(f[i], f[i - 1] + g);
        for (int i = f.Length - 2; i >= 0; i--) f[i] = Math.Min(f[i], f[i + 1] + g);
    }

    private static void Cross(IReadOnlyList<EmPoint> ring, double ax, double ay,
                              double ux, double uy, List<double> into)
    {
        for (int k = 0; k < ring.Count; k++)
        {
            var p = ring[k];
            var q = ring[(k + 1) % ring.Count];
            double ex = q.X - p.X, ey = q.Y - p.Y;
            double den = ux * ey - uy * ex;
            if (den == 0) continue;
            double tt = ((p.X - ax) * uy - (p.Y - ay) * ux) / -den;
            if (tt < 0 || tt >= 1) continue;
            into.Add(((p.X + ex * tt) - ax) * ux + ((p.Y + ey * tt) - ay) * uy);
        }
    }
}
