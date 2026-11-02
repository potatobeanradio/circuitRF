# Brief TO-4 — The Tuning panel

**Series:** `brief-tuneopt-0-overview.md` (D1–D9, §3 UX) · **Tag:** `R-to4-<m>` · **Depends on:** TO-3
**Area:** `src/Ui/ViewModels/Dock/` (a new `TuningTool` beside `AnalysesTool`, `InstancesTool`),
`src/Ui/Docking/DockLayoutSchema.cs` (`DockPanelIds.Tuning`), `CircuitRfDockFactory.cs`, `src/Ui/Tuning/`,
the Properties Inspector parameter rows, the schematic canvas parameter-text rendering in `src/Render`,
`src/Ui/Smith/SmithSliderRowViewModel.cs` (precedent for a slider row)

---

## 1. Goal

The panel the owner will judge the whole feature by. Compact, fast, obvious, keyboard-friendly.

## 2. Layout (top to bottom)

```
┌ Tuning ─────────────────────────────────────────────┐
│ ▶ amp_testbench.csch                    ● 0.4 s     │  header: schematic name, status dot + last eval time
│ [▶][■][↺][⤓ Push][📷][⊘📷][＋][⚙][?]  Presets ▾     │  toolbar: Start/Stop, Revert, Push, Snapshot,
├─────────────────────────────────────────────────────┤           Clear snapshot, Add…, settings, Help, presets
│ C1.C            [====|==========]  [ 1.80 ] pF   ⋮  │  one row per tune-enabled entry
│   0.9 pF                         3.6 pF   log       │  min/max under the slider, editable in place
│ DUT:Wline       [=======|=======]  [ 212  ] um   ⋮  │  sub-cell rows show "DUT · ×2"
│ R1.R  (sweep)   disabled — swept by SW1              │  D1 disabled row, reason only
└─────────────────────────────────────────────────────┘
```
Buttons are the Analyses panel's size and style, icon-only with tooltips. Presets (TO-5) is a small drop-down on the
toolbar's right; this phase leaves a placeholder.

## 3. Requirements

**R-to4-1 — Dock.** New `DockPanelIds.Tuning` (a file-format string — never rename). Default layout: tabbed **behind
Analyses** in the same tool dock. Window ▸ menu entry. Existing saved layouts that lack the panel gain it in the
default place on first load (follow how the most recently added panel, e.g. `Instances`, was migrated).

**R-to4-2 — Follows focus.** Cleared on focus change, set for a `.csch` only (the `InstancesTool` rule, R-fi-2/3).
Otherwise one line: "Focus a schematic to tune it." Switching schematic while tuning stops the session (TO-3 Stop).

**R-to4-3 — Activating tunables, three ways, one effect** (sets the entry's `tune` flag; creates the entry with D4
defaults if absent; one undo step):
1. **Add…** (＋) opens a **search popup** listing the TO-1 catalog: a search box (name, instance, parameter, cell),
   an **Include sub-cells** check like the Instances panel's, rows showing key, location (`top` / `DUT · ×2`), current
   value, read-only mark. Multi-select, Enter adds. Typing filters live.
2. **Properties Inspector:** a small tune toggle on each parameter row that the catalog offers (absent on rows it does
   not offer — no greyed toggles).
3. **Canvas:** right-click a shown parameter value → **Tune**. Same for a VAR row.

**R-to4-4 — Row.** Name (key, with location for sub-cell rows); a slider (log or linear per D4 `Scale`); a number box
in the parameter's own unit/scale prefix; min and max shown under the slider, **editable in place** (click → box); a
⋮ menu: Linear/Log, Step…, Re-centre range on value, Reset range to default, Remove from tuning, Reveal on canvas.
Integer rows snap. Keyboard per overview §3. Dragging sends requests continuously (or only on release, D7); typing
sends on Enter.

**R-to4-5 — Start/Stop.** Start creates the TO-3 session (analysis scope from ⚙: all enabled, or a checked subset,
with the point count of each and a warning when a sweep exceeds 50 points). Moving a slider before Start does nothing
but move the slider. Stop ends the session (TO-3 R-to3-7).

