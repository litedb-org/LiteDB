param(
    [Parameter(Mandatory = $true)][int]$RuntimeMajor,
    [Parameter(Mandatory = $true)][string]$Framework,
    [ValidateSet('x64', 'x86', 'arm64')][string]$Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant(),
    [string]$Filter,
    [string]$ResultFile = 'TestResults.trx',
    [string]$RuntimeDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$temporary = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
if (!$RuntimeDirectory) {
    $RuntimeDirectory = Join-Path $temporary "litedb-runtime-$RuntimeMajor-$Architecture"
    if ($IsWindows) {
        $installer = Join-Path $temporary 'litedb-dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Runtime dotnet -Channel "$RuntimeMajor.0" -Architecture $Architecture -InstallDir $RuntimeDirectory -NoPath
    } else {
        $installer = Join-Path $temporary 'litedb-dotnet-install.sh'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.sh' -OutFile $installer
        & bash $installer --runtime dotnet --channel "$RuntimeMajor.0" --architecture $Architecture --install-dir $RuntimeDirectory --no-path
        if ($LASTEXITCODE -ne 0) { throw 'Runtime installation failed.' }
    }
}
$hostName = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
$testHost = Join-Path $RuntimeDirectory $hostName
if (!(Test-Path $testHost)) { throw "Test runtime host not found: $testHost" }
& $testHost --info
if ($LASTEXITCODE -ne 0) { throw 'Test runtime host could not start.' }

# The SDK running VSTest can have other runtimes installed. Pin testhost to the
# isolated installation; roll net8.0 forward there when testing on .NET 9.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:LITEDB_EXPECTED_RUNTIME_MAJOR = [string]$RuntimeMajor
$env:LITEDB_EXPECTED_ARCHITECTURE = $Architecture
$assembly = Join-Path $repoRoot "LiteDB.Tests/bin/Release/$Framework/LiteDB.Tests.dll"
$results = Join-Path $repoRoot 'LiteDB.Tests/TestResults'
$resultPath = Join-Path $results $ResultFile
if (Test-Path $resultPath) { Remove-Item $resultPath }
$arguments = @(
    'vstest', $assembly,
    "/Framework:.NETCoreApp,Version=v$RuntimeMajor.0", "/Platform:$Architecture",
    "/Settings:$(Join-Path $repoRoot 'tests.runsettings')", "/ResultsDirectory:$results",
    "/Logger:trx;LogFileName=$ResultFile", '/Logger:console;verbosity=detailed'
)
if ($Filter) { $arguments += "/TestCaseFilter:($Filter)|FullyQualifiedName~TestHost_Tests" }
$arguments += '--', "RunConfiguration.DotNetHostPath=$testHost"
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# A green run must actually execute these guards, including filtered CI jobs.
[xml]$report = Get-Content $resultPath
foreach ($guard in @('RequestedRuntimeAndArchitecture_AreActuallyRunning', 'LoadedLibrary_ContainsTheRequiredEngineTestHooks')) {
    $passed = @($report.TestRun.Results.UnitTestResult | Where-Object {
        $_.testName.EndsWith($guard) -and $_.outcome -eq 'Passed'
    })
    if ($passed.Count -ne 1) { throw "Required CI guard did not pass: $guard" }
}
