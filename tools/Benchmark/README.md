# Bergamot benchmarks

Tools for measuring `Tarjamot` on a native machine, used for the
experiments in [X86-PERFORMANCE.md](../../docs/X86-PERFORMANCE.md).
Read its methodology sections before comparing builds; this file only explains the tools.

- `Harness` (`TarjamotHarness`): translation benchmark through the public API. One process per
  measurement: first translation, then warmup and timed translations, CPU time, allocations, live
  bytes and peak working set.
- `Microbenchmark` (`TarjamotMicrobenchmark`): internals on one thread: int8 products on the model
  shapes, model loading, output-layer conversion, matrix metadata sizes, attention loops, core
  count. The engine grants it `InternalsVisibleTo` since the commit that added these tools, so
  it works only with builds that include that commit; older builds throw `MethodAccessException`.
- `Scripts`: building copies of the engine, byte-for-byte output checks, alternating benchmark
  runs, summaries and profile analysis.
- `Results`: the raw measurements of the experiments.

Both programs take a build output directory (a copy of `tools/Translate/bin/Release/net10.0`)
and load the engine from it, so one harness binary compares any two builds.

## Setup

Windows with Git Bash (the scripts), PowerShell 7 (the corpus), Node.js (the summaries), and
`dotnet-trace` for profiles. From the repository root:

```bash
export TARJAMOT_BENCH=/c/Users/$USERNAME/tarjamot-bench   # work directory, keep it between sessions
dotnet build tools/Benchmark/Harness/TarjamotHarness.csproj -c Release
dotnet build tools/Benchmark/Microbenchmark/TarjamotMicrobenchmark.csproj -c Release
pwsh tools/Benchmark/Scripts/prepare-corpus.ps1 "$TARJAMOT_BENCH"
dotnet tools/Translate/bin/Release/net10.0/Translate.dll download cs ja
dotnet tools/Translate/bin/Release/net10.0/Translate.dll download zh-Hans en
dotnet tools/Translate/bin/Release/net10.0/Translate.dll download cs en --shortlist
```

Download the pivot before the shortlisted cs-en pair: downloading cs-ja also installs cs-en
without its shortlist and replaces an existing cs-en installation.

The models go to the user's local application data, where the harness looks for them; record
their hashes (see `Results/model-hashes.txt`) and do not refresh them between compared runs.
`prepare-corpus.ps1` fetches the current Wikipedia extracts; when an article has changed since
the recorded revision, it reports a different hash.

## Comparing two builds

```bash
S=tools/Benchmark/Scripts
git switch --detach <baseline commit> && $S/copy-build.sh baseline
git switch experiment/bergamot && $S/copy-build.sh candidate
$S/outputs.sh baseline baseline && $S/outputs.sh candidate candidate && $S/compare.sh candidate baseline
DOTNET_EnableAVX512=0 $S/outputs.sh candidate candidate-avx2    # and compare with the baseline's AVX2 outputs
$S/bench.sh "$TARJAMOT_BENCH/results.txt" baseline candidate      # about 15 minutes, keep the machine idle
node $S/summary.js "$TARJAMOT_BENCH/results.txt"
```

`copy-build.sh` builds the current working tree. Instruction-set variants: `DOTNET_EnableAVX512=0`
(AVX2; `DOTNET_EnableAVX512F=0` has no effect on .NET 10), with `DOTNET_EnableAVXVNNI=0` as well
(AVX2 without VNNI), `DOTNET_EnableAVX2=0` (portable kernel, `Vector128`) and
`DOTNET_EnableHWIntrinsic=0` (scalar). The harness prints the flags it sees; check them.

For tuning constants, make a temporary build that reads them from environment variables, check
that its outputs match with several settings (a mistake there once made products skip their work),
and run `bench-env.sh` with one variant per setting, then `summary-n.js`.

Microbenchmarks and profiles:

