using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace LivingMap
{
    // The game's map shader (Custom/mapshader) ported to the detail tiles, so a zoomed-in tile
    // looks like the vanilla map: paper, forest stamps, water lines, mountains, lava, mist,
    // clouds and fog, lit by the same sun and ambient colours the environment sets for the time
    // of day. Read from the shader's DX11 bytecode; every pattern is sampled at the same
    // whole-map coordinate as in the shader, so it keeps its size and place when zooming in.
    // The shader snaps the map coordinate to a grid of _zoom * _pixelSize * 35 cells across the
    // map; the game sets _pixelSize = 200 / _zoom, so that is always 7000 cells, about 3.5 m -
    // the stylised pixel look. The port snaps the same way (DetailPixelMeters can change it).
    // Not ported: the animation of water and clouds (a tile is a snapshot) and the space
    // picture past the edge of the world.
    //
    // The shader's inputs: the vanilla map textures (biome colour, mist and lava masks, fog of
    // war) with Living Map's own data on top, at the tile's resolution - paths, fields and
    // buildings painted into the colour, heights from the world generator with the players'
    // terrain edits, and the forest mask from the trees that stand (or the vanilla forest with
    // the cleared/planted corrections where the zone's objects are not known).
    public partial class LivingMapPlugin
    {
        private ConfigEntry<string> _cfgDetailStyle;
        private ConfigEntry<float> _cfgDetailPixel;
        private ConfigEntry<bool> _cfgDetailClouds;
        private ConfigEntry<bool> _cfgDetailStyled;
        // coast line width: 0 from _zoom, 1 from _pixelSize (the shader multiplies one of them by 50)
        internal static int s_portCoastFrom = 0;

        private void BindPortConfig()
        {
            _cfgDetailStyle = Config.Bind(SecDetail, "DetailStyle", "Vanilla",
                new ConfigDescription("How the detailed picture is drawn. Vanilla: the game's own map look (paper, forest stamps, water lines, time-of-day light). Legacy: the earlier flat shaded picture.",
                    new AcceptableValueList<string>("Vanilla", "Legacy")));
            _cfgDetailPixel = Config.Bind(SecDetail, "DetailPixelMeters", 0f,
                new ConfigDescription("Vanilla style: size of the map's pixels in metres. 0 = automatic: the vanilla map's pixel (about 3.5 m) at first, halved at each closer level (1.75 m, 0.9 m); a number = that size at every level.",
                    new AcceptableValueRange<float>(0f, 8f)));
            _cfgDetailStyled = Config.Bind(SecDetail, "DetailStyledPieces", true,
                "Vanilla style: buildings and paths drawn with pixel textures (planks, masonry, cobbles, furrows), shaded edges, lighter high roofs and shadows. Off = flat colours.");
            _cfgDetailClouds = Config.Bind(SecDetail, "DetailClouds", true,
                "Vanilla style: the vanilla map's drifting clouds over the detailed picture (under the fog of war, as in the game). Off = no clouds there.");
        }

        private bool PortWanted { get { return _cfgDetailStyle != null && _cfgDetailStyle.Value == "Vanilla"; } }

        // ------------------------------------------------------------------
        // pattern textures, read once per map
        // ------------------------------------------------------------------
        private class PortTex
        {
            public int Levels;
            public int[] W, H;
            public float[][] R, G, B, A;     // linear colour per mip level

            // bilinear, repeating, from the level that fits 'texelsPerPixel'
            public void Sample(float u, float v, float lod, out float r, out float g, out float b, out float a)
            {
                int l = Mathf.Clamp((int)(lod + 0.5f), 0, Levels - 1);
                int w = W[l], h = H[l];
                float x = u * w - 0.5f, y = v * h - 0.5f;
                int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
                float tx = x - x0, ty = y - y0;
                x0 %= w; if (x0 < 0) x0 += w;
                y0 %= h; if (y0 < 0) y0 += h;
                int x1 = x0 + 1 == w ? 0 : x0 + 1, y1 = y0 + 1 == h ? 0 : y0 + 1;
                int i00 = y0 * w + x0, i10 = y0 * w + x1, i01 = y1 * w + x0, i11 = y1 * w + x1;
                float w00 = (1f - tx) * (1f - ty), w10 = tx * (1f - ty), w01 = (1f - tx) * ty, w11 = tx * ty;
                float[] cr = R[l], cg = G[l], cb = B[l], ca = A[l];
                r = cr[i00] * w00 + cr[i10] * w10 + cr[i01] * w01 + cr[i11] * w11;
                g = cg[i00] * w00 + cg[i10] * w10 + cg[i01] * w01 + cg[i11] * w11;
                b = cb[i00] * w00 + cb[i10] * w10 + cb[i01] * w01 + cb[i11] * w11;
                a = ca[i00] * w00 + ca[i10] * w10 + ca[i01] * w01 + ca[i11] * w11;
            }

            // mip level for a pattern tiled 'repeat' times over the world, drawn at 'mpp' m/px
            public float Lod(float repeat, float mpp, float world)
            {
                float texelsPerPixel = repeat * W[0] * mpp / world;
                return texelsPerPixel <= 1f ? 0f : Mathf.Log(texelsPerPixel, 2f);
            }
        }

        private class PortAssets
        {
            public PortTex Background, FogLayer, Water, Lava, Mountain, Cloud, Forest;
            public Texture2D CloudTex;           // the clouds as white with alpha, for the drifting layer
        }

        private PortAssets _port;
        private Material _portMat;
        private bool _portFailed;
        private static float[] s_srgbToLinear;

        private static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static byte RawByte(float c)
        {
            if (c <= 0f) return 0;
            if (c >= 1f) return 255;
            return (byte)(c * 255f + 0.5f);
        }

        private static byte LinearToSrgbByte(float c)
        {
            if (c <= 0f) return 0;
            if (c >= 1f) return 255;
            float s = c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
            return (byte)(s * 255f + 0.5f);
        }

        // Every texture goes through an sRGB render texture and a readback, so textures that are
        // not readable (the pattern assets) are read the same way; RGB is decoded back to linear.
        private static PortTex ReadPattern(Texture t)
        {
            return ReadPattern(t, s_portLinTextures);
        }

        private static PortTex ReadPattern(Texture t, bool decode)
        {
            if (t == null) return null;
            if (s_srgbToLinear == null)
            {
                s_srgbToLinear = new float[256];
                for (int i = 0; i < 256; i++) s_srgbToLinear[i] = SrgbToLinear(i / 255f);
            }
            int w = t.width, h = t.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Texture2D read = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                read.Apply(false);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            Color32[] px = read.GetPixels32();
            UnityEngine.Object.Destroy(read);

            List<float[]> lr = new List<float[]>(), lg = new List<float[]>(), lb = new List<float[]>(), la = new List<float[]>();
            List<int> lw = new List<int>(), lh = new List<int>();
            float[] r = new float[w * h], g = new float[w * h], b = new float[w * h], a = new float[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                if (decode) { r[i] = s_srgbToLinear[px[i].r]; g[i] = s_srgbToLinear[px[i].g]; b[i] = s_srgbToLinear[px[i].b]; }
                else { r[i] = px[i].r / 255f; g[i] = px[i].g / 255f; b[i] = px[i].b / 255f; }
                a[i] = px[i].a / 255f;
            }
            while (true)
            {
                lr.Add(r); lg.Add(g); lb.Add(b); la.Add(a); lw.Add(w); lh.Add(h);
                if (w <= 1 && h <= 1) break;
                int nw = Mathf.Max(1, w / 2), nh = Mathf.Max(1, h / 2);
                float[] nr = new float[nw * nh], ng = new float[nw * nh], nb = new float[nw * nh], na = new float[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Mathf.Min(2 * x, w - 1), x1 = Mathf.Min(2 * x + 1, w - 1);
                        int y0 = Mathf.Min(2 * y, h - 1), y1 = Mathf.Min(2 * y + 1, h - 1);
                        int o = y * nw + x;
                        nr[o] = (r[y0 * w + x0] + r[y0 * w + x1] + r[y1 * w + x0] + r[y1 * w + x1]) * 0.25f;
                        ng[o] = (g[y0 * w + x0] + g[y0 * w + x1] + g[y1 * w + x0] + g[y1 * w + x1]) * 0.25f;
                        nb[o] = (b[y0 * w + x0] + b[y0 * w + x1] + b[y1 * w + x0] + b[y1 * w + x1]) * 0.25f;
                        na[o] = (a[y0 * w + x0] + a[y0 * w + x1] + a[y1 * w + x0] + a[y1 * w + x1]) * 0.25f;
                    }
                r = nr; g = ng; b = nb; a = na; w = nw; h = nh;
            }
            PortTex p = new PortTex();
            p.Levels = lr.Count; p.R = lr.ToArray(); p.G = lg.ToArray(); p.B = lb.ToArray(); p.A = la.ToArray();
            p.W = lw.ToArray(); p.H = lh.ToArray();
            return p;
        }

        private static Texture2D CloudTexture(PortTex c)
        {
            int w = c.W[0], h = c.H[0];
            Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, true, true);
            Color32[] px = new Color32[w * h];
            float[] a = c.A[0];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a[i] * 255f), 0, 255));
            t.SetPixels32(px);
            t.Apply(true, true);
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Trilinear;
            t.name = "LivingMap clouds";
            return t;
        }

        // main thread; false when the map material is not the one this port was read from
        private static PortAssets LoadPortAssets(Material m, bool decode)
        {
            PortAssets p = new PortAssets();
            p.Background = ReadPattern(m.GetTexture("_BackgroundTex"), decode);
            p.FogLayer = ReadPattern(m.GetTexture("_FogLayerTex"), decode);
            p.Water = ReadPattern(m.GetTexture("_WaterTex"), decode);
            p.Lava = ReadPattern(m.GetTexture("_lavaTex"), decode);
            p.Mountain = ReadPattern(m.GetTexture("_MountainTex"), decode);
            p.Cloud = ReadPattern(m.GetTexture("_CloudTex"), decode);
            p.Forest = ReadPattern(m.GetTexture("_ForestTex"), decode);
            return p;
        }

        private bool EnsurePort()
        {
            if (_portFailed || _mm == null || _mm.m_mapImageLarge == null) return false;
            Material m = _mm.m_mapImageLarge.material;
            if (m == null || m.shader == null || m.shader.name != "Custom/mapshader")
            {
                if (!_portFailed) Logger.LogWarning("The map shader is not Custom/mapshader; the vanilla detail style is off.");
                _portFailed = true;
                return false;
            }
            if (_port != null && _portMat == m) return true;
            try
            {
                PortAssets p = new PortAssets();
                p.Background = ReadPattern(m.GetTexture("_BackgroundTex"));
                p.FogLayer = ReadPattern(m.GetTexture("_FogLayerTex"));
                p.Water = ReadPattern(m.GetTexture("_WaterTex"));
                p.Lava = ReadPattern(m.GetTexture("_lavaTex"));
                p.Mountain = ReadPattern(m.GetTexture("_MountainTex"));
                p.Cloud = ReadPattern(m.GetTexture("_CloudTex"));
                p.Forest = ReadPattern(m.GetTexture("_ForestTex"));
                if (p.Background == null || p.FogLayer == null || p.Water == null || p.Lava == null || p.Mountain == null || p.Cloud == null || p.Forest == null)
                    throw new Exception("a pattern texture of the map material is missing");
                p.CloudTex = CloudTexture(p.Cloud);
                _port = p; _portMat = m;
                return true;
            }
            catch (Exception e)
            {
                _portFailed = true;
                Logger.LogError("[detail vanilla style] " + e + "\nThe vanilla detail style is off for this session.");
                return false;
            }
        }

        // ------------------------------------------------------------------
        // per job snapshot
        // ------------------------------------------------------------------
        private class PortJob
        {
            public PortAssets A;
            public float World;                  // metres the whole map texture spans
            public int TexSize;
            // material and global values (colours linear)
            public Color Forest, Water, WaterDeep, WaterAsh, WaterAshDeep, SunFog, Light, Ambient, Sun, AmbientG, Lava1, Lava2;
            public Vector3 LightDir;
            public float NormalWidth, NormalIntensity, Zoom, SharedFade, CloudX, CloudZ, TimeX, TimeY;
            public float Cells;                  // snapping grid: cells across the whole map (0 = none)
            public bool CellPerPixel;            // the grid is the tile's own pixels: sample at the grid points
            public bool Clouds;                  // clouds drift in their own layer (not drawn into the tile)
            // Living Map's colours as it paints them into the map texture (gamma, with alpha)
            public Color32[] Mat; public Color32 MatUnknown, Outline, Paved, Dirt, Cultivated, Cleared;
            public bool DrawOutline;
            public bool Styled;                  // pixel textures for buildings and paths
            // world data around the tile, on the vanilla map grid (step 1)
            public int X0, Z0, W, H;
            public float[] Height, MainR, MainG, MainB, MaskX, MaskY, MaskZ, FogX, FogY;
        }

        private static readonly FieldInfo s_fiHeightTex = typeof(Minimap).GetField("m_heightTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo s_fiForestTex = typeof(Minimap).GetField("m_forestMaskTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo s_fiFogTex = typeof(Minimap).GetField("m_fogTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Material colours reach the shader converted to linear; the environment globals
        // (_SunColor, _AmbientColor, _SunFogColor) as they are - measured with 'livingmap port'.
        // measured against the real shader with 'livingmap port': about 2 of 255 apart
        internal static bool s_portLinMaterial = false, s_portLinGlobals = false;
        // textures decoded from sRGB, result encoded to sRGB (the linear pipeline) or both raw
        internal static bool s_portLinTextures = true, s_portSrgbOut = true;
        // light direction: 0 the material's _lightDir, 1 the environment's _SunDir, 2 its opposite,
        // 3 the opposite of _lightDir;
        // forest/mist/lava mask: the vanilla texture or the one the map material holds now
        internal static int s_portLightMode = 1;
        internal static bool s_portMaskFromMaterial = false;
        private static Color Lin(Color c) { return s_portLinMaterial ? c.linear : c; }
        private static Color LinG(Color c) { return s_portLinGlobals ? c.linear : c; }

        private PortJob SnapshotPort(float x0, float z0, float size)
        {
            if (!EnsurePort()) return null;
            Material m = _portMat;
            PortJob p = new PortJob();
            p.A = _port;
            p.TexSize = _texSize;
            p.World = _texSize * _pixelSize;
            p.Forest = Lin(m.GetColor("_ForestColor"));
            p.Water = Lin(m.GetColor("_WaterColor")); p.WaterDeep = Lin(m.GetColor("_WaterColorDeep"));
            p.WaterAsh = Lin(m.GetColor("_WaterColorAshlands")); p.WaterAshDeep = Lin(m.GetColor("_WaterColorAshlandsDeep"));
            p.SunFog = LinG(Shader.GetGlobalColor("_SunFogColor"));     // the material's _FogColor is not used
            p.Light = Lin(m.GetColor("_lightColor")); p.Ambient = Lin(m.GetColor("_ambientLightColor"));
            p.Lava1 = Lin(m.GetColor("_lavaColor1")); p.Lava2 = Lin(m.GetColor("_lavaColor2"));
            p.Sun = LinG(Shader.GetGlobalColor("_SunColor")); p.AmbientG = LinG(Shader.GetGlobalColor("_AmbientColor"));
            Vector4 ld = s_portLightMode == 0 || s_portLightMode == 3 ? m.GetVector("_lightDir") : Shader.GetGlobalVector("_SunDir");
            if (s_portLightMode == 2 || s_portLightMode == 3) ld = -ld;
            p.LightDir = new Vector3(ld.x, ld.y, ld.z).normalized;
            p.NormalWidth = m.GetFloat("_normalWidth"); p.NormalIntensity = m.GetFloat("_normalIntensity");
            float zoom = m.GetFloat("_zoom"), pixel = m.GetFloat("_pixelSize");
            p.Zoom = s_portCoastFrom == 0 ? zoom : pixel;
            p.SharedFade = m.GetFloat("_SharedFade");
            float cell = _cfgDetailPixel != null ? _cfgDetailPixel.Value : 0f;
            p.CellPerPixel = cell <= 0.01f;                 // automatic: one map pixel per tile pixel
            p.Cells = p.CellPerPixel ? p.World / (size / DTileSize) : p.World / cell;
            p.Clouds = _cfgDetailClouds == null || _cfgDetailClouds.Value;
            p.Mat = new Color32[_cfgMatColor.Length];
            for (int i = 0; i < _cfgMatColor.Length; i++) p.Mat[i] = _cfgMatColor[i].Value;
            p.MatUnknown = _cfgMatUnknownColor.Value; p.Outline = _cfgOutlineColor.Value; p.DrawOutline = _cfgOutline.Value;
            p.Paved = _cfgPavedColor.Value; p.Dirt = _cfgDirtColor.Value; p.Cultivated = _cfgCultivatedColor.Value; p.Cleared = _cfgClearedColor.Value;
            p.Styled = _cfgDetailStyled == null || _cfgDetailStyled.Value;
            // one fixed moment for every tile: the water, fog edge, mist and clouds move with
            // time in the shader, and tiles drawn at different moments would not meet
            p.CloudX = 0f; p.CloudZ = 0f;
            p.TimeX = 0f; p.TimeY = 0f;

            // the vanilla textures around the tile: the normal reaches _normalWidth back, the
            // fog wobble 0.0004 either way, plus the bilinear neighbour
            Texture2D main = _vanillaTex;
            Texture2D height = s_fiHeightTex != null ? s_fiHeightTex.GetValue(_mm) as Texture2D : null;
            Texture2D mask = s_fiForestTex != null ? s_fiForestTex.GetValue(_mm) as Texture2D : null;
            Texture2D fog = s_fiFogTex != null ? s_fiFogTex.GetValue(_mm) as Texture2D : null;
            if (main == null || height == null || mask == null || fog == null) return null;
            int margin = Mathf.CeilToInt((p.NormalWidth + 0.0005f) * _texSize) + 2;
            float half = _texSize * 0.5f;
            int px0 = Mathf.Clamp(Mathf.FloorToInt(x0 / _pixelSize + half) - margin, 0, _texSize - 1);
            int pz0 = Mathf.Clamp(Mathf.FloorToInt(z0 / _pixelSize + half) - margin, 0, _texSize - 1);
            int px1 = Mathf.Clamp(Mathf.CeilToInt((x0 + size) / _pixelSize + half) + margin, 0, _texSize - 1);
            int pz1 = Mathf.Clamp(Mathf.CeilToInt((z0 + size) / _pixelSize + half) + margin, 0, _texSize - 1);
            int w = px1 - px0 + 1, h = pz1 - pz0 + 1;
            p.X0 = px0; p.Z0 = pz0; p.W = w; p.H = h;
            Color[] hc = height.GetPixels(px0, pz0, w, h);
            Color[] mc = main.GetPixels(px0, pz0, w, h);
            Color[] kc = s_portMaskFromMaterial ? ReadRegion(m.GetTexture("_MaskTex"), px0, pz0, w, h) : null;
            if (kc == null) kc = mask.GetPixels(px0, pz0, w, h);
            Color[] fc = fog.GetPixels(px0, pz0, w, h);
            int n = w * h;
            p.Height = new float[n]; p.MainR = new float[n]; p.MainG = new float[n]; p.MainB = new float[n];
            p.MaskX = new float[n]; p.MaskY = new float[n]; p.MaskZ = new float[n]; p.FogX = new float[n]; p.FogY = new float[n];
            bool mainSrgb = main.isDataSRGB && s_portLinTextures;
            for (int i = 0; i < n; i++)
            {
                p.Height[i] = hc[i].r;
                Color c = mainSrgb ? mc[i].linear : mc[i];
                p.MainR[i] = c.r; p.MainG[i] = c.g; p.MainB[i] = c.b;
                p.MaskX[i] = kc[i].r; p.MaskY[i] = kc[i].g; p.MaskZ[i] = kc[i].b;
                p.FogX[i] = fc[i].r; p.FogY[i] = fc[i].g;
            }
            return p;
        }

        // a region of any texture (a render texture too), values as the shader samples them
        private static Color[] ReadRegion(Texture t, int x, int y, int w, int h)
        {
            if (t == null) return null;
            RenderTexture src = t as RenderTexture;
            RenderTexture tmp = null;
            if (src == null)
            {
                Texture2D t2 = t as Texture2D;
                if (t2 != null && t2.isReadable) return t2.GetPixels(x, y, w, h);
                tmp = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(t, tmp);
                src = tmp;
            }
            RenderTexture prev = RenderTexture.active;
            Texture2D read = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = src;
                read.ReadPixels(new Rect(x, y, w, h), 0, 0, false);
                read.Apply(false);
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = prev;
                if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
                UnityEngine.Object.Destroy(read);
            }
        }

        // bilinear, clamped, on the snapshot grid; (u, v) in whole-map coordinates
        private static float Data(PortJob p, float[] a, float u, float v)
        {
            float x = u * p.TexSize - 0.5f - p.X0, y = v * p.TexSize - 0.5f - p.Z0;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, p.W - 2), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, p.H - 2);
            float tx = Mathf.Clamp01(x - x0), ty = Mathf.Clamp01(y - y0);
            int i = y0 * p.W + x0;
            float a0 = a[i] + (a[i + 1] - a[i]) * tx;
            float a1 = a[i + p.W] + (a[i + p.W + 1] - a[i + p.W]) * tx;
            return a0 + (a1 - a0) * ty;
        }

        private static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        // ------------------------------------------------------------------
        // the shader, per pixel (background thread)
        // ------------------------------------------------------------------
        private static byte[] RenderPortTile(DJob j)
        {
            return RenderPortTile(j, null);
        }

        // Living Map's data at the tile's sample points: colour (sRGB bytes with paths and
        // buildings), height grid, tree mask. The sample point of pixel (ix, iz) is its grid point
        // with one map pixel per tile pixel, else the pixel centre.
        private class PortDetail
        {
            public byte[] Main;          // n x n RGBA, gamma
            public float[] MaskX;        // the forest mask on the vanilla grid with Living Map's cleared /
                                         // planted corrections - sampled bilinearly, like the main layer's
            public byte[] Ground;        // n x n RGBA: the colour before Living Map's paint
            public float[] H; public int Gs;   // heights every DStep pixels, one sample of border
            public float Half;           // 0 or 0.5: where a pixel samples, in pixels
            public byte[] Kind;          // n x n: 0 none, 1..4 terrain kind, 10 + material building
            public short[] Box;          // n x n: the box covering a building pixel, -1 none
        }

        private static PortDetail BuildPortDetail(DJob j, WorldGenerator wg)
        {
            PortJob p = j.Port;
            int n = DTileSize;
            float mpp = j.Mpp, world = p.World;
            PortDetail d = new PortDetail();
            d.Half = p.CellPerPixel ? 0f : 0.5f;

            // colour: the vanilla biome colour, then Living Map's paint
            d.Main = new byte[n * n * 4];
            for (int iz = 0; iz < n; iz++)
            {
                float v = (j.Z0 + (iz + d.Half) * mpp) / world + 0.5f;
                for (int ix = 0; ix < n; ix++)
                {
                    float u = (j.X0 + (ix + d.Half) * mpp) / world + 0.5f;
                    int o = (iz * n + ix) * 4;
                    d.Main[o] = LinearToSrgbByte(Data(p, p.MainR, u, v)); d.Main[o + 1] = LinearToSrgbByte(Data(p, p.MainG, u, v));
                    d.Main[o + 2] = LinearToSrgbByte(Data(p, p.MainB, u, v)); d.Main[o + 3] = 255;
                }
            }
            // the painters fill pixels whose centre is inside; shift so "centre" = sample point
            float x0 = j.X0, z0 = j.Z0;
            j.X0 -= (0.5f - d.Half) * mpp; j.Z0 -= (0.5f - d.Half) * mpp;
            try
            {
                // what covers each pixel: 0 nothing, 1..4 a terrain kind, 10 + material a building;
                // for buildings also which box (its direction and height) - for the styling pass
                d.Kind = new byte[n * n];
                d.Box = new short[n * n];
                for (int k = 0; k < d.Box.Length; k++) d.Box[k] = -1;
                d.Ground = (byte[])d.Main.Clone();
                // a pixel is painted when its centre is inside; anything thinner than a pixel is
                // widened to exactly one, so a wall or a path is "its width, at least a pixel" at
                // every level
                float grid = j.Grid;
                for (int i = 0; i < j.TerrKeys.Count; i++)
                {
                    long key = j.TerrKeys[i];
                    float cx0 = (int)(key >> 32) * grid, cz0 = (int)key * grid;
                    byte kind = j.TerrKinds[i];
                    Color32 c = kind == TerrainPaved ? p.Paved : kind == TerrainDirt ? p.Dirt : kind == TerrainCultivated ? p.Cultivated : p.Cleared;
                    PaintRect(d, n, j, cx0, cz0, cx0 + grid, cz0 + grid, c, kind);
                }
                // flat outlines only when not styled (styled buildings get a darker edge of their own)
                bool outline = p.DrawOutline && !p.Styled && mpp < 1.2f;
                float w = mpp;
                if (outline)
                {
                    for (int i = 0; i < j.Pieces.Count; i++)
                    {
                        PieceRec r = j.Pieces[i];
                        FillRect(d.Main, n, j, r.X0 - w, r.Z0 - w, r.X1 + w, r.Z1 + w, p.Outline);
                    }
                    for (int i = 0; i < j.Boxes.Count; i++) FillBox(d.Main, n, j, j.Boxes[i], w, p.Outline, 1f);
                }
                for (int i = 0; i < j.Pieces.Count; i++)
                {
                    PieceRec r = j.Pieces[i];
                    Color32 c = r.Mat != MatNone && r.Mat < p.Mat.Length ? p.Mat[r.Mat] : p.MatUnknown;
                    PaintRect(d, n, j, r.X0, r.Z0, r.X1, r.Z1, c, (byte)(10 + Mathf.Min((int)r.Mat, 200)));
                }
                for (int i = 0; i < j.Boxes.Count; i++)
                {
                    DBox bx = j.Boxes[i];
                    Color32 c = bx.Mat != MatNone && bx.Mat < p.Mat.Length ? p.Mat[bx.Mat] : p.MatUnknown;
                    PaintBox(d, n, j, bx, c, (byte)(10 + Mathf.Min((int)bx.Mat, 200)), (short)Mathf.Min(i, short.MaxValue));
                }

                SoftenTerrainEdges(d, n);
            }
            finally { j.X0 = x0; j.Z0 = z0; }

            // forest: the vanilla mask with the corrections, one value per map pixel; the shader
            // pattern is cut by it smoothly (a cleared cell is a soft patch, not a square)
            d.MaskX = (float[])p.MaskX.Clone();
            for (int gz = 0; gz < p.H; gz++)
                for (int gx = 0; gx < p.W; gx++)
                {
                    int fx = p.X0 + gx - j.FogX0, fz = p.Z0 + gz - j.FogZ0;
                    if (fx < 0 || fz < 0 || fx >= j.FogW || fz >= j.FogH) continue;
                    sbyte fix = j.ForestFix[fz * j.FogW + fx];
                    if (fix != 0) d.MaskX[gz * p.W + gx] = fix > 0 ? 1f : 0f;
                }

            // heights: the world generator plus the players' edits, every DStep pixels
            int gs = n / DStep + 3;
            d.Gs = gs;
            d.H = new float[gs * gs];
            Color mask;
            for (int sz = 0; sz < gs; sz++)
                for (int sx = 0; sx < gs; sx++)
                {
                    float wx = j.X0 + ((sx - 1) * DStep + d.Half) * mpp, wz = j.Z0 + ((sz - 1) * DStep + d.Half) * mpp;
                    Heightmap.Biome bm = wg.GetBiome(wx, wz, 0.02f, false);
                    d.H[sz * gs + sx] = wg.GetBiomeHeight(bm, wx, wz, out mask, false, true) + HeightDelta(j, wx, wz);
                }
            if (p.Styled && mpp < 3f) StylePaint(d, j, n);
            return d;
        }

        // The edge of a path, a field or paving blends half into the ground next to it; a lone
        // cell blends further. On every level, before the styling.
        private static void SoftenTerrainEdges(PortDetail d, int n)
        {
            byte[] kind = d.Kind, c = d.Main, gr = d.Ground;
            for (int iz = 0; iz < n; iz++)
                for (int ix = 0; ix < n; ix++)
                {
                    int k = iz * n + ix;
                    byte kd = kind[k];
                    if (kd == 0 || kd >= 10) continue;
                    int around = 0;
                    if (ix > 0 && kind[k - 1] > 0 && kind[k - 1] < 10) around++;
                    if (ix < n - 1 && kind[k + 1] > 0 && kind[k + 1] < 10) around++;
                    if (iz > 0 && kind[k - n] > 0 && kind[k - n] < 10) around++;
                    if (iz < n - 1 && kind[k + n] > 0 && kind[k + n] < 10) around++;
                    if (around == 4) continue;
                    float keep = around == 0 ? 0.35f : 0.55f;
                    int o = k * 4;
                    c[o] = (byte)(gr[o] + (c[o] - gr[o]) * keep);
                    c[o + 1] = (byte)(gr[o + 1] + (c[o + 1] - gr[o + 1]) * keep);
                    c[o + 2] = (byte)(gr[o + 2] + (c[o + 2] - gr[o + 2]) * keep);
                }
        }

        private static uint Hash(int x, int z, int salt)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        private static float Rand(int x, int z, int salt) { return (Hash(x, z, salt) & 0xFFFF) / 65535f; }

        private static void Scale(byte[] c, int o, float f)
        {
            c[o] = ToByte(c[o] * f); c[o + 1] = ToByte(c[o + 1] * f); c[o + 2] = ToByte(c[o + 2] * f);
        }

        // Pixel textures over the flat paint, on the level's own pixel grid (x, z = the pixel's
        // index across the world, so the pattern does not move between tiles): planks, masonry,
        // metal, marble and ice for buildings, with a darker edge, lighter high roofs and a
        // shadow; speckled dirt, cobbles, furrows and faint cleared ground for the terrain.
        private static void StylePaint(PortDetail d, DJob j, int n)
        {
            float mpp = j.Mpp;
            bool fine = true;
            // strength: a third at 2.5 m, three quarters at 1.75 m, full from 1.25 m - the
            // textures grow in as you zoom, instead of appearing at once
            float amp = Mathf.Clamp01((3.2f - mpp) / 2f);
            int gx0 = Mathf.RoundToInt(j.X0 / mpp), gz0 = Mathf.RoundToInt(j.Z0 / mpp);
            byte[] c = d.Main;
            byte[] kind = d.Kind;
            byte[] orig = (byte[])kind.Clone();     // shadows and edges read the kinds before any change
            for (int iz = 0; iz < n; iz++)
                for (int ix = 0; ix < n; ix++)
                {
                    int k = iz * n + ix, o = k * 4;
                    byte kd = orig[k];
                    int gx = gx0 + ix, gz = gz0 + iz;
                    if (kd == 0)
                    {
                        // a building's shadow: the pixel north-west of this one is a building
                        if (ix > 0 && iz < n - 1 && orig[(iz + 1) * n + ix - 1] >= 10) Scale(c, o, 1f - 0.28f * amp);
                        continue;
                    }
                    if (kd < 10)
                    {
                        // terrain: a pattern of its own (the edges were softened already)
                        float f = 1f;
                        if (kd == TerrainDirt) f = 0.88f + 0.24f * Rand(gx, gz, 1);
                        else if (kd == TerrainPaved)
                        {
                            // cobbles of 2 x 2 pixels, rows offset, dark joints
                            int row = gz >> 1, col = (gx + (row & 1)) >> 1;
                            f = 0.86f + 0.24f * Rand(col, row, 2);
                            if (fine && ((gz & 1) == 0 || ((gx + (row & 1)) & 1) == 0) && Rand(gx, gz, 3) < 0.35f) f *= 0.82f;
                        }
                        else if (kd == TerrainCultivated) f = ((gz & 1) == 0 ? 0.84f : 1.08f) * (0.95f + 0.1f * Rand(gx, gz, 4));
                        else f = 0.95f + 0.1f * Rand(gx, gz, 5);
                        Scale(c, o, 1f + (f - 1f) * amp);
                        continue;
                    }

                    // buildings
                    int mat = kd - 10;
                    int bi = d.Box[k];
                    float lx = gx, lz = gz;                 // pattern axes: the box's own if known
                    float top = 1f;
                    if (bi >= 0 && bi < j.Boxes.Count)
                    {
                        DBox b = j.Boxes[bi];
                        float wx = (gx + 0.5f) * mpp, wz = (gz + 0.5f) * mpp;
                        float dx = wx - b.X, dz = wz - b.Z;
                        float ax = dx * b.Cos - dz * b.Sin, az = dx * b.Sin + dz * b.Cos;
                        bool alongX = b.HX >= b.HZ;
                        lx = (alongX ? ax : az) / mpp; lz = (alongX ? az : ax) / mpp;
                        float ground = Sample(d.H, d.Gs, (b.X - j.X0) / mpp / DStep + 1f, (b.Z - j.Z0) / mpp / DStep + 1f);
                        top = Mathf.Clamp(0.88f + 0.025f * (b.Top - ground), 0.88f, 1.16f);   // high roofs lighter
                    }
                    int px = Mathf.FloorToInt(lx), pz = Mathf.FloorToInt(lz);
                    float m = 1f;
                    if (fine)
                    {
                        if (mat == 0 || mat == 3 || mat == 8)          // wood: planks along the piece, knots
                        {
                            m = (pz & 1) == 0 ? 0.93f : 1.05f;
                            if (((pz + 1) % 3) == 0) m *= 0.9f;
                            if (Rand(px, pz, 11) > 0.94f) m *= 0.8f;
                        }
                        else if (mat == 1 || mat == 5 || mat == 6)     // masonry: bricks 2-3 px, mortar joints
                        {
                            int row = pz, len = 2 + (int)(Rand(0, row, 12) * 1.99f);
                            int col = (px + row * 7) / len;
                            m = 0.9f + 0.18f * Rand(col, row, 13);
                            if (((px + row * 7) % len) == 0) m *= 0.84f;
                        }
                        else if (mat == 2)                             // iron: dark plates, light rivets
                            m = (((px % 3) + 3) % 3 == 0 && ((pz % 3) + 3) % 3 == 0) ? 1.35f : 0.92f + 0.08f * Rand(px, pz, 14);
                        else if (mat == 4)                             // marble: light, sparse veins
                            m = Rand(px + pz, pz, 15) > 0.9f ? 0.86f : 1f + 0.04f * Rand(px, pz, 16);
                        else                                           // ice and the rest: a soft shimmer
                            m = 0.94f + 0.12f * Rand(px, pz, 17);
                    }
                    else m = 0.95f + 0.1f * Rand(gx, gz, 18);
                    // the edge of a building: a darker shade of its own colour
                    bool edge = (ix > 0 && orig[k - 1] < 10) || (ix < n - 1 && orig[k + 1] < 10) || (iz > 0 && orig[k - n] < 10) || (iz < n - 1 && orig[k + n] < 10);
                    float s = m * top * (edge ? 0.72f : 1f);
                    Scale(c, o, 1f + (s - 1f) * amp);
                }
        }

        // Paint + record: a pixel whose centre is inside; a side thinner than a pixel is widened
        // to one pixel around its middle.
        private static void PaintRect(PortDetail d, int n, DJob j, float x0, float z0, float x1, float z1, Color32 c, byte kind)
        {
            float mpp = j.Mpp;
            if (x1 - x0 < mpp) { float m = (x0 + x1) * 0.5f; x0 = m - mpp * 0.5f; x1 = m + mpp * 0.5f; }
            if (z1 - z0 < mpp) { float m = (z0 + z1) * 0.5f; z0 = m - mpp * 0.5f; z1 = m + mpp * 0.5f; }
            int ix0 = Mathf.Max(0, Mathf.CeilToInt((x0 - j.X0) / mpp - 0.5f)), ix1 = Mathf.Min(n - 1, Mathf.CeilToInt((x1 - j.X0) / mpp - 0.5f) - 1);
            int iz0 = Mathf.Max(0, Mathf.CeilToInt((z0 - j.Z0) / mpp - 0.5f)), iz1 = Mathf.Min(n - 1, Mathf.CeilToInt((z1 - j.Z0) / mpp - 0.5f) - 1);
            float a = c.a / 255f;
            for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    int k = iz * n + ix;
                    Blend(d.Main, k * 4, c.r, c.g, c.b, a);
                    d.Kind[k] = kind; d.Box[k] = -1;
                }
        }

        private static void PaintBox(PortDetail d, int n, DJob j, DBox b, Color32 c, byte kind, short box)
        {
            float mpp = j.Mpp;
            float hx = Mathf.Max(b.HX, mpp * 0.5f), hz = Mathf.Max(b.HZ, mpp * 0.5f);
            float ext = Mathf.Sqrt(hx * hx + hz * hz);
            int ix0 = Mathf.Max(0, Mathf.FloorToInt((b.X - ext - j.X0) / mpp)), ix1 = Mathf.Min(n - 1, Mathf.CeilToInt((b.X + ext - j.X0) / mpp));
            int iz0 = Mathf.Max(0, Mathf.FloorToInt((b.Z - ext - j.Z0) / mpp)), iz1 = Mathf.Min(n - 1, Mathf.CeilToInt((b.Z + ext - j.Z0) / mpp));
            float a = c.a / 255f;
            for (int iz = iz0; iz <= iz1; iz++)
                for (int ix = ix0; ix <= ix1; ix++)
                {
                    float dx = j.X0 + (ix + 0.5f) * mpp - b.X, dz = j.Z0 + (iz + 0.5f) * mpp - b.Z;
                    float lx = dx * b.Cos - dz * b.Sin, lz = dx * b.Sin + dz * b.Cos;
                    if (Mathf.Abs(lx) > hx || Mathf.Abs(lz) > hz) continue;
                    int k = iz * n + ix;
                    Blend(d.Main, k * 4, c.r, c.g, c.b, a);
                    d.Kind[k] = kind; d.Box[k] = box;
                }
        }

        private static byte[] RenderPortTile(DJob j, WorldGenerator wg)
        {
            PortJob p = j.Port;
            PortDetail det = wg != null && j.Trees != null && j.TerrKeys != null ? BuildPortDetail(j, wg) : null;
            PortAssets A = p.A;
            int n = DTileSize;
            byte[] px = new byte[n * n * 4];
            byte[] fogPx = new byte[n * n * 4];             // fog of war (and space) over the clouds
            j.Pixels2 = fogPx;
            float mpp = j.Mpp, world = p.World;
            float lodBg5 = A.Background.Lod(5f, mpp, world), lodBg40 = A.Background.Lod(40f, mpp, world);
            float lodFogL = A.FogLayer.Lod(5f, mpp, world), lodWater = A.Water.Lod(80f, mpp, world);
            float lodLava40 = A.Lava.Lod(40f, mpp, world), lodLava80 = A.Lava.Lod(80f, mpp, world), lodLava60 = A.Lava.Lod(60f, mpp, world);
            float lodMount = A.Mountain.Lod(70f, mpp, world), lodForest = A.Forest.Lod(150f, mpp, world);
            float lodMist15 = A.Cloud.Lod(15f, mpp, world), lodMist20 = A.Cloud.Lod(20f, mpp, world), lodCloud = A.Cloud.Lod(7f, mpp, world);
            float tx = p.TimeX, ty = p.TimeY;
            float s5x = tx * 20f, s5y = tx * 19.08246f, s5z = tx * 16.86f, s5w = tx * 18.882462f;
            float rotA = ty * 0.0005f, rotB = ty * -0.00087f;
            float rsA = Mathf.Sin(rotA), rcA = Mathf.Cos(rotA), rsB = Mathf.Sin(rotB), rcB = Mathf.Cos(rotB);
            float coastWidth = Mathf.Clamp(p.Zoom * 50f, 2f, 10f);
            Color lt = p.Light, amb = p.Ambient, sun = p.Sun, ambG = p.AmbientG;
            // fog and mist are lit by the environment's sun + ambient, not the material's light
            float laR = sun.r + ambG.r, laG = sun.g + ambG.g, laB = sun.b + ambG.b;
            float nw = p.NormalWidth;
            float cells = p.Cells;
            float r, g, b, a;

            for (int iz = 0; iz < n; iz++)
            {
                float wz = j.Z0 + (iz + (p.CellPerPixel ? 0f : 0.5f)) * mpp;
                float v = wz / world + 0.5f;
                if (cells > 0f) v = Mathf.Floor(v * cells + 0.5f) / cells;
                for (int ix = 0; ix < n; ix++)
                {
                    float wx = j.X0 + (ix + (p.CellPerPixel ? 0f : 0.5f)) * mpp;
                    float u = wx / world + 0.5f;
                    if (cells > 0f) u = Mathf.Floor(u * cells + 0.5f) / cells;

                    float flR, flG, flB, flA;
                    A.FogLayer.Sample(u * 5f, v * 5f, lodFogL, out flR, out flG, out flB, out flA);
                    float mX = Data(p, p.MaskX, u, v), mY = Data(p, p.MaskY, u, v), mZ = Data(p, p.MaskZ, u, v);
                    float hgt = Data(p, p.Height, u, v);
                    int di = -1;
                    if (det != null)
                    {
                        // the snapped point, back to world metres and to this tile's pixel
                        float sxw = (u - 0.5f) * world, szw = (v - 0.5f) * world;
                        float fx = (sxw - j.X0) / mpp - det.Half, fz = (szw - j.Z0) / mpp - det.Half;
                        int pxi = Mathf.Clamp(Mathf.RoundToInt(fx), 0, n - 1), pzi = Mathf.Clamp(Mathf.RoundToInt(fz), 0, n - 1);
                        di = pzi * n + pxi;
                        hgt = Sample(det.H, det.Gs, fx / DStep + 1f, fz / DStep + 1f);
                        // forest: as the main layer draws it (vanilla mask + corrections, smooth)
                        mX = Data(p, det.MaskX, u, v);
                    }

                    // fog, sampled with a wobble
                    float f1 = Data(p, p.FogX, u + Mathf.Sin(v * 1700f + s5x) * 0.0004f, v + Mathf.Cos(u * 1200f + s5y) * 0.0004f);
                    float qu = u + Mathf.Cos(v * 1846f - s5z) * 0.0004f, qv = v + Mathf.Sin(u * 1246.8f - s5w) * 0.0004f;
                    float f2x = Data(p, p.FogX, qu, qv), f2y = Data(p, p.FogY, qu, qv);

                    // normal from the height texture
                    bool land = hgt >= 29.5f;
                    float nx = 0f, ny = 1f, nz = 0f;
                    if (land)
                    {
                        float dx, dz;
                        if (det != null)
                        {
                            // the terrain keeps the vanilla 25 m baseline; the players' edits are
                            // measured across one pixel and scaled to it, or a raised ring would
                            // light a ghost of itself 25 m away
                            float sxw = (u - 0.5f) * world, szw = (v - 0.5f) * world, nwm = nw * world;
                            float e0 = HeightDelta(j, sxw, szw), step = Mathf.Max(mpp, 0.5f), k = nwm / step;
                            dx = Data(p, p.Height, u - nw, v) - Data(p, p.Height, u, v) + Mathf.Clamp((HeightDelta(j, sxw - step, szw) - e0) * k, -40f, 40f);
                            dz = Data(p, p.Height, u, v - nw) - Data(p, p.Height, u, v) + Mathf.Clamp((HeightDelta(j, sxw, szw - step) - e0) * k, -40f, 40f);
                        }
                        else { dx = Data(p, p.Height, u - nw, v) - hgt; dz = Data(p, p.Height, u, v - nw) - hgt; }
                        float len = Mathf.Sqrt(dx * dx + p.NormalIntensity * p.NormalIntensity + dz * dz);
                        nx = dx / len; ny = p.NormalIntensity / len; nz = dz / len;
                    }
                    float diff = Mathf.Max(0f, nx * p.LightDir.x + ny * p.LightDir.y + nz * p.LightDir.z);
                    float litR = diff * lt.r * sun.r + amb.r * ambG.r;
                    float litG = diff * lt.g * sun.g + amb.g * ambG.g;
                    float litB = diff * lt.b * sun.b + amb.b * ambG.b;

                    float cR, cG, cB, cA;
                    if (!land)
                    {
                        float bgR, bgG, bgB, bgA;
                        A.Background.Sample(u * 5f, v * 5f, lodBg5, out bgR, out bgG, out bgB, out bgA);
                        float dt = Mathf.Clamp01((hgt - 9.5f) * 0.05f);
                        float ash = Mathf.Clamp01((hgt - 29f) * -0.071429f);
                        float asR = p.WaterAshDeep.r + (p.WaterAsh.r - p.WaterAshDeep.r) * dt, asG = p.WaterAshDeep.g + (p.WaterAsh.g - p.WaterAshDeep.g) * dt;
                        float asB = p.WaterAshDeep.b + (p.WaterAsh.b - p.WaterAshDeep.b) * dt, asA = p.WaterAshDeep.a + (p.WaterAsh.a - p.WaterAshDeep.a) * dt;
                        float wR = p.WaterDeep.r + (p.Water.r - p.WaterDeep.r) * dt, wG = p.WaterDeep.g + (p.Water.g - p.WaterDeep.g) * dt;
                        float wB = p.WaterDeep.b + (p.Water.b - p.WaterDeep.b) * dt, wA = p.WaterDeep.a + (p.Water.a - p.WaterDeep.a) * dt;
                        float k = Smooth01(mZ * 20f);
                        wR += (asR - wR) * k; wG += (asG - wG) * k; wB += (asB - wB) * k; wA += (asA - wA) * k;
                        float pR = flR + (bgR - flR) * 0.5f, pG = flG + (bgG - flG) * 0.5f, pB = flB + (bgB - flB) * 0.5f, pA = flA + (bgA - flA) * 0.5f;
                        float baseR = pR * wR, baseG = pG * wG, baseB = pB * wB, baseA = pA * wA;
                        float wu = u * 80f + tx * 0.1f, wv = v * 80f + Mathf.Sin(u * v * 4000f + s5x) * 0.01f;
                        float lR, lG, lB, lA;
                        A.Water.Sample(wu, wv, lodWater, out lR, out lG, out lB, out lA);
                        float wf = (1f - ash) * lA;
                        cR = baseR + (lR - baseR) * wf; cG = baseG + (lG - baseG) * wf; cB = baseB + (lB - baseB) * wf; cA = baseA + (lA - baseA) * wf;
                    }
                    else
                    {
                        float bgR, bgG, bgB, bgA;
                        A.Background.Sample(u * 40f, v * 40f, lodBg40, out bgR, out bgG, out bgB, out bgA);
                        float mR, mG, mB;
                        if (det != null)
                        {
                            int o4 = di * 4;
                            if (s_portLinTextures) { mR = s_srgbToLinear[det.Main[o4]]; mG = s_srgbToLinear[det.Main[o4 + 1]]; mB = s_srgbToLinear[det.Main[o4 + 2]]; }
                            else { mR = det.Main[o4] / 255f; mG = det.Main[o4 + 1] / 255f; mB = det.Main[o4 + 2] / 255f; }
                        }
                        else { mR = Data(p, p.MainR, u, v); mG = Data(p, p.MainG, u, v); mB = Data(p, p.MainB, u, v); }
                        float mx = Mathf.Max(bgR, Mathf.Max(bgG, bgB)), mn = Mathf.Min(bgR, Mathf.Min(bgG, bgB));
                        if (mx - mn >= 0.0001f) { bgR = mx; bgG = mx; bgB = mx; }
                        float gR = mR * bgR, gG = mG * bgG, gB = mB * bgB, gA = bgA;   // main alpha is 1
                        cR = gR * 1.5f; cG = gG * 1.5f; cB = gB * 1.5f; cA = gA * 1.5f;

                        float hl = Mathf.Clamp01((hgt - 30.5f) * 0.2f);
                        float lv0, d1, d2, d3;
                        A.Lava.Sample(u * 40f, v * 40f, lodLava40, out lv0, out d1, out d2, out d3);
                        float pu = u - 0.5f, pv = v - 0.5f;
                        float ra, rb;
                        A.Lava.Sample((rcA * pu + rsA * pv + 0.5f) * 80f, (rcA * pv - rsA * pu + 0.5f) * 80f, lodLava80, out ra, out d1, out d2, out d3);
                        A.Lava.Sample((rcB * pu + rsB * pv + 0.5f) * 60f, (rcB * pv - rsB * pu + 0.5f) * 60f, lodLava60, out rb, out d1, out d2, out d3);
                        float o1 = ra <= 0.5f ? 2f * ra * rb : 1f - 2f * (1f - ra) * (1f - rb);
                        float o2 = lv0 <= 0.5f ? 2f * o1 * lv0 : 1f - 2f * (1f - lv0) * (1f - o1);
                        o2 = o2 > 0f ? Mathf.Pow(o2, 2.5f) : 0f;
                        float lcR = p.Lava1.r + (p.Lava2.r - p.Lava1.r) * o2, lcG = p.Lava1.g + (p.Lava2.g - p.Lava1.g) * o2, lcB = p.Lava1.b + (p.Lava2.b - p.Lava1.b) * o2;
                        float lm = hl * mZ;
                        float fk = lm <= 0.5f ? 2f * lv0 * lm : 1f - 2f * (1f - lv0) * (1f - mZ * hl);
                        cR += (lcR - cR) * fk; cG += (lcG - cG) * fk; cB += (lcB - cB) * fk;
                    }

                    // mountains above 70 m, snow-grey on flat high ground, then the light
                    float mtR, mtG, mtB, mtA;
                    A.Mountain.Sample(u * 70f, v * 70f, lodMount, out mtR, out mtG, out mtB, out mtA);
                    float mk = Mathf.Clamp01((hgt - 70f) * 0.04f) * mtA;
                    r = cR + (mtR - cR) * mk; g = cG + (mtG - cG) * mk; b = cB + (mtB - cB) * mk; a = cA + (mtA - cA) * mk;
                    float high = Mathf.Clamp01((hgt - 80f) * 0.05f);
                    float flat = Mathf.Clamp01((ny - 0.14f) * 6.25f);
                    float lum = Mathf.Clamp01((r + g + b) * 1.5f);
                    float sk = Mathf.Max(high * flat - lum, 0f);
                    r += (0.5f - r) * sk; g += (0.5f - g) * sk; b += (0.5f - b) * sk;
                    r *= litR; g *= litG; b *= litB;

                    // the dark line along the coast
                    float ck = 1f - Mathf.Min(Mathf.Abs(hgt - 29.5f) / coastWidth, 1f);
                    r += (0.02f - r) * ck; g += (0.01f - g) * ck; b += (0.01f - b) * ck; a += (1f - a) * ck;

                    // mist (Mistlands)
                    if (mY > 0f)
                    {
                        float m1u = u * 15f + Mathf.Sin(v * 850f + tx * 5f) * 0.01f, m1v = v * 15f + Mathf.Cos(u * 600f + tx * 4.770615f) * 0.01f;
                        float m2u = u * 20f + Mathf.Sin(v * 750f + tx * 5f) * 0.01f, m2v = v * 20f + Mathf.Cos(u * 300f + tx * 3.270615f) * 0.01f;
                        float e1, e2, e3, c1, c2;
                        A.Cloud.Sample(m1u, m1v, lodMist15, out e1, out e2, out e3, out c1);
                        A.Cloud.Sample(m2u, m2v, lodMist20, out e1, out e2, out e3, out c2);
                        float k1 = mY;
                        r += (laR * 0.7f * c1 - r) * k1; g += (laG * 0.5f * c1 - g) * k1; b += (laB * c1 - b) * k1; a += (c1 * c1 - a) * k1;
                        float k2 = mY * c2;
                        r += (laR * 1.2f - r) * k2; g += (laG * 0.7f - g) * k2; b += (laB * 0.7f - b) * k2; a += (c2 - a) * k2;
                    }

                    // forest stamps, lit at 80 %
                    if (mX > 0f)
                    {
                        float fR, fG, fB, fA;
                        A.Forest.Sample(u * 150f, v * 150f, lodForest, out fR, out fG, out fB, out fA);
                        fR *= p.Forest.r; fG *= p.Forest.g; fB *= p.Forest.b; fA *= p.Forest.a;
                        fR += (fR * litR - fR) * 0.8f; fG += (fG * litG - fG) * 0.8f; fB += (fB * litB - fB) * 0.8f;
                        float fk = mX * fA;
                        r += (fR - r) * fk; g += (fG - g) * fk; b += (fB - b) * fk; a += (fA - a) * fk;
                    }

                    // clouds (drawn here only when they do not drift in their own layer)
                    if (!p.Clouds)
                    {
                        float e1, e2, e3, cl;
                        A.Cloud.Sample(u * 7f - p.CloudX, v * 7f - p.CloudZ, lodCloud, out e1, out e2, out e3, out cl);
                        r += (sun.r * lt.r - r) * cl; g += (sun.g * lt.g - g) * cl; b += (sun.b * lt.b - b) * cl; a += (1f - a) * cl;
                    }

                    // fog of war
                    float s1 = Smooth01(2f * f1);
                    float sy = Smooth01(2f * f2x), sz = Smooth01(2f * f2y);
                    float mix = 0.5f * Mathf.Min(sy, sz) + 0.5f * s1;
                    float fogAmt = s1 + p.SharedFade * (mix - s1);
                    float fa = Mathf.Clamp01(fogAmt);
                    float qR = 0f, qG = 0f, qB = 0f;
                    float du = u - 0.5f, dv = v - 0.5f;
                    float dist = Mathf.Sqrt(du * du + dv * dv);
                    if (fa > 0f)
                    {
                        qR = flR * fogAmt * laR * p.SunFog.r; qG = flG * fogAmt * laG * p.SunFog.g; qB = flB * fogAmt * laB * p.SunFog.b;
                        float e = Smooth01(Mathf.Min(dist * 2.325581f, 1f));
                        qR -= 0.8f * e * qR; qG -= 0.8f * e * qG; qB -= 0.8f * e * qB;
                    }
                    // past the edge of the world the shader shows space; a dark fill stands in
                    float sp = Smooth01((dist - 0.42f) * 99.9998f);

                    int o = (iz * n + ix) * 4;
                    if (s_portSrgbOut) { px[o] = LinearToSrgbByte(r); px[o + 1] = LinearToSrgbByte(g); px[o + 2] = LinearToSrgbByte(b); }
                    else { px[o] = RawByte(r); px[o + 1] = RawByte(g); px[o + 2] = RawByte(b); }
                    px[o + 3] = 255;

                    // the fog layer: lerp(lerp(x, fog, fa), space, sp) as one colour and alpha
                    float al = 1f - (1f - fa) * (1f - sp);
                    if (al > 0f)
                    {
                        float kf = fa * (1f - sp) / al, ks = sp / al;
                        float cr = qR * kf + 0.01f * ks, cg = qG * kf + 0.01f * ks, cb = qB * kf + 0.015f * ks;
                        if (s_portSrgbOut) { fogPx[o] = LinearToSrgbByte(cr); fogPx[o + 1] = LinearToSrgbByte(cg); fogPx[o + 2] = LinearToSrgbByte(cb); }
                        else { fogPx[o] = RawByte(cr); fogPx[o + 1] = RawByte(cg); fogPx[o + 2] = RawByte(cb); }
                        fogPx[o + 3] = (byte)Mathf.Clamp(Mathf.RoundToInt(al * 255f), 0, 255);
                    }
                }
            }
            return px;
        }

        // what the time of day changes in a tile: the sun and ambient colours, the sun fog, the
        // sun's direction (about 4 degrees) and the fade of ground known from others; a change
        // redraws the visible tiles (see RelightTiles)
        private long PortLightSig()
        {
            Color s = Shader.GetGlobalColor("_SunColor"), a = Shader.GetGlobalColor("_AmbientColor"), f = Shader.GetGlobalColor("_SunFogColor");
            Vector4 d = Shader.GetGlobalVector("_SunDir");
            long q = 17;
            q = q * 31 + Q(s.r, 10f); q = q * 31 + Q(s.g, 10f); q = q * 31 + Q(s.b, 10f);
            q = q * 31 + Q(a.r, 20f); q = q * 31 + Q(a.g, 20f); q = q * 31 + Q(a.b, 20f);
            q = q * 31 + Q(f.r, 20f); q = q * 31 + Q(f.g, 20f); q = q * 31 + Q(f.b, 20f);
            q = q * 31 + Q(d.x, 14f); q = q * 31 + Q(d.y, 14f); q = q * 31 + Q(d.z, 14f);
            if (_portMat != null) q = q * 31 + Q(_portMat.GetFloat("_SharedFade"), 16f);
            return q;
        }

        private static long Q(float v, float steps) { return Mathf.RoundToInt(Mathf.Clamp(v, -4f, 4f) * steps); }
    }
}
