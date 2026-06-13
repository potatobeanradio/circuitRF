# Brief: cnl-cells — emit `define … end` cell blocks in CnlWriter

**Goal.** Teach `CnlWriter` to serialize a `Library` of `Cell` definitions as `define … end`
blocks, so a hierarchical `TestBench`+`Library` round-trips through `.cnl`. The **reader already
parses** these blocks (`CnlReader.ParseDefine` / `parameters` / `end` / `Cell:Inst` lines) — this
is writer-only plus round-trip tests.

Authority: `docs/design/hierarchical-net-extraction.md` §4. Format spec: data-model §10 (the
reader is the de-facto authority — match it exactly).

This brief is **format-only** and independent of the extractor: it serializes whatever `Library`
it's handed. It can land before or after brief-hier-extract.

## File

- `src/Core/Netlist/CnlWriter.cs` — add a `Library`-aware overload + cell-block emission.
- Tests: `tests/Core.Tests` — add hierarchy round-trip tests (locate the existing CNL test file,
  e.g. `CnlWriterTests.cs` / `CnlRoundTripTests.cs`; if none fits, create `CnlHierarchyTests.cs`).

## Step 1 — overload (no breaking change)

Keep the existing `Write(TestBench, string?)` working (current callers + tests pass `(tb, header)`).
Add a `Library`-aware overload and have the old one delegate:

```csharp
/// <summary>Writes a flat TestBench (no cell definitions).</summary>
public static string Write(TestBench tb, string? header = null)
    => Write(tb, null, header);

/// <summary>
/// Writes <paramref name="tb"/> plus the cell definitions in <paramref name="library"/> as
/// `define … end` blocks (emitted before the top-level content, leaf-first as supplied).
/// </summary>
public static string Write(TestBench tb, Library? library, string? header = null)
{
    var sb = new StringBuilder();

    if (!string.IsNullOrWhiteSpace(header))
    {
        sb.AppendLine($"; {header}");
        sb.AppendLine();
    }

    // ── Cell definitions first (define-before-use; reader is order-independent) ──
    if (library is { Cells.Count: > 0 })
    {
        foreach (var cell in library.Cells)
        {
            AppendCell(sb, cell);
            sb.AppendLine();
        }
    }

    // ── existing body: globals, instances, analyses, measurements, raw ──
    foreach (var v in tb.GlobalVariables)
        sb.AppendLine(FormatVariable(v));

    if (tb.GlobalVariables.Count > 0 && HasContent(tb))
        sb.AppendLine();

    foreach (var inst in tb.Instances)
        sb.AppendLine(FormatInstance(inst));

    if (tb.Instances.Count > 0 && HasDirectives(tb))
        sb.AppendLine();

    foreach (var analysis in tb.Analyses)
        sb.AppendLine(FormatAnalysis(analysis));

    foreach (var m in tb.Measurements)
        sb.AppendLine(!string.IsNullOrEmpty(m.Unit)
            ? $"measure {m.Name} = {m.Expression} {m.Unit}"
            : $"measure {m.Name} = {m.Expression}");

    foreach (var raw in tb.RawDirectives)
        sb.AppendLine($"{raw.Kind} {raw.RawLine}");

    return sb.ToString();
}
```

(Refactor: keep the current body intact; just move it into the new overload and insert the cell
block emission before the globals.)

## Step 2 — cell-block helpers

```csharp
private static void AppendCell(StringBuilder sb, Cell cell)
{
    // "define Name (P1 P2 …)" — empty ports → "define Name ()"
    sb.Append("define ").Append(cell.Name)
      .Append(" (").Append(string.Join(' ', cell.Ports)).Append(')').AppendLine();

    // "  parameters n=expr [unit] n2=expr2 …"  (only if any)
    if (cell.Parameters.Count > 0)
        sb.Append("  parameters")
          .Append(' ')
          .AppendLine(string.Join("  ", cell.Parameters.Select(FormatParamDecl)));

    // Instance lines (primitives + nested Cell:Inst), reusing FormatInstance, indented 2 spaces.
    foreach (var inst in cell.Instances)
        sb.Append("  ").AppendLine(FormatInstance(inst));

    sb.Append("end ").AppendLine(cell.Name);
}

private static string FormatParamDecl(ParameterDeclaration pd)
    => string.IsNullOrEmpty(pd.Unit)
        ? $"{pd.Name}={pd.DefaultExpression}"
        : $"{pd.Name}={pd.DefaultExpression} {pd.Unit}";
```

