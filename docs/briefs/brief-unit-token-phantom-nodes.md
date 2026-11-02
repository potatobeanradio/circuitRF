# Sonnet Brief — Fix unit tokens leaking as nodes (phantom "dBm"/"V"); Vout2 zeros; node picker shows only user nodes

Three trace-card/HB issues from the user's HB sweep. The first two share **one root cause** in the CNL parser;
fixing it very likely fixes the third symptom too. The node-picker filter is a small separate change.

User's netlist (single-tone HB, Pin sweep) has nodes: `Vin, Vout, n1, n2, n3, Vout2` (+ ground). The trace-card
node combobox shows phantom **"dBm"** and **"V"** entries, and `V[:, 5, 1]` (Vout2 fundamental) renders all
zeros.

## Root cause (confirmed) — unit tokens leak into `nets`
`P1Tone` and `Vdc` lines are **not** dispatched to a dedicated parser; they use the generic `ParseInstanceLine`.
There, a token is classified as a **net** (no `=`) or a **param** (`key=val`). A trailing unit token is only
consumed when `Units.IsKnown(nextToken)` is true. But `Units` is a **linear-scale table** that deliberately
**excludes** `V`, `A`, `W`, `dBm`, `dB` (see the comments in `Units.cs` and `UnitNormalizer.cs`:
*"dBm — measurement function, not a linear scale suffix"*, *"V, A, W … are identity at this scale layer"*).

So for the user's lines:
- `P1Tone:P1 n1 0  Pavl=Pin dBm …` → `dBm` is not in `Units`, so it's **not** consumed → falls to the net branch
  → phantom **"dBm" node**.
- `Vdc:V1 n2 0  Vdc=-3.05 V` and `Vdc:V2 n3 0  Vdc=48 V` → `V` is not in `Units` → phantom **"V" node** (shared).

These phantom nets get added to the node map, **shifting every subsequent node index**. The picker offers them
as nodes (#2), and the index shift is almost certainly why `V[:, 5, 1]` (Vout2) reads zeros (#3) — index 5 no
longer points at Vout2 once "dBm"/"V" pollute the map.

## Fix 1 — the parser must recognize ALL unit tokens as consumable, not just linear-scale ones
The unit-consume decision in `ParseInstanceLine` (and anywhere else the generic path peeks for a trailing unit)
must use a predicate that includes **identity/measurement units** (`V`, `A`, `W`, `dBm`, `dB`, `kV`, `nV`, `µV`,
`nA`, `µA`, `µW`, `nm`, `cm`, `%`, …), not just `Units.IsKnown`.

- Add `Units.IsRecognizedUnit(string)` (or a `CnlReader`-local set) = `IsKnown(u)` **OR** the token is a known
  **dimensionless/identity/measurement** unit. Seed the extra set from the units `UnitNormalizer` documents as
  "table-uncovered but valid": `V, A, W, kV, nV, uV, nA, uA, mA, mV, uW, mW, W, dB, dBm, dBc, nm, cm, %`,
  plus the glyph forms normalized first (`Ω`/`µ` via `UnitNormalizer.ToEngineUnit`). Keep it conservative — a
  fixed allow-list of real units, NOT "any alpha token" (we must not swallow a genuinely-named net like `Vout`).
- Replace the two `Units.IsKnown(tokens[i + 1])` / glued-unit checks in `ParseInstanceLine`'s param branch with
  the new `IsRecognizedUnit`. The consumed identity unit is recorded on the `ParameterAssignment` (or dropped —
  it has no scale); what matters is it **no longer becomes a net**.
- **Glued form too:** `TrySplitGluedUnit` is guarded by `Units.IsKnown`; extend it (or its guard) so
  `Pavl=Pin` stays intact (Pin is an identifier — no split) but a glued `48V`/`-3.05V`/`0dBm` splits value+unit.
  Be careful: `Vs_mag` and bare identifiers must NOT split. The existing regex `^([+-]?num)([A-Za-z]+)$` already
  requires a numeric head, so `Pin`/`Vs_mag` won't match — only extend the **unit allow-list** the guard checks.

**Critical disambiguation — don't eat real nets.** A net token like `Vout`, `Vin`, `V1` must never be treated as
a unit. Safeguards:
- Only consider a token a trailing unit when it **immediately follows a `key=value` param token** (the existing
  structure already does this — units are consumed inside the `tok.Contains('=')` branch, peeking at `i+1`). A
  bare net in the leading net section is never in that position. So `R:R2 Vout2 0 R=80 Ohm` → `Vout2` is a net
  (before any `=`), `Ohm` is consumed after `R=80`. Good — keep the consume logic strictly in the param branch.
- The single-letter `V` is the risky one (could be a net name). But as a **post-`key=value` trailing token** it's
  unambiguously a unit (a net never appears there). So gating on position fully resolves it.

After this fix, the user's nets are exactly `Vin, Vout, n1, n2, n3, Vout2` — no "dBm"/"V" — and the node indices
are correct, so `V[:, 5, 1]` resolves to the real Vout2.

## Fix 2 — verify Vout2 is non-zero after Fix 1 (don't assume)
Once the node map is clean, re-check Vout2's fundamental. Vout2 is a **linear-only** node (R2 to ground, C2 to
Vout) recovered by the back-solve (`HbLinearBackSolver.GetNodeVoltage`). The `Vfull` assembly in `HbEngine.Run`
already back-solves non-interface nodes. Two possibilities:
1. **Fix 1 fully resolves it** (index was wrong) → Vout2 fundamental is now the C2-coupled voltage. Most likely.
2. If Vout2 is **still zero** after Fix 1, investigate the back-solve: confirm `GetNodeVoltage(6, 1, 0)` returns
   the AC voltage at the node behind series cap C2. At the fundamental, C2 (1 mF) is a near-short at 2 GHz, so
   Vout2 ≈ Vout at the fundamental — definitely non-zero. If it's zero, the back-solve's full-MNA solve may not
   include the linear-only subnetwork (R2/C2 branch) correctly. Add a test (below) and, only if needed, a
   follow-up; do **not** pre-emptively rework the back-solver — verify first.

