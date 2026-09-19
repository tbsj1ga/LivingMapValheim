# LivingMap — state and plans

A Valheim mod: shows buildings (by material) and hoe-worked ground (paths,
paving, cultivator) on the map. No Harmony patches, client-side only, nothing
synchronised over the network.

Current version: **0.14.0** (tag `v0.14.0`), started in the game. Game: Valheim
1.0.14, BepInEx 5.4.23.5.

---

## Done

- [x] **The layer is drawn straight into the map texture.** `RenderTexture` +
      `Graphics.Blit` of the vanilla map + shapes drawn through `GL` with the
      built-in `Hidden/Internal-Colored` shader. The result *is* the map: it
      pans, zooms, goes under the fog of war and under every marker, works on
      the minimap. Nothing is held in RAM.
- [x] **Resolution above vanilla.** `MapTextureScale`: 2048 (12 m/px, 16 MB) →
      4096 (6 m/px, 67 MB) → 8192 (3 m/px, 268 MB) → 16384 (1.5 m/px, ~1 GB).
      Video memory only. If the card refuses, the size steps down automatically.
- [x] **Buildings with real footprints.** `collider.bounds` of every collider,
      drawn as the real rectangle rather than a fixed-size square. A 32 m
      per-side cap against stray giant colliders.
- [x] **Scanning through ZDO instead of physics** (0.9.0). Every
      `ZdoScanInterval` a snapshot of `ZDOMan.m_objectsByID` (reflection on the
      field, type checked) is walked in slices of `ZdoObjectsPerFrame` per
      frame. A cache of "prefab hash → material + local AABB of the `piece`-layer
      colliders" is built from the prefab in `ZNetScene`; each object's rotation
      is applied from `ZDO.GetRotation()`. Merge semantics: on the host
      (`ZNet.IsServer()`) the set is authoritative for the whole world — what is
      missing from the database is removed; on a client of a dedicated server
      the database holds everything the server has sent this session
      (persistent ZDOs are not dropped when a zone unloads, and destruction is
      broadcast to every peer), so a pixel with data is always overwritten while
      an empty pixel is cleared only inside the active area
      (`ZNetScene.InActiveArea`) and only if it was inside it on the previous
      pass too — otherwise the map would flicker while zones load. Buckets are
      compared by an order-independent signature so a pass with no changes does
      not rebuild the GPU layer. On errors (10 of them) or a missing field, the
      physics fallback takes over; `BuildingScanSource = Physics` forces it.
- [x] **Cleared forest** (0.10.0). The vanilla forest on the map is a static
      mask from the generator (`_MaskTex`, R channel: Meadows and Plains by
      forest factor, the whole Black Forest; G is the Mistlands fog, B is lava).
      A tree is a `TreeBase` only (what falls and gives logs); stumps, bushes and
      saplings are `Destructible`, some with the `Tree` hit type, but a clearing
      is full of them and they are not forest. During the ZDO pass trees are
      counted per map pixel (`byte[]`, 4 MB); a pixel is cleared when the mask
      says forest, the `ClearedForestRadius` window (3×3 pixels by default)
      holds no more than `ClearedForestMaxTrees` trees, and the whole window
      lies in zones with complete data: on the host `ZoneSystem.m_generatedZones`
      (reflection), on a client the zones that sat inside the active area two
      passes in a row (accumulated over the session, the result saved to the
      file, format v4). Evaluation is the second phase of the pass, 150 zones per
      frame. Drawn into a copy of the mask with a multiplicative `(0,1,1,1)`
      blend — only R goes dark, and the vanilla shader stops drawing forest by
      itself. Memory: two 512 KB bit sets and 4 MB of counters. GPU layer only.
      The first in-game run showed a 5×5 window with no tree at all was too
      strict: 12 hits per world and none near the base — hence the tree budget
      per window and the 3×3 default.
- [x] **Paths from terrain records** (0.12.0). Every zone with hoe edits
      carries a `_TerrainCompiler` ZDO (the `TerrainComp` component); in
      `ZDOVars.s_TCData` is a GZip blob: `int version, int ops, Vector3, float,
      int n, [bool modifiedHeight, (float, float)]×n, int m,
      [bool modifiedPaint, (r,g,b,a)]×m`, a 65×65 grid at 1 m, vertex `(x, z)`
      at `zoneCentre + (x − 32, z − 32)` in the world. Records are collected
      during the ZDO pass and parsed in a third phase, 4 zones per frame, but
      only those whose `ZDO.DataRevision` changed — in a quiet world the phase
      costs nothing. A cell is taken at the same vertex the old scan read
      (`Heightmap.WorldToVertexMask`, queried at `centre − 0.5`), so both
      sources agree to the metre. A record is authoritative for its zone: cells
      without paint are removed. `ShowClearedGround` now also uses
      `modifiedHeight`. Falls back to `Heightmap` after 10 errors or by the
      `PathScanSource` setting.
