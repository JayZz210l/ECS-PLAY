# M2: 行为树接入 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 M1 同步脊柱之上接入 Unity Behavior 包,跑通"需求驱动决策"闭环:市民饥饿 -> BT 决策 SeekFood -> ECS 移动到食物点 -> 到点吃(hunger 降)-> BT 切到 Wander。验证 BT 分片调度有界。状态着色(红=饥饿,绿=漫游)。

**Architecture:** BT 只决策"当前去哪",把 goal 写进 GO(`CitizenAuthoring.currentGoal`),`SnapshotSystem` 下一帧同步进 ECS,移动/满足需求由 ECS 执行。500 个市民共用一个 BehaviorGraph 资产,每 agent 独立 blackboard。BT 低频分片 tick(~17 agent/帧,每市民约 0.5–1s 决策一次);威胁插队在 M4 加。

**Tech Stack:** Unity 6000.5.3f1, Entities 6.5.0, Burst 1.8.29, Mathematics 1.4.0, **com.unity.behavior 1.0.16**(已安装,程序集 `Unity.Behavior`), Unity Test Framework 1.7.0 (NUnit)。

**本计划范围:** 仅 M2(hunger->SeekFood->Wander 闭环 + needs 衰减 + 到点吃 + 分片调度 + 着色)。fatigue/fun/家/娱乐点/空间网格/避障/威胁/刻度盘/profiling 在 M3/M4。

---

## 关键 API 调研结论(已通过 unity_reflect 确认)

| 类型 | 命名空间 | 用途 | 确认状态 |
|---|---|---|---|
| `BehaviorGraphAgent` | `Unity.Behavior` | MonoBehaviour,挂 GO 上跑图;有 `Graph`/`BlackboardReference` 属性,自带 `Update()` | ✅ 已确认 |
| `BehaviorGraph` | `Unity.Behavior` | ScriptableObject 图资产;方法 `Start`/`Tick`/`End`/`Restart`,属性 `IsRunning` | ✅ 已确认(`Tick` 是手动求值入口) |
| `Condition` | `Unity.Behavior` | 抽象基类;override `IsTrue()`,有 `GameObject` 属性(拿 agent GO) | ✅ 已确认 |
| `Action` | `Unity.Behavior` | 抽象基类(基类 `Node`);override 方法为 protected,反射未显示 | ⚠️ 签名待 spike 确认(见 Task 1) |
| `Blackboard`/`BlackboardVariable<T>`/`BlackboardReference` | `Unity.Behavior` | 每 agent 黑板数据 | ✅ 已确认 |
| 节点暴露特性 | ? | `[NodeInfo]`/`[NodeCategory]` 在 1.0.16 反射均未找到 | ⚠️ 特性名待 spike 确认 |

**最大风险(规格 §9.1):** `BehaviorGraphAgent` 自带 `Update()`,疑似每帧自动 tick 图。若不能关掉自动 tick,则无法用调度器按需 `graph.Tick()`。Task 1 spike 必须确认:能否禁用 agent 自动 Update 并由调度器手动 `graph.Tick()`(Design A);若不能,退路是 round-robin 启用/禁用 agent(Design B,略丑但能跑)。

---

## 文件结构(M2 新增/改动)

