# Sonnet Brief — S-Parameter Engine robustness: Z0-terminated (wave) port formulation + Re(Z0)=0 fallback

**Goal.** Make `SParameterEngine` gracefully solve trivial/degenerate topologies that currently throw — most
importantly **two Terms on the same node pair / a port directly across a short** (e.g. `Term1 n1 0 Num=1`,
`Term2 n1 0 Num=2`, short between → a perfect thru). Today both Terms stamp **ideal 0 V sources**; two ideal
voltage sources in parallel are linearly dependent → exact MNA rank deficiency → "no pivot" `SingularMatrixException`,
after a noisy regularization retry that can't help (the singularity is in the branch-constraint block, not a
floating node). Commercial tools avoid this by modeling an S-param port as a **source behind its Z0** (Norton /
incident-wave), which is non-singular by construction for passive real Z0 and yields S **directly** (no Y→S
inversion, no per-port voltage-source branch).

**Blast radius is contained:** Port/Term branches are consumed **only** by `SParameterEngine`. HB explicitly
skips them (`HbLinearExtractor.BuildMna`: `if (ec.Model is PortModel or TermModel) continue;`) and DC treats them
as inert (`TermScopingTests` Gate 1). So this change touches `SParameterEngine.Run` and (optionally) the port
models — **do not** alter HB/DC/export behavior.

## The formulation (per frequency)
For a port `j` with reference `Z0_j` and `Re(Z0_j) > 0`, model it as a **Norton source behind Z0** using the
power-wave definition (Kurokawa), consistent with `RFNetwork`'s power-wave conventions:

- **Termination stamp (always, for every port):** add a conductance `G_j = 1/Z0_j` directly between the port's
  two nodes via `mna.AddAdmittance(c.Nodes[0], c.Nodes[1], 1.0/Z0_j)`. **No branch unknown.** (This is the only
  change to what a port contributes to the matrix.)
- **Excitation for the driven port `j` (unit incident wave `a_j = 1`, others 0):** inject a current source at the
  port node pair representing the incident wave behind Z0:
  ```
  I_j = 2 · a_j · sqrt(Re(Z0_j)) / Z0_j        // = 2/sqrt(Re Z0) for real Z0
  ```
  via `mna.AddCurrentInjection(c.Nodes[0], +I_j)` and `AddCurrentInjection(c.Nodes[1], -I_j)` (sign per the
  IMnaContext convention: current injected INTO the first node).
- **Solve** the network (sources from the netlist zeroed, exactly as today — independent sources off for S-param).
  Read each port's terminal voltage `V_k = V(Nodes0_k) − V(Nodes1_k)` from the solution.
- **Reflected wave / S column** directly from the wave definition:
  ```
  a_k = (V_k + Z0_k · I_k) / (2·sqrt(Re Z0_k))     // incident
  b_k = (V_k − conj(Z0_k) · I_k) / (2·sqrt(Re Z0_k)) // reflected
  ```
  where `I_k` is the current delivered into port k's termination = `(V_k)·G_k − I_inj_k` … **simpler and
  numerically clean:** because the termination conductance is in the matrix, the reflected wave reduces to
  `b_k = V_k / sqrt(Re Z0_k) − a_k` with `a_k = 1` only for `k == j`, else `a_k = 0`. Concretely:
  ```
  S[k, j] = b_k / a_j = (V_k / sqrt(Re Z0_k)) − (k == j ? 1 : 0)     // real Z0
  ```
  For **complex** Z0 use the full wave formula above (compute `a_k`, `b_k` from `V_k` and the known injected
  `I_k`); `S[k,j] = b_k` since `a_j = 1`. **Verify the exact algebra against a 2-port unit test** (a matched 50 Ω
  thru must give `S = [[0,1],[1,0]]`; a 50 Ω/75 Ω mismatch must match `RFNetwork.YToS` of the same network).

This replaces the current per-port loop (`BuildRhsWithPortDrive` on a port branch row + `yMat[k,j] = -xBuf[branch]`
+ `RFNetwork.YToS`). The S-matrix is assembled column-by-column directly; **drop the Y→S step** in the wave path.

## Two code paths, chosen once up front
Compute `bool allPortsResistive = z0PerPort.All(z => z.Real > 1e-12);` after collecting ports.

