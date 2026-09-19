# Checks the built DLL against the current game version.
#
#   powershell -ExecutionPolicy Bypass -File .\check-refs.ps1 [-Dll path]
#
# build.ps1 runs it after every build. Two things are checked:
#
#   1. Every reference the mod makes to a type or member (method, field) of any
#      assembly in the game folder or BepInEx resolves to a real definition. The
#      compiler checks this too, but only against the assemblies present at build
#      time; after a game update a mismatch surfaces at runtime as a
#      MissingMethodException / TypeLoadException.
#
#   2. Reflection targets. The compiler never sees them: `typeof(ZDOMan).GetField(
#      "m_objectsByID", ...)` is just a string. The script finds the calls to
#      Type.GetField / GetMethod / GetProperty in the IL, takes the nearest ldtoken
#      (the type) and ldstr (the name) before each, and checks the type has that member.
#
# The tool is Mono.Cecil.dll from BepInEx\core; nothing needs installing.
# Paths can be overridden with the VALHEIM_MANAGED and BEPINEX_PROFILE environment variables.

param([string]$Dll = "$PSScriptRoot\build\LivingMap.dll")

$ErrorActionPreference = "Stop"
$managed = if ($env:VALHEIM_MANAGED) { $env:VALHEIM_MANAGED } else { "D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed" }
$core    = if ($env:BEPINEX_PROFILE) { "$env:BEPINEX_PROFILE\BepInEx\core" } else { "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\Valheim\BepInEx\core" }

if (-not (Test-Path $Dll))     { throw "DLL not found: $Dll" }
if (-not (Test-Path $managed)) { throw "Game assemblies not found: $managed" }
if (-not (Test-Path $core))    { throw "BepInEx core not found: $core" }

Add-Type -Path "$core\Mono.Cecil.dll"

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$resolver.AddSearchDirectory($core)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver
$mod = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Dll, $rp).MainModule

$problems = New-Object System.Collections.Generic.List[string]
$types = 0; $members = 0; $reflect = 0

# --- 1. types
foreach ($tr in $mod.GetTypeReferences()) {
    $types++
    try {
        $d = $tr.Resolve()
        if ($null -eq $d) { $problems.Add("type not found: $($tr.FullName) [$($tr.Scope.Name)]") }
    } catch {
        $problems.Add("type unresolvable: $($tr.FullName) [$($tr.Scope.Name)]: $($_.Exception.Message)")
    }
}

# --- 1. members
foreach ($mr in $mod.GetMemberReferences()) {
    $members++
    $where = $mr.DeclaringType.FullName + "::" + $mr.Name
    try {
        $d = $mr.Resolve()
        if ($null -eq $d) { $problems.Add("member not found: $where [$($mr.DeclaringType.Scope.Name)]") }
    } catch {
        $problems.Add("member unresolvable: $where : $($_.Exception.Message)")
    }
}

# --- 2. reflection
function Walk-Types($t) {
    $t
    foreach ($n in $t.NestedTypes) { Walk-Types $n }
}
foreach ($t in ($mod.Types | ForEach-Object { Walk-Types $_ })) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = $m.Body.Instructions
        for ($i = 0; $i -lt $ins.Count; $i++) {
            $op = $ins[$i]
            if ($op.OpCode.Name -notmatch '^callvirt$|^call$') { continue }
            $callee = $op.Operand
            if ($callee.DeclaringType.FullName -ne "System.Type") { continue }
            if ($callee.Name -notin @("GetField", "GetMethod", "GetProperty")) { continue }

            # back to the ldstr (name), then to the first ldtoken before it (the type): a GetMethod
            # with a parameter-type array has the parameters' ldtokens between the ldstr and the call
            $name = $null; $type = $null
            for ($j = $i - 1; $j -ge 0 -and $j -ge $i - 60; $j--) {
                $p = $ins[$j]
                if ($null -eq $name) {
                    if ($p.OpCode.Name -eq "ldstr") { $name = $p.Operand }
                    continue
                }
                if ($p.OpCode.Name -eq "ldtoken" -and $p.Operand -is [Mono.Cecil.TypeReference]) { $type = $p.Operand; break }
            }
            if (-not $name -or -not $type) {
                $problems.Add("reflection in $($m.Name): could not read the target of $($callee.Name)")
                continue
            }
            $reflect++
            $def = $null
            try { $def = $type.Resolve() } catch { }
            if ($null -eq $def) { $problems.Add("reflection: type $($type.FullName) not found (for $($callee.Name)('$name'))"); continue }

            $found = $false
            $cur = $def
            while ($null -ne $cur -and -not $found) {
                switch ($callee.Name) {
                    "GetField"    { $found = ($cur.Fields     | Where-Object { $_.Name -eq $name }).Count -gt 0 }
                    "GetMethod"   { $found = ($cur.Methods    | Where-Object { $_.Name -eq $name }).Count -gt 0 }
                    "GetProperty" { $found = ($cur.Properties | Where-Object { $_.Name -eq $name }).Count -gt 0 }
                }
                if ($found) { break }
                $cur = if ($null -ne $cur.BaseType) { try { $cur.BaseType.Resolve() } catch { $null } } else { $null }
            }
            if ($found) { Write-Host ("  ok  {0,-11} {1}.{2}" -f $callee.Name, $type.FullName, $name) }
            else        { $problems.Add("reflection: $($type.FullName).$name not found (in $($m.Name), $($callee.Name))") }
        }
    }
}

Write-Host "Checked $types type refs, $members member refs, $reflect reflection targets against $managed"
if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "PROBLEMS:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "All references resolve against the current game build." -ForegroundColor Green
exit 0
