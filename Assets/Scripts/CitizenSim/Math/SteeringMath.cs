using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim
{
    public static class SteeringMath
    {
        // 朝目标全速前进，不做减速。
        public static float3 Seek(float3 pos, float3 target, float speed)
        {
            float3 dir = math.normalizesafe(target - pos);
            return dir * speed;
        }

        // 朝目标前进，进入 slowRadius 后线性减速到 0。
        public static float3 Arrive(float3 pos, float3 target, float speed, float slowRadius)
        {
            float3 toTarget = target - pos;
            float dist = math.length(toTarget);
            if (dist < 1e-4f) return float3.zero;
            float3 dir = toTarget / dist;
            float v = speed;
            if (dist < slowRadius)
                v = speed * (dist / slowRadius);
            return dir * v;
        }

        // 全速远离 threatCenter。正对中心时给任意方向避免零向量卡死。
        public static float3 Evade(float3 pos, float3 threatCenter, float speed)
        {
            float3 away = pos - threatCenter;
            float d = math.length(away);
            if (d < 1e-4f) return new float3(speed, 0, 0);
            return (away / d) * speed;
        }

        // 单个邻居的排斥力贡献:方向背离邻居,1/d² 衰减,超过 avoidRadius 不计。
        // 近距 1/d² 会爆炸(密集抖动主因):钳制在 avoidRadius 边界处的力,近距不再增强。
        public static float3 RepulsionFrom(float3 pos, float3 neighbor, float avoidRadius)
        {
            float3 away = pos - neighbor;
            float d2 = math.lengthsq(away);
            float r2 = avoidRadius * avoidRadius;
            if (d2 > 1e-6f && d2 < r2)
            {
                // 原实现 away/(d*d) 在 d→0 时爆炸;钳制到边界力 1/r²。
                float force = math.min(1f / d2, 1f / r2);
                return away * force;
            }
            return float3.zero;
        }

        // 一组邻居的排斥力和(供单测;SteeringJob 内逐邻居调 RepulsionFrom 累加)。
        public static float3 Repulsion(float3 pos, NativeArray<float3> neighbors, float avoidRadius)
        {
            float3 sum = float3.zero;
            for (int i = 0; i < neighbors.Length; i++)
                sum += RepulsionFrom(pos, neighbors[i], avoidRadius);
            return sum;
        }

        // 沿流场方向走,接近目标(cost < slowCost)时线性减速到 0。
        // 不可达(cost=Inf,如被物理推到障碍物格子内) -> 查 4 邻域找最近可达格子方向"逃出"。
        // 越界 -> zero(市民停,由 outOfBounds 单独处理)。
        public static float3 FlowFieldArrive(float3 pos, int2 cell, in FlowField field, float speed, float slowCost)
        {
            if (!field.InBounds(cell)) return float3.zero;
            int ci = field.CellIndex(cell);
            float cost = field.costs[ci];

            // 当前格子不可达(障碍物内/未访问) -> 朝 4 邻域中 cost 最小的可达格子逃
            if (cost >= FlowFieldMath.Inf)
                return EscapeDirection(cell, field, speed);

            float3 dir = field.directions[ci];
            float v = cost < slowCost ? speed * (cost / slowCost) : speed;
            return dir * v;
        }

        // 市民被困在不可达格子(如被推到障碍物内)时,查 4 邻域找 cost 最小的可达格子,朝它走。
        // 没有可达邻居(四面被堵/越界) -> zero。Wander 前方 blocked 时也复用此方法绕开。
        public static float3 EscapeDirection(int2 cell, in FlowField field, float speed)
        {
            float bestCost = FlowFieldMath.Inf;
            float3 bestDir = float3.zero;
            float3 centerC = field.CellCenter(cell);
            TryEscape(cell + new int2(1, 0), field, centerC, speed, ref bestCost, ref bestDir);
            TryEscape(cell + new int2(-1, 0), field, centerC, speed, ref bestCost, ref bestDir);
            TryEscape(cell + new int2(0, 1), field, centerC, speed, ref bestCost, ref bestDir);
            TryEscape(cell + new int2(0, -1), field, centerC, speed, ref bestCost, ref bestDir);
            return bestDir;
        }

        static void TryEscape(int2 n, in FlowField field, float3 centerC, float speed, ref float bestCost, ref float3 bestDir)
        {
            if (!field.InBounds(n)) return;
            int ni = field.CellIndex(n);
            if (field.blocked[ni] == 1) return;
            float nc = field.costs[ni];
            if (nc < bestCost)
            {
                bestCost = nc;
                bestDir = math.normalizesafe(field.CellCenter(n) - centerC) * speed;
            }
        }

        // 移动障碍物排斥力:方向背离障碍物,越近越强(1 - d/r 线性衰减)。d >= r 不计。
        // 只读前 count 个(静态数组预分配,count 为实际移动障碍物数)。
        public static float3 ObstacleRepulsion(float3 pos, NativeArray<float3> obstacles, NativeArray<float> radii, int count, float strength)
        {
            float3 sum = float3.zero;
            for (int i = 0; i < count; i++)
            {
                float3 away = pos - obstacles[i];
                float d = math.length(away);
                float r = radii[i];
                if (d > 1e-4f && d < r)
                    sum += (away / d) * (1f - d / r) * strength;
            }
            return sum;
        }
    }
}
