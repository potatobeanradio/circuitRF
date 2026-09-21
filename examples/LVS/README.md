# Layout versus schematic

Three designs that exist to answer one question — **does the artwork implement the drawing** — and
to be checkable by hand while they do it.

| | what it is | what it is for |
|---|---|---|
| **Attenuator** | a 3 dB resistive pi attenuator on a 2-layer board | the artwork matches the drawing, and LVS must say so with **nothing to report** |
| **Attenuator broken** | the **same** schematic, byte for byte, and a layout carrying **six deliberate faults** | each fault is one thing LVS exists to find |
| **Bias tee** | an MMIC cell on the shipped GaAs process — a spiral inductor, two MIM capacitors and a thin-film resistor | three things only a die can exercise: an undrawn ground, parameters on the layout side, and a device whose terminals are on different layers |
| **Attenuator bench** | the correct cell between two 50 Ω terminations, swept 0.1–6 GHz | something to run: press **Simulate** and read the attenuation |

> **`Attenuator broken` is broken on purpose.** It is not a mistake and it is not a bug report.
> Its schematic is a byte-for-byte copy of `Attenuator`'s; only the artwork differs, and it differs
> in six named ways listed below. It still passes `circuitrf check` with **zero errors** — being
> broken for LVS is not the same as being malformed, and a fixture that could not be opened would
> be testing the wrong thing.

Everything here is an ordinary editable workspace: open it, change it, re-run the generators.

---

## The correct board, and why this circuit

```
          R2 (17.4 ohm)
   IN ──────/\/\/\────── OUT
    │    ┌───┤├───┐       │
    │    └─ C1 1 pF ┘     │
    │                     │
   R1 (294)              R3 (294)
    │                     │
   ─┴─ ground            ─┴─ ground
```

It is the smallest design that is at once **symmetric**, **grounded**, **multi-layer** and boring
enough to verify by reading it. Run `Attenuator bench` and it reads **−2.97 dB at 0.1 GHz**, rising
to **−2.55 dB at 6 GHz** — the roll-off is C1 shunting R2, which is exactly what a part the
topology does not need looks like when you measure it.

- **Symmetric.** R1 and R3 are the same part, at the same value, in the same orientation. Swapping
  them is an *automorphism* of the netlist: the two circuits are indistinguishable, so a comparison
  cannot read the pairing off the designators and has to resolve it. A design where every part is
  distinguishable would never exercise the branch that is hardest to get right.
- **C1 is across R2**, which keeps that symmetry and is deliberately a part the topology does not
  need. Something has to be there that the attenuator does not require.
- **Grounded on drawn copper.** `Bottom Copper` is the stackup's ground reference *and* it draws,
  so the ground net here is ordinary metal and nothing is inferred. `Bias tee` is the other half of
  that story.
- **Multi-layer.** The top ground is three separate pieces, stitched to the bottom pour by four
  barrels. That is what makes an open reachable by deleting one of them.

**The artwork is generated**, from `Attenuator/layout/Attenuator.gen.py`:

```
python3 "examples/LVS/Attenuator/layout/Attenuator.gen.py" examples/LVS
```

It writes `Attenuator.clay` and nothing else, and it refuses to write anything at all if its own
audit of the geometry fails. Hand-drawn artwork in a fixture is artwork nobody can regenerate after
a format change.

---

## The six faults

`Attenuator broken/layout/Attenuator.break.py` reads the correct board and mutates it:

```
python3 "examples/LVS/Attenuator broken/layout/Attenuator.break.py" examples/LVS
python3 "examples/LVS/Attenuator broken/layout/Attenuator.break.py" examples/LVS --only F4
```

The no-argument form writes all six, which is the realistic case. `--only Fn` writes exactly one,
which is how you see a single finding on its own.

| | the mutation | what LVS should say |
|---|---|---|
| **F1** swapped net | R2's output link is re-pointed: it runs up into the top ground instead of right into the output trace | R2's terminal 2 is on the wrong net, **naming both** — it is on ground and it should be on the output. Not an open: the output net is still there, fed by R3 and C1 |
| **F2** missing part | C1's placement is deleted; its two risers and its run are left bare | C1 is in the schematic and not in the layout |
| **F3** extra part | a second 294 Ω resistor, designator `R4`, placed and wired to nothing | R4 is in the layout and not in the schematic |
| **F4** short | a 0.2 mm spur of copper from the lower ground strip up to the input trace | IN and ground are one net — **with the path, and the spur's own coordinate**. A short reported without its path is a finding nobody can act on |
| **F5** open | the upper-left ground's only stitching via is deleted | ground is in **two** islands, and R1's return is on the one that reaches nothing else |
| **F6** wrong value | R3's placement is re-pointed from the 294 Ω part to a 150 Ω one | R3's value disagrees — *schematic 294 Ω, layout 150 Ω, tolerance 1 %*. Same land pattern, same designator, same topology, so it is the one fault here that is not a topology fault at all |

