#!/usr/bin/env python3
"""brief-emi-2 R-emi2-1b -- the hash-locked requirement lines each EMerge recipe writes.

For each platform: `uv pip compile --generate-hashes` pins EMerge and every transitive dependency at an exact
version, then this keeps, of each pinned version's hashes, only those of the wheels that platform could install
(PyPI's JSON API maps a hash to its file name), and prints one requirement per line -- the form a recipe's `lines`
take and SolverInstallTests parses. Nothing is built: the compile runs with --only-binary :all:.

    python3 tools/EmergeLock/make_locks.py <uv> <emerge-version> <python-version> <out-dir> [<recipes-dir>]

writes <out-dir>/<platform>.txt for each platform below. The uv given should be the version the recipes pin. With
<recipes-dir> (src/Design/Em3d/Install/recipes) it also puts each lock into its recipe's "Write the locked
requirements" step, keeping that step's two comment lines; the Windows arm64 recipe takes Windows x64's lock, since it
installs the x64 Python. --no-python-downloads: the compile needs no interpreter of the target version, and without it
uv fetches one into its default folder in the user's home.
"""
import json, os, re, subprocess, sys, tempfile

# Upstream publishes no Linux arm64 Gmsh wheel, so that recipe builds Gmsh's library from source and installs the rest
# with --no-deps; its lock leaves gmsh out (an override whose marker never matches drops a dependency).
#
# EMerge's macOS sparse solver is a dependency of EMerge since 3.0.0b4, but its one wheel needs macOS 15, and circuitRF runs from
# macOS 13. A macOS lock is therefore resolved without it (so nothing else in it needs macOS 15 either) and gets it back as a line
# whose marker holds from Darwin 24, which is macOS 15: an older macOS installs the rest and runs on rslab, below. Those recipes
# install with --no-deps, as the Linux arm64 one does.
MARKED = {"emerge-aasds": ("0.4.0", 'platform_release >= "24"')}
DROPPED = {"linux-arm64": ["gmsh"], "macos-arm64": list(MARKED), "macos-x64": list(MARKED)}

# brief-emi-3 R-emi3-2c -- the wheel-only multi-threaded sparse solver, where it was measured to beat EMerge's
# single-threaded default and nothing faster comes without a compiler. 0.38.0 is the last release whose API EMerge
# 3.0.0 calls (1.0 renamed the factorisation settings, and EMerge's call then raises). On macOS arm64 it is what a Mac older
# than macOS 15 runs on; from macOS 15 EMerge picks its macOS sparse solver first.
EXTRA = {"macos-arm64": ["rslab==0.38.0"], "linux-arm64": ["rslab==0.38.0"]}

PLATFORMS = {
    # recipe platform: (uv --python-platform, platform-tag test)
    "macos-arm64":   ("aarch64-apple-darwin",     re.compile(r"^macosx_\d+_\d+_(arm64|universal2)$")),
    "macos-x64":     ("x86_64-apple-darwin",      re.compile(r"^macosx_\d+_\d+_(x86_64|universal2)$")),
    "linux-x64":     ("x86_64-unknown-linux-gnu", re.compile(r"^manylinux(1|2010|2014|_\d+_\d+)_x86_64$")),
    "linux-arm64":   ("aarch64-unknown-linux-gnu", re.compile(r"^manylinux(2014|_\d+_\d+)_aarch64$")),
    "windows-x64":   ("x86_64-pc-windows-msvc",   re.compile(r"^win_amd64$")),
}

def python_ok(py, abi, minor):
    pys, abis = py.split("."), abi.split(".")
    cp = f"cp3{minor}"
    for a in abis:
        if a == cp and cp in pys: return True
        if a == "abi3" and any(re.fullmatch(r"cp3(\d+)", p) and int(p[3:]) <= minor for p in pys): return True
        if a == "none" and any(p in ("py3", f"py3{minor}", cp) or re.fullmatch(r"py3\d*", p) for p in pys): return True
    return False

