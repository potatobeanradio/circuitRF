# src/Ui — resolved briefs (detail, off the CLAUDE.md growth path)

`src/Ui/CLAUDE.md` reached 21,417 lines as an append-only phase log and had to be archived to
`src/Ui/HISTORY.md`. Going forward, a completed brief's detail lands here instead — one `##` section
per brief, sparingly, only for findings that are still true, still surprising, and would cost someone
real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions only. Mirrors
`src/Ui/DataDisplay/RESOLVED.md`'s own pattern.

## wBond editor, round 2 — the toolbar, the arrangement, and four gesture rules that were quietly inverted or absent (2026-08-16)

**Two invariants worth keeping in mind before touching either canvas** (they were written into
`src/Ui/CLAUDE.md` in round 1 and moved here):

- **A `LayoutCanvas` overlay clips ITSELF, and is handed the layout's own `LayoutRenderTheme`.**
  Nothing else clips the overlay pass — the layout underneath is culled against the viewport before it
  is drawn, but an overlay draws whatever it holds, on screen or not, and `LayoutCanvas` sets no
  `ClipToBounds`. `ILayoutCanvasOverlay.Draw` takes the theme so shared visual language (the selection
  accent above all) cannot drift from the layout's own.
- **A wire edit repaints through `WBondViewModel.ReadoutChanged`, and BOTH canvases must listen** — a
  wBond edit deliberately never touches `LayoutView.Changed` (WB23/WB17), which is the only thing that
  repaints `LayoutCanvas` on its own. A SELECTION change raises neither, so it needs its own
  subscription; without it, clicking empty space in the layout view left the same wires drawn as
  selected in the profile view.

**The profile view's absolute span axis had its origin on the wire's own input foot, and three
owner-reported bugs were that one fact.** `Points[0]` sat at span 0 permanently: it could not move in
that view whatever happened to it in the world, and any motion of it was rendered as motion of
everything ELSE. So an alt-drag anchored on the output foot DREW the output foot moving — while the
layout view drew the truth, which is why the two views disagreed about the same gesture — and a plain
drag of the start point left it glued in place while the rest of the curve slid out from under the
cursor ("regular click-drag of the start of a wire is changing span"). Absolute now measures from the
WORLD origin, along the wire's own chord direction under AUTO and along the view direction under a
fixed plane. **`SpanMode.Normalised` keeps the foot-relative 0..1 origin** — that is the
shape-comparison mode §6.2 argues for, and it still overlays wires of any angle and length; Absolute's
whole stated purpose is "true geometry", and a true picture cannot re-origin itself on the point being
dragged. The envelope band had to follow: it carries a NORMALISED span, so it is mapped onto the
reference wire's projected origin AND extent, not just its length. Separately, the profile canvas's
alt-drag was missing the anchor SIGN FLIP the layout overlay has carried since it was written — pulling
the input foot backwards along the axis is what lengthens a wire.

**`LayoutCanvas.ZoomToFit` unions the layout's shapes and instances — and an overlay's content is in
neither.** A wBond document on an empty scratch layout therefore fitted to an EMPTY extent and landed
at `LayoutViewport.Default`, with every wire off screen. `ILayoutCanvasOverlay` now declares
`ContentBounds()`, in the canvas's own DBU (the nm→DBU bridge crossed there, and the descent transform
applied first — framing untransformed coordinates would frame a place the wires are not). Note that a
wire-less design is unreachable: `WBondDesign.Validate` refuses both an empty array and an array-less
design, because either makes the array-basis inductance singular. The one reachable empty case is
depth with an uncomposable chain (WB27), where the wires are deliberately not drawn.

**The layout renderer accented whole WIRES and nothing finer**, so a segment picked in the profile view
lit up there and showed nothing in the layout view — and a picked INPUT foot lit up nowhere at all,
because the input-end colour (WB3) outranked the accent unconditionally. Both views now share one
`SegmentSelected`/`PointSelected` pair, and a selected point outranks the input-end colour: the accent
is transient and says what the user is holding, and the end is still identifiable while selected
because it is still the wire's first dot. The per-kind nature of this defect is why it kept resurfacing
— whole-wire selection always worked, which is exactly what hid it — so the guard is a pixel oracle run
over all four kinds (wire, segment, interior point, input foot) in BOTH views.

**A live marquee's contents belong on the shared view-model, not inside the canvas that owns the
gesture** (`WBondViewModel.PreviewSelection` / `EffectiveSelection`; every renderer reads the latter,
none reads `Selection` to draw). A wire caught by a box dragged in the profile view is the same wire in
the layout view and has to light up in both. Two more things had to change for the profile half to
show anything at all: **it accented whole WIRES only**, so an enclose marquee — whose whole job is
catching some of a wire's vertices — appeared to select nothing; and it **skipped every bound member
but the representative**, so a marquee catching members of a twenty-wire array highlighted nothing. A
selected wire is now always drawn individually, because the band is one shape over the whole array and
cannot carry a highlight. A counter cannot tell "drawn" from "drawn highlighted", so that one is
guarded by a pixel oracle.

**A press must not resolve the selection, and a press must not open a gesture.** Three separate
owner-reported defects were the same two lines of `OnPointerPressed` in each canvas:

- *"Clicking on the selection starts a new selection."* The press re-resolved the hit unconditionally,
  so grabbing three selected segments to move them collapsed the selection to the one element under
  the cursor and dragged only that. Now a press on something the selection already covers **keeps** it
  — and, so an element inside a selected wire stays reachable, a gesture that turns out to be a plain
  click re-resolves on RELEASE (`_deferredPress`). That click-through is why
  `HoldingW_PromotesAClickToTheWholeWire` now has to release before asserting.
- *"Clicking on the start point of a wire changes the span."* The press opened the gesture immediately
  and the first move measured its delta from the UNSNAPPED press point — so a click with a pixel of
  hand-shake snapped the grabbed foot onto the nearest pad corner, and a moved foot is a changed span.
  Two fixes: the drag baseline is the **snapped** press point, and nothing happens at all until the
  pointer leaves the hit tolerance (`_dragThresholdNm`, `DragThresholdPixels`). A click therefore also
  leaves no undo entry, which it previously did on every single click.

**The alt-drag anchor was inverted, and the double negative is where it went wrong.** WB26a's rule is
"grabbing near an end IS the instruction to move that end". `ScaleSpan` takes `moveOutputFoot`, so the
helper must answer *which foot moves* — a first version answered *which foot was grabbed near*, and an
alt-drag on the output end pulled the INPUT end, shrinking the wire when the hand said grow. The
helper is now named `GrabMovesOutputFoot` for that reason. **Alt-drag also now scales span AND height
together, every frame**: the old code declared one axis on the first few pixels of travel and ignored
the other for the rest of the gesture, so a diagonal alt-drag silently did half of what it looked
like. And it **works on a detached wire** — it used to look up the selection's bound profile, find
none, and do nothing while saying nothing (`WireEdits.ScaleWires` / `WBondViewModel.ScaleSelection`).
**Alt-drag in the LAYOUT view scales span too**, which it never did; the displacement is projected onto
the wire's own chord, so a drag perpendicular to the wire correctly changes nothing.

**The profile view's plane is now a SETTING, not a derivation.** Round 1 labelled the plane it happened
to be showing; the owner's answer is that a user needs to *choose* it. `ProfileProjection.Project`
takes an optional azimuth: null is AUTO (each wire on its own chord — §6.2's parameterisation, and the
only mode in which two wires of different angle are comparable), a number fixes the plane. Under a
fixed plane a wire running perpendicular is foreshortened to **nothing**, which is what looking down a
wire actually looks like and is why AUTO is still the default. `ProfileAxisSetting` owns the text round
trip so the combo, the persisted view state and the parser cannot disagree ("90" reads back as "Y-Z").
The **band** had to be projected too: it carries normalised span, so it is scaled by the reference
wire's extent *in the current projection* — reading the plain chord length would leave the band at full
width in a plane the curves are foreshortened in.

**The profile view got its own marquee, and it is resolved against span and z**, not world x —
`SelectionResolver.ResolveMarquee`'s `spanOf` hook exists for exactly this and was previously unused.
Live preview, kept separate from the committed selection, same as the layout side.

**A `LayoutView`'s `SnapDbu` defaults to ZERO, and zero means "no grid" to `LayoutRenderer` as well as
"no snapping" to the editor** — which is why the wBond layout view drew no grid at all. A reference
layout attached to a wBond document now gets 1 mil if it has none (`OnReferenceLayoutChanged`). The
metadata bar's Snap box is the reference layout's OWN ladder and its own three handlers, bound
straight through; it sets the grid pitch for **both** canvases and the fallback wire-point snap
(geometry first, grid second — a grid that overrode a pad corner would pull the foot back off the pad).
The profile canvas reuses `LayoutGridMath.ComputeGridPitch` rather than `LayoutRenderer.DrawGrid`,
which is bound to a `LayoutView` and a `LayoutViewport` this view does not have: the part that can be
*wrong* is the decimation, and that is the part that is shared.

