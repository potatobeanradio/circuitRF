#!/usr/bin/env python3
"""How far circuitRF's `cni` layer gets with a kit's device cells.

Reports, per device: does it IMPORT, does it declare its parameters, does it GENERATE geometry — and
when it does not, exactly which part of the API was missing. That last column is the point: it turns
"the shim is incomplete" into a ranked list of what to build next.

    python3 tools/pcell-python/vendor_conformance.py <path-to-kit-pcell-library>

Nothing is installed and nothing is written. The kit is read where it lies.
"""

from __future__ import annotations

import collections
import importlib.util
import inspect
import sys
import traceback
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import circuitrf_pcell as crf          # noqa: E402
from cni.dlo import DloGen, Tech, UnsupportedByCircuitRF   # noqa: E402
from cni.hostglue import MAX_HOST_MODULES, install_host_module   # noqa: E402
from cni.tech import load_tech          # noqa: E402

DBU_PER_MICRON = 1000


def _load_module(path: Path, package_root: Path):
    """Import one device file with its own package importable, so its relative imports resolve."""
    root_parent = str(package_root.parent)
    if root_parent not in sys.path:
        sys.path.insert(0, root_parent)
    rel = path.relative_to(package_root.parent).with_suffix("")
    name = ".".join(rel.parts)
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"no loader for {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module

    # A kit's modules import their host; stand in for whatever they name, learned from the failure
    # rather than from a list here — see cni.hostglue.
    for _ in range(MAX_HOST_MODULES):
        try:
            spec.loader.exec_module(module)
            return module
        except ModuleNotFoundError as exc:
            missing = exc.name or ""
            if not missing or missing.startswith(name.split(".")[0]):
                raise
            if not install_host_module(missing):
                raise
    raise ImportError(f"'{name}' still could not be imported after standing in for its host modules.")


def _cells(module) -> list[type]:
    return [obj for _, obj in inspect.getmembers(module, inspect.isclass)
            if issubclass(obj, DloGen) and obj is not DloGen
            and obj.__module__ == module.__name__]


def _reason(exc: BaseException) -> str:
    if isinstance(exc, UnsupportedByCircuitRF):
        return f"unsupported: {exc}".split(".")[0]
    return f"{type(exc).__name__}: {exc}"[:110]


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2

    # The kit's package initialiser is registration glue that runs on import and expects its host
    # module to exist. circuitRF does its own registering, so an inert stand-in is enough to reach the
    # device modules underneath it.
    # Host stand-ins are installed lazily, named by the kit's own failing imports rather than by this
    # file — see cni.hostglue.

    lib = Path(argv[1]).resolve()
    if not lib.is_dir():
        print(f"not a folder: {lib}")
        return 2

    tech_json = next(iter(sorted(lib.glob("*tech*.json"))), None)
    technology = load_tech(tech_json) if tech_json else Tech()
    print(f"kit           {lib}")
    print(f"process data  {tech_json.name if tech_json else '(none found)'} "
          f"— {len(technology.getTechParams())} parameters, grid {technology.getGridResolution()}")

    # A technology whose layer table simply accepts whatever the kit names, so a missing LAYER is not
    # miscounted as a missing API. What the shim can DRAW is the question here.
    seen_layers: dict[str, crf.Layer] = {}

    class _AcceptingTechnology(crf.Technology):
        def __init__(self): super().__init__({})
        def layer_named(self, name):
            if name not in seen_layers:
                seen_layers[name] = crf.Layer(len(seen_layers) + 1, 0)
            return seen_layers[name]

    crf_tech = _AcceptingTechnology()

    # A kit may keep its device modules directly in the library folder or in a sub-package of its
    # own. Discovered rather than named: the sub-folder that actually holds "*_code.py" modules wins,
    # so no kit's own directory name is written into circuitRF.
    device_dir = next(
        (d for d in sorted(lib.iterdir())
         if d.is_dir() and not d.name.startswith("_") and any(d.glob("*_code.py"))),
        lib)
    files = sorted(p for p in device_dir.glob("*.py") if not p.name.startswith("__"))

    rows: list[tuple[str, str, str]] = []
    reasons: collections.Counter[str] = collections.Counter()

    for path in files:
        try:
            module = _load_module(path, lib)
        except Exception as exc:                      # noqa: BLE001
            rows.append((path.stem, "import failed", _reason(exc)))
            reasons[_reason(exc)] += 1
            continue

        cells = _cells(module)
        if not cells:
            continue                                   # a helper module, not a device

        for cell in cells:
            try:
                specs = cell.declared_parameters(technology)
            except Exception as exc:                  # noqa: BLE001
                rows.append((cell.__name__, "no parameters", _reason(exc)))
                reasons[_reason(exc)] += 1
                continue

            params = {s["name"]: s["default"] for s in specs}
            try:
                result = cell.generate(technology, params, crf_tech, DBU_PER_MICRON)
            except Exception as exc:                  # noqa: BLE001
                rows.append((cell.__name__, f"{len(specs)} params, no geometry", _reason(exc)))
                reasons[_reason(exc)] += 1
                continue

            rows.append((cell.__name__,
                         f"{len(specs)} params, {len(result.shapes)} shapes, {len(result.pins)} pins",
                         ""))

    generated = sum(1 for _, _, why in rows if not why)
    print(f"\n{'device':<26} {'result':<34} why not")
    print("-" * 100)
    for name, status, why in rows:
        print(f"{name:<26} {status:<34} {why}")

    print("-" * 100)
    print(f"{generated} of {len(rows)} cells generated geometry.")
    if reasons:
        print("\nwhat is missing, most blocking first:")
        for why, n in reasons.most_common():
            print(f"  {n:3d}  {why}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
