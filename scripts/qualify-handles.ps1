param([string]$Framework = 'net8.0')
$ErrorActionPreference = 'Stop'
$output = 'artifacts_temp/handle-qualification'
New-Item -ItemType Directory -Force $output | Out-Null
$classes = @(Get-ChildItem LiteDB.Tests -Recurse -Filter 'TransactionHandle*Tests.cs' | Where-Object {
    $_.BaseName -match 'Wait|Lifetime|Process|Abandon|Close|Deadline|Cooperative'
} | Sort-Object BaseName | ForEach-Object { $_.BaseName })
if (!$classes.Count) { throw 'Empty handle stress inventory' }
$filter = ($classes | ForEach-Object { "FullyQualifiedName~.$_." }) -join '|'
$names = $null
for ($repeat = 1; $repeat -le 50; $repeat++) {
    $name = "stress-$repeat.trx"
    dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f $Framework --no-build --no-restore `
        -p:TestingEnabled=true --settings tests.runsettings --filter "($filter)|FullyQualifiedName~TestHost_Tests" `
        --logger "trx;LogFileName=$name" --results-directory $output
    if ($LASTEXITCODE) { throw "Stress iteration $repeat failed" }
    [xml]$trx = Get-Content (Join-Path $output $name)
    $results = @($trx.TestRun.Results.UnitTestResult)
    if ($trx.TestRun.ResultSummary.outcome -ne 'Completed' -or @($results | Where-Object outcome -ne 'Passed').Count) {
        throw "Incomplete/failed/skipped stress iteration $repeat"
    }
    $current = @($results.testName | Sort-Object)
    if ($null -ne $names -and (Compare-Object $names $current)) { throw 'Stress test set changed' }
    $names = $current
    foreach ($class in $classes) {
        if (!@($results | Where-Object { $_.testName -like "*$class.*" }).Count) { throw "Missing class $class" }
    }
}
@{ sha = (git rev-parse HEAD); classes = $classes; repetitions = 50; tests = $names } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'inventory.json')
