# `src/Ui/Diagnostics` — the user-docs factory

**A DocGen run that changes nothing must produce a ZERO-file diff, and since 2026-09-21 it does.**
Keep it that way. `tools/DocGen/check-docs-current.sh` (run by `.github/workflows/docs-current.yml`)
is what holds it, and the five rules below are what it cost to get there.

- **Never revert a generated file because its diff looks unfamiliar.** A figure put back is a figure
  that is now genuinely stale, so it comes back on every later run — that is how one small change
  came to print a hundred-file diff. Measured before the fix: 54 files changed on a no-change run and
  **41 were simply what the code draws**, reverted by earlier commits. One page had been committed
  inlining a NEWER copy of a figure than the standalone `.svg` beside it; both were live on the site.
- **Regenerate everything your change reaches, and commit all of it.** A window figure draws the whole
  window, so one new toolbar button lands in every window figure showing that toolbar — not just in
  `toolbar-*.svg`.
- **Two consecutive runs disagreeing is a bug in the FIXTURE, never something to classify away.**
  Three causes were found and fixed; a fourth will look exactly like ordinary churn.
- **A figure must never draw a number that measures the machine** — a solve count, a frame rate, an
  elapsed time. `SvgLint.Measurements` fails the run on one. Suppress it behind
  `UiArtworkGenerator.HeadlessCapture`, the way the railRF status strip and the harmonicaRF message
  line do; the application keeps showing it.
- **Settling an animation needs real time to pass AND a render-timer tick.** Either half alone is
  inert — the headless timer reports `Stopwatch.Elapsed`, so N forced ticks in a loop advance the
  animation clock by nothing at all. See `UiArtworkGenerator.SettleAnimations`.

Detail, the measurements behind each of these, and the two traps in adding a page to a NEW docs
section: the sibling `RESOLVED.md`.