- **`allPortsResistive == true` → wave path (above).** This is the common case and fixes the bug. Ports stamp a
  conductance (no branch); excitation via current injection; S read directly. Regularization (gmin/inductance)
  still available but should now be genuinely unnecessary for these trivial circuits — so the "trying
  regularization…" warning stops firing on them (see Messaging below).
- **`allPortsResistive == false` (any port has `Re(Z0) ≤ 1e-12`, e.g. a purely reactive reference) → legacy
  path, unchanged.** Keep today's ideal-0 V-source branch stamping + unit-voltage solve + `RFNetwork.YToS`. A
  reactive-reference port can't be a series-conductance termination, so the existing formulation remains the
  fallback. (This path keeps its current singular-matrix behavior for genuinely ill-posed circuits — that's
  correct; we're only making the resistive-port common case robust.)

**Port model stamping:** the wave path needs the port to contribute a conductance, not a 0 V branch. Cleanest:
have `SParameterEngine` stamp the port termination/excitation itself in the wave path (it already special-cases
Port/Term), and **skip the normal `PortModel/TermModel.Stamp`** for ports in that path (so no branch is created).
Keep `PortModel`/`TermModel.Stamp` (the 0 V-source branch) intact for the legacy fallback path and for
`CollectPortsAndBranchLabels`. Do NOT change DC/HB (they already skip Port/Term). Confirm the port-node lookup:
the engine needs each port's `Nodes[0]`/`Nodes[1]` — `PortEntry` currently stores only `BranchIndex`; **add the
two node indices to `PortEntry`** (read from the `ElaboratedComponent.Nodes`) for the wave path. The legacy path
keeps using `BranchIndex`.

## Messaging cleanup (fold in)
With the wave path, trivial shorts solve on the first factorization, so the regularization retry no longer fires
for them. Additionally: demote the `AddWarningOnce("sparam-regularization", …)` message so it isn't alarming on
normal circuits — keep ONE concise warning only when regularization is actually engaged on the **legacy** path,
and make the wave path silent on success. Do not emit "trying regularization… failed…" chatter. (Per the owner:
VendorA doesn't flood messages for a trivial short.)

## Tests (`tests/Engine.Tests/Linear`)
Use the existing helper pattern (`ds["S"][fi, r, c]`):
1. **TwoTermsSameNode_PerfectThru** (the bug): netlist `Term1 n1 0 Num=1 Z=50`, `Term2 n1 0 Num=2 Z=50`, sweep a
   few freqs → solves with **no** exception and `S ≈ [[0,1],[1,0]]` at every frequency (matched thru). This is
   the headline gate.
2. **PortAcrossShort_Solves**: a port pair bridged by a `Short` (or both ports on one node) → no exception,
   physically correct S.
3. **WaveVsLegacy_Parity**: a normal 2-port (e.g. a series R or an L-C section) solved by the wave path matches
   the previous Y→S result within ~1e-9 (regression: the rewrite didn't move correct answers).
4. **MismatchedZ0_Thru**: 50 Ω port 1, 75 Ω port 2, ideal thru → matches `RFNetwork.YToS` of the same Y (validates
   the complex/again-real wave algebra and per-port Z0).
5. **ReactiveRefFallback**: a port with `Z = j*50` (Re=0) → takes the legacy path and behaves as before (no
   regression, no crash for a well-posed network).
6. Existing `TermScopingTests` (DC inert, buried-Term scoping) and all current S-param tests still pass
   unchanged.

## Gate
Build 0W/0E (TreatWarningsAsErrors); all Engine tests green. The two-Terms-on-one-node netlist from the report
runs end-to-end and plots a clean matched thru, with **no** regularization message flood. Existing S-param
results are unchanged (parity tests).

## On completion
Note in `src/Engine/CLAUDE.md`: S-parameter ports use a **Z0-terminated power-wave (Norton) formulation** when
all port references are resistive (`Re(Z0)>0`) — each port stamps `1/Z0` between its nodes and is excited by an
incident-wave current source, with S read directly (no Y→S inversion, no per-port voltage-source branch). This
removes the parallel-port / port-across-short singularity class. Ports with `Re(Z0)≤0` (reactive reference) fall
back to the legacy ideal-source + Y→S path. HB/DC are unaffected (they already treat Port/Term as inert).
Regularization is now a genuine last resort and no longer warns on trivial circuits.
