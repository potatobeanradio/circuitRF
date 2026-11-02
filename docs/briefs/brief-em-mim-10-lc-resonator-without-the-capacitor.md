# Brief MIM-10 — an LC resonator that can be simulated this week

**SECOND of the round, and its gating experiment has already been run — it came back clean.** MIM-12
is the real fix and it is weeks; this is what lets a designer close an LC resonator in the meantime,
and on the evidence below **most of it is documentation and a kit change rather than engine work**.

This is the route that works with the kernel as it stands, and it is not a workaround bolted on for
that reason — **it is what a MMIC designer does anyway.** A MIM capacitor on a known process is a
part with a compact model; nobody spends unknowns solving a parallel plate whose capacitance is
`ε₀εᵣA/d` to a fraction of a per cent.
What earns the EM run is the coil, the crossover, the ground return and the coupling — and every one
of those is on Metal1 and Metal2 and is nowhere near the 0.2 µm film.

The kit already computes the capacitance: `KIT_MIMCAP` carries `C` as a computed parameter and reads
1.0838 pF for the 60 × 60 µm plate, which is `ε₀ · 6.8 · A / d` and which MIM-8's own instrument
independently confirms to **0.3 %**. That number is better than the EM will ever be, and it is free.

## What is missing today

**A port inside the layout.** Both existing ports on this structure are edge ports on outer metal.
To EM the coil alone and hand the capacitor to the circuit simulator, the coil's inner terminal has
to become a port in the middle of the artwork, and the two halves have to meet again in a schematic.
The pieces mostly exist:

- The layout model already carries port kinds beyond `Edge` (`LabelShape.PortKind`, with null meaning
  "infer it from the artwork" — `LayoutPortDirection.KindOf`).
- `EmPortExtraction` already resolves a port onto the mesh and reports what it landed on.
- The de-embedding already declines gracefully where it cannot measure a standard, and already adds
  a uniform lead where a feed has a neighbour in it — it says so per port.

What is not established is the **calibration** for an internal port, and that is the whole content of
this brief. An edge port de-embeds against two uniform lines of the port's own cross-section. An
internal port on a spiral's inner turn has no such line, and the honest answers are, in order:

1. **A DELTA-GAP port with no de-embedding at all**, reported as such. The reference plane is the gap,
   the answer carries the port's own discontinuity, and the note says so in one sentence rather than
   pretending to a calibration. For a resonator this is very often enough: the capacitor that follows
   is 1 pF and the port parasitic is femtofarads.
2. **The same two-line calibration, on a stub the user draws.** If the inner terminal is brought out
   on a short uniform lead — which `KIT_SPIRAL` already does, it has a Metal2 crossover and a landing
   pad — then the existing machinery applies unchanged and the port is an ordinary edge port on that
   lead. **This may be the whole answer**, and it should be tried before anything is built: take the
   user's own layout, delete the `KIT_MIMCAP` instance, put an edge port on the pad the capacitor was
   abutting, and run it. If that gives a clean passive two-port with a sensible L and Q, MIM-10's
   engine work is *documentation*, not code.
3. **An internal port with a declared reference plane**, which is the general case and the largest
   piece. Not until 2 has been measured.

### Step 0 is done: option 2 works

Measured 2026-09-16. The user's own `SpiralInductor.clay` with the `KIT_MIMCAP` instance removed and
an edge port on the landing pad it was abutting. With no plate level in the run, `PatternedDielectric`
turns the film to air and the structure is ordinary Metal1/Metal2 interconnect — two levels 6 µm
apart, cell/separation of order 6, nowhere near MIM-12's floor of 40:

| f | σ_max(S) | `−1/Y₂₁` |
|---|---|---|
| 1 GHz | **0.99775** | 0.21 + 37.11j Ω |
| 2 GHz | **0.99764** | 0.57 + 44.77j Ω |
| 3 GHz | **0.99748** | 0.69 + 58.91j Ω |

**Passive at every point, no NOT PASSIVE warning, and no caveat written into the `.s2p`.** That is
the whole of option 2's premise confirmed, and it is why this brief is sized as documentation.

**Two honest caveats on that table.** The port-2 position is a RECONSTRUCTION — the capacitor was
deleted and a port placed where it had attached, not the owner's own file — so reproduce it from the
real layout before quoting it. And the series reactance does **not** read as a constant inductance:
5.91 / 3.56 / 3.13 nH at 1 / 2 / 3 GHz, falling. That is a distributed structure with a Metal2
underpass, not a lumped coil, so **the gate below must not be a single L number** — compose the
`.s2p` in a circuit and gate on the composed response.

## The other half: the two pieces have to meet

The coil's `.s2p` and the capacitor's model have to compose into one circuit, and that is an ordinary
circuitRF job — an SnP component and a capacitor in a `.cnl`, run as `sparam`. Two things make it
worth naming rather than leaving to the user:

- **The kit should offer the schematic side.** `KIT_MIMCAP` already has a `.csym` and a schematic
  view. If its computed `C` reaches the schematic as the capacitor's value, the composition is two
  parts and a wire and the number is the kit's own.
- **The reference impedance and the port order of the coil's `.s2p` have to be right**, and the EM
  run reports both. A note on the EM setup saying "this result is meant to be composed, here is the
  port order" is cheap and prevents the failure where someone swaps them and gets a plausible curve.

## What this does NOT do, and must say so

**It does not model the capacitor's own parasitics** — the plate-to-backside capacitance of the
bottom plate through 100 µm of εᵣ = 12.9, the MIM via's inductance, the Metal2 strap. Those are not
negligible: the T-model shunt arm of the user's own run reads a very stable 0.113 pF across the whole
band, and a 1 pF series element with 0.11 pF of shunt beside it is a different resonator from an
ideal one. (That 0.113 pF comes off a NON-PASSIVE matrix and is indicative, not a measurement — but
0.1 pF is also what a 200 µm structure over 100 µm of εᵣ = 12.9 has to ground on geometry alone, so
the order is not in doubt.) So the cut must be made where the parasitics stay in the EM:

**Cut at the PLATES, not at the part.** EM everything except the 0.2 µm gap: the bottom plate, the
top plate, the via, the strap — all of it — with an internal port on each plate, and insert the
1.0838 pF between them as a circuit element. That keeps every parasitic in the full-wave answer and
takes out only the one quantity the solve cannot read and the closed form gives to 0.3 %. It is also
the reason option 3 above (a genuine internal port) is the general answer and option 2 is the
shortcut: a port on a plate face is exactly an internal port.

**It is not a substitute for MIM-12.** A user who draws a capacitor and presses Simulate must get a
right answer or a refusal, and MIM-10 gives neither on its own. It gives a designer a way to finish
the resonator while MIM-12 is built, and it gives the engine the internal-port capability that the
proper fix wants anyway.

## Gates

- The user's spiral, capacitor instance removed, edge port on the landing pad, run from the REAL
  layout: **passive at every point** and σ_max within 1e-3 of the table above.
- That `.s2p` composed in a `.cnl` with the kit's own 1.0838 pF: **a series resonance**, and the
  composed response — not a single extracted L — is the gate.
- The same resonance from the plate-level cut, once internal ports exist. Until then this is the
  gate's open end and must be recorded as such rather than quietly dropped.
- A one-page note in the example workspace's `README.md` saying this is how an LC resonator is
  simulated today, and why.
