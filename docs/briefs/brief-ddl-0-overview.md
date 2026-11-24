# Brief series DDL — linked plots and display controls

**Goal.** A marker on one plot, or a control placed on the Data Display canvas (a slider, a knob, a button), supplies
an index, a value or a position. Other plots use it to choose what they show, and they follow it live as it moves.
"Click a point here, see its detail there" becomes something a `.cdd` can hold. A Data Display can then be built as an
interactive panel, and the same model is the seam a plugin will later use to drive one.

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl<n>-<k>` · **Origin:** GitHub issue #86, extended by the owner with display
controls and off-grid loadpull reads.
**Design note:** `docs/design/data-display.md` §2.10 (written by DDL-1, completed by DDL-8).

## Why

Many results are cubes in which one axis is a choice among cases (a load, a bias point, a drive level, a Monte Carlo
trial, a frequency), and the other axes are the curve to inspect. An overview plot shows every case and a detail plot
should show one. Today a slice index is a typed integer (`Pout[5, :]`), so the two cannot be connected. Every detail
plot is hard-coded to one case, or the whole family is drawn and read off a legend.

The cases issue #86 names, with the shape of the data each one has today:

| Overview | Detail | Axis the link pins |
|---|---|---|
| Loadpull: Smith plot of `GammaLoad[gridPoint]`, or Pout/PAE contours | `Pout`, `Gt`, `PAE` against drive; a table of ZL and efficiency | `gridPoint` (or, off-grid, a Γ position — DDL-3) |
| FET I-V: Id against Vds per Vgs, every bias point drawn | `Y[point, freq, 2, 2]` as S-parameters, on Rect and Smith | `point` |
| Gain against frequency | A multi-frequency loadpull's contours | the contour's frequency index |
| Monte Carlo: a scatter or histogram of a figure of merit | The full response of one trial | `trial` |
| HB: Pout and PAE against Pin | `HB1.V[node, harmonic, sweep]` spectrum or waveform | the sweep axis |

## The model, in one paragraph

A **link source** has a name and a persistent id. In this series it is a marker or a display control. Resolving a
source gives a **link value**: for a marker, the sample it sits on, as one coordinate (axis, index, value, unit) for
each axis its trace keeps (X, and the family axis when there is one), plus a Γ/Z position when the marker is on a
Smith or contour plot. For a control, its value and, when it is bound to an axis, its index. A **link consumer** is
any place that today takes a fixed index: a pinned slice axis (`Pout[@m1, :]`), a contour's frequency index, a
summary table's operating point, and later an expression operand. Each consumer resolves its links in dependency
order, every time a source moves. All of the resolution lives in `src/Render`, beside `TraceResolve`, so
`circuitrf render` draws a linked `.cdd` exactly as the window does (`data-display.md` §2.9).

## The series

| Brief | Delivers |
|---|---|
| [DDL-1](brief-ddl-1-links-headless.md) | The link model and its resolution in `src/Render`: persistent marker ids, document-wide marker names, the `@name` slice token, the matching rules, frozen fallback, cycle refusal, `.cdd` fields, `render --marker` |
| [DDL-2](brief-ddl-2-links-in-the-display.md) | Linking in the window: the pin row's link picker, live follow while dragging, the legend, the dangling chip and one-click relink, undo, marker re-point, copy/paste remapping |
| [DDL-3](brief-ddl-3-loadpull-off-grid.md) | Loadpull at a marker anywhere on the Γ plane: a free contour marker drives a synthesized drive-up (`LoadpullSurface.GetPowerSweep`) and a summary table read "at m1" |
| [DDL-4](brief-ddl-4-more-sources-and-consumers.md) | Family and versus markers (the I-V case), the contour frequency index, the expression frequency index, the selected trial as a link source, HB sweeps |
| [DDL-5](brief-ddl-5-controls-and-slider.md) | Display controls as canvas items: the model, S/M/L and the gripper, move/select/undo, the Skia renderer in export and `render`, and the first control, the slider |
| [DDL-6](brief-ddl-6-knob-button-readout.md) | The knob, the illuminated button and the readout indicator; control values in trace expressions; a trace shown or hidden by a control |
| [DDL-7](brief-ddl-7-plugin-bridge.md) | **Not scheduled.** The seam a plugin uses to read and drive display variables, and to declare a panel of display controls; instrument control |
| [DDL-8](brief-ddl-8-docs-and-example.md) | The user page, `data-display.md` §2.10, an example workspace with overview and detail tabs and a slider, DocGen figures |

DDL-1 and DDL-2 make use cases 1, 4 (on a scatter) and 5 work at measured points. DDL-3 adds the off-grid load.
DDL-4 covers the rest. DDL-5 and DDL-6 can start once DDL-1 has landed. They do not need DDL-2–4.

## Decisions

- **D1 — Links are by id, shown by name.** A marker's `Guid` is persisted in the `.cdd` (it is not today), and a link
  stores the id. Renaming a marker keeps its links. Deleting one cannot let a later, unrelated marker that happens to
  reuse the name take the links over without anyone choosing it.
- **D2 — A dangling link freezes, and is said.** When a source is gone (deleted, or its trace removed), each consumer
  keeps the index it last resolved to and draws as a static plot. Its card says so ("m3 is gone — showing gridPoint 17,
  where m3 last was") and offers **Link to ▾**. When a source with the same name exists again, the chip offers it in one
  click ("Use the new m3"). It never relinks by itself. Undoing the deletion brings the same id back, so the link is
  live again with nothing to choose.
- **D3 — Same axis is an index; anything else is a value.** A coordinate on an axis of the same name, length and
  values as the consumer's is used as an index. Otherwise the consumer takes the sample nearest the source's value,
  in the same unit. Different units are refused by name. This lets a marker on one run's frequency axis drive another
  file's frequency axis, while the common case of the same run stays exact.
- **D4 — Cycles are refused when they are made.** Plot A pinned by a marker on plot B, whose own pin follows a marker
  on A, is a cycle. The link picker greys a source that would close one, and a typed `@m1` that would close one is an
  error on the spec. A loaded file that holds one resolves the cycle's members frozen, with a note.
- **D5 — Marker names become unique per document.** Today `GetNextMarkerIndex` is per tab, so two tabs can each have
  an `m1`. New markers take the lowest number free in the whole document. Duplicates in existing files keep their
  names: a typed `@m1` resolves within its own tab first, and is refused as ambiguous only when neither tab is the
  consumer's.
- **D6 — Controls are display variables, not markers.** The owner's sketch had a slider as "a marker index under the
  hood". The control model keeps that idea: a control bound to an axis supplies exactly the coordinate a marker on
  that axis would. A control is also allowed a plain range (start, stop, step, unit) that is not any file's axis, and
  a button's 0/1 is not an index into anything. So controls are their own kind of link source, and both kinds resolve
  through one `LinkValue`.
- **D7 — Every pixel is Skia, vector on export.** Controls are drawn by a renderer in `src/Render`, used by the
  canvas, by Copy/Export and by `render`. The button's glow is built from gradients, never from a blur mask filter or
  an image filter: those rasterise or vanish in Skia's SVG and PDF output, and an exported panel must stay vector.
- **D8 — A fixed control vocabulary.** Slider, knob, button and readout, each added because a use case needs it. This
  is the plugin design's own rule (`plugins.md` P-3, P-8): a panel is declared from a small fixed set, not programmed.
  It is what lets a later plugin declare a display panel without circuitRF loading its code (DDL-7).

## Risks and frank opinions

- **Live follow can be expensive.** Re-slicing a trace is cheap. A contour refit, or the first build of a loadpull RBF
  stack, is not. DDL-2 coalesces updates to the latest position per frame. A consumer whose resolution is slow
  updates on release, and the card says so. This is decided by what the consumer is (a contour or an off-grid
  loadpull read), not by a timer.
- **"A front panel" is a direction, not a scope.** Four controls and a variable bus deliver most of the interactive
  panel. A general UI builder (layouts, containers, scripted behaviour) would duplicate the plugin panel and break
  P-3. Instrument control belongs to a plugin process (DDL-7), never to circuitRF loading drivers.
- **Operate or edit.** A control that is dragged to change its value must also be movable and resizable. DDL-5 uses
  this rule: the control face operates and the frame moves. Alt-drag moves it from anywhere. **Lock Position** (the
  context menu, like Lock Axes Panning) pins a finished panel's controls so that nothing moves them. This needs visual
  review in UiSnapshot before DDL-6 builds two more controls on it.
- **Interpolating a loadpull is a model, not a measurement.** DDL-3 draws a synthesized curve differently (dashed)
  from a measured one, and says when the marker is outside the measured loads.

## Not here

Scattered-data interpolation for anything but a loadpull (the FET bias plane takes the nearest point); links across two
`.cdd` files; a marker that drives a simulation (a sweep re-run at the marker's value); animation or "play" on a
slider; plugin-supplied control kinds.
