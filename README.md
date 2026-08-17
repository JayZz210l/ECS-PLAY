# ECS + 行为树 市民行为模拟 Demo

一个面向简历的技术 demo：数百到数千个胶囊市民在网格世界上按需求（饥饿/疲劳/娱乐）生活，遭遇威胁时恐慌逃散。核心卖点不是画面，而是**规模与性能**——在 GameObject 主体架构与 DOTS 优化层之间给出清晰的同步边界与 profiling 证据。

- Unity 6000.5.3f1 · URP 17.5 · Entities 1.0（ISystem / IJobEntity / Burst）· Unity Behavior 1.0.16
- 第一次看代码：[`docs/START_HERE.md`](docs/START_HERE.md)
- 设计规格：[`docs/superpowers/specs/2026-07-15-ecs-citizen-sim-design.md`](docs/superpowers/specs/2026-07-15-ecs-citizen-sim-design.md)

---

## 架构总览

**硬约束：GameObject 为脊柱，ECS 为性能优化层。** 不做纯 ECS。

市民是 GameObject（身份、BT、配置、Transform、渲染的权威）；每个 GO 挂一个 ECS 镜像 Entity，只承载每帧高频模拟计算。GO 是 source of truth，ECS 持有每帧快照 + 计算中间量。

| 数据 | 权威 | 备注 |
|---|---|---|
| 身份/配置/阈值/速度 | GO (MonoBehaviour) | Inspector 可改 |
| Needs（饥饿/疲劳/娱乐） | GO 存储 / ECS Job 算衰减 | 每帧快照进 ECS，Job 衰减后回写 GO |
| 当前目标（类型 + 目标点） | GO（BT 产出） | BT 写 GO，同步进 ECS |
| Position | GO Transform | Job 算速度，主线程 apply 到 Transform |
| Velocity / 邻居 / 空间格 / Threatened bit | ECS（纯中间量/enableable） | 不回写 GO（Threatened bit 镜像回 GO 供 BT 读） |

**BT 独占决策**（"当前去哪"），ECS 执行移动与需求满足。BT 不碰每帧移动。5000 个 agent 共用同一份 BehaviorGraph 资产，各自独立 blackboard。

---

## GO ↔ DOTS 同步边界（工程核心）

`CitizenRegistry`（MonoBehaviour 单例）持有 `GameObject[]` + `Entity[]` + `Authoring[]` + `Renderer[]`，按下标一一对应。跨 GO/ECS 边界的同步点只有 Snapshot 和 Resolve；中间的高频模拟由 ECS 系统协作完成：

```
   GO (source of truth)                          ECS (optimization layer)
┌─────────────────────┐                  ┌──────────────────────────────────┐
│  CitizenAuthoring   │   ① Snapshot     │  SimPosition / SimNeeds / SimGoal │
│  transform.position │ ───────────────► │  (镜像实体,主线程写)              │
│  needs / goal       │                  │                                  │
└─────────────────────┘                  │  ② NeedsDecay  (IJobEntity·Burst)│
                                         │  ③ SpatialGrid  (Job·Burst)      │
                                         │  ④ ThreatDetect (IJobEntity·Burst)│
                                         │  ⑤ FlowField / congestion       │
                                         │  ⑥ Steering     (IJobEntity·Burst)│
                                         │      └─► SimVelocity              │
                                         └──────────────┬───────────────────┘
   GO (apply 结果)                                      │ ⑦ Resolve (主线程)
┌─────────────────────┐  ◄─────────────────────────────┘
│  transform.position │   读 SimVelocity/SimNeeds -> apply 到 GO Transform、回写 needs
│  needs += decay     │   镜像 Threatened bit -> ca.threatened 供 BT 读
└─────────────────────┘
```

