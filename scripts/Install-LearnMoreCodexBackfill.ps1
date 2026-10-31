[CmdletBinding()]
param(
    [ValidateSet('Probe','Enable','Status')][string]$Mode = 'Probe',
    [string]$InstallRoot = 'D:\Data\LearnMoreCodexBackfill',
    [string]$AppSettingsPath = 'D:\Web\LearnMore\appsettings.json',
    [string]$SqlDataSource = '',
    [string]$PythonPath = '',
    [string]$CodexPath = '',
    [ValidateRange(1,30)][int]$IntervalMinutes = 2
)
$ErrorActionPreference = 'Stop'
$taskName = 'LearnMore-CodexBackfill'
$probeName = $taskName + '-Probe'
$bin = Join-Path $InstallRoot 'bin'
$state = Join-Path $InstallRoot 'state'
$worker = Join-Path $bin 'Invoke-LearnMoreCodexBackfill.ps1'
$helper = Join-Path $bin 'learnmore_codex_backfill.py'
foreach ($value in @($InstallRoot,$AppSettingsPath,$SqlDataSource,$PythonPath,$CodexPath)) {
    if ($value -match '["\r\n]') { throw 'Invalid installation argument' }
}
if ($Mode -eq 'Status') {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $probe = Get-ScheduledTask -TaskName $probeName -ErrorAction SilentlyContinue
    $receipt = Test-Path (Join-Path $state 'probe\receipt.json')
    [pscustomobject]@{ registered=[bool]$task; taskState=[string]$task.State; probeState=[string]$probe.State; probeReceipt=$receipt } | ConvertTo-Json -Compress
    exit 0
}
if (-not $PythonPath) { $PythonPath = (Get-Command python.exe -ErrorAction Stop).Source }
if (-not $CodexPath) { $CodexPath = (Get-Command codex.cmd -ErrorAction Stop).Source }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType S4U -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 25) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
foreach ($name in @($taskName,$probeName)) {
    $existingTask = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($existingTask -and @($existingTask.Actions | Where-Object { $_.Arguments -notlike ('*' + $worker + '*') }).Count -gt 0) {
        throw 'Task name already belongs to another worker'
    }
}
if ($Mode -eq 'Probe') {
    $null = New-Item -ItemType Directory -Path $bin -Force
    $null = New-Item -ItemType Directory -Path $state -Force
    & icacls.exe $InstallRoot /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' >$null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect worker directory' }
    foreach ($name in @('Invoke-LearnMoreCodexBackfill.ps1','learnmore_codex_backfill.py')) {
        $source = Join-Path $PSScriptRoot $name
        $target = Join-Path $bin $name
        if ([IO.Path]::GetFullPath($source) -cne [IO.Path]::GetFullPath($target)) { Copy-Item -LiteralPath $source -Destination $target -Force }
    }
    $oldProof = Join-Path $state 'probe\receipt.json'
    if (Test-Path -LiteralPath $oldProof) { Remove-Item -LiteralPath $oldProof }
}
if (-not (Test-Path $worker) -or -not (Test-Path $helper)) { throw 'Worker files missing' }
$arguments = '-NoProfile -NonInteractive -File "' + $worker + '" -AppSettingsPath "' + $AppSettingsPath +
    '" -StateDirectory "' + $state + '" -PythonPath "' + $PythonPath + '" -CodexPath "' + $CodexPath + '"'
if ($SqlDataSource) { $arguments += ' -SqlDataSource "' + $SqlDataSource + '"' }
$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($Mode -eq 'Probe') {
    $action = New-ScheduledTaskAction -Execute $powerShell -Argument ($arguments + ' -SelfTest') -WorkingDirectory $bin
    $null = Register-ScheduledTask -TaskName $probeName -Action $action -Settings $settings -Principal $principal -Force
    Start-ScheduledTask -TaskName $probeName
    [pscustomobject]@{ phase='probe_started'; databaseWrites=$false } | ConvertTo-Json -Compress
    exit 0
}
$proofPath = Join-Path $state 'probe\receipt.json'
if (-not (Test-Path $proofPath)) { throw 'Scheduled Codex probe has not passed' }
$proof = Get-Content -LiteralPath $proofPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $proof.ok -or $proof.scriptHash -cne (Get-FileHash $worker -Algorithm SHA256).Hash -or
    $proof.helperHash -cne (Get-FileHash $helper -Algorithm SHA256).Hash) { throw 'Worker changed since scheduled probe' }
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and @($existing.Actions | Where-Object { $_.Arguments -notlike ('*' + $worker + '*') }).Count -gt 0) {
    throw 'Task name already belongs to another worker'
}
$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments -WorkingDirectory $bin
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes($IntervalMinutes) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$null = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force
Start-ScheduledTask -TaskName $taskName
[pscustomobject]@{ enabled=$true; intervalMinutes=$IntervalMinutes; maxSongsPerRun=1 } | ConvertTo-Json -Compress