**F5 splits the ground into two islands, not three.** A via joins at most one piece of copper per
conductor, so on a two-layer board it is an edge of degree two and deleting one edge of a tree
splits it in two. Three islands from one deleted via is not reachable at any geometry.

---

## The MMIC cell

`Bias tee` is a die: two RF pads, a MIM DC block between them, a spiral inductor feeding the bias
node, a MIM capacitor decoupling it to ground and a thin-film resistor out to a gate pad. It runs
on `tech/mmic-GaAs_2LM_100um.ctech`, which is the **shipped** starter technology copied in with
nothing changed — partly to prove that technology works.

Three things here that a board cannot show:

1. **The ground reference is not drawn.** `Backside Metal` has no drawing layers at all, so the two
   backside vias terminate on metal that exists in the *stackup* and nowhere in the artwork. Read
   naively, every grounded device on this process reads as open. LVS says so on every run that
   relies on it, because it is the one inference the user cannot see on their own screen.
2. **The parts carry parameters.** Each cell under `parts/` records the generator that drew it, the
   resolved values it drew from, and which of those the generator *derived* from its own geometry
   rather than reading. A board's land pattern claims nothing about resistance; these do.
3. **The MIM capacitor is a multi-layer device.** Its bottom plate is Metal1 and its top plate is
   MIM Metal, joined by neither a via nor a touch — the dielectric between them *is* the device —
   and it comes out on Metal2 through a MIM via. So terminal 1 is on layer 1 and terminal 2 is on
   layer 2. A pin lookup that ignores layers passes every board test and fails here.

**There is no broken copy of this cell**, and that asymmetry is deliberate: its faults are produced
in a test by mutating the loaded model, because a second MMIC would double the artwork for faults
the board already covers.

```
python3 "examples/LVS/Bias tee/layout/Bias tee.gen.py" examples/LVS
```

That writes the four part cells and the cell itself, and prints the table below.

### What the geometry actually resolves to

Each part is drawn on a 0.25 µm grid, so the value it ends up with is not exactly the value the
schematic asked for. The gap is the **quantisation of a correct design**, and it is the floor under
any property tolerance: a tolerance tighter than this rejects good artwork.

| part | dimension | requested | resolved | spread |
|---|---|---|---|---|
| `MIM-0P8P` | capacitance | 0.8 pF | 0.79844 pF | 0.195 % |
| `MIM-4P0P` | capacitance | 4.0 pF | 3.99861 pF | 0.035 % |
| `SPIRAL-1N2` | inductance | 1.2 nH | 1.19985 nH | 0.013 % |
| `TFR-62R` | resistance | 62 Ω | 61.875 Ω | 0.202 % |

Re-run the generator and it prints these again; if a change to a generator widens one of them past
the tolerance, that is a real regression and the test suite fails on it.

That table is also where LVS's **property tolerances** come from. The widest gap above is 0.2 %, and
one step of the E96 series — the smallest wrong part anybody could have fitted — is 2.4 %, so the
shipped default sits between them at **1 %**, for resistance, capacitance and inductance alike. A
length is compared to one database unit instead, because a smaller difference cannot be drawn. Every
other dimension is compared exactly and the report says that no tolerance has been measured for it,
rather than a plausible number being invented. The numbers are provisional, they are overridable per
technology in the `.ctech`, and **every finding prints the tolerance it applied**, so a wrong default
shows up on the line it produced.

---

## Reading the workspace

```
LVS/
  tech/
    pcb-2layer-lvs.ctech          a trimmed 2-layer board process
    mmic-GaAs_2LM_100um.ctech     the shipped MMIC stackup, byte for byte
  footprints/                     the board's parts: one land pattern each, with the value it is for
  parts/                          the die's parts: a spiral, two MIM caps, a thin-film resistor
  Attenuator/                     the correct board — schematic, symbol, layout
  Attenuator broken/              the same schematic and symbol, six faults in the artwork
  Attenuator bench/               two terminations and an S-parameter sweep
  Bias tee/                       the MMIC cell
```

Each cell's `.ccell` states its **terminal map** — which layout pin is which schematic port. None
of the three designs relies on a positional guess, which is what lets them be an oracle for
anything: a fixture whose correspondence was derived by counting pins would be testing the
derivation rather than the comparison.

The board's parts are named after what they *are* — `R0402-294R` is a 294 Ω 0402, not a bare land
pattern — because that is the only place a board layout can state a value. `R0402-150R` exists for
one reason: to be the wrong part F6 points at.

## Two things this workspace does not do

**The three design cells declare no analysis.** They are cells with ports, not benches, so
`circuitrf check` reports one warning each saying no analysis will dispatch. That is accurate —
`Attenuator bench` is the one that runs, and it is what a cell is for.

**No copper carries a net name except the board's ground pour.** That is not an oversight either —
a `Net` stamped on a trace that touches unnamed copper (a footprint's pad, which lives in a shared
cell and can never carry a board net) is currently reported as a minimum-spacing violation. The
pour is the only shape on its layer, so it is the one place a name is safe. `examples/RESOLVED.md`
records the detail.
