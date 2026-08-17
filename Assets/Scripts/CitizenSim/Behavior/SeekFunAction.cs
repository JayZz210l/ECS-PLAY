using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [NodeDescription("Seek Fun", "go to a fun point to play", "go play at a fun point", id: "e5f647a8b9c0d1e2f3a4b5c6d7e8f9a0")]
    public class SeekFunAction : Action
    {
        protected override Status OnStart()
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            var funs = PoiRegistry.Instance != null
                ? PoiRegistry.Instance.GetFunPositions()
                : System.Array.Empty<Vector3>();
            GoalDecision.SetGoal(ca, GoalType.SeekFun, funs);
            return Status.Success;
        }

        protected override Status OnUpdate() => Status.Success;
    }
}
