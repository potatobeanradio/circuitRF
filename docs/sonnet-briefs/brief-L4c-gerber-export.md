# Sonnet Brief — Phase L4c: Gerber RS-274X / X2 export and Excellon drill

**Design:** `docs/design/layout-view.md` §8 (interchange, R15), §3.1a (holes), §3.1b R10e (bitmaps never
exported), §2.4 / R-L4a-1 (the `.ctech` interchange mappings), §3.4 (nets). **Consumes L4a's shared
interchange layer** — the neutral model, the `.ctech` mappings and L1g's reconciliation already exist.

**This closes Phase L4.** Export only — **no Gerber import**, per §8 and reaffirmed by R-menu-5 in
`brief-file-menu-restructure.md`, which deliberately omits a Gerber entry from the Import submenu entirely.
Do not add one, and do not build a reader.

Gate command is plain `dotnet test`.

---

## 1. The coordinate mapping is exact — build on it

**Gerber's number format is declared, not fixed.** With

```
%MOMM*%              ← millimetres
%FSLAX46Y46*%        ← leading zeros omitted, absolute, 4 integer + 6 decimal digits
```

one output unit is **10⁻⁶ mm = 1 nanometre**, which is exactly one DBU at the default
`DbuPerMicron = 1000`.

**R-L4c-1. Emit `%MOMM*%` with a 6-decimal format so DBU maps to output units by integer copy.** No scaling,
no rounding, no accumulated error — the same kind of win as DXF's bulge identity in L4b. Four integer digits
covers 9,999 mm, comfortably beyond any board.

If a layout's `DbuPerMicron` is finer than 1000, widen the decimal count to match rather than rounding, and
**report** the format used. Never silently round coordinates into the declared format — that is the failure
mode this rule exists to prevent.

## 2. File set, naming, and X2 attributes

**One file per layer** (§8), driven by the `.ctech` interchange mapping (R-L4a-1) which already carries each
layer's **Gerber file suffix and X2 file function**.

**R-L4c-2. Emit X2 attributes; they are what let a fab identify files without a README.**

- `%TF.FileFunction,Copper,L1,Top*%` / `Soldermask,Top` / `Legend,Top` / `Profile,NP` etc., from the mapping.
- `%TF.FilePolarity,Positive*%`.
- `%TF.GenerationSoftware,circuitRF,<version>*%` and `%TF.CreationDate,<ISO8601>*%`.
- **`%TO.N,<net>*%` object attributes** where a shape carries a net (§3.4). This is Gerber's own net
  vocabulary and it costs nothing — it is what makes a fab's netlist extraction agree with the design.

Also emit a **`.gbrjob` job file** listing the set. It is the X2 answer to "which files belong together,"
which §8 named as the hard part of Gerber, and it is cheap to write. If it proves awkward, report and skip —
but the individual files must still be complete.

## 3. Geometry mapping

| Ours | Gerber | Notes |
|---|---|---|
| `Polygon`, `Curve`, `Rect`, `RoundedRect` | `G36`/`G37` **region** | Fully general |
| Arc edges | **`G02`/`G03` inside the region** | Curves stay curves (§8) |
| `Circle` | **aperture flash** (`%ADD..C,<d>*%` + `D03`) | Semantically a pad; a fab reads it as one |
| `Path`, round caps | **`D01` stroke** with a circular aperture | Parametric and compact |
| `Path`, other end styles | **region outline** | See R-L4c-4 |
| Holes (§3.1a) | `%LPC*%` clear region after the `%LPD*%` dark one | Gerber's native mechanism |
| `Via` | pad flash **+ Excellon hit** (§5) | |
| `Label` | **stroked-font geometry** | R-L4c-5 |
| `Bitmap` | omitted | §3.1b R10e |

**R-L4c-3. Emit `G75` before any arc, always.** Multi-quadrant mode. `G74` single-quadrant is deprecated,
and the I/J offsets mean different things between the two — an arc written without `G75` is a silent,
plausible-looking wrong shape, which is the worst class of interchange defect. The I/J offsets are relative
to the arc's **start point** and are signed in multi-quadrant mode.

**R-L4c-4. `Path` end styles do not survive stroking.** A `D01` stroke with a circular aperture produces
round caps inherently — correct for `Round`, wrong for `Flush`, `Square` and `Extended`. Those must be
emitted as a **region outline** built through Clipper2's offset (L1e's `ToClipperPaths` path outline, *not*
the display outline — the R-L1e-1 split applies here). Prefer the stroke when caps are round, since it is
smaller and reads as a trace; fall back to the region otherwise. Report which was used per layer.

**R-L4c-5. Labels become geometry, not nothing.** Gerber has no text. A silkscreen layer that exports
without its legend is useless, so convert labels using the **same stroked vector font** as the
label-flattening work (`SkiaFonts.PlexRegular`), so on-screen and shipped text agree. Report the count.
Port labels (`IsPort = true`) are markers, not artwork — **omit them** and report separately.

