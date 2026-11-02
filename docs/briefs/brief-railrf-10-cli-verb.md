# Brief 10 — `circuitrf rail`: the whole window, with no display

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail10-n`
**Area:** `src/Cli/` · **Depends on:** 5 (6 and 8 enrich it) · **Blocks:** nothing
**Design note:** [`railrf.md`](../design/railrf.md) §11.5, §5 · **Read first:** [`docs/design/cli.md`](../design/cli.md)

---

## 0. What this brief delivers

One verb, `src/Cli/Rail.cs`, on `Authoring.cs`' terms.

> Per `docs/design/cli.md`, **the verb holds no analysis logic of its own** — a rule the automation verbs
> already follow, and the reason a board can be gated in CI.

`src/Cli/Rail.cs` is **argument parsing, refusals and reporting.** Every number it prints comes from the
`src/Design/RailRf/` functions brief 5 built and the GUI's own window calls. The gate is a source scan plus
byte identity against the in-process run — the same two things `AuthoringCliVerbTests`, `EmCliVerbTests` and
`RenderCliVerbTests` already assert.

**Read `docs/design/cli.md` before adding the verb** — it covers the five-step anatomy of a run verb and the
stdout/stderr split.

---

## 1. `R-rail10-1` — the surface

```
circuitrf rail <path.crail> [options]
```

| Option | Meaning |
|---|---|
| `--rail <name>` | Which rail of the set. **Omitting it runs them all in dependency order** — the shape `hb`/`lp` already have for a wrapped sweep. |
| `--fast` (default) / `--accurate` | §2.9's two speeds. The default is Fast, as in the window. |
| `--source <REFDES.PIN>=<model>` | **Repeatable.** `--source BT1.1=batt`. |
| `--load <REFDES.PIN>=<current>` | **Repeatable.** `--load U1.VDD=120mA`. **A load with no current is ACCEPTED as an observation port**, not refused — because that is what it means in the window (`R-rail10-3`). |
| `--target-drop`, `--target-z`, `--mask <file>` | §2.2's target forms, spelled per the CLI's own value+unit conventions. |
| `--aggressor <name>=<freq>[×n]` | Repeatable. |
| `--reference <layer>`, `--extent as-imported\|filled\|infinite` | Brief 1 `R-rail1-6`. |
| `--set var=expr` | Overrides a global before elaboration, exactly as every run verb's does. |
| `-o out.{s2p,npy,csv,svg,pdf}` | The result document. |
| `--json` | The machine-readable form, per `cli.md`. |

### `R-rail10-2` — a `.csch`, a `.clay` and a cell folder are all first-class inputs

`netlist`/`render`/`check` already classify through `DocumentKinds.Classify`, and brief 1 `R-rail1-11` added
`.crail` to it. Follow the house rule: **any other document kind is a refusal BY KIND**, naming what was
handed over. A `.cnl` handed to `rail` is refused as a netlist, not as an unreadable file.

A `.clay` **with no `.crail` beside it** is the interesting case: there is a board but no rail declaration, so
`rail` refuses and names the flags that would make one (`--rail`, `--reference`, `--source`, `--load`). It
does **not** author a `.crail` — brief 1's document is written with a file tool, on the same rule `new` states:
*once a document exists, the way to change it is to WRITE it, because the format is the contract.*

### `R-rail10-3` — an unstated value is a refusal, and an unstated CURRENT is not

The distinction is the whole of Q-16 and it is easy to implement backwards:

- **No reference layer** → refusal naming `--reference`. railRF never infers it (Q-8).
- **No placement origin** → refusal naming the flag (brief 2 `R-rail2-2`).
- **No Excellon format** → refusal, `convert`'s existing sentence.
- **No plating thickness** → **not** a refusal. Brief 6 takes it as a setting and prints which value each
  flag used.
- **No current on a load** → **not** a refusal. It is an observation port and the report lists it as
  observed.

---

## 2. `R-rail10-4` — what it writes

§11.5: *"`-o` for Touchstone, `.npy`, CSV or an SVG/PDF report drawn by the same renderers."*

- **`.s2p`/`.sNp`** — Z(f) at the observation ports (brief 12; at P0 this is a refusal naming `--accurate`
  and the phase).
- **`.npy` / `.mat`** — the `DataSet`, through the existing exporters.
- **`.csv`** — the tables: the ranked breakdown, the via flags, the parts, the anti-resonances.
- **`.svg` / `.pdf`** — the report, **drawn by brief 9's one function**, which is also what `Report ▸` calls.
  §11.7: *"there is one route from an overlay to a page and not two."*

**There is no picture on stdout** — stdout is the result document and `--json` has to co-exist with the write,
which is `render`'s own rule.

### `R-rail10-5` — every export carries the provenance

Overview §4 rule 1, and it is the single most important thing this verb does that a window does not have to
think about: a file read six months later has no status strip. So every export carries **which model produced
it** (Fast or Accurate), **which reference extent** was used, the **temperature** (20 °C), the count of parts
with no bias curve, the count modelled from a file, and whether any peak height is **indicative** (Q-15).

`em`'s `.npy` diagnostics group is the precedent for where this goes in a binary export; the CSV carries it as
a comment header; the SVG/PDF carries it on the page.

---

## 3. `R-rail10-6` — progress, cancellation and exit codes

Ride `RunHost`'s `RunControl`, exactly as `em` and `render` already do.

| Outcome | Exit |
|---|---|
| Success | 0 |
| A refusal — no reference, no origin, a rail-order cycle, Fast above its threshold | **1**, with the run service's own sentence |
| Cancelled | **130**, and **nothing is written** |

A cancelled render writes nothing (`render`'s own rule) and a cancelled `rail` does the same — a partial
`.csv` of a half-solved board is worse than no file.

---

## 4. Tests — `tests/Ui.Tests/RailRf/RailCliVerbTests.cs`

The house gate for a CLI verb is two things and this brief needs both:

### `R-rail10-7` — byte identity against the in-process call

Run the verb **as a process** and compare its output **byte for byte** against the in-process
`RailDcRun` + export the GUI itself calls. `EmCliVerbTests` compares `.sNp` byte for byte excluding only the
provenance **write-timestamp**, which the file carries by design; `RenderCliVerbTests` compares SVG and PDF
with **no exclusion at all**. Follow whichever applies per format, and state the exclusion explicitly where
there is one.

### `R-rail10-8` — the source scan

A comment-stripped scan over `src/Cli/Rail.cs` finds **no** extraction, no mesh, no solve, no breakdown
arithmetic and no second export path. The rule `Authoring.cs` already states: *an operation that lives only in
a view model is not a capability, and a verb that re-implements one diverges from it silently.*

### The rest

- **`R-rail10-1`**: omitting `--rail` on a two-rail document runs **both**, in dependency order, and the
  output names each.
- **`R-rail10-2`**: a `.cnl` is refused **by kind**; a `.clay` with no `.crail` is refused naming the four
  flags.
- **`R-rail10-3`**: all five rows of that table, as five cases. The two non-refusals are the ones that matter.
- **`R-rail10-5`**: every export format carries the provenance, asserted by reading it back.
- **`R-rail10-6`**: a refusal exits 1 with the sentence; a cancellation exits 130 and **no file exists**.
- **End to end, with no display at any step**: author a board (`convert` a Gerber set into a cell), write a
  `.crail`, `check` it, `rail` it, and read the drop back — the same shape `AuthoringCliVerbTests`' own
  end-to-end takes. **That test is what proves a board can be gated in CI**, which is §5's claim for this
  whole architecture.

### `R-rail10-9` — this verb is how the deferred reference package gets tested

Q-18's package arrives at manual testing (overview §1h). **`rail` run as a process against those real files
is the cheapest way to exercise every reader, both extractors and the whole solve in one command** — no
window, no clicking, and a refusal that names its own flag is readable in a terminal in a way a red control
in a dialog is not.

So when the package lands, the first thing to point at it is this verb, not the window. Whatever it refuses
or mis-reads becomes a `FixtureFact` case in brief 2's guarded gate, and whichever of `R-rail2-14`'s four
shape allowances fired gets recorded in brief 17 `R-rail17-6`.

---

## 5. Scope

- **No analysis logic.** `cli.md`'s rule, and `R-rail10-8` enforces it.
- **No per-primitive edit verbs.** No `add-load`, no `set-target`. A `.crail` is written, not edited by verb
  — the rule `new` already states.
- **No `rail` noun-verb split.** It is one verb with options, like `sparam`/`hb`/`lp`, not one with nouns like
  `new` and `history`. The difference is that this one *runs an analysis*; those *author and manage*.
- **No new export writer.** Touchstone, `.npy`, `.mat`, CSV and the renderers all exist.

**On completion:** record findings in `src/Cli/RESOLVED.md`, and update `docs/design/cli.md` with the verb's
own section. Never write findings into a CLAUDE.md.
