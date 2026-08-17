# M4: 威胁 + 刻度盘 + HUD v2 + Profiling + 交付 实现计划

> **For agentic workers:** 用 superpowers:executing-plans 逐任务实现。Steps 用 `- [ ]` 跟踪。TDD:先写失败测试 -> 红 -> 实现 -> 绿 -> commit。

**Goal:** 在 M3 完整日常闭环之上,加威胁区与 Flee 闭环(ECS 每帧检测 + enableable 标记 + BT 插队 + evade 转向),开放 100/500/2000/5000 刻度盘,HUD v2 显示 FPS 曲线,Profiler 截图 + Burst on/off 对比,README + 录像,完成可交付 demo。

**Architecture:** 不变。GO 为 source of truth,BT 独占决策,ECS 执行移动/需求/避障/威胁检测。新增:威胁检测是 ECS 侧(ECS->GO 镜像 threatened 标记供 BT 读),enableable `Threatened` 组件作"免 archetype 碎片"演示。

**Tech Stack:** Unity 6000.5, Entities 1.0(ISystem/IJobEntity/Burst), Unity Behavior 1.0.16。沿用 M1-M3 模式。

**本计划范围:** M4 全部 7 任务。**自然分期:** T1-T3(威胁/Flee 闭环,功能) -> T4-T7(刻度/性能/交付,证据)。可在 T3 后暂停验收再继续。

**参考:** 规格§4(BT 结构,威胁插队)、§5(管线第 4 段 ThreatDetectionSystem + 威胁反应性设计)、§6(性能/profiling)、§7(刻度盘+交付物)、§9(风险);M3/M4 路线图。

---

## 关键设计决策

1. **enableable `Threatened` 组件**(规格§5 第 4 段、§6 "enableable 组件免 archetype 碎片")。`public struct Threatened : IComponentData, IEnableableComponent {}`。加入 Bootstrap archetype,创建时 disabled。检测系统每帧 toggle bit,**不做 add/remove(零 archetype 变更)**--这是 demo 的简历 talking point。已用 unity_reflect 确认 API:`EntityManager.SetComponentEnabled<T>(Entity,bool)` / `IsComponentEnabled<T>(Entity)` / `EntityCommandBuffer.SetComponentEnabled<T>` 均存在。

2. **威胁检测 toggle 用"job 算 + 主线程 apply"**(非 ECB)。ThreatDetectionSystem 的 Burst job 把每个 entity 的 threatened bool 写入 `NativeArray<bool>`(by CitizenIndex),主线程循环 `em.SetComponentEnabled<Threatened>(ent, flag)`。理由:同帧生效(ResolveSystem 同帧能读到新鲜状态,反应延迟 1 帧内),只用已验证 API,可单测检测数学。5000 规模 SetComponentEnabled 是 bit flip(零结构变更),实测若 >1ms 再议 ECB 并行。**T1 内 spike:若 IJobEntity 能直接写 `Enabled` 位(Entities 1.0 部分版本支持),则省掉 NativeArray 往返--跑通后用 Profiler 对比选优。**

3. **威胁标记流:ECS 检测 -> GO 镜像 -> BT 读**。Threatened enableable bit 是 ECS 侧权威;ResolveSystem(已在 ECS->GO 循环)顺带写 `ca.threatened = em.IsComponentEnabled<Threatened>(ent)`。BT `IsThreatened` 读 `ca.threatened`。与现有 needs 衰减流(ECS 算 -> Resolve 回写 ca.needs)对称,架构一致。

4. **Flee 转向用 evade,目标点由 BT 存威胁中心**。`SetGoal(Flee, threatCenters)` 选最近威胁中心存入 `goal.Target`;SteeringSystem 对 `GoalType.Flee` 用 `SteeringMath.Evade(pos, goal.Target, Speed)`(全速远离)。依赖 BT 插队每帧重设目标以跟踪移动威胁(见决策 5)。SteeringSystem 不需新读 registry。

