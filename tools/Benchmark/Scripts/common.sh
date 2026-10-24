# Sourced by the other scripts. TARJAMOT_BENCH is the work directory with the corpus, the build
# copies (bin-*), outputs and results; it defaults to tarjamot-native-benchmark in the temp directory.
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
SCRIPTS="$REPO/tools/Benchmark/Scripts"
BENCH=${TARJAMOT_BENCH:-${TMPDIR:-/tmp}/tarjamot-native-benchmark}
HARNESS="$REPO/tools/Benchmark/Harness/bin/Release/net10.0/TarjamotHarness.dll"
MICRO="$REPO/tools/Benchmark/Microbenchmark/bin/Release/net10.0/TarjamotMicrobenchmark.dll"
