using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [NodeDescription("Wander", "wander to a random nearby point", "wander to a random nearby point", id: "b2c3d4e5f647a8b9c0d1e2f3a4b5c6d7")]
    public class WanderAction : Action
    {
        protected override Status OnStart()
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            GoalDecision.SetGoal(ca, GoalType.Wander, System.Array.Empty<Vector3>());
            return Status.Success;
        }

        protected override Status OnUpdate() => Status.Success;
    }
}
