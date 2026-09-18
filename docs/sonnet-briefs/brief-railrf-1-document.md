# Brief 1 — the document: the rail set, and what a source and a load are anchored to

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail1-n`
**Area:** `src/Design/RailRf/` · **Depends on:** nothing · **Blocks:** every other brief in the series
**Design note:** [`railrf.md`](../design/railrf.md) §2.2, §2.3, §11.3, §11.4

---

## 0. What this brief delivers

The **document** — the thing a railRF window opens, the `rail` verb reads, and revision control keeps.
No solve, no extractor, no UI, no window. Framework-free types in `src/Design/RailRf/`, a JSON reader and
writer, and the eight places a new document extension has to be declared.

| Type | File | What it is |
|---|---|---|
| `RailDocument` | `src/Design/RailRf/RailDocument.cs` | The whole document: the artwork reference, the stackup reference, the part library reference, the rail set, and the settings. |
| `RailSpec` | `src/Design/RailRf/RailSpec.cs` | One rail: net, reference layer, reference extent, sources, loads, target, band, aggressors. |
| `RailPortAnchor` | `src/Design/RailRf/RailPortAnchor.cs` | **Where a source or a load sits.** A refdes and a pin, or — as a fallback — a coordinate. |
| `RailSource`, `RailLoad` | same folder | A branch on a rail, each carrying a `RailPortAnchor` and its own model or current. |
| `RailTarget` | `src/Design/RailRf/RailTarget.cs` | A drop budget, a flat Z, a piecewise mask, or a transient spec. |
| `RailAggressor` | `src/Design/RailRf/RailAggressor.cs` | A name, a frequency and a harmonic count. |
| `RailDocumentIo` | `src/Design/RailRf/RailDocumentIo.cs` | Read and write `.crail`. |
| `RailOrder` | `src/Design/RailRf/RailOrder.cs` | The dependency order over the rail set, and the cycle refusal. |

`src/Design` references `src/Core` and `src/Engine`, so `Bbox`, `Technology`, `LayoutView` and the
expression engine are all in scope. Avalonia is not, and `tests/Firewall.Tests` fails the build if
anything here reaches for it.

---

## 1. `R-rail1-1` — a source and a load are anchored to a PAD, and a coordinate is the fallback

This is the largest change rev 4 made to the note and it is the one thing in this brief that is easy to
get subtly wrong. §2.2:

> **a pad moves when the board is re-laid out and a coordinate does not**

The A/B comparison of brief 16 pairs two designs by refdes and pin. A document whose ports are
coordinates is a document whose ports silently point at different copper after a re-layout, and the
comparison then compares nothing while looking entirely normal. So:

```csharp
/// <summary>Where a source or a load sits on the board.
///
/// <para><b>A refdes and a pin, wherever one can be had.</b> A coordinate is accepted where there is
/// no placement file and no board netlist, and it is the FALLBACK rather than the spelling: a pad
/// moves when the board is re-laid out and a coordinate does not, which is exactly what brief 16's
/// comparison would get wrong on the one input it cannot check.</para></summary>
public sealed record RailPortAnchor
{
    /// <summary>The component reference — <c>U1</c>, <c>BT1</c>. Null only on a coordinate anchor.</summary>
    public string? Refdes { get; init; }

    /// <summary>The pin, as the netlist or the footprint names it — <c>VDD</c>, <c>1</c>. A pin FIELD
    /// (an IC's whole set of power pins) is named by its net at that refdes, and resolves to every
    /// matching pad; see R-rail1-2.</summary>
    public string? Pin { get; init; }

    /// <summary>The fallback, in DBU on the artwork's own coordinate system. Null on a pad anchor.</summary>
    public (long X, long Y)? Point { get; init; }

