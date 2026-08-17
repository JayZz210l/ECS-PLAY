using CitizenSim;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace CitizenSim.Tests
{
    public class PoiMathTests
    {
        [Test]
        public void NearestIndex_PicksClosest()
        {
            var pts = new NativeArray<float3>(new[] {
                new float3(10, 0, 0), new float3(1, 0, 0), new float3(20, 0, 0)
            }, Allocator.Persistent);
            try
            {
                Assert.AreEqual(1, PoiMath.NearestIndex(new float3(0, 0, 0), pts));
            }
            finally { pts.Dispose(); }
        }

        [Test]
        public void NearestIndex_EmptyReturnsNegativeOne()
        {
            var pts = new NativeArray<float3>(0, Allocator.Persistent);
            try
            {
                Assert.AreEqual(-1, PoiMath.NearestIndex(new float3(0, 0, 0), pts));
            }
            finally { pts.Dispose(); }
        }

        [Test]
        public void WithinRadius_TrueInside_FalseOutside()
        {
            var pts = new NativeArray<float3>(new[] { new float3(5, 0, 0) }, Allocator.Persistent);
            try
            {
                Assert.IsTrue(PoiMath.WithinRadius(new float3(5.5f, 0, 0), pts, 1f), "0.5 距离应在 1 半径内");
                Assert.IsFalse(PoiMath.WithinRadius(new float3(7, 0, 0), pts, 1f), "2 距离应在 1 半径外");
            }
            finally { pts.Dispose(); }
        }
    }
}
