$ErrorActionPreference = 'Stop'
$analysis = Join-Path $PWD 'analysis'
New-Item -ItemType Directory -Force $analysis | Out-Null
Start-Transcript -Path (Join-Path $analysis 'native-transcript.txt')
try {
    $paths = Get-Content (Join-Path $analysis 'paths.txt')
    $dump = $paths[0]
    $runtime = $paths[1]
    $binary = $paths[2]
    $cdb = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Debuggers\x86\cdb.exe'
    if (!(Test-Path $cdb)) {
        # Official SDK 10.0.26100.9169 installer from https://learn.microsoft.com/windows/apps/windows-sdk/downloads
        $url = 'https://go.microsoft.com/fwlink/?linkid=2376216'
        $installer = Join-Path $env:RUNNER_TEMP 'sdksetup.exe'
        Invoke-WebRequest $url -OutFile $installer
        $signature = Get-AuthenticodeSignature $installer
        $signature | Format-List Status, SignerCertificate
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Unverified SDK installer.' }
        Write-Host "SDK installer source=$url; SHA256=$((Get-FileHash $installer).Hash)"
        $install = Start-Process $installer -ArgumentList '/features OptionId.WindowsDesktopDebuggers /quiet /norestart' -PassThru -Wait
        Write-Host "SDK installer exit code: $($install.ExitCode)"
        if ($install.ExitCode -notin @(0,3010)) { throw 'SDK debugger install failed.' }
    }
    if (!(Test-Path $cdb)) { throw 'x86 CDB unavailable.' }
    (Get-Item $cdb).VersionInfo | Format-List FileVersion, ProductVersion
    Get-AuthenticodeSignature $cdb | Format-List Status, SignerCertificate
    Write-Host "CDB SHA256=$((Get-FileHash $cdb).Hash)"
    $cache = Join-Path $env:RUNNER_TEMP 'debug-symbols'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $symbolPath = "srv*$cache*https://msdl.microsoft.com/download/symbols;$binary"
    $commands = "vertarget;lm;lmv m coreclr;lmv m LiteDB;.reload /f ntdll.dll;.reload /f kernelbase.dll;.reload /f kernel32.dll;.reload /f coreclr.dll;~* kp;!handle 0 f Mutant;!handle 0 f File;q"
    & $cdb -z $dump -y $symbolPath -i "$runtime;$binary" -c $commands 2>&1 | Tee-Object -FilePath (Join-Path $analysis 'native-stacks.txt')
    Write-Host "CDB exit code: $LASTEXITCODE"
}
finally { Stop-Transcript }
