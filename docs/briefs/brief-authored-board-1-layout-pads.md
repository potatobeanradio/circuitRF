# Brief 1 — the pads are on the board already

**Series:** [authored board](brief-authored-board-0-overview.md) · **Tag:** `R-ab1-n` · **Phase:** P1
**Area:** `src/Design/Layout/Pdn/PdnLayoutPads.cs` (new), `PdnAttachments.cs`,
`src/Design/RailRf/RailArtwork.cs`, `src/Ui/Views/RailRf/RailRfWindow.Open.cs`,
`RailRfWindow.Import.cs`, `src/Ui/RailRf/RailRfViewModel.Open.cs`, `src/Cli/Rail.cs`
**Depends on:** [footprint 3](brief-footprint-3-update-layout.md),
[footprint 4b](brief-footprint-4b-designators.md) · **Blocks:** briefs 2, 3

---

## 0. What this brief delivers

A `.clay` the user drew resolves its own pads, so `U1.VDD` means something, mounting inductance is
computed from geometry instead of typed, and `circuitrf rail board.clay --load U1.VDD=120mA` runs.

One new file, two existing functions, and a rule about where a pad came from.

```
LayoutView.Instances  ──  DisplayRefDes  ─────────────────┐
        │                                                 ├──►  PdnPad
        └─ CellPins.Resolve(subView, tech)  ──  pin name ──┤
                    │                                      │
                    └─ LayoutInstanceTransform.TransformPoint  ──  X, Y
```

Nets are **out of scope here** and arrive in brief 2. This brief produces pads whose `Net` is null,
which is already a representable state (`PdnPad.Net` is `string?`) and is exactly what a board with
placement and no netlist produces today.

---

## 1. `R-ab1-1` — `PdnLayoutPads`, beside `PdnBoardPads` and shaped like it

A new file `src/Design/Layout/Pdn/PdnLayoutPads.cs`, with the same two entry points and the same
total, side-effect-free, framework-free character:

```csharp
public static IReadOnlyList<PdnPad>      PadsOf(LayoutView view, string clayPath, Technology? tech, ...)
public static IReadOnlyList<PdnNetPoint> NetPointsOf(...)
```

**`R-ab1-1a`** It walks `view.Instances` — the ROOT's own placements, not a recursive descent.
That is `LayoutDesignFlatten`'s own rule for designators (R-fp4b-4b) and it is right for the same
reason: a pad belongs to the part that was placed on the board, and a land pattern nested three cells
deep inside a module is that module's internal business until somebody places the module.

**`R-ab1-1b`** Per instance: `DisplayRefDes` (`LayoutModel.cs:875`), and **an instance with none
contributes nothing**. Null there means *this placement has no identity to draw* — R-fp4b-8c is
explicit that it must not be handed a fabricated one, and a pad keyed on a fabricated refdes is worse
here than no pad at all, because an anchor would then resolve to it.

**`R-ab1-1c`** Per instance: `CellPins.Resolve(res.View, tech)` for the resolved sub-cell, through
`CellLayoutResolver.Resolve(inst.CellRef, layoutDir)` — the same resolution `LayoutDesignFlatten`
performs at `:76`. An instance that does not resolve contributes nothing and is **reported**, reusing
the sentence the flatten already emits rather than writing a second one.

**`R-ab1-1d`** Per pin: `LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, col)` over the
instance's `Rows` × `Cols`, which is `LayoutRenderer.Instances.cs:1352` exactly. **An array
placement produces one pad per pin per cell**, and the refdes is the same on all of them — which is
correct and worth stating, because a via fence placed as a 1×N array is the shape that makes it look
wrong.

**`R-ab1-1e`** `NetPointsOf` emits a point per pad that carries a net, plus **every `ViaShape` in the
root's own shapes that carries one**. `PdnBoardPads.NetPointsOf`'s own note says why vias are
included and not an oversight: a stitching via is frequently the only thing standing on an
inner-layer pour, and a net point is how `PdnRailRegions` learns that a pour it reached is the rail's.
The root's vias are in `view.Shapes` and need no transform.

---

## 2. `R-ab1-2` — a pad says where it came from

**`R-ab1-2a`** `PdnPad` gains `PdnPadSource Source` — `BoardNetlist` or `Artwork`. `PdnBoardPads`
stamps the first, `PdnLayoutPads` the second.

