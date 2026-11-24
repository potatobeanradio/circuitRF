# Brief DDL-1 — the link model, resolved headlessly

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl1-<n>` · **Depends on:** nothing in the series
**Area:** `src/Render/DataDisplay/` (`Models/Marker.cs`, `Models/Trace.cs`, `Models/DataDisplayConfig.cs`,
`SliceTokenParser.cs`, `CubeTraceSpecParser.cs`, `TraceResolve`, `PlotConfigLoader.cs`, a new `Links/` folder),
`src/Cli/RenderDataDisplay.cs`, `src/Ui/DataDisplay/ViewModels/DataDisplayViewModel.cs` (`GetNextMarkerIndex`),
`docs/design/data-display.md` (new §2.10).

## The gap

A pinned slice axis holds an integer and nothing else (`AxisSlice.Index`). A marker has a `Guid Id`, but it is not
written to the `.cdd` (`MarkerConfig` has no `Id`). It is not carried by the copy constructor or by
`MarkerInfoBoxViewModel.ChangeToTrace`, which builds a new `Marker`. So nothing could point at a marker and still find
it after a save, an undo or a re-point.

## Requirements

**R-ddl1-1 — Marker identity.** `Marker.Id` is persisted (`MarkerConfig.Id`, a `Guid` string, always written; a file
without one gets a fresh id on load). The copy constructor carries it. `ChangeToTrace` carries it, so a marker moved to
another trace keeps its links. Anything that makes a genuinely new marker (paste, duplicate a plot or tab) assigns a
new id explicitly. That case is DDL-2's remapping, not an accident of the copy constructor.

**R-ddl1-2 — Document-wide marker names.** `GetNextMarkerIndex` takes the lowest `m<n>` free across every tab of the
display (it moves to `DisplayWindowViewModel`, which owns the tabs; each tab's `DataDisplayViewModel` asks it).
Existing duplicate names across tabs are left alone (DDL-0 D5).

**R-ddl1-3 — The link value.** A new `LinkValue` (in `src/Render/DataDisplay/Links/`) is what any source resolves to:
- `Coordinates`: for each axis the source's trace keeps (its X, and its family axis), the source path, the group, the
  axis name, the index, the value and its unit. A marker on a network (Touchstone) trace gives one coordinate on `freq`.
- `Position` (optional): a complex Γ or Z, with its plane and reference impedance, for a marker on a Smith or contour
  plot. It also records whether the marker is snapped to a sample. DDL-3 consumes it.
- `Scalar` (optional): a plain value with a unit, for controls (DDL-5).
- `Name`, and `Frozen` (true when the source is gone and this is the last value it gave).

A marker's coordinates come from the sample it sits on: `Trace.CubeMarkerIndex` for a cube trace, the curve index in
`PositionStatic.Y` for a family, and `Freq` for a network trace (see `src/Ui/DataDisplay/CLAUDE.md`, "cube markers
carry it in their POSITION"). Nothing re-derives a sample by comparing doubles with `==`.

**R-ddl1-4 — A pinned axis may be linked.** `AxisSlice` gains `string? Link` (the source id), and `AxisSliceConfig`
gains `Link` and `LinkName`. The name is for a person reading the file and for the dangling message; resolution uses
the id. `Index` keeps meaning "the index drawn", and is the frozen fallback (R-ddl1-7). An older circuitRF reading a
newer file ignores the new fields and draws the pinned index, which is the frozen picture.

**R-ddl1-5 — The `@name` token.** `SliceTokenParser` accepts `@name` as a pin token (`Pout[@m1, :]`,
`HB1.V["drain", :, @m2]`). Both `CubeTraceSpecParser` and `TraceExpression` take it through that one parser
(`src/Ui/DataDisplay/CLAUDE.md`, "Slice grammar — one parser"). The name is resolved to an id when the spec is
committed: in the consumer's own tab first, then the document. A name in two other tabs is refused, listing the tabs.
An unknown name is refused, naming the sources that exist. `BuildPickerExpression` writes a linked pin back as
`@name`.

**R-ddl1-6 — Matching (DDL-0 D3).** A linked pin resolves to:
1. the coordinate on an axis with the same name and the same length and values (the same run, or the same sweep
   written twice): its index;
2. else a coordinate whose unit is the consumer axis's unit: the consumer's sample nearest that value. A value
   outside the axis's range takes the end sample, and the trace's note says so ("m1 is 3.1 GHz; this axis ends at
   3 GHz");
3. else, for a source with a `Scalar` (a control), the same rule as 2 on its value;
4. else it is unresolved: drawn at its frozen index, with a note naming what the source supplies and what the axis is
   ("m1 gives freq (Hz); this axis is gridPoint").

Label axes (`node`, `branch`, `i`, `j`) cannot be linked in this series. Their pin rows keep their own pickers.

**R-ddl1-7 — Frozen.** A link whose source id is not in the document resolves to its stored `Index` with
`Frozen = true`. The trace's note says the source is gone and where it last was. The `.cdd` keeps the link and the
index, so the file reopens static until someone relinks it.

**R-ddl1-8 — Order and cycles.** A `LinkGraph` built per display has one node per trace and per source, with an edge
from a consumer to the trace its marker sits on. Resolution runs in topological order. A cycle (including a marker
whose own trace is pinned by that marker) is refused at commit, with the chain named ("Pout pinned by m1 on Gt,
pinned by m2 on Pout"). A file that already holds one resolves the cycle's members frozen, with that note.

**R-ddl1-9 — One resolution path.** `TraceResolve` resolves links before slicing, through the `IPlotDataSources` seam,
so `src/Ui` and `circuitrf render` produce the same picture. `PlotConfigLoader` restores `Link`. A test asserts
that the window's resolution and the CLI's agree for a linked `.cdd` (the RND-4 rule, `data-display.md` §2.9).

**R-ddl1-10 — `render --marker`.** `circuitrf render x.cdd` draws linked plots at the markers' saved positions.
`--marker m1=2.4GHz` (a value, nearest sample) or `--marker m1=#17` (an index on the marker's trace) moves a marker
first, for that render only, so an agent or a build can draw "the detail at this load" with no window. An unknown
marker name is a refusal that lists the markers. `docs/design/cli.md` records the flag.

