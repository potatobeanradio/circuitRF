# Brief PM1 — running physics-based compact models correctly

**Status:** proposed · **Date:** 2026-09-03 · **Companion:** PM2
(`project-brief-physics-model-placement-ux.md`), which is about *clicks*. This one is about
*correctness*, and PM2 depends on it. Do this one first, or alone.

**The owner's question was: can circuitRF run two openly published physics-based GaN HEMT
compact-model families, and is it as simple as pointing the VerilogA component at a `.va` file?**

**The backend answer: yes, it runs them — after three defects this model shape hits and the existing
fixtures cannot.** (The `.va`-vs-`.osdi` half of the question is PM2's.)

Model families are referred to as **Family S** (surface-potential core, field plates, trapping
nodes) and **Family V** (virtual-source core). Neither is named, nor is any version, nor any path
outside the repository — see §6.

---

## 1. What these models are, structurally

Both are ordinary Verilog-AMS compact models of exactly the shape this repository anticipated in
`docs/PRD.md` §6.1 and `docs/Development_Plan.md` §3.4:

| | Family S | Family V (full) | Family V (compact variant) |
|---|---|---|---|
| Source size | ~2,300 lines | ~1,900 lines | ~430 lines |
| External terminals | 5 — drain, gate, source, bulk, **thermal** | 5 — same | 3 electrical, thermal node **internal** |
| Internal nodes | ~10 base, +8 with field plates enabled | ~20 | 4 |
| Disciplines | `electrical` + `thermal` | `electrical` + `thermal` | `electrical` + `thermal` |
| Charge storage | `ddt(q)` throughout | `ddt(q)` throughout | `ddt(q)` |
| Op-vars | ~40, via `ddx` | ~30 | few |
| Noise | `white_noise` ×5, `flicker_noise` ×1 | present | present |
| `$simparam` asked for | `gmin`, `minr` | `gmin`, `minr` | `gmin` |
| `$port_connected` | **yes**, on the thermal terminal | no | no |

Nothing there is outside the OSDI ABI the shipped worker already hosts.

**They contribute their own thermal RC.** Both write, on the thermal node, a conductance `1/rth`
and a `ddt(cth · T)` reactive term, and add `$temperature` to it themselves — so the node carries
the **rise**, referenced to the thermal ground. That is the *opposite* convention from
`docs/design/pdk-external-devices.md` §5.1, which assumes a compiled electrothermal model contains
no RC of its own and that the host must build one to an ambient source. §5.1 is not wrong — it was
measured on two other families — but it is **not universal**, and §2 F3 is what has to happen.

---

## 2. Findings

### What already works, and the evidence for it

Not assumed — each is code on the path a placed component takes:

- **The component.** `SymbolKind.VerilogA` (`src/Ui/Schematic/ComponentTypeRegistry.cs:408`,
  parameters at `:1548`): `File`, `Model`, `Pins`, plus any of the model's own parameters.
- **Resolution with no kit and no configuration.** `VerilogAFileResolver` composes a provider name
  from the absolute path, so two instances of one file share one worker and two files get one each.
  Built into the chain, survives `ClearResolvers`.
- **Introspection fills in `Model` and `Pins` from the file** through the *same* provider Run uses
  (`src/Ui/Schematic/VerilogAModelIntrospection.cs`), so the dialog cannot promise a device Run
  refuses.
- **The ABI marshalling is the right one.** Residual/Jacobian split resistive vs reactive maps onto
  `Evaluate`'s `(i, q, dg, dc)` one-to-one; `load_spice_rhs_*` deliberately unused.
- **Node collapsing is read from the model at `create`** (`tools/osdi-worker/osdi_worker.c:607`),
  including collapsed-to-ground; the elaborator merges the groups with an external pin always
  winning.
- **A node the model writes no equation for is measured and resolved**, or the run stops with a
  sentence rather than burning the iteration budget (`src/Core/Elaboration/Elaborator.cs:1330-1378`).
- **DC, S-parameters and HB all take an external device today.** `NonlinearDcEngine` has a thermal
  survey (`:360`, `:508`); `SParameterEngine` runs one serially by design (`:195`, with the reason
  stated); HB consumes the same `ComponentModel`.
- **A real five-terminal compact model is already gated on** — `CompiledModelValidationTests`,
  `VerilogATransistorSanityTests`, both env-gated on `CIRCUITRF_OSDI_MODELS`, both skipping with a
  reason when absent.

**So the backend is built. What follows is where this particular model shape breaks it.**

### F1 — the worker always claims every terminal is connected

`osdi_worker.c:444` and `:585` pass `d->num_terminals` to `setup_instance` unconditionally. That
argument is the count of terminals the *instance* connects. Consequences, worst first:

1. **`$port_connected` is always true.** Family S branches on exactly this to decide whether to
   ground its own thermal node. With self-heating switched off *and* the terminal claimed connected,
   the model writes **no equation at all** for that node — the `Temp(dt) <+ 0.0` that would have
   grounded it sits in the unreachable branch.
2. A five-terminal model cannot be placed as a four-terminal part, which is the ordinary way to say
   "I do not want a thermal pin on my schematic".

`Pins` is already the number the user states. It has to reach `create`.

### F2 — the OSDI worker never reports a node's quantity kind, so every thermal path is dead

`src/Core/Devices/External/DeviceWorkerProvider.cs:466` reads `quantityKind` from the node report and
classifies `"thermal"`. `osdi_worker.c`'s node emission (`:331-343`) writes `index`, `external` and
`label` — and nothing else. So for **every** OSDI model, every node is `NodeQuantityKind.Electrical`,
and each of these is unreachable:

- `Elaborator.PinUnreferencedThermalNodes` (`:167`) — holds an unconnected thermal terminal at
  ambient instead of leaving a floating node with no DC solution.
- `NonlinearDcEngine.ReportThermalNodes` (`:360`) — catches a thermal network referenced to
  electrical ground, the silent several-percent error §5.1 describes.
- The thermal exclusion at `Elaborator.cs:406`.

The information is available: `OsdiNode` carries `units` and `residual_units`
(`tools/osdi-worker/osdi.h:126`), `"K"`/`"W"` for a thermal node against `"V"`/`"A"` for an
electrical one. **This is a small change in one C function and it is the highest-value item in
either brief.**

### F3 — §5.1's thermal convention does not hold for these families, and the mitigations may misfire

With F2 fixed, the thermal machinery starts running against models that **do** carry their own RC.
Two behaviours must be established by measurement, not by reading:

- `PinUnreferencedThermalNodes` must **skip** them. `SelfReferencedThermalNodes` already tests for a
  positive conductance on the node's own row, which both families supply when their thermal
  resistance is non-zero (the default in both). Expected correct already — but if it is not, pinning
  the node at ambient makes the model compute `ambient + ambient + rise`: finite, plausible, wrong.
- `ReportThermalNodes` must **not warn** about them. Its test is "the reference is zero while the
  ambient is not", which is precisely how a self-contained model's rise-carrying node looks. A
  warning on every correctly-modelled part is worse than no warning at all.

Neither can be settled without a compiled artefact; both are cheap once there is one.

### F4 — one requested `$simparam` is absent

Both families ask for `"minr"`; the worker's table (`osdi_worker.c:359`) offers `gmin`, `imax`,
`imelt`, `scale`, `shrink`, `tnom`, `simulatorVersion`, `sourceScaleFactor`, `iteration`. Both
families carry a sane fallback (1 mΩ), so this is **exactness, not a fix** — recorded so it is not
re-discovered as a mystery.

---

## 3. Phases

### P1 — the worker tells the truth about nodes and terminals *(small, highest value)*

1. Emit `"quantityKind":"thermal"` for a node whose `units` is `"K"` (and/or whose `residual_units`
   is `"W"`), electrical otherwise. **Report the raw strings alongside**, so a discipline nobody
   anticipated is visible rather than silently electrical.
2. Accept an optional `connectedTerminals` on `create` and pass it to `setup_instance`, defaulting
   to `num_terminals`. Refuse a value above the declared count or below 2, by sentence.
3. Send the instance's own pin count from `ComponentModelFactory`'s VerilogA path — it is already
   holding `Pins`.
4. Add `"minr"` to the `$simparam` table.

**Gate — and this is the phase's best property: it needs no proprietary artefact.** Extend
`tools/fake-osdi-model` with a thermal node and a `$port_connected` branch; `OsdiWorkerTests` then
asserts the kind and the terminal count round-trip, and `verify.py` still passes end to end.

### P2 — validate the two families across DC, S-parameters and HB

Extend `CompiledModelValidationTests` and `VerilogATransistorSanityTests` (already env-gated,
already skipping with a reason) with a five-terminal electrothermal case, then add:

- **DC** — a common-source stage: on above threshold, off below, drain current saturates,
  transconductance positive. Same oracle shape as the existing sanity test; no reference simulator.
- **S-parameters** — |S21| > 1 in the band the parameter set was fitted for; passive at zero drain
  bias; small-signal `y21` at DC agreeing with a finite difference of the DC sweep. Self-consistent,
  so still no reference simulator.
- **HB** — 1 dB compression exists and is monotone in drive; harmonics fall off; **the DC operating
  point HB converges to matches the DC engine's own at zero drive**. That third one is the real
  check: it catches a charge/current mix-up every static test passes.
- **Self-heating actually does something** — sweep the model's own thermal resistance and confirm
  the drain current moves. `docs/design/pdk-external-devices.md` §5.1 records a family where it did
  not, and that is exactly the vacuity this check exists to refuse.

**Gate:** all green on a machine with artefacts; all *skipped with a reason* on one without, and
report which of the two happened rather than reporting "green".

### P3 — write down what circuitRF does not support, **in the code**

Per the owner's instruction, each omission gets a comment where it lives, not only in this brief.

| Feature | Why not | Where the note goes |
|---|---|---|
| `white_noise` / `flicker_noise` | circuitRF has **no noise analysis** — no noise engine exists and none is planned for v1. OSDI exposes noise through a separate `load_noise` the worker never calls, so these contributions are structurally absent rather than mishandled. | `osdi_worker.c`, beside the load functions |
| Op-vars (operating-point outputs) | Read by the worker and deliberately excluded from the settable list — they are model *outputs*. Surfacing them as read-back quantities is worthwhile and is **not** in either brief. | `emit_describe`, extending the note already there |
| Aging / degradation parameters | One family ships an aging parameter set. There is no aging analysis and no stress history to feed it; the parameters are settable like any other and simply do nothing. | `ComponentModelFactory` VerilogA path |
| `$strobe` and friends | The worker hosts no text output channel, so a model that strobes a diagnostic loses it. Worth stating because one family strobes exactly the misconfiguration F1 creates. | `osdi_worker.c` sim-params block |
| Multiplicity (`$mfactor`) | Not passed. A user scales by placing instances or through the model's own width/finger parameters. | `ComponentModelFactory` VerilogA path |
| `$limit` and limiting functions | The worker installs no limiting; convergence is circuitRF's continuation's problem. Recorded because a model expecting host limiting converges *differently*, not wrongly. | `osdi_worker.c` |

### P4 — correct §5.1

Once F3 is measured, `docs/design/pdk-external-devices.md` §5.1 should say that **some** compiled
electrothermal models carry their own RC and some do not, and state how circuitRF tells them apart —
the mechanism (`SelfReferencedThermalNodes`, a positive conductance on the node's own row) already
exists and is already the right one. This is a doc correction, not a design change.

---

## 4. What this does not do

- No hand-port of any model to C#. `docs/PRD.md` §6.1 decided against it; nothing here reopens it.
- No noise analysis.
- No compiler, no vendored model source, no committed `.osdi` (see PM2 for the compile question).
- No change to the kit/PDK path. This is the *user supplies one file* path — no kit, no manifest,
  no workspace.

---

## 5. Test posture

Per the repo's standing rule: run the suite **once** and read the TRX. The projects this can reach
are `Core.Tests`, `Engine.Tests` and `Ui.Tests`; `tools/osdi-worker` has its own `build.sh` and
`verify.py`, which are not part of `dotnet test` and must be run separately. No new
`Category=Benchmark` timing test — assert the structural property (a node's reported kind, a
terminal count) rather than a duration.

---

## 6. Naming and licensing posture

- **No model family, version, author, institution, supplier or external file path enters the
  repository from this work.** Families are described structurally, as §1 does.
- **Pre-existing exception, flagged rather than changed:** `docs/PRD.md` §6.1/§11 and
  `docs/Development_Plan.md` §3.4 already name one family by name and abbreviation, as the stated
  rationale for this whole backend, and `CLAUDE.md:385`/`:401` repeat it. Two tests and
  `tools/osdi-worker/README.md` name an open MOSFET model family as a fixture file name. These
  predate this brief; whether to scrub them is the owner's call and is not assumed here.
- **Citing a published paper or thesis is approved** by the owner, and is how a physics claim should
  be sourced if one is needed. Nothing in this brief as written requires one.
- The model sources are the user's to obtain under their own licences. circuitRF ingests none.
