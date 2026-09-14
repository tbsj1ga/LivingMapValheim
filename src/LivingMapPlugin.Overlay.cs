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
        // detailed overlays, one per map view
        private class Layer
        {
            public string Name;
            public GameObject Go;
            public RawImage Image;
            public Texture2D Tex;
            public Color32[] Buf;
            public Vector2 LastOrigin = new Vector2(float.MinValue, float.MinValue);
            public float LastScale = float.MinValue;
            public int LastDrawn = -1;
            public int Redraws;
        }

        private readonly Layer _layerLarge = new Layer { Name = "Map" };
        private readonly Layer _layerSmall = new Layer { Name = "Minimap" };
        private bool _hiResDisabled;
        private bool _announcedFirstDraw;

        // ------------------------------------------------------------------
        // detailed overlays - drawn in map-view space, so they stay sharp when zoomed in
        // ------------------------------------------------------------------
        private void UpdateOverlays()
        {
            if (_mm == null || !_cfgHiRes.Value)
            {
                HideLayer(_layerLarge);
                HideLayer(_layerSmall);
                return;
            }

            UpdateLayer(_layerLarge, _mm.m_mapImageLarge, _mm.m_largeRoot,
                _mm.m_pinRootLarge, _cfgHiResSize.Value, true);

            if (_cfgMinimapOverlay.Value)
                UpdateLayer(_layerSmall, _mm.m_mapImageSmall, _mm.m_smallRoot,
                    _mm.m_pinRootSmall, _cfgMinimapSize.Value, false);
            else
                HideLayer(_layerSmall);
        }

        private static void HideLayer(Layer layer)
        {
            if (layer.Go != null && layer.Go.activeSelf) layer.Go.SetActive(false);
        }

        private void UpdateLayer(Layer layer, RawImage src, GameObject root, RectTransform pinRoot, int size, bool isLarge)
        {
            if (src == null || root == null || !root.activeInHierarchy)
            {
                HideLayer(layer);
                return;
            }

            EnsureLayer(layer, src, pinRoot, size);
            if (layer.Tex == null || layer.Image == null) return;

            // affine basis: world (x,z) -> local gui position inside this map image
            Vector2 gOrigin, gX, gZ;
            if (!WorldToGui(src, Vector3.zero, out gOrigin)) return;
            if (!WorldToGui(src, new Vector3(1000f, 0f, 0f), out gX)) return;
            if (!WorldToGui(src, new Vector3(0f, 0f, 1000f), out gZ)) return;

            float sx = (gX.x - gOrigin.x) / 1000f;
            float sz = (gZ.y - gOrigin.y) / 1000f;
            if (Mathf.Abs(sx) < 1e-8f || Mathf.Abs(sz) < 1e-8f) return;

            bool viewChanged = Mathf.Abs(sx - layer.LastScale) > 1e-7f ||
                               (gOrigin - layer.LastOrigin).sqrMagnitude > 0.01f;
            if (!viewChanged && _pending.Count == 0 && layer.Redraws > 0)
            {
                layer.Go.SetActive(true);
                return;
            }

            layer.LastScale = sx;
            layer.LastOrigin = gOrigin;

            // MapPointToLocalGuiPos returns 0..rect.width / 0..rect.height measured from the
            // image's lower-left corner, using the MAP image's own rect.
            Rect rect = src.rectTransform.rect;
            if (rect.width <= 1f || rect.height <= 1f) return;

            int texW = layer.Tex.width, texH = layer.Tex.height;
            Color32[] buf = layer.Buf;
            float pxPerGuiX = texW / rect.width;
            float pxPerGuiY = texH / rect.height;

            Array.Clear(buf, 0, buf.Length);

            float wx0 = (0f - gOrigin.x) / sx;
            float wx1 = (rect.width - gOrigin.x) / sx;
            float wz0 = (0f - gOrigin.y) / sz;
            float wz1 = (rect.height - gOrigin.y) / sz;
            if (wx0 > wx1) { float t = wx0; wx0 = wx1; wx1 = t; }
            if (wz0 > wz1) { float t = wz0; wz0 = wz1; wz1 = t; }

            BitArray explored = null, exploredOthers = null;
            if (_cfgRespectFog.Value)
            {
                explored = _fiExplored != null ? _fiExplored.GetValue(_mm) as BitArray : null;
                exploredOthers = _fiExploredOthers != null ? _fiExploredOthers.GetValue(_mm) as BitArray : null;
            }

            int drawn = 0;

            if (_cfgShowPaths.Value && _terrain.Count > 0)
            {
                float grid = Mathf.Max(0.5f, _cfgTerrainGrid.Value);
                int halfW = Mathf.Max(0, Mathf.RoundToInt(grid * Mathf.Abs(sx) * pxPerGuiX * 0.5f));
                int halfH = Mathf.Max(0, Mathf.RoundToInt(grid * Mathf.Abs(sz) * pxPerGuiY * 0.5f));
                foreach (KeyValuePair<long, byte> kv in _terrain)
                {
                    int gx = (int)(kv.Key >> 32);
                    int gz = (int)(kv.Key & 0xFFFFFFFFL);
                    float wx = (gx + 0.5f) * grid;
                    float wz = (gz + 0.5f) * grid;
                    if (wx < wx0 || wx > wx1 || wz < wz0 || wz > wz1) continue;
                    if (!IsWorldExplored(explored, exploredOthers, wx, wz)) continue;

                    int px = Mathf.RoundToInt((gOrigin.x + wx * sx) * pxPerGuiX);
                    int py = Mathf.RoundToInt((gOrigin.y + wz * sz) * pxPerGuiY);
                    Stamp(buf, px, py, halfW, halfH, TerrainColor(kv.Value), texW, texH);
                    drawn++;
                }
            }

            if (_cfgShowBuildings.Value && _pieces.Count > 0)
            {
                float minSize = Mathf.Max(0.1f, _cfgPieceSize.Value);
                float pxPerMetreX = Mathf.Abs(sx) * pxPerGuiX;
                float pxPerMetreY = Mathf.Abs(sz) * pxPerGuiY;
                int minHalfW = Mathf.Max(0, Mathf.RoundToInt(minSize * pxPerMetreX * 0.5f));
                int minHalfH = Mathf.Max(0, Mathf.RoundToInt(minSize * pxPerMetreY * 0.5f));

                foreach (KeyValuePair<int, List<PieceRec>> kv in _pieces)
                {
                    List<PieceRec> bucket = kv.Value;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        PieceRec rec = bucket[i];
                        if (rec.X1 < wx0 || rec.X0 > wx1 || rec.Z1 < wz0 || rec.Z0 > wz1) continue;
                        if (!IsWorldExplored(explored, exploredOthers, rec.CX, rec.CZ)) continue;

                        int cpx = Mathf.RoundToInt((gOrigin.x + rec.CX * sx) * pxPerGuiX);
                        int cpy = Mathf.RoundToInt((gOrigin.y + rec.CZ * sz) * pxPerGuiY);
                        int halfW = Mathf.Max(minHalfW, Mathf.RoundToInt((rec.X1 - rec.X0) * pxPerMetreX * 0.5f));
                        int halfH = Mathf.Max(minHalfH, Mathf.RoundToInt((rec.Z1 - rec.Z0) * pxPerMetreY * 0.5f));
                        Stamp(buf, cpx, cpy, halfW, halfH, MatColor(rec.Mat), texW, texH);
                        drawn++;
                    }
                }
            }

            if (_cfgDebugMarker.Value && isLarge && Player.m_localPlayer != null)
            {
                Vector3 pp = Player.m_localPlayer.transform.position;
                int px = Mathf.RoundToInt((gOrigin.x + pp.x * sx) * pxPerGuiX);
                int py = Mathf.RoundToInt((gOrigin.y + pp.z * sz) * pxPerGuiY);
                Color32 magenta = new Color32(255, 0, 255, 255);
                for (int d = -6; d <= 6; d++)
                {
                    Stamp(buf, px + d, py, 0, 0, magenta, texW, texH);
                    Stamp(buf, px, py + d, 0, 0, magenta, texW, texH);
                }
            }

            // nothing here and nothing last time: keep the texture as it is
            if (drawn == 0 && layer.LastDrawn == 0)
            {
                layer.Go.SetActive(false);
                return;
            }
            layer.LastDrawn = drawn;

            layer.Tex.SetPixels32(buf);
            layer.Tex.Apply(false);
            layer.Go.SetActive(true);
            layer.Redraws++;

            if (!_announcedFirstDraw && drawn > 0)
            {
                _announcedFirstDraw = true;
                Logger.LogInfo(string.Format(
                    "Detailed overlay drew for the first time on {0}: {1} shapes, texture {2}px, rect {3:0}x{4:0}, scale {5:0.####} gui/m",
                    layer.Name, drawn, texW, rect.width, rect.height, sx));
            }

            if (_cfgDebug.Value && (layer.Redraws % 40 == 1))
                Logger.LogInfo(string.Format("{0} overlay: drawn={1}, world x[{2:0}..{3:0}] z[{4:0}..{5:0}], {6:0.###} px/m",
                    layer.Name, drawn, wx0, wx1, wz0, wz1, Mathf.Abs(sx) * pxPerGuiX));
        }

        private static void Stamp(Color32[] buf, int cx, int cy, int halfW, int halfH, Color32 c, int texW, int texH)
        {
            int x0 = cx - halfW, x1 = cx + halfW;
            int y0 = cy - halfH, y1 = cy + halfH;
            if (x1 < 0 || y1 < 0 || x0 >= texW || y0 >= texH) return;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 >= texW) x1 = texW - 1;
            if (y1 >= texH) y1 = texH - 1;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * texW;
                for (int x = x0; x <= x1; x++) buf[row + x] = c;
            }
        }

        private bool WorldToGui(RawImage img, Vector3 world, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (_miWorldToMapPoint == null || _miMapPointToLocalGuiPos == null || img == null) return false;

            object[] a = { world, 0f, 0f };
            _miWorldToMapPoint.Invoke(_mm, a);
            object res = _miMapPointToLocalGuiPos.Invoke(_mm, new object[] { a[1], a[2], img });
            if (!(res is Vector2)) return false;
            gui = (Vector2)res;
            return true;
        }

        private void EnsureLayer(Layer layer, RawImage src, RectTransform pinRoot, int size)
        {
            if (layer.Tex != null && layer.Tex.width != size) DestroyLayer(layer);

            if (layer.Tex == null)
            {
                layer.Tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                layer.Tex.name = "LivingMap_" + layer.Name;
                layer.Tex.wrapMode = TextureWrapMode.Clamp;
                layer.Buf = new Color32[size * size];
                layer.Redraws = 0;
                layer.LastDrawn = -1;
                layer.LastScale = float.MinValue;
                layer.LastOrigin = new Vector2(float.MinValue, float.MinValue);
            }

            layer.Tex.filterMode = _cfgSmoothOverlay.Value ? FilterMode.Bilinear : FilterMode.Point;

            if (layer.Go == null)
            {
                layer.Go = new GameObject("LivingMap_" + layer.Name,
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));

                // A child of the map image: it inherits the map's mask and transform, sits right
                // on top of the map itself, and stays BELOW the pin layer so player and pin
                // markers keep drawing over it.
                RectTransform rt = layer.Go.GetComponent<RectTransform>();
                rt.SetParent(src.rectTransform, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;

                if (pinRoot != null && pinRoot.parent == rt.parent)
                    rt.SetSiblingIndex(pinRoot.GetSiblingIndex());

                layer.Image = layer.Go.GetComponent<RawImage>();
                layer.Image.texture = layer.Tex;
                layer.Image.raycastTarget = false;
            }
            else
            {
                layer.Image.texture = layer.Tex;
            }

            float a = Mathf.Clamp01(_cfgOverlayOpacity.Value);
            layer.Image.color = new Color(1f, 1f, 1f, a);
        }

        private static void DestroyLayer(Layer layer)
        {
            if (layer.Go != null)
            {
                try { UnityEngine.Object.Destroy(layer.Go); } catch { }
                layer.Go = null;
                layer.Image = null;
            }
            if (layer.Tex != null)
            {
                try { UnityEngine.Object.Destroy(layer.Tex); } catch { }
                layer.Tex = null;
            }
            layer.Buf = null;
            layer.Redraws = 0;
            layer.LastDrawn = -1;
        }

        private void DestroyHiRes()
        {
            DestroyLayer(_layerLarge);
            DestroyLayer(_layerSmall);
            _announcedFirstDraw = false;
        }
    }
}
