# LivingMap

Client-side map mod for Valheim. It draws onto the map texture itself, so everything
below is part of the map: it pans, zooms, hides under the fog of war and under every
marker, on the big map and the minimap alike.

![The big map: vanilla on the left, LivingMap on the right - the base, its paved circle, paths and the cleared forest around it](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/map-compare.png)

![The minimap: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/minimap-compare.png)

- **Buildings**, coloured by material (wood, stone, iron, marble, ...), with a dark
  outline so a house separates from the levelled ground it stands on.
- **Hoe-made paths**: dirt paths, paving and cultivated soil.
- **Forest that is gone**: where the vanilla map still shows woods but the trees have
  been cut, the forest pattern is erased. Where you planted a grove, it appears.
- Sharper map texture: 3 m per pixel by default (vanilla is 12 m), up to 1.5 m.

Everything is read from the game's object database rather than from what is loaded
around you. On the host (single player, or the player hosting) that is the entire
world at once, and whatever is torn down disappears from the map. On a client of a
dedicated server it is everything the server has sent you this session, remembered
between sessions in a file per world.

No Harmony patches, nothing sent over the network, no effect on players without the
mod. If anything fails, the mod switches that layer off and leaves the vanilla map as
it was.

## Settings

`BepInEx\config\j1ga.livingmap.cfg`, grouped into General, Layers, Scanning, Rendering,
Colors, Advanced and Debug. The ones worth knowing:

| Setting | Meaning |
|---|---|
| `MapTextureScale` | 1 / 2 / 4 / 8 = 12 / 6 / 3 / 1.5 m per pixel; 16 MB / 67 MB / 268 MB / 1 GB of video memory. Change needs a world re-enter. |
| `ShowBuildings`, `ShowPaths`, `ShowClearedForest`, `ShowPlantedForest` | The layers. |
| `RespectFog` | Only draw on explored ground. |
| `MinPieceSizeMeters` | Smallest size a build piece is drawn at, in metres. |
| `ClearedForestRadius`, `ClearedForestMaxTrees` | How empty an area must be to count as cleared forest. |
| `PlantedForestMinTrees` | Trees per 12 x 12 m map pixel to count as a planted grove. |
| `Debug` | Verbose log with timings. |

Colours are configurable per material and per path type.

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

## More mods by j1gA

| | Mod |
|---|---|
| [![StationSpeed](https://raw.githubusercontent.com/tbsj1ga/StationSpeedValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/) | **[StationSpeed](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/)** — Faster smelters, kilns, fermenters and crops — consistent even for players without the mod. |
| [![WeaponArts](https://raw.githubusercontent.com/tbsj1ga/WeaponArtsValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/) | **[WeaponArts](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/)** — One key, one active ability per weapon: stagger, taunt, heals, berserk, crits. |
| [![ExtendedBosses](https://raw.githubusercontent.com/tbsj1ga/ExtendedBossesValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/) | **[ExtendedBosses](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/)** — Raid-style boss fights: phases, adds, nests, shields, marks — built from vanilla parts. |
| [![HostOwner](https://raw.githubusercontent.com/tbsj1ga/HostOwnerValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/) | **[HostOwner](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/)** — The host takes ownership of stations and bosses near it, so its mods work for everyone. |

Source, documentation and the changelog: https://github.com/tbsj1ga/LivingMapValheim

*Developed with the help of an AI assistant (Claude by Anthropic); the design
decisions, verification against the game code and in-game testing are the
author's.*
