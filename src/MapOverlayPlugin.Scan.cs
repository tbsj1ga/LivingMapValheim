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
        // the old scanners: physics colliders and the loaded heightmaps around the player

        private Collider[] _colliderBuf;
        private int _pieceLayerMask;

        // ------------------------------------------------------------------
        // scanning
        // ------------------------------------------------------------------
        private void Scan(Vector3 center)
        {
            bool changed = false;
            if (_cfgShowBuildings.Value && !UseZdoScan && ScanBuildings(center)) { changed = true; _rtDirty = true; }
            if (_cfgShowPaths.Value && !_terrainDisabled && !UseZdoPaths) changed |= ScanTerrain(center);
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
            _sw.Restart();
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
                Logger.LogInfo(string.Format("Terrain scan: heightmaps={0}, samples={1}, painted={2}, stored={3}, {4:0.0} ms",
                    maps.Count, samples, painted, _terrain.Count, _sw.Elapsed.TotalMilliseconds));

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
    }
}
