# Sonnet Brief — SnP (Touchstone) standard-library component

Adds an SnP component to the standard library: a frequency-domain N-port backed by a Touchstone file,
with an arbitrary port count sniffed from the file, a reference-node toggle, a dynamic symbol that
grows with port count and arrangement, three pin-layout templates, and a file-picker + combobox
property UI.

**The single most important fact: the engine, .cnl, elaboration, and Touchstone reader ALREADY
support SnP fully.** This is overwhelmingly a UI/library task. Do NOT reimplement the device model.
Confirmed existing infrastructure (read these, don't rebuild):
- `src/Core/Devices/SnpModel.cs` — stamps Z(ω) per port with a `ReferenceNode` (ground or the N+1
  net); loads the file lazily via `TouchstoneIO.ReadFile`. Done.
- `src/Core/Devices/ComponentModelFactory.cs` `CreateSnpModel` — reads `NumPorts`, `File`,
  `InterpMode`, `ExtrapMode`. `_parameterizedTypes` already includes "SnP". Done.
- `src/Core/Elaboration/Elaborator.cs` — generic param path resolves SnP overrides; `RefNetBinding`
  (on `Instance`) → `ElaboratedComponent.ReferenceNode` (null → ground 0). Done.
- `src/Core/Design/Instance.cs` `RefNetBinding` — documents the N-or-N+1 rule: "Set by the .cnl
  reader when an SnP line has NumPorts+1 nets." Done.
- `src/Core/Netlist/CnlWriter.cs` `FormatStandardInstance` — already emits SnP: signal nets, then
  `RefNetBinding` when non-null, then `param=val` overrides. Names "SnP" explicitly. Done.
- `RfCore/src/TouchstoneIO.cs` — reads Touchstone; infers port count from extension
  (`ParsePortsFromExtension`) and from data (`TryInferPorts`, `TryInferPortsFromTotalTokens`).

So the work is: (1) a lightweight port-count sniffer in RfCore; (2) `SymbolKind.Snp` + registry;
(3) the dynamic symbol + pin geometry with 3 layout templates; (4) the SnP property UI (file picker,
RefNode toggle, PinConfig combo) with sniff-on-select + message-pane errors; (5) the schematic→netlist
emission for SnP (NetExtractor) — STOP-and-report first.

Build 0W/0E (TreatWarningsAsErrors) after each part.

---

## PART 1 — Lightweight port-count sniffer (RfCore)

Add a public method to `RfCore/src/TouchstoneIO.cs` that reads the port count WITHOUT loading the whole
file, reusing the existing private helpers:
```csharp
/// <summary>
/// Reads just enough of a Touchstone file to determine its port count, without loading all data.
/// Strategy: prefer the .sNp extension; otherwise read lines until the first complete frequency
/// block's worth of numeric tokens is accumulated and infer N from the token count. Returns false
/// (with a human-readable error) when the file is missing, unreadable, or not valid Touchstone.
/// </summary>
public static bool TryGetPortCount(string path, out int ports, out string? error)
{
    ports = 0; error = null;
    try
    {
        if (!File.Exists(path)) { error = $"File not found: {path}"; return false; }

        // 1) Extension hint (.s2p, .s4p, …) — authoritative enough to draw the symbol.
        int? fromExt = ParsePortsFromExtension(path);

        // 2) Sniff the data: read lines, skip comments(!)/option(#), accumulate numeric tokens
        //    until we have >= one block. Stop as soon as N can be inferred — do NOT read the rest.
        using var reader = new StreamReader(path);
        var tokens = new List<double>();
        string? line;
        int linesRead = 0;
        while ((line = reader.ReadLine()) != null && linesRead++ < 10000)
        {
            string t = line.Trim();
            if (t.Length == 0 || t[0] == '!' || t[0] == '#') continue;
            int bang = t.IndexOf('!'); if (bang >= 0) t = t[..bang];
            foreach (var tok in t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(tok, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v))
                    tokens.Add(v);

            // Once we have a plausible first block, infer N from (count-1) data values.
            if (tokens.Count >= 3)
            {
                int? n = fromExt ?? TryInferPorts(tokens.Count - 1);
                // For N>2 a row spans multiple lines, so keep reading until the count is exactly
                // 1 + 2N² for some N (TryInferPorts returns non-null only at an exact block boundary).
                if (n.HasValue && (tokens.Count - 1) == 2 * n.Value * n.Value)
                { ports = n.Value; return true; }
            }
        }
        // Fall back to extension if data sniffing didn't land on a clean block.
        if (fromExt is > 0) { ports = fromExt.Value; return true; }
        error = "Could not determine port count from file contents or extension.";
        return false;
    }
    catch (Exception ex) { error = ex.Message; return false; }
}
```
> RfCore is the lockstep-with-splotRF library, but this is a purely ADDITIVE, read-only helper — it
> changes no DataSet/DataCube API and needs no splotRF coordination. Note it in the RfCore changelog
> if one exists. The `data takes precedence over extension` behavior matches `ReadFile`.

**Part 1 test** (RfCore tests): `TryGetPortCount` returns the right N for a .s1p/.s2p/.s3p sample,
returns the data-inferred N when the extension lies, and returns false with an error for a missing or
garbage file. Confirm it does NOT read the whole file (e.g. point it at a large multi-thousand-point
file and assert it returns quickly / reads only a bounded number of lines — or just trust the bounded
loop).

---

## PART 2 — `SymbolKind.Snp` + registry entry + default parameters

### 2a. Enum (`src/Ui/Schematic/SchematicModel.cs`)
Add `Snp` to the `SymbolKind` enum (after `Generic` or near the data-file types).

### 2b. Registry (`src/Ui/Schematic/ComponentTypeRegistry.cs`)
- Add to `Registry`:
  ```csharp
  [SymbolKind.Snp] = new("SnP", "S",
      Category: ComponentCategory.DataFiles,
      SearchTerms: ["SnP", "Touchstone", "snp", "s2p", "sparam file", "data file", "network"],
      IsCommon: true),
  ```
- `EngineReference(Snp)` → `"SnP"` (matches the factory + CnlWriter; case-insensitive there but use
  "SnP").
- `DisplayName(Snp, portCount)` → `"S{portCount}P"` when portCount ≥ 1 (e.g. "S2P"), else "SnP". Add
  a branch in the `DisplayName(kind, portCount)` method like the ZPort/Sdd ones.
- `DefaultParameters(Snp, portCount)`:
  ```csharp
  case SymbolKind.Snp:
  {
      int n = portCount >= 1 ? portCount : 1;
      return [
          new("NumPorts",  $"{n}",       "", false, UnitDimension.None),  // hidden; sniffed from file
          new("File",      "",           "", true,  UnitDimension.None),  // touchstone path (rel-to-workspace or absolute)
          new("RefNode",   "false",      "", false, UnitDimension.None),  // reference-node toggle
          new("PinConfig", "Standard",   "", false, UnitDimension.None),  // Standard | SplitLR | DualRow
          new("Pitch",     "Loose",      "", false, UnitDimension.None),  // Tight (100) | Loose (200); only used when N >= 4
          new("InterpMode","CubicSpline","", false, UnitDimension.None),  // factory reads this
          new("ExtrapMode","Clamp",      "", false, UnitDimension.None),  // factory reads this
      ];
  }
  ```
  (`File` shows on the schematic so the user sees which file; `NumPorts`/`RefNode`/`PinConfig`/`Pitch`/interp
  are managed by the custom SnP UI in Part 4, not the generic row editor — they're hidden from the
  generic rows but edited by the SnP panel.)

### 2c. Variadic port count
`EditableComponent.PortCount` currently special-cases `ZPort`/`Sdd` to read `NumPorts`. Add `Snp` to
that same branch so `PortCount` reads `NumPorts` for SnP too:
```csharp
if (Symbol is SymbolKind.ZPort or SymbolKind.Sdd or SymbolKind.Snp) { /* read NumPorts */ }
```

**Part 2 test:** `DefaultParameters(Snp, 2)` yields the seven params above with NumPorts=2;
`DisplayName(Snp, 2)` == "S2P"; a placed SnP's `PortCount` reflects its `NumPorts`.

---

## PART 3 — Dynamic SnP symbol + pin geometry (3 layout templates)

### 3a. Pin-config + pitch enums
Add to `src/Ui/Schematic/SymbolModel.cs` (framework-free) next to the other symbol enums:
```csharp
/// <summary>SnP pin-arrangement template. Drives both the symbol drawing and the pin positions.</summary>
public enum SnpPinConfig { Standard, SplitLR, DualRow }

/// <summary>SnP pin pitch on a side. Tight = 100 units, Loose = 200 units. Only applied when N >= 4
/// (1/2/3-port use the fixed mid-edge positions below regardless of Pitch).</summary>
public enum SnpPitch { Tight, Loose }
```
Parse `SnpPinConfig` from the `PinConfig` param string ("Standard"/"SplitLR"/"DualRow"; default Standard
on any unrecognized value) and `SnpPitch` from the `Pitch` param string ("Tight"/"Loose"; default Loose).

### 3b. The symbol (`src/Ui/Schematic/BuiltInSymbols.cs`)
SnP is variadic like SDD/ZPort. Add a per-(N, refNode, config) builder. Because the symbol depends on
THREE inputs (port count, RefNode on/off, PinConfig) plus the "no valid file yet" placeholder state,
it can't use the simple `Primitives(kind)` path. Extend the variadic dispatch:

- Add a cache keyed on `(int n, bool refNode, SnpPinConfig cfg)` (or build uncached — these are cheap).
- **Placeholder** (no valid file / NumPorts unknown): a generic 200×200 rounded-rect square, no pins
  beyond a default, drawn until a valid file sets NumPorts. Spec: when `NumPorts` is unset/invalid,
  `Primitives(Snp, …)` returns just `RRect(0,0,200,200,12)` (a 200×200 rounded square).
- **Resolved symbol** (NumPorts = N ≥ 1): a rounded-rect body sized to the pin layout, port-number
  text primitives inside the rect, optional reference pin, and pins per the layout template below.

The dispatch needs N + refNode + config + pitch. Two options — use (i):
- (i) **Drive it from the placed component's parameters**, not just `SymbolKind`. The cleanest hook:
  add an overload `BuiltInSymbols.PrimitivesForSnp(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)`
  and have the renderer / `ComputeGlyphBb` / ghost call it for `SymbolKind.Snp` (reading the component's
  `NumPorts`, `RefNode`, `PinConfig`, `Pitch` params). The existing `Primitives(kind, portCount)` only
  knows portCount; SnP needs the extra three, so the SnP path must look them up. Mirror how SDD/ZPort
  already special-case in `Primitives(kind, portCount)` but add the refNode+config+pitch lookup at the
  call sites that build SnP (they have the `EditableComponent`/`SchematicComponent` and thus the params).

> This is the crux of the dynamic symbol. The renderer's component loop calls
> `BuiltInSymbols.Primitives(c.Symbol, c.Ports.Count / 2)`. For SnP that's insufficient (needs refNode
> + config + pitch). Add a small resolver: when `c.Symbol == Snp`, the caller reads the params off the
> component and calls `PrimitivesForSnp(n, refNode, cfg, pitch)`. Do this in EACH place that builds SnP
> geometry: the renderer component loop, `EditableComponent.ComputeGlyphBb`, and the ghost/preview.
> Report if a cleaner single-source hook is feasible (e.g. caching the resolved primitive list on the
> render component at build time, like cell-ref components already cache `CellRefPrimitives`).

