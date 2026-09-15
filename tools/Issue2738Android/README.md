# Issue 2738: actual Android encrypted-file creation

This APK executes the retained lifecycle regression on Android. It runs Direct
and Shared mode, each with an absent file and a pre-created empty file, three
times. Each case checks the encrypted header, acknowledged IDs and payloads,
updates, deletion, and index queries across independent reopens.

On an Android x86_64 device or emulator already running under `adb`:

```sh
python3 tools/Issue2738Android/run.py --sdk /path/to/android-sdk \
  --jdk /path/to/jdk-17 --serial emulator-5580 --output /tmp/issue2738
```

Install the .NET 10 `android` workload and Android SDK dependencies first. The
project's `InstallAndroidDependencies` target accepts `AndroidSdkDirectory`,
`JavaSdkDirectory`, and `AcceptAndroidSdkLicenses=true`. Build support alone does
not count as an Android observation: the driver requires the APK's actual
Android runtime flag, expected package/source build metadata, and all 12 cases.
It clears application data between variants and retains the complete JSON results.
The supplied driver targets `android-x64`; use an x86_64 emulator.

Observed 2026-09-15 on Android 15 (API 35, Google APIs x86_64 emulator), .NET
10.0.11:

| Variant | Executed cases | Reported failures | Other failures |
| --- | ---: | ---: | ---: |
| LiteDB 5.0.21 | 12 | 0 | 0 |
| dev source `a50661a9d1a25b5713586d0096c6325d76bf5dfe` | 12 | 0 | 0 |

An independent second invocation of the retained driver repeated all 24 cases
successfully. This is an actual Android no-reproduction result for the specified
fresh-file lifecycle. It does not establish what originally existed at the
reporter's path, or whether another application path opened/rebuilt it without
a password. The separate parameterless-Rebuild defect is not counted as #2738.
