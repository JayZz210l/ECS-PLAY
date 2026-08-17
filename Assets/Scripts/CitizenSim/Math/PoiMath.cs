using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim
{
    // 纯函数：POI 查询。可单测；NeedsDecaySystem 使用。
    public static class PoiMath
    {
        // 返回最近 POI 下标;空数组返回 -1。
        public static int NearestIndex(float3 pos, NativeArray<float3> points)
        {
            if (points.Length == 0) return -1;
            int best = 0;
            float bestD = math.distancesq(pos, points[0]);
            for (int i = 1; i < points.Length; i++)
            {
                float d = math.distancesq(pos, points[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // 是否在任意 POI 的 radius 内(距离平方比较,免开方)。
        public static bool WithinRadius(float3 pos, NativeArray<float3> points, float radius)
        {
            float r2 = radius * radius;
            for (int i = 0; i < points.Length; i++)
                if (math.distancesq(pos, points[i]) <= r2) return true;
            return false;
        }
    }
}
