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
        // ------------------------------------------------------------------
        // console command: livingmap
        // ------------------------------------------------------------------
        // The host never needs this: its database is the truth and the map follows it. A client
        // of a dedicated server keeps what earlier sessions collected, and that can go stale;
        // this forgets a layer in an area and lets the scanners fill it in again, without
        // deleting the world file and losing everything else with it.
        private const byte LayerBuildings = 1, LayerPaths = 2, LayerForest = 4;
        private const float DefaultResetRadius = 100f;

        private void RegisterCommands()
        {
            new Terminal.ConsoleCommand("livingmap",
                "Living Map. 'livingmap status' shows what the map remembers; 'livingmap reset [buildings|paths|forest] [metres|all]' forgets it around you (100 m by default) and collects it again",
                delegate(Terminal.ConsoleEventArgs args) { RunCommand(args); });
        }

        private static void Say(Terminal.ConsoleEventArgs args, string text)
        {
            if (args != null && args.Context != null) args.Context.AddString(text);
        }

        private void RunCommand(Terminal.ConsoleEventArgs args)
        {
            try
            {
                string sub = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "";
                if (sub == "status") { Say(args, StatusText()); return; }
                if (sub == "probe") { Say(args, ProbeCommand(args.Args)); return; }
                if (sub == "port") { Say(args, PortTest()); return; }
                if (sub != "reset")
                {
                    Say(args, "livingmap status | livingmap reset [buildings|paths|forest] [metres|all]");
                    return;
                }

                byte layers = 0;
                float radius = DefaultResetRadius;
                bool world = false;
                for (int i = 2; i < args.Args.Length; i++)
                {
                    string t = args.Args[i].ToLowerInvariant();
                    float r;
                    if (t == "buildings" || t == "pieces") layers |= LayerBuildings;
                    else if (t == "paths" || t == "terrain") layers |= LayerPaths;
                    else if (t == "forest" || t == "trees") layers |= LayerForest;
                    else if (t == "all" || t == "world") world = true;
                    else if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r) && r > 0f) radius = r;
                    else { Say(args, "livingmap: unknown word '" + args.Args[i] + "'"); return; }
                }
                if (layers == 0) layers = LayerBuildings | LayerPaths | LayerForest;

                if (!_ready || _mm == null) { Say(args, "livingmap: not attached to a map right now"); return; }
                if (Player.m_localPlayer == null) { Say(args, "livingmap: no player"); return; }

                Say(args, ResetData(layers, Player.m_localPlayer.transform.position, radius, world));
            }
            catch (Exception e)
            {
                HandleError("console command", e);
                Say(args, "livingmap: failed, see the BepInEx log");
            }
        }

        private string StatusText()
        {
            if (!_ready || _mm == null) return "Living Map " + Version + ": not attached to a map";
            return string.Format(
                "Living Map {0}: {1} pieces in {2} map pixels, {3} path cells, forest cleared {4} / planted {5}; buildings via {6}, paths via {7}; map {8}px ({9:0.##} m/px); last pass {10} frames, {11:0.0} ms",
                Version, _pieceCount, _pieces.Count, _terrain.Count, _clearedForest.Count, _plantedForest.Count,
                UseZdoScan ? (_zdoAuthoritative ? "ZDO (host, whole world)" : "ZDO (client)") : "physics",
                UseZdoPaths ? "terrain records" : "loaded heightmaps",
                _rt != null ? _rt.width : 0, _rt != null ? _pixelSize / _rtScale : 0f,
                _zdoPassFrames, _zdoPassMs) + "; " + DetailStatus();
        }

        // Forgets the chosen layers inside the radius (or everywhere) and restarts collection.
        private string ResetData(byte layers, Vector3 center, float radius, bool world)
        {
            float half = _texSize * 0.5f;
            float r2 = radius * radius;
            int pieces = 0, cells = 0, forest = 0;

            if ((layers & LayerBuildings) != 0)
            {
                _removeScratch.Clear();
                foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                {
                    if (!world && !PixelWithin(kv.Key, center, r2, half)) continue;
                    _removeScratch.Add(kv.Key);
                }
                for (int i = 0; i < _removeScratch.Count; i++)
                {
                    List<PieceRec> list = _pieces[_removeScratch[i]];
                    pieces += list.Count;
                    _pieceCount -= list.Count;
                    ReturnList(list);
                    _pieces.Remove(_removeScratch[i]);
                }
                _removeScratch.Clear();
                _addPieces.Clear();
                _fogPieces.Clear();
            }

            if ((layers & LayerPaths) != 0)
            {
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                List<long> gone = new List<long>();
                foreach (KeyValuePair<long, byte> kv in _terrain)
                {
                    if (!world)
                    {
                        float wx = ((int)(kv.Key >> 32) + 0.5f) * grid - center.x;
                        float wz = ((int)(kv.Key & 0xFFFFFFFFL) + 0.5f) * grid - center.z;
                        if (wx * wx + wz * wz > r2) continue;
                    }
                    gone.Add(kv.Key);
                }
                for (int i = 0; i < gone.Count; i++) _terrain.Remove(gone[i]);
                cells = gone.Count;
                _addTerrain.Clear();
                _fogTerrain.Clear();
                _tcRevision.Clear();                                    // every record is read again
                _lastScanPos = new Vector3(float.MinValue, 0f, float.MinValue);
                _nextScanTime = 0f;
            }

            if ((layers & LayerForest) != 0)
            {
                forest += RemovePixels(_clearedForest, center, r2, half, world);
                forest += RemovePixels(_plantedForest, center, r2, half, world);
                _trustedZones.Clear();                                  // client: trust is earned again
                _fogForest.Clear();
                _maskDirty = true;
            }

            ResetZdoPass();
            _nextZdoPass = 0f;
            _rtDirty = true;
            _storeChanged = true;

            string where = world ? "the whole world" : string.Format("{0:0} m around you", radius);
            string text = string.Format("Living Map: forgot {0} pieces, {1} path cells, {2} forest pixels in {3}; collecting again", pieces, cells, forest, where);
            Logger.LogInfo(text);
            return text;
        }

        private bool PixelWithin(int idx, Vector3 center, float r2, float half)
        {
            float dx = (idx % _texSize - half) * _pixelSize - center.x;
            float dz = (idx / _texSize - half) * _pixelSize - center.z;
            return dx * dx + dz * dz <= r2;
        }

        private int RemovePixels(HashSet<int> pixels, Vector3 center, float r2, float half, bool world)
        {
            if (world)
            {
                int n = pixels.Count;
                pixels.Clear();
                return n;
            }
            _removeScratch.Clear();
            foreach (int idx in pixels)
                if (PixelWithin(idx, center, r2, half)) _removeScratch.Add(idx);
            for (int i = 0; i < _removeScratch.Count; i++) pixels.Remove(_removeScratch[i]);
            int removed = _removeScratch.Count;
            _removeScratch.Clear();
            return removed;
        }
    }
}
