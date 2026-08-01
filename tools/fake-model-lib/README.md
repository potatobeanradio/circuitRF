# fake-model-lib — something for the device worker to load

The repository commits no vendor kit, so without this there is nothing for a test run to load and
every test of the worker would be our own reader agreeing with our own writer. This is the other
side of the contract, written from the ABI rather than shared with it.

```
tools/fake-model-lib/fake_model.c        the fixture
tools/fake-model-lib/crf_test_host.def   the host module it imports FROM, on Windows
tools/fake-model-lib/build.sh            builds fake_model.so / fake_model.dll
```

**It references no project in this repository** — the same treatment `tools/DeviceWorkerExample`
gets, for the same reason. **It is not built by `dotnet build`**: a fixture that fails to compile
must never be able to fail an application build. Run `./build.sh linux` or `./build.sh windows` by
hand, or from a CI step that wants worker coverage.

## What it serves

One family, `CRF_TEST_V1`: a two-terminal 10 mS conductance with one declared parameter. Small on
purpose, and **symmetric in the Jacobian** on purpose — the worker's probe separates a conductive
path from a thermal one by reciprocity rather than magnitude, so an asymmetric fixture would be
classified wrongly and the test would be measuring the fixture.

Driven through the real protocol against the Linux worker it answers, in order:

```
describe  → CRF_TEST_V1, 2 external pins, 0 internal nodes, param "W"
create    → handle 0, probeEval true
probe     → both nodes: not degenerate, conductively coupled, electrical
eval      → I = 0.01 A at 1 V, 0.02 A at 2 V, currents equal and opposite
```

## The Windows half is the part that actually proves something

On Linux this library leaves its host callbacks **undefined** and they resolve against whatever
process loaded it — which is why the Linux worker is linked `-rdynamic`. Nothing needs staging.

On Windows it **imports them by name from `crf_test_host.dll`**, a module that is never built. That
is the whole point: something has to supply a module under that name at load time, and doing so is
the worker launcher's job. `build.sh windows` generates an import library from `crf_test_host.def`
with `dlltool` precisely so the resulting DLL genuinely carries that import descriptor.

So a Windows run exercises the real mechanism end to end: the launcher reads `crf_test_host.dll` out
of **this file's own import table**, stages `crf-model-host.dll` under that name in a per-user
cache, loads it, and only then loads this library — whose import binds to the already-loaded module.

`../senior-worker/verify-windows.sh` drives exactly that, under Wine in a container, and also builds
a second copy of this library importing from a *different* host module name so the "two staged names
coexist" case has two real names to work with.

**The name is this fixture's own business, exactly as a vendor's is.** Nothing in circuitRF knows
it, and nothing may come to know it: the worker derives it by matching *our* ABI symbols against the
import descriptors, never by recognising a name.
