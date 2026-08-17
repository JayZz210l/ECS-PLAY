# ECS-PLAY 项目程序架构总结

> 面向对象的架构总结。覆盖 `Assets/Scripts/CitizenSim`（核心模拟）+ `CitizenSim.Tests`（测试）+ `Editor/CitizenSim`（编辑器工具），以及项目其他脚本的大致分类。
>
> 最后更新：2026-08-10，对应当前分支 `feat/m1-ecs-sync-spine`（代码实际已到 M5 流场寻路）。

---

## 1. 项目是什么

一个 **市民行为模拟** resume tech demo：500 市民（可热切 100/500/2000/5000）各自拥有"饿→吃 / 累→睡 / 无聊→玩 / 受威胁→逃"的行为循环，在场景里漫游、避障、绕开障碍、寻找最近的兴趣点。

**技术栈**：Unity 6000.5.3f1、URP 17.5、Entities 1.0（ISystem / IJobEntity / Burst）、Unity Behavior 1.0.16。

**核心架构哲学（硬约束）**：

```
┌──────────────────────────────┐
│  GameObject 层 = Source of   │   市民是 GameObject：身份、行为树(BT)、
│  Truth（身份 / BT / 配置 / 渲染）│   配置、Transform、渲染 = 数据的"真相源"
└──────────────┬───────────────┘
               │  Snapshot：GO → ECS（每帧同步 GO 状态进 ECS）
               ▼
┌──────────────────────────────┐
│  ECS 层 = 优化层（只做每帧高频） │   镜像 entity 只承载高频模拟：
│  转向 / 需求衰减 / 空间网格 /    │   steering、needs 衰减、空间网格、避障
│  威胁检测 / 流场寻路             │
└──────────────┬───────────────┘
               │  Resolve：ECS → GO（每帧把 ECS 算好的写回 GO）
               ▼
┌──────────────────────────────┐
│  GameObject 层               │   Transform 移动 / needs 回写 / 着色
└──────────────────────────────┘
```

这不是纯 DOTS 展示——**GO ↔ ECS 的往返同步本身就是被演示的核心卖点**（配合性能证据）。5000 规模下 GO Transform 回写是已知性能天花板（见 `docs/perf/m4-profiling.md`）。

---

## 2. 目录结构

```
Assets/Scripts/CitizenSim/
├── Components/        SimComponents.cs      # 全部 ECS 组件定义（IComponentData）
├── Bootstrap/         CitizenBootstrap.cs    # 出生/清场，GO + entity 一对一同生共死
├── Registry/          数据单例 + 挂载脚本
│   ├── CitizenRegistry.cs      # 市民数组缓存（GameObjects/Entities/Authoring/Renderers）
│   ├── CitizenAuthoring.cs     # 单个市民 GO 的配置与状态（source of truth）
│   ├── PoiRegistry.cs          # 食物/家/娱乐点
│   ├── ThreatZoneRegistry.cs   # 威胁区（常驻 + 临时恐惧区）
│   ├── ObstacleRegistry.cs     # 障碍物单例 + 动态障碍物状态机
│   └── ObstacleAuthoring.cs    # 单个障碍物 GO 的尺寸配置
├── Systems/           ECS 系统（每帧执行）
│   ├── SnapshotSystem.cs       # GO → ECS 同步（同步脊柱入口）
│   ├── ResolveSystem.cs        # ECS → GO 同步（移动 + needs 回写）
│   ├── SteeringSystem.cs       # 转向/避障（Burst IJobEntity）
│   ├── SpatialGridSystem.cs    # 空间哈希网格
│   ├── NeedsDecaySystem.cs     # 需求衰减（hunger/fatigue/fun）
│   ├── ThreatDetectionSystem.cs# 威胁检测（enableable 组件）
│   ├── ColoringSystem.cs       # 按目标着色
│   └── FlowFieldBuildSystem.cs # 3 张多源流场构建（寻路）
├── Math/              纯函数数学库（脱离 ECS 可单测）
│   ├── SteeringMath.cs         # 转向算法（Seek/Arrive/Evade/排斥/流场/逃出）
│   ├── FlowFieldMath.cs        # 流场结构 + BFS 构建/局部重建
│   ├── PoiMath.cs              # POI 最近点/半径查询
│   └── ThreatMath.cs           # 威胁判定（滞回）
├── Behavior/          Unity Behavior 行为树层
│   ├── BtScheduler.cs          # 时间分片调度器（每 0.5s 决策一次 + 受威胁插队）
│   ├── GoalDecision.cs         # BT 决策纯函数逻辑（滞回判定 + 选目标）
│   ├── BtNodeSpikes.cs         # spike 节点（作者模式验证模板）
│   └── 各 Condition/Action 节点
├── UI/                运行时 UI
│   ├── Hud.cs                   # 状态栏 + 行为图例
│   ├── ScaleDial.cs             # 刻度盘热键（1/2/3/4 切换规模、T 威胁、E 临时恐惧区）
│   ├── FpsGraph.cs              # FPS 滚动柱状图
│   └── FlowFieldDebugVisualizer.cs  # 流场 Gizmos 调试可视化

Assets/Scripts/CitizenSim.Tests/    # NUnit 测试（EditMode）
Assets/Editor/CitizenSim/BtGraphBuilder.cs  # 编辑器工具：程序化生成行为树资产
```

---

