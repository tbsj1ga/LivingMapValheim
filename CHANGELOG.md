# Changelog

The version is set in one place — `LivingMapPlugin.Version` in `src/LivingMapPlugin.cs`.

## 0.17.0 — The host's map for its players

- **When you host, the players on your server get your map** - if they have Living Map
  too. The host knows the whole world; a player only learns what is around where they walk.
  Now each of them draws the host's buildings, paths and cleared and planted forest **on
  ground their own map has explored**: nothing new is revealed, but where they have been the
  map is current - a base rebuilt while they were away shows as it is now.
- How: the world is cut into 128 m tiles; the host sends the list of tiles with a hash of
  each when a player joins and whenever something changed (checked once a minute), the
  player fetches only the tiles it lacks or has with another hash, compressed, a capped batch
  at a time. The parts on unexplored ground wait in memory and are applied as the map gets
  explored (every 20 s).
- Settings, new group *4. Multiplayer* (`[09 Multiplayer]`): `ShareWithPlayers` (host,
  on) and `UseHostMap` (player, on). Players without the mod are not touched; a dedicated
  server does not share (it has no map of its own).
- `livingmap status` shows the sharing state: players served and tiles sent on the host,
  tiles received on a player.

- **Buildings in the mountains, on the minimap and the zoomed-out map** (`HighGroundOverlay`,
  new, in *1. Map layers*). The game's own map shader hides everything under its rock
  pattern above 70 m, and that is the shader that draws the minimap and the map until the
  detail starts - so a mountain base is simply not there. Living Map now draws those shapes
  a second time, into a texture of its own, and lays it over both map images. Only what
  stands at or above 70 m goes in, so nothing lower is drawn twice, and the texture exists
  only once something is built that high: a world without a mountain base costs nothing.
  `Off` / `Minimap` (default) / `Both`. It is a flat colour over the rock, not the shader's
  own shading - zoom in for the proper picture. Its texture shares the map layer's grid
  (capped at 8192), with no smoothing: it is always seen magnified, on the minimap and on
  the big map while the detailed one is still drawing, and anything coarser or smoothed
  turns a building into a soft patch. It is drawn again only when something up there has
  changed, not every time the map layer updates. How loud it is is a setting of its own
  (`HighGroundStrength`, 0.7): the layer lies over the map rather than in it, so at full
  strength its colours are the plain ones and in daylight on snow that is louder than
  anything the map draws itself; lower lets the rock show through. The colours themselves
  are taken a fifth of the way towards their own brightness for the same reason. On the
  zoomed-out map
  (`Both`) it lies under the detailed map: a tile of that map is opaque, so it covers this
  layer as soon as it is drawn, and this layer shows only where there is nothing better
  yet. Neither a moment with no mountain at all, nor flat buildings sitting over finished
  ones until the whole view is done.

- **How many drawn tiles are kept is now a setting** (`DetailTileCache`, default 96 as
  before). Panning the map used to throw the oldest away and draw them again from nothing
  on the way back - a thousand tiles drawn for a hundred different ones. Each tile is about
  half a megabyte of memory and as much again on the card. `DetailWorkerThreads` can now be
  set as high as 8 by hand; the automatic choice is unchanged.
- **The widest level of the detail is drawn like the rest** instead of being read back off
  the graphics card. That read was what made the first level match the map it replaces to
  the pixel, but it stopped the game for 20 to 225 ms per tile while the card answered -
  the worst hitch the mod had, and worst exactly where tiles are most numerous, zoomed out -
  and the pixels it brought back already had the rock pattern over the mountains with the
  buildings gone from under it. Drawn from the world and Living Map's own data instead,
  the level costs nothing on the main thread and keeps what is built up high.
  `DetailFirstLevelFromMapTexture` puts the old way back.
- **Zooming no longer empties the map for a moment.** While a level is being drawn, what
  shows behind it is the picture already drawn - and that was taken to be the one level
  coarser, which is not always where it is. The zoom steps over a level now and then, and a
  wide view is pushed coarser still, and then there was nothing behind and the game's own
  map showed through: on a mountain that map has no buildings at all, so they disappeared
  until the new tiles arrived. Whichever level has the most of the view already drawn is
  used instead.
