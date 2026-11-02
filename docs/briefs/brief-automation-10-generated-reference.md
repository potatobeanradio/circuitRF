# Sonnet Brief — AUT-10: the generated reference — say what may be written

**Read `brief-automation-7-mcp-hardening.md` first.** This brief extends **AUT-6**, which built the
generated component catalogue; the machinery it needs mostly exists and is being pointed at more
things.

**Scope: telling a client what may be written, generated from the live registries so it cannot
drift.** Purely additive — nothing existing changes behaviour, so this brief can land at any time and
in parallel with the rest of the series.

**It is also the cheapest prevention in the series.** R-aut10-1 alone would have prevented three of
AUT-8's findings outright, and R-aut10-2 would have prevented the false defect report that
`brief-automation-7-mcp-hardening.md` §3 describes. A client with no schematic editor and no source
tree has nothing but these pages.

---

## R-aut10-1. A generated `reference` topic for analyses

**The single most valuable page not currently written.** Every `analysis type=` token, and for each
one every key with its default, its unit and whether it is required — generated from the live
registries exactly as `components` is generated (AUT-6's pattern), so it cannot drift. This one page
prevents R-aut8-3, R-aut8-1 and R-aut8-2 outright.

## R-aut10-2. The generated component catalogue reports a symbol's pin count, not a netlist line's net count

`reference components` reports `Tuner` as `"ports": {"count": 1, "names": ["1"]}`. **The `.cnl`
instance line takes two nets** — the DUT node and the reference node — as the repo's own tests write
it (`tests/Engine.Tests/Loadpull/TunerModelTests.cs:21`,
`tests/Core.Tests/Netlist/TunerNetParseTests.cs`, and every loadpull fixture beside them:
`Tuner:Load n_drain 0 …`). `Port` is reported as `"names": []`, stating no count at all, and its
`.cnl` line takes two.

The catalogue is generated from the **symbol** registry, and a symbol's pin count is not the netlist
line's net count. They differ wherever a terminal is implicit on the glyph, and they differ for `SDD`
by construction (two nets per differential port). The catalogue already carries a note against `Term`
saying that nothing below the UI firewall states how many nets its instance line takes — **that
caveat is the defect, not a disclaimer of it.** For a client with no schematic editor and no source
tree, this catalogue is the only statement of the netlist contract that exists.

**Add the netlist net count to the catalogue as its own field**, distinct from the symbol's port
count, generated from whatever the reader actually binds. Where the count is dynamic (`SDD`,
`SnP`, `Z_Port`), state the rule rather than a number.

This is not a small documentation nit; it is the origin of the series' worst finding. Writing
`Tuner:T1 n1 Z[1]=50 … BiasTee=on Vbias=48` — one net, exactly as the catalogue describes the part —
produces a circuit whose bias tee delivers nothing, and reads exactly like a broken component. It is
not: with the second net supplied, the bench behaves correctly and the loadpull that could not be made
to run completes in 38 seconds. **The component model is correct; the catalogue and the reader are
what mislead.** `brief-automation-7-mcp-hardening.md` §3 tells that story in full; R-aut8-4 is the
refusal that would have caught it at the line.

**Gate: for every registered type, a test asserting the catalogue's stated net count equals the
number of nets `CnlReader` binds** for a minimal instance line of that type.

## R-aut10-3. A `reference` topic for the data-display format

`render` on a `.cdd` is the only route from the automation surface to a plot, and `create` does not
make one. The exercise succeeded only because an unrelated data display happened to exist on the
machine to copy from; a client working in a fresh workspace has nothing. Document the format, or add
R-aut11-2, or both.

## R-aut10-4. A `reference` topic for the technology format

Needed for the same reason and reachable the same way — see R-aut12-1.

## R-aut10-5. Correct and extend the tool schemas

- `read` **accepts a `.cdd`** and returns it; its schema does not list `.cdd`. The exercise found this
  by trying it. Audit every tool's declared path kinds against what it actually accepts — a schema
  that under-promises is as costly as one that over-promises, because a client believes it.
- `render`'s `attachImage` does not say what the 4 MB threshold does to a caller who asked for bytes
  and gets a path. State the behaviour in the schema, not only in the result.
- The server's `instructions` should carry **one worked end-to-end example** — create, write a
  `.cnl`, `check`, `run`, `render` — in about fifteen lines. Most of what the exercise learned by
  trial and error is in that sequence.

---

---

## Gates

**For every registered component type, assert the catalogue's stated netlist net count equals the
number of nets `CnlReader` binds** for a minimal instance line of that type (R-aut10-2). This is the
gate that closes the series' worst finding.

**Assert every `analysis type=` token the reader accepts appears in the generated analyses topic, and
vice versa** (R-aut10-1) — a generated page that can fall behind the registry is the problem it was
written to solve.

**Assert every `reference` topic listed by the topic index resolves and is non-empty.**

## On completion

Write the findings to the relevant `RESOLVED.md`; **never to a `CLAUDE.md`.** Update
`docs/design/cli.md` and `docs/design/automation-architecture.md`, and the user docs at
`docs/user/src/reference/cli.md` where a verb or flag changed — do not regenerate the user docs.
See `brief-automation-7-mcp-hardening.md` §7 for the series-level completion rules.
