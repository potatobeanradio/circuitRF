# Sonnet Brief — Phase L3d: arbitrary-angle instances

**Design:** `docs/design/layout-view.md` §7 (hierarchy), §3.2 (shape promotion), §8 (interchange).
**Consumes L3a (instances/arrays), L3c (flatten/group), L1h (the shared coordinate walk).**

**Scope is exactly one field:** `LayoutInstance.Rot` stops being a four-value enum and becomes a real
angle. Nothing else about instances changes — not arrays, not magnification, not mirroring, not the
instance cache.

**Test loop** (root `CLAUDE.md` §"Layout/UI work") — two commands; this SDK rejects more than one
project path per invocation:
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. What this is, and what it is not

**Non-Manhattan *geometry* has been supported since L1.** `AngleMode.AnyAngle` lets a user draw at any
angle; `LayoutClipper`, DRC and the MoM path all already cope — `src/Engine/Mom/PlanarCellRegion.cs`
opens by naming the deviation it measured on non-Manhattan artwork. Only *placement* is quantized.

**This brief is placement catching up with drawing.** It adds no geometry capability, no rendering
capability, and touches no numerics. If a change in this phase reaches `src/Engine`, the phase has gone
wrong.

**It is also small, and the reason is architectural.** `LayoutInstance.Rot` has 26 use sites across 13
source files, and 21 of them are `Rot = inst.Rot` field copies that do not care what the type is. The
five that actually branch on the value are:

| Site | What it does |
|---|---|
| `LayoutInstanceTransform.cs` (lines 36, 57, 95, 154) | the canonical transform — three switches plus `RotToInt`/`IntToRot` |
| `LayoutPortDirection.cs:673` | carries a pin's outward direction through an instance |
| `GdsiiTransformCodec` / `DxfTransformCodec` | `LayoutRotation` ↔ the wire format's own angle |
| `LayoutEditorViewModel.Rotate.cs:198` | the rotate command |
| `LayoutShapePropertiesViewModel.cs:832/937` + `LayoutShapePropertiesView.axaml:359` | the properties-panel picker |

Everything else — renderer, hit-test, snapping, bbox, flatten, wirebond descent, PCell handles —
derives from `LayoutInstanceTransform`, exactly as that file's header claims it does. That claim was
verified call-site by call-site before this brief was written; **do not weaken it by adding a second
rotation path anywhere.**

## 2. The angle

**R-L3d-1. The angle is a `double`, in DEGREES, counter-clockwise, in the layout's own Y-up DBU frame.**
Same convention and same sense `LabelShape.PortDirection` already documents (`R0` = +x̂, `R90` = +ŷ).
Never radians in the model; never a "quadrant plus residual" pair — one number, one meaning.

Normalization is to `[0, 360)`, applied on set, so two instances that look identical compare identical.

## 3. The transform — generalize the five switches, add nothing

**R-L3d-2. Every rotation switch in `LayoutInstanceTransform` becomes the general form, and the four
cardinal angles must reproduce today's tables exactly.** The algebra is already in that file's own doc
comments; this is substitution, not redesign.

- **`TransformPoint` / `InverseTransformPoint`** — the 4-case switch becomes
  `rx = mx·cosθ − my·sinθ`, `ry = mx·sinθ + my·cosθ` (and the transpose for the inverse).
- **`PathSpaceLinearCoefficients`** — `(A, B, C, D) = (sx·cosθ, sy·sinθ, −sx·sinθ, sy·cosθ)`.
  Substitute θ = 0/90/180/270 and the existing table falls out term for term. The renderer is
  unaffected: still one `SKMatrix` per placement against a cached cell-local `SKPath`.
- **`ComposeInstances`** — `RotToInt`/`IntToRot` delete. The composition rule is unchanged in form:
  `MirrorC = ⊕`, `MagC = ×`, `θC = mirrorOuter ? θouter − θinner : θouter + θinner`. The complex
  derivation in that method's doc comment holds with `e^{iθ}` in place of `i^Rot`.

**Rewrite the "dihedral group of order 8" paragraph rather than leaving it.** That restriction was a
*consequence* of the enum, never a requirement of the math, and a stale comment asserting a closure
property the code no longer relies on is worse than no comment.

**R-L3d-3. Rounding to integer DBU happens once, at the outermost transform — never once per level of
composition.** At cardinal angles rotation is exact; at every other angle it is not, so rotate-then-
unrotate is not the identity and a deep hierarchy that rounded at each rung would visibly drift.
`ComposeInstances` already has this right (it applies `TransformPoint` once, to the inner origin);
preserve that property and assert it, because it is easy to lose while editing the same method.

State the consequence in the properties panel's own tooltip the way L1h already states its `Mag ≠ 1`
rounding: a non-cardinal angle is not perfectly reversible.

## 4. Persistence — additive, and exactly one accessor

