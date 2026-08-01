# Example device worker

A complete, working **device worker** — the kind of program circuitRF runs to evaluate a device
model it does not itself implement. It serves one synthetic square-law transistor.

It is here for three reasons:

- a **template** for writing a real worker,
- an **executable definition** of the wire format, and
- so circuitRF's process plumbing is tested against a real process rather than only against
  in-memory streams (`tests/Core.Tests/Devices/External/DeviceWorkerProcessTests.cs`).

It references nothing — not even `CircuitRF.Core`. A real worker is usually a native program that
loads a compiled model and so cannot use circuitRF's own frame codec; neither does this one. That
makes the tests an agreement between two independent implementations rather than one implementation
agreeing with itself.

## Why a worker is a separate process

Not a preference — two properties of the arrangement:

- A compiled device model calls back into the process that loaded it for services that process must
  export as C symbols. A managed host cannot do that.
- One process can hold exactly one build of one model library. Several builds means several
  processes.

Both dissolve once the model lives in its own process. circuitRF then loads nothing, links against
nothing, and talks over a pipe.

## The protocol

```
[ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of float64 ]
```

Little-endian throughout; `binLen` is a **byte** count. Control is JSON so a frame stays readable in
a hex dump. Bulk numbers ride as raw doubles so a large batch costs no parsing.

Commands: `describe`, `create`, `probe`, `eval`, `destroy`, `shutdown`. Each gets exactly one reply.
A refusal is `{"ok":false,"error":"…"}` — an ordinary reply, not a dropped connection.

`eval` sends `count × nodes` doubles and returns `status[count]` followed, per point, by
`I[n]`, `Q[n]`, `G[n×n]`, `C[n×n]`, row-major, with `G[i][j] = ∂I[i]/∂V[j]`.

**Current is positive flowing INTO the device** at every node. circuitRF stamps this directly and
applies no sign flip, so a worker that reports the other convention will converge to an answer that
is confidently inverted.

### Three things that will bite

- **Flush after every reply.** Otherwise the host waits for a reply sitting in your output buffer
  and the two deadlock. This is the most common way a working worker appears to hang.
- **Loop short reads.** A partial read is normal on a pipe. Treating one as end-of-stream produces
  frames that decode as nonsense only under load.
- **Batch.** Measured against a real worker, one evaluation per round trip costs ~100 µs against
  ~4 µs at batch 2000. Harmonic balance evaluates every device once per sample per Newton
  iteration, so a per-call worker makes the transport, not the model, the simulator.

## Making a kit that uses it

circuitRF finds a worker through a `device-provider.json` beside the kit — the one fact it cannot
derive. Importing the kit copies that file into the workspace and points resolution at it, so the
user's whole sequence is *import kit → place part → configure analysis → Run*.

The example manifest here declares a per-platform command:

```json
{
  "provider": "ExampleKit",
  "workers": [
    { "platform": "win", "command": "DeviceWorkerExample.exe" },
    { "platform": "any", "command": "DeviceWorkerExample" }
  ]
}
```

- **Most specific wins**: an exact runtime identifier (`linux-x64`) beats an operating system
  (`linux`), which beats `any`. A model built for one platform and run through a helper on another
  is just a different `command` — there is no separate mechanism for it.
- **Relative paths resolve against the manifest's folder**, then against `baseDirectory` if it
  declares one. Import writes `baseDirectory` pointing back at the kit, because the worker and model
  files stay there while the manifest is copied into the workspace.
- **The copy is named for the kit.** Installed cells record `Provider = <kit name>`, so that is what
  a netlist asks for.

A kit with no manifest imports silently — its parts still place, draw and export. Only simulating
them needs one.

## Running it by hand

```
dotnet run --project tools/DeviceWorkerExample
```

It then expects protocol frames on standard input, so this is mostly useful under a debugger or a
driver script. The tests are the practical way to exercise it.