- [x] **Incremental redraw** (0.11.0). The layer texture lives between
      updates; everything new (buildings from the diff of the ZDO pass buckets,
      path cells from the terrain scanner, whatever came out of the fog of war)
      is queued and drawn on top every `MapRebuildInterval`. The layer order is
      restored locally: after a new shape's outline, every shape whose rectangle
      its halo touches is filled again (bucket lookup ±2 pixels); a new path
      cell redraws the buildings standing on it. A full rebuild (`Blit` +
      everything again) happens only when something disappeared or changed (a
      demolished piece, repainted ground), the texture was lost, a layer was
      switched in the config, `DebugMarker` is on, or more than 1500 new shapes
      arrived at once. The forest mask is a separate cheap layer (`_maskDirty`),
      rebuilt whole only when the forest changes. Fog of war: skipped shapes
      wait in deferred lists and are checked for exploration every 15 s — on the
      GPU path they used to appear only at the next change.
- [x] **Manual area reset and targeted forest reset** (0.14.0) — the
      `livingmap reset [buildings|paths|forest] [metres|all]` console command in
      `Commands.cs` (`Terminal.ConsoleCommand`, registered in `Awake`; the
      constructor just puts the command into a static dictionary). Forgets the
      chosen layers within a radius (100 m by default) or across the whole
      world, resets the terrain record revisions and the trusted forest zones,
      and starts a pass again. `livingmap status` — what the map remembers and
      where from.
- [x] **Player-built only** (0.14.0) — `OnlyPlayerBuilt`, off.
      `ZDO.GetLong(ZDOVars.s_creator, 0) == 0` → a location's piece, skipped
      (in the ZDO pass and in the physics scanner alike). `PieceRec` carries no
      creator, so changing the rule drops the stored pieces; so that a client
      does not lose them on every start the rule is written to the file (format
      v6, one byte at the end; v3–v5 read as "rule off").
- [x] **Cleanup** (0.14.0). Removed the CPU path that painted the vanilla
      texture (`Flush`, `_pending` and the queues) and the `DetailedOverlay`
      screen-space layer together with their settings (`PaintMapTexture`,
      `DetailedOverlay`, `MinimapOverlay`, `FlushInterval`, `PixelsPerFlush`,
      `OverlayOpacity`, `SmoothOverlay`, `MinimapOverlayResolution`,
      `DetailedOverlayResolution`, `DetailedOverlayInterval`) and two reflection
      targets. The layer lives on the GPU only; if the `RenderTexture` cannot be
      created the mod logs a warning and stays idle for the session, leaving the
      vanilla map alone. The physics scanner and the live heightmap remain as
      automatic fallbacks. Orphaned keys in an old `.cfg` are kept by BepInEx
      and are harmless.
- [x] **Planted forest** (0.10.0) — the reverse case through the same
      mechanism: a pixel outside the vanilla mask holding ≥ `PlantedForestMinTrees`
      trees gets R=1 with an additive `(1,0,0,0)` blend, and the shader draws its
      pattern. The game has no "planted" marker on a grown tree (`Plant.Grow`
      simply instantiates the adult prefab), so the only difference from nature
      is density and biome: by default only Meadows / Black Forest / Plains
      count (`WorldGenerator.GetBiome`, cached per pixel, at most 600 first-time
      lookups per frame); `PlantedForestAnyBiome` lifts the restriction. Adding
      needs no zone trust (a tree in the database exists); removal follows the
      building rules. Stored in the file, format v5 (v3/v4 still read).
- [x] **Colour by material.** `WearNTear.MaterialType`, all 9 values
      (Wood, Stone, Iron, HardWood, Marble, Ashstone, Ancient, Ice, Timberwood).
- [x] **Dark building outline.** A separate pass before the fill, so a house
      separates from the levelled ground under it.
- [x] **Paths from all three channels of the paint mask.** `PaintType` in the
      game is `{ Dirt = red, Cultivate = green, Paved = blue }`; alpha is the
      vegetation mask and has nothing to do with the paint type. Plus an
      optional layer for merely levelled ground (`ShowClearedGround`).
- [x] **Fog of war respected** — nothing is drawn where the player has not been.
- [x] **Accumulation and persistence.** A file per world in
      `BepInEx/config/LivingMap/<worldUID>.bin`. Walked once — kept forever, no
      need to walk again.
