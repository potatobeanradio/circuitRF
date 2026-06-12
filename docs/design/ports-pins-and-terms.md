# Ports, Pins, and Terms — connectivity vs. excitation

Status: design — **decisions locked** (see "Decisions" below). Defines how a cell's interface
ports relate to the schematic and symbol, what a Term is, how S-parameter ports get their
reference impedance, and how differential ports work. Written to kill the "two kinds of
port" confusion before any of it is wired.

The driving rule (consistent with the rest of circuitRF): **one concept = one source of
truth, enforced at input.** The confusion here comes from one English word — "port" —
naming two unrelated things. We separate them by role and never overload the word in the UI.

---

## TL;DR (the three concepts)

| Concept | Layer | Role | Electrical effect | Netlist form |
|---|---|---|---|---|
| **Interface Port** (realized by a **Pin**) | cell / schematic / symbol | hierarchical connectivity — how a cell connects to its parent | NONE (a named terminal; merges nets on instantiation) | subckt terminal |
| **Term** | testbench schematic | S-parameter excitation/measurement | defines an S-param port (Num + reference Z) | `Port:` object |
| **S-parameter port** | analysis | the numbered port the S-matrix is defined over | — (derived from Terms) | — |

- A **cell** has N **interface ports** (the RF meaning: "a 2-port amplifier"). Each is realized
  by a **Pin** in the symbol and a **Pin** in the schematic. Pure connectivity. `Cell.NumPorts`.
- A **Term** is a *testbench* element (two terminals `+`/`−`, params `Num` + `Z`) that says
  "this is S-parameter port `Num`, with reference impedance `Z`." It is NOT a hierarchical
  terminal and must never live inside a reusable cell.
- The S-parameter **engine** builds its port set from the Terms (and legacy `Port` objects) at
  the top of the analysis.