**Hiding a focused control orphans the focus, and this view's key handler is gated on
`IsKeyboardFocusWithin`** — so cycling away from the canvas the user was working in left the editor
deaf to its own shortcuts until they clicked something ("pressing V repeatedly does not cycle unless I
click on a canvas between keystrokes"). `ApplyArrangement(restoreFocus: true)` puts focus back on a
canvas that is still on screen, and only ever when focus was already inside this view, so it can never
yank focus out of a field elsewhere in the application. **The cycle key is `V`, not `Tab`**: Tab is the
focus-navigation key every Avalonia control expects, so claiming it would have to out-race the focus
manager in every host this view is embedded in, and would leave keyboard users unable to walk the
toolbar.

**The Snap box is formatted in the LAYOUT's `DisplayUnit`, which defaults to microns** — so a document
set to `mil` offered a snap ladder in µm right beside a Unit box saying mil. The editor's chosen unit
is now mirrored into the reference layout (`PushDisplayUnitToReferenceLayout`), which carries the
ladder, the snap text, the cursor readout, the extent and Zoom 1:1 with it. §6.5's "independent of the
`.ctech` display unit" is untouched: that rule is about the wBond not being FORCED to follow the
technology's unit, and the arrow here still points the other way. The two enums list the same five
units in the same order, and the mapping is written out rather than cast for exactly that reason — an
ordinal cast would keep compiling and start lying the moment either gains a member.

**The snap ladder gained two SUB-unit rungs** (`SnapLadderMultipliers` is now
`0.1 · 0.5 · 1 · 5 · 10 · 25 · 50`, in `decimal` because a `double` cannot hold 1 mil = 25,400 nm
exactly). It stays R-snp-2's RELATIVE ladder — multiples of the technology's own default snap — so on a
1 mil process the new rungs are the "0.5 mil" and "0.1 mil" the owner asked for, and on any other
process they are the same fractions of its step. A rung that quantises to zero DBU is dropped: zero is
`LayoutSnapping`'s OFF state, not a fine snap, so offering it in a distance list would be a trap.
**This changes the Layout Editor's ladder too**, deliberately — it is one control with one rule.

**A new wBond document snaps at 0.1 mil off a 1 mil LADDER, and those are two separate statements.**
With no technology the ladder falls back to the document's own snap, so seeding the snap to 0.1 mil
would have re-based the whole ladder and offered a 0.01 mil finest rung. `SnapLadderBaseDbu` is the
explicit base a host can state; it is a base and never a selection, so R-crash-1's "an items collection
must not be a function of the selection made from it" is untouched. `RefreshSnapLadder()` exists
because the layout view-model's constructor builds the ladder before the wBond document has seeded
anything — without it the editor kept offering the µm-scale fallback rungs all session.

**View arrangement persists in the `.wBond`'s own opaque `ViewState` field, with NO format-version
bump** — that field exists so the UI can persist what the framework-free half must not parse, and an
older build reads the string, understands none of it and writes it back unaltered. Every field is
optional and malformed JSON takes the defaults: a view setting is never worth refusing to open a design
over. **The row/column collapse is written from code-behind, not bound**: a `GridSplitter` writes a
concrete `GridLength` straight into the definition it resizes, silently replacing any binding on that
property, so a bound collapse would work exactly until the first time the user dragged the splitter.

**Panel:** lengths now carry per-unit precision (`Decimals`) chosen so one digit is worth roughly the
same physical amount in every unit — mil pinned at one decimal per the owner, pH likewise. The card is
name + inductance + expander, collapsed by default; the "redundant pH readout" was the SELF term being
listed again among the mutuals, so only that entry is dropped and cross-array mutuals stay (under the
fold). The return-path line is suppressed for the ordinary image plane at z = 0 — a sentence that says
the same expected thing on every document costs a row and tells nobody anything, while the UNDECLARED
case WB20/RW13 exists for is unconditional.

## wBond editor — eleven owner-reported defects, and the two that were the same bug wearing two faces (2026-08-16)

**The profile view's "one editable curve per array" was never drawn — only the band was.** `wbond.md`
§6.2 idea 3 is *one curve plus a translucent min/max envelope*; `WBondRenderer.DrawProfile` drew the
envelope and `continue`d past every bound member. The envelope is a min/max over the bound members, so
**whenever those members share a shape, min == max at every sample and the band is a zero-area path
that fills nothing.** Two ordinary situations hit that: a ONE-WIRE array (the shipped default
document), and *any* array mid-drag once `QualityLadder` has collapsed its members onto their chords
(WB15) — which is exactly the owner's "the profile view sometimes disappears while dragging wire
segments in the layout view". The fix draws `envelope.BoundWires[0]` as the representative curve in
`theme.Wire`. **A counter test cannot see this**: the old code emitted a path, it just filled nothing,
so the guard is a rendered-pixel oracle on a single bound wire.

**Nothing clipped the wire pass.** The layout underneath is culled against the viewport before it is
drawn; `WBondRenderer.Draw` iterates *every* wire in the design regardless of where it lands on
screen, and `LayoutCanvas` sets no `ClipToBounds`. So a wire off the left edge painted straight across
the inductance panel docked beside the canvas. `WBondLayoutOverlay.Draw` now saves/clips/restores
against `viewport.Width`/`Height`. Verified by removing the clip and watching the test go red.

**A Properties-panel edit repainted the profile canvas and not the layout canvas**, because only
`WBondProfileCanvas` listened to `WBondViewModel.ReadoutChanged` — the overlay only ever raised
`OverlayChanged` from its own gestures, and the layout canvas repaints on `LayoutView.Changed`, which
a wire edit deliberately never touches (WB23/WB17). Reported as "changing the Span takes seconds; Loop
ht is fast", and the asymmetry is the tell rather than the cost: **the model path is ~0.05 ms per
commit, measured, and Span and Loop height are within noise of each other** — span's visible effect is
in the layout view (a foot moves in XY) and loop height's is in the profile view, which was already
repainting. `WBondEditorView` now subscribes `ReadoutChanged → RepaintBoth`.

**The profile view's horizontal axis now moves geometry, and the mapping is the chord.** A plain drag
used to be z-only, on the stated grounds that span is derived and "move this point sideways" has no
single answer. It does: displacement **along that wire's own XY chord**. `WireEdits.Translate` owns
it, so the drag and the arrow-key nudge got it together, and a wire with coincident feet in XY is
skipped rather than guessed. The old code's profile `dx` was applied as world x, which for any wire
not already parallel to x moved the point *off* its chord and barely changed its span.

Smaller, but each a real defect: the panel's Total length / Landing span were **hard-coded to mm**
(now `WBondPanelViewModel.Unit`, pushed from `Editor.DisplayUnit`; inductance deliberately stays pH
per WB27a/D9); the toolbar unit picker showed the bare enum (`Mil`, `Um`, `Inch`) and now shows the
**suffix strings themselves**, so the picker offers exactly what `WBondUnits.TryParseUnit` accepts;
the marquee's fill alpha, hairline stroke and dash period are now transcribed from
`LayoutRenderer.DrawMarquee` and its colour is the **same `LayoutRenderTheme` object the layout
underneath was drawn with** — `ILayoutCanvasOverlay.Draw` takes the theme for that reason; the marquee
highlight is live, with the preview kept **separate from the committed selection** for the reason L1i
already established (the committed selection is also the Shift-base, so a self-writing preview can
never shrink), and preview and commit share one `WBondPointerController.ResolveMarquee`.

**Escape belongs to the VIEW, not the canvases.** `WBondLayoutOverlay.OnKeyDown` could already cancel
a half-placed wire and clear a selection — but it cannot un-press a `ToggleButton`, so the tool stayed
armed, the next click started another wire, and Escape read as doing nothing. `WBondEditorView`'s
tunnel handler now unwinds one step at a time: disarm Draw/Rotate (which cancels a half-placed wire
through `WireDrawArmed`'s own setter) → clear the selection → leave the key **unhandled** so an
ancestor still sees it.

**The profile view states its plane** (`ProfileProjection.AxisLabel` → `WBondViewModel.ProfileAxisLabel`):
X-Z, Y-Z, or the azimuth for a diagonal array. The layout view needs no counterpart — it is always
X-Y. An axis is named only within 5°; rounding 45° to "X-Z" would be a plausible-looking wrong answer.
Rendered as a `TextBlock` over the canvas, not Skia text, to stay clear of the headless-typeface trap.

## Round 11 — a K edit must not invent a marker, and per-band menus need their own signal (2026-08-15)

`HarmonicaViewModel.RetargetTerminations` used to rebuild the marker list wholesale, which applied
§4.2's "S1/L1 are always present" rule and so **made an S1 marker appear whenever HB Order was
edited**, on a document that deliberately had none (R8B §3: S1/S2 start with no marker). It now prunes
`Markers` instead — dropping only bands above the new K, keeping the surviving instances and the
session state hanging off them. The load path still rebuilds wholesale, and must: a loaded `.charm`
has nothing but its terminations to reconstruct markers from.

**The coupling that broke when it was removed, and the reason this is here.**
`HarmonicaMenuViewModel.RebuildBandMenus` learned about a K change ONLY by observing
`Markers.CollectionChanged` — its own doc comment called that "one signal, three lists". It worked by
accident of the wholesale rebuild, so removing the rebuild broke *raising* K (which drops no marker
and notified nobody) while lowering K still worked, and the failure surfaced as a Contour Harmonic
menu stuck at 3 items. K now raises `HarmonicaViewModel.HarmonicCountChanged` in its own right.

**Ctrl/⌘+L toggles Display ▸ Grid Points, and the two modifiers live on DIFFERENT surfaces on
purpose.** ⌘L is a `Gesture` on both NativeMenu surfaces (`HarmonicaAppMenuInjector` and
`HarmonicaMenuView.axaml`); Ctrl+L is a `KeyBinding` installed on `HarmonicaView` alongside the menu
view model. A macOS menu key equivalent is consumed by AppKit before Avalonia's input pipeline runs,
so declaring the same gesture on both surfaces would give one keystroke two live handlers and toggle
the setting twice — i.e. do nothing. The in-window `MenuItem`'s `InputGesture` is display-only in
Avalonia (`HotKey` is the functional one), which is why it can safely label Ctrl+L without becoming a
second handler for it.

## Round 10 follow-up — current probes, node labels and a PA measurement block on the exported testbench (2026-08-15)

The owner supplied a hand-drawn testbench (`Example.csch`) showing the shape wanted: `IProbe`s in the
signal and bias paths, net labels on the four interesting nodes, and `MEAS` blocks whose equations
name them. The export now writes all three, and the exported schematic reports Pout / Gp / Gt / IRL /
Zin / Idc / Pdc / DE / PAE on its own.

### The orientation question, which is the whole of the risk

An `IProbe` reports the current flowing `np → nm`. Its pins are local `(0, +100)` and `(+100, +100)`,
so **`np` sits at the component's own X in BOTH mirror states** and `MirrorX` is what decides which
side `nm` lands on — that asymmetry is what makes the placement arithmetic a single case. Insert one
backwards and every derived number keeps its magnitude and flips its sign, which is exactly what the
owner warned about. Four probes, and they are NOT all oriented alike:

| probe | measures | orientation |
|---|---|---|
| `Iin` | current into the DUT's gate plane | np left (chain is built outward, power flows inward) |
| `Iout` | current out of the DUT's drain plane | np left (chain and power both run outward) |
| `IDC` | current leaving VDD, which sits RIGHT of its choke | np right — **mirrored** |
| `Igate` | current leaving VGG, which sits LEFT of its choke | np left — **not** mirrored |

`PlaceProbe` takes `currentAlongTravel` rather than a mirror flag, because "does the current I want run
the same way I am building this chain?" is what a caller actually knows.

**The example file mirrors `Igate` as well as `IDC`**, which makes its own gate term negative. Ours
does not, deliberately: both probes measure current OUT of their own supply, so `V(supply)·I(probe)` is
power DELIVERED on both sides with no sign correction anywhere. **That term is not negligible on the
shipped device** — its gate is a plain 50 Ω to source, so at Vgs = −3.05 V it draws −61 mA and the
negative supply really delivers +0.186 W. Dropping or flipping it moves DE by about 1.6 points.

### Verified end to end, through the product's own path

`HarmonicaExportedMetricsTests` extracts, elaborates, runs `HbEngine` and evaluates the block through
`MeasurementEvaluator` — the same four steps `Cli hb` takes. At the shipped default, 20 dBm, load
80 + j10 Ω:

```
Pin_avail 20 dBm · Pin_deliv 0.1 W (20 dBm) · IRL −4e-10 dB · Zin 50.000 + j2.0e-7
Pout 5.545 W (37.44 dBm) · Gp 17.44 dB · Gt 17.44 dB
Idc 0.2367 A · Pdc 11.543 W (= 48·0.2367 + (−3.05)(−0.061)) · DE 48.04 % · PAE 47.17 %
```

IRL is identically zero because the source presents 50 Ω and the DUT's gate IS 50 Ω — which also means
a reversed `Iin` would give a NEGATIVE `Pin_deliv` and `10*log10` would throw rather than land on 0 dB.
**And the cross-check that matters:** the exported schematic's own `Zin` measurement (a stamped
netlist, solved by `HbEngine`, read through an `IProbe`) agrees with harmonicaRF's own closed-form
termination closure to **~1e-11** — two genuinely different routes to one number.

### TWO ROUTER BUGS THIS SHOOK OUT, both silent, both found by the 3-port SDD

- **`AddComponent` registered a component's pins BEFORE its mirror was applied.** `MirrorX` used to be
  set by the caller afterwards, which was harmless while nothing here was mirrored — `IProbe` is the
  first. The result is a phantom obstacle at a coordinate no pin occupies AND no obstacle at the one
  that does. `MirrorX` is a constructor argument now, so the two cannot get out of order.
- **The staircase's escape leg travels along the very axis the obstacle sits on**, so it only worked
  while the obstacle was further from `a` than the step. Put a component pin one grid step short of a
  grounded DUT terminal — routine once a probe is inserted on a 3-port SDD, where two ports share one
  column and the third sits 200 units off the chain — and every candidate either lands ON the obstacle
  or crosses it on the way. All were rejected and `ConnectStraightSafely` fell back to the direct wire
  it was trying to avoid: **a short**, whose only symptom is a `SingularMatrixException` from the
  engine with nothing in the drawing to point at. `EscapeCandidatesDbu` now leads with **0** — turn
  perpendicular immediately, an ordinary Z-bend, which needs no room along the blocked axis at all —
  and ends with the negative half as a last resort. `TryStaircase` became `TryRoute`, a general
  point-list route that collapses consecutive duplicates first, which is what lets the zero-escape
  candidate degenerate gracefully instead of failing its own midpoint check against `a`.

## Round 10 — the `.csch` export rebuilt, the VSWR circle unrestricted, the intrinsic glyph un-compressed (harmonicaRF fixes, 2026-08-15)

### The `.csch` export carried a SIGN INVERSION on both supplies, and nobody could see it

`HarmonicaSchematicExport.PlaceTerminationTail` grounded the `Vdc`'s **pin 0** and fed the bias choke
from **pin 1**. Pin 0 is the "+" terminal (`BuiltInSymbols.BuildVdcSource` draws the `+` marker at
local y = −100; `VdcModel.Stamp` constrains `V(Nodes[0]) − V(Nodes[1]) = Vdc`) — so **every schematic
this exporter ever wrote put −Vgs on the gate and −Vds on the drain**. It was invisible because the
only gate the export had was "does it extract, elaborate and converge", and a sign-flipped bias
converges perfectly well; it just answers for a different amplifier. Found while implementing the
owner's §12 (a wire drawn straight down through the Vdc symbol), which is the same code. The supplies
are now placed to the OUTSIDE — gate supply left, drain supply right, both one pitch above the choke —
so the "+" wire leaves the pin sideways before turning down, and the "−" is grounded where it stands.
`HarmonicaSchematicExportR10Tests.BothSupplies_FeedTheirChokeFromThePlusPin_...` pins both halves.

### A Tuner under a plain `type=hb` run presented `Z[1]` AT EVERY HARMONIC — fixed in the engine

The owner asked for the load to be a `LoadTuner` named `Load` (§8) instead of the tone-less `PnTone`
this file used to write. That exposed a live engine defect: `TunerModel.GetZ` takes its "S-param mode"
branch whenever `_toneFreqHz <= 0`, and `_toneFreqHz` was only ever set by
`LoadpullEngine`/`LoadpullPursuitEngine` (`SetTone`) — `HbEngine.Run` configured `P1Tone`/`PnTone` tone
context and **nothing else**. So *any* Tuner on an ordinary HB testbench declared `Z[2]`, `Z[3]`… and
had them quietly ignored; it ran, converged, and answered for a different circuit. This is not new to
the export — it has been true of every hand-written `type=hb` netlist with a Tuner in it.

**Fixed** (owner-approved) by `HbEngine.GiveTunerItsBandRuler`, called from the same tone-context loops
that already configure `P1Tone`/`PnTone` in both `Run` and the two-tone path. Two things make it safe:
it is **role-gated to `Load`** (a Source-role tuner's `StampSource` stamps a `V_1Tone` branch as soon as
its tone is set, at a `|Vs|` only `SetSourceDrive` computes — so an unconfigured one would stamp a
0 V source, i.e. a SHORT where there was an open; nothing outside the loadpull engines assigns a role,
so every tuner on a plain-HB testbench is already `Load`), and the loadpull path goes through
`RunSinglePoint`, which has **no** tone-context pass of its own.

**Measured both ways** (`TunerPerHarmonicZInPlainHbTests`, a square-law SDD into a Tuner whose `Z[2]`
is the only thing that varies): *without* the fix the second-harmonic load voltage is `5.0000E-002` for
`Z[2]` = 1e-6, 50 **and** 1e6 Ω alike — bit-identical, all three solved at `Z[1]`. *With* it the
implied `I₂ = |V₂|/Z[2]` is **1.0000e-3 A across twelve decades of `Z[2]`**, i.e. the tuner presents
exactly what it declares. (The 1 MΩ case reads 2 ppm low because the ideal 1 H choke is 25 GΩ at 4 GHz
and no longer utterly negligible next to it — stated in the test rather than tolerated silently.)

### The rest of the rebuild, and one trap that only a 3-port SDD can spring

- **`Num` was `"G17"`** — round-trip-safe by brute force, printing all 17 digits whether or not they
  carry information (`1e-6 H` came out `9.9999999999999995E-07`). It is `"R"` now, which has meant
  *shortest* round-trippable since .NET Core 3.0, plus an exponent tidy (`1E-06` → `1e-6`). No value
  changed; only how much of it is written down.
- **The bias network's L and C carry a unit whose SI PREFIX is chosen from the magnitude**
  (`Engineering`). A fixed `uH`/`uF` pair — the literal request — reads well at microscale and badly
  anywhere else: the shipped default is now the ideal 1 H / 1 F, which a fixed micro prefix would write
  as `1000000 uH`. The clean single digit the owner asked for is the request; the prefix is the means.
- **A ground now sits EXACTLY on the pin it grounds**, with no wire and no offset search — a Ground's
  one pin is at its own origin, so the coincidence rule `NetExtractor` already applies unions them.
  The SDD's "−" terminals each get their own symbol rather than sharing one through a wire.
- **A series element is oriented along its own run** (R90 for a left/right run, R0 for up/down), which
  is what makes the DC blocks horizontal (§7) and removes the L-bend a chain used to need to reach a
  component lying across it.
- **`BiasTee` must be written QUOTED (`"off"`), not bare.** A `.cnl` says `BiasTee=off` and a schematic
  parameter is an *expression*, so a bare `off` resolves as a variable name and elaboration fails with
  `Unresolved name 'off' in scope 'global'`. `CreateTunerModel` wants a `ValueKind.String` and only ever
  compares it to `"on"`. (This also means the registry's own `DefaultParameters` spelling for a
  hand-placed Tuner has the same shape — worth knowing before "fixing" the quotes.)
- **THE TRAP: `CoincidesWithWireInterior` cannot see a wire's own ENDPOINTS.** `PointOnSegmentInterior`
  excludes them by definition, so a brand-new component pin landing exactly on an existing wire's
  *corner* passed every obstruction check and shorted anyway. On the 3-port SDD — whose gate and drain
  sit on the SAME column, so both bias chains route up it — the drain choke's near pin landed precisely
  on the corner of the gate supply's route, tying VGG and VDD together through their chokes. The
  symptom was a `SingularMatrixException` from the engine, with nothing in the drawing to point at.
  `Ctx.WireVertices` is the fix, and it is checked by `IsObstructed` alongside the interior test.

### VSWR: the restriction was a search BRACKET, and the inverse has a closed form in RfCore

`HarmonicaVswrHandle` bisected `f(v) = |Γ_drag − ctr(v)| − rad(v)` over `[1.001, 10⁶]`, 60 iterations a
pointer-move. That whole apparatus is unnecessary: the drawn locus is the image of the power-wave
circle `|s_c| = ρ` about the marker's own impedance, so inverting it is "map the drag point back to
`s_c` and read its magnitude" — which is exactly `RfCore.RfHelpers.VswrFromGamma`. One call.

**And that is what unlocked the owner's ask.** `ρ > 1` — a drag OUTSIDE the image of the passive disk
— makes `(1+ρ)/(1−ρ)` *negative*, which `LoadpullSurface.VswrCircleGamma` draws perfectly well (it
squares ρ for the centre and takes `|ρ|` for the radius). The old bracket could not express it at all,
which is why every drag past the rim pinned at the ceiling. R8B's own note — "a passive marker's whole
VSWR family stays strictly inside |Γ| = 1, so it literally cannot be dragged outside the chart" — is
true only of the **positive** half of the family; the family continues past the rim with ρ > 1 and the
theorem was being read one clause too far. `MinVswr`/`MaxVswr` are gone, along with the floor in
`SetMarkerVswr` and the "at least 1" refusal in the Set… dialog; the only two values still refused are
the two the owner named (NaN is dropped, ±∞ becomes ±`InfiniteVswr` = 1e9).

### The intrinsic glyph was drawn on a DIFFERENT radial scale from its own marker

`IntrinsicGlyphScale` compressed everything past `|Γ| = 1` into a bounded annulus (asymptotic to
1 + 0.25) while a marker is drawn through the raw `GammaToCanvas`. Inside the disc the two agree
exactly, which is why this never showed — but drag a marker OUTSIDE the chart on the default DUT (a
bare SDD: intrinsic plane == extrinsic plane, so the two values are *the same impedance*) and the glyph
sits at radius ≤ 1.25 while its marker sits at 1.6. `IntrinsicGlyphScale.Compress` is now `false` and
`DisplayRadius`/`TrueRadius` are the identity up to `MaxTrueMagnitude`. **The cost is stated rather than
hidden**: a glyph with a large `|Γ_intr|` can be clipped at the panel edge again, which is exactly what
the compression existed to prevent — the same trade already accepted for `AnnulusHeadroom = 0`. The
curve is kept behind the flag, not deleted.

### Also: the picker showed `.csch` twice

`SuggestedFileName = "harmonica-testbench.csch"` **plus** a `FileTypeChoices` entry whose pattern is
`*.csch` — the picker appends the type's extension itself. The suggested name carries no extension now.
(`ExportGamAsync` has the same shape of `SuggestedFileName` but declares no `FileTypeChoices`, so it was
left alone.)

## R9D — S1 "Match to Zin*", and the PA-class preset terminations (brief-harmonicarf-r9d, 2026-08-15)

**§2 — `Match to Zin*` reuses the frame-carried-outcome plumbing verbatim, and costs TWO frames, not
one.** `HarmonicaSolver.Options.ConjugateMatchBackoffDb` asks a frame to also read Zin off an
already-solved rung of the tier-A drive-up (`HarmonicaSolver.IndexOfBackoffStep`, a pure function
pinned on a synthetic ladder — including the "target below the ladder's first rung lands on the first
rung" case); the answer rides home as `HarmonicaFrame.ConjugateMatch`
(`ConjugateMatchOutcome`), exactly the shape `HarmonicaFrame.Inverse` already uses and for the same
stated reason ("computed on a WORKER, UI-visible state"). `HarmonicaViewModel.RequestConjugateMatch`
submits a `SkipContours` measurement-only frame first; its `PublishFrame` → `ApplyConjugateMatch` then
writes S1 (via `SetMarkerImpedance`, never a second mechanism) and requests the REAL frame that
regenerates the iso-lines. A `Found: false` outcome writes nothing (R-h6-9) and only sets the message.

**§3 — the preset walk is a straight read of `CircuitModel.IntrinsicDragAllowed`'s existing four-way
predicate, not a hand-rolled re-diagnosis.** `HarmonicaViewModel.ApplyPaClassPreset` builds a
transform-only model copy (nonlinear Cgs/Cdg/Cds replaced by the SAME linearized value
`Inputs`'s own strip row already shows, falling back to `Coefficients[0]` — "(at V=0)" — when nothing
has been solved yet, exactly the strip's own fallback) and then asks `IntrinsicDragAllowed` about
THAT copy: true means the ABCD transform runs per band (`IntrinsicAbcd.ExtrinsicFor`, refusing only a
per-band pole with the band left unchanged); false — for whichever of the OTHER three reasons the
predicate names (non-SDD DUT, a non-absent Cdg, or a package that couples input/output) — means every
band is written straight at the extrinsic plane, with a message naming why. One predicate call handles
every row of §3.4's table without the view-model code needing to know which refusal it hit. Only
Load-side markers that ALREADY EXIST are written (`Markers.Where(Side == Load)`) — a preset never
creates one, so `K=5` with markers only up to L3 leaves L4/L5 reporting `TerminationSet.
UnmarkedBandOhms`, exactly the owner's own example, now a gate. One `RequestScheduledFrame` after
every band is written, never one per band.

**The menu item wiring caught a stale source-scan pin, not a design bug.** Adding the optional
`KeyGesture? gesture` parameter to `HarmonicaAppMenuInjector.Item` broke
`HarmonicaAppMenuInjectorTests.Item_AlwaysConstructsAFreshInstance...`, which pins the method's exact
source text — expected, since the whole point of that test is to catch an accidental change to how a
`NativeMenuItem` is built; its expected strings were updated alongside the signature, not relaxed.

Gate: `tests/Ui.Tests/Harmonica/HarmonicaConjugateMatchTests.cs` (8 tests — the found/not-found/no-marker
cases via the same `PublishFrame` seam `ApplyInverseOutcome`'s own tests use, the
"only-when-set" check, and `IndexOfBackoffStep` pinned directly), `HarmonicaPaClassPresetTests.cs` (6
tests — the owner's own K=5/L1-L3 example, no-marker-created, source-untouched, the Cdg best-effort
path, the nonlinear-Cgs linearized-copy path with a real Rd/Ld to prove the transform actually ran, and
the one-frame-per-application count), `HarmonicaR9dPresetTerminationsSourceScanTests.cs` (8 tests — all
three menu surfaces' headers/parameters/gestures, plus the command's own string→enum mapping exercised
end to end). All existing `Ui.Tests` (7,048) and `Harmonica.Tests` (241) still green.

## R9C — SolveAtOptimum never reports a failed search, and the launch frame stops lying about grid size (brief-harmonicarf-r9c, 2026-08-15)

Companion entry to `src/Harmonica/RESOLVED.md`'s own R9C section (the ladder fix and the neighbour-seed
distance-guard finding); this one covers the two things that changed in `src/Ui`. §0's investigation
and its two measurement tables are recorded there, not duplicated here.

**§2 — `SolveAtOptimum` used to fall back to a failed search's LAST SURVIVING PROBE and hand it to
`AddMxColumn` as though it were the compression point.** On the shipped default at ZL1 = 132.3 Ω that
probe was Pin 11 dBm at 15.72 dB gain, published as "MXE Pout 26.72 dBm" while the strip's own P-3dB
read 39.28 dBm at the identical termination — the owner's exact bug report. Fixed two ways together:

1. **The drive-up is now `PinSearch.Sweep` at the document's own ladder settings**
   (`PinStartDbm`/`PinMaxDbm`/`PinStepDbm`) — literally the same call tier-A's own drive-up makes — not
   `PinSearch.Run`. MXP/MXE and the strip's operating-point column then agree by construction (one
   function, one definition of "P-x dB", one running-`gMax` rule), not by coincidence. Cost: ~38 solves
   each, two per full-quality frame (was ~11 with `Run`), measured on
   `HarmonicaOptimumSolveTests.MeasuredCost_TheOptimumSolvesCostRoughlyTwoDriveUps`.
2. **A search that did not reach the compression target now REFUSES rather than reports.**
   `SmithPanelData.SmithOptimum` gained `string? UnsolvedReason` (populated from the failed search's
   own `PinStopReason` — `PinMax` and `NonConvergence` read as different sentences, per R3B §3.3's own
   rule that the two stay distinguishable) and `CompressionReadout? SolvedCompression` (the ladder's
   own interpolated/one-real-solve reading AT the target — read for Pout/Eff/PAE/Gain/Pdc, falling back
   to `Solved`'s own nearest-rung numbers only for a hypothetical future `Run()`-based caller, the
   identical `sc?.X ?? at.X` shape the operating-point column already uses). `AddMxColumn`'s "no
   optimum" tooltip now reads `optimum.UnsolvedReason` when present, falling back to the original two
   sentences (every grid point a hole / mid-drag) otherwise — R7C §1.4's row-SHAPE rule is untouched:
   the same ten rows either way, gated by `HarmonicaOptimumSolveTests.RowCount_IsIdenticalBetween-
   ASolvedAndAFailedSearch`.

**`SolveAtOptimum` and `AddMxColumn` are now `internal` (not `private`), for the gate tests' own
sake.** Reproducing "the interpolated argmax lands somewhere that genuinely fails to re-solve" through
the natural pipeline (drive `InterpolatedArgmax` toward a real failure by tuning `PinMaxDbm`) turned out
to be a narrow, fixture-specific band rather than a reliable scenario — scanned directly on the shipped
default: `PinMaxDbm` from 14 to 20 dBm moves the grid from "36/37 holes, `Optimum` itself null" straight
to "33/37 holes, cleanly solved", with no dBm step in between landing on "an interpolated optimum exists
but its own fresh re-solve fails" (the InterpolatedArgmax seed and a fresh `Sweep` at that exact Γ
apparently succeed or fail together almost everywhere on this device). Rather than chase a fragile
fixture, the two methods are exposed via `InternalsVisibleTo("CircuitRF.Ui.Tests")` (already wired for
several other Ui view-model test seams) and called directly with a hand-picked failing termination
(`PinMaxDbm` set far below `PinStartDbm`, mirroring `ContourGridTests`' own
`D4_ANonCompressingPointStopsAtPinMaxAndSaysSo` fixture) and a hand-crafted `SmithOptimum`.

