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
            "Wood", "Stone", "Iron", "HardWood", "Marble", "Ashstone", "Ancient", "Ice", "Timberwood",
            "Roof_Thatch", "Roof_Darkwood", "Roof_Turf", "Roof_Slate"
        };
        // WearNTear.MaterialType has these many values; the entries after them are roof kinds
        // recognised by the prefab name (all roofs are Wood / HardWood to the game)
        private const int GameMatCount = 9;
        private const byte MatRoofThatch = 9, MatRoofDarkwood = 10, MatRoofTurf = 11, MatRoofSlate = 12;

        private void BindConfig()
        {
            // Player settings first (1. Map layers, 3. Colours), the technical ones marked advanced.
            // --- 01 General
            _cfgEnabled = Config.Bind(SecGeneral, "Enabled", true,
                Ui("Living Map on or off. Off: the vanilla map, as if the mod were not there.", UiLayers, "Living Map on", 100, false));
            _cfgPersist = Config.Bind(SecGeneral, "SaveOverlay", true,
                Ui("Remember what was collected between sessions, in a file per world. Matters on a dedicated server, where you only get what the server has sent you; the host collects everything again within seconds anyway.", UiAdvanced, "Remember between sessions", 100, true));

            // --- 02 Layers
            _cfgShowBuildings = Config.Bind(SecLayers, "ShowBuildings", true,
                Ui("Show buildings on the map.", UiLayers, "Buildings", 90, false));
            _cfgShowPaths = Config.Bind(SecLayers, "ShowPaths", true,
                Ui("Show dirt paths, fields and paving made with the hoe and the cultivator.", UiLayers, "Paths and fields", 85, false));
            _cfgShowForest = Config.Bind(SecLayers, "ShowClearedForest", true,
                Ui("Remove the forest from the map where you have cut it down (the vanilla map always shows the forest the world started with).", UiLayers, "Cleared forest", 80, false));
            _cfgShowPlanted = Config.Bind(SecLayers, "ShowPlantedForest", true,
                Ui("Draw forest where you have planted a grove.", UiLayers, "Planted forest", 75, false));
            _cfgOnlyPlayerBuilt = Config.Bind(SecLayers, "OnlyPlayerBuilt", false,
                Ui("Only buildings placed by players: ruins, villages and other buildings that come with the world are left out.", UiLayers, "Only player buildings", 70, false));
            _cfgRespectFog = Config.Bind(SecLayers, "RespectFog", true,
                Ui("Only draw on ground you have explored. Off shows everything the mod knows about - on the host, the whole world.", UiLayers, "Only explored ground", 65, false));
            _cfgShowCleared = Config.Bind(SecLayers, "ShowClearedGround", false,
                Ui("Also mark ground that was only levelled or raised, not painted. Covers everything you have terraformed, so it paints wide areas.", UiLayers, "Levelled ground", 60, false));
            _cfgForestRadius = Config.Bind(SecLayers, "ClearedForestRadius", 12f,
                Ui("Metres around a map pixel checked for trees when deciding whether it is cleared forest. Larger ignores natural gaps between trees, smaller follows the edge of a clearing more closely.",
                    new AcceptableValueRange<float>(6f, 96f), UiAdvanced, "Cleared forest: search radius (m)", 60, true));
            _cfgForestMaxTrees = Config.Bind(SecLayers, "ClearedForestMaxTrees", 2,
                Ui("Trees that may still stand in that area for it to count as cleared. A natural wood has 6 to 40 there.",
                    new AcceptableValueRange<int>(0, 50), UiAdvanced, "Cleared forest: trees left", 59, true));
            _cfgPlantedMinTrees = Config.Bind(SecLayers, "PlantedForestMinTrees", 6,
                Ui("Trees a map pixel (12 x 12 m) must hold to be drawn as planted forest. A planted grove has 15 or more.",
                    new AcceptableValueRange<int>(1, 30), UiAdvanced, "Planted forest: trees needed", 58, true));
            _cfgPlantedAnyBiome = Config.Bind(SecLayers, "PlantedForestAnyBiome", false,
                Ui("Also draw planted forest where the vanilla map never draws forest (swamps, mountains, Mistlands, Ashlands). Their natural woods then get the forest pattern too.", UiAdvanced, "Planted forest in every biome", 57, true));

            // --- 03 Scanning
            _cfgScanSource = Config.Bind(SecScanning, "BuildingScanSource", "ZDO",
                Ui("Where buildings are read from. ZDO: the game's object database - the whole world on the host, what the server has sent on a dedicated server. Physics: only what is loaded around you (the old way). ZDO falls back to Physics by itself if needed.",
                    new AcceptableValueList<string>("ZDO", "Physics"), UiAdvanced, "Read buildings from", 50, true));
            _cfgPathSource = Config.Bind(SecScanning, "PathScanSource", "ZDO",
                Ui("Where painted ground is read from. ZDO: the terrain records in the object database. Heightmap: the loaded terrain around you (the old way). ZDO needs buildings read from ZDO too.",
                    new AcceptableValueList<string>("ZDO", "Heightmap"), UiAdvanced, "Read paths from", 49, true));
            _cfgZdoInterval = Config.Bind(SecScanning, "ZdoScanInterval", 5f,
                Ui("Seconds between passes over the object database: how long a new or removed building takes to reach the map.",
                    new AcceptableValueRange<float>(1f, 120f), UiAdvanced, "Update every (s)", 48, true));

            // --- 04 Rendering
            _cfgMapScale = Config.Bind(SecRendering, "MapTextureScale", 2,
                Ui("How sharp buildings and paths are on the zoomed-out map: 1 = 12 m a pixel, 2 = 6 m (~67 MB of video memory), 4 = 3 m (~268 MB), 8 = 1.5 m (~1 GB). Close up, the detail layer draws them itself. Takes effect on the next world load.",
                    new AcceptableValueList<int>(1, 2, 4, 8), UiAdvanced, "Map layer resolution", 45, true));
            _cfgPieceSize = Config.Bind(SecRendering, "MinPieceSizeMeters", 2f,
                Ui("Smallest size a building piece is drawn at on the zoomed-out map, so thin walls stay visible.",
                    new AcceptableValueRange<float>(0.1f, 8f), UiAdvanced, "Smallest piece (m)", 44, true));
            _cfgOutline = Config.Bind(SecRendering, "BuildingOutline", true,
                Ui("A dark edge around buildings on the zoomed-out map, so a wooden house does not blend into the dirt it stands on.", UiAdvanced, "Building outline", 43, true));
            _cfgOutlineWidth = Config.Bind(SecRendering, "OutlineWidthMeters", 1f,
                Ui("How wide that edge is.", new AcceptableValueRange<float>(0.1f, 4f), UiAdvanced, "Outline width (m)", 42, true));
            _cfgTerrainGrid = Config.Bind(SecRendering, "TerrainGridSize", 2f,
                Ui("Metres per cell of the paths layer. Smaller is finer but takes four times the memory for half the size. Changing it collects the paths again.",
                    new AcceptableValueRange<float>(0.5f, 8f), UiAdvanced, "Paths cell size (m)", 41, true));

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
                new Color(0.70f, 0.45f, 0.30f, 1f),
                new Color(0.80f, 0.66f, 0.34f, 1f),     // thatch (straw)
                new Color(0.33f, 0.27f, 0.24f, 1f),     // darkwood (tar)
                new Color(0.42f, 0.55f, 0.26f, 1f),     // turf
                new Color(0.47f, 0.48f, 0.52f, 1f)      // grausten slate
            };
            string[] names =
            {
                "Wood", "Stone", "Iron", "Core wood", "Marble", "Ashstone", "Ancient", "Ice", "Timber",
                "Roof: thatch", "Roof: darkwood", "Roof: turf", "Roof: slate"
            };
            for (int i = 0; i < MatNames.Length; i++)
                _cfgMatColor[i] = Config.Bind(SecColors, "Material_" + MatNames[i], defaults[i],
                    Ui("Colour of " + names[i].ToLowerInvariant() + " on the map.", UiColours, names[i], 100 - i, false));
            _cfgMatUnknownColor = Config.Bind(SecColors, "Material_Unknown", new Color(0.85f, 0.30f, 0.75f, 1f),
                Ui("Colour of pieces whose material is not known.", UiColours, "Other pieces", 80, false));
            _cfgCultivatedColor = Config.Bind(SecColors, "Terrain_Cultivated", new Color(0.36f, 0.26f, 0.12f, 0.85f),
                Ui("Colour of cultivated soil.", UiColours, "Fields", 79, false));
            _cfgPavedColor = Config.Bind(SecColors, "Terrain_Paved", new Color(0.62f, 0.62f, 0.66f, 0.9f),
                Ui("Colour of paving.", UiColours, "Paving", 78, false));
            _cfgDirtColor = Config.Bind(SecColors, "Terrain_DirtPath", new Color(0.42f, 0.36f, 0.30f, 0.8f),
                Ui("Colour of dirt paths.", UiColours, "Dirt paths", 77, false));
            _cfgClearedColor = Config.Bind(SecColors, "Terrain_Cleared", new Color(0.50f, 0.47f, 0.36f, 0.5f),
                Ui("Colour of levelled ground (when it is shown).", UiColours, "Levelled ground", 76, false));
            _cfgOutlineColor = Config.Bind(SecColors, "Building_Outline", new Color(0.10f, 0.07f, 0.04f, 0.85f),
                Ui("Colour of the building outline on the zoomed-out map.", UiColours, "Building outline", 75, false));

            // --- 06 Advanced
            _cfgZdoPerFrame = Config.Bind(SecAdvanced, "ZdoObjectsPerFrame", 4000,
                Ui("Objects looked at per frame while reading the object database. Higher updates the map sooner, lower is smoother.",
                    new AcceptableValueRange<int>(500, 50000), UiAdvanced, "Objects per frame", 30, true));
            _cfgScanRadius = Config.Bind(SecAdvanced, "ScanRadius", 64f,
                Ui("Radius scanned around you by the old Physics / Heightmap readers. Unused with the default ZDO.",
                    new AcceptableValueRange<float>(16f, 256f), UiAdvanced, "Old scan radius (m)", 29, true));
            _cfgMaxPieces = Config.Bind(SecAdvanced, "MaxPieces", 300000,
                Ui("Most building pieces remembered (300 000 take about 7 MB).",
                    new AcceptableValueRange<int>(1000, 3000000), UiAdvanced, "Max pieces", 28, true));
            _cfgMaxTerrain = Config.Bind(SecAdvanced, "MaxTerrainCells", 500000,
                Ui("Most path cells remembered.", new AcceptableValueRange<int>(1000, 5000000), UiAdvanced, "Max path cells", 27, true));

            // --- 07 Debug
            _cfgDebug = Config.Bind(SecDebug, "Debug", false,
                Ui("Verbose log with timings, for bug reports.", UiDebug, "Debug log", 20, true));
            _cfgDebugMarker = Config.Bind(SecDebug, "DebugMarker", false,
                Ui("A magenta cross at your position, to check the layer lines up with the map.", UiDebug, "Position marker", 19, true));
            _cfgIncremental = Config.Bind(SecDebug, "IncrementalRedraw", true,
                Ui("Draw only what changed. Off redraws everything on each change - only for tracking down a drawing glitch.", UiDebug, "Draw changes only", 18, true));
            _cfgLinearFix = Config.Bind(SecDebug, "LinearColorFix", true,
                Ui("Colour conversion for the game's linear colour space. Off only to check whether colours are the problem.", UiDebug, "Colour conversion", 17, true));
            _cfgMapFlipY = Config.Bind(SecDebug, "MapLayerFlipY", false,
                Ui("Flip the layer vertically, for a graphics API that draws it upside down.", UiDebug, "Flip layer", 16, true));

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
