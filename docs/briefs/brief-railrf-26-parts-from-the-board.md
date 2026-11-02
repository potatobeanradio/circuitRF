# Brief 26 — the parts table has no producer: nothing in circuitRF ever makes a part row

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail26-n` · **Phase:** feature, no model change
**Area:** `src/Design/RailRf/` (new `RailPartDiscovery.cs`, `RailPart.cs`),
`src/Ui/RailRf/RailRfViewModel.Parts.cs`, `src/Ui/Views/RailRf/RailRfWindow.axaml`, `src/Cli/Rail.cs`
**Depends on:** 7 (the window), 11 (part models), 13 (the mounting loop),
[authored board 1](brief-authored-board-1-layout-pads.md) (the pads) · **Blocks:** nothing · **Followed by:** [27](brief-railrf-27-gerber-set-to-a-curve.md), which closes §10
**Found by:** a field report, 2026-09-21 — a Gerber set imported into a workspace, the designer's own
footprints then placed by hand in the `.clay`, the `.clay` reopened in railRF days later, and none of
the placed parts in the table under the board.

---

## 0. What this brief is about, and what it is not

It is not that the parts table lost some rows. **It is that the parts table has never had a
producer.**

`RailRfViewModel.RebuildParts` lists `rail.Parts` — the part rows the `.crail` carries — and enriches
them from the BOM, the placement file and the part library. That much works and is gated. What is
missing is every route by which a row gets into `rail.Parts` in the first place:

- **`new RailPart` appears nowhere in `src/`** except `RailDocumentIo`, reading one back off a file.
- **`RailPartOrigin.Bom` is never assigned** — by anything — although `RailPart.cs`'s own header
  states the behaviour as fact: *"A rail with an imported BOM carries rows pre-filled from it
  (Origin = Bom, exactly as RailAggressor does)."* No it does not.
- **The window has no add-part gesture at all.** The Parts card is a header row and a list; there is
  no Add, no Remove, no context menu, and `RailRfViewModel.Menu.cs` names no part command. Mount and
  unmount (brief 23) act on rows that are already there.
- **`PdnLayoutPads`' own header lists the four things a drawn board degraded in** — no pick list,
  coordinate-only anchors, typed mounting loops, **"and no parts-table row"**. Authored-board 1 fixed
  the first three by producing the pads. Nothing consumes those pads to make a row.

So the only way a part has ever appeared in railRF is somebody writing the JSON — which is how the
shipped Power Rail example got its fourteen (`Origin: "Typed"`, all of them, hand-authored by
[footprint 5](brief-footprint-5-power-rail-example.md)). §2.3 step 3 of `railrf.md` says *"Confirm
the parts. A table: refdes, part number, value, model source, derated value, position, computed
mounting inductance."* There is nothing to confirm, because nothing produced anything.

**The reported board is the ordinary case, not an exotic one:** 55 placed footprint instances, each
carrying a designator, each pointing at a generated land pattern. Every one of them is invisible to
the parts table, and the pane it should fill shows column headings over nothing with no sentence
anywhere saying why.

---

## 1. `R-rail26-1` — the making of a row lives in `src/Design`, not in the view model

A new `src/Design/RailRf/RailPartDiscovery.cs`, framework-free, side-effect-free, posting nothing —
`RailArtwork`'s own terms, and for `RailArtwork`'s own reason: `circuitrf rail` and the window must
not come to different conclusions about one board. The window turns the answer into an offer; the
verb prints it (R-rail26-7).

It **returns candidate rows and never writes them onto the document.** A function that edited the
document would be a second writer beside the view model's undo stack.

### `R-rail26-1a` — its inputs are what the window and the verb both already hold

`IReadOnlyList<PdnPad>` (from `RailArtwork.PadsFor`), the `RailSpec`, the rail's own region set from
the last extraction (`PdnRailRegionSet`), the technology, the flattened shapes, and the `BomTable`
where one was read. **Nothing new is computed about the board** — this brief adds no geometry pass.

---

## 2. `R-rail26-2` — what "a part on this rail" is, stated so it cannot quietly become "a part"

This is the whole correctness question and it is the reason this is a brief rather than a fix.

**A candidate is a TWO-terminal part with one pad on the rail's own copper and one pad on the
reference.** That is decoupling, which is what `RailPart`'s shunt row models (§2.2, brief 25's
`RailPartConnection.Shunt`), and nothing else may become one:

| what the board holds | what it is | what discovery does |
|---|---|---|
| 2 pads, rail + reference | decoupling | **offered** |
| 2 pads, rail + rail | a series element (brief 25) | **reported**, never offered as shunt |
| 2 pads, one on the rail, one elsewhere | not on this rail's return path | reported |
| >2 pads touching the rail | an IC, a connector, a regulator — a LOAD | reported, never a capacitor |
| no pad on the rail | not this rail's business | silent |

**The failure this table exists to prevent** is a series resistor, a ferrite or a load turning into a
shunt capacitor with a library-defaulted ESR. The curve that comes out of that is smooth, plausible
and wrong, and nothing on the window would say so — which is `RailPart`'s own standing rule (a
defaulted number inside the sanity band is indistinguishable from a measured one).

### `R-rail26-2a` — the predicate is REGION MEMBERSHIP, and the mounting loop is not part of it

A pad is *on the rail* when it lands in one of `PdnRailRegionSet.Power`'s islands, and *on the
reference* when it lands in one of `Reference`'s. **That is a galvanic test and not a same-layer
one**, which is the whole reason it must be the region walk: on a two-layer board both pads of a
decoupling capacitor sit on the top, and the ground-side one reaches the plane through its own
stitching via. A test that asked "is this pad on the reference LAYER" would find no decoupling on
any such board at all.

**Read the regions off the last extraction, never walk them again** — `RailRfViewModel.SeriesRegions`
already states that rule and the reason: a second connectivity model beside the one the DC answer is
built from is two answers to one question.

**`PdnMountingLoopExtractor` is NOT the predicate**, and this is worth being explicit about because
it is the obvious shortcut. Its header reads *"a part with no pad on the rail, no pad on the
reference, or no via within reach of either"* — the first two clauses are this predicate, the third
is not. A capacitor whose return via is too far to price still IS decoupling on this rail; it is a
row whose mounting inductance is unresolved, which `RailMountingBasis` and the parts table already
have a spelling for. Using the loop's verdict as the filter would silently drop real parts from the
bank, which is the one outcome this feature exists to prevent.

### `R-rail26-2b` — it needs the regions, so it lands after the first solve, not at import

Both halves of the predicate come from the extraction, so there is nothing to discover until the
rail has been solved once. **In practice that is not an extra step**: confirming the reference calls
`QueueResolve`, Fast is the default, and the regions exist by the time the offer would be drawn. But
the offer must be derived from whether the regions are THERE rather than from whether the reference
was confirmed — a rail whose run refused has no regions, and an offer computed from the confirmation
alone would come up empty with nothing saying why.

**This is also the order §2.3's own steps are in**: load the board, identify the rail and confirm its
reference, *then* confirm the parts.

---

## 3. `R-rail26-3` — the part NUMBER comes from the BOM or from nowhere

- A BOM row for that refdes → `PartNumber` from it, `Origin = RailPartOrigin.Bom`. **This is the
  behaviour `RailPart.cs`'s header already claims and the enum value already exists for.**
- More than one BOM row for the refdes → the part number is left EMPTY and the row is unresolved.
  `RebuildParts` already refuses to pick between approved manufacturers (R-rail2-14 item 1); the
  discovery must not undo that by choosing here.
- No BOM → empty part number, `Origin = RailPartOrigin.Artwork` (new member, additive; absent from a
  `.crail` still reads `Typed`).

**Nothing is ever derived from the footprint.** An 0402 land pattern is a case size; it is not a
capacitance, an ESR or a dielectric class, and a row invented from one would be the defaulted number
this whole tool marks and counts everywhere else. A row with no part number is listed AS unresolved,
which is a state the parts table already renders and §2.3 step 3 explicitly asks for.

### `R-rail26-3a` — the mounting loop rides along

`ComputedMounting` already fills it for the rows a rail has; a discovered row gets it by the same
call, on the same terms (computed is a default, typed wins, `RailMountingBasis` says which).

---

## 4. `R-rail26-4` — it is an OFFER, and the counts are part of the offer

The rows land in a document that gets saved, so this follows `RecognisedAggressors` and
`RailRegulatorOffer` exactly: a line in the Parts pane with a button, not an edit that happens by
itself.

> **24 two-terminal parts sit between this rail and its reference and are not in this document.**
> Add them · *(12 more parts touch this rail and are not decoupling: 1 spans the rail twice, 11 have
> more than two pads.)*

**The second half is not decoration.** A designer who is told 24 were added and not that 12 were
skipped has no way to know whether the bulk capacitor they are looking for is one of the 12.

### `R-rail26-4a` — idempotent, by refdes

A refdes the rail already carries is never offered again; adding twice adds nothing. The offer is
recomputed on the same funnel `RebuildParts` runs on.

### `R-rail26-4b` — one undo entry

The batch is one edit, like `SetPartsMounted`: *"unmount these four and re-run"* has already settled
that a batch is the real gesture, and 24 separate undo entries would be 24 presses to take back one
button.

---

## 5. `R-rail26-5` — the empty pane says where rows come from

Independent of everything above, and true whenever the table is empty — before a reference is
confirmed, on a document with no artwork, on a rail whose parts nobody added. The card today is
column headings over nothing.

One sentence in the empty list, naming the state it is in: *no board yet*, *reference not confirmed
yet*, *no two-terminal part found between this rail and its reference*, or *this rail has no part
rows — add the parts railRF found, or type them*. **This is the part of the report that is a defect
on its own**: a pane that is empty and silent reads as a broken pane, and that is what was reported.

---

## 6. `R-rail26-6` — a discovered row is a row like any other

No second kind of part. Once added it is `rail.Parts[i]`, it saves, it mounts and unmounts, it
resolves against the library, it is compared in A/B, and it is deletable. `Origin` is what the row
says about where it came from, exactly as it does for a BOM-filled aggressor — and a user who edits
its part number has adopted it, which the existing `Origin` display already handles.

---

## 7. `R-rail26-7` — the verb reports; it does not write

`circuitrf rail` prints what discovery found and what it skipped, as a note beside the run's other
notes, and **writes no rows into the `.crail`**. That is `Authoring.cs`' standing rule — *"there are
deliberately no per-primitive edit verbs: once a document exists, the way to change it is to WRITE
it"* — and it is what lets an out-of-process agent see the same 24 parts the window is offering and
write them itself.

---

## 8. Tests — `tests/Ui.Tests/RailRf/RailPartDiscoveryTests.cs`

One per claim, on a synthetic board built the way `PdnMeshExtractorTests`' fixtures are built.

1. **The classification table of R-rail26-2, in one fixture.** A board carrying: two caps rail →
   reference, one resistor rail → rail, one cap reference → reference, one three-pad regulator with a
   pad on the rail. **Offered: exactly the two caps.** The other three come back in the skipped list,
   each with its own reason. This is the test that says a load never becomes a capacitor.
2. **The BOM fills the part number and marks the origin** — and **two BOM rows for one refdes leave
   it empty** rather than choosing (R-rail26-3).
3. **Nothing is derived from the footprint.** A cap whose BOM row is missing comes back with an
   empty part number and renders unresolved — not with a capacitance guessed from `0402`.
4. **Adding is idempotent** (R-rail26-4a) and **one undo takes the batch back** (R-rail26-4b).
5. **The empty pane's sentence changes with the state** (R-rail26-5) — no board, unconfirmed
   reference, nothing found, nothing added.
6. **The verb and the window agree** on one document: the same refdeses, offered in the window and
   printed by `circuitrf rail` (R-rail26-1, R-rail26-7).
7. **The shipped example is unchanged.** Its fourteen typed rows stay fourteen, nothing is offered
   twice, and its whole `DataSet` compares bit for bit against before this brief.

---

## 9. Scope

- **Shunt decoupling only.** A series candidate is reported, never offered — brief 25 owns that row
  and its terminals are a user's statement about the topology.
- **No loads and no sources.** §2.3 step 4 stays a user act: a load is a refdes AND a current, and
  the current is not on the board.
- **No net-name inference.** Discovery reads copper connectivity, which is what the rail already is;
  it names no net the board did not.
- **No new geometry pass.** Everything here is read off pads and regions the run already computes.
- **The document format gains one enum member** (`RailPartOrigin.Artwork`) and nothing else.

---

## 10. What this does NOT close, on the board it came from

Stated because "the parts are in the table" and "the designer has a curve" are not the same thing,
and the gap between them is three more steps that are somebody's to take.
**[Brief 27](brief-railrf-27-gerber-set-to-a-curve.md) closes all three** and carries the end-to-end
gate for the whole path; what follows is why each one is not this brief's to close:

1. **The reference layer still has to exist as a drawing layer.** That board's inner ground plane
   came in from Gerber as a `drawing` layer belonging to no stackup conductor, so there is no
   reference to confirm, no extraction, no regions, and therefore nothing for this brief to
   discover. One attachment in the technology editor fixes it; the combo now says so
   (`src/Ui/RESOLVED.md`, 2026-09-21). **Discovery is downstream of it.**
2. **The rail has to be the rail.** With no board netlist and no schematic the only route to a rail
   is clicking a pour, and clicking the ground pour makes a "rail" whose copper IS the reference —
   on which no part bridges anything and this brief correctly finds nothing.
3. **A discovered row still carries no capacitance.** R-rail26-3 refuses to invent one from a land
   pattern, so with no BOM every row is listed AS unresolved and the |Z| curve has no decoupling in
   it. That is the honest state and the table says so — but the path out of it is brief 24's part
   library editor and its seeding, not this brief. **Anyone reading "24 parts added" as "the answer
   now includes 24 capacitors" is reading it wrong**, which is why the unresolved count is already
   on the status strip.

---

**On completion:** record findings in `src/Design/RESOLVED.md` (discovery) and `src/Ui/RESOLVED.md`
(the offer and the empty pane). **Never write findings into a CLAUDE.md.** Two comments become false
when this lands and must be corrected in the same change: `RailPart.cs`'s header, which describes
BOM pre-filling as existing behaviour, and `PdnLayoutPads.cs`' header, which lists "no parts-table
row" among four degradations it fixed three of.
