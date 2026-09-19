# Brief 1 — the `.csmith` document, and the seven places a document type has to be declared

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith1-n` · **Phase:** P1
**Area:** `src/Design/Smith/` · **Depends on:** nothing · **Blocks:** everything
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §3.1-§3.3, §7

---

## 0. What this brief delivers

The data model and its file format, framework-free, with **no arithmetic and no UI**. `SmithDesign`, the
element records, the generator table, `SmithDesignIo`, `SmithClipboard`, and the seven registration points
that make a `.csmith` a document type the operating system and the shell both know about.

Nothing in this brief evaluates anything. A `SmithDesign` that round-trips is the whole deliverable.

---

## 1. `R-smith1-1` — `SmithDesign`, in `src/Design/Smith/`

A folder in the existing `CircuitRF.Design` project. **No new `.csproj`** — overview §1a, and it is what
makes the firewall gate free.

```
SmithDesign
  Name
  Chart      : Z0Ohm (real), DesignFrequencyHz, Window, ShowGrippers/Targets/Labels
  Generator  : Rows[ { FrequencyHz, ResistanceOhm, ReactanceOhm } ], SourcePath
  Elements   : ordered IList<SmithElement>
  Sweep      : Enabled, StartHz, StopHz, Points
  ConstantQ  : Enabled, Q
  Overlays   : IList<SmithOverlayRef>
  Markers    : the Data Display Marker shape, verbatim
  View       : splitter positions, network scroll/zoom, MirrorNetwork
```

```
SmithElement
  Kind        : R | L | C | Srlc | Prlc | Z1P | S1P | S2P | Tline | StubOpen | StubShorted
  Placement   : Series | Shunt
  Name        : instance name, unique within the design
  Enabled     : bool
  ActiveParameter : which parameter a gripper drags (brief 3, brief 6)
  Values      : ROhm, LHenry, CFarad, Z0Ohm, ElectricalLengthDeg, ReferenceFrequencyHz,
                ImpedanceOhm { Re, Im }
  FileRef     : path relative to the document — S1P/S2P only
  SliderRange : per-parameter { Min, Max }
