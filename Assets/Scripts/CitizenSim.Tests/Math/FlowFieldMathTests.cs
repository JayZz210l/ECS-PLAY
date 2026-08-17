using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class FlowFieldMathTests
    {
        // 造测试用小网格。cellSize=1, origin=zero,全可走(blocked=0)。
        static FlowField MakeTestField(int w, int h)
        {
            return new FlowField
            {
                gridSize = new int2(w, h),
                cellSize = 1f,
                origin = float3.zero,
                directions = new NativeArray<float3>(w * h, Allocator.Persistent),
                costs = new NativeArray<float>(w * h, Allocator.Persistent),
                blocked = new NativeArray<byte>(w * h, Allocator.Persistent),
            };
        }

        [Test] public void SingleTarget_DirectionsPointTowardTarget()
        {
            // 5x5 网格,目标 (4,4)。(0,0) 方向应朝 +x 或 +z 象限(朝目标)
            var field = MakeTestField(5, 5);
            try
            {
                FlowFieldMath.BuildSingleTarget(ref field, new int2(4, 4));

                var dir00 = field.directions[field.CellIndex(new int2(0, 0))];
                Assert.IsTrue(dir00.x >= 0f && dir00.z >= 0f,
                    $"(0,0) 方向应朝目标象限(+x/+z),实际 {dir00}");

                // (3,4) 应朝 +x(目标在 +x)
                var dir34 = field.directions[field.CellIndex(new int2(3, 4))];
                Assert.IsTrue(dir34.x > 0.5f, $"(3,4) 应朝 +x,实际 {dir34}");

                // (4,3) 应朝 +z
                var dir43 = field.directions[field.CellIndex(new int2(4, 3))];
                Assert.IsTrue(dir43.z > 0.5f, $"(4,3) 应朝 +z,实际 {dir43}");
            }
            finally { field.Dispose(); }
        }

        [Test] public void SingleTarget_TargetCell_ZeroDirectionZeroCost()
        {
            var field = MakeTestField(5, 5);
            try
            {
                FlowFieldMath.BuildSingleTarget(ref field, new int2(2, 2));

                int ti = field.CellIndex(new int2(2, 2));
                Assert.AreEqual(0f, field.costs[ti], "目标格子 cost 应为 0");
                Assert.AreEqual(float3.zero, field.directions[ti], "目标格子 direction 应为 zero");
            }
            finally { field.Dispose(); }
        }

        [Test] public void SingleTarget_BlockedCell_DetoursAround()
        {
            // 5x3 网格,目标 (4,0),(2,0) 是障碍。(0,0) 应能绕道到达(不被堵死)
            var field = MakeTestField(5, 3);
            try
            {
                field.blocked[field.CellIndex(new int2(2, 0))] = 1;
                FlowFieldMath.BuildSingleTarget(ref field, new int2(4, 0));

                // (0,0) 应可达(绕道),cost 非 INF
                float cost00 = field.costs[field.CellIndex(new int2(0, 0))];
                Assert.IsTrue(cost00 < 1e8f, $"(0,0) 应绕道可达,cost={cost00}");

                // (0,0) 方向应朝目标象限(不背离目标)
                var dir00 = field.directions[field.CellIndex(new int2(0, 0))];
                Assert.IsTrue(dir00.x >= 0f && dir00.z >= 0f,
                    $"(0,0) 方向应朝目标象限,实际 {dir00}");

                // 障碍格子本身不可达
                float costBlocked = field.costs[field.CellIndex(new int2(2, 0))];
                Assert.IsTrue(costBlocked >= 1e8f, "障碍格子应 INF");
            }
            finally { field.Dispose(); }
        }

        [Test] public void SingleTarget_TargetBlocked_AllInf()
        {
            // 目标本身被障碍占 -> 全场 INF(directions 保持 zero)
            var field = MakeTestField(5, 5);
            try
            {
                field.blocked[field.CellIndex(new int2(2, 2))] = 1;
                FlowFieldMath.BuildSingleTarget(ref field, new int2(2, 2));

                for (int i = 0; i < field.CellCount; i++)
                {
                    Assert.IsTrue(field.costs[i] >= 1e8f, $"目标被堵,格子 {i} 应 INF");
                    Assert.AreEqual(float3.zero, field.directions[i], "目标被堵,direction 应 zero");
                }
            }
            finally { field.Dispose(); }
        }

        [Test] public void SingleTarget_UnreachableCell_Inf()
        {
            // 用障碍墙把目标 (4,0) 和 (0,0) 隔开:(2,0)(2,1)(2,2) 全堵,5x3 网格
            // 目标在 (4,0),(0,0) 不可达
            var field = MakeTestField(5, 3);
            try
            {
                field.blocked[field.CellIndex(new int2(2, 0))] = 1;
                field.blocked[field.CellIndex(new int2(2, 1))] = 1;
                field.blocked[field.CellIndex(new int2(2, 2))] = 1;
                FlowFieldMath.BuildSingleTarget(ref field, new int2(4, 0));

                float cost00 = field.costs[field.CellIndex(new int2(0, 0))];
                Assert.IsTrue(cost00 >= 1e8f, $"被墙隔开的 (0,0) 应 INF,cost={cost00}");
                Assert.AreEqual(float3.zero, field.directions[field.CellIndex(new int2(0, 0))],
                    "不可达格子 direction 应 zero");
            }
            finally { field.Dispose(); }
        }

        [Test] public void MultiSource_TwoSources_PicksNearest()
        {
            // 10x1 网格,源在 (0,0) 和 (9,0)。(2,0) 应朝左(0,0),(7,0) 应朝右(9,0)
            var field = MakeTestField(10, 1);
            var sources = new NativeList<int2>(Allocator.Persistent);
            try
            {
                sources.Add(new int2(0, 0));
                sources.Add(new int2(9, 0));
                FlowFieldMath.BuildMultiSource(ref field, sources);

                var dir2 = field.directions[field.CellIndex(new int2(2, 0))];
                Assert.IsTrue(dir2.x < -0.5f, $"(2,0) 应朝左(0,0),实际 {dir2}");

                var dir7 = field.directions[field.CellIndex(new int2(7, 0))];
                Assert.IsTrue(dir7.x > 0.5f, $"(7,0) 应朝右(9,0),实际 {dir7}");

                // 两个源格子自身 cost=0, direction=zero
                Assert.AreEqual(0f, field.costs[field.CellIndex(new int2(0, 0))]);
                Assert.AreEqual(0f, field.costs[field.CellIndex(new int2(9, 0))]);
            }
            finally { field.Dispose(); sources.Dispose(); }
        }

        [Test] public void MultiSource_BlockedSource_Skipped()
        {
            // 源本身被障碍占 -> 跳过,不崩溃,另一个源仍生效
            var field = MakeTestField(5, 1);
            var sources = new NativeList<int2>(Allocator.Persistent);
            try
            {
                field.blocked[field.CellIndex(new int2(0, 0))] = 1;  // 这个源被堵
                sources.Add(new int2(0, 0));
                sources.Add(new int2(4, 0));
                FlowFieldMath.BuildMultiSource(ref field, sources);

                // (4,0) 源生效,(2,0) 应朝右(4,0)
                var dir2 = field.directions[field.CellIndex(new int2(2, 0))];
                Assert.IsTrue(dir2.x > 0.5f, $"(2,0) 应朝右(4,0),实际 {dir2}");
            }
            finally { field.Dispose(); sources.Dispose(); }
        }

        [Test] public void MultiSource_Empty_AllInf()
        {
            // 无源 -> 全 INF
            var field = MakeTestField(5, 5);
            var sources = new NativeList<int2>(Allocator.Persistent);
            try
            {
                FlowFieldMath.BuildMultiSource(ref field, sources);
                for (int i = 0; i < field.CellCount; i++)
                    Assert.IsTrue(field.costs[i] >= 1e8f, $"无源,格子 {i} 应 INF");
            }
            finally { field.Dispose(); sources.Dispose(); }
        }

        [Test] public void Diagonal_DirectionIsDiagonal()
        {
            // 9x9 网格,源 (8,8)。(1,1) 在目标对角象限:8 邻域下应朝对角线方向(斜向走,非纯正交)。
            var field = MakeTestField(9, 9);
            var sources = new NativeList<int2>(Allocator.Persistent);
            try
            {
                sources.Add(new int2(8, 8));
                FlowFieldMath.BuildMultiSource(ref field, sources);

                var dir = field.directions[field.CellIndex(new int2(1, 1))];
                // 对角方向:x 和 z 分量都应明显(朝 (8,8) 斜上方)
                Assert.IsTrue(dir.x > 0.3f && dir.z > 0.3f,
                    $"(1,1) 应朝对角方向(x,z 都显著),实际 {dir}");
            }
            finally { field.Dispose(); sources.Dispose(); }
        }

        [Test] public void Diagonal_NoCornerCutThrough()
        {
            // 5x5 网格,源 (4,4)。(2,2) 和 (3,3) 是两个对角障碍,
            // (2,3) 的斜向邻居 (3,4)? —— 用墙角结构验证不穿角:
            // 设 (1,0) 和 (0,1) blocked,则 (0,0) 到 (1,1) 的对角被两个正交障碍夹住,不可斜穿。
            var field = MakeTestField(5, 5);
            var sources = new NativeList<int2>(Allocator.Persistent);
            try
            {
                // 源 (4,4)。在 (2,2) 周围造墙角:
                // (2,1) 和 (1,2) blocked,则 (1,1) 不能对角到 (2,2)。
                field.blocked[field.CellIndex(new int2(2, 1))] = 1;
                field.blocked[field.CellIndex(new int2(1, 2))] = 1;
                sources.Add(new int2(4, 4));
                FlowFieldMath.BuildMultiSource(ref field, sources);

                // (1,1) 的 direction 不应指向被夹住的对角 (2,2)(即不能沿 (1,1)->(2,2) 对角方向)
                var dir = field.directions[field.CellIndex(new int2(1, 1))];
                // (2,2) 方向 = (+1,+1)。若被夹,方向应偏向正交路径,而非纯对角穿过墙角。
                // 允许方向是 +x 或 +z(绕行),但不能是同时 x,z 都指向被堵的对角。
                bool cutThrough = dir.x > 0.3f && dir.z > 0.3f;
                Assert.IsFalse(cutThrough,
                    $"(1,1) 不应斜穿墙角(两正交障碍夹住),实际 {dir}");
            }
            finally { field.Dispose(); sources.Dispose(); }
        }

        [Test] public void Congestion_HighDensityCell_Avoided()
        {
            // 7x1 网格,源 (6,0)。无障碍时 (0,0) 直线到源。设 (2,0) 高密度,
            // 拥堵成本应让路径优先绕开该格(cost 升高/方向偏离)。
            var field = MakeTestField(7, 1);
            var sources = new NativeList<int2>(Allocator.Persistent);
            var density = new NativeArray<float>(7 * 1, Allocator.Persistent);
            try
            {
                sources.Add(new int2(6, 0));

                // 无拥堵:直线 cost = 6
                FlowFieldMath.BuildMultiSource(ref field, sources);
                float costNoCong = field.costs[field.CellIndex(new int2(0, 0))];
                Assert.AreEqual(6f, costNoCong, 0.01f, "无拥堵时 (0,0) 直线 cost=6");

                // 高密度格 (2,0):拥堵成本应提高该格成本
                density[field.CellIndex(new int2(2, 0))] = 99f;  // 远超 maxDensity
                FlowFieldMath.BuildMultiSource(ref field, sources, density, 3f, 8f);
                float costCong = field.costs[field.CellIndex(new int2(0, 0))];
                Assert.Greater(costCong, costNoCong,
                    $"拥堵后 (0,0) cost 应高于无拥堵({costNoCong}->{costCong}),走更贵的路或绕行");
            }
            finally { field.Dispose(); sources.Dispose(); density.Dispose(); }
        }

        [Test] public void RebuildRegion_AfterObstacleAdded_UpdatesRegionCost()
        {
            // 7x3 网格,源 (6,1)。无障碍时 (2,1) cost=4。加障碍 (3,1) 后局部重算,(2,1) 应绕道 cost>4。
            var field = MakeTestField(7, 3);
            var sources = new NativeList<int2>(Allocator.Persistent);
            var changed = new NativeList<int2>(Allocator.Persistent);
            try
            {
                sources.Add(new int2(6, 1));
                FlowFieldMath.BuildMultiSource(ref field, sources);
                Assert.AreEqual(4f, field.costs[field.CellIndex(new int2(2, 1))],
                    "无障碍时 (2,1) cost=4(直线到源)");

                // 加障碍 (3,1),局部重算(radius=2 覆盖 x=1..5)
                field.blocked[field.CellIndex(new int2(3, 1))] = 1;
                changed.Add(new int2(3, 1));
                FlowFieldMath.RebuildRegion(ref field, changed, 2);

                Assert.Greater(field.costs[field.CellIndex(new int2(2, 1))], 4f,
                    "障碍后 (2,1) cost 应 > 4(绕道)");
            }
            finally { field.Dispose(); sources.Dispose(); changed.Dispose(); }
        }

        [Test] public void RebuildRegion_OutsideRegion_Unchanged()
        {
            // 7x3 网格,源 (6,1)。加障碍 (3,1),局部重算 radius=1。
            // (0,1) 在区域外(x=0,区域 x=2..4),cost 不应变。
            var field = MakeTestField(7, 3);
            var sources = new NativeList<int2>(Allocator.Persistent);
            var changed = new NativeList<int2>(Allocator.Persistent);
            try
            {
                sources.Add(new int2(6, 1));
                FlowFieldMath.BuildMultiSource(ref field, sources);
                float costBefore = field.costs[field.CellIndex(new int2(0, 1))];

                field.blocked[field.CellIndex(new int2(3, 1))] = 1;
                changed.Add(new int2(3, 1));
                FlowFieldMath.RebuildRegion(ref field, changed, 1);

                float costAfter = field.costs[field.CellIndex(new int2(0, 1))];
                Assert.AreEqual(costBefore, costAfter, "区域外 (0,1) cost 不应变");
            }
            finally { field.Dispose(); sources.Dispose(); changed.Dispose(); }
        }
    }
}