## 3. 分层详解

### 3.1 Components 层 — `SimComponents.cs`

全部 ECS 组件集中在一个文件。市民 entity 的完整 archetype（见 `CitizenBootstrap.Spawn`）：

| 组件 | 类型 | 作用 |
|---|---|---|
| `SimPosition` | struct | 位置（float3），Snapshot 从 GO Transform 写入 |
| `SimVelocity` | struct | 速度（float3），SteeringJob 写、Resolve 读来移动 |
| `SimGoal` | struct | 当前目标：`Type`(GoalType) + `Target`(float3) |
| `SimRadius` | struct | 市民半径（避障用） |
| `CitizenIndex` | struct | 市民下标，GO 数组 ↔ entity 的桥梁（by-index 对齐） |
| `GridCell` | struct | 空间哈希网格坐标，SpatialGridSystem 写、Steering 读 |
| `Threatened` | struct + `IEnableableComponent` | 威胁标记。**enableable**：翻转 chunk 内 bit，零 archetype 变更（防 ECS 碎片化）。默认 disabled |
| `SimNeeds` | struct | 需求 float3：x=hunger, y=fatigue, z=fun（均 0..1） |
| `SimExit` | struct | 离开推力：Timer + Direction。吃饱离开 POI 时短暂背离原目标，冲出人群防拥堵（M5） |

枚举 `GoalType`：`Wander / SeekFood / SeekHome / SeekFun / Flee`。

---

### 3.2 Bootstrap 层 — `CitizenBootstrap.cs`

`MonoBehaviour`，挂在场景根物体上，负责市民的出生与清场。**每个市民 = 一个 GO + 一个 entity，按下标一一对应**。

| 方法 | 作用 |
|---|---|
| `Start()` | 进入 Play 即调 `Spawn()` |
| `Clear()` | 清场。销毁全体 entity（按 `CitizenIndex` 查询，不依赖可能过期的 registry）和 GO，清空 registry 缓存、BtScheduler 的 agents。热切重生时 Spawn() 开头会调它 |
| `Spawn()` | ① 建 archetype（含全部组件）→ ② `Resources.LoadAll<BehaviorGraph>("CitizenBehavior")` 取行为树运行时图 → ③ 循环 `count` 次：`Instantiate` prefab（随机位置，**避开障碍物且不出流场网格**，最多重试 50 次），随机化初始 needs，创建 entity 并填初始组件 → ④ `registry.Register()` 存数组 → ⑤ `BtScheduler.SetAgents()` 接入 BT |

**关键设计**：`BehaviorGraphAgent.Init()` 会克隆图实例 + 独立 blackboard，所以 500 个市民各有一份独立的行为树状态。GO 是 source of truth，entity 初始状态从 GO 读出。

---

### 3.3 Registry 层

#### `CitizenAuthoring.cs` — 单个市民 GO 的配置/状态

挂在市民 prefab 上。**GO 侧数据真相源**：BT 写 `currentGoal`，Snapshot 同步进 ECS；ECS 算完 needs 后 Resolve 回写 `needs`。

字段分 4 组：
- **标识**：`Index`（市民下标）
- **需求阈值（各带滞回）**：hunger（`hungerThreshold`=0.7 触发、`fullThreshold`=0 吃饱）、fatigue（0.7/0）、fun（`boredThreshold`=0.3、`funFullThreshold`=0.9）
- **目标（BT 写、ECS 读）**：`currentGoalType` / `currentGoalTarget`
- **威胁（ECS 写、BT 读）**：`threatened`（Resolve 从 ECS bit 镜像回）
- **离开推力（M5）**：`lastGoalType` / `exitTimer` / `exitDirection`（Snapshot 检测切换时启动，Resolve 递减计时）
- **视觉**：`capsuleRenderer`（Bootstrap 注入，Coloring 用）

#### `CitizenRegistry.cs` — 市民数组缓存单例

静态单例 `Instance`，持有并行数组：
- `GameObjects[]` / `Entities[]` / `Authoring[]`（CitizenAuthoring 缓存）/ `Renderers[]`（缓存）
- **缓存的意义（M3 优化）**：消掉 Snapshot/Resolve/Coloring 每帧 `GetComponent` 的开销——5000 市民每帧省 5000 次组件查找。

| 成员 | 作用 |
|---|---|
| `OnEnable/OnDisable` | 单例赋值/清空 |
| `Register(gos, ents)` | 填数组，一次性取好 Authoring/Renderers 缓存 |
| `Count` | 市民数 |

> 各 Registry 的 `Register()` / `Clear()` 静态方法是为了 **EditMode 测试**：`OnEnable` 在 EditMode 不触发，测试里手动注册单例，保证可隔离。

#### `PoiRegistry.cs` — 兴趣点单例（食物/家/娱乐）

| 成员 | 作用 |
|---|---|
| `foodPoints/homePoints/funPoints` | 3 组 Transform[]，场景里拖点配置 |
| `GetFood/Home/FunPositions()` | 返回 Vector3[] 位置 |
| `OnDrawGizmos`（Editor） | 场景里画 POI 球 + 名字 |

#### `ThreatZoneRegistry.cs` — 威胁区单例

两类威胁区：**常驻 zones**（全局 `radius`）+ **临时恐惧区**（按 E 键生成，15m/3s，纯数据、不可见，靠市民反应反馈）。

