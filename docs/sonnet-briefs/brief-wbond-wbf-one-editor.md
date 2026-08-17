# WB-F — one editor: the wBond editor HOSTS `LayoutEditorView` instead of transcribing it

**Phase:** WB-F. Not on §13's original roadmap; it exists because round 4 exposed the Layout Editor's
drawing tools through a *second* view shell and the owner immediately hit bugs the Layout Editor
itself does not have.

**Design authority:** `docs/design/wbond.md` **§6.11 (WB39, WB39a, WB40)**, **§9.5 (WB41)**,
**§9.6 (WB42)**, **§10.1**. Read those first. They were written for this brief and settle every
question it would otherwise have to argue; this document implements them and does not restate their
reasoning.

**Predecessor:** WB-A…WB-E complete. Round 4 (2026-08-16) added the second toolbar row this brief
removes — see `src/Ui/RESOLVED.md`, "wBond editor, round 4b", for the measurement that justifies the
whole phase.

---

## 0. What this phase is, in one paragraph

The wBond editor's layout half is **already** a real `LayoutEditorViewModel` inside a real
`LayoutCanvas` — the engine was never duplicated. What was duplicated is the ~2,700-line **view
shell**: the toolbar, the keyboard routing, the context menu, the focus handling, the breadcrumbs.
This phase deletes that copy and hosts `LayoutEditorView` itself, so the wBond editor is the Layout
Editor plus two panels (the profile view and the Array Inductance panel), which is what §6.1/WB22
specified from the start.

**This is a subtraction.** If the diff adds more lines than it removes, something has gone wrong —
stop and re-read §6.11.

### 0.1 The owner's report, and what it is actually about

> *"I just tried the new geometry shape tools in wBond and there are many many bugs (that we had
> previously resolved when we hardened the Layout Editor). It will take us days to solve all these
> geometry layout bugs that we've now put into wBond."*

**They are not in the geometry.** `LayoutEditorViewModel` is one object shared by both editors — its
snapping, hit-testing, drawing tools, commands and undo stack are the hardened ones. The bugs are in
the *shell around it*: a tool armed with no Escape to disarm it, a canvas with no context menu, arrow
keys that reach the wrong handler, no breadcrumb bar so push-in is unreachable, focus that does not
return after a toolbar click. **Do not go bug-hunting in `src/Ui/Layout/`.** Every one of these
disappears when the real shell is hosted, and any that does not is a genuine Layout Editor bug worth
fixing once, there.

### 0.2 The rule this phase establishes

> **If a change would be needed in both editors, it belongs in neither — it belongs in the shared
> control.**

A `Click=` handler in `WBondEditorView` that does what a `LayoutEditorView` handler already does is
the smell. After this phase, adding one is the thing code review should refuse.

---

## 1. What exists

### 1.1 Shared already — do not touch, do not duplicate

```
src/Ui/Layout/LayoutEditorViewModel.cs        + .Instances .Clipboard .Snap .Rotate .Booleans
                                              .Scale .PCells .PCellHandles .PaletteDrag .Drc .Retarget
src/Ui/Controls/LayoutCanvas.cs               pointer/key routing, DnD targets, overlay seam
src/Ui/Layout/LayoutSnapQuery.cs              geometry snap (+ WireSnap, round 4)
src/Ui/Layout/LayoutHitTest.cs
src/Ui/Renderers/LayoutRenderer*.cs
src/Ui/Commands/Layout/*                      every edit, on one undo stack
```

~9,500 lines, one copy, already used by both editors.

### 1.2 The shell that is duplicated — this is the subject

```
src/Ui/Views/Layout/LayoutEditorView.axaml         619   the real shell
src/Ui/Views/Layout/LayoutEditorView.axaml.cs     1129
src/Ui/Views/WBond/WBondEditorView.axaml           ~800  toolbar rows 1 AND 2
src/Ui/Views/WBond/WBondEditorView.axaml.cs        ~990
src/Ui/Views/WBond/WBondEditorView.LayoutTools.cs  ~230  ← round 4, deleted whole by this phase
src/Ui/Views/WBond/WBondEditorView.Selection.cs     211
src/Ui/Views/WBond/WBondEditorView.ProfileMenu.cs   217
src/Ui/Views/WBond/WBondEditorView.Dxf.cs           146
src/Ui/Views/WBond/WBondEditorView.Touchstone.cs     92
```

### 1.3 The seam that makes hosting possible

```csharp
// src/Ui/Layout/LayoutDocument.cs
public LayoutDocument(string title, LayoutEditorViewModel viewModel, string? filePath = null)
```

`LayoutEditorView`'s `x:DataType` is `LayoutDocument` and it binds `ActiveViewModel` throughout.
**A `LayoutDocument` can be constructed around the wBond editor's existing reference layout**, which
is the whole trick — no new abstraction, no interface extraction, no view-model surgery.

`LayoutDocument` also owns the push-in/pop-out frame stack, so hosting it hands the wBond editor
hierarchy navigation and the breadcrumb bar it never had.

---

## 2. Milestones

### M1 — delete the transcribed row

Delete `WBondEditorView.LayoutTools.cs` and toolbar row 2 from `WBondEditorView.axaml`.

