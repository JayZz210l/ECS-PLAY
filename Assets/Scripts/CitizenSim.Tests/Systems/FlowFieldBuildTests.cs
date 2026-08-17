using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class FlowFieldBuildTests
    {
        // 造测试用小网格。cellSize=1, origin=zero。
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

        [Test] public void MarkBlockedRect_MarksSingleCellAtCellCenter()
        {
            // 障碍物在格子 (0,0) 中心 (0.5,0,0.5),size 1x1 -> 只覆盖 (0,0)
            var field = MakeTestField(5, 5);
            try
            {
                ObstacleRegistry.MarkBlockedRect(ref field, new float3(0.5f, 0, 0.5f), new float2(1f, 1f));
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(0, 0))], "(0,0) 应被标记");
                Assert.AreEqual(0, field.blocked[field.CellIndex(new int2(1, 0))], "(1,0) 不应被标记");
                Assert.AreEqual(0, field.blocked[field.CellIndex(new int2(0, 1))], "(0,1) 不应被标记");
            }
            finally { field.Dispose(); }
        }

        [Test] public void MarkBlockedRect_TwoByTwo_MarksFourCells()
        {
            // 障碍物在格子角点 (2,0,2),size 2x2 -> 覆盖 (1,1)(2,1)(1,2)(2,2)
            var field = MakeTestField(5, 5);
            try
            {
                ObstacleRegistry.MarkBlockedRect(ref field, new float3(2f, 0, 2f), new float2(2f, 2f));
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(1, 1))]);
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(2, 1))]);
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(1, 2))]);
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(2, 2))]);
                Assert.AreEqual(0, field.blocked[field.CellIndex(new int2(3, 2))], "(3,2) 不应被标记");
            }
            finally { field.Dispose(); }
        }

        [Test] public void MarkBlockedRect_OutOfBounds_Clamped()
        {
            // 障碍物部分越界:中心在 (0.5,0,0.5),size 2x2 -> 覆盖 (-1..1) 范围,越界部分忽略
            var field = MakeTestField(5, 5);
            try
            {
                ObstacleRegistry.MarkBlockedRect(ref field, new float3(0.5f, 0, 0.5f), new float2(2f, 2f));
                // 只标记 in-bounds 的 (0,0)(1,0)(0,1)(1,1)
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(0, 0))]);
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(1, 1))]);
                // 不崩溃即可
            }
            finally { field.Dispose(); }
        }

        [Test] public void MarkBlockedRect_Rotated90_FlipsOrientation()
        {
            // 6x2 矩形旋转 90°:长边从 x 轴转到 z 轴,blocked 应成竖条而非横条。
            // 中心 (2.5,0,2.5),6x2,旋转 90°:覆盖 z=1..3 且 x=1..3(约 3x3)。
            var field = MakeTestField(6, 6);
            try
            {
                ObstacleRegistry.MarkBlockedRect(ref field, new float3(2.5f, 0, 2.5f), new float2(6f, 2f), 90f);

                // 旋转后:z 方向延伸(竖条),x 方向窄(2 宽)
                // (2,1) 应被标记(z 方向延伸), (1,2) 也应被标记
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(2, 1))], "旋转90后 z 方向应延伸(竖条)");
                Assert.AreEqual(1, field.blocked[field.CellIndex(new int2(2, 3))], "旋转90后 z 方向应延伸");
                // 与未旋转对比:x 方向不应延伸到 4(横条才这样)
                Assert.AreEqual(0, field.blocked[field.CellIndex(new int2(4, 2))], "旋转90后 x 方向应窄,不延伸");
            }
            finally { field.Dispose(); }
        }

        [Test] public void MarkBlockedRect_Rotated_TouchesEdge_Marks()
        {
            // 旋转矩形只擦到格子一角:中心判断会漏,相交判断应标记。
            // 中心 (1.9,0,1.9),size 0.2x0.2 旋转 45° -> 矩形极小的对角菱形,
            // 恰好在 (1,1)(2,1)(1,2)(2,2) 四格交点的正中间。旧中心判断全部漏掉;
            // 相交判断应至少标记一些擦到的格子(不要求精确,验证不保守)。
            var field = MakeTestField(5, 5);
            try
            {
                ObstacleRegistry.MarkBlockedRect(ref field, new float3(2f, 0, 2f), new float2(0.2f, 0.2f), 45f);
                // 中心 (2,0,2) 处的小菱形,格子中心 (2.5,0,2.5) 离它较远,
                // 但格子 (1,1) 的角点 (1.5,0,1.5) 接近矩形中心 (2,0,2),可能擦到。
                // 关键断言:至少有一个格子被标记(不保守全空)。
                int marked = 0;
                for (int i = 0; i < field.blocked.Length; i++) if (field.blocked[i] == 1) marked++;
                Assert.Greater(marked, 0, "旋转矩形擦到格子角落时应至少标记 1 格");
            }
            finally { field.Dispose(); }
        }

        [Test] public void ObstacleDetour_MultiSourceBypassesBlockedCell()
        {
            // 5x3 网格,食物源在 (4,0)。(2,0) 是障碍。(0,0) 应绕道到达(不被堵死)。
            var field = MakeTestField(5, 3);
            var sources = new NativeList<int2>(Allocator.Persistent);
            try
            {
                ObstacleRegistry.MarkBlockedRect(ref field, new float3(2.5f, 0, 0.5f), new float2(1f, 1f));
                sources.Add(new int2(4, 0));
                FlowFieldMath.BuildMultiSource(ref field, sources);

                // (0,0) 应可达(绕道)
                float cost00 = field.costs[field.CellIndex(new int2(0, 0))];
                Assert.IsTrue(cost00 < 1e8f, $"(0,0) 应绕道可达,cost={cost00}");

                // 障碍格子不可达
                Assert.IsTrue(field.costs[field.CellIndex(new int2(2, 0))] >= 1e8f, "障碍格子应 INF");
            }
            finally { field.Dispose(); sources.Dispose(); }
        }
    }
}