```
Assets/Scripts/CitizenSim/
  Components/SimComponents.cs          # 改:加 SimNeeds(float3 hunger/fatigue/fun)
  Registry/CitizenAuthoring.cs         # 改:加 needs/thresholds/currentGoal/cached renderer
  Registry/PoiRegistry.cs              # 新:食物/家/娱乐点位置单例(M2 只用食物)
  Math/PoiMath.cs                      # 新:纯函数 NearestFood(pos, foodPositions) 可单测
  Systems/SnapshotSystem.cs            # 改:快照 needs + currentGoal -> SimNeeds/SimGoal
  Systems/ResolveSystem.cs             # 改:回写 needs -> GO
  Systems/NeedsDecaySystem.cs          # 新:IJobEntity+Burst,衰减 hunger;到食物点反向衰减
  Systems/ColoringSystem.cs            # 新:SystemBase 主线程,按 goal.Type 设 MaterialPropertyBlock 颜色
  Behavior/BtScheduler.cs              # 新:MonoBehaviour,分片调 graph.Tick()(Design A/B 由 spike 定)
  Behavior/IsHungryCondition.cs        # 新:Condition,读 CitizenAuthoring.hunger>阈值
  Behavior/SetGoalAction.cs            # 新:Action,算最近食物/随机漫游点,写 currentGoal
  Behavior/BtNodeSpikes.cs             # 新(Task 1):spike 用最小自定义节点,确认 API
Assets/Scripts/CitizenSim.Tests/
  Math/PoiMathTests.cs                 # 新:NearestFood 单测
  Systems/NeedsDecayTests.cs           # 新:衰减 + 到点吃 单测
  Behavior/BtDecisionTests.cs          # 新:SetGoal 决策逻辑(纯函数路径)单测
Assets/Behavior/CitizenBehavior.graph.asset  # 新:BehaviorGraph 资产(hunger->SeekFood->Wander)
Assets/Prefabs/Citizen.prefab          # 改:挂 BehaviorGraphAgent + 绑 graph
Assets/Scenes/CitizenSim/CitizenSimScene.unity  # 改:加食物点 GO + PoiRegistry
```

---

## Task 1: Spike — Unity Behavior tick 机制 + 自定义节点模式(研究,门控调度器)

**Files:**
- Create: `Assets/Scripts/CitizenSim/Behavior/BtNodeSpikes.cs`(spike 用,可保留作模板)
- Create: `docs/superpowers/notes/2026-07-16-m2-bt-spike.md`(spike 结论)

本任务不写正式测试,产出是一份**已验证的决策**与可编译的最小自定义节点。

- [ ] **Step 1: 确认包与最小图**

确认 `com.unity.behavior` 已安装(已确认 `Unity.Behavior` 程序集已加载)。新建空场景或临时场景,放一个空 GO `SpikeAgent`,挂 `BehaviorGraphAgent`。在 Behavior 编辑器窗口新建一个 `BehaviorGraph` 资产,拖进 agent 的 `Graph` 字段。图里放一个内置 Action 节点(如 `Log` 或 `Wait`),Play,确认 agent 自动跑图(`IsRunning=true`)。

- [ ] **Step 2: 确认 tick 机制(Design A 是否可行)**

关键问题:`BehaviorGraphAgent.Update()` 是否每帧自动调 `Graph.Tick()`?能否禁用自动 tick 改由外部调 `Graph.Tick()`?

验证步骤:
1. 用 `unity_reflect` 查 `BehaviorGraphAgent` 是否有 `updateMode`/`tickMode` 之类属性(目前反射只见 `Graph`/`BlackboardReference`)。
2. 写一个测试脚本:把 `SpikeAgent` 的 `BehaviorGraphAgent.enabled = false`,然后每帧从外部调 `agent.Graph.Tick()`。Play,观察图是否仍按外部节奏求值(用图里 `Log` 节点打 log 验证)。
3. 若 `enabled=false` 后 `Graph.Tick()` 仍能求值 -> **Design A 可行**(调度器控 tick)。确认 `Graph.Start()` 是否需手动调(初始化)。
4. 若 `enabled=false` 导致图不初始化或 `Tick` 无效 -> 退路 **Design B**:保持 agent enabled,但 round-robin 把不需要 tick 的 agent 临时 `enabled=false`,每帧只 enable ~17 个。验证 enable/disable 切换是否让图正确暂停/恢复。

- [ ] **Step 3: 确认自定义节点模式**

写最小自定义节点,确认 override 签名与暴露特性:

```csharp
using Unity.Behavior;
using UnityEngine;

namespace CitizenSim.Behavior
{
    // spike:确认 Action 的 override 方法名 + Status 枚举 + 暴露特性
    // 1.0.16 反射未见 [NodeInfo]/[NodeCategory];需在 Behavior 编辑器确认节点是否自动出现在菜单
    [UnityEngine.CreateAssetMenu] // 占位:实际暴露特性以 spike 确认为准
    public class SpikeLogAction : Action
    {
        // 疑似 override(待确认):protected override Status OnStart() / OnUpdate() / OnEnd()
        // 若签名不同,以实际反射/编译为准,更新本文件与 Task 5 模板
    }

    public class SpikeAlwaysTrueCondition : Condition
    {
        // 已确认:Condition 有 IsTrue() + GameObject 属性
        public override bool IsTrue() => true;
    }
}
```

