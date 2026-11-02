# Brief — harmonicaRF R7B: Set DUT — a device combo, no Diode, and a real SDD equation editor

**Read first:** `src/Ui/Views/Dialogs/HarmonicaSetDutDialog.axaml` + `.axaml.cs`,
`src/Ui/Harmonica/HarmonicaDutEditor.cs`, `src/Ui/Harmonica/HarmonicaDutCatalog.cs`,
`src/Ui/Harmonica/HarmonicaInputs.cs` (`DeclaredModelParameters`, ~line 205),
`src/Harmonica/CircuitModel.cs` (`DutSpec`, `StructuralKey` at ~line 462),
`src/Harmonica/HarmonicaNetlist.cs` (`Build`, `DutLine`), `src/Harmonica/CharmIo.cs` (`CharmDut`),
`src/Ui/Schematic/VarTextParser.cs`, `src/Ui/ViewModels/VarEditorViewModel.cs`,
`src/Ui/Views/Dialogs/VarEditorView.axaml`, `src/Core/Netlist/CnlReader.cs` (the line grammar in its
header comment), `src/Core/Elaboration/Elaborator.cs` (`ResolveSddParameters` and
`InjectSddScopeVars`, ~line 1014–1080), `src/Core/Devices/ComponentModelFactory.cs`
(`CreateSddModel`, ~line 1104 — read the comment block listing what the parameters dictionary may
hold), and `src/Core/Expressions/` (`Parser`, `Evaluator`, `Scope`, `AstWalker`).

**Do NOT update any `CLAUDE.md`.** Write to `src/Ui/RESOLVED.md` or `src/Harmonica/RESOLVED.md` only
if you find something genuinely worth recording.

Tag new comments `R7B §n`.

---

## 0. What the owner asked for

> "Remove the Diode option from the Set DUT dialog. We don't loadpull diodes."

> "In Set DUT dialog, change the radio button selector for the Device to be a combobox."

> "In Set DUT dialog, when SDD option is selected, instead of stating 'This model declares no
> parameters here.', show the drain current equations for the ports. Also, make it accept full
> expressions of variables. And have input UI be similar to the VAR Variables dialog box in Text
> mode. (Ie. One variable per line: name = expression. Reuse the expression engine and check for
> cycles.) … Reuse as much of the VAR Variables dialog box in Text mode as possible. Robust
> validation. Can we have the variables and the current equations all within one Text editor? Or is
> it better to separate the current equations from the vars?"

§3 answers that last question: **one editor.** The reasoning is there; read it before deciding you
disagree.

---

## 1. Remove Diode from the dialog — but not from the model

Delete the `KindDiode` radio (it becomes a combo item that simply is not offered — see §2) and the
`DutKind.Diode` arm of `HarmonicaSetDutDialog.OnKindChanged`.

**Do not delete `DutKind.Diode` itself**, nor `HarmonicaDutEditor.SetKind`'s Diode arm, nor
`HarmonicaNetlist.DutLine`'s `DutKind.Diode` branch, nor `HarmonicaDutCatalog`'s Diode defaults.
A `.charm` saved with a Diode DUT must still open, still solve and still round-trip — silently
failing to load somebody's saved file is a far worse outcome than an unofferable device kind. If a
document arrives carrying `Kind == Diode`, the dialog should show it as the current selection (a
combo item present *only* in that case, marked e.g. `Diode (legacy)`) so the user can see what they
have and switch away from it. Say so in `RefreshStatus` if that is simpler than a conditional combo
item — but the state must not be invisible and must not be silently rewritten.

---

## 2. Device kind: radio row → ComboBox

`HarmonicaSetDutDialog.axaml:24–30` is four `RadioButton`s in a `StackPanel`. Replace with a single
`ComboBox` (`x:Name="KindCombo"`), same row, same `Device` label and `Width="70"` label column, items
in this order:

1. `SDD equations`
2. `Native FET`
3. `External model`

