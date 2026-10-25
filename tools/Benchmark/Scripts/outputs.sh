#!/bin/bash
# Usage: outputs.sh <bin name> <output name>
# Translates every correctness case with $BENCH/bin-<bin name>/Translate.dll into $BENCH/out-<output name>.
# Compare two runs with compare.sh. Set DOTNET_EnableAVX512=0 (AVX2) or DOTNET_EnableAVX2=0
# (portable) in the environment for the other kernels; the *-portable cases always use the
# portable kernel.
source "$(dirname "$0")/common.sh"
BIN="$BENCH/bin-$1"; OUT="$BENCH/out-$2"
mkdir -p "$OUT"
run() { name=$1; shift; dotnet "$BIN/Translate.dll" "$@" > "$OUT/$name.out" 2> "$OUT/$name.err" || echo "FAIL $name"; }
# The runtime switches that disable the x86 and the ARM kernel.
run_portable() { name=$1; shift; DOTNET_EnableAVX2=0 DOTNET_EnableArm64Dp=0 dotnet "$BIN/Translate.dll" "$@" > "$OUT/$name.out" 2> "$OUT/$name.err" || echo "FAIL $name"; }
for mode in "single:--single-thread" "within:" "across:--across-sentences"; do
	m=${mode%%:*}; f=${mode#*:}
	run email-$m cs en --input "$BENCH/email-cs.txt" $f
	run article-cs-$m cs en --input "$BENCH/article-cs.txt" $f
	run article-zh-$m zh-Hans en --input "$BENCH/article-zh.txt" $f
	run email-short-$m cs en --input "$BENCH/email-cs.txt" --shortlist $f
	run article-cs-short-$m cs en --input "$BENCH/article-cs.txt" --shortlist $f
	run html-$m cs en --input "$BENCH/email-cs.html" --html $f
	run pivot-$m cs ja --input "$BENCH/email-cs.txt" $f
done
run_portable email-portable cs en --input "$BENCH/email-cs.txt"
run_portable article-cs-portable cs en --input "$BENCH/article-cs.txt"
run_portable html-portable cs en --input "$BENCH/email-cs.html" --html
