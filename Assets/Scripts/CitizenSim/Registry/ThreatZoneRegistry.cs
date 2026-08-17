using System.Collections.Generic;
using UnityEngine;

namespace CitizenSim
{
    // 威胁区单例(规格§5 第 4 段、§8 单区域)。运行时热键可移动/开关(ScaleDial)。
    // 临时恐惧区:按 E 在鼠标处生成(15m,8s),受 active 开关统一控制,纯数据(不可见,靠市民反应反馈)。
    public class ThreatZoneRegistry : MonoBehaviour
    {
        public static ThreatZoneRegistry Instance { get; private set; }

        public Transform[] zones;
        public float radius = 5f;
        public bool active = true;

        struct TempZone
        {
            public Vector3 position;
            public float radius;
            public float expireTime;
        }

        readonly List<TempZone> tempZones = new List<TempZone>();

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        // OnEnable 在 EditMode 不触发;测试/Bootstrap 显式注册用(同 PoiRegistry 模式)。
        public void Register() { Instance = this; }
        public static void Clear() { Instance = null; }

        public Vector3[] GetZonePositions()
        {
            if (zones == null || zones.Length == 0) return System.Array.Empty<Vector3>();
            var arr = new Vector3[zones.Length];
            for (int i = 0; i < zones.Length; i++)
                arr[i] = zones[i] != null ? zones[i].position : Vector3.zero;
            return arr;
        }

        // 含临时区的所有活动威胁位置(常驻+临时),供 BT Flee 选最近中心。
        // GetZonePositions 只返常驻;临时区触发的 Flee 必须用本方法,否则 evade 目标错。
        static readonly List<Vector3> s_ActivePosBuf = new List<Vector3>();
        public Vector3[] GetActiveZonePositions()
        {
            s_ActivePosBuf.Clear();
            if (active)
            {
                if (zones != null)
                    for (int i = 0; i < zones.Length; i++)
                        if (zones[i] != null) s_ActivePosBuf.Add(zones[i].position);
                for (int i = 0; i < tempZones.Count; i++)
                    s_ActivePosBuf.Add(tempZones[i].position);
            }
            return s_ActivePosBuf.Count == 0 ? System.Array.Empty<Vector3>() : s_ActivePosBuf.ToArray();
        }

        // 生成临时恐惧区(duration 秒后过期)。调用方负责确保 active 状态(ScaleDial 仅 active 时调)。
        public void SpawnTempZone(Vector3 pos, float zoneRadius, float duration)
        {
            tempZones.Add(new TempZone
            {
                position = pos,
                radius = zoneRadius,
                expireTime = Time.time + duration,
            });
        }

        void Update()
        {
            if (tempZones.Count == 0) return;
            float now = Time.time;
            for (int i = tempZones.Count - 1; i >= 0; i--)
                if (now >= tempZones[i].expireTime)
                    tempZones.RemoveAt(i);
        }

        // 合并常驻 zones(全局 radius)+ 临时 zones(各自 radius)。受 active 开关统一控制。
        // ThreatDetectionSystem 每帧调用,填两个列表(清空后追加)。
        public void GetActiveZones(List<Vector3> outPos, List<float> outRad)
        {
            outPos.Clear();
            outRad.Clear();
            if (!active) return;

            if (zones != null)
            {
                for (int i = 0; i < zones.Length; i++)
                {
                    if (zones[i] == null) continue;
                    outPos.Add(zones[i].position);
                    outRad.Add(radius);
                }
            }
            for (int i = 0; i < tempZones.Count; i++)
            {
                outPos.Add(tempZones[i].position);
                outRad.Add(tempZones[i].radius);
            }
        }

        public int TempZoneCount => tempZones.Count;

#if UNITY_EDITOR
        static readonly Color ThreatColor = new Color(0.9f, 0.2f, 0.2f, 0.25f);
        static readonly Color ThreatWire = new Color(0.9f, 0.2f, 0.2f, 0.9f);

        void OnDrawGizmos()
        {
            if (zones != null)
            {
                Gizmos.color = ThreatColor;
                foreach (var t in zones)
                {
                    if (t == null) continue;
                    Gizmos.DrawSphere(t.position, radius);
                }
                Gizmos.color = ThreatWire;
                foreach (var t in zones)
                {
                    if (t == null) continue;
                    Gizmos.DrawWireSphere(t.position, radius);
                }
            }
            // 临时恐惧区(Scene 视图调试用,Game 视图不可见)。
            for (int i = 0; i < tempZones.Count; i++)
            {
                Gizmos.color = ThreatColor;
                Gizmos.DrawSphere(tempZones[i].position, tempZones[i].radius);
                Gizmos.color = ThreatWire;
                Gizmos.DrawWireSphere(tempZones[i].position, tempZones[i].radius);
            }
        }
#endif
    }
}
