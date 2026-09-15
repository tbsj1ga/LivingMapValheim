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
        private long _worldUid;
        private bool _storeChanged;

        // ------------------------------------------------------------------
        // persistence
        // ------------------------------------------------------------------
        private string StorePath()
        {
            return Path.Combine(Path.Combine(Paths.ConfigPath, "LivingMap"), _worldUid + ".bin");
        }

        private void LoadStore()
        {
            try
            {
                if (_worldUid == 0L) return;
                string path = StorePath();
                MigrateStoreFromMapOverlay(path);
                if (!File.Exists(path)) return;

                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (br.ReadUInt32() != 0x334F4D4Du) return;   // "MMO3"
                    int version = br.ReadInt32();
                    if (version < 3 || version > 6) return;
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

                    int maxIdx = _texSize * _texSize;
                    if (version >= 4)
                    {
                        int clearedCount = br.ReadInt32();
                        for (int i = 0; i < clearedCount; i++)
                        {
                            int idx = br.ReadInt32();
                            if (idx >= 0 && idx < maxIdx) _clearedForest.Add(idx);
                        }
                    }
                    if (version >= 5)
                    {
                        int plantedCount = br.ReadInt32();
                        for (int i = 0; i < plantedCount; i++)
                        {
                            int idx = br.ReadInt32();
                            if (idx >= 0 && idx < maxIdx) _plantedForest.Add(idx);
                        }
                    }

                    // pieces were collected under a rule; older files knew no rule, i.e. everything
                    bool onlyPlayerBuilt = version >= 6 && br.ReadBoolean();
                    _piecesOnlyPlayerBuilt = onlyPlayerBuilt;
                    if (onlyPlayerBuilt != _cfgOnlyPlayerBuilt.Value) DropPieces("the file was collected with OnlyPlayerBuilt " + (onlyPlayerBuilt ? "on" : "off"));
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not read the stored overlay, starting fresh: " + e.Message);
                _pieces.Clear();
                _terrain.Clear();
                _clearedForest.Clear();
                _plantedForest.Clear();
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
                    bw.Write(6);
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

                    bw.Write(_clearedForest.Count);
                    foreach (int idx in _clearedForest) bw.Write(idx);

                    bw.Write(_plantedForest.Count);
                    foreach (int idx in _plantedForest) bw.Write(idx);

                    bw.Write(_piecesOnlyPlayerBuilt);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _storeChanged = false;

                if (_cfgDebug.Value)
                    Logger.LogInfo("Overlay saved: " + _pieceCount + " pieces, " + _terrain.Count + " terrain cells, forest pixels cleared " + _clearedForest.Count + " / planted " + _plantedForest.Count + ".");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not save the overlay: " + e.Message);
            }
        }
    }
}