**Aperture table hygiene:** dedupe apertures by (shape, size). A naive writer emitting one aperture per
object produces files that are large and that some CAM tools reject outright.

## 4. Hierarchy flattens — reuse, do not rewrite

**Gerber has no hierarchy at all** (§8). Instances and arrays must be flattened on export.

**R-L4c-6. Flatten through L3c's existing machinery**, including its affine coordinate walk (R-L3c-2) and its
cross-technology layer reconciliation (R-L3c-3). A sub-cell from a different technology hitting a Gerber
writer has exactly the same `(1,0)`–`(8,0)` collision hazard as everywhere else, and L1g's mapping is the
answer here too. **Do not write a second flattener.**

Report the flattened object count — an array that expands to 2,500 placements is worth telling the user
about before they send the files to a fab.

## 5. Excellon drill

Separate format, separate file(s): `M48` header, `METRIC` units, tool definitions (`T1C0.300`), body of
`T<n>` selections and `X…Y…` hits, `M30`.

- **Plated and non-plated holes go in separate files** by convention; take the distinction from the `.ctech`
  mapping if it carries one, otherwise emit a single plated file and say so.
- Dedupe tools by diameter, exactly as apertures are deduped.
- Coordinate format must be declared and consistent with the Gerber files — the same integer-copy discipline
  as R-L4c-1.
- `Via` contributes both a **pad flash** in copper and a **drill hit** here. Neither alone is a via.

## 6. Report before writing

**R-L4c-7. The export dialog states every conversion before anything is written**, per §8's fidelity note and
consistent with GDSII (R-L4a-3) and DXF. Counts for: Bézier edges flattened, hierarchy flattened, labels
converted to geometry, port labels omitted, bitmaps omitted, paths emitted as regions rather than strokes,
and the coordinate format chosen.

When every count is zero, **show nothing** — the same rule the testing-fixes brief applied to GDSII. A dialog
that says "nothing will change" trains people to dismiss dialogs unread.

## 7. Guardrails

- **No Gerber import.** No reader, no Import menu entry (R-menu-5).
- No new geometry model changes, no new flattener, no second path-outline mechanism.
- Do not add a Gerber library dependency — write from the public specification.
- Do not reuse the *display* path outline for geometry (R-L1e-1).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 8. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Coordinates are exact (R-L4c-1)** — a shape at a known DBU coordinate appears as the identical integer
   in the output; a round-trip through the emitted numbers loses nothing. A finer `DbuPerMicron` widens the
   format rather than rounding.
3. **Arcs (R-L4c-3)** — `G75` precedes every arc; a 90° arc inside a region renders as an arc, not a chord,
   in a third-party viewer; I/J offsets are relative to the start point.
4. **Holes (§3.1a)** — a polygon with two holes emits one dark region and two clear regions, and renders
   with both holes open.
5. **Path end styles (R-L4c-4)** — round caps emit a `D01` stroke; `Flush`/`Square`/`Extended` emit region
   outlines whose silhouette matches the modelled outline within tolerance.
6. **Circles flash**; the aperture table is deduped; no aperture is defined twice.
7. **Hierarchy (R-L4c-6)** — a 5×5 array exports as 25 flattened footprints in the correct positions, and a
   sub-cell on a different technology is reconciled through L1g rather than silently remapped.
8. **Labels (R-L4c-5)** — a silkscreen label appears as stroked geometry; a port label does not appear, and
   both are reported.
9. **Excellon (§5)** — tool table deduped, hits at the right coordinates, header and `M30` well-formed; a
   `Via` produces both a copper flash and a drill hit.
10. **X2 attributes (R-L4c-2)** — `FileFunction` matches the `.ctech` mapping per file; net attributes appear
    for shapes carrying nets; the `.gbrjob` lists the set.
11. **Silent clean export** — a design needing no conversions shows no dialog.
12. **Third-party check — the gate that matters most.** Open the full output set in an independent Gerber
    viewer (an independent one; record which and its version) and confirm layer alignment, holes open
    where they should be, arcs curved, and drill hits registering with their pads. As with L4a's KLayout gate
    and L4b's CAD gate, this is the only check that catches "correct by our own writer's standards" — and
    Gerber has no reader here to round-trip against, so it is the **only** external check available.

## 9. On completion

Add a "Phase L4c — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: **the exact nm↔output-unit
mapping** and the format declared; **R-L4c-3's `G75`** and why a missing one is silently wrong; the path
end-style split and that it uses the *geometry* outline; that hierarchy flattening reuses L3c including its
cross-technology reconciliation; the label and port-label treatment; the X2 attributes emitted and whether
the job file landed; and **what the third-party viewer actually showed**.

Then state whether **Phase L4 is complete**, and note that §8's Gerber row can be updated from "export only
for v1" to a description of what now exists.