- **The queue is kept short.** The main thread could take two snapshots a frame while the
  threads drew far fewer tiles in the same time, so it was paying three milliseconds each
  for jobs that the view had moved past by the time they were taken, or that were dropped
  when the map closed - 2689 snapshots for 1362 tiles drawn in one session. It now stops at
  about one job per thread in hand, which also makes the priorities mean something: the
  nearest tile is really the next one drawn.
- **A little more than the screen is drawn ahead** (`DetailPrefetch`, off by default): a ring of tiles
  around the view, so panning does not begin from nothing at the edge, and the view at the
  next closer level, so a step of the zoom has something to show at once. It is queued
  behind everything visible and never more than one tile a frame, so it takes nothing from
  what is on screen, and never at the first level, whose tiles cost the main thread a read
  back from the card. Measured on a world with the cache at 256 it still drew half the
  tiles a second time, so it is off unless the cache is raised well past its default.
- **The high-ground layer is lit like the map it lies on.** The shader darkens and tints
  everything it draws by the time of day; the layer is drawn after it and kept its colours,
  which read as a sticker at dusk. It is now multiplied by what that light comes to on flat
  ground - free, on the card, every frame. The paper and the shading of the terrain are
  still the detailed map's alone.
- **The detail is drawn faster, and only while it is wanted.** The threads that draw it run
  only while the big map is open, which is when the player is looking at the map rather than
  at the game, so the automatic choice is now the processor's threads less two, at most four
  (it was one on a processor with four threads or fewer, two otherwise). To keep that honest,
  whatever is still queued when the map closes is dropped instead of being worked through
  during play; a tile a thread had already started is finished and kept.

**Fixes**

- **Buildings in the mountains were not on the detailed map.** Above 70 m the game's map
  draws a rock pattern over the colour of the ground, and above 80 m it pulls everything
  dark on flat ground towards the grey of snow. Living Map paints its buildings and paths
  into that same colour, and the rock pattern is opaque, so from 95 m up it covered them
  completely: a mountain base was not on the map at all, and lower down it faded out. The
  detail layer now draws the rock, the snow and the lava of the Ashlands over the ground
  only, never over what Living Map painted. (The zoomed-out map and the minimap are drawn
  by the game's own shader, which still covers them - zoom in to see a mountain base.)
- **Buildings could be missing from the detailed map** although the map knew about them.
  The prefab cache is dropped on teardown (leaving a world, the map texture being
  re-created), but the detail layer's own table of prefab kinds was not: it then said
  "this is a building" while the cache had nothing, and the snapshot dropped every object
  of that prefab without a word. The tile was cached that way and stayed until something
  else in the area changed. Both tables are now dropped together, and every reader of the
  cache builds what it is missing instead of skipping the object.
- **A tile could keep showing an old picture** after something changed just outside it. A
  tile draws the objects of its own zones plus a 20 m ring around them, but the number
  that decides whether to redraw only counted the zones inside the tile. Both now use the
  same range.
- `livingmap status` lists the stored pieces by material, roof kinds included - enough to
  tell "the scan never saw it" from "it was collected but not drawn".

## 0.16.0 — The detailed map (experimental)

**For players**

- **The big map is drawn in detail when you zoom in**, in the vanilla map's own style: the
  same paper, forest, water, clouds and time-of-day light, with buildings, roofs, paths,
  fields and cleared forest in far more detail. The detail grows step by step as you zoom
  (3.5 → 3.1 → 2.75 → 2.45 → 2.2 → 2 m a pixel); the first step is the old picture exactly,
  so the switch is seamless. The minimap and the zoomed-out map are unchanged.
- **Buildings** keep their real shape and direction, with textures (planks, masonry, metal,
  marble), shaded edges and shadows; higher roofs are lighter.
- **Roofs by kind**: thatch, darkwood, turf and slate get their own colours and textures,
  zoomed out too, and their slopes catch the sun.
- **Paths, paving and fields** have their own textures and soft edges.
- **Buildings are drawn lowest first**, so roofs show over the walls and floors under them
  (before, a wall or floor could end up on top on the zoomed-out map).
- **A one-time note** the first time you open the map: keep the detailed map on (Yes) or turn
  it off (No).
- **Settings reorganised** for ConfigurationManager (F1): *1. Map layers*, *2. Close-up
  detail* (with a slider for how far out the detail starts), *3. Colours*; the technical
  ones are marked advanced and hidden by default. The keys in the config file are unchanged,
  so your values are kept.
- **Performance**: the detail is drawn only while the big map is open and zoomed in, on
  background threads (about 0.2 s a tile on an i5-12400F); until a part is ready the normal
  map shows there. On a processor with 4 threads or fewer it uses one thread and starts
  closer in.
- **Experimental**: please report anything that looks wrong, with a screenshot and
  `livingmap status`.

**Technical**

- The detail layer is a port of the game's map shader (`Custom/mapshader`, from its DX11
  bytecode) run on the tiles, fed with Living Map's data: the world generator's heights with
  the players' terrain edits, the colour with paths and buildings painted in, the forest mask
  with Living Map's cleared / planted corrections. Measured against the real shader with
  `livingmap port`: about 2 of 255 apart. The light direction is `_SunDir`; material and
  environment colours reach the shader unconverted.
