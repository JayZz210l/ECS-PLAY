using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim.Tests
{
    public class SyncLoopTests
    {
        private World world;
        private EntityManager em;
        private GameObject registryGo;
        private GameObject citizenGo;

        [SetUp] public void Setup()
        {
            world = new World("SyncLoopTest");
            em = world.EntityManager;
            // 手动 World：显式把三个系统挂进 SimulationSystemGroup 的 update list
            // （[UpdateInGroup] 不自动归组；[UpdateBefore]/[UpdateAfter] 负责组内排序）。
            var simGroup = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystemManaged<SnapshotSystem>());
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystem<SpatialGridSystem>());
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystem<SteeringSystem>());
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystemManaged<ResolveSystem>());

            registryGo = new GameObject("Registry");
            var registry = registryGo.AddComponent<CitizenRegistry>();

            citizenGo = new GameObject("Citizen");
            citizenGo.transform.position = Vector3.zero;

            var e = em.CreateEntity(
                typeof(SimPosition), typeof(SimVelocity), typeof(SimGoal),
                typeof(SimRadius), typeof(CitizenIndex), typeof(GridCell), typeof(Threatened), typeof(SimExit));
            em.SetComponentData(e, new SimPosition { Value = float3.zero });
            em.SetComponentData(e, new SimVelocity { Value = float3.zero });
            em.SetComponentData(e, new SimGoal { Type = GoalType.Wander, Target = new float3(100, 0, 0) });
            em.SetComponentData(e, new SimRadius { Value = 0.5f });
            em.SetComponentData(e, new CitizenIndex { Value = 0 });
            em.SetComponentData(e, new GridCell { Value = new int2(0, 0) });

            registry.Register(new[] { citizenGo }, new[] { e });

            // SteeringJob 字段含 FlowField(NativeArray),schedule 时必须 IsCreated。手动分配 1x1 空流场。
            AllocateTestFlowFields();
        }

        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(citizenGo);
            Object.DestroyImmediate(registryGo);
            if (SpatialGridSystem.Grid.IsCreated) SpatialGridSystem.Grid.Dispose();
            if (SpatialGridSystem.Positions.IsCreated) SpatialGridSystem.Positions.Dispose();
            SpatialGridSystem.Count = 0;
            DisposeTestFlowFields();
            world.Dispose();
        }

        static void AllocateTestFlowFields()
        {
            AllocateOne(ref FlowFieldBuildSystem.FoodField);
            AllocateOne(ref FlowFieldBuildSystem.HomeField);
            AllocateOne(ref FlowFieldBuildSystem.FunField);
            FlowFieldBuildSystem.Initialized = true;
            FlowFieldBuildSystem.Dirty = false;
            if (!ObstacleRegistry.MovingObstaclePos.IsCreated)
                ObstacleRegistry.MovingObstaclePos = new NativeArray<float3>(64, Allocator.Persistent);
            if (!ObstacleRegistry.MovingObstacleRad.IsCreated)
                ObstacleRegistry.MovingObstacleRad = new NativeArray<float>(64, Allocator.Persistent);
            ObstacleRegistry.MovingObstacleCount = 0;
        }

        static void AllocateOne(ref FlowField field)
        {
            if (field.directions.IsCreated) field.Dispose();
            field = new FlowField
            {
                gridSize = new int2(1, 1),
                cellSize = 1f,
                origin = float3.zero,
                directions = new NativeArray<float3>(1, Allocator.Persistent),
                costs = new NativeArray<float>(1, Allocator.Persistent),
                blocked = new NativeArray<byte>(1, Allocator.Persistent),
            };
            field.costs[0] = FlowFieldMath.Inf;
        }

        static void DisposeTestFlowFields()
        {
            if (FlowFieldBuildSystem.FoodField.directions.IsCreated) FlowFieldBuildSystem.FoodField.Dispose();
            if (FlowFieldBuildSystem.HomeField.directions.IsCreated) FlowFieldBuildSystem.HomeField.Dispose();
            if (FlowFieldBuildSystem.FunField.directions.IsCreated) FlowFieldBuildSystem.FunField.Dispose();
            FlowFieldBuildSystem.Initialized = false;
            FlowFieldBuildSystem.Dirty = true;
            ObstacleRegistry.DisposeStatics();
        }

        [Test] public void SyncLoop_MovesCitizenTowardTarget()
        {
            float xBefore = citizenGo.transform.position.x;
            // world.Update 会把 SetTime 的时间同步到 WorldUnmanaged，ResolveSystem 才能读到 dt。
            // 用 SetTime 而非 PushTime，避免时间栈平衡断言。
            world.SetTime(new Unity.Core.TimeData(0.1, 0.016f));
            world.Update();
            float xAfter = citizenGo.transform.position.x;
            Assert.Greater(xAfter, xBefore, "市民应朝 +x 目标移动");
        }
    }
}
