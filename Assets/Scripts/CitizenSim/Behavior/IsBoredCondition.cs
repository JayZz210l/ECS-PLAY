using Unity.Behavior;
using Unity.Properties;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [Condition("IsBored", "checks if the citizen is bored (low fun)", "the agent is bored", id: "47a8b9c0d1e2f3a4b5c6d7e8f9a1b2c3")]
    public class IsBoredCondition : Condition
    {
        public override bool IsTrue()
            => GoalDecision.IsBored(GameObject.GetComponent<CitizenAuthoring>());
    }
}
