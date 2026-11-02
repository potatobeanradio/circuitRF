# Brief AS-12 — The netlist a Gerber set declares: parts, pins and nets from its pad attributes

**Series:** `brief-artsch-0-overview.md` · **Tag:** `R-as12-<m>`
**Depends on:** AS-3 (board graph), AS-4 (parts and the parts table), AS-6 (emit)
**Area:** `src/Design/Layout/Recognition/` (a new evidence source and the net cross-check),
`src/Design/Layout/Interchange/GerberImport.cs` (only if an attribute is not yet carried), `docs/user/src`

---

## 1. Goal

A modern Gerber set states its own netlist. Every pad flash or region can carry the format's object attributes:
`%TO.P,<refdes>,<pin>[,<pin name>]*%` names the component pin the pad belongs to, and `%TO.N,<net>*%` names its
net. The import already reads both and keeps them on the shape (`LayoutShape.Pin` = `"C1,1"`, `"J1,1,In"`;
`LayoutShape.Net` = `"GND"`). Artwork → Schematic ignores them: it finds parts from placed instances, a placement
file, a BOM, land-pattern geometry and silkscreen text, and reads connectivity from copper alone.

Designer report, round 18: a two-layer LNA board's Gerbers carry `%TO.P`/`%TO.N` on every pad (C1–C7, L1–L5,
R1, FB1, U1, J1–J3), and the designer asked for the netlist to be used so the parts land on the board without the
guesswork. The information is already in memory after the import; this brief makes recognition read it.

## 2. Requirements

**R-as12-1 — A new evidence source, `PartEvidenceSource.DeclaredPads`.** Pads grouped by the refdes of their
`Pin` are one part each, with their declared pin numbers as its terminals. It implements `IPartEvidenceSource`
(AS-4's seam, not an edit of the reading) and ranks **after `Instance` and `Placement`, ahead of `Bom`,
`LandPattern` and silkscreen**: the file declares which pads form a part, which nothing geometric can match, but
it says nothing about kind or value.

**R-as12-2 — Kind and value.** Kind from the refdes prefix by the table AS-4 already uses (C, L, R, FB, J, U, Q,
D …); value from a BOM row when one is given, otherwise the parts table's unknown-value variable (AS-4's
`DefaultVariable`). A pin's NAME (the optional third field) is carried to the emitted terminal label where the
emitter has one — `"J1,1,In"` names the port.

**R-as12-3 — Terminals are the declared pins, in pin order.** A two-pad part's terminal 1 is the pad declaring
pin 1, not whichever pad the geometry met first — so a polarised or asymmetric part is wired the way round the file
says. A part of three or more pins is what recognition already does with one (block / SnP placeholder), with the
declared pin numbers as its port order.

**R-as12-4 — Connectors are ports.** A part whose refdes `PortDiscovery.IsConnectorRefdes` accepts, with a pad on
a signal island, gives a port at that pad (today's rule, now with a declared pad rather than an inferred one).

**R-as12-5 — Declared nets cross-checked against copper, never trusted over it.** Connectivity stays the copper's
(AS-3). The declared nets are a CHECK: two different declared nets on one copper island is a finding (a short, or
an import artefact), one declared net split across islands is a finding (an open — or a hairline gap, which AS-3's
joining already settles). Each finding names the nets and a location. Agreement is one summary line.

**R-as12-6 — Mixed evidence.** Where a placed instance or a placement row claims the same pads, the stronger
source wins as AS-4's ranking says, and a disagreement on refdes is reported, not resolved silently. Pads with
no `Pin` are read as today.

**R-as12-7 — Report and headless.** The recognition report gains one line: "N parts from pad attributes declared
in the artwork". `circuitrf recognize` and the MCP `recognize` tool need no new argument — the evidence is in the
layout.

## 3. Out of scope
- Reading a board tool's schematic or netlist file. The Gerber attributes are the contract; a board file
  (`.kicad_pcb`) import already carries footprints as instances.
- Inferring footprint cells from declared pads (`GerberImport` explains why it builds no hierarchy from `%TO.C`).

## 4. Gates (minimal tests, run only these classes)
- `DeclaredPadsEvidenceTests` — a synthetic board with two 0402 pad pairs declared `C1,1`/`C1,2` and `L1,1`/`L1,2`
  on a line, and a connector pad `J1,1,In`: recognition places C1 and L1 with terminals in declared order and a
  port named from `In`; the same board with the attributes stripped falls back to land-pattern evidence (today's
  result).
- A declared-net short (`GND` and `RF` pads on one island) and a declared-net open each give their finding; a
  board whose declared nets agree gives the summary line only.
- Acceptance: the designer's LNA Gerber set (kept local, not committed) recognises all eighteen parts with the
  pad attributes, and its declared nets agree with the copper.
