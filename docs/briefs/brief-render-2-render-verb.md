# Sonnet Brief — RND-2: `circuitrf render`

**Read `brief-render-0-overview.md` first.** Depends on **RND-1** — this brief adds no rendering code
and moves no files; it is argument parsing, viewport arithmetic, refusals and reporting, which is
what `src/Cli/Authoring.cs` already established a CLI verb is allowed to be.

**Scope: a schematic, a symbol or a layout, as a file.** `.cdd` is RND-4.

---

## 1. The shape

```
circuitrf render <path> -o <out.svg|.pdf|.png> [options]
```

**R-rnd2-1. One verb over every document kind, inferred from the path.** `check`'s rule (R-aut4-11),
through the same function — `src/Cli/DocumentKinds.Classify`. `<path>` may be:

- a **`.csch`, `.csym` or `.clay` file** — including one that belongs to no workspace at all. An
  orphan document is a first-class input, not a degraded one: the layout resolves its technology by
  the walk-up it always uses, finds none, and renders on the fallback palette exactly as the layout
  editor does with an unresolved technology. **That is a note, not a warning, and never a refusal** —
  a caller rendering a bare `.clay` handed to it by a converter knows there is no workspace.
- a **cell folder** — the view chosen by `--view`, or, when the folder holds exactly one view, that
  one. A folder with more is a **refusal listing the views it holds and naming `--view`** (R-rnd0-6).
  Primacy inside a view sub-folder is `CellFolder.ResolvePrimary`'s answer and no other.
- a **workspace**, with `--cell <name>` — the cell is resolved through the same resolver
  `explain --cells` reports (RND-3). A workspace with no `--cell` is a refusal naming it; rendering
  "the workspace" is not a thing that means anything.

**R-rnd2-2. `-o` is required and its extension chooses the format**, exactly as `convert` infers a
format from a path. `--format svg|pdf|png` overrides. An extension this verb does not write is a
refusal listing the three. There is no stdout image: a binary on stdout would break §3.1's contract
that stdout is *the result* in a form a caller can read, and `--json` has to be able to co-exist
with the write.

---

## 2. The viewport

