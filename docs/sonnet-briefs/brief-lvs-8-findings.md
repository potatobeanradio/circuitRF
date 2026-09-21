# Brief 8 — findings: the result, the shorts, the opens, the markers

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs8-n` · **Design note:** [`lvs.md`](../design/lvs.md) §6.5, §8.1
**Area:** `src/Design/Layout/Lvs/LvsFindings.cs` (new), `LvsRunResult.cs` (new),
`src/Design/Layout/Extraction/CopperPieces.cs`
**Depends on:** 7 · **Blocks:** 9, 10, 11, 12

---

## 0. What this brief delivers

Brief 7's typed divergences become a report a designer can act on: a short with the **path** that
causes it, an open with its **island structure**, a marker on everything, and a result model the
CLI and the panel both consume.

---

## 1. `R-lvs8-1` — `LvsRunResult`, on `DrcRunResult`'s terms

```csharp
public sealed record LvsRunResult(
    IReadOnlyList<LvsFinding> Findings,      // waived ones included and marked
    IReadOnlyList<LvsPair>    Correspondence,
    LvsCounts                 Counts,        // devices/nets per side, before and after reduction
    string?                   TechnologyName,
    ReductionMode             Reduction,
    IReadOnlyList<Diagnostic> Diagnostics)   // what the run could NOT do
{
    public int ErrorCount { get; }
    public int WarningCount { get; }
    public int WaivedCount { get; }
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;
}
```

**`R-lvs8-1a`** Every field of `DrcRunResult` that earned its place earns it here: waived findings
are **still reported** and merely not counted; the **technology is named** because a workspace with
two processes has a default that may not be the one the designer has in mind; and **everything the
run could not do is stated rather than dropped**.

**`R-lvs8-1b` Ordering is deterministic**: severity, then finding id, then the primary object's
path. Two runs over unchanged documents produce identical lists and a test can assert on order.

**`R-lvs8-1c`** `Counts` carries **both** the pre- and post-reduction device counts per side. A
user reading *"32 devices"* against *"8 devices"* needs to see the merge that explains it.

**`R-lvs8-1d`** `Reduction` is on the face of the result, always (brief 6 `R-lvs6-5c`).

---

## 2. `R-lvs8-2` — a finding

```csharp
public sealed record LvsFinding(
    Diagnostic            Diagnostic,   // stable lvs. id + typed arguments + SEVERITY
    IReadOnlyList<string> Objects,      // the designers' own names — un-reduced (brief 6)
    IReadOnlyList<long[]> MarkerRings,  // DBU, the DRC marker convention
    Bbox                  Marker,
    string                Key)          // what a waiver names — brief 12
{
    public DiagnosticSeverity Severity => Diagnostic.Severity;
    public bool Waived { get; init; }
    public string? WaiverReason { get; init; }
}
```

**`R-lvs8-2z` There is exactly ONE severity and it lives on the `Diagnostic`.** A separate
`Severity` field beside a `Diagnostic` that already has one is two fields with one meaning, which
is this repo's recurring scar — three copies of a version number disagreeing is the scar
`VersionSingleSourceTests` exists for. `DrcViolation` carries its own because its severity comes
from the **rule** and not from the violation; here the diagnostic is the producer and owns it.
`Severity` above is a derived accessor, on `DisplayRefDes`' and `RotationDegrees`' pattern.

**`R-lvs8-2a` Every finding is a `Diagnostic`** with a stable dotted `lvs.` id and the producing
step's own typed arguments. **The id is the contract; the sentence is not**, and every test in this
series asserts ids.

**`R-lvs8-2b` `Objects` are un-reduced** (brief 6 `R-lvs6-5b`). A finding that can only say *"the
merged group at net 14"* is one a user cannot act on.

**`R-lvs8-2c` Every finding has a marker**, or it is a run-level line with an empty one. *"Spacing
violation somewhere on M1"* is §9A.1's own example of a report that is not usable, and the same
applies here.

---

## 3. `R-lvs8-3` — the catalogue

Fixed here, once, so the CLI's `--json`, the panel and the tests agree.

| id | Severity | Means |
|---|---|---|
| `lvs.device.unmatched-schematic` | error | a schematic device with no layout counterpart |
| `lvs.device.unmatched-layout` | error | the reverse |
| `lvs.device.type-mismatch` | error | matched by name, different canonical type |
| `lvs.device.duplicate-designator` | error | two placements, one designator — reported **first** |
| `lvs.device.dangling-schematic-id` | warning | a `SchematicId` naming nothing |
| `lvs.device.no-terminal-map` | error | brief 1 could not derive one; the device is unmatchable |
| `lvs.terminal.wrong-net` | error | a matched device's terminal reaches the wrong net |
| `lvs.terminal.split-across-nets` | error | one terminal's pads land on different nets |
| `lvs.net.short` | error | two schematic nets are one piece of copper — **with a path** |
| `lvs.net.open` | error | one schematic net is several islands — **with the islands** |
| `lvs.net.label-disagrees` | warning | a stamped `Net` contradicts the conclusion |
| `lvs.pin.no-copper` | error | a pin lands on nothing, naming the layer |
| `lvs.anchor.contradicted` | warning | a name-based pairing the structure refutes |
| `lvs.match.by-symmetry` | info | an arbitrary pairing within an automorphism group |
| `lvs.match.structural-only` | info | no anchors were available |
| `lvs.terminals.derived-by-order` | warning | brief 1 guessed positionally |
| `lvs.ground.reference-undrawn` | info | brief 3's unconditional reminder |
| `lvs.ground.no-reference-conductor` | warning | nothing in the stackup is the reference |
| `lvs.cell.unclassified` | warning | copper-bearing cell that is neither device nor interconnect |
| `lvs.reduce.parallel` / `.series` / `.jumper` | info | brief 6's summary |
| `lvs.property.*` | — | brief 10 |
| `lvs.hierarchy.*` | — | brief 9 |

**`R-lvs8-3a`** Adding an id later is additive. **Changing one is a breaking change** and gets the
same treatment as changing a file format.

---

## 4. `R-lvs8-4` — a short is a PATH

**`R-lvs8-4a`** Brief 2 retained one `PieceJoin` per union. A short between two pins is a
breadth-first walk over that spanning forest, and the shortest join sequence between the two
pieces is the answer.

**`R-lvs8-4b`** The finding names the **narrowest** join on the path and its coordinate:

> *VDD and VOUT are joined through a 0.2 mm neck of Metal1 at (1.204 mm, 3.881 mm).*

not *"VDD and VOUT are shorted"*. This is the whole reason the merge edges exist, and brief 5's
fault F4 is the gate.

**`R-lvs8-4c`** The marker is the join geometry, not the whole net. A marker covering a board-wide
pour is a marker that points at nothing.

**`R-lvs8-4d`** Where several schematic nets share one piece, **one finding per pair**, capped and
summarised. A pour accidentally joined to everything must not emit a finding per pair of the
forty nets it touches without saying so.

---

## 5. `R-lvs8-5` — an open is an ISLAND STRUCTURE

**`R-lvs8-5a`** `Regions.Walk` already produces island structure and railRF already reports it.
The finding is the same shape:

> *Net VDD is three islands — U1.1 and C4.2 here; R7.1 alone; J1.3 alone.*

with **a marker per island**, and railRF's own note applies unchanged: this alone has caught real
problems.

**`R-lvs8-5b` Two islands joined by nothing at DC and by a capacitor at AC is NOT an error** —
`PdnRailRegions`' own rule, and it is the LVS answer too: if the schematic says the two are one
net, it is an open; if the schematic says they are two nets, it is correct and silent. **The
schematic decides, not the copper.**

**`R-lvs8-5c`** An island containing no pins at all is reported separately as
`lvs.net.floating-copper` at **warning**, not as an open: unconnected copper on a net is a pour
someone forgot to stitch, which is worth saying and is not the same defect.

---

## 6. `R-lvs8-6` — cap, summarise, and stay readable

**`R-lvs8-6a`** Per-object cap with a trailing count, run-level lines outside it — the change-report
convention already in the repo (`SchematicToLayoutGenerator.ReportLine`'s own rule, where an empty
instance name means the line is about the run).

**`R-lvs8-6b`** A run that found nothing still emits the run-level summary: counts, technology,
reduction mode, and the ground reminder if it fired. *"Nothing changed, say nothing"* is a rule
about per-object noise; a run that deliberately concluded "these match" and said nothing is
indistinguishable from a broken command.

---

## 7. Gate

`tests/Ui.Tests/Lvs/FindingsTests.cs`, plus brief 5's fault gates completing here.

1. **Every id in §3's table is produced by some fixture**, and no finding is emitted with an id not
   in the table. A source scan plus a coverage assertion — an id nothing produces is dead, and a
   finding with an unlisted id breaks the CLI's `--json` contract silently.
2. **F4 short: the path.** The finding names the spur's coordinate to within its own extent and
   names the **narrowest** join on the path, not the first (`R-lvs8-4b`).
3. **F5 open: three islands**, each with its pins and its own marker (`R-lvs8-5a`).
4. **Two nets deliberately separate are silent** even though the copper has two islands
   (`R-lvs8-5b`) — the test that stops LVS reporting every correct AC coupling as an open.
5. **A pour with no pins on it** is `lvs.net.floating-copper`, warning, not an open (`R-lvs8-5c`).
6. **Ordering is deterministic** over ten runs on the six-fault board (`R-lvs8-1b`).
7. **Objects are un-reduced.** Break one of four parallel caps; the finding names that cap's
   designator (`R-lvs8-2b`).
8. **Every finding carries a marker** with non-empty rings, except run-level lines
   (`R-lvs8-2c`).
9. **A forty-net pour short caps and summarises** rather than emitting 780 findings
   (`R-lvs8-4d`).
10. **A clean run still reports its summary**, including the ground reminder on the MMIC cell
    (`R-lvs8-6b`).
11. **`Counts` shows both sides before and after reduction** (`R-lvs8-1c`).

## 8. Scope

- **No waivers.** Brief 12 — `Key` is produced here and consumed there.
- **No rendering.** Markers are DBU rings; drawing them is brief 12's.
- **No `--json` schema.** Brief 11 — it projects this record and adds nothing.
- **No new geometry.** The path walk uses brief 2's edges; the islands use `Regions.Walk`.
