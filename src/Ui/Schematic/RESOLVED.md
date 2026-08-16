# Schematic — resolved issues (see also `CLAUDE.md`)

Per-topic notes that don't belong in the standing `CLAUDE.md` file. Newest first.

## Library Palette: explicit "All" order, "All - Alphabetical", "Nonlinear" filter (2026-08-16)

Owner report: the "All" filter's order looked "random" — it was never random, it was
`LibraryCatalog.BuildAllItems()`'s category-rank-then-DisplayName sort (`CategorySortKey`), which
reads as arbitrary unless you know the category priority order. Three owner-requested changes:

- **`LibraryCatalog.AllItemsPinnedOrder()`** — the "All" filter now shows an explicit 22-row pin
  list first (`AllFilterPinnedOrder`, keyed by `(SymbolKind, PortCount)` because Snp/ZPort/Sdd share
  one Kind across several port-count entry points), then every remaining built-in in `AllItems`'s
  own order. `PaletteTool.ComputeRawItems` calls this instead of `LibraryCatalog.AllItems` for the
  `All` category; PDK parts are still appended after, unsorted, unchanged from before.
- **`LibraryCatalog.AllItemsAlphabetical()`** + `PaletteTool.WithPdkAlphabeticalByKit` — the new
  "All - Alphabetical" filter (`PaletteCategoryKind.AllAlphabetical`, listed directly under "All" in
  `BuildCategories`). Built-ins pure-alphabetical by DisplayName, then PDK parts grouped by kit (kit
  groups alphabetical, matching the kit list's own ordering elsewhere), alphabetical within each kit,
  never interleaved across kits.
- **`ComponentCategory.Nonlinear`** — a new Real-category filter. Deliberately an
  `ExtraCategories` membership on nine registry entries (NonlinearC, VerilogA, Diode, the 5 FETs, and
  the shared `Sdd` entry, which covers all of SDD/SDD1/SDD2/SDD3), never anyone's *primary* Category
  — so it changes nothing about where those items sort in `AllItems`/the pinned "All" order, it only
  adds one more filter that finds them.

**"VnTone" resolved to `ToneSource`.** The owner's pin list paired `PnTone` with a `VnTone` that
does not exist anywhere in the codebase — the actual single-tone voltage source is `SymbolKind.ToneSource`,
`DisplayName` "VTone" (no "n"; `EngineReference` is `V_1Tone`, which is likely where the "V1Tone"
naming came from). Confirmed with the owner directly — pinned row 14 is `ToneSource`.
