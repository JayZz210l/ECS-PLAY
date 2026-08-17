using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class ThreatDetectionTests
    {
        [Test]
        public void IsThreatened_InsideRadius_True()
        {
            var zones = new NativeArray<float3>(1, Allocator.Temp);
            zones[0] = new float3(0, 0, 0);
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(1, 0, 0), zones, 5f), "距中心 1 < 5 应受威胁");
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(4.9f, 0, 0), zones, 5f), "距中心 4.9 < 5 应受威胁");
            zones.Dispose();
        }

        [Test]
        public void IsThreatened_OutsideRadius_False()
        {
            var zones = new NativeArray<float3>(1, Allocator.Temp);
            zones[0] = new float3(0, 0, 0);
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(5.1f, 0, 0), zones, 5f), "距中心 5.1 > 5 不受威胁");
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(100, 0, 0), zones, 5f), "远离不受威胁");
            zones.Dispose();
        }

        [Test]
        public void IsThreatened_Boundary_Exclusive()
        {
            var zones = new NativeArray<float3>(1, Allocator.Temp);
            zones[0] = new float3(0, 0, 0);
            // 距离正好 = radius:lenSq = 25,< 25 为 false(严格小于,边界不算在内)
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(5, 0, 0), zones, 5f), "边界距离=radius 不算受威胁");
            zones.Dispose();
        }

        [Test]
        public void IsThreatened_MultipleZones_AnyHit_True()
        {
            var zones = new NativeArray<float3>(2, Allocator.Temp);
            zones[0] = new float3(0, 0, 0);
            zones[1] = new float3(100, 0, 0);
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(100.5f, 0, 0), zones, 5f), "命中第二区");
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(0.5f, 0, 0), zones, 5f), "命中第一区");
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(50, 0, 0), zones, 5f), "两区之间不受威胁");
            zones.Dispose();
        }

        [Test]
        public void IsThreatened_NoZonesOrZeroRadius_False()
        {
            var empty = new NativeArray<float3>(0, Allocator.Temp);
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(0, 0, 0), empty, 5f), "空威胁区不受威胁");
            empty.Dispose();

            var zones = new NativeArray<float3>(1, Allocator.Temp);
            zones[0] = new float3(0, 0, 0);
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(0, 0, 0), zones, 0f), "radius=0 不受威胁");
            zones.Dispose();
        }

        // 每区域独立半径(临时恐惧区 15m 与常驻区 5m 共存)。
        [Test]
        public void IsThreatened_PerZoneRadius_DifferentRadii()
        {
            var zones = new NativeArray<float3>(2, Allocator.Temp);
            var radii = new NativeArray<float>(2, Allocator.Temp);
            zones[0] = new float3(0, 0, 0);   radii[0] = 5f;   // 常驻 5m
            zones[1] = new float3(100, 0, 0); radii[1] = 15f;  // 临时 15m
            // 距第一区 6m:超出 5m 常驻,但也不在第二区范围 -> false
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(6, 0, 0), zones, radii), "6m 超出 5m 常驻区");
            // 距第二区 10m:在 15m 临时区内 -> true(若用全局 5m 则为 false)
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(110, 0, 0), zones, radii), "10m 在 15m 临时区内");
            // 距第二区 16m:超出 15m -> false
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(116, 0, 0), zones, radii), "16m 超出 15m 临时区");
            zones.Dispose();
            radii.Dispose();
        }

        [Test]
        public void IsThreatened_PerZoneRadius_ZeroRadiusSkipped()
        {
            var zones = new NativeArray<float3>(2, Allocator.Temp);
            var radii = new NativeArray<float>(2, Allocator.Temp);
            zones[0] = new float3(0, 0, 0); radii[0] = 0f;   // 半径 0 跳过
            zones[1] = new float3(10, 0, 0); radii[1] = 5f;
            // 正在第一区中心,但半径 0 -> false;命中第二区边缘
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(0, 0, 0), zones, radii), "半径 0 的区域跳过");
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(12, 0, 0), zones, radii), "命中第二区");
            zones.Dispose();
            radii.Dispose();
        }

        [Test]
        public void IsThreatened_PerZoneRadius_Empty_False()
        {
            var emptyZones = new NativeArray<float3>(0, Allocator.Temp);
            var emptyRadii = new NativeArray<float>(0, Allocator.Temp);
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(0, 0, 0), emptyZones, emptyRadii), "空威胁区不受威胁");
            emptyZones.Dispose();
            emptyRadii.Dispose();
        }

        // 滞回因子:factor>1 扩大判定半径。模拟"已 threatened 用 exit 阈值"防边界抖动。
        [Test]
        public void IsThreatened_HysteresisFactor_WidensRadius()
        {
            var zones = new NativeArray<float3>(1, Allocator.Temp);
            var radii = new NativeArray<float>(1, Allocator.Temp);
            zones[0] = new float3(0, 0, 0); radii[0] = 5f;
            // 距离 6:factor=1 超出 5m(enter 阈值)-> false;factor=1.3 在 6.5m 内(exit 阈值)-> true
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(6, 0, 0), zones, radii, 1f), "6m 超出 enter 阈值 5m");
            Assert.IsTrue(ThreatMath.IsThreatened(new float3(6, 0, 0), zones, radii, 1.3f), "6m 在 exit 阈值 6.5m 内(滞回带)");
            // 距离 7:factor=1.3 超出 6.5m -> false(已逃出滞回带,可解除)
            Assert.IsFalse(ThreatMath.IsThreatened(new float3(7, 0, 0), zones, radii, 1.3f), "7m 超出 exit 阈值 6.5m");
            zones.Dispose();
            radii.Dispose();
        }
    }
}
