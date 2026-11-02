# Brief — a port whose feed has a neighbour is de-embedded against the wrong structure: the series

**Status:** the series is COMPLETE — briefs 1, 2, 3, 4, 6 and 7 all reported · **Date:** 2026-09-13 · **Area:** `src/Engine/Mom` (port calibration)
**Requirement tag for the series:** `R-pcal-n`

---

## 0. The short answer

circuitRF's planar kernel de-embeds every edge port with a two-line calibration whose standard is an
**isolated uniform line of the port's own cross-section**. When the DUT's feed is *not* isolated —
another conductor inside the length the standard replaces — the error box is measured on a structure
that is not the one being corrected, and D6's peel divides that mismatch by a₂₁² (~10⁴ at 1 GHz).

The result is not a mild inaccuracy. On a coupled pair it is **22 dB of error in S₂₁ at the bottom
of the band**, published with a note and no refusal.

This is a **known and documented** limitation, not a discovery — `PlanarCalibration.cs`'s own header
states it, `PlanarPorts.CheckFeedClearance` exists to warn about it, and `mom-engine.md` §10.6 lists
multi-mode ports as later work. What is new here is (a) a measurement of how large the error actually
is, (b) the observation that **the machinery to fix it mostly exists already**, and (c) the finding
that the obvious fix — have the solver separate the feeds automatically — **is the wrong mechanism**
and should not be attempted.

The user-facing half of this is written up as
[`docs/user/src/app-notes/an01-ports-and-coupling.md`](../user/src/app-notes/an01-ports-and-coupling.md).

---

## 1. What was measured, 2026-09-12

A synthetic coupled pair, chosen so the answer is independently known: two straight microstrips,
**254 µm wide, 3.83 mm long, 246 µm apart** edge to edge, on 0.9 mm FR-4 (ε_r 4.4, tan δ 0.02), ground
plane below, 1–7 GHz, edge ports at all four ends. Mesh: cells/λ 5, cells across 2, transmission-line
current model, accelerated solve. Files are reproducible from §5.

Because the pair is a uniform cross-section, **Kernel A solves it essentially exactly** and is the
oracle. Kernel B was then forced on the identical file.

| f (GHz) | S₁₁ Kernel A | S₁₁ Kernel B | S₂₁ Kernel A | S₂₁ Kernel B | σ_max A | σ_max B |
|---|---|---|---|---|---|---|
| 1.0 | −19.09 dB | −0.08 dB | **−0.11 dB** | **−22.75 dB** | 0.9992 | 1.0008 |
| 3.4 | −9.91 dB | −0.85 dB | −0.84 dB | −13.23 dB | 0.9973 | 1.0023 |
| 7.0 | −6.52 dB | −4.01 dB | −1.88 dB | −13.07 dB | 0.9970 | 1.0041 |

Kernel B is **non-passive at 48 of 51 frequencies** and reports, once per port:

> Port 1's feed has other metal 246 µm away, inside the 2.7 mm the calibration standard assumes is
> empty.

### 1a. It is not the mesh, and that was checked rather than assumed

- The run above meshed **7 cells across** the 254 µm conductor — a healthy mesh — and still failed.
- On the board this was first seen on, doubling cells per wavelength (5 → 10, cells across 2 → 4,
  accelerated solve, **26 minutes**) moved S₁₁ by **at most 0.19 dB**, left S₂₁ at −34.2 dB at 1 GHz,
  and left the answer non-passive at 51 of 51 points (worst σ_max 1.0044 → 1.0042).

A finer mesh computes the wrong error box more accurately.

### 1b. Separating the feeds fixes it completely

The same coupled section, with **4 mm of line at each port and the other conductor 6 mm away there**,
same mesh settings, same kernel:

| f (GHz) | S₁₁ | S₂₁ | S₃₁ | σ_max |
|---|---|---|---|---|
| 1.0 | −8.71 dB | −0.69 dB | −26.60 dB | 0.9983 |
| 3.4 | −3.79 dB | −2.76 dB | −16.33 dB | 0.9907 |
| 7.0 | −26.85 dB | −0.74 dB | −11.61 dB | 0.9884 |

