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
        // the CPU fallback: paints vanilla-resolution pixels when the GPU layer is unavailable

        private readonly Color32[] _onePixel = new Color32[1];

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
    }
}
