# Sonnet Briefs — GI series: Gerber import, round 2

**Five phases, and they are ordered by how much of the user's own time each one gives back — not by
size.** GI1 and GI2 are the two that change what a real six-layer board import produces; GI3 makes the
document that import produces fillable-in; GI4 and GI5 stop the import from ignoring files that were
sitting in the same folder holding the answers it apologised for not having.

| Phase | What it fixes | Size |
|---|---|---|
| **GI1** — the report says less than the import knows | Three places where a fact was established and then not used or not stated | Small |
| **GI2** — the stackup skeleton | An import that resolves six copper layers in order and then writes a stackup with no conductors in it | Medium |
| **GI3** — the substrate fields | Conductor conductivity, via wall thickness, total stack height, duplicate names, via rows inside the z order | Medium |
| **GI4** — companion declaration files | A folder's own format/tool declarations, currently classified as "no Gerber or drill content" and skipped | Medium |
| **GI5** — the netlist companion | Net names, plating, layer spans and the via/component-hole distinction that the import currently says are "not available" | Large |

## The single fact behind the whole series

**A Gerber file set is not one file, and the artwork is not the only thing in the folder.** The import
already understands the first half of that — `GerberFileClassifier` reads a folder, not a file, and
`FindSiblingDrillCandidates` reaches into neighbouring folders for drill data. It does not yet
understand the second half. A production output set routinely ships, next to its artwork:

* a **parameter file** stating the coordinate format, units and zero suppression the whole job was
  written with;
* a **tool listing** stating which tools are plated and which are not;
* a **netlist** stating a net name for every pad, whether each hole is a via or a component hole, and
  which layers each hole reaches;
* an **archive** holding a second copy of all of it.

Every one of those is currently classified `Other` and reported as "skipped". Meanwhile the import
prints, in the same run, three separate sentences of the form *"the distinction was not available"* and
*"this was INFERRED"*. GI4 and GI5 close that gap; GI1 and GI2 fix the places where the answer was
already in hand and got dropped anyway.

## Standing rules for every phase in this series

1. **Never invent a substrate.** `GerberStackupMapping`'s own header states this and it is not being
   relaxed by GI2 — GI2 separates *structure* (how many conductors, in what order, bound to which
   drawing layers) from *values* (thickness, permittivity, loss tangent) and supplies only the first.
   A skeleton must be **unsimulatable** until someone types the numbers.
2. **A guess and a declaration must never read the same.** L4d's R-L4d-5 and L4g's R-L4g-10 both exist
   because a silently wrong stack order produces a run that completes cleanly and answers a different
   question. Every phase here adds evidence sources; each one must be reportable by name.
3. **Never name a laminate, a product or a toolchain.** Element conductivities are physics; laminate
   trade names are not, and the repo's own rule (root `CLAUDE.md` §"Commercial Vendor References")
   forbids the table that would tempt someone. GI4 recognises a declaration file **by its keywords**,
   never by a vendor's filename convention, which is also what the classifier's own doctrine already
   requires ("BY CONTENT, extension second").
4. **Additive and nullable.** Every new `StackupLayer` or `Technology` field in this series follows the
   `SheetAt`/`PresentWithLayer`/`Fill` pattern — nullable, default meaning bit-identical to today, no
   `.ctech` `FormatVersion` bump.
5. **No new timing tests.** Assert counters and content, never wall clock.

## Test loop (all five)

```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

`src/Design` is the home of every reader and writer touched here, and `tests/Firewall.Tests` is what
holds the no-Avalonia boundary shut while they grow.

## On completion of each phase

Write the phase's completion entry at the top of `src/Design/RESOLVED.md` — **not** `CLAUDE.md`, and
not `src/Ui/RESOLVED.md` (the interchange readers moved to `src/Design` in 2026-09). Each phase names
what to record.