**§4 — the launch frame is solved at full quality, like every other frame.** Two changes, both
needed:

1. `HarmonicaView.EnsureFirstSolve` now calls `RequestScheduledFrame(dragging: false)` instead of a
   bare `RequestFrame()`. The bare call took `Options`' own (coarse) defaults, so a document's FIRST
   frame swept 25 points while every later one swept 37 — measured (§0, `src/Harmonica/RESOLVED.md`) to
   move the DE optimum from Z = 122.579 − j0.805 to Z = 132.319 − j1.786 and carry 4 holes instead of 1,
   which is what the owner saw as "the contours change when I move L1". Cost: ~65 extra solves on the
   document's own opening frame (measured 451 ms whole, in Debug) — paid once, deliberately.
2. **`HarmonicaSolver.Options`' `Rings`/`Spokes` defaults changed from `FrameScheduler.CoarseRings`/
   `CoarseSpokes` to `FullRings`/`FullSpokes`.** A bare `new Options()` is used by tests and reachable
   by any future caller; leaving it silently coarse would re-arm the same trap under a different name.
   Nothing reads the OLD doc comment's "fast rather than correct-and-slow" framing any more — the
   record's own comment now states the opposite and why.

**Benchmarks re-measured and their recorded numbers updated, per §5's own instruction that a
tripled grid solve does not free anyone from re-checking the ladder threshold or the recorded cost
comments:**

- **`FrameScheduler` was checked, not just reasoned about** — `HarmonicaFrameTierCostTests.
  Tier9_FrameTimeAtEachDegradationTier` still passes with only RELATIVE assertions (no rung threshold
  hardcodes an absolute number that could go stale), so no threshold needed moving. Re-measured on the
  shipped default: Full 37 pts / 892 solves / 468.9 ms total; CoarseRaster 37 pts / 892 solves /
  309.2 ms; CoarseGrid 25 pts / 639 solves / 227.3 ms; FrozenContours 0 pts / 40 solves / 11.7 ms.
- **`HarmonicaGridDragCostTests`'s recorded numbers changed, and its OWN doc comment now says why:**
  the per-point search became `PinSearch.Sweep`'s ladder (every 2 dB rung from PinStart to PinMax,
  rather than a ~5-solve secant), so both halves' SOLVE COUNTS rose — full rebuild 272 → **1319** HB
  solves, one dragged point 3 → **23** HB solves. R-h7-12's own reuse mechanism (keyed on Γ,
  search-independent) is UNCHANGED — still exactly 60 of 61 points reused, confirming the reuse itself
  was never the thing that moved. Wall-clock stayed well inside budget despite the solve-count rise:
  full rebuild 547.8 → **476.2** ms (each ladder rung is a cheap, well-warm-started solve), one dragged
  point 3.3 → **7.3** ms — still ~65× faster than a full rebuild, which is the number that actually
  gates whether a drag holds frame rate.

## R9B — the Appearance tab becomes circuitRF's Color Theme layout (brief-harmonicarf-r9b, 2026-08-15)

Pure layout/gesture parity — `HarmonicaColorEditor` (the model) did not move, and
`HarmonicaColorEditorTests` needed no edits. `HarmonicaAppearanceSettingsView.axaml`/`.axaml.cs` were
rewritten to transcribe `SettingsView.axaml`'s "Color Theme" tab shape: a role list with a 14 px colour
swatch per row (bound to a reused, namespace-level `RoleRowModel` — no second row-model type), RGBA
sliders + linked integer boxes, a hex field, and double-click-a-swatch (`OnRoleDoubleTapped`) opening
`ColorPickerDialog` in place of the former "Pick…" button, which is gone.

**The one structural difference from `SettingsView` is deliberate and stated at the top of the new
`.axaml`:** no theme-name combo, no `Save Theme…`, no `ForkToCustomIfNeeded`, no working-copy
dictionaries — harmonicaRF runs standalone with no workspace open, so a theme name has nothing to
resolve against (`HarmonicaColorEditor`'s own header, R-h7-15). Every edit still writes straight
through `HarmonicaColorEditor.Set` immediately (R-h7-16, live preview stays free — no re-solve,
re-fit, or re-factorization on this path). `Import .ccolor…` / `Export .ccolor…` / `Reset All Colours`
keep their place as harmonicaRF's answer to the theme combo.

**One addition beyond the brief's letter, needed once swatches exist:** `RefreshAllSwatches()` (the
`OnVariantChanged` counterpart the brief specifies) is also called from `OnRevertClick`,
`OnResetAllClick`, and `OnImportClick` — each can change a role's resolved colour without the user
touching that row directly, and without the call the list would show a stale swatch until the next
variant flip. `SettingsView` has no equivalent case (it has no per-role revert), so there was nothing
to transcribe here.

**§5 checked, not assumed:** the standalone harmonicaRF entry point (`HarmonicaApp.axaml`) already
carries `CircuitRfStyles.axaml`'s `Application.Styles` include (its own header calls this a
"superset by construction"), which pulls in `Avalonia.Controls.ColorPicker`'s Fluent theme
(`CircuitRfStyles.axaml:31`) — so `ColorPickerDialog`'s `ColorView` renders correctly standalone.
Nothing needed to change there; this brief just raises the stakes on it staying true, since the
picker is now the only way to reach a colour wheel at all.

**Gate:** `HarmonicaAppearanceSettingsView` is a `UserControl` and cannot be constructed headlessly
(same limitation as `HarmonicaSetTerminationDialogTests`), so the check is a source scan —
`HarmonicaR9bAppearanceParityTests` — asserting the double-tap gesture, all four RGBA sliders/boxes,
the hex box, the swatch-bound `Rectangle` inside the role list's `DataTemplate`, `PickButton`'s and the
theme-combo/Save-Theme controls' absence, and that `SettingsView.axaml` (the file this one was copied
from) still carries the same gesture and binding. `dotnet build`, `dotnet test tests/Ui.Tests`, and
`dotnet test tests/Firewall.Tests` all green. No screenshot verification (per brief). No `CLAUDE.md`
edit.

## R9A — the readout strip, the menus, and four defaults (brief-harmonicarf-r9a, 2026-08-15)

Eleven small, independent owner items. Nothing here moves a solved number except §7/§8, which are
explicitly defaults.

**§1 — "Add Source Marker" not drawing was a stale SNAPSHOT, not a missing redraw.** The panels draw
`SmithPanelData.Markers`, a copy taken once inside `RequestFrame` and carried onto the frame — not
`HarmonicaViewModel.Markers`, which `HarmonicaHitTest` reads live. `Refresh()`/`InvalidateVisual()`
after `AddMarkerBand` redrew the SAME stale snapshot, so the new marker was immediately hit-testable
and completely invisible. Fixed with `SyncMarkerSnapshotIntoFrame` (a pure re-projection via `Frame
with { Markers = …, SmithPower = Frame.SmithPower with { Markers = … }, … }`, no re-solve) called from
both `AddMarkerBand` and `RemoveMarkerBand`, plus `AddMarkerBandAndShow` (now the menu's own call site)
requesting a real frame afterward so the strip gains its row and the intrinsic glyph appears.

**§2 — `rgs` moved into the Capacitance chunk by inserting one key into `SettingsColumnKeys`,
between the spacer and `KeyCgs`.** Everything else (the label's `(Ω):` suffix, the missing `*`,
double-click-to-edit) fell out for free from the existing per-key dispatch — re-implementing any of
it would have been two answers to the same question. `EffectiveSettingsColumnKeys`'s existing
`ContainsKey(KeyCgs)` gate already covers `rgs` too (both are SDD-only, emitted by the same branch),
so it needed no second condition.

**§3/§6/§9 — pure text/structure removals**, gated by source-scan (`HarmonicaR9aSourceScanTests`):
the two rule `Border`s are gone from `ReadoutStripView.axaml` (kept `Spacing="3"`, only the lines
went); "Add Point(s)" → "Add Grid Points" on both menu rows; "DE" → "Drain Efficiency" on all three
menu surfaces (`HarmonicaView.axaml.cs`, `HarmonicaMenuView.axaml` ×2, `HarmonicaAppMenuInjector.cs`)
— `CommandParameter` stays the string `"DE"` everywhere, only the display text changed.

**§4 — `FormatComplex` gained optional `partDecimals`/`magDecimals` parameters (default the existing
constants) rather than a second formatter body.** `FormatZCompact` (1 decimal, `MxHeaderZDecimals`) is
the MXP/MXE header's own impedance now — an argmax off a fitted RBF surface does not carry the three
digits every other complex row (`FormatZ`) claims. `AddMxColumn`'s own `Zin` row is untouched.

**§5 — the power-sweep plot's dashed operating-point cursor is gone, by owner ruling, not superseded
by anything.** `PowerSweepPanelData.CursorIndex` still drives which step the glyphs/loadline/readouts
read; it simply has no mark on the curve any more. Pinned by a DIFFERENTIAL render test (same panel
at `CursorIndex = -1` vs. a valid index must be pixel-identical) — a single-column pixel probe cannot
tell a cursor line from a grid line, the same trap H4–H5 recorded for iso-lines vs. Smith chrome.

**§7 — the default L1 marker is `80+j0 Ω`, not `80+j10 Ω`.** 80 Ω is both the default DUT's own
R_opt and the default `HarmonicaSettings.Z0`, so the shipped document now opens with L1 at Γ = 0, the
centre of its own Smith chart. Default-model path only — `RebuildMarkersFromTerminations` (the load
path) is untouched, and every test fixture elsewhere in the suite that explicitly sets `80+j10`
(there are many, all independent of the constructor default) is unaffected.

**§10 — "Locked" now shares `Toggle`'s own checkbox glyph pair** (`CheckboxOutline`/
`CheckboxBlankOutline`, the same pair "Show Grid Points" uses) instead of a `Lock`/`LockOpenVariant`
pair, by routing both `AddAutoscaleLockedItems` rows through the shared `Toggle(header, on, onClick)`
helper (`Toggle("Autoscale", autoscaleOn, …)` / `Toggle("Locked", !autoscaleOn, …)`) rather than
hand-building two `MenuItem`s. `Toggle` never sets `ToggleType` (R7A §2.3's own finding about the
Fluent template's icon/check-glyph slot collision), so that invariant carries over unchanged.

**§11 — nothing is posted to the message line while a gesture is live**, gated on
`HarmonicaCanvas.Gesture.IsLive` (covers a marker drag, an intrinsic-glyph drag, a grid-point drag,
and an Edit Display grab — every case the owner can be inside). Extracted as a pure
`HarmonicaView.MessageLineText(gestureLive, statusMessage, idleSummary)` so `Ui.Tests` can pin all
combinations without a live control. The idle solve-cost summary used to update on every mid-drag
frame — a changing line under a moving hand, which is exactly what R1C's §2 said this line must
never be. A solve error raised mid-drag still surfaces, one `Refresh()` after release.

**§8's blast radius is in `src/Harmonica/RESOLVED.md`** (touches `CircuitModel`/`CharmIo`, not `src/Ui`).

## R8C — the readouts carry live impedance, γ suppresses its own noise, and the intrinsic drag stops solving (brief-harmonicarf-r8c, 2026-08-15)

**§1 — `HarmonicaTitles.MxHeaderRow` gained a `zText` parameter rather than an `HarmonicaReadoutFormatting`
reference, because the two files sit on opposite sides of the UI wall** (`src/Harmonica` is
framework-free by rule; `HarmonicaReadoutFormatting` is `src/Ui`). `AddMxColumn` computes the real
optimum's Z (`HarmonicaDataSet.ImpedanceOf(optimum.Gamma, z0)`, never the marker's, per the owner's own
explicit instruction) ONLY inside the solved branch — the no-optimum branch still calls `MxHeaderRow`
with `zText: null`, keeping R7C §1.4's "row shape must not change between branches" rule intact
(computing it unconditionally from `optimum?.Gamma` looked simpler at first but leaked a Z into the
"no optimum" header text, which the brief explicitly forbids).

**Header rows became `SelectableTextBlock`, and the one hazard the brief flagged (R-h9r2-15: it eats a
double-tap before `DoubleTapped` fires) genuinely does not apply — headers have no `DoubleTapped`
handler at all**, confirmed by reading `BuildColumnRowShell`'s own early return for `isHeader`.

**§2 — the γ phase floor (`GammaPhaseNoiseFloor = 1e-3`) collided with an EXISTING test's own
fixture**, `HarmonicaGammaFactorTests.GammaRow_IsComputedThreeTimes_FromThreeDifferentDataSets`: the
shipped default document's OP/MXP/MXE operating points all carry |γ| comfortably under the new floor,
so their FORMATTED strings collapsed to the identical `"0.000∠—"` — correct display behaviour (the
whole point of §2), not evidence the three computations merged into one. Fixed by comparing the raw
`Complex` γ from each chunk's own `V_intr` cube (via the SAME private `ReadComplex`/`GammaFactor`
reflection the file's other tests already use) instead of the rendered text — a strictly BETTER test
than the string comparison it replaced, decoupled from any future formatting change.

**§4 — `HarmonicaPanelRenderer.MarkerRadius` is now the one place either the round marker or the
triangular intrinsic glyph computes its own radius from**, hoisted out of `DrawMarkers`. The 0.9
scale factor collides in NAME ONLY with the triangle's own unrelated 0.9/0.75 shape-proportion
literals a few lines below — commented at both sites so a reader does not fold them together.

