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
