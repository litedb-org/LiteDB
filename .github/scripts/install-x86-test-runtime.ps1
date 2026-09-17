param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^net[0-9]+\.[0-9]+$')]
    [string] $Framework
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The x86 test runtime is only supported on Windows.' }
if (-not $env:RUNNER_TEMP -or -not $env:GITHUB_ENV) { throw 'GitHub runner paths are required.' }

$channel = $Framework.Substring(3)
$installer = Join-Path $env:RUNNER_TEMP 'dotnet-install-x86.ps1'
$runtimeRoot = Join-Path $env:RUNNER_TEMP 'dotnet-x86'
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Runtime dotnet -Channel $channel -Architecture x86 -InstallDir $runtimeRoot -NoPath
if ($LASTEXITCODE -ne 0) { throw "x86 runtime installation failed: $LASTEXITCODE" }

# Keep the x64 SDK on PATH. Only testhost's x86 apphost should use this runtime.
"DOTNET_ROOT_X86=$runtimeRoot" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
& (Join-Path $runtimeRoot 'dotnet.exe') --list-runtimes
if ($LASTEXITCODE -ne 0) { throw 'The installed x86 runtime cannot run.' }
