# WB-D — assembly DRC: the `.wasm` rule file, 3D predicates, and the loop envelope

**Phase:** WB-D. **Design authority:** `docs/design/wbond.md` §8 (WB31, WB32, WB32a) — read it first;
this brief implements it and does not restate its reasoning.
**Predecessor:** WB-C is complete (see `src/Ui/CLAUDE.md`'s wBond entries). WB-A (physics), WB-B (the
component and stamp) and WB-C (the editor) all shipped.

---

## 0. What this phase is, in one paragraph

A wirebond design can be **checked against the assembly house's own rules**: minimum wire pitch, the
loop-height-vs-span envelope, wire-to-wire 3D clearance, wire-to-pad and wire-to-die-edge clearance,
allowed diameters and metals. The rules live in a **new resolvable document, `.wasm`**, referenced by
the workspace — never inside `.ctech`, because the relation between assembly houses and process
technologies is many-to-many and the lifecycles differ (§8's own argument). The DRC evaluates the
**union** of `.ctech`'s die-side rules and `.wasm`'s assembly-side rules.

**The single most important instruction in this brief: almost nothing here is new machinery.** The
rule model, the expression parser, the waiver model, the results panel, the reporting, the
merge-two-documents logic and the run-refusal ceiling all exist and all get reused. Exactly **one**
genuinely new thing is being built — a 3D predicate class — plus one small language extension. If you
find yourself writing a second rule language, a second waiver store, or a second results panel, stop:
you have taken a wrong turn, and §8.1 says so explicitly.

---

## 1. What exists, and what you will reuse

Read these before writing anything. Signatures are given so you do not have to re-derive them.

### 1.1 The DRC stack (`src/Ui/Layout/Drc/`)

```csharp
// DrcLayerExprParser.cs
public static bool TryParse(string? text, out DrcLayerExpr? expr, out string? error);

// DrcLayerExpr.cs — an abstract record hierarchy
public abstract record DrcLayerExpr
{
    sealed record Layer(LayerKey Key);
    sealed record And/Or/Not/Xor(DrcLayerExpr A, DrcLayerExpr B);
    sealed record Sized(DrcLayerExpr A, long ByDbu);
    sealed record Select(DrcLayerExpr A, DrcLayerExpr B, DrcSelectOp Op);
    sealed record Holes/Merged(DrcLayerExpr A);
    sealed record WithArea(DrcLayerExpr A, long? MinDbu2, long? MaxDbu2);
    sealed record WithPerimeter(DrcLayerExpr A, long? MinDbu, long? MaxDbu);
}

// DrcEngine.cs
public static DrcRunResult Run(
    IReadOnlyList<LayoutShape> shapes,
    Technology?                tech,
    IEnumerable<DrcWaiver>?    waivers  = null,
    DrcRunSettings?            settings = null);

// DrcModel.cs
public sealed record DrcViolation(
    string RuleName, DrcRuleKind Kind, DrcSeverity Severity, LayerKey Layer,
    long RequiredDbu, IReadOnlyList<long[]> MarkerRings, Bbox Marker,
    string? NetA, string? NetB, string Key)          // + Waived, WaiverReason (init)
public sealed class DrcWaiver { string Key; string Reason; string RuleName; }
public sealed record DrcRunSettings(int MaxShapes = DrcEngine.DefaultMaxShapes);
public sealed record DrcRunResult(Violations, RulesEvaluated, LayersChecked, ShapesChecked, TechnologyName, …);
```

`DrcRuleKind` lives in `src/Ui/Layout/TechModel.cs` alongside `DrcRule`. Waivers persist on
`LayoutView.DrcWaivers`, keyed by `DrcViolation.Key`. The results panel is `DrcTool`
(`SetActiveLayout`), tabbed with Messages; markers render via `LayoutOverlay.DrcMarkers`.

### 1.2 The merge machinery

`src/Ui/Layout/TechnologyMerge.cs` — per-section identity, replace-keeps-position, everything cloned,
collisions listed with both sides. §8 says the DRC evaluates the union of the two documents' rules
"merged by the same machinery `TechnologyMerge.cs` already uses". **Reuse it; do not write a second
merge.** If its identity rules do not fit a `.wasm` section, widen it — do not fork it.

### 1.3 The wBond side (`src/WBond/`, `src/Ui/WBond/`)

`WBondDesign` / `WireArray` / `Wire` / `Point3` (integer **nanometres**), `Wire.LoopHeightNm` (the
definition — max z − min z, §3.0), `Wire.FootDropNm`, `Wire.SpanMetres()` (the span
definition — the XY distance between the feet, §3.0a; it was `ChordLengthMetres()` and 3-D until
2026-08-19),
`WBondDesign.AllWires()` (defines flat wire order), `WireMesh` (filaments + `ArrayOfWire`),
`WBondUnits.TryParseLength` (the one number-with-unit parser). The editor is `WBondViewModel`;
`WBondDocumentViewModel.ReferenceLayout` carries the layout the wires ride over.

---

## 2. The traps — read this section twice

These are the four things most likely to be got wrong silently. Three of them have already bitten this
codebase once each.

### R-wbd-1 (THE BIG ONE) — a wire point is **nanometres**; a `LayoutShape` is **DBU**

The existing DRC works entirely in the layout's database units. Wire coordinates are nanometres. **The
two coincide exactly at the 1,000 DBU/µm default and nowhere else.** This exact bridge has now shipped
broken twice — once in `WBondRenderer` (nm→µm instead of nm→DBU) and once in `DxfWireIo` (nm fed to a
DBU-taking writer) — and both times it was invisible on every default document.

- Convert at **one** stated crossing, as `WBondSnap.ToDbu`/`ToNm` and `DxfWireIo.NmToDrawingUnit`
  already do. Name the crossing in a doc comment.
- **Every test of a mixed wire/layout predicate must run at a NON-default `DbuPerMicron`** (100 and
  10,000, not just 1,000). A suite built only on the default cannot tell a correct conversion from a
  missing one — this is not a hypothetical, it is what let both prior bugs through.
- Prefer keeping the 3D predicates in **nanometres** internally and converting layout geometry *into*
  nm once, rather than the reverse: wires are the only 3D thing, and the wire side is where the
  precision matters.

### R-wbd-2 — segment-to-segment distance has degenerate cases that a naive formula gets wrong

Wire-to-wire clearance is a **capsule-to-capsule** distance: the minimum distance between two 3D line
segments, minus both radii. The closed form is standard, and its failure modes are:

- **parallel or near-parallel segments** — the usual `denom = a·c − b·b` goes to zero and the
  unclamped solution explodes;
- **zero-length segments** — a degenerate wire point pair;
- **clamping**: the unconstrained closest points must be clamped to `[0,1]` **and then re-solved on the
  other parameter**, not clamped independently. Clamping both independently is the classic wrong
  answer and is wrong by a bounded-but-real amount on exactly the crossing wires DRC exists to find.

**Gate it against an independent oracle, not against itself.** A brute-force sampled minimum over both
segments (say 200×200 samples) is a perfectly good oracle for a test and is trivially obviously
correct; assert the closed form agrees to a tolerance far below a wire radius, over a randomised
corpus that deliberately includes parallel, coincident, touching, crossing and zero-length pairs.

### R-wbd-3 — a wire violation's waiver `Key` must not be the flat wire index

`DrcViolation.Key` is what a waiver stores. Flat wire indices **shift** whenever a wire is added,
deleted, pasted or moved between groups — so a key built on one would silently re-point a waiver at a
different wire after any structural edit. That is worse than losing the waiver.

The 2D DRC keys on rule + layer + the marker's exact bounding box, and the reasoning there applies
here: **a waiver names a PLACE.** Key a wire violation on rule name + the participating groups' names
+ the marker's own bbox in DBU. Editing the offending geometry changes the key and un-waives it, which
is the correct outcome — the waiver was granted for geometry that no longer exists.

`DrcViolation.Layer` is `LayerKey` and a wire has none. Decide explicitly (§5 open question 2) rather
than defaulting to `(0,0)` and letting the panel group every wire violation under a layer that means
nothing.

### R-wbd-4 — the cost is quadratic in wires, and the owner's stated worst case is 600

600 wires is 179,700 unordered pairs before you look at their segments; each wire is 6–7 points, so a
naive all-pairs all-segments sweep is ~7 million segment-pair distances. That is not catastrophic
(the physics kernel already does 12.9 M filament pairs in ~0.5 s) but it is far too slow to be
casual, and DRC is run repeatedly while fixing violations.

- **Reject pairs on a bounding-box test first**, using the same spatial-index pattern the 2D DRC
  already uses (`DrcRegions` / the L2b index). A wire's 3D bbox is cheap and rejects almost everything.
- **Bound the run** the way `DrcRunSettings.MaxShapes` already bounds the 2D one — refuse with a
  message rather than hang. Reuse that record; add a wire ceiling to it rather than inventing a second
  settings type.
- **Report the measured cost in the completion note.** State it at 100 and 600 wires. Do not claim a
  budget you have not measured.

---

## 3. Milestones

Each milestone is independently completable and independently gated. **Do M1 first and completely** —
it is where every rule that can be wrong lives, and it is fully headless-testable.

### M1 — the `.wasm` document, framework-free

New files under `src/Ui/Layout/Drc/` (or a sibling folder if that reads better — say which and why).

- **`WasmFile`** — the three sections §8's WB32 requires: `machine`, `process`, `material`. Each
  carries rules; `material` additionally carries the allowed wire diameters and metals.
- **Persistence** mirroring `TechPersistence` exactly: `System.Text.Json`, `WriteIndented`,
  `JsonStringEnumConverter`, PascalCase, a `format_version` that is **rejected** (never migrated) when
  newer, `Id` never persisted, written through `AtomicFile`.
- **Resolution** mirroring `TechnologyResolver`: a `.wasm` reference on the document, else a workspace
  default from `.cws`, else none — and **none is not an error**, it means there are no assembly rules
  to check, reported once.
- **A violation reports which section it came from** (WB32) — "your bonder cannot do this" and "your
  house prefers not to" have different answers. Carry it on the rule and surface it in the panel.

**Gate M1:** round-trip every section; a newer `format_version` is refused by name; an absent `.wasm`
resolves to "no assembly rules" rather than failing; a `.wasm` merged with a `.ctech` through
`TechnologyMerge` produces the union with collisions listed.

### M2 — the language extensions

Three widenings to the **existing** parser and AST. Nothing is replaced.

| | add |
|---|---|
| **operands** | wire sets — an array by name (`G1`), a wire, a segment, or a selector over them |
| **functions** | `wire_spacing`, `loop_height`, `span`, `dist_to_edge`, `wire_to_layer`, `angle_change` |
| **values** | `envelope(...)` — a **piecewise-linear lookup**, so a limit can be a curve of span |

The envelope is the one genuine language extension: minimum and maximum loop height are both functions
of span and houses supply them as a table. Make it a **first-class value**, not a special case bolted
onto one rule — it will serve any other tabulated limit a house supplies.

**Gate M2:** `wire_spacing(G1, G2) >= 4mil && loop_height(G1) <= envelope(span(G1))` parses to the
expected AST; an unknown function is refused **by name** with the position; an envelope table with
out-of-order or duplicated span points is refused rather than interpolated into nonsense; a table with
one point is a constant (decide and state whether that is legal); an expression using only the
pre-existing 2D vocabulary parses **byte-identically** to before (a pinned regression, so the widening
provably did not disturb the existing language).

### M3 — the 3D predicate class

The only genuinely new code. Framework-free, in `src/WBond/` if it needs no layout types, otherwise
`src/Ui/Layout/Drc/`.

- Capsule-to-capsule minimum distance (R-wbd-2), with the brute-force oracle test.
- Wire-to-layer-geometry distance: a 3D segment against 2D artwork at a stated z. **State the z
  assumption explicitly** — artwork on a conductor layer sits at that layer's own stackup height, and
  assuming z = 0 for everything is exactly the kind of silent wrongness this brief exists to avoid.
- Bbox rejection + the spatial-index acceleration (R-wbd-4).

**Gate M3:** the oracle comparison over a randomised corpus including every degenerate case; a
measured cost at 100 and 600 wires, reported as numbers; two wires that touch report distance zero
rather than a negative or a NaN.

### M4 — the run, the panel, the waivers

- Widen `DrcEngine.Run` **additively** to accept a `WBondDesign?` and a resolved assembly rule set;
  every existing caller compiles unchanged and produces identical results (pin this).
- Wire violations flow into the **existing** `DrcRunResult` / `DrcTool` / marker machinery.
- Markers: a wire violation's marker is a **projection** into the layout plane, since that is the view
  it is drawn in. Say so in the panel — a 3D clearance shown as a 2D marker will otherwise read as
  wrong when two wires that look far apart in plan are close in space.
- Waivers via the existing `LayoutView.DrcWaivers` and `DrcViolation.Key`, keyed per R-wbd-3.

**Gate M4:** an existing 2D-only DRC run is byte-identical before and after the widening; a wire
violation appears in the panel, names its section, waives, persists through save/reload, and
un-waives when the wire is moved; a design with wires but no `.wasm` reports "no assembly rules" and
checks the layout normally.

---

## 4. Guardrails

- **Do not touch the physics** (`src/WBond/` inductance path, `IncrementalFill`, `WireMesh` beyond
  reading it). DRC reads geometry; it never edits a design.
- **Do not write a second rule language, waiver store, results panel, or merge.** §8.1 is explicit.
- **Do not put assembly rules in `.ctech`.** WB31 is an owner decision with a stated argument.
- **Do not bump `.clay`, `.ctech` or `.wBond` format versions.** A `.wasm` reference on a document, if
  one is needed, is an additive nullable field written only when set.
- `src/Core`, `src/Engine`, `RfCore` are untouched. `tests/Firewall.Tests` must stay green.
- **No GDSII work** — unchanged from WB-C's own ruling.

---

## 5. Open questions the owner should settle before or during M1

State the answer you adopted in the completion note either way; do not let a guess become a silent
convention.

1. **Where does the `.wasm` reference live?** On the `.wBond` document, on the workspace `.cws`, or
   both (document overrides workspace, like `.clay`'s `TechRef`)? The `.ctech` precedent argues for
   both, and that is the recommendation — but it is a persisted-format decision and should be
   deliberate.
2. **What `LayerKey` does a wire violation carry?** The panel groups and colours by layer. Options: a
   reserved synthetic key; the layer of the pad the wire lands on; or widen `DrcViolation` so the
   field is nullable. The third is cleanest and is the recommendation, but it touches a record every
   existing violation uses.
3. **Is a one-point envelope table a constant, or an error?** A constant is convenient; an error
   catches a truncated table. Recommendation: legal, and treated as a constant, because a house that
   states one number means one number.
4. **Does WB-D own a `.wasm` EDITOR, or is hand-authored JSON enough for this phase?** The `.ctech`
   editor (L0d) was its own phase. Recommendation: **not in WB-D** — ship the format, the resolution,
   the rules and the checking, and let the editor be its own phase once the vocabulary has settled
   against a real house's rule set. Say so rather than half-building one.

---

## 6. A known gap that is NOT this phase, flagged so it can be scheduled

**§9.2 routes 2 and 3 remain blocked on a WB-B gap.** The Core/Engine half of the wBond component
exists — `ComponentModelFactory` dispatches the `wBond` type, `Elaborator.ResolveWBondParameters`
resolves it, `WBondSymbolGenerator` can build the symbol — but there is **no `SymbolKind.WBond` and no
`ComponentTypeRegistry` entry**, so a wBond cannot be placed in a schematic at all. Adding a wires
group to an existing schematic, and adding wires-plus-geometry as a new cell, both need it.

It is a small-looking gap with one real design question inside it: a wBond's symbol is generated from
a **referenced file's contents** (the pin count comes from the `.wBond`'s arrays), which no existing
placement path handles — every other symbol's shape is known at registry time. That mechanism is worth
its own short brief rather than being smuggled into WB-D.

**Recommendation:** if placing a wBond in a schematic matters sooner than assembly DRC, do that brief
first — it is smaller, and it unblocks a feature the design doc already promises.

---

## 7. Completion note — what to record

Follow the house convention in `src/Ui/CLAUDE.md`: what was built, **what was found**, what was
deliberately not built and why, the gate numbers, and an explicit "not interactively verified" list.

Specifically, record: the measured 3D-predicate cost at 100 and 600 wires; every open question from §5
with the answer adopted; any rule in §8's table that turned out **not** to be expressible in the
widened language (report it, do not quietly drop it); and — if the nm↔DBU bridge bites a third time —
say so plainly, because at that point the lesson is about the codebase and not about one file.
