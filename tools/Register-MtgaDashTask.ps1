<#
.SYNOPSIS
    Registers a Task Scheduler task that starts MtgaDash hidden at logon.

.DESCRIPTION
    The task runs as the current user with logon type S4U ("run whether the user is logged on or not",
    without storing a password), so no console window appears. It restarts the app up to three times if it
    exits with an error and never stops it for running too long. Re-running the script replaces the task.

    Run from an elevated PowerShell: S4U tasks need administrator rights to register.

    With -Interactive the task runs in your desktop session instead (only while you are logged on), so a
    game started by the dashboard's LaunchGame setting is visible. The dashboard's own console window is
    kept hidden by starting it through a hidden PowerShell window.

.EXAMPLE
    .\Register-MtgaDashTask.ps1 -ExePath "C:\dev\mtga\app\MtgaDash.exe" -TaskName "MtgaDash" -Port 5080

.EXAMPLE
    .\Register-MtgaDashTask.ps1 -ExePath "C:\dev\mtga\app\MtgaDash.exe" -TaskName "MtgaDash" -Port 5080 -Interactive

.EXAMPLE
    .\Register-MtgaDashTask.ps1 -Unregister -TaskName "MtgaDash"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The published executable (dotnet publish ... -o C:\dev\mtga\app).
    [string]$ExePath = "C:\dev\mtga\app\MtgaDash.exe",

    # The task's name in Task Scheduler.
    [string]$TaskName = "MtgaDash",

    # Overrides MtgaDash:Port in appsettings.json.
    [int]$Port = 5080,

    # The account whose logon starts the task.
    [string]$UserId = "$env:USERDOMAIN\$env:USERNAME",

    # Seconds to wait after logon, so the desktop settles first.
    [int]$DelaySeconds = 30,

    # Run in your desktop session, so the game started by LaunchGame is visible. Default: hidden (S4U).
    [switch]$Interactive,

    # Remove the task instead of registering it.
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed task '$TaskName'."
    } else {
        Write-Host "No task named '$TaskName'."
    }
    return
}

$exe = Resolve-Path -LiteralPath $ExePath -ErrorAction SilentlyContinue
if (-not $exe) {
    throw "MtgaDash.exe not found at '$ExePath'. Publish first: dotnet publish src/MtgaDash -c Release -r win-x64 --self-contained false -o C:\dev\mtga\app"
}
$exe = $exe.Path
$folder = Split-Path -Parent $exe

# The app reads its settings from its own folder, but give it a sensible working directory too.
if ($Interactive) {
    # A console app started by an interactive task opens a window. Starting it from a hidden PowerShell keeps
    # that window hidden, and passing its exit code back keeps the task's restart-on-failure working.
    $command = "& '$($exe.Replace("'", "''"))' --MtgaDash:Port=$Port; exit `$LASTEXITCODE"
    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"$command`"" `
        -WorkingDirectory $folder
} else {
    $action = New-ScheduledTaskAction -Execute $exe -Argument "--MtgaDash:Port=$Port" -WorkingDirectory $folder
}
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserId
$trigger.Delay = "PT${DelaySeconds}S"
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
# S4U runs without a visible window and without storing a password, but has no desktop, so a game it starts
# would be invisible. Interactive runs in your logged-on desktop session.
$logonType = if ($Interactive) { "Interactive" } else { "S4U" }
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType $logonType -RunLevel Limited

if ($PSCmdlet.ShouldProcess($TaskName, "Register logon task for $exe")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
        -Description "MtgaDash dashboard on http://localhost:$Port" -Force | Out-Null
    Write-Host "Registered task '$TaskName': $exe on port $Port, at logon of $UserId (+${DelaySeconds}s), logon type $logonType."
    Write-Host "Start it now:  Start-ScheduledTask -TaskName '$TaskName'"
    Write-Host "Then open:     http://localhost:$Port"
}
