# Vendor libraries — series overview: a manufacturer's part library in the palette

**Series:** Vendor libraries · **Tag:** `R-vl{n}-m` · **Briefs:** 1–5
**Area:** new `src/Design/VendorLibraries/`, `src/Design/Workspace/WorkspacePersistence.cs`,
`src/Design/Schematic/SpiceModelSymbolProvider.cs`, `SpiceModelPeek.cs`, `SpiceModelNetlist.cs`,
`NetExtractor.cs`, `src/Ui/ViewModels/Dock/PaletteTool.cs`, `src/Ui/Views/Palette/PaletteToolView.axaml`,
`src/Ui/ViewModels/ParameterEditorViewModel.SpiceModel.cs`, `src/Ui/Views/Dialogs/ManagePdksDialog.axaml(.cs)`,
`src/Ui/Schematic/PdkReferenceManager.cs`, `src/Ui/Archive/WorkspaceArchiveScanner.cs`,
`src/Ui/Theming/AppPreferences.cs`, `src/Cli/Authoring.cs`, `src/Cli/Find.cs`, `src/Cli/Check.cs`,
`src/Cli/Serve/ToolCatalog.cs`
**Found by:** a public feature request (2026-10-27) asking for component manufacturers' simulation
libraries.
**Rule:** no manufacturer, part number, series name, library file name, or other simulator is named
anywhere in the repo — code, comments, tests, fixtures, docs, commit messages. `pdk-import.md` §0 and
`spice-models.md` §0 apply unchanged. Fixtures are synthetic and made up. The request itself is not
linked or quoted.

---

## 0. What a manufacturer's library is

Passive-component manufacturers publish their simulation libraries for several simulators, as free
downloads. Each library comes in two kinds:

| Kind | What it is | Can circuitRF use it? |
|---|---|---|
| **Static** | One small R/L/C subcircuit per part, valid at one temperature, at 0 V / 0 A bias, small-signal, over a stated frequency range | **Yes.** The plain-SPICE editions are ordinary `.subckt` text, and circuitRF's SPICE reader already reads every part with no note and no refusal |
| **Dynamic** | The same parts with DC-bias and temperature dependence | **No.** Every edition is encrypted, each to one simulator's own key, and carries a "do not reverse engineer" notice |

The design-kit editions for schematic-capture tools keep the circuit topology in an encrypted binary,
even for the static parts. Only the plain-text SPICE editions are readable. The SPICE edition is a
folder tree. Folders group the parts (capacitors, RF inductors, general / automotive). Each `.lib` file
holds one series, and a series can run to many thousands of parts. A whole library is **~34,000 parts
in ~40 MB**.

Every part carries a comment header in front of its `.subckt`. The fields this series reads:

```
* Description : <size code>/<dielectric>(<temp range>)/<value>[<unit>]/<voltage>[V]   (capacitors)
* <...> P/N : <part number>
* Property : C = <value>[<unit>]          or   L = <value>[<unit>]
*   Frequency Range = <low>[<unit>]-<high>[<unit>]     (spellings vary: "50MHz - 20GHz")
*   Temperature = 25 degC
*   DC Bias Voltage = 0V           or   DC Bias Current = 0 A
.SUBCKT <part number> Port1 Port2
```

Inductor headers carry no `Description` line. Any header field may be missing, and a missing field is
never an error. **No code may decode a part number, except the tolerance letter of §1 of brief 5, which
is found by anchoring on the value code computed from the header's own stated value — never by a
position, a prefix or a series.** Every grouping and every searchable field comes from the folder tree,
the file stem and the header text.

The download terms forbid redistribution. **circuitRF never bundles, copies or archives a library
by default.** The user downloads it and circuitRF references it in place.

## 1. The briefs

