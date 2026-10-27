param(
    [Parameter(Mandatory = $true)][int]$RuntimeMajor,
    [Parameter(Mandatory = $true)][string]$Framework,
    [ValidateSet('x64', 'x86', 'arm64')][string]$Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant(),
    [string]$Filter,
    [string]$ResultFile = 'TestResults.trx',
    [string]$RuntimeDirectory,
    [switch]$PartitionSuite,
    [string]$VerifyPartitions,
    [string]$DiscoveryFile
)

$ErrorActionPreference = 'Stop'
if ($IsLinux -and !$env:LITEDB_CI_TMPDIR) {
    # Keep the original temp volume, with a private base shared by parent and children.
    $privateTmp = Join-Path ([IO.Path]::GetTempPath()) ("litedb-ci-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $privateTmp | Out-Null
    & chmod 700 -- $privateTmp
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the Linux test temp directory private.' }
    $env:TMPDIR = $privateTmp
    $env:LITEDB_CI_TMPDIR = $privateTmp
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$temporary = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
if (!$RuntimeDirectory) {
    $RuntimeDirectory = Join-Path $temporary "litedb-runtime-$RuntimeMajor-$Architecture"
    # Explicit versions bypass aka.ms, which can point at an unavailable release.
    $metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$RuntimeMajor.0/releases.json"
    try {
        $metadata = Invoke-RestMethod -Uri $metadataUrl -ErrorAction Stop
    } catch {
        throw "Could not fetch .NET $RuntimeMajor runtime release metadata from ${metadataUrl}: $($_.Exception.Message)"
    }
    $runtimeVersion = $metadata.'latest-runtime'
    if ($runtimeVersion -notmatch "^$RuntimeMajor\.0\.\d+$") {
        throw "Invalid latest-runtime '$runtimeVersion' in .NET $RuntimeMajor release metadata from $metadataUrl."
    }
    Write-Host "Installing .NET runtime $runtimeVersion ($Architecture)."
    if ($IsWindows) {
        $installer = Join-Path $temporary 'litedb-dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Runtime dotnet -Version $runtimeVersion -Architecture $Architecture -InstallDir $RuntimeDirectory -NoPath
    } else {
        $installer = Join-Path $temporary 'litedb-dotnet-install.sh'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.sh' -OutFile $installer
        & bash $installer --runtime dotnet --version $runtimeVersion --architecture $Architecture --install-dir $RuntimeDirectory --no-path
        if ($LASTEXITCODE -ne 0) { throw 'Runtime installation failed.' }
    }
}
$hostName = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
$testHost = Join-Path $RuntimeDirectory $hostName
if (!(Test-Path $testHost)) { throw "Test runtime host not found: $testHost" }
& $testHost --info
if ($LASTEXITCODE -ne 0) { throw 'Test runtime host could not start.' }

# Run disjoint slices in separate test sessions, each still limited to 300 seconds.
# The final complement includes new namespaces automatically. Every slice also runs
# the runtime/architecture and hook guards below; no tests are sampled or skipped.
# Each partition is a conjunction of FullyQualifiedName contains (~) / not-contains (!~)
# clauses. A clause can contain OR alternatives (|), so the coverage check below
# can evaluate the filters exactly as VSTest does.
if ($PartitionSuite) {
    if ($Filter) { throw 'PartitionSuite cannot be combined with Filter.' }
    $groups = [ordered]@{
        issues = 'FullyQualifiedName~LiteDB.Tests.Issues.'
        rebuild = 'FullyQualifiedName~LiteDB.Tests.Engine.Rebuild'
        'engine-compact' = 'FullyQualifiedName~LiteDB.Tests.Engine.Compact'
        'engine-index' = 'FullyQualifiedName~LiteDB.Tests.Engine.Index'
        # Windows x86 TRX timings: separate shared coordination and allocation work
        # from the engine complement (docs/ci-partition-capacity.md).
        'engine-shared' = 'FullyQualifiedName~LiteDB.Tests.Engine.&FullyQualifiedName~Shared&FullyQualifiedName!~LiteDB.Tests.Engine.Rebuild&FullyQualifiedName!~LiteDB.Tests.Engine.Compact&FullyQualifiedName!~LiteDB.Tests.Engine.Index&FullyQualifiedName!~LiteDB.Tests.Engine.Allocation'
        # Keep the observed x86 allocation stall below the 180-second capacity
        # target by isolating WAL failures and the three other heavy classes.
        'engine-allocation-wal' = 'FullyQualifiedName~LiteDB.Tests.Engine.AllocationWalFailure_Tests'
        'engine-allocation-stress' = 'FullyQualifiedName~LiteDB.Tests.Engine.AllocationConcurrent_Tests|FullyQualifiedName~LiteDB.Tests.Engine.AllocationPowerLoss_Tests|FullyQualifiedName~LiteDB.Tests.Engine.AllocationOracle_Tests'
        # This complement intentionally catches future Allocation test classes.
        'engine-allocation' = 'FullyQualifiedName~LiteDB.Tests.Engine.Allocation&FullyQualifiedName!~LiteDB.Tests.Engine.AllocationWalFailure_Tests&FullyQualifiedName!~LiteDB.Tests.Engine.AllocationConcurrent_Tests&FullyQualifiedName!~LiteDB.Tests.Engine.AllocationPowerLoss_Tests&FullyQualifiedName!~LiteDB.Tests.Engine.AllocationOracle_Tests'
        engine = 'FullyQualifiedName~LiteDB.Tests.Engine.&FullyQualifiedName!~LiteDB.Tests.Engine.Rebuild&FullyQualifiedName!~LiteDB.Tests.Engine.Compact&FullyQualifiedName!~LiteDB.Tests.Engine.Index&FullyQualifiedName!~Shared&FullyQualifiedName!~LiteDB.Tests.Engine.Allocation'
        'query-not-equal' = 'FullyQualifiedName~LiteDB.Tests.QueryTest.NotEqualIndex_Tests'
        query = 'FullyQualifiedName~LiteDB.Tests.QueryTest.&FullyQualifiedName!~LiteDB.Tests.QueryTest.NotEqualIndex_Tests'
        # Mapped storage and coordination upgrades dominate shared-process timings.
        'shared-mapped-process' = 'FullyQualifiedName~LiteDB.Internals.SharedMappedProcess'
        'shared-upgrade-process' = 'FullyQualifiedName~LiteDB.Internals.SharedCoordinationUpgradeProcess'
        'shared-process' = 'FullyQualifiedName~LiteDB.Internals.Shared&FullyQualifiedName~Process&FullyQualifiedName!~LiteDB.Internals.SharedMappedProcess&FullyQualifiedName!~LiteDB.Internals.SharedCoordinationUpgradeProcess'
        # The old shared session exhausted 300 s while still completing cases.
        # Keep process-death matrices and tenure reviews independently bounded.
        'shared-maintenance-crash' = 'FullyQualifiedName~LiteDB.Internals.SharedOneFileMaintenanceCrash&FullyQualifiedName!~Process'
        'shared-final-close' = 'FullyQualifiedName~LiteDB.Internals.SharedOneFileFinalClose&FullyQualifiedName!~Process'
        'shared-rebuild' = 'FullyQualifiedName~LiteDB.Internals.SharedOneFileRebuild&FullyQualifiedName!~Process'
        'shared-writer-tenure' = 'FullyQualifiedName~LiteDB.Internals.SharedWriterTenure&FullyQualifiedName!~Process'
        shared = 'FullyQualifiedName~LiteDB.Internals.Shared&FullyQualifiedName!~Process&FullyQualifiedName!~LiteDB.Internals.SharedOneFileMaintenanceCrash&FullyQualifiedName!~LiteDB.Internals.SharedOneFileFinalClose&FullyQualifiedName!~LiteDB.Internals.SharedOneFileRebuild&FullyQualifiedName!~LiteDB.Internals.SharedWriterTenure'
        mvcc = 'FullyQualifiedName~LiteDB.Internals.Mvcc'
        internals = 'FullyQualifiedName~LiteDB.Internals.&FullyQualifiedName!~LiteDB.Internals.Shared&FullyQualifiedName!~LiteDB.Internals.Mvcc'
        remaining = 'FullyQualifiedName!~LiteDB.Tests.Issues.&FullyQualifiedName!~LiteDB.Tests.Engine.&FullyQualifiedName!~LiteDB.Tests.QueryTest.&FullyQualifiedName!~LiteDB.Internals.'
    }
    $results = Join-Path $repoRoot 'LiteDB.Tests/TestResults'
    New-Item -ItemType Directory -Force $results | Out-Null
    & $PSCommandPath -RuntimeMajor $RuntimeMajor -Framework $Framework -Architecture $Architecture `
        -RuntimeDirectory $RuntimeDirectory -VerifyPartitions ($groups | ConvertTo-Json -Compress) `
        -DiscoveryFile (Join-Path $results 'discovered-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Test partitions do not cover every test exactly once.' }
    # Record what this leg tested so the Safety evidence job can prove the run is
    # complete and current (.github/scripts/check_test_evidence.py).
    $build = Join-Path $repoRoot 'LiteDB.Tests/bin/Release/build-evidence.json'
    $evidence = [ordered]@{
        schemaVersion = 1
        job = $env:GITHUB_JOB
        matrix = if ($env:LITEDB_EVIDENCE_MATRIX) { $env:LITEDB_EVIDENCE_MATRIX | ConvertFrom-Json } else { $null }
        sha = (& git -C $repoRoot rev-parse HEAD)
        tree = (& git -C $repoRoot rev-parse 'HEAD^{tree}')
        buildSha = if (Test-Path $build) { (Get-Content $build -Raw | ConvertFrom-Json).sha } else { $null }
        runtimeMajor = $RuntimeMajor
        framework = $Framework
        architecture = $Architecture
        runtimeDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        format = 'trx'
        discovery = 'discovered-tests.txt'
        partitions = [ordered]@{}
    }
    foreach ($group in $groups.GetEnumerator()) { $evidence.partitions[$group.Key] = "TestResults-$($group.Key).trx" }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $results 'evidence-leg.json') -Encoding utf8
    $failed = @()
    foreach ($group in $groups.GetEnumerator()) {
        Write-Host "Running complete-suite partition: $($group.Key)"
        & $PSCommandPath -RuntimeMajor $RuntimeMajor -Framework $Framework -Architecture $Architecture `
            -RuntimeDirectory $RuntimeDirectory -Filter $group.Value -ResultFile "TestResults-$($group.Key).trx"
        if ($LASTEXITCODE -ne 0) { $failed += $group.Key }
    }
    if ($failed.Count) { throw "Test partitions failed: $($failed -join ', ')" }
    exit 0
}

# The SDK running VSTest can have other runtimes installed. Pin testhost to the
# isolated installation; roll net8.0 forward there when testing on .NET 9.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:LITEDB_EXPECTED_RUNTIME_MAJOR = [string]$RuntimeMajor
$env:LITEDB_EXPECTED_ARCHITECTURE = $Architecture
$assembly = Join-Path $repoRoot "LiteDB.Tests/bin/Release/$Framework/LiteDB.Tests.dll"

# A partition set must place every discovered test method in exactly one session:
# a gap would silently skip tests, an overlap would run them twice.
if ($VerifyPartitions) {
    $partitions = $VerifyPartitions | ConvertFrom-Json
    $listing = Join-Path $temporary "litedb-partition-tests-$([guid]::NewGuid().ToString('N')).txt"
    & dotnet vstest $assembly "/Framework:.NETCoreApp,Version=v$RuntimeMajor.0" "/Platform:$Architecture" `
        /ListFullyQualifiedTests "/ListTestsTargetPath:$listing" -- "RunConfiguration.DotNetHostPath=$testHost" | Out-Null
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $listing)) { throw 'Could not list the test methods.' }
    $names = @(Get-Content $listing | Where-Object { $_ })
    if ($DiscoveryFile) { Move-Item -Force $listing $DiscoveryFile } else { Remove-Item $listing }
    $problems = @()
    foreach ($name in $names) {
        $matched = @($partitions.PSObject.Properties | Where-Object {
            $allClausesMatch = $true
            foreach ($clause in ($_.Value -split '&')) {
                $clauseMatches = $false
                foreach ($alternative in ($clause -split '\|')) {
                    $alternative = $alternative.Trim('() ')
                    if ($alternative -like 'FullyQualifiedName!~*') {
                        $clauseMatches = $clauseMatches -or !$name.Contains($alternative.Substring(20), [StringComparison]::OrdinalIgnoreCase)
                    }
                    elseif ($alternative -like 'FullyQualifiedName~*') {
                        $clauseMatches = $clauseMatches -or $name.Contains($alternative.Substring(19), [StringComparison]::OrdinalIgnoreCase)
                    }
                    else { throw "Unsupported partition clause: $alternative" }
                }
                if (!$clauseMatches) {
                    $allClausesMatch = $false
                    break
                }
            }
            $allClausesMatch
        } | ForEach-Object { $_.Name })
        if ($matched.Count -ne 1) { $problems += "$name -> [$($matched -join ', ')]" }
    }
    Write-Host "Partition coverage: $($names.Count) test methods, $($problems.Count) not in exactly one partition."
    $problems | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
    exit ([int]($problems.Count -ne 0))
}

