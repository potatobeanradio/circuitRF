# Core — local conventions

Built-in large-signal FET family (2026-08-02) — COMPLETE, palette included.
`src/Core/Devices/Fet/`: `CurticeQuadraticFetModel`, `CurticeCubicFetModel`, `StatzFetModel`,
`MaterkaFetModel`, `AngelovFetModel`, over a shared `FetModelBase`. Types `FET_Curtice`,
`FET_CurticeCubic`, `FET_Statz`, `FET_Materka`, `FET_Angelov`.

**Each model is its own type with its own parameter set, deliberately — they are not variants.**
Several use the same spelling for different quantities: `Beta` is a transconductance parameter in the
quadratic law and a gate-voltage-shift-with-drain-bias in the cubic one. One shared parameter block
would silently mis-feed whichever model the user did not have in mind, and the result would simulate.

- **Three nets, two ports.** The user draws `gate drain source`; the model is (gate,source) and
  (drain,source), so `v[0]` is Vgs and `v[1]` is Vds — the coordinates every published FET equation
  is written in. `Elaborator` expands the three declared nets into the four the port pairs need,
  the same mechanism `Tuner`/`P1Tone`/`Diode` use.
- **Derivatives are ANALYTIC, not finite-differenced.** A finite-difference Jacobian inside a Newton
  loop costs an extra evaluation per entry and is least accurate exactly where the device is most
  nonlinear. The gate test compares every model's `gm` and `gds` against a central difference over a
  25-point bias grid, and names the model in the failure message — a bare tolerance failure would not
  say which law is wrong.
- **Below pinch-off the current AND both derivatives are exactly zero.** A fudge conductance would
  put current where there is none; the DC engine's own gmin already keeps the node solvable.
- **`Cgd` bridges gate and drain, so in (Vgs,Vds) coordinates it appears on BOTH ports and in the
  off-diagonals of `Dc`.** Dropping those cross terms is the classic plausible-but-wrong Jacobian;
  there is a test that checks all four entries and the charge consistent with them.
- **The Statz knee is continuous in value and slope by construction** at `Vds = 3/Alpha` — the
  cubic's derivative is zero there — so no smoothing is applied and none is needed.

- **Gate charge is SELECTABLE, because the published laws differ on it** — `CapModel` picks:
  `0` none, `1` constant `Cgs`/`Cgd` (default), `2` bias-dependent junction charge
  (`Cgs`, `Cgd`, `Vbi`, `Mj`, `Fc`), the standard depletion form applied to Vgs and Vgd separately
  and continued by its tangent above `Fc·Vbi`. Hardcoding one scheme would have been wrong for
  whichever models use the other.
- **The SOURCE IS AN INDEPENDENT TERMINAL.** Writing the law in (Vgs, Vds) is a coordinate choice,
  not a common-source restriction: the source is an ordinary net the user wires anywhere. A gate
  test lifts the whole device off ground and shows only the differences matter, and an elaboration
  test builds a source-degenerated stage — the configuration a common-source-only device could not
  represent at all.
- **Temperature dependence is modelled, in the published forms**, which are NOT all the same shape:
  `Beta` and `Alpha` scale as `1.01^(tc·ΔT)` (their coefficients are *percent* per degree), `Gamma`
  as `1 + tc·ΔT` (a plain fraction), and `Vto` shifts additively as `Vto + Vtotc·ΔT` (volts per
  degree). Confusing the first two costs ~4 % at ΔT = 100, which is well inside the range these
  exist for, so there is a test that asserts the exponential form *specifically* — and first asserts
  the two candidate forms actually differ at the tested point, so the check cannot pass vacuously.
  Shared with them: `Vbi` falls with temperature, `Cgs`/`Cgd` follow it, and the gate saturation
  current rises via `Xti`/`Eg`.
- **A model accepts only the coefficients whose parameters it HAS.** `CurticeCubic` takes `Gammatc`
  and no `Betatc`, because its `Beta` is not a transconductance parameter; `Materka` and `Angelov`
  take no `Betatc` at all. Mapping `Betatc` onto whatever each law happens to use as its current
  scale would be fitting, not implementing.
- **`Temp` and `Tnom` are DEGREES CELSIUS everywhere**, including `Diode` — whose model constructor
  still takes kelvin, with the conversion at the factory boundary where it belongs. Two components
  in one palette must never read the same parameter name in different units. Both default to the
  same value, so a device with no stated temperature is evaluated exactly at its extraction point
  and every relation collapses to the identity; a gate test asserts that collapse is EXACT, with
  large coefficients supplied deliberately, because that is what catches a °C/K mix-up.

**NOT modelled:** the Statz/TOM-family charge formulation. It is a different scheme, not a parameter
change — it works on a *smoothed effective voltage* (`Veff = ½{Vgs + Vgd + √((Vgd−Vgs)² + Vδ1²)}`,
then a second smoothing against `Vto`) rather than on Vgs and Vgd separately, so it needs its own
implementation and its own derivatives. Also absent: transit-time delay, breakdown, self-heating.

Gate: 23 tests in `tests/Core.Tests/Devices/FetModelTests.cs`, 8 in
`tests/Core.Tests/Elaboration/DeviceNodeExpansionTests.cs`, 15 in
`tests/Ui.Tests/DevicePaletteWiringTests.cs`.

**Palette exposure — see `src/Ui/CLAUDE.md` for the wiring.** All six are placeable, editable in the
Component Parameter dialog, and under a new `Devices` palette category.

Junction diode primitive (2026-08-02) — COMPLETE. `src/Core/Devices/DiodeModel.cs`, type `Diode`.

Standard exponential I-V with depletion and diffusion charge, optional reverse breakdown, and
optional series resistance. Parameters are the conventional ones — `Is`, `N`, `Rs`, `Cj0`, `Vj`,
`M`, `Fc`, `Bv`, `Ibv`, `Tt`, `Temp` — all optional, each defaulting to its usual value.

- **The equations are the standard ones**: exponential I-V, depletion charge
  `Cj0·Vj/(1−M)·(1−(1−V/Vj)^(1−M))`, diffusion charge `Tt·I`, optional reverse breakdown.
- **Both runaway regions are continued by their TANGENT, not clamped** — the exponential above
  `40·N·Vt`, the depletion charge above `Fc·Vj`. Value *and* slope stay continuous, which is what
  keeps Newton convergent; a clamp keeps the value finite and puts a kink in the Jacobian, which
  stalls the solve in a way that looks like a bad circuit. Two gate tests straddle each changeover
  and assert continuity of both.
- **`Gmin` defaults to ZERO, unlike SPICE.** The DC engine already adds `gmin = 1e-12 S` to every
  voltage node, so a device supplying its own doubles it exactly where it matters. Caught by a test
  that expected the closed-form current and got the leak term as well.
- **`Bv = 0` means "breakdown not modelled", never "breaks down at 0 V".** The wrong reading makes
  every reverse-biased diode conduct hugely and is silent; there is a test for it.
- **Series resistance is INSIDE the model, on a real internal node** — a separate resistor beside
  every diode is not required and would not scale to a part built from many of them. With `Rs > 0`
  the device is a two-port over three nets, `anode — internal — cathode`; `Elaborator` mints the
  internal node exactly as it does for `Tuner`/`P1Tone`/`ExtDevice`.
- **That internal node is a genuine unknown, NOT collapsed locally.** Solving `(V−Vj)/Rs = I(Vj)`
  inside `Evaluate` is exact at DC and **wrong in HB**, where the internal node carries its own
  harmonic content — at RF the junction capacitance shunts `Rs`, and a quasi-static collapse cannot
  represent that. Same reasoning as the `ExtDevice` internal nodes. `Rs = 0` stays a one-port, so a
  diode without series resistance costs no extra unknown.

**The oracles are the closed-form equations and finite differences, not stored numbers from another
simulator** — a golden file from elsewhere would only prove two implementations agree. `dQ/dV` is
checked against the returned capacitance by central difference, which is the real consistency check
between the charge and its derivative.

Gate: 23 tests in `tests/Core.Tests/Devices/DiodeModelTests.cs`. The series-resistance tests
solve the two-port pair by bisection and assert the internal-node KCL directly — both ports
carrying the same current at balance, and Ohm's law closing the loop across the resistor.
Core.Tests 920 pass.

A kit's symbols may live in a LIBRARY, not one file per part (2026-08-01) — COMPLETE for reading and
binding. `src/Core/Pdk/KitSymbolLibraryReader.cs`, bound to parts in `PdkImporter.DiscoverParts`.

**Why this shape matters.** Every symbol reader beside this one takes one drawing per file. A library
inverts it: many parts share a handful of templates and each part names the one it wants. Measured on
a kit — **7 templates serving 109 parts, in about four kilobytes**. So reading one small file is
what makes a whole kit placeable, and those same seven templates are what the palette should show.

- **Read best-effort and deliberately partial**, the same bargain `KitSymbolDefinitionReader` strikes:
  only the records whose layout is unambiguous — the symbol names and the terminals with their
  positions. The drawn body is not read, so a part gets correct, correctly-named pins and a body
  circuitRF draws itself. Pins decide whether a part can be WIRED; the rest is appearance.
- **A symbol library is its own asset kind, not symbol artwork.** It is not one part's drawing:
  matching it to a part by file name finds nothing, and counting it as unreadable artwork would warn
  about a file that reads perfectly.
- **The symbol outranks a name-matched subcircuit on terminal count.** The kit DREW it, and a part
  naming one is stating how many pins it has.
- **Keyed by extension, not content** — the payload is binary and `PeekText` returns nothing for
  anything containing a NUL, by design.

**Two bugs found by running it against a real library rather than by inspection**, both silent:

1. **Record delimiters were being read as part of the pin name.** Records are bracket-framed, so a
   name sits hard against its own record's terminator and the next one's opener; taking every
   printable byte read a pin called `1` as `1][`. No crash, nothing obviously wrong in a dump — every
   pin on every symbol renamed.
2. **A kit's catalog and its own symbol library can spell the same symbol differently.** One
   references `A_B` for a symbol it declares as `A B`. Matching only exactly cost **18 of 109 parts**
   their pins, silently, since a pinless part still imports and still appears. Now matched
   separator-insensitively through the same `Normalize` already used for artwork.

**Result on that kit: 109 of 109 parts carry pins** — 2-pin ×16, 3-pin ×69, 4-pin ×24, matching the
families exactly, and matching the pin counts derived independently from the network data's own port
labels.

**`PdkPart.Pins` carries the template outward**, and the Ui half is now done too —
`src/Ui/Schematic/KitTemplateSymbol.cs` builds a placeable symbol from it, tried only after a
per-part drawing so a part that has its own keeps it. **End to end on that kit: 109 parts → 109
palette entries → 109 symbols installed, 0 omitted.** Before it, every one of them was dropped from
the palette as unplaceable.

- **The pins are the kit's; the body is circuitRF's own.** The library states terminals, not artwork.
  Drawing a body we cannot read would be inventing the kit's drawing; drawing none would leave pins
  floating with nothing to click.
- **Placement SHARES the drawing reader's scale, snap and axis flip** rather than reimplementing
  them (`DsnSymbolReader.PinGrid`/`SnapToPinGrid`/`ChooseScale`, widened to `internal`). Pins must
  land on exact multiples of the connection grid or a wire will not attach, and two rules for that
  would drift at the first change. It also means library-backed and drawing-backed parts put their
  pins in the same places.
- **A two-terminal part still gets a body.** Its pins are colinear, so one dimension of their
  bounding box is zero; without a floor the body collapses to a line nobody can see or click.
- **`MakeKitPart` is shared by both symbol sources**, because everything it does decides what a
  PLACED instance is — port count, provider binding, parameter interface — and two copies would
  drift the moment one changed.
- **`DsnSymbolReader.TranslationVersion` was deliberately NOT bumped.** Its rule is to bump whenever
  a change could MOVE a pin, because a workspace records the version its kits were translated under
  and moved pins silently disconnect wires. Nothing here moves a pin on the drawing path — the two
  helpers were only widened to `internal` — and the template path is new, so no workspace has one
  recorded. **It does now govern this path too**: a future change to `KitTemplateSymbol`'s scale,
  snap, body or ordering moves pins on library-backed parts and needs the bump.

Gate: `tests/Core.Tests/Pdk/KitSymbolLibraryReaderTests.cs` (10) and 5 binding tests in
`ComponentCatalogDiscoveryTests` — all synthetic, including a regression for each bug above.

A kit may DECLARE its parts in a catalog (2026-08-01) — COMPLETE.
`ComponentCatalogRecognizer` + a catalog pass in `PdkImporter.DiscoverParts`.

**The gap this closes, measured before and after.** `DiscoverParts` knew two shapes: a
`<cell>/<view>/<file>` database tree, or the subcircuits a netlist declares. A third exists — a kit
that lists its parts in an XML catalog (name, model, symbol, description; one entry per part) and
supplies their behaviour from a **compiled model library** rather than any netlist. Such a kit has
nothing to walk and nothing to parse, so it imported as a pile of recognised files with an **empty
palette**. Measured of that shape: **0 parts → 109**, grouped by catalog file.

