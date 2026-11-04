"""circuitRF's EMerge driver (brief-emi-4).

A CONSTANT program: circuitRF writes it unchanged into every EMerge run directory beside that run's problem.json, which is
pure data -- every object by index, every length in metres, every decision made by circuitRF before this starts. Nothing
read from problem.json is ever executed, evaluated or imported: names are only labels handed to EMerge, numbers are
numbers, and a word of the format is only ever compared against a fixed set.

Each solid is built with the same OpenCASCADE calls circuitRF's Gmsh script for Palace writes, then handed to EMerge as
one of its own volumes, so the two FEM solvers mesh one geometry. Overlaps are EMerge's priorities (circuitRF's precedence,
already ranked). Kernel solids are their B-rep files, imported as they are: they are in micrometres, the model's units
while it is built and meshed (MODEL_SCALE).

    python -I circuitrf_emerge.py <problem.json> mesh

builds the model and meshes it, and prints one line of JSON: the counts, and any warning. Exit 0 on success; 2 with an
"error" line when the input or EMerge refuses it.

    python -I circuitrf_emerge.py <problem.json> solve [gpu-from=<unknowns>]

(brief-emi-9) gpu-from: a driven problem of at least that many unknowns is factorised by EMerge's GPU sparse solver, with
its iterative refinement on; absent, or an eigenmode problem, it never is. It is an argument and not part of problem.json:
where a problem is solved is the machine's, and problem.json is what the run's provenance hashes.

(brief-emi-5) builds, meshes and solves it, and writes PLAIN ARRAYS beside problem.json -- circuitRF builds the DataSet,
the Touchstone and their provenance from them, so EMerge's own Touchstone writer is never used:
  driven     freq.npy (the requested grid, Hz), S.npy (complex, [f, i, j], ports in ascending number), Z0.npy (complex,
             [f, ports]: a lumped port's reference, constant; a wave port's mode impedance at each frequency), gamma.npy
             (complex, [f, ports]: a wave port's propagation constant alpha + j beta, 0 for a lumped port)
  eigenmode  modes.npy (complex frequencies, Hz, as EMerge returns them -- null-space solutions included; circuitRF
             filters), Q.npy (each one's Q)
and run.json (versions, the sparse solver EMerge called, tets, samples, the sweep's error, timings). The JSON line it
prints last is run.json's content.

(brief-emw-3) A wave-port entry with "terminals" is a TERMINAL GROUP: one ModalPort carrying one mode per terminal (EMerge
numbers them <number>.1 ... <number>.N, terminal order), never a WavePortIH (it carries one mode) and never align_modes (for
terminals it picks one mode twice). A face of one material has its modes solved once, before the sweep, around its
presolveNeff (EMerge's own shift there sits on the TEM eigenvalue and misses the second TEM mode); a face carrying more or
fewer modes of one speed than terminals is refused, naming the cause. A face of several materials is EMerge's own
per-frequency solve, each frequency's modes checked to be the terminal count and TEM-classified. When the problem has a
group, the driven solve also writes, per requested frequency, over every wave-port entry in circuitRF port order
([f, n, n], block-diagonal by face): TV.npy (path k's integral of mode n's E, 21-point Gauss-Legendre), W.npy (the
E-overlap EMerge's S projection uses, <e_n, e_i> / <e_i, e_i> over the face), Gp.npy (1/2 integral of e_n x conj(h_m) . n);
gamma.npy and Z0.npy carry each entry's own mode (Z0 = |TV_kk|^2 / 2: modes carry 1 W). At a frequency solved directly (a
point sweep) V.npy is each entry's path integral of the TOTAL E for each excitation [f, n, ports]: the run's check, never the
conversion's input. A terminal group's own excitations are modal, so no field step is saved for one.

(brief-emi-7) When problem.json has a "fields" block, the solve also writes FIELDS, in the .vtu layout circuitRF reads for
Palace (an UnstructuredGrid of Lagrange tetrahedra, every cell with its own nodes, inline-base64 geometry, appended-raw
arrays, no compression, complex quantities as <name>_real/<name>_imag):
  driven     postpro/paraview/driven/excitation_<port>/Cycle<k>/data.pvtu + proc000000.vtu, one per (port, frequency) asked
             for, each from a direct solve at that frequency (the adaptive sweep's fields are at its own samples). E and B
             at a PEAK incident power of fields.incidentPowerW, circuitRF's reading of Palace's convention; ff_*.npy, the
             far field on the air box's absorbing faces, when fields.farField asks.
  eigenmode  postpro/paraview/eigenmode/Cycle<k>/..., mode k-1 of modes.npy, normalised so the integral of eps|E|^2 over
             the mesh is fields.modeEnergyJ (Palace's mode normalisation), its largest field component real.
(brief-emi-11) Beside every step, on the conductors' walls and the sheets, the boundary step Palace writes beside its own:
postpro/paraview/driven_boundary/excitation_<port>/Cycle<k>/... or eigenmode_boundary/Cycle<k>/..., Lagrange triangles
with J_s (the jump in tangential H, n x [H]) and Q_s (n . [D]) at the volume step's scale, and groups.json names each
conductor's and sheet's faces (Dimension 2, Kind Conductor or Sheet) as Palace's mesh does.
run.json's "fieldSteps" lists them with what circuitRF needs beside them: each driven step's port voltage and incident
voltage, each mode's integral of eps|E|^2 per material and each lumped port's voltage. circuitRF writes the .pvd collections.
"""

import json
import os
import re
import sys

DRIVER_VERSION = 8
FORMAT = "circuitRF EMerge problem"
FORMAT_VERSION = 2
C0 = 299792458
# EMerge's ModalPort.N_mesh_tris: the triangles its mesher aims a port face at (bc._size_constraint), when the port exists
# before the mesh does. circuitRF assigns its conditions after meshing, so the driver states that size itself (brief-emw-2).
PORT_TRIANGLES = 50
MODES = ("mesh", "solve")
RUN_FILE = "run.json"


def report(**fields):
    fields["driver"] = DRIVER_VERSION
    print(json.dumps(fields, sort_keys=True), flush=True)


def fail(message):
    report(error=message)
    sys.exit(2)


class Refused(Exception):
    """brief-emw-3 — what the run refuses once EMerge has shown it (a terminal face's modes): its message is the sentence
    circuitRF reports, and its line says "refused" so the run is a refusal, not a failure. brief-dfr28-3 — cause names any
    other kind of refusal on that line (NON_FINITE); none is a terminal face's."""

    def __init__(self, message, cause=None):
        super().__init__(message)
        self.cause = cause


def main(argv):
    gpu_from = None
    if len(argv) == 4 and argv[2] == "solve" and argv[3].startswith("gpu-from="):
        try:
            gpu_from = int(argv[3][len("gpu-from="):])
        except ValueError:
            gpu_from = -1
        if gpu_from < 0:
            fail("gpu-from takes a whole number of unknowns, not '%s'" % argv[3])
    elif len(argv) != 3 or argv[2] not in MODES:
        fail("usage: circuitrf_emerge.py <problem.json> " + "|".join(MODES) + " [gpu-from=<unknowns>]")
    path = os.path.abspath(argv[1])
    with open(path, encoding="utf-8") as f:
        problem = json.load(f)
    if problem.get("format") != FORMAT or problem.get("version") != FORMAT_VERSION:
        fail("this driver reads %s version %d, and %s is not one" % (FORMAT, FORMAT_VERSION, os.path.basename(path)))

    import emerge as em
    import gmsh
    try:
        from emerge._emerge.geometry import GeoVolume, GeoSurface
    except ImportError as e:
        fail("this EMerge has no geometry classes where circuitRF's driver looks for them (%s); tested with EMerge %s"
             % (e, ", ".join(problem["testedEmerge"])))

    warnings = []
    version = getattr(em, "__version__", None)
    if version not in problem["testedEmerge"]:
        warnings.append("EMerge %s is not a version circuitRF has tested (%s); it is run as it is."
                        % (version, ", ".join(problem["testedEmerge"])))

    install_guard(warnings)
    install_thin_conductor_split(warnings)
    import time
    started = time.time()
    try:
        sim, counts, model = build(problem, os.path.dirname(path), em, gmsh, GeoVolume, GeoSurface)
    except Exception as e:  # EMerge's or Gmsh's own refusal, reported as the run's
        fail(named(e))
    if argv[2] == "mesh":
        if model["waves"]:
            counts["wavePorts"] = wave_conditions(sim, model["waves"])
        report(emerge=version, warnings=warnings, **counts)
        return
    meshed = time.time()
    # The mesh's size, before any solve: circuitRF checks the run's memory on it and stops the run when it is refused.
    report(stage="meshed", tets=counts["tets"], edges=counts["edges"], triangles=counts["triangles"])
    keep_x64_solver_off()
    on_gpu = use_gpu(problem, counts, gpu_from, sim, em, warnings)
    if not on_gpu:
        keep_gpu_solver_off()
    # After the build: creating the Simulation reconfigures EMerge's logger, which would drop a handler added before it.
    # Not caught: the stall watch stops a GPU sweep by raising from the sink (GpuStalled).
    heard = Heard(on_gpu)
    from loguru import logger
    logger.add(heard.sink, level="INFO", format="{message}", catch=False)
    if (unguarded := guard_solves(sim, model, heard)) is not None:
        warnings.append(unguarded)
    try:
        outcome = solve(problem, os.path.dirname(path), sim, model, heard, warnings)
    except Refused as e:
        report(error=str(e), refused=True, **({"cause": e.cause} if e.cause else {}))
        sys.exit(2)
    except Exception as e:
        fail(named(e))
    # The device the solves USED, read back from the solver EMerge's log says it called, never assumed from what was asked.
    device = None if heard.solver is None else ("gpu" if heard.solver == GPU_SOLVER_NAME else "cpu")
    run = dict(emerge=version, warnings=warnings, sparseSolver=heard.solver, device=device, gpuFrom=gpu_from,
               meshSeconds=round(meshed - started, 3), solveSeconds=round(time.time() - meshed, 3), **counts)
    run.update(outcome)
    run["tets"] = int(sim.mesh.n_tets)          # after any adaptive refinement
    mw = sim.mw
    basis = getattr(mw, "basis", None)
    run["unknowns"] = int(basis.n_field) if basis is not None and hasattr(basis, "n_field") else None
    run["driver"] = DRIVER_VERSION
    with open(os.path.join(os.path.dirname(path), RUN_FILE), "w", encoding="utf-8") as f:
        json.dump(run, f, sort_keys=True)
    report(**run)


# -- the duplicate-mode guard (brief-emw-2) -------------------------------------------------------------------------------

def install_guard(warnings):
    """EMerge 3.0.0b2 APPENDS a re-solved frequency's modes to those already stored for it (a sweep solves its mean frequency
    twice, and an adaptive sweep every sample), so a face's stored list can hold one mode twice (src/Design/RESOLVED.md,
    EMW-1, R-emw1-6). Before EMerge solves a port's modes at a frequency, the guard forgets that frequency's stored ones.
    Harmless if a later EMerge stops duplicating, so it is always installed; a run whose EMerge has moved the method or the
    attribute is told so and runs."""
    try:
        from emerge._emerge.physics.microwave.microwave_3d import Microwave3D
        solve_modes = Microwave3D.modal_analysis
    except (ImportError, AttributeError):
        warnings.append("circuitRF could not install its guard against EMerge storing a port's modes twice (this EMerge has "
                        "no Microwave3D.modal_analysis where circuitRF looks for it); the run goes on without it.")
        return

    warned = []

    def modal_analysis(self, port, *a, freq=None, **k):
        f = self.frequencies[0] if freq is None else freq
        stored = getattr(port, "available_modes", None)
        if stored is None:
            if not warned:
                warnings.append("circuitRF's guard against EMerge storing a port's modes twice found no available_modes on the "
                                "port (a later EMerge renamed it); the run goes on without it.")
                warned.append(True)
        else:
            stored.pop(2 * 3.141592653589793 * f / C0, None)     # EMerge's own k0 expression
        return solve_modes(self, port, *a, freq=freq, **k)

    Microwave3D.modal_analysis = modal_analysis


