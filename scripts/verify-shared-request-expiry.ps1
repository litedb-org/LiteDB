$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$entry = (Get-Content .github/safety/regression-proofs.json -Raw | ConvertFrom-Json).unpublishedInterleavingProofs |
    Where-Object id -eq 'p3-expired-request-admission'
if (!$entry -or @($entry).Count -ne 1) { throw 'Missing unique expired-request proof registration.' }
$output = Join-Path $root 'artifacts_temp/request-expiry-proof'
New-Item -ItemType Directory -Force $output | Out-Null
$before = Join-Path ([IO.Path]::GetTempPath()) ('litedb-expiry-proof-' + [Guid]::NewGuid().ToString('N'))
$method = 'Expired_live_request_reenters_after_the_already_published_successor'
$class = 'SharedWriterTenureRequestSafety_Tests'
$testSource = 'LiteDB.Tests/Internals/SharedWriterTenureRequestSafety_Tests.cs'
$results = @()

function Test-Revision([string]$checkout, [string]$variant, [bool]$knownBad) {
    Push-Location $checkout
    try {
        & dotnet build LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 -p:TestingEnabled=true -p:GitVersionEnabled=false | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$variant build failed." }
        $directory = Join-Path $output $variant
        & dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 --no-build --no-restore `
            -p:TestingEnabled=true --settings tests.runsettings `
            --filter "FullyQualifiedName~.$class.$method|FullyQualifiedName~TestHost_Tests" `
            --logger 'trx;LogFileName=proof.trx' --results-directory $directory | Out-Host
        $testExit = $LASTEXITCODE
        [xml]$trx = Get-Content (Join-Path $directory 'proof.trx')
        $rows = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_ -and $_.testName })
        $target = @($rows | Where-Object { $_.testName.EndsWith(".$class.$method") })
        if ($rows.Count -ne 3 -or $target.Count -ne 1) { throw "$variant has an incomplete or unexpected case set." }
        foreach ($guard in @('RequestedRuntimeAndArchitecture_AreActuallyRunning', 'LoadedLibrary_ContainsTheRequiredEngineTestHooks')) {
            if (@($rows | Where-Object { $_.testName.EndsWith(".$guard") -and $_.outcome -eq 'Passed' }).Count -ne 1) {
                throw "$variant lacks a passing $guard guard."
            }
        }
        if ($knownBad) {
            if ($testExit -eq 0 -or $target[0].outcome -ne 'Failed' -or
                $target[0].Output.ErrorInfo.Message -notmatch '(?s)Expected admittedBeforeRecheck.*to be 0.*found 1') {
                throw 'Known-bad revision did not fail at the expired-ticket admission oracle.'
            }
        } elseif ($testExit -ne 0 -or @($rows | Where-Object outcome -ne 'Passed').Count) {
            throw 'Candidate did not pass the same regression and both host guards.'
        }
        return [ordered]@{ variant = $variant; revision = (git rev-parse HEAD); exitCode = $testExit;
            targetOutcome = $target[0].outcome; passedGuards = 2; testSourceHash = (Get-FileHash $testSource -Algorithm SHA256).Hash }
    } finally { Pop-Location }
}

try {
    & git worktree add --detach $before $entry.knownBadRevision
    if ($LASTEXITCODE -ne 0) { throw 'Cannot check out the registered real known-bad revision.' }
    if ((Get-FileHash (Join-Path $before $testSource)).Hash -ne (Get-FileHash $testSource).Hash) {
        throw 'Before/after must use identical permanent regression source.'
    }
    $results += Test-Revision $before 'known-bad' $true
    $results += Test-Revision $root 'candidate' $false
    [ordered]@{ id = $entry.id; scope = $entry.scope; results = $results } |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'proof.json')
} finally {
    if (Test-Path $before) {
        & git worktree remove --force $before
        if ($LASTEXITCODE -ne 0) { throw 'Cannot clean up the known-bad checkout.' }
    }
}