    public bool IsPad => Refdes is not null;
}
```

**Both forms are representable and exactly one is set.** `RailDocumentIo` refuses a document carrying
both or neither, naming the rail and the row — not because it is malformed JSON but because a silently
preferred one of the two is the bug this record exists to prevent.

### `R-rail1-2` — a load port is a pin FIELD, and it is tied into one port

§2.2: *"A load port is an IC's power/ground pin field — a set of pads, not a point — and railRF ties them
into one port, because that is what the die sees."*

So an anchor resolves to a **set** of pads, and the resolution is not this brief's job — brief 3's
extractor does it, against the netlist and the placement. What brief 1 owns is that the anchor is
**capable of naming a field**: `Pin` naming a net at that refdes (`U1.VDD` where `VDD` reaches six pads)
resolves to six pads, and `Pin` naming a single pin resolves to one. A record that could only ever name
one pad would make the note's own worked example unrepresentable.

---

## 2. `R-rail1-3` — the document holds the RAIL SET, and one rail is analysed at a time

Both halves matter and rev 4 re-closed Q-6 on exactly this distinction:

- **The document holds every rail the board has.** Review's boards run a primary cell into a converter or
  an LDO that makes a second voltage. A document that could describe one net could not describe one of
  these boards.
- **One rail is *analysed* at a time.** There is no simultaneous multi-rail solve, ever (overview §3).

```csharp
public sealed class RailDocument
{
    /// <summary>The imported artwork, as a reference to a cell in the workspace — never a private copy
    /// of the geometry (§2.3). Relative to the document, so an archived workspace still resolves
    /// (the repointing rule the workspace archive already follows).</summary>
    public string? ArtworkCellRef { get; set; }

    /// <summary>Every rail on this board, in declaration order. RailOrder computes the SOLVE order.</summary>
    public List<RailSpec> Rails { get; } = [];

    public string? PartLibraryRef { get; set; }
    public RailSettings Settings { get; set; } = new();
}
```

### `R-rail1-4` — a regulator is one part in two rows, and `RailOrder` is what that buys

§2.2: *a regulator is a load on its input rail and a source on its output rail.* That is **two rows
naming the same refdes** — a `RailLoad` on the input rail's list and a `RailSource` on the output rail's —
and nothing in the model links them except the refdes. `RailOrder` reads that:

```csharp
public static class RailOrder
{
    /// <summary>The order the rails must be solved in, so a regulator's input voltage is the UPSTREAM
    /// ANSWER rather than a nominal.
    ///
    /// <para>A rail B depends on rail A when some refdes is a LOAD on A and a SOURCE on B. Returns the
    /// topological order, or a refusal naming the two rails in the cycle.</para></summary>
    public static RailOrderResult Resolve(RailDocument doc);
}

public sealed record RailOrderResult(IReadOnlyList<string> Order, string? Refusal);
```

**The cycle refusal is load-bearing and it is not a limitation to be lifted.** Note §9: solving the rails
*together* — a regulator as a two-port with a forward transfer and a PSRR — is a different model, it needs
exactly the data §8.2 records as frequently impossible to obtain, and *"it would be entered by accident the
first time someone asked for a cycle in the order to be supported."* The refusal names both rails and the
refdes that closes the loop.

### `R-rail1-5` — what a regulator needs typed, and what happens when it is not (Q-20)

Two nullable fields on `RailLoad`, both typed and neither ever defaulted:

```csharp
/// <summary>The regulator's own input current at the operating point being judged. Null on an
/// ordinary load, which states its current in <see cref="DcCurrentA"/> like any other.</summary>
public double? RegulatorInputCurrentA { get; init; }

/// <summary>The minimum input voltage this part needs to regulate.
///
/// <para><b>Null is not zero and not a default.</b> Without it railRF can report the input rail's drop
/// and cannot report that the drop BROKE the rail downstream — which is the finding the whole chain
/// exists to produce (§2.2). Brief 5 reports the drop always and the headroom finding only where this
/// is stated, and says on the report which rails had no minimum.</para></summary>
public double? MinimumInputVoltageV { get; init; }
```

---

## 3. `R-rail1-6` — the reference is a layer AND an extent, and the extent is stamped on everything

§2.2's table, verbatim in the model:

```csharp
public enum RailReferenceExtent
{
    /// <summary>The actual copper on that layer. Honest; a fragmented reference shows as one.</summary>
    AsImported,

    /// <summary>That layer taken as solid within the board outline. Removes return constrictions the
    /// real board may have — OPTIMISTIC.</summary>
    FilledToOutline,

