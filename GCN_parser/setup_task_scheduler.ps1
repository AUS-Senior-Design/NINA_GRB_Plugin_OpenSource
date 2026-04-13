# setup_task_scheduler.ps1
#
# Run this ONCE as Administrator to register all three scheduled tasks:
#   1. grb-nina-launcher    — runs nina_launcher.py at startup (sleeps until sunset-5min)
#   2. grb-nina-launcher-noon — safety restart at noon in case PC rebooted after sunrise
#   3. grb-image-analyzer   — runs image_analyzer.py at startup (24/7 loop)
#
# Usage (run as Administrator):
#   powershell -ExecutionPolicy Bypass -File setup_task_scheduler.ps1

$PythonW      = "pythonw.exe"   # silent Python — no console window
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$LauncherScript  = Join-Path $ScriptDir "nina_launcher.py"
$AnalyzerScript  = Join-Path $ScriptDir "image_analyzer.py"

# ── Verify files exist ────────────────────────────────────────────────────────
foreach ($f in @($LauncherScript, $AnalyzerScript)) {
    if (-not (Test-Path $f)) {
        Write-Error "File not found: $f"
        exit 1
    }
}

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Host "Registering tasks for user: $currentUser"
Write-Host "Script directory: $ScriptDir"

# ── Helper ────────────────────────────────────────────────────────────────────
function Register-GRBTask {
    param(
        [string]$TaskName,
        [string]$Description,
        [string]$ScriptPath,
        $Trigger
    )

    $action = New-ScheduledTaskAction `
        -Execute $PythonW `
        -Argument "`"$ScriptPath`"" `
        -WorkingDirectory $ScriptDir

    $settings = New-ScheduledTaskSettingsSet `
        -ExecutionTimeLimit (New-TimeSpan -Hours 0) `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -StartWhenAvailable

    $principal = New-ScheduledTaskPrincipal `
        -UserId $currentUser `
        -LogonType Interactive `
        -RunLevel Highest

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description $Description `
        -Action $action `
        -Trigger $Trigger `
        -Settings $settings `
        -Principal $principal `
        -Force | Out-Null

    Write-Host "  [OK] Task registered: $TaskName"
}

# ── Task 1: nina_launcher.py — at startup ─────────────────────────────────────
# Starts when Windows boots, sleeps internally until sunset-5min, then opens NINA
Register-GRBTask `
    -TaskName    "grb-nina-launcher" `
    -Description "Launches NINA 5 minutes before sunset for GRB monitoring. Sleeps until the right time." `
    -ScriptPath  $LauncherScript `
    -Trigger     (New-ScheduledTaskTrigger -AtStartup)

# ── Task 2: nina_launcher.py — noon safety restart ────────────────────────────
# If the PC was already running when startup task fired but has since been
# restarted after sunrise, this noon trigger ensures the launcher runs again
Register-GRBTask `
    -TaskName    "grb-nina-launcher-noon" `
    -Description "Safety: re-runs nina_launcher.py at noon so sunset launch is never missed." `
    -ScriptPath  $LauncherScript `
    -Trigger     (New-ScheduledTaskTrigger -Daily -At "12:00")

# ── Task 3: image_analyzer.py — at startup ────────────────────────────────────
# Polls Firestore grb_captures every 30s, runs photometry on new FITS files
Register-GRBTask `
    -TaskName    "grb-image-analyzer" `
    -Description "GRB image analyzer — watches Firestore and processes FITS files 24/7." `
    -ScriptPath  $AnalyzerScript `
    -Trigger     (New-ScheduledTaskTrigger -AtStartup)

Write-Host ""
Write-Host "All tasks registered. To verify:"
Write-Host "  Get-ScheduledTask | Where-Object { `$_.TaskName -like 'grb-*' }"
Write-Host ""
Write-Host "To start them now without rebooting:"
Write-Host "  Start-ScheduledTask -TaskName 'grb-nina-launcher'"
Write-Host "  Start-ScheduledTask -TaskName 'grb-image-analyzer'"
