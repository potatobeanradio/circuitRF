# Brief YA-12 — The Centering mode, and the quadratic surrogate

**Series:** `brief-yield-0-overview.md` · **Tag:** `R-ya12-<m>` · **Depends on:** YA-10, YA-11
**Area:** `src/Ui/Yield/`, `src/Ui/Optimization/` (the progress chart, reused), `src/Design/Statistics/`
(`QuadraticSurrogate`), `src/Engine/Statistics/` (least-squares fit), `src/Cli/Yield.cs`, `docs/design/yield.md`

---

## 1. Goal

The Yield panel gains its fourth mode, and centering gets the one acceleration the owner put in scope: a quadratic
model of each spec margin that lets thousands of virtual trials stand in for simulations — with every answer still
verified by real simulation.

## 2. Requirements

**R-ya12-1 — Centering mode.** Mode **Centering** in the panel: the variable list shows Opt and Stat together
(designable nominals with their ranges, tolerances beside them); the `center` settings (algorithm from the registry's
`SuitsNoisyObjective` entries, M, verify trials, limits) collapsed to a summary line as in YA-10 R-ya10-5; Run / Pause
/ Stop; the **yield-vs-iteration** chart — the Optimizer panel's cost chart control, reused with yield on its axis;
the per-variable start → best with railed marks; the verification result as **start yield [interval] → centred
yield [interval]**. **Lock in** (a preset, TO-5) and **Push** (one undo step per document, `TuningPush`) act on the
centred nominals exactly as in the Optimizer. **Send to Tuning** loads them into the sliders.

**R-ya12-2 — The quadratic surrogate.** `center surrogate=none|quadratic` (default `none`). With `quadratic`, for each
candidate nominal: evaluate a design of **2k + 1** points in z-space (centre plus ±δ on each statistical variable,
δ = 1) plus k(k−1)/2 cross points when k ≤ 12 (full quadratic), otherwise diagonal-only (stated in the report); fit
each yield goal's margin as a quadratic in z by least squares; estimate the smooth objective and the yield on
**10,000 virtual trials** of the fitted models, drawn from the same common-random-number set. Kit mismatch draws, which
can number in the hundreds, enter as one aggregated contributor per instance (YA-4 R-ya4-9's grouping) or the
surrogate is refused for that design with the count in the sentence — never silently truncated.

**R-ya12-3 — Honesty.** The surrogate is a search accelerator, never the answer: the reported yield of the result is
always YA-11's **simulated** verification. Each iteration also reports the surrogate's fit R² per goal; a fit below
0.9 on any goal is a warning on that iteration and, three iterations running, an automatic switch back to simulated
trials with a note.

**R-ya12-4 — Headless.** `yield center --surrogate quadratic`; MCP `surrogate`; `reference statistics` documents
when it helps (few statistical variables, smooth margins, expensive simulations) and when it does not.

## 3. Gates (minimal; run only these classes)
- `QuadraticSurrogateTests` — on a margin that is exactly quadratic in z, the fit is exact (R² = 1 to 1e-12) and the
  virtual yield equals the simulated yield on the same trials; a cubic margin gets R² < 1 and the warning.
- `SurrogateCenteringTests` — the divider centred with `surrogate=quadratic` reaches the same verified yield as without
  (within the intervals) using fewer simulations (a counter).
- `CenteringPanelTests` — the headless panel's centred nominals equal `yield center`'s for the same seed; Push writes
  them as one undo step.