| 成员 | 作用 |
|---|---|
| `GetZonePositions()` | 仅常驻区位置（BT Flee 备选） |
| `GetActiveZonePositions()` | 常驻 + 临时全部活动威胁位置（**临时区触发的 Flee 必须用这个**，否则 evade 目标错） |
| `SpawnTempZone(pos, radius, duration)` | 生成临时恐惧区 |
| `GetActiveZones(outPos, outRad)` | 合并常驻(全局半径) + 临时(各自半径)，供 ThreatDetectionSystem 每帧读；复用静态 List 免 GC |
| `Update()` | 过期清理临时区 |
| `TempZoneCount` | 调试用 |
| `active` | 全局开关（T 键切换），关掉后所有威胁失效 |

#### `ObstacleRegistry.cs` — 障碍物单例 + 动态障碍物状态机

管理所有 `ObstacleAuthoring`，驱动动态障碍物状态机（M5）：
- **移动中**（isMoving=true）：Steering 排斥力每帧生效 + 流场每 0.5s 局部重算
- **静止累计 1s**：触发一次全量重算（终态准确），之后停算

障碍物有四层标识：GO 层（Authoring 状态）/ 流场层（blocked）/ Registry 层（本类）/ Steering 层（MovingObstaclePos）。

| 成员 | 作用 |
|---|---|
| `MovingObstaclePos/Rad/Count` | 静态预分配数组，SteeringJob 每帧读（免 GC） |
| `OnEnable/Register()` | 初始化单例 + 分配静态数组 |
| `DisposeStatics()` | 测试用：释放静态数组 |
| `Update()` | **状态机主循环**：检测每障碍物移动（记录新旧格子供局部重算）、推进静止计时、到 1s 标 isMoving=false；移动中按 0.5s 间隔 `RebuildRegions`，刚静止触发 `RebuildAll`；最后 `CollectMovingObstacles` |
| `CollectMovingObstacles()` | 收集所有障碍物（静态+动态）位置/半径到静态数组，供 SteeringJob 做排斥力。**流场 blocked 管宏观绕路，排斥力管微观避让**（防市民擦入 mesh 边缘） |
| `WriteBlocked(ref field)` | 把所有障碍物占用的格子标记到流场 blocked（先清零再标记） |
| `MarkBlockedRect(ref field, pos, size)` | **纯函数**：按 pos+size 算覆盖格子范围标 blocked=1。max 用排他边界（max-eps），避免贴边多标一格 |
| `IsInObstacle(pos)` | pos 是否在任一障碍物内（Bootstrap 出生时避开障碍物用） |

#### `ObstacleAuthoring.cs` — 单个障碍物 GO 配置

`size`(x,z 米) + `height`(y 米)，驱动 `transform.localScale`，保证 **视觉 mesh、BoxCollider、流场 blocked 三者尺寸一致**。`isMoving/lastPosition/stationaryTime` 由 ObstacleRegistry 每帧检测。

| 成员 | 作用 |
|---|---|
| `OnValidate()`（Editor） | 编辑器改 size/height 时实时同步 scale |
| `SyncSize()` | 运行时/初始化强制同步 scale |

---

### 3.4 Systems 层 — ECS 每帧执行

所有系统都在 `SimulationSystemGroup`，靠 `[UpdateBefore]/[UpdateAfter]` 组内排序。**一帧的完整执行链**：

```
SnapshotSystem ──▶ SpatialGridSystem ──▶ NeedsDecaySystem
     │                    │                    │
     ▼                    ▼                    ▼
ThreatDetectionSystem ─▶ FlowFieldBuildSystem ─▶ SteeringSystem ─▶ ResolveSystem ─▶ ColoringSystem
(GO→ECS)              (流场重算)              (Burst 转向)      (ECS→GO 移动)      (着色)
```

#### `SnapshotSystem.cs` — 同步脊柱入口（GO → ECS）

主线程遍历，把 GO 状态写进 ECS（**慢、但每市民必须做**，因为 GO 是真相源）：
- 写 `SimPosition` = GO 位置
- 写 `SimNeeds` = GO needs（ECS 在 NeedsDecay 里改，但写回前以 GO 为准）
- 写 `SimGoal` = GO currentGoal（BT 刚决策的结果）
- **exit 推力检测（M5）**：若 goal 从 Seek* 切到其他（吃饱离开），启动 1s 的背离原 POI 方向推力，写 `SimExit`

| 方法 | 作用 |
|---|---|
| `OnUpdate()` | 遍历 `registry` 的 GO 数组，按 index 写对应 entity 组件 |

> 属性排序：`[UpdateBefore(SteeringSystem)]`，先于转向。

#### `ResolveSystem.cs` — 同步脊柱出口（ECS → GO）

把 ECS 算好的结果写回 GO：
- 读 `SimVelocity` × dt 移动 GO Transform
- **流场硬约束（M5）**：市民在可达格子时，x/z 分别检查目标位置是否 blocked/网格外，被堵方向取消、沿边缘滑动；被困在 blocked 格子内时（被物理/合力推入）允许移动（否则永远卡死）
- 写回 `ca.needs`（ECS 衰减结果）
- 镜像 `ca.threatened` = ECS 的 `Threatened` bit（BT 的 IsThreatened 读它）
- 递减 `ca.exitTimer`

