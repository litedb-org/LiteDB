# iOS AOT validation

This report records the iOS simulator and physical-device validation performed against commit
`d774a4167893ec9e88026e96594bc605bbaf0fef` on 2026-09-17. It covers two different
AOT environments:

1. .NET for iOS using Mono full AOT and NativeAOT.
2. Unity using IL2CPP.

Both applications were compiled as ARM64 binaries, installed into an Apple
CoreSimulator device, launched as applications, and verified from their runtime
output. The Unity IL2CPP application and both .NET AOT variants were subsequently
built for iPhoneOS, signed, installed, and run on a physical iPhone as well.

## Result summary

| Test | Runtime and target | Result | Important qualification |
| --- | --- | --- | --- |
| .NET for iOS | .NET 10, `net10.0-ios`, `iossimulator-arm64`, Mono full AOT | Passed | `RuntimeFeature.IsDynamicCodeSupported` and `IsDynamicCodeCompiled` were both `false`, so LiteDB selected the new single-argument expression path automatically. |
| .NET for iOS, physical device | .NET 10, `net10.0-ios`, `ios-arm64`, Mono full AOT | Passed | Release build with `PublishAot=false` and `UseInterpreter=false`; the bundle contained 24 Mono AOT-data files, including LiteDB, and the full smoke suite exited 0. |
| .NET for iOS, physical device | .NET 10, `net10.0-ios`, `ios-arm64`, NativeAOT | Passed | The clean bundle contained no managed DLLs or Mono AOT-data files, and the same full smoke suite exited 0. |
| Unity iOS, default behavior | Unity 6 IL2CPP, ARM64 iOS Simulator | Passed | Unity's class library did not expose `RuntimeFeature.IsDynamicCodeSupported`; LiteDB therefore retained its ordinary five-argument delegate path. IL2CPP precompiled that path successfully. |
| Unity iOS, forced PR path | Unity 6 IL2CPP, ARM64 iOS Simulator | Passed | The test explicitly enabled `UseSingleArgumentDelegates` before running the workload. This directly exercised the expression path added for runtimes without dynamic code. |
| Unity iOS, physical device | Unity 6 IL2CPP, ARM64 iPhoneOS Release build on an iPhone 13 Pro Max | Passed | The signed player ran on iOS 26.7 with the PR path forced; its console and copied result file both reported PASS. |

## Shared environment

- Host: Apple silicon macOS
- .NET SDK: 10.0.400
- .NET iOS workload: 26.5.10301/10.0.100
- Xcode for the reproducible simulator and physical-device reruns: 27.0 (build 27A266a)
- Simulator: iPhone 17 Pro, iOS 26.3
- Physical device: iPhone 13 Pro Max, iOS 26.7
- Architecture: ARM64
- LiteDB configuration: Release, `TestingEnabled=false`

Apple calls this environment the iOS Simulator rather than an emulator. It runs
ARM64 application code but still differs from an iPhone in OS integration, signing,
hardware, and deployment behavior.

## Reproducing the tests

The repository now contains both application fixtures and self-checking wrappers:

- `integration/LiteDB.iOSAotSmoke` is the .NET UIKit application. It links the shared
  `LiteDB.AotSmokeTests` scenarios into the app.
- `scripts/validate-ios-aot.sh` cleans, builds or publishes, inspects, installs, launches, and
  verifies the .NET simulator, device Mono full-AOT, and device NativeAOT modes. The
  clean between modes prevents stale Mono payload files from making a NativeAOT
  package ambiguous.
- `integration/LiteDB.UnityIosAotSmoke` is the minimal Unity project. The wrapper
  builds the current LiteDB `netstandard2.0` assembly and stages it with its exact
  `System.Runtime.CompilerServices.Unsafe` dependency before Unity imports it.
- `scripts/validate-unity-ios-aot.sh` exports IL2CPP, runs the Xcode build, installs
  the player, and checks the console and physical-device result marker. It supports
  both Unity's default delegate behavior and the forced single-argument PR path.