**R-to4-6 — Status and lag.** The header's dot: idle / running / lagging. While lagging, rows whose requested value ≠
displayed value get a subtle tint, and the header shows the displayed result's age ("1.4 s behind"). When the session
suggests run-on-release (R-to3-3), the ⚙ button shows a badge; one click turns it on.

**R-to4-7 — The canvas tells the truth.** While a session holds values that differ from the schematic, each affected
parameter's text on the canvas draws in the **tuned** color and shows the tuned value. A sub-cell's tuned values show
the same way when the user pushes into that cell. The drawing lives in `src/Render` as an overlay input (the renderer
draws; it does not know about sessions).

**R-to4-8 — Push (one undo step per document).** Writes every tuned value into the document that owns it, as the
value text (`47 pF`), through each document's own edit/undo path: top-level and VAR values into the tuned schematic
(**one** undo step), each sub-cell's into that cell's schematic (one undo step in that document). A value on an
instance that was inheriting the cell default **adds** the override to that instance. Every document written becomes
**dirty**. A sub-cell schematic that is not open is **opened as a tab without taking focus** (overview D2), so its
unsaved state is visible and goes through the ordinary unsaved-changes prompt. Never leave a dirty document in a
background session with no tab. Tuning without Push dirties nothing. Read-only owners are skipped (D2). The status line
says what was written and skipped ("Pushed 5 values · DUT: 2 · skipped 1 read-only"). After Push the session's values
equal the schematic, and the session continues.

**R-to4-9 — Revert.** Returns every slider to the schematic's value and drops the published result (TO-3).

**R-to4-10 — Entry edits are document edits** (D5): enabling, ranges, scale, step — each one undo step in the tuned
schematic, and marks it dirty.

**R-to4-11 — Complex values (overview D18; amended 2026-10-07, built).**
- **Activating.** A complex literal offers its four parts everywhere the three ways meet: the Add… list shows
  `real(ZL)`, `imag(ZL)`, `mag(ZL)`, `phase(ZL)` as rows of their own; the Inspector row's tune button opens a menu
  of checkable **Real / Imaginary / Magnitude / Phase (deg)** items instead of toggling; the canvas right-click ▸
  **Tune** is a submenu of the same four. `ITuningSurface.KeysFor` (one key for a plain number, four for a complex
  value) is the one mapping all three use.
- **Rows.** Each tuned part is its own row in the value's unit (phase in deg). The panel holds ONE complex number per
  value; a part row's move asks the panel, which moves the value along that part's path with its partner held and
  **stops at the first edge** of the region every entry of the value allows (`ComplexRegion.Move`); every part row of
  the value then shows its own view of the result.
- **Refusals.** A range edit, Re-centre, Reset or first activation that leaves no complex value inside every range of
  the value is refused with the sentence in the status line ("Refused: real(ZL)'s range leaves no value of ZL inside
  every range (…)"), no undo step, and the row's boxes back at their old values.
- **Values out.** `CurrentValues()` carries the value once, whole, under its own key and in the schematic's form, and
  only once one of its parts has moved; lag, the canvas's tuned text and Push all read that.
- **Push** writes the whole value in its own form (`polar(50,45)` stays `polar(…)`), one command for the value however
  many parts moved. Written keys stop being session values, so the rows rest on the schematic's new value.

## 4. Gates (minimal; run only these classes)
- `TuningPanelFollowsFocusTests` — schematic → populated; layout/other → cleared; switching stops the session.
- `TunableActivationTests` — the three ways set the same flag and are each one undo step; an expression parameter
  has no Inspector toggle and is absent from Add….
- `TuningPushTests` — top + VAR + sub-cell values pushed into two documents, one undo step each, both dirty, the
  unopened sub-cell now a tab that is not focused, read-only skipped and reported; an inherited instance parameter
  gains an override; undo in the top document restores only the top document; a session that was never pushed leaves
  every document clean.
- `TuningSliderMappingTests` — log/linear value↔position round trip; integer snap; keyboard step sizes.
- Docking: the new id appears behind Analyses in the default layout and in a migrated older layout.
- `ComplexTuningPanelTests` — moving the real part holds the imaginary, updates the magnitude row and stops at the
  magnitude's max; a conflicting range is refused with no undo step; Push writes `polar(…)` back as `polar(…)`.
- `TunableActivationTests` reads the canvas key through `KeysFor`.
