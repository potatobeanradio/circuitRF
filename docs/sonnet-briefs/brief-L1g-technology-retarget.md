# Sonnet Brief — Phase L1g: retargeting a layout to a different technology

**Design:** `docs/design/layout-view.md` §2.4 (technology scope and the `TechRef` convention), §2.1 (layer
identity is `(Layer, Datatype)`, names are for humans), §6.4 (paste reconciliation). **Consumes** all of
L1a–L1f. **Runs before L2.**

Two owner-reported gaps, one underlying cause. This brief fixes both with a single shared component.

---

## 0. The two gaps

### Gap 1 — there is no UI to change a layout's technology

`LayoutView.TechRef` exists and is honoured, but nothing in the application can set it. A layout authored
against the wrong technology is stranded: all the geometry is fine, and there is no way to move it.

### Gap 2 — cross-technology paste silently maps onto unrelated layers

This is the serious one, because it looks like success.

`LayoutFragment.GetMissingLayers` asks *"which layer keys are absent from the destination technology?"* and
only those trigger reconciliation. But **both starter technologies use the identical key range** — `(1,0)`
through `(8,0)`:

| Key | PCB 2-Layer | MMIC GaAs |
|---|---|---|
| (1,0) | Top Copper | Metal1 |
| (2,0) | Bottom Copper | Metal2 |
| (3,0) | **Soldermask Top** | **Via** |
| (5,0) | **Silk Top** | **Cap Dielectric** |
| (7,0) | **Drill** | **Substrate** |

So when a PCB selection is pasted into an MMIC layout, **nothing is "missing"**. No dialog appears, no
warning is logged, and every shape silently adopts a semantically unrelated layer — drill geometry becomes
substrate, soldermask becomes via. The paste reports success and quietly destroys the design's meaning.

**The question `GetMissingLayers` asks is the wrong one.** The right question is *"which layers need the
user's confirmation?"*, and when the source and destination technologies differ, the answer is **all of
them** — with confident proposals pre-filled so the obvious cases stay one click.

This is not a reason to distrust §2.1's rule that identity is `(Layer, Datatype)`. That rule is correct
*within* a technology. **Across** technologies the numeric key carries no meaning at all, because each
process numbers its own layers. Names are what survive the crossing.

---

## 1. One mapping component, two callers

**R-L1g-1. Build `LayoutLayerMapping` once, framework-free, and use it for both paste reconciliation and
technology retargeting.** They are the same problem — *"these shapes were authored against technology A and
are moving to technology B; where does each layer go?"* — and solving it twice guarantees the two answers
drift apart.

`src/Ui/Layout/LayoutLayerMapping.cs`:

```csharp
public enum LayerMatchKind { ExactName, SameKeyDifferentName, SameKeySameName, NoMatch }

public sealed record LayerMappingRow(
    LayerKey        Source,
    string?         SourceName,      // from the fragment's LayerDefs, or the source technology
    int             ShapeCount,      // how many shapes use it — sort descending
    LayerKey?       Proposed,
    LayerMatchKind  Match,           // WHY it was proposed — shown in the dialog
    LayerReconciliationChoice Choice); // what the user settled on

public static IReadOnlyList<LayerMappingRow> Propose(
    IReadOnlyList<LayoutShape> shapes,
    IReadOnlyList<LayerDef>    sourceLayers,
    Technology?                destTech);
```

### Matching rules, in order

1. **Same key, same name** → `SameKeySameName`. High confidence, effectively the same layer. Pre-selected.
2. **Exact name match** (case- and whitespace-insensitive) on a *different* key → `ExactName`. High
   confidence: names are what a human authored and they survive renumbering. Pre-selected.
3. **Same key, different name** → `SameKeyDifferentName`. **Low confidence, and this is exactly the
   Drill→Substrate trap.** Propose it, but mark it clearly and — see R-L1g-2 — never apply it silently.
4. Otherwise → `NoMatch`, defaulting to **Keep as unknown**.

**Show the match kind in the dialog for every row.** A proposal the user cannot see the reasoning behind is
one they will either rubber-stamp or distrust; showing "matched by name" vs "same layer number, different
name" makes a 20-row table scannable in seconds. This is what makes the mapping *elegant* rather than merely
present.

**R-L1g-2. Confirmation is required whenever any row is `SameKeyDifferentName` or `NoMatch`.** If every row
is `SameKeySameName` or `ExactName`, apply silently — that is the same-technology case and must stay
frictionless. This single rule fixes Gap 2 without making ordinary same-tech paste noisy.

The three destination actions stay exactly as L1f defined them, so the vocabulary is unchanged:
**Keep as unknown** (default for `NoMatch`) · **Map to existing** · **Add to technology** (via the live-tech
dirty path, never a silent file write).

## 2. Shared dialog

One dialog serving both callers: a table of `LayerMappingRow`, sorted by `ShapeCount` descending so the
layers that matter appear first. Columns: source layer (name + key), shape count, match kind, and an action
combo. Plus **Map all unmatched to…**, **Keep all unknown**, and a live summary — *"1,204 shapes · 6 layers →
5 mapped, 1 unknown"*.

Title and framing differ per caller ("Paste into *MMIC GaAs*" vs "Change technology to *MMIC GaAs*"); the
table is identical.

## 3. Fix the paste path

