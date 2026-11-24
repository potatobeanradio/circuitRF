# Brief DDL-5 — display controls on the canvas, and the slider

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl5-<n>` · **Depends on:** DDL-1 (the link value); not DDL-2–4
**Area:** new `src/Render/DataDisplay/Controls/` (model + `ControlRenderer`), `Models/DataDisplayConfig.cs`
(`TabConfig.Controls`), `PlotComposer.cs`, `PlotConfigLoader.cs`, `src/Cli/RenderDataDisplay.cs`; in `src/Ui`, a
`ControlContainerViewModel` and view beside `PlotContainerViewModel`/`PlotContainerView`, `PlotCanvasView.axaml`,
`DataDisplayViewModel`, `UndoCommands.cs`, the Data Display toolbar, and a control inspector beside
`AnnotationInspectorViewModel`.

## The gap

The Data Display canvas holds plots, marker info boxes, legends and annotations (`CanvasText`, `CanvasLine`,
`CanvasBox`, `CanvasOval`). Nothing on it is an input. A value that other plots should follow (a frequency, a drive
step, a case) can only come from a marker, which has to sit on some trace. A display built to be explored, which
the owner describes as an instrument-style front panel, needs inputs of its own.

## Requirements

**R-ddl5-1 — The model.** `DisplayControl` (in `src/Render`, no Avalonia) has an `Id` (Guid), a `Name` (`slider1`,
`knob1`, … unique per document, like markers after DDL-1 R-ddl1-2), a `Label`, `Left`/`Top`/`Width`/`Height`, a `Size`
preset (`Small | Medium | Large | Custom`) and an accent colour (optional; a theme accent when unset). A control is a
link source. It resolves to a `LinkValue` with `Scalar` set, and with one `Coordinate` when it is bound to an axis
(R-ddl5-3). Persisted as `TabConfig.Controls`, a polymorphic list in the `CanvasAnnotation` pattern
(`[JsonDerivedType]`, discriminator `Kind`), so later kinds are additive.

**R-ddl5-2 — Size.** S, M and L are fixed default sizes per kind, chosen in the control inspector. The bottom-right
gripper resizes freely, exactly as a plot's does (`PlotContainerView`'s `ResizeHandle`: hidden until hovered, the
resize is one undo step), and sets `Custom`. Picking S, M or L again snaps back. The drawing scales with the frame:
fonts and stroke widths follow the frame's short side, so a Large slider is not a Small one with more empty space.

**R-ddl5-3 — The slider.** `SliderControl` holds one of two domains:
- **Bound to an axis.** A source, a cube and one of its axes (picked from the display's data sources, as the trace
  card picks). Its stops are that axis's samples. Its value is a sample, so it supplies exactly the coordinate a marker
  on that axis would (DDL-0 D6, the owner's "a marker index under the hood").
- **A range.** Start, stop, a step or a point count, a unit, linear or log. Its value is a number with a unit.
  Consumers take the nearest sample (DDL-1 rule 3).

It also holds an orientation (horizontal or vertical), and whether to show ticks, the end labels and the value
readout. The value is persisted, so a display reopens where it was left.

**R-ddl5-4 — Operate and arrange.** On the canvas, the control's face operates and its frame arranges. The face is the
track and thumb: dragging the thumb, or clicking the track, sets the value. The frame is the label strip and border:
dragging it moves the control. Alt-drag moves it from anywhere. A click on the frame selects it, and a selected
control steps its value with the arrow keys, as a selected marker does. Copy, paste, duplicate and delete behave as
they do for plots. This rule is reviewed in UiSnapshot before DDL-6 adds two more controls on it (DDL-0, Risks).

**R-ddl5-5 — Undo and dirt.** Adding, removing, moving, resizing and editing a control are undo steps (new commands in
`UndoCommands.cs`, shaped like `AddPlotCommand`, `MovePlotsCommand` and `ResizePlotCommand`). A value change is one
step when the drag ends, not one per pointer move, and it marks the document dirty, because the value is saved.

**R-ddl5-6 — Drawn by Skia, everywhere.** `ControlRenderer.Draw(canvas, control, theme, bounds)` in `src/Render` draws
every control. The canvas view hosts it in a Skia-drawn control, as `PlotControl` hosts `PlotRenderer`. `PlotComposer`
places controls on the exported page, as it places annotations and marker boxes, so Copy, Export (SVG, PDF, PNG) and
`circuitrf render x.cdd` all draw them. Nothing in a control's look uses a blur mask filter or an image filter
(DDL-0 D7).

**R-ddl5-7 — Adding one.** The Data Display toolbar gains **Controls ▾** (Slider now; DDL-6 adds the rest). It places a
Medium control at the next free canvas slot, by the rule Add Plot uses (`ComputeNewPlotPosition`, so it always lands
in view), and opens the inspector on it. The inspector edits the name, the label, the domain, the size and the accent.
Any list it offers is a picker, never a text box (`src/Ui/CLAUDE.md`, "Prefer a picker"). A domain that cannot be
resolved (its source is gone) keeps its last value and range and says so, as a frozen link does.

**R-ddl5-8 — Linking to a control.** Once DDL-2 exists, its pickers list controls beside markers. Before that, a typed
`@slider1` already works through DDL-1's parser, because a control is a source like any other.

**R-ddl5-9 — Lock Position.** A control's context menu (right-click on the control) has **Lock Position**, a checkable
item in the style of the plot menu's **Lock Axes Panning** (`PlotControl`'s menu: the same checkbox icon, ticked when
locked). A locked control:
- cannot be moved or resized. A frame drag and an Alt-drag do nothing, the gripper is not shown, and a multi-selection
  that includes it moves the others and leaves it in place;
- still operates. Its value changes as usual, and the frame drag, which would have moved it, may operate the face
  instead;
- can still be selected, edited in the inspector, copied and deleted. Deleting is a separate, undoable act, not a
  move.

When several controls are selected, the item applies to all of them. It shows ticked only when every one is locked,
and choosing it then unlocks them all. Locking and unlocking is one undo step, marks the document dirty, and is saved
(`DisplayControl.Locked`). This differs from `LockedPanning`, which `AxesConfig` does not persist: a panel arranged
and locked must reopen locked. A locked, selected control shows a small lock glyph at its gripper corner. The glyph is
canvas chrome and never exported. New controls start unlocked, and a pasted control keeps the lock state it was copied
with.

**R-ddl5-10 — `render --control`.** `circuitrf render x.cdd --control slider1=2.4GHz` (or `=#5` for a bound slider's stop)
sets a control's value for one render, by DDL-1 R-ddl1-10's grammar, so an agent can draw the panel at any setting.