### 3c. Layout templates (pin positions)
Body is a rounded rect. Port-number text sits inside the rect next to each pin. Pin tips are on grid
(multiples of 100), on the body edges. Reference pin (when RefNode=true) is an extra pin.

Per the spec:
- **1-port:** 200×200 square. Port 1 at mid-LEFT (−100, 0). Optional reference pin at mid-RIGHT
  (+100, 0) when RefNode on.
- **2-port:** 200×200 square. Port 1 mid-LEFT (−100,0), Port 2 mid-RIGHT (+100,0). Optional reference
  pin at BOTTOM-middle (0,+100) when RefNode on.
- **3-port:** like 2-port plus Port 3 at TOP-middle (0,−100). Reference (when on) stays bottom-middle.
  (Note: with refNode on, port 3 top + ref bottom coexist — that's fine.)
- **N ≥ 4:** autogen; the body GROWS vertically (±y) to fit the pins, arranged per the PinConfig
  template (below) at the spacing given by the `Pitch` param. Reference pin (when on) at bottom-middle,
  body extended to clear it.

The three PinConfig templates (apply for general N, especially N≥4):

1. **Standard (sequential wrapping):** pins distributed sequentially around the boundary in a
   continuous loop. For N=4: Pin1 top-left, Pin2 bottom-left, Pin3 bottom-right, Pin4 top-right.
   General rule: walk the perimeter counter-clockwise (or clockwise — pick one and document) placing
   ports 1..N in sequence. Practically for the rect: fill the LEFT side top→bottom with the first
   ceil(N/2)… no — to match "1 TL, 2 BL, 3 BR, 4 TR", fill LEFT side top→bottom with ports 1..ceil(N/2),
   then RIGHT side bottom→top with the remainder. So left = 1,2,3,…; right (bottom-up) = …,N. Verify
   the N=4 example lands exactly 1=TL,2=BL,3=BR,4=TR with this rule.
2. **Split L/R (in-out flow):** lower half of ports on the LEFT (top→bottom), upper half on the RIGHT
   (top→bottom). N=8: 1,2,3,4 left (top→bottom); 5,6,7,8 right (top→bottom). For odd N put the extra
   port on the left (ceil on left, floor on right).
3. **Dual-Row (interleaved/differential):** odd ports on the LEFT, even ports on the RIGHT, paired by
   row so complementary pairs sit across from each other. N=4: 1,3 left; 2,4 right, with 1 across from
   2 and 3 across from 4. General: left = odd ports in order (1,3,5,…), right = even ports (2,4,6,…),
   row k holds (2k−1) left and (2k) right.

Pin vertical spacing (N ≥ 4 only): driven by the `Pitch` param — **Tight = 100 units**, **Loose = 200
units** between adjacent pins on a side. Loose is the default and matches the SDD/ZPort breathing-room
convention (a clear grid lane between pins for routing); Tight halves the body height for very high port
counts at the cost of routing room. Both keep every pin tip on grid (multiples of 100). For N ≤ 3 the
Pitch param is IGNORED — those use the fixed mid-edge positions above. Body half-height = max|pinY| +
margin (mirror `SddBodyRect`).

Put port-number `Txt` primitives just inside the rect next to each pin (left pins → left-aligned text
just inside the left edge; right pins → right-aligned just inside the right edge; top/bottom → centered).

### 3d. Pin definitions (`SymbolPortDefs.For` in `EditableSchematic.cs`)
SnP pins must come from the SAME layout function so geometry and connectivity never drift. Add a
`SymbolKind.Snp` case to `SymbolPortDefs.For(kind, portCount)` — but it ALSO needs refNode + config,
which `For(kind, portCount)` doesn't take. Add an SnP-specific generator
`GenerateSnpPorts(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)` returning the pin tuples
(including the reference pin as the LAST pin when refNode is true — so the reference pin is pin index N,
the N+1th pin). `pitch` only affects spacing for n ≥ 4. The callers that know the component (have its
params) call this; the bare `For(Snp, n)` overload can default to refNode=false, Standard, Loose for the
no-param path (ghost before params exist).

> Pin ORDER contract: pins[0..N−1] = ports 1..N (in port-number order, regardless of physical
> position); pins[N] = reference pin (only when refNode on). This ordering is what the netlist
> emission (Part 5) relies on: signal nets in port order, then the reference net last → the N-or-N+1
> rule. Keep port-number order independent of the visual layout (layout only moves pins in space, not
> their index).

### 3e. Glyph BB
`ComputeGlyphBb`/`ComputeGlyphBbLocal` must bound the (possibly tall, N≥4) SnP body + pins. Since the
body grows vertically, walk the SnP pins like the SDD/ZPort branch already does (it walks
`SymbolPortDefs.For(kind, n)` pin extents). Add `Snp` to that branch, using the SnP pin generator.
Also ensure the FullBb cull fix from the rendering brief applies to SnP (it will, since FullBb unions
the glyph BB).

**Part 3 tests:** for each PinConfig at N=4, assert the pin positions match the documented corners;
1/2/3-port assert the exact mid-edge positions + reference pin position; refNode adds exactly one
extra pin at the end; placeholder (no NumPorts) returns a 200×200 rounded rect with no extra pins;
body half-height grows for N≥6; at N≥4, Loose pitch puts adjacent same-side pins 200 apart and Tight
puts them 100 apart (and Pitch is a no-op for N≤3).

---

## PART 4 — SnP property UI: file picker, RefNode toggle, PinConfig combobox + sniff-on-select

The user explicitly noted SnP likely needs a custom properties view — its controls (file picker,
toggle, enum combo) don't fit the generic name=value `Rows`. Implement as an **SnP-specific section in
the existing `ParameterEditorView`**, shown only when the target is SnP (no new document type needed).

### 4a. ViewModel (`ParameterEditorViewModel.cs`)
- Add `public bool IsSnp => _target?.Symbol == SymbolKind.Snp;` (raise `OnPropertyChanged(nameof(IsSnp))`
  in `SetTarget`).
- Add SnP-bound observable properties read from / written to the target's params:
  - `SnpFilePath` (string) ↔ `File` param.
  - `SnpRefNode` (bool) ↔ `RefNode` param ("true"/"false").
  - `SnpPinConfig` (SnpPinConfig enum) ↔ `PinConfig` param; expose
    `public static SnpPinConfig[] PinConfigOptions { get; } = Enum.GetValues<SnpPinConfig>();`
  - `SnpPitch` (SnpPitch enum) ↔ `Pitch` param; expose
    `public static SnpPitch[] PitchOptions { get; } = Enum.GetValues<SnpPitch>();`
  - `SnpPortCountText` (read-only display, e.g. "2-port" or "no file").
- Each setter executes an undoable param edit through `_schematicVm.Execute(...)` (reuse the existing
  parameter-set command path — check how `ParameterRowViewModel` commits an expression and mirror it;
  likely `EditParameterCommand` or `SetParametersCommand`). For the generic SnP params the existing
  per-param commit works; do NOT route these through the generic `Rows` (they're hidden there).
- **File picker command** `PickSnpFileCommand`: invokes a file-picker callback (see 4c), and on a
  chosen path:
  1. Resolve to an absolute path for sniffing (relative paths are relative to the Workspace — get the
     workspace root from the schematic/workspace model; report how you obtain it).
  2. Call `TouchstoneIO.TryGetPortCount(absPath, out int n, out string? err)`.
  3. On success: set `File` (store the path AS ENTERED — relative if under workspace, else absolute),
     set `NumPorts = n`, and rebuild (the symbol redraws via the normal model-changed path).
  4. On failure: send `err` to the message pane via `_schematicVm.MessageSink?.Warning(...)` (or
     `.Error`) and do NOT change NumPorts/File. (`SchematicViewModel.MessageSink` exists.)
- Also sniff when the user types a path directly into the File field (commit handler), same flow.

### 4b. View (`ParameterEditorView.axaml`)
Add an SnP section (a `StackPanel` with `IsVisible="{Binding IsSnp}"`) ABOVE or INSTEAD of the generic
`Rows` list for SnP. Contents:
- A read-only "Ports: {SnpPortCountText}" line.
- File row: a `TextBox` bound to `SnpFilePath` + a "Browse…" `Button` bound to `PickSnpFileCommand`.
- "Reference node" `CheckBox` bound to `SnpRefNode` (tooltip: "When on, expose a reference-node pin;
  when off, the reference is ground").
- "Pin configuration" `ComboBox` bound to `SnpPinConfig`, ItemsSource `PinConfigOptions` (tooltip
  summarizing the three templates).
- "Pitch" `ComboBox` bound to `SnpPitch`, ItemsSource `PitchOptions` (tooltip: "Pin spacing for 4+ port
  symbols: Loose = 200 units (clear routing lanes), Tight = 100 units (compact). Ignored for 1–3 ports.").
  Optionally disable/hide it when the current `NumPorts` < 4 since it has no effect there.
Hide the generic `Rows` ItemsControl for SnP (e.g. wrap it with `IsVisible="{Binding !IsSnp}"` or show
the SnP panel in its place) so the user doesn't see raw `NumPorts`/`PinConfig`/`Pitch` rows.

### 4c. File picker plumbing
The VM is framework-free-ish (no Avalonia file dialogs directly). Use the same pattern the app already
uses for file dialogs (search for `IStorageProvider` / `FilePickerOpenOptions` / existing "Browse"
buttons — e.g. how bitmaps or cells are picked). Wire a `Func<Task<string?>>? PickSnpFileAsync` callback
set by the view/host, invoked by `PickSnpFileCommand`. Filter to Touchstone files
(`*.s1p;*.s2p;*.s3p;*.snp;*.s*p`). Report the exact dialog mechanism you used.

**Part 4 check (manual):** select an SnP → SnP panel shows; Browse → pick a .s2p → Ports shows
"2-port", symbol redraws with 2 ports; toggle Reference node → a reference pin appears/disappears and
the symbol updates; change Pin configuration → pins rearrange; on a 4+ port SnP, switch Pitch
Loose↔Tight → pin spacing changes between 200 and 100 (and the body height changes); pick a garbage
file → a message appears in the Message pane and the symbol/NumPorts are unchanged.

---

## PART 5 — Schematic → netlist emission for SnP (NetExtractor) — STOP AND REPORT FIRST

The .cnl/elaboration/engine already consume SnP correctly (see top). What remains is producing the
`Instance` for an SnP `EditableComponent` during net extraction: signal nets in port order, the
reference pin's net as `RefNetBinding` (only when RefNode on), and `File`/`NumPorts`/interp as
overrides. `NumPorts` need NOT be emitted (the reader infers N from net count + extension — but SnP
has no ± pairs, so net count == N or N+1; confirm the reader derives N for SnP, else DO emit NumPorts).

**STOP-and-report before editing** — read `src/Ui/Schematic/NetExtractor.cs` and report:
- How an `EditableComponent` becomes an `Instance` today (where `NetBindings`/`Overrides`/`RefNetBinding`
  are set), and whether any component currently sets `RefNetBinding` (likely none yet — SnP is first).
- For SnP: confirm the plan = emit the N signal pins' nets in port-number order as `NetBindings`; if
  RefNode on, emit the reference pin's net as `RefNetBinding`; emit `File` (and `InterpMode`/`ExtrapMode`
  if non-default) as overrides; decide NumPorts emission based on whether `CnlReader` can infer N for
  SnP (report what `CnlReader` does for an SnP line — does it need NumPorts, or infer from net count?).
- Whether the reference pin (pin index N, the N+1th) is correctly the one mapped to `RefNetBinding` vs.
  a regular `NetBinding`.

Then implement the SnP emission per the confirmed plan and add a round-trip test (schematic → .cnl →
read back → elaborate → `SnpModel` with the right `PortCount` and `ReferenceNode`).

---

## Gate
Build 0W/0E. Tests green. Verify on disk:
- Placeholder SnP draws a 200×200 rounded square before a file is set.
- Browsing to a valid .s2p sets Ports=2 and draws a 2-port symbol; .s4p draws a 4-port that grows
  vertically; the three PinConfig templates rearrange pins as documented.
- Reference-node toggle adds/removes the reference pin and changes whether the .cnl emits N or N+1 nets.
- An invalid file posts a message to the Message pane and leaves the symbol unchanged.
- A 2-port SnP wired into a testbench elaborates and runs an S-parameter sweep using the Touchstone
  data (end-to-end).

**STOP-and-report checkpoints** (report before finalizing each):
1. Part 3b — the cleanest hook for passing (N, refNode, config) into SnP symbol building across the
   renderer / glyph-BB / ghost call sites (or caching the resolved primitives on the render component).
2. Part 4a/4c — how you obtain the Workspace root for relative-path resolution, and the exact file
   dialog mechanism used.
3. Part 5 — the NetExtractor emission plan + whether `CnlReader` infers SnP N from net count or needs
   NumPorts emitted.
