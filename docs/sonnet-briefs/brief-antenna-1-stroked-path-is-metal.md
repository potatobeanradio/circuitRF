# Brief — ANT-1: a width-bearing Path is metal

**Series:** `brief-antenna-0-overview.md` §1a. **Depends on:** nothing. **Blocks:** everything.

`PlanarExtractor` discards every `PathShape` on every layer. A width-bearing stroke is how the Gerber
and KiCad readers represent every track, so **an imported board is currently EM-simulated with its
traces missing, silently**. Fix that.

This is the smallest brief in the series and the first, because no measurement taken on an imported
board means anything until it lands.

---

## 1. What is wrong

`src/Design/Layout/Em/PlanarExtractor.cs:220-226`:

```
// A Path is a centreline on a via layer for the same reason it is one on a conductor
// layer — it encloses no area — but on a via layer it now gets its OWN sentence.
if (s is PathShape)
{
    if (viaBinding.ContainsKey(s.Layer)) ignoredViaPath++;
    else                                 ignoredOther++;
    continue;
}
```

The premise is stated in the comment and it is **false for `Width > 0`**. `PathShape` carries
`Width`, `End` (`Flush` / `Round` / `Square` / `Extended`), optional arc `Edges` and an optional
`FlattenTolDbu`. `GerberReader.Regions.cs:276` keeps a stroke as a `PathShape` "primitive-for-
primitive"; `GerberWriter.cs:39` writes one back out through a circle aperture of that width;
`PcbReader.cs:1373` builds them; the DRC engine outlines them. Everything in the application except
this one branch treats a width-bearing stroke as copper.

**A zero-width Path IS a centreline** and must keep being ignored — that is what `DxfReader.cs:677`
produces for an open polyline, and it genuinely encloses no area.

## 2. The measurement that pins it

Owner-supplied imported patch board, one frequency point (`brief-antenna-0-overview.md` §1a):

| what was solved | Zin near 1.7 GHz | power leaving the port |
|---|---|---|
| as imported, N = 3,796 | 0.70 − j56.5 Ω | 2.3 % |
| as imported, N = 7,442, conformal, accelerated | 0.70 − j56.5 Ω | 2.3 % |
| feed replaced by an equivalent `Rect`, N = 4,854 | 13.6 − j89.4 Ω | 22.6 % |

The first two are a pure 1.6 pF capacitor, flat and monotonic across 1.55-1.90 GHz. **Doubling the
mesh and switching on conformal cells moved neither digit** — which is what excludes mesh coarseness
and leaves only "the conductor is not in the model". With the feed present the board resonates at
1.740 GHz against a cavity-model estimate of 1.734 GHz.

## 3. The fix, and why it is small

**The conversion already exists in the same project.** `LayoutClipper.ToClipperPaths(shape, tolDbu)`
is public and dispatches a `PathShape` to `PathOutlinePaths`, which flattens arcs through
`LayoutFlattener.FlattenOpenEdgeList` and offsets by `Width / 2` with the end type mapped from
`PathEndStyle` — all four styles, `Extended` included. DRC already relies on it. The extractor does
not have to know any of that.

So the branch becomes: a `PathShape` with `Width > 0` on a **conductor-bound** layer is outlined and
joins `conductorShapes` as ordinary artwork; `Width == 0` keeps the existing centreline path and its
existing sentence.

