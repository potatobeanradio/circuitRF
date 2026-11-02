# Sonnet Brief — Phase L4a: interchange scaffolding and GDSII read/write

**Design:** `docs/design/layout-view.md` §8 (interchange, R15), §2.4 (the tech file's interchange
mappings), §3.2 R9e (curves flatten on export), §3.1a (GDSII keyholes holes), §3.1b R10e (bitmaps are never
exported), §1.4 (resolution and rounding). **Consumes all of L1–L3.**

**L4 is three briefs.** This is **L4a: the shared interchange layer plus GDSII read/write** — the format
that maps most closely onto our model and the one the MMIC market needs. Then **L4b** (DXF write
first-class, read a documented subset) and **L4c** (Gerber RS-274X/X2 + Excellon, export only).

**Test loop**, two commands — this SDK rejects multiple project paths in one invocation:
```
dotnet test tests/Ui.Tests --filter "Category!=Nightly" --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The shared layer — build it once, here

**R15 requires all three formats to go through one neutral model and one layer-mapping dialog.** GDSII is
first, so it carries the scaffolding — but write it so DXF and Gerber plug in, not so GDSII owns it.

**R-L4a-1. Interchange mappings live in `.ctech`.** L0a deliberately deferred this field
(*"Interchange mappings (§2.4) are deferred to L4; do not add a placeholder field for them"*) — this is
that moment. Per §2.4: GDSII `(layer, datatype)` ↔ DXF layer name ↔ Gerber file suffix and X2 file
function. Additive and nullable so existing `.ctech` files load unchanged, and editable in the L0d tech
editor.

**R-L4a-2. Layer reconciliation reuses L1g's `LayoutLayerMapping` and its dialog.** Import is exactly the
problem L1g solved — geometry authored against one layer vocabulary arriving in another — and its
name-before-number matching with the match-kind shown per row is precisely what an import needs. Do not
write a second reconciliation. The `.ctech` mapping (R-L4a-1) supplies the proposals; L1g's dialog resolves
what it cannot.

Per §8's cross-cutting rules: imported layers absent from the technology are auto-created with generated
names and reported; and **import creates real cells through the normal `CellFolder` machinery** — a GDSII
library becomes N proper circuitRF cells with layout views, never an opaque blob.

## 2. GDSII — write from the public spec

**Never ingest GPL sources.** The root `CLAUDE.md` licensing rule and §8 both say so; the format is
publicly documented and record-based.

Records needed: `HEADER`, `BGNLIB`, `LIBNAME`, `UNITS`, `BGNSTR`, `STRNAME`, `BOUNDARY`, `PATH`, `SREF`,
`AREF`, `TEXT`, `COLROW`, `STRANS`, `MAG`, `ANGLE`, `XY`, `LAYER`, `DATATYPE`, `TEXTTYPE`, `WIDTH`,
`PATHTYPE`, `BGNEXTN`, `ENDEXTN`, `ENDEL`, `ENDSTR`, `ENDLIB`.

### 2.1 Five things that reliably go wrong

Budget real time for these; they are where GDSII implementations fail, and they fail *silently* — a file
that opens in your own reader but is subtly wrong in a fab's.

1. **8-byte reals are excess-64 base-16, NOT IEEE 754.** `UNITS`, `MAG` and `ANGLE` all use them. This is the
   single most common GDSII bug. Implement the conversion explicitly, and unit-test it against known bit
   patterns in **both** directions before writing anything that uses it.
2. **Coordinates are 4-byte signed integers** in database units: ±2,147,483,647. At our default 1 nm DBU
   that is ±2.1 m — ample, but our storage is `long`. **Validate on export and report any coordinate that
   will not fit**, naming the shape, rather than truncating.
3. **`BOUNDARY` must be explicitly closed** — the first point repeated as the last. Readers that tolerate an
   unclosed boundary hide the bug until a fab's does not.
4. **`STRANS` bit 15 is reflection about the X axis, applied *before* rotation.** Get the order wrong and
   mirrored instances land rotated the wrong way — which looks plausible and is wrong.
5. **`AREF` carries `COLROW` plus three points**: origin, the column reference point, and the row reference
   point — where the reference points are the origin displaced by `cols × pitch` and `rows × pitch`
   respectively, **already transformed**. Not a pitch pair.

### 2.2 Units

`UNITS` holds two reals: the user unit in metres and the database unit in metres. Map to and from
`DbuPerMicron`.

On import, if the source database unit is **finer** than ours, coordinates round — §8's cross-cutting rule
requires warning with the count of affected coordinates. Offer to refine the layout's `DbuPerMicron` instead,
since L0a's `LayoutScaling` already makes refinement lossless.

### 2.3 The four lossy conversions on export

All four are already decided elsewhere; L4a implements them and **reports each with a count**:

| Conversion | Rule |
|---|---|
| Curved primitives → polygons | §3.2 R9e, at each shape's resolved `FlattenTolDbu` |
| Polygons with holes → keyholed single contours | §3.1a — a zero-width slit per inner ring |
| Bitmaps → omitted | §3.1b R10e |
| Labels → `TEXT` records | Metadata, not geometry — a fab sees annotation, not copper |

**R-L4a-3. The export dialog states what will change *before* writing**, per §8's fidelity note: curve
count and tolerance, hole count, bitmaps skipped. Silently different output from the same design is how
trust is lost, and holes are the newest and least expected of these.

### 2.4 Hierarchy

`SREF`/`AREF` map onto `LayoutInstance` almost exactly — this is the format that preserves our hierarchy, so
**do not flatten on export**.

- **Structure names**: the spec's limit is short (§8 notes 200 chars). Cell names must be mangled
  deterministically, collisions resolved, and **the mapping reported** so a user can trace a fab's structure
  name back to their cell.
- **Import cycles**: a malformed or hostile file can contain one. R-L3a-2 already requires load-time
  detection — route imported hierarchy through the same check rather than adding a second.

### 2.5 Streaming

A production GDSII runs to hundreds of megabytes. **Read and write as a stream**; do not materialize the
whole file. L2a measured a 500k-shape `.clay` at 219 MB, so this codebase already has designs at that scale.

## 3. Scope guardrails

- **No DXF (L4b), no Gerber or Excellon (L4c)** — but the neutral model and the mapping dialog must not
  assume GDSII.
- No DRC (L5b), no schematic-to-layout (L5), no mesh/EM (L6+).
- No changes to the geometry model, the flattener, the keyhole logic, or `LayoutLayerMapping` beyond wiring.
- Do not add a GDSII library dependency — write from the spec.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 4. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Real-format conversion (§2.1 item 1)** — excess-64 base-16 ↔ double round-trips for a table of known
   bit patterns including 0, 1, negative values and typical `UNITS` values. Test this **first**.
3. **Round-trip** — a design containing every primitive, a polygon **with holes**, an arc-bearing curve, a
   `Path` with each end type, a `Label`, a plain instance and a 5×5 array exports and re-imports to
   geometry equal to the original **modulo the four documented conversions**. Assert the conversions
   happened rather than asserting byte equality.
4. **Hierarchy survives** — the re-imported design has instances and an array, not flattened geometry, and
   `COLROW` plus the three `AREF` points reproduce the original pitch and counts.
5. **Transform order (§2.1 item 4)** — all 8 rotation/mirror combinations round-trip to the same rendered
   result, verified by off-screen pixel comparison. This is the test that catches reflect-after-rotate.
6. **Boundaries are closed** — every exported `BOUNDARY`'s first point equals its last.
7. **Keyholing (§3.1a)** — an exported polygon with two holes produces one self-touching contour whose
   filled area matches the original within tolerance; the count is reported.
8. **Coordinate overflow (§2.1 item 2)** — a shape beyond int32 range is reported by name and does not
   silently truncate.
9. **Unit mismatch (§2.2)** — importing a finer-resolution file warns with the affected-coordinate count and
   offers refinement; importing a coarser one is silent.
10. **Import creates real cells** — a multi-structure library yields proper cell folders that open as layouts
    and appear in the project tree.
11. **Cycle safety** — a crafted GDSII with a structure cycle imports with the offending instance marked
    broken, and does not throw or overflow.
12. **Third-party check** — export a non-trivial design and confirm it opens correctly in an independent
    viewer (KLayout). Record which viewer and version; this is the only gate that catches "correct by our
    own reader's standards."

## 5. On completion

1. Add a "Phase L4a — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: the excess-64 real
   implementation and its test vectors; the int32 coordinate ceiling and how overflow is reported; the
   `STRANS` reflect-before-rotate order; the `AREF` three-point convention; the structure-name mangling
   scheme and where its mapping is reported; and which parts of the neutral model are genuinely
   format-agnostic versus provisional until L4b/L4c exercise them.
2. Report back before L4b (DXF) is briefed.