| # | System | 线程 | 职责 |
|---|---|---|---|
| 1 | SnapshotSystem | 主线程 | 遍历 GO：读 Transform/Needs/goal → 写镜像实体 |
| 2 | NeedsDecaySystem | IJobEntity·Burst | 衰减 needs；在 POI 到达时反向衰减（吃/睡/玩） |
| 3 | SpatialGridSystem | Job·Burst | 按 SimPosition 建空间哈希网格（9 邻域避障） |
| 4 | ThreatDetectionSystem | IJobEntity·Burst | 每帧查威胁半径，翻转 `Threatened` enableable bit（零 archetype 变更） |
| 5 | FlowFieldBuildSystem | ECS + 主线程汇总 | 多源流场与动态拥堵 |
| 6 | SteeringSystem | IJobEntity·Burst | 流场/arrive/evade + 网格邻居与障碍避让 → SimVelocity |
| 7 | ResolveSystem | 主线程 | 读 SimVelocity/SimNeeds → apply 到 GO Transform、回写 needs + threatened |
| 8 | ColoringSystem | 主线程 | 按目标状态更新市民颜色 |

Burst Job 只碰非托管数据。GO↔ECS 的正式状态同步仍只发生在 ① Snapshot 和 ⑦ Resolve；流场系统读取 Registry 是混合架构扩展层的主线程准备工作。

### 威胁的反应性设计

BT 是低频分片 tick 的，但 Flee 要立刻反应。再切一刀：
- **ECS 每帧**做威胁检测（空间查询），写 `Threatened` enableable bit。
- **BtScheduler** 给被标记的市民**插队**（当帧 tick，不受分片节流），其余照常 round-robin 分片（每帧 `N/30` 个）。
- BT 的 `IsThreatened → SetGoal(Flee)` 仍负责**下决策**，ECS 只喂条件 + 插队。

反应延迟 1–2 帧（肉眼不可见），被威胁人数通常很少，插队开销有界。`Threatened` 是 `IEnableableComponent`——toggle 只翻转 chunk 内 enabled 位，**零 archetype 变更**（无 chunk migration），是 demo 的简历 talking point。

---

## 运行步骤

1. 打开 `Assets/Scenes/CitizenSim/CitizenSimScene.unity`。
2. 按 **Play**。默认 500 市民生成。
3. 热键（运行时，`ScaleDial` 组件）：
   - `1` / `2` / `3` / `4` —— 切刻度到 **100 / 500 / 2000 / 5000**（清场重生平滑）。
   - `T` —— 开关威胁区。
   - `WASD` —— 移动威胁区（默认在原点，半径 5m）。
4. HUD（左上）：`FPS | Citizens | Threatened | Scale | BT ticks/frame`，下方是 120 帧 FPS 滚动柱状图（≥55 绿 / 30–55 黄 / <30 红）。
5. 状态着色：红=饥饿/前往食物 · 蓝=疲劳/回家 · 黄=娱乐 · 绿=漫游 · 白=威胁逃散。

> 拖动威胁区移入人群 → 市民变白四散（evade）→ 移出 → 恢复日常色。反应延迟 ≤2 帧。

---

## 性能数据

Editor Play mode，Burst ON，threat OFF（隔离日常闭环天花板）。详见 [`docs/perf/m4-profiling.md`](docs/perf/m4-profiling.md)。

| 市民数 | 整帧 (ms) | ~FPS | 主线程 (ms) | GPU (ms) |
|--------|----------|------|------------|----------|
| 100    | 6.0      | 167  | 2.3        | 0.37     |
| 500    | 9.0      | 111  | 4.0        | 0.67     |
| 2000   | 19.4     | 52   | 11.7       | 1.25     |
| 5000   | 34.0     | 29   | 23.0       | 1.70     |

- **spawn 一次性成本**：5000 ≈ 1006ms · 2000 ≈ 110ms · 500 ≈ 12ms · 100 ≈ 3ms。
- **5000 threat ON**（174/5000 逃散）：~38ms 稳态 + ~80ms 周期尖峰（BT 批次帧），335 BT ticks/frame（174 插队 + ~161 round-robin）。

### 天花板诚实分析

**5000 ≈ 29 FPS**，略低于 30 FPS 线，**未做主动缓解**——这是 GO-centric 架构的诚实天花板，也是 demo 的 talking point。