- **Recognised STRUCTURALLY, never by a schema URI or namespace.** A namespace names the tool that
  wrote the file; keying off one would put supplier knowledge in circuitRF and would fail on the next
  kit with the same shape. A test proves a namespace-prefixed catalog reads.
- **The discriminator is an element that NAMES a part**, not the number of times the word appears.
  Counting mentions was tried first and wrongly rejected a catalog offering a single part — the rule
  was wrong, not the fixture. A document that merely *talks about* components is still not a catalog.
- **The identity taken is the MODEL name, falling back to the declared name.** Real catalogs disagree
  between the two: an entry named for a package variant can point at a shared model. Taking the
  display name would name a part nothing can resolve at run time.
- **Parsed by regex, not an XML parser** — deliberately. These files carry a default namespace, so
  element lookups would have to be qualified against a URI this code must not know. Matching the
  local name reads both spellings.
- **The catalog file name is the part's category.** A kit of this shape splits its catalog by family,
  one file each; it is the only grouping offered and it is a real one.
- **The catalog wins over the other two shapes** when present — it is the kit *saying* what it
  offers, rather than circuitRF inferring it from which files happen to be lying around.

**A zero-part import now ALWAYS says why**, and this is the half that matters most. Recognising files
and reporting nothing leaves the user with an empty palette and no reason for it, which satisfies
neither of the two distinct messages the three `PdkAssetSupport` states exist to keep apart. The
reason names which shape was found — a catalog that declared nothing readable, netlists whose
subcircuits the kit never draws, drawings not arranged as a cell database, or no declaration at all —
because those need different fixes.

**A compiled model library (`.dll`/`.so`/`.dylib`) is now recognised as model data.** circuitRF never
loads one into its own process; a device provider runs it out of process. Recognising it is what
turns "the parts do not simulate" into a message at IMPORT time — the existing provider warning now
fires — instead of a failure at Run.

Gate: `tests/Core.Tests/Pdk/ComponentCatalogDiscoveryTests.cs` (12, synthetic fixtures — the repo
commits no kit data). Core.Tests 883 pass, Firewall 4 pass.

An extracted network says where its externally-connectable ports stop — sometimes (2026-08-01) —
COMPLETE for the reading half. `src/Core/Pdk/TouchstonePortLabels.cs`.

**The problem this solves.** A kit part can be delivered as nothing but a Touchstone file, and that
file routinely declares **more ports than the part has pins**: it was extracted from a physical
structure with an opening left at every place a lumped component attaches. Stamping all of them as
pins builds a different circuit — and it still simulates, which is what makes it dangerous.

**Solvers commonly write `! Port[k] = <name>` alongside the data**, naming each port after the
geometry it sits on. That is a property of the FORMAT and of how such tools emit it; nothing here
knows anything about any supplier, and nothing may. RfCore already preserves comments
(`SNP.Comments`), so no file-reader change was needed.

- **The split is decided only by the file's own structure**: ports are external until the first port
  belonging to a **multi-terminal object**, since an object carrying several terminals is a place
  components attach, not a pin. A label's *group* is itself with a terminal suffix removed — both
  `name:1` (modal) and `name_T3` (terminal) are stripped, so a single-terminal port is its own group
  and is therefore distinguishable from a shared one.
- **When structure does not decide, the answer is `Ambiguous` — never a plausible-looking number.**
  Two real shapes hit this: every port naming a different object (a structure whose attachment
  points are one port each, far side grounded), and a multi-terminal object starting at port 1. Both
  are reported with a reason. A wrong split is silent, so "I cannot tell" is the only safe answer and
  the caller supplies the split as run-time data — the same rule as everything else in this area.
- **Labels are NOT necessarily near the top of the file.** One real file declares them at line 226,
  after a long variables block. Read them off the parsed network, never off a capped read of the text
  — a probe written the other way reported "no labels" for a file that has fourteen.

**Gate — and the oracle is exact, needing no reference simulator.** S-parameters are *defined* with
every other port terminated in the reference impedance, so terminating the attachment ports in Z0 and
measuring the external ones must reproduce the corresponding sub-block of the file's own matrix,
entry for entry. That is a real check on **port order**, the **reference-node rule** and the
**Z-expansion** — the three things most likely to be quietly wrong.
`tests/Core.Tests/Pdk/TouchstonePortLabelsTests.cs` (17) and
`tests/Engine.Tests/Linear/ExtractedNetworkExternalPortsTests.cs` (2, synthesised fixtures — the repo
commits no kit data). The second of those two is the guard that gives the gate teeth: leaving the
attachment ports **open** must change the answer, or a stamp that ignored them entirely would pass.

**Verified against kit data by disposable probe** (deleted after running), across four families
and port counts 6 to 27: the external sub-block reproduces to **1.7e-13 … 2.7e-11**, i.e. machine
precision. Two files correctly reported `Ambiguous` rather than guessing.

**NOT done here, and deliberately:** nothing yet turns such a file into a placeable part. That needs
the split as run-time data for the ambiguous cases, plus the lumped devices that attach at the
remaining ports — see the standing rule that a kit's own facts are data beside the kit, never
knowledge compiled in.

The provider-unavailable message names the usual cause (2026-08-01) — COMPLETE. `ExternalDeviceRegistry.Require`
now says the model is **usually a compiled one whose implementing library was not found**, that such a
library often ships as a separate package beside the kit, and what to do about it (reference the package
in File ▸ Manage PDKs, or supply a `device-provider.json`). Everything the message already said is kept.

**Named as the USUAL cause, not as a diagnosis.** This layer knows only that nothing answered to the name;
asserting the library is missing would be over-claiming. Leaving it out, though, sends the user looking
for a missing provider REGISTRATION when what is actually missing is a file on disk — which is the wrong
half of the system entirely.

**`DeviceWorkerProviderResolver.Describe`'s empty case now says what it MEANS.** It read "no kit folders",
which is literally true and tells a user nothing; it now reads "no kit in this workspace settled on a way
to evaluate its devices" — the reason there are none being the interesting part. The
nothing-was-ever-registered case is still worded separately, because it is a different situation with a
different fix.

Gate: `ProviderUnavailableMessageTests` (4).

Compiled device models on Windows (brief-windows-device-worker.md, 2026-07-31) — COMPLETE, with the
one gate that needs a Windows machine still open. The worker now builds and runs for Windows, and a
synthesised manifest names `win-x64` again.

**The Windows and Linux builds need the SAME 15 functions** — not a similar set, the same set, in
two manglings (`_ZN15DeviceInstallerC1EPKcPFivEi` vs `??0DeviceInstaller@@QEAA@QEBDP6AHXZH@Z` for
the fifteenth). There was no new ABI to work out and no vendor header to obtain.

**The one real problem was that a Windows model IMPORTS its host callbacks from a NAMED MODULE.** A
Linux model leaves them undefined and the loader resolves them against whatever loaded it (that is
what `-rdynamic` is for); an executable's exports are never consulted for a DLL's import-by-name, so
a module under that name has to exist at load time. Hence two products from one source file:
`crf-model-host.dll` holds the callbacks, the protocol and `crf_worker_main`; `senior_worker.exe` is
a launcher that derives the name, stages the DLL under it, loads it and calls in. **The logic is in
the DLL because the callbacks are not pure** — they write worker state, and splitting them from that
state would need a registration handshake and a forwarding thunk per callback.

- **The module name is read out of the model, never remembered** (`PeImports.ModuleSupplying`, and
  `derive_host_module` in the worker). The import descriptor is selected by matching **our own ABI
  symbols**; matching a remembered module name would put kit knowledge back in one string at a time
  and would silently serve nothing for a kit naming its host module differently. This is the third
  instance of the principle already load-bearing here — the ELF symbol-table scan instead of a
  compiled-in name list, the runtime alias map instead of a compiled-in table.
- **Two implementations of that walk exist, deliberately.** The C one runs inside the launcher,
  which has to do it before any managed code exists in its process; the C# one lets the RULE be
  exercised on every platform and lets the importer say whether a kit's Windows build is drivable.
  Keep them in step.
- **The staged shim is loaded EXPLICITLY, before the model** — Windows resolves an import by first
  checking whether a module with that base name is already loaded, so no `SetDllDirectory`, no
  `AddDllDirectory`, no `PATH` edit. The search-path approaches work by accident of ordering.
