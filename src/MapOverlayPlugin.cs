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
    [BepInPlugin(Guid, Name, Version)]
    public class MapOverlayPlugin : BaseUnityPlugin
    {
        public const string Guid = "j1ga.mapoverlay";
        public const string Name = "Map Overlay";
        public const string Version = "0.11.0";

        private const byte MatNone = 255;
        private const byte TerrainNone = 0;
        private const byte TerrainCultivated = 1;
        private const byte TerrainPaved = 2;
        private const byte TerrainDirt = 3;
        private const byte TerrainCleared = 4;

        private struct PieceRec
        {
            public float X0, Z0, X1, Z1;   // world axis-aligned footprint
            public float Y;
            public byte Mat;

            public float CX { get { return (X0 + X1) * 0.5f; } }
            public float CZ { get { return (Z0 + Z1) * 0.5f; } }
        }

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

        // ------------------------------------------------------------------
        // state
        // ------------------------------------------------------------------
        // buildings, bucketed by map-pixel index so we can cull by area quickly
        private readonly Dictionary<int, List<PieceRec>> _pieces = new Dictionary<int, List<PieceRec>>();
        private int _pieceCount;

        // terrain, on its own metric grid: key = (gx << 32) | gz
        private readonly Dictionary<long, byte> _terrain = new Dictionary<long, byte>();

        // coarse layer bookkeeping
        private readonly HashSet<int> _pending = new HashSet<int>();
        private readonly HashSet<int> _deferredByFog = new HashSet<int>();
        private readonly List<int> _pendingScratch = new List<int>();
        private readonly HashSet<int> _paintedPixels = new HashSet<int>();

        private readonly List<int> _circleScratch = new List<int>();
        private readonly Dictionary<int, List<PieceRec>> _scanBuckets = new Dictionary<int, List<PieceRec>>();
        private readonly List<long> _terrainScratch = new List<long>();

        private Minimap _mm;
        private Texture2D _vanillaTex;
        private Texture2D _ourTex;
        private Texture _origSmallTex;
        private Texture _origLargeTex;
        private int _texSize;
        private float _pixelSize;
        private bool _ready;

        // GPU map layer: the map texture itself, rendered on the graphics card
        private RenderTexture _rt;
        private Material _glMat;
        private bool _gpuUnavailable;
        private bool _rtDirty;                  // full rebuild wanted: something disappeared, or the texture was lost
        private bool _maskDirty;                // forest mask wanted, it is cheap and rebuilt whole
        // incremental GPU layer: shapes waiting to be drawn on top, and shapes the fog is holding back
        private readonly List<PieceRec> _addPieces = new List<PieceRec>();
        private readonly List<long> _addTerrain = new List<long>();
        private readonly List<PieceRec> _fogPieces = new List<PieceRec>();
        private readonly List<long> _fogTerrain = new List<long>();
        private readonly List<int> _fogForest = new List<int>();
        private readonly List<PieceRec> _groupScratch = new List<PieceRec>();
        private readonly List<PieceRec> _fillScratch = new List<PieceRec>();
        private int _rtIncrements;
        private bool _drawnBuildings, _drawnPaths, _drawnOutline;   // layer switches at the last full rebuild
        private const int MaxIncrementalPieces = 1500;    // beyond this a full rebuild is the cheaper option
        private float _nextRtRebuild;
        private int _rtScale = 1;
        private int _rtRebuilds;

        private Collider[] _colliderBuf;
        private int _pieceLayerMask;
        private readonly Color32[] _onePixel = new Color32[1];

        // ZDO scanner: the object database instead of the physics scene
        private struct PrefabInfo
        {
            public bool IsPiece;
            public bool IsTree;
            public byte Mat;
            public Vector3 Min, Max;      // prefab-local AABB of its "piece"-layer colliders
        }

        private FieldInfo _fiObjectsByID;
        private int _zdoPieceLayer = -1;
        private readonly Dictionary<int, PrefabInfo> _prefabCache = new Dictionary<int, PrefabInfo>();
        private readonly List<ZDO> _zdoSnapshot = new List<ZDO>();
        private readonly Dictionary<int, List<PieceRec>> _zdoBuckets = new Dictionary<int, List<PieceRec>>();
        private readonly Stack<List<PieceRec>> _listPool = new Stack<List<PieceRec>>();
        private readonly List<int> _removeScratch = new List<int>();
        private int _zdoCursor = -1;            // index into the snapshot, -1 = no pass running
        private float _nextZdoPass;
        private Vector3 _zdoPassOrigin, _zdoPrevOrigin;
        private bool _zdoHavePrevOrigin;
        private bool _zdoAuthoritative;
        private int _zdoPasses, _zdoPassFrames, _zdoErrorCount;
        private bool _zdoDisabled;
        private const int MaxZdoErrors = 10;

        // cleared forest: the vanilla mask says woods, the object database says no trees
        private FieldInfo _fiGeneratedZones;
        private BitArray _forestMask;              // vanilla forest pixels, read once from _MaskTex
        private BitArray _pixelTrusted;            // pixels whose zone we hold complete tree data for
        private byte[] _treeCount;                 // trees per vanilla map pixel, rebuilt every ZDO pass
        private readonly HashSet<int> _clearedForest = new HashSet<int>();
        private readonly HashSet<int> _trustedZones = new HashSet<int>();      // client: zones synced this session
        private readonly List<int> _trustedZoneScratch = new List<int>();
        private readonly List<int> _forestSample = new List<int>();   // debug: a few pixels added this pass
        private bool _forestDisabled;
        private int _forestCursor = -1;            // index into _trustedZoneScratch, -1 = not evaluating
        private bool _forestChanged;
        private int _forestAdded, _forestRemoved;
        private const int ForestZonesPerFrame = 150;
        private bool _zdoPassPieces;
        // planted forest: trees stand where the vanilla mask shows none
        private readonly HashSet<int> _plantedForest = new HashSet<int>();
        private readonly Dictionary<int, int> _outsideCounts = new Dictionary<int, int>();   // trees per pixel outside the vanilla forest, per pass
        private readonly Dictionary<int, bool> _forestBiome = new Dictionary<int, bool>();   // pixel -> the vanilla map draws forest in this biome at all
        private int _biomeLookupsThisFrame;
        private const int BiomeLookupsPerFrame = 600;
        private RenderTexture _rtMask;
        private Material _glMatMask;
        private Material _glMatMaskAdd;
        private Texture2D _vanillaMask;
        private Texture _origSmallMask, _origLargeMask;

        private Vector3 _lastScanPos;
        private float _nextScanTime, _lastScanTime, _nextFlushTime, _nextSaveTime, _nextRefogTime, _nextHiResTime;
        private const float RefogInterval = 15f;
        private const float MaxFootprint = 32f;

        private long _worldUid;
        private bool _storeChanged;

        // detailed overlays, one per map view
        private class Layer
        {
            public string Name;
            public GameObject Go;
            public RawImage Image;
            public Texture2D Tex;
            public Color32[] Buf;
            public Vector2 LastOrigin = new Vector2(float.MinValue, float.MinValue);
            public float LastScale = float.MinValue;
            public int LastDrawn = -1;
            public int Redraws;
        }

        private readonly Layer _layerLarge = new Layer { Name = "Map" };
        private readonly Layer _layerSmall = new Layer { Name = "Minimap" };
        private bool _hiResDisabled;
        private bool _announcedFirstDraw;

        private FieldInfo _fiExplored, _fiExploredOthers;
        private MethodInfo _miWorldToMapPoint, _miMapPointToLocalGuiPos;

        private int _errorCount, _terrainErrorCount;
        private bool _disabledByErrors, _terrainDisabled;
        private const int MaxErrors = 25;
        private const int MaxTerrainErrors = 10;
        private readonly HashSet<string> _loggedErrors = new HashSet<string>();

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------
        private void Awake()
        {
            try
            {
                BindConfig();
                _pieceLayerMask = LayerMask.GetMask("piece");
                _zdoPieceLayer = LayerMask.NameToLayer("piece");

                FieldInfo fiObjects = typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fiObjects != null && typeof(Dictionary<ZDOID, ZDO>).IsAssignableFrom(fiObjects.FieldType))
                    _fiObjectsByID = fiObjects;
                else
                    Logger.LogWarning("ZDOMan.m_objectsByID was not found or has an unexpected type; build pieces will be scanned through physics instead.");

                FieldInfo fiZones = typeof(ZoneSystem).GetField("m_generatedZones", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fiZones != null && typeof(HashSet<Vector2s>).IsAssignableFrom(fiZones.FieldType))
                    _fiGeneratedZones = fiZones;
                else
                    Logger.LogWarning("ZoneSystem.m_generatedZones was not found; the cleared-forest layer will only cover zones you have visited this session, even on the host.");

                _fiExplored = typeof(Minimap).GetField("m_explored", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _fiExploredOthers = typeof(Minimap).GetField("m_exploredOthers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _miWorldToMapPoint = typeof(Minimap).GetMethod("WorldToMapPoint", bf,
                    null, new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                _miMapPointToLocalGuiPos = typeof(Minimap).GetMethod("MapPointToLocalGuiPos", bf,
                    null, new[] { typeof(float), typeof(float), typeof(RawImage) }, null);

                if (_miWorldToMapPoint == null || _miMapPointToLocalGuiPos == null)
                {
                    _hiResDisabled = true;
                    Logger.LogWarning("Could not find the map projection methods, the detailed overlay is off.");
                }

                Logger.LogInfo(Name + " " + Version + " loaded. No Harmony patches are applied.");
            }
            catch (Exception e)
            {
                Logger.LogError("Awake failed, mod is inert: " + e);
                _disabledByErrors = true;
            }
        }

        private void BindConfig()
        {
            _cfgEnabled = Config.Bind("01 General", "Enabled", true, "Master switch.");
            _cfgDebug = Config.Bind("01 General", "Debug", false, "Verbose logging.");
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
            _cfgMapScale = Config.Bind("02 Layers", "MapTextureScale", 2,
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

        private void OnDestroy()
        {
            try
            {
                SaveStore();
                Teardown(true);
            }
            catch (Exception e)
            {
                Logger.LogWarning("OnDestroy: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // main loop
        // ------------------------------------------------------------------
        private void Update()
        {
            if (_disabledByErrors) return;
            try { Tick(); }
            catch (Exception e) { HandleError("Tick", e); }
        }

        private void Tick()
        {
            Minimap mm = Minimap.instance;

            if (mm == null || ZNet.instance == null)
            {
                if (_mm != null) { SaveStore(); Teardown(false); }
                return;
            }
            if (!_cfgEnabled.Value)
            {
                if (_mm != null) { SaveStore(); Teardown(true); }
                return;
            }
            if (Player.m_localPlayer == null) return;

            bool mapLayerMissing = _cfgPaintMapTexture.Value && _ourTex == null && _rt == null;
            bool forestToggled = _rt != null && ForestWanted != (_rtMask != null);
            if (!_ready || _mm != mm || mapLayerMissing || forestToggled || mm.m_mapTexture == null ||
                _texSize != mm.m_textureSize || _vanillaTex != mm.m_mapTexture)
            {
                if (!Setup(mm)) return;
            }

            float now = Time.realtimeSinceStartup;
            Vector3 pos = Player.m_localPlayer.transform.position;

            if (now >= _nextScanTime)
            {
                bool moved = (pos - _lastScanPos).sqrMagnitude >= _cfgMoveDelta.Value * _cfgMoveDelta.Value;
                bool idleDue = now >= _lastScanTime + _cfgIdleRescan.Value;
                if (moved || idleDue)
                {
                    _nextScanTime = now + _cfgScanInterval.Value;
                    _lastScanTime = now;
                    _lastScanPos = pos;
                    Scan(pos);
                }
                else _nextScanTime = now + 0.25f;
            }

            if (UseZdoScan && (_cfgShowBuildings.Value || _treeCount != null)) StepZdoScan(now, pos);

            if (now >= _nextRefogTime &&
                (_deferredByFog.Count > 0 || _fogPieces.Count > 0 || _fogTerrain.Count > 0 || _fogForest.Count > 0))
            {
                _nextRefogTime = now + RefogInterval;
                foreach (int idx in _deferredByFog) _pending.Add(idx);
                _deferredByFog.Clear();
                ReleaseFromFog();
            }

            if (_rt != null)
            {
                if (!_rt.IsCreated()) _rtDirty = true;      // contents lost, e.g. after alt-tab
                if (_rtMask != null && !_rtMask.IsCreated()) _maskDirty = true;
                if (_cfgShowBuildings.Value != _drawnBuildings || _cfgShowPaths.Value != _drawnPaths || _cfgOutline.Value != _drawnOutline)
                    _rtDirty = true;                        // a layer switch flipped: only a rebuild can honour it
                bool adds = _addPieces.Count > 0 || _addTerrain.Count > 0;
                if ((_rtDirty || adds || _maskDirty) && now >= _nextRtRebuild)
                {
                    _nextRtRebuild = now + _cfgMapRebuildInterval.Value;
                    if (_rtDirty || (adds && (!_cfgIncremental.Value || _cfgDebugMarker.Value || _addPieces.Count > MaxIncrementalPieces)))
                    {
                        _rtDirty = false;
                        RebuildGpuLayer();
                    }
                    else if (adds) DrawAdditions();

                    if (_maskDirty && _rt != null)
                    {
                        _maskDirty = false;
                        RebuildMask();
                    }
                }
            }
            else if (_cfgPaintMapTexture.Value && _ourTex != null && _pending.Count > 0 && now >= _nextFlushTime)
            {
                _nextFlushTime = now + _cfgFlushInterval.Value;
                Flush();
            }

            if (!_hiResDisabled && now >= _nextHiResTime)
            {
                _nextHiResTime = now + _cfgHiResInterval.Value;
                try { UpdateOverlays(); }
                catch (Exception e)
                {
                    HandleError("detailed overlay", e);
                    _hiResDisabled = true;
                    DestroyHiRes();
                    Logger.LogWarning("The detailed overlay failed and was switched off. The coarse layer keeps working.");
                }
            }

            if (_cfgPersist.Value && _storeChanged && now >= _nextSaveTime)
            {
                _nextSaveTime = now + _cfgSaveInterval.Value;
                SaveStore();
            }
        }

        // ------------------------------------------------------------------
        // setup / teardown
        // ------------------------------------------------------------------
        private bool Setup(Minimap mm)
        {
            if (_ready) SaveStore();
            Teardown(true);

            Texture2D vanilla = mm.m_mapTexture;
            if (vanilla == null || mm.m_textureSize <= 0 || mm.m_pixelSize <= 0f) return false;
            if (vanilla.width != mm.m_textureSize || vanilla.height != mm.m_textureSize) return false;

            _mm = mm;
            _vanillaTex = vanilla;
            _texSize = mm.m_textureSize;
            _pixelSize = mm.m_pixelSize;
            MapPixelSize = _pixelSize;
            _colliderBuf = new Collider[Mathf.Clamp(_cfgMaxColliders.Value, 256, 65536)];

            _worldUid = 0L;
            try { if (ZNet.instance != null) _worldUid = ZNet.instance.GetWorldUID(); }
            catch (Exception e) { HandleError("Setup/GetWorldUID", e); }

            _pieces.Clear();
            _terrain.Clear();
            _pieceCount = 0;
            _pending.Clear();
            _deferredByFog.Clear();
            _paintedPixels.Clear();
            _storeChanged = false;

            if (_cfgPersist.Value) LoadStore();

            ResetZdoPass();
            _zdoHavePrevOrigin = false;
            _nextZdoPass = 0f;
            InitForestLayer();

            if (_cfgPaintMapTexture.Value)
            {
                bool gpu = false;
                if (!_gpuUnavailable)
                {
                    try { gpu = InitGpuLayer(); }
                    catch (Exception e)
                    {
                        _gpuUnavailable = true;
                        ReleaseGpuLayer();
                        HandleError("GPU map layer", e);
                    }
                }

                if (gpu)
                {
                    ApplyTextureToMap(_rt, true);
                    if (_rtMask != null) ApplyMaskToMap(_rtMask, true);
                    else DropForestData();
                    PokeMinimap();
                    _rtDirty = true;
                    _maskDirty = _rtMask != null;
                    _nextRtRebuild = 0f;
                }
                else
                {
                    DropForestData();
                    // fall back to painting pixels on the CPU at vanilla resolution
                    Color32[] baseColors;
                    try { baseColors = vanilla.GetPixels32(); }
                    catch (Exception e) { HandleError("Setup/GetPixels32", e); return false; }

                    Texture2D copy = new Texture2D(vanilla.width, vanilla.height, TextureFormat.RGBA32, false);
                    copy.name = "MapOverlay_MapTexture";
                    copy.filterMode = vanilla.filterMode;
                    copy.wrapMode = vanilla.wrapMode;
                    copy.anisoLevel = vanilla.anisoLevel;
                    copy.SetPixels32(baseColors);
                    copy.Apply(false);
                    _ourTex = copy;

                    ApplyTextureToMap(_ourTex, true);
                    PokeMinimap();
                    QueueAllPixels();
                    Logger.LogWarning("Falling back to CPU map painting at vanilla resolution.");
                }
            }

            _lastScanPos = new Vector3(float.MinValue, 0f, float.MinValue);
            _nextScanTime = 0f; _lastScanTime = 0f; _nextFlushTime = 0f; _nextHiResTime = 0f;
            _nextSaveTime = Time.realtimeSinceStartup + _cfgSaveInterval.Value;
            _ready = true;

            Logger.LogInfo(string.Format(
                "Attached to map: vanilla {0}px at {1} m/px; map layer {2}; buildings via {3}; forest layers {4}; world={5}, restored pieces={6}, terrain cells={7}, forest pixels cleared={8} planted={9}",
                _texSize, _pixelSize.ToString("0.##"),
                _rt != null
                    ? _rt.width + "px on the GPU = " + (_pixelSize / _rtScale).ToString("0.##") + " m/px"
                    : (_ourTex != null ? "CPU " + _texSize + "px" : "off"),
                UseZdoScan ? "the object database (ZDO)" : "physics",
                _treeCount != null ? "on" : "off",
                _worldUid, _pieceCount, _terrain.Count, _clearedForest.Count, _plantedForest.Count));
            return true;
        }

        private void Teardown(bool restoreVanillaTexture)
        {
            if (restoreVanillaTexture && _mm != null)
            {
                try { RestoreOriginalTextures(); }
                catch (Exception e) { Logger.LogWarning("Could not restore the vanilla map texture: " + e.Message); }
            }

            DestroyHiRes();
            ReleaseGpuLayer();

            if (_ourTex != null)
            {
                try { UnityEngine.Object.Destroy(_ourTex); } catch { }
                _ourTex = null;
            }

            _mm = null; _vanillaTex = null; _origSmallTex = null; _origLargeTex = null;
            _vanillaMask = null; _origSmallMask = null; _origLargeMask = null;
            DropForestData(); _clearedForest.Clear(); _plantedForest.Clear(); _trustedZones.Clear(); _forestBiome.Clear();
            _colliderBuf = null; _texSize = 0; _pixelSize = 0f; _ready = false;

            _pieces.Clear(); _terrain.Clear(); _pieceCount = 0;
            _pending.Clear(); _deferredByFog.Clear(); _paintedPixels.Clear();
            _circleScratch.Clear(); _scanBuckets.Clear(); _terrainScratch.Clear(); _pendingScratch.Clear();
            ResetZdoPass(); _prefabCache.Clear();
        }

        private void ApplyTextureToMap(Texture tex, bool rememberOriginals)
        {
            if (_mm == null) return;
            if (_mm.m_mapImageSmall != null && _mm.m_mapImageSmall.material != null)
            {
                if (rememberOriginals) _origSmallTex = _mm.m_mapImageSmall.material.GetTexture("_MainTex");
                _mm.m_mapImageSmall.material.SetTexture("_MainTex", tex);
            }
            if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
            {
                if (rememberOriginals) _origLargeTex = _mm.m_mapImageLarge.material.GetTexture("_MainTex");
                _mm.m_mapImageLarge.material.SetTexture("_MainTex", tex);
            }
        }

        // the minimap keeps its own copy of the material state; a toggle makes it pick up new textures
        private void PokeMinimap()
        {
            if (_mm != null && _mm.m_smallRoot != null && _mm.m_smallRoot.activeSelf)
            {
                _mm.m_smallRoot.SetActive(false);
                _mm.m_smallRoot.SetActive(true);
            }
        }

        private void ApplyMaskToMap(Texture tex, bool rememberOriginals)
        {
            if (_mm == null) return;
            if (_mm.m_mapImageSmall != null && _mm.m_mapImageSmall.material != null)
            {
                if (rememberOriginals) _origSmallMask = _mm.m_mapImageSmall.material.GetTexture("_MaskTex");
                _mm.m_mapImageSmall.material.SetTexture("_MaskTex", tex);
            }
            if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
            {
                if (rememberOriginals) _origLargeMask = _mm.m_mapImageLarge.material.GetTexture("_MaskTex");
                _mm.m_mapImageLarge.material.SetTexture("_MaskTex", tex);
            }
        }

        private void RestoreOriginalTextures()
        {
            if (_mm == null) return;
            if (_mm.m_mapImageSmall != null && _mm.m_mapImageSmall.material != null)
            {
                _mm.m_mapImageSmall.material.SetTexture("_MainTex", _origSmallTex != null ? _origSmallTex : _vanillaTex);
                if (_origSmallMask != null) _mm.m_mapImageSmall.material.SetTexture("_MaskTex", _origSmallMask);
            }
            if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
            {
                _mm.m_mapImageLarge.material.SetTexture("_MainTex", _origLargeTex != null ? _origLargeTex : _vanillaTex);
                if (_origLargeMask != null) _mm.m_mapImageLarge.material.SetTexture("_MaskTex", _origLargeMask);
            }
            PokeMinimap();
        }

        // ------------------------------------------------------------------
        // scanning
        // ------------------------------------------------------------------
        private void Scan(Vector3 center)
        {
            bool changed = false;
            if (_cfgShowBuildings.Value && !UseZdoScan && ScanBuildings(center)) { changed = true; _rtDirty = true; }
            if (_cfgShowPaths.Value && !_terrainDisabled) changed |= ScanTerrain(center);
            if (changed) _storeChanged = true;
        }

        private bool ScanBuildings(Vector3 center)
        {
            float radius = _cfgScanRadius.Value;
            float reconRadius = radius * 0.9f;

            _scanBuckets.Clear();

            int count = Physics.OverlapSphereNonAlloc(center, radius, _colliderBuf, _pieceLayerMask);
            for (int i = 0; i < count; i++)
            {
                Collider col = _colliderBuf[i];
                if (col == null) continue;

                Piece piece = col.GetComponentInParent<Piece>();
                if (piece == null) continue;
                ZNetView nview = piece.GetComponentInParent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                Bounds b = col.bounds;
                Vector3 p = b.center;
                float dx = p.x - center.x, dz = p.z - center.z;
                if (dx * dx + dz * dz > reconRadius * reconRadius) continue;

                // a stray oversized collider must not smear across the map
                if (b.size.x > MaxFootprint || b.size.z > MaxFootprint) continue;

                int idx;
                if (!WorldToIndex(p, out idx)) continue;

                byte mat = MatNone;
                WearNTear wnt = piece.GetComponent<WearNTear>();
                if (wnt != null)
                {
                    int m = (int)wnt.m_materialType;
                    mat = (m >= 0 && m < MatNames.Length) ? (byte)m : MatNone;
                }

                PieceRec rec = new PieceRec();
                rec.X0 = b.min.x; rec.X1 = b.max.x;
                rec.Z0 = b.min.z; rec.Z1 = b.max.z;
                rec.Y = b.max.y; rec.Mat = mat;

                List<PieceRec> bucket;
                if (!_scanBuckets.TryGetValue(idx, out bucket))
                {
                    bucket = new List<PieceRec>();
                    _scanBuckets[idx] = bucket;
                }
                bucket.Add(rec);
            }

            // everything inside the trusted radius is rebuilt from this scan
            bool changed = false;
            CollectPixelsInCircle(center, reconRadius, _circleScratch);
            for (int i = 0; i < _circleScratch.Count; i++)
            {
                int idx = _circleScratch[i];
                List<PieceRec> fresh;
                _scanBuckets.TryGetValue(idx, out fresh);

                List<PieceRec> old;
                bool had = _pieces.TryGetValue(idx, out old);

                int oldN = had ? old.Count : 0;
                int newN = fresh != null ? fresh.Count : 0;

                if (oldN == 0 && newN == 0) continue;
                if (had && fresh != null && SameBucket(old, fresh)) continue;

                if (had) _pieceCount -= oldN;

                if (newN == 0)
                {
                    _pieces.Remove(idx);
                    _pending.Add(idx);
                    changed = true;
                    continue;
                }

                if (_pieceCount + newN > _cfgMaxPieces.Value)
                {
                    LogOnce("piececap", "Reached MaxPieces (" + _cfgMaxPieces.Value + "), no more build pieces are recorded.");
                    if (had) _pieceCount += oldN;
                    continue;
                }

                _pieces[idx] = fresh;
                _pieceCount += newN;
                _pending.Add(idx);
                changed = true;
            }

            return changed;
        }

        private bool ScanTerrain(Vector3 center)
        {
            List<Heightmap> maps;
            try { maps = Heightmap.GetAllHeightmaps(); }
            catch (Exception e) { TerrainError("GetAllHeightmaps", e); return false; }
            if (maps == null || maps.Count == 0) return false;

            float radius = _cfgScanRadius.Value * 0.9f;
            float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            int steps = Mathf.CeilToInt(radius / grid);
            float r2 = radius * radius;

            Heightmap cached = null;
            int samples = 0, painted = 0;
            bool changed = false;

            int gcx = Mathf.FloorToInt(center.x / grid);
            int gcz = Mathf.FloorToInt(center.z / grid);

            try
            {
                for (int gx = gcx - steps; gx <= gcx + steps; gx++)
                {
                    for (int gz = gcz - steps; gz <= gcz + steps; gz++)
                    {
                        float wx = (gx + 0.5f) * grid;
                        float wz = (gz + 0.5f) * grid;
                        float dx = wx - center.x, dz = wz - center.z;
                        if (dx * dx + dz * dz > r2) continue;

                        Vector3 p = new Vector3(wx, center.y, wz);
                        Heightmap hm = PickHeightmap(maps, p, ref cached);
                        if (hm == null) continue;

                        // vanilla queries the paint of point p at texel (p - 0.5)
                        Color mask = hm.GetPaintMask(new Vector3(wx - 0.5f, center.y, wz - 0.5f));
                        samples++;

                        // PaintType in the game is { Dirt = red, Cultivate = green, Paved = blue },
                        // and the alpha channel is the vegetation mask, never a paint type.
                        byte kind = TerrainNone;
                        if (mask.b > 0.5f) kind = TerrainPaved;
                        else if (mask.r > 0.5f) kind = TerrainDirt;
                        else if (mask.g > 0.5f) kind = TerrainCultivated;
                        else if (_cfgShowCleared.Value && mask.a < 0.5f) kind = TerrainCleared;

                        long key = ((long)gx << 32) | (uint)gz;
                        byte cur;
                        bool had = _terrain.TryGetValue(key, out cur);

                        if (kind == TerrainNone)
                        {
                            if (had) { _terrain.Remove(key); changed = true; _rtDirty = true; MarkPixelDirty(wx, wz); }
                            continue;
                        }

                        painted++;
                        if (had && cur == kind) continue;
                        if (!had && _terrain.Count >= _cfgMaxTerrain.Value)
                        {
                            LogOnce("terraincap", "Reached MaxTerrainCells (" + _cfgMaxTerrain.Value + ").");
                            continue;
                        }
                        _terrain[key] = kind;
                        changed = true;
                        if (had) _rtDirty = true;           // a different paint under the old one: repaint everything
                        else _addTerrain.Add(key);
                        MarkPixelDirty(wx, wz);
                    }
                }
            }
            catch (Exception e)
            {
                TerrainError("terrain sampling", e);
                return changed;
            }

            if (_cfgDebug.Value)
                Logger.LogInfo(string.Format("Terrain scan: heightmaps={0}, samples={1}, painted={2}, stored={3}",
                    maps.Count, samples, painted, _terrain.Count));

            return changed;
        }

        private static Heightmap PickHeightmap(List<Heightmap> maps, Vector3 p, ref Heightmap cached)
        {
            const float half = 32f; // ZoneSystem.c_ZoneSizeHalf
            if (cached != null)
            {
                Vector3 cp = cached.transform.position;
                if (Mathf.Abs(p.x - cp.x) <= half && Mathf.Abs(p.z - cp.z) <= half) return cached;
            }
            for (int i = 0; i < maps.Count; i++)
            {
                Heightmap hm = maps[i];
                if (hm == null || hm.IsDistantLod) continue;
                Vector3 hp = hm.transform.position;
                if (Mathf.Abs(p.x - hp.x) <= half && Mathf.Abs(p.z - hp.z) <= half)
                {
                    cached = hm;
                    return hm;
                }
            }
            return null;
        }

        private void TerrainError(string where, Exception e)
        {
            _terrainErrorCount++;
            HandleError("terrain: " + where, e);
            if (_terrainErrorCount >= MaxTerrainErrors && !_terrainDisabled)
            {
                _terrainDisabled = true;
                Logger.LogWarning("The path layer failed too often and is off for this session. Buildings keep working.");
            }
        }

        // ------------------------------------------------------------------
        // ZDO scanning - the object database instead of the physics scene
        // ------------------------------------------------------------------
        // Physics only sees colliders that exist, i.e. the loaded zones around the player.
        // ZDOMan holds a ZDO for every networked object the client knows about: on the host
        // that is the entire world, on a client of a dedicated server everything the server
        // has sent this session. Persistent ZDOs stay in the dictionary after their zone is
        // unloaded, and destruction is broadcast to every peer, so the set is authoritative
        // for everything it contains. What it does NOT contain on a client are pieces seen in
        // earlier sessions only, which is why removal is trusted only inside the active area.
        private bool UseZdoScan
        {
            get
            {
                return !_zdoDisabled && _fiObjectsByID != null &&
                       string.Equals(_cfgScanSource.Value, "ZDO", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void StepZdoScan(float now, Vector3 pos)
        {
            try
            {
                if (_zdoCursor < 0 && _forestCursor < 0)
                {
                    if (now < _nextZdoPass) return;
                    if (!BeginZdoPass(pos))
                    {
                        _nextZdoPass = now + _cfgZdoInterval.Value;
                        return;
                    }
                }

                if (_zdoCursor >= 0)
                {
                    int end = Mathf.Min(_zdoSnapshot.Count, _zdoCursor + Mathf.Clamp(_cfgZdoPerFrame.Value, 500, 50000));
                    _biomeLookupsThisFrame = 0;
                    for (; _zdoCursor < end && _biomeLookupsThisFrame < BiomeLookupsPerFrame; _zdoCursor++)
                        ExamineZdo(_zdoSnapshot[_zdoCursor]);
                    _zdoPassFrames++;

                    if (_zdoCursor >= _zdoSnapshot.Count)
                    {
                        FinishZdoPass();                    // may queue the forest check as a second phase
                        if (_forestCursor < 0) _nextZdoPass = now + _cfgZdoInterval.Value;
                    }
                    return;
                }

                // second phase: the forest check of the same pass, a few zones per frame
                StepForestEval();
                if (_forestCursor < 0) _nextZdoPass = now + _cfgZdoInterval.Value;
            }
            catch (Exception e)
            {
                ZdoError("pass", e);
                ResetZdoPass();
                _nextZdoPass = now + _cfgZdoInterval.Value;
            }
        }

        private bool BeginZdoPass(Vector3 pos)
        {
            ZDOMan man = ZDOMan.instance;
            if (man == null || ZNetScene.instance == null || ZNet.instance == null) return false;

            Dictionary<ZDOID, ZDO> all = _fiObjectsByID.GetValue(man) as Dictionary<ZDOID, ZDO>;
            if (all == null) throw new InvalidOperationException("ZDOMan.m_objectsByID is null");

            ResetZdoPass();

            // a snapshot of the references: the dictionary changes while we walk it over several frames
            if (_zdoSnapshot.Capacity < all.Count) _zdoSnapshot.Capacity = all.Count;
            foreach (ZDO zdo in all.Values) _zdoSnapshot.Add(zdo);

            if (_treeCount != null)
            {
                Array.Clear(_treeCount, 0, _treeCount.Length);
                _outsideCounts.Clear();
            }

            _zdoCursor = 0;
            _zdoPassFrames = 0;
            _zdoPassOrigin = pos;
            _zdoPassPieces = _cfgShowBuildings.Value;
            _zdoAuthoritative = ZNet.instance.IsServer();
            return true;
        }

        private void ResetZdoPass()
        {
            _zdoCursor = -1;
            _forestCursor = -1;
            _zdoSnapshot.Clear();
            foreach (KeyValuePair<int, List<PieceRec>> kv in _zdoBuckets) ReturnList(kv.Value);
            _zdoBuckets.Clear();
            _removeScratch.Clear();
        }

        private void ExamineZdo(ZDO zdo)
        {
            // a ZDO returned to the pool keeps its prefab hash; only the id and position are cleared
            if (zdo == null || zdo.m_uid.IsNone() || !zdo.IsValid()) return;

            int hash = zdo.GetPrefab();
            if (hash == 0) return;

            PrefabInfo info;
            if (!_prefabCache.TryGetValue(hash, out info))
            {
                info = BuildPrefabInfo(hash);
                _prefabCache[hash] = info;
            }
            if (info.IsTree)
            {
                int tidx;
                if (_treeCount != null && WorldToIndex(zdo.GetPosition(), out tidx))
                {
                    if (_treeCount[tidx] < 255) _treeCount[tidx]++;
                    if (_cfgShowPlanted.Value && !_forestMask[tidx] && PixelCanHoldForest(tidx))
                    {
                        int c;
                        _outsideCounts.TryGetValue(tidx, out c);
                        _outsideCounts[tidx] = c + 1;
                    }
                }
                return;
            }
            if (!info.IsPiece || !_zdoPassPieces) return;

            Vector3 p = zdo.GetPosition();
            Quaternion q = zdo.GetRotation();

            float minX, minY, minZ, maxX, maxY, maxZ;
            if (q.x == 0f && q.y == 0f && q.z == 0f)
            {
                minX = info.Min.x; minY = info.Min.y; minZ = info.Min.z;
                maxX = info.Max.x; maxY = info.Max.y; maxZ = info.Max.z;
            }
            else
            {
                // rotate the prefab's box and take the axis-aligned footprint of the result
                minX = minY = minZ = float.MaxValue;
                maxX = maxY = maxZ = float.MinValue;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 v = q * new Vector3(
                        (c & 1) == 0 ? info.Min.x : info.Max.x,
                        (c & 2) == 0 ? info.Min.y : info.Max.y,
                        (c & 4) == 0 ? info.Min.z : info.Max.z);
                    if (v.x < minX) minX = v.x;
                    if (v.x > maxX) maxX = v.x;
                    if (v.y < minY) minY = v.y;
                    if (v.y > maxY) maxY = v.y;
                    if (v.z < minZ) minZ = v.z;
                    if (v.z > maxZ) maxZ = v.z;
                }
            }

            if (maxX - minX > MaxFootprint || maxZ - minZ > MaxFootprint) return;

            PieceRec rec = new PieceRec();
            rec.X0 = p.x + minX; rec.X1 = p.x + maxX;
            rec.Z0 = p.z + minZ; rec.Z1 = p.z + maxZ;
            rec.Y = p.y + maxY; rec.Mat = info.Mat;

            int idx;
            if (!WorldToIndex(new Vector3(rec.CX, 0f, rec.CZ), out idx)) return;

            List<PieceRec> bucket;
            if (!_zdoBuckets.TryGetValue(idx, out bucket))
            {
                bucket = RentList();
                _zdoBuckets[idx] = bucket;
            }
            bucket.Add(rec);
        }

        private PrefabInfo BuildPrefabInfo(int hash)
        {
            PrefabInfo info = new PrefabInfo();
            info.Mat = MatNone;

            GameObject prefab = ZNetScene.instance.GetPrefab(hash);
            if (prefab == null) return info;
            if (prefab.GetComponent<Piece>() == null)
            {
                info.IsTree = IsTreePrefab(prefab);
                return info;
            }

            WearNTear wnt = prefab.GetComponent<WearNTear>();
            if (wnt != null)
            {
                int m = (int)wnt.m_materialType;
                if (m >= 0 && m < MatNames.Length) info.Mat = (byte)m;
            }

            // the same colliders the physics scan used to see, measured once on the prefab and
            // kept in its local space; the rotation of each placed object is applied later
            Collider[] cols = prefab.GetComponentsInChildren<Collider>(true);
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;

            for (int i = 0; i < cols.Length; i++)
            {
                Collider col = cols[i];
                if (col == null || !col.enabled || col.gameObject.layer != _zdoPieceLayer) continue;

                Vector3 c, e;
                if (!LocalBox(col, out c, out e)) continue;

                Matrix4x4 m = toRoot * col.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 v = m.MultiplyPoint3x4(new Vector3(
                        (k & 1) == 0 ? c.x - e.x : c.x + e.x,
                        (k & 2) == 0 ? c.y - e.y : c.y + e.y,
                        (k & 4) == 0 ? c.z - e.z : c.z + e.z));
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
                any = true;
            }

            // no "piece" collider at all (ships, carts, plants) or a stray oversized one: not a building
            if (!any) return info;
            if (max.x - min.x > MaxFootprint || max.z - min.z > MaxFootprint) return info;

            info.IsPiece = true;
            info.Min = min;
            info.Max = max;
            return info;
        }

        private static bool LocalBox(Collider col, out Vector3 center, out Vector3 extents)
        {
            BoxCollider box = col as BoxCollider;
            if (box != null)
            {
                center = box.center;
                extents = box.size * 0.5f;
                return true;
            }

            SphereCollider sph = col as SphereCollider;
            if (sph != null)
            {
                center = sph.center;
                extents = new Vector3(sph.radius, sph.radius, sph.radius);
                return true;
            }

            CapsuleCollider cap = col as CapsuleCollider;
            if (cap != null)
            {
                float half = Mathf.Max(cap.radius, cap.height * 0.5f);
                center = cap.center;
                extents = new Vector3(cap.radius, cap.radius, cap.radius);
                if (cap.direction == 0) extents.x = half;
                else if (cap.direction == 1) extents.y = half;
                else extents.z = half;
                return true;
            }

            MeshCollider mesh = col as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null)
            {
                Bounds b = mesh.sharedMesh.bounds;
                center = b.center;
                extents = b.extents;
                return true;
            }

            center = Vector3.zero;
            extents = Vector3.zero;
            return false;
        }

        private void FinishZdoPass()
        {
            bool changed = false;
            int replaced = 0, removed = 0, fresh = 0;
            int seenPixels = _zdoBuckets.Count;

            // 1. Pixels we remember that the database does not mention. On the host they are
            //    gone for real. On a client the server only keeps us current inside the active
            //    area, and even there the sync takes a moment after arriving, so a pixel has to
            //    have been inside it at the start of this pass AND the previous one.
            bool trustNear = _zdoPassPieces && (_zdoAuthoritative ||
                             (_zdoHavePrevOrigin && ZoneSystem.instance != null && ZoneSystem.instance.IsActiveAreaLoaded()));
            if (trustNear)
            {
                float half = _texSize * 0.5f;
                _removeScratch.Clear();
                foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                {
                    int idx = kv.Key;
                    if (_zdoBuckets.ContainsKey(idx)) continue;
                    if (!_zdoAuthoritative)
                    {
                        Vector3 w = new Vector3((idx % _texSize - half) * _pixelSize, 0f, (idx / _texSize - half) * _pixelSize);
                        if (!ZNetScene.InActiveArea(w, _zdoPassOrigin) || !ZNetScene.InActiveArea(w, _zdoPrevOrigin)) continue;
                    }
                    _removeScratch.Add(idx);
                }
                for (int i = 0; i < _removeScratch.Count; i++)
                {
                    int idx = _removeScratch[i];
                    List<PieceRec> old = _pieces[idx];
                    _pieceCount -= old.Count;
                    ReturnList(old);
                    _pieces.Remove(idx);
                    _pending.Add(idx);
                    changed = true;
                    _rtDirty = true;
                    removed++;
                }
                _removeScratch.Clear();
            }

            // 2. Pixels this pass saw: replace the stored bucket when its contents differ
            foreach (KeyValuePair<int, List<PieceRec>> kv in _zdoBuckets)
            {
                int idx = kv.Key;
                List<PieceRec> list = kv.Value;
                fresh += list.Count;

                List<PieceRec> old;
                bool had = _pieces.TryGetValue(idx, out old);
                if (had && SameBucket(old, list))
                {
                    ReturnList(list);
                    continue;
                }

                int oldN = had ? old.Count : 0;
                if (_pieceCount - oldN + list.Count > _cfgMaxPieces.Value)
                {
                    LogOnce("piececap", "Reached MaxPieces (" + _cfgMaxPieces.Value + "), no more build pieces are recorded.");
                    ReturnList(list);
                    continue;
                }

                // what is new gets drawn on top; anything gone means the texture is rebuilt
                if (DiffBucket(had ? old : null, list)) _rtDirty = true;

                _pieces[idx] = list;
                _pieceCount += list.Count - oldN;
                if (had) ReturnList(old);
                _pending.Add(idx);
                changed = true;
                replaced++;
            }
            _zdoBuckets.Clear();       // its lists now live in _pieces or went back to the pool
            _zdoSnapshot.Clear();
            _zdoCursor = -1;

            int forestZones = 0;
            if (_treeCount != null)
            {
                try
                {
                    forestZones = BeginForestEval();
                    if (UpdatePlantedForest()) { changed = true; _maskDirty = true; }
                    if (_forestCursor < 0 && _forestChanged) { changed = true; _maskDirty = true; }   // the cleared set was emptied with no second phase to follow
                }
                catch (Exception e) { ForestError(e); }
            }

            _zdoPrevOrigin = _zdoPassOrigin;
            _zdoHavePrevOrigin = true;
            _zdoPasses++;

            if (changed) _storeChanged = true;

            if (_zdoPasses == 1 || (_cfgDebug.Value && changed))
                Logger.LogInfo(string.Format(
                    "ZDO scan: {0} build pieces in {1} map pixels, {2}; {3} pixels updated, {4} cleared; forest check queued for {5} zones; {6} frames",
                    fresh, seenPixels,
                    _zdoAuthoritative ? "host, the whole world" : "client, what the server has sent",
                    replaced, removed, forestZones, _zdoPassFrames));
        }

        // ------------------------------------------------------------------
        // cleared forest
        // ------------------------------------------------------------------
        // The vanilla forest layer is a static mask from the world generator (red channel of
        // _MaskTex: Meadows and Plains by forest factor, the whole Black Forest). Trees are
        // persistent ZDOs placed once when a zone is generated, and chopping one destroys its
        // ZDO, so "forest on the mask but no tree ZDO anywhere near" means the woods are gone.
        // That verdict is only safe where we hold complete tree data: every generated zone on
        // the host, and on a client the zones that sat inside the active area for two passes.
        private bool ForestWanted
        {
            get { return (_cfgShowForest.Value || _cfgShowPlanted.Value) && !_forestDisabled && UseZdoScan; }
        }

        private void InitForestLayer()
        {
            DropForestData();
            if (_mm == null || !ForestWanted)
            {
                if ((_cfgShowForest.Value || _cfgShowPlanted.Value) && !_forestDisabled && !UseZdoScan)
                    LogOnce("forestzdo", "The forest layers need BuildingScanSource = ZDO and are off.");
                return;
            }

            try
            {
                Texture2D mask = null;
                if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
                    mask = _mm.m_mapImageLarge.material.GetTexture("_MaskTex") as Texture2D;
                if (mask == null || mask.width != _texSize || mask.height != _texSize)
                {
                    _forestDisabled = true;
                    Logger.LogWarning("The map's forest mask is missing or has an unexpected size; the cleared-forest layer is off.");
                    return;
                }

                // one 16 MB read, kept as 512 KB of bits (three such bit sets in all)
                Color32[] px = mask.GetPixels32();
                BitArray bits = new BitArray(px.Length);
                int forest = 0;
                for (int i = 0; i < px.Length; i++)
                    if (px[i].r > 127) { bits[i] = true; forest++; }

                _vanillaMask = mask;
                _forestMask = bits;
                _pixelTrusted = new BitArray(px.Length);
                _treeCount = new byte[px.Length];
                if (_cfgDebug.Value) Logger.LogInfo("Forest mask read: " + forest + " forest pixels.");
            }
            catch (Exception e)
            {
                _forestDisabled = true;
                DropForestData();
                HandleError("forest mask", e);
                Logger.LogWarning("Could not read the map's forest mask; the cleared-forest layer is off.");
            }
        }

        private void DropForestData()
        {
            _forestMask = null;
            _pixelTrusted = null;
            _treeCount = null;
            _trustedZoneScratch.Clear();
            _outsideCounts.Clear();
            _forestCursor = -1;
        }

        // A tree is a TreeBase: the thing that falls and leaves logs. Saplings, bushes, stumps
        // and young trees are Destructibles; some carry the Tree hit type, but none of them is
        // what the map means by "forest", and a clearing keeps plenty of them.
        private bool IsTreePrefab(GameObject prefab)
        {
            if (prefab.GetComponent<TreeBase>() == null) return false;
            if (_cfgDebug.Value) Logger.LogInfo("Tree prefab: " + prefab.name);
            return true;
        }

        private static int ZoneKey(int zx, int zy)
        {
            return (zx << 16) | (zy & 0xFFFF);
        }

        private void ForestError(Exception e)
        {
            HandleError("cleared forest", e);
            DropForestData();
            _forestDisabled = true;
            Logger.LogWarning("The cleared-forest layer failed and is off for this session. Buildings and paths keep working.");
        }

        // Returns the number of zones queued for evaluation; StepForestEval works through them.
        private int BeginForestEval()
        {
            _forestCursor = -1;
            _forestChanged = false;
            _forestAdded = 0;
            _forestRemoved = 0;
            _forestSample.Clear();
            _pixelTrusted.SetAll(false);
            _trustedZoneScratch.Clear();

            // 1. zones with complete tree data
            HashSet<Vector2s> generated = null;
            if (_zdoAuthoritative && _fiGeneratedZones != null && ZoneSystem.instance != null)
                generated = _fiGeneratedZones.GetValue(ZoneSystem.instance) as HashSet<Vector2s>;

            if (generated != null)
            {
                foreach (Vector2s z in generated) _trustedZoneScratch.Add(ZoneKey(z.x, z.y));
            }
            else
            {
                // synced once, synced for the session: persistent objects are never dropped
                if (_zdoHavePrevOrigin)
                {
                    Vector2s c = ZoneSystem.GetZone(_zdoPassOrigin);
                    for (int dy = -4; dy <= 4; dy++)
                    {
                        for (int dx = -4; dx <= 4; dx++)
                        {
                            Vector2s z = new Vector2s(c.x + dx, c.y + dy);
                            Vector3 zc = ZoneSystem.GetZonePos(z);
                            if (ZNetScene.InActiveArea(zc, _zdoPassOrigin) && ZNetScene.InActiveArea(zc, _zdoPrevOrigin))
                                _trustedZones.Add(ZoneKey(z.x, z.y));
                        }
                    }
                }
                foreach (int key in _trustedZones) _trustedZoneScratch.Add(key);
            }

            int zones = _trustedZoneScratch.Count;
            if (zones == 0) return 0;

            float half = _texSize * 0.5f;
            for (int i = 0; i < zones; i++)
            {
                int px0, py0, px1, py1;
                ZonePixelRange(_trustedZoneScratch[i], half, out px0, out py0, out px1, out py1);
                for (int py = py0; py <= py1; py++)
                    for (int px = px0; px <= px1; px++)
                        _pixelTrusted[py * _texSize + px] = true;
            }

            if (!_cfgShowForest.Value)
            {
                if (_clearedForest.Count > 0) { _clearedForest.Clear(); _forestChanged = true; }
                return 0;
            }

            _forestCursor = 0;
            return zones;
        }

        // Planted forest: enough trees in a pixel the vanilla mask leaves bare. A tree that is
        // in the database exists, so pixels are added anywhere; they are dropped again on the
        // host everywhere and on a client only where the zone is synced, like build pieces.
        private bool UpdatePlantedForest()
        {
            if (!_cfgShowPlanted.Value)
            {
                if (_plantedForest.Count == 0) return false;
                _plantedForest.Clear();
                return true;
            }

            int min = Mathf.Max(1, _cfgPlantedMinTrees.Value);
            int added = 0, removed = 0;
            foreach (KeyValuePair<int, int> kv in _outsideCounts)
            {
                if (kv.Value < min || !_plantedForest.Add(kv.Key)) continue;
                added++;
                if (_forestSample.Count < 6) _forestSample.Add(kv.Key);
            }

            _removeScratch.Clear();
            foreach (int idx in _plantedForest)
            {
                int c;
                if (_outsideCounts.TryGetValue(idx, out c) && c >= min) continue;
                if (_zdoAuthoritative || _pixelTrusted[idx]) _removeScratch.Add(idx);
            }
            for (int i = 0; i < _removeScratch.Count; i++)
                if (_plantedForest.Remove(_removeScratch[i])) removed++;
            _removeScratch.Clear();

            if (_cfgDebug.Value && (added > 0 || removed > 0))
                Logger.LogInfo(string.Format("Planted forest: {0} pixels (+{1}, -{2}), {3} tree pixels outside the vanilla forest, {4} biome lookups cached{5}",
                    _plantedForest.Count, added, removed, _outsideCounts.Count, _forestBiome.Count, SampleText()));
            else _forestSample.Clear();
            return added > 0 || removed > 0;
        }

        private bool PixelCanHoldForest(int idx)
        {
            if (_cfgPlantedAnyBiome.Value) return true;
            bool ok;
            if (_forestBiome.TryGetValue(idx, out ok)) return ok;

            // the generator's biome noise costs a few microseconds, so it is asked once per
            // pixel and the number of first-time asks per frame is capped by the caller
            WorldGenerator wg = WorldGenerator.instance;
            if (wg == null) return false;
            _biomeLookupsThisFrame++;
            float half = _texSize * 0.5f;
            Vector3 w = new Vector3((idx % _texSize - half) * _pixelSize, 0f, (idx / _texSize - half) * _pixelSize);
            Heightmap.Biome b = wg.GetBiome(w);
            ok = b == Heightmap.Biome.Meadows || b == Heightmap.Biome.BlackForest || b == Heightmap.Biome.Plains;
            _forestBiome[idx] = ok;
            return ok;
        }

        // 2. every forest pixel of a trusted zone: cleared when the whole window around it is
        //    trusted and holds no tree. A few zones per frame: a host with thousands of
        //    generated zones would otherwise spend tens of milliseconds here every pass.
        private void StepForestEval()
        {
            if (_forestCursor < 0) return;
            if (_pixelTrusted == null || _treeCount == null || _forestMask == null)
            {
                _forestCursor = -1;
                return;
            }

            try
            {
                float half = _texSize * 0.5f;
                int r = Mathf.Max(1, Mathf.CeilToInt(_cfgForestRadius.Value / _pixelSize));
                int maxTrees = Mathf.Max(0, _cfgForestMaxTrees.Value);
                int end = Mathf.Min(_trustedZoneScratch.Count, _forestCursor + ForestZonesPerFrame);
                for (; _forestCursor < end; _forestCursor++)
                    EvalForestZone(_trustedZoneScratch[_forestCursor], half, r, maxTrees);

                if (_forestCursor < _trustedZoneScratch.Count) return;

                _forestCursor = -1;
                if (_forestChanged)
                {
                    _storeChanged = true;
                    _maskDirty = true;
                    if (_cfgDebug.Value)
                        Logger.LogInfo(string.Format("Cleared forest: {0} pixels (+{1}, -{2}) over {3} zones{4}",
                            _clearedForest.Count, _forestAdded, _forestRemoved, _trustedZoneScratch.Count, SampleText()));
                }
            }
            catch (Exception e)
            {
                ForestError(e);
            }
        }

        private void EvalForestZone(int key, float half, int r, int maxTrees)
        {
            int px0, py0, px1, py1;
            ZonePixelRange(key, half, out px0, out py0, out px1, out py1);
            for (int py = py0; py <= py1; py++)
            {
                for (int px = px0; px <= px1; px++)
                {
                    int idx = py * _texSize + px;
                    bool cleared = false;
                    if (_forestMask[idx])
                    {
                        // the whole window must be trusted, and hold no more than maxTrees trees
                        cleared = true;
                        int trees = 0;
                        for (int wy = py - r; wy <= py + r && cleared; wy++)
                        {
                            if (wy < 0 || wy >= _texSize) { cleared = false; break; }
                            int row = wy * _texSize;
                            for (int wx = px - r; wx <= px + r; wx++)
                            {
                                if (wx < 0 || wx >= _texSize || !_pixelTrusted[row + wx]) { cleared = false; break; }
                                trees += _treeCount[row + wx];
                                if (trees > maxTrees) { cleared = false; break; }
                            }
                        }
                    }
                    if (cleared)
                    {
                        if (_clearedForest.Add(idx))
                        {
                            _forestChanged = true;
                            _forestAdded++;
                            if (_forestSample.Count < 6) _forestSample.Add(idx);
                        }
                    }
                    else if (_clearedForest.Remove(idx)) { _forestChanged = true; _forestRemoved++; }
                }
            }
        }

        // debug helper: world coordinates of a few pixels, so a result can be checked on the map
        private string SampleText()
        {
            if (_forestSample.Count == 0) return "";
            float half = _texSize * 0.5f;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(", e.g. at");
            for (int i = 0; i < _forestSample.Count; i++)
            {
                int idx = _forestSample[i];
                sb.Append(string.Format(" ({0:0}, {1:0})", (idx % _texSize - half) * _pixelSize, (idx / _texSize - half) * _pixelSize));
            }
            _forestSample.Clear();
            return sb.ToString();
        }

        private void ZonePixelRange(int key, float half, out int px0, out int py0, out int px1, out int py1)
        {
            // pixels whose centre lies inside the 64 m zone square
            Vector3 c = ZoneSystem.GetZonePos(new Vector2s((short)(key >> 16), (short)(key & 0xFFFF)));
            const float zh = 32f; // ZoneSystem.c_ZoneSizeHalf
            px0 = Mathf.Clamp(Mathf.CeilToInt((c.x - zh) / _pixelSize + half), 0, _texSize - 1);
            px1 = Mathf.Clamp(Mathf.CeilToInt((c.x + zh) / _pixelSize + half) - 1, 0, _texSize - 1);
            py0 = Mathf.Clamp(Mathf.CeilToInt((c.z - zh) / _pixelSize + half), 0, _texSize - 1);
            py1 = Mathf.Clamp(Mathf.CeilToInt((c.z + zh) / _pixelSize + half) - 1, 0, _texSize - 1);
        }

        private List<PieceRec> RentList()
        {
            return _listPool.Count > 0 ? _listPool.Pop() : new List<PieceRec>();
        }

        private void ReturnList(List<PieceRec> list)
        {
            if (list == null) return;
            list.Clear();
            if (_listPool.Count < 4096) _listPool.Push(list);
        }

        private static bool RecEquals(PieceRec a, PieceRec b)
        {
            return a.X0 == b.X0 && a.X1 == b.X1 && a.Z0 == b.Z0 && a.Z1 == b.Z1 && a.Y == b.Y && a.Mat == b.Mat;
        }

        private static bool BucketContains(List<PieceRec> list, PieceRec rec)
        {
            for (int i = 0; i < list.Count; i++)
                if (RecEquals(list[i], rec)) return true;
            return false;
        }

        // Queues the pieces of fresh that old lacks; returns true when old holds something fresh lacks.
        private bool DiffBucket(List<PieceRec> old, List<PieceRec> fresh)
        {
            if (old != null)
                for (int i = 0; i < old.Count; i++)
                    if (!BucketContains(fresh, old[i])) return true;
            for (int i = 0; i < fresh.Count; i++)
                if (old == null || !BucketContains(old, fresh[i])) _addPieces.Add(fresh[i]);
            return false;
        }

        private static bool SameBucket(List<PieceRec> a, List<PieceRec> b)
        {
            if (a.Count != b.Count) return false;
            // order-independent, so the dictionary handing us objects in a different order
            // does not count as a change and trigger a rebuild of the whole map layer
            return Signature(a) == Signature(b);
        }

        private static long Signature(List<PieceRec> list)
        {
            long sum = 0;
            for (int i = 0; i < list.Count; i++)
            {
                PieceRec r = list[i];
                int h = r.X0.GetHashCode();
                h = h * 31 + r.X1.GetHashCode();
                h = h * 31 + r.Z0.GetHashCode();
                h = h * 31 + r.Z1.GetHashCode();
                h = h * 31 + r.Y.GetHashCode();
                h = h * 31 + r.Mat;
                sum += h;
            }
            return sum;
        }

        private void ZdoError(string where, Exception e)
        {
            _zdoErrorCount++;
            HandleError("zdo: " + where, e);
            if (_zdoErrorCount >= MaxZdoErrors && !_zdoDisabled)
            {
                _zdoDisabled = true;
                ResetZdoPass();
                Logger.LogWarning("Reading the object database failed too often; build pieces are scanned through physics for the rest of this session.");
            }
        }

        // ------------------------------------------------------------------
        // coarse layer (map texture)
        // ------------------------------------------------------------------
        private void MarkPixelDirty(float worldX, float worldZ)
        {
            int idx;
            if (WorldToIndex(new Vector3(worldX, 0f, worldZ), out idx)) _pending.Add(idx);
        }

        private void QueueAllPixels()
        {
            foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces) _pending.Add(kv.Key);
            float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            foreach (KeyValuePair<long, byte> kv in _terrain)
            {
                int gx = (int)(kv.Key >> 32);
                int gz = (int)(kv.Key & 0xFFFFFFFFL);
                MarkPixelDirty((gx + 0.5f) * grid, (gz + 0.5f) * grid);
            }
        }

        private void Flush()
        {
            if (_ourTex == null || _vanillaTex == null) return;

            int budget = Mathf.Clamp(_cfgPixelsPerFlush.Value, 64, 100000);
            _pendingScratch.Clear();
            foreach (int idx in _pending)
            {
                _pendingScratch.Add(idx);
                if (_pendingScratch.Count >= budget) break;
            }
            if (_pendingScratch.Count == 0) return;

            BitArray explored = null, exploredOthers = null;
            if (_cfgRespectFog.Value)
            {
                explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
            }

            int written = 0;
            for (int i = 0; i < _pendingScratch.Count; i++)
            {
                int idx = _pendingScratch[i];
                _pending.Remove(idx);

                int px = idx % _texSize;
                int py = idx / _texSize;
                if (px < 0 || py < 0 || px >= _texSize || py >= _texSize) continue;

                Color overlay;
                bool hasOverlay = TryGetCoarseColor(idx, out overlay);

                if (hasOverlay && _cfgRespectFog.Value && !IsExplored(explored, exploredOthers, idx))
                {
                    _deferredByFog.Add(idx);
                    continue;
                }

                if (!hasOverlay && !_paintedPixels.Contains(idx)) continue;

                Color baseColor = _vanillaTex.GetPixel(px, py);
                Color final = hasOverlay ? Color.Lerp(baseColor, overlay, Mathf.Clamp01(overlay.a)) : baseColor;

                if (hasOverlay) _paintedPixels.Add(idx); else _paintedPixels.Remove(idx);

                _onePixel[0] = final;
                _ourTex.SetPixels32(px, py, 1, 1, _onePixel);
                written++;
            }

            if (written > 0)
            {
                _ourTex.Apply(false);
                if (_cfgDebug.Value)
                    Logger.LogInfo(string.Format("Flush: {0} written, {1} pending", written, _pending.Count));
            }
        }

        private bool TryGetCoarseColor(int idx, out Color color)
        {
            color = Color.clear;

            if (_cfgShowBuildings.Value)
            {
                List<PieceRec> bucket;
                if (_pieces.TryGetValue(idx, out bucket) && bucket.Count > 0)
                {
                    byte mat = MatNone;
                    float bestY = float.MinValue;
                    for (int i = 0; i < bucket.Count; i++)
                        if (bucket[i].Y > bestY) { bestY = bucket[i].Y; mat = bucket[i].Mat; }
                    color = MatColor(mat);
                    return true;
                }
            }

            if (_cfgShowPaths.Value && _terrain.Count > 0)
            {
                byte kind = TerrainKindForPixel(idx);
                if (kind != TerrainNone) { color = TerrainColor(kind); return true; }
            }

            return false;
        }

        private Color TerrainColor(byte kind)
        {
            if (kind == TerrainPaved) return _cfgPavedColor.Value;
            if (kind == TerrainDirt) return _cfgDirtColor.Value;
            if (kind == TerrainCultivated) return _cfgCultivatedColor.Value;
            return _cfgClearedColor.Value;
        }

        private byte TerrainKindForPixel(int idx)
        {
            float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            int px = idx % _texSize;
            int py = idx / _texSize;
            float half = _texSize * 0.5f;
            float x0 = (px - half - 0.5f) * _pixelSize;
            float z0 = (py - half - 0.5f) * _pixelSize;

            int gx0 = Mathf.FloorToInt(x0 / grid);
            int gz0 = Mathf.FloorToInt(z0 / grid);
            int span = Mathf.CeilToInt(_pixelSize / grid);

            int paved = 0, dirt = 0, cult = 0, cleared = 0;
            for (int gx = gx0; gx <= gx0 + span; gx++)
            {
                for (int gz = gz0; gz <= gz0 + span; gz++)
                {
                    byte k;
                    if (!_terrain.TryGetValue(((long)gx << 32) | (uint)gz, out k)) continue;
                    if (k == TerrainPaved) paved++;
                    else if (k == TerrainDirt) dirt++;
                    else if (k == TerrainCultivated) cult++;
                    else if (k == TerrainCleared) cleared++;
                }
            }
            int best = Mathf.Max(Mathf.Max(paved, dirt), Mathf.Max(cult, cleared));
            if (best == 0) return TerrainNone;
            if (best == paved) return TerrainPaved;
            if (best == dirt) return TerrainDirt;
            if (best == cult) return TerrainCultivated;
            return TerrainCleared;
        }

        private Color MatColor(byte mat)
        {
            if (mat != MatNone && mat < _cfgMatColor.Length) return _cfgMatColor[mat].Value;
            return _cfgMatUnknownColor.Value;
        }

        private static bool IsExplored(BitArray explored, BitArray exploredOthers, int idx)
        {
            if (explored == null && exploredOthers == null) return true;
            if (explored != null && idx < explored.Length && explored[idx]) return true;
            if (exploredOthers != null && idx < exploredOthers.Length && exploredOthers[idx]) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // GPU map layer - draws straight into the texture the map shader samples
        // ------------------------------------------------------------------
        private bool InitGpuLayer()
        {
            if (!SystemInfo.supportsRenderTextures) return false;

            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null)
            {
                Logger.LogWarning("The built-in colored shader is missing, cannot render the map layer on the GPU.");
                return false;
            }

            int scale = Mathf.Clamp(_cfgMapScale.Value, 1, 8);
            int maxSize = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            while (_texSize * scale > maxSize && scale > 1) scale /= 2;
            if (_texSize * scale > maxSize) return false;

            LinearColors = _cfgLinearFix.Value && QualitySettings.activeColorSpace == ColorSpace.Linear;

            _glMat = new Material(sh);
            _glMat.hideFlags = HideFlags.HideAndDontSave;
            _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _glMat.SetInt("_ZWrite", 0);
            _glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);

            while (scale >= 1)
            {
                int size = _texSize * scale;
                RenderTexture rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
                rt.name = "MapOverlay_MapTexture";
                rt.useMipMap = false;
                rt.autoGenerateMips = false;
                rt.filterMode = _vanillaTex.filterMode;
                rt.wrapMode = _vanillaTex.wrapMode;

                bool created = false;
                try { created = rt.Create(); }
                catch (Exception e) { Logger.LogWarning("Could not create a " + size + "px map texture: " + e.Message); }

                if (created)
                {
                    _rt = rt;
                    break;
                }

                try { UnityEngine.Object.Destroy(rt); } catch { }
                Logger.LogWarning("The card refused a " + size + "px map texture, stepping down.");
                if (scale == 1) break;
                scale /= 2;
            }

            if (_rt == null)
            {
                ReleaseGpuLayer();
                return false;
            }

            _rtScale = scale;
            _rtRebuilds = 0;

            // The forest pattern is drawn by the map shader from _MaskTex, so cleared woods have
            // to be erased in a copy of that mask. Vanilla size is enough: the detection works
            // per vanilla pixel anyway, and it saves another large texture.
            if (_forestMask != null)
            {
                try
                {
                    RenderTexture mask = new RenderTexture(_texSize, _texSize, 0, RenderTextureFormat.ARGB32);
                    mask.name = "MapOverlay_ForestMask";
                    mask.useMipMap = false;
                    mask.autoGenerateMips = false;
                    mask.filterMode = _vanillaMask.filterMode;
                    mask.wrapMode = _vanillaMask.wrapMode;
                    if (mask.Create())
                    {
                        _rtMask = mask;
                        _glMatMask = new Material(sh);
                        _glMatMask.hideFlags = HideFlags.HideAndDontSave;
                        // multiply: the quad colour scales each channel, so (0,1,1,1) wipes red only
                        _glMatMask.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.DstColor);
                        _glMatMask.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                        _glMatMask.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                        _glMatMask.SetInt("_ZWrite", 0);
                        _glMatMask.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                        // add: (1,0,0,0) switches red on and leaves the other channels alone
                        _glMatMaskAdd = new Material(sh);
                        _glMatMaskAdd.hideFlags = HideFlags.HideAndDontSave;
                        _glMatMaskAdd.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                        _glMatMaskAdd.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                        _glMatMaskAdd.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                        _glMatMaskAdd.SetInt("_ZWrite", 0);
                        _glMatMaskAdd.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                    }
                    else
                    {
                        try { UnityEngine.Object.Destroy(mask); } catch { }
                    }
                }
                catch (Exception e)
                {
                    HandleError("forest mask texture", e);
                    _rtMask = null;
                }
                if (_rtMask == null)
                {
                    _forestDisabled = true;
                    Logger.LogWarning("The card refused the forest mask texture; the cleared-forest layer is off for this session.");
                }
            }

            RebuildGpuLayer();
            if (_rtMask != null) RebuildMask();
            return true;
        }

        private void ReleaseGpuLayer()
        {
            if (_rt != null)
            {
                try { _rt.Release(); UnityEngine.Object.Destroy(_rt); } catch { }
                _rt = null;
            }
            if (_glMat != null)
            {
                try { UnityEngine.Object.Destroy(_glMat); } catch { }
                _glMat = null;
            }
            if (_rtMask != null)
            {
                try { _rtMask.Release(); UnityEngine.Object.Destroy(_rtMask); } catch { }
                _rtMask = null;
            }
            if (_glMatMask != null)
            {
                try { UnityEngine.Object.Destroy(_glMatMask); } catch { }
                _glMatMask = null;
            }
            if (_glMatMaskAdd != null)
            {
                try { UnityEngine.Object.Destroy(_glMatMaskAdd); } catch { }
                _glMatMaskAdd = null;
            }
            _rtScale = 1;
            _rtDirty = false;
            _maskDirty = false;
            _addPieces.Clear(); _addTerrain.Clear();
            _fogPieces.Clear(); _fogTerrain.Clear(); _fogForest.Clear();
        }

        private void RebuildGpuLayer()
        {
            if (_rt == null || _glMat == null || _vanillaTex == null) return;

            try
            {
                if (!_rt.IsCreated() && !_rt.Create()) return;

                // the vanilla map, upscaled on the card - no managed memory involved
                Graphics.Blit(_vanillaTex, _rt);

                // everything is drawn from scratch, so nothing is pending any more
                _addPieces.Clear(); _addTerrain.Clear();
                _fogPieces.Clear(); _fogTerrain.Clear();
                _drawnBuildings = _cfgShowBuildings.Value;
                _drawnPaths = _cfgShowPaths.Value;
                _drawnOutline = _cfgOutline.Value;

                BitArray explored = null, exploredOthers = null;
                if (_cfgRespectFog.Value)
                {
                    explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                    exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
                }

                float k = _rt.width / (float)_texSize;      // render-texture pixels per map pixel
                float half = _texSize * 0.5f;
                bool flip = _cfgMapFlipY.Value;
                int h = _rt.height;
                int quads = 0;

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = _rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, _rt.width, 0f, h);
                _glMat.SetPass(0);
                GL.Begin(GL.QUADS);

                if (_cfgShowPaths.Value && _terrain.Count > 0)
                {
                    float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                    float halfGrid = grid * 0.5f;
                    foreach (KeyValuePair<long, byte> kv in _terrain)
                    {
                        int gx = (int)(kv.Key >> 32);
                        int gz = (int)(kv.Key & 0xFFFFFFFFL);
                        float wx = (gx + 0.5f) * grid;
                        float wz = (gz + 0.5f) * grid;
                        if (!IsWorldExplored(explored, exploredOthers, wx, wz)) { _fogTerrain.Add(kv.Key); continue; }
                        EmitQuad(wx - halfGrid, wz - halfGrid, wx + halfGrid, wz + halfGrid,
                            TerrainColor(kv.Value), k, half, h, flip, grid);
                        quads++;
                    }
                }

                if (_cfgShowBuildings.Value && _pieces.Count > 0)
                {
                    float minPiece = Mathf.Max(0.1f, _cfgPieceSize.Value);

                    // first pass: a dark halo a little wider than every piece, so a building
                    // separates from the levelled dirt it usually stands on
                    if (_cfgOutline.Value)
                    {
                        float grow = Mathf.Max(0f, _cfgOutlineWidth.Value);
                        Color oc = _cfgOutlineColor.Value;
                        foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                        {
                            List<PieceRec> bucket = kv.Value;
                            for (int i = 0; i < bucket.Count; i++)
                            {
                                PieceRec rec = bucket[i];
                                if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) continue;
                                EmitQuad(rec.X0 - grow, rec.Z0 - grow, rec.X1 + grow, rec.Z1 + grow,
                                    oc, k, half, h, flip, minPiece + grow * 2f);
                                quads++;
                            }
                        }
                    }

                    foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                    {
                        List<PieceRec> bucket = kv.Value;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            PieceRec rec = bucket[i];
                            if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) { _fogPieces.Add(rec); continue; }
                            EmitQuad(rec.X0, rec.Z0, rec.X1, rec.Z1, MatColor(rec.Mat), k, half, h, flip, minPiece);
                            quads++;
                        }
                    }
                }

                if (_cfgDebugMarker.Value && Player.m_localPlayer != null)
                {
                    Vector3 pp = Player.m_localPlayer.transform.position;
                    EmitQuad(pp.x - 12f, pp.z - 2f, pp.x + 12f, pp.z + 2f, new Color(1f, 0f, 1f, 1f), k, half, h, flip, 0f);
                    EmitQuad(pp.x - 2f, pp.z - 12f, pp.x + 2f, pp.z + 12f, new Color(1f, 0f, 1f, 1f), k, half, h, flip, 0f);
                }

                GL.End();
                GL.PopMatrix();
                RenderTexture.active = prev;

                _rtRebuilds++;
                if (_rtRebuilds == 1 || (_cfgDebug.Value && _rtRebuilds % 20 == 1))
                    Logger.LogInfo(string.Format(
                        "Map layer rebuilt on the GPU: {0}px ({1:0.##} m/px), {2} shapes drawn, {3} held back by fog, {4:0} MB of video memory, linear colours {5}",
                        _rt.width, _pixelSize / _rtScale, quads, _fogPieces.Count + _fogTerrain.Count,
                        (_rt.width * (long)_rt.height + (_rtMask != null ? _rtMask.width * (long)_rtMask.height : 0L)) * 4L / (1024f * 1024f),
                        LinearColors ? "on" : "off"));
            }
            catch (Exception e)
            {
                GpuFailed("GPU map layer rebuild", e);
            }
        }

        // Draws what was queued since the last update straight onto the existing texture. A
        // fresh outline would darken the edge of every neighbour it overlaps, and a fresh path
        // cell would cover the building standing on it, so after each such shape the fills of
        // everything its halo can touch are painted again: the same order as a full rebuild,
        // restored locally.
        private void DrawAdditions()
        {
            if (_rt == null || _glMat == null) return;

            try
            {
                if (!_rt.IsCreated()) { _rtDirty = true; return; }

                BitArray explored = null, exploredOthers = null;
                if (_cfgRespectFog.Value)
                {
                    explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                    exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
                }

                float k = _rt.width / (float)_texSize;
                float half = _texSize * 0.5f;
                bool flip = _cfgMapFlipY.Value;
                int h = _rt.height;
                int quads = 0, cells = 0, pieces = 0;

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = _rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, _rt.width, 0f, h);
                _glMat.SetPass(0);
                GL.Begin(GL.QUADS);

                if (_addTerrain.Count > 0)
                {
                    float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                    float halfGrid = grid * 0.5f;
                    for (int i = 0; i < _addTerrain.Count; i++)
                    {
                        long key = _addTerrain[i];
                        byte kind;
                        if (!_terrain.TryGetValue(key, out kind)) continue;
                        float wx = ((int)(key >> 32) + 0.5f) * grid;
                        float wz = ((int)(key & 0xFFFFFFFFL) + 0.5f) * grid;
                        if (!IsWorldExplored(explored, exploredOthers, wx, wz)) { _fogTerrain.Add(key); continue; }
                        if (!_cfgShowPaths.Value) continue;
                        EmitQuad(wx - halfGrid, wz - halfGrid, wx + halfGrid, wz + halfGrid,
                            TerrainColor(kind), k, half, h, flip, grid);
                        quads++;
                        cells++;
                        if (_cfgShowBuildings.Value)
                        {
                            // any building standing on this cell was just painted over
                            _groupScratch.Clear();
                            PiecesIntersecting(wx - halfGrid, wz - halfGrid, wx + halfGrid, wz + halfGrid, true, _groupScratch);
                            if (_groupScratch.Count > 0) quads += DrawPieceGroup(_groupScratch, explored, exploredOthers, k, half, h, flip);
                        }
                    }
                    _addTerrain.Clear();
                }

                if (_addPieces.Count > 0)
                {
                    for (int i = 0; i < _addPieces.Count; i++)
                    {
                        PieceRec rec = _addPieces[i];
                        if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) { _fogPieces.Add(rec); continue; }
                        if (!_cfgShowBuildings.Value) continue;
                        _groupScratch.Clear();
                        _groupScratch.Add(rec);
                        quads += DrawPieceGroup(_groupScratch, explored, exploredOthers, k, half, h, flip);
                        pieces++;
                    }
                    _addPieces.Clear();
                }

                GL.End();
                GL.PopMatrix();
                RenderTexture.active = prev;

                _rtIncrements++;
                if (_cfgDebug.Value && (_rtIncrements == 1 || _rtIncrements % 20 == 0))
                    Logger.LogInfo(string.Format("Map layer updated in place: {0} new pieces, {1} new cells, {2} quads (update #{3})",
                        pieces, cells, quads, _rtIncrements));
            }
            catch (Exception e)
            {
                GpuFailed("GPU map layer update", e);
            }
        }

        // Outlines for the group, then fills for everything the outlines can have touched.
        private int DrawPieceGroup(List<PieceRec> group, BitArray explored, BitArray exploredOthers,
                                   float k, float half, int h, bool flip)
        {
            float minPiece = Mathf.Max(0.1f, _cfgPieceSize.Value);
            float grow = _cfgOutline.Value ? Mathf.Max(0f, _cfgOutlineWidth.Value) : 0f;
            int quads = 0;

            float rx0 = float.MaxValue, rz0 = float.MaxValue, rx1 = float.MinValue, rz1 = float.MinValue;
            for (int i = 0; i < group.Count; i++)
            {
                PieceRec rec = group[i];
                float x0, z0, x1, z1;
                DrawnRect(rec, grow, minPiece + grow * 2f, out x0, out z0, out x1, out z1);
                if (x0 < rx0) rx0 = x0;
                if (z0 < rz0) rz0 = z0;
                if (x1 > rx1) rx1 = x1;
                if (z1 > rz1) rz1 = z1;
                if (_cfgOutline.Value && IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ))
                {
                    EmitQuad(rec.X0 - grow, rec.Z0 - grow, rec.X1 + grow, rec.Z1 + grow,
                        _cfgOutlineColor.Value, k, half, h, flip, minPiece + grow * 2f);
                    quads++;
                }
            }

            _fillScratch.Clear();
            if (_cfgOutline.Value) PiecesIntersecting(rx0, rz0, rx1, rz1, false, _fillScratch);
            else _fillScratch.AddRange(group);
            for (int i = 0; i < _fillScratch.Count; i++)
            {
                PieceRec rec = _fillScratch[i];
                if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) continue;
                EmitQuad(rec.X0, rec.Z0, rec.X1, rec.Z1, MatColor(rec.Mat), k, half, h, flip, minPiece);
                quads++;
            }
            return quads;
        }

        // The rectangle a piece actually occupies on the texture: its footprint, grown by the
        // halo and widened to the minimum drawn size, exactly as EmitQuad will draw it.
        private static void DrawnRect(PieceRec rec, float grow, float minWorld, out float x0, out float z0, out float x1, out float z1)
        {
            x0 = rec.X0 - grow; x1 = rec.X1 + grow;
            z0 = rec.Z0 - grow; z1 = rec.Z1 + grow;
            if (x1 - x0 < minWorld) { float m = (x0 + x1) * 0.5f; x0 = m - minWorld * 0.5f; x1 = m + minWorld * 0.5f; }
            if (z1 - z0 < minWorld) { float m = (z0 + z1) * 0.5f; z0 = m - minWorld * 0.5f; z1 = m + minWorld * 0.5f; }
        }

        // Every stored piece whose drawn rectangle (with halo when asked) overlaps the area.
        // Pieces are bucketed by the map pixel of their centre and reach at most half of
        // MaxFootprint plus the halo from it, so only a few buckets around the area matter.
        private void PiecesIntersecting(float x0, float z0, float x1, float z1, bool withHalo, List<PieceRec> result)
        {
            float minPiece = Mathf.Max(0.1f, _cfgPieceSize.Value);
            float grow = _cfgOutline.Value ? Mathf.Max(0f, _cfgOutlineWidth.Value) : 0f;
            float reach = MaxFootprint * 0.5f + grow + minPiece;
            float half = _texSize * 0.5f;
            int px0 = Mathf.Clamp(Mathf.FloorToInt((x0 - reach) / _pixelSize + half), 0, _texSize - 1);
            int px1 = Mathf.Clamp(Mathf.CeilToInt((x1 + reach) / _pixelSize + half), 0, _texSize - 1);
            int py0 = Mathf.Clamp(Mathf.FloorToInt((z0 - reach) / _pixelSize + half), 0, _texSize - 1);
            int py1 = Mathf.Clamp(Mathf.CeilToInt((z1 + reach) / _pixelSize + half), 0, _texSize - 1);

            float g = withHalo ? grow : 0f;
            float minWorld = withHalo ? minPiece + grow * 2f : minPiece;
            for (int py = py0; py <= py1; py++)
            {
                for (int px = px0; px <= px1; px++)
                {
                    List<PieceRec> bucket;
                    if (!_pieces.TryGetValue(py * _texSize + px, out bucket)) continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        PieceRec rec = bucket[i];
                        float ax0, az0, ax1, az1;
                        DrawnRect(rec, g, minWorld, out ax0, out az0, out ax1, out az1);
                        if (ax1 < x0 || ax0 > x1 || az1 < z0 || az0 > z1) continue;
                        result.Add(rec);
                    }
                }
            }
        }

        // Shapes skipped because their ground was unexplored get another look now and then.
        private void ReleaseFromFog()
        {
            if (_rt == null || !_cfgRespectFog.Value) return;
            BitArray explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
            BitArray exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;

            int kept = 0;
            for (int i = 0; i < _fogPieces.Count; i++)
            {
                PieceRec rec = _fogPieces[i];
                if (IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) _addPieces.Add(rec);
                else _fogPieces[kept++] = rec;
            }
            _fogPieces.RemoveRange(kept, _fogPieces.Count - kept);

            float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            kept = 0;
            for (int i = 0; i < _fogTerrain.Count; i++)
            {
                long key = _fogTerrain[i];
                float wx = ((int)(key >> 32) + 0.5f) * grid;
                float wz = ((int)(key & 0xFFFFFFFFL) + 0.5f) * grid;
                if (IsWorldExplored(explored, exploredOthers, wx, wz)) _addTerrain.Add(key);
                else _fogTerrain[kept++] = key;
            }
            _fogTerrain.RemoveRange(kept, _fogTerrain.Count - kept);

            for (int i = 0; i < _fogForest.Count; i++)
            {
                if (IsExplored(explored, exploredOthers, _fogForest[i])) { _maskDirty = true; break; }
            }
        }

        private void RebuildMask()
        {
            if (_rtMask == null || _glMatMask == null || _glMatMaskAdd == null || _vanillaMask == null) return;

            try
            {
                if (!_rtMask.IsCreated() && !_rtMask.Create()) return;

                BitArray explored = null, exploredOthers = null;
                if (_cfgRespectFog.Value)
                {
                    explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                    exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
                }
                bool flip = _cfgMapFlipY.Value;
                _fogForest.Clear();

                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(_vanillaMask, _rtMask);
                RenderTexture.active = _rtMask;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, _rtMask.width, 0f, _rtMask.height);
                // red is the forest channel: multiply by zero to clear it, add one to set it
                int clearedDrawn = 0, plantedDrawn = 0;
                if (_cfgShowForest.Value && _clearedForest.Count > 0)
                    clearedDrawn = DrawMaskPixels(_clearedForest, _glMatMask, new Color(0f, 1f, 1f, 1f), explored, exploredOthers, flip);
                if (_cfgShowPlanted.Value && _plantedForest.Count > 0)
                    plantedDrawn = DrawMaskPixels(_plantedForest, _glMatMaskAdd, new Color(1f, 0f, 0f, 0f), explored, exploredOthers, flip);
                GL.PopMatrix();
                RenderTexture.active = prev;

                if (_cfgDebug.Value)
                    Logger.LogInfo(string.Format("Forest mask rebuilt: {0} cleared, {1} planted, {2} held back by fog",
                        clearedDrawn, plantedDrawn, _fogForest.Count));
            }
            catch (Exception e)
            {
                GpuFailed("forest mask rebuild", e);
            }
        }

        private void GpuFailed(string where, Exception e)
        {
            RenderTexture.active = null;
            _gpuUnavailable = true;
            HandleError(where, e);
            try
            {
                RestoreOriginalTextures();
                ReleaseGpuLayer();
            }
            catch { }
            Logger.LogWarning("The GPU map layer failed and was switched off; restart the world to fall back to CPU painting.");
        }

        private int DrawMaskPixels(HashSet<int> pixels, Material mat, Color c, BitArray explored, BitArray exploredOthers, bool flip)
        {
            int mh = _rtMask.height;
            int drawn = 0;
            mat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(c);
            foreach (int idx in pixels)
            {
                if (_cfgRespectFog.Value && !IsExplored(explored, exploredOthers, idx)) { _fogForest.Add(idx); continue; }
                float x0 = idx % _texSize;
                float y0 = idx / _texSize;
                if (flip) y0 = mh - 1 - y0;
                GL.Vertex3(x0, y0, 0f);
                GL.Vertex3(x0 + 1f, y0, 0f);
                GL.Vertex3(x0 + 1f, y0 + 1f, 0f);
                GL.Vertex3(x0, y0 + 1f, 0f);
                drawn++;
            }
            GL.End();
            return drawn;
        }

        private static void EmitQuad(float wx0, float wz0, float wx1, float wz1, Color c,
                                     float k, float half, int rtHeight, bool flip, float minWorld)
        {
            // The floor has to be expressed in metres. Clamping to one texture pixel instead
            // means a thin wall shrinks as the texture gets sharper, which makes a higher
            // MapTextureScale look worse rather than better.
            if (minWorld > 0f)
            {
                if (wx1 - wx0 < minWorld) { float m = (wx0 + wx1) * 0.5f; wx0 = m - minWorld * 0.5f; wx1 = m + minWorld * 0.5f; }
                if (wz1 - wz0 < minWorld) { float m = (wz0 + wz1) * 0.5f; wz0 = m - minWorld * 0.5f; wz1 = m + minWorld * 0.5f; }
            }

            float x0 = (wx0 / MapPixelSize + half) * k;
            float x1 = (wx1 / MapPixelSize + half) * k;
            float y0 = (wz0 / MapPixelSize + half) * k;
            float y1 = (wz1 / MapPixelSize + half) * k;

            if (x1 - x0 < 1f) { float m = (x0 + x1) * 0.5f; x0 = m - 0.5f; x1 = m + 0.5f; }
            if (y1 - y0 < 1f) { float m = (y0 + y1) * 0.5f; y0 = m - 0.5f; y1 = m + 0.5f; }

            if (flip)
            {
                float t0 = rtHeight - y1;
                float t1 = rtHeight - y0;
                y0 = t0; y1 = t1;
            }

            GL.Color(GlColor(c));
            GL.Vertex3(x0, y0, 0f);
            GL.Vertex3(x1, y0, 0f);
            GL.Vertex3(x1, y1, 0f);
            GL.Vertex3(x0, y1, 0f);
        }

        private static float MapPixelSize;
        private static bool LinearColors;

        private static Color GlColor(Color c)
        {
            // In a linear-space project the value handed to GL.Color is written straight into an
            // sRGB render target, so a plain colour comes back washed out. Pre-converting keeps
            // it looking like the same colour the config asked for.
            if (!LinearColors) return c;
            Color lin = c.linear;
            lin.a = c.a;
            return lin;
        }

        // ------------------------------------------------------------------
        // detailed overlays - drawn in map-view space, so they stay sharp when zoomed in
        // ------------------------------------------------------------------
        private void UpdateOverlays()
        {
            if (_mm == null || !_cfgHiRes.Value)
            {
                HideLayer(_layerLarge);
                HideLayer(_layerSmall);
                return;
            }

            UpdateLayer(_layerLarge, _mm.m_mapImageLarge, _mm.m_largeRoot,
                _mm.m_pinRootLarge, _cfgHiResSize.Value, true);

            if (_cfgMinimapOverlay.Value)
                UpdateLayer(_layerSmall, _mm.m_mapImageSmall, _mm.m_smallRoot,
                    _mm.m_pinRootSmall, _cfgMinimapSize.Value, false);
            else
                HideLayer(_layerSmall);
        }

        private static void HideLayer(Layer layer)
        {
            if (layer.Go != null && layer.Go.activeSelf) layer.Go.SetActive(false);
        }

        private void UpdateLayer(Layer layer, RawImage src, GameObject root, RectTransform pinRoot, int size, bool isLarge)
        {
            if (src == null || root == null || !root.activeInHierarchy)
            {
                HideLayer(layer);
                return;
            }

            EnsureLayer(layer, src, pinRoot, size);
            if (layer.Tex == null || layer.Image == null) return;

            // affine basis: world (x,z) -> local gui position inside this map image
            Vector2 gOrigin, gX, gZ;
            if (!WorldToGui(src, Vector3.zero, out gOrigin)) return;
            if (!WorldToGui(src, new Vector3(1000f, 0f, 0f), out gX)) return;
            if (!WorldToGui(src, new Vector3(0f, 0f, 1000f), out gZ)) return;

            float sx = (gX.x - gOrigin.x) / 1000f;
            float sz = (gZ.y - gOrigin.y) / 1000f;
            if (Mathf.Abs(sx) < 1e-8f || Mathf.Abs(sz) < 1e-8f) return;

            bool viewChanged = Mathf.Abs(sx - layer.LastScale) > 1e-7f ||
                               (gOrigin - layer.LastOrigin).sqrMagnitude > 0.01f;
            if (!viewChanged && _pending.Count == 0 && layer.Redraws > 0)
            {
                layer.Go.SetActive(true);
                return;
            }

            layer.LastScale = sx;
            layer.LastOrigin = gOrigin;

            // MapPointToLocalGuiPos returns 0..rect.width / 0..rect.height measured from the
            // image's lower-left corner, using the MAP image's own rect.
            Rect rect = src.rectTransform.rect;
            if (rect.width <= 1f || rect.height <= 1f) return;

            int texW = layer.Tex.width, texH = layer.Tex.height;
            Color32[] buf = layer.Buf;
            float pxPerGuiX = texW / rect.width;
            float pxPerGuiY = texH / rect.height;

            Array.Clear(buf, 0, buf.Length);

            float wx0 = (0f - gOrigin.x) / sx;
            float wx1 = (rect.width - gOrigin.x) / sx;
            float wz0 = (0f - gOrigin.y) / sz;
            float wz1 = (rect.height - gOrigin.y) / sz;
            if (wx0 > wx1) { float t = wx0; wx0 = wx1; wx1 = t; }
            if (wz0 > wz1) { float t = wz0; wz0 = wz1; wz1 = t; }

            BitArray explored = null, exploredOthers = null;
            if (_cfgRespectFog.Value)
            {
                explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
            }

            int drawn = 0;

            if (_cfgShowPaths.Value && _terrain.Count > 0)
            {
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                int halfW = Mathf.Max(0, Mathf.RoundToInt(grid * Mathf.Abs(sx) * pxPerGuiX * 0.5f));
                int halfH = Mathf.Max(0, Mathf.RoundToInt(grid * Mathf.Abs(sz) * pxPerGuiY * 0.5f));
                foreach (KeyValuePair<long, byte> kv in _terrain)
                {
                    int gx = (int)(kv.Key >> 32);
                    int gz = (int)(kv.Key & 0xFFFFFFFFL);
                    float wx = (gx + 0.5f) * grid;
                    float wz = (gz + 0.5f) * grid;
                    if (wx < wx0 || wx > wx1 || wz < wz0 || wz > wz1) continue;
                    if (!IsWorldExplored(explored, exploredOthers, wx, wz)) continue;

                    int px = Mathf.RoundToInt((gOrigin.x + wx * sx) * pxPerGuiX);
                    int py = Mathf.RoundToInt((gOrigin.y + wz * sz) * pxPerGuiY);
                    Stamp(buf, px, py, halfW, halfH, TerrainColor(kv.Value), texW, texH);
                    drawn++;
                }
            }

            if (_cfgShowBuildings.Value && _pieces.Count > 0)
            {
                float minSize = Mathf.Max(0.1f, _cfgPieceSize.Value);
                float pxPerMetreX = Mathf.Abs(sx) * pxPerGuiX;
                float pxPerMetreY = Mathf.Abs(sz) * pxPerGuiY;
                int minHalfW = Mathf.Max(0, Mathf.RoundToInt(minSize * pxPerMetreX * 0.5f));
                int minHalfH = Mathf.Max(0, Mathf.RoundToInt(minSize * pxPerMetreY * 0.5f));

                foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                {
                    List<PieceRec> bucket = kv.Value;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        PieceRec rec = bucket[i];
                        if (rec.X1 < wx0 || rec.X0 > wx1 || rec.Z1 < wz0 || rec.Z0 > wz1) continue;
                        if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) continue;

                        int cpx = Mathf.RoundToInt((gOrigin.x + rec.CX * sx) * pxPerGuiX);
                        int cpy = Mathf.RoundToInt((gOrigin.y + rec.CZ * sz) * pxPerGuiY);
                        int halfW = Mathf.Max(minHalfW, Mathf.RoundToInt((rec.X1 - rec.X0) * pxPerMetreX * 0.5f));
                        int halfH = Mathf.Max(minHalfH, Mathf.RoundToInt((rec.Z1 - rec.Z0) * pxPerMetreY * 0.5f));
                        Stamp(buf, cpx, cpy, halfW, halfH, MatColor(rec.Mat), texW, texH);
                        drawn++;
                    }
                }
            }

            if (_cfgDebugMarker.Value && isLarge && Player.m_localPlayer != null)
            {
                Vector3 pp = Player.m_localPlayer.transform.position;
                int px = Mathf.RoundToInt((gOrigin.x + pp.x * sx) * pxPerGuiX);
                int py = Mathf.RoundToInt((gOrigin.y + pp.z * sz) * pxPerGuiY);
                Color32 magenta = new Color32(255, 0, 255, 255);
                for (int d = -6; d <= 6; d++)
                {
                    Stamp(buf, px + d, py, 0, 0, magenta, texW, texH);
                    Stamp(buf, px, py + d, 0, 0, magenta, texW, texH);
                }
            }

            // nothing here and nothing last time: keep the texture as it is
            if (drawn == 0 && layer.LastDrawn == 0)
            {
                layer.Go.SetActive(false);
                return;
            }
            layer.LastDrawn = drawn;

            layer.Tex.SetPixels32(buf);
            layer.Tex.Apply(false);
            layer.Go.SetActive(true);
            layer.Redraws++;

            if (!_announcedFirstDraw && drawn > 0)
            {
                _announcedFirstDraw = true;
                Logger.LogInfo(string.Format(
                    "Detailed overlay drew for the first time on {0}: {1} shapes, texture {2}px, rect {3:0}x{4:0}, scale {5:0.####} gui/m",
                    layer.Name, drawn, texW, rect.width, rect.height, sx));
            }

            if (_cfgDebug.Value && (layer.Redraws % 40 == 1))
                Logger.LogInfo(string.Format("{0} overlay: drawn={1}, world x[{2:0}..{3:0}] z[{4:0}..{5:0}], {6:0.###} px/m",
                    layer.Name, drawn, wx0, wx1, wz0, wz1, Mathf.Abs(sx) * pxPerGuiX));
        }

        private bool IsWorldExplored(BitArray explored, BitArray exploredOthers, float wx, float wz)
        {
            if (!_cfgRespectFog.Value) return true;
            int idx;
            if (!WorldToIndex(new Vector3(wx, 0f, wz), out idx)) return false;
            return IsExplored(explored, exploredOthers, idx);
        }

        private static void Stamp(Color32[] buf, int cx, int cy, int halfW, int halfH, Color32 c, int texW, int texH)
        {
            int x0 = cx - halfW, x1 = cx + halfW;
            int y0 = cy - halfH, y1 = cy + halfH;
            if (x1 < 0 || y1 < 0 || x0 >= texW || y0 >= texH) return;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 >= texW) x1 = texW - 1;
            if (y1 >= texH) y1 = texH - 1;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * texW;
                for (int x = x0; x <= x1; x++) buf[row + x] = c;
            }
        }

        private bool WorldToGui(RawImage img, Vector3 world, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (_miWorldToMapPoint == null || _miMapPointToLocalGuiPos == null || img == null) return false;

            object[] a = { world, 0f, 0f };
            _miWorldToMapPoint.Invoke(_mm, a);
            object res = _miMapPointToLocalGuiPos.Invoke(_mm, new object[] { a[1], a[2], img });
            if (!(res is Vector2)) return false;
            gui = (Vector2)res;
            return true;
        }

        private void EnsureLayer(Layer layer, RawImage src, RectTransform pinRoot, int size)
        {
            if (layer.Tex != null && layer.Tex.width != size) DestroyLayer(layer);

            if (layer.Tex == null)
            {
                layer.Tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                layer.Tex.name = "MapOverlay_" + layer.Name;
                layer.Tex.wrapMode = TextureWrapMode.Clamp;
                layer.Buf = new Color32[size * size];
                layer.Redraws = 0;
                layer.LastDrawn = -1;
                layer.LastScale = float.MinValue;
                layer.LastOrigin = new Vector2(float.MinValue, float.MinValue);
            }

            layer.Tex.filterMode = _cfgSmoothOverlay.Value ? FilterMode.Bilinear : FilterMode.Point;

            if (layer.Go == null)
            {
                layer.Go = new GameObject("MapOverlay_" + layer.Name,
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));

                // A child of the map image: it inherits the map's mask and transform, sits right
                // on top of the map itself, and stays BELOW the pin layer so player and pin
                // markers keep drawing over it.
                RectTransform rt = layer.Go.GetComponent<RectTransform>();
                rt.SetParent(src.rectTransform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;

                if (pinRoot != null && pinRoot.parent == rt.parent)
                    rt.SetSiblingIndex(pinRoot.GetSiblingIndex());

                layer.Image = layer.Go.GetComponent<RawImage>();
                layer.Image.texture = layer.Tex;
                layer.Image.raycastTarget = false;
            }
            else
            {
                layer.Image.texture = layer.Tex;
            }

            float a = Mathf.Clamp01(_cfgOverlayOpacity.Value);
            layer.Image.color = new Color(1f, 1f, 1f, a);
        }

        private static void DestroyLayer(Layer layer)
        {
            if (layer.Go != null)
            {
                try { UnityEngine.Object.Destroy(layer.Go); } catch { }
                layer.Go = null;
                layer.Image = null;
            }
            if (layer.Tex != null)
            {
                try { UnityEngine.Object.Destroy(layer.Tex); } catch { }
                layer.Tex = null;
            }
            layer.Buf = null;
            layer.Redraws = 0;
            layer.LastDrawn = -1;
        }

        private void DestroyHiRes()
        {
            DestroyLayer(_layerLarge);
            DestroyLayer(_layerSmall);
            _announcedFirstDraw = false;
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        private bool WorldToIndex(Vector3 world, out int idx)
        {
            idx = -1;
            if (_texSize <= 0 || _pixelSize <= 0f) return false;
            float half = _texSize * 0.5f;
            int px = Mathf.RoundToInt(world.x / _pixelSize + half);
            int py = Mathf.RoundToInt(world.z / _pixelSize + half);
            if (px < 0 || py < 0 || px >= _texSize || py >= _texSize) return false;
            idx = py * _texSize + px;
            return true;
        }

        private void CollectPixelsInCircle(Vector3 center, float radius, List<int> result)
        {
            result.Clear();
            float half = _texSize * 0.5f;
            int cx = Mathf.RoundToInt(center.x / _pixelSize + half);
            int cy = Mathf.RoundToInt(center.z / _pixelSize + half);
            int pr = Mathf.CeilToInt(radius / _pixelSize) + 1;
            float r2 = radius * radius;

            for (int y = cy - pr; y <= cy + pr; y++)
            {
                if (y < 0 || y >= _texSize) continue;
                for (int x = cx - pr; x <= cx + pr; x++)
                {
                    if (x < 0 || x >= _texSize) continue;
                    float wx = (x - half) * _pixelSize;
                    float wz = (y - half) * _pixelSize;
                    float dx = wx - center.x, dz = wz - center.z;
                    if (dx * dx + dz * dz > r2) continue;
                    result.Add(y * _texSize + x);
                }
            }
        }

        // ------------------------------------------------------------------
        // persistence
        // ------------------------------------------------------------------
        private string StorePath()
        {
            return Path.Combine(Path.Combine(Paths.ConfigPath, "MapOverlay"), _worldUid + ".bin");
        }

        private void LoadStore()
        {
            try
            {
                if (_worldUid == 0L) return;
                string path = StorePath();
                if (!File.Exists(path)) return;

                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (br.ReadUInt32() != 0x334F4D4Du) return;   // "MMO3"
                    int version = br.ReadInt32();
                    if (version < 3 || version > 5) return;
                    long uid = br.ReadInt64();
                    int texSize = br.ReadInt32();
                    float pixelSize = br.ReadSingle();
                    if (uid != _worldUid || texSize != _texSize || Mathf.Abs(pixelSize - _pixelSize) > 0.001f)
                    {
                        Logger.LogInfo("Stored overlay does not match this map, starting fresh.");
                        return;
                    }

                    int pieceCount = br.ReadInt32();
                    for (int i = 0; i < pieceCount; i++)
                    {
                        PieceRec rec = new PieceRec();
                        rec.X0 = br.ReadSingle();
                        rec.X1 = br.ReadSingle();
                        rec.Z0 = br.ReadSingle();
                        rec.Z1 = br.ReadSingle();
                        rec.Y = br.ReadSingle();
                        rec.Mat = br.ReadByte();
                        if (_pieceCount >= _cfgMaxPieces.Value) continue;
                        int idx;
                        if (!WorldToIndex(new Vector3(rec.CX, rec.Y, rec.CZ), out idx)) continue;
                        List<PieceRec> bucket;
                        if (!_pieces.TryGetValue(idx, out bucket))
                        {
                            bucket = new List<PieceRec>();
                            _pieces[idx] = bucket;
                        }
                        bucket.Add(rec);
                        _pieceCount++;
                    }

                    int terrainCount = br.ReadInt32();
                    for (int i = 0; i < terrainCount; i++)
                    {
                        long key = br.ReadInt64();
                        byte kind = br.ReadByte();
                        if (kind == TerrainNone) continue;
                        if (_terrain.Count >= _cfgMaxTerrain.Value) continue;
                        _terrain[key] = kind;
                    }

                    int maxIdx = _texSize * _texSize;
                    if (version >= 4)
                    {
                        int clearedCount = br.ReadInt32();
                        for (int i = 0; i < clearedCount; i++)
                        {
                            int idx = br.ReadInt32();
                            if (idx >= 0 && idx < maxIdx) _clearedForest.Add(idx);
                        }
                    }
                    if (version >= 5)
                    {
                        int plantedCount = br.ReadInt32();
                        for (int i = 0; i < plantedCount; i++)
                        {
                            int idx = br.ReadInt32();
                            if (idx >= 0 && idx < maxIdx) _plantedForest.Add(idx);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read the stored overlay, starting fresh: " + e.Message);
                _pieces.Clear();
                _terrain.Clear();
                _clearedForest.Clear();
                _plantedForest.Clear();
                _pieceCount = 0;
            }
        }

        private void SaveStore()
        {
            if (!_cfgPersist.Value || _worldUid == 0L || _texSize <= 0 || !_storeChanged) return;
            try
            {
                string path = StorePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(0x334F4D4Du);
                    bw.Write(5);
                    bw.Write(_worldUid);
                    bw.Write(_texSize);
                    bw.Write(_pixelSize);

                    bw.Write(_pieceCount);
                    foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                    {
                        List<PieceRec> bucket = kv.Value;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            bw.Write(bucket[i].X0);
                            bw.Write(bucket[i].X1);
                            bw.Write(bucket[i].Z0);
                            bw.Write(bucket[i].Z1);
                            bw.Write(bucket[i].Y);
                            bw.Write(bucket[i].Mat);
                        }
                    }

                    bw.Write(_terrain.Count);
                    foreach (KeyValuePair<long, byte> kv in _terrain)
                    {
                        bw.Write(kv.Key);
                        bw.Write(kv.Value);
                    }

                    bw.Write(_clearedForest.Count);
                    foreach (int idx in _clearedForest) bw.Write(idx);

                    bw.Write(_plantedForest.Count);
                    foreach (int idx in _plantedForest) bw.Write(idx);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _storeChanged = false;

                if (_cfgDebug.Value)
                    Logger.LogInfo("Overlay saved: " + _pieceCount + " pieces, " + _terrain.Count + " terrain cells, forest pixels cleared " + _clearedForest.Count + " / planted " + _plantedForest.Count + ".");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not save the overlay: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // errors
        // ------------------------------------------------------------------
        private void LogOnce(string key, string message)
        {
            if (_loggedErrors.Add("once:" + key)) Logger.LogWarning(message);
        }

        private void HandleError(string where, Exception e)
        {
            _errorCount++;
            string key = where + "|" + (e != null ? e.GetType().Name + "|" + e.Message : "null");
            if (_loggedErrors.Add(key)) Logger.LogError("[" + where + "] " + e);

            if (_errorCount >= MaxErrors && !_disabledByErrors)
            {
                _disabledByErrors = true;
                Logger.LogError("Too many errors, Map Overlay is switching itself off for this session. The game is unaffected.");
                try { Teardown(true); } catch { }
            }
        }
    }
}