在 Behavior 编辑器新建图,确认 `SpikeLogAction`/`SpikeAlwaysTrueCondition` 是否出现在节点菜单。若不出现,排查暴露特性(查 `Unity.Behavior` 程序集内现有 Action 子类用的是什么特性,例如用 `unity_reflect` 看某个内置 Action 节点的 attribute,或翻包源码)。把确认到的特性名记进 spike 结论。

- [ ] **Step 4: 写 spike 结论文档**

`docs/superpowers/notes/2026-07-16-m2-bt-spike.md` 记录:
- tick 机制结论:Design A 或 B,及具体做法(enabled 怎么设、Tick/Start 怎么调)。
- 自定义节点:Action 的 override 方法名 + Status 枚举名 + 暴露特性名。附可编译的最小模板。
- 已知坑(若有)。

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/CitizenSim/Behavior/BtNodeSpikes.cs docs/superpowers/notes/2026-07-16-m2-bt-spike.md
git commit -m "spike(m2): confirm Unity Behavior manual tick + custom node pattern"
```

> 后续 Task 5/7 的代码以本任务结论为准。若结论与计划代码片段冲突,**以 spike 结论为准**并回填本计划。

---

## Task 2: Needs 数据模型(TDD)

**Files:**
- Modify: `Assets/Scripts/CitizenSim/Components/SimComponents.cs`(加 `SimNeeds`)
- Modify: `Assets/Scripts/CitizenSim/Registry/CitizenAuthoring.cs`(加 needs/thresholds/currentGoal/renderer)
- Modify: `Assets/Scripts/CitizenSim/Systems/SnapshotSystem.cs`(快照 needs + goal)
- Modify: `Assets/Scripts/CitizenSim/Systems/ResolveSystem.cs`(回写 needs)
- Create: `Assets/Scripts/CitizenSim.Tests/Systems/NeedsRoundTripTests.cs`

- [ ] **Step 1: 加 SimNeeds 组件**

在 `SimComponents.cs` 加:

```csharp
// hunger/fatigue/fun,均 0..1。M2 只消费 hunger;fatigue/fun 在 M3。
public struct SimNeeds : IComponentData { public float3 Value; }
```

`CitizenAuthoring` 扩展:

```csharp
public class CitizenAuthoring : MonoBehaviour
{
    public int Index;
    [Header("Needs (0..1)")]
    public Vector3 needs = new Vector3(0f, 0f, 0.5f); // x=hunger,y=fatigue,z=fun
    public float hungerThreshold = 0.7f;
    [Header("Goal (BT 写, ECS 读)")]
    public GoalType currentGoalType = GoalType.Wander;
    public Vector3 currentGoalTarget = Vector3.zero;
    [Header("Visuals")]
    [HideInInspector] public Renderer capsuleRenderer; // Bootstrap 注入
}
```

- [ ] **Step 2: 写失败测试(GO<->ECS needs 往返保真)**

```csharp
[Test] public void Needs_RoundTrip_PreservesValues()
{
    // 建手工 World + Snapshot/Resolve + 一 citizen GO + 镜像 entity(含 SimNeeds)
    // GO.needs = (0.3,0.4,0.5); Snapshot -> SimNeeds; 改 GO.needs=(0.9,0.9,0.9);
    // 再 Snapshot -> Resolve; 断言 SimNeeds.Value == (0.9,0.9,0.9) 且 GO.needs 同
    // (沿用 M1 SyncLoopTests 的手工 World 模式:GetOrCreateSystemManaged + AddSystemToUpdateList)
}
```

(完整 setup 沿用 M1 `SyncLoopTests` 模式:`GetOrCreateSystemManaged<SimulationSystemGroup>()` + `AddSystemToUpdateList()`。)

- [ ] **Step 3: 运行确认失败**

Expected: 编译错(`SimNeeds` 未定义)或测试 FAIL。

- [ ] **Step 4: 改 Snapshot/Resolve 处理 needs**

`SnapshotSystem` 循环里加:

```csharp
em.SetComponentData(ents[i], new SimNeeds { Value = gos[i].GetComponent<CitizenAuthoring>().needs });
em.SetComponentData(ents[i], new SimGoal {
    Type = gos[i].GetComponent<CitizenAuthoring>().currentGoalType,
    Target = gos[i].GetComponent<CitizenAuthoring>().currentGoalTarget });