| # | Brief | What it adds | Depends on |
|---|---|---|---|
| 1 | [Library reference and index](brief-vendorlib-1-reference-and-index.md) | the `.cws` reference, the `vendorlib://` part reference, the header index, Add / Manage, the per-user list, `import library`, `find --parts` | — |
| 2 | [The palette](brief-vendorlib-2-palette.md) | the Type menu with sub-menus, a virtualized part list, value-aware search, placing a part, the footprint from the part's case size | 1 |
| 3 | [The placed part](brief-vendorlib-3-placed-part.md) | the part picker with value stepping (the footprint following the part), one-block extraction, the frequency-range warning, archive, missing-library repair, `check` | 1, 2 |
| 4 | [Optimizing over real parts](brief-vendorlib-4-optimize-over-parts.md) | a placed part as an optimizer variable whose levels are real candidate parts; Discrete search, snap from an ideal proxy, Apply through the part change | 1–3 |
| 5 | [A part's tolerance](brief-vendorlib-5-tolerance.md) | the tolerance letter read from the part number by anchoring on the value code; the part's value as a statistical entry with `tol=part`; the `at <frequency>` header fix | 1–4 |

Build in order.

## 2. Decisions (owner, 2026-10-27; the defaults below were taken unless changed before the build starts)

- **D1 — Static only.** No dynamic model in this series, and no attempt to read an encrypted file.
  An encrypted file is recognised and reported (brief 1).
- **D2 — "Vendor Library" in the UI, one dialog.** **File ▸ Add Vendor Library…** is new. **File ▸
  Manage PDKs…** becomes **Manage Libraries…**, listing PDKs and vendor libraries with a Kind
  column. Ctrl/⌘+P is unchanged. The palette offers "Add Vendor Library…" too (brief 2).
- **D3 — The WORKSPACE is the truth, and a per-user list is the convenience.** A library is referenced
  in `.cws` (by name, like a PDK), so a headless run, `check` and a colleague all resolve it the same
  way. A per-user list in `AppPreferences` remembers the libraries a user has added. Each entry carries
  **Use in new workspaces** (on by default). New Workspace adds those entries, and a missing library
  whose name the list knows is repaired in one click.
- **D4 — Archive leaves a vendor library out by default.** One row per library, unticked, on the
  same terms as a kit.
- **D5 — No on-disk index cache.** The header scan is fast enough to run at every workspace open (brief
  1 measures it), which keeps `pdk-import.md` §9's rejected-cache decision intact. If the measurement
  says otherwise, stop and ask. Do not add a cache.

## 3. Rules that hold across the series

- **One SPICE path.** A vendor library part is placed as the existing `SymbolKind.SpiceModel`
  component, with its `File` naming the library through `vendorlib://`. It is translated by the
  existing `SubcircuitTranslation` / `SpiceModelNetlist`. No brief adds a second reader, a second
  translation or a new component kind.
- **The UI stays responsive at 34,000 parts.** This is the series' first acceptance criterion, not a
  polish item. Concretely:
  - opening a workspace never waits on an index
  - the palette realises only the rows on screen
  - a keystroke in the search box reads no file
  - selecting a placed part parses nothing
  - a run reads one `.subckt` block per distinct part

  **Every one of these is gated by a COUNTER, never by a timing test**, because timing tests measure
  the machine and flake. The timings themselves are measured once, with a scratch harness over a
  generated 35,000-part library, and recorded in `src/Design/RESOLVED.md`.
- **The test fixture is generated, never downloaded.** Each brief's tests write a synthetic library
  tree at test time: made-up part names (`TSTC0402A100`-shaped), the header shapes of §0, a few
  hundred parts. The responsiveness counters are size-independent, so a few hundred parts prove them.
  The 35,000-part measurement is a scratch harness, not a test.
- **Diagnostics are returned, never posted** (`src/Design/CLAUDE.md`). The GUI posts them to
  Messages, and the CLI writes them to stderr.
- **Every GUI operation has a headless spelling**, and both call the one function in `src/Design`
  (`src/Design/CLAUDE.md`, "an operation that lives only in a view model is not a capability").

## 4. Not in this series

- **Dynamic (bias- and temperature-dependent) models**, by any route. A user who needs one condition
  can export that condition as a plain static model from the manufacturer and place it as an ordinary
  SpiceModel today. The user docs say so in one sentence.
- **Reading a schematic-capture design-kit edition.** Its topology is encrypted (§0).
- **Optimising over a discrete list of real parts.** Brief 3's value stepping is the manual form, and
  an optimizer variable over "parts in this series" is [brief 4](brief-vendorlib-4-optimize-over-parts.md).
- **A `.cnl` spelling for a library part.** A headless design places one through a `.csch`, which
  every run verb accepts. If agents need the `.cnl` form, that is a follow-up.
- **Touchstone (`.sNp`) part libraries.** The same index could later list a folder of measured
  `.s2p` files, so keep the index's entry type free of SPICE-only assumptions where it costs nothing.
  Build none of it here.

## 5. On completion (every brief)

Findings go in `src/Design/RESOLVED.md`, with the UI half in `src/Ui/RESOLVED.md`. Never put them in
any `CLAUDE.md`. Update:

- `docs/user/src/reference/pdk-integration.md`: a Vendor libraries section, or a sibling page linked
  from it
- `docs/user/src/reference/components.md`: the palette
- `docs/user/src/reference/footprints.md`: a vendor part's footprint
- `docs/user/src/reference/cli.md`: `import library`, `find --parts`
- `docs/user/src/reference/file-formats.md`: the `.cws` key
- `docs/design/spice-models.md` and `docs/design/library-palette.md`: short sections pointing at the
  new code

Do not run DocGen; the owner regenerates at the end of the series. **Do not reply to the feature
request** until the series is built and committed; the owner will.