| 方法 | 作用 |
|---|---|
| `OnUpdate()` | 遍历，读 velocity 移动 + 回写 GO 状态 |
| `IsCellBlocked(field, pos)`（static） | pos 所在格子是否 blocked 或网格外（网格外返回 true，阻止市民走出网格） |

> 属性排序：`[UpdateAfter(SteeringSystem)]`，转向算完才移动。

#### `SteeringSystem.cs` — 转向/避障（核心 Burst 作业）

**`SteeringJob`**（`IJobEntity` + `[BurstCompile]`）：每个市民独立算合力。

| 方法 | 作用 |
|---|---|
| `Execute(ref SimVelocity, in SimPosition/SimGoal/GridCell/CitizenIndex/SimExit)` | ① 按 goal 类型算基础速度（arrive）② 9 邻域查空间网格累加邻居排斥 ③ 障碍物排斥 ④ exit 推力 ⑤ 合成、限速，写 velocity |

基础速度按 goal 分派：
- `Flee` → `Evade`（全速远离威胁中心，**不走流场**——威胁系统已接走高频动态）
- `SeekFood/Home/Fun` → `FlowFieldArrive`（查对应流场格子方向，绕开障碍）
- `Wander` → `Arrive` 航点（无固定 POI 不走流场；**前方格子 blocked 时用 `EscapeDirection` 绕开**，避免顶住）

**`SteeringSystem`**（`ISystem`）：`OnUpdate()` 配置 job 参数后 `ScheduleParallel`。

| 关键参数 | 值 | 作用 |
|---|---|---|
| `Speed` | 2 | 最大速度 |
| `SlowCost` | 3 | 接近 POI 3 格内开始减速 |
| `AvoidRadius` | 1 | 邻居排斥半径 |
| `AvoidStrength` | 1.5 | 邻居排斥强度 |
| `ObstacleStrength` | 3 | 障碍物排斥强度 |
| `ExitStrength` | 4 | 离开推力强度（略强于障碍排斥，确保冲出人群） |

> 注：系统 OnUpdate 本身**不加 Burst**（要读各系统静态字段，Burst 不支持非只读静态），但 job 本身 Burst。SpatialGrid/FlowFieldBuild 都在前一帧 `Complete`，保证数据安全读。

#### `SpatialGridSystem.cs` — 空间哈希网格

每帧把全体市民装入固定格子哈希 `cell → citizenIndex`，并缓存 `positions[]`。SteeringJob 查 9 邻域找邻居做避障，**避免 O(N²)**（规格 §5 第 3 段）。ECS 中间量，不回写 GO。

| 成员 | 作用 |
|---|---|
| 静态 `Grid` / `Positions` / `Count` | cell→index 哈希 / 按 index 的位置数组 / 当前数量（大小变化时重分配） |
| `CellSize` | 1.0（≈避障半径×2） |
| `OnCreate/OnDestroy` | 建查询 / 释放 NativeArray |
| `OnUpdate()` | 计数变化则重分配 → `Grid.Clear()` → `BuildGridJob.ScheduleParallel` → **`Complete()`**（保证 Steering 读到完整网格；5000 规模若成瓶颈，改跨系统依赖链不 Complete） |

**`BuildGridJob`**（IJobEntity + Burst）：写 `GridCell`、填 `positions[idx]`、`grid.Add(cell, idx)`。用 `[NativeDisableParallelForRestriction]`（CitizenIndex 与迭代下标不一致，每 entity 写自己唯一 idx，需关并行写索引安全检查）。

#### `NeedsDecaySystem.cs` — 需求衰减

hunger/fatigue 随时间上升、fun 下降；**在对应 POI 且目标匹配时反向**（吃/睡/玩）。

| 速率常量 | 值 | 含义 |
|---|---|---|
| `HungerRate` 0.01 | hunger 每秒增量（~70s 从 0 到阈值，缓解食物点瓶颈） |
| `EatRate` 0.15 | 到食物点每秒减量 |
| `FatigueRate` 0.018 / `RestRate` 0.15 | fatigue 增量 / 到家减量 |
| `FunDecayRate` 0.03 / `PlayRate` 0.15 | fun 减量 / 到娱乐点增量 |
| `PoIRadius` 2 | POI 判定半径 |

| 方法 | 作用 |
|---|---|
| `OnUpdate()` | 主线程把 PoiRegistry 的托管数组转 NativeArray → `NeedsDecayJob.ScheduleParallel` → Complete → Dispose（TempJob） |
| `ToNative(src)`（static） | Vector3[] → NativeArray<float3> |
| `NeedsDecayJob.Execute` | 按 `goal.Type` + `PoiMath.WithinRadius` 决定增减，`math.saturate` 夹到 0..1 |

#### `ThreatDetectionSystem.cs` — 威胁检测（enableable 组件）

每帧查威胁半径，设 `Threatened` enableable bit（**零 archetype 变更**，规格 §6）。

**关键陷阱处理**：迭代按 `SimPosition + CitizenIndex`（全体市民）而不是按 Threatened bit——否则 bit=disabled 的市民会从查询消失，再也进不了检测。bit 只在主线程 `SetComponentEnabled` 时碰。

| 成员 | 作用 |
|---|---|
| `flagsEnter/flagsExit` | 进入/退出阈值 flags（by CitizenIndex），复用 NativeArray 免每帧分配 |
| `OnUpdate()` | ① ThreatZoneRegistry.GetActiveZones 取威胁区 → 转 Native → `ThreatJob.ScheduleParallel` → Complete ② **主线程循环 SetComponentEnabled**（同帧生效）③ Resolve 把 bit 镜像回 ca.threatened |

