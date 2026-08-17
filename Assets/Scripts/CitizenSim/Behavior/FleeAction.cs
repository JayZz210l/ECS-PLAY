using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

namespace CitizenSim
{
    // Flee:设目标为最近威胁中心(SteeringSystem 对 Flee 用 Evade 远离之)。
    // 依赖 BtScheduler 插队每帧重设,以跟踪移动威胁。
    [System.Serializable]
    [GeneratePropertyBag]
    [NodeDescription("Flee", "flee from the nearest threat zone", "flee from threat", id: "ffeeddccbbaa00998877665544332211")]
    public class FleeAction : Action
    {
        protected override Status OnStart()
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            var zones = ThreatZoneRegistry.Instance != null
                ? ThreatZoneRegistry.Instance.GetActiveZonePositions()
                : System.Array.Empty<Vector3>();
            GoalDecision.SetGoal(ca, GoalType.Flee, zones);
            return Status.Success;
        }

        protected override Status OnUpdate() => Status.Success;
    }
}
