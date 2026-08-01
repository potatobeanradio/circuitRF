# Schematic — local conventions for `src/Ui/Schematic/`

## Imported kit parts: symbol reader + palette (2026-07-30)

**`DsnSymbolReader`** reads the record-based ASCII symbol-description format (`.dsn`) into a
`Symbol`. It reads the **format**, not any part — nothing in it is specific to a kit, and it must
stay that way. Two conversions are load-bearing and easy to get wrong silently:

- **Y is negated.** The file is Y-up; symbol local coords are Y-down. Because the flip is a
  *reflection*, it also reverses arc handedness — `BuildArc` negates both the start angle and the
  sweep. Getting this wrong still draws an arc, just a mirrored one, which survives review; there
  is a dedicated test (`PartialSweep_..._HandednessFlippedByTheYAxisReflection`).
- **Pins are snapped to P=100** after scaling, because `SymbolModel` requires it. Two pins that
  collide after snapping are both kept and **reported** — never silently merged.

Scale is a power of ten chosen from the file's own declared view bounding box (record `44`, falling
back to measured content), targeting a 300–30,000 local-unit extent — so a kit authored in a
different drawing unit still lands legible without the reader knowing anything about that kit. All
five real-kit files measured in hand resolve to scale 1.

**Text is anchored from the object's bounding box, deliberately NOT from the text record's own x/y**
— those are min-corner in some files and centre in others, distinguished only by an undocumented
flag. The box is unambiguous everywhere.

**Kit parts are held IN MEMORY, not installed as cell folders (2026-08-01).** `PdkPartInstaller.Install`
returns them; `PdkKitRegistry` holds them under `pdk://<kit>/<part>`; the workspace records only a reference
to the kit in `.cws`. The paragraph below explains why a kit part is an ordinary cell REFERENCE and that
reasoning is unchanged and still load-bearing — only the reference form moved from a relative path to a
virtual one, which is what lets everything downstream stay untouched. See `src/Ui/CLAUDE.md` for the full
note; read `<workspace>/pdk/<kit>/<part>/` below as history.

**`PdkPartInstaller` used to install kit parts as ordinary cells** (`<workspace>/pdk/<kit>/<part>/`), and
this is the whole reason kit parts need no new component species: a cell reference is *already* the
component whose artwork lives in an external file and resolves at render time, so placement,
rendering, pin geometry, hit-testing and the symbol editor all work on kit parts unchanged. Do not
add a parallel "external part" render path — it would duplicate all of that and drift.

**Two artworks, two jobs, on purpose:** the kit's `.bmp` browser icon is the palette tile
(`PaletteGlyphControl.IconPath`); the `.dsn` vector symbol is what goes on the schematic. Each is
used for what it was drawn for. A missing/undecodable icon falls back to the built-in glyph.

**A kit part is identified by kit+part id, never by `SymbolKind`** — every kit part shares one kind,
so an identity check on kind alone lights up every kit tile at once (`PaletteTool.ArmedFor`,
`PlacementService.Toggle(PaletteItem)`). There is a test for exactly that.

### An UNMODIFIED vendor kit is the case that must work — the importer sets up what it needs

A vendor kit is written for its own simulator and says nothing about circuitRF, so shipping no
`device-provider.json` is the ORDINARY case, not an omission. `PdkPartInstaller.SynthesiseProviderManifest`
writes one when the imported kit has none — otherwise every kit needs a hand-written file before
anything can be simulated, which is the setup step this exists to remove. It is written into the
WORKSPACE (the kit is read-only) as ordinary JSON: everything chosen automatically is visible and one
line to correct, which is what makes choosing automatically safe.

**Which library serves the kit is not written down anywhere, so it is established rather than read.**
A delivery is several read-only kits beside one shared library package; a part kit names its device
types but never says which library implements them — the vendor's simulator resolves them by name
across everything loaded. `DeviceLibraryDiscovery` closes that gap:

- **The types wanted** are the references a kit's netlists name but do not define — the same
  structural classification `BindNativeDeviceTypes` uses below, shared so the two cannot disagree.
