# Brief — fix circuitRF's own GDSII reader and writer

**Tag:** `R-gnf-n` · **Phases:** F1 → F2 → F3 → F4
**Precedent:** the GDSII interchange (`brief-L4a-gdsii-interchange.md`), the gdstk G0 spike that found these defects
(`docs/design/oasis-gdstk-findings.md` § Q4, `src/Design/RESOLVED.md` "OASIS and gdstk — spike")
**Area:** `src/Design/Layout/Interchange/` (`GdsiiReader.cs`, `GdsiiWriter.cs`, `GdsiiRecordIo.cs`,
`GdsiiCoordinateValidation.cs`, `GdsiiExport.cs`), `tests/Ui.Tests/` (the `Gdsii*` and `LayoutGdsii*` classes)
**Holds:** none; every decision in §0a is made. **Land this before G2 of
`brief-oasis-gdstk.md`**: G2's byte-identity snapshot of native export must be taken after these fixes, because F3
changes the bytes the native writer produces.

---

## 0. Why this brief exists

The gdstk G0 spike read the same GDSII files with circuitRF's `GdsiiReader` and with gdstk 1.0.1, which is an
independent implementation. Six differences were circuitRF's, and reading the code for this brief found a seventh.
Two of them (1 and 3) lose or misplace geometry **without the user being able to tell**. Every one has a
synthetic fixture already committed in `testdata/interchange/gdstk/8a/`, written by gdstk, which is the independent
writer these fixes are tested against.

| # | Defect | Where | Effect today |
|---|---|---|---|
| 1 | An AREF whose column vector points along y is collapsed | `GdsiiReader.ReadRef` | gdstk writes a 90° 3 × 2 array as `COLROW (2,3)`, `XY (0,0  0,3000  6000,0)`. That is legal: the lattice is the same, with columns and rows named the other way. We compute both pitches as 0, so **all 6 instances land at the origin**, and the diagnostic says only "approximated". A genuinely skewed lattice is also approximated, not reproduced. |
| 2 | Layers and datatypes above 32767 read negative | `GdsiiReader` (`AsInt16Array()[0]`) | 40000 reads as −25536. Our writer writes 40000 correctly, so our own round trip breaks. The writer also wraps any value outside 0–65535 silently. |
| 3 | `BOX` elements are dropped with no message | `GdsiiReader.ReadOneStructure` (`default:` branch) | The geometry is gone, and nothing says so. `NODE` elements are skipped the same silent way (they carry no geometry, but the user is not told). |
| 4 | The `UNITS` record is written and read against the spec | `GdsiiWriter.Write`, `GdsiiReader.ReadPreamble` | The spec's first real is **the database unit in user units** (0.001 for 1 nm in µm). We write the user unit in metres (1e-6) and read the first real back as "metres". Geometry is right, because only the second real reaches it, but every other reader derives a **1 mm** user unit from our files. Our round-trip test passes only because the writer and reader share the mistake. |
| 5 | A TEXT element's `MAG` is ignored silently | `GdsiiReader.ReadText` | A label with magnification 2 arrives at half its size, with no message. (The mirror is already reported.) |
| 6 | A TEXT element's `TEXTTYPE` other than 0 or 1 is lost | `GdsiiReader.ReadText`, `GdsiiWriter.WriteText` | TEXTTYPE 5 becomes "not a port", and the number is gone. |
| 7 | **A label's own datatype never reaches the file** (found while writing this brief) | the same two | The writer writes `TEXTTYPE` from `IsPort` alone, and the reader rebuilds the key as `LayerKey(layer, 0)`. So a label on `(31, 5)` comes back on `(31, 0)` **through our own round trip**, and a PDK's label-purpose datatype is never seen. `PinInference` matches label purposes by `LayerKey`, so pin names on a non-zero label datatype can never be inferred from an imported file. |

### 0a. Decisions

