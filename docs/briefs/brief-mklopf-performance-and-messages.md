# Sonnet Brief — MKlopf: hoist the frequency-independent work, cut N, and route warnings to Messages

Owner report: an S-parameter sweep with MKlopf is very slow (his case resolved **N = 4096** sections), the
model recomputes on every analysis, and its warnings go to the terminal instead of the Messages window.

Both are real. The performance problem is larger than the section count suggests.

Gate command is plain `dotnet test`.

---

## 1. The dominant cost: `Stamp` runs per frequency point, and almost everything in it is frequency-independent

`MicrostripKlopfModel.Stamp(mna, c, omega)` is called **once per frequency point**. Nearly everything it does
depends only on geometry and substrate.

**Frequency-independent work currently repeated on every point:**

| Line | Work |
|---|---|
| ~87–89 | `TotalArcLength`, `MinRadiusOfCurvature` |
| ~95–105 | the **200-sample curvature scan** — see R-mk-2 |
| ~129–133 | `SynthesizeWidth(_z1)`, `SynthesizeWidth(_z2)`, two `HammerstadJensen.Compute` |
| ~137 | `MicrostripCascadeSectioning.Resolve(...)`, which itself samples `WidthAtArcFraction` |
| ~152–155 | **per section: `WidthAtArcFraction(sMid)` and `HammerstadJensen.Compute(wMid, …)`** |