- Tiles of 256×256, drawn on `DetailWorkerThreads` background threads (0 = automatic),
  at most two snapshots a frame on the main thread, up to 96 cached (~48 MB). When the light
  changes the visible tiles are drawn again and swapped in together.
- Clouds: `DetailClouds` = `Smooth` (their own drifting layer, a little dimmer), `Exact`
  (drawn into the tiles with the game's brightness, moving in steps) or `Off` (none in the
  detail; the zoomed-out map keeps its clouds, which carry the Mistlands mist).
- Buildings close up come from the object database (rotated, with roof slopes and heights);
  where the objects are not loaded, from the stored footprints. Roof kinds are recognised by
  the prefab family (`wood_roof`, `darkwood_roof`, `turf_roof`, `piece_grausten_roof`), the
  slope from the prefab's sloped collider.
- `MapTextureScale` defaults to 2 (was 4): close up the detail layer draws buildings itself,
  so the big texture is no longer needed — about 200 MB less video memory. An existing config
  keeps its value.
- `livingmap status` also reports the detail level shown and its cost (tile and snapshot
  times, cache).
- The earlier flat detail picture is still there as `DetailStyle = Legacy` (advanced); its
  tuning is no longer settings.

## 0.15.3

- Package page rewritten for players: what the mod shows, what it does not do (the terrain
  itself is not made more detailed), multiplayer, the few settings worth knowing. The
  misleading "sharper map texture" line is gone; `MapTextureScale` is described everywhere
  as the sharpness of buildings and paths. No gameplay changes.

## 0.15.2

- Package page: a section with the author's other mods (icons, one line each, links). No
  code changes.

## 0.15.1

- Package page: screenshots of the big map and the minimap, vanilla vs LivingMap; sections on
  compatibility, who needs the mod, known conflicts and where to report bugs; links to the
  GitHub repository. No code changes.

## 0.15.0

- **Settings regrouped**, by who needs them. Seven sections: `General`
  (`Enabled`, `SaveOverlay`), `Layers` (every switch followed by its own
  thresholds — the forest thresholds moved here from `Scanning`), `Scanning`
  (the two sources and `ZdoScanInterval`), `Rendering` (`MapTextureScale` and
  `TerrainGridSize` moved here), `Colors`, `Advanced` (`ZdoObjectsPerFrame`,
  `ScanRadius`, `MaxPieces`, `MaxTerrainCells`) and `Debug` (`Debug`,
  `DebugMarker`, `IncrementalRedraw`, `LinearColorFix`, `MapLayerFlipY`). The
  `Storage` section is gone. The outline colour is `Building_Outline`, so it no
  longer shares a name with the `BuildingOutline` switch. On the first start the
  values are carried over from the old file and the obsolete lines removed
  (including those of the ten settings 0.14.0 dropped); a value already
  present under the new section wins.
