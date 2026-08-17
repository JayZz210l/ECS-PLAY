using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 流场构建系统:3 张多源流场(食物/家/娱乐),全局静态供 SteeringSystem 读。
    // POI 注册时生成,障碍物变更时标记 Dirty 重算(M5 Task 4 接入)。
    // 网格大小/原点由 FlowFieldConfig 配置(默认 40x40 格 × 2m = 80m x 80m,origin 跟随地面中心)。
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SnapshotSystem))]
    [UpdateBefore(typeof(SteeringSystem))]
    public partial struct FlowFieldBuildSystem : ISystem
    {
        public static FlowField FoodField;
        public static FlowField HomeField;
        public static FlowField FunField;
        public static bool Dirty;       // 障碍物变更标记,触发重算
        public static bool Initialized; // 首次生成标记
        public static NativeArray<float> Density;  // 每流场格拥堵度(市民数),流场 BFS 绕路用

        // 拥堵绕路参数
        public const float CongestionStrength = 3f;  // 拥堵格额外步进成本(中等绕路)
        public const float MaxDensity = 8f;          // 每格多少市民算饱和(线性 clamp)
        const float k_DensityInterval = 0.25f;       // 密度更新间隔(秒)

        // 网格配置(运行时从 FlowFieldConfig.Instance 读取,origin 跟随地面中心)。
        public static int2 GridSize => cfg != null ? cfg.GridSize : new int2(40, 40);
        public static float CellSize => cfg != null ? cfg.cellSize : 2f;
        public static float3 Origin => cfg != null ? cfg.Origin : new float3(-40f, 0f, -40f);
        static FlowFieldConfig cfg => FlowFieldConfig.Instance;

        public void OnCreate(ref SystemState state)
        {
            // 分配延迟到 OnUpdate 首次运行:origin 依赖 FlowFieldConfig.Instance,
            // 该单例只有运行时才存在(Edit 模式为 null)。
            Dirty = true;
            Initialized = false;
        }

        public void OnDestroy(ref SystemState state)
        {
            if (FoodField.directions.IsCreated) FoodField.Dispose();
            if (HomeField.directions.IsCreated) HomeField.Dispose();
            if (FunField.directions.IsCreated) FunField.Dispose();
            if (Density.IsCreated) Density.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 首次运行分配流场(需运行时单例读 origin/gridSize)。
            if (!Initialized)
            {
                AllocateField(ref FoodField);
                AllocateField(ref HomeField);
                AllocateField(ref FunField);
                if (!Density.IsCreated) Density = new NativeArray<float>(GridSize.x * GridSize.y, Allocator.Persistent);
                Dirty = true;
            }

            // 拥堵是动态的:每 k_DensityInterval 更新密度并重建流场(让市民绕开新拥堵)。
            _densityTimer += SystemAPI.Time.DeltaTime;
            if (_densityTimer >= k_DensityInterval)
            {
                _densityTimer = 0f;
                ComputeDensity();
                Dirty = true;
            }

            if (!Dirty) return;
            RebuildAll();
        }

        float _densityTimer;

        // 全量重算 3 张流场(读 PoiRegistry + 障碍物 blocked)。
        public static void RebuildAll()
        {
            var poi = PoiRegistry.Instance;
            WriteObstacles(ref FoodField);
            WriteObstacles(ref HomeField);
            WriteObstacles(ref FunField);
            RebuildField(ref FoodField, poi != null ? poi.GetFoodPositions() : null);
            RebuildField(ref HomeField, poi != null ? poi.GetHomePositions() : null);
            RebuildField(ref FunField,  poi != null ? poi.GetFunPositions()  : null);
            Dirty = false;
            Initialized = true;
        }

        // 局部重算 3 张流场(障碍物移动后增量更新)。只重算 changedCells 周围 radius 格。
        public static void RebuildRegions(NativeList<int2> changedCells, int radius)
        {
            WriteObstacles(ref FoodField);
            WriteObstacles(ref HomeField);
            WriteObstacles(ref FunField);
            FlowFieldMath.RebuildRegion(ref FoodField, changedCells, radius, Density, CongestionStrength, MaxDensity);
            FlowFieldMath.RebuildRegion(ref HomeField, changedCells, radius, Density, CongestionStrength, MaxDensity);
            FlowFieldMath.RebuildRegion(ref FunField, changedCells, radius, Density, CongestionStrength, MaxDensity);
        }

        // 世界坐标是否在流场网格内(供 spawn 检查,避免生成在网格外卡死)。
        public static bool IsWorldInBounds(Vector3 pos)
        {
            float3 min = Origin;
            float3 max = Origin + new float3(GridSize.x * CellSize, 0f, GridSize.y * CellSize);
            return pos.x >= min.x && pos.x <= max.x && pos.z >= min.z && pos.z <= max.z;
        }

        static void AllocateField(ref FlowField field)
        {
            int2 size = GridSize;
            field = new FlowField
            {
                gridSize = size,
                cellSize = CellSize,
                origin = Origin,
                directions = new NativeArray<float3>(size.x * size.y, Allocator.Persistent),
                costs = new NativeArray<float>(size.x * size.y, Allocator.Persistent),
                blocked = new NativeArray<byte>(size.x * size.y, Allocator.Persistent),
            };
        }

        static void RebuildField(ref FlowField field, Vector3[] poiPositions)
        {
            var sources = new NativeList<int2>(Allocator.Temp);
            if (poiPositions != null)
            {
                for (int i = 0; i < poiPositions.Length; i++)
                {
                    int2 cell = field.WorldToCell(poiPositions[i]);
                    if (field.InBounds(cell)) sources.Add(cell);
                }
            }
            FlowFieldMath.BuildMultiSource(ref field, sources, Density, CongestionStrength, MaxDensity);
            sources.Dispose();
        }

        // 统计每流场格的市民数(拥堵度)。主线程 O(N),0.25s 一次。
        // 市民位置从 CitizenRegistry.GameObjects 读(和 SpatialGrid 同源)。
        static void ComputeDensity()
        {
            if (!Density.IsCreated) return;
            for (int i = 0; i < Density.Length; i++) Density[i] = 0f;
            var reg = CitizenRegistry.Instance;
            if (reg == null || reg.GameObjects == null) return;
            var field = FoodField;
            if (!field.directions.IsCreated) return;
            for (int i = 0; i < reg.GameObjects.Length; i++)
            {
                var go = reg.GameObjects[i];
                if (go == null) continue;
                int2 cell = field.WorldToCell(go.transform.position);
                if (field.InBounds(cell)) Density[field.CellIndex(cell)] += 1f;
            }
        }

        // 障碍物层写 blocked。无 ObstacleRegistry 时清零(开放场景)。
        static void WriteObstacles(ref FlowField field)
        {
            var obs = ObstacleRegistry.Instance;
            if (obs != null) obs.WriteBlocked(ref field);
            else { for (int i = 0; i < field.CellCount; i++) field.blocked[i] = 0; }
        }
    }
}
