using Unity.Behavior;
using Unity.Properties;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [Condition("IsFatigued", "checks if the citizen is fatigued", "the agent is fatigued", id: "f647a8b9c0d1e2f3a4b5c6d7e8f9a1b2")]
    public class IsFatiguedCondition : Condition
    {
        public override bool IsTrue()
            => GoalDecision.IsFatigued(GameObject.GetComponent<CitizenAuthoring>());
    }
}
