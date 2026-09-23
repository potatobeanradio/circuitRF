# Brief 31 — the return is a net, not a layer

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail31-n` · **Phase:** defect, changes numbers
**Area:** `src/Design/Layout/Extraction/Regions.cs` (`Walk`, `NetsAt`, `ReferenceNetOn`),
`src/Design/Layout/Pdn/PdnGraphExtractor.cs`, `src/Design/Layout/Pdn/PdnMeshExtractor.cs`,
`src/Design/Layout/Pdn/PdnRailConnectivity.cs`, `src/Design/Layout/Pdn/PdnAssembly.cs` (`ChooseGround`,
`Build`), `src/Ui/RailRf/RailRfViewModel.Solve.cs` (`BuildRequest`), `src/Cli/Rail.cs`
**Depends on:** brief 30 · **Blocks:** 32 and 33 (both gate against numbers only this brief makes right)
**Evidence (outside the repo, never copy into it):** the fourth field report's workspace, held by the
owner, and brief 30's two scratch harnesses, held beside it with a README (`h30`: the `refnet`,
`seeds`, `nets`, `run` and `accurate` modes and the `REFNET`/`CELLS`/`DUMP` switches; `fx`: the
old-DLL-against-new fixture dump). Rewrite the `.crail`'s absolute `ArtworkCellRef` relative and
regenerate `.generated-cells` first. Build a harness IN PLACE, never with `-o`: that writes worker
binaries into `src/Ui/private/…` inside the repo.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What is wrong, measured (brief 30, `src/Design/RESOLVED.md`)

The designer's rail: a coordinate source on the 3v3 connector land, a 30 mA coordinate load on a VDD
pad, JP1 declared series, the inner GND layer (3/0) as the reference. **railRF answers it, and every
answer it gives is wrong:**

| | Fast | Accurate (default mesh) |
|---|---|---|
| today | **−149,954,700 V** at the load, not refused | **1.316 mV** drop |
| the rail it should have priced (converged mesh, brief 32) | — | **~4.0 mV** drop |

Three faults, all in how railRF decides what the RETURN is:

1. **A rail seed claims the ground net.** The load coordinate stands on a VDD pad (Top) directly over
   the Bottom Copper GND pour. `Regions.Walk` seeds a coordinate on every layer EXCEPT the reference,
   and the reference is a LAYER, so the pour — galvanically the same net as the 3/0 plane, through the
   ground vias — became rail. The whole ground net (1,055 GND net points) was priced as supply copper.
2. **The reference is "every piece on the reference layer".** No reference net is named on this board,
   so `Walk` takes all 62 pieces on 3/0 as reference — including the rail's OWN lands there (a 2.5 mm²
   3v3 island in an antipad next to the source). `ChooseGround` put node 0, the source's return, on that
   island.
3. **A split return is solved, not refused.** With the plane read as a trace (brief 33), the netlist is
   three disconnected graphs: the rail, the plane's skeleton carrying the load's return, and the island
   carrying the source's. The 30 mA load drives a floating network and `LinearDcEngine` reports −150 MV.
   Nothing between the extraction and the result looks at connectivity on the reference side.

**The measurement that makes the fix cheap:** `Regions.ReferenceNetOn` — galvanic ambiguity, already
written and already run by the window's net preview (`RailRfViewModel.NetPreview`) — resolves this
board's return to **GND** on 3/0 (and on 1/0). The run never receives it: `BuildRequest` passes
`board.ReferenceNet`, which is null on a Gerber board, and `circuitrf rail` passes the document's, also
empty. Forcing the reference net to `GND` in the harness (with the pour kept out of the rail by hand)
moves Fast from −150 MV to **5.38 mV** — finite, and brief 33's to make right.

## 1. `R-rail31-1` — resolve the return net once, in the extraction

- One function, in `src/Design`, that both extractors call before `Regions.Walk`: the NAMED reference
  net (request, else document) where there is one; else `ReferenceNetOn` on the confirmed reference
  layer; else null. The window's preview and the run must get the same answer from the same call — the
  preview's copy becomes a caller of it, not a second rule.
- It is reported, with where it came from ("measured from the copper on 3/0" / "named in the document"),
  on the result's provenance and in the window's reference row, which already says *the reference return
  — also the schematic's '0'* for the preview.
- `circuitrf rail` and the window must produce the same netlist for the same document. Hold that with the
  existing CLI-vs-in-process pattern.

## 2. `R-rail31-2` — a rail seed never claims the return net

In `Regions.Walk`, once the return's galvanic nets (`refNets`) are known, remove them from `railNets`.
A pad or coordinate over a ground pour on another layer then seeds only the copper that is not the
return. `OwnReturnRefusalFor` keeps its job — a rail anchored ONLY on its return is still refused, and
its predicate must be re-checked against the new order (it runs on seeds, not on `railNets`).

Where the return is null, keep today's seeding, and see §3's refusal.

## 3. `R-rail31-3` — the reference is the return net's copper

With a return net, `refNets` is that net's pieces on the reference layer only (this is `Walk`'s existing
`refSeeds` branch; §1 is what feeds it). The rail's own lands on the reference layer are then neither
rail (a conductor cannot be its own return — unchanged) nor reference, and they drop out.

**Where the return cannot be resolved AND a rail net has copper on the reference layer, refuse.** That
is exactly the case in which "every piece on the layer" silently mixes the rail into its own return.
The refusal names the layer and the rail net found on it, and says what answers it: name the reference
net (the window's reference row gets a net pick list; the `.crail` spelling is `ReferenceNet`). A board
whose reference layer carries only the plane — every existing fixture — is untouched.

## 4. `R-rail31-4` — never solve a split return

Two checks, both needed:

- **Galvanic, up front** (beside `PdnRailConnectivity`, same cost class): every source's return point
  and every load's return point must land on reference copper that is ONE galvanic piece, or pieces a
  declared part joins. Refuse otherwise, naming the two return points and the layer. This is a fact the
  walk already has.
- **Netlist backstop, in `PdnAssembly.Build`, for BOTH extractors:** every port's power and reference
  node, and every source terminal, must be in the ground node's connected component of the stamped
  netlist. A thinned or coarse reading can split copper the galvanic check saw as one piece — that is
  what happened here — so the check that matters is on what was stamped. Refuse, naming the port; never
  hand `LinearDcEngine` a floating network. (`IncludeIsolatedRegions` islands that carry no port are not
  this and stay a diagnostic.)

## 5. Gates

1. **The field board, the designer's own document** (JP1 + 3/0, anchors unedited), through `RailDcRun`,
   Release: the rail's islands are 3v3 and VDD only (no GND net point on any rail island — brief 30's
   `nets` mode); the reference is GND's copper on 3/0 only; Fast is finite; **Accurate with the mesh at
   6 cells across equals brief 30's hand-seeded run (pour deleted, reference net GND): 3.927 mV to 1e-6
   relative.** The pour, once neither rail nor reference, contributes nothing — identical is the right
   bar, not close.
2. **Every existing answer unchanged** on boards this brief does not reach: dump-compare
   `PdnFastExtractorTests`, `PdnRefusalCauseTests`, `PdnMeshExtractorTests` and the shipped Power Rail
   example before and after (brief 30's `fx` runner pattern — the test sources compiled against old and
   new DLLs, every element, port, node cell and voltage). Any difference is either this brief's defect
   on that fixture — then say which — or a regression.

## 6. Tests (minimal — one per claim, `FullyQualifiedName~` filters only)

1. A VDD pad on Top over a GND pour on Bottom, the pour joined by vias to an inner GND plane that is the
   reference, GND net points on ground pads: the rail's copper holds no GND piece, and the answer equals
   the same board with the pour deleted.
2. Same board, no net points at all (return unresolvable) and a rail land on the reference layer: refused,
   naming the layer and the net.
3. A reference split by construction (two plane halves, source return on one, load return on the other,
   nothing joining them): refused by the galvanic check. And the backstop alone: a fixture whose stamped
   netlist splits while the copper is one piece (the plane-as-trace case — build it small) refuses in
   `PdnAssembly`, for Fast; and the same backstop in Accurate on a hand-made split mesh.

## 7. On completion

Findings in `src/Design/RESOLVED.md` (and `src/Ui/RESOLVED.md` for the reference row), never in any
`CLAUDE.md`. Correct brief 29's and brief 30's notes on this board where they depend on the old
reading. Re-run brief 30's harness `run` on the designer's document and record the new Fast and Accurate
numbers — those are brief 32's and 33's starting points.