5. **BT 插队保 cursor 一致性**(规格§9 风险"插队不能破坏分片 cursor")。BtScheduler.Update:先扫 `ca.threatened==true` 的 agent 立即 tick(记 `lastTick[i]=frame`),再跑 round-robin 批次(跳过本帧已 tick)。cursor 每帧仍前进 perFrame 位,只是跳过已 tick 的--不破坏分片节奏。用 `int[] lastTick` 防 double-tick。可测逻辑抽成纯函数 `ShouldPreempt(bool threatened, int lastTick, int frame)`。

6. **5000 天花板如实标注**(规格§9、路线图最高风险)。T4 跑 5000 实测帧率,README 如实写。**默认不做主动缓解**(降回写频率/关键帧回写),把天花板作为 GO-centric 架构的 talking point。若实测 <30fps 不可看,再启动缓解(优先"只在 goal 变化时回写 Transform"--与 BT 决策频率绑定)。此为 T4 决策点,非现在定。

7. **HUD v2 范围收口**:FPS 滚动折线(最近 120 帧)+ 当前 FPS + 市民数 + 受威胁数 + 当前刻度 + BT ticks/帧。**每系统 ms 分解列为 stretch**(需给每个 system 加 ProfilerMarker + ProfilerRecorder,工作量大);per-system 数据优先用 Unity Profiler 截图(T6)呈现,不硬塞进 HUD。

---

## 文件结构(M4 新增/改动)

```
Assets/Scripts/CitizenSim/
  Components/SimComponents.cs          # 改:加 Threatened : IComponentData, IEnableableComponent
  Math/SteeringMath.cs                 # 改:加 Evade(pos, threat, speed)
  Registry/PoiRegistry.cs             # 不改(threat 独立 registry)
  Registry/ThreatZoneRegistry.cs      # 新:威胁区 Transform[] + radius 单例(仿 PoiRegistry)
  Registry/CitizenAuthoring.cs        # 改:加 public bool threatened
  Behavior/GoalDecision.cs            # 改:IsThreatened + SetGoal(Flee) 选最近威胁中心
  Behavior/IsThreatenedCondition.cs   # 新(仿 IsHungryCondition)
  Behavior/FleeAction.cs              # 新(仿 SeekFoodAction)
  Behavior/BtScheduler.cs             # 改:插队 tick + lastTick[] + 纯函数 ShouldPreempt
  Systems/ThreatDetectionSystem.cs    # 新:job 算 threatened + 主线程 apply enableable bit
  Systems/ResolveSystem.cs            # 改:回写 ca.threatened
  Systems/SteeringSystem.cs           # 改:Flee -> Evade
  Systems/ColoringSystem.cs           # 改:Flee=白
  Bootstrap/CitizenBootstrap.cs       # 改:archetype 加 Threatened(disabled) + Clear() 方法
  UI/ScaleDial.cs                     # 新:热键 1/2/3/4 切刻度 + 热键移动/开关威胁
  UI/Hud.cs                           # 改:FPS 折线 + 受威胁数 + 刻度 + ticks/帧
Assets/Editor/CitizenSim/BtGraphBuilder.cs  # 改:树顶插 Threat 分支
Assets/Scenes/CitizenSim/CitizenSimScene.unity  # 改:加 ThreatZone GO + ScaleDial 接线
Assets/Scripts/CitizenSim.Tests/
  Math/SteeringMathTests.cs           # 改:Evade
  Behavior/BtDecisionTests.cs         # 改:IsThreatened + SetGoal(Flee)
  Behavior/BtSchedulerTests.cs        # 改:ShouldPreempt 纯函数
  Systems/ThreatDetectionTests.cs     # 新:检测数学(距威胁中心 <radius -> true)
  Systems/SyncLoopTests.cs            # 改:archetype 加 Threatened
README.md                             # 新(仓库根)
```

---

## Task 1: 威胁区 + ThreatDetection + enableable