**空间滞回**（防边界抖动）：已 threatened 用退出阈值 `radius×1.3`（更宽容），未 threatened 用进入阈值 `radius`。逃出区后保持 Flee 直到 1.3 倍半径外，避免边界 Flee/Seek 反复横跳。

**`ThreatJob`**（IJobEntity + Burst）：写 `flagsEnter = IsThreatened(..., factor=1)`、`flagsExit = IsThreatened(..., factor=1.3)`。

#### `ColoringSystem.cs` — 着色

按 `goal.Type` 给市民换色：SeekFood=红 / SeekHome=蓝 / SeekFun=黄 / Wander=绿 / Flee=白。主线程 `MaterialPropertyBlock`，用 Registry 缓存的 `Renderers[]`（免每帧 GetComponent）。

| 方法 | 作用 |
|---|---|
| `OnUpdate()` | 遍历 renderers，读 SimGoal 设 `_BaseColor` |
| `ColorFor(GoalType)`（static） | 类型→颜色（HUD 图例也复用，保证一致） |

#### `FlowFieldBuildSystem.cs` — 流场构建（M5 寻路）

3 张**多源**流场（食物/家/娱乐），全局静态供 SteeringSystem 读。POI 注册时生成，障碍物变更时标记 Dirty 重算。网格 40×40 格 × 2m = 80×80m，覆盖默认场景（spawnRadius=40）。

| 成员 | 作用 |
|---|---|
| 静态 `FoodField/HomeField/FunField` | 3 张流场（结构见 FlowFieldMath） |
| 静态 `Dirty` / `Initialized` | 障碍物变更标记触发重算 / 首次生成标记 |
| `OnCreate` | 分配 3 张流场 NativeArray，Dirty=true |
| `OnDestroy` | 释放 |
| `OnUpdate()` | Dirty 时 `RebuildAll()` |
| `RebuildAll()`（static） | 全量重算：写 3 张 blocked（障碍物层）→ 各以 POI 为源 `BuildMultiSource` |
| `RebuildRegions(changedCells, radius)`（static） | 局部重算：障碍物移动后只重算 changedCells 周围 radius 格（增量更新，比全量便宜） |
| `IsWorldInBounds(pos)`（static） | 世界坐标是否在流场网格内（Bootstrap 出生检查，避免生成在网格外卡死） |
| `AllocateField()` / `RebuildField()` / `WriteObstacles()` | 内部：分配 / 单张重算（POI→sources） / 障碍物层写 blocked |

---

### 3.5 Math 层 — 纯函数数学库

**全部 static、脱离 ECS 可单测**（这是本项目的测试策略核心：把逻辑抽成纯函数，测试不依赖 EntityManager）。

#### `SteeringMath.cs` — 转向算法

| 方法 | 作用 |
|---|---|
| `Seek(pos, target, speed)` | 朝目标全速前进 |
| `Arrive(pos, target, speed, slowRadius)` | 朝目标，进入 slowRadius 线性减速到 0 |
| `Evade(pos, threatCenter, speed)` | 全速远离威胁中心；正对中心给任意方向避免零向量卡死 |
| `RepulsionFrom(pos, neighbor, avoidRadius)` | 单个邻居排斥力：方向背离邻居、1/d² 衰减、超过半径不计 |
| `Repulsion(pos, neighbors, radius)` | 一组邻居排斥力和（供单测；job 内逐邻居累加） |
| `FlowFieldArrive(pos, cell, field, speed, slowCost)` | 沿流场方向走，cost<slowCost 线性减速；**不可达格子（cost=Inf）→ EscapeDirection 逃出**；越界→zero |
| `EscapeDirection(cell, field, speed)` | 被困不可达格子时查 4 邻域找 cost 最小可达格子朝它走；无可达邻居→zero |
| `TryEscape(...)` | EscapeDirection 的单个邻居尝试（内部） |
| `ObstacleRepulsion(pos, obstacles, radii, count, strength)` | 移动障碍物排斥力：方向背离、越近越强（1-d/r 线性），d≥r 不计 |

#### `FlowFieldMath.cs` — 流场结构 + 构建算法

**`struct FlowField`**（IDisposable）：固定网格，每格存方向 `directions[]`(float3)、代价 `costs[]`(float，Inf=未访问/不可达/障碍)、障碍 `blocked[]`(byte)。格子坐标 int2(x,z) 映射世界 x/z 轴（y 不用，市民在平面）。

| 方法 | 作用 |
|---|---|
| `CellCount` | 格子总数 |
| `WorldToCell(pos)` | 世界→格子坐标（floor） |
| `CellCenter(cell)` | 格子中心 = origin + (cell+0.5)×cellSize |
| `InBounds(cell)` | 是否在网格内 |
| `CellIndex(cell)` | 2D→1D 索引 |
| `Dispose()` | 释放三个 NativeArray |

**`static class FlowFieldMath`**：

