# `src/Ui/Layout/Em` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Ui/DataDisplay/RESOLVED.md`.

## The port marker: width measured at the end face, and a second mark for a gap (2026-08-24)

Both found by building the user-doc figures for port placement, on a real MKLOPF. A figure of a live
control is a test of it, and this is the second time that has paid — the first was `EmSetupWithLayout`.

### A port's WIDTH was the conductor's BOUNDING BOX

`LayoutPortDirection.Resolve` took the width from `WidthAcross(bbox, direction)`. On a rectangle that
is the metal, to the DBU. On anything that changes width along its length it is not: a Klopfenstein
taper's box is its WIDE end's width at BOTH ports, so the narrow end drew a reference-plane bar four
times the metal actually there and an arrow scaled to match. The number is user-visible twice — the
bar and arrow the editor draws, and the width the Properties Inspector reports for the port.

**That file's own header already recorded this defect arriving by the other route** (a port on a PCell
INSTANCE, where the array-expanded box spanned a 63 × 9 mm envelope against a real 1.06 mm pin) and
the cure there was to prefer the cell's own `LayoutPin`. This is the top-level-polygon case, which has
no pin to prefer. The header's own sentence — *"a bbox is what you fall back to when there is no pin,
not the thing you prefer"* — is now true of both routes.

**The fix is a scanline** (`LayoutPortDirection.SpanAt`): flatten the shape's outline, cut across it
just inside the end face, and keep the run of metal that contains the port's own transverse position.
Three details are load-bearing:

- **The cut is INSET, by a thousandth of the conductor's length.** A cut exactly on the end face runs
  along that face's own edge, where a scanline's crossings are degenerate — the answer would be 0, 1
  or the whole span depending on rounding. The inset costs one part in a thousand of the flank's
  slope, which is what the gate's tolerance is sized from rather than from taste.
- **The run CONTAINING the port, never the total.** A cut across a tee or a coupled pair crosses
  several pieces of metal and the port is on one of them.
- **`ConductorInfo` had to carry the SHAPE.** It carried only a `Bbox`, which is precisely the
  information a width measurement needs and the thing a bounding box has already thrown away. Null for
  an instance, where the box (or the pin) remains the honest answer and the marker still draws.

### The internal delta gap needed its own mark, and the first two attempts were invisible

An internal port's cut is mid-conductor, so the edge port's mark is not merely unhelpful there — it is
wrong, because `PortHint.PlaneX/Y` snaps the bar to the conductor's END. A gap drawn that way puts the
cut at the far end of the trace.

