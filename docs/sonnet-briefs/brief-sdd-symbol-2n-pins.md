# Sonnet Brief — SDD schematic symbol must expose 2N pins (± pairs), not N+1

## Problem
Placing a 2-port SDD in the **schematic editor** emits a 3-net instance line (`Vin Vout 0`) →
elaboration now correctly errors: *"SDD 'X1': expected an even number of nets (2 per port: +,−); got 3."*
The engine convention (locked) is **2N nets per SDD, as differential ± pairs**: `_v(p) = V(net[2p]) − V(net[2p+1])`,
`portCount = netCount / 2`. So an SDD2 needs **4 pins / 4 nets**: `port1+ port1− port2+ port2−`.

The schematic SDD symbol currently shares ZPort's port generator, which produces **N+1** pins (N signal + 1 shared
"ref") — correct for ZPort/SnP, **wrong for SDD**. We're splitting SDD onto its own 2N-pin generator. ZPort is
unchanged.

[DECISION — user chose Option A] SDD symbol exposes 2N pins as ± pairs; user wires the − pins to ground (or across
nodes for a true differential port). No engine-side change.

## Root cause (all in `src/Ui/Schematic/EditableSchematic.cs`)
- `SymbolPortDefs.For(kind, portCount)` routes `case SymbolKind.ZPort: case SymbolKind.Sdd:` → `GeneratePorts(n)`,
  which returns **N+1** pins (ceil(N/2) signal left; floor(N/2) signal + 1 "ref" right). For SDD2 → 3 pins.
- `EditableComponent.PortCount` for ZPort/Sdd reads the `NumPorts` parameter (= **N signal ports**, default 2).
  This stays N. Pin **count** for SDD becomes 2N; every `SymbolPortDefs.For(Sdd, PortCount)` consumer then gets 2N
  pins automatically.
- `NetExtractor` emits one net per pin **in pin-index order** — so the pin order below IS the net order, which must
  match the engine's pairing.

There is a mirror copy in `src/Ui/Schematic/SchematicModelBuilder.cs` (`GenerateVariadicPorts`, demo/test
scaffolding only). Update it too for consistency so the Hero demo and any test that places an SDD stays correct.

## The contract (engine pairing — must match exactly)
For port p (1-based): `NetBindings[2(p−1)]` = **port p, +** ; `NetBindings[2(p−1)+1]` = **port p, −**.
So pin order MUST be: `pin0 = 1+`, `pin1 = 1−`, `pin2 = 2+`, `pin3 = 2−`, … `pin[2N−2] = N+`, `pin[2N−1] = N−`.

## Fix 1 — add an SDD-specific 2N-pin generator (`SymbolPortDefs`)
Split SDD out of the `ZPort`/`Sdd` shared case:
```
case SymbolKind.ZPort:
    return GeneratePorts(portCount >= 1 ? portCount : 2);     // unchanged: N+1, shared ref
case SymbolKind.Sdd:
    return GenerateSddPorts(portCount >= 1 ? portCount : 2);  // NEW: 2N pins, ± pairs
```
New generator — 2N pins, each port a +/− pair, laid out so pairs are visually grouped. Suggested geometry
(local units, 100 = 1 grid square; SDD body is the box ±80 x, ±50 y from BuildSdd):
```
// 2N pins for an N-port SDD. Port p (1-based): "+" on the LEFT column, "−" on the RIGHT column,
// pairs stacked top→bottom. Pin index order is the NetExtractor contract:
//   pin[2(p-1)] = "p+", pin[2(p-1)+1] = "p-".
private static (string Name, float LocalX, float LocalY)[] GenerateSddPorts(int n)
{
    var ports = new (string, float, float)[2 * n];
    // Stack N ports vertically; each port occupies one row. Row spacing 200 (grid).
    for (int p = 0; p < n; p++)
    {
        float rowY = (p - (n - 1) * 0.5f) * 200f;          // centered vertically
        ports[2 * p]     = ($"{p + 1}+", -200f, rowY);      // + on left edge
        ports[2 * p + 1] = ($"{p + 1}-", +200f, rowY);      // − on right edge
    }
    return ports;
}
```
Pin NAMES use `"1+"`, `"1-"`, `"2+"`, `"2-"` (ASCII `+`/`-`) so the contract is visible and they can't be confused
with ZPort's numeric pins. (Match whatever ASCII/unicode the renderer already expects for labels — Term uses the
unicode "−"; pins are fine as ASCII since they're short stubs. Use ASCII `-` to avoid font issues on pin labels.)