| 方法 | 作用 |
|---|---|
| `BuildSingleTarget(ref field, targetCell)` | 单目标 BFS：从 targetCell 反向扩散填 cost/direction；blocked 跳过；目标被堵→全场 Inf |
| `BuildMultiSource(ref field, sources)` | 多源 BFS：所有源同时入队，算每格到**最近**源的 direction（"找最近 POI"的流场解法，天然选最近，成本与单目标一致） |
| `RebuildRegion(ref field, changedCells, radius)` | 局部重算：changedCells 周围 radius 格置 Inf，从区域边界（区域外 cost 已知格子）重新 BFS 扩散进区域内（动态障碍物静止后增量更新） |
| `EnqueueBoundary(...)` | 边界源入队：区域外、cost<Inf、非障碍、未入队（内部） |
| `RelaxInRegion(...)` / `TryRelax(...)` | BFS 松弛：邻居非障碍且 newCost<当前则更新 cost+direction 并入队（内部） |
| `Inf` 常量 | 1e9 |

#### `PoiMath.cs` — POI 查询

| 方法 | 作用 |
|---|---|
| `NearestIndex(pos, points)` | 返回最近 POI 下标；空数组返回 -1 |
| `WithinRadius(pos, points, radius)` | 是否在任意 POI 半径内（距离平方比较免开方） |

#### `ThreatMath.cs` — 威胁判定

| 方法 | 作用 |
|---|---|
| `IsThreatened(pos, zones, radius)` | 是否在任一威胁区内（统一半径） |
| `IsThreatened(pos, zones, radii)` | 每区域独立半径（临时 15m 与常驻 5m 共存） |
| `IsThreatened(pos, zones, radii, factor)` | 带滞回因子：factor>1 扩大判定半径（已 threatened 时用更宽容退出阈值防边界抖动） |

---

### 3.6 Behavior 层 — 行为树

#### `BtScheduler.cs` — 时间分片调度器

**为什么需要**：500 市民每人一棵行为树，每帧全 tick 太重。所以把 agent 的自动 `Update()` 关掉（`agent.enabled = false`），由本调度器 round-robin 接管，**每市民约每 0.5s 决策一次**。

**核心修复（M2 关键坑）**：手建图的 `Node.Parent` 字段是 `[SerializeReference]`，但反射组装节点树后 `ScriptableObject.Instantiate` 深拷贝没有还原 Parent 链（Unity Behavior 的 GraphAssetProcessor 才会补）。Parent=null 时 selector 完成后无法唤醒 Start，Start.Repeat 死锁，首个决策之后 Tick() 永远空转。所以 `SetAgents` 时用反射 `FixupParentChain` 手动补全 Parent。

| 成员 | 作用 |
|---|---|
| `Instance` | 单例 |
| `SetAgents(agents)` | 接收 agent 数组，逐个补 Parent 链、`Graph.Start()`、`agent.enabled=false` |
| `FixupParentChain(graph)` / `FixupNode(node, parent)` | 反射遍历节点树补 Parent 字段（按 Modifier/Composite/Action 类型选对应 `m_Parent` 字段） |
| `ComputePerFrame(agentCount)`（static） | `max(1, count/30)`：500→16/帧，每 agent ~0.5s 决策一次（纯函数可单测） |
| `ShouldPreempt(threatened, lastTickFrame, currentFrame)`（static） | 插队判定：受威胁且本帧未 tick → 应插队（纯函数可单测） |
| `Update()` | ① **插队**：受威胁 agent 立即 tick（不受分片节流，威胁反应性）② **round-robin 批次**：跳本帧已插队 tick 的，保 cursor 节奏 |
| `TickAgent(i)` | `agent.Graph.Tick()` |
| `LastTickCount` | 本帧 tick 总数（HUD 显示 BT ticks/帧） |

#### `GoalDecision.cs` — BT 决策纯函数

BT 节点是"薄封装"，真正的决策逻辑抽到这里，脱离 Behavior 运行时**可单测**。

| 方法 | 作用 |
|---|---|
| `IsHungry(ca)` | 滞回：已在 SeekFood 时吃到 `fullThreshold` 才停；否则 hunger>`hungerThreshold` 触发觅食 |
| `IsFatigued(ca)` | 滞回：SeekHome 休息到 `restedThreshold` 才停；否则 fatigue>threshold 触发回家 |
| `IsBored(ca)` | 滞回：SeekFun 玩到 `funFullThreshold` 才停；否则 fun<boredThreshold 触发找娱乐 |
| `IsThreatened(ca)` | 读 ca.threatened（无滞回；ECS 每帧写，BtScheduler 插队保证当帧反应） |
| `SetGoal(ca, type, pois)` | 设目标：Seek* 选最近 POI；Flee 选最近威胁中心 |
| `SetWanderGoal(ca)` | Wander 到达重选：还在走向当前目标就保持（不换，防止 BT 每 0.5s 重选导致原地抖动）；到达或刚切 Wander 时选新随机点 |

#### BT 节点

**Action 节点**（`OnStart` 里调 `GoalDecision.SetGoal` 后立刻 Success，真正的移动由 ECS Steering 做）：

| 节点 | 作用 |
|---|---|
| `SeekFoodAction` | 设 SeekFood 目标 = 最近未满食物点 |
| `SeekHomeAction` | 设 SeekHome 目标 = 最近未满家点 |
| `SeekFunAction` | 设 SeekFun 目标 = 最近未满娱乐点 |
| `WanderAction` | 设 Wander 随机航点 |
| `FleeAction` | 设 Flee 目标 = 最近威胁中心（依赖 BtScheduler 插队每帧重设以跟踪移动威胁） |
| `SpikeLogAction` | spike 验证节点：log 一次成功 |