- **A library is recognised by the entry points OUR worker will call** (`boot_senior_<TYPE>`). That is
  a fact about circuitRF's own worker ABI, not about any vendor, which is what keeps this free of kit
  knowledge.
- **A plain byte scan, not an ELF/PE/Mach-O parse.** An exported name sits verbatim in all three
  formats, so one scan handles the Linux, Windows and macOS builds a vendor ships side by side.
- **Search widens only when the narrower search finds nothing** — the imported folder first, then a
  bounded walk outward, because the library is routinely a SIBLING of the kit. Searching every level
  at once lets unrelated territory compete with what is sitting next to the kit; that was a real
  defect, caught by a full-suite run where two test fixtures under `/tmp` found each other.
- **Same file name = the same library built for many toolchains** (a real delivery ships 14). That is
  ordinary, so the most specifically named build wins and the choice is reported. Different file
  NAMES is genuine ambiguity — the choice would change which model evaluates the design — and is
  refused.
- **One search PER TARGET, not one for the host.** The manifest describes every platform at once, so
  the Windows entry must name the Windows build even when the import happens on a Mac. On macOS the
  worker runs inside the Linux VM circuitRF ships, so its target is Linux too.

**This only supplies the DEFAULT.** Which library an instance actually uses is the user's choice, in
the Parameter dialog's file-valued `ModelLibrary` row, which overrides it per instance by riding in
the provider name. Discovery exists so a freshly imported kit works before anyone opens that dialog.

Gate: 11 tests in `tests/Core.Tests/Devices/External/DeviceLibraryDiscoveryTests.cs` and 3 in
`PdkPartVariantTests`, over synthetic libraries (a file containing the entry-point name is all the
scan looks for). **Verified against the production kit**: importing it unmodified finds
`SIM_2025_linux_x86_64_GCC1210/RfPowerDesignKit.so` out of 14 builds in 62 ms — the same
library a hand-written manifest named — and the Windows entry gets the `win32_64_VS2022` DLL.

### A kit netlist names its own device types NATIVELY — they are bound to the kit's provider

A kit's cell instantiates exactly three kinds of thing: circuitRF primitives (`R`, `SnP`, `Chain`),
other cells the same kit defines, and its own compiled device models — written natively, e.g.
`KITLIB_DEVICE_v1:FET1 …`. The first two are recognisable, **so whatever is left is the third**,
and the kit has already said which provider evaluates it (`.ccell` `ExternalProvider`).
`NetExtractor.BindNativeDeviceTypes` rewrites those into ordinary `ExtDevice` instances before the
cells are copied into the library, so what lands there is already expressed in terms every
downstream layer understands. **Nothing here knows any type name** — the classification is
structural, which is what keeps it a kit-agnostic rule.

**A genuine mistake in the kit lands here too, and that is the better outcome.** A misspelled type,
or one whose definition sat in a sibling file that failed to read, is now refused by the provider —
which is the authority on what it serves — instead of by a bare "cell not found".

Everything but `Provider`/`Type` is forwarded verbatim, for the provider to match against the names
its own descriptor declares. The per-instance `ModelLibrary` override rides in the provider NAME via
`DeviceWorkerProviderResolver.ComposeOverride`, the same rule the leaf provider-backed path follows.
**Idempotent** — the netlist read is cached, so every instance of the part shares one `Cell` object,
and a rewrite that ran twice would nest `ExtDevice` inside itself.

**This is the AS-IS path, and it is not the only one.** A kit may instead ship a pre-translated
netlist whose devices are already written as `ExtDevice … Provider=… Type=…`; that is what a
`parts[].netlist` entry in an additions folder usually points at, and such a netlist passes through
here untouched. The rewrite exists so that importing a read-only vendor kit with no additions folder
still yields devices that resolve.

Gate: 4 tests in `tests/Ui.Tests/PdkPartVariantTests.cs` — the rewrite itself, parameters and nets
carried through, the two things it must NOT touch (a primitive and a sibling kit cell), and the
place-twice idempotence check. The first and last were confirmed to fail with the rewrite disabled.

### A provider-backed cell is a LEAF, not a hierarchy

