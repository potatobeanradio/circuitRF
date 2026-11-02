# Brief IMF-6 — Ports, terminals, return rails and names, as datasheets and textbooks draw them

**Series:** `brief-imf-0-overview.md` (F9, O3, L2, L6) · **Tag:** `R-imf6-<m>`
**Depends on:** IMF-5 · **Owner decision:** O3 (2026-10-11)
**Area:** `src/Design/Schematic/Recognition/` (`SchematicImageEmit.cs`, `LabelAssociation.cs`,
`SchematicImageDrawing.cs`), design note §22

---

## 1. Goal

A datasheet draws a port as a **named terminal** — an open circle or a filled dot with `VIN`, `IN`, `RF In` beside it
— at the free end of a wire (ds-a, ds-c, ds-h). On ds-h the two named terminals already became ports; on ds-a and
ds-c they did not. A textbook draws a two-port as two pairs of open terminal circles joined by a bottom rail, with no
ground symbol; today every circle becomes its own port to ground (a, b, e, f) and the rail stays a floating net. And a
name a little way from its wire end names nothing (i: `RF+DC`, `DC`, `RF` read correctly and were dropped).

## 2. Requirements

**R-imf6-1 — A named terminal is a port (datasheets first).** An open circle, or a filled dot 3–8 `w` across, at the
**free end** of a wire, with a word beside it (any side, within 3 `h`), is a **Port** named by the word. A supply
word (`VCC`, `Vcc`, `+Vs`, `V+`, a voltage such as `+3 or +5 V`) makes it a supply terminal instead (IMF-8). A circle
**on** a lead between a wire and a part (ds-c's pin markers) is not a terminal (IMF-8). Coax-connector symbols are
IMF-8's and become ports by this rule.

**R-imf6-2 — Terminal pairs are ports.** Two terminals (open circles, or IMF-3's arrow-into-terminal ends) are **one
port** when any of: a voltage arrow spans them (IMF-3); they are vertically stacked within 0.5 `s` in x at the
drawing's left or right edge; or one of them is on a **return rail** (R-imf6-3). The upper (or left) terminal is the
port's `+`, the other its reference. An unpaired, unnamed terminal is a one-terminal port to ground, as today.

**R-imf6-3 — The return rail is ground.** When the picture draws no ground symbol, a net that holds the reference
terminal of two or more ports (or of one port and the reference side of a source) — a long horizontal net along the
bottom of the drawing, as every textbook ladder has — is ground. Drawn **as drawn**: the rail stays a wire, and one
ground symbol is placed at its left end (the only addition, reported: "the common return was taken as ground"). When
the picture draws a ground anywhere, no rail is promoted — every datasheet draws its grounds.

**R-imf6-4 — Names at a distance.** A word reads as an open wire end's name when it lies in line with the wire
(within 1 `h` across) **beyond its end** within 6 `h` — not 2 `w` — and no symbol or other wire is nearer. The end
becomes a Port carrying the name (`RF+DC`, `DC`, `RF`; `Vin`, `Vout`). A name matching a supply rule stays a supply
net as IM-9 reads it. A name by a junction rather than an end is a net label, as now.

**R-imf6-5 — Numbering.** Ports are numbered **input first**: a name reading as an input (`IN`, `RFIN`, `RF In`,
`VIN`, `Vin`, `RF-IN`) is port 1, an output (`OUT`, `RFOUT`, `VOUT`, `Vo`) the next, then the rest left to right, top
to bottom. An AC source (IMF-5's `P1Tone`) counts as an input.

**R-imf6-6 — Unknown parts (O3).** A two-pin region of no known kind is emitted, on a picture-read schematic, as a
**0 Ω resistor** whose designator is `?<n>` — it keeps the circuit connected as drawn and changes no value. It stays
selected after paste and is counted in the paste's outcome line (IMF-9). The artwork recognition keeps its own rule
(a C); the emit option that chooses is `SchematicImageOptions.UnknownAs`.

**R-imf6-7 — The drawing.** `SchematicImageDrawing` places each Port where the picture draws its terminal (the `+`
terminal of a pair), the reference wired to the rail as drawn; it never moves a wire to make room. A port made from a
name keeps the word as the Port's name, not as a separate label.

**R-imf6-8 — The report.** Ports by source (named terminal, terminal pair, arrow pair, name at a distance, single
terminal, `P1Tone`), rails taken as ground, unknown parts.

## 3. Not in this phase
Reading the words themselves (IMF-7); connector symbols and supply terminals (IMF-8).

## 4. Gates (minimal tests, run only these classes)
- `ImageDatasheetStyleTests` — `ds-ldo` gives Port 1 `VIN` and Port 2 `VOUT`; `ds-rf-amp` gives `RF In` and `RF Out`
  from its filled dots; at 1× and 2×. Turn those on.
- `ImageWebStyleTests` — `textbook-zigzag` gives exactly 2 ports and a ground on its bottom rail; `loop-coil-names`
  gives 3 ports named by its words; `ladder-dashed` gives 2 ports. Turn those on.
- `PortPairingTests` (new, in-memory): two stacked circles with a voltage arrow are one port; four circles on a
  ladder with a bottom rail are two ports and the rail is ground; a drawn ground suppresses the rail rule; `OUT` drawn
  left of `IN` is still port 2.
- `ImageScreenshotPasteTests` stays green (its picture draws a ground, so no rail is promoted).
