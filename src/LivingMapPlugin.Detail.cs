using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace LivingMap
{
    // Detail layer: when the big map is zoomed in, tiles of 4 / 2 / 1 m per pixel are laid over
    // it - relief shaded from the world generator's heights, water by depth, the vanilla forest
    // rule with this mod's cleared/planted corrections, and the paths and buildings the mod
    // already collects. Zoomed out and on the minimap nothing changes.
    //
    // Tiles are drawn on background threads from a snapshot taken on the main thread (the
    // threads never touch the mod's collections) and uploaded at most two per frame. They sit
    // in a UI layer under the map's pins, positioned from the large map image's uvRect in
    // LateUpdate. Any failure switches off this layer only.
    public partial class LivingMapPlugin
    {
        private const string SecDetail = "08 Detail";
        private ConfigEntry<bool> _cfgDetail;
        private ConfigEntry<float> _cfgDetailStartSpan;
        private ConfigEntry<float> _cfgDetailFinest;
        private ConfigEntry<float> _cfgDetailRelief;
        private ConfigEntry<float> _cfgDetailForestShade;
        private ConfigEntry<Color> _cfgDetailWaterShallow;
        private ConfigEntry<Color> _cfgDetailWaterDeep;
        private ConfigEntry<int> _cfgDetailWorkers;

        private void BindDetailConfig()
        {
            _cfgDetail = Config.Bind(SecDetail, "DetailEnabled", true,
                "When the big map is zoomed in, draw a detailed picture over it: shaded relief, water by depth, forest, paths and buildings, down to 1 m per pixel. Zoomed out and on the minimap nothing changes.");
            _cfgDetailStartSpan = Config.Bind(SecDetail, "DetailStartSpanMeters", 3000f,
                new ConfigDescription("The detailed picture fades in once the map window shows at most this many metres across (fully visible at 80% of it).",
                    new AcceptableValueRange<float>(500f, 8000f)));
            _cfgDetailFinest = Config.Bind(SecDetail, "DetailFinestMetersPerPixel", 1f,
                new ConfigDescription("The finest level drawn: 1, 2 or 4 metres per pixel. Coarser = fewer tiles to draw.",
                    new AcceptableValueList<float>(1f, 2f, 4f)));
            _cfgDetailRelief = Config.Bind(SecDetail, "DetailRelief", 1f,
                new ConfigDescription("Strength of the hill shading. 0 = flat colours.", new AcceptableValueRange<float>(0f, 3f)));
            _cfgDetailForestShade = Config.Bind(SecDetail, "DetailForestShade", 0.35f,
                new ConfigDescription("How much darker forest is drawn. 0 = forest not shown.", new AcceptableValueRange<float>(0f, 0.9f)));
            _cfgDetailWaterShallow = Config.Bind(SecDetail, "DetailWaterShallow", new Color(0.36f, 0.50f, 0.56f, 1f),
                "Colour of shallow water in the detailed picture.");
            _cfgDetailWaterDeep = Config.Bind(SecDetail, "DetailWaterDeep", new Color(0.14f, 0.22f, 0.30f, 1f),
                "Colour of deep water in the detailed picture.");
            _cfgDetailWorkers = Config.Bind(SecDetail, "DetailWorkerThreads", 2,
                new ConfigDescription("Background threads that draw tiles. Takes effect on the next world load.", new AcceptableValueRange<int>(1, 4)));
        }

        // ------------------------------------------------------------------
        // model
        // ------------------------------------------------------------------
        private const int DTileSize = 256;
        private const int DCacheLimit = 96;
        private const int DVisibleLimit = 64;
        private const int DUploadsPerFrame = 2;
        private const float DRedrawMinAge = 15f;
        private static readonly float[] DLevels = { 4f, 2f, 1f };

        private class DTile
        {
            public int Level, TX, TZ;
            public long Key;
            public Texture2D Tex;
            public RawImage Img;
            public bool Ready, Queued, Failed;
            public int Rev, FogSig;
            public float RenderedAt, LastUsed;
        }

        private class DJob
        {
            public DTile Tile;
            public float X0, Z0, Mpp;
            public float Priority;
            // snapshot
            public float WaterLevel, Relief, ForestShade;
            public Color32 WaterShallow, WaterDeep;
            public Color32[] Biome;                 // index = biome bit position
            public bool[] Fog; public int FogX0, FogZ0, FogW, FogH; public float PixelSize; public int TexSize;
            public sbyte[] ForestFix;               // same grid as Fog: -1 cleared, +1 planted
            public List<PieceRec> Pieces;
            public Color32[] MatColors; public Color32 MatUnknown, Outline; public bool DrawOutline;
            public List<long> TerrKeys; public List<byte> TerrKinds; public float Grid;
            public Color32 Paved, Dirt, Cultivated, Cleared;
            public int Rev, FogSig;
            // result
            public byte[] Pixels;
            public double Ms;
        }

        private readonly Dictionary<long, DTile> _dTiles = new Dictionary<long, DTile>();
        private readonly List<DJob> _dPending = new List<DJob>();
        private readonly List<DJob> _dDone = new List<DJob>();
        private readonly object _dLock = new object();
        private Thread[] _dWorkers;
        private volatile bool _dStop;
        private bool _dBroken;
        private RectTransform _dRoot, _dBack, _dFront;
        private CanvasGroup _dGroup;
        private int _dRev, _dDataSig;
        private int _dFogCursor;
        private readonly List<DTile> _dScratch = new List<DTile>();
        private FieldInfo _fiMistColor;
        private int _dRendered; private double _dMsTotal;

        private void LateUpdate()
        {
            if (_disabledByErrors || _dBroken) return;
            try { DetailTick(); }
            catch (Exception e) { DetailFail("tick", e); }
        }

        private void DetailFail(string where, Exception e)
        {
            _dBroken = true;
            Logger.LogError("[detail " + where + "] " + e + "\nThe detailed map layer is off for this session; the rest of Living Map keeps working.");
            try { DetailTeardown(); } catch { }
        }

        // ------------------------------------------------------------------
        // main thread
        // ------------------------------------------------------------------
        private void DetailTick()
        {
            Minimap mm = _mm;
            bool show = _ready && mm != null && _cfgDetail.Value && mm.m_mode == Minimap.MapMode.Large &&
                        mm.m_mapImageLarge != null && mm.m_largeRoot != null && mm.m_largeRoot.activeInHierarchy;
            if (!show)
            {
                if (_dRoot != null && _dRoot.gameObject.activeSelf) _dRoot.gameObject.SetActive(false);
                if (!_ready && (_dTiles.Count > 0 || _dWorkers != null)) DetailTeardown();
                return;
            }

            RawImage img = mm.m_mapImageLarge;
            Rect uv = img.uvRect;
            float world = _texSize * _pixelSize;                    // metres across UV 0..1
            float spanX = uv.width * world;
            float start = _cfgDetailStartSpan.Value;
            float alpha = Mathf.Clamp01((start - spanX) / (start * 0.2f));
            if (alpha <= 0f)
            {
                if (_dRoot != null && _dRoot.gameObject.activeSelf) _dRoot.gameObject.SetActive(false);
                return;
            }

            EnsureDetailLayer(img);
            if (!_dRoot.gameObject.activeSelf) _dRoot.gameObject.SetActive(true);
            _dGroup.alpha = alpha;
            EnsureWorkers();

            int sig = _pieceCount * 31 + _terrain.Count * 17 + _clearedForest.Count * 7 + _plantedForest.Count * 13 +
                      (_cfgShowBuildings.Value ? 1 : 0) + (_cfgShowPaths.Value ? 2 : 0) + (_cfgShowCleared.Value ? 4 : 0);
            if (sig != _dDataSig) { _dDataSig = sig; _dRev++; }

            // level: the coarsest whose pixel is no bigger than a screen pixel
            Canvas canvas = img.canvas;
            float screenPx = img.rectTransform.rect.width * (canvas != null ? canvas.scaleFactor : 1f);
            float need = spanX / Mathf.Max(1f, screenPx);
            float finest = Mathf.Max(1f, _cfgDetailFinest.Value);
            int level = -1;
            for (int i = 0; i < DLevels.Length; i++)
            {
                if (DLevels[i] < finest - 0.01f) break;
                level = i;
                if (DLevels[i] <= need) break;
            }
            if (level < 0) level = 0;

            float vx0 = (uv.xMin - 0.5f) * world, vx1 = (uv.xMax - 0.5f) * world;
            float vz0 = (uv.yMin - 0.5f) * world, vz1 = (uv.yMax - 0.5f) * world;
            while (level > 0 && TileCount(level, vx0, vx1, vz0, vz1) > DVisibleLimit) level--;
            float cx = (vx0 + vx1) * 0.5f, cz = (vz0 + vz1) * 0.5f;
            float now = Time.realtimeSinceStartup;

            foreach (DTile t in _dTiles.Values) if (t.Img != null && t.Img.enabled) t.Img.enabled = false;

            // fallback one level coarser, behind
            if (level > 0) ShowLevel(level - 1, vx0, vx1, vz0, vz1, uv, world, now, false, cx, cz);
            ShowLevel(level, vx0, vx1, vz0, vz1, uv, world, now, true, cx, cz);

            UploadDone();
            RecheckFog();
            Evict(now);
        }

        private static int TileCount(int level, float x0, float x1, float z0, float z1)
        {
            float t = DTileSize * DLevels[level];
            int nx = Mathf.FloorToInt(x1 / t) - Mathf.FloorToInt(x0 / t) + 1;
            int nz = Mathf.FloorToInt(z1 / t) - Mathf.FloorToInt(z0 / t) + 1;
            return nx * nz;
        }

        private static long TileKey(int level, int tx, int tz)
        {
            return ((long)level << 58) ^ ((long)(tx & 0x1FFFFFFF) << 29) ^ (long)(tz & 0x1FFFFFFF);
        }

        private void ShowLevel(int level, float vx0, float vx1, float vz0, float vz1, Rect uv, float world, float now, bool request, float cx, float cz)
        {
            float mpp = DLevels[level];
            float t = DTileSize * mpp;
            int tx0 = Mathf.FloorToInt(vx0 / t), tx1 = Mathf.FloorToInt(vx1 / t);
            int tz0 = Mathf.FloorToInt(vz0 / t), tz1 = Mathf.FloorToInt(vz1 / t);
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    long key = TileKey(level, tx, tz);
                    DTile tile;
                    if (!_dTiles.TryGetValue(key, out tile))
                    {
                        if (!request) continue;
                        tile = new DTile { Level = level, TX = tx, TZ = tz, Key = key };
                        _dTiles[key] = tile;
                    }
                    tile.LastUsed = now;
                    float wx = tx * t, wz = tz * t;
                    if (request && !tile.Queued && !tile.Failed)
                    {
                        bool stale = tile.Ready && tile.Rev != _dRev && now - tile.RenderedAt >= DRedrawMinAge;
                        if (!tile.Ready || stale)
                        {
                            float dx = wx + t * 0.5f - cx, dz = wz + t * 0.5f - cz;
                            Enqueue(tile, wx, wz, mpp, dx * dx + dz * dz);
                        }
                    }
                    if (!tile.Ready || tile.Img == null) continue;
                    RectTransform rt = tile.Img.rectTransform;
                    if (rt.parent != (request ? _dFront : _dBack)) rt.SetParent(request ? _dFront : _dBack, false);
                    Vector2 a0 = new Vector2((wx / world + 0.5f - uv.xMin) / uv.width, (wz / world + 0.5f - uv.yMin) / uv.height);
                    Vector2 a1 = new Vector2(((wx + t) / world + 0.5f - uv.xMin) / uv.width, ((wz + t) / world + 0.5f - uv.yMin) / uv.height);
                    rt.anchorMin = a0; rt.anchorMax = a1;
                    rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                    tile.Img.enabled = true;
                }
        }

        private void EnsureDetailLayer(RawImage img)
        {
            if (_dRoot != null && _dRoot.parent == img.transform) return;
            DestroyDetailLayer();
            GameObject root = new GameObject("LivingMap detail", typeof(RectTransform));
            _dRoot = (RectTransform)root.transform;
            _dRoot.SetParent(img.transform, false);
            _dRoot.SetAsFirstSibling();
            Stretch(_dRoot);
            root.AddComponent<RectMask2D>();
            _dGroup = root.AddComponent<CanvasGroup>();
            _dGroup.blocksRaycasts = false; _dGroup.interactable = false;
            _dBack = NewLayer("back", _dRoot);
            _dFront = NewLayer("front", _dRoot);
            if (_fiMistColor == null) _fiMistColor = typeof(Minimap).GetField("m_mistlandsColor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        }

        private static RectTransform NewLayer(string name, RectTransform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            Stretch(rt);
            return rt;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
        }

        private void DestroyDetailLayer()
        {
            foreach (DTile t in _dTiles.Values)
            {
                if (t.Img != null) UnityEngine.Object.Destroy(t.Img.gameObject);
                if (t.Tex != null) UnityEngine.Object.Destroy(t.Tex);
                t.Img = null; t.Tex = null; t.Ready = false;
            }
            if (_dRoot != null) UnityEngine.Object.Destroy(_dRoot.gameObject);
            _dRoot = null; _dBack = null; _dFront = null; _dGroup = null;
        }

        private void DetailTeardown()
        {
            StopWorkers();
            DestroyDetailLayer();
            _dTiles.Clear();
            lock (_dLock) { _dPending.Clear(); _dDone.Clear(); }
        }

        // ------------------------------------------------------------------
        // jobs: snapshot on the main thread
        // ------------------------------------------------------------------
        private void Enqueue(DTile tile, float x0, float z0, float mpp, float priority)
        {
            Minimap mm = _mm;
            DJob j = new DJob();
            j.Tile = tile; j.X0 = x0; j.Z0 = z0; j.Mpp = mpp; j.Priority = priority;
            j.WaterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            j.Relief = _cfgDetailRelief.Value;
            j.ForestShade = _cfgDetailForestShade.Value;
            j.WaterShallow = _cfgDetailWaterShallow.Value;
            j.WaterDeep = _cfgDetailWaterDeep.Value;
            Color mist = mm.m_blackforestColor;
            try { if (_fiMistColor != null) mist = (Color)_fiMistColor.GetValue(mm); } catch { }
            j.Biome = new Color32[10];
            j.Biome[0] = mm.m_meadowsColor; j.Biome[1] = mm.m_swampColor; j.Biome[2] = mm.m_mountainColor;
            j.Biome[3] = mm.m_blackforestColor; j.Biome[4] = mm.m_heathColor; j.Biome[5] = mm.m_ashlandsColor;
            j.Biome[6] = mm.m_deepnorthColor; j.Biome[7] = Color.white; j.Biome[8] = Color.white; j.Biome[9] = mist;

            float size = DTileSize * mpp;
            j.PixelSize = _pixelSize; j.TexSize = _texSize;
            float half = _texSize * 0.5f;
            j.FogX0 = Mathf.FloorToInt(x0 / _pixelSize + half) - 1;
            j.FogZ0 = Mathf.FloorToInt(z0 / _pixelSize + half) - 1;
            j.FogW = Mathf.CeilToInt(size / _pixelSize) + 3;
            j.FogH = j.FogW;
            j.Fog = new bool[j.FogW * j.FogH];
            j.ForestFix = new sbyte[j.FogW * j.FogH];
            BitArray ex = _fiExplored != null ? _fiExplored.GetValue(mm) as BitArray : null;
            BitArray exo = _fiExploredOthers != null ? _fiExploredOthers.GetValue(mm) as BitArray : null;
            bool fog = _cfgRespectFog.Value;
            bool cleared = _cfgShowForest.Value, planted = _cfgShowPlanted.Value;
            int fogSig = 0;
            for (int gz = 0; gz < j.FogH; gz++)
                for (int gx = 0; gx < j.FogW; gx++)
                {
                    int px = j.FogX0 + gx, pz = j.FogZ0 + gz;
                    int k = gz * j.FogW + gx;
                    if (px < 0 || pz < 0 || px >= _texSize || pz >= _texSize) continue;
                    int idx = pz * _texSize + px;
                    bool e = !fog || IsExplored(ex, exo, idx);
                    j.Fog[k] = e;
                    if (e) fogSig++;
                    if (planted && _plantedForest.Contains(idx)) j.ForestFix[k] = 1;
                    else if (cleared && _clearedForest.Contains(idx)) j.ForestFix[k] = -1;
                }
            j.FogSig = fogSig;

            j.Pieces = new List<PieceRec>();
            if (_cfgShowBuildings.Value)
            {
                for (int gz = 0; gz < j.FogH; gz++)
                    for (int gx = 0; gx < j.FogW; gx++)
                    {
                        int px = j.FogX0 + gx, pz = j.FogZ0 + gz;
                        if (px < 0 || pz < 0 || px >= _texSize || pz >= _texSize) continue;
                        List<PieceRec> bucket;
                        if (!_pieces.TryGetValue(pz * _texSize + px, out bucket)) continue;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            PieceRec r = bucket[i];
                            if (r.X1 < x0 || r.X0 > x0 + size || r.Z1 < z0 || r.Z0 > z0 + size) continue;
                            j.Pieces.Add(r);
                        }
                    }
            }
            j.MatColors = new Color32[_cfgMatColor.Length];
            for (int i = 0; i < _cfgMatColor.Length; i++) j.MatColors[i] = _cfgMatColor[i].Value;
            j.MatUnknown = _cfgMatUnknownColor.Value;
            j.Outline = _cfgOutlineColor.Value;
            j.DrawOutline = _cfgOutline.Value;

            j.TerrKeys = new List<long>(); j.TerrKinds = new List<byte>();
            j.Grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            if (_cfgShowPaths.Value && _terrain.Count > 0)
            {
                bool showCleared = _cfgShowCleared.Value;
                foreach (KeyValuePair<long, byte> kv in _terrain)
                {
                    if (kv.Value == TerrainCleared && !showCleared) continue;
                    float wx = (int)(kv.Key >> 32) * j.Grid, wz = (int)kv.Key * j.Grid;
                    if (wx + j.Grid < x0 || wx > x0 + size || wz + j.Grid < z0 || wz > z0 + size) continue;
                    j.TerrKeys.Add(kv.Key); j.TerrKinds.Add(kv.Value);
                }
            }
            j.Paved = _cfgPavedColor.Value; j.Dirt = _cfgDirtColor.Value;
            j.Cultivated = _cfgCultivatedColor.Value; j.Cleared = _cfgClearedColor.Value;
            j.Rev = _dRev;

            tile.Queued = true;
            lock (_dLock) { _dPending.Add(j); Monitor.Pulse(_dLock); }
        }

        private void UploadDone()
        {
            for (int n = 0; n < DUploadsPerFrame; n++)
            {
                DJob j = null;
                lock (_dLock) { if (_dDone.Count > 0) { j = _dDone[0]; _dDone.RemoveAt(0); } }
                if (j == null) return;
                DTile t = j.Tile;
                t.Queued = false;
                if (j.Pixels == null) { t.Failed = true; continue; }   // drawn again only after a world reload
                if (_dFront == null) continue;
                DTile live;
                if (!_dTiles.TryGetValue(t.Key, out live) || live != t) continue;   // evicted meanwhile
                if (t.Tex == null)
                {
                    t.Tex = new Texture2D(DTileSize, DTileSize, TextureFormat.RGBA32, false, false);
                    t.Tex.wrapMode = TextureWrapMode.Clamp;
                    t.Tex.filterMode = FilterMode.Bilinear;
                }
                t.Tex.LoadRawTextureData(j.Pixels);
                t.Tex.Apply(false, false);
                if (t.Img == null)
                {
                    GameObject go = new GameObject("tile", typeof(RectTransform));
                    go.transform.SetParent(_dFront, false);
                    t.Img = go.AddComponent<RawImage>();
                    t.Img.raycastTarget = false;
                    t.Img.enabled = false;
                }
                t.Img.texture = t.Tex;
                t.Ready = true; t.Rev = j.Rev; t.FogSig = j.FogSig; t.RenderedAt = Time.realtimeSinceStartup;
                _dRendered++; _dMsTotal += j.Ms;
                if (_cfgDebug.Value && _dRendered % 20 == 0)
                    Logger.LogInfo("Detail: " + _dRendered + " tiles drawn, " + (_dMsTotal / _dRendered).ToString("0") + " ms per tile on average; cached " + _dTiles.Count);
            }
        }

        // A few ready visible tiles per frame: if more ground was explored, draw them again.
        private void RecheckFog()
        {
            if (!_cfgRespectFog.Value || _dTiles.Count == 0) return;
            BitArray ex = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
            BitArray exo = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
            _dScratch.Clear();
            foreach (DTile t in _dTiles.Values) if (t.Ready && !t.Queued && t.Img != null && t.Img.enabled) _dScratch.Add(t);
            if (_dScratch.Count == 0) return;
            for (int n = 0; n < 2; n++)
            {
                _dFogCursor = (_dFogCursor + 1) % _dScratch.Count;
                DTile t = _dScratch[_dFogCursor];
                float mpp = DLevels[t.Level];
                float size = DTileSize * mpp, x0 = t.TX * size, z0 = t.TZ * size;
                float half = _texSize * 0.5f;
                int fx0 = Mathf.FloorToInt(x0 / _pixelSize + half) - 1, fz0 = Mathf.FloorToInt(z0 / _pixelSize + half) - 1;
                int w = Mathf.CeilToInt(size / _pixelSize) + 3;
                int sig = 0;
                for (int gz = 0; gz < w; gz++)
                    for (int gx = 0; gx < w; gx++)
                    {
                        int px = fx0 + gx, pz = fz0 + gz;
                        if (px < 0 || pz < 0 || px >= _texSize || pz >= _texSize) continue;
                        if (IsExplored(ex, exo, pz * _texSize + px)) sig++;
                    }
                if (sig != t.FogSig) Enqueue(t, x0, z0, mpp, 0f);
            }
        }

        private void Evict(float now)
        {
            if (_dTiles.Count <= DCacheLimit) return;
            _dScratch.Clear();
            foreach (DTile t in _dTiles.Values) if (!t.Queued && (t.Img == null || !t.Img.enabled)) _dScratch.Add(t);
            _dScratch.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
            int drop = _dTiles.Count - DCacheLimit;
            for (int i = 0; i < _dScratch.Count && drop > 0; i++, drop--)
            {
                DTile t = _dScratch[i];
                if (t.Img != null) UnityEngine.Object.Destroy(t.Img.gameObject);
                if (t.Tex != null) UnityEngine.Object.Destroy(t.Tex);
                _dTiles.Remove(t.Key);
            }
        }

        // ------------------------------------------------------------------
        // workers
        // ------------------------------------------------------------------
        private void EnsureWorkers()
        {
            if (_dWorkers != null) return;
            WorldGenerator wg = WorldGenerator.instance;
            if (wg == null) return;
            _dStop = false;
            int n = Mathf.Clamp(_cfgDetailWorkers.Value, 1, 4);
            _dWorkers = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                Thread th = new Thread(WorkerLoop);
                th.IsBackground = true;
                th.Priority = System.Threading.ThreadPriority.BelowNormal;
                th.Name = "LivingMap detail " + i;
                _dWorkers[i] = th;
                th.Start(wg);
            }
        }

        private void StopWorkers()
        {
            if (_dWorkers == null) return;
            _dStop = true;
            lock (_dLock) { Monitor.PulseAll(_dLock); }
            for (int i = 0; i < _dWorkers.Length; i++)
            {
                try { _dWorkers[i].Join(500); } catch { }
            }
            _dWorkers = null;
        }

        private void WorkerLoop(object state)
        {
            WorldGenerator wg = (WorldGenerator)state;
            while (!_dStop)
            {
                DJob j = null;
                lock (_dLock)
                {
                    while (!_dStop && _dPending.Count == 0) Monitor.Wait(_dLock, 1000);
                    if (_dStop) return;
                    int best = 0;
                    for (int i = 1; i < _dPending.Count; i++) if (_dPending[i].Priority < _dPending[best].Priority) best = i;
                    j = _dPending[best];
                    _dPending.RemoveAt(best);
                }
                long t0 = Stopwatch.GetTimestamp();
                try { j.Pixels = RenderTile(j, wg); }
                catch (Exception e)
                {
                    j.Pixels = null;
                    if (_loggedErrors.Add("detail-render|" + e.GetType().Name)) Logger.LogWarning("Detail tile failed: " + e);
                }
                j.Ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                lock (_dLock) { _dDone.Add(j); }
            }
        }

        // ------------------------------------------------------------------
        // drawing (background thread; only the job and the world generator)
        // ------------------------------------------------------------------
        private const int DStep = 2;   // height samples every 2 pixels, bilinear in between

        private static byte[] RenderTile(DJob j, WorldGenerator wg)
        {
            int n = DTileSize;
            float mpp = j.Mpp;
            int gs = n / DStep + 3;                             // samples incl. a border on each side
            float[] h = new float[gs * gs];
            byte[] bio = new byte[gs * gs];
            float[] forest = new float[gs * gs];
            Color mask;
            for (int sz = 0; sz < gs; sz++)
                for (int sx = 0; sx < gs; sx++)
                {
                    float wx = j.X0 + ((sx - 1) * DStep + 0.5f) * mpp;
                    float wz = j.Z0 + ((sz - 1) * DStep + 0.5f) * mpp;
                    Heightmap.Biome b = wg.GetBiome(wx, wz, 0.02f, false);
                    float hh = wg.GetBiomeHeight(b, wx, wz, out mask, false, true);
                    int k = sz * gs + sx;
                    h[k] = hh;
                    bio[k] = BiomeIndex(b);
                    forest[k] = ForestAmount(b, wx, wz, hh, j.WaterLevel);
                }

            byte[] px = new byte[n * n * 4];
            Vector3 light = new Vector3(-1f, 1.6f, 1f).normalized;   // from the north-west, map up = north
            float flat = light.y;
            float relief = j.Relief;
            for (int iz = 0; iz < n; iz++)
            {
                float fz = iz / (float)DStep + 1f;
                for (int ix = 0; ix < n; ix++)
                {
                    float fx = ix / (float)DStep + 1f;
                    float hc = Sample(h, gs, fx, fz);
                    float dhx = (Sample(h, gs, fx + 0.5f, fz) - Sample(h, gs, fx - 0.5f, fz)) / (DStep * mpp);
                    float dhz = (Sample(h, gs, fx, fz + 0.5f) - Sample(h, gs, fx, fz - 0.5f)) / (DStep * mpp);
                    float wx = j.X0 + (ix + 0.5f) * mpp, wz = j.Z0 + (iz + 0.5f) * mpp;

                    float r, g, b;
                    if (hc < j.WaterLevel)
                    {
                        float d = Mathf.Clamp01((j.WaterLevel - hc) / 20f);
                        r = Lerp(j.WaterShallow.r, j.WaterDeep.r, d); g = Lerp(j.WaterShallow.g, j.WaterDeep.g, d); b = Lerp(j.WaterShallow.b, j.WaterDeep.b, d);
                    }
                    else
                    {
                        Color32 c = j.Biome[bio[Nearest(gs, fx, fz)]];
                        r = c.r; g = c.g; b = c.b;
                        Vector3 nrm = new Vector3(-dhx, 1f, -dhz).normalized;
                        float lit = Vector3.Dot(nrm, light);
                        float f = 1f + relief * (lit - flat) * 1.4f;
                        f = Mathf.Clamp(f, 0.35f, 1.5f);
                        r *= f; g *= f; b *= f;

                        float fa = Sample(forest, gs, fx, fz);
                        int fk = FogIndex(j, wx, wz);
                        if (fk >= 0 && j.ForestFix[fk] != 0) fa = j.ForestFix[fk] > 0 ? 1f : 0f;
                        if (fa > 0.01f && j.ForestShade > 0f)
                        {
                            float crown = Crown(wx, wz);
                            float dark = j.ForestShade * fa * (0.55f + 0.45f * crown);
                            r *= 1f - dark; g *= 1f - dark * 0.8f; b *= 1f - dark;
                        }
                    }
                    int o = (iz * n + ix) * 4;
                    px[o] = ToByte(r); px[o + 1] = ToByte(g); px[o + 2] = ToByte(b);
                    px[o + 3] = ToByte(FogAlpha(j, wx, wz) * 255f);
                }
            }

            float grid = j.Grid;
            for (int i = 0; i < j.TerrKeys.Count; i++)
            {
                long key = j.TerrKeys[i];
                float cx0 = (int)(key >> 32) * grid, cz0 = (int)key * grid;
                Color32 c = TerrainColor32(j, j.TerrKinds[i]);
                FillRect(px, n, j, cx0, cz0, cx0 + grid, cz0 + grid, c);
            }
            for (int i = 0; i < j.Pieces.Count; i++)
            {
                PieceRec r = j.Pieces[i];
                Color32 c = r.Mat != MatNone && r.Mat < j.MatColors.Length ? j.MatColors[r.Mat] : j.MatUnknown;
                if (j.DrawOutline)
                {
                    float w = Mathf.Max(0.5f, mpp);
                    FillRect(px, n, j, r.X0 - w, r.Z0 - w, r.X1 + w, r.Z1 + w, j.Outline);
                }
                FillRect(px, n, j, r.X0, r.Z0, r.X1, r.Z1, c);
            }
            return px;
        }

        private static byte BiomeIndex(Heightmap.Biome b)
        {
            int v = (int)b;
            for (byte i = 0; i < 10; i++) if (v == (1 << i)) return i;
            return 0;
        }

        // the vanilla map's forest rule (Minimap.GetMaskColor), as an amount 0..1
        private static float ForestAmount(Heightmap.Biome b, float wx, float wz, float height, float water)
        {
            if (height < water) return 0f;
            Vector3 p = new Vector3(wx, 0f, wz);
            if (b == Heightmap.Biome.Meadows) return WorldGenerator.InForest(p) ? 1f : 0f;
            if (b == Heightmap.Biome.Plains) return WorldGenerator.GetForestFactor(p) < 0.8f ? 1f : 0f;
            if (b == Heightmap.Biome.BlackForest) return 1f;
            if (b == Heightmap.Biome.Mistlands)
            {
                float f = WorldGenerator.GetForestFactor(p);
                return 1f - Mathf.Clamp01((f - 1.1f) / 0.2f);
            }
            return 0f;
        }

        // cheap tree-crown texture: a hashed dot on a 4 m lattice
        private static float Crown(float wx, float wz)
        {
            int cx = Mathf.FloorToInt(wx / 4f), cz = Mathf.FloorToInt(wz / 4f);
            uint hsh = (uint)(cx * 73856093) ^ (uint)(cz * 19349663);
            hsh ^= hsh >> 13; hsh *= 0x5bd1e995; hsh ^= hsh >> 15;
            float ox = (hsh & 0xFF) / 255f, oz = ((hsh >> 8) & 0xFF) / 255f;
            float dx = wx / 4f - cx - ox, dz = wz / 4f - cz - oz;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            return Mathf.Clamp01(1f - d * 1.6f);
        }

        private static int FogIndex(DJob j, float wx, float wz)
        {
            float half = j.TexSize * 0.5f;
            int gx = Mathf.FloorToInt(wx / j.PixelSize + half) - j.FogX0;
            int gz = Mathf.FloorToInt(wz / j.PixelSize + half) - j.FogZ0;
            if (gx < 0 || gz < 0 || gx >= j.FogW || gz >= j.FogH) return -1;
            return gz * j.FogW + gx;
        }

        // explored cells, bilinear between cell centres so the edge is soft
        private static float FogAlpha(DJob j, float wx, float wz)
        {
            float half = j.TexSize * 0.5f;
            float fx = wx / j.PixelSize + half - j.FogX0 - 0.5f;
            float fz = wz / j.PixelSize + half - j.FogZ0 - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            float a = Fog(j, x0, z0), b = Fog(j, x0 + 1, z0), c = Fog(j, x0, z0 + 1), d = Fog(j, x0 + 1, z0 + 1);
            return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
        }

        private static float Fog(DJob j, int x, int z)
        {
            if (x < 0 || z < 0 || x >= j.FogW || z >= j.FogH) return 0f;
            return j.Fog[z * j.FogW + x] ? 1f : 0f;
        }

        private static Color32 TerrainColor32(DJob j, byte kind)
        {
            if (kind == TerrainPaved) return j.Paved;
            if (kind == TerrainDirt) return j.Dirt;
            if (kind == TerrainCultivated) return j.Cultivated;
            return j.Cleared;
        }

        private static void FillRect(byte[] px, int n, DJob j, float x0, float z0, float x1, float z1, Color32 c)
        {
            int ix0 = Mathf.Max(0, Mathf.FloorToInt((x0 - j.X0) / j.Mpp));
            int ix1 = Mathf.Min(n - 1, Mathf.CeilToInt((x1 - j.X0) / j.Mpp) - 1);
            int iz0 = Mathf.Max(0, Mathf.FloorToInt((z0 - j.Z0) / j.Mpp));
            int iz1 = Mathf.Min(n - 1, Mathf.CeilToInt((z1 - j.Z0) / j.Mpp) - 1);
            if (ix1 < ix0 || iz1 < iz0) return;
            float a = c.a / 255f, ia = 1f - a;
            for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    int o = (iz * n + ix) * 4;
                    px[o] = (byte)(c.r * a + px[o] * ia);
                    px[o + 1] = (byte)(c.g * a + px[o + 1] * ia);
                    px[o + 2] = (byte)(c.b * a + px[o + 2] * ia);
                }
        }

        private static float Sample(float[] a, int gs, float fx, float fz)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, gs - 2), z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, gs - 2);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float v0 = Lerp(a[z0 * gs + x0], a[z0 * gs + x0 + 1], tx);
            float v1 = Lerp(a[(z0 + 1) * gs + x0], a[(z0 + 1) * gs + x0 + 1], tx);
            return Lerp(v0, v1, tz);
        }

        private static int Nearest(int gs, float fx, float fz)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt(fx), 0, gs - 1), z = Mathf.Clamp(Mathf.RoundToInt(fz), 0, gs - 1);
            return z * gs + x;
        }

        private static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        private static byte ToByte(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 255f) return 255;
            return (byte)v;
        }
    }
}