# -- the thin-conductor split on a refined mesh ----------------------------------------------------------------------------

def install_thin_conductor_split(warnings):
    """EMerge 3.0.0b2 splits a lossy sheet's degrees of freedom in its one-time boundary setup, but a mesh refinement builds a new
    basis, which then has no split: its second pass stops with "'NoneType' object is not subscriptable". 3.0.0b3 splits each new
    basis, and this does the same on b2; on b3 the basis is already split and nothing happens. A run whose EMerge has moved the
    method is told so and runs."""
    try:
        from emerge._emerge.physics.microwave.microwave_3d import Microwave3D, ThinConductor
        initialize = Microwave3D._initialize_bc_data
    except (ImportError, AttributeError):
        warnings.append("circuitRF could not install its repair of a lossy sheet on a refined mesh (this EMerge has no "
                        "Microwave3D._initialize_bc_data where circuitRF looks for it); the run goes on without it.")
        return

    def initialize_bc_data(self):
        initialize(self)
        sheets = self.bc.oftype(ThinConductor)
        if len(sheets) > 0 and not self.basis._partitioned:
            self.basis.partition_dof([x.tags for x in sheets])
            self.basis._partitioned = True

    Microwave3D._initialize_bc_data = initialize_bc_data


# -- the GPU (brief-emi-9) -------------------------------------------------------------------------------------------------

GPU_SOLVER_NAME = "SPARTA"   # how EMerge 3.0.0b2's log names it: "Calling SPARTA Solver (GPU, 0.2.0)."


def use_gpu(problem, counts, gpu_from, sim, em, warnings):
    """Hands a driven problem of at least gpu_from unknowns to EMerge's GPU sparse solver, with its iterative refinement ON.

    EMerge never picks this solver itself, and when it is forced EMerge calls it with refinement_steps=0: a bare
    single-precision factorisation. Measured on the Cavity (src/Design/RESOLVED.md, EMI-9): bare, its answers differed from the
    CPU's by up to 8e-4 and the adaptive sweep ran to its 100-sample cap without converging at 9k tets; with the solver's own
    default refinement (-1: as many double-precision corrections as its 1e-6 residual target needs) it converged in exactly
    the CPU's sample count at every mesh, within 1.5e-6 of the CPU's S, and still faster."""
    if gpu_from is None or problem["problem"] != "driven":
        return False
    order = problem["settings"]["elementOrder"]
    unknowns = 2 * (counts["edges"] + counts["triangles"]) if order == 2 else counts["edges"]
    if unknowns < gpu_from:
        return False
    routine = sim.mw.solveroutine
    kind, loaded = gpu_solver_class()
    gpu = routine.solvers.get(kind) if kind is not None else None
    if gpu is None or not loaded:
        warnings.append("EMerge's GPU sparse solver could not be loaded, so this run solved on the CPU.")
        return False
    gpu.settings["refinement_steps"] = -1
    # EMerge 3.0: the solver's mode follows the routine's device, so forcing the solver alone runs its CPU path.
    if hasattr(routine, "set_device"):
        routine.set_device("GPU")
    routine.set_solver(kind)
    return True


def gpu_solver_class():
    """The GPU sparse solver as this EMerge's solve routine keys it, and whether it can run: (None, False) when there is none.
    EMerge 3.0 made the solvers classes in a package; before it they were an enum's members, with one flag for each."""
    try:
        from emerge._emerge.solvers import SolverSparta
        return SolverSparta, bool(SolverSparta.available and SolverSparta.gpu)
    except ImportError:
        from emerge._emerge import solver as s
        members = s.EMSolver
        return getattr(members, "SPARTA", None), bool(getattr(s, "_SPARTA_AVAILABLE", None))


# What EMerge 3.0 can run on arm64, by its names in em.solvers. Everything else there needs an x64 library.
ARM64_SOLVERS = ("SuperLU", "UMFPACK", "MUMPS", "AASDS", "CHOLMOD", "RSLab", "Sparta", "BiCGSTAB", "CG", "Corvid", "LAPACK", "ARPACK")


def keep_x64_solver_off():
    """EMerge 3.0 takes its x64 vendor library's solver as available when "arm" is not in platform.processor(), which on Linux arm64
    says aarch64. It then picks that solver, finds no library and stops with an UnboundLocalError at the first solve, on every
    Linux arm64 machine (src/Design/RESOLVED.md, "EMerge 3.0.0"). The library has no arm64 build anywhere, so it is taken out here."""
    import platform
    if platform.machine().lower() not in ("aarch64", "arm64"):
        return
    try:
        from emerge._emerge.solvers import PUBLIC_SOLVERS
    except ImportError:
        return
    for name, kind in PUBLIC_SOLVERS.items():
        if name not in ARM64_SOLVERS:
            kind.available = False


def keep_gpu_solver_off():
    """EMerge 3.0's own pick is the GPU solver's CPU path whenever it is installed, ahead of the macOS sparse solver, which is
    faster on the same answer (src/Design/RESOLVED.md, "EMerge 3.0.0"), and a run's device is read from the solver's name, so it
    would also say GPU. Its picker reads the class's flag and then goes on down its list; SolveRoutine.disable would instead land
    on the single-threaded default. Before 3.0 EMerge never picked it."""
    try:
        from emerge._emerge.solvers import SolverSparta
        SolverSparta.available = False
    except ImportError:
        pass


# brief-emw-3 — an adaptive sweep on the GPU whose estimated error has not reached a new low in this many samples has
# stalled: the GPU's single precision leaves its samples too noisy for the fit (the Coupled Microstrip, 336,748 unknowns:
# its best error 3.0e-3 at sample 16, no better by sample 80, where the CPU converged in 9; src/Design/RESOLVED.md, EMW-3).
GPU_STALL_SAMPLES = 8

# brief EMG-1 — the most a GPU solve may miss its own equations by, as ||Ax - b|| / ||b|| in double precision. The solver's flag
# below is not enough: it is read before the solve, and points it passed were up to 3.5e-3 from the CPU's S, past the sweep's
# 1e-3 tolerance. Every point past that read 4.2e-3 or more, and nearly all within it 7.5e-4 or less (src/Design/RESOLVED.md, EMG-1).
GPU_RESIDUAL_LIMIT = 1e-3

# brief-dfr28-1 follow-up — what EMerge 3.0.0b2 logs when the GPU solver's own check finds its factorisation unusable
# (factor.quality.usable is false). EMerge solves with it anyway. On the TP149 board (67,712 tets, order 2) it said so at the
# first frequency and every one after, and the sweep chased noise round 350 MHz: the sweep goes to the CPU at once.
GPU_UNUSABLE = "Matrix is so singular that it is broken on single precision"


class GpuStalled(Exception):
    """Raised from the log sink to stop a stalled GPU sweep; the driver solves it again on the CPU."""


def leave_gpu(sim, heard):
    """Everything after this solves on the CPU (EMerge's own choice, in this process and on this mesh). The factorisation is
    freed first: on a machine whose GPU shares its memory, it would otherwise sit under the CPU's. Returns what the GPU reached."""
    stalled = {"samples": heard.samples, "bestError": heard.best, "bestAt": heard.best_at,
               "unusable": heard.unusable > 0 or heard.residual is not None}
    if heard.residual is not None:
        stalled["residual"] = heard.residual
    report(stage="gpu-stalled", **stalled)
    routine = sim.mw.solveroutine
    routine.forced_solver = []
    if hasattr(routine, "set_device"):
        routine.set_device("CPU")
    keep_gpu_solver_off()
    routine.reset()
    heard.on_gpu = False
    return stalled


def watched(sim, heard, freqs, folder, attempt):
    """Runs attempt(stalled), the sweep and everything it writes; on the GPU, watched, and once more on the CPU the moment a solve
    fails its check or the sweep stalls. The whole sweep moves, whichever solve it was: one result comes from one device. Returns
    attempt's result."""
    if not heard.on_gpu:
        return attempt(heard.stalled)
    before = set(os.listdir(folder))
    heard.watch = True
    try:
        return attempt(None)
    except GpuStalled:
        stalled = leave_gpu(sim, heard)
    finally:
        heard.watch = False
    # The interrupted sweep left EMerge's frequency list at its last batch and its samples in the dataset, where the CPU's
    # fit would read them: the band is restored and the dataset emptied, so the CPU starts as a sweep does. A saved field's
    # solve comes after the arrays are written, so the GPU's go too: a CPU run that does not finish leaves none of them.
    sim.mw.set_frequency(list(freqs))
    sim.mw._state.data.reset()
    heard.error = heard.converged = None
    for name in set(os.listdir(folder)) - before:
        if name.endswith(".npy"):
            os.remove(os.path.join(folder, name))
    return attempt(stalled)


class Heard:
    """What EMerge's own log said that the run reports: the direct solver it called and the adaptive sweep's last
    estimate. Read from the message text EMerge logs (3.0.0b2 to 3.0.0); a line it no longer logs leaves the field unreported."""

    def __init__(self, on_gpu=False):
        self.solver = None
        self.error = None
        self.converged = None
        self.on_gpu = on_gpu
        self.watch = False
        self.samples = 0
        self.best = None
        self.best_at = 0
        self.unusable = 0
        self.residual = None
        self.stalled = None

    def sink(self, message):
        import re
        text = message.record["message"]
        if self.on_gpu and text.startswith(GPU_UNUSABLE):
            self.unusable += 1
            if self.watch:
                raise GpuStalled()
        m = re.search(r"Calling (\S+(?: \S+)?) Solver", text)
        if m:
            self.solver = m.group(1)
        m = re.search(r"estimated error ([0-9.eE+-]+)", text)
        if m:
            self.error = float(m.group(1))
        m = re.match(r"Adaptive sweep: (\d+) samples, estimated error ([0-9.eE+-]+)", text)
        if m and self.watch:
            self.samples = int(m.group(1))
            if self.best is None or self.error < self.best:
                self.best, self.best_at = self.error, self.samples
            elif self.samples - self.best_at >= GPU_STALL_SAMPLES:
                raise GpuStalled()
        if text.startswith("Adaptive sweep converged"):
            self.converged = True
        elif text.startswith("Adaptive sweep stopped at max_samples"):
            self.converged = False


# -- a non-finite solve (brief-dfr28-3 R-dfr28-3-1) ----------------------------------------------------------------------

# The refusal's cause, on its JSON line: circuitRF forwards it as a solve's refusal, not a terminal face's.
NON_FINITE = "non-finite-solve"