The kind-specific chooser (`LawCombo` / `SddChooser` / `ExternalChooser`) and `ApplyKindVisibility`
are unchanged — they just switch on the combo's selection instead of the radios'. Keep `_loading`
guarded exactly as the radios were: `SelectionChanged` fires during construction.

The **SDD 2-port / 3-port** choice stays a pair of radio buttons — it is a two-way structural choice
with an explanatory paragraph beside it, and the owner asked only about the Device selector. Same for
the external `Model file` / `Kit part` pair.

Map combo index ⇄ `DutKind` in **one** place (a `static readonly DutKind[]`), not in two switch
statements that can disagree.

---

## 3. The SDD editor

### 3.1 Why the box is empty today

`HarmonicaInputs.DeclaredModelParameters` returns `[]` for `DutKind.Sdd`
(`HarmonicaInputs.cs:218`) — deliberately, because R-h9c-5 removed SDD equations from the *readout
strip* on the grounds that "§6's Set DUT dialog now edits them properly". The dialog then asks that
same function what to render, gets nothing, and prints `"This model declares no parameters here."`
The strip half of that decision is right; the dialog half never landed.

**Do not "fix" it by making `DeclaredModelParameters` return the equations.** That would put them
back on the strip, which the owner explicitly removed. The dialog needs its own SDD branch.

### 3.2 One editor, not two

**Decision: one text editor holding variables and equations together**, exactly as the owner pasted
them. Reasons, in order of weight:

1. The syntax is identical — `name = expression` — and `I[2,0]` is just a name whose spelling puts it
   in the equation partition. A second editor would be the same widget with the same grammar and a
   different title.
2. A model is shared, pasted and archived as **one** block. Two boxes means two copy operations and a
   silent way to paste half a model.
3. The destinations (`§3.5`) differ, but that is an emission detail, not a user-facing one. The user
   should not have to know that `Sc` becomes a netlist global and `I[2,0]` becomes an instance
   parameter.

Make the partition **visible** rather than structural: a status line under the box reading
`7 variables · 2 equations · 2 ports` that updates as the user types, plus per-line error reporting
(§3.6). If the user cannot tell which lines are which, the single editor has failed and you should
report that.

