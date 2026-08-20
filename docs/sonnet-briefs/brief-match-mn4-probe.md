# Sonnet Brief — Match MN-4: probing the external network

**Design:** `docs/design/match.md` §10 (and §5.4 for why the conjugate case needs inductive
terminations). **Depends on MN-1, MN-2, MN-3.** This brief implements the **Probe** button: looking
outward from a `Match` pin into the circuit it is placed in, extracting that network's impedance over
the design band, fitting a two-element termination model to it, and filling in the termination fields.

**Where findings go: `src/Engine/RESOLVED.md`** for the extraction and the fit, `src/Ui/RESOLVED.md`
for the button and its states. **Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests     --no-build
dotnet test tests/Firewall.Tests --no-build
```

Separate commands (`MSB1008`). `Engine.Tests` is ~3 min 24 s on its own — `--filter` on your own class
while iterating.

---

## 0. What this is for, and the one thing to get right

A designer places two FETs with their bias networks, drops a `Match` between them, and presses Probe on
each side. The left button reports the stage-1 output (a parallel RC — Ropt with Cds); the right
reports the stage-2 input (a series RC — Rgs with Cgs). They now have a real interstage matching problem
stated in real numbers, in about four seconds.

**The one thing to get right:** the probe must look at the network *without* the `Match` in it. With
the instance deleted the two sides are electrically separate, so each probe sees only its own side —
which is exactly what "looking outward" means. Leaving the `Match` in place measures the network in
series with the thing you are trying to design.

---

## 1. `TerminationProbe` — `src/Engine/Match/TerminationProbe.cs`

It lands in `src/Engine`, not `src/Core`, because it needs `SParameterEngine`. Signature roughly:

```csharp
public static ProbeResult Probe(
    TestBench bench,          // the enclosing testbench, already extracted by the caller
    string matchInstanceName,
    int pinIndex,             // 0 = Term1 side, 1 = Term2 side
    double f1, double f2, int points,
    bool conjugate);
```

Algorithm:

1. Work on an **in-memory copy** of the testbench. Never mutate the caller's.
2. **Delete the `Match` instance.**
3. Attach a `Term` (`Num = 1`, `Z = 50 Ω`, ground reference) to the net the probed pin was on.
4. **Keep every DC source and bias network.** The interesting case is a transistor, and a transistor's
   small-signal impedance is only meaningful at its operating point.
5. Replace the bench's analyses with a single S-parameter sweep over `[f1, f2]`, `points` points.
6. Elaborate and run `SParameterEngine`.
7. `Z(f) = 50·(1 + Γ)/(1 − Γ)` from `S11`.
8. If `conjugate`, use `Z*(f)`.
9. Fit (§2), rank (§3), return.

**Nonlinear devices** linearise at their DC operating point through the existing mechanism
(`docs/design/nonlinear-in-linear-engines.md`). **If the DC solve fails, the probe refuses and reports
the DC failure** — it must not return an impedance computed at zero bias, which would be a plausible
number and a wrong one.

---

## 2. The four fits — all closed-form linear least squares

Each candidate is linear in its two unknowns in the right domain, so there is no optimiser and no
starting guess:

| model | domain | unknowns |
|---|---|---|
| series `R + C` | `Z = R + (1/C)·(1/jω)` | `R`, `1/C` |
| series `R + L` | `Z = R + jωL` | `R`, `L` |
| parallel `R‖C` | `Y = G + jωC` | `G`, `C` |
| parallel `R‖L` | `Y = G + (1/L)·(1/jω)` | `G`, `1/L` |

In each case the real part gives the resistive unknown and the imaginary part the reactive one, so the
"fit" is two independent 1-D least squares over the band. Do them as ordinary normal equations; do not
reach for a solver library.

A fit is **non-physical** if `R ≤ 0` or the reactive value `≤ 0`. Keep it in the ranking (so the user
can see it was considered) but never auto-apply it.

---

## 3. Ranking — one metric, in Γ

Convert each fitted model **back to Γ over the band** and score by

```
  residual = mean over the band of |Γ_model(f) − Γ_measured(f)|
