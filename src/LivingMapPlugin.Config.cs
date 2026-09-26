using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace LivingMap
{
    public partial class LivingMapPlugin
    {
        // ------------------------------------------------------------------
        // config
        // ------------------------------------------------------------------
        // 01 General
        private ConfigEntry<bool> _cfgEnabled;
        private ConfigEntry<bool> _cfgPersist;

        // 02 Layers: what is drawn, each switch followed by its own thresholds
        private ConfigEntry<bool> _cfgShowBuildings;
        private ConfigEntry<bool> _cfgOnlyPlayerBuilt;
        private ConfigEntry<bool> _cfgShowPaths;
        private ConfigEntry<bool> _cfgShowCleared;
        private ConfigEntry<bool> _cfgShowForest;
        private ConfigEntry<float> _cfgForestRadius;
        private ConfigEntry<int> _cfgForestMaxTrees;
        private ConfigEntry<bool> _cfgShowPlanted;
        private ConfigEntry<int> _cfgPlantedMinTrees;
        private ConfigEntry<bool> _cfgPlantedAnyBiome;
        private ConfigEntry<bool> _cfgRespectFog;

        // 03 Scanning: where the data comes from and how often
        private ConfigEntry<string> _cfgScanSource;
        private ConfigEntry<string> _cfgPathSource;
        private ConfigEntry<float> _cfgZdoInterval;

        // 04 Rendering: how it is drawn
        private ConfigEntry<int> _cfgMapScale;
        private ConfigEntry<float> _cfgPieceSize;
        private ConfigEntry<bool> _cfgOutline;
        private ConfigEntry<float> _cfgOutlineWidth;
        private ConfigEntry<float> _cfgTerrainGrid;

        // 05 Colors
        private ConfigEntry<Color>[] _cfgMatColor;
        private ConfigEntry<Color> _cfgMatUnknownColor;
        private ConfigEntry<Color> _cfgCultivatedColor;
        private ConfigEntry<Color> _cfgPavedColor;
        private ConfigEntry<Color> _cfgDirtColor;
        private ConfigEntry<Color> _cfgClearedColor;
        private ConfigEntry<Color> _cfgOutlineColor;

        // 06 Advanced: budgets and caps nobody needs to touch
        private ConfigEntry<int> _cfgZdoPerFrame;
        private ConfigEntry<float> _cfgScanRadius;
        private ConfigEntry<int> _cfgMaxPieces;
        private ConfigEntry<int> _cfgMaxTerrain;

        // 07 Debug: logging and the escape hatches for diagnosing drawing problems
        private ConfigEntry<bool> _cfgDebug;
        private ConfigEntry<bool> _cfgDebugMarker;
        private ConfigEntry<bool> _cfgIncremental;
        private ConfigEntry<bool> _cfgLinearFix;
        private ConfigEntry<bool> _cfgMapFlipY;

        private const string SecGeneral = "01 General";
        private const string SecLayers = "02 Layers";
        private const string SecScanning = "03 Scanning";
        private const string SecRendering = "04 Rendering";
        private const string SecColors = "05 Colors";
        private const string SecAdvanced = "06 Advanced";
        private const string SecDebug = "07 Debug";
        // "08 Detail" lives in LivingMapPlugin.Detail.cs

        private static readonly string[] MatNames =
        {
            "Wood", "Stone", "Iron", "HardWood", "Marble", "Ashstone", "Ancient", "Ice", "Timberwood"
        };

        private void BindConfig()
        {
            // --- 01 General
            _cfgEnabled = Config.Bind(SecGeneral, "Enabled", true, "Master switch.");
            _cfgPersist = Config.Bind(SecGeneral, "SaveOverlay", true,
                "Remember what was collected between sessions, in a file per world. The host collects everything again within seconds anyway; this matters on a client of a dedicated server, which only ever gets what the server has sent it.");

            // --- 02 Layers
            _cfgShowBuildings = Config.Bind(SecLayers, "ShowBuildings", true, "Show build pieces.");
            _cfgOnlyPlayerBuilt = Config.Bind(SecLayers, "OnlyPlayerBuilt", false,
                "Show only pieces placed by players. Pieces that came with a location - ruins, draugr villages, dvergr outposts, stone circles - carry no creator and are skipped. Switching it drops the stored pieces and collects them again: the host has them all within seconds, a client of a dedicated server gets pieces from earlier sessions back only by passing by them.");
            _cfgShowPaths = Config.Bind(SecLayers, "ShowPaths", true,
                "Show ground painted with the hoe: dirt paths, cultivated soil and paving.");
            _cfgShowCleared = Config.Bind(SecLayers, "ShowClearedGround", false,
                "Also mark ground that was merely levelled or raised, with no paint applied. This covers everything you have terraformed, so it paints wide areas.");
            _cfgShowForest = Config.Bind(SecLayers, "ShowClearedForest", true,
                "Erase the map's forest pattern where the trees are gone. The vanilla forest layer comes from the world generator and never changes, so a clear-cut around your base still shows as woods; this compares it with the trees that actually exist in the object database. On the host every generated zone is checked; on a client of a dedicated server only zones you have been near this session, remembered between sessions. Clearings the game itself makes around locations show up too. Needs BuildingScanSource = ZDO.");
            _cfgForestRadius = Config.Bind(SecLayers, "ClearedForestRadius", 12f,
                new ConfigDescription("Metres around a map pixel that are checked for trees when deciding whether the pixel is cleared forest. 12 = the pixel and its neighbours, a 36 x 36 m window. Larger ignores natural gaps between trees, smaller follows the edge of a clearing more closely.",
                    new AcceptableValueRange<float>(6f, 96f)));
            _cfgForestMaxTrees = Config.Bind(SecLayers, "ClearedForestMaxTrees", 2,
                new ConfigDescription("Trees allowed inside that window for the pixel to still count as cleared. A natural wood has 6 to 40 in a 36 x 36 m window, a clearing with a couple of trees left standing has 1 or 2.",
                    new AcceptableValueRange<int>(0, 50)));
            _cfgShowPlanted = Config.Bind(SecLayers, "ShowPlantedForest", true,
                "Draw the map's own forest pattern where trees stand but the map shows none: a grove you planted, or woods the generator's mask missed. It is the vanilla pattern, so it looks like any other forest. Limited to the biomes where the vanilla map draws forest at all (Meadows, Black Forest, Plains) unless PlantedForestAnyBiome is on. Needs BuildingScanSource = ZDO.");
            _cfgPlantedMinTrees = Config.Bind(SecLayers, "PlantedForestMinTrees", 6,
                new ConfigDescription("Trees a map pixel (12 x 12 m at vanilla map resolution) must hold to be drawn as forest. A planted grove has 15 or more; the edge of a natural wood that the mask cuts off mid-pixel has up to about 5.",
                    new AcceptableValueRange<int>(1, 30)));
            _cfgPlantedAnyBiome = Config.Bind(SecLayers, "PlantedForestAnyBiome", false,
                "Draw forest in biomes where the vanilla map never does: swamps, mountains, Mistlands, Ashlands. Their natural woods then get the forest pattern as well, which changes the look of the whole map.");
            _cfgRespectFog = Config.Bind(SecLayers, "RespectFog", true,
                "Only draw on explored ground. Off shows everything the mod knows about, which on the host is the whole world.");

            // --- 03 Scanning
            _cfgScanSource = Config.Bind(SecScanning, "BuildingScanSource", "ZDO",
                new ConfigDescription(
                    "Where build pieces are read from. ZDO walks the game's object database: on the host (single player, or the player hosting the game) that is every piece in the whole world at once, and pieces that were torn down disappear from the map; on a client of a dedicated server it is everything the server has sent this session. Physics is the old way: only colliders in the loaded zones around you, within ScanRadius. ZDO falls back to Physics on its own if the database cannot be read.",
                    new AcceptableValueList<string>("ZDO", "Physics")));
            _cfgPathSource = Config.Bind(SecScanning, "PathScanSource", "ZDO",
                new ConfigDescription(
                    "Where hoe-painted ground is read from. ZDO reads each zone's terrain record from the object database (the same _TerrainCompiler data the game saves): on the host every modified zone of the world at once, on a client of a dedicated server the zones the server has sent this session. Only records whose revision changed are re-read. Heightmap is the old way: sampling the loaded terrain around you within ScanRadius. ZDO needs BuildingScanSource = ZDO and falls back to Heightmap on its own if the records cannot be read.",
                    new AcceptableValueList<string>("ZDO", "Heightmap")));
            _cfgZdoInterval = Config.Bind(SecScanning, "ZdoScanInterval", 5f,
                new ConfigDescription("Seconds between full passes over the object database: how long a new or removed piece takes to reach the map.",
                    new AcceptableValueRange<float>(1f, 120f)));

            // --- 04 Rendering
            _cfgMapScale = Config.Bind(SecRendering, "MapTextureScale", 2,
                new ConfigDescription(
                    "Resolution multiplier for the map texture (the zoomed-out map and the minimap; close up, the detail layer in 08 Detail draws finer). Vanilla 2048 is 12 m per pixel. 2 = 4096 = 6 m/px (~67 MB of video memory), 4 = 8192 = 3 m/px (~268 MB), 8 = 16384 = 1.5 m/px (~1 GB, only worth it on a card with plenty of VRAM). Video memory only, nothing is held in RAM. If the card refuses the size the mod steps down automatically. Takes effect on the next world load.",
                    new AcceptableValueList<int>(1, 2, 4, 8)));
            _cfgPieceSize = Config.Bind(SecRendering, "MinPieceSizeMeters", 2f,
                new ConfigDescription("Smallest size, in metres, that a build piece is drawn at. Pieces use their real footprint; this is the floor so thin walls stay visible however sharp the map texture is. Raise it if buildings read as too faint.",
                    new AcceptableValueRange<float>(0.1f, 8f)));
            _cfgOutline = Config.Bind(SecRendering, "BuildingOutline", true,
                "Draw a dark halo behind every build piece. Without it a wooden building blends into the dirt path it usually stands on.");
            _cfgOutlineWidth = Config.Bind(SecRendering, "OutlineWidthMeters", 1f,
                new ConfigDescription("How far the halo extends past a piece, in metres.",
                    new AcceptableValueRange<float>(0.1f, 4f)));
            _cfgTerrainGrid = Config.Bind(SecRendering, "TerrainGridSize", 2f,
                new ConfigDescription("Metres per cell of the path layer. The terrain records hold 1 m; 2 is plenty up to MapTextureScale 4, at 8 the difference shows. Halving it means four times the cells in memory and in the file. Changing it drops the stored path cells: the host has them back within one pass, a client of a dedicated server as the server sends the zones again.",
                    new AcceptableValueRange<float>(0.5f, 8f)));

            // --- 05 Colors
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
                _cfgMatColor[i] = Config.Bind(SecColors, "Material_" + MatNames[i], defaults[i],
                    "Colour for " + MatNames[i] + " pieces.");
            _cfgMatUnknownColor = Config.Bind(SecColors, "Material_Unknown", new Color(0.85f, 0.30f, 0.75f, 1f),
                "Colour for pieces with no material information.");
            _cfgCultivatedColor = Config.Bind(SecColors, "Terrain_Cultivated", new Color(0.36f, 0.26f, 0.12f, 0.85f),
                "Colour for cultivated soil (the cultivator).");
            _cfgPavedColor = Config.Bind(SecColors, "Terrain_Paved", new Color(0.62f, 0.62f, 0.66f, 0.9f),
                "Colour for paved ground (the hoe's paving).");
            _cfgDirtColor = Config.Bind(SecColors, "Terrain_DirtPath", new Color(0.42f, 0.36f, 0.30f, 0.8f),
                "Colour for dirt paths (the hoe's path tool). Kept deliberately dull so buildings stand out against it.");
            _cfgClearedColor = Config.Bind(SecColors, "Terrain_Cleared", new Color(0.50f, 0.47f, 0.36f, 0.5f),
                "Colour for levelled ground with no paint, when ShowClearedGround is on.");
            _cfgOutlineColor = Config.Bind(SecColors, "Building_Outline", new Color(0.10f, 0.07f, 0.04f, 0.85f),
                "Colour of the halo drawn behind build pieces, when BuildingOutline is on.");

            // --- 06 Advanced
            _cfgZdoPerFrame = Config.Bind(SecAdvanced, "ZdoObjectsPerFrame", 4000,
                new ConfigDescription("Objects examined per frame during a pass over the object database. A world of 70 000 objects takes about 18 frames at the default; raise it if you would rather have the map update sooner than smoother.",
                    new AcceptableValueRange<int>(500, 50000)));
            _cfgScanRadius = Config.Bind(SecAdvanced, "ScanRadius", 64f,
                new ConfigDescription("Radius in metres scanned around the player by the old scanners: build pieces with BuildingScanSource = Physics, paths with PathScanSource = Heightmap. Unused with the default ZDO sources. Objects exist only while their zone is loaded, so much above ~128 gains nothing.",
                    new AcceptableValueRange<float>(16f, 256f)));
            _cfgMaxPieces = Config.Bind(SecAdvanced, "MaxPieces", 300000,
                new ConfigDescription("Hard cap on stored build pieces; beyond it new pieces are not recorded, with one warning in the log. 300 000 pieces take about 7 MB.",
                    new AcceptableValueRange<int>(1000, 3000000)));
            _cfgMaxTerrain = Config.Bind(SecAdvanced, "MaxTerrainCells", 500000,
                new ConfigDescription("Hard cap on stored path cells; beyond it new cells are not recorded, with one warning in the log.",
                    new AcceptableValueRange<int>(1000, 5000000)));

            // --- 07 Debug
            _cfgDebug = Config.Bind(SecDebug, "Debug", false,
                "Verbose logging, with timings of every scan and redraw, and a self-check of terrain records against the loaded terrain.");
            _cfgDebugMarker = Config.Bind(SecDebug, "DebugMarker", false,
                "Draw a magenta cross at your own position on the map. Use it once to confirm the layer lines up with the vanilla player arrow; while it is on, every update is a full rebuild.");
            _cfgIncremental = Config.Bind(SecDebug, "IncrementalRedraw", true,
                "Draw new shapes on top of the existing map texture; a full rebuild happens only when something disappeared, a layer was switched, or too much arrived at once. Off = rebuild the whole texture on every change, as before 0.11.0. Only for pinning down a drawing glitch.");
            _cfgLinearFix = Config.Bind(SecDebug, "LinearColorFix", true,
                "Convert the colours to linear before drawing on the GPU. The game renders in linear colour space, and the conversion is applied only then; without it everything comes out pale and washed out. Off only to check whether the colours are the problem.");
            _cfgMapFlipY = Config.Bind(SecDebug, "MapLayerFlipY", false,
                "Flip the drawn shapes vertically. Only needed if the graphics API renders the map layer upside down - check with DebugMarker, set it once and the whole layer lines up.");

            // --- 08 Detail
            BindDetailConfig();
        }

        // ------------------------------------------------------------------
        // settings that moved
        // ------------------------------------------------------------------
        // 0.15.0 regrouped the sections and dropped a few settings. BepInEx keeps every line of
        // the file it has no binding for as an "orphan" and writes it back on save, so without
        // this the moved settings would silently fall back to their defaults and the old lines
        // would sit in the file for good. A key keeps its name when it moves, so the match is
        // by key; the one rename is listed. Values already present under the new section win.
        // The dropped list also names the ten settings 0.14.0 removed, so their lines go too.
        private static readonly string[] DroppedSettings =
        {
            "MapRebuildInterval", "MoveDelta", "ScanInterval", "IdleRescanInterval", "SaveInterval", "MaxColliders",
            "PaintMapTexture", "DetailedOverlay", "MinimapOverlay", "FlushInterval", "PixelsPerFlush", "OverlayOpacity",
            "SmoothOverlay", "MinimapOverlayResolution", "DetailedOverlayResolution", "DetailedOverlayInterval"
        };

        private void MigrateConfigLayout(HashSet<ConfigDefinition> inFile)
        {
            Dictionary<ConfigDefinition, string> orphans = OrphanedConfigEntries();
            if (orphans == null || orphans.Count == 0) return;

            Dictionary<string, ConfigEntryBase> byKey = new Dictionary<string, ConfigEntryBase>();
            foreach (ConfigDefinition def in Config.Keys) byKey[def.Key] = Config[def];

            List<ConfigDefinition> done = new List<ConfigDefinition>();
            int carried = 0;
            bool save = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            try
            {
                foreach (KeyValuePair<ConfigDefinition, string> kv in orphans)
                {
                    string key = kv.Key.Key;
                    if (kv.Key.Section == "06 Colors" && key == "BuildingOutline") key = "Building_Outline";

                    ConfigEntryBase target;
                    if (!byKey.TryGetValue(key, out target))
                    {
                        if (Array.IndexOf(DroppedSettings, key) >= 0) done.Add(kv.Key);
                        continue;
                    }
                    done.Add(kv.Key);
                    if (inFile != null && inFile.Contains(target.Definition)) continue;
                    try { target.SetSerializedValue(kv.Value); carried++; }
                    catch (Exception e) { Logger.LogWarning("Could not carry the setting " + kv.Key.Section + "/" + kv.Key.Key + " over: " + e.Message); }
                }
                for (int i = 0; i < done.Count; i++) orphans.Remove(done[i]);
            }
            finally { Config.SaveOnConfigSet = save; }

            if (done.Count == 0) return;
            Config.Save();
            Logger.LogInfo("Settings file updated to the 0.15.0 layout: " + carried + " values moved to their new sections, " + (done.Count - carried) + " obsolete lines removed.");
        }

        // Before anything is bound, every line of the file is an orphan: this is what the file holds.
        private HashSet<ConfigDefinition> SettingsInFile()
        {
            Dictionary<ConfigDefinition, string> orphans = OrphanedConfigEntries();
            return orphans != null ? new HashSet<ConfigDefinition>(orphans.Keys) : null;
        }

        private Dictionary<ConfigDefinition, string> OrphanedConfigEntries()
        {
            try
            {
                PropertyInfo pi = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (pi != null) return pi.GetValue(Config, null) as Dictionary<ConfigDefinition, string>;
            }
            catch (Exception e) { Logger.LogWarning("Could not read the unbound settings: " + e.Message); }
            return null;
        }
    }
}
