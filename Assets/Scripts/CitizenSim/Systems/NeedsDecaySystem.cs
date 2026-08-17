using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 需求衰减:hunger/fatigue 随时间上升、fun 随时间下降;在对应 POI 且目标匹配时反向(吃/睡/玩)。
    // SystemBase 主线程读 PoiRegistry(托管)-> NativeArray,Burst 作业做计算。
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(SteeringSystem))]
    [UpdateAfter(typeof(SnapshotSystem))]
    public partial class NeedsDecaySystem : SystemBase
    {
        public const float HungerRate = 0.008f;  // hunger 每秒增量(慢饿,更少触发觅食)
        public const float EatRate = 0.7f;     // 到食物点每秒减量(快恢复满)
        public const float FatigueRate = 0.015f;// fatigue 每秒增量(慢累,更少回家)
        public const float RestRate = 0.7f;    // 到家每秒减量(快休息满)
        public const float FunDecayRate = 0.025f;// fun 每秒减量(慢无聊)
        public const float PlayRate = 0.7f;    // 到娱乐点每秒增量(快玩满)
        public const float PoIRadius = 4f;

        protected override void OnUpdate()
        {
            var poi = PoiRegistry.Instance;
            var foods = ToNative(poi != null ? poi.GetFoodPositions() : null);
            var homes = ToNative(poi != null ? poi.GetHomePositions() : null);
            var funs  = ToNative(poi != null ? poi.GetFunPositions()  : null);

            float dt = SystemAPI.Time.DeltaTime;
            Dependency = new NeedsDecayJob
            {
                dt = dt,
                poiRadius = PoIRadius,
                hungerRate = HungerRate, eatRate = EatRate,
                fatigueRate = FatigueRate, restRate = RestRate,
                funDecayRate = FunDecayRate, playRate = PlayRate,
                foods = foods, homes = homes, funs = funs,
            }.ScheduleParallel(Dependency);
            Dependency.Complete();
            foods.Dispose();
            homes.Dispose();
            funs.Dispose();
        }

        static NativeArray<float3> ToNative(Vector3[] src)
        {
            int n = src != null ? src.Length : 0;
            var arr = new NativeArray<float3>(n, Allocator.TempJob);
            for (int i = 0; i < n; i++) arr[i] = src[i];
            return arr;
        }
    }

    [BurstCompile]
    public partial struct NeedsDecayJob : IJobEntity
    {
        public float dt;
        public float poiRadius;
        public float hungerRate, eatRate;
        public float fatigueRate, restRate;
        public float funDecayRate, playRate;
        [ReadOnly] public NativeArray<float3> foods;
        [ReadOnly] public NativeArray<float3> homes;
        [ReadOnly] public NativeArray<float3> funs;

        void Execute(ref SimNeeds needs, in SimPosition pos, in SimGoal goal)
        {
            float3 v = needs.Value;
            // hunger:饿 -> 到食物点吃
            bool eating = goal.Type == GoalType.SeekFood && PoiMath.WithinRadius(pos.Value, foods, poiRadius);
            v.x += eating ? -eatRate * dt : hungerRate * dt;
            // fatigue:累 -> 回家休息降
            bool resting = goal.Type == GoalType.SeekHome && PoiMath.WithinRadius(pos.Value, homes, poiRadius);
            v.y += resting ? -restRate * dt : fatigueRate * dt;
            // fun:降 -> 到娱乐点升
            bool playing = goal.Type == GoalType.SeekFun && PoiMath.WithinRadius(pos.Value, funs, poiRadius);
            v.z += playing ? playRate * dt : -funDecayRate * dt;
            needs.Value = math.saturate(v);
        }
    }
}
