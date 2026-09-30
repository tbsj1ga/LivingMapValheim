# LivingMap

Client-side Valheim mod: the map shows the world the way people have shaped
it — buildings, hoe-worked ground, clear-cuts and planted groves — and changes
along with it. No Harmony patches, nothing sent over the network, no effect on
players without the mod.

> **New in 0.16.0 — the detailed map (experimental).** Zoom the big map in and it is drawn in
> detail, in the vanilla map's own style: buildings with their roofs, paths, fields and
> cleared forest. See [The detailed map](#the-detailed-map-0160-experimental) and the
> [changelog](CHANGELOG.md).

It is not a fork of [AzuMapDetails](https://thunderstore.io/c/valheim/p/Azumatt/AzuMapDetails/)
nor derived from it — an independent implementation with a different mechanism.

Buildings are read from the game's object database (`ZDOMan`) rather than from
the physics scene: on the host that is the whole world at once, and anything
torn down disappears from the map on its own. Trees come from the same
database: where the vanilla map draws forest but the trees are gone, the forest
pattern is erased, and where trees stand but the map does not know them (a
planted grove) the pattern appears — the vanilla one, because the map shader
draws it from our copy of the forest mask. Paths are read from the database
too: every zone anyone has hoed carries a `_TerrainCompiler` record with all
of its ground paint — on the host, again, the whole world at once.

State and plans are in `ROADMAP.md`, version history in `CHANGELOG.md`.
Russian versions: `README-RU.md`, `ROADMAP-RU.md`, `CHANGELOG-RU.md`.

## Repository

The folder is a local git repository (branch `main`; the first recorded
version is tag `v0.8.0`).

Under version control: the sources, `.csproj`, the build and check scripts,
the documentation, the Thunderstore package skeleton and the built
`build\LivingMap.dll`. Not under version control: the per-world data
(`*.bin`), the BepInEx config, intermediate `bin/` and `obj/`, zip packages
and a stray DLL copy in the root — all listed in `.gitignore`.

## The detailed map (0.16.0, experimental)

When you zoom the big map in, it is drawn in detail — in the vanilla map's own style. The
tiles are drawn by a port of the game's map shader (`Custom/mapshader`, read from its DX11
bytecode), fed with Living Map's own data instead of the vanilla 12 m textures:

- **the colour** with Living Map's paths, paving, fields and buildings painted in at the
  tile's resolution — buildings from the object database where their zone is loaded
  (rotated, with roof slopes and heights), from the stored footprints elsewhere, drawn
  lowest first so roofs end up on top;
- **heights** from the world generator plus the players' terrain edits;
- **the forest mask** with Living Map's cleared / planted corrections, sampled smoothly;
- the vanilla fog of war, mist and lava masks.

Everything the shader does is kept: the paper, the forest stamps, the water lines, the
mountains, the coast line, the snapping to "map pixels", the light from `_SunDir` with the
environment's sun and ambient colours, the fog of war. Measured against the real shader
(`livingmap port`): about 2 of 255 apart.

**Levels.** The first level is the vanilla map pixel (world / 7000, about 3.5 m) drawn from
the very textures the map shows — the old picture exactly, so the switch is invisible. Then
3.1, 2.75, 2.45, 2.2 and 2 m a pixel, spread evenly over the zoom from `DetailStartSpanMeters`
(6000 m of map width; 3000 m on a processor with 4 threads or fewer) to the game's closest
zoom. Finer than ~2 m would be denser than the map's own paper texture. Building and path
textures, edges, shadows and lit roof slopes grow in over the levels.

**Clouds** (`DetailClouds`): `Smooth` — their own layer drifting over the tiles, a little
dimmer than the game's (a layer cannot go brighter than white, the shader's clouds can);
`Exact` — drawn into the tiles with the game's brightness, the tiles redrawn every ~20 m of
drift; `Off` — none in the detail (the zoomed-out map keeps its clouds: the Mistlands mist
comes from the same texture).

