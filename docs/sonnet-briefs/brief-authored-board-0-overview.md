# Brief — a board circuitRF drew is a board railRF can read — the series

**Status:** unstarted, briefs 1-4 · **Date:** 2026-09-20
**Area:** `src/Design/Layout/Pdn/`, `src/Design/RailRf/RailArtwork.cs`,
`src/Design/Layout/Interchange/`, `src/Ui/RailRf/`, `src/Ui/Layout/`, `src/Cli/Rail.cs`,
`examples/Power Rail/`
**Requirement tag for the series:** `R-ab<n>-<m>`, scoped per brief (`R-ab1-4` is brief 1's fourth)
**Raised by:** the owner, 2026-09-20 — can a user author a `.clay` and then analyse its PDN, and if
the gap is that nothing can build the `.ipc`, what would it take to close it

---

## 0. The short answer

A user can already point railRF at a board they drew. `File ▸ Open…` lists `*.clay`
(`src/Ui/Views/RailRf/RailRfWindow.Open.cs:49`), `circuitrf rail` takes
`<path.crail | path.clay | path.csch | cell-folder>` (`src/Cli/Rail.cs:116`), the stackup resolves
through the layout's own ancestor walk, the copper flattens, the pour can be clicked, and it solves.

**What it cannot do is find the parts on it.** `RailBoardInputs.Pads` and `.NetPoints` are populated
from exactly one source:

```
BoardNetlistFile.ReadFile(.ipc)  →  BoardNetlist  →  PdnBoardPads.PadsOf / NetPointsOf  →  Pads, NetPoints
```

`src/Design/Layout/Pdn/PdnBoardPads.cs:53,78`, and nothing else in `src/` produces either record.
A `.clay` carries instances with designators, footprint cells with named pins, and a `Net` field on
every shape — and not one of the three reaches the PDN side.

So a board the user drew opens with `Pads = []` and `NetPoints = []`, and then degrades in four
places that `PdnBoardPads`' own header already names as the bug it was written to fix:

| | what goes quiet |
|---|---|
| 1 | `AvailableNets` is empty, so the rail pick list offers nothing and the window says no board netlist named any nets (`RailRfViewModel.Import.cs:157`) |
| 2 | every `U1.VDD` / `BT1.1` anchor resolves to no copper, so sources and loads fall back to **coordinate** anchors — which `RailPortAnchor` calls the fallback rather than the spelling, and which brief 16's A/B comparison cannot pair between two designs |
| 3 | `PdnMountingLoop` answers *"the board netlist has no pad for it"* for every part (`PdnMountingLoop.cs:257`), so **every mounting inductance is a typed one** and `RailMountingBasis.ComputedFromGeometry` is unreachable |
| 4 | no BOM means no parts-table rows, so the `.crlib` has nothing to resolve against |

None of the four fails. All four degrade to the path a board with no companion files takes, which is
why nobody has noticed: the answers are plausible and the report does not say which half of the model
was never reached.

### There is no writer for any of it

`BoardNetlistFile`, `PlacementFile` and `BomFile` are `Read` / `ReadFile` / `Recognize` only. The
shipped Power Rail example's `Board.ipc` and `Board.placement.csv` are written by
`examples/Power Rail/Sensor board/layout/Board.gen.py` — a Python script outside the application,
which brief footprint-5 R-fp5-2 makes load-bearing (*"it writes the `.clay`, the `.ipc` and the
placement table together, because they have to agree"*). **The one example that proves railRF works
end to end is the one thing a user cannot reproduce.**

### The one rule the series is built on

> **A board circuitRF drew already states everything the `.ipc` would state. Derive it. Do not ask
> the user to export it.**

An export step is a step a user forgets after the fourth trace edit, and it mints three files that
can go stale against the board with nothing to say so. The `.ipc` is a projection of facts the
`.clay` already holds; the projection belongs in the application, on the way in, not in a file the
user maintains.

The corollary, and it is what brief 3 is scoped by: **the writers are still worth having, for
interop and to retire `Board.gen.py`. They are not the railRF path and must not be sold as one.**

### The decision that shapes brief 2

**Nets are re-derived, with a stamped fallback** (owner, 2026-09-20).

```
net(pad)  =  the schematic's own binding for that refdes and pin,  where a schematic resolves
             else the shape's stored LayoutShape.Net
```

Which is `DisplayRefDes`' shape exactly (`LayoutModel.cs:875`: `SchematicId` before `RefDes`), and
it is chosen for that record's reason: **a re-derived value cannot go stale**, and the stored one is
what a board with no schematic behind it needs. The consequence stated up front, because it is the
part that surprises: **nothing in this series writes `Net` automatically.** Update Layout does not
stamp it. The stamp is a user's own act on a board they drew by hand, exactly as `RefDes` is for a
hand-placed instance, and it is authorable today — `LayoutShapePropertiesViewModel.CommitNetText`
(`:311`) sets it per-shape and across a multi-select.

---

## 1. Six things that are not obvious, resolved here once

These came out of reading the code before writing the briefs. Each is restated where it bites.

### 1a. The projection already exists, in two halves, and the renderer performs it every frame

```csharp
var pins = CellPins.Resolve(subView, tech);
var (wx, wy) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, col);
```

That is `LayoutRenderer.Instances.cs:1341,1352`, drawing pin markers. Brief 1 is those two lines plus
a refdes and a net, and it must use **those two functions** rather than a second pin resolution or a
second transform. `CellPins` exists precisely because three callers had grown their own copy of the
two-branch answer and the branch that matters fires only on older cells — its own header says so.

### 1b. A layout-derived pad set breaks `PdnBoardPads`' governing rule, and that is fine if it is said

`PdnBoardPads`' header is emphatic: *"It is a projection, never a reader and never geometry"* —
inherited from `BoardNetlistFile`'s R-gi5-1, that **the netlist is evidence ABOUT the artwork**.

A layout-derived pad is the opposite: it **is** geometry, measured off the artwork itself. That is
not a violation to be hidden behind the same type; it is a different kind of knowledge with different
failure modes (a netlist can disagree with the board; a layout cannot, and a layout can be missing a
net name where a netlist never is). So `PdnPad` grows a provenance field and every report that names
a pad can say where it came from. **A pad set whose origin is not recoverable is the defect this
series exists to fix, one level up.**

### 1c. Pin name against port order is unstated anywhere, and it can swap a part's two pads silently

Footprint R-fp3-5 enforces **pad count equals port count** and says nothing about which pad is which
port. The schematic gives `Instance.NetBindings` in **port order** (`src/Core/Design/Instance.cs:23`);
a generated chip land gives pins **named** `"1"` and `"2"` (`ChipLandPatternGenerator.cs:165`); an
imported cell gives whatever the vendor named them.

Join those wrong and pad 1 gets the reference net and pad 2 gets the rail. Nothing fails: the part
still bridges the two nets, still appears in the netlist, still contributes its capacitance. What
moves is `PdnMountingLoop`'s `powerPad` / `returnPad` (`:262-266`) — the two sides of the loop swap,
which on a symmetric 0402 is a mirror and on an asymmetric part is a wrong inductance that looks
entirely normal. Brief 1 R-ab1-4 states the rule and refuses what it cannot join.

### 1d. The flatten is the precedent site, and its ceiling applies here too

`LayoutDesignFlatten.Flatten` already emits the root's own designators from the root's own placements
(R-fp4b-4b), for the reason brief 1 needs: **the designator belongs to the placement, not to the
cell.** The pad projection is the same walk over the same list with the same ceiling — and
`RailArtwork.FlattenedShapes` (`:256`) already drives it for railRF, already reports `ExceedsCeiling`
and already reports each unresolved instance. Brief 1 resolves pads **beside** that, in the same
file, so a board whose geometry was truncated does not come back with a confident pad list over it.

### 1e. `.crlib` is the one companion that is already solved

`PartLibraryIo` reads **and writes** (`src/Design/RailRf/PartLibraryIo.cs:69`), and railRF brief 24
shipped a Part Library editor document — double-click from the project tree, a grid, add/remove rows,
bias curves, undo, Save As, revision control. **No new format is needed and no generator is needed.**
The only gap is that `AddRow` adds an *empty* row while `PartLibraryCoverageContext` already knows
exactly which part numbers the board asked about and did not find. That is brief 4, and it is small.

### 1f. Five places construct the board inputs, and a source only some of them consult is the same bug again

`new RailBoardInputs` appears at `RailRfViewModel.Open.cs:113`, `RailRfWindow.Import.cs:172`,
`RailRfWindow.Open.cs:148` and `DocRailFixtures.cs:159`; `src/Cli/Rail.cs:497` builds its own
`BoardInputs`. Four of them take pads from `PdnBoardPads`. **The bare-`.clay` one at
`RailRfWindow.Open.cs:148` takes none at all** — which is the whole reported gap, in one line.

This is `RebuildAvailableNets`' scar verbatim (`RailRfViewModel.Import.cs:131`: a derived list only
one of two writers refreshed was wrong on the other path, silently). So brief 1 R-ab1-5 puts the
resolution in `RailArtwork`, where neither surface owns it, and every site calls it — the same move
`RailArtwork` itself was created by, for the same reason.

---

## 2. What each brief delivers

| | Brief | Delivers |
|---|---|---|
| 1 | [layout pads](brief-authored-board-1-layout-pads.md) | `PdnLayoutPads` — instances + `CellPins` + the transform into `PdnPad`/`PdnNetPoint`; provenance; the precedence rule; the pin/port join; one funnel in `RailArtwork` |
| 2 | [net identity](brief-authored-board-2-net-identity.md) | re-derived from the schematic beside the cell, stamped `LayoutShape.Net` as the fallback; naming a pour as a gesture; `AvailableNets` off a board with no `.ipc`; divergence reported |
| 3 | [companion writers](brief-authored-board-3-companion-writers.md) | `BoardNetlistWriter` / `PlacementWriter` / `BomWriter`, the export rows, the `convert` pairs, and `Board.gen.py` retired |
| 4 | [part library seeding](brief-authored-board-4-part-library-seeding.md) | "add the rows this board asked for" off the existing coverage context |

Brief 1 is independent and is the one that makes an authored board work at all. 2 depends on 1.
3 depends on 1 and 2 — it serialises their projection and must not grow a second one. 4 is
independent of all three and can land in any order.

**Brief 1 alone is worth shipping on its own.** With it, a drawn board resolves refdes anchors,
computes mounting loops from geometry, and runs headlessly with `--load U1.VDD`. Brief 2 is what
gives the nets names.

---

## 3. What this series is NOT

- **Not an `.ipc` requirement.** §0's rule. If a brief here finds itself telling a user to export a
  netlist before analysing their own board, it has taken a wrong turn.
- **Not a second flatten, a second pin resolution or a second transform.** §1a and §1d.
- **Not LVS.** Brief 2 R-ab2-5 *reports* a disagreement between a netlist and the artwork because the
  comparison is free once both are in hand. It does not resolve one, rank one, or refuse on one.
- **Not a router and not a ratsnest.** R-L5h-3 removed persisted ratsnest geometry deliberately;
  nothing here brings it back.
- **Not a new part model, a new device type or a new result type.** Pads are geometry; everything
  downstream of them is unchanged.
- **Not a change to `PdnBoardPads`' own behaviour.** A board that ships an `.ipc` must answer exactly
  as it does today — which is brief 1's last gate.

## 4. Vendor references

The Power Rail example and every fixture in this series stay synthetic: no board vendor, no
component manufacturer, no ordering part number, no PDK name — not as a fixture, not as a comment,
and not as a list of names to filter. `.kicad_pcb` remains the one permitted spelling, as a file
extension. Grep before any commit.