**`R-ab1-2b`** It is a **required** positional field on the record, not an optional init with a
default. `PdnPad` is a `readonly record struct` with five positional members today
(`PdnAttachments.cs:41`) and adding a sixth breaks every construction site — which is the point: four
sites and the fixtures must each state which kind of knowledge they are handing over, and a default
would let a new one drift in unmarked.

**`R-ab1-2c`** Every report that names a pad says which. §1b of the overview is the reason: a netlist
can disagree with the board and a projection of the board cannot, so *"computed from the artwork"* and
*"stated by the board netlist"* are different claims and a reader is entitled to know which one they
are reading. The sites are `PdnMountingLoop`'s basis string, `RailProvenance`'s banner, and the
`--json` run report.

**`R-ab1-2d`** `RailMountingBasis.ComputedFromGeometry` is now reachable from an authored board and
that is the headline. It needs no new member — the existing one finally has a producer.

---

## 3. `R-ab1-3` — which wins when a board has both

A `.crail` can name an `.ipc` **and** point at an artwork cell full of instances. The Power Rail
example will be exactly this once footprint 5 lands.

**`R-ab1-3a`** **The board netlist wins, per refdes.** A netlist is written by the tool that made the
board and is evidence about it; artwork-derived pads are circuitRF's own reading of the same board.
Where the netlist speaks, it is the statement of record.

**`R-ab1-3b`** **Per refdes, not per board.** A netlist naming eleven of thirteen parts contributes
eleven, and the artwork contributes the other two. Whole-file precedence would throw away a correct
pad because a different part was missing from a different file — and the two-missing-parts case is
the ordinary one on a board somebody edited after exporting.

**`R-ab1-3c`** A refused netlist contributes **nothing**, and the artwork then supplies everything.
`BoardNetlist.Refusal` non-null means nothing was read and nothing may be used — the contract that
type states, kept intact.

**`R-ab1-3d`** Where both name a refdes and they **disagree about position**, that is brief 2
R-ab2-5's report. This brief does not resolve it, does not warn on it and does not rank it; it takes
the netlist per R-ab1-3a and moves on.

---

## 4. `R-ab1-4` — which pad is which pin

The overview's §1c. Footprint R-fp3-5 guarantees pad count equals port count and says nothing about
order. The join rule, stated here once:

**`R-ab1-4a`** **By NAME first.** A `LayoutPin.Name` that matches a port name of the component the
instance's `SchematicId` names is that port's pad. Comparison is ordinal, case-insensitive — the
comparison `PdnMountingLoop` already uses for refdes and net.

**`R-ab1-4b`** **By INDEX where no name matches, and only when every name fails.** Pad *i* is port
*i*, which is safe precisely because R-fp3-5 has already refused any instance where the counts
differ. A generated chip land — pins `"1"` and `"2"`, ports unnamed — takes this branch, and it is
the common case.

**`R-ab1-4c`** **A PARTIAL name match is a refusal, never a fill-in.** Two pins named `A` and `K`
against ports named `A` and `anode`: one matches, one does not, and completing it by index is a guess
about the pair that did not match. The part contributes no pads and is reported, naming both lists.
This is the Excellon-suppression class of decision — the two readings differ by a swap that nothing
downstream can detect.

**`R-ab1-4d`** No `SchematicId`, so no ports to join to: pads come out named by their **pin name**
alone, which is what an anchor written against a hand-placed part will spell. `U1.1` resolves;
`U1.VDD` does not, and that is honest — nothing on that board ever said `VDD`.

**`R-ab1-4e`** A **pin field** — R-rail11's `U1.VDD` reaching six pads — falls out of this with no
extra work, because the anchor resolution matches on the pad's net and several pads carry it.
Nothing here special-cases it; the note is so nobody adds a case that breaks it.

---

## 5. `R-ab1-5` — one funnel, and the site that has none

**`R-ab1-5a`** The resolution goes in **`RailArtwork`**, beside `FlattenedShapes` — a new
`RailArtwork.PadsFor(view, clayPath, tech, netlist)` returning both lists, applying R-ab1-3's
precedence, and returning its notes rather than posting them. That file exists for exactly this: its
header records that the CLI resolved three references the window resolved none of, and the fix was to
put the walks where neither surface owns them.

**`R-ab1-5b`** Every construction site calls it. Today four take pads from `PdnBoardPads`
(`RailRfViewModel.Open.cs:120`, `RailRfViewModel.Import.cs:77`, `DocRailFixtures.cs:168`,
`Cli/Rail.cs:289`) and **`RailRfWindow.Open.cs:148` takes none at all** — the bare-`.clay` path, which
is the whole reported gap. After this brief no site calls `PdnBoardPads` directly.

