"""Turn a vendor kit's parametric cells into circuitRF generators.

This is the join. Everything else in ``cni`` exists so a kit's own cell code can RUN; this module is
what makes those cells reachable from circuitRF — one registered generator per device, discovered
from the kit itself rather than listed by hand anywhere.

Usage, from a kit's own additions folder (see ``docs/design/pcell-vendor-bridge.md``)::

    import circuitrf_pcell as crf
    from cni.bridge import register_kit

    register_kit("mykit_cells.devices")
    crf.run()

**Discovered, never declared.** The list of cells comes from walking the kit's own package — so a kit
that gains a device gains a generator with nothing to update. This is the same reason ``describe`` is
the only source of a script's generator list (schema §4.1): a second, written-down copy is a cache
with no invalidation, and the failure is a palette offering a cell that is not there.
"""

from __future__ import annotations

import importlib
import inspect
import pkgutil
from typing import Any, Iterable

import circuitrf_pcell as crf

from .dlo import DloGen, Tech
from .hostglue import install_host_module_for

__all__ = ["register_kit", "KitRegistration"]


class KitRegistration:
    """What :func:`register_kit` found, and what it could not read."""

    def __init__(self, ids: list[str], problems: list[str]):
        self.ids = ids
        self.problems = problems

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"KitRegistration({len(self.ids)} cells, {len(self.problems)} problems)"


def _cells_in(module) -> list[type]:
    """The parametric cells a module DEFINES — not the ones it merely imported.

    A kit's device modules import their own base classes from each other, so matching every
    ``DloGen`` subclass in scope would register the shared bases once per device that imports them.
    """
    return [
        obj for _, obj in inspect.getmembers(module, inspect.isclass)
        if issubclass(obj, DloGen) and obj is not DloGen and obj.__module__ == module.__name__
        # A class that never overrides genLayout draws nothing — it is a shared base its devices
        # subclass, not a device. Detected STRUCTURALLY rather than by name: a "*_base"/"*Base"
        # convention is one supplier's habit and would not survive the next kit.
        and getattr(obj, "genLayout", None) is not getattr(DloGen, "genLayout", None)
    ]


def _declare(cls: type, kit_tech: Any) -> tuple[list[crf.Parameter], dict[str, Any]]:
    """One cell's parameters, and their defaults, IN THE KIT'S OWN TYPES.

    **A parameter crosses as whatever the kit says it is — a count stays a count, a string stays a
    string — and that is a correctness requirement, not a nicety.** A kit states some defaults in its
    own engineering notation (``'1u'``, ``'0.13u'``) and parses them with its own reader; it states
    others as plain numbers and does ARITHMETIC on them (``if vn_columns > 1``). Flattening everything
    to text breaks the second kind at the comparison, far from the declaration — measured: ten of the
    kit's cells failed on ``'>' not supported between instances of 'str' and 'int'``.

    **Nothing is converted, in either direction.** Declaring a width as a LENGTH would make circuitRF
    convert SI metres to database units and hand the cell a number its own parser never expected —
    circuitRF silently translating somebody else's parameter language. Preserving the type means
    circuitRF HOSTS that language rather than reinterpreting it.

    The cost is stated rather than hidden: a kit parameter gets no unit dropdown and no dimensional
    checking in circuitRF's own editor, because circuitRF genuinely does not know what it is. A kit
    that wants those can declare them beside itself later — the same shape ``PinInferenceRules``
    already uses — without anything here changing.
    """
    specs = cls.declared_parameters(kit_tech)
    params: list[crf.Parameter] = []
    defaults: dict[str, Any] = {}

    for spec in specs:
        name = str(spec.get("name", ""))
        if not name:
            continue
        default = spec.get("default")
        defaults[name] = default

        # bool BEFORE int: in Python bool subclasses int, so the other order sends every flag as the
        # integer 1 — a bug that only surfaces where something compares against True.
        if isinstance(default, bool):
            params.append(crf.Parameter.flag(name, default))
        elif isinstance(default, int):
            params.append(crf.Parameter.integer(name, default))
        elif isinstance(default, float):
            params.append(crf.Parameter.real(name, default))
        elif default is None:
            params.append(crf.Parameter.text(name))
        else:
            params.append(crf.Parameter.text(name, str(default)))

    return params, defaults


def _read(params: crf.Parameters, name: str, default: Any) -> Any:
    """Read one parameter back in the type its default was declared in."""
    if isinstance(default, bool):
        return params.flag(name, default)
    if isinstance(default, int):
        return params.integer(name, default)
    if isinstance(default, float):
        return params.real(name, default)
    if default is None:
        return params.text(name, "") or None
    return params.text(name, str(default))


def _make_generator(cls: type, defaults: dict[str, Any]):
    def generate(params: crf.Parameters, tech: crf.Technology) -> crf.Result:
        # Resolved fresh each call: the kit's registry is populated at import time and a technology
        # object is not ours to cache across generations.
        kit_tech = Tech.get()
        values = {name: _read(params, name, default) for name, default in defaults.items()}
        # Wire version 2 states the resolution; 1000 DBU per micrometre is circuitRF's own default and
        # the only sensible fallback for a host that predates it.
        dbu_per_micron = getattr(tech, "dbu_per_micron", None) or 1000
        return cls.generate(kit_tech, values, tech, int(dbu_per_micron))

    return generate


def register_kit(package: str, registry: Any = None, prefix: str = "",
                 only: Iterable[str] | None = None) -> KitRegistration:
    """Register every parametric cell in ``package`` as a circuitRF generator.

    ``package`` is the kit's own device package, importable because the manifest put its parent on
    ``pythonPath`` — the kit is referenced where it lies and nothing is copied.

    ``prefix`` namespaces the generator ids when two kits would otherwise collide. It is empty by
    default: circuitRF checks its own built-ins FIRST, so a kit can never shadow ``MLIN``, and an
    unprefixed id is what a user recognises from the kit's own documentation.

    **A device that cannot be read is skipped and reported, never fatal.** One unreadable module must
    not cost a user every other cell in the kit — the same rule the kit importer already follows for
    a symbol it cannot parse.
    """
    ids: list[str] = []
    problems: list[str] = []
    wanted = set(only) if only is not None else None

    try:
        # Stands in for whatever host modules the kit's own registration glue imports, learned from
        # the failures rather than from a list — see cni.hostglue.
        install_host_module_for(package)
        root = importlib.import_module(package)
    except Exception as exc:                                # noqa: BLE001
        return KitRegistration([], [f"Could not import '{package}': {exc}"])

    kit_tech = Tech.get()

    for info in pkgutil.iter_modules(getattr(root, "__path__", [])):
        module_name = f"{package}.{info.name}"
        try:
            module = importlib.import_module(module_name)
        except Exception as exc:                            # noqa: BLE001
            problems.append(f"{module_name}: {type(exc).__name__}: {exc}")
            continue

        for cls in _cells_in(module):
            generator_id = f"{prefix}{cls.__name__}"
            if wanted is not None and generator_id not in wanted:
                continue
            try:
                params, defaults = _declare(cls, kit_tech)
            except Exception as exc:                        # noqa: BLE001
                problems.append(f"{generator_id}: could not read its parameters: {exc}")
                continue

            try:
                (registry or crf.default_registry).add(
                    generator_id, params, _make_generator(cls, defaults))
            except ValueError as exc:
                # Two cells under one id is not a preference this layer can resolve, and silently
                # keeping either would make which cell you get depend on module order.
                problems.append(str(exc))
                continue
            ids.append(generator_id)

    ids.sort()
    return KitRegistration(ids, problems)