- **Six settings became constants**: `MoveDelta`, `ScanInterval`,
  `IdleRescanInterval` and `MaxColliders` (pacing and buffer of the fallback
  scanners, which do nothing with the default ZDO sources), `MapRebuildInterval`
  (0.5 s) and `SaveInterval` (60 s).
- **`TerrainGridSize` is safe to change.** Path cells are keyed by grid cell
  and the file did not record the step, so a changed step silently misplaced
  every stored cell. The file now records it (format v7); on a mismatch the
  path cells are dropped and collected again. Older files are read as is.
- Descriptions corrected: `ScanRadius` no longer claims to serve the path layer
  (not since 0.12.0 with `PathScanSource = ZDO`); the forest layers no longer
  ask for "the GPU map layer", the only one since 0.14.0.

## 0.14.0

- **`livingmap` console command**: `status` — what the map remembers and
  where from; `reset [buildings|paths|forest] [metres|all]` — forget a layer
  within a radius (100 m by default) or across the whole world and collect it
  again. Resetting the forest no longer means deleting the world file and the
  paths with it.
- **`OnlyPlayerBuilt`** — show only what players built, without the ruins and
  villages that come with locations (they carry no creator in the ZDO). Off.
  Switching it drops the stored pieces; the rule is remembered in the file (v6).
- **Cleanup.** The CPU painting path and the `DetailedOverlay` screen-space
  layer are gone along with their ten settings. The layer lives on the GPU
  only; without it the mod stays idle instead of painting the old way.

## 0.13.0

- **Rename: MapOverlay → LivingMap.** GUID `j1ga.livingmap`, name "Living Map",
  assembly `LivingMap.dll`, namespace and class `LivingMapPlugin`, package
  `LivingMap`. On the first start the settings and the per-world data are
  copied from the old files; the old files are left alone. If the old
  `MapOverlay.dll` is still loaded, the mod stays idle and says so in the log.

## 0.12.0

- **Paths across the whole map.** Ground paint is read from the
  `_TerrainCompiler` records in the object database (the same blob the game
  saves) instead of the loaded terrain around the player: on the host the whole
  world at once, on a client of a dedicated server the zones sent this session.
  Only records whose revision changed are re-read. Cells match the old scan to
  the metre. Setting: `PathScanSource` (`ZDO` / `Heightmap`). One difference
  from the old scan: paint that locations apply to the terrain without a record
  (the old `TerrainModifier`) is absent from the records and no longer shown.
  In debug mode, loaded zones get a check of the record against the live
  terrain.
