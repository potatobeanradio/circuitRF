# Brief TRN-9 — docs and examples

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn9-<n>` · **Depends on:** all · **Design note:** all
**Area:** `docs/user/src` (new pages and sections), the generated reference pages (analyses, components), `examples/`
(three new workspaces), `docs/design/transient.md` (status line), `README.md` (one feature line).

## The gap

Nothing tells a user that transient exists, when to use it instead of harmonic balance or envelope, what ngspice can
and cannot run, or how to seed harmonic balance from a transient.

## Requirements

**R-trn9-1 — User page: Transient.** What it is for and what it is not for (steady-state RF questions go to HB,
modulated ones to envelope), the directive, sources and the switch, initial conditions, the output grid, measurements,
the Transient preset, and the steering line's meaning. Figures from real runs.

**R-trn9-2 — User page section: ngspice.** Installing from Settings ▸ Solvers or the CLI, choosing it on the card, what
translates exactly, what is refused and why, `SpiceLines=rlgc` and how to read each line's reported |ΔS|, `SpiceFit=yes` and its label, where the deck is and that it runs by hand.
States plainly that the translatable set is smaller than what the native engine runs.

**R-trn9-3 — User page section: Transient-assisted HB.** When to reach for it, `InitialGuess` and its three values,
fast-bias scaling and what it does not change, oscillators without `Fguess`.

**R-trn9-4 — Reference pages.** The generated analyses page gains `tran` and the new `hb`/`osc` keys; the components page
gains the waveform sources, `Idc`, `Switch` and the tone sources' `Delay`/`Ramp`/`Damping`, with symbol figures.

**R-trn9-5 — Examples** (File ▸ New ▸ Examples, each with a README stating what it shows and every setting traded for
run time):
- **Pulse Response** — a lumped low-pass filter and a lossless line driven by a pulse: rise time, overshoot, reflection;
  native and ngspice side by side.
- **Buck Converter** — switch, diode, LC: start-up, ripple, settling, `IC=`.
- **Class-E with a Transient Seed** — the class-E stage of TRN-8 gate 1, an HB bench with `InitialGuess=transient` and a
  transient bench showing the waveforms the seed came from.

**R-trn9-6 — Status.** The design note's status line updated to what was built, and anything not built listed.

## Gates

1. Every example passes `circuitrf check` and runs headlessly with the CLI (ngspice benches skip with a reason when it
   is not discovered).
2. Every figure is regenerated from a run, not drawn.
3. The user pages build with the docs generator and every link resolves.

## Not here

Tutorials beyond the three examples; video.