Run the simulator matrix on an Apple-silicon Mac with the .NET iOS workload, an iOS
Simulator, Unity 6000.0.84f1 with iOS Build Support, Rosetta 2, and Xcode:

```bash
./scripts/validate-ios-aot.sh simulator
./scripts/validate-unity-ios-aot.sh simulator-all
```

For a connected, unlocked, paired iPhone with Developer Mode enabled, supply the
machine-specific signing values without storing them in the repository:

```bash
IOS_DEVICE_ID=<device-udid> \
IOS_CODESIGN_KEY='Apple Development: Name (TEAMID)' \
  ./scripts/validate-ios-aot.sh device-all

IOS_DEVICE_ID=<device-udid> \
IOS_DEVELOPMENT_TEAM=<team-id> \
  ./scripts/validate-unity-ios-aot.sh device-forced
```

Set `DEVELOPER_DIR` to select a non-default Xcode. If the installed .NET iOS workload
and Xcode version deliberately differ, also set `IOS_VALIDATE_XCODE_VERSION=false`;
the default keeps the workload's version check enabled. Logs, exported Xcode projects,
and derived data are retained under `artifacts/ios-aot-validation` and
`artifacts/unity-ios-aot-validation`.

## Test 1: .NET for iOS with Mono full AOT and NativeAOT

### Setup

The checked-in UIKit application targets `net10.0-ios` with these relevant settings
for its simulator mode:

```xml
<RuntimeIdentifier>iossimulator-arm64</RuntimeIdentifier>
<SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
<PublishAot>false</PublishAot>
<UseInterpreter>false</UseInterpreter>
<TrimMode>full</TrimMode>
```

The application referenced the LiteDB and LiteDB.SourceGenerator projects directly.
It linked the reusable scenarios from `LiteDB.AotSmokeTests` into the application and
ran them from `UIApplicationDelegate.FinishedLaunching`. The generated application
bundle contained ARM64 Mono AOT data for LiteDB and its dependencies.

The application was built, inspected, installed, and launched with:

```bash
./scripts/validate-ios-aot.sh simulator
```

The checked-in test project is `integration/LiteDB.iOSAotSmoke`; the product scenarios
it reuses remain in `LiteDB.AotSmokeTests`.

### Coverage

The application verified:

- `RuntimeFeature.IsDynamicCodeSupported == false` and
  `RuntimeFeature.IsDynamicCodeCompiled == false`;
- document inserts, indexes, queries, nested expressions, SQL, persistence, and reopen;
- source-generated typed mappings, scalar boundaries, nullable values, inheritance,
  computed properties, arrays, lists, dynamic dictionaries, and LINQ translation;
- commit, rollback, encryption, checkpoint, pragmas, rebuild, culture-specific
  collation, shared connections, concurrent writers, and durable reopen;
- vector index creation and nearest-neighbour search;
- file storage upload, queries, download, deletion, integer IDs, and class IDs;
- all 111 registered expression methods, all five registered expression functions,
  and 41 operator/path forms; and
- every SQL statement kind, every `SELECT` clause in the sweep, and the system
  collections exercised by `SqlSweepScenarios`.

### Outcome

The application exited successfully with:

```text
Runtime: .NET 10.0.11
Dynamic code supported: False
Dynamic code compiled: False
[PASS] Document, expression, SQL, index, and durable-reopen scenario
[PASS] Source-generated mapping scenarios
LITEDB_IOS_AOT_RESULT=PASS
```

Because dynamic code was reported unavailable, this run automatically exercised
LiteDB's single-argument interpreted expression delegate. It is direct runtime
evidence that the new path works in a .NET iOS ARM64 simulator application.

### Physical-device follow-up

The same application and complete workload were run in two signed Release builds on
an iPhone 13 Pro Max running iOS 26.7. Both builds set `UseInterpreter=false`, so
neither could escape an AOT failure by interpreting all managed application code.

