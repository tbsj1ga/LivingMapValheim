# Сверка собранного DLL с текущей версией игры.
#
#   powershell -ExecutionPolicy Bypass -File .\check-refs.ps1 [-Dll путь]
#
# build.ps1 вызывает его сам после каждой сборки. Проверяются две вещи:
#
#   1. Каждая ссылка мода на тип или член (метод, поле) любой сборки из папки
#      игры и BepInEx резолвится в реальное определение. Компилятор это тоже
#      проверяет, но только против тех сборок, что лежали рядом при сборке; после
#      обновления игры несовпадение всплывает уже в рантайме как
#      MissingMethodException / TypeLoadException. Именно так ломались
#      AutoRepair и CraftFromContainers.
#
#   2. Цели рефлексии. Компилятор их не видит вовсе: `typeof(ZDOMan).GetField(
#      "m_objectsByID", …)` — просто строка. Скрипт находит в IL вызовы
#      Type.GetField / GetMethod / GetProperty, берёт ближайшие перед ними
#      ldtoken (тип) и ldstr (имя) и проверяет, что такой член у типа есть.
#
# Инструмент — Mono.Cecil.dll из BepInEx\core, ничего ставить не нужно.

param([string]$Dll = "$PSScriptRoot\build\MapOverlay.dll")

$ErrorActionPreference = "Stop"
$managed = "D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed"
$core    = "$env:APPDATA\r2modmanPlus-local\Valheim\profiles\Valheim\BepInEx\core"

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

# --- 1. типы
foreach ($tr in $mod.GetTypeReferences()) {
    $types++
    try {
        $d = $tr.Resolve()
        if ($null -eq $d) { $problems.Add("type not found: $($tr.FullName) [$($tr.Scope.Name)]") }
    } catch {
        $problems.Add("type unresolvable: $($tr.FullName) [$($tr.Scope.Name)]: $($_.Exception.Message)")
    }
}

# --- 1. члены
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

# --- 2. рефлексия
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

            # назад до ldstr (имя), затем до первого ldtoken перед ним (тип): у GetMethod
            # с массивом типов параметров между ldstr и вызовом лежат ldtoken параметров
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