```

`ResolveSystem` 循环里加(回写 needs,ECS 算完衰减后写回 GO):

```csharp
var needs = em.GetComponentData<SimNeeds>(ents[i]);
gos[i].GetComponent<CitizenAuthoring>().needs = needs.Value;
```

> 性能注:`GetComponent<CitizenAuthoring>()` 每帧 500×2 次调用有缓存开销。M2 先求正确;若 profiler 见瓶颈,在 `CitizenRegistry` 缓存 `CitizenAuthoring[]` 数组(M3 优化)。

- [ ] **Step 5: 运行确认通过 + Commit**

```bash
git add Assets/Scripts/CitizenSim/Components/SimComponents.cs Assets/Scripts/CitizenSim/Registry/CitizenAuthoring.cs Assets/Scripts/CitizenSim/Systems/SnapshotSystem.cs Assets/Scripts/CitizenSim/Systems/ResolveSystem.cs Assets/Scripts/CitizenSim.Tests/Systems/NeedsRoundTripTests.cs
git commit -m "feat(m2): add SimNeeds + GO<->ECS needs/goal round-trip"
```

---

## Task 3: NeedsDecaySystem — 衰减 + 到点吃(TDD)

**Files:**
- Create: `Assets/Scripts/CitizenSim/Registry/PoiRegistry.cs`
- Create: `Assets/Scripts/CitizenSim/Math/PoiMath.cs`
- Create: `Assets/Scripts/CitizenSim/Systems/NeedsDecaySystem.cs`
- Create: `Assets/Scripts/CitizenSim.Tests/Math/PoiMathTests.cs`
- Create: `Assets/Scripts/CitizenSim.Tests/Systems/NeedsDecayTests.cs`

- [ ] **Step 1: PoiRegistry + PoiMath 纯函数**

```csharp
// PoiRegistry:MonoBehaviour 单例,持有食物点位置(M3 扩 home/fun)
public class PoiRegistry : MonoBehaviour
{
    public static PoiRegistry Instance { get; private set; }
    public Transform[] foodPoints;
    void OnEnable() => Instance = this;
    void OnDisable() { if (Instance == this) Instance = null; }
    public Vector3[] GetFoodPositions() { /* 投影 Transform[] -> Vector3[] */ }
}
```

```csharp
public static class PoiMath
{
    // 返回最近食物点的下标;空数组返回 -1(Wander 兜底)
    public static int NearestFoodIndex(float3 pos, NativeArray<float3> foods)
    {
        if (foods.Length == 0) return -1;
        int best = 0; float bestD = math.distancesq(pos, foods[0]);
        for (int i = 1; i < foods.Length; i++)
        {
            float d = math.distancesq(pos, foods[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
    // 到任意食物点距离 <= eatRadius?
    public static bool WithinEatRadius(float3 pos, NativeArray<float3> foods, float eatRadius)
    {
        float r2 = eatRadius * eatRadius;
        for (int i = 0; i < foods.Length; i++)
            if (math.distancesq(pos, foods[i]) <= r2) return true;
        return false;
    }
}
```

- [ ] **Step 2: 写失败测试**

`PoiMathTests`:NearestFoodIndex 选对最近;空数组返回 -1;WithinEatRadius 边界。
`NeedsDecayTests`:手工 World + NeedsDecaySystem + 一个 entity(SimNeeds/SimPosition/SimGoal);`world.SetTime(0.1s)`+`Update`,断言 hunger 增加了 `decayRate*dt`;再设 pos 在食物点 eatRadius 内 + goal=SeekFood,断言 hunger 减少。

- [ ] **Step 3: 运行确认失败**

- [ ] **Step 4: 写 NeedsDecaySystem**

```csharp
[BurstCompile]
public partial struct NeedsDecayJob : IJobEntity
{
    public float dt;
    public float decayRate;     // hunger 每秒增量
    public float eatRate;       // 到点每秒减量
    public float eatRadius;
    [ReadOnly] public NativeArray<float3> foods;
    void Execute(ref SimNeeds needs, in SimPosition pos, in SimGoal goal)
    {
        float hunger = needs.Value.x;
        if (goal.Type == GoalType.SeekFood && PoiMath.WithinEatRadius(pos.Value, foods, eatRadius))
            hunger -= eatRate * dt;        // 吃
        else
            hunger += decayRate * dt;      // 饿
        hunger = math.saturate(hunger);
        needs.Value = new float3(hunger, needs.Value.y, needs.Value.z);
    }
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(SteeringSystem))]
public partial struct NeedsDecaySystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var poi = PoiRegistry.Instance;
        if (poi == null) return;
        var foods = new NativeArray<float3>(poi.GetFoodPositions(), Allocator.TempJob);
        state.Dependency = new NeedsDecayJob
        {
            dt = SystemAPI.Time.DeltaTime,
            decayRate = 0.05f,   // ~20s 从 0 到阈值 1.0(调参在 Inspector/常量)
            eatRate = 0.5f,      // ~2s 吃饱
            eatRadius = 1.0f,
            foods = foods
        }.ScheduleParallel(state.Dependency);
        state.Dependency.Complete();
        foods.Dispose();
    }
}
```

- [ ] **Step 5: 运行确认通过 + Commit**

```bash
git add Assets/Scripts/CitizenSim/Registry/PoiRegistry.cs Assets/Scripts/CitizenSim/Math/PoiMath.cs Assets/Scripts/CitizenSim/Systems/NeedsDecaySystem.cs Assets/Scripts/CitizenSim.Tests/Math/PoiMathTests.cs Assets/Scripts/CitizenSim.Tests/Systems/NeedsDecayTests.cs
git commit -m "feat(m2): NeedsDecaySystem with eat-on-arrival at food POI"
```

---

## Task 4: 场景加食物点 + POI 注册

**Files:**
- Modify: `Assets/Scenes/CitizenSim/CitizenSimScene.unity`

- [ ] **Step 1: 放食物点**

在 `CitizenSimScene` 加 N 个空 GO(如 6 个)`FoodPoint_0..5`,散布在地面上(y=0),如半径 25 圆周。新建一个空 GO `PoiRoot` 挂 `PoiRegistry`,把 6 个 FoodPoint 拖进 `PoiRegistry.foodPoints`。

- [ ] **Step 2: 视觉标记食物点(可选)**

每个 FoodPoint 下放一个小球或圆柱(红/橙色)作可视化,让画面能看出食物位置。无需 ECS。

- [ ] **Step 3: 验证 + Commit**

Play 确认 `PoiRegistry.Instance` 非空、`foodPoints.Length==6`。

```bash
git add Assets/Scenes/CitizenSim/CitizenSimScene.unity
git commit -m "feat(m2): add food POIs and PoiRegistry to scene"
```

---

## Task 5: 自定义 BT 节点 — IsHungry / SetGoal(TDD 决策路径)

**Files:**
- Create: `Assets/Scripts/CitizenSim/Behavior/IsHungryCondition.cs`
- Create: `Assets/Scripts/CitizenSim/Behavior/SetGoalAction.cs`
- Create: `Assets/Scripts/CitizenSim.Tests/Behavior/BtDecisionTests.cs`

> 代码以 **Task 1 spike 结论**为准。下面用已确认的 `Condition.IsTrue`+`GameObject` 模式;Action 的 override 签名/Status 枚举/暴露特性按 spike 回填。

- [ ] **Step 1: IsHungryCondition**

```csharp
using Unity.Behavior;
using UnityEngine;

namespace CitizenSim.Behavior
{
    // [暴露特性:以 spike 为准]
    public class IsHungryCondition : Condition
    {
        public override bool IsTrue()
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            return ca != null && ca.needs.x > ca.hungerThreshold;
        }
    }
}
```

- [ ] **Step 2: SetGoalAction(决策走可单测的纯函数)**

```csharp
using Unity.Behavior;
using Unity.Mathematics;
using UnityEngine;

