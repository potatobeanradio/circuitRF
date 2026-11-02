# Brief 119 — Palace terminal ports by symmetry: the magnetic-wall edge spike

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d119-n` ·
**Precedent:** [brief 113-a](brief-em3d-113-a-terminal-port-redo.md) and its findings in `src/Design/RESOLVED.md`
§ "Terminal wave ports — brief-em3d-113-a"
**Area:** a scratch harness (not committed); findings in `src/Design/RESOLVED.md`; fixtures in
`testdata/em3d/terminal/palace-symmetry/`
**Depends on:** 113-a · **Blocks:** a later rewrite of brief 115 (Palace terminal S), if this spike says go.
**Not urgent:** terminal ports ship on openEMS only for now (overview D14). This spike decides whether Palace joins them.

---

## 0. Why this brief exists

Brief 113-a measured that Palace cannot give a coupled pair per-conductor (terminal) ports either way its documentation
describes:

- **One port rectangle per line, touching at the midline** (`examples/cpw`). Palace treats a touching port's edge as
  PEC in each port's 2D mode solve, so every port mode is half of the **odd** mode. The even mode is never represented:
  on the air stripline pair it is lost from the result (singular values 0.75 in a lossless structure), the thru is off
  1.17 dB and the far-end coupling 13.4 dB.
- **Several modes on one shared face** (`Mode` 2, `"Active": false`). The degenerate TEM pair comes back in a
  different mixture at each end, and S is non-passive (σ_max 1.27). That is upstream issue #328.

There is a measured hint in the first result. **The odd half of the answer is right:** two of the four singular values
are 1.0000, and they are the odd-mode subspace. Only the even mode is missing. The Palace maintainers' own suggestion,
in [#251](https://github.com/awslabs/palace/issues/251) and [#328](https://github.com/awslabs/palace/issues/328), is to
split the port and impose the symmetry of each mode on the edge between the halves: **PEC for odd, PMC for even**.

Palace has no key that makes the shared edge of two touching ports PMC. **It does have an explicit `PMC` boundary**
(`config["Boundaries"]["PMC"]`), it is the natural boundary condition of any exterior surface given none, and
`docs/src/guide/boundaries.md` says the 2D port solve honours PMC. So a port's edge can be made magnetic with documented
keys only, by making that edge border a PMC surface instead of the other port. This spike measures whether that gives
terminal S, and what it costs.

## 1. The documentation this spike rests on

Read before writing any input (Palace **v0.18.1**, the local tree at the pinned tag):

| Source | What it settles |
|---|---|
| `docs/src/guide/boundaries.md` — *Perfect magnetic conductor (PMC) boundary*, *Lumped and wave port excitation* | PMC is the natural condition on an exterior surface; the 2D port solve supports PEC and PMC; touching ports see each other's edges as PEC |
| `scripts/schema/config-schema.json` — `PMC`, `WavePortPEC`, `WavePort` | Every key used. There is a `WavePortPEC` and no `WavePortPMC` |
| `docs/src/examples/cpw.md`, `examples/cpw/mesh/mesh.jl` | The per-line construction 113-a reproduced and this spike changes one thing at a time from |
| Issues #251, #328, #171 | The symmetry route and why the shared face fails |

Search the issue tracker again for PMC on wave-port edges, symmetry planes and even/odd ports before running, and
record what is found. Read it, never post to it.

## 2. Method rules

The rules of brief 113-a §2 apply unchanged: start from 113-a's reproduced cpw construction and change one thing at a
time; no invented corrections (the even/odd combination below is the owner's chosen route, not an invention); runs
under 5 minutes each and about an hour of solver time in total; every number into `RESOLVED.md` with its geometry,
mesh size, wall clock and memory. **When a result does not make sense, search the issue tracker and the web for a
documented cause before theorising.** Judge any S entry below about −30 dB by its absolute error |ΔS|, not by dB or
degrees.

## 3. The combination, stated once

For a structure that is mirror-symmetric about the plane between the two lines, with ports 1 and 3 on lines 1 and 2 at
the near end and 2 and 4 at the far end, every terminal S entry is a half-sum or half-difference of an even-mode and an
odd-mode 2-port, each referred to the same terminal impedance:

```
S11 = (S11e + S11o)/2     S31 = (S11e − S11o)/2
S21 = (S21e + S21o)/2     S41 = (S21e − S21o)/2        (and the rest by the pair's symmetry)
```

Palace normalises each port mode to unit power, so each mode's S is referred to that mode's own impedance (its `Z_PV`).
Before combining, each modal 2-port is renormalised to the terminal reference Z0 with that mode's impedance: an even
port of the half-structure carries Z₀e, an odd one Z₀o. That renormalisation is RfCore's existing one, applied twice.
**This formula holds only for a mirror-symmetric structure.** Even-to-odd conversion is zero in it by construction, so
it cannot describe an asymmetric pair (§4d measures how fast that goes wrong).

## 4. `R-em3d119-1` — what to measure

The reference geometry is 113-a's **A** (air stripline pair, b 2 mm, W 1.2, S 0.4, t 0.02 mm, ℓ 15 mm, PEC walls 6 mm
out), with the same 2D reference (Z₀e 101.95, Z₀o 70.885 Ω) and 113-a's r2 and r3 mesh densities. 5 GHz first.

**a. Route A — the magnetic-wall edge, full model.** Keep 113-a's per-line ports, but separate the two port rectangles
on each end face by a thin strip of width g centred on the midline, and give that strip the `PMC` boundary. Each port's
mode solve now sees a magnetic edge, so each port mode is half of the **even** mode. Measure:

- Z_PV of each port against the 2D PMC-midline half-structure (101.94 Ω).
- That two of the four singular values are now 1 and that they are the even subspace (the odd one is now the lost one).
- The strip's own effect: g = 2, 1 and 0.5 element sizes. In 3D the strip is a small PMC patch on the end face, and its
  effect on the even mode should vanish as g → 0.

Then **combine** the even-mode entries of this run with the odd-mode entries of 113-a's touching-port run (PEC edge) by
§3, and compare with the 2D reference: thru, near-end, far-end, S11.

**b. Route B — the symmetry half-model.** Cut the geometry at the mirror plane and model one line only. The cut plane
is `PMC` in one run (even mode) and `PEC` in the other (odd mode); one wave port per end, one mode each, `VoltagePath`
signal → ground as before. This is entirely ordinary Palace: no touching ports at all. Combine by §3 and compare with
the same reference. Each run is about half of route A's size.

**c. The same on geometry B** (113-a's microstrip pair, εr 3.5), with route B only unless route A is clearly better on A.
Here the modes have different wavenumbers, so each run's Robin term is matched to its own mode, which the shared face
never was. The references are 113-a's 2D quasi-static values.

**d. How much asymmetry the combination tolerates.** Make A asymmetric in one controlled way: line 2 10 % wider, or a
2 mm stub on line 2 only. Route B cannot model it at all, so run route A on it (the halves are no longer mirror images,
but each port still sees one line), combine by §3 anyway, and compare with **openEMS** on the same geometry. openEMS
terminal ports need no symmetry (113-a). The size of the error at one asymmetry is what lets brief 115 decide whether to
refuse an asymmetric face or only warn.

**e. Passivity and losslessness.** For every combined result, report σ_max and σ_min. On these lossless structures a
right answer has both within 0.002 of 1. 113-a showed σ_max alone is blind to a missing mode.

## 5. Go/no-go for a Palace terminal-port brief

**Go** if route A or route B meets **0.05 dB / 0.5° on every entry above −30 dB, and |ΔS| ≤ 0.006 on the rest**, on A at
113-a's spacing (S = 0.4 mm) on a mesh that runs in under 5 minutes, with σ_max and σ_min within 0.002 of 1. B must meet
**0.1 dB / 1°** against its quasi-static reference. Report which route, its cost against one 113-a run, and §4d's
asymmetry error as the input to a refusal rule. **No-go** otherwise: report and stop. Terminal ports then stay
openEMS-only.

Whichever way it goes, the result says what a rewritten brief 115 would build: either route A (two runs on the drawn
model, with a PMC strip the lowering adds between touching ports), or route B (two runs on a half-model the lowering
cuts at a declared symmetry plane), and how circuitRF recognises a symmetric face.

## 6. Deliverables

- `src/Design/RESOLVED.md`: a section "Palace terminal ports by symmetry — brief-em3d-119", headed by the documentation
  and issues read, then every number as §2 asks, and the go/no-go.
- `testdata/em3d/terminal/palace-symmetry/`: configs, `port-S.csv`, `port-V.csv`, `port-Z.csv` and logs of the runs the
  findings rest on, with a README naming each departure from 113-a's runs. No meshes, no personal paths; shorten the
  launch line in logs as `testdata/em3d/eigen/wr90/palace.log` does.
- The overview: the D14 row's revisit note says what this spike found.

## 7. Scope

- No product code, no UI, no file-format change. Scratch harness only.
- Two-conductor pairs only. N > 2 conductors and asymmetric faces are reported on, not solved.
- Nothing is posted upstream.
