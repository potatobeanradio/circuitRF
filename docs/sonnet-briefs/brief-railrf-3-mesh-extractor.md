# Brief 3 — the netlist contract, and the accurate DC extractor

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail3-n`
**Area:** `src/Design/Layout/Pdn/` · **Depends on:** 1, 2 · **Blocks:** 4, 5, 6, 13
**Design note:** [`railrf.md`](../design/railrf.md) §3, §4.1, §4.2, §4.3, §2.8

---

## 0. What this brief delivers

The piece the whole architecture turns on: **copper in, `ElaboratedNetlist` out.**

| Type | File | What it is |
|---|---|---|
| `PdnNetlist` | `src/Design/Layout/Pdn/PdnNetlist.cs` | The extraction's result: an `ElaboratedNetlist` plus the **map back** — which element came from which cell, which via, which part, which trace section. |
| `PdnMeshExtractor` | `src/Design/Layout/Pdn/PdnMeshExtractor.cs` | The **accurate** reading: a mesh of unit cells over the real copper, at DC. |
| `PdnRailRegions` | `src/Design/Layout/Pdn/PdnRailRegions.cs` | The galvanic region walk — which copper is this rail, and is it one region or three islands. |
| `PdnViaModel` | `src/Design/Layout/Pdn/PdnViaModel.cs` | Barrel resistance from the drill, the span and the plating. |
| `PdnAttachments` | `src/Design/Layout/Pdn/PdnAttachments.cs` | §4.3: what hangs on the mesh — parts, sources, loads, series elements. |

**At DC only.** Set ω = 0 and §4.1's inductance and shunt branch both vanish; what is left is a purely
resistive mesh — real, symmetric, positive-definite and fast. Brief 13 adds L, brief 14 adds the shunt.
The note is explicit that this is not a mode bolted on: **DC is the first point of the sweep**, and one
extractor serving both is what stops a DC answer and an AC answer drifting apart.

---

## 1. `R-rail3-1` — the deliverable is a NETLIST, and this is enforced

§3, and it is the architectural heart of the proposal:

> **So the deliverable of the extraction step is a NETLIST, not an `EmProblem`.**

```csharp
public sealed class PdnNetlist
{
    /// <summary>The extraction, as the engine consumes it. Ordinary ResistorModel / SeriesRlcModel /
    /// CapacitorModel / SnpModel / PortModel instances on ordinary nodes.</summary>
    public required ElaboratedNetlist Netlist { get; init; }

    /// <summary>What each element CAME FROM. Without this the ranked breakdown of §2.4 is impossible —
    /// "this 42 mm run of 0.3 mm inner-layer copper is 38 % of your drop" needs the element to name its
    /// trace section, and the drop map needs every node to name its cell.</summary>
    public required IReadOnlyList<PdnElementOrigin> Origins { get; init; }

    /// <summary>Node index → the cell it sits at, for the map overlays (briefs 8 and 15).</summary>
    public required IReadOnlyDictionary<int, PdnCellRef> NodeCells { get; init; }

    /// <summary>Port index → the anchor it resolved from, so a result names the load a user typed.</summary>
    public required IReadOnlyList<PdnPortBinding> Ports { get; init; }

