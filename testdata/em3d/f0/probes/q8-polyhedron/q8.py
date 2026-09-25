# circuitRF F0 spike -- Q8: does the pinned CSXCAD read a triangle file (STL / PLY) as a polyhedron?
# SPIKE MATERIAL. A 50-ohm lumped port across a 1 mm gap between two PEC plates; the gap is either
# left empty or bridged by a PEC block read from a triangle file. If the file is read, the port is
# shorted and its voltage collapses; if it is silently skipped, the two runs agree.
import sys, os, numpy as np
from CSXCAD import ContinuousStructure
from openEMS import openEMS
mode = sys.argv[1]            # none | stl | ply
box = [(0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1)]
faces = [(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),(1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,0,4),(3,4,7)]
V = [(x*2-1, y*2-1, z) for x,y,z in box]          # 2 x 2 x 1 mm block spanning the gap z[0,1]
if mode == 'stl':
    with open('block.stl','w') as f:
        f.write('solid block\n')
        for a,b,c in faces:
            f.write(' facet normal 0 0 0\n  outer loop\n')
            for i in (a,b,c): f.write('   vertex %g %g %g\n' % V[i])
            f.write('  endloop\n endfacet\n')
        f.write('endsolid block\n')
if mode == 'ply':
    with open('block.ply','w') as f:
        f.write('ply\nformat ascii 1.0\nelement vertex 8\nproperty float x\nproperty float y\nproperty float z\n'
                'element face 12\nproperty list uchar int vertex_indices\nend_header\n')
        for v in V: f.write('%g %g %g\n' % v)
        for a,b,c in faces: f.write('3 %d %d %d\n' % (a,b,c))
FDTD = openEMS(NrTS=3000, EndCriteria=1e-4); FDTD.SetGaussExcite(5e9, 5e9); FDTD.SetBoundaryCond(['MUR']*6)
CSX = ContinuousStructure(); FDTD.SetCSX(CSX); g = CSX.GetGrid(); g.SetDeltaUnit(1e-3)
m = CSX.AddMetal('plates'); m.AddBox([-3,-3,0],[3,3,0]); m.AddBox([-3,-3,1],[3,3,1])
if mode != 'none':
    blk = CSX.AddMetal('block')
    blk.AddPolyhedronReader(os.path.abspath('block.' + mode), priority=10)
FDTD.AddLumpedPort(1, 50, [2.5,-0.5,0], [2.5,0.5,1], 'z', excite=1)
g.AddLine('x', np.linspace(-6,6,49)); g.AddLine('y', np.linspace(-6,6,49)); g.AddLine('z', np.linspace(-4,5,37))
os.makedirs('run_'+mode, exist_ok=True); FDTD.Write2XML('run_%s/case.xml' % mode)
