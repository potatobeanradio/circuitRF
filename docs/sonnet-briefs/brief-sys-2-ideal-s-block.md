# Brief SYS-2 — `IdealSBlockModel`: one stamp for every ideal S-matrix, and its first two users

**Read the series brief first:** `brief-sys-series.md` — in particular the finding that the ideal
circulator has no Z matrix and the ideal through no Y, which is why this brief exists at all.

This brief builds the shared machinery every later block in the series is a thin subclass of, and
proves it with the two simplest members: the **attenuator** and the **switch**.

**Read first:** `src/Core/Devices/ZPortModel.cs` (the Group-2 branch expansion, and the 2N-net ±
pair convention this brief reuses verbatim); `src/Core/Devices/SnpModel.cs` (the same expansion
with a reference node, and its refusal style); `src/Core/Devices/ChainModel.cs` (whose whole doc
comment is the argument for stamping a form that does not degenerate);
`src/Core/IMnaContext.cs`; `src/Core/ComponentModel.cs`; `src/Core/Devices/MixerModel.cs` (the
non-ideality conventions and the factory/elaborator wiring); `src/Engine/HarmonicBalance/
HbLinearExtractor.cs` §`StampAt`/`StampInto`.

## The stamp

One branch-current unknown `iₚ` per port; `vₚ` is the port voltage across that port's own ± pair
(`Nodes[2p] − Nodes[2p+1]`), the same convention `ZPortModel` uses. For a block with per-port real
reference impedances `Z₀ₚ` and an S-matrix `S(ω)`:

```
constraint row p:   (vₚ − Z₀ₚ·iₚ)/√Z₀ₚ  −  Σ_q S_pq(ω)·(v_q + Z₀_q·i_q)/√Z₀_q  =  0
KCL:                iₚ flows from Nodes[2p] to Nodes[2p+1]
```

In `IMnaContext` terms: `AddBranch()` per port; `AddBranchCurrent(bₚ, Nodes[2p], Nodes[2p+1])`;
`AddConstraint(bₚ, node, coeff)` for each port's ± node with `+1/√Z₀ₚ` on the diagonal port and
`−S_pq/√Z₀_q` elsewhere (negated for the − node of each pair); `AddBranchConstraint(bₚ, b_q, coeff)`
for the current columns, `−√Z₀ₚ` on the diagonal and `−S_pq·√Z₀_q` off it.

**Why this and not Z or Y.** It is the definition of S rather than a transformation of it, so it
has no singular case: the ideal through (`S = [[0,1],[1,0]]`) reduces to `v₁ = v₂`, `i₁ = −i₂` — an
ideal wire, which MNA represents routinely — and the ideal open (`S = I`) reduces to `i = 0`. Both
are states a switch is actually placed in, and both are what a filter degenerates to at DC.

**Three rules the base class owns, so no subclass can get them wrong:**

1. **`S(−ω) = conj(S(ω))`.** Keyed on the sign of the `omega` handed to `Stamp`. Verify what the HB
   linear extractor actually passes (it caches per ω = k·ω₀ and has an explicit DC entry); the rule
   is right either way and costs one line. A block with a real S never notices it; the quadrature
   coupler is wrong without it.
2. **`Z₀ₚ > 0`.** A zero or negative reference impedance is not a port. Fall back to 50 Ω rather than
   producing a NaN mid-factorisation, exactly as `MixerModel`'s constructor does with its port
   impedances.
3. **The net count.** `PortCount` ports means exactly `2·PortCount` nets, validated in the
   `Elaborator` with a message that NAMES the instance — the `Mixer` precedent
   (`Elaborator.cs`, and its line in `tests/Firewall.Tests/user-facing-text-allowlist.txt`).
   Generalise the mixer's check rather than adding ten more copies of it.

## Shape of the class

```
src/Core/Devices/System/IdealSBlockModel.cs      # abstract base: ports, Z0, the stamp, the rules
src/Core/Devices/System/AttenuatorModel.cs       # S from Loss and RL
src/Core/Devices/System/SwitchModel.cs           # S from State, IL, Isolation, OffState
```

The base declares `protected abstract void FillS(double omega, Complex[,] s)` — filled in place
into a buffer the base owns, so a frequency-flat block computes its S once in the constructor and
copies, and a frequency-dependent one (SYS-6) evaluates per ω. `Kind => ModelKind.Linear` here;
SYS-4 makes it conditional, so **write it as a virtual/derived property from the start** rather
than a hard `Linear`, and leave a comment saying why.

`TerminalNames` should be overridden per block (`"1"`, `"2"`… is the base default; a coupler's
`"in"`, `"thru"`, `"cpl"`, `"iso"` is what a branch-current cube key should read).