| # | Decision | Status | Why |
|---|---|---|---|
| D1 | **A label's `LayerKey.Datatype` is GDSII's `TEXTTYPE`**, in both directions. | Decided | TEXTTYPE is to TEXT what DATATYPE is to BOUNDARY. That is how every layer map treats it, how gdstk holds it, and how `brief-oasis-gdstk.md` §6c expects "text type → the layer's text purpose" to work. It fixes 6 and 7 together, and lets the layer-mapping dialog and `PinInference` see the real `(layer, texttype)`. |
| D2 | **An AREF is read as the lattice its three points define**, not as two pitches: an axis-aligned lattice in either orientation maps exactly to `Rows`/`Cols`/`PitchX`/`PitchY`; any other lattice is **expanded into single instances**, exactly, with a counted message. | Decided | Our array model keeps its pitch in the parent's unrotated frame (`LayoutInstanceTransform.ArrayCellOrigin`). A lattice that is not axis-aligned cannot be stored as one array, but it can be stored exactly as N instances. Approximating geometry is the one outcome an importer must never choose silently. |
| D3 | **`IsPort` travels as a GDSII property on the TEXT element**: `PROPATTR 126`, `PROPVALUE "circuitrf:port"`. TEXTTYPE stays the datatype (D1). The reader sets `IsPort` from that property only. | Decided (owner) | D1 takes TEXTTYPE away from the port flag, so the flag needs another carrier. A property is the format's own mechanism for per-element data that others may ignore: gdstk keeps it, and a reader that does not know it drops it harmlessly. Attribute 126 is high in the user range to keep clear of the low attribute numbers some tools use for net names. The alternative, keeping `TEXTTYPE 1 = port`, was rejected because it turns every label on datatype 1 into a port. |
| D4 | **No legacy convention.** A file written by circuitRF before this brief is read like any other file: its port labels (`TEXTTYPE 1`) arrive as ordinary labels on datatype 1, and there is no fingerprint, no special case and no message. | Decided (owner) | Earlier exports are not worth a special case in the reader. A rule keyed on one writer's past mistake would stay in the code forever for files that matter less every month. |
| D5 | **`MAG` on TEXT scales `Height`** (`Height = WIDTH-or-default × MAG`). The writer keeps writing `WIDTH = Height` and no `MAG`. | Decided | `LabelShape` has a height and no magnification. Folding MAG into it keeps the label's drawn size, which is what MAG means, with no `.clay` format change. Our own files never carry MAG, so their round trip is unchanged. |
| D6 | **`BOX` is read as a `PolygonShape` on `(LAYER, BOXTYPE)`**, counted in one message. **`NODE` is skipped**, counted in one message. | Decided | gdstk reads BOX the same way. A BOX is a 5-point closed outline by definition. NODE is a connectivity marker with no artwork, but skipping it silently hides that the file carried it. |
| D7 | **Layer, datatype and texttype are read as unsigned 16-bit (0–65535).** The writer **refuses** any value outside that range, and any `COLROW` count outside 1–32767, **before writing a byte**, with the same exception and offender list that `GdsiiCoordinateValidation` already uses for coordinates. | Decided | This is what gdstk and the common readers do. A wrapped value is a different layer, and writing one is a silent loss, which is exactly what the coordinate check already refuses to do. |

---

## 1. Read first

- `docs/design/oasis-gdstk-findings.md` § Q4 and `testdata/interchange/gdstk/README.md`. The `8a/*.json` beside each
  fixture states its source content and what each reader made of it.
- `src/Design/Layout/Interchange/GdsiiReader.cs` and `GdsiiWriter.cs` in full (each about 300 lines), plus
  `GdsiiRecordIo.cs`, `GdsiiCoordinateValidation.cs`, `GdsiiExport.cs` (where `GdsiiUnits` is built) and
  `GdsiiImport.cs` (which reads every structure before it creates anything).
- `LayoutInstanceTransform.ArrayCellOrigin` and the `LayoutInstance` array fields; `LabelShape`;
  `PinInference.Infer` (it matches label purposes by `LayerKey`).
- `src/Design/Layout/Interchange/DxfWriter.cs` ≈ line 1199: its port marker comment cites `GdsiiWriter`'s TEXTTYPE
  convention. Correct the comment to point at D3's property; DXF's behaviour does not change.
