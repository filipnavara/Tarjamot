#!/bin/bash
# Usage: bench.sh <result file> <bin name A> <bin name B> [cases file]
# Runs every case in three fresh processes per build, alternating the order, one process at a
# time; summarize with summary.js. The default cases are cases-all.txt.
set -euo pipefail
source "$(dirname "$0")/common.sh"
R=$1; A="$BENCH/bin-$2"; C="$BENCH/bin-$3"; CASES=${4:-$SCRIPTS/cases-all.txt}
: > "$R"
cd "$BENCH"
while IFS='|' read -r name arguments; do
	[ -z "$name" ] && continue
	for round in 1 2 3; do
		if [ $((round % 2)) = 1 ]; then order="$A $C"; else order="$C $A"; fi
		for bin in $order; do
			result=$(dotnet "$HARNESS" "$bin" $arguments)
			echo "$name $(basename "$bin") $round $result" >> "$R"
		done
	done
done < "$CASES"
