# AGENTS.md

Guidance for coding agents working in this repository.

## Layout

- `src/Tarjamot`: the library (NuGet package `Tarjamot`, MIT). `Engine/` is the Marian inference,
  `Projection/` the default HTML mode (`ProjectedMarkupTranslator`).
- `src/Tarjamot.BergamotMarkup`: Bergamot's HTML mode (NuGet package `Tarjamot.BergamotMarkup`,
  MPL-2.0), which matches Firefox. It uses the engine's internals, so it depends on exactly the
  same version.
- `tests/Tarjamot.Tests`: NUnit tests. `ExternalServices` tests download models from Mozilla;
  set `TARJAMOT_TEST_MODELS` to keep them in a fixed directory.
- `tools/Translate`: command-line translation. `tools/Benchmark`: benchmark harness,
  microbenchmarks and scripts. `tools/MarkupComparison`: compares the two HTML modes.
- `docs/`: performance notes and measurements.

## Commands

```
dotnet build -c Release
dotnet test -c Release --filter "TestCategory!=ExternalServices"
dotnet test -c Release
dotnet pack src/Tarjamot -c Release -o artifacts
dotnet pack src/Tarjamot.BergamotMarkup -c Release -o artifacts
```

Kernels depend on the CPU: run the tests with `DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX2=0`,
`DOTNET_EnableArm64Dp=0` or `DOTNET_EnableHWIntrinsic=0` to cover the fallbacks.

## Conventions

- Tabs for indentation in `.cs` files, UTF-8 without BOM, `\uXXXX` escapes for non-ASCII
  characters in source.
- Outputs are meant to match Firefox's engine: keep the integer arithmetic and the order of
  floating-point sums when optimizing, and check translations byte for byte before and after.
- Files ported from MPL-2.0 code keep their MPL-2.0 header, go in `src/Tarjamot.BergamotMarkup`
  (never in the MIT `src/Tarjamot`), and are listed in `THIRD-PARTY-NOTICES.md`. Never copy code
  from Bergamot or from `src/Tarjamot.BergamotMarkup` into `src/Tarjamot`.
- Pure .NET: no native code.

## Releasing

Versions come from Nerdbank.GitVersioning: `version.json` holds major.minor, the patch is the
number of commits since it changed. Both packages get the same version. Builds of `main` are prereleases (`0.1.5-g1a2b3c4d`); a
`vX.Y.Z` tag on the same commit makes the public release `X.Y.Z`. To release, tag the commit with
`dotnet nbgv tag <commit>` (the local tool in `dotnet-tools.json`, after `dotnet tool restore`) and push the tag. CI
packs it and publishes it to nuget.org with Trusted Publishing (no API key in the repository).
Bump `version` in `version.json` to start the next minor version.

The workflow does not use Nerdbank.GitVersioning's version environment variables, and
`version.json` turns them off (`cloudBuild.setVersionVariables`): with multi-targeting, the
parallel MSBuild nodes of Nerdbank.GitVersioning 3.10 corrupt `GITHUB_ENV` when they all append
them, and the runner then fails the build step.

One-time setup: a nuget.org Trusted Publishing policy for `filipnavara/Tarjamot`, workflow file
`ci.yml`, environment `nuget`, and the repository secret `NUGET_USER` with the nuget.org profile
name that owns the policy.
