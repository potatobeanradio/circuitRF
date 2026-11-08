"""Satisfies a kit's registration glue so its device cells can be imported.

A kit's package initialiser registers its cells with whatever tool is hosting it — it announces a
library, reads a technology file, logs. circuitRF does its own registering, so none of that is wanted;
but it runs on ``import``, so the device modules underneath it cannot be reached until it succeeds.

Measured, the glue reaches for exactly **four** names from the host module, all of them
registration concerns and none of them geometry: a logger, a technology handle, a library handle, and
a subprocess helper. So this is a doorway, not an emulation — nothing here draws anything, and if a
cell ever reached one of these for real it would be a bug in this file's premise rather than a missing
feature.

**The host module's NAME is discovered from the kit, never written down here.** A kit says which host
it expects by importing it; when that import fails, Python reports the missing module by name and this
layer installs a stand-in under exactly that name and retries. Naming a specific tool in this file
would put one vendor's identifier into circuitRF for no gain — the mechanism does not need to know
which host a kit was written against, only that one is missing.

**An installed host is found the same way: from the kit's failure.** Where the host's own Python module
is importable (a tools image commonly puts it on the path), the glue registers the cells with it — and
the real host refuses circuitRF's cell wrapper, so the kit's import fails and no cell draws. The kit
named its host by using it: the modules in the glue's own namespace at the failing frame that are not
the kit's, not circuitRF's and not the standard library. Those are stood in for and the import retried.
"""

from __future__ import annotations

import importlib
import importlib.util
import os
import sys
import types
from typing import Any

__all__ = ["install_host_module", "install_host_module_for"]

#: A kit's registration glue may import a few host modules before it gets going. Bounded so a genuinely
#: broken import chain ends as an error rather than a loop.
MAX_HOST_MODULES = 8


class _Inert:
    """Accepts anything and does nothing, loudly enough to be found if it ever matters.

    Registration glue calls methods this layer has no opinion about. Returning another inert object
    rather than raising lets the import complete; every call is recorded so a caller can check that
    nothing meaningful was asked of it.
    """

    calls: list[str] = []

    def __init__(self, path: str = "host"):
        object.__setattr__(self, "_path", path)

    def __getattr__(self, name: str) -> "_Inert":
        return _Inert(f"{object.__getattribute__(self, '_path')}.{name}")

    def __call__(self, *args: Any, **kwargs: Any) -> "_Inert":
        _Inert.calls.append(object.__getattribute__(self, "_path"))
        return _Inert(object.__getattribute__(self, "_path") + "()")

    def __iter__(self): return iter(())
    def __bool__(self): return False
    def __repr__(self): return f"<inert {object.__getattribute__(self, '_path')}>"

    def __mro_entries__(self, bases):
        """Allow the glue to SUBCLASS a host type.

        A kit's initialiser declares its library by subclassing one of the host's classes, then
        instantiates it and calls methods on it to register each cell. Python asks a non-class base
        for its real bases through this hook; answering :class:`_InertBase` means the declaration
        succeeds AND the instance keeps answering inertly, so the whole registration pass runs to
        completion and does nothing — which is exactly right, since circuitRF registers the kit's
        cells itself and never looks at the kit's own library object.
        """
        return (_InertBase,)


class _InertBase:
    """The base a kit's library class ends up with. Answers anything, does nothing."""

    def __getattr__(self, name: str) -> "_Inert":
        return _Inert(f"{type(self).__name__}.{name}")


def install_host_module(module_name: str) -> bool:
    """Put an inert stand-in in place for one host module, unless the user genuinely has the real one.

    Returns True when this layer supplied it. **A real installation wins here** — if the user has the
    actual tool available, its own behaviour is more correct than anything here, and quietly shadowing
    it would be the kind of substitution that is impossible to debug later. The exception is a host
    the kit has already failed against; see :func:`install_host_module_for`.
    """
    if module_name in sys.modules:
        return False
    try:
        __import__(module_name)
        return False
    except ImportError:
        pass

    _stand_in(module_name)
    return True


def _stand_in(module_name: str) -> None:
    module = types.ModuleType(module_name)
    module.__getattr__ = lambda name: _Inert(f"{module_name}.{name}")  # type: ignore[attr-defined]
    sys.modules[module_name] = module