**Passive at every frequency, no clearance breach, no passivity note.** So the mechanism is confirmed:
it is the neighbour inside the standard's length, and nothing else.

**Re-measured 2026-09-12 on the widened fixture.** `separated-pair` held its neighbour 4 mm away until
PCAL2, which is 4.16 substrate heights — inside the 5 the shipped driven threshold asks for, so the
series' own clean case would have been refused. It is 6 mm (6.38 h) now, and the numbers above are that
geometry's. The earlier table, at 4 mm, read −10.07/−0.49/−34.32/0.9986 at 1 GHz; the difference is the
extra electrical length, not a change of mechanism.

---

## 2. The obvious fix is the wrong one, and this is why

The tempting move is: the solver already **adds metal for the solve and removes it exactly**
(`PlanarFeedExtension`, which grew 2.573 mm of lead on two ports in the original run), so have it
separate the coupled feeds automatically and the user need not redraw anything.

**It cannot work, for a structural reason.** The lead `PlanarFeedExtension` grows is *collinear and
uniform*, which is the only reason it can be peeled exactly: it is a matched section of the port's own
line whose γ the calibration has already measured. To separate two coupled feeds you must **route them
apart**, and a bend is not a matched uniform section. The peel has no way to remove it. You would move
the reference plane to a point separated from the user's drawn metal by a discontinuity **the solver
invented**, trading a known, reported error for an unknown, unreported one — on geometry the user
never drew and cannot see.

That is the same class of mistake `PlanarFeedExtension`'s own header declines: *"Guessing any of those
would move metal the user drew, which is a worse failure than the one being fixed."*

**So the fix belongs on the standard's side, not the DUT's:** make the standard reproduce the port's
actual neighbourhood, instead of making the neighbourhood match the standard.

---

## 3. Why that is closer to reach than it looks

Three pieces already exist.

1. **The standard is already built from the DUT's own mesh, cell by cell** (D4) — the DUT's transverse
   gridlines across the port verbatim, its longitudinal cell run verbatim, bulk cells in the middle.
   It is not a re-mesh of an idealised line.
2. **The port profile is already multi-conductor.** `PlanarPortProfile` carries `Lines[]` plus
   `PositiveLo/Hi` and `NegativeLo/Hi` — it was built for coplanar ports, where the standard must
   reproduce the signal *and* its coplanar grounds. `SpanLoM`/`SpanHiM` exist precisely so
   `CheckFeedClearance` does not mistake the profile's own conductors for intruders, because
   **everything inside the profile is already reproduced in the standard.**
3. **Calibrations are already shared between ports** of the same cross-section — the four-port run
   reported "2 calibration(s) over 4 de-embedded port(s)".

What is missing is: a neighbour that is *outside* the profile today has no way to get *inside* it, and
where two ports sit on mutually coupled conductors the error box must stop being a per-port scalar.

### 3a. The one hard part

D6's peel is

    y_ij = (S_meas,ij − δ_ij·a₁₁) / (a₂₁(i)·a₂₁(j))

with a per-port **scalar** box (A11, A22, A21). Two ports on a coupled pair support **two modes** with
different γ and Z_c, so no assignment of scalars to those two ports can represent the port region.
That half is genuinely multimode (multiline) TRL and is the expensive brief.

**But the cheaper half is separable:** when the nearby metal is *not* a port — a passive trace, a
ground pour, a neighbouring net that is simply there — there is still **one driven mode per port**,
and widening the profile to include it may need no new algebra at all. That is a large fraction of
real boards, and it validates the profile-widening machinery before any matrix algebra is written.

### 3b. A constraint the series must respect

`PlanarDeembed.cs`'s header rules Kernel A **out** as an input to Kernel B's de-embedding:

> Kernel A is the ORACLE for Z_c, never an input. Reading Z_c or C_pul off `QuasiStaticKernel` and
> feeding it into B would make the phase table's own "A and B agree on a uniform line" gate a
> tautology and would import A's discretisation error into B's answer.