**`R-ab1-5c`** The CLI gets it for free and that is a gate, not a side effect:
`circuitrf rail board.clay --load U1.VDD=120mA` is refused today and must run after this brief, with
no display at any step.

**`R-ab1-5d`** The flatten's own outcomes gate the pads. `FlattenedShapes` returns
`view.Shapes` unflattened when `ExceedsCeiling` fires, with a note saying the parts' lands were not
read (`RailArtwork.cs:268-274`). **Pads follow that**: a board over the ceiling comes back with no
artwork-derived pads and the note says so, rather than with a confident pad list over geometry the
run never saw.

---

## 6. `R-ab1-6` — what the window shows

**`R-ab1-6a`** `HasNoPickableNets` currently means *a board is loaded and nothing named a net*, and
the window then says no board netlist named any nets and to click the pour. On an authored board with
pads and no net names that sentence is still true and still the right advice — **do not change it
here.** Brief 2 is what makes it conditional.

**`R-ab1-6b`** The parts table's Position column and the row-to-board selection (footprint 5
R-fp5-2c) work off pads and now populate on an authored board. Nothing to write; a test that asserts
it rather than assuming it.

**`R-ab1-6c`** The status strip gains the pad count and its source — *"142 pads, from the artwork"*,
*"142 pads, from the board netlist"*, *"142 pads: 128 from the board netlist, 14 from the artwork"*.
That last spelling is R-ab1-3b made visible, and it is the only way a user finds out their netlist is
two parts stale.

---

## 7. Gate

`tests/Ui.Tests/RailRf/LayoutPadsTests.cs`.

1. **A synthetic board with instances and no `.ipc` resolves pads.** Two footprint cells, known pin
   positions, a known transform — assert the exact DBU coordinates against hand arithmetic, not
   against another circuitRF path (R-ab1-1c, R-ab1-1d).
2. **Rotation and mirror.** The same board at 90°, at 217° (`RotDeg`, non-cardinal) and with
   `MirrorX` — pads land where `LayoutInstanceTransform` puts them. A mirror that is a no-op on a
   symmetric land proves nothing, so **the fixture's pins must be off both axes** — the nm↔DBU trap
   from WB-C, in its second form.
3. **An array placement produces one pad per pin per cell**, all carrying the same refdes
   (R-ab1-1d).
4. **An instance with no designator contributes nothing** (R-ab1-1b), and one whose `CellRef` does
   not resolve contributes nothing and is reported (R-ab1-1c).
5. **Name join, index join, partial-match refusal** — three rows (R-ab1-4a/b/c). The refusal row is
   the one that matters: assert the sentence names both lists.
6. **Precedence is per refdes.** A netlist naming eleven of thirteen parts, artwork holding all
   thirteen: eleven pads carry `BoardNetlist`, two carry `Artwork`, and the strip says so
   (R-ab1-3b, R-ab1-6c).
7. **A refused netlist yields all-artwork pads** (R-ab1-3c).
8. **Mounting inductance is computed from geometry.** The end-to-end that this brief exists for: an
   authored board, a rail, a decoupling capacitor, and a `RailMountingBasis.ComputedFromGeometry`
   answer — against a hand-computed loop, not against another railRF run (R-ab1-2d).
9. **The CLI runs it as a PROCESS.** `circuitrf rail board.clay --load U1.VDD=120mA` exits 0 with no
   display, and its `--json` pad count equals the in-process one (R-ab1-5c).
10. **Over the flatten ceiling: no pads, and the note says so** (R-ab1-5d).
11. **The shipped example is unchanged, bit for bit.** Run `examples/Power Rail` before and after
    this brief and compare the whole `DataSet`. It ships an `.ipc`, R-ab1-3a gives it precedence, and
    **a board that already worked must answer identically.** This is the gate that will catch a
    precedence rule implemented backwards.

## 8. Scope

- **No nets.** Brief 2. Pads come out with `Net` null on the artwork path.
- **No recursive descent.** R-ab1-1a — the root's own placements, the flatten's rule.
- **No second flatten, pin resolution or transform.** Overview §1a, §1d.
- **No change to `PdnBoardPads`.** It keeps its behaviour exactly; it gains a provenance stamp and
  loses its direct callers.
- **No writer.** Brief 3.
- **No LVS.** A netlist that disagrees with the artwork is brief 2 R-ab2-5's report, and this brief
  takes the netlist and says nothing (R-ab1-3d).
