# Sonnet Brief — RND-3: what is in there, and how big is it — three questions on `explain`

**Read `brief-render-0-overview.md` first.** Depends on **RND-1** for extents on a layout (the fit
arithmetic lives there); the other two need nothing new.

**Scope: the three questions a caller must be able to ask before `render` is usable.** *What cells
does this workspace hold, and which views does each have?* *What layers can I ask for?* *How big is
this document?* Today the only way to answer any of them is to open the GUI.

---

## 1. Why these are options on `explain` and not three new verbs

**R-rnd3-1. R-aut-9, and `cli.md` §11.3's count discipline.** A client that discovers tools up front
carries every description for the whole session whether or not it calls one. Three new verbs would be
three new tools — a 43% increase in a surface the repo deliberately capped at seven — to answer three
questions that are all the same question `explain` already exists for: **what did circuitRF decide?**

They are not stretches:

- **Which file is a cell's schematic** is `CellFolder.ResolvePrimary`'s decision, and it is a
  three-state one (`PrimaryState`) with real failure modes — a named primary that is missing, an empty
  sub-folder, an ambiguous folder. That is a resolution result, reported exactly as `--ref`'s is.
- **Which layers exist** is the answer to the technology walk-up `explain` *already performs and
  already reports* for a `.clay`. This adds the payload to a walk that is being done anyway.
- **How big is it** is the one genuinely new computation, and it is what `--fit` resolves to — which
  is a decision a caller needs before it can ask for anything other than `--fit`.

**R-rnd3-2. They join the "refused together rather than ordered" rule.** `explain` already refuses
`--expr` with `--analysis` rather than inventing a precedence. Six questions obey the same rule as
three.

---

## 2. `explain <workspace|folder|cell> --cells`

Every cell reachable from the path, and for each: its name, its folder, which of the three views it
has, and for each view the primary file and its resolution state.

```
"result": { "explain": { "cells": [
    { "name","folder","outsideWorkspace",
      "views": [ { "type":"schematic|symbol|layout",
                   "primary","state","candidates","defect" } ] } ] } }
```

**R-rnd3-3. It reports the resolution, not a directory listing.** `state` is `PrimaryState` verbatim
and `defect` is `CellViewFileValidator.DescribeDefect`'s answer — both of which `check` already
surfaces. A cell whose schematic sub-folder holds three `.csch` files and no named primary is
**listed, with its ambiguity**, not omitted and not silently resolved to the alphabetically first
one. The whole value of this over `ls` is the cases where the answer is not obvious.

**R-rnd3-4. It uses the workspace's own scanner** (`WorkspaceScanner`), so a folder the GUI hides is
hidden here too. `.generated-cells` is the case that matters: it holds one content-addressed cell
folder per distinct PCell placement, it can hold hundreds, none of them is a cell anyone authored,
and the project tree already excludes it deliberately. **Excluded by default; `--all` includes them**,
because a caller debugging a PCell genuinely does want to render one.

`--cells` on a **cell folder** reports that one cell — the same shape, one entry. That is what makes
it composable with `render`: ask what views a cell has, then render one.

---

## 3. `explain <clay|ctech|cell|workspace> --layers`

The resolved technology's layers, and **which of them the document actually uses**.

```
"result": { "explain": { "layers": {
    "technology","resolvedFrom","resolvedBy",
    "layers": [ { "name","number","datatype","purpose","visible","selectable",
                  "color","fill","shapes","instancesUsing" } ] } } }
```

**R-rnd3-5. `shapes` is the field that makes this worth having.** A technology defines every layer a
process has; a given `.clay` draws on a handful. A caller told only the technology's list will ask
`render --layers` for layers that are empty and conclude the render is broken. On a `.ctech` alone
there is no document to count against and the field is absent — not zero, which would be a lie.

**R-rnd3-6. The count is of what would be RENDERED**, hierarchy included — a layer used only inside a
placed sub-cell is used. This is `CellHierarchy`'s existing recursive walk with a layer filter, which
it already takes (`layerVisible`). Do not write a second walk.

