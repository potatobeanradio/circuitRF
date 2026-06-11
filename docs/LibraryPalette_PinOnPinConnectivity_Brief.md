# circuitRF — Pin-on-pin connectivity detection (+ clean-rebuild for border) (Claude Code / Sonnet)

**The real bug, finally pinned down.** The user's test is **pin-on-pin**: two component pins placed directly on
top of each other, **no wire**. They expect both pins to show **connected** + a **junction dot** where they
touch. **Root cause (confirmed in code):** `BuildRenderModel.IsConnected(wx,wy)` checks **only wires** —
`WirePointHash` is built solely from wire vertices, the fallback scans only wire segments — so a pin coincident
with another **pin** (no wire) returns `false`: unconnected, no dot. The prior pin-on-**wire** drag-follow work
is unrelated to this case (different scenario), which is why pin-on-pin testing showed no change. This is a
**connectivity-DETECTION** fix. Read `docs/design/placement-connectivity-and-drag-follow.md` (rev 2) first.
**Instrument-first with a headless oracle test** (three "fixed but unchanged" rounds — prove it with a test,
not a claim). Firewall green.

> Read first: `docs/design/placement-connectivity-and-drag-follow.md` (rev 2 — the detection gap). Context
> code, all in `src/Ui/Schematic/EditableSchematic.cs`:
> - `BuildRenderModel` → local `IsConnected(wx,wy)` (checks `cg.WirePointHash` + wire-segment fallback — **does
>   NOT include component ports**). This is the gap.
> - `ComputeConnectivityGeometry` — builds `wirePointHash` (wire vertices only), `conPointCounts` (this one
>   ALREADY counts component ports via `AddConPoint`), the auto-dot pass (`autoDotKeys`/`autoDotPts`, gated on
>   wire vertices + `IncidentAt`), `IsCrossingAtDot`.
> - `AssembleConnectionDots` — emits user crossing-dots + `autoDotPts`.
> - `SchematicGeometry.CoincidentPoints` / `PointOnSegmentInterior`, `ConnectTolerance` (0.5), `QuantKey`.
> Note: `conPointCounts` ALREADY includes component ports — so the *counting* substrate exists; the gap is
> that `IsConnected` and the *dot* pass don't use port participation.

## The spine
- **Component ports participate in the ONE connectivity source.** Make the same pass that unions wire vertices
  also recognize component-port coincidences, so `IsConnected` AND the dot set both see pin-on-pin and
  pin-on-wire. Reuse `QuantKey`/`CoincidentPoints`/`PointOnSegmentInterior` + `ConnectTolerance` — never a new
  predicate.
- **A port is connected when it coincides with ANY other connection endpoint** — another component port, a
  wire vertex, or a wire body — not only a wire. (Today: only wire.)
- **A junction dot appears where 2+ distinct connection endpoints coincide and form a real junction**,
  including pin-on-pin (two ports) and pin-on-wire (port + wire vertex/body). ADD these cases; keep the
  existing wire-only auto-dot + 4-way-crossing/user-dot rules intact.
- **Don't regress** wire connectivity, existing auto-dots, crossing/user-dot invariant, or net extraction
  agreement.
- **Scope fence:** connectivity DETECTION (IsConnected + dot emission) for component-port coincidences. NOT
  drag-follow, NOT auto-wiring, NOT component coupling, NOT extraction code.

## Part A (small, do first) — border: clean rebuild + verify
The tile border was switched to a real `SolidColorBrush` (`CrfTileBorderBrush` `#55808080`, in `App.axaml`)
and set as DIRECT attributes on the `Border` (not a style selector). If it still doesn't show after a normal
build, a stale compiled-XAML cache may be the cause.
1. `dotnet clean` then `dotnet build` (clear `obj/`/`bin/` compiled-XAML) and re-run.
2. If the border now shows → done (it was a stale-XAML cache). If it STILL doesn't show, report that — we'll
   instrument the visual tree (the brush resolves and is a real Brush, so a non-showing border then points at
   layout/render, not the brush).
**Part A gate:** report whether a clean rebuild makes the tile border visible. (Quick — just clean+build+look.)

## Part B — pin-on-pin (and pin-on-wire) detection

