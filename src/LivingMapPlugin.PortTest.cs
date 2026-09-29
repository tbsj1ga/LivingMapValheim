using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LivingMap
{
    // Dev tool for the shader port: 'livingmap port test' renders the tile window at the map's
    // centre twice - through the game's real map shader (a blit with _MainTex_ST pointing at the
    // window) and through the port - and saves both as PNG next to the config. Then it sets each
    // colour of the material and each environment global to magenta in turn, renders again and
    // logs how much of the window changed, which tells which constant does what.
    public partial class LivingMapPlugin
    {
        private static readonly string[] PortTestGlobals = { "_SunColor", "_AmbientColor", "_SunFogColor" };

        private string PortTest()
        {
            if (!_ready || _mm == null || _mm.m_mapImageLarge == null) return "livingmap port: not attached to a map right now";
            if (!EnsurePort()) return "livingmap port: the vanilla style is not available (see the log)";
            Rect uvr = _mm.m_mapImageLarge.uvRect;
            float world = _texSize * _pixelSize;
            float cx = (uvr.center.x - 0.5f) * world, cz = (uvr.center.y - 0.5f) * world;
            float mpp = 4f, size = DTileSize * mpp;
            float x0 = Mathf.Floor(cx / size) * size, z0 = Mathf.Floor(cz / size) * size;
            string dir = Path.Combine(BepInEx.Paths.ConfigPath, "LivingMap");
            Directory.CreateDirectory(dir);
            StringBuilder log = new StringBuilder("==== LivingMap port test at x " + x0 + ".." + (x0 + size) + ", z " + z0 + ".." + (z0 + size) + " (" + mpp + " m/px) ====\n");
            foreach (string g in PortTestGlobals)
                log.AppendLine("global " + g + " colour " + Shader.GetGlobalColor(g) + " vector " + Shader.GetGlobalVector(g).ToString("F4"));
            log.AppendLine("global _CloudOffset " + Shader.GetGlobalVector("_CloudOffset").ToString("F4") + ", time " + Time.timeSinceLevelLoad.ToString("F2"));

            Material src = _portMat;
            Material mat = new Material(src);
            try
            {
                float w = size / world, u0 = x0 / world + 0.5f, v0 = z0 / world + 0.5f;
                mat.SetTextureScale("_MainTex", new Vector2(w, w));
                mat.SetTextureOffset("_MainTex", new Vector2(u0, v0));
                mat.SetFloat("_zoom", 1000f); mat.SetFloat("_pixelSize", 1000f);        // no coordinate snapping
                mat.SetVector("_mapCenter", src.GetVector("_mapCenter"));
                mat.SetFloat("_SharedFade", src.GetFloat("_SharedFade"));

                string stamp = DateTime.Now.ToString("HHmmss");
                float[] gpu = GpuRender(mat, DTileSize);
                SavePng(gpu, DTileSize, Path.Combine(dir, "port_" + stamp + "_gpu.png"));

                DJob j = new DJob();
                j.X0 = x0; j.Z0 = z0; j.Mpp = mpp;
                j.Port = SnapshotPort(x0, z0, size);
                j.Port.TimeX = Time.timeSinceLevelLoad / 20f; j.Port.TimeY = Time.timeSinceLevelLoad;
                j.Port.Zoom = 1000f; j.Port.Cells = 0f;
                Vector4 co = Shader.GetGlobalVector("_CloudOffset");
                j.Port.CloudX = co.x; j.Port.CloudZ = co.z;
                j.Port.Clouds = false; j.Port.CellPerPixel = false;
                byte[] cpu = Composite(RenderPortTile(j), j.Pixels2);
                File.WriteAllBytes(Path.Combine(dir, "port_" + stamp + "_cpu.png"), EncodePng(cpu, DTileSize));
                double diff = 0; int n = DTileSize * DTileSize;
                byte[] dimg = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float d = gpu[i * 4 + c] * 255f - cpu[i * 4 + c];
                        diff += Math.Abs(d);
                        dimg[i * 4 + c] = (byte)Mathf.Clamp(128f + d * 2f, 0f, 255f);      // grey = equal, brighter = shader brighter
                    }
                    dimg[i * 4 + 3] = 255;
                }
                File.WriteAllBytes(Path.Combine(dir, "port_" + stamp + "_diff.png"), EncodePng(dimg, DTileSize));
                log.AppendLine("port vs shader: mean difference " + (diff / (n * 3)).ToString("F2") + " of 255 per channel");

                // the game's own zoom and pixel size: the snapping grid and the coast line width
                {
                    float zoom = src.GetFloat("_zoom");
                    mat.SetFloat("_zoom", zoom); mat.SetFloat("_pixelSize", 200f / zoom);
                    float[] g2 = GpuRender(mat, DTileSize);
                    SavePng(g2, DTileSize, Path.Combine(dir, "port_" + stamp + "_gpu_snap.png"));
                    int keepC = s_portCoastFrom;
                    try
                    {
                        for (int k = 0; k < 2; k++)
                        {
                            s_portCoastFrom = k;
                            DJob jk = new DJob();
                            jk.X0 = x0; jk.Z0 = z0; jk.Mpp = mpp;
                            jk.Port = SnapshotPort(x0, z0, size);
                            jk.Port.TimeX = j.Port.TimeX; jk.Port.TimeY = j.Port.TimeY;
                            jk.Port.Zoom = k == 0 ? zoom : 200f / zoom;
                            jk.Port.Cells = 7000f;
                            jk.Port.CloudX = co.x; jk.Port.CloudZ = co.z;
                            jk.Port.Clouds = false; jk.Port.CellPerPixel = false;
                            byte[] ck = Composite(RenderPortTile(jk), jk.Pixels2);
                            double dk = 0;
                            for (int i = 0; i < n; i++)
                                for (int c = 0; c < 3; c++) dk += Math.Abs(g2[i * 4 + c] * 255f - ck[i * 4 + c]);
                            log.AppendLine("  snapped, zoom " + zoom.ToString("F3") + ", coast width from " + (k == 0 ? "_zoom" : "_pixelSize") + ": mean difference " + (dk / (n * 3)).ToString("F2"));
                            File.WriteAllBytes(Path.Combine(dir, "port_" + stamp + "_cpu_snap" + k + ".png"), EncodePng(ck, DTileSize));
                        }
                    }
                    finally { s_portCoastFrom = keepC; mat.SetFloat("_zoom", 1000f); mat.SetFloat("_pixelSize", 1000f); }
                }

                // which constant does what
                Shader s = src.shader;
                for (int i = 0; i < s.GetPropertyCount(); i++)
                {
                    if (s.GetPropertyType(i) != ShaderPropertyType.Color) continue;
                    string name = s.GetPropertyName(i);
                    Color keep = mat.GetColor(name);
                    mat.SetColor(name, new Color(1f, 0f, 1f, 1f));
                    log.AppendLine("material " + name + " = " + keep + ": " + Changed(gpu, GpuRender(mat, DTileSize)));
                    mat.SetColor(name, keep);
                }
                foreach (string g in PortTestGlobals)
                {
                    Color keep = Shader.GetGlobalColor(g);
                    Shader.SetGlobalColor(g, new Color(1f, 0f, 1f, 1f));
                    log.AppendLine("global " + g + ": " + Changed(gpu, GpuRender(mat, DTileSize)));
                    Shader.SetGlobalColor(g, keep);
                }
                {
                    Vector4 keep = Shader.GetGlobalVector("_CloudOffset");
                    Shader.SetGlobalVector("_CloudOffset", keep + new Vector4(0.37f, 0f, 0.21f, 0f));
                    log.AppendLine("global _CloudOffset moved: " + Changed(gpu, GpuRender(mat, DTileSize)));
                    Shader.SetGlobalVector("_CloudOffset", keep);
                }
            }
            finally { UnityEngine.Object.Destroy(mat); }
            try { PortTestLevels(dir, log); }
            catch (Exception e) { log.AppendLine("levels test failed: " + e); }
            Logger.LogInfo(log.ToString());
            return "livingmap port: port_*_gpu/cpu/diff.png written to " + dir + "; details in the BepInEx log";
        }

        // The map as it is shown (its material, Living Map's textures, the game's own zoom) against
        // the first level (the same textures through the port) and the second (Living Map's data
        // painted by the port), over the second level's tile at the map centre: mean colour of
        // each and their differences, and the three pictures.
        private void PortTestLevels(string dir, StringBuilder log)
        {
            Rect uvr = _mm.m_mapImageLarge.uvRect;
            float world = _texSize * _pixelSize;
            float cx = (uvr.center.x - 0.5f) * world, cz = (uvr.center.y - 0.5f) * world;
            int lv = 1;
            float mpp = DLevels[lv], size = DTileSize * mpp;
            DTile t1 = new DTile { Level = lv, TX = Mathf.FloorToInt(cx / size), TZ = Mathf.FloorToInt(cz / size) };
            DJob j1 = BuildJob(t1, 0f, false);
            if (j1.Port == null) { log.AppendLine("levels: no port job"); return; }
            j1.Port.Clouds = true;                                     // clouds are their own layer on screen
            byte[] second = Composite(RenderPortTile(j1, WorldGenerator.instance), j1.Pixels2);
            // the same window through the first level's inputs
            DJob j0 = BuildJob(t1, 0f, false);
            j0.Port = SnapshotPort(j0.X0, j0.Z0, size, true);
            j0.Port.Clouds = true;
            byte[] first = Composite(RenderPortTile(j0, null), j0.Pixels2);
            // the real shader: the map material as it is, over the same window, clouds excluded
            Material mat = new Material(_portMat);
            float[] gpu;
            try
            {
                float w = size / world, u0 = j1.X0 / world + 0.5f, v0 = j1.Z0 / world + 0.5f;
                mat.SetTextureScale("_MainTex", new Vector2(w, w));
                mat.SetTextureOffset("_MainTex", new Vector2(u0, v0));
                float zoom = _portMat.GetFloat("_zoom");
                mat.SetFloat("_zoom", zoom); mat.SetFloat("_pixelSize", 200f / zoom);
                mat.SetVector("_mapCenter", _portMat.GetVector("_mapCenter"));

                Texture2D none = new Texture2D(1, 1, TextureFormat.Alpha8, false);
                none.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f)); none.Apply();
                mat.SetTexture("_CloudTex", none);
                gpu = GpuRender(mat, DTileSize);
                UnityEngine.Object.Destroy(none);
            }
            finally { UnityEngine.Object.Destroy(mat); }
            string stamp = DateTime.Now.ToString("HHmmss");
            SavePng(gpu, DTileSize, Path.Combine(dir, "levels_" + stamp + "_shader.png"));
            File.WriteAllBytes(Path.Combine(dir, "levels_" + stamp + "_first.png"), EncodePng(first, DTileSize));
            File.WriteAllBytes(Path.Combine(dir, "levels_" + stamp + "_second.png"), EncodePng(second, DTileSize));
            int n = DTileSize * DTileSize;
            double[] mg = new double[3], m0 = new double[3], m1 = new double[3];
            double d0 = 0, d1 = 0;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++)
                {
                    float g = gpu[i * 4 + c] * 255f;
                    mg[c] += g; m0[c] += first[i * 4 + c]; m1[c] += second[i * 4 + c];
                    d0 += Math.Abs(g - first[i * 4 + c]); d1 += Math.Abs(g - second[i * 4 + c]);
                }
            log.AppendLine("levels at x " + j1.X0 + ", z " + j1.Z0 + " (" + mpp + " m/px): mean rgb shader (" + (mg[0] / n).ToString("F1") + ", " + (mg[1] / n).ToString("F1") + ", " + (mg[2] / n).ToString("F1")
                           + "), first (" + (m0[0] / n).ToString("F1") + ", " + (m0[1] / n).ToString("F1") + ", " + (m0[2] / n).ToString("F1")
                           + "), second (" + (m1[0] / n).ToString("F1") + ", " + (m1[1] / n).ToString("F1") + ", " + (m1[2] / n).ToString("F1") + ")");
            log.AppendLine("  shader vs first: " + (d0 / (n * 3)).ToString("F2") + ", shader vs second: " + (d1 / (n * 3)).ToString("F2"));
        }

        // the fog layer over the base, as the UI blends them (in linear light)
        private static byte[] Composite(byte[] px, byte[] fog)
        {
            if (fog == null) return px;
            for (int o = 0; o < px.Length; o += 4)
            {
                float a = fog[o + 3] / 255f;
                if (a <= 0f) continue;
                for (int c = 0; c < 3; c++)
                {
                    float lin = SrgbToLinear(px[o + c] / 255f) * (1f - a) + SrgbToLinear(fog[o + c] / 255f) * a;
                    px[o + c] = LinearToSrgbByte(lin);
                }
            }
            return px;
        }

        private static string Changed(float[] a, float[] b)
        {
            int n = a.Length / 4, changed = 0;
            double dr = 0, dg = 0, db = 0;
            for (int i = 0; i < n; i++)
            {
                float r = b[i * 4] - a[i * 4], g = b[i * 4 + 1] - a[i * 4 + 1], bl = b[i * 4 + 2] - a[i * 4 + 2];
                if (Mathf.Abs(r) + Mathf.Abs(g) + Mathf.Abs(bl) > 0.01f) changed++;
                dr += r; dg += g; db += bl;
            }
            return (100.0 * changed / n).ToString("F1") + "% of pixels changed, mean delta rgb (" + (dr / n).ToString("F3") + ", " + (dg / n).ToString("F3") + ", " + (db / n).ToString("F3") + ")";
        }

        // the material drawn over a size x size sRGB target; returns sRGB-encoded 0..1 values
        private static float[] GpuRender(Material mat, int size)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Texture2D read = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0f, 0f, 0f, 1f));
                // a quad with uv 0..1: the shader's own _MainTex_ST picks the window (a Blit
                // would reset it)
                GL.PushMatrix();
                GL.LoadOrtho();
                mat.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(0f, 1f); GL.Vertex3(0f, 1f, 0f);
                GL.TexCoord2(1f, 1f); GL.Vertex3(1f, 1f, 0f);
                GL.TexCoord2(1f, 0f); GL.Vertex3(1f, 0f, 0f);
                GL.End();
                GL.PopMatrix();
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                read.Apply(false);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            Color32[] px = read.GetPixels32();
            UnityEngine.Object.Destroy(read);
            float[] f = new float[px.Length * 4];
            for (int i = 0; i < px.Length; i++)
            {
                f[i * 4] = px[i].r / 255f; f[i * 4 + 1] = px[i].g / 255f; f[i * 4 + 2] = px[i].b / 255f; f[i * 4 + 3] = px[i].a / 255f;
            }
            return f;
        }

        private static void SavePng(float[] f, int size, string path)
        {
            byte[] b = new byte[f.Length];
            for (int i = 0; i < f.Length; i++) b[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(f[i] * 255f), 0, 255);
            File.WriteAllBytes(path, EncodePng(b, size));
        }

        private static byte[] EncodePng(byte[] rgba, int size)
        {
            Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            try
            {
                t.LoadRawTextureData(rgba);
                t.Apply(false);
                return ImageConversion.EncodeToPNG(t);
            }
            finally { UnityEngine.Object.Destroy(t); }
        }
    }
}