**R-rnd3-7. The technology walk is reported as a walk**, R-aut4-7, and it is the same walk `explain`
already prints for a `.clay`. Where it resolves to nothing, that is the answer — with the fallback
palette named, since that *is* what `render` will draw with (R-rnd2-1) and a caller needs to know the
colours it gets are not the process's.

---

## 4. `explain <clay|csch|csym|cell> --extents`

```
"result": { "explain": { "extents": {
    "x0","y0","x1","y1","width","height","unit","scale","empty",
    "perLayer": [ { "name","x0","y0","x1","y1" } ] } } }
```

**R-rnd3-8. Base SI, with the unit AND the scale named.** R-aut4-9, and R-rnd0-5. A layout's numbers
come out in metres with `unit: "m"` and the DBU scale that produced them stated, so a caller can
reconstruct what it would type into `--window`. A schematic's or symbol's come out as dimensionless
design units with `unit: null` and are *said* to be dimensionless — not quietly emitted as if they
were metres, which is the mistake `sweep-unit-scale-and-mark` records in another form.

**R-rnd3-9. It is the same box `--fit` uses, from the same code.** If `--extents` and `render --fit`
can disagree, the number is worse than useless: the caller uses one to compute a window for the
other. One function, in `src/Render` or `src/Design`, called by both.

**R-rnd3-10. Empty is `empty: true`, not a zero box.** A document with no geometry has no extents;
`(0,0,0,0)` is a point at the origin, which is a different fact and one a caller would happily divide
by. `perLayer` is layout-only and covers only layers with geometry.

The **symbol** case needs the same treatment `ComponentPreviewRenderer` documents at length: a pin's
name is drawn in *pixels* at a size with a floor, so it is not in the primitive bounding box and
cannot be put there. Report the **geometry** extents here — that is the stable, zoom-independent
answer — and say in the field's own documentation that a fit adds room for marks that are measured at
render time. Do not report a zoom-dependent number from a zoom-independent verb.

---

## 5. Gates

1. **`--cells` finds every cell `check` walks**, on the shipped example workspaces: the two sets are
   compared, not eyeballed. A cell that `check` validates and `--cells` does not list is a bug in one
   of them and the test says which.
2. **The awkward cells are listed with their state**: a fixture with a missing named primary, one
   with an empty view sub-folder, one with an ambiguous folder, one with a defective view file. Each
   appears with the `PrimaryState` and defect `check` reports for it.
3. **`.generated-cells` is excluded by default and included with `--all`**, on a workspace with a
   placed PCell.
4. **`--layers`' shape counts agree with a render.** For a fixture layout, `shapes` per layer equals
   what `render --layers <that layer> --json` reports as `shapesDrawn`. Two paths, one answer — and
   if they disagree, R-rnd3-6's "do not write a second walk" was violated.
5. **`--extents` equals `--fit`.** `render --fit --json`'s reported `extents` are byte-identical to
   `explain --extents`' (R-rnd3-9), for a schematic, a symbol and a layout.
6. **Extents are hierarchy-correct**: a layout whose only geometry is inside a placed sub-cell,
   rotated and arrayed, reports the transformed box — checked against `CellHierarchy.InstanceBbox`,
   which is the existing answer.
7. **The unit is present and the scale is right.** A layout whose technology declares a non-default
   DBU-per-µm reports extents that a hand-computed metre value agrees with. This is the 2-Hz bug in
   its geometric form and it deserves its own test.
8. **`explain` still writes nothing** (R-aut4-6/§10.1) with all three options, including on a
   read-only tree.

---

## 6. On completion

`src/Cli/RESOLVED.md`, **never a `CLAUDE.md`**. Update `docs/design/cli.md` §10.4 and §10.5 — the
three new questions and their `result` shapes. Docs are RND-5's.

**Report, do not silently absorb:**
- Any cell state `--cells` found in the shipped workspaces that is not clean. Per R-aut4's gate 2's
  standard, that is a finding about the repo, not something to smooth over.
- Whether `--extents` and `--fit` genuinely share one function or merely agree today (R-rnd3-9).
- Anything the layer report cannot say that a caller would need — a via layer spanning two levels, a
  layer defined only by a purpose pair, a layer the fallback palette invented. The gaps are the point
  of writing this verb before the docs.
