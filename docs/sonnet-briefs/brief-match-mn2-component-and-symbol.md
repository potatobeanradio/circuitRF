# Sonnet Brief — Match MN-2: the component, the symbol and the palette

**Design:** `docs/design/match.md` §7.2, §8. **Depends on MN-1**
(`docs/sonnet-briefs/brief-match-mn1-synthesis-core.md`) — `src/Core/Match/` must exist and its gate
must be green before you start. This brief makes the `Match` **placeable, elaboratable and
simulatable**: a `ComponentModel` that stamps the ladder, a symbol, a palette entry. **It adds no
editor** — MN-3 does that, and until it lands a `Match` is placed with a default design and edited only
through its `Design` parameter.

**Where findings go: `src/Core/Match/RESOLVED.md`** (MN-1 created it) for anything about the model or
the elaborator, and `src/Ui/RESOLVED.md` for anything about the symbol, registry or palette.
**Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/Core.Tests   --no-build
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests     --no-build
dotnet test tests/Firewall.Tests --no-build
```

Separate commands (`MSB1008`). `Engine.Tests` is ~3 min 24 s on its own — expect the loop to be slow
and scope your iteration to `--filter` on your own classes until the end.

---

## 0. The two facts that decide whether this brief is correct

### 0.1 The component does NOT contain the absorbed elements

**The component stamps the ladder minus the two absorbed termination reactances.** Those belong to the
external network — absorbing them is the entire premise of the feature. If a design's end arm is
(L = 153.5169 pH, C = 10 pF) with the 10 pF absorbed, the component contains **the inductor only**.

A `CFano`/`LFano` surplus element (MN-1 §5) and a `CDetune`/`LDetune` element (MN-1 §6) **are** ours and
**are** stamped. MN-1 flags each element `IsAbsorbed`; read the flag, not the name.

Getting this backwards produces a component that looks perfect in MN-3's preview and is wrong the
moment it is placed, with no error anywhere. §5.1 tests it.

### 0.2 Stamp the elements, not an ABCD block

The model declares how many internal nodes it needs, the **elaborator mints them**, and `Stamp`
contributes each element the way the primitive models do. Do **not** cascade 2×2 ABCD matrices and
stamp a lumped two-port. Three reasons, in order:

1. **DC.** A series arm contains a capacitor, so its ABCD entries diverge at ω = 0 — and HB always
   includes the DC harmonic. Stamping elements inherits `InductorModel`'s already-correct DC-open
   behaviour instead of re-deriving it.
2. **HB.** Internal node voltages carry their own harmonic content. Eliminating them locally is exact
   at DC and wrong in HB — the documented reason `DiodeModel`'s internal node is not collapsed.
3. **MN-5.** Flatten writes the same elements as ordinary components; the two must agree to 1e-12. With
   an ABCD block that equality is an accident waiting to break.

---

## 1. `MatchModel` — `src/Core/Devices/MatchModel.cs`

```csharp
public sealed class MatchModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    /// How many nets beyond the two pins this instance needs. Read by the elaborator.
    public int InternalNodeCount { get; }
    …
}
```

Node contract: `Nodes[0]` = port-1 signal (Term1 side, left pin), `Nodes[1]` = port-2 signal, ground is
the common return — the `TLineModel` convention (`src/Core/Devices/TLineModel.cs`, read its header).
`Nodes[2…]` are the minted internal nodes, in ladder order along the through path.

`InternalNodeCount` = (number of series arms in the stamped ladder) − 1, computed from the ladder MN-1
returns. A ladder that is a single shunt arm needs none. Derive it; do not guess it from `Order`,
because §5's surplus and detune elements can change the count.

**Stamping**, per element, at each ω:

- **series arm** (L in series with C, in the through path) → one branch-current unknown with
  `Z = jωL + 1/(jωC)`. This is exactly what `InductorModel` already implements for `L=` plus `C=`,
  **including the ω = 0 case** (branch is a DC open: `AddBranchCurrent` then a `−i = 0` constraint).
  Read `src/Core/Devices/InductorModel.cs` lines 1–60 and reproduce its structure rather than
  re-deriving the DC branch.
- **shunt arm** → `AddAdmittance(node, 0, jωC)` for the capacitor plus an inductor branch to ground for
  the L (again the `InductorModel` shape; at ω = 0 a bare inductor is an exact short).
- **absorbed elements** → nothing. Skip them.

Construction goes through `ComponentModelFactory`:

- add `"Match"` to `_parameterizedTypes`;
- `CreateMatchModel(parameters)` decodes `Design` via `MatchEmbedding.TryDecode`, runs MN-1's rebuild
  (§10), and constructs the model from the resulting element list. Follow `CreateWBondModel`
  (`src/Core/Devices/ComponentModelFactory.cs`, ~line 1331) — including its failure behaviour:
  a `Design` that will not decode **throws with a message naming the instance and telling the user to
  re-open the Designer**. It must never fall back to a default network.
- MN-1's rebuild can return notes (a dropped transform, a clamped N, a fingerprint mismatch). Surface
  them through `IReportsWarnings`, exactly as `WBondModel` does, phrased with the instance path.

### 1.1 The elaborator mint

In `src/Core/Elaboration/Elaborator.cs`, beside the existing `Tuner` / `P1Tone` / `Diode` blocks
(~lines 325–370):

```csharp
if (model is MatchModel mm && mm.InternalNodeCount > 0)
{
    var extra = new int[mm.InternalNodeCount];
    for (int k = 0; k < extra.Length; k++)
        extra[k] = netlist.Nodes.GetOrAssign($"__match_{childPath}_{k}");
    resolvedNodes = [..resolvedNodes, ..extra];
}
```

The `__` prefix is reserved so a user net can never collide. Keying on `childPath` is what makes two
instances of the same design independent.

**This is the only edit this brief makes to an existing Core file outside `src/Core/Devices/`.** If you
find yourself changing `NodeMap`, `ElaboratedNetlist` or the resolution order, stop and report.

---

## 2. Parameters

| parameter | kind | written by |
|---|---|---|
| `Design` | hidden string, base64 JSON | MN-3's Designer only |
| `F1`, `F2`, `Order`, `Response`, `R1`, `R2` | **echo** — visible, read-only | the Designer only |

Echo parameters exist so the user can display the design on the schematic. They are never an input and
must never be read back by the model — `Design` is authoritative and complete. This mirrors `wBond`'s
`Arrays` exactly; read `src/Ui/ViewModels/ParameterEditorViewModel.WBond.cs`'s header for the reasoning
and the "must not also appear as generic text rows" mechanism (`IsWBondPanelParameter`). Add the
equivalent `IsMatchPanelParameter` so `Design` never renders as an unreadable text row.

Until MN-3 exists, placement writes a **default design** that synthesises cleanly: 1–2 GHz, order 3,
both terminations 50 Ω with `Kind = None`, `ChebyshevFano`. A freshly dropped `Match` must simulate
immediately — the same rule `wBond` follows in shipping a default wire rather than an empty array.

---

## 3. The symbol — `src/Ui/Schematic/BuiltInSymbols.cs`

A square body carrying the standard **bandpass** glyph: **three stacked full-cycle sine waves, with a
slash across the top one and a slash across the bottom one**, plus pins left and right.

Build it from primitives that already exist (`src/Ui/Schematic/SymbolModel.cs`):

- body: a rectangle;
- three `SinePrimitive` — `Cycles = 1`, `Axis = SineAxis.Horizontal`, equal `Length`, stacked in `Cy`,
  amplitude small enough that the three do not touch;
- two line primitives, one crossing the top sine and one the bottom, at the same angle;
- two pins, left and right, on the grid.

There are `Sine(...)` and `Poly(...)` helpers in that file (~line 216) — use them. Check the result at
all four rotations and mirrored; the sine primitive already handles rotation, but the slashes are plain
lines and it is easy to draw them so that they read as a strikethrough only at 0°.

---

## 4. Registry, category and palette

`src/Ui/Schematic/SchematicModel.cs` — add `SymbolKind.Match` at the end of the enum with an XML-doc
comment in the style of its neighbours.

`src/Ui/Schematic/ComponentTypeRegistry.cs`:

```csharp
[SymbolKind.Match] = new("Match", "MN",
    Category: ComponentCategory.Matching,
    SearchTerms: ["impedance matching", "filter", "filter design", "transform", "Chebyshev",
                  "Butterworth", "Bessel", "match", "matching", "interstage", "Fano", "Norton",
                  "absorb", "Cgs", "Cds", "Ropt", "bandpass"],
    IsCommon: true),
