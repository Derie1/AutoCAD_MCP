<#
  Starts or stops AutoCAD for the smoke test.

    .\tests\AcadSession.ps1 -Start 2026    # start AutoCAD 2026 with a new empty drawing, run MCPSTART,
                                           # wait until the bridge answers
    .\tests\AcadSession.ps1 -Stop          # close all drawings WITHOUT saving and quit AutoCAD

  The test session allows run_command / eval_lisp through the ACAD_MCP_ALLOW_COMMANDS environment
  variable of that AutoCAD process only (the user's config.json is not touched); -NoCommands starts
  it with them off.

  With -NoCommands AutoCAD is started through WMI so it does not belong to the calling shell: stopping or
  timing out that shell does not kill AutoCAD. -Stop refuses if a drawing that is saved to a
  file has unsaved changes, so real work is never thrown away.
#>
param(
    [ValidateSet('2025', '2026', '2027')][string]$Start,
    [switch]$Stop,
    [switch]$NoCommands,
    [int]$Port = 8766,
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'

if ($Start) {
    if (Get-Process acad -ErrorAction SilentlyContinue) { throw 'AutoCAD is already running; stop it first.' }
    $exe = "C:\Program Files\Autodesk\AutoCAD $Start\acad.exe"
    if (-not (Test-Path $exe)) { throw "AutoCAD $Start is not installed ($exe)." }
    $script = Join-Path $env:TEMP 'acadmcp_start.scr'
    [IO.File]::WriteAllText($script, "MCPSTART`r`n")
    $cmd = "`"$exe`" /nologo /b `"$script`""
    if ($NoCommands) {
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
        if ($r.ReturnValue -ne 0) {
            # WMI can be denied (code 2) by policy; fall back to a normal child process.
            Write-Output "WMI start refused ($($r.ReturnValue)); starting AutoCAD as a child process."
            Start-Process -FilePath $exe -ArgumentList '/nologo', '/b', "`"$script`""
        }
    }
    else {
        # A WMI-created process does not inherit this shell's environment: start a child process with
        # the variable set (AutoCAD then belongs to this shell; -Stop still closes it cleanly).
        $env:ACAD_MCP_ALLOW_COMMANDS = '1'
        try { Start-Process -FilePath $exe -ArgumentList '/nologo', '/b', "`"$script`"" }
        finally { Remove-Item Env:ACAD_MCP_ALLOW_COMMANDS }
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        try {
            $h = Invoke-RestMethod "http://localhost:$Port/health" -TimeoutSec 3
            Write-Output "bridge up: $($h.host_product) $($h.acad_build), build $($h.build_series), plugin $($h.version), commands $($h.commands_source)"
            exit 0
        } catch { }
    }
    throw "the bridge did not answer within $TimeoutSeconds s (is the plugin installed? check %LOCALAPPDATA%\AcadMcpBridge\log.txt)"
}

if ($Stop) {
    $p = Get-Process acad -ErrorAction SilentlyContinue
    if (-not $p) { Write-Output 'AutoCAD is not running.'; exit 0 }
    # A busy AutoCAD rejects COM calls (RPC_E_CALL_REJECTED); retry for a while.
    function Retry([scriptblock]$action) {
        for ($i = 0; ; $i++) {
            try { return & $action }
            catch [Runtime.InteropServices.COMException] {
                if ($_.Exception.HResult -ne -2147418111 -or $i -ge 40) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
    }
    $app = Retry { [Runtime.InteropServices.Marshal]::GetActiveObject('AutoCAD.Application') }
    $docs = Retry { @($app.Documents) }
    foreach ($doc in $docs) {
        $named = Retry { [bool]$doc.FullName }
        $saved = Retry { $doc.Saved }
        if ($named -and -not $saved) { throw "drawing $(Retry { $doc.FullName }) has unsaved changes; not closing AutoCAD." }
    }
    foreach ($doc in $docs) { Retry { $doc.Close($false) } | Out-Null }
    Retry { $app.Quit() } | Out-Null
    $p | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue
    Write-Output 'AutoCAD closed.'
    exit 0
}

Write-Output 'Use -Start <year> or -Stop.'