def guard_solves(sim, model, heard):
    """Every direct solve EMerge makes is checked: a matrix or right-hand side holding a non-finite entry stops the run
    before it is solved, and a solution that is not finite stops it after. EMerge 3.0.0b2 does neither: on the TP149 board
    one degenerate second-order tetrahedron put NaN in its 20 unknowns' columns of A, the fast sparse solver returned
    x = NaN and reported itself converged (EMerge drops that report anyway), and every frequency's S came out exactly -I.
    SolveRoutine.solve is the one call every solver EMerge may pick goes through, serial or threaded, so the check sits
    there. A run whose EMerge has moved it is told so and runs, with the -I check circuitRF makes on its result."""
    import numpy as np
    try:
        try:
            from emerge._emerge.solveroutine import SolveRoutine   # EMerge 3.0
        except ImportError:
            from emerge._emerge.solver import SolveRoutine
        solve_ = SolveRoutine.solve
    except (ImportError, AttributeError):
        return "circuitRF could not install its check on EMerge's solves (this EMerge has no SolveRoutine.solve where " \
               "circuitRF looks for it); the run goes on without it."

    def solve(self, A, b, solve_ids, *a, **k):
        finite_a, finite_b = bool(np.isfinite(A.data).all()), bool(np.isfinite(b).all())
        if not (finite_a and finite_b):
            c = A.tocoo()
            bad = ~np.isfinite(c.data)
            dofs = np.unique(np.concatenate([c.row[bad], c.col[bad],
                                             np.flatnonzero(~np.isfinite(np.reshape(b, (b.shape[0], -1))).all(axis=1))]))
            raise Refused(non_finite_matrix(sim, model, dofs, int(bad.sum()) + int((~np.isfinite(b)).sum())), NON_FINITE)
        x, report_ = solve_(self, A, b, solve_ids, *a, **k)
        if not np.isfinite(x).all():
            raise Refused("EMerge's sparse solver (%s) returned a solution that is not a number from a matrix that is finite, "
                          "and EMerge took it as solved; no S-parameters were written. Its system is singular or the "
                          "solver failed without saying why. Run it again on the other device (EMerge's Device), or "
                          "with EMerge.ElementOrder: 1." % (heard.solver or "unnamed"), NON_FINITE)
        if heard.on_gpu:
            worst = gpu_residual(A, b, x, solve_ids)
            if worst > GPU_RESIDUAL_LIMIT:
                heard.residual = worst
            if heard.unusable or heard.residual is not None:
                if heard.watch:
                    raise GpuStalled()
                # A mesh refinement's pass, which cannot be restarted: this system goes to the CPU, and so does the rest.
                heard.stalled = leave_gpu(sim, heard)
                return solve(self, A, b, solve_ids, *a, **k)
        return x, report_

    SolveRoutine.solve = solve
    return None


def gpu_residual(A, b, x, solve_ids):
    """The worst right-hand side's ||Ax - b|| / ||b|| over the unknowns EMerge solved for."""
    import numpy as np
    solved = A[:, solve_ids][solve_ids, :]
    rhs = np.reshape(b, x.shape)[solve_ids, :]
    worst = 0.0
    for j in range(x.shape[1]):
        size = np.linalg.norm(rhs[:, j])
        if size > 0:
            worst = max(worst, float(np.linalg.norm(solved @ x[solve_ids, j] - rhs[:, j]) / size))
    return worst


def non_finite_matrix(sim, model, dofs, entries):
    """The refusal for a system matrix holding a non-finite entry: the tetrahedra whose unknowns those are, where they are
    (centroid in mm, in circuitRF's layout coordinates, as the run's other notes give them) and what solids they lie in or
    touch. One degenerate element puts a non-finite value in every one of its own unknowns, and only it has all of them."""
    import numpy as np
    mesh = sim.mesh
    t2f = np.asarray(sim.mw.basis.tet_to_field)              # (unknowns per tetrahedron, tetrahedra)
    hits = np.isin(t2f, dofs).sum(axis=0)
    cells = np.flatnonzero(hits == t2f.shape[0])
    if cells.size == 0 and hits.max() > 0:
        cells = np.flatnonzero(hits == hits.max())
    if cells.size == 0:
        return ("EMerge's system holds %d entr%s that are not numbers, in %d unknown%s no element of its mesh owns; the "
                "run was stopped before solving." % (entries, "y" if entries == 1 else "ies", dofs.size,
                                                     "" if dofs.size == 1 else "s"))
    obj, _ = cell_owners(mesh, model)
    names = model["names"]
    where = []
    for t in cells[:3]:
        corners = mesh.nodes[:, mesh.tets[:, t]]               # (3, 4), metres
        c = corners.mean(axis=1) * 1e3
        edges = [np.linalg.norm(corners[:, i] - corners[:, j]) for i, j in TET_EDGES]
        volume = abs(np.linalg.det(corners[:, 1:] - corners[:, :1])) / 6
        touching = np.flatnonzero(np.isin(mesh.tets, mesh.tets[:, t]).any(axis=0))
        around = sorted({names[k] for k in obj[touching]} - {names[obj[t]]})
        where.append("a tetrahedron at (%.4f, %.4f, %.4f) mm, in %s%s; its edges run %.3g to %.3g um and its volume is "
                     "%.3g um^3" % (c[0], c[1], c[2], quoted(names[obj[t]]),
                                    ", touching " + ", ".join(quoted(n) for n in around) if around else "",
                                    min(edges) * 1e6, max(edges) * 1e6, volume * 1e18))
    more = "" if cells.size <= 3 else ", and %d more" % (cells.size - 3)
    return ("%s: %s%s. Its entries in EMerge's system are not numbers and reach every unknown, so the solve would return "
            "no field (S = -I); the run was stopped before solving. Look at the geometry there: a sliver between two "
            "solids' faces, or a feature far smaller than the mesh around it." % (
                "The mesh has %d degenerate element%s" % (cells.size, "" if cells.size == 1 else "s"),
                "; ".join(where), more))


def quoted(name):
    return "the air box" if name == "background" else "'%s'" % name


# -- geometry ------------------------------------------------------------------------------------------------------------

# brief-dfr27-4: OpenCASCADE's tolerances are absolute (1e-7 in model units). EMerge's model is in metres, where that is
# 0.1 um, and a board's own artwork sits a few tenths of a micrometre from it: a plane cropped at the board edge left an
# edge 0.39 um long leaving the edge at an angle, a pad's polygon grazed the edge 0.24 um short of touching it. The fragment
# then left pieces unsplit, Gmsh's remove_all_duplicates split them after EMerge had taken its map, and the build stopped
# on a region the map named and the model no longer had; and once that was fixed, Gmsh's boundary recovery found a segment
# crossing a facet in metres where the same model meshed in micrometres. So the model is built, fragmented and meshed in
# MICROMETRES, as circuitRF's Gmsh script for Palace is: EMerge's own scale factor (Simulation.set_scale_factor) scales
# the mesh to metres once it is made, and everything EMerge reads from it is in metres as it always was. What the driver
# hands Gmsh or EMerge's mesher itself -- a coordinate, a size -- is in model units, so it is multiplied by MODEL_SCALE.
MODEL_SCALE = 1e6        # model units per metre


def points(gmsh, ring):
    k = MODEL_SCALE
    return [gmsh.model.occ.addPoint(p[0] * k, p[1] * k, p[2] * k) for p in ring]


def polygon_loop(gmsh, ring):
    tags = points(gmsh, ring)
    lines = [gmsh.model.occ.addLine(tags[k], tags[(k + 1) % len(tags)]) for k in range(len(tags))]
    return gmsh.model.occ.addCurveLoop(lines)


def planar(gmsh, rings):
    """A plane region: the first ring the outline, the rest holes."""
    return gmsh.model.occ.addPlaneSurface([polygon_loop(gmsh, r) for r in rings])


def rectangle(gmsh, origin, u, v):
    o = origin
    ring = [o, [o[0] + u[0], o[1] + u[1], o[2] + u[2]],
            [o[0] + u[0] + v[0], o[1] + u[1] + v[1], o[2] + u[2] + v[2]], [o[0] + v[0], o[1] + v[1], o[2] + v[2]]]
    return planar(gmsh, [ring])


def volumes_of(dimtags):
    return [t for d, t in dimtags if d == 3]


# -- what a tag is, in the designer's words (brief-dfr27-4) ---------------------------------------------------------------

# Each fragment's (dim, tag) -> the words for the objects it was cut from, filled when the model is committed. A Gmsh error
# naming a tag ("Unknown model region with tag 398") is reported with them: the tag alone tells a designer nothing.
FRAGMENT_OF = {}


def solid_words(gmsh, s, problem, tag):
    """A solid as a designer finds it: its name (a layout solid's starts with its layer), its material and where it is."""
    box = [round(c * 1e6 / MODEL_SCALE, 3) + 0.0 for c in gmsh.model.occ.getBoundingBox(3, tag)]   # to the nm; never -0
    span = ", ".join("%s %.6g to %.6g µm" % (axis, box[k], box[k + 3]) for k, axis in enumerate("xyz"))
    return "'%s' (%s, %s)" % (s["name"], problem["materials"][s["material"]]["name"], span)


def commit_whole(sim, gmsh, described):
    """EMerge's commit_geometry, held to the map it takes. EMerge fragments the model, keeps the fragment's map from each
    object to its pieces, then calls Gmsh's remove_all_duplicates -- which fragments everything once more and renumbers
    whatever it splits, with no map. When that second pass changes a piece, EMerge's
    map names an entity that is gone and the build stops later on a bare tag; here it stops at once, naming the objects
    the kernel could not fragment."""
    occ = gmsh.model.occ
    fragment, dedupe = occ.fragment, occ.remove_all_duplicates

    def fragment_(objects, tools, *a, **k):
        out, maps = fragment(objects, tools, *a, **k)
        FRAGMENT_OF.clear()
        for dimtag, pieces in zip(list(objects) + list(tools), maps):
            for piece in pieces:
                FRAGMENT_OF.setdefault(tuple(piece), []).append(described.get(tuple(dimtag), "an unnamed object"))
        return out, maps

    def dedupe_(*a, **k):
        occ.synchronize()
        before = set(gmsh.model.getEntities(3)) | set(gmsh.model.getEntities(2))
        dedupe(*a, **k)
        occ.synchronize()
        lost = sorted(t for t in before - set(gmsh.model.getEntities(3)) - set(gmsh.model.getEntities(2)) if t in FRAGMENT_OF)
        if lost:
            words = sorted({w for t in lost for w in FRAGMENT_OF[t]})
            raise GeometryNotFragmented(
                "the geometry kernel could not fragment %s cleanly: a second pass split %s again, so EMerge's map of the "
                "model's regions no longer holds. Faces, edges or vertices nearly but not quite touching are the usual "
                "cause." % (" and ".join(words), "it" if len(words) == 1 else "them"))

    occ.fragment, occ.remove_all_duplicates = fragment_, dedupe_
    try:
        sim.commit_geometry()
    finally:
        occ.fragment, occ.remove_all_duplicates = fragment, dedupe


class GeometryNotFragmented(Exception):
    pass


UNKNOWN_TAG = r"Unknown model (region|face|curve|point) with tag (\d+)"
DIM_OF = {"region": 3, "face": 2, "curve": 1, "point": 0}


def named(e):
    """A build's exception as the run reports it: a Gmsh error naming a tag also names what that tag was cut from."""
    text = "%s: %s" % (type(e).__name__, e)
    m = re.search(UNKNOWN_TAG, str(e))
    words = FRAGMENT_OF.get((DIM_OF[m.group(1)], int(m.group(2)))) if m else None
    return text + (" -- a piece of %s" % " and ".join(sorted(set(words))) if words else "")


def box_of(gmsh, lo, hi):
    """A box from its corners in metres, built in model units."""
    k = MODEL_SCALE
    return gmsh.model.occ.addBox(lo[0] * k, lo[1] * k, lo[2] * k, (hi[0] - lo[0]) * k, (hi[1] - lo[1]) * k, (hi[2] - lo[2]) * k)


