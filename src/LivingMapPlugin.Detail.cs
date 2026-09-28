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
    // it - relief shaded from the world generator's heights plus the players' terrain edits,
    // water by depth, forest, rocks, paths and buildings. Zoomed out and on the minimap nothing
    // changes.
    //
    // Tiles are drawn on background threads from a snapshot taken on the main thread (the
    // threads never touch the mod's or the game's collections) and uploaded at most two per
    // frame. They sit in a UI layer under the map's pins, positioned from the large map image's
    // uvRect in LateUpdate. Any failure switches off this layer only.
    //
    // At 2 and 1 m per pixel the snapshot also reads the objects of each zone straight from the
    // object database: trees and rocks are drawn where they stand, buildings as rotated boxes
    // shaded by height. Zones the database does not hold (a client far from where it has been
    // this session) fall back to the vanilla forest rule and the stored building footprints.
    public partial class LivingMapPlugin
    {
        private const string SecDetail = "08 Detail";
        private ConfigEntry<bool> _cfgDetail;
        private ConfigEntry<float> _cfgDetailStartSpan;
        private ConfigEntry<float> _cfgDetailFinest;
        private ConfigEntry<float> _cfgDetailRelief;
        private ConfigEntry<float> _cfgDetailForestShade;
        private ConfigEntry<bool> _cfgDetailObjects;
        private ConfigEntry<Color> _cfgDetailWaterShallow;
        private ConfigEntry<Color> _cfgDetailWaterDeep;
        private ConfigEntry<Color> _cfgDetailTreeColor;
        private ConfigEntry<Color> _cfgDetailRockColor;
        private ConfigEntry<int> _cfgDetailWorkers;
        private ConfigEntry<bool> _cfgDetailVanillaTone;
        private ConfigEntry<Color> _cfgDetailToneTint;
        private ConfigEntry<float> _cfgDetailToneDesaturate;
        private ConfigEntry<float> _cfgDetailSharedOpacity;

        private void BindDetailConfig()
        {
            _cfgDetail = Config.Bind(SecDetail, "DetailEnabled", true,
                "When the big map is zoomed in, draw a detailed picture over it: shaded relief, water by depth, forest, rocks, paths and buildings, down to 1 m per pixel. Zoomed out and on the minimap nothing changes.");
            _cfgDetailStartSpan = Config.Bind(SecDetail, "DetailStartSpanMeters", 3000f,
                new ConfigDescription("The detailed picture fades in once the map window shows at most this many metres across (fully visible at 80% of it).",
                    new AcceptableValueRange<float>(500f, 8000f)));
            _cfgDetailFinest = Config.Bind(SecDetail, "DetailFinestMetersPerPixel", 1f,
                new ConfigDescription("The finest level drawn: 1, 2 or 4 metres per pixel. Coarser = fewer tiles to draw.",
                    new AcceptableValueList<float>(1f, 2f, 4f)));
            _cfgDetailRelief = Config.Bind(SecDetail, "DetailRelief", 1f,
                new ConfigDescription("Strength of the hill shading. 0 = flat colours.", new AcceptableValueRange<float>(0f, 3f)));
            _cfgDetailForestShade = Config.Bind(SecDetail, "DetailForestShade", 0.35f,
                new ConfigDescription("How much darker forest is drawn where single trees are not known (4 m per pixel, or zones the object database does not hold). 0 = not shown.", new AcceptableValueRange<float>(0f, 0.9f)));
            _cfgDetailObjects = Config.Bind(SecDetail, "DetailObjects", true,
                "At 2 and 1 m per pixel, draw trees and rocks where they stand and buildings as rotated boxes shaded by height, read from the object database. Off = the forest pattern and the stored footprints only.");
            _cfgDetailWaterShallow = Config.Bind(SecDetail, "DetailWaterShallow", new Color(0.27f, 0.28f, 0.33f, 1f),
                "Colour of shallow water in the detailed picture (as drawn; the vanilla map's water).");
            _cfgDetailWaterDeep = Config.Bind(SecDetail, "DetailWaterDeep", new Color(0.22f, 0.24f, 0.30f, 1f),
                "Colour of deep water in the detailed picture (as drawn).");
            _cfgDetailTreeColor = Config.Bind(SecDetail, "DetailTreeColor", new Color(0.55f, 0.62f, 0.45f, 1f),
                "Colour of tree crowns in the detailed picture (before DetailVanillaTone).");
            _cfgDetailRockColor = Config.Bind(SecDetail, "DetailRockColor", new Color(0.50f, 0.50f, 0.48f, 1f),
                "Colour of rocks in the detailed picture (before DetailVanillaTone). Rocks under water are not drawn.");
            _cfgDetailVanillaTone = Config.Bind(SecDetail, "DetailVanillaTone", true,
                "Tone the detailed picture - terrain, trees, rocks, paths and buildings; water has its own colours - the way the game's map shader tones the map: darker and less saturated, so zooming in keeps the vanilla look. Off = the raw colours.");
            _cfgDetailToneTint = Config.Bind(SecDetail, "DetailToneTint", new Color(0.38f, 0.48f, 0.44f, 1f),
                "DetailVanillaTone: every colour is multiplied by this (measured against the vanilla map).");
            _cfgDetailToneDesaturate = Config.Bind(SecDetail, "DetailToneDesaturate", 0.5f,
                new ConfigDescription("DetailVanillaTone: how much colour is taken out before the tint. 0 = none, 1 = grey.", new AcceptableValueRange<float>(0f, 1f)));
            _cfgDetailSharedOpacity = Config.Bind(SecDetail, "DetailSharedOpacity", 0.5f,
                new ConfigDescription("Ground known only from a cartography table (explored by others) is drawn this opaque, so the vanilla haze over it shows through as on the vanilla map. 1 = like your own explored ground.", new AcceptableValueRange<float>(0f, 1f)));
            BindPortConfig();
            MigrateDetailConfig();
            _cfgDetailWorkers = Config.Bind(SecDetail, "DetailWorkerThreads", 2,
                new ConfigDescription("Background threads that draw tiles. Takes effect on the next world load.", new AcceptableValueRange<int>(1, 4)));
        }

        // Once per config file: colours still at the defaults of the first detail builds move to
        // the vanilla-toned ones; anything the user picked is kept.
        private void MigrateDetailConfig()
        {
            ConfigEntry<int> ver = Config.Bind(SecDetail, "DetailConfigVersion", 1, "Internal: which defaults this section has been updated to. Do not edit.");
            if (ver.Value >= 2) return;
            MoveDefault(_cfgDetailWaterShallow, new Color(0.36f, 0.50f, 0.56f, 1f));
            MoveDefault(_cfgDetailWaterDeep, new Color(0.14f, 0.22f, 0.30f, 1f));
            MoveDefault(_cfgDetailTreeColor, new Color(0.20f, 0.30f, 0.14f, 1f));
            MoveDefault(_cfgDetailRockColor, new Color(0.52f, 0.52f, 0.50f, 1f));
            ver.Value = 2;
        }

        // The game's map shader draws the map darker and less saturated than its colours; the
        // same tone on every land and overlay colour keeps the zoomed-in picture in the vanilla
        // palette (and paths and buildings look like the ones the shader draws zoomed out).
        private Color32 Tone(Color c)
        {
            if (!_cfgDetailVanillaTone.Value) return c;
            float ds = Mathf.Clamp01(_cfgDetailToneDesaturate.Value);
            Color t = _cfgDetailToneTint.Value;
            float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            return new Color((c.r + (l - c.r) * ds) * t.r, (c.g + (l - c.g) * ds) * t.g, (c.b + (l - c.b) * ds) * t.b, c.a);
        }

        private static void MoveDefault(ConfigEntry<Color> e, Color old)
        {
            Color v = e.Value;
            if (Mathf.Abs(v.r - old.r) < 0.005f && Mathf.Abs(v.g - old.g) < 0.005f && Mathf.Abs(v.b - old.b) < 0.005f)
                e.Value = (Color)e.DefaultValue;
        }

        // ------------------------------------------------------------------
        // model
        // ------------------------------------------------------------------
        private const int DTileSize = 256;
        private const int DCacheLimit = 96;
        private const int DVisibleLimit = 64;
        private const int DUploadsPerFrame = 2;
        private const int DEnqueuesPerFrame = 4;
        private const float DRedrawMinAge = 5f;
        private const float DObjectsMaxMpp = 2f;
        // metres per tile pixel of each level; the vanilla style uses the vanilla map's pixel
        // (the world / 7000) and its halves, so a tile pixel is exactly one map pixel
        private static float[] DLevels = { 4f, 2f, 1f };

        private class DTile
        {
            public int Level, TX, TZ;
            public long Key;
            public Texture2D Tex;
            public RawImage Img;
            public bool Ready, Queued, Failed;
            public long LightSig;                   // the light it was drawn under (vanilla style)
            public byte[] Pending, Pending2; public long PendingLight;   // drawn again under new light, shown with the others
            public Texture2D Tex2; public RawImage Img2;          // vanilla style: the fog of war over the clouds
            public long Sig;
            public float RenderedAt, LastUsed;
        }

        // players' height edits of one zone: level + smooth delta per heightmap vertex
        private class HeightZone
        {
            public float Cx, Cz, Scale;
            public int Pitch, Half;
            public float[] D;
        }

        private struct DTree { public float X, Z, R; public bool Rock; }
        private struct DBox { public float X, Z, HX, HZ, Cos, Sin, Top; public byte Mat; }

        private class DJob
        {
            public DTile Tile;
            public float X0, Z0, Mpp;
            public float Priority;
            // snapshot
            public float WaterLevel, Relief, ForestShade, ZoneSize;
            public Color32 WaterShallow, WaterDeep, Tree, Rock;
            public Color32[] Biome;                 // index = biome bit position
            public float[] Fog; public int FogX0, FogZ0, FogW, FogH; public float PixelSize; public int TexSize;
            public sbyte[] ForestFix;               // same grid as Fog: -1 cleared, +1 planted
            public List<PieceRec> Pieces;
            public List<DBox> Boxes;
            public List<DTree> Trees;
            public HashSet<long> LiveZones;         // zones whose objects were read (trees drawn, no forest pattern)
            public Dictionary<long, HeightZone> Heights;
            public Color32[] MatColors; public Color32 MatUnknown, Outline; public bool DrawOutline;
            public List<long> TerrKeys; public List<byte> TerrKinds; public float Grid;
            public Color32 Paved, Dirt, Cultivated, Cleared;
            public long Sig;
            public PortJob Port;                    // the vanilla map shader's inputs (DetailStyle = Vanilla)
            public long LightSig; public bool Relight;
            // result
            public byte[] Pixels, Pixels2;          // Pixels2: the fog layer (vanilla style)
            public double Ms;
        }

        private readonly Dictionary<long, DTile> _dTiles = new Dictionary<long, DTile>();
        private readonly List<DJob> _dPending = new List<DJob>();
        private readonly List<DJob> _dDone = new List<DJob>();
        private readonly object _dLock = new object();
        private Thread[] _dWorkers;
        private volatile bool _dStop;
        private bool _dBroken;
        private RectTransform _dRoot, _dBack, _dFront, _dFogBack, _dFogFront;
        private CanvasGroup _dGroup;
        private int _dRecheckCursor, _dEnqueuedThisFrame;
        private readonly List<DTile> _dScratch = new List<DTile>();
        private FieldInfo _fiMistColor, _fiObjectsBySector;
        private int _dRendered; private double _dMsTotal;

        private readonly Dictionary<long, HeightZone> _dHeights = new Dictionary<long, HeightZone>();
        private readonly Dictionary<long, int> _dZoneRev = new Dictionary<long, int>();   // terrain record parses per zone
        private long _dHeightsWorld;
        private readonly Dictionary<int, byte> _dKind = new Dictionary<int, byte>();       // prefab -> 0 none, 1 tree, 2 rock, 3 piece

        private static long DZoneKey(int zx, int zz) { return ((long)zx << 32) | (uint)zz; }

        private void LateUpdate()
        {
            if (_disabledByErrors) return;
            if (_probeImg != null)
            {
                try { ProbeTick(); }
                catch (Exception e) { Logger.LogError("[probe] " + e); ProbeHide(); }
                return;
            }
            if (_dBroken) return;
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
        // terrain records -> height edits (called from ApplyTerrainRecord)
        // ------------------------------------------------------------------
        private void DetailWorldCheck()
        {
            if (_dHeightsWorld == _worldUid) return;
            _dHeightsWorld = _worldUid;
            _dHeights.Clear();
            _dZoneRev.Clear();
        }

        private void StoreHeightZone(Vector3 zonePos, int pitch, float scale, float[] delta)
        {
            DetailWorldCheck();
            Vector2s z = ZoneSystem.GetZone(zonePos);
            long key = DZoneKey(z.x, z.y);
            if (delta == null) _dHeights.Remove(key);
            else
            {
                HeightZone hz = new HeightZone();
                hz.Cx = zonePos.x; hz.Cz = zonePos.z; hz.Scale = scale;
                hz.Pitch = pitch; hz.Half = pitch / 2; hz.D = delta;
                _dHeights[key] = hz;                       // replaced, never mutated: safe to hand to workers
            }
            int rev;
            _dZoneRev.TryGetValue(key, out rev);
            _dZoneRev[key] = rev + 1;
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
            DetailWorldCheck();

            RawImage img = mm.m_mapImageLarge;
            Rect uv = img.uvRect;
            float world = _texSize * _pixelSize;                    // metres across UV 0..1
            UpdateLevels(world);
            float spanX = uv.width * world;
            float start = _cfgDetailStartSpan.Value;
            float alpha = Mathf.Clamp01((start - spanX) / (start * 0.2f));
            if (alpha <= 0f)
            {
                if (_dRoot != null && _dRoot.gameObject.activeSelf) _dRoot.gameObject.SetActive(false);
                return;
            }

            // the vanilla style matches the map under it pixel for pixel, so it is shown at once:
            // a half-transparent layer would let its ground show through its own fog of war
            if (PortWanted) alpha = 1f;
            EnsureDetailLayer(img);
            if (!_dRoot.gameObject.activeSelf) _dRoot.gameObject.SetActive(true);
            _dGroup.alpha = alpha;
            EnsureWorkers();
            _dEnqueuedThisFrame = 0;

            // level: the coarsest whose pixel is no bigger than a screen pixel
            Canvas canvas = img.canvas;
            float screenPx = img.rectTransform.rect.width * (canvas != null ? canvas.scaleFactor : 1f);
            float need = spanX / Mathf.Max(1f, screenPx);
            float finest = Mathf.Max(1f, _cfgDetailFinest.Value);
            int level = -1;
            for (int i = 0; i < DLevels.Length; i++)
            {
                if (DLevels[i] < finest * 0.85f) break;
                level = i;
                // vanilla style: a map pixel stays at most ~6 screen pixels (they shrink as you zoom in)
                if (DLevels[i] <= need * (PortWanted ? 6f : 1f)) break;
            }
            if (level < 0) level = 0;

            float vx0 = (uv.xMin - 0.5f) * world, vx1 = (uv.xMax - 0.5f) * world;
            float vz0 = (uv.yMin - 0.5f) * world, vz1 = (uv.yMax - 0.5f) * world;
            while (level > 0 && TileCount(level, vx0, vx1, vz0, vz1) > DVisibleLimit) level--;
            float cx = (vx0 + vx1) * 0.5f, cz = (vz0 + vz1) * 0.5f;
            float now = Time.realtimeSinceStartup;

            foreach (DTile t in _dTiles.Values)
            {
                if (t.Img != null && t.Img.enabled) t.Img.enabled = false;
                if (t.Img2 != null && t.Img2.enabled) t.Img2.enabled = false;
            }

            // fallback one level coarser, behind
            if (level > 0) ShowLevel(level - 1, vx0, vx1, vz0, vz1, uv, world, now, false, cx, cz);
            ShowLevel(level, vx0, vx1, vz0, vz1, uv, world, now, true, cx, cz);
            if (level > 0) HideCoveredFog(level);
            UpdateClouds(uv);

            UploadDone();
            RecheckTiles(now);
            RelightTiles(now);
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
                    if (request && !tile.Ready && !tile.Queued && !tile.Failed && _dEnqueuedThisFrame < DEnqueuesPerFrame)
                    {
                        float dx = wx + t * 0.5f - cx, dz = wz + t * 0.5f - cz;
                        Enqueue(tile, dx * dx + dz * dz);
                    }
                    if (!tile.Ready || tile.Img == null) continue;
                    // a vanilla-style pixel is centred on its grid point, like the map's own pixels
                    float sx = wx, sz = wz;
                    if (tile.Img2 != null) { sx -= mpp * 0.5f; sz -= mpp * 0.5f; }
                    Vector2 a0 = new Vector2((sx / world + 0.5f - uv.xMin) / uv.width, (sz / world + 0.5f - uv.yMin) / uv.height);
                    Vector2 a1 = new Vector2(((sx + t) / world + 0.5f - uv.xMin) / uv.width, ((sz + t) / world + 0.5f - uv.yMin) / uv.height);
                    Place(tile.Img, request ? _dFront : _dBack, a0, a1);
                    if (tile.Img2 != null) Place(tile.Img2, request ? _dFogFront : _dFogBack, a0, a1);
                }
        }

        private static void Place(RawImage img, RectTransform parent, Vector2 a0, Vector2 a1)
        {
            RectTransform rt = img.rectTransform;
            if (rt.parent != parent) rt.SetParent(parent, false);
            rt.anchorMin = a0; rt.anchorMax = a1;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            img.enabled = true;
        }

        // A coarser tile stays behind the current level while it loads; its fog is hidden where
        // the finer tiles over it are all shown, or the two fog layers would add up.
        private void HideCoveredFog(int level)
        {
            float fine = DTileSize * DLevels[level], coarse = DTileSize * DLevels[level - 1];
            foreach (DTile t in _dTiles.Values)
            {
                if (t.Level != level - 1 || t.Img2 == null || !t.Img2.enabled) continue;
                float x0 = t.TX * coarse, z0 = t.TZ * coarse;
                int fx0 = Mathf.FloorToInt(x0 / fine), fx1 = Mathf.FloorToInt((x0 + coarse - 0.01f) / fine);
                int fz0 = Mathf.FloorToInt(z0 / fine), fz1 = Mathf.FloorToInt((z0 + coarse - 0.01f) / fine);
                bool covered = true;
                for (int fz = fz0; fz <= fz1 && covered; fz++)
                    for (int fx = fx0; fx <= fx1 && covered; fx++)
                    {
                        DTile c;
                        if (!_dTiles.TryGetValue(TileKey(level, fx, fz), out c) || c.Img == null || !c.Img.enabled) covered = false;
                    }
                if (covered) t.Img2.enabled = false;
            }
        }

        // The vanilla style's drifting clouds: the cloud texture over the whole view, moved like
        // in the shader (map coordinate x 7 - _CloudOffset) and tinted sun x light colour.
        private RawImage _dClouds;

        private void UpdateClouds(Rect uv)
        {
            if (_dClouds == null) return;
            bool on = PortWanted && _port != null && _port.CloudTex != null && _cfgDetailClouds.Value && _portMat != null;
            if (_dClouds.enabled != on) _dClouds.enabled = on;
            if (!on) return;
            if (_dClouds.texture != _port.CloudTex) _dClouds.texture = _port.CloudTex;
            Vector4 off = Shader.GetGlobalVector("_CloudOffset");
            _dClouds.uvRect = new Rect(uv.x * 7f - off.x, uv.y * 7f - off.z, uv.width * 7f, uv.height * 7f);
            Color sun = Shader.GetGlobalColor("_SunColor"), lt = _portMat.GetColor("_lightColor");
            Color c = new Color(Mathf.Clamp01(sun.r * lt.r), Mathf.Clamp01(sun.g * lt.g), Mathf.Clamp01(sun.b * lt.b), 1f);
            _dClouds.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? c.gamma : c;   // vertex colours are gamma
        }

        // Levels follow the style; a change drops the tiles (their keys are per level).
        private void UpdateLevels(float world)
        {
            float first = PortWanted ? world / 7000f : 4f;
            int count = PortWanted ? 5 : 3;
            if (Mathf.Abs(DLevels[0] - first) < 0.0001f && DLevels.Length == count) return;
            // the vanilla style steps by 1/sqrt(2) (3.5, 2.5, 1.75, 1.25, 0.9 m): each change of
            // pixel size is half the area, not a quarter; the legacy picture keeps 4 / 2 / 1 m
            float step = PortWanted ? 0.70710678f : 0.5f;
            DLevels = new float[count];
            for (int i = 0; i < count; i++) DLevels[i] = first * Mathf.Pow(step, i);
            foreach (DTile t in _dTiles.Values) DestroyTile(t);
            _dTiles.Clear();
        }

        private static void DestroyTile(DTile t)
        {
            if (t.Img != null) UnityEngine.Object.Destroy(t.Img.gameObject);
            if (t.Img2 != null) UnityEngine.Object.Destroy(t.Img2.gameObject);
            if (t.Tex != null) UnityEngine.Object.Destroy(t.Tex);
            if (t.Tex2 != null) UnityEngine.Object.Destroy(t.Tex2);
            t.Img = null; t.Img2 = null; t.Tex = null; t.Tex2 = null; t.Ready = false;
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
            RectTransform clouds = NewLayer("clouds", _dRoot);
            _dClouds = clouds.gameObject.AddComponent<RawImage>();
            _dClouds.raycastTarget = false;
            _dClouds.enabled = false;
            _dFogBack = NewLayer("fog back", _dRoot);
            _dFogFront = NewLayer("fog front", _dRoot);
            if (_fiMistColor == null) _fiMistColor = typeof(Minimap).GetField("m_mistlandsColor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_fiObjectsBySector == null)
            {
                FieldInfo f = typeof(ZDOMan).GetField("m_objectsBySector", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null && typeof(List<ZDO>[]).IsAssignableFrom(f.FieldType)) _fiObjectsBySector = f;
                else LogOnce("detail-sectors", "ZDOMan.m_objectsBySector was not found; the detailed map draws the forest pattern and stored footprints only.");
            }
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
            foreach (DTile t in _dTiles.Values) DestroyTile(t);
            if (_dRoot != null) UnityEngine.Object.Destroy(_dRoot.gameObject);
            _dRoot = null; _dBack = null; _dFront = null; _dFogBack = null; _dFogFront = null; _dGroup = null; _dClouds = null;
        }

        private void DetailTeardown()
        {
            StopWorkers();
            DestroyDetailLayer();
            _dTiles.Clear();
            lock (_dLock) { _dPending.Clear(); _dDone.Clear(); }
        }

        // ------------------------------------------------------------------
        // what a tile shows, as a number: redraw only when it changes
        // ------------------------------------------------------------------
        private void TileArea(DTile t, out float x0, out float z0, out float size, out int fx0, out int fz0, out int w)
        {
            float mpp = DLevels[t.Level];
            size = DTileSize * mpp; x0 = t.TX * size; z0 = t.TZ * size;
            float half = _texSize * 0.5f;
            fx0 = Mathf.FloorToInt(x0 / _pixelSize + half) - 1;
            fz0 = Mathf.FloorToInt(z0 / _pixelSize + half) - 1;
            w = Mathf.CeilToInt(size / _pixelSize) + 3;
        }

        private long RegionSig(DTile t)
        {
            float x0, z0, size; int fx0, fz0, w;
            TileArea(t, out x0, out z0, out size, out fx0, out fz0, out w);
            BitArray ex = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
            BitArray exo = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
            bool fog = _cfgRespectFog.Value, buildings = _cfgShowBuildings.Value;
            long sig = 17;
            for (int gz = 0; gz < w; gz++)
                for (int gx = 0; gx < w; gx++)
                {
                    int px = fx0 + gx, pz = fz0 + gz;
                    if (px < 0 || pz < 0 || px >= _texSize || pz >= _texSize) continue;
                    int idx = pz * _texSize + px;
                    if (!fog || IsExplored(ex, exo, idx)) sig += ex != null && idx < ex.Length && ex[idx] ? 2 : 1;   // own vs others' ground
                    if (_plantedForest.Contains(idx)) sig += 7919;
                    else if (_clearedForest.Contains(idx)) sig += 104729;
                    List<PieceRec> bucket;
                    if (buildings && _pieces.TryGetValue(idx, out bucket)) sig += bucket.Count * 131L;
                }
            float zs = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : 64f;
            int zx0 = Mathf.FloorToInt(x0 / zs + 0.5f), zx1 = Mathf.FloorToInt((x0 + size) / zs + 0.5f);
            int zz0 = Mathf.FloorToInt(z0 / zs + 0.5f), zz1 = Mathf.FloorToInt((z0 + size) / zs + 0.5f);
            List<ZDO>[] sectors = DLevels[t.Level] <= DObjectsMaxMpp && _cfgDetailObjects.Value ? Sectors() : null;
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    int rev;
                    if (_dZoneRev.TryGetValue(DZoneKey(zx, zz), out rev)) sig += rev * 1000003L;
                    List<ZDO> list = SectorList(sectors, zx, zz);
                    if (list != null) sig += list.Count * 65537L;
                }
            sig += (_cfgShowBuildings.Value ? 1 : 0) * 3 + (_cfgShowPaths.Value ? 1 : 0) * 5 + (_cfgShowCleared.Value ? 1 : 0) * 11;
            return sig;
        }

        private List<ZDO>[] Sectors()
        {
            if (_fiObjectsBySector == null || ZDOMan.instance == null) return null;
            return _fiObjectsBySector.GetValue(ZDOMan.instance) as List<ZDO>[];
        }

        private static List<ZDO> SectorList(List<ZDO>[] sectors, int zx, int zz)
        {
            if (sectors == null) return null;
            uint idx = ZoneSystem.SectorToIndex(zx, zz).Sector;
            if (idx >= sectors.Length) return null;
            return sectors[idx];
        }

        // ------------------------------------------------------------------
        // jobs: snapshot on the main thread
        // ------------------------------------------------------------------
        private void Enqueue(DTile tile, float priority)
        {
            Enqueue(tile, priority, false);
        }

        private void Enqueue(DTile tile, float priority, bool relight)
        {
            _dEnqueuedThisFrame++;
            Minimap mm = _mm;
            float x0, z0, size; int fx0, fz0, w;
            TileArea(tile, out x0, out z0, out size, out fx0, out fz0, out w);
            float mpp = DLevels[tile.Level];

            DJob j = new DJob();
            j.Tile = tile; j.X0 = x0; j.Z0 = z0; j.Mpp = mpp; j.Priority = priority;
            j.Sig = RegionSig(tile);
            if (PortWanted) j.Port = SnapshotPort(x0, z0, DTileSize * mpp);
            if (_dLight == 0 && j.Port != null) _dLight = PortLightSig();
            j.LightSig = _dLight; j.Relight = relight;
            j.WaterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            j.ZoneSize = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : 64f;
            j.Relief = _cfgDetailRelief.Value;
            j.ForestShade = _cfgDetailForestShade.Value;
            j.WaterShallow = _cfgDetailWaterShallow.Value;
            j.WaterDeep = _cfgDetailWaterDeep.Value;
            j.Tree = _cfgDetailTreeColor.Value;
            j.Rock = _cfgDetailRockColor.Value;
            Color mist = mm.m_blackforestColor;
            try { if (_fiMistColor != null) mist = (Color)_fiMistColor.GetValue(mm); } catch { }
            j.Biome = new Color32[10];
            j.Biome[0] = mm.m_meadowsColor; j.Biome[1] = mm.m_swampColor; j.Biome[2] = mm.m_mountainColor;
            j.Biome[3] = mm.m_blackforestColor; j.Biome[4] = mm.m_heathColor; j.Biome[5] = mm.m_ashlandsColor;
            j.Biome[6] = mm.m_deepnorthColor; j.Biome[7] = Color.white; j.Biome[8] = Color.white; j.Biome[9] = mist;
            for (int i = 0; i < j.Biome.Length; i++) j.Biome[i] = Tone(j.Biome[i]);
            j.Tree = Tone(j.Tree); j.Rock = Tone(j.Rock);

            // explored cells and the forest corrections, on the vanilla map grid
            j.PixelSize = _pixelSize; j.TexSize = _texSize;
            j.FogX0 = fx0; j.FogZ0 = fz0; j.FogW = w; j.FogH = w;
            j.Fog = new float[w * w];
            float shared = Mathf.Clamp01(_cfgDetailSharedOpacity.Value);
            j.ForestFix = new sbyte[w * w];
            BitArray ex = _fiExplored != null ? _fiExplored.GetValue(mm) as BitArray : null;
            BitArray exo = _fiExploredOthers != null ? _fiExploredOthers.GetValue(mm) as BitArray : null;
            bool fog = _cfgRespectFog.Value;
            bool cleared = _cfgShowForest.Value, planted = _cfgShowPlanted.Value;
            for (int gz = 0; gz < w; gz++)
                for (int gx = 0; gx < w; gx++)
                {
                    int px = fx0 + gx, pz = fz0 + gz;
                    int k = gz * w + gx;
                    if (px < 0 || pz < 0 || px >= _texSize || pz >= _texSize) continue;
                    int idx = pz * _texSize + px;
                    // own ground opaque; ground known only from others (cartography table) lets
                    // the vanilla haze over it show through
                    j.Fog[k] = !fog || (ex == null && exo == null) || (ex != null && idx < ex.Length && ex[idx]) ? 1f
                             : exo != null && idx < exo.Length && exo[idx] ? shared : 0f;
                    if (planted && _plantedForest.Contains(idx)) j.ForestFix[k] = 1;
                    else if (cleared && _clearedForest.Contains(idx)) j.ForestFix[k] = -1;
                }

            // zones: height edits, and at the finer levels the objects themselves
            j.Heights = new Dictionary<long, HeightZone>();
            j.LiveZones = new HashSet<long>();
            j.Trees = new List<DTree>();
            j.Boxes = new List<DBox>();
            float zs = j.ZoneSize;
            int zx0 = Mathf.FloorToInt(x0 / zs + 0.5f) - 1, zx1 = Mathf.FloorToInt((x0 + size) / zs + 0.5f) + 1;
            int zz0 = Mathf.FloorToInt(z0 / zs + 0.5f) - 1, zz1 = Mathf.FloorToInt((z0 + size) / zs + 0.5f) + 1;
            List<ZDO>[] sectors = mpp <= DObjectsMaxMpp && _cfgDetailObjects.Value ? Sectors() : null;
            bool onlyPlayer = _cfgOnlyPlayerBuilt.Value, buildings = _cfgShowBuildings.Value;
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    long zk = DZoneKey(zx, zz);
                    HeightZone hz;
                    if (_dHeights.TryGetValue(zk, out hz)) j.Heights[zk] = hz;
                    List<ZDO> list = SectorList(sectors, zx, zz);
                    if (list == null || list.Count == 0) continue;
                    j.LiveZones.Add(zk);
                    for (int i = 0; i < list.Count; i++)
                        SnapshotObject(j, list[i], x0, z0, size, onlyPlayer, buildings);
                }

            // stored footprints, only where the zone's objects were not read
            j.Pieces = new List<PieceRec>();
            if (buildings)
            {
                for (int gz = 0; gz < w; gz++)
                    for (int gx = 0; gx < w; gx++)
                    {
                        int px = fx0 + gx, pz = fz0 + gz;
                        if (px < 0 || pz < 0 || px >= _texSize || pz >= _texSize) continue;
                        List<PieceRec> bucket;
                        if (!_pieces.TryGetValue(pz * _texSize + px, out bucket)) continue;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            PieceRec r = bucket[i];
                            if (r.X1 < x0 || r.X0 > x0 + size || r.Z1 < z0 || r.Z0 > z0 + size) continue;
                            if (j.LiveZones.Count > 0 && j.LiveZones.Contains(DZoneKey(Mathf.FloorToInt(r.CX / zs + 0.5f), Mathf.FloorToInt(r.CZ / zs + 0.5f)))) continue;
                            j.Pieces.Add(r);
                        }
                    }
            }
            j.Boxes.Sort((a, b) => a.Top.CompareTo(b.Top));
            j.MatColors = new Color32[_cfgMatColor.Length];
            for (int i = 0; i < _cfgMatColor.Length; i++) j.MatColors[i] = Tone(_cfgMatColor[i].Value);
            j.MatUnknown = Tone(_cfgMatUnknownColor.Value);
            j.Outline = Tone(_cfgOutlineColor.Value);
            j.DrawOutline = _cfgOutline.Value;

            j.TerrKeys = new List<long>(); j.TerrKinds = new List<byte>();
            j.Grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            if (_cfgShowPaths.Value && _terrain.Count > 0)
            {
                bool showCleared = _cfgShowCleared.Value;
                int gx0 = Mathf.FloorToInt(x0 / j.Grid) - 1, gx1 = Mathf.CeilToInt((x0 + size) / j.Grid);
                int gz0 = Mathf.FloorToInt(z0 / j.Grid) - 1, gz1 = Mathf.CeilToInt((z0 + size) / j.Grid);
                long cells = (long)(gx1 - gx0 + 1) * (gz1 - gz0 + 1);
                if (cells <= _terrain.Count)
                {
                    for (int gz = gz0; gz <= gz1; gz++)
                        for (int gx = gx0; gx <= gx1; gx++)
                        {
                            long key = ((long)gx << 32) | (uint)gz;
                            byte kind;
                            if (!_terrain.TryGetValue(key, out kind) || (kind == TerrainCleared && !showCleared)) continue;
                            j.TerrKeys.Add(key); j.TerrKinds.Add(kind);
                        }
                }
                else
                {
                    foreach (KeyValuePair<long, byte> kv in _terrain)
                    {
                        if (kv.Value == TerrainCleared && !showCleared) continue;
                        int gx = (int)(kv.Key >> 32), gz = (int)kv.Key;
                        if (gx < gx0 || gx > gx1 || gz < gz0 || gz > gz1) continue;
                        j.TerrKeys.Add(kv.Key); j.TerrKinds.Add(kv.Value);
                    }
                }
            }
            j.Paved = Tone(_cfgPavedColor.Value); j.Dirt = Tone(_cfgDirtColor.Value);
            j.Cultivated = Tone(_cfgCultivatedColor.Value); j.Cleared = Tone(_cfgClearedColor.Value);

            tile.Queued = true;
            lock (_dLock) { _dPending.Add(j); Monitor.Pulse(_dLock); }
        }

        private void SnapshotObject(DJob j, ZDO zdo, float x0, float z0, float size, bool onlyPlayer, bool buildings)
        {
            if (zdo == null || !zdo.IsValid()) return;
            int hash = zdo.GetPrefab();
            if (hash == 0) return;
            byte kind = DKind(hash);
            if (kind == 0) return;
            Vector3 p = zdo.GetPosition();
            const float pad = 20f;
            if (p.x < x0 - pad || p.x > x0 + size + pad || p.z < z0 - pad || p.z > z0 + size + pad) return;

            if (kind == 2 && p.y < j.WaterLevel) return;          // seabed rocks: the vanilla map shows none
            if (kind == 1 || kind == 2)
            {
                float s = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.one).x;
                if (s <= 0.01f) s = 1f;
                DTree t = new DTree();
                t.X = p.x; t.Z = p.z; t.Rock = kind == 2;
                t.R = (kind == 1 ? 2.6f : 1.6f) * Mathf.Clamp(s, 0.3f, 3f);
                j.Trees.Add(t);
                return;
            }
            if (!buildings) return;
            if (onlyPlayer && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L) return;
            PrefabInfo info;
            if (!_prefabCache.TryGetValue(hash, out info)) return;
            float hx = (info.Max.x - info.Min.x) * 0.5f, hzz = (info.Max.z - info.Min.z) * 0.5f;
            if (hx * 2f > MaxFootprint || hzz * 2f > MaxFootprint || hx <= 0f || hzz <= 0f) return;
            Quaternion q = zdo.GetRotation();
            Vector3 lc = new Vector3((info.Min.x + info.Max.x) * 0.5f, 0f, (info.Min.z + info.Max.z) * 0.5f);
            Vector3 c = p + q * lc;
            float yaw = q.eulerAngles.y * Mathf.Deg2Rad;
            DBox b = new DBox();
            b.X = c.x; b.Z = c.z; b.HX = hx; b.HZ = hzz;
            b.Cos = Mathf.Cos(yaw); b.Sin = Mathf.Sin(yaw);
            b.Top = p.y + info.Max.y;
            b.Mat = info.Mat;
            j.Boxes.Add(b);
        }

        private byte DKind(int hash)
        {
            byte k;
            if (_dKind.TryGetValue(hash, out k)) return k;
            k = 0;
            PrefabInfo info;
            if (!_prefabCache.TryGetValue(hash, out info))
            {
                info = BuildPrefabInfo(hash);
                _prefabCache[hash] = info;
            }
            if (info.IsPiece) k = 3;
            else if (info.IsTree) k = 1;
            else if (!info.IsTerrain && ZNetScene.instance != null)
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(hash);
                if (prefab != null)
                {
                    if (prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null) k = 2;
                    else if (prefab.GetComponent<Destructible>() != null && prefab.name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0) k = 2;
                }
            }
            _dKind[hash] = k;
            return k;
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
                if (j.Relight && t.Ready && t.Tex != null)
                {
                    t.Pending = j.Pixels; t.Pending2 = j.Pixels2; t.PendingLight = j.LightSig;   // shown together with the others
                    continue;
                }
                t.Pending = null; t.Pending2 = null; t.LightSig = j.LightSig;
                SetFogTexture(t, j.Pixels2);
                if (t.Tex == null)
                {
                    t.Tex = new Texture2D(DTileSize, DTileSize, TextureFormat.RGBA32, false, false);
                    t.Tex.wrapMode = TextureWrapMode.Clamp;
                    t.Tex.filterMode = j.Port != null && j.Port.Cells > 0f ? FilterMode.Point : FilterMode.Bilinear;   // crisp map pixels
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
                t.Ready = true; t.Sig = j.Sig; t.RenderedAt = Time.realtimeSinceStartup;
                _dRendered++; _dMsTotal += j.Ms;
                if (_cfgDebug.Value && _dRendered % 20 == 0)
                    Logger.LogInfo("Detail: " + _dRendered + " tiles drawn, " + (_dMsTotal / _dRendered).ToString("0") + " ms per tile on average (last " + j.Ms.ToString("0") +
                                   " ms at " + j.Mpp + " m/px, " + j.Trees.Count + " trees/rocks, " + j.Boxes.Count + " boxes); cached " + _dTiles.Count);
            }
        }

        private void SetFogTexture(DTile t, byte[] px)
        {
            if (px == null)
            {
                if (t.Img2 != null) { UnityEngine.Object.Destroy(t.Img2.gameObject); t.Img2 = null; }
                if (t.Tex2 != null) { UnityEngine.Object.Destroy(t.Tex2); t.Tex2 = null; }
                return;
            }
            if (t.Tex2 == null)
            {
                t.Tex2 = new Texture2D(DTileSize, DTileSize, TextureFormat.RGBA32, false, false);
                t.Tex2.wrapMode = TextureWrapMode.Clamp;
                t.Tex2.filterMode = FilterMode.Point;
            }
            t.Tex2.LoadRawTextureData(px);
            t.Tex2.Apply(false, false);
            if (t.Img2 == null)
            {
                GameObject go = new GameObject("fog", typeof(RectTransform));
                go.transform.SetParent(_dFogFront, false);
                t.Img2 = go.AddComponent<RawImage>();
                t.Img2.raycastTarget = false;
                t.Img2.enabled = false;
            }
            t.Img2.texture = t.Tex2;
        }

        // The vanilla style is lit by the time of day. When the light changes enough, the visible
        // tiles are drawn again in the background and swapped in all at once, so the map never
        // shows a patchwork of old and new light.
        private long _dLight;
        private float _dLightNext;

        private void RelightTiles(float now)
        {
            if (!PortWanted || _portMat == null) return;
            if (now >= _dLightNext)
            {
                _dLightNext = now + 2f;
                _dLight = PortLightSig();
            }
            bool complete = true;
            int pending = 0;
            foreach (DTile t in _dTiles.Values)
            {
                if (!t.Ready || t.Img == null || !t.Img.enabled || t.LightSig == _dLight) continue;
                if (t.Pending != null && t.PendingLight == _dLight) { pending++; continue; }
                complete = false;
                if (!t.Queued && _dEnqueuedThisFrame < DEnqueuesPerFrame) Enqueue(t, 1f, true);
            }
            if (!complete || pending == 0) return;
            foreach (DTile t in _dTiles.Values)
            {
                if (t.Pending == null || t.PendingLight != _dLight || t.Tex == null) continue;
                t.Tex.LoadRawTextureData(t.Pending);
                t.Tex.Apply(false, false);
                SetFogTexture(t, t.Pending2);
                t.LightSig = t.PendingLight; t.Pending = null; t.Pending2 = null;
            }
        }

        // Two visible ready tiles per frame: redraw if what they cover changed (explored ground,
        // buildings, paths, height edits, forest corrections, objects in their zones).
        private void RecheckTiles(float now)
        {
            if (_dTiles.Count == 0) return;
            _dScratch.Clear();
            foreach (DTile t in _dTiles.Values) if (t.Ready && !t.Queued && t.Img != null && t.Img.enabled) _dScratch.Add(t);
            if (_dScratch.Count == 0) return;
            for (int n = 0; n < 2 && _dEnqueuedThisFrame < DEnqueuesPerFrame; n++)
            {
                _dRecheckCursor = (_dRecheckCursor + 1) % _dScratch.Count;
                DTile t = _dScratch[_dRecheckCursor];
                if (now - t.RenderedAt < DRedrawMinAge) continue;
                if (RegionSig(t) != t.Sig) Enqueue(t, 0f);
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
                DestroyTile(t);
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
                try { j.Pixels = j.Port != null ? RenderPortTile(j, wg) : RenderTile(j, wg); }
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
                    float hh = wg.GetBiomeHeight(b, wx, wz, out mask, false, true) + HeightDelta(j, wx, wz);
                    int k = sz * gs + sx;
                    h[k] = hh;
                    bio[k] = BiomeIndex(b);
                    forest[k] = j.LiveZones.Contains(ZoneOf(j, wx, wz)) ? 0f : ForestAmount(b, wx, wz, hh, j.WaterLevel);
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
                        float f = Shade(dhx, dhz, light, flat, relief);
                        r *= f; g *= f; b *= f;

                        float fa = Sample(forest, gs, fx, fz);
                        int fk = FogIndex(j, wx, wz);
                        if (fk >= 0 && j.ForestFix[fk] != 0 && !j.LiveZones.Contains(ZoneOf(j, wx, wz))) fa = j.ForestFix[fk] > 0 ? 1f : 0f;
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

            // rocks, then tree shadows and crowns
            for (int i = 0; i < j.Trees.Count; i++) if (j.Trees[i].Rock) DrawDisc(px, n, j, j.Trees[i], j.Rock, 0f, 0.9f);
            for (int i = 0; i < j.Trees.Count; i++) if (!j.Trees[i].Rock) DrawShadow(px, n, j, j.Trees[i]);
            for (int i = 0; i < j.Trees.Count; i++) if (!j.Trees[i].Rock) DrawDisc(px, n, j, j.Trees[i], j.Tree, 0.35f, 0.95f);

            // stored footprints (zones not read), then rotated boxes lowest first, outlines under fills
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
            if (j.DrawOutline)
            {
                float w = Mathf.Max(0.5f, mpp);
                for (int i = 0; i < j.Boxes.Count; i++) FillBox(px, n, j, j.Boxes[i], w, j.Outline, 1f);
            }
            for (int i = 0; i < j.Boxes.Count; i++)
            {
                DBox bx = j.Boxes[i];
                Color32 c = bx.Mat != MatNone && bx.Mat < j.MatColors.Length ? j.MatColors[bx.Mat] : j.MatUnknown;
                float ground = Sample(h, gs, (bx.X - j.X0) / mpp / DStep + 1f - 0.25f, (bx.Z - j.Z0) / mpp / DStep + 1f - 0.25f);
                float lift = Mathf.Clamp(0.82f + 0.035f * (bx.Top - ground), 0.82f, 1.15f);   // higher = lighter: roofs over walls
                FillBox(px, n, j, bx, 0f, c, lift);
            }
            return px;
        }

        private static float Shade(float dhx, float dhz, Vector3 light, float flat, float relief)
        {
            Vector3 nrm = new Vector3(-dhx, 1f, -dhz).normalized;
            float lit = Vector3.Dot(nrm, light);
            return Mathf.Clamp(1f + relief * (lit - flat) * 1.4f, 0.35f, 1.5f);
        }

        private static long ZoneOf(DJob j, float wx, float wz)
        {
            return DZoneKey(Mathf.FloorToInt(wx / j.ZoneSize + 0.5f), Mathf.FloorToInt(wz / j.ZoneSize + 0.5f));
        }

        // players' terrain edits: bilinear over the zone's heightmap vertices
        private static float HeightDelta(DJob j, float wx, float wz)
        {
            if (j.Heights.Count == 0) return 0f;
            HeightZone z;
            if (!j.Heights.TryGetValue(ZoneOf(j, wx, wz), out z)) return 0f;
            float fx = (wx - z.Cx) / z.Scale + z.Half, fz = (wz - z.Cz) / z.Scale + z.Half;
            int max = z.Pitch - 1;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, max - 1), z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, max - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float[] d = z.D;
            int p = z.Pitch;
            return Lerp(Lerp(d[z0 * p + x0], d[z0 * p + x0 + 1], tx), Lerp(d[(z0 + 1) * p + x0], d[(z0 + 1) * p + x0 + 1], tx), tz);
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

        // a round crown or rock: lit from the north-west, soft edge
        private static void DrawDisc(byte[] px, int n, DJob j, DTree t, Color32 c, float highlight, float opacity)
        {
            float r = t.R;
            int ix0 = Mathf.Max(0, Mathf.FloorToInt((t.X - r - j.X0) / j.Mpp)), ix1 = Mathf.Min(n - 1, Mathf.CeilToInt((t.X + r - j.X0) / j.Mpp));
            int iz0 = Mathf.Max(0, Mathf.FloorToInt((t.Z - r - j.Z0) / j.Mpp)), iz1 = Mathf.Min(n - 1, Mathf.CeilToInt((t.Z + r - j.Z0) / j.Mpp));
            if (ix1 < ix0 || iz1 < iz0) return;
            float soft = Mathf.Max(j.Mpp, r * 0.25f);
            for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    float dx = j.X0 + (ix + 0.5f) * j.Mpp - t.X, dz = j.Z0 + (iz + 0.5f) * j.Mpp - t.Z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d > r) continue;
                    float a = opacity * Mathf.Clamp01((r - d) / soft);
                    float lit = 1f + highlight * Mathf.Clamp((-dx + dz) / (r * 1.41f), -1f, 1f);   // brighter towards the north-west
                    Blend(px, (iz * n + ix) * 4, c.r * lit, c.g * lit, c.b * lit, a);
                }
        }

        private static void DrawShadow(byte[] px, int n, DJob j, DTree t)
        {
            DTree s = t;
            s.X += t.R * 0.35f; s.Z -= t.R * 0.35f;              // to the south-east
            Color32 black = new Color32(0, 0, 0, 255);
            DrawDisc(px, n, j, s, black, 0f, 0.35f);
        }

        private static void FillBox(byte[] px, int n, DJob j, DBox b, float grow, Color32 c, float lift)
        {
            float hx = b.HX + grow, hz = b.HZ + grow;
            float ext = Mathf.Sqrt(hx * hx + hz * hz);
            int ix0 = Mathf.Max(0, Mathf.FloorToInt((b.X - ext - j.X0) / j.Mpp)), ix1 = Mathf.Min(n - 1, Mathf.CeilToInt((b.X + ext - j.X0) / j.Mpp));
            int iz0 = Mathf.Max(0, Mathf.FloorToInt((b.Z - ext - j.Z0) / j.Mpp)), iz1 = Mathf.Min(n - 1, Mathf.CeilToInt((b.Z + ext - j.Z0) / j.Mpp));
            if (ix1 < ix0 || iz1 < iz0) return;
            float a = c.a / 255f;
            float half = j.Mpp * 0.5f;                            // cover a pixel if its centre is within half a pixel
            for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    float dx = j.X0 + (ix + 0.5f) * j.Mpp - b.X, dz = j.Z0 + (iz + 0.5f) * j.Mpp - b.Z;
                    // world -> local: the inverse of a yaw rotation (Unity's y rotation turns +z towards +x)
                    float lx = dx * b.Cos - dz * b.Sin;
                    float lz = dx * b.Sin + dz * b.Cos;
                    if (Mathf.Abs(lx) > hx + half || Mathf.Abs(lz) > hz + half) continue;
                    Blend(px, (iz * n + ix) * 4, c.r * lift, c.g * lift, c.b * lift, a);
                }
        }

        private static void Blend(byte[] px, int o, float r, float g, float b, float a)
        {
            float ia = 1f - a;
            px[o] = ToByte(r * a + px[o] * ia);
            px[o + 1] = ToByte(g * a + px[o + 1] * ia);
            px[o + 2] = ToByte(b * a + px[o + 2] * ia);
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
            return j.Fog[z * j.FogW + x];
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
            float a = c.a / 255f;
            for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                    Blend(px, (iz * n + ix) * 4, c.r, c.g, c.b, a);
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
