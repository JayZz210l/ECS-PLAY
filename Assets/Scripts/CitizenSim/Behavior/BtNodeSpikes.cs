using Unity.Behavior;
using Unity.Properties;
using UnityEngine;

namespace CitizenSim
{
    // Spike 节点：验证 Unity Behavior 1.0.16 自定义节点作者模式。
    // 结论见 docs/superpowers/notes/2026-07-16-m2-bt-spike.md。
    // 同时作为 Task 5 真实节点（IsHungry/SetGoal）的编译模板。
    [System.Serializable]
    [GeneratePropertyBag]
    [NodeDescription("SpikeLog", "spike: log once then succeed", "the agent logs a message")]
    public class SpikeLogAction : Action
    {
        public string Message = "spike";

        protected override Status OnStart()
        {
            Debug.Log($"[SpikeLog] {Message} on {GameObject?.name}");
            return Status.Success;
        }

        protected override Status OnUpdate() => Status.Success;
    }

    [System.Serializable]
    [GeneratePropertyBag]
    [Condition("SpikeAlwaysTrue", "spike: always true", "always true")]
    public class SpikeAlwaysTrueCondition : Condition
    {
        public override bool IsTrue() => true;
    }
}
