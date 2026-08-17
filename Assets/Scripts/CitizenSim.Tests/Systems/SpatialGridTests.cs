using CitizenSim;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using System.Collections.Generic;

namespace CitizenSim.Tests
{
    public class SpatialGridTests
    {
        private World world;
        private EntityManager em;
        private SimulationSystemGroup simGroup;

        [SetUp]
        public void Setup()
        {
            world = new World("SpatialGridTest");
            em = world.EntityManager;
            simGroup = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            var handle = world.GetOrCreateSystem<SpatialGridSystem>();
            simGroup.AddSystemToUpdateList(handle);
        }

        [TearDown]
        public void TearDown()
        {
            if (SpatialGridSystem.Grid.IsCreated) SpatialGridSystem.Grid.Dispose();
            if (SpatialGridSystem.Positions.IsCreated) SpatialGridSystem.Positions.Dispose();
            SpatialGridSystem.Count = 0;
            if (world != null && world.IsCreated) world.Dispose();
        }

        private Entity MakeCitizen(float3 pos, int index)
        {
            var e = em.CreateEntity(typeof(SimPosition), typeof(CitizenIndex), typeof(GridCell));
            em.SetComponentData(e, new SimPosition { Value = pos });
            em.SetComponentData(e, new CitizenIndex { Value = index });
            return e;
        }

        private void Tick()
        {
            world.SetTime(new Unity.Core.TimeData(0.016f, 0.016f));
            simGroup.Update();
            em.CompleteAllTrackedJobs();
        }

        [Test]
        public void Positions_Populated_By_CitizenIndex()
        {
            MakeCitizen(new float3(10, 0, 20), 0);
            MakeCitizen(new float3(5, 0, 5), 1);

            Tick();

            Assert.AreEqual(new float3(10, 0, 20), SpatialGridSystem.Positions[0]);
            Assert.AreEqual(new float3(5, 0, 5), SpatialGridSystem.Positions[1]);
        }

        [Test]
        public void Grid_Queryable_By_Cell()
        {
            // cellSize=1:(0,0,0) 与 (0.5,0,0.5) 同 cell(0,0);(10,0,10) 在 cell(10,10)
            MakeCitizen(new float3(0, 0, 0), 0);
            MakeCitizen(new float3(0.5f, 0, 0.5f), 1);
            MakeCitizen(new float3(10, 0, 10), 2);

            Tick();

            // cell(0,0) 应含索引 0 和 1
            var found = new List<int>();
            var cell00 = new int2(0, 0);
            Assert.IsTrue(SpatialGridSystem.Grid.TryGetFirstValue(cell00, out int idx, out var it),
                "cell(0,0) 应有值");
            found.Add(idx);
            while (SpatialGridSystem.Grid.TryGetNextValue(out idx, ref it)) found.Add(idx);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, found, "cell(0,0) 应含索引 0 和 1");

            // cell(10,10) 应含索引 2
            var cellTT = new int2(10, 10);
            Assert.IsTrue(SpatialGridSystem.Grid.TryGetFirstValue(cellTT, out int idx2, out var it2),
                "cell(10,10) 应有值");
            Assert.AreEqual(2, idx2, "cell(10,10) 应含索引 2");
        }

        [Test]
        public void Grid_EmptyCell_ReturnsFalse()
        {
            MakeCitizen(new float3(0, 0, 0), 0);
            Tick();

            Assert.IsFalse(SpatialGridSystem.Grid.TryGetFirstValue(new int2(99, 99), out _, out _),
                "空 cell 应查询不到");
        }
    }
}
