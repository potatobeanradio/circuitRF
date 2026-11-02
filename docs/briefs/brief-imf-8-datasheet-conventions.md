# Brief IMF-8 — Datasheet conventions: IC boxes, amplifier triangles, connectors, supplies, System blocks

**Series:** `brief-imf-0-overview.md` (F11, O5, O6, O7, L2, L6) · **Tag:** `R-imf8-<m>`
**Depends on:** IMF-5, IMF-6, IMF-7 · **Owner decisions:** O5, O6, O7 (2026-10-11)
**Area:** `src/Design/Schematic/Recognition/` (`SymbolTemplates.cs`, `SymbolMatch.cs`, `RecognisedSymbol.cs`,
`SchematicImageEmit.cs`, `SchematicImageDrawing.cs`, `LabelAssociation.cs`), `src/Design/resources/image-symbol-templates/`,
design note §24

---

## 1. Goal

Every datasheet application circuit is drawn around **the part the datasheet is for** — an IC box or an amplifier
triangle with pin numbers — connected to RF connectors, a supply and a handful of passives. IMF-3…IMF-7 make the
passives, the arrows, the terminals and the words read. This phase makes the centre of the drawing read: the box or
triangle becomes **one** part with the right pins in the datasheet's own pin order, the connectors become ports, the
supply becomes a supply, and the drawing places circuitRF's System components where the picture draws one.

## 2. Requirements

**R-imf8-1 — The IC box (O6).** A closed box (IMF-4) with three or more attachments is an **IC**. Its pins are its
attachments; each takes:
- its **pin number** — the glyph run IMF-4 tagged *at a pin* or *inside a box* nearest the attachment, inside the
  box's outline or in a small pin-number box on it (ds-a), or just outside on the lead (ds-f, ds-g); a run like `2,4`
  or `3(1)(5)[3]` gives the first number;
- its **pin name** — the known word (IMF-7) inside the box nearest the attachment and on its side (`RFIN`, `VCC`,
  `PWDN`), or just outside along the lead (ds-d: `Input`, `Output`, `Adjust`).
Emitted as an **SnP** whose ports are the box's non-ground pins **in pin-number order** (position order when no
number is read), each port named by its pin name; the grounded pins (a pin wired only to a ground symbol, or named
`GND`) are its reference. The part's name inside the box (`U1`, a part number) is its designator or PartNumber. The
report says "IC drawn as a box: give it a Touchstone file whose ports follow its pin numbers".

**R-imf8-2 — The amplifier triangle (O5).** A triangle (hollow or filled, IMF-2 keeps its outline) with leads at its
base and apex is an **amplifier**: emitted as the System **`Amp`** — `in+` the base lead, `out+` the apex lead,
`in-`/`out-` to ground. Leads elsewhere on the triangle: one to a ground symbol or pin-numbered as a ground (`2,4`
beside a ground) is dropped into the reference; a lead to a supply is drawn as a wire ending at the Amp's body,
reported "the supply pin of an ideal amplifier carries no current". `Gain` is a tunable global at the Amp's default;
every other parameter at its default. A triangle with `+` / `−` marks at its base (an op amp, ds-f) or more than one
input or output lead is an SnP by R-imf8-1's rules. In a datasheet gain block the output pin also carries the bias
(ds-b, ds-c: `RFC` from the supply into the output node) — that is drawn as drawn and needs nothing special.

**R-imf8-3 — RF connectors.** A **coax-connector** symbol — a small circle with a centre dot, its shell grounded
(ds-b, ds-e, ds-g) — at a wire's free end is a terminal; a **filled dot** 3–8 `w` at a free end (ds-a) is a terminal.
Both become Ports by IMF-6's named-terminal rule (named by the word beside them, numbered input first). The ground
symbol on a connector's shell is not drawn again (the Port is grounded).

