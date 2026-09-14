# Issue 2320 signing check

This check operates on the artifact a consumer receives. It does not treat the
presence of signing configuration as proof, and it refuses an old signed package
renamed as a current one by checking the nuspec version and (when supplied) commit.

For a published package it requires both:

1. `dotnet nuget verify --all` cryptographically accepts an **Author** signature
   whose certificate subject contains `SignPath Foundation`. A NuGet.org
   **Repository** signature alone is deliberately insufficient.
2. Every `lib/` or `runtimes/` DLL has a well-formed PE `WIN_CERTIFICATE` entry.
   A structurally signed DLL is then checked with Windows
   `Get-AuthenticodeSignature`, or `osslsigncode` on non-Windows hosts. Missing
   platform verification is a harness error, never a green result.

The immutable LiteDB 5.0.21 package is downloaded as a positive control for the
package-author-signature oracle. An optional known Authenticode-signed PE can
exercise the independent PE-header parser.

```sh
python3 scripts/regressions/issue_2320/verify_package_signing.py \
  --version 6.0.0-prerelease.181 \
  --expected-commit a50661a9d1a25b5713586d0096c6325d76bf5dfe
```

Exit codes are intentionally non-overlapping:

- `0`, `VERIFIED_2320_SIGNING`: the published artifact satisfies both contracts.
- `1`, `BUG_2320_CONFIRMED`: valid LiteDB artifact, but a required signature is absent.
- `2`, `HARNESS_2320_ERROR`: download, identity, parser, or verifier failure.
- `3`, `PRE_SIGN_2320_ONLY`: local pack inspected, with no release verdict.

Local output exists *before* any external signing service. Inspect it without
mislabeling that observation as a release regression:

```sh
artifact_dir="$(mktemp -d)"
dotnet pack LiteDB/LiteDB.csproj -c Release -p:TestingEnabled=false -o "$artifact_dir"
package="$(find "$artifact_dir" -maxdepth 1 -name 'LiteDB*.nupkg' -print -quit)"
version="$(basename "$package" .nupkg | sed 's/^LiteDB\.//')"
python3 scripts/regressions/issue_2320/verify_package_signing.py \
  --package "$package" --expected-version "$version" --stage local-pre-sign
```

Strong-name signing is not Authenticode and does not satisfy either assertion.
Do not weaken the Author-signature check to accept a Repository signature, or
the PE check to accept only a nonzero directory without cryptographic validation.
