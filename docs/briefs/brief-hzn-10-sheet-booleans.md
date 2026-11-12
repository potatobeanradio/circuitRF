# HZN-10 — booleans of coplanar sheets: a `.c3d` capability, and the Hertzon examples it opens

Series: `brief-hzn-0-overview.md`. Needs HZN-2 and the HZN review (`src/Design/RESOLVED.md` §HZN, "HZN review").

## Why

A planar conductor is rarely one rectangle:
- a patch antenna has an inset notch cut out of it and a feed line joined to it;
- a stub filter is a through line with open stubs joined along it;
- a ground plane has cut-outs for an antenna's keep-out or a coplanar feed.

Hertzon draws each of these as rectangles combined by `Unite` and `Subtract`, every rectangle sized by design variables.

**A `.c3d` cannot hold that today.** A `.c3d` `Boolean` takes solids only: its operands go to the geometry worker, which builds solids.
So HZN-2 refuses a boolean whose blank or tool is a sheet. After the HZN review, that one item is all that stops these designs:
- the patch antenna example and its six mesher variants;
- the stub filter.

**Freezing the result to an `Outline` of numbers is not acceptable.** Every operand is sized by variables, and brief-hzn-0 D5 says a
dimension that is an expression opens and saves as that expression. A frozen outline would stop following `Wp` or `inset` and could not be
tuned or optimised.

The capability is useful to circuitRF's own users too, not just to Hertzon designs: a notched patch or a ground plane with a cut-out,
drawn in the 3D editor, with every dimension still a variable.

## What to build

### R-hzn10-1 — the format: a `Boolean` of coplanar sheets

- A `Boolean` whose `Blank` and every `Tool` are `Sheet`s is legal when all of them lie in **one plane**: the same `Plane`, and the same
  `Offset` once resolved. `Op` is `Unite`, `Subtract` or `Intersect`. `KeepTools` with `Subtract` keeps each tool as its own sheet, as it
  does for solids.
- Each operand keeps its own `Rect` or `Outline`/`Holes` and its own expressions. **Nothing is computed into the file.**
- The following are refused, in validation's words, naming the object and the operand:
  - a boolean that mixes sheets and solids;
  - operands in different planes, or at different offsets;
  - an operand whose placement moves it out of the plane.
- An operand's placement may translate it within the plane. Any other placement on an operand is refused for now; the boolean's own
  placement is unrestricted.
- **The result is a sheet**: it conducts, it takes the blank's material, role and `ThicknessUm`, and it has no faces. Every reader that
  asks "is this a sheet?" must answer yes for it. Audit them and list each in the completion note:
  - `C3dValidation`, including its sheet-material rule;
  - `GeometryKernelTree.CanHold`, which must stay false so the worker is never asked;
  - port and lumped-element landing (`C3dPorts` and its edge inference);
  - bounds and extents;
  - the hierarchy flattener;
  - `C3dToHzn` and `HznPatch`.
- `docs/design/c3d-format.md` §Operations: add a row. The `Boolean` row says what "coplanar" means, and §Faces says the result names none.

### R-hzn10-2 — evaluation: in circuitRF, exactly, with no worker