The Mono full-AOT build used `ios-arm64` with `PublishAot=false`, which is the normal
.NET iOS device runtime. Its application bundle contained 24 `.aotdata.arm64` files,
including `LiteDB.aotdata.arm64`, alongside the corresponding managed assemblies. It
was built, installed, and launched with the equivalent of:

```bash
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
IOS_VALIDATE_XCODE_VERSION=false \
IOS_DEVICE_ID=<device-udid> \
IOS_CODESIGN_KEY='Apple Development: Name (TEAMID)' \
  ./scripts/validate-ios-aot.sh device-all
```

The Mono application reported both dynamic-code flags as `false`, passed the full
workload listed above, emitted `LITEDB_IOS_AOT_RESULT=PASS`, and exited with code 0.
This is direct physical-device coverage of the Mono full-AOT runtime involved in
#2804, without `MtouchInterpreter` fallback.

The second build enabled `PublishAot=true`. After cleaning the shared output first,
its application bundle contained a 7.7 MB ARM64 Mach-O executable and no managed DLLs
or Mono `.aotdata.arm64` files, confirming that this was the NativeAOT variant rather
than a stale Mono package. It produced the same runtime flags, PASS markers, and exit
code 0 on the phone.

The installed .NET iOS workload expects Xcode 26.6. Xcode 27 was selected explicitly
and the workload's Xcode-version validation was disabled. Both packages compiled,
signed, installed, and ran successfully, but this toolchain combination is newer than
the workload's declared Xcode version and is therefore a qualification on the result.

## Test 2: Unity iOS with IL2CPP

### Setup

The Unity fixture used:

- Unity 6 LTS 6000.0.84f1 with iOS Build Support;
- the IL2CPP scripting backend;
- the iOS Simulator SDK with ARM64 simulator architecture;
- iOS 15.0 as the minimum target;
- High managed stripping;
- the Release `netstandard2.0` LiteDB assembly and its exact
  `System.Runtime.CompilerServices.Unsafe` dependency; and
- a `link.xml` entry preserving the complete LiteDB assembly.

Unity generated the IL2CPP C++ sources, including LiteDB, and exported an Xcode
project. Xcode then compiled and linked the application for
`arm64-apple-ios-simulator`. The resulting Mach-O application was installed and
launched through `simctl`; this was not only a Unity export or Xcode compile check.

The checked-in reproduction command is:

```bash
./scripts/validate-unity-ios-aot.sh simulator-all
```

The Unity fixture is checked in under `integration/LiteDB.UnityIosAotSmoke`; generated
Unity state, Xcode output, staged assemblies, logs, and derived data remain ignored.

### Coverage

The Unity workload used the `BsonDocument` API and verified:

- that the player was compiled with `ENABLE_IL2CPP`;
- file-backed inserts and reads;
- index creation and a parameterized indexed query;
- scalar, MAP, FILTER, document-initializer, and ANY expressions;
- a SQL projection and predicate; and
- persistence across database disposal and reopen.

The workload ran twice:

1. With LiteDB's automatically selected behavior. Unity did not expose
   `RuntimeFeature.IsDynamicCodeSupported`, so `UseSingleArgumentDelegates` was
   `false`. The complete workload passed.
2. With `UseSingleArgumentDelegates` set to `true` through reflection before any
   expression was compiled. The same complete workload passed, directly validating
   the PR's single-argument expression path under IL2CPP.

### Outcome

The forced-path run reported:

```text
Scripting backend: IL2CPP
Unity: 6000.0.84f1; platform: IPhonePlayer
RuntimeFeature.IsDynamicCodeSupported: <missing>
LiteDB single-argument expression delegates: True
LiteDB document, index, expression, SQL, persistence, and reopen scenarios passed.
LITEDB_UNITY_IOS_IL2CPP_RESULT=PASS
```

For the physical-device run, the result marker was also read back from the installed
application's data container, not only from its console stream.

### Physical-device follow-up