```

That single bounded metric ranks all four on equal terms. **Do not rank in the impedance domain**: an
impedance-domain residual over-weights frequencies where |Z| is large and is not comparable between the
series and parallel forms, so it will confidently pick the wrong topology on exactly the loads this
feature exists for.

- Apply the best-scoring **physical** fit.
- **Show all four with their residuals**, in Γ units. The residual is data the user is entitled to see,
  never a hidden gate.
- If even the best residual exceeds a warning threshold — a setting, **default mean |ΔΓ| > 0.05** — the
  result is still applied but **flagged**: "the external network is not well described by a two-element
  model over this band." That is the honest answer for a network with a resonance in band and it points
  the user at narrowing the band.

**The threshold is a calibration task, not a design constant** (`match.md` §14.5). Make it a setting,
default it to 0.05, and say in your report what residuals you actually measured on the fixtures you
built. For scale: 0.05 in Γ is about the difference between a −20 dB and a −16.5 dB match.

---

## 4. Conjugate

A **Conjugate** toggle per side, off by default. With it on the target is `Z*` — which flips the
reactance sign, so a measured parallel-RC becomes a **parallel-RL** target. MN-1 §1.1 already supports
that; this is the feature `match.md` §5.4 exists for, and the conjugate test in §6 is the one that
proves the two halves meet.

Near the toggle, state once: *a conjugate match is the right target for a small-signal stage and
generally the wrong one for a power amplifier's output, where the load should come from loadpull
(Ropt), not from the device's own output impedance.* Say it in the UI; do not leave the user to
rediscover it.

---

## 5. Button states

Greyed out **with a tooltip saying which**, when:

- the pin is unconnected;
- the pin's net carries no component other than the `Match` itself;
- the schematic has unresolved errors;
- the `Match` is inside a cell rather than in a testbench — there is no external network to look at
  from inside a definition.

The UI side extracts the testbench with `NetExtractor.Extract` (`src/Ui/Schematic/NetExtractor.cs`) and
answers the connectivity questions from it. Run the probe **off the UI thread** with the existing
progress/cancel affordance — it is a real S-parameter sweep and a biased FET network makes it a real
DC solve first.

---

## 6. Provenance

A probed termination records `Probed = true` and `ProbedAtUtc`; the field shows a small badge. Editing
the value by hand clears it to `Manual`. **The user's override always wins and is never silently
re-probed.**

**A probed termination is a snapshot, not a live link.** Changing the surrounding circuit does not
invalidate or update it; re-probing is always an explicit action. A live link would mean the network
silently re-synthesising — and therefore changing topology — because someone edited a bias resistor
three components away.

---

## 7. Tests

| test | project | what it protects |
|---|---|---|
| **Round-trip, parallel RC** — a testbench containing a bare 200 Ω ‖ 0.125 pF probes back to within 0.1 % and ranks *parallel RC* first | Engine.Tests | the core claim |
| **Round-trip, series RC** — 1.25 Ω + 10 pF, likewise | Engine.Tests | both topologies |
| **Round-trip, both inductive** — series R+L and parallel R‖L | Engine.Tests | §2's other half |
| **Topology discrimination** — a load that is genuinely parallel must not be reported as series, and vice versa, across a spread of Q | Engine.Tests | §3's metric choice. **Also assert an impedance-domain metric would have got at least one of these wrong** — that is the evidence for the choice |
| **Conjugate** — the same parallel-RC network with Conjugate on returns a parallel **RL** target | Engine.Tests | §4, and MN-1's inductive path end to end |
| **The `Match` is excluded** — probing with a `Match` present gives the same answer as probing the same network with the `Match` deleted by hand | Engine.Tests | §0 |
| **Biased FET** — a FET with a bias network probes to a sensible small-signal impedance, and the answer *changes* with bias | Engine.Tests | that step 4 actually kept the bias |
| **DC failure refuses** — a bench whose DC solve fails reports the DC failure, not an impedance | Engine.Tests | §1 |
| **Poor fit is flagged** — a network with an in-band resonance is applied but flagged | Engine.Tests | §3 |
| **Button states** — each row of §5 disables with the right tooltip | Ui.Tests | §5 |
| **Provenance** — probe sets the badge; a hand edit clears it | Ui.Tests | §6 |

### 7.1 Cost

The fixtures are small linear networks — milliseconds. The **biased FET** test is the one that could
cross ~5 s; measure it alone, `--no-build`, before deciding whether it needs
`[Trait("Category","Benchmark")]`. A benchmark measured inside a full run reads more than twice slow —
that mistake has been made twice in this repo.

---

## 8. Report

State: the residuals you measured on each fixture; what the impedance-domain metric did on the
discrimination test (the evidence for §3); whether the biased-FET test needed tagging; the threshold
you would now propose as the default. Findings to `src/Engine/RESOLVED.md` and `src/Ui/RESOLVED.md`.
