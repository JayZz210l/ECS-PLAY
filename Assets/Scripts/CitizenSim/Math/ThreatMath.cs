using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim
{
    // 威胁检测数学(纯函数,脱离 ECS 可单测)。ThreatDetectionSystem 的 Burst job 调用此处。
    public static class ThreatMath
    {
        // pos 是否在任一威胁区内(距任一威胁中心 < radius)。空 zones 或 radius<=0 返回 false。
        public static bool IsThreatened(float3 pos, NativeArray<float3> zones, float radius)
        {
            if (radius <= 0f || zones.Length == 0) return false;
            float r2 = radius * radius;
            for (int i = 0; i < zones.Length; i++)
                if (math.lengthsq(pos - zones[i]) < r2) return true;
            return false;
        }

        // 每区域独立半径版本:pos 是否在任一威胁区内(距 zones[i] < radii[i])。用于临时恐惧区(15m)与常驻区(5m)共存。
        public static bool IsThreatened(float3 pos, NativeArray<float3> zones, NativeArray<float> radii)
            => IsThreatened(pos, zones, radii, 1f);

        // 带滞回因子版本:factor>1 扩大判定半径,用于"已 threatened 时用更宽容的退出阈值"防边界抖动。
        public static bool IsThreatened(float3 pos, NativeArray<float3> zones, NativeArray<float> radii, float factor)
        {
            if (zones.Length == 0) return false;
            for (int i = 0; i < zones.Length; i++)
            {
                float r = radii[i] * factor;
                if (r <= 0f) continue;
                if (math.lengthsq(pos - zones[i]) < r * r) return true;
            }
            return false;
        }
    }
}