def shape(gmsh, s, folder):
    """The OCC volume tags of one solid's shape, in model units (MODEL_SCALE)."""
    occ = gmsh.model.occ
    kind = s["kind"]
    k = MODEL_SCALE
    if kind == "box":
        return [box_of(gmsh, s["min"], s["max"])]
    if kind == "cylinder":
        p, a = [c * k for c in s["start"]], [c * k for c in s["axis"]]
        return [occ.addCylinder(p[0], p[1], p[2], a[0], a[1], a[2], s["radius"] * k)]
    if kind == "sphere":
        c = [x * k for x in s["center"]]
        return [occ.addSphere(c[0], c[1], c[2], s["radius"] * k)]
    if kind == "truncatedSphere":
        c = [x * k for x in s["center"]]
        ball = occ.addSphere(c[0], c[1], c[2], s["radius"] * k)
        clip = box_of(gmsh, s["clipMin"], s["clipMax"])
        kept, _ = occ.intersect([(3, ball)], [(3, clip)])
        return volumes_of(kept)
    if kind == "ellipsoid":
        c, (a, b, cc) = [x * k for x in s["center"]], [x * k for x in s["semiAxes"]]
        v = occ.addSphere(0, 0, 0, 1)
        occ.dilate([(3, v)], 0, 0, 0, a, b, cc)
        if "rotationAxis" in s:
            ax = s["rotationAxis"]
            occ.rotate([(3, v)], 0, 0, 0, ax[0], ax[1], ax[2], s["rotationAngle"])
        occ.translate([(3, v)], c[0], c[1], c[2])
        return [v]
    if kind == "extrusion":
        base = planar(gmsh, s["rings"])
        return volumes_of(occ.extrude([(2, base)], 0, 0, s["height"] * k))
    if kind == "sweep":
        wires = []
        for ring in s["rings"]:
            tags = points(gmsh, ring)
            if s["round"]:
                curves = [occ.addSpline(tags + [tags[0]])]
            else:
                curves = [occ.addLine(tags[k], tags[(k + 1) % len(tags)]) for k in range(len(tags))]
            wires.append(occ.addWire(curves))
        return volumes_of(occ.addThruSections(wires, makeSolid=True, makeRuled=True))
    if kind == "polyhedron":
        tags = points(gmsh, s["vertices"])
        edges = {}

        def edge(a, b):
            key = (min(a, b), max(a, b))
            if key not in edges:
                edges[key] = occ.addLine(tags[key[0]], tags[key[1]])
            return edges[key] if a < b else -edges[key]

        faces = []
        for rings in s["faces"]:
            loops = [occ.addCurveLoop([edge(r[k], r[(k + 1) % len(r)]) for k in range(len(r))]) for r in rings]
            faces.append(occ.addPlaneSurface(loops))
        return [occ.addVolume([occ.addSurfaceLoop(faces)])]
    if kind == "brep":
        imported = occ.importShapes(os.path.join(folder, s["file"]), highestDimOnly=True)
        scale = s["scale"] * k     # a kernel B-rep is in micrometres, which is what model units are: usually 1
        if scale != 1:
            occ.dilate(imported, 0, 0, 0, scale, scale, scale)
        return volumes_of(imported)
    raise ValueError("unknown shape kind")


def material(em, m):
    if m["pec"]:
        return em.lib.PEC
    er = m["erTensor"] if "erTensor" in m else m["er"]
    if isinstance(er, list):
        import numpy as np
        er = np.diag(er)
    return em.Material(er=er, ur=m["ur"], tand=m["tand"], cond=m["cond"], name=m["name"])


def impedance(element):
    """A lumped element's Z(f), from its R, L and C."""
    import math
    r, l, c, series = element["r"], element["l"], element["c"], element["topology"] == "series"

    def z(f):
        w = 2 * math.pi * f
        if series:
            return (r or 0) + (1j * w * l if l is not None else 0) + (1 / (1j * w * c) if c is not None else 0)
        y = (1 / r if r is not None else 0) + (1 / (1j * w * l) if l is not None else 0) + (1j * w * c if c is not None else 0)
        return 1 / y

    return z


# -- the model -----------------------------------------------------------------------------------------------------------

def build(problem, folder, em, gmsh, GeoVolume, GeoSurface):
    from emerge._emerge.attributes import FiniteThickness
    sim = em.Simulation("circuitrf")
    sim.set_scale_factor(MODEL_SCALE)
    if problem["wavePorts"]:
        # brief-emw-2: EMerge's S post-processing caps |S_ij| at 1 and power-normalises columns, which changes the modal S a
        # wave port's renormalisation then reads (R-emw1-1). A lumped-only run keeps EMerge's defaults, as it always has.
        sim.settings.mw_cap_sp_single = False
        sim.settings.mw_cap_sp_col = False
    occ = gmsh.model.occ
    settings = problem["settings"]
    mats = [material(em, m) for m in problem["materials"]]

    box = problem["box"]
    lo, hi = box["min"], box["max"]
    air = GeoVolume(box_of(gmsh, lo, hi), name="air box")
    air.set_material(mats[box["material"]]).prio_set(0)
    owners = [(air, 0, "air", box["material"])]
    names = ["background"]
    described = {(3, t): "the air box" for t in air.tags}
    for s in problem["solids"]:
        v = GeoVolume(shape(gmsh, s["shape"], folder), name=s["name"])
        v.set_material(mats[s["material"]]).prio_set(s["priority"])
        owners.append((v, s["priority"], s["role"], s["material"]))
        names.append(s["name"])
        described.update({(3, t): solid_words(gmsh, s, problem, t) for t in v.tags})

    sheets = []
    for sh in problem["sheets"]:
        g = GeoSurface(planar(gmsh, sh["rings"]), name=sh["name"])
        g.set_material(mats[sh["material"]])
        if not problem["materials"][sh["material"]]["pec"]:
            # EMerge 3.0.0b2's GeoSurface.assign_thickness calls a _store it does not have; the property it stores is
            # FiniteThickness, which EMerge's own PCB builder adds this way and its boundary conditions read.
            g.properties += FiniteThickness(sh["thicknessM"])
        sheets.append((sh, g))
    ports = [(p, GeoSurface(rectangle(gmsh, p["origin"], p["u"], p["v"]), name=p["name"])) for p in problem["ports"]]
    elements = [(e, GeoSurface(rectangle(gmsh, e["origin"], e["u"], e["v"]), name=e["name"]))
                for e in problem["lumpedElements"]]
    # brief-emw-2: a wave port's region is a surface of its own on the air box's face, built as a lumped port's plate is;
    # commit_geometry fragments it into the face.
    regions = [(w, GeoSurface(rectangle(gmsh, w["origin"], w["u"], w["v"]), name=w["name"])) for w in problem["wavePorts"]]
    for entry, g in sheets + ports + elements + regions:
        described.update({(2, t): "'%s'" % entry["name"] for t in g.tags})

    commit_whole(sim, gmsh, described)

    # Conductors are voids, as in Palace's mesh: EMerge has already fragmented the model and resolved every overlap by
    # priority, so each fragment has one owner. Every fragment a conductor owns is removed before meshing, and the faces
    # it shares with meshed space carry its metal (a perfect conductor, or its surface impedance). Without this, Gmsh fills
    # every conductor with tetrahedra EMerge then discards, and a conductor overlapping another leaves the overlap's
    # slivers, curves and points to mesh (the 3D Connector's pin, half sunk into its 35 um trace, never finished).
    gmsh.model.occ.synchronize()
    owner = {}
    for k, (v, priority, role, mat) in enumerate(owners):
        for t in v.tags:
            if t not in owner or priority >= owner[t][0]:
                owner[t] = (priority, role, mat, k)
    metal = {t for t, (_, role, _, _) in owner.items() if role == "conductor"}
    walls, plated = {}, {}
    for _, tag in gmsh.model.getEntities(2):
        up, _ = gmsh.model.getAdjacencies(2, tag)
        in_metal = [v for v in up if v in metal]
        if in_metal and any(v not in metal for v in up):
            _, _, mat, k = owner[in_metal[0]]
            walls.setdefault(mat, []).append(tag)
            plated.setdefault(k, []).append(tag)    # brief-emi-11: and the conductor it belongs to, for its surface current
    # Recursive, as Palace's script deletes its conductors: whatever bounded only metal goes too, down to its curves and
    # points, and whatever still bounds meshed space stays.
    gmsh.model.occ.remove([(3, t) for t in sorted(metal)], recursive=True)
    gmsh.model.occ.synchronize()
    for v, _, _, _ in owners:
        v.tags = [t for t in v.tags if t not in metal]
    # brief-emw-2: a port region's fragments over a conductor's face bounded only metal and went with it (the Launch's square
    # over its housing), so the region is what is left: the part over meshed space.
    left = {t for _, t in gmsh.model.getEntities(2)}
    for _, g in regions:
        g.tags = [t for t in g.tags if t in left]
    # commit_geometry has already cached every object's boundary faces and generated EMerge's default boundary conditions
    # from them: a removed conductor would keep a surface impedance on faces that no longer exist, and the solve stops on
    # the first. So the cache is taken again, as commit_geometry takes it (a removed conductor has no boundary left), and
    # the defaults are cleared; EMerge generates them afresh when it solves, under the conditions set below.
    for g in sim.all_geos():
        g._cache_gmsh()
    sim.mw.reset()

    sim.mw.set_basis_space(element_order=settings["elementOrder"])
    sim.mw.set_resolution(settings["resolution"])
    sim.mw.set_frequency(problem["frequenciesHz"] if problem["problem"] == "driven" else [problem["sizingHz"]])

    # Refinement along the edges of every sheet and every conductor's faces.
    k = MODEL_SCALE
    edge = settings["edgeSizeM"] * k
    for _, g in sheets:
        sim.mesher.set_boundary_size(g, edge)
    for tags in walls.values():
        sim.mesher.set_boundary_size(em.FaceSelection(tags), edge)
    for region in problem["meshRegions"]:
        rlo, rhi = region["min"], region["max"]
        field = gmsh.model.mesh.field.add("Box")
        for key, value in (("VIn", region["sizeM"]), ("VOut", settings["largestSizeM"]), ("Thickness", region["thicknessM"]),
                           ("XMin", rlo[0]), ("XMax", rhi[0]), ("YMin", rlo[1]), ("YMax", rhi[1]), ("ZMin", rlo[2]), ("ZMax", rhi[2])):
            gmsh.model.mesh.field.setNumber(field, key, value * k)
        sim.mesher.mesh_fields.append(field)

    # brief-emw-2: a wave port's region meshed as EMerge meshes a port face, to about PORT_TRIANGLES triangles (its
    # _size_constraint, sqrt(area / N * 4 / sqrt 3); the area and the size both in model units). Left to the volume's size,
    # a small region on a coarse mesh had too few triangles for its mode solve, and EMerge's dense eigensolver failed on
    # it (LinAlgError from ggev; RESOLVED, EMW-2).
    for _, g in regions:
        area = sum(gmsh.model.occ.getMass(2, t) for t in g.tags)
        if area > 0:
            sim.mesher.set_face_size(g, (area / PORT_TRIANGLES * 4 / 3 ** 0.5) ** 0.5)

    # The air box's faces: every outer face of meshed space on a box plane. PEC is EMerge's own default for an outer face,
    # so only PMC and absorbing faces are stated.
    size = max(hi[a] - lo[a] for a in range(3)) * k
    tol = 1e-9 * size
    planes = {"xmin": (0, lo[0] * k), "xmax": (0, hi[0] * k), "ymin": (1, lo[1] * k), "ymax": (1, hi[1] * k),
              "zmin": (2, lo[2] * k), "zmax": (2, hi[2] * k)}
    faces = {key: [] for key in planes}
    # A port region's fragments are its port, never the face's boundary: a region left PEC is a short across the port.
    in_region = {t for _, g in regions for t in g.tags}
    for _, tag in gmsh.model.getEntities(2):
        if tag in in_region:
            continue
        up, _ = gmsh.model.getAdjacencies(2, tag)
        if len(up) != 1:
            continue
        c = gmsh.model.occ.getCenterOfMass(2, tag)
        for key, (axis, at) in planes.items():
            if abs(c[axis] - at) <= tol:
                faces[key].append(tag)

    # A floor under every element size, a thousandth of the conductor-edge size. EMerge sizes curved edges by their
    # curvature, and on an imported B-rep's trimmed curve that estimate can be degenerate: the 3D Connector's pin, cut
    # by its trace, asked Gmsh for elements of essentially zero length and took gigabytes in the 1D mesh before being
    # stopped -- in metres, before brief-dfr27-4 put the model in micrometres; the floor stays. No real feature is near
    # the floor, and the launch's mesh is the same from edge/10 000 to edge/10.
    gmsh.option.setNumber("Mesh.MeshSizeMin", edge / 1000)
    sim.generate_mesh()

    for mat, tags in walls.items():
        if problem["materials"][mat]["pec"]:
            sim.mw.bc.PEC(em.FaceSelection(tags))
        else:
            sim.mw.bc.SurfaceImpedance(em.FaceSelection(tags), mats[mat])
    for key, kind in box["faces"].items():
        if not faces[key] or kind == "pec":
            continue
        selection = em.FaceSelection(faces[key])
        if kind == "pmc":
            sim.mw.bc.PMC(selection)
        elif kind == "absorbing":
            sim.mw.bc.AbsorbingBoundary(selection)
    numbers = emerge_numbers(problem)
    lumped = []
    for p, plate in ports:
        lumped.append((p, sim.mw.bc.LumpedPort(plate, numbers[p["number"]], width=p["widthM"], height=p["heightM"],
                                               direction=tuple(p["direction"]), Z0=p["z0"])))
    for e, plate in elements:
        sim.mw.bc.LumpedElement(plate, impedance(e), width=e["widthM"], height=e["heightM"])
    waves = []
    for w, region in regions:
        if "terminals" in w:
            waves.append((w, terminal_port(sim, w, region, numbers[w["number"]])))
            continue
        # EMerge 3.0 has WavePort, with modetype="TEM" what its deprecated WavePortIH is; before it there is only WavePortIH.
        conditions = sim.mw.bc
        wave = getattr(conditions, "WavePort", None)
        bc = wave(region, numbers[w["number"]], modetype="TEM") if wave is not None else conditions.WavePortIH(region, numbers[w["number"]])
        # R-emw2-2a — the one line that decides a wave port's polarity: EMerge turns the mode so its integral along this line
        # is positive, and circuitRF's path runs from the negative object to the positive one, the sense a lumped port's
        # direction has. Proved on a thru with a wave port at one end and a lumped port at the other (RESOLVED, EMW-2).
        bc.set_integration_line(tuple(w["path"]["from"]), tuple(w["path"]["to"]), 101)
        # Either keeps mixed_materials True, and it must: its dense block and its excitation are built from the stored
        # mode's own fields and beta (get_modepf_kappa), never rescaled to k0, so the mode is solved at every frequency.
        # Overriding it on a face of one material gave S right only at the sweep's centre (src/Design/RESOLVED.md, EMW-2).
        waves.append((w, bc))

    mesh = sim.mesh
    # brief-emi-7 — what the fields need of the model: each meshed volume's owner (circuitRF's object, by index into
    # names, and its material), the lumped ports' EMerge objects, and the absorbing faces a far field is integrated over.
    volumes = {t: (k, mat) for t, (_, _, mat, k) in owner.items() if t not in metal}
    absorbing = [tag for key, kind in box["faces"].items() if kind == "absorbing" for tag in faces[key]]
    # brief-emi-11 — the faces a surface current is written on, grouped as Palace's mesh groups them: each conductor's walls
    # and each sheet, with its +n. A sheet's face tags are read once meshed (EMerge's own, after fragmenting). Ports, lumped
    # elements and the air box's own faces are not among them, as on a Palace run.
    surfaces = [{"kind": "Conductor", "name": names[k], "material": owners[k][3], "tags": (lambda t=tags: t), "normal": None}
                for k, tags in sorted(plated.items())]
    surfaces += [{"kind": "Sheet", "name": sh["name"], "material": sh["material"], "thicknessM": sh["thicknessM"],
                  "tags": (lambda g=g: list(g.tags)), "normal": sh["normal"]} for sh, g in sheets]
    model = {"names": names, "volumes": volumes, "ports": lumped + waves, "waves": waves, "absorbing": absorbing,
             "surfaces": surfaces}
    return sim, {"tets": int(mesh.n_tets), "edges": int(mesh.n_edges), "triangles": int(mesh.n_tris),
                 "faces": {key: len(tags) for key, tags in faces.items()}, "conductorFaces": sum(len(t) for t in walls.values())}, model


