# Native x86 performance follow-up

Handoff baseline: `experiment/bergamot` at `422ccc9b50d` (2026-10-22).
Update this baseline to the revision actually tested when resuming work.
Completed experiments are recorded under [Results](#results).

## Status (2026-10-22)

All six experiments below and the follow-ups are done and on `experiment/bergamot`; nothing is
in progress. Every kept change produced byte-identical outputs with AVX-512, AVX2 and the portable
kernel, and the complete suite including the `ExternalServices` tests passes (781 passed, 66
ARM-only skipped). The tools, scripts and raw measurements are in
[tools/Benchmark](../tools/Benchmark/README.md).

Against the handoff baseline on the Ryzen 9 9950X (medians from different sessions, so
approximate):

| Case | Baseline | Now |
|---|---:|---:|
| Czech article, `None` | 968 ms | 844 ms |
| Czech article, `WithinProducts` | 562 ms | 447 ms |
| Czech article, `AcrossSentences` | 115 ms | 104 ms |
| Chinese article, `WithinProducts` | 1,267 ms | 1,039 ms |
| Chinese article, `AcrossSentences` | 726 ms | 596 ms |
| Email, `WithinProducts` | 25.5 ms | 21.6 ms |
| Pivot cs-ja email | 46.4 ms | 38.9 ms |
| First load of cs-en (x86 / portable kernel) | 216 / 198 ms | 72 / 71 ms |

| Commit | Change | Outcome |
|---|---|---|
| `218678d8f91` | 1. x86 quantization writes only the shifted activations | 1.5-4 MB less retained, time neutral |
| `b88372555bc` | 2. Panel loops reach tier 1 (no `stackalloc`), kernels inlined | 1-3.4% faster |
| `3fbec68bb60` | 3. Dot loops without per-row address math, two-row kernel | 2-5% faster, email and pivot 8-10% |
| `59f7cd60ce6` | 4. Windows core count respects the affinity mask | Restricted affinity: -40% CPU, +10% wall |
| `9f187565ae8` | 4. x86 `WithinProducts`: products from 2^22, chunks of 2^21, one per physical core | 5-9% faster |
| `9308320f1b3` | 5. No saturation metadata for x86 matrices | Loading twice as fast, 0.4-0.7 MB less |
| `5a5fa814627` | 6. Attention for two heads at a time | 4-7% faster on the articles |
| `885c76a52fc` | Follow-up: loading vectorized and optimized from the first call, no embedding metadata | First load halved again, portable kernel too |
| `ebc8fc18244` | Follow-up: encoder multi-query attention and batch sizes measured, not changed | |
| `16a8804fa81` | Eight-row AVX-512 tiles | Serial articles faster; `WithinProducts` CPU +8% |
| (this commit) | AVX2 + AVX-VNNI: `VPDPBUSD` with exact saturation correction | 15-29% faster on that path, outputs identical |

Corrections to this document's original plan: `DOTNET_EnableAVX512F=0` does not disable
AVX-512 on .NET 10 (use `DOTNET_EnableAVX512=0`), so earlier "AVX2" runs were AVX-512 runs.

Open, in rough order of expected value:

0. **512-bit `VPDPBUSD` with .NET 11.** `AvxVnni.V512` (in the .NET 11 previews, supported on
   Zen 5) would apply the AVX2 + AVX-VNNI change below to the AVX-512 kernels: 1.47-1.57 times the
   eight-row tile's throughput in a standalone microbenchmark. Needs the move to .NET 11.
1. **Other hardware.** Nothing here was measured on Intel (512-bit frequency effects, hybrid
   performance and efficiency cores, which `ProcessorTopology` counts as equal) or on ARM (the
   portable and ARM kernels kept their thresholds; loading changed for them too and was measured
   only with the portable kernel on x86). The x86-only constants, the AVX-512 dispatch and the
   physical-core policy need checking there first.
2. **`AcrossSentences` batch size.** One batch per worker was 13% faster for zh-en and 24% slower
   for cs-en, consistent with the 32 MB output layer of the large vocabulary exceeding the L3
   cache. A rule based on the output layer size needs more models and machines.
3. **Encoder attention for two queries per pass.** 1.5-1.8 times faster scores in a prototype
   (`TarjamotMicrobenchmark scores`), about 1% of translation time.
4. **Linux core count** ignores the affinity mask (sysfs lists all cores); intersect with
   `sched_getaffinity`. Deferred on request; macOS has no affinity API.
5. **`WithinProducts` allocations** rose 50-60% with the smaller x86 chunks (more `Parallel.For`
   calls); a pooled partitioner could remove them if they matter.

To resume: read the tool README, recreate a work directory with `prepare-corpus.ps1` (or reuse a
kept one), build the current branch tip as the baseline with `copy-build.sh`, and follow the
measurement rules below. Keep one experiment per commit and record it under Results.

## Work already upstream

All five sequential memory/performance changes from `codex/bergamot-performance`
are merged: streaming model loading (`3e9dd7cb436`), bounded idle workers and model
eviction (`ff4666d4b2b`), fused output argmax (`54fffd9ef43`), reusable decoder
contexts (`c132f530943`), and sentence token reuse (`326db6b581a`). Upstream also
fixed failed-load retries and idle-worker eviction/timer behavior. Preserve those fixes.

The original deferred task to implement an x86 kernel has been superseded by
`ccec252c4d8`. It adds AVX2/AVX-512 kernels, wider attention, panel-level argmax
filtering, and physical-core scheduling for `AcrossSentences`. The
[README x86 section](README.md#x86-kernel) records native Ryzen 9 9950X results and
the original comparison methodology. Those are existing upstream measurements;
this handoff adds no benchmark results. Earlier ARM or Rosetta runs cannot establish
native x86 speedups.

## Source map and correctness constraints

- [Int8Gemm.cs](Engine/Int8Gemm.cs): dispatch, activation buffers, quantization,
  parallel thresholds, and multiplication. The x86 path retains both signed
  `Values` and unsigned `Shifted`; the portable path also uses `Widened`.
- [Int8Gemm.X86.cs](Engine/Int8Gemm.X86.cs): 16-output panels with four adjacent
  inputs, four-row weight reuse, AVX-512BW/AVX2 products, scoring, and argmax.
  Shortlists use the row-major path.
- [QuantizedMatrix.cs](Engine/QuantizedMatrix.cs): streaming packing, row sums,
  and sparse saturation metadata. Each linear matrix keeps one weight layout.
- [MarianMath.cs](Engine/MarianMath.cs): attention, normalization, and softmax.
- [ProcessorTopology.cs](Engine/ProcessorTopology.cs): physical-core worker count.
  `WithinProducts` still uses logical processors and work thresholds.
- [IdleWorkerPool.cs](Engine/IdleWorkerPool.cs) and
  [DecoderContextBuffers.cs](Engine/DecoderContextBuffers.cs): retained scratch
  accounting and budgets. Include new buffers in retained-byte accounting.

Preserve these properties when changing arithmetic or layouts:

1. Quantize activations with nearest-even rounding and clamp to `[-127, 127]`.
   The unsigned shift is **127**, not 128. Weights may include -128.
2. Saturate each adjacent unsigned-activation/signed-weight pair to int16 before
   accumulating into int32. `PMADDUBSW` already produces the Int8Shift pair sums;
   x86 must not add the row-sum shift or sparse saturation correction again.
   Current AVX-VNNI operates on the resulting signed **word** pairs. Replacing this
   sequence with an unsaturated byte-dot instruction changes the result.
3. Keep the float operation order in prepared bias and final score conversion.
   A different reduction order or fused operation can change token selection.
4. Argmax uses strict `>` and preserves the earliest candidate on ties, excludes
   UNK, ignores NaNs, and returns -1 if no score beats negative infinity.
   Parallel partition merging must retain vocabulary order.
5. Cover partial panels, odd input tails, selected rows, saturation, and accumulator
   overflow behavior. Do not reinterpret an already packed matrix with a different
   panel width: `Pack` does not repack an existing packed matrix.
6. Keep `TarjamotPlatformMath` disabled for equivalent comparisons. HTML now uses
   attention alignments; attention changes require markup and pivot coverage as
   well as plain-text output checks.

## Experiments to run one at a time

These are hypotheses, not promised improvements. Profile the current baseline
first and keep a change only when measurements support it (a memory saving alone
can justify a change even when translation latency is neutral).

| Priority | Experiment | Evidence needed |
|---|---|---|
| 1 | Quantize directly into x86 `Shifted`, avoiding the signed activation buffer and second byte pass. Audit all callers before making `Values` optional on x86. | Bit-identical rounding at boundaries; lower allocated/retained scratch; quantization and whole-translation timings. Preserve ARM/portable contracts and buffer accounting. |
| 2 | Inspect JIT assembly around the stack buffer used between x86 dot products and scoring/argmax. If stores and reloads survive, try passing vector accumulators directly to the consumer. | Disassembly showing a real eliminated round trip; separate single-row output projection and multi-row timings; no register-spill regression. |
| 3 | Tune AVX2 single-row accumulator dependency chains and panel sizes using actual model shapes. | Compare 1-row decoding with 4-row and larger batches, full vocabulary and shortlists. Include cache traffic and packed weight size; avoid permanent duplicate weight layouts. |
| 4 | Measure AVX-512 versus AVX2 and product-parallel thresholds on each target CPU. Check physical/logical core choices and hybrid CPUs. | Wall and CPU time across modes, small-email latency and long-input throughput, actual affinity/core availability. A Zen 5 result does not establish the right dispatch on Intel. |
| 5 | Audit whether x86-only matrices can omit unused sparse saturation metadata. Row sums remain needed for prepared bias. | Exact loader/projection tests, model live bytes and load peak; check portable fallback and shared matrix users before omitting anything. |
| 6 | Re-profile attention after kernel changes; optimize only the remaining measured work. Wider Vector256/Vector512 attention is already upstream. | Same summation order, identical alignment/HTML output, and a whole-translation benefit beyond microbenchmark noise. |

## Native machine setup

Use a native Intel/AMD x64 process, an idle machine, and the same SDK/runtime,
power policy, GC settings, and tiering settings for baseline and candidate.
Record CPU model, physical/logical cores, OS, process architecture, .NET version,
memory, power policy, affinity, and any runtime overrides. In a small diagnostic
harness print `Avx2.IsSupported`, `AvxVnni.IsSupported`, `Avx512BW.IsSupported`, and
`Vector512.IsHardwareAccelerated`; verify the intended path for every variant.

The following PowerShell commands run from the repository root. Build baseline
and candidate in separate checkouts so rebuilding one cannot overwrite the other.
Use the DLL path printed by the build if local output settings differ.

```powershell
dotnet --info
dotnet build tools/Translate/Translate.csproj -c Release '/bl:bergamot-build-{}.binlog'
dotnet test tests/Tarjamot.Tests/Tarjamot.Tests.csproj -c Release --filter 'TestCategory!=ExternalServices' '/bl:bergamot-tests-{}.binlog'

$translateDll = 'tools/Translate/bin/Release/net10.0/Translate.dll'
$benchmarkRoot = Join-Path ([IO.Path]::GetTempPath()) 'tarjamot-native-benchmark'
$models = Join-Path $benchmarkRoot 'models'
New-Item -ItemType Directory -Force $benchmarkRoot | Out-Null
dotnet $translateDll download cs en --shortlist --models $models
dotnet $translateDll download zh-Hans en --shortlist --models $models
dotnet $translateDll download cs ja --models $models
```

Prepare fixed UTF-8 files for a short Czech email, a multi-thousand-word Czech
article, a long Chinese article, and HTML containing inline tags, entities, and
multiple sentences. Record their SHA-256 hashes, source, and lengths; retain the
exact files with the results. Record model versions and file hashes too. Download
once and use the same directory for both revisions. Do not depend on temporary
artifacts from the earlier ARM session or silently refresh models between runs.

After saving the email as `$benchmarkRoot/email-cs.txt`:

```powershell
$inputFile = Join-Path $benchmarkRoot 'email-cs.txt'
dotnet $translateDll cs en --models $models --input $inputFile --single-thread --benchmark 20
dotnet $translateDll cs en --models $models --input $inputFile --benchmark 20
dotnet $translateDll cs en --models $models --input $inputFile --across-sentences --benchmark 20
```

`--input` avoids Windows console encoding ambiguities. The tool prints the first
translation to stdout, and cold elapsed time plus warm wall/CPU averages to
stderr. Save both streams. Its cold timer includes translation, model loading,
and associated JIT work, but excludes process startup. Its warm loop starts after
only one translation; for a rigorous steady-state comparison use a harness with
at least two seconds of untimed warmup and enough measured iterations to stabilize
tiering. Do not mix those harness results with CLI warm averages without labeling
the difference.

## Variants and validation

Run each case with the default kernel on baseline and candidate. `--portable`
is a correctness/fallback diagnostic, not the performance baseline for a new x86
optimization. In fresh shell sessions, repeat with these runtime feature limits:

| Variant | Environment setting before starting dotnet |
|---|---|
| Default | No hardware-intrinsic overrides |
| AVX2 on an AVX-512 host | `$env:DOTNET_EnableAVX512 = '0'` |
| AVX2 without VNNI | `$env:DOTNET_EnableAVX512 = '0'; $env:DOTNET_EnableAVXVNNI = '0'` |
| Portable dispatch | `$env:DOTNET_EnableAVX2 = '0'` |
| Scalar fallback | `$env:DOTNET_EnableHWIntrinsic = '0'` |

These are runtime diagnostics: verify the reported support flags instead of
assuming an override took effect. On .NET 10.0.13, `DOTNET_EnableAVX512F=0` has no
effect (`Avx512BW.IsSupported` and `Vector512.IsHardwareAccelerated` stay true), so
runs labeled AVX2 with that setting, including experiments 1 and 2 below and the
earlier README results, used the AVX-512 kernel. `DOTNET_EnableAVX512=0` disables
it; `DOTNET_PreferredVectorBitWidth=256` also selects the AVX2 kernel, because it
turns off `Vector512.IsHardwareAccelerated`. Test AVX2 without VNNI on suitable hardware or
with an override verified against the installed runtime. Keep feature limits out
of subsequent default runs; using a separate shell for each variant avoids leaks.

Run the non-external suite for each supported feature variant. Inspect skips:
tests for unsupported hardware do not validate that kernel. Before accepting a
candidate, also run the complete suite (the `ExternalServices` tests download
models and require network access):

```powershell
dotnet test tests/Tarjamot.Tests/Tarjamot.Tests.csproj -c Release '/bl:bergamot-full-tests-{}.binlog'
```

Include `Int8GemmTest`, `OutputProjectionTest`, `MarianMathTest`, model loading,
retention, and decoder-buffer tests in regression review. Compare actual output
bytes baseline versus candidate for both languages, all three parallel modes,
shortlist on/off, default/portable kernels, HTML, and a Czech-to-Japanese pivot.
Use `--html` for one fragment or `--html-json` with a JSON array of fragments.
If attention changes, compare alignment data in tests as well as rendered text.

## Measurement and acceptance

- Run one benchmark process at a time; alternate baseline/candidate order across
  at least three fresh processes per case. Report individual runs and medians,
  including spread. Investigate differences near the noise floor.
- Separate cold first-use, steady-state translation, and concurrent/burst cases.
  Include short email, long Czech, long Chinese, shortlist, HTML, and pivot.
  Exercise `None`, `WithinProducts`, and `AcrossSentences` explicitly.
- Report wall time and process CPU time together. A lower wall time obtained by
  using more CPU is a tradeoff, not an unqualified efficiency improvement.
- Measure allocations with a process-wide counter in a harness; thread-local
  allocation counters miss parallel work. Measure live managed bytes after
  quiescence/GC separately from cumulative allocations. Do not force GC inside
  timed translation loops.
- Record OS peak working set/RSS for a fresh process and explain the platform's
  metric. Measure model loading separately from warm scratch retention. Include
  oversized input followed by idle time and worker bursts to check pool budgets.
  Retained managed memory, peak RSS, and allocation volume are different metrics.
- For kernel microbenchmarks, include actual dimensions and 1/4/many activation
  rows, packed and row-major paths, tails, and shortlists. Inspect JIT assembly
  before attributing a win to instruction counts. Confirm microbenchmark gains
  in translation before accepting extra complexity for speed alone.
- Keep one experiment per commit with baseline/candidate hashes, commands,
  runtime/CPU flags, corpus/model hashes, correctness results, and raw measurements.
  Record unsuccessful experiments too. Preserve measured memory wins even when
  speed is neutral, provided latency and correctness remain acceptable.

## Results

### Environment and corpus

AMD Ryzen 9 9950X (Zen 5, 16 cores, 32 logical processors, AVX2, AVX-VNNI, AVX-512BW,
`Vector512` accelerated), 94 GB, Windows 11 Pro 10.0.29683, x64 process, .NET 10.0.13
(SDK 10.0.402), Release, default GC and tiering, no affinity. Baseline: `e0efab33a13`
(`422ccc9b50d` plus this document).

| Input | Bytes | SHA-256 (prefix) | Source |
|---|---:|---|---|
| `article-cs.txt` | 22,379 | `e22e3db95a66b8fb` | Czech Wikipedia "Karel IV.", plain-text extract without headings, first 3,147 words |
| `article-zh.txt` | 30,852 | `dadfb0dab49905ea` | Chinese Wikipedia "长城" (zh-Hans), without headings, 10,689 non-whitespace characters |
| `email-cs.txt` | 703 | `944f0545e9459b14` | First 108 words of `article-cs.txt` |
| `email-cs.html` | — | `1f5dcdb0dc31d2c2` | Hand-written Czech email: `<b>`, `<i>`, `<a>`, `<em>`, `<span>`, `<br>`, `&amp;`, `&lt;`, `&nbsp;`, as `innerHTML` writes them |

Models (Mozilla v3.0, unchanged between runs): cs-en `c815273b31074492` (shortlist
`eba6bd9575251525`), zh-Hans-en `3535442962ec8f4a`, en-ja `59ae659f9bb63e4f`.

The measurements use a small harness that loads `Tarjamot.dll` from a
given build output. It prints the active instruction sets, then per process: cold first
translation (including model loading and JIT), two seconds of untimed warmup, then the median
of N timed translations, process CPU time per translation, process-wide allocated bytes per
translation (`GC.GetTotalAllocatedBytes(true)`), live managed bytes after a full GC with the
translator idle, and the peak working set. Process CPU time on Windows has a 15.6 ms
resolution, so CPU times for the email cases are coarse. Each case ran three fresh processes
per binary, alternating the order.

### 1. Quantize directly into `Shifted` (kept)

On x86, `Quantize` writes `round(x * alpha)` clamped to [-127, 127], plus 127, straight into
`Shifted`; it no longer fills `Values` or runs a second byte pass. Adding 127 to the int32
before taking the low byte gives the same bits as the former wrapping byte addition for every
input, NaN included. No x86 code path read `Values` (only the ARM and portable kernels, and the
ARM/portable saturation correction, do). The tests now compute their reference quantization
from the float inputs instead of reading `Values`.

Profile before the change (Czech article, `None`, `dotnet-trace` CPU sampling): int8 dot
kernels 58% of translation time, full-vocabulary argmax 27% inclusive, attention 9%,
quantization 1.5%. A latency gain was therefore not expected.

Correctness: the non-external suite passed with the default kernel (622 passed, 56 ARM-only
skipped), `DOTNET_EnableAVX512F=0` (622), `DOTNET_EnableAVX2=0` (566, 112 skipped) and
`DOTNET_EnableHWIntrinsic=0` (566). All 24 output cases were byte-identical to the baseline
with AVX-512, with `DOTNET_EnableAVX512F=0` and with `DOTNET_EnableAVX2=0`: email, both articles,
shortlisted email and article, HTML and Czech-to-Japanese pivot in all three modes, plus
`--portable` email, article and HTML. The `DOTNET_EnableAVX512F=0` runs were AVX-512 runs (see
above); the AVX2 kernel was validated for this change together with experiment 3.

Medians of three processes, baseline / candidate:

| Case | Wall ms | Live managed MB | Peak working set MB |
|---|---:|---:|---:|
| Email, `None` | 36.8 / 37.2 | 51.6 / 51.3 | 119.3 / 118.9 |
| Email, `WithinProducts` | 25.5 / 25.1 | 51.6 / 51.3 | 118.1 / 117.9 |
| Email, `AcrossSentences` | 15.1 / 15.2 | 53.1 / 52.8 | 120.5 / 121.1 |
| Email, shortlist | 21.9 / 21.8 | 56.4 / 56.1 | 126.2 / 126.3 |
| HTML email | 11.9 / 11.6 | 48.4 / 48.3 | 111.9 / 111.9 |
| Pivot cs-ja email | 46.4 / 46.3 | 116.6 / 116.0 | 192.3 / 192.0 |
| Czech article, `None` | 967.5 / 962.1 | 88.2 / 86.7 | 197.1 / 192.1 |
| Czech article, `WithinProducts` | 561.5 / 558.6 | 88.2 / 86.7 | 207.7 / 202.7 |
| Czech article, `AcrossSentences` | 115.4 / 115.8 | 147.9 / 143.9 | 322.6 / 312.8 |
| Chinese article, `None` | 3,064 / 3,064 | 143.3 / 141.3 | 242.3 / 227.3 |
| Chinese article, `WithinProducts` | 1,267 / 1,289 | 143.3 / 141.3 | 250.7 / 239.6 |
| Chinese article, `AcrossSentences` | 726 / 717 | 219.2 / 217.2 | 626.3 / 659.4 |

The Chinese `WithinProducts` difference did not repeat: six more processes per binary gave
1,291 / 1,297 ms (ranges 1,283-1,292 and 1,286-1,311), and the Czech article 570 / 570 ms.
Wall time is neutral. Each worker retains one activation buffer less (1.5 MB for the Czech
article, 2 MB for the Chinese, 4 MB across the `AcrossSentences` workers), and the peak working
set of the long serial and `WithinProducts` runs fell by 5-15 MB. The Chinese `AcrossSentences`
peak varies with batch scheduling (its per-process peaks spread by more than the difference).

### 2. JIT assembly around the panel sums (kept: tier 1 for the consumers)

Listings were taken with `DOTNET_JitDisasm` and `DOTNET_JitStdOutFile` while translating the
Czech article (`--single-thread --benchmark 3`).

The dot kernels themselves were fine: tier-1 `DotAvx512x4` keeps the four accumulators in
`zmm1`-`zmm4` and stores them once after the loop, four `vmovups zmm` per 128-512 iterations.
The cost was in the consumers. `MultiplyX86`'s `ComputeRange` and `ArgMaxX86Range` used
`stackalloc` for the sums, and OSR does not support `stackalloc`, so the JIT switched both
methods from tier 0 to FullOpts ("Tier-0 switched to FullOpts", synthesized PGO only), and they
were never rejitted at tier 1. In that code every 16-column panel made three real calls:
`LoadBias` (not inlined because of its own `stackalloc`, returning its tuple through memory), the
dot kernel, and the capturing `Consider` local function with its vector arguments spilled to the
stack. The upper halves of `ymm6`/`ymm7` were saved and restored around each call
(`vextractf128`/`vinsertf128`, as the Windows ABI preserves only the low 128 bits).

The change: the sums are an `[InlineArray]` local instead of `stackalloc`; `LoadBias`,
`StoreX86` and `Consider` keep only the full-panel path and the rejection test inline, with
partial panels and lane-by-lane winner updates in separate methods. Both consumers now go
through tier 0, OSR and tier 1 with dynamic PGO, which inlines `DotAvx512x4`/`DotAvx512x1`,
`LoadBias`, the scoring and the panel test (40 inlinees in `ArgMaxX86Range`); only the partial-
panel helpers remain calls. Arithmetic and its order are unchanged.

The sums still go through the stack in tier 1: four `zmm` stores, then two `vcvtdq2ps ymm, [mem]`
loads and a slice bounds check per row, about 30 instructions per four-row step against roughly
3,200 in the dot loop at width 512. Passing the accumulators directly would need separate
AVX-512 and AVX2 result shapes for an upper bound of about 0.5%, so it was not attempted.

Correctness: suite passed with AVX-512 (622), `DOTNET_EnableAVX512F=0` (622) and
`DOTNET_EnableAVX2=0` (566); all 24 outputs byte-identical with AVX-512 and with
`DOTNET_EnableAVX512F=0`, which was AVX-512 as well. The AVX2 kernel was validated together with
experiment 3.

Wall ms, medians of three processes, experiment 1 / this change (per-process medians):

| Case | Wall ms | Per process |
|---|---:|---|
| Email, `None` | 34.1 / 33.0 | 41.6, 34.1, 34.0 / 39.6, 33.0, 32.9 |
| Email, `WithinProducts` | 24.1 / 23.4 | 23.6, 24.1, 25.3 / 22.6, 25.1, 23.4 |
| Email, `AcrossSentences` | 15.1 / 14.7 | 14.8, 15.1, 15.3 / 14.9, 14.4, 14.7 |
| Email, shortlist | 21.9 / 22.0 | 21.9, 21.9, 21.9 / 22.0, 22.2, 21.9 |
| HTML email | 11.5 / 11.3 | 11.5, 11.5, 11.4 / 11.3, 11.2, 11.4 |
| Pivot cs-ja email | 46.5 / 45.6 | 46.8, 46.3, 46.5 / 45.8, 45.5, 45.6 |
| Czech article, `None` | 959.5 / 937.8 | 959.5, 962.5, 955.5 / 926.2, 940.1, 937.8 |
| Czech article, `WithinProducts` | 558.9 / 552.8 | 558.0, 563.9, 558.9 / 550.0, 554.4, 552.8 |
| Czech article, `AcrossSentences` | 117.5 / 113.5 | 117.7, 116.4, 117.5 / 113.0, 113.8, 113.5 |
| Chinese article, `None` | 3,090 / 3,017 | 3,104, 3,090, 3,084 / 3,001, 3,017, 3,026 |
| Chinese article, `WithinProducts` | 1,285 / 1,279 | 1,280, 1,287, 1,285 / 1,280, 1,269, 1,279 |
| Chinese article, `AcrossSentences` | 712 / 703 | 712, 717, 711 / 703, 709, 701 |

Long inputs became 1-3.4% faster, with non-overlapping process ranges for both serial articles,
the Czech `AcrossSentences` run and the pivot; process CPU time fell by similar amounts (Czech
`AcrossSentences` 1,500 / 1,422 ms). The shortlisted email, which uses the row-major kernel, is
unchanged. Allocations, live bytes and peak working set did not change beyond the usual
`AcrossSentences` scheduling variation (Chinese article 72.5 / 81.0 MB allocated per run, peaks
656 / 647 MB). The tier-1 dot loop
still computes each row's address with `lea`/`movsxd` per broadcast; that belongs to
experiment 3.

### 3. Dot loop tuning on the model shapes (kept: address math and a two-row kernel)

Shapes, from a temporary instrumented build (not committed) that counted products by kind,
width, outputs and rows while translating the corpus with `WithinProducts`:

| Model | Width | FFN | Vocabulary | Largest shares of int8 work |
|---|---:|---:|---:|---|
| cs-en | 384 | 1,536 | 32,000 | Email: argmax 5-8 rows 24%, encoder 65-256 rows 41%, decoder 5-8 rows 14%. Article: argmax 17-64 rows 29%, encoder >256 rows 38%, decoder 17-64 rows 17% |
| zh-en | 512 | 2,048 | 64,000 | Article: argmax 17-64 rows 36%, 9-16 rows 14%, encoder >256 rows 31% |

Products of one row are rare (2% of the Chinese article's work). Batches of 5-8 rows matter
for short texts, and with four-row steps their last one to three rows went through the
single-row kernel.

A single-thread microbenchmark (in the benchmark directory, not committed) calls
`Int8Gemm.Multiply` and `MultiplyArgMax` with packed random weights of these shapes after 0.3 s
of warmup and reports GMAC/s. Before this change, AVX-512 products of 4 or more rows reached
295-320 GMAC/s, about 85% of the two 512-bit multiply pipes at about 5.7 GHz (each four-row step
issues 8 multiply micro-ops for 256 multiply-adds); 6 rows reached 255-275, and the argmax
220-285. The tier-1 four-row loop had 29 instructions per step for those 8 multiplies: besides
the vector work, a `shl`/`movsxd` for the weight offset and an `lea`/`movsxd` for each row.

Changes, applied to the AVX-512 and AVX2 kernels:

1. Address math: each kernel keeps one reference per activation row, advances the panel
   reference by 64 bytes and counts with a `nuint`. The four-row AVX-512 step is now 21
   instructions: the weight load, four broadcasts from memory, eight multiplies, four adds and
   the loop control.
2. A two-row kernel: after the four-row steps, two remaining rows share each weight load, and
   only a last odd row uses the single-row kernel.

Not changed: the accumulator chains (the only loop-carried dependency is a one-cycle `vpaddd`
per accumulator, and the single-row kernel already alternates two), the panel width (16 columns
are one 512-bit vector; wider panels would only save broadcast loads, and five loads per eight
multiplies are below the load ports' limit) and AVX-512 VNNI (`VPDPWSSD` would merge the add
into the multiply, which leaves the multiply micro-ops, the bound, unchanged; `VPDPBUSD` does not
saturate pairs to int16 and changes the results).

Microbenchmark, GMAC/s, two runs each before / after:

| Kernel, shape | 1 row | 4 rows | 6 rows | 42 rows | 300 rows |
|---|---|---|---|---|---|
| AVX-512, cs FFN 384 x 1,536 | 180, 175 / 185, 182 | 300, 304 / 305, 306 | 262, 262 / 300, 305 | 298, 300 / 306, 307 | 303, 304 / 310, 305 |
| AVX-512, zh FFN 2,048 x 512 | 162, 163 / 173, 169 | 313, 313 / 321, 319 | 265, 268 / 311, 309 | 301, 300 / 317, 315 | 313, 310 / 321, 321 |
| AVX-512, cs argmax 384 x 32,000 | 76, 96 / 139, 150 | 222, 251 / 308, 313 | 214, 235 / 306, 310 | 261, 277 / 311, 312 | - |
| AVX-512, zh argmax 512 x 64,000 | 74, 102 / 93, 113 | 225, 250 / 216, 263 | 214, 230 / 265, 260 | 260, 254 / 268, 268 | - |
| AVX2, cs FFN 384 x 1,536 | 100, 104 / 119, 119 | 171, 173 / 172, 172 | 139, 138 / 172, 172 | 166, 168 / 172, 172 | 172, 172 / 172, 172 |
| AVX2, cs argmax 384 x 32,000 | 97, 101 / 111, 113 | 169, 171 / 171, 172 | 133, 137 / 172, 172 | 162, 165 / 170, 171 | - |
| AVX2, zh argmax 512 x 64,000 | 69, 78 / 77, 83 | 156, 149 / 154, 155 | 126, 130 / 157, 158 | 155, 157 / 164, 163 | - |

The address math alone gave 1-3% on the plain products and most of the argmax gain for 4 and
16 rows (the argmax inlines the kernels into a larger loop, where the freed registers and
instructions matter more); the two-row kernel gave 6 rows 12-15% (AVX-512) and 25% (AVX2). The
single-row argmax of the large vocabularies streams 12-32 MB of weights per step and varies
with memory bandwidth.

Correctness: new test cases for 2 and 6 rows in `MatchesIntgemmSemantics` and for 2 and 6
rows in `WinnersMatchMultiplyIncludingSaturationAndTails` (698 passed, 64 ARM-only skipped).
The suite passed with the default kernel, `DOTNET_EnableAVX512=0` (AVX2 with VNNI, verified:
`Avx512BW` and `Vector512` acceleration false), `DOTNET_EnableAVX512=0` with
`DOTNET_EnableAVXVNNI=0`, `DOTNET_EnableAVX2=0` (634) and `DOTNET_EnableHWIntrinsic=0` (634). All 24
outputs were byte-identical to the baseline (`e0efab33a13`) with AVX-512, with AVX2 and with AVX2
without VNNI, and the baseline's AVX2 outputs matched its AVX-512 outputs. As this build
contains experiments 1 and 2, this also validates their AVX2 paths.

Translation, medians of three processes, experiment 2 / this change (wall ms, per process):

| Case | Wall ms | Per process |
|---|---:|---|
| Email, `None` | 38.3 / 35.0 | 38.3, 38.5, 38.0 / 35.0, 35.6, 34.1 |
| Email, `WithinProducts` | 27.3 / 24.5 | 25.8, 28.2, 27.3 / 24.2, 24.5, 25.6 |
| Email, `AcrossSentences` | 17.9 / 16.9 | 19.9, 17.9, 16.3 / 19.1, 16.9, 15.6 |
| Email, shortlist | 21.9 / 20.6 | 21.6, 22.3, 21.9 / 20.6, 22.3, 20.6 |
| HTML email | 11.5 / 11.1 | 11.5, 13.1, 11.3 / 15.3, 11.1, 10.8 |
| Pivot cs-ja email | 49.3 / 44.4 | 49.5, 47.5, 49.3 / 43.4, 45.2, 44.4 |
| Czech article, `None` | 932 / 890 | 932, 931, 936 / 890, 884, 897 |
| Czech article, `WithinProducts` | 536 / 525 | 551, 535, 536 / 521, 525, 528 |
| Czech article, `AcrossSentences` | 112.8 / 107.0 | 110.3, 112.8, 114.0 / 107.0, 104.4, 107.9 |
| Chinese article, `None` | 2,986 / 2,867 | 3,028, 2,986, 2,984 / 2,867, 2,896, 2,832 |
| Chinese article, `WithinProducts` | 1,268 / 1,219 | 1,279, 1,266, 1,268 / 1,236, 1,215, 1,219 |
| Chinese article, `AcrossSentences` | 700 / 679 | 701, 698, 700 / 681, 679, 667 |

With `DOTNET_EnableAVX512=0` (AVX2 kernel), three processes each: email `WithinProducts`
34.0 / 30.5 ms, Czech article `None` 1,515 / 1,453 ms, Chinese article `WithinProducts`
1,803 / 1,760 ms.

The long serial and `WithinProducts` cases became 2-5% faster and the pivot and email 8-10%,
with non-overlapping process ranges except for the email `AcrossSentences`, shortlist and HTML
cases. Serial CPU time fell with wall time (Czech article 938 / 891 ms, Chinese 3,016 /
2,875 ms). The shortlisted email's output layer uses the unchanged row-major kernel, so its
difference is not attributed to this change. Allocations, live bytes and peak working set did
not change beyond `AcrossSentences` scheduling variation. On this Zen 5 host, the AVX2 kernel
is about 1.6 times slower than AVX-512 in serial translation.

### 4. Dispatch, product-parallel thresholds and core counts

Variants of the thresholds, the chunk cap and the `AcrossSentences` worker count came from a
temporary build (not committed) that read them from environment variables; its outputs were
byte-identical to the baseline with the defaults and with three overrides. Each table row is
the median of three processes, rotating the variant order.

**AVX-512 versus AVX2** (experiment 3 build, default versus `DOTNET_EnableAVX512=0`), wall ms,
CPU ms:

| Case | AVX-512 | AVX2 |
|---|---|---|
| Email, `None` | 30.4, 31 | 47.8, 47 |
| Email, `WithinProducts` | 21.9, 31 | 30.0, 31 |
| Email, `AcrossSentences` | 14.4, 47 | 19.1, 63 |
| Email, shortlist | 21.4, 31 | 28.0, 31 |
| Czech article, `None` | 902, 906 | 1,433, 1,438 |
| Czech article, `WithinProducts` | 523, 1,172 | 773, 1,969 |
| Czech article, `AcrossSentences` | 104, 1,344 | 147, 1,828 |
| Chinese article, `None` | 2,852, 2,891 | 4,501, 4,547 |
| Chinese article, `WithinProducts` | 1,230, 4,859 | 1,739, 7,531 |
| Chinese article, `AcrossSentences` | 670, 8,109 | 749, 9,109 |

AVX-512 is faster with less CPU time in every mode, so the dispatch stays. This is Zen 5, which
executes 512-bit multiplies at full width; it does not establish the choice for Intel CPUs,
where 512-bit code may lower the clock frequency.

**`WithinProducts` thresholds** (`MinParallelWork` / `MinChunkWork` as powers of two, chunk cap),
wall ms / CPU ms. The first sweep:

| Variant | Email | Pivot | Czech article | Chinese article |
|---|---|---|---|---|
| 24 / 23, 4 x logical (before) | 21.9 / 31 | 42.4 / 78 | 523 / 1,094 | 1,238 / 4,594 |
| 22 / 21, 4 x logical | 21.8 / 47 | 40.3 / 125 | 484 / 1,500 | 1,118 / 5,547 |
| 23 / 22, 4 x logical | 21.4 / 31 | 40.7 / 78 | 493 / 1,297 | 1,162 / 5,219 |
| 25 / 24, 4 x logical | 27.5 / 47 | 48.0 / 63 | 547 / 1,156 | 1,388 / 4,688 |
| 26 / 25, 4 x logical | 28.6 / 31 | 53.8 / 63 | 587 / 1,203 | 1,467 / 4,422 |
| 24 / 23, logical (32) | 22.4 / 31 | 42.4 / 78 | 529 / 1,141 | 1,251 / 4,828 |
| 24 / 23, physical (16) | 22.4 / 31 | 41.7 / 63 | 554 / 1,156 | 1,272 / 3,703 |
| 24 / 23, 2 x physical | 22.3 / 31 | 42.3 / 78 | 524 / 1,359 | 1,252 / 4,641 |

Smaller chunks lowered the wall time for more CPU time, and the physical-core cap the CPU
time for more wall time. Combined (second and third sweeps):

| Variant | Email | Pivot | Czech article | Chinese article |
|---|---|---|---|---|
| 24 / 23, 4 x logical (before) | 22.0 / 31 | 42.0 / 63 | 530 / 1,234 | 1,223 / 5,094 |
| 22 / 21, physical (kept) | 21.0 / 31 | 40.0 / 109 | 475 / 1,188 | 1,135 / 4,516 |
| 21 / 20, physical | 21.4 / 47 | 38.5 / 94 | 464 / 1,281 | 1,094 / 4,156 |
| 22 / 21, 2 x physical | 21.4 / 31 | 40.1 / 125 | 471 / 1,344 | 1,117 / 4,953 |
| 23 / 21, physical | 21.1 / 31 | 39.4 / 78 | 481 / 1,297 | 1,159 / 4,203 |

An earlier sweep of the same pair gave 533 / 1,266 versus 493 / 1,219 for the Czech article and
1,232 / 4,656 versus 1,146 / 4,453 for the Chinese one. The CPU times of the email and pivot
(20-40 ms runs) are within the 15.6 ms timer resolution.

The x86 kernel now uses 2^22 / 2^21 with at most one chunk per physical core; the ARM and
portable kernels keep 2^24 / 2^23 and 4 chunks per logical processor, which were measured on an
Apple M5 and cannot be checked here. All chunk counts now come from one helper,
`Int8Gemm.ParallelChunks`. 2^21 / 2^20 was faster still but used more CPU time on the Czech
article.

Final comparison with the experiment 3 build, three alternating processes:

| Case | Wall ms | CPU ms | Allocated per run |
|---|---:|---:|---:|
| Email, `WithinProducts` | 22.2 / 21.1 | 31 / 31 | 469 / 549 KB |
| Pivot cs-ja email | 42.4 / 40.3 | 78 / 94 | 881 / 1,042 KB |
| Czech article, `WithinProducts` | 531 / 482 | 1,188 / 1,297 | 7.5 / 12.1 MB |
| Chinese article, `WithinProducts` | 1,220 / 1,137 | 4,516 / 4,172 | 13.3 / 19.7 MB |
| Czech article, `None` | 880 / 881 | 922 / 906 | 4.2 / 4.2 MB |
| Czech article, `AcrossSentences` | 106.5 / 106.5 | 1,344 / 1,359 | 7.0 / 7.0 MB |
| Chinese article, `None` | 2,823 / 2,821 | 2,828 / 2,859 | 4.5 / 4.5 MB |
| Chinese article, `AcrossSentences` | 701 / 696 | 8,594 / 8,469 | 79 / 78 MB |

`WithinProducts` is 5-9% faster. Its CPU time was lower for the Chinese article in all three
comparisons (-4% to -11%); for the Czech article it was -4%, -4% and +9%, which is within the
variation of a 32-thread process. Each additional `Parallel.For` allocates, so allocations per
`WithinProducts` translation rose by 50-60%; live managed bytes and the peak working set did
not change. `None` and `AcrossSentences` do not split products and were unchanged. The suite
passed with AVX-512, AVX2 and the portable kernel (698, 698 and 634), and all 24 outputs were
byte-identical with AVX-512 and with AVX2.

**`AcrossSentences` workers** (`BERGAMOT_ACROSS`), wall ms / CPU ms:

| Workers | Email | Czech article | Chinese article |
|---|---|---|---|
| 16 (physical cores, current) | 14.7 / 47 | 108 / 1,344 | 701 / 8,641 |
| 8 | 14.4 / 47 | 181 / 1,094 | 652 / 4,344 |
| 24 | 14.4 / 47 | 108 / 2,016 | 755 / 13,625 |
| 32 (logical processors) | 14.6 / 47 | 116 / 2,859 | 823 / 19,188 |

One worker per physical core remains the best overall. Fewer workers helped only the Chinese
article; the batch size follows the worker count (`MarianEngine` aims at a fixed number of
batches per worker), so that difference is probably the batching, a separate experiment.

**Affinity.** On Windows, `ProcessorTopology` counted every core of the system and only clamped
the result to `Environment.ProcessorCount`, which does respect the affinity mask. With
`start /affinity FF` (logical processors 0-7, four cores with SMT) it reported 8 cores. Only
cores with a logical processor in the process's mask count now (processes spanning several
processor groups have no single mask and count all cores):

| Affinity mask | Logical processors | Physical cores before / after |
|---|---:|---:|
| (all) | 32 | 16 / 16 |
| `FF` | 8 | 8 / 4 |
| `5555` (one per core) | 8 | 8 / 8 |
| `0F` | 4 | 4 / 2 |
| `1` | 1 | 1 / 1 |
| `FFFF0000` | 16 | 16 / 8 |

`AcrossSentences` under `FF`, before / after (4 instead of 8 workers): Czech article 254 / 285 ms
wall and 1,703 / 1,078 ms CPU, Chinese 926 / 1,003 ms wall and 6,250 / 3,469 ms CPU, email
unchanged at 15.3 ms. On four cores the SMT siblings still add some throughput, so the
documented one-worker-per-core policy costs 8-12% wall time there and saves 37-45% of the CPU
time. Linux (all of sysfs) and macOS (`hw.physicalcpu`) still ignore affinity; Linux could
intersect with `sched_getaffinity`. Hybrid CPUs (performance and efficiency cores) are counted
as equal cores; that needs Intel hardware to evaluate.

### 5. Saturation metadata on x86 (kept: omitted)

`SaturationOffsets` / `SaturationPairs` list the weight pairs whose int16 pair sum can overflow,
for the ARM and portable kernels, which compute exact sums and correct them. The x86 kernel's
`PMADDUBSW` saturates the pairs itself, and a loaded model serves only the kernel it was loaded
for (`MarianEngine` creates its weights and workers with the same `TranslationOptions` kernel).
Row sums remain: `PrepareBias` needs them.

Sizes with the x86 kernel (all matrices including the output layer; the offsets are almost all
of it, with 1-367 actual pairs per matrix):

| Model | Weights | Row sums | Offsets + pairs |
|---|---:|---:|---:|
| cs-en | 43.5 MB | 407 KB | 411 KB |
| zh-Hans-en | 91.8 MB | 668 KB | 674 KB |
| en-ja | 55.7 MB | 535 KB | 543 KB |

The audit found that the row-major paths of `Multiply` and `MultiplyArgMax` (the shortlisted
output layer on x86) sliced each row's pairs before deciding whether the kernel needs them; with
the metadata omitted, the first run crashed in every shortlisted case, which the unit tests did not
cover because `QuantizedMatrix.Create` always computes the metadata. Those slices now go through
`RowPairs`, which returns no pairs for the x86 kernel.

The change: `QuantizedMatrix.CreateRows` takes a `saturation` flag; the model loader and
`CreateOutputWeights` pass `Int8Gemm.UsesSaturationMetadata(kernel)`, false only for the x86
kernel. `Multiply` and `MultiplyArgMax` throw `NotSupportedException` when a kernel that needs the
metadata gets a matrix without it, instead of failing on an index. New tests: x86 products and
argmax are bit-identical with and without the metadata (packed, row-major and selected rows,
with saturating pairs, failing before the `RowPairs` fix), output weights without it keep
everything else, the portable and ARM kernels reject such matrices, and the stream loader omits
it exactly for the x86 kernel.

Loading `MarianModel` plus `MarianWeights` (microbenchmark, seven loads per process, three
processes; first load includes JIT):

| Model | First load ms | Later loads ms | Live model bytes |
|---|---:|---:|---:|
| cs-en | 216 / 116 | 184-186 / 88-91 | 45,082,736 / 44,667,920 |
| zh-Hans-en | 394-398 / 195-197 | 305-317 / 111-150 | 94,291,944 / 93,615,312 |

The scalar pair scan over every weight took about half of the loading time. In translation
(medians of three processes, experiment 4 / this change):

| Case | Cold first translation ms | Warm wall ms | Live managed MB |
|---|---:|---:|---:|
| Email, `None` | 778 / 670 | 30.5 / 31.9 | 51.3 / 50.9 |
| Email, `WithinProducts` | 551 / 459 | 21.1 / 21.5 | 51.3 / 50.9 |
| Email, `AcrossSentences` | 500 / 393 | 14.8 / 14.4 | 52.8 / 53.2 |
| Email, shortlist | 529 / 414 | 21.4 / 20.5 | 56.1 / 55.7 |
| HTML email | 406 / 315 | 10.9 / 11.5 | 48.3 / 47.9 |
| Pivot cs-ja email (two models) | 1,125 / 751 | 40.9 / 40.9 | 116.1 / 115.1 |
| Czech article, `None` | 1,675 / 1,672 | 891 / 884 | 86.7 / 86.3 |
| Czech article, `WithinProducts` | 1,544 / 1,481 | 477 / 487 | 86.7 / 86.3 |
| Czech article, `AcrossSentences` | 757 / 752 | 106.0 / 105.7 | 143.8 / 143.5 |
| Chinese article, `None` | 3,969 / 3,697 | 2,822 / 2,822 | 141.3 / 140.6 |
| Chinese article, `WithinProducts` | 2,365 / 2,315 | 1,133 / 1,127 | 141.3 / 140.6 |
| Chinese article, `AcrossSentences` | 1,609 / 1,430 | 680 / 668 | 218.9 / 219.8 |

The first translation of an email is 14-22% faster, of a pivot 33%. Warm times did not change
beyond the variation seen between identical binaries (the Czech `WithinProducts` difference,
+2%, is the size of the earlier run-to-run spread of the same binary, 477-482 ms). Peak working
sets were within 1.5 MB except for the Chinese `AcrossSentences` case, which varies by more.

Correctness: the suite passed with AVX-512, `DOTNET_EnableAVX512=0`, the same without VNNI (704
each), `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0` (637 each). The complete suite,
including the `ExternalServices` tests that download models, passed with AVX-512 (726 passed,
66 ARM-only skipped); it had not been run for experiments 1-4, so this covers them as well.
All 24 outputs were byte-identical to the baseline with AVX-512, AVX2 and the portable kernel.

Not changed: the embedding matrices' row sums and, on the other kernels, their saturation
metadata (128-256 KB per embedding matrix) are never read either, as embeddings are only looked
up, but omitting them affects every platform. On the ARM and portable kernels the same pair scan
is still half of the load time and could be vectorized.

### 6. Attention after the kernel changes (kept: two heads at a time)

Profile of the experiment 5 build (`dotnet-trace`, `--single-thread`, share of translation time):
attention (`MarianTranslator.Attend`) took 16.8% of the Czech article, up from 9% before the
kernel experiments, and 11.6% of the Chinese article; half of it each in the encoder's
self-attention and the decoder's context attention. Within it, the inlined score loop took about
62%, `AttentionValues` 35% and the softmax under 2%.

Both loops are latency-bound. To keep the sums bit-identical, each lane (one key for the
scores, one output column for the values) adds its terms in order, so every step depends on the
previous one: the score loop kept two additions in flight per step and spent eight scalar
instructions on a bounds-checked `q[...]` and an `imul` for the key offset. With the cs-en head
size of 48 (384 / 8 heads), `AttentionValues` took one 256-bit pass for 32 columns and one 128-bit
pass for 16, so each key was two chains of four additions.

The change processes two adjacent heads at once; each lane's operations and their order are
unchanged:

- `MarianMath.AttentionScoresPair` computes both heads' scores, with references advanced by the
  key padding instead of indexed arrays: four independent additions per step for blocks of 32
  keys, two for the narrower tails.
- `MarianMath.AttentionValuesPair` covers both heads' columns in one pass over the keys, with a
  512-bit accumulator for every 16 columns (four for head size 32, six for 48, eight for 64). It is
  used where `Vector512` is accelerated; otherwise each head still calls `AttentionValues`.
- `Attend` takes pairs of heads, keeps the existing single-head code for an odd last head, and
  takes the alignment from head 0 as before.

Microbenchmark of the values for eight heads (two `AttentionValues` calls per pair versus one
`AttentionValuesPair`), ns per query, stable over three runs:

| Head size, keys | 12 | 30 | 60 |
|---|---:|---:|---:|
| 48 | 145 / 91 | 337 / 202 | 718 / 553 |
| 64 | 131 / 110 | 237 / 368 | 986 / 849 |

Head size 64 already had four 512-bit accumulators per head, and 30 keys were slower in this
microbenchmark. In translation, a build that used the pair only for head sizes 32 and 48 was
slower for the Chinese article (head size 64) than one that also used it for 64: `None`
2,822 / 2,853 ms and `WithinProducts` 1,098 / 1,119 ms with non-overlapping process ranges, so the
pair stays enabled for 64.

New tests compare `AttentionScoresPair` bit for bit with a scalar in-order reference (11 key
lengths, head sizes 32, 48 and 64, including NaN and infinity) and `AttentionValuesPair` with
two `AttentionValues` calls (including NaN, infinity and -0). The suite passed with AVX-512 (752),
`DOTNET_EnableAVX512=0` (737, the values pair skipped), `DOTNET_EnableAVX2=0` and
`DOTNET_EnableHWIntrinsic=0` (670 each); the complete suite with the `ExternalServices` tests
passed with AVX-512 (774). All 24 outputs, including HTML (which uses the attention alignments)
and the pivot, were byte-identical to the baseline with AVX-512, AVX2 and the portable kernel.

Translation, medians of three processes, experiment 5 / this change:

| Case | Wall ms | Per process | CPU ms |
|---|---:|---|---:|
| Email, `None` | 32.9 / 32.6 | 33.1, 32.9, 32.7 / 32.6, 33.4, 31.1 | 31 / 31 |
| Email, `WithinProducts` | 23.0 / 21.6 | 23.1, 23.0, 23.0 / 21.6, 23.7, 20.5 | 31 / 31 |
| Email, `AcrossSentences` | 15.0 / 15.4 | 15.0, 16.4, 15.0 / 14.8, 15.4, 15.6 | 47 / 47 |
| Email, shortlist | 22.0 / 21.6 | 22.0, 20.5, 22.5 / 21.3, 22.1, 21.6 | 31 / 31 |
| HTML email | 10.1 / 10.1 | 10.1, 13.5, 9.7 / 11.8, 10.1, 10.0 | 16 / 16 |
| Pivot cs-ja email | 39.9 / 38.9 | 39.7, 39.9, 41.5 / 38.9, 38.8, 40.0 | 94 / 78 |
| Czech article, `None` | 881 / 844 | 881, 875, 883 / 847, 844, 841 | 891 / 859 |
| Czech article, `WithinProducts` | 481 / 447 | 486, 481, 480 / 446, 447, 450 | 1,328 / 1,281 |
| Czech article, `AcrossSentences` | 105.0 / 103.8 | 105.7, 104.2, 105.0 / 102.8, 105.4, 103.8 | 1,344 / 1,297 |
| Chinese article, `None` | 2,822 / 2,764 | 2,822, 2,847, 2,750 / 2,770, 2,764, 2,696 | 2,844 / 2,781 |
| Chinese article, `WithinProducts` | 1,094 / 1,039 | 1,094, 1,113, 1,077 / 1,039, 1,033, 1,045 | 4,172 / 3,891 |
| Chinese article, `AcrossSentences` | 606 / 596 | 601, 610, 606 / 596, 596, 596 | 7,266 / 7,281 |

The Czech article is 4% faster on one thread and 7% in `WithinProducts`, the Chinese article
5% in `WithinProducts`, with non-overlapping process ranges; the Chinese serial runs overlap
(2% by the medians), and the email cases are within noise. Allocations and live bytes did not
change. After the change, attention took 15.2% of the Czech article's single-thread profile
(`AttentionScoresPair` 5.0%, the inlined values pass most of the rest), about 13% less time
than before.

Not done: in the encoder, all query rows of a sentence share its keys, so scoring several rows at
once would also share the key loads; the decoder's rows attend to different sentences, so this
would help only the encoder's half.

## Follow-ups

### Model loading (kept)

Loading `MarianModel` plus `MarianWeights` (microbenchmark, seven loads per process, three
processes each; ms for the first load, which includes JIT and is what a translation pays once
per model):

| Build | cs-en, x86 | zh-en, x86 | cs-en, portable | zh-en, portable |
|---|---:|---:|---:|---:|
| Experiment 6 | 117 | 195-205 | 197-199 | 367-372 |
| Vectorized pair scan and packing | 103-110 | 170-174 | 102-105 | 169-172 |
| Also without embedding metadata | 118 | 180-189 | 144-146 | 217-218 |
| Also optimized from the first call (kept) | 72-73 | 95-96 | 71 | 94-96 |

"Portable" is the portable kernel with `DOTNET_EnableAVX2=0`, which uses `Vector128` like ARM.

1. **Saturation pair scan.** A pair can saturate only when both weights have the same sign and
   `|w0 + w1| >= 130`; weights of different signs sum to at most 128 in magnitude, so the sum
   alone decides. Reading a pair as one little-endian `short` v, `w0 = (v << 8) >> 8` and
   `w1 = v >> 8`, a `Vector256`/`Vector128` step tests 16 or 8 pairs, and only blocks with a hit
   go through the scalar rule. A test checks all 65,536 weight pairs and odd tails against the
   scalar rule for `Create` and `CreateRows`.
2. **Packing.** `PackRow` copied each group of four inputs with a `Span.CopyTo`; it now does one
   unaligned 4-byte copy per group with a precomputed stride.
3. **Embeddings** are only looked up (and read to build the output layer), so the loader no longer
   computes their row sums or saturation metadata on any kernel: 128 KB (x86) or 256 KB (other
   kernels) less per embedding matrix, 256-512 KB for the measured models. Products and
   `PrepareBias` throw `NotSupportedException` for such a matrix.
4. **Unoptimized code.** The third row showed loading becoming slower with less work. The
   profile put the time into code that had not changed, and isolated, the output-layer conversion
   took 80 ms for its first four calls and 18-23 ms afterwards: loading runs each of these loops
   a few times, before tiered compilation optimizes them, so the result depended on which loop
   happened to be promoted. The per-row loops of loading (`CreateRows`, the row sum, now
   vectorized, the pair scan, packing and the output-layer lookup) are now
   `MethodImplOptions.AggressiveOptimization`. They are simple loops that gain nothing from
   dynamic PGO.

The first translation of a new process (`WithinProducts`, experiment 6 / this change; x86 pivot
over six processes because of its spread):

| Case | x86 | Portable |
|---|---|---|
| Email | 415-431 / 400-409 ms | 802-812 / 596-675 ms |
| Pivot cs-ja (two models) | 714-900 / 700-750 ms | 1,180-1,184 / 849-866 ms |

Warm translation does not run this code. The suite passed with AVX-512, AVX2, the portable
kernel and without hardware intrinsics, and all 24 outputs were byte-identical to the baseline
with AVX-512, AVX2 and the portable kernel.

### Encoder attention for several queries (measured, not changed)

In the encoder, all query rows of a sentence attend to the same keys. A prototype in the
microbenchmark scored two queries per pass over the keys (two queries x two heads, eight
512-bit sums in flight, key loads shared); per query, against `AttentionScoresPair` one query at
a time:

| Head size, keys | 32 | 64 |
|---|---:|---:|
| 48 | 233 / 157 ns | 655 / 362 ns |
| 64 | 306 / 186 ns | 896 / 493 ns |

The scores are 1.5-1.8 times faster, but they are about 5% of single-thread time and the encoder
half of that, so the expected gain is about 1%, for a restructured `Attend` with separate
two-query tails. The values pass would not gain: with two queries per pass it would be limited by
the multiply and add units instead of latency, which comes to the same time per query. The
decoder's rows attend to different sentences and cannot share keys.

### `AcrossSentences` batch size (measured, not changed)

`MarianEngine` aims at `BatchesPerThread` = 2 batches per worker, at least `MinBatchTokens` = 64
tokens each. A temporary build read both from environment variables (outputs byte-identical
with three settings). 16 workers, wall ms / CPU ms, medians of three processes:

| Variant | Email | Czech article | Chinese article |
|---|---|---|---|
| 2 per worker, at least 64 tokens (current) | 14.2 / 47 | 104 / 1,313 | 673 / 8,266 |
| 1 per worker | 14.3 / 47 | 129 / 1,375 | 587 / 5,859 |
| 3 per worker | 14.5 / 47 | 110 / 1,328 | 751 / 9,938 |
| 4 per worker | 14.5 / 47 | 109 / 1,438 | 843 / 12,438 |
| At least 128 tokens | 18.7 / 31 | 103 / 1,266 | 667 / 8,078 |
| At least 256 tokens | 19.7 / 31 | 105 / 1,266 | 664 / 8,266 |
| 1 per worker, at least 128 tokens | 18.3 / 31 | 128 / 1,391 | 586 / 5,844 |

One batch per worker made the Chinese article 13% faster with 29% less CPU time and the Czech
article 24% slower; a larger minimum turned the email into a single batch on one thread. Each
decoder step reads the whole output layer, 32 MB for zh-en (64,000 tokens x 512), more than
one Zen 5 chiplet's 32 MB L3, against 12 MB for cs-en, so fewer and larger batches save memory
traffic for the large vocabulary, while the smaller one gains more from balancing the batches
across workers (with one batch per worker the longest batch bounds the time). This is
consistent with the earlier run in which 8 workers helped only the Chinese article. A rule based
on the output layer size would fit both models here, but it is tuned on one machine and two
models with opposite effects, so it was not adopted; it needs more models and machines.

### Eight-row AVX-512 tiles (2026-10-22)

Baseline for this follow-up: `a58f08e8b3a`. Candidate: the eight-row AVX-512 implementation
introduced with this section. Ryzen 9 9950X (16 cores / 32 logical processors),
Windows 11 build 29683, .NET SDK 10.0.402 / runtime 10.0.13, Balanced power plan,
AVX-512BW and hardware-accelerated Vector512 enabled, no runtime feature overrides.
The corpus and installed benchmark models match the hashes in the benchmark Results directory.

The packed AVX-512 products now share each weight load across eight activation rows instead
of four, with eight independent vector accumulators. Both multiplication and fused argmax use
the new tile, followed by the existing four-, two- and one-row tails. The cache block is still
about 16 KB of activations, rounded down to a whole tile (at least one tile). AVX2 keeps its
four-row tile. No new weight layout, retained buffer, managed allocation or floating-point
reordering is involved; the temporary inline array on the stack grows by 256 bytes.

Regression coverage adds eight-row tiles, mixed 4/2/1 tails, and rows that cross cache blocks,
including width 1,536. Multiplication is compared bit for bit with the scalar saturated-pair
reference; fused argmax is checked against multiplication, including partial output panels.
The full suite passes with AVX-512 (1,053 passed, 82 skipped); without ExternalServices it
passes with AVX2 (1,016 passed, 97 skipped) and the portable kernel (933 passed, 180 skipped).
All 24 outputs are byte-identical to the baseline in each of these three modes, including
shortlists, HTML/alignment and the pivot.

The benchmark scripts now propagate a failed harness exit instead of silently writing a row
without measurements. Both scripts were checked with a failing substitute for dotnet: they
return its exit code and record zero result rows. The setup order in the tool README also
keeps the shortlist: downloading cs-ja after cs-en without `--shortlist` replaces that pair's
installation and removes the previously downloaded shortlist.

Measurements are medians of three alternating fresh processes, with the existing harness warmup
and iteration counts. The first run's cs-across and Chinese cases overlapped a validation
subprocess that survived interruption; those timings were discarded and repeated after checking
that no other translation process was running. The HTML case was repeated as well. Only valid
measurements are kept in `tools/Benchmark/Results/results-opt8.txt` (`opt-base` / `opt8`).

| Case | Warm wall ms, baseline / candidate | CPU ms | First translation ms |
|---|---:|---:|---:|
| Czech article, None | 828.6 / 815.3 | 844 / 828 | 1,634 / 1,411 |
| Chinese article, None | 2,756.0 / 2,627.7 | 2,781 / 2,656 | 3,632 / 3,237 |
| Czech article, WithinProducts | 442.3 / 439.3 | 1,250 / 1,344 | 1,459 / 1,319 |
| Chinese article, WithinProducts | 1,032.5 / 1,031.7 | 3,719 / 4,031 | 2,155 / 2,274 |
| Czech article, AcrossSentences | 102.6 / 103.3 | 1,281 / 1,234 | 929 / 638 |
| Chinese article, AcrossSentences | 612.9 / 606.3 | 7,453 / 7,375 | 1,153 / 1,178 |

The serial article improvement is 1.6% for Czech and 4.7% for Chinese, with non-overlapping
process medians in both cases; the first serial translations are 14% and 11% faster. The warm
parallel cases are effectively unchanged (the 1% Chinese AcrossSentences difference has
overlapping process ranges). WithinProducts CPU-time medians rose by 7.5-8.4%, so this is a
tradeoff for serial throughput, not a demonstrated improvement to the default parallel mode.
Small-email and HTML results vary too much to claim a speedup. Allocations and retained bytes
are unchanged within run-to-run variation; the Chinese AcrossSentences working set still varies
substantially. This change has not been measured on Intel or ARM hardware.

### `VPDPBUSD` with exact correction on AVX2 + AVX-VNNI (kept)

Baseline: `16a8804fa81`. All measurements on the Ryzen 9 9950X with `DOTNET_EnableAVX512=0`
(AVX2 and AVX-VNNI, no AVX-512), which takes the same code as Intel CPUs with AVX-VNNI and
without AVX-512 (Alder Lake and later client parts); not measured on such a CPU.

The AVX2 panel kernels used `VPMADDUBSW` + `VPDPWSSD`, two instructions per 32 products, because
`VPMADDUBSW` saturates adjacent pairs to int16 exactly as intgemm does. `VPDPBUSD`
(`AvxVnni.MultiplyWideningAndAdd` on bytes, available in .NET 10) adds four products to int32 in
one instruction, but exactly. The difference is confined to the pairs listed in the saturation
metadata (1-367 per matrix in the measured models), so:

- `X86ExactSums` (AVX-VNNI and no accelerated AVX-512) selects `VPDPBUSD` in `DotAvx2x4/x2/x1`
  through `AccumulatePanel256`. The condition is written out there, because tier-0 code does not
  inline and a property call per step made the first translation of an email 30 ms slower.
- `UsesSaturationMetadata` is true for the x86 kernel in that configuration, so the loader computes
  the metadata again (1-2 ms per model, 287 KB for cs-en).
- `CorrectSaturation` runs after each tile of a panel that has such pairs, in both the products and
  the fused argmax: for each pair and activation row, it replaces the exact pair sum with the
  int16-saturated one. Pairs are adjacent inputs, so both weights are in one packed group.
- The AVX-512 kernels and the row-major (shortlist) kernel keep saturating, unchanged.

Correctness: the suite passed with AVX-512, AVX2 + AVX-VNNI, AVX2 without VNNI and the portable
kernel; the complete suite with `ExternalServices` passed on the new path (1,035 passed). Without
the correction 111 product tests fail, and without it only in the argmax 20 argmax tests fail, so
both are covered. All 24 outputs were byte-identical to the baseline in the same four
configurations. The real models' outputs were identical even with the argmax correction
accidentally disabled (a stale build, see below): saturation rarely decides a winner, so the unit
tests are what guard it.

A stale build invalidated the first round of checks: restoring a file with `mv` kept the backup's
older timestamp, MSBuild considered the experiment's DLL up to date, and `copy-build.sh` copied
it. A scalar reference in a probe exposed it (fused argmax winners differed from `Multiply`).
Touch restored files, and compare the copied DLL with the one the tests used.

Microbenchmark (`gemm`, now with weights in [-64, 63] so that, as in the models, no pair can
saturate; with uniform int8 weights a quarter of the pairs could, and the correction dominated
at 3 GMAC/s), GMAC/s, two runs each, before / after (`Results/micro-vnni2-avx2.txt`):

| Shape | 1 row | 4 rows | 6 rows | 42 rows | 300 rows |
|---|---|---|---|---|---|
| cs FFN 384 x 1,536 | 94, 113 / 116, 115 | 162, 163 / 290, 289 | 154, 164 / 241, 240 | 165, 163 / 285, 285 | 163, 163 / 290, 288 |
| zh FFN 2,048 x 512 | 89, 87 / 88, 88 | 164, 164 / 292, 287 | 163, 164 / 234, 231 | 164, 164 / 282, 283 | 164, 164 / 292, 291 |
| cs argmax 384 x 32,000 | 106, 98 / 106, 110 | 163, 164 / 295, 296 | 163, 164 / 238, 240 | 163, 164 / 288, 288 | - |
| zh argmax 512 x 64,000 | 89, 90 / 81, 85 | 156, 160 / 255, 260 | 157, 160 / 214, 219 | 160, 161 / 270, 270 | - |

The single-row kernel did not gain: it alternates two accumulators, and with one instruction per
step the chain latency bounds it; more accumulators are a possible follow-up.

Translation, medians of three processes, baseline / candidate (`Results/results-vnni-avx2.txt`;
the cold column is the first translation of a new process):

| Case | Wall ms | CPU ms | Cold ms |
|---|---:|---:|---:|
| Email, `None` | 48.4 / 37.9 | 47 / 47 | 428 / 522 |
| Email, `WithinProducts` | 32.8 / 28.1 | 63 / 31 | 429 / 468 |
| Email, `AcrossSentences` | 28.7 / 25.1 | 94 / 63 | 423 / 481 |
| Email, shortlist | 30.2 / 26.8 | 47 / 31 | 405 / 433 |
| HTML email | 22.0 / 15.2 | 31 / 31 | 291 / 320 |
| Pivot cs-ja email | 64.5 / 55.9 | 203 / 141 | 727 / 683 |
| Czech article, `None` | 1,441 / 1,024 | 1,453 / 1,047 | 2,178 / 1,828 |
| Czech article, `WithinProducts` | 828 / 694 | 3,000 / 2,172 | 1,766 / 1,791 |
| Czech article, `AcrossSentences` | 188.3 / 142.7 | 2,453 / 1,875 | 997 / 734 |
| Chinese article, `None` | 4,571 / 3,284 | 4,578 / 3,328 | 5,443 / 4,231 |
| Chinese article, `WithinProducts` | 1,985 / 1,681 | 10,641 / 7,250 | 3,053 / 2,779 |
| Chinese article, `AcrossSentences` | 828 / 760 | 11,000 / 9,406 | 1,585 / 1,641 |

Serial articles are 28-29% faster, `WithinProducts` 15-16% with 28-32% less CPU time,
`AcrossSentences` 8-24%, the email and pivot 13-22%; all with non-overlapping process ranges
except the HTML email. The cold email regression in this table came from the property call in
tier-0 code; with the condition written out, five fresh processes each gave first email
translations of 437-443 ms (baseline), 436-478 ms (property) and 432-463 ms (written out), and
warm times did not change (`Results/results-vnni2-check.txt`: Czech article `None` 1,030 / 1,032 ms,
email 30.0 / 30.0 ms). The default AVX-512 path is unaffected: Czech article `None` 864 / 860 ms,
email 24.7 / 24.6 ms.