**Files:** `ThreatZoneRegistry.cs`(新)、`SimComponents.cs`(Threatened)、`ThreatDetectionSystem.cs`(新)、`CitizenAuthoring.cs`(threatened 字段)、`CitizenBootstrap.cs`(archetype)、`ResolveSystem.cs`(回写)、`ThreatDetectionTests.cs`(新)、`SyncLoopTests.cs`(archetype)

- [ ] **Step 1: Threatened 组件 + archetype**
```csharp
// SimComponents.cs
public struct Threatened : IComponentData, IEnableableComponent { }
```
`CitizenBootstrap` archetype 加 `typeof(Threatened)`;创建后 `em.SetComponentEnabled<Threatened>(e, false)`(默认未受威胁)。`SyncLoopTests` 同步加。

- [ ] **Step 2: ThreatZoneRegistry**(仿 PoiRegistry)
```csharp
public class ThreatZoneRegistry : MonoBehaviour {
    public static ThreatZoneRegistry Instance { get; private set; }
    public Transform[] zones;
    public float radius = 5f;
    public bool active = true;  // 热键开关
    void OnEnable() => Instance = this;
    void OnDisable() { if (Instance == this) Instance = null; }
    public Vector3[] GetZonePositions() { /* 仿 PoiRegistry.ToArray */ }
#if UNITY_EDITOR
    void OnDrawGizmos() { /* 红色半透明球画威胁区,半径=radius */ }
#endif
}
```

- [ ] **Step 3: CitizenAuthoring.threatened 字段**
```csharp
[Header("Threat (ECS 写, BT 读)")]
[HideInInspector] public bool threatened;
```

- [ ] **Step 4: ThreatDetectionSystem**(ISystem,job 算 + 主线程 apply)
```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SnapshotSystem))]
[UpdateBefore(typeof(SteeringSystem))]
public partial struct ThreatDetectionSystem : ISystem {
    NativeArray<bool> flags;  // by CitizenIndex
    int count;
    public void OnUpdate(ref SystemState state) {
        var reg = ThreatZoneRegistry.Instance;
        var zones = ToNative(reg != null && reg.active ? reg.GetZonePositions() : null);
        float radius = reg != null ? reg.radius : 0f;
        int n = /* query.CalculateEntityCount 或 registry.Count */;
        if (!flags.IsCreated || n != count) { /* realloc */ }
        state.Dependency = new ThreatJob { zones=zones, radius=radius, flags=flags }
            .ScheduleParallel(state.Dependency);
        state.Dependency.Complete();
        // 主线程 apply enableable bit(零 archetype 变更)
        var em = state.EntityManager;
        foreach (var (e, idx) in SystemAPI.Query<Entity, CitizenIndex>()) {
            em.SetComponentEnabled<Threatened>(e, flags[idx.Value]);
        }
        zones.Dispose();
    }
}
[BurstCompile] public partial struct ThreatJob : IJobEntity {
    [ReadOnly] public NativeArray<float3> zones; public float radius;
    [NativeDisableParallelForRestriction][WriteOnly] public NativeArray<bool> flags;
    void Execute(in SimPosition pos, in CitizenIndex idx) {
        bool t = false;
        float r2 = radius*radius;
        for (int i=0;i<zones.Length;i++)
            if (math.lengthsq(pos.Value - zones[i]) < r2) { t=true; break; }
        flags[idx.Value] = t;
    }
}
```

- [ ] **Step 5: ResolveSystem 回写 threatened**(在现有循环里加一行)
```csharp
ca.threatened = em.IsComponentEnabled<Threatened>(ents[i]);
```

- [ ] **Step 6: 写失败测试** `ThreatDetectionTests`
  - 检测数学纯函数化:抽 `ThreatMath.IsThreatened(float3 pos, NativeArray<float3> zones, float radius)` 供单测(无 World)。测:区内 true、区外 false、多区任一命中 true、active=false 全 false、空区全 false。

