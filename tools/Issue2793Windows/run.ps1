param([string]$OutputDirectory = "$PSScriptRoot/results")
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'A real Windows host is required.' }
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$caseRoot = Join-Path $env:PUBLIC ('LiteDB2793-' + [guid]::NewGuid().ToString('N'))
$userName = 'litedb' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$password = ConvertTo-SecureString ('Aa1!' + [guid]::NewGuid().ToString('N')) -AsPlainText -Force
$credential = [pscredential]::new("$env:COMPUTERNAME\$userName", $password)
$owner = $null
$peer = $null
$taskName = 'LiteDB2793-' + [guid]::NewGuid().ToString('N')
function Wait-File([string]$Path) {
    $until = [datetime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ([datetime]::UtcNow -gt $until) { throw "Timed out waiting for $Path" }
        Start-Sleep -Milliseconds 50
    }
}
try {
    New-Item -ItemType Directory -Path $caseRoot, $OutputDirectory -Force | Out-Null
    $user = New-LocalUser -Name $userName -Password $password -AccountNeverExpires
    & icacls $caseRoot /grant "*$($user.SID.Value):(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the test account access to its isolated directory.' }
    foreach ($source in @($false, $true)) {
        $variant = if ($source) { 'dev' } else { '5.0.19' }
        $binary = Join-Path $caseRoot "$variant-bin"
        & dotnet publish "$PSScriptRoot/Issue2793Windows.csproj" -c Release -r win-x64 --self-contained true -p:TestingEnabled=false "-p:UseProjectReference=$source" -o $binary
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $variant" }
        $exe = Join-Path $binary 'Issue2793Windows.exe'
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $directory = Join-Path $caseRoot "$variant-$attempt"
            New-Item -ItemType Directory -Path $directory | Out-Null
            # A real LocalSystem service-account logon must share with the local user.
            # Separate user names in one desktop logon SID can miss this defect.
            $action = New-ScheduledTaskAction -Execute $exe -Argument "owner $directory" -WorkingDirectory $directory
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
            Start-ScheduledTask -TaskName $taskName
            Wait-File "$directory/owner-ready"
            if ((Get-Content "$directory/owner-ready" -Raw) -ne 'S-1-5-18') { throw 'Owner must really run as LocalSystem.' }
            $peer = Start-Process -FilePath $exe -ArgumentList @('peer', $directory) -Credential $credential -WorkingDirectory $directory -PassThru
            Wait-File "$directory/peer-ready"
            Start-Sleep -Milliseconds 750
            $finishedWhileHeld = Test-Path "$directory/peer-result.json"
            Set-Content "$directory/release-owner" 'release'
            Wait-File "$directory/peer-result.json"
            $until = [datetime]::UtcNow.AddSeconds(45)
            while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
                if ([datetime]::UtcNow -gt $until) { throw 'Scheduled peer did not exit.' }
                Start-Sleep -Milliseconds 50
            }
            $ownerExit = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
            if (-not $peer.WaitForExit(45000)) { throw 'Peer did not exit.' }
            $peerExit = $peer.ExitCode
            $result = Get-Content "$directory/peer-result.json" -Raw | ConvertFrom-Json
            if ($ownerExit -ne 0 -or -not (Test-Path "$directory/owner-verified")) { throw 'Independent reopened ledger failed.' }
            if ($result.outcome -eq 'passed' -and $finishedWhileHeld) { throw 'Shared mode bypassed the held mutex.' }
            if ($result.outcome -eq 'passed' -and $peerExit -ne 10) { throw 'Unexpected peer pass exit code.' }
            if ($result.outcome -eq 'reported-failure' -and $peerExit -ne 0) { throw 'Unexpected peer failure exit code.' }
            if ($result.outcome -notin @('passed', 'reported-failure')) { throw 'Unknown peer outcome.' }
            if ($result.outcome -eq 'reported-failure' -and
                ($result.error -notmatch 'Global\\' -or $result.error -notmatch '\.Mutex')) {
                throw 'Access denial did not identify the reported global mutex.'
            }
            if (-not $source -and $result.outcome -ne 'reported-failure') { throw 'Pinned historical package did not reproduce.' }
            if ($source -and $result.outcome -ne 'passed') { throw 'Current source regressed cross-account sharing.' }
            $result | Add-Member -NotePropertyName finishedWhileHeld -NotePropertyValue $finishedWhileHeld
            $result | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutputDirectory "$variant-$attempt.json")
            Write-Output "$variant attempt=$attempt outcome=$($result.outcome) finishedWhileHeld=$finishedWhileHeld"
        }
    }
}
finally {
    foreach ($process in @($owner, $peer)) {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Get-ScheduledTaskInfo -TaskName $taskName | Select-Object LastTaskResult, LastRunTime, TaskName | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'task-info.json')
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    if (Test-Path $caseRoot) {
        Copy-Item "$caseRoot/*" $OutputDirectory -Recurse -Force -Exclude '*-bin'
        Remove-Item $caseRoot -Recurse -Force
    }
    if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $userName }
}