    /// <summary>Which model produced this, which reference extent was used, what was defaulted, what
    /// was refused. Carried into every result and every export (overview §4 rule 1).</summary>
    public required PdnProvenance Provenance { get; init; }
}
```

### `R-rail3-2` — the one-write-path scan

Nothing under `src/Design/Layout/Pdn/` may build a matrix, factorise anything, own a result type, or
name `CircuitRF.Engine.Mom`. A **comment-stripped source scan** holds it, on the precedent
`src/Cli/Authoring.cs` and brief 6 of the stackup series already set:

- no `MnaSystem`, no `SparseLU`, no `CompressedColumnStorage`
- no `using CircuitRF.Engine.Mom`, no `EmProblem`
- no `new DataSet(`, no `new DataCube(`

This is not style. A second solve path here is the defect that makes §2.8's "one extractor, one mesh, one
solver" false, and it would not announce itself — the DC and AC answers would simply disagree by a few
percent on boards nobody checked by hand.

---

## 2. `R-rail3-3` — the rail's copper comes from `DrcConnectivity`, and there is no second walk

`src/Design/Layout/Drc/DrcConnectivity.cs` already partitions flat per-layer geometry into
electrically-joined pieces, **bridging layers through the stackup's own via spans**
(`StackupLayer.SpanFromLayer` / `SpanToLayer`). It is `internal`, which is not an obstacle — `Pdn/` is the
same assembly.

**Do not copy it, do not make it public, and do not write a second walk.** A rail whose island structure
the DRC and railRF disagree about is a bug neither of them reports. What it does not do is name a net;
net identity comes from the board netlist or the `.kicad_pcb` (brief 2), and `PdnRailRegions` joins the
two.

### `R-rail3-4` — the copper stops at every pad, and that is correct

§2.8:

> On imported artwork **the copper stops at every pad**, so the board is not electrically continuous
> until the user has said what bridges each gap; a capacitor bridges nothing at DC, which is correct and
> occasionally surprising.

So `PdnRailRegions` reports the **island structure** as a first-class output — *"this rail is three
regions joined by a 20 mil neck"* — and that report is what brief 7 draws and brief 8 highlights. §2.3
step 2 says *that alone has caught real problems*, so it is an output rather than a diagnostic.

Two islands joined by nothing at DC and by a capacitor at AC is not an error and must not be reported as
one. It is two regions, stated.

### `R-rail3-5` — the reference extent is applied HERE, and it is stamped

Brief 1 `R-rail1-6`'s three options are honoured by the extractor, and each changes the mesh:

- `AsImported` — the actual copper on the reference layer.
- `FilledToOutline` — that layer taken as solid within the board outline. **Optimistic.**
- `Infinite` — unbounded at its own z. Optimistic, and the only way to compare two outlines on equal
  terms.

The chosen extent goes into `PdnProvenance` and from there onto every plot, every table and every export.
A result that does not carry it is a result a reader cannot interpret.

---

## 3. `R-rail3-6` — the mesh: §4.1, at ω = 0

Divide the overlap region of the two conductors into cells of side Δ. At DC each cell edge contributes
one resistance and nothing else:

```
Series resistance along each cell edge     R = 2·Rs        (both planes, in series in the loop)
    below two skin depths                  Rs = ρ / T
```

The factor of two is the two planes in series in the loop, and it is the thing rev 2 of the note got
wrong — it used one plane's sheet resistance and every derived crossover frequency came out at half its
real value. **Carry the factor of two in the code's own comment**, because it is exactly the kind of term
that gets "simplified" out by someone reading the expression without the loop in mind.

For non-square cells R scales by the aspect ratio (along/across).

### `R-rail3-7` — the mesh FOLLOWS the copper, and that is what makes arbitrary shapes work

> A cell is present where **both** conductors have copper; a cutout, an antipad field, a split or a board
> edge simply removes cells.

There is no special case for a shape. *"The actual shapes used"* is not a stretch goal — it falls out of
the meshing, and any code that special-cases a rectangle has made it a stretch goal again.

### `R-rail3-8` — the antipad field is a CORRECTNESS requirement, not an optimisation

§9, and it is our own risk rather than a user-facing question:

> A BGA's antipad array removes a large fraction of the copper in a small region and it is precisely
> under the load port. Too coarse a mesh there and the spreading inductance is underestimated — again
> **optimistically**. Local refinement under port regions is a correctness requirement.

At DC the same geometry under-estimates the *spreading resistance*, by the same mechanism and in the same
direction. So the extractor refines locally under every port region and under every via field, at a
stated ratio, and **the refinement is visible in the classification overlay** (brief 8) rather than being
an invisible internal choice.

The gate is a convergence test: halving Δ under a port region changes the port resistance by less than a
stated tolerance. A mesh that is not converged there is optimistic and looks entirely ordinary.

---

## 4. `R-rail3-9` — vias are a READING of the artwork

§4.2, and the word is deliberate — *a reading rather than an approximation.*

- The drill data gives every hole.
- The stackup's via entries give the layers each span joins and carry a **plated-wall thickness**, which
  is what sets barrel resistance.
- The connectivity walk bridges layers **through via geometry** rather than by assuming the metal above
  and below overlaps — which is what makes an offset staircase of metal connect correctly.

Parallel vias fall out as parallel resistances with **no special case anywhere**. A 0.3 mm plated via
through a 1.6 mm board is about **1.2 mΩ**: twenty in parallel is negligible, two is not.

**Shared return vias are handled naturally** — two parts sharing one return via are coupled through it
because the mesh has a single node there, not two. That sentence is a test: build two parts on one return
via and assert the node count, because a mesh that gives them a node each produces a plausible number and
no error.

### `R-rail3-10` — a via and a plated component hole are distinguished by the NETLIST, not by geometry

The one honest limit from artwork alone. `BoardNetlistFile` already carries it: a record with a component
reference and a pin is a component hole; a record with a net and **no** component reference is a via.
`DrillViaPairing` already declares that distinction. Use it; do not re-derive it from hole diameter.

v1 assumes **through** vias (Q-10, one through-drill file), reads a span declaration where one exists, and
**reports rather than assumes** when it meets blind or buried spans it cannot resolve.

---

## 5. `R-rail3-11` — what attaches to the mesh, per §4.3

Every one of these is an ordinary `ComponentModel`. No new device type exists anywhere in this series.

| Attachment | At DC | Model |
|---|---|---|
| A capacitor | bridges nothing | present in the netlist, contributing no DC path (`R-rail3-4`) |
| A source | its own pad's cells | `ResistorModel` (the R of the R-L) to the reference, plus a DC voltage branch |
| A series part — the protection FET, the ferrite | **the largest terms after the source** | `ResistorModel` at its on-resistance / DCR. **Elements, never annotations.** |
| A load port | across power and reference at its own pin-field cells, tied together | a current injection, plus a `PortModel` for observation |
| A regulator | a load branch on its input rail, a source branch on its output rail | **two solves, never two branches in one mesh** |

### `R-rail3-12` — more than one source is more than one branch and NOTHING else

§4.3, and §9 says why it is worth a rule of its own:

> Two supplies feeding one net do not share in proportion to anything a designer can see; the copper
> decides, and on a compact board it decides badly. … a second source stamped at the wrong node produces
> **a plausible number, not an error**.

So there is no special case for a second source anywhere in the extractor. Brief 5 gates it by
superposition, which is arithmetic rather than opinion.

### `R-rail3-13` — a regulator is never two branches in one mesh

§4.3's last row, and note §9 calls the alternative *"the modelling mistake this arrangement exists to make
impossible."* The extractor extracts **one rail**. It takes the rail name, it produces that rail's
netlist, and it has no concept of a second rail at all. Brief 5 runs it once per rail in `RailOrder`'s
order.

---

## 6. `R-rail3-14` — cell size, and where it is allowed to be coarse

§4.1: cells are square-ish and sized by the shortest wavelength in the dielectric, Δ ≤ λ_min/20 — 7.2 mm
at 1 GHz on FR-4, 1.4 mm at 5 GHz. A 90 × 70 mm board at 1.4 mm is about 3,200 cells, trivially sparse.

**Below the cavity band the mesh may be far coarser**, which is what makes this brief cheap. At DC the
binding constraint is not wavelength at all — it is **geometry**: a cell must resolve the narrowest
conductor that carries current, or a 0.15 mm trace becomes a cell wide and its resistance is wrong by
whatever the cell size is. So the DC cell size is set from the **minimum feature width on the rail**,
with local refinement per `R-rail3-8`, and the wavelength rule binds only from brief 14 onward.

State both rules in one place, with the reason each exists, because a later reader who sees only the
wavelength rule will "fix" the DC mesh to it and quietly lose every thin trace.

---

## 7. Tests — `tests/Ui.Tests/RailRf/PdnMeshExtractorTests.cs`

### The headline gate — §7's cheapest real one

**DC resistance against the closed form.** A straight trace of known width, thickness and length has

```
R = L / (σ·W·T)
```

exactly, and a stepped trace is the sum over its sections. **The mesh must reproduce it to under 1 %.**
This is external arithmetic in the sense the PRD requires — it is not our own model agreeing with itself.

Test rows, from §2.8's own table so the numbers are checkable by hand:

| Geometry | Expected |
|---|---|
| 50 mm of 0.3 mm inner trace, 0.5 oz | ~165 mΩ (167 squares × 0.99 mΩ/sq) |
| 30 mm of 0.5 mm outer trace, 1 oz | ~29 mΩ |
| 10 mm of 1 mm-wide 1 oz trace | ~5 mΩ |
| One 0.3 mm plated via, 1.6 mm board | ~1.2 mΩ |

Per the standing rule on minimal tests: **one test per claim, not one per measured rung.** Trim the
`InlineData` to the rows that straddle the interesting behaviour — a thin inner trace, a wide outer one,
and a stepped one — and fold the arithmetic-only rows into a single sheet-resistance test.

### The rest

- **`R-rail3-7`**: a board with a cutout, a split and an antipad field produces a mesh with **no cells**
  in any of them, asserted by count against the copper area.
- **`R-rail3-8`**: halving Δ under a port region changes the port resistance by under the stated
  tolerance. Structural, not timed.
- **`R-rail3-9`**: twenty parallel vias are 1/20 of one, to under 1 %. Two parts on one return via share
  **one node** — asserted on the node count.
- **`R-rail3-11`**: a protection FET at 350 mΩ appears in `Origins` as an element, and the same board
  without it differs by exactly 350 mΩ on the path.
- **`R-rail3-2`**: the comment-stripped source scan.
- **Determinism**: the same board extracted twice produces element-wise identical netlists. An extraction
  that depends on a dictionary's hash order produces a drop map that moves between runs, and briefs 9 and
  17 both depend on it not doing that.

---

## 8. Scope

- **No inductance, no shunt branch, no frequency.** Briefs 13 and 14. ω = 0 throughout.
- **No fast graph extractor and no classification.** Brief 4 — and brief 4's gate is agreement with
  *this*, which is why this one is first.
- **No solve.** Brief 5. This brief produces a netlist and never calls anything that factorises it.
- **No via current limit.** Brief 6. This brief computes the barrel *resistance*; the limit is a
  different question with a different basis.
- **No UI, no overlay, no map.** Briefs 7 and 8. `NodeCells` exists so those briefs can draw; nothing
  here draws.
- **No `EmProblem`, ever.** §3 of the note is the argument and `R-rail3-2` is the enforcement.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.