`DrawInternalGapMarker` draws two bracketed bars facing each other across a break, at the label's own
position, re-measured at that station (`SpanAt`'s `alongAt`). Two things were learned by looking at a
rendered figure, neither of which is visible in the code:

- **Flanges drawn at the bar ends are invisible**, because those ends sit exactly ON the conductor's
  own outline and the flange is drawn on top of it. Lengthening them changed nothing at all; the mark
  read as two plain parallel lines however large the number got. The fix is an OVERHANG — the bars run
  past the metal, and the brackets are drawn from the overhung ends, where there is background behind
  them. An edge port never had this problem because its bar's ends are at the end of the structure.
- **A headless shaft through the break reads as debris.** The arrow was removed from the gap marker
  entirely (owner request): an edge port's head lands on its reference plane and a gap has no single
  plane for one to land on. Polarity is a number, not a picture — the run's note names it.

### The edge port's arrow now ARRIVES at the plane

Owner request. It ran from the plane INTO the metal; it now approaches from outside with its head on
the bar. Two consequences worth recording: the marker stops covering the conductor (the old arrow was
longest exactly where the port was widest, so a wide port's arrow lay a third of the way across the
part), and `PortArrowLengthOverWidth` had to come down from 0.66 to 0.35 — two thirds of the port
width is a sensible proportion of a thing you are lying ON and a long tail in the empty space beside
the part. `LayoutPortArrowSizingTests`' five claims are all relative and hold unchanged at 0.35.

### An internal gap port on the UNIFORM-LINE kernel is refused by name

The trap is that it does not look like one: **a uniform line carrying an interior gap is still a
uniform CROSS-SECTION**, so kernel A's extractor accepts it and `Auto` prefers A whenever A accepts.
Kernel A never meshes the plane — its two ports are the ends of the extracted line by construction —
so the gap has nowhere to be, and nothing in that path could report its absence. The run would publish
a complete, plausible two-port answer for the line WITHOUT the port the user placed.

`EmSetup.DeclaresInternalGapPort()` is asked in `EmRunService` right after the kernel choice, and the
panel shows the same refusal live (`InternalGapOnTheWrongKernel`, sharing
`EmRunService.InternalGapNeedsFullWave`'s wording rather than paraphrasing it). Live matters more here
than for most refusals **because the user did not choose that kernel — Auto did**, and nothing else on
screen connects "I set port 3 to Internal delta gap" to "the answer has two ports". Silently
re-routing to the planar kernel was rejected: it is a guess at intent that costs minutes of solve time,
and the remedy is one dropdown.

### Two `.cem` files on one `.clay`: the layout names its owner

Asked as a design question and it is a real conflict, not a bug to design away. More than one EM setup
may analyse one layout, and two of them may legitimately disagree about a port — which is the whole
reason the type is an analysis setting. But there is only ONE layout on screen and it can draw only
one of the two answers, and it was drawing whichever setup last refreshed with nothing saying which.

`LayoutEditorViewModel.InternalGapPortsOwner` records whose interpretation is on screen, and
`WorkspaceViewModel.AdoptPortTypes` posts a Messages line when a takeover **actually changes the
marks**, naming both setups. Two properties of that are deliberate:

- **A takeover that changes nothing says nothing** — two setups that agree, or the same setup
  refreshing, which is the overwhelmingly common case. A message nobody can act on is one they learn
  to skip, which is the same lesson `CheckFeedClearance` already cost this area once.
- **The owner is cleared by an ordinary layout edit**, R-em-17's rule applied to this overlay: an
  edited layout drops what a setup told it about itself rather than drawing marks against moved metal.

This is worse for port TYPES than for the overlays that share the channel (mesh, current density,
reference planes) and the difference is why it needed anything: those become non-null only after Mesh
or Simulate, so "the setup you last acted on" is a defensible reading of them. Port types are pushed on
every refresh, so they could flip while a user typed in an unrelated field of the other setup.

### A gap port's LABEL is centre-aligned, and only a gap port's

Owner observation: the text sat to one side of a mark that is symmetric about its anchor. Every other
label in a layout is left-anchored and that is not merely cosmetic in general — **Flatten to Polygon
turns label text into real geometry**, so a render-only alignment would diverge from it.

**A PORT label is the exception that makes it safe**: it is excluded from Flatten to Polygon and from
every boolean (`LayoutEditorViewModel.Booleans` returns an empty result for `IsPort`), so nothing
downstream can disagree with where it was drawn. Checked before the change rather than assumed — the
alternative was a figure-only nudge in the fixture, which would have fixed the picture and not the app.


### The co-simulation figure, and the question it answers

`Example_EM_SeriesGap.csch` + the `em-series-gap-cosim` figure exist because the correct way to use an
internal gap port **looks wrong**: the series component is drawn from the SnP's port 3 to GROUND, which
reads as a shunt element.

It is correct, and the reason is worth having written down. `SnpModel.Stamp` imposes the network's own
relation `V_k − V_ref = Σ_j Z[k,j]·I_j`, so terminating port 3 with an impedance imposes that
impedance's V-I on **the gap voltage and the current crossing the gap** — which is the definition of
inserting it into the cut. circuitRF's SnP is single-reference (N or N+1 nets, one shared reference,
never a per-port differential pair, unlike `Z_Port`/`SDD`), and that is FINE here: the shared ground is
the N-port formalism's bookkeeping, not a claim that one lip of the cut is grounded.

**This was checked before the figure was drawn, not after.** Had SnP's shared reference actually been
wrong for a series port, the feature would have shipped with no way to consume its result.


### The gap mark is drawn at the MESH's width once a mesh exists

Owner request. The break was a fixed fraction of the port width — a legibility glyph — which is the
right answer only while there is nothing to measure. The gap the solver uses is the pair of mesh cells
either side of the cut, and with the mesh overlay drawn underneath, a fraction is a number that means
nothing sitting next to numbers that do. `LayoutRenderer.MeshGapHalfWidth` reads it off
`LayoutRenderOptions.PlanarMesh`; null (no mesh, or an edit that invalidated it) falls back to the
fraction, so a stale width can never sit on screen looking like a live one.

Three things that were not obvious until it was drawn:

- **Two half-widths, not one.** A graded mesh's two cells differ, and near a conductor end they
  routinely do. Each bracket lands on its own cell's outer gridline.
- **They are returned in the PORT's frame, not world order.** R180/R270 point down-coordinate, so
  their extents swap. Invisible on a uniform mesh — the two are equal there — which is exactly why
  `TheTwoHalvesAreInThePortsOwnFrame_NotWorldOrder` asserts it.
- **The mark had to move to the CUT.** It was centred on the label, and the cut is the nearest
  gridline, which can be half a cell away — so the brackets would not have landed on the gridlines
  underneath them, which is the entire claim the change makes. Caught by the first version of
  `TheBreakIsTheTwoMeshCellsEitherSideOfTheCut`, not by looking. The snap is now drawn as a dashed
  leader from the label to the cut, the idiom an edge port already uses.

**This does NOT re-resolve the port.** `PlanarPorts` additionally requires the cell pair to be paired
into a ROOFTOP, which depends on the conformal cut and is not reconstructible from a mesh report. Where
the two disagree the mark still sits on a real gridline and the run's note remains the authority — it is
a picture of the mesh, not a second resolution.

### The planar path never said which conductor was ground

Found from the question "on a `.ctech` with many metal layers, how does the user specify the edge
port's negative terminal?" The answer is that they do not — R-em-4 resolves ONE ground plane from the
technology and every port returns through it — but the panel could not tell them which one it picked:
the **"Ground reference" row is bound to `Readback`, which is the CROSS-SECTION readback**, and a
full-wave run produces none. `PlanarExtractor` named the plane only in the FALLBACK branch (no
conductor designated), i.e. in the case where the answer is least likely to be what anyone wanted.

`PlanarExtractor` now emits a note in the normal branch too, naming the conductor, its height, the
signal level it sits below, and the fact that the plane is not per-port and is modelled as laterally
infinite. R-em-4 is a 2%-scale trap this area has already paid for once (taking `Stackup.Bottom`
literally instead of the designated conductor cost the Tier A oracle), and on a many-metal stackup
"the highest ground-designated conductor below the signal" is not something a user can derive by
looking.

**The user-facing page asserted the wrong thing** — that the panel shows the resolved ground under the
cross-section readback — which was true of kernel A and false of the kernel that page is about. Fixed
in the same pass.


## `RaiseState` had been emptying the PLANAR port list since L8e (2026-08-24)

Found while wiring the new per-port TYPE control into the same rows, not by a report — nobody had
noticed, because there was nothing to notice: the list simply never appeared.

`EmSetupEditorViewModel.RaiseState` carried a stale-row guard, `if (Problem is null &&
PortRows.Count > 0) RebuildPortRows(null)`. It was written when there was one extractor, and
`Problem` is the CROSS-SECTION `EmProblem`. A planar refresh leaves that null by construction — its
problem is `PlanarProblem` — so `RefreshPlanar` called `RebuildPlanarPortRows`, filled the list, then
called `RaiseState` one line later and emptied it again. **`ShowPortList` is true for every planar
analysis, so the panel showed an empty per-port group.** R-cpl-6's per-port reference impedance has
therefore been unreachable for a full-wave setup since L8e; only `Port1Z0`/`Port2Z0` were editable,
which is enough for a 2-port and silently wrong for anything else.

**Why no test caught it:** every existing `PortRows` test (`EmCoupledSetupTests`) drives the
cross-section kernel, where `Problem` is not null and the guard does not fire.
`RebuildPlanarPortRows` had no test of its own at all.

The fix is one term — both problems must be null for the rows to be stale, which is what "no
extraction succeeded" actually means now that there are two extractors —
and `InternalDeltaGapPortUiTests.ThePlanarPortListIsPopulatedAtAll` is its regression test,
deliberately written about the impedance rather than the new control so it keeps standing if the
port-type row is ever removed.

**The generalisable shape:** a guard phrased as "if the (one) problem is null" outlives the moment
there is a second problem, and it fails by producing an EMPTY panel rather than an error — which
reads as "this setup has no ports" rather than as a bug.

## The port TYPE is a `.cem` setting, per port (2026-08-24)

`EmSetup.PortKinds` beside `PortZ0s`, for the same reason: a layout is geometry. The same artwork can
be analysed with a gap in the middle of a trace in one setup and driven from its ends in another, and
neither should edit the drawing. The engine half is `src/Engine/Mom/RESOLVED.md`.

Three things about it differ from the flags around it, each deliberately:

- **It DOES enter `EmSnpProvenance.PortHash`**, unlike `AcceleratedSolve` and the core cap. Those pick
  how an answer is computed; this decides where the excitation is cut and whether the port is
  de-embedded at all, so an `.snp` written under one type is emphatically not current for the other.
  **But it is appended only when a port is non-default** — appending it unconditionally would change
  the hash of every all-edge port set, i.e. of every `.snp` this application has ever written, and
  report a one-time false staleness on files nothing has invalidated.
- **It DOES call `InvalidateMesh()`**, unlike the reference impedance in the same row. Z₀ is a
  renormalisation applied to the answer; the type changes which rooftops are driven, so a mesh report
  computed under the other type is about a different excitation.
- **`FlattenPortKinds` returns null for an all-`Edge` list, not just for an empty one.** The panel
  materialises one row per port whether or not the user touches anything, so a naive writer would put
  `["Edge","Edge"]` into every `.cem` that has ever been opened — exactly the byte-identity the
  omit-at-default rule exists to protect. Asserted directly.

## Every EM-run message respects the layout's own display unit (2026-08-15)

Owner request: "all messages from running an EM sim that reference distance/length need to respect
the units of the `.clay` file." Both kernels' notes were SI-only (kernel B's `SurfaceMesher.Eng`,
kernel A's raw `{v:G4} m`) regardless of what unit the layout itself is drawn and displayed in.

**The engine cannot know about mil/µm/DBU** — the UI firewall forbids `src/Engine` from referencing
`LayoutUnits` — so both kernels' length-bearing entry points (`SurfaceMesher.Mesh`,
`PlanarFeedExtension.Extend`, `PlanarSolve.Run` and its two public helpers, `BoundaryMesher.Mesh`,
`QuasiStaticKernel.Mesh`/`SolveDetailed`) take an optional `SurfaceMesher.PlanarLengthFormat`
(`delegate string PlanarLengthFormat(double metres)`) — a plain delegate, never a UI type, so the
firewall holds. `EmLengthFormat.For(displayUnit, dbuPerMicron)` builds the real one, on this side of
it, from `LayoutUnits.Format` + `LayoutUnits.Suffix`.

