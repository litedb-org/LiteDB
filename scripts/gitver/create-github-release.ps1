[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Ref = 'HEAD',
    [switch]$Push
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..' '..')

# Restore tools if needed
if (-not (Get-Command dotnet-gitversion -ErrorAction SilentlyContinue)) {
    Write-Host "Restoring .NET tools..."
    dotnet tool restore
}

# Get version info
$jsonText = & "$PSScriptRoot/gitversion.ps1" -Ref $Ref -Json
$data = $jsonText | ConvertFrom-Json

$majorMinorPatch = $data.MajorMinorPatch
$preLabel = $data.PreReleaseLabel
$preNumber = [int]$data.PreReleaseNumber
$sha = $data.Sha

if ([string]::IsNullOrEmpty($preLabel) -or $preLabel -eq 'null') {
    throw "Commit $sha is not a prerelease version. Use this script only for prerelease tags."
}

# Format with zero-padded prerelease number for proper GitHub sorting
$paddedVersion = '{0}-{1}.{2:0000}' -f $majorMinorPatch, $preLabel, $preNumber
$tagName = "v$paddedVersion"

Write-Host ""
Write-Host "Creating prerelease tag:" -ForegroundColor Cyan
Write-Host "  Commit:  $sha"
Write-Host "  Tag:     $tagName"
Write-Host "  Version: $paddedVersion"
Write-Host ""

# Check if tag already exists
$existingTag = git -C $repoRoot rev-parse --verify --quiet "refs/tags/$tagName" 2>$null
if ($existingTag) {
    # we dont abort because we want to allow to separate creation and pushing of tags
    Write-Host "Tag $tagName already exists." -ForegroundColor Yellow
} else{
    # Create annotated tag
    $message = "Prerelease $paddedVersion"
    git -C $repoRoot tag -a $tagName -m $message $sha
    
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create tag"
    }
    
    Write-Host "Tag created successfully!" -ForegroundColor Green

}


if ($Push) {
    Write-Host ""
    Write-Host "Pushing tag to https://github.com/litedb-org/LiteDB..." -ForegroundColor Cyan
    git -C $repoRoot push https://github.com/litedb-org/LiteDB $tagName
    
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push tag"
    }
    
    Write-Host "Tag pushed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "You can now create a GitHub release at:" -ForegroundColor Yellow
    Write-Host "  https://github.com/litedb-org/LiteDB/releases/new?tag=$tagName"
}
else {
    Write-Host ""
    Write-Host "Tag created locally. To push it, run:" -ForegroundColor Yellow
    Write-Host "  git push https://github.com/litedb-org/LiteDB $tagName"
    Write-Host ""
    Write-Host "Or re-run this script with -Push:"
    Write-Host "  ./scripts/gitver/tag-prerelease.ps1 -Push"
}