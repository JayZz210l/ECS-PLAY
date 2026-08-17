using UnityEngine;

namespace CitizenSim
{
    // 每个 citizen GameObject 挂一个。GO 是 source of truth：BT 写 currentGoal，
    // Snapshot 同步进 ECS；ECS 算完 needs 衰减后 Resolve 回写 needs。
    public class CitizenAuthoring : MonoBehaviour
    {
        public int Index;

        [Header("Needs (0..1)")]
        public Vector3 needs = new Vector3(0f, 0f, 0.5f); // x=hunger, y=fatigue, z=fun
        public float hungerThreshold = 0.7f;   // 超过此值开始觅食
        public float fullThreshold = 0.0f;     // 觅食中降到满(0)才算吃饱,恢复满再走

        [Header("Fatigue")]
        public float fatigueThreshold = 0.7f;  // 疲劳超过此值回家
        public float restedThreshold = 0.0f;   // 回家降到满(0)才算休息够,恢复满再走

        [Header("Fun")]
        public float boredThreshold = 0.3f;    // fun 低于此值无聊->找娱乐
        public float funFullThreshold = 0.9f;  // 娱乐升到满(0.9)才算玩够,恢复满再走

        [Header("Goal (BT 写, ECS 读)")]
        public GoalType currentGoalType = GoalType.Wander;
        public Vector3 currentGoalTarget = Vector3.zero;

        [Header("Threat (ECS 写, BT 读)")]
        [HideInInspector] public bool threatened;

        // 离开推力(M5 防拥堵):goal 从 Seek 切换时启动,短暂背离原 POI。
        [HideInInspector] public GoalType lastGoalType = GoalType.Wander;
        [HideInInspector] public Vector3 lastGoalTarget;
        [HideInInspector] public float exitTimer;
        [HideInInspector] public Vector3 exitDirection;

        [Header("Visuals")]
        [HideInInspector] public Renderer capsuleRenderer; // Bootstrap 注入
        [HideInInspector] public Animator animator;        // Bootstrap 注入(人形动画)
        [HideInInspector] public float moveSpeed;         // 当前水平速度(ResolveSystem 写,驱动动画)
    }
}