`CcellFile.ExternalProvider`/`ExternalType` (both `WhenWritingNull`, so every existing `.ccell` is
byte-identical) mark a cell whose behaviour comes from a registered external device provider. Such a
cell has a symbol and **deliberately no schematic**.

`NetExtractor.TryEmitExternalDeviceInstance` is checked BEFORE `EmitCellInstance` and emits one
`ExtDevice` instance — `Provider=`/`Type=` from the `.ccell`, every other parameter forwarded
verbatim for the provider to match against its own descriptor. Returns null for an ordinary cell, so
the hierarchical path is untouched. `Provider`/`Type` on the instance are dropped, not merged: a
stray override must never shadow the cell's own identity.

**An unconnected pin is not an error here.** The engine's external-device mapping makes every node
its own ground-referenced port, so an open thermal terminal is ordinary and correct — it just gets
its own auto-named net. Do not add a "floating pin" conflict for these.

### Importing an "additions" folder — nothing is copied by hand

A supplier's kit is routinely read-only and often far too large to duplicate, so what circuitRF needs
for it — a manifest, a translated netlist — lives in **its own small folder that names the kit**:

```json
{ "provider": "…", "baseDirectory": "/path/to/the/read-only/kit", "workers": […], "parts": […] }
```

The user imports **that folder**. `PdkImporter` reads `baseDirectory` (already the field meaning "the
kit's own folder"), enumerates both, and records the kit in `PdkImportReport.KitRoot`; identical
relative paths resolve to the additions folder, which wins. Anything that later resolves an asset —
`PdkPartInstaller.Resolve`, `SubcircuitsIn` — must try **both** roots, or symbols come from one folder
while parameters silently come from neither.

**The kit name comes from the manifest's `provider`, not from the imported folder**, when that folder
is an additions folder — it may be called anything, and it is not the kit. Each installed cell records
`Provider = kitName` and a netlist asks for that name, so taking it from the folder would leave every
step working except the one that resolves the provider. An ordinary kit import is unchanged: its
folder name is its name.

**`baseDirectory` is written RELATIVE to the additions folder**, so the whole tree can be moved or
checked out anywhere. `DeviceWorkerManifest.TryRead` resolves it against the manifest's own folder;
the workspace copy records the resolved absolute path, because a relative one means nothing once the
file has been copied somewhere else.

**The import reports what settings it read** (`DescribeSimulationSettings`), and says so in BOTH
directions. Importing the kit itself rather than the folder that adds to it otherwise shows up three
steps later as a parameter that is not there — a bad way to learn it. A kit carrying nothing says so
and names the fix.

**The importer copies every netlist a part is defined by into `<workspace>/pdk/<kit>/`**, and the
manifest copy is written **before** the parts so each cell records its definition at the workspace's
own copy. The workspace is then self-contained — the imported folder can be moved or deleted and the
parts still build (there is a test that deletes it). The worker and the model libraries are NOT
copied: they are the kit's own, large, and `baseDirectory` is what keeps reaching them.

### The workspace's own `pdk/<kit>/` folder is where a kit's declarations are read from

A vendor kit is very often read-only, and duplicating one to add a file to it is not a workflow. So
**everything a kit does not itself carry — a manifest, a translated netlist — is dropped into the
folder circuitRF already made for it inside the workspace**, and picked up from there.

`PdkPartInstaller.LoadInstalled` (which runs at every workspace open, via `RestoreInstalledPdks`)
reads `<workspace>/pdk/<kit>/device-provider.json` and reconciles each installed `.ccell` against it:
declared choices, and where a part's circuit definition lives. `DeviceWorkerManifest.ResolveFile`
checks **the manifest's own folder before the kit's**, so a `.cnl` beside the dropped manifest is
found without the kit being touched.

**Reconcile at every open, not only at import** — otherwise picking up a file dropped beside a kit
would mean re-importing the kit, which is the step this exists to avoid. It also self-heals a moved
kit, since the recorded netlist path is absolute and re-resolved here.

**A value the user already chose survives.** Only the closed set of choices and the location of the
definition are refreshed; a declared default that has been changed deliberately is left alone.

**A part placed before the declarations arrived still gets them.** An instance's parameters are
seeded from the cell's interface at placement, so a cell that gains one afterwards would leave every
earlier instance without it — the ordinary case here, not an edge one.
`ParameterEditorViewModel.AdoptCellDeclaredParameters` tops the instance up at the cell's own
default when the editor opens: no undo entry (opening a dialog is not an edit) and never a change to
a value that is already there.

### A packaged part is a CIRCUIT, not a device

A worker evaluates one device. A packaged part is several of them plus the passives that connect
them — a subcircuit. So a kit can point a part at a `.cnl` holding its definition:

```json
"parts": [ { "id": "…_MODEL", "netlist": "circuitrf/package.cnl",
             "cell": "…_SPmodel_{ModelAs}" } ]
```

The installer records this on the cell (`ExternalNetlistPath`/`ExternalNetlistCell`, absolute —
the netlist stays with the kit while the cell is installed into the workspace).
`NetExtractor.TryEmitNetlistBackedCellInstance` reads the file, copies its cells into the run's
`Library`, and emits an **ordinary cell instance** — so elaboration, nets, sweeps and results treat
it exactly like a cell the user drew. The devices *inside* it still resolve to the worker.

**`{Param}` in the cell name is substituted from the instance**, which is how one placed part
resolves to one of several formulations.

**Circuit beats device.** This path is checked before the external-device path, and once a cell
declares a netlist definition, a failure there is **terminal for that instance** — it is never
quietly re-emitted as an `ExtDevice`. Falling back would answer with something the user did not
place. Two tests exist because the first implementation did exactly that.

**The whole file's cells are copied, not just the named one.** The definition instantiates others
beside it, and walking the dependency graph would re-derive what reading the file already told us.
A name already in the library wins — a design's own cell is never replaced by a kit's.

**The netlist's own functions and globals are merged into the testbench** (`NetlistImports`), because
the copied cells reference them by bare name. A name the design already uses is kept and the
collision reported: the design the user wrote wins over one a kit happened to ship.

**Only parameters the definition declares are forwarded.** A subcircuit handed a parameter it never
named is an elaborator error, and the kit's interface is the authority on which of a part's
parameters reach the circuit and which only pick *which* circuit it is.

**A terminal-count mismatch is refused, never guessed at.** The symbol and the subcircuit come from
the same kit, so their counts agreeing is the whole basis for binding pin *k* to port *k*.

### Parameter order in the dialog: which FILE, then which FORMULATION, then the values

`ParameterEditorViewModel.SetTarget` orders rows in three stable groups — file-valued
(`CcellParameter.IsFilePath`, declared by a kit as `fileParameters`), then choice-valued
(`Choices`), then everything else. That is the order the questions actually arrive in: the later
answers only mean anything once the earlier ones are settled, and it puts the two things a user of an
imported kit reaches for at the top rather than buried among a dozen numbers. Stable within each
group, so a kit's own ordering survives.

A file-valued row gets a **Browse… button** (`ParameterRowViewModel.PickFileAsync`, supplied by the
host — the picker itself stays in code-behind, per the UI firewall). A path is exactly the kind of
value nobody should be asked to type, and a mistyped one fails much later with a worse message. A
cancelled pick changes nothing.

### Model-selection parameters ("which formulation of this part?")

Some kits ship several formulations of one part and a parameter that picks between them. circuitRF
cannot work out which formulations exist, what the parameter is called, which one should be the
default, or which ones it can actually build — so **all four are declared as data**, in the same
`device-provider.json` the kit already uses to say what circuitRF cannot derive:

```json
"variants": [ { "parameter": "ModelAs", "choices": ["TYPEA","TYPEB"],
                "default": "TYPEA", "unsupported": ["TYPEB"] } ]
```

Nothing about any of those values is compiled into circuitRF. `DeviceWorkerVariant` reads them;
`PdkPartInstaller` writes each one into the part's `.ccell` as an ordinary declared parameter that
happens to carry `Choices`/`UnsupportedChoices`; `ParameterRowViewModel` shows a picker instead of a
text box for any parameter whose cell declares choices; `NetExtractor` refuses an instance set to an
unsupported choice, by name.

**The default is what makes the first run produce results** — a placed part arrives already on the
choice that works, so import → place → Run answers rather than explains.

**An unsupported choice is still offered.** Leaving it out of the picker reads as the kit missing
something; offering it and refusing by name at Run is information. The refusal is a real refusal —
substituting a supported formulation would answer for a different model than the one asked for.

**A variant parameter is never forwarded to the provider.** It selects WHICH implementation is
built; it is not a value the implementation takes, and a provider handed one would rightly reject it
as unknown.

**A variant is scoped to the parts it belongs to** (`DeviceWorkerVariant.Parts`; empty = all of them).
A kit's parts are not alike — the same folder holds real components and the pin-less helper cells they
are assembled from — so a formulation choice belonging to one part must not appear on the others.
Found by looking at a real workspace, not by inspection.

**A contradictory declaration is dropped whole**, not half-applied — no parameter name, fewer than
two choices, or a default that is not one of its own choices. A part offering a broken picker is
worse than a part offering none.

**The Parameter Editor needs no kit-specific surface.** A part's declared parameters are written as
the cell's published interface (`.ccell` `Parameters`), and cell placement already seeds instance
parameters from that — so the ordinary editor works on kit parts for free. Defaults are left BLANK
on purpose: the provider owns them, and a value invented at install time would silently override
whatever the kit itself specifies.


`SymbolKind.NonlinearC` + registry + glyph (brief-nonlinearc-symbol, 2026-06-19) — COMPLETE: Added `NonlinearC` to end of `SymbolKind` enum (`SchematicModel.cs`). 5 `ComponentTypeRegistry.cs` edits: `Registry` entry (`"NLC"` display name, `"C"` prefix, `Lumped`, not IsCommon), `EngineReference("NonlinearC")`, `DefaultParameters` seeding `C0=1pF`, `UserParamTemplate` for `C1,C2,…` (raw SI, `None` dimension, `FirstAddIndex=1`), `TryParseCode("NLC")`. `BuiltInSymbols.cs`: `_nonlinearC` cache field, `Primitives` case, `BuildNonlinearC()` (capacitor glyph + 3 diagonal slashes). `SymbolPortDefs.For` falls through to `default` (2-terminal vertical), no separate case needed. Updated 2 `LibraryCatalogTests` that hardcoded Lumped = R/L/C. 1 Engine integration test (`T1_ConstantC_NonlinearC_MatchesLinearCapacitor`). Build 0W/0E, 1901 total tests.



Read with root `CLAUDE.md` and `src/Ui/CLAUDE.md`.

## SDD placement defaults (brief-p1tone-num-sddx-defaults, 2026-06-17)

`ComponentTypeRegistry.DefaultParameters(SymbolKind.Sdd, portCount)` now returns:
- `NumPorts = portCount`
- One `I[x,0] = _vx/50` per port x ∈ [1, portCount] (`ShowOnSchematic = true`)

This means a freshly-placed SDD acts as N independent 50 Ω conductances without any user edits — it
can be run through S-parameter analysis immediately and produces physically meaningful results.

The notation `_vx` is the port-x voltage (`V(net[2x]) − V(net[2x+1])`) in SDD equation syntax. The
engine parses `I[x,0]` as a two-index port-x current at harmonic 0 (the DC/baseband member in HB;
the only current in S-param mode where the SDD is treated as linear).

**Do not change these defaults without also updating `ParameterEditorRegistryTests` and
`SddDefaultParamsTests`** — they are the gate tests for this behavior.

## P1Tone Num parameter and port-number pool (brief-p1tone-num-sddx-defaults, 2026-06-17)

`DefaultParameters(SymbolKind.P1Tone, 0)` now includes `Num` as the first parameter (before Pavl/Z/Freq/Phase).
`Num` is the S-parameter port index; it is auto-assigned at placement from the **shared Term + P1Tone pool**
(`NextFreeTermNum` scans both symbol kinds) so Term:T1 (Num=1) and P1Tone:P1 (Num=1) can never coexist
on the same testbench top level.

`CommitPlacement` and `CommitInlineEdit` both handle the P1Tone case, mirroring the Term case.
