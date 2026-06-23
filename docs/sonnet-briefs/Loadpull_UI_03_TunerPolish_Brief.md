# Brief — Loadpull UI 03: Tuner polish, bias-supply rendering option, deferral docs, verification

**Goal:** Finish the Tuner component: the optional "render the bias supply" affordance, glyph/label polish
on the compact box, the deferred-reference-pin documentation, palette ergonomics, and a full test pass. No
engine work.

**Depends on:** briefs 01 (general Tuner) and 02 (Source/Load variants). Note the design decisions there:
**single pin** (Tuner/Load left, Source right), reference net **hard-coded** (ground for Tuner/Load; an
auto-generated unique internal net for Source), and exposing the reference net as a pin is **deferred**.

**Reads with:** `loadpull.md` §1.1 (the internal bias-tee + bias supply), `standard-library-symbols.md`
(glyph conventions), `parameter-editor.md` (param visibility, Γ-vs-Z entry).

## 1 — The "render the bias supply" option

`loadpull.md` §1.1: a Tuner may embed its own bias-tee (choke + DC-block) and DC bias source. Provide a
per-instance display toggle that draws that embedded bias branch when active, so the schematic shows the
tuner carries its own bias supply rather than the user inferring it.

- Add a boolean param `ShowBias` to the shared tuner default-param list (brief 01 step 5), hidden from the
  schematic label (`ShowOnSchematic: false`), default `"false"`. **Display-only — never reaches the
  engine.** In `NetExtractor.EmitInstance`, the Tuner branch (briefs 01/02) builds `overrides2`; add
  `ShowBias` (and any other display-only key) to the dropped set, exactly as SnP drops
  `RefNode`/`PinConfig`/`Pitch` and NonlinearC drops `CvData`. Read those branches and mirror them.
- When `ShowBias == true` (meaningful only with `BiasTee=on`), the glyph gains a small bias branch teed off
  the **single DUT-facing lead**: a Vdc-style two-bar source + a 2-arc choke, drawn beneath the box. Because
  `BuiltInSymbols` symbols are cached per `SymbolKind`, make this per-instance the SnP way: add
  `BuiltInSymbols.PrimitivesForTuner(SymbolKind kind, bool showBias)` returning a cached with/without-bias
  variant, and have `EditableComponent.ToRenderComponent` / `ComputeGlyphBb` call it for the three tuner
  kinds (mirror the existing SnP branch that varies the symbol by params via `GetSnpBool` /
  `PrimitivesForSnp`). The bias add-on is a **shared** primitive list appended to each base builder (the
  bias-tee hardware is identical across the three kinds; only the base box/motif differs). Keep it modest —
  it annotates an internal detail, not a full schematic.

**Acceptance:** toggling `ShowBias` (with `BiasTee=on`) redraws with/without the bias branch; the extracted
`Instance` is **identical regardless of `ShowBias`** (verify by extraction test).

## 2 — Glyph + label polish

- **Label clearance.** The boxes are 200 tall (±100 local Y); the 2-terminal default `LabelBaseYFor` may
  place type/name labels over the box. Read `SchematicComponent.LabelBaseYFor(Symbol, portCount, glyphMaxY)`
  and add Tuner/SourceTuner/LoadTuner cases (or a glyph-height-aware branch) so labels sit just below the
  box, like the SDD/ZPort tall-symbol handling. Confirm labels clear the lead and the bias glyph.
- **Single-pin balance.** The general Tuner's pin is far left (box centered); confirm the type/name labels
  still anchor sensibly and the symbol reads cleanly. Same for Source (pin right) / Load (pin left).
- **Motif legibility** at 1× and zoomed-out; tweak radii/positions if cramped. The general Tuner stays
  minimal (advanced users want a small footprint).
- **Ghost + palette tile** show the correct glyph for all three (use the no-bias default variant for the
  tile if you took the `PrimitivesForTuner` route).

## 3 — Deferred-reference-pin documentation (required)

Document, in code and docs, that **exposing the reference/source net as a schematic pin is deferred**:
- A comment at the Tuner `SymbolPortDefs.For` cases and at the `EmitInstance` Tuner branch: the single pin
  is the DUT-facing net; the reference (ground for Tuner/Load) or internal source net (Source) is bound at
  extraction, not via a pin; a second pin can be added later if users need a non-ground reference (e.g.
  differential terminations) or to wire a source's outer net to something.
- A sentence in `docs/design/loadpull.md` §1 (alongside the equivalence note from brief 02): the GUI Tuner
  exposes one pin today; the reference/source net is implicit; exposing it as a pin is a deferred
  enhancement.

## 4 — Palette & parameter-editor ergonomics

- All three tiles appear under their categories and in search (briefs 01/02 terms).
- The "+" adds `Z[2]`, `Z[3]`, … (the shared `UserParamTemplate`); added rows show on the schematic and
  round-trip on save/reload.
- **Γ vs Z entry.** The factory accepts `Z[k]` and `G[k]` (+ `Z0` for the Γ form) and errors if a harmonic
  gets both. The "+" produces `Z[k]`. Document (param comment + the tuner doc note) that to enter a
  reflection coefficient the user renames a row to `G[k]` and sets `Z0`. A dedicated Γ/Z picker is OUT of
  scope (note as future polish).

## 5 — Verification checklist

Automated:
1. `dotnet build` zero warnings; `dotnet test` green.
2. Extraction tests: (a) Tuner/LoadTuner emit `[pinNet, "0"]`; SourceTuner emits `[uniqueNet, pinNet]` with
   the source net unique & non-ground; (b) Tuner ≡ LoadTuner netlist except instance name; (c) `ShowBias`
   and other display-only params never appear in `ParameterAssignment`s; (d) `Z[2]` via "+" round-trips.
3. Firewall passes.

Manual:
4. Place `Tuner1` (compact 300, pin left), `SourceTuner1` (wider, drive circle, pin right), `LoadTuner1`
   (wider, passive, pin left). Confirm prefixes + `Z[1]=50 Ω`.
5. Wire each tuner's single pin to a DUT node; confirm connection dots/hit-test behave (box hit-tests on its
   glyph, like other box symbols). The reference is implicit — no second pin to wire.
6. Toggle `BiasTee`/`ShowBias`; the bias branch appears/disappears off the single lead; labels stay clear.
7. Save + reload; all params incl. `ShowBias` round-trip (automatic via `SchematicPersistence`).
8. Rotate / mirror; the single pin stays on grid and the glyph transforms correctly. (Note: rotating a
   Source/Load tuner moves its pin side — that's expected; the net binding follows the pin.)

## Out of scope
- Analysis authoring (briefs 04–07). A dedicated Γ/Z widget or Smith-chart `.gam` picker (future). A second
  (reference/source) pin (deferred).
