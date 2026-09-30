# LivingMap

**The vanilla map shows the world as it was generated. LivingMap shows the world as you've
changed it.**

Your bases, roads, fields and the forest you've cut down appear on the map and the minimap,
right where they are, and update as you keep building.

> ## 🆕 New in 0.16.0: the detailed map (experimental)
>
> Zoom the big map in and it is now **drawn in detail, in the vanilla map's own style**:
> buildings with their roofs, paths, fields and cleared forest. The closer you zoom, the
> finer it gets.
>
> It's **experimental** — if something looks wrong, please
> [report it](https://github.com/tbsj1ga/LivingMapValheim/issues). The first time you open
> the map, a short note asks whether to keep it on. You can turn it off any time in
> `BepInEx/config/j1ga.livingmap.cfg`, section `[08 Detail]`: `DetailEnabled = false`.

![Zooming in on a base: the map turns into the detailed picture step by step](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-zoom.webp)

![Close up: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-compare.png)

## What you'll see

- **Your buildings**, coloured by material — wood, stone, iron, marble… — and **roofs by
  their kind**: thatch, darkwood, turf, slate.
- **Roads and fields**: hoe paths, paving and farmland.
- **Cleared forest**: where you've chopped the trees down, the map stops showing forest.
  Plant a grove and it appears.
- Everything respects the fog of war and sits under your pins and markers, on the big map
  and the minimap.

![The big map: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/map-compare.png)

![The minimap: vanilla on the left, LivingMap on the right](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/minimap-compare.png)

## The detailed map, close up

- It looks like the vanilla map — the same paper, forest, water and light — just with
  more detail. Switching to it as you zoom in is seamless.
- **Buildings** keep their real shape and direction, with textures (planks, masonry,
  straw…), shaded edges and shadows. **Roof slopes** catch the sun, so you can see the
  shape of a roof.
- **Paths, paving and fields** get their own textures, soft at the edges.
- The **light follows the time of day** like the rest of the map, and the clouds drift over
  it.
- The **minimap and the zoomed-out map are not changed** by it.

![A base up close: roofs, walls, paving, paths and fields](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-base.png)

![A clearing cut into the forest](https://raw.githubusercontent.com/tbsj1ga/LivingMapValheim/main/docs/media/detail-forest.png)

**Performance.** It only works while the big map is open and zoomed in; closed, it costs
nothing. Until a part of the map is ready you see the normal map there, so a slower
computer shows the detail a little later rather than lagging. On a processor with 4
threads or fewer it uses one background thread and starts closer in.

## What it doesn't do

- It doesn't reveal unexplored areas: if you haven't explored it, you won't see it.
- Zoomed out, coastlines, mountains and biome colours stay exactly as in vanilla.

## Multiplayer

- Only you need it: nothing is sent to other players, and friends without the mod aren't
  affected. The server doesn't need it.
- As the host or in single player you see every building in the world at once.
- On a dedicated server you see what's around the places you've visited; the map remembers
  them between sessions.

## Settings

Everything works out of the box. With [ConfigurationManager](https://thunderstore.io/c/valheim/p/shudnal/ConfigurationManager/)
(F1 in game) the settings are grouped:

- **1. Map layers** — turn buildings, paths and fields, cleared and planted forest on or off.
- **2. Close-up detail** — the detailed map on or off, **how far out it starts** (a slider),
  clouds (smooth / exact / off), building textures.
- **3. Colours** — every material, roof kind and kind of ground.

The technical settings are marked *advanced* and hidden unless you tick "Show advanced
settings". Without ConfigurationManager, everything is in `BepInEx/config/j1ga.livingmap.cfg`.

## Compatibility

Tested with **Valheim 1.0.16**, **BepInEx 5.4.23.5** (BepInExPack_Valheim 5.4.2351). Mods
that replace or resize the map texture may clash with it.

## Bugs and feedback

GitHub Issues: https://github.com/tbsj1ga/LivingMapValheim/issues — please attach
`BepInEx/LogOutput.log`, and for the detailed map a screenshot and the output of the console
command `livingmap status` (F5).

## More mods by j1gA

| | Mod |
|---|---|
| [![StationSpeed](https://raw.githubusercontent.com/tbsj1ga/StationSpeedValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/) | **[StationSpeed](https://thunderstore.io/c/valheim/p/j1gA/StationSpeed/)** — Faster smelters, kilns, fermenters and crops — consistent even for players without the mod. |
| [![WeaponArts](https://raw.githubusercontent.com/tbsj1ga/WeaponArtsValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/) | **[WeaponArts](https://thunderstore.io/c/valheim/p/j1gA/WeaponArts/)** — One key, one active ability per weapon: stagger, taunt, heals, berserk, crits. |
| [![ExtendedBosses](https://raw.githubusercontent.com/tbsj1ga/ExtendedBossesValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/) | **[ExtendedBosses](https://thunderstore.io/c/valheim/p/j1gA/ExtendedBosses/)** — Raid-style boss fights: phases, adds, nests, shields, marks — built from vanilla parts. |
| [![HostOwner](https://raw.githubusercontent.com/tbsj1ga/HostOwnerValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/) | **[HostOwner](https://thunderstore.io/c/valheim/p/j1gA/HostOwner/)** — The host takes ownership of stations and bosses near it, so its mods work for everyone. |
| [![HudLayout](https://raw.githubusercontent.com/tbsj1ga/HudLayoutValheim/main/docs/media/icon-128.png)](https://thunderstore.io/c/valheim/p/j1gA/HudLayout/) | **[HudLayout](https://thunderstore.io/c/valheim/p/j1gA/HudLayout/)** — Move, resize and restyle your HUD with the mouse: bars, food, hotbar, minimap, even other mods' HUD. |

Source, full documentation and the changelog: https://github.com/tbsj1ga/LivingMapValheim

*Developed with the help of an AI assistant (Claude by Anthropic); the design decisions,
verification against the game code and in-game testing are the author's.*