- Replace the `GetMissingLayers` trigger with `LayoutLayerMapping.Propose` + R-L1g-2's confirmation rule.
  Keep `GetMissingLayers` only if something else uses it; otherwise delete it rather than leaving a second,
  wronger answer in the codebase.
- **Add `TechName` to the fragment payload** so the dialog can say where the geometry came from. The payload
  already carries the `LayerDef`s it used, which is what supplies `SourceName` — this is one string for
  legibility, not new data.
- Everything else about L1f's paste — rescale, anchor, ghost placement, undo — is unchanged.

## 4. New: Change Technology…

**Entry points:** a **Technology: *PCB 2-Layer* ▾** affordance in the layout metadata bar (which also closes
Gap 1 by making the current technology visible at all), and a Layout-menu item.

**The picker offers:**
- **(Workspace default)** — writes `TechRef = null`. This is L0c's convention and the normal case; it must be
  an explicit, selectable option, not something only reachable by never having chosen.
- Each `.ctech` in the workspace's `tech/` folder.
- **Browse…** for one outside the workspace.

**On confirm**, one undoable `RetargetTechnologyCommand` that:
1. Rewrites `TechRef`.
2. Rewrites every affected shape's `LayerKey` per the mapping.
3. Optionally adopts the target's `DefaultDisplayUnit` and `DefaultSnapDbu` — **a checkbox, default OFF**.
   Those are document state (§1.3/§1.5) and silently overwriting a user's working unit mid-retarget is
   exactly the kind of helpfulness that erodes trust. Offer it; do not assume it.
4. Applies any **Add to technology** choices through the live-tech mechanism, leaving the `.ctech` dirty.

**`DbuPerMicron` is NOT touched.** Resolution is a property of the layout, not of the technology — nothing in
`.ctech` sets it, and retargeting must not silently rescale a design. If the user wants a different
resolution that remains `LayoutScaling.TryChangeResolution`, which is a separate, guarded operation.

**One undo entry** restores `TechRef` and every `LayerKey` together. A half-undone retarget is worse than no
undo at all.

## 5. Report what happened

After a retarget (and after a cross-tech paste), post a Messages summary: *"Retargeted to MMIC GaAs · 1,204
shapes · Top Copper→Metal1 (name), Drill→(unknown), …"*. The user needs a record of a bulk change to their
geometry that they can read after the dialog is gone.

---

## Scope guardrails (do NOT do in L1g)

- No spatial index, caching or LOD (L2). No DRC (L5b), no interchange (L4), no instances (L3).
- No changes to `DbuPerMicron` handling, `LayoutScaling`, or the flattener.
- No automatic retarget on open, no "detect wrong technology" heuristics — this is a user-invoked operation.
- No layer *renaming* or editing here; that is the `.ctech` editor's job.
- Do not modify `WindowsClipboard`, `SchematicClipboard` or `SymbolClipboard`.
- Don't touch `src/Core`, `src/Engine`, or `RfCore`.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **The Gap 2 regression test.** Copy a PCB selection using Drill `(7,0)` and Soldermask Top `(3,0)`; paste
   into an MMIC layout. Assert the confirmation dialog is **required** (R-L1g-2) and that, without user
   choices, **no shape is silently rewritten** onto Substrate or Via. This test must fail against today's
   `GetMissingLayers` behaviour.
3. **Same-technology paste stays silent** — copy and paste between two layouts sharing a technology raises no
   dialog and rewrites nothing.
4. **Match kinds** — `Propose` returns `SameKeySameName`, `ExactName`, `SameKeyDifferentName` and `NoMatch`
   correctly, including name matching that is case- and whitespace-insensitive, and a name match that beats a
   competing numeric match.
5. **Shape counts** are correct and rows sort by count descending.
6. **Retarget round-trip** — author a layout in PCB, retarget to MMIC mapping Top Copper→Metal1 and Bottom
   Copper→Metal2, and assert every shape's `LayerKey` moved, `TechRef` changed, geometry coordinates are
   **byte-identical**, and nets and holes are untouched.
7. **`(Workspace default)`** writes `TechRef = null` and re-resolves through L0c's resolution order.
8. **Units are not adopted unless asked** — with the checkbox off, `DisplayUnit` and `SnapDbu` are unchanged;
   with it on, both come from the target. `DbuPerMicron` is unchanged either way.
9. **One undo entry** restores `TechRef` and every `LayerKey` in a single Ctrl+Z
   (`LayoutPersistence.Serialize` equality with the pre-retarget state).
10. **Add to technology** marks the `.ctech` dirty through the live mechanism and does **not** write the file;
    it is undoable in the tech editor.
11. **Keep as unknown** leaves the `LayerKey` intact and renders through `FallbackPalette`.
12. **Nothing is ever dropped** — shape count is identical before and after, for every combination of choices.
13. **Messages summary** is posted for both retarget and cross-tech paste.

## On completion

1. Add a "Phase L1g — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: **why
   `GetMissingLayers` was the wrong question** and the Drill→Substrate example that proves it, the
   **name-before-number matching order** and the reason (`(Layer, Datatype)` identifies a layer *within* a
   technology, not across two), **R-L1g-2's confirmation rule** as the thing that keeps same-tech paste
   frictionless while making cross-tech paste safe, that **`DbuPerMicron` is deliberately untouched by
   retargeting**, that **unit adoption is opt-in**, and the test file names.
2. Note that Phase L1 is complete including L1g, and report back before **L2 — performance** is briefed.
