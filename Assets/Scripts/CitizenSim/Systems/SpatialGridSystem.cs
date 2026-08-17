using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 空间哈希网格:每帧把全体 SimPosition 装入固定格子哈希(cell -> citizenIndex),
    // 并缓存 positions 数组(by citizenIndex)。SteeringSystem 查 9 邻域做避障。
    // 规格 §5 第 3 段。ECS 中间量,不回写 GO。
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SnapshotSystem))]
    [UpdateBefore(typeof(SteeringSystem))]
    public partial struct SpatialGridSystem : ISystem
    {
        public static NativeParallelMultiHashMap<int2, int> Grid;  // cell -> citizenIndex
        public static NativeArray<float3> Positions;               // by citizenIndex
        public static int Count;
        public const float CellSize = 1.0f;                        // ~避障半径×2

        EntityQuery m_Query;

        public void OnCreate(ref SystemState state)
        {
            m_Query = state.GetEntityQuery(
                ComponentType.ReadOnly<SimPosition>(),
                ComponentType.ReadOnly<CitizenIndex>(),
                ComponentType.ReadOnly<GridCell>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (Grid.IsCreated) Grid.Dispose();
            if (Positions.IsCreated) Positions.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            int count = m_Query.CalculateEntityCount();

            if (!Grid.IsCreated || count != Count)
            {
                if (Grid.IsCreated) Grid.Dispose();
                if (Positions.IsCreated) Positions.Dispose();
                Grid = new NativeParallelMultiHashMap<int2, int>(math.max(1, count), Allocator.Persistent);
                Positions = new NativeArray<float3>(math.max(1, count), Allocator.Persistent);
                Count = count;
            }

            Grid.Clear();
            state.Dependency = new BuildGridJob
            {
                cellSize = CellSize,
                positions = Positions,
                grid = Grid.AsParallelWriter(),
            }.ScheduleParallel(m_Query, state.Dependency);
            // Complete 保证 SteeringSystem 读到完整网格(500 规模构建极快)。
            // M4 5000 规模若成瓶颈,改跨系统依赖链不 Complete。
            state.Dependency.Complete();
        }
    }

    [BurstCompile]
    public partial struct BuildGridJob : IJobEntity
    {
        public float cellSize;
        // CitizenIndex 与 job 迭代下标不一致,需禁用并行写索引安全检查(每 entity 写自己的唯一 idx)。
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float3> positions;
        [WriteOnly] public NativeParallelMultiHashMap<int2, int>.ParallelWriter grid;

        void Execute(in SimPosition pos, in CitizenIndex idx, ref GridCell cell)
        {
            float3 p = pos.Value;
            int2 c = new int2((int)math.floor(p.x / cellSize), (int)math.floor(p.z / cellSize));
            cell.Value = c;
            positions[idx.Value] = p;
            grid.Add(c, idx.Value);
        }
    }
}
