# Performance notes

Measurements behind the optimizations, mostly on Apple M5. The x86 work is in
[X86-PERFORMANCE.md](X86-PERFORMANCE.md); the benchmark tools are in [tools/Benchmark](../tools/Benchmark/README.md).

## Kernel optimization comparison

Apple M5, .NET 10.0.13, Release, compared with `336a96caad8`. Both versions used the
same installed models and inputs, with the shortlist disabled. Times are medians of warm
translations, excluding model loading: eight runs for the email, three for single-threaded
articles and four for the other modes. Machine load and clock changes affect these numbers.

| Input and mode | Before | After | Less time |
|---|---:|---:|---:|
| Czech email, 108 words, `None` | 254 ms | 190 ms | 25% |
| Czech article, 3,136 words, `None` | 5,586 ms | 4,119 ms | 26% |
| Czech article, `WithinProducts` | 3,352 ms | 2,469 ms | 26% |
| Czech article, `AcrossSentences` | 2,028 ms | 1,717 ms | 15% |
| Chinese article, 7,633 non-whitespace characters, `None` | 8,372 ms | 6,230 ms | 26% |

All five outputs matched the baseline byte for byte. Managed allocations per translation
fell from 171.7 MB to 146.0 MB for the single-threaded Czech article, and from 149.6 MB to
110.4 MB for the Chinese article. CPU time also fell in both threaded Czech cases.

The changes are SIMD activation quantization, ARM dot-product tiles covering four activation
rows and two output columns, sharing attention score loads across four vectors, and avoiding
full-vocabulary candidate arrays for every sentence. The rounding, saturation correction and
floating-point summation order are preserved. The portable dot-product kernel is unchanged.

For a before/after comparison, build `tools/Translate` in Release at each revision, then run
with the same installed models and input, for example:

```
dotnet tools/Translate/bin/Release/net10.0/Translate.dll cs en --benchmark 8 --input article.txt
```

Add `--single-thread` or `--across-sentences` to compare the other modes. The tool reports
warm wall time and CPU time; its first translation includes model loading.

## Further buffer reuse

Compared with `246d129a29e`, sharing one encoder transpose buffer across layers, reusing it
for the first decoder context, and using existing work buffers for the encoder output and
context key projections reduced warm managed allocations per translation:

| Input, one thread | Before | After |
|---|---:|---:|
| Czech email, 108 words | 4.98 MB | 2.59 MB |
| Czech article, 3,136 words | 145.98 MB | 74.93 MB |
| Chinese article, 7,633 non-whitespace characters | 110.41 MB | 38.32 MB |

Outputs matched byte for byte, including the Czech article with `WithinProducts`. These
measurements establish the allocation reduction; elapsed times varied between runs.
Transpose offsets and sentence lengths are computed once per batch. At that checkpoint, decoder
contexts remained scoped to the batch; they are now reused as described below. Recurrent cells
are still initialized for every batch.

## Packed ARM kernel

ARM dot-product models use four-output/four-input weight tiles for dense products, with each
int32 accumulator lane corresponding to an output column. Four activation rows share each
weight load. A separate single-row loop uses independent accumulators to hide SDOT latency.
Saturation correction uses the original pair metadata, and partial tiles and parallel partitions
preserve Int8Shift arithmetic. Widths not divisible by 16 keep the row-major kernel.

Packing happens at model initialization. Linear matrices retain one weight layout, sharing the
row-sum and saturation metadata with their packed replacement. Source and target embeddings
remain row-major for lookup. When lexical shortlisting is enabled, its output projection also
stays row-major: scattered row gathers were slower from packed storage. Dense hidden layers
still use the packed kernel. Portable models keep their row-major weights.

Apple M5, .NET 10.0.13, Release, warm median times compared with `246d129a29e`, including
both the packed kernel and buffer reuse:

| Input and mode | Before | After | Less time |
|---|---:|---:|---:|
| Czech article, `None` | 4,027 ms | 3,268 ms | 19% |
| Czech article, `WithinProducts` | 2,487 ms | 2,001 ms | 20% |
| Czech article, `AcrossSentences` | 1,447 ms | 1,035 ms | 28% |
| Chinese article, `None` | 5,546 ms | 4,719 ms | 15% |
| Czech email, shortlist, `None` | 102 ms | 82 ms | 20% |

Three measured runs per article case, four for `AcrossSentences`, and twelve for the shortlisted
email, after warmup and model-loading garbage collection. All outputs matched the baseline byte
for byte. Timings vary with machine load. The same full-vocabulary articles and 108-word email
were used as above.

