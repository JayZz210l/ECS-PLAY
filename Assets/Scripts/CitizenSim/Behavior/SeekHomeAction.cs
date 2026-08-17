using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [NodeDescription("Seek Home", "go home to rest", "go home to rest", id: "d4e5f647a8b9c0d1e2f3a4b5c6d7e8f9")]
    public class SeekHomeAction : Action
    {
        protected override Status OnStart()
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            var homes = PoiRegistry.Instance != null
                ? PoiRegistry.Instance.GetHomePositions()
                : System.Array.Empty<Vector3>();
            GoalDecision.SetGoal(ca, GoalType.SeekHome, homes);
            return Status.Success;
        }

        protected override Status OnUpdate() => Status.Success;
    }
}
