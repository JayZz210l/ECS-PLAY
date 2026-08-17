using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim
{
    // 威胁检测(规格§5 第 4 段、§6 "enableable 组件免 archetype 碎片")。
    // 每帧查威胁半径,设 Threatened enableable bit(零 archetype 变更:仅翻转 chunk 内 enabled 位)。
    //
    // 关键:迭代按 SimPosition+CitizenIndex(全体市民),不按 Threatened bit--否则 bit=disabled 的
    // 市民会从查询里消失,再也进不了检测。Threatened 仅在主线程 SetComponentEnabled 时碰。
    //
    // 流程:Burst job 算 threatened flags(by CitizenIndex)-> 主线程循环 SetComponentEnabled(同帧生效)
    // -> ResolveSystem 把 bit 镜像回 ca.threatened 供 BT 读。
    //
    // 每区域独立半径:常驻 zones(全局 radius)+ 临时恐惧区(各自 radius,如 15m)共存。
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SnapshotSystem))]
    [UpdateBefore(typeof(SteeringSystem))]
    public partial struct ThreatDetectionSystem : ISystem
    {
        NativeArray<bool> flagsEnter;  // dist < radius(进入阈值),by CitizenIndex
        NativeArray<bool> flagsExit;   // dist < radius*1.3(退出阈值,滞回带),by CitizenIndex
        int count;
        EntityQuery m_Query;

        // 主线程复用的托管缓存,免每帧 new List 的 GC(GetActiveZones 内部 Clear)。
        static readonly List<Vector3> s_Pos = new List<Vector3>();
        static readonly List<float> s_Rad = new List<float>();

        public void OnCreate(ref SystemState state)
        {
            m_Query = state.GetEntityQuery(
                ComponentType.ReadOnly<SimPosition>(),
                ComponentType.ReadOnly<CitizenIndex>());
        }

        public void OnDestroy(ref SystemState state)
        {
            if (flagsEnter.IsCreated) flagsEnter.Dispose();
            if (flagsExit.IsCreated) flagsExit.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var reg = ThreatZoneRegistry.Instance;
            if (reg != null) reg.GetActiveZones(s_Pos, s_Rad);
            else { s_Pos.Clear(); s_Rad.Clear(); }

            var zones = ToNativeFloat3(s_Pos);
            var radii = ToNativeFloat(s_Rad);

            int n = m_Query.CalculateEntityCount();
            if (!flagsEnter.IsCreated || n != count)
            {
                if (flagsEnter.IsCreated) flagsEnter.Dispose();
                if (flagsExit.IsCreated) flagsExit.Dispose();
                flagsEnter = new NativeArray<bool>(math.max(1, n), Allocator.Persistent);
                flagsExit = new NativeArray<bool>(math.max(1, n), Allocator.Persistent);
                count = n;
            }

            state.Dependency = new ThreatJob
            {
                zones = zones,
                radii = radii,
                flagsEnter = flagsEnter,
                flagsExit = flagsExit,
            }.ScheduleParallel(m_Query, state.Dependency);
            state.Dependency.Complete();

            // 主线程 apply enableable bit(零 archetype 变更)。空间滞回:已 threatened 用 exit 阈值
            // (radius*1.3,更宽容),未 threatened 用 enter 阈值(radius)。逃出区后仍保持 Flee 直到 1.3 倍半径外,
            // 避免边界 Flee/Seek 反复横跳(与 hunger/fatigue/fun 滞回同哲学)。
            var em = state.EntityManager;
            var entities = m_Query.ToEntityArray(Allocator.Temp);
            var indices = m_Query.ToComponentDataArray<CitizenIndex>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                int idx = indices[i].Value;
                bool was = em.IsComponentEnabled<Threatened>(entities[i]);
                bool now = was ? flagsExit[idx] : flagsEnter[idx];
                em.SetComponentEnabled<Threatened>(entities[i], now);
            }
            entities.Dispose();
            indices.Dispose();
            zones.Dispose();
            radii.Dispose();
        }

        static NativeArray<float3> ToNativeFloat3(List<Vector3> src)
        {
            var arr = new NativeArray<float3>(src.Count, Allocator.TempJob);
            for (int i = 0; i < src.Count; i++) arr[i] = src[i];
            return arr;
        }

        static NativeArray<float> ToNativeFloat(List<float> src)
        {
            var arr = new NativeArray<float>(src.Count, Allocator.TempJob);
            for (int i = 0; i < src.Count; i++) arr[i] = src[i];
            return arr;
        }
    }

    [BurstCompile]
    public partial struct ThreatJob : IJobEntity
    {
        [ReadOnly] public NativeArray<float3> zones;
        [ReadOnly] public NativeArray<float> radii;
        const float kHysteresis = 1.3f;  // 退出半径 = 进入半径 * 1.3(空间滞回防边界抖动)
        // 每个市民写自己唯一的 idx,CitizenIndex 与 job 迭代下标不一致,禁用并行写索引检查。
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<bool> flagsEnter;
        [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<bool> flagsExit;

        void Execute(in SimPosition pos, in CitizenIndex idx)
        {
            flagsEnter[idx.Value] = ThreatMath.IsThreatened(pos.Value, zones, radii, 1f);
            flagsExit[idx.Value] = ThreatMath.IsThreatened(pos.Value, zones, radii, kHysteresis);
        }
    }
}
