using Unity.Behavior;
using Unity.Properties;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [Condition("IsHungry", "checks if the citizen is hungry", "the agent is hungry", id: "c3d4e5f647a8b9c0d1e2f3a4b5c6d7e8")]
    public class IsHungryCondition : Condition
    {
        public override bool IsTrue()
            => GoalDecision.IsHungry(GameObject.GetComponent<CitizenAuthoring>());
    }
}
