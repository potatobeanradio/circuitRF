# Brief 3 — the layout netlist: devices, terminals, nets, ground

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs3-n` · **Design note:** [`lvs.md`](../design/lvs.md) §4.1, §4.3, §4.4, §4.7
**Area:** `src/Design/Layout/Lvs/LvsNetlist.cs` (new), `LayoutRead.cs` (new),
`src/Design/Layout/LayoutDesignFlatten.cs`, `src/Design/Layout/Extraction/`
**Depends on:** 1, 2 · **Blocks:** 7, 9, 14

---

## 0. What this brief delivers

A `.clay` plus its technology becomes an `LvsNetlist`: devices with named terminals, nets that came
from geometry alone, and ground. **Flat, one technology, no comparison.**

```
LayoutView ──► TaggedFlatten ──► LayerRegions ──► CopperPieces(+index,+joins)
     │                                                       ▲
     └─ instances ─► device tier (0-2) ─► TerminalMap ─► PlacedPins ──┘
                                                          │
                                                          └──► LvsNetlist
```

---

## 1. `R-lvs3-1` — `LvsNetlist`, and both sides will produce it

```csharp
public sealed record LvsTerminal(int Port, string Name, int NetIndex);

public sealed record LvsDevice(
    string      Path,          // "R1", "U3/M1", "R1[0,2]" — the report's own spelling
    string      Designator,    // may be empty
    DeviceType  Type,
    IReadOnlyList<LvsTerminal> Terminals,
    IReadOnlyDictionary<string, object?> Parameters,   // resolved SI; empty where nothing claims
    LvsProvenance Provenance);

public sealed record LvsNet(int Index, string? Label, IReadOnlyList<(int Device, int Terminal)> Pins);

public sealed record LvsNetlist(
    IReadOnlyList<LvsDevice> Devices,
    IReadOnlyList<LvsNet>    Nets,
    IReadOnlyList<int>       BoundaryNets,   // cell ports, in port order
    IReadOnlyList<Diagnostic> Notes);
