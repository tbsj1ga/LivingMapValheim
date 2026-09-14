# MapOverlay

Клиентский мод для Valheim: рисует на карте постройки и обработанную мотыгой
землю. Ничего не патчит через Harmony, ничего не передаёт по сети, игроку без
мода не мешает никак.

Состояние и план работ — в `ROADMAP.md`, история версий — в `CHANGELOG.md`.

## Репозиторий

Папка — локальный git-репозиторий. Если он ещё не создан, один раз запусти
`git-init.ps1` (см. раздел «Git» ниже).

Что под версионированием: исходник, `.csproj`, документация и собранный
`build\MapOverlay.dll`. Что нет — накопленные данные по мирам (`*.bin`),
конфиг BepInEx и промежуточные `bin/`, `obj/`; всё это перечислено в
`.gitignore`.

## Установка

Файл `build/MapOverlay.dll` кладётся в

```
%AppData%\r2modmanPlus-local\Valheim\profiles\Valheim\BepInEx\plugins\MapOverlay\
```

Он уже там лежит — эта папка просто копия исходников и сборки.

## Где что лежит

| Что | Где |
|---|---|
| Конфиг | `BepInEx\config\j1ga.mapoverlay.cfg` |
| Накопленные данные по миру | `BepInEx\config\MapOverlay\<worldUID>.bin` |
| Исходник | `src\MapOverlayPlugin.cs` |
| Сборка | `build\MapOverlay.dll` |
| Версия мода (одно место) | константа `Version` в `src\MapOverlayPlugin.cs` |

Файл данных привязан к UID мира. Формат менялся несколько раз; при
несовпадении версии он просто игнорируется и набирается заново.

## Основные настройки

| Параметр | Смысл |
|---|---|
| `MapTextureScale` | 1 / 2 / 4 / 8 → 2048 / 4096 / 8192 / 16384 пикселей, то есть 12 / 6 / 3 / 1.5 метра на пиксель. Стоит только видеопамяти. |
| `MapRebuildInterval` | Минимум секунд между пересборками слоя. На масштабе 8 имеет смысл поднять до 1–2. |
| `MinPieceSizeMeters` | Минимальный размер куска постройки на карте, в метрах. Именно в метрах, чтобы рост разрешения не делал мелочь незаметнее. |
| `BuildingOutline` | Тёмная обводка вокруг построек, чтобы дом отделялся от площадки под ним. |
| `ScanRadius` | Радиус сканирования вокруг игрока. Выше ~128 м смысла нет: объекты существуют только в загруженных зонах. |
| `TerrainGridSize` | Шаг выборки ландшафта в метрах. 1.0 точнее, но вчетверо больше выборок. |
| `LinearColorFix` | Коррекция цвета под linear-рендер. Если цвета выглядят слишком тёмными — выключить. |
| `MapLayerFlipY` | Аварийный переворот слоя по вертикали, если графический API отрисует его зеркально. |
| `ShowClearedGround` | Показывать просто выровненную мотыгой землю (без покраски). По умолчанию выключено — закрашивает всю террасированную площадь. |
| `DetailedOverlay` | Старый слой поверх карты в экранном пространстве. Выключен; самый резкий на сильном приближении, но это отдельный слой, а не часть карты. |
| `DebugMarker` | Пурпурный крестик в позиции персонажа — для проверки, что слой совпадает с картой. |

После смены `MapTextureScale` нужен перезаход в мир.

## Сборка

Нужны ссылки на сборки игры и BepInEx. На Windows проще всего открыть
`src\MapOverlay.csproj` в Visual Studio или собрать из командной строки:

```
dotnet build src\MapOverlay.csproj -c Release
```

Пути к сборкам игры прописаны в `.csproj` и указывают на твою установку:
`D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed` и профиль
r2modman. Если игра переедет — поправить там.

Вариант через mono (так собирался этот DLL):

```
mcs -target:library -out:MapOverlay.dll -sdk:4.5 -langversion:latest \
  -r:<Managed>/assembly_valheim.dll \
  -r:<Managed>/UnityEngine.dll \
  -r:<Managed>/UnityEngine.CoreModule.dll \
  -r:<Managed>/UnityEngine.PhysicsModule.dll \
  -r:<Managed>/UnityEngine.UI.dll \
  -r:<Managed>/UnityEngine.UIModule.dll \
  -r:<Managed>/netstandard.dll \
  -r:<Managed>/SoftReferenceableAssets.dll \
  -r:<BepInEx>/core/BepInEx.dll \
  -r:<BepInEx>/core/0Harmony.dll \
  MapOverlayPlugin.cs
```

## Git

Создать репозиторий здесь:

```
powershell -ExecutionPolicy Bypass -File .\git-init.ps1
```

Скрипт проверяет наличие git, не трогает уже существующий `.git`, при
необходимости прописывает имя и почту локально (только для этого репозитория) и
делает первый коммит. `core.autocrlf` выключается намеренно — переводами строк
управляет `.gitattributes`, иначе они конфликтуют и дают шумные диффы.

Вручную то же самое:

```
git init -b main
git add -A
git commit -m "MapOverlay 0.8.0"
git tag v0.8.0
```

## Проверка перед установкой

Полезная привычка, которая в этом проекте уже дважды ловила чужие баги: после
сборки сверять все обращения мода в `assembly_valheim.dll` с тем, что реально
есть в текущей версии игры. Именно так в этой сессии были найдены причины
поломки AutoRepair и CraftFromContainers — они звали
`Character.Message(MessageType, string, int, Sprite)`, а в игре у метода уже
пять параметров. Несовпадение сигнатуры даёт `MissingMethodException` в
рантайме, и компилятор о нём не предупреждает, если собирать против старых
сборок.
