# M3: 完整需求 + 空间网格 + 避障 实现计划

> **For agentic workers:** 用 superpowers:executing-plans 逐任务实现。Steps 用 `- [ ]` 跟踪。TDD:先写失败测试 -> 红 -> 实现 -> 绿 -> commit。

**Goal:** 在 M2 闭环之上,把 BT 补成完整日常(fatigue->回家、fun->找娱乐),加空间哈希网格做邻居查询,加局部避障消除穿模,Wander 到达重选。市民有完整的"吃-睡-玩-逛"循环,且不穿模。

**Architecture:** 不变。BT 只决策"去哪"写 GO,ECS 执行移动/需求/避障。GO 仍是 source of truth。新增空间网格作为 ECS 中间量(不回写 GO)。

**Tech Stack:** Unity 6000.5, Entities 6.5, Burst, Unity Behavior 1.0.16。沿用 M1/M2 的 ISystem + IJobEntity + 主线程同步模式。

**本计划范围:** M3 = 完整需求 + 空间网格 + 避障 + Wander 重选 + 着色扩展 + GO 同步缓存优化。**不做**威胁/Flee、刻度盘、HUD v2、Profiling、README(M4)。

**参考:** 规格§4(BT 结构)、§5(管线)、§7 里程碑、§9 风险;M3/M4 路线图。

---

## 关键设计决策

1. **多条件优先级树用嵌套 Conditional Branch**(非规格的 Selector+Sequence)。语义等价(优先 if/else 链),且复用 M2 已验证的 Conditional Branch 构建模式(低风险)。规格的 Selector+Sequence 需要隐藏的 ConditionalGuardModifier,程序化构建复杂。
2. **Wander 重选放在 BT/GoalDecision**(非规格的"ECS 重选")。`SetGoal(Wander)` 保持目标直到到达再换新点。理由:BT 每 ~0.5s 本就要 tick 检查 IsHungry,顺带判断到达代价极低;ECS 重选会拆分 Wander 逻辑且涉及 ECS 写 goal 再回写 GO,更复杂。功能等价。
3. **空间网格用 `NativeMultiHashMap<int2,int>`**(cell->citizenIndex)。每帧主线程 Clear + 并行 Job 填充,SteeringJob 并行读。500 规模足够;5000 在 M4 Profiler 测后决定是否并行化。
4. **滞回(hysteresis)推广到 fatigue/fun**:沿用 M2 的 IsHungry 双阈值模式,防止 fatigue/fun 在阈值附近抖动。

---

## 文件结构(M3 新增/改动)

```
Assets/Scripts/CitizenSim/
  Components/SimComponents.cs          # 改:加 GridCell(int2)
  Registry/CitizenAuthoring.cs         # 改:加 fatigue/bored 阈值
  Registry/PoiRegistry.cs             # 改:加 homePoints/funPoints
  Registry/CitizenRegistry.cs         # 改:缓存 CitizenAuthoring[]/Renderer[]
  Math/PoiMath.cs                     # 改:NearestIndex/WithinRadius 泛化到任意 POI
  Math/SteeringMath.cs                # 改:加 Repulsion(邻居排斥和)
  Systems/NeedsDecaySystem.cs         # 改:fatigue/fun 衰减 + 到点恢复
  Systems/SpatialGridSystem.cs        # 新:建空间哈希网格
  Systems/SteeringSystem.cs           # 改:Arrive + 邻居避障
  Systems/ColoringSystem.cs           # 改:Home=蓝/Fun=黄
  Behavior/GoalDecision.cs            # 改:IsFatigued/IsBored + SetGoal(Home/Fun) + Wander 重选
  Behavior/SeekHomeAction.cs          # 新
  Behavior/SeekFunAction.cs           # 新
  Behavior/IsFatiguedCondition.cs     # 新
  Behavior/IsBoredCondition.cs        # 新
Assets/Editor/CitizenSim/BtGraphBuilder.cs  # 改:树扩 4 条件嵌套
Assets/Scenes/CitizenSim/CitizenSimScene.unity  # 改:加 home/fun POI
Assets/Scripts/CitizenSim.Tests/
  Math/PoiMathTests.cs                # 改:泛化查询
  Math/SteeringMathTests.cs           # 改:Repulsion
  Systems/NeedsDecayTests.cs          # 改:fatigue/fun
  Systems/SpatialGridTests.cs         # 新:网格查询
  Behavior/BtDecisionTests.cs         # 改:IsFatigued/IsBored/SetGoal Home/Fun
```

