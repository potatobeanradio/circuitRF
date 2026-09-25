# circuitRF F0 spike -- case B for openEMS, written with openEMS's OWN Python interface.
# HAND-WRITTEN SPIKE MATERIAL (brief-em3d-1 R-em3d1-2). Not circuitRF code: nothing under src/ or
# tools/ may import or copy it. It writes case.xml; the solver is then run as a program on that file.
#
#   python make_case.py <outdir> <dw> [lossy|lossless] [max timesteps]
#     dw: grid step (um) at the lines and the via; lossless sets kappa = 0
#
# Geometry is case B of the spike (see ../palace/case.geo). CSXCAD has no booleans: the antipad is
# a higher-PRIORITY dielectric cylinder inside the ground plane's PEC box, and the barrel a still
# higher-priority PEC cylinder (em-3d.md 6.5). Copper is PEC with its true 35 um thickness (no
# conductor loss). The substrate's tan(delta) = 0.004 becomes a constant conductivity fitted at the
# band centre, 10 GHz (overview 1k): kappa = 2 pi f0 eps0 er tand. Outer walls: first-order Mur.
import sys, os
import numpy as np
from CSXCAD import ContinuousStructure
from openEMS import openEMS

out, dw = sys.argv[1], float(sys.argv[2])
lossless = len(sys.argv) > 3 and sys.argv[3] == 'lossless'
nrts = int(sys.argv[4]) if len(sys.argv) > 4 else 3_000_000
os.makedirs(out, exist_ok=True)

w, rp, rd, ra = 400.0, 300.0, 150.0, 450.0
Bx, By, m = 5000.0, 2500.0, 1500.0
z0, z1, z2, z3, z4, z5 = 0.0, 35.0, 235.0, 270.0, 470.0, 505.0
er, tand, f0 = 3.66, 0.004, 10e9
kappa = 0.0 if lossless else 2 * np.pi * f0 * 8.8541878128e-12 * er * tand

FDTD = openEMS(EndCriteria=1e-5, NrTS=nrts)
FDTD.SetGaussExcite(float(os.environ.get('F0', 10e9)), float(os.environ.get('FC', 10e9)))  # default 0-20 GHz
FDTD.SetBoundaryCond([os.environ.get('BC', 'MUR')] * 6)
CSX = ContinuousStructure()
FDTD.SetCSX(CSX)
mesh = CSX.GetGrid()
mesh.SetDeltaUnit(1e-6)

diel = CSX.AddMaterial('dielectric', epsilon=er, kappa=kappa)
diel.AddBox([-Bx, -By, z1], [Bx, By, z4], priority=1)
gnd = CSX.AddMetal('ground')
gnd.AddBox([-Bx, -By, z2], [Bx, By, z3], priority=10)
fill = CSX.AddMaterial('antipad_fill', epsilon=er, kappa=kappa)
fill.AddCylinder([0, 0, z2], [0, 0, z3], ra, priority=20)
sig = CSX.AddMetal('signal')
sig.AddBox([-Bx, -w/2, z4], [0, w/2, z5], priority=30)
sig.AddCylinder([0, 0, z4], [0, 0, z5], rp, priority=30)
sig.AddBox([0, -w/2, z0], [Bx, w/2, z1], priority=30)
sig.AddCylinder([0, 0, z0], [0, 0, z1], rp, priority=30)
sig.AddCylinder([0, 0, z0], [0, 0, z5], rd, priority=30)

ports = [FDTD.AddLumpedPort(1, 50, [-Bx, -w/2, z3], [-Bx, w/2, z4], 'z', excite=1.0, priority=40),
         FDTD.AddLumpedPort(2, 50, [Bx, -w/2, z2], [Bx, w/2, z1], 'z', priority=40)]

# Grid: lines on every copper face; the thirds rule at the line edges (a line dw/3 inside and
# 2dw/3 outside each edge); a uniform dw patch over the via; smoothed to at most 400 um.
x = [-Bx - m, Bx + m, -Bx, Bx, 0]
y = [-By - m, By + m, -By, By, 0]
z = [z0 - m, z5 + m, z0, z1, z2, z3, z4, z5]
for s in (-1, 1):
    y += [s * (w/2 - dw/3), s * (w/2 + 2*dw/3)]
x += list(np.arange(-ra - 2*dw, ra + 2*dw + 1e-6, dw))
y += list(np.arange(-ra - 2*dw, ra + 2*dw + 1e-6, dw))
z += list(np.linspace(z1, z2, 6)) + list(np.linspace(z3, z4, 6))    # 5 cells per dielectric
mesh.AddLine('x', x); mesh.AddLine('y', y); mesh.AddLine('z', z)
mesh.SmoothMeshLines('all', 400, 1.4)

FDTD.Write2XML(os.path.join(out, 'case.xml'))
n = [len(mesh.GetLines(a)) for a in 'xyz']
print(f'grid {n[0]} x {n[1]} x {n[2]} lines, dw = {dw} um, kappa = {kappa:.6e} S/m')
