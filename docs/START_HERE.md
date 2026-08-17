# CitizenSim：从这里开始

这份文档只回答两个问题：程序每帧做什么，以及第一次应该按什么顺序读代码。

## 先记住一句话

**GameObject 决定“这个市民是谁、想去哪里”，ECS 批量计算“这一帧应该怎么移动”。**

每个市民同时有两份表现形式：

- GameObject：Transform、行为树、需求阈值、渲染和动画，是 source of truth。
- ECS 镜像 Entity：位置快照、需求、目标、速度、网格和威胁标记，只服务高频模拟。

这不是重复设计。GO ↔ ECS 的同步边界本身就是这个作品要展示的工程能力。

## 每帧主线

```text
BT 低频决策：写 CitizenAuthoring.currentGoal*
                         │
                         ▼
SnapshotSystem：GO ──────────────► ECS
                         │
                         ▼
Needs / Grid / Threat / FlowField / Steering
                         │
                         ▼
ResolveSystem： ECS ─────────────► GO
                         │
                         ▼
Coloring / Animator / HUD
```

必须保留的同步脊柱只有两端：

1. `SnapshotSystem` 把 GO 的位置、目标和需求写进镜像 Entity。
2. `ResolveSystem` 读取 ECS 算出的速度与状态，移动 GO，并回写行为树需要的数据。

中间增加或修改模拟功能时，不要绕过这两个同步点。

## 第一次只读这 6 个文件

| 顺序 | 文件 | 阅读时只回答一个问题 |
|---|---|---|
| 1 | `Components/SimComponents.cs` | ECS 镜像里到底存了什么？ |
| 2 | `Registry/CitizenAuthoring.cs` | GO 侧保存了什么？ |
| 3 | `Bootstrap/CitizenBootstrap.cs` | GO 和 Entity 如何一一创建并登记？ |
| 4 | `Systems/SnapshotSystem.cs` | 哪些数据从 GO 进入 ECS？ |
| 5 | `Systems/SteeringSystem.cs` | ECS 如何得到速度？ |
| 6 | `Systems/ResolveSystem.cs` | 速度和状态如何回到 GO？ |

读完后，用一句话复述数据流：

> BT 写 GO 目标 → Snapshot 复制进 ECS → Steering 算速度 → Resolve 移动 GO。

能说清这句话，就已经理解项目主干。Needs、威胁、流场都是插在主干中间的功能模块。

## 第二轮再按功能阅读

### 日常行为

- `Behavior/GoalDecision.cs`：饥饿、疲劳、娱乐和 Wander 的决策规则。
- `Behavior/Is*Condition.cs`、`*Action.cs`：Unity Behavior 的薄适配节点。
- `Behavior/BtScheduler.cs`：分片 tick，以及受威胁市民插队。
- `Systems/NeedsDecaySystem.cs`：需求随时间变化，到 POI 后恢复。

### 人群与威胁

- `Systems/SpatialGridSystem.cs`：建立邻居查询网格。
- `Math/SteeringMath.cs`：Seek、Arrive、Evade 和排斥纯函数。
- `Systems/ThreatDetectionSystem.cs`：检测威胁并翻转 enableable bit。
- `Registry/ThreatZoneRegistry.cs`：常驻与临时威胁区。

### 寻路与障碍物（扩展层）

- `Math/FlowFieldMath.cs`：流场数据和 BFS 算法。
- `Systems/FlowFieldBuildSystem.cs`：构建食物、家、娱乐三张流场。
- `Registry/ObstacleRegistry.cs`：障碍物状态和流场重建。
- `Registry/FlowFieldConfig.cs`：网格范围与格子尺寸。

如果当前目标只是理解 GO/ECS 混合架构，可以暂时跳过整个扩展层。

### 展示与调试（不属于模拟核心）

- `Systems/ColoringSystem.cs`：状态颜色。
- `UI/Hud.cs`、`UI/FpsGraph.cs`：运行数据展示。
- `UI/FlowFieldDebugVisualizer.cs`：调试网格和方向可视化。
- `UI/CitizenBounce.cs`、`UI/PoiLabel.cs`：纯展示效果。

## 常见名词

| 名词 | 在本项目里的意思 |
|---|---|
| Authoring | 挂在市民 GO 上的配置和行为状态，不是 ECS Baker |
| Registry | 用相同下标关联 `GameObject[]` 与 `Entity[]` 的运行时目录 |
| Mirror Entity | 某个 GO 对应的 ECS 高频计算副本 |
| IJobEntity | 批量遍历匹配组件的 Burst Job |
| Source of truth | 冲突时以 GO 数据为准；ECS 是快照和计算层 |
| Enableable | 不移动 archetype，只翻转组件是否启用的 bit |

## 想改功能时去哪里

| 想修改 | 首选位置 |
|---|---|
| 饥饿/疲劳/娱乐阈值 | `CitizenAuthoring`、`GoalDecision` |
| 需求增长和恢复速度 | `NeedsDecaySystem` |
| 移动、避让、逃跑力度 | `SteeringSystem`、`SteeringMath` |
| 威胁范围和滞回 | `ThreatZoneRegistry`、`ThreatMath` |
| GO ↔ ECS 同步字段 | `SnapshotSystem`、`ResolveSystem` |
| 绕障碍寻路 | `FlowFieldMath`、`FlowFieldBuildSystem` |
| 画面、颜色和 HUD | `ColoringSystem`、`UI/` |

改 Math 或决策纯函数后先跑 EditMode 测试。改同步逻辑时额外检查 `SyncLoopTests` 和 `NeedsRoundTripTests`。