---

## Task 1: 扩需求与 POI(fatigue/fun + home/fun 点)

**Files:**
- Modify: `PoiRegistry.cs`(homePoints/funPoints)、`PoiMath.cs`(泛化)、`NeedsDecaySystem.cs`(fatigue/fun)、`CitizenAuthoring.cs`(阈值)
- Modify: `PoiMathTests.cs`、`NeedsDecayTests.cs`

- [ ] **Step 1: PoiRegistry 加 home/fun 点**

```csharp
public Transform[] homePoints;
public Transform[] funPoints;
public Vector3[] GetHomePositions() { /* 同 GetFoodPositions */ }
public Vector3[] GetFunPositions() { /* 同 */ }
```

- [ ] **Step 2: PoiMath 泛化**

```csharp
// 任意 POI 数组最近下标(原 NearestFoodIndex 改名/泛化)
public static int NearestIndex(float3 pos, NativeArray<float3> points);
// 任意 POI 数组是否在半径内(原 WithinEatRadius 泛化)
public static bool WithinRadius(float3 pos, NativeArray<float3> points, float radius);
```
保留旧方法名作转发或直接改名(更新调用方)。

- [ ] **Step 3: CitizenAuthoring 加阈值**

```csharp
[Header("Fatigue")] public float fatigueThreshold = 0.7f;  // 累了
public float restedThreshold = 0.0f;                        // 睡饱(滞回)
[Header("Fun")] public float boredThreshold = 0.3f;         // 无聊(fun 低于此)
public float funFullThreshold = 0.9f;                       // 玩够(滞回)
```

- [ ] **Step 4: 写失败测试**
  - `PoiMathTests`:NearestIndex 选最近;空数组返回 -1;WithinRadius 边界。
  - `NeedsDecayTests`:fatigue 随时间上升;SeekHome 且在 home 点 -> fatigue 降;fun 随时间下降;SeekFun 且在 fun 点 -> fun 升。

- [ ] **Step 5: NeedsDecaySystem 扩**

```csharp
// NeedsDecayJob 扩:同时处理 hunger/fatigue/fun
void Execute(ref SimNeeds needs, in SimPosition pos, in SimGoal goal)
{
    float3 v = needs.Value;
    // hunger
    bool eating = goal.Type == GoalType.SeekFood && PoiMath.WithinRadius(pos.Value, foods, eatRadius);
    v.x += eating ? -eatRate*dt : decayRate*dt;
    // fatigue:累 -> 回家降
    bool resting = goal.Type == GoalType.SeekHome && PoiMath.WithinRadius(pos.Value, homes, eatRadius);
    v.y += resting ? -restRate*dt : fatigueRate*dt;
    // fun:降 -> 娱乐升
    bool playing = goal.Type == GoalType.SeekFun && PoiMath.WithinRadius(pos.Value, funs, eatRadius);
    v.z += playing ? playRate*dt : -funDecayRate*dt;
    needs.Value = math.saturate(v);
}
```
速率常量:fatigueRate=0.04/s, restRate=0.1/s, funDecayRate=0.03/s, playRate=0.1/s(可调)。

- [ ] **Step 6: 运行通过 + Commit**
```bash
git add <Task1 文件 + .meta>
git commit -m "feat(m3): fatigue/fun needs + home/fun POIs + decay"
```

---

## Task 2: 扩 BT(Fatigued/Bored 分支)

**Files:**
- Modify: `GoalDecision.cs`、`BtGraphBuilder.cs`
- Create: `SeekHomeAction.cs`、`SeekFunAction.cs`、`IsFatiguedCondition.cs`、`IsBoredCondition.cs`
- Modify: `BtDecisionTests.cs`

- [ ] **Step 1: GoalDecision 扩**