```

### `R-smith1-1a` — the two names, decided here to avoid a collision discovered late

The model is **`SmithDesign`** (mirroring `MatchDesign`) and brief 4's Dock document is
**`SmithChartDocument`** (mirroring `DataDisplayDocument`). **`SmithDocument` is used for neither**,
because it is the obvious name for both and whichever one took it would make the other one's name read
as a mistake. The test file is `SmithDocumentTests.cs` and that is fine — it tests the `.csmith`
document, which is what the file is about.

The `Values` bag is deliberately one flat record rather than a per-kind class hierarchy: the window's
slider panel, the `.csmith` reader and the paste recognizer all have to ask "does this element have an
`L`?", and a polymorphic answer to that question turns three call sites into nine.

### `R-smith1-2` — the vocabulary is closed, and it is closed because it maps onto real components

Each `Kind` names a `SymbolKind` that `ComponentTypeRegistry` already declares. **Write that mapping down
once, in this brief's code, as a single function** — brief 6 needs it to draw, brief 7 needs it to copy,
and brief 2 needs it to build the oracle's netlist. Three copies of it would drift.

| Kind | `SymbolKind` | engine | nets | parameters it carries |
|---|---|---|---|---|
| R / L / C | `Resistor` / `Inductor` / `Capacitor` | `R` / `L` / `C` | 2 | one |
| Srlc / Prlc | `Srlc` / `Prlc` | `SRLC` / `PRLC` | 2 | R, L, C |
| Z1P | `ZPort`, `NumPorts=1` | `ZPort` | 1 + ref | `Z[1,1]` |
| S1P | `Snp`, `NumPorts=1` | `SnP` | 1 + ref | `File` |
| S2P | `Snp`, `NumPorts=2` | `SnP` | 2 + ref | `File` |
| Tline / StubOpen / StubShorted | `Tline` | `TLIN` | 2 | `Z`, `E`, `F` |

**`S2P` is `Placement.Series` only.** A 2-port with its second port grounded is a different component
than the one the user placed, and a shunt one-port is what `S1P` and `Z1P` are for. A `SmithDesign`
carrying a shunt `S2P` is invalid and `Refusal()` says so by element name.

### `R-smith1-3` — `Refusal()`, on `RailDocument`'s own pattern

One method returning `string?`: the first thing wrong with this document, as a sentence naming the
offending object, or null. `SmithDesignIo.Serialize` calls it and throws rather than writing (§7 of the
note: *a document that cannot be read back is a document that was never written*). The rules:

- the generator table is non-empty, sorted, and has **unique frequencies** — a duplicate is a refusal
  naming the frequency, never a silent last-wins;
- every element name is non-empty and unique;
- `Placement.Shunt` with `Kind.S2P` is refused (`R-smith1-2`);
- `S1P`/`S2P` carry a `FileRef`, and nothing else does;
- the design frequency is inside the table's span, **unless the table has exactly one row**, in which case
  one impedance is flat and any design frequency is legal;
- a TLIN's `ReferenceFrequencyHz` is positive, its `Z0Ohm` positive, its length non-negative;
- R, L and C are non-negative wherever they appear;
- the sweep, if enabled, has `Start < Stop` and `Points ≥ 2`;
- `ConstantQ.Q` is finite and positive when enabled.

---

## 2. `R-smith1-4` — `SmithDesignIo`, mirroring `RailDocumentIo` exactly

`System.Text.Json`, `WriteIndented`, `JsonStringEnumConverter`, `DefaultIgnoreCondition =
WhenWritingNull`, `PropertyNameCaseInsensitive`, `AtomicFile` write, gzip sniff on load, and a
`CurrentFormatVersion` that **refuses a newer file rather than half-reading it**.

Read `src/Design/RailRf/RailDocumentIo.cs` before writing this. A fifth spelling of the same thing would
be a fifth thing to keep in step.

```csharp
public const string Extension            = ".csmith";
public const int    CurrentFormatVersion = 1;
```

### `R-smith1-5` — base SI, and the one named exception

Every number on disk is base SI: **hertz, henries, farads, ohms**. A picohenry is `1e-12`, not `1`. This
is the sweep-unit trap in `src/Engine/RESOLVED.md` — a mark read without its scale once produced a run at
2 Hz that looked entirely normal — and this tool's inputs are all picohenries and gigahertz, so it is the
one most likely to be got wrong here.

**The exception is electrical length, stored in degrees in a field named `ElectricalLengthDeg`**, on
`RailTarget`'s millivolts precedent. The rule base-SI actually protects is *a number must not be readable
at the wrong scale*, and a field whose name carries its unit satisfies it. Radians would match the letter
and disagree with `TLIN`'s own `E`, the schematic, the UI and every textbook, at four conversion sites.

**Gate it**: a test that writes 1 pH, 1 pF and a 90° line, reads the raw JSON as text, and asserts the
literal tokens.

### `R-smith1-6` — `SmithClipboard`, marker-guarded, framework-free

`src/Design/RailRf/RailClipboard.cs` is the shape. One constant, one envelope, one `TryDeserialize` that
returns **false for absolutely everything that is not this**:

```csharp
public const string Marker = "circuitrf/smith-clipboard-v1";
```

The guard is not tidiness. The system clipboard is one shared channel, and the text on it may be a layout
fragment, a schematic fragment, a Data Display config or a `.crail` somebody pasted out of an editor —
every one of them JSON-shaped often enough that a permissive reader gets *part* of it and reports success.
A document half-replaced by an unrelated payload is worse than one that refused, because nothing says so.

**And a `SerializeUnvalidated` beside `Serialize`, for the clipboard and nothing else.** A copy always
writes: a half-built design is exactly what someone copies while they are still working, and a copy that
writes nothing leaves the *previous* copy sitting there for the next paste to find.

### `R-smith1-7` — the generator's `.s1p` import copies values IN

The reader lives here (`SmithGeneratorImport`), takes a path, reads it through `TouchstoneIO`, converts
S₁₁ to Z **against the file's own stated reference impedance**, and returns one row per file frequency.

`SourcePath` is recorded as **provenance only** — it is displayed and it drives a Re-import button, and
nothing resolves it at load. This is the opposite choice from `Overlays` (brief 8) and deliberately: an
overlay is reference material the user is comparing against, while the generator is part of the design,
and a design that stops opening because a file moved is a design that was never portable.

**Conjugate is a one-shot edit of the table**, not a persistent flag — negate every row's X in place. A
flag would mean the number in the table and the number the tool uses disagree, and there is no way to
display that which does not eventually mislead someone. It is a method here and an undo entry in brief 4.

---

## 3. `R-smith1-8` — the seven registration points, in one change

Overview §1b has the table. Do all seven, then run the three parity tests that already exist:

```
dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~WBondStandalone"
dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~AsyncDocumentOpenRouting"
dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~WorkspaceUserSidecar"
```

Two of the seven are stubs at this brief and are completed by brief 4:

- `WorkspaceViewModel.OpenSmithPath` opens **nothing** yet — it exists so the dispatcher has a target and
  the routing test passes. Brief 4 fills it in.
- `DocumentKinds.Classify` returns a new `DocumentKind.Smith`; the CLI verbs that switch on it get their
  `.csmith` cases in brief 10. **`check` and `find` must not throw on one in the meantime** — a kind with
  no case is a crash, not a refusal.

**Do not add the Tools ▸ Smith Chart menu item or the On Launch row here.** Both are brief 4, because both
need a window to open and a half-wired menu entry that opens nothing is worse than no entry.

---

## 4. The gate

`tests/Ui.Tests/Smith/SmithDocumentTests.cs` — one test per claim, not one per field:

1. **Round trip.** A design with every `Kind`, both placements, a three-row generator table, a sweep, a Q
   setting, two overlays, two markers and `MirrorNetwork = true` survives serialize → deserialize
   unchanged.
2. **Base SI on disk** (`R-smith1-5`) — the raw-text token assertion.
3. **A newer `FormatVersion` is refused**, with a sentence naming the version.
4. **`Refusal()` fires for each rule in `R-smith1-3`**, and each sentence names its object. One test, a
   table of cases; nine assertions, not nine tests.
5. **`SmithClipboard.TryDeserialize` returns false** for: a `.crail` payload, a schematic-selection
   payload, a Data Display config, `"{}"`, `""`, and a truncated one of its own.
6. **`SerializeUnvalidated` writes a design `Serialize` refuses**, and that payload round-trips.
7. **`.s1p` import** against a small committed fixture: three rows, values converted against the file's
   own reference, `SourcePath` recorded.
8. **Conjugate negates X and leaves R and f alone**, and applying it twice is the identity.

Plus, free and with no new code:

```
dotnet test tests/Firewall.Tests --no-build
```

which is what asserts `src/Design/Smith/` reached for no UI framework.

---

## 5. What this brief must NOT do

- **No evaluation.** No impedance, no Γ, no trajectory. Brief 2.
- **No window, no menu item, no On Launch row.** Brief 4.
- **No second Touchstone interpretation.** `TouchstoneIO` reads the file; this brief converts what it
  returns and adds no opinion about per-port references or `#`-line parsing.
- **No new project, and no `public` promotion of anything in `src/Design` that is currently `internal`** —
  `src/Design/Smith/` is the same assembly.

---

## 6. On completion

Findings to `src/Design/RESOLVED.md`. **Never a `CLAUDE.md`.**
