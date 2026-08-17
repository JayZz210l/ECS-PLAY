using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class SteeringMathTests
    {
        [Test] public void Seek_PointsTowardTarget_AtFullSpeed()
        {
            var v = SteeringMath.Seek(new float3(0, 0, 0), new float3(10, 0, 0), 2f);
            Assert.AreEqual(new float3(2, 0, 0), v);
        }

        [Test] public void Seek_ZeroDistance_ReturnsZero()
        {
            var v = SteeringMath.Seek(new float3(1, 1, 1), new float3(1, 1, 1), 2f);
            Assert.AreEqual(float3.zero, v);
        }

        [Test] public void Arrive_FarFromTarget_FullSpeed()
        {
            var v = SteeringMath.Arrive(new float3(0, 0, 0), new float3(10, 0, 0), 2f, 1f);
            Assert.AreEqual(new float3(2, 0, 0), v);
        }

        [Test] public void Arrive_InsideSlowRadius_SlowedProportionally()
        {
            // dist=0.5, slowRadius=1 -> v = 2 * (0.5/1) = 1
            var v = SteeringMath.Arrive(new float3(9.5f, 0, 0), new float3(10, 0, 0), 2f, 1f);
            Assert.AreEqual(new float3(1, 0, 0), v);
        }

        [Test] public void Arrive_AtTarget_ZeroVelocity()
        {
            var v = SteeringMath.Arrive(new float3(5, 0, 0), new float3(5, 0, 0), 2f, 1f);
            Assert.AreEqual(float3.zero, v);
        }

        [Test] public void Evade_AwayFromThreat_FullSpeed()
        {
            // 威胁在 +x,应朝 -x 全速远离
            var v = SteeringMath.Evade(new float3(0, 0, 0), new float3(10, 0, 0), 2f);
            Assert.AreEqual(new float3(-2, 0, 0), v);
        }

        [Test] public void Evade_AtThreatCenter_ReturnsNonZero()
        {
            // 正好在威胁中心:away 为零向量,必须返回非零任意方向避免卡死
            var v = SteeringMath.Evade(new float3(0, 0, 0), new float3(0, 0, 0), 2f);
            Assert.Greater(math.length(v), 0f, "在威胁中心时 evade 不应返回零向量");
            Assert.AreEqual(2f, math.length(v), 1e-5f, "中心 evade 仍应为全速");
        }

        [Test] public void Evade_Symmetric_OppositeSides()
        {
            // A 在 -x 侧,B 在 +x 侧,威胁中心在原点:两者应各自朝外侧远离(互相远离)
            var forceOnA = SteeringMath.Evade(new float3(-5, 0, 0), new float3(0, 0, 0), 2f);
            var forceOnB = SteeringMath.Evade(new float3(5, 0, 0), new float3(0, 0, 0), 2f);
            Assert.Less(forceOnA.x, 0f, "A 应朝 -x 远离中心");
            Assert.Greater(forceOnB.x, 0f, "B 应朝 +x 远离中心");
        }

        [Test] public void Repulsion_NoNeighbors_Zero()
        {
            var neighbors = new NativeArray<float3>(0, Allocator.Persistent);
            try
            {
                var v = SteeringMath.Repulsion(new float3(0, 0, 0), neighbors, 1f);
                Assert.AreEqual(float3.zero, v);
            }
            finally { neighbors.Dispose(); }
        }

        [Test] public void Repulsion_OneNeighbor_PushesAway()
        {
            // 自己在 (0,0,0),邻居在 (0.5,0,0):排斥力应朝 -x(背离邻居)
            var neighbors = new NativeArray<float3>(new[] { new float3(0.5f, 0, 0) }, Allocator.Persistent);
            try
            {
                var v = SteeringMath.Repulsion(new float3(0, 0, 0), neighbors, 1f);
                Assert.Less(v.x, 0f, "排斥力 x 应为负(背离 +x 的邻居)");
                Assert.AreEqual(0f, v.z, 1e-5f, "z 方向无分量");
            }
            finally { neighbors.Dispose(); }
        }

        [Test] public void Repulsion_FarNeighbor_NotCounted()
        {
            // 邻居在 avoidRadius(1) 之外:不计
            var neighbors = new NativeArray<float3>(new[] { new float3(5, 0, 0) }, Allocator.Persistent);
            try
            {
                var v = SteeringMath.Repulsion(new float3(0, 0, 0), neighbors, 1f);
                Assert.AreEqual(float3.zero, v, "超过 avoidRadius 的邻居不应产生排斥");
            }
            finally { neighbors.Dispose(); }
        }

        [Test] public void Repulsion_Symmetric_TwoCitizens()
        {
            // A 在 (0,0,0),B 在 (1,0,0) 之外... 用半径 2 让两者互斥
            var aNeighbors = new NativeArray<float3>(new[] { new float3(1, 0, 0) }, Allocator.Persistent);
            var bNeighbors = new NativeArray<float3>(new[] { new float3(0, 0, 0) }, Allocator.Persistent);
            try
            {
                var forceOnA = SteeringMath.Repulsion(new float3(0, 0, 0), aNeighbors, 2f);
                var forceOnB = SteeringMath.Repulsion(new float3(1, 0, 0), bNeighbors, 2f);
                Assert.Less(forceOnA.x, 0f, "A 被推向 -x");
                Assert.Greater(forceOnB.x, 0f, "B 被推向 +x");
                Assert.AreEqual(math.length(forceOnA), math.length(forceOnB), 1e-4f, "互斥力大小对称");
            }
            finally { aNeighbors.Dispose(); bNeighbors.Dispose(); }
        }

        // ---------- FlowFieldArrive ----------

        // 造一个 5x5 流场,指定格子的 direction/cost。其余默认 Inf/zero。
        static FlowField MakeFlowFieldWith(int2 cell, float3 dir, float cost)
        {
            var field = new FlowField
            {
                gridSize = new int2(5, 5),
                cellSize = 1f,
                origin = float3.zero,
                directions = new NativeArray<float3>(25, Allocator.Persistent),
                costs = new NativeArray<float>(25, Allocator.Persistent),
                blocked = new NativeArray<byte>(25, Allocator.Persistent),
            };
            for (int i = 0; i < 25; i++) field.costs[i] = FlowFieldMath.Inf;
            int ci = field.CellIndex(cell);
            field.directions[ci] = dir;
            field.costs[ci] = cost;
            return field;
        }

        [Test] public void FlowFieldArrive_FollowsDirection_AtFullSpeed()
        {
            // 格子 (2,2) 方向 (1,0,0),cost=10(远),speed=2 -> (2,0,0)
            var field = MakeFlowFieldWith(new int2(2, 2), new float3(1, 0, 0), 10f);
            try
            {
                var v = SteeringMath.FlowFieldArrive(new float3(2.5f, 0, 2.5f), new int2(2, 2), field, 2f, 4f);
                Assert.AreEqual(new float3(2, 0, 0), v);
            }
            finally { field.Dispose(); }
        }

        [Test] public void FlowFieldArrive_NearTarget_SlowedProportionally()
        {
            // cost=1, slowCost=4, speed=2 -> v = 2 * (1/4) = 0.5
            var field = MakeFlowFieldWith(new int2(2, 2), new float3(1, 0, 0), 1f);
            try
            {
                var v = SteeringMath.FlowFieldArrive(new float3(2.5f, 0, 2.5f), new int2(2, 2), field, 2f, 4f);
                Assert.AreEqual(new float3(0.5f, 0, 0), v);
            }
            finally { field.Dispose(); }
        }

        [Test] public void FlowFieldArrive_Unreachable_Zero()
        {
            // cost=Inf(不可达) -> zero
            var field = MakeFlowFieldWith(new int2(2, 2), new float3(1, 0, 0), FlowFieldMath.Inf);
            try
            {
                var v = SteeringMath.FlowFieldArrive(new float3(2.5f, 0, 2.5f), new int2(2, 2), field, 2f, 4f);
                Assert.AreEqual(float3.zero, v, "不可达格子应返回 zero");
            }
            finally { field.Dispose(); }
        }

        [Test] public void FlowFieldArrive_OutOfBounds_Zero()
        {
            // cell 越界 -> zero
            var field = MakeFlowFieldWith(new int2(2, 2), new float3(1, 0, 0), 10f);
            try
            {
                var v = SteeringMath.FlowFieldArrive(float3.zero, new int2(99, 99), field, 2f, 4f);
                Assert.AreEqual(float3.zero, v, "越界格子应返回 zero");
            }
            finally { field.Dispose(); }
        }

        [Test] public void FlowFieldArrive_AtTargetCost_ZeroVelocity()
        {
            // cost=0(已到目标) -> zero
            var field = MakeFlowFieldWith(new int2(2, 2), float3.zero, 0f);
            try
            {
                var v = SteeringMath.FlowFieldArrive(new float3(2.5f, 0, 2.5f), new int2(2, 2), field, 2f, 4f);
                Assert.AreEqual(float3.zero, v, "cost=0 应返回 zero");
            }
            finally { field.Dispose(); }
        }

        [Test] public void FlowFieldArrive_BlockedCell_EscapesTowardReachableNeighbor()
        {
            // 市民在 (2,2) cost=Inf(被推到障碍物内)。邻居 (3,2) cost=5 可达。
            // 应朝 +x 逃向 (3,2),不卡死。
            var field = MakeFlowFieldWith(new int2(2, 2), float3.zero, FlowFieldMath.Inf);
            try
            {
                field.costs[field.CellIndex(new int2(3, 2))] = 5f;
                var v = SteeringMath.FlowFieldArrive(new float3(2.5f, 0, 2.5f), new int2(2, 2), field, 2f, 4f);
                Assert.IsTrue(v.x > 0.5f, $"被困格子应朝 +x 逃向可达邻居,实际 {v}");
            }
            finally { field.Dispose(); }
        }

        [Test] public void FlowFieldArrive_AllNeighborsBlocked_Zero()
        {
            // 四面被堵(全 blocked 或 Inf) -> 无路可逃,zero
            var field = MakeFlowFieldWith(new int2(2, 2), float3.zero, FlowFieldMath.Inf);
            try
            {
                field.blocked[field.CellIndex(new int2(3, 2))] = 1;
                field.blocked[field.CellIndex(new int2(1, 2))] = 1;
                field.blocked[field.CellIndex(new int2(2, 3))] = 1;
                field.blocked[field.CellIndex(new int2(2, 1))] = 1;
                var v = SteeringMath.FlowFieldArrive(new float3(2.5f, 0, 2.5f), new int2(2, 2), field, 2f, 4f);
                Assert.AreEqual(float3.zero, v, "四面被堵应返回 zero");
            }
            finally { field.Dispose(); }
        }

        // ---------- ObstacleRepulsion ----------

        [Test] public void ObstacleRepulsion_NoObstacles_Zero()
        {
            var obs = new NativeArray<float3>(0, Allocator.Persistent);
            var rads = new NativeArray<float>(0, Allocator.Persistent);
            try
            {
                var v = SteeringMath.ObstacleRepulsion(float3.zero, obs, rads, 0, 1f);
                Assert.AreEqual(float3.zero, v);
            }
            finally { obs.Dispose(); rads.Dispose(); }
        }

        [Test] public void ObstacleRepulsion_OneObstacle_PushesAway()
        {
            // 市民在 (0,0,0),障碍在 (0.5,0,0),半径 2 -> 排斥力朝 -x
            var obs = new NativeArray<float3>(new[] { new float3(0.5f, 0, 0) }, Allocator.Persistent);
            var rads = new NativeArray<float>(new[] { 2f }, Allocator.Persistent);
            try
            {
                var v = SteeringMath.ObstacleRepulsion(float3.zero, obs, rads, 1, 1f);
                Assert.Less(v.x, 0f, "排斥力应朝 -x(背离 +x 的障碍)");
            }
            finally { obs.Dispose(); rads.Dispose(); }
        }

        [Test] public void ObstacleRepulsion_FarObstacle_NotCounted()
        {
            // 障碍在 (10,0,0),半径 2 -> 距离 10 > 2,不计
            var obs = new NativeArray<float3>(new[] { new float3(10, 0, 0) }, Allocator.Persistent);
            var rads = new NativeArray<float>(new[] { 2f }, Allocator.Persistent);
            try
            {
                var v = SteeringMath.ObstacleRepulsion(float3.zero, obs, rads, 1, 1f);
                Assert.AreEqual(float3.zero, v, "超过半径的障碍不应产生排斥");
            }
            finally { obs.Dispose(); rads.Dispose(); }
        }

        [Test] public void ObstacleRepulsion_CountLimitsIterations()
        {
            // 数组有 2 个,但 count=1 -> 只算第一个
            var obs = new NativeArray<float3>(new[] { new float3(0.5f, 0, 0), new float3(-0.5f, 0, 0) }, Allocator.Persistent);
            var rads = new NativeArray<float>(new[] { 2f, 2f }, Allocator.Persistent);
            try
            {
                var v = SteeringMath.ObstacleRepulsion(float3.zero, obs, rads, 1, 1f);
                // 只算第一个 (+x 障碍),排斥朝 -x。第二个 (-x) 不算,无 +x 分量。
                Assert.Less(v.x, 0f, "count=1 只算第一个障碍");
                Assert.AreEqual(0f, v.z, 1e-5f);
            }
            finally { obs.Dispose(); rads.Dispose(); }
        }
    }
}
