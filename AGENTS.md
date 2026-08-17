# AGENTS.md — 给 AI 的项目导览

本文档帮助 AI 快速理解本项目架构、约束与踩坑点。读完即可定位代码、避免重蹈已解决的坑。

## 这是什么

一个 **简历/作品集 tech demo**：500 个公民的行为模拟（可扩到 2000/5000）。核心卖点是 **GameObject-as-spine + ECS-as-optimization-layer** 的清晰边界 + 性能实证，**不是**纯 DOTS showcase。GO↔ECS 的 round-trip 本身就是被演示的东西。

## 硬约束（不可违反）

- **GO 是 source of truth**，ECS mirror 实体只载高频 sim（steering/needs/grid/threat）；流场是 ECS 侧共享导航数据。不要提议纯 ECS / 纯 DOTS 架构。
- **5000 GO-Transform writeback 是已知天花板**（主线程 ~23ms 花在 Snapshot+Resolve 的 GO 循环）。这是架构固有的 talking point，**不要试图修掉**，诚实呈现。
- 改 sim 逻辑要保留 `Snapshot -> ... -> Resolve` 同步脊柱，不要绕过。

## 技术栈

Unity 6000.5.3f1 · URP 17.5 · Entities 1.0（ISystem / IJobEntity / Burst）· Unity Behavior 1.0.16 · Input System 1.19。

## 同步脊柱（2 个边界同步点 + ECS 中间层）

```
Snapshot(GO→ECS)
  → NeedsDecay / SpatialGrid / ThreatDetection / FlowFieldBuild
  → Steering(Burst) → Resolve(ECS→GO) → Coloring
```

| 系统 | 职责 | 关键点 |
|---|---|---|
| SnapshotSystem | GO Transform/Goal/Needs → ECS 镜像实体 | 写入侧 GO 读 |
| NeedsDecaySystem | hunger/fatigue/fun 衰减 + 到点消耗 | 到 POI 才消耗 |
| SpatialGridSystem | 空间哈希网格 | 邻居查询 |
| ThreatDetectionSystem | 每市民 threatened bit | enableable，空间滞回 1.3× |
| FlowFieldBuildSystem | 食物/家/娱乐三张多源流场 | 障碍/拥堵变化时重建 |
| SteeringSystem | FlowField/Arrive/Evade + 邻居/障碍避让 | Burst IJobEntity |
| ResolveSystem | ECS 速度/goal → GO Transform + ca 字段 | GO 写回（天花板在此） |
| ColoringSystem | 按 goal.type 设 capsule 颜色 | MaterialPropertyBlock |

## 代码地图

`Assets/Scripts/CitizenSim/`

- `Bootstrap/` — `CitizenBootstrap`：GO + mirror entity 生命周期与运行时装配
- `Registry/` — GO 侧 source of truth 与运行时目录；还包括 POI、威胁区、障碍物、流场配置
- `Components/` — `SimComponents`：`SimPosition`/`Velocity`/`Goal`/`Needs`/`Threatened`(**enableable**)/`CitizenIndex`/`GridCell`
- `Systems/` — 同步、需求、网格、威胁、流场、转向、写回、着色
- `Behavior/` — Unity Behavior 自定义节点 + `BtScheduler`（分片调度）+ `GoalDecision`（纯函数，可单测）
- `Math/` — `SteeringMath`/`ThreatMath`/`PoiMath`/`FlowFieldMath`（纯函数/算法，单测覆盖）
- `UI/` — HUD、输入刻度与纯展示/调试组件；不属于模拟核心

## 关键设计决策

- **enableable `Threatened`**：bit 翻转零 archetype 变更，主线程 `SetComponentEnabled`。查询按 `SimPosition+CitizenIndex`（全体），不能按 `Threatened` bit（disabled 的会从查询消失）。
- **BT 分片调度**：`BtScheduler` round-robin 每帧 tick 一批 + 受威胁市民插队（`ShouldPreempt` 纯函数 + `lastTick[]` 防双 tick）。保证威胁当帧反应。
- **空间滞回**：威胁 enter 阈值 = radius，exit 阈值 = radius×1.3。逃出区仍保持 Flee 到 1.3× 半径外才解除，防 Flee/Seek 边界反复横跳（与 hunger/fatigue/fun 滞回同哲学）。
- **每区独立半径**：常驻区（`ThreatZoneRegistry.radius` 字段）+ 临时区（各自 radius，如 15m）共存。用 `GetActiveZones(pos[], rad[])` 平行数组。
- **Needs 滞回**：SeekFood 时吃到 `fullThreshold` 才停；否则 `hungerThreshold` 触发。fatigue/fun 同理。防阈值抖动。
- **多源流场**：SeekFood/Home/Fun 共享网格配置，各自一张到最近 POI 的流场；Wander/Flee 不走流场。
- **障碍物两层处理**：流场 blocked 管宏观绕路，Steering 排斥力管移动障碍与边缘避让；Resolve 保留最终硬约束。