**Condition 节点**（薄封装调 GoalDecision）：

| 节点 | 作用 |
|---|---|
| `IsHungryCondition` / `IsFatiguedCondition` / `IsBoredCondition` / `IsThreatenedCondition` | 对应 GoalDecision 判定 |
| `SpikeAlwaysTrueCondition` | spike：恒真 |

> 每个节点带 `[GeneratePropertyBag]`（Unity Behavior 1.0.16 作者模式必需）+ 唯一 id 的 `[NodeDescription]`。

**行为树结构**（M4 完整版，由 BtGraphBuilder 程序化构建）：

```
Start (Repeat)
 └─ Conditional Branch [IsThreatened]
     ├─ True  → Flee
     └─ False → Conditional Branch [IsHungry]
                ├─ True  → SeekFood
                └─ False → Conditional Branch [IsFatigued]
                           ├─ True  → SeekHome
                           └─ False → Conditional Branch [IsBored]
                                      ├─ True  → SeekFun
                                      └─ False → Wander
```

优先级从高到低：**威胁 > 饥饿 > 疲劳 > 无聊 > 漫游**。

---

### 3.7 UI 层

#### `Hud.cs` — 状态栏 + 行为图例

| 方法 | 作用 |
|---|---|
| `Update()` | 每 0.5s 算 FPS；每帧算瞬时 FPS push 给 FpsGraph；统计市民数/受威胁数/BT ticks；拼状态栏文本 `FPS | Citizens | Threatened | Scale | BT ticks/frame` |
| `CreateLegend()` | 程序化生成左下角半透明图例面板（5 行：色块 + 标签，颜色取 `ColoringSystem.ColorFor`，保证与市民着色一致） |

#### `ScaleDial.cs` — 刻度盘 + 威胁热键

| 方法 | 作用 |
|---|---|
| `Update()` | 热键：`1/2/3/4` 切 100/500/2000/5000（改 `bootstrap.count` 后 `Spawn()` 重生）；`T` 开关威胁；`WASD` 移动威胁区（M4 单区域）；`E` 在鼠标处生成临时恐惧区 |
| `SpawnTempZoneAtMouse()` | 鼠标位置射线投射到 y=0 地面，`SpawnTempZone(15m, 3s)` |

> 用新 Input System（`Keyboard.current`），因为项目 Player Settings 已切 Input System package，旧 `UnityEngine.Input` 会抛异常。

#### `FpsGraph.cs` — FPS 滚动柱状图

继承 `Graphic`，最近 120 帧画竖条。颜色分档：≥55 绿、30–55 黄、<30 红（5000 天花板视觉标红）。

| 方法 | 作用 |
|---|---|
| `Push(fps)` | 环形缓冲写入 + 标记重绘 |
| `ColorFor(fps)` | 帧率分档颜色 |
| `OnPopulateMesh(vh)` | 把 samples 画成竖条（每帧 4 顶点 2 三角形） |

#### `FlowFieldDebugVisualizer.cs` — 流场调试可视化

Game 视图（开 Gizmos）显示：网格线（绿）、障碍物占用格子（红块）、流场方向（黄箭头，1600 个较密默认关）。

| 方法 | 作用 |
|---|---|
| `OnDrawGizmos()` | 按开关画 3 层 |
| `GetField()` | 按 `fieldGoal` 选 3 张流场之一（3 张配置相同，blocked 相同） |
| `DrawGrid/DrawBlocked/DrawDirections()` | 各层绘制 |

---

### 3.8 Editor 工具 — `BtGraphBuilder.cs`

程序化创建标准 `BehaviorAuthoringGraph`（源图）资产，可在 Behavior 编辑器双击打开、可视化编辑。保存时 GraphAssetProcessor 自动烘焙运行时子资产（Bootstrap 加载的那个）。

| 方法 | 作用 |
|---|---|
| `Build()`（菜单 Tools/CitizenSim/Build） | 非破坏式：资产已存在则跳过（保护手动编辑） |
| `ForceRebuild()`（菜单 Force Rebuild） | 删了重建，弹确认框 |
| `BuildInternal()` | 核心：创建 BehaviorAuthoringGraph → 取 Start 设 Repeat → 用 internal API 程序化连好 5 分支树（Threat>Hungry>Fatigued>Bored>Wander）→ `ValidateAsset()` + `BuildRuntimeGraph(true)` 烘焙运行时子资产 → 存盘 |
| `CreateBranch(...)` | 建一个带条件的 Conditional Branch 节点 |
| `CreateAction(...)` | 在指定端口建 Action 节点 |
| `EnsureFolder(path)` | 递归建文件夹 |

> 依赖 `Assembly-CSharp-Editor` 的 IVT（InternalsVisibleTo）访问 Unity.Behavior internal API。

---

### 3.9 Tests 层 — `CitizenSim.Tests`

NUnit **EditMode** 测试，分两类：

**纯函数测试**（不建 World，直接调 Math/GoalDecision 静态方法）：
| 文件 | 测什么 |
|---|---|
| `PoiMathTests.cs` | NearestIndex / WithinRadius |
| `FlowFieldMathTests.cs` | BFS 单目标/多源/局部重建 |
| `SteeringMathTests.cs` | Arrive/Evade/Repulsion/FlowFieldArrive |
| `BtSchedulerTests.cs` | ComputePerFrame / ShouldPreempt / round-robin 覆盖 |
| `BtDecisionTests.cs` | GoalDecision 滞回判定与选点 |