- [ ] **Step 7: 运行通过 + Commit**
```bash
git add <Task1 文件 + .meta> && git commit -m "feat(m4): threat zones + ThreatDetection (enableable) + ECS->GO mirror"
```

---

## Task 2: BT Flee 分支 + BtScheduler 插队

**Files:** `GoalDecision.cs`、`IsThreatenedCondition.cs`(新)、`FleeAction.cs`(新)、`BtScheduler.cs`、`BtGraphBuilder.cs`、`BtDecisionTests.cs`、`BtSchedulerTests.cs`

- [ ] **Step 1: GoalDecision 扩 IsThreatened + SetGoal(Flee)**
```csharp
public static bool IsThreatened(CitizenAuthoring ca) => ca != null && ca.threatened;
// SetGoal 扩 Flee:在威胁中心数组里选最近,存入 target(SteeringSystem Evade 用)
public static void SetGoal(CitizenAuthoring ca, GoalType type, Vector3[] pois) {
    if (type == GoalType.Flee) {
        Vector3 nearest = FindNearest(ca.transform.position, pois);  // 复用现有最近点逻辑
        ca.currentGoalType = GoalType.Flee;
        ca.currentGoalTarget = pois.Length > 0 ? nearest : ca.transform.position;
        return;
    }
    // ... 现有 Wander/Seek* 逻辑
}
```

- [ ] **Step 2: 新节点** `IsThreatenedCondition`(仿 IsHungryCondition,调 `GoalDecision.IsThreatened`)、`FleeAction`(OnStart 调 `GoalDecision.SetGoal(ca, Flee, ThreatZoneRegistry.Instance.GetZonePositions())`)。`[NodeDescription]`/`[Condition]` 的 `id` 唯一。

- [ ] **Step 3: BtScheduler 插队 + 纯函数**
```csharp
int[] lastTick;
// 纯函数(可单测):是否该插队
public static bool ShouldPreempt(bool threatened, int lastTickFrame, int currentFrame)
    => threatened && lastTickFrame != currentFrame;
void Update() {
    if (agents == null) return;
    int frame = Time.frameCount;
    var authoring = CitizenRegistry.Instance?.Authoring;
    // 插队:受威胁 agent 立即 tick
    if (authoring != null)
        for (int i=0;i<agents.Length && i<authoring.Length;i++)
            if (ShouldPreempt(authoring[i]!=null && authoring[i].threatened, lastTick[i], frame)) {
                agents[i]?.Graph?.Tick(); lastTick[i]=frame;
            }
    // round-robin 批次(跳过本帧已 tick,保 cursor 节奏)
    int perFrame = ComputePerFrame(agents.Length);
    for (int k=0;k<perFrame;k++) {
        if (cursor >= agents.Length) cursor = 0;
        if (lastTick[cursor] != frame) { agents[cursor]?.Graph?.Tick(); lastTick[cursor]=frame; }
        cursor++;
    }
}
```
`SetAgents` 里分配 `lastTick = new int[a.Length]`。

- [ ] **Step 4: BtGraphBuilder 树顶插 Threat 分支**
```
Start(Repeat)
  -> Conditional Branch [IsThreatened]
       True  -> Flee
       False -> Conditional Branch [IsHungry]   // 现有 M3 树
                 ...
```
在 `BuildInternal` 里把现有 `bHungry` 的 input 从 `startOut` 改为新 `bThreat` 的 False 端口;`bThreat` 的 True -> FleeAction。

- [ ] **Step 5: 写失败测试**
  - `BtDecisionTests`:IsThreatened true/false;SetGoal(Flee) 选最近威胁中心;无威胁区时 target=原地。
  - `BtSchedulerTests`:ShouldPreempt(threatened=true, last=0, frame=1)=true;ShouldPreempt(threatened=true, last=1, frame=1)=false(防 double-tick);ShouldPreempt(threatened=false,...)=false。

