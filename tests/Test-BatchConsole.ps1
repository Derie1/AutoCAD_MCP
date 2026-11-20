<#
  Batch mode check: the installed plugin inside AcCoreConsole (no AutoCAD window needed).

    .\tests\Test-BatchConsole.ps1                 # AutoCAD 2026 and 2027 (those installed)
    .\tests\Test-BatchConsole.ps1 -Years 2027

  Works on copies of AutoCAD's sample "Architectural - Metric.dwg" in %TEMP%\acadmcp_batch_test (removed
  at the end). Checks: read-only job, change + copy (same DWG version), change in place (.bak, folder with a
  space), a tool refused in batches, reopening the copy, four consoles in parallel.
#>
param([string[]]$Years = @('2026', '2027'))
$ErrorActionPreference = 'Stop'

$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AcadMcpBridge.bundle\Contents'
$work = Join-Path $env:TEMP 'acadmcp_batch_test'
$script:passed = 0; $script:failed = 0

function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:passed++ } else { $script:failed++; Write-Output "    FAIL: $what" }
}

function Header([string]$file) { [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($file)[0..5]) }

# Starts one console for a job; returns the process (call .WaitForExit()).
function Start-Batch([string]$year, [string]$dwg, [hashtable]$job, [string]$name) {
    $job.result = Join-Path $work "$name.result.json"
    $jobFile = Join-Path $work "$name.json"
    $job | ConvertTo-Json -Depth 10 | Set-Content $jobFile -Encoding utf8
    $env:ACADMCP_BATCH_JOB = $jobFile
    $exe = "C:\Program Files\Autodesk\AutoCAD $year\accoreconsole.exe"
    # /isolate: own profile, as batch_run does (otherwise the console rewrites the user's LogFilePath).
    $profile = Join-Path $env:LOCALAPPDATA "AcadMcpBridge\console\$year"
    New-Item -ItemType Directory -Force $profile | Out-Null
    Start-Process $exe -ArgumentList "/i `"$dwg`" /s `"$work\batch_$year.scr`" /l en-US /isolate acadmcp_batch `"$profile`"" -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work "$name.out.txt")
}

function Run-Batch([string]$year, [string]$dwg, [hashtable]$job, [string]$name) {
    $p = Start-Batch $year $dwg $job $name
    if (-not $p.WaitForExit(120000)) { $p.Kill(); throw "console for $name did not finish in 120 s" }
    $result = Join-Path $work "$name.result.json"
    if (-not (Test-Path $result)) { Check $false "$name wrote no result (exit $($p.ExitCode))"; return $null }
    Get-Content $result -Raw -Encoding utf8 | ConvertFrom-Json
}

function Get-LogPaths {
    Get-ChildItem 'HKCU:\Software\Autodesk\AutoCAD' -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $v = $_.GetValue('LogFilePath'); if ($null -ne $v) { "$($_.Name)=$v" } }
}
$logsBefore = (Get-LogPaths) -join "`n"

