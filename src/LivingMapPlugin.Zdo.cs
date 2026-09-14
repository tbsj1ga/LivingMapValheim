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
        // ZDO scanner: the object database instead of the physics scene
        private struct PrefabInfo
        {
            public bool IsPiece;
            public bool IsTree;
            public bool IsTerrain;        // the _TerrainCompiler record of a zone
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
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();   // main-thread time of one step
        private double _zdoPassMs, _forestMs;
        private bool _zdoDisabled;

        private const int MaxZdoErrors = 10;

        private bool _zdoPassPieces;

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
                if (_zdoCursor < 0 && _forestCursor < 0 && _tcCursor < 0)
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
                    _sw.Restart();
                    int end = Mathf.Min(_zdoSnapshot.Count, _zdoCursor + Mathf.Clamp(_cfgZdoPerFrame.Value, 500, 50000));
                    _biomeLookupsThisFrame = 0;
                    for (; _zdoCursor < end && _biomeLookupsThisFrame < BiomeLookupsPerFrame; _zdoCursor++)
                        ExamineZdo(_zdoSnapshot[_zdoCursor]);
                    _zdoPassFrames++;
                    _zdoPassMs += _sw.Elapsed.TotalMilliseconds;

                    if (_zdoCursor >= _zdoSnapshot.Count)
                    {
                        FinishZdoPass();                    // may queue the forest check and the terrain records as further phases
                        if (_forestCursor < 0 && _tcCursor < 0) _nextZdoPass = now + _cfgZdoInterval.Value;
                    }
                    return;
                }

                // second phase: the forest check of the same pass, a few zones per frame
                if (_forestCursor >= 0)
                {
                    StepForestEval();
                    if (_forestCursor < 0 && _tcCursor < 0) _nextZdoPass = now + _cfgZdoInterval.Value;
                    return;
                }

                // third phase: terrain records whose revision changed, a few zones per frame
                StepTerrainRecords();
                if (_tcCursor < 0) _nextZdoPass = now + _cfgZdoInterval.Value;
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
            _zdoPassMs = 0.0;
            _zdoPassOrigin = pos;
            _zdoPassPieces = _cfgShowBuildings.Value;
            _zdoAuthoritative = ZNet.instance.IsServer();
            return true;
        }

        private void ResetZdoPass()
        {
            _zdoCursor = -1;
            _forestCursor = -1;
            _tcCursor = -1;
            _tcZdos.Clear();
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
            if (info.IsTerrain)
            {
                if (UseZdoPaths && _cfgShowPaths.Value) _tcZdos.Add(zdo);
                return;
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
            if (prefab.GetComponent<TerrainComp>() != null)
            {
                info.IsTerrain = true;
                return info;
            }
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

            if (_tcZdos.Count > 0)
            {
                _tcCursor = 0;
                _tcParsed = 0; _tcSkipped = 0; _tcAdded = 0; _tcRemoved = 0; _tcChanged = 0;
                _tcMs = 0.0;
            }

            _zdoPrevOrigin = _zdoPassOrigin;
            _zdoHavePrevOrigin = true;
            _zdoPasses++;

            if (changed) _storeChanged = true;

            if (_zdoPasses == 1 || (_cfgDebug.Value && changed))
                Logger.LogInfo(string.Format(
                    "ZDO scan: {0} build pieces in {1} map pixels, {2}; {3} pixels updated, {4} cleared; forest check queued for {5} zones; {6} frames, {7:0.0} ms CPU",
                    fresh, seenPixels,
                    _zdoAuthoritative ? "host, the whole world" : "client, what the server has sent",
                    replaced, removed, forestZones, _zdoPassFrames, _zdoPassMs));
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
    }
}
