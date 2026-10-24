#!/bin/bash
# Usage: bench-env.sh <result file> <bin name> <cases file> <variant>...
# One build, variants of environment variables: each variant is name=VAR1=a,VAR2=b (or name= for
# none). Three rounds, rotating the variant order; summarize with summary-n.js.
set -euo pipefail
source "$(dirname "$0")/common.sh"
R=$1; BIN="$BENCH/bin-$2"; CASES=$3; shift 3; V=("$@")
: > "$R"
cd "$BENCH"
while IFS='|' read -r name arguments; do
	[ -z "$name" ] && continue
	for round in 0 1 2; do
		n=${#V[@]}
		for ((i = 0; i < n; i++)); do
			v=${V[$(( (i + round) % n ))]}; variant=${v%%=*}; settings=${v#*=}
			envs=(); [ -n "$settings" ] && IFS=',' read -ra envs <<< "$settings"
			result=$(env "${envs[@]}" dotnet "$HARNESS" "$BIN" $arguments)
			echo "$name $variant $round $result" >> "$R"
		done
	done
done < "$CASES"
