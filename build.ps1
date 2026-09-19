# Builds build\LivingMap.dll.
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1            # build and check references
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install   # ... and copy into plugins
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Package   # ... and zip for Thunderstore
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -NoCheck   # skip the reference check
#
# The compiler is csc.exe from the .NET Framework, present on every Windows. It only
# knows C# 5, and the source is deliberately written within that (no out var, ?., $""
# or nameof). With a dotnet SDK the same build can be done through src\LivingMap.csproj.
# References are read straight from the installed game and the r2modman profile, so
# the build is against exactly the game version the mod will run in.
#
# After the build, check-refs.ps1 compares every reference the DLL makes into the game's
# assemblies (and every reflection target) with what those assemblies actually contain -
# the compiler cannot guarantee that once the game updates, and the failure would only
# show at runtime.
#
# Paths are build-time only. Override them with environment variables instead of editing:
#   VALHEIM_MANAGED  = <game>\valheim_Data\Managed
#   BEPINEX_PROFILE  = the r2modman / Thunderstore Mod Manager profile that holds BepInEx\core

param([switch]$Install, [switch]$Package, [switch]$NoCheck)

$ErrorActionPreference = "Stop"
$root       = $PSScriptRoot
$managed    = if ($env:VALHEIM_MANAGED) { $env:VALHEIM_MANAGED } else { "D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed" }
$bepProfile = if ($env:BEPINEX_PROFILE) { $env:BEPINEX_PROFILE } else { "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\Valheim" }
$core       = "$bepProfile\BepInEx\core"
$plugins    = "$bepProfile\BepInEx\plugins\LivingMap"
$out     = "$root\build\LivingMap.dll"
$src     = Get-ChildItem "$root\src\*.cs" | Sort-Object Name | ForEach-Object { $_.FullName }
$main    = "$root\src\LivingMapPlugin.cs"

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc))     { throw "csc.exe not found under $env:WINDIR\Microsoft.NET" }
if (-not (Test-Path $managed)) { throw "Game assemblies not found: $managed (set VALHEIM_MANAGED or edit build.ps1)" }
if (-not (Test-Path $core))    { throw "BepInEx core not found: $core (set BEPINEX_PROFILE or edit build.ps1)" }

$refs = @(
    "assembly_valheim", "assembly_utils", "SoftReferenceableAssets",
    "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.PhysicsModule",
    "UnityEngine.UI", "UnityEngine.UIModule",
    "netstandard", "mscorlib", "System", "System.Core"
) | ForEach-Object { "/r:$managed\$_.dll" }
$refs += "/r:$core\BepInEx.dll"
$refs += "/r:$core\0Harmony.dll"

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# /nostdlib plus the game's mscorlib: compile against the runtime the mod will live in
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /nowarn:0618,1701,1702 "/out:$out" @refs @src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$ver = [System.Text.RegularExpressions.Regex]::Match((Get-Content $main -Raw), 'Version = "([^"]+)"').Groups[1].Value
Write-Host "Built $out (v$ver, $((Get-Item $out).Length) bytes, $($src.Count) source files)"

if (-not $NoCheck) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\check-refs.ps1" -Dll $out
    if ($LASTEXITCODE -ne 0) { throw "check-refs.ps1 found references that do not exist in the current game build" }
}

if ($Install) {
    New-Item -ItemType Directory -Force $plugins | Out-Null
    Copy-Item $out "$plugins\LivingMap.dll" -Force
    Write-Host "Installed to $plugins"
}

if ($Package) {
    # Thunderstore package: manifest.json (version filled in from the source), icon.png
    # 256x256, README.md, CHANGELOG.md and the DLL, all at the root of the archive.
    $ts   = "$root\thunderstore"
    $tmp  = Join-Path ([System.IO.Path]::GetTempPath()) ("LivingMap-pkg-" + [guid]::NewGuid().ToString("N"))
    $zip  = "$root\build\LivingMap-$ver.zip"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $manifest = Get-Content "$ts\manifest.json" -Raw -Encoding UTF8
        $manifest = $manifest -replace '"version_number"\s*:\s*"[^"]*"', ('"version_number": "' + $ver + '"')
        [System.IO.File]::WriteAllText("$tmp\manifest.json", $manifest, (New-Object System.Text.UTF8Encoding($false)))
        Copy-Item "$ts\icon.png"   "$tmp\icon.png"
        Copy-Item "$ts\README.md"  "$tmp\README.md"
        Copy-Item "$root\CHANGELOG.md" "$tmp\CHANGELOG.md"
        Copy-Item $out "$tmp\LivingMap.dll"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path "$tmp\*" -DestinationPath $zip
        Write-Host "Packaged $zip ($((Get-Item $zip).Length) bytes)"
    } finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
