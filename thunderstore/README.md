# LivingMap

**The vanilla map shows the world as it was generated. LivingMap shows the world as you've
changed it.**

Your bases, roads, fields and the forest you've cut down appear on the map and the minimap,
right where they are, and update as you keep building.

![The big map: vanilla on the left, LivingMap on the right - the base, its paved circle, paths and the cleared forest around it](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/map-compare.png)

![The minimap: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/minimap-compare.png)

## What you'll see

- **Your buildings**, coloured by material: wood, stone, iron, marble…
- **Roads and fields**: hoe paths, paving and farmland.
- **Cleared forest**: where you've chopped the trees down, the map stops showing forest.
  Plant a grove and it appears.
- **Zoom in for detail**: below about 3 km across, a detailed picture fades in — shaded
  relief with the ground players levelled, water by depth, trees and rocks where they
  stand, buildings as rotated boxes, down to 1 m per pixel.
- Everything respects the fog of war and sits under your pins and markers, on the big map
  and the minimap.

## What it doesn't do

- It doesn't change how the world looks from afar: zoomed out, coastlines, mountains and
  biome colours stay exactly as in vanilla.
- It doesn't reveal unexplored areas: if you haven't explored it, you won't see it.

## Multiplayer

- Only you need it: nothing is sent to other players, and friends without the mod aren't
  affected. The server doesn't need it.
- As the host or in single player you see every building in the world at once.
- On a dedicated server you see what's around the places you've visited; the map remembers
  them between sessions.

## Settings you might want

Everything works out of the box. If you want to tweak it (in
`BepInEx/config/j1ga.livingmap.cfg`, or in game with ConfigurationManager, F1):

- **Each layer** — buildings, roads, cleared and planted forest — can be turned off.
- **Colours** of every material and road type.
- **`MapTextureScale`** — how crisp **buildings and roads** look (not the terrain):
  1 / 2 / 4 / 8. Higher values use more video memory (2 ≈ 70 MB, 4 ≈ 270 MB); with the detail layer 2 is plenty.
  Takes effect when you re-enter the world.
- **Detail layer** (section `08 Detail`): on/off, from how far in it appears, the finest level,
  relief strength, trees and buildings from the world.

## Compatibility

Tested with **Valheim 1.0.16**, **BepInEx 5.4.23.5** (BepInExPack_Valheim 5.4.2351). Mods
that replace or resize the map texture may clash with it — set `MapTextureScale` to 1 to
rule that out.

## Bugs and feedback

GitHub Issues: https://github.com/tbsj1ga/LivingMapValheim/issues — please attach
`BepInEx/LogOutput.log`.

## More mods by j1gA

| | Mod |
|---|---|
| [![StationSpeed](https://raw.githubusercontent.com/tbsj1ga/StationSpeedValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/) | **[StationSpeed](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/)** — Faster smelters, kilns, fermenters and crops — consistent even for players without the mod. |
| [![WeaponArts](https://raw.githubusercontent.com/tbsj1ga/WeaponArtsValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/) | **[WeaponArts](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/)** — One key, one active ability per weapon: stagger, taunt, heals, berserk, crits. |
| [![ExtendedBosses](https://raw.githubusercontent.com/tbsj1ga/ExtendedBossesValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/) | **[ExtendedBosses](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/)** — Raid-style boss fights: phases, adds, nests, shields, marks — built from vanilla parts. |
| [![HostOwner](https://raw.githubusercontent.com/tbsj1ga/HostOwnerValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/) | **[HostOwner](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/)** — The host takes ownership of stations and bosses near it, so its mods work for everyone. |

Source, full documentation and the changelog: https://github.com/tbsj1ga/LivingMapValheim

*Developed with the help of an AI assistant (Claude by Anthropic); the design decisions,
verification against the game code and in-game testing are the author's.*
