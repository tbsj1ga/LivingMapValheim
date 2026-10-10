using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace LivingMap
{
    // High ground: the one place where the map texture is not enough.
    //
    // Above 70 m the game's map shader stops showing that texture. It lays its rock pattern
    // (Assets/UI/map/mountain.png) over the colour it sampled, by height, and the pattern is
    // opaque - alpha 0.999 - at full weight from 95 m. To the shader the texture is only the
    // colour of the ground, so there is nothing to lose; to Living Map, which paints its
    // buildings and paths into that same texture, it means they are simply gone on any
    // mountain. The detailed map draws its own picture and keeps them (see VanillaPort); the
    // zoomed-out map and the minimap are the game's shader and cannot be reached from there.
    //
    // So those shapes are drawn a second time, into a texture of their own that is transparent
    // everywhere else, and it is laid over the map images as a child of each - above the
    // shader, below the pins. Only shapes on ground at or above 70 m go in, so nothing below
    // the snow line is drawn twice, and the texture is created only when such a shape exists:
    // a world without a mountain base costs nothing at all.
    //
    // It is not the same picture as the shader's. Inside the map texture a shape is multiplied
    // by the paper, lit by the sun against the terrain's normal and drawn under the forest
    // stamps and the clouds; here it is the flat colour over everything. On a mountain, where
    // there is no forest and the rock would have erased it anyway, that is the trade.
    public partial class LivingMapPlugin
    {
        private const string HighOff = "Off", HighMinimap = "Minimap", HighBoth = "Both";
        private const float HighGroundFrom = 70f;       // where the shader's rock pattern begins
        private const float WaterHidesBelow = 29.5f;    // and below this it draws water instead of the map

        private ConfigEntry<string> _cfgHighOverlay;
        private ConfigEntry<float> _cfgHighStrength;

        private RenderTexture _rtHigh;
        private Material _glMatHigh;
        private bool _highDirty;
        private bool _highUnavailable;
        private BitArray _highPixels;                   // map pixels whose ground is that high
        private BitArray _highMist;                     // ... and those the Mistlands mist covers
        private RawImage _highSmall, _highLarge;
        private Sprite _highCircle;
        private Color _highLight = Color.white;
        private int _highPieceCount, _highCellCount;    // what the last rebuild drew
        private long _highSig = -1;                     // and what it was: a redraw it does not need is skipped
        private readonly List<PieceRec> _highScratch = new List<PieceRec>();
        private readonly List<long> _highCells = new List<long>();

        private void BindHighGroundConfig()
        {
            _cfgHighOverlay = Config.Bind(SecLayers, "HighGroundOverlay", HighBoth,
                Ui("Buildings and roads the game's map will not show: above 70 metres it covers the ground with its rock pattern, and over water it draws the sea from its own colours - in both it ignores what the mod paints, so a mountain base or a dirt bridge across a lake is not on the minimap or the zoomed-out map at all. This draws them on top. Close up the detailed map draws it properly anyway, so this is for the other two. Both (default): on the minimap and on the zoomed-out map. Minimap: only there, for anyone who would rather the zoomed-out map stayed exactly the game's own picture. Costs a texture of its own, the same size as the map layer (16 to 256 MB, see Map sharpness), and only from the moment something is built that high - livingmap status says how much.",
                    new AcceptableValueList<string>(HighOff, HighMinimap, HighBoth), UiLayers, "Show what the map hides (mountains, water)", 85, false));
            _cfgHighStrength = Config.Bind(SecLayers, "HighGroundStrength", 0.55f,
                Ui("How strongly those buildings show. The layer is drawn over the map rather than into it, so at full strength its colours are the plain ones, which in daylight on snow is louder than anything the map draws itself. It also lies over the clouds and the fog the map draws for itself, which nothing short of drawing into the map could change, so it is kept low: lower lets the rock show through and settles it into the picture; 1 is the plain colour.",
                    new AcceptableValueRange<float>(0.2f, 1f), UiLayers, "...and how strongly", 84, false));
        }

        private string HighMode { get { return _cfgHighOverlay != null ? _cfgHighOverlay.Value : HighOff; } }
        private float HighStrength { get { return _cfgHighStrength != null ? Mathf.Clamp(_cfgHighStrength.Value, 0.2f, 1f) : 0.7f; } }
        private bool HighWanted { get { return !_highUnavailable && HighMode != HighOff && _cfgEnabled != null && _cfgEnabled.Value; } }

        // ------------------------------------------------------------------
        // which map pixels are high ground
        // ------------------------------------------------------------------
        // The same height the shader reads: Minimap.m_heightTexture, one texel per map pixel,
        // the world generator's height in metres in the red channel. Read once per world, in
        // bands, so a 2048 x 2048 map does not want a 64 MB Color[] at once; kept as one bit
        // per pixel (512 KB at the vanilla size).
        private void BuildHighPixels()
        {
            _highPixels = null;
            if (!HighWanted || _mm == null || _texSize <= 0) return;
            try
            {
                Texture2D height = s_fiHeightTex != null ? s_fiHeightTex.GetValue(_mm) as Texture2D : null;
                if (height == null || height.width != _texSize || height.height != _texSize)
                {
                    LogOnce("high-height", "The map's height texture is not available; buildings on high ground are drawn only by the detailed map.");
                    _highUnavailable = true;
                    return;
                }

                // The mist of the Mistlands is drawn by the shader, inside the map's own picture;
                // this layer goes over that picture and would hang its buildings in front of the
                // mist that is there to hide them. The mask's green channel is where the shader
                // reads it from, and it never changes, so it is read once with the heights.
                Texture2D mask = s_fiForestTex != null ? s_fiForestTex.GetValue(_mm) as Texture2D : null;
                if (mask != null && (mask.width != _texSize || mask.height != _texSize)) mask = null;

                BitArray high = new BitArray(_texSize * _texSize);
                BitArray mist = mask != null ? new BitArray(_texSize * _texSize) : null;
                const int band = 64;
                int found = 0, misty = 0;
                for (int z = 0; z < _texSize; z += band)
                {
                    int h = Mathf.Min(band, _texSize - z);
                    Color[] px = height.GetPixels(0, z, _texSize, h);
                    int baseIdx = z * _texSize;
                    for (int i = 0; i < px.Length; i++)
                    {
                        // The two places the game's map does not show what is drawn into it: under
                        // the rock pattern of high ground, and under water - where the shader draws
                        // the sea from its own colours and never reads the map texture at all. Its
                        // heights come from the world generator, so ground a player raised above
                        // the water is still water to it, and a dirt bridge was on no map but the
                        // detailed one.
                        float m = px[i].r;
                        if (m >= HighGroundFrom || m < WaterHidesBelow) { high[baseIdx + i] = true; found++; }
                    }
                    if (mist == null) continue;
                    Color[] mk = mask.GetPixels(0, z, _texSize, h);
                    for (int i = 0; i < mk.Length; i++)
                        if (mk[i].g > 0.35f) { mist[baseIdx + i] = true; misty++; }
                }
                _highPixels = high;
                _highMist = mist;
                if (_cfgDebug.Value)
                    Logger.LogInfo("Hidden ground: " + found + " map pixels above " + HighGroundFrom + " m or under water (" +
                                   (found * 100f / (_texSize * (float)_texSize)).ToString("0.0") + " % of the map), " +
                                   misty + " under the mist.");
            }
            catch (Exception e)
            {
                _highPixels = null; _highMist = null;
                _highUnavailable = true;
                HandleError("high ground pixels", e);
            }
        }

        private bool IsHighPixel(int idx)
        {
            if (_highPixels == null || idx < 0 || idx >= _highPixels.Length || !_highPixels[idx]) return false;
            return _highMist == null || !_highMist[idx];        // under the mist the map shows nothing, and nor does this
        }

        private bool IsHighWorld(float wx, float wz)
        {
            int idx;
            if (!WorldToIndex(new Vector3(wx, 0f, wz), out idx)) return false;
            return IsHighPixel(idx);
        }

        // ------------------------------------------------------------------
        // the layer
        // ------------------------------------------------------------------
        private void RebuildHighLayer()
        {
            _highDirty = false;
            if (!HighWanted || _rt == null || _mm == null) { ReleaseHighLayer(); return; }
            if (_highPixels == null) return;

            try
            {
                _sw.Restart();
                BitArray explored = null, exploredOthers = null;
                if (_cfgRespectFog.Value)
                {
                    explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                    exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
                }

                // One pass over each: what stands on high ground and is explored. A world with
                // nothing built up there keeps neither the lists nor the texture.
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                _highScratch.Clear();
                _highCells.Clear();
                if (_cfgShowBuildings.Value)
                {
                    foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                    {
                        if (!IsHighPixel(kv.Key)) continue;
                        List<PieceRec> bucket = kv.Value;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            PieceRec rec = bucket[i];
                            if (IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) _highScratch.Add(rec);
                        }
                    }
                }
                if (_cfgShowPaths.Value)
                {
                    foreach (KeyValuePair<long, byte> kv in _terrain)
                    {
                        float cx = ((int)(kv.Key >> 32) + 0.5f) * grid, cz = ((int)(kv.Key & 0xFFFFFFFFL) + 0.5f) * grid;
                        if (!IsHighWorld(cx, cz)) continue;
                        if (!IsWorldExplored(explored, exploredOthers, cx, cz)) continue;
                        _highCells.Add(kv.Key);
                    }
                }
                // The map layer updates whenever anything at all changes - the forest alone is
                // reason enough - and drawing 15 000 shapes again costs ten milliseconds in one
                // frame. Walking them is cheap; drawing them is not. So the walk also tells us
                // whether this is the same picture as last time, and if it is, nothing is drawn.
                long sig = 17;
                for (int i = 0; i < _highScratch.Count; i++)
                {
                    PieceRec rec = _highScratch[i];
                    sig = sig * 31 + rec.X0.GetHashCode();
                    sig = sig * 31 + rec.Z0.GetHashCode();
                    sig = sig * 31 + rec.Y.GetHashCode();
                    sig = sig * 31 + rec.Mat;
                }
                for (int i = 0; i < _highCells.Count; i++) sig = sig * 31 + _highCells[i];
                sig = sig * 31 + (_cfgOutline.Value ? 1 : 0);

                if (_highScratch.Count == 0 && _highCells.Count == 0)
                {
                    _highSig = sig;
                    ReleaseHighLayer();
                    return;
                }
                if (sig == _highSig && _rtHigh != null && _rtHigh.IsCreated())
                {
                    _highScratch.Clear(); _highCells.Clear();
                    return;
                }
                _highSig = sig;

                if (!EnsureHighTexture()) return;
                if (!_rtHigh.IsCreated() && !_rtHigh.Create()) return;

                float k = _rtHigh.width / (float)_texSize;
                float half = _texSize * 0.5f;
                bool flip = _cfgMapFlipY.Value;
                int h = _rtHigh.height;
                float minPiece = Mathf.Max(0.1f, _cfgPieceSize.Value);
                float halfGrid = grid * 0.5f;

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = _rtHigh;
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, _rtHigh.width, 0f, h);
                _glMatHigh.SetPass(0);
                GL.Begin(GL.QUADS);
                try
                {
                    for (int i = 0; i < _highCells.Count; i++)
                    {
                        long key = _highCells[i];
                        byte kind;
                        if (!_terrain.TryGetValue(key, out kind)) continue;
                        float cx = ((int)(key >> 32) + 0.5f) * grid, cz = ((int)(key & 0xFFFFFFFFL) + 0.5f) * grid;
                        EmitQuad(cx - halfGrid, cz - halfGrid, cx + halfGrid, cz + halfGrid,
                            Solid(TerrainColor(kind)), k, half, h, flip, PathDrawWidth(grid));
                    }

                    if (_cfgOutline.Value)
                    {
                        float grow = Mathf.Max(0f, _cfgOutlineWidth.Value);
                        Color oc = Solid(_cfgOutlineColor.Value);
                        for (int i = 0; i < _highScratch.Count; i++)
                        {
                            PieceRec rec = _highScratch[i];
                            EmitQuad(rec.X0 - grow, rec.Z0 - grow, rec.X1 + grow, rec.Z1 + grow,
                                oc, k, half, h, flip, minPiece + grow * 2f);
                        }
                    }

                    _highScratch.Sort(ByTop);          // lowest first, as the map layer draws them
                    for (int i = 0; i < _highScratch.Count; i++)
                    {
                        PieceRec rec = _highScratch[i];
                        EmitQuad(rec.X0, rec.Z0, rec.X1, rec.Z1, Solid(MatColor(rec.Mat)), k, half, h, flip, minPiece);
                    }
                }
                finally
                {
                    GL.End();
                    GL.PopMatrix();
                    RenderTexture.active = prev;
                }

                _highPieceCount = _highScratch.Count; _highCellCount = _highCells.Count;
                _highScratch.Clear(); _highCells.Clear();
                if (_cfgDebug.Value)
                    Logger.LogInfo(string.Format("High-ground layer: {0} pieces, {1} path cells, {2}px, {3:0.0} ms CPU",
                        _highPieceCount, _highCellCount, _rtHigh.width, _sw.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                _highUnavailable = true;
                ReleaseHighLayer();
                HandleError("high-ground layer", e);
            }
        }

        // Inside the map texture a half-transparent colour blends with the ground under it and
        // the shader shows the result. Here there is no ground to blend with: the layer is drawn
        // into an empty texture and composited by the UI afterwards, which expects straight
        // alpha, while the blend leaves it premultiplied - the shape would come out darker than
        // it is on the map. So everything is drawn solid: a path is its own colour over the rock
        // rather than mixed into it.
        private static Color Solid(Color c)
        {
            // a fifth of the way to its own brightness: the map's palette is never this saturated,
            // and a building here is seen against snow, which shows every bit of it
            const float mute = 0.2f;
            float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            return new Color(c.r + (l - c.r) * mute, c.g + (l - c.g) * mute, c.b + (l - c.b) * mute, 1f);
        }

        private bool EnsureHighTexture()
        {
            if (_rtHigh != null && _glMatHigh != null) return true;

            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) { _highUnavailable = true; return false; }

            if (_glMatHigh == null)
            {
                _glMatHigh = new Material(sh);
                _glMatHigh.hideFlags = HideFlags.HideAndDontSave;
                _glMatHigh.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glMatHigh.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glMatHigh.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _glMatHigh.SetInt("_ZWrite", 0);
                _glMatHigh.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }

            if (_rtHigh == null)
            {
                // The map layer's own grid, so a building here is exactly the size and shape it
                // is there, capped at 8192 (a gigabyte at MapTextureScale 8 is not worth it for
                // a layer of flat colour). Coarser was tried at 4096 and is wrong: this texture
                // is magnified, not minified - the minimap shows about a metre a screen pixel,
                // and the big map while the detailed one is still drawing - so every halving
                // shows up directly as a blurrier building.
                int size = Mathf.Min(_rt.width, 8192);
                RenderTexture rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
                rt.name = "LivingMap_HighGround";
                rt.useMipMap = false;
                rt.autoGenerateMips = false;
                // Point, not the map layer's smoothing: what is drawn here are shapes with
                // edges, and under magnification smoothing turns a building into a soft patch.
                rt.filterMode = FilterMode.Point;
                rt.anisoLevel = 0;
                rt.wrapMode = _rt.wrapMode;
                bool created = false;
                try { created = rt.Create(); }
                catch (Exception e) { Logger.LogWarning("Could not create the high-ground texture: " + e.Message); }
                if (!created)
                {
                    try { UnityEngine.Object.Destroy(rt); } catch { }
                    _highUnavailable = true;
                    Logger.LogWarning("The card refused the high-ground texture; buildings in the mountains are drawn only by the detailed map.");
                    return false;
                }
                _rtHigh = rt;
            }
            return true;
        }

        private void ReleaseHighLayer()
        {
            if (_rtHigh != null)
            {
                try { _rtHigh.Release(); UnityEngine.Object.Destroy(_rtHigh); } catch { }
                _rtHigh = null;
            }
            _highPieceCount = 0; _highCellCount = 0;
            _highSig = -1;
            HideHighImages();
        }

        private void HideHighImages()
        {
            if (_highSmall != null) _highSmall.enabled = false;
            if (_highLarge != null) _highLarge.enabled = false;
        }

        private void HighTeardown()
        {
            ReleaseHighLayer();
            if (_glMatHigh != null) { try { UnityEngine.Object.Destroy(_glMatHigh); } catch { } _glMatHigh = null; }
            DestroyHighImage(ref _highSmall);
            DestroyHighImage(ref _highLarge);
            _highPixels = null; _highMist = null;
            _highScratch.Clear();
        }

        private static void DestroyHighImage(ref RawImage img)
        {
            if (img == null) return;
            // the image sits inside a wrapper of ours (the mask), so the wrapper goes
            Transform t = img.transform.parent;
            try { UnityEngine.Object.Destroy(t != null ? t.gameObject : img.gameObject); } catch { }
            img = null;
        }

        // ------------------------------------------------------------------
        // on screen: a child of each map image, moved by the same uvRect the game sets
        // ------------------------------------------------------------------
        private void HighTick()
        {
            // the height bitset is read on the first tick that wants it, not at every world load
            if (HighWanted && _highPixels == null && _ready) { BuildHighPixels(); _highDirty = true; }
            if (!HighWanted || _rtHigh == null || _mm == null) { HideHighImages(); return; }
            _highLight = HighLight();

            bool wantLarge = HighMode == HighBoth && _mm.m_mapImageLarge != null &&
                             _mm.m_largeRoot != null && _mm.m_largeRoot.activeInHierarchy;
            // Close up the detailed map draws the same shapes properly, so this one steps aside -
            // but only once that map has really drawn the view, and not while its first level is
            // the map's own picture read back from the card. In those pixels the rock pattern is
            // already applied and our buildings already gone from under it, and nothing in the
            // port can tell which pixels were ours.
            bool blind = PortClassicFirst && _dShownLevel == 0;
            if (wantLarge && _dRoot != null && _dRoot.gameObject.activeSelf && _dViewCovered && !blind) wantLarge = false;
            bool wantSmall = _mm.m_mapImageSmall != null && _mm.m_smallRoot != null && _mm.m_smallRoot.activeInHierarchy;

            if (wantSmall) ShowHigh(ref _highSmall, _mm.m_mapImageSmall, true);
            else if (_highSmall != null) _highSmall.enabled = false;

            if (wantLarge) ShowHigh(ref _highLarge, _mm.m_mapImageLarge, false);
            else if (_highLarge != null) _highLarge.enabled = false;
        }

        // The shader lights everything it draws by the time of day: the same base is warm at
        // noon and nearly black at night, while this layer, drawn after the shader, would keep
        // its colours whatever the hour and read as a sticker. The full answer is the shader
        // itself - the paper under the colour, the terrain's own normal - and that is the
        // detailed map's job. What can be had here for nothing is the light: a RawImage
        // multiplies its texture by its colour on the card, free every frame, so the layer is
        // given what the shader's light comes to on flat ground.
        private Color HighLight()
        {
            Material m = _mm != null && _mm.m_mapImageSmall != null ? _mm.m_mapImageSmall.material : null;
            if (m == null) return new Color(1f, 1f, 1f, HighStrength);
            try
            {
                if (!m.HasProperty("_lightColor") || !m.HasProperty("_ambientLightColor")) return Color.white;
                Color lt = m.GetColor("_lightColor").linear, amb = m.GetColor("_ambientLightColor").linear;
                Color sun = Shader.GetGlobalColor("_SunColor").linear, ambG = Shader.GetGlobalColor("_AmbientColor").linear;
                Vector4 ld = s_portLightMode == 0 || s_portLightMode == 3 ? m.GetVector("_lightDir") : Shader.GetGlobalVector("_SunDir");
                if (s_portLightMode == 2 || s_portLightMode == 3) ld = -ld;
                Vector3 dir = new Vector3(ld.x, ld.y, ld.z);
                float len = dir.magnitude;
                float diff = len > 0.0001f ? Mathf.Max(0f, dir.y / len) : 1f;    // flat ground: the normal is up
                Color lit = new Color(
                    Mathf.Clamp01(diff * lt.r * sun.r + amb.r * ambG.r),
                    Mathf.Clamp01(diff * lt.g * sun.g + amb.g * ambG.g),
                    Mathf.Clamp01(diff * lt.b * sun.b + amb.b * ambG.b), 1f);
                Color g = lit.gamma;                                             // the UI multiplies in gamma space
                g.a = HighStrength;                                              // ... and blends the layer by its alpha
                return g;
            }
            catch { return new Color(1f, 1f, 1f, HighStrength); }
        }

        private void ShowHigh(ref RawImage img, RawImage map, bool round)
        {
            if (img == null || img.transform.parent == null || img.transform.parent.parent != map.transform)
            {
                DestroyHighImage(ref img);
                img = MakeHighImage(map, round);
                if (img == null) return;
            }
            img.texture = _rtHigh;
            img.uvRect = map.uvRect;       // the game moves the map by this; we follow it exactly
            img.color = _highLight;
            img.enabled = true;
            KeepBehindDetail(img.transform.parent as RectTransform);
        }

        // Under the detailed map, not over it. Both hang off the same map image, and a child
        // drawn later is drawn on top: with this layer on top, its flat buildings sat over the
        // finished ones until the whole view was drawn and then vanished at once - the far ones
        // being "replaced" as the map filled in. Underneath, a tile that is ready simply covers
        // it - the tiles are opaque - so the picture is only ever uncovered where there is
        // nothing better to show, and nothing is ever drawn twice over the same ground.
        private void KeepBehindDetail(RectTransform wrap)
        {
            if (wrap == null || _dRoot == null || _dRoot.parent != wrap.parent) return;
            int mine = wrap.GetSiblingIndex(), theirs = _dRoot.GetSiblingIndex();
            if (mine > theirs) wrap.SetSiblingIndex(theirs);
        }

        private RawImage MakeHighImage(RawImage map, bool round)
        {
            try
            {
                GameObject wrap = new GameObject("LivingMap high ground", typeof(RectTransform));
                RectTransform wrt = (RectTransform)wrap.transform;
                wrt.SetParent(map.transform, false);
                wrt.SetAsFirstSibling();                  // above the map image, below our other layers
                Stretch(wrt);

                // The minimap is round. If something above it already clips (a Mask or a
                // RectMask2D of the game's or another mod's), ours would only add a stencil
                // level, so it is left alone; otherwise a circle of our own is put in.
                if (round && !ClippedAbove(map.transform))
                {
                    Image circle = wrap.AddComponent<Image>();
                    circle.sprite = HighCircleSprite();
                    circle.raycastTarget = false;
                    Mask mask = wrap.AddComponent<Mask>();
                    mask.showMaskGraphic = false;
                }

                GameObject go = new GameObject("layer", typeof(RectTransform));
                RectTransform rt = (RectTransform)go.transform;
                rt.SetParent(wrt, false);
                Stretch(rt);
                RawImage img = go.AddComponent<RawImage>();
                img.raycastTarget = false;
                img.texture = _rtHigh;
                return img;
            }
            catch (Exception e)
            {
                HandleError("high-ground image", e);
                _highUnavailable = true;
                return null;
            }
        }

        private static bool ClippedAbove(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (p.GetComponent<Mask>() != null || p.GetComponent<RectMask2D>() != null) return true;
            }
            return false;
        }

        private Sprite HighCircleSprite()
        {
            if (_highCircle != null) return _highCircle;
            const int n = 256;
            Texture2D t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            t.name = "LivingMap circle";
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[n * n];
            float c = (n - 1) * 0.5f, r = c;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c, dy = y - c;
                    // a soft last pixel, so the rim is not a staircase
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false);
            _highCircle = Sprite.Create(t, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f));
            _highCircle.hideFlags = HideFlags.HideAndDontSave;
            return _highCircle;
        }

        private string HighStatus()
        {
            if (!HighWanted) return "hidden-ground overlay off";
            if (_highPixels == null) return "hidden-ground overlay: no height data";
            if (_rtHigh == null) return "hidden-ground overlay on, nothing on ground the map hides";
            return string.Format("hidden-ground overlay {0}: {1} pieces, {2} path cells at {3}px ({4:0} MB)",
                HighMode.ToLowerInvariant(), _highPieceCount, _highCellCount, _rtHigh.width,
                _rtHigh.width * (double)_rtHigh.height * 4 / (1024 * 1024));
        }
    }
}
