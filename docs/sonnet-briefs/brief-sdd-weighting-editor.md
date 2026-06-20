# Brief #4 (SDD weighting): surface `I[p,w]` + `H[w]` in the parameter editor (Option A — minimal)

Design ref: `docs/design/sdd.md` §2–3. Depends on brief #3 (parser — makes `I[p,w≥2]` + `H[w]=expr`
netlist-real). This is the **UI** half: let a user author those equations in the parameter editor. **Option A
(minimal):** lean on the fact that SDD rows are already free-form named String params with an **editable name
field** — add grammar **validation** so bad names are caught inline instead of at run time, plus light
discoverability. No structured/dedicated editor (that's a possible Option-B follow-on).

UI only (`src/Ui`). Build **0W/0E** (this csproj is TreatWarningsAsErrors; capture nullable-on-property into
locals; no `<`/`>` in XML doc comments). No Core/engine changes — brief #3 owns those.

---

## What already works (verified on disk — don't rebuild it)

- `ParameterRowViewModel.NameEditable` is **already true for SDD** (`UserParamTemplate(Sdd) is not null`), so
  the row's **name cell is editable today** — a user can already rename a row to `I[1,2]`, `Q[1]`, or `H[2]`.
- `CanonicalSort` / `TryParseTemplateIndex` treat any name that doesn't match the `I[{0}]` template as
  "non-indexed" and **preserve it in original order** — so `I[1,2]`, `Q[1]`, `H[2]` already survive sort and
  round-trip without corruption. Good; leave that alone.
- The "+" button (`AddGroup`) adds single-index `I[{n}]` slots. Single-index `I[n]` is still valid SDD (the
  landed single-index brief), so **keep "+" as-is** — don't try to make it emit two-index forms in Option A.

**The actual gap:** `ParameterRowViewModel.CommitName` only checks empty + duplicate. It does **no grammar
validation**, so `H[2]` commits fine but so does `I[1,` or `H[1]` (illegal redefinition) — the user gets no
feedback until a run fails deep in elaboration. Option A closes that.

## 1. Add SDD name validation on commit

In `ParameterRowViewModel.CommitName`, after the empty/duplicate checks and **only when
`_ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd`**, validate `name` against the SDD equation grammar.
Mirror the factory's regexes (single source of truth — see `ComponentModelFactory.CreateSddModel`; if those
patterns are `internal`/`public` and reachable from Ui without a firewall break, reuse them; otherwise
duplicate the small set here with a comment pointing back). Accept exactly:
- `I[p]`            (single-index current, p ≥ 1)
- `I[p,w]`          (two-index, p ≥ 1, w ≥ 0)
- `Q[p]`            (charge shorthand, p ≥ 1)
- `H[w]`            (weighting function, **w ≥ 2 only**)

Reject with a specific `NameError` (these mirror what the engine would say, surfaced early):
- `H[0]` / `H[1]` → `"H[0] and H[1] are built-in (1 and jω) — not user-definable"`
- `H[w]` with non-integer / missing index → `"H[w] requires an integer weight ≥ 2"`
- malformed brackets / unknown head (`F[…]`, `C[…]`, `In[…]`, random text) → `"Not a valid SDD equation name (use I[p], I[p,w], Q[p], or H[w])"`

Keep it a **name-level** check only — don't try to validate the expression here (the expression is committed
separately via `CommitExpression`, and `_v`-referencing equations aren't scalar-evaluable in this scope). On
any rejection set `NameError` and **do not** execute the rename command (same control flow as the existing
empty/duplicate guards). On success, clear `NameError` and proceed exactly as now.

> Scope guard: this validation runs **only for SDD/FetSdd owners**. Every other extensible type (P1Tone,
> ToneSource, ZPort, VAR, MEAS, NonlinearC) keeps today's empty+duplicate-only behavior untouched.

## 2. Light discoverability (small, no new dialog)

Two cheap touches so a user can find the feature without a manual:
- **Watermark/hint on the SDD name field** when editable and SDD-owned: e.g. `I[p,w] · Q[p] · H[w]`
  placeholder text (the view already binds `StagedName`; add a `NameWatermark` string on the row VM —
  SDD-specific, empty for other types — and bind it as the TextBox `Watermark`). Keeps the affordance visible
  without a tutorial.
- **A small header note** on the editor for SDD targets (optional, only if the editor header already has a
  spot for per-type hints — check `ParameterEditorView.axaml`): one line like
  `Equations: I[p,w] weighted by H[w]; H[0]=1, H[1]=jω built in.` If there's no clean spot, skip this and keep
  just the watermark — don't add chrome.

## 3. Tests (`tests/Ui.Tests`)

Extend `ParameterEditorAddParamTests` or add `SddEquationNameValidationTests`:
- `CommitName` on an SDD row accepts `I[1]`, `I[1,0]`, `I[2,1]`, `I[1,2]`, `Q[1]`, `H[2]`, `H[7]` → `NameError`
  empty, rename executed.
- Rejects `H[0]`, `H[1]` → `NameError` set (built-in message), no rename.
- Rejects `H[x]`, `H[]`, `I[0]` (p must be ≥1), `I[1,`, `Foo`, `F[1]` → `NameError` set, no rename.
- **Non-SDD unaffected:** committing an unusual-but-nonempty name on a P1Tone/VAR row still behaves as today
  (no new grammar rejection) — pin this so the scope guard doesn't regress other types.
- Duplicate + empty checks still fire for SDD (existing behavior preserved).

Use the existing test seams (`EditableParameter`, `ParameterRowViewModel` ctor with `ownerSymbol`,
`ownerComp`). If `CommitName`'s side effect (execute rename) needs a `SchematicViewModel`, mirror however the
current row tests construct one; if validation can be factored into a pure `static bool TryValidateSddName(
string, out string error)`, do that and unit-test it directly (cleaner, and reusable if Option B lands later).

## Gate
Build 0W/0E; tests green. After this, a user can author `I[1,2]=Q(_v1)` + `H[2]=j*2*pi*freq` on an SDD
entirely in the editor, with malformed names caught inline. Note for a possible **Option B** follow-on (not
this brief): a dedicated SDD equation/weighting editor (grouped per-port `I[p,w]` + an `H[w]` list with the
built-ins shown read-only), modeled on the NonlinearC CV dialog — defer until the feature has real use.
