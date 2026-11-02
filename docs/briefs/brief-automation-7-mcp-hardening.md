# Sonnet Brief — AUT-7: hardening the automation surface — overview and findings

**Read `brief-automation-0-overview.md` first.** This brief follows AUT-5 (the protocol adapter,
`src/Cli/Serve/`) and AUT-6 (`reference` and the generated component catalogue). It is an **overview**
in the same sense as `brief-automation-0-overview.md`: it carries the evidence and the reasoning, and
the work lives in five numbered siblings.

| Brief | Scope | Reqs |
|---|---|---|
| **AUT-8** `brief-automation-8-netlist-contract.md` | Refuse what the reader does not understand — `CnlReader`, parameter parsing, `check`/`explain` honesty | 8 |
| **AUT-9** `brief-automation-9-result-documents.md` | What a result document says, and what it costs to receive | 12 |
| **AUT-10** `brief-automation-10-generated-reference.md` | The generated reference — extends AUT-6's machinery | 5 |
| **AUT-11** `brief-automation-11-missing-verbs.md` | Extraction, and the tools a client asked for and did not find | 4 |
| **AUT-12** `brief-automation-12-render-ergonomics.md` | Layout and render ergonomics | 4 |

AUT-8 and AUT-9 change existing behaviour and need care. AUT-10, AUT-11 and AUT-12 are additive and
can be taken in parallel by anyone.

---

## 1. Where these findings come from

The surface was driven end to end by an out-of-process AI client that had the MCP server and nothing
else — no repository, no source, no prior knowledge of circuitRF. Three tasks were attempted: design a
Klopfenstein microstrip transformer and plot its input match; convert a Gerber file set and render two
layer views of it; run a loadpull on an existing workspace's HEMT at a stated bias.

The first two succeeded. **The third was abandoned as impossible, wrongly** — it was blocked by a
one-net `Tuner` instance line the client had written from the catalogue's own description of the part,
which nothing in the surface refused, warned about, or failed on. With the second net supplied the
same run completes in 38 seconds.

Every requirement in the five sibling briefs is a defect or a gap that exercise produced, recorded
with the observation that produced it.

## 2. What the exercise established, stated so it can be tested

**The artwork half of the surface is good and needs only polish.** `import`, `explain
--layers/--extents/--cells` and `render` composed cleanly; the Gerber import's diagnostics named every
inference *as* an inference, which is exactly what lets a non-human caller decide whether to trust a
result. The unknown-technology refusal that lists the five real ids is the pattern the rest of the
surface should copy. AUT-12 is all that half needs.

**The netlist half is not.** The `reference` pages document roughly the `hb` and `parametric_sweep`
directives; the reader's response to most of the rest is to **accept the line and discard what it did
not recognise**. `check` then reports zero errors. That combination is the through-line of this
series:

> **R-aut7-0. The governing rule, inherited by all five sibling briefs: on the netlist path, silence
> is the defect.**
> A client that writes a document, asks `check` whether it is sound, and is told "yes" must be able to
> act on that answer. Nearly every requirement below is either an instance of that rule or a
> consequence of it. **A fix that makes something work but leaves the silent-acceptance path intact
> does not satisfy the requirement.**

## 3. Why that rule is worth a five-brief series

The cost of silence is usually described as wasted effort. This exercise showed it is worse than that.

Finding one undocumented `analysis type=` token took eight guesses. Reconstructing one analysis
directive's key names took roughly fifteen round trips, each a full process launch and a full result
payload. That is the ordinary cost, and it is already high.

**The extraordinary cost is that the client reported a working component as broken.** The one-net
`Tuner` line produced a circuit whose bias tee delivered nothing, with internal nodes settling at
`-Vbias`. Every diagnostic was clean: `check` reported zero errors and zero warnings, `explain`
reported the analysis runnable, and the run itself returned `status: ok` with `Pout = -300 dBm` — the
engine's floor sentinel — at all 56 drive points, with no diagnostic. On that evidence the client
wrote up `TunerModel` as defective, with a minimal reproduction case, and stopped.

The model is correct. Nothing in the surface was willing to say which of the two participants was
wrong.

> A surface that stays silent does not merely slow a non-human caller down. **It manufactures
> confident wrong answers about the product** — and a client that reports them is behaving
> reasonably on the evidence it was given.

That is the argument for AUT-8 specifically, and it is why R-aut8-4 (refusing a wrong net count on an
instance line — a handful of lines against a registry that already exists) is the first thing in the
series to do.

## 4. Measured token cost

Raw payload sizes over the wire, from the same exercise. Roughly four bytes per token. AUT-9 acts on
these.

| Call | Bytes |
|---|---|
| `tools/list`, on every session | 20,027 |
| `reference components` | 296,988 |
| `reference component-notes` | 90,173 |
| `run sparam`, 551 points, 2 ports, returned inline | 173,151 |
| `import convert --list-cells`, whose answer is one cell name | 30,546 |

The three structural amplifiers behind those numbers — every diagnostic emitted twice, results that
cannot be narrowed by axis, and JSON serialised inside a JSON string — are R-aut9-8, R-aut9-9 and
R-aut9-12.

## 5. What this series does not do

- **No change to the format-is-the-interface design.** No per-primitive edit verbs (R-aut0-5). Every
  requirement either fixes a directive the reader never reads, refuses an input, generates a page,
  narrows a payload, or adds a verb.
- **No new file format.** AUT-10 documents formats that already exist.
- **No remote control of a running GUI** (R-aut-11).
- **No change to `TunerModel` or to any component's electrical behaviour.** The exercise's one
  apparent model defect was a client authoring error that the surface failed to catch; the model is
  correct. Every requirement is about what the surface *says*, not what it computes — except
  R-aut8-3, which is a directive the reader never reads.

## 6. Suggested order across the series

**R-aut8-4 first** — refusing a wrong net count is the single change that would have prevented this
exercise's false defect report. **R-aut8-1 and R-aut8-2 next**, refusing unknown analysis keys and
type tokens: one change that turns three silent wrong answers into refusals, and it will surface
anything else of the same shape while the rest is written.

Then **AUT-10**, which is what a client should have been reading instead of guessing, and which is
independent of everything else. Then the rest of **AUT-8**, then **AUT-9** by severity — its
correctness half before its payload half. **AUT-11** and **AUT-12** are independent of all of the
above and of each other.

## 7. On completion of the series

Write the findings to the relevant `RESOLVED.md` — `src/Cli/RESOLVED.md` exists; `src/Core` needs one
if the netlist-reader work lands there. **Never write findings to a `CLAUDE.md`.** Update
`docs/design/cli.md` and `docs/design/automation-architecture.md` as each brief lands, and the user
docs at `docs/user/src/reference/cli.md` for any new verb, with an example — do not regenerate the
user docs. The verb list in the repo-root `CLAUDE.md` is standing project memory: flag the one-line
addition for the owner rather than editing it as part of a findings write-up.

**Series-level gate, beyond the per-requirement ones in each brief:** a test that drives the MCP
server as a process over stdio — `initialize`, `tools/list`, then one call per tool — and asserts each
result document validates against the tool's own declared schema. The exercise found more than one
schema that under-describes what its tool accepts, and nothing currently catches that.