```bash
source $S/common.sh
dotnet $MICRO "$BENCH/bin-candidate" gemm
dotnet $MICRO "$BENCH/bin-candidate" load "$LOCALAPPDATA/Tarjamot/Models/cs-en/model.csen.intgemm.alphas.bin"
cmd //c "start /b /wait /affinity FF dotnet $MICRO $BENCH/bin-candidate cores"
dotnet-trace collect --profile cpu-sampling --format Speedscope -o "$BENCH/trace.nettrace" -- dotnet "$BENCH/bin-candidate/Translate.dll" cs en --input "$BENCH/article-cs.txt" --single-thread --benchmark 4
node $S/profile.js "$BENCH/trace.speedscope.json"                       # shares under TranslateBatch
node $S/profile.js "$BENCH/trace.speedscope.json" "" "MarianTranslator\.Attend\("   # call chains
DOTNET_JitDisasm='DotAvx512x4' DOTNET_JitStdOutFile=jit.asm dotnet "$BENCH/bin-candidate/Translate.dll" ...  # JIT code
```

## Results

Raw harness lines (`case build round key=value...`), summarized in X86-PERFORMANCE.md. Builds:
`baseline` = `e0efab33a13` (`422ccc9b50d` plus the handoff document); `expN` = the commit of
experiment N (`exp1` `218678d8f91`, `exp2a` `b88372555bc`, `exp3b` `3fbec68bb60`, `exp4`
`9f187565ae8`, `exp5` `9308320f1b3`, `exp6` `5a5fa814627`); `f2c` = `885c76a52fc`; `aff` = `exp3b`
plus the affinity fix (`59f7cd60ce6`); `tune` = `exp3b` and `tune2` = `f2c` with constants read
from environment variables (not committed); `exp6b` = `exp6` with the two-head values only for
head sizes 32 and 48.

| File | Content |
|---|---|
| `results-exp1.txt`, `results-exp1-recheck.txt` | Experiment 1: baseline / exp1, and six more Chinese and Czech `WithinProducts` processes |
| `results-exp2a.txt` | Experiment 2: exp1 / exp2a |
| `results-exp3b.txt`, `results-exp3b-avx2.txt` | Experiment 3: exp2a / exp3b, and with `DOTNET_EnableAVX512=0` |
| `results-exp4-thresholds.txt`, `results-exp4-caps.txt`, `results-exp4-confirm.txt` | Experiment 4: `WithinProducts` threshold and chunk-cap sweeps (`tune`) |
| `results-exp4-across.txt`, `results-exp4-isa.txt` | `AcrossSentences` worker counts (`tune`), AVX-512 / AVX2 (exp3b) |
| `results-exp4-affinity.txt`, `results-exp4.txt` | `AcrossSentences` under affinity `FF` (exp3b / aff), exp3b / exp4 |
| `results-exp5.txt` | Experiment 5: exp4 / exp5 |
| `results-exp6.txt`, `results-exp6-quick.txt`, `results-exp6-zh.txt` | Experiment 6: exp5 / exp6, a first check, and exp5 / exp6 / exp6b for zh-en |
| `results-f2-cold.txt` | First translations, exp6 / f2c, with the x86 and the portable kernel |
| `results-f4-batches.txt` | `AcrossSentences` batch-size sweep (`tune2`) |
| `corpus-hashes.txt`, `model-hashes.txt` | SHA-256 of the corpus and of the models used |
| `results-opt8.txt` | `a58f08e8b3a` / eight-row AVX-512 tiles, three processes per case; invalid overlapping measurements excluded |
| `results-vnni-avx2.txt` | `16a8804fa81` (`head`) / `VPDPBUSD` with exact correction (`vnni`, first version), `DOTNET_EnableAVX512=0` |
| `results-vnni2-check.txt` | `vnni` / final version (`vnni2`) with AVX2, and `head` / `vnni2` with AVX-512 |
| `micro-vnni2-avx2.txt` | `gemm` microbenchmark, `head` / `vnni2`, two runs each, `DOTNET_EnableAVX512=0` |