- [ ] **Step 6: 运行通过 + Force Rebuild 图资产 + Commit**
```bash
git add <Task2 文件 + .meta + CitizenBehavior.asset> && git commit -m "feat(m4): Flee BT branch + threatened preemption in BtScheduler"
```

---

## Task 3: Flee 着色 + evade 转向

**Files:** `SteeringMath.cs`(Evade)、`SteeringSystem.cs`(Flee 分支)、`ColoringSystem.cs`(Flee=白)、`SteeringMathTests.cs`

- [ ] **Step 1: SteeringMath.Evade**
```csharp
// 全速远离 threatCenter。在威胁中心正上方时给任意方向避免零向量。
public static float3 Evade(float3 pos, float3 threatCenter, float speed) {
    float3 away = pos - threatCenter;
    float d = math.length(away);
    if (d < 1e-4f) return new float3(speed, 0, 0);
    return (away / d) * speed;
}
```

- [ ] **Step 2: SteeringSystem Flee 分支**
```csharp
float3 arrive = goal.Type == GoalType.Flee
    ? SteeringMath.Evade(pos.Value, goal.Target, Speed)
    : SteeringMath.Arrive(pos.Value, goal.Target, Speed, SlowRadius);
// 后续 repulsion + 限速不变(Flee 也可叠避障)
```

- [ ] **Step 3: ColoringSystem Flee=白**
```csharp
static readonly Color FleeColor = new Color(0.95f, 0.95f, 0.95f);
// ColorFor 加: case GoalType.Flee: return FleeColor;
```

- [ ] **Step 4: 写失败测试** `SteeringMathTests`:Evade 远离方向正确;在中心点返回非零;对称(两侧市民互相远离)。

- [ ] **Step 5: Play 验收 + Commit**
  - Play:威胁区移入人群 -> 市民变白四散 -> 移出 -> 恢复日常色。无穿模。
```bash
git commit -m "feat(m4): Flee evade steering + white coloring"
```

**👉 T3 后是自然分期点。建议 Play 验收威胁闭环后再继续 T4-T7。**

---

## Task 4: 刻度盘 100/500/2000/5000 + 威胁热键

**Files:** `ScaleDial.cs`(新)、`CitizenBootstrap.cs`(Clear/Respawn)、`CitizenSimScene.unity`

- [ ] **Step 1: CitizenBootstrap.Clear()**
```csharp
public void Clear() {
    var reg = GetComponent<CitizenRegistry>();
    if (reg != null) {
        var em = World.DefaultGameObjectInjectionWorld?.EntityManager;
        if (em != null) foreach (var e in reg.Entities) if (e != Entity.Null) em.DestroyEntity(e);
        if (reg.GameObjects != null) foreach (var go in reg.GameObjects) if (go != null) Destroy(go);
        reg.GameObjects = reg.Entities = reg.Authoring = reg.Renderers = null;
    }
    if (BtScheduler.Instance != null) BtScheduler.Instance.SetAgents(null);
}
```
`Spawn()` 开头调 `Clear()`(若已存在则先清)。`count` 改为 public 可热调。

- [ ] **Step 2: ScaleDial**(热键 1/2/3/4 + 威胁热键)
```csharp
public class ScaleDial : MonoBehaviour {
    public CitizenBootstrap bootstrap;
    public ThreatZoneRegistry threatZone;
    readonly int[] scales = {100, 500, 2000, 5000};
    void Update() {
        for (int i=0;i<scales.Length;i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) { bootstrap.count = scales[i]; bootstrap.Spawn(); }
        if (Input.GetKeyDown(KeyCode.T)) threatZone.active = !threatZone.active;
        // WASD 移动威胁区(M4 单区域)
        Vector3 m = new Vector3(Input.GetAxis("Horizontal"),0,Input.GetAxis("Vertical")) * 20f * Time.deltaTime;
        if (threatZone.zones != null && threatZone.zones[0] != null) threatZone.zones[0].position += m;
    }
}
```

