# Brief EMI-8 — User docs, the example, and the validation set

**Tag:** `R-emi8-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-1…EMI-7
**Area:** `docs/user/src/reference/em-solvers.md`, `em-setup.md`, `em-3d.md`, `settings.md`, `cli.md`, the MCP
reference text, `examples/3D Eigenmode/` (README + `expected-numbers.json`), `docs/design/em-3d.md`. **No DocGen**: the
owner regenerates at the end of the series.

---

## 1. `R-emi8-1` — `em-solvers.md`

- **The programs section** becomes three: Palace, openEMS, EMerge — what each is (method, licence, who maintains it,
  how circuitRF installs it), written factually and respectfully of each project.
- **Side by side** and **Choosing, by problem** gain EMerge's column/rows from what was measured in this series, not
  from upstream claims.
- **Getting the most speed from EMerge** (the owner asked for this explicitly). Per platform, from EMI-3's measured
  table: what the install gives you, what makes it faster, and what to install for that — e.g. on a Mac with Apple
  silicon, Apple's command-line tools (`xcode-select --install`), after which circuitRF adds the faster solver itself
  the next time it runs EMerge. Also: the **Cores** setting, why eigenmode runs use one core, what the adaptive sweep
  does and when to turn it off, memory per unknown, and how Settings ▸ Solvers shows which speed tier you have. No
  vendor names; say what a thing does.
- **Why not just one?** gains the third solver honestly: a third independent answer is what makes Compare useful.

## 2. `R-emi8-2` — the other pages

`settings.md` (the EMerge row, its status line, Install/Uninstall), `em-setup.md` (the EMerge settings block, Compare —
EMI-6's section), `em-3d.md` (where results land, field plots for EMerge), `cli.md` (`--solver` lists, `--reference`,
`solver install emerge`), the MCP `run`/`solver` text. README/docs describe the product as it is, with no change
history.

## 3. `R-emi8-3` — the example

`examples/3D Eigenmode/`: add an **EMerge** variant of each setup (or switch the existing setups to Compare where that
reads better — decide by what keeps the README's walkthrough simplest), run them on the owner's machine, and add the
measured numbers to `expected-numbers.json` beside Palace's. The README's "Eigenmode is Palace only" section becomes
accurate again (openEMS still has no eigensolver; EMerge has one). Record each run's cost line as the README's table
does. Document any setting traded for speed (the standing rule).

## 4. `R-emi8-4` — the validation set

A short, fixed list of shipped `.c3d` setups run on EMerge and compared with Palace (and openEMS where it runs), with
tolerances in `expected-numbers.json` and a `Benchmark`-tier gate that is **Skipped when EMerge is not installed**:

| Case | Checks |
|---|---|
| 3D Eigenmode Cavity, *Modes* | cavity-mode frequency within 0.2 % of Palace's |
| 3D Eigenmode Cavity, *Driven* | notch within one 5 MHz step of the eigenmode; off-notch |S21| within 0.05 dB of Palace's |
| Cavity with post | cavity mode above the band; no in-band notch |
| 3D Connector Launch (lumped) | |ΔS| against Palace below the threshold the owner judges small entries by |

## 5. `R-emi8-5` — the open item: the notch depth

EMerge's Cavity notch is −13.3 dB against Palace −11.69 dB and openEMS −11.54 dB, unmoved by a 4× mesh (EMI-0 §2).
Find out why, on a case with a known answer: a matched 50 Ω thru (S11 floor, S21 phase against the line's closed-form
electrical length), and a lumped-port-loaded resonator whose external Q has a closed form. If the cause is EMerge's
lumped-port model, record it in `src/Design/RESOLVED.md` and in the comparison's EMerge facts (EMI-6), and offer the
upstream project a minimal reproducer (owner posts; do not post). If it is circuitRF's lowering, fix it here.

## 6. `R-emi8-6` — the design doc

`docs/design/em-3d.md`: §2-§3's "two solvers" framing becomes the solver seam (EMI-1) with three descriptors; §4.4
"Running both" becomes Compare; §7.1's distribution rule names EMerge's Python environment as the same arrangement.
