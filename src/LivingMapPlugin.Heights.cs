using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace LivingMap
{
    // The heights the map goes by are the world generator's, and nothing a player ever does
    // changes them. The shader reads them for almost every decision it makes: below the water
    // line it draws the sea from its own colours and never looks at the map texture at all,
    // from 70 m it lays the rock pattern over it, and in between it draws the coast line and
    // shades the ground by the slope between neighbouring texels.
    //
    // So a causeway raised across a lake was still a lake to the map, and anything Living Map
    // painted there was simply not read - which is why such things had to be drawn on top of
    // the finished picture, where they look like a sticker on it.
    //
    // This hands the shader a corrected copy instead: the generator's heights with the
    // players' terrain edits added, which the mod already reads out of the terrain records for
    // the detailed map. The game then draws a causeway as land itself - its own coast line
    // around it, its own paper, light, fog and clouds over it - and Living Map's paint reaches
    // it through the same path as paint on any other ground.
    //
    // Only the map material's texture is swapped. Minimap.m_heightTexture is left alone, so
    // the detailed map, which adds the same edits itself, and anything else that reads the
    // game's own heights still see the generator's.
    public partial class LivingMapPlugin
    {
        private ConfigEntry<bool> _cfgHeightFromTerrain;

        private Texture2D _heightTex;                       // the copy handed to the map material
        private Texture _origSmallHeight, _origLargeHeight;
        private bool _heightDirty, _heightUnavailable;
        private readonly Dictionary<int, float> _heightApplied = new Dictionary<int, float>();   // pixel -> the metres added to it

        private const float HeightEditMin = 0.5f;           // metres: below this an edit is noise
        private const int HeightSamples = 3;                // samples across a map pixel, each way

        private bool HeightsWanted
        {
            get { return _cfgHeightFromTerrain == null || _cfgHeightFromTerrain.Value; }
        }

        private void BindHeightConfig()
        {
            _cfgHeightFromTerrain = Config.Bind(SecLayers, "HeightFromTerrain", true,
                Ui("Tell the game's map about ground the players have raised or dug out. The map goes by the world generator's heights, which no amount of digging changes, and it decides from them where the sea is, where the rock of the high ground starts and where the coast line runs - so a dirt causeway across a lake was still lake to it, and nothing drawn there reached the map at all. With this on, the map is handed those heights with the terrain edits added, and draws a causeway as land itself, with its own coast line and light. A map pixel is 12 m across, so a narrow causeway becomes a 12 m strip of land and the water's edge moves by a pixel. Off: the map keeps the generator's heights exactly.",
                    UiAdvanced, "Ground the players changed counts as ground", 70, true));
        }

        // ------------------------------------------------------------------
        // which map pixels the players moved, and by how much
        // ------------------------------------------------------------------
        // The edits come per zone, as a grid of deltas in metres (the same the detailed map
        // draws its relief from). A map pixel is bigger than that grid, so it is sampled
        // across: the strongest raise in it wins, and if nothing was raised, the deepest cut.
        // A causeway two metres wide then still turns its pixel into land, which is the whole
        // point - the map cannot say anything narrower than one pixel anyway.
        private void CollectHeightEdits(Dictionary<int, float> into)
        {
            into.Clear();
            if (_dHeights.Count == 0 || _texSize <= 0 || _pixelSize <= 0f) return;

            float half = _texSize * 0.5f;
            float step = _pixelSize / (HeightSamples + 1);
            foreach (KeyValuePair<long, HeightZone> kv in _dHeights)
            {
                HeightZone z = kv.Value;
                if (z == null || z.D == null || z.Pitch < 2) continue;
                float zhalf = z.Scale * (z.Pitch - 1) * 0.5f;
                int px0 = Mathf.FloorToInt((z.Cx - zhalf) / _pixelSize + half);
                int px1 = Mathf.CeilToInt((z.Cx + zhalf) / _pixelSize + half);
                int pz0 = Mathf.FloorToInt((z.Cz - zhalf) / _pixelSize + half);
                int pz1 = Mathf.CeilToInt((z.Cz + zhalf) / _pixelSize + half);
                for (int pz = Mathf.Max(pz0, 0); pz <= Mathf.Min(pz1, _texSize - 1); pz++)
                {
                    // the world rect this texel covers, as the shader reads it: [i, i + 1)
                    float wz0 = (pz - half) * _pixelSize;
                    for (int px = Mathf.Max(px0, 0); px <= Mathf.Min(px1, _texSize - 1); px++)
                    {
                        float wx0 = (px - half) * _pixelSize;
                        float up = 0f, down = 0f;
                        for (int sz = 1; sz <= HeightSamples; sz++)
                            for (int sx = 1; sx <= HeightSamples; sx++)
                            {
                                float d = ZoneDelta(z, wx0 + step * sx, wz0 + step * sz);
                                if (d > up) up = d;
                                else if (d < down) down = d;
                            }
                        float delta = up >= HeightEditMin ? up : down <= -HeightEditMin ? down : 0f;
                        if (delta == 0f) continue;
                        int idx = pz * _texSize + px;
                        float had;
                        // zones overlap a pixel at their edges: the strongest edit in it wins
                        if (into.TryGetValue(idx, out had) && Mathf.Abs(had) >= Mathf.Abs(delta)) continue;
                        into[idx] = delta;
                    }
                }
            }
        }

        private static float ZoneDelta(HeightZone z, float wx, float wz)
        {
            float fx = (wx - z.Cx) / z.Scale + z.Half, fz = (wz - z.Cz) / z.Scale + z.Half;
            int max = z.Pitch - 1;
            if (fx < 0f || fz < 0f || fx > max || fz > max) return 0f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, max - 1), z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, max - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float[] d = z.D;
            int p = z.Pitch;
            float a = Mathf.Lerp(d[z0 * p + x0], d[z0 * p + x0 + 1], tx);
            float b = Mathf.Lerp(d[(z0 + 1) * p + x0], d[(z0 + 1) * p + x0 + 1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        // ------------------------------------------------------------------
        // the texture
        // ------------------------------------------------------------------
        private Texture2D VanillaHeightTexture()
        {
            Texture2D t = s_fiHeightTex != null ? s_fiHeightTex.GetValue(_mm) as Texture2D : null;
            return t != null && t.width == _texSize && t.height == _texSize ? t : null;
        }

        private void RebuildHeightLayer()
        {
            _heightDirty = false;
            if (_mm == null || _texSize <= 0 || _heightUnavailable) return;
            if (!HeightsWanted) { DropHeightTexture(); return; }

            Texture2D src = VanillaHeightTexture();
            if (src == null)
            {
                _heightUnavailable = true;
                LogOnce("height-tex", "The map's height texture is not available; ground the players raised is left to the hidden-ground layer.");
                return;
            }

            Dictionary<int, float> want = new Dictionary<int, float>();
            try { CollectHeightEdits(want); }
            catch (Exception e) { _heightUnavailable = true; HandleError("terrain heights", e); return; }

            if (want.Count == 0 && _heightTex == null) return;      // nothing edited anywhere yet

            try
            {
                _sw.Restart();
                if (_heightTex == null && !CreateHeightTexture(src)) return;

                int set = 0, cleared = 0;
                foreach (KeyValuePair<int, float> kv in want)
                {
                    float had;
                    if (_heightApplied.TryGetValue(kv.Key, out had) && Mathf.Approximately(had, kv.Value)) continue;
                    int x = kv.Key % _texSize, y = kv.Key / _texSize;
                    Color c = src.GetPixel(x, y);
                    c.r += kv.Value;
                    _heightTex.SetPixel(x, y, c);
                    set++;
                }
                if (_heightApplied.Count > 0)
                {
                    List<int> gone = new List<int>();
                    foreach (KeyValuePair<int, float> kv in _heightApplied)
                        if (!want.ContainsKey(kv.Key)) gone.Add(kv.Key);
                    for (int i = 0; i < gone.Count; i++)
                    {
                        int x = gone[i] % _texSize, y = gone[i] / _texSize;
                        _heightTex.SetPixel(x, y, src.GetPixel(x, y));   // back to the generator's own
                        cleared++;
                    }
                }
                if (set == 0 && cleared == 0) return;

                _heightTex.Apply(false);
                _heightApplied.Clear();
                foreach (KeyValuePair<int, float> kv in want) _heightApplied[kv.Key] = kv.Value;

                // the overlay draws what the map hides, and this just stopped it hiding these
                _highPixels = null; _highWater = null;
                _highDirty = true; _highSig = -1;

                if (_cfgDebug.Value)
                    Logger.LogInfo(string.Format("Map heights: {0} pixels raised or cut ({1} changed, {2} put back), {3:0.0} ms CPU",
                        _heightApplied.Count, set, cleared, _sw.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                _heightUnavailable = true;
                DropHeightTexture();
                HandleError("map heights", e);
            }
        }

        private bool CreateHeightTexture(Texture2D src)
        {
            Texture2D tex = new Texture2D(_texSize, _texSize, src.format, false);
            tex.name = "LivingMap_Heights";
            tex.filterMode = src.filterMode;
            tex.wrapMode = src.wrapMode;
            const int band = 64;
            for (int y = 0; y < _texSize; y += band)
            {
                int h = Mathf.Min(band, _texSize - y);
                tex.SetPixels(0, y, _texSize, h, src.GetPixels(0, y, _texSize, h));
            }
            tex.Apply(false);
            _heightTex = tex;
            _heightApplied.Clear();
            ApplyHeightToMap(tex, true);
            if (_cfgDebug.Value)
                Logger.LogInfo("Map heights: a copy of the map's height texture (" + _texSize + "px, " + src.format + ") is handed to the map shader.");
            return true;
        }

        private void ApplyHeightToMap(Texture tex, bool rememberOriginals)
        {
            if (_mm == null) return;
            if (_mm.m_mapImageSmall != null && _mm.m_mapImageSmall.material != null)
            {
                if (rememberOriginals) _origSmallHeight = _mm.m_mapImageSmall.material.GetTexture("_HeightTex");
                _mm.m_mapImageSmall.material.SetTexture("_HeightTex", tex);
            }
            if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
            {
                if (rememberOriginals) _origLargeHeight = _mm.m_mapImageLarge.material.GetTexture("_HeightTex");
                _mm.m_mapImageLarge.material.SetTexture("_HeightTex", tex);
            }
            PokeMinimap();
        }

        private void RestoreHeightTexture()
        {
            if (_mm == null) return;
            Texture2D src = VanillaHeightTexture();
            if (_mm.m_mapImageSmall != null && _mm.m_mapImageSmall.material != null)
                _mm.m_mapImageSmall.material.SetTexture("_HeightTex", _origSmallHeight != null ? _origSmallHeight : src);
            if (_mm.m_mapImageLarge != null && _mm.m_mapImageLarge.material != null)
                _mm.m_mapImageLarge.material.SetTexture("_HeightTex", _origLargeHeight != null ? _origLargeHeight : src);
            PokeMinimap();
        }

        private void DropHeightTexture()
        {
            if (_heightTex == null) return;
            RestoreHeightTexture();
            try { UnityEngine.Object.Destroy(_heightTex); } catch { }
            _heightTex = null;
            _heightApplied.Clear();
            _origSmallHeight = null; _origLargeHeight = null;
            _highPixels = null; _highWater = null;      // what the map hides has changed back
            _highDirty = true; _highSig = -1;
        }

        private string HeightStatus()
        {
            if (!HeightsWanted) return "map heights: the generator's own" + HereText();
            if (_heightUnavailable) return "map heights: unavailable";
            if (_heightTex == null) return "map heights: nothing raised or cut yet" + HereText();
            return string.Format("map heights: {0} pixels of ground the players changed, {1:0.0} MB{2}",
                _heightApplied.Count, _texSize * (double)_texSize * 4 / (1024 * 1024), HereText());
        }

        // What the map shader is told about the ground under your feet: the generator's height,
        // what the players' digging adds to it, and which side of the water line that puts it.
        // One line settles "why is my causeway still water" without guessing.
        private string HereText()
        {
            try
            {
                if (Player.m_localPlayer == null) return "";
                int idx;
                if (!WorldToIndex(Player.m_localPlayer.transform.position, out idx)) return "";
                Texture2D src = VanillaHeightTexture();
                if (src == null) return "";
                float gen = src.GetPixel(idx % _texSize, idx / _texSize).r;
                float add;
                _heightApplied.TryGetValue(idx, out add);
                float shown = gen + add;
                return string.Format("; under you: pixel {0},{1} - generator {2:0.0} m{3}, the map sees {4} (water below {5:0.0} m)",
                    idx % _texSize, idx / _texSize, gen,
                    add == 0f ? "" : string.Format(" {0}{1:0.0} m from digging", add > 0f ? "+" : "", add),
                    shown < WaterHidesBelow ? "water" : shown >= HighGroundFrom ? "rock" : "ground",
                    WaterHidesBelow);
            }
            catch { return ""; }
        }
    }
}