- [x] **Colour-space correction** (`LinearColorFix`) — otherwise the colours
      wash out under linear rendering.
- [x] **Minimum shape size in metres, not texture pixels.** Without it, raising
      `MapTextureScale` made small pieces *less* visible.
- [x] **Safety.** Zero Harmony patches; everything in try/catch; deduplicated
      errors in the log; a global fuse (after 25 errors the mod switches itself
      off and restores the vanilla texture); separate degradation of the terrain
      layer; caps on the stored data; `OverlapSphereNonAlloc` with a reused
      buffer.

---

## Known limitations (not bugs)

- **The precision ceiling is 1.5 m per pixel.** The map covers 24.6 km; at the
  card's maximum texture size (16384) that is the limit. Sharper is possible
  only with a separate screen-space layer.
- **With `PathScanSource = Heightmap`, paths are visible only where you have
  been** — the terrain loads zone by zone. In `ZDO` mode (the default) the host
  sees all of them, a client the ones sent this session, same as buildings.
- **On a client of a dedicated server buildings can go stale.** The client's
  object database holds only what the server has sent this session. A piece
  demolished while you were offline, and standing alone in its map pixel, stays
  until you visit its zone. The host has no such limitation.
- **A clearing is "no trees in the window", not "someone felled them".**
  Clearings around locations and natural gaps wider than `ClearedForestRadius`
  (mostly in the sparse Plains woods) show up too. That is honest about the
  terrain, but it does not tell a clear-cut from a glade. The edge of a clearing
  on the map is accurate to a vanilla mask pixel (12 m) and "creeps" inward by
  the window radius.

---

## Before new tasks

- **The client path has never been exercised**: trust in the active area,
  `_trustedZones`, terrain records on a client, the migration — all written from
  the game's IL and never run, because the author is always the host. One
  player who joins with the mod and sends a log would close the biggest blind
  spot.
- The repository exists on this disk only. One `git push` to a private GitHub
  and that risk is gone, and the manifest gets a `website_url`.

## Plan

### 1. `livingmap reload` — re-read the config without a restart
BepInEx reads the `.cfg` once at start; without Configuration Manager any edit
means re-entering the world. Most switches are already tracked on the fly
(`ShowBuildings/ShowPaths/BuildingOutline` → full rebuild, `ShowClearedForest`
→ `Setup`, `OnlyPlayerBuilt` → dropping the pieces), so `Config.Reload()` in
`Commands.cs` plus a message about what changed is enough. Only
`MapTextureScale` and the colours are not picked up without a re-enter (the
colours because what is already drawn is not repainted; after `reload` simply
setting `_rtDirty` makes a full rebuild pick them up). ~20 lines.

### 2. Ships and carts as pins
The ZDO pass already runs; prefabs with a `Ship` or `Vagon` component are one
more branch in `BuildPrefabInfo` (`IsVehicle`). Not texture but pins: they move,
so they cannot be drawn into the layer. Mechanism — `Minimap.AddPin(pos,
PinType, name, save: false, isChecked: false)` returns a `PinData` with a
public `m_pos`; the pin is updated in place, `RemovePin(PinData)` when the ZDO
is gone. A `ZDOID → PinData` dictionary, updated in `FinishZdoPass`, i.e. every
`ZdoScanInterval`; a ship under way lags by up to 5 s — fine for a map. Icon:
a custom sprite (`PinData.m_icon`), otherwise `PinType.Icon0..4` — the vanilla
markers. Name — the prefab (`Karve`, `VikingShip`, `Cart`); for a named ship,
its name from the ZDO if it is there (check `ZDOVars`). The host sees every ship
in the world, a client the ones sent; `RespectFog` — not shown in unexplored
areas. Setting `ShowVehicles`, off by default: it is the only layer that
changes the vanilla set of pins. `save: false` — the pins do not enter the
player profile and do not survive the mod being removed.

### 3. `livingmap export [size]` — the map as PNG
`_rt` already holds the finished map with our layers; the forest on top of it is
drawn by the shader from `_rtMask`, and the fog from `m_fogTexture`, so an
honest "as on screen" export is impossible without repeating the shader. What
gets exported is `_rt` as it is (buildings, paths, the vanilla base) — enough
to share a map. Mechanism: `RenderTexture.active = _rt`, `Texture2D.ReadPixels`,
`EncodeToPNG` (needs a reference to `UnityEngine.ImageConversionModule.dll` in
`build.ps1` and the `.csproj`). At scale 8 that is 16384² = 1 GB of managed
memory for `ReadPixels` — first `Graphics.Blit` into a temporary
`RenderTexture` of the wanted size (4096 by default, a command argument), then
read. The file — `BepInEx\config\LivingMap\<world>-<date>.png`, the path in
the command's reply. Option: `export map` — the vanilla map only, for
comparison.