    /// <summary>The layer taken as unbounded at its own z. Removes edge effects too; an upper bound,
    /// and the only way to compare two different outlines on equal terms.</summary>
    Infinite,
}
```

`AsImported` is the default. **The choice is carried on every result and stamped on every plot, every
table and every export** — that is brief 5's and brief 12's job, but the reason lives here: *the second
and third are optimistic and a reader who does not know which was used cannot tell.*

**The reference layer is asked for, never inferred** (Q-8). The model therefore has no "infer the
reference" path at all: `RailSpec.ReferenceLayer` is a `LayerKey?` and a null one is a document that is
not yet ready to solve. Brief 7's window proposes a layer and says why; brief 10's verb refuses and names
the flag. Neither of them guesses, and the model does not offer a way to.

---

## 4. `R-rail1-7` — a load with no current is an observation port

Q-16, and it is a model rule rather than a UI one:

```csharp
/// <summary>The DC current this port draws. <b>Null means this is not a load</b> — it is an OBSERVATION
/// port over frequency and contributes nothing to the DC solve (§2.2). Never zero-by-default: a
/// defaulted zero and a stated zero are the same number and mean different things, and the DC report
/// has to list an observed port AS observed rather than omitting it.</summary>
public double? DcCurrentA { get; init; }

/// <summary>Optional peak current, which the transient form of the target consumes.</summary>
public double? PeakCurrentA { get; init; }
```

The test is the property: a document with three loads, one of them currentless, solves with **two**
current injections and reports **three** ports.

---

## 5. `R-rail1-8` — the target is one of four things and the document says which

```csharp
public enum RailTargetKind { DropBudget, FlatImpedance, Mask, Transient }
```

- `DropBudget` — millivolts, and it is what the DC mode is judged against.
- `FlatImpedance` — milliohms.
- `Mask` — a table of (frequency, limit) points. **Per observation port**, so it lives on the load row
  rather than on the rail.
- `Transient` — ΔI, ΔV and a rise time, **from which railRF derives the flat target and the top of the
  band that matters.** That derivation is arithmetic and belongs here, not in the window: the same
  document opened headlessly has to produce the same target.

A rail carries a `DropBudget` and one frequency-domain target; they are not alternatives.

---

## 6. `R-rail1-9` — the aggressors are an input, not a decoration

§2.2's last paragraph is the justification and it is worth carrying into the type's doc comment: *"A PDN
peak matters if something on the board excites it, and this is the only input that knows whether it
does."*

```csharp
public sealed record RailAggressor(string Name, double FrequencyHz, int Harmonics)
{
    /// <summary>Where this row came from — recognised from the BOM, or typed. Shown on the row, because
    /// a pre-filled frequency a user did not check is exactly the one that will be wrong.</summary>
    public RailAggressorOrigin Origin { get; init; } = RailAggressorOrigin.Typed;
}
```

Brief 2's BOM reader pre-fills them where a crystal or a converter can be recognised; brief 12 draws them
and runs the coincidence check. Brief 1 only has to hold them, and to hold **where each came from**.

---

## 7. `.crail`, and the eight places a document type is declared

### `R-rail1-10` — the format

JSON, `System.Text.Json`, `WhenWritingNull`, a `FormatVersion` integer, on the same shape `.charm` and
`.wbond` already use. **Read it before writing this** — `src/Ui/Harmonica/` carries the precedent,
including the marker-guarded shape brief 9's clipboard payload will need.

Numbers are stored in **base SI** and carry their display scale separately, which is the sweep-unit trap
recorded in `src/Engine/RESOLVED.md`: a mark read without its scale once produced a run at 2 Hz that
looked entirely normal. A frequency field is Hz, a current field is amps, a length is DBU.

### `R-rail1-11` — all eight registrations, in one change

A new extension that is declared to an operating system with no case in the dispatcher **launches
circuitRF and opens nothing**, which reads to a user as a broken file. Six parity tests in
`tests/Ui.Tests/WBondStandaloneTests.cs` already hold the lists shut against each other, and they will
fail loudly the moment one of these is missed:

| # | Place | What to add |
|---|---|---|
| 1 | `src/Ui/App.axaml.cs`'s `OpenFiles` dispatcher | `case ".crail":` beside `".charm"` |
| 2 | `src/Ui/ViewModels/WorkspaceViewModel.cs`'s open routing | `case ".crail": OpenRailPath(abs); return true;` |
| 3 | `src/Ui/Schematic/WorkspaceScanner.cs` | `".crail" => NodeKind.RailFile` |
| 4 | `src/Ui/Assets/macOS/*.plist` (all three) | a `CFBundleDocumentTypes` entry, role **Editor** in circuitRF |
| 5 | `packaging/windows/*.wxs` | the file association |
| 6 | `packaging/linux/circuitrf.desktop` + the mime file | **both**, and they must claim the same set |
| 7 | `src/Cli/DocumentKinds.cs` | `".crail" => DocumentKind.Rail`, so `check`, `explain`, `find` and `render` classify it by kind rather than calling it unreadable |
| 8 | The project-tree icon and its Properties panel stub | brief 7 fills the panel; brief 1 adds the node kind so the file is not invisible in the tree |

The parity tests to run, by name:

```
dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~WBondStandaloneTests"
```

`EveryDocumentTypeCircuitRfClaims_IsActuallyHandledByItsOpenFilesDispatcher`,
`EveryDocumentTypeTheWindowsInstallerClaims_IsActuallyHandledByItsOpenFilesDispatcher`,
`EveryDocumentTypeTheAppDispatcherAccepts_IsOpenedByTheWorkspaceViewModel`,
`AllThreePlatformsClaimTheSameDocumentTypes`,
`EveryDocumentTypeTheLinuxPackageClaims_IsActuallyHandledByItsOpenFilesDispatcher`,
`TheDesktopEntryAndTheMimeFileClaimExactlyTheSameTypes`.

**All six must be green before this brief is done.** They are the reason this is one change and not
eight.

---

## 8. Tests — `tests/Ui.Tests/RailRf/RailDocumentTests.cs`

No window, no app host. These are pure model tests.

- **Round trip.** A document with two rails, three sources, five loads, a mask, four aggressors and a
  regulator in two rows writes and reads back element-wise equal, and the JSON is byte-identical on a
  second write.
- **`R-rail1-1`'s refusal.** An anchor with both a refdes and a point, and one with neither, are each
  refused by name.
- **`R-rail1-4`'s order.** A three-rail ladder (cell → regulator → regulator) resolves to the ladder's
  own order. A rail pair where each is a load on the other is **refused, naming both rails and the
  refdes**, and the refusal is asserted as a sentence rather than as a thrown type.
- **`R-rail1-5`.** A regulator load with no `MinimumInputVoltageV` round-trips as null, and nothing in
  the model substitutes a value.
- **`R-rail1-7`.** Three loads, one currentless: `doc.Rails[0].Loads.Count(l => l.DcCurrentA is not null)`
  is 2 and `Loads.Count` is 3.
- **`R-rail1-8`.** A transient target derives the same flat Z and band top from the same ΔI/ΔV/rise on
  two separate reads — the derivation is a pure function and is tested as one.
- **Forward compatibility.** A `.crail` carrying an unknown key reads without loss of the keys it does
  know, on the `.ctech` precedent. A document written by this version and read by it changes no byte.

---

## 9. Scope

- **No solve, no extractor, no mesh.** Briefs 3-6.
- **No window, no view model, no Avalonia.** Brief 7. The three `src/Ui` registrations in `R-rail1-11`
  are the exception, and they are one line each.
- **No readers for placement, BOM or the part library.** Brief 2. `PartLibraryRef` is a path here and
  nothing resolves it yet.
- **No import dialog.** Brief 7. The document holds `ArtworkCellRef` and does not know how it got there.
- **No CLI verb.** Brief 10. `DocumentKinds.cs` gets the extension so the *existing* verbs classify it;
  `rail` does not exist yet.

**On completion:** record findings in `src/Design/RESOLVED.md`. Never in a CLAUDE.md.