### LAYER B1 — INSTRUMENT with a headless oracle test (prove the gap + the fix target)
Add a headless test (no Avalonia) that builds a `SchematicEditModel` with **two components whose ports are
placed at the same world point** (pin-on-pin, no wire), calls `BuildRenderModel()`, and asserts on the result:
1. **Document current (buggy) behavior:** assert that TODAY both coincident ports report
   `PortConnectionState.Unconnected` and `ConnectionDots` has no dot at the touch point — capturing the bug.
2. Add a second case: a component **port on a wire vertex** (pin-on-wire) — confirm it already reports
   Connected (the working path) as a control.
**Report the test output — no fix yet.** This is the permanent oracle B2 must flip to green.

**Layer B1 gate:** the test compiles + runs headless and demonstrates: pin-on-pin → both ports Unconnected +
no dot (bug); pin-on-wire-vertex → Connected (control). Report the assertions.

### LAYER B2 — implement: ports participate in connectivity + dots
1. **`IsConnected` (in `BuildRenderModel`):** a port at (wx,wy) is connected if its P-cell coincides with
   **another component port** OR a wire vertex OR a wire body. Reuse the connectivity geometry: e.g. build a
   port-position multiset (P-cell → count, EXCLUDING the port being tested so a port isn't "connected to
   itself") and return true when another port shares the cell; keep the existing wire checks. (NB
   `conPointCounts` already aggregates ports + wire vertices — but it counts the tested port itself, so use it
   carefully: "connected" = the cell's total endpoint count ≥ 2, i.e. something OTHER than this single port is
   there. Verify this reasoning against the dedup in `ComputeConnectivityGeometry`.)
2. **Junction dot for pin coincidences:** in the dot pass, emit a visible junction dot where **2+ distinct
   connection endpoints** coincide at a P-cell and it's a real junction — including two component ports
   (pin-on-pin) and a port + wire vertex/body (pin-on-wire). Keep `autoDotPts` (wire 3-way) and the
   crossing/user-dot rules; ADD the port-coincidence dot. Ensure no double-dots where a wire-vertex auto-dot
   already covers the point.
3. **Keep it O(N)** (hash by P-cell; no O(N²)).
4. **Flip B1 green:** pin-on-pin now → both ports Connected + exactly one dot at the touch point; the
   pin-on-wire control stays Connected. Add an assertion that a LONE port (nothing coincident) stays
   Unconnected with no dot (guard against over-connecting).

**Layer B2 gate:** B1 test green (pin-on-pin Connected + one dot; pin-on-wire Connected; lone port
Unconnected/no dot); in-app, dropping a component pin onto another component's pin shows both pins connected +
a dot, and onto a wire shows connected; existing wire connectivity / auto-dots / crossing-dots unregressed;
`dotnet build`/`dotnet test` green. Report (incl. screenshot description of pin-on-pin + dot).

## Acceptance
1. Pin-on-pin: coincident component ports show Connected + a junction dot (in-app + headless oracle test).
2. Pin-on-wire detection still works; lone ports stay unconnected (no over-connecting); auto-dots, crossing
   dots, user-dot invariant, and wire connectivity all unregressed.
3. Border: clean-rebuild result reported (visible, or escalate).
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **One connectivity source** — extend `ComputeConnectivityGeometry`/`IsConnected`/the dot pass; reuse
  `QuantKey`/`CoincidentPoints`/`PointOnSegmentInterior`/`ConnectTolerance`. No parallel predicate.
- **Don't over-connect** — a lone port, or two ports NOT coincident, must stay unconnected (the "exclude self"
  detail matters — assert it).
- **No double dots** — a point already covered by a wire auto-dot must not get a second dot.
- **Scope fence:** detection + dot emission for port coincidences only. NOT drag-follow, auto-wiring, coupling,
  or extraction.
- **Instrument-first** with the headless oracle test; it stays as a permanent regression test.
- Update `placement-connectivity-and-drag-follow.md` (implemented), `grid-and-connectivity.md` if the dot rule
  is restated there, and `src/Ui/CLAUDE.md` (component ports participate in connectivity; pin-on-pin shows a
  dot; the "exclude self" gotcha).

*Exit: two component pins placed on top of each other show connected with a junction dot (and pin-on-wire
likewise), proven by a headless oracle test — the connectivity detection is correct for pin coincidences, not
just wires.*