- **The staged copy is per-user, never the repo/install/kit** (`%LOCALAPPDATA%\circuitRF\hostshim\`).
  The file bearing the vendor's module name is created on the user's machine, from their own kit.

**`DeviceLibraryDiscovery.Find` gained a `LibraryFormat` filter, and it fixed a latent bug.** The
`preferPathContaining` hints only ever RANKED; they never filtered. So a kit shipping a single
library answered both a "find the Linux build" and a "find the Windows build" search with the same
file — harmless only while the `win-x64` entry was suppressed, and a launch failure the moment it was
written back. The filter is decided by the file's own **magic bytes**, not its extension: a vendor
names its folders for the toolchain and its files for whatever it likes, and the magic is the one
property that cannot be a naming convention.

**`DeviceLibraryDiscovery.ServedTypes` needed no change** — its plain byte scan is format-agnostic by
design, and that design is now paid off rather than merely claimed. Nor did `DeviceWorkerManifest`,
`MatchScore` or the process transport: a launch entry is `Command` + `Arguments` through an ordinary
`Process.Start`, and `win-x64` was already computed.

**Gate — the managed half.** `PeImportsTests` (15, everywhere — descriptor selected by our ABI
symbols, ordinal imports skipped, PE32 as well as PE32+, and a truncated/garbage image refused rather
than read past its end, all against PEs built in the test). `PdkWindowsWorkerManifestTests` (7 — the
`win-x64` entry is written again, names the Windows build, and carries a bare `.exe` command that
resolves in the tools folder wherever it runs; a Linux-only kit still gets no Windows entry).
**Verified beyond our own code:** the C# parser resolves `KERNEL32.dll` / `SHELL32.dll` out of a real
mingw-built binary's import table, and derives `crf_test_host.dll` from `tools/fake-model-lib`'s real
DLL.

**Gate — the native half, `tools/senior-worker/verify-windows.sh`.** None of this can be a C# test:
staging a file under a name read out of a model's import table, an import binding to an
already-loaded module, and stdio mode are properties of a RUNNING process. The script loads a real PE
under Wine and checks them. An unprivileged user with a **read-only** install stages the shim,
resolves the model's import against it, walks the PE exports to find the family, and completes a
`describe` → `create` → `eval` exchange with bit-exact currents; a second model naming a different
host module stages alongside the first; a newer shipped shim refreshes the staged copy. The Linux
worker drives the same fixture end to end too (adding `probe`).

**R-win-7 is proven load-bearing, not assumed.** The script builds a control with the two `_setmode`
calls neutralised and nothing else changed: the identical payload comes back corrupted and the stream
desyncs, while `describe` passes identically in both runs — which is exactly why the bug is easy to
ship and why a `describe`-only test cannot catch it.

**STILL OPEN — and it is the one that matters: no production library has been loaded on Windows.**
Wine is a reimplementation and the fixture is ours, so a PASS exercises the mechanism without
standing in for the kit. Unsettled until a Windows machine with a kit runs it: whether the 15
symbols are *sufficient* (they are demonstrably necessary); a CRT mismatch against a genuinely
UCRT-built library; whether the kit's own `an extra export` export wants anything at load time; and
the vectored exception handler under a real access violation. See `tools/senior-worker/README.md`.

**Bisected with the CLI, 2026-07-31 — the solver and the topology are NOT the problem.** A vendor
kit's package would not settle (`residual 35.6`, 279k iterations). Two substitutions in the generated
`netlist.cnl`, both run headless with `dotnet run --project src/Cli -- dc`:

- **compiled FET → 1 GOhm open**: converges, and to the right answer (n26 = 28 V, n24 = 3 V).
- **compiled FET → a square-law SDD** in the same socket, all five instances: **converges in 5
  iterations**, residual 2e-7, Ids 36 mA.

So elaboration, the 15-port package network, the supplies, the thermal wiring and the nonlinear DC
engine are all sound at this topology; what remains is the compiled model itself or how its ten nodes
(4 external + 6 internal, one thermal) are mapped. **Substituting into the generated netlist is the
cheap bisection for any kit-backed non-convergence** — it separates "our circuit handling" from "that
model" in one run, and needs no VM.

A bare word after a parameter is a UNIT, never a net (2026-07-31) — COMPLETE, and it was building a
different circuit in silence. `Units._scales` had `Ohm/kOhm/MOhm/GOhm` but **no `TOhm`**, and a real
kit ties every unused package pin to ground through `R=1 TOhm`. The unrecognised token fell through
to `nets.Add(tok)`: fourteen resistors all wired to one node named **`TOhm`**, that node constrained
by nothing, the MNA matrix singular with an all-zero row — and no message anywhere mentioning a unit.
Same failure class as brief-unit-token-phantom-nodes, one table entry further along.

- **`TOhm` and `mOhm` added** — the series had a hole at each end.
- **The parser no longer guesses.** Nets come first and are finished by the first `name=value`, so a
  bare word after that can only be a unit; one that is not recognised is now reported by name.
  Reported rather than absorbed, because the old behaviour produced a *working parse of a different
  circuit*, and the error is what makes a one-entry fix obvious.
- **Instance lines never stripped their trailing `;` comment** — only `SplitExprUnit` did, for
  variable assignments. So every word of a comment joined the net list:
  `C:C1 a b C=1m ; near-short at 2 GHz` gave a capacitor with eight nets, six named after English
  words. Invisible because a two-terminal model reads the first two and ignores the rest; found only
  because the stricter rule above threw on the `;`. Now stripped quote-aware, so a `;` in a file path
  survives.

Gate: 11 tests in `tests/Core.Tests/Netlist/BareTokenAfterParameterTests.cs`.

A kit's data files reach the guest by being mounted WHERE THEY LIVE (2026-07-31) — COMPLETE.
**The bug the whole macOS path had been walking towards.** A compiled model is told which data files
to read through its OWN parameters: this kit's FET declares exactly four (`File`, `TAMB`, `RTH`,
`CTH` — confirmed by the worker's own `pars=4` against the kit's
`KITLIB_DEVICE_v1:FET1 … File=File TAMB=TAMB RTH=RTH CTH=CTH`), so its entire parameterisation is a
data file plus three thermal numbers. Those parameters arrive from the netlist long after the VM has
started, so **unlike the model library there is no command line left in which to rewrite them**. The
model then refused every operating point — cleanly, `analyze_nl` returning 0 with no SIGSEGV — and the
only symptom was a non-finite result far downstream.

**So the path is made true rather than translated.** `crf-vmhost` gained `--share-at TAG=PATH[:ro]`,
which mounts a share at the same absolute path it has on the host; `guest-init` honours it via
`crf.mountat=<tag>,<base64 of the mount point>` (base64 for the same reason argv is — the kernel
command line is space-separated and unquoted, and a macOS path may contain a space; it cannot contain
a comma, so the split is unambiguous). Nothing anywhere rewrites a data path.

- **The tree offered is exactly the one `KitDataFileResolver` can anchor a file within** —
  `OutermostSearchRoot`, the same constant, deliberately not a second notion of "near the kit". Two
  would drift, and the failure when they do is a path that resolves perfectly at import and cannot be
  opened at run time, which is this bug one level along.
- **Anchored on the kit's NETLIST folder, not the import root.** They are not interchangeable: for a
  kit imported whole the import root is the delivery folder, and for one healed from an installed
  workspace it is the netlist folder itself.
- **A library override under a `--share-at` tree is left exactly as written** — the host path is
  already valid in the guest, and rewriting it to `/mnt/<tag>/…` would name a place nothing was
  mounted.
- **`generatedFormat` on our own manifests, bumped to 2.** "Stale" used to mean only that a named
  program had gone, so a manifest whose every path still resolved was never reconsidered — and one
  that runs is exactly the one nothing else would ever replace. An older format is now redone at the
  next workspace open instead of staying runnable and wrong.

Gate: 5 tests in `tests/Ui.Tests/PdkKitDataShareTests.cs` — including one that reads a data file
through `KitNetlistReader` and asserts the shared tree contains what it resolved, which is the
assertion that actually ties the two halves together — plus 1 in `VmHostLibraryOverrideTests`. **The
guest half is unverified by any automated test**: the Swift and the initramfs build and their
non-VM paths run, but nothing here can start a VM.

A failed evaluation point must not assert a cause the worker never reported (2026-07-31) — COMPLETE.
`senior_worker.c` sets `status[k] = 0` for **three different things** — the model's `analyze_nl`
returned 0 (it refused the point), a SIGSEGV was caught inside it, or a current came back non-finite
— and the wire cannot tell them apart. The message named only the last and went on to suggest the
bias was out of range, which reads as a diagnosis and sent a real investigation the wrong way. It now
says all three and names the two usual causes: a bias outside the model's valid range, **and a file
the model needs and could not open**.

- **The worker's own log is now attached here too.** A failed point arrives as a perfectly normal
  reply, so it never passes through `DeviceWorkerChannel.Failed` — the only place that used to attach
  worker output. Yet the log is the one thing that separates the three cases: the worker writes
  `eval: SIGSEGV caught` when the model crashed, and a model that cannot read a data file usually
  says so there.
- **`ExternalDeviceModel` no longer rethrows an `ExternalDeviceException` unchanged**; it adds the
  instance label. A worker can only name the TYPE, which is useless the moment a design holds several
  devices of one type — this kit's package holds five of `KITLIB_DEVICE_v1`, wired differently, two
  with gate and drain shorted and a thermal node joined to nothing else. Which instance failed is the
  first thing anyone asks.

Gate: 2 tests in `DeviceWorkerProviderTests` (the message names all three possibilities; the existing
failed-point test kept).

A worker must never outlive circuitRF (2026-07-31) — COMPLETE. **Found from a leaked VM still
running 23 minutes after the application that started it had quit**, and it was not presenting as a
leak: the NEXT run failed with `the connection failed (Broken pipe)` and no worker output at all,
naming neither the leak nor the run that caused it.

**Why a leaked worker is a real fault on macOS and not untidiness.** macOS allows only a few VMs at
once. A leaked one holds its slot indefinitely, because closing the pipe tells the GUEST nothing — a
virtio console has no end-of-stream to deliver, so the guest blocks on a read forever and the VM never
powers down. The next run then cannot start its VM and is **killed by the system before it can print
anything**, which is why the report contains no diagnostic.

**Windows and Linux need nothing, and that is checked rather than assumed.** There the worker is an
ordinary child on a real pipe: when circuitRF goes away by any means, the OS closes the write end,
`read_exact` sees `r <= 0` (`senior_worker.c`), the command loop breaks and the worker exits. The
virtio console is the *only* reason macOS needs any of the following.

- **`ExternalDeviceRegistry.ResetResolved()` now runs on `AppDomain.CurrentDomain.ProcessExit`**
  (`App.axaml.cs`). It was wired only to a workspace switch, so quitting left one worker per kit the
  design had used. Hooked on ProcessExit rather than added to `Quit()` because Quit is not the only
  way out — three paths reach `Environment.Exit` directly, and a fourth added later would silently
  not be covered.
- **That is not sufficient on its own, and cannot be.** No code of the caller's runs on a crash or a
  `kill -9`. So `crf-vmhost` also ends ITSELF when the caller goes, on two independent triggers: the
  parent process exiting (a `DispatchSource` process watch — this is the one that makes the leak
  impossible rather than merely unlikely), and its own stdin reaching end of stream (the caller's
  deliberate shutdown signal, and the only one available while the application is still running).
  Exiting is enough to take the VM with it: the machine's state belongs to that process.
- **The parent can die between reading its id and the watch being armed**, leaving the source
  registered against a process that is already gone so it never fires. Re-reading `getppid()`
  afterwards closes that window — a reparented process reads 1.
- **A startup reaper for orphaned VMs was considered and NOT added.** With the watch in place a leak
  can no longer be created, so a reaper would only ever collect strays from a build that predates it
  — a migration aid, permanently on, that kills processes by heuristic. The one existing stray was
  cleared by hand instead.

Gate: 3 tests in `tests/Core.Tests/Devices/External/ResolvedWorkerTeardownTests.cs`, over a real
worker process — teardown reaches the worker through both `ResetResolved` and `Clear`, and a provider
the HOST registered is left alone (the same bug in the opposite direction). Identifying the worker by
diffing the process table was tried and is wrong: other test classes start the same reference worker
concurrently, so the diff is whoever happened to launch at that moment — it flaked in a full-solution
run and passed alone. The transport the resolver built is kept instead. **The Swift half is
unverified by any automated test** — it compiles, signs and its non-VM paths run, but nothing here can
start a VM.

**A fast-dying worker's own words were being lost, and the first attempt to settle this reached the
wrong answer (2026-07-31).** Error output arrives on a background reader, so a worker that dies during
start-up can be reported before a single line is delivered — leaving `(Broken pipe)` and nothing else,
in exactly the case where the worker's message is the only description of what happened.
`ProcessDeviceWorkerTransport.RecentErrorOutput` now waits for the reader to finish first.
`WaitForExit()` with NO timeout is the only overload that waits for the redirected readers to reach
end of stream; the timed one returns as soon as the process is gone and leaves output in flight. It is
run off-thread under a 2 s bound, and only ever on a process that has already exited.

**It only reproduces under load, and that is the part worth remembering.** 12 isolated runs passed and
were taken as proof the race did not exist — the wrong experiment, and the wrong conclusion drawn from
it. Measured properly afterwards: **5 failures in 12 full-solution runs without the fix, 0 in 12 with
it**, and 0 in 40 runs of the test on its own either way. If
`AWorkerThatDiesImmediately_StillReportsWhatItSaidOnTheWayOut` fails, it is not flaky — and it must
not be "stabilised" by waiting for the process to be reaped, because that wait IS the grace period the
test exists to remove.

A per-instance model-library override (2026-07-31) — COMPLETE. `ModelLibrary` is circuitRF's own
file-valued parameter on every kit part, blank by default: set it and just that instance is evaluated
with a different library, which is what makes two revisions comparable side by side in one schematic.

**It travels in the PROVIDER NAME** (`kit|path`, `DeviceWorkerProviderResolver.ComposeOverride`)
because that is what `ExternalDeviceRegistry` keys on — two instances naming different libraries must
get two providers, or the second is silently evaluated by the first's models.

**The argument replaced is the one that NAMES a shared library** (`.so`/`.dll`/`.dylib`) — a checkable
property of the value, not its position. A worker's arguments are the kit's to arrange, so replacing
"the last one" would be reading a habit; appending would hand the worker two libraries and let it
choose. Nothing to replace, or more than one candidate, is reported.

Gate: 7 tests in `tests/Core.Tests/Devices/External/ModelLibraryOverrideTests.cs`.

**A chosen library is put through the VM's SHARE mechanism on macOS, never handed over as written
(2026-07-31)** — the first real-kit bring-up failure, and it looked nothing like an override bug. The
kit's own library reaches the worker as `/mnt/kit/…` because the worker runs inside `crf-vmhost`; the
substitution wrote the chosen path in verbatim, so a path on the **Mac** replaced a working **guest**
path. The VM then started perfectly and failed inside the guest with `dlopen … No such file or
directory`, naming a file that plainly exists — the reported path is on the host, and the host's
filesystem is not the guest's.

`VmHostArguments` is now the ONE place that knows the contract's two halves — a directory is offered
as `--share TAG=PATH` and the guest sees it at `/mnt/TAG` — because anything writing one half without
the other produces exactly this failure. `PdkPartInstaller` builds the osx entry through it too, so
the importer and the override cannot drift apart.

- **An existing share is reused whenever it already covers the file**, which is the common case and
  not a nicety: another revision of a library normally sits beside the kit's own. It also keeps the
  guest command short, and the kernel command line carrying it is a fixed-size buffer that
  `crf-vmhost` refuses to overflow.
- **Only the argv run INSIDE the guest can name the library to replace.** The VM host's own options
  describe the machine, not the work, and a share value can carry a `.so`-shaped directory name.
- **A new share is inserted BEFORE the `--`**, so the option stays an option; past it, the share
  would be lost and the worker would get a flag it never asked for.

Gate: 12 tests in `tests/Core.Tests/Devices/External/VmHostLibraryOverrideTests.cs`. **Verified to
fail without the fix** (4 red) and **verified against a kit**: the workspace's own manifest
plus the host path the schematic actually carries now resolve to `/mnt/kit/RfPowerDesignKit.so`,
reusing the kit's share and adding none.

**Platforms, verified against a kit rather than assumed.** It ships Linux x86-64 ELF and
Windows only — no macOS build at all. So Windows and Linux x86-64 load the library natively; macOS
cannot load a Linux ELF under any circumstances, because that is a binary-format and OS-ABI mismatch,
not an instruction-set one. Rosetta 2 translates x86-64 *macOS* binaries and does not apply. What does
apply is **Rosetta for Linux** — Apple's Virtualization framework offering Rosetta *inside* an arm64
Linux VM so it can run x86-64 Linux binaries. The VM is not optional; Rosetta only makes it fast. That
is exactly what the working macOS entry does today (`orb -m <vm> …`), and it needs no special code:
per `MatchScore`, it is just another platform's command.

**Re-verified exhaustively (2026-07-31), because "can macOS run this out of the box" keeps recurring:**
the kit carries **34 build directories, every one Linux or Windows** — zero `darwin`, zero `.dylib`,
nothing macOS-shaped anywhere in the tree. There is no vendor build to fall back to, and the reason is
structural: the kit targets a simulator that itself ships Linux/Windows only.

**So on macOS the VM is not a workaround, it is the only mechanism — the open question is only WHO
SUPPLIES IT.** macOS has no Linux ABI personality (no `linuxulator`, no WSL1-style pico process), so
there is nothing to load a Linux ELF into. The three real options, stated once so they are not
re-derived:
1. **The user installs a Linux VM** (OrbStack/Lima/Docker) — today's `orb -m <vm> …` entry. Works,
   costs the user an install.
2. **circuitRF ships its own VM** — a bundled Linux kernel + minimal rootfs driven through Apple's
   `Virtualization.framework` (built into macOS) with `VZLinuxRosettaDirectoryShare` for x86-64. Out of
   the box *from the user's side*, since the component ships with us rather than being installed by
   them. Needs a small native helper (the framework is Objective-C, not reachable from .NET), the
   `com.apple.security.virtualization` entitlement, and a GPL-source offer for the kernel. **It needs
   no circuitRF engine change at all** — `MatchScore` already resolves `"platform": "osx"` (score 2,
   verified), so it is a manifest `command` swap.
3. **A macOS-native model** — either the vendor ships one (they do not) or the MINT effort produces
   one. This is the only path with no VM anywhere; it is paused by owner decision.

**Option 2 is built and proven (2026-07-31).** `tools/macos-vmhost` (a Swift VM host driving
`Virtualization.framework`) plus `tools/macos-vmimage` (a reproducible, Mac-buildable Linux image).
Verified with the production library: the worker starts under Rosetta, `dlopen`s the x86-64 Linux
library and reports its device families ready. See `tools/macos-vmhost/README.md` for the five
non-obvious things that had to be right, each found by measurement.

**A kit may now name a shipped helper by BARE NAME** — `"command": "crf-vmhost"` — and
`DeviceWorkerManifest.ResolveCommand` finds it in `ToolsDirectory` (circuitRF's own install,
`AppContext.BaseDirectory`). Deliberately narrow: the COMMAND only, never arguments (those name the
kit's files and have no business resolving inside circuitRF's install), and only a bare name
(anything with a separator is a path the kit meant literally). The kit's own folders are searched
first, so a kit shipping its own build of a tool keeps it. Gate:
`tests/Core.Tests/Devices/External/ShippedToolResolutionTests.cs`.

**Do not go looking for a fourth.** Emulating a Linux x86-64 `.so` in-process on macOS means an ELF
loader plus a syscall translator plus an x86-64 emulator — which is precisely what option 2's VM
already is, bought rather than built.

Reading a kit's own netlist — Stages A and B (brief-kit-netlist-reader.md, 2026-07-31) — COMPLETE.
**A vendor kit imported as-is into a fresh workspace now yields a working part, with no file placed
anywhere afterwards by anyone.** `KitVariantDiscovery` finds a part's formulations from the names
alone (a shared stem, a differing trailing token) and picks the default by BUILDABILITY.

**Buildability is "was it read completely", NOT "are its types familiar" — getting that backwards
inverts the answer, measured.** An unfamiliar type is very often a device a provider
supplies, which is the normal case for the formulation a kit expects you to use; the formulation that
cannot be built is typically the one written in a form the reader could not take. Testing for
unfamiliar types marked the working formulation broken and the broken one working. It is recursive:
a cell reads cleanly while the cell it instantiates does not.

**A part with no buildable formulation offers no choice at all** — a picker that cannot produce an
answer is worse than no picker. **A tie between two families identifies nothing**, because guessing
would attach a formulation choice to the wrong part.

A frequency-dependent value may now CROSS A CELL BOUNDARY (2026-07-31) — COMPLETE. `freq` is bound
at stamp time by the three models defined as functions of it (`Z_Port`'s `Z[i,j]`, `Chain`'s A/B/C/D,
the SDD's `H[w]`), while the elaborator resolves parameters once with no frequency bound. A kit's
frequency-dependent transmission line computes its RLGC — skin effect, dielectric loss — in ordinary
cell variables and passes them DOWN to one of those models, so the value has to survive the trip as
an expression. `src/Core/Expressions/FreqDeferral.cs` is that mechanism.

**This is a deliberate, bounded amendment to the scope/binding rule, taken with owner approval** (the
"Ask before" item below). The invariant it does NOT weaken: the numeric layer still receives a
self-contained expression in `freq` and nothing else — the same form `Z_Port`/`Chain` have always
accepted — never an unbound name.

- **Deferral is opt-in by dependence, not by position.** Only a transitively frequency-dependent
  value is carried as an expression; everything else still folds to a number exactly as before.
  Deferring indiscriminately would put a growing expression tree in the HB inner loop, which
  evaluates every device once per harmonic sample per Newton iteration.
- **Two rules, and conflating them is the mistake that got made and caught.** At a CELL BOUNDARY
  (`InlineForCellBoundary`) every non-`freq` name is folded to a literal, because the expression is
  about to be bound into the child's scope where the parent's names are not visible. At a DEVICE
  (`InlineForDevice`) only names that are THEMSELVES frequency-dependent are inlined — a `Z[i,j]`
  mentions `freq` **by definition**, so the cell-boundary rule fires on every existing ZPort and
  folds its scope variables into literals, which is numerically harmless and still breaks the
  inject-by-name contract `InjectZPortScopeVars` provides. Caught by `ZPortDiagTest`, not by review.
- **Recursion always folds, whatever the caller asked.** Inside a binding, names resolve in the
  binding's OWN scope, which need not be the one the result is evaluated in; leaving one as a
  reference could silently pick up a different binding of the same name.
- **Inlining is AST-level.** Splicing expression text gets precedence wrong the first time a
  substituted body is more than a single term. `Render` is fully parenthesised for the same reason —
  the gate test asserts the re-parsed VALUE, never the text.
- **A unit is applied exactly once.** Inlining absorbs each binding's own unit, so the site unit is
  skipped when the original expression referenced a unit-bearing var — through `Evaluator`'s own
  `ReferencesUnitBearingVariable` rather than a second copy of the var-unit-wins rule.
- **Frequency dependence MUST terminate at a model that binds `freq`.** Reaching anything else raises
  `FrequencyDependentValueException` naming the device, the parameter, and the three models that can
  take one — a resistor takes a single number, and saying so beats a bare "Unresolved name 'freq'"
  reported from somewhere inside the value.
- **`complex(re, im)` is now a builtin**, the rectangular counterpart of the existing `polar()`. It is
  the natural way to write a frequency-dependent immittance and is the form `FreqDeferral` renders a
  resolved Complex back into, so a deferred expression can be re-parsed by the model that evaluates it.

Gate: `tests/Core.Tests/Expressions/FreqDeferralTests.cs` (22) and
`tests/Engine.Tests/Linear/FreqDependentCellParameterTests.cs` (10) — every engine-level result checked
against the analytic response of the network the chain matrix describes (series L between two ports),
across two- and three-deep hierarchies, plus the flat-response guard, the units guard, the termination
error, and the inject-by-name regression. **Verified to fail without the fix** (`Unresolved name 'freq'`)
and **verified against the production kit**: its own 2 mm × 30 µm m1 line elaborates and sweeps —
|S21| 0.978 → 0.507 and ∠ −12.5° → −194.6° over 1–20 GHz, with the 1 GHz phase agreeing with a hand
calculation from the kit's own RLGC formulas (β·l = 11.95° before loss).

A kit's declarations must SURVIVE THE ROUND TRIP, not merely be extracted (2026-07-31) — three
bugs found in one sitting, all with the same shape: extraction was correct and the loss happened
between `CnlWriter` and `CnlReader`. **The run path is `RunAnalysis → WriteNetlist → netlist.cnl →
SchematicRunService.RunNetlist → CnlReader`, so anything the writer cannot say is gone by the time
the elaborator sees it** — and the error then names a position in a generated file that no longer
contains the thing that was wrong. When a kit-backed run fails, check the round trip before the
extractor; two of these three looked exactly like extractor bugs and were not.

- **`CnlWriter` never emitted `tb.Functions`.** `CnlReader` has always PARSED the `name(a, b) = expr`
  form, so only the writing half was missing — a kit's cells call its functions by bare name, and the
  file arrived with every call site and no declaration (`Unknown function 'KIT_TECH_CALC_PAD_CAP'`).
  Globals were unaffected, so a missing global is NOT the same bug. Gate: `CnlWriterTests`
  `UserFunctions_RoundTrip` + `AFunctionAndAVariable_StayDistinct_AcrossTheRoundTrip` (the reader
  tells them apart only by the parenthesised parameter list).
- **`**` is the kit's exponentiation operator; circuitRF's is `^`.** `KitNetlistReader` translated
  `if…then…else…endif` and `strcat` but not this, so `(Gy*(TL_FREQ*1.0e-9)**GLE_val)` reached the
  parser verbatim. Both operators are right-associative and bind tighter than the arithmetic ones, so
  `RewritePowerOperator` is a spelling change — **quote-aware**, because the same values carry file
  paths where a `**` is data, not an operator.
- **A kit writes a unit glued as readily as spaced** — a kit has `CLINE=1 pF  LLINE=1pH` on ONE
  line. The reader handled only the spaced form, so `1pH` reached the expression engine. Fixed by
  **sharing `CnlReader.TrySplitGluedUnit`** (now `internal`) rather than writing a second splitter:
  its guards — numeric head, recognised unit — are the entire reason splitting is safe, and a second
  copy is a second set of guards to keep in step.

**`RewriteExpression` is the ONE place a kit's expression text is translated.** All three call sites
route through it; add a dialect rewrite there, never at a call site.

**A kit's data files are ANCHORED where the netlist is read (2026-07-31)** — `KitDataFileResolver`,
reached from `KitNetlistReader.ReadFile`. A kit writes `File=strcat(DataPath,"X.s15p")` with
`DataPath="SomeKit_Data\"`: a path relative to the simulator's own data search path, which its
installation puts there. circuitRF has no such search path, so left relative the value survives into
the generated `.cnl` and is finally resolved against **that** file's folder — the workspace — and the
run fails naming a file in a directory the kit has nothing to do with, while the file sits untouched
in the kit. This is the same shape as the three round-trip bugs above: the extraction was right and
the loss happened downstream of it.

- **The netlist's own folder is NOT the anchor.** A kit keeps netlists in `circuit/models/` and
  data in `circuit/data/`, so this is a bounded look AROUND the netlist — two ancestors, one level of
  children each — not a resolve against one root.
- **The bound is load-bearing and is not to be relaxed when something is not found.** Each ancestor's
  children are listed, so one level too far starts listing a home or temp directory, and a value that
  happens to match a file in there resolves to something the kit never named.
- **Nothing is rewritten unless a real file is found**, which is what makes this safe to try on every
  value instead of on a list of parameter names. A kit names its files with whatever keyword it likes
  — `.s15p` for the network, `.mdl`/`.mds` for the compiled models — so a name list would silently
  cover some and not others.
- **Reading from TEXT anchors nothing.** There is no file to be relative to, and resolving against the
  process's working directory would make the result depend on where circuitRF was started.

Gate: 10 tests in `tests/Core.Tests/Netlist/KitDataFileAnchoringTests.cs`, synthetic. **Verified
against the kit:** all 12 of its `File=` values — one `.s15p` and eleven `.mdl`/`.mds` — now
resolve into `circuit/data/`, where they are.

**FOLLOW-ON, NOT FIXED: those absolute paths are HOST paths, and on macOS the worker reads them
inside the VM.** An anchored `.mdl`/`.mds` is correct for a native Linux or Windows worker and is
strictly better than the relative form, which resolved nowhere at all. But the guest only has the
shares `crf-vmhost` was given — the worker's folder and the model library's — so a kit data file
reaches it by no path at all. It needs the same treatment the model-library override just got (see
`VmHostArguments`), except that these paths arrive as DEVICE PARAMETERS rather than command
arguments, so the share cannot be added at launch from the argument list alone.

**Standing check when adding anything to `TestBench` or `Cell`: can `CnlWriter` say it?** A field the
writer cannot express is silently absent from every run, and the symptom appears far from the cause.

**OPEN, NOT FIXED — an empty parameter value is lost AND eats its neighbours.** A kit writes
`InterpDom=` with no value; `CnlWriter` emits it verbatim; `CnlReader.MergeSpacedAssignments` sees a
token ending in `=` and glues the NEXT token on as its value. Measured's 15-port SnP:
`File Type InterpMode InterpDom ExtrapMode Temp CheckPassivity NumPorts` read back as five, with
`InterpDom = ExtrapMode="constant"` and **`ExtrapMode`, `Temp` and `CheckPassivity` gone** — so the
extrapolation mode silently reverts to its default. This is a wrong answer, not a crash. Left open
deliberately: every candidate fix (writer omits an empty value / writer emits `""` / reader refuses to
glue a token that carries its own `=`) changes the `.cnl` contract, which this file's own "Ask before"
rule covers.

Reading a kit's own netlist — Stage A (brief-kit-netlist-reader.md, 2026-07-31) — COMPLETE.
`src/Core/Netlist/KitNetlistReader.cs` reads the dialect a kit ships into the same
`Library`/`Cell`/`Instance` model `CnlReader` produces, so a kit's part is treated exactly like a cell
the user drew. **Why it exists:** a vendor kit is read-only and self-contained, and importing one must
produce a working part with no file placed anywhere afterwards — the three facts a part needs (that it
offers a choice of formulation, which one is buildable, what circuit it is) are all in this file, and
every alternative is a declaration someone has to write and put somewhere.

**It reads a FORMAT, not a kit** — nothing in it names a supplier, library, part or model family — and
it is deliberately not a general-purpose importer: everything it does not understand is reported by
line and skipped, never guessed at.

Rules worth keeping:
- **A bare word after a value is that value's UNIT** (`R=1 TOhm`); a word containing `=` starts the
  next parameter. This is the one that silently corrupts: `R=1 TOhm` read as `R=1` is a resistor a
  thousand billion times too small and everything downstream still runs.
- **A backslash in a quoted value is a directory separator, not an escape** — a kit spells a folder
  `Path="Data\"`. Normalised to `/`, so joining it to a filename gives a path rather than a run-on word.
- **`if(c) then (a) else (b) endif` → `if(c, a, b)`**, purely syntactic; a malformed one is left exactly
  as written, because an expression that fails with the kit's own text beats one that evaluates to
  something nobody wrote. Same rule for `strcat` over an unresolvable piece.
- **A mismatched `define`/`end` is an error, not a note** — every later cell would be attributed to the
  wrong define.
- **`Options:` is the one `Type:Name` line that is not a device**, skipped and reported rather than
  special-cased into silence.

Gate: 19 tests in `tests/Core.Tests/Netlist/KitNetlistReaderTests.cs`, all over synthetic fixtures —
the reader is a format reader, and the repo does not commit proprietary kit data. **Verified against
the production kit by disposable probe:** its two netlists read as 2 + 7 cells with 4 notes total
(two `Options:` lines and two genuinely unhandled constructs), the 26-port package defines both read,
`strcat` resolved to real paths, and units survived. Full suite 5,623 pass.

**A kit's netlists are ONE library split across files** — the file defining a part instantiates cells
declared in another and its process constants live in a third — so `NetExtractor.NetlistImports` reads
every netlist beside the named one, the named file winning on a name collision. Reading only the named
file gives a definition whose own contents do not resolve (measured: 2 cells and no constants, against
9 cells, 38 constants and 4 functions once siblings are read).

Out-of-process device-worker provider (M5 transport, 2026-07-31) — COMPLETE: the first concrete `IExternalDeviceProvider` in the repo. `src/Core/Devices/External/`: `DeviceWorkerProtocol` (frame codec), `DeviceWorkerTransport` (`IDeviceWorkerTransport`, a process transport and a stream transport), `DeviceWorkerChannel` (request/reply), `DeviceWorkerProvider`, `DeviceWorkerInstance`. **Still nothing about any particular provider in circuitRF** — a worker executable path is runtime configuration, and every device type, parameter name, pin count and node role is learned from the worker's replies. Gate: 46 tests in `tests/Core.Tests/Devices/External/`, 0W/0E, full suite 5,490 pass.

**Why a process and not a library — the reason is structural, not a preference.** A compiled device model calls back into the process that loaded it for services that process must export as C symbols, which a managed host cannot do; and one process can hold exactly one build of one library, so several builds means several processes. Both constraints dissolve once the model lives in its own process, and circuitRF then loads nothing and links against nothing. This also means **the macOS path needs no separate design**: a worker built for another OS runs in a VM and is driven over the same two streams, so `IDeviceWorkerTransport` is the only thing that varies.

Wire format: `[ uint32 jsonLen ][ uint32 binLen ][ JSON ][ raw little-endian doubles ]`. JSON control plane so a frame stays readable in a hex dump; bulk numerics as raw doubles so a large batch costs no parsing. Commands `describe | create | probe | eval | destroy | shutdown`. `eval` sends `count × nodes` doubles and returns `status[count]` then, per point, `I[n], Q[n], G[n×n], C[n×n]`, all row-major.

- **Batching is the point, not an optimisation.** Measured against a real worker: ~100 µs per evaluation one at a time, ~4.2 µs at batch 2000 — **~24×**. HB evaluates every device once per sample per Newton iteration, so the per-call version makes the transport, not the model, the simulator. `EvaluateBatch` is one round trip.
- **No sign flip, and this is checked rather than inherited.** A worker reports current positive INTO the device, already matching this repo's convention (see the M3 note below). Confirmed against behaviour: at a drain bias the drain node's current is positive while the device sinks it, and the thermal node's current is negative with magnitude equal to the dissipated power — power leaving the device. A second, defensive flip would invert every operating point *and still converge*.
- **Partial pipe reads must be looped.** A short read is normal on a pipe; treating one as end-of-stream yields frames that decode as garbage only under load. Tested with a stream that returns one byte per read.
- **An implausible frame length is a desync, not a large result.** Believing a corrupt length means allocating gigabytes instead of reporting the stream is out of step.
- **stderr is drained on a thread.** Nobody reading it fills the pipe and the worker blocks forever inside a write — presenting as a hang midway through a long solve with no error anywhere. The drained output is attached to the exception, since it is usually the only description of what went wrong.
- **One request at a time, locked.** Two threads writing frames into one pipe interleave them and the worker reads a header out of the middle of somebody else's JSON. Correctness, not convenience.
- **An unknown parameter name is rejected at `Create`.** The worker matches by keyword and ignores what it does not recognise, so a typo would otherwise present as a device quietly running on a default. The error names the parameter and lists the real ones. A *blank* value is omitted instead, so the model keeps its own default.
- **A point the worker could not evaluate raises.** `IExternalDeviceInstance` has no channel for per-point status; returning the non-finite numbers would put a NaN in the matrix and surface far away as unexplained non-convergence. When a damping channel exists, this becomes a status return.
- **Node roles are measured, not declared.** `create` is followed by `probe`, which reports per node whether it is a free unknown and whether an external pin is thermal — the discriminator being Jacobian *symmetry*, not magnitude. A worker too old to probe is not an error; the declared descriptor stands.

Test seam: `FakeDeviceWorker` speaks the real wire format over in-memory streams, so the provider is exercised end to end with no model present. Its device is deliberately **asymmetric** (`G[0,1] = 3`, `G[1,0] = 0`) — a symmetric one lets a transposed Jacobian, or a charge block read as a current block, pass every check.

**Zero-setup provider resolution (2026-07-31).** The user's path is *import kit → place part → configure analysis → Run*, with no provider to configure. `ExternalDeviceRegistry` therefore takes `IExternalProviderResolver`s alongside registered providers: when a netlist names a provider nobody registered, the resolvers are asked, and whatever one produces is cached under that name.

- **Resolvers, not providers, are registered at workspace open — so nothing starts.** A workspace may hold many kits and a given design typically uses none of them. A worker process starts the first time a design actually asks for that kit's devices.
- **`DeviceWorkerProviderResolver` looks for a `device-provider.json` manifest** in each kit folder (a root and one level down). The manifest is the single fact circuitRF cannot derive — which program evaluates this kit's devices — and it is **data beside the kit**, not knowledge compiled in. Per-platform entries pick by `MatchScore`: exact runtime identifier > operating system > catch-all, so a kit gives one general entry and overrides it for one platform. **This is where the "runs in a VM on macOS" case lands with no special code** — it is just another platform's command.
- **Relative paths resolve against the manifest's folder, then its declared `baseDirectory`.** Importing copies the manifest into the workspace while the worker and model files stay in the installed kit, so the copy records where the kit was. Manifest-folder-first means dropping real files beside the copy overrides the kit — the only escape hatch that needs no configuration.
- **The copy is always named for the kit.** Each installed cell records `Provider = <kit name>`, so that is what a netlist asks for. A copy keeping the manifest's own name leaves every step working and only Run failing — caught by a test, not by inspection.
- **`ResetResolved()` on workspace switch** ends providers the registry started (their workers point at the old workspace's kits) and leaves host-registered ones alone.
- **`Require`'s message names the folders searched** and the manifest filename. "Provider unavailable" with nowhere to look is a dead end.
- A kit with no manifest imports **silently** — its parts still place, draw and export. Only simulating them needs one, so a message here would be noise on nearly every import.

**Reference worker + real-process coverage (2026-07-31).** `tools/DeviceWorkerExample` is a complete worker serving one synthetic square-law FET, and it **references nothing — not even `CircuitRF.Core`**. That is the point: a real worker is a native program that cannot use our frame codec, so this one implements the framing itself. `DeviceWorkerProcessTests` (17 tests) then compares two independent implementations rather than one agreeing with itself.

Until this landed, `ProcessDeviceWorkerTransport` had **zero** coverage — every test spoke the protocol over `MemoryStream`, which cannot produce the failures that actually occur: short reads, writes buffered until a flush, a deadlock when nobody drains stderr, an abrupt EOF when the child exits. The tests that matter most: a 2000-point batch (~72k doubles, far past any pipe buffer, so it only passes if partial reads are looped on *both* sides), 200 sequential round trips (a framing error leaving one stray byte is invisible on the first call and corrupts every one after), and Kirchhoff's law holding on the decoded currents. Whole file runs in ~0.5 s.

Two failure paths are deliberately distinguished, because they have different causes and different fixes: a worker that **died on its own** surfaces as `ExternalDeviceException` naming the worker (driven by really shutting one down and then reading), while using a device after the **application ended its provider** is `ObjectDisposedException` — a mistake in the calling code, not a transport failure.

The worker's path reaches the tests as build-recorded assembly metadata (`DeviceWorkerExampleDir`), not a relative guess from the test's output folder, which would break the first time a layout changed. The shipped `device-provider.json` is itself asserted valid — it is the template a kit author copies, and no product code reads it.

**End to end: a netlist that merely names a kit now solves (2026-07-31).** `tests/Engine.Tests/External/WorkerBackedAnalysisTests.cs`, 8 tests. No provider is registered — a kit folder with a manifest is stood up the way an import leaves one, and `ComponentModelFactory.Require` resolves it mid-elaboration. Covers: closed-form operating point through a worker process; the device genuinely **off** below threshold (a device that conducts nothing converges beautifully, so the on case alone proves nothing); **source degeneration**, where the operating point depends on itself and so actually tests the Jacobian that crossed the pipe; netlist parameters reaching the model; two devices sharing one worker; a sweep reusing the resolved provider rather than relaunching per bias point; and both "kit not installed" and "type not served" messages.

**A real integration bug, found by exactly that test.** `CreateExternalDeviceModel` forwarded **every** non-selector parameter to the provider — including circuitRF's own `__instanceLabel`, which it reads two lines further down. A permissive provider ignores an undeclared name, so this survived unnoticed against the in-process `SquareLawFetProvider`; a strict one rejects it and fails **every** device it serves. The strict behaviour is the correct one (it is what turns a misspelled parameter into an error rather than a silently defaulted device), so the fix is in the factory: **`__`-prefixed parameters are circuitRF plumbing and are not part of "everything else is forwarded".**

*Not a bug, worth knowing before asserting:* an "off" device does not read exactly zero current. The DC engine adds `gmin = 1e-12 S` to every voltage node for continuity, so 5 V leaks exactly 5 pA regardless of the device. Asserting zero asserts against the solver's own regularisation.

*Open, unreproduced:* one Core.Tests failure occurred on the single run immediately after this project was first built, and was not captured by name. Six subsequent full runs — including one forcing a rebuild — and three isolated runs of the process tests are all clean. Recorded rather than dismissed; if it returns, capture the test name before assuming it is first-build noise.

`Chain` — ABCD two-port primitive (M4, 2026-07-30) — COMPLETE: `src/Core/Devices/ChainModel.cs`, a two-port given by its chain matrix, entries as expressions in `freq` exactly like `Z_Port`'s `Z[i,j]`. Four nets as ± pairs (`[p1+, p1−, p2+, p2−]`); Group 2, two branch unknowns. Convention `V1 = A·V2 − B·I2`, `I1 = C·V2 − D·I2` with both currents INTO the device, matching every other model here. Omitted entries default to the identity two-port, so a partially-specified block degrades to a wire rather than a silent zero matrix.

**Why it exists when `Z_Port` already does — the reason is specific, not stylistic.** A chain matrix describes two-ports that have **no impedance matrix at all**. The case that matters: a pure series element has `C = 0`, so `Z11 = A/C` is infinite. Frequency-domain line models routinely degenerate to exactly that at DC (`A = D = 1`, `C = 0`, `B` = the series resistance), so a model that is perfectly well-behaved in ABCD form cannot be expressed as a Z-block at ω = 0. Stamping the chain relations directly stays non-singular there: with `C = 0, D = 1` the second constraint reduces to `I1 = −I2` and the first to `V1 − V2 = B·I1` — a series impedance. Gate: 8 tests in `tests/Engine.Tests/Linear/ChainModelTests.cs`, each against the analytic result for the network the matrix describes (series-Z at DC across three decades of value, identity, defaults, shunt-Y, ideal transformer, and a series-L whose |S21| is checked against `2·Z0/(2·Z0 + jωL)` at three frequencies).

Three v1 language capabilities wired up (M4, 2026-07-30) — COMPLETE. Each was specified but unreachable from a netlist; the gap was wiring, not arithmetic. Gate: 20 tests in `tests/Core.Tests/Expressions/LanguageAdditionsTests.cs`, all through the full `.cnl` → elaborate path.

- **User-defined expression functions are now declarable in `.cnl`**: `name(a, b) = expr` at top level. `Evaluator.RegisterFunction` has existed since v1 and the root CLAUDE.md lists the feature, but nothing parsed a declaration — `CnlReader.IsVariableAssignment` requires a bare identifier on the left, so such a line never reached any parser. Now `TryParseFunctionDeclaration` runs **before** the assignment case, stores into `TestBench.Functions`, and `Elaborator.Elaborate` registers them **before flattening** (a cell parameter default may call one). Declarations inside a `define` are rejected. `y = (a+b)*2` is still a variable — the parameter list must be identifiers.
- **String equality.** `Value.Equal`/`NotEqual` accept two `String` values (ordinal). Deliberately narrow: `==`/`!=` only, both sides String, no coercion either way. `String` stays storage-only otherwise — a string in arithmetic, or compared to a number, is still an error, and both are tested.
- **Rounding family**: `floor`, `ceil`, `round` (away from zero), `int` (truncate toward zero). Componentwise for Complex, which keeps them total. `int` vs `floor` on negatives is tested explicitly since that is the distinction that bites.

**Known constraint, worth knowing before writing any `.cnl` generator:** the generic instance-line parser splits on whitespace and treats bare tokens as nets, so an **unquoted parameter value must contain no spaces** — `R=if(a,1,2)` is fine, `R=if(a, 1, 2)` silently becomes a value plus two phantom nets, shifting every later node index. Only the SDD line parser does depth-aware boundary detection. Quoted values (file paths) are safe, the tokeniser is quote-aware. Not changed here: widening the generic parser touches every device and was not needed, since expressions are perfectly valid without spaces.

External devices — descriptor-driven `ExtDevice` (M3, 2026-07-30) — COMPLETE: a generic component whose behaviour comes from a registered external provider, with **nothing about any particular provider in circuitRF's code**. New `src/Core/Devices/External/`: `ExternalDeviceDescriptor` (TypeId/DisplayName/pin+node counts/params/nodes — all opaque, rendered never interpreted), `IExternalDeviceProvider` + `IExternalDeviceInstance` (with a **batched** `EvaluateBatch` on the interface from the start — HB evaluates per harmonic sample per Newton iteration, so a per-eval round trip to an out-of-process provider would dominate runtime), `ExternalDeviceRegistry` (host registers providers; Core never constructs one), `ExternalDeviceModel`, `ExternalDeviceException`.

**The mapping that makes this need zero engine change.** A provider reports currents per NODE and derivatives per node PAIR; `ComponentModel` is written in ports that each span a node pair. The two reconcile exactly when **every node is its own ground-referenced port** — the elaborator lays the node array out `[n0,0, n1,0, …]`, so `PortVoltages[k]` IS node k's voltage, `I[k]` the current into it, `Dg[k,l] = ∂I[k]/∂V[l]`. The engine's existing four-way port stamp does the rest.

**Passive sign convention, no flip anywhere.** A provider's current is positive INTO the device, which is exactly what `NonlinearDcEngine` stamps (`f[np-1] += ip`, "port current flows into device at np"). Verified by test, not assumed.

**Internal nodes are real unknowns.** `Elaborator.BuildExternalDeviceNodes` mints `__extdev_{instancePath}_n{k}` via `Nodes.GetOrAssign` — the same mechanism Tuner/P1Tone/PnTone already use — so they get ordinary global matrix rows. They are deliberately **not** locally eliminated: Schur reduction is simpler and is wrong for HB, where an internal node voltage carries its own harmonic content.

**Slaved nodes cost nothing.** A descriptor node reporting `SlavedTo` is given its master's node index instead of a fresh one; the engine's four-way stamp then folds the chain rule by itself (slaved row is identically zero → adds nothing; slaved COLUMN lands on the master's column → exactly what slaving requires). Chains and self-reference are hard errors. A provider that reports a node as degenerate **without** naming what it follows is a hard error at elaboration — the alternative is a silently dead device, which is the failure mode this path is most prone to.

`ExtDevice` reserves two parameter names, `Provider=` and `Type=`; **every** other parameter is forwarded to the provider verbatim, matched against the names its own descriptor declared. `ResolveExtDeviceParameters` follows the string-param rule (see below): `Provider`/`Type` are stored raw, and any other override that fails to evaluate as an expression is stored verbatim rather than throwing — a provider may declare file paths or enum-valued parameters, and a leading `/` alone crashes the expression parser at position 0 (the same trap `SnP`'s `File=` hit).

Gate: 11 tests in `tests/Engine.Tests/External/` against a synthetic square-law FET provider (`Id = β(Vgs−Vth)²(1+λVds)`, Rg/Rs creating two genuine internal nodes, plus a self-heating thermal node with its own internal Rth to a fixed reference). Asserted against a **closed-form scalar oracle that never touches the matrix** — operating point, internal node voltages, the exact identity `Tj = Id·Vds·Rth` at an externally-open thermal pin, self-heating actually derating the current, the analytic Jacobian entry-by-entry vs finite difference, the passive sign convention, and three distinguishable failure modes. Tolerances are set to what each side actually guarantees (engine `AbsTol=1e-6`; oracle iterates to 1e-15 on its own update) — asserting tighter tests the solver's stopping rule, not its correctness. Build 0W/0E; 5,284 tests pass.

Var-unit-wins in `Evaluator.Eval` (brief-var-unit-wins-consistency Part B, 2026-06-23) — COMPLETE: `Evaluator.Eval(expr, scope, unit)` now applies the **var-unit-wins** rule: when the expression references any variable that declares its own unit in scope (`scope.Lookup(name).Unit` non-empty), the site unit is **skipped** — the variable's unit was already applied once in `Resolve`. A new `private static bool ReferencesUnitBearingVar(Expr ast, Scope scope)` uses `AstWalker.CollectRefs` + `Scope.Lookup` to check. Guard: `!string.IsNullOrEmpty(unit)` — the no-site-unit path is untouched. Literals (no refs) still get the site unit; unit-less variable refs still get the site unit. Fixes `P1 Freq=RFfreq GHz` where `RFfreq` is unit-bearing (swept override with `Unit=Hz` or VAR declared `= 2 GHz`), and the latent prefixed-unit double (e.g. `Cval pF` where `Cval = 1 pF` gave `1e-24` instead of `1e-12`). 5 gate tests in `tests/Core.Tests/Expressions/EvaluatorVarUnitWinsTests.cs`. Build 0W/0E.

Parametric-sweep range units (brief-sweep-range-units, 2026-06-22) — COMPLETE: `SweepSpec` gains `public string Unit { get; } = ""` (optional trailing ctor param). The spec constructor of `ParametricSweepAnalysis` applies `Units.Scale(spec.Unit) ?? 1.0` when materializing `SweepValues`: Start and Stop are always scaled; `StepOrCount` is scaled only in StepSize mode (PointCount count is dimensionless). `SweepValues` are therefore always base-unit — the engine injection of bare doubles stays correct. CnlWriter emits `Unit=<unit>` after `Step=|Npts=` when non-empty; CnlReader reads `Unit=` (default `""`) in the spec form. Absent `Unit=` on existing files → `""` → scale 1.0 (back-compatible). 3 gate tests in `SweepSpecCnlTests.cs` (T1: StepSize scaling; T2: PointCount scaling (count not scaled); T3: CNL round-trip with/without Unit=). Build 0W/0E.

`ToneSourceModel.LastBranchIndex` (brief-sdd-control-current-tonesource, 2026-06-19) — COMPLETE: `ToneSourceModel` (`V_1Tone`/`V_nTone`) now exposes `public int LastBranchIndex { get; private set; } = -1`, captured in `Stamp` (`int br = LastBranchIndex = mna.AddBranch();`) exactly like `VdcModel`. This makes the tone source's branch current referenceable as an SDD control current (`C[n]=<toneSrc>`); the three engine resolvers (DC/HB/S-param) validate it as a two-terminal kind. No factory change — `CreateSddModel` stores raw control-ref instance names and only cross-validates `_cn`↔`C[n]`; kind validation lives in the engines. Engine-side detail + tests in `src/Engine/CLAUDE.md`.

SDD arbitrary weighting `I[p,w]` + `H[w]=expr` (brief-sdd-weighting-parser, 2026-06-19) — COMPLETE: SDD now accepts arbitrary weighting `I[p,w]` for `w≥2` with user `H[w]=expr` (Complex, in `freq`). `H[0]=1` and `H[1]=jω` are built-in and not redefinable. `I[p,w]` uses the real dual-AD evaluator (`SddEvaluator`); `H[w]` uses the Complex general `Evaluator` with `freq=ω/2π` bound at evaluation time. Parser chain: **CnlReader** `SddAssignmentHeader` regex extended to match `H\[\d+\]`; **Elaborator** `RxSddEquation` extended with `H` so `H[w]` params are stored raw and scope vars injected; **ComponentModelFactory** drops the v1 hard-error for `w≥2`, parses `H[w]` entries via `RxWeightFn`, and cross-validates that every referenced `w` has a matching `H[w]`. **SddModel** gains `_higherAst[][]` (per-port `w≥2` bucket lists) and `_weightAst` (w→H[w] AST), emits one `WeightedTerm` per distinct `w` from `Evaluate`, and overrides `Weight(w,ω)` to evaluate `H[w]` via the Complex evaluator. Ctor gains two optional params (existing tests unaffected). 10 gate tests: 8 in `SddWeightingParserTests.cs` (Core.Tests/Devices) + 2 in `SddWeightingParserE2eTests.cs` (Engine.Tests/HarmonicBalance). Build 0W/0E.

`NonlinearCModel` + `PolynomialFit` (brief-nonlinearc-model, 2026-06-19) — COMPLETE: Added `src/Core/Devices/NonlinearCModel.cs` (1-D polynomial nonlinear capacitor; `PortCount=1`, `Kind=Nonlinear`; `CapAt` Horner, `ChargeAt` Horner-integrated, `Stamp` no-op, `Evaluate` returns `Dc[[C(Vd)]]` and `Q[ChargeAt(Vd)]`). Added `src/Core/Expressions/PolynomialFit.cs` (normal-equation least-squares, Gauss partial-pivot, lowest-power-first output). Wired `"NonlinearC"` into `ComponentModelFactory._parameterizedTypes` and `TryCreate(typeName, params)` with `CreateNonlinearCModel` (reads `C0,C1,…` consecutively). 8 gate tests in `NonlinearCModelTests.cs`. Build 0W/0E, 1899 total tests.

`ComponentModel.StampLinearized` (brief-nonlinear-engine-seam, 2026-06-19) — COMPLETE: Added `using System.Numerics` and a new `public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)` to `ComponentModel` base class. Default implementation calls `Evaluate(bias)` and stamps `Y[p,q] = Dg[p,q] + jω·Dc[p,q]` as an N-port admittance block via `AddBlockAdmittance`, using the same port→node-pair convention as `NonlinearDcEngine`. Linear engines call this for `Kind==Nonlinear` devices; HB/DC never call it.

Schematic housecleaning (brief-schematic-housecleaning, 2026-06-19) — COMPLETE (Core items): **Item 2 (P1Tone S-param lint):** `Elaborator.LintTopLevelTerms` now includes `P1ToneModel` in the top-level port-family filter alongside `PortModel` and `TermModel`. A netlist with `P1Tone Num=1` + `Term Num=2` no longer emits a "port 1 missing" warning. Warning text still says "Terms are numbered…" but the diagnostic now spans the full S-param port family. **Item 5 (ohm/ohms units):** `Units._scales` (case-sensitive ordinal map) now includes `{ "ohm", 1.0 }` and `{ "ohms", 1.0 }` so `Z=50 ohms` no longer tokenizes `ohms` as a phantom net. `IsKnown("ohm")` / `IsKnown("ohms")` return true; `Scale("ohm")` / `Scale("ohms")` return 1.0. `Ohm`/`Ohms` (Title-case) are unchanged. 9 gate tests: 5 in `OhmLowercaseTests.cs` + 4 in `P1ToneLintTests.cs`. Build 0W/0E.

Standing instructions for `src/Core` (the design layer, the elaboration layer, the expression
engine, and the `ComponentModel` types). Read with the root `CLAUDE.md`. Design notes:
`docs/design/data-model.md`, `docs/design/expressions.md`. `Data/` and (numeric `ComponentModel`
behavior) the engine have their own notes.

## What lives here
- **Design layer:** `Library`, `Cell`, `Instance`, `TestBench`, `ParameterDeclaration`,
  `ParameterAssignment`, `Variable`, `Analysis` subtypes, `Measurement`. Editable, serializable
  (`.cnl` + JSON), human-readable.
- **Elaboration layer:** flatten hierarchy → `ElaboratedNetlist` (`ElaboratedComponent` list +
  `NodeMap`), resolving parameters/variables and numbering nodes.
- **Expression engine:** tokenizer → Pratt parser → AST → evaluator. Serves variables, cell
  parameters, the SDD, and measurements.
- **`UnitNormalizer`** (`Expressions/UnitNormalizer.cs`): `ToEngineUnit(editorUnit)` maps editor glyph
  unit strings (Ω, µ) to ASCII engine spellings (Ohm, u). Called at the extraction boundary only — do
  not scatter; editor glyphs and the `Units` table are both unchanged.
- **`ComponentModel`** base + `Devices/` (the numeric behaviors; their stamping/evaluation contract
  is detailed in `data-model.md` §5 and exercised by the engine).

## Key type distinctions — do not blur
- **`Cell` vs `TestBench`.** A `Cell` is a **reusable** definition (ports, parameter interface,
  contents) that gets instanced. A `TestBench` is the **one** thing you simulate (top cell +
  globals + analyses + measurements). **Analyses and measurements attach to the `TestBench`, never
  to a `Cell`.**
- **`ComponentModel`, not "Device".** The single base for passive **and** active parts. "Device" is
  reserved for its RF meaning (an active part). A resistor and a FET are both `ComponentModel`s.
- **Three layers flow one way:** design → elaboration → numeric. Nothing in `src/Core` may depend
  on `src/Engine` or `src/Ui`. The GUI edits the design layer; it never hands a design-layer object
  to the engine — always elaborate first.

## Expression engine — non-negotiables
- **Never string substitution.** Real tokenize → Pratt-parse → AST → evaluate. (This replaces the
  prototype's NSExpression/NSPredicate/regex path.)
- **Parse once, evaluate many.** Cache the AST on the owning `Variable`/parameter/SDD/`Measurement`;
  the SDD hot path (per time sample × Newton step × sweep point) must allocate no garbage.
- **Kinded values: Real / Complex / Bool.** A resolved variable or parameter is Real **or** Complex
  (not forced complex) — most component values are Real, impedances Complex. Ordering comparisons
  are **real-only**; SDD equations are real-only time-domain (no `j`).
- **Cycle detection is mandatory** and spans variables, cell-parameter defaults, and instance
  overrides. Report the offending chain (e.g. `a → b → a`); never recurse without the in-progress
  guard. Fixture `recursion.log` (a valid multi-hop chain → resolves to `2`) and a synthetic
  cyclic fixture (`a=b, b=a` → must be reported) are the Phase-1 tests.
- **Scope is structural,** not string-keyed: globals at the base; each cell instance pushes a scope
  binding parameters. **Override expression evaluates in the PARENT scope; default in the cell's
  own scope.** Resolution is local-then-global; no upward/sideways reach.

## Elaboration — string-param devices (do not Eval their non-numeric params)

`Elaborator.ResolveParameters` dispatches to a per-device resolver for primitives whose overrides
must NOT be expression-evaluated. Currently: **SDD**, **Z_Port**, **V_1Tone/V_nTone**, **P1Tone**,
and **SnP** (brief-snp-fixes, 2026-06-17). Each stores its string-valued params as `new Value(raw)`
(verbatim), and only evaluates genuinely numeric overrides via `_evaluator.Eval()`.

**Why:** a file path (`File=/Users/…/x.s2p`) is not an expression — the leading `/` crashes the
expression parser at position 0. Similarly, `InterpMode=Cubic` and `ExtrapMode=NearestEdge` are
string-valued enum names, not numeric expressions.

**Rule:** when adding a new primitive device that has string-valued params (file paths, enum names,
equation strings), add a `ResolveXxxParameters` dispatcher in `Elaborator.cs` that stores those
params raw. The generic `ResolveParameters` fallback evaluates ALL overrides — never let a string
param fall through to it.

## Elaboration
- Flatten depth-first; primitives emitted, cells recursed with a fresh scope.
- Resolve every expression to a kinded value (units applied here), with cycle detection.
- Number nodes: ports map onto parent nets; internal nets uniquified by instance-path prefix;
  **ground = 0**. Node names carry the instance path (`X1.drain`) — this is what measurement paths
  resolve against, so keep it stable.
- Compute `NonlinearComponents` / `NonlinearNodes` (the HB partition seed) here.
- Numbering is stable + unique; the fill-reducing permutation for the solve is the **engine's** job,
  not the elaborator's.

## `.cnl` reader + writer
- Vendor-neutral hierarchical netlist; maps directly onto the design layer. JSON carries the same
  logical model.
- `CnlReader` (existing) parses `.cnl` text to `TestBench`.
- `CnlWriter` (Phase 6e Step 2, `src/Core/Netlist/CnlWriter.cs`) is the exact inverse: emits a
  `TestBench` as `.cnl` text that `CnlReader` round-trips back to an equivalent `TestBench`.
  Handles: variables, standard instances (R/L/C/Port/SnP…), SDD (equation format), Z_Port (Z[i,j]=
  format + N-or-N+1 rule), Tuner (skips synthetic TunerName), typed analyses (HB/Loadpull/etc.),
  measurements, raw directives verbatim, and a top-level `labelednets <name> <name> …` directive
  recording which nets came from user-placed schematic labels (see below). Gate: 10 round-trip tests
  in `tests/Core.Tests/Netlist/CnlWriterTests.cs`, all green.
- **`labelednets` directive (brief-cnl-labelednets-provenance, 2026-06-16).** `CnlWriter` emits a
  top-level `labelednets n1 n2 …` line (sorted, stable) from `tb.LabeledNets` when any labeled nets
  exist; `CnlReader` parses it back into `tb.LabeledNets`. This is what lets the node-picker
  labeled-filter survive the schematic→`.cnl`→CnlReader run path. `HbLabeledNodesCubeTests` (T4/T6)
  previously only exercised the in-memory injection path and missed the `.cnl` round-trip gap; T7
  (`EndToEnd_SchematicCnl_EmitsLabeledNodesCube`) is the regression guard for the full round-trip.
- **Skip unknown header/comment lines** so real-world exports import cleanly; committed fixtures are
  clean `.cnl`.
- The **VendorA importer** (a separate front-end, Phase 2) translates legacy `if…then…else…endif`
  → canonical `if(cond,then,else)` at import time, so the engine grammar stays single-form. (Native
  `.cnl` only ever uses the canonical form.)
- Analysis/measurement **directive grammar is deliberately deferred** (data-model §10) — nail it
  down before implementing those lines; the circuit/cell/variable lines are settled.
- **SnP / frequency-domain N-port reference node (N-or-N+1 rule).** A frequency-domain N-port
  component (SnP block, impedance block, TLIN, user freq model) lists either **N nets** (each port
  referenced to ground, node 0) **or N+1 nets**, in which case the **last net is the common
  reference node** for all ports (the floating-block case). The reader validates node count against
  `NumPorts`: `== NumPorts` or `== NumPorts + 1`, else error. The reference node is recorded on the
  component (a `ReferenceNet`, ground when absent) and the model uses it in its own stamp
  (linear-engine §4.1). This rule does **not** apply to 2-terminal R/L/C. SnP line fields:
  `File` (relative paths resolved against the `.cnl` file's dir; absolute as-is), `Type`
  (v1: `"touchstone"` only — **hard-error on any other value**; extensible, future `"datacube"`),
  `InterpMode` (`"spline"` default | `"linear"`), `InterpDom`, `ExtrapMode` (`"clamp"` default |
  `"extrapolate"`); `Temp` and passivity/noise flags are parsed and ignored.

## Phase 1 deliverable — COMPLETE (2026-05-30)
Expression engine + elaboration + `.cnl` reader, validated by the cycle-detection fixtures.
**Phase 1 does not need RfCore** — it is built and tested standalone.

### Implementation notes (reality vs. design)
- `if(...)` keyword is handled in the **Parser** (produces `ConditionalExpr`), not in `Evaluator.EvalCall`.
- `Evaluator.InjectResolved(scopeDebugName, name, value)` lets the Elaborator inject
  pre-resolved override values into the memo cache without round-tripping through `ToString()`
  (avoids breakage on Complex values).
- Left-assoc operators use `rbp = lbp + 1` in the Pratt table; right-assoc (`^`) uses `rbp = lbp - 1`.
- Analysis/measure `.cnl` lines → `RawDirective { Kind; RawLine }` on `TestBench`. Typed `Analysis`
  subclasses (`SParameterAnalysis`, etc.) are defined but not populated by the Phase-1 reader.
- Top-level instances in a `.cnl` (outside any `define` block) live directly on `TestBench.Instances`
  (no synthetic wrapper cell).
- `ComponentModel.Stamp` uses `object` placeholders for `mna` and `c` — types resolved in Phase 2.

## Phase 2 Step 1 deliverable — COMPLETE (2026-05-31)
SnP/Touchstone block end-to-end: `.cnl` reader parses `SnP:` lines, elaboration creates `SnpModel`,
`SnpModel.Stamp` performs Z-expansion into a real `MnaSystem`. RfCore is now a dependency of Core.
Gate: 4 tests in `tests/Engine.Tests/Linear/SnpStampTests.cs` — all green.

### Implementation notes (reality vs. design)
- `ValueKind.String` was added to `Value` (storage-only; no operators, no coercions). This is the
  mechanism for SnP configuration params (`File`, `Type`, `InterpMode`, `ExtrapMode`). The tokenizer
  lexes `"..."` as `StringLiteral`; the parser produces `StringLiteralExpr`; the evaluator returns
  `Value.String(...)`. A String value is a type error in any arithmetic context.
- `Instance.RefNetBinding` (nullable string net name) carries the N+1 floating reference node for
  frequency-domain N-ports. `null` = ground. The elaborator resolves it to `ElaboratedComponent.ReferenceNode`.
- `IMnaContext` interface lives in `CircuitRF.Core` (not Engine) because `ComponentModel.Stamp` must
  be able to call it. `MnaSystem` in `CircuitRF.Engine` implements it.
- `ComponentModel.Stamp(IMnaContext mna, ElaboratedComponent c, double omega)` — `object` placeholders
  replaced with real types.
- The elaborator now resolves parameters **before** creating the model (order swapped). This allows
  `ComponentModelFactory.TryCreate(typeName, params)` to construct `SnpModel` with its file path
  and settings baked in.
- `CnlReader` stores `_sourceDirectory` (from `ReadFile`); relative `File=` paths are resolved to
  absolute at parse time and re-stored in the `ParameterAssignment.Expression` as a string literal.
- `TokeniseLine` in CnlReader now respects quoted regions so `key="value with spaces"` stays one
  token.

## Phase 3 deliverable — COMPLETE (2026-06-01)
AD engine + SDD device, validated by the hero GaN HEMT bias.

### Step 1: AD engine
New files in `src/Core/Expressions/`:
- `IAdScalar.cs` — static-abstract interface (C# 11 generic math) for real-only scalar operations
- `Dual.cs` — forward-mode dual number, N-wide gradient via `[InlineArray(8)]` (MaxN=8, allocation-free)
- `SddScalar.cs` — thin `double` wrapper implementing `IAdScalar<SddScalar>` (FD / plain-eval path)
- `AdWarnings.cs` — thread-static model-name context for domain-clamp warnings to `Console.Error`
- `SddEvaluator.cs` — generic `Eval<T>(Expr, bindings, modelName)` — ONE tree-walk, two scalar types
- `AstWalker.cs` — collects all `RefExpr` names from an AST (used by Elaborator for SDD scope injection)
- `FiniteDiff.cs` — central-difference gradient helper (AD oracle + production FD fallback)

Gate (tests/Core.Tests/Expressions/AdVsFdTests.cs): AD of the hero i2 at (v1=−3.05, v2=48) matches
central FD to ≥4 sig figs: gm = ∂i2/∂v1 ≈ 62.4 mS, gds = ∂i2/∂v2 ≈ −9.45 µS (negative — correct).

### Implementation notes (reality vs. design)
- `SddEvaluator.Eval<T>` is a generic local-function nest inside a single static method. Conditions in
  `ConditionalExpr` are evaluated by extracting `T.ValueOf()` (the scalar) and comparing doubles —
  AD takes the active-branch derivative, the other branch is not evaluated.
- `Dual.Exp` caps argument at 700 (preventing overflow); `Dual.Log`/`Sqrt` clamp with warn.
  Together, `log(exp(x)+1)` (softplus) evaluates correctly for all x — large x gives ≈ x, very
  negative x gives ≈ exp(x). No special softplus pattern needed.
- SDD equation expressions **may contain whitespace** — the SDD line parser uses bracket-depth-zero
  boundary detection instead of the general whitespace tokenizer (Phase 3 follow-up, 2026-06-02).
  Boundary: next `I[p,w]=`, `Q[p,w]=`, etc. at paren-depth 0. Multiple assignments on one line OK.
  Backslash line-continuation (`\` at end of line) is also supported.
- `Dual.NMax(a, b)`: picks the larger N for binary operations; constants have N=0 (zero gradient).

### Step 2: SDD device
New file `src/Core/Devices/SddModel.cs`:
- `ComponentModel` subclass, `ModelKind.Nonlinear`, `Stamp` is a no-op.
- Constructor receives cached equation ASTs + resolved scope-variable dict.
- `Evaluate(in PortVoltages v)` calls `SddEvaluator.EvalDual` for each port equation → (i, q, dg, dc).

`ComponentModelFactory` change: "SDD" added to `_parameterizedTypes`. `CreateSddModel` parses
`Value.String` equation entries, validates `F[]/C[]/w≥2` hard errors, skips `In[]/Nc[]` noise entries.

`Elaborator` change: `ResolveSddParameters` special-cases SDD — stores equation strings as
`Value.String`, walks each equation AST to collect scope-variable references, resolves them from scope,
and injects them as `Value.Real` in the resolved-params dict. The factory then sees both strings and
resolved numbers.

Gate (tests/Core.Tests/Devices/SddModelTests.cs): hero SDD parses; `Evaluate` at (−3.05, 48) returns
i2 ≈ 49.11 mA, i1 = −61 mA, gm ≈ 62.4 mS, gds ≈ −9.45 µS (negative).

### New device: VoltageSourceModel
`src/Core/Devices/VoltageSourceModel.cs` — Group-2 branch-current element. Stamps Va − Vb = V
(branch constraint + KCL). Parameter `V=`. Required for bias sources in the DC hero circuit.
Registered as type `V` in `ComponentModelFactory`.

## Enabled semantics — AnalysisChain (brief-sweep-revamp-2-dispatch, 2026-06-17)

`AnalysisChain` (`src/Core/Design/AnalysisChain.cs`) is a pure, framework-free resolver that
honors `Analysis.Enabled` when walking a parametric-sweep chain.

- **`ResolveEffectiveInner(innerName, tb)`**: descends from `innerName`, skipping disabled
  `ParametricSweepAnalysis` nodes (collapse), until it reaches either an enabled sweep or any base
  analysis. Used by `ParametricSweepEngine.Run` in place of the former raw name lookup.
- **`ResolveEffectiveTop(root, tb)`**: from the chain root, skips disabled outer sweeps to the
  first thing that actually runs. Used by `SchematicRunService` dispatch.
- **`IsChainRunnable(top, tb)`**: true only if the chain eventually bottoms out at an enabled base
  analysis. Used by `SchematicRunService` to skip dead chains (disabled base).

Semantics:
- Disabled sweep → collapses (its axis is dropped; its inner runs in its place). Spec is untouched.
- Disabled base → whole chain is inert (nothing runs, no result emitted).
- Both sweeps disabled → effective top is the base; runs as a plain single-point analysis.

Gate: 9 tests in `tests/Core.Tests/Design/AnalysisChainTests.cs` (pure); 4 integration tests in
`tests/Engine.Tests/Parametric/ParametricSweepEnabledTests.cs`. Build 0W/0E; 1629 total pass.

Stage 3 (unified editor UX with per-axis Enabled + reorder) follows.

## `.cnl` enabled flag + Spec persistence (brief-sweep-revamp-1-persistence, 2026-06-17)

- **`enabled=false` in `.cnl`**: `CnlWriter` appends `enabled=false` to every sub-line of any
  analysis whose `Enabled` is false (multi-segment S-param gets it on each segment line). `CnlReader`
  has a shared `ParseEnabledToken` helper wired into all six typed parsers (DC, HB, S-param,
  parametric sweep, loadpull, loadpull-pursuit). Absent token → `Enabled = true` (default). Gate:
  5 tests in `tests/Core.Tests/Netlist/CnlEnabledTests.cs`.
- **Sweep Spec round-trip through `.csch`/`.canl`/clipboard**: `CschAnalysis` DTO now carries
  `PsaMode`, `PsaStart`, `PsaStop`, `PsaStepOrCount`, `PsaKind` for spec-form sweeps. `ToDto`
  prefers the Spec fields (omits `PsaValues`) when `Spec` is non-null; `FromDto` has a Spec arm
  (tried first) and a list arm. Explicit-list PSAs still round-trip unchanged. Gate: 7 new tests in
  `tests/Ui.Tests/AnalysisSerializationTests.cs`.

## Parametric sweep — Start/Stop/Step|Npts (brief-parametric-sweep-stepcount, 2026-06-16)

`SweepExpander` and `SweepAxisMode` moved from `src/Ui/Schematic/` → **`src/Core/Design/SweepExpander.cs`** (no Avalonia deps) so the CNL reader can use them without violating the Core→UI firewall.

`SweepSpec` (in `Analysis.cs`) redesigned: `{ Start, Stop, StepOrCount, Mode: SweepAxisMode, Kind: SweepKind }` — no `Variable` field. `ParametricSweepAnalysis` gains a **spec constructor** that expands eagerly (populates `SweepValues`) and stores `Spec` for `.cnl` round-trip fidelity; the existing array constructor sets `Spec = null`.

**CNL reader** (`TryParseParametricSweepDirective`): now accepts both `Values=v1,v2,…` (list; `Spec=null`) and `Start= Stop= (Step= | Npts=) [log | log=true]` (spec; `Spec` retained). Bare `log` keyword detected via `HashSet<string> bare` (same pattern as SParam parser).

**CNL writer** (`FormatParametricSweepAnalysis`): emits compact `Start= Stop= Step=|Npts=` form when `psa.Spec != null`, falling back to `Values=` list for array-only PSAs.

6 gate tests in `tests/Core.Tests/Netlist/SweepSpecCnlTests.cs`: StartStopStep (121 pts), StartStopNpts (7 pts linspace), Log (4 decades), log=true keyword, Values regression, round-trip compact form. Build 0W/0E; 260 Core.Tests pass.

## Vdc — DC voltage source (brief-vsource-vdc-fix, 2026-06-16)

`VoltageSourceModel` **deleted**; replaced by `VdcModel` (`src/Core/Devices/VdcModel.cs`).

**Root bug:** legacy `V:` CNL lines produced `Vac=` parameter names, but `VoltageSourceModel.Stamp` only read the `"V"` key → voltage silently stamped as 0 V at DC.

**Fix architecture:**
- `VdcModel` stamps at DC only: `Math.Abs(omega) < OmegaTolRads (1 rad/s)` → stamps `Vdc` value; all other ω → stamps zero. Reads `"Vdc"` param (alias `"V"`). Keeps `LastBranchIndex` for HB linear extractor.
- **CnlReader backward compat** (`ParseInstanceLine`): if `typeName == "V"` (OrdinalIgnoreCase) → remap to `"Vdc"`. Also normalizes `Vac=` or `V=` param names to `Vdc=` for any `Vdc` instance (if no `Vdc` override already present). Old `.cnl` files with `V:` sources load and simulate correctly with no manual conversion.
- **`ToneSourceModel`**: fixed 0-Hz tone superposition — at ω=0, now accumulates `_currentVdc` plus all tone phasors whose `FreqHz ≈ 0` into the source voltage. `GetZeroHzToneWarnings(path)` returns a list of warnings (one per zero-Hz tone with non-trivial phasor); called by the Elaborator which routes them into `netlist.AddWarningOnce`.
- **`HbLinearExtractor`**: updated `VoltageSourceModel` references at lines 390 and 547 → `VdcModel`.
- **`ComponentModelFactory`**: `"V"` factory entry → `"Vdc"` → `new VdcModel()`.

8 gate tests: 4 Engine.Tests (`tests/Engine.Tests/Devices/VdcComponentTests.cs`) + 4 Ui.Tests (`tests/Ui.Tests/VdcComponentTests.cs`). Build 0W/0E; 1419 total tests pass.

## VAR variable component — design note
`NetExtractor` in `src/Ui` routes `SymbolKind.Var` component parameter rows into `Cell.Variables` (sub-cell) or `TestBench.GlobalVariables` (testbench top). No Core change was needed: `Elaborator.BuildGlobalScope` already binds `tb.GlobalVariables` and `BuildCellScope` already binds `cell.Variables`, so per-cell isolation and HB sweepability are automatic. VAR never appears as an `Instance` or `ElaboratedComponent`; its `EngineReference` sentinel is `"VAR"` (not a factory primitive).

## CNL generic instance parser — unit token handling (brief-unit-token-phantom-nodes, 2026-06-16)

The CNL generic instance parser (`ParseInstanceLine` in `CnlReader.cs`) now recognises
**identity/measurement unit tokens** — `V`, `A`, `W`, `dBm`, `dB`, `kV`, `mV`, etc. — as
consumable trailing units after a `key=value` param token. Previously, only linear-scale units
(in the `Units._scales` table) were consumed; tokens like `V` and `dBm` that are absent from that
table leaked into the net list as phantom "net" entries, shifting all subsequent node indices.

**Root cause fixed:** A P1Tone line `Pavl=Pin dBm` or a Vdc line `Vdc=-3.05 V` placed `dBm`/`V`
in the net section because `Units.IsKnown` is intentionally linear-scale-only (see `Units.cs`
comments). The fix adds `Units.IsRecognizedUnit(u)` = `IsKnown(u) || _identityUnits.Contains(u)`,
where `_identityUnits` is a fixed allow-list of valid-but-identity units. This predicate replaces
`IsKnown` in both the separate-token path and `TrySplitGluedUnit` in `ParseInstanceLine`.

**Position gate (safety):** the consume check fires **only inside the `key=value` param branch**,
never in the leading net section. A net token (even one named `V`) can never appear in that
position, so the single-letter `V` is unambiguous.

**Evaluator:** `Evaluator.ApplyUnit` extended to treat identity/measurement units as scale = 1.0
(value already in base unit) rather than throwing `Unknown unit`. Linear-scale units are unchanged.

**Node-picker effect:** with phantom nodes removed, the V-cube node axis contains only real
user-named nets. The existing `__`-prefix filter hides internal engine-minted nodes. No additional
picker code was needed — the cleanup is fully from the parser fix.

5 gate tests: `CnlReader_P1Tone_NoPhantomUnitNets`, `CnlReader_Vdc_NoPhantomUnitNets`,
`CnlReader_DoesNotEatRealNet`, `GluedUnit_StillSafe` (in Core.Tests) and
`Hb_Vout2_NonZeroFundamental` (Engine.Tests — verifies back-solved linear node is non-zero
after the index-shift bug is eliminated).

## SDD single-index equations + net-arity validation (brief-sdd-single-index-nets, 2026-06-16)

SDD equations accept **single-index** sugar in both `CnlReader` and `ComponentModelFactory`:
- `I[p]=expr` ≡ `I[p,0]` (port-p current); `Q[p]=expr` ≡ `I[p,1]` (port-p charge).
- Two-index `I[p,w]` and `I[p,1]` (legacy charge form) still work unchanged.

**CnlReader**: `SddAssignmentHeader` regex extended to `\d+(,\d+)?` — single-index `I[1]=` is now a valid boundary marker, so equation fragments no longer leak into the net list as phantom nodes. `ParseSddLine` also strips any `key=value` tokens in the net section (e.g. `Ports=2`) into parameter overrides rather than treating them as net names.

**Elaborator**: `RxSddEquation` extended to `^[IFCQi][^\[]*\[` to pass `Q[p]` single-index through to the factory. Odd net count (not divisible by 2) now throws: `"SDD '<inst>': expected an even number of nets (2 per port: +,−); got N."` — no more silent `portCount = N/2` truncation.

**Factory**: `RxCurrentEq1 = ^I\[(\d+)\]$` and `RxChargeEq1 = ^Q\[(\d+)\]$` handle single-index forms. The shared `ValidateAndBind` helper gives a clear error when an equation references a port index beyond the net count: `"equation references port P but only K ports of nets were given (need 2P nets for a P-port SDD)"`.

**User-facing correction**: `SDD:X1  Vin 0  Vout 0  I[1]=…  I[2]=…` — 4 nets (each port referenced to ground). `_v1 = V(Vin)−V(0)`, `_v2 = V(Vout)−V(0)`.

**Node-picker (deferred)**: `n1/n2/n3`-style auto-named nodes are real user nets — filtering them from the axis combo requires a scope decision (hide `^n\d+$`? user toggle?). Not implemented here.

7 gate tests: 6 in `tests/Core.Tests/Devices/SddSingleIndexTests.cs` (net/equation split, `I[p]` binds current, `Q[p]` binds charge, two-index regression, odd-net error, port-ref-beyond-nets error) + 1 in `tests/Engine.Tests/HarmonicBalance/SddSingleIndexHbTests.cs` (full HB sweep with single-index SDD, Vout fundamental non-zero, no phantom nodes in axis).

## Ask before
- Changing the `.cnl` or JSON format (round-trip + interop).
- Changing the scope/binding rule or the kinded-value model (ripples into the engine and SDD).