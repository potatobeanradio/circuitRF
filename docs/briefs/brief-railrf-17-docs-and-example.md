# Brief 17 — docs, an example workspace, and the closeout

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail17-n`
**Area:** `docs/user/`, `examples/`, `docs/design/railrf.md` · **Depends on:** all · **Blocks:** nothing

---

## 0. What this brief delivers

The parts of shipping a feature that are invisible until they are missing.

1. A **user chapter**, `docs/user/railrf.*`, with its figures.
2. An **example workspace** under `examples/`, offered by Tools ▸ Examples.
3. The **design note's own status** brought up to date — it currently reads *"Proposal — rev 4, for external
   review · Phase: unstarted."*
4. `docs/design/cli.md`'s section for the `rail` verb (brief 10 adds it; this brief checks it is there).

---

## 1. `R-rail17-1` — the chapter's shape

Follow the user docs' existing chapters. The order that matters is **the order §2.1 puts the four questions
in**, because that is the order a board designer asks them:

1. Q0 — is this rail connected, and what does it cost to get there.
2. Q1 — does it meet its target.
3. Q2 — which capacitors are doing anything.
4. Q3 — did my form factor break it.

§2.6's worked example is the chapter's spine — it is already written as a seven-step narrative with real
numbers, and it is the best single artefact this design note produces. Use it.

### `R-rail17-2` — the boundary is part of the documentation, not a footnote

§2.7 exists *"because a tool that is vague about its boundary gets trusted past it."* The chapter states all
eight limits plainly, in the chapter body rather than in an appendix: not full-wave, not thermal, stops at the
package, no regulator forward transfer, no transient, no split-plane bridging, no derating without a curve, no
routing or optimisation.

### `R-rail17-3` — the two-speed model needs one paragraph and one figure

A user who does not understand that Fast is a different reading of the geometry will read a fast pass as a
pass. The status strip says which model produced every number (brief 4 `R-rail4-2`), and the chapter explains
what the two are, **once**, with the classification picture beside it.

**Do not tell users what they already know.** A note's audience is self-selected: say what *Accuracy* buys,
not that Fast exists and is faster.

---

## 2. `R-rail17-4` — the example workspace

Six shipped examples already exist under `examples/`, offered by Tools ▸ Examples; adding one is **a folder
plus a row in `examples.json`**. railRF's is the seventh.

It needs to be **editable files**, small, and it must run in seconds. A synthetic board is the right choice —
not an anonymised real one:

- A four-layer board, reference on L2, a handful of decaps, a bulk, a series FET and a ferrite.
- One rail, one source, two loads, one of them an observation port, so the example demonstrates
  `R-rail1-7`'s distinction rather than merely describing it.
- A drop budget and a flat Z target, both **just met**, so a user's first edit shows them a change.

### `R-rail17-5` — document the setting you traded away

If the example's analysis settings are reduced for speed — a coarser mesh, fewer frequency points, Fast rather
than Accurate — **the README must give the alternative and its expected numbers.** An example that runs fast
and quietly answers a different question teaches the wrong thing.

---

## 3. `R-rail17-6` — the design note's own status

`docs/design/railrf.md` opens with **Status: Proposal — rev 4, for external review · Phase: unstarted.**
Update it to record what shipped, per phase, and leave the closed questions' *reasoning* in place — §8 says
why: *"the reasoning behind a closed question is what stops it reopening by accident."*

Record the answers to the four open ones as they landed:

- **Q-18** — what of the reference package arrived at manual testing, which gates it closed, and
  **which of `R-rail2-14`'s four shape allowances turned out to be needed.** That last one is the
  finding: an allowance that was never exercised was cheap insurance, and one that was is evidence the
  deferral was survivable only because it was planned for.
- **Q-20** — whether typing the regulator's input current and minimum input voltage was accepted.
- **Q-21** — which netlist flavour the example turned out to be, and whether a fourth reader was needed.
- **Q-22** — whether the plating thickness was stated anywhere, or stayed a typed setting.

---

## 4. `R-rail17-7` — figures, and the DocGen churn rule

Figures are generated. Before reporting, **classify the churn**: a DocGen run reporting hundreds of changed
`.svg`s is a red flag, and three families of that churn are nondeterministic while one is real.

- The id counter is **hexadecimal** — a `\d+` pattern mis-classifies it.
- ~67 figures drift by one pixel in a way **HEAD itself reproduces**, so isolate your own by regenerating in a
  worktree at HEAD and diffing against that, not against the working tree.

Report the classified counts, not the raw one.

---

## 5. `R-rail17-8` — the vendor-name sweep, before anything is committed

The repo's standing rule, and this series touches more external-file territory than most: board netlists, BOM
descriptions, part libraries, placement files, and a synthetic example whose parts have to be called
something.

**Before any commit in this series, grep for commercial vendor names, product names and PDK names** — in the
example workspace, in `testdata/`, in the docs, in the figures' own text, and in the briefs. Remove them, and
say in chat what was removed. The only permitted exception in the whole repo is `.kicad_pcb`, which is a file
extension for a data format.

The same sweep covers the owner's own paths and workspace names: a fixture is anonymised to the **shape** of a
path, never to a real one.

---

## 6. Scope

- **No new features.** If a gap turns up while writing the chapter, it is a finding for a RESOLVED.md and a
  possible brief 18 — not a change made under cover of documentation.
- **No real board committed**, anonymised or otherwise, unless Q-18's package explicitly permits it.

**On completion:** record findings in the RESOLVED.md beside whatever was touched. Never in a CLAUDE.md.
