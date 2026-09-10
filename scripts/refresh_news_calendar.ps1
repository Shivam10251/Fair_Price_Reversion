<#
.SYNOPSIS
    Refreshes the FPMR news calendar file from ForexFactory's weekly feed.

.DESCRIPTION
    Downloads the current week's economic calendar and writes it to the file the
    strategy reads. Intended to run unattended on a VPS from Task Scheduler.

    WHAT THIS DOES AND DOES NOT GIVE YOU
    ------------------------------------
    The ForexFactory weekly feed carries the SCHEDULE and the FORECAST only. It has
    no "actual" field at all - verified against the live feed, both the JSON and the
    CSV form. So this script keeps the strategy's schedule current; it cannot supply
    the released figure that the reversion-vs-continuation branch turns on.

    Actuals come from one of:
      * NinjaTrader's live economic calendar push, in-process, while the strategy
        runs (NewsActualSource = Nt8CalendarThenFile); or
      * an external fetcher writing an "Actual" column into this same file, which
        this script PRESERVES across refreshes - see -KeepActuals.

    The strategy re-reads the file automatically when it changes on disk, between
    sessions, so a refresh never disturbs a live trade.

.PARAMETER OutFile
    Destination CSV. Defaults to the repo's config\ff_calendar_thisweek.csv.

.PARAMETER KeepActuals
    Merge any existing "Actual" values forward into the newly downloaded file,
    matched on Title + Country + Date + Time. On by default: a refresh must never
    silently wipe figures a fetcher has already written.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File refresh_news_calendar.ps1

.EXAMPLE
    # Weekly on the VPS, Sunday 06:00 local
    schtasks /create /tn "FPMR news calendar" /sc weekly /d SUN /st 06:00 ^
      /tr "powershell -ExecutionPolicy Bypass -File C:\path\to\refresh_news_calendar.ps1"
#>
[CmdletBinding()]
param(
    [string] $OutFile = (Join-Path (Split-Path -Parent $PSScriptRoot) "config\ff_calendar_thisweek.csv"),
    [switch] $KeepActuals = $true,
    [string] $Url = "https://nfs.faireconomy.media/ff_calendar_thisweek.csv"
)

$ErrorActionPreference = "Stop"

function Log([string] $m) {
    Write-Output ((Get-Date -Format "yyyy-MM-dd HH:mm:ss") + "  " + $m)
}

Log "Refreshing news calendar"
Log ("  source : " + $Url)
Log ("  target : " + $OutFile)

# ── Download to a temp file first, so a failed or truncated fetch never replaces
#    a good calendar with a broken one. ──
$tmp = [System.IO.Path]::GetTempFileName()

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec 60 `
        -UserAgent "Mozilla/5.0 (FPMR calendar refresher)"
}
catch {
    Log ("FAILED to download: " + $_.Exception.Message)
    Log "The existing calendar file has been left untouched."
    if (Test-Path $tmp) { Remove-Item $tmp -Force }
    exit 1
}

$fresh = @(Import-Csv $tmp)

if ($fresh.Count -eq 0) {
    Log "FAILED: downloaded file parsed to zero rows. Existing calendar left untouched."
    Remove-Item $tmp -Force
    exit 1
}

Log ("  downloaded " + $fresh.Count + " events")

# ── Carry existing actuals forward ──
$carried = 0

if ($KeepActuals -and (Test-Path $OutFile)) {
    $old = @(Import-Csv $OutFile)
    $hasActual = $old.Count -gt 0 -and ($old[0].PSObject.Properties.Name -contains "Actual")

    if ($hasActual) {
        $lookup = @{}
        foreach ($r in $old) {
            if (-not [string]::IsNullOrWhiteSpace($r.Actual)) {
                $lookup[($r.Title + "|" + $r.Country + "|" + $r.Date + "|" + $r.Time)] = $r.Actual
            }
        }
        Log ("  existing file carries " + $lookup.Count + " actual value(s)")

        $fresh = $fresh | ForEach-Object {
            $key = ($_.Title + "|" + $_.Country + "|" + $_.Date + "|" + $_.Time)
            $val = if ($lookup.ContainsKey($key)) { $carried++; $lookup[$key] } else { "" }
            $_ | Add-Member -NotePropertyName Actual -NotePropertyValue $val -PassThru
        }
    }
    else {
        Log "  existing file has no Actual column - nothing to carry forward"
        $fresh = $fresh | ForEach-Object { $_ | Add-Member -NotePropertyName Actual -NotePropertyValue "" -PassThru }
    }
}
else {
    $fresh = $fresh | ForEach-Object { $_ | Add-Member -NotePropertyName Actual -NotePropertyValue "" -PassThru }
}

# ── Write atomically: full file to a sibling temp, then move over the target. The
#    strategy watches this path and must never observe a half-written file. ──
$stage = $OutFile + ".new"
$fresh | Export-Csv -Path $stage -NoTypeInformation -Encoding UTF8
Move-Item -Path $stage -Destination $OutFile -Force
Remove-Item $tmp -Force

$usdHigh = @($fresh | Where-Object { $_.Country -eq "USD" -and $_.Impact -eq "High" })

Log ("  wrote " + $fresh.Count + " events (" + $carried + " actual value(s) carried forward)")
Log ("  USD High-impact this week: " + $usdHigh.Count)
foreach ($e in $usdHigh) {
    Log ("      " + $e.Date + " " + $e.Time + "  " + $e.Title +
         "  forecast=" + $(if ($e.Forecast) { $e.Forecast } else { "-" }) +
         "  actual="   + $(if ($e.Actual)   { $e.Actual }   else { "-" }))
}
Log "Done. The strategy will pick this up automatically between sessions."