- Infrastructure: the source is split into 10 `partial class` files by area;
  `check-refs.ps1` compares references and reflection targets with the current
  game after every build; `build.ps1 -Package` builds the Thunderstore package
  (`thunderstore\`); `LICENSE` (MIT); the `.csproj` writes to `build\`.

## 0.11.0

- **Incremental redraw.** New buildings, path cells and everything that comes
  out of the fog of war are drawn on top of the existing texture; the layer
  order (paths → outlines → fills) is restored locally by refilling the
  neighbours. A full rebuild happens only when something disappeared or
  changed, the texture was lost or a layer was switched. At scale 8 that is the
  difference between a gigabyte at a time and a couple of dozen quads.
- The forest mask is rebuilt separately and only when the forest changes.
- Fog of war on the GPU path: shapes in unexplored places now appear within
  15 s of exploring, not at the next map change.
- The physics building scanner no longer rebuilds the layer when nothing in
  its radius changed.
- Setting: `IncrementalRedraw` (off = the old behaviour).
- Default `MapTextureScale` 2 → 4. The debug log now carries the time of every
  step (`… ms CPU`).

## 0.10.0

- **Cleared forest.** Where the vanilla map draws forest but the object
  database holds no more than `ClearedForestMaxTrees` trees within the
  `ClearedForestRadius` window (`TreeBase` only; stumps, bushes and saplings do
  not count), the forest pattern is erased — clear-cuts around a base and
  clearings around locations show as they are. Trees are counted during the
  same ZDO pass; on the host every generated zone is checked
  (`ZoneSystem.m_generatedZones`), on a client of a dedicated server the zones
  that were inside the active area, with the result remembered in the file
  (format v4; v3 still reads). Drawn into a copy of the `_MaskTex` mask with a
  multiplicative blend, so the vanilla shader removes the forest itself.
  Settings: `ShowClearedForest`, `ClearedForestRadius`, `ClearedForestMaxTrees`.
  GPU layer only.
- **Planted forest.** Where the map knows no forest but a pixel holds
  `PlantedForestMinTrees` trees or more, the forest pattern appears — the
  vanilla one, with an additive blend into the same mask copy. Only in biomes
  where vanilla draws forest at all, unless `PlantedForestAnyBiome` is on.
  Settings: `ShowPlantedForest`, `PlantedForestMinTrees`,
  `PlantedForestAnyBiome`. File format v5 (v3/v4 still read).
- The ZDO pass now runs with `ShowBuildings` off as well (the forest layer
  needs it); buildings are left alone then.

## 0.9.0

- **Buildings are read from the game's object database (`ZDOMan`), not from
  physics.** On the host that is the whole world at once, no walking needed,
  and demolished pieces disappear from the map on their own; on a client of a
  dedicated server it is everything the server has sent this session, and
  clearing empty pixels is trusted only inside the active area. Footprints
  come from the `piece`-layer colliders of the prefab (`ZNetScene`) with the
  object's rotation applied; cached per prefab hash. The database is walked in
  slices across frames with no allocations between passes (a list pool, a
  reused reference snapshot). New settings: `BuildingScanSource`
  (`ZDO` / `Physics`), `ZdoScanInterval`, `ZdoObjectsPerFrame`. The physics
  scanner stays as the fallback: by setting, or on its own if the
  `m_objectsByID` field is missing or reading the database fails.
- `build.ps1` — builds `build\LivingMap.dll` with the stock `csc.exe` from the
  .NET Framework; `-Install` copies into plugins. `assembly_utils` added to the
  `.csproj` and the reference list (the `Vector2s` type in the signature of
  `ZNetScene.InActiveArea`).

## 0.8.0

- The minimum shape size is in **metres** (`MinPieceSizeMeters`), not texture
  pixels. Before, raising `MapTextureScale` made small building pieces *less*
  visible, not more.
- A contrasting material palette plus a dark outline around buildings
  (`BuildingOutline`), so a house separates from the hoe-levelled ground under
  it (wood and a dirt path used to be almost the same brown).

## 0.7.0

- The layer is drawn straight into the map texture: `RenderTexture` +
  `Graphics.Blit` of the vanilla texture + drawing through `GL` with the
  `Hidden/Internal-Colored` shader. It is now *part of the map*: it pans,
  zooms, goes under markers and under the fog of war, works on the minimap.
- `MapTextureScale` 1/2/4/8 → 2048/4096/8192/16384 pixels, stepping down when
  the texture allocation is refused.
- `LinearColorFix` — colour-space correction (`Color.linear`); otherwise the
  colours washed out under linear rendering.
- The screen-space layer (`DetailedOverlay`) is now off by default.

## 0.3.0

- Fixed the position of the screen-space layer: `Minimap.MapPointToLocalGuiPos`
  already returns `0..rect.width` from the lower-left corner of the image, so
  subtracting `rect.xMin` was wrong — the layer drifted up and right by half a
  screen.
- Removed a systematic ~0.5 m terrain offset: `GetVegetationMask` samples at
  `p - 0.5`, `GetPaintMask(Vector3)` does not.

## 0.2.0

- Hoe-made paths are finally visible. Only the green and blue channels of the
  paint mask were checked, but `PaintType.Dirt` is **red**, and that is the most
  common way of making roads.
- An optional layer for merely levelled ground (`ShowClearedGround`).

## 0.1.0

- First working version: buildings scanned through `Physics.OverlapSphere`,
  colour by `WearNTear.MaterialType`, accumulation in a per-world file, fog of
  war respected, a 25-error fuse.
- Fixed right after release: ground-paint classification summed all four mask
  channels, and alpha is 1 in every mask — so all ground around the player was
  painted as paved.
