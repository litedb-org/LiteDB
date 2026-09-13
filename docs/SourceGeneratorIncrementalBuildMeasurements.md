# Source-generator incremental build measurements

## Purpose and scope

This document records the first controlled **P2.3** measurement of the LiteDB source-generator consumer build loop. It establishes a reproducible baseline before any change to the generator’s centralized `models.Collect()` / single-registrar architecture is considered. It does **not** change LiteDB runtime behavior, the source-generator mapping algorithm, emitted registration API, package contents, or Native AOT support.

The measurement is deliberately a synthetic consumer rather than a claim about an external application. It creates 128 independent `[BsonSourceGenerated]` model files. Each model has eight persisted, supported properties: `int`, `string`, nullable `int`, `DateTimeOffset`, nullable `DateTimeOffset`, `List<string>`, rank-one `string[]`, and `Dictionary<string, object?>`. The consumer also has one explicit `LiteDbGeneratedMappings.Register` call, so the source generator must emit the centralized registrar that a real consumer uses.

> The script measures a controlled end-to-end **CLI build**. It does not prove internal Roslyn transform-cache hits, IDE design-time responsiveness, or the build duration of a particular production application.

## Reproduction

Run the following command from the repository root. The default is five samples; the synthetic project is created under a temporary directory and removed on exit.

```bash
TZ=Europe/Istanbul ./scripts/measure-source-generator-incremental-build.sh --samples 5
```

The script normally resolves `dotnet` from `PATH`. A reset or isolated environment can pass an absolute SDK command without changing the script:

```bash
DOTNET_COMMAND=/path/to/dotnet TZ=Europe/Istanbul \
  ./scripts/measure-source-generator-incremental-build.sh --samples 5
```

Use `--keep-workspace` only when inspection of the generated disposable consumer is required. The harness reports the retained directory and otherwise removes it through an exit trap.

| Controlled input | Value |
| --- | --- |
| Target framework | `net8.0` |
| Model files | 128 independently compiled annotated classes |
| Persisted supported properties per model | 8 |
| Baseline mutation target | `MeasurementModel064.cs` |
| Source change | `BsonField("measurement_name")` → `BsonField("measurement_name_updated")` |
| Restore timing | Performed outside timed regions |
| Clean sample | Removes the synthetic consumer’s `bin` and `obj`, restores, then builds Release |
| Update sample | Rewrites only model 64 and performs the following Release build |
| Timed command | `dotnet build --configuration Release --no-restore --nologo -p:GitVersionEnabled=false -p:UseSharedCompilation=false` |
| Correctness guards | Exactly one generated mapping file; registrar contains model 128 and the mutated field name |

The script first runs one untimed warmup. It then records each clean baseline and immediately following one-model-update build in seconds, plus arithmetic mean and median. The default disabled shared compilation makes every sample’s compiler-host setup explicit and avoids ambient server reuse. Consequently, these results are a stable command-line build-loop baseline, not a measurement of persistent IDE incremental-driver state.

## Recorded local result

The following observation was captured on the development sandbox using .NET SDK **8.0.130**, Linux `6.18.38+` (`x86_64`), and six logical processors. It is a local observation only; it must not be used as an absolute performance target on another machine or SDK.

| Build kind | Raw elapsed seconds | Mean seconds | Median seconds |
| --- | --- | ---: | ---: |
| Clean baseline | 6.451, 6.125, 6.395, 6.812, 6.673 | 6.491 | 6.451 |
| One-model update | 6.406, 6.216, 7.251, 6.479, 6.473 | 6.565 | 6.473 |

The one-model-update median was **0.022 seconds** above the clean median in this run. That difference is smaller than the observed sample spread and is not evidence that centralized aggregation is a material bottleneck. The update samples still perform normal CLI compilation work; they should not be interpreted as evidence that the in-memory Roslyn incremental pipeline recomputed every transform or that it reused every transform.

## Decision and evolution trigger

**Decision: retain the centralized `Collect()` and one-registrar design.** The baseline establishes that a 128-model, 1,024-property synthetic consumer builds in approximately 6.5 seconds in this controlled environment, but it does not identify centralized registry emission as a dominant cost. A per-model generated-file redesign would alter generated-source shape, registration ordering, and package-consumer assumptions without sufficient evidence.

Reopen aggregation design only when the owner records all of the following evidence:

1. The script or an equivalent documented consumer measurement is reproduced on the declared target SDK and machine class.
2. A one-model source update demonstrably misses the owner’s stated build-loop target by a material, repeatable margin.
3. Profiling or a focused driver experiment attributes a meaningful portion of that cost to centralized collection/emission rather than restore, compiler startup, unrelated compilation, or I/O.
4. A separate design review preserves deterministic model ordering, the single explicit `LiteDbGeneratedMappings.Register` seam, existing fail-closed diagnostics, package-consumer behavior, and Native AOT validation.

Until those conditions are met, the present architecture is the simpler, compatibility-preserving choice.
