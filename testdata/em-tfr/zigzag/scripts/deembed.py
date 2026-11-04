#!/usr/bin/env python3
# 2x-thru de-embedding by the half-thru's ABCD matrix square root, reporting the film section.
import sys, re, numpy as np
Z0=50.0
def read_s2p(p):
    rows=[]
    for l in open(p,encoding='utf-8',errors='replace'):
        l=l.strip()
        if not l or l[0] in '!#': continue
        rows.append([float(x) for x in l.split()])
    a=np.array(rows); f=a[:,0]; s=np.zeros((len(f),2,2),complex)
    s[:,0,0]=a[:,1]+1j*a[:,2]; s[:,1,0]=a[:,3]+1j*a[:,4]; s[:,0,1]=a[:,5]+1j*a[:,6]; s[:,1,1]=a[:,7]+1j*a[:,8]
    return f,s
def s2a(s):
    S11,S12,S21,S22=s[0,0],s[0,1],s[1,0],s[1,1]; d=2*S21
    return np.array([[((1+S11)*(1-S22)+S12*S21)/d, Z0*((1+S11)*(1+S22)-S12*S21)/d],
                     [((1-S11)*(1-S22)-S12*S21)/(d*Z0), ((1-S11)*(1+S22)+S12*S21)/d]])
def a2s(a):
    A,B,C,D=a[0,0],a[0,1],a[1,0],a[1,1]; d=A+B/Z0+C*Z0+D
    return np.array([[(A+B/Z0-C*Z0-D)/d, 2*(A*D-B*C)/d],[2/d,(-A+B/Z0-C*Z0+D)/d]])
def sqrtm2(m, prev=None):
    # principal branch, continued in frequency by the caller via the half-thru's phase
    t=np.trace(m); d=np.linalg.det(m); sd=np.sqrt(d); 
    st=np.sqrt(t+2*sd)
    return (m+sd*np.eye(2))/st
def film(dut,thru):
    fd,sd=read_s2p(dut); ft,st=read_s2p(thru); out=[]
    for k,f in enumerate(fd):
        j=np.argmin(abs(ft-f))
        A=s2a(sd[k]); T=s2a(st[j])
        H=sqrtm2(T)
        # the half-thru is electrically short of 180 deg at every point here: keep A11 of the positive-real branch
        if H[0,0].real<0: H=-H
        M=np.linalg.solve(H,A); M=np.linalg.solve(H.T,M.T).T   # H^-1 A H^-1
        M=np.linalg.inv(H)@A@np.linalg.inv(H)
        out.append((f,M,a2s(M)))
    return out
if __name__=="__main__":
    runs=[a.split('=') for a in sys.argv[1:]]   # name=dut,thru
    base=None
    res={}
    for name,v in runs:
        d,t=v.split(','); res[name]=film(d,t)
    names=list(res)
    print("Series impedance of the film section, B of its ABCD (Ohm); DC row is R")
    for n in names:
        print(n.ljust(12)," ".join(f"{r[0]:g}GHz:{r[1][0,1].real:8.2f}{r[1][0,1].imag:+8.2f}j" for r in res[n]))
    print("S21 of the film section")
    for n in names:
        print(n.ljust(12)," ".join(f"{20*np.log10(abs(r[2][1,0])):7.3f}dB/{np.degrees(np.angle(r[2][1,0])):7.2f}" for r in res[n]))
    print("S11 of the film section")
    for n in names:
        print(n.ljust(12)," ".join(f"{20*np.log10(abs(r[2][0,0])+1e-30):7.2f}dB/{np.degrees(np.angle(r[2][0,0])):7.2f}" for r in res[n]))
    ref=names[0]
    for n in names[1:]:
        print(f"|dS| {n} vs {ref}: ", " ".join(f"{r[0]:g}GHz:S21 {abs(r[2][1,0]-q[2][1,0]):.4f} S11 {abs(r[2][0,0]-q[2][0,0]):.4f} S22 {abs(r[2][1,1]-q[2][1,1]):.4f}" for r,q in zip(res[n],res[ref])))