**R-rnd2-3. `--fit` is the default and it is the GUI's own fit.** `LayoutViewport.ZoomToFit` for a
layout (already in `src/Design`), and the symbol/schematic counterparts `ComponentPreviewRenderer`
already solves — including its convergent widening pass for marks that are drawn in *pixels* and
cannot be put in a world-space box (a pin's name, a label). Read that file's comment before writing
any fit code; the two errors it documents (reserving room on the wrong side, and reserving the widest
label at the rightmost pin) are both errors this verb can make again.

```
--fit                      the whole document, with --margin (default: the GUI's own)
--window <x0,y0,x1,y1>     an explicit world-space rectangle
--center <x,y> --span <w>  a centre and a width; height follows from the output aspect
```

The three are **refused together rather than ordered** — `explain`'s rule for its own three
questions. A precedence nobody stated is an invention.

**R-rnd2-4. On a layout, every coordinate carries an SI unit and a bare number is a refusal.**
R-rnd0-5, and this is the place it bites. `--window 0,0,500,300` could mean DBU, µm or mm; those are
three pictures separated by six orders of magnitude and all three are *plausible*. The refusal prints
what it would have accepted (`500um`, `0.5mm`) and does not guess. A schematic or symbol takes bare
numbers, because its coordinates are dimensionless design units — and the `--json` document says so
in its `units` field rather than leaving the caller to assume metres.

**R-rnd2-5. The requested window is honoured exactly, and the aspect mismatch is reported.** A
window whose aspect differs from the output's is **letterboxed, never cropped and never stretched** —
a caller that asked for a region and silently got less of it than it asked for has no way to notice.
The resolved window (after letterboxing) is in the `--json` document.

---

## 3. Size, resolution and detail

```
--size <W>x<H>       output size. Device pixels for png; points for svg/pdf. Default: 1600x1200.
--scale <n>          raster multiplier (2 = "@2x"). png only; a refusal on svg/pdf, which have no
                     pixels to multiply.
--dpi <n>            an alternative spelling of --scale for png, relative to 96. Refused together
                     with --scale.
```

**R-rnd2-6. `--detail` is a real knob because the LOD tiers are real.** `LayoutRenderOptions` already
carries six of them, and each already documents that "a NEGATIVE value disables the tier outright,
which is how an export pins exact vector geometry". So:

| `--detail` | Means |
|---|---|
| `full` (default) | every LOD tier off. What is stored is what is drawn. |
| `screen` | the tiers engage exactly as they would on a canvas at this zoom — what the user actually sees. |
| `<n>` | the pixel budget, passed to `DetailPixelThreshold`; the octave bucketing in `LayoutRenderDetail` still applies and the effective tolerance is reported. |

**Default `full`, and this is a deliberate cost.** The measurement in `LayoutRenderDetail`'s own
header: a real 6-layer board is 3,284 shapes and **764,110 vertices**, of which 99.4% belong to 1,928
polygons; decimating at half a device pixel leaves 100,421. An undecimated SVG of that board is
therefore ~7.6× the vertices of the on-screen one, and vector formats store every one of them.
**The verb reports the vertex count and the output size it produced** (§6) so a caller finds this out
from the answer rather than from a 40 MB file. `--detail screen` is the answer for anyone who wants
the picture rather than the geometry, and RND-5's docs must say so plainly.

`PathCache` is **null** on this path — one-shot render, nothing to persist across frames — which is
what every existing export already passes.

---

## 4. Layers

```
--layers <a,b,...>        render only these
--hide-layers <a,b,...>   render everything except these
```

Layout only; refused on a schematic or symbol. The two are mutually exclusive. Default is every layer
the resolved technology marks visible — **the technology's own `LayerDef.Visible`, which is what the
editor honours**, not "all layers regardless".

**R-rnd2-7. A layer name that the resolved technology does not define is a refusal naming
`explain --layers`.** Not a silent skip. A caller that misspells a layer and gets a picture with that
layer missing has no way to tell it apart from a layer that is genuinely empty, and this series
provides the verb that answers the question (RND-3).

**R-rnd2-8. Selection is applied to a CLONE of the resolved technology, never to the cached one.**
`TechnologyCache` hands back a shared instance; flipping `LayerDef.Visible` on it would leak into the
next render in the same process — which is not hypothetical, because `serve` runs many calls in one
process. This is the kind of defect that only appears on the second call.

---

## 5. Colour

```
--theme <name|path.ccolor>   a theme name (resolved through ThemeResolver's chain) or a file
--variant light|dark         default: light
--background opaque|transparent   default: opaque
--grid                       default: off
```

**R-rnd2-9. Resolution order is `ThemeResolver`'s existing chain and nothing new**: an explicit
`--theme <path>` first, then, for a name, workspace directory → user themes directory → shipped
`.ccolor` → `ColorTheme.BuiltIn`. With no `--theme`, the workspace's own recorded theme; with no
workspace, the shipped default. This is precisely the "default from the workspace or else the
circuitRF default" the request asks for, and it already exists — **the only thing RND-1 had to fix
was that step 3 silently vanished with no Avalonia host** (R-rnd1-5). A theme name that resolves to
nothing is a refusal listing what it looked at, not a fallback.

`--background transparent` maps to `TransparentBackground`, which the layout renderer documents as
export mode; `--grid` off is what every existing export passes (`excludeGrid: true`). Overlay,
handles, marquee, PCell pins, snap glyphs and EM/DRC overlays are all **off by construction** — they
default off and this verb never sets them. **Rulers are the exception and they stay ON**, because
`ShowRulers`' own comment says a ruler is document content rather than overlay state, it is in the
`.clay`, and an export that dropped it would contradict the layout design doc. `--no-rulers` may
turn them off; the default matches the document.

---

## 6. Output, progress and `--json`

**R-rnd2-10. Progress goes to stderr and through `RunControl`, and both come free.** §3.1: stdout is
the result, stderr is everything else. Report the stages that actually take time — *resolve*
(hierarchy walk and technology), *measure* (extents), *draw*, *encode* — and for a layout, the
instance/shape counts as they are resolved. Deliver them through `RunHost.Control`'s `RunProgress`,
the same one `em` uses, so `serve` gets `notifications/progress` and cancellation with **no plumbing
in this verb at all** and a cancelled render exits 130 and writes nothing.

Whether a render is slow enough to need this is a measurement, not a guess: take it (§7.4) and say
so. The honest expectation is that a single frame is fast and the *resolve* of a deep hierarchy and
the *encode* of a large vector file are not.

**R-rnd2-11. `--json` per §3.2**, `outputs` carrying the file written, `result.render` carrying what
was decided — because every one of these is something the caller could not otherwise see:

```
"result": { "render": {
    "path","kind","view","format",
    "viewport": { "mode","x0","y0","x1","y1","unit","scale","zoom","letterboxed" },
    "extents":  { "x0","y0","x1","y1","unit","scale" },
    "size":     { "width","height","unitKind","scale" },
    "theme":    { "name","variant","resolvedFrom" },
    "layers":   [ {"name","rendered","shapes"} ],
    "detail":   { "mode","toleranceDbu" },
    "counters": { "shapesExamined","shapesDrawn","verticesEmitted","instancesDrawn","drawCalls" },
    "bytes": 0 } }
```

`counters` is `LayoutRenderResult`'s own counters — `ShapesExamined`, `ShapesDrawn`,
`PathsConstructed`, `DrawCalls`, `LayersVisited`, `InstancesExamined`, `InstancesDrawn`, all of which
exist already. They are deterministic and machine-independent by design, which is what makes §7's
gates assertions about work done rather than about wall clock, and it is why this verb reports them
rather than a duration.

**`verticesEmitted` is the one counter this brief ADDS**, in exactly that existing style and for
exactly that reason: R-rnd2-6's whole claim is that `--detail` changes how much geometry comes out,
and there is currently no counter that can be asserted against. It counts vertices contributed to
committed-layer geometry, on the same terms `PathsConstructed` already uses (overlay, ghost, handle
and marquee geometry excluded). Add it to `LayoutRenderResult`'s doc-comment list beside the others —
a counter that is not documented there is one the next person will add a second time.

---

## 7. Gates

1. **Byte identity against the GUI, end to end.** `render -o x.svg` run **as a process** produces the
   same bytes as the in-process `CircuitRF.Render` call on the same document, theme and viewport —
   and that call is already gated against the GUI's clipboard export by RND-1 §5.2. This is the
   `ConvertCliVerbTests` / `EmCliVerbTests` standard and it is not negotiable.
2. **Every refusal in this brief has a test**, asserting the diagnostic id and that the message names
   the flag that answers it: multi-view cell folder without `--view`; unknown output extension;
   bare-number `--window` on a layout; unknown layer name; two viewport modes together; `--scale` on
   svg; `--layers` on a schematic; a `--theme` name that resolves to nothing.
3. **The orphan document renders.** A `.clay` in a bare temp directory with no workspace above it
   produces a file, exits 0, and emits a NOTE (not a warning) about the unresolved technology. Same
   for a `.csch` with no workspace.
4. **The real board, measured and reported** (R-rnd0-3's series-wide item): time and output size for
   svg/pdf/png × `--detail full`/`screen` on a ~750k-vertex import. **Report the numbers; do not add
   a timing test** — `feedback-no-new-timing-benchmark-tests`. Assert the COUNTERS instead: that
   `--detail screen` emits materially fewer vertices than `--detail full` on the same document, and
   that `--layers` narrowing reduces `shapesDrawn` and not merely `shapesExamined`.
5. **Layer selection does not leak.** Two renders in one process, the second with no `--layers`,
   render every layer (R-rnd2-8). This test fails on the obvious implementation.
6. **The viewport is honoured.** A `--window` covering a known quarter of a fixture's extents
   produces an image whose drawn content matches that quarter — measured from the raster, as
   `ComponentPreviewTests`' own probe does, not asserted from the transform arithmetic.
7. **Cancellation.** A render cancelled through `RunControl` exits 130 and leaves **no output file** —
   §7's rule that a cancelled run abandons its result rather than publishing a partial one. A
   half-written PNG that a caller reads as a finished one is the failure this prevents.

---

## 8. On completion

`src/Cli/RESOLVED.md`, **never a `CLAUDE.md`**. Update `docs/design/cli.md` — a new section for
`render` in §2's verb list and a full section beside §8's `em` and §10's `check`/`explain` — and the
repo-root `CLAUDE.md` verb list. The user-docs chapter is RND-5's.

**Report, do not silently absorb:**
- §7.4 in full, per format and per detail mode. This is the number that decides whether the docs
  recommend `--detail screen` or `full` as the everyday answer.
- Any GUI render option that has no CLI spelling and should have — and any that has one and should
  not, because a knob nobody can explain is a knob nobody will set correctly.
- Whether progress was worth reporting at all (R-rnd2-10), from the measurement rather than from
  taste.
