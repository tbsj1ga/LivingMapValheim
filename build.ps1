# Сборка build\MapOverlay.dll.
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1            # только собрать
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install   # собрать и положить в plugins
#
# Компилятор — csc.exe из .NET Framework, который есть на любой Windows. Он понимает
# только C# 5, и исходник намеренно написан в этих рамках (без out var, ?., $"" и
# nameof). Если появится dotnet SDK, можно собирать и через src\MapOverlay.csproj,
# результат тот же. Ссылки берутся прямо из установленной игры и профиля r2modman,
# поэтому сборка идёт против ровно той версии игры, в которой мод будет работать.

param([switch]$Install)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$managed = "D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed"
$profile = "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\Valheim"
$core    = "$profile\BepInEx\core"
$plugins = "$profile\BepInEx\plugins\MapOverlay"
$out     = "$root\build\MapOverlay.dll"
$src     = "$root\src\MapOverlayPlugin.cs"

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc))     { throw "csc.exe not found under $env:WINDIR\Microsoft.NET" }
if (-not (Test-Path $managed)) { throw "Game assemblies not found: $managed (edit `$managed in build.ps1)" }
if (-not (Test-Path $core))    { throw "BepInEx core not found: $core (edit `$profile in build.ps1)" }

$refs = @(
    "assembly_valheim", "assembly_utils", "SoftReferenceableAssets",
    "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.PhysicsModule",
    "UnityEngine.UI", "UnityEngine.UIModule",
    "netstandard", "mscorlib", "System", "System.Core"
) | ForEach-Object { "/r:$managed\$_.dll" }
$refs += "/r:$core\BepInEx.dll"
$refs += "/r:$core\0Harmony.dll"

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

# /nostdlib + mscorlib игры: собираем против того рантайма, в котором мод будет жить
& $csc /nologo /noconfig /nostdlib+ /target:library /optimize+ /nowarn:0618,1701,1702 "/out:$out" @refs $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$ver = [System.Text.RegularExpressions.Regex]::Match((Get-Content $src -Raw), 'Version = "([^"]+)"').Groups[1].Value
Write-Host "Built $out (v$ver, $((Get-Item $out).Length) bytes)"

if ($Install) {
    New-Item -ItemType Directory -Force $plugins | Out-Null
    Copy-Item $out "$plugins\MapOverlay.dll" -Force
    Write-Host "Installed to $plugins"
}