**R-imf8-4 — Pin markers are not terminals.** An open circle **on** a lead — between a wire and a part's body (ds-c's
amplifier pins, ds-h's terminals inside the drawing) — is a pin marker: the lead passes through it (one net), it is
removed from the ink, and its pin number (if any) goes to R-imf8-1/2.

**R-imf8-5 — Supplies.** A supply arrow (IMF-3), a terminal with a supply word (IMF-6), or a short bar with a supply
word, names a **supply net** (`VCC`, `Vcc`, `+Vs`, `+3 or +5 V`). Emitted as IM-10 emits a supply today — the named
net, and a `Vdc` from it to ground **only** when a voltage is read (`+5 V`, `3.3V`: the first value of a list or an
`or`), else the named net alone, reported. A supply that is also an RF port's name is never both.

**R-imf8-6 — Not-populated parts.** A part whose value or a word beside it reads `DNP`, `NC`, `NF` or `OPEN`, or that
is drawn in a second colour the picture's legend or note marks as not fitted (IMF-2 reports the colour), takes the
reader's **existing** not-fitted rule (`BomTablePaste.IsNotFittedMarker` → `PartKind.Open` in `SchematicImageEmit`):
left open, its drawn kind and any value kept in the parts table, so the circuit simulates as the board is built and
the user can fit it with one edit.

**R-imf8-7 — System block symbols (O7).** Templates for the block symbols datasheets and their
block diagrams draw, emitted as their System component with default parameters: a box with one to three sine waves,
some struck through (**`Filter`**, its type from the struck waves — low-, high-, band-pass); a box marked `dB` or
`ATT` (**`Atten`**); a circle with a curved arrow (**`Circulator`**); a switch blade between contacts (**`Switch`**,
throw count from the contacts); a box with a through line and a coupled arm (**`Coupler`**); a transformer marked
`balun` or drawn single-ended to balanced (**`Balun`**). A **mixer** (a circle with an X) is an SnP. Each is reported
"default parameters — set them". The owner wants these (O7); they come last in this phase only because the
amplifier, the IC and the connectors come first.

**R-imf8-8 — Not drawing what is not circuit.** A switch drawn as `ON` / `OFF` line ends (ds-h's enable pin) is two
named open ends, not a part. Text tables beside the drawing (an `R BIAS` table, a pin table) are outside the crop's
circuit and, if inside it, are comments (IMF-7). A part name inside a box that is also a datasheet's own title is
the PartNumber, never a net.

**R-imf8-9 — The report and the overlay** name ICs with their pin counts and the pins read by number and name,
amplifiers, connectors, pin markers, supplies, not-fitted parts and System blocks; the parts table's Kind column
gains `IC` (the SnP), `Amp` and each System block.

## 3. Not in this phase
A Touchstone file for an IC (the user supplies it); a model for a not-fitted part; an IC's internal function drawn
inside its box (a block diagram inside a box is the box's detail, never parts).

## 4. Gates (minimal tests, run only these classes)
- `ImageDatasheetStyleTests`, at 1× and 2×: `ds-rf-amp`'s IC is an 8-pin SnP with ports in pin-number order named
  `VCC`, `RFIN`, `RFOUT`, `PWDN` and pins 3, 4, 6, 8 as its reference; `ds-gain-block`'s triangle is an `Amp` with
  `in+` on the C1 net and `out+` on the RFC/C2 net, its coax connectors Ports 1 and 2, its supply net named;
  `ds-regulator`'s IC is a 3-pin SnP named `Input`, `Output`, `Adjust`; `ds-ldo`'s IC has `VIN`, `VOUT`, `VEN` and
  `GND` as its reference; `ds-test-circuit` reads its triangle as an `Amp` and its dashed box as nothing. Turn those on.
- `DatasheetSymbolTests` (new, in-memory): pin numbers inside, on and outside a box; a `2,4` ground pin; a coax
  connector; a pin marker on a lead; a DNP part; a filter box and a circulator.
