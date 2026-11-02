# Brief 3 — pads and vias: via in pad, ground via distance, shared ground vias, thermal vias

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev3-n` · **Phase:** checks
**Area:** `src/Design/Layout/Review/Checks/` (new: `ReviewPads.cs`, `ViaInPadCheck.cs`, `GroundViaDistanceCheck.cs`,
`SharedGroundViaCheck.cs`, `ThermalViasCheck.cs`)
**Depends on:** 2 · **Blocks:** 6

---

## 0. What a reviewer looks for

- **No via in a pad.** Production does not want a drill inside an SMD pad (solder wicks down the barrel). A GND via
  belongs CLOSE to the pad, not in it.
- **A ground pad has its own ground via, close by.** A long path from a ground pad to a via is inductance in series with
  the part's ground.
- **Ground vias are not shared** between pads where it can be avoided, for the same reason.
- **A power stage's thermal pad has a via farm under it**, dense enough to pull the heat into the planes.

## 1. Shared definitions: `ReviewPads`

Built once per run (on the run's shared regions, connectivity and evaluator; nothing re-flattened) and used by all four
checks. Every definition is a `DrcLayerExpr` built in code where brief 1 makes that possible.

**R-rev3-1 — Pad.** Per side: `and(outer copper, soldermask)` — copper exposed by a mask opening. This is the one
definition that holds on a Gerber import (no parts, no pins) and on a native layout (footprints draw their mask openings
through `LandPatternLayers`). A side with no soldermask layer has no pads and every check here reports *not run* for that
side. Where the layout has placed parts, `PlacedPins.Of` names the part and pin a pad belongs to, for the finding's
sentence only ("C209 pad 2"); it never changes which pads exist.

**R-rev3-2 — Via.** A polygon of a drill layer (`ViaSpanResolver.DrillLayerKeys`, the one place that names them) whose
stackup row is plated (`Fill` = Plated). A `ViaShape`'s barrel and
a bare circle on a drill layer are the same thing here (both arrive as drill-layer regions through `DrcRegions.Expand`;
DFR29-3 established that an import draws vias both ways). A non-plated drill row is a hole, not a via.

**R-rev3-3 — Through-hole pin.** A pad that contains exactly one plated drill, with the pad's centroid within the drill's
radius of the drill's centre, is a through-hole pin: the drill is the pin's hole, not a via in the pad. Its drill is not
a via for any check here. (Owner decision D2 below.)

**R-rev3-4 — Ground.** `on_ground(…)` (brief 1). A ground pad is a pad on a ground net; a ground via is a via on a ground
net that is not a through-hole pin's hole. **No ground reference flagged in the stackup means all four checks are
*not run*** with the Technology Editor sentence (brief 2 R-rev2-5); nothing is inferred.

**R-rev3-5 — Thermal pad.** A ground pad whose area is at least the `thermal-pad-area` threshold (default **2.0 mm²**).
Sized to catch an exposed paddle (a 3 × 3 mm QFN paddle is 9 mm²) and not a passive's pad (an 0603 pad is under 1 mm²).

## 2. The checks

**R-rev3-6 — `via-in-pad`.** Every via that overlaps a pad which is not a thermal pad. Marker: the via's barrel and the
pad outline. Sentence: "Via in pad (drill ⌀0.2 mm inside a 0.6 × 0.5 mm pad)". A via in a thermal pad is
`thermal-vias`' business and is not reported here. Severity Warning. No thresholds beyond `thermal-pad-area`.

**R-rev3-7 — `ground-via-distance`.** Every ground pad that is not a thermal pad with no ground via within `max-distance`
(default **0.5 mm**, closest approach pad edge to barrel edge, `near(pad, vias, d, 0, 0)`). The measured value is the
distance to the nearest ground via, searched out to 10 × `max-distance` and stated as "no ground via within 5 mm"
beyond that. Marker: the pad and, when found, a line to the nearest via. A ground pad sitting in a ground pour is judged
the same way: the pour's own stitching vias count if they are close enough, because the question is the path length to
a via, not what the copper between is called.

**R-rev3-8 — `shared-ground-via`.** A ground via that is the ONLY ground via within `max-distance` of two or more ground
pads that are not thermal pads. Marker: the via and every pad sharing it. Sentence names how many pads share it. A via in
a pour with several pads near it, each of which has another via of its own within reach, is not a finding: the point is a
pad that depends on a via another pad also depends on. The per-pad via lists come from brief 1's `near` helper with the
identities kept (expose the internal routine), not from a second distance computation.

**R-rev3-9 — `thermal-vias`.** Every thermal pad whose ground via count divided by its area is below `min-via-density`
(default **1.0 per mm²**, roughly a 1 mm pitch). A thermal pad with no via at all is always a finding. Sentence: "4 vias
under a 9.0 mm² thermal pad (0.44 per mm²); the review asks for 1.0 per mm²". Vias counted are those whose barrel lies at
least half inside the pad.

Thresholds (brief 2's table): `thermal-pad-area` (mm², shared by all four), `ground-via-distance.max-distance` (length,
also used by `shared-ground-via`), `thermal-vias.min-via-density` (per mm²).

## 3. Owner decisions — the defaults are taken unless the owner changes them

- **D1 — default thresholds.** 2.0 mm² thermal pad; 0.5 mm ground via distance; 1.0 via per mm².
- **D2 — through-hole pins** are recognised geometrically (R-rev3-3). The alternative, counting every drill as a via,
  floods `via-in-pad` with every connector pin.
- **D3 — a via in a thermal pad is never a `via-in-pad` finding.** The alternative is to report it as a Note, because
  some assembly houses want those plugged or tented; default no.

## 4. Tests (one small layout per claim, built in code, in `tests/Ui.Tests/Review/PadViaChecksTests.cs`)

1. A via inside an 0603-sized pad → one `via-in-pad`; the same via 0.2 mm outside the pad → none.
2. A through-hole pin (pad centred on its drill) → no `via-in-pad`.
3. A ground pad with a ground via 0.3 mm away → none; at 0.8 mm → one `ground-via-distance` stating the distance.
4. Two ground pads whose only via within reach is the same via → one `shared-ground-via`; give one pad its own via →
   none.
5. A 9 mm² ground thermal pad with 4 vias → one `thermal-vias`; with 9 → none; a via in it → no `via-in-pad`.
6. The same layout with no ground reference flagged → all four *not run*, one Note.

Run only that class.

## 5. By hand on board C (completion note)

All four checks: wall time, peak memory, counts, and the `via-in-pad` count against the 69 drill ∩ copper ∩ mask
regions the pre-series probe found (the difference is the thermal-pad and through-hole exemptions; say how many of
each). Pick five findings of each check and say whether a reviewer would agree. If any check produces more than a few
hundred findings, report that rather than tuning the default to hide it.