```

**`R-lvs3-1a`** One type for both sides (note R-lvs-2). The comparator must not be able to tell
them apart; a comparator that can will eventually treat them differently and the asymmetry will be
a bug nobody can see.

**`R-lvs3-1b`** `Provenance` — the document, the instance path, the source coordinate — is for the
**report** and for brief 7's deterministic tie-break, and for nothing else. The comparator reads it
nowhere in its matching decisions.

**`R-lvs3-1c`** `Path` is the user-facing identity and is stable across runs. It is not a GUID and
not an index.

---

## 2. `R-lvs3-2` — a tagged flatten, beside the existing one

**`R-lvs3-2a`** `LayoutDesignFlatten.Flatten` is **not changed**. Gerber export and DRC depend on
its output byte for byte and neither needs a tag.

**`R-lvs3-2b`** A new `FlattenTagged` returns the same shapes plus, per shape, the **instance path**
it came from (empty for the root's own shapes) and the sub-cell pin it realises, where it does. It
shares `LayoutFlatten.FlattenAllLevels` and the same coordinate walk — it is a driving loop that
keeps a breadcrumb, not a second flattener.

**`R-lvs3-2c`** Same ceiling, same refusal, same cross-technology reconciliation behaviour, same
`UnresolvedInstances` reporting. A design over the ceiling comes back with no netlist and a note
saying so, rather than with a confident netlist over geometry the run never saw — `RailArtwork`'s
own rule (R-ab1-5d).

**`R-lvs3-2d`** Hierarchy proper is brief 9. This brief flattens; brief 9 adds the per-cell reading
and gates itself against this one.

---

## 3. `R-lvs3-3` — which instances are devices

The note's §4.1 tiers. This brief builds 0, 1 and the classification; tier 2 is brief 7's
algorithm and tier 3 is brief 14.

**`R-lvs3-3a` Classification.** An instance is a **device** when any of: `SchematicId` is set;
`DisplayRefDes` is non-null; `PartKind` is set; `PCellOrigin.GeneratorId` resolves through brief
4's `DeviceType` map; the resolved cell's `.ccell` declares `NumPorts > 0` or an
`ExternalProvider`. Otherwise it is **interconnect**: its copper joins the partition and it
contributes no device.

**`R-lvs3-3b` A user-authored PDK needs no registration.** A kit part installed by
`PdkPartInstaller` is an ordinary cell folder with a symbol, a layout and a `.ccell`, so the last
clause makes it a device for the same reason every other cell folder is. There is no device table
to maintain per kit and nothing for a kit author to get wrong.

**`R-lvs3-3c` Neither, but drawn on conductor layers** — no ports, no designator, real copper —
is `lvs.cell.unclassified`, warning, **once per cell type rather than per placement**, and treated
as interconnect. Treating it as a device would invent a terminal list out of nothing.

**`R-lvs3-3d` Tier 0 is a proposal, never a verification.** `SchematicId` says which component this
placement was generated from. It says nothing about whether the copper reaches the right nets, and
brief 7 verifies it like any other anchor. A `SchematicId` naming a component that no longer exists
is `lvs.device.dangling-schematic-id` — a finding, not a crash and not a silent skip.

**`R-lvs3-3e` Designator uniqueness is checked BEFORE anything is matched.** Two placements sharing
a `DisplayRefDes` is `lvs.device.duplicate-designator`, error, reported first, because it makes
tier 1 meaningless and every downstream finding derived from it misleading. `DesignatorPool`
already answers the question; nothing asks it at the right time today.

---

## 4. `R-lvs3-4` — arrays

**`R-lvs3-4a`** An `M×N` array is `M×N` devices (owner, note §12.1), each with path
`<Designator>[r,c]` and each with the array element's own transformed pins.

**`R-lvs3-4b`** All of them carry the **same** designator, which is correct and is what
`PlacedPins` already does for pads. The path, not the designator, is the identity.

**`R-lvs3-4c`** An array of an **interconnect** cell — the via fence, the thermal via field —
contributes no devices at all and only copper. This is the shape that makes arrays look wrong if
`R-lvs3-3a` is got wrong, so it is a gate.

---

## 5. `R-lvs3-5` — terminals and their nets

**`R-lvs3-5a`** Per device: `TerminalMap.Resolve` (brief 1) for the resolved cell, giving port
numbers, names and the layout pins each covers. Origin `None` makes the device **unmatchable**:
it is emitted with an empty terminal list, `lvs.device.no-terminal-map` is reported naming the
cell, and brief 7 never matches it. It is not dropped — a device the comparison cannot handle must
still appear in the count.

**`R-lvs3-5b`** Per layout pin: `PlacedPins.Of(..., PinNaming.ArtworkOnly, ...)` — brief 2's mode,
written explicitly. **Nothing a schematic says may reach this answer** (note R-lvs-5).

**`R-lvs3-5c`** Per pin: `CopperPieces.PieceAt` **on the pin's own layer** (R-ab2-2d). A pin landing
on nothing is an **open**: `lvs.pin.no-copper`, and the finding **names the layer**, so the answer
is *"your technology does not call Metal3 a conductor"* rather than *"your pad is not connected"* —
two very different fixes.

**`R-lvs3-5d`** A terminal covering several layout pins (a bonded ground, a FET's two sources) is
one terminal. Where its pins land on **different** nets, that is a finding —
`lvs.terminal.split-across-nets`, error — and not a silent choice of one.

**`R-lvs3-5e` A stamped `Net` is read, reported and never obeyed.** Where a piece's stamped name
disagrees with what the comparison concludes, `lvs.net.label-disagrees` at warning. The existing
"one connected piece carries two different net names" refusal is kept verbatim: it is a genuine
short or a genuine mislabel, nothing downstream can tell which, so nothing picks one.

---

## 6. `R-lvs3-6` — ground, and the metal that is not drawn

The shipped `mmic-GaAs_2LM_100um.ctech` has `Backside Metal` as a conductor with
`DrawingLayers: []`. Read naively every backside via terminates in mid-air and every grounded
device on the starter MMIC technology reads as open.

**`R-lvs3-6a`** A `StackupKind.Conductor` with `IsGroundReference: true` **is** net `"0"`, whether
or not it draws (note R-lvs-15). The flag exists already; this is its second reader.

**`R-lvs3-6b`** Every piece on such a conductor's drawing layers, where it has any, is net `"0"`.

**`R-lvs3-6c`** A via whose span **reaches** such a conductor terminates on `"0"` even where that
conductor draws nothing, because the stackup is the statement that the metal is there.

**`R-lvs3-6d`** No ground-reference conductor, and a schematic that grounds something:
`lvs.ground.no-reference-conductor`, **warning** naming the flag, with ground terminals then read
as ordinary opens. Warning and not error — a die with no backside metal, grounded only through
bondwires to a package, is a real and correct design.

**`R-lvs3-6e` The reminder, unconditional** (owner, note §12.6 / R-lvs-16).
`lvs.ground.reference-undrawn` at **info**, on every run that relies on an undrawn reference,
including a perfectly clean one, naming the stackup entry and the count:

> *Ground came from the stackup, not from the artwork: 'Backside Metal' is the ground reference and
> draws no layer, so 34 vias were read as reaching net 0. Nothing on your drawing shows this
> connection.*

Info rather than warning because on a correct MMIC it is the normal state. Unconditional because
it is the **one** inference in the extraction the user cannot see on their own screen: the metal is
not drawn, so there is nothing to look at and nothing to select. The first time it is wrong — a
technology whose reference is mis-flagged — the design would read as perfectly connected and this
sentence is the only reason anyone would notice.

---

## 7. `R-lvs3-7` — what this file may not do

A comment-stripped source scan over `src/Design/Layout/Lvs/`:

- no `Clipper` call, no `Union`, no `InflatePaths` — geometry belongs to `Extraction` and `Drc`
- no stackup via-span walk — `DrcConnectivity`'s
- no `CellPins` reimplementation, no `LayoutInstanceTransform` arithmetic done by hand
- no second flatten
- no write of any kind: LVS is read-only, on `check`'s terms (R-aut4-6), so it runs on a read-only
  tree and on a workspace another process has open

---

## 8. Gate

`tests/Ui.Tests/Lvs/LayoutNetlistTests.cs`.

1. **A two-resistor divider.** Three nets, two devices, four terminals, asserted against hand
   arithmetic — never against another circuitRF path.
2. **Rotation, mirror and a non-cardinal angle.** The same board at 90°, at 217° (`RotDeg`) and
   mirrored: every terminal lands on the same net as before. **The fixture's pins must be off both
   axes** — a mirror that is a no-op on a symmetric land proves nothing (the WB-C trap, second
   form).
3. **A via joins two layers and the two pins are one net**; an offset staircase of metal connects
   through a via without the two metals overlapping each other.
4. **Four-layer board, inner power plane.** Pins on layer 2 reach the plane through a barrel that
   only *passes* it — the every-conductor-the-barrel-passes case.
5. **The undrawn ground reference.** On the shipped MMIC technology: a backside via reaches net
   `"0"`, **and `lvs.ground.reference-undrawn` is reported with the right count** (`R-lvs3-6e`).
   Assert the diagnostic id, not the sentence.
6. **No ground reference:** warning, ground pins read as opens, run completes (`R-lvs3-6d`).
7. **Classification, one case each:** a `SchematicId` instance, a `DisplayRefDes`-only instance, a
   `PartKind` instance, a kit cell with `NumPorts > 0`, a via-fence cell (interconnect, no device),
   and an unclassified copper cell (warning, once) (`R-lvs3-3a`, `R-lvs3-3c`).
8. **Duplicate designators are reported before anything else** and the run says so (`R-lvs3-3e`).
9. **A 2×3 array of a device cell is six devices** with paths `R1[0,0]`…`R1[1,2]`, all carrying
   designator `R1`; a 1×20 array of a via cell is **zero** devices (`R-lvs3-4`).
10. **A cell with no derivable terminal map** produces a device with no terminals and
    `lvs.device.no-terminal-map`, and is **present in the device count** (`R-lvs3-5a`).
11. **A bonded terminal whose two pads land on different nets** is reported
    (`lvs.terminal.split-across-nets`), not resolved to one (`R-lvs3-5d`).
12. **A pin on a layer the stackup does not describe** reports `lvs.pin.no-copper` **naming the
    layer** (`R-lvs3-5c`).
13. **Nothing a schematic says can change the answer.** Extract a board twice, once with a
    correct schematic beside it and once with a deliberately wrong one; the `LvsNetlist` is
    identical (`R-lvs3-5b`). This is the test that catches overview §1a.
14. **Over the flatten ceiling: no netlist, and the note says so** (`R-lvs3-2c`).
15. **The source scan** of §7.
16. **`LayoutDesignFlatten`'s existing output is unchanged**, byte for byte, on a fixture with
    nested instances (`R-lvs3-2a`).

## 9. Scope

- **Flat only.** Hierarchy is brief 9; this brief's `--flat` reading is that brief's oracle.
- **One technology.** Brief 13.
- **No reduction** (brief 6), **no comparison** (brief 7), **no properties** (brief 10).
- **No wBond.** Brief 13.
- **No geometric recognition.** Brief 14.
