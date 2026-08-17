using CitizenSim;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim.Tests
{
    public class NeedsDecayTests
    {
        private World world;
        private EntityManager em;
        private GameObject registryGo;
        private GameObject foodGo, homeGo, funGo;
        private Entity e;

        [SetUp]
        public void Setup()
        {
            world = new World("NeedsDecayTest");
            em = world.EntityManager;
            var simGroup = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystemManaged<NeedsDecaySystem>());

            registryGo = new GameObject("PoiRegistry");
            var poi = registryGo.AddComponent<PoiRegistry>();
            poi.Register();

            foodGo = new GameObject("Food"); foodGo.transform.position = Vector3.zero;
            homeGo = new GameObject("Home"); homeGo.transform.position = new Vector3(50, 0, 0);
            funGo  = new GameObject("Fun");  funGo.transform.position  = new Vector3(-50, 0, 0);
            poi.foodPoints = new[] { foodGo.transform };
            poi.homePoints = new[] { homeGo.transform };
            poi.funPoints  = new[] { funGo.transform };

            e = em.CreateEntity(typeof(SimPosition), typeof(SimGoal), typeof(SimNeeds));
            em.SetComponentData(e, new SimPosition { Value = new float3(100f, 0f, 0f) });
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = float3.zero });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0.5f, 0f, 0.5f) });
        }

        [TearDown]
        public void TearDown()
        {
            PoiRegistry.Clear();
            if (foodGo != null) Object.DestroyImmediate(foodGo);
            if (homeGo != null) Object.DestroyImmediate(homeGo);
            if (funGo != null) Object.DestroyImmediate(funGo);
            if (registryGo != null) Object.DestroyImmediate(registryGo);
            if (world != null && world.IsCreated) world.Dispose();
        }

        private void Tick(float dt)
        {
            world.SetTime(new Unity.Core.TimeData(dt, dt));
            world.Update();
        }

        [Test]
        public void Hunger_Increases_WhenNotAtFood()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(100f, 0f, 0f) });
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = float3.zero });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0.5f, 0f, 0.5f) });

            Tick(0.1f);

            float hunger = em.GetComponentData<SimNeeds>(e).Value.x;
            Assert.Greater(hunger, 0.5f, "不在食物点时 hunger 应随时间增加");
        }

        [Test]
        public void Hunger_Decreases_WhenAtFoodAndSeeking()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(0f, 0f, 0f) }); // 在食物点
            em.SetComponentData(e, new SimGoal { Type = GoalType.SeekFood, Target = float3.zero });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0.9f, 0f, 0.5f) });

            Tick(0.1f);

            float hunger = em.GetComponentData<SimNeeds>(e).Value.x;
            Assert.Less(hunger, 0.9f, "在食物点且 SeekFood 时 hunger 应减少（吃）");
        }

        [Test]
        public void Hunger_DoesNotEat_WhenAtFoodButWandering()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(0f, 0f, 0f) }); // 在食物点
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = float3.zero });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0.5f, 0f, 0.5f) });

            Tick(0.1f);

            float hunger = em.GetComponentData<SimNeeds>(e).Value.x;
            Assert.Greater(hunger, 0.5f, "在食物点但 Wander 时不应吃，hunger 仍应增加");
        }

        [Test]
        public void Fatigue_Increases_WhenNotAtHome()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(100f, 0f, 0f) });
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = float3.zero });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0f, 0.5f, 0.5f) });

            Tick(0.1f);

            float fatigue = em.GetComponentData<SimNeeds>(e).Value.y;
            Assert.Greater(fatigue, 0.5f, "不在家时 fatigue 应随时间增加");
        }

        [Test]
        public void Fatigue_Decreases_WhenAtHomeAndSeeking()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(50f, 0f, 0f) }); // 在家点
            em.SetComponentData(e, new SimGoal { Type = GoalType.SeekHome, Target = new float3(50, 0, 0) });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0f, 0.9f, 0.5f) });

            Tick(0.1f);

            float fatigue = em.GetComponentData<SimNeeds>(e).Value.y;
            Assert.Less(fatigue, 0.9f, "在家且 SeekHome 时 fatigue 应减少(休息)");
        }

        [Test]
        public void Fun_Decreases_WhenNotAtFun()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(100f, 0f, 0f) });
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = float3.zero });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0f, 0f, 0.8f) });

            Tick(0.1f);

            float fun = em.GetComponentData<SimNeeds>(e).Value.z;
            Assert.Less(fun, 0.8f, "不在娱乐点时 fun 应随时间下降");
        }

        [Test]
        public void Fun_Increases_WhenAtFunAndSeeking()
        {
            em.SetComponentData(e, new SimPosition { Value = new float3(-50f, 0f, 0f) }); // 在娱乐点
            em.SetComponentData(e, new SimGoal { Type = GoalType.SeekFun, Target = new float3(-50, 0, 0) });
            em.SetComponentData(e, new SimNeeds { Value = new float3(0f, 0f, 0.2f) });

            Tick(0.1f);

            float fun = em.GetComponentData<SimNeeds>(e).Value.z;
            Assert.Greater(fun, 0.2f, "在娱乐点且 SeekFun 时 fun 应增加(玩)");
        }
    }
}
