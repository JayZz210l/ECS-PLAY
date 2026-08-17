using System;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim
{
    // 流场:固定网格,每格存方向(float3)和代价(float)。
    // 格子坐标 int2(x, z),x 映射世界 x 轴,z 映射世界 z 轴(y 在流场里不用,市民在平面)。
    // 网格原点:格子 (0,0) 的角点对应世界 origin。格子中心 = origin + (cell + 0.5) * cellSize。
    public struct FlowField : IDisposable
    {
        public int2 gridSize;          // (x 方向格子数, z 方向格子数)
        public float cellSize;
        public float3 origin;          // 格子 (0,0) 角点的世界坐标
        public NativeArray<float3> directions;  // 每格方向(normalize),指向离目标更近的邻居
        public NativeArray<float> costs;        // 每格代价,Inf=未访问/不可达/障碍
        public NativeArray<byte> blocked;       // 0=可走,1=障碍物

        public int CellCount => gridSize.x * gridSize.y;

        public int2 WorldToCell(float3 pos)
            => new int2(
                (int)math.floor((pos.x - origin.x) / cellSize),
                (int)math.floor((pos.z - origin.z) / cellSize));

        public float3 CellCenter(int2 cell)
            => origin + new float3((cell.x + 0.5f) * cellSize, 0f, (cell.y + 0.5f) * cellSize);

        public bool InBounds(int2 cell)
            => cell.x >= 0 && cell.x < gridSize.x && cell.y >= 0 && cell.y < gridSize.y;

        public int CellIndex(int2 cell) => cell.x + cell.y * gridSize.x;

        public void Dispose()
        {
            if (directions.IsCreated) directions.Dispose();
            if (costs.IsCreated) costs.Dispose();
            if (blocked.IsCreated) blocked.Dispose();
        }
    }

    // 流场生成/查询纯函数。可单测;FlowFieldBuildSystem 调用。
    public static class FlowFieldMath
    {
        public const float Inf = 1e9f;

        // 8 邻域偏移(4 正交 + 4 对角线)。正交步 cost=1,对角步 cost=√2(对角线更长)。
        // 对角线支持市民斜向走(路径可切角,而非纯 L 型正交折线)。
        static readonly int2[] k_Neighbors =
        {
            new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1),
            new int2(1, 1), new int2(1, -1), new int2(-1, 1), new int2(-1, -1),
        };
        static readonly float k_DiagCost = 1.41421356f;  // √2

        static float StepCost(int2 offset)
            => (offset.x != 0 && offset.y != 0) ? k_DiagCost : 1f;

        // 斜向邻居 n(cell 的对角)可否通行:两个相邻正交格都非 blocked,否则会穿墙角。
        static bool CanDiagonal(in FlowField field, int2 cell, int2 n)
        {
            int dx = n.x - cell.x, dz = n.y - cell.y;
            int2 a = new int2(cell.x + dx, cell.y);
            int2 b = new int2(cell.x, cell.y + dz);
            return IsWalkable(field, a) && IsWalkable(field, b);
        }

        static bool IsWalkable(in FlowField field, int2 c)
            => field.InBounds(c) && field.blocked[field.CellIndex(c)] != 1;

        // 单目标 BFS:从 targetCell 反向扩散,填 directions/costs。
        // blocked 格子跳过(不可通过)。目标被堵 -> 全场 Inf。
        // density(可选):每格拥堵度,进入拥堵格加额外成本(绕路)。null=无拥堵。
        public static void BuildSingleTarget(ref FlowField field, int2 targetCell,
            NativeArray<float> density = default, float congestionStrength = 0f, float maxDensity = 8f)
        {
            for (int i = 0; i < field.CellCount; i++)
            {
                field.costs[i] = Inf;
                field.directions[i] = float3.zero;
            }

            if (!field.InBounds(targetCell)) return;
            int targetIdx = field.CellIndex(targetCell);
            if (field.blocked[targetIdx] == 1) return;

            var queue = new NativeQueue<int2>(Allocator.Temp);
            field.costs[targetIdx] = 0f;
            queue.Enqueue(targetCell);

            bool hasDensity = density.IsCreated;
            while (queue.TryDequeue(out var c))
            {
                float costC = field.costs[field.CellIndex(c)];
                float3 centerC = field.CellCenter(c);
                for (int i = 0; i < k_Neighbors.Length; i++)
                {
                    int2 off = k_Neighbors[i];
                    int2 n = c + off;
                    if (off.x != 0 && off.y != 0 && !CanDiagonal(field, c, n)) continue;
                    float cong = 0f;
                    if (hasDensity && field.InBounds(n))
                        cong = math.min(density[field.CellIndex(n)] / maxDensity, 1f) * congestionStrength;
                    TryRelax(ref field, n, costC, StepCost(off) + cong, centerC, queue);
                }
            }
            queue.Dispose();
        }

        // 多源 BFS:所有 sources 同时入队(多目标 POI),算出每格到最近源的 direction。
        // 这正是"找最近 POI"的流场解法:成本与单目标一致,天然选最近。
        // density(可选):每格拥堵度,进入拥堵格加额外成本(绕路)。null=无拥堵。
        public static void BuildMultiSource(ref FlowField field, NativeList<int2> sources,
            NativeArray<float> density = default, float congestionStrength = 0f, float maxDensity = 8f)
        {
            for (int i = 0; i < field.CellCount; i++)
            {
                field.costs[i] = Inf;
                field.directions[i] = float3.zero;
            }

            var queue = new NativeQueue<int2>(Allocator.Temp);
            for (int i = 0; i < sources.Length; i++)
            {
                int2 s = sources[i];
                if (!field.InBounds(s)) continue;
                int si = field.CellIndex(s);
                if (field.blocked[si] == 1) continue;
                field.costs[si] = 0f;
                queue.Enqueue(s);
            }

            bool hasDensity = density.IsCreated;
            while (queue.TryDequeue(out var c))
            {
                float costC = field.costs[field.CellIndex(c)];
                float3 centerC = field.CellCenter(c);
                for (int i = 0; i < k_Neighbors.Length; i++)
                {
                    int2 off = k_Neighbors[i];
                    int2 n = c + off;
                    if (off.x != 0 && off.y != 0 && !CanDiagonal(field, c, n)) continue;
                    float cong = 0f;
                    if (hasDensity && field.InBounds(n))
                        cong = math.min(density[field.CellIndex(n)] / maxDensity, 1f) * congestionStrength;
                    TryRelax(ref field, n, costC, StepCost(off) + cong, centerC, queue);
                }
            }
            queue.Dispose();
        }

        // 局部重算:changedCells 周围 radius 格内置 INF,从区域边界(区域外 cost 已知格子)重新 BFS 扩散。
        // 只更新区域内格子(区域外不变)。blocked 已由 WriteBlocked 更新,这里只重算 cost/direction。
        // 用于动态障碍物静止后的增量更新(比全量重算便宜)。
        // density(可选):每格拥堵度,进入拥堵格加额外成本(绕路)。null=无拥堵。
        public static void RebuildRegion(ref FlowField field, NativeList<int2> changedCells, int radius,
            NativeArray<float> density = default, float congestionStrength = 0f, float maxDensity = 8f)
        {
            // 1. 收集影响区域(changedCells 周围 radius 格)
            var region = new NativeHashSet<int2>(256, Allocator.Temp);
            for (int i = 0; i < changedCells.Length; i++)
            {
                int2 cc = changedCells[i];
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int2 n = cc + new int2(dx, dz);
                        if (field.InBounds(n)) region.Add(n);
                    }
                }
            }

            // 2. 区域内置 INF + zero(blocked 已由 WriteBlocked 更新)
            foreach (var c in region)
            {
                int ci = field.CellIndex(c);
                field.costs[ci] = Inf;
                field.directions[ci] = float3.zero;
            }

            // 3. 收集边界源:区域外紧邻区域、cost<Inf、非障碍的格子,作为多源 BFS 源
            var queue = new NativeQueue<int2>(Allocator.Temp);
            var enqueued = new NativeHashSet<int2>(64, Allocator.Temp);
            foreach (var c in region)
            {
                EnqueueBoundary(ref field, c + new int2(1, 0), region, queue, enqueued);
                EnqueueBoundary(ref field, c + new int2(-1, 0), region, queue, enqueued);
                EnqueueBoundary(ref field, c + new int2(0, 1), region, queue, enqueued);
                EnqueueBoundary(ref field, c + new int2(0, -1), region, queue, enqueued);
            }

            // 4. BFS 扩散进区域内(区域外格子不更新)
            bool hasDensity = density.IsCreated;
            while (queue.TryDequeue(out var c))
            {
                float costC = field.costs[field.CellIndex(c)];
                float3 centerC = field.CellCenter(c);
                for (int i = 0; i < k_Neighbors.Length; i++)
                {
                    int2 off = k_Neighbors[i];
                    int2 n = c + off;
                    if (off.x != 0 && off.y != 0 && !CanDiagonal(field, c, n)) continue;
                    float cong = 0f;
                    if (hasDensity && field.InBounds(n))
                        cong = math.min(density[field.CellIndex(n)] / maxDensity, 1f) * congestionStrength;
                    RelaxInRegion(ref field, n, costC, StepCost(off) + cong, centerC, queue, region);
                }
            }

            queue.Dispose();
            enqueued.Dispose();
            region.Dispose();
        }

        // 边界源入队:区域外、cost<Inf、非障碍、未入队的格子。
        static void EnqueueBoundary(ref FlowField field, int2 n, NativeHashSet<int2> region, NativeQueue<int2> queue, NativeHashSet<int2> enqueued)
        {
            if (!field.InBounds(n)) return;
            if (region.Contains(n)) return;
            if (enqueued.Contains(n)) return;
            int ni = field.CellIndex(n);
            if (field.blocked[ni] == 1) return;
            if (field.costs[ni] >= Inf) return;
            enqueued.Add(n);
            queue.Enqueue(n);
        }

        // 区域内松弛:只更新 region 内的格子(区域外不变)。
        static void RelaxInRegion(ref FlowField field, int2 n, float parentCost, float stepCost, float3 parentCenter, NativeQueue<int2> queue, NativeHashSet<int2> region)
        {
            if (!field.InBounds(n)) return;
            if (!region.Contains(n)) return;
            int ni = field.CellIndex(n);
            if (field.blocked[ni] == 1) return;
            float newCost = parentCost + stepCost;
            if (newCost < field.costs[ni])
            {
                field.costs[ni] = newCost;
                field.directions[ni] = math.normalizesafe(parentCenter - field.CellCenter(n));
                queue.Enqueue(n);
            }
        }

        // BFS 松弛:邻居 n 若非障碍且 newCost < 当前 cost,更新 cost + direction(指向 parent),入队。
        static void TryRelax(ref FlowField field, int2 n, float parentCost, float stepCost, float3 parentCenter, NativeQueue<int2> queue)
        {
            if (!field.InBounds(n)) return;
            int ni = field.CellIndex(n);
            if (field.blocked[ni] == 1) return;
            float newCost = parentCost + stepCost;
            if (newCost < field.costs[ni])
            {
                field.costs[ni] = newCost;
                field.directions[ni] = math.normalizesafe(parentCenter - field.CellCenter(n));
                queue.Enqueue(n);
            }
        }
    }
}