```csharp
// 滞回:SeekHome 时吃到 restedThreshold 才停;否则 fatigue > fatigueThreshold
public static bool IsFatigued(CitizenAuthoring ca) {
    if (ca == null) return false;
    if (ca.currentGoalType == GoalType.SeekHome) return ca.needs.y > ca.restedThreshold;
    return ca.needs.y > ca.fatigueThreshold;
}
// 滞回:SeekFun 时玩到 funFullThreshold 才停;否则 fun < boredThreshold
public static bool IsBored(CitizenAuthoring ca) {
    if (ca == null) return false;
    if (ca.currentGoalType == GoalType.SeekFun) return ca.needs.z < ca.funFullThreshold;
    return ca.needs.z < ca.boredThreshold;
}
// SetGoal 扩 SeekHome/SeekFun:选最近 home/fun 点(复用 PoiMath.NearestIndex)
// SetGoal(Wander) 加到达重选:还在走向当前 Wander 目标就保持,到达再换新点
```

- [ ] **Step 2: 新 Action/Condition 节点**

`SeekHomeAction`/`SeekFunAction`:仿 `SeekFoodAction`,`[NodeDescription(..., id:"唯一")]`,OnStart 调 `GoalDecision.SetGoal(ca, SeekHome/SeekFun, homes/funs)`。
`IsFatiguedCondition`/`IsBoredCondition`:仿 `IsHungryCondition`,`[Condition(..., id:"唯一")]`,`IsTrue()` 调 `GoalDecision.IsFatigued/IsBored`。

- [ ] **Step 3: 写失败测试**
  - `BtDecisionTests`:IsFatigued 滞回;IsBored 滞回;SetGoal(SeekHome) 选最近 home;SetGoal(SeekFun) 选最近 fun;SetGoal(Wander) 到达前保持目标、到达后换新点。

- [ ] **Step 4: BtGraphBuilder 扩树(嵌套 Conditional Branch)**

```
Start(Repeat)
  -> Conditional Branch [IsHungry]
       True  -> SeekFood
       False -> Conditional Branch [IsFatigued]
                  True  -> SeekHome
                  False -> Conditional Branch [IsBored]
                             True  -> SeekFun
                             False -> Wander
```
程序化构建:在现有 builder 基础上,把最外层 False 端口连到新的内层 Branch(嵌套 3 层)。每个 Branch 加对应 Condition。

- [ ] **Step 5: 运行通过 + 重建图资产(Force Rebuild 菜单)+ Commit**
```bash
git add <Task2 文件 + .meta + CitizenBehavior.asset>
git commit -m "feat(m3): Fatigued/Bored BT branches + Wander repick"
```

---

## Task 3: 空间哈希网格

**Files:**
- Modify: `SimComponents.cs`(加 GridCell)
- Create: `SpatialGridSystem.cs`
- Create: `SpatialGridTests.cs`

- [ ] **Step 1: GridCell 组件**

```csharp
public struct GridCell : IComponentData { public int2 Value; }
```
Bootstrap archetype 加 GridCell。

- [ ] **Step 2: SpatialGridSystem 设计**

```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SnapshotSystem))]
[UpdateBefore(typeof(SteeringSystem))]
public partial class SpatialGridSystem : ISystem
{
    NativeMultiHashMap<int2, int> grid;   // cell -> citizenIndex
    NativeArray<float3> positions;        // by citizenIndex
    int count;
    const float CellSize = 1.0f;          // ~避障半径×2

    // OnUpdate: 主线程 Clear grid -> 并行 BuildGridJob 填充 + 写 positions + 写 GridCell
}
```

BuildGridJob(IJobEntity):拿 (SimPosition, CitizenIndex),算 `cell = (int2)floor(pos/CellSize)`,写 `GridCell`,写 `positions[index]=pos`,并行 `grid.ParallelWriter.Add(cell, index)`。

- [ ] **Step 3: 写失败测试**
  - `SpatialGridTests`:手工建 grid,查某 cell 的邻居索引正确;9 邻域查询返回预期范围内的索引;空 cell 返回空。

- [ ] **Step 4: 实现 SpatialGridSystem**

注意:NativeMultiHashMap 并行写要先 Clear(主线程)。count 变化时重分配 grid/positions。Burst Compile。

