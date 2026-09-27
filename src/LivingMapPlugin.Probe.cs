using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace LivingMap
{
    // Probe for the next step of the detail layer: can the game's own map shader draw a
    // zoomed-in window from cropped, upscaled copies of its input textures? A dev tool only:
    // 'livingmap probe' logs the shader's properties and textures; 'probe show [size]' lays a
    // copy of the map material over the big map, fed with the visible part of each input
    // texture blitted to size x size; 'probe same' does the same with the original textures
    // (a baseline: it must look exactly like the map under it); 'probe hide' removes it.
    public partial class LivingMapPlugin
    {
        private static readonly string[] ProbeTexNames = { "_MainTex", "_HeightTex", "_MaskTex", "_FogTex" };
        private RawImage _probeImg;
        private Material _probeMat;
        private RenderTexture[] _probeRt;
        private Texture[] _probeSrc;
        private Rect _probeUv;
        private bool _probeCrop;
        private int _probeSize;

        private string ProbeCommand(string[] a)
        {
            if (!_ready || _mm == null || _mm.m_mapImageLarge == null) return "livingmap probe: not attached to a map right now";
            string mode = a.Length > 2 ? a[2].ToLowerInvariant() : "";
            if (mode == "hide") { ProbeHide(); return "livingmap probe: overlay removed"; }
            if (mode == "same") return ProbeShow(false, 0);
            if (mode == "show")
            {
                int size = 2048, s;
                if (a.Length > 3 && int.TryParse(a[3], out s)) size = Mathf.Clamp(s, 256, 8192);
                return ProbeShow(true, size);
            }
            string dump = ProbeDump();
            Logger.LogInfo(dump);
            return "livingmap probe: shader and textures written to the BepInEx log. Also: 'livingmap probe show [2048]', 'probe same', 'probe hide'.";
        }

        private string ProbeDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==== LivingMap probe: map material ====");
            DumpImage(sb, "large", _mm.m_mapImageLarge);
            DumpImage(sb, "small", _mm.m_mapImageSmall);
            sb.AppendLine("Minimap textures: map " + TexInfo(MmField(typeof(Minimap).GetField("m_mapTexture", MmFlags)) as Texture) + " | height " + TexInfo(MmField(typeof(Minimap).GetField("m_heightTexture", MmFlags)) as Texture)
                          + " | forest " + TexInfo(MmField(typeof(Minimap).GetField("m_forestMaskTexture", MmFlags)) as Texture) + " | fog " + TexInfo(MmField(typeof(Minimap).GetField("m_fogTexture", MmFlags)) as Texture));
            Material ml = MmField(typeof(Minimap).GetField("m_mapLargeShader", MmFlags)) as Material, ms = MmField(typeof(Minimap).GetField("m_mapSmallShader", MmFlags)) as Material;
            sb.AppendLine("m_mapLargeShader " + (ml != null ? ml.name + " / " + ml.shader.name : "null")
                          + ", m_mapSmallShader " + (ms != null ? ms.name + " / " + ms.shader.name : "null"));
            sb.AppendLine("colour space " + QualitySettings.activeColorSpace + ", graphics " + SystemInfo.graphicsDeviceType);
            return sb.ToString();
        }

        private const System.Reflection.BindingFlags MmFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        private object MmField(System.Reflection.FieldInfo f) { return f != null ? f.GetValue(_mm) : null; }

        private static void DumpImage(StringBuilder sb, string which, RawImage img)
        {
            if (img == null) { sb.AppendLine(which + ": no image"); return; }
            Material m = img.material;
            RectTransform rt = img.rectTransform;
            sb.AppendLine(which + ": uvRect " + img.uvRect + ", rect " + rt.rect.size + ", image texture " + TexInfo(img.texture) + ", colour " + img.color);
            if (m == null) { sb.AppendLine("  no material"); return; }
            Shader s = m.shader;
            sb.AppendLine("  material '" + m.name + "', shader '" + s.name + "', queue " + m.renderQueue + ", passes " + m.passCount
                          + ", keywords [" + string.Join(" ", m.shaderKeywords) + "]");
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = s.GetPropertyName(i);
                ShaderPropertyType t = s.GetPropertyType(i);
                string v;
                switch (t)
                {
                    case ShaderPropertyType.Color: v = m.GetColor(name).ToString(); break;
                    case ShaderPropertyType.Vector: v = m.GetVector(name).ToString("F4"); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: v = m.GetFloat(name).ToString("F4"); break;
                    case ShaderPropertyType.Texture:
                        v = TexInfo(m.GetTexture(name)) + ", scale " + m.GetTextureScale(name) + ", offset " + m.GetTextureOffset(name);
                        break;
                    default: v = "?"; break;
                }
                sb.AppendLine("  " + t + " " + name + " = " + v + "  (" + s.GetPropertyDescription(i) + ")");
            }
            // properties set on the material that the shader does not declare (set from code)
            foreach (string name in new[] { "_mapCenter", "_pixelSize", "_zoom", "_SharedFade" })
                if (s.FindPropertyIndex(name) < 0)
                    sb.AppendLine("  (undeclared) " + name + " float " + m.GetFloat(name).ToString("F4") + " vector " + m.GetVector(name).ToString("F4"));
        }

        private static string TexInfo(Texture t)
        {
            if (t == null) return "null";
            string f = t is Texture2D ? ((Texture2D)t).format.ToString() : t is RenderTexture ? ((RenderTexture)t).format.ToString() : t.GetType().Name;
            return "'" + t.name + "' " + t.width + "x" + t.height + " " + f + " (" + t.graphicsFormat + ") filter " + t.filterMode + " wrap " + t.wrapMode;
        }

        private string ProbeShow(bool crop, int size)
        {
            ProbeHide();
            RawImage img = _mm.m_mapImageLarge;
            Material src = img.material;
            if (src == null) return "livingmap probe: the map has no material";
            _probeMat = new Material(src);
            _probeMat.name = "LivingMap probe";
            _probeCrop = crop; _probeSize = size;
            _probeSrc = new Texture[ProbeTexNames.Length];
            _probeRt = new RenderTexture[ProbeTexNames.Length];
            StringBuilder sb = new StringBuilder("livingmap probe: overlay " + (crop ? "with cropped textures " + size + "px" : "with the original textures"));
            for (int i = 0; i < ProbeTexNames.Length; i++)
            {
                Texture t = src.GetTexture(ProbeTexNames[i]);
                _probeSrc[i] = t;
                if (t == null || !crop) continue;
                RenderTexture rt = new RenderTexture(size, size, 0, RtFormat(t));
                rt.filterMode = t.filterMode; rt.wrapMode = TextureWrapMode.Clamp;
                rt.name = "LivingMap probe " + ProbeTexNames[i];
                rt.Create();
                _probeRt[i] = rt;
                _probeMat.SetTexture(ProbeTexNames[i], rt);
                _probeMat.SetTextureScale(ProbeTexNames[i], Vector2.one);
                _probeMat.SetTextureOffset(ProbeTexNames[i], Vector2.zero);
                sb.Append("; " + ProbeTexNames[i] + " " + rt.format);
            }

            GameObject go = new GameObject("LivingMap_Probe", typeof(RectTransform));
            go.transform.SetParent(img.transform, false);
            RectTransform r = (RectTransform)go.transform;
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
            _probeImg = go.AddComponent<RawImage>();
            _probeImg.raycastTarget = false;
            _probeImg.texture = crop ? null : img.texture;
            _probeImg.material = _probeMat;
            _probeImg.color = img.color;
            _probeUv = new Rect(-1f, -1f, 0f, 0f);
            if (_dRoot != null) _dRoot.gameObject.SetActive(false);     // the detail layer would cover it
            ProbeTick();
            Logger.LogInfo(sb.ToString());
            return sb.ToString() + ". Pan and zoom freely; 'livingmap probe hide' removes it.";
        }

        private static RenderTextureFormat RtFormat(Texture t)
        {
            Texture2D t2 = t as Texture2D;
            if (t2 != null)
            {
                if (t2.format == TextureFormat.RFloat) return RenderTextureFormat.RFloat;
                if (t2.format == TextureFormat.RHalf) return RenderTextureFormat.RHalf;
                if (t2.format == TextureFormat.RGBAFloat) return RenderTextureFormat.ARGBFloat;
                if (t2.format == TextureFormat.RGBAHalf) return RenderTextureFormat.ARGBHalf;
            }
            RenderTexture r = t as RenderTexture;
            if (r != null) return r.format;
            return RenderTextureFormat.ARGB32;
        }

        // every frame: the material values the game changes (zoom, centre, fades...) follow the
        // original; in crop mode the visible part of each texture is blitted again after a pan
        private void ProbeTick()
        {
            if (_probeImg == null || _probeMat == null) return;
            RawImage img = _mm != null ? _mm.m_mapImageLarge : null;
            if (img == null || img.material == null) { ProbeHide(); return; }
            Material src = img.material;
            Shader s = src.shader;
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = s.GetPropertyName(i);
                switch (s.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: _probeMat.SetColor(name, src.GetColor(name)); break;
                    case ShaderPropertyType.Vector: _probeMat.SetVector(name, src.GetVector(name)); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: _probeMat.SetFloat(name, src.GetFloat(name)); break;
                }
            }
            _probeImg.color = img.color;
            if (!_probeCrop)
            {
                for (int i = 0; i < ProbeTexNames.Length; i++)
                    if (_probeSrc[i] != null) _probeMat.SetTexture(ProbeTexNames[i], src.GetTexture(ProbeTexNames[i]));
                _probeImg.uvRect = img.uvRect;
                return;
            }
            Rect uv = img.uvRect;
            if (uv == _probeUv) return;
            _probeUv = uv;
            for (int i = 0; i < ProbeTexNames.Length; i++)
            {
                Texture t = src.GetTexture(ProbeTexNames[i]);
                if (t == null || _probeRt[i] == null) continue;
                Graphics.Blit(t, _probeRt[i], new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
            }
            _probeImg.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        private void ProbeHide()
        {
            if (_probeImg != null) UnityEngine.Object.Destroy(_probeImg.gameObject);
            _probeImg = null;
            if (_probeMat != null) UnityEngine.Object.Destroy(_probeMat);
            _probeMat = null;
            if (_probeRt != null)
                for (int i = 0; i < _probeRt.Length; i++)
                    if (_probeRt[i] != null) { _probeRt[i].Release(); UnityEngine.Object.Destroy(_probeRt[i]); }
            _probeRt = null;
            if (_dRoot != null) _dRoot.gameObject.SetActive(true);
        }
    }
}