```

Prefix is `MN`, **not** `M` — `M` is `Mutual`.

`ComponentCategory.Matching` is a **new** enum member (owner decision, 2026-08-19). Four places know
about categories and all four need it:

| file | what |
|---|---|
| `src/Ui/Schematic/ComponentTypeRegistry.cs` | the `ComponentCategory` enum itself |
| `src/Ui/ViewModels/Dock/PaletteTool.cs` ~line 190 | the ordered category list the picker renders |
| `src/Ui/Schematic/LibraryCatalog.cs` `CategorySortKey` | sort order |
| `src/Ui/Schematic/LibraryCatalog.cs` `AllItems` (+ `AllFilterPinnedOrder`) | the palette item itself |

`PaletteTool.RealDisplayName` needs **no** entry — "Matching" is one word and falls through to
`ToString()`. Put `Matching` after `Microstrip` and before `DataFiles` in the picker order; put it in
`AllFilterPinnedOrder` near `WBond`.

Also needed, following what every other 2-pin component does — find them by grepping for an existing
`SymbolKind` such as `SymbolKind.Tline` and covering every hit:
`EditableSchematic` pin geometry, `ComponentTypeRegistry.DefaultParameters` and its engine-reference
mapping (`SymbolKind.Match => "Match"`), auto-naming, and the placement path.

---

## 5. Tests

### 5.1 The invariants that matter

| test | project | what it protects |
|---|---|---|
| **Absorbed elements are not stamped** — a `Match` between two 50 Ω terms does not contain the termination reactances. Build a design with a known absorbed C, stamp it, and compare against a hand-built netlist of the ladder *without* that element | Engine.Tests | §0.1, the invertible mistake |
| **Elementwise ≡ hand-built** — a `Match` and an equivalent hand-built netlist of R/L/C primitives give identical S-parameters to **1e-12** | Engine.Tests | §0.2, and it is MN-5's precondition |
| **DC** — in a DC analysis a `Match` presents its series arms as opens and its shunt arms as shorts; no singular matrix | Engine.Tests | §0.2 reason 1 |
| **HB** — an HB run including the DC harmonic, with a `Match` in the linear part, converges and matches the hand-built equivalent | Engine.Tests | §0.2 reason 2 |
| **Internal nodes are minted per instance** — two `Match` instances of the same design do not share internal nets | Engine.Tests | the `childPath` key |
| **Corrupt `Design`** — refuses at elaboration, message names the instance, never substitutes a default | Core.Tests | §1 |
| **Default design simulates** — a freshly placed `Match` runs a 1-port S-param sweep with no edits | Ui.Tests | §2 |
| **Registry/palette** — `Match` appears in the `Matching` category, the category appears in the picker, prefix is `MN`, auto-naming gives `MN1`, `MN2` | Ui.Tests | §4 |
| **Symbol renders at four rotations + mirrored** | Ui.Tests | §3 |
| **CLI** — a `.cnl` containing a `Match` runs headless under `Cli sparam` | Engine.Tests | the headless promise in `match.md` §2.1 |

### 5.2 Cost

Nothing here is slow. If any single test approaches ~5 s, it is doing too much — say so rather than
tagging it `Benchmark`.

---

## 6. What is NOT in this brief

No Designer window, no probe, no flatten, no plots, no sliders. A `Match` in this brief is edited only
by hand-writing its `Design` parameter, and that is fine: MN-3 is the next brief.

---

## 7. Report

State: the internal-node count for the golden n = 4 design; the measured agreement between the
component and the hand-built netlist; what the DC and HB tests actually did; every existing file you
touched and why. Findings to `src/Core/Match/RESOLVED.md` and `src/Ui/RESOLVED.md`.
