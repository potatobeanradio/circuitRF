# DE-3 — Deembed in the schematic editor

Read `brief-de-0-overview.md` and `brief-de-2-deembed-model.md` first.

**What.** A user finds **Deembed** in the palette beside SnP, places it, picks a Touchstone file, and the symbol grows to the
file's port count, exactly as an SnP does. Extraction writes `Deembed:…`, and Simulate runs it.

## The work

- `SymbolKind.Deembed`, engine token `Deembed`, a `ComponentTypeRegistry` row next to SnP's. It sits in SnP's category, with
  D10's search terms and a one-line tooltip: *"Removes the network in a Touchstone file: the opposite of an SnP. Wire it as
  you would wire the SnP that adds it."*
- **Everywhere SnP and EM Field Data are treated as a Touchstone block, add Deembed.** These are the `IsSnpBox`,
  `IsDataFileBlock`, `SymbolKind.Snp or SymbolKind.EmFieldData` sites: file browse, `SetSnpFileCommand`'s port count from
  the file, `SnpPathPolicy` for the stored path, the variadic palette glyph, `SnpGlyph`, `NetExtractor`, `EditableSchematic`,
  `NetlistSchematic` (a `.cnl` drawn back as a `.csch`), `SymbolArtworkGenerator`. **Not** the sites that are about S entries
  (`ParameterRowViewModel.IsSnpEntryRow`, the yield panel's SnP rows, `ParameterEditorViewModel`'s entry list at ≈ line 2531)
  and not `EmCoSimNetlist`. D7 says a Deembed has no entries.
- Picking a file with an odd port count is refused in the editor, in the sentence DE-2's reader uses, and the symbol does not
  change.
- **Symbol.** The SnP box with ports 1…K down the left and K+1…2K down the right, numbered as in the file, so the two sides
  of D1 are visible. Mark it clearly as a removal, for example with "S⁻¹" in place of the SnP glyph, and keep it readable
  at 2 and at 6 ports. Check the symbol SnP draws for 2K ports first: if it already splits K/K, keep that.
- Hover / instance tooltip: the file name, the port count and "removes this network".

## Verification

- UiSnapshot PNGs, looked at: a 2-port and a 6-port Deembed placed beside an SnP of the same file, the palette with
  "deembed" typed in its search, and the parameter editor with a file picked. List them in the completion note.
- A test that a placed Deembed with a 4-port file extracts as `Deembed:X n1 n2 n3 n4 File="…" NumPorts=4`, and that a `.cnl`
  with one draws back as a `.csch` with the Deembed symbol (the `NetlistSchematic` round trip).
- `ExampleWorkspacesTests` and the schematic connectivity suite (`--filter Suite=SchematicConnectivity`), since both
  enumerate symbol kinds.

## Docs and agents

- `components.md` Deembed section (DE-2 wrote the netlist half): add the palette entry and a figure of the two-chain wiring
  rule.
- The MCP `reference` resource for components and the tool text that lists placeable types (`src/Cli/Serve/ToolCatalog.cs`
  and the reference sources it serves) list Deembed beside SnP.