## Gates

1. **Bound slider.** A slider bound to a loadpull's `gridPoint`, linked from `Pout[@slider1, :]`: stepping it through
   every stop draws each `Pout[k, :]` exactly.
2. **Range slider.** A range slider in MHz linked to a GHz `freq` axis: the nearest sample, by DDL-1 rule 3.
3. **S/M/L and the gripper.** UiSnapshot PNGs of a slider at S, M, L and after a free resize, looked at. The thumb, the
   ticks and the label scale with the frame, and nothing clips.
4. **Operate versus arrange.** A scripted drag on the thumb changes the value and not the position. A drag on the frame
   moves the control and leaves the value alone. Each is one undo step.
5. **Vector export.** Export a display holding a slider to SVG and PDF: the slider is vector paths (no `<image>`
   element in the SVG), and `render` of the saved `.cdd` matches the export byte for byte (the RND rule).
6. **Round trip.** Save, reopen: same position, size, domain, value and lock. A `.cdd` from before this brief opens
   unchanged.
7. **Lock Position.** Lock a slider: a scripted frame drag, an Alt-drag and a gripper drag leave its bounds unchanged,
   and a thumb drag still changes its value. Select it with an unlocked plot and drag: only the plot moves. The menu
   item is ticked. Undo unlocks it. A mixed selection shows the item unticked, and choosing it locks both.

## Not here

The knob, the button and the readout (DDL-6); control values inside expressions (DDL-6); plugins (DDL-7); a "play"
button that sweeps a slider.
