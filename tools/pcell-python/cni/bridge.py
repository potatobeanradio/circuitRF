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
import math
import pkgutil
import sys
from dataclasses import dataclass, replace
from typing import Any, Callable, Iterable

import circuitrf_pcell as crf

from .dlo import DloGen, Numeric, Tech
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


def _declare(cls: type, kit_tech: Any, root_package: str = "") -> tuple[
        list[crf.Parameter], dict[str, Any], list[dict[str, Any]], dict[str, list["_RecipeStep"]]]:
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
    # Watched while it runs: a default the kit COMPUTED names the function that computed it, which
    # is the only place a derived quantity's arithmetic is reachable at all. See _DeclarationRecorder.
    if root_package:
        with _DeclarationRecorder(root_package) as recorder:
            specs = cls.declared_parameters(kit_tech)
        recipes = _value_recipes(specs, recorder.calls)
    else:
        specs = cls.declared_parameters(kit_tech)
        recipes = {}

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
            param = crf.Parameter.flag(name, default)
        elif isinstance(default, int):
            param = crf.Parameter.integer(name, default)
        elif isinstance(default, float):
            param = crf.Parameter.real(name, default)
        elif default is None:
            param = crf.Parameter.text(name)
        else:
            param = crf.Parameter.text(name, str(default))

        params.append(_with_editor_hints(param, spec))

    return params, defaults, specs, recipes


def _with_editor_hints(param: "crf.Parameter", spec: dict[str, Any]) -> "crf.Parameter":
    """Carry the kit's own ``defineParamSpecs`` metadata onto the declaration circuitRF receives.

    **The kit already states this and circuitRF used to throw all of it away.** A vendor cell writes
    ``specs('Display', 'Selected', 'Display', ChoiceConstraint(['All', 'Selected']))`` — a label and
    an enumeration, sitting right there in the declaration — and the parameter arrived as a bare
    string with a free-text box under it. Across one open kit that is 127 enumerations and 9 ranges
    discarded, 42 of the enumerations two-valued yes/no pairs that are checkboxes in any dialog ever
    written.

    Nothing here changes what the cell RECEIVES; ``_declare``'s type rule is untouched and remains
    the correctness requirement it documents itself as. These are display facts only.
    """
    from .dlo import ChoiceConstraint, RangeConstraint

    label = spec.get("label")
    # A kit that passes no label gets the name echoed back as one (see _ParamSpecs.__call__). Sending
    # that is a label that says nothing, and it would suppress the name the host would otherwise show.
    if label and str(label) != param.name:
        param = replace(param, label=str(label))

    constraint = spec.get("constraint")
    if isinstance(constraint, ChoiceConstraint) and constraint.choices:
        # Choices cross in the parameter's OWN kind, not as text. A choice list that arrived as
        # strings for an int-kinded parameter would never compare equal to that parameter's value,
        # and the dropdown would show the right items with none of them selected.
        param = replace(param, choices=tuple(_as_kind(c, param.kind) for c in constraint.choices))
    elif isinstance(constraint, RangeConstraint):
        low = _as_number(constraint.low)
        high = _as_number(constraint.high)
        if low is not None or high is not None:
            param = replace(param, minimum=low, maximum=high)

    return param


#: What a kit spells "true" in a two-valued choice list. Yes/No is the overwhelmingly common one
#: (42 parameters in one open kit); t/f and nil/t are the spellings kits inherit from their own
#: scripting language and mix in alongside it.
_TRUTHY = frozenset({"yes", "true", "t", "on", "1"})


def _as_kind(value: Any, kind: str) -> Any:
    """One choice, in the kind its parameter declared. Anything that will not convert is left as
    text — a choice the host cannot represent is still better shown than dropped."""
    try:
        if kind == "bool":
            return value if isinstance(value, bool) else str(value).strip().lower() in _TRUTHY
        if kind == "int":
            return int(value)
        if kind == "real":
            return float(value)
    except (TypeError, ValueError):
        return str(value)
    return value if isinstance(value, str) else str(value)


