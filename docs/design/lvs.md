# circuitRF — Layout Versus Schematic (LVS)

**Status:** Proposal — rev 2 (owner's six decisions folded in) · **Date:** 2026-09-21 ·
**Phase:** proposed (post-L9, post-railRF)

**Decisions taken (owner, 2026-09-21).** All folded into the body below; §12 keeps the record.
1. **An `M×N` array is `M×N` devices** (§4.7).
2. **Terminals may be derived by position** when both sides are entirely unnamed — with a warning
   on every run that does it (§4.2).
3. **The default unit of comparison is the CELL**, not the testbench (§5).
4. **Series/parallel reduction is ON by default**, following what users expect from an LVS, bounded
   by the table in §6.3 R-lvs-35 and reversible with `--no-reduce`. Every reduction is reported and
   every finding un-reduces.
5. **The whole of LVS lands before any of it ships** — §10 is a build order, not a release plan.
6. **A ground-reference conductor with no drawing layers is still the reference**, and the run
   reminds the user every time it relies on one (§4.4).

Companions: [`layout-view.md`](layout-view.md) §3.4 and §9A.3 (where LVS was first named as a
direction, and what was deliberately carried from day one to keep the door open),
[`railrf.md`](railrf.md) (the other consumer of the same extraction), [`wbond.md`](wbond.md) §5.2 /
§9.5 / §9.6, [`pcell-contract.md`](pcell-contract.md) R3/R4 (pin names and origin),
[`hierarchical-net-extraction.md`](hierarchical-net-extraction.md) (the schematic side, already
built), [`net-extraction-and-run.md`](net-extraction-and-run.md), [`cli.md`](cli.md) §9 (adding a
verb) and §10 (why this is **not** part of `check`), [`pdk-import.md`](pdk-import.md).

---

## 0. Reuse map — what already exists

**This is the most important section in the document.** LVS in circuitRF is not a new subsystem; it
is a fourth reader of geometry the repo already partitions, a second consumer of a pad projection
railRF already performs, and a comparison against a netlist the schematic side already extracts
hierarchically. Almost every part that would normally be the hard, risky half of an LVS is written.

| Need | Already exists | Where | Reused how |
|---|---|---|---|
| Copper → electrically-joined pieces, bridged through via geometry and through **every conductor a barrel passes** | `DrcConnectivity.Extract` | `src/Design/Layout/Drc` | **Verbatim.** `PdnRailRegions`' own header states the rule and it binds here too: *do not copy it, do not make it public, do not write a second walk.* |
| Per-layer unioned regions from flat shapes | `PdnMeshExtractor.BuildLayerRegions`, `DrcRegions.Expand/Union` | `src/Design/Layout/{Pdn,Drc}` | Verbatim, promoted (§3) |
| Point → which piece, and what that piece is CALLED | `PdnCopperPieces` | `src/Design/Layout/Pdn/PdnLayoutNets.cs` | Promoted and **indexed** (§3, G7) |
| Pads of every placed part: designator, pin name, world coordinate | `PdnLayoutPads.PadsOf` | `src/Design/Layout/Pdn` | Promoted and made **hierarchical** (§3, §4.5) |
| Cell pins, including the branch that only fires on pre-persistence generated cells | `CellPins.Resolve` | `src/Design/Layout` | Verbatim. A fourth private copy is exactly the defect that type exists to prevent |
| Instance placement, mirror, arbitrary angle, array element | `LayoutInstanceTransform.TransformPoint` | `src/Design/Layout` | Verbatim |
| Whole-design elaboration with a ceiling | `LayoutDesignFlatten`, `LayoutFlatten` | `src/Design/Layout` | Extended to carry instance identity (G2) |
| Hierarchical schematic netlist: `TestBench` + `Library`, cell ports, ground → `"0"` | `NetExtractor` | `src/Design/Schematic` | Verbatim |
| Flatten of that hierarchy, parameter resolution, node numbering | `Elaborator` | `src/Core/Elaboration` | Verbatim (§5) |
| Terminal table: `SymbolPin.PortIndex i` ↔ `LayoutView.Pins[i-1]` | `ComponentTerminals.Build` | `src/Design/Layout/Interchange` | The **model** for §4.2's terminal map; today it covers imported parts only |
| Violation + marker + severity + stable key + persisted, visible waiver | `DrcViolation`, `DrcWaiver`, `DrcRunResult` | `src/Design/Layout/Drc` | Shape reused; key differs (§8.2) |
| Two-claims-about-one-board reporting: refdes, pin, sentence; reported never resolved | `PdnBoardDivergence` | `src/Design/Layout/Pdn` | Reporting shape reused |
| Typed, id-stable user-facing findings | `Diagnostic` | `src/Diagnostics` | Verbatim |
| Spatial index over layout geometry | `LayoutSpatialIndex` | `src/Design/Layout` | The fix for G7 |
| Headless verb anatomy, refusals, `--json`, exit codes | `src/Cli` + `cli.md` §9 | | §8.3 |

**What is deliberately NOT reused.** `PdnGraphExtractor`'s raster-and-skeleton reading is about
**resistance along a path**, not about connectivity, and it can be wrong in three named ways that
cost a plausible number and no error. LVS asks a topological question with an exact answer; it must
never inherit an approximation whose failure mode is silence. The skeleton has no place here.

---

## 1. What LVS is, and the one premise that makes it tractable

**LVS compares two netlists.** One comes from the schematic, one is extracted from the artwork, and
the answer is a correspondence between them plus every place the correspondence fails. It is not a
picture comparison, not a DRC, and not a claim about performance.

### 1.1 The premise: circuitRF's layout is instance-bearing

In a classical flow, layout is polygon soup and LVS must *recognise* devices by finding the layer
combinations that constitute them — a diffusion rectangle crossed by poly is a transistor. That
recognition step is where a classical LVS spends most of its complexity and most of its
configuration burden.

**circuitRF's layout is not polygon soup.** A `.clay` holds `LayoutInstance`s, each naming a cell.
An MMIC FET is a PCell instance carrying its resolved parameters; a board resistor is a land-pattern
instance carrying a `PartKind` and a designator; an imported part is a cell folder whose symbol and
layout were numbered together by `ComponentTerminals`. **A device is already a first-class object in
the file.** So the primary reading is *correspondence*, not recognition, and that is what makes
industrial-strength LVS reachable here at a fraction of the usual cost.

**R-lvs-1. Instance-based device extraction is the primary reading. Geometric device recognition is
an opt-in second reading, for artwork that has no instances** — hand-drawn devices, and imported
GDSII or Gerber where the hierarchy was flattened away (§4.1 tier 3). It is not the default, it is
not required for a design circuitRF authored, and nothing in the primary path depends on it.

**Say the consequence out loud, because it is the honest limit:** LVS verifies that the artwork's
*declared* devices are wired the way the schematic says. Where the artwork declares a device that is
not really there — a PCell whose generator draws something other than what its parameters claim —
LVS cannot catch it and does not pretend to. That is the EM extractor's question, not this one's.

### 1.2 What LVS answers

1. **Every schematic device has exactly one layout device, and vice versa.** Unmatched on either
   side is a finding.
2. **Corresponding devices' terminals reach corresponding nets.** A terminal that reaches the wrong
   net is a **short** or an **open**, and the report says which and *where*.
3. **Corresponding devices agree about their parameters**, within a stated per-dimension tolerance
   (§6.4).
4. **Nothing in the artwork is electrically present and unaccounted for** — copper joining two nets
   the schematic keeps apart is a short even when no device is involved.

### 1.3 What LVS is not

Not DRC (`DrcEngine` answers manufacturability). Not extraction for simulation (that is the EM
engine and railRF). Not a router and not an ERC. It does not repair, does not stamp, does not
re-save, and — like `check` — **it writes nothing**, so it runs on a read-only tree and on a
workspace another process has open.

---

## 2. The three artifacts

```
   .csch ──NetExtractor──► TestBench + Library ──Elaborator──► SCHEMATIC NETLIST
                                                                      │
                                                                      ├──► CORRESPONDENCE  ──► LvsResult
                                                                      │
   .clay ──LayoutExtract──► LayoutNetlist ──────────────────────► LAYOUT NETLIST
   .ctech ──┘                                                    (+ .wBond, for an assembly)
```

**R-lvs-2. Both sides reduce to ONE netlist type before comparison, and the comparator sees nothing
else.** Not two half-specialised graphs with a comparator that knows which is which. A comparator
that can tell the two sides apart will eventually treat them differently, and the asymmetry will be
a bug nobody can see. The type is `LvsNetlist`: devices, terminals, nets, plus a provenance tag per
object saying which document and which instance path it came from. The provenance is for the
**report**; the comparator never reads it except to break ties deterministically (§6.2).

---

## 3. One extraction, two readers — railRF and LVS share the API

**R-lvs-3. railRF's PDN extraction and LVS read the geometry through the same functions. Where they
differ today, the shared part is promoted rather than copied.** This is the rule
`PdnRailRegions` already states for `DrcConnectivity` ("a rail whose island structure the DRC and
railRF disagree about is a bug neither of them reports") applied one level up. A board whose
connectivity railRF and LVS disagree about is exactly that bug again, and it would be worse: railRF
would price current through copper LVS says is a different net.

### 3.1 What moves, and where to

A new namespace `CircuitRF.Design.Layout.Extraction`, below the UI firewall beside the rest of
`src/Design`, holding what is common. **Nothing changes meaning in the move.** Each type keeps its
current behaviour bit for bit, and railRF's existing tests are the gate on that.

| Moves to `Layout/Extraction` | From | Why it is common |
|---|---|---|
| `CopperPieces` (was `PdnCopperPieces`) | `Pdn/PdnLayoutNets.cs` | The partition + the point→piece lookup + the "one piece, two names" refusal. LVS needs all three unchanged |
| `LayerRegions.Build` (was `PdnMeshExtractor.BuildLayerRegions`) | `Pdn/PdnMeshExtractor.cs` | Shapes → per-layer unioned `Paths64`. The input to `DrcConnectivity`, for both |
| `PlacedPins.Of` (was `PdnLayoutPads.PadsOf`) | `Pdn/PdnLayoutPads.cs` | Instances → world pins via `CellPins` + `LayoutInstanceTransform`. **The projection neither may copy** |
| `PlacedPin` (was `PdnPad`) | `Pdn/PdnAttachments.cs` | refdes, pin, coordinate, and a **source** tag |
| `Conductors.Of` (was `PdnRailRegions.PdnConductor` construction) | `Pdn/PdnRailRegions.cs` | Which drawing layers are conductors, per the stackup. Both need the list; only railRF needs its sheet resistance |
| `Regions.Walk` (was `PdnRailRegions.Walk`) | `Pdn/PdnRailRegions.cs` | Island structure of a named net. railRF reports it as rail regions; LVS reports it as *this net is three islands*, which is an open |

**Stays in `Pdn`, because it is genuinely PDN:** sheet resistance, `PdnViaModel`, `PdnInductance`,
`PdnMountingLoop`, `PdnPlaneModes`, the mesh, the graph/skeleton reading, `PdnAssembly`,
`PdnNetlist`. Those price copper. LVS never prices anything.

**Stays in `Drc`:** `DrcConnectivity` itself, and `DrcRegions`. They are `internal` and all three
callers are in one assembly, so there is nothing to widen. **Do not make `DrcConnectivity` public.**

### 3.2 `PlacedPin.Source` grows a third value, and that is the whole of the API change

`PdnPadSource` today distinguishes `BoardNetlist` (a companion file, which is *evidence about* the
artwork and can go stale) from `Artwork` (a projection of the artwork, which cannot). LVS introduces
a third claimant:

```csharp
public enum PinSource { BoardNetlist, Artwork, Schematic }
```

**R-lvs-4. The source tag is required, positional and has no default** — R-ab1-2b's rule, kept for
its reason: adding it broke every construction site, and that is the point. Every report that names
a pin is entitled to say which claim it is reading, and LVS's entire output is a statement about
which claims agree.

### 3.3 The precedence rule INVERTS for LVS, and this is the trap

`PdnLayoutNets`' governing rule is:

> net(pad) = the schematic's own binding for that refdes and that pin, where a schematic resolves;
> else the net stated on the copper the pad lands on

That is exactly right for railRF, which wants the *best available* answer about what a pad is on.
**It is catastrophic for LVS**, which would then ask the schematic what the layout says and get the
schematic's own answer back. Every net would match. The tool would report a clean design, always,
and there would be no symptom.

**R-lvs-5. LVS resolves a pin's net FROM GEOMETRY ONLY.** Not from `Instance.NetBindings`, not from
a `Net` stamped on a shape, not from an `.ipc`. `PlacedPins.Of` is therefore called with its
schematic-facing arguments (`portNamesOf`, `portNetsOf`) **null**, which is an already-supported
call shape — R-ab1-4d's "pads come out named by their pin name alone". The shared API must make this
mode easy to ask for and hard to get wrong:

```csharp
var pins = PlacedPins.Of(view, clayPath, tech, PinNaming.ArtworkOnly, notes);
```

An enum rather than four nullable delegates, so a future caller cannot half-supply the schematic and
silently re-enter the trap.

**What stamped `Net` and a companion `.ipc` ARE good for in LVS:** naming nets in the *report*
(`net 47` is unreadable; `VDD_5V` is not), and seeding the correspondence with an anchor (§6.2).
Both are downstream of the answer and neither can change it. A stamped net that disagrees with the
extracted partition is itself a finding (`lvs.net.label-disagrees`), reported and never obeyed.

---

## 4. Extracting the layout netlist

### 4.1 Devices — four tiers of identity, in precedence order

**R-lvs-6.** A layout device is a `LayoutInstance` (or an array element of one, §4.7) whose resolved
cell is a *device cell* rather than an interconnect cell. The identity used to match it is taken
from the first tier that answers:

**Tier 0 — `SchematicId`.** The explicit correspondence `SchematicToLayoutGenerator` /
`LayoutToSchematicGenerator` already stamp. Honoured as a *proposed pairing*, **never as a verified
one**: it says which schematic component this placement was generated from, and says nothing about
whether the copper reaches the right nets. A `SchematicId` naming a component that no longer exists
is a finding (`lvs.device.dangling-schematic-id`), not a crash and not a silent skip.

**Tier 1 — the designator.** `LayoutInstance.DisplayRefDes` against `Instance.InstanceName`. This is
what a hand-placed board part has, and `DesignatorPool` already answers "what is taken" across a
cell's primary schematic and primary layout views. **Uniqueness is not enforced today** and must be
checked here: two placements sharing a designator is `lvs.device.duplicate-designator`, an error,
reported before any matching is attempted because it makes tier 1 meaningless.

**Tier 2 — structural.** No name on either side, or names that do not correspond: the device is
matched by the graph algorithm in §6.2 alone. This is the classical path and it is the one that
still works when someone renames every part.

**Tier 3 — geometric recognition (opt-in).** For artwork with no instances at all. A recognition
deck declared in the `.ctech` beside `DrcRules`:

```json
"DeviceRules": [
  { "Name": "NiCr resistor", "Kind": "Resistor",
    "Body": "Resistor", "Terminals": "Metal1",
    "Parameters": { "R": "SheetRho * Length / Width" } },
  { "Name": "MIM capacitor", "Kind": "Capacitor",
    "Body": "MIM Metal AND Nitride", "Terminals": ["Metal1", "MIM Metal"],
    "Parameters": { "C": "CapDensity * Area" } }
]
```

**R-lvs-7. The recognition deck reuses `DrcLayerExpr` and `DrcPredicateParser` and introduces no
second expression language.** `Body` is a layer expression, exactly as a DRC rule's region is; the
parameter formulas go through the **one** expression engine (`docs/design/expressions.md`) like
everything else in circuitRF. A second parser here would be a second grammar to document, test and
get wrong.

**Tier 3 ships OFF and stays off until a real MMIC design needs it.** It is specified now because
the extraction's shape must accommodate it — a recognised device and an instance device must be the
same `LvsDevice` to the comparator — and retrofitting that is the kind of change that never happens
later. It is not in the first phases (§10).

#### Which cells are devices, and which are interconnect

**R-lvs-8.** A resolved cell is **interconnect** when it corresponds to no schematic component:
a via-fence cell, a keep-out, a logo, a fiducial, a land pattern placed by the Footprint tool with
no `PartKind` and no designator. Its copper joins the partition and it contributes no device. A cell
is a **device** when any of: it has a `SchematicId`; it has a `DisplayRefDes`; it has a `PartKind`;
its `PCellOrigin.GeneratorId` maps to a component type; its cell folder's `.ccell` declares
`NumPorts > 0` or an `ExternalProvider`.

The last clause is what makes a **user-authored PDK** work with no LVS-specific configuration: a kit
part installed by `PdkPartInstaller` lands as an ordinary cell folder with a symbol, a layout and a
`.ccell`, and it is a device for the same reason every other cell folder is. There is no kit
registration step, no device table to maintain per PDK, and nothing for a kit author to get wrong.

**A cell that is neither** — no ports, no designator, but drawn on conductor layers — is reported
once as `lvs.cell.unclassified` (warning) and treated as interconnect, because treating it as a
device would invent a terminal list out of nothing.

### 4.2 Terminals — the terminal map, and the largest gap in the repo today

This is **G3**, and it is the one thing that genuinely blocks LVS rather than merely costing work.

For a device to be compared, circuitRF must know which layout pin is which schematic port. Today
that correspondence exists in three different strengths:

| Cell origin | What ties layout pin to schematic port | Strength |
|---|---|---|
| Imported component (`ComponentImport`) | `ComponentTerminals.Build` — `SymbolPin.PortIndex i` ↔ `LayoutView.Pins[i-1]`, one numbering decided once for both views | **Guaranteed, by construction** |
| PCell | `pcell-contract.md` R3: "Name must match the symbol's pin, or schematic and layout disagree about connectivity" | **Documented, unenforced** |
| Hand-drawn cell | Nothing | **Absent** |

`LayoutPin.Name` is explicitly allowed to be empty ("an unnamed pin is still a real connection
point, just one the user must identify"), and the schematic side orders a cell's ports by the `Num`
parameter on its `Port` components (`NetExtractor.BuildCellPorts`), which nothing relates to the
order pins were added to a `.clay`. **Position is not a correspondence either** — a symbol's pin
positions and a layout's pad positions have no reason to agree.

**R-lvs-9. A cell's terminal map is persisted in its `.ccell`, is produced by whichever path
authored the cell, and is validated by `check`.**

```json
"Terminals": [
  { "Port": 1, "Name": "G", "LayoutPin": "G" },
  { "Port": 2, "Name": "D", "LayoutPin": "D" },
  { "Port": 3, "Name": "S", "LayoutPin": "S" }
]
```

- `Port` is the number the schematic side already uses (`SymbolPin.PortIndex`, `Port Num=`).
- `LayoutPin` names an entry in the primary `.clay`'s `Pins`. Where several pins are one terminal —
  a bonded ground, a FET's two source pads — it is a **list**, which is the `GND@1`/`GND@2` case
  `ComponentTerminals` already understands.
- **Additive and nullable, no `FormatVersion` bump**, on the convention every field added to
  `.clay` and `.ccell` already follows.

**R-lvs-10. Absent is not a warning, and absent is not a failure — it is a DERIVATION with its
provenance stated.** A cell with no `Terminals` block derives one, in this order, and the LVS report
says which rule answered:

1. **`ComponentTerminals`' own table**, where the cell carries `ImportedFrom` provenance. This is
   the guaranteed case and it needs only to be written down rather than recomputed.
2. **By name**, case-insensitively, between `SymbolPin.Name` and `LayoutPin.Name`. This is what the
   PCell contract's R3 already promises; making the derivation explicit is what turns an unenforced
   sentence into a checked one.
3. **By position in the two lists**, when every pin on both sides is unnamed and the counts match.
   Reported as `lvs.terminals.derived-by-order` at **warning**, always, even on a clean run — it is
   a guess that happens to be right most of the time, and a guess that is never announced is the
   shape of a wrong answer nobody finds.
4. **Nothing.** Counts disagree, or names partly match. `lvs.terminals.underivable`, error, and the
   device is reported as unmatchable rather than matched against a fabricated terminal list.

**BUILT, 2026-09-21** (`brief-lvs-1-terminal-map.md`). `src/Design/Layout/TerminalMap.cs` is the one
place R-lvs-9's question is answered, and it returns the ORIGIN of every answer. One guard had to be
added that this section does not state: `None` covers "the two sides disagree" **and** "there is only
one side", and only the first is R-lvs-10's error — a cell with a symbol and no layout is most cells
in every workspace, and a layout-only cell is what every shipped land pattern is. See
`src/Design/RESOLVED.md`.

**R-lvs-11. `check` validates the terminal map without running LVS.** A cell whose map names a pin
the `.clay` does not have, or leaves a declared port unmapped, is a `check` error today — long
before anyone asks for an LVS. This is exactly R-aut4-2's rule: the validator lives where the GUI
can enforce it, and `check` calls it.

**R-lvs-12. The three authoring paths write the map at creation.** `ComponentImport` already has the
table and need only persist it. `CellCreate` writes an empty one. The PCell path writes it from the
generator's own pins, whose names R3 already constrains. Nothing is retrofitted onto existing cells:
the derivation above is what they get, permanently and correctly.

### 4.3 Nets — the copper partition

**R-lvs-13.** A net is a connected piece of the partition `DrcConnectivity.Extract` produces, plus
every terminal whose pin lands on it. Unchanged from the DRC and railRF reading, including the
1 DBU touch dilation (a via landing exactly on a metal edge is a real connection) and including the
rule that **a plated barrel joins every conductor it passes, not only its two span ends** — the
defect that read a four-layer board's inner power plane as a galvanically separate island while the
picture showed it plainly connected.

A terminal lands on a net by point-in-piece lookup **on the pin's own layer** (R-ab2-2d). A pin on a
layer the stackup does not describe reaches nothing; that is `lvs.pin.no-copper`, an open, and the
finding names the layer so the answer is "your technology does not call that layer a conductor"
rather than "your pad is not connected".

**R-lvs-14. A pin that lands on no copper at all is an OPEN, and a pin whose piece carries two
different stamped names is reported but not obeyed** (§3.3). The existing "one connected piece of
copper carries two different net names" refusal is kept verbatim — it is a genuine short or a
genuine mislabel, and nothing downstream can tell which, so nothing picks one.

### 4.4 Ground, and the conductor with no drawing layer

**This is a real gap (G4) and the shipped MMIC technology demonstrates it.** In
`mmic-GaAs_2LM_100um.ctech`, the `Backside Metal` conductor has `DrawingLayers: []`. A backside via
spans `Metal1` → `Backside Metal`, and the thing it lands on is *not drawn*. Extracted naively,
every backside via terminates in mid-air and every grounded device reads as open — on the one
technology an MMIC designer is most likely to start from.

The schematic side has no such ambiguity: a `Ground` component makes the net `"0"`, unconditionally
and before net labels are applied.

**R-lvs-15. A `StackupKind.Conductor` entry with `IsGroundReference: true` is the global reference
net, whether or not it has drawing layers.** The flag exists already — it was added so a microstrip's
substrate resolution would not mistake an MMIC's second metal for ground — and this is its second
reader. Concretely:

- Every piece on a ground-reference conductor's drawing layers, if it has any, is net `"0"`.
- A via whose span reaches a ground-reference conductor terminates on `"0"` **even where that
  conductor draws nothing**, because the stackup is the statement that the metal is there.
- A technology with **no** ground-reference conductor and a schematic that grounds something is
  `lvs.ground.no-reference-conductor`, a warning naming the flag, with every ground terminal then
  read as an ordinary open. It must be a warning and not an error: a die with no backside metal,
  grounded only through bondwires to a package, is a real and correct design.

**R-lvs-16. When the reference conductor draws nothing, the run SAYS SO — every time, on a clean
run, unasked** (owner, 2026-09-21). `lvs.ground.reference-undrawn` at **info**, naming the stackup
entry and how many terminals reached ground through it:

> *Ground came from the stackup, not from the artwork: 'Backside Metal' is the ground reference and
> draws no layer, so 34 vias were read as reaching net 0. Nothing on your drawing shows this
> connection.*

This is the one inference in the whole extraction that a user cannot see on their own screen — the
metal is not drawn, so there is nothing to look at and nothing to select. An inference that
invisible has to announce itself, or the first time it is wrong (a technology whose ground reference
is mis-flagged) the design reads as perfectly connected and the tool is the reason nobody noticed.
It is INFO rather than a warning because on a correct MMIC it is the normal, expected state.

On a PCB this costs nothing and changes nothing: the ground pour is drawn, so it partitions like any
other copper and the flag simply names which net it is.

**BUILT, 2026-09-21** (`brief-lvs-3-layout-netlist.md`), **with R-lvs-15's first bullet deliberately
NOT built.** Three of the four shipped PCB technologies flag their BOTTOM COPPER as the ground
reference — correctly, for what the flag was added for — and a two-layer board routes signals there,
so "every piece on a ground-reference conductor's drawing layers is net 0" turns every bottom-side
trace into ground and the board into one short, with no symptom but a passing LVS. The shipped
four-layer technology flags Bottom Copper beside its real inner plane, so no rule keyed on the flag
alone can tell a plane from a routing layer. **Only an UNDRAWN reference is inferred**, which is the
gap (G4) this section is actually about; a reference that draws is ordinary copper and the partition
reads it from the artwork. The other three bullets and R-lvs-16 are built as written. See
`src/Design/RESOLVED.md`.

### 4.5 Hierarchy

**R-lvs-17. LVS extracts hierarchically and compares hierarchically, with a flat fallback it names.**
DRC v1 runs flat, on the elaborated geometry, with a 500,000-shape ceiling — the right answer for
DRC, where a rule is about *geometry* and the hierarchy is irrelevant to it. It is the wrong answer
here for two reasons: flattening destroys the instance identity the whole comparison is built on
(G2), and a design with a hundred placements of one cell would pay for that cell a hundred times.

The reading is:

1. **Extract each distinct resolved cell ONCE**, in its own coordinate frame, producing a cell-level
   `LvsNetlist` whose *boundary* is its `LayoutPin` list. Cache it, keyed by a content hash of
   (cell directory, primary `.clay` mtime, resolved technology identity, resolved PCell parameters)
   — the same key shape `GeneratedCellStore.BuildCellName` and `CellLayoutResolver` already use.
2. **At each level, stitch:** a sub-cell's boundary pin, transformed into the parent's frame by
   `LayoutInstanceTransform`, is merged into whatever parent net the point lands on. A sub-cell
   instance becomes one `LvsDevice` whose terminals are its boundary pins, and a *hierarchical*
   comparison stops there; a *flattening* comparison substitutes the sub-cell's own netlist in.
3. **Compare the corresponding levels** against the schematic's own `Library` cells. This is the
   comparison that makes a 3,000-part board tractable, because 3,000 parts of 40 types is 40
   extractions and 40 small comparisons plus one board-level one.

**R-lvs-18. A sub-cell whose copper touches the parent's copper anywhere OTHER than a declared pin
breaks the hierarchical reading, and is reported rather than absorbed** (`lvs.hierarchy.undeclared-
contact`, error). This is the classic hierarchical-LVS failure and the only honest responses are to
report it or to flatten that one cell. circuitRF does both: it reports, and `--flatten-cell <name>`
(and a per-cell `.ccell` flag) is the escape hatch. Silently absorbing it would mean the hierarchy
says one thing and the copper another, which is the whole class of defect this tool exists to find.

**R-lvs-19. Flat is always available and always correct**: `circuitrf lvs --flat` extracts through
`LayoutDesignFlatten` and compares one big graph. It is the reference the hierarchical reading is
gated against (§9.3), and it is what a design with pervasive undeclared contact falls back to.

### 4.6 The wBond — the case that has no precedent in either reading

A wBond is the one component whose two views are structurally unlike anything else:

- **Schematic side:** one component, M coupled branches, one per array; array *k* runs from array
  *k*'s input node to its output node, plus the reference conductor of §5.4 and, with capacitance
  on, three shunt/bridge capacitors per array. Its ports are therefore **2M** (+ reference), ordered
  by array.
- **Layout side:** a `.wBond` sidecar beside the `.clay`, holding `WireArray`s of `Wire`s, each a
  polyline of `Point3` in **nanometres**. There is no net on a wire, no pad binding on a foot, and
  no `LayoutPin` anywhere in the picture.

**R-lvs-20. A wire's FOOT resolves to a net by the same point-in-piece lookup every pin uses, after
the nm → DBU conversion, and the conversion is stated at the call site.** The nm↔DBU bridge fails
*silently* at the 1000 DBU/µm default — the two numbers coincide — so a test of it must use geometry
off both axes and a coordinate that is not a round number of microns. This is a recorded scar, not a
hypothetical.

**R-lvs-21. An array is matched to its port pair BY NAME, never by index.** `WBondPlacement`'s
existing array-drift report (§9.2/WB35a, `WBondPlacement.DriftBetween`) exists for exactly the
failure this would otherwise produce: reorder the array list and every pin keeps its position while
its name moves, so the wires now connect to different arrays. LVS consumes that report rather than
re-deriving it, and a drifted wBond is a finding before any net is compared.

**R-lvs-22. Which of the two wire sources LVS reads is the instance's own `Source` parameter
(Carried or Linked), and the report says which.** §9.7's per-instance choice is what the *engine*
simulates; LVS must verify the same wires the engine will use, or it verifies a design nobody runs.
A Carried instance whose payload has drifted from its cell's `.wBond` is `lvs.wbond.payload-drift`,
a warning naming "Update Schematic from wBond Layout" as the remedy — the state §9.6 calls normal
and recoverable.

**R-lvs-23. A wBond is what makes an ASSEMBLY the unit of LVS rather than a cell.** Wires join two
different dies' copper, or a die to a package lead, and no single `.clay` contains both. The
extraction root is therefore the cell that *places* the dies — its instances are the dies, its own
shapes are the package/leadframe copper, and the `.wBond` beside it holds the wires that join them.
Each die is extracted as a sub-cell by §4.5 and its bond pads are its boundary pins. Nothing new is
required for this; it is §4.5 with a wBond in it.

### 4.7 Instance arrays

**R-lvs-24. An `M×N` array is `M×N` devices, each with its own identity** — `R1[0,0]`, `R1[0,1]`, …
— and the correspondence must find `M×N` schematic components. A schematic with one `R1` against a
layout array of six is an error naming the count, not a silent one-to-one.

This is the honest reading and the alternative — an array is one device — is wrong for the case
arrays actually exist for: a via fence is interconnect (§4.8, no device at all), while a thermal-pad
array of six capacitors is six capacitors.

**It is also less startling than it sounds, because §6.3 catches the common shape.** Six identical
capacitors on one net pair are a parallel group, so they reduce to one device on the layout side and
match the schematic's one — and the finding, if any, is that the value is 6× rather than that the
count is wrong. The count error survives only where the array elements land on *different* nets,
which is the case where it is genuinely the right answer. The finding text names "Explode Array" as
the remedy for the rest.

### 4.8 Two technologies in one assembly

A die on a board has its own `.ctech`, its own DBU resolution, and its own layer numbering.
`LayoutFlatten` already reconciles layers across a technology boundary, and only at the **direct**
sub-cell level.

**R-lvs-25. Copper never connects across a technology boundary by layer coincidence.** Two
technologies' `Metal1`s are not the same metal, and a layer-number collision between an MMIC's
layer 1 and a board's layer 1 is a coincidence of integers. Across a boundary, connection is only
through: a declared boundary pin of the sub-cell that lands on parent copper, a via whose stackup
entry spans the boundary, or a bondwire foot. Anything else in the sub-cell is that cell's internal
business. A layer reconciliation pending at the boundary makes the sub-cell **unextractable**, with
the same refusal `LayoutDesignFlatten` already gives — reported, never guessed at.

---

## 5. The schematic side

Mostly done. `NetExtractor` produces a hierarchical `TestBench` + `Library`; `Elaborator` flattens
it, resolves every parameter top-down, and numbers nodes.

**R-lvs-26. LVS reads the schematic through the same `.cnl` round trip the GUI's Simulate performs**
— `NetExtractor.Extract → CnlWriter.Write → CnlReader.Read`, in memory, via
`src/Cli/CircuitSource.cs`. §10.3's finding binds here verbatim: skipping it reports errors the
application does not have, because the two readers disagree about bare words.

**R-lvs-27. The comparison runs on the DESIGN model, not on the elaborated netlist**, with the
elaborated one used only to resolve parameter values. The elaborator uniquifies nets by instance
path and flattens hierarchy; LVS needs the hierarchy intact (§4.5) and needs to report in the user's
own names. What it takes from elaboration is §6.4's resolved values and nothing else.

**R-lvs-28. Non-electrical and marker components are excluded, from one list shared with
`NetExtractor`.** `VAR`, `MEAS`, `Ground`, `Pin` (a connectivity marker the elaborator already
skips), and disabled components. The list must be shared rather than restated — a component that is
electrical to one and not the other produces a mismatch whose cause is in neither document.

**Ports and Terms are a policy, not an omission.** A `Port` or `Term` in the testbench is a
measurement fixture, not a device on the die. **R-lvs-29: the default compares the DUT cell, not the
testbench** — `circuitrf lvs <cell>` compares a cell's own schematic view against its own layout
view, and a cell port corresponds to a layout boundary pin. `--testbench` includes the fixture, for
the designer who has actually drawn the launches.

---

## 6. The comparison

### 6.1 A canonical device type, resolved once

Four namespaces name device types today: `SymbolKind` (schematic built-ins), `CellRef` (both sides),
`PCellOrigin.GeneratorId` (layout PCells), and `PartKind` / `FootprintRef` (board parts).
`LayoutToSchematicGenerator.ReverseGeneratorMap` is the seed of a bridge and covers six microstrip
generators.

**R-lvs-30. One function resolves a canonical `DeviceType` for either side, and both sides call it.**
Precedence: the resolved **cell directory** where both sides reference a cell (an absolute path is
an unambiguous identity and needs no name matching); else the generator-id ↔ `SymbolKind` map,
extended from six entries to every registered generator; else `PartKind`; else the built-in kind.

**R-lvs-31. Two devices of different canonical type never match**, and a type mismatch is reported
as such rather than as two unmatched devices — "R1 is a resistor in the schematic and a capacitor in
the layout" is one sentence a user can act on; two "unmatched" lines are a puzzle.

### 6.2 Anchored correspondence, then colour refinement

**R-lvs-32. The algorithm is iterative partition refinement, seeded by anchors.** Named in full
because it is the part that must be fast and must terminate: it is the standard colour-refinement
(Weisfeiler–Leman) fixed point, run over the bipartite device/net graph, with automorphism
tie-breaking. It is near-linear and it is what makes thousands of components a sub-second
comparison rather than a backtracking search.

1. **Anchor.** Every tier-0 and tier-1 pairing from §4.1, plus every pairing implied by a net name
   present and unambiguous on both sides (a cell boundary port, a stamped net that agrees with a
   schematic net label). Anchors are *hypotheses* — each one is verified by the refinement, and an
   anchor that the refinement contradicts is itself a finding (`lvs.anchor.contradicted`), which is
   usually the single most useful line in the whole report because it names a mis-wired part by the
   designer's own name for it.
2. **Colour.** Initial colour of a device = hash(canonical type, terminal count, parameter class,
   anchor id-or-none). Initial colour of a net = hash(terminal count, anchor id-or-none, whether it
   is net `"0"`).
3. **Refine.** Recolour each object from the sorted multiset of its neighbours' colours **and the
   terminal position each neighbour is attached through** — a resistor is symmetric but a FET is
   not, and losing the pin index would match a drain to a source. Iterate to a fixed point.
4. **Match.** Colour classes of size 1 on both sides are a correspondence. Classes of size *n* > 1
   on both sides with identical colours are an **automorphism** — *n* genuinely interchangeable
   objects, which is the common case for paralleled fingers and decoupling caps. Pair them by a
   deterministic tie-break (provenance order: instance path, then designator) and **say so**:
   `lvs.match.by-symmetry` at info, because the pairing is arbitrary and a later report naming
   "C7" may mean the one the designer calls C9.
5. **Diverge.** Classes present on one side and not the other are the mismatch. **Report at the
   point of divergence, not at the end**: the smallest colour class that differs, with the path from
   the nearest anchor to it. "Everything matched except 412 devices" is not a report; "the gate of
   M3 reaches VDD, and the schematic says it reaches n17" is.

**R-lvs-33. The comparison is deterministic.** Same two documents, same result, same order, every
time and on every platform — the rule `DrcRunResult` already states for its violation ordering. All
hashing is over ordered, culture-invariant strings; no `GetHashCode`, no dictionary enumeration
order, no parallel non-determinism.

### 6.3 Reduction policy — what it is, and the default users expect

**What reduction is.** The two documents can describe one circuit with different numbers of objects,
legitimately. A power FET is one symbol on a drawing and eight identical fingers in the artwork; a
100 pF bypass is one symbol and two 47 pF parts side by side; a bias resistor is one symbol and two
in series because the board only had two footprints of the right power rating. Compared object for
object all three are mismatches, and all three are correct designs. **Reduction** is the pass that
collapses such groups — on *both* netlists, with the same code — so the comparison happens between
two circuits in the same reduced form.

Every production LVS does this and does it by default. Turning it off is what produces the
astonishment, not the reverse: a designer whose fingered FET reports as "seven extra devices" stops
running the tool.

**R-lvs-34. Reduction runs on BOTH netlists, through one function, before matching.** Never on one
side "to make it look like" the other. This is R-lvs-2's rule applied to the reduction pass: a
reducer that can tell the two sides apart will eventually treat them differently.

**R-lvs-35. What is reduced, by default:**

| Collapse | Applies to | Condition |
|---|---|---|
| **Parallel, two-terminal** | `R`, `C`, `L` | Same canonical type, same net pair. Values sum as the physics does — `C` and `1/R` and `1/L` add |
| **Parallel, multi-terminal** | any device type | **Every** terminal on the same net as its counterpart's. This is the fingered-FET and the paralleled-transistor case |
| **Series, two-terminal** | `R`, `C`, `L` | The shared node has **degree exactly 2**, is not a cell port, carries no net label, and is named by no `measure` line. `R` and `L` sum; `C` adds as `1/C` |

**R-lvs-36. What is NEVER reduced, and the RF reason for the exclusion:**
- **Distributed and behavioural elements** — `MLIN` and the rest of the microstrip family, `SnP`,
  `SDD`, `Tuner`, `Port`, `Term`, and the wBond. Two series `MLIN`s are one line only if their `Z0`s
  agree, and a comparison tool must not be performing that arithmetic on the user's behalf;
  collapsing an `SnP` is not even definable.
- **Series anything-but-R/C/L.** Two FETs in series are a cascode, not a bigger FET.
- **Across a node anything else touches.** Degree-2 is the whole safety argument: if nothing else
  reaches the node, the two parts are electrically indistinguishable from one. A probe, a port, a
  net label or a measurement reference makes the node observable, and an observable node that
  vanishes is a silently changed circuit.
- **Across a node a `measure` line names.** Reducing it away would quietly invalidate a measurement
  that still parses and still runs.

**R-lvs-37. Three further collapses that are not reductions at all**, listed here because they look
like ones:
- **`Pin` markers** — connectivity markers; the elaborator already skips them.
- **Copper joining two nets** — that is the partition, not a reduction.
- **A part declared a jumper** (`PartKind` = a shorting link, a 0 Ω) — collapsed on both sides, and
  reported as `lvs.reduce.jumper` at info either way, because a jumper that is absent from one
  document is exactly the kind of thing a designer wants told.

**R-lvs-38. Every reduction is reported, and every finding UN-reduces.** A merged group appears once
in the run summary (`lvs.reduce.parallel`, `lvs.reduce.series`, info, with counts by type), and any
finding that involves a merged group names the **individual** devices and their designators. A
report that can only say "the merged group at net 14" is one a user cannot act on. This is also what
makes the default safe to have on: nothing is hidden, it is only counted differently.

**R-lvs-39. The merged multiplicity is compared, not discarded.** Four fingers in the layout against
a schematic device declaring `Nf=3` (or `M=3`) is a **property** finding, not a match — the merge
makes the comparison possible and §6.4 is where it is judged. Where the schematic declares no
multiplicity parameter at all, four-against-one is `lvs.reduce.multiplicity-unstated` at warning.

**R-lvs-40. `--no-reduce` turns the whole pass off**, for the designer who wants the object-for-object
reading, and the run summary always states which mode produced it. A result whose reduction mode is
not on its face is a result two people can read differently.

### 6.4 Property comparison

Runs **after** topology matches, never before — comparing parameters of devices that may not
correspond produces noise proportional to the size of the design. It compares the **reduced**
devices (§6.3), because that is what corresponds; a merged group's value is the summed one and the
report names the parts it was summed from.

**R-lvs-41. A property is compared only where BOTH sides claim a value.** The layout side claims one
when the device is a PCell (`PCellOrigin.Parameters`, resolved SI values, and `IsComputed` already
says which the generator derives from geometry rather than reads) or when tier-3 recognition
measured one. A land-pattern instance claims nothing about resistance, and a comparison against
nothing is not a finding — it is `lvs.property.layout-silent` at **info**, said once per device
type rather than once per device.

**R-lvs-42. Tolerance is per unit dimension, declared in the technology, defaulted sanely, and
stated in the report.** `UnitDimension` already exists on `CcellParameter`. A 1 % default on
resistance and capacitance, exact on integers and enumerations, and a geometric dimension compared
in DBU with a tolerance of one DBU. The report prints both values and the tolerance that was
applied, never "mismatch".

**R-lvs-43. A parameter the generator DERIVES is compared as a derived quantity and the report says
so.** `PCellOrigin.IsComputed` is the flag. A MIM cap whose C comes from its own w and l cannot
disagree with its geometry; it can disagree with the *schematic's* C, and that is a design error
worth naming precisely ("the layout's geometry gives 1.82 pF; the schematic asks for 2.0 pF").

### 6.5 Shorts and opens need a PATH, not a partition

**R-lvs-44. The union-find retains its merge edges.** `DrcConnectivity` today discards them: it
unions and renumbers, and what is left is membership. Membership answers "are these two pins on one
net" and cannot answer "**why**", which is the only question a designer with a short actually has.

Keeping one edge per union — (piece A, piece B, the via or the touch that joined them, and its
coordinate) — makes a shortest path between two pins recoverable by a breadth-first walk over a
graph with as many edges as there were merges. The cost is one small list; the benefit is
`lvs.net.short` reporting *"VDD and VOUT are joined through a 0.2 mm neck of Metal1 at
(1.204 mm, 3.881 mm)"* with a marker on it, rather than *"VDD and VOUT are shorted"*.

**R-lvs-45. An open is reported as the PARTITION of the schematic net**, using `Regions.Walk`'s
existing island structure: "net VDD is three islands — U1.1 and C4.2 here, R7.1 alone there, and
J1.3 alone there", with a marker per island. That is the same output railRF already produces for a
rail, and railRF's own note that this alone has caught real problems applies unchanged.

---

## 7. Performance — thousands of components

The target: **a 3,000-component board or a 500-device MMIC compares in a few seconds**, and the
progress and cancellation ride `RunHost`'s `RunControl` like `em` and `render` do, so a big one can
be stopped.

**R-lvs-46. What makes it fast is hierarchy, and hierarchy is a correctness feature that happens to
pay for the performance.** 3,000 parts of 40 types is 40 cell extractions (cached, content-keyed)
plus one board-level partition — not 3,000. This is the dominant factor and no micro-optimisation
substitutes for it.

Then, in measured-cost order:

**R-lvs-47. Index the point→piece lookup.** `PdnCopperPieces.PieceAt` is a linear scan over every
piece, with a bbox rejection, called once per pin. At railRF's scale — one rail, tens of anchors —
that is free. At 3,000 parts × 4 pins × a partition with thousands of pieces it is the whole run.
Reuse `LayoutSpatialIndex` over piece bounding boxes; the Clipper2 `Contains` test stays as the
exact answer after the index narrows the candidates. **This is a promotion, not a rewrite**: the
indexed lookup replaces the scan inside the shared `CopperPieces`, so railRF gets it too.

**R-lvs-48. Union per layer once, over the FLAT geometry of one cell** — not per placement, and not
per shape pair. `LayerRegions.Build` already does this; the hierarchy is what keeps its input small.

**R-lvs-49. Colour refinement is near-linear and must stay that way.** Sort neighbour colour
multisets with a counting sort over the previous iteration's dense colour ids, not a comparison sort
over strings. Cap the iteration count at the graph diameter and report if the cap binds.

**R-lvs-50. A ceiling, with a refusal rather than a hang** — `DrcEngine`'s bargain, reused: above
`MaxDevices` / `MaxShapes` (defaulting to the existing 500,000) the run refuses and says what it
would have needed. A pathological design costs a message.

**R-lvs-51. Gate on COUNTERS, not on wall-clock.** The structural property is "one extraction per
distinct cell, not per placement" and "one point-in-piece query per pin, answered through the
index" — both are counters, both catch the regression that matters (an accidental O(n²)), and
neither measures the machine. This is the repo's standing rule and it applies here without
exception. The one timed measurement, if any, goes in `Category=Benchmark`.

---

## 8. Results, surfaces and persistence

### 8.1 The result model

`LvsRunResult` mirrors `DrcRunResult`'s shape and its discipline: the findings, waived ones included
and marked; what was actually compared (device counts per side, net counts, cells extracted, cells
taken from cache); the technology that was resolved and named (a workspace with two processes has a
default that may not be the one the designer has in mind); and the diagnostics for everything the
run could not do, stated rather than dropped.

Every finding is a `Diagnostic` with a stable `lvs.` id and typed arguments — the ids used through
this document are the contract; the sentences are not.

### 8.2 Waivers

**R-lvs-52. Waivers are per-finding, persisted on the `.clay` that was checked, and visible** —
`DrcWaiver`'s rules verbatim, including that a waived finding is still reported and merely not
counted.

**R-lvs-53. The key is the CORRESPONDENCE, not a bounding box.** A DRC waiver keys on the marker's
exact bbox, deliberately: move the shape and the waiver stops applying, because a waiver names a
place. An LVS waiver names a *relationship* — "R7's pin 2 is deliberately not connected" — and that
relationship survives moving R7. The key is (finding id, the schematic-side identity, the terminal),
which is stable under every layout edit and correctly stops applying when the schematic changes.

### 8.3 Surfaces

**GUI.** A results panel on the DRC panel's pattern — click a finding, zoom to its marker on the
system layer, cross-probe the corresponding schematic component. Cross-probing is the feature that
makes LVS usable rather than merely correct, and the correspondence is what makes it possible: once
LVS has matched R7 to R7, selecting one selects the other.

**CLI.**

```
circuitrf lvs <path> [--flat] [--flatten-cell <name>] [--testbench] [--no-reduce]
                     [--severity warning|error] [--json] [-o report.txt]
```

**R-lvs-54. LVS is its own verb and is NOT folded into `check`.** R-aut4-1 is explicit that `check`
must be cheap enough to call after every edit and stops at elaboration; an LVS on a real board is
seconds, not milliseconds. What `check` *does* gain is §4.2's terminal-map validation, which is
cheap, static, and exactly the kind of rule R-aut4-2 says must live where the GUI enforces it too.

**R-lvs-55. The verb holds no comparison logic**, on `Authoring.cs`' terms — argument parsing,
refusals, reporting, and one call into `src/Design`. Held by a comment-stripped source scan, like
every other verb.

**MCP / `serve`.** Falls out of the verb with no extra work, which is the point of the automation
architecture: an agent that authors a layout can ask whether it matches the schematic.

---

## 9. The gaps — what would block an implementation today

Ranked by how much they block, with what closes each. Six of the eleven are small.

| # | Gap | Blocks | Closes with |
|---|---|---|---|
| **G3** | **No terminal map.** Layout pin ↔ schematic port is guaranteed only for imported parts; documented-but-unenforced for PCells; absent for hand-drawn cells. `LayoutPin.Name` may be empty by design | **Everything.** Without it a device's terminals cannot be compared | §4.2: persist `Terminals` in `.ccell`, derive with stated provenance, validate in `check`, write it at the three authoring sites |
| **G2** | **`LayoutDesignFlatten` returns bare `LayoutShape`s** — instance path and pin identity are destroyed at the one function both DRC and Gerber use | Hierarchical extraction, device identity, every report that names a part | §4.5: a hierarchical walk that never flattens by default, plus an instance-tagged flatten for `--flat` |
| **G4** | **Ground is implicit and often undrawn.** The shipped MMIC technology's `Backside Metal` has no drawing layers, so every backside via terminates in mid-air | Every grounded MMIC device reads as open — on the starter technology | §4.4: `IsGroundReference` is the global reference, drawn or not. The flag already exists |
| **G7** | **`PieceAt` is a linear scan** and there is no index over pieces | Nothing functionally; it is the run time at scale | §7 R-lvs-47: `LayoutSpatialIndex` inside the shared `CopperPieces`. railRF benefits too |
| **G9** | **The union-find discards its merge edges**, so a short can be detected but not localised | A usable short report | §6.5 R-lvs-44: retain one edge per union. Small change, large payoff |
| **G1** | **Nothing stamps `Net` in layout.** `SchematicToLayoutGenerator` never did, and the persisted ratsnest was withdrawn. `Net` is a user assertion only | Nothing — and this is *good*: it forces R-lvs-5's geometry-only reading. But it means there is no anchor to bootstrap from on a hand-drawn board | Nothing required. Use `Net` for report naming and anchoring only, and report disagreement |
| **G5** | **Instance arrays** — one `LayoutInstance` with `Rows×Cols` is N physical devices and one object | Correctness of any design using arrays for real parts | §4.7 R-lvs-24: N devices with derived identities; state the rule in the finding |
| **G6** | **The wBond has no pad binding at all** — wire feet are bare nm coordinates with no net and no terminal | Every multi-die assembly | §4.6: foot → point-in-piece after nm→DBU (watch the silent 1000 DBU/µm coincidence); arrays matched by name through the existing drift report |
| **G16** | **Four namespaces for device type**, bridged by a six-entry map | Type-aware matching and the "R is a C" message | §6.1 R-lvs-30: one canonical resolver, cell directory first |
| **G8** | **No parameter source on a non-PCell layout device.** A land pattern claims nothing | Property checking on boards | §6.4 R-lvs-41: compare only where both claim; info, not a finding, otherwise |
| **G15** | **`SchematicId` is unverified and `RefDes` uniqueness is unenforced.** `DesignatorPool` can answer the question; nothing asks it at the right time | Tier-0 and tier-1 identity are silently unreliable | §4.1: check both before matching; a duplicate designator is an error reported first |

**Two further gaps that are policy rather than code**, listed so they are decided rather than
discovered: multi-technology assemblies (§4.8 — connection across a boundary is only through
declared interfaces, never layer coincidence), and hierarchical undeclared contact (§4.5 R-lvs-18 —
reported, with a per-cell flatten as the escape hatch).

**What is NOT a gap, and is worth saying because it is where the effort would normally go:** the
copper partition, the via bridging (including the every-conductor-the-barrel-passes rule that was
learned the hard way), the pad projection through `CellPins` and `LayoutInstanceTransform`, the
hierarchical schematic netlist, parameter resolution, the diagnostic model, the waiver model, the
violation-and-marker plumbing, the results panel pattern, and the headless verb anatomy. All
present, all in use, all tested.

---

## 10. Build order

**Implementation:** [`docs/sonnet-briefs/brief-lvs-0-overview.md`](../sonnet-briefs/brief-lvs-0-overview.md)
— 15 briefs, written 2026-09-21. The overview fixes the boundaries between them and records the
nine things that came out of reading the code rather than out of this note; the table below is the
order and the reason for it.

**The whole of LVS lands before any of it ships** (owner, 2026-09-21), so the list below is a
BUILD ORDER, not a set of release gates. Nothing here is a feature flag waiting for a second
opinion and no phase is a place to stop. What the ordering buys is that each step ends green, each
step has an oracle, and the two riskiest steps (LVS-1's promotion, LVS-3's hierarchy) are gated
against something that already works rather than against a new assertion.

- **LVS-0 — the terminal map (G3).** `.ccell` `Terminals`, the four-rule derivation with its stated
  provenance, `check` validation, and the three authoring paths writing it. No comparison yet.
  First because everything else needs it, and because `check` gains a real rule on its own account.
- **LVS-1 — the shared extraction (§3).** Promote `CopperPieces`, `LayerRegions`, `PlacedPins`,
  `PlacedPin`, `Regions.Walk` into `Layout/Extraction`; add `PinNaming.ArtworkOnly`; index the
  point→piece lookup (G7); retain the merge edges (G9). **railRF's existing tests are the gate** —
  its behaviour must not change by one number, and if it does, the promotion is wrong.
- **LVS-2 — flat comparison, single technology, no hierarchy.** Instance-tagged flatten (G2),
  canonical device type (G16), `SchematicId`/designator anchoring with the duplicate check (G15),
  the reduction pass (§6.3), colour refinement, shorts with paths and opens with islands, ground
  (G4). **This is the step that first says yes or no**, and the owner's own hand-tested board with
  footprints assigned is the validation for it.
- **LVS-3 — hierarchy (§4.5).** Per-cell extraction with the content-keyed cache, boundary
  stitching, undeclared-contact reporting, `--flatten-cell`, arrays (G5). The 3,000-part board
  becomes fast here, and **LVS-2's `--flat` result is the oracle** every hierarchical run is gated
  against — same findings, same order.
- **LVS-4 — properties (§6.4) and the GUI surface.** Tolerances, derived parameters, merged-group
  multiplicity, waivers, the results panel, cross-probing.
- **LVS-5 — assemblies and the wBond (§4.6, §4.8).** Multi-die, multi-technology, bondwires.
- **LVS-6 — tier-3 geometric recognition (§4.1).** Built, shipped, and **default off** — it exists
  for artwork that carries no instances, and turning it on for a design circuitRF authored would
  re-recognise devices it already knows.

**Validation.** The five heroes are the wrong anchor here; LVS is not numerical. The right ones are:

1. A shipped example workspace holding a **correct** board and a deliberately **broken** copy —
   one swapped net, one missing part, one short, one wrong value, one un-exploded array — asserted
   finding by finding, ids and all.
2. The **`--flat` vs hierarchical equivalence gate** (LVS-3), which is the only cheap way to know
   the boundary stitching is right.
3. The **`--no-reduce` vs default gate**: reduction must change how a correct design is *counted*
   and never whether it passes.
4. A generated scale fixture (never committed as artwork) proving R-lvs-51's counters — one
   extraction per distinct cell, one indexed query per pin.

---

## 11. Non-goals

Auto-repair of any kind. Back-annotation of extracted nets into the schematic. ERC. Antenna and
density rules (those are DRC). Parasitic extraction (that is the EM engine). Reduction beyond
§6.3's table — in particular, series reduction of anything other than lumped `R`/`C`/`L`, and any
reduction across an observable node. A rule language for *matching* — the matching rules are the
algorithm, not configuration, and every knob on them is a way to make a wrong design pass.

---

## 12. Decisions taken (owner, 2026-09-21)

All six questions this note opened with are answered. They are folded into the body above; this
section keeps the record.

1. **An `M×N` array is `M×N` devices** (§4.7, R-lvs-23). Confirmed. §6.3's parallel reduction takes
   most of the sting out of it: an array whose elements share a net pair merges and matches.
2. **Terminals may be derived by POSITION when both sides are entirely unnamed** (§4.2, R-lvs-10
   rule 3). Confirmed — and it reports `lvs.terminals.derived-by-order` at warning every time,
   including on an otherwise clean run, because it is a guess that is usually right.
3. **The default unit of comparison is the CELL, not the testbench** (§5, R-lvs-29). Confirmed;
   `--testbench` opts in.
4. **Reduction follows what users expect, which is ON** (§6.3, rewritten). The question was raised
   as "no automatic series/parallel reduction" and that was the wrong default: every production LVS
   reduces, and a fingered FET reporting as seven extra devices is how a tool stops being run. The
   answer is the table in R-lvs-35 — parallel `R`/`C`/`L` on a net pair, parallel multi-terminal
   devices with every terminal common (the fingered-FET case), and series `R`/`C`/`L` through a
   node of degree exactly 2 that no port, label or `measure` line observes. Distributed and
   behavioural elements are never reduced, series cascodes are never reduced, and `--no-reduce`
   gives the object-for-object reading. Every reduction is reported and every finding un-reduces.
5. **Staging is a build order, not a release plan** (§10, rewritten). The whole of LVS lands before
   any of it ships, so no phase is a place to stop and no capability waits on a second opinion.
   What the ordering buys is an oracle at each step.
6. **A ground-reference conductor with no drawing layers is still the reference** (§4.4, R-lvs-15),
   **and the run says so every time** (R-lvs-16). The reminder is INFO, unconditional, and names
   the stackup entry and the number of terminals that reached ground through it — because this is
   the one inference in the extraction that the user cannot see on their own screen.

### Still genuinely open

- **§6.4 R-lvs-45's default tolerances.** 1 % on R and C is a guess; the right numbers may differ
  per unit dimension and per market (a PCB resistor is a 1 % part; an MMIC NiCr is not). Worth
  setting against a real design rather than in advance.
- **§4.1 tier 3's recognition deck**, in detail. The shape is fixed (a `DeviceRules` block reusing
  `DrcLayerExpr` and the one expression engine) and the contents are not, because no design has
  needed them yet. Fixing them before there is a design to fix them against is how a rule language
  acquires features nobody uses.
