using Unity.Entities;
using Unity.Mathematics;

namespace CitizenSim
{
    public enum GoalType
    {
        Wander = 0,
        SeekFood = 1,
        SeekHome = 2,
        SeekFun = 3,
        Flee = 4
    }

    public struct SimPosition : IComponentData { public float3 Value; }
    public struct SimVelocity : IComponentData { public float3 Value; }
    public struct SimGoal : IComponentData { public GoalType Type; public float3 Target; }
    public struct SimRadius : IComponentData { public float Value; }
    public struct CitizenIndex : IComponentData { public int Value; }
    // 空间哈希网格格子坐标(SpatialGridSystem 每帧写,SteeringSystem 读)。ECS 中间量,不回写 GO。
    public struct GridCell : IComponentData { public int2 Value; }

    // 威胁标记(enableable,M4)。ThreatDetectionSystem 每帧 toggle bit,零 archetype 变更。
    // 默认 disabled;ResolveSystem 把 bit 镜像回 ca.threatened 供 BT 读。
    public struct Threatened : IComponentData, IEnableableComponent { }

    // x=hunger, y=fatigue, z=fun，均 0..1。M2 只消费 hunger；fatigue/fun 在 M3。
    public struct SimNeeds : IComponentData { public float3 Value; }

    // 离开推力(M5 防拥堵):goal 从 Seek 切换(吃饱离开)时,短暂(1s)朝背离原 POI 方向推,
    // 冲出 POI 周围人群。SnapshotSystem 检测切换时写,ResolveSystem 递减 Timer。
    public struct SimExit : IComponentData { public float Timer; public float3 Direction; }
}