namespace CitizenSim.Behavior
{
    // [暴露特性:以 spike 为准]
    public class SetGoalAction : Action
    {
        public GoalType goalType; // Inspector/图里设:SeekFood 或 Wander

        // override 签名以 spike 为准,下面是预期形:
        // protected override Status OnUpdate()
        public void Apply() // 抽出可单测的逻辑,节点 override 调它
        {
            var ca = GameObject.GetComponent<CitizenAuthoring>();
            if (ca == null) return;
            if (goalType == GoalType.SeekFood)
            {
                var foods = new NativeArray<float3>(PoiRegistry.Instance.GetFoodPositions(), Allocator.Temp);
                int idx = PoiMath.NearestFoodIndex(ca.transform.position, foods);
                ca.currentGoalType = GoalType.SeekFood;
                ca.currentGoalTarget = idx >= 0 ? (Vector3)foods[idx] : ca.transform.position;
                foods.Dispose();
            }
            else // Wander:附近随机点(M3 加到达重选)
            {
                var r = UnityEngine.Random.insideUnitCircle * 10f;
                ca.currentGoalType = GoalType.Wander;
                ca.currentGoalTarget = ca.transform.position + new Vector3(r.x, 0, r.y);
            }
        }
    }
}
```

- [ ] **Step 3: 写决策测试(纯函数路径)**

`BtDecisionTests`:构造 GO+CitizenAuthoring+PoiRegistry(假食物点),调 `SetGoalAction.Apply()`(或抽出的更纯函数),断言 `currentGoalType`/`currentGoalTarget` 正确:hungry+SeekFood -> 选中最近食物坐标;Wander -> 目标在原点附近 10 半径内。

- [ ] **Step 4: 运行确认通过 + Commit**

```bash
git add Assets/Scripts/CitizenSim/Behavior/ Assets/Scripts/CitizenSim.Tests/Behavior/BtDecisionTests.cs
git commit -m "feat(m2): custom BT nodes IsHungry + SetGoal with decision tests"
```

---

## Task 6: BehaviorGraph 资产(hunger->SeekFood->Wander)

**Files:**
- Create: `Assets/Behavior/CitizenBehavior.graph.asset`

- [ ] **Step 1: 建图**

Behavior 编辑器新建 `BehaviorGraph` 资产 `Assets/Behavior/CitizenBehavior.graph`。树结构(M2 子集,威胁分支 M4 加):

```
Selector (root)
├─ Sequence [饿了]
│   ├─ Condition: IsHungryCondition
│   └─ Action: SetGoalAction(goalType=SeekFood)
└─ Action: SetGoalAction(goalType=Wander)   // 兜底漫游
```

- [ ] **Step 2: 确认节点可拖入**

确认 Task 5 的自定义节点在节点菜单出现(若不出现,回 Task 1 spike 结论修暴露特性)。

- [ ] **Step 3: Commit**

```bash
git add Assets/Behavior/CitizenBehavior.graph.asset
git commit -m "feat(m2): BehaviorGraph asset (hunger->SeekFood->Wander)"
```

---

## Task 7: BT 分片调度器(基于 spike 结论)

**Files:**
- Create: `Assets/Scripts/CitizenSim/Behavior/BtScheduler.cs`

> 以 Task 1 spike 结论为准。下面给 Design A(手动 `graph.Tick()`)主方案;若 spike 判 Design B,改 round-robin `agent.enabled`。

- [ ] **Step 1: BtScheduler MonoBehaviour**

```csharp
using Unity.Behavior;
using UnityEngine;

