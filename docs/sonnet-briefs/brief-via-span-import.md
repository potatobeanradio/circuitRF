# Brief — importing a via SPAN, not just a via

**Status:** not built.
**Depends on:** `src/Design/Layout/ViaSpanResolver.cs` (built), which is the shared answer to "which
two conductors does this via join?" and is what the export half now reads.
**Scope:** the board-format read path. Smaller than it first looks — see §3, which corrects the
estimate this brief was first written with.

---

## 1. What is already true

A via's span is a PROCESS parameter on the technology's `StackupKind.Via` stackup entry
(`SpanFromLayer`/`SpanToLayer`, R-via-3), and the via's DRAWING LAYER selects the entry. One entry per
span, each bound to its own drawing layer, is how blind and buried vias are expressed. The shipped
`pcb-4layer_FR-4_62mil_1oz` technology already ships a blind stitching via beside its PTH; the shipped
MMIC technology ships three entries.

Every consumer reads that one place through `ViaSpanResolver`:

| Consumer | Reads the span |
|---|---|
| `DrcConnectivity.Extract` | yes, always did |
| `PlanarExtractor.BuildVias` | yes, always did — restricted to ADJACENT analysis levels, reported when not |
| `PcbWriter.WriteVia` | yes — writes the real pair plus the `blind` kind atom |
| `GdsiiWriter`/`DxfWriter`/`GerberExport` | yes — the PAD lands on the span's top conductor |
| `LayoutEditorViewModel` (Via tool) | yes — refuses a layer no via entry claims, auto-selects the sole one |
| Properties inspector | yes — read-only "Spans" row |
| **the board reader/importer** | **no — this brief** |

## 2. The gap, stated precisely

Two separate things, and only the first is about blind vias:

**(a) `PcbReader.ReadVia` discards the span it read.** It correctly IDENTIFIES a blind, buried or micro
via — from the kind atom, and from a layer pair that is not outer-to-outer — then places the via on its
top span layer and records a `Degraded` count. `specs[0]`/`specs[1]` are read and dropped.

**(b) `PcbStackupMapping.Build` never emits a `StackupKind.Via` entry at all.** Its `KindOf` maps only
`copper → Conductor` and `core`/`prepreg` → `Dielectric`; everything else is counted as ignored. So an
imported board's technology has ZERO via entries — which means **every imported via resolves no span,
through vias included**, not just blind ones. Re-exporting an imported board therefore writes every via
as an unspanned through via with a note (`PcbWriter`'s `UnspannedVias` counter), and an imported via's
pad has no copper layer to land on (`GerberExport`'s `UnspannedViaPads`).

(b) is the bigger defect and the one to fix first: it is the common case, it is not about blind vias,
and fixing it is what makes (a) expressible.

## 3. Why this is smaller than it looks — correcting an earlier estimate

The first draft of this brief claimed the hard part was grafting a synthesized via entry and its
drawing layer onto the destination technology, and named that as three problems. **Measured against the
code, the graft mechanism already exists and is already wired end to end, on both paths:**

- `PcbImport.ImportResult` already carries `IReadOnlyList<LayerDef> LayersToAdd` **and** `Stackup?`.
- `WorkspaceViewModel.ApplyImportToTechnology` already clones the destination technology, appends
  `LayersToAdd`, applies `Stackup`, and routes through an open `TechDocument` so an editor holding its
  own working copy cannot overwrite the change.
- `Cli/LayoutConvert.MintTechnology` does the same for the headless `convert` path, writing a `.ctech`
  beside the staged cells.
- The import already creates the via BARREL's drawing layer (`PcbReader.DrillLayerName`) and it already
  arrives in `LayersToAdd`.

So the pieces are all present. What is missing is that nothing constructs the via ENTRY that binds that
drill layer and names the span.

**The one real obstacle is the conservative stackup rule, and it is in both places:** each applies the
imported stackup only when the destination declares none (`clone.Stackup.Layers.Count == 0` /
`tech.Stackup.Layers.Count == 0`), otherwise warning and refusing. That rule is right for a whole
stackup — replacing a technology's substrate silently would be indefensible — but it is wrong for an
ADDITIVE via entry, which cannot invalidate anything already declared. Adding a via entry to a
non-empty stackup needs its own path, not the whole-stackup one.

## 4. Proposed shape

1. **`PcbReader.ReadVia` keeps the span.** Add the two reconciled source layer names to
   `PcbImportedShape` beside the existing `LandingLayerName` — the same route, already built for exactly
   this class of "not a shape layer" data. Do NOT try to store it on `ViaShape`; the span does not
   belong on the artwork.
2. **`PcbImport` groups vias by distinct span** and asks `PcbStackupMapping` (or a sibling) for one
   `StackupKind.Via` entry per distinct span, named for what it carries (`"Via Top-In1"`), each bound
   to its own drawing layer. One drill layer per span, allocated from the same key space
   `LayoutFragment.ApplyReconciliation` already allocates from, so two vias sharing a span share a layer.
   The board's vias are then drawn on the matching layer.
3. **A through-via span reuses an existing PTH entry** when the destination technology already has
   exactly one whose span is outer-to-outer. This is what stops every import accumulating a duplicate
   entry, and it is gate 3 below.
4. **Additive via entries apply even to a non-empty stackup.** Extend the `ImportResult` with the via
   entries as their own field rather than folding them into `Stackup`, so `ApplyImportToTechnology` and
   `MintTechnology` can append them without touching the whole-stackup refusal. Report what was added,
   in the same `Messages` list everything else is reported in.
5. **Declining is still a legitimate outcome on the GUI path.** If the import is landing in a technology
   the user does not want changed, today's behaviour — via on the top span layer, degraded count — must
   remain reachable, not become unreachable.

## 5. Gates

1. A board file with a blind via (Top → In1) imports to a via whose layer resolves, through
   `ViaSpanResolver.Resolve`, to a span of Top → In1. Assert through the resolver, not through a layer
   name, or the test passes on a coincidence.
2. **Round trip.** That import, re-exported, is byte-identical in its `(via …)` lines to the source
   file's. `tests/Ui.Tests/ViaSpanTests.AWrittenBlindVia_IsReadBackAsBlind_…` is the half that exists;
   this closes the cycle.
3. Importing a board whose vias are all through vias adds NO via entry when the destination technology
   already has exactly one outer-to-outer entry — the common case must not accumulate junk on every
   import.
4. Two vias with the same span produce ONE entry and ONE drawing layer, not two.
5. An imported board re-exported to Gerber reports `UnspannedViaPads == 0` — today it reports one per
   via, which is the measurable form of §2(b).
6. `tests/Ui.Tests/ConvertCliVerbTests.cs`'s 24-pair matrix still passes, byte-identity checks against
   the in-process exporters included.

## 6. Not in scope

- **Pads on intermediate layers.** `ViaShape` carries one `LandingLayer`, so a through via in a 4-layer
  board still cannot state a pad on every copper layer it passes. Separate model question.
- **The planar extractor's adjacency restriction.** A via spanning more than one dielectric gap is
  dropped by `BuildVias` with a note naming the remedy (one entry per gap). A solver limitation that is
  already reported, not a silent failure.
- **Microvia as a distinct kind.** The model has no field for it; `blind` is written for every
  non-through span and read back the same way. Adding one means a new field on the via entry and a
  reason to want it.

## On completion

Record findings in `src/Design/RESOLVED.md` (never in a `CLAUDE.md`).
