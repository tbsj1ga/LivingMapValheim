using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BepInEx.Configuration;
using UnityEngine;

namespace LivingMap
{
    // The host's map for the players on its server. The host has the whole world's object
    // database, so its layers are complete and current; a client only ever learns what the
    // server sent it near where it walked. With this the host hands its layers to every player
    // with Living Map, and each of them draws them only on ground their own map has explored:
    // nothing new is revealed, but where they have been the map is the host's - a rebuilt base,
    // new paths, cleared forest, even if they have not been back since.
    //
    // The world is cut into 128 m tiles. The host keeps, per tile, its layers serialised in a
    // fixed order and a hash of them, and sends the list of (tile, hash) to each player with
    // the mod when they join and whenever something changed. The player asks for the tiles it
    // does not have or has with another hash, a batch at a time, and the host answers with
    // those tiles, compressed. A tile that left the list is empty on the host now.
    //
    // The player keeps the host's tiles in memory and applies to its own layers only the parts
    // that lie on explored ground; the rest waits and is applied as the map gets explored.
    // Players without the mod are not touched: the routed RPCs only go to those who said hello.
    public partial class LivingMapPlugin
    {
        private const string SecShare = "09 Multiplayer";
        private const int ShareProto = 1;
        private const float ShareTile = 128f;
        private const float ShareRefresh = 60f;             // host: seconds between checks for changes
        private const float ShareReapply = 20f;             // player: seconds between applying newly explored parts
        private const int ShareReplyBytes = 200 * 1024;     // host: raw tile bytes per answer (a network message holds 512 KB)
        private const int ShareAskTiles = 128;              // player: tiles asked for at a time
        private const int ShareTilesPerFrame = 64;          // host: tiles serialised per frame

        private const string RpcHello = "LivingMap_Hello";
        private const string RpcIndex = "LivingMap_Index";
        private const string RpcGet = "LivingMap_Get";
        private const string RpcTiles = "LivingMap_Tiles";

        private ConfigEntry<bool> _cfgShare;
        private ConfigEntry<bool> _cfgUseHostMap;

        private ZRoutedRpc _rpcRegistered;
        private int _storeRev, _sharedRev = -1;

        private void BindShareConfig()
        {
            _cfgShare = Config.Bind(SecShare, "ShareWithPlayers", true,
                Ui("When you host: give your map (buildings, paths, cleared and planted forest) to the players on your server who have Living Map. Each of them sees it only on ground their own map has explored.", UiShare, "Share my map with players", 100, false));
            _cfgUseHostMap = Config.Bind(SecShare, "UseHostMap", true,
                Ui("When you play on someone's server: take the host's map where your own map is explored, so a base rebuilt while you were away shows as it is now. Needs Living Map on the host.", UiShare, "Use the host's map", 90, false));
        }

        // every place that changes the stored layers goes through here, so the host knows when to look again
        private void MarkStoreChanged()
        {
            _storeChanged = true;
            _storeRev++;
        }

        private bool ShareHost
        {
            get
            {
                return _cfgShare.Value && ZNet.instance != null && ZNet.instance.IsServer() && !ZNet.instance.IsDedicated()
                    && _zdoAuthoritative && _zdoPasses > 0;
            }
        }

        private bool ShareClient
        {
            get { return _cfgUseHostMap.Value && ZNet.instance != null && !ZNet.instance.IsServer(); }
        }

        private static int TileId(float x, float z)
        {
            int tx = Mathf.FloorToInt(x / ShareTile), tz = Mathf.FloorToInt(z / ShareTile);
            return ((tx & 0xFFFF) << 16) | (tz & 0xFFFF);
        }

        private static void TileOrigin(int id, out float x0, out float z0)
        {
            int tx = (short)(id >> 16), tz = (short)(id & 0xFFFF);
            x0 = tx * ShareTile; z0 = tz * ShareTile;
        }

