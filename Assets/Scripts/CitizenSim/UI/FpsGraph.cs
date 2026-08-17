using UnityEngine;
using UnityEngine.UI;

namespace CitizenSim
{
    // FPS 滚动柱状图(最近 120 帧)。OnPopulateMesh 把 samples[] 画成竖条;颜色按 fps 分档:
    // >=55 绿,30..55 黄,<30 红(5000 天花板视觉标红)。由 Hud 每帧 Push。
    public class FpsGraph : Graphic
    {
        public float maxFps = 120f;
        readonly float[] samples = new float[120];
        int head;
        int filled;

        public void Push(float fps)
        {
            samples[head] = fps;
            head = (head + 1) % samples.Length;
            if (filled < samples.Length) filled++;
            SetVerticesDirty();
        }

        static Color ColorFor(float fps)
        {
            if (fps >= 55f) return new Color(0.2f, 0.8f, 0.2f);
            if (fps >= 30f) return new Color(0.9f, 0.8f, 0.2f);
            return new Color(0.9f, 0.2f, 0.2f);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (filled == 0) return;
            var r = rectTransform.rect;
            float w = r.width;
            float h = r.height;
            float barW = w / samples.Length;
            for (int i = 0; i < filled; i++)
            {
                int idx = (head - filled + i + samples.Length) % samples.Length;
                float v = Mathf.Clamp(samples[idx], 0f, maxFps);
                float barH = (v / maxFps) * h;
                float x0 = -w * 0.5f + i * barW;
                float x1 = x0 + Mathf.Max(1f, barW - 1f);
                float y0 = -h * 0.5f;
                float y1 = y0 + barH;
                Color c = ColorFor(samples[idx]);
                int s = vh.currentVertCount;
                vh.AddVert(new Vector3(x0, y0), c, Vector2.zero);
                vh.AddVert(new Vector3(x1, y0), c, Vector2.zero);
                vh.AddVert(new Vector3(x1, y1), c, Vector2.zero);
                vh.AddVert(new Vector3(x0, y1), c, Vector2.zero);
                vh.AddTriangle(s, s + 1, s + 2);
                vh.AddTriangle(s, s + 2, s + 3);
            }
        }
    }
}