- The GDSII Stream Format manual, release 6.0, as far as the records of §0: `UNITS`, `AREF`/`COLROW`/`XY`, `TEXT`/
  `TEXTTYPE`/`MAG`/`PRESENTATION`, `BOX`/`BOXTYPE`, `NODE`/`NODETYPE`, `PROPATTR`/`PROPVALUE` (properties go after
  an element's other records and before `ENDEL`).

## 2. Method rules

- **One test per claim**, in the existing classes (`GdsiiImportTests`, `LayoutGdsiiRoundTripTests`,
  `LayoutGdsiiExportTests`). Run only those classes (`dotnet test tests/Ui.Tests --no-build --filter …`). Read the
  TRX rather than re-running. No timing tests. Never run the full suite.
- **Test against the independent writer's files.** Each fix has a gdstk-written fixture in
  `testdata/interchange/gdstk/8a/`. "Our reader reads gdstk's file to its stated source" is the claim that matters,
  because our own round trip passed for defect 4 while both sides were wrong. **Those files are fixed data and are
  never regenerated.** The `*.circuitrf.gds` files there were written by the pre-fix writer; leave them as they are,
  and do not test the fixed reader against them, because they hold the old conventions (D4).
- **A round-trip test alone never proves a format fix.** Pair every changed writer behaviour with a test that reads
  the written bytes at record level (`GdsiiRecordReader`), so writer and reader cannot agree on a mistake again.
- Messages are **returned**, never posted (`src/Design/CLAUDE.md`): they go in `GdsiiReader.Diagnostics` or the
  export summary's diagnostics, and `GdsiiImport` already forwards both.
- **No `.clay` format change.** No model field is added. If a fix seems to need one, stop and ask.

---

## 3. F1 — the reader, geometry (`R-gnf-1`)

**a. AREF lattice (D2).** In `ReadRef`, with `P0`, `Pc`, `Pr` the three points and `cols`, `rows` from `COLROW`:
`vc = (Pc − P0) / cols`, `vr = (Pr − P0) / rows`.
- If a division is not exact, that is a malformed AREF: expand under the rule below and say so.
- If `vc = (a, 0)` and `vr = (0, b)`: `Cols = cols`, `PitchX = a`, `Rows = rows`, `PitchY = b` (today's case).
- If `vc = (0, b)` and `vr = (a, 0)`: **swap**, giving `Cols = rows`, `PitchX = a`, `Rows = cols`, `PitchY = b`.
  This is gdstk's spelling of a 90°/270° array and is exact.
- If `cols == 1` or `rows == 1`, the unused vector does not matter (writers disagree on what they put there). Map the
  used vector to whichever axis it lies along.
- Anything else (a skewed or rotated lattice) is **expanded into `cols × rows` single instances**, each with the
  AREF's own `STRANS`/`MAG`/`ANGLE`. Add one message per structure:
  *"AREF "<cell>": its lattice is not axis-aligned; placed as N separate instances."*
- **Expansion limit.** The reader keeps a running count of instances created by expansion across the whole file.
  Above **100,000** it throws `InvalidDataException` naming the count and the limit, and **nothing is created**:
  `GdsiiImport` reads every structure before it creates anything, so this already holds — prove it with a test. The
  number is a proposal; the owner may set another, and `brief-oasis-gdstk.md` R-oas-4c may later unify the two.
- `ReadRef` returns a list, or the caller expands; the rest of the reader is unchanged.

**b. Unsigned layer numbers (D7).** Layer, datatype, texttype and boxtype are read as `(ushort)` values into
`LayerKey`'s `int`s. `COLROW` stays signed, and a non-positive count is a malformed AREF (`InvalidDataException`).

**c. BOX and NODE (D6).** Add `Node = 0x15`, `NodeType = 0x2A`, `Box = 0x2D` and `BoxType = 0x2E` to
`GdsiiRecordType`. Read `BOX` as a `PolygonShape` on `(LAYER, BOXTYPE)`, dropping the closing point as
`DropClosingDuplicate` does. Skip `NODE` to its `ENDEL`. Add one message each per file:
*"N BOX element(s) read as polygons."*, *"N NODE element(s) skipped: they carry connectivity, not artwork."*

**Gate F1:** `aref-rotated.gdstk.gds` reads to 6 instances at its source's 6 positions, as one 3 × 2 array; a
hand-built skewed AREF reads as `cols × rows` instances at exactly the lattice points, with the message; an AREF
over the limit is refused and creates nothing; `layer-40000.gdstk.gds` and `datatype-40000.gdstk.gds` read 40000;
`box-record.gds` reads both polygons, with the message. The existing `GdsiiImportTests` and `LayoutGdsii*` classes
pass unchanged.

## 4. F2 — the reader and writer, labels (`R-gnf-2`)

**a. Read.** `LabelShape.Layer = LayerKey(LAYER, TEXTTYPE)` (D1). `IsPort` is set from the D3 property only, never from
`TEXTTYPE` (D4). `Height = (WIDTH if present, else DefaultTextHeightDbu) × MAG` (D5), rounded to an integer DBU.
Read `PROPATTR`/`PROPVALUE` pairs on TEXT; ignore any attribute other than D3's.

**b. Write.** `TEXTTYPE = label.Layer.Datatype`, and for a port label, after `STRING` and before `ENDEL`:
`PROPATTR 126`, `PROPVALUE "circuitrf:port"`. Nothing else in `WriteText` changes.

**Gate F2:** `label-texttype.gdstk.gds` reads texttype 1 and texttype 5 as datatypes 1 and 5, with no port.
`labels.gdstk.gds` reads the label "big" at twice the height of "east". A label on `(31, 5)` round-trips through
our writer and reader on `(31, 5)`. A port label round-trips as a port (`PortLabel_RoundTrips_IsPortSurvives` stays
green), and its written TEXT carries the property at record level.

## 5. F3 — the writer (`R-gnf-3`)

**a. UNITS (defect 4).** Write `[DbUnitMeters / UserUnitMeters, DbUnitMeters]`. Read
`UserUnitMeters = v[1] / v[0]` and `DbUnitMeters = v[1]`, and treat a non-positive real as malformed
(`InvalidDataException`). `GdsiiUnits`, `SourceDbuPerMicron` and `GdsiiExport` are unchanged: the struct's meaning
was always right, and only its serialization was wrong. A file written before the fix reads with a 1 mm user unit,
which nothing downstream uses.

**b. Range refusals (D7).** `GdsiiCoordinateValidation.CheckOverflow` (or a sibling it calls) also names every
shape whose layer, datatype or (D1) label datatype is outside 0–65535, and every instance whose `Cols`/`Rows` is
outside 1–32767. It reuses `GdsiiExportException`, and nothing is written.

**Gate F3:** reading any `*.gdstk.gds` (a spec-conformant writer) gives `UserUnitMeters` = 1e-6. Our own output's
`UNITS` record, read at record level, holds 0.001 and 1e-9 for 1000 DBU/µm, and 0.00025 and 2.5e-10 for 4000,
each within 1e-15 relative: `1e-9 / 1e-6` is not exactly 0.001 in double precision, and gdstk's own file reads
as `0.0009999999999999998`. A
layer of 70000 and a negative datatype are each refused before any byte is written. A layer of 40000 round-trips.
`ConvertCliVerbTests`' GDSII byte gate stays green, because the CLI and the in-process export change together.

## 6. F4 — records (`R-gnf-4`)

- `src/Design/RESOLVED.md`: under the "OASIS and gdstk — spike" section's list of six, add one line per defect
  saying it is fixed and naming its test. Record defect 7 and D1–D7 as decided.
- `docs/design/oasis-gdstk-findings.md` § Q4: add one line under the findings saying they were fixed by this brief.
  Do not rewrite the spike's numbers; they describe what the spike measured.
- `testdata/interchange/gdstk/README.md`: one line saying the `*.circuitrf.gds` files were written by the pre-fix
  writer, so they hold its `UNITS` value and its `TEXTTYPE 1` port convention.
- `docs/user/src/reference/layout-editor.md` (GDSII import/export): describe how label text types, BOX, NODE and
  skewed arrays are handled now, and the port property. Describe what happens, with no change history, and do not
  regenerate `docs/user`.
- `CLAUDE.md` is not edited.

## 7. Scope

- **In:** the seven defects above; their messages; their tests.
- **Out:** a label mirror field (it is reported today and needs a `.clay` change); `PRESENTATION` (font and anchor,
  which `LabelShape` does not hold. It is ignored today; whether to report it is a question for the owner, not part
  of this brief); `PATHTYPE 4` custom extensions (already reported; our model extends by width/2 only); anything in
  `brief-oasis-gdstk.md`.
- **Ask the owner before:** any `.clay` or model change; any change to how DXF marks ports; a different expansion
  limit than §3a's proposal; anything that changes the bytes of an export beyond `UNITS`, the label `TEXTTYPE` and
  the port property.

## 8. Phase gates, in order

| Phase | Done when |
|---|---|
| F1 | AREF lattice, unsigned layers, BOX/NODE: the F1 gate green; the existing GDSII classes unchanged and green |
| F2 | the F2 gate green |
| F3 | UNITS and range refusals: the F3 gate green; `ConvertCliVerbTests` green |
| F4 | records written |
