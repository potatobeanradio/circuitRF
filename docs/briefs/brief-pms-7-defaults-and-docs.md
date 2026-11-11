# PMS-7 — defaults, panel wording, reference and docs

Series: `brief-pms-0-overview.md`. Last brief. Its content depends on which of briefs 3–6 were built.
If brief 2 stopped the series, this brief is only the documentation of brief 1.

## What to build

### R-pms7-1 — the defaults (owner decision, made here)

For each setting the series added (`StackedRegions`, `LocalGrid`), present the owner with:

- the accuracy evidence from its briefs' gates;
- the unknown counts on the fixture family;
- what changing the default moves.

A new default changes the mesh of existing setups. Two precedents apply:

- **Hashing:** the setting is hashed as `DetailFloorDivisor` is, unconditionally, once its default
  differs from the pre-existing behaviour, so a `.snp` stamped before it is reported stale rather than
  silently reused.
- **Starters and examples:** every starter and shipped example is re-meshed and its unknown count
  reported, before and after. The examples must still run in minutes.

Do not flip a default without the owner's yes in the conversation. If the answer is "opt-in", the
budget note names the setting as a remedy only where it binds (`src/Engine/Mom/CLAUDE.md`: never name
a remedy that does not bind).

### R-pms7-2 — the EM panel

- The setting(s) appear in the planar mesh section beside the existing mesh controls, with a picker
  or checkbox (no text box).
- The mesh summary line shows cells and unknowns as it does today. When the local grid is on it
  adds the refinement-step count.
- Snapshot the panel with `tools/UiSnapshot`, look at it, list it.

### R-pms7-3 — reference and docs

- `circuitrf reference em-setup` (and the MCP `reference` resource built from the same source): the
  new `.cem` keys, their defaults, and one sentence each.
- `docs/user/src/reference/mom-engine.md`: what the mesh is now made of. Cover the stacked region and
  the local grid in plain statements of what is true, with no change history. Add one sentence on
  what a medium MMIC costs, quoting fixture S's numbers in the user's terms (unknowns, memory per
  frequency point).
- The preflight's behaviour from brief 1: a layout past the machine limit is reported within
  seconds, with the per-level breakdown, and the standards are not priced when the structure alone is
  already past it.
- Regenerate only the edited pages with DocGen at the end; no figures unless a page needs one.

### R-pms7-4 — CLAUDE.md is not edited

The durable invariants this series creates go into `src/Engine/Mom/RESOLVED.md` and `HISTORY.md`:

- a cell is a block;
- a rooftop may cover a segment;
- standards are built from the port's own lines;
- a stacked region keeps a rim.

The owner decides whether any of it is promoted.

## Gates

1. Every starter and shipped example: the unknown count before and after, reported. If a default
   moved, each example's run still completes and its result is reported against the previous one in
   |ΔS|.
2. The edited user-doc pages regenerate with DocGen, and the diff touches only those pages.
3. The panel snapshot is looked at and listed.