# -- the solve (brief-emi-5) ----------------------------------------------------------------------------------------------

def solve(problem, folder, sim, model, heard, warnings):
    """Solves the built model and writes its arrays into folder; returns run.json's solve fields."""
    import numpy as np
    settings = problem["settings"]
    if problem["problem"] == "eigenmode":
        if settings["adaptiveMesh"] > 0:
            warnings.append("EMerge refines a mesh on a driven solve, so this eigenmode run used the initial mesh.")
        eig = problem["eigenmode"]
        data = sim.mw.eigenmode(search_frequency=eig["targetHz"], nmodes=eig["count"])
        entries = list(data.scalar.iter())
        np.save(os.path.join(folder, "modes.npy"), np.array([complex(e.freq) for e in entries], dtype=np.complex128))
        np.save(os.path.join(folder, "Q.npy"), np.array([float(np.real(e.Q)) for e in entries], dtype=np.float64))
        out = {"modes": len(entries)}
        if problem.get("fields") is not None:
            out.update(mode_fields(problem, folder, sim, model, list(data.field.iter())))
        return out

    if settings["adaptiveMesh"] > 0:
        # Up to adaptiveMesh refinements: EMerge counts solve passes, the first on the initial mesh. Held (brief-sdl-8 D7),
        # every pass is made: no run of converged passes can be as long as the passes there are.
        if settings.get("holdMesh"):
            sim.adaptive_mesh_refinement(max_steps=settings["adaptiveMesh"] + 1,
                                         min_refined_passes=settings["adaptiveMesh"] + 2)
        else:
            sim.adaptive_mesh_refinement(max_steps=settings["adaptiveMesh"] + 1)
        if heard.on_gpu and heard.unusable:     # no check on EMerge's solves was installed, so nothing has moved it yet
            heard.stalled = leave_gpu(sim, heard)
    freqs = np.array(problem["frequenciesHz"], dtype=np.float64)
    sim.mw.set_frequency(list(freqs))
    adaptive = settings["adaptiveSweep"] and len(freqs) > settings["initialSamples"]
    groups = [(w, bc) for w, bc in model["waves"] if "terminals" in w]
    for w, bc in groups:
        if not w["mixed"]:
            presolve(sim, w, bc, float(freqs[len(freqs) // 2]))
    return watched(sim, heard, freqs, folder,
                   lambda stalled: sweep(problem, folder, sim, model, heard, freqs, adaptive, groups, stalled))


def sweep(problem, folder, sim, model, heard, freqs, adaptive, groups, stalled):
    """The driven sweep, its arrays and its saved fields; returns run.json's solve fields."""
    import numpy as np
    settings = problem["settings"]
    if adaptive:
        data = sim.mw.run_adaptive_sweep(n_initial=settings["initialSamples"], tol=settings["tolerance"],
                                         max_samples=settings["maxSamples"])
    else:
        data = sim.mw.run_sweep()
    grid = data.scalar.grid
    keys = entry_keys(problem)
    every = sorted(keys)
    index = [grid._portmap[keys[n]] for n in every]
    full = grid.model_Smat(freqs) if adaptive else grid.Smat
    S = np.asarray(full)[:, index, :][:, :, index].astype(np.complex128)
    if not adaptive and not np.allclose(np.ravel(grid.freq), freqs, rtol=1e-12, atol=0):
        raise ValueError("EMerge solved at frequencies other than the requested ones")
    stated = np.asarray(grid.Z0).reshape(-1, len(grid._portmap))[0][index].astype(np.complex128)
    z0 = np.tile(stated, (len(freqs), 1))
    gamma = np.zeros((len(freqs), len(every)), dtype=np.complex128)
    waves = model["waves"]
    if waves:
        wz, wg = wave_modes(sim, [(w, bc) for w, bc in waves if "terminals" not in w], freqs, adaptive)
        for j, (w, _) in enumerate([(w, bc) for w, bc in waves if "terminals" not in w]):
            z0[:, every.index(w["number"])] = wz[:, j]
            gamma[:, every.index(w["number"])] = wg[:, j]
    if groups:
        # brief-emw-3 R-emw3-4 — every wave-port entry's mode data, the conversion's input; and V where the run solved directly
        arrays = terminal_arrays(sim, waves, every, freqs, adaptive)
        for name, a in arrays.items():
            np.save(os.path.join(folder, name + ".npy"), np.ascontiguousarray(a))
        rows = [every.index(n) for w, _ in waves for n in entry_numbers(w)]
        for i in rows:
            z0[:, i] = np.abs(arrays["TV"][:, i, i]) ** 2 / 2
            gamma[:, i] = arrays["gammaEntries"][:, i]
        os.remove(os.path.join(folder, "gammaEntries.npy"))
        if not adaptive:
            np.save(os.path.join(folder, "V.npy"), np.ascontiguousarray(measured_v(data, waves, every, keys, freqs)))
    # C order, which circuitRF reads: S is indexed out of EMerge's matrix, and numpy keeps such a view in Fortran order.
    np.save(os.path.join(folder, "freq.npy"), np.ascontiguousarray(freqs))
    np.save(os.path.join(folder, "S.npy"), np.ascontiguousarray(S))
    np.save(os.path.join(folder, "Z0.npy"), np.ascontiguousarray(z0))
    np.save(os.path.join(folder, "gamma.npy"), np.ascontiguousarray(gamma))
    out = {"samples": int(np.size(grid.freq)), "adaptive": bool(adaptive)}
    if stalled is not None:
        out["gpuStalled"] = stalled
    if waves:
        out["secondModes"] = second_modes(sim, waves, float(np.max(freqs)))
    if groups:
        out["terminalModes"] = [{"port": w["number"], "modes": len(w["terminals"]),
                                 "presolved": not w["mixed"]} for w, _ in groups]
    if adaptive:
        out["error"] = heard.error
        out["converged"] = heard.converged
    if problem.get("fields") is not None and problem["fields"]["frequenciesHz"]:
        out.update(driven_fields(problem, folder, sim, model, [] if adaptive else list(data.field.iter())))
    return out


# -- wave ports (brief-emw-2) ---------------------------------------------------------------------------------------------

def wave_modes(sim, waves, freqs, adaptive):
    """Each wave port's mode impedance Z0 (EMerge's own, on circuitRF's voltage path: Z_PV, R-emw1-5) and propagation
    constant alpha + j beta at every requested frequency, [f, wave port]. Never read from the result's port properties, which
    repeat mode 1's for every mode (R-emw1-1). A point sweep solved every port's mode at every frequency. An adaptive sweep's
    requested frequencies had no mode solve of their own: a face of several materials is solved at each here, after it (the
    guard keeps each solve fresh); a face of one material needs none, its impedance being constant and beta scaling with k0
    for a TEM mode (EMerge's get_beta), so the nearest stored mode serves."""
    import numpy as np
    z = np.zeros((len(freqs), len(waves)), dtype=np.complex128)
    g = np.zeros((len(freqs), len(waves)), dtype=np.complex128)
    for j, (w, bc) in enumerate(waves):
        for i, f in enumerate(freqs):
            k0 = 2 * np.pi * f / C0
            if w["mixed"] and adaptive:
                sim.mw.modal_analysis(bc, 1, direct=False, TEM=True, freq=float(f))
            z[i, j] = complex(bc.get_modes(k0)[0].Z0)
            # fields go as exp(-j beta z), so a lossy mode's beta is beta' - j alpha and j beta = alpha + j beta'
            g[i, j] = 1j * complex(bc.get_beta(k0))
    return z, g


def second_modes(sim, waves, top):
    """R-emw2-3a — whether each wave port's face carries a second mode at the sweep's top: a mode solve asking for two, with
    the port's TEM-only filter lifted for it (a box mode is TE or TM). EMerge keeps only modes that carry power, so a second
    mode kept is a propagating one. The solve's own result is forgotten afterwards and the port's stored state restored."""
    import numpy as np
    k0 = 2 * np.pi * top / C0
    out = []
    for w, bc in waves:
        own = len(w.get("terminals", [None]))
        saved = bc.available_modes.pop(k0, None)
        state = (bc.forced_modetype, bc._last_k0, bc._first_k0, bc.initialized)
        try:
            bc.forced_modetype = None
            # brief-emw-3: a terminal face asks for its own modes and the next; a face of one material around its presolve shift
            sim.mw.modal_analysis(bc, own + (4 if "presolveNeff" in w else 1), direct=False, TEM=False, freq=top,
                                  target_neff=w.get("presolveNeff"))
            modes = sorted(bc.available_modes.get(k0, []), key=lambda m: -complex(m.beta).real)
        except Exception as e:
            out.append({"port": w["number"], "frequencyHz": top, "error": "%s: %s" % (type(e).__name__, e)})
            continue
        finally:
            bc.forced_modetype, bc._last_k0, bc._first_k0, bc.initialized = state
            bc.available_modes.pop(k0, None)
            if saved is not None:
                bc.available_modes[k0] = saved
        entry = {"port": w["number"], "frequencyHz": top, "modes": len(modes), "own": own,
                 "betas": [[complex(m.beta).real, complex(m.beta).imag] for m in modes],
                 "types": [getattr(m, "modetype", None) for m in modes]}
        out.append(entry)
    return out


def wave_conditions(sim, waves):
    """The mesh mode's account of each wave port: the condition EMerge holds for its region, and how many of the region's
    faces any PEC also claims once EMerge has generated its default conditions (a region left PEC is a short across it)."""
    try:
        sim.mw._autogenerate_bcs()
    except Exception:
        pass
    pec = set()
    for bc in sim.mw.bc.boundary_conditions:
        if type(bc).__name__ == "PEC":
            pec.update(bc.tags)
    return [{"number": w["number"], "condition": type(bc).__name__, "regionFaces": len(bc.tags),
             "pecFaces": len([t for t in bc.tags if t in pec]), "mixed": w["mixed"],
             "modePerFrequency": bool(bc.mixed_materials)} for w, bc in waves]


# -- terminal groups (brief-emw-3) ----------------------------------------------------------------------------------------

GAUSS_POINTS = 21        # R-emw1-4: 7, 21 and 41 points along a terminal's path move V by under 5e-5
SAME_SPEED = 1e-3        # two TEM modes whose Re beta agree to this are of one speed


def entry_numbers(w):
    """The circuitRF port numbers a wave-port entry carries: its terminals' in terminal order, or its own."""
    return [t["number"] for t in w["terminals"]] if "terminals" in w else [w["number"]]


def emerge_numbers(problem):
    """Each EMerge port's number, by the circuitRF number of its entry: EMerge requires them to run 1, 2, ... K, and a
    terminal group is one EMerge port carrying several circuitRF numbers, so EMerge's are the entries' ranks. Without a
    group they are circuitRF's own."""
    entries = sorted([p["number"] for p in problem["ports"]] + [w["number"] for w in problem["wavePorts"]])
    return {n: k + 1 for k, n in enumerate(entries)}


def entry_keys(problem):
    """Each circuitRF port number's index in EMerge's S: a lumped port's and a one-terminal wave port's EMerge number, and a
    terminal's <EMerge number>.<mode> (EMerge's p.m naming of a port's modes; nine at most, R-emw1-1)."""
    numbers = emerge_numbers(problem)
    keys = {p["number"]: numbers[p["number"]] for p in problem["ports"]}
    for w in problem["wavePorts"]:
        if "terminals" in w:
            for k, t in enumerate(w["terminals"]):
                keys[t["number"]] = float("%d.%d" % (numbers[w["number"]], k + 1))
        else:
            keys[w["number"]] = numbers[w["number"]]
    return keys


def terminal_port(sim, w, region, number):
    """A terminal group's ModalPort: one mode per terminal. A face of one material is made TEM-only, its modes solved once
    before the sweep (presolve), and not re-solved by it; a face of several materials is solved by EMerge at every frequency."""
    n = len(w["terminals"])
    bc = sim.mw.bc.ModalPort(region, number, number_of_modes=n, mixed_materials=bool(w["mixed"]),
                             modetype=None if w["mixed"] else "TEM")
    # Terminal 1's path: it sets the modes' signs (the conversion does not care which) and EMerge's own Z0's line.
    t = w["terminals"][0]["path"]
    bc.set_integration_line(tuple(t["from"]), tuple(t["to"]), 101)
    return bc


def refuse_modes(w, found, why):
    n = len(w["terminals"])
    raise Refused(("Port '%s''s face carries %d mode%s of one speed for %d terminals: " % (w["name"], found, "" if found == 1 else "s", n))
                     + why)


SPLIT = ("its reference is split into separate conductors on the face (two PEC faces with PMC between them are two "
         "conductors). Make the reference one conductor on the face, or run the setup on openEMS.")
MISSED = ("EMerge's mode solve found fewer modes than the face has terminals, so the terminals' S cannot be formed. Run the "
          "setup on openEMS.")


def presolve(sim, w, bc, freq):
    """R-emw3-4 — a face of one material: terminals + 4 modes around 0.9 of its index, before the sweep. Its TEM modes all
    travel at the first's speed; more of them than terminals means a split reference, fewer a missed mode. Exactly the
    terminals' count is kept, and the sweep does not solve the face again."""
    import numpy as np
    n = len(w["terminals"])
    sim.mw.modal_analysis(bc, nmodes=n + 4, direct=False, TEM=True, target_neff=w["presolveNeff"], freq=freq)
    k0 = 2 * np.pi * freq / C0
    modes = bc.get_modes(k0)
    same = [m for m in modes if m.modetype == "TEM" and abs(complex(m.beta).real - complex(modes[0].beta).real)
            <= SAME_SPEED * abs(complex(modes[0].beta).real)] if modes else []
    if len(same) > n:
        refuse_modes(w, len(same), SPLIT)
    if len(same) < n:
        refuse_modes(w, len(same), MISSED)
    bc.available_modes[min(bc.available_modes.keys(), key=lambda k: abs(k - k0))] = same


def checked_modes(w, bc, freq):
    """A terminal face's modes at freq, the terminals' count of them; a face of several materials refused unless EMerge's
    solve there gave that many, each TEM-classified (EMerge classes a quasi-TEM mode TEM)."""
    import numpy as np
    n = len(w["terminals"])
    k0 = 2 * np.pi * freq / C0
    modes = bc.get_modes(k0)[:n]
    if w["mixed"] and (len(modes) < n or any(m.modetype != "TEM" for m in modes)):
        kinds = ", ".join(m.modetype for m in modes) or "none"
        raise Refused("Port '%s''s face gives %d TEM mode(s) for its %d terminals at %.6g GHz (EMerge's modes there: %s), so "
                         "the terminals' S cannot be formed. Make the port region smaller (a box mode may be propagating), or "
                         "run the setup on openEMS." % (w["name"], sum(m.modetype == "TEM" for m in modes), n, freq / 1e9, kinds))
    return modes


def face_quadrature(sim, bc):
    """A degree-4, 6-point rule on every triangle of the port face: points (3, T*6) and weights carrying each area."""
    import numpy as np
    mesh = sim.mesh
    tv = mesh.tris[:, mesh.get_triangles(bc.tags)]
    a, b = 0.445948490915965, 0.091576213509771
    wa, wb = 0.223381589678011, 0.109951743655322
    bary = np.array([[a, a, 1 - 2 * a], [a, 1 - 2 * a, a], [1 - 2 * a, a, a],
                     [b, b, 1 - 2 * b], [b, 1 - 2 * b, b], [1 - 2 * b, b, b]])
    weights = np.array([wa] * 3 + [wb] * 3)
    P = [mesh.nodes[:, tv[k]] for k in range(3)]
    area = 0.5 * np.linalg.norm(np.cross((P[1] - P[0]).T, (P[2] - P[0]).T), axis=1)
    pts = sum(bary[:, k][None, :, None] * P[k].T[:, None, :] for k in range(3))
    return pts.reshape(-1, 3).T, (area[:, None] * weights[None, :]).ravel()


def path_integral(func, path):
    """The integral of func's (3, N) field along the straight path, GAUSS_POINTS-point Gauss-Legendre."""
    import numpy as np
    x, wt = np.polynomial.legendre.leggauss(GAUSS_POINTS)
    p, q = np.asarray(path["from"], float), np.asarray(path["to"], float)
    s = (x + 1) / 2
    pts = p[:, None] + (q - p)[:, None] * s[None, :]
    E = func(pts[0], pts[1], pts[2])
    d = q - p
    return complex(np.sum(wt / 2 * (E[0] * d[0] + E[1] * d[1] + E[2] * d[2])))


def terminal_arrays(sim, waves, every, freqs, adaptive):
    """R-emw3-4 — TV, W, Gp [f, n, n] block-diagonal over every wave-port entry and gammaEntries [f, n] (j beta of each
    entry's own mode), n the circuitRF port order. Read from the port's own modes (port.get_modes, port_mode_3d_global), never
    from the result's port properties, which repeat mode 1's for every mode (R-emw1-1). An adaptive sweep's requested
    frequencies had no mode solve: a face of several materials is solved at each here (the guard keeps it fresh); a face of
    one material keeps its one solve, its TEM modes' shapes not changing with frequency and beta scaling with k0."""
    import numpy as np
    n = len(every)
    TV = np.zeros((len(freqs), n, n), dtype=np.complex128)
    W = np.zeros_like(TV)
    Gp = np.zeros_like(TV)
    gam = np.zeros((len(freqs), n), dtype=np.complex128)
    for w, bc in waves:
        rows = [every.index(x) for x in entry_numbers(w)]
        paths = [t["path"] for t in w["terminals"]] if "terminals" in w else [w["path"]]
        xq, wq = face_quadrature(sim, bc)
        for i, f in enumerate(freqs):
            k0 = 2 * np.pi * f / C0
            if "terminals" in w:
                if w["mixed"] and adaptive:
                    sim.mw.modal_analysis(bc, len(rows), direct=False, TEM=False, freq=float(f))
                modes = checked_modes(w, bc, float(f))
            else:
                if w["mixed"] and adaptive:
                    sim.mw.modal_analysis(bc, 1, direct=False, TEM=True, freq=float(f))
                modes = bc.get_modes(k0)[:1]
            normal = np.asarray(bc.cs.zax.np, float)
            e = [bc.port_mode_3d_global(xq[0], xq[1], xq[2], k0, which="E", mode_nr=m + 1) for m in range(len(rows))]
            h = [bc.port_mode_3d_global(xq[0], xq[1], xq[2], k0, which="H", mode_nr=m + 1) for m in range(len(rows))]
            et = [x - np.outer(normal, normal @ x) for x in e]
            for a, ra in enumerate(rows):
                gam[i, ra] = 1j * (complex(modes[a].beta) * k0 / modes[a].k0 if modes[a].modetype == "TEM"
                                   else complex(modes[a].beta))
                for b, rb in enumerate(rows):
                    TV[i, ra, rb] = path_integral(lambda x, y, z, m=b: bc.port_mode_3d_global(x, y, z, k0, which="E", mode_nr=m + 1),
                                                  paths[a])
                    W[i, ra, rb] = (np.sum(wq * np.sum(et[b] * np.conj(et[a]), 0)) /
                                    np.sum(wq * np.sum(et[a] * np.conj(et[a]), 0)))
                    Gp[i, ra, rb] = 0.5 * np.sum(wq * (normal @ np.cross(e[a].T, np.conj(h[b]).T).T))
    return {"TV": TV, "W": W, "Gp": Gp, "gammaEntries": gam}


def measured_v(data, waves, every, keys, freqs):
    """R-emw3-5 — at each directly solved frequency, every wave-port entry's path integral of the TOTAL E for each excitation
    [f, entry, port], entries and ports in circuitRF order: the run's check of the conversion, never its input."""
    import numpy as np
    paths = {}
    for w, _ in waves:
        for t in (w["terminals"] if "terminals" in w else [w]):
            paths[t["number"]] = t["path"]
    out = np.zeros((len(freqs), len(every), len(every)), dtype=np.complex128)
    fields = list(data.field.iter())
    for i, f in enumerate(freqs):
        fd = min(fields, key=lambda e: abs(float(np.real(e.freq)) - f))
        fd._silent = True
        for j, n in enumerate(every):
            fd.excite_port(keys[n])
            for r, m in enumerate(every):
                if m in paths:
                    out[i, r, j] = path_integral(lambda x, y, z: fd.interpolate(x, y, z, usenan=False).E, paths[m])
    return out


# -- fields (brief-emi-7) -------------------------------------------------------------------------------------------------

MU0 = 4e-7 * 3.141592653589793
EPS0 = 8.8541878128e-12
PARAVIEW = os.path.join("postpro", "paraview")
# VTK's Lagrange tetrahedron and its 10-node edge order: the four vertices, then the midpoints of (0,1) (1,2) (0,2) (0,3)
# (1,3) (2,3).
LAGRANGE_TETRAHEDRON = 71
TET_EDGES = ((0, 1), (1, 2), (0, 2), (0, 3), (1, 3), (2, 3))
# brief-emi-11 — VTK's Lagrange triangle and its 6-node edge order: the three vertices, then the midpoints of (0,1) (1,2) (2,0).
LAGRANGE_TRIANGLE = 69
TRI_EDGES = ((0, 1), (1, 2), (2, 0))


def cell_nodes(mesh, order):
    """Every cell's own nodes, cell by cell (a Nedelec field is not continuous across a face, so no node is shared): the
    points (3, cells * per), the cell each belongs to, and the nodes per cell."""
    import numpy as np
    corners = mesh.nodes[:, mesh.tets]                          # (3, 4, cells)
    if order == 2:
        mids = [(corners[:, a, :] + corners[:, b, :]) / 2 for a, b in TET_EDGES]
        corners = np.concatenate([corners, np.stack(mids, axis=1)], axis=1)
    per = corners.shape[1]
    points = np.ascontiguousarray(np.transpose(corners, (0, 2, 1)).reshape(3, -1))
    return points, np.repeat(np.arange(mesh.n_tets), per), per


def evaluate(field, points, cells):
    """E and H at points, each evaluated INSIDE the cell named for it (the point-to-tetrahedron mapping supplied), never by
    a search that would pick either side of a face."""
    import numpy as np
    field._silent = True
    field.interpolate(points[0], points[1], points[2], usenan=False, mapping=cells)
    E = np.array([field.Ex, field.Ey, field.Ez]).reshape(3, -1)
    H = np.array([field.Hx, field.Hy, field.Hz]).reshape(3, -1)
    return E, H


def cell_owners(mesh, model):
    """Each tetrahedron's object (by index into the model's names) and material index."""
    import numpy as np
    obj = np.zeros(mesh.n_tets, dtype=np.int32)
    mat = np.zeros(mesh.n_tets, dtype=np.int32)
    for tag, tets in mesh.vtag_to_tet.items():
        if tag in model["volumes"]:
            k, m = model["volumes"][tag]
            obj[tets] = k
            mat[tets] = m
    return obj, mat


def write_step(folder, rel, points, per, attribute, arrays, cell_type=LAGRANGE_TETRAHEDRON):
    """One step in Palace's layout: <rel>/data.pvtu naming one piece, <rel>/proc000000.vtu, its cells all of cell_type with
    per nodes. arrays: name -> (components, N) or (N,) float (point data, Float32). Returns the bytes written."""
    import base64
    import numpy as np
    d = os.path.join(folder, rel)
    os.makedirs(d, exist_ok=True)
    cells = attribute.shape[0]

    def inline(a):
        b = np.ascontiguousarray(a).tobytes()
        return (base64.b64encode(np.uint32(len(b)).tobytes()) + base64.b64encode(b)).decode("ascii")

    def head(kind, name, comps, tail):
        n = ' Name="%s"' % name if name else ""
        return '<DataArray type="%s"%s NumberOfComponents="%d" %s' % (kind, n, comps, tail)

    arrays = [(name, np.atleast_2d(a)) for name, a in arrays]
    blocks, offset, entries = [], 0, []
    for name, a in arrays:
        b = np.ascontiguousarray(a.T.astype(np.float32)).tobytes()
        entries.append(head("Float32", name, a.shape[0], 'format="appended" offset="%d"/>' % offset))
        blocks.append(np.uint32(len(b)).tobytes() + b)
        offset += 4 + len(b)
    xml = ['<?xml version="1.0"?>',
           '<VTKFile type="UnstructuredGrid" version="2.2" byte_order="LittleEndian" header_type="UInt32">',
           '<UnstructuredGrid>', '<Piece NumberOfPoints="%d" NumberOfCells="%d">' % (points.shape[1], cells),
           '<Points>', head("Float32", None, 3, 'format="binary">') + inline(points.T.astype(np.float32)) + '</DataArray>',
           '</Points>', '<Cells>',
           head("Int32", "connectivity", 1, 'format="binary">') + inline(np.arange(cells * per, dtype=np.int32)) + '</DataArray>',
           head("Int32", "offsets", 1, 'format="binary">') + inline(np.arange(1, cells + 1, dtype=np.int32) * per) + '</DataArray>',
           head("UInt8", "types", 1, 'format="binary">') + inline(np.full(cells, cell_type, dtype=np.uint8)) + '</DataArray>',
           '</Cells>', '<PointData>'] + entries + ['</PointData>', '<CellData>',
           head("Int32", "attribute", 1, 'format="binary">') + inline(attribute.astype(np.int32)) + '</DataArray>',
           '</CellData>', '</Piece>', '</UnstructuredGrid>', '<AppendedData encoding="raw">']
    piece = os.path.join(d, "proc000000.vtu")
    with open(piece, "wb") as f:
        f.write(("\n".join(xml) + "\n_").encode("ascii"))
        for b in blocks:
            f.write(b)
        f.write(b"\n</AppendedData>\n</VTKFile>\n")
    pdata = "\n".join('<PDataArray type="Float32" Name="%s" NumberOfComponents="%d" format="appended"/>' % (n, a.shape[0])
                      for n, a in arrays)
    pvtu = "\n".join([
        '<?xml version="1.0"?>', '<VTKFile type="PUnstructuredGrid" version="2.2" byte_order="LittleEndian">',
        '<PUnstructuredGrid GhostLevel="0">',
        '<PPoints>', '<PDataArray type="Float32" Name="Points" NumberOfComponents="3" format="binary"/>', '</PPoints>',
        '<PCells>', '<PDataArray type="Int32" Name="connectivity" NumberOfComponents="1" format="binary"/>',
        '<PDataArray type="Int32" Name="offsets" NumberOfComponents="1" format="binary"/>',
        '<PDataArray type="UInt8" Name="types" NumberOfComponents="1" format="binary"/>', '</PCells>',
        '<PPointData>', pdata, '</PPointData>',
        '<PCellData>', '<PDataArray type="Int32" Name="attribute" NumberOfComponents="1" format="binary"/>', '</PCellData>',
        '<Piece Source="proc000000.vtu"/>', '</PUnstructuredGrid>', '</VTKFile>', ''])
    with open(os.path.join(d, "data.pvtu"), "w", encoding="ascii") as f:
        f.write(pvtu)
    return os.path.getsize(piece) + len(pvtu)


def write_field(folder, rel, mesh, field, points, cells, per, attribute, scale):
    """E and B of field (times scale) at every cell's own nodes, as one step. Returns the bytes written."""
    import numpy as np
    E, H = evaluate(field, points, cells)
    ur = np.real(np.asarray(field._dur)).reshape(-1)[cells]
    E = E * scale
    B = H * (scale * MU0) * ur
    arrays = [("B_imag", B.imag), ("B_real", B.real), ("E_imag", E.imag), ("E_real", E.real)]
    return write_step(folder, rel, points, per, attribute, arrays)


def surface_groups(model):
    """brief-emi-11 — each conductor's and sheet's face group with its attribute, numbered after every object's volume."""
    first = len(model["names"]) + 1
    return [(first + i, s) for i, s in enumerate(model["surfaces"])]


def boundary_nodes(mesh, model, order):
    """brief-emi-11 — the triangles of every conductor's walls and every sheet, each with its own nodes as cell_nodes gives a
    tetrahedron's: a dict of the points (3, triangles * per), the tetrahedron on each side of every node's triangle ("a" the
    one n points into, "b" the other; -1 where there is none), n at every node, each triangle's attribute, and the nodes per
    triangle; None when there is no such face. n points out of a conductor into the meshed side, and along a sheet's own +n;
    a triangle wound against n is wound again, so every triangle of one group faces the same way."""
    import numpy as np
    ids, attrs, refs = [], [], []
    for attribute, s in surface_groups(model):
        tags = [t for t in s["tags"]() if t in mesh.ftag_to_tri]
        if not tags:
            continue
        tris = np.asarray(mesh.get_triangles(tags), dtype=np.int64)
        ids.append(tris)
        attrs.append(np.full(tris.size, attribute, dtype=np.int32))
        refs.append(np.repeat(np.asarray(s["normal"] if s["normal"] is not None else [np.nan] * 3, dtype=np.float64)[:, None],
                              tris.size, axis=1))
    if not ids:
        return None
    ids, attribute, ref = np.concatenate(ids), np.concatenate(attrs), np.concatenate(refs, axis=1)
    corners = mesh.nodes[:, mesh.tris[:, ids]]                  # (3, 3, triangles)
    centre = corners.mean(axis=1)
    a, b = mesh.tri_to_tet[0, ids].copy(), mesh.tri_to_tet[1, ids].copy()
    a[a < 0], b[b < 0] = -1, -1
    toward_a = mesh.nodes[:, mesh.tets[:, a]].mean(axis=1) - centre
    wall = np.isnan(ref[0])
    ref[:, wall] = toward_a[:, wall]                            # a conductor's wall: into its one tetrahedron
    n = np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0], axis=0)
    flip = np.sum(n * ref, axis=0) < 0
    n[:, flip] *= -1
    corners[:, 1:, flip] = corners[:, 2:0:-1, flip]
    n /= np.linalg.norm(n, axis=0)
    swap = np.sum(n * toward_a, axis=0) < 0                     # a sheet: side a is the +n side
    a[swap], b[swap] = b[swap], a[swap]
    if order == 2:
        mids = [(corners[:, i, :] + corners[:, j, :]) / 2 for i, j in TRI_EDGES]
        corners = np.concatenate([corners, np.stack(mids, axis=1)], axis=1)
    per = corners.shape[1]
    return {"points": np.ascontiguousarray(np.transpose(corners, (0, 2, 1)).reshape(3, -1)),
            "a": np.repeat(a, per), "b": np.repeat(b, per), "n": np.repeat(n, per, axis=1), "attribute": attribute, "per": per}


def side_fields(field, points, cells):
    """D and H on one side of every boundary node, each evaluated inside that side's tetrahedron (the one-sided limit); zero
    where there is no tetrahedron (cells -1). D is eps E with the cell's own real permittivity, as energy_by_material reads it."""
    import numpy as np
    D = np.zeros((3, cells.size), dtype=np.complex128)
    H = np.zeros((3, cells.size), dtype=np.complex128)
    on = cells >= 0
    if on.any():
        E, H[:, on] = evaluate(field, points[:, on], cells[on])
        D[:, on] = EPS0 * np.real(np.asarray(field._der)).reshape(-1)[cells[on]] * E
    return D, H


def surface_current(n, h_a, h_b):
    """J_s = n x (H_a - H_b): the jump in tangential H across a face, n pointing into side a (src/Design/RESOLVED.md, EMI-11:
    Palace's own, n into element 1 and H1 - H2). A conductor's wall has no side b, so it is n x H on the meshed side."""
    import numpy as np
    return np.cross(n, h_a - h_b, axis=0)


def surface_charge(n, d_a, d_b):
    """Q_s = n . (D_a - D_b), on the same terms as surface_current (Palace's two-sided surface charge)."""
    import numpy as np
    return np.sum(n * (d_a - d_b), axis=0)


def write_boundary(folder, rel, field, bnd, scale):
    """J_s and Q_s of field (times scale, the volume step's own) on the boundary triangles, as one step. Returns the bytes."""
    Da, Ha = side_fields(field, bnd["points"], bnd["a"])
    Db, Hb = side_fields(field, bnd["points"], bnd["b"])
    J = surface_current(bnd["n"], Ha, Hb) * scale
    Q = surface_charge(bnd["n"], Da, Db) * scale
    arrays = [("J_s_imag", J.imag), ("J_s_real", J.real), ("Q_s_imag", Q.imag), ("Q_s_real", Q.real)]
    return write_step(folder, rel, bnd["points"], bnd["per"], bnd["attribute"], arrays, LAGRANGE_TRIANGLE)


def write_groups(folder, model, mats):
    """groups.json: each attribute's object, as circuitRF's Palace mesh records it (FieldGroups reads Name, Attribute,
    Dimension, Kind) -- the volumes, then (brief-emi-11) the conductors' and sheets' face groups."""
    groups = [{"Attribute": k + 1, "Name": name, "Dimension": 3, "Kind": "Background" if k == 0 else "Volume",
               "Material": mats[m]["name"]}
              for k, name in enumerate(model["names"])
              for m in sorted({mm for kk, mm in model["volumes"].values() if kk == k})[:1]]
    for attribute, s in surface_groups(model):
        g = {"Attribute": attribute, "Name": s["name"], "Dimension": 2, "Kind": s["kind"], "Material": mats[s["material"]]["name"]}
        if "thicknessM" in s:
            g["ThicknessM"] = s["thicknessM"]
        groups.append(g)
    with open(os.path.join(folder, "groups.json"), "w", encoding="utf-8") as f:
        json.dump({"Generator": "circuitRF brief-emi-7: the mapping from named objects to EMerge field attributes",
                   "LengthUnitM": 1, "Groups": groups}, f, indent=2)


def port_voltage(field, bc):
    """A lumped port's voltage in field: EMerge's own integration lines, averaged, as its S-parameters are computed."""
    import numpy as np
    lines = list(getattr(bc, "voltage_integration_line", None) or [])
    if not lines:
        return None
    total = 0
    for line in lines:
        field._silent = True
        field.interpolate(np.asarray(line.xs), np.asarray(line.ys), np.asarray(line.zs), usenan=False)
        total += line.line_integral_precalc(np.ravel(field.Ex), np.ravel(field.Ey), np.ravel(field.Ez)) / len(lines)
    return complex(total)


def energy_by_material(field, mesh, mat, materials):
    """The integral of Re(eps)|E|^2 over each material's cells, J (peak phasors), by EMerge's own tetrahedral quadrature."""
    import numpy as np
    from emerge._emerge.mth.optimized import gaus_quad_tet
    from emr.mth import generate_int_data_tet
    rule = gaus_quad_tet(4)
    X, Y, Z, W, A, _ = generate_int_data_tet(mesh.nodes, mesh.tets, rule)
    cells = np.tile(np.arange(mesh.n_tets), rule.shape[1])     # the points come quadrature point by quadrature point
    E, _ = evaluate(field, np.array([X, Y, Z]), cells)
    er = np.real(np.asarray(field._der)).reshape(-1)[cells]
    density = EPS0 * er * np.sum(np.abs(E) ** 2, axis=0) * A * W
    return [float(np.sum(density[mat[cells] == m])) for m in range(len(materials))]


def driven_fields(problem, folder, sim, model, solved):
    """The fields asked for, one step per (port, frequency), and the far field when asked; returns run.json's fields."""
    import numpy as np
    req = problem["fields"]
    freqs = req["frequenciesHz"]
    near = lambda a, b: abs(a - b) <= 1e-9 * max(abs(a), abs(b))
    have = {}
    for e in solved:
        for f in freqs:
            if near(float(np.real(e.freq)), f):
                have[f] = e
    missing = [f for f in freqs if f not in have]
    if missing:
        # A direct solve at each: the adaptive sweep fitted S between its own samples, and has no field anywhere else.
        sim.mw.set_frequency(list(missing))
        for e in sim.mw.run_sweep().field.iter():
            for f in missing:
                if near(float(np.real(e.freq)), f):
                    have[f] = e
    mesh = sim.mesh
    order = problem["settings"]["elementOrder"]
    points, cells, per = cell_nodes(mesh, order) if req["write"] else (None, None, 0)
    bnd = boundary_nodes(mesh, model, order) if req["write"] else None
    obj, _ = cell_owners(mesh, model)
    ports = sorted(model["ports"], key=lambda pb: pb[0]["number"])
    keys = entry_keys(problem)
    steps, written = [], 0
    farfield = req.get("farField")
    if farfield:
        theta = np.arange(0, 180 + 1e-9, farfield["thetaStepDeg"])
        phi = np.arange(0, 360 - 1e-9, farfield["phiStepDeg"])
        T, P = np.meshgrid(np.radians(theta), np.radians(phi), indexing="ij")
        far = np.zeros((len(freqs), len(ports), 2, len(theta), len(phi)), dtype=np.complex128)
        faces = em_faces(model["absorbing"])
    for i, f in enumerate(freqs):
        field = have[f]
        for j, (p, bc) in enumerate(ports):
            if "terminals" in p:
                continue        # brief-emw-3: a terminal group's excitations are modal; a terminal's own is not formed here
            field.excite_port(keys[p["number"]])
            v, vinc = port_voltage(field, bc), getattr(bc, "voltage", None)
            if p in [w for w, _ in model["waves"]]:
                # a wave port's mode at 1 W reads sqrt(2 Z_PV) along its line (R-emw1-5); EMerge's own voltage is sqrt(Z0)
                vinc = (2 * complex(bc.get_modes(2 * np.pi * f / C0)[0].Z0).real) ** 0.5
            # EMerge drives a lumped port with an incident wave of sqrt(2 P Z0) peak, P its power (1 W): P time-averaged.
            scale = (req["incidentPowerW"] / bc.power) ** 0.5
            step = {"port": p["number"], "frequencyHz": f, "scale": scale,
                    "v": None if v is None else [v.real * scale, v.imag * scale],
                    "vinc": None if vinc is None else [float(vinc) * scale, 0.0]}
            if req["write"]:
                rel = os.path.join(PARAVIEW, "driven", "excitation_%d" % p["number"], "Cycle%06d" % (i + 1))
                written += write_field(folder, rel, mesh, field, points, cells, per, obj + 1, scale)
                step["pvtu"] = os.path.join(rel, "data.pvtu").replace(os.sep, "/")
                if bnd is not None:
                    rel = os.path.join(PARAVIEW, "driven_boundary", "excitation_%d" % p["number"], "Cycle%06d" % (i + 1))
                    written += write_boundary(folder, rel, field, bnd, scale)
                    step["boundaryPvtu"] = os.path.join(rel, "data.pvtu").replace(os.sep, "/")
            if farfield:
                ff = field.farfield(T, P, faces)
                far[i, j, 0] = np.asarray(ff.Etheta).reshape(T.shape) * scale
                far[i, j, 1] = np.asarray(ff.Ephi).reshape(T.shape) * scale
            steps.append(step)
    if req["write"]:
        write_groups(folder, model, problem["materials"])
    if farfield:
        np.save(os.path.join(folder, "ff_E.npy"), np.ascontiguousarray(far))
        np.save(os.path.join(folder, "ff_theta.npy"), np.ascontiguousarray(theta.astype(np.float64)))
        np.save(os.path.join(folder, "ff_phi.npy"), np.ascontiguousarray(phi.astype(np.float64)))
        np.save(os.path.join(folder, "ff_freq.npy"), np.ascontiguousarray(np.array(freqs, dtype=np.float64)))
    return {"fieldSteps": steps, "fieldBytes": written, "fieldOrder": order}


def em_faces(tags):
    import emerge as em
    return em.FaceSelection(list(tags))


def mode_fields(problem, folder, sim, model, entries):
    """Each mode's energy by material, its lumped ports' voltages and (asked) its field, normalised as Palace normalises a
    mode; returns run.json's fields."""
    import numpy as np
    req = problem["fields"]
    mesh = sim.mesh
    order = problem["settings"]["elementOrder"]
    points, cells, per = cell_nodes(mesh, order) if req["write"] else (None, None, 0)
    bnd = boundary_nodes(mesh, model, order) if req["write"] else None
    obj, mat = cell_owners(mesh, model)
    ports = sorted(model["ports"], key=lambda pb: pb[0]["number"])
    steps, written = [], 0
    for k, field in enumerate(entries):
        energy = energy_by_material(field, mesh, mat, problem["materials"])
        total = sum(energy)
        scale = (req["modeEnergyJ"] / total) ** 0.5 if total > 0 else 1.0
        if req["write"]:
            # The phase: the largest field component at any node real and positive, so Re{E} is the mode at its peak.
            E, _ = evaluate(field, points, cells)
            at = np.unravel_index(np.argmax(np.abs(E)), E.shape)
            scale = scale * np.conj(E[at]) / abs(E[at]) if abs(E[at]) > 0 else scale
        volts = [port_voltage(field, bc) for _, bc in ports]
        step = {"mode": k, "energy": [e * abs(scale) ** 2 for e in energy],
                "portV": [None if v is None else [(v * scale).real, (v * scale).imag] for v in volts]}
        if req["write"]:
            rel = os.path.join(PARAVIEW, "eigenmode", "Cycle%06d" % (k + 1))
            written += write_field(folder, rel, mesh, field, points, cells, per, obj + 1, scale)
            step["pvtu"] = os.path.join(rel, "data.pvtu").replace(os.sep, "/")
            if bnd is not None:
                rel = os.path.join(PARAVIEW, "eigenmode_boundary", "Cycle%06d" % (k + 1))
                written += write_boundary(folder, rel, field, bnd, scale)
                step["boundaryPvtu"] = os.path.join(rel, "data.pvtu").replace(os.sep, "/")
        steps.append(step)
    if req["write"]:
        write_groups(folder, model, problem["materials"])
    return {"fieldSteps": steps, "fieldBytes": written, "fieldOrder": order,
            "portNumbers": [p["number"] for p, _ in ports], "portR": [p["z0"] for p, _ in ports],
            "materialNames": [m["name"] for m in problem["materials"]]}


if __name__ == "__main__":
    main(sys.argv)
