# Brief 2 — one extraction, two readers: railRF and LVS share the API

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs2-n` · **Design note:** [`lvs.md`](../design/lvs.md) §3, §7
**Area:** `src/Design/Layout/Extraction/` (new), `src/Design/Layout/Pdn/PdnLayoutNets.cs`,
`PdnLayoutPads.cs`, `PdnAttachments.cs`, `PdnMeshExtractor.cs`, `PdnRailRegions.cs`,
`src/Design/Layout/Drc/DrcConnectivity.cs`, `src/Design/RailRf/RailArtwork.cs`, `src/Cli/Rail.cs`
**Depends on:** — · **Blocks:** 3, 9

---

## 0. What this brief delivers

The copper reading railRF performs becomes a shared API that LVS calls too, gains a broad phase it
never had, and starts remembering **why** two pieces are one net.

**Nothing about railRF's answers changes.** Its existing tests are the gate, and if any number
moves, the promotion is wrong.

```
   shapes ──► LayerRegions.Build ──► DrcConnectivity.Extract ──► CopperPieces
                                             │                        │
                                     (+ merge edges, NEW)      (+ PieceIndex, NEW)
   instances ──► PlacedPins.Of ──────────────────────────────────────┴──► net of each pin
```

---

## 1. `R-lvs2-1` — what moves, and it moves without changing meaning

A new namespace `CircuitRF.Design.Layout.Extraction`, below the UI firewall, same assembly.

| New | Was | Change |
|---|---|---|
| `CopperPieces` | `PdnCopperPieces` in `Pdn/PdnLayoutNets.cs` | renamed, indexed (§3), edges (§4) |
| `LayerRegions.Build` | `PdnMeshExtractor.BuildLayerRegions` | renamed; `internal` → `public` within the assembly's own conventions |
| `PlacedPins.Of` / `.NetPointsOf` | `PdnLayoutPads.PadsOf` / `.NetPointsOf` | renamed, + `PinNaming` (§2), + hierarchy hook (brief 9) |
| `PlacedPin` | `PdnPad` in `PdnAttachments.cs` | renamed; `PdnPadSource` → `PinSource`, one new member |
| `Regions.Walk` | `PdnRailRegions.Walk` | renamed |
| `Conductors.Of` | the conductor enumeration inside `PdnRailRegions` | extracted; **sheet resistance stays behind** |

**`R-lvs2-1a` Behaviour is preserved exactly.** This is a move and a rename, not a rewrite. Where a
signature grows a parameter it takes a default that reproduces the old call.

**`R-lvs2-1b` Nothing PDN moves.** `SheetResistanceOhmsPerSquare`, `PdnViaModel`, `PdnInductance`,
`PdnMountingLoop`, `PdnPlaneModes`, `PdnMeshExtractor`'s mesh, `PdnGraphExtractor`, `PdnAssembly`,
`PdnNetlist` all stay. Those **price** copper; LVS never prices anything. `PdnConductor` keeps its
sheet resistance and gains a projection to the neutral `Conductor`.

**`R-lvs2-1c` `DrcConnectivity` does not move and does not become public.** Overview §1b. It gains
one thing, in §4, and its existing signature keeps working unchanged.

**`R-lvs2-1d` The old names do not survive as aliases.** No `[Obsolete]` forwarders, no `using`
aliases. A type with two names is two types as far as a later reader is concerned, and this repo
has paid for that with three copies of a version number. Every call site is updated in this brief.

---

## 2. `R-lvs2-2` — `PinNaming`, and the trap it exists to close

`PlacedPins.Of` today takes `portNamesOf` and `portNetsOf` delegates and applies `PdnLayoutNets`'
precedence: *the schematic's binding, else the stamped net*. Right for railRF. **For LVS it is the
defect that has no symptom** (overview §1a).

**`R-lvs2-2a`** The mode becomes an explicit enum parameter, required, no default:

```csharp
public enum PinNaming
{
    /// Ask the schematic first, then the artwork. railRF's rule, unchanged.
    SchematicThenArtwork,
    /// The artwork only. Nothing a schematic says may reach the answer.
    ArtworkOnly,
}
```

**`R-lvs2-2b`** An enum rather than "pass null for the delegates". Null-means-artwork-only is
already a supported shape (R-ab1-4d) and it is exactly how a caller half-supplies the schematic by
accident. Making a caller **write the word** is the whole mechanism.

**`R-lvs2-2c`** In `ArtworkOnly`, the schematic-facing delegates are not merely unused — passing
them is an `ArgumentException`. A parameter that is silently ignored in one mode is a parameter
someone will supply and believe in.

**`R-lvs2-2d`** `PlacedPin.Source` gains `Schematic` alongside `BoardNetlist` and `Artwork`, still
required and still positional (R-ab1-2b's rule, kept for its reason). An LVS pin is always
`Artwork`; the third value exists so brief 4's schematic-side netlist can carry pins in the same
type without lying about where they came from.

---

## 3. `R-lvs2-3` — `PieceIndex`, the broad phase `PieceAt` never had

`PdnCopperPieces.PieceAt` is a linear scan over every piece with a bbox rejection, then an exact
Clipper2 `Contains`. At railRF's scale — one rail, tens of anchors — it is free. At 3,000 parts ×
4 pins against a partition of thousands of pieces it is the entire run.

**`R-lvs2-3a`** A **uniform grid** over piece bounding boxes, not an R-tree. `WirePairSweep`'s
header is the precedent and the reasoning carries: `LayoutSpatialIndex` is the right structure for
10⁵–10⁶ shapes of wildly varying size, and it is typed to `LayoutShape` lists besides. A grid is a
fraction of the code and no tree to keep fresh.

**`R-lvs2-3b` The degenerate case is bounded and must be stated.** A board-wide ground pour's bbox
covers every cell, so it is a candidate for every query. That is fine *because the number of such
pieces is small* — a handful of pours, tested exactly, per query. It is not fine if someone later
reaches for the same grid over shapes rather than pieces. Say so at the class header.

**`R-lvs2-3c`** Rebuilt from scratch per run, never maintained incrementally —
`WirePairSweep`'s rule, for its reason: a run already re-reads the whole design and a stale
acceleration structure is a source of silently missed answers.

**`R-lvs2-3d` Cell size is derived from the median piece bbox**, not configured. A knob here is a
knob nobody can set correctly.

**`R-lvs2-3e` The exact answer is unchanged.** The grid narrows candidates; `Regions.Contains`
still decides. A test asserts index-vs-scan agreement over a randomized fixture, which is the only
way to know a broad phase is not rejecting real hits.

---

## 4. `R-lvs2-4` — the union-find remembers why

`DrcConnectivity.Extract` unions and renumbers, and what survives is **membership**. Membership
answers *are these two pins one net* and cannot answer *why*, which is the only question a designer
looking at a short actually has.

**`R-lvs2-4a`** The extract retains one record per successful union:

```csharp
public readonly record struct PieceJoin(int PieceA, int PieceB, JoinKind Kind, long X, long Y, LayerKey Layer);
public enum JoinKind { SameLayerTouch, Via }
```

**`R-lvs2-4b`** This is an **additive return**, not a change to the existing one.
`DrcConnectivity.Extract`'s current signature keeps working and keeps returning what it returns;
an overload returns the joins alongside. DRC does not ask for them and pays nothing.

**`R-lvs2-4c`** One edge per union, not every touching pair. A spanning forest is all a path walk
needs, and recording every adjacency would make the structure quadratic in the thing it exists to
make cheap.

**`R-lvs2-4d`** The coordinate is **where the join happens** — the via's own position for a via,
and a point inside the intersection for a same-layer touch. That coordinate is what brief 8 puts a
marker on, and *"joined through a 0.2 mm neck of Metal1 at (1.204 mm, 3.881 mm)"* is the whole
difference between a report a user can act on and one they cannot.

**`R-lvs2-4e`** railRF may use it too — an island report that can say which via bridges two
regions is strictly better — but this brief does not change railRF's output. Later, separately, on
its own evidence.

---

## 5. `R-lvs2-5` — every call site moves, and railRF's answers do not

**`R-lvs2-5a`** Call sites to update: `RailArtwork`, `PdnMeshExtractor`, `PdnRailRegions`,
`PdnAssembly`, `PdnLayoutNets`' remaining half, `src/Cli/Rail.cs`, the railRF view models, and the
railRF test fixtures. After this brief **nothing outside `Layout/Extraction` names the old types**,
held by a source scan.

**`R-lvs2-5b` The `RailArtwork` seam stays where it is.** That file exists because the CLI resolved
three references the window resolved none of, and the fix was to put the walks where neither
surface owns them. LVS gets its own equivalent seam in brief 3; it does not reach into
`RailArtwork`.

**`R-lvs2-5c` railRF's own precedence rule is untouched.** `SchematicThenArtwork` is its mode,
spelled explicitly now, and `PdnLayoutNets`' header keeps saying what it says.

---

## 6. Gate

`tests/Ui.Tests/Lvs/SharedExtractionTests.cs`, plus the whole of `tests/Ui.Tests/RailRf/`.

1. **`examples/Power Rail` answers identically, to the number.** Run the whole analysis before and
   after and compare the complete `DataSet`, not a summary. This is the gate that catches a
   promotion that changed meaning, and it is the most important test in the brief.
2. **Every existing railRF test passes unmodified** except for type renames. A test whose
   *assertion* had to change is evidence the promotion was not behaviour-preserving — stop and
   report rather than updating the expectation.
3. **`PinNaming.ArtworkOnly` ignores a schematic that would otherwise answer.** A board whose
   schematic binds `U1.1` to `VDD` and whose copper says nothing: `SchematicThenArtwork` returns
   `VDD`, `ArtworkOnly` returns null. This is overview §1a made a test.
4. **Supplying a schematic delegate in `ArtworkOnly` throws** (`R-lvs2-2c`).
5. **Index vs scan agree.** 2,000 randomized pieces, 10,000 randomized query points, on-layer and
   any-layer: the indexed answer equals the scan's for every query (`R-lvs2-3e`).
6. **The index is asymptotically better, asserted as a COUNTER.** Exact-test count per query stays
   bounded as piece count grows 10× — not a wall-clock assertion (overview §1i).
7. **A board-wide pour does not defeat the grid.** One piece covering the whole extent plus 2,000
   small ones: exact tests per query stays within a small constant of the pour count
   (`R-lvs2-3b`).
8. **Joins reconstruct the partition.** Union the joins independently and assert the resulting
   partition equals `DrcConnectivity`'s net numbering exactly (`R-lvs2-4a`). A spanning forest that
   does not reproduce the partition is a wrong forest.
9. **A via join carries the via's own coordinate**; a same-layer join carries a point inside the
   intersection (`R-lvs2-4d`).
10. **The four-layer inner-plane case still works.** The every-conductor-the-barrel-passes rule is
    the one that was learned the hard way; assert it survives the move with the same fixture that
    caught it.
11. **No old type name survives.** A comment-stripped source scan over `src/` for `PdnCopperPieces`,
    `PdnPad`, `PdnPadSource`, `PdnLayoutPads`, `BuildLayerRegions` (`R-lvs2-1d`, `R-lvs2-5a`).

## 7. Scope

- **No LVS.** This brief has no comparison, no device and no schematic in it.
- **No behaviour change anywhere.** Gates 1 and 2 are the whole contract.
- **No change to `DrcConnectivity`'s existing signature or to DRC's cost** (`R-lvs2-4b`).
- **No new railRF output** from the join edges (`R-lvs2-4e`).
- **No incremental index** (`R-lvs2-3c`).
