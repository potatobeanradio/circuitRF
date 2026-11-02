# Sonnet Brief — SDD single-index I[p] equations + 4-net SDD2 convention (fixes equation-fragment phantom nodes & wrong output)

The user switched to a real SDD with `I[1]=…` / `I[2]=…` equations and hit three issues. The first two share a
root cause in the **SDD parse path**; the SDD instance is being mis-parsed end to end, which is why the output is
wrong. The third (n1/n2/n3 in the picker) is the still-unimplemented user-node filter — needs a scope decision.

User's SDD line:
```
SDD:X1  Vin  Vout  0  I[1]=_v1/50  I[2]=(B*TC*tanh(... _v1 ... _v2 ...)...)/2
```

## Root cause (confirmed across reader, elaborator, factory)
The SDD equation syntax is recognized **only** in the two-index form `I[p,w]` (w=0 current, w=1 charge):
- **CnlReader** `SddAssignmentHeader = (I|Q|F|In|Nc)\[\d+,\d+\]…` — requires **two** indices. With single-index
  `I[1]`, `FindFirstSddEquation` returns −1, so **the entire tail after the instance name (equations included)
  is split on whitespace and treated as net names.** That dumps `I[1]=_v1/50` and whitespace-separated fragments
  of the big `I[2]` expression into the **net list** → they surface as phantom nodes in the combo (issue #1), and
  the real net list is garbage (issue #2).
- **Factory** `RxCurrentEq = ^I\[(\d+),(\d+)\]$` — also requires two indices; a single-index `I[1]` would be
  silently skipped (`if (!m.Success) continue;`) → no current equation bound → zero device current → wrong/zero
  output even if the nets were right.
- **Elaborator** `RxSddEquation = ^[IFCi][^\[]*\[` already accepts single-index (it only checks the prefix), so
  it's the reader + factory that must change.

## Net/port convention (confirms the user's instinct)
`Elaborator.ResolveSddParameters`: `int portCount = inst.NetBindings.Count / 2;` with the comment **"Port count =
half the net count (2N nets in +/− pairs)."** So an **SDD2 needs exactly 4 nets**: `port1+ port1− port2+ port2−`.
The user is right: their line should be `SDD:X1  Vin 0  Vout 0  …` (port1 = Vin→gnd, port2 = Vout→gnd), not
`Vin Vout 0`. With 3 nets, `portCount` truncates to 1 → only `I[1]` would ever bind, and port 2 (the drain) is
silently dropped. (This is masked today because the equations don't parse at all, but it must be fixed too.)

## Fix 1 — accept single-index `I[p]` as `I[p,0]` (current); `Q[p]` as `Q[p,1]` (charge)
Define the mapping: a **single-index** equation `I[p]` means `I[p,0]` (port-p current, weight 0). `Q[p]` means
the charge equation (equivalent to `I[p,1]`). Two-index forms keep working unchanged.

Apply in BOTH places:
1. **CnlReader `SddAssignmentHeader`** — extend the regex to match one OR two indices:
   `(I|Q|F|In|Nc)\[\d+(,\d+)?\]\s*(=)|(C(?:port)?)\[\d+\]\s*(=)`. The boundary scanner
   (`FindFirstSddEquation` / `ParseSddEquations`) then correctly finds `I[1]=`/`I[2]=` and splits nets from
   equations. The captured `assignName` will be `"I[1]"` / `"I[2]"` (single-index) — pass it through as-is.
2. **Factory `CreateSddModel`** — add a single-index current/charge regex and normalize:
   - `RxCurrentEq1 = ^I\[(\d+)\]$` → `(p, w=0)`.
   - `RxChargeEq1  = ^Q\[(\d+)\]$` → `(p, w=1)`. (Also accept two-index `Q[p,1]` if it already does — check.)
   - Keep the existing two-index `RxCurrentEq` for `I[p,0]`/`I[p,1]`.
   - After extracting `(p, w)`, the existing range/weight checks and `currentAst`/`chargeAst` assignment apply
     unchanged. **Note** `I[p]` (w=0) → `currentAst[p-1]`; `Q[p]` → `chargeAst[p-1]`.
   - The factory currently routes `I[p,1]` (w==1) to charge — preserve that. Single-index `I[p]` is **always**
     w=0 (current); charge uses `Q[p]`.

## Fix 2 — port count + net arity validation (don't silently truncate)
`portCount = NetBindings.Count / 2` silently truncates an odd net count. Add validation so the user gets a clear
error instead of wrong physics:
- In `ResolveSddParameters` (or at elaboration), if `NetBindings.Count` is **odd**, throw/warn:
  `"SDD '<inst>': expected an even number of nets (2 per port: +,−); got <n>. An SDD<k> needs <2k> nets."`
- Cross-check against the highest port index referenced in the equations: if equations reference `I[2]` but only
  1 port's worth of nets is present, error:
  `"SDD '<inst>': equation references port 2 but only <portCount> port(s) of nets were given (need 4 nets for a
  2-port: p1+ p1− p2+ p2−)."`
- This turns the user's current silent-wrong-output into an actionable message, and confirms `Vin 0 Vout 0`
  (4 nets) once they fix the line.

**Tell the user explicitly** (in the gate notes): their SDD line must be `SDD:X1  Vin 0  Vout 0  I[1]=…  I[2]=…`
— 4 nets (each port referenced to ground here). With the parser fix AND the 4-net line, `_v1 = V(Vin)−V(0)` and
`_v2 = V(Vout)−V(0)`, the drain current `I[2]` drives Vout, and the fundamental will be non-zero.

## Fix 3 — node picker: show only user-named nodes (still needs the scope decision)
n1/n2/n3 still appear. After Fix 1+2, the equation fragments stop polluting the node axis, so the *phantom*
entries vanish — but n1/n2/n3 are **real user nets** the user nonetheless wants hidden. So "hide non-user nodes"
is NOT the rule that removes them; they ARE user nodes.

**This needs the user's intent before coding** (ASK, don't guess): the earlier reply leaned toward "only nodes I
explicitly named," but n1/n2/n3 *were* named. Likely they actually want one of:
  (a) hide auto-`n#`-pattern connection nodes (anything matching `^n\d+$`), showing only "meaningful" names like
      Vin/Vout/Vout2; or
  (b) a user-toggle "show internal nodes"; or
  (c) only nodes that carry a probe/output marker.
Recommend (a) as the default (matches "n1/n2/n3 are noise, Vin/Vout/Vout2 are signals") with a toggle to reveal
all — but **confirm with the user**. Do NOT implement the filter in this brief; this brief is the SDD parse fix.
(If you want a placeholder: add `bool ShowAllNodes` defaulting false that, when false, hides `^n\d+$` node labels
from the axis-role node combo only — purely a display filter over `PinOptions`, not a cube change.)

## Tests (`tests/Core.Tests` + `tests/Engine.Tests`)
1. **Sdd_SingleIndex_I_p_Parses:** parse `SDD:X1 Vin 0 Vout 0 I[1]=_v1/50 I[2]=_v2/100` → nets are exactly
   `["Vin","0","Vout","0"]`; overrides contain `I[1]` and `I[2]`; NO net contains `I[`, `_v`, `=`, or expression
   fragments.
2. **Sdd_SingleIndex_BindsCurrent:** elaborate + build the SDD model → `currentAst[0]` and `currentAst[1]` are
   non-null (both port currents bound); `chargeAst` all null.
3. **Sdd_Qp_BindsCharge:** `Q[1]=…` → `chargeAst[0]` non-null, `currentAst[0]` null.
4. **Sdd_TwoIndex_StillWorks:** `I[1,0]=…ai I[2,1]=…` → current[0], charge[1] (regression).
5. **Sdd_OddNets_Errors:** `SDD:X1 Vin Vout 0 I[1]=… I[2]=…` (3 nets) → clear error naming the arity, not silent
   truncation.
6. **Sdd_PortRefBeyondNets_Errors:** equations reference port 2 but only 2 nets → clear error.
7. **Hb_RealSdd_Vout_NonZero (Engine):** the user's full netlist with the 4-net SDD line and single-index
   equations → HB sweep → Vout (`V[:, <Vout>, 1]`) fundamental is non-zero and Pin-dependent; node axis has no
   `I[`/`_v` fragments.

## Gate
Build 0W/0E; tests green. Manual: with the SDD line corrected to `SDD:X1 Vin 0 Vout 0 I[1]=… I[2]=…`, re-run the
HB sweep → node combobox shows only real nodes (no `I[1]`, no `_v` fragments); Vout and Vout2 fundamentals are
non-zero. The 3-net form now errors clearly instead of producing zeros.

## On completion
Note in `src/Core/.../CLAUDE.md`: SDD equations accept single-index `I[p]` (≡ `I[p,0]`, current) and `Q[p]`
(charge) in addition to the two-index `I[p,w]` form, in both the CnlReader boundary scanner and the factory
binder. SDD nets are validated as 2 per port (±); an odd net count or a port-index/net mismatch now errors
clearly instead of silently truncating `portCount = nets/2`. (Node-picker user-name filter deferred pending the
user's scope decision.)
