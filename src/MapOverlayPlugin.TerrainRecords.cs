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
        // paths from the terrain records in the database
        private readonly List<ZDO> _tcZdos = new List<ZDO>();                          // records seen by the current pass
        private readonly Dictionary<ZDOID, uint> _tcRevision = new Dictionary<ZDOID, uint>();   // record -> revision last parsed
        private int _tcCursor = -1;                 // index into _tcZdos, -1 = not parsing
        private int _tcParsed, _tcSkipped, _tcAdded, _tcRemoved, _tcChanged, _tcErrorCount, _tcPasses;
        private double _tcMs;
        private bool _tcDisabled;
        private bool[] _tcHeightScratch;
        private byte[] _tcKindScratch;
        private const int TerrainZonesPerFrame = 4;
        private const int MaxTcErrors = 10;

        // ------------------------------------------------------------------
        // paths from the terrain records
        // ------------------------------------------------------------------
        // Every zone anyone has ever hoed carries a _TerrainCompiler object whose ZDO holds the
        // whole zone's terrain edits as one compressed blob (ZDOVars.s_TCData): per vertex of
        // the 65 x 65 grid, whether its height was changed and what paint it got. That is the
        // same data the loaded heightmap shows, readable without loading the zone, and on the
        // host it exists for the entire world. The revision counter on the ZDO says whether a
        // blob changed since it was last read, so a quiet world costs nothing.
        private bool UseZdoPaths
        {
            get
            {
                return UseZdoScan && !_tcDisabled &&
                       string.Equals(_cfgPathSource.Value, "ZDO", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void StepTerrainRecords()
        {
            if (_tcCursor < 0) return;
            try
            {
                _sw.Restart();
                int end = Mathf.Min(_tcZdos.Count, _tcCursor + TerrainZonesPerFrame);
                for (; _tcCursor < end; _tcCursor++)
                {
                    ZDO zdo = _tcZdos[_tcCursor];
                    if (zdo == null || zdo.m_uid.IsNone() || !zdo.IsValid()) continue;

                    uint rev = zdo.DataRevision;
                    uint seen;
                    if (_tcRevision.TryGetValue(zdo.m_uid, out seen) && seen == rev) { _tcSkipped++; continue; }

                    byte[] data = zdo.GetByteArray(ZDOVars.s_TCData, null);
                    if (data == null) { _tcRevision[zdo.m_uid] = rev; continue; }

                    ApplyTerrainRecord(zdo, data);
                    _tcRevision[zdo.m_uid] = rev;
                    _tcParsed++;
                }
                _tcMs += _sw.Elapsed.TotalMilliseconds;

                if (_tcCursor < _tcZdos.Count) return;

                _tcCursor = -1;
                _tcZdos.Clear();
                _tcPasses++;
                if (_tcAdded + _tcRemoved + _tcChanged > 0) _storeChanged = true;
                if (_tcPasses == 1 || (_cfgDebug.Value && _tcParsed > 0))
                    Logger.LogInfo(string.Format(
                        "Terrain records: {0} zones read, {1} unchanged, cells +{2} -{3} ~{4}, {5} stored, {6:0.0} ms CPU",
                        _tcParsed, _tcSkipped, _tcAdded, _tcRemoved, _tcChanged, _terrain.Count, _tcMs));
            }
            catch (Exception e)
            {
                _tcErrorCount++;
                HandleError("terrain records", e);
                if (_tcErrorCount >= MaxTcErrors && !_tcDisabled)
                {
                    _tcDisabled = true;
                    Logger.LogWarning("Reading the terrain records failed too often; paths are sampled from the loaded heightmaps for the rest of this session.");
                }
                _tcCursor = -1;
                _tcZdos.Clear();
            }
        }

        // Replaces this zone's cells with what the record says. The record is authoritative for
        // its zone, so cells it does not paint are removed, and every cell is sampled at the
        // vertex the heightmap scan would have read, so the two sources agree to the metre.
        private void ApplyTerrainRecord(ZDO zdo, byte[] compressed)
        {
            ZPackage pkg = new ZPackage(Utils.Decompress(compressed));
            pkg.ReadInt();          // format version
            pkg.ReadInt();          // operation count
            pkg.ReadVector3();      // last operation point
            pkg.ReadSingle();       // last operation radius

            int heightCount = pkg.ReadInt();
            int pitch = Mathf.RoundToInt(Mathf.Sqrt(heightCount));
            if (pitch < 2 || pitch * pitch != heightCount) throw new InvalidOperationException("terrain record with " + heightCount + " height entries");

            if (_tcHeightScratch == null || _tcHeightScratch.Length < heightCount) _tcHeightScratch = new bool[heightCount];
            bool[] modifiedHeight = _tcHeightScratch;
            for (int i = 0; i < heightCount; i++)
            {
                modifiedHeight[i] = pkg.ReadBool();
                if (modifiedHeight[i]) { pkg.ReadSingle(); pkg.ReadSingle(); }   // level and smooth deltas
            }

            int paintCount = pkg.ReadInt();
            int paintPitch = Mathf.RoundToInt(Mathf.Sqrt(paintCount));
            if (paintPitch < 2 || paintPitch * paintPitch != paintCount) throw new InvalidOperationException("terrain record with " + paintCount + " paint entries");

            if (_tcKindScratch == null || _tcKindScratch.Length < paintCount) _tcKindScratch = new byte[paintCount];
            byte[] kinds = _tcKindScratch;
            bool showCleared = _cfgShowCleared.Value;
            for (int i = 0; i < paintCount; i++)
            {
                bool levelled = showCleared && paintPitch == pitch && modifiedHeight[i];
                if (!pkg.ReadBool())
                {
                    kinds[i] = levelled ? TerrainCleared : TerrainNone;
                    continue;
                }
                float r = pkg.ReadSingle(), g = pkg.ReadSingle(), b = pkg.ReadSingle(), a = pkg.ReadSingle();
                // PaintType in the game is { Dirt = red, Cultivate = green, Paved = blue }
                byte kind = TerrainNone;
                if (b > 0.5f) kind = TerrainPaved;
                else if (r > 0.5f) kind = TerrainDirt;
                else if (g > 0.5f) kind = TerrainCultivated;
                else if (showCleared && (a < 0.5f || levelled)) kind = TerrainCleared;
                kinds[i] = kind;
            }

            // Heightmap.WorldToVertexMask: vertex = floor((world - zoneCentre) / scale + 0.5 + (width + 1) / 2);
            // the heightmap scan queries the mask at (cell centre - 0.5), and so does this
            float zoneSize = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : 64f;
            Vector3 c = ZoneSystem.GetZonePos(ZoneSystem.GetZone(zdo.GetPosition()));
            float scale = zoneSize / (paintPitch - 1);
            int halfMask = paintPitch / 2;
            float zh = zoneSize * 0.5f;

            float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
            int gx0 = Mathf.FloorToInt((c.x - zh) / grid), gx1 = Mathf.CeilToInt((c.x + zh) / grid);
            int gz0 = Mathf.FloorToInt((c.z - zh) / grid), gz1 = Mathf.CeilToInt((c.z + zh) / grid);

            // Debug self-check: when this zone's terrain is loaded, read the same cells the old way
            // and count how the two sources disagree. A shift by one vertex would show up as
            // mismatches in both directions along every path edge; paint that only the loaded
            // terrain knows (locations paint the ground without a record) shows up one-sided.
            Heightmap live = null;
            if (_cfgDebug.Value)
            {
                try
                {
                    List<Heightmap> maps = Heightmap.GetAllHeightmaps();
                    Heightmap cached = null;
                    if (maps != null) live = PickHeightmap(maps, c, ref cached);
                }
                catch { live = null; }
            }
            int liveOnly = 0, recordOnly = 0, kindDiffers = 0, compared = 0;
            string firstMismatch = null;

            for (int gz = gz0; gz <= gz1; gz++)
            {
                int lz = Mathf.FloorToInt(((gz + 0.5f) * grid - 0.5f - c.z) / scale + 0.5f + halfMask);
                if (lz < 0 || lz >= paintPitch) continue;
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    int lx = Mathf.FloorToInt(((gx + 0.5f) * grid - 0.5f - c.x) / scale + 0.5f + halfMask);
                    if (lx < 0 || lx >= paintPitch) continue;

                    byte kind = kinds[lz * paintPitch + lx];
                    long key = ((long)gx << 32) | (uint)gz;

                    if (live != null)
                    {
                        float wx = (gx + 0.5f) * grid, wz = (gz + 0.5f) * grid;
                        if (Mathf.Abs(wx - c.x) < zh && Mathf.Abs(wz - c.z) < zh)
                        {
                            Color mask = live.GetPaintMask(new Vector3(wx - 0.5f, c.y, wz - 0.5f));
                            byte liveKind = TerrainNone;
                            if (mask.b > 0.5f) liveKind = TerrainPaved;
                            else if (mask.r > 0.5f) liveKind = TerrainDirt;
                            else if (mask.g > 0.5f) liveKind = TerrainCultivated;
                            else if (showCleared && mask.a < 0.5f) liveKind = TerrainCleared;
                            compared++;
                            if (liveKind != kind)
                            {
                                if (kind == TerrainNone) liveOnly++;
                                else if (liveKind == TerrainNone) recordOnly++;
                                else kindDiffers++;
                                if (firstMismatch == null) firstMismatch = string.Format(" first at ({0:0}, {1:0}): live {2}, record {3}", wx, wz, liveKind, kind);
                            }
                        }
                    }
                    byte cur;
                    bool had = _terrain.TryGetValue(key, out cur);

                    if (kind == TerrainNone)
                    {
                        if (had) { _terrain.Remove(key); _rtDirty = true; _tcRemoved++; MarkPixelDirty((gx + 0.5f) * grid, (gz + 0.5f) * grid); }
                        continue;
                    }
                    if (had && cur == kind) continue;
                    if (!had && _terrain.Count >= _cfgMaxTerrain.Value)
                    {
                        LogOnce("terraincap", "Reached MaxTerrainCells (" + _cfgMaxTerrain.Value + ").");
                        continue;
                    }
                    _terrain[key] = kind;
                    if (had) { _rtDirty = true; _tcChanged++; }
                    else { _addTerrain.Add(key); _tcAdded++; }
                    MarkPixelDirty((gx + 0.5f) * grid, (gz + 0.5f) * grid);
                }
            }

            if (live != null && compared > 0)
                Logger.LogInfo(string.Format(
                    "Terrain record check at zone ({0:0}, {1:0}): {2} cells compared, live-only {3}, record-only {4}, kind differs {5}{6}",
                    c.x, c.z, compared, liveOnly, recordOnly, kindDiffers, firstMismatch ?? ""));
        }
    }
}
