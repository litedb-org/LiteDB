param([Parameter(Mandatory = $true)][string]$Framework)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
Set-Location $root
$output = 'artifacts_temp/macos-qualification'
New-Item -ItemType Directory -Force $output | Out-Null
# Inventory comes from source, so newly added mapped classes enter automatically.
$classes = @(Get-ChildItem LiteDB.Tests -Recurse -Filter '*Tests.cs' | Where-Object {
    (Get-Content $_.FullName -Raw) -match '\[OneFile(Fact|Theory)' -or
    $_.BaseName -like 'SharedOneFile*' -or
    $_.BaseName -match '^(SharedPlatformWake|SharedIntegrationSeam|DirectProcessHostRekey)_Tests$'
} | Sort-Object BaseName)
$inventory = @()
$batches = @()
$failed = $false
$stress = $env:QUALIFICATION_STRESS -eq 'true'
$diagnostic = $env:REQUEST_SAFETY_DIAGNOSTIC -eq 'true'
$repetitions = if ($stress -or $diagnostic) { 50 } else { 1 }
foreach ($file in $classes) {
    $class = $file.BaseName
    if ($diagnostic -and $class -ne 'SharedWriterTenureRequestSafety_Tests') { continue }
    if ($stress -and $class -notmatch 'Tenure|Wake|Coherence|Process') { continue }
    $source = Get-Content $file.FullName -Raw
    # Linux kernel syscall assertions have no Darwin equivalent. They remain in
    # the ordinary full CI matrix; retain their explicit eligibility inventory.
    $linuxOnly = @([regex]::Matches($source, '\[LinuxFutex(?:Fact|Theory)\][\s\S]*?public void (\w+)') | ForEach-Object { $_.Groups[1].Value })
    if ($class -eq 'SharedOneFileLock_Tests') {
        # This ordinary Fact returns before assertions on Darwin; inventory the
        # Linux POSIX/OFD control explicitly rather than count a vacuous pass.
        $linuxOnly += 'Linux_negative_control_POSIX_close_drops_lock_but_OFD_close_and_duplicate_do_not'
    }
    $filter = "FullyQualifiedName~.$class."
    foreach ($method in $linuxOnly) { $filter += "&FullyQualifiedName!~.$method" }
    $inventory += [ordered]@{ class = $class; linuxOnly = $linuxOnly; repetitions = $repetitions }
    if ($class -eq 'SharedOneFileMaintenanceCrash_Tests') {
        # This class exceeded the unchanged 300-second session budget on Intel.
        # Keep every theory/case/recovery repetition, with guards in each session.
        $methods = @([regex]::Matches($source, 'public async Task (\w+)\(') | ForEach-Object { $_.Groups[1].Value })
        foreach ($method in $methods) {
            $batches += [pscustomobject]@{ name = "$class-$method"; filter = "($filter&FullyQualifiedName~.$method)"; classes = @($class) }
        }
    } else {
        $batches += [pscustomobject]@{ name = $class; filter = "($filter)"; classes = @($class) }
    }
}
if ($stress -and !$diagnostic) {
    # Repeat the complete stress set in fifty fresh test hosts. Each class must
    # appear in each TRX; runtime/hook guards execute once per actual test host.
    $batches = @([pscustomobject]@{ name = 'stress'; filter = ($batches.filter -join '|'); classes = @($inventory.class) })
}
foreach ($batch in $batches) {
    for ($repeat = 1; $repeat -le $repetitions; $repeat++) {
        $name = "$($batch.name)-$repeat.trx"
        & dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f $Framework --no-build --no-restore `
            -p:TestingEnabled=true --settings tests.runsettings `
            --filter "($($batch.filter))|FullyQualifiedName~TestHost_Tests" --blame `
            --logger "trx;LogFileName=$name" --results-directory $output
        if ($LASTEXITCODE -ne 0) { $failed = $true }
        $path = Join-Path $output $name
        if (!(Test-Path $path)) { $failed = $true; continue }
        [xml]$trx = Get-Content $path
        $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_ -and $_.testName })
        foreach ($class in $batch.classes) {
            if (!@($results | Where-Object { $_.testName -like "*$class.*" }).Count) { $failed = $true }
        }
        if (@($results | Where-Object outcome -ne 'Passed').Count) { $failed = $true }
        foreach ($guard in @('RequestedRuntimeAndArchitecture_AreActuallyRunning', 'LoadedLibrary_ContainsTheRequiredEngineTestHooks')) {
            if (@($results | Where-Object { $_.testName.EndsWith($guard) -and $_.outcome -eq 'Passed' }).Count -ne 1) { $failed = $true }
        }
    }
}
[ordered]@{ mode = $(if ($diagnostic) { 'diagnostic' } elseif ($stress) { 'stress' } else { 'full' }); sha = (git rev-parse HEAD); framework = $Framework; architecture = $env:LITEDB_EXPECTED_ARCHITECTURE; classes = $inventory;
    linuxOnlyClasses = @('SharedWriterTenureFutexProbe_Tests') } |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'inventory.json')
if ($failed) { throw 'macOS qualification failed: missing, failed or skipped test results.' }