- [ ] **Step 5: 运行通过 + Commit**
```bash
git add <Task3 文件 + .meta>
git commit -m "feat(m3): spatial hash grid system"
```

---

## Task 4: 局部避障 + Wander 重选

**Files:**
- Modify: `SteeringMath.cs`(Repulsion)、`SteeringSystem.cs`(避障)
- Modify: `GoalDecision.cs`(Wander 重选,Task 2 已做,此处只验证)
- Modify: `SteeringMathTests.cs`、`SteeringSystemTests.cs`

- [ ] **Step 1: SteeringMath.Repulsion**

```csharp
// 从邻居位置累加排斥力。neighbors 是半径内的其他市民位置。
// 排斥 = sum( normalize(pos - other) / max(dist, eps) ),dist < avoidRadius 才计。
public static float3 Repulsion(float3 pos, NativeArray<float3> neighbors, float avoidRadius, float maxStrength)
```

- [ ] **Step 2: 写失败测试**
  - `SteeringMathTests`:Repulsion 无邻居=0;一个近邻居产生反向力;对称(两个市民互斥力反向);远邻居(>radius)不计。

- [ ] **Step 3: SteeringSystem 加避障**

```csharp
// SteeringJob 增参:[ReadOnly] grid, [ReadOnly] positions, avoidRadius, avoidStrength
void Execute(ref SimVelocity vel, in SimPosition pos, in SimGoal goal, in GridCell cell, in CitizenIndex idx)
{
    float3 arrive = SteeringMath.Arrive(pos.Value, goal.Target, Speed, SlowRadius);
    // 查 9 邻域 cell,收集邻居位置(跳过自己),算排斥
    float3 rep = QueryRepulsion(cell.Value, idx.Value, pos.Value, grid, positions, avoidRadius);
    float3 v = arrive + rep * avoidStrength;
    vel.Value = math.select(v, math.normalizesafe(v)*Speed, math.lengthsq(v) > Speed*Speed); // 限速
}
```
9 邻域查询在 Job 内用 `grid.GetValues(cell)` 迭代。

- [ ] **Step 4: 运行通过 + Play 调参**
  - EditMode 测试绿。
  - Play 看避障:市民不穿模(录像/肉眼)。avoidStrength/avoidRadius 调到不抖动且不穿模。

- [ ] **Step 5: Commit**
```bash
git add <Task4 文件 + .meta>
git commit -m "feat(m3): neighbor avoidance via spatial grid"
```

---

## Task 5: 着色扩展 + GO 同步缓存

**Files:**
- Modify: `ColoringSystem.cs`、`CitizenRegistry.cs`、`SnapshotSystem.cs`、`ResolveSystem.cs`、`CitizenBootstrap.cs`

- [ ] **Step 1: CitizenRegistry 缓存数组**

```csharp
[NonSerialized] public CitizenAuthoring[] Authoring;  // 缓存,消掉 per-frame GetComponent
[NonSerialized] public Renderer[] Renderers;
public void Register(GameObject[] gos, Entity[] ents) {
    // 填充 Authoring[i] = gos[i].GetComponent<CitizenAuthoring>();
    // 填充 Renderers[i] = gos[i].transform.Find("Mesh")?.GetComponent<Renderer>(); (或用 ca.capsuleRenderer)
}
```

- [ ] **Step 2: ColoringSystem 用缓存 + 扩色**

```csharp
static readonly Color Home = new(0.2f, 0.4f, 0.9f);   // 蓝
static readonly Color Fun = new(0.9f, 0.8f, 0.2f);    // 黄
// 按 goal.Type switch: SeekFood->红, SeekHome->蓝, SeekFun->黄, Wander->绿
// 用 reg.Renderers[i] 替代 go.GetComponent<Renderer>
```

- [ ] **Step 3: Snapshot/Resolve 用缓存**

`SnapshotSystem`/`ResolveSystem`/`ColoringSystem` 用 `reg.Authoring[i]` 替代 `gos[i].GetComponent<CitizenAuthoring>()`。

- [ ] **Step 4: 运行 + Play 确认着色 + Commit**
```bash
git add <Task5 文件 + .meta>
git commit -m "feat(m3): Home/Fun coloring + registry array caching"
```

