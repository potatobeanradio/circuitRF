# circuitRF F0 spike -- Palace port-S.csv (dB, degrees) -> Touchstone RI, by hand-checked arithmetic.
# SPIKE MATERIAL. Port 1 is the only port excited, so only S11 and S21 exist in the CSV; S22 = S11
# and S12 = S21 follow from the geometry's symmetry, and the file says so.
#   python palace2s2p.py <port-S.csv> <out.s2p> <title> <symmetry sentence>
import sys, numpy as np
src, out, title, sym = sys.argv[1:5]
d = np.loadtxt(src, delimiter=',', skiprows=1)
f = d[:, 0]
S11 = 10**(d[:, 1]/20) * np.exp(1j*np.radians(d[:, 2]))
S21 = 10**(d[:, 3]/20) * np.exp(1j*np.radians(d[:, 4]))
with open(out, 'w') as fh:
    fh.write(f'! circuitRF F0 spike reference -- {title}\n')
    fh.write(f'! Palace postpro/port-S.csv, dB/deg -> RI; port 1 excited only; {sym}\n')
    fh.write('# GHz S RI R 50\n')
    for k in range(len(f)):
        s = [S11[k], S21[k], S21[k], S11[k]]
        fh.write(f'{f[k]:g} ' + ' '.join(f'{v.real:.9e} {v.imag:.9e}' for v in s) + '\n')