**R-ddl1-11 — The design note.** `data-display.md` gains §2.10 "Links": the source/value/consumer model, the matching
rules, frozen, cycles and identity, written as decisions.

## Gates

1. **Same run, by index.** A loadpull `.npy` (`GammaLoad[gridPoint]`, `Pout[gridPoint, pinStep]`). Marker m1 on the
   Smith trace at sample 17. `Pout[@m1, :]` draws exactly `Pout[17, :]`.
2. **Another file, by value.** A marker at 2.4 GHz on one Touchstone file drives `freq` on a cube whose grid is offset:
   the nearest sample is taken. A marker on a `pinStep` axis linked to a `freq` axis is refused by unit, with the
   note.
3. **Frozen.** Delete m1 from the config and reload: the trace draws `Pout[17, :]`, `Frozen` is set, and the note names
   m1. The `.cdd` written back still holds `Link` and `Index = 17`.
4. **Cycle.** Two traces, each pinned by a marker on the other: the second commit is refused with the chain. A
   hand-written cyclic file loads with both frozen.
5. **Headless equals window.** One linked `.cdd`, resolved through `LibraryDataSources` and through `CddSources`:
   identical points. `render --marker m1=#5` produces the same picture as the window with m1 moved to sample 5.
6. **Identity.** Re-pointing m1 to another trace (`ChangeToTrace`) keeps its id, and the consumer follows it.

## Not here

The window's link picker, live follow, relink and paste remapping (DDL-2); positions on the Γ plane (DDL-3); contour and
expression frequency indices (DDL-4); controls (DDL-5).