Why this matches the reader:
- `ParseDefine` finds `(`, reads to `)`, splits ports on whitespace (handles `()` → no ports and
  `(a b)` → `[a,b]`).
- `ParseParameterDeclarations` reads `name=expr` tokens with an optional following unit token
  (`Units.IsKnown`) — `FormatParamDecl` emits exactly that shape.
- Instance lines inside the block reuse `FormatInstance`. A **cell instance** is just
  `CellName:Inst net1 net2 … param=val …` (the existing `FormatStandardInstance`, since
  `Reference = CellName`); the reader's general instance path reconstructs
  `Instance(Reference = CellName)`. No special-casing needed for cell instances.
- The reader `Trim()`s every line, so the 2-space indentation is cosmetic and safe.
- Analyses/measures are **never** emitted inside a `define` (the reader rejects them there); cells
  never carry them.

## Step 3 — round-trip tests

Build a hierarchical model in code, `Write` it, read it back with `CnlReader.Read`, and assert
equivalence:

```csharp
// Cell "amp" (ports in,out; one parameter; two primitives)
var cell = new Cell("amp");
cell.Ports.AddRange(["in", "out"]);
cell.Parameters.Add(new ParameterDeclaration("gain", "10", null));
cell.Instances.Add(new Instance("R1", "R", ["in", "out"], [new ParameterAssignment("R", "50", "Ohm")]));
cell.Instances.Add(new Instance("C1", "C", ["out", "0"], [new ParameterAssignment("C", "1", "pF")]));
var lib = new Library("netlist");
lib.Cells.Add(cell);

var tb = new TestBench("tb");
tb.Instances.Add(new Instance("X1", "amp", ["n1", "n2"], [new ParameterAssignment("gain", "20", null)]));

var text          = CnlWriter.Write(tb, lib, "test");
var (rLib, rTb)   = new CnlReader().Read(text);

// Library round-trips
var rAmp = rLib.Find("amp")!;
Assert.Equal(["in", "out"], rAmp.Ports);
Assert.Equal("gain", rAmp.Parameters.Single().Name);
Assert.Equal("10",   rAmp.Parameters.Single().DefaultExpression);
Assert.Equal(2, rAmp.Instances.Count);
Assert.Equal("R", rAmp.Instances[0].Reference);
Assert.Equal(["in", "out"], rAmp.Instances[0].NetBindings);

// Top instance round-trips as a cell instance
var x1 = rTb.Instances.Single();
Assert.Equal("amp", x1.Reference);              // cell reference, not a primitive
Assert.Equal(["n1", "n2"], x1.NetBindings);
Assert.Equal("20", x1.Overrides.Single(o => o.Name == "gain").Expression);
```

Add cases for: a cell with **no parameters** (no `parameters` line emitted/round-tripped), a cell
with **no ports** (`define Name ()`), and a **nested** cell (cell A instantiates cell B — both in
the Library, both round-trip).

## Acceptance

- `Write(tb, lib, header)` emits leaf-first `define … end` blocks, then globals/instances/
  analyses/measures unchanged.
- `CnlReader.Read(Write(tb, lib))` reproduces the Library (names, ports, parameters, instances,
  bindings) and the TestBench.
- Existing flat `Write(tb, header)` tests are unaffected (delegates with `library: null`).

## Out of scope

- Cell-scoped `Variables` emission (v1 cells have none).
- Choosing/ordering the Library — the extractor supplies it leaf-first (brief-hier-extract).
