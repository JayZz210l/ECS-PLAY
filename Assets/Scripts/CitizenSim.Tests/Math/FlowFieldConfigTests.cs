using CitizenSim;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim.Tests
{
    public class FlowFieldConfigTests
    {
        private GameObject cfgGo;
        private GameObject groundGo;
        private FlowFieldConfig cfg;

        [SetUp]
        public void Setup()
        {
            cfgGo = new GameObject("FlowFieldConfig");
            cfg = cfgGo.AddComponent<FlowFieldConfig>();
            cfg.Register();
        }

        [TearDown]
        public void TearDown()
        {
            FlowFieldConfig.Clear();
            if (groundGo != null) Object.DestroyImmediate(groundGo);
            if (cfgGo != null) Object.DestroyImmediate(cfgGo);
        }

        [Test]
        public void Origin_NullGround_FallsBackToWorldOrigin()
        {
            cfg.ground = null;
            cfg.gridSize = new Vector2Int(40, 40);
            cfg.cellSize = 2f;

            // 地面中心 = 世界原点,origin = 0 - (40*2/2, 0, 40*2/2) = (-40,0,-40)
            Assert.AreEqual(new float3(-40f, 0f, -40f), cfg.Origin, "无 ground 时 origin 应回退到原点为中心的默认网格");
        }

        [Test]
        public void Origin_FollowsGroundCenter()
        {
            groundGo = new GameObject("Ground");
            groundGo.transform.position = new Vector3(100f, 0f, 200f);
            cfg.ground = groundGo.transform;
            cfg.gridSize = new Vector2Int(20, 10);
            cfg.cellSize = 2f;

            // origin.x = 100 - 20*2/2 = 80;origin.z = 200 - 10*2/2 = 190
            Assert.AreEqual(new float3(80f, 0f, 190f), cfg.Origin, "origin 应等于地面中心减去网格半边长");
        }

        [Test]
        public void Origin_GridSizeChange_MovesOrigin()
        {
            groundGo = new GameObject("Ground");
            groundGo.transform.position = new Vector3(0f, 0f, 0f);
            cfg.ground = groundGo.transform;
            cfg.cellSize = 2f;

            cfg.gridSize = new Vector2Int(40, 40);
            Assert.AreEqual(new float3(-40f, 0f, -40f), cfg.Origin, "40x40 时 origin 在 (-40,0,-40)");

            cfg.gridSize = new Vector2Int(20, 20);
            Assert.AreEqual(new float3(-20f, 0f, -20f), cfg.Origin, "20x20 时 origin 应移到 (-20,0,-20)");
        }

        [Test]
        public void GridSize_Property_ReturnsInt2()
        {
            cfg.gridSize = new Vector2Int(13, 7);
            Assert.AreEqual(new int2(13, 7), cfg.GridSize, "GridSize 属性应映射 Vector2Int 到 int2");
        }
    }
}
