using CitizenSim;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim.Tests
{
    public class NeedsRoundTripTests
    {
        private World world;
        private EntityManager em;
        private GameObject registryGo;
        private GameObject citizenGo;
        private CitizenAuthoring ca;
        private Entity e;

        // 构造手工 World，按需挂 Snapshot/Resolve（沿用 SyncLoopTests 模式）。
        private void BuildWorld(bool withSnapshot, bool withResolve)
        {
            world = new World("NeedsTest");
            em = world.EntityManager;
            var simGroup = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            if (withSnapshot) simGroup.AddSystemToUpdateList(world.GetOrCreateSystemManaged<SnapshotSystem>());
            if (withResolve) simGroup.AddSystemToUpdateList(world.GetOrCreateSystemManaged<ResolveSystem>());

            registryGo = new GameObject("Registry");
            registryGo.AddComponent<CitizenRegistry>();

            citizenGo = new GameObject("Citizen");
            ca = citizenGo.AddComponent<CitizenAuthoring>();

            e = em.CreateEntity(
                typeof(SimPosition), typeof(SimVelocity), typeof(SimGoal),
                typeof(SimRadius), typeof(CitizenIndex), typeof(SimNeeds), typeof(Threatened), typeof(SimExit));
            em.SetComponentData(e, new SimPosition { Value = float3.zero });
            em.SetComponentData(e, new SimVelocity { Value = float3.zero });
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = float3.zero });
            em.SetComponentData(e, new SimRadius { Value = 0.5f });
            em.SetComponentData(e, new CitizenIndex { Value = 0 });
            em.SetComponentData(e, new SimNeeds { Value = float3.zero });

            registryGo.GetComponent<CitizenRegistry>().Register(new[] { citizenGo }, new[] { e });
        }

        [TearDown]
        public void TearDown()
        {
            if (citizenGo != null) Object.DestroyImmediate(citizenGo);
            if (registryGo != null) Object.DestroyImmediate(registryGo);
            if (world != null && world.IsCreated) world.Dispose();
        }

        [Test]
        public void Needs_Snapshot_CopiesGOToEntity()
        {
            BuildWorld(withSnapshot: true, withResolve: false);
            ca.needs = new Vector3(0.3f, 0.4f, 0.5f);

            world.SetTime(new Unity.Core.TimeData(0.0, 0.016f));
            world.Update();

            var needs = em.GetComponentData<SimNeeds>(e);
            Assert.AreEqual(0.3f, needs.Value.x, 1e-5f, "hunger 应从 GO 快照到 Entity");
            Assert.AreEqual(0.4f, needs.Value.y, 1e-5f, "fatigue 应从 GO 快照到 Entity");
            Assert.AreEqual(0.5f, needs.Value.z, 1e-5f, "fun 应从 GO 快照到 Entity");
        }

        [Test]
        public void Needs_Resolve_CopiesEntityToGO()
        {
            BuildWorld(withSnapshot: false, withResolve: true);
            em.SetComponentData(e, new SimNeeds { Value = new float3(0.9f, 0.8f, 0.7f) });
            ca.needs = Vector3.zero;

            world.SetTime(new Unity.Core.TimeData(0.1, 0.016f));
            world.Update();

            Assert.AreEqual(0.9f, ca.needs.x, 1e-5f, "hunger 应从 Entity 回写到 GO");
            Assert.AreEqual(0.8f, ca.needs.y, 1e-5f, "fatigue 应从 Entity 回写到 GO");
            Assert.AreEqual(0.7f, ca.needs.z, 1e-5f, "fun 应从 Entity 回写到 GO");
        }

        [Test]
        public void GoalTransition_LeavingSeek_UsesPreviousPoiForExitDirection()
        {
            BuildWorld(withSnapshot: true, withResolve: false);
            citizenGo.transform.position = new Vector3(10f, 0f, 0f);
            ca.lastGoalType = GoalType.SeekFood;
            ca.lastGoalTarget = new Vector3(9f, 0f, 0f);       // 旧食物点在市民左侧
            ca.currentGoalType = GoalType.Wander;
            ca.currentGoalTarget = new Vector3(20f, 0f, 0f);   // 新目标在市民右侧

            world.SetTime(new Unity.Core.TimeData(0.0, 0.016f));
            world.Update();

            Assert.AreEqual(Vector3.right, ca.exitDirection, "离开推力应远离旧 POI，而不是远离新目标");
            Assert.AreEqual(1f, ca.exitTimer, 1e-5f);
            Assert.AreEqual(ca.currentGoalTarget, ca.lastGoalTarget, "快照后应保存当前目标供下次切换使用");

            var exit = em.GetComponentData<SimExit>(e);
            Assert.AreEqual(1f, exit.Direction.x, 1e-5f);
            Assert.AreEqual(0f, exit.Direction.z, 1e-5f);
        }

        [Test]
        public void GoalTransition_EnteringFlee_ClearsPreviousPoiExitForce()
        {
            BuildWorld(withSnapshot: true, withResolve: false);
            citizenGo.transform.position = new Vector3(10f, 0f, 0f);
            ca.lastGoalType = GoalType.SeekFood;
            ca.lastGoalTarget = new Vector3(9f, 0f, 0f);
            ca.currentGoalType = GoalType.Flee;
            ca.currentGoalTarget = Vector3.zero;
            ca.exitTimer = 0.75f;
            ca.exitDirection = Vector3.right;

            world.SetTime(new Unity.Core.TimeData(0.0, 0.016f));
            world.Update();

            Assert.AreEqual(0f, ca.exitTimer, 1e-5f, "Flee 应清除旧 POI 离开推力");
            Assert.AreEqual(Vector3.zero, ca.exitDirection);

            var exit = em.GetComponentData<SimExit>(e);
            Assert.AreEqual(0f, exit.Timer, 1e-5f);
            Assert.AreEqual(float3.zero, exit.Direction);
        }
    }
}