**`null` (every existing caller, including the CLI, which has no `.clay` at all) reproduces the
exact pre-2026-08-15 text — asserted, not merely arranged.** Kernel B's default is
`SurfaceMesher.Eng(v) + "m"` (SI engineering notation); kernel A's is its own pre-existing
`{v:G4} m` — the two were never the same convention, and giving them one shared fallback would have
been a silent format change for every existing kernel-A test. **This is why one bug already surfaced
and was fixed while wiring this in**:
`EmMeshOverlayTests.EveryNumberInTheReport_ReachesThePanelUnmodified` compared the panel's (now
formatted) notes against an un-formatted direct kernel call and read as "the panel modified the
engine's notes" — the fix was giving the reference computation the same formatter, not reverting the
panel. Any future "the panel's notes don't match a raw kernel call" report should ask this question
first.

**The two panel entry points (`BuildMesh`, kernel A's own Mesh button; `PreparePlanarMesh` /
`ComputePlanarMesh`, kernel B's) capture the display unit as PLAIN VALUES**
(`EmSetupEditorViewModel._pendingDisplayUnit`/`_pendingDbuPerMicron`, both set in `Refresh()` and
again in `PreparePlanarMesh()`) **rather than the live `LayoutView` reference** — `ComputePlanarMesh`
runs off the UI thread (see its own header for why: `Extract`/`Flatten` read the live, editable
`LayoutView` and must stay on the UI thread, but the mesher itself is poolable), and passing the
live view across that boundary would be exactly the data race that split already exists to avoid.
`EmRunService.RunPlanar`/kernel A's own solve path build the formatter directly from `source.View`
instead, since neither of those runs off-thread relative to `source`.

Gate: `tests/Ui.Tests/Em/EmLengthFormatTests.cs` (3 tests) — the mil round trip, a mil-displayed
layout's mesh note actually reading in mil, and the default-display-unit case matching a
formatter-supplied direct kernel call (not the bare 2-arg one — see the note above).

---

## An open `.cem` never re-read its layout — the port list was a snapshot from open time (2026-08-25)

Owner report: *"placed 3 ports in my .clay drawing, but only 2 ports show up in the .cem for the
file."*

**Nothing downstream was wrong, and that is the part worth remembering.** Driven directly against the
owner's own `NewPortTest.clay`, `EmPortExtraction.Extract` resolved all three ports, with correct
sides and notes; `EmSetupEditorViewModel.Refresh()` over the same live view built three `PortRows`;
and a Simulate would have run all three, because `EmRunService.RunPlanar` re-extracts from
`source.View.Shapes` at run time. Time spent auditing the extractor — the numbering, the
duplicate-name refusal, the multi-level refusal, the ambiguity threshold — found nothing, because
`Extract` is all-or-nothing by construction: **every path in its per-label loop either appends a port
or returns a refusal, so it cannot return a short list.** A short port list is therefore never an
extractor bug, and the next report of one should start where this one ended.

**What was actually wrong: `Refresh()` was called at exactly three moments** — when the `.cem`
document was opened, when a setting inside the panel was committed, and when Mesh was pressed. **No
call existed anywhere for "the referenced `.clay` changed".** Add a port with the panel already open
and it went on showing the port list, mesh summary and blocking reason from before. The port COUNT is
derived from the geometry precisely so a user never types it, which is exactly what makes a silently
stale one indistinguishable from the extractor having missed a port.

**`InvalidateMesh` had documented itself as "Called by the workspace when the referenced `.clay`
changes" since R-em-17, and no such call had ever existed.** A doc comment naming a caller is not
evidence the caller exists; this one had been wrong for as long as it had been written.

**The fix is one subscription, in `WorkspaceViewModel`:**
- `vm.Model.Changed += (_, _) => NotifyEmSetupsLayoutChanged(vm.CurrentLayoutPath);` at **both**
  `LayoutEditorViewModel` construction sites (`BuildLayoutSessionVm` and the scratch-layout one) —
  **not** in `RegisterLayoutSession`, which runs again for the same VM on Save-As and would
  double-subscribe. `CurrentLayoutPath` is read **live** for that same reason: a Save-As moves the
  session, and the `.cem` that cares is the one pointed at wherever it now lives.
- `NotifyEmSetupsLayoutChanged` **posts** at `DispatcherPriority.Background` and never works inline.
  `LayoutModel.NotifyChanged` raises `Changed` **while holding `RenderLock`**, and its own contract
  says every subscriber there is a cheap non-blocking invalidate — a flatten plus two extractions is
  neither. Posting also collapses a burst (a paste, a multi-shape delete) into one refresh, via a
  pending-path set keyed by absolute path.
- Both halves run: `InvalidateMesh()` (drop a report describing artwork that has since changed) then
  `Refresh()` (re-extract to replace it).
- `ResolveEmLayoutPath` was split out of `ResolveEmLayout` so "does this `.cem` point at THAT layout?"
  can be asked without loading geometry, and so the two cannot drift about what a `LayoutRef` means.

No feedback loop: `PushEmMeshToLayout`/`AdoptPortTypes` write layout *view-model* properties only and
never call `Model.NotifyChanged`.

Gate: `tests/Ui.Tests/Em/EmSetupLayoutStalenessTests.cs` (3) — a port added after the panel opened
appearing on the next `Refresh` (the mechanism the subscription relies on, and the half that was
already correct), `InvalidateMesh` dropping a stale report, and a comment-stripped source scan for the
subscription itself, since `WorkspaceViewModel` cannot be constructed headlessly.

### The same session's four follow-ups, all in the port surfaces

**A port off the metal was refused by a BOUNDING-BOX distance, which for a concave shape is
unbounded.** Owner report: *"if I move Port 1 or Port 3 off the metal, I get no live update for bad
port (but I do get a warning for port 2)."* `NearestPolygon`'s off-metal test measured the distance
to the polygon's bounding box and compared it against half that box's smaller side. **A tee's box
spans its own empty notch, so a port dragged into the notch measured exactly ZERO** and was accepted
however far it was from any copper — measured on the reporter's own file, a label 1.2 mm from the
nearest metal resolved silently. That is the asymmetry in the report: port 2 sat at the far end,
where moving it leaves the box at once, while ports 1 and 3 flank the notch and could never leave
it. Both halves are now local to the conductor — a true point-to-BOUNDARY distance, and a reach of
`Area / Perimeter`, which for a strip of width `w` and length `L ≫ w` is `w/2`. **"Within half a
trace width of the metal" is a sentence about the conductor; "within half the smaller side of
everything this polygon spans" was a sentence about the drawing** — the same class of mistake
`AmbiguityFraction`'s own note records having already made once.

**The panel drew TWO port controls and a reader only ever saw one.** Owner report: *"I don't see a
Port 3 Z₀ option in the .cem editor."* The Ports group showed a fixed grid captioned *Port 1 Z₀* and
*Port 2 Z₀* **and**, beneath it, a row per port. The captioned pair reads as the port list, stops at
two, and gives no reason to take the unlabelled rows below it as the same thing continued. They are
also one value: a row renders `ResolvePortZ0`, which falls back to that pair until overridden.
`ShowPortList`'s own note already said the two must never appear together — that held while the list
was cross-section-only and needed three ports to appear, and **the planar kernel shows the list
unconditionally, which the rule was never extended to cover**. `ShowNearFarPortZ0` is now its exact
negation and the XAML binds to it.

**An internal port reverted to edge rendering for the whole of a drag, and its selection box sat in
the wrong place.** Two symptoms, one root: the renderer asked the shape it was about to DRAW.
`MarkKindOf` matches a label's exact DBU anchor, and a live move drag renders a translated CLONE
while the model stays untouched until commit (R-L1c-3) — so from the first pixel no mark matched and
every internal port fell through to `Edge`. It now asks `original`, which is the shape the `.cem`'s
marks were computed from; **moving a port cannot retype it, so this is the correct question rather
than a workaround for the coordinate key.** Separately, `MeasureLabelWorldBbox` mirrored
`DrawLabelText`'s ROTATION table and nothing else, so once an internal port's text began being drawn
CENTRED the outline went on being measured from a left-anchored baseline — out by half the advance
width and by the baseline lift. It takes a `centred` flag now and applies both of that method's own
offsets. **The two must move together; this is the second time a box drawn from a re-derived idea of
where the text is has drifted from where it actually is.**

**A floated `.cem` window is capped at 700 logical units wide and given 200 units of extra height**
(`CrfHostWindow.EmSetupFloatMaxWidth` / `EmSetupFloatExtraHeight`), applied in `OnOpened`. **Not in `CircuitRfDockFactory.CreateWindowFrom`, and the ordering is the
reason**: a width set during window creation is not final — `DockWindowOptions.ApplyTo` assigns
geometry unconditionally, the same overwrite the `OwnerMode` override beside it already documents
having to work around, and Dock's drag tear-off supplies the dragged tab's bounds through exactly
that path. `OnOpened` is the last point in the sequence this codebase owns. It is a CAP, not an
assignment — a narrower float is left alone. **The height bonus rides inside that same guard, and
that is what stops it ratcheting**: a floating window's geometry is captured into the `.cws`, so an
unconditional `Height += 200` would restore 200 taller each launch and add 200 again, without bound;
gating it on the width cap actually firing leaves a restored float exactly as the user last sized it
and adjusts a fresh tear-off exactly once. It is clamped to the screen's working height, converted
from device pixels by the screen's own scaling. The panel's own floor is the stackup row
(`76,*,90,190,*`, ~380 units of FIXED columns), so at 700 its two stretching columns get ~150 each
and trim longer conductor names, which is the accepted trade.

**Also this session, from the same reports:** each port row's name moved onto the same grid row as
its impedance (it had been vertically centred against a StackPanel of *[impedance, type, error]*, so
on a planar row it came to rest between two controls and labelled neither), and the impedance box
gained a `Ω` beside it — it was the only numeric field in the panel with no unit, so "50" beside a
port name read as a port number more readily than as ohms.

Gates: the off-metal and panel-duplication halves are in
`tests/Ui.Tests/Em/EmSetupLayoutStalenessTests.cs` (14 tests total with the staleness ones above);
the drag/selection halves are `tests/Ui.Tests/Em/InternalPortDragRenderTests.cs` (5), whose oracle is
a **differential render** — a port dragged to 12 mm must produce the same frame as one stored at
12 mm — plus a companion asserting the gap mark and an edge port's mark are not the same picture, so
the first test cannot pass on a renderer that draws the same thing either way. The window cap is
`tests/Ui.Tests/EmSetupFloatWidthTests.cs` (6), source-scanned because `CrfHostWindow` is an Avalonia
`Window` and cannot be constructed headlessly.

### One bad port no longer erases the others — and three more from the same afternoon

**`EmPortExtraction` now reports EVERY numbered label, resolved or not** (`EmPortExtractionResult
.Rows`, one `EmPortRow` per label). Owner request: *"if any ports aren't touching metal, the .cem
editor will not list the ports. I'd like to still see them listed, even if they are not on a
conductor (and the .cem gives a warning)."* The panel built its rows from `.Ports`, which is empty on
any refusal — so **one bad label emptied the list at exactly the moment the user was trying to find
the bad label.** The per-label loop records each problem and carries on instead of returning at the
first; `firstProblem` becomes the refusal, so **every existing refusal-wording gate still reads the
same sentence**. **`.Ports` is still all-or-nothing and the run is still blocked** — a port that is
not on metal has no location the mesher could honestly place it at, and a solve over the ports that
happened to resolve is a complete, plausible answer for a structure nobody drew. `EmPortZ0Row` gained
`Problem`, kept **separate from `Error`** because the two have different lifetimes: a successful
impedance commit clears `Error`, and must not thereby erase "this port is not on any conductor".

**A refusal was also silently retyping every internal port in the drawing.** Owner report: *"P3
renders as edge port (even though it is a gap port) when P2 is not on a conductor."* Same cause —
`InternalPortMarkAnchors` was built from `.Ports`. It comes from `.Rows` now, with the KIND read from
the `.cem` (`ResolvePortKind`) rather than from the resolved port, because that is where it lives and
an unresolved row has no port to ask. For a resolved row the two are the same value by construction.

**The mouse-up flash.** Owner report: *"after drag of the port, the port rendering glitches
momentarily on the mouse up."* Two causes, either sufficient. `LayoutEditorViewModel`'s
`Model.Changed` handler cleared `InternalPortMarks` on **every** mutation — right for the mesh
overlay beside it, which is derived from the geometry, and **wrong for a port TYPE, which lives in
the `.cem` and cannot be changed by moving a label**. And the marks are keyed by ANCHOR, which the
move had just changed, so even an uncleared list stopped matching. The window stayed open until the
`.cem` republished — a Background-priority refresh, i.e. one or more visible frames. Now: the clear
is gated on `info.Kind != Updated` (an add or delete can renumber the ports, so those really are
stale); `CommitMoveDrag` shifts the marks by the move delta **before** `Execute`; and the `Updated`
branch **prunes** any mark with no port label under it, which is what makes an UNDO — the same
`Updated` change with no shift beside it — drop the stranded mark instead of leaving it on empty
space.

**Two ports 0.381 mm apart could not both be picked.** Owner report: *"the port2 hitbox is
interfering with the port3, so I currently can't drag select p3, even though port 2 is far from port
3."* A port's pick square is deliberately generous — 2.52 mm across for a two-character label at the
1.016 mm height in that file, because a user aims at the marker's bar and arrow rather than at a
glyph — so each anchor sat deep inside the other's box. **`LayoutHitTest.HitStack`'s
smaller-area-wins term cannot separate two labels, because `LayoutGeometry.BboxOf` of a label is a
zero-area POINT**; both scored 0 and the sort fell through to ascending list index, so the port
written earlier in the `.clay` won every overlapping pick and the later one was unreachable. A
distance term now sits between area and index, **recorded only for zero-area shapes** so ordering
between real geometry is untouched — two overlapping equal-area rectangles still tie-break by index,
which is asserted rather than assumed.

**And the pick square itself was twice the size it was meant to be.** Follow-up report minutes later:
*"the hitbox for the ports now seems too big — I am always selecting ports almost everywhere I click
in the layout."* `LabelHitBbox` read `half = Max(w, h)`, and **w/h are FULL extents** — an ordinary
label of the same text occupies w × h — so the 2026-08-09 change that made a port's region SYMMETRIC
had also doubled it in each direction, four times the area. On the reporter's file that is a 2.52 mm
square per port over a 3.5 × 2.2 mm structure, and since a label's bbox is zero-area, `HitStack`
ranks it ahead of any real geometry on the same layer — so the metal underneath was unreachable
rather than merely second. It is `Max(w, h) / 2` now: the square circumscribes the glyph instead of
using the glyph as its radius, which is what "symmetric about the anchor" should have meant. **The
click tolerance the caller adds on top is untouched**, which is the part of the 2026-08-09 report that
was really about reach.

**`LayoutPortPickSymmetryTests` was UPDATED, not loosened, and its header now records the
supersession.** That file encodes an owner decision — *"the farther distance is working good right
now for UX"* — which was a parenthetical about WHICH of two reaches to keep, not a request to double
it. The symmetry half (what the report actually asked for) is unchanged and still asserted; the size
assertion moved and says why; and `ThePortIsGrabbableFromBehind` now probes at a FRACTION of the
measured reach so it tests the property rather than re-pinning the size. One case in
`LayoutPortDragSnapTests` pressed 400 DBU off a port whose region is now 310, and its offset moved
with a note — the property it gates (the ANCHOR lands on the target, not the press point) is
unchanged.

**And the highlight was a different rectangle from the hitbox entirely.** Owner report: *"the hitbox
of the port does not match with the select highlight rendering."* Measured on a two-character port at
a 1.016 mm height: the highlight ran **x 63,500..1,217,414 / y −15,875..746,125** — the tight GLYPH
box from `MeasureLabelWorldBbox`'s real font metrics, up and to the right of the anchor — while the
pick region is the square **−629,920..+629,920** on both axes. **They share one corner.** So a click
below-left of the text selected the port and drew a box over the text, up and right of where the
click landed. `LayoutHitTest.PortPickBbox` is now the single source and the renderer calls it;
`BuildOutlinePathForSelection` gained a `LabelShape { IsPort: true }` case ahead of the general label
one, and `MeasureLabelWorldBbox` keeps the glyph measurement for ordinary labels, which is what an
annotation's outline should be.

**This SUBSUMES the `centred` flag added earlier the same day** for "the internal port highlight
select box is rendered in the wrong spot" — that plumbed `DrawLabelText`'s centring offsets into the
glyph measurement so an internal port's box would follow its centred text. Taking the outline from
the pick region removes the question instead of answering it: **the pick region is centred on the
anchor for every port, whatever its type**, so the outline no longer depends on the port's type at
all. The parameter, its thread through `DrawSelectionOutlines`, and that method's `InternalPortMarks`
argument were all removed rather than left as dead plumbing.

**The deliberate consequence, stated because it is visible:** an EDGE port's highlight no longer wraps
its text. The glyphs run inward from the anchor while the region is centred on it, so the box marks
the port and the name sits beside it. A region that both stayed centred (the 2026-08-09 report) and
enclosed off-centre glyphs would need to be twice this size — which is exactly the "too big" it was
halved from hours earlier. Centring every port's text would satisfy all three and is the open
alternative; it was not done because an edge port's text running inward is itself a documented choice.

Gates: the port-listing half is in `EmSetupLayoutStalenessTests` (19 tests now); the drag, mouse-up,
undo and outline halves in `InternalPortDragRenderTests` (9) — the outline one asserts IDENTITY
against `PortPickBbox` rather than comparing two computed rectangles, because two rectangles that
happen to agree today are precisely what produced this bug; the pick order in
`tests/Ui.Tests/Em/PortPickNearestAnchorTests.cs` (4), whose fixture is the reporter's own
coordinates and label height; the pick SIZE in `LayoutPortPickSymmetryTests` as above.

### The port's hitbox and highlight are now the MARK, and nothing else

Four owner reports in a row, each rejecting the previous answer, ending at: *"Make the
hitbox/highlight the arrow boundary box for edge and internal ports and make it the gap boundary
rendering for the gap port. Keep it simple."* The intermediate attempts are recorded because each was
refuted by measurement rather than by taste:

1. *"the hitbox does not match the select highlight"* — they were two independently-derived
   rectangles sharing one corner (measured: highlight x 63,500..1,217,414 from real font metrics;
   pick square −629,920..+629,920). Unified on the anchor square.
2. *"the highlight box is not placed over the port text name"* — an edge port's glyphs run out of a
   centred square. Centring every port's name put them back inside it.
3. *"I want the hitbox+highlight to be only over the port text"* — then, on seeing it, the request
   above.

**The region is `LayoutPortDirection.MarkerBbox` and there is one of it.** An EDGE port's box is its
plane bar and arrow, **at the conductor end** — so a port is grabbed and highlighted at its arrow and
**deliberately NOT at its name**, which is the visible cost of the request and is asserted as a
negative so a later reader does not read it as a regression. A GAP port's is its break, and an
INTERNAL-to-ground port's is its ring; **both of those are drawn at the label's anchor, so the ring
goes with the gap rather than with the arrow** — contrary to the request's wording, and following
what `DrawInternalPortMarker` actually centres on (`label.X/Y`, or the meshed via footprint).

**`LayoutSpatialIndex` stops culling port labels, and that is load-bearing.** `ConservativeBboxOf`
bounded a label by `(chars+1) × height` about its ANCHOR, and an edge port's mark is an **unbounded**
distance away — knowable only from the technology and the artwork, neither of which that
framework-free file has. Measured before the change: a press directly on a port's arrow selected the
trace underneath, because the port was pruned before its exact test ran. Over-inclusion is the safe
direction by that method's own contract, and the cost is bounded by construction — a port is one EM
port, so a layout has a handful. It also fixes a rarer bug the other way: a port whose anchor was
off-screen while its plane was on-screen had its marker culled and not drawn.

**Two live limitations, recorded rather than papered over:**
- **Two edge ports naming the SAME conductor end share one plane and therefore one region**, and no
  distance rule can separate them; overlap cycling is what reaches the second. The reporter's own
  P2/P3 were exactly this pair, which is why `PortPickNearestAnchorTests`' fixture had to move to
  ports with no conductor at all — the nearest-anchor ordering rule still decides there, and that is
  the case it now gates.
- The port marker's geometry constants and `ArrowGeometry` moved from `LayoutRenderer` into
  `LayoutPortDirection` so the framework-free hit test can reach them; the renderer's own constants
  are now aliases of those, so **there is exactly one literal per number** and the drawing is
  untouched. Two copies of a marker's size, one in the renderer and one in the hit test, is precisely
  the drift that produced report 1.

Three earlier gates were UPDATED, not loosened, each carrying its own supersession note:
`LayoutPortPickSymmetryTests` (its symmetry claim is now about the no-conductor FALLBACK square only),
`PortPickNearestAnchorTests` (fixture moved as above), and `InternalPortDragRenderTests`.

### Related, and NOT a bug: an edge port's marker is drawn at the conductor END, not at its label

Same session, owner report: *"I can't place an internal port on the metal… the port always snaps to
an edge in the layout."* **Nothing snaps.** `LayoutSnapQuery.FindCandidates` returns **zero**
candidates at the centre of the owner's tee, even at a 254 µm tolerance (100× the real 8-device-pixel
one), so a port drags to mid-metal freely. What moves back is the *marker*: `DrawPortMarker` draws the
reference-plane bar and arrow at `hint.PlaneX/PlaneY` — the conductor end — with a leader line from
the label's anchor, which is deliberate (R-res-5: "where is the reference plane" had no readable
answer when the bar was drawn wherever the user clicked). Measured on the owner's file: dragging P3
from the stub end to the middle of the stub leaves its plane at `(-63500, -1701800)` either way.

An edge port's marker belongs at the end **because it is an edge port**; the port TYPE lives in the
`.cem` (`ThePortTypeComesFromTheCem_NotFromTheLabel`), so mid-metal artwork cannot and must not
retype it. The two reports compound: before the staleness fix above, a port added in the layout had
**no row in the panel to set its type on**, so the type could not be changed and the marker could
never stop being an edge one.
