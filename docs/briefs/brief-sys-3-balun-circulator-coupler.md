# Brief SYS-3 — balun, circulator, directional coupler, 90° hybrid

**Read first:** `brief-sys-series.md`, then `brief-sys-2-ideal-s-block.md` — this brief is four
`FillS` implementations and their parameters. It adds no machinery.

Passive intermodulation is **out of scope here and arrives in SYS-4**. Every block below is
`ModelKind.Linear`, and every non-ideality defaults to the honest large number that means "absent"
(`MixerModel`'s two constants: snap to EXACTLY ideal above a threshold, so a freshly placed block
stamps no leakage entry at all).

---

## `Circulator` — 3 ports, 6 nets

The whole point of the component is that it is **non-reciprocal**: `S ≠ Sᵀ`, which no other
component in the repository is. Ports are numbered around the circle, and `Direction` says which
way energy goes.

| Parameter | Default | Meaning |
|---|---|---|
| `Direction` | `CW` | `CW`: 1→2, 2→3, 3→1. `CCW` reverses it. Drives the glyph (SYS-1). |
| `IL` | `0` dB | Loss along the forward path. |
| `Isolation` | `200` dB | Reverse leakage, 2→1 / 3→2 / 1→3. 200 means none. |
| `RL` | `200` dB | Return loss at each port. 200 means each port is exactly matched. |
| `Z0` | `50` Ω | |

```
S_forward = 10^(-IL/20)      on (2,1), (3,2), (1,3)      [CW]
S_reverse = 10^(-Isolation/20)  on (1,2), (2,3), (3,1)
S_ii      = 10^(-RL/20)
```

**Worth stating in the model's doc comment, because it is the reason the series is built the way
it is:** this S has **no Z matrix**. `det(I − S) = 0` exactly for the ideal case. Its Y does exist
and equals `(1/Z₀)·[[0,1,−1],[−1,0,1],[1,−1,0]]` — antisymmetric, zero diagonal, and itself singular
because every row and column sums to zero. SYS-4 needs that Y; SYS-2's wave constraint needs
neither. A test should assert `det(I − S) ≈ 0` for the ideal parameters, so the next reader who
wonders why the repository grew a third N-port stamp finds the answer executable.

## `Coupler` — 4 ports, 8 nets · and `Hybrid90`, the same component

Port order is `1 = IN`, `2 = THRU`, `3 = CPL`, `4 = ISO`.

| Parameter | Default (`Coupler`) | Default (`Hybrid90`) | Meaning |
|---|---|---|---|
| `Coupling` | `20` dB | `3.0103` dB | Coupled-port level below the input. |
| `Phase` | `90` ° | `90` ° | Phase of the coupled port relative to the through port: `0`, `90` or `180`. |
| `Directivity` | `200` dB | `200` dB | Isolated-port level below the coupled port. 200 means the isolated port is exactly isolated. |
| `IL` | `0` dB | `0` dB | Loss ADDED to the ideal split. |
| `RL` | `200` dB | `200` dB | |
| `Z0` | `50` Ω | `50` Ω | |

The ideal split is set by `Coupling` alone and is lossless:

```
c  = 10^(-Coupling/20)              t = sqrt(1 - c^2)        (so 3.0103 dB gives c = t = 1/sqrt2)
S31 = c · exp(-j·Phase·pi/180)      S21 = t                  both scaled by 10^(-IL/20)
S41 = c · 10^(-Directivity/20)      and the symmetric partners
```

`IL` is a loss on top of the split, not a substitute for it: an ideal 20 dB coupler already loses
0.044 dB through its main arm, and it must come out of the arithmetic rather than out of a
parameter.

**`Hybrid90` is the SAME engine component** (`EngineReference → "Coupler"`), with a different tile
and different defaults — the `Mixer`/`MixerD` precedent. It keeps its own instance prefix (`HYB`)
because a user does not swap a hybrid for a directional coupler mid-design and `HYB1` is the name
they expect; that is a deliberate deviation from the mixer's shared-prefix reasoning and belongs in
the registry comment.

**`Phase = 90` makes S complex, which is what SYS-2's `S(−ω) = conj(S(ω))` rule is for.** State the
consequence in the doc comment rather than letting a user discover it: a quadrature relationship
held at *every* frequency is an idealisation with no causal realisation — it is a Hilbert
transform, not a network — and circuitRF is a frequency-domain simulator, so it costs nothing here
and would be meaningless in a transient one. What it is NOT is a branch-line coupler: a real
quadrature hybrid holds its 90° over a band, and a user who wants that bandwidth should build one
from four `TLIN` quarter-wave arms. Say so, and say where.