Kernel A solves coupled multiconductor cross-sections exactly and cheaply, so it is an obvious source
of modal γ and Z_c for a coupled port region — and that is exactly the shortcut the rule forbids. Any
brief that wants it must argue the case explicitly and propose what replaces the gate. It is not a
free win.

---

## 4. The briefs, in the order they are worth doing

**All four briefs have reported (2026-09-12), and so has brief 6 (2026-09-13).** Their findings are
in `src/Engine/Mom/RESOLVED.md`, "PCAL1 — how much clearance a calibrated port actually needs",
"PCAL2", "PCAL3", "PCAL4", "PCAL5" and "PCAL6". **Brief 4 was not made unnecessary by 3**: a driven
neighbour needs 2–3× the clearance of a passive one, so a design 3 makes safe can still be refused —
and brief 4 is what stops it being refused.

| # | Brief | Status after PCAL1 |
|---|---|---|
| 1 | **The investigation** — `brief-portcal-1-investigation.md` | **Done.** The clearance law is `s/h`: the neighbour's distance in SUBSTRATE HEIGHTS, and line width is inert (a 4× change in w moves the threshold 5 %). |
| 2 | **Stop publishing the bad answer** — `brief-portcal-2-refuse-not-warn.md` | **Done (2026-09-12).** Independent of 1's outcome and now carrying 1's numbers: the threshold must be its own setting, **5 h for a neighbour carrying a port and 2 h for one that does not**, and the margin it reports is `s/h`, because nothing the solve already computes bounds the error. |
| 3 | **A passive neighbour inside the port profile** — `brief-portcal-3-passive-neighbour.md` | **Done (2026-09-12).** The standard reproduces the neighbour and the fixture that was 18.0 dB out in S₁₁ and non-passive at 3 of 7 points comes back **at the A-vs-B floor and passive at every point** — at six of its seven frequencies. The seventh is the finding: a reproduced neighbour is open at both ends, so the STANDARD resonates at βL = nπ, and those points are named rather than fixed. Standards double, 4.57× → 9.14× the DUT's unknowns. `src/Engine/Mom/RESOLVED.md`, "PCAL3". |
| 4 | **Calibration groups and a modal error box** — `brief-portcal-4-modal-error-box.md` | **Done (2026-09-12).** The coupled pair the series opened on de-embeds to the A-vs-B floor and is passive across the band. `src/Engine/Mom/RESOLVED.md`, "PCAL4". |
| 5 | **A group's feed leads, a cut cell's clearance, a severed conductor** — no brief; owner report | **Done (2026-09-12).** `src/Engine/Mom/RESOLVED.md`, "PCAL5". |
| 6 | **Which separation a grouped port calibrates on** — `brief-portcal-6-separation-selection.md` | **Reported (2026-09-13), and it refuted its own framing AND its own remedy.** The selection rule is innocent: at the bottom of a band `ModeSeparationDegrees` is not the modes' distance, because the SHORT standard — 3 substrate heights, 1.5° of line at 200 MHz — cannot separate them at any Δℓ, and the near-zero readings a run refuses on are two corrupt curves crossing. Regrowing that standard was built and then removed: measured against the cross-section oracle it makes the published answer WORSE at every frequency. The refusal stands and now reports both numbers. Separately measured and reassuring: an ordinary grouped run is below the well-separated floor at every point down to 100 MHz. `src/Engine/Mom/RESOLVED.md`, "PCAL6". |
| 7 | **The mode-separation refusal is drawn on the wrong quantity** — `brief-portcal-7-separation-gate.md` | **Reported (2026-09-13), and the measurement refutes the brief's own title.** 93 de-embedded points scored against the kernel-A oracle: over a 9× swing of the measured separation the answer moves by 1%, and the two points it refuses on are the two BEST answers of their ladder — so brief 6's diagnosis holds. But the same family produced two runs that publish nonsense, and they fail through DIFFERENT quantities: a pair whose electrostatics reads 0.517° (over the floor) and whose measurement reads 0.026° publishes 13.6× its floor, and an equal-width triple whose electrostatics reads 0.28° while its measurement reads 0.53° publishes 7.5× its floor. Candidates (a), (c) and (d) each publish one of them; the shipped arrangement — the quasi-static question at setup, the measured one per frequency — catches both. **Nothing moved.** What changed is the refusal's remedy, which named the feeds and now names the two levers that bind: the sweep's BAND (0.26° → 1.15° on the same metal) and the MESH (0.19° → 2.94° with the edge mesh on). `src/Engine/Mom/RESOLVED.md`, "PCAL7". |