namespace CitizenSim.Behavior
{
    // 每帧 tick 一批 agent,使每市民约每 0.5–1s 决策一次。500 agent @60fps -> ~17/帧。
    public class BtScheduler : MonoBehaviour
    {
        public static BtScheduler Instance { get; private set; }
        BehaviorGraphAgent[] agents;
        int cursor;

        public void SetAgents(BehaviorGraphAgent[] a)
        {
            agents = a;
            // Design A(spike 确认):禁用 agent 自动 Update,由本调度器控 Tick
            foreach (var ag in a)
            {
                ag.enabled = false;              // 阻止自动 tick(spike 确认可行性)
                ag.Graph.Start();                // 手动初始化(spike 确认是否需要)
            }
        }

        void Update()
        {
            if (agents == null) return;
            int perFrame = Mathf.Max(1, agents.Length / 30); // ~每秒 2 轮 -> 每 agent ~0.5s
            for (int k = 0; k < perFrame; k++)
            {
                if (cursor >= agents.Length) cursor = 0;
                var ag = agents[cursor];
                if (ag != null && ag.Graph != null) ag.Graph.Tick(); // 手动求值
                cursor++;
            }
            // M4:被 Threatened 标记的 agent 在此插队 tick(不受分片节流)。
        }
    }
}
```

- [ ] **Step 2: Bootstrap 注入 agent + scheduler**

`CitizenBootstrap.Spawn()` 里:每个 citizen GO 挂 `BehaviorGraphAgent`,`agent.Graph = CitizenBehavior 资产`,设独立 blackboard(初始 needs)。场景 `SimRoot` 挂 `BtScheduler`,Spawn 后调 `BtScheduler.Instance.SetAgents(...)`。

- [ ] **Step 3: 分片正确性测试(可选,纯逻辑)**

`BtSchedulerTests`:用假 agents 数组验证 cursor round-robin 覆盖全部、perFrame 计算正确。(BT 实际 tick 不在 EditMode 测。)

- [ ] **Step 4: 运行 + Commit**

```bash
git add Assets/Scripts/CitizenSim/Behavior/BtScheduler.cs Assets/Scripts/CitizenSim/Bootstrap/CitizenBootstrap.cs
git commit -m "feat(m2): BT time-sliced scheduler (manual graph.Tick round-robin)"
```

---

## Task 8: 状态着色 + 集成验收

**Files:**
- Create: `Assets/Scripts/CitizenSim/Systems/ColoringSystem.cs`
- Modify: `Assets/Scripts/CitizenSim/Bootstrap/CitizenBootstrap.cs`(注入 renderer)
- Modify: `Assets/Scenes/CitizenSim/CitizenSimScene.unity`(SimRoot 挂 BtScheduler)
- Modify: `Assets/Prefabs/Citizen.prefab`(挂 BehaviorGraphAgent + 绑 graph)

- [ ] **Step 1: ColoringSystem(按 goal.Type 着色)**

```csharp
using Unity.Entities;
using UnityEngine;