The loaded Czech model's live managed size stayed at about 50.1 MB. After a single-threaded
Czech article, live managed memory was 85.1 MB before and 88.1 MB after: reusing encoder
scratch retains a larger worker buffer. Per-translation allocations nevertheless fell from
146.0 MB to 74.9 MB. The corresponding Chinese allocations fell from 110.4 MB to 38.3 MB.

## Wider ARM tiles

The packed kernel's 4 x 4 tile kept only four accumulators. Each SDOT waits for the previous
one into the same accumulator, so the core issued about 1.5 SDOTs per cycle where it can sustain
about two. Dense products and the output projection now compute four activation rows times four
panels (16 output columns) at a time. That gives 16 independent accumulators, and each weight
load feeds four rows. A single row uses 16 accumulators across the four panels. Tiles load their
row sums and biases as vectors. Attention keeps 8 (scores) or 12-16 (values) sums in flight on
`Vector128`. The integer sums and the order of every floating-point sum are unchanged, so outputs
are byte-identical.

Single-thread products on the model shapes, best of three runs (GMAC/s):

| Shape | 1 row | 4 rows | 16 rows |
|---|---|---|---|
| cs feed-forward 384 x 1,536 | 44 -> 64 | 80 -> 119 | 100 -> 114 |
| cs feed-forward 1,536 x 384 | 53 -> 76 | 70 -> 158 | 88 -> 168 |
| cs output 384 x 32,000 (argmax) | 56 -> 61 | 64 -> 120 | 85 -> 120 |
| zh feed-forward 2,048 x 512 | 67 -> 73 | 77 -> 163 | 75 -> 174 |

Apple M5, .NET 10.0.13, warm medians of three alternating runs against `49301f5dcbb`:

| Input and mode | Before | After | Less time |
|---|---:|---:|---:|
| Czech article, `None` | 3,705 ms | 2,142 ms | 42% |
| Czech article, `WithinProducts` | 2,135 ms | 1,749 ms | 18% |
| Czech article, `AcrossSentences` | 1,151 ms | 1,034 ms | 10% |
| Chinese article, `None` | 5,397 ms | 3,243 ms | 40% |
| Czech email, 108 words, `None` | 172 ms | 100-155 ms | 10-42% |

The email's time varies between runs, probably depending on whether the thread runs on a P-core
or an E-core. Products are still about 80% of a single-threaded translation, now near the core's
SDOT throughput from four rows up. A single row remains at about half of it.

**What would need intrinsics .NET does not have.** Measured with C on the M5:

