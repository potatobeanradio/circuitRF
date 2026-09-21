# Brief 15 — docs, the example, and closing the series

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs15-n`
**Area:** `docs/user/`, `docs/design/lvs.md`, `docs/design/layout-view.md`, `docs/design/cli.md`,
`docs/design/railrf.md`, `examples/examples.json`, `src/Design/Layout/Lvs/RESOLVED.md`,
`src/Design/Layout/Extraction/RESOLVED.md`
**Depends on:** all · **Blocks:** —

---

## 0. What this brief delivers

The feature becomes findable, the design note stops saying "proposal", and everything the series
learned is written where the next person will look.

---

## 1. `R-lvs15-1` — user documentation

**`R-lvs15-1a`** One page: what LVS checks, how to run it from the window and from the command
line, how to read a report, what a waiver is for, and the four things it does **not** check
(performance, manufacturability, parasitics, whether a declared device is really there).

**`R-lvs15-1b` It is written against `examples/LVS/`**, which ships both boards. The reader
follows along in the broken one and sees each of the six findings in turn. A documentation page
whose examples the reader cannot open is a page nobody finishes.

**`R-lvs15-1c`** Brief 5 `R-lvs5-5b` already puts the fault list in the example's own README; this
page **links to it** rather than restating it. Two copies of a fault list drift, and the one in
the workspace is the one a reader has open.

**`R-lvs15-1d` Say what the audience's chosen path buys, never that a cheaper one exists.** A
reader on the LVS page has already decided to check their layout; telling them they could skip it
is noise.

**`R-lvs15-1e`** Generated docs are regenerated wherever this change reaches them and the result
is committed — never reverted. A no-change run is a zero-file diff.

---

## 2. `R-lvs15-2` — the design notes

**`R-lvs15-2a`** `lvs.md`'s status flips from **Proposal — rev 2** to **BUILT**, with the
implementation line naming this series, on `railrf.md`'s own pattern.

**`R-lvs15-2b`** Anything the build decided differently from the note is **amended in the note**,
with the reason, in the section it belongs to. A note that disagrees with the code is worse than
no note, and §12's "Still genuinely open" list is closed out — in particular the property
tolerances, which brief 10 measured.

**`R-lvs15-2c`** `layout-view.md` §3.4 and §9A.3 already point at `lvs.md`; the *"LVS is a
direction, not a phase"* line in its risk table and its non-goals list are updated, because both
are now false.

**`R-lvs15-2d`** `cli.md` gains §19 for the verb (brief 11 `R-lvs11-5c`), on §13-§18's pattern.

**`R-lvs15-2e`** `railrf.md`'s companion line already names the shared extraction; it is updated
to point at the built namespace rather than the proposed one.

---

## 3. `R-lvs15-3` — `RESOLVED.md`, where the findings actually go

**`R-lvs15-3a`** Significant findings from this series go in the **sibling `RESOLVED.md`**, never
in a `CLAUDE.md`. Create one where none exists — `src/Design/Layout/Lvs/` will need its own.

**`R-lvs15-3b`** The ones known in advance to be worth recording, because each is a trap whose
failure mode is silence:

- **The precedence inversion.** railRF's *schematic-then-artwork* rule would make LVS pass every
  design with no symptom. Why `PinNaming` is an enum and not four nullable delegates
  (overview §1a, brief 2).
- **The undrawn ground reference.** The shipped MMIC technology's `Backside Metal` draws no layer;
  what that costs read naively, and why the reminder is unconditional (brief 3).
- **The terminal map's four derivations**, and why partial name matching must not fall through to
  positional (brief 1 `R-lvs1-3e`).
- **The terminal index in the colour hash.** Dropping it matches a drain to a source and passes
  every other test (brief 7 `R-lvs7-3c`).
- **The nm↔DBU coincidence at 1000 DBU/µm**, in its third recorded form (brief 13).
- **Whatever the build found that this list does not predict** — which is the point of the file.

**`R-lvs15-3c`** `src/Design/Layout/Extraction/RESOLVED.md` records what the promotion cost and
what it preserved, so the next person to touch `CopperPieces` knows railRF's answers are a gate.

**`R-lvs15-3d`** No owner or user is quoted anywhere — in a `RESOLVED.md`, in a commit message, or
in the code. Paraphrase.

---

## 4. `R-lvs15-4` — the example ships

**`R-lvs15-4a`** Brief 5's row in `examples/examples.json` is present and Tools ▸ Examples offers
it. The summary says the broken board is **deliberately** broken; an example that looks like a
mistake is one somebody reports as a bug.

**`R-lvs15-4b`** Opening it, running LVS on both boards, and reading the report is a **manual
check** on the release list, because no test asserts that a human can follow it.

---

## 5. `R-lvs15-5` — the series' own closing gates

**`R-lvs15-5a` No vendor or PDK name anywhere.** Grep the whole series' new code, docs, examples
and test fixtures before the work is called done, and report what was removed. `.kicad_pcb` is the
one allowed exception and it is a file extension.

**`R-lvs15-5b` No personal path, workspace name or username** in any fixture or example. Fixtures
carry the *shape* of a path, never a real one.

**`R-lvs15-5c` The firewall is unchanged** — `tests/Firewall.Tests` passing, nothing in
`src/Design/Layout/{Lvs,Extraction}/` referencing a UI framework.

**`R-lvs15-5d` Every shipped example still answers identically**, `examples/Power Rail` most of
all: brief 2 moved the code it runs on. Run them and compare whole `DataSet`s, not summaries.

**`R-lvs15-5e` The version number is still in exactly one place.** This series adds files; it does
not add a version string.

**`R-lvs15-5f` Scoped test runs, read the TRX.** The projects this series can reach are
`Ui.Tests`, `Firewall.Tests` and — through brief 2 only — nothing in `Engine.Tests` or
`Core.Tests`. Run the targeted classes, read `TestResults/last-run.trx` for failures, and never
re-run to find out what broke.

---

## 6. Gate

1. **The user page exists, links to the example, and every command in it runs as written.** A doc
   whose commands are stale is worse than no doc — execute them in a test.
2. **`lvs.md` says BUILT** and its open-questions list is closed (`R-lvs15-2a`, `b`).
3. **`layout-view.md`'s two false statements are gone** (`R-lvs15-2c`).
4. **`cli.md` §19 exists** and matches the verb's actual synopsis, asserted against `--help`
   (`R-lvs15-2d`).
5. **Both `RESOLVED.md` files exist and carry §3's entries** (`R-lvs15-3`).
6. **The vendor-name grep is clean** over everything the series added (`R-lvs15-5a`).
7. **No personal path in any new fixture** (`R-lvs15-5b`).
8. **Every shipped example answers identically** to before the series (`R-lvs15-5d`).
9. **Firewall passing** (`R-lvs15-5c`).
10. **Generated docs regenerated, zero-file diff where nothing changed** (`R-lvs15-1e`).

## 7. Scope

- **No new capability.** Every behaviour shipped in briefs 1-14.
- **No tutorial series, no video, no marketing page.** One reference page and one worked example.
- **No restatement of the design note in user docs.** The note is for the implementer; the page is
  for the designer.