**One thing §0's "the machinery mostly exists already" got wrong, and it is worth recording**: it
does, up to the point where a 2N×2N cascade has to be formed from raw port scattering. A series delta
gap makes |S₂₁| ~ 1e-4 at the bottom of a band, and the cancellation in T₂T₁⁻¹ is of order
|S₂₁|^{−2N} — 1e8 at one conductor, which D5 already survives, and **1e16 at two, which is every digit
a double has.** Brief 4's own §2 is about the similarity transform that removes it; the point for
anyone reading this overview is that "the algebra generalises" and "the arithmetic generalises" were
not the same claim.

**Three findings that change how the rest of the series should be read:**

- **The 2.7 mm figure quoted throughout this document is the standard's own construction length, and
  the measured requirement is larger** — ≈ 4 h at 0.9 mm and ≈ 5.5 h at 0.225 mm for a driven
  neighbour, against the 3 h the engine tests. At exactly 3 h the coupled pair is still 0.17 out in
  |ΔS| and non-passive.
- **Neither de-embedding residual predicts the error, and structurally cannot** — `SolveErrorBox`'s
  inputs are the two standards and not the DUT, so they are bit-for-bit identical between the run that
  is 22 dB wrong and the run that is at the floor. σ_max is a good *detector* and a useless
  *estimator* (non-monotonic: the worst case measured reports a smaller passivity excess than a case
  with half the error).
- **A flare on the port's own net is a distinct case and is already handled** by
  `PlanarFeedExtension` — measured passive, with the clearance warning silent. An own-net flare that
  reports "0 m away" is one of that file's own declines (a lead that would run into other metal), so
  the blocker is a neighbour and it reduces to case 1 or 2.

---

## 5. Reproducing the measurements

**Both layouts are now committed, at `testdata/portcal/`** — see that folder's `README.md` for which
brief consumes which. `dotnet run --project src/Cli -- em testdata/portcal/coupled-pair/em/coupled-pair.cem`
reproduces §1; `separated-pair` is §1b. What follows is how they were built, for anyone rebuilding
them by hand.
Build them as a workspace with:

- technology: one conductor level (`Top Copper`), 0.9 mm FR-4 ε_r 4.4 tan δ 0.02, a ground-reference
  conductor below, `Bottom: Ground`;
- **coupled**: two rectangles, 254 µm × 3830 µm, lower edges at y = 0 and y = 500 µm, an edge port at
  each end of each;
- **fedpair**: the same coupled section, plus a 4 mm run at each port with the second conductor
  displaced 6 mm in y there (4 mm until PCAL2 — see §1b);
- `.cem`: 1–7 GHz, 51 points, `AnalysisKind: Planar` (Auto picks Kernel A on the coupled one, which is
  the oracle — run it both ways), `AcceleratedSolve: true`, PlanarMesh `{Auto: false,
  CellsPerWavelength: 5, EdgeCells: 2, MinCellsAcrossConductor: 2, CurrentModel: TransmissionLine,
  MeshFrequencyHz: 7e9}`.

`dotnet run --project src/Cli -- em <file>.cem` prints every note quoted above.

---

## 6. What this series does not claim

- **Not that the two-line calibration is wrong.** It is exact for the structure it is defined on. The
  defect is that circuitRF applies it to structures it is not defined on and publishes the result.
- **Not that this is the only source of non-passivity.** A coarse mesh moves reference planes off the
  drawn edge, and that is a separate, real problem — measured above as *not* the cause here.
- **Not that every nearby conductor matters equally.** How clearance scales was brief 1's question and
  is now measured (§4): with **substrate height**, not with line width; and a driven neighbour needs
  about twice what a passive one does. The 2.7 mm figure quoted above is the standard's own length,
  **not** the measured threshold — the measured one is larger.