**Keep**, because they are not part of the transcription and are load-bearing:

- `WBondLayoutOverlay.LayoutToolArmed` — still exactly the right seam once the real toolbar arms a
  tool; re-point it at the hosted document's `ActiveViewModel.ActiveTool`.
- The round-4 fix that makes `OnPointerPressed` decline a press landing on layout geometry
  (`LayoutHasSomethingAt`). That is what makes a cell instance movable and is independent of any
  toolbar.
- `WireSnap` and the wire-aware snapping in both editors.
- The drag-slip fix (`QualityLadder.RestoreFromChord`, `ChordIsFaithful`).

**Gate:** `WBondRound4Tests.AnArmedLayoutTool_TakesEveryPress` and
`APressOnLayoutGeometry_IsDeclinedSoTheLayoutEditorCanHaveIt` still pass.

### M2 — host `LayoutEditorView`

`WBondDocumentViewModel` gains a `LayoutDocument` wrapping its `ReferenceLayout`, created wherever
`ReferenceLayout` is set (there are three places — `EnsureReferenceLayout`, `Open`'s unpack path, and
the `ConfigureReferenceLayout` hook added in round 4; the hook is the one place to do it).
`WBondEditorView.axaml`'s layout half becomes:

```xml
<lv:LayoutEditorView DataContext="{Binding ViewModel.LayoutDocument}"/>
```

**Three things to settle, and none is hard:**

1. **Duplicate chrome.** `LayoutEditorView` brings its own rulers, metadata bar and (when torn off)
   file menu. wBond has its own rulers for *both* canvases. Prefer the hosted control's — delete
   wBond's layout-side ruler hosting and its Unit/Snap metadata bar, keep the profile view's. The
   profile ruler stays wBond's own; there is no shared control for a span/z axis.
2. **The wire overlay** is attached to `LayoutCanvas` today by name (`LayoutCanvasCtrl.CanvasOverlay`).
   Once the canvas is inside a hosted control, expose it — a `CanvasOverlay` pass-through property on
   `LayoutEditorView` is the smallest honest answer. **Do not** reach through the visual tree.
3. **Toolbar row 1** (the WIRE tools, view mode, profile plane, Ø, wire marquee, the five
   selection transforms) stays wBond's. It is genuinely wBond-specific and has no counterpart.

**Gate:** every tool on the hosted toolbar works — draw a rectangle, place a via, push into a cell,
pop out, right-click for the context menu, Escape to disarm, Ctrl+Z. None of these have handlers in
`src/Ui/Views/WBond/` afterwards.

### M3 — the two panels follow the active layout

The profile view and the Array Inductance panel become dockable tools that follow the active layout
document, exactly as the DRC and Properties panels already do (`PropertiesTool.SetActiveLayout`,
`DrcTool.SetActiveLayout`). §10.1's table is the specification.

**This is what turns "two editors" into "one editor with two extra panels"** and is the milestone the
whole phase is for. It can ship after M2 if M2 lands clean.

### M4 — WB40: a wirebond cell

`LayoutEditorViewModel.WireDesign` is already the seam. A cell folder holding a `.wBond` beside its
`.clay` loads it into `WireDesign`, and the overlay draws. Push into such a cell in the Layout Editor
and its wires are there.

**Do not add a wire shape type to `.clay`.** WB23 is unchanged and §6.11 restates why.

**Scope note:** M4 is the foundation for §9.5/§9.6 (schematic↔layout round trip) but does **not**
include them. Those are their own phase — see §5 below.

---

## 3. What must not change

- **WB23** — wires are an overlay; nothing 3D enters `.clay`.
- **§5.0/WB17b** — the schematic component carries its design; there is no `File` parameter.
- **The `RefPin` / `SymbolPitch` / array-editor work from round 4b.** That is schematic-side and
  orthogonal.
- **The standalone application** (§11). It hosts the same overlay without a workspace; if hosting
  `LayoutEditorView` inside it is awkward, say so and stop — do not fork the shell again to make it
  fit, which would recreate the exact problem this phase removes.

---

## 4. How you will know it worked

1. `grep -c "Click=" src/Ui/Views/WBond/WBondEditorView.axaml` drops sharply, and every survivor is a
   wire/profile/inductance handler.
2. `WBondEditorView.LayoutTools.cs` does not exist.
3. The diff removes more lines than it adds.
4. `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` green.
5. Manually: draw a rectangle in the wBond editor, undo it, right-click it, push into a cell, pop
   out — with no wBond-specific code in any of those paths.

---

## 5. Explicitly NOT in this phase

- **§9.5/§9.6 — the schematic↔layout round trip** (`Schematic to Layout` emitting a wirebond cell,
  `Update Schematic from wBond Layout` re-encoding the payload). Designed in `wbond.md`, built in a
  later phase. WB-F is a prerequisite: doing the round trip while two shells exist would mean
  building it twice.
- **Retiring `WBondDocument`.** It stays. §10.1's table keeps three surfaces, and the standalone
  binary needs the document type.
- **Fixing round 4's transcribed-toolbar bugs individually.** That is the option this phase exists
  instead of.
