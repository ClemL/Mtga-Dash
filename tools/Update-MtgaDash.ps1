<#
.SYNOPSIS
    Updates a published MtgaDash: stops it, pulls the latest code, republishes, and starts it again.

.DESCRIPTION
    1. Stops the logon task (if registered) and waits for MtgaDash.exe to exit, so publishing can replace it.
    2. Pulls the branch in the repository (fast-forward only: local commits or edits stop the update).
    3. Optionally runs the tests.
    4. Publishes to the app folder. Your settings in appsettings.Production.json, the database and
       account-exceptions.json are not touched: they are not part of the publish output.
    5. Starts the task again, or tells you how to start the app if no task is registered.

    Run from a normal PowerShell window; only registering the task needs administrator rights.

.EXAMPLE
    .\Update-MtgaDash.ps1

.EXAMPLE
    .\Update-MtgaDash.ps1 -RepoPath "C:\dev\mtga\MtgaDash" -AppPath "C:\dev\mtga\app" -TaskName "MtgaDash" -Branch "main" -RunTests
#>
[CmdletBinding()]
param(
    # The git clone of the repository.
    [string]$RepoPath = "C:\dev\mtga\MtgaDash",

    # Where the app is published (the folder holding MtgaDash.exe).
    [string]$AppPath = "C:\dev\mtga\app",

    # The Task Scheduler task registered by Register-MtgaDashTask.ps1.
    [string]$TaskName = "MtgaDash",

    # The branch to update to.
    [string]$Branch = "main",

    # The runtime identifier for dotnet publish.
    [string]$Runtime = "win-x64",

    # Seconds to wait for the running app to exit before stopping it.
    [int]$StopTimeoutSeconds = 30,

    # Run the test suite before publishing; a failure stops the update and leaves the old version in place.
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    Write-Host "==> $What"
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

foreach ($tool in "git", "dotnet") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not on the PATH." }
}
if (-not (Test-Path -LiteralPath (Join-Path $RepoPath "MtgaDash.sln"))) { throw "No MtgaDash.sln in '$RepoPath'. Set -RepoPath to your clone." }

# Refuse to update over local edits: a pull would fail halfway or mix them in.
$dirty = git -C $RepoPath status --porcelain
if ($dirty) { throw "The repository at '$RepoPath' has uncommitted changes. Commit or discard them first:`n$dirty" }

# 1. Stop the running app, so its files can be replaced.
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Write-Host "==> Stopping task '$TaskName'"
    Stop-ScheduledTask -TaskName $TaskName
}
$exe = Join-Path $AppPath "MtgaDash.exe"
$deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
do {
    $running = Get-Process -Name "MtgaDash" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if (-not $running) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
if ($running) {
    Write-Host "==> MtgaDash did not exit within $StopTimeoutSeconds s; stopping it"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# 2. Update the code.
Invoke-Checked "Fetching" { git -C $RepoPath fetch origin $Branch }
Invoke-Checked "Switching to $Branch" { git -C $RepoPath checkout $Branch }
Invoke-Checked "Pulling" { git -C $RepoPath pull --ff-only origin $Branch }

# 3. Optionally test.
if ($RunTests) {
    Invoke-Checked "Testing" { dotnet test (Join-Path $RepoPath "MtgaDash.sln") }
}

# 4. Publish.
Invoke-Checked "Publishing to $AppPath" {
    dotnet publish (Join-Path $RepoPath "src\MtgaDash") -c Release -r $Runtime --self-contained false -o $AppPath
}

# 5. Start it again.
if ($task) {
    Write-Host "==> Starting task '$TaskName'"
    Start-ScheduledTask -TaskName $TaskName
} else {
    Write-Host "No task named '$TaskName'. Start the app with: & '$exe'"
}

$updates = Join-Path $AppPath "wwwroot\updates.txt"
if (Test-Path -LiteralPath $updates) {
    $latest = Get-Content -LiteralPath $updates | Where-Object { $_.Trim() } | Select-Object -Last 1
    Write-Host "Latest change: $latest"
}
Write-Host "Updated. Dashboard: http://localhost:5080/#/activity"
