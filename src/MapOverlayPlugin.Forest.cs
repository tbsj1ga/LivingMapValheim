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
            _forestMs = 0.0;
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
                _sw.Restart();
                float half = _texSize * 0.5f;
                int r = Mathf.Max(1, Mathf.CeilToInt(_cfgForestRadius.Value / _pixelSize));
                int maxTrees = Mathf.Max(0, _cfgForestMaxTrees.Value);
                int end = Mathf.Min(_trustedZoneScratch.Count, _forestCursor + ForestZonesPerFrame);
                for (; _forestCursor < end; _forestCursor++)
                    EvalForestZone(_trustedZoneScratch[_forestCursor], half, r, maxTrees);
                _forestMs += _sw.Elapsed.TotalMilliseconds;

                if (_forestCursor < _trustedZoneScratch.Count) return;

                _forestCursor = -1;
                if (_forestChanged)
                {
                    _storeChanged = true;
                    _maskDirty = true;
                    if (_cfgDebug.Value)
                        Logger.LogInfo(string.Format("Cleared forest: {0} pixels (+{1}, -{2}) over {3} zones, {4:0.0} ms CPU{5}",
                            _clearedForest.Count, _forestAdded, _forestRemoved, _trustedZoneScratch.Count, _forestMs, SampleText()));
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
    }
}
