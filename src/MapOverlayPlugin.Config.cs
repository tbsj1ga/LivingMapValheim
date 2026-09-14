using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace MapOverlay
{
    public partial class MapOverlayPlugin
    {
        // ------------------------------------------------------------------
        // config
        // ------------------------------------------------------------------
        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<bool> _cfgDebug;
        private ConfigEntry<bool> _cfgDebugMarker;

        private ConfigEntry<bool> _cfgShowBuildings;
        private ConfigEntry<bool> _cfgShowPaths;
        private ConfigEntry<bool> _cfgRespectFog;
        private ConfigEntry<bool> _cfgPaintMapTexture;
        private ConfigEntry<int> _cfgMapScale;
        private ConfigEntry<bool> _cfgMapFlipY;
        private ConfigEntry<bool> _cfgLinearFix;
        private ConfigEntry<bool> _cfgOutline;
        private ConfigEntry<float> _cfgOutlineWidth;
        private ConfigEntry<Color> _cfgOutlineColor;
        private ConfigEntry<float> _cfgMapRebuildInterval;
        private ConfigEntry<bool> _cfgIncremental;
        private ConfigEntry<bool> _cfgHiRes;

        private ConfigEntry<string> _cfgScanSource;
        private ConfigEntry<string> _cfgPathSource;
        private ConfigEntry<float> _cfgZdoInterval;
        private ConfigEntry<int> _cfgZdoPerFrame;
        private ConfigEntry<float> _cfgScanRadius;
        private ConfigEntry<float> _cfgMoveDelta;
        private ConfigEntry<float> _cfgScanInterval;
        private ConfigEntry<float> _cfgIdleRescan;
        private ConfigEntry<float> _cfgTerrainGrid;

        private ConfigEntry<float> _cfgFlushInterval;
        private ConfigEntry<int> _cfgPixelsPerFlush;

        private ConfigEntry<int> _cfgHiResSize;
        private ConfigEntry<int> _cfgMinimapSize;
        private ConfigEntry<bool> _cfgMinimapOverlay;
        private ConfigEntry<float> _cfgOverlayOpacity;
        private ConfigEntry<bool> _cfgSmoothOverlay;
        private ConfigEntry<float> _cfgHiResInterval;
        private ConfigEntry<float> _cfgPieceSize;

        private ConfigEntry<bool> _cfgPersist;
        private ConfigEntry<float> _cfgSaveInterval;
        private ConfigEntry<int> _cfgMaxPieces;
        private ConfigEntry<int> _cfgMaxTerrain;
        private ConfigEntry<int> _cfgMaxColliders;

        private ConfigEntry<Color>[] _cfgMatColor;
        private ConfigEntry<Color> _cfgMatUnknownColor;
        private ConfigEntry<Color> _cfgCultivatedColor;
        private ConfigEntry<Color> _cfgPavedColor;
        private ConfigEntry<Color> _cfgDirtColor;
        private ConfigEntry<Color> _cfgClearedColor;
        private ConfigEntry<bool> _cfgShowCleared;
        private ConfigEntry<bool> _cfgShowForest;
        private ConfigEntry<float> _cfgForestRadius;
        private ConfigEntry<int> _cfgForestMaxTrees;
        private ConfigEntry<bool> _cfgShowPlanted;
        private ConfigEntry<int> _cfgPlantedMinTrees;
        private ConfigEntry<bool> _cfgPlantedAnyBiome;

        private static readonly string[] MatNames =
        {
            "Wood", "Stone", "Iron", "HardWood", "Marble", "Ashstone", "Ancient", "Ice", "Timberwood"
        };

        private void BindConfig()
        {
            _cfgEnabled = Config.Bind("01 General", "Enabled", true, "Master switch.");
            _cfgDebug = Config.Bind("01 General", "Debug", false, "Verbose logging, with timings of every scan and redraw, and a self-check of terrain records against the loaded terrain.");
            _cfgDebugMarker = Config.Bind("01 General", "DebugMarker", false,
                "Draw a magenta cross at your own position in the detailed overlay. Use it once to confirm the overlay lines up with the vanilla player arrow.");

            _cfgShowBuildings = Config.Bind("02 Layers", "ShowBuildings", true, "Show build pieces.");
            _cfgShowPaths = Config.Bind("02 Layers", "ShowPaths", true,
                "Show ground painted with the hoe: dirt paths, cultivated soil and paving.");
            _cfgShowCleared = Config.Bind("02 Layers", "ShowClearedGround", false,
                "Also mark ground that was merely levelled or raised, with no paint applied. This covers everything you have terraformed, so it paints wide areas.");
            _cfgShowForest = Config.Bind("02 Layers", "ShowClearedForest", true,
                "Erase the map's forest pattern where the trees are gone. The vanilla forest layer comes from the world generator and never changes, so a clear-cut around your base still shows as woods; this compares it with the trees that actually exist in the object database. On the host every generated zone is checked; on a client of a dedicated server only zones you have been near this session, remembered between sessions. Clearings the game itself makes around locations show up too. Needs the GPU map layer and the ZDO scan source.");
            _cfgForestRadius = Config.Bind("03 Scanning", "ClearedForestRadius", 12f,
                new ConfigDescription("Metres around a map pixel that are checked for trees when deciding whether the pixel is cleared forest. 12 = the pixel and its neighbours, a 36 x 36 m window. Larger ignores natural gaps between trees, smaller follows the edge of a clearing more closely.",
                    new AcceptableValueRange<float>(6f, 96f)));
            _cfgForestMaxTrees = Config.Bind("03 Scanning", "ClearedForestMaxTrees", 2,
                new ConfigDescription("Trees allowed inside that window for the pixel to still count as cleared. A natural wood has 6 to 40 in a 36 x 36 m window, a clearing with a couple of trees left standing has 1 or 2.",
                    new AcceptableValueRange<int>(0, 50)));
            _cfgShowPlanted = Config.Bind("02 Layers", "ShowPlantedForest", true,
                "Draw the map's own forest pattern where trees stand but the map shows none: a grove you planted, or woods the generator's mask missed. It is the vanilla pattern, so it looks like any other forest. Limited to the biomes where the vanilla map draws forest at all (Meadows, Black Forest, Plains) unless PlantedForestAnyBiome is on. Same requirements as ShowClearedForest.");
            _cfgPlantedMinTrees = Config.Bind("03 Scanning", "PlantedForestMinTrees", 6,
                new ConfigDescription("Trees a map pixel (12 x 12 m at vanilla map resolution) must hold to be drawn as forest. A planted grove has 15 or more; the edge of a natural wood that the mask cuts off mid-pixel has up to about 5.",
                    new AcceptableValueRange<int>(1, 30)));
            _cfgPlantedAnyBiome = Config.Bind("02 Layers", "PlantedForestAnyBiome", false,
                "Draw forest in biomes where the vanilla map never does: swamps, mountains, Mistlands, Ashlands. Their natural woods then get the forest pattern as well, which changes the look of the whole map.");
            _cfgRespectFog = Config.Bind("02 Layers", "RespectFog", true, "Only draw on explored ground.");
            _cfgPaintMapTexture = Config.Bind("02 Layers", "PaintMapTexture", true,
                "Draw straight into the map texture, so the result IS the map: it pans, zooms, fogs and layers under every marker exactly like vanilla, on both the big map and the minimap.");
            _cfgMapScale = Config.Bind("02 Layers", "MapTextureScale", 4,
                new ConfigDescription(
                    "Resolution multiplier for the map texture. Vanilla 2048 is 12 m per pixel. 2 = 4096 = 6 m/px (~67 MB of video memory), 4 = 8192 = 3 m/px (~268 MB), 8 = 16384 = 1.5 m/px (~1 GB, only worth it on a card with plenty of VRAM). Video memory only, nothing is held in RAM. If the card refuses the size the mod steps down automatically.",
                    new AcceptableValueList<int>(1, 2, 4, 8)));
            _cfgLinearFix = Config.Bind("02 Layers", "LinearColorFix", true,
                "Convert overlay colours to linear before drawing on the GPU. Needed when the game renders in linear colour space, otherwise everything comes out pale and washed out. Turn it off if the colours look too dark instead.");
            _cfgMapRebuildInterval = Config.Bind("02 Layers", "MapRebuildInterval", 0.5f,
                new ConfigDescription("Minimum seconds between map-texture updates after something changes.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            _cfgIncremental = Config.Bind("02 Layers", "IncrementalRedraw", true,
                "Draw new shapes on top of the existing map texture instead of rebuilding it from scratch; a full rebuild happens only when something disappeared. Off = always rebuild the whole texture, as before 0.11.0.");
            _cfgMapFlipY = Config.Bind("02 Layers", "MapLayerFlipY", false,
                "Flip the drawn shapes vertically. Only needed if the graphics API renders the map layer upside down - set it once and the whole layer lines up.");
            _cfgHiRes = Config.Bind("02 Layers", "DetailedOverlay", false,
                "Extra layer drawn on top of the map in screen space. It is the sharpest option when zoomed right in, but it is a separate layer rather than part of the map. Off by default.");

            _cfgScanSource = Config.Bind("03 Scanning", "BuildingScanSource", "ZDO",
                new ConfigDescription(
                    "Where build pieces are read from. ZDO walks the game's object database: on the host (single player, or the player hosting the game) that is every piece in the whole world at once, and pieces that were torn down disappear from the map; on a client of a dedicated server it is everything the server has sent this session. Physics is the old way: only colliders in the loaded zones around you. ZDO falls back to Physics on its own if the database cannot be read.",
                    new AcceptableValueList<string>("ZDO", "Physics")));
            _cfgZdoInterval = Config.Bind("03 Scanning", "ZdoScanInterval", 5f,
                new ConfigDescription("Seconds between full passes over the object database.",
                    new AcceptableValueRange<float>(1f, 120f)));
            _cfgPathSource = Config.Bind("03 Scanning", "PathScanSource", "ZDO",
                new ConfigDescription(
                    "Where hoe-painted ground is read from. ZDO reads each zone's terrain record from the object database (the same _TerrainCompiler data the game saves): on the host every modified zone of the world at once, on a client of a dedicated server the zones the server has sent this session. Only records whose revision changed are re-read. Heightmap is the old way: sampling the loaded terrain around you within ScanRadius. ZDO needs BuildingScanSource = ZDO and falls back to Heightmap on its own if the records cannot be read.",
                    new AcceptableValueList<string>("ZDO", "Heightmap")));
            _cfgZdoPerFrame = Config.Bind("03 Scanning", "ZdoObjectsPerFrame", 4000,
                new ConfigDescription("Objects examined per frame during a pass. A world of 70 000 objects takes about 18 frames at the default; raise it if you would rather have the map update sooner than smoother.",
                    new AcceptableValueRange<int>(500, 50000)));
            _cfgScanRadius = Config.Bind("03 Scanning", "ScanRadius", 64f,
                new ConfigDescription("Radius in metres scanned around the player: always for the path layer, and for build pieces only with BuildingScanSource = Physics. Objects exist only while their zone is loaded, so much above ~128 gains nothing.",
                    new AcceptableValueRange<float>(16f, 256f)));
            _cfgMoveDelta = Config.Bind("03 Scanning", "MoveDelta", 8f,
                new ConfigDescription("Metres to move before the next scan.", new AcceptableValueRange<float>(1f, 64f)));
            _cfgScanInterval = Config.Bind("03 Scanning", "ScanInterval", 1f,
                new ConfigDescription("Minimum seconds between scans.", new AcceptableValueRange<float>(0.1f, 30f)));
            _cfgIdleRescan = Config.Bind("03 Scanning", "IdleRescanInterval", 10f,
                new ConfigDescription("Rescan after this long even when standing still.", new AcceptableValueRange<float>(1f, 300f)));
            _cfgTerrainGrid = Config.Bind("03 Scanning", "TerrainGridSize", 2f,
                new ConfigDescription("Metres per terrain sample. Smaller is sharper but costs more per scan.",
                    new AcceptableValueRange<float>(0.5f, 8f)));

            _cfgFlushInterval = Config.Bind("04 Rendering", "FlushInterval", 0.5f,
                new ConfigDescription("Minimum seconds between map-texture uploads.", new AcceptableValueRange<float>(0.1f, 10f)));
            _cfgPixelsPerFlush = Config.Bind("04 Rendering", "PixelsPerFlush", 2000,
                new ConfigDescription("Maximum coarse pixels written per upload.", new AcceptableValueRange<int>(64, 100000)));
            _cfgMinimapOverlay = Config.Bind("02 Layers", "MinimapOverlay", true,
                "Draw the detailed overlay on the small minimap as well.");
            _cfgOverlayOpacity = Config.Bind("04 Rendering", "OverlayOpacity", 0.85f,
                new ConfigDescription("Opacity of the detailed overlay. Lower values let the map show through so it reads as part of the map rather than a sticker.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
            _cfgSmoothOverlay = Config.Bind("04 Rendering", "SmoothOverlay", true,
                "Filter the overlay smoothly instead of hard pixels, which blends better with the blurry map.");
            _cfgMinimapSize = Config.Bind("04 Rendering", "MinimapOverlayResolution", 512,
                new ConfigDescription("Pixel size of the minimap overlay texture. The minimap redraws constantly, so keep this small.",
                    new AcceptableValueList<int>(256, 512, 1024)));
            _cfgHiResSize = Config.Bind("04 Rendering", "DetailedOverlayResolution", 1024,
                new ConfigDescription("Pixel size of the detailed overlay texture. 2048 is sharper and uses 16 MB instead of 4 MB.",
                    new AcceptableValueList<int>(512, 1024, 1536, 2048)));
            _cfgHiResInterval = Config.Bind("04 Rendering", "DetailedOverlayInterval", 0.05f,
                new ConfigDescription("Minimum seconds between redraws of the detailed overlay. 0.05 keeps it glued to the map while dragging.",
                    new AcceptableValueRange<float>(0.02f, 2f)));
            _cfgPieceSize = Config.Bind("04 Rendering", "MinPieceSizeMeters", 2f,
                new ConfigDescription("Smallest size, in metres, that a build piece is drawn at. Pieces use their real footprint; this is the floor so thin walls stay visible however sharp the map texture is. Raise it if buildings read as too faint.",
                    new AcceptableValueRange<float>(0.1f, 8f)));
            _cfgOutline = Config.Bind("04 Rendering", "BuildingOutline", true,
                "Draw a dark halo behind every build piece. Without it a wooden building blends into the dirt path it usually stands on.");
            _cfgOutlineWidth = Config.Bind("04 Rendering", "OutlineWidthMeters", 1f,
                new ConfigDescription("How far the halo extends past a piece, in metres.",
                    new AcceptableValueRange<float>(0.1f, 4f)));
            _cfgOutlineColor = Config.Bind("06 Colors", "BuildingOutline", new Color(0.10f, 0.07f, 0.04f, 0.85f),
                "Colour of the halo drawn behind build pieces.");

            _cfgPersist = Config.Bind("05 Storage", "SaveOverlay", true, "Remember discoveries between sessions.");
            _cfgSaveInterval = Config.Bind("05 Storage", "SaveInterval", 60f,
                new ConfigDescription("Seconds between background saves.", new AcceptableValueRange<float>(10f, 600f)));
            _cfgMaxPieces = Config.Bind("05 Storage", "MaxPieces", 300000,
                new ConfigDescription("Hard cap on stored build pieces.", new AcceptableValueRange<int>(1000, 3000000)));
            _cfgMaxTerrain = Config.Bind("05 Storage", "MaxTerrainCells", 500000,
                new ConfigDescription("Hard cap on stored terrain samples.", new AcceptableValueRange<int>(1000, 5000000)));
            _cfgMaxColliders = Config.Bind("05 Storage", "MaxColliders", 8192,
                new ConfigDescription("Physics query buffer size.", new AcceptableValueRange<int>(256, 65536)));

            _cfgMatColor = new ConfigEntry<Color>[MatNames.Length];
            Color[] defaults =
            {
                new Color(0.95f, 0.55f, 0.15f, 1f),
                new Color(0.72f, 0.74f, 0.80f, 1f),
                new Color(0.40f, 0.44f, 0.52f, 1f),
                new Color(0.55f, 0.34f, 0.16f, 1f),
                new Color(0.90f, 0.87f, 0.80f, 1f),
                new Color(0.45f, 0.20f, 0.20f, 1f),
                new Color(0.36f, 0.52f, 0.36f, 1f),
                new Color(0.65f, 0.85f, 0.95f, 1f),
                new Color(0.70f, 0.45f, 0.30f, 1f)
            };
            for (int i = 0; i < MatNames.Length; i++)
                _cfgMatColor[i] = Config.Bind("06 Colors", "Material_" + MatNames[i], defaults[i],
                    "Colour for " + MatNames[i] + " pieces.");
            _cfgMatUnknownColor = Config.Bind("06 Colors", "Material_Unknown", new Color(0.85f, 0.30f, 0.75f, 1f),
                "Colour for pieces with no material information.");
            _cfgCultivatedColor = Config.Bind("06 Colors", "Terrain_Cultivated", new Color(0.36f, 0.26f, 0.12f, 0.85f),
                "Colour for cultivated soil (the cultivator).");
            _cfgPavedColor = Config.Bind("06 Colors", "Terrain_Paved", new Color(0.62f, 0.62f, 0.66f, 0.9f),
                "Colour for paved ground (the hoe's paving).");
            _cfgDirtColor = Config.Bind("06 Colors", "Terrain_DirtPath", new Color(0.42f, 0.36f, 0.30f, 0.8f),
                "Colour for dirt paths (the hoe's path tool). Kept deliberately dull so buildings stand out against it.");
            _cfgClearedColor = Config.Bind("06 Colors", "Terrain_Cleared", new Color(0.50f, 0.47f, 0.36f, 0.5f),
                "Colour for levelled ground with no paint, when ShowClearedGround is on.");
        }
    }
}