## The two first users

### `Atten` — 2 ports, 4 nets

| Parameter | Default | Meaning |
|---|---|---|
| `Loss` | `10` dB | Insertion loss, a positive number. `S21 = S12 = 10^(−Loss/20)`. |
| `Z0` | `50` Ω | Reference impedance of both ports. |
| `RL` | `200` dB | Return loss. 200 means EXACTLY matched — `S11 = S22 = 0`, no entry stamped. |

`Loss = 0` with `RL` at its default is an ideal through, and that is a legitimate thing to place:
after SYS-4 it becomes the standalone PIM generator the series brief describes.

### `Switch` (SPST, 2 ports) and `SwitchD` (SPDT, 3 ports)

One engine component, `"Switch"`, with the throw count as a parameter the registry seeds per tile —
the `Mixer`/`MixerD` pattern, and the reason is the same: nothing electrical distinguishes them
beyond how many throws exist, so there must not be two models.

| Parameter | Default | Meaning |
|---|---|---|
| `State` | `1` | Which throw is closed. `0` = all open (SPST: open; SPDT: both throws open). |
| `IL` | `0` dB | Insertion loss of the closed path. |
| `Isolation` | `200` dB | Open-path leakage. 200 means none. |
| `OffState` | `Reflective` | `Reflective` — an open throw is an open circuit (`S = 1` at that port). `Absorptive` — it is `Z0` to its reference (`S = 0`). |
| `Z0` | `50` Ω | |
| `RL` | `200` dB | Return loss of the closed path. |

`State` being a plain parameter is a feature worth documenting: a parametric sweep over `State`
gives every switch position in one run, and the schematic glyph follows it (SYS-1).

An SPDT's two throws are not symmetric in the S-matrix — the closed throw carries `10^(−IL/20)`, the
open one `10^(−Isolation/20)`, and the two throws see each other through whichever leakage terms
the state leaves. Derive the whole 3×3 explicitly in the model's doc comment; do not let a reader
reconstruct it from the code.

## Milestones

1. **The base and its stamp**, with the three rules, plus the elaborator net-count check
   generalised from the mixer's.
2. **`AttenuatorModel`**, factory registration (`PrimitiveTypeNames` + the `typeName.Equals` branch
   in `ComponentModelFactory.Create`), registry defaults, and its S derivation documented.
3. **`SwitchModel`**, both tiles, both off-state behaviours.
4. **The degenerate cases, on purpose.** An ideal through and an ideal open each solve, in
   S-parameters and in HB, at DC and above it.

## Must NOT

- Touch `src/Engine`.
- Convert S to Z or Y anywhere in this brief. That conversion is what the brief exists to avoid, and
  it is only ever reintroduced deliberately, in SYS-4, where the memoryless form genuinely needs it
  and guards its own singular case.
- Give a block a reference-node parameter or an N+1-net mode. Every port here has its own ± pair;
  the ground-referenced schematic tile supplies the `"0"` nets at extraction (SYS-1).
- Add a "reciprocity" or "passivity" check that refuses a user's numbers. A user is allowed to type
  a gain into a coupling. Refuse only what cannot be stamped.

## Gates

- **S in, S out.** A one-block netlist terminated in ideal ports, swept, returns exactly the S the
  parameters state — computed independently in the test from the dB values, to 1e-12. Do this for
  the attenuator at three losses (including 0 dB) and for the switch in every state, both off-state
  behaviours.
- **Reference-impedance independence.** The same attenuator measured with 50 Ω and with 75 Ω
  terminations gives S values that renormalise into each other. This is the gate that catches a
  `√Z₀` dropped from the constraint row, which nothing else will.
- **Unequal port impedances.** A block with `Z₀₁ = 50`, `Z₀₂ = 75` and `S = [[0,1],[1,0]]` is a
  lossless ideal transformer, and its measured S in a uniform 50 Ω system must equal the
  renormalisation of the identity — a closed form the test computes itself.
- **DC and HB.** An ideal through and an ideal open both solve at ω = 0; a two-tone HB run through a
  10 dB attenuator moves every tone down 10 dB and creates nothing (assert the absence of products,
  not only the presence of tones).
- **Cascade.** Two 10 dB attenuators measure 20 dB; an attenuator and a length of `TLIN` in either
  order give the same S. Sanity that the branch bookkeeping is not order-dependent.
- Elaborator refusal on a wrong net count, named, with its allowlist line. `dotnet test
  tests/Core.Tests tests/Engine.Tests` (two invocations) and `tests/Firewall.Tests` green.
  Write-up in `src/Core/RESOLVED.md`.