namespace CitizenSim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ResolveSystem))]
    public partial class ColoringSystem : SystemBase
    {
        static readonly Color Hungry = new Color(0.9f, 0.2f, 0.2f);
        static readonly Color Wander = new Color(0.2f, 0.8f, 0.2f);
        // M3 再加 Home=蓝 / Fun=黄;M4 Flee=白

        protected override void OnUpdate()
        {
            var reg = CitizenRegistry.Instance;
            if (reg == null) return;
            var em = EntityManager;
            var gos = reg.GameObjects; var ents = reg.Entities;
            var mpb = new MaterialPropertyBlock();
            for (int i = 0; i < gos.Length; i++)
            {
                var ca = gos[i].GetComponent<CitizenAuthoring>();
                if (ca == null || ca.capsuleRenderer == null) continue;
                var goal = em.GetComponentData<SimGoal>(ents[i]);
                mpb.SetColor("_BaseColor", goal.Type == GoalType.SeekFood ? Hungry : Wander);
                ca.capsuleRenderer.SetPropertyBlock(mpb);
            }
        }
    }
}
```

> URP/Lit 的颜色属性名是 `_BaseColor`(确认:若无效改 `_Color` 或用 `manage_material` 查 shader 属性)。500 个 `SetPropertyBlock` 主线程, profiler 见瓶颈再优化(per-instance instanced color 在 M4)。

- [ ] **Step 2: Bootstrap 注入 capsuleRenderer**

Spawn 循环里:`ca.capsuleRenderer = go.transform.Find("Mesh").GetComponent<Renderer>();`(M1 重构后胶囊是 root 下的 Mesh 子物体)。

- [ ] **Step 3: Prefab 挂 BehaviorGraphAgent**

`Citizen.prefab` root 加 `BehaviorGraphAgent`,`Graph = CitizenBehavior.graph`。(blackboard 每 agent 独立,运行时设。)

- [ ] **Step 4: 播放验收**

Run: Play Mode。
Expected:
- 市民初始绿色漫游(Wander)。
- ~10–20s 后 hunger 过阈值 -> 变红,朝最近食物点移动(Arrive)。
- 到食物点 -> 停下吃 -> hunger 降 -> 变绿切回 Wander。
- 循环往复,多股人流在食物点与漫游点间流动。
- HUD 仍 `FPS xxx | Citizens 500`,Console 无报错。
- Profiler:BT ticks/帧 ~17,有界;整帧仍 < 16ms。

Run: Test Runner EditMode 全跑 `CitizenSim.Tests`。
Expected: M1 的 8 + M2 新增(NeedsRoundTrip 1 + PoiMath 若干 + NeedsDecay 若干 + BtDecision 若干)全 PASS。

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/CitizenSim/Systems/ColoringSystem.cs Assets/Scripts/CitizenSim/Bootstrap/CitizenBootstrap.cs Assets/Prefabs/Citizen.prefab Assets/Scenes/CitizenSim/CitizenSimScene.unity
git commit -m "feat(m2): state coloring + BT-driven needs loop acceptance"
```