**系统测试**（手建 World + EntityManager，手动把系统挂进 SimulationSystemGroup update list）：
| 文件 | 测什么 |
|---|---|
| `NeedsDecayTests.cs` | NeedsDecayJob 增减逻辑 |
| `SpatialGridTests.cs` | 网格构建 |
| `ThreatDetectionTests.cs` | 威胁检测 |
| `SteeringSystemTests.cs` | 转向朝目标/到达归零/邻居排斥 |
| `SyncLoopTests.cs` | **完整同步循环**：Snapshot→Grid→Steering→Resolve，市民朝目标移动 |
| `FlowFieldBuildTests.cs` | 流场构建系统 |
| `NeedsRoundTripTests.cs` | needs GO→ECS→GO 往返 |

> 测试手动分配 1×1 空流场（全 Inf）和 ObstacleRegistry 静态数组，因为 SteeringJob 字段的 NativeArray 在 schedule 时必须 IsCreated。

---

## 4. 一帧的数据流（黄金路径）

以"市民饿了去吃饭"为例：

```
1. [GO] BT tick：IsHungry 为真 → SeekFoodAction → GoalDecision.SetGoal
    写 ca.currentGoalType = SeekFood, currentGoalTarget = 最近食物点
        │
2. [SnapshotSystem] ca → entity：SimGoal / SimPosition / SimNeeds / SimExit
        │
3. [SpatialGridSystem] 全体市民装入格子哈希 + positions 缓存
        │
4. [NeedsDecaySystem] 在食物点半径内 → hunger 下降（否则上升）
        │
5. [ThreatDetectionSystem] 在威胁区 → Threatened bit 置位（enableable，零碎片）
        │
6. [FlowFieldBuildSystem] 需要时重算 3 张流场（含障碍物 blocked）
        │
7. [SteeringSystem] Burst job：查流场方向 + 9 邻域避障 + 障碍物排斥
    写 SimVelocity
        │
8. [ResolveSystem] 读 velocity 移动 GO（带流场硬约束滑动），
    回写 needs / threatened / exitTimer
        │
9. [ColoringSystem] 按 SimGoal 给 GO 换色（红=觅食）
```

**同步点只有 2 处**：Snapshot（GO→ECS）和 Resolve（ECS→GO）。中间全部在 ECS 里以 Burst 并行算，这就是"GO 作脊柱、ECS 作优化层"的边界。

---

## 5. 项目其他脚本（非 CitizenSim 核心）

`Assets/Scripts/CitizenSim` 是**这个项目的核心**（当前分支工作全部集中于此）。其余脚本属于 demo 的场景展示，不属于模拟架构，分类如下：

- `Assets/Scenes/Cockpit/Scripts/` — 座舱过场：Boids（群体飞行）、CapitalShipCannon、SplineFollower（样条跟随）、WormLazerBeam、MeteorPiece、TimelineLooper 等特效/动画脚本
- `Assets/Scenes/Garden/` `Oasis/` `Terminal/` — 各场景的灯光/雾效脚本
- `Assets/SharedAssets/` — 跨场景通用：FirstPersonController（FPS 控制器）、SceneLoader/SceneTransitionManager（场景管理）、FPSCounter、QualityInitialization、LoadingBar 等
- `Assets/Samples/Behavior/1.0.16/` — Unity Behavior 官方样例

---

## 6. 关键设计决策速查

| 决策 | 原因 |
|---|---|
| GO 是 source of truth，ECS 只做优化层 | demo 卖点是"GO/ECS 干净边界 + 性能证据"，不是纯 DOTS |
| 市民 GO 数组 + entity 数组按 `CitizenIndex` 对齐 | Snapshot/Resolve 靠 index 同步，免查找 |
| `Threatened` 用 enableable 组件 | 翻转 chunk bit，零 archetype 变更（5000 规模防碎片化） |
| Registry 缓存 Authoring/Renderers | 消掉每帧 GetComponent 的 GC |
| Steering/Threat/Grid/Needs 全部 Burst IJobEntity | 5000 规模只有 Burst 并行才扛得住 |
| 数学逻辑全部抽成静态纯函数 | 脱离 ECS 可单测（测试策略核心） |
| BT 每 0.5s 决策一次 + 受威胁插队 | 500 棵树全 tick 太重；威胁要当帧反应 |
| 3 张多源流场做寻路 | "找最近 POI"天然由多源 BFS 解决，一次构建全体复用 |
| 滞回（hunger/fatigue/fun/threat 都有） | 防阈值边界抖动导致反复横跳 |
| 拥堵流场 + 离开推力（M5） | 绕开高密度区域，并防止市民吃完堵在原地 |
| Resolve 的流场硬约束 | 市民永不进障碍格子/不出网格（先硬约束再滑行） |

---

## 7. 已知天花板（按规格 §9.4）

5000 规模下 **GO Transform 每帧回写**是已知性能天花板（Snapshot + Resolve 是主线程 O(N)）。当前实现已验证到 5000 可跑，但若继续扩规模，瓶颈在 GO 层而非 ECS 层。详见 `docs/perf/m4-profiling.md`。
