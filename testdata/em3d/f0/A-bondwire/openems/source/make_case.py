# circuitRF F0 spike -- case A for openEMS, written with openEMS's OWN Python interface.
# HAND-WRITTEN SPIKE MATERIAL (brief-em3d-1 R-em3d1-2). Not circuitRF code: nothing under src/ or
# tools/ may import or copy it. It writes case.xml; the solver is then run as a program on that file.
#
#   python make_case.py <outdir> <model> <dw>
#     model  curve : the wire as a thin PEC curve on grid edges ("thin wire at the grid resolution")
#            wire  : the wire as a PEC cylinder of the true 12.7 um radius, rasterised by the grid
#     dw     grid step (um) in the refined box around the wire
#
# Geometry is case A of the spike (see ../palace-*/case.geo): PEC box x[-1500,1500] y[-1000,1000]
# z[0,1100], substrate z[0,100] er 9.8, PEC pads 100x100 um at x = -500 / +500, lumped 50-ohm ports
# from each pad's outer edge to ground. The wire follows the same axis as the Palace model, from
# where it meets the pad plane: (-487.7, 100) (-325, 262.7) (325, 262.7) (487.7, 100) in (x, z).
# The wire is LOSSLESS here (PEC): resolving gold's skin depth is outside what an FDTD grid of this
# size can do; at 10 GHz the wire's own loss is ~0.04 dB of |S21| (stated in the findings).
import sys, os
import numpy as np
from CSXCAD import ContinuousStructure
from openEMS import openEMS

out, model, dw = sys.argv[1], sys.argv[2], float(sys.argv[3])
os.makedirs(out, exist_ok=True)

r = 12.7
Lx, Ly, H, hs = 3000.0, 2000.0, 1100.0, 100.0
za = hs + r                      # axis height at the round wire's ends
pts = np.array([[-475 - r, -325, 325, 475 + r],
                [0, 0, 0, 0],
                [hs, za + 150, za + 150, hs]])

FDTD = openEMS(EndCriteria=1e-5, NrTS=2_000_000)
FDTD.SetGaussExcite(20e9, 20e9)                  # 0-40 GHz
FDTD.SetBoundaryCond(['PEC'] * 6)
CSX = ContinuousStructure()
FDTD.SetCSX(CSX)
mesh = CSX.GetGrid()
mesh.SetDeltaUnit(1e-6)

sub = CSX.AddMaterial('substrate', epsilon=9.8)
sub.AddBox([-Lx/2, -Ly/2, 0], [Lx/2, Ly/2, hs], priority=1)
pads = CSX.AddMetal('pads')
pads.AddBox([-550, -50, hs], [-450, 50, hs], priority=10)
pads.AddBox([450, -50, hs], [550, 50, hs], priority=10)
wire = CSX.AddMetal('wire')
if model == 'curve':
    wire.AddCurve(pts, priority=10)
else:
    wire.AddWire(pts, radius=r, priority=10)

ports = [FDTD.AddLumpedPort(1, 50, [-550, -50, 0], [-550, 50, hs], 'z', excite=1.0, priority=5),
         FDTD.AddLumpedPort(2, 50, [550, -50, 0], [550, 50, hs], 'z', priority=5)]

# Grid: fixed lines on every edge that matters, a uniform dw box around the wire, smoothed out
# to at most 100 um (lambda/10 at 40 GHz in the substrate is ~240 um).
x = [-Lx/2, Lx/2, -550, -450, 450, 550, -500, 500]
y = [-Ly/2, Ly/2, -50, 50, 0]
z = [0, H, hs, za + 150]
x += list(np.arange(-575, -425 + 1, 25)) + list(np.arange(425, 575 + 1, 25))   # pads and ports
y += list(np.arange(-75, 75 + 1, 25))
z += list(np.arange(0, hs + 1, 20))                                             # substrate: 5 cells
x += list(np.arange(-500, 500 + dw/2, dw))
z += list(np.arange(hs, za + 150 + 3*r + dw/2, dw))
y += list(np.arange(-3*r, 3*r + dw/2, dw))
def merge(fixed, fill, tol):
    # keep every fixed line; drop a fill line closer than tol to any line already kept. Without this
    # a fill line 0.6 um from a fixed one makes a sub-micron cell that sets the time step for the
    # whole grid (first ladder attempt: dt 1.2e-15 s on a 12.7 um grid).
    out = sorted(set(fixed))
    for v in sorted(fill):
        if min(abs(v - o) for o in out) >= tol: out.append(v); out.sort()
    return out
fx = [-Lx/2, Lx/2, -550, -450, 450, 550, -500, 500]
fy = [-Ly/2, Ly/2, -50, 50, 0]
fz = [0, H, hs, za + 150]
x = merge(fx, [v for v in x if v not in fx], 0.5 * min(dw, 25))
y = merge(fy, [v for v in y if v not in fy], 0.5 * min(dw, 25))
z = merge(fz, [v for v in z if v not in fz], 0.5 * min(dw, 20))
mesh.AddLine('x', x); mesh.AddLine('y', y); mesh.AddLine('z', z)
mesh.SmoothMeshLines('all', 100, 1.4)

FDTD.Write2XML(os.path.join(out, 'case.xml'))
n = [len(mesh.GetLines(a)) for a in 'xyz']
print(f'grid {n[0]} x {n[1]} x {n[2]} = {n[0]*n[1]*n[2]} cells(lines product), dw = {dw} um, model = {model}')