After Developer Mode was enabled, the Unity project was exported for the iPhoneOS
Device SDK. Xcode 27 compiled it against the iOS 27.0 SDK as a Release ARM64
application, signed it with an Apple Development certificate and development
provisioning profile, and installed it on an iPhone 13 Pro Max running iOS 26.7.

The application was launched with its console attached through `devicectl`. It ran on
the Apple A15 GPU and reported:

```text
Build type 'Release', Scripting Backend 'il2cpp'
Scripting backend: IL2CPP
Unity: 6000.0.84f1; platform: IPhonePlayer
RuntimeFeature.IsDynamicCodeSupported: <missing>
LiteDB single-argument expression delegates: True
LiteDB document, index, expression, SQL, persistence, and reopen scenarios passed.
LITEDB_UNITY_IOS_IL2CPP_RESULT=PASS
```

`devicectl` remained attached because the Unity player did not exit after writing its
result, so the command reached its 30-second timeout. The explicit PASS marker had
already been emitted and `Documents/unity-result.txt` was copied from the phone with
the same PASS value; the process was then terminated normally. The timeout is a
harness shutdown issue, not a LiteDB test failure.

### Interpretation of Unity's runtime detection

The result proves both expression implementations work in the tested Unity IL2CPP
player. It does not prove that Unity selects the new path automatically: it does not.
The `netstandard2.0` probe deliberately treats missing runtime metadata as dynamic
code being available, preserving the previous behavior. That behavior worked in this
IL2CPP build because IL2CPP generated the required delegate code ahead of time.

This is not an observed Unity correctness failure. It is an integration decision that
still needs to be made: either document the ordinary delegate path as the supported
IL2CPP behavior, or add a supported way for Unity applications to request the
single-argument path without private reflection.

## What remains untested

The following gaps remain after the simulator and physical-device runs:

- The original #2804 reproducer on a physical device, including confirmation that it
  fails before this change and passes after it without `MtouchInterpreter` fallback.
- A controlled before/after run against the PR base commit. These tests establish that
  the current commit passes; they do not by themselves measure the regression delta.
- The .NET physical-device runs with the workload's expected Xcode 26.6. Xcode 27
  passed with version validation disabled, so the exact supported toolchain pairing
  remains unqualified.
- App Store archive, distribution signing, TestFlight, and release-build deployment.
- Mac Catalyst, tvOS, and visionOS AOT targets.
- Older supported iOS releases, older Unity LTS versions, and older IL2CPP class
  library profiles.
- Unity without `link.xml` preserving all of LiteDB. High stripping was enabled, but
  preserve-all means this run does not validate LiteDB metadata survival under an
  aggressively minimal linker configuration.
- Source-generated typed collections inside Unity. The Unity workload used the
  document API; source-generated mapping was covered only by the .NET iOS test.
- Unity coverage for encryption, file storage, vector search, concurrent writers,
  rebuild, shared mode, and the complete expression and SQL sweeps.
- Long-running, memory-pressure, low-disk, crash-recovery, background/foreground,
  forced-termination, and upgrade-from-an-older-package scenarios.
- Performance, startup time, memory use, and application-size comparisons between the
  ordinary and single-argument expression paths.

## Recommended next steps

1. Repeat the Mono full-AOT physical-device run with Xcode 26.6, the version expected
   by the installed workload, and run the original #2804 reproducer before and after
   this change.
2. Add the checked-in simulator fixtures to a suitably provisioned macOS CI runner so
   the no-dynamic-code and IL2CPP paths run automatically. Physical-device signing and
   deployment remain an intentionally manual gate.
3. Decide the Unity contract: retain and document IL2CPP's default delegate path, or
   expose and test a supported opt-in for the single-argument path. Do not rely on
   private reflection in application code.
4. Extend the checked-in Unity fixture with a minimal linker-preservation mode in
   addition to the current documented preserve-all production configuration.
5. Repeat the most relevant runs against the PR base to demonstrate the exact behavior
   changed by the PR, then add the physical-device result to #2804.
