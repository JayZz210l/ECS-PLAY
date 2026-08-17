using UnityEngine;
using UnityEngine.UI;

namespace CitizenSim
{
    public class Hud : MonoBehaviour
    {
        public Text text;
        public FpsGraph fpsGraph;
        public CitizenBootstrap bootstrap;
        private int frames;
        private float timer;
        private int fps;

        // 颜色->行为图例(与 ColoringSystem 一致)。UGUI 程序化生成,免场景手动摆放。
        struct LegendEntry { public GoalType type; public string label; }
        static readonly LegendEntry[] kLegend =
        {
            new LegendEntry { type = GoalType.SeekFood, label = "SeekFood" },
            new LegendEntry { type = GoalType.SeekHome, label = "SeekHome" },
            new LegendEntry { type = GoalType.SeekFun,  label = "SeekFun" },
            new LegendEntry { type = GoalType.Wander,   label = "Wander" },
            new LegendEntry { type = GoalType.Flee,     label = "Flee" },
        };

        // 图例每行的计数 Text(Update 里刷新数字)。
        readonly Text[] _legendCounts = new Text[kLegend.Length];
        readonly int[] _counts = new int[kLegend.Length];

        // 玩家输入提示(与 ScaleDial 一致)。
        struct InputEntry { public string key; public string label; }
        static readonly InputEntry[] kInputs =
        {
            new InputEntry { key = "1-4",  label = "切换规模 100/500/2000/5000" },
            new InputEntry { key = "WASD", label = "移动威胁区" },
            new InputEntry { key = "T",    label = "开关威胁区" },
            new InputEntry { key = "E",    label = "鼠标处生成恐惧区" },
        };

        void Start()
        {
            CreateLegend();
            CreateInputHints();
        }

        void Update()
        {
            frames++;
            timer += Time.unscaledDeltaTime;
            if (timer >= 0.5f)
            {
                fps = Mathf.RoundToInt(frames / timer);
                frames = 0;
                timer = 0f;
            }

            int instantFps = Mathf.RoundToInt(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f));
            if (fpsGraph != null) fpsGraph.Push(instantFps);

            var reg = CitizenRegistry.Instance;
            int count = reg != null ? reg.Count : 0;

            int tc = 0;
            var authoring = reg != null ? reg.Authoring : null;
            if (authoring != null)
                for (int i = 0; i < authoring.Length; i++)
                    if (authoring[i] != null && authoring[i].threatened) tc++;

            int ticks = BtScheduler.Instance != null ? BtScheduler.Instance.LastTickCount : 0;
            int scale = bootstrap != null ? bootstrap.count : count;

            if (text != null)
                text.text = $"FPS {fps} | Citizens {count} | Threatened {tc} | Scale {scale} | BT ticks/frame {ticks}";

            UpdateLegendCounts();
        }

        // 统计每种状态(currentGoalType)的市民数,刷新图例右侧数字。
        void UpdateLegendCounts()
        {
            if (_legendCounts[0] == null) return;
            for (int k = 0; k < _counts.Length; k++) _counts[k] = 0;
            var authoring = CitizenRegistry.Instance != null ? CitizenRegistry.Instance.Authoring : null;
            if (authoring != null)
                for (int i = 0; i < authoring.Length; i++)
                    if (authoring[i] != null)
                    {
                        var t = authoring[i].currentGoalType;
                        for (int k = 0; k < kLegend.Length; k++)
                            if (kLegend[k].type == t) { _counts[k]++; break; }
                    }
            for (int k = 0; k < _legendCounts.Length; k++)
                if (_legendCounts[k] != null)
                    _legendCounts[k].text = _counts[k].ToString();
        }

