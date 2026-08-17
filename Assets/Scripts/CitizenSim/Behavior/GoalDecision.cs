using UnityEngine;

namespace CitizenSim
{
    // BT 决策的纯函数逻辑，脱离 Behavior 运行时可单测。
    // IsHungryCondition / Is*Action 薄封装调用这里。
    public static class GoalDecision
    {
        // 滞回(hysteresis)防阈值抖动:已在 SeekFood 时吃到 fullThreshold 才停;
        // 否则 hunger > hungerThreshold 触发觅食。
        public static bool IsHungry(CitizenAuthoring ca)
        {
            if (ca == null) return false;
            if (ca.currentGoalType == GoalType.SeekFood)
                return ca.needs.x > ca.fullThreshold;
            return ca.needs.x > ca.hungerThreshold;
        }

        // 滞回:SeekHome 时休息到 restedThreshold 才停;否则 fatigue > fatigueThreshold 触发回家。
        public static bool IsFatigued(CitizenAuthoring ca)
        {
            if (ca == null) return false;
            if (ca.currentGoalType == GoalType.SeekHome)
                return ca.needs.y > ca.restedThreshold;
            return ca.needs.y > ca.fatigueThreshold;
        }

        // 滞回:SeekFun 时玩到 funFullThreshold 才停;否则 fun < boredThreshold 触发找娱乐。
        public static bool IsBored(CitizenAuthoring ca)
        {
            if (ca == null) return false;
            if (ca.currentGoalType == GoalType.SeekFun)
                return ca.needs.z < ca.funFullThreshold;
            return ca.needs.z < ca.boredThreshold;
        }

        // 威胁标记(无滞回):ECS ThreatDetectionSystem 每帧写 ca.threatened,BT 读。
        // BtScheduler 插队保证受威胁时当帧反应(规格§5 威胁反应性)。
        public static bool IsThreatened(CitizenAuthoring ca)
            => ca != null && ca.threatened;

        // 设目标。Seek* 选择最近 POI；Flee 选择最近威胁中心；
        // Wander 使用航点漫游（到达后重选）。
        public static void SetGoal(CitizenAuthoring ca, GoalType type, Vector3[] pois)
        {
            if (ca == null) return;
            if (type == GoalType.Wander)
            {
                SetWanderGoal(ca);
                return;
            }

            Vector3 best = ca.transform.position;
            float bestD = float.MaxValue;
            for (int i = 0; i < pois.Length; i++)
            {
                float d = (pois[i] - ca.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = pois[i]; }
            }

            ca.currentGoalType = type;
            ca.currentGoalTarget = pois.Length > 0 ? best : ca.transform.position;
        }

        // Wander 到达重选:还在走向当前 Wander 目标就保持(不换),到达或刚切到 Wander 时选新点。
        // 防止 BT 每 ~0.5s 重选导致原地抖动。
        // 新目标最小距离约束:避免选到过近的点(市民刚减速又要转向,原地打转)。
        const float k_WanderRadius = 10f;
        const float k_MinWanderDist = 5f;
        static void SetWanderGoal(CitizenAuthoring ca)
        {
            bool stillPursuing = ca.currentGoalType == GoalType.Wander
                && (ca.transform.position - ca.currentGoalTarget).sqrMagnitude > 1f;
            if (stillPursuing) return;

            // 重试找足够远的目标(≥k_MinWanderDist),避免原地游走。
            for (int i = 0; i < 8; i++)
            {
                var r = UnityEngine.Random.insideUnitCircle * k_WanderRadius;
                Vector3 t = ca.transform.position + new Vector3(r.x, 0f, r.y);
                if ((t - ca.transform.position).sqrMagnitude >= k_MinWanderDist * k_MinWanderDist)
                {
                    ca.currentGoalType = GoalType.Wander;
                    ca.currentGoalTarget = t;
                    return;
                }
            }
            // 兜底:8 次未找到(极端),直接接受一个随机点。
            var r2 = UnityEngine.Random.insideUnitCircle * k_WanderRadius;
            ca.currentGoalType = GoalType.Wander;
            ca.currentGoalTarget = ca.transform.position + new Vector3(r2.x, 0f, r2.y);
        }
    }
}
