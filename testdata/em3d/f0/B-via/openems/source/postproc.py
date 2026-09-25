# circuitRF F0 spike -- probe files -> S-parameters, by hand-checked arithmetic. SPIKE MATERIAL.
# Each probe's OWN time column is used for its DFT (voltage and current probes are sampled half a
# time step apart, overview 1f). Port 1 is the only one excited; S22 = S11 and S12 = S21 follow
# from the mirror symmetry of the geometry about x = 0, and the .s2p says so.
#   python postproc.py <run dir> <out.s2p> <title>
import sys, numpy as np
run, out, title = sys.argv[1], sys.argv[2], sys.argv[3]
Z = 50.0
f = np.round(np.arange(1, 201) * 0.1, 1) * 1e9
def load(name):
    d = np.loadtxt(f'{run}/{name}', comments='%')
    t, x = d[:, 0], d[:, 1]
    # DFT on the probe's own samples; dt is the file's own sampling interval
    dt = t[1] - t[0]
    return np.array([np.sum(x * np.exp(-2j*np.pi*fk*t)) * dt for fk in f]), t
u1, tu = load('port_ut_1'); i1, ti = load('port_it_1')
u2, _ = load('port_ut_2'); i2, _ = load('port_it_2')
a1 = 0.5 * (u1 + Z*i1); b1 = u1 - a1           # incident / reflected voltage waves at port 1
b2 = 0.5 * (u2 - Z*i2)                          # outgoing wave at the passive port 2
# (openEMS's current probe at the passive port points INTO the port, as at port 1, so the wave
#  leaving the circuit into port 2's resistor is 0.5*(u2 - Z*i2); checked against CalcPort below)
S11 = b1 / a1; S21 = b2 / a1
with open(out, 'w') as fh:
    fh.write(f'! circuitRF F0 spike reference -- {title}\n')
    fh.write('! openEMS probes -> S by postproc.py (own time column per probe); port 1 excited only;\n')
    fh.write('! S22 = S11 and S12 = S21 by the 180-degree rotational symmetry (not a second run)\n')
    fh.write('# GHz S RI R 50\n')
    for k in range(len(f)):
        s = [S11[k], S21[k], S21[k], S11[k]]
        fh.write(f'{f[k]/1e9:g} ' + ' '.join(f'{v.real:.9e} {v.imag:.9e}' for v in s) + '\n')
print('first samples: u', tu[0], 'i', ti[0], ' offset', ti[0]-tu[0])
for k in [0, 9, 49, 99, 199]:
    print(f'{f[k]/1e9:5.1f} GHz |S11| {20*np.log10(abs(S11[k])):8.3f} dB  |S21| {20*np.log10(abs(S21[k])):8.4f} dB  ang S21 {np.degrees(np.angle(S21[k])):8.3f}')
# cross-check against openEMS's own CalcPort (upstream post-processing)
try:
    from openEMS.ports import LumpedPort
    import CSXCAD
    CSX = CSXCAD.ContinuousStructure()
    p1 = LumpedPort(CSX, 1, 50, [-5000, -200, 270], [-5000, 200, 470], 'z', excite=1.0)
    p2 = LumpedPort(CSX, 2, 50, [5000, -200, 235], [5000, 200, 35], 'z')
    p1.CalcPort(run, f); p2.CalcPort(run, f)
    s11o = p1.uf_ref / p1.uf_inc; s21o = p2.uf_ref / p1.uf_inc
    print('max |S11 - CalcPort|', np.max(abs(s11o - S11)), ' max |S21 - CalcPort|', np.max(abs(s21o - S21)))
except Exception as ex:
    print('CalcPort cross-check skipped:', ex)
