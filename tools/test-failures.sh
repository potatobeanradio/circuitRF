#!/usr/bin/env bash
# Prints which tests failed in the most recent `dotnet test` run — name, message, and the assertion
# site — without re-running anything.
#
# Every runsettings in this repo registers the trx logger at a fixed filename, so each run overwrites
# tests/<Project>/TestResults/last-run.trx. That file is written by the test host itself, which is the
# whole point: a long run's console output gets piped, filtered or truncated, and then the only way to
# learn which of "Failed: 5" actually failed was to run the suite a second time. Reading the trx costs
# nothing and cannot be lost downstream.
#
#   tools/test-failures.sh              # every project that has a run on record, each with its date
#   tools/test-failures.sh tests/Ui.Tests/TestResults/last-run.trx
#
# Exit status is 1 if any reported run had a failure, so this also works as a gate in a script.
set -euo pipefail

# Portable on purpose: macOS ships bash 3.2 (no `mapfile`) and BSD find has no `-newermt`, so this
# collects files with a read loop and shows each one's timestamp instead of trying to filter by age.
# A root-level `dotnet test` writes one trx PER PROJECT, so reporting all of them is what makes a
# whole-repo run legible; a stale file is obvious from the date rather than silently dropped.
files=()
if [[ $# -gt 0 ]]; then
    files+=("$1")
else
    while IFS= read -r f; do
        [[ -n "$f" ]] && files+=("$f")
    done < <(find . -name last-run.trx -type f 2>/dev/null | sort)
fi

if [[ ${#files[@]} -eq 0 ]]; then
    echo "No last-run.trx found. Run 'dotnet test' first (any runsettings in this repo writes one)." >&2
    exit 1
fi

status=0
for trx in "${files[@]}"; do
    [[ -f "$trx" ]] || continue
    echo "── $trx   ($(date -r "$trx" '+%Y-%m-%d %H:%M:%S'))"
    python3 - "$trx" <<'PY' || status=1
import sys, xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(sys.argv[1]).getroot()

counters = root.find(".//t:ResultSummary/t:Counters", ns)
if counters is not None:
    a = counters.attrib
    print(f"   total={a.get('total')}  passed={a.get('passed')}  failed={a.get('failed')}\n")

failed = [r for r in root.findall(".//t:UnitTestResult", ns) if r.get("outcome") == "Failed"]
if not failed:
    print("   No failed tests in this run.\n")
    sys.exit(0)

for r in failed:
    print(f"FAILED  {r.get('testName')}   [{r.get('duration', '')}]")
    out = r.find("t:Output", ns)
    if out is None:
        print()
        continue
    msg = out.find("t:ErrorInfo/t:Message", ns)
    if msg is not None and msg.text:
        for line in msg.text.strip().splitlines():
            print(f"        {line}")
    trace = out.find("t:ErrorInfo/t:StackTrace", ns)
    if trace is not None and trace.text:
        # First frame in our own code is the assertion site; the rest is xunit plumbing.
        for line in trace.text.strip().splitlines():
            if "CircuitRF" in line:
                print(f"        {line.strip()}")
                break
    print()
sys.exit(1)
PY
done
exit $status