- At elaboration, `C3dLowering` resolves every operand to its polygon (outline plus holes, in DBU, in the plane's (u, v)) and combines them
  with the layout polygon booleans already in `src/Design/Layout` (integer coordinates, so exact).
- The result becomes the `C3dSheetGeometry` an ordinary sheet lowers to.
- A result of **several disjoint pieces** lowers to one `Em3dSheet` per piece. Each piece carries the object's provenance, so a field plot,
  a port's touch test and `explain` name the object, not the piece.
- An **empty result** is the refusal `C3dBooleans.EmptyResult` already words for solids.
- **No geometry worker, no OCC.** Such a sheet elaborates on a machine without the worker; a test says so.
- Every solver that consumes `Em3dSheet` (EMerge, Palace, openEMS) needs nothing new: assert this for EMerge and Palace with a dry run in
  the gate, not a solve.

### R-hzn10-3 — the 3D editor

- **The scene draws the result**: the lowered polygon with its holes, never the operands overlapped.
- **The tree shows the operands under the boolean**, as it does for solids, and selecting an operand edits its rectangle with its
  expressions.
- **Modeler ▸ Boolean ▸ Unite / Subtract / Intersect** accepts two or more selected coplanar sheets. When they are not coplanar, the command
  is disabled and its tooltip gives the reason.
- **Snapshots** (UiSnapshot `--c3d`), looked at and listed in the completion note:
  - a notched patch with its feed;
  - the operand selected in the tree;
  - the disabled command's tooltip.

### R-hzn10-4 — HZN: translate, save and export

- **HZN-2:** `Unite`, `Subtract` and `Intersect` on a sheet translate into R-hzn10-1's `Boolean` when the operands are coplanar rectangles or
  polylines; otherwise the existing item stays, with the reason. The tools' `consumed_by` rule is unchanged.
- A boundary assigned to the blank (the patch's Perfect E, a trace's two-sided Finite Conductivity) dresses the result.
- An assignment on a consumed tool is refused by name: it has no object left to sit on.
- **HZN-5:** an edited operand rewrites only that operand's creation-step props. Adding or removing an operand is refused with the reason,
  until the history writer can append steps.
- **HZN-4:** a sheet boolean is written as Hertzon writes one: the blank's `CreateRectangle`/`CreatePolyline` followed by one `Unite` or
  `Subtract` step per tool, each tool an object with `consumed_by`.

## Gates

- **`C3dSheetBooleanTests`**, one test per claim:
  - Unite of two overlapping rectangles is their union.
  - Subtract of a notch is a polygon with the notch.
  - Subtract of an interior rectangle is a polygon with a hole.
  - A disjoint result is two sheets with one provenance.
  - An empty result is refused.
  - Mixed sheets and solids are refused.
  - Different planes or offsets are refused.
  - It elaborates with no geometry worker.
  - Changing a variable moves the result (D5).
- **`HznCoverageTests`** under `CIRCUITRF_HERTZON_HOME`: `PatchAntenna` and its six mesher variants, and `StubFilter`, translate. That is
  12 of 58, up from the review's 4.
  - `Meta_Ex1` stays refused (its sheets are one-sided).
  - Record the new count and the next items in `src/Design/RESOLVED.md` §HZN.
- **Gate 1:** every newly opened design saves with no edit byte for byte (`EveryDesignUnderHertzonHomeThatOpens_SavesWithNoEditByteForByte`).
- **Gate 2:** a patch edited (`inset` changed) and saved, and a native notched patch exported, pass `hertzon-cli describe`. Skipped without
  an install.
- **Gate 4:** the patch's input match. Compare circuitRF's EMerge run with `hertzon-cli solve` on the patch at a few frequencies around
  its resonance, kept to minutes, with the tolerance HZN-7 recorded. Report the resonance from each. Any gap is a finding to record, not a
  reason to tune.

## Risk

Medium.
- **Low:** the polygon arithmetic. It is integer and already used by the layout code.
- **Medium, and the reason for the audit:** the number of readers that ask "is this a sheet?" and "what are its edges?". The likeliest
  miss is a port landing on a united trace finding the operand's edge instead of the result's.
- **Save:** HZN-5's history patching has only ever met a boolean of solids. Changing an operand's size must rewrite one prop string and
  nothing else.

## Rules

- **Tests:** one per claim, named classes only. No new `Category=Benchmark` test. No EM solve in a routine test; gate 4 is run by hand
  and its numbers recorded.
- **UI:** every UI piece is rendered with UiSnapshot and looked at.
- **Names:** no commercial tool, PDK or laminate names.
- **Clean room (brief-hzn-0 D1):** the meaning of a Hertzon step comes from its files, its documentation and black-box runs, never its
  source.
- **Findings** go in `src/Design/RESOLVED.md` §HZN.