If you remember one thing: **Pins connect. Terms excite.** They coincide *spatially* in a
standard DUT testbench (you put a Term on each of the DUT's pins) but they are different objects.

---

## Why the confusion exists (and what the code does today)

1. **One word, two jobs.** "Port" means both "a cell's I/O connection" (connectivity) and
   "an S-parameter measurement port" (excitation). EDA tools (including the VendorA tool) overload it.
2. **The netlist calls a Term a "Port".** `testdata/Hero1/hero1.cnl`:
   ```
   Port:Term1  p1 0  Num=1 Z=50 Ohm
   ```
   Reference = `Port`, instance = `Term1`, nodes `p1`(+)/`0`(−), params `Num` + `Z`. This is the
   VendorA-tool pattern: the schematic component is a **Term**; the netlist object is a **Port**.
   We keep this netlist spelling (engine compatibility) but call the *component* a **Term** in the UI.
3. **Current code conflates them.** `src/Core/Devices/PortModel.cs` defines `PortModel` AND
   `TermModel` as *identical* — both stamp a 0 V source between their two nodes; the S-param
   engine drives that branch to 1 V and reads the current to form the Y-matrix. `Z` is the
   **renormalization impedance** (used in Y→S), not a resistor stamped into the network.
   `SParameterEngine.CollectPortsAndBranchLabels` accepts `PortModel` OR `TermModel`, reads
   `Num` (required) and `Z` (default 50), sorts by `Num`. So **engine-side, Port == Term today.**
4. **The schematic Term is half-built.** `ComponentTypeRegistry`: `SymbolKind.Port` →
   `EngineReference "Port"`, but its `DefaultParameters` are **empty** — no `Num`, no `Z`. The
   engine throws if `Num` is missing. So the placed "Port" component cannot drive an S-param port
   yet. There is also **no interface-Pin concept** and **no scoping** to stop a Port/Term from
   acting inside an instantiated sub-cell (where its 0 V branch would short the node).

So the substrate (engine `TermModel`, netlist `Port:` object, per-port complex `Z0`) exists; the
**schematic-level Term component, the interface-Pin concept, and the scoping rule do not.**

---

## The model

### 1. Interface Port = a cell terminal, realized by Pins (connectivity)

- A cell exposes an ordered list of **interface ports**, numbered `1..N` (1-based, RF convention).
  `Cell.NumPorts` (the `.ccell` field added in Brief E2) is the count. The cell owns it.
- Each interface port is realized in two views, both by a **Pin**:
  - **Symbol:** a pin object (your definition of "pin"), carrying the port number/name.
  - **Schematic (the cell's primary schematic):** a **Pin** component that declares
    "this net is interface port `k`." Carries an explicit **`Num`** (and optional `Name`).
- A Pin has **no electrical model**. At elaboration it is a named terminal of the cell's
  subcircuit. When the cell is instantiated in a parent, the parent's net wired to symbol-pin `k`
  is merged with the schematic Pin `k`'s net. Nothing is stamped; nothing loads the circuit.
- **Mapping is explicit, never inferred from placement order.** Interface port `k` ↔ schematic
  `Pin(Num=k)` ↔ symbol `pin k`. This is what makes "map schematic nodes to cell ports"
  deterministic. (This is the missing `Num` parameter you noticed — it belongs on the Pin.)
- **Authority / single source of truth:** the **cell** owns `NumPorts`. The primary schematic's
  Pins and the primary symbol's pins must conform. A mismatch is surfaced by the existing
  **unmapped-port panel** in the symbol editor (`EditableSymbol.ExternalPortCount`, fed from the
  cell — see Brief E2's `TryCellPortCount` change). No silent drift.

### 2. Term = S-parameter excitation/measurement (electrical, testbench-only)

- A **Term** is placed in a **testbench** (a top-level schematic not meant to be instantiated).
- Two terminals: **`+`** (`Nodes[0]`, signal) and **`−`** (`Nodes[1]`, reference).
- Parameters: **`Num`** (integer S-param port index) and **`Z`** (reference impedance; real or
  complex; default `50 Ω`). Per-port `Z` is already supported end-to-end (`z0PerPort`,
  `RFNetwork.YToS` takes complex `Z0` per port).
- Behavior: during S-parameter analysis it is the port branch the engine drives/reads. `Z` is the
  renormalization impedance, **not** a resistor in the network.
- **Single-ended:** wire `−` to ground → port `Num` referenced to ground with impedance `Z`.
- **Differential:** wire `+` and `−` across the DUT's two differential nodes → port `Num` is a
  differential port with differential reference `Z`. (Mixed-mode Sdd/Scc/Sdc/Scd decomposition is
  a post-processing concern layered on the port set — v2; the Term primitive already expresses the
  differential port.)
- **Netlist:** emits the `Port:` object (`Port:Term1 + − Num=k Z=...`), matching `hero1.cnl`.
- A Term is **never** part of a reusable cell's interface. Reusable cells use Pins.

### 3. S-parameter port = the analysis's numbered port (derived)

- The S-param engine collects all Terms (and legacy `Port` objects) **at the analysis's top
  schematic**, sorts by `Num`, and defines the S-matrix over them with their reference `Z`.
- This is the only place "port" means "S-parameter port." It is produced by Terms, not by Pins.

---

## How the three high-level goals are met

1. **Run an S-param sim in a TestBench with arbitrary reference impedance** → place **Term**s
   (each `Num` + `Z`) at the testbench top. The engine builds the port set with per-port complex
   `Z0`. (Substrate exists; needs the schematic Term component with `Num`/`Z` defaults.)
2. **Map schematic nodes → cell ports** → place **Pin**s in the cell's primary schematic, each
   carrying an explicit `Num`. Deterministic net ↔ interface-port ↔ symbol-pin binding.
3. **Differential ports** → a **Term** straddling the `+`/`−` pair defines a differential S-param
   port with differential reference `Z`. Differential *interface* ports are supported in v1 as a
   grouped `+`/`−` Pin pair on the symbol (two single-ended Pins tagged as a pair). A dedicated
   differential Pin primitive and mixed-mode UI are deferred to v2.

---

## The scoping rule (fixes the "Term loads the circuit" problem)

Your concern: in VendorA, a Term buried in an instantiated sub-cell stamps its impedance into the
MNA and loads the parent — wrong. circuitRF's current 0 V-source Port/Term has the analogous
failure (it would *short* the node). Resolution:

1. **Terms are recognized only at the analysis's top schematic.** During elaboration for an
   analysis, only top-level Terms become S-param ports. A Term found **inside an instantiated
   sub-cell is a design error** → warn and treat it as **inert** (do not stamp). This is the
   single rule that prevents unwanted loading/shorting.
2. **Reusable cells contain Pins, never Terms.** A linter flags Terms in any non-testbench cell.
   Because Pins have no electrical model, a reusable cell never perturbs its parent.
3. **Engine refinement (flag for the linear-engine doc):** today Port/Term stamp their 0 V branch
   in *every* analysis that calls `StampAll`, which shorts the port node during DC/AC/HB. The
   port branch should be stamped **only** in the driven-port (S-parameter) analysis; in other
   analyses a Term should be **inert (open)**, or optionally present `Z` as a termination if the
   user explicitly wants a realistically-terminated bias point. Until that lands, keep Terms in
   S-param testbenches only.

---

## Terminology — lock these (the clarifying statements)

- **Interface Port** (or just **Port** in cell/RF context): a cell's external RF connection,
  numbered `1..N`. Connectivity only — defines *where* you connect, not *how* you excite.
  Count = `Cell.NumPorts`.
- **Pin**: the concrete realization of an interface port — a pin object in a **symbol**, and the
  matching **Pin component** in a schematic. Pins carry the port `Num` (and optional `Name`).
  ("Pins connect.")
- **Term**: the S-parameter excitation/measurement element placed in a **testbench**. Two
  terminals (`+`/`−`), params `Num` + `Z`. Defines S-parameter port `Num` with reference `Z`.
  Netlists as a `Port:` object. Never used for hierarchical connection. ("Terms excite.")
- **S-parameter port**: the numbered port the S-matrix is defined over — produced by Terms at the
  analysis top. The only context where "port" means the measurement port.

In the **UI and docs** we say **Pin** for connectivity and **Term** for S-param excitation, and
reserve **Port** for (a) the cell's interface-port *count/numbering* and (b) the netlist `Port:`
object. We never place a bare component literally called "Port" — it's ambiguous; it becomes
either a **Pin** (connectivity) or a **Term** (excitation).

---

## Decisions (locked)

1. **Add a schematic `Term` component** (`SymbolKind.Term`, prefix `Term`, `EngineReference
   "Port"`, `ModelKind.Linear`): two terminals `+`/`−`; `DefaultParameters` = `Num` (int, default
   next free index) + `Z` (impedance, default `50 Ω`, complex-allowed). Maps to `TermModel`.
   Category `Terminals`. This is the VendorA-tool-style Term and the thing that drives S-param ports.
2. **Add an interface `Pin` component** (`SymbolKind.Pin`): one terminal; `DefaultParameters`
   = `Num` (int) + optional `Name`; **no engine model / inert** (resolved to a subckt terminal at
   elaboration). This realizes a cell interface port in the schematic and binds to the symbol pin.
   **Interface mapping is via explicit Pin components carrying `Num`** — never net-name = pin-name
   matching, never inferred from placement order.
3. **Retire `SymbolKind.Port`** and **rename it to `Term`** (it already nets to `Port:` and the
   `.cnl` instances are `Term…`). No deprecated alias is kept — alpha, no back-compat. The bare
   "Port" component ceases to exist; placement offers **Pin** (connectivity) or **Term** (excitation).
4. **Numbering:** 1-based for display and `Num` (S-param convention); 0-based internal arrays.
   Auto-assign the next free `Num` on placement; validate uniqueness within a schematic.
5. **Differential interface ports ship in v1** as a grouped `+`/`−` Pin pair (two single-ended Pins
   tagged as a pair, two symbol pins). Deferred to v2: a dedicated differential Pin primitive and
   mixed-mode S-parameters (Sdd/Scc/Sdc/Scd) as a post-processor over differential Terms.
6. **Elaboration/flattening:** Pins become subckt terminals; Terms become `Port:` objects only at
   the analysis top; sub-cell Terms → warning + inert (scoping rule above).
7. **Engine (separate brief):** stamp Term/Port branches only in driven-port analyses; inert
   elsewhere (see scoping rule §3). Update `linear-engine.md` accordingly.

### Interface-component naming
The interface component is **`Pin`** (matches the symbol pin and the project's definition of a
pin as a placed connection point). `IOPin` / `Terminal` were considered and rejected.

---

## Cross-references
- `linear-engine.md` — §9 port extraction; needs the "stamp only in driven analysis" refinement.
- `net-extraction-and-run.md` — elaboration/flattening; Pin→subckt-terminal, Term→`Port:` object.
- `project-file-formats.md` — `.cnl` `Port:` object; `.ccell` `NumPorts`.
- `standard-library-symbols.md` — symbols for Term (`+`/`−`) and Pin.
- Brief E2 — cell owns `NumPorts`; `TryCellPortCount` feeds the symbol's `ExternalPortCount`.
- `testdata/Hero1/hero1.cnl` — canonical `Port:Term… Num=… Z=…` example.