$results = Join-Path $repoRoot 'LiteDB.Tests/TestResults'
$resultPath = Join-Path $results $ResultFile
if (Test-Path $resultPath) { Remove-Item $resultPath }
$arguments = @(
    'vstest', $assembly,
    "/Framework:.NETCoreApp,Version=v$RuntimeMajor.0", "/Platform:$Architecture",
    "/Settings:$(Join-Path $repoRoot 'tests.runsettings')", "/ResultsDirectory:$results",
    "/Logger:trx;LogFileName=$ResultFile", '/Logger:console;verbosity=minimal'
)
if ($Filter) { $arguments += "/TestCaseFilter:($Filter)|FullyQualifiedName~TestHost_Tests" }
# Per-test inactivity capture distinguishes a stalled Shared operation from a
# session that simply exhausts its unchanged 300-second cumulative budget.
if ($Filter -match 'Shared') {
    $arguments += '/Blame:CollectHangDump;TestTimeout=120s;HangDumpType=mini'
}
$arguments += '--', "RunConfiguration.DotNetHostPath=$testHost"
& dotnet @arguments
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0 -and !(Test-Path $resultPath)) {
    # VSTest occasionally exits on macOS runners without any output or result file
    # (seen in the query partition). Record what is known and retry once with a
    # diagnostic log; the guards below still decide whether the retry counts.
    Write-Host "VSTest exited with $exitCode and wrote no result file ($resultPath); retrying once with diagnostics."
    $diag = Join-Path $results "vstest-diag-$([IO.Path]::GetFileNameWithoutExtension($ResultFile)).log"
    $retry = @($arguments[0..($arguments.IndexOf('--') - 1)]) + "/Diag:$diag" + @($arguments[$arguments.IndexOf('--')..($arguments.Count - 1)])
    & dotnet @retry
    $exitCode = $LASTEXITCODE
    foreach ($log in @(Get-ChildItem -Path $results -Filter 'vstest-diag-*' -ErrorAction SilentlyContinue)) {
        Write-Host "--- last lines of $($log.Name)"
        Get-Content $log.FullName -Tail 40 | ForEach-Object { Write-Host $_ }
    }
    if ($exitCode -ne 0 -and !(Test-Path $resultPath)) { Write-Host "Retry also wrote no result file." }
}
if ($exitCode -ne 0) { exit $exitCode }

# A green run must actually execute these guards, including filtered CI jobs.
[xml]$report = Get-Content $resultPath
foreach ($guard in @('RequestedRuntimeAndArchitecture_AreActuallyRunning', 'LoadedLibrary_ContainsTheRequiredEngineTestHooks')) {
    $passed = @($report.TestRun.Results.UnitTestResult | Where-Object {
        $_.testName.EndsWith($guard) -and $_.outcome -eq 'Passed'
    })
    if ($passed.Count -ne 1) { throw "Required CI guard did not pass: $guard" }
}
