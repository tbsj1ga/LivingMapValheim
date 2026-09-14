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
        public const string Version = "0.8.0";

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
        private ConfigEntry<bool> _cfgHiRes;

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
        private bool _rtDirty;
        private float _nextRtRebuild;
        private int _rtScale = 1;
        private int _rtRebuilds;

        private Collider[] _colliderBuf;
        private int _pieceLayerMask;
        private readonly Color32[] _onePixel = new Color32[1];

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
                new ConfigDescription("Minimum seconds between map-texture rebuilds after something changes.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            _cfgMapFlipY = Config.Bind("02 Layers", "MapLayerFlipY", false,
                "Flip the drawn shapes vertically. Only needed if the graphics API renders the map layer upside down - set it once and the whole layer lines up.");
            _cfgHiRes = Config.Bind("02 Layers", "DetailedOverlay", false,
                "Extra layer drawn on top of the map in screen space. It is the sharpest option when zoomed right in, but it is a separate layer rather than part of the map. Off by default.");

            _cfgScanRadius = Config.Bind("03 Scanning", "ScanRadius", 64f,
                new ConfigDescription("Radius in metres scanned around the player. Objects exist only while their zone is loaded, so much above ~128 gains nothing.",
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
            if (!_ready || _mm != mm || mapLayerMissing || mm.m_mapTexture == null ||
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

            if (_deferredByFog.Count > 0 && now >= _nextRefogTime)
            {
                _nextRefogTime = now + RefogInterval;
                foreach (int idx in _deferredByFog) _pending.Add(idx);
                _deferredByFog.Clear();
            }

            if (_rt != null)
            {
                if (!_rt.IsCreated()) _rtDirty = true;      // contents lost, e.g. after alt-tab
                if (_rtDirty && now >= _nextRtRebuild)
                {
                    _nextRtRebuild = now + _cfgMapRebuildInterval.Value;
                    _rtDirty = false;
                    RebuildGpuLayer();
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
                    _rtDirty = true;
                    _nextRtRebuild = 0f;
                }
                else
                {
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
                    QueueAllPixels();
                    Logger.LogWarning("Falling back to CPU map painting at vanilla resolution.");
                }
            }

            _lastScanPos = new Vector3(float.MinValue, 0f, float.MinValue);
            _nextScanTime = 0f; _lastScanTime = 0f; _nextFlushTime = 0f; _nextHiResTime = 0f;
            _nextSaveTime = Time.realtimeSinceStartup + _cfgSaveInterval.Value;
            _ready = true;

            Logger.LogInfo(string.Format(
                "Attached to map: vanilla {0}px at {1} m/px; map layer {2}; world={3}, restored pieces={4}, terrain cells={5}",
                _texSize, _pixelSize.ToString("0.##"),
                _rt != null
                    ? _rt.width + "px on the GPU = " + (_pixelSize / _rtScale).ToString("0.##") + " m/px"
                    : (_ourTex != null ? "CPU " + _texSize + "px" : "off"),
                _worldUid, _pieceCount, _terrain.Count));
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
            _colliderBuf = null; _texSize = 0; _pixelSize = 0f; _ready = false;

            _pieces.Clear(); _terrain.Clear(); _pieceCount = 0;
            _pending.Clear(); _deferredByFog.Clear(); _paintedPixels.Clear();
            _circleScratch.Clear(); _scanBuckets.Clear(); _terrainScratch.Clear(); _pendingScratch.Clear();
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
            if (_mm.m_smallRoot != null && _mm.m_smallRoot.activeSelf)
            {
                _mm.m_smallRoot.SetActive(false);
                _mm.m_smallRoot.SetActive(true);
            }
        }

        private void RestoreOriginalTextures()
        {
            if (_mm == null) return;
            if (_mm.m_mapImageSmall != null && _mm.m_mapImageSmall.material != null)
                _mm.m_mapImageSmall.material.SetTexture("_MainTex", _origSmallTex != null ? _origSmallTex : _vanillaTex);
            if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
                _mm.m_mapImageLarge.material.SetTexture("_MainTex", _origLargeTex != null ? _origLargeTex : _vanillaTex);
            if (_mm.m_smallRoot != null && _mm.m_smallRoot.activeSelf)
            {
                _mm.m_smallRoot.SetActive(false);
                _mm.m_smallRoot.SetActive(true);
            }
        }

        // ------------------------------------------------------------------
        // scanning
        // ------------------------------------------------------------------
        private void Scan(Vector3 center)
        {
            bool changed = false;
            if (_cfgShowBuildings.Value) changed |= ScanBuildings(center);
            if (_cfgShowPaths.Value && !_terrainDisabled) changed |= ScanTerrain(center);
            if (changed) { _storeChanged = true; _rtDirty = true; }
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
                            if (had) { _terrain.Remove(key); changed = true; MarkPixelDirty(wx, wz); }
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
            RebuildGpuLayer();
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
            _rtScale = 1;
            _rtDirty = false;
        }

        private void RebuildGpuLayer()
        {
            if (_rt == null || _glMat == null || _vanillaTex == null) return;

            try
            {
                if (!_rt.IsCreated() && !_rt.Create()) return;

                // the vanilla map, upscaled on the card - no managed memory involved
                Graphics.Blit(_vanillaTex, _rt);

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
                        if (!IsWorldExplored(explored, exploredOthers, wx, wz)) continue;
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
                            if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) continue;
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
                        "Map layer rebuilt on the GPU: {0}px ({1:0.##} m/px), {2} shapes drawn, {3:0} MB of video memory, linear colours {4}",
                        _rt.width, _pixelSize / _rtScale, quads,
                        _rt.width * (long)_rt.height * 4L / (1024f * 1024f),
                        LinearColors ? "on" : "off"));
            }
            catch (Exception e)
            {
                RenderTexture.active = null;
                _gpuUnavailable = true;
                HandleError("GPU map layer rebuild", e);
                try
                {
                    RestoreOriginalTextures();
                    ReleaseGpuLayer();
                }
                catch { }
                Logger.LogWarning("The GPU map layer failed and was switched off; restart the world to fall back to CPU painting.");
            }
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
                    if (br.ReadInt32() != 3) return;
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
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read the stored overlay, starting fresh: " + e.Message);
                _pieces.Clear();
                _terrain.Clear();
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
                    bw.Write(3);
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
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _storeChanged = false;

                if (_cfgDebug.Value)
                    Logger.LogInfo("Overlay saved: " + _pieceCount + " pieces, " + _terrain.Count + " terrain cells.");
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
