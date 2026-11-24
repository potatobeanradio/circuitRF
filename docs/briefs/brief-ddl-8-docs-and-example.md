# Brief DDL-8 — the user page, the design note, an example

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl8-<n>` · **Depends on:** DDL-1 … DDL-6
**Area:** `docs/user/src/reference/` (a new page, and `data-display.md`'s user page), `docs/design/data-display.md`
§2.10, `examples/` (one example workspace), `tools/DocGen` (figures), `docs/design/cli.md` (`render --marker`,
`--control`).

## The gap

The series adds a way of working the Data Display never had. Without a page and an example that show it, the feature is
there but nobody finds it.

## Requirements

**R-ddl8-1 — The user page.** A new reference page, *Linked plots and controls*:
- An overview and a detail: link a pin to a marker, from the pin row and from the marker's menu.
- What a link takes from its source (index, value, position) and the matching rules, said as behaviour ("the same
  sweep is followed exactly; another file's frequency takes its nearest sample"), not as an algorithm.
- Off-grid loadpull: the dashed synthesized curve, Interp and Nearest, the extrapolation note, and the table at the
  marker.
- Families, versus traces, and the I-V to S-parameters case.
- The selected trial, the contour's frequency, HB drive.
- Controls: adding them, S/M/L and the gripper, operate versus arrange, Lock Position, the slider's two domains, the knob, the button,
  the readout, `@name` in expressions, and showing a trace by a control.
- When a source is gone: the frozen plot, the chip, relinking.
- Headless: `render --marker`, `render --control`.

The page states behaviour, with no change history (no "used to", no "now"). It is regenerated straight after it is edited
(`dotnet run -c Release --project tools/DocGen -- --page …`).

**R-ddl8-2 — The design note.** `data-display.md` §2.10 is completed from the briefs' decisions: the model, the matching
rules, identity and frozen links, cycles, live-follow costs, the controls and their rendering rule, and the plugin
seam (DDL-7).

**R-ddl8-3 — An example.** The shipped `examples/Loadpull` workspace gains a `.cdd` with three tabs (its run must be
multi-frequency for tab 2; if it is not, the tab uses a second, multi-frequency bench added beside it):
1. **Load.** A Pout contour Smith plot with a free marker m1, a detail plot of Gt and PAE against Pout at m1, and a
   summary table "At marker".
2. **Frequency.** Gain against frequency with marker m2, driving the contours' frequency.
3. **Panel.** Its controls saved locked. A slider bound to the drive axis, a knob bound to frequency, a button that shows or hides the measured
   curves beside the interpolated ones, and readouts of each.

Its README says what to try. The example's expected numbers, if it has an `expected-numbers.json`, are unchanged: the
display adds no simulation.

**R-ddl8-4 — Figures.** DocGen figures for the new page: the overview and detail pair, a dashed off-grid curve beside a
measured one, and the panel tab. These are rendered by `circuitrf render` from the example's `.cdd`, so they cannot
drift from what the window draws.

## Gates

1. The page builds and its figures render from the example's `.cdd`. The page's HTML is regenerated in the same change.
2. Opening the example and moving m1, m2 and each control does what its README says (UiSnapshot PNGs, looked at).
3. `check` on the example reports no errors, and the vendor-name grep finds nothing in any file the series added.

## Not here

A video or animation; translated pages.