Add a console/debug check or a unit test that prints Vout2's fundamental from the cube after Fix 1 before
concluding.

## Fix 3 — node picker shows only user-named nodes
Separate, additive UX change. The node axis-role combo (and any node list in the trace card) should list only
nodes the **user explicitly named** in the netlist, not engine-minted internals.
- Today the V cube already excludes `__`-prefixed mint nodes (per the linear-nodes brief). After Fix 1, the
  remaining nodes are all user nodes (`Vin, Vout, n1, n2, n3, Vout2`), so the phantom entries vanish for free.
- For the "explicitly named" refinement: a node is user-named if it appeared as a **net token in a user instance
  line** (vs. an auto-generated name). In practice, after Fix 1, every node on the V-cube `node` axis that lacks
  the `__` prefix IS user-named — so the picker can simply continue listing the cube's `node` axis labels, and
  the fix is really "stop polluting the axis with non-nodes" (Fix 1). **Confirm with the user this is sufficient**
  — if they later want to hide truly auto-generated nodes (e.g. an internal node a multi-terminal device mints
  without `__`), we'd need an explicit "user-authored" flag on the node map. For now: rely on Fix 1 + the
  existing `__` filter; no extra picker code needed unless a non-`__` engine node still leaks.
- If a non-`__` engine-minted node does appear for some component (check P1Tone — it may mint an internal source
  node), add the `__` prefix to that mint in the relevant model so the existing filter catches it. Note which, if
  any, you found.

## Tests (`tests/Core.Tests` / `tests/Engine.Tests`)
1. **CnlReader_P1Tone_NoPhantomUnitNets:** parse the user's `P1Tone` line → nets are exactly `["n1","0"]`; no
   `"dBm"` net. `Pavl` override present.
2. **CnlReader_Vdc_NoPhantomUnitNets:** parse `Vdc:V1 n2 0 Vdc=-3.05 V` → nets `["n2","0"]`; no `"V"` net; `Vdc`
   override = −3.05.
3. **CnlReader_DoesNotEatRealNet:** `R:R2 Vout2 0 R=80 Ohm` → nets `["Vout2","0"]` (Vout2 preserved as a net,
   `Ohm` consumed). A hypothetical net literally named `V` in the leading net section is still a net (position
   gate).
4. **Hb_Vout2_NonZeroFundamental:** the user's netlist through HB → the `V` cube `node` axis has no "dBm"/"V";
   `Vout2` is present; its fundamental (harmonic index 1) is non-zero and ≈ Vout's fundamental (C2 near-short at
   2 GHz).
5. **GluedUnit_StillSafe:** `Vs_mag` and `Pin` never split; `48V`/`0dBm` split into value+unit.

## Gate
Build 0W/0E; tests green. Manual: re-run the attached HB sweep → node combobox lists only
`Vin, Vout, n1, n2, n3, Vout2` (no "dBm", no "V"); selecting Vout2 / fundamental renders a non-zero,
Pin-dependent voltage in the Table.

## On completion
Note in `src/Core/.../CLAUDE.md`: the CNL generic instance parser now recognizes identity/measurement unit
tokens (`V`, `A`, `W`, `dBm`, `dB`, …) as consumable trailing units (allow-list, position-gated to post-`=`
tokens) so they no longer leak into the net list; this also fixed the node-index shift that zeroed back-solved
linear nodes. The node picker lists only non-`__` user nodes. If any component minted a non-`__` internal node,
it was prefixed so the existing filter hides it.