foreach ($year in $Years) {
    $exe = "C:\Program Files\Autodesk\AutoCAD $year\accoreconsole.exe"
    $sample = "C:\Program Files\Autodesk\AutoCAD $year\Sample\en-us\Dynamic Blocks\Architectural - Metric.dwg"
    if (-not (Test-Path $exe) -or -not (Test-Path $sample)) { Write-Output "== $year SKIPPED (console or sample missing)"; continue }
    $series = if ($year -eq '2027') { 'R26' } else { 'R25' }
    $dll = Join-Path $bundle "$series\AcadMcpBridge.dll"
    if (-not (Test-Path $dll)) { throw "plugin not installed: $dll" }

    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $work 'with space') | Out-Null
    [IO.File]::WriteAllText("$work\batch_$year.scr", "NETLOAD`r`n`"$dll`"`r`nMCPBATCH`r`n_.QUIT`r`n_Y`r`n")
    $src = Join-Path $work 'src.dwg'
    Copy-Item $sample $src
    $version = Header $src
    Write-Output "== $year ($series, sample $version)"
    $sw = [Diagnostics.Stopwatch]::StartNew()

    # 1. read-only: nothing changes, nothing is saved
    $r = Run-Batch $year $src @{ steps = @(
            @{ tool = 'get_document_info'; args = @{} },
            @{ tool = 'list_layers'; args = @{ with_counts = $true } },
            @{ tool = 'purge'; args = @{ dry_run = $true } },
            @{ tool = 'audit'; args = @{} }); save_as = (Join-Path $work 'never.dwg') } 'read'
    if ($r) {
        Check ($r.ok -eq $true) "read: ok ($($r.steps | Where-Object { -not $_.ok } | ForEach-Object { $_.error }))"
        Check ($r.changed -eq $false) 'read: changed is false (AUDIT without fix sets DBMOD)'
        Check (-not (Test-Path (Join-Path $work 'never.dwg'))) 'read: nothing saved'
        Check ($null -ne $r.steps[3].result.errors_found) 'read: audit reports errors_found'
        Check ($r.plugin -match '^\d+\.\d+\.\d+$') 'read: plugin version in the result'
    }
    Write-Output "    one console: $($sw.ElapsedMilliseconds) ms"

    # 2. change + copy in the source's DWG version
    $copy = Join-Path $work 'copy.dwg'
    $r = Run-Batch $year $src @{ steps = @(
            @{ tool = 'create_layer'; args = @{ name = 'MCP_BATCH'; color = 3 } },
            @{ tool = 'create_line'; args = @{ start = @(0, 0, 0); end = @(10, 10, 0); layer = 'MCP_BATCH' } }); save_as = $copy } 'copy'
    if ($r) {
        Check ($r.ok -and $r.changed) 'copy: ok and changed'
        Check ((Test-Path $copy) -and (Header $copy) -eq $version) "copy: saved in $version"
        Check ($null -eq $r.backup) 'copy: no backup for a copy'
    }

    # 3. the copy holds the change
    $r = Run-Batch $year $copy @{ steps = @(@{ tool = 'find_entities'; args = @{ layers = @('MCP_BATCH') } }) } 'reopen'
    if ($r) { Check ($r.ok -and $r.steps[0].result.total -eq 1) 'reopen: the new line is in the copy' }

    # 4. in place, folder with a space: SAVEAS keeps the version and leaves .bak
    $inPlace = Join-Path $work 'with space\in place.dwg'
    Copy-Item $src $inPlace
    $r = Run-Batch $year $inPlace @{ steps = @(@{ tool = 'create_circle'; args = @{ center = @(0, 0, 0); radius = 5 } }); save_as = $inPlace } 'inplace'
    if ($r) {
        Check ($r.ok -and $r.changed -and $r.saved_to) "in place: saved ($($r.save_error))"
        Check ((Header $inPlace) -eq $version) "in place: still $version"
        Check ($r.backup -and (Test-Path $r.backup)) 'in place: .bak reported and present'
    }

    # 5. a tool that cannot run in a batch; continue_on_error goes on
    $r = Run-Batch $year $src @{ steps = @(
            @{ tool = 'zoom'; args = @{} },
            @{ tool = 'list_layouts'; args = @{} }); continue_on_error = $true } 'refused'
    if ($r) {
        Check ($r.ok -eq $false -and $r.steps[0].code -eq 'not_in_batch') 'refused: zoom is not_in_batch'
        Check ($r.steps.Count -eq 2 -and $r.steps[1].ok) 'refused: continue_on_error ran the next step'
    }
    $r = Run-Batch $year $src @{ steps = @(
            @{ tool = 'no_such_tool'; args = @{} },
            @{ tool = 'list_layouts'; args = @{} }) } 'stop'
    if ($r) { Check ($r.steps.Count -eq 1 -and $r.steps[0].code -eq 'unknown_tool') 'stop: stops at the first failed step' }

    # 6. four consoles in parallel
    $sw.Restart()
    $procs = foreach ($i in 1..4) {
        $f = Join-Path $work "par$i.dwg"
        Copy-Item $src $f
        Start-Batch $year $f @{ steps = @(@{ tool = 'create_line'; args = @{ start = @(0, 0, 0); end = @($i, $i, 0) } }); save_as = $f } "par$i"
    }
    foreach ($p in $procs) { if (-not $p.WaitForExit(120000)) { $p.Kill() } }
    $okCount = @(1..4 | Where-Object {
            $f = Join-Path $work "par$_.result.json"
            (Test-Path $f) -and (Get-Content $f -Raw -Encoding utf8 | ConvertFrom-Json).ok }).Count
    Check ($okCount -eq 4) "parallel: 4 of 4 ok (got $okCount)"
    Write-Output "    four consoles in parallel: $($sw.ElapsedMilliseconds) ms"
}

Check (((Get-LogPaths) -join "`n") -eq $logsBefore) "users' LogFilePath unchanged (consoles run /isolate)"
Remove-Item Env:\ACADMCP_BATCH_JOB -ErrorAction SilentlyContinue
if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
Write-Output "$($script:passed)/$($script:passed + $script:failed) passed"
if ($script:failed) { exit 1 }