- [ ] **Step 3: 场景接线** 加 ThreatZone GO(挂 ThreatZoneRegistry)、ScaleDial GO(拖 bootstrap + threatZone 引用)。

- [ ] **Step 4: Play 验收 + 5000 决策点**
  - 切 100/500/2000/5000,市民重生平滑,HUD count 响应。
  - **决策点**:5000 实测帧率。若 <30fps 且不可看,启动缓解(优先"goal 变化时才回写 Transform",在 ResolveSystem 加 `if (goal 变化) go.transform...`)。记录数字供 README。

- [ ] **Step 5: Commit**
```bash
git commit -m "feat(m4): scale dial 100/500/2000/5000 + threat hotkeys"
```

---

## Task 5: HUD v2(FPS 曲线 + 受威胁数 + ticks/帧)

**Files:** `Hud.cs`、`BtScheduler.cs`(暴露 ticks/帧计数)

- [ ] **Step 1: BtScheduler 暴露 ticks/帧**
```csharp
public int LastTickCount { get; private set; }  // Update 末尾 = 本帧 tick 总数(插队+批次)
```

- [ ] **Step 2: Hud FPS 滚动折线**
```csharp
readonly float[] fpsHistory = new float[120]; int histHead;
void Update() {
    // 采 FPS、push history
    // 统计 threatened 数:遍历 CitizenRegistry.Instance.Authoring 计 threatened==true
    // 取 BtScheduler.Instance?.LastTickCount
}
// 用 UnityEngine.UI 画折线:120 个 Image bar 高度随 fpsHistory,或 LineRenderer on Canvas
```
文本:`FPS {fps} | Citizens {count} | Threatened {tc} | Scale {scale} | BT ticks/frame {ticks}`。

- [ ] **Step 3: stretch(可选)** 每系统 ProfilerMarker + ProfilerRecorder 读 ms 显示。若时间紧跳过,per-system 用 Profiler 截图呈现。

- [ ] **Step 4: Play 验收 + Commit**
```bash
git commit -m "feat(m4): HUD v2 FPS curve + threatened count + BT ticks"
```

---

## Task 6: Profiling + Burst on/off 对比

**Files:** 无代码(数据采集),产物存 `docs/perf/` 或 README 内联。

- [ ] **Step 1: 各刻度 Profiler 截图**
  - 100/500/2000/5000 各跑稳定 10s,Unity Profiler 录制,截图主线程 vs Job 分解。用 `manage_profiler`(profiler_start + get_counters)或 Profiler 窗口手动截。
  - 记录每刻度:整帧 ms、SteeringSystem ms、SpatialGridSystem ms、Snapshot/Resolve ms、ThreatDetection ms。

- [ ] **Step 2: Burst on/off 对比**
  - 同场景同种子 500 规模。Burst on(默认)录 SteeringSystem ms。
  - Burst off:Jobs 菜单 > Enable Burst Compilation 关闭,重录。
  - 记录两套数字(预期 Burst 提速显著)。

- [ ] **Step 3: agents-vs-frametime 曲线数据** 100/500/2000/5000 的 (人数, 帧时间) 点,标 5000 拐点。

- [ ] **Step 4: Commit 数据**
```bash
git add docs/perf/ README.md && git commit -m "docs(m4): profiling data + Burst comparison"
```

---

## Task 7: README + 录像

**Files:** `README.md`(仓库根,新)

- [ ] **Step 1: README 结构**
  - 架构总览(GO-spine + ECS-optimization-layer,硬约束)
  - GO↔DOTS 同步边界图(ASCII:Snapshot/Resolve 管线 6 步表)
  - 运行步骤(打开场景、Play、刻度热键 1-4、威胁热键 T/WASD)
  - 性能数据表(刻度 -> FPS -> sim ms -> 主线程/Job 分解,含 5000 天花板)
  - Burst on/off 对比数字
  - 天花板诚实分析(GO Transform 回写是 ceiling,纯 ECS 可线性扩展;拐点)
  - 里程碑历程(M1-M4 一句话各)