**Cost.** Tiles of 256×256 on background threads (`DetailWorkerThreads`, 0 = automatic: 1 on
4 threads or fewer, else 2), at most two snapshots a frame on the main thread (~3.5 ms each on
an i5-12400F), about 0.2 s a tile there, up to 96 tiles cached (~48 MB) and ~60 MB for the
pattern textures. Only while the big map is open and zoomed in; until a tile is ready the
normal map shows there. When the light changes (sun, ambient, fog colour, sun direction),
the visible tiles are drawn again and swapped in together. `livingmap status` shows the
level on screen and these costs.

A one-time note the first time the map is opened with 0.16.0 lets a player keep it or turn
it off (`01 General / LastSeenNotice` remembers it). The earlier flat picture is still there
as `DetailStyle = Legacy`.

## Compatibility

Tested with **Valheim 1.0.16** (network version 40), **BepInEx 5.4.23.5** (BepInExPack_Valheim 5.4.2351).

## Who needs it

| Who | What |
|---|---|
| Player with the mod | sees buildings, paths and forest changes on their own map |
| Host / single player | the same, for the whole world at once |
| Dedicated server | not needed |
| Players without the mod | unaffected — nothing is sent over the network |

## Known conflicts

- Other mods that replace or resize the map texture or its shader may clash with `MapTextureScale` — set it to 1 to rule this out.
- SatelliteMap (Qua8ion) draws its own picture over the big map when zoomed in; the two have not been tested together.

## Bugs and feedback

GitHub Issues: https://github.com/tbsj1ga/LivingMapValheim/issues — please attach `BepInEx/LogOutput.log`.

## Screenshots

![Zooming in on a base: the map turns into the detailed picture step by step](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-zoom.webp)

![Close up: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-compare.png)

![A base up close: roofs, walls, paving, paths and fields](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-base.png)

![A clearing cut into the forest](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-forest.png)

![The big map: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/map-compare.png)

![The minimap: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/minimap-compare.png)

## Installation

`build/LivingMap.dll` goes into

```
%AppData%\r2modmanPlus-local\Valheim\profiles\Valheim\BepInEx\plugins\LivingMap\
```