**§5 is where the real design work landed — see `src/Harmonica/RESOLVED.md`'s own R8C entry for the
`IntrinsicAbcd` chain-order finding and the round-trip residual.** What lands here: `BeginIntrinsicDrag`/
`EndIntrinsicDrag` became no-ops (clearing `InverseMessage`); `DragIntrinsicGlyph` calls
`IntrinsicAbcd.ExtrinsicFor` synchronously on the UI thread and writes the result through the SAME
`SetMarkerImpedance` an extrinsic drag uses, then routes the forward frame through
`RequestFrameOnMarkerRelease` — the SAME pacing/dedup machinery an extrinsic marker drag already uses
(its own doc comment already named `DragIntrinsicGlyph` as a caller, apparently written in
anticipation of this exact change). `_inverse`/`_inverseMarker`/`_inverseBands`/`_inverseTargets` and
`RequestInverseFrame` are now genuinely unreferenced from anywhere in this class (not merely "from the
drag path") — `_inverse`/`_inverseMarker` needed explicit `= null` initializers or the compiler's
CS0649 ("field is never assigned") turns into a build ERROR in this project's config, not a warning.

**`HarmonicaHitTest.Resolve` gained `intrinsicDragAllowed` (default `true`, so every existing direct
test keeps today's behaviour); Pass 2 does not run at all when it is false** — a grab that starts and
then refuses to move is worse than no grab, per the brief's own instruction. A NEW hit-test helper,
`IsOverIntrinsicGlyph`, runs the identical Pass-2 distance check independent of the allowed flag, used
ONLY so `HarmonicaPointer.PointerDown` can still tell the user WHY a click that visibly landed on the
glyph did nothing (`InverseMessage`), without granting the grab itself.

**`ShowReachableRegion` now defaults `false`.** The property, sampler and `DrawReachableRegion` all
stay in place (nothing here is deleted) — only the default flipped, since the shading answered "what
can the retired inverse solve reach," a question the closed form's exact inversion makes uninteresting
(everything is reachable except the pole).

Gate: see `src/Harmonica/RESOLVED.md`'s own R8C entry for the full test list across both projects.

## Terminations, the marker Γ, and the context menus — a re-entrancy flag cannot express "who owns this box"; only identity can (brief-harmonicarf-r8b, 2026-08-15)

**§1 — the "can't type 50" bug (reported three times) was never in `TryParse`.** The Set Termination
dialog's three combined-text boxes stayed in sync by having each `TextChanged` handler write the other
two's `Text`, guarded by a single `bool _loading` set for the duration of that write. `_loading` is a
*window in time*, not a statement about identity — an echo landing after the window closes (a deferred
raise, a re-entrant write) is processed as if the user had typed it, which is what rewrote the Z box
under the user's own caret. Fixed by replacing the flag with **ownership**: `TerminationEditModel`
(new, no Avalonia reference) tracks `Editing` (which field, or none) and every `Edit(field, text)` call
for a field that isn't the current `Editing` one is simply ignored, regardless of when or how it was
raised. The dialog is now a thin shell — `GotFocus` sets `Editing`, `TextChanged` forwards to
`Edit`, `LostFocus` clears `Editing` *before* reformatting so that reformat's own echo is disowned too.
`TerminationEditModelTests` drives the actual echo call the old bug depended on
(`AnEchoFromAnotherField_WhileEditingZ_DoesNotMoveTheModel`) — the case three prior "fixes," each
verified only against a hand-built simulation of the handler shape, could never observe. **Not
interactively verified against a live `TextBox`** (no headless-Avalonia harness for this dialog in this
repo, and the brief asked for no screenshot verification) — the model-level fix is pinned directly; if
the live control still misbehaves after this, that would be a SECOND, unlocated defect, not a
regression of this one.

**§2 — the marker glyph and its own VSWR circle were drawn on two different radial mappings, and only
one of them was ever meant for a marker.** `IntrinsicGlyphScale`'s compressed radial map exists for the
INTRINSIC glyph (`|Γ_intr|` is unbounded, R-h45-4) — `MarkerToCanvas`/`CanvasToMarker` composed that
same map into the EXTRINSIC termination marker's own canvas transform too, invisibly inside the unit
disc and wrong the moment `|Γ| > 1`. Both wrappers are deleted outright (not merely unused) so the
composition cannot be silently reintroduced by "reusing the marker helper" — every extrinsic call site
(hit-test passes, the drag gesture, `DrawMarkers`) now goes straight through the plain
`GammaToCanvas`/`CanvasToGamma` affine map, exactly like a grid point or the VSWR locus already did.
Consequence, intended: an active marker can now leave the panel entirely (`DrawMarkers` carries no
`ClipRect`), and `IntrinsicGlyphScale.MaxTrueMagnitude`'s soft saturation at |Γ|=10 no longer applies to
a marker drag — the practical bound is now whatever the panel's own pixel extent reaches (~1.3 at the
chart margins), a harder and more honest one. **Measured, not assumed:** `GammaToCanvas`/`CanvasToGamma`
round-trips to 1e-9 only near the origin; `SKPoint` is `float` (32-bit), so a value near the rim
(|Γ|≈0.9–0.999) already loses precision to ~1e-7 absolute, and a value well outside the rim loses far
more to the underlying chart viewport's own finite window — neither is a regression, both are properties
of the transform this brief exposed rather than introduced.

**§3 — an unmarked band is a near-short (1e-6 Ω), so "S1/S2 off by default" and "S1 defaults to 50 Ω"
are the SAME change, not two.** Deleting the S1 marker without also writing its termination would have
silently turned the DUT's source into a near-short the instant the marker vanished — exactly what
`AddMarkerBand`'s own comment says must never happen. Fixed by writing `Terminations.Set(Source, 1, 50Ω)`
in the constructor even though no marker exists for it — `TerminationSet`'s own ctor already does this
for both S1/L1, so the explicit call is a second, defensive statement of the same fact rather than a
behavior change on its own. A fresh document now ships **L1/L2/L3 only** (three markers, not five) —
S1/S2 are added from the Smith panel's new "Add Source Marker" item. **Band 1 is removable on both
sides now** (`RemoveMarkerBand`, the Markers-menu `HarmonicaBandMenuItem.CanRemove`) — it used to refuse
outright; removing it now leaves the termination in place, same as bands ≥ 2 leave their absence as the
unmarked value. The Source readout column keeps its header row even with zero markers on it (R7C §1.4's
row-shape-stability rule), with a tooltip naming the fix rather than a silent gap.

**§7.3 — "I can't drag the VSWR circle outside the chart" was two separate findings, and only one of
them is fixable.** (1) A THEOREM: a passive marker's (`|Γ|<1`) whole VSWR family stays strictly inside
`|Γ|=1` for every finite VSWR — the underlying Möbius map is an automorphism of the passive half-plane,
so a passive marker's circle *cannot* be dragged outside the chart, ever, by construction. (2) A
saturation that hid (1) badly: `VswrThrough`'s bisection silently returned the clamped `MaxVswr` (1e6)
the instant a drag point fell outside its search bracket, which reads as "the number stopped moving."
Fixed by reporting the clamp instead of hiding it (`VswrThroughEx` → `(Vswr, Saturated)`,
`HarmonicaReadoutFormatting.FormatVswr(vswr, saturated)` → `"VSWR: > 10⁶"`), in both the live drag
readout and the marker menu's own row. §2's fix is the other half the owner actually wanted: an ACTIVE
marker's whole VSWR family sits entirely *outside* `|Γ|=1`, and with the marker itself no longer
compressed onto the intrinsic scale, that family now draws (and drags) concentric with its marker,
genuinely outside the Smith chart, unclipped.

**§5 — the Fluent `MenuItem` template trap (R7A §2.3 first found it) gets closed for good, not
patched twice.** `ToggleType.CheckBox` and `Icon` share the SAME leading slot in this Avalonia build —
an item with both shows a missing icon, a missing checkmark, or a doubled indent depending on theme.
R7A §2.3 fixed exactly two items this way (Autoscale/Locked) and left every other toggle on
`ToggleType`; this brief converts the rest through one shared builder (`Toggle(header, on, onClick,
glyph: MenuGlyph.Check|Radio)`) and pins it with a source scan (`HarmonicaView.axaml.cs` contains zero
occurrences of `MenuItemToggleType`). Power Sweep/Time Domain — genuinely a two-state radio, not two
independent checkboxes — is now one `Mode ▸` submenu row with the current mode in its own header text;
its own row carries no `Click` (a `MenuItem` with children never raises one — R7A §2.4's trap, again).
`HarmonicaAppMenuInjector.cs`'s one `NativeMenuItem.ToggleType` use was checked and left alone: it never
sets `Icon` alongside it, so the slot-collision this rule exists for cannot occur there.

**§6 — the Ω icon on the marker menu's Γ/Z rows was never anything but a placeholder** to satisfy
`Item`'s old non-nullable `MaterialIconKind icon` parameter; made explicitly nullable (`icon: null`
leaves `Icon` unset) rather than swapped for a different glyph that would mean something equally
nothing.

## Iso-line labels: a 30.0-vs-2π unit mismatch meant ZERO ever drew on a Smith/Polar plot; harmonicaRF's own toggle was wired end to end except the draw call (brief-harmonicarf-r8a §4, 2026-08-15)

**`ContourRenderer.DrawIsoLines`' label placer walks in WORLD units, and Data Display's own Γ-plane
default was 5× the longest polyline that can exist there.** `ContourData.LabelSpacing` seeded
Smith/Polar contours at 30.0 — the SAME number used for a rectangular dB-vs-frequency axis, where it's
sensible. But the Γ world is the unit disc: the longest closed polyline is the rim, arc length 2π ≈
6.28, and the walk's first target is `startFrac × spacing ≥ 0.15 × 30 = 4.5`. For almost every real
contour (arc 1–3) that target is never reached, so the `while (targetArcW <= segEnd)` loop's body never
executes and **not one label was ever drawn, for every contour on every Smith/Polar plot, at any zoom,
regardless of the `DrawLabels` toggle** — invisible on a Rect plot (hundreds of world units, so 30.0
works fine there), which is exactly why nobody had noticed. Fixed two ways, both required: the seed
default is now plane-dependent (0.35 for Γ, matching the disc's own scale), AND the placer itself
(`ContourRenderer.ComputeLabelAnchors`) now falls back to placing exactly ONE label when the configured
spacing exceeds the polyline's own total arc length, rather than silently placing none — a user asking
for a wide spacing wants FEWER labels, never zero, and zero is indistinguishable from "broken".

**harmonicaRF's `ShowIsoLineLabels` toggle was wired end to end — the menu item, the `.charm` round
trip, the render-cache key — except for the one thing it names.** `HarmonicaPanelRenderer.DrawContours`
stroked polylines and returned; the toggle's entire observable effect was busting the Layer-A raster
cache key. This was not a rendering bug to hunt for — it was a feature shipped with its last step
missing. Fixed by extracting Data Display's own placement arithmetic into
`ContourRenderer.DrawIsoLineLabel`/`ComputeLabelAnchors` (Skia draw / pure arc-walk, split so the
arithmetic is unit-testable without a canvas) and calling it from harmonicaRF too — one placer, two
renderers, rather than a second hand-rolled one. Each label gets the SAME ramped alpha byte its own
polyline got (`IsoLineAlphaRamp.AlphaByte`), so a faded low-rank contour never carries a fully-opaque
label — with the fade floor now 0.01 (below), that would have been the loudest possible artifact.

**Measured on the shipped default document** (37-point grid, `HarmonicaViewModel.DefaultModel()`,
load side band 1): 55–68 label anchors across 11–15 polylines per metric at the new 0.35 spacing — 4–5
labels per contour, matching the "~5–6 around a full rim-scale ring" estimate the new default was
chosen from.

**Tab split trap, named so it doesn't reappear:** moving the fade sliders and the label checkbox from
the Appearance tab to the Advanced tab moved their MARKUP, but both write through the SAME
`HarmonicaColorEditor` instance the Appearance tab was already handed — construct a second editor for
the Advanced tab (the obvious way to wire a newly-independent view) and the two tabs silently diverge,
with whichever one loads last winning. `HarmonicaSettingsDialog` now hands `vm.ColorEditor` explicitly
to both `Attach` calls; there is exactly one editor per document, never two.

## `Grid.IsSharedSizeScope` does not align columns hosted in a `StackPanel` — five failed attempts had the wrong culprit; units moved into the labels, γ landed (brief-harmonicarf-r7c, 2026-08-14)

**The readout strip's label/value misalignment was never a width bug. `Grid.IsSharedSizeScope` set on
a `StackPanel` host is a no-op in this Avalonia build (12.0.3) — confirmed by an isolated repro, not
inferred.** A throwaway headless-Avalonia harness (`AppBuilder.Configure<T>().UseHeadless(...).
UseSkia()`, real Skia text shaping, no display needed) built the minimal case directly: a `StackPanel`
with `Grid.SetIsSharedSizeScope(host, true)`, two child `Grid`s each with a `SharedSizeGroup`'d
`Auto,Auto` column set, one row labelled `"Short:"` and one `"A Much Longer Label:"`. Their VALUE
cells measured at **X = 39 and X = 138** — never aligned, because each row's label column sized to
its OWN text, exactly as if `IsSharedSizeScope` had never been set. R-hui-4 (2026-08-14, the brief
before this one) built the whole three-column layout on this premise and it never worked; R-hui-5
through R-hui-7 kept re-diagnosing the SYMPTOM (a jittering value column) as a width-reservation bug
and re-fixing the value column's own width, which never touched the actual defect — the LABEL
column, silently un-shared the entire time. Five attempts, one root cause, never named until this
brief actually built the isolated case rather than staring at the full control tree.

**The fix (§1.5's own explicit fallback): every chunk's label column is pinned to a MEASURED width,
the same discipline `ReservedValueWidth` already used for the value column** — `ChunkLabelWidth`
(readout columns), `UpdateSettingsColumn`'s own `labelWidth` (the Settings column), and a second pass
over `Items.Children` after building (`General`, which rebuilds every call and so has no persistent
row to probe a typeface from before all its rows exist). `SharedSizeGroup`/`Grid.SetIsSharedSizeScope`
are deleted outright, not left in as inert scaffolding — "drop it entirely," per the brief.

**A second, genuine bug was found and fixed while building the label-width measurement, and it is
worth naming on its own: measuring against a control's OWN `.FontSize` property reads ONE FRAME
STALE.** `ChunkLabelWidth`/`UpdateSettingsColumn`'s first draft read `probe.FontSize` (the label
TextBlock's own current property) rather than the `fontSize` PARAMETER `SetItems` was just called
with — `UpdateColumnRow` is what writes the new value onto that property, and it had not run yet at
the point `ChunkLabelWidth` needed it. Harmless when font size never changes frame to frame (the
stale read equals the current one), but caught directly: the SAME headless harness re-solved twice at
the SAME font size and the whole `OperatingPointColumn` value column shifted 4–5 px between the two
calls anyway — the tell was that `ChunkLabelWidth`'s own measured width (69.35 px, printed both
before and after) was DIFFERENT on the very first call (74.69 px) than every call after, which only
happens if the number being measured AT depends on something other than the label text and the font
size actually requested. **Any future "measure once, pin the result" helper in this file must take
its font size as an explicit parameter, never read it back off a control this same call is in the
middle of updating.**

**Measured, replacing the guess:** the widest complex value's worst case (`"−0000.000−j0000.000"` /
`"0000.000∠−000.0°"`, the wider of the two) at the strip's own SemiBold weight, 13 px — a realistic
mid-range font size for this panel — is **120.71 px**. The OLD `22 * fontSize * 0.55` formula
(`RectComplexChars`'s old character budget) would have reserved **157.30 px** — 30% too generous,
which is a real cost: every OTHER row-kind's column in the same chunk pays for a complex row's own
padding once `ReservedValueWidth`'s max wins, even though nothing on screen actually needs that much
room. §1.3's own prediction (the constant is wrong by "a different amount for every glyph and every
non-integer font size") is not approximately right, it is measurably 30% off at one realistic size.

**§1.4's row-count churn was real, not hypothetical — the pre-fix test asserted it directly.** Before
this brief, `HarmonicaReadoutColumnsTests.MxpColumn_SaysNoOptimum_OnASkipContoursFrame` asserted
`Assert.Single(mxp)` on a `SkipContours` frame — i.e. the MXP chunk genuinely collapsed from nine rows
to one every time a degraded ladder rung or a `SkipContours` frame carried no fresh optimum, and
expanded back to nine the instant a full-quality frame supplied one. That test is what proved the bug
was live, not merely theoretically possible from reading `AddMxColumn`'s branch. Fixed by always
emitting the same nine (now ten, with γ) rows and rendering `"—"` when unavailable — pinned by the
SAME test, now asserting the opposite: row count invariant across both states.

**γ, the input nonlinearity factor (§2 of the brief), landed as a NON-complex row on purpose** — see
`HarmonicaSolver.GammaFactor`/`AddGammaRow` and `HarmonicaReadoutFormatting.FormatGammaFactor`. Marking
it `IsComplex: false` is structural, not cosmetic: a `true` row would both offer a real/imaginary menu
that means nothing for `γ = V₂·conj(V₁)²/|V₁|³` (no sensible real/imaginary split) and collide with
Zin's own saved format state, since `HarmonicaReadout.FormatKey` resolves any complex row in a given
column to the SAME key (`"MXP.Zin"` etc.) regardless of which row it is.

**Not verified in this brief, and it should be before the layout is called fully closed:** the
headless harness above proves column alignment and drag-stability ALGEBRAICALLY (measured X
positions, before/after a re-solve, across four font sizes) — it does not prove what a human eye
sees. `screencapture`/AppleScript UI automation was attempted from this session and blocked by macOS
Screen Recording permission not being granted to the sandboxed shell; no screenshot of the running app
was taken. The four gate screenshots §4 asks for (rest state, a live drag, MXP/MXE's nine dashes,
γ under Pdc) are still owed.

## Active-termination Γ bug was the compressed radial scale's clamp; fly menus are real context menus now (brief-harmonicarf-r7a, 2026-08-14)

**§1 — `IntrinsicGlyphScale.MaxTrueMagnitude = 10.0`.** The old inverse (`TrueRadius`) saturated at
`u = 1 - 1e-9`, a near-pole that put every pointer position at or beyond drawn radius `1 + margin`
at the SAME Γ ≈ −1.0000000282×10⁹ (measured, reproduced by a test that pins the exact figure) — a
value that does not survive its own Γ↔Z round trip (`GammaOf(ImpedanceOf(Γ))` was off by ~41) and so
disagreed with itself everywhere it was re-derived from Z. At `|Γ| = 10`: **Z = −40.91 Ω at Z0 = 50**,
**Z = −65.45 Ω at Z0 = 80** — past every physically interesting active termination and small enough
that the round trip is exact to double precision. The clamp is now derived algebraically from the
constant (`u_max = k(MaxTrueMagnitude−1)/(1+k(MaxTrueMagnitude−1))`), so the two can never drift.
`HarmonicaSetTerminationDialog`'s live Z preview had the SAME bug's sibling — it clamped `|Γ|` to
0.999 before previewing, which is wrong for this brief's whole subject (typing Γ = −3 showed the Z of
−0.999, not −25 Ω). Fixed by deleting the clamp entirely: the preview is now exactly
`HarmonicaDataSet.ImpedanceOf`, which already nudges only the genuine `|1−Γ| < 1e-12` singularity.
Extracted as `HarmonicaSetTerminationDialog.PreviewImpedance` (internal static) since the dialog is a
`Window` and cannot be constructed headlessly in `tests/Ui.Tests`.

**§2 — every fly menu routes through one `Item(header, icon, onClick)` helper**, plus a shared
`AddAutoscaleLockedItems` for the two panels that both carry an Autoscale/Locked pair. No
`MaterialIconKind` name from the brief's own suggested map needed substituting — every one of
`ContentCopy, Pencil, Cog, PlusCircleOutline, PlusCircleMultipleOutline, Delete, Magnet, MagnetOn,
ChartBellCurve, SineWave, Percent, ChartLine, Waveform, Omega, Lock, LockOpenVariant, ArrowExpandAll`
compiled — checked exhaustively against `Enum.GetNames(typeof(MaterialIconKind))` from a throwaway
console probe before writing any call site, not by trial and error. The one gap: no
`ArrowExpandAllOutline` (or any outline variant) exists for the inactive Autoscale state, so §2.3's
own documented fallback is what shipped — the same `ArrowExpandAll` glyph at reduced opacity (0.35)
rather than a different icon.

**§2.3's Fluent `MenuItem` Icon/checkmark trap — NOT VISUALLY VERIFIED.** The owner's chosen
resolution (Autoscale/Locked carry an icon and no `ToggleType` at all) was applied as specified, which
sidesteps the trap by construction rather than by observing it. Whether the trap is real for the
OTHER checkbox items the brief named to leave icon-free (Snap to Grid, Show Grid Points, Power
Sweep/Time Domain, Contour Plane/Harmonic/Efficiency Metric's children) was not checked — the
`/run`-and-screenshot half of this brief's own §4 gate was explicitly declined for this round (running
the app means driving a real mouse/window on the owner's own machine), so this is deferred to whoever
next has a live session open, not silently assumed passing.

**§2.4 — the actual bug, generalized.** `MenuItem` with children never raises `Click`: true of the
VSWR row (a checkbox carrying a lone "Set…" child) AND of all three format rows (Γ real/imag, Γ
mag/angle, Z real/imag — each just a lone "Set…" child with no Click of its own). All four flattened;
pinned structurally (`HarmonicaR7aMenuTests`) by asserting `ItemsSource` never appears in
`BuildMarkerMenu`/`BuildFormatRow`'s own (comment-stripped) source, rather than re-deriving the
specific defect shape by hand at every call site.

## Persisted axis limits + autoscale: one mechanism, three plots (brief-harmonicarf-r6e, 2026-08-14)

**§1 — the Drain Sweep bug was the Grid's own `*` row, not a positioning mistake.**
`HarmonicaDcivSweepsDialog.axaml`'s `RowDefinitions="Auto,Auto,*,Auto"` put the "Drain sweep (Vds)"
title in row 2 — the ONE row marked `*` — so it floated at the top of a row that stretched to fill
whatever space `Height="300"` left over, while its own fields (row 3) were pushed to the bottom of
the window. Fixed by making every row `Auto` and switching the Window to `SizeToContent="Height"`
(dropping the explicit `Height` entirely) rather than picking a new fixed number — the same fix this
brief's own §3.1 addition (a whole new "Axis limits" section) needed anyway, so the window grows to
fit rather than needing a second guess at a magic height.

**§2 — the three-state rule, expressed as one small pure function plus one write-back method.**
`HarmonicaPanelRenderer.StoredAxisWindow` (X/Y/Y2 min+max + `Autoscale`) is read by
`ApplyStoredWindow`, called at the END of `BuildLoadlinePlot`/`BuildPowerSweepPlot`/
`BuildTimeDomainPlot` — strictly AFTER `AutoScale`, `PinAxisPin` and the right-edge headroom, so an
explicit stored limit always wins over all three. `Autoscale == true` makes `ApplyStoredWindow` a
pure no-op, which is the trick that lets ONE call site serve both "read the stored window" (ordinary
render) and "tell me what AutoScale/PinAxisPin/headroom would compute right now" (the capture path,
below) — the caller decides which question it's asking by what it does with `Autoscale` and with the
result, not by a second code path.

**The write-back is a SEPARATE method (`HarmonicaViewModel.CaptureAxisWindows`), fired from
`OnFrameChanged` — never from inside the renderer.** The renderer is a pure function called from
several places (the live canvas, Copy Plot, export) that must never have a mutating side effect;
`CaptureAxisWindows` is the one place anything WRITES a stored limit, and it only fires per SOLVED
FRAME, never per repaint. It calls each `Build*Plot` with `Autoscale` forced to `true` (so
`ApplyStoredWindow` no-ops and the returned `Axes.Window`/`WindowSecondary` is always today's
natural fit), then writes that back into `HarmonicaSettings` under exactly two conditions: autoscale
is actually ON (every frame — this is what makes "turn it off" freeze exactly what's on screen), or
autoscale is OFF and nothing has ever been stored (ONCE, from the first frame that has real data —
checked against the panel's own arrays, not against the Window looking "big enough", because
`Axes`'s own default `Window` is `(-50,-50,150,150)`, not `(0,0,0,0)`, and would otherwise read as a
plausible captured value before any data exists). Neither condition holds once a limit is held with
autoscale off — the anti-breathing property itself, not a special case of it.

**Time Domain gets its OWN thirteen-field-shaped block, not a shared one with Power Sweep** — same
panel slot, different quantity (time/V/A vs power/dB/%), so `TimeDomainXMin`/… are separate
`HarmonicaSettings` fields and a separate `HarmonicaPowerSweepAxesDialog(vm, timeDomain: bool)`
construction, never a shared set gated by the current mode. Confirmed by test
(`ApplyTimeDomainAxisLimits_IsIndependentOfPowerSweepAxisLimits`) that setting one leaves the other
untouched.

**One dialog class serves two modes.** `HarmonicaPowerSweepAxesDialog` relabels its Y/Y2 rows at
construction time (`Gain`/`Efficiency|PAE` vs `Vds`/`Ids`) and calls whichever `Apply…AxisLimits`/
`Set…Autoscale` pair matches the mode it was opened in — cheaper than two near-identical dialog
classes, and the brief's own "say if you find a cheaper representation" invited exactly this.

Tests: `AxisLimitsPersistenceTests` (`Harmonica.Tests`, 3 — round-trip, all-absent-on-an-old-file,
no-bloat-when-untouched), `HarmonicaR6eAxisLimitsTests` + `HarmonicaR6eDialogsAndMenusTests`
(`Ui.Tests`, 19 — the §1 layout fix, the §2.4 precedence ordering pinned against values PinAxisPin/
headroom would NOT have produced, the anti-breathing property both as a direct render-level assertion
and end-to-end through a real `HarmonicaViewModel` solve, and the dialog/fly-menu wiring). Full gate:
`Ui.Tests` 6,756, `Harmonica.Tests` 175, `Firewall.Tests` 6 — all green. **Not verified interactively**
(no live Avalonia session in this environment) — the owner-check items in the brief's own §5.5 (typed
limits surviving a drag and a save/reopen, the checkbox and fly-menu item agreeing on screen) are
covered by the tests above at the API/mechanism level but not watched happen in the running app.

## The power-sweep right axis, drawn twice: the third fix stops covering and just doesn't draw the underlying one (brief-harmonicarf-r6d, 2026-08-14)

**Third time reporting the identical symptom — "the right axis renders in two colours" — after two
prior fixes that both, in different ways, tried to make the COVER match the COVERED exactly** (R-h9b-9
added the cover; R3C §5 fixed the cover's paint SHAPE so it matched `AxesRenderer.DrawBorder`'s stroke
field-for-field, see this file's own r3c entry above). Both were real fixes for the bug they diagnosed
and neither could be the last fix, because **the thing being covered was still there** — any future
change to either paint's shape (a stroke width formula, a cap style, an AA setting) reopens the exact
same symptom with a new mismatch. The owner's own framing this round: "Do not render the green line
underneath it" — not "match it better."

**The actual fix: stop drawing the underlying secondary-axis chrome at all**, rather than adding a
third generation of paint-matching. `HarmonicaPanelRenderer.DrawWithSuppressedSecondaryChrome` swaps
`plot.Axes` for a deep copy (`Axes`'s own copy constructor, `Axes.cs:161`) with `ShowSecondary = false`
for the ONE `PlotRenderer.Draw` call, then restores the original before the (renamed, colour-
parametrized) overlay draws the axis for the first time. Confirmed safe by reading rather than assumed,
per the brief's own instructions:

- **Trace rendering never reads `Axes.ShowSecondary`** — grepped the whole `PlotRenderer`/
  `TraceRenderer` stack; only `AxesRenderer`'s chrome (border, ticks, tick numbers, label) and a few
  interaction call sites in `PlotControl` (irrelevant here — harmonicaRF never uses that control) branch
  on it. So the efficiency/loadline trace itself renders identically whichever way the flag is set.
- **The viewport does not move.** `PlotRenderer.ComputeViewport` only re-derives a `ShowSecondary`-
  dependent viewport for a Rect plot with NO pinned `Axes.Viewport` — `BuildPowerSweepPlot`/
  `BuildTimeDomainPlot` both pin `PowerSweepShapedViewport()` explicitly (R-h9b-11), so that formula
  never runs for these panels regardless of which `Axes` instance is live when `Draw` is called.

The general lesson, sharpened from r3c's own "the cover must match the covered exactly" note: **a cover
that must match the covered exactly is the wrong design in the first place — check whether the
covered draw can simply be suppressed before reaching for a better-matched cover.** Here it could,
because the ONE thing keying the covered draw (`Axes.ShowSecondary`) was a plain bool with an existing
copy-and-flip path, and nothing downstream of the `Draw` call needed it to stay true. That will not
always be true (a shared renderer might branch on a dozen fields, or the caller might not own a cheap
copy of its own state) — when it is not, r3c's paint-matching route is still the right fallback, not a
mistake to avoid on principle.

A headless pixel test (`HarmonicaPanelTests.PowerSweepPanel_RightAxis_NoOrdinaryAxisColourSurvivesUnderneathTheOverlay`)
is the gate the previous two fixes could never have written, because the previous two never made it
true: it renders the panel and asserts NO pixel along the right axis, its tick-number band or its
rotated label carries the ordinary `Harmonica.AxisLine` colour — not "the fringe is small enough not to
notice," an assertion that is exact by construction now rather than approximately true by paint-shape
coincidence.

## The readout strip: 2×4 grid, intrinsic chunks, stable widths, per-chunk copy (brief-harmonicarf-r6c, 2026-08-14)

**§1 — the six-column horizontal `StackPanel` became an 8-cell `Grid` (2 rows × 4 columns), re-parenting
only.** Every `x:Name` from the old `Columns` row survives unchanged (`SettingsColumn`,
`OperatingPointColumn`, `SourceColumn`, `LoadColumn`, `MxpColumn`, `MxeColumn`, plus two new ones), so
`UpdateReadoutColumn`'s build-once/update-in-place machinery needed no changes at all — only the XAML
`Grid.Row`/`Grid.Column` placement and `ReadoutColumn`'s own doc comment moved. Row 1: Settings ·
OperatingPoint · MXP · MXE. Row 2: **Load · Source** · IntrinsicVDS · IntrinsicIDS — Load left of
Source, the reverse of R1C's own left-to-right order, per the owner's explicit (row, column)
specification. `HarmonicaR3cStripTests`' own column-order test now reads `Grid.Row`/`Grid.Column` off
the XAML rather than trusting declaration order (a `Grid`'s children may appear in any order in markup).

**§2 — two new chunks (`ReadoutColumn.IntrinsicVds`/`IntrinsicIds`) read `V_intr`/`I_intr` at
`ctx.IntrinsicPorts.DrainPort`, never recomputed.** `HarmonicaSolver.ReadComplex(ds, cubeName,
sideIndex, harmonic)` already generically indexes any `[axis0, harmonic]` complex cube — it needed no
change to serve a `[port, harmonic]` cube with `sideIndex = DrainPort` instead of a `[side, harmonic]`
one with `sideIndex = (int)TerminationSide`. **These two chunks default to magnitude ∠ angle, unlike
every other complex row's real/imaginary default** — `HarmonicaReadoutFormatting.DefaultReadoutFormat`
special-cases the `VDSi.`/`IDSi.` key namespace (one place, shared by `HarmonicaSolver`'s null-resolver
fallback and `HarmonicaViewModel.ReadoutFormatLookup`'s own unrecognized-key fallback, so the two
cannot disagree) — the row is still an ordinary `IsComplex` row with a working format flyout, the
owner's default preference is just the OTHER format from everywhere else. `SetItems`'s column-routing
switch is now **exhaustive over `ReadoutColumn`** (it used to fall through to `default: mxe.Add(item)`,
which would have silently swallowed both new columns into MXE).

**§4 — TWO independent sources of column-width churn, and the fix for one nearly broke the other.**
Trailing-zero trimming (`0.###` turns 10.01 into 10.1, one character shorter) is fixed by fixed decimal
places; the INTEGER side growing (an impedance running 0.5 Ω → 5000 Ω) additionally needs
`HarmonicaReadoutFormatting.FixedWidth(value, decimals, budget)` — pads to a stated per-quantity
character budget, or switches to a fixed-width exponent form past it, so a row's rendered length is a
function of WHAT KIND of row it is, never of its current value.

**The trap: `FixedWidth`'s padding must NEVER reach an editable `TextBox`.** The strip's inline editor
(`BeginInlineEdit`) and `HarmonicaSetTerminationDialog`'s three boxes both used to SEED from the exact
same formatted string the strip DISPLAYS — so baking left-padding spaces into `FormatZ`/`FormatGamma`'s
output put whitespace ahead of the caret in a live edit box. Concretely: typing "200" into a freshly
opened Z field now inserted after the leading pad spaces of a PRIOR reformat, landing the digits in the
wrong place — reproduced directly by `HarmonicaSetTerminationDialogTests`' own old-algorithm simulation,
which is exactly the caret-under-a-rewrite defect class brief-harmonicarf-r6a §6 already fixed once for
a different cause. **Fixed by splitting the two purposes**: `FixedWidth`/`FormatComplex`/`FormatZ`/
`FormatGamma` gained a `pad` parameter (default `true`, for display); every EDITABLE-text call site
passes `pad: false` — `ReadoutStripView.EditSeedValue` (new, parallel to `DisplayValue`), and
`HarmonicaSetTerminationDialog.LoadFields`. The marker context menu's read-only `"Γ = …"`/`"Z = …"`
header rows also pass `pad: false` — not because they are editable, but because the padding exists
ONLY to reserve a strip COLUMN's width, and a `MenuItem` header has no column to protect.

**Column width itself is reserved on the CONTROL, not inferred from the padded string.**
`HarmonicaReadoutFormatting.ReservedValueChars(item)` is a pure function of a row's KIND (Label/
IsComplex/IsGamma — never its value or even its current format, since it takes the WIDER of
rectangular and polar so a live format toggle cannot move the column either) — `ReadoutStripView`
writes `chars * fontSize * 0.55` to the value control's `Width` on every refresh, which is a no-op on
screen for a value-only update and stays correct across a live font-size change. The Settings column
gets the same discipline from a small per-key budget table (`SettingsValueWidth`), since §3's label
renames widened the LABEL column and the brief calls out rechecking the VALUE column too.

**§5 — one `ContextMenu` per chunk (not per row), relying on Avalonia's own ContextRequested
bubbling.** A complex row's existing per-row format flyout (`row.ContextMenu`, set only for `IsComplex`
rows) wins on a right-click landing inside it; everything else in a chunk (its header row, a plain
scalar row, the chunk's own whitespace) has no row-level `ContextMenu` and falls through to the
chunk-level one, built once per chunk in the constructor and populated lazily on `Opening` — the same
pattern `BuildLiveFormatMenu` already uses. `HarmonicaClipboard.RowsText(IEnumerable<(string, string)>)`
(new) factors out the one `label\tvalue\n` loop shared by the whole-canvas text-clipboard flavour, the
existing `Edit ▸ Copy Readouts`, and this new per-chunk Copy — the per-chunk version reads straight off
the chunk's own built controls (label/value/unit `TextBlock`s) rather than off `HarmonicaReadout`
objects, since the Settings chunk has no `HarmonicaReadout` backing at all.

## Smith charts — grab-anywhere VSWR, Add Point, fly menus (brief-harmonicarf-r6b, 2026-08-14)

**§1 — the VSWR circle has no gripper; the whole circumference is grabbable, and the drag is
unclamped.** `HarmonicaHitTest.Resolve`'s Pass 2.5 now hit-tests point-to-SEGMENT distance against
`LoadpullSurface.VswrLocus`'s own default-resolution polyline (the Data Display's `HitTestVswrLocus`
pattern), not a single θ = 0 handle point; `HarmonicaPanelRenderer.DrawVswrLocus` lost its square
handle glyph to match. `HarmonicaVswrHandle.HandleGamma` is gone — nothing needs "the" grab point any
more. The old display clamp (`VswrOf`/`RhoOf`'s `Math.Clamp(rho, 0, 0.99)` → `MaxVswr = 199`) is
gone too; `HarmonicaViewModel.SetMarkerVswr` now only floors at `MinVswr = 1.001` (VSWR ≥ 1 is
geometric, not policy). `VswrThrough`'s own rim-clamp on the DRAG POINT (`|Γ| < 0.999`) was also
removed — it was copied from `SetMarkerGamma`'s Γ = 1 guard, which matters there because that Γ
becomes a termination; here the drag point is only ever compared against a circle's centre/radius, so
the guard bought nothing but silently capping the exact gesture this brief exists to unlock.
`MaxVswr` is now `1e6` — a bisection SEARCH ceiling, not a display cap.

**MEASURED, NOT ASSUMED — the reason `MaxVswr` almost never actually bites.** For an ordinary
(passive, `|marker.Gamma| < 1`) marker, the WHOLE VSWR family stays strictly inside `|Γ| = 1` for
every finite VSWR — it approaches the rim as VSWR → ∞ but provably never reaches or crosses it (the
underlying power-wave Möbius map is an automorphism of the passive half-plane). Probed directly
across several passive centres up to VSWR = 1e6: max `|Γ|` on the locus never exceeded ~0.99999.
The MIRROR case — an ACTIVE marker (`|Γ| > 1`, R-h6-10's own flag) — has its ENTIRE family sitting
OUTSIDE `|Γ| = 1` instead, for every VSWR down to the floor. So "the user drags the circle outside
the Smith chart" (§1.2's own framing) cashes out as: a passive marker's circle can be dragged
arbitrarily CLOSE to the rim (any VSWR up to 1e6, never saturating at the old 199), while only an
ACTIVE marker's circle is ever actually beyond it. `HarmonicaVswrHandleTests` pins both regimes.

**§1.3 — the live readout is gesture state, not view-model state**, tracked on `HarmonicaGesture`
itself (`VswrReadoutActive`/`VswrReadoutPointer`/`VswrReadoutText`, set on press AND move, cleared on
release/cancel — mirrors `PlotControl._vswrReadoutActive`). Drawn by
`HarmonicaPanelRenderer.DrawVswrReadout`, called from `HarmonicaCanvas`'s own draw operation AFTER
`HarmonicaCanvasRenderer.DrawAll` (i.e. outside every panel's own clip rect) — the same "unclipped,
last" rule Data Display's `vswrReadout` block follows. `HarmonicaReadoutFormatting.FormatVswr` is the
ONE formatter both this readout and §2.1's menu header use, so the number a drag lands on is the
number the menu then shows.

**§2 — `Add Point`/`Add Points to VSWR` needed a THIRD layer in the grid model, not a second
`CustomGrid` contract.** `HarmonicaViewModel.AddedGridPoints` (an `ObservableCollection<Complex>`) is
additive on top of whatever `HarmonicaSolver.Options.GammaGrid` resolves to — the ring/spoke lattice
by default, or an imported `.gam`/dragged scatter when `GammaGrid` supersedes it.
`HarmonicaSolver.Solve` composes `(opt.GammaGrid ?? RingGrid(...)) ++ opt.AddedGridPoints` right
before calling `ContourGrid.Build`; no partial-reuse path was built (§2.2's own "either is
acceptable" — the node SET moves, which invalidates the RBF factorization cache by construction
anyway, so a full re-solve was the honest, not-noticeably-worse choice). **Measured**: a shipping
3 × 12 (37-point) grid re-solve is ~250–280 ms either way; adding one point (38 points) costs the
same order of magnitude, not noticeably more — one HB solve per point dominates regardless. Cleared
by `ResetGrid()` and by `SetGridPreset()` (the owner's own ruling: "the preset must always describe
exactly what is on screen"), NOT by a `.gam` import (`SetGammaGrid` only ever replaces the base — the
brief names this explicitly and does not list import as a third clearing trigger). Persists in the
`.charm` via a new `CharmIo.CharmDocument.AddedGridPoints` string array (`"re,im"` per entry, same
encoding `TerminationsToJson` already uses), absent-block-when-empty like every other optional
`.charm` field.

**§2.1 — `HarmonicaSetVswrDialog` (new)**, sized/shaped like `HarmonicaSetZ0Dialog` (a single field,
OK/Cancel gated) rather than `HarmonicaSetTerminationDialog`'s three-synced-rows shape — the closer
precedent for "one number." Reject-and-keep on non-finite or < 1, never a silent substitution; OK
commits through `SetMarkerVswr`, which now also flips `VswrEnabled` on (typing a value and seeing
nothing happen was the failure mode named in the brief).

**§3 — the MXP/MXE cross is gone from the Smith panels, deliberately (deferred to v2), but the
DATA is untouched.** `HarmonicaPanelRenderer.DrawOptima` is deleted; `SmithPanelData.Optimum` still
gets computed and populated exactly as before (the readout columns read it). `Optimum` came OUT of
`HarmonicaBackdropCache`'s `LayerAKey` — it was the only thing forcing a full Layer-A raster rebuild
every time the argmax moved during a drag, for a pixel difference that no longer exists.
`HarmonicaBackdropCacheTests.ChangingOptimum_RebuildsLayerA` inverted to
`..._DoesNotRebuildLayerA`, per the brief's own "invert, don't delete" instruction.

**§4 — one dispatch, two new panel-scoped fly menus, reusing (never re-deriving) existing
geometry.** `HarmonicaPanelRenderer.TitleBandHeight` went `private` → `public` so
`HarmonicaView.OnCanvasContextMenuOpening` can resolve a title-band click against the SAME band the
renderer draws into. Dispatch order: marker/glyph/VSWR-handle (unchanged) → `HarmonicaHitTest.PanelAt`
resolves a Smith panel → title vs body by `local.Y < TitleBandHeight(size)` → the Edit-Display panel
branches (power sweep / loadline), unchanged. Body: Copy (via `HarmonicaClipboard.CopyAsync` with the
RESOLVED panel id, never `Canvas.PanelUnderPointer()`) + Show Grid Points. Title: Contour Plane +
Contour Harmonic (built from `HarmonicaMenuViewModel.ContourHarmonics`, never hardcoded f₀/2f₀/3f₀ —
the exact bug that list already exists to prevent) on both charts, + Efficiency Metric on the
efficiency chart only — every item bound to the SAME `ICommand` the `Display` menu uses, checked to
show the current selection. This brief's own §4 note ("R6D and R6E extend this") means the dispatch
shape here is the pattern to copy, not a one-off.

**Tests**: `HarmonicaVswrHandleTests` (rewritten, 17), `HarmonicaSetVswrDialogTests` (5, new),
`HarmonicaSmithFlyMenuTests` (7, new), `HarmonicaAddedGridPointsTests` (12, new),
`CharmTracesAndGridReuseTests` (+2, `Harmonica.Tests`), `HarmonicaBackdropCacheTests` (1 inverted),
`HarmonicaR3cStripTests` (2 fixed for `TitleBandHeight`'s new visibility). `dotnet test tests/Ui.Tests`
6,702 passed; `tests/Harmonica.Tests` 172 passed; `tests/Firewall.Tests` 6 passed.

## The docked menu injection, the Settings merge, and a reformat-under-caret bug (brief-harmonicarf-r6a, 2026-08-13)

**§1.2 — "Markers shows, Display/Grid do not" did NOT reproduce as a throw under a normal
Inject/Withdraw/re-Inject cycle, headlessly.** `HarmonicaAppMenuInjectorTests` already proved (before
this brief) that a plain two-round Inject/Withdraw round-trip against hand-built stand-in items is
clean. What the old `HarmonicaAppMenuInjector.Inject` genuinely lacked was ATOMICITY: a bare `foreach`
appending one item at a time with no rollback, so if item 2 of 3 ever threw for ANY reason (an item
that already carries a `Parent` — `NativeMenu`'s own list validator refuses that), item 1 would already
be sitting in `appMenu.Items` while items 2 and 3 never landed — exactly the reported shape. Fixed to
be atomic (build a scratch `added` list, roll it all back on any exception) and failure-visible
(`InjectDockedItemsIfNeeded` now catches and reports through `HarmonicaViewModel.SolveError` instead of
losing the failure silently). `HarmonicaAppMenuInjectorTests.Inject_NeverLeavesAPartialSet_...` proves
the OLD code's exact vulnerability class (a poisoned item mid-list leaves a partial `appMenu.Items`)
and that the fix closes it.

**§2.1 — none of the three "Settings" paths the owner could reach were actually dead; the docked one
was unreachable, which reads the same from the outside.** circuitRF's own `Settings…` (File menu /
macOS app menu ⌘,) opens circuitRF's OWN app-level dialog and does something — it is just not
harmonicaRF's. harmonicaRF's own `Edit ▸ Preferences…` (torn-off/in-window) already worked, from an
earlier round's `RunHook`/error-reporting fix. What genuinely had no route at all: harmonicaRF's own
settings **while docked** — before this brief's §1.3, the docked injected set was Markers/Display/Grid
only, and the in-window `Menu` (which carries `Preferences…`) is hidden on macOS whenever docked. The
owner's report ("Edit ▸ Settings does nothing") is the visible symptom of clicking circuitRF's own item
believing it is harmonicaRF's — an easy thing to do, since docked, harmonicaRF's own Edit menu is not
visible at all. §1.3's injected `harmonicaRF` top-level menu (with its own `Settings…` item) is what
actually closes this, not a fix to any of the three items themselves.

**§6.1 — the exact "typed 200, committed 190" figure did NOT reproduce under the most plausible
headless caret model, and that is recorded rather than papered over.** `HarmonicaSetTerminationDialog`
is a `Window` (uninstantiable headlessly, same constraint as every other dialog in this file); the
mechanism was instead driven against the REAL `HarmonicaReadoutFormatting` parse/format functions under
a simulated "CaretIndex preserved across a programmatic Text rewrite, clamped to the new length" model
— the one documented, ordinary Avalonia `TextBox` behaviour. Under that model, typing "200" into a
FRESH, empty (selected-then-replaced) box happens NOT to corrupt — confirmed by test, not assumed. What
DOES reproduce, under the identical mechanism: typing into a box that already carries text (resuming
mid-edit rather than replacing a selection) corrupts outright, and so does anything with an imaginary
term or an exponent (`"-25+j40"` loses its imaginary part; `"1e3"` comes out as `0+j31`) — because the
reformatted string's own structure (a `+j0 Ω` tail, or a totally different digit grouping) shifts under
a caret index that does not know the string got longer or shorter. The fix removes the mechanism
entirely rather than chasing one caret model: the box currently being edited is now NEVER
programmatically rewritten (`LoadFields(except:)`), so no caret assumption is needed at all — reformat
happens exactly once, on blur (`OnFieldLostFocus`) or OK. See
`tests/Ui.Tests/Harmonica/HarmonicaSetTerminationDialogTests.cs` for both the (partial) reproduction and
the fixed algorithm's own gate.

## The instrument, the strip rebuild, and drag starvation (brief-harmonicarf-r5, 2026-08-13)

**§6's own bar — the owner's real drag, with the overlay on — is met.** Two prior briefs (R3B §1.4, R4
§4.6) each ended with "not measured this pass — requires a live interactive Avalonia session, which
this session had no way to drive." This one closes it: reported directly by the owner, from the
shipped build, first thing after landing —

> `last 16.7  mean 34  p95 17.5  p99 144.9  max 1632.0 ms   >33ms: 2/96`

**Read exactly, not smoothed over — the mean sitting ABOVE p95 is real and says something, not a
typo.** 94 of 96 frames are fast (p95 17.5 ms is comfortably under the 33.3 ms/30 fps line, matching
`last` 16.7 ms), and only 2 of 96 crossed the budget at all. The mean (34) and p99 (144.9) are both
being pulled hard by a single outlier — `max` 1632 ms is almost certainly one cold/first-touch frame
(JIT, first backdrop-cache fill, or a one-time GC pause), not a representative drag frame; one 1632 ms
sample alone contributes ~17 ms to a 96-sample mean, which is most of the gap between `mean` and `p95`
on its own. **This is exactly the right shape for "conflate-and-pace fixed the starvation, and the
strip rebuild fixed the steady-state cost, with one unrelated warm-up hitch left over"** — a
19 ms-ish stutter magnitude concentrated in ~2% of frames, not the ~90 ms/11 fps `EVERY` frame the
brief opened with. Matches the owner's own words ("extremely fast... exactly the UX I was looking
for") independent of the numbers. **Not yet separately isolated**: whether the `max` outlier is
specifically the document's first solve (a known, one-time, already-understood cost — first backdrop
fill, first HB solve, JIT) rather than a genuine mid-drag hitch. Worth a look only if it recurs; a
single first-frame outlier in an otherwise-clean 96-sample window is not a regression to chase.
`LastSetItemsMs`/`LastRenderMs`/the solve-stage breakdown/`SolvePool` counters/GC deltas were not part
of the reported line — the frame-interval read alone is what the owner chose to report, and it is
the one §0's whole diagnosis turned on ("stutter is frame-interval VARIANCE... no number anywhere in
this repo has ever measured it"), so it is the one that actually closes the brief.

**§1 — the instrument, built.** `HarmonicaDiagnosticsOverlay` (new, `src/Ui/Harmonica/`, framework-free
— a rolling 120-frame ring buffer of interval/GC samples, `Compute()` returning
mean/p95/p99/max/`>33ms` count fresh from the buffer every call rather than maintained running
aggregates) plus `HarmonicaDiagnosticsOverlayRenderer` (new, `Renderers/` — the Skia draw, plain text,
`IsAntialias = false` throughout, times its own draw and writes `LastDrawMs` back for the NEXT frame to
show, the same one-frame-behind convention `LastRenderMs` already uses). Owned by `HarmonicaViewModel`
(`Diagnostics`), not by the canvas, so `Display ▸ Reset Diagnostics Overlay` reaches it with no hook
back into the view. `HarmonicaCanvas`'s draw operation records a sample and draws the HUD, both gated on
`ShowDiagnosticsOverlay` (default OFF, persisted per document exactly like `ShowGridPoints` — new
`CharmAppearance.ShowDiagnosticsOverlay`, an untouched document still re-serialises byte-for-byte). It
shows every number §1.1 asked for: frame-interval last/mean/p95/p99/max + `>33ms` count,
`FrameTiming`'s own per-stage breakdown + `LastRenderMs`, the readout strip's `LastSetItemsMs` **and**
`LastSetInputsMs` (new — §1.1 also asked for this half to be timed "if it isn't already"; it wasn't),
`SolvePool`'s `StartedCount`/`CompletedCount`/`SupersededCount` + the completed/started ratio,
`NoOpDragFrameSkipCount`, `Lever1DisabledCount` (new VM passthrough to the solver's own counter), and
the GC gen0/gen1 deltas across the window. Deterministic tests (`HarmonicaDiagnosticsOverlayTests`, fed
a clock the same D1 convention `FrameScheduler` uses) pin the rolling-window arithmetic itself —
mean/max/percentile-ordering/window-eviction/reset-clears-the-seed — since the DRAW cost and a real
frame cadence are exactly the two things this environment cannot produce.

**§2 — `SetItems`, build-once/update-in-place, done and measured (headlessly, where it can be).**
Applied the Settings-column's own pattern (a per-column SHAPE SIGNATURE — label, header-or-not,
`IsComplex`, `Editable`, joined per row — compared before any `.Clear()`) to all five non-General
columns (OperatingPoint/Source/Load/Mxp/Mxe), independently: `_columnSignatures` is keyed by
`ReadoutColumn`, so adding an L2 marker rebuilds ONLY Load. `SettingsRowMayBeOverwritten` — the exact
predicate R3C built — now guards these rows' value slots too, closing R3C's own named follow-up "for
free": an open Source/Load inline editor is no longer destroyed and reopened as a stale row every
published frame, because the row is no longer destroyed at all in the steady state. The per-row
context menu (real/imaginary ⇄ magnitude/angle, "Set…") moved from eagerly rebuilt every `SetItems`
call to built once and populated lazily on `ContextMenu.Opening` — a user right-click, not a published
frame. The General column is explicitly untouched (still rebuilds every call) — it carries no editors
and is typically 0–1 rows, so it was never where the ~70–110-control cost lived. All 480 Harmonica
`Ui.Tests` pass, including 7 new tests pinning the signature's own dependence on the marker set (not on
the current VALUE) and the per-column independence claim at the data level. **`LastSetItemsMs` itself,
in the steady state of a drag, could not be measured this pass for the same reason §1's primary gate
could not — it needs the readout strip actually rebuilding real Avalonia controls, which needs the live
host.** The overlay reads it live now; that reading is what closes this.

**§3 — latest-wins starvation, real, fixed, and demonstrated (though not against a real pointer).**
Confirmed by reading exactly as the brief predicted: `HarmonicaViewModel.RequestFrameOnMarkerRelease`'s
`dragging: true` branch called `RequestScheduledFrame` — and through it, `SolvePool.Submit` — on EVERY
pointer-move with no pacing, and `Submit` cancels whatever was in flight before the new job even starts.
**Fixed with conflate-and-pace, not with a change to `SolvePool`** (guardrail 2 holds — latest-wins is
untouched for every other submitter): a mid-drag call now checks `DragSolveInFlight` — computed from
the POOL's own `LastCompletedSequence` against the sequence this class itself last submitted, not from
a private flag a completion callback would have to remember to clear — and conflates into a pending
slot rather than submitting when one is still outstanding. `OnPoolSettled` (called by whoever marshals
the pool's `Completed`/`Failed` events to the UI thread — `HarmonicaView` in the live app) submits the
conflated move the moment the in-flight one finishes, reading the marker's Γ at THAT moment rather than
whatever it was when the move first arrived. The marker glyph itself is never paced — `SetMarkerGamma`
still runs on every pointer event, unconditionally, before any of this. **This is where an existing
test's own assertion had to invert, and that is worth recording rather than quietly rewriting past.**
`HarmonicaDragTests.ASyntheticDrag_...` used to assert `SupersededCount > 20` on a 40-move burst as
proof latest-wins was collapsing the drag — correct for the OLD mechanism, and now the WRONG signature
for the fix: conflate-and-pace collapses the same burst by never submitting most of the 40 in the first
place, so `SupersededCount` stays near zero and the right assertion is that far fewer than 40 solves
ever START. Rewritten accordingly, plus three new deterministic tests
(`ConflateAndPace_*`) pinning the mechanism directly — a second move arriving before the first settles
does not reach the pool; the conflated move resubmits automatically once the in-flight one completes,
with no further pointer event; a 30-move synchronous burst starts far fewer than 30 solves, the glyph
still tracks the last move, and release still submits a real full-quality solve. **What could not be
produced: the `CompletedCount / StartedCount` ratio from an actual drag**, and with it, whether the
starvation was actually large enough to explain the owner's ~11 fps in practice rather than merely real
in principle. §3.2's own confirm-before-fix instruction is answered "yes, mechanically, by reading and
by a synthetic burst" — not yet answered "yes, and here is how much it cost" — for the same reason
everything else in this note carries the same caveat.

**§4 — the Avalonia dispatcher-priority finding, established by reading the installed 12.0.3 assembly,
not from memory.** `DispatcherPriority` in this version is a struct (not an enum), with an ordered
integer `.Value`. Reflecting the actual shipped `Avalonia.Base.dll` (12.0.3, the version this repo
pins): `Invalid −7, Inactive −6, SystemIdle −5, ApplicationIdle −4, ContextIdle −3, Background −2,
Input −1, Default 0, Loaded 1, UiThreadRender 2, Render 4, BeforeRender 5, AsyncRenderTargetResize 6,
DataBind 7, Normal 8, Send 9` (mirrors WPF's own canonical list, same names, same relative order).
`Dispatcher.Post(Action action, DispatcherPriority priority = default)` — confirmed via
`MethodInfo.GetParameters()[1].DefaultValue` and directly via `default(DispatcherPriority) ==
DispatcherPriority.Default` (`Value == 0`) — so `HarmonicaCanvas.OnRedrawRequested`'s
`Dispatcher.UIThread.Post(InvalidateVisual)`, which supplies no explicit priority, posts at `Default`
(0), confirmed **above** `Input` (−1) (`DispatcherPriority.Default.CompareTo(DispatcherPriority.Input) >
0`). So §4's suspected mechanism is real as stated: a redraw posted this way can win the dispatcher's
attention ahead of queued pointer-input processing during a burst. **Not acted on** — §4's own
guardrail is "only worth pursuing if the overlay shows the stutter clustering... rather than
throughout," which is exactly the reading this note cannot yet produce. `OnRedrawRequested` is
unchanged.

**Guardrails held.** Nothing in `PinSearch`/`ContourGrid`/`HarmonicaContext`/any solver path changed.
`SolvePool`'s latest-wins semantics are untouched for every submitter but the marker-drag path.
`SetItems`' rendered output is unchanged (source-scanned and behaviourally pinned, not eyeballed). The
overlay ships off by default, persisted, and every recording call site is gated on the toggle — no
timer runs and no buffer fills when it is off. `PlotRenderer`/`AxesRenderer` untouched.

**Full gate.** `dotnet build` clean across the whole solution. `dotnet test` (no flags, the routine
gate): Firewall.Tests 6/6, Core.Tests 1361/1361 (1 pre-existing unrelated skip), Harmonica.Tests
167/167, WBond.Tests 237/237, RfCore.Tests 298/298, Ui.Tests 6645/6645 (486 of them are this brief's own
— 480 Harmonica + a mix of new §1/§2/§3 tests). **One unrelated failure, confirmed a pre-existing
full-suite-load flake, not a regression**: `Engine.Tests`' `Hero1B_ImportElaborateAndSolve_
WithinBudgetAndConsistent` (a performance-budget gate, 12.4 s against a 10 s ceiling under full-suite
contention) — re-run alone, 1 s, comfortably under budget. Nothing in this brief touches `src/Engine`,
`src/Core`, or anything the Hero 1B fixture exercises; this matches this repo's own documented pattern
(`verify-races-under-full-suite-load` memory) of timing-sensitive gates flaking only under parallel
contention.

**Closed.** The owner's own reading (above) confirms what §2 and §3 argued for from reading and from
synthetic tests: the drag is fast now, and fast in the specific shape (a clean p95, two rare outliers)
that a fixed starvation-plus-rebuild-cost problem should produce rather than a merely-averaged-down one.
The per-stage numbers (`LastSetItemsMs`, the solve breakdown, the pool ratio, GC deltas) remain
available in the overlay for whenever a future regression needs them — that is what §1 built the
instrument FOR — but are not needed to close this brief, since the frame-interval read alone already
answers the question §0 opened with.

**Owner follow-up, same day — the two Display menu items removed, the code behind kept.** "Remove the
2 diagnostic menu items, but keep the code behind so we can turn this back on easily." Both AXAML lines
(`NativeMenuItem`/`MenuItem` for Toggle and Reset) removed from `HarmonicaMenuView.axaml`, on both menu
surfaces, each replaced with a comment naming exactly what to re-add. Nothing else moved:
`HarmonicaMenuViewModel.ToggleDiagnosticsOverlay`/`ResetDiagnosticsOverlay` (the commands themselves),
`HarmonicaViewModel.ShowDiagnosticsOverlay`/`Diagnostics`, the overlay/renderer classes, and the
`.charm` persistence are all untouched and still fully wired to each other — "turning it back on" is
re-adding the two lines the comments point at, nothing more. Pinned by test rather than left to the
comment alone: one test asserts the AXAML no longer references either command, a second drives both
commands directly (no menu in the loop at all) and confirms they still flip `ShowDiagnosticsOverlay`,
write `Appearance`, and reset the rolling window exactly as before.

## A batch of owner follow-ups: marker clamp, Contour Harmonic, a settings dialog, silent hooks (2026-08-13)

**`HarmonicaViewModel.SetMarkerGamma`'s own clamp was redundant with — and stricter than —
`HarmonicaDataSet.ImpedanceOf`'s already-correct handling of the SAME edge case.** The owner asked
for markers to be draggable outside the unit circle (negative Z, an active termination); the clamp
(`if (mag > 0.999) gamma = gamma/mag*0.999`) silently forbade ANY `|Γ| > 0.999`, forever. But
`ImpedanceOf`, one call downstream, already nudges only the true singularity (Γ = 1 exactly, where
`1−Γ` is the pole) and its own doc comment already says "`|Γ| > 1` is left alone, because an active
termination is a legitimate thing... to land on" — so the fix was deleting the redundant guard in
`SetMarkerGamma`, not narrowing it. **Lesson worth keeping: when a caller pre-clamps "to be safe"
before handing a value to a callee that already has its own, correct handling of the dangerous case,
check the callee before assuming the caller's guard is load-bearing** — this one had been silently
overriding a design decision made lower in the stack the whole time.

**Contour Harmonic was three hardcoded XAML items (f₀/2f₀/3f₀) on EACH menu surface, on a document
whose K is a live setting.** `SetGridHarmonicCommand` itself was already K-aware (validates
`k <= Terminations.HarmonicCount`) — only the ITEM LIST was frozen at 3, so K=5 had no menu path to
the bands it actually has. Fixed by mirroring the Markers menu's own `SourceBands`/`LoadBands`
pattern exactly (`HarmonicaMenuViewModel.ContourHarmonics`, an `ObservableCollection` rebuilt to K's
own length, triggered by the SAME `Markers.CollectionChanged` event the band checkboxes already used
— K only ever moves through `RetargetTerminations`, which always touches `Markers`, so no new
"K changed" signal was needed). Both surfaces (in-window `ItemsSource`, NativeMenu's own
code-behind `Fill`) share the pattern the band checkboxes already established; a new test
(`DisplayMenu_ListsTheSameItems_OnBothSurfaces`) checks SUBMENU parity specifically, since the
existing menu-parity test only ever compared top-level headers and would not have caught either
surface drifting alone.

**The SAME silent-guard bug R-h9c-10 diagnosed and fixed once (`ShowSetDutAsync`) was still sitting,
unfixed, in two sibling hooks in the identical file — `ShowPreferencesAsync` (the owner's own "Edit ▸
Settings does nothing" report — there is no menu item literally named "Settings"; it's Preferences…)
and `ShowSetZ0Async` (found alongside it, same shape, not yet reported).** `if (_doc is null ||
TopLevel.GetTopLevel(this) is not Window owner) return;` — a bare early return throws nothing, so
`RunHook`'s own exception-reporting fix (R-h9a-13) cannot help with it; the failure is silent by
construction, not by an exception slipping past a handler. **Worth stating plainly: R-h9c-10's own
note ("every OTHER dialog-opening hook in this file shares the identical guard shape... fixed because
it is the one under report") was accurate and specific — the SAME class of bug was always going to
resurface in the next sibling hook someone happened to exercise, and it did, twice.** Both are now
fixed the identical way (`Vm is not { } h → return`, then a NAMED `SolveError` + `Refresh()` on a
missing `TopLevel`) — any FUTURE dialog-opening hook copy-pasted from one of these now copies the
reporting shape too, not the silent one.

**A new per-document dialog (`HarmonicaAdvancedSettingsDialog`) for the four inputs the strip no
longer renders** (loadline pts / FFT× / charge / M — owner: "remove... from the display... set via a
menu item AND a settings in a separate dialog"). `HarmonicaInputs.Build` is UNCHANGED and still
returns all four — only `ReadoutStripView.SetInputs` stopped rendering them
(`HiddenFromStripKeys`, alongside the pre-existing `SettingsColumnKeys` split) — so the dialog reads
and writes through the exact same `HarmonicaViewModel.ApplyInput`/`HarmonicaInputs` keys the strip
row used to, per `HarmonicaSetZ0Dialog`'s own established "second surface, never a second write path"
rule. Four independent fields, each its own key — unlike `HarmonicaPowerSweepDialog`'s combined
Start/Stop/Step, there is no cross-field relationship to validate together here.

**Owner: "Idq should display in mA, not A; convert to A when searching for the proper Vgs."**
`BiasSpec.Idq` itself stays amps (the unit `SolveVgsForIdq` and every other solver-side consumer
expect) — the mA/A boundary is exactly ONE place, `HarmonicaInputs.Build`/`Apply`'s own Idq rows.
**Owner, same conversation: "keep Idq to 1 decimal place, Vgs to 3 — the inline editor should still
show the full value."** This needed a real DISPLAY-vs-EDIT split that did not exist before:
`HarmonicaInput.EditText` (falls back to `Text` when absent — every other input has no separate
rounding) is what an inline editor now seeds from, while `Text` is what the row shows at rest.
`ReadoutStripView`'s `SettingsRowState` gained `EditSeedText`, refreshed every
`UpdateSettingsColumnRow` call alongside the existing placeholder bookkeeping, so a double-click
reads the CURRENT full-precision value live rather than closing over a build-time one — the identical
staleness concern that already justified reading `value.Text`/`state.IsPlaceholder` live instead of
capturing `input` in R3C's own Settings-column closure.

## The strip's columns, Smith titles, and the efficiency axis fringe (brief-harmonicarf-r3c, 2026-08-13)

**The antialias/cap mismatch behind the two-colour axis line, and it will recur.** The power-sweep
plot's right axis showed a green fringe under the red efficiency-axis overlay because
`HarmonicaPanelRenderer.DrawEfficiencyAxisOverlay`'s cover stroke (`linePaint`/`tickPaint`) was drawn
`IsAntialias = false` with the default `Butt` cap, over `AxesRenderer.StrokePaint`'s antialiased,
`Square`-capped stroke of the identical nominal width. An antialiased stroke covers a wider pixel
footprint than a hard-edged one of the same width, and a `Square` cap extends half a stroke-width past
each endpoint where `Butt` does not — so the underlying border was always going to show as a border
around the cover, on every side and past both ends, regardless of colour choice. **The general lesson:
when one renderer paints over another's stroke to recolour it (rather than to add a new one), the
cover's `SKPaint` must match the covered one's shape field-for-field — width and colour are not
enough.** Fixed by matching `AxesRenderer.StrokePaint` exactly (`IsAntialias = true`, `StrokeCap =
Square`) rather than by widening `AxesRenderer` itself, per the standing "never widen `PlotRenderer`/
`AxesRenderer` for a harmonicaRF need" rule.

**Two owner-reported follow-ups on the inline editor itself, both found after the first pass landed —
worth keeping because they will recur wherever this codebase floats an editor over live content.**

- **Escape was silently eaten by `WorkspaceWindow`'s own `<KeyBinding Gesture="Escape"
  Command="{Binding DisarmPlacementCommand}"/>`.** A docked document sits inside that window, and a
  `KeyBinding` gesture is resolved BEFORE ordinary tunnel/bubble routing ever reaches the focused
  control — so the editor's own `box.KeyDown` Escape branch never ran. This is not a new failure mode:
  `SchematicView.OnViewKeyDownTunnel` documents hitting the IDENTICAL problem for its own inline
  editor, and the fix is the same shape — a `Tunnel`-routed `KeyDownEvent` handler registered with
  `handledEventsToo: true` (the only way to still see a key the KeyBinding already marked `Handled`),
  intercepting Escape for whichever editor currently has focus. **Any future inline editor hosted
  inside `WorkspaceWindow` needs this same handler — Escape does not work there by default.**
- **A spliced-in editor widens its own row, and a `StackPanel` column sizes to its widest row.** The
  original R-h9c-8 scheme removed the value control and inserted the `TextBox` in its place — so the
  box's `MinWidth` (70px) became that ROW's width the moment it opened, and every column laid out
  after it in `Columns` (a horizontal `StackPanel`) visibly shifted right. Fixed by floating the box in
  a new transparent `Canvas` (`EditorOverlay`, layered on top of the content in a shared `Panel`) at
  the original control's translated position, while the original control merely goes `Opacity = 0`
  (which reserves its layout slot; removing it would not). **The general lesson: an editor that needs
  to be WIDER than its cell must never become a literal member of that cell's layout container — float
  it in an overlay that shares the container's coordinate space instead.** A useful side effect: since
  `EditorOverlay` is untouched by `SetItems`'s per-frame `.Clear()` of the Source/Load columns, an open
  Source/Load editor now survives a published-frame refresh better than it did before this change,
  even though that specific hazard (previous bullet) was not itself the target here.
- **A third follow-up, same session: the flat `MinWidth = 70` this bullet's own fix carried over
  (unused once nothing else in the row constrained it, but still oversized for a short value like
  "-1.5") was itself owner-reported.** Replaced with `ReadoutStripView.CalcInlineEditWidth(text,
  fontSize)` — the IDENTICAL formula `SchematicView.CalcInlineEditWidth` already uses for its own
  inline editor (average per-char width for IBM Plex Sans, floored at two characters) — set on open and
  recomputed on every `TextChanged`, so the box genuinely grows and shrinks live as the user types
  rather than being sized once. Growing to the right falls out for free from the overlay shape above:
  the box's `Canvas.Left` is set once at open time and never touched again, so widening only moves the
  RIGHT edge.

**The title-band padding was NEVER the real cause of "the title renders too high above the chart" —
and two prior fixes (R-h9r2-13, then this brief's own §4) both tuned it anyway, because nobody had
measured the actual gap.** The 3rd owner report of the identical complaint prompted actually measuring
it against the shipped code rather than adjusting the same few-pixel constant a third time: on a
representative panel the gap between the title band and the VISIBLE Smith circle was **~63px, ~11% of
the chart's own height** — two orders of magnitude bigger than `TitleBottomPaddingFraction`'s few
pixels, which is exactly why tuning it twice never visibly helped. **The real cause was
`HarmonicaPanelRenderer.AnnulusHeadroom`**, R-h45-4's panel-wide 20% shrink (`k=1/(1+0.25)`,
`IntrinsicGlyphScale.DefaultMargin`) that reserves room around the ENTIRE Smith circle so a marker for
a device whose intrinsic Γ is legitimately outside the unit circle (§4.5 consequence 2 — ordinary, not
an error) is never clipped at the panel edge. That shrink is applied UNIFORMLY on all four sides via a
scale about the panel's own centre — so half of the freed-up space sits above the circle where the
title already lives, and half below where nothing does; neither prior fix touched it because both
were reasoning about the title band in isolation from what the chart itself does within `chartSize`.
Presented with the actual trade-off (a real, deliberately-built, but never empirically-measured-against
real device data safety margin, vs. a visibly tight chart), the owner chose to **remove the margin
entirely** (`AnnulusHeadroom = 0`, AskUserQuestion, 2026-08-13) and explicitly accepted that a
sufficiently far-out intrinsic glyph can be clipped at the panel edge again — the exact failure mode
R-h45-4 was built to prevent. `IntrinsicGlyphScale.DefaultMargin` itself is untouched (0.25) — it
governs the compression CURVE (how a glyph's position reads), a distinct question from whether the
panel shrinks to make room for it, and the request was about the panel, not the curve. **General
lesson worth keeping: when a repeated visual complaint survives a plausible-looking fix twice, measure
the actual pixel gap against the shipped renderer before touching the same constant a third time** —
the fix that finally worked took five minutes once the real number was in hand; the two before it
spent that same five minutes each on the wrong knob.

**The strip-rebuild-destroys-an-open-editor hazard, and how it was closed for the new Settings
column.** `ReadoutStripView.SetItems` (Source/Load/MXP/MXE) and `SetInputs` (the input half) both run
on every published frame and both used to handle this differently: `SetItems` clears and rebuilds its
four columns unconditionally (safe only because none of THOSE rows survive a rebuild anyway — an open
editor there gets destroyed and reopened as a stale row every published frame, a pre-existing gap this
brief did not touch), while `SetInputs`'s original always-live-`TextBox` scheme used a shape signature
plus per-row `UpdateInPlace` specifically so a solve landing mid-keystroke could not stomp the caret.
R3C's new Settings column (double-click-to-edit, like Source/Load) needed the SAME discipline
`SetInputs` already had, extended to cover "a row is mid-edit" rather than just "a TextBox has focus":
the column is built ONCE (its shape — the same 7 keys, in the same order, every time — never changes,
since `HarmonicaInputs.Build` always emits them) and every later call only WRITES into the existing
rows, skipping a row's value slot entirely while its own `SettingsRowState.IsEditing` is true. The
decision itself (`ReadoutStripView.SettingsRowMayBeOverwritten(bool isEditing)`) is a bare pure
predicate for exactly this reason — Ui.Tests cannot construct a live Avalonia control to prove a real
`TextBox` survives a refresh, but the boolean logic gating it is fully testable without one.

**The title band's render/hit-test coupling** (`HarmonicaPanelRenderer.TitleBandHeight`/
`GammaToCanvas`/`CanvasToGamma`) needed nothing new here beyond what R1B already documented — the 85%
size factor and the bottom-padding constant both flow through the same `TitleBandHeight` both
directions already call, so the coupling that fixed R1B's render-vs-hit-test bug could not be
reopened by construction. One thing worth stating that the existing comments do not: **the 7.0pt
floor is deliberately NOT scaled by the new 0.85× factor** — a panel small enough to hit the floor is
already at the smallest legible size, and shrinking the floor itself would only make an
already-clamped title harder to read for no space saved.

**A real, pre-existing gap found while surfacing the "solved Vgs" R3C §3 asked for, worth flagging
here because a future maintainer touching bias/Idq will otherwise assume the opposite.** The removed
readout-half "Vgs" row used to show the literal text `"(from Idq)"` whenever the bias was
current-driven — never an actual number. Searching the whole repo for how `Bias.Idq` is consumed
confirms why: `HarmonicaContext.Apply` substitutes a bare `model.Bias.Vgs ?? 0.0` whenever `Vgs` is
null, and nothing anywhere runs the "1-D secant on the DC solve" the tooltips and doc comments
describe. `Idq` is round-tripped and persisted (`.charm`, `CharmIo`) but never actually drives a
solve. R3C §3 preserves the informational text (now the Vgs Settings input's own `Placeholder`) rather
than inventing a number — implementing the secant itself is solver work and out of this brief's scope
(§6's guardrails).

## §1.4 — the drag frame's render cost, not the solve, is most of what the owner saw (brief-harmonicarf-r3b, 2026-08-13)

**The solve is no longer the story.** After §1's evaluator work, a mid-drag L1-marker frame's SOLVE
side (tier-A 46-solve sweep + dataset + loadline) measures **7.3 ms** — down from the brief's own
~33 ms baseline. What was never measured before is the REST of the frame, and it turns out to be the
larger half.

**Measured** (`HarmonicaDragFrameBreakdownTests`, `Category=Benchmark`, real solver + real
`HarmonicaPanelRenderer` SkiaSharp draw calls, a REAL carried-forward contour layer — the drag starts
from an already-solved 37-point grid, exactly as §1's own carry-forward rule keeps its polylines on
screen frozen through every drag frame, which a from-empty measurement would have understated):

| stage | 1x (1600×1000) | 2x / Retina (3200×2000 px) |
|---|---|---|
| solve (tier A + dataset + loadline) | 7.3 ms | 7.3 ms |
| **render** (2 Smith panels w/ 30 carried polylines + loadline + power sweep) | **11.5 ms** | **21.2 ms** |
| SolvePool.Submit → Completed round trip | ~0.0 ms | ~0.0 ms |
| **measured total** | **18.9 ms (53 fps upper bound)** | **28.5 ms (35 fps upper bound)** |

**The render is real and was previously invisible** — `HarmonicaRenderBudgetTests`' own R4 note said
the readout strip "costs a layout pass, not a frame of this number," which was correct but left the
CANVAS render itself unmeasured for an actual drag-shaped frame (a carried contour layer, not an
empty grid). It roughly **doubles from 1x to 2x**, which matters directly: a Retina/HiDPI display
(the ordinary case on macOS, one of this repo's three target platforms) pays close to the WHOLE
60 fps frame budget on the render alone, before the solve, the readout strip, or anything Avalonia
itself does are even added.

**Per-panel breakdown, the four panels drawn in isolation at their own real placement size** (not the
whole canvas — an earlier pass of this measurement drew each at full-canvas size, overstating every
panel; fixed to each panel's own sub-rect: Smith 800×600, loadline/power-sweep 640×500, matching
`RenderAt`'s own layout):

| panel | @1x | @2x |
|---|---|---|
| SmithPower | 2.40 ms | 7.01 ms |
| SmithEfficiency | 2.24 ms | 6.76 ms |
| Loadline | 1.13 ms | 1.42 ms |
| PowerSweep | 0.25 ms | 0.42 ms |

**Neither the loadline nor the power-sweep panel is the bottleneck** — combined they are 1.4 ms @1x /
1.8 ms @2x, a small fraction of the total. **The two Smith charts dominate**, at roughly 4–17× the
cost of the other two panels each, and scale far worse with device pixel count (nearly 3× from 1x to
2x, against loadline's ~1.3× and power-sweep's ~1.7×) — consistent with them being the panels that
draw the grid-point dots (37), markers, glyphs, contour polylines AND the Smith-chart chrome (circles,
grid lines, title rows) all at once, where the other two panels draw a handful of simple curves.

**Frozen contour DATA is not the same as frozen contour PIXELS — worth stating precisely, since it is
easy to mis-hear "carried forward" as "free."** R-h9r2-1's freeze means the 30 iso-line polylines are
not re-solved/re-fit/re-rastered during a drag, and that is genuinely true and unchanged. But
`HarmonicaPanelRenderer.DrawContours` is immediate-mode Skia with, by its own doc comment, "no
geometry cache" — it re-issues every `DrawPath` call from scratch on every repaint, and the panel DOES
repaint every drag frame (the marker glyph and power-sweep curve are live, which triggers
`InvalidateVisual` on the whole canvas). **Measured, isolated** (re-rendering the same frame with
`Contours` cleared): the 30 frozen polylines cost **1.0 ms @1x / 1.4 ms @2x** of the render total above
— real, but a small (~7–9%) share. The render cost is dominated by everything else on the panel (37
grid-point dots, markers/glyphs, Smith-chart chrome, the loadline and power-sweep curves), not by the
contours specifically. Caching the frozen layer as a pre-rendered picture/bitmap and compositing it
was considered as a follow-up but not built — the measured payoff (≤1.4 ms) does not justify it on
its own; it would only be worth doing as part of a broader render-caching pass across the whole panel.

**What could not be measured, and why, named explicitly rather than left implicit:**
- **The §7.5 readout-strip rebuild** (`ReadoutStripView.SetItems` — real Avalonia
  `StackPanel`/`TextBlock` construction, ~37 items → ~70–110 controls for this fixture, every
  frame). `Ui.Tests` may not call Avalonia runtime APIs (a hard project rule — SkiaSharp canvas
  drawing is not one of those, which is why the render above IS measurable), so this cannot be
  benchmarked headlessly. **`ReadoutStripView.LastSetItemsMs` (new)** self-times the call; reading it
  during the interactive check below is how this gets a real number.
- **The Avalonia compositor/dispatcher round trip** (the worker-to-UI-thread `Dispatcher.Post`,
  `InvalidateVisual`, and whatever layout/compositing Avalonia itself does around the raw canvas
  draw) — structurally unmeasurable outside a live `Application`/`Window`, for the same reason.

**The honest accounting:** measured solve+render+pool is 18.9–28.5 ms depending on device scale,
against the owner's ~90 ms (~11 fps) observation. The gap (~60–70 ms) is therefore concentrated in
exactly the two unmeasurable stages above, not spread thin across many small costs — which is a
useful, falsifiable claim for the interactive check to confirm or refute (read `LastSetItemsMs` and
compare a real drag's actual fps against the 35–53 fps upper bound this file computes from the
measurable stages alone).

## §4 — the render backdrop cache, and the pixel-mismatch bug that guarded it (brief-harmonicarf-r4, 2026-08-13)

`HarmonicaBackdropCache` (new, `src/Ui/Harmonica/Renderers/`) rasterises a Smith panel's Layer A
(chrome + frozen contour polylines + optimum cross) and Layer B (grid-point dots) once into offscreen
`SKSurface`s and blits them back — one instance per panel, owned by `HarmonicaCanvas`, never static.
`HarmonicaPanelRenderer.DrawSmithPanel` falls back to its original, byte-identical uncached draw when
no cache is supplied (export, Copy Plot, a one-off render).

**§4.5's own correctness gate — cache-on vs cache-off must be pixel-identical — did not hold on the
first cut, and the reason was subtle enough to be worth recording precisely.** `HarmonicaBackdropCacheTests`
caught it directly (`CacheOnVsOff_ArePixelIdentical_ForAStaticScene` et al.), initially failing with
~5% of pixels differing by up to 199 levels/channel — nothing like ordinary antialiasing rounding.
Root-caused to **three independent, compounding effects**, fixed in order:

1. **AA sub-pixel phase mismatch (the dominant one, ~9500 px).** An offscreen raster's own pixel grid
   always starts at phase 0 at its local origin. The live canvas, by contrast, places chart-local
   (0,0) at whatever FRACTIONAL device pixel its accumulated transform happens to land on — `ChartBox`'s
   margin/title-band arithmetic is essentially never pixel-integral. Rasterising Layer A/B at phase 0
   and blitting onto that fractional position forces Skia to resample the whole image, reprocessing
   every antialiased edge in the backdrop differently from the uncached vector draw. **Fixed** by
   reading `canvas.TotalMatrix` at the point of render, baking that exact matrix into the offscreen
   surface (`SetMatrix`, not a bare `Scale(deviceScale)`) shifted by only the INTEGER part of where
   local (0,0) lands (`floorX`/`floorY` — an integer translate cannot change AA phase), then blitting
   that integer shift back in raw device space (`canvas.SetMatrix(Identity)`, bypassing whatever CTM
   was active) — an integer-aligned, same-size copy needs no resampling at all. General on purpose
   (matrix-derived, not `deviceScale`-arithmetic-derived): verified to hold under an outer 2x HiDPI
   scale composed with a fractional outer translate too
   (`CacheOnVsOff_ArePixelIdentical_At2xWithAnOuterFractionalTransform`), not just the test harness's
   simplest identity-CTM case.
2. **Fractional destRect size (~300 px on its own).** `chartSize` (a `double`) fed a `Ceiling`d integer
   pixel size for the raster but the blit `destRect` used the un-ceiling'd fractional `chartSize`
   directly — a tiny (`pixelSize/deviceScale`)⁄`chartSize` rescale on every blit. Folded away by the
   same fix: `destRect` is now the raster's own integer extent, never `chartSize`.
3. **Double alpha-blend rounding through a transparent offscreen background (~28 px, ≤2 levels/channel
   — real, not merely theoretical).** Layer A clears to `SKColors.Transparent`, so every antialiased
   edge is 8-bit-rounded once when rasterised and AGAIN when composited onto the live canvas — two
   roundings where the uncached draw does one. **Fixed for Layer A** by clearing to the panel's real
   (opaque) background color instead: every edge blends against it exactly once, matching the uncached
   draw, and the blit degenerates to an exact copy (opaque source, no blend math needed). **Layer B
   (the grid-point dots) can't take the same fix** — it's sparse, so it can't be pre-filled with a
   uniform opaque background without occluding Layer A underneath it. Instead Layer B is **fused**
   directly onto a COPY of Layer A's already-opaque pixels in one compositing pass
   (`HarmonicaBackdropCache.GetOrRenderFusedWithLayerB`) rather than blitted as its own second
   translucent layer — exactly one rounding step per pixel, the same as the uncached path drawing dots
   directly over the already-rendered chrome. `LayerBRebuilds` still counts only when Layer B's OWN key
   (grid points/theme/chartSize/matrix/pixel size) changes, not when a recompose is forced by Layer A
   changing underneath it (`ChangingContours_RebuildsLayerA_NotLayerB` pins this distinction) — an F16
   offscreen color format was tried first as a precision fix and made things WORSE (6365 px, likely an
   implicit linear-light blend Skia applies for F16 targets), which is why the fused-compositing
   approach was built instead of chasing more bits.

**After all three: 0/176,400 differing pixels, cache-on vs cache-off, including at 2x with an outer
fractional transform.** All 15 `HarmonicaBackdropCacheTests` (bit-exact identity, and one test per
invalidation-key field — contours, levels, optimum, title/subtitle, grid points, panel rect, device
pixel scale, theme, `ShowGridPoints` toggle, iso-line labels, the R-h9r2-1 carried-list-reference
case) pass.

**Per-panel render cost, warm steady state of a marker drag** (`HarmonicaDragFrameBreakdownTests`,
`Category=Benchmark`, same 37-point carried-forward fixture §1.4 used, best of 9, measured alone),
directly against §1.4's own "before" table:

| panel | @1x before | @1x after (cache warm) | @2x before | @2x after (cache warm) |
|---|---|---|---|---|
| SmithPower | 3.30 ms | **0.16 ms** | 10.06 ms | **0.53 ms** |
| SmithEfficiency | 3.03 ms | **0.16 ms** | 9.84 ms | **0.53 ms** |

(The §1.4 "before" figures quoted above are re-measured on today's tree, not the original 2.40/2.24/
7.01/6.76 ms figures — this tree carries 37 Γ points/39 polylines against §1.4's 37/30, and §1/§3's
already-landed convergence fixes changed the exact grid, so the two are close but not identical; both
are reported as measured rather than reconciled, per this repo's own measurement-honesty convention.)

**Far better than §4.2's own "roughly halved, 3–4 ms @2x" prediction, and worth explaining rather than
just believing.** The prediction priced a naive two-separate-translucent-layer blit against "order 1–2
ms" for a raw 7.7 MB RGBA CPU copy. What's actually being blitted after the fused-compositing fix is
ONE opaque, axis-aligned, integer-pixel-aligned image — a case Skia's raster backend copies near
memcpy-speed rather than through general blend math, and the fusion means there is only ONE blit per
frame (not two) plus a handful of cheap live draws (marker glyphs, the reachable-region wash). The
speedup (≈20×, not ≈2×) reflects that the STATIC content (grid + 30–39 polylines + dots) was the
overwhelming majority of the original render cost, and a warm cache now pays for essentially none of
it every frame — consistent with, not contradicting, §4.1's own diagnosis that the cacheable share was
"most of each [panel], not 1.4 ms across both."

**§4.6 — `ReadoutStripView.LastSetItemsMs` was not read this pass.** It requires a live interactive
Avalonia session (real `StackPanel`/`TextBlock` construction — `Ui.Tests` may not call Avalonia runtime
APIs, per §1.4's own note above), which this session had no way to drive. Per the brief: not fixed
here regardless (out of scope), and the number is still worth reading in the owner's own interactive
check — §1.4's own estimate (~60–70 ms of the observed ~90 ms sitting in the strip rebuild + Avalonia
round trip) is now the DOMINANT term by a wider margin than before, since §4 just cut the render side
from ~7–10 ms/panel to ~0.2–0.5 ms/panel.

**A pre-existing, unrelated test failure was found and fixed while running the full suite as this
brief's own gate.** `HarmonicaPanelTests.Tier8_AGridWithAHole_DrawsNoContourAndNoFillInsideTheExcludedDisc`'s
own fixture (`BuildGridWithADeliberateHole`, `maxGamma: 0.85`) started failing its own precondition
(`Assert.InRange(grid.HoleCount, 1, …)`, actual 0) — not from anything in this brief, but from §3's
already-landed `PinSearch.Run` bracket fix (`src/Harmonica/RESOLVED.md`'s own §3 entry), which closed
most of the bracket-stage holes this smaller 31-point fixture used to rely on for "a few holes."
Scanned `maxGamma` 0.85–0.98 in 0.02 steps (deterministic — no RNG in this solve path): 0.90 reproduces
2/31 holes reliably; the test now uses that instead of 0.85, with a comment recording why.

## §5 — the drag-size FPS asymmetry: measured, not guessed, and it is real but small (brief-harmonicarf-r4, 2026-08-13)

The owner's own diagnosis (§5.1) named the mechanism exactly: `PinSearch.Sweep`'s `priorLevelSpectra`
(R-h9r2-19's "lever 1" — the previous FRAME's converged spectrum, tried first at every Pin level)
is a near-perfect seed on a small drag move and can be an actively misleading one on a large move that
lands the termination in a different HB solution basin, since the solution surface across the
termination plane is not smooth. Measured directly rather than assumed
(`tests/Harmonica.Tests/DragSeedPolicyTests.cs`, `Category=Benchmark`, Hero 2's GaN HEMT under
25 Ω/80+j10 Ω — the same fixture §1/§3 already use, chosen because the shipped default's own
unmarked-band terminations don't compress at all — shipped `PinMaxDbm=50`, §1's early stop already
landed, best-of-5 per frame after one discarded warm-up run):

| policy | small jump (\|ΔΓ\|≈0.004) | tangential control (\|ΔΓ\|≈0.13, const \|Γ\|=0.5) | large jump (\|ΔΓ\|≈0.99) |
|---|---|---|---|
| A — today (always reuse) | **9.23 ms** | **10.72 ms** | 13.76 ms |
| B — owner's (never reuse) | 12.10 ms | 12.01 ms | **11.94 ms** |
| C — hedged (below) | 9.22 ms | — | **11.88 ms** |

**Policy B does not win outright — measured, not assumed away.** The brief's own decision rule was
"if B's small-drag time is within noise of A, delete lever 1 and take B." It is not: B is ~24% SLOWER
than A at |ΔΓ| ≈ 0.004, a small, reproducible, above-noise gap (stable across repeated runs), and the
tangential control shows the same thing at a genuinely large per-frame Γ MOVEMENT (0.13) that never
approaches a harder region — so this is not the "large is also hard" confound §5.3 warned about; lever 1
is genuinely still winning there. So Policy B is not adopted outright.

**The threshold was found by scanning the crossover, not picked**
(`AvsB_CrossoverPoint_WhereLever1StopsHelping`, same fixture, single jump from a converged base point at
each size): A wins clearly through |ΔΓ| ≈ 0.15, ties through ~0.20–0.25, and B starts winning from
~0.30. `HarmonicaSolver.LeverOneDeltaGammaThreshold = 0.20` sits just past where A stops winning
outright — Policy C, the hedge, is what shipped: lever 1 is read only when the LARGEST single-band Γ
move since the previous frame (a freshly-marked band counts as infinite) is under this threshold.
Wired in `HarmonicaSolver.Solve` (new fields `_lastTerminationGammas`, `Lever1DisabledCount` — a
counter, not a stopwatch, gated by `HarmonicaSeedPolicyTests`), not in `PinSearch.Sweep` itself, which
is unchanged and still does exactly what its own doc comment says.

**Gradual, with one real cliff, not a clean either/or.** `PolicyA_FrameTimeVsJumpSize_GradualOrCliff`:
frame time rises smoothly from 9.7 ms (|ΔΓ|=0.01) to 11.9 ms (|ΔΓ|=0.20), then SPIKES to 18.4 ms at
|ΔΓ|=0.30 (only 103 Newton iterations there — fewer than the 118 at 0.20 — so the extra ~6.5 ms is not
"more iterations," it is one or more rungs' Newton solve taking an internal continuation-stepping
detour, §5.3's own predicted cliff mechanism), then drops back to 12.8/12.0 ms at 0.45/0.60. The cliff
is narrow and Γ-position-dependent rather than a clean function of |ΔΓ| alone — worth knowing, not
worth chasing further this pass.

**A large-jump drag frame's own factor over a small one, stated rather than asserted against a
target:** under the SHIPPED policy (C), 11.88 ms / 9.22 ms ≈ **1.29×**. Nothing like the owner's
subjective "roughly two thirds unaccounted for" 11 fps experience — which is exactly what §4's own
combined reading (below) explains.

**§5.4 — the no-op frame, independent of the policy work, landed too.** A mid-drag marker frame whose
Γ has not moved (quantised to `HarmonicaViewModel.DragNoOpGammaTolerance = 1e-4`, an order of magnitude
under both a Smith glyph's own on-screen resolution and every readout's decimal precision) past the
last frame ACTUALLY submitted to the pool never reaches `SolvePool.Submit` at all — `RequestFrameOnMarkerRelease`
returns `-1` (matching `DragGridPoint`'s own sentinel) and increments `NoOpDragFrameSkipCount`, a
counter. **Release is never skipped by this**, even when it lands within tolerance of the last mid-drag
frame — a real, full-quality solve always runs on release, matching `DragGridPoint`'s own "mid-drag is
free, release is real" shape. Gated on counters, not a stopwatch, exactly as the brief asked
(`HarmonicaDragTests.MidDragMarkerFrame_WithinToleranceOfLastSubmitted_IsSkipped_GatedOnACounterNotAStopwatch`,
`.MarkerReleaseAlwaysSolves_EvenWithinTheNoOpTolerance`).

**§4 and §5 measured together, as the brief's own §5.5 asked.** With Layer A/B's cache warm (§4:
~0.16–0.53 ms per Smith panel, down from ~3–10 ms), the render's contribution to a drag frame is now a
small fraction of the SOLVE side above (9–14 ms) rather than comparable to or larger than it — so the
solve, and specifically the seed-policy asymmetry this section measures, is now the dominant and
VISIBLE cost in a drag frame, confirming §5.5's own prediction ("the asymmetry will be more visible
after §4 than before it, not less") rather than needing a separate render-included re-measurement:
render is close enough to zero now that solve-only numbers above already stand in for total frame time
to within the ~1–3 ms `HarmonicaDragFrameBreakdownTests` measured for the non-Smith panels.

**Not chased further, named rather than silently dropped:** the ~60–70 ms `ReadoutStripView.LastSetItemsMs`
gap from §1.4/§4.6 is unmeasured in this headless environment and is now, by a wide margin, the largest
unaccounted-for piece of the owner's original ~90 ms/11 fps observation — bigger than everything §4 and
§5 together move.

## A grid-point drag was costing the whole tier-A power sweep (brief-harmonicarf-r3b §2, 2026-08-13)

**A gesture that changes no circuit state was costing 46 HB solves.** `HarmonicaViewModel.
DragGridPoint(dragging: true)` routed every mid-drag frame through `RequestFrame`, whose
`OptionsFor(..., dragging: true)` sets `SkipContours = true` — but `SkipContours` only ever skips the
CONTOUR GRID build; `HarmonicaSolver.Solve` runs tier A's whole `PinSearch.Sweep` ladder
unconditionally, every frame, at terminations a grid-point drag never touches at all (the dragged Γ
is a sample the grid sweeps LATER, not a termination anything solves against). R-h9r2-4 chose the
"splice the moved point into the carried `GridPoints` list, display only" shape precisely so this
gesture would be cheap, then routed it through the full frame pump anyway.

**Fix:** a mid-drag grid-point frame no longer calls `RequestFrame`/touches `_pool` at all. It splices
the moved Γ into the CURRENTLY PUBLISHED `Frame.SmithPower`/`SmithEfficiency` grid-point lists
directly (the existing `ApplyGridPointOverride` helper, already built for exactly this splice) and
sets `Frame` — an `[ObservableProperty]`, so the assignment itself raises `RedrawRequested` via
`OnFrameChanged`. Same no-re-solve shape as `SetMarkerVswr`/`ToggleMarkerVswrEnabled`, applied to a
grid point instead of a marker overlay. `CustomGrid` stays untouched mid-drag (unchanged from
before — only committed on release), and release (`dragging: false`) is unchanged: it still commits
into `CustomGrid` and requests a real frame with `ReuseUnchangedGridPoints = true`.

**Gated on a counter** (`HarmonicaGridPointDragTests.
MidDragGridPointFrame_CostsZeroHbSolves_GatedOnACounterNotAStopwatch`): five simulated pointer-move
events during a drag leave `SolvePool.StartedCount` and `HarmonicaSolver.LastSolveCount` unchanged,
while the glyph's own Γ visibly tracks the last move — and release still submits a real solve. All
6563 `Ui.Tests` pass.

## macOS native menu: docked focus and the crash (brief-harmonicarf-r3a, 2026-08-13)

The macOS "menu not shown when docked" bug and the "crashed switching apps / opening Settings" crash
were ONE bug, not two, and R2B's own diagnosis of the crash ("a genuine Avalonia.Native race this
view cannot see into") was wrong — the mechanism is fully knowable from Avalonia 12.0.3's own source
(`src/Avalonia.Native/AvaloniaNativeMenuExporter.cs`, `IAvnMenu.cs`).

**The standing invariant, from here on: on macOS, a window's `NativeMenu` instance is chosen ONCE
and never changes for that window's whole lifetime. To change what the menu bar shows, mutate that
instance's `Items` — never call `NativeMenu.SetMenu` on a window a second time with a different
instance.** Four facts pin this down:

1. **One `AvaloniaNativeMenuExporter` per `TopLevel`, created once, never torn down.** Every
   `NativeMenu.SetMenu(window, x)` for that window routes to the SAME exporter, for the window's
   whole life.
2. **The exporter binds to the FIRST `NativeMenu` instance it is ever given, permanently.**
   `__MicroComIAvnMenuProxy.Initialize` is called only on that first bind. Its own `Update`:
   ```csharp
   internal void Update(IAvaloniaNativeFactory factory, NativeMenu menu)
   {
       if (menu != ManagedMenu)
           throw new ArgumentException("The menu being updated does not match.", nameof(menu));
   ```
   A second, different instance handed to the same window throws — synchronously, on the calling
   thread, out of `NativeMenu.SetMenu` itself.
3. **`SetMenu(window, null)` is not a clear — it substitutes a brand-new empty `NativeMenu`**
   (`_menu = menu ?? new NativeMenu();`), so calling it on a window that already holds a real menu
   ALSO throws, for the same reason (the throwaway empty menu is not `ManagedMenu` either) — R2B's
   own "defensive clear" was therefore a poisoning step, not a safety step, and is now gone.
4. **`_menu` is assigned BEFORE the throw, and a later dispatcher-queued reset re-reads it.** Any
   `NativeMenuItem` added to or removed from the exporter's *original* menu calls `QueueReset()` →
   `Dispatcher.UIThread.Post(DoLayoutReset, ...)`. That queued call re-runs `SetMenu` with the now
   *poisoned* `_menu` and throws again — on the dispatcher, where no call-site `try`/`catch` can
   reach it. This is the exact owner-reported crash: a menu-item mutation (rebuilding the Window menu
   on `Activated`, or opening Settings) some time AFTER the poisoning attach is what actually brings
   the process down, which is why the failure looked delayed/intermittent rather than immediate.

**The fix (`HarmonicaMenuView.RecomputeAttachment`, split into `AttachToWindowOutright` +
inject/withdraw):** a torn-off document or the standalone binary still owns its hosting window
outright via `NativeMenu.SetMenu` (that window has never had a menu, so this is always the FIRST
bind and always succeeds). A **docked** document never calls `NativeMenu.SetMenu` on the
`WorkspaceWindow` at all — that window's exporter is already permanently bound to circuitRF's own
app-menu instance (`WorkspaceWindow.AttachNativeMenuAtApplicationScope`, at startup). Instead, on
docked focus, the document's own top-level items (Markers / Display / Grid — not File/Edit/Help,
which circuitRF's bar already shows) are appended to that SAME instance's `Items`
(`HarmonicaAppMenuInjector.Inject`), and removed again — by reference, never by header match — on
blur (`.Withdraw`).

**The item-`Parent` validator forces a THIRD rendering, not a copy.** `NativeMenu`'s list validator
throws `InvalidOperationException` for any item that already has a `Parent` — so the injected items
must be freshly-built `NativeMenuItem`s from `HarmonicaMenuViewModel`'s own collections/commands
(`HarmonicaAppMenuInjector`), never `_ownMenu`'s own children. This mirrors the view's existing
"TWO SURFACES, HAND-MIRRORED" shape (the in-window `Menu` and the standalone `NativeMenu` are already
two independent renderings of one source) — the injected set is simply a third.

**`WorkspaceViewModel.TryWireWindowFocusTracking`'s Harmonica/WBond exclusion already closed the
§2.3 ordering trap**, before this brief: `AttachSharedNativeMenuIfMacOS` is gated on
`doc is not HarmonicaDocument and not WBondDocument`, so a torn-off harmonicaRF/wBond window can
never receive circuitRF's shared app-menu instance regardless of activation order (each owns its own
per-window attach). This makes the invariant type-based rather than order-dependent — verified, and
now pinned by a dedicated test, rather than left as "today's ordering happens to favour it."

**`Dispatcher.UIThread.UnhandledException` (`App.WireNativeMenuDispatcherBackstop`) is a floor, not
the fix** — it exists only because a queued `DoLayoutReset` throw is, structurally, unreachable by
any call-site `try`/`catch`. It matches ONLY `ArgumentException("...menu being updated does not
match...")` whose stack contains `Avalonia.Native`; a blanket handler was rejected on purpose.

## harmonicaRF menu round (owner bug list, 2026-08-15)

**Display ▸ Contour Harmonic going stale after a K edit was real, and had TWO independent causes —
the first fix pass only caught the first one.**

1. `HarmonicaAppMenuInjector.BuildDisplay` — the THIRD rendering of the menu, injected into
   circuitRF's own app menu while a **docked** harmonicaRF document has focus (the in-window `Menu`
   and the standalone/torn-off `NativeMenu` are the other two) — built Contour Harmonic from three
   hardcoded items (`f₀`/`2f₀`/`3f₀` via `SetGridHarmonicCommand`), the exact bug
   `HarmonicaHarmonicMenuItem`'s own doc comment already named as fixed elsewhere. Fixed by building
   the submenu from `vm.ContourHarmonics` directly, the same collection the other two surfaces read.

2. **The real reason the bug survived that fix, on macOS specifically.** Neither NativeMenu-based
   surface (standalone/torn-off, or docked-injected) subscribes to `ContourHarmonics` directly —
   `HarmonicaMenuView` only listens to `SourceBands`/`LoadBands.CollectionChanged`
   (`OnBandsChanged`), and rebuilds the NativeMenu's Contour Harmonic submenu as a side effect of
   that. `HarmonicaMenuViewModel.RebuildBandMenus` used to call `Sync(SourceBands, …)` /
   `Sync(LoadBands, …)` — whose own `Clear()`/`Add()` raise that CollectionChanged SYNCHRONOUSLY —
   **before** `SyncContourHarmonics()`. So by the time `OnBandsChanged` fired and read
   `vm.ContourHarmonics`, that collection had not been rebuilt for the new K yet — it read the OLD
   K-length list, one call behind. `SyncContourHarmonics()` never got a rebuild trigger of its own,
   so nothing rebuilt the NativeMenu submenu again once it finally did update. This is why it looked
   *intermittent* even after fix #1: correct immediately after some OTHER band edit trips
   `OnBandsChanged` a second time, wrong right after the K edit itself. The in-window `Menu` was never
   affected — its `ContourHarmonics` `ItemsSource` binding updates independently of call order. Fixed
   by reordering `RebuildBandMenus` to call `SyncContourHarmonics()` FIRST, so every later
   `SourceBands`/`LoadBands` subscriber — on any surface, present or future — sees the new K's
   `ContourHarmonics` already in place. Pinned by
   `HarmonicaMenuAndInputTests.ContourHarmonicMenu_IsAlreadyAtTheNewK_WhenSourceBandsCollectionChangedFires`,
   which reproduces the ordering directly against a `SourceBands.CollectionChanged` subscriber with no
   Avalonia/NativeMenu platform involved (confirmed to fail — observed count 3, not 5 — against the
   old call order before the fix).

**"harmonicaRF ▸ Copy Plot/Copy Readouts/Copy Termination Set" (owner's literal wording) turned out
to mean the *Edit* menu's copy of these three items, not only the docked-injected `harmonicaRF`
top-level menu's copy.** The first pass removed them from `HarmonicaAppMenuInjector.BuildHarmonicaRf`
(the one place literally titled "harmonicaRF") on the reasoning that the other `X->Y` bugs in the same
report all named their literal parent menu. The owner still saw them on macOS afterward — they meant
Edit ▸ Copy Plot / Copy Readouts / Copy Termination Set, which every surface still carried. Removed
from both the NativeMenu and in-window Edit menus too; `CopyPlotCommand`/`CopyReadoutsCommand`/
`CopyTerminationsCommand` and their hooks stay wired, same convention as Grid ▸ Solve Now and
Markers ▸ Reset to Defaults above.

**The `.npy.npy` suggested-filename bug (Export Data…) was the identical class of bug
`ExportTestbenchAsync` had already fixed once, in the same file, and the fix note said so at the
time.** `SaveFilePickerAsync`'s `DefaultExtension` already appends the extension; `ExportDataAsync`'s
`SuggestedFileName` was separately appending `".npy"` on top of it. `ExportTestbenchAsync`
(`HarmonicaView.axaml.cs`) carries a comment recording the exact same trap for `.csch` — the two call
sites simply hadn't been kept in sync.

**"Running the coarsest contour grid to keep up" (`FrameScheduler.RecordFrame`'s D4 message) is
retired** — harmonicaRF no longer has a coarse-grid tier-B rung worth naming in a user-facing string
(R-h9r2-2 already retired every OTHER per-rung message for the same reason; D4's was the one
message that survived that pass). `TierAHealthy` still latches `false` and is still the signal any
future caller should read — only the string is gone, so `StatusMessage` is simply `null` in this
case now.
