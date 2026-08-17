using CitizenSim;
using NUnit.Framework;
using UnityEngine;

namespace CitizenSim.Tests
{
    public class BtDecisionTests
    {
        private GameObject citizenGo;
        private CitizenAuthoring ca;

        [SetUp]
        public void Setup()
        {
            citizenGo = new GameObject("Citizen");
            ca = citizenGo.AddComponent<CitizenAuthoring>();
            ca.transform.position = Vector3.zero;
            ca.hungerThreshold = 0.7f;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(citizenGo);

        [Test]
        public void IsHungry_TrueAboveThreshold_FalseBelow()
        {
            ca.needs = new Vector3(0.8f, 0f, 0f);
            Assert.IsTrue(GoalDecision.IsHungry(ca), "hunger 0.8 > 0.7 应饿");

            ca.needs = new Vector3(0.5f, 0f, 0f);
            Assert.IsFalse(GoalDecision.IsHungry(ca), "hunger 0.5 < 0.7 不应饿");
        }

        [Test]
        public void IsHungry_Hysteresis_StaysHungryWhileSeekingUntilFull()
        {
            // Wander 时 0.5 < 0.7:不饿
            ca.currentGoalType = GoalType.Wander;
            ca.needs = new Vector3(0.5f, 0f, 0f);
            Assert.IsFalse(GoalDecision.IsHungry(ca), "Wander 且 0.5<0.7 不应饿");

            // SeekFood 时 0.5 仍饿(吃到 full=0 才停,防止阈值抖动)
            ca.currentGoalType = GoalType.SeekFood;
            ca.needs = new Vector3(0.5f, 0f, 0f);
            Assert.IsTrue(GoalDecision.IsHungry(ca), "SeekFood 且 0.5>0 应继续吃");

            // SeekFood 且吃饱(0):不饿 -> 切 Wander
            ca.needs = new Vector3(0f, 0f, 0f);
            Assert.IsFalse(GoalDecision.IsHungry(ca), "SeekFood 且吃饱(0) 不应饿");
        }

        [Test]
        public void SetGoal_SeekFood_PicksNearest()
        {
            var foods = new[] { new Vector3(10, 0, 0), new Vector3(1, 0, 0), new Vector3(20, 0, 0) };
            GoalDecision.SetGoal(ca, GoalType.SeekFood, foods);

            Assert.AreEqual(GoalType.SeekFood, ca.currentGoalType, "应设为 SeekFood");
            Assert.AreEqual(new Vector3(1, 0, 0), ca.currentGoalTarget, "应选最近食物 (1,0,0)");
        }

        [Test]
        public void SetGoal_SeekFood_NoFood_StaysPut()
        {
            GoalDecision.SetGoal(ca, GoalType.SeekFood, System.Array.Empty<Vector3>());
            Assert.AreEqual(GoalType.SeekFood, ca.currentGoalType);
            Assert.AreEqual(ca.transform.position, ca.currentGoalTarget, "无食物点目标应留在原地");
        }

        [Test]
        public void SetGoal_Wander_SetsWanderGoalNearby()
        {
            GoalDecision.SetGoal(ca, GoalType.Wander, System.Array.Empty<Vector3>());
            Assert.AreEqual(GoalType.Wander, ca.currentGoalType);
            Assert.AreEqual(0f, ca.currentGoalTarget.y, 1e-4f, "Wander 目标应在地面 y=0");
            Assert.LessOrEqual(ca.currentGoalTarget.magnitude, 10f + 1e-3f, "Wander 目标应在半径 10 内");
        }

        [Test]
        public void IsFatigued_Hysteresis_StaysTiredWhileSeekingHomeUntilRested()
        {
            ca.currentGoalType = GoalType.Wander;
            ca.needs = new Vector3(0f, 0.8f, 0f);
            Assert.IsTrue(GoalDecision.IsFatigued(ca), "Wander 且 fatigue 0.8>0.7 应累");
            ca.needs = new Vector3(0f, 0.5f, 0f);
            Assert.IsFalse(GoalDecision.IsFatigued(ca), "Wander 且 fatigue 0.5<0.7 不应累");

            ca.currentGoalType = GoalType.SeekHome;
            ca.needs = new Vector3(0f, 0.5f, 0f);
            Assert.IsTrue(GoalDecision.IsFatigued(ca), "SeekHome 且 0.5>0 应继续休息");
            ca.needs = new Vector3(0f, 0f, 0f);
            Assert.IsFalse(GoalDecision.IsFatigued(ca), "SeekHome 且休息够(0) 不应累");
        }

        [Test]
        public void IsBored_Hysteresis_StaysBoredWhileSeekingFunUntilFull()
        {
            ca.currentGoalType = GoalType.Wander;
            ca.needs = new Vector3(0f, 0f, 0.2f);
            Assert.IsTrue(GoalDecision.IsBored(ca), "Wander 且 fun 0.2<0.3 应无聊");
            ca.needs = new Vector3(0f, 0f, 0.5f);
            Assert.IsFalse(GoalDecision.IsBored(ca), "Wander 且 fun 0.5>0.3 不应无聊");

            ca.currentGoalType = GoalType.SeekFun;
            ca.needs = new Vector3(0f, 0f, 0.5f);
            Assert.IsTrue(GoalDecision.IsBored(ca), "SeekFun 且 0.5<0.9 应继续玩");
            ca.needs = new Vector3(0f, 0f, 0.9f);
            Assert.IsFalse(GoalDecision.IsBored(ca), "SeekFun 且玩够(0.9) 不应无聊");
        }

        [Test]
        public void SetGoal_SeekHome_PicksNearest()
        {
            var homes = new[] { new Vector3(10, 0, 0), new Vector3(2, 0, 0), new Vector3(20, 0, 0) };
            GoalDecision.SetGoal(ca, GoalType.SeekHome, homes);
            Assert.AreEqual(GoalType.SeekHome, ca.currentGoalType);
            Assert.AreEqual(new Vector3(2, 0, 0), ca.currentGoalTarget, "应选最近家 (2,0,0)");
        }

        [Test]
        public void SetGoal_SeekFun_PicksNearest()
        {
            var funs = new[] { new Vector3(10, 0, 0), new Vector3(3, 0, 0), new Vector3(20, 0, 0) };
            GoalDecision.SetGoal(ca, GoalType.SeekFun, funs);
            Assert.AreEqual(GoalType.SeekFun, ca.currentGoalType);
            Assert.AreEqual(new Vector3(3, 0, 0), ca.currentGoalTarget, "应选最近娱乐 (3,0,0)");
        }

        [Test]
        public void SetGoal_Wander_KeepsTargetUntilArrived_ThenRepicks()
        {
            // 初始设 Wander 目标(currentGoalType 变 Wander)
            GoalDecision.SetGoal(ca, GoalType.Wander, System.Array.Empty<Vector3>());
            Assert.AreEqual(GoalType.Wander, ca.currentGoalType);

            // 手动设一个远目标(未到达,dist>1):SetGoal(Wander) 应保持
            ca.currentGoalTarget = new Vector3(5, 0, 5);
            GoalDecision.SetGoal(ca, GoalType.Wander, System.Array.Empty<Vector3>());
            Assert.AreEqual(new Vector3(5, 0, 5), ca.currentGoalTarget, "未到达时应保持当前 Wander 目标");

            // 到达(dist<1):应换新点
            ca.currentGoalTarget = new Vector3(0.1f, 0, 0);
            GoalDecision.SetGoal(ca, GoalType.Wander, System.Array.Empty<Vector3>());
            Assert.AreNotEqual(new Vector3(0.1f, 0, 0), ca.currentGoalTarget, "到达后应换新 Wander 目标");
        }

        [Test]
        public void IsThreatened_MirrorsAuthoringFlag()
        {
            ca.threatened = false;
            Assert.IsFalse(GoalDecision.IsThreatened(ca), "未受威胁应 false");
            ca.threatened = true;
            Assert.IsTrue(GoalDecision.IsThreatened(ca), "受威胁应 true");
        }

        [Test]
        public void SetGoal_Flee_PicksNearestThreatCenter()
        {
            var zones = new[] { new Vector3(10, 0, 0), new Vector3(2, 0, 0), new Vector3(20, 0, 0) };
            GoalDecision.SetGoal(ca, GoalType.Flee, zones);
            Assert.AreEqual(GoalType.Flee, ca.currentGoalType, "应设为 Flee");
            Assert.AreEqual(new Vector3(2, 0, 0), ca.currentGoalTarget, "应选最近威胁中心 (2,0,0) 作 evade 目标");
        }

        [Test]
        public void SetGoal_Flee_NoZones_StaysPut()
        {
            GoalDecision.SetGoal(ca, GoalType.Flee, System.Array.Empty<Vector3>());
            Assert.AreEqual(GoalType.Flee, ca.currentGoalType);
            Assert.AreEqual(ca.transform.position, ca.currentGoalTarget, "无威胁区目标应留在原地");
        }
    }
}