## `Balun` — 3 ports, 6 nets (**D3**)

**The decision.** An ideal balun is an ideal transformer, and a transformer is exactly expressible
here as a **2-port with a floating second port** — circuitRF's port convention is already
differential, so `S = [[0,1],[1,0]]` with `Z₀₁ = 50` and `Z₀₂ = 100` IS an ideal 1:2 balun, exact at
every frequency including DC, with no approximation anywhere. What that form cannot express is
**imbalance**, because there are no separate balanced ports to be imbalanced between.

**Recommended: the 3-port, ground-referenced form**, because amplitude and phase imbalance are the
first thing a system user asks a balun model for, and because a 3-port matches the tile SYS-1 draws.

```
port 1 = UNB, port 2 = BAL+, port 3 = BAL-
S21 =  (1/sqrt2)·k·10^(-IL/20)
S31 = -(1/sqrt2)/k·10^(-IL/20)·exp(-j·(180 + PhaseImb)·pi/180)      k = 10^(AmpImb/40)
S11 = 0 (matched);  S22 = S33 = S23 = S32 = 1/2                     (the ideal 3-port balun)
```

| Parameter | Default | Meaning |
|---|---|---|
| `Zunb` | `50` Ω | Unbalanced port impedance. |
| `Zbal` | `50` Ω | Impedance of EACH balanced port to ground (so the differential impedance is `2·Zbal`). |
| `IL` | `0` dB | |
| `AmpImb` | `0` dB | Amplitude imbalance between the balanced outputs. |
| `PhaseImb` | `0` ° | Departure from 180°. |

The `S22 = S33 = S23 = 1/2` block is not a mistake and must be documented: a lossless, reciprocal
3-port **cannot** have all three ports matched, and a real balun does not isolate its balanced ports
from each other either — the common mode sees a mismatch. A user who wants the exact ideal
transformer instead should be told, in the user documentation, to use a 2-port with unequal port
impedances, which is what SYS-2's gate already proves works.

## Milestones

1. `CirculatorModel`, with the no-Z fact asserted by test.
2. `CouplerModel` serving both tiles, all three phases.
3. `BalunModel` in whichever form D3 selects.
4. Registry defaults, factory registration, parameter descriptions, elaborator net counts for all
   four.

## Must NOT

- Add PIM, or any parameter named for it. SYS-4 owns that and needs the linear behaviour settled
  first so it has something to be identical to.
- Model the coupler as coupled transmission lines, or the hybrid as a branch line. Both are real,
  causal, band-limited components a user can already build from `TLIN`; this brief ships the ideal
  frequency-flat block, and the documentation points at the other route.
- Refuse a physically impossible parameter set (a coupling above 0 dB, an amplitude imbalance of
  40 dB). Stamp what the user typed.

## Gates

- **Every S entry, independently computed** from the dB and degree values in the test, to 1e-12,
  for each block at its defaults and at three non-ideal settings.
- **Non-reciprocity is measured, not assumed:** the circulator's simulated `S21 ≠ S12` by the full
  isolation, and reversing `Direction` exchanges them exactly.
- **The circulator's ideal Y**, computed from the simulated S in the test, equals
  `(1/Z₀)·[[0,1,−1],[−1,0,1],[1,−1,0]]`; and `det(I − S) ≈ 0`.
- **Coupler energy balance:** at ideal settings `|S21|² + |S31|² = 1` to 1e-12 across the sweep, and
  the isolated port is exactly zero (no entry stamped, not 1e-10 of one).
- **Quadrature:** `arg(S31) − arg(S21) = −90°` at every swept frequency, and the conjugate rule
  holds — verify by stamping at a negative ω directly if HB does not supply one.
- **A hybrid drives a two-branch network:** two hybrids back to back reproduce the input (the
  classic quadrature-combiner identity), which catches a sign error no single-block test can.
- **Balun:** a differential load across BAL+/BAL− sees the stated impedance transformation, and
  `AmpImb`/`PhaseImb` at zero give exactly antiphase outputs of equal magnitude.
- HB: each block passes a two-tone signal with no products created (assert absence).
- `dotnet test tests/Core.Tests` and `tests/Engine.Tests`; write-up in `src/Core/RESOLVED.md`.