---

## Task 6: 场景 POI + 验收

**Files:**
- Modify: `CitizenSimScene.unity`(加 home/fun POI GO,挂 PoiRegistry)

- [ ] **Step 1: 场景加 home/fun 点**
  - 加 N 个 home 点(蓝标)、M 个 fun 点(黄标),散布地图。
  - PoiRegistry 拖入 homePoints/funPoints。

- [ ] **Step 2: Play 验收**
  - 吃-睡-玩-逛循环可见:市民颜色切换(红->食物->绿->...蓝->家->...黄->娱乐->绿)。
  - 不穿模(避障生效)。
  - Wander 是航点漫游(走到点再换),不再原地抖。
  - HUD 正常,Console 无报错。
  - EditMode 全测试 PASS(M1+M2+M3)。

- [ ] **Step 3: Profiler 抽测(可选,M4 正式做)**
  - 500 规模 SpatialGridSystem 构建 < 1ms?整帧 < 16ms?记录数字。

- [ ] **Step 4: Commit**
```bash
git add Assets/Scenes/CitizenSim/CitizenSimScene.unity
git commit -m "feat(m3): scene home/fun POIs + acceptance"
```

---

## M3 验收清单

- [ ] 完整日常循环:饿(红)->食物->饱;累(蓝)->家->休息;无聊(黄)->娱乐->玩够;闲(绿)->漫游
- [ ] 市民不穿模(邻居避障生效)
- [ ] Wander 航点漫游(到达重选,不原地抖)
- [ ] BT 优先级正确:Threat(M4)>Hunger>Fatigue>Bored>Wander(M3 不含 Threat)
- [ ] GO 同步无 per-frame GetComponent(缓存数组)
- [ ] EditMode 测试全 PASS(M1+M2+M3)
- [ ] Console 无报错
- [ ] 500 规模整帧 < 16ms(抽测)

---

## 风险 / 决策点

1. **空间网格性能(§9.3)**:500 规模 NativeMultiHashMap 构建 + 9 邻域查询应 <1ms。Task 3 后 Profiler 抽测,若 >2ms 考虑并行构建/更粗格子。**影响 M4 的 5000 刻度。**
2. **避障参数**:avoidStrength/avoidRadius 需 Play 调参,过强抖动、过弱穿模。Task 4 录像确认。
3. **NativeMultiHashMap 并发**:Clear 必须主线程,填充用 ParallelWriter,读用 GetValues(并发读安全)。注意 Dispose 时机。
4. **BT 嵌套 Branch 深度**:3 层嵌套构建/可读性尚可;M4 加 Threat 变 4 层,届时评估是否改 Selector+Guard。

## Self-Review

**Spec 覆盖**:M3 覆盖规格§4 BT 完整结构(Hunger/Fatigue/Bored/Wander 子集,Threat 在 M4)、§5 管线第 3 段(SpatialGridSystem)、§5 第 5 段(SteeringSystem 避障)、§2 着色(蓝/黄)。规格§4 的 Selector+Sequence 改嵌套 Conditional Branch(语义等价,决策见上)。规格"Wander ECS 重选"改 BT 重选(决策见上)。

**类型一致性**:`SimNeeds(float3 x/y/z = hunger/fatigue/fun)` 在 Task 1/2 一致;`GoalType.SeekHome/SeekFun` 沿用 M1 定义;`GridCell(int2)` 新增,Bootstrap archetype 同步;`PoiMath.NearestIndex/WithinRadius` 泛化,NeedsDecay/SetGoal 共用;`CitizenRegistry.Authoring[]/Renderers[]` 缓存,Snapshot/Resolve/Coloring 共用。

**已知 API 风险**:
- `NativeMultiHashMap<int2,int>.ParallelWriter.Add` + `GetValues` 在 Burst Job 内:标准用法,确认签名。
- `BtGraphBuilder` 嵌套 Branch 构建:复用 M2 的 CreateNode + FindPortModelByName("True"/"False") 模式,低风险。
- 新节点 `[NodeDescription]`/`[Condition]` 的 `id` 必须唯一(M2 踩坑:默认 "" 冲突)。