## 踩坑（别重蹈）

1. **`BurstCompilerOptions.EnableBurstCompilation` 运行时改会 corrupt live job pipeline**，不可恢复（帧提交停摆），重启用也不行。`BurstCompiler.IsEnabled` 只读。**别碰运行时 Burst 开关**。Burst on/off 对比只能靠架构推理（disabling Burst 不动 5000 天花板，因为 GO writeback 从不跑在 Burst 下）。
2. **`GetZonePositions()` 只返常驻区**。临时区触发的 Flee 必须用 `GetActiveZonePositions()`（含临时区），否则 evade 目标错——市民会朝远离常驻区的固定方向跑，而非从临时区中心向外辐射。
3. **威胁检测原本无滞回** → 边界 Flee/Seek 反复横跳。已加 1.3× 空间滞回（`ThreatMath.IsThreatened(..., float factor)` + 双 flag 数组 `flagsEnter`/`flagsExit`，主线程按"上帧是否 threatened"选阈值）。
4. **Unity Behavior 1.0.16**：运行时必须 rebuild `Node.Parent` chain（`BtGraphBuilder` 非破坏式），否则 BT 不重评。详见 `docs/m2-behavior-debugging-log.md`。
5. **`FindObjectOfType<T>()`** 可用；`FindFirstObjectOfType` 在此 Unity 版本不存在。

## 运行 / 操作

- 场景：`Assets/Scenes/CitizenSim/CitizenSimScene.unity`
- 按键：`1/2/3/4` = 100/500/2000/5000 市民（实时重生）· `T` = 威胁区开关 · `E` = 鼠标位置生成临时恐惧区（15m, 3s）· `WASD` = 相机
- 市民颜色：红=SeekFood · 蓝=SeekHome · 黄=SeekFun · 绿=Wander · 白=Flee（HUD 左下角有 UGUI 图例）
- POI gizmo：food=红 · home=蓝 · fun=黄

## 测试

EditMode 当前 93 个（项目测试 92 + Unity 配置测试 1），项目测试在 `Assets/Scripts/CitizenSim.Tests` 的 13 个文件中，覆盖 M1–M5。改 Math/纯函数后跑 `run_tests` (EditMode)。跑前 `refresh_unity` compile。`GoalDecision`/`ThreatMath`/`SteeringMath`/`FlowFieldMath` 都是专为可测设计的逻辑层。

## 性能（实测）

| 规模 | 帧时间 | FPS |
|---|---|---|
| 100 | 6.0ms | 167 |
| 500 | 9.0ms | 111 |
| 2000 | 19.4ms | 52 |
| 5000 | 34ms | ~29 |

5000 的 ~29fps = GO writeback 天花板（主线程 23ms 在 Snapshot+Resolve GO 循环），ECS 层（Burst）非瓶颈。详见 `docs/perf/m4-profiling.md`。

## 文档地图

- `docs/superpowers/specs/2026-07-15-ecs-citizen-sim-design.md` — **权威 spec**（§4 BT 结构、§5 pipeline、§7 里程碑、§9 风险含 5000 天花板）
- `docs/START_HERE.md` — 新手阅读入口：先看哪些文件、哪些可以暂时跳过
- `docs/superpowers/plans/` — M1–M5 TDD plans + roadmap
- `docs/perf/m4-profiling.md` — 性能数据 + 5000 天花板分析 + Burst 诚实说明
- `docs/unity-behavior-usage-guide.md` — Unity Behavior API 用法
- `docs/m2-behavior-debugging-log.md` — BT 反射/spike 踩坑
- `README.md` — 给人看的简历材料（架构图 + 运行说明 + 性能表）

## 里程碑

M1（同步脊柱）→ M2（BT 接入）→ M3（完整需求 + 空间网格 + 避障）→ M4（威胁/Flee + 刻度盘 + HUD v2 + profiling）→ M5（多源流场 + 动态障碍物）。当前代码已到 M5。

## 若用 MCP for Unity 操作本项目

- `manage_camera` 可能被禁用；截图用 `ScreenCapture.CaptureScreenshot(path)` 写文件再读。
- `execute_code`（roslyn）某些命名空间（如 `CitizenSim.UI`）缺 assembly 引用会编译失败——用核心命名空间或避开 UI 层。
- play mode 下 MCP round-trip 间隔数秒；验证短时效效果（如 3s 临时恐惧区）时，临时区可能在两次调用间就过期——用更长 duration 替代或 spawn 后同步检查。
- 改脚本后 `refresh_unity` compile；play mode 下触发编译会退出 play。
- `BurstCompilerOptions` 见踩坑 #1，绝对不要运行时改。
