# Sonnet Brief — L5 follow-ups, round three: exact code locations, not behaviour descriptions

**Read this preamble.** Three of these have been briefed before and are still broken. The previous briefs
described the *behaviour wanted* without naming the *code path that decides it*, and each round added a guard
somewhere downstream of the real dispatch. This brief names the lines. Do not re-derive the location.

Gate command is plain `dotnet test`.

---

## 1. Double-click on a PCell — the dispatch, not the guard (third attempt)

Current symptom: double-clicking a PCell shows *"Can't push into cell: A parametric cell's geometry is
generated; edit its parameters instead"* and **no parameter editor opens.**

### 1.1 The exact location

`src/Ui/Views/Layout/LayoutEditorView.axaml.cs`, line ~136:

```csharp
private void OnInstanceDoubleTapped(object? sender, LayoutInstance instance)
{
    if (DataContext is not LayoutDocument doc) return;
    DoPushInto(doc, instance);          // ← unconditional
}
```

`LayoutCanvas.OnDoubleTapped` (line ~242) only *raises* `InstanceDoubleTapped`; the canvas deliberately
"only reports" and this handler decides. **Every previous fix guarded `DoPushInto` instead of changing this
dispatch**, which is exactly why the refusal message appears: push-in is still being called, and now it
declines politely.

### 1.2 The fix

**R-L5h-1. `OnInstanceDoubleTapped` must branch before `DoPushInto` is reached.** If the instance resolves to
a PCell, open its parameter editor and return. Otherwise push in as now. `DoPushInto` should never be entered
for a PCell, so its guard becomes unreachable for this path — leave the guard (the toolbar button still needs
it) but it must stop being what the user sees.

**R-L5h-2. The parameter editor opened must be the same one the layout Properties Inspector uses** (added in
round one), not a new dialog. If the schematic's parameter-editor dialog is reusable, prefer it — the owner's
comparison is "like the schematic does."

**Check the toolbar Push-In button too** (`OnToolbarPushIn`, ~line 146) — it calls the same `DoPushInto`. It
should be *disabled* for a PCell selection, which is where the guard message legitimately belongs.

## 2. The Path objects between pins are a ratsnest, and they are real geometry

**They are `PathShape` objects on a reserved layer**, emitted by `SchematicToLayoutGenerator` around line 229:

```csharp
private static readonly LayerKey RatsnestLayer = new(0, 900);   // line ~69
...
var line = new PathShape { Layer = RatsnestLayer, ... };         // line ~264
```

**R-L5h-3. Stop emitting the ratsnest as model geometry.** As `LayoutShape`s they are selectable, movable,
deletable, swept into booleans, flattened, copied to the clipboard, counted in the spatial index, and one
`.ctech` mapping away from being **exported into a fabrication file**. A connectivity guide is an *overlay*,
never artwork — the identical rule R-L5g-13/14 states for pins, and this is the second instance of the same
error.

**R-L5h-4. Delete the ratsnest shapes that existing layouts already contain.** The owner's current designs are
polluted with them. On load or on the next generator run, remove shapes on `(0, 900)` and report the count —
otherwise this fix leaves the mess behind.

**Do not build an overlay ratsnest in this brief.** The owner said he does not want to see them; a proper
overlay implementation with a view toggle is a separate decision he has not asked for. Remove, report, stop.

## 3. MBend geometry — the sense of the miter percentage is the likely bug

The owner reports the optimal miter "looks like two microstrips butted up with one corner cut out," which is a
*nick*, not the deep chamfer an optimal miter produces.

### 3.1 The geometry, stated unambiguously

For a right-angle bend of two arms of width `W`, the overlap is a `W × W` square with an **inner corner** and
an **outer corner**:

| Symbol | Meaning |
|---|---|
| **D** | the diagonal of that square, inner corner to outer corner: **`D = W·√2`** |
| **M** | the miter percentage: **`M = 52 + 65·e^(−1.35·W/h)`** (Douville & James), valid `W/h ≥ 0.25`, `εr ≤ 25` |
| **X** | the length **removed**, measured from the **outer** corner along the diagonal: **`X = (M/100)·D`** |
| **A** | the remaining diagonal, `A = D − X` — the calculators' "compensated length" |

The cut line is **perpendicular to the diagonal**, i.e. at 45° to both arms.

**R-L5h-5. `M` is the fraction REMOVED, not the fraction kept.** At `W/h = 1`, `M ≈ 69%` — so roughly
**two-thirds of the corner disappears**, leaving a short stub near the inner corner. That is a deep chamfer.
Interpreting `M` as "keep 69%" removes only 31% and produces precisely the nick the owner is describing. **This
inversion is the leading hypothesis; check it first.**

