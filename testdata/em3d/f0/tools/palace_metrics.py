# SPIKE MATERIAL: one row of run metrics per Palace run directory, from its own log and palace.json.
import sys, re, json, os
print('| run | tets | ND unknowns (final) | AMR iterations | sweep samples (n) | Palace total s | peak memory (Palace, all ranks) |')
print('|---|---|---|---|---|---|---|')
for d in sys.argv[1:]:
    log = open(os.path.join(d, 'palace.log')).read()
    nd = re.findall(r'ND \(p = \d\): (\d+)', log)
    tets = re.findall(r'^ elements\s+\d+\s+\d+\s+\d+\s+(\d+)', log, re.M)
    amr = re.findall(r'Completed (\d+) iterations of adaptive mesh refinement', log)
    n = re.findall(r'^ n = (\d+), error = ([\d.e+-]+)', log, re.M)
    mem = re.findall(r'^Total\s+(\S+)\s+(\S+)\s+(\S+)\s*$', log, re.M)
    j = json.load(open(os.path.join(d, 'postpro', 'palace.json')))
    tot = j['ElapsedTime']['Durations']['Total']
    print(f"| {d} | {tets[-1] if tets else '?'} | {nd[-1] if nd else '?'} | {amr[-1] if amr else '0'} | "
          f"{(n[-1][0] + ' (err ' + n[-1][1] + ')') if n else 'single point'} | {tot:.1f} | {mem[-1][2] if mem else '?'} |")
