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
        private const float MapRebuildInterval = 0.5f;    // seconds between texture updates after something changed
        private float _nextRtRebuild;
        private int _rtScale = 1;
        private int _rtRebuilds;

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
                rt.name = "LivingMap_MapTexture";
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
                    mask.name = "LivingMap_ForestMask";
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
                _sw.Restart();

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
                        "Map layer rebuilt on the GPU: {0}px ({1:0.##} m/px), {2} shapes drawn, {3} held back by fog, {4:0} MB of video memory, linear colours {5}, {6:0.0} ms CPU",
                        _rt.width, _pixelSize / _rtScale, quads, _fogPieces.Count + _fogTerrain.Count,
                        (_rt.width * (long)_rt.height + (_rtMask != null ? _rtMask.width * (long)_rtMask.height : 0L)) * 4L / (1024f * 1024f),
                        LinearColors ? "on" : "off", _sw.Elapsed.TotalMilliseconds));
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
                _sw.Restart();

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
                    Logger.LogInfo(string.Format("Map layer updated in place: {0} new pieces, {1} new cells, {2} quads, {3:0.00} ms CPU (update #{4})",
                        pieces, cells, quads, _sw.Elapsed.TotalMilliseconds, _rtIncrements));
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
                _sw.Restart();

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
                    Logger.LogInfo(string.Format("Forest mask rebuilt: {0} cleared, {1} planted, {2} held back by fog, {3:0.0} ms CPU",
                        clearedDrawn, plantedDrawn, _fogForest.Count, _sw.Elapsed.TotalMilliseconds));
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
    }
}