The second candidate remains R-bnd-1's missing `√2` (applying `M` to `W` rather than to `D`), which
under-cuts by 41%. Both produce "too shallow," so check both.

### 3.2 Validate numerically against the owner's own references

**R-L5h-6. Use the calculators as an independent oracle, not the eye.** Plug several `(W, h)` pairs into
[everythingrf](https://www.everythingrf.com/rf-calculators/microstrip-mitred-bend-calculator) and
[calctown](https://www.calctown.com/calculators/microstrip-optimal-mitre-bend), and assert our computed **D**,
**X** and **A** match. That is a numeric gate, and it settles this without another round of "looks wrong."

Record the table in the completion note.

**R-L5h-7. Non-90° bends: Douville & James is a right-angle formula.** MBend has an `Angle` parameter, and the
fit does not cover arbitrary angles. Either restrict `Optimal` to 90° (disabled with a reason otherwise) or
document the extrapolation explicitly — **decide and say which**, because silently extrapolating a curve fit
outside its geometry is how wrong numbers look plausible.

**And the three modes must produce three different outlines** (R-pc-18) — the owner reports 0, 1 and 2 look
identical, so assert the outlines differ, not merely that the parameter was set. If `Miter` is an enum, note
R-L5g-11: `TryResolveSiValue` accepts only `Real` and `Bool`, so an enum parameter cannot resolve and every
value falls back to one default.

## 4. MKlopf layout parameter editor: the entry-mode toggles are disabled

The `W1`/`W2` and `L`/`f3dB` buttons were added but do nothing because they are disabled.

**R-L5h-8. Find why the enablement predicate is false and report it.** R-L5g-1 said the impedance fields
should be disabled **only** when no technology resolves — so either the technology is not resolving in the
layout parameter-editor context, or the predicate is inverted or reading the wrong state.

**The technology-resolution possibility is the one to check first**, because it would be a larger bug than the
buttons: a layout-side PCell editor that cannot resolve the technology also cannot convert `Z ↔ W` *or* show
correct derived values anywhere. Verify the editor resolves the same technology the generator does, by the
same path (`workspace-and-project-tree.md` §5A.2).

**R-L5h-9. A disabled control must say why.** Per R13a, a button that cannot act carries a reason on hover.
"Pressed it and nothing happened" should not be reachable.

## 5. Guardrails

- Do not add another guard inside `DoPushInto` (§1) — change the dispatch.
- Do not build an overlay ratsnest (§2) — remove, report, stop.
- Do not adjust the miter by eye (§3) — match the calculators numerically.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Double-click (R-L5h-1/2)** — double-clicking a PCell opens the parameter editor and produces **no**
   message; `DoPushInto` is never entered (assert it, don't infer it); an ordinary cell instance still pushes
   in; the toolbar Push-In button is **disabled with a reason** for a PCell selection.
3. **No ratsnest geometry (R-L5h-3)** — after Update Layout from Schematic, the layout contains **zero**
   shapes on `(0, 900)` and zero shapes the user did not author. Export to GDSII, DXF and Gerber contains no
   ratsnest artifacts.
4. **Cleanup (R-L5h-4)** — a layout already containing `(0, 900)` shapes has them removed, with the count
   reported.
5. **Miter geometry (R-L5h-5/6)** — `D`, `X` and `A` match the calculator table for at least three `(W, h)`
   pairs; at `W/h = 1` the removed length is ≈69% of `D`, **not** 31%; `Miter` = 0, 1, 2 produce three
   **distinct** outlines.
6. **Miter resolves** — the value reaching the generator equals the value set, for all three modes.
7. **Angle handling (R-L5h-7)** — a non-90° bend either refuses `Optimal` with a reason or documents the
   extrapolation; assert whichever was chosen.
8. **Entry-mode toggles (R-L5h-8/9)** — with a technology resolved, both toggles are **enabled** and switching
   converts correctly; with none, they are disabled **and state why**.

## 7. On completion

Record in `src/Ui/CLAUDE.md`: **that double-click dispatch lives in `LayoutEditorView.OnInstanceDoubleTapped`
and the canvas only reports** — so future "double-click does the wrong thing" reports go straight there;
**that a ratsnest was being emitted as real geometry** and that connectivity guides are overlays, never
artwork (second occurrence of that error); **the calculator comparison table** for the miter, and which of the
two hypotheses was the cause; whether `Miter` was failing to resolve as an enum; and **why the entry-mode
toggles were disabled** — specifically whether the layout parameter editor was resolving the technology at
all.
