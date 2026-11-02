# Sonnet Brief — Z_Port per-port references (2N nets, ± pairs) — mirrors the SDD model

## Goal
Give `Z_Port` **per-port reference nets**, exactly parallel to the SDD: an N-port Z_Port takes **2N nets as
differential ± pairs** — `port1+ port1− port2+ port2− …`. Each port voltage is `V_p = V(net[2p]) − V(net[2p+1])`,
with its **own** reference, instead of the current single shared reference. The schematic symbol reuses the SDD
2N-pin work just landed.

[DECISIONS — locked by user]
- **Scope: Z_Port only.** SnP, TLIN, user freq-models KEEP the existing N-or-(N+1) single-shared-reference
  convention. Do NOT touch them.
- **Strict ± wiring:** every − pin must be wired explicitly (no implicit-ground default). An odd net count errors,
  exactly like the SDD.

This replaces the old N-or-(N+1) reference convention **for Z_Port only**. (Old form: N signal nets + optional
shared ref net popped as `RefNetBinding`. New form: 2N nets, ± pairs, no shared ref.)

## ⚠️ CRITICAL — net/pin ORDER is INTERLEAVED ± pairs (this is exactly what went wrong on the SDD last round)
The 2N nets/pins MUST be ordered **+, −, +, −, …** (each port's + immediately followed by ITS OWN −), **NOT**
all-pluses-then-all-minuses.

- **CORRECT** (interleaved, per-port pairs): `port1+ port1− port2+ port2−`  → `Nodes[0]=1+, Nodes[1]=1−, Nodes[2]=2+, Nodes[3]=2−`
- **WRONG** (grouped — do NOT do this): `port1+ port2+ port1− port2−`

Concrete 2-port example. The line `Z_Port:Z1  a 0 b 0  Z[1,1]=… …` means:
- net[0]=`a` = port1+,  net[1]=`0` = port1−   → `_v1 = V(a) − V(0)`
- net[2]=`b` = port2+,  net[3]=`0` = port2−   → `_v2 = V(b) − V(0)`

The index formula is the single source of truth everywhere (parser, elaborator, stamp, symbol pins, NetExtractor):
**`index 2p` = port (p+1) PLUS ; `index 2p+1` = port (p+1) MINUS** (p 0-based). If any of those layers groups
the pluses and minuses separately, the device is wrong even though the net COUNT is right — that is the exact bug
from the SDD round. Every test below that checks order is there to catch this; do not weaken them.

## Why this is contained
Per design (`linear-engine.md` §4.1): *"The model receives the reference node as just another node index and
stamps it itself … the engine does not special-case it."* S-param extraction and the HB linear partition only read
node voltages — they never special-case Z_Port's reference. So per-port refs live entirely in: data model →
CnlReader → Elaborator net mapping → `ZPortModel.Stamp` → schematic symbol → CnlWriter. No HB/S-param/extraction
change.

## Current state (what changes)
- **`ZPortModel.Stamp`** (`src/Core/Devices/ZPortModel.cs`): uses `int refNode = c.ReferenceNode;` (single shared)
  and `nodeP = c.Nodes[p]`. Constraint `V_p − V_ref − Σ_q Z[p,q] I_q = 0`; KCL branch flows `nodeP → refNode`.
- **`Elaborator.ResolveZPortParameters`**: determines N from max Z[i,j] index; nets map 1:1 to `Nodes[]`;
  `ReferenceNode` comes from `RefNetBinding` (or 0).
- **`CnlReader.ParseZPortLine`**: pops the (N+1)th net into `RefNetBinding` (N-or-N+1 rule).
- **Schematic** `SymbolPortDefs.For(ZPort, n)` → `GeneratePorts(n)` (N+1 pins, shared "ref").

## Fix 1 — CnlReader: parse Z_Port as 2N nets (± pairs)
In `ParseZPortLine` (`src/Core/Netlist/CnlReader.cs`): the Z[i,j]= boundary scan stays; the **net section** is now
2N nets, no shared-ref pop. Remove the `nets.Count == portCount+1 → RefNetBinding` logic for Z_Port. Do NOT validate
arity here (N isn't yet known from nets alone — Z entries give N). Leave `RefNetBinding = null`. (Keep the SnP
N-or-(N+1) logic in `ValidateSnpNets` untouched — that's a different component.)

## Fix 2 — Elaborator: 2N-net mapping + arity validation (mirror the SDD)
In `ResolveZPortParameters`: determine N from max Z[i,j] index (unchanged). Then validate the **net count is 2N**:
```
int netCount = inst.NetBindings.Count;
if (netCount != 2 * portCount)
    throw new InvalidOperationException(
        $"Z_Port '{inst.InstanceName}': expected {2*portCount} nets (2 per port: +,−) for a {portCount}-port " +
        $"(Z[{portCount},{portCount}] present); got {netCount}. Each port needs a +,− net pair.");
```
Also reject an **odd** net count up front with the same ±-pair message (mirror the SDD's even-count check) in case
N can't be inferred. The 2N nets flow through the existing `resolvedNodes = inst.NetBindings.Select(...)` — so
`Nodes[2p]` = port p+, `Nodes[2p+1]` = port p−. `ReferenceNode` is now unused by Z_Port (leave the field at its
default 0; the stamp won't read it).

## Fix 3 — ZPortModel.Stamp: per-port references
Rewrite the node access + constraints to read ± pairs (`src/Core/Devices/ZPortModel.cs`):
```
// 2N nets: Nodes[2p] = port p+, Nodes[2p+1] = port p−. Per-port reference (no shared refNode).
var branches = new int[_portCount];
for (int p = 0; p < _portCount; p++) branches[p] = mna.AddBranch();

for (int p = 0; p < _portCount; p++)
{
    int nodePlus  = c.Nodes[2 * p];
    int nodeMinus = c.Nodes[2 * p + 1];
    mna.AddBranchCurrent(branches[p], nodePlus, nodeMinus);   // current flows + → −
}

for (int p = 0; p < _portCount; p++)
{
    int nodePlus  = c.Nodes[2 * p];
    int nodeMinus = c.Nodes[2 * p + 1];
    // V_p − V_ref_p − Σ_q Z[p,q]·I_q = 0  with V_ref_p = V(nodeMinus)
    mna.AddConstraint(branches[p], nodePlus,  new Complex(+1, 0));
    if (nodeMinus > 0)
        mna.AddConstraint(branches[p], nodeMinus, new Complex(-1, 0));   // ground-aware
    for (int q = 0; q < _portCount; q++)
        mna.AddBranchConstraint(branches[p], branches[q], -z[p, q]);
}
```
Update the model's doc-comment: 2N nets (± per port), per-port reference; remove the shared-`ReferenceNode` /
N-or-(N+1) wording. (Keep `PortCount` = N.)

## Fix 4 — schematic symbol: reuse the SDD 2N-pin generator
In `SymbolPortDefs.For` (`src/Ui/Schematic/EditableSchematic.cs`): split `ZPort` off `Sdd`'s case is already done —
now route **ZPort** to a 2N-pin ± generator too. The cleanest path: rename/generalize the SDD generator so both
share it (the geometry is identical — ports stacked, "+" left / "−" right):
```
case SymbolKind.ZPort:
case SymbolKind.Sdd:
    return GenerateDifferentialPorts(portCount >= 1 ? portCount : 2);   // 2N pins, ± pairs
```
where `GenerateDifferentialPorts` is the `GenerateSddPorts` you just wrote (pins `1+,1−,2+,2−,…`, order =
NetExtractor contract `pin[2p]=p+`, `pin[2p+1]=p−`). Delete the now-unused N+1 `GeneratePorts` **only if** nothing
else references it (search first — SnP-on-schematic may; if so, keep it for whatever still needs N+1).
- **Mirror** in `SchematicModelBuilder.cs`: route ZPort to the same 2N generator (`GenerateSddPorts` mirror) in
  `BuildPorts`; the `ComputeGlyphBbLocal` / `ComputeGlyphBb` `ZPort or Sdd` union blocks already pick up the new
  pins.
- **`EditableComponent.PortCount`** for ZPort stays N (reads `NumPorts`); pin count becomes 2N automatically.
- **`FromRenderModel`**: the variadic-N parse for ZPort currently falls back to `rc.Ports.Count - 1`. With 2N pins
  that's wrong — change ZPort (like SDD) to `Math.Max(1, rc.Ports.Count / 2)`. (The type-label parse path, when a
  "Z2P" label is present, still wins and is correct.)

## Fix 5 — CnlWriter: emit 2N nets for Z_Port
In `src/Core/Netlist/CnlWriter.cs`, wherever Z_Port instance lines are written, emit all 2N nets in ± order (no
separate shared-ref net). Confirm round-trip: read→write→read yields identical nets. (If the writer is generic over
`Instance.NetBindings`, it already emits all nets in order — just verify no special Z_Port ref-net handling
remains.)

## Fix 6 — ComponentTypeRegistry / NumPorts
Confirm Z_Port's `NumPorts` hidden param still means **N signal ports** (not pin count) and the "Z<N>P" display
code derives from N. No change expected; verify the default-parameters seed and DisplayName for ZPort are N-based,
same as SDD.

## Tests
Core/Engine (`tests/Core.Tests`, `tests/Engine.Tests`):
1. **ZPort_2Port_Parses4Nets:** `Z_Port:Z1 a 0 b 0 Z[1,1]=50 Z[2,2]=50 Z[1,2]=0 Z[2,1]=0` → nets
   `["a","0","b","0"]`, `ZPortCount==2`, no `RefNetBinding`.
2. **ZPort_OddNets_Errors:** 3 nets with Z[2,2] present → clear ±-pair arity error (not silent).
3. **ZPort_NetCountMismatch_Errors:** Z[2,2] present but only 2 nets → the "expected 4 nets" error.
4. **ZPort_PerPortRef_Stamps:** build a 2-port Z_Port with port1 ref = node A, port2 ref = node B (distinct,
   non-ground); assert the two constraint rows reference DIFFERENT minus-nodes (A vs B), proving per-port refs.
5. **ZPort_1Port_2Nets_Unchanged_Behavior:** a 1-port `Z_Port:Z a 0 Z[1,1]=R` (2 nets, − to ground) gives the same
   stamp/result as before (port to ground) — regression that the common grounded case still works.
6. **Sparam_With_ZPort_PerPortRef:** an S-param run with a Z_Port whose − pins are grounded reproduces the prior
   ground-referenced S-params to 1e-9 (proves no extraction regression).
7. **SnP_Unchanged:** an SnP N+1-net line still parses with `RefNetBinding` (Z_Port change didn't leak into SnP).

UI (`tests/Ui.Tests`):
8. **ZPortSymbol_2Port_Has4Pins:** `SymbolPortDefs.For(ZPort, 2)` → 4 pins `1+,1−,2+,2−`, order = contract.
9. **ZPort_NetExtraction_4Nets:** place Z2P, wire 1+→a, 1−→0, 2+→b, 2−→0 → extracted line has 4 nets `a 0 b 0`.

## Gate
Build 0W/0E; tests green. Manual: place a Z2P in the schematic → 4 pins (1+/1−/2+/2−); wire the − pins to ground →
emitted line `Z_Port:Z1 a 0 b 0 Z[1,1]=… …` (4 nets), S-param/HB run matches the prior grounded result; wiring a −
pin to a non-ground node now gives that port its own reference.

## On completion
Note in `src/Engine/CLAUDE.md` (or Core): **Z_Port now uses 2N nets as differential ± pairs with per-port
references** (`V_p = V(net[2p]) − V(net[2p+1])`), parallel to the SDD — NOT the N-or-(N+1) shared-reference
convention. That single-shared-reference convention still applies to **SnP/TLIN/user freq-models** (unchanged).
`ZPortModel` no longer reads `ElaboratedComponent.ReferenceNode`. Schematic Z_Port reuses the SDD 2N-pin ± port
generator; `PortCount` = N (signal ports), pin count = 2N; `FromRenderModel` derives ZPort N = pins/2.
Update `linear-engine.md` §4.1/§4.4 to carve out Z_Port's per-port-reference exception.
```
