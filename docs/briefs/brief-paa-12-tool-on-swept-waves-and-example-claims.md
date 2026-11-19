# Brief PAA-12 — the tool on a swept harmonic-balance bench, and a gate on what example READMEs promise

**Series:** PAA-0 … PAA-12 · **Tag:** `R-paa12-<n>` · **Depends on:** PAA-6 (`waves=`), PAA-9 (the tool), PAA-10 (the example) ·
**Area:** `src/Ui/PhasedArray` (`PhasedArraySession`), `src/Design/Em3d/FieldCosimulation.Hb.cs`, `examples/Phased Array`,
`tests/Ui.Tests/Examples`, `src/Ui/RESOLVED.md`.

## The gap

**The *Phased Array* example's *5 Brute Force* does not open in the tool.** Its README says: "In the Phased Array tool, colour the
aperture by PA PAE: the tool reads the measures named `PAE1` … `PAE4`." The tool opens on "Fields 'F1' gave no pattern." — at HEAD
and at the commit that wrote the README (`11cc68dac`), so it never worked.

Why: `F1` is `waves=HB1`, and `HB1` is wrapped by two sweeps (`SW_theta` over `SW_Pin`). A `waves=` fields analysis whose source a
sweep wraps runs "a result at each point of sweep 'SW_theta'" (`FieldCosimulation.Hb`, the stand-in that runs the sweep with the
fields at each point): 12 points, no single array state, so `PhasedArraySession.Evaluate` finds none. Running `F1` alone also leaves
the PAE measures unevaluated ("No analysis named 'HB1' in measurement context"); naming `HB1` as well (which `SettledAnalyses`
already does) evaluates them, over the same 12 points. The tool shows one steering at a time, and the drag writes `theta0` — so its
frame wants the bench at its current values, not the sweep.

**And this is the second README promise about the tool to reach a release broken** — the *mmWave Array Link*'s was the first
(PCH-13). Nothing runs an example the way its README tells a user to use it.

An audit of the array, channel, system and antenna examples (2026-11-19) found the rest of what can be checked mechanically in
order: every display a README names exists or is written by the run it describes, every menu command a README quotes exists, and the
tool opens with a pattern on *1 Transmit* (a drag to 30° settles at 29.9°, the Scan maps tab draws), *2 Receive* (29.3 dBi, G/T
−2.70 dB/K, as tabulated) and both *mmWave Array Link* arrays. Not checked: README number tables beyond the two links, and live UI
behaviour (a Tuning slider redrawing a pattern in *Antenna Co-Simulation* and *SAR MRI Loop Coil*; the RF Chain Budget window's tabs).

## Requirements

**R-paa12-1 — The tool evaluates a swept `waves=` bench at its current values.** When the fields analysis's waves come from an
analysis a sweep wraps, the tool's frames (settled and interactive) run that analysis at the bench's own values: the wrapping sweeps
collapsed for the tool's evaluation only (a disabled sweep's inner runs in its place, `AnalysisChain`), on a copy of the bench — the
schematic, the bench's own Run and every other caller untouched. One array state; the PAE and Pout measures read that one point. A
drag that writes `theta0` then shows the beam at the new steering, with each amplifier's compression in it.

**R-paa12-2 — The frame says when it is a point of a sweep.** The status line names the sweep(s) collapsed and the values used
(`theta0 = 0°, Pin = 22 dBm`), so nobody reads the tool's frame as the sweep's.

**R-paa12-3 — *5 Brute Force* in the tool, as its README says.** Colour by PA PAE offered and drawing the four amplifiers' PAE at the
current `theta0`/`Pin`; the README's numbers (68 % / 63 % at boresight at 22 dBm; 37 % / 44 % / 51 % at 45°) visible by dragging to
those steerings with `Pin` at 22 dBm. UiSnapshot PNGs, looked at.

**R-paa12-4 — A gate on what example READMEs promise.** A test (routine tier) that, for every example whose README tells the user to
open the Phased Array tool on a bench, opens the tool headlessly on that bench as the application does (`--phased-array` takes a
`.csch` since PCH-13) and asserts a frame, not a refusal; and for each "colour the aperture by X" the quantity is offered. The list
comes from the READMEs themselves (a phrase the test finds), so a new example that promises the tool is covered without editing the
test — and an example whose README promise cannot be found by that phrase fails, rather than being skipped in silence. Where a bench
needs an EM result that does not ship, the test runs it or names it as the reason.

## Gates

1. *5 Brute Force*: the tool opens with a pattern; PA PAE offered; at `theta0 = 0°`, `Pin = 22 dBm` the inner two read 68 % and the
   outer two 63 % (the README's, to its precision).
2. The bench's own Run unchanged: `F1` still a result at each of the 12 sweep points, the README's table reproduced.
3. R-paa12-4's test passes on today's examples and fails with *5 Brute Force*'s fix reverted (and with PCH-13's `FT`/`FR` removed
   from the *mmWave Array Link*).

## Out of scope

Running every example's README number tables (a larger, separate gate), and live Tuning-panel behaviour in the 3D view.
