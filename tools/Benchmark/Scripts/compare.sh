#!/bin/bash
# Usage: compare.sh <output name> <reference output name>
# Compares the translations of two outputs.sh runs byte for byte.
source "$(dirname "$0")/common.sh"
n=0
for f in "$BENCH/out-$2"/*.out; do
	cmp -s "$f" "$BENCH/out-$1/$(basename "$f")" || { echo "DIFF $(basename "$f")"; n=$((n+1)); }
done
echo "$1 vs $2: $n differences in $(ls "$BENCH/out-$2"/*.out | wc -l) cases"
grep -l "Exception" "$BENCH/out-$1"/*.err 2>/dev/null
[ $n -eq 0 ]
