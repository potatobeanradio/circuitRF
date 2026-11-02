# Sonnet Brief — AUT-12: layout and render ergonomics

**Read `brief-automation-7-mcp-hardening.md` first.** Depends on **RND-2** (the `render` verb) and
**AUT-4** (`explain`'s query surface).

**Scope: the four places where the artwork half of the surface — otherwise the strongest part of it —
made a caller do arithmetic or edit a document by hand.** Small, independent of every other brief in
the series, and independent of each other. Anyone can take this in parallel.

---

## R-aut12-1. Layer visibility and colour need a supported route

A Gerber import assigns the six copper layers near-identical colours, so a copper *overlay* — a
reasonable thing to ask for, and one of the three tasks — is unreadable. `render --layers` selects
which layers draw but nothing sets how they draw. The exercise resolved it by hand-editing the
generated `.ctech`, an undocumented format, outside the MCP.

Either document the format (R-aut10-4) or add a render-time override (`layerColors`, applied to the
cloned technology `TechnologyLayerSelection` already produces so a shared cached instance is not
mutated — the trap `docs/design` records for `--layers`). A render-time override is preferable: it
does not modify the design to change a picture.

## R-aut12-2. `render --fit` needs a way to exclude an outlier layer

`--fit` over all layers of an imported board is dominated by the drill-map fabrication drawing, which
sits far outside the board and shrinks it to a fraction of the frame. `--hide-layers` removes the
content but a fit that ignores hidden layers is not currently the behaviour a caller can rely on.
Add `--fit-layers a,b` (frame on these, draw everything), or specify and test that `--fit` frames only
what is drawn.

## R-aut12-3. `explain --extents` emits a format `render --window` refuses

Extents come back as bare numbers in metres. `--window` requires an SI unit on every coordinate and
refuses a bare number — correctly, and for a good reason. But the one tool that tells a caller where
the content is emits exactly what the other tool rejects, so every windowed render needs a manual
conversion. **Have `explain --extents` emit unit-bearing strings alongside the numeric values**, in
the spelling `--window` accepts, so the output of one is the input of the other.

## R-aut12-4. `convert` with a `.clay` output path produces a directory of that name

Asking for `out/board.clay` produced a *directory* `out/board.clay/` containing a cell folder whose
layout view is the actual `.clay`. The result document does report the true output path, so nothing
is lost — but a path that names a file and yields a directory of that name is a surprise worth
removing. Either write the `.clay` where asked, or refuse a file-shaped path and say a directory is
required.

---

## Gates

**R-aut12-1: render the same layout twice in one process with different layer colours** and assert
the second render is unaffected by the first — the shared-cached-technology trap `docs/design`
records for `--layers` applies identically here, and it is the defect that only appears on the second
call.

**R-aut12-3: assert `explain --extents`' emitted window string is accepted verbatim by
`render --window`** — the round trip is the whole requirement.

## On completion

Write the findings to the relevant `RESOLVED.md`; **never to a `CLAUDE.md`.** Update
`docs/design/cli.md` and `docs/design/automation-architecture.md`, and the user docs at
`docs/user/src/reference/cli.md` where a verb or flag changed — do not regenerate the user docs.
See `brief-automation-7-mcp-hardening.md` §7 for the series-level completion rules.