Line grammar (a superset of `VarTextParser`'s, which is why you reuse it):

- blank → ignored
- `;` or `#` or `//` first → comment, preserved verbatim on round trip
- `NAME = EXPRESSION` where `NAME` matches the SDD equation shapes
  (`I[p,w]`, `I[p]`, `Q[p]`, `H[w]`, `C[n]`, `Cport[n]`, `In[…]`, `Nc[…]` — the regexes are already
  written in `ComponentModelFactory.cs:~1087–1102`, **reference them, do not re-spell them**) →
  an **equation**
- `NAME = EXPRESSION` where `NAME` is a plain identifier → a **variable**
- anything else → an error on that line

### 3.3 Reuse `VarTextParser`

`src/Ui/Schematic/VarTextParser.cs` is 109 lines, framework-free, already unit-tested, and already
does: split lines, recognise blanks, recognise `#`/`//` comments, split on the first `=`, reject an
empty LHS, reject a line with no `=`, find duplicate names, and serialise back. That is the whole of
the lexical layer.

Use it as-is. It lives in `CircuitRF.Ui.Schematic`; harmonicaRF is in the same assembly, so this is a
`using`, not a move. **Two additions are needed and both belong in `VarTextParser`, not in a copy:**

- `;` as a comment lead (the `.cnl` comment character — an SDD block a user pastes out of a netlist
  will use it). Additive; existing callers are unaffected.
- Preserve blank and comment lines through `SerializeLines` so a round trip through the dialog does
  not silently delete the user's own blank line between the variables and the equations. The current
  overload takes `IEnumerable<EditableParameter>`; add a sibling that takes the parsed `VarLine`
  list, rather than changing the existing signature.

If either addition turns out to break `VarEditorViewModel` or `VarTextParserTests`, stop and report
rather than forking the file.

### 3.4 Where the text lives

Add **one** field to `DutSpec` (`src/Harmonica/CircuitModel.cs:28`):

```csharp
/// <summary>R7B §3.4 — the SDD editor's verbatim text (variables, equations, comments, blank lines,
/// in the order the user wrote them). The AUTHORITATIVE user-facing form; <see cref="Parameters"/>
/// is derived from it … Null for every non-SDD kind, and for an SDD loaded from a .charm written
/// before this field existed.</summary>
public string? SddText { get; init; }
```

- `HarmonicaDutEditor` holds it, and `Build()` **derives** `Parameters` from it whenever
  `Kind == Sdd` and `SddText` is non-null. One source of truth for the user; one derived map for the
  engine; nothing downstream of `DutSpec.Parameters` changes at all.
- `CharmIo`: persist `SddText` additively on `CharmDut` (`src/Harmonica/CharmIo.cs:381`). **Absent
  means: reconstruct the text from `Parameters`** (variables first — there will be none in an old
  file — then equations sorted by port), so every existing `.charm` opens and shows something
  sensible. Follow `CharmIo`'s own absent-means-default rule; an untouched document must still
  re-serialise byte-for-byte, so do **not** write `SddText` for a DUT that has none.
- `StructuralKey` (`CircuitModel.cs:462`) already hashes `Parameters`. **Leave it alone** — it must
  move when the equations or variable *values* change and must NOT move when only a comment or a
  blank line does. Deriving `Parameters` from the text gives exactly that for free. Do not add
  `SddText` to the key.

### 3.5 How variables reach the engine — globals, not instance parameters

`HarmonicaNetlist.Build` currently emits every `DutSpec.Parameters` entry onto the SDD instance line
(`DutLine`, `HarmonicaNetlist.cs:~150`). Change the partition:

- keys matching the SDD equation regexes → **instance-line parameters**, unchanged
- every other key → a **top-level global variable line** `name = expression`, emitted near the top of
  the generated `.cnl`, before the DUT line

Two facts make globals the correct route, and both are already in the code — verify them, then rely
on them:

1. `CnlReader`'s line grammar (its header comment, `CnlReader.cs:12`) includes `name = expr [unit]`
   as a global variable, parsed by `IsVariableAssignment` / `ParseVariableAssignment`.
2. `Elaborator.InjectSddScopeVars` (`Elaborator.cs:1049`) walks each SDD equation's AST, collects its
   free names, skips `_v…`/`_c…`, and **resolves every remaining one from the enclosing scope** via
   `_evaluator.Resolve` — which is where cycle detection and unresolved-name reporting already live.
   `CreateSddModel` then picks each up as a numeric parameter (`ComponentModelFactory.cs:~1122`,
   "Collect resolved numeric parameters (scope variables like B, Sc, …)").

**Do not put variables on the instance line instead.** Two reasons, both fatal: the generic
instance-line parser splits on whitespace, so an expression with a space in it is silently truncated
into net names (`HarmonicaNetlist`'s own class comment warns about exactly this); and a variable that
references another variable would be evaluated in the parent scope where the first one does not
exist.

### 3.6 Validation — "robust", concretely

Do this in a new framework-free static class (`src/Ui/Harmonica/HarmonicaSddText.cs`) so it is
testable without a window, matching `HarmonicaDutEditor`'s own stated split. It should expose roughly
`Parse(text, portCount) -> (ordered variables, ordered equations, IReadOnlyList<Problem>)` where a
`Problem` carries a 1-based line number, the offending text, and a message.

Checks, all of them:

1. **Lexical** — `VarTextParser`'s own: no `=`, empty name, duplicate name.
2. **Syntax** — `Parser.Parse` on every RHS. Report the parser's own message with the line number
   prepended. Never swallow it.
3. **Variables must be constants.** Build a `Scope`, `Bind` every variable, then `Evaluator.Resolve`
   each one. This gets three things at once: a **cycle** is caught by the evaluator's own resolution
   stack (`Evaluator.cs:12`, `:49`), an unknown name is caught as `UnresolvedNameException`, and a
   non-Real result (Complex/Bool/String) is caught by kind — SDD equations are real-only
   (`Elaborator.cs:~1070` throws on Complex, so catch it here where you can name the line instead).
   A variable that references `_v1` is an error: a variable is evaluated once at elaboration, not per
   bias point.
4. **Equations** — `AstWalker.CollectRefs` on each; every free name must be a declared variable,
   `_v1`…`_vN` where `N = SddPortCount`, or `_c<n>`. Anything else is an error naming the line **and
   the name** ("line 12: `Vt` is not a variable declared above, a port voltage (`_v1`…`_v2`) or a
   control current"). Today an undeclared name is silently skipped by `InjectSddScopeVars` and then
   surfaces as an opaque factory failure at solve time — that is the failure mode this check exists
   to remove.
5. **Port indices** — every `I[p,w]`/`Q[p]` must have `1 ≤ p ≤ SddPortCount`. Changing 3-port to
   2-port while an `I[3,0]` line exists must be reported, not silently dropped.
6. **At least one `I[p,0]`.** An SDD with no current equation is a device that does nothing.
7. **Reserved names** — a variable may not be named `_v…`, `_c…`, `freq`, or anything matching an
   equation regex. Reject with the reason.

Validation runs on every keystroke (the text is small; `Parser.Parse` on ten short expressions is
nothing) and drives the same reject-and-keep behaviour `VarEditorViewModel` already uses: the text is
never rewritten under the user, errors are listed, and `ApplyButton.IsEnabled` is false while any
error stands. Feed the message into the dialog's existing `StatusLabel` /
`HarmonicaDutEditor.Validate()` path so there is one place the dialog asks "can I commit".

### 3.7 Sanitising — the trap that will bite you

**The owner's own default text contains an invisible `U+200E` LEFT-TO-RIGHT MARK** immediately after
`Periphery_mm` on the first line. Pasted text routinely carries U+200B/U+200E/U+200F/U+FEFF and
non-breaking spaces, and an identifier with one glued on is a *different* identifier that will fail
to resolve with a message naming a symbol that looks correct on screen. This is the single most
likely way this feature ships broken.

On parse: strip `U+200B`–`U+200F`, `U+FEFF`, and `U+00A0` (→ ordinary space) from every line before
anything else looks at it, and normalise CRLF. Do it once, in `HarmonicaSddText.Parse`. Add a test
that feeds `"Periphery_mm‎ = 1.0"` and asserts a variable named exactly `Periphery_mm`.

On emission of an equation onto the instance line: **remove all whitespace**. Whitespace is
insignificant in these expressions (two adjacent identifiers are never legal), and the instance-line
parser reads a space as a net separator. Global lines may keep their spaces. Add a test that an
equation containing spaces round-trips through `HarmonicaNetlist.Build` → `CnlReader` → `Elaborator`
to a working model.

### 3.8 The new default model

Replace `HarmonicaViewModel.DefaultModel()`'s folded-coefficient SDD
(`HarmonicaViewModel.cs:~2033`) with the owner's variable form, as `SddText`:

```
Periphery_mm = 1.0
Sv = -0.837
Sc = 0.71
TV0 = 4.268
TC = 1.507
th = 0.001
a = 0.176
g = 0.089
lam = 0.0012
B = 1130

I[1,0] = _v1/50
I[2,0] = Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2
```

**This is the same device.** Substituting the constants into the variable form reproduces the current
`I[2,0]` string term for term (`B*TC` = 1130·1.507, `_v2*a` = `_v2*0.176`, `g*(TV0 - _v1 + …)` =
`0.089*(4.268-_v1+…)`, `(_v2*lam+1)` = `(_v2*0.0012+1)`, the trailing `/2`, and
`Periphery_mm` = 1.0). **Prove it rather than trusting this paragraph** — see the gate in §5.3. If it
does not agree, the transcription is wrong and you must say so rather than adjusting a coefficient
to make a test pass.

`HarmonicaDutEditor.SetKind`'s `DutKind.Sdd` arm reseeds from
`HarmonicaViewModel.DefaultModel().Dut.Parameters` (line 78) — it must reseed `SddText` too, or
switching to SDD gives a device with equations and no variables.

### 3.9 The dialog surface

When the kind is SDD, `ParamHost` hosts the editor instead of the per-parameter rows:

- header text: `SDD equations and variables` (not "…as the model itself declares them" — for an SDD
  the user *is* the declaration)
- a monospaced, multi-line, wrapping `TextBox` filling the available height, `AcceptsReturn="True"`,
  `AcceptsTab="False"`
- the status line from §3.2 and the error list from §3.6 beneath it
- borrow the look from `VarEditorView.axaml`'s text mode; do not re-invent spacing

For every other kind the existing per-parameter rows are unchanged.

**Trap:** `RebuildParameterRows` currently calls `_editor.SetParameter(name, input.Text)` for every
row it builds, to seed values the user never touches (line 295). The SDD branch must not fall through
into that loop, or it will write equation keys back as if they were declared parameters.

---

## 4. What is out of scope

- Do not change the SDD engine, `SddEvaluator`, `SddModel`, or `CreateSddModel`.
- Do not change the readout strip (`DeclaredModelParameters` stays `[]` for SDD — §3.1).
- Do not add a Rows/Table mode. Text mode only, which is what was asked for.
- Do not touch `H[w]` weighting or control-current (`C[n]`) semantics; parse and pass them through.

---

## 5. Gates

1. `dotnet test tests/Ui.Tests --no-build`, `dotnet test tests/Harmonica.Tests --no-build`,
   `dotnet test tests/Core.Tests --no-build`, `dotnet test tests/Firewall.Tests --no-build` — each as
   its own invocation.
2. New `tests/Ui.Tests/Harmonica/HarmonicaSddTextTests.cs` covering §3.6's seven checks, §3.7's
   invisible-character case, and a full round trip `text → Parse → DutSpec → CharmIo → DutSpec →
   Serialize → text` that is stable (comments and blank lines survive; a second round trip is a
   fixed point).
3. **The equivalence gate — the one that matters.** A test that builds the OLD folded-coefficient
   `I[2,0]` string and the NEW variable form, evaluates both through `SddEvaluator.EvalDouble` over a
   grid of `(_v1, _v2)` covering the device's real operating range (say `_v1 ∈ [-6, 0]` step 0.25,
   `_v2 ∈ [0, 60]` step 2.5) and asserts agreement to `1e-12` relative. The old string is in this
   brief's own §3.8 discussion and in git history at `HarmonicaViewModel.cs`. Paste it into the test
   as a literal — it is the oracle, and it must not be deleted from the test when it is deleted from
   the product.
4. An end-to-end netlist gate: `HarmonicaNetlist.Build(DefaultModel())` produces text that
   `CnlReader` + `Elaborator` accept, with the variables appearing as global lines and the equations
   on the instance line whitespace-free. `HarmonicaContext` must elaborate it and `PinSearch` must
   still converge — the cheapest form of that is an existing harmonicaRF solve test still passing.
5. **Run the app** (`/run`). Screenshot the Set DUT dialog with the SDD kind selected, showing the
   default text; screenshot it again with a deliberate cycle (`a = b`, `b = a`) showing the error and
   a disabled Set DUT button. `tests/Ui.Tests` is forbidden from calling Avalonia runtime APIs, so a
   screenshot is the only evidence the dialog actually renders.
6. Report: whether one editor turned out to be right (§3.2) — with the status line's wording — and
   any check in §3.6 you could not implement, with the reason.