        // called from Tick
        private void ShareTick(float now)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) { _rpcRegistered = null; return; }
            if (rpc != _rpcRegistered)
            {
                _rpcRegistered = rpc;
                rpc.Register<ZPackage>(RpcHello, RpcOnHello);
                rpc.Register<ZPackage>(RpcIndex, RpcOnIndex);
                rpc.Register<ZPackage>(RpcGet, RpcOnGet);
                rpc.Register<ZPackage>(RpcTiles, RpcOnTiles);
                _hostPeers.Clear(); _hostTiles.Clear(); _sharedRev = -1; _hostBuilding = false;
                _mirror.Clear(); _wantQueue.Clear(); _helloSent = false; _waiting = false;
            }
            if (!_ready) return;
            if (ShareHost) HostTick(now);
            else if (ShareClient) ClientTick(now);
        }

        // ==================================================================
        // host
        // ==================================================================
        private sealed class HostTile { public long Hash; public byte[] Data; }

        private readonly HashSet<long> _hostPeers = new HashSet<long>();
        private Dictionary<int, HostTile> _hostTiles = new Dictionary<int, HostTile>();
        private bool _hostBuilding;
        private float _nextShareCheck;
        private int _shareSentTiles;
        private long _shareSentBytes;

        private void HostTick(float now)
        {
            if (_hostBuilding || now < _nextShareCheck) return;
            _nextShareCheck = now + (_sharedRev < 0 ? 2f : ShareRefresh);
            // players who left
            List<long> gone = null;
            foreach (long uid in _hostPeers) if (ZNet.instance.GetPeer(uid) == null) (gone ?? (gone = new List<long>())).Add(uid);
            if (gone != null) foreach (long uid in gone) _hostPeers.Remove(uid);
            if (_hostPeers.Count == 0 || _storeRev == _sharedRev) return;
            StartCoroutine(BuildHostTiles(true));
        }

        private void RpcOnHello(long sender, ZPackage pkg)
        {
            try
            {
                if (!_cfgShare.Value || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                int proto = pkg.ReadInt();
                if (proto != ShareProto) { LogOnce("shareproto" + sender, "A player has another Living Map map-sharing version; they keep their own map."); return; }
                _hostPeers.Add(sender);
                if (!ShareHost) return;                       // the first pass is not done yet: HostTick builds after it
                if (_storeRev == _sharedRev && !_hostBuilding) SendIndex(sender);
                else if (!_hostBuilding) StartCoroutine(BuildHostTiles(true));
                // when a build is running, its end sends the index to everyone who said hello
            }
            catch (Exception e) { HandleError("share/hello", e); }
        }

        // Groups the layers by tile and serialises each tile in a fixed order, a few tiles per frame.
        private IEnumerator BuildHostTiles(bool announce)
        {
            _hostBuilding = true;
            int rev = _storeRev;
            Dictionary<int, TileData> tiles = new Dictionary<int, TileData>();
            try
            {
                float half = _texSize * 0.5f;
                // a piece goes with the tile of its map pixel's centre, as the player buckets it
                foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                {
                    TileData td = Tile(tiles, TileId((kv.Key % _texSize - half) * _pixelSize, (kv.Key / _texSize - half) * _pixelSize));
                    td.Pieces.AddRange(kv.Value);
                }
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                foreach (KeyValuePair<long, byte> kv in _terrain)
                {
                    int gx = (int)(kv.Key >> 32), gz = (int)(uint)kv.Key;
                    Tile(tiles, TileId((gx + 0.5f) * grid, (gz + 0.5f) * grid)).Terrain.Add(new KeyValuePair<long, byte>(kv.Key, kv.Value));
                }
                foreach (int idx in _clearedForest)
                    Tile(tiles, TileId((idx % _texSize - half) * _pixelSize, (idx / _texSize - half) * _pixelSize)).Cleared.Add(idx);
                foreach (int idx in _plantedForest)
                    Tile(tiles, TileId((idx % _texSize - half) * _pixelSize, (idx / _texSize - half) * _pixelSize)).Planted.Add(idx);
            }
            catch (Exception e) { HandleError("share/group", e); _hostBuilding = false; yield break; }

            Dictionary<int, HostTile> built = new Dictionary<int, HostTile>();
            int n = 0;
            foreach (KeyValuePair<int, TileData> kv in tiles)
            {
                byte[] data = SerializeTile(kv.Value);
                built[kv.Key] = new HostTile { Hash = Hash(data), Data = data };
                if (++n % ShareTilesPerFrame == 0) yield return null;
            }
            _hostTiles = built;
            _sharedRev = rev;
            _hostBuilding = false;
            if (announce) foreach (long uid in _hostPeers) SendIndex(uid);
        }

        private static TileData Tile(Dictionary<int, TileData> tiles, int id)
        {
            TileData t;
            if (!tiles.TryGetValue(id, out t)) { t = new TileData(); tiles[id] = t; }
            return t;
        }

        private void SendIndex(long uid)
        {
            if (ZNet.instance.GetPeer(uid) == null) return;
            ZPackage p = new ZPackage();
            p.Write(ShareProto);
            p.Write(_worldUid);
            p.Write(_texSize);
            p.Write(_pixelSize);
            p.Write(Mathf.Max(0.5f, _cfgTerrainGrid.Value));
            p.Write(_piecesOnlyPlayerBuilt);
            p.Write(_hostTiles.Count);
            foreach (KeyValuePair<int, HostTile> kv in _hostTiles) { p.Write(kv.Key); p.Write(kv.Value.Hash); }
            ZRoutedRpc.instance.InvokeRoutedRPC(uid, RpcIndex, p);
        }

        private void RpcOnGet(long sender, ZPackage pkg)
        {
            try
            {
                if (!ShareHost || !_hostPeers.Contains(sender)) return;
                int count = pkg.ReadInt();
                if (count < 0 || count > 4096) return;
                MemoryStream raw = new MemoryStream();
                BinaryWriter w = new BinaryWriter(raw);
                int found = 0, handled = 0;
                List<int> ids = new List<int>();
                for (int i = 0; i < count; i++) ids.Add(pkg.ReadInt());
                foreach (int id in ids)
                {
                    HostTile t;
                    if (_hostTiles.TryGetValue(id, out t))
                    {
                        if (found > 0 && raw.Length + t.Data.Length > ShareReplyBytes) break;   // the rest in the next answer
                        w.Write(id); w.Write(t.Hash); w.Write(t.Data.Length); w.Write(t.Data);
                        w.Flush();
                        found++;
                    }
                    handled++;
                }
                w.Flush();
                byte[] packed = Deflate(raw.ToArray());
                ZPackage p = new ZPackage();
                p.Write(handled);
                p.Write(found);
                p.Write(packed);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcTiles, p);
                _shareSentTiles += found;
                _shareSentBytes += packed.Length;
            }
            catch (Exception e) { HandleError("share/get", e); }
        }

        // ==================================================================
        // tile contents
        // ==================================================================
        private sealed class TileData
        {
            public readonly List<PieceRec> Pieces = new List<PieceRec>();
            public readonly List<KeyValuePair<long, byte>> Terrain = new List<KeyValuePair<long, byte>>();
            public readonly List<int> Cleared = new List<int>();
            public readonly List<int> Planted = new List<int>();
        }

        private static byte[] SerializeTile(TileData t)
        {
            t.Pieces.Sort(ComparePieces);
            t.Terrain.Sort((a, b) => a.Key.CompareTo(b.Key));
            t.Cleared.Sort();
            t.Planted.Sort();
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(t.Pieces.Count);
            foreach (PieceRec r in t.Pieces) { w.Write(r.X0); w.Write(r.X1); w.Write(r.Z0); w.Write(r.Z1); w.Write(r.Y); w.Write(r.Mat); }
            w.Write(t.Terrain.Count);
            foreach (KeyValuePair<long, byte> c in t.Terrain) { w.Write(c.Key); w.Write(c.Value); }
            w.Write(t.Cleared.Count);
            foreach (int i in t.Cleared) w.Write(i);
            w.Write(t.Planted.Count);
            foreach (int i in t.Planted) w.Write(i);
            w.Flush();
            return ms.ToArray();
        }

        private static TileData DeserializeTile(byte[] data)
        {
            TileData t = new TileData();
            BinaryReader r = new BinaryReader(new MemoryStream(data));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                PieceRec p = new PieceRec();
                p.X0 = r.ReadSingle(); p.X1 = r.ReadSingle(); p.Z0 = r.ReadSingle(); p.Z1 = r.ReadSingle(); p.Y = r.ReadSingle(); p.Mat = r.ReadByte();
                t.Pieces.Add(p);
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) { long k = r.ReadInt64(); t.Terrain.Add(new KeyValuePair<long, byte>(k, r.ReadByte())); }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) t.Cleared.Add(r.ReadInt32());
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) t.Planted.Add(r.ReadInt32());
            return t;
        }

        private static int ComparePieces(PieceRec a, PieceRec b)
        {
            int c = a.X0.CompareTo(b.X0); if (c != 0) return c;
            c = a.Z0.CompareTo(b.Z0); if (c != 0) return c;
            c = a.Y.CompareTo(b.Y); if (c != 0) return c;
            c = a.X1.CompareTo(b.X1); if (c != 0) return c;
            c = a.Z1.CompareTo(b.Z1); if (c != 0) return c;
            return a.Mat.CompareTo(b.Mat);
        }

        // FNV-1a, 64 bit
        private static long Hash(byte[] data)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 1099511628211UL; }
            return (long)h;
        }

        private static byte[] Deflate(byte[] raw)
        {
            MemoryStream ms = new MemoryStream();
            using (DeflateStream d = new DeflateStream(ms, CompressionMode.Compress, true)) d.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private static byte[] Inflate(byte[] packed)
        {
            MemoryStream outMs = new MemoryStream();
            using (DeflateStream d = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = d.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
            }
            return outMs.ToArray();
        }

        // ==================================================================
        // player
        // ==================================================================
        private sealed class MirrorTile
        {
            public long Hash;
            public TileData Data;
            public bool Hidden;          // part of it lies on ground not explored yet
            public bool Pending;         // to be applied
        }

        private readonly Dictionary<int, MirrorTile> _mirror = new Dictionary<int, MirrorTile>();
        private readonly Dictionary<int, List<long>> _ownTerrainByTile = new Dictionary<int, List<long>>();
        private readonly Queue<int> _wantQueue = new Queue<int>();
        private readonly Dictionary<int, long> _indexHashes = new Dictionary<int, long>();
        private readonly List<int> _asked = new List<int>();
        private bool _helloSent, _waiting;
        private float _waitingSince, _nextReapply;
        private bool _hostPieces, _hostTerrain, _hostForest;
        private int _shareGotTiles;
        private long _shareGotBytes;

        private void ClientTick(float now)
        {
            if (ServerPeer() == 0L) return;                 // not connected yet
            if (!_helloSent)
            {
                _helloSent = true;
                ZPackage p = new ZPackage();
                p.Write(ShareProto);
                ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), RpcHello, p);
            }
            if (_waiting && now - _waitingSince > 30f) _waiting = false;    // an answer that never came
            if (!_waiting && _wantQueue.Count > 0) RequestBatch(now);

            bool due = now >= _nextReapply;
            if (due) _nextReapply = now + ShareReapply;
            ApplyMirror(due);
        }

        private static long ServerPeer()
        {
            ZNetPeer server = ZNet.instance != null ? ZNet.instance.GetServerPeer() : null;
            return server != null ? server.m_uid : 0L;
        }

        private void RpcOnIndex(long sender, ZPackage pkg)
        {
            try
            {
                if (!ShareClient || ZNet.instance.IsServer()) return;
                if (pkg.ReadInt() != ShareProto) return;
                long world = pkg.ReadLong();
                int texSize = pkg.ReadInt();
                float pixelSize = pkg.ReadSingle();
                float grid = pkg.ReadSingle();
                bool onlyPlayerBuilt = pkg.ReadBool();
                if (world != _worldUid) return;
                // what can be taken: pieces in world coordinates always, unless they were collected
                // under another OnlyPlayerBuilt rule; path cells only on the same grid; forest
                // pixels only on the same map texture
                _hostPieces = onlyPlayerBuilt == _cfgOnlyPlayerBuilt.Value;
                _hostTerrain = Mathf.Abs(grid - Mathf.Max(0.5f, _cfgTerrainGrid.Value)) < 0.001f;
                _hostForest = texSize == _texSize && Mathf.Abs(pixelSize - _pixelSize) < 0.001f;
                if (!_hostPieces) LogOnce("sharerule", "The host collects buildings with another 'Only player buildings' setting; its buildings are not taken.");
                if (!_hostTerrain) LogOnce("sharegrid", "The host uses another TerrainGridSize; its paths are not taken.");

                _indexHashes.Clear();
                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++) { int id = pkg.ReadInt(); _indexHashes[id] = pkg.ReadLong(); }

                // tiles the host no longer has: empty there now
                List<int> dropped = new List<int>();
                foreach (KeyValuePair<int, MirrorTile> kv in _mirror) if (!_indexHashes.ContainsKey(kv.Key)) dropped.Add(kv.Key);
                foreach (int id in dropped)
                {
                    _mirror[id].Data = new TileData();
                    _mirror[id].Hash = 0;
                    _mirror[id].Pending = true;
                }
                _wantQueue.Clear();
                foreach (KeyValuePair<int, long> kv in _indexHashes)
                {
                    MirrorTile m;
                    if (!_mirror.TryGetValue(kv.Key, out m) || m.Hash != kv.Value) _wantQueue.Enqueue(kv.Key);
                }
                if (_cfgDebug.Value) Logger.LogInfo("Host map index: " + count + " tiles, " + _wantQueue.Count + " to fetch, " + dropped.Count + " emptied.");
            }
            catch (Exception e) { HandleError("share/index", e); }
        }

        private void RequestBatch(float now)
        {
            ZPackage p = new ZPackage();
            List<int> ids = _asked;
            ids.Clear();
            while (_wantQueue.Count > 0 && ids.Count < ShareAskTiles) ids.Add(_wantQueue.Dequeue());
            p.Write(ids.Count);
            foreach (int id in ids) p.Write(id);
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), RpcGet, p);
            _waiting = true;
            _waitingSince = now;
        }

        private void RpcOnTiles(long sender, ZPackage pkg)
        {
            try
            {
                _waiting = false;
                if (!ShareClient || ZNet.instance.IsServer()) return;
                int handled = pkg.ReadInt();
                int found = pkg.ReadInt();
                byte[] packed = pkg.ReadByteArray();
                // what the host did not get to (its answer is capped) is asked for again
                for (int i = Mathf.Max(0, handled); i < _asked.Count; i++) _wantQueue.Enqueue(_asked[i]);
                _asked.Clear();
                _shareGotBytes += packed.Length;
                BinaryReader r = new BinaryReader(new MemoryStream(Inflate(packed)));
                for (int i = 0; i < found; i++)
                {
                    int id = r.ReadInt32();
                    long hash = r.ReadInt64();
                    byte[] data = r.ReadBytes(r.ReadInt32());
                    MirrorTile m;
                    if (!_mirror.TryGetValue(id, out m)) { m = new MirrorTile(); _mirror[id] = m; }
                    m.Hash = hash;
                    m.Data = DeserializeTile(data);
                    m.Pending = true;
                    _shareGotTiles++;
                }
            }
            catch (Exception e) { HandleError("share/tiles", e); }
        }

        // Puts the explored parts of the host's tiles into our own layers: new tiles always, and
        // on a timer the tiles that still had parts on unexplored ground.
        private void ApplyMirror(bool reapplyHidden)
        {
            if (_mirror.Count == 0) return;
            List<int> todo = null;
            foreach (KeyValuePair<int, MirrorTile> kv in _mirror)
                if (kv.Value.Pending || (reapplyHidden && kv.Value.Hidden)) (todo ?? (todo = new List<int>())).Add(kv.Key);
            if (todo == null) return;

            // our own path cells by tile, once for the round
            _ownTerrainByTile.Clear();
            if (_hostTerrain)
            {
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                foreach (KeyValuePair<long, byte> c in _terrain)
                {
                    int gx = (int)(c.Key >> 32), gz = (int)(uint)c.Key;
                    int tid = TileId((gx + 0.5f) * grid, (gz + 0.5f) * grid);
                    List<long> l;
                    if (!_ownTerrainByTile.TryGetValue(tid, out l)) { l = new List<long>(); _ownTerrainByTile[tid] = l; }
                    l.Add(c.Key);
                }
            }
            BitArray ex = _fiExplored != null && _mm != null ? _fiExplored.GetValue(_mm) as BitArray : null;
            BitArray exO = _fiExploredOthers != null && _mm != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
            // without our explored map nothing can be limited to explored ground: take nothing
            if (ex == null && exO == null) { LogOnce("shareexplored", "The map's explored area could not be read; the host's map is not used."); return; }
            bool changed = false;
            int applied = 0;
            foreach (int id in todo)
            {
                MirrorTile m = _mirror[id];
                if (ApplyTile(id, m, ex, exO)) changed = true;
                m.Pending = false;
                if (m.Data.Pieces.Count == 0 && m.Data.Terrain.Count == 0 && m.Data.Cleared.Count == 0 && m.Data.Planted.Count == 0 && m.Hash == 0)
                    _mirror.Remove(id);
                if (++applied >= 256) break;                // the rest next frame
            }
            if (changed) MarkStoreChanged();
        }

        private bool ApplyTile(int id, MirrorTile m, BitArray ex, BitArray exO)
        {
            float x0, z0;
            TileOrigin(id, out x0, out z0);
            float half = _texSize * 0.5f;
            bool changed = false, hidden = false;

            // the map pixels whose centre lies in the tile
            int px0 = Mathf.CeilToInt(x0 / _pixelSize + half), px1 = Mathf.CeilToInt((x0 + ShareTile) / _pixelSize + half) - 1;
            int py0 = Mathf.CeilToInt(z0 / _pixelSize + half), py1 = Mathf.CeilToInt((z0 + ShareTile) / _pixelSize + half) - 1;
            px0 = Mathf.Max(px0, 0); py0 = Mathf.Max(py0, 0); px1 = Mathf.Min(px1, _texSize - 1); py1 = Mathf.Min(py1, _texSize - 1);

            if (_hostPieces)
            {
                Dictionary<int, List<PieceRec>> byPixel = new Dictionary<int, List<PieceRec>>();
                foreach (PieceRec r in m.Data.Pieces)
                {
                    int idx;
                    if (!WorldToIndex(new Vector3(r.CX, r.Y, r.CZ), out idx)) continue;
                    List<PieceRec> l;
                    if (!byPixel.TryGetValue(idx, out l)) { l = new List<PieceRec>(); byPixel[idx] = l; }
                    l.Add(r);
                }
                for (int py = py0; py <= py1; py++)
                    for (int px = px0; px <= px1; px++)
                    {
                        int idx = py * _texSize + px;
                        List<PieceRec> host;
                        byPixel.TryGetValue(idx, out host);
                        List<PieceRec> mine;
                        bool had = _pieces.TryGetValue(idx, out mine);
                        if (host == null && !had) continue;
                        if (!IsExplored(ex, exO, idx)) { if (host != null) hidden = true; continue; }
                        if (host == null)
                        {
                            _pieceCount -= mine.Count;
                            ReturnList(mine);
                            _pieces.Remove(idx);
                            _rtDirty = true;
                            changed = true;
                            continue;
                        }
                        if (had && SameBucket(mine, host)) continue;
                        int oldN = had ? mine.Count : 0;
                        if (_pieceCount - oldN + host.Count > _cfgMaxPieces.Value) { LogOnce("piececap", "Reached MaxPieces (" + _cfgMaxPieces.Value + "), no more build pieces are recorded."); continue; }
                        List<PieceRec> list = RentList();
                        list.AddRange(host);
                        if (DiffBucket(had ? mine : null, list)) _rtDirty = true;
                        _pieces[idx] = list;
                        _pieceCount += list.Count - oldN;
                        if (had) ReturnList(mine);
                        changed = true;
                    }
            }

            if (_hostTerrain)
            {
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                Dictionary<long, byte> host = new Dictionary<long, byte>();
                foreach (KeyValuePair<long, byte> c in m.Data.Terrain) host[c.Key] = c.Value;
                // ours in this tile that the host does not have any more
                _terrainScratch.Clear();
                List<long> own;
                if (_ownTerrainByTile.TryGetValue(id, out own))
                    foreach (long k in own)
                    {
                        if (host.ContainsKey(k)) continue;
                        int gx = (int)(k >> 32), gz = (int)(uint)k;
                        if (!IsWorldExploredAlways(ex, exO, (gx + 0.5f) * grid, (gz + 0.5f) * grid)) continue;
                        _terrainScratch.Add(k);
                    }
                foreach (long k in _terrainScratch) { _terrain.Remove(k); _rtDirty = true; changed = true; }
                _terrainScratch.Clear();
                foreach (KeyValuePair<long, byte> c in host)
                {
                    int gx = (int)(c.Key >> 32), gz = (int)(uint)c.Key;
                    if (!IsWorldExploredAlways(ex, exO, (gx + 0.5f) * grid, (gz + 0.5f) * grid)) { hidden = true; continue; }
                    byte cur;
                    bool had = _terrain.TryGetValue(c.Key, out cur);
                    if (had && cur == c.Value) continue;
                    if (!had && _terrain.Count >= _cfgMaxTerrain.Value) continue;
                    _terrain[c.Key] = c.Value;
                    _rtDirty = true;
                    changed = true;
                }
            }

            if (_hostForest)
            {
                if (ApplyForest(m.Data.Cleared, _clearedForest, px0, px1, py0, py1, ex, exO, ref hidden)) { changed = true; _maskDirty = true; }
                if (ApplyForest(m.Data.Planted, _plantedForest, px0, px1, py0, py1, ex, exO, ref hidden)) { changed = true; _maskDirty = true; }
            }

            m.Hidden = hidden;
            return changed;
        }

        private bool ApplyForest(List<int> host, HashSet<int> mine, int px0, int px1, int py0, int py1, BitArray ex, BitArray exO, ref bool hidden)
        {
            HashSet<int> h = new HashSet<int>(host);
            bool changed = false;
            for (int py = py0; py <= py1; py++)
                for (int px = px0; px <= px1; px++)
                {
                    int idx = py * _texSize + px;
                    bool want = h.Contains(idx), have = mine.Contains(idx);
                    if (want == have) continue;
                    if (!IsExplored(ex, exO, idx)) { if (want) hidden = true; continue; }
                    if (want) mine.Add(idx); else mine.Remove(idx);
                    changed = true;
                }
            return changed;
        }

        // explored ground, whatever RespectFog says: the host's map is only ever taken where we have been
        private bool IsWorldExploredAlways(BitArray ex, BitArray exO, float wx, float wz)
        {
            int idx;
            if (!WorldToIndex(new Vector3(wx, 0f, wz), out idx)) return false;
            return IsExplored(ex, exO, idx);
        }

        private string ShareStatus()
        {
            if (ShareHost) return "map sharing: host, " + _hostPeers.Count + " player(s) with Living Map, " + _hostTiles.Count + " tiles, sent " + _shareSentTiles + " tiles / " + (_shareSentBytes / 1024) + " KB";
            if (ShareClient) return "host map: " + (_indexHashes.Count > 0 ? _mirror.Count + " of " + _indexHashes.Count + " tiles, " + _wantQueue.Count + " to fetch, got " + (_shareGotBytes / 1024) + " KB" : "no answer from the host (no Living Map there, or sharing off)");
            return "map sharing: off";
        }
    }
}
