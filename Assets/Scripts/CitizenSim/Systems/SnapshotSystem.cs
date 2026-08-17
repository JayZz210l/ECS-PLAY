using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(SteeringSystem))]
    public partial class SnapshotSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var registry = CitizenRegistry.Instance;
            if (registry == null) return;

            var em = EntityManager;
            var gos = registry.GameObjects;
            var ents = registry.Entities;
            var authoring = registry.Authoring;
            for (int i = 0; i < gos.Length; i++)
            {
                var go = gos[i];
                if (go == null) continue;
                em.SetComponentData(ents[i], new SimPosition { Value = go.transform.position });
                var ca = authoring != null ? authoring[i] : null;
                if (ca != null)
                {
                    // 检测 goal 切换:从 Seek 切到其他(吃饱离开)时启动 exit 推力,短暂背离原 POI 冲出人群。
                    if (ca.currentGoalType != ca.lastGoalType)
                    {
                        bool wasSeek = ca.lastGoalType == GoalType.SeekFood
                                    || ca.lastGoalType == GoalType.SeekHome
                                    || ca.lastGoalType == GoalType.SeekFun;
                        // Flee 必须无条件抢占日常移动。若保留离开 POI 的推力，旧 POI 方向会在
                        // 约 1 秒内盖过逃跑向量，表现为刚受威胁时先朝随机方向拐一下。
                        if (ca.currentGoalType == GoalType.Flee)
                        {
                            ca.exitTimer = 0f;
                            ca.exitDirection = Vector3.zero;
                        }
                        else if (wasSeek)
                        {
                            ca.exitTimer = 1f;
                            // 使用切换前保存的目标；currentGoalTarget 此时已经是新目标。
                            Vector3 away = go.transform.position - ca.lastGoalTarget;
                            ca.exitDirection = away.sqrMagnitude > 1e-4f
                                ? away.normalized
                                : UnityEngine.Random.onUnitSphere;
                        }
                        ca.lastGoalType = ca.currentGoalType;
                    }
                    // 每帧保存当前目标，供下一次 goal 类型切换时计算离开旧 POI 的方向。
                    ca.lastGoalTarget = ca.currentGoalTarget;
                    em.SetComponentData(ents[i], new SimExit { Timer = ca.exitTimer, Direction = ca.exitDirection });
                    em.SetComponentData(ents[i], new SimNeeds { Value = ca.needs });
                    em.SetComponentData(ents[i], new SimGoal
                    {
                        Type = ca.currentGoalType,
                        Target = ca.currentGoalTarget
                    });
                }
            }
        }
    }
}
