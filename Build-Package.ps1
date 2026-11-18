<#
  Builds AcadMcpBridge for every supported AutoCAD series and makes the release:

    release\AutoCAD_MCP_<ver>\INSTALL.BAT               installer (EN/RU), run by the user
    release\AutoCAD_MCP_<ver>\AcadMcpBridge.bundle.zip  the bundle INSTALL.BAT unpacks
    release\AutoCAD_MCP_<ver>\RELEASE.md                what the release is, requirements, installation (RU + EN)
    release\AutoCAD_MCP_<ver>\README.md
    release\AutoCAD_MCP_<ver>.zip                       the same folder as one archive, for users
    release\RELEASE.md                                  the description of the current release

  Archives of earlier versions stay in release\; only the unpacked folder of an earlier
  version is replaced by the current one.

    .\Build-Package.ps1           # build R25 + R26 and make the release
    .\Build-Package.ps1 -Deploy   # also install it here: INSTALL.BAT A, without registering the
                                  # server in AI clients (they already point at the installed DLL)

  The version comes from <Version> in src\AcadMcpBridge.csproj; BridgeApp.PluginVersion must
  match it, and the MCP server (server\AcadMcpServer) is published with the same version.
#>
param([switch]$Deploy)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$series = 'R25', 'R26'   # R25 = AutoCAD 2025-2026 (net8.0), R26 = AutoCAD 2027 (net10.0)

# ---- version: one source of truth, checked against the code
$version = ([xml](Get-Content "$root\src\AcadMcpBridge.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not (Select-String -Path "$root\src\BridgeApp.cs" -SimpleMatch "PluginVersion = `"$version`"" -Quiet)) {
    throw "BridgeApp.PluginVersion does not match csproj version $version" }

foreach ($s in $series) {
    Write-Host "== Building $s" -ForegroundColor Cyan
    dotnet build "$root\src\AcadMcpBridge.csproj" -c Release -p:AcadSeries=$s -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "build $s failed" }
}
Write-Host "== Publishing the MCP server" -ForegroundColor Cyan
$serverOut = Join-Path $root 'bin\Release\Server'
if (Test-Path $serverOut) { Remove-Item $serverOut -Recurse -Force }
dotnet publish "$root\server\AcadMcpServer\AcadMcpServer.csproj" -c Release -p:Version=$version -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

# ---- bundle, staged outside OneDrive (OneDrive holds handles on synced folders)
$stage = Join-Path $env:TEMP "AcadMcpBridge_build_$([guid]::NewGuid().ToString('N'))"
$bundle = Join-Path $stage 'AcadMcpBridge.bundle'
$parts = Join-Path $bundle 'Installer\PackageContents'
New-Item -ItemType Directory -Force $parts | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($f in Get-ChildItem "$root\installer\PackageContents\*.xml") {
    $text = [IO.File]::ReadAllText($f.FullName).Replace('@VERSION@', $version)
    [IO.File]::WriteAllText((Join-Path $parts $f.Name), $text, $utf8)
}
# Default manifest = all supported versions; INSTALL.BAT rebuilds it for the chosen ones.
$manifest = (Get-Content -Raw "$parts\head.xml") + (Get-Content -Raw "$parts\ALL.xml") + (Get-Content -Raw "$parts\tail.xml")
[IO.File]::WriteAllText("$bundle\PackageContents.xml", $manifest, $utf8)

foreach ($s in $series) {
    New-Item -ItemType Directory -Force "$bundle\Contents\$s" | Out-Null
    Copy-Item "$root\bin\Release\$s\AcadMcpBridge.dll" "$bundle\Contents\$s\"
}
# MCP server: started by the AI client as "dotnet.exe Contents\Server\acad_mcp_server.dll".
New-Item -ItemType Directory -Force "$bundle\Contents\Server" | Out-Null
Copy-Item "$serverOut\*" "$bundle\Contents\Server\"

# ---- release folder + archive
$name = "AutoCAD_MCP_$version"
$releaseRoot = Join-Path $root 'release'
$releaseDir = Join-Path $releaseRoot $name
# Keep the archives of all versions; only the unpacked folder is replaced by the current version.
Get-ChildItem $releaseRoot -Filter 'AutoCAD_MCP_*' -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $name } | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$zip = Join-Path $releaseDir 'AcadMcpBridge.bundle.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$bundle\*" -DestinationPath $zip

# INSTALL.BAT must be UTF-8 without BOM with CRLF line ends for cmd.exe.
$bat = [IO.File]::ReadAllText("$root\installer\INSTALL.BAT") -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $releaseDir 'INSTALL.BAT'), $bat, $utf8)
Copy-Item "$root\README.md" $releaseDir -Force
$notes = [IO.File]::ReadAllText("$root\installer\RELEASE.md").Replace('@VERSION@', $version)
[IO.File]::WriteAllText((Join-Path $releaseDir 'RELEASE.md'), $notes, $utf8)
[IO.File]::WriteAllText((Join-Path $releaseRoot 'RELEASE.md'), $notes, $utf8)

$releaseZip = Join-Path $releaseRoot "$name.zip"
if (Test-Path $releaseZip) { Remove-Item $releaseZip -Force }
Compress-Archive -Path "$releaseDir\*" -DestinationPath $releaseZip
Remove-Item $stage -Recurse -Force

Write-Host "Release $version ready:" -ForegroundColor Green
Write-Host "  $releaseDir"
Write-Host "  $releaseZip"

if ($Deploy) {
    $env:ACADMCP_NO_CLIENTS = '1'
    & cmd.exe /c "`"$releaseDir\INSTALL.BAT`" A EN"
    if ($LASTEXITCODE -ne 0) { throw "INSTALL.BAT failed with code $LASTEXITCODE" }
}
