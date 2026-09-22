$ErrorActionPreference = 'Stop'
$analysis = Join-Path $PWD 'analysis'
New-Item -ItemType Directory -Force $analysis | Out-Null
Start-Transcript -Path (Join-Path $analysis 'managed-transcript.txt')
try {
    $dumps = @(Get-ChildItem retained -Recurse -Filter 'dump-6408-947ce305222e443f8c48818794e62065.dmp')
    if ($dumps.Count -ne 1) { throw 'Expected exactly one retained target dump.' }
    $dump = $dumps[0]
    $hash = (Get-FileHash $dump.FullName -Algorithm SHA256).Hash
    if ($hash -ne 'FFA5FCD8CFB3D55BF8BB5FF5629B3F70EED081B43734BF352BF15EBD7CAC0C92') { throw 'Retained dump hash mismatch.' }
    if ($dump.Length -ne 151340805) { throw 'Retained dump size mismatch.' }
    Write-Host "Verified dump PID=6408; bytes=$($dump.Length); SHA256=$hash; path=$($dump.FullName)"
    $runtimes = @(Get-ChildItem retained -Recurse -Filter mscordaccore.dll)
    $binaries = @(Get-ChildItem retained -Recurse -Filter LiteDB.dll)
    if ($runtimes.Count -ne 1 -or $binaries.Count -ne 1) { throw 'Runtime/binary artifact identity is ambiguous.' }
    $runtime = $runtimes[0].DirectoryName
    $binary = $binaries[0].DirectoryName
    @($dump.FullName, $runtime, $binary) | Set-Content (Join-Path $analysis 'paths.txt')
    Get-ChildItem $runtime -File | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName; Version=$_.VersionInfo.FileVersion; SHA256=(Get-FileHash $_.FullName).Hash }
    } | ConvertTo-Json | Set-Content (Join-Path $analysis 'runtime-manifest.json')
    Get-ChildItem $binary -File | Where-Object Name -match '^LiteDB.*\.(dll|pdb)$' | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName; Version=$_.VersionInfo.FileVersion; SHA256=(Get-FileHash $_.FullName).Hash }
    } | ConvertTo-Json | Set-Content (Join-Path $analysis 'binary-manifest.json')

    # Official x86 link documented at https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-dump
    # Pin its resolved Microsoft-hosted build instead of taking a future moving release.
    $url = 'https://download.visualstudio.microsoft.com/download/pr/dotnet-diagnostics_20260904.1/ED254523DC4622A57559A35A63AAF244959B6A3A638DE5D770C93C3959F9F2B0/dotnet-dump.exe'
    $tool = Join-Path $env:RUNNER_TEMP 'dotnet-dump-x86.exe'
    Invoke-WebRequest $url -OutFile $tool
    $signature = Get-AuthenticodeSignature $tool
    $signature | Format-List Status, StatusMessage, SignerCertificate
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Unverified diagnostic tool signature.' }
    $bytes = [IO.File]::ReadAllBytes($tool)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    if ($machine -ne 0x14c) { throw "Diagnostic tool must be x86; PE machine=$machine" }
    Write-Host "Diagnostic tool source=$url; machine=0x14c; SHA256=$((Get-FileHash $tool).Hash)"
    $installScript = Join-Path $env:RUNNER_TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
    $toolRuntime = Join-Path $env:RUNNER_TEMP 'analysis-dotnet-x86'
    & $installScript -Runtime dotnet -Version '8.0.31' -Architecture x86 -InstallDir $toolRuntime -NoPath
    $env:DOTNET_ROOT_X86 = $toolRuntime
    $env:DOTNET_ROOT = $toolRuntime
    & (Join-Path $toolRuntime 'dotnet.exe') --info
    & $tool --version
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic tool did not start.' }
    $commands = @('setsymbolserver -ms', "setsymbolserver -directory $binary", "setclrpath $runtime", 'eeversion', 'runtimes', 'clrmodules', 'clrthreads', 'clrstack -all', 'syncblk -all', 'threadpool', 'parallelstacks', 'exit')
    $arguments = @('analyze', $dump.FullName)
    foreach ($command in $commands) { $arguments += @('-c', $command) }
    & $tool @arguments 2>&1 | Tee-Object -FilePath (Join-Path $analysis 'managed-stacks.txt')
    if ($LASTEXITCODE -ne 0) { throw "dotnet-dump failed: $LASTEXITCODE" }
}
finally { Stop-Transcript }
