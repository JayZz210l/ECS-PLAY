using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitizenSim
{
    [BurstCompile]
    public partial struct SteeringJob : IJobEntity
    {
        public float Speed;
        public float SlowRadius;     // Wander Arrive 减速半径
        public float SlowCost;       // 流场减速阈值(接近 POI N 格内减速)
        public float AvoidRadius;
        public float AvoidStrength;
        public float ObstacleStrength;  // 移动障碍物排斥强度
        public float ExitStrength;      // 离开推力(吃饱脱离 POI 人群)
        public float dt;            // 帧时长(速度阻尼用)
        public float SmoothTime;    // 速度低通滤波时间常数(抑制密集抖动)
        [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
        [ReadOnly] public NativeArray<float3> positions;
        [ReadOnly] public FlowField FoodField;
        [ReadOnly] public FlowField HomeField;
        [ReadOnly] public FlowField FunField;
        [ReadOnly] public NativeArray<float3> movingObstaclePos;  // 移动中障碍物位置(预分配)
        [ReadOnly] public NativeArray<float> movingObstacleRad;   // 移动中障碍物排斥半径
        public int movingObstacleCount;                            // 实际移动障碍物数

        void Execute(ref SimVelocity vel, in SimPosition pos, in SimGoal goal, in GridCell cell, in CitizenIndex idx, in SimExit exit)
        {
            // 流场格子坐标(用 FlowField.WorldToCell 转换,非 GridCell--后者是 SpatialGrid cellSize=1 的格子)。
            // 3 张流场配置相同(gridSize/cellSize/origin),用 FoodField 算一次即可。
            int2 flowCell = FoodField.WorldToCell(pos.Value);

            // Flee:全速远离威胁中心(不走流场,威胁系统已接走高频动态)。
            // SeekFood/Home/Fun:沿流场方向走(查对应流场格子 direction,绕开障碍)。
            // Wander:朝航点 Arrive(无固定 POI,不走流场)。
            float3 arrive;
            switch (goal.Type)
            {
                case GoalType.Flee:
                    arrive = SteeringMath.Evade(pos.Value, goal.Target, Speed);
                    break;
                case GoalType.SeekFood:
                    arrive = SteeringMath.FlowFieldArrive(pos.Value, flowCell, FoodField, Speed, SlowCost);
                    break;
                case GoalType.SeekHome:
                    arrive = SteeringMath.FlowFieldArrive(pos.Value, flowCell, HomeField, Speed, SlowCost);
                    break;
                case GoalType.SeekFun:
                    arrive = SteeringMath.FlowFieldArrive(pos.Value, flowCell, FunField, Speed, SlowCost);
                    break;
                default:  // Wander
                    // Wander 朝航点直线走,但前方有障碍物格子时用 escape 方向绕开(避免顶住)。
                    float3 toTarget = goal.Target - pos.Value;
                    float3 fwd = math.normalizesafe(toTarget);
                    int2 fwdCell = FoodField.WorldToCell(pos.Value + fwd * 1.5f);
                    if (FoodField.InBounds(fwdCell) && FoodField.blocked[FoodField.CellIndex(fwdCell)] == 1)
                        arrive = SteeringMath.EscapeDirection(flowCell, FoodField, Speed);
                    else
                        arrive = SteeringMath.Arrive(pos.Value, goal.Target, Speed, SlowRadius);
                    break;
            }

            // 9 邻域查网格,累加邻居排斥力(跳过自己)。
            float3 rep = float3.zero;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    int2 nc = cell.Value + new int2(dx, dz);
                    if (grid.TryGetFirstValue(nc, out int j, out var it))
                    {
                        do
                        {
                            if (j != idx.Value)
                                rep += SteeringMath.RepulsionFrom(pos.Value, positions[j], AvoidRadius);
                        } while (grid.TryGetNextValue(out j, ref it));
                    }
                }
            }

            float3 obRep = SteeringMath.ObstacleRepulsion(pos.Value, movingObstaclePos, movingObstacleRad, movingObstacleCount, ObstacleStrength);
            // 离开推力仅服务于日常状态。Flee 的威胁逃逸拥有绝对优先级，不能与旧 POI 推力混合。
            float3 exitForce = goal.Type != GoalType.Flee && exit.Timer > 0f
                ? exit.Direction * ExitStrength
                : float3.zero;
            float3 target = arrive + rep * AvoidStrength + obRep + exitForce;
            // 限速:合力超过 Speed 则归一化到 Speed。
            float speed = math.length(target);
            if (speed > Speed) target = math.normalizesafe(target) * Speed;
            // 速度低通滤波:旧速度向目标 lerp,抑制密集时高频振荡(抖动)。
            // k = 1 - exp(-dt/SmoothTime),SmoothTime=0 退化为直接赋值。
            float k = SmoothTime > 0f ? 1f - math.exp(-dt / SmoothTime) : 1f;
            vel.Value = math.lerp(vel.Value, target, k);
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct SteeringSystem : ISystem
    {
        // OnUpdate 不加 [BurstCompile]:需读 SpatialGridSystem/FlowFieldBuildSystem 的静态字段
        // (Burst 不支持非只读静态字段)。SteeringJob 本身仍是 Burst 编译。
        public void OnUpdate(ref SystemState state)
        {
            // SpatialGridSystem.OnUpdate 已 Complete,grid/positions 可安全读。
            // FlowFieldBuildSystem.OnUpdate 已重算(Dirty 时),3 张流场可安全读。
            state.Dependency = new SteeringJob
            {
                Speed = 2f,
                SlowRadius = 0.5f,
                SlowCost = 3f,        // 接近 POI 3 格内开始减速
                AvoidRadius = 0.5f,   // 避让半径 = 市民视觉半径(视觉 scale 已缩半,避免隔空碰撞)
                AvoidStrength = 1.5f,
                ObstacleStrength = 3f,
                ExitStrength = 4f,    // 略强于障碍排斥,确保能冲出 POI 人群
                dt = SystemAPI.Time.DeltaTime,
                SmoothTime = 0.12f,   // 速度平滑时间(抑制密集抖动)
                grid = SpatialGridSystem.Grid,
                positions = SpatialGridSystem.Positions,
                FoodField = FlowFieldBuildSystem.FoodField,
                HomeField = FlowFieldBuildSystem.HomeField,
                FunField = FlowFieldBuildSystem.FunField,
                movingObstaclePos = ObstacleRegistry.MovingObstaclePos,
                movingObstacleRad = ObstacleRegistry.MovingObstacleRad,
                movingObstacleCount = ObstacleRegistry.MovingObstacleCount,
            }.ScheduleParallel(state.Dependency);
        }
    }
}