def _as_number(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


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


class _TrackingValues(dict):
    """The parameter mapping a kit's cell is handed, remembering which names it actually read.

    A cell reads its parameters by subscript (``params['w']``), so this is where a read can be seen
    at all — :class:`circuitrf_pcell.Parameters` is drained in full before the cell ever sees it.
    """

    def __init__(self, values: dict[str, Any]):
        super().__init__(values)
        self.read: set[str] = set()

    def __getitem__(self, key):
        self.read.add(key)
        return super().__getitem__(key)

    def get(self, key, default=None):
        self.read.add(key)
        return super().get(key, default)


def _calculable_names(specs: list[dict[str, Any]]) -> set[str]:
    """The parameters a cell's own CDF ``Calculate`` selector says are solved for.

    A kit states this structurally and it is worth reading structurally. A selector is a parameter
    whose ``ChoiceConstraint`` lists nothing but the names of OTHER parameters of the same cell,
    optionally grouped — ``['C', 'w', 'l', 'w&l']`` on a MIM cap, ``['R', 'w', 'l']`` on a resistor,
    ``['R,A', 'w,A', 'l,A', ...]`` on a poly resistor. That the choices resolve to declared parameter
    names is the whole test; nothing keys off the selector being spelled "Calculate", because the
    next kit will spell it differently and a name list is a table to maintain.

    This alone does not say which of them is the output — see :func:`_computed_names`.
    """
    from .dlo import ChoiceConstraint

    names = {str(s.get("name", "")) for s in specs}
    names.discard("")
    calculable: set[str] = set()
    for spec in specs:
        constraint = spec.get("constraint")
        if not isinstance(constraint, ChoiceConstraint) or not constraint.choices:
            continue
        parts: set[str] = set()
        for choice in constraint.choices:
            for part in str(choice).replace("&", ",").split(","):
                part = part.strip()
                if part:
                    parts.add(part)
        # Every part must name a parameter. A choice list of layer names or model names has nothing
        # to do with solving for a quantity, and this is what tells the two apart.
        if parts and parts <= names and str(spec.get("name", "")) not in parts:
            calculable |= parts
    return calculable


def _computed_names(calculable: set[str], read: set[str]) -> set[str]:
    """Which of a cell's calculable quantities this run treated as an OUTPUT.

    **Both halves are needed and each without the other is wrong.**

    *The selector alone is not enough, and its stated value is not to be believed.* An open kit's MIM
    cap declares ``Calculate`` defaulting to ``'w&l'`` — read literally, w and l are the outputs and
    C is the input. The opposite is true of the code that actually runs: ``setupParams`` reads only w
    and l, and C is never read at any setting of ``Calculate``. Measured, not argued: generating the
    cell with C at its default, at 300 fF and at 1 pF yields byte-identical geometry, while changing
    w or l changes it. The vendor's dialog is where the back-solve lived; the layout port kept the
    declaration and not the behaviour.

    *Reads alone are not enough either.* A cell reads none of ``model``, ``m``, ``ic``, ``trise`` —
    they are netlist parameters that never had anything to do with the artwork, and they are still
    the user's to type into. Locking every unread parameter would take the model name away.

    The intersection is exactly the set the kit itself calls a solvable quantity and its own code
    then declines to read. Where the two disagree the parameter stays editable, which is the failure
    direction that costs nothing: one open kit has a resistor cell that assigns its resistance
    parameter to an instance attribute and then never uses it, so it reads as an input and keeps its
    edit box.
    """
    return calculable - read


# ── the kit's own calculator, found and replayed ─────────────────────────────────────────────
#
# A cell that DERIVES a quantity usually does not compute it: the vendor's dialog did, and the
# layout port kept the declaration without the behaviour. But the kit still ships the arithmetic —
# ``defineParamSpecs`` calls it, once, to produce that parameter's DEFAULT:
#
#     C = CapCalc('C', 0, Numeric(defLW), Numeric(defLW), 'mim')
#     specs('C', eng_string(C), 'C')
#
# So the function is right there, reachable, and already proven to produce a value of the right
# kind — it produced the one the declaration ships with. What it was not given is THIS instance's
# geometry. Watching the declaration run records the route; running that route again with the
# instance's own values is the derived number, computed by the kit.
#
# **circuitRF does no arithmetic of its own here and must not start.** It never models what a
# capacitance or a resistance is; it re-invokes the kit's function with different arguments. Where
# the route cannot be established the parameter is reported as derived WITH NO VALUE, exactly as
# before, and the host shows no number rather than a wrong one.


@dataclass
class _RecordedCall:
    """One call a kit made while declaring a cell's parameters."""

    fn: Callable[..., Any]
    names: tuple[str, ...]      # the callee's own parameter names, in order
    args: list[Any]             # what it was called with
    result: Any


class _DeclarationRecorder:
    """Records the kit's own module-level calls while ``defineParamSpecs`` runs.

    Tracing rather than wrapping the module's functions: a cell reaches its helpers by whatever name
    it likes — a star-import, a qualified module attribute, a helper calling a helper — and patching
    one module's globals sees only the first of those. The trace sees the call wherever it is made,
    and is filtered to the kit's own package so nothing else in the process is recorded.

    Active only while a cell DECLARES its parameters (once per cell, at registration), never during
    generation.
    """

    def __init__(self, root_package: str):
        self._root = root_package.split(".")[0]
        self.calls: list[_RecordedCall] = []
        self._previous: Any = None

    def __enter__(self) -> "_DeclarationRecorder":
        self._previous = sys.gettrace()
        sys.settrace(self._on_call)
        return self

    def __exit__(self, *_exc: Any) -> bool:
        sys.settrace(self._previous)
        return False

    def _on_call(self, frame, event, _arg):
        if event != "call":
            return None
        module = frame.f_globals.get("__name__", "")
        if module.split(".")[0] != self._root:
            return None

        code = frame.f_code
        fn = frame.f_globals.get(code.co_name)
        # A plain module-level function only. A method or a closure cannot be re-invoked from a
        # recording without also reconstructing whatever it was bound to, and a derived value that
        # needs that is one this cannot honestly claim to replay.
        if not (inspect.isfunction(fn) and fn.__code__ is code):
            return None

        names = code.co_varnames[:code.co_argcount]
        try:
            args = [frame.f_locals[n] for n in names]
        except KeyError:                                    # a frame mid-teardown; not ours to read
            return None

        frame.f_trace_lines = False

        def on_return(_f, ev, value):
            # Appended on RETURN, so the list is in completion order: an inner call lands before the
            # outer one that consumed it, which is the order the chain search below walks backwards.
            if ev == "return":
                self.calls.append(_RecordedCall(fn, names, args, value))
            return None

        return on_return


#: How many nested calls a derived default may be wrapped in before the route is abandoned. A
#: formatter around a calculator is one level; three is already generous.
_MAX_RECIPE_DEPTH = 6


@dataclass
class _RecipeStep:
    """One call to replay: the kit's function, the arguments it was recorded with, which of those
    are the cell's own parameters, and which one takes the previous step's result."""

    fn: Callable[..., Any]
    args: list[Any]
    substitutions: dict[int, str]       # argument index -> cell parameter name
    feed: int | None                    # argument index fed by the previous step, or None


def _replayable(value: Any) -> bool:
    """Whether a value can be tracked from one recorded call to another.

    Matching is by IDENTITY — ``specs('C', eng_string(C), 'C')`` hands on the very object the
    calculator returned — which is exact where it applies and silent where it does not. Ints, bools
    and one-character strings are excluded because Python interns them: two unrelated calls that both
    return ``0`` are the same object, and a chain built on that coincidence would report a number
    from the wrong function.
    """
    return isinstance(value, float) or (isinstance(value, str) and len(value) >= 2)


def _producer(calls: list[_RecordedCall], value: Any, before: int) -> int:
    """The last call before ``before`` whose result IS ``value``, or -1."""
    for i in range(before - 1, -1, -1):
        if calls[i].result is value:
            return i
    return -1


def _is_declared_default(recorded: Any, declared: Any) -> bool:
    """Whether a recorded argument is the parameter's declared default, read the kit's own way.

    A kit states a default in its own notation (``'6.99u'``) and hands the calculator the number it
    parses to (``6.99e-06``), so the two are compared as numbers through the kit's own reader — never
    as text, and never through a parser of circuitRF's.
    """
    if declared is None or isinstance(recorded, bool):
        return False
    if isinstance(recorded, str):
        return recorded == str(declared)
    if not isinstance(recorded, (int, float)):
        return False
    try:
        want = Numeric(declared)
    except (TypeError, ValueError):
        return False
    return math.isclose(float(recorded), want, rel_tol=1e-9, abs_tol=1e-30)


def _substitutions(call: _RecordedCall, defaults: dict[str, Any], own_name: str) -> dict[int, str]:
    """Which of a recorded call's arguments are this cell's parameters.

    **Both halves are required and the value half is what makes it safe.** The callee naming an
    argument ``w`` is a strong hint and nothing more — plenty of functions take a width that is not
    this cell's. That the value it was handed is also exactly what this cell declares ``w`` to
    default to is what identifies it, and it is checkable rather than assumed.

    The parameter being derived is never substituted into its own calculation: a solver takes the
    quantity as an argument when solving the other way round (``calc='w'`` reads ``c``), and feeding
    it the stale stored value would be feeding back the very number this exists to replace.
    """
    by_lowered = {name.lower(): name for name in defaults}
    found: dict[int, str] = {}
    for index, (arg_name, value) in enumerate(zip(call.names, call.args)):
        param = by_lowered.get(arg_name.lower())
        if param is None or param == own_name:
            continue
        if _is_declared_default(value, defaults[param]):
            found[index] = param
    return found


def _recipe_for(name: str, default: Any, calls: list[_RecordedCall],
                defaults: dict[str, Any]) -> list[_RecipeStep] | None:
    """The route from this cell's parameters to one declared default, or None if there is none."""
    if not _replayable(default):
        return None

    steps: list[_RecipeStep] = []
    target, before = default, len(calls)
    for _ in range(_MAX_RECIPE_DEPTH):
        index = _producer(calls, target, before)
        if index < 0:
            break
        call = calls[index]
        substitutions = _substitutions(call, defaults, name)

        # Only a call that does NOT take the cell's parameters is looked through. Once one does, it
        # is the calculator and the search stops there; anything outside it is formatting, and
        # anything inside it is that calculator's own business.
        feed = None
        if not substitutions:
            for i, value in enumerate(call.args):
                if _replayable(value) and _producer(calls, value, index) >= 0:
                    feed = i
                    break

        steps.append(_RecipeStep(call.fn, list(call.args), substitutions, feed))
        if feed is None:
            break
        target, before = call.args[feed], index

    # A route with nothing of the cell's own in it computes the same number for every instance. That
    # is not a derived value, it is a constant, and reporting it per run would only make a stale
    # number look live.
    if not any(step.substitutions for step in steps):
        return None

    steps.reverse()                                          # innermost first: the order to replay in
    return steps


def _value_recipes(specs: list[dict[str, Any]], calls: list[_RecordedCall]) -> dict[str, list[_RecipeStep]]:
    """Every declared default this cell computed, and how to compute it again."""
    defaults: dict[str, Any] = {}
    for spec in specs:
        name = str(spec.get("name", ""))
        if name and name not in defaults:
            defaults[name] = spec.get("default")

    recipes: dict[str, list[_RecipeStep]] = {}
    for name, default in defaults.items():
        recipe = _recipe_for(name, default, calls, defaults)
        if recipe and _reproduces_the_default(recipe, name, default, defaults):
            recipes[name] = recipe
    return recipes


def _reproduces_the_default(recipe: list[_RecipeStep], name: str, default: Any,
                            defaults: dict[str, Any]) -> bool:
    """Replay a route at the cell's OWN defaults and require the declared default back.

    **The route is inferred, so it is checked before it is believed, against the one answer already
    known to be right.** A recipe that reproduces the shipped default has been shown end to end —
    the right function, the right arguments, in the right places, formatted the way the kit formats
    it — on the single case where a wrong answer is detectable. One that does not is discarded and
    the parameter goes back to being named as derived with no value, which is what it would have been
    anyway.

    It cannot prove the substitution positions are right for a cell whose defaults are all equal
    (a square device tells w from l only by which one moved), but a symmetric formula does not care
    and an asymmetric one is caught the moment either default differs.
    """
    try:
        replayed = _run_recipe(recipe, crf.Parameters({}), defaults)
    except Exception as exc:                                # noqa: BLE001 - an unusable route, not a fault
        print(f"could not verify how '{name}' is derived; it will be reported without a value: {exc!r}",
              file=sys.stderr)
        return False
    if isinstance(default, str):
        return str(replayed) == default
    return _is_declared_default(replayed, default)


def _as_recorded(value: Any, recorded: Any) -> Any:
    """One instance value in the shape the kit's function was recorded receiving.

    The recording says what the function is fed — a parsed number or the kit's own notation — and the
    instance value arrives as whatever the host holds. Matching the recorded shape is what lets a
    calculator that does no parsing of its own be called with a value that needed some.
    """
    if isinstance(recorded, bool):
        return bool(value)
    if isinstance(recorded, str):
        return str(value)
    if isinstance(recorded, float):
        return Numeric(value)
    if isinstance(recorded, int):
        return int(Numeric(value))
    return value


def _run_recipe(steps: list[_RecipeStep], params: crf.Parameters, defaults: dict[str, Any]) -> Any:
    """Replay one route with this instance's parameters. Raises; the caller decides what that costs."""
    value: Any = None
    for step in steps:
        args = list(step.args)
        for index, param in step.substitutions.items():
            args[index] = _as_recorded(_read(params, param, defaults.get(param)), step.args[index])
        if step.feed is not None:
            args[step.feed] = value
        value = step.fn(*args)
    return value


def _make_generator(cls: type, defaults: dict[str, Any], specs: list[dict[str, Any]],
                    recipes: dict[str, list[_RecipeStep]] | None = None):
    calculable = _calculable_names(specs)
    recipes = recipes or {}

    def generate(params: crf.Parameters, tech: crf.Technology) -> crf.Result:
        # Resolved fresh each call: the kit's registry is populated at import time and a technology
        # object is not ours to cache across generations.
        kit_tech = Tech.get()
        values = _TrackingValues({name: _read(params, name, default)
                                  for name, default in defaults.items()})
        # Wire version 2 states the resolution; 1000 DBU per micrometre is circuitRF's own default and
        # the only sensible fallback for a host that predates it.
        dbu_per_micron = getattr(tech, "dbu_per_micron", None) or 1000
        result = cls.generate(kit_tech, values, tech, int(dbu_per_micron))

        # Reported per run rather than declared once, because the read set is only observable by
        # running the cell — and a cell may legitimately read a parameter at one setting and not at
        # another. The host takes the latest word.
        # What the cell did not look at. Measured on the same read set the output inference uses, and
        # reported as the plain fact it is — see Result.unread for why it locks nothing.
        if isinstance(getattr(result, "unread", None), list):
            result.unread = sorted(set(defaults) - values.read)

        computed = _computed_names(calculable, values.read)
        if computed and isinstance(getattr(result, "computed", None), dict):
            automatic: set[str] = set()
            for name in computed:
                # None: named as an output, with no value stated. Nothing in the CELL computes the
                # quantity — the vendor's own dialog callback did — so claiming a number here would
                # be inventing one. See Result.computed.
                result.computed.setdefault(name, None)
                # ...unless the kit's own calculator was seen producing this parameter's declared
                # default, in which case it is re-invoked with this instance's values. The number is
                # the kit's; a failure costs the readout and never the cell.
                if result.computed[name] is None and name in recipes:
                    try:
                        result.computed[name] = _run_recipe(recipes[name], params, defaults)
                        automatic.add(name)
                    except Exception as exc:                # noqa: BLE001 - see the note above
                        print(f"{cls.__name__}: could not compute '{name}' from the kit's own "
                              f"calculator: {exc!r}", file=sys.stderr)
            # Named so a calculator supplied by hand can still take precedence over one found this
            # way — see circuitrf_pcell.reports_computed, which would otherwise see a value already
            # present and silently drop its own.
            if automatic:
                result.auto_computed = automatic
        return result

    return generate


def _interpreter_note(exc: BaseException) -> str:
    """Names the interpreter that did the parsing, on a SyntaxError only.

    A kit's own sources are not broken — they parse for the vendor. When they do not parse here it
    is almost always that this interpreter is older than the syntax they use, and the bare message
    ("invalid syntax (res_base_code.py, line 61)") reads instead as a broken kit and sends the
    reader to the vendor. IHP's sg13g2 is the worked example: its cells use `match`, so every one of
    them fails this way under Python 3.9 and none of them under 3.10.

    Stated as a fact about which interpreter ran rather than as a diagnosis — a genuine syntax error
    in a kit is still possible, and the version is the piece the reader cannot otherwise see.
    """
    if not isinstance(exc, SyntaxError):
        return ""

    v = sys.version_info
    return (f" — parsed by Python {v.major}.{v.minor}.{v.micro} ({sys.executable}). If the kit needs"
            f" a newer one, name it as \"interpreter\" in this generator's manifest, or as"
            f" \"PythonInterpreter\" in the workspace's .cws.")


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
        return KitRegistration([], [f"Could not import '{package}': {exc}{_interpreter_note(exc)}"])

    kit_tech = Tech.get()

    for info in pkgutil.iter_modules(getattr(root, "__path__", [])):
        module_name = f"{package}.{info.name}"
        try:
            module = importlib.import_module(module_name)
        except Exception as exc:                            # noqa: BLE001
            problems.append(f"{module_name}: {type(exc).__name__}: {exc}{_interpreter_note(exc)}")
            continue

        for cls in _cells_in(module):
            generator_id = f"{prefix}{cls.__name__}"
            if wanted is not None and generator_id not in wanted:
                continue
            try:
                params, defaults, specs, recipes = _declare(cls, kit_tech, package)
            except Exception as exc:                        # noqa: BLE001
                problems.append(f"{generator_id}: could not read its parameters: {exc}")
                continue

            try:
                (registry or crf.default_registry).add(
                    generator_id, params, _make_generator(cls, defaults, specs, recipes))
            except ValueError as exc:
                # Two cells under one id is not a preference this layer can resolve, and silently
                # keeping either would make which cell you get depend on module order.
                problems.append(str(exc))
                continue
            ids.append(generator_id)

    ids.sort()
    return KitRegistration(ids, problems)