## Fix 2 — glyph box sizing for 2N pins (`BuiltInSymbols.BuildSdd` + `EditableComponent.ComputeGlyphBb`)
`BuildSdd` draws a fixed ±80×±50 box. With N>1 the pins now span ±((N−1)*100 + slack) in Y. Two options — pick the
simpler that looks acceptable:
- (preferred, minimal) Leave the drawn box fixed; the existing variadic lead-extension loop in
  `EditableComponent.ComputeGlyphBb` (the `if (Symbol is ZPort or Sdd)` block that unions `SymbolPortDefs.For` pin
  coords) already grows the glyph BB to include the new pins, so hit-testing/zoom stay correct. The box just won't
  enclose all stubs — acceptable for v1.
- (nicer, optional) Make `BuildSdd` height scale with N. Out of scope unless trivial; do (preferred) unless it looks
  broken.

`ComputeGlyphBbLocal` in `SchematicModelBuilder.cs` has the same `ZPort or Sdd` union block — it already iterates
`SymbolPortDefs.For(kind, n)`, so it picks up the new pins automatically once Fix 1 lands. No change needed beyond
the mirror generator.

## Fix 3 — mirror generator in `SchematicModelBuilder.cs`
`BuildPorts` routes `ZPort or Sdd → GenerateVariadicPorts`. Split SDD to a local 2N-pin generator mirroring
`GenerateSddPorts` (same pin order/names). `MakeComponent`'s `n = ZPort/Sdd ? 2 : 0` default stays (N=2 signal
ports → 4 pins). This path is demo/test only but must stay self-consistent or SDD-placing tests break.

## Fix 4 — type label / NumPorts stays N
Do NOT change `EditableComponent.PortCount` to return 2N. It must keep returning **N** (the NumPorts param = signal
port count); the symbol renders 2N pins because `GenerateSddPorts(N)` returns 2N. Confirm `ComponentTypeRegistry`'s
SDD display code (e.g. "SDD2") is derived from N, not pin count. `FromRenderModel`'s variadic-N parse
(`rc.Ports.Count - 1` fallback) is WRONG for SDD now (pin count = 2N, so N = pins/2, not pins−1). Update the
fallback: for `SymbolKind.Sdd` use `Math.Max(1, rc.Ports.Count / 2)`; ZPort keeps `rc.Ports.Count - 1`.

## Tests (`tests/Ui.Tests`)
1. **SddSymbol_2Port_Has4Pins:** `SymbolPortDefs.For(SymbolKind.Sdd, 2)` → 4 pins, names `["1+","1-","2+","2-"]`,
   left/right X = −200/+200, pin order matches the contract.
2. **SddSymbol_3Port_Has6Pins:** `For(Sdd, 3)` → 6 pins, names `1+,1-,2+,2-,3+,3-`.
3. **ZPortSymbol_Unchanged:** `For(ZPort, 2)` still returns 3 pins (`1`,`2`,`ref`) — regression guard.
4. **Sdd_NetExtraction_4Nets:** build a `SchematicEditModel`, place an SDD2, wire `1+`→"Vin", `1-`→gnd(0),
   `2+`→"Vout", `2-`→gnd(0); `NetExtractor.Extract` → the SDD instance line has exactly 4 nets in order
   `["Vin","0","Vout","0"]`.
5. **Sdd_Elaborates_NoArityError:** the extracted 4-net SDD elaborates without the "even number of nets" throw;
   `SddPortCount == 2`.
6. **EditableComponent_Sdd_PortCount_IsN:** an SDD2 component's `PortCount == 2` (signal ports), while
   `SymbolPortDefs.For(Sdd, PortCount).Length == 4`.

## Gate
Build 0W/0E; tests green. Manual: place an SDD2 in the schematic editor → it shows 4 pins (1+/1−/2+/2−); wire the
two − pins to ground and 1+/2+ to your nodes → the emitted netlist line is `SDD:X1 Vin 0 Vout 0 …` (4 nets); the
prior arity error is gone and HB runs.

## On completion
Note in `src/Ui/CLAUDE.md`: the SDD schematic symbol exposes **2N pins as differential ± pairs** (pin order
`1+,1−,2+,2−,…`), separate from ZPort's N+1 (signal + shared ref) generator. Pin order is the NetExtractor contract
matching the engine's `_v(p) = V(net[2p]) − V(net[2p+1])`. `EditableComponent.PortCount` for SDD remains N (signal
ports); pin count is 2N. `FromRenderModel` derives SDD N as pins/2 (ZPort stays pins−1).
