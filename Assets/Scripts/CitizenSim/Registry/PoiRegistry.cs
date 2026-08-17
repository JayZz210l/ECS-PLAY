using UnityEngine;

namespace CitizenSim
{
    // 食物/家/娱乐点位置单例。
    public class PoiRegistry : MonoBehaviour
    {
        public static PoiRegistry Instance { get; private set; }

        public Transform[] foodPoints;
        public Transform[] homePoints;
        public Transform[] funPoints;

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        // OnEnable 在 EditMode 不触发;Bootstrap/测试显式注册用(强制覆盖,便于隔离)。
        public void Register() { Instance = this; }
        public static void Clear() { Instance = null; }

        public Vector3[] GetFoodPositions() => ToArray(foodPoints);
        public Vector3[] GetHomePositions() => ToArray(homePoints);
        public Vector3[] GetFunPositions() => ToArray(funPoints);

        static Vector3[] ToArray(Transform[] src)
        {
            if (src == null || src.Length == 0) return System.Array.Empty<Vector3>();
            var arr = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++)
                arr[i] = src[i] != null ? src[i].position : Vector3.zero;
            return arr;
        }

#if UNITY_EDITOR
        static readonly Color FoodColor = new Color(0.9f, 0.3f, 0.3f);
        static readonly Color HomeColor = new Color(0.2f, 0.4f, 0.9f);
        static readonly Color FunColor = new Color(0.9f, 0.8f, 0.2f);

        void OnDrawGizmos()
        {
            DrawPoiGizmos(foodPoints, FoodColor);
            DrawPoiGizmos(homePoints, HomeColor);
            DrawPoiGizmos(funPoints, FunColor);
        }

        static void DrawPoiGizmos(Transform[] pts, Color c)
        {
            if (pts == null) return;
            Gizmos.color = c;
            UnityEditor.Handles.color = c;
            foreach (var t in pts)
            {
                if (t == null) continue;
                Vector3 p = t.position;
                Gizmos.DrawWireSphere(p, NeedsDecaySystem.PoIRadius);
                UnityEditor.Handles.Label(p + Vector3.up * 2f, t.gameObject.name);
            }
        }
#endif
    }
}
