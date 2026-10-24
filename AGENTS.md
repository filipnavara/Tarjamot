# AGENTS.md

Guidance for coding agents working in this repository.

## Layout

- `src/Tarjamot`: the library (NuGet package `Tarjamot`). `Engine/` is the Marian inference,
  `Markup/` the HTML mode.
- `tests/Tarjamot.Tests`: NUnit tests. `ExternalServices` tests download models from Mozilla;
  set `TARJAMOT_TEST_MODELS` to keep them in a fixed directory.
- `tools/Translate`: command-line translation. `tools/Benchmark`: benchmark harness,
  microbenchmarks and scripts.
- `docs/`: performance notes and measurements.

## Commands

```
dotnet build -c Release
dotnet test -c Release --filter "TestCategory!=ExternalServices"
dotnet test -c Release
dotnet pack src/Tarjamot -c Release -o artifacts
```

Kernels depend on the CPU: run the tests with `DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`,
`DOTNET_EnableArm64Dp=0` or `DOTNET_EnableHWIntrinsic=0` to cover the fallbacks.

## Conventions

- Tabs for indentation in `.cs` files, UTF-8 without BOM, `\uXXXX` escapes for non-ASCII
  characters in source.
- Outputs are meant to match Firefox's engine: keep the integer arithmetic and the order of
  floating-point sums when optimizing, and check translations byte for byte before and after.
- Files ported from MPL-2.0 code keep their MPL-2.0 header; list new ports in
  `THIRD-PARTY-NOTICES.md`.
- Pure .NET: no native code.
