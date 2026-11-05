# Brief IPX-3 — IPC-2581 reader: nets, pads, vias and the stackup

**Series:** IPC-2581 import, IPX-0 to IPX-4 · **Tag:** `R-ipx3-n`
**Area:** `src/Design/Layout/Interchange/Ipc2581Reader*.cs`, `PcbStackupMapping.cs`, `PcbViaSpanMapping.cs`
**Depends on:** IPX-0 and IPX-2.

---

## 1. `R-ipx3-1` — nets on copper

- **`R-ipx3-1a`** `Set@net` or `PadStack@net` becomes `LayoutShape.Net`, by name and with the step prefix rule from
  IPX-2. A net with an empty name, or the file's stated no-net value, stays null.
- **`R-ipx3-1b`** `LogicalNet` gives component-and-pin membership, in any of the spellings in §2. `PhyNet` gives
  points. Neither creates a shape (series rule S2).
- **`R-ipx3-1c`** Nets stated by the file and carried by no copper are reported with a count. They do not appear as
  nets on the board.

## 2. `R-ipx3-2` — pads know their part

- **`R-ipx3-2a`** A pad's component and pin come from whichever is present: a `PinRef` child (`pin`, `componentRef`),
  `pin` on the `Pad`, or matching a `Pad` to `LogicalNetPin` through a stated component reference. Set
  `LayoutShape.Component` and `Pin`. Where nothing states the part, leave them null; do not match by location within a
  tolerance in this brief.
- **`R-ipx3-2b`** A `Component` is read for its reference, side and position. Per decision D1 it is not placed. The
  message reports how many components were read and that they were not placed as instances. Reference designators
  keep the step prefix rule.

## 3. `R-ipx3-3` — a negative plane's net

Features on a negative layer are clearances, so the copper left behind has no `Set` stating its net. A clearance pad's
`net` describes a pin passing through the plane and is not the plane's net.

- **`R-ipx3-3a`** Each piece of copper from IPX-2 §6 takes its net from the `PhyNetPoint`s stated on that layer that fall
  inside it. A thermal pad's net is further evidence of the same kind: a thermal connects its pin to the plane.
- **`R-ipx3-3b`** Evidence for one net names the piece. No evidence leaves it null. Conflicting evidence leaves it null
  and is reported with the layer and the nets. A layer's name is never evidence, including a layer called `GND`.
- **`R-ipx3-3c`** Fixtures: a split plane with two nets, a plane with no evidence, and a plane with conflicting evidence.

## 4. `R-ipx3-4` — holes and vias

- **`R-ipx3-4a`** `platingStatus` `VIA` or `VIA_CAPPED` with a round copper pad at the same point becomes a `ViaShape`,
  with the pad consumed so copper is not drawn twice. The pairing rule is the one `DrillViaPairing` states. Share its
  logic through a small adapter, or say in the completion note why it could not be shared.
- **`R-ipx3-4b`** `PLATED` (a component hole) stays a circle on the drill layer with its pads as copper, as
  `DrillViaPairing` treats component drills. `NONPLATED` stays a hole. A via with no pad, or a non-round pad, stays a
  hole beside its copper, with a count in `DegradedCounts`.
- **`R-ipx3-4c`** The span comes from a hole's own `Span`, or from its drill layer's `Span`. A drill layer with no span
  goes through the board. Set `SpanFromName` and `SpanToName` and let `PcbViaSpanMapping` build the entries. A drill
  layer's name is never parsed for a span, although names often look like one.
- **`R-ipx3-4d`** Slots (`SlotCavity`) use a `PathShape` when the shape allows it and are otherwise counted. Backdrill
  specifications are reported by count and are not modelled, because the barrel stub affects RF results and the user
  should know it was left out.
- **`R-ipx3-4e`** Vias carry a net from their `Set` or `PadStack`. A via's copper reaches every layer in its span in the
  saved document, and the fixture checks this through the existing span resolver.

## 5. `R-ipx3-5` — the stackup

- **`R-ipx3-5a`** Walk `Stackup` → `StackupGroup` → `StackupLayer`, recursively where `layerOrGroupRef` names a group.
  Order by `sequence` when every entry has one, and otherwise by document order. A stackup read upside down simulates
  cleanly and answers the wrong question, so check the result against the `side` of the layers it names: top first,
  bottom last. If reversed, reverse it and report; if neither end agrees, refuse the stackup and keep the artwork.
- **`R-ipx3-5b`** An entry's kind comes from the `Layer` it references: copper functions are conductors and `DIEL*`
  functions are dielectrics. Where the referenced name has no `Layer`, use a `LAYER_TYPE` non-standard attribute if
  stated. Otherwise, an entry sitting between two copper layers with a nonzero thickness is a dielectric. Mask, coating
  and surface entries are skipped and counted. `materialType` decides nothing.
- **`R-ipx3-5c`** Thickness comes from the entry, in the units of `CadHeader`. `overallThickness` becomes
  `BoardThicknessDbu`. Compare the sum against it and report a mismatch over 5%.
- **`R-ipx3-5d`** Values in order of trust:
  1. `SpecRef` to a `Spec` holding `Dielectric` (`DIELECTRIC_CONSTANT`, `LOSS_TANGENT`) or `Conductor` (`CONDUCTIVITY`),
     with the unit stated on the `Property`.
  2. A non-standard attribute on the entry with a plain name: `LAYER_DIELECTRIC_CONSTANT`, `LAYER_LOSS_TANGENT`,
     `LAYER_ELECTRICAL_CONDUCTIVITY`, with the unit in its value, such as `mho/cm`. Report that the value came from an
     exporter's attribute and not from a `Spec`.
  3. Nothing: the value is missing.

  A conductivity with a unit not understood is treated as missing and reported; it is not assumed to be S/m. `mho/cm`
  is 100 S/m.
- **`R-ipx3-5e`** Frequency tables follow D2. `refValue` in another unit is converted through `refUnit`, and an
  unconvertible one is reported.
- **`R-ipx3-5f`** A missing or zero value does not stay at air. Complete the stackup through `SubstrateDefaults.Fill`,
  as the Gerber path does, and name every filled value in the message. A stackup with structure but no values, where
  every entry is a placeholder with zero thickness, is common. It is imported as structure plus named defaults, and the
  message says the file stated no values.
- **`R-ipx3-5g`** A `Spec` roughness is not read into `RoughnessRmsM`: the file's types (up-facing, down-facing, treated)
  do not state an RMS value. Report that it was present.
- **`R-ipx3-5h`** Rev C impedance `Spec`s, `Port`, `Model`, `BendArea` and `Dfx` are counted and skipped. Impedance targets
  would be useful to the Impedance tool, so the message names how many there were; using them is a separate piece of work.
- **`R-ipx3-5i`** Zones follow D4.

## 6. Gates

Hand-written fixtures in `testdata/ipc2581/`:

1. Nets on tracks, pads, vias and a pour surviving an erase, with `Component` and `Pin` in each spelling.
2. The three plane cases in `R-ipx3-3c`.
3. Via, plated hole, non-plated hole, blind span from each source, and a drill layer whose name disagrees with its `Span`.
   Assert no double copper where a via consumed a pad.
4. Stackup: one test each for `Spec` values, attribute values with a `mho/cm` conversion, a placeholder stackup completed
   with named defaults, a frequency table, a reversed order, and nested groups.

One test per claim. Run those classes only.
