param([Parameter(Mandatory = $true)][string]$Framework)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
Set-Location $root
$output = 'artifacts_temp/macos-qualification'
New-Item -ItemType Directory -Force $output | Out-Null
# Inventory comes from source, so newly added mapped classes enter automatically.
$classes = @(Get-ChildItem LiteDB.Tests -Recurse -Filter '*Tests.cs' | Where-Object {
    (Get-Content $_.FullName -Raw) -match '\[OneFile(Fact|Theory)' -or
    $_.BaseName -match '^(SharedPlatformWake|SharedMapped|SharedCoordinationUpgrade|DirectProcessHostRekey)_Tests$'
} | Sort-Object BaseName)
$inventory = @()
$failed = $false
$stress = $env:QUALIFICATION_STRESS -eq 'true'
$repetitions = if ($stress) { 50 } else { 1 }
foreach ($file in $classes) {
    $class = $file.BaseName
    if ($stress -and $class -notmatch 'Tenure|Wake|Coherence|Process') { continue }
    $source = Get-Content $file.FullName -Raw
    # Linux kernel syscall assertions have no Darwin equivalent. They remain in
    # the ordinary full CI matrix; retain their explicit eligibility inventory.
    $linuxOnly = @([regex]::Matches($source, '\[LinuxFutex(?:Fact|Theory)\][\s\S]*?public void (\w+)') | ForEach-Object { $_.Groups[1].Value })
    $filter = "FullyQualifiedName~.$class."
    foreach ($method in $linuxOnly) { $filter += "&FullyQualifiedName!~.$method" }
    $inventory += [ordered]@{ class = $class; linuxOnly = $linuxOnly; repetitions = $repetitions }
    for ($repeat = 1; $repeat -le $repetitions; $repeat++) {
        $name = "$class-$repeat.trx"
        & dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f $Framework --no-build --no-restore `
            -p:TestingEnabled=true --settings tests.runsettings `
            --filter "($filter)|FullyQualifiedName~TestHost_Tests" `
            --logger "trx;LogFileName=$name" --results-directory $output
        if ($LASTEXITCODE -ne 0) { $failed = $true }
        $path = Join-Path $output $name
        if (!(Test-Path $path)) { $failed = $true; continue }
        [xml]$trx = Get-Content $path
        $results = @($trx.TestRun.Results.UnitTestResult)
        $actual = @($results | Where-Object { $_.testName -like "*$class.*" })
        if (!$actual.Count -or @($results | Where-Object outcome -ne 'Passed').Count) { $failed = $true }
        foreach ($guard in @('RequestedRuntimeAndArchitecture_AreActuallyRunning', 'LoadedLibrary_ContainsTheRequiredEngineTestHooks')) {
            if (@($results | Where-Object { $_.testName.EndsWith($guard) -and $_.outcome -eq 'Passed' }).Count -ne 1) { $failed = $true }
        }
    }
}
[ordered]@{ sha = (git rev-parse HEAD); framework = $Framework; architecture = $env:LITEDB_EXPECTED_ARCHITECTURE; classes = $inventory } |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'inventory.json')
if ($failed) { throw 'macOS qualification failed: missing, failed or skipped test results.' }
