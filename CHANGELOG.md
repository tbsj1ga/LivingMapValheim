# Changelog

The version is set in one place — `LivingMapPlugin.Version` in `src/LivingMapPlugin.cs`.

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