That last row is the one that matters. **`WidthAtArcFraction` calls `HammerstadJensen.SynthesizeWidth` — a
numerical inversion** (R-klp-5's `Z₀ → W`). So the inner loop performs **one root-find per section per
frequency point.**

At N = 4096 over a 201-point sweep that is **~823,000 root-finds**, each itself perhaps 10–30 forward
Hammerstad-Jensen evaluations — order **10–25 million** evaluations, to recompute a width profile that never
changes. And `HammerstadJensen.Compute(wMid, …)` returns the **static** `Z₀` and `εeff`, which are likewise
frequency-independent.

**Only three things in the loop genuinely vary with frequency:** `KirschningJansen.Compute`, the two loss
terms, and the ABCD cascade itself.

**R-mk-1. Build a frequency-independent section table once, then make `Stamp` a pure cascade.** Per section,
precompute and store: arc position, width, static `Z₀`, static `εeff`. Hoist `totalArc`, `minRadius`, `w1`,
`w2`, `eeffMax` and `N` alongside it. `Stamp` then does dispersion + loss + cascade and nothing else.

This alone should be the bulk of the win, and it changes no numbers — the same values, computed once.

**R-mk-2. The curvature-warning guard is wrong and costs a scan per frequency point.** `_warnedCurvature` is
set **only when the warning fires**, so in the healthy case — `rMin ≥ 3·W`, which is the normal case — the
flag is never set and the 200-sample loop plus an `ArcLength` and a `WidthAtArcFraction` root-find run on
**every** point, forever. Use a separate "already evaluated" flag, or fold the check into R-mk-1's one-time
setup where it belongs.

**R-mk-3. Cache the section table across analyses**, keyed on `(Z1, Z2, Γmax, L, Offset, substrate, N)`. The
owner reports recomputation on each new analysis. This is the same principle as the PCell contract's
"evaluate once per unique parameter set" and the layout cell cache — a purely geometric result should be
computed once per distinct input.

## 2. N = 4096 is ~150× more than the electrical criterion needs

R-tap-1 set two criteria and takes the larger. Check what each actually asks for in the owner's case
(L = 20 mm, Offset = 5 mm):

- **Electrical** (`section ≤ λ_min/20`): at 10 GHz on a typical substrate, λ ≈ 15 mm, so λ/20 ≈ 750 µm →
  **N ≈ 30**.
- **Profile resolution** (`ΔW ≤ 2%` of range): **N = 4096** → 4.9 µm per section.

So **N is entirely driven by the geometric criterion**, and 4.9 µm sections on a 20 mm taper are far finer
than the physics requires. The cause is structural:

**R-mk-4. Uniform spacing forces the whole taper to the density needed only at its steepest point.** A
Klopfenstein profile is flat over much of its length and steep near the middle; sampling it uniformly in arc
length means most sections are wasted resolving a region that barely changes. **Space sections
non-uniformly — equal `Δ(ln Z)` per section** — so density follows the profile. Expect a large reduction;
the ratio of peak to mean slope is what N is currently paying for.

**R-mk-5. `Δ(ln Z)` is the physically meaningful criterion, not `ΔW`.** The reflection from a small step is
`Γ ≈ Δ(ln Z)/2`, so bounding `Δ(ln Z)` bounds each section's contribution to the error directly. `ΔW` is a
proxy for it, and a poor one where `dZ/dW` is small.

**R-mk-6. Better still, converge on the answer rather than on a geometric proxy.** Compute at `N`, then at
`2N`, at the **top** sweep frequency (the worst case), and accept `N` when the S-parameters agree within a
stated tolerance. Do this **once per parameter set** as part of R-mk-3's cached setup — it costs one extra
evaluation and it makes the section count self-validating rather than rule-of-thumb.

Keep `_sectionCountOverride` as the manual escape hatch, and **report the resolved N** (see §3).

## 3. Warnings must reach Messages, not the terminal

Two sites write to the console: the curvature warning (~113) and the section-count report (~142).

**R-mk-7. The model already holds a reporting channel — use it.** `_reporter = new MicrostripValidityReporter(instancePath)`
is constructed in the ctor and passed to `HammerstadJensen`, `KirschningJansen` and the loss functions. These
two messages simply bypass it.

**R-mk-8. Verify the reporter actually reaches the Messages UI before assuming R-mk-7 is sufficient.**
`src/Core` cannot reference the UI, so there must be an abstraction between them — find how the engine
surfaces warnings today (convergence failures must arrive somewhere) and confirm `MicrostripValidityReporter`
uses it. **If the reporter itself writes to the console, that is the real bug**, and fixing it repairs every
microstrip validity warning rather than these two. Report which case it was.

**R-mk-9. Fix the doubled prefix.** The observed line reads `MKLOPF:MKLOPF:` because the format string
hardcodes `"MKLOPF:"` and then appends `_instancePath`, which is already `MKLOPF`. Drop the literal; let the
instance path identify the component, as the reporter presumably already does.

**R-mk-10. The section-count line is informational, not a warning — do not emit it on every run.** `N = 30`
is noise; `N = 4096` is worth knowing. Emit it only when N exceeds a threshold, or classify it as
informational so it does not sit in Messages alongside genuine problems. The curvature warning **is** a
warning and should stay one.

## 4. Guardrails

- **This must not change any computed value.** §1 is pure hoisting and caching; §2 changes the section count
  and therefore the numbers, so it is gated on convergence (R-mk-6) rather than on assertion.
- Do not change the Klopfenstein profile, the synthesis model family, the offset centreline, or `SmoothSteps`
  artwork behaviour.
- Do not remove `_sectionCountOverride`.
- Do not touch MLIN, MTaper, MBend, MTee or MCross beyond any shared sectioning helper — and if
  `MicrostripCascadeSectioning` is shared, **MTaper benefits from §2 as well**; say so rather than
  special-casing MKlopf.

## 5. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Hoisting changes nothing (R-mk-1)** — S-parameters for the owner's case are **bit-identical** before and
   after, at fixed N. This is the assertion that makes the optimisation safe.
3. **Root-finds collapse** — instrument `SynthesizeWidth` call count across a 201-point sweep: it must be
   O(N) for the whole sweep, not O(N × points). Assert the count, not the time.
4. **Curvature scan runs once (R-mk-2)** — the 200-sample loop executes once per analysis regardless of
   whether the warning fires. Assert with a geometry that does **not** warn, which is where the bug lives.
5. **Cache hit across analyses (R-mk-3)** — running two S-parameter analyses on an unchanged design builds
   the section table once; changing any keyed parameter rebuilds it.
6. **N falls substantially (R-mk-4/5)** — for the owner's case (Z1 = 50, Z2 = 7, Γmax = 0.05, L = 20 mm,
   Offset = 5 mm) report the new N beside the old 4096, and assert the resulting S-parameters match the
   N = 4096 result within the convergence tolerance. **Both numbers go in the completion note.**
7. **Convergence is real (R-mk-6)** — doubling N beyond the resolved value changes S-parameters by less than
   the tolerance; the check runs once per parameter set, not per frequency.
8. **Timing, reported not asserted** — wall-clock for the owner's case before and after. It is the reason for
   the work, but per the standing rule it is the diagnostic, not the gate.
9. **Messages (R-mk-7/8)** — the curvature warning and the section-count line appear in the **Messages
   window**; nothing from this model reaches stdout or stderr. Assert no `Console.` call remains in the file.
10. **Prefix (R-mk-9)** — the component is named once.
11. **Noise (R-mk-10)** — a small-N run produces no section-count entry; a large-N run does.

## 6. On completion

Record in `src/Ui/CLAUDE.md`: that **`Stamp` was recomputing the entire frequency-independent width profile
per frequency point**, with the root-find count before and after; **R-mk-2's guard bug** and that it only
manifested in the healthy case; the sectioning change from uniform-`ΔW` to non-uniform `Δ(ln Z)` with the old
and new N for the owner's case and the measured speed-up; whether `MicrostripCascadeSectioning` is shared
with MTaper; and **the answer to R-mk-8** — whether the reporter already reached Messages or was itself
writing to the console.
