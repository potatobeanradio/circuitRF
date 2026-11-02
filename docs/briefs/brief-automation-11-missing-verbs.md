# Sonnet Brief — AUT-11: extraction, and the tools a client asked for and did not find

**Read `brief-automation-7-mcp-hardening.md` first.** Depends on **AUT-3**'s rule that a verb holds
no logic of its own: each of these calls the function the GUI's own command calls, or the renderer the
GUI draws with (**RND-1**), rather than a headless second copy.

**Scope: four capabilities a client reached for and did not find.** R-aut11-1 is by a wide margin the
most valuable thing in the whole series; the other three are conveniences that each removed a
disproportionate amount of incidental work.

Additive: nothing existing changes behaviour.

---

## R-aut11-1. Nothing converts a schematic into a runnable netlist

`run` on a `.csch` fails with `Error: Cell '"FormatVersion"' not found in libraries (referenced by
'')` — it parsed the JSON document as netlist text and reported its first key as a missing cell name.
`check` and `explain` both accept a `.csch` happily, so the surface reads as though `run` should too.

There is no `extract` verb and no `netlist` verb. **The consequence is that the automation surface
cannot simulate any design a user actually drew.** It can run hand-authored netlists only — and every finding
in AUT-8 and AUT-9 was reached only because a testbench had to be reconstructed from scratch instead
of started from a known-good extraction.

**It is also the direct cause of the false defect report in
`brief-automation-7-mcp-hardening.md` §3.** The workspace held a schematic that
instantiates tuners correctly. Had the client been able to extract its netlist, one look would have
shown two nets on the tuner line and the whole detour would not have happened. An extraction verb is
not only a capability — it is the reference answer a client checks its own authoring against.

Do the smallest correct thing:

- **`circuitrf netlist <path.csch> [-o out.cnl]`** — write the extraction the GUI's own Simulate
  performs, through the same code path, on `Authoring.cs`' terms (no second copy of the extraction).
  A `.cws` plus `--cell` and a cell folder resolve as `render` resolves them.
- **Let every run verb accept a `.csch`** and extract in memory, which is what `explain` already does
  (repo-root `CLAUDE.md` records that a `.csch` reaches the elaborator through the `.cnl` because the
  two readers disagree about bare words — the same reason applies here).
- Until both land, **`run` on a `.csch` must refuse by kind**: *"run takes a netlist; extract one
  with `circuitrf netlist …`"*. The current message is actively misleading.

**Gate: a test that extracts a netlist from a schematic and runs it headlessly, byte-for-byte against
the in-process extraction** — the pattern `AuthoringCliVerbTests` and `RenderCliVerbTests` already
establish.

---

## R-aut11-2. A `plot` tool

Hand-authoring a `.cdd` to draw one trace is the largest piece of incidental work in an otherwise
short task. Add a tool that takes a result path, a list of traces (`{cube, i, j, y: "dB"}`), optional
axis windows and a title, and writes a picture — building the same document `render --data` consumes,
so there is one plotting path rather than two. **`.cdd` authoring stays available**; this is the
convenience over it, not a replacement.

## R-aut11-3. A `find` tool

There is no way to ask the server what exists. Locating a workspace containing a particular device
required searching the filesystem outside the MCP entirely. Add a `--root`-scoped listing: workspaces,
their cells, each cell's views and each cell's declared analyses. The server already knows how to read
every one of those documents; it simply never offers to enumerate them. Bound the walk and make the
depth an argument.

## R-aut11-4. State that the surface writes documents but provides no way to write one

The server's own `instructions` say circuitRF is driven by *writing* its documents, and no tool
writes a file. `create` also cannot create a missing parent directory — creating a workspace under a
path whose parent does not exist fails with `No such directory`. Both behaviours are defensible; both
are currently discovered by hitting them.

Say plainly in `instructions` that the client supplies its own file writing, and **have `create`
create missing parents** (or refuse with the parent named and a suggestion, which it half does
already). Adding a general write tool is explicitly *not* proposed: it would put an unbounded
filesystem write behind the server's root, and the format-is-the-interface design (R-aut0-5) assumes
the client already has one.

---

---

## Gates

**R-aut11-1: extract a netlist from a schematic and run it headlessly, byte for byte against the
in-process extraction** — the pattern `AuthoringCliVerbTests` and `RenderCliVerbTests` already
establish, and the same comment-stripped source scan proving no second copy of the extraction exists.

**R-aut11-2: a plot rendered by the new tool must be byte-identical to one rendered from the
equivalent hand-authored data display**, for the same reason — one plotting path, not two.

**R-aut11-3: assert the listing is bounded** and that a path outside the server's root never appears
in it.

## On completion

Write the findings to the relevant `RESOLVED.md`; **never to a `CLAUDE.md`.** Update
`docs/design/cli.md` and `docs/design/automation-architecture.md`, and the user docs at
`docs/user/src/reference/cli.md` where a verb or flag changed — do not regenerate the user docs.
See `brief-automation-7-mcp-hardening.md` §7 for the series-level completion rules.
