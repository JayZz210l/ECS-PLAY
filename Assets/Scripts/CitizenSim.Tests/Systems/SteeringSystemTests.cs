using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class SteeringSystemTests
    {
        private World world;
        private EntityManager em;
        private SimulationSystemGroup simGroup;

        [SetUp] public void Setup()
        {
            world = new World("SteeringTest");
            em = world.EntityManager;
            simGroup = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            // SpatialGridSystem 必须在 SteeringSystem 前(SteeringJob 读 grid/positions)。
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystem<SpatialGridSystem>());
            simGroup.AddSystemToUpdateList(world.GetOrCreateSystem<SteeringSystem>());
            // SteeringJob 字段含 FlowField(NativeArray),schedule 时必须 IsCreated。
            // 测试世界无 FlowFieldBuildSystem,手动分配 1x1 空流场(全 INF)。Wander 不查流场,不受影响。
            AllocateTestFlowFields();
        }

        [TearDown] public void TearDown()
        {
            if (SpatialGridSystem.Grid.IsCreated) SpatialGridSystem.Grid.Dispose();
            if (SpatialGridSystem.Positions.IsCreated) SpatialGridSystem.Positions.Dispose();
            SpatialGridSystem.Count = 0;
            DisposeTestFlowFields();
            if (world != null && world.IsCreated) world.Dispose();
        }

        // 分配 1x1 空流场(全 INF),让 SteeringJob 能 schedule。测试用 Wander goal,不查流场。
        static void AllocateTestFlowFields()
        {
            AllocateOne(ref FlowFieldBuildSystem.FoodField);
            AllocateOne(ref FlowFieldBuildSystem.HomeField);
            AllocateOne(ref FlowFieldBuildSystem.FunField);
            FlowFieldBuildSystem.Initialized = true;
            FlowFieldBuildSystem.Dirty = false;
            // SteeringJob 字段含 movingObstaclePos(NativeArray),schedule 时必须 IsCreated。
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

        private Entity MakeCitizen(float3 pos, int index, GoalType goal, float3 target)
        {
            var e = em.CreateEntity();
            em.AddComponentData(e, new SimPosition { Value = pos });
            em.AddComponentData(e, new SimVelocity { Value = float3.zero });
            em.AddComponentData(e, new SimGoal { Type = goal, Target = target });
            em.AddComponentData(e, new GridCell { Value = new int2((int)pos.x, (int)pos.z) });
            em.AddComponentData(e, new CitizenIndex { Value = index });
            em.AddComponentData(e, new SimExit { Timer = 0f, Direction = float3.zero });
            return e;
        }

        private void Tick()
        {
            world.SetTime(new Unity.Core.TimeData(0.016f, 0.016f));
            simGroup.Update();
            em.CompleteAllTrackedJobs();
        }

        [Test] public void Steering_SetsVelocityTowardTarget()
        {
            // Wander 走 Arrive(SeekFood 走流场,在 FlowFieldMathTests 覆盖)。测 Arrive 朝目标。
            var e = MakeCitizen(new float3(0, 0, 0), 0, GoalType.Wander, new float3(10, 0, 0));
            Tick();
            var vel = em.GetComponentData<SimVelocity>(e);
            Assert.Greater(vel.Value.x, 0f, "velocity.x 应朝 +x 目标为正");
            Assert.AreEqual(0f, vel.Value.z, 1e-5f, "z 方向无分量");
        }

        [Test] public void Steering_AtTarget_ZeroVelocity()
        {
            var e = MakeCitizen(new float3(7, 7, 7), 0, GoalType.Wander, new float3(7, 7, 7));
            Tick();
            var vel = em.GetComponentData<SimVelocity>(e);
            Assert.AreEqual(float3.zero, vel.Value);
        }

        [Test] public void Steering_RepelsFromNearbyNeighbor()
        {
            // A 朝 +x 目标走(Wander Arrive),B 在 A 前方近处(avoidRadius=0.5 内):A 的速度应有 -x 排斥分量抵消部分
            var a = MakeCitizen(new float3(0, 0, 0), 0, GoalType.Wander, new float3(10, 0, 0));
            var b = MakeCitizen(new float3(0.4f, 0, 0), 1, GoalType.Wander, new float3(0.4f, 0, 0));
            Tick();
            var velA = em.GetComponentData<SimVelocity>(a).Value;
            // 无 B 时 A 速度 x=2(全速)。有 B 挡在 +x,排斥 -x,故 velA.x < 2
            Assert.Less(velA.x, 2f, "邻居在 +x 挡路时 A 的 x 速度应被排斥力削弱");
        }

        [Test] public void Steering_Flee_IgnoresStalePoiExitForce()
        {
            // 威胁中心在 +x，逃跑必须朝 -x。残留的 +x 离开推力强度更大，若被混入会反向。
            var e = MakeCitizen(new float3(0, 0, 0), 0, GoalType.Flee, new float3(10, 0, 0));
            em.SetComponentData(e, new SimExit { Timer = 1f, Direction = new float3(1f, 0f, 0f) });

            Tick();

            var velocity = em.GetComponentData<SimVelocity>(e).Value;
            Assert.Less(velocity.x, 0f, "Flee 不应被残留的 POI 离开推力带向威胁中心");
            Assert.AreEqual(0f, velocity.z, 1e-5f);
        }
    }
}
