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
    [BepInPlugin(Guid, Name, Version)]
    public partial class LivingMapPlugin : BaseUnityPlugin
    {
        public const string Guid = "j1ga.livingmap";
        public const string Name = "Living Map";
        public const string Version = "0.15.0";

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
        // state
        // ------------------------------------------------------------------
        // buildings, bucketed by map-pixel index so we can cull by area quickly
        private readonly Dictionary<int, List<PieceRec>> _pieces = new Dictionary<int, List<PieceRec>>();
        private int _pieceCount;

        // terrain, on its own metric grid: key = (gx << 32) | gz
        private readonly Dictionary<long, byte> _terrain = new Dictionary<long, byte>();

        private readonly List<int> _circleScratch = new List<int>();
        private readonly Dictionary<int, List<PieceRec>> _scanBuckets = new Dictionary<int, List<PieceRec>>();
        private readonly List<long> _terrainScratch = new List<long>();

        private Minimap _mm;
        private Texture2D _vanillaTex;
        private Texture _origSmallTex;
        private Texture _origLargeTex;
        private int _texSize;
        private float _pixelSize;
        private bool _ready;

        private Vector3 _lastScanPos;
        private float _nextScanTime, _lastScanTime, _nextSaveTime, _nextRefogTime;
        private const float RefogInterval = 15f;
        private const float MaxFootprint = 32f;

        private FieldInfo _fiExplored, _fiExploredOthers;

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
                MigrateFromMapOverlay();
                HashSet<ConfigDefinition> inFile = SettingsInFile();   // before binding: what the file holds
                BindConfig();
                MigrateConfigLayout(inFile);
                RegisterCommands();
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

                Logger.LogInfo(Name + " " + Version + " loaded. No Harmony patches are applied.");
            }
            catch (Exception e)
            {
                Logger.LogError("Awake failed, mod is inert: " + e);
                _disabledByErrors = true;
            }
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
            if (!_oldPluginChecked && OldPluginLoaded()) return;

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

            bool mapLayerMissing = _rt == null;
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
                bool moved = (pos - _lastScanPos).sqrMagnitude >= ScanMoveDelta * ScanMoveDelta;
                bool idleDue = now >= _lastScanTime + IdleRescanInterval;
                if (moved || idleDue)
                {
                    _nextScanTime = now + ScanInterval;
                    _lastScanTime = now;
                    _lastScanPos = pos;
                    Scan(pos);
                }
                else _nextScanTime = now + 0.25f;
            }

            if (UseZdoScan && (_cfgShowBuildings.Value || _treeCount != null || (UseZdoPaths && _cfgShowPaths.Value))) StepZdoScan(now, pos);

            if (now >= _nextRefogTime && (_fogPieces.Count > 0 || _fogTerrain.Count > 0 || _fogForest.Count > 0))
            {
                _nextRefogTime = now + RefogInterval;
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
                    _nextRtRebuild = now + MapRebuildInterval;
                    if (_rtDirty || (adds && (!_cfgIncremental.Value || _cfgDebugMarker.Value ||
                                              _addPieces.Count > MaxIncrementalPieces || _addTerrain.Count > MaxIncrementalPieces)))
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

            if (_cfgPersist.Value && _storeChanged && now >= _nextSaveTime)
            {
                _nextSaveTime = now + SaveInterval;
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
            _colliderBuf = new Collider[MaxColliders];

            _worldUid = 0L;
            try { if (ZNet.instance != null) _worldUid = ZNet.instance.GetWorldUID(); }
            catch (Exception e) { HandleError("Setup/GetWorldUID", e); }

            _pieces.Clear();
            _terrain.Clear();
            _pieceCount = 0;
            _storeChanged = false;

            _piecesOnlyPlayerBuilt = _cfgOnlyPlayerBuilt.Value;
            if (_cfgPersist.Value) LoadStore();

            ResetZdoPass();
            _zdoHavePrevOrigin = false;
            _nextZdoPass = 0f;
            InitForestLayer();

            // The map layer lives on the graphics card and nowhere else. Without it there is
            // nothing to draw into, so the mod stays out of the way for the session.
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
            if (!gpu)
            {
                Logger.LogWarning("The map layer could not be created on the graphics card; Living Map is off for this session and the vanilla map is untouched.");
                Teardown(true);
                _disabledByErrors = true;
                return false;
            }

            ApplyTextureToMap(_rt, true);
            if (_rtMask != null) ApplyMaskToMap(_rtMask, true);
            else DropForestData();
            PokeMinimap();
            _rtDirty = true;
            _maskDirty = _rtMask != null;
            _nextRtRebuild = 0f;

            _lastScanPos = new Vector3(float.MinValue, 0f, float.MinValue);
            _nextScanTime = 0f; _lastScanTime = 0f;
            _nextSaveTime = Time.realtimeSinceStartup + SaveInterval;
            _ready = true;

            Logger.LogInfo(string.Format(
                "Attached to map: vanilla {0}px at {1} m/px; map layer {2}; buildings via {3}; forest layers {4}; world={5}, restored pieces={6}, terrain cells={7}, forest pixels cleared={8} planted={9}",
                _texSize, _pixelSize.ToString("0.##"),
                _rt.width + "px on the GPU = " + (_pixelSize / _rtScale).ToString("0.##") + " m/px",
                UseZdoScan ? "the object database (ZDO)" : "physics",
                (_treeCount != null ? "on" : "off") + "; paths via " + (UseZdoPaths ? "terrain records (ZDO)" : "loaded heightmaps"),
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

            ReleaseGpuLayer();

            _mm = null; _vanillaTex = null; _origSmallTex = null; _origLargeTex = null;
            _vanillaMask = null; _origSmallMask = null; _origLargeMask = null;
            DropForestData(); _clearedForest.Clear(); _plantedForest.Clear(); _trustedZones.Clear(); _forestBiome.Clear();
            _colliderBuf = null; _texSize = 0; _pixelSize = 0f; _ready = false;

            _pieces.Clear(); _terrain.Clear(); _pieceCount = 0;
            _circleScratch.Clear(); _scanBuckets.Clear(); _terrainScratch.Clear();
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
        // shared helpers
        // ------------------------------------------------------------------

        private Color TerrainColor(byte kind)
        {
            if (kind == TerrainPaved) return _cfgPavedColor.Value;
            if (kind == TerrainDirt) return _cfgDirtColor.Value;
            if (kind == TerrainCultivated) return _cfgCultivatedColor.Value;
            return _cfgClearedColor.Value;
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

        private bool IsWorldExplored(BitArray explored, BitArray exploredOthers, float wx, float wz)
        {
            if (!_cfgRespectFog.Value) return true;
            int idx;
            if (!WorldToIndex(new Vector3(wx, 0f, wz), out idx)) return false;
            return IsExplored(explored, exploredOthers, idx);
        }

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
        // the old name: this mod was MapOverlay (j1ga.mapoverlay) up to 0.12.0
        // ------------------------------------------------------------------
        private const string OldGuid = "j1ga.mapoverlay";
        private const string OldFolder = "MapOverlay";
        private bool _oldPluginChecked;

        // The config file is named after the GUID and the data folder after the mod, so both
        // moved with the rename. Old files are copied, never deleted, on the first start.
        private void MigrateFromMapOverlay()
        {
            try
            {
                string oldCfg = Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                if (!File.Exists(Config.ConfigFilePath) && File.Exists(oldCfg))
                {
                    File.Copy(oldCfg, Config.ConfigFilePath);
                    Config.Reload();
                    Logger.LogInfo("Copied the MapOverlay settings to " + Path.GetFileName(Config.ConfigFilePath) + ".");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not carry the MapOverlay settings over: " + e.Message);
            }
        }

        private void MigrateStoreFromMapOverlay(string path)
        {
            try
            {
                string old = Path.Combine(Path.Combine(Paths.ConfigPath, OldFolder), _worldUid + ".bin");
                if (File.Exists(path) || !File.Exists(old)) return;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(old, path);
                Logger.LogInfo("Copied the MapOverlay data for this world to " + path + ".");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not carry the MapOverlay data over: " + e.Message);
            }
        }

        // Both plugins would fight over the map texture. The old DLL has to go.
        private bool OldPluginLoaded()
        {
            _oldPluginChecked = true;
            bool old = false;
            try { old = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(OldGuid); } catch { }
            if (!old) return false;
            _disabledByErrors = true;
            Logger.LogError("The old MapOverlay plugin is loaded as well. Living Map is the same mod renamed; " +
                            "remove BepInEx\\plugins\\MapOverlay\\MapOverlay.dll. Living Map stays inert until then.");
            return true;
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
                Logger.LogError("Too many errors, Living Map is switching itself off for this session. The game is unaffected.");
                try { Teardown(true); } catch { }
            }
        }
    }
}