---

## M2 验收清单

- [ ] 市民饥饿(红)-> 朝最近食物点移动 -> 到点吃 -> 变绿漫游,循环
- [ ] BT 分片生效:Profiler 见 ticks/帧 ~17 有界,非 500 全 tick
- [ ] HUD 正常,Console 无报错
- [ ] EditMode 测试全 PASS(M1 8 + M2 新增)
- [ ] 整帧 < 16ms
- [ ] BT 决策权独占(IsHungry->SetGoal),ECS 只执行;GO 仍是 source of truth

完成后:M2 结束,进入 M3(完整需求 + 空间网格)。M3 详细计划届时另写。

---

## Self-Review

**Spec 覆盖**:M2 覆盖规格 §4(BT 结构 hunger->SeekFood->Wander 子集、自定义节点、共享图+独立 blackboard、BT 时间分片、needs 衰减+到点吃、状态着色)与 §5 管线第 2 段 NeedsDecaySystem。规格 §4 的 fatigue/fun/家/娱乐点/Flee 与 §5 的 SpatialGrid/ThreatDetection 属 M3/M4,不在本计划。-- 一致。

**Spike 门控**:Task 1 产出 tick 机制 + 自定义节点模式决策,Task 5/6/7 依赖其结论。计划已标注"以 spike 为准"处:Action override 签名、Status 枚举名、节点暴露特性名、Design A/B 选择。这是规格 §9.1 风险的落地。

**类型一致性**:`SimNeeds(float3)` 在 Task 2/3 一致;`SimGoal{Type,Target}` 沿用 M1(Task 2 快照 currentGoal->SimGoal,Steering 读 Target 不变,Type 现被 NeedsDecay 消费);`PoiMath.NearestFoodIndex/WithinEatRadius` 在 Task 3/5 一致;`CitizenAuthoring.needs/.currentGoalType/.currentGoalTarget/.capsuleRenderer` 在 Task 2/5/8 一致。

**已知 API 风险**(实现时若报错按此处理):
- `BehaviorGraph.Tick()`/`Start()`/`IsRunning`:已反射确认存在。`agent.enabled=false` 后能否手动 Tick 由 spike 确认;不能则 Design B。
- `Condition.IsTrue()` + `GameObject` 属性:已反射确认。
- `Action` override:反射未显示 protected 方法,以 spike 编译验证为准。
- 节点暴露特性:`[NodeInfo]`/`[NodeCategory]` 反射未找到,spike 查包内现有节点用的特性。
- URP/Lit 颜色属性:`_BaseColor`(无效则 `_Color`)。
- `NativeArray<float3>` 从 `Vector3[]` 构造:`new NativeArray<float3>(vec3Array, Allocator.TempJob)` 需显式转换或逐元素拷贝(实现时确认)。