def wheel_ok(filename, plat_re, minor):
    if not filename.endswith(".whl"): return False
    parts = filename[:-4].split("-")
    py, abi, plat = parts[-3], parts[-2], parts[-1]
    if not python_ok(py, abi, minor): return False
    return any(p == "any" or plat_re.match(p) for p in plat.split("."))

def pypi_files(name, version):
    # curl, not urllib: a Python without a working certificate store is common, and this is a one-off.
    out = subprocess.run(["curl", "-sfL", f"https://pypi.org/pypi/{name}/{version}/json"], check=True, capture_output=True).stdout
    return {u["digests"]["sha256"]: u["filename"] for u in json.loads(out)["urls"]}

def main(uv, emerge, python, out):
    minor = int(python.split(".")[1])
    os.makedirs(out, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for name, (target, plat_re) in PLATFORMS.items():
            req = os.path.join(tmp, name + "-in.txt")
            open(req, "w").write("".join(f"{r}\n" for r in [f"emerge=={emerge}"] + EXTRA.get(name, [])))
            lock = os.path.join(tmp, name + ".txt")
            overrides = os.path.join(tmp, name + "-overrides.txt")
            open(overrides, "w").write("".join(f'{p} ; sys_platform == "never"\n' for p in DROPPED.get(name, [])))
            subprocess.run([uv, "pip", "compile", req, "--no-config", "--python-version", python, "--python-platform", target,
                            "--generate-hashes", "--no-annotate", "--no-header", "--only-binary", ":all:", "--no-python-downloads",
                            "--overrides", overrides, "-o", lock],
                           check=True, capture_output=True)
            text = open(lock).read().replace("\\\n", " ")
            lines = []
            for line in text.splitlines():
                line = line.split("#")[0].strip()
                if not line: continue
                spec, *rest = line.split()
                pkg, ver = re.split(r"==", spec.split(";")[0])
                hashes = [h.split(":", 1)[1] for h in rest if h.startswith("--hash=sha256:")]
                files = pypi_files(pkg, ver)
                keep = [h for h in hashes if h in files and wheel_ok(files[h], plat_re, minor)]
                if not keep: sys.exit(f"{name}: no installable wheel of {pkg}=={ver}")
                lines.append(f"{pkg}=={ver} " + " ".join(f"--hash=sha256:{h}" for h in keep))
            for pkg in DROPPED.get(name, []):
                if pkg in MARKED:
                    ver, marker = MARKED[pkg]
                    keep = [h for h, f in pypi_files(pkg, ver).items() if wheel_ok(f, plat_re, minor)]
                    if not keep: sys.exit(f"{name}: no installable wheel of {pkg}=={ver}")
                    lines.append(f"{pkg}=={ver} ; {marker} " + " ".join(f"--hash=sha256:{h}" for h in keep))
            open(os.path.join(out, name + ".txt"), "w").write("\n".join(lines) + "\n")
            print(f"{name}: {len(lines)} requirements, {sum(l.count('--hash') for l in lines)} hashes")

def splice(out, recipes, emerge):
    for name in list(PLATFORMS) + ["windows-arm64"]:
        path = os.path.join(recipes, f"emerge-{emerge}-{name}.json")
        if not os.path.exists(path): continue
        lines = [l for l in open(os.path.join(out, ("windows-x64" if name == "windows-arm64" else name) + ".txt")).read().splitlines() if l]
        recipe = json.load(open(path))
        step = next(s for s in recipe["steps"] if s["name"] == "Write the locked requirements")
        step["lines"] = [l for l in step["lines"] if l.startswith("#")] + lines
        with open(path, "w") as f:
            json.dump(recipe, f, indent=2, ensure_ascii=False); f.write("\n")
        print(f"{path}: {len(lines)} requirements")

if __name__ == "__main__":
    main(*sys.argv[1:5])
    if len(sys.argv) > 5: splice(sys.argv[4], sys.argv[5], sys.argv[2])
