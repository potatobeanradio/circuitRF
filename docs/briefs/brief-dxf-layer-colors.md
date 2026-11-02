# Sonnet Brief — DXF layer colours, both directions

Two owner questions after L4b: are circuitRF layer colours exported to DXF (they don't appear in QCAD), and
what happens on import when a DXF layer isn't in the `.ctech`?

**Both answers are in the code, and QCAD is not the problem.**

**Sequencing:** this brief changes `$ACADVER`, which `brief-dxf-version-support.md` documents. **Land that
one first**, then apply this — and update its conclusion per §1.3, which revises it in one specific respect.

---

## 1. Export: colours are not written at all

### 1.1 The cause

`DxfWriter`'s layer table writes a hardcoded constant:

```csharp
w.WriteString(100, "AcDbLayerTableRecord");
w.WriteString(2, name);
w.WriteInt(70, 0);
w.WriteInt(62, 7);          // ← every layer, always
w.WriteString(6, "CONTINUOUS");
```

Group code 62 is the AutoCAD Color Index, and **7 is the default black/white**. Every exported layer is
therefore the same colour. QCAD is rendering the file correctly; the file has no colours in it.

Also verify that entities do **not** write their own colour. An entity omitting 62 inherits `ByLayer`, which
is what makes layer colours take effect. If any entity writes an explicit colour, the layer table becomes
decorative.

### 1.2 The constraint that shapes the fix

**AC1015 (R2000) supports only indexed colour** — 255 palette entries, so a `LayerDef`'s exact RGB cannot be
represented and the best available is a nearest match.

**True 24-bit colour — group code 420, any RGB — arrived with AC1018 (R2004) and is unchanged in every
version since.** R2018 (AC1032) offers exactly the same colour capability as AC1018: 420 is 420. There is no
further colour tier to unlock, and nothing about colour argues for AC1032 over AC1018.

### 1.3 This revises `brief-dxf-version-support.md`, and I got that partly wrong

That brief concluded "nothing added between AC1015 and AC1032 improves 2D interchange." That reasoning was
about **geometry entities**, where it holds — and it overlooked colour, which is not geometry. **AC1018
buys exact layer colours**, and that is a real gap in the earlier answer.

**R-col-1. Support three versions and let the user choose:**

| Version | Colour | Notes |
|---|---|---|
| **AC1015** (R2000) | ACI index only (62) | Widest compatibility; colours approximate |
| **AC1018** (R2004) | 62 **and** 420 — exact RGB | Full colour with near-universal reader support |
| **AC1032** (R2018) | 62 **and** 420 — exact RGB | **Default.** Same colour as AC1018; newest header |

Always write **both** 62 (nearest ACI) and 420 (exact RGB) on the two versions that support 420, so readers
that understand true colour use it and older ones fall back to the index. AC1015 emits 62 only.

**Default to AC1032 (R2018)**, per the owner's direction. Record alongside it, in the code comment and the
docs, that **this is a product choice rather than a colour one** — AC1018 delivers identical colour with
broader reach, so if a compatibility complaint ever arrives, changing the default is a one-line change and
not a colour regression. Someone will otherwise re-derive this question from scratch.

The writer's `AcadVersionCode` and `FormatDescription` constants already exist as the single source of truth
for the version string; they become a small per-version table rather than two constants. Every UI surface and
all documentation reads from it.

**R-col-1a. The export dialog remembers the chosen version for the session.** A user exporting repeatedly
for one downstream tool should pick the version once. Session-scoped, alongside the existing view-mode
choice from §2A — not persisted to disk, not per-document.

**Verify AC1032 output actually opens.** Newer readers can expect header variables and object sections a
minimal writer omits. A version bump that produces an unreadable file is worse than an approximate colour, so
confirm each of the three versions loads in a real reader before calling this done.

**R-col-2. An ACI palette table is required, and it is fixed data.** The 256-entry AutoCAD Color Index is a
standard, unchanging table. Embed it once and use it for **both** directions: nearest-match on export, and
index→RGB on import (§2). Do not approximate it with a formula — entries 1–9 and 250–255 are not on any
regular grid.

## 2. Import: the layer table is never read

`DxfReader` takes a layer **name** from each entity's group 8 and nothing more. **It does not parse the
`LAYER` table at all**, so colour, linetype and flags are simply unavailable — which is why an imported
layer can only ever get a generated colour.

**R-col-3. Parse the `LAYER` table** — name, colour (62), true colour (420 when present), and the frozen/off
flags — and carry it alongside the geometry into reconciliation.

### 2.1 What happens today, and what should

The owner asks whether the user is prompted. Two rules currently disagree:

- §8's cross-cutting import rule says unknown layers are *"auto-created with generated names and reported."*
- L1g's **R-L1g-2** requires confirmation whenever any row is `NoMatch` — which an unknown DXF layer is.

**R-col-4. Resolve in favour of prompting, with the DXF's own values pre-filled.** L1g's dialog appears, and
for each unmatched layer the **"Add to technology"** action is pre-selected and pre-filled with **the DXF
layer's name and its colour**. The common case is then one click, the user can override any row, and nothing
is invented that the source file already told us.

Note this **changes L1g's default for the import path specifically**: L1g chose *Keep as unknown* as the safe
default, which is right for a paste between circuitRF layouts. For a DXF import it is the wrong default,
because a DXF's layer names and colours are the author's deliberate intent, not incidental metadata. Record
the divergence and its reasoning so the two don't get "unified" later.

### 2.2 Colour fallback

**R-col-5. ACI 7 means "black or white, depending on background" — never take it literally.** A layer arriving
as colour 7 (including every layer in a DXF this application exported before §1 is fixed) must fall back to
`FallbackPalette`, not become black. The same applies when the table is absent or the layer is missing from
it. Rendering an entire imported design in black would look like a colour bug, which is where this brief
started.

## 3. Guardrails

- No changes to the entity mappings, the bulge identity, `SPLINE` export, arrays, or the §2A view/extents
  work.
- Do not write group 420 into an AC1015 file — that path emits ACI only.
- **Do not add R12 (AC1009) output.** `brief-dxf-version-support.md`'s reasoning against it is unaffected by
  colour: it lacks LWPOLYLINE, ELLIPSE, SPLINE and HATCH, which costs geometry fidelity.
- Reuse L1g's `LayoutLayerMapping` and dialog; do not add a DXF-specific reconciliation.
- Don't touch GDSII, `src/Core`, `src/Engine`, or `RfCore`.

## 4. Gate

Gate command is plain `dotnet test`.

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Colours are written** — export a layout whose layers have distinct colours; assert the `LAYER` table
   carries a per-layer 62 **and** 420, that no two differing layers share an index, and that entities are
   `ByLayer`.
2a. **All three versions emit correctly** — AC1015 writes 62 only and never 420; AC1018 and AC1032 both
   write 62 and 420 with identical colour bytes (they differ only in `$ACADVER`); each of the three **opens
   in a real reader** without error. Record the reader and version.
2b. **Version choice persists for the session (R-col-1a)** — pick AC1015, export, reopen the dialog: the
   choice is still AC1015. It resets on restart and is not stored per document.
3. **Nearest-ACI mapping** — a table of known RGB values maps to the expected indices; the palette's
   irregular low and high entries are covered explicitly.
4. **Round-trip** — export with colours, re-import, and assert each layer's colour matches the original
   **exactly** via group 420 (not merely the nearest index).
5. **AC1015 option** — emitting AC1015 writes 62 only, never 420, and the dialog says colours are
   approximate. The default offered is **AC1032**.
6. **Layer table is read (R-col-3)** — an imported DXF's layer names, colours and true colours are captured.
7. **Import prompt (R-col-4)** — a DXF with layers absent from the `.ctech` opens L1g's dialog with
   "Add to technology" pre-selected and the DXF's name and colour pre-filled; accepting creates layers with
   exactly those values; overriding a row is honoured.
8. **Colour 7 fallback (R-col-5)** — importing a DXF whose layers are all colour 7 produces `FallbackPalette`
   colours, not black. Use a file exported by the *current* (pre-fix) writer as the fixture.
9. **Third-party check** — open a colour-bearing export in QCAD, and record what it shows. This is the
   question that started the brief, so answer it with an observation rather than an inference.

## 5. On completion

**1. Update `docs/design/layout-view.md` §8 — this is a required deliverable, not a footnote.** The DXF row
and the surrounding notes currently describe a single-version, colourless exporter. Replace with:

- **the three supported write versions** and what each carries — AC1015 (ACI only, approximate colour),
  AC1018 and AC1032 (exact 24-bit colour via group 420), with **AC1032 as the default**;
- that **AC1018 and AC1032 are colour-identical**, so the default is a product choice and not a fidelity one
  — stated in the doc so the question is not re-litigated;
- that **layer colours round-trip exactly** through 420, which is a fidelity claim §8 does not currently
  make for any format;
- the **import** behaviour: the `LAYER` table is read, unmatched layers prompt through L1g's dialog
  pre-filled with the DXF's own name and colour, and ACI 7 falls back to the generated palette;
- fold the outcome into §8's existing "curve and hole fidelity" paragraph so **colour** sits alongside curves
  and holes as a third thing the export dialog reports.

Also reconcile `brief-dxf-version-support.md`'s matrix so no reader is left with its superseded
"AC1015, nothing newer is worth it" conclusion.

**2. Record in `src/Ui/CLAUDE.md`:** that layer colour was hardcoded to ACI 7; the three-version table and
that **AC1032-as-default is a product decision, changeable in one line without any colour regression**; where
the ACI palette table lives and that it serves both directions; **R-col-4's import default diverging from
L1g's paste default, with the reason**; and R-col-5's colour-7 rule.