**Order matters.** The conductor binding is asked before the via binding today ("so a layer bound to
both … keeps behaving exactly as it did before MIM-1") and that must not change. Do the width test
inside the existing dispatch rather than ahead of it.

### 3a. The via-layer case is a separate decision, and it is the owner's

A width-bearing Path on a **via-bound** layer is how a routed, plated slot is drawn, and the same
geometric argument applies. But `ignoredViaPath` was given its own sentence deliberately in MIM-1,
and a via footprint carries a stackup span a stroke does not obviously imply.

**Recommendation: convert it too, by the same rule**, since the alternative is that the same shape
means copper on one layer and nothing on another. **If that is not taken, the sentence must change**
— it currently says a Path "is a centreline with no enclosed area", which is the false premise this
brief exists to remove. Do not leave a refusal standing on a reason that has just been shown wrong.

## 4. What must be reported

The note that exists today is the reason this went unnoticed for as long as it did — one line among
twenty, saying nothing about what was lost.

- **Say what was CONVERTED**, not only what was ignored: how many strokes became conductor artwork,
  and their total outlined area, so a user can see that the traces are in.
- **The ignored-Path sentence must stop claiming a stroke encloses no area.** After this phase it can
  only ever be about `Width == 0`, and it should say so.
- Arc-bearing strokes flatten at the layout's own tolerance. The extractor already emits a sentence
  about flattening curved shapes and about that tolerance being an artwork decision the mesh does not
  inherit — a converted stroke must be counted there, not silently.

## 5. What this disturbs

- **No recorded number can move.** There is **no `.clay` file anywhere in the repository** (checked:
  zero, excluding `bin`/`obj`), so every EM fixture is constructed in code and none of `HISTORY.md`'s
  measurements sit on a stroke. The risk is confined to a test that constructs a conductor-layer
  `PathShape` and then runs an extraction — `grep -rn "new PathShape" tests` is 88 hits across
  rendering, snapping, DXF and PCB export; confirm none of them reaches `PlanarExtractor` and say so
  in the write-up.
- **`EmSnpProvenance` geometry hash changes for any design containing a stroke** — correctly, because
  the answer changes. Every existing `.snp` beside such a design becomes stale and will be reported
  as such. That is right, and it should be *stated* in the write-up rather than discovered.
- The cell count rises on any board with tracks, because there is now metal where there was none.
  On the measured board that is the difference between 704,482 unknowns and — still 704,482, since
  the narrowest metal was already a connector land. ANT-2 is what makes that number usable; this
  brief does not have to.

## 6. Gates

- **The decisive one is an independently-authored oracle**: the same layout with the feed as a
  `PathShape` and as a hand-written `Rect` covering the same copper must extract to the same metal
  area and solve to the same Zin within the mesh's own tolerance. This is not a second circuitRF path
  agreeing with itself — the rectangle is written by hand from the stroke's endpoints and width.
- All four `PathEndStyle` values, on a conductor layer: `Flush` ends at the endpoint, `Round` and
  `Square`/`Extended` extend by `Width / 2`. Assert the extracted extent, not a picture.
- An arc-bearing stroke flattens and is counted in the flattening note.
- `Width == 0` is still ignored, still counted, and its sentence is the zero-width one.
- A stroke on a **ground-designated** conductor layer is still ignored as ground artwork — the
  conversion must not smuggle a ground pour into the mesh, which the extractor refuses by design.
- The report names the conversion count.
- `RESOLVED.md` write-up in `src/Design` (**not `CLAUDE.md`**). `src/Engine/Mom/CLAUDE.md` gains
  nothing here — this is not an engine change.

## 7. Must NOT

- **Do not write a second path-outlining routine.** `LayoutClipper` is the one, DRC uses it, and a
  second offsetter that disagrees at a mitre is a defect nobody can localise.
- **Do not silently convert zero-width Paths** to a hairline. A centreline with no width is a drawing
  aid and meshing it as metal invents copper.
- **Do not change the conductor-before-via dispatch order.**
- **Do not "fix" this by asking the user to convert paths to polygons in the editor.** The document is
  the contract; a format the solver reads differently from the renderer is the bug.

## 8. Reading order

`src/Design/Layout/Em/PlanarExtractor.cs` (the shape-classification loop, ~lines 196-250, and the
note-building block near line 826) · `src/Design/Layout/LayoutClipper.cs` (`ToClipperPaths`,
`PathOutlinePaths`) · `src/Design/Layout/LayoutModel.cs` (`PathShape`) ·
`src/Design/Layout/Interchange/GerberReader.Regions.cs:276` (why a track is a stroke).