        void CreateLegend()
        {
            if (text == null) return;
            Canvas canvas = text.canvas;
            if (canvas == null) return;
            Font font = text.font != null ? text.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // 左下角半透明面板
            var panel = new GameObject("BehaviorLegend", typeof(Image));
            panel.transform.SetParent(canvas.transform, false);
            var pImg = panel.GetComponent<Image>();
            pImg.color = new Color(0f, 0f, 0f, 0.55f);
            pImg.raycastTarget = false;
            var rt = panel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(15f, 15f);
            rt.sizeDelta = new Vector2(320f, 192f);

            const float rowH = 36f;
            const float pad = 9f;
            for (int i = 0; i < kLegend.Length; i++)
            {
                var row = new GameObject("Row", typeof(RectTransform));
                row.transform.SetParent(rt, false);
                var rrt = row.GetComponent<RectTransform>();
                rrt.anchorMin = new Vector2(0f, 1f);
                rrt.anchorMax = new Vector2(1f, 1f);
                rrt.pivot = new Vector2(0f, 1f);
                rrt.anchoredPosition = new Vector2(pad, -pad - i * rowH);
                rrt.sizeDelta = new Vector2(-pad * 2, rowH - 1f);

                var sw = new GameObject("Swatch", typeof(Image));
                sw.transform.SetParent(rrt, false);
                var srt = sw.GetComponent<RectTransform>();
                srt.anchorMin = new Vector2(0f, 0.5f);
                srt.anchorMax = new Vector2(0f, 0.5f);
                srt.pivot = new Vector2(0f, 0.5f);
                srt.anchoredPosition = Vector2.zero;
                srt.sizeDelta = new Vector2(24f, 24f);
                var sImg = sw.GetComponent<Image>();
                sImg.color = ColoringSystem.ColorFor(kLegend[i].type);
                sImg.raycastTarget = false;

                var lbl = new GameObject("Label", typeof(Text));
                lbl.transform.SetParent(rrt, false);
                var lrt = lbl.GetComponent<RectTransform>();
                lrt.anchorMin = new Vector2(0f, 0.5f);
                lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.anchoredPosition = new Vector2(33f, 0f);
                lrt.sizeDelta = new Vector2(-33f, 30f);
                var t = lbl.GetComponent<Text>();
                t.text = kLegend[i].label;
                t.font = font;
                t.fontSize = 21;
                t.alignment = TextAnchor.MiddleLeft;
                t.color = Color.white;
                t.raycastTarget = false;

                // 右侧计数(UpdateLegendCounts 每帧刷新)
                var cnt = new GameObject("Count", typeof(Text));
                cnt.transform.SetParent(rrt, false);
                var crt = cnt.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(1f, 0.5f);
                crt.anchorMax = new Vector2(1f, 0.5f);
                crt.pivot = new Vector2(1f, 0.5f);
                crt.anchoredPosition = new Vector2(-14f, 0f);
                crt.sizeDelta = new Vector2(70f, 30f);
                var ct = cnt.GetComponent<Text>();
                ct.text = "0";
                ct.font = font;
                ct.fontSize = 21;
                ct.alignment = TextAnchor.MiddleRight;
                ct.color = new Color(0.8f, 0.9f, 1f);
                ct.raycastTarget = false;
                _legendCounts[i] = ct;
            }
        }

        void CreateInputHints()
        {
            if (text == null) return;
            Canvas canvas = text.canvas;
            if (canvas == null) return;
            Font font = text.font != null ? text.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // 右下角半透明面板
            var panel = new GameObject("InputHints", typeof(Image));
            panel.transform.SetParent(canvas.transform, false);
            var pImg = panel.GetComponent<Image>();
            pImg.color = new Color(0f, 0f, 0f, 0.55f);
            pImg.raycastTarget = false;
            var rt = panel.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-15f, 15f);
            rt.sizeDelta = new Vector2(340f, 36f * kInputs.Length + 9f * 2f);

            const float rowH = 36f;
            const float pad = 9f;
            for (int i = 0; i < kInputs.Length; i++)
            {
                var row = new GameObject("Row", typeof(RectTransform));
                row.transform.SetParent(rt, false);
                var rrt = row.GetComponent<RectTransform>();
                rrt.anchorMin = new Vector2(0f, 1f);
                rrt.anchorMax = new Vector2(1f, 1f);
                rrt.pivot = new Vector2(0f, 1f);
                rrt.anchoredPosition = new Vector2(pad, -pad - i * rowH);
                rrt.sizeDelta = new Vector2(-pad * 2, rowH - 1f);

                // 左侧按键高亮(金色)
                var key = new GameObject("Key", typeof(Text));
                key.transform.SetParent(rrt, false);
                var krt = key.GetComponent<RectTransform>();
                krt.anchorMin = new Vector2(0f, 0.5f);
                krt.anchorMax = new Vector2(0f, 0.5f);
                krt.pivot = new Vector2(0f, 0.5f);
                krt.anchoredPosition = Vector2.zero;
                krt.sizeDelta = new Vector2(90f, 30f);
                var kt = key.GetComponent<Text>();
                kt.text = kInputs[i].key;
                kt.font = font;
                kt.fontSize = 20;
                kt.alignment = TextAnchor.MiddleLeft;
                kt.color = new Color(1f, 0.83f, 0.35f);
                kt.raycastTarget = false;

                var lbl = new GameObject("Label", typeof(Text));
                lbl.transform.SetParent(rrt, false);
                var lrt = lbl.GetComponent<RectTransform>();
                lrt.anchorMin = new Vector2(0f, 0.5f);
                lrt.anchorMax = new Vector2(1f, 0.5f);
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.anchoredPosition = new Vector2(98f, 0f);
                lrt.sizeDelta = new Vector2(-98f, 30f);
                var t = lbl.GetComponent<Text>();
                t.text = kInputs[i].label;
                t.font = font;
                t.fontSize = 20;
                t.alignment = TextAnchor.MiddleLeft;
                t.color = Color.white;
                t.raycastTarget = false;
            }
        }
    }
}
