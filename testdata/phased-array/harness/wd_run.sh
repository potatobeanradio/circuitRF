#!/bin/bash
# Usage: wd_run.sh <cem> <limitGB> <capSeconds> <log>
# Runs the CLI em verb with a memory/time watchdog on the CLI's own pid; logs peak RSS and elapsed.
CEM="$1"; LIMKB=$(python3 -c "print(int(float('$2')*1024*1024))"); CAP="$3"; LOG="$4"
CLI="${CRF_CLI:-$(cd "$(dirname "$0")/../../.." && pwd)/src/Cli/bin/Release/net10.0/CircuitRF.Cli}"
start=$(date +%s)
"$CLI" em "$CEM" > "$LOG" 2>&1 &
pid=$!
peak=0
while kill -0 $pid 2>/dev/null; do
  rss=$(ps -o rss= -p $pid 2>/dev/null | tr -d ' ')
  [ -n "$rss" ] && [ "$rss" -gt "$peak" ] && peak=$rss
  now=$(date +%s)
  if [ -n "$rss" ] && [ "$rss" -gt "$LIMKB" ]; then echo "WATCHDOG: RSS ${rss}KB over limit, killing" >> "$LOG"; kill -9 $pid; fi
  if [ $(( now - start )) -gt "$CAP" ]; then echo "WATCHDOG: time cap ${CAP}s, killing" >> "$LOG"; kill -9 $pid; fi
  sleep 1
done
wait $pid; rc=$?
echo "RESULT $(basename "$CEM") rc=$rc elapsed=$(( $(date +%s) - start ))s peakRSS=$(( peak / 1024 ))MB" | tee -a "$LOG"