`LayoutPersistence` serializes with `JsonStringEnumConverter`, so today's files carry `"Rot": "R90"`.

**R-L3d-4. `Rot` stays; add a nullable `RotDeg`.** Null means "use `Rot`". This is precisely the
additive pattern `LabelShape.PortDirection` already established — `[JsonIgnore(WhenWritingNull)]`, no
`FormatVersion` bump, every existing `.clay` loads unchanged. On write: when the angle is one of the
four cardinals, write `Rot` and omit `RotDeg` entirely, so a design that never uses a non-cardinal
angle round-trips **byte-identically**.

**R-L3d-5. Exactly one accessor may read the angle, and a test must hold that shut.** Add
`LayoutInstance.RotationDegrees` (get/set, reconciling the two fields), and let nothing anywhere else
read `.Rot` or `.RotDeg` directly except the persistence layer and the accessor itself.

Two fields with one meaning is the drift trap this repo has already paid for once — three copies of the
version number had silently diverged, which is what `VersionSingleSourceTests` now prevents. Do the
same here: a **source-scan test with comments stripped** (the H8 precedent, which exists because an
unstripped scan reports its own documentation as a violation).

**`LayoutGeometry.Clone` (line 243) is a hand-maintained field list** whose own doc comment says it
exists so "a future new field can't be added to one copy and forgotten in another." `RotDeg` is that
future new field. Add it there, and add it to the paste/fragment path if that carries its own list.

## 5. Flatten — this is the actual work of the phase

`LayoutCoordinateWalk.Transform` maps the *corner points* of shapes whose type presumes axis alignment.
That is correct for scale, mirror and 90° rotation, and **silently wrong** for anything else: pushing a
`RectShape`'s `(X1,Y1)` and `(X2,Y2)` through 37° and re-normalizing yields the axis-aligned bounding
box of the rotated rect — plausible-looking output, no error, wrong copper.

**R-L3d-6. A non-axis-aligned flatten promotes the shapes that presume axis alignment:**

