# Brief MIM-1 — region vias: drawn via artwork beyond the point `ViaShape`

**Problem.** `PlanarExtractor` turns exactly one shape kind into a `PlanarVia`: a `ViaShape` — an
(X, Y) point with pad/drill sizes, meshed as the equal-area square (side = 0.886 × drill). A MIM
capacitor's plate connection is a drawn REGION nearly as large as the plate itself, and today a
rectangle or polygon drawn on a layer bound to a `StackupKind.Via` entry is dropped into
`ignoredOther` with **no note at all** — the extractor's classification loop only recognises
via-bound layers inside its `ViaShape` branch, so region artwork on them misses `binding` (which
`BuildStack` builds from non-Via entries only) and vanishes silently. The same silence swallows a
drawn backside-via slot or bar.

**The engine needs nothing.** `PlanarVia.Polygons` is already an arbitrary polygon list; the mesher
resolves the footprint onto the shared tensor grid and makes one vertical basis per covered cell
that carries metal on both levels (no separate via mesh, no edge grading on a via footprint — see
`PlanarProblem.cs`'s own doc comment). The point-via path merely never *produces* more than one
small square. This brief is extraction + reporting + UI verification, and must not touch
`src/Engine`.

**The `.ctech` side also needs nothing built, only verified.** A Via stackup entry already binds a
drawing layer in the technology editor (`StackupLayerRowViewModel.ShowsDrawingLayerPicker` is
`Kind == Via`) and already states its span (`SelectedSpanFrom`/`SelectedSpanTo`). The layout editor
already draws rectangles/polygons on any layer. So "which drawing layer is the capacitor via on" is
answered exactly the way it is for point vias: by the Via entry's own binding. Confirm each of
those paths with a test rather than assuming; build nothing new unless one is genuinely missing.

Read first: `src/Design/Layout/Em/PlanarExtractor.cs` (the classification loop, `BuildVias`, and
the polygon conversion the conductor branch uses); `src/Engine/Mom/PlanarProblem.cs` (`PlanarVia`,
`GroundTerminal`); `src/Engine/Mom/CLAUDE.md` §7 (via refusals that must keep firing);
`tests/Ui.Tests/Layout/Em/` extractor tests; `ViaBasisTests` (engine oracles — read-only here).

## Milestones

1. **Region shapes on via-bound layers become `PlanarVia`s.** Every filled-region shape kind the
   conductor branch accepts becomes a footprint polygon (reuse the conductor path's own
   shape→`PlanarPolygon` conversion — outer ring plus holes; a `PathShape` stays ignored on via
   layers exactly as it is on conductor layers). Span, conductivity and the ground-terminal rule
   come from the stackup entry, identical to `BuildVias` today; a region via participates in the
   same `noSpan`/`notAdjacent`/`toGround`/`wrongGround` accounting. Multiple region shapes on one
   via layer are multiple footprint polygons.
2. **The silence becomes a sentence.** Whatever is still ignored on a via-bound layer is counted
   and reported in `notes` — the extractor's own R-em-4c pattern. Nothing on a via-bound layer may
   fall into `ignoredOther` any more.
3. **Point vias unchanged, provably.** A `ViaShape` still produces the equal-area square with
   byte-identical geometry — assert an existing point-via fixture's `PlanarVia` list is unchanged.
4. **UI verification pass.** One test per assumed path: a Via row's drawing-layer picker binds a
   layer; a rectangle drawn on that layer reaches the extractor; `EmDiagnostics`' via count
   includes region vias. If any path is missing, report it in the write-up and build the smallest
   version.

## Must NOT

- Touch `src/Engine` — via physics, the vertical basis, the mesh, and every §7 refusal stay as
  they are. A region via spanning non-adjacent analysis levels gets the existing `notAdjacent`
  note; a region via to a non-ground, non-level conductor gets `wrongGround`.
- Convert point `ViaShape`s into polygons "for uniformity" — the equal-area square is a documented
  modelling decision and existing runs must stay bit-identical.
- Add edge grading or extra gridlines for a via footprint beyond what its polygon outline already
  contributes to the tensor grid.

## Gates

Structural equivalence: a region via covering exactly the cells of an N×N array of touching
point-via squares must yield the same set of vertical basis functions (compare meshed via cells,
not S-parameters — this is a counter gate, not a timing one). New extractor tests for milestones
1–3; `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` green; `tests/Engine.Tests` untouched
and green. Write-up in `src/Design/RESOLVED.md`; correct the "point vias only" sentences in
`docs/design/mom-engine.md` (§10.12) and the user-facing EM reference in place, per the series
conventions.