- [ ] **Step 2: 录像** GIF/MP4 ~1-2min:人群流动 -> 需求色切换 -> 威胁恐慌 -> 刻度 500->5000 + HUD 响应。

- [ ] **Step 3: 最终验收 + Commit**
```bash
git add README.md && git commit -m "docs(m4): README + delivery"
```

---

## M4 验收清单

- [ ] 威胁闭环:威胁区移入 -> 市民变白四散(evade);移出 -> 恢复日常;反应延迟 ≤2 帧(肉眼不可见)
- [ ] enableable `Threatened` toggling,零 archetype 变更(Profiler 无 chunk migration)
- [ ] BT 插队:受威胁 agent 当帧 tick,不破坏 round-robin cursor
- [ ] 刻度盘 100/500/2000/5000 可热切,重生平滑
- [ ] HUD v2:FPS 折线 + 受威胁数 + 刻度 + ticks/帧
- [ ] 各刻度 Profiler 截图 + Burst on/off 对比数据
- [ ] README 完整,5000 帧率如实标注
- [ ] EditMode 测试全 PASS(M1+M2+M3+M4)
- [ ] Console 无报错

---

## 风险 / 决策点

1. **enableable in-job toggle(T1 spike)**:IJobEntity 能否直接写 `Enabled` 位待跑通确认。基线用"job 算 NativeArray<bool> + 主线程 SetComponentEnabled"(已验证 API)。若 in-job 直写可行,省往返,Profiler 选优。
2. **5000 GO 回写天花板(§9,最高风险,T4 决策点)**:实测帧率。默认不缓解,作 talking point。若 <30fps 启动"goal 变化时才回写 Transform"。README 如实标注,不强行宣称 60fps。
3. **插队 cursor 一致性(T2)**:`lastTick[i]` 防 double-tick,cursor 每帧前进 perFrame 位(跳过已 tick)。纯函数 ShouldPreempt 可单测。
4. **Burst 对比可信度(T6)**:同场景同种子,只切 Burst 开关。两套数据如实记。
5. **威胁区移动追踪(T3)**:Flee 依赖插队每帧重设 goal.Target 跟踪移动威胁。验证威胁快速移动时市民方向跟得上。
6. **5000 BehaviorGraphAgent 常驻开销(§9 风险 3)**:5000 个 GO+agent 的内存/初始化开销,Profiler 确认。若超标,退路是子集 BT(仅部分市民挂 BT)。

## Self-Review

**Spec 覆盖**:M4 覆盖规格§4 完整 BT(Threat>Fatigue>Bored>Wander 全优先级)、§5 管线第 4 段(ThreatDetectionSystem enableable)+ 威胁反应性设计(ECS 检测 + BT 插队)、§6 性能/profiling 产物、§7 刻度盘 + 交付物、§9 风险 1-3。enableable 演示"免 archetype 碎片"。

**类型一致性**:`Threatened : IComponentData, IEnableableComponent` 新增,Bootstrap archetype + SyncLoopTests 同步;`ca.threatened bool` GO 镜像;`GoalType.Flee`(M1 已留位)启用;`SteeringMath.Evade` 与 Seek/Arrive 同风格;`BtScheduler.ShouldPreempt` 纯函数与 `ComputePerFrame` 同可测模式。

**已知 API 风险**:已用 unity_reflect 确认 `SetComponentEnabled`/`IsComponentEnabled`/ECB.SetComponentEnabled 存在。in-job `Enabled` 直写待 T1 spike。BtGraphBuilder 嵌套 Branch 复用 M3 模式(第 4 层,决策点 4 已评估可接受)。

**分期**:T1-T3 = 威胁/Flee 功能闭环;T4-T7 = 刻度/性能/交付证据。T3 后可暂停验收。