## Ideas

No dates; as real need appears.

- **Colour by owner.** The ZDO holds `creator` and `creatorName`; a "who built
  it" mode instead of "what of" — on a shared world it shows at once whose base
  is where. Cost: an owner index byte in `PieceRec`, an owner table in the file
  (format v7), a palette, a mode setting. The only idea that changes *what* the
  map shows.
- **`ShowOnMinimap`** — draw on the big map only. Cheap if `m_mapImageSmall`
  and `m_mapImageLarge` have different materials (vanilla sets the textures on
  both separately — it looks like they do; verify).
- **`TerrainGridSize = 1`.** The terrain records store 1 m, the mod takes 2 m;
  at scale 8 the difference shows. Already in the config, now free on the CPU —
  only ×4 cells in memory and in the file. A note rather than a task.
- **Splitting `Gpu.cs`** into the rebuild and the incremental draw, if it keeps
  growing.
- **A server half for clients.** A client would see the whole world like the
  host. A networked mod with version synchronisation — the "deliberately not
  doing" section; move it only on an explicit request.

---

## Project infrastructure

Not about the mod's functionality, but about keeping it convenient to work on.

- [x] Local git repository (`.gitignore`, `.gitattributes`; created
      2026-09-14, the first commit is tag `v0.8.0`)
- [x] `CHANGELOG.md` with the version history
- [x] **One-command build into `build\` and copy into plugins** —
      `build.ps1` / `build.ps1 -Install` (0.9.0). Builds with the legacy
      `csc.exe` from the .NET Framework, because this machine has neither a
      dotnet SDK nor mono; hence the "source within C# 5" constraint. The
      `.csproj` now also writes to `build\` (`OutputPath`, no deps/pdb) and takes
      every `src\*.cs` by the SDK-style default — not verified on this machine
      (no dotnet). Build paths can be overridden with the `VALHEIM_MANAGED` and
      `BEPINEX_PROFILE` environment variables (the `.csproj` reads
      `VALHEIM_MANAGED` and `BEPINEX_CORE`).
- [x] **Automatic signature check after the build** — `check-refs.ps1`,
      called by `build.ps1` (`-NoCheck` disables it). `Mono.Cecil` resolves
      every reference to a type and member of the game's/BepInEx's assemblies,
      and the reflection targets are fished out of the IL: an `ldstr` with the
      name and an `ldtoken` with the type before `Type.GetField/GetMethod/GetProperty`.
      Verified with a negative test: a swapped field name in a reflection string
      and a swapped method name in the reference table — both caught.
- [x] **`LICENSE`** — MIT, the author from `git config user.name`; adjust if
      the signature should differ.
- [x] **Rename to LivingMap** (0.13.0). The old name described an overlay on
      top of the map, gone since 0.7.0; "MapOverlay" also collides with a term
      in the Jötunn API. The repository folder was renamed to `LivingMap` on
      2026-09-19. The config and data migration lives in `LivingMapPlugin.cs`,
      section "the old name"; it can be removed a few versions on.
- [x] **Thunderstore package** — `thunderstore\{manifest.json, icon.png,
      README.md}`; `build.ps1 -Package` fills in the version from the source and
      builds `build\LivingMap-<version>.zip` (in `.gitignore`). The 256×256
      icon was drawn by a script — a fragment of the kind of map the mod draws.
      `website_url` is empty: fill it in if a public repository appears.
      Committing the manifest, icon and README while leaving the zip out
      matches common practice (JotunnModStub `Package/`, JereKuusela's
      `publish/`, searica's `Package/`).
- [x] **Splitting `LivingMapPlugin.cs`** — 10 files of one `partial class` by
      area (see the table in README). The cut was mechanical, by lines;
      verified with Cecil: 90 methods and 194 fields match by name, signature
      and IL size against the build before the split.
- [x] **Documentation in English** with Russian copies (`*-RU.md`); every
      comment in the scripts, the `.csproj` and the git config files is English.

---

### Deliberately not doing
- **Synchronisation between players.** Decided at the planning stage: the mod
  is local. Writing anything into the cartography table's blob is out — any
  vanilla player saving their map to the table would wipe our data, and the
  blob is already ~4 MB before compression.