- **I8MM (`SMMLA`, `USMMLA`):** it does twice the work per instruction, but the M5 issues it at half
  SDOT's rate. A 16-accumulator loop reached the same 130 GMAC/s with either. An exact
  unsigned-by-signed product (`USDOT`, `USMMLA`) would save the +127 shift and its row-sum
  correction, a small gain. .NET exposes I8MM only in the SVE-based proposal
  [dotnet/runtime#94027](https://github.com/dotnet/runtime/issues/94027), which is open with no
  milestone. Apple cores run SVE only in streaming mode, so it would not reach them. No proposal
  covers a NEON (`AdvSimd`) form.
- **SME2 (`USMOPA` into ZA tiles):** a simple exact kernel (one 16-row group, four 16 x 16 int32
  tiles, activations packed per call) reached 200-600 GMAC/s from 16 rows up, 2-4x the SDOT kernel.
  It was slower for a single row. The integer sums would be identical. The SME proposal
  [dotnet/runtime#131694](https://github.com/dotnet/runtime/issues/131694) was closed as not
  planned, and streaming mode would also need runtime support for `smstart`/`smstop`.

## Model loading

The model file is read through a seekable stream, loading dense weights directly into their
retained ARM tile layout and embeddings directly into row-major storage. This avoids both a
full-file managed byte array and temporary row-major copies of packed matrices. Output weights
are requantized using a 256-value lookup with the original float multiplication and rounding;
the vocabulary-sized dequantized float array is no longer needed. Shortlisted output remains
row-major.

Measured against checkpoint `942a75b0924` on Apple M5, macOS ARM64, .NET 10.0.13, SDK
10.0.402. Values are medians of seven separate processes per case with alternating baseline
and changed binaries, cached model files, and no translation during the measurement. MB means
1,000,000 bytes. Loading time includes first-use JIT; it varied substantially between processes
(for example, Czech baseline 181-354 ms and changed 198-313 ms), so these timings do not
establish a throughput improvement.

| Model, kernel | Load before / after | Allocated before / after | Live loaded before / after | Process peak RSS before / after |
|---|---:|---:|---:|---:|
| cs-en, ARM dot product | 265 / 246 ms | 166.7 / 54.9 MB | 50.0 / 50.0 MB | 209.3 / 114.6 MB |
| zh-Hans-en, ARM dot product | 473 / 450 ms | 362.3 / 112.8 MB | 102.7 / 102.7 MB | 356.4 / 170.0 MB |
| cs-en, portable | 235 / 248 ms | 135.6 / 54.9 MB | 50.0 / 50.0 MB | 195.7 / 114.6 MB |
| zh-Hans-en, portable | 491 / 500 ms | 303.3 / 112.8 MB | 102.7 / 102.7 MB | 356.1 / 169.9 MB |

Allocations use `GC.GetTotalAllocatedBytes(true)` around `TranslationModel.Load`; live loaded
memory uses `GC.GetTotalMemory(true)` less a pre-load collection baseline. Peak RSS is the
whole standalone process maximum from `/usr/bin/time -l`, including the runtime, JIT and model;
it is neither incremental model memory nor the sum of GC allocations. `Process.PeakWorkingSet64`
returned zero on this machine and was not used. Streamed and raw model layouts, row sums,
saturation metadata and requantized bytes are covered by focused equivalence tests. Full Czech
and Chinese article translations matched the checkpoint SHA-256 hashes with both ARM and
portable kernels.

## Fused output projection

The full-vocabulary output layer selects its winning token while computing each output tile,
without writing and rescanning a vocabulary-sized logits matrix. Dedicated packed ARM and
row-major paths reuse the existing dot products, saturation correction, prepared biases and
float operation order. Dense products and shortlisted output retain their existing paths.

Each worker reuses score/token arrays sized by activation rows times output partitions, capped
at four partitions per logical processor. These capacities participate in the idle worker byte
budget. Output partitions merge in vocabulary order: strict-greater comparisons preserve the
first token on ties, UNK is excluded, NaNs cannot win, and all negative-infinity scores return
`-1`, matching the original argmax.

Measured against checkpoint `ff4666d4b2b` on Apple M5, .NET 10.0.13 ARM64. Each case used separate
baseline/changed processes, at least two seconds of warmup, and four measured translations;
wall times and allocations below are medians. Live managed memory was collected after warmup
with `GC.GetTotalMemory(true)` and includes the model plus retained worker buffers. MB is
1,000,000 bytes. These figures are neither native RSS nor active-memory peaks.

| Input, mode | Wall time before / after | Allocated before / after | Live warm before / after |
|---|---:|---:|---:|
| Czech email, None | 152 / 130 ms | 2.595 / 2.591 MB | 53.84 / 53.19 MB |
| Czech email, WithinProducts | 70 / 74 ms | 2.845 / 2.838 MB | 54.56 / 53.51 MB |
| Czech email, AcrossSentences | 71 / 67 ms | 2.821 / 2.809 MB | 55.82 / 55.52 MB |
| Czech article, None | 3066 / 2784 ms | 74.926 / 74.904 MB | 88.09 / 79.90 MB |
| Czech article, WithinProducts | 1814 / 1772 ms | 77.759 / 77.737 MB | 88.15 / 79.99 MB |
| Czech article, AcrossSentences | 940 / 955 ms | 80.021 / 76.837 MB | 149.15 / 124.31 MB |
| Chinese article, None | 4333 / 4311 ms | 38.318 / 38.296 MB | 159.09 / 142.69 MB |
| Chinese article, WithinProducts | 2053 / 1931 ms | 41.560 / 41.536 MB | 159.13 / 142.79 MB |
| Chinese article, AcrossSentences | 1588 / 1446 ms | 49.799 / 40.224 MB | 203.27 / 168.44 MB |

A single Czech sentence (one activation row) measured 21.9 / 20.7 ms with `None` and
19.5 / 15.7 ms with `WithinProducts`, over 24 measured calls after warmup. The output product
stays below the parallel threshold at one row. Its allocations fell from 413,008 to 410,768
bytes per translation. These timings varied between process launches; the consistent result
is reduced retained logits storage, rather than a universal latency gain. AcrossSentences
allocations and retained capacities also depend on worker/batch scheduling.

Shortlisted email scoring was unchanged. Initial twelve-call medians were 60.9 / 71.0 ms
(`None`), 44.5 / 54.4 ms (`WithinProducts`), and 39.3 / 38.1 ms (`AcrossSentences`). Repeating in
the opposite binary order for sixteen calls reversed the first two differences to 78.5 / 67.0
and 61.3 / 57.3 ms. This did not establish a repeatable shortlist regression or improvement;
serial allocations stayed about 2.8535 MB, and parallel allocations about 2.974 MB.

All measured ARM, shortlisted and single-row text outputs matched the checkpoint byte for byte. Full Czech/Chinese article hashes also matched with the portable kernel. The full ARM
suite passed 502 tests; focused projection tests passed 29 portable cases with intrinsics
disabled and 29 under x64 Rosetta (56 ARM-specific cases skipped in each). These checks cover
rows/widths/output tails, overflowing pairs, parallel finite/infinite ties across partitions,
UNK, NaNs, all negative infinities and scratch reuse. Rosetta validates correctness only;
native x86 optimization and performance measurements remain deferred.

## Idle memory retention

`TranslationOptions` bounds idle memory per loaded model. Active translations can exceed these
limits; workers are assessed when returned to the pool. The defaults retain ordinary Czech and
Chinese article buffers instead of rebuilding them on each translation:

| Option | Default | Scope |
|---|---:|---|
| `MaxIdleWorkers` | 16 | Combined serial and internally parallel workers per model |
| `MaxIdleWorkerPoolBytes` | 128 MiB | Total retained scratch capacity per model |
| `MaxIdleWorkerBytes` | 96 MiB | Workers above this size are discarded on return |
| `MaxCachedModels` | 4 | LRU model count, allowing temporary excess while models are in use |
| `ModelIdleTimeout` | 5 minutes | Time since the last translation lease was returned |

Scratch accounting includes actual array capacities for all retained float buffers and both
quantized activation arrays, plus conservative 64-bit array headers/alignment. Shared model
weights and per-call arrays are excluded. Serial and internally parallel workers are kept in
separate lists, so each mode takes its own kind, but share the count and byte limits. A returned
worker that would exceed a pool limit evicts the least recently returned idle workers of either
mode; a burst of one mode therefore cannot lock the other out. Only a worker larger than
`MaxIdleWorkerBytes` or the whole pool is discarded. Zero worker limits disable idle worker
retention; zero cached models keeps models only while leased. Negative limits and nonpositive
idle timeouts are rejected.

`LocalTranslator` sweeps idle models without needing another translation request, at intervals
of at most one minute (at least one millisecond for very short configured timeouts). The timer
runs only while idle models exist: it is armed when a model's last lease is returned and
disarmed when no idle model remains, so an empty or fully leased cache never wakes the process.
The timer holds a weak reference, so it cannot keep an abandoned translator and its models alive.
Disposing `LocalTranslator` stops the sweep and clears idle models. Models being loaded or translated are leased and protected against automatic
TTL/LRU eviction, including both legs of a pivot. Explicit `UnloadModels()` immediately detaches
the current cache generation after a model update; existing translations finish with their old
leases, while subsequent requests load the new generation. Returning an old lease cannot evict
a newer generation.

These policies bound idle retention, not peak active memory. Discarding a worker requires new
scratch allocation on its next use; evicting a model adds model loading/JIT latency on the next
request. Memory becomes collectible when a lease or worker is released; actual reclamation
still depends on the GC. Directly loaded `TranslationModel` instances have bounded idle workers
but remain alive as long as their callers retain them.

Measured against checkpoint `3e9dd7cb436` on the same M5/.NET environment as model loading.
Each scenario used a separate process, then four further translations; warm times below are
medians of the last three calls and are subject to machine/scheduler variation. MB is decimal.
All ten scenario/model output hashes matched the checkpoint. No active-memory cap or peak-RSS
improvement is claimed by these idle-retention measurements.

| Scenario | Idle workers before / after | Owned scratch before / after | Live managed memory after GC before / after |
|---|---:|---:|---:|
| 24 simultaneous Czech emails | 24 / 16 | 84.7 / 56.5 MB | 135.2 / 106.9 MB |
| 24 simultaneous Chinese excerpts | 24 / 16 | 26.8 / 17.9 MB | 129.9 / 121.0 MB |
| Large Czech operation | 1 / 1 | 289.2 / 9.2 MB | 340.3 / 60.3 MB |
| Large Chinese operation | 1 / 0 | 266.8 / 0 MB | 371.1 / 104.4 MB |

The large workload repeats each article three times with `MaxBatchTokens = 8192`, exercising
oversized retained buffers rather than the normal 1024-token setting. The Czech run ended with
a smaller batch whose worker could be kept; the Chinese run discarded its last worker. Before
an explicit collection, managed memory at the end of the large Czech operation was 704.3 / 603.8
MB; after collection it was 340.3 / 60.3 MB. These are end-of-operation snapshots, not active
peaks. After the Chinese worker was discarded, the first small translation allocated 1.57 MB
instead of 0.43 MB to rebuild scratch; subsequent calls returned to 0.43 MB. Repeated normal
articles retained their original 37.3 MB Czech / 55.0 MB Chinese worker capacities, with unchanged
warm allocations of 74.9 / 38.3 MB per article and times of 2842 / 2890 ms Czech and
4429 / 4289 ms Chinese (baseline / changed).

On ten-core `AcrossSentences` articles, the default retained all ten workers within the byte
budget; warm times were 974 / 1018 ms Czech and 1623 / 1537 ms Chinese. The rejected four-worker
default roughly doubled allocation churn (Czech about 80 to 160 MB/article); the sixteen-worker
default avoided that count-induced rebuilding. Scheduling determines which capacities each
worker reaches, so retained totals and AcrossSentences allocations vary between runs.

For a short-timeout demonstration, `ModelIdleTimeout = 200 ms` followed by a 500 ms pause
released the idle cache without a subsequent request. After GC, live managed memory fell from
54.1 to 0.41 MB for Czech and 104.4 to 0.35 MB for Chinese; the checkpoint retained 54.1 / 104.4
MB. The next translation, including reload, took 335 / 373 ms versus the checkpoint's warm
131 / 151 ms. Production's five-minute timeout avoids this cold-load cost during ordinary use.

## Reused decoder contexts

Each worker now owns separate key/value context buffers for every decoder layer. The first key
buffer also supplies the encoder transpose scratch, then is overwritten with the first decoder
context. Other contexts are allocated after encoding, preserving the original first-use
lifetime order. All valid values are overwritten, and transposition clears each sentence's
padding lanes before reuse, so changing batch layouts cannot expose stale keys. Recurrent
cells remain freshly zero-initialized per batch.

Context arrays grow exactly to the required capacity, without doubling or shrinking on smaller
requests. This avoids retaining an extra batch's worth of every layer's context and prematurely
exceeding the idle budget. Outer reference arrays, all inner array capacities and conservative
headers/alignment are included in the worker's retained-byte count. The existing 96 MiB per
worker / 128 MiB total idle limits apply to these contexts together with other scratch; an
oversized worker releases its contexts when discarded. No global array pool hides these costs.

Compared with checkpoint `54fffd9ef43` on Apple M5/.NET 10.0.13 ARM64, with the same full Czech
and Chinese articles. Each separate process warmed for at least two seconds, then made eight
measured calls. Times/allocations below are medians of the last five. Owned idle scratch is the
final pool capacity; live managed memory was collected immediately after the initial warmup,
before those eight calls, so the snapshots differ when worker capacities grow later. MB is
decimal. These are retained-memory measurements, not active-memory or native-RSS peaks.

| Article, mode | Wall time before / after | Allocated before / after | Final owned idle scratch before / after | Live after initial warmup before / after |
|---|---:|---:|---:|---:|
| Czech, None | 2717 / 2769 ms | 74.906 / 4.812 MB | 29.15 / 42.07 MB | 79.90 / 92.82 MB |
| Czech, AcrossSentences | 910 / 924 ms | 76.624 / 8.235 MB | 73.53 / 109.86 MB | 124.31 / 159.46 MB |
| Chinese, None | 4153 / 4250 ms | 38.296 / 2.725 MB | 38.65 / 47.29 MB | 142.69 / 151.33 MB |
| Chinese, AcrossSentences | 1441 / 1449 ms | 39.588 / 4.570 MB | 71.82 / 89.96 MB | 172.12 / 185.93 MB |

The tradeoff is about 93% lower serial allocation and 88-89% lower AcrossSentences allocation,
with more idle memory retained: initial warm live memory grew by 12.9 MB for serial Czech and
35.1 MB for Czech AcrossSentences. These runs measured latency 0.6-2.4% slower; repeated process
comparisons would be needed to distinguish code effects from machine variation. Serial worker and context
array identities stayed unchanged across the final five calls. AcrossSentences kept the same
ten workers in both models, below the default aggregate budget; exact-growth context arrays
sometimes expanded when scheduling gave a worker a larger batch, rather than because of pool
eviction. Final retained contexts totaled 12.91 MB / 8.63 MB for serial Czech/Chinese and
36.33 MB / 18.15 MB across ten workers. A later smaller request keeps those capacities until
the worker or model is evicted.

All four measured text outputs matched the checkpoint byte for byte. The ARM suite passed 510
tests, including poisoned-padding checks with changing sentence layouts, separate layer
ownership, exact growth, budget rejection, and existing long/short, cancellation and concurrent
translation scenarios. Focused tests also verify that encoder scratch retains the first
context's identity without allocating the other contexts early.

## Tokenize initial sentences once

Initial sentence filtering now retains its tokenization for the first translation model. A
request-owned internal wrapper records whether the original encoding was empty before adding
EOS once; the engine consumes that same list. Segmentation/ranges and whitespace assembly are
unchanged. Pivot legs still tokenize their incoming translated strings with their own
vocabulary, without resegmentation or another initial empty-sentence filter. Tokens and text
are not added to model/worker caches, and public APIs are unchanged.

Against checkpoint `c132f530943`, isolated preprocessing measured the old filter/discard plus
string-to-engine encoding boundary against the new retained-token boundary, using the same
unchanged tokenizer implementation. Complete token-ID/EOS checksums matched on every run.
After ten warmup pairs, 100 old/new pairs alternated order; medians below use the final 50
pairs. This excludes model loading, decoder computation and output assembly. Allocations use
`GC.GetAllocatedBytesForCurrentThread`; times use `Stopwatch.GetTimestamp`.

| Input | Preprocessing time before / after | Allocated before / after |
|---|---:|---:|
| Czech email | 0.508 / 0.293 ms | 76.3 / 46.3 KB |
| Czech article | 16.56 / 8.40 ms | 2.287 / 1.405 MB |
| Chinese article | 4.93 / 2.58 ms | 1.125 / 0.630 MB |

The full translation comparison used separate Apple M5/.NET 10.0.13 ARM64 processes, at least
two seconds of warmup and four measured calls per case. MB is decimal. The article allocation
reduction was about 18% on one thread, rather than a halving of total translation work:

| Input, mode | Wall time before / after | Allocated before / after |
|---|---:|---:|
| Czech email, None | 133 / 73 ms* | 0.2313 / 0.2012 MB |
| Czech email, WithinProducts | 69 / 67 ms | 0.4828 / 0.4525 MB |
| Czech email, AcrossSentences | 68 / 67 ms | 0.4481 / 0.4180 MB |
| Czech article, None | 2765 / 2746 ms | 4.8118 / 3.9300 MB |
| Czech article, AcrossSentences | 990 / 904 ms | 12.1376 / 10.1554 MB |
| Chinese article, None | 4233 / 4367 ms | 2.7252 / 2.2304 MB |
| Chinese article, AcrossSentences | 1484 / 1454 ms | 6.7634 / 6.4577 MB |

*The initial serial email timing was not repeatable: two further 24-call process comparisons
(last eight calls) measured 76 / 101 ms and 91 / 74 ms, reversing direction. Its allocation
saving stayed near 30 KB. No consistent overall latency gain is established; preprocessing is
only a small part of article translation. AcrossSentences allocations also include variable
context-buffer growth as workers receive different batches. Serial warm live managed memory
stayed near 92.8 MB Czech / 151.3 MB Chinese; tokenization introduces no retained worker state.

All nine before/after text comparisons (seven primary cases and two repeated email cases)
matched byte for byte. The full ARM suite passed 517 tests, including one tokenizer invocation
per initial candidate, EOS/normalized-empty handling, mixed-text ranges and ordering, both
split modes, and pivot equivalence with independently tokenized vocabularies. Existing
cancellation and concurrent translation scenarios also passed.

## x86 kernel

See the [native x86 performance handoff](X86-PERFORMANCE.md) for remaining experiments,
arithmetic constraints, and a reproducible benchmark and memory-measurement workflow.

On x86 with AVX2, the int8 products use `PMADDUBSW`: it multiplies the shifted activations
(unsigned, value + 127) by the signed weights and adds adjacent pairs with int16 saturation,
which is exactly intgemm's Int8Shift product. The sums therefore need neither the
127 x row-sum shift nor the saturation correction, and `PMADDWD` (or AVX-VNNI `VPDPWSSD`)
widens them to int32. Weights are packed in panels of 16 output columns with four adjacent
inputs per column. Each step broadcasts four activation bytes against 64 weight bytes, so every
int32 lane is one output column and there is no horizontal reduction. AVX-512BW processes a
panel in one 512-bit vector, and AVX2 in two 256-bit vectors. Four activation rows share each
weight load, and the two rows left over after four-row steps share it as well. The full-vocabulary output layer uses the same panels with the fused argmax. The
shortlisted output layer keeps row-major weights and a `PMADDUBSW` row kernel. Quantization
stores only the shifted activations: the x86 kernel needs neither the signed int8 copy nor the
portable kernel's int16 copy.

With AVX-VNNI but without AVX-512, the panel kernels use 256-bit `VPDPBUSD`, which adds four
exact products to int32 in one instruction; the pairs that intgemm saturates to int16 are then
corrected from the matrix's saturation metadata, as on ARM, so the results stay identical (see
"VPDPBUSD with exact correction" in [X86-PERFORMANCE.md](X86-PERFORMANCE.md)). 512-bit `VPDPBUSD`
would halve the AVX-512 multiplies the same way, but .NET 10 exposes it only through AVX10, which
Zen 5 does not report; .NET 11 adds `AvxVnni.V512`. A microbenchmark of the AVX-512 kernel reached 260-280 GMAC/s on one
Zen 5 core for 4 to 64 activation rows, about 80% of the two vector multiply ports.

The `IsSupported` checks appear directly at each dispatch site, rather than behind a helper
property, so the JIT drops the other platforms' branches even in tier 0. There is no runtime
kernel choice: the portable kernel runs where the CPU has neither, or with the runtime switches
`DOTNET_EnableArm64Dp=0` and `DOTNET_EnableAVX2=0`, which is how the tests and the output checks
cover it. (An `Int8Kernel` value and `TranslationOptions.UsePortableKernel` used to select it on
any CPU; the products tested that value in their loops.) Removing the value changed no output and
no warm time measurably (Apple M5, one thread, 6-8 alternating runs: cs-en with and without the
shortlist within 1%, the portable kernel within 2%). `DOTNET_EnableArm64Dp=0` itself makes the
same portable code about 4% slower than the old option did, so compare portable timings only
under the same switch.

Two further changes apply to all platforms. Attention scores and attention-weighted values
use `Vector512`/`Vector256` where available. Each lane is still one key or one output column
summed in the original order, so the results do not change. Attention computes two heads at
a time, which keeps twice as many of these in-order sums in flight
([x86 experiment 6](X86-PERFORMANCE.md#results)). The x86 argmax also compares a
whole 16-column panel with the row's current winner before checking lanes.

`AcrossSentences` now starts one worker per physical core, not per logical processor
(`ProcessorTopology`: `GetLogicalProcessorInformationEx` on Windows, `hw.physicalcpu` on macOS,
sysfs on Linux and Android). SMT siblings share the vector multiply ports. With 32 workers on
16 cores, the Czech article took the same wall time for twice the CPU time, and the Chinese
article's longest batch, which bounds its wall time, ran 15% slower. On Windows, only cores
with a logical processor in the process's affinity mask count. With the x86 kernel,
`WithinProducts` splits products from 2^22 multiply-adds into chunks of at least 2^21, at most
one per physical core; the other kernels keep the thresholds above and up to 4 chunks per
logical processor (see [x86 experiment 4](X86-PERFORMANCE.md#results)).

AMD Ryzen 9 9950X (Zen 5, 16 cores, 32 threads, AVX-512), Windows 11, .NET 10.0.13, Release,
compared with `4abc86b19e1`. Baseline and changed binaries ran one process at a time on an
otherwise idle machine, alternating, three processes per case. Times are the medians of the
processes' warm averages, excluding model loading. The inputs are the first 3,147 words of the
Czech Wikipedia article "Karel IV.", the full Chinese article "长城" (10,689 non-whitespace
characters), and the Czech article's first 108 words as an email.

| Input, mode | Wall time before / after | CPU time before / after | Less time |
|---|---:|---:|---:|
| Czech email, `None` | 188 / 38 ms | 201 / 44 ms | 80% |
| Czech email, `WithinProducts` | 103 / 30 ms | 245 / 61 ms | 71% |
| Czech email, `AcrossSentences` | 69 / 23 ms | 233 / 81 ms | 67% |
| Czech article, `None` | 5,008 / 955 ms | 5,047 / 1,000 ms | 81% |
| Czech article, `WithinProducts` | 1,838 / 554 ms | 9,371 / 1,312 ms | 70% |
| Czech article, `AcrossSentences` | 452 / 137 ms | 12,167 / 1,591 ms | 70% |
| Chinese article, `None` | 15,633 / 2,981 ms | 15,641 / 3,023 ms | 81% |
| Chinese article, `WithinProducts` | 3,753 / 1,259 ms | 28,661 / 5,229 ms | 66% |
| Chinese article, `AcrossSentences` | 1,803 / 686 ms | 39,727 / 8,391 ms | 62% |

On one thread, the kernel alone took the Czech article from 5,008 to about 1,085 ms and the
Chinese article from 15,633 to about 3,445 ms. The wider attention and the argmax check took
them to about 958 and 3,046 ms (11% each). Most of the `AcrossSentences` CPU saving comes from
the physical-core worker count. The first translation after starting the process, including
model loading and JIT, fell from about 790 to about 525 ms for the email.

All outputs matched the baseline byte for byte: both articles in all three modes, with AVX-512
and with AVX-512 disabled (`DOTNET_EnableAVX512=0`, the AVX2 path; these runs originally used
`DOTNET_EnableAVX512F=0`, which .NET 10 ignores, and were repeated with the effective setting in
[x86 experiment 3](X86-PERFORMANCE.md#results)). The suite passes with
AVX-512, AVX2, AVX2 without VNNI, and the portable kernel (`DOTNET_EnableAVX2=0`). The kernel
and output projection tests cover the x86 kernel on row-major and packed weights, partial
panels, saturating pairs and parallel partitions.

After the x86 kernel, one-thread time on the Czech article is about 53% int8 products and 14%
attention. In the Chinese article it is 64% products and 11% attention.

On Windows, `tools/Translate` used to read redirected standard input in the OEM code page,
so non-ASCII input was garbled before translation. It now reads and writes UTF-8, and
`--input <file>` reads a file. That option is also needed for profiling, because `dotnet-trace` does not forward
standard input.

## Platform math

`MuslMath` ports musl's `expf` and `sincosf` so that results are bit-identical to Firefox's
WASM build. They are used by the scalar softmax (attention rows whose length is not a multiple
of 4) and by the sinusoidal position embeddings. Building with `-p:TarjamotPlatformMath=true`
replaces them with `MathF.Exp` and `MathF.SinCos` and removes `MuslMath`.

Marian calls `sinf` and `cosf` separately. musl's `sincosf` uses the same kernels and range
branches, but checks the range and reduces the argument only once. The position scales
`exp(i * -log(10000) / (dim / 2 - 1))` do not depend on the position, so they are computed once
per model. On `Vector128` hardware, four values at a time go through a branch-free `sincosf`
(each lane picks its reduction and quadrant by masks, with no fused multiply-add). The scalar and
vector versions matched `sinf`/`cosf` bit for bit on every float below `2^28 * pi/2` (the scalar
one on both signs) on Arm64, and on x64 (Rosetta) with AVX2 and with `DOTNET_EnableAVX2=0`.
Translations did not change.

The table of positions 0-511 (`TarjamotMicrobenchmark positions`, Apple M5, .NET 10.0.13, ranges
over runs) took 1.9-2.1 µs per position with dim 384 and 2.4-2.9 µs with dim 512 before. The
scalar `sincosf` takes 1.0-1.3 and 1.4-1.8 µs, and the vector one 0.7-1.2 and 0.9-1.0 µs. Each
model fills the table once per process and doubles it as sentences get longer, so this saves
about 1 ms of a first translation. Angle-addition recurrences and the platform's libm would be
faster still, but change the last bits.

On Windows (.NET 10.0.13, the C runtime's libm), `MathF` differed from musl in 0.07% of `expf`
results over softmax inputs in [-30, 0]. It also differed in 0.12-0.14% of `sinf`/`cosf`
results for positions 0-1023, by at most 1 ulp. The position scales did not differ. Other
platforms' libm may round differently, so with this option, output could differ between
platforms.

No sample translation changed. The full Czech article (about 10,400 words, cs-en), the full
Chinese article (zh-Hans-en), the email, and both English outputs translated to German (en-de)
and Japanese (en-ja), about 33,000 source words in all, were byte-identical with either math.
The tests pass with either; none checks the musl bits directly. Warm speed did not change
measurably (softmax is about 2% of one-thread time). A cold first translation looked about
60 ms faster, but cold timings vary too much to confirm this. A 1-ulp difference can still flip
a near-tie between output tokens, so identical output is likely, not guaranteed.

## Threads

`TranslationOptions.Parallelism` chooses how threads are used. All modes give identical output.

- `WithinProducts` (default): large matrix products (the encoder and the full-vocabulary output
  layer) are split across threads, and batches of sentences run one after another.
- `AcrossSentences`: the text is cut into about two batches per physical core, and the batches
  run at once on single-threaded workers that share the weights.
- `None`: one thread.

`AcrossSentences` is faster but not more efficient. Spreading the batches has almost no software
overhead: ten independent single-threaded processes reach the same throughput. With all cores
busy, though, memory bandwidth, the efficiency cores and lower clock speeds cap an Apple M5 at
about 2.7x one core. CPU time per article grew from 5.2 s with one process to 6.5 s with two,
8.5 s with four and 9.2 s with ten. Batch size itself hardly matters: on one thread, cutting the
article into 10 batches costs nothing and into 40 batches about 17%. Measurements are in the
commit that introduced the option.