def _hosts_the_glue_used(exc: BaseException, package: str) -> list[str]:
    """The installed host modules a kit's registration glue was using when its import failed.

    The glue is the kit's code outside its device package (its package initialiser, typically). A
    module in the glue's namespace that is not the kit's own — its root package, or anything found
    where that package was found — not circuitRF's and not the standard library is the host it
    registers with. Python 3.10+ only (``sys.stdlib_module_names``); older interpreters keep the
    original error.

    **A candidate whose own code is on the traceback is the host, and then only those are.** A kit's
    initialiser imports ordinary libraries beside its host, and an error the host raised in Python
    passes through the host's frames, so that narrows it to the module that refused. A host written
    as a native extension leaves no frame, and then every candidate is returned;
    :func:`install_host_module_for` puts them all back if standing in for them does not help.
    """
    stdlib = getattr(sys, "stdlib_module_names", None)
    if stdlib is None:
        return []
    root = package.split(".")[0]
    try:
        spec = importlib.util.find_spec(root)
    except (ImportError, ValueError):
        spec = None
    beside = [os.path.dirname(p) + os.sep for p in (spec.submodule_search_locations or [])] if spec else []
    hosts: list[str] = []
    on_trace: set[str] = set()
    tb = exc.__traceback__
    while tb is not None:
        scope = tb.tb_frame.f_globals
        name = str(scope.get("__name__", ""))
        on_trace.add(name.split(".")[0])
        if name.split(".")[0] == root and not name.startswith(package + "."):
            for value in list(scope.values()):
                if not isinstance(value, types.ModuleType):
                    continue
                top = value.__name__.split(".")[0]
                where = getattr(value, "__file__", None) or ""
                if (top in (root, "cni", "circuitrf_pcell") or top in stdlib or top in hosts
                        or any(where.startswith(b) for b in beside)):
                    continue
                hosts.append(top)
        tb = tb.tb_next
    return [h for h in hosts if h in on_trace] or hosts


def install_host_module_for(package: str) -> list[str]:
    """Import ``package``, standing in for whatever host modules it turns out to need.

    **The names come from the failures, not from a list in this file.** Python's
    :class:`ModuleNotFoundError` reports the missing module by name; this installs an inert stand-in
    under that name and retries, until the import succeeds or the bound is reached. So a kit written
    against any host works without circuitRF naming that host — and a kit needing nothing installs
    nothing.

    A host that IS installed is replaced too, once the kit's import has failed using it: the real one
    refuses circuitRF's objects (measured: its cell registration takes only its own declaration type),
    so it can only stop the kit from loading. A real host that does no harm is left alone.

    **A stand-in that does not help is taken back.** When the import still fails after an installed
    module was stood in for, the modules it displaced are put back and the FIRST error is raised: the
    module was not the cause, and the kit's own error is the one worth reading. Leaving the stand-in
    would also break that library for every later cell this worker draws.

    Returns the names that were stood in for, so a caller can report them. Raises the original error
    if the import fails for any other reason.
    """
    installed: list[str] = []
    displaced: dict[str, Any] = {}           # module name -> the real module a stand-in replaced
    first: BaseException | None = None
    for _ in range(MAX_HOST_MODULES):
        loaded = set(sys.modules)
        try:
            importlib.import_module(package)
            return installed
        except ModuleNotFoundError as exc:
            missing = exc.name or ""
            # The package ITSELF being missing is the caller's problem, not a host to stand in for —
            # standing in for it would hide a bad path behind an inert module that draws nothing.
            if not missing or missing == package or missing.startswith(package + "."):
                raise
            if not install_host_module(missing):
                raise
            installed.append(missing)
        except Exception as exc:                            # noqa: BLE001 - re-raised unless a host
            hosts = [h for h in _hosts_the_glue_used(exc, package) if h not in installed]
            if not hosts:
                if first is None:
                    raise
                for name, module in displaced.items():
                    sys.modules[name] = module
                raise first from None
            first = first or exc
            # The real modules are kept to be put back. Then the kit's modules from the failed attempt
            # go with it, so the retry runs the kit from the start against the stand-ins rather than
            # half against the real host.
            for name, module in list(sys.modules.items()):
                if name.split(".")[0] in hosts:
                    displaced.setdefault(name, module)
            for name in set(sys.modules) - loaded:
                if name.split(".")[0] in [package.split(".")[0], *hosts]:
                    del sys.modules[name]
            for name in hosts:
                _stand_in(name)
            installed += hosts

    raise ImportError(
        f"'{package}' still could not be imported after standing in for {len(installed)} host "
        f"module(s) ({', '.join(installed)}). This looks like a broken import chain rather than a "
        "missing host."
    )
