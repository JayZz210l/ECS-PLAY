using Unity.Behavior;
using Unity.Properties;

namespace CitizenSim
{
    [System.Serializable]
    [GeneratePropertyBag]
    [Condition("IsThreatened", "checks if the citizen is threatened", "the agent is threatened", id: "11223344556677889900aabbccddeeff")]
    public class IsThreatenedCondition : Condition
    {
        public override bool IsTrue()
            => GoalDecision.IsThreatened(GameObject.GetComponent<CitizenAuthoring>());
    }
}
