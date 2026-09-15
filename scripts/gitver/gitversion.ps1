[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Ref = 'HEAD',
    [string]$Branch,
    [switch]$Json,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')
$manifestPath = Join-Path $repoRoot '.config/dotnet-tools.json'
$gitVersionConfig = Join-Path $repoRoot 'GitVersion.yml'

if (-not (Test-Path $manifestPath)) {
    throw "Tool manifest not found at $manifestPath"
}

if (-not (Test-Path $gitVersionConfig)) {
    throw "GitVersion configuration not found at $gitVersionConfig"
}

$sha = ((& git -C $repoRoot rev-parse --verify --quiet "$Ref").Trim())
if (-not $sha) {
    throw "Unable to resolve git ref '$Ref'"
}

if (-not $Branch) {
    $candidates = (& git -C $repoRoot branch --contains $sha) | ForEach-Object {
        ($_ -replace '^[*+\s]+', '').Trim()
    } | Where-Object { $_ }

    foreach ($candidate in @('dev', 'develop', 'master', 'main')) {
        if ($candidates -contains $candidate) {
            $Branch = $candidate
            break
        }
    }

    if (-not $Branch -and $candidates) {
        $Branch = $candidates[0]
    }
}

if (-not $Branch) {
    $Branch = "gv-temp-" + $sha.Substring(0, 7)
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("litedb-gv-" + [Guid]::NewGuid().ToString('N'))

try {
    git clone --quiet --local --no-hardlinks "$repoRoot" "$tempRoot"

    Push-Location -LiteralPath $tempRoot

    git checkout -B "$Branch" "$sha" | Out-Null

    if (-not (Test-Path '.config')) {
        New-Item -ItemType Directory -Path '.config' | Out-Null
    }

    Copy-Item -Path $manifestPath -Destination '.config/dotnet-tools.json' -Force
    Copy-Item -Path $gitVersionConfig -Destination 'GitVersion.yml' -Force

    if (-not $NoRestore) {
        dotnet tool restore | Out-Null
    }

    $jsonText = dotnet tool run dotnet-gitversion /output json

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet-gitversion failed'
    }

    if ($Json) {
        $jsonText
        return
    }

    $data = $jsonText | ConvertFrom-Json

    $semVer = $data.MajorMinorPatch
    if ([string]::IsNullOrEmpty($data.PreReleaseLabel) -eq $false) {
        $preNumber = [int]$data.PreReleaseNumber
        $semVer = '{0}-{1}.{2:0000}' -f $data.MajorMinorPatch, $data.PreReleaseLabel, $preNumber
    }

    $line = '{0,-22} {1}'
    Write-Host ($line -f 'Resolved SHA:', $sha)
    Write-Host ($line -f 'FullSemVer:', $semVer)
    Write-Host ($line -f 'NuGetVersion:', $semVer)
    Write-Host ($line -f 'Informational:', "$semVer+$($data.ShortSha)")
    Write-Host ($line -f 'BranchName:', $data.BranchName)
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    if (Test-Path $tempRoot) {
        Remove-Item -Recurse -Force $tempRoot
    }
}