| Shape | Under a non-cardinal angle |
|---|---|
| `RectShape` | → `PolygonShape`, four transformed corners |
| `RoundedRectShape` | → `CurveShape` with arc edges (the Polygon↔Curve promotion rule in `LayoutModel.cs`'s header already governs this lineage) |
| `LabelShape` | position transforms; `Rotation` snaps to the nearest cardinal, reported once with a count |
| `BitmapShape` | cannot rotate (min-corner + size). Skipped, reported. `LayoutCoordinateWalk.cs`'s own comment at the bitmap branch already anticipates this exact case |
| `CircleShape`, `ViaShape` | unchanged — both rotation-invariant (the via pad is drawn as a circle, `LayoutRenderer.cs:1204`) |
| `Polygon`, `Curve`, `Path` | unchanged. Bulge is unchanged under rotation, sign-flipped under mirror — L3c's rule, untouched |

**R-L3d-7. The walk must not be able to produce a wrong `Rect`.** Two designs are acceptable — pick one
and record why: (a) the transform carries a "this rotates" flag and the walk's axis-presuming branches
refuse it, with promotion done by the caller beforehand; or (b) a separate promotion pass runs ahead of
the walk. What is *not* acceptable is leaving the walk mapping two corners through a rotating transform
and relying on callers to have promoted first. Silently-plausible wrong geometry is the failure class
this codebase's own history keeps re-learning; make it unrepresentable.

The guard test is the one that catches the specific bug: a `Rect` flattened at 45° has **four distinct
corners** and preserves its area to within DBU rounding. An AABB-of-a-rotated-rect passes a bbox check
and fails this one.

## 6. Interchange — both codecs get smaller

Both wire formats already carry an arbitrary angle. `GdsiiTransformCodec` and `DxfTransformCodec` each
have a `SnapToRotation` helper and a `snappedDeltaDegrees` out-parameter whose entire job is to report
how much of a third-party file's angle *we* discarded.

**R-L3d-8. Delete the snapping and the loss reporting from both codecs, and the callers' reports with
them.** Keep everything else exactly as documented: GDSII's reflect-then-rotate-180 correction still
applies (its axis differs from ours, which is why that trick exists), DXF's mapping is still direct,
and DXF's separate `yScaleMismatch` report is unrelated and stays.

This is a **behaviour change to existing tests** — a third-party 30° `INSERT` that previously imported
as 0° with a loss note now imports as 30°. Update those tests deliberately, asserting the new
behaviour; do not delete them.

**R-L3d-9. Arrays at a non-cardinal angle: change nothing, measure what happens, and write it down.**
Array pitch is deliberately unrotated (`ArrayCellOrigin`'s doc comment), and `GdsiiWriter.WriteInstance`
writes AREF's three reference points literally in that convention on the stated grounds that "a
compliant reader takes these three points as-is." Our own round-trip stays exact. But a reader that
rotates the lattice by `ANGLE` will now disagree **visibly** — a skewed array — where at cardinal angles
the same disagreement was invisible. Write a 3×3 array at 30°, confirm our own reader returns it
identically, open it in an independent viewer, and state in the completion note what that viewer shows.
**Do not "fix" the pitch convention in this brief.**

## 7. UI

**R-L3d-10.** The properties panel's rotation `ComboBox` (`LayoutShapePropertiesView.axaml:359`) becomes
a numeric degrees field with the four cardinals as presets. Multi-instance selection keeps its existing
"mixed" semantics unchanged.

**R-L3d-11. The R key still rotates by exactly 90°, and it *advances* rather than snaps.** Rotating a
30° instance three times gives 300°, not 270°. Rotation is about the selection centre, as today
(`LayoutEditorViewModel.Rotate.cs:198`). A snapping R key would make a non-cardinal placement
un-nudgeable, which is the opposite of what this phase is for.

**Noted, not in scope:** a rotate-drag handle on the selection box. It is the natural gesture and it is
a separate piece of interaction work; do not build it here.

## 8. Port direction — state the limit, do not widen it

`LayoutPortDirection.TransformDirection` returns a `LayoutRotation` and would snap a carried direction
to the nearest cardinal. Note that `LayoutPin.OutwardDeg` is **already a `double`** — the pin model is
fine; only the carried result is four-way.

**R-L3d-12. Carry the direction as a real angle internally and snap only at the boundary that demands a
`LayoutRotation`, reporting the snap.** Do **not** widen `LabelShape.PortDirection` or `PlanarPortSide`
here. Whether an EM port on a non-Manhattan conductor is meaningful is an L8/L9 question about port
extraction; answering it in this brief turns a placement change into an EM change.

## 9. Scope guardrails

- No new shape types, no text rendered at a non-cardinal angle, no rotate-drag handle.
- No change to array pitch semantics, the instance cache, or L3b's invalidation.
- No change to the EM, mesh or DRC paths — all three consume flattened geometry and already handle
  non-Manhattan artwork.
- No PCell changes: generators keep emitting cardinal placements.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 10. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses except
   the two interchange tests R-L3d-8 deliberately changes.
2. **Cardinal identity, bit-for-bit.** For all 8 mirror × cardinal-rotation combinations,
   `TransformPoint`, `InverseTransformPoint`, `PathSpaceLinearCoefficients` and `ComposeInstances`
   return **exactly** today's values — pinned against a written-out table of expected numbers, not
   against the old implementation. This is the test that proves every existing design is untouched.
3. **Every existing `.clay` round-trips byte-identically** (`LayoutPersistence.Serialize` equality) and
   no file gains a `RotDeg` key.
4. **Render.** A 30° instance renders pixel-identically to the same cell flattened at 30° (L3c gate-2's
   pattern).
5. **Inverse.** `InverseTransformPoint(TransformPoint(p))` returns `p` within 1 DBU at 30°, 45° and
   137.5°, for mirrored and unmirrored, at `Mag` 1 and 2.
6. **Composition.** outer 30° ∘ inner 20° = 50°; with the outer mirrored, 10°. A two-deep hierarchy
   renders pixel-identically to the single composed instance.
7. **Promotion (R-L3d-6).** Rect at 45° → Polygon, four distinct corners, area preserved; RoundedRect →
   Curve; label snapped with a note; bitmap skipped with a note; circle and via unchanged; bulge
   unchanged under rotation and sign-flipped under mirror.
8. **Interchange.** A 30° instance writes `ANGLE 30` / `INSERT` rotation 30 and reads back 30 with no
   loss report; a third-party file at 30° imports at 30°.
9. **`LayoutGdsiiTransformTests`' all-8-combination comparison still passes unmodified.**
10. **Single accessor (R-L3d-5).** A comment-stripped source scan finds no read of `.Rot`/`.RotDeg`
    outside `LayoutPersistence` and the accessor.
11. **Counter, not clock.** A 500-instance layout at non-cardinal angles builds the same number of paths
    (`PathsConstructed`) as the same layout at cardinal angles — proving arbitrary angles did not defeat
    the instance cache. **No wall-clock assertion** (root `CLAUDE.md`'s benchmark rule).

## 11. On completion

Write a **"Phase L3d — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not** `CLAUDE.md`.
Call out:

1. **The cardinal-identity proof** (gate 2) and the fact that the generalized formulas reduce to the old
   tables term for term.
2. **R-L3d-6's promotion table** — specifically which shapes cannot rotate and what the user is told.
3. **R-L3d-5's two-fields-one-accessor rule** and the scan test that holds it.
4. **What an independent viewer shows for a non-cardinal AREF** (R-L3d-9), stated as a measurement.
5. The deferred items: rotate-drag handle, port direction beyond the boundary snap, text at a
   non-cardinal angle.
