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
"""

from __future__ import annotations

import importlib
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

    Returns True when this layer supplied it. **A real installation always wins** — if the user has
    the actual tool available, its own behaviour is more correct than anything here, and quietly
    shadowing it would be the kind of substitution that is impossible to debug later.
    """
    if module_name in sys.modules:
        return False
    try:
        __import__(module_name)
        return False
    except ImportError:
        pass

    module = types.ModuleType(module_name)
    module.__getattr__ = lambda name: _Inert(f"{module_name}.{name}")  # type: ignore[attr-defined]
    sys.modules[module_name] = module
    return True


def install_host_module_for(package: str) -> list[str]:
    """Import ``package``, standing in for whatever host modules it turns out to need.

    **The names come from the failures, not from a list in this file.** Python's
    :class:`ModuleNotFoundError` reports the missing module by name; this installs an inert stand-in
    under that name and retries, until the import succeeds or the bound is reached. So a kit written
    against any host works without circuitRF naming that host — and a kit needing nothing installs
    nothing.

    Returns the names that were stood in for, so a caller can report them. Raises the original error
    if the import fails for any reason other than a missing module.
    """
    installed: list[str] = []
    for _ in range(MAX_HOST_MODULES):
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

    raise ImportError(
        f"'{package}' still could not be imported after standing in for {len(installed)} host "
        f"module(s) ({', '.join(installed)}). This looks like a broken import chain rather than a "
        "missing host."
    )