(Thunderstore Mod Manager keeps its profiles under
`%AppData%\Thunderstore Mod Manager\DataFolder\Valheim\profiles\` instead.)
The mod has no runtime dependencies on paths: BepInEx tells it where the config
folder is, and the game's assemblies come with the game.

## Where things are

| What | Where |
|---|---|
| Config | `BepInEx\config\j1ga.livingmap.cfg` |
| Accumulated per-world data | `BepInEx\config\LivingMap\<worldUID>.bin` |
| Sources | `src\LivingMapPlugin*.cs` — one `partial class`, a file per area (see below) |
| Build | `build\LivingMap.dll` |
| Mod version (one place) | the `Version` constant in `src\LivingMapPlugin.cs`; `build.ps1 -Package` writes it into `manifest.json` |
| Reference check | `check-refs.ps1`, run by the build |
| Thunderstore package | `thunderstore\` (manifest, 256×256 icon, README) → `build\LivingMap-<version>.zip` |
| Licence | `LICENSE`, MIT |

The source is split into files of one `partial class LivingMapPlugin`:

| File | Contents |
|---|---|
| `LivingMapPlugin.cs` | constants, `PieceRec`, shared state, `Awake`/`OnDestroy`, `Tick`, `Setup`/`Teardown`, shared helpers, error handling |
| `.Config.cs` | every `ConfigEntry`, `BindConfig`, and the carry-over of settings that moved in 0.15.0 |
| `.Zdo.cs` | the `ZDOMan` pass: snapshot, prefab classification, footprints, bucket merging |
| `.TerrainRecords.cs` | paths from `_TerrainCompiler` records |
| `.Forest.cs` | cleared and planted forest, the `_MaskTex` mask |
| `.Gpu.cs` | the GPU layer: full rebuild, incremental drawing, the mask, fog of war |
| `.Scan.cs` | the fallback scanners: physics (`Physics.OverlapSphere`) and the live heightmap |
| `.Store.cs` | the `<worldUID>.bin` file |
| `.Commands.cs` | the `livingmap` console command |

The data file is bound to the world UID. Its format has changed several times;
on a version mismatch it is simply ignored and collected afresh.

## Settings

`BepInEx\config\j1ga.livingmap.cfg`, eight sections. In game, ConfigurationManager (F1)
shows them grouped for players: **1. Map layers** (`Enabled` and the `02 Layers` switches),
**2. Close-up detail** (`DetailEnabled`, `DetailStartSpanMeters` as a slider, `DetailClouds`,
`DetailStyledPieces`), **3. Colours**; everything else is marked *advanced* (**4. Advanced**,
**5. Debug**) and hidden until "Show advanced settings" is ticked. The keys and sections in
the file are what the tables below list.

**01 General**

| Setting | Meaning |
|---|---|
| `Enabled` | Master switch. |
| `SaveOverlay` | Remember what was collected between sessions, in a file per world. The host collects everything again within seconds anyway; this matters on a client of a dedicated server, which only ever gets what the server has sent it. |

**02 Layers** — each switch followed by its own thresholds

| Setting | Meaning |
|---|---|
| `ShowBuildings` | Build pieces, coloured by material. |
| `OnlyPlayerBuilt` | Show only what players placed: pieces that came with a location (ruins, draugr villages, dvergr outposts) carry no creator and are skipped. Switching it drops the stored pieces and collects them again — the host in seconds, a client of a dedicated server gets pieces from earlier sessions back only by passing by them. Off. |
| `ShowPaths` | Hoe-painted ground: dirt paths, paving and cultivated soil. |
| `ShowClearedGround` | Also mark ground that was merely levelled with the hoe (no paint). Off by default — it paints every terraced area. |
| `ShowClearedForest` | Erase the map's forest pattern where the trees are gone. The vanilla forest is a static mask from the world generator; the mod compares it with the trees in the object database. On the host every generated zone is checked, on a client of a dedicated server the zones you have been in this session (remembered between sessions). Clearings around locations show up too. Needs `BuildingScanSource = ZDO`. |
| `ClearedForestRadius` | Radius in metres of the window in which trees around a pixel are counted. 12 = the pixel and its neighbours, a 36×36 m window. Larger ignores natural gaps, smaller follows the edge of a clearing more closely. |
| `ClearedForestMaxTrees` | How many trees that window may still hold for the pixel to count as cleared. A natural wood has 6–40 per 36×36 m window, a clearing with a couple of trees left standing 1–2. |
| `ShowPlantedForest` | Draw the vanilla forest pattern where trees stand but the map knows no forest: a planted grove, or woods the generator's mask missed. Only in biomes where vanilla draws forest at all (Meadows, Black Forest, Plains); otherwise see `PlantedForestAnyBiome`. |
| `PlantedForestMinTrees` | Trees a map pixel (12×12 m) must hold to become forest. A planting at 2–3 m spacing gives 15+; the edge of a natural wood that the mask cuts mid-pixel gives up to 5. |
| `PlantedForestAnyBiome` | Draw forest in biomes where vanilla never does (swamps, mountains, Mistlands, Ashlands). Their natural woods then get the pattern as well — the look of the whole map changes. |
| `RespectFog` | Only draw on explored ground. Off shows everything the mod knows about, which on the host is the whole world. |

**03 Scanning**

| Setting | Meaning |
|---|---|
| `BuildingScanSource` | Where buildings come from. `ZDO` (default) — the game's object database: on the host the whole world at once, on a client of a dedicated server everything the server has sent this session. `Physics` — the old way, only colliders in the loaded zones around the player, within `ScanRadius`. If the database cannot be read the mod falls back to `Physics` on its own. |
| `PathScanSource` | Where paths come from. `ZDO` (default) — the terrain records in the object database: on the host every modified zone of the world at once, on a client the zones sent this session; only records whose revision changed are re-read. `Heightmap` — the old way, sampling the loaded terrain within `ScanRadius`. If the records cannot be read the mod falls back to `Heightmap` on its own. |
| `ZdoScanInterval` | Seconds between full passes over the object database: how long a new or removed piece takes to reach the map. |

**04 Rendering**

| Setting | Meaning |
|---|---|
| `MapTextureScale` | 1 / 2 / 4 / 8 → 2048 / 4096 / 8192 / 16384 pixels, i.e. 12 / 6 / 3 / 1.5 m per pixel; 67 MB / 268 MB / 1 GB of video memory. Default 2 (4 before 0.16.0). It decides how crisp **buildings and paths** are; the terrain underneath (coastlines, biome colours, the forest pattern) is the vanilla 12 m texture stretched to this size and does not get any more detailed; close up, the detail layer (`08 Detail`) draws the terrain itself. Takes effect on the next world load. |
| `MinPieceSizeMeters` | Smallest size a build piece is drawn at, in metres. Metres on purpose, so a sharper texture does not make small pieces fainter. |
| `BuildingOutline`, `OutlineWidthMeters` | A dark halo around buildings, so a house separates from the levelled ground under it, and how far it extends. |
| `TerrainGridSize` | Metres per cell of the path layer. The terrain records hold 1 m; 2 is plenty up to `MapTextureScale` 4, at 8 the difference shows. Halving it means four times the cells in memory and in the file. Changing it drops the stored path cells (the file records the step they were collected at); the host has them back within one pass. |

**05 Colors** — one per material (`Material_Wood` … `Material_Timberwood`, the roof kinds
`Material_Roof_Thatch`, `Material_Roof_Darkwood`, `Material_Roof_Turf`, `Material_Roof_Slate`,
and `Material_Unknown`), per kind of ground (`Terrain_DirtPath`, `Terrain_Paved`,
`Terrain_Cultivated`, `Terrain_Cleared`) and `Building_Outline` for the halo.

**06 Advanced**

| Setting | Meaning |
|---|---|
| `ZdoObjectsPerFrame` | Objects examined per frame during a pass. A world of 70 000 objects takes about 18 frames at the default. |
| `ScanRadius` | Radius around the player for the old `Physics` / `Heightmap` scanners; unused with the default ZDO sources. Above ~128 m gains nothing: objects exist only in loaded zones. |
| `MaxPieces`, `MaxTerrainCells` | Hard caps on what is stored; beyond them nothing new is recorded, with one warning in the log. |

**07 Debug**

| Setting | Meaning |
|---|---|
| `Debug` | Verbose log: the time of every scan and redraw, coordinates of newly added forest pixels, a self-check of terrain records against the loaded terrain. |
| `DebugMarker` | A magenta cross at the character's position, to confirm the layer lines up with the map. While it is on, every update is a full rebuild. |
| `IncrementalRedraw` | Off = rebuild the whole texture on every change, as before 0.11.0. Only for pinning down a drawing glitch. |
| `LinearColorFix` | Colour conversion for linear rendering, applied only when the game renders in linear colour space (it does). Off only to check whether the colours are the problem. |
| `MapLayerFlipY` | Emergency vertical flip of the layer, should the graphics API render it mirrored. |

**08 Detail** — the detailed map

| Setting | Meaning |
|---|---|
| `DetailEnabled` | The detailed map on or off. |
| `DetailStartSpanMeters` | How far out the detail starts, as the map's width on screen in metres (1000–12000). Default 6000, 3000 on a processor with 4 threads or fewer. Larger starts earlier and makes each step smaller. |
| `DetailClouds` | `Smooth` / `Exact` / `Off`, see above. |
| `DetailStyledPieces` | Textures, shaded edges, lit roof slopes and shadows for buildings and paths. Off = flat colours. |
| `DetailStyle` | `Vanilla` (the ported map shader) or `Legacy` (the earlier flat picture). Advanced. |
| `DetailPixelMeters` | 0 = automatic levels; a number = that pixel size at every level. Advanced. |
| `DetailFinestMetersPerPixel` | The finest level allowed. Advanced. |
| `DetailObjects` | Read buildings close up from the object database (rotated, with roofs). Advanced. |
| `DetailWorkerThreads` | Background drawing threads, 0 = automatic. Advanced, next world load. |

The scanners' pacing (a scan after 8 m of walking, at most once a second,
every 10 s when standing still), the redraw interval (0.5 s) and the save
interval (60 s) are constants in the source since 0.15.0.

## Console command

The game console (F5):

```
livingmap status                                  what the map remembers, sources, texture size, pass timing
livingmap reset                                   forget everything within 100 m and collect again
livingmap reset 300                               the same within 300 m
livingmap reset forest                            forest only, 100 m
livingmap reset paths all                         paths across the whole world
livingmap reset buildings forest 50               buildings and forest within 50 m
```

Layers: `buildings`, `paths`, `forest`; no layer means all three; `all` means
the whole world instead of a radius. The host hardly needs it — its database is
authoritative; the command is for a client of a dedicated server, whose data
from earlier sessions may have gone stale, and for debugging: resetting the
forest no longer means deleting the world file and the paths with it.

## Performance

Everything runs on Unity's main thread; below is what happens and how often.
Exact numbers go to the log with `Debug = true` (`… ms CPU` at the end of the
lines).

| What | When | Cost |
|---|---|---|
| `Tick` | every frame | a few time comparisons, nothing |
| Terrain scan (paths), `Heightmap` mode | at most once a second after moving 8 m, otherwise every 10 s | ~2600 `GetPaintMask` samples within 58 m, about 0.5–1 ms |
| ZDO pass | every `ZdoScanInterval` | reference snapshot (76k → ~0.3 ms), then `ZdoObjectsPerFrame` objects per frame: ~0.2–0.4 ms × 20 frames; merge ~0.1 ms |
| Forest evaluation | second phase of the same pass | 150 zones per frame, ~0.5 ms × (zones/150) frames |
| Terrain records (paths), `ZDO` mode | third phase of the same pass | only records whose revision changed: GZip inflate + parsing 65×65 vertices ≈ 0.5 ms per zone, 4 zones per frame; in a quiet world, nothing |
| Incremental draw | on new shapes, at most every 0.5 s | a handful of quads, fractions of a millisecond |
| Full rebuild | something disappeared, the texture was lost, a layer was switched | `Blit` of the whole texture (268 MB at scale 4, 1 GB at 8 — GPU work, 1–4 ms) + every shape through `GL.Vertex3` (~48k calls for 5300 pieces, 2–5 ms CPU) |
| Forest mask | when the forest changes | 16 MB `Blit` + a few hundred quads, <1 ms |
| Save | every 60 s when something changed | a ~200 KB file, fractions of a millisecond |
| World start | once | reading the 16 MB forest mask, loading the file, the first rebuild and the first pass; on a big world the first pass is stretched by the limit of 600 biome lookups per frame |

Memory: ~6–7 MB RAM (4 MB tree counters, two 512 KB bit sets, a ~600 KB ZDO
snapshot, buildings and caches for the rest). Video memory — see
`MapTextureScale`, plus 16 MB for the forest mask.

## Building

```
powershell -ExecutionPolicy Bypass -File .\build.ps1            # -> build\LivingMap.dll + reference check
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install   # ... and straight into plugins
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Package   # ... and a Thunderstore zip in build\
powershell -ExecutionPolicy Bypass -File .\build.ps1 -NoCheck   # skip the reference check
```

The script builds with `csc.exe` from the .NET Framework — present on every
Windows, nothing to install. It only knows C# 5, and the source is deliberately
written within that (no `out var`, `?.`, `$""`, `nameof`). References are read
from the installed game and the r2modman profile, so the build is against
exactly the game version the mod will run in. The paths are build-time only;
on another machine set the `VALHEIM_MANAGED` (the game's `valheim_Data\Managed`)
and `BEPINEX_PROFILE` (the profile holding `BepInEx\core`) environment variables
instead of editing the scripts.

With a dotnet SDK the project works as well (same environment variables, or
`-p:ValheimManaged=… -p:BepInExCore=…`):

```
dotnet build src\LivingMap.csproj -c Release
```

With mono:

```
mcs -target:library -out:LivingMap.dll -sdk:4.5 -langversion:latest \
  -r:<Managed>/assembly_valheim.dll \
  -r:<Managed>/assembly_utils.dll \
  -r:<Managed>/UnityEngine.dll \
  -r:<Managed>/UnityEngine.CoreModule.dll \
  -r:<Managed>/UnityEngine.PhysicsModule.dll \
  -r:<Managed>/UnityEngine.UI.dll \
  -r:<Managed>/UnityEngine.UIModule.dll \
  -r:<Managed>/netstandard.dll \
  -r:<Managed>/SoftReferenceableAssets.dll \
  -r:<BepInEx>/core/BepInEx.dll \
  -r:<BepInEx>/core/0Harmony.dll \
  LivingMapPlugin*.cs
```

## Reference check after the build

`check-refs.ps1` (run by `build.ps1` automatically) uses `Mono.Cecil` from
`BepInEx\core` to resolve every reference the DLL makes to a type or member of
the game's and BepInEx's assemblies, and finds the reflection targets in the IL
— `ldtoken` and `ldstr` before `Type.GetField/GetMethod/GetProperty` — checking
that the type really has such a member. Any mismatch fails the build.

Why: the compiler only checks against the assemblies present at build time;
after a game update a mismatch surfaces at runtime as a
`MissingMethodException`.

After a game update, running `build.ps1` is enough — if something was renamed,
the check says what.

## Thunderstore package

`build.ps1 -Package` produces `build\LivingMap-<version>.zip`: `manifest.json`
from `thunderstore\` with the version filled in, `icon.png` (256×256, a hard
requirement), the package `README.md` (the mod page), `CHANGELOG.md` and the DLL
at the root of the archive. The dependency is `denikson-BepInExPack_Valheim`.

## More mods by j1gA

| | Mod |
|---|---|
| [![StationSpeed](https://raw.githubusercontent.com/tbsj1ga/StationSpeedValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/) | **[StationSpeed](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/)** — Faster smelters, kilns, fermenters and crops — consistent even for players without the mod. |
| [![WeaponArts](https://raw.githubusercontent.com/tbsj1ga/WeaponArtsValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/) | **[WeaponArts](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/)** — One key, one active ability per weapon: stagger, taunt, heals, berserk, crits. |
| [![ExtendedBosses](https://raw.githubusercontent.com/tbsj1ga/ExtendedBossesValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/) | **[ExtendedBosses](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/)** — Raid-style boss fights: phases, adds, nests, shields, marks — built from vanilla parts. |
| [![HostOwner](https://raw.githubusercontent.com/tbsj1ga/HostOwnerValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/) | **[HostOwner](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/)** — The host takes ownership of stations and bosses near it, so its mods work for everyone. |
| [![HudLayout](https://raw.githubusercontent.com/tbsj1ga/HudLayoutValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/HudLayout/) | **[HudLayout](https://thunderstore.io/c/valheim/p/j1gA/HudLayout/)** — Move, resize and restyle your HUD with the mouse: bars, food, hotbar, minimap, even other mods' HUD. |

## AI assistance

This mod was developed with the help of an AI assistant (Claude by Anthropic).
The code and the documentation were written together with it and checked
against the game's IL; the design decisions, in-game testing and releases are
the author's.
