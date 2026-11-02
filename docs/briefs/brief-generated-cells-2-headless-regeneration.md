# Generated cells 2 — headless runs rebuild the artwork, or refuse

**Series:** generated cells (2 of 2) · **Tag:** `R-gc2-n` · **Phase:** architecture move + defect
**Area (moves):** `src/Ui/Layout/PCells/` → `src/Design/Layout/PCells/` — `GeneratedCellsLifecycle`,
`GeneratedCellStore`, `PCellRegistry`, `PCellGeometryCache`/`PCellGeometryHelpers`, the six microstrip
generators (`Mlin`, `MBend`, `MTee`, `MCross`, `MTaper`, `MKlopf`), `FootprintGeneratorResolver`, and
`Wire/` (the kit-PCell worker client: codec, frame, schema, transport, provider, resolver,
`KitPCellLibrary`, manifest, content hash, `PythonInterpreterDiscovery`, `PCellTrust`)
**Area (uses):** `src/Design/Layout/LayoutDesignFlatten.cs` and every headless caller of it —
`RailArtwork`, `LvsRun`/`LayoutRead*`, `TraceImpedanceAnalysis`, `GerberExport`, `BoardCompanions`,
`PlacedPins`, `src/Cli/Check.cs`, `render`, `em`, `convert`, `lvs`, `rail`, `impedance`, `serve`
**Depends on:** nothing · **Blocks:** nothing
**Rule:** no vendor, PDK, customer or reporter names in the code, tests or docs.

---

## 0. The gap

`ChipLandPatternGenerator` already lives in `src/Design` — footprint-1 put it there so the CLI "can
generate the same artwork with no display attached". **But nothing headless ever calls it at resolve
time.** The registry that maps a generator id to a generator, the store that writes a cell, and the
lifecycle that rebuilds missing or stale cells are all in `src/Ui`, so the CLI resolves a placed PCell
only if a GUI session already wrote its folder. When the folder is absent — an unpacked archive (brief
1), a `history clone`, a CI checkout (the workspace `.gitignore` excludes `.generated-cells/`), a
workspace nobody has opened in the GUI since a generator changed — `LayoutDesignFlatten` drops the
instance with a warning and every headless verb goes on to give a COMPLETE, PLAUSIBLE answer for a
board with parts missing: a railRF run with no land patterns, an LVS against missing devices, a Gerber
export with no pads, an EM run of the wrong metal. That is the failure mode this codebase refuses
everywhere else.

It is also the agent's path: the MCP server drives exactly these verbs, and it is out of process.

## 1. `R-gc2-1` — the move

Everything listed under Area moves to `src/Design` unchanged in behaviour. It is already framework-free
(`GeneratedCellsLifecycle` and `KitPCellLibrary` say so in their headers; confirm with
`tests/Firewall.Tests` — no Avalonia, no `Dispatcher`). Three things to watch, from earlier moves:

- **Preferences are ARGUMENTS** (`src/Design/CLAUDE.md`). The Python interpreter choice and the trust
  store are per-user state today; the moved code takes them as parameters, and `src/Ui` passes its
  preferences in. Headless there is no preferences file.
- **Embedded resources move with their class** (the `ShippedTechnologies` lesson: a class moved without
  its `EmbeddedResource` items enumerates nothing, silently). The Python worker package, if embedded,
  goes with `PCellPythonPackage`.
- **Process-wide registration** (`FootprintGeneratorResolver` is registered once per process) needs a
  headless registration point — the CLI's entry, not a GUI module initializer.

## 2. `R-gc2-2` — headless resolution regenerates

- A headless caller resolving a layout rebuilds any referenced generated cell that is missing or stale,
  through the same `GeneratedCellsLifecycle` pass the GUI's open uses — one path, so an open document
  and a CLI run can never generate different artwork.
- **Read-only verbs stay read-only.** `check`, `explain`, `render` and `lvs` promise to write nothing;
  they regenerate into an IN-MEMORY store for the run. `em`, `rail` and the run verbs may write the
  folder as the GUI would (writable workspaces only — SL2), and should, so the next run is free.
- **Kit PCells run the kit's Python only under trust.** A headless run never runs untrusted kit code
  silently: it uses the trust already recorded for that kit where a trust store is passed, otherwise a
  refusal naming the kit and the flag that grants it for this run (e.g. `--trust-kit <path>`). Decide
  the spelling with `docs/design/cli.md`'s rules and write it there.

## 3. `R-gc2-3` — what cannot be rebuilt is a refusal, not a warning

A referenced generated cell that is missing and cannot be regenerated (kit absent, no Python, generator
error, trust refused) makes a headless verb that USES the geometry fail — exit 1, the cell, the
generator id, and why — instead of the current warning-and-skip. `check` reports it as an error
finding. The GUI keeps its placeholder rendering (a user can see a placeholder; a number cannot show
one), but its Messages line should say the same sentence.

## 4. Gates

1. The round-9 shape, reproduced with circuitRF's own content: a board with placed `smt:` footprints,
   `.generated-cells` deleted → `circuitrf rail` / `render` / `lvs` produce the same result as with the
   folder present, and `check`/`render` wrote nothing to disk.
2. A kit-PCell layout with the folder deleted and no trust → refused with the kit named; with trust →
   identical artwork to the GUI's (byte-compare the regenerated cell folders).
3. `tests/Firewall.Tests` green; GUI behaviour unchanged (existing PCell/regeneration tests pass
   unmodified apart from namespaces).

## 5. Tests (minimal)

One per claim above, `--filter` on the PCell, lifecycle, footprint, CLI-verb and firewall classes —
never the whole of `Ui.Tests`.

## 6. On completion

`src/Design/RESOLVED.md` (and `src/Cli/RESOLVED.md` for the verb behaviour), never any `CLAUDE.md`;
`docs/design/cli.md` for the trust flag and the new refusal.
