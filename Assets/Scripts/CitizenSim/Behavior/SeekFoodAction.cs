using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [NodeDescription("Seek Food", "move toward the nearest food point", "seek the nearest food", id: "a1b2c3d4e5f6478a9b0c1d2e3f4a5b6c")]
    public class SeekFoodAction : Action
    {
        protected override Status OnStart()
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            var foods = PoiRegistry.Instance != null
                ? PoiRegistry.Instance.GetFoodPositions()
                : System.Array.Empty<Vector3>();
            GoalDecision.SetGoal(ca, GoalType.SeekFood, foods);
            return Status.Success;
        }

        protected override Status OnUpdate() => Status.Success;
    }
}