- 天花板是主线程 GO Transform 回写（`SnapshotSystem` + `ResolveSystem`，各 O(N) 主线程），**不是** Burst job 成本——四个 `[BurstCompile]` job（NeedsDecay/SpatialGrid/Threat/Steering）跑在 worker 线程，不在主线程关键路径上，5000 时只占帧时间的一小部分。
- **2000 ≈ 52 FPS** 是实用的"看着舒服"演示刻度。
- 纯 ECS 构建（无 GO 回写）会移除这个天花板，ECS 层可继续线性扩展——这正是 GO-as-spine / ECS-as-optimization-layer 设计的显式取舍。

### Burst ON / OFF

Burst ON 是上述所有数字的基线。Burst OFF 未在本会话内干净测得：此 Unity 6000.5.3f1 编辑器未注册 `Jobs > Use Burst Jobs` 菜单项，而运行时反射改 `BurstCompilerOptions.EnableBurstCompilation`（仅控 `FunctionPointer`，不控 `IJobEntity`）会破坏在线 job 管线。架构上，关掉 Burst 不会移动 5000 天花板（主线程回写从不走 Burst）；只会在 100–2000（job 时间占比更大）有温和回退。干净对比需编辑器菜单或带 `EnableBurstCompilation=false` 的 player build——列为后续。详见 [`docs/perf/m4-profiling.md`](docs/perf/m4-profiling.md)。

---

## 测试

EditMode 测试覆盖数学纯函数与 BT 决策逻辑（M1–M4 全绿）：
- `SteeringMathTests`（Seek/Arrive/Evade）
- `BtDecisionTests`（IsHungry/IsThreatened、SetGoal 各类型、最近 POI 选取）
- `BtSchedulerTests`（`ShouldPreempt` 纯函数、`ComputePerFrame`）
- `ThreatDetectionTests`（`ThreatMath.IsThreatened` 检测数学）
- `SyncLoopTests`（archetype 含 `Threatened`、GO↔ECS 往返）

---

## 里程碑历程

- **M1 — ECS 同步脊柱**：`SnapshotSystem`/`ResolveSystem` GO↔ECS 镜像，Burst `SteeringSystem`，500 胶囊 seek 目标，场景 + HUD v1。
- **M2 — 行为树集成**：Unity Behavior 1.0.16 手动 tick spike，自定义 `IsHungry`/`SetGoal` 节点，`BtScheduler` round-robin 分片，`ColoringSystem` 状态着色，BehaviorGraph 资产可开可编。
- **M3 — 完整日常闭环**：fatigue/fun 需求 + home/fun POI，Fatigued/Bored/Wander BT 分支，空间哈希网格 9 邻域避障，POI 可视标记（红/蓝/黄）+ gizmo，hunger 率/POI 半径调优。
- **M4 — 威胁 + 刻度 + 交付**：威胁区 + `ThreatDetectionSystem`（enableable `Threatened`，零 archetype 变更）+ BT Flee 分支 + 插队，evade 转向 + 白色，刻度盘 100/500/2000/5000，HUD v2（FPS 曲线 + 受威胁数 + ticks/帧），profiling 数据 + 5000 诚实天花板，README。
- **M5 - 多源流场寻路 + 动态障碍物**：`FlowField`数据结构 + `FlowFieldMath`（多源 BFS，4 邻域，障碍格子不可通过） + `FlowFieldBuildSystem`（3 张流场：食物/家/娱乐，40×40 格 × 2m） + `SteeringSystem`接入（SeekFood/Home/Fun 查流场方向，Wander/Flee 保留原逻辑） + `ObstacleRegistry`状态机（移动中 Steering 排斥力 + 0.5s 局部重算，静止 1s 后全量重算，之后停算） + `FlowFieldMath.RebuildRegion`（局部增量重算，从区域边界源 BFS 扩散）。障碍物四层标识（GO/流场 blocked/Registry/Steering movingObstacles）。

---

## 录像

一段 1–2 min 演示视频待补（人群流动 → 需求色切换 → 威胁恐慌 → 刻度 500→5000 + HUD 响应）。手动捕获步骤：Play → `2` 起步 → `T` + `WASD` 移威胁区入群 → `4` 拉 5000 → 观察 FPS 曲线变红。Editor Play mode 下 game view 不便脚本截图，建议用 Unity Recorder 或外部录屏。
